using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.ProjectAutomation.Core;
using Sdl.ProjectAutomation.FileBased;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using TradosToolkit.Diagnostics;
using TradosToolkit.Workbench;

namespace TradosToolkit.Action
{
    /// <summary>
    /// 首页 Home 选项卡"从模板新建项目"按钮：弹窗口选 Studio 项目模板 + 源文件，
    /// 用 FileBasedProject 编程式创建项目，Add 到 Studio 并自动打开。
    /// Studio 15 兼容（不依赖 2019 SR2 的 OpenNewProjectWizardEvent）。
    /// </summary>
    [Action("TradosToolkit_NewFromTemplate", Name = "NewFromTemplate_Action_Name", Description = "NewFromTemplate_Action_Description", Icon = "NewFromTemplate_Action_Icon")]
    [ActionLayout(typeof(ToolkitWorkbenchRibbonGroup), 25, DisplayType.Large)]
    public class NewFromTemplateAction : AbstractAction
    {
        protected override void Execute()
        {
            ToolkitLog.Info("Home 功能区按钮：从模板新建项目");
            var result = NewFromTemplateDialog.Prompt();
            if (result == null) return;

            try
            {
                var projectsCtrl = SdlTradosStudio.Application.GetController<ProjectsController>();

                // 解析 Studio 项目模板
                var template = ResolveStudioTemplate(projectsCtrl, result.StudioTemplatePath,
                    result.SourceLang, result.TargetLangs);
                if (template == null)
                {
                    ToolkitLog.Error("NewFromTemplateAction：未找到 Studio 项目模板");
                    return;
                }

                // 构建项目信息
                var info = new ProjectInfo
                {
                    Name = result.ProjectName,
                    SourceLanguage = new Language(result.SourceLang),
                    TargetLanguages = result.TargetLangs != null && result.TargetLangs.Count > 0
                        ? result.TargetLangs.Select(l => new Language(l)).ToArray()
                        : null,
                    LocalProjectFolder = result.ProjectFolder,
                };

                var project = new FileBasedProject(info, template);

                // 添加源文件（可选）
                var validFiles = (result.FilePaths ?? new List<string>()).Where(f => File.Exists(f)).ToList();
                Guid[] fileIds;
                if (validFiles.Count > 0)
                {
                    var added = project.AddFiles(validFiles.ToArray());
                    fileIds = added.Select(f => f.Id).ToArray();
                    project.SetFileRole(fileIds, FileRole.Translatable);
                }
                else
                {
                    fileIds = Array.Empty<Guid>();
                }

                // 运行 Scan（自动任务）
                if (fileIds.Length > 0)
                {
                    try
                    {
                        var scanTask = project.RunAutomaticTask(fileIds, AutomaticTaskTemplateIds.Scan);
                    }
                    catch (Exception scanEx)
                    {
                        ToolkitLog.Warn("NewFromTemplateAction：Scan 自动任务失败（非致命）", scanEx);
                    }
                }

                project.Save();
                ToolkitLog.Info("从模板创建项目成功：" + result.ProjectName);

                // 注册到 Studio 项目列表并打开
                try
                {
                    projectsCtrl.Add(project.FilePath);
                    projectsCtrl.Open(project);
                    ToolkitLog.Info("已在 Studio 中打开项目：" + result.ProjectName);
                }
                catch (Exception openEx)
                {
                    ToolkitLog.Warn("NewFromTemplateAction：打开项目失败（项目已创建）", openEx);
                }
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("NewFromTemplateAction 异常", ex);
            }
        }

        /// <summary>
        /// 解析 Studio 项目模板：显式给了路径直接用；否则从 Studio 模板列表按语言代码匹配，匹配不到用第一个。
        /// </summary>
        private static ProjectTemplateReference ResolveStudioTemplate(
            ProjectsController projectsCtrl, string explicitPath, string sourceLang, List<string> targetLangs)
        {
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
                return new ProjectTemplateReference(explicitPath);

            var templates = projectsCtrl.GetProjectTemplates().ToList();
            if (templates.Count == 0)
                throw new InvalidOperationException("本机没有 Studio 项目模板（*.sdltpl），请先在 Studio 制作");

            // 先按源语言代码匹配，再退到任一目标语言代码，都匹配不到才用第一个。
            // 不再硬编码 "en-"：否则任何语向的项目都会挂上英文模板，可能语向不符。
            var srcCode = (sourceLang ?? string.Empty).Trim();
            var match = srcCode.Length > 0
                ? templates.FirstOrDefault(t => NameContains(t.Name, srcCode))
                : null;
            if (match == null && targetLangs != null)
            {
                foreach (var tgt in targetLangs)
                {
                    var code = (tgt ?? string.Empty).Trim();
                    if (code.Length == 0) continue;
                    match = templates.FirstOrDefault(t => NameContains(t.Name, code));
                    if (match != null) break;
                }
            }
            if (match == null) match = templates.First();
            ToolkitLog.Info("从模板新建：选用模板 " + match.Name + "（源语言 " + srcCode + "）");

            if (match.Uri == null)
                throw new InvalidOperationException("模板 '" + match.Name + "' 没有本地路径");
            return new ProjectTemplateReference(match.Uri.LocalPath);
        }

        private static bool NameContains(string name, string code)
        {
            return (name ?? string.Empty).IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
