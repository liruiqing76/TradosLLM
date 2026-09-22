using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationMemories
{
    /// <summary>
    /// 本地记忆库「共享索引」：把 <see cref="LocalTmScanner"/> 的一次昂贵扫描（逐个打开 .sdltm 读语言对/条目数）
    /// 落到 SQLite 数据库，供记忆库管理与收件箱共用。
    /// <para>
    /// 关键点：收件箱每个任务只需「按语言对找一个库」，不必每次全盘扫描（共享目录上可达数分钟）。
    /// 索引按目录 + 文件名 + 最后写入时间 + 大小 做增量刷新：文件没变的行直接复用，新增/改动的行才重新读取。
    /// 索引本身只是命中查询与加速用的缓存，权威结果仍来自 Studio 的 FileBasedTranslationMemory。
    /// </para>
    /// 所有方法线程安全；IO 失败一律降级为「重新扫描」，不抛给调用方。
    /// </summary>
    public static class LocalTmIndex
    {
        /// <summary>索引数据库：%APPDATA%\TradosToolkit\tm-index.db。</summary>
        public static string DbFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TradosToolkit", "tm-index.db");

        /// <summary>旧版 JSON 索引路径；仅用于一次性迁移，迁移后删除。</summary>
        private static string LegacyJsonPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TradosToolkit", "tm-index.json");

        private static readonly object Gate = new object();
        private static readonly object SchemaGate = new object();
        private static bool _schemaReady;

        private static string ConnectionString => $"Data Source={DbFilePath};Version=3;";

        private static System.Data.SQLite.SQLiteConnection Open()
        {
            var conn = new System.Data.SQLite.SQLiteConnection(ConnectionString);
            conn.Open();
            return conn;
        }

        /// <summary>ISO-8601 往返格式（带时区后缀），解析时按 UTC 还原。</summary>
        private const string TimeFormat = "o";

        private static string NowText() => DateTime.UtcNow.ToString(TimeFormat, CultureInfo.InvariantCulture);

        private static void EnsureSchema()
        {
            lock (SchemaGate)
            {
                if (_schemaReady) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DbFilePath));
                    using (var conn = Open())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS tm_index(
    path           TEXT PRIMARY KEY,
    root           TEXT NOT NULL,
    name           TEXT,
    language_pair  TEXT,
    readable       INTEGER NOT NULL DEFAULT 0,
    modified_ticks INTEGER NOT NULL DEFAULT -1,
    size           INTEGER NOT NULL DEFAULT -1,
    scanned_at     TEXT
);
CREATE INDEX IF NOT EXISTS ix_tm_index_lang ON tm_index(language_pair);";
                        cmd.ExecuteNonQuery();
                    }
                    MigrateLegacyJson();
                    _schemaReady = true;
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("LocalTmIndex: 初始化数据库失败", e);
                }
            }
        }

        /// <summary>一次性迁移：旧 tm-index.json 存在则导入（不覆盖已有行），成功导入后删除旧文件。</summary>
        private static void MigrateLegacyJson()
        {
            try
            {
                if (!File.Exists(LegacyJsonPath)) return;
                var doc = new JavaScriptSerializer().Deserialize<LegacyDocument>(File.ReadAllText(LegacyJsonPath));
                var entries = doc?.entries;
                if (entries == null || entries.Count == 0)
                {
                    File.Delete(LegacyJsonPath);
                    return;
                }
                var root = doc.root ?? string.Empty;
                var scannedAt = doc.scannedAt ?? NowText();
                using (var conn = Open())
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var e in entries)
                    {
                        if (e == null || string.IsNullOrEmpty(e.path)) continue;
                        UpsertRow(conn, e.path, root, e.name, e.languagePair,
                                  e.readable, e.modifiedTicks, e.size, scannedAt);
                    }
                    tx.Commit();
                }
                File.Delete(LegacyJsonPath);
                ToolkitLog.Info("LocalTmIndex: 已从旧 JSON 索引迁移 " + entries.Count + " 条到 SQLite");
            }
            catch (Exception e)
            {
                // 迁移失败不影响使用：下次 GetOrScan 会按需重建；这里保留 JSON 不删。
                ToolkitLog.Error("LocalTmIndex: 迁移旧 JSON 索引失败", e);
            }
        }

        /// <summary>
        /// 取目录的记忆库清单：优先读数据库索引，再对「新增 / 改动」的文件做增量补齐，写回索引。
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

            EnsureSchema();
            lock (Gate)
            {
                // 只取属于当前 root 的行；目录变了则天然查不到旧目录条目，等效作废（旧行后续被清理）
                var byPath = LoadEntries(root);
                var result = new List<LocalTmInfo>();
                var changed = 0;
                var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var conn = Open())
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        livePaths.Add(file);

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
                        byPath.TryGetValue(file, out entry);
                        // 命中且文件未改动（时间戳 + 大小一致）→ 直接复用，不再打开库文件
                        var unchanged = ticks >= 0 && entry != null &&
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
                            UpsertRow(conn, entry.path, root, entry.name, entry.languagePair,
                                      entry.readable, entry.modifiedTicks, entry.size, NowText());
                            changed++;
                        }

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

                    // 清理当前 root 下已不存在的文件行
                    foreach (var stale in byPath.Keys.Where(p => !livePaths.Contains(p)).ToList())
                        DeleteRow(conn, stale);

                    tx.Commit();
                }

                result.Sort((a, b) => b.Modified.CompareTo(a.Modified));
                ToolkitLog.Info("LocalTmIndex: " + root + " 共 " + files.Count + " 个库，增量刷新 " + changed + " 个");
                return result;
            }
        }

        /// <summary>
        /// 按语言对（如 "zh-CN → en-US"，OrdinalIgnoreCase）查第一个可用本地库；找不到返回 null。
        /// 走索引（SQL WHERE + language_pair 索引），绝不全盘重扫——收件箱每个任务调用它来挑库。
        /// </summary>
        public static LocalTmInfo FindByLanguagePair(string root, string languagePair, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(languagePair)) return null;
            var pair = languagePair.Trim();
            EnsureSchema();
            lock (Gate)
            {
                try
                {
                    using (var conn = Open())
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
SELECT path, name, language_pair, modified_ticks, size
FROM tm_index
WHERE root = @root AND readable = 1 AND language_pair = @pair COLLATE NOCASE
ORDER BY modified_ticks DESC
LIMIT 1;";
                        cmd.Parameters.AddWithValue("@root", root.Trim());
                        cmd.Parameters.AddWithValue("@pair", pair);
                        using (var r = cmd.ExecuteReader())
                        {
                            if (!r.Read()) return null;
                            long ticks = Convert.ToInt64(r["modified_ticks"]);
                            long size = Convert.ToInt64(r["size"]);
                            return new LocalTmInfo
                            {
                                Name = Convert.ToString(r["name"]),
                                FilePath = Convert.ToString(r["path"]),
                                LanguagePair = Convert.ToString(r["language_pair"]),
                                State = LocalTmState.Ok,
                                Modified = ticks >= 0 ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime() : DateTime.MinValue,
                                Size = size >= 0 ? LocalTmScanner.HumanSize(size) : "-",
                                Units = "-",
                            };
                        }
                    }
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("LocalTmIndex: 按语言对查询失败 " + root, e);
                    return null;
                }
            }
        }

        /// <summary>全量重扫（记忆库管理点「扫描」时用），把当前 root 的结果覆盖写回索引。</summary>
        public static List<LocalTmInfo> Refresh(string root, IProgress<int> progress, CancellationToken ct)
        {
            var list = LocalTmScanner.Scan(root, progress, ct);
            if (string.IsNullOrWhiteSpace(root)) return list;
            root = root.Trim();

            EnsureSchema();
            lock (Gate)
            {
                try
                {
                    using (var conn = Open())
                    using (var tx = conn.BeginTransaction())
                    {
                        DeleteRowsByRoot(conn, root);
                        var scannedAt = NowText();
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
                            UpsertRow(conn, t.FilePath, root, t.Name, t.LanguagePair,
                                      t.State == LocalTmState.Ok, ticks, size, scannedAt);
                        }
                        tx.Commit();
                    }
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("LocalTmIndex: 全量重建失败 " + root, e);
                }
            }
            return list;
        }

        /// <summary>把单个库的条目写进索引（新建 / 导入后调用），避免后续查询重复打开。</summary>
        public static void Upsert(LocalTmInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.FilePath)) return;
            try
            {
                var root = Path.GetDirectoryName(info.FilePath) ?? string.Empty;
                long ticks = -1, size = -1;
                try
                {
                    var fi = new FileInfo(info.FilePath);
                    ticks = fi.LastWriteTimeUtc.Ticks;
                    size = fi.Length;
                }
                catch { /* 读不到就按 -1 记 */ }

                EnsureSchema();
                lock (Gate)
                {
                    using (var conn = Open())
                    using (var tx = conn.BeginTransaction())
                    {
                        UpsertRow(conn, info.FilePath, root, info.Name, info.LanguagePair,
                                  info.State == LocalTmState.Ok, ticks, size, NowText());
                        tx.Commit();
                    }
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LocalTmIndex: 写入条目失败 " + info.FilePath, e);
            }
        }

        private static void UpsertRow(System.Data.SQLite.SQLiteConnection conn, string path, string root,
                                      string name, string languagePair, bool readable,
                                      long modifiedTicks, long size, string scannedAt)
        {
            // 注意：不采用 ON CONFLICT/REPLACE 语法——Studio 捆绑的 SQLite 版本较老，
            // 与 GlossaryDb 一样走「先判存、再 UPDATE/INSERT」的稳妥写法。
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM tm_index WHERE path = @path;";
                cmd.Parameters.AddWithValue("@path", path);
                var exists = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
                cmd.Parameters.Clear();

                cmd.CommandText = exists
                    ? @"UPDATE tm_index SET root=@root, name=@name, language_pair=@pair,
                              readable=@readable, modified_ticks=@ticks, size=@size, scanned_at=@scanned
                        WHERE path=@path;"
                    : @"INSERT INTO tm_index(path, root, name, language_pair, readable, modified_ticks, size, scanned_at)
                        VALUES(@path, @root, @name, @pair, @readable, @ticks, @size, @scanned);";
                cmd.Parameters.AddWithValue("@path", path);
                cmd.Parameters.AddWithValue("@root", root ?? string.Empty);
                cmd.Parameters.AddWithValue("@name", name ?? string.Empty);
                cmd.Parameters.AddWithValue("@pair", languagePair ?? string.Empty);
                cmd.Parameters.AddWithValue("@readable", readable ? 1 : 0);
                cmd.Parameters.AddWithValue("@ticks", modifiedTicks);
                cmd.Parameters.AddWithValue("@size", size);
                cmd.Parameters.AddWithValue("@scanned", scannedAt ?? NowText());
                cmd.ExecuteNonQuery();
            }
        }

        private static void DeleteRow(System.Data.SQLite.SQLiteConnection conn, string path)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM tm_index WHERE path = @path;";
                cmd.Parameters.AddWithValue("@path", path);
                cmd.ExecuteNonQuery();
            }
        }

        private static void DeleteRowsByRoot(System.Data.SQLite.SQLiteConnection conn, string root)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM tm_index WHERE root = @root;";
                cmd.Parameters.AddWithValue("@root", root);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>读某 root 下的全部行，键为文件路径（OrdinalIgnoreCase）。</summary>
        private static Dictionary<string, Entry> LoadEntries(string root)
        {
            var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var conn = Open())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT path, name, language_pair, readable, modified_ticks, size
FROM tm_index WHERE root = @root;";
                    cmd.Parameters.AddWithValue("@root", root);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var p = Convert.ToString(r["path"]);
                            if (string.IsNullOrEmpty(p)) continue;
                            map[p] = new Entry
                            {
                                path = p,
                                name = Convert.ToString(r["name"]),
                                languagePair = Convert.ToString(r["language_pair"]),
                                readable = Convert.ToInt64(r["readable"]) != 0,
                                modifiedTicks = Convert.ToInt64(r["modified_ticks"]),
                                size = Convert.ToInt64(r["size"]),
                            };
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Error("LocalTmIndex: 读取索引行失败 " + root, e);
            }
            return map;
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

        /// <summary>旧版 JSON 索引的落盘结构，仅迁移时反序列化用。</summary>
        private class LegacyDocument
        {
            public string root = string.Empty;
            public string scannedAt = string.Empty;
            public List<LegacyEntry> entries = new List<LegacyEntry>();
        }

        private class LegacyEntry
        {
            public string name = null;
            public string path = null;
            public string languagePair = null;
            public bool readable = false;
            public long modifiedTicks = -1;
            public long size = -1;
        }
    }
}
