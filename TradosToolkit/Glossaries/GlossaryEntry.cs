using System;

namespace TradosToolkit.Glossaries
{
    /// <summary>
    /// 一条术语替换规则：命中 From 即替换为 To。
    /// 译前库作用于源文，译后库作用于译文。
    /// </summary>
    public class GlossaryEntry
    {
        public long Id { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        /// <summary>所属领域（对应全局领域树，如"通用/法律/医疗…"）。空串按"通用"处理。</summary>
        public string Domain { get; set; }

        /// <summary>入库时间（UTC；库里存 ISO-8601 文本）。加列之前的历史数据为 null。</summary>
        public DateTime? CreatedAt { get; set; }
        /// <summary>最后一次改动时间（UTC；库里存 ISO-8601 文本）。加列之前的历史数据为 null。</summary>
        public DateTime? UpdatedAt { get; set; }

        /// <summary>创建时间的界面展示文本（本地时间）；加列前的历史数据显示"—"。</summary>
        public string CreatedAtText => Fmt(CreatedAt);
        /// <summary>更新时间的界面展示文本（本地时间）；加列前的历史数据显示"—"。</summary>
        public string UpdatedAtText => Fmt(UpdatedAt);

        private static string Fmt(DateTime? t) =>
            t.HasValue ? t.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—";
    }
}
