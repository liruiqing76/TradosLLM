using System.Collections.Generic;
using System.Threading.Tasks;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;

namespace TradosToolkit.TranslationProvider.Engines
{
    /// <summary>
    /// 翻译引擎抽象：Direction 层只负责与 Studio 的接口协议，
    /// 具体"发请求 → 拿译文"的差别全部收敛在实现里。
    /// </summary>
    public interface ITranslationEngine
    {
        /// <summary>显示用名称，如 "TM Service" / "GPT-4o"</summary>
        string DisplayName { get; }

        /// <summary>
        /// 批量查询/翻译。sources 为 Direction 抽取好的源文本
        /// （开启标签支持时含 [[n]] 占位符），返回每个段的候选结果。
        /// TM 引擎返回真实 fuzzy 分数，LLM 引擎返回固定 MT 分数。
        /// contexts 为每段的文档邻段上下文（可整体或单项为 null），仅 LLM 引擎使用。
        /// </summary>
        Task<EngineResult[][]> TranslateAsync(
            LanguagePair languagePair,
            string[] sources,
            bool[] mask,
            string apiKey,
            SegmentContext[] contexts,
            System.Threading.CancellationToken cancellationToken);
    }

    /// <summary>送 LLM 时的邻段上下文：同文档前一段的源文与已确定译文，用于术语/代词一致性。</summary>
    public class SegmentContext
    {
        public string PrevSource { get; set; }
        public string PrevTarget { get; set; }

        /// <summary>
        /// N 段滑动窗口（功能 #5）：按文档序排列、在当前段之前的若干段（源文+已确定译文）。
        /// 供提示词注入多段上下文，提升跨句一致性。前一段也包含在窗口内（即 Window 末元素 == PrevSource/PrevTarget）。
        /// </summary>
        public List<ContextPair> Window { get; set; }
    }

    /// <summary>滑动窗口里的一对上下文：一段的源文与（可能已确定的）译文。</summary>
    public class ContextPair
    {
        public string Source { get; set; }
        public string Target { get; set; }
    }

    public class EngineResult
    {
        public string Translation { get; set; }
        public int MatchPercentage { get; set; }
        public string Origin { get; set; }
    }
}
