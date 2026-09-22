using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TradosToolkit.Common
{
    /// <summary>
    /// 文件处理工具箱：目录创建、容错读写、原子写（先写临时文件再替换，避免半截文件）、
    /// 可写性探测、唯一命名、体积人性化、路径包含判断等。
    /// <para>风格约定：读类方法失败返回安全默认值（不抛）；写类方法返回 bool 表示成败，不抛。</para>
    /// </summary>
    public static class FileKit
    {
        /// <summary>确保目录存在并返回该路径（失败返回原路径，交由后续操作报错）。</summary>
        public static string EnsureDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
            }
            return dir;
        }

        public static bool FileExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return File.Exists(path); }
            catch { return false; }
        }

        public static bool DirectoryExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return Directory.Exists(path); }
            catch { return false; }
        }

        /// <summary>读文本；不存在或读失败返回 null（用于"有就返回、没有就算了"）。</summary>
        public static string ReadAllTextOrNull(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读文本；失败返回 fallback（默认空串）。</summary>
        public static string ReadAllText(string path, string fallback = "")
        {
            var text = ReadAllTextOrNull(path);
            return text ?? fallback;
        }

        /// <summary>
        /// 原子写文本：先写同目录临时文件再替换目标，避免进程中断留下半截文件。
        /// 返回是否成功。
        /// </summary>
        public static bool WriteAllTextAtomic(string path, string text, Encoding encoding = null)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(text ?? string.Empty);
            return WriteAllBytesAtomic(path, bytes);
        }

        /// <summary>原子写字节：先写同目录临时文件再替换目标。返回是否成功。</summary>
        public static bool WriteAllBytesAtomic(string path, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path) || bytes == null) return false;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) EnsureDirectory(dir);

            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path))
                    File.Replace(tmp, path, null); // 目标存在：原子替换
                else
                    File.Move(tmp, path);
                return true;
            }
            catch
            {
                SafeDelete(tmp);
                return false;
            }
        }

        /// <summary>
        /// 探测文件是否可写（如 .sdltm 能否被预翻译写入）。
        /// 文件存在则尝试以读写方式打开；不存在则探测所在目录能否建临时文件。
        /// </summary>
        public static bool IsWritable(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (File.Exists(path))
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        return true;
                }

                var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                var probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    fs.WriteByte(0);
                SafeDelete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>若 path 已存在，返回带 (1)(2)… 后缀的唯一路径；否则原样返回。</summary>
        public static string UniquePath(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var i = 1; i < 10000; i++)
            {
                var candidate = Path.Combine(dir, string.Format("{0} ({1}){2}", name, i, ext));
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ext);
        }

        /// <summary>字节数人性化：1023 B / 1.5 KB / 2.0 MB / 3.1 GB。</summary>
        public static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var u = 0;
            double v = bytes;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return (u == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + units[u];
        }

        /// <summary>
        /// 文件名安全化：把非法字符换成下划线，去掉首尾空白与结尾的点；
        /// 结果为空则用 fallback。仅处理文件名本身，不含路径。
        /// </summary>
        public static string SanitizeFileName(string name, string fallback = "file")
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                var bad = false;
                foreach (var ic in invalid)
                    if (c == ic) { bad = true; break; }
                sb.Append(bad ? '_' : c);
            }
            var result = sb.ToString().Trim().TrimEnd('.');
            return result.Length == 0 ? fallback : result;
        }

        /// <summary>删除文件；失败静默返回 false。</summary>
        public static bool SafeDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>递归删除目录；失败静默返回 false。</summary>
        public static bool SafeDeleteDirectory(string dir, bool recursive = true)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                if (!Directory.Exists(dir)) return false;
                Directory.Delete(dir, recursive);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// child 是否位于 parent 之下（含自身）。用于防止目录穿越、判断投放目录归属。
        /// 比较前对两侧做全路径归一与去尾分隔符。
        /// </summary>
        public static bool IsSubPathOf(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
            try
            {
                var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
                return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>复制目录（递归）；返回复制的文件数。失败静默停止。</summary>
        public static int CopyDirectory(string source, string target, bool overwrite = true)
        {
            var count = 0;
            if (!DirectoryExists(source)) return count;
            EnsureDirectory(target);
            try
            {
                foreach (var file in Directory.GetFiles(source))
                {
                    var dest = Path.Combine(target, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite);
                    count++;
                }
                foreach (var sub in Directory.GetDirectories(source))
                {
                    var name = Path.GetFileName(sub);
                    count += CopyDirectory(sub, Path.Combine(target, name), overwrite);
                }
            }
            catch
            {
            }
            return count;
        }

        /// <summary>枚举文件，目录不存在或访问受限时返回空集合（不抛）。</summary>
        public static IEnumerable<string> EnumerateFilesSafe(string dir, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly)
        {
            if (!DirectoryExists(dir)) return new string[0];
            try
            {
                return Directory.EnumerateFiles(dir, pattern, option);
            }
            catch
            {
                return new string[0];
            }
        }

        /// <summary>枚举子目录，目录不存在或访问受限时返回空集合（不抛）。</summary>
        public static IEnumerable<string> EnumerateDirectoriesSafe(string dir, string pattern = "*", SearchOption option = SearchOption.TopDirectoryOnly)
        {
            if (!DirectoryExists(dir)) return new string[0];
            try
            {
                return Directory.EnumerateDirectories(dir, pattern, option);
            }
            catch
            {
                return new string[0];
            }
        }

        /// <summary>拼出 %APPDATA%\TradosToolkit 下的路径（不创建目录，需要时自行 EnsureDirectory）。</summary>
        public static string AppDataPath(params string[] segments)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TradosToolkit");
            var path = segments == null || segments.Length == 0 ? root : Path.Combine(Combine(root, segments));
            return path;
        }

        private static string[] Combine(string root, string[] segments)
        {
            var result = new string[segments.Length + 1];
            result[0] = root;
            Array.Copy(segments, 0, result, 1, segments.Length);
            return result;
        }
    }
}
