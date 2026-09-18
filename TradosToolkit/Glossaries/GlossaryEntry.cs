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
    }
}
