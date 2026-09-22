using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sdl.Core.Globalization;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using TradosToolkit.Common;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;
using TradosToolkit.TranslationProvider.Engines;
using System.Threading;

namespace TradosToolkit.Server
{
    /// <summary>
    /// HTTP 路由 → Studio 项目自动化。所有 Studio 对象模型操作 marshal 回 UI 线程。
    /// </summary>
    public static class ProjectApi
    {
        internal static readonly Dictionary<string, string> TaskTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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
                case "/api/project/report/save":
                    return SaveTaskReport(query);
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
                case "/api/glossary/domains":
                    return GlossaryDomains();
                case "/api/project/triage":
                    return Triage(query);
                case "/api/project/audit":
                    return Audit(method, query);
                case "/api/project/sdlxliff":
                    return method == "POST" ? WriteSdlxliff(query, body) : ApiResult.Json(405, Error("sdlxliff 写回端点需 POST"));
                case "/api/project/pipeline":
                    return method == "POST" ? Pipeline(query, body) : ApiResult.Json(405, Error("pipeline 端点需 POST"));
                case "/api/review":
                    return Review(method, query, body);
                case "/api/review/result":
                    return ReviewResult(query);
                case "/api/project/btqa":
                    return BackTranslateQa.Handle(method, query, body);
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

        internal static ApiResult CreateProject(string body)
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
                var ids = added.Select(f => f.Id).ToArray();
                project.SetFileRole(ids, FileRole.Translatable);

                var prepared = new List<string>();
                foreach (var taskTemplateId in new[]
                         {
                             AutomaticTaskTemplateIds.Scan,
                             AutomaticTaskTemplateIds.ConvertToTranslatableFormat,
                             AutomaticTaskTemplateIds.CopyToTargetLanguages,
                         })
                {
                    var prepareTask = project.RunAutomaticTask(ids, taskTemplateId);
                    foreach (var m in prepareTask.Messages ?? new ExecutionMessage[0])
                    {
                        var text = MessageText(m);
                        if (!string.IsNullOrWhiteSpace(text)) prepared.Add(text);
                    }
                }
                project.Save();

                if (project.GetTargetLanguageFiles().Length == 0)
                    return ApiResult.Json(500, Error("项目已创建但未生成目标文件，Prepare 失败: " + string.Join(" | ", prepared)));

                var sdlp = project.FilePath;
                if (Bool(request, "openInStudio"))
                    ProjectsController().Add(sdlp);

                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "projectPath", sdlp },
                    { "id", project.GetProjectInfo().Id },
                    { "prepare", prepared },
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
        /// 预翻译前把该提供程序并入项目级级联（不覆盖模板既有提供程序）；本地配置了 LLM 时自动补入插件 TM→LLM 提供程序。
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
            return WithProject(query, (project, _) => ExecuteTaskOnProject(project, taskKey, templateId, query, request));
        }

        /// <summary>在已打开的 project 上跑单个自动任务（providerUri 先写入级联配置）。供单任务与 pipeline 复用。</summary>
        internal static ApiResult ExecuteTaskOnProject(FileBasedProject project, string taskKey, string templateId,
            Dictionary<string, string> query, Dictionary<string, object> request)
        {
            var providerUri = Str(request, "providerUri");

            Guid[] fileIds;
            if (query.TryGetValue("files", out var ids) && !string.IsNullOrEmpty(ids))
                fileIds = ids.Split(',').Select(Guid.Parse).ToArray();
            else
                fileIds = project.GetTargetLanguageFiles().Select(f => f.Id).ToArray();

            if (fileIds.Length == 0)
                return ApiResult.Json(400, Error("项目没有目标文件"));

            if (!string.IsNullOrEmpty(providerUri) || HasConfiguredLlm())
            {
                var state = Str(request, "providerState") ?? string.Empty;
                var config = GetProviderConfig(project);

                // 已匹配的本地库：插到级联最前优先查询；并入而非覆盖，避免冲掉模板里既有的提供程序
                if (!string.IsNullOrEmpty(providerUri))
                {
                    var tmUri = new Uri(providerUri);
                    if (!ContainsProvider(config, tmUri))
                        config.Entries.Insert(0, new TranslationProviderCascadeEntry(
                            new TranslationProviderReference(tmUri, state, true), false, true, false));
                }

                // 本地已配置 LLM：把插件 TM→LLM 提供程序补进级联，保证预翻译走 LLM 回退
                var toolkitUri = BuildToolkitUri();
                if (toolkitUri != null && !ContainsProvider(config, toolkitUri))
                    config.Entries.Add(new TranslationProviderCascadeEntry(
                        new TranslationProviderReference(toolkitUri, null, true), false, true, false));

                project.UpdateTranslationProviderConfiguration(config);
                ToolkitLog.Info("预翻译：已更新项目提供程序级联，共 " + config.Entries.Count + " 项");
            }

            var task = project.RunAutomaticTask(fileIds, templateId);
            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "task", taskKey },
                { "messages", (task.Messages ?? new ExecutionMessage[0]).Select(MessageText).ToList() },
                { "reports", (task.Reports ?? new TaskReport[0]).Select(r => new Dictionary<string, object>
                    { { "id", r.Id }, { "name", r.Name } }).ToList() },
            });
        }

        private static string MessageText(object message)
        {
            var text = message.GetType().GetProperty("Message");
            return (text == null ? message.ToString() : text.GetValue(message, null) as string) ?? string.Empty;
        }

        /// <summary>读取项目级提供程序配置；读不到则返回空配置（不覆盖父级）。</summary>
        private static TranslationProviderConfiguration GetProviderConfig(FileBasedProject project)
        {
            TranslationProviderConfiguration config = null;
            try
            {
                config = project.GetTranslationProviderConfiguration();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("读取项目提供程序配置失败，改为空配置", e);
            }
            if (config == null)
                config = new TranslationProviderConfiguration();
            if (config.Entries == null)
                config.Entries = new List<TranslationProviderCascadeEntry>();
            return config;
        }

        private static bool ContainsProvider(TranslationProviderConfiguration config, Uri uri)
        {
            return config.Entries != null && config.Entries.Any(e =>
                e != null && e.MainTranslationProvider != null &&
                e.MainTranslationProvider.Uri != null &&
                e.MainTranslationProvider.Uri.Equals(uri));
        }

        private static bool HasConfiguredLlm()
        {
            try
            {
                return !string.IsNullOrEmpty(ToolkitConfig.Load().LlmBaseUrl);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>用本地 config.json 的 LLM 参数构建插件 TM→LLM 提供程序 URI；未配置 LLM 返回 null。</summary>
        private static Uri BuildToolkitUri()
        {
            try
            {
                var cfg = ToolkitConfig.Load();
                if (string.IsNullOrEmpty(cfg.LlmBaseUrl))
                    return null;
                return TranslationProvider.ToolkitUri.Build(cfg.LlmBaseUrl, cfg.LlmModel, true, true, true);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("构建插件提供程序 URI 失败", e);
                return null;
            }
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

        /// <summary>
        /// POST /api/project/pipeline?path=...
        /// body { "files": "id1,id2"(可选), "steps": [ { "task": "pretranslate", "providerUri": "...", "providerState": "" }, { "task": "updatetm" } ],
        ///        "tolerant": false(可选，缺省 false；true=某步失败不中断后续) }
        /// 多步自动任务按序编排：一次提交按 steps 顺序执行，后台异步（TaskRegistry），/api/task 可见逐步进度（currentStep + 每步 done/error）。
        /// </summary>
        private static ApiResult Pipeline(Dictionary<string, string> query, string body)
        {
            var request = ParseBody(body);
            var stepsRaw = request.ContainsKey("steps") ? EngineHttp.AsList(request["steps"]) : null;
            if (stepsRaw == null || stepsRaw.Count == 0)
                return ApiResult.Json(400, Error("缺少 steps 数组"));

            var steps = new List<Dictionary<string, object>>();
            foreach (var o in stepsRaw)
            {
                var s = o as Dictionary<string, object>;
                if (s == null) continue;
                var tk = Str(s, "task");
                if (string.IsNullOrEmpty(tk) || !TaskTemplates.ContainsKey(tk)) continue;
                steps.Add(s);
            }
            if (steps.Count == 0)
                return ApiResult.Json(400, Error("steps 里没有可识别的 task（" + string.Join("|", TaskTemplates.Keys) + "）"));

            var tolerant = Bool(request, "tolerant");
            var path = query.TryGetValue("path", out var p) ? p : null;
            BackgroundTask st = null;
            st = TaskRegistry.Start("pipeline", path, () =>
            {
                var stepsState = new List<Dictionary<string, object>>();
                for (int i = 0; i < steps.Count; i++)
                    stepsState.Add(new Dictionary<string, object> { { "step", i + 1 }, { "task", Str(steps[i], "task") }, { "status", "pending" } });

                TaskRegistry.SetProgress(st.Id, new Dictionary<string, object>
                { { "currentStep", 0 }, { "stepCount", steps.Count }, { "steps", stepsState }, { "status", "running" } });

                ApiResult outcome = null;
                var wp = WithProject(query, (project, info) =>
                {
                    for (int i = 0; i < steps.Count; i++)
                    {
                        var step = steps[i];
                        var tk = Str(step, "task");
                        stepsState[i]["status"] = "running";
                        TaskRegistry.SetProgress(st.Id, new Dictionary<string, object>
                        { { "currentStep", i + 1 }, { "stepCount", steps.Count }, { "steps", stepsState }, { "status", "running" } });

                        var stepReq = new Dictionary<string, object>();
                        if (Str(step, "providerUri") != null) stepReq["providerUri"] = Str(step, "providerUri");
                        if (Str(step, "providerState") != null) stepReq["providerState"] = Str(step, "providerState");

                        ApiResult stepResult;
                        try { stepResult = ExecuteTaskOnProject(project, tk, TaskTemplates[tk], query, stepReq); }
                        catch (Exception e) { stepResult = ApiResult.Json(500, Error(e.Message)); }

                        var ok = stepResult != null && stepResult.Status >= 200 && stepResult.Status < 300;
                        stepsState[i]["status"] = ok ? "done" : "error";
                        stepsState[i]["error"] = ok ? null : ErrorText(stepResult);
                        if (ok)
                        {
                            stepsState[i]["result"] = stepResult.Payload;
                            outcome = stepResult;
                        }
                        else
                        {
                            if (!tolerant)
                            {
                                for (int j = i + 1; j < steps.Count; j++) stepsState[j]["status"] = "skipped";
                                break;
                            }
                        }
                    }
                    return ApiResult.Json(200, new Dictionary<string, object>());
                });

                if (outcome == null && wp != null && wp.Status >= 400)
                    outcome = wp;
                if (outcome == null)
                    outcome = ApiResult.Json(200, new Dictionary<string, object> { { "pipeline", "completed" }, { "steps", stepsState } });

                TaskRegistry.SetProgress(st.Id, new Dictionary<string, object>
                { { "currentStep", steps.Count }, { "stepCount", steps.Count }, { "steps", stepsState },
                  { "status", stepsState.Any(x => (string)x["status"] == "error") ? "warn" : "done" } });
                return outcome;
            });

            return ApiResult.Json(202, new Dictionary<string, object>
            {
                { "taskId", st.Id }, { "task", "pipeline" }, { "steps", steps.Count }, { "status", "running" },
            });
        }

        private static object ErrorText(ApiResult r)
        {
            var pd = r == null ? null : r.Payload as Dictionary<string, object>;
            return pd != null && pd.ContainsKey("error") ? pd["error"] : "step failed";
        }

        internal static ApiResult Report(Dictionary<string, string> query)
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

                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "project", info.Name },
                    { "total", total },
                    { "targets", perTarget },
                });
            });
        }

        /// <summary>
        /// GET /api/project/report/save?path=...&reportId=&lt;guid&gt;&out=...&format=excel|xml|html|mht
        /// 把项目里已生成的任务报告，按 Trados 原生方式（SaveTaskReportAs）「另存为」到 out。
        /// 产出的是 Studio 自带报告引擎的结果（Excel/XML/HTML/MHT），不是自造 CSV。
        /// </summary>
        internal static ApiResult SaveTaskReport(Dictionary<string, string> query)
        {
            return WithProject(query, (project, _) =>
            {
                if (!query.TryGetValue("reportId", out var idText) || !Guid.TryParse(idText, out var reportId))
                    return ApiResult.Json(400, Error("缺少或非法的 query 参数 reportId（任务报告 Id）"));
                if (!query.TryGetValue("out", out var outPath) || string.IsNullOrEmpty(outPath))
                    return ApiResult.Json(400, Error("缺少 query 参数 out（报告另存为路径）"));

                var format = ParseReportFormat(query.TryGetValue("format", out var fmt) ? fmt : null);
                outPath = Path.GetFullPath(outPath);
                var dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // 走 Studio 原生报告引擎：把项目里已有的任务报告另存为指定格式
                project.SaveTaskReportAs(reportId, outPath, format);

                if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                    return ApiResult.Json(500, Error("报告另存为失败：" + outPath));

                ToolkitLog.Info("报告另存为：" + outPath + "（" + format.Name + "）");
                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "reportPath", outPath },
                    { "format", format.Name },
                    { "size", new FileInfo(outPath).Length },
                });
            });
        }

        /// <summary>报告格式名 → Sdl.ProjectAutomation.Core.ReportFormat（缺省 Excel）。</summary>
        private static ReportFormat ParseReportFormat(string format)
        {
            switch ((format ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "xml": return ReportFormat.Xml;
                case "html": return ReportFormat.Html;
                case "mht": return ReportFormat.Mht;
                default: return ReportFormat.Excel;
            }
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
        internal static ApiResult Package(string method, Dictionary<string, string> query, string body)
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
                if (package == null)
                    return ApiResult.Json(500, Error("打包失败：未返回打包结果"));

                var waited = 0;
                while (package.Status == PackageStatus.NotStarted || package.Status == PackageStatus.Scheduled
                       || package.Status == PackageStatus.InProgress || package.Status == PackageStatus.Cancelling)
                {
                    if (waited >= 180000)
                        return ApiResult.Json(500, Error("打包超时（" + package.Status + "）：" + package.StatusMessage));
                    Thread.Sleep(200);
                    waited += 200;
                }
                if (package.Status != PackageStatus.Completed)
                    return ApiResult.Json(500, Error("打包失败（" + package.Status + "）：" + package.StatusMessage));

                project.SavePackageAs(package.PackageId, outPath);

                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "packagePath", outPath },
                    { "manualTaskId", manualTask.Id },
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
        /// GET /api/glossary/domains  领域树（主领域/子领域两级）。
        /// 当前由插件本地占位实现（DomainTree.Defaults）返回，供术语管理/工作台等 UI 加载领域下拉；
        /// 服务端未来可用 API 返回真正的行业细分树。
        /// </summary>
        private static ApiResult GlossaryDomains()
        {
            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "domains", DomainCatalog.Tree() },
            });
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
        /// 按 query 选定目标文件并解析其双语参照文件，交给 handler；统一处理 多文件/无文件/双语未生成 三种失败。
        /// </summary>
        internal static ApiResult WithTargetBilingual(
            Dictionary<string, string> query,
            Func<ProjectInfo, ProjectFile, string, List<BilingualSegment>, ApiResult> handler)
        {
            return WithProject(query, (project, info) =>
            {
                var targets = project.GetTargetLanguageFiles().ToList();
                if (targets.Count == 0)
                    return ApiResult.Json(400, Error("项目没有目标文件"));

                ProjectFile file;
                var wanted = query.TryGetValue("file", out var f) ? f : null;
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

                var bp = ResolveBilingualPath(file, info);
                if (bp == null)
                    return ApiResult.Json(409, Error("双语参照文件尚未生成（请在 Studio 打开过该文件后重试）: " + file.Name));
                return handler(info, file, bp, BilingualParser.Parse(bp));
            });
        }

        /// <summary>源文归一化键：折叠空白 + 小写，用于重复检测/一致性分组。</summary>
        internal static string NormKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
        }

        private static int CountChar(string s, char c)
        {
            int n = 0;
            foreach (var x in s) if (x == c) n++;
            return n;
        }

        /// <summary>单段分类：skip=可跳过(省成本) / chunk=超长 / highrisk=疑似异常 / normal=正常。reason 说明原因。</summary>
        internal static string TriageCategory(string src, out string reason)
        {
            reason = string.Empty;
            var t = (src ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(t)) { reason = "空文本"; return "skip"; }

            var hasLetter = false;
            foreach (var c in t) if (char.IsLetter(c)) { hasLetter = true; break; }
            if (!hasLetter) { reason = "纯数字/符号"; return "skip"; }

            if (t.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("www.", StringComparison.OrdinalIgnoreCase) >= 0)
            { reason = "URL"; return "skip"; }

            if (CountChar(t, '(') != CountChar(t, ')') || CountChar(t, '[') != CountChar(t, ']')
                || CountChar(t, '{') != CountChar(t, '}') || CountChar(t, '（') != CountChar(t, '）')
                || CountChar(t, '「') != CountChar(t, '」') || CountChar(t, '“') != CountChar(t, '”')
                || CountChar(t, '"') % 2 != 0)
            { reason = "括号/引号不闭合"; return "highrisk"; }

            if (t.IndexOf('\uFFFD') >= 0) { reason = "含替换字符(疑似乱码)"; return "highrisk"; }

            if (t.Length > 200) { reason = "超长句(" + t.Length + "字)"; return "chunk"; }

            return "normal";
        }

        /// <summary>
        /// GET /api/project/triage?path=...&amp;file=&lt;可选&gt;
        /// 翻译前智能分诊：给每条源段分类（skip/chunk/highrisk/normal + 重复次数），
        /// 产出"重活 vs 白花钱"总结，开工前先看清这份活有多少重复可去重、多少异常要盯。纯本地、零 LLM、零依赖。
        /// </summary>
        private static ApiResult Triage(Dictionary<string, string> query)
        {
            return WithTargetBilingual(query, (info, file, bp, rows) =>
            {
                var lang = file.Language == null ? null : file.Language.IsoAbbreviation;
                var counts = new Dictionary<string, int> { { "skip", 0 }, { "chunk", 0 }, { "highrisk", 0 }, { "normal", 0 } };

                // pass1: 重复键频次
                var repeatKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var seg in rows)
                {
                    var key = NormKey(seg.Source);
                    repeatKey[key] = repeatKey.ContainsKey(key) ? repeatKey[key] + 1 : 1;
                }

                // pass2: 分类
                var per = new List<Dictionary<string, object>>();
                foreach (var seg in rows)
                {
                    var cat = TriageCategory(seg.Source, out var reason);
                    if (counts.ContainsKey(cat)) counts[cat]++;
                    var key = NormKey(seg.Source);
                    per.Add(new Dictionary<string, object>
                    {
                        { "id", seg.Id }, { "source", seg.Source }, { "target", seg.Target },
                        { "status", seg.Status }, { "category", cat }, { "reason", reason },
                        { "repeat", repeatKey[key] },
                    });
                }

                var repeatGroups = repeatKey.Where(kv => kv.Value >= 3)
                    .OrderByDescending(kv => kv.Value).Take(30)
                    .Select(kv => new Dictionary<string, object> { { "source", kv.Key }, { "count", kv.Value } })
                    .ToList();

                var highrisk = per.Where(p => (string)p["category"] == "highrisk").Take(50).ToList();
                var chunks = per.Where(p => (string)p["category"] == "chunk").Take(30).ToList();

                var summary = "共 " + rows.Count + " 段：正常 " + counts["normal"]
                    + "；可跳过(省网关费) " + counts["skip"]
                    + "；超长待分块 " + counts["chunk"]
                    + "；疑似异常待人工 " + counts["highrisk"]
                    + "；重复出现≥3次的源段 " + repeatGroups.Count + " 组。";

                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "project", info.Name }, { "file", file.Name }, { "language", lang },
                    { "count", rows.Count }, { "summary", summary },
                    { "categories", counts },
                    { "repeatGroups", repeatGroups },
                    { "highrisk", highrisk },
                    { "chunks", chunks },
                });
            });
        }

        /// <summary>
        /// GET /api/project/audit?path=...&amp;file=&lt;可选&gt;          → 一致性审计列表（只读）
        /// POST /api/project/audit?path=...&amp;file=&lt;可选&gt;          → 对全部分歧组应用"统一为主流译法"
        /// 同源文多次出现却译得不一样 → 分组列出分歧，POST 把少数派改写为主流译文（写入 .sdlxliff，先备份 .bak；
        /// 目标文本含标签组的跳过列人工，避免误伤标签）。
        /// </summary>
        private static ApiResult Audit(string method, Dictionary<string, string> query)
        {
            var all = query.TryGetValue("all", out var av) && av == "1";
            if (all) return AuditAllFiles(method, query);

            var apply = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);
            return WithTargetBilingual(query, (info, file, bp, rows) =>
            {
                var lang = file.Language == null ? null : file.Language.IsoAbbreviation;
                var byKey = new Dictionary<string, List<BilingualSegment>>(StringComparer.OrdinalIgnoreCase);
                foreach (var seg in rows)
                {
                    if (string.IsNullOrWhiteSpace(seg.Target)) continue;
                    var key = NormKey(seg.Source);
                    if (!byKey.TryGetValue(key, out var l)) { l = new List<BilingualSegment>(); byKey[key] = l; }
                    l.Add(seg);
                }

                var groups = new List<Dictionary<string, object>>();
                foreach (var kv in byKey.OrderByDescending(k => k.Value.Count))
                {
                    var votes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    foreach (var seg in kv.Value)
                    {
                        var tv = seg.Target.Trim();
                        if (!votes.TryGetValue(tv, out var ids)) { ids = new List<string>(); votes[tv] = ids; }
                        ids.Add(seg.Id ?? string.Empty);
                    }
                    if (votes.Count < 2) continue;

                    var ordered = votes.OrderByDescending(v => v.Value.Count).ToList();
                    var majority = ordered[0];
                    var needsManual = kv.Value.Any(s => s.Target.IndexOf('<') >= 0);
                    groups.Add(new Dictionary<string, object>
                    {
                        { "source", kv.Key },
                        { "occurrences", kv.Value.Count },
                        { "distinctTranslations", ordered.Count },
                        { "needsManual", needsManual },
                        { "majorityTarget", majority.Key },
                        { "votes", ordered.Select(v => new Dictionary<string, object>
                            {
                                { "target", v.Key }, { "count", v.Value.Count }, { "segIds", v.Value },
                            }).ToList() },
                    });
                }

                if (!apply)
                    return ApiResult.Json(200, new Dictionary<string, object>
                    {
                        { "project", info.Name }, { "file", file.Name }, { "language", lang },
                        { "segments", rows.Count },
                        { "divergentGroups", groups.Count },
                        { "needsManual", groups.Count(g => (bool)g["needsManual"]) },
                        { "groups", groups.Take(100).ToList() },
                    });

                return ApplyUniform(bp, groups);
            });
        }

        /// <summary>把分歧组的少数派译文改写为组内主流译文。纯文本目标才改写；备份 .bak 后再保存。</summary>
        private static ApiResult ApplyUniform(string bp, List<Dictionary<string, object>> groups)
        {
            if (groups.Count == 0)
                return ApiResult.Json(200, new Dictionary<string, object> { { "applied", 0 }, { "skippedTags", 0 } });

            XDocument doc;
            try { doc = XDocument.Load(bp); }
            catch (Exception e) { return ApiResult.Json(500, Error("解析双语文件失败: " + e.Message)); }

            var backup = bp + ".bak";
            var applied = 0;
            var skippedTags = 0;
            var missing = 0;

            foreach (var g in groups)
            {
                if ((bool)g["needsManual"]) { skippedTags++; continue; }
                var majority = (string)g["majorityTarget"];
                var votes = g["votes"] as List<Dictionary<string, object>>;
                if (votes == null) continue;
                foreach (var v in votes)
                {
                    var target = (string)v["target"];
                    if (string.Equals(target, majority, StringComparison.Ordinal)) continue;
                    var segIds = v["segIds"] as List<string>;
                    if (segIds == null) continue;
                    foreach (var id in segIds)
                    {
                        var tu = doc.Descendants().FirstOrDefault(e =>
                            e.Name.LocalName == "trans-unit" && (string)e.Attribute("id") == id);
                        if (tu == null) { missing++; continue; }
                        var tgtEl = tu.Elements().FirstOrDefault(e => e.Name.LocalName == "target");
                        if (tgtEl == null) { missing++; continue; }
                        tgtEl.RemoveNodes();
                        tgtEl.Add(new XText(majority));
                        applied++;
                    }
                }
            }

            if (applied == 0)
                return ApiResult.Json(200, new Dictionary<string, object> { { "applied", 0 }, { "skippedTags", skippedTags } });

            try { File.Copy(bp, backup, true); }
            catch (IOException e) { return ApiResult.Json(409, Error("原文件备份失败(可能被 Studio 占用): " + e.Message)); }

            try { doc.Save(bp, SaveOptions.DisableFormatting); }
            catch (IOException e) { return ApiResult.Json(409, Error("写入失败(文件被 Studio 占用，请关闭该文件后重试): " + e.Message)); }

            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "applied", applied }, { "skippedTags", skippedTags }, { "missingIds", missing },
                { "backup", backup },
                { "message", "已统一 " + applied + " 处译文；含标签分歧组自动跳过 " + skippedTags +
                             " 组(列人工)；原文件已备份到 .bak，请在 Studio 重新打开该文件生效。预览/应用的是目标文本，不含内部标签语义。" },
            });
        }

        /// <summary>
        /// POST /api/project/sdlxliff?path=...&amp;file=&lt;可选&gt;
        /// body { "segments": [ { "id": "...", "target": "...", "status": "Translated|SignedOff|..." } ] }
        /// 批量写回目标译文（+可选确认状态）。先备份 .bak。
        /// 安全规则：仅当 target 无结构内联标签(g/x/bx/ex/ph 等)且是单文本片段时才整体替换（保留 mrk 分隔）；
        /// 含内联标签/多文本片段的段跳过列 skipped，避免破坏占位符。Studio 重新打开该文件生效。
        /// </summary>
        private static ApiResult WriteSdlxliff(Dictionary<string, string> query, string body)
        {
            var request = ParseBody(body);
            var segsRaw = request.ContainsKey("segments") ? request["segments"] as List<object> : null;
            if (segsRaw == null || segsRaw.Count == 0)
                return ApiResult.Json(400, Error("缺少 segments 数组 [{id,target?,status?}]"));

            return WithProject(query, (project, info) =>
            {
                var targets = project.GetTargetLanguageFiles().ToList();
                if (targets.Count == 0) return ApiResult.Json(400, Error("项目没有目标文件"));

                ProjectFile file;
                var wanted = query.TryGetValue("file", out var f) ? f : null;
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
                    if (file == null) return ApiResult.Json(404, Error("目标文件不存在: " + wanted));
                }

                var bp = ResolveBilingualPath(file, info);
                if (bp == null)
                    return ApiResult.Json(409, Error("双语参照文件尚未生成（请在 Studio 打开过该文件后重试）: " + file.Name));

                XDocument doc;
                try { doc = XDocument.Load(bp); }
                catch (Exception e) { return ApiResult.Json(500, Error("解析双语文件失败: " + e.Message)); }

                var backup = bp + ".bak";
                int applied = 0, skipped = 0, missing = 0, statuses = 0;
                var dirty = false;
                foreach (var o in segsRaw)
                {
                    var s = o as Dictionary<string, object>;
                    if (s == null) continue;
                    var id = Str(s, "id");
                    if (string.IsNullOrEmpty(id)) continue;

                    var tu = doc.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName == "trans-unit" && (string)e.Attribute("id") == id);
                    if (tu == null) { missing++; continue; }
                    var tgtEl = tu.Elements().FirstOrDefault(e => e.Name.LocalName == "target");
                    if (tgtEl == null) { missing++; continue; }

                    var target = Str(s, "target");
                    if (target != null)
                    {
                        var textNodes = tgtEl.DescendantNodes().OfType<XText>().ToList();
                        var structural = tgtEl.Descendants().Any(e => IsStructuralTag(e.Name.LocalName));
                        if (!structural && textNodes.Count == 1)
                        {
                            textNodes[0].Value = target;
                            applied++; dirty = true;
                        }
                        else skipped++;
                    }

                    var status = Str(s, "status");
                    if (!string.IsNullOrEmpty(status)) { tu.SetAttributeValue("conf", status); statuses++; dirty = true; }
                }

                if (dirty)
                {
                    try { File.Copy(bp, backup, true); }
                    catch (IOException e2) { return ApiResult.Json(409, Error("备份失败(文件被 Studio 占用?): " + e2.Message)); }
                    try { doc.Save(bp, SaveOptions.DisableFormatting); }
                    catch (IOException e2) { return ApiResult.Json(409, Error("写入失败(文件被 Studio 占用，请关闭该文件后重试): " + e2.Message)); }
                }

                var lang = file.Language == null ? null : file.Language.IsoAbbreviation;
                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "project", info.Name }, { "file", file.Name }, { "language", lang },
                    { "requested", segsRaw.Count }, { "applied", applied }, { "statusesSet", statuses },
                    { "skippedTagged", skipped }, { "missingIds", missing }, { "backup", backup },
                    { "hasBackup", dirty },
                    { "message", "写回 " + applied + " 段译文" +
                        (statuses > 0 ? "+" + statuses + " 段状态" : "") +
                        (skipped > 0 ? "；含内联标签/多文本片段跳过 " + skipped + " 段(请到 Studio 编辑)" : "") +
                        (missing > 0 ? "；未定位段 " + missing : "") + "。备份于 " + backup + "，Studio 重新打开该文件生效。" },
                });
            });
        }

        private static bool IsStructuralTag(string name)
        {
            // 内联占位/标签类元素（携带格式与顺序，直接改文本会破坏）；mrk 仅做分段不视为破坏。
            switch (name)
            {
                case "g": case "x": case "bx": case "ex": case "ph": case "it":
                case "bp": case "ep": case "xid": return true;
                default: return false;
            }
        }

        /// <summary>
        /// GET /api/project/audit?path=...&amp;all=1   → 跨文件一致性审计（只读）
        /// 遍历项目全部目标文件，跨文件汇总"同源文译文不一致"分组；POST 统一暂不开放在跨文件级（逐文件用 file 参数应用）。
        /// votes 的 segIds 形如 "&lt;segId&gt;@&lt;文件名&gt;"，可定位到具体文件。
        /// </summary>
        private static ApiResult AuditAllFiles(string method, Dictionary<string, string> query)
        {
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                return ApiResult.Json(405, Error("跨文件一致性 POST 未开放，请用 file 参数逐文件应用"));

            return WithProject(query, (project, info) =>
            {
                var targets = project.GetTargetLanguageFiles().ToList();
                var byKey = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
                var fileInfo = new List<Dictionary<string, object>>();
                int totalSegs = 0, parseable = 0, skippedNoBilingual = 0;

                foreach (var f in targets)
                {
                    var bp = ResolveBilingualPath(f, info);
                    if (bp == null) { skippedNoBilingual++; continue; }
                    var rows = BilingualParser.Parse(bp);
                    totalSegs += rows.Count; parseable++;
                    foreach (var seg in rows)
                    {
                        if (string.IsNullOrWhiteSpace(seg.Target)) continue;
                        var key = NormKey(seg.Source);
                        if (!byKey.TryGetValue(key, out var m))
                        { m = new Dictionary<string, List<string>>(StringComparer.Ordinal); byKey[key] = m; }
                        var tv = seg.Target.Trim();
                        if (!m.TryGetValue(tv, out var ids)) { ids = new List<string>(); m[tv] = ids; }
                        ids.Add(seg.Id + "@" + f.Name);
                    }
                    var lang = f.Language == null ? null : f.Language.IsoAbbreviation;
                    fileInfo.Add(new Dictionary<string, object>
                    { { "file", f.Name }, { "language", lang }, { "bilingual", bp }, { "segments", rows.Count } });
                }

                var groups = new List<Dictionary<string, object>>();
                foreach (var kv in byKey.OrderByDescending(k => k.Value.Values.Sum(v => v.Count)))
                {
                    if (kv.Value.Count < 2) continue;
                    var ordered = kv.Value.OrderByDescending(v => v.Value.Count).ToList();
                    var occurrences = ordered.Sum(v => v.Value.Count);
                    var needsManual = ordered.Any(v => v.Key.IndexOf('<') >= 0);
                    var votes = ordered.Select(v => new Dictionary<string, object>
                    { { "target", v.Key }, { "count", v.Value.Count }, { "segIds", v.Value } }).Cast<object>().ToList();
                    groups.Add(new Dictionary<string, object>
                    {
                        { "source", kv.Key }, { "occurrences", occurrences },
                        { "distinctTranslations", ordered.Count }, { "needsManual", needsManual },
                        { "majorityTarget", ordered[0].Key }, { "votes", votes },
                    });
                }

                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "project", info.Name }, { "mode", "all-files" },
                    { "targetFiles", targets.Count }, { "parseableFiles", parseable },
                    { "skippedBilingual", skippedNoBilingual }, { "totalSegments", totalSegs },
                    { "files", fileInfo },
                    { "divergentGroups", groups.Count },
                    { "needsManual", groups.Count(g => (bool)g["needsManual"]) },
                    { "groups", groups.Take(200).ToList() },
                });
            });
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

        internal static ApiResult WithProject(
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

        internal static Dictionary<string, object> ParseBody(string body)
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

        internal static string Str(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var value) ? value as string : null;
        }

        internal static bool Bool(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var value) && value is bool b && b;
        }

        private static List<string> StrList(Dictionary<string, object> map, string key)
        {
            var result = new List<string>();
            // JavaScriptSerializer 的 JSON 数组是 ArrayList，只实现非泛型 IEnumerable
            if (map.TryGetValue(key, out var value) && value is System.Collections.IEnumerable items && !(value is string))
                foreach (var o in items)
                {
                    var s = o as string;
                    if (s != null) result.Add(s);
                }
            return result;
        }

        internal static string SanitizeFileName(string name)
        {
            return FileKit.SanitizeFileName(name, string.Empty);
        }

        /// <summary>
        /// POST /api/review?path=...&amp;file=...
        /// body 可选 { "maxSegments": N } 。对目标文件逐段(或空译文除外)调用 LLM 审校：
        /// 上下文用前后段 + 当前领域术语，要求模型返回 JSON 数组逐段给出 verdict(red/amber/ok) + issues + 建议修订。
        /// 同步执行（建议放在后台线程调用，文本量大时耗时较长）。
        /// </summary>
        private static ApiResult Review(string method, Dictionary<string, string> query, string body)
        {
            if (method != "POST")
                return ApiResult.Json(405, Error("review 端点需 POST"));

            var config = ToolkitConfig.Load();
            if (string.IsNullOrWhiteSpace(config.LlmBaseUrl) || string.IsNullOrWhiteSpace(config.LlmModel)
                || string.IsNullOrWhiteSpace(config.ApiKey))
                return ApiResult.Json(412, Error("未配置 LLM(llmBaseUrl/llmModel/apiKey见 config.json)，无法审校"));

            var request = ParseBody(body);
            var want = request.ContainsKey("maxSegments")
                ? Math.Max(1, Math.Min(5000, Convert.ToInt32(request["maxSegments"]))) : 2000;

            string bp = null, fileName = null, lang = null, srcLang = null;
            var bpErr = WithProject(query, (project, info) =>
            {
                var targets = project.GetTargetLanguageFiles().ToList();
                if (targets.Count == 0) return ApiResult.Json(400, Error("项目没有目标文件"));
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
                    if (file == null) return ApiResult.Json(404, Error("目标文件不存在: " + wanted));
                }
                var p = ResolveBilingualPath(file, info);
                if (p == null) return ApiResult.Json(409, Error("双语参照文件未生成: " + file.Name));
                bp = p; fileName = file.Name;
                lang = file.Language == null ? null : file.Language.IsoAbbreviation;
                srcLang = info.SourceLanguage == null ? null : info.SourceLanguage.IsoAbbreviation;
                return ApiResult.Json(200, new Dictionary<string, object>());
            });
            if (bpErr.Status >= 400) return bpErr;

            List<BilingualSegment> rows;
            try { rows = BilingualParser.Parse(bp); }
            catch (Exception e) { return ApiResult.Json(500, Error("解析双语文件失败: " + e.Message)); }

            var candidates = rows.Where(r => !string.IsNullOrWhiteSpace(r.Target)).ToList();
            if (candidates.Count > want) candidates = candidates.Take(want).ToList();
            if (candidates.Count == 0) return ApiResult.Json(200, EmptyReview(fileName, lang));

            // 领域术语表（审校提示"术语一致性"）
            var termsText = GlossaryTermsText(config.Domain, srcLang, lang);
            var termPairs = SplitPairs(termsText);

            // 把行号对齐：candidates 里的序号用于取前后文
            var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++) if (!indexOf.ContainsKey(rows[i].Id)) indexOf[rows[i].Id] = i;

            var items = new List<Dictionary<string, object>>();
            foreach (var batch in Chunk(candidates, 14))
                ReviewBatch(batch, rows, indexOf, lang, config, termsText, termPairs, items);

            var summary = new Dictionary<string, long>();
            foreach (var it in items)
            {
                var v = Str(it, "verdict") ?? "ok";
                if (!summary.ContainsKey(v)) summary[v] = 0;
                summary[v]++;
            }
            var csv = BuildReviewCsv(items);

            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "project", query.TryGetValue("path", out var pp) ? Path.GetFileNameWithoutExtension(pp) : "" },
                { "file", fileName }, { "language", lang },
                { "count", items.Count },
                { "summary", summary },
                { "items", items },
                { "csv", csv },
                { "domain", config.Domain },
                { "message", "审校完成：共 " + items.Count + " 段。 红=" + N(summary, "red")
                    + " 黄(机器可改)=" + N(summary, "amber") + " 通过=" + N(summary, "ok") + "。" },
            });
        }

        private static long N(Dictionary<string, long> map, string k)
        {
            return map.TryGetValue(k, out var v) ? v : 0;
        }

        private static Dictionary<string, object> EmptyReview(string fileName, string lang)
        {
            return new Dictionary<string, object>
            {
                { "file", fileName }, { "language", lang }, { "count", 0 },
                { "summary", new Dictionary<string, long>() }, { "items", new List<Dictionary<string, object>>() },
                { "csv", "id,score,verdict,type,reason,suggestion,source,target\r\n" },
                { "message", "没有可审校的段落（无译文或超过最大段数）。" },
            };
        }

        internal static IEnumerable<List<BilingualSegment>> Chunk(List<BilingualSegment> all, int size)
        {
            for (int i = 0; i < all.Count; i += size)
                yield return all.GetRange(i, Math.Min(size, all.Count - i));
        }

        /// <summary>把当前领域术语表转成 Prompt 里的约束文本（from => to）。ElCL语言不完全时尽量按目标语言匹配。</summary>
        internal static string GlossaryTermsText(string domain, string srcLang, string tgtLang)
        {
            var list = new List<string>();
            try
            {
                var db = new GlossaryDb();
                var gathered = new List<GlossaryEntry>();
                foreach (var pair in db.GetPairs(GlossaryDb.KindPre))
                {
                    var lgSrc = pair[0]; var lgTgt = pair[1];
                    var srcMatch = string.IsNullOrWhiteSpace(srcLang) || LangEq(lgSrc, srcLang);
                    var tgtMatch = string.IsNullOrWhiteSpace(tgtLang) || LangEq(lgTgt, tgtLang);
                    if (!srcMatch || !tgtMatch) continue;
                    gathered.AddRange(db.GetTerms(GlossaryDb.KindPre, lgSrc, lgTgt, domain));
                }
                if (gathered.Count == 0 && domain != Glossaries.DomainTree.DefaultDomain)
                    foreach (var pair in db.GetPairs(GlossaryDb.KindPre))
                        gathered.AddRange(db.GetTerms(GlossaryDb.KindPre, pair[0], pair[1], null));

                foreach (var e in gathered)
                    if (!string.IsNullOrWhiteSpace(e.From) && !string.IsNullOrWhiteSpace(e.To))
                        list.Add(e.From.Trim() + " => " + e.To.Trim());
            }
            catch (Exception) { /* 术语库不可用时不影响审校 */ }
            return string.Join("\n", list);
        }

        private static bool LangEq(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.Trim().ToLowerInvariant(), b.Trim().ToLowerInvariant(), StringComparison.Ordinal)
                || string.Equals(a.Trim().ToLowerInvariant(), b.Trim().ToLowerInvariant().Split('-')[0], StringComparison.Ordinal);
        }

        private static IReadOnlyList<KeyValuePair<string, string>> SplitPairs(string text)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var arrow = line.IndexOf("=>");
                if (arrow < 0) continue;
                var from = line.Substring(0, arrow).Trim();
                var to = line.Substring(arrow + 2).Trim();
                if (!string.IsNullOrEmpty(from))
                    result.Add(new KeyValuePair<string, string>(from, to));
            }
            return result;
        }

        private static void ReviewBatch(
            List<BilingualSegment> batch, List<BilingualSegment> rows, Dictionary<string, int> indexOf,
            string lang, ToolkitConfig config, string termsText, IReadOnlyList<KeyValuePair<string, string>> termPairs,
            List<Dictionary<string, object>> items)
        {
            var input = new List<Dictionary<string, string>>();
            foreach (var r in batch)
            {
                var ctx = SegCtx(r, rows, indexOf);
                var ctxArr = new List<string>();
                foreach (var c in ctx) ctxArr.Add(Convert.ToString(c));
                input.Add(new Dictionary<string, string>
                {
                    { "id", r.Id }, { "source", r.Source }, { "target", r.Target },
                    { "prev", ctxArr.Count > 0 ? ctxArr[0] : "" }, { "next", ctxArr.Count > 1 ? ctxArr[1] : "" },
                });
            }

            var prompt = BuildReviewPrompt(lang, termsText, termPairs);
            string reply;
            try
            {
                reply = CallLlmJson(config, prompt, input);
            }
            catch (Exception e)
            {
                foreach (var r in batch)
                    items.Add(new Dictionary<string, object>
                    {
                        { "id", r.Id }, { "source", r.Source }, { "target", r.Target },
                        { "score", 0 }, { "verdict", "error" },
                        { "type", "调用失败" }, { "reason", e.Message }, { "suggestion", null },
                    });
                return;
            }

            var verdicts = JsonArray(reply);
            var byId = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in verdicts)
                if (d.TryGetValue("id", out var idv)) byId[Convert.ToString(idv).Trim()] = d;

            foreach (var r in batch)
                MergeReviewItem(r, byId.TryGetValue(r.Id, out var v) ? v : null, items);
        }

        private static void MergeReviewItem(BilingualSegment r, Dictionary<string, object> v,
            List<Dictionary<string, object>> items)
        {
            if (v == null)
            {
                items.Add(new Dictionary<string, object>
                {
                    { "id", r.Id }, { "source", r.Source }, { "target", r.Target },
                    { "score", 100 }, { "verdict", "ok" },
                    { "type", null }, { "reason", null }, { "suggestion", null },
                });
                return;
            }
            var score = v.ContainsKey("score") ? Convert.ToInt32(v["score"]) : 100;
            var verdict = v.ContainsKey("verdict") ? Convert.ToString(v["verdict"]) : "ok";
            var issues = v.ContainsKey("issues") ? v["issues"] : null;
            var issuesList = issues as List<Dictionary<string, object>>;
            string firstType = null, firstReason = null;
            if (issuesList != null && issuesList.Count > 0)
            {
                firstType = issuesList[0].ContainsKey("type") ? Convert.ToString(issuesList[0]["type"]) : null;
                firstReason = issuesList[0].ContainsKey("reason") ? Convert.ToString(issuesList[0]["reason"]) : null;
            }
            var suggestion = v.ContainsKey("suggestion") ? Convert.ToString(v["suggestion"]) : null;
            if (string.IsNullOrWhiteSpace(suggestion) && issuesList != null)
                foreach (var iss in issuesList)
                    if (iss.ContainsKey("suggestion") && !string.IsNullOrWhiteSpace(Convert.ToString(iss["suggestion"])))
                    { suggestion = Convert.ToString(iss["suggestion"]); break; }

            items.Add(new Dictionary<string, object>
            {
                { "id", r.Id }, { "source", r.Source }, { "target", r.Target },
                { "score", score }, { "verdict", verdict },
                { "type", firstType }, { "reason", firstReason }, { "suggestion", suggestion },
                { "issues", issues },
            });
        }

        private static IEnumerable<object> SegCtx(BilingualSegment r, List<BilingualSegment> rows, Dictionary<string, int> indexOf)
        {
            if (!indexOf.TryGetValue(r.Id, out var idx))
            {
                yield return ""; yield return "";
                yield break;
            }
            var prev = idx > 0 ? rows[idx - 1].Target : "";
            var next = idx < rows.Count - 1 ? rows[idx + 1].Source : "";
            yield return Clip(prev);
            yield return Clip(next);
        }

        internal static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Trim();
            return text.Length <= 300 ? text : text.Substring(0, 300) + "…";
        }

        private static string BuildReviewPrompt(string lang, string termsText,
            IReadOnlyList<KeyValuePair<string, string>> termPairs)
        {
            var termRules = string.IsNullOrWhiteSpace(termsText)
                ? "（无可用术语表）"
                : termsText;
            return
                "你是专业翻译审校。将逐段审校并只返回 JSON 数组（不要 markdown、不要额外文字）。" +
                "目标语言: " + (string.IsNullOrEmpty(lang) ? "未知" : lang) + "。\n" +
                "术语表(严格: 出现 from 必须用 to，违反判red):\n" + termRules + "\n" +
                "审校规则:\n" +
                "- 漏译/错译/数字单位错误/术语违背 -> verdict=\"red\"\n" +
                "- 仅标点/大小写/空格/换行等安全可改 -> verdict=\"amber\"，且提供完整修正译文在 suggestion\n" +
                "- 无明显问题 -> verdict=\"ok\"\n" +
                "- 一律给 score(0-100)、issues 数组(元素: type, reason, suggestion)。amber/red 必须给 suggestion(完整目标译文，含占位保持原序)。\n" +
                "- 保持源文中的占位符(如 [[1]])原位原样。\n" +
                "输入数组元素含 {id, source, target, prev, next}。返回形如 [{\"id\":\"1\",\"score\":88,\"verdict\":\"ok\",\"issues\":[]}]。";
        }

        internal static string CallLlmJson(ToolkitConfig config, string prompt, List<Dictionary<string, string>> input)
        {
            var messages = new List<object>
            {
                new Dictionary<string, object> { { "role", "system" }, { "content", prompt } },
                new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(input) },
                },
            };
            var body = new Dictionary<string, object>
            {
                { "model", config.LlmModel },
                { "temperature", 0 },
                { "max_tokens", 4000 },
                { "messages", messages },
            };
            var url = config.LlmBaseUrl.TrimEnd('/') + "/chat/completions";
            var response = EngineHttp.PostJsonAsync(url, body, config.ApiKey, CancellationToken.None).GetAwaiter().GetResult();
            var choices = EngineHttp.AsList(response.TryGetValue("choices", out var c) ? c : null);
            if (choices == null || choices.Count == 0)
                throw new InvalidOperationException("LLM 未返回 choices");
            var message = EngineHttp.AsDict(
                EngineHttp.AsDict(choices[0])?.TryGetValue("message", out var m) == true ? m : null);
            if (message == null) throw new InvalidOperationException("LLM 未返回 message");
            return (EngineHttp.AsString(message.TryGetValue("content", out var ct) ? ct : null) ?? string.Empty).Trim();
        }

        /// <summary>从 LLM 回复里安全抽取 JSON 数组（容忍 ``` 包裹与前后废话）。</summary>
        internal static List<Dictionary<string, object>> JsonArray(string content)
        {
            var result = new List<Dictionary<string, object>>();
            if (string.IsNullOrWhiteSpace(content)) return result;
            int start = content.IndexOf('[');
            if (start < 0) return result;
            int end = content.LastIndexOf(']');
            if (end < start) return result;
            var json = content.Substring(start, end - start + 1);
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var arr = ser.Deserialize<List<object>>(json);
                foreach (var o in arr)
                    if (o is Dictionary<string, object> d) result.Add(d);
            }
            catch (Exception) { }
            return result;
        }

        private static string BuildReviewCsv(List<Dictionary<string, object>> items)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append('\ufeff');
            sb.Append("id,score,verdict,type,reason,suggestion,source,target\r\n");
            foreach (var it in items)
                sb.Append(BilingualParser.Csv(Str(it, "id"))).Append(',').Append(BilingualParser.Csv(Convert.ToString(it["score"])))
                  .Append(',').Append(BilingualParser.Csv(Str(it, "verdict"))).Append(',').Append(BilingualParser.Csv(Str(it, "type")))
                  .Append(',').Append(BilingualParser.Csv(Str(it, "reason"))).Append(',').Append(BilingualParser.Csv(Str(it, "suggestion")))
                  .Append(',').Append(BilingualParser.Csv(Str(it, "source"))).Append(',').Append(BilingualParser.Csv(Str(it, "target")))
                  .Append("\r\n");
            return sb.ToString();
        }

        /// <summary>GET /api/review/result?id=...：返回最近一次审校任务的 summar (保留位/交给 UI 拉取)。</summary>
        private static ApiResult ReviewResult(Dictionary<string, string> query)
        {
            return ApiResult.Json(404, Error("审校结果为同步返回，无需单独拉取（POST /api/review 的响应含 items/csv）。"));
        }

        internal static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { { "error", message } };
        }
    }
}
