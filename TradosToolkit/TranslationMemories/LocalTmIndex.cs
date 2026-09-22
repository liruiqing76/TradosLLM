using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationMemories
{
    /// <summary>
    /// 本地记忆库「共享索引」：把 <see cref="LocalTmScanner"/> 的一次昂贵扫描（逐个打开 .sdltm 读语言对/条目数）
    /// 落到磁盘缓存，供记忆库管理与收件箱共用。
    /// <para>
    /// 关键点：收件箱每个任务只需「按语言对找一个库」，不必每次全盘扫描（共享目录上可达数分钟）。
    /// 索引按目录 + 文件名 + 最后写入时间 + 大小 做增量刷新：文件没变的行直接复用，新增/改动的行才重新读取。
    /// 索引文件本身只是命中查询与加速用的缓存，权威结果仍来自 Studio 的 FileBasedTranslationMemory。
    /// </para>
    /// 所有方法线程安全；IO 失败一律降级为「重新扫描」，不抛给调用方。
    /// </summary>
    public static class LocalTmIndex
    {
        /// <summary>索引文件：%APPDATA%\TradosToolkit\tm-index.json。</summary>
        public static string IndexFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TradosToolkit", "tm-index.json");

        private static readonly object Gate = new object();

        /// <summary>磁盘上的索引文档。</summary>
        internal class Document
        {
            public string root = string.Empty;
            public string scannedAt = string.Empty;
            public List<Entry> entries = new List<Entry>();
        }

        /// <summary>一条索引记录；凭证 = 目录 + 文件名 + 修改时间 + 大小。</summary>
        internal class Entry
        {
            public string name;
            public string path;
            public string languagePair;
            public bool readable;
            public long modifiedTicks;
            public long size;
        }

        /// <summary>
        /// 取目录的记忆库清单：优先读磁盘索引，再对「新增 / 改动」的文件做增量补齐，写回索引。
        /// 文件没变的行不会再次打开，因此常态下几乎是瞬时返回。
        /// </summary>
        public static List<LocalTmInfo> GetOrScan(string root, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return new List<LocalTmInfo>();
            root = root.Trim();

            List<string> files;
            try
            {
                files = Directory.GetFiles(root, "*.sdltm", SearchOption.AllDirectories).ToList();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LocalTmIndex: 枚举目录失败 " + root, e);
                return new List<LocalTmInfo>();
            }

            lock (Gate)
            {
                var doc = ReadDocument();
                // 目录变了 → 旧索引作废（不同目录的条目不应混用）
                if (!string.Equals(doc.root, root, StringComparison.OrdinalIgnoreCase))
                    doc = new Document { root = root };
                var byPath = doc.entries
                    .Where(x => x != null && !string.IsNullOrEmpty(x.path))
                    .GroupBy(x => x.path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var result = new List<LocalTmInfo>();
                var kept = new List<Entry>();
                var changed = 0;

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    long ticks = -1, size = -1;
                    try
                    {
                        var fi = new FileInfo(file);
                        ticks = fi.LastWriteTimeUtc.Ticks;
                        size = fi.Length;
                    }
                    catch (Exception e)
                    {
                        ToolkitLog.Error("LocalTmIndex: 读文件信息失败 " + file, e);
                    }

                    Entry entry = null;
                    var unchanged = ticks >= 0 && byPath.TryGetValue(file, out entry) && entry != null &&
                                    entry.modifiedTicks == ticks && entry.size == size;
                    if (!unchanged)
                    {
                        // 新增或改动 → 用扫描器重新打开这一个文件
                        var info = LocalTmScanner.ReadOne(file);
                        entry = new Entry
                        {
                            name = info.Name,
                            path = file,
                            languagePair = info.LanguagePair,
                            readable = info.State == LocalTmState.Ok,
                            modifiedTicks = ticks,
                            size = size,
                        };
                        changed++;
                    }

                    kept.Add(entry);
                    result.Add(new LocalTmInfo
                    {
                        Name = entry.name,
                        FilePath = entry.path,
                        LanguagePair = entry.languagePair,
                        State = entry.readable ? LocalTmState.Ok : LocalTmState.Protected,
                        Modified = ticks >= 0 ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime() : DateTime.MinValue,
                        Size = size >= 0 ? LocalTmScanner.HumanSize(size) : "-",
                        Units = "-",
                    });
                }

                result.Sort((a, b) => b.Modified.CompareTo(a.Modified));
                doc.entries = kept;
                doc.scannedAt = DateTime.Now.ToString("o");
                if (changed > 0 || !File.Exists(IndexFilePath)) WriteDocument(doc);
                ToolkitLog.Info("LocalTmIndex: " + root + " 共 " + files.Count + " 个库，增量刷新 " + changed + " 个");
                return result;
            }
        }

        /// <summary>
        /// 按语言对（如 "zh-CN → en-US"，OrdinalIgnoreCase）查第一个可用本地库；找不到返回 null。
        /// 走索引，绝不全盘重扫——收件箱每个任务调用它来挑库。
        /// </summary>
        public static LocalTmInfo FindByLanguagePair(string root, string languagePair, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(languagePair)) return null;
            var pair = languagePair.Trim();
            return GetOrScan(root, ct)
                .FirstOrDefault(t => t.State == LocalTmState.Ok &&
                                     string.Equals(t.LanguagePair, pair, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>全量重扫（记忆库管理点「扫描」时用），把结果覆盖写回索引。</summary>
        public static List<LocalTmInfo> Refresh(string root, IProgress<int> progress, CancellationToken ct)
        {
            var list = LocalTmScanner.Scan(root, progress, ct);
            lock (Gate)
            {
                var entries = new List<Entry>();
                foreach (var t in list)
                {
                    long ticks = -1, size = -1;
                    try
                    {
                        var fi = new FileInfo(t.FilePath);
                        ticks = fi.LastWriteTimeUtc.Ticks;
                        size = fi.Length;
                    }
                    catch { /* 读不到就按 -1 记，下次仍会重算 */ }
                    entries.Add(new Entry
                    {
                        name = t.Name,
                        path = t.FilePath,
                        languagePair = t.LanguagePair,
                        readable = t.State == LocalTmState.Ok,
                        modifiedTicks = ticks,
                        size = size,
                    });
                }
                WriteDocument(new Document { root = root, scannedAt = DateTime.Now.ToString("o"), entries = entries });
            }
            return list;
        }

        /// <summary>把单个库的条目写进索引（新建 / 导入后调用），避免后续查询重复打开。</summary>
        public static void Upsert(LocalTmInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.FilePath)) return;
            try
            {
                var root = Path.GetDirectoryName(info.FilePath);
                long ticks = -1, size = -1;
                try
                {
                    var fi = new FileInfo(info.FilePath);
                    ticks = fi.LastWriteTimeUtc.Ticks;
                    size = fi.Length;
                }
                catch { /* 读不到就按 -1 记 */ }

                lock (Gate)
                {
                    var doc = ReadDocument();
                    if (!string.Equals(doc.root, root, StringComparison.OrdinalIgnoreCase))
                        doc = new Document { root = root };
                    doc.entries.RemoveAll(x => x != null &&
                        string.Equals(x.path, info.FilePath, StringComparison.OrdinalIgnoreCase));
                    doc.entries.Add(new Entry
                    {
                        name = info.Name,
                        path = info.FilePath,
                        languagePair = info.LanguagePair,
                        readable = info.State == LocalTmState.Ok,
                        modifiedTicks = ticks,
                        size = size,
                    });
                    doc.scannedAt = DateTime.Now.ToString("o");
                    WriteDocument(doc);
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LocalTmIndex: 写入条目失败 " + info.FilePath, e);
            }
        }

        private static Document ReadDocument()
        {
            try
            {
                if (!File.Exists(IndexFilePath)) return new Document();
                var doc = new JavaScriptSerializer().Deserialize<Document>(File.ReadAllText(IndexFilePath));
                if (doc == null) return new Document();
                if (doc.entries == null) doc.entries = new List<Entry>();
                doc.entries.RemoveAll(x => x == null);
                return doc;
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LocalTmIndex: 读取索引失败，将重建", e);
                return new Document();
            }
        }

        private static void WriteDocument(Document doc)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IndexFilePath));
                File.WriteAllText(IndexFilePath, new JavaScriptSerializer().Serialize(doc));
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LocalTmIndex: 写入索引失败", e);
            }
        }
    }
}
