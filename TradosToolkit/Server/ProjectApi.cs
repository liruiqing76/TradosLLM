using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;

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
                case "/api/project/report":
                    return Report(query);
                case "/api/project/tmfiles":
                    return TmFiles(query);
                case "/api/project/package":
                    return Package(method, query, body);
                case "/api/file":
                    return DownloadFile(query);
                default:
                    return ApiResult.Json(404, Error("未知端点 " + path));
            }
        }

        private static ApiResult Status()
        {
            return ApiResult.Json(200, new Dictionary<string, object>
            {
                { "product", "TradosToolkit" },
                { "studio", Environment.Is64BitProcess ? "x64" : "x86" },
                { "version", typeof(ProjectApi).Assembly.GetName().Version.ToString() },
                { "port", ToolkitApiServer.Instance.Port },
                { "startedAt", ToolkitApiServer.Instance.StartedAt },
                { "listening", ToolkitApiServer.Instance.IsListening },
            });
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
            return WithProject(query, (project, _) =>
            {
                var sources = project.GetSourceLanguageFiles().Select(DescribeFile).ToList();
                var targets = project.GetTargetLanguageFiles().Select(DescribeFile).ToList();
                return ApiResult.Json(200, new Dictionary<string, object>
                {
                    { "source", sources },
                    { "target", targets },
                });
            });
        }

        private static Dictionary<string, object> DescribeFile(ProjectFile file)
        {
            return new Dictionary<string, object>
            {
                { "id", file.Id },
                { "name", file.Name },
                { "role", file.Role.ToString() },
                { "isSource", file.IsSource },
                { "language", file.Language == null ? null : file.Language.IsoAbbreviation },
                { "path", file.LocalFilePath },
                { "bilingualPath", file.BilingualReferenceFileLocalPath },
            };
        }

        /// <summary>
        /// POST /api/project/task?path=...&task=pretranslate|analyze|...&files=id1,id2(可选)
        /// body 可选 {"providerUri":"tradostoolkit://...","providerState":""}：
        /// 预翻译前把该提供程序写入项目所有目标语言的 TM 配置。
        /// </summary>
        private static ApiResult RunTask(string method, Dictionary<string, string> query, string body)
        {
            if (method != "POST")
                return ApiResult.Json(405, Error("task 端点需 POST"));

            var taskKey = query.TryGetValue("task", out var t) ? t : null;
            if (taskKey == null || !TaskTemplates.TryGetValue(taskKey, out var templateId))
                return ApiResult.Json(400, Error("task 需为: " + string.Join("|", TaskTemplates.Keys)));

            var request = ParseBody(body);
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

        private static ApiResult Report(Dictionary<string, string> query)
        {
            return WithProject(query, (project, _) =>
            {
                var stats = project.GetProjectStatistics();
                var perTarget = (stats == null ? new TargetLanguageStatistics[0] : stats.TargetLanguageStatistics)
                    .Select(MapStatistics)
                    .ToList();
                return ApiResult.Json(200, perTarget);
            });
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
