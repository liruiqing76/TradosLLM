using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TradosToolkit.Diagnostics
{
    /// <summary>
    /// 全插件统一文件日志：%APPDATA%\TradosToolkit\logs\plugin.log。
    /// 由 Ribbon 组构造时 Boot()，同时挂钩 AppDomain 未处理异常与未观察任务异常。
    /// 日志失败绝不影响插件本身。
    /// </summary>
    public static class ToolkitLog
    {
        private static readonly object Gate = new object();
        private static int _booted;

        private static readonly string LogFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TradosToolkit", "logs");

        public static string LogFile => Path.Combine(LogFolder, "plugin.log");

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

        public static void Error(string message, Exception e = null)
        {
            Write("ERROR", message, e);
        }

        private static void Write(string level, string message, Exception e)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogFolder);
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
