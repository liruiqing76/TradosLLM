using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TradosToolkit.TranslationProvider.Engines
{
    /// <summary>
    /// 批内重复段去重的纯逻辑（无 Studio 依赖，可独立验证）。
    /// 同一批里"归一化后源文相同"的段只送引擎一次，其余段复用结果：
    /// 既砍掉重复的网关调用（本地网关 ~7s/段，这是最大等待源），
    /// 又天然保证同一源文全文译法一致。
    /// </summary>
    public static class SegmentDedup
    {
        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>键归一化：Trim + 连续空白压成单个空格（[[n]] 占位符按字面参与）。</summary>
        public static string Normalize(string text) =>
            Whitespace.Replace((text ?? string.Empty).Trim(), " ");

        /// <summary>
        /// 计算送引擎的掩码与重复映射。
        /// 返回重复段数；engineMask = mask 但重复段置 false（不送引擎）；
        /// dupOf[i] = 该段复用哪个首现段的下标（-1 表示不适用）。
        /// 调用方在拿到引擎结果后按 dupOf 把首现结果抄给重复段。
        /// </summary>
        public static int Plan(string[] sources, bool[] mask, out bool[] engineMask, out int[] dupOf)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            var n = sources.Length;
            engineMask = new bool[n];
            dupOf = new int[n];
            for (var i = 0; i < n; i++)
            {
                engineMask[i] = mask != null && i < mask.Length ? mask[i] : true;
                dupOf[i] = -1;
            }

            var first = new Dictionary<string, int>();
            var duplicates = 0;
            for (var i = 0; i < n; i++)
            {
                if (!engineMask[i] || string.IsNullOrEmpty(sources[i])) continue;
                var key = Normalize(sources[i]);
                if (first.TryGetValue(key, out var head))
                {
                    dupOf[i] = head;
                    engineMask[i] = false;
                    duplicates++;
                }
                else
                {
                    first[key] = i;
                }
            }
            return duplicates;
        }

        /// <summary>把首现段的引擎结果抄给重复段（原地填充 batches）。</summary>
        public static void Apply(EngineResult[][] batches, int[] dupOf)
        {
            if (batches == null || dupOf == null) return;
            for (var i = 0; i < dupOf.Length && i < batches.Length; i++)
            {
                var head = dupOf[i];
                if (head < 0 || head >= batches.Length) continue;
                var src = batches[head];
                batches[i] = src != null && src.Length > 0 ? src : new EngineResult[0];
            }
        }
    }
}
