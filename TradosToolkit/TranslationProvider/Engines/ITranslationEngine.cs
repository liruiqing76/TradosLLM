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
        /// </summary>
        Task<EngineResult[][]> TranslateAsync(
            LanguagePair languagePair,
            string[] sources,
            bool[] mask,
            string apiKey,
            System.Threading.CancellationToken cancellationToken);
    }

    public class EngineResult
    {
        public string Translation { get; set; }
        public int MatchPercentage { get; set; }
        public string Origin { get; set; }
    }
}
