using System;
using System.Collections.Generic;
using System.IO;
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

        private void EnsureSchema()
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS terms(
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    kind      TEXT NOT NULL,
    src       TEXT NOT NULL,
    tgt       TEXT NOT NULL,
    domain    TEXT NOT NULL DEFAULT '" + DomainTree.DefaultDomain + @"',
    from_term TEXT NOT NULL,
    to_term   TEXT NOT NULL,
    UNIQUE(kind, src, tgt, from_term)
);";
                cmd.ExecuteNonQuery();
                // 老库缺 domain 列时迁移补齐（默认归入"通用"）。
                // 注意：不能用 pragma_table_info('terms') 表值语法，net48 捆绑的旧版 SQLite 不支持，
                // 必须用 PRAGMA table_info() 传统写法。
                cmd.CommandText = "PRAGMA table_info(terms);";
                var hasDomain = false;
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (string.Equals(Convert.ToString(r["name"]), "domain", StringComparison.OrdinalIgnoreCase))
                        { hasDomain = true; break; }
                if (!hasDomain)
                {
                    cmd.CommandText = "ALTER TABLE terms ADD COLUMN domain TEXT NOT NULL DEFAULT '" +
                                      DomainTree.DefaultDomain + "';";
                    cmd.ExecuteNonQuery();
                }
            }
        }

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
                var sql = "SELECT id, from_term, to_term, domain FROM terms WHERE kind=$kind AND src=$src AND tgt=$tgt";
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
                        });
            }
            return list;
        }

        public void SaveTerm(string kind, string src, string tgt, GlossaryEntry entry)
        {
            var dom = Norm(entry?.Domain);
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                // 按 (kind,src,tgt,domain,from_term) 做手动 UPSERT，避免跨领域同 from 互相覆盖
                cmd.CommandText = "SELECT COUNT(*) FROM terms WHERE kind=$kind AND src=$src AND tgt=$tgt AND domain=$domain AND from_term=$from";
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$src", src);
                cmd.Parameters.AddWithValue("$tgt", tgt);
                cmd.Parameters.AddWithValue("$domain", dom);
                cmd.Parameters.AddWithValue("$from", entry.From);
                var exists = Convert.ToInt64(cmd.ExecuteScalar()) > 0;

                cmd.Parameters.Clear();
                cmd.CommandText = exists
                    ? "UPDATE terms SET to_term=$to WHERE kind=$kind AND src=$src AND tgt=$tgt AND domain=$domain AND from_term=$from"
                    : "INSERT INTO terms(kind,src,tgt,domain,from_term,to_term) VALUES($kind,$src,$tgt,$domain,$from,$to)";
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$src", src);
                cmd.Parameters.AddWithValue("$tgt", tgt);
                cmd.Parameters.AddWithValue("$domain", dom);
                cmd.Parameters.AddWithValue("$from", entry.From);
                cmd.Parameters.AddWithValue("$to", entry.To ?? string.Empty);
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
            return s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
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
