using System;
using Sdl.FileTypeSupport.Framework.IntegrationApi;
using Sdl.ProjectAutomation.AutomaticTasks;
using Sdl.ProjectAutomation.Core;

namespace TradosToolkit.BatchTasks
{
    /// <summary>
    /// QA/批处理任务入口（M5 里程碑实现：术语一致性、漏译检测等）。
    /// 在 ConfigureConverter 中挂载自定义双语处理器即可逐句检查。
    /// </summary>
    [AutomaticTask(
        "TradosToolkit.QaCheckTask_ID",
        "Qa_Task_Name",
        "Qa_Task_Description",
        GeneratedFileType = AutomaticTaskFileType.BilingualTarget)]
    [AutomaticTaskSupportedFileType(AutomaticTaskFileType.BilingualTarget)]
    [RequiresSettings(typeof(QaCheckSettings), typeof(QaCheckSettingsPage))]
    public class QaCheckBatchTask : AbstractFileContentProcessingAutomaticTask
    {
        protected override void ConfigureConverter(ProjectFile projectFile, IMultiFileConverter multiFileConverter)
        {
            var settings = GetSetting<QaCheckSettings>();
            multiFileConverter.AddBilingualProcessor(new QaCheckProcessor(settings));
        }
    }
}
