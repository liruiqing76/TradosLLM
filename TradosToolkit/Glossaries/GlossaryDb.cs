using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TradosToolkit.Diagnostics;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;

namespace TradosToolkit.Glossaries
{
    /// <summary>
    /// 本地 SQLite 术语库：一张 terms 表同时承载 译前/译后 × 语言对。
    /// 供翻译流水线读取（TermReplacer）与管理界面（增删改查/导入导出）共用。
    /// </summary>
    public class GlossaryDb
    {
        public const string KindPre = "pre";
        public const string KindPost = "post";

        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "TradosToolkit", "glossary.db");

        private readonly string _connectionString;

        public GlossaryDb(string path = null)
        {
            path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _connectionString = $"Data Source={path};Version=3;";
            EnsureSchema();
        }

        private System.Data.SQLite.SQLiteConnection Open()
        {
            var conn = new System.Data.SQLite.SQLiteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        /// <summary>UTC 时间落库格式（ISO-8601 往返格式，带时区后缀，解析时按 UTC 还原）。</summary>
        private const string TimeFormat = "o";

        private void EnsureSchema()
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS terms(
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    kind       TEXT NOT NULL,
    src        TEXT NOT NULL,
    tgt        TEXT NOT NULL,
    domain     TEXT NOT NULL DEFAULT '" + DomainTree.DefaultDomain + @"',
    from_term  TEXT NOT NULL,
    to_term    TEXT NOT NULL,
    created_at TEXT,
    updated_at TEXT,
    UNIQUE(kind, src, tgt, from_term)
);";
                cmd.ExecuteNonQuery();
                AddMissingColumns(cmd);

                // 完整术语模型：一句一条（带词性/定义/例句/状态/备注），同义词另表按语言分列。
                // 与上面的 terms（流水线替换用的扁平表）并存：terms 供 TermReplacer 快速取值，
                // term_entries 供 Studio 原生术语引擎与术语管理界面使用。
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS term_entries(
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    src_lang   TEXT NOT NULL,
    tgt_lang   TEXT NOT NULL,
    domain     TEXT NOT NULL DEFAULT '" + DomainTree.DefaultDomain + @"',
    from_term  TEXT NOT NULL,
    to_term    TEXT NOT NULL,
    pos        TEXT NOT NULL DEFAULT '',
    definition TEXT NOT NULL DEFAULT '',
    example    TEXT NOT NULL DEFAULT '',
    status     TEXT NOT NULL DEFAULT '" + TermStatus.Preferred + @"',
    note       TEXT NOT NULL DEFAULT '',
    created_at TEXT,
    updated_at TEXT,
    UNIQUE(src_lang, tgt_lang, domain, from_term)
);
CREATE INDEX IF NOT EXISTS ix_term_entries_pair ON term_entries(src_lang, tgt_lang);

CREATE TABLE IF NOT EXISTS term_synonyms(
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_id INTEGER NOT NULL,
    lang     TEXT NOT NULL,
    term     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_term_synonyms_entry ON term_synonyms(entry_id);";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 老库按需补列：domain（默认归入"通用"）、created_at/updated_at（历史数据留 null，界面显示"—"）。
        /// 注意：不能用 pragma_table_info('terms') 表值语法，net48 捆绑的旧版 SQLite 不支持，
        /// 必须用 PRAGMA table_info() 传统写法。
        /// </summary>
        private static void AddMissingColumns(System.Data.SQLite.SQLiteCommand cmd)
        {
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cmd.CommandText = "PRAGMA table_info(terms);";
            using (var r = cmd.ExecuteReader())
                while (r.Read()) cols.Add(Convert.ToString(r["name"]));

            if (!cols.Contains("domain"))
            {
                cmd.CommandText = "ALTER TABLE terms ADD COLUMN domain TEXT NOT NULL DEFAULT '" +
                                  DomainTree.DefaultDomain + "';";
                cmd.ExecuteNonQuery();
            }
            if (!cols.Contains("created_at"))
            {
                cmd.CommandText = "ALTER TABLE terms ADD COLUMN created_at TEXT;";
                cmd.ExecuteNonQuery();
            }
            if (!cols.Contains("updated_at"))
            {
                cmd.CommandText = "ALTER TABLE terms ADD COLUMN updated_at TEXT;";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>把库里的 ISO-8601 文本还原成 UTC 时间；空/解析不了返回 null。</summary>
        private static DateTime? ParseUtc(object value)
        {
            var s = value as string;
            if (string.IsNullOrWhiteSpace(s)) return null;
            DateTime dt;
            if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                return null;
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        private static string NowText() => DateTime.UtcNow.ToString(TimeFormat, CultureInfo.InvariantCulture);

        private static string Norm(string domain)
        {
            return string.IsNullOrWhiteSpace(domain) ? DomainTree.DefaultDomain : domain.Trim();
        }

        /// <summary>按领域查询术语；domain 为 null/空时返回全部领域（管理界面看全量用）。</summary>
        public List<GlossaryEntry> GetTerms(string kind, string src, string tgt, string domain = null)
        {
            var list = new List<GlossaryEntry>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                var sql = "SELECT id, from_term, to_term, domain, created_at, updated_at FROM terms WHERE kind=$kind AND src=$src AND tgt=$tgt";
                if (!string.IsNullOrWhiteSpace(domain)) sql += " AND domain=$domain";
                sql += " ORDER BY id";
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$src", src);
                cmd.Parameters.AddWithValue("$tgt", tgt);
                if (!string.IsNullOrWhiteSpace(domain)) cmd.Parameters.AddWithValue("$domain", Norm(domain));
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(new GlossaryEntry
                        {
                            Id = r.GetInt64(0),
                            From = r.GetString(1),
                            To = r.GetString(2),
                            Domain = r.IsDBNull(3) ? DomainTree.DefaultDomain : r.GetString(3),
                            CreatedAt = r.IsDBNull(4) ? (DateTime?)null : ParseUtc(r.GetString(4)),
                            UpdatedAt = r.IsDBNull(5) ? (DateTime?)null : ParseUtc(r.GetString(5)),
                        });
            }
            return list;
        }

        /// <summary>
        /// 写回一条术语。
        /// entry.Id &gt; 0 表示修改既有行：按主键定位更新，允许改 替换前/替换为/领域，
        /// created_at 保持首次入库时间不动，只刷新 updated_at。
        /// entry.Id == 0 表示新增：按数据库唯一键 (kind,src,tgt,from_term) 判存（UNIQUE 不含 domain），
        /// 命中已有行则更新其内容与领域，否则插入并写入 created_at + updated_at。
        /// </summary>
        public void SaveTerm(string kind, string src, string tgt, GlossaryEntry entry)
        {
            var dom = Norm(entry?.Domain);
            var now = NowText();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                // 修改既有行：按主键更新，改 替换前/领域 不会撞唯一键，也不会留下重复行
                if (entry.Id > 0)
                {
                    cmd.CommandText = "UPDATE terms SET from_term=$from, to_term=$to, domain=$domain, updated_at=$updated WHERE id=$id";
                    cmd.Parameters.AddWithValue("$from", entry.From);
                    cmd.Parameters.AddWithValue("$to", entry.To ?? string.Empty);
                    cmd.Parameters.AddWithValue("$domain", dom);
                    cmd.Parameters.AddWithValue("$updated", now);
                    cmd.Parameters.AddWithValue("$id", entry.Id);
                    cmd.ExecuteNonQuery();
                    return;
                }

                // 新增：UNIQUE 只约束 (kind,src,tgt,from_term)，故按这四列判存
                cmd.CommandText = "SELECT COUNT(*) FROM terms WHERE kind=$kind AND src=$src AND tgt=$tgt AND from_term=$from";
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$src", src);
                cmd.Parameters.AddWithValue("$tgt", tgt);
                cmd.Parameters.AddWithValue("$from", entry.From);
                var exists = Convert.ToInt64(cmd.ExecuteScalar()) > 0;

                cmd.Parameters.Clear();
                cmd.CommandText = exists
                    ? "UPDATE terms SET to_term=$to, domain=$domain, updated_at=$updated WHERE kind=$kind AND src=$src AND tgt=$tgt AND from_term=$from"
                    : "INSERT INTO terms(kind,src,tgt,domain,from_term,to_term,created_at,updated_at) VALUES($kind,$src,$tgt,$domain,$from,$to,$created,$updated)";
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$src", src);
                cmd.Parameters.AddWithValue("$tgt", tgt);
                cmd.Parameters.AddWithValue("$domain", dom);
                cmd.Parameters.AddWithValue("$from", entry.From);
                cmd.Parameters.AddWithValue("$to", entry.To ?? string.Empty);
                cmd.Parameters.AddWithValue("$updated", now);
                if (!exists)
                {
                    // 老数据首次被改写时补上创建时间，避免一直显示"—"
                    cmd.Parameters.AddWithValue("$created", entry.CreatedAt.HasValue
                        ? entry.CreatedAt.Value.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture)
                        : now);
                }
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteTerm(long id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM terms WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>已存有术语的语言对列表（管理界面下拉用）。</summary>
        public List<string[]> GetPairs(string kind)
        {
            var list = new List<string[]>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT src, tgt FROM terms WHERE kind=$kind ORDER BY src, tgt";
                cmd.Parameters.AddWithValue("$kind", kind);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) list.Add(new[] { r.GetString(0), r.GetString(1) });
            }
            return list;
        }

        // --------------------------- 完整术语模型（term_entries / term_synonyms） ---------------------------

        /// <summary>
        /// 按语言对（+可选领域）读取完整术语条目。domain 为空时返回该语言对全部领域。
        /// 同义词整批取回后在内存里按 entry_id 归并，避免 N+1 查询。
        /// </summary>
        public List<TermEntry> GetTermEntries(string srcLang, string tgtLang, string domain = null)
        {
            var list = ReadTermEntries(srcLang, tgtLang, domain);
            if (list.Count > 0) return list;

            // 库内语言代码写法可能与调用方不同（en-US / en_US / en）。
            // 精确匹配落空时按规范化形态（小写、- 与 _ 统一）再查一次，避免"术语明明在库里却查不到"。
            var ns = NormalizeLangKey(srcLang);
            var nt = NormalizeLangKey(tgtLang);
            if (ns == (srcLang ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-') &&
                nt == (tgtLang ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-'))
                return list; // 已经就是规范化形态，无第二形态可试

            list = ReadTermEntriesLike(ns, nt, domain);
            if (list.Count > 0)
                ToolkitLog.Info($"术语查询：语言对 {srcLang}-{tgtLang} 精确未命中，按规范化 {ns}-{nt} 命中 {list.Count} 条");
            return list;
        }

        private static string NormalizeLangKey(string lang)
        {
            return string.IsNullOrWhiteSpace(lang)
                ? string.Empty
                : lang.Trim().ToLowerInvariant().Replace('_', '-');
        }

        private List<TermEntry> ReadTermEntriesLike(string srcKey, string tgtKey, string domain)
        {
            var list = new List<TermEntry>();
            var byId = new Dictionary<long, TermEntry>();
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    var sql = "SELECT id, src_lang, tgt_lang, domain, from_term, to_term, pos, definition, example, status, note, created_at, updated_at " +
                              "FROM term_entries " +
                              "WHERE LOWER(REPLACE(src_lang,'_','-'))=$src AND LOWER(REPLACE(tgt_lang,'_','-'))=$tgt";
                    if (!string.IsNullOrWhiteSpace(domain)) sql += " AND domain=$domain";
                    sql += " ORDER BY from_term";
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$src", srcKey);
                    cmd.Parameters.AddWithValue("$tgt", tgtKey);
                    if (!string.IsNullOrWhiteSpace(domain)) cmd.Parameters.AddWithValue("$domain", Norm(domain));
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) list.Add(ReadTermEntryRow(r));
                }
                FillSynonyms(conn, byId, list);
            }
            return list;
        }

        private List<TermEntry> ReadTermEntries(string srcLang, string tgtLang, string domain)
        {
            var list = new List<TermEntry>();
            var byId = new Dictionary<long, TermEntry>();
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    var sql = "SELECT id, src_lang, tgt_lang, domain, from_term, to_term, pos, definition, example, status, note, created_at, updated_at " +
                              "FROM term_entries WHERE src_lang=$src COLLATE NOCASE AND tgt_lang=$tgt COLLATE NOCASE";
                    if (!string.IsNullOrWhiteSpace(domain)) sql += " AND domain=$domain";
                    sql += " ORDER BY from_term";
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$src", srcLang);
                    cmd.Parameters.AddWithValue("$tgt", tgtLang);
                    if (!string.IsNullOrWhiteSpace(domain)) cmd.Parameters.AddWithValue("$domain", Norm(domain));
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            var e = ReadTermEntryRow(r);
                            list.Add(e);
                            byId[e.Id] = e;
                        }
                }

                FillSynonyms(conn, byId, list);
            }
            return list;
        }

        private static TermEntry ReadTermEntryRow(System.Data.SQLite.SQLiteDataReader r)
        {
            return new TermEntry
            {
                Id = r.GetInt64(0),
                SourceLang = r.GetString(1),
                TargetLang = r.GetString(2),
                Domain = r.IsDBNull(3) ? DomainTree.DefaultDomain : r.GetString(3),
                FromTerm = r.GetString(4),
                ToTerm = r.GetString(5),
                PartOfSpeech = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                Definition = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                Example = r.IsDBNull(8) ? string.Empty : r.GetString(8),
                Status = r.IsDBNull(9) ? TermStatus.Preferred : r.GetString(9),
                Note = r.IsDBNull(10) ? string.Empty : r.GetString(10),
                CreatedAt = r.IsDBNull(11) ? (DateTime?)null : ParseUtc(r.GetString(11)),
                UpdatedAt = r.IsDBNull(12) ? (DateTime?)null : ParseUtc(r.GetString(12)),
            };
        }

        /// <summary>整批取同义词并按 entry_id 归并到 list 里的条目上。</summary>
        private static void FillSynonyms(System.Data.SQLite.SQLiteConnection conn,
                                         Dictionary<long, TermEntry> byId, List<TermEntry> list)
        {
            foreach (var e in list)
                if (!byId.ContainsKey(e.Id)) byId[e.Id] = e;
            if (byId.Count == 0) return;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, entry_id, lang, term FROM term_synonyms";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        TermEntry e;
                        if (!byId.TryGetValue(r.GetInt64(1), out e)) continue;
                        e.Synonyms.Add(new TermSynonym
                        {
                            Id = r.GetInt64(0),
                            EntryId = r.GetInt64(1),
                            Lang = r.GetString(2),
                            Term = r.GetString(3),
                        });
                    }
            }
        }

        /// <summary>单条读取（原生术语引擎 GetEntry 用）。</summary>
        public TermEntry GetTermEntry(long id)
        {
            using (var conn = Open())
            {
                TermEntry e = null;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, src_lang, tgt_lang, domain, from_term, to_term, pos, definition, example, status, note, created_at, updated_at " +
                                      "FROM term_entries WHERE id=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    using (var r = cmd.ExecuteReader())
                        if (r.Read())
                            e = new TermEntry
                            {
                                Id = r.GetInt64(0),
                                SourceLang = r.GetString(1),
                                TargetLang = r.GetString(2),
                                Domain = r.IsDBNull(3) ? DomainTree.DefaultDomain : r.GetString(3),
                                FromTerm = r.GetString(4),
                                ToTerm = r.GetString(5),
                                PartOfSpeech = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                                Definition = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                                Example = r.IsDBNull(8) ? string.Empty : r.GetString(8),
                                Status = r.IsDBNull(9) ? TermStatus.Preferred : r.GetString(9),
                                Note = r.IsDBNull(10) ? string.Empty : r.GetString(10),
                                CreatedAt = r.IsDBNull(11) ? (DateTime?)null : ParseUtc(r.GetString(11)),
                                UpdatedAt = r.IsDBNull(12) ? (DateTime?)null : ParseUtc(r.GetString(12)),
                            };
                }
                if (e == null) return null;

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, entry_id, lang, term FROM term_synonyms WHERE entry_id=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            e.Synonyms.Add(new TermSynonym
                            {
                                Id = r.GetInt64(0),
                                EntryId = r.GetInt64(1),
                                Lang = r.GetString(2),
                                Term = r.GetString(3),
                            });
                }
                return e;
            }
        }

        /// <summary>
        /// 写回一条完整术语。
        /// Id &gt; 0 按主键更新；Id == 0 按唯一键 (src_lang,tgt_lang,domain,from_term) 判存。
        /// 同义词按"先删后插"整体替换，保持与界面提交内容一致。返回落库后的条目 Id。
        /// </summary>
        public long SaveTermEntry(TermEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var dom = Norm(entry.Domain);
            var now = NowText();
            var status = TermStatus.Normalize(entry.Status);

            using (var conn = Open())
            {
                long id = entry.Id;
                using (var cmd = conn.CreateCommand())
                {
                    if (id > 0)
                    {
                        cmd.CommandText = "UPDATE term_entries SET from_term=$from, to_term=$to, pos=$pos, definition=$def, " +
                                          "example=$ex, status=$status, note=$note, domain=$domain, updated_at=$updated WHERE id=$id";
                        cmd.Parameters.AddWithValue("$from", entry.FromTerm);
                        cmd.Parameters.AddWithValue("$to", entry.ToTerm ?? string.Empty);
                        cmd.Parameters.AddWithValue("$pos", entry.PartOfSpeech ?? string.Empty);
                        cmd.Parameters.AddWithValue("$def", entry.Definition ?? string.Empty);
                        cmd.Parameters.AddWithValue("$ex", entry.Example ?? string.Empty);
                        cmd.Parameters.AddWithValue("$status", status);
                        cmd.Parameters.AddWithValue("$note", entry.Note ?? string.Empty);
                        cmd.Parameters.AddWithValue("$domain", dom);
                        cmd.Parameters.AddWithValue("$updated", now);
                        cmd.Parameters.AddWithValue("$id", id);
                        cmd.ExecuteNonQuery();
                    }
                    else
                    {
                        // UNIQUE 不含 kind，故按 (src_lang,tgt_lang,domain,from_term) 判存；语言大小写不敏感，避免重复行
                        cmd.CommandText = "SELECT id FROM term_entries WHERE src_lang=$src COLLATE NOCASE AND tgt_lang=$tgt COLLATE NOCASE AND domain=$domain AND from_term=$from";
                        cmd.Parameters.AddWithValue("$src", entry.SourceLang);
                        cmd.Parameters.AddWithValue("$tgt", entry.TargetLang);
                        cmd.Parameters.AddWithValue("$domain", dom);
                        cmd.Parameters.AddWithValue("$from", entry.FromTerm);
                        var hit = cmd.ExecuteScalar();
                        var exists = hit != null && hit != DBNull.Value;

                        cmd.Parameters.Clear();
                        if (exists)
                        {
                            id = Convert.ToInt64(hit);
                            cmd.CommandText = "UPDATE term_entries SET to_term=$to, pos=$pos, definition=$def, example=$ex, " +
                                              "status=$status, note=$note, updated_at=$updated WHERE id=$id";
                            cmd.Parameters.AddWithValue("$to", entry.ToTerm ?? string.Empty);
                            cmd.Parameters.AddWithValue("$pos", entry.PartOfSpeech ?? string.Empty);
                            cmd.Parameters.AddWithValue("$def", entry.Definition ?? string.Empty);
                            cmd.Parameters.AddWithValue("$ex", entry.Example ?? string.Empty);
                            cmd.Parameters.AddWithValue("$status", status);
                            cmd.Parameters.AddWithValue("$note", entry.Note ?? string.Empty);
                            cmd.Parameters.AddWithValue("$updated", now);
                            cmd.Parameters.AddWithValue("$id", id);
                            cmd.ExecuteNonQuery();
                        }
                        else
                        {
                            cmd.CommandText = "INSERT INTO term_entries(src_lang,tgt_lang,domain,from_term,to_term,pos,definition,example,status,note,created_at,updated_at) " +
                                              "VALUES($src,$tgt,$domain,$from,$to,$pos,$def,$ex,$status,$note,$created,$updated)";
                            cmd.Parameters.AddWithValue("$src", entry.SourceLang);
                            cmd.Parameters.AddWithValue("$tgt", entry.TargetLang);
                            cmd.Parameters.AddWithValue("$domain", dom);
                            cmd.Parameters.AddWithValue("$from", entry.FromTerm);
                            cmd.Parameters.AddWithValue("$to", entry.ToTerm ?? string.Empty);
                            cmd.Parameters.AddWithValue("$pos", entry.PartOfSpeech ?? string.Empty);
                            cmd.Parameters.AddWithValue("$def", entry.Definition ?? string.Empty);
                            cmd.Parameters.AddWithValue("$ex", entry.Example ?? string.Empty);
                            cmd.Parameters.AddWithValue("$status", status);
                            cmd.Parameters.AddWithValue("$note", entry.Note ?? string.Empty);
                            cmd.Parameters.AddWithValue("$created", entry.CreatedAt.HasValue
                                ? entry.CreatedAt.Value.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture)
                                : now);
                            cmd.Parameters.AddWithValue("$updated", now);
                            cmd.ExecuteNonQuery();
                            cmd.Parameters.Clear();
                            cmd.CommandText = "SELECT last_insert_rowid()";
                            id = Convert.ToInt64(cmd.ExecuteScalar());
                        }
                    }
                }

                // 同义词整体替换
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM term_synonyms WHERE entry_id=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                if (entry.Synonyms != null && entry.Synonyms.Count > 0)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO term_synonyms(entry_id,lang,term) VALUES($id,$lang,$term)";
                        foreach (var s in entry.Synonyms)
                        {
                            if (s == null || string.IsNullOrWhiteSpace(s.Term) || string.IsNullOrWhiteSpace(s.Lang)) continue;
                            cmd.Parameters.Clear();
                            cmd.Parameters.AddWithValue("$id", id);
                            cmd.Parameters.AddWithValue("$lang", s.Lang);
                            cmd.Parameters.AddWithValue("$term", s.Term.Trim());
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
                return id;
            }
        }

        /// <summary>删除一条完整术语（同义词随删）。</summary>
        public void DeleteTermEntry(long id)
        {
            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM term_entries WHERE id=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM term_synonyms WHERE entry_id=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>完整术语库里已存在的语言对（管理界面下拉用）。</summary>
        public List<string[]> GetTermEntryPairs()
        {
            var list = new List<string[]>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT src_lang, tgt_lang FROM term_entries ORDER BY src_lang, tgt_lang";
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) list.Add(new[] { r.GetString(0), r.GetString(1) });
            }
            return list;
        }

        /// <summary>
        /// 把扁平 terms 表按语言对迁移进 term_entries（一次性、幂等）。
        /// 只在目标语言对尚无条目时迁移，避免覆盖用户在完整模型里已细化的内容。
        /// 返回迁移条数。
        /// </summary>
        public int MigrateFlatTerms(string kind = KindPre)
        {
            var moved = 0;
            using (var conn = Open())
            {
                var flats = new List<object[]>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT src, tgt, domain, from_term, to_term, created_at, updated_at FROM terms WHERE kind=$kind";
                    cmd.Parameters.AddWithValue("$kind", kind);
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            flats.Add(new object[]
                            {
                                r.GetString(0), r.GetString(1),
                                r.IsDBNull(2) ? DomainTree.DefaultDomain : r.GetString(2),
                                r.GetString(3), r.GetString(4),
                                r.IsDBNull(5) ? null : r.GetString(5),
                                r.IsDBNull(6) ? null : r.GetString(6),
                            });
                }

                foreach (var f in flats)
                {
                    string src = (string)f[0], tgt = (string)f[1], dom = (string)f[2],
                           from = (string)f[3], to = (string)f[4];
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM term_entries WHERE src_lang=$src AND tgt_lang=$tgt AND domain=$domain AND from_term=$from";
                        cmd.Parameters.AddWithValue("$src", src);
                        cmd.Parameters.AddWithValue("$tgt", tgt);
                        cmd.Parameters.AddWithValue("$domain", dom);
                        cmd.Parameters.AddWithValue("$from", from);
                        if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) continue;

                        cmd.Parameters.Clear();
                        cmd.CommandText = "INSERT INTO term_entries(src_lang,tgt_lang,domain,from_term,to_term,status,created_at,updated_at) " +
                                          "VALUES($src,$tgt,$domain,$from,$to,$status,$created,$updated)";
                        cmd.Parameters.AddWithValue("$src", src);
                        cmd.Parameters.AddWithValue("$tgt", tgt);
                        cmd.Parameters.AddWithValue("$domain", dom);
                        cmd.Parameters.AddWithValue("$from", from);
                        cmd.Parameters.AddWithValue("$to", to);
                        cmd.Parameters.AddWithValue("$status", TermStatus.Preferred);
                        cmd.Parameters.AddWithValue("$created", (object)f[5] ?? NowText());
                        cmd.Parameters.AddWithValue("$updated", (object)f[6] ?? (object)f[5] ?? NowText());
                        cmd.ExecuteNonQuery();
                        moved++;
                    }
                }
            }
            if (moved > 0)
                ToolkitLog.Info($"GlossaryDb: 扁平术语迁移到完整模型 {moved} 条（kind={kind}）");
            return moved;
        }

        /// <summary>导入两列 CSV（from,to，可有表头），按 from 冲突覆盖。返回导入条数。</summary>
        public int ImportCsv(string kind, string src, string tgt, string csvPath, string domain = null)
        {
            var dom = Norm(domain);
            var count = 0;
            foreach (var line in File.ReadAllLines(csvPath, Encoding.UTF8))
            {
                var parts = SplitCsvLine(line);
                if (parts.Length < 2 || parts[0] == "from") continue;
                SaveTerm(kind, src, tgt, new GlossaryEntry { From = parts[0].Trim(), To = parts[1].Trim(), Domain = dom });
                count++;
            }
            return count;
        }

        /// <summary>导出为两列 CSV（from,to），UTF-8 BOM 方便 Excel 打开。</summary>
        public void ExportCsv(string kind, string src, string tgt, string csvPath, string domain = null)
        {
            var sb = new StringBuilder("from,to\r\n");
            foreach (var e in GetTerms(kind, src, tgt, Norm(domain)))
                sb.Append(EscapeCsv(e.From)).Append(',').Append(EscapeCsv(e.To)).Append("\r\n");
            File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(true));
        }

        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }

        // --------------------------- 完整模型 CSV 导入导出 ---------------------------

        /// <summary>
        /// 导入完整术语 CSV。表头（可选，首列须为 from）：
        /// from,to,pos,status,domain,definition,example,note,src_syn,tgt_syn
        /// 其中 src_syn/tgt_syn 用分号分隔多个同义词。按 (src,tgt,domain,from) 冲突覆盖。返回导入条数。
        /// </summary>
        public int ImportTermEntriesCsv(string src, string tgt, string csvPath, string domain = null)
        {
            var dom = Norm(domain);
            var count = 0;
            foreach (var line in File.ReadAllLines(csvPath, Encoding.UTF8))
            {
                var parts = SplitCsvLine(line);
                if (parts.Length < 2) continue;
                var from = (parts[0] ?? "").Trim();
                if (from.Length == 0 || string.Equals(from, "from", StringComparison.OrdinalIgnoreCase)) continue;

                var entry = new TermEntry
                {
                    SourceLang = src,
                    TargetLang = tgt,
                    FromTerm = from,
                    ToTerm = parts.Length > 1 ? (parts[1] ?? "").Trim() : string.Empty,
                    PartOfSpeech = parts.Length > 2 ? (parts[2] ?? "").Trim() : string.Empty,
                    Status = parts.Length > 3 ? TermStatus.Normalize(parts[3]) : TermStatus.Preferred,
                    Domain = parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]) ? parts[4].Trim() : dom,
                    Definition = parts.Length > 5 ? (parts[5] ?? "").Trim() : string.Empty,
                    Example = parts.Length > 6 ? (parts[6] ?? "").Trim() : string.Empty,
                    Note = parts.Length > 7 ? (parts[7] ?? "").Trim() : string.Empty,
                };
                if (parts.Length > 8)
                    foreach (var s in SplitMulti(parts[8]))
                        entry.Synonyms.Add(new TermSynonym { Lang = src, Term = s });
                if (parts.Length > 9)
                    foreach (var s in SplitMulti(parts[9]))
                        entry.Synonyms.Add(new TermSynonym { Lang = tgt, Term = s });

                SaveTermEntry(entry);
                count++;
            }
            return count;
        }

        /// <summary>导出完整术语为 CSV（含词性/状态/领域/定义/例句/备注/同义词），UTF-8 BOM 方便 Excel 打开。</summary>
        public void ExportTermEntriesCsv(string src, string tgt, string csvPath, string domain = null)
        {
            var sb = new StringBuilder("from,to,pos,status,domain,definition,example,note,src_syn,tgt_syn\r\n");
            foreach (var e in GetTermEntries(src, tgt, Norm(domain)))
            {
                sb.Append(EscapeCsv(e.FromTerm)).Append(',')
                  .Append(EscapeCsv(e.ToTerm)).Append(',')
                  .Append(EscapeCsv(e.PartOfSpeech)).Append(',')
                  .Append(EscapeCsv(e.Status)).Append(',')
                  .Append(EscapeCsv(e.Domain)).Append(',')
                  .Append(EscapeCsv(e.Definition)).Append(',')
                  .Append(EscapeCsv(e.Example)).Append(',')
                  .Append(EscapeCsv(e.Note)).Append(',')
                  .Append(EscapeCsv(string.Join(";", e.SynonymsFor(src)))).Append(',')
                  .Append(EscapeCsv(string.Join(";", e.SynonymsFor(tgt)))).Append("\r\n");
            }
            File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>库内是否已有完整术语条目（用于判断术语库是否为空）。</summary>
        public bool HasTermEntries()
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM term_entries";
                return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            }
        }

        /// <summary>
        /// 返回库内全部语言对（去重，优先完整模型，兼容扁平 terms 表）；
        /// 供项目挂载时逐个语言对生成术语源。
        /// </summary>
        public List<string[]> GetAllTermPairs()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string[]>();
            Action<string, string> add = (s, t) =>
            {
                if (string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(t)) return;
                var key = s + "|" + t;
                if (seen.Add(key)) list.Add(new[] { s, t });
            };

            using (var conn = Open())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT DISTINCT src_lang, tgt_lang FROM term_entries ORDER BY src_lang, tgt_lang";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) add(r.GetString(0), r.GetString(1));
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT DISTINCT src, tgt FROM terms ORDER BY src, tgt";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) add(r.GetString(0), r.GetString(1));
                }
            }
            return list;
        }

        /// <summary>按分号（也兼容竖线/顿号）拆多个同义词，去空白去重。</summary>
        private static List<string> SplitMulti(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var raw in text.Split(new[] { ';', '|', '；', '、' }))
            {
                var t = (raw ?? "").Trim();
                if (t.Length == 0) continue;
                if (!list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
            }
            return list;
        }

        private static string[] SplitCsvLine(string line)
        {
            // TODO M4: 正式 CSV 解析（跨行引号字段），当前支持常规两列
            if (line == null) return new string[0];
            if (line.StartsWith("\""))
            {
                var end = line.IndexOf("\",", StringComparison.Ordinal);
                if (end > 0)
                    return new[] { line.Substring(1, end - 1).Replace("\"\"", "\""), line.Substring(end + 2) };
            }
            var i = line.IndexOf(',');
            return i < 0 ? new[] { line } : new[] { line.Substring(0, i), line.Substring(i + 1) };
        }
    }
}
