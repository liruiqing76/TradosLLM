using Sdl.Core.Settings;

namespace TradosToolkit.BatchTasks
{
    /// <summary>
    /// QA 批处理任务设置：各检查项开关，默认全开。
    /// </summary>
    public class QaCheckSettings : SettingsGroup
    {
        /// <summary>漏译 / 空译：目标为空或与源文完全一致。</summary>
        public bool CheckUntranslated
        {
            get => GetSetting<bool>(nameof(CheckUntranslated)).Value;
            set => GetSetting<bool>(nameof(CheckUntranslated)).Value = value;
        }

        /// <summary>数字一致性：源文中的数字必须出现在译文。</summary>
        public bool CheckNumbers
        {
            get => GetSetting<bool>(nameof(CheckNumbers)).Value;
            set => GetSetting<bool>(nameof(CheckNumbers)).Value = value;
        }

        /// <summary>标点一致性：结尾终结标点须与源文一致。</summary>
        public bool CheckPunctuation
        {
            get => GetSetting<bool>(nameof(CheckPunctuation)).Value;
            set => GetSetting<bool>(nameof(CheckPunctuation)).Value = value;
        }

        /// <summary>术语未采用：源文命中的术语库建议译法须出现在译文。</summary>
        public bool CheckTermAdoption
        {
            get => GetSetting<bool>(nameof(CheckTermAdoption)).Value;
            set => GetSetting<bool>(nameof(CheckTermAdoption)).Value = value;
        }

        /// <summary>重复段一致性：相同源文的译文须保持一致。</summary>
        public bool CheckConsistency
        {
            get => GetSetting<bool>(nameof(CheckConsistency)).Value;
            set => GetSetting<bool>(nameof(CheckConsistency)).Value = value;
        }

        protected override object GetDefaultValue(string settingId)
        {
            switch (settingId)
            {
                case nameof(CheckUntranslated):
                case nameof(CheckNumbers):
                case nameof(CheckPunctuation):
                case nameof(CheckTermAdoption):
                case nameof(CheckConsistency):
                    return true;
                default:
                    return base.GetDefaultValue(settingId);
            }
        }
    }
}
