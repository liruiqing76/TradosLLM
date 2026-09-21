using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sdl.Core.Globalization;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using TradosToolkit.Glossaries;

namespace TradosToolkit.Server
{
    /// <summary>
    /// HTTP 路由 → Studio 项目自动化。所有 Studio 对象模型操作 marshal 回 UI 线程。
    /// </summary>
    public static class ProjectApi
    {
        private static readonly Dictionary<string, string> TaskTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "pretranslate", AutomaticTaskTemplateIds.PreTranslateFiles },
            { "analyze", AutomaticTaskTemplateIds.AnalyzeFiles },
            { "wordcount", AutomaticTaskTemplateIds.WordCount },
            { "target", AutomaticTaskTemplateIds.GenerateTargetTranslations },
            { "export", AutomaticTaskTemplateIds.ExportFiles },
            { "updatetm", AutomaticTaskTemplateIds.UpdateMainTranslationMemories },
        };

        public static ApiResult Handle(string method, string path, Dictionary<string, string> query, string body, string key, string token)
        {
            if (path == "/" || path == "/status.html")
                return StatusPage.Build();

            if (path == "/api/status")
                return Status();

            if (string.IsNullOrEmpty(token) || !string.Equals(key, token, StringComparison.Ordinal))
                return ApiResult.Json(401, Error("缺少或错误的 X-Api-Key（令牌见 %AppData%\\TradosToolkit\\api.token）"));

            switch (path)
            {
                case "/api/templates":
                    return Templates();
                case "/api/projects":
                    return method == "POST" ? CreateProject(body) : ListProjects();
                case "/api/project/files":
                    return ProjectFiles(query);
                case "/api/project/task":
                    return RunTask(method, query, body);
                case "/api/tasks":
                    return ApiResult.Json(200, TaskRegistry.Snapshot(null));
                case "/api/task":
                    return TaskStatus(query);
                case "/api/requests":
                    return ApiResult.Json(200, RequestTracker.Snapshot());
                case "/api/project/segments":
                    return Segments(query);
                case "/api/project/report":
                    return Report(query);
                case "/api/project/pretranslate":
                    return PreTranslate(method, query, body);
                case "/api/project/tmfiles":
                    return TmFiles(query);
                case "/api/project/package":
                    return Package(method, query, body);
                case "/api/file":
                    return DownloadFile(query);
                case "/api/health":
                    return HealthProbe.Build();
                case "/api/glossary/backfill":
                    return BackfillGlossary(body);
                default:
                    return ApiResult.Json(404, Error("未知端点 " + path));
            }
        }

        private static ApiResult Status()
        {
            var server = ToolkitApiServer.Instance;
            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "product", "TradosToolkit" },
                { "studio", Environment.Is64BitProcess ? "x64" : "x86" },
                { "version", typeof(ProjectApi).Assembly.GetName().Version.ToString() },
                { "port", server.Port },
                { "startedAt", server.StartedAt },
                { "listening", server.IsListening },
                { "uptimeSeconds", server.StartedAt == default(DateTime) ? 0 : (long)(DateTime.Now - server.StartedAt).TotalSeconds },
                { "totalRequests", RequestTracker.Total },
                { "runningTasks", TaskRegistry.RunningCount },
            });
        }

        private static ApiResult TaskStatus(Dictionary<string, string> query)
        {
            string id;
            if (!query.TryGetValue("id", out id) || string.IsNullOrEmpty(id))
                return ApiResult.Json(400, Error("缺少 query 参数 id"));
            var rows = TaskRegistry.Snapshot(id);
            return rows.Count == 0 ? ApiResult.Json(404, Error("无此任务 " + id)) : ApiResult.Json(200, rows[0]);
        }

        private static ApiResult Templates()
        {
            return OnUi(() =>
            {
                var list = ProjectsController().GetProjectTemplates()
                    .Select(t => (Dictionary<string, object>)new Dictionary<string, object>
                    {
                        { "name", t.Name },
                        { "uri", t.Uri == null ? null : t.Uri.LocalPath },
                    })
                    .ToList();
                return ApiResult.Json(200, list);
            });
        }

        private static ApiResult ListProjects()
        {
            return OnUi(() =>
            {
                var list = ProjectsController().GetProjects()
                    .Select(p => DescribeProject(p.GetProjectInfo()))
                    .ToList();
                return ApiResult.Json(200, list);
            });
        }

        private static Dictionary<string, object> DescribeProject(ProjectInfo info)
        {
            return new Dictionary<string, object>
            {
                { "id", info.Id },
                { "name", info.Name },
                { "projectPath", info.Uri == null ? null : info.Uri.LocalPath },
                { "folder", info.LocalProjectFolder },
                { "sourceLang", info.SourceLanguage == null ? null : info.SourceLanguage.IsoAbbreviation },
                { "targetLangs", (info.TargetLanguages ?? new Language[0]).Select(l => l.IsoAbbreviation).ToList() },
                { "isCompleted", info.IsCompleted },
            };
        }

        private static ApiResult CreateProject(string body)
        {
            var request = ParseBody(body);
            var name = Str(request, "name");
            var folder = Str(request, "folder");
            var sourceLang = Str(request, "sourceLang");
            var targetLangs = StrList(request, "targetLangs");
            var files = StrList(request, "files");

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(folder)
                || string.IsNullOrEmpty(sourceLang) || targetLangs.Count == 0 || files.Count == 0)
                return ApiResult.Json(400, Error("必填: name folder sourceLang targetLangs[] files[]"));
            if (!files.All(File.Exists))
                return ApiResult.Json(400, Error("存在不存在的源文件"));

            return OnUi(() =>
            {
                Directory.CreateDirectory(folder);

                ProjectTemplateReference template;
                try
                {
                    template = ResolveTemplate(request, sourceLang, targetLangs);
                }
                catch (Exception e)
                {
                    return ApiResult.Json(400, Error("模板解析失败: " + e.Message));
                }

                var info = new ProjectInfo
                {
                    Name = name,
                    Description = Str(request, "description") ?? "created by TradosToolkit api",
                    SourceLanguage = new Language(sourceLang),
                    TargetLanguages = targetLangs.Select(l => new Language(l)).ToArray(),
                    LocalProjectFolder = folder,
                };

                var project = new FileBasedProject(info, template);
                var added = project.AddFiles(files.ToArray());
                project.SetFileRole(added.Select(f => f.Id).ToArray(), FileRole.Translatable);
                project.Save();

                var sdlp = project.FilePath;
                if (Bool(request, "openInStudio"))
                    ProjectsController().Add(sdlp);

                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "projectPath", sdlp },
                    { "id", project.GetProjectInfo().Id },
                });
            });
        }

        private static ProjectTemplateReference ResolveTemplate(
            Dictionary<string, object> request, string sourceLang, List<string> targetLangs)
        {
            var given = Str(request, "template");
            if (!string.IsNullOrEmpty(given))
            {
                if (!File.Exists(given))
                    throw new FileNotFoundException("模板文件不存在", given);
                return new ProjectTemplateReference(given);
            }

            var templates = ProjectsController().GetProjectTemplates().ToList();
            if (templates.Count == 0)
                throw new InvalidOperationException("本机没有项目模板，请传 template 路径或先在 Studio 制作模板");

            var pairMark = sourceLang + "-" + targetLangs[0];
            var match = templates.FirstOrDefault(t =>
                              (t.Name ?? string.Empty).IndexOf(pairMark, StringComparison.OrdinalIgnoreCase) >= 0)
                          ?? templates.First();
            if (match.Uri == null)
                throw new InvalidOperationException("默认模板没有本地路径: " + match.Name);
            return new ProjectTemplateReference(match.Uri.LocalPath);
        }

        private static ApiResult ProjectFiles(Dictionary<string, string> query)
        {
            return WithProject(query, (project, info) =>
            {
                var sources = project.GetSourceLanguageFiles().Select(f => DescribeFile(f, info)).ToList();
                var targets = project.GetTargetLanguageFiles().Select(f => DescribeFile(f, info)).ToList();
                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "source", sources },
                    { "target", targets },
                });
            });
        }

        private static Dictionary<string, object> DescribeFile(ProjectFile file, ProjectInfo info)
        {
            return new Dictionary<string, object>
            {
                { "id", file.Id },
                { "name", file.Name },
                { "role", file.Role.ToString() },
                { "isSource", file.IsSource },
                { "language", file.Language == null ? null : file.Language.IsoAbbreviation },
                { "path", file.LocalFilePath },
                { "bilingualPath", file.IsSource ? null : ResolveBilingualPath(file, info) },
            };
        }

        /// <summary>
        /// POST /api/project/task?path=...&task=pretranslate|analyze|...&files=id1,id2(可选)&async=1(可选)
        /// body 可选 {"providerUri":"tradostoolkit://...","providerState":""}：
        /// 预翻译前把该提供程序写入项目所有目标语言的 TM 配置。
        /// async=1 立即返回 202 {taskId}，进度查 GET /api/task?id=；缺省仍同步阻塞到完成。
        /// </summary>
        private static ApiResult RunTask(string method, Dictionary<string, string> query, string body)
        {
            if (method != "POST")
                return ApiResult.Json(405, Error("task 端点需 POST"));

            var taskKey = query.TryGetValue("task", out var t) ? t : null;
            if (taskKey == null || !TaskTemplates.TryGetValue(taskKey, out var templateId))
                return ApiResult.Json(400, Error("task 需为: " + string.Join("|", TaskTemplates.Keys)));

            var request = ParseBody(body);
            Func<ApiResult> run = () => ExecuteTask(taskKey, templateId, query, request);

            if (query.TryGetValue("async", out var async) && async == "1")
            {
                var path = query.TryGetValue("path", out var p) ? p : null;
                var st = TaskRegistry.Start(taskKey, path, run);
                return ApiResult.Json(202, new Dictionary<string, object>
                {
                    { "taskId", st.Id }, { "task", taskKey }, { "status", "running" },
                });
            }
            return run();
        }

        private static ApiResult ExecuteTask(string taskKey, string templateId,
            Dictionary<string, string> query, Dictionary<string, object> request)
        {
            var providerUri = Str(request, "providerUri");

            return WithProject(query, (project, _) =>
            {
                Guid[] fileIds;
                if (query.TryGetValue("files", out var ids) && !string.IsNullOrEmpty(ids))
                    fileIds = ids.Split(',').Select(Guid.Parse).ToArray();
                else
                    fileIds = project.GetTargetLanguageFiles().Select(f => f.Id).ToArray();

                if (fileIds.Length == 0)
                    return ApiResult.Json(400, Error("项目没有目标文件"));

                if (!string.IsNullOrEmpty(providerUri))
                {
                    var config = new TranslationProviderConfiguration
                    {
                        Entries = new List<TranslationProviderCascadeEntry>
                        {
                            new TranslationProviderCascadeEntry(
                                new TranslationProviderReference(new Uri(providerUri), Str(request, "providerState") ?? string.Empty, true),
                                false, true, false)
                        },
                        StopSearchingWhenResultsFound = true,
                    };
                    foreach (var lang in project.GetProjectInfo().TargetLanguages)
                        project.UpdateTranslationProviderConfiguration(lang, config);
                }

                var task = project.RunAutomaticTask(fileIds, templateId);
                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "task", taskKey },
                    { "messages", (task.Messages ?? new ExecutionMessage[0]).Select(MessageText).ToList() },
                    { "reports", (task.Reports ?? new TaskReport[0]).Select(r => new Dictionary<string, object>
                        { { "id", r.Id }, { "name", r.Name } }).ToList() },
                });
            });
        }

        private static string MessageText(object message)
        {
            var text = message.GetType().GetProperty("Message");
            return (text == null ? message.ToString() : text.GetValue(message, null) as string) ?? string.Empty;
        }

        /// <summary>
        /// POST /api/project/pretranslate?path=...&amp;files=id1,id2(可选)
        /// body 可选 {"providerUri":"tradostoolkit://...","providerState":""}
        /// 远程预翻译调度：等价于 /api/project/task?task=pretranslate&amp;async=1，
        /// 固定走后台异步（TaskRegistry），立即返回 202 {taskId}，进度查 GET /api/task?id=。
        /// </summary>
        private static ApiResult PreTranslate(string method, Dictionary<string, string> query, string body)
        {
            if (method != "POST")
                return ApiResult.Json(405, Error("pretranslate 端点需 POST"));

            var request = ParseBody(body);
            Func<ApiResult> run = () => ExecuteTask("pretranslate", TaskTemplates["pretranslate"], query, request);
            var path = query.TryGetValue("path", out var p) ? p : null;
            var st = TaskRegistry.Start("pretranslate", path, run);
            return ApiResult.Json(202, new Dictionary<string, object>
            {
                { "taskId", st.Id }, { "task", "pretranslate" }, { "status", "running" },
            });
        }

        private static ApiResult Report(Dictionary<string, string> query)
        {
            return WithProject(query, (project, info) =>
            {
                var stats = project.GetProjectStatistics();
                var tls = stats == null ? new TargetLanguageStatistics[0] : stats.TargetLanguageStatistics;
                var perTarget = tls.Select(MapStatistics).ToList();

                // 各匹配等级跨语言汇总（报价常用总词数/总句段数）
                var sum = new Dictionary<string, long>(); // key = words|segments|characters
                foreach (var target in tls)
                {
                    var a = target.AnalysisStatistics;
                    if (a == null) continue;
                    Add(sum, a.Total); Add(sum, a.Perfect); Add(sum, a.Exact);
                    Add(sum, a.InContextExact); Add(sum, a.New); Add(sum, a.Repetitions);
                }
                var total = new Dictionary<string, object>
                {
                    { "words", sumDict(sum, "words") },
                    { "segments", sumDict(sum, "segments") },
                    { "characters", sumDict(sum, "characters") },
                    { "targetLangs", perTarget.Count },
                };

                var format = query.TryGetValue("format", out var fmt) ? fmt : "json";
                if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                {
                    var csv = new System.Text.StringBuilder();
                    csv.Append('\ufeff');
                    csv.Append("targetLang,level,words,segments,characters\r\n");
                    foreach (var t in tls)
                    {
                        var a = t.AnalysisStatistics;
                        if (a == null) continue;
                        var lang = t.TargetLanguage == null ? "" : t.TargetLanguage.IsoAbbreviation;
                        Row(csv, lang, "Total", a.Total);
                        Row(csv, lang, "Perfect", a.Perfect);
                        Row(csv, lang, "Exact", a.Exact);
                        Row(csv, lang, "InContextExact", a.InContextExact);
                        Row(csv, lang, "New", a.New);
                        Row(csv, lang, "Repetitions", a.Repetitions);
                    }
                    var name = SanitizeFileName(info.Name + "_wordcount") + ".csv";
                    return ApiResult.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", name);
                }

                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "project", info.Name },
                    { "total", total },
                    { "targets", perTarget },
                });
            });
        }

        private static void Row(System.Text.StringBuilder csv, string lang, string level, CountData c)
        {
            csv.Append(BilingualParser.Csv(lang)).Append(',').Append(BilingualParser.Csv(level)).Append(',')
               .Append(c == null ? "0" : c.Words.ToString()).Append(',')
               .Append(c == null ? "0" : c.Segments.ToString()).Append(',')
               .Append(c == null ? "0" : c.Characters.ToString()).Append("\r\n");
        }

        private static void Add(Dictionary<string, long> map, CountData c)
        {
            if (c == null) return;
            Add(map, "words", c.Words); Add(map, "segments", c.Segments); Add(map, "characters", c.Characters);
        }

        private static void Add(Dictionary<string, long> map, string key, int value)
        {
            if (map.TryGetValue(key, out var cur)) map[key] = cur + value; else map[key] = value;
        }

        private static long sumDict(Dictionary<string, long> map, string key)
        {
            return map.TryGetValue(key, out var v) ? v : 0;
        }

        private static Dictionary<string, object> MapStatistics(TargetLanguageStatistics target)
        {
            var a = target.AnalysisStatistics;
            return new Dictionary<string, object>
            {
                { "targetLang", target.TargetLanguage == null ? null : target.TargetLanguage.IsoAbbreviation },
                { "analysis", a == null ? null : new Dictionary<string, object>
                    {
                        { "total", MapCount(a.Total) },
                        { "perfect", MapCount(a.Perfect) },
                        { "exact", MapCount(a.Exact) },
                        { "inContextExact", MapCount(a.InContextExact) },
                        { "new", MapCount(a.New) },
                        { "repetitions", MapCount(a.Repetitions) },
                    }
                },
            };
        }

        private static Dictionary<string, object> MapCount(CountData count)
        {
            return count == null ? null : new Dictionary<string, object>
            {
                { "words", count.Words },
                { "segments", count.Segments },
                { "characters", count.Characters },
            };
        }

        private static ApiResult TmFiles(Dictionary<string, string> query)
        {
            return WithProject(query, (project, info) =>
            {
                var files = Directory.EnumerateFiles(info.LocalProjectFolder, "*.sdltm", SearchOption.AllDirectories)
                    .Select(p => new Dictionary<string, object>
                    {
                        { "path", p },
                        { "size", new FileInfo(p).Length },
                    })
                    .ToList();
                return ApiResult.Json(200, files);
            });
        }

        /// <summary>
        /// POST /api/project/package?path=...  body 可选 {"out":"d:\\x.sdlppx","packageName":"..","comment":".."}
        /// 建交付任务 → 打包 sdlppx（含主 TM 与分析结果）→ 保存到项目目录（或 out）。
        /// </summary>
        private static ApiResult Package(string method, Dictionary<string, string> query, string body)
        {
            if (method != "POST")
                return ApiResult.Json(405, Error("package 端点需 POST"));

            var request = ParseBody(body);
            return WithProject(query, (project, info) =>
            {
                var name = Str(request, "packageName") ?? SanitizeFileName(info.Name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outPath = Str(request, "out");
                if (string.IsNullOrEmpty(outPath))
                    outPath = Path.Combine(info.LocalProjectFolder, name + ".sdlppx");

                var fileIds = project.GetTargetLanguageFiles().Select(f => f.Id).ToArray();
                if (fileIds.Length == 0)
                    return ApiResult.Json(400, Error("项目没有目标文件"));

                var manualTask = project.CreateManualTask(
                    "TradosToolkit api delivery", Environment.UserName, DateTime.Now.AddDays(7), fileIds);
                var options = new ProjectPackageCreationOptions
                {
                    IncludeMainTranslationMemories = true,
                    IncludeReports = true,
                    RecomputeAnalysisStatistics = true,
                };
                var package = project.CreateProjectPackage(manualTask.Id, name, Str(request, "comment") ?? "created by TradosToolkit api", options);
                project.SavePackageAs(package.Task.Id, outPath);

                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "packagePath", outPath },
                    { "manualTaskId", package.Task.Id },
                });
            });
        }

        private static ApiResult DownloadFile(Dictionary<string, string> query)
        {
            if (!query.TryGetValue("path", out var path) || !File.Exists(path))
                return ApiResult.Json(404, Error("文件不存在"));

            var bytes = File.ReadAllBytes(path);
            return ApiResult.File(bytes, "application/octet-stream", Path.GetFileName(path));
        }

        /// <summary>
        /// POST /api/glossary/backfill   body: {"srcLang":"zh-CN","tgtLang":"en-US","bilingualPath":"...sdlxliff"[, "max":200]}
        ///   或 body: {"srcLang","tgtLang","segments":[{"source":"..","target":".."},...]}
        /// 术语库自动回填：从已确认(译前/已审)双语段，用统计共现法反抽高频术语对，写入译前术语库 kind=pre。
        /// 纯本地、零 LLM 成本、确定性输出；返回抽取到的术语对列表供人工复核。写入量上限 max（缺省 200）。
        /// </summary>
        private static ApiResult BackfillGlossary(string body)
        {
            var request = ParseBody(body);
            var srcLang = Str(request, "srcLang") ?? "";
            var tgtLang = Str(request, "tgtLang") ?? "";
            if (string.IsNullOrWhiteSpace(srcLang) || string.IsNullOrWhiteSpace(tgtLang))
                return ApiResult.Json(400, Error("必填: srcLang tgtLang"));

            var max = 200;
            if (request.TryGetValue("max", out var mv))
            {
                try { max = Math.Max(20, Math.Min(1000, Convert.ToInt32(mv))); }
                catch { max = 200; }
            }

            // —— 收集已确认双语段 (src,tgt) ——
            var pairs = new List<KeyValuePair<string, string>>();
            var bilingualPath = Str(request, "bilingualPath");
            if (!string.IsNullOrWhiteSpace(bilingualPath))
            {
                if (!File.Exists(bilingualPath))
                    return ApiResult.Json(404, Error("双语参照文件不存在: " + bilingualPath));
                foreach (var seg in BilingualParser.Parse(bilingualPath))
                    if (IsConfirmed(seg) && !string.IsNullOrWhiteSpace(seg.Source) && !string.IsNullOrWhiteSpace(seg.Target))
                        pairs.Add(new KeyValuePair<string, string>(seg.Source.Trim(), seg.Target.Trim()));
            }
            else
            {
                var segs = request.TryGetValue("segments", out var sv) ? sv as System.Collections.IEnumerable : null;
                if (segs != null)
                {
                    foreach (var o in segs)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        var s = d.TryGetValue("source", out var ss) ? ss as string : null;
                        var t = d.TryGetValue("target", out var tt) ? tt as string : null;
                        if (!string.IsNullOrWhiteSpace(s) && !string.IsNullOrWhiteSpace(t))
                            pairs.Add(new KeyValuePair<string, string>(s.Trim(), t.Trim()));
                    }
                }
            }

            if (pairs.Count < 2)
                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "processed", pairs.Count }, { "extracted", 0 }, { "reason", "已确认段不足 2 条" },
                    { "terms", new List<object>() },
                });

            var terms = ExtractGlossaryTerms(pairs, max);
            var db = new GlossaryDb();
            var written = new List<Dictionary<string, object>>();
            foreach (var t in terms)
            {
                db.SaveTerm(GlossaryDb.KindPre, srcLang, tgtLang, new GlossaryEntry { From = t.Key, To = t.Value });
                written.Add(new Dictionary<string, object> { { "term", t.Key }, { "translation", t.Value } });
            }

            var msg = terms.Count == 0
                ? "未抽取到高频稳定的术语对（请确认双语段已含一致的译文）"
                : "已写入译前术语库 " + terms.Count + " 对，建议在术语管理界面复核后再使用";
            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "processed", pairs.Count }, { "extracted", terms.Count }, { "max", max },
                { "kind", GlossaryDb.KindPre }, { "languagePair", srcLang + "->" + tgtLang },
                { "message", msg }, { "terms", written },
            });
        }

        /// <summary>段 conf 属 Decision 是否已确认（Approved / Translation / Translated）。</summary>
        private static bool IsConfirmed(BilingualSegment seg)
        {
            var s = seg.Status ?? string.Empty;
            return s.IndexOf("Approved", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Translation", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("Translated", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 统计共现术语抽取：源/目标两侧各自抽高频连续短语，再按"出现在同一批已确认段的共现覆盖度"配对。
        /// 只保留覆盖度≥0.5 且共现段数≥2 的稳定对，按 覆盖度×支持度 排序取前 max。确定性、无 LLM 成本。
        /// </summary>
        private static List<KeyValuePair<string, string>> ExtractGlossaryTerms(
            List<KeyValuePair<string, string>> pairs, int max)
        {
            const double MinCoverage = 0.5;
            const int MinCoSup = 2;

            var src = new Dictionary<string, HashSet<int>>(); // phrase -> segment indices
            var tgt = new Dictionary<string, HashSet<int>>();
            for (int i = 0; i < pairs.Count; i++)
            {
                IndexPhrases(src, pairs[i].Key, i);
                IndexPhrases(tgt, pairs[i].Value, i);
            }

            // 只考虑两侧都至少出现 2 次的短语
            var srcFreq = src.Where(x => x.Value.Count >= MinCoSup).OrderByDescending(x => x.Value.Count).ToList();
            var tgtFreq = tgt.Where(x => x.Value.Count >= MinCoSup).ToList();
            var tgtBySeg = new Dictionary<int, List<string>>(); // 段 i -> 高频目标短语
            foreach (var kv in tgtFreq)
                foreach (var i in kv.Value)
                {
                    if (!tgtBySeg.TryGetValue(i, out var l)) { l = new List<string>(); tgtBySeg[i] = l; }
                    l.Add(kv.Key);
                }

            var scored = new List<Tuple<double, string, string>>();
            foreach (var skv in srcFreq)
            {
                var phrase = skv.Key;
                var segs = skv.Value;
                string bestT = null;
                double bestScore = 0;
                var coCounts = new Dictionary<string, int>();
                foreach (var i in segs)
                    if (tgtBySeg.TryGetValue(i, out var tl))
                        foreach (var t in tl)
                            coCounts[t] = coCounts.ContainsKey(t) ? coCounts[t] + 1 : 1;

                foreach (var c in coCounts)
                {
                    if (c.Value < MinCoSup) continue;
                    double coverage = (double)c.Value / segs.Count;
                    if (coverage < MinCoverage) continue;
                    // 更高覆盖率优先，同覆盖率取更高共现段数
                    if (coverage > bestScore ||
                        (coverage == bestScore && (bestT == null || c.Value > coCounts[bestT])))
                    {
                        bestT = c.Key; bestScore = coverage;
                    }
                }
                if (bestT != null)
                    scored.Add(Tuple.Create(bestScore * segs.Count, phrase, bestT));
            }

            // 同一目标短语避免被多个源短语重复占用：按分排序取最高者
            var usedT = new HashSet<string>();
            var result = new List<KeyValuePair<string, string>>();
            foreach (var s in scored.OrderByDescending(x => x.Item1))
            {
                if (result.Count >= max) break;
                if (usedT.Contains(s.Item3)) continue;
                if (string.Equals(s.Item2, s.Item3, StringComparison.Ordinal)) continue; // 两侧相同(品牌/代号)价值低
                usedT.Add(s.Item3);
                result.Add(new KeyValuePair<string, string>(s.Item2, s.Item3));
            }
            return result;
        }

        /// <summary>把一段文本切成候选连续短语（拉丁词 n-gram / 中文连续 n-gram），去标点、折空白，句内去重。</summary>
        private static void IndexPhrases(Dictionary<string, HashSet<int>> index, string text, int segIndex)
        {
            foreach (var phrase in Phrases(text))
            {
                if (!index.TryGetValue(phrase, out var set))
                {
                    set = new HashSet<int>();
                    index[phrase] = set;
                }
                set.Add(segIndex);
            }
        }

        /// <summary>把一段文本切成候选连续短语：连续 字母/数字 为一个 token，再生成 1..4 元 n-gram，纯数字短语跳过。</summary>
        private static IEnumerable<string> Phrases(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            var tokens = new List<string>();
            var sb = new System.Text.StringBuilder();
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(ch);
                else if (sb.Length > 0)
                {
                    if (sb.Length >= 2) tokens.Add(sb.ToString());
                    sb.Length = 0;
                }
            }
            if (sb.Length >= 2) tokens.Add(sb.ToString());
            if (tokens.Count == 0) yield break;

            var maxN = Math.Min(4, tokens.Count);
            for (int len = 1; len <= maxN; len++)
            {
                for (int i = 0; i + len <= tokens.Count; i++)
                {
                    var ph = string.Join(" ", tokens, i, len);
                    if (ph.Length < 2) continue;
                    if (IsAllDigits(ph)) continue;
                    yield return ph;
                }
            }
        }

        private static bool IsAllDigits(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c != ' ' && !char.IsDigit(c)) return false;
            }
            return true;
        }

        /// <summary>
        /// GET /api/project/segments?path=...&file=&lt;targetFileId 或文件名&gt;(单目标文件可省略)&format=json|csv
        /// 读双语参照文件 (.sdlxliff) 导出段级双语表：id/status/origin/percent/source/target。
        /// </summary>
        private static ApiResult Segments(Dictionary<string, string> query)
        {
            return WithProject(query, (project, info) =>
            {
                var targets = project.GetTargetLanguageFiles().ToList();
                if (targets.Count == 0)
                    return ApiResult.Json(400, Error("项目没有目标文件"));

                var wanted = query.TryGetValue("file", out var f) ? f : null;
                ProjectFile file;
                if (string.IsNullOrEmpty(wanted))
                {
                    if (targets.Count > 1)
                        return ApiResult.Json(400, Error("多个目标文件，请用 file 指定: " +
                            string.Join("; ", targets.Select(t => t.Id + " = " + t.Name))));
                    file = targets[0];
                }
                else
                {
                    file = targets.FirstOrDefault(t =>
                        string.Equals(t.Id.ToString(), wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
                    if (file == null)
                        return ApiResult.Json(404, Error("目标文件不存在: " + wanted));
                }

                var bilingualPath = ResolveBilingualPath(file, info);
                if (bilingualPath == null)
                    return ApiResult.Json(409, Error("双语参照文件尚未生成（请在 Studio 打开过该文件后重试）: " + file.Name));

                var rows = BilingualParser.Parse(bilingualPath);
                var lang = file.Language == null ? null : file.Language.IsoAbbreviation;

                var format = query.TryGetValue("format", out var fmt) ? fmt : "json";
                if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                {
                    var csv = new System.Text.StringBuilder();
                    csv.Append('\ufeff'); // Excel 双击直开
                    csv.Append(BilingualParser.ToCsv(rows));
                    var name = SanitizeFileName(info.Name + "_" + (lang ?? "tgt")) + "_segments.csv";
                    return ApiResult.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", name);
                }

                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "project", info.Name },
                    { "file", file.Name },
                    { "fileId", file.Id },
                    { "language", lang },
                    { "bilingualPath", bilingualPath },
                    { "count", rows.Count },
                    { "segments", rows },
                });
            });
        }

        /// <summary>
        /// 双语参照文件定位：先取 BilingualReferenceFileLocalPath 属性；该属性在部分项目
        /// （如非文件包项目）恒为 null，回退到 &lt;项目目录&gt;\&lt;语言&gt;\ 及源文件同目录按同名 .sdlxliff 查找。
        /// 找不到返回 null。
        /// </summary>
        private static string ResolveBilingualPath(ProjectFile file, ProjectInfo info)
        {
            var declared = file.BilingualReferenceFileLocalPath;
            if (!string.IsNullOrEmpty(declared) && File.Exists(declared))
                return declared;

            var baseName = Path.GetFileNameWithoutExtension(file.Name);
            if (string.Equals(baseName, file.Name, StringComparison.OrdinalIgnoreCase))
                baseName = Path.GetFileNameWithoutExtension(baseName); // a.sdlxliff → a

            var candidates = new List<string>();
            var lang = file.Language == null ? null : file.Language.IsoAbbreviation;
            if (info != null && !string.IsNullOrEmpty(info.LocalProjectFolder) && !string.IsNullOrEmpty(lang))
                candidates.Add(Path.Combine(info.LocalProjectFolder, lang, file.Name));
            var dir = Path.GetDirectoryName(file.LocalFilePath);
            if (!string.IsNullOrEmpty(dir))
                candidates.Add(Path.Combine(dir, file.Name));
            if (!string.IsNullOrEmpty(baseName))
                foreach (var c in candidates.ToList())
                    if (!string.Equals(Path.GetFileName(c), file.Name, StringComparison.OrdinalIgnoreCase))
                        candidates.Add(Path.Combine(Path.GetDirectoryName(c), baseName + ".sdlxliff"));

            foreach (var candidate in candidates)
                if (File.Exists(candidate)) return candidate;
            // 语言目录内任意与文件主名前缀匹配的 .sdlxliff
            if (info != null && !string.IsNullOrEmpty(info.LocalProjectFolder) && !string.IsNullOrEmpty(lang))
            {
                var folder = Path.Combine(info.LocalProjectFolder, lang);
                if (Directory.Exists(folder) && !string.IsNullOrEmpty(baseName))
                {
                    var fuzzy = Directory.GetFiles(folder, baseName + "*.sdlxliff")
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (fuzzy != null) return fuzzy;
                }
            }
            return null;
        }

        /// <summary>状态面板专用：2 秒内拿不到 UI 线程就返回 null（页面显示 busy），绝不拖死 HTTP 线程。</summary>
        public static List<Dictionary<string, object>> SafeProjects()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return ListProjectsOnUi();
                return app.Dispatcher.Invoke(new Func<List<Dictionary<string, object>>>(ListProjectsOnUi),
                    TimeSpan.FromSeconds(2)) as List<Dictionary<string, object>>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<Dictionary<string, object>> ListProjectsOnUi()
        {
            return ProjectsController().GetProjects()
                .Select(p => DescribeProject(p.GetProjectInfo()))
                .ToList();
        }

        private static ApiResult WithProject(
            Dictionary<string, string> query, Func<FileBasedProject, ProjectInfo, ApiResult> handler)
        {
            if (!query.TryGetValue("path", out var path) || string.IsNullOrEmpty(path))
                return ApiResult.Json(400, Error("缺少 query 参数 path（.sdlp 路径）"));
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                return ApiResult.Json(404, Error("项目文件不存在: " + path));

            return OnUi(() =>
            {
                var project = ProjectsController().GetAllProjects()
                                .FirstOrDefault(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase))
                            ?? new FileBasedProject(path);
                return handler(project, project.GetProjectInfo());
            });
        }

        private static T OnUi<T>(Func<T> action)
        {
            var app = System.Windows.Application.Current;
            if (app == null)
                return action();
            return app.Dispatcher.Invoke(action);
        }

        private static ProjectsController ProjectsController()
        {
            return SdlTradosStudio.Application.GetController<ProjectsController>();
        }

        private static Dictionary<string, object> ParseBody(string body)
        {
            var normalized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(body))
                return normalized;

            var parsed = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<Dictionary<string, object>>(body);
            if (parsed != null)
                foreach (var pair in parsed)
                    normalized[pair.Key] = pair.Value;
            return normalized;
        }

        private static string Str(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var value) ? value as string : null;
        }

        private static bool Bool(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var value) && value is bool b && b;
        }

        private static List<string> StrList(Dictionary<string, object> map, string key)
        {
            var result = new List<string>();
            if (map.TryGetValue(key, out var value) && value is List<object> list)
                result.AddRange(list.OfType<string>());
            return result;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { { "error", message } };
        }
    }
}
