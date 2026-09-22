using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using TradosToolkit.Diagnostics;
using TradosToolkit.Server;
using TradosToolkit.TranslationMemories;
using TradosToolkit.TranslationProvider.Engines;

namespace TradosToolkit.Inbox
{
    /// <summary>
    /// 「拖入即产出」编排：把一个源文件按 0..6 走完
    /// 匹配本地库 → 建项目 → 套库预翻译 → 分析统计 → 报告 → 交付包(.sdlppx) → 导出匹配库。
    /// 本方法在后台线程运行；所有 Studio 对象模型调用由 ProjectApi 内部 marshal 回 UI 线程。
    /// 每一步的起止都写回 InboxJob（可绑定界面），异常降级为步骤失败并中止后续步骤。
    /// </summary>
    internal static class InboxOrchestrator
    {
        private const int ExportBatch = 500;

        public static void Run(InboxJob job, ToolkitConfig cfg)
        {
            var current = -1;
            string projectPath = null, projectName = null, tmPath = null, tmName = null;
            var haveTm = false;

            try
            {
                job.AppendLog("开始处理：" + job.FileName);

                // ---- 0 匹配本地记忆库 ----
                current = 0;
                job.BeginStep(0, "扫描本地记忆库目录…");
                haveTm = TryMatchTm(cfg, job, out tmPath, out tmName);

                // ---- 1 创建项目 ----
                current = 1;
                job.BeginStep(1, "创建 Studio 项目…");
                projectPath = CreateProject(job, cfg, out projectName);
                job.EndStep(1, true, Path.GetFileName(projectPath));
                job.AppendLog("已创建项目：" + projectPath);

                // ---- 2 套库预翻译 ----
                current = 2;
                if (haveTm)
                {
                    job.BeginStep(2, "附加本地库并预翻译…");
                    RunTask(job, projectPath, "pretranslate", tmPath);
                    job.EndStep(2, true, "预翻译完成");
                    job.AppendLog("套库预翻译完成：" + tmName);
                }
                else
                {
                    job.SkipStep(2, "无匹配本地库，跳过预翻译");
                }

                // ---- 3 分析统计 ----
                current = 3;
                job.BeginStep(3, "运行分析统计…");
                RunTask(job, projectPath, "analyze", null);
                job.EndStep(3, true, "分析完成");

                // ---- 4 生成分析报告 ----
                current = 4;
                job.BeginStep(4, "生成分析报告…");
                WriteReport(job, projectPath);
                job.EndStep(4, true, Path.GetFileName(job.ReportPath));
                job.AppendLog("分析报告：" + job.ReportPath);

                // ---- 5 生成交付包 ----
                current = 5;
                job.BeginStep(5, "打包 .sdlppx…");
                MakePackage(job, projectPath, projectName);
                job.EndStep(5, true, Path.GetFileName(job.PackagePath));
                job.AppendLog("交付包：" + job.PackagePath);

                // ---- 6 导出匹配记忆库 ----
                current = 6;
                if (haveTm)
                {
                    job.BeginStep(6, "导出匹配记忆库…");
                    ExportMatchedTm(job, projectPath, tmPath, tmName);
                    job.EndStep(6, true, Path.GetFileName(job.TmPath));
                    job.AppendLog("匹配记忆库：" + job.TmPath);
                }
                else
                {
                    job.SkipStep(6, "无匹配本地库，跳过导出");
                }

                job.MarkDone("三件套已生成 · " + job.JobFolder);
                job.AppendLog("全部完成，产出目录：" + job.JobFolder);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("收件箱任务失败 " + job.FileName, e);
                job.AppendLog("失败：" + e.Message);
                if (current >= 0) job.EndStep(current, false, e.Message);
                for (var i = current + 1; i < job.Steps.Count; i++)
                    if (job.Steps[i].Status == "pending") job.SkipStep(i, "未执行");
                job.MarkFailed(e.Message);
            }
        }

        // ==================== 步骤 0：匹配本地库 ====================

        private static bool TryMatchTm(ToolkitConfig cfg, InboxJob job, out string tmPath, out string tmName)
        {
            tmPath = null;
            tmName = null;

            var root = (cfg.TmScanDirectory ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                job.SkipStep(0, "未配置记忆库目录（tmScanDirectory）");
                job.AppendLog("未配置本地记忆库目录，跳过匹配。");
                return false;
            }

            string want;
            try
            {
                // 本地库扫描出的语言对是 CultureInfo.Name（ISO 代码，如 "zh-CN → en-US"）
                var srcName = System.Globalization.CultureInfo.GetCultureInfo(cfg.InboxSourceLang).Name;
                var tgtName = System.Globalization.CultureInfo.GetCultureInfo(cfg.InboxTargetLang).Name;
                want = srcName + " → " + tgtName;
            }
            catch (Exception e)
            {
                job.SkipStep(0, "语言代码无效：" + cfg.InboxSourceLang + "/" + cfg.InboxTargetLang);
                ToolkitLog.Error("收件箱：语言代码解析失败", e);
                return false;
            }

            var tms = LocalTmScanner.Scan(root, null, CancellationToken.None);
            var hit = tms.FirstOrDefault(t => t.State == LocalTmState.Ok &&
                                              string.Equals(t.LanguagePair, want, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                job.SkipStep(0, "未找到语言对 " + want + " 的可用本地库");
                job.AppendLog("未找到匹配的本地记忆库：" + want + "（已扫描 " + tms.Count + " 个）。");
                return false;
            }

            tmPath = hit.FilePath;
            tmName = hit.Name;
            job.EndStep(0, true, hit.Name);
            job.AppendLog("匹配本地记忆库：" + hit.Name + "（" + hit.LanguagePair + "）");
            return true;
        }

        // ==================== 步骤 1：创建项目 ====================

        private static string CreateProject(InboxJob job, ToolkitConfig cfg, out string projectName)
        {
            var baseName = Path.GetFileNameWithoutExtension(job.FileName);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var outputRoot = ResolveOutputRoot(cfg);

            var jobFolder = Path.Combine(outputRoot, ProjectApi.SanitizeFileName(baseName) + "_" + stamp);
            Directory.CreateDirectory(jobFolder);
            job.JobFolder = jobFolder;

            // 源文件挪进任务目录：收件箱保持干净，且项目与产物自包含（避免重复触发）
            var sourceDir = Path.Combine(jobFolder, "Source");
            Directory.CreateDirectory(sourceDir);
            var sourcePath = Path.Combine(sourceDir, job.FileName);
            try
            {
                File.Move(job.FilePath, sourcePath);
            }
            catch
            {
                File.Copy(job.FilePath, sourcePath, true);
                try { File.Delete(job.FilePath); } catch { /* 删除失败不影响流程 */ }
            }

            projectName = ProjectApi.SanitizeFileName(baseName) + "_" + stamp;
            var projectRoot = ResolveProjectRoot(cfg, outputRoot);
            var projectFolder = Path.Combine(projectRoot, projectName);

            var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "name", projectName },
                { "folder", projectFolder },
                { "sourceLang", cfg.InboxSourceLang },
                { "targetLangs", new List<string> { cfg.InboxTargetLang } },
                { "files", new List<string> { sourcePath } },
                { "openInStudio", false },
            });

            var result = ProjectApi.CreateProject(body);
            if (result == null || result.Status < 200 || result.Status >= 300)
                throw new InvalidOperationException("创建项目失败：" + ErrorOf(result));

            var payload = result.Payload as Dictionary<string, object>;
            var projectPath = payload != null && payload.ContainsKey("projectPath")
                ? payload["projectPath"] as string
                : null;
            if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath))
                throw new InvalidOperationException("项目文件未生成");
            return projectPath;
        }

        // ==================== 步骤 2/3：自动任务 ====================

        private static void RunTask(InboxJob job, string projectPath, string taskKey, string providerTmPath)
        {
            var query = new Dictionary<string, string> { { "path", projectPath } };
            var request = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(providerTmPath))
            {
                var uri = FileBasedTranslationMemory.GetFileBasedTranslationMemoryUri(providerTmPath);
                request["providerUri"] = uri.AbsoluteUri;
            }

            var result = ProjectApi.WithProject(query, (project, info) =>
                ProjectApi.ExecuteTaskOnProject(project, taskKey, ProjectApi.TaskTemplates[taskKey], query, request));

            if (result == null || result.Status < 200 || result.Status >= 300)
                throw new InvalidOperationException(taskKey + " 失败：" + ErrorOf(result));

            var payload = result.Payload as Dictionary<string, object>;
            var messages = payload != null && payload.ContainsKey("messages") ? payload["messages"] as System.Collections.IEnumerable : null;
            if (messages != null)
                foreach (var m in messages)
                {
                    var text = Convert.ToString(m);
                    if (!string.IsNullOrWhiteSpace(text)) job.AppendLog("  · " + text);
                }
        }

        // ==================== 步骤 4：分析报告 ====================

        private static void WriteReport(InboxJob job, string projectPath)
        {
            var query = new Dictionary<string, string>
            {
                { "path", projectPath },
                { "format", "csv" },
            };
            var result = ProjectApi.Report(query);
            if (result == null || result.Status < 200 || result.Status >= 300)
                throw new InvalidOperationException("生成报告失败：" + ErrorOf(result));
            if (result.Bytes == null || result.Bytes.Length == 0)
                throw new InvalidOperationException("报告内容为空");

            var name = string.IsNullOrEmpty(result.DownloadName) ? "wordcount.csv" : result.DownloadName;
            var path = Path.Combine(job.JobFolder, name);
            File.WriteAllBytes(path, result.Bytes);
            job.ReportPath = path;
        }

        // ==================== 步骤 5：交付包 ====================

        private static void MakePackage(InboxJob job, string projectPath, string projectName)
        {
            var query = new Dictionary<string, string> { { "path", projectPath } };
            var outPath = Path.Combine(job.JobFolder, projectName + ".sdlppx");
            var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "out", outPath },
                { "packageName", projectName },
                { "comment", "TradosToolkit 收件箱自动交付" },
            });

            var result = ProjectApi.Package("POST", query, body);
            if (result == null || result.Status < 200 || result.Status >= 300)
                throw new InvalidOperationException("打包失败：" + ErrorOf(result));

            var payload = result.Payload as Dictionary<string, object>;
            var packagePath = payload != null && payload.ContainsKey("packagePath")
                ? payload["packagePath"] as string
                : null;
            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
                throw new InvalidOperationException("交付包未生成");
            job.PackagePath = packagePath;
        }

        // ==================== 步骤 6：导出匹配记忆库 ====================

        /// <summary>
        /// 从匹配到的本地库中，抽出被本文档命中的翻译单元，另存为一个「本文档专用」小库；
        /// 抽不到命中的句段时退化为整库复制，保证三件套始终齐全。
        /// </summary>
        private static void ExportMatchedTm(InboxJob job, string projectPath, string tmPath, string tmName)
        {
            var baseName = ProjectApi.SanitizeFileName(Path.GetFileNameWithoutExtension(tmName ?? "matched"));
            var outPath = Path.Combine(job.JobFolder, "匹配记忆库_" + baseName + ".sdltm");

            HashSet<string> keys = null;
            try
            {
                var bilingual = FindBilingual(projectPath);
                if (bilingual != null)
                {
                    keys = new HashSet<string>();
                    foreach (var seg in BilingualParser.Parse(bilingual))
                    {
                        var key = SegmentDedup.Normalize(seg.Source ?? string.Empty);
                        if (key.Length > 0) keys.Add(key);
                    }
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Error("收件箱：抽取本文档句段失败", e);
            }

            if (keys == null || keys.Count == 0)
            {
                File.Copy(tmPath, outPath, true);
                job.TmPath = outPath;
                job.AppendLog("未取到本文档句段，改为整库复制（" + Path.GetFileName(outPath) + "）。");
                return;
            }

            var source = new FileBasedTranslationMemory(tmPath);
            var sourceDir = source.LanguageDirection;
            var all = TmToolkit.ReadAll(source, null, CancellationToken.None);
            var keep = all.Where(tu => tu != null && tu.SourceSegment != null &&
                                       keys.Contains(SegmentDedup.Normalize(tu.SourceSegment.ToPlain()))).ToList();

            if (keep.Count == 0)
            {
                File.Copy(tmPath, outPath, true);
                job.TmPath = outPath;
                job.AppendLog("本文档句段未命中该库，改为整库复制（" + Path.GetFileName(outPath) + "）。");
                return;
            }

            TmToolkit.CreateNew(outPath, Path.GetFileNameWithoutExtension(outPath),
                sourceDir.SourceLanguage, sourceDir.TargetLanguage);

            var target = new FileBasedTranslationMemory(outPath);
            var targetDir = target.LanguageDirection;
            var settings = new ImportSettings();
            for (var i = 0; i < keep.Count; i += ExportBatch)
            {
                var chunk = keep.Skip(i).Take(ExportBatch).ToArray();
                var mask = chunk.Select(_ => true).ToArray();
                targetDir.AddTranslationUnitsMasked(chunk, settings, mask);
            }
            target.Save();

            job.TmPath = outPath;
            job.AppendLog("命中并导出 " + keep.Count + " 条翻译单元（库内共 " + all.Count + " 条）。");
        }

        // ==================== 辅助 ====================

        private static string FindBilingual(string projectPath)
        {
            var folder = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            return Directory.GetFiles(folder, "*.sdlxliff", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static string ResolveOutputRoot(ToolkitConfig cfg)
        {
            var root = (cfg.InboxOutputFolder ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "TradosToolkit 收件箱");
            Directory.CreateDirectory(root);
            return root;
        }

        private static string ResolveProjectRoot(ToolkitConfig cfg, string outputRoot)
        {
            var root = (cfg.InboxProjectRoot ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(root)) root = Path.Combine(outputRoot, "Projects");
            Directory.CreateDirectory(root);
            return root;
        }

        private static string ErrorOf(ApiResult result)
        {
            if (result == null) return "无返回";
            var payload = result.Payload as Dictionary<string, object>;
            if (payload != null && payload.ContainsKey("error"))
                return Convert.ToString(payload["error"]);
            return "HTTP " + result.Status;
        }
    }
}
