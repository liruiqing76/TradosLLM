using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Sdl.ProjectAutomation.Core;

namespace TradosToolkit.Server
{
    /// <summary>
    /// 回译语义校验（Back-Translation QA / 往返收敛质检）——本插件的创新型质检能力。
    ///
    /// 痛点：现有 QA 与一致性审计都是"规则式"（数字/标点/术语/重复/标签），只能抓机械错误，
    /// 抓不到语义级问题（漏译、增译、错译、语义漂移）——而这些恰恰最致命、最费人工。
    ///
    /// 思路：把译文独立回译成源语言，再与原源文比对。忠实译文经得起"往返"；差异越大越可疑。
    /// 三段式流水线，兼顾质量与成本：
    ///   1) 本地预筛（零 LLM 成本）：复用分诊规则剔除空/纯符号/URL 噪声，并对相同 (源,译) 去重；
    ///   2) 回译（LLM）：把每批译文回译成源语言，输入仅 {id,text}；
    ///   3) 判官（LLM + 确定性收敛度）：LLM 判语义保真，叠加本地字符 bigram Dice 收敛度做锚点，
    ///      判官放过但收敛度低于阈值的自动降级为"需复核"。
    ///
    /// GET  = 预检计划（不调 LLM，先看清要花多少）；
    /// POST = 执行；body {maxSegments, batchSize, minScore, async}，async=1 时走后台任务返回 202。
    /// </summary>
    public static class BackTranslateQa
    {
        private const int DefaultMaxSegments = 300;
        private const int MaxSegmentsCap = 2000;
        private const int DefaultBatchSize = 10;
        private const int MinBatchSize = 2;
        private const int MaxBatchSize = 30;
        private const int DefaultMinScore = 60;

        private const string CsvHeader =
            "id,dice,llmScore,score,verdict,type,reason,suggestion,source,target,back\r\n";

        public static ApiResult Handle(string method, Dictionary<string, string> query, string body,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                return Run(query, body, ct);
            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                return Plan(query);
            return ApiResult.Json(405, ProjectApi.Error("btqa 端点仅支持 GET(预检) 与 POST(执行)"));
        }

        /// <summary>GET：零成本预检——看清有多少段要校验、多少能省掉、预计多少次 LLM 调用。</summary>
        private static ApiResult Plan(Dictionary<string, string> query)
        {
            return ProjectApi.WithTargetBilingual(query, (info, file, bp, rows) =>
            {
                var lang = file.Language == null ? null : file.Language.IsoAbbreviation;
                var srcLang = info.SourceLanguage == null ? null : info.SourceLanguage.IsoAbbreviation;
                var plan = BuildPlan(rows);
                var estCalls = EstimateCalls(plan.Unique.Count, DefaultBatchSize);
                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "project", info.Name }, { "file", file.Name },
                    { "language", lang }, { "srcLanguage", srcLang },
                    { "segments", rows.Count },
                    { "translated", plan.Translated },
                    { "untranslated", plan.Untranslated },
                    { "noiseSkipped", plan.Skipped },
                    { "duplicatesCollapsed", plan.Deduped },
                    { "plannedChecks", plan.Unique.Count },
                    { "batchSize", DefaultBatchSize },
                    { "estLlmCalls", estCalls },
                    { "skippedSamples", plan.SkippedSamples },
                    { "summary", BuildPlanSummary(rows.Count, plan, estCalls) },
                });
            });
        }

        private static ApiResult Run(Dictionary<string, string> query, string body,
            CancellationToken ct = default(CancellationToken))
        {
            var config = ToolkitConfig.Load();
            if (string.IsNullOrWhiteSpace(config.LlmBaseUrl) || string.IsNullOrWhiteSpace(config.LlmModel)
                || string.IsNullOrWhiteSpace(config.ApiKey))
                return ApiResult.Json(412, ProjectApi.Error("未配置 LLM(llmBaseUrl/llmModel/apiKey见 config.json)，无法回译校验"));

            var request = ProjectApi.ParseBody(body);
            var maxSegments = request.ContainsKey("maxSegments")
                ? Math.Max(1, Math.Min(MaxSegmentsCap, Convert.ToInt32(request["maxSegments"]))) : DefaultMaxSegments;
            var batchSize = request.ContainsKey("batchSize")
                ? Math.Max(MinBatchSize, Math.Min(MaxBatchSize, Convert.ToInt32(request["batchSize"]))) : DefaultBatchSize;
            var minScore = request.ContainsKey("minScore")
                ? Math.Max(0, Math.Min(100, Convert.ToInt32(request["minScore"]))) : DefaultMinScore;
            var wantAsync = ProjectApi.Bool(request, "async");

            if (!wantAsync)
                return Execute(query, config, maxSegments, batchSize, minScore, null, ct);

            var path = query.TryGetValue("path", out var p) ? p : null;
            BackgroundTask st = null;
            st = TaskRegistry.Start("btqa", path, () =>
                Execute(query, config, maxSegments, batchSize, minScore,
                    progress => TaskRegistry.SetProgress(st == null ? null : st.Id, progress),
                    TaskRegistry.GetCancelToken(st == null ? null : st.Id)));

            return ApiResult.Json(202, new Dictionary<string, object>
            {
                { "taskId", st.Id }, { "task", "btqa" }, { "projectPath", path }, { "status", "running" },
            });
        }

        /// <summary>UI 线程解析出的纯数据快照（不含 Studio 自动化对象，可安全带到后台线程使用）。</summary>
        private sealed class BilingualSnapshot
        {
            public string ProjectName;
            public string FileName;
            public string Language;
            public string SourceLanguage;
            public List<BilingualSegment> Rows;
        }

        private static ApiResult Execute(Dictionary<string, string> query, ToolkitConfig config,
            int maxSegments, int batchSize, int minScore, Action<Dictionary<string, object>> progress,
            CancellationToken ct = default(CancellationToken))
        {
            // WithTargetBilingual 经 WithProject→OnUi 在 Studio UI 线程上执行回调，而下面的回译/判官
            // 是批式 LLM 调用（可达数十秒到数分钟）。若整段留在回调里，宿主 UI 会全程冻结；
            // 因此回调内只解析出纯数据快照，LLM 调用留在本线程（HTTP 线程 / TaskRegistry 后台线程）。
            BilingualSnapshot snap = null;
            var resolution = ProjectApi.WithTargetBilingual(query, (info, file, bp, rows) =>
            {
                snap = new BilingualSnapshot
                {
                    ProjectName = info.Name,
                    FileName = file.Name,
                    Language = file.Language == null ? null : file.Language.IsoAbbreviation,
                    SourceLanguage = info.SourceLanguage == null ? null : info.SourceLanguage.IsoAbbreviation,
                    Rows = rows,
                };
                return ApiResult.Json(200, new Dictionary<string, object>());
            });
            if (snap == null)
                return resolution; // 解析失败（缺 path / 多目标文件 / 文件不存在等），原样返回错误

            var plan = BuildPlan(snap.Rows);
            var unique = plan.Unique;
            if (unique.Count > maxSegments) unique = unique.Take(maxSegments).ToList();

            if (unique.Count == 0)
                return EmptyResult(snap.ProjectName, snap.FileName, snap.Language, snap.SourceLanguage, plan);

            return RunChecks(snap.ProjectName, snap.FileName, snap.Language, snap.SourceLanguage,
                unique, plan, config, batchSize, minScore, progress, ct);
        }

        private static ApiResult RunChecks(string projectName, string fileName, string lang, string srcLang,
            List<BilingualSegment> unique, BtqaPlan plan, ToolkitConfig config,
            int batchSize, int minScore, Action<Dictionary<string, object>> progress,
            CancellationToken ct = default(CancellationToken))
        {
            var termsText = ProjectApi.GlossaryTermsText(config.Domain, srcLang, lang);
            var batches = ProjectApi.Chunk(unique, batchSize).ToList();

            // —— 阶段1：回译（target -> source）——
            var backById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var backError = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < batches.Count; i++)
            {
                if (ct.IsCancellationRequested) return Cancelled();
                var batch = batches[i];
                Report(progress, "backtranslate", i + 1, batches.Count, backById.Count);

                var input = batch.Select(r => new Dictionary<string, string>
                {
                    { "id", r.Id ?? string.Empty }, { "text", r.Target ?? string.Empty },
                }).ToList();
                try
                {
                    var reply = ProjectApi.CallLlmJson(config, BackPrompt(srcLang, lang), input);
                    foreach (var d in ProjectApi.JsonArray(reply))
                    {
                        var id = d.ContainsKey("id") ? Convert.ToString(d["id"]).Trim() : null;
                        var back = d.ContainsKey("back") ? Convert.ToString(d["back"]) : null;
                        if (!string.IsNullOrEmpty(id) && back != null) backById[id] = back;
                    }
                }
                catch (Exception e)
                {
                    foreach (var r in batch) backError[r.Id ?? string.Empty] = e.Message;
                }
            }

            // —— 阶段2：判官（LLM 语义保真 + 本地收敛度锚点）——
            var items = new List<Dictionary<string, object>>();
            for (int i = 0; i < batches.Count; i++)
            {
                if (ct.IsCancellationRequested) return Cancelled();
                var batch = batches[i];
                var ready = batch.Where(r => backById.ContainsKey(r.Id)).ToList();
                if (ready.Count == 0) continue;
                Report(progress, "judge", i + 1, batches.Count, items.Count);

                var input = ready.Select(r => new Dictionary<string, string>
                {
                    { "id", r.Id ?? string.Empty },
                    { "source", r.Source ?? string.Empty },
                    { "back", backById[r.Id] },
                    { "target", r.Target ?? string.Empty },
                }).ToList();
                try
                {
                    var reply = ProjectApi.CallLlmJson(config, JudgePrompt(lang, termsText), input);
                    var byId = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var d in ProjectApi.JsonArray(reply))
                        if (d.ContainsKey("id")) byId[Convert.ToString(d["id"]).Trim()] = d;
                    foreach (var r in ready)
                        items.Add(MergeItem(r, backById[r.Id],
                            byId.TryGetValue(r.Id, out var v) ? v : null, plan, minScore));
                }
                catch (Exception e)
                {
                    foreach (var r in ready) items.Add(FailedItem(r, backById[r.Id], e.Message, plan));
                }
            }

            // 回译阶段整批失败的段单列出来，避免静默漏报
            foreach (var r in unique)
            {
                var key = r.Id ?? string.Empty;
                if (!backById.ContainsKey(key) && backError.ContainsKey(key))
                    items.Add(FailedItem(r, null, backError[key], plan));
            }

            Report(progress, "done", batches.Count, batches.Count, items.Count);
            return BuildResult(projectName, fileName, lang, srcLang, plan, unique.Count, config.Domain, items);
        }

        private static ApiResult BuildResult(string projectName, string fileName, string lang, string srcLang,
            BtqaPlan plan, int checkedCount, string domain, List<Dictionary<string, object>> items)
        {
            var summary = new Dictionary<string, long> { { "red", 0 }, { "amber", 0 }, { "ok", 0 }, { "error", 0 } };
            foreach (var it in items)
            {
                var v = ProjectApi.Str(it, "verdict") ?? "ok";
                if (!summary.ContainsKey(v)) summary[v] = 0;
                summary[v]++;
            }

            var ordered = items
                .OrderBy(i => Rank(ProjectApi.Str(i, "verdict")))
                .ThenBy(i => Convert.ToInt32(i["score"]))
                .ToList();

            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "project", projectName }, { "file", fileName },
                { "language", lang }, { "srcLanguage", srcLang }, { "domain", domain },
                { "segments", plan.Translated + plan.Untranslated },
                { "checked", checkedCount },
                { "untranslated", plan.Untranslated },
                { "noiseSkipped", plan.Skipped },
                { "duplicatesCollapsed", plan.Deduped },
                { "count", ordered.Count },
                { "summary", summary },
                { "items", ordered },
                { "csv", BuildCsv(ordered) },
                { "message", BuildMessage(ordered.Count, summary) },
            });
        }

        /// <summary>取消时的统一返回：499 + 可读消息（调用方按 IsCancellationRequested 自行处理）。</summary>
        private static ApiResult Cancelled()
        {
            return ApiResult.Json(499, ProjectApi.Error("已取消"));
        }

        private static ApiResult EmptyResult(string projectName, string fileName, string lang, string srcLang, BtqaPlan plan)
        {
            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "project", projectName }, { "file", fileName },
                { "language", lang }, { "srcLanguage", srcLang },
                { "count", 0 },
                { "untranslated", plan.Untranslated },
                { "noiseSkipped", plan.Skipped },
                { "duplicatesCollapsed", plan.Deduped },
                { "summary", new Dictionary<string, long>() },
                { "items", new List<Dictionary<string, object>>() },
                { "csv", CsvHeader },
                { "message", "预筛后没有需要回译校验的段落（未译 " + plan.Untranslated
                    + "、噪声跳过 " + plan.Skipped + "、重复去重 " + plan.Deduped + "）。" },
            });
        }

        // ====================================================================
        // 本地预筛
        // ====================================================================

        private sealed class BtqaPlan
        {
            public int Translated;
            public int Untranslated;
            public int Skipped;
            public int Deduped;
            public readonly List<BilingualSegment> Unique = new List<BilingualSegment>();
            public readonly Dictionary<string, int> Repeat = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Dictionary<string, object>> SkippedSamples = new List<Dictionary<string, object>>();
        }

        private static BtqaPlan BuildPlan(List<BilingualSegment> rows)
        {
            var plan = new BtqaPlan();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in rows)
            {
                if (string.IsNullOrWhiteSpace(seg.Target)) { plan.Untranslated++; continue; }
                plan.Translated++;

                var cat = ProjectApi.TriageCategory(seg.Source, out var reason);
                if (cat == "skip")
                {
                    plan.Skipped++;
                    if (plan.SkippedSamples.Count < 5)
                        plan.SkippedSamples.Add(new Dictionary<string, object>
                        {
                            { "id", seg.Id }, { "source", ProjectApi.Clip(seg.Source) }, { "reason", reason },
                        });
                    continue;
                }

                var key = PairKey(seg);
                plan.Repeat[key] = plan.Repeat.TryGetValue(key, out var n) ? n + 1 : 1;
                if (!seen.Add(key)) { plan.Deduped++; continue; }
                plan.Unique.Add(seg);
            }
            return plan;
        }

        private static string PairKey(BilingualSegment seg)
        {
            return ProjectApi.NormKey(seg.Source) + "\u0001" + ProjectApi.NormKey(seg.Target);
        }

        private static int RepeatOf(BilingualSegment r, BtqaPlan plan)
        {
            return plan.Repeat.TryGetValue(PairKey(r), out var n) && n > 0 ? n : 1;
        }

        private static int EstimateCalls(int count, int batchSize)
        {
            if (count <= 0) return 0;
            return ((count + batchSize - 1) / batchSize) * 2;   // 回译 + 判官
        }

        private static string BuildPlanSummary(int total, BtqaPlan plan, int estCalls)
        {
            return "共 " + total + " 段：已译 " + plan.Translated + "、未译 " + plan.Untranslated
                + "；噪声跳过 " + plan.Skipped + "、重复去重 " + plan.Deduped
                + "；实际需回译校验 " + plan.Unique.Count + " 段，预计 " + estCalls + " 次 LLM 调用（回译+判官各半）。";
        }

        private static void Report(Action<Dictionary<string, object>> progress, string stage,
            int batch, int batches, int done)
        {
            if (progress == null) return;
            progress(new Dictionary<string, object>
            {
                { "stage", stage }, { "batch", batch }, { "batches", batches },
                { "done", done }, { "status", "running" },
            });
        }

        // ====================================================================
        // 合并与汇总
        // ====================================================================

        private static Dictionary<string, object> MergeItem(BilingualSegment r, string back,
            Dictionary<string, object> judge, BtqaPlan plan, int minScore)
        {
            var dice = Dice(r.Source, back);
            var llmScore = judge != null && judge.ContainsKey("score") ? Convert.ToInt32(judge["score"]) : 100;
            var verdict = judge != null && judge.ContainsKey("verdict") ? Convert.ToString(judge["verdict"]) : "ok";
            if (string.IsNullOrWhiteSpace(verdict)) verdict = "ok";

            string type = null, reason = null, suggestion = null;
            if (judge != null)
            {
                if (judge.ContainsKey("suggestion") && !string.IsNullOrWhiteSpace(Convert.ToString(judge["suggestion"])))
                    suggestion = Convert.ToString(judge["suggestion"]);

                var issues = judge.ContainsKey("issues") ? AsDictList(judge["issues"]) : null;
                if (issues != null && issues.Count > 0)
                {
                    var first = issues[0];
                    type = first.ContainsKey("type") ? Convert.ToString(first["type"]) : null;
                    reason = first.ContainsKey("reason") ? Convert.ToString(first["reason"]) : null;
                    if (string.IsNullOrWhiteSpace(suggestion))
                        foreach (var iss in issues)
                            if (iss.ContainsKey("suggestion") && !string.IsNullOrWhiteSpace(Convert.ToString(iss["suggestion"])))
                            { suggestion = Convert.ToString(iss["suggestion"]); break; }
                }
            }

            // 确定性收敛度兜底：判官放过、但回译与原文差异过大 → 降级为需复核
            if (verdict == "ok" && dice < minScore)
            {
                verdict = "amber";
                type = "回译收敛偏低";
                reason = "回译与原文相似度仅 " + dice + "%，疑似语义漂移，建议人工复核。";
            }

            return new Dictionary<string, object>
            {
                { "id", r.Id }, { "source", r.Source }, { "target", r.Target }, { "back", back },
                { "dice", dice }, { "llmScore", llmScore }, { "score", (llmScore + dice) / 2 },
                { "verdict", verdict }, { "type", type }, { "reason", reason }, { "suggestion", suggestion },
                { "repeat", RepeatOf(r, plan) },
            };
        }

        private static Dictionary<string, object> FailedItem(BilingualSegment r, string back,
            string error, BtqaPlan plan)
        {
            object dice = back == null ? null : (object)Dice(r.Source, back);
            return new Dictionary<string, object>
            {
                { "id", r.Id }, { "source", r.Source }, { "target", r.Target }, { "back", back },
                { "dice", dice }, { "llmScore", 0 }, { "score", 0 }, { "verdict", "error" },
                { "type", "调用失败" }, { "reason", error }, { "suggestion", null },
                { "repeat", RepeatOf(r, plan) },
            };
        }

        private static int Rank(string verdict)
        {
            switch (verdict)
            {
                case "red": return 0;
                case "amber": return 1;
                case "error": return 2;
                default: return 3;
            }
        }

        private static long Count(Dictionary<string, long> map, string key)
        {
            return map.TryGetValue(key, out var v) ? v : 0;
        }

        private static string BuildMessage(int count, Dictionary<string, long> summary)
        {
            var msg = "回译语义校验完成：共校验 " + count + " 段。 红(语义不符)=" + Count(summary, "red")
                + " 黄(需复核)=" + Count(summary, "amber") + " 通过=" + Count(summary, "ok") + "。";
            if (Count(summary, "error") > 0) msg += " 调用失败=" + Count(summary, "error") + "（可重试）。";
            return msg;
        }

        private static string BuildCsv(List<Dictionary<string, object>> items)
        {
            var sb = new StringBuilder();
            sb.Append('\ufeff');
            sb.Append(CsvHeader);
            foreach (var it in items)
                sb.Append(BilingualParser.Csv(ProjectApi.Str(it, "id"))).Append(',')
                  .Append(BilingualParser.Csv(Convert.ToString(it["dice"]))).Append(',')
                  .Append(BilingualParser.Csv(Convert.ToString(it["llmScore"]))).Append(',')
                  .Append(BilingualParser.Csv(Convert.ToString(it["score"]))).Append(',')
                  .Append(BilingualParser.Csv(ProjectApi.Str(it, "verdict"))).Append(',')
                  .Append(BilingualParser.Csv(ProjectApi.Str(it, "type"))).Append(',')
                  .Append(BilingualParser.Csv(ProjectApi.Str(it, "reason"))).Append(',')
                  .Append(BilingualParser.Csv(ProjectApi.Str(it, "suggestion"))).Append(',')
                  .Append(BilingualParser.Csv(ProjectApi.Str(it, "source"))).Append(',')
                  .Append(BilingualParser.Csv(ProjectApi.Str(it, "target"))).Append(',')
                  .Append(BilingualParser.Csv(ProjectApi.Str(it, "back")))
                  .Append("\r\n");
            return sb.ToString();
        }

        private static List<Dictionary<string, object>> AsDictList(object value)
        {
            var result = new List<Dictionary<string, object>>();
            var seq = value as System.Collections.IEnumerable;
            if (seq == null || value is string) return result;
            foreach (var o in seq)
                if (o is Dictionary<string, object> d) result.Add(d);
            return result;
        }

        // ====================================================================
        // 确定性收敛度：字符 bigram Dice（0..100，纯本地、可复现、零成本）
        // ====================================================================

        private static int Dice(string a, string b)
        {
            var x = NormalizeForSim(a);
            var y = NormalizeForSim(b);
            if (x.Length == 0 && y.Length == 0) return 100;
            if (x.Length == 0 || y.Length == 0) return 0;
            if (x.Length < 2 || y.Length < 2) return DiceOf(CharCounts(x), CharCounts(y));
            return DiceOf(Bigrams(x), Bigrams(y));
        }

        private static string NormalizeForSim(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static Dictionary<string, int> Bigrams(string s)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < s.Length; i++)
            {
                var g = s.Substring(i, 2);
                d[g] = d.TryGetValue(g, out var c) ? c + 1 : 1;
            }
            return d;
        }

        private static Dictionary<string, int> CharCounts(string s)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in s)
            {
                var g = c.ToString();
                d[g] = d.TryGetValue(g, out var n) ? n + 1 : 1;
            }
            return d;
        }

        private static int DiceOf(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            long ta = 0; foreach (var kv in a) ta += kv.Value;
            long tb = 0; foreach (var kv in b) tb += kv.Value;
            if (ta + tb == 0) return 0;
            long overlap = 0;
            foreach (var kv in a)
                if (b.TryGetValue(kv.Key, out var c)) overlap += Math.Min(kv.Value, c);
            return (int)Math.Round(200.0 * overlap / (ta + tb));
        }

        // ====================================================================
        // 提示词
        // ====================================================================

        private static string BackPrompt(string srcLang, string tgtLang)
        {
            var s = string.IsNullOrEmpty(srcLang) ? "源语言" : srcLang;
            var t = string.IsNullOrEmpty(tgtLang) ? "目标语言" : tgtLang;
            return "你是专业翻译。把下面这批 " + t + " 译文逐条准确回译成 " + s + "。\n" +
                "只返回 JSON 数组，不要 markdown、不要任何额外文字。\n" +
                "要求：忠实还原语义；不润色、不补充、不省略；保持数字、专名、占位符(如 [[1]])原样。\n" +
                "输入数组元素含 {id, text}(text 为 " + t + " 译文)。返回形如 [{\"id\":\"1\",\"back\":\"回译后的" + s + "文本\"}]。";
        }

        private static string JudgePrompt(string tgtLang, string termsText)
        {
            var t = string.IsNullOrEmpty(tgtLang) ? "未知" : tgtLang;
            var terms = string.IsNullOrWhiteSpace(termsText) ? "（无可用术语表）" : termsText;
            return "你是专业翻译质检。下面每条给出原文(source)、译文(target)、以及译文被独立回译后的文本(back)。\n" +
                "原理：若译文忠实，back 应与 source 语义一致；差异越大越可能漏译/增译/错译/语义漂移。据此判定。\n" +
                "目标语言: " + t + "。\n" +
                "术语表(严格: 出现 from 必须用 to，违反判red):\n" + terms + "\n" +
                "判定规则:\n" +
                "- back 与 source 语义明显不符(漏译/增译/错译/张冠李戴) -> verdict=\"red\"\n" +
                "- 语义基本一致但个别词/数字/单位/指代有偏差，或回译收敛偏低 -> verdict=\"amber\"\n" +
                "- 语义一致无问题 -> verdict=\"ok\"\n" +
                "- 一律给 score(0-100，反映语义保真度)、issues 数组(元素: type, reason, suggestion)。red/amber 必须给 suggestion(完整修正后的目标译文)。\n" +
                "- 保持源文占位符(如 [[1]])原位原样。\n" +
                "输入数组元素含 {id, source, back, target}。返回形如 [{\"id\":\"1\",\"score\":80,\"verdict\":\"amber\",\"issues\":[{\"type\":\"语义漂移\",\"reason\":\"...\",\"suggestion\":\"...\"}]}]。";
        }
    }
}
