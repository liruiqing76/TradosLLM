using Sdl.Core.Settings;

namespace TradosToolkit.Verification
{
    /// <summary>
    /// 原生验证器设置：各检查项开关，默认全开。
    /// 与 QaCheckSettings 同构但独立（验证器走 ISettingsBundle，批处理走 BatchTask settings）。
    /// </summary>
    public class ToolkitQaVerifierSettings : SettingsGroup
    {
        public bool CheckUntranslated
        {
            get => GetSetting<bool>(nameof(CheckUntranslated)).Value;
            set => GetSetting<bool>(nameof(CheckUntranslated)).Value = value;
        }

        public bool CheckNumbers
        {
            get => GetSetting<bool>(nameof(CheckNumbers)).Value;
            set => GetSetting<bool>(nameof(CheckNumbers)).Value = value;
        }

        public bool CheckPunctuation
        {
            get => GetSetting<bool>(nameof(CheckPunctuation)).Value;
            set => GetSetting<bool>(nameof(CheckPunctuation)).Value = value;
        }

        public bool CheckTermAdoption
        {
            get => GetSetting<bool>(nameof(CheckTermAdoption)).Value;
            set => GetSetting<bool>(nameof(CheckTermAdoption)).Value = value;
        }

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
