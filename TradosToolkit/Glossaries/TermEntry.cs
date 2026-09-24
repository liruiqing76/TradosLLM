using System;
using System.Collections.Generic;
using System.Linq;

namespace TradosToolkit.Glossaries
{
    /// <summary>
    /// 术语状态：对应 MultiTerm 的 preferred / admitted / forbidden 三级。
    /// 存库用固定缩写串（见 TermStatus 常量），避免界面语言切换时结构漂移。
    /// </summary>
    public static class TermStatus
    {
        public const string Preferred = "preferred";
        public const string Admitted = "admitted";
        public const string Forbidden = "forbidden";

        /// <summary>界面显示用中文标签。</summary>
        public static string Label(string status)
        {
            switch (status)
            {
                case Preferred: return "首选";
                case Admitted: return "可用";
                case Forbidden: return "禁用";
                default: return "首选";
            }
        }

        public static string Normalize(string status)
        {
            switch ((status ?? string.Empty).Trim().ToLowerInvariant())
            {
                case Admitted: return Admitted;
                case Forbidden: return Forbidden;
                default: return Preferred;
            }
        }

        /// <summary>下拉可选值（存库值，界面另用 Label 转中文）。</summary>
        public static List<string> All()
        {
            return new List<string> { Preferred, Admitted, Forbidden };
        }
    }

    /// <summary>词性（简表，够用即可；存库用小写缩写）。</summary>
    public static class PartOfSpeech
    {
        public const string None = "";
        public const string Noun = "noun";
        public const string Verb = "verb";
        public const string Adjective = "adjective";
        public const string Adverb = "adverb";
        public const string Phrase = "phrase";
        public const string Abbreviation = "abbreviation";

        public static string Label(string pos)
        {
            switch (pos)
            {
                case Noun: return "名词";
                case Verb: return "动词";
                case Adjective: return "形容词";
                case Adverb: return "副词";
                case Phrase: return "短语";
                case Abbreviation: return "缩写";
                default: return "（无）";
            }
        }

        public static List<string> All()
        {
            return new List<string> { None, Noun, Verb, Adjective, Adverb, Phrase, Abbreviation };
        }

        /// <summary>把存库值归一到可选集合内的规范值；未知值回落为 None（不能借用 TermStatus 的归一，否则词性被清空）。</summary>
        public static string Normalize(string pos)
        {
            var v = (pos ?? string.Empty).Trim().ToLowerInvariant();
            return All().Contains(v) ? v : None;
        }
    }

    /// <summary>
    /// 一条完整术语条目：源术语 → 目标术语，带词性/定义/例句/状态/备注。
    /// 同义词按语言分列存放（TermSynonym），编辑/展示时与主术语一起读写。
    /// </summary>
    public class TermEntry
    {
        public long Id { get; set; }
        public string SourceLang { get; set; }
        public string TargetLang { get; set; }
        public string Domain { get; set; }
        public string FromTerm { get; set; }
        public string ToTerm { get; set; }
        public string PartOfSpeech { get; set; }
        public string Definition { get; set; }
        public string Example { get; set; }
        public string Status { get; set; }
        public string Note { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        /// <summary>同义词（含自身以外的写法），按语言归类。</summary>
        public List<TermSynonym> Synonyms { get; set; } = new List<TermSynonym>();

        public string StatusText => TermStatus.Label(Status);
        public string PartOfSpeechText => Glossaries.PartOfSpeech.Label(PartOfSpeech);
        public string CreatedAtText => CreatedAt.HasValue ? CreatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
        public string UpdatedAtText => UpdatedAt.HasValue ? UpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

        /// <summary>管理界面用的同义词展示文本：源侧/目标侧各取几个拼接，超长省略。</summary>
        public string SynonymText
        {
            get
            {
                if (Synonyms == null || Synonyms.Count == 0) return "—";
                var src = SynonymsFor(SourceLang);
                var tgt = SynonymsFor(TargetLang);
                var parts = new List<string>();
                if (src.Count > 0) parts.Add(string.Join("、", src.Take(3)) + (src.Count > 3 ? "…" : ""));
                if (tgt.Count > 0) parts.Add("→ " + string.Join("、", tgt.Take(3)) + (tgt.Count > 3 ? "…" : ""));
                return parts.Count == 0 ? "—" : string.Join("  ", parts);
            }
        }

        /// <summary>取出指定语言的同义词值列表（不含主术语）。</summary>
        public List<string> SynonymsFor(string lang)
        {
            var list = new List<string>();
            if (Synonyms == null) return list;
            foreach (var s in Synonyms)
                if (s != null && string.Equals(s.Lang, lang, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(s.Term))
                    list.Add(s.Term);
            return list;
        }
    }

    /// <summary>术语同义词：一条记录对应某语言下的一个同义写法。</summary>
    public class TermSynonym
    {
        public long Id { get; set; }
        public long EntryId { get; set; }
        public string Lang { get; set; }
        public string Term { get; set; }
    }
}
