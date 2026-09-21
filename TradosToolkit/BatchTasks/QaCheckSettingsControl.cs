using System;
using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;

namespace TradosToolkit.BatchTasks
{
    /// <summary>
    /// QA 批处理任务设置页 UI：勾选式检查项开关。
    /// </summary>
    public class QaCheckSettingsControl : UserControl, ISettingsAware<QaCheckSettings>
    {
        private readonly FlowLayoutPanel _panel;
        private readonly CheckBox _chkUntranslated;
        private readonly CheckBox _chkNumbers;
        private readonly CheckBox _chkPunctuation;
        private readonly CheckBox _chkTermAdoption;
        private readonly CheckBox _chkConsistency;
        private bool _loading;

        public QaCheckSettingsControl()
        {
            AutoSize = true;
            Padding = new Padding(12);
            _panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Padding = new Padding(0),
            };

            _chkUntranslated = MakeCheck("检查漏译 / 空译（目标为空或与源文一致）", s => Settings.CheckUntranslated = s);
            _chkNumbers = MakeCheck("检查数字一致性（源文数字必须出现在译文）", s => Settings.CheckNumbers = s);
            _chkPunctuation = MakeCheck("检查标点一致性（结尾终结标点对齐）", s => Settings.CheckPunctuation = s);
            _chkTermAdoption = MakeCheck("检查术语未采用（对照本地术语库 pre 词）", s => Settings.CheckTermAdoption = s);
            _chkConsistency = MakeCheck("检查重复段译法一致性", s => Settings.CheckConsistency = s);

            _panel.Controls.Add(_chkUntranslated);
            _panel.Controls.Add(_chkNumbers);
            _panel.Controls.Add(_chkPunctuation);
            _panel.Controls.Add(_chkTermAdoption);
            _panel.Controls.Add(_chkConsistency);
            Controls.Add(_panel);
        }

        public QaCheckSettings Settings
        {
            get => _settings;
            set
            {
                _settings = value;
                LoadFromSettings();
            }
        }
        private QaCheckSettings _settings;

        private CheckBox MakeCheck(string text, Action<bool> writeBack)
        {
            var box = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            box.CheckedChanged += (s, e) =>
            {
                if (_loading || _settings == null) return;
                writeBack(box.Checked);
            };
            return box;
        }

        private void LoadFromSettings()
        {
            if (_settings == null) return;
            _loading = true;
            try
            {
                _chkUntranslated.Checked = _settings.CheckUntranslated;
                _chkNumbers.Checked = _settings.CheckNumbers;
                _chkPunctuation.Checked = _settings.CheckPunctuation;
                _chkTermAdoption.Checked = _settings.CheckTermAdoption;
                _chkConsistency.Checked = _settings.CheckConsistency;
            }
            finally
            {
                _loading = false;
            }
        }
    }
}