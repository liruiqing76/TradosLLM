using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TradosToolkit.Diagnostics;
using TradosToolkit.Common.Catalog;

namespace TradosToolkit.Glossaries
{
    /// <summary>
    /// 一条待审候选：LLM 从文档源文挖掘出的术语 + 目标语建议，等待人工批准/驳回。
    /// </summary>
    public class TermCandidate
    {
        public long Id { get; set; }
        public string SourceLang { get; set; }
        public string TargetLang { get; set; }
        public string Domain { get; set; }
        /// <summary>挖掘到的源语术语。</summary>
        public string CandidateTerm { get; set; }
        /// <summary>LLM 建议的目标语译法。</summary>
        public string ProposedTerm { get; set; }
        /// <summary>出处例句（含该术语的源文片段）。</summary>
        public string Example { get; set; }
        /// <summary>出现次数（同术语重复挖掘时累加）。</summary>
        public int OccurrenceCount { get; set; }
        /// <summary>审批状态：pending|approved|rejected。</summary>
        public string Status { get; set; }
        /// <summary>来源：llm|heuristic（LLM 不可用时的正则降级）。</summary>
        public string SuggestedBy { get; set; }
        public string ReviewedNote { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? ReviewedAt { get; set; }

        public string CreatedAtText => CreatedAt.HasValue ? CreatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
        public string StatusText => TermMiningDb.StatusLabel(Status);
    }

    /// <summary>
    /// 术语审批队列的数据层：term_candidates 表独立存放待审候选，
    /// 与 GlossaryDb 共用同一个 glossary.db 文件（两个连接串互不冲突）。
    /// 批准后由 TermMiner.ApplyCandidate 写入 term_entries + terms(译前) 完成闭环。
    /// </summary>
    public class TermMiningDb
    {
        public const string Pending = "pending";
        public const string Approved = "approved";
        public const string Rejected = "rejected";

        public static string StatusLabel(string status)
        {
            switch (status)
            {
                case Approved: return "已通过";
                case Rejected: return "已驳回";
                default: return "待审批";
            }
        }

        private readonly string _connectionString;

        public TermMiningDb(string path = null)
        {
            path = string.IsNullOrWhiteSpace(path) ? GlossaryDb.DefaultPath : path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _connectionString = "Data Source=" + path + ";Version=3;";
            EnsureSchema();
        }

        private System.Data.SQLite.SQLiteConnection Open()
        {
            var conn = new System.Data.SQLite.SQLiteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private const string TimeFormat = "o";

        private void EnsureSchema()
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS term_candidates(
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    src_lang         TEXT NOT NULL,
    tgt_lang         TEXT NOT NULL,
    domain           TEXT NOT NULL DEFAULT '" + DomainTree.DefaultDomain + @"',
    candidate_term   TEXT NOT NULL,
    proposed_term    TEXT NOT NULL DEFAULT '',
    example          TEXT NOT NULL DEFAULT '',
    occurrence_count INTEGER NOT NULL DEFAULT 1,
    status           TEXT NOT NULL DEFAULT 'pending',
    suggested_by     TEXT NOT NULL DEFAULT 'llm',
    reviewed_note    TEXT NOT NULL DEFAULT '',
    created_at       TEXT,
    reviewed_at      TEXT,
    UNIQUE(src_lang, tgt_lang, domain, candidate_term)
);
CREATE INDEX IF NOT EXISTS ix_term_candidates_status ON term_candidates(status);";
                cmd.ExecuteNonQuery();
            }
        }

        private static string Norm(string domain) =>
            string.IsNullOrWhiteSpace(domain) ? DomainTree.DefaultDomain : domain.Trim();

        private static string NowText() => DateTime.UtcNow.ToString(TimeFormat, CultureInfo.InvariantCulture);

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

        /// <summary>
        /// 写入/累加一条候选。同 (src,tgt,domain,candidate_term) 已存在时：
        /// 保持首次状态，occurrence_count+1，刷新 example 与 proposed_term（若原建议为空）。
        /// 返回该候选人 Id。
        /// </summary>
        public long UpsertCandidate(TermCandidate c)
        {
            var dom = Norm(c.Domain);
            var now = NowText();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, occurrence_count, proposed_term FROM term_candidates " +
                                  "WHERE src_lang=$src COLLATE NOCASE AND tgt_lang=$tgt COLLATE NOCASE AND domain=$domain AND candidate_term=$term";
                cmd.Parameters.AddWithValue("$src", c.SourceLang);
                cmd.Parameters.AddWithValue("$tgt", c.TargetLang);
                cmd.Parameters.AddWithValue("$domain", dom);
                cmd.Parameters.AddWithValue("$term", c.CandidateTerm);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        var id = r.GetInt64(0);
                        var occ = r.GetInt32(1) + 1;
                        var existingProposed = r.IsDBNull(2) ? string.Empty : r.GetString(2);
                        var proposed = string.IsNullOrWhiteSpace(existingProposed) ? (c.ProposedTerm ?? string.Empty) : existingProposed;
                        using (var up = conn.CreateCommand())
                        {
                            up.CommandText = "UPDATE term_candidates SET occurrence_count=$occ, proposed_term=$proposed, example=$example WHERE id=$id";
                            up.Parameters.AddWithValue("$occ", occ);
                            up.Parameters.AddWithValue("$proposed", proposed);
                            up.Parameters.AddWithValue("$example", c.Example ?? string.Empty);
                            up.Parameters.AddWithValue("$id", id);
                            up.ExecuteNonQuery();
                        }
                        return id;
                    }
                }

                cmd.Parameters.Clear();
                cmd.CommandText = "INSERT INTO term_candidates(src_lang,tgt_lang,domain,candidate_term,proposed_term,example,occurrence_count,status,suggested_by,created_at) " +
                                  "VALUES($src,$tgt,$domain,$term,$proposed,$example,1,$status,$by,$created)";
                cmd.Parameters.AddWithValue("$src", c.SourceLang);
                cmd.Parameters.AddWithValue("$tgt", c.TargetLang);
                cmd.Parameters.AddWithValue("$domain", dom);
                cmd.Parameters.AddWithValue("$term", c.CandidateTerm);
                cmd.Parameters.AddWithValue("$proposed", c.ProposedTerm ?? string.Empty);
                cmd.Parameters.AddWithValue("$example", c.Example ?? string.Empty);
                cmd.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(c.Status) ? Pending : c.Status);
                cmd.Parameters.AddWithValue("$by", string.IsNullOrWhiteSpace(c.SuggestedBy) ? "llm" : c.SuggestedBy);
                cmd.Parameters.AddWithValue("$created", now);
                cmd.ExecuteNonQuery();
                cmd.CommandText = "SELECT last_insert_rowid()";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        /// <summary>按语言对（+可选领域/状态）列出待审候选，最新的在前。</summary>
        public List<TermCandidate> List(string srcLang, string tgtLang, string domain = null, string status = null)
        {
            var list = new List<TermCandidate>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                var sql = "SELECT id, src_lang, tgt_lang, domain, candidate_term, proposed_term, example, occurrence_count, status, suggested_by, reviewed_note, created_at, reviewed_at FROM term_candidates WHERE src_lang=$src COLLATE NOCASE AND tgt_lang=$tgt COLLATE NOCASE";
                cmd.Parameters.AddWithValue("$src", srcLang);
                cmd.Parameters.AddWithValue("$tgt", tgtLang);
                if (!string.IsNullOrWhiteSpace(domain)) { sql += " AND domain=$domain"; cmd.Parameters.AddWithValue("$domain", Norm(domain)); }
                if (!string.IsNullOrWhiteSpace(status)) { sql += " AND status=$status"; cmd.Parameters.AddWithValue("$status", status); }
                sql += " ORDER BY (status='pending') DESC, id DESC";
                cmd.CommandText = sql;
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        list.Add(ReadRow(r));
            }
            return list;
        }

        public TermCandidate Get(long id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, src_lang, tgt_lang, domain, candidate_term, proposed_term, example, occurrence_count, status, suggested_by, reviewed_note, created_at, reviewed_at FROM term_candidates WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                using (var r = cmd.ExecuteReader())
                    return r.Read() ? ReadRow(r) : null;
            }
        }

        private static TermCandidate ReadRow(System.Data.SQLite.SQLiteDataReader r)
        {
            return new TermCandidate
            {
                Id = r.GetInt64(0),
                SourceLang = r.GetString(1),
                TargetLang = r.GetString(2),
                Domain = r.IsDBNull(3) ? DomainTree.DefaultDomain : r.GetString(3),
                CandidateTerm = r.GetString(4),
                ProposedTerm = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                Example = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                OccurrenceCount = r.GetInt32(7),
                Status = r.IsDBNull(8) ? Pending : r.GetString(8),
                SuggestedBy = r.IsDBNull(9) ? "llm" : r.GetString(9),
                ReviewedNote = r.IsDBNull(10) ? string.Empty : r.GetString(10),
                CreatedAt = r.IsDBNull(11) ? (DateTime?)null : ParseUtc(r.GetString(11)),
                ReviewedAt = r.IsDBNull(12) ? (DateTime?)null : ParseUtc(r.GetString(12)),
            };
        }

        /// <summary>设置审批状态（+备注），写入审批时间。</summary>
        public void SetStatus(long id, string status, string note = null)
        {
            var now = NowText();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE term_candidates SET status=$status, reviewed_note=$note, reviewed_at=$reviewed WHERE id=$id";
                cmd.Parameters.AddWithValue("$status", status);
                cmd.Parameters.AddWithValue("$note", note ?? string.Empty);
                cmd.Parameters.AddWithValue("$reviewed", now);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>删除一条候选（驳回后清理可选）。</summary>
        public void Delete(long id)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM term_candidates WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }
    }
}