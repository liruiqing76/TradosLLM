using System;
using System.Threading;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationMemories
{
    /// <summary>
    /// 本地库索引定时刷新：每天在配置的时间点重建一次共享索引
    /// （<see cref="LocalTmIndex.Refresh"/>），让记忆库管理与收件箱查到的是最新数据。
    /// <para>
    /// 不依赖 Windows 计划任务：插件在 Studio 里实例化 Ribbon 组时调用 <see cref="Start"/>，
    /// 用一个后台线程按「今天/明天的目标时刻」休眠轮询。每次判断前都重读 config.json，
    /// 因此界面上改了开关/时间即时生效；同一自然日只跑一次（用 tmIndexLastRun 去重，跨重启仍然有效）。
    /// </para>
    /// </summary>
    public static class TmIndexScheduler
    {
        private static readonly object Gate = new object();
        private static Thread _thread;
        private static volatile bool _running;
        private static CancellationTokenSource _cts;

        /// <summary>上次刷新时刻的 ticks；用 Interlocked 读写，避免 UI 线程与调度线程之间的可见性问题。</summary>
        private static long _lastRunTicks = DateTime.MinValue.Ticks;

        /// <summary>本次进程内自动刷新完成后触发（在工作线程；订阅方需自行 marshal 回 UI）。</summary>
        public static event Action<string> Refreshed;

        /// <summary>上次自动刷新时间（进程内记录；无则读配置里的 tmIndexLastRun）。</summary>
        public static DateTime LastRun
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastRunTicks);
                return ticks == DateTime.MinValue.Ticks ? ReadLastRunFromConfig() : new DateTime(ticks);
            }
        }

        /// <summary>是否在运行调度线程。</summary>
        public static bool IsRunning => _running;

        /// <summary>启动调度线程（幂等）；Ribbon 组实例化时调用。</summary>
        public static void Start()
        {
            lock (Gate)
            {
                if (_running) return;
                _running = true;
                Interlocked.Exchange(ref _lastRunTicks, ReadLastRunFromConfig().Ticks);
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _thread = new Thread(() => Loop(ct)) { IsBackground = true, Name = "TradosToolkit-TmIndexScheduler" };
                _thread.Start();
                // 进程退出是插件唯一可靠的释放时机（Studio 插件无卸载钩子），在此停掉调度线程。
                AppDomain.CurrentDomain.ProcessExit += (s, e) => Stop();
                ToolkitLog.Info("库索引定时：调度线程已启动");
            }
        }

        /// <summary>停止调度线程（幂等）。置回 _running 后可再次 Start。</summary>
        public static void Stop()
        {
            Thread t;
            CancellationTokenSource cts;
            lock (Gate)
            {
                if (!_running) return;
                _running = false;
                t = _thread;
                _thread = null;
                cts = _cts;
                _cts = null;
            }
            try { cts?.Cancel(); } catch { /* 已释放则忽略 */ }
            // 取消后 Loop 会立刻从等待中醒来，Join 只是给个短暂的收敛窗口。
            if (t != null && t != Thread.CurrentThread)
            {
                try { t.Join(5000); }
                catch (Exception e) { ToolkitLog.Error("库索引定时：等待调度线程退出异常", e); }
            }
            try { cts?.Dispose(); } catch { }
            ToolkitLog.Info("库索引定时：调度线程已停止");
        }

        private static void Loop(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    var cfg = ToolkitConfig.Load();
                    var due = NextDue(cfg, DateTime.Now);
                    if (due != null)
                    {
                        var wait = due.Value - DateTime.Now;
                        if (wait > TimeSpan.Zero)
                        {
                            // 分片等待：用 WaitHandle 而非 Sleep，Stop() 取消后可立刻醒来退出
                            var step = TimeSpan.FromSeconds(30);
                            while (_running && !ct.IsCancellationRequested && DateTime.Now < due.Value)
                                if (ct.WaitHandle.WaitOne(wait < step ? wait : step)) break;
                            continue;
                        }
                        RunNow(cfg);
                    }
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("库索引定时：循环异常", e);
                }
                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30))) break;
            }
        }

        /// <summary>算出下一次该刷新索引的时刻；不需要刷新（未启用 / 目录缺失 / 今天已跑过）返回 null。</summary>
        internal static DateTime? NextDue(ToolkitConfig cfg, DateTime now)
        {
            if (cfg == null || !cfg.TmIndexAutoRefresh) return null;
            var dir = (cfg.TmScanDirectory ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(dir)) return null;
            if (!ToolkitConfig.IsValidTime(cfg.TmIndexRefreshTime)) return null;

            var tod = DateTime.TryParseExact(cfg.TmIndexRefreshTime.Trim(), new[] { "H:mm", "HH:mm" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var t)
                ? t.TimeOfDay : new TimeSpan(2, 0, 0);

            var target = now.Date.Add(tod);
            if (target <= now) target = target.AddDays(1);

            // 今天该时刻之后已经跑过 → 排到明天
            var last = ReadLastRunFromConfig();
            if (last.Date == now.Date && now.TimeOfDay >= tod) target = now.Date.AddDays(1).Add(tod);
            return target;
        }

        private static void RunNow(ToolkitConfig cfg)
        {
            var dir = (cfg.TmScanDirectory ?? string.Empty).Trim();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var list = LocalTmIndex.Refresh(dir, null, CancellationToken.None);
                var now = DateTime.Now;
                Interlocked.Exchange(ref _lastRunTicks, now.Ticks);
                ToolkitConfig.Save(tmIndexLastRun: now.ToString("o"));
                var msg = string.Format("库索引已自动重建：{0} 个记忆库，耗时 {1:0.0}s（目录 {2}）",
                    list.Count, watch.ElapsedMilliseconds / 1000.0, dir);
                ToolkitLog.Info("库索引定时：" + msg);
                Refreshed?.Invoke(msg);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("库索引定时：重建失败 " + dir, e);
                Refreshed?.Invoke("库索引自动重建失败：" + e.Message);
            }
        }

        private static DateTime ReadLastRunFromConfig()
        {
            try
            {
                var s = ToolkitConfig.Load().TmIndexLastRun;
                if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    return dt;
            }
            catch { /* 解析失败按未跑过 */ }
            return DateTime.MinValue;
        }
    }
}
