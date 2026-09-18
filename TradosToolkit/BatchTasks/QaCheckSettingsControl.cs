using System;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;

namespace TradosToolkit.BatchTasks
{
    /// <summary>
    /// QA 任务设置页 UI（M5 里程碑实现，WinForms 控件）。
    /// </summary>
    public class QaCheckSettingsControl : UserControl, ISettingsAware<QaCheckSettings>
    {
        public QaCheckSettings Settings
        {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }
    }
}
