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
    }
}
