using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using TradosToolkit.Common;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.FileConvert
{
    /// <summary>一次「任意文件 → sdlxliff」转换的结果。</summary>
    public sealed class ConvertOutcome
    {
        public string File;
        public string Output;
        public string SourceLang;
        public string TargetLang;
        public string ProjectPath;
        public List<string> Messages = new List<string>();
    }

    /// <summary>
    /// 任意受 Studio 文件类型系统支持的源文件 → 单个目标语言的 .sdlxliff。
    /// 实现：临时目录静默创建项目 → AddFiles → Scan / ConvertToTranslatableFormat /
    /// CopyToTargetLanguages → 把产出的目标 sdlxliff 复制到输出，临时项目默认清理。
    /// 只用公开的项目自动化 API；对项目对象模型的访问会 marshal 回 Studio UI 线程。
    /// UI（ConvertWindow）与 HTTP（POST /api/convert）共用这一份实现。
    /// </summary>
    public static class SdlxliffConverter
    {
        /// <summary>
        /// 转换入口。参数非法/文件不存在时抛异常，由调用方转成合适的提示。
        /// 可在任意线程调用（内部会切到 Studio UI 线程执行）。
        /// </summary>
        public static ConvertOutcome Convert(string file, string sourceLang, string targetLang,
                                             string output, string templatePath, bool keepProject,
                                             Action<string> log = null)
        {
            if (string.IsNullOrEmpty(file)) throw new ArgumentException("输入文件不能为空", nameof(file));
            if (string.IsNullOrEmpty(sourceLang)) throw new ArgumentException("源语言不能为空", nameof(sourceLang));
            if (string.IsNullOrEmpty(targetLang)) throw new ArgumentException("目标语言不能为空", nameof(targetLang));
            if (!File.Exists(file)) throw new FileNotFoundException("输入文件不存在: " + file, file);

            var fullInput = Path.GetFullPath(file);
            if (string.IsNullOrEmpty(Path.GetExtension(fullInput)))
                throw new InvalidOperationException("输入文件没有扩展名，无法判定文件类型: " + fullInput);

            if (string.IsNullOrEmpty(output))
                output = Path.Combine(Path.GetDirectoryName(fullInput) ?? ".",
                                      Path.GetFileNameWithoutExtension(fullInput) + ".sdlxliff");
            output = Path.GetFullPath(output);

            return RunOnStudioUi(() => ConvertCore(fullInput, sourceLang, targetLang, output,
                                                   templatePath, keepProject, log));
        }

        private static ConvertOutcome ConvertCore(string fullInput, string sourceLang, string targetLang,
                                                  string output, string templatePath, bool keepProject,
                                                  Action<string> log)
        {
            var workDir = Path.Combine(Path.GetTempPath(), "TradosToolkit", "convert",
                                       Guid.NewGuid().ToString("N"));
            var outcome = new ConvertOutcome
            {
                File = fullInput,
                Output = output,
                SourceLang = sourceLang,
                TargetLang = targetLang,
            };
            try
            {
                SweepStaleWorkDirs();
                var template = ResolveTemplate(templatePath, sourceLang, targetLang);
                Directory.CreateDirectory(workDir);

                var info = new ProjectInfo
                {
                    Name = FileKitSanitize(Path.GetFileNameWithoutExtension(fullInput)) + "-convert",
                    Description = "converted by TradosToolkit",
                    SourceLanguage = new Language(sourceLang),
                    TargetLanguages = new[] { new Language(targetLang) },
                    LocalProjectFolder = workDir,
                };

                log?.Invoke("创建临时项目：" + workDir);
                var project = new FileBasedProject(info, template);
                var added = project.AddFiles(new[] { fullInput });
                var ids = added.Select(f => f.Id).ToArray();
                project.SetFileRole(ids, FileRole.Translatable);

                foreach (var taskTemplateId in new[]
                         {
                             AutomaticTaskTemplateIds.Scan,
                             AutomaticTaskTemplateIds.ConvertToTranslatableFormat,
                             AutomaticTaskTemplateIds.CopyToTargetLanguages,
                         })
                {
                    log?.Invoke("执行任务：" + taskTemplateId);
                    var prepareTask = project.RunAutomaticTask(ids, taskTemplateId);
                    foreach (var m in prepareTask.Messages ?? new ExecutionMessage[0])
                    {
                        var text = MessageText(m);
                        if (!string.IsNullOrWhiteSpace(text)) outcome.Messages.Add(text);
                    }
                }
                project.Save();

                var targetFile = project.GetTargetLanguageFiles().FirstOrDefault();
                if (targetFile == null || string.IsNullOrEmpty(targetFile.LocalFilePath)
                    || !File.Exists(targetFile.LocalFilePath))
                    throw new InvalidOperationException(
                        "转换未产出目标文件: " + string.Join(" | ", outcome.Messages));

                var outDir = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                File.Copy(targetFile.LocalFilePath, output, true);

                outcome.ProjectPath = project.FilePath;
                log?.Invoke("已产出：" + output);
                return outcome;
            }
            finally
            {
                if (!keepProject)
                    TryCleanWorkDir(workDir);
            }
        }

        /// <summary>删除临时项目目录；只读文件会导致 Directory.Delete 失败（Studio 产出的文件常带只读），
        /// 因此先清只读属性再删，仍失败则告警并交给下次运行的过期清理兜底，避免 %TEMP% 无限累积。</summary>
        private static void TryCleanWorkDir(string workDir)
        {
            try
            {
                if (!Directory.Exists(workDir)) return;
                Directory.Delete(workDir, true);
                return;
            }
            catch (Exception e)
            {
                ToolkitLog.Warn("convert: 直接删除临时项目目录失败，改清只读属性后重试 " + workDir, e);
            }

            try
            {
                foreach (var file in Directory.GetFiles(workDir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* 单个文件失败不影响其余 */ }
                }
                Directory.Delete(workDir, true);
            }
            catch (Exception e)
            {
                ToolkitLog.Warn("convert: 清理临时项目目录失败（留待过期清理） " + workDir, e);
            }
        }

        /// <summary>清掉上次残留的临时项目（默认保留 7 天），兜底防止 %TEMP%\TradosToolkit\convert 无限增长。</summary>
        private static void SweepStaleWorkDirs()
        {
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "TradosToolkit", "convert");
                if (!Directory.Exists(root)) return;
                var deadline = DateTime.Now.AddDays(-7);
                foreach (var dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        if (Directory.GetLastWriteTime(dir) < deadline) TryCleanWorkDir(dir);
                    }
                    catch (Exception e) { ToolkitLog.Warn("convert: 过期临时项目清理失败 " + dir, e); }
                }
            }
            catch (Exception e)
            {
                ToolkitLog.Warn("convert: 扫描过期临时项目失败", e);
            }
        }

        /// <summary>
        /// 解析项目模板：显式给了路径就用它；否则按语言对在 Studio 模板里挑一个，挑不到用第一个。
        /// 须在 Studio UI 线程执行（访问 ProjectsController）。
        /// </summary>
        public static ProjectTemplateReference ResolveTemplate(string templatePath, string sourceLang, string targetLang)
        {
            if (!string.IsNullOrEmpty(templatePath))
            {
                if (!File.Exists(templatePath))
                    throw new FileNotFoundException("模板文件不存在", templatePath);
                return new ProjectTemplateReference(templatePath);
            }

            var templates = ProjectsController().GetProjectTemplates().ToList();
            if (templates.Count == 0)
                throw new InvalidOperationException("本机没有项目模板，请传 template 路径或先在 Studio 制作模板");

            var pairMark = sourceLang + "-" + targetLang;
            var match = templates.FirstOrDefault(t =>
                            (t.Name ?? string.Empty).IndexOf(pairMark, StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? templates.First();
            if (match.Uri == null)
                throw new InvalidOperationException("默认模板没有本地路径: " + match.Name);
            return new ProjectTemplateReference(match.Uri.LocalPath);
        }

        private static ProjectsController ProjectsController()
        {
            return SdlTradosStudio.Application.GetController<ProjectsController>();
        }

        private static T RunOnStudioUi<T>(Func<T> action)
        {
            var app = System.Windows.Application.Current;
            if (app == null)
                return action();
            return app.Dispatcher.Invoke(action);
        }

        private static string FileKitSanitize(string name)
        {
            return FileKit.SanitizeFileName(name, string.Empty);
        }

        private static string MessageText(object message)
        {
            var text = message.GetType().GetProperty("Message");
            return (text == null ? message.ToString() : text.GetValue(message, null) as string) ?? string.Empty;
        }
    }
}
