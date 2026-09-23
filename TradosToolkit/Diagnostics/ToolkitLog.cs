using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TradosToolkit.Diagnostics
{
    /// <summary>
    /// 全插件统一文件日志：%APPDATA%\TradosToolkit\logs\plugin.log。
    /// 按天或超 5MB 归档为 plugin-yyyyMMdd-HHmmss.log，只保留最近 14 份。
    /// 由 Ribbon 组构造时 Boot()，同时挂钩 AppDomain 未处理异常与未观察任务异常。
    /// 日志失败绝不影响插件本身。
    /// </summary>
    public static class ToolkitLog
    {
        private const long MaxFileBytes = 5 * 1024 * 1024;
        private const int KeepArchives = 14;

        private static readonly object Gate = new object();
        private static int _booted;

        private static readonly string LogFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TradosToolkit", "logs");

        public static string LogFile => Path.Combine(LogFolder, "plugin.log");

        public static string FolderPath => LogFolder;

        public static void Boot(string component)
        {
            if (Interlocked.Exchange(ref _booted, 1) != 0)
                return;

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Error("UNHANDLED [" + (e.IsTerminating ? "terminating" : "non-terminating") + "]", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
                Error("UNOBSERVED task", e.Exception);

            Info("boot from " + component + " | log=" + LogFile);
        }

        public static void Info(string message)
        {
            Write("INFO ", message, null);
        }

        public static void Debug(string message)
        {
            Write("DEBUG", message, null);
        }

        public static void Warn(string message, Exception e = null)
        {
            Write("WARN ", message, e);
        }

        public static void Error(string message, Exception e = null)
        {
            Write("ERROR", message, e);
        }

        /// <summary>在资源管理器中打开日志目录，供 UI"打开日志目录"入口调用。</summary>
        public static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + LogFolder + "\"")
                {
                    UseShellExecute = true
                });
                Info("打开日志目录: " + LogFolder);
            }
            catch (Exception e)
            {
                Error("打开日志目录失败", e);
            }
        }

        private static void Write(string level, string message, Exception e)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogFolder);
                    RollIfNeeded();
                    File.AppendAllText(LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] [t" +
                        Thread.CurrentThread.ManagedThreadId + "] " + message +
                        (e == null ? "" : Environment.NewLine + Format(e)) + Environment.NewLine);
                }
            }
            catch
            {
            }
        }

        // 调用方已持锁
        private static void RollIfNeeded()
        {
            var fi = new FileInfo(LogFile);
            if (!fi.Exists || (fi.LastWriteTime.Date == DateTime.Today && fi.Length <= MaxFileBytes))
                return;

            var stamp = fi.LastWriteTime.ToString("yyyyMMdd-HHmmss");
            var target = Path.Combine(LogFolder, "plugin-" + stamp + ".log");
            var n = 1;
            while (File.Exists(target))
                target = Path.Combine(LogFolder, "plugin-" + stamp + "-" + (++n) + ".log");
            File.Move(LogFile, target);
            PruneArchives();
        }

        private static void PruneArchives()
        {
            var archives = new List<FileInfo>();
            foreach (var path in Directory.GetFiles(LogFolder, "plugin-*.log"))
            {
                try
                {
                    archives.Add(new FileInfo(path));
                }
                catch
                {
                }
            }
            archives.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (var i = KeepArchives; i < archives.Count; i++)
            {
                try
                {
                    archives[i].Delete();
                }
                catch
                {
                }
            }
        }

        public static string Format(Exception e)
        {
            if (e == null)
                return string.Empty;
            var text = e.GetType().FullName + ": " + e.Message + Environment.NewLine + e.StackTrace;
            if (e.InnerException != null)
                text += Environment.NewLine + "--- inner ---" + Environment.NewLine + Format(e.InnerException);
            return text;
        }
    }
}
