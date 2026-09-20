using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationMemories
{
    public enum LocalTmState { Ok, Protected, Error }

    /// <summary>一行本地记忆库记录，供工作台列表直接绑定。</summary>
    public class LocalTmInfo
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public string LanguagePair { get; set; }
        public string Units { get; set; }
        public string Size { get; set; }
        public DateTime Modified { get; set; }
        public LocalTmState State { get; set; }
        public string Detail { get; set; }

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case LocalTmState.Ok: return UiText.T("WB_Mem_St_Ok");
                    case LocalTmState.Protected: return UiText.T("WB_Mem_St_Protected");
                    default: return UiText.T("WB_Mem_St_Error");
                }
            }
        }
    }

    /// <summary>
    /// 本地记忆库扫描器：递归目录内 *.sdltm，用 FileBasedTranslationMemory（Studio 公共 API）
    /// 读取语言对与条目数。整体跑在后台线程，支持进度与取消；单文件失败降级为错误行，不中断整批。
    /// </summary>
    public static class LocalTmScanner
    {
        public static Task<List<LocalTmInfo>> ScanAsync(string root, IProgress<int> progress, CancellationToken ct)
            => Task.Run(() => Scan(root, progress, ct), CancellationToken.None);

        public static List<LocalTmInfo> Scan(string root, IProgress<int> progress, CancellationToken ct)
        {
            var result = new List<LocalTmInfo>();
            var files = Directory.GetFiles(root, "*.sdltm", SearchOption.AllDirectories);
            ToolkitLog.Info("LocalTmScanner: " + root + " 找到 " + files.Length + " 个 sdltm");
            for (int i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                result.Add(ReadOne(files[i]));
                progress?.Report(i + 1);
            }
            result.Sort((a, b) => b.Modified.CompareTo(a.Modified));
            return result;
        }

        private static LocalTmInfo ReadOne(string path)
        {
            var info = new LocalTmInfo
            {
                Name = Path.GetFileName(path),
                FilePath = path,
                LanguagePair = "-",
                Units = "-",
            };
            try
            {
                var fi = new FileInfo(path);
                info.Modified = fi.LastWriteTime;
                info.Size = HumanSize(fi.Length);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LocalTmScanner: 读文件信息失败 " + path, e);
            }

            FileBasedTranslationMemory tm = null;
            try
            {
                tm = new FileBasedTranslationMemory(path);
                info.LanguagePair = FormatPair(tm);
                if (tm.IsProtected)
                {
                    info.State = LocalTmState.Protected;
                    return info;
                }
                info.Units = tm.GetTranslationUnitCount().ToString("N0", CultureInfo.CurrentCulture);
                info.State = LocalTmState.Ok;
                return info;
            }
            catch (Exception e)
            {
                info.State = LocalTmState.Error;
                info.Detail = e.Message;
                ToolkitLog.Error("LocalTmScanner: 打开失败 " + path, e);
                return info;
            }
            finally
            {
                try { (tm as IDisposable)?.Dispose(); } catch { /* 只读打开，释放失败无影响 */ }
            }
        }

        private static string FormatPair(FileBasedTranslationMemory tm)
        {
            try
            {
                var dir = tm.LanguageDirection;
                if (dir == null || dir.SourceLanguage == null || dir.TargetLanguage == null) return "-";
                return dir.SourceLanguage.Name + " → " + dir.TargetLanguage.Name;
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LocalTmScanner: 读语言对失败", e);
                return "-";
            }
        }

        public static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            int u = 0;
            double v = bytes;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return (u == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + units[u];
        }
    }
}
