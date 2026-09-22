using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Inbox
{
    /// <summary>
    /// 收件箱目录监视：FileSystemWatcher 捕获投放文件，去抖 + 等文件写完，
    /// 交给后台单线程队列逐个跑 InboxOrchestrator（Studio 项目自动化必须串行）。
    /// 单例，可供 Ribbon 组按配置自启（后台模式），界面只是附加的可操作外壳。
    /// </summary>
    public sealed class InboxWatcher
    {
        private static readonly InboxWatcher _instance = new InboxWatcher();
        public static InboxWatcher Instance => _instance;

        /// <summary>新建任务时触发（在工作线程；订阅方需自行 marshal 回 UI）。</summary>
        public event System.Action<InboxJob> JobCreated;

        private static readonly string[] BlockedExtensions =
            { ".tmp", ".temp", ".crdownload", ".part", ".partial", ".sdlppx", ".sdltm", ".sdltb", ".log" };

        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private readonly object _gate = new object();
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private FileSystemWatcher _watcher;
        private Thread _worker;
        private volatile bool _running;
        private volatile string _outputRoot = string.Empty;
        private ToolkitConfig _cfg;

        public bool IsRunning => _running;
        public string WatchingFolder { get; private set; } = string.Empty;

        /// <summary>Ribbon 组启动时调用：配置允许则自动开始监视。</summary>
        public void AutoStart()
        {
            try
            {
                var cfg = ToolkitConfig.Load();
                if (cfg.InboxAutoStart && !string.IsNullOrWhiteSpace(cfg.InboxWatchFolder))
                {
                    ToolkitLog.Info("收件箱：按配置自动开始监视 " + cfg.InboxWatchFolder);
                    Start(cfg);
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Error("收件箱：自动开始监视失败", e);
            }
        }

        public void Start(ToolkitConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            var folder = (cfg.InboxWatchFolder ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(folder))
                throw new InvalidOperationException("请先设置要监视的目录");
            if (!Directory.Exists(folder))
                throw new InvalidOperationException("监视目录不存在：" + folder);

            lock (_gate)
            {
                if (_running && string.Equals(folder, WatchingFolder, StringComparison.OrdinalIgnoreCase))
                    return;

                StopLocked();

                _cfg = cfg;
                _outputRoot = (cfg.InboxOutputFolder ?? string.Empty).Trim();
                WatchingFolder = folder;

                _watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Created += OnChanged;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += (s, e) => ToolkitLog.Error("收件箱：监视出错", e.GetException());
                _watcher.EnableRaisingEvents = true;

                _running = true;
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "TradosToolkit-Inbox" };
                _worker.Start();

                ToolkitLog.Info("收件箱：开始监视 " + folder);
            }
        }

        public void Stop()
        {
            lock (_gate) StopLocked();
        }

        /// <summary>
        /// 界面改了配置（源/目标语言、报告格式、产出目录等）时调用：运行中也立即生效，无需重启监视。
        /// 监视目录变更不在其列——那要重开 FileSystemWatcher，走 Stop/Start。
        /// </summary>
        public void RefreshConfig(ToolkitConfig cfg)
        {
            if (cfg == null) return;
            lock (_gate)
            {
                if (!_running) return;
                _cfg = cfg;
                _outputRoot = (cfg.InboxOutputFolder ?? string.Empty).Trim();
                ToolkitLog.Info("收件箱：运行中配置已刷新（语向 " + cfg.InboxSourceLang + "→" + cfg.InboxTargetLang +
                                "，报告格式 " + cfg.InboxReportFormat + "）");
            }
        }

        private void StopLocked()
        {
            if (_watcher != null)
            {
                try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
                _watcher = null;
            }
            _running = false;
            try { _worker?.Interrupt(); } catch { }
            _worker = null;
            WatchingFolder = string.Empty;
            while (_queue.TryDequeue(out _)) { }
            lock (_pending) _pending.Clear();
            ToolkitLog.Info("收件箱：已停止监视");
        }

        /// <summary>界面「立即处理」：把收件箱里已有的文件排队（不等待事件）。</summary>
        public int EnqueueExisting()
        {
            if (!_running || string.IsNullOrEmpty(WatchingFolder)) return 0;
            var n = 0;
            foreach (var path in Directory.GetFiles(WatchingFolder))
                if (Accept(path) && Enqueue(path)) n++;
            return n;
        }

        /// <summary>界面「处理这个文件」：直接投一个路径进队列。</summary>
        public bool Enqueue(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            lock (_pending)
            {
                if (!_pending.Add(path)) return false;
            }
            _queue.Enqueue(path);
            return true;
        }

        // ==================== 事件 → 队列 ====================

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (!_running) return;
            if (e.ChangeType != WatcherChangeTypes.Created && e.ChangeType != WatcherChangeTypes.Changed) return;
            Enqueue(e.FullPath);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (!_running) return;
            Enqueue(e.FullPath);
        }

        // ==================== 工作线程 ====================

        private void WorkerLoop()
        {
            while (_running)
            {
                if (!_queue.TryDequeue(out var path))
                {
                    try { Thread.Sleep(300); } catch (ThreadInterruptedException) { }
                    continue;
                }

                try
                {
                    if (!Accept(path)) { Done(path); continue; }
                    if (!WaitUntilReady(path, 60000)) { Done(path); continue; }

                    var job = new InboxJob(path);
                    JobCreated?.Invoke(job);
                    InboxOrchestrator.Run(job, _cfg ?? ToolkitConfig.Load());
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("收件箱：处理失败 " + path, e);
                }
                finally
                {
                    Done(path);
                }
            }
        }

        private void Done(string path)
        {
            lock (_pending) _pending.Remove(path);
        }

        private bool Accept(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

                var fi = new FileInfo(path);
                if ((fi.Attributes & FileAttributes.Hidden) != 0) return false;
                if ((fi.Attributes & FileAttributes.Directory) != 0) return false;

                var name = fi.Name;
                if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal))
                    return false;

                var ext = fi.Extension ?? string.Empty;
                foreach (var blocked in BlockedExtensions)
                    if (string.Equals(ext, blocked, StringComparison.OrdinalIgnoreCase)) return false;

                // 产物目录若被配进监视目录，避免自产自销
                if (!string.IsNullOrEmpty(_outputRoot))
                {
                    var abs = Path.GetFullPath(path);
                    var outAbs = Path.GetFullPath(_outputRoot)
                        .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (abs.StartsWith(outAbs, StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>等文件大小稳定且可独占打开（投放未写完时反复重试）。</summary>
        private static bool WaitUntilReady(string path, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            long lastLength = -1;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) return false;
                    if (fi.Length > 0 && fi.Length == lastLength)
                    {
                        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                        return true;
                    }
                    lastLength = fi.Length;
                }
                catch
                {
                    // 文件仍被写入占用，继续等
                }
                try { Thread.Sleep(400); } catch (ThreadInterruptedException) { }
            }
            return File.Exists(path);
        }
    }
}
