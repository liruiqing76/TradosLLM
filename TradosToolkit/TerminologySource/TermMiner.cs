using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;
using TradosToolkit.TranslationProvider.Engines;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// LLM 术语挖掘器（功能 #2）：从文档源文里挑出值得入库的专业术语/产品名/缩略语，
    /// 连同目标语建议写入审批队列（term_candidates，status=pending），人工批准后闭环写正式术语库。
    /// 流程：正则粗筛（同时作为 LLM 不可用时的降级方案）→ 过滤已登录术语 → 一次 LLM 调用精筛 → 入队。
    /// </summary>
    public static class TermMiner
    {
        /// <summary>
        /// 对一段源文全文执行挖掘。返回新入队/累加的候选条数。
        /// LLM 未配置或调用失败时自动降级为启发式提取（suggested_by=heuristic，仅源词无译法）。
        /// </summary>
        public static async Task<int> MineAsync(string sourceText, string srcLang, string tgtLang, string domain)
        {
            if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(srcLang) || string.IsNullOrWhiteSpace(tgtLang))
                return 0;
            domain = string.IsNullOrWhiteSpace(domain) ? ToolkitConfig.Load().Domain : domain;

            // 1) 正则粗筛出候选短语（名词性信号），统计出现次数
            var rough = HeuristicExtract(sourceText);
            if (rough.Count == 0)
            {
                ToolkitLog.Info("术语挖掘：粗筛无候选，跳过");
                return 0;
            }

            // 2) 剔除已登录术语（term_entries 的 from_term/同义词 + terms 译前表命中即视为已管理）
            var db = new GlossaryDb();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in db.GetTermEntries(srcLang, tgtLang, domain))
            {
                if (!string.IsNullOrWhiteSpace(e.FromTerm)) known.Add(e.FromTerm);
                foreach (var s in e.Synonyms)
                    if (!string.IsNullOrWhiteSpace(s.Term)) known.Add(s.Term);
            }
            foreach (var t in db.GetTerms(GlossaryDb.KindPre, srcLang, tgtLang, domain))
                if (!string.IsNullOrWhiteSpace(t.From)) known.Add(t.From);
            rough.RemoveAll(p => KnownHit(known, p.Text));

            if (rough.Count == 0)
            {
                ToolkitLog.Info("术语挖掘：候选全部已登录，跳过");
                return 0;
            }

            // 送精筛的候选上限，避免超长输入
            var top = rough.OrderByDescending(p => p.Count).Take(60).ToList();
            var samples = top.ToDictionary(p => p.Text, p => p.Example, StringComparer.Ordinal);

            // 3) LLM 精筛：只保留真术语并给出目标语译法；失败降级
            var mined = await RefineWithLlm(srcLang, tgtLang, top.Select(p => p.Text).ToList(), samples).ConfigureAwait(false);
            string suggestedBy;
            if (mined == null || mined.Count == 0)
            {
                suggestedBy = "heuristic";
                mined = top.Select(p => new Candidate { Term = p.Text, Translation = string.Empty, Example = p.Example, Count = p.Count }).ToList();
                ToolkitLog.Info("术语挖掘：LLM 不可用或无结果，降级启发式 " + mined.Count + " 条");
            }
            else
            {
                suggestedBy = "llm";
                foreach (var m in mined)
                {
                    if (samples.ContainsKey(m.Term)) m.Count = top.First(p => p.Text == m.Term).Count;
                    if (string.IsNullOrEmpty(m.Example) && samples.TryGetValue(m.Term, out var ex)) m.Example = ex;
                }
            }

            // 4) 入审批队列（同词重复挖掘累加次数）
            var mining = new TermMiningDb();
            var added = 0;
            foreach (var m in mined.Take(40))
            {
                try
                {
                    mining.UpsertCandidate(new TermCandidate
                    {
                        SourceLang = srcLang,
                        TargetLang = tgtLang,
                        Domain = domain,
                        CandidateTerm = m.Term,
                        ProposedTerm = m.Translation ?? string.Empty,
                        Example = m.Example ?? string.Empty,
                        Status = TermMiningDb.Pending,
                        SuggestedBy = suggestedBy,
                    });
                    added++;
                }
                catch (Exception e)
                {
                    ToolkitLog.Warn("术语挖掘：候选入库失败 " + m.Term, e);
                }
            }
            ToolkitLog.Info("术语挖掘完成: 粗筛=" + top.Count + " 精筛=" + mined.Count + " 入队=" + added + " (" + srcLang + "->" + tgtLang + " " + domain + ")");
            return added;
        }

        /// <summary>
        /// 审批通过：候选写入正式术语库（term_entries 首选 + terms 译前替换），并令翻译引擎术语缓存失效。
        /// </summary>
        public static void ApplyApproved(TermMiningDb mining, GlossaryDb db, long candidateId, string finalTerm = null)
        {
            var c = mining.Get(candidateId);
            if (c == null) throw new InvalidOperationException("候选不存在: " + candidateId);
            var from = (finalTerm ?? c.CandidateTerm ?? string.Empty).Trim();
            var to = (c.ProposedTerm ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(from)) throw new InvalidOperationException("候选术语为空");

            var entry = new TermEntry
            {
                SourceLang = c.SourceLang,
                TargetLang = c.TargetLang,
                Domain = c.Domain,
                FromTerm = from,
                ToTerm = to,
                Status = TermStatus.Preferred,
                Example = c.Example ?? string.Empty,
                Note = "挖掘审批通过 " + DateTime.Now.ToString("yyyy-MM-dd") + " (id=" + c.Id + ")",
            };
            db.SaveTermEntry(entry);
            // 有译法才进译前替换表（无 To 的替换规则没意义）
            if (!string.IsNullOrEmpty(to))
                db.SaveTerm(GlossaryDb.KindPre, c.SourceLang, c.TargetLang,
                    new GlossaryEntry { From = from, To = to, Domain = c.Domain });

            mining.SetStatus(candidateId, TermMiningDb.Approved, "已入正式术语库");
            OpenAiCompatEngine.InvalidateTermCache();
            ToolkitLog.Info("术语审批通过: " + from + " => " + to + " (" + c.SourceLang + "->" + c.TargetLang + ")");
        }

        /// <summary>驳回一条候选。</summary>
        public static void Reject(TermMiningDb mining, long candidateId, string note = null)
        {
            mining.SetStatus(candidateId, TermMiningDb.Rejected, note ?? "人工驳回");
            ToolkitLog.Info("术语候选驳回 id=" + candidateId);
        }

        // ============================ 内部实现 ============================

        private class Phrase { public string Text; public string Example; public int Count; }
        private class Candidate { public string Term; public string Translation; public string Example; public int Count; }

        /// <summary>
        /// 正则粗筛：抓拉丁多词大写短语、缩写（含数字/连字符）、CamelCase、以及中文 2-10 字疑似术语窗口。
        /// 同一短语统计出现次数并记首个例句（所在整句，截 200 字）。
        /// </summary>
        private static List<Phrase> HeuristicExtract(string sourceText)
        {
            var byPhrase = new Dictionary<string, Phrase>(StringComparer.Ordinal);
            var sentences = Regex.Split(sourceText, @"(?<=[。！？!?.;；\n])");

            var patterns = new[]
            {
                // 英文多词专有名词：The Hague Convention / Human Rights Act
                new Regex(@"\b[A-Z][a-zA-Z0-9]+(?:\s+(?:of|the|and|for)?\s*[A-Z][a-zA-Z0-9]+)+\b", RegexOptions.Compiled),
                // 缩写：AI / SDK / WTO / 5G / TCP-IP
                new Regex(@"\b[A-Z]{2,6}[0-9]?\b", RegexOptions.Compiled),
                new Regex(@"\b[A-Z]{2,6}(?:-[A-Z0-9]{1,4})+\b", RegexOptions.Compiled),
                // CamelCase / snake_case 产品名
                new Regex(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b", RegexOptions.Compiled),
                // 中文 2-10 字窗口（无空格语言，靠高频重复信号）
                new Regex(@"[\u4e00-\u9fff]{2,10}", RegexOptions.Compiled),
                // 引号内强调词：「术语」/ "term"
                new Regex(@"[「""]([^「」""]{2,20})[」""]", RegexOptions.Compiled),
            };

            foreach (var raw in sentences)
            {
                var sent = raw.Trim();
                if (sent.Length == 0) continue;
                foreach (var re in patterns)
                {
                    foreach (Match m in re.Matches(sent))
                    {
                        var p = m.Groups[re.ToString().Contains("[^「」") ? 1 : 0].Value.Trim();
                        if (p.Length < 2 || p.Length > 60) continue;
                        // 过滤纯数字/标点/单字虚词
                        if (Regex.IsMatch(p, @"^[\W\d_]+$")) continue;
                        Phrase item;
                        if (!byPhrase.TryGetValue(p, out item))
                        {
                            item = new Phrase { Text = p, Example = sent.Length > 200 ? sent.Substring(0, 200) + "…" : sent, Count = 1 };
                            byPhrase[p] = item;
                        }
                        else item.Count++;
                    }
                }
            }

            // 中文窗口噪声大：要求出现 ≥2 次；英文信号强：≥1 次即可
            return byPhrase.Values.Where(p =>
                Regex.IsMatch(p.Text, @"[\u4e00-\u9fff]") ? p.Count >= 2 : p.Count >= 1)
                .OrderByDescending(p => p.Count).ToList();
        }

        private static bool KnownHit(HashSet<string> known, string phrase)
        {
            if (known.Contains(phrase)) return true;
            // 短语被已登录术语包含、或包含已登录术语 → 视为已管理（保守：只做精确相等与整词包含）
            foreach (var k in known)
                if (k.Length >= 2 && (phrase.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            return false;
        }

        private static async Task<List<Candidate>> RefineWithLlm(
            string srcLang, string tgtLang, List<string> rough, Dictionary<string, string> samples)
        {
            var config = ToolkitConfig.Load();
            if (string.IsNullOrWhiteSpace(config.LlmBaseUrl) || string.IsNullOrWhiteSpace(config.ApiKey)
                || string.IsNullOrWhiteSpace(config.LlmModel))
                return null;

            var sb = new StringBuilder();
            var budget = 6000;
            foreach (var p in rough)
            {
                var line = p;
                string ex;
                if (samples.TryGetValue(p, out ex) && !string.IsNullOrEmpty(ex))
                    line = p + " | " + (ex.Length > 120 ? ex.Substring(0, 120) : ex);
                if (sb.Length + line.Length > budget) break;
                sb.Append(line).Append('\n');
            }

            var prompt = "You are a terminology extraction expert for translators. "
                + "From the candidate list below (format: phrase | context sentence), pick ONLY the terms that truly deserve a glossary entry: "
                + "product names, abbreviations, domain-specific nouns, fixed expressions. Reject ordinary words, generic phrases and noise. "
                + "For each selected term give the standard translation into " + tgtLang + " (source language is " + srcLang + "). "
                + "Output STRICTLY a JSON array, no markdown fences, no commentary. Each element: {\"term\":\"...\",\"translation\":\"...\"}. "
                + "Use the EXACT term string from the input. Maximum 20 items.\n\nCandidates:\n" + sb;

            var body = new Dictionary<string, object>
            {
                { "model", config.LlmModel },
                { "temperature", 0 },
                {
                    "messages", new List<object>
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", "Output only a JSON array. No other text." } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", prompt } },
                    }
                }
            };

            try
            {
                var url = config.LlmBaseUrl.TrimEnd('/') + "/chat/completions";
                var response = await EngineHttp.PostJsonAsync(url, body, config.ApiKey,
                    default, Math.Max(30, config.LlmTimeoutSeconds)).ConfigureAwait(false);
                var choices = EngineHttp.AsList(response.TryGetValue("choices", out var c) ? c : null);
                if (choices == null || choices.Count == 0) return null;
                var choice = EngineHttp.AsDict(choices[0]);
                string content = null;
                if (choice != null &&
                    EngineHttp.AsDict(choice.TryGetValue("message", out var m) ? m : null) is Dictionary<string, object> message)
                    content = EngineHttp.AsString(message.TryGetValue("content", out var ct) ? ct : null);
                return ParseCandidates(content, rough);
            }
            catch (Exception e)
            {
                ToolkitLog.Warn("术语挖掘：LLM 精筛失败，降级启发式", e);
                return null;
            }
        }

        /// <summary>容错解析 LLM 输出：剥 code fence、截取首个 [ ... ] 数组、单条解析失败跳过。</summary>
        private static List<Candidate> ParseCandidates(string content, List<string> roughAllowed)
        {
            var list = new List<Candidate>();
            if (string.IsNullOrWhiteSpace(content)) return list;
            content = content.Trim();
            if (content.StartsWith("```"))
            {
                var nl = content.IndexOf('\n');
                var end = content.LastIndexOf("```", StringComparison.Ordinal);
                if (nl > 0 && end > nl) content = content.Substring(nl + 1, end - nl - 1);
            }
            var start = content.IndexOf('[');
            var stop = content.LastIndexOf(']');
            if (start < 0 || stop <= start) return list;
            content = content.Substring(start, stop - start + 1);

            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 4_000_000 };
                var arr = serializer.DeserializeObject(content) as System.Collections.ArrayList;
                if (arr == null) return list;
                var allowed = new HashSet<string>(roughAllowed, StringComparer.Ordinal);
                foreach (var o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var term = d.TryGetValue("term", out var t) ? t as string : null;
                    var trans = d.TryGetValue("translation", out var tr) ? tr as string : null;
                    term = (term ?? string.Empty).Trim();
                    trans = (trans ?? string.Empty).Trim();
                    // LLM 偶发大小写/空格漂移：宽松匹配回原候选串
                    if (!allowed.Contains(term))
                    {
                        var match = roughAllowed.FirstOrDefault(p => string.Equals(p.Trim(), term, StringComparison.OrdinalIgnoreCase));
                        if (match == null) continue;
                        term = match;
                    }
                    if (term.Length == 0) continue;
                    list.Add(new Candidate { Term = term, Translation = trans });
                    if (list.Count >= 20) break;
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Warn("术语挖掘：LLM JSON 解析失败", e);
            }
            return list;
        }
    }
}