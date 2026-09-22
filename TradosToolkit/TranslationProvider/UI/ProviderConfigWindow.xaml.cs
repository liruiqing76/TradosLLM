using System;
using System.Windows;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationProvider.UI
{
    /// <summary>
    /// 提供程序配置窗口：TM 地址来自 config.json（只读展示），
    /// 用户只填 LLM 回退参数（可留空 = 仅用 TM）。
    /// </summary>
    public partial class ProviderConfigWindow : Window
    {
        private readonly string _defaultSrc;
        private readonly string _defaultTgt;

        public ProviderConfigWindow(string defaultSrc = "zh-CN", string defaultTgt = "en-US")
        {
            ToolkitLog.Info("ProviderConfigWindow ctor: " + defaultSrc + "->" + defaultTgt);
            InitializeComponent();
            _defaultSrc = defaultSrc;
            _defaultTgt = defaultTgt;

            var config = ToolkitConfig.Load();
            if (!string.IsNullOrEmpty(config.LlmBaseUrl)) BaseUrlBox.Text = config.LlmBaseUrl;
            if (!string.IsNullOrEmpty(config.LlmModel)) ModelBox.Text = config.LlmModel;
            InitOriginCombo(config.LlmOrigin);
            if (string.IsNullOrEmpty(config.TmUrl))
            {
                TmStatusLabel.Text = "未配置（编辑 %APPDATA%\\TradosToolkit\\config.json 的 tmUrl）";
                TmDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x5D, 0x4B));
            }
            else
            {
                TmStatusLabel.Text = config.TmUrl;
                TmDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xA8, 0x6B));
            }

            if (string.IsNullOrEmpty(config.ApiKey))
            {
                ApiKeyStatusLabel.Text = "当前未保存任何 API Key";
                ApiKeyDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x5D, 0x4B));
            }
            else
            {
                ApiKeyStatusLabel.Text = "已保存 API Key（长度 " + config.ApiKey.Length + "），留空则沿用";
                ApiKeyDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xA8, 0x6B));
            }
        }

        /// <summary>
        /// 译文来源标识下拉：Value(Tag)=Studio 枚举名，Label=编辑器实际显示文案。
        /// 只列 Studio 2019 枚举真实存在的值（该版本没有 Nmt）。
        /// </summary>
        private void InitOriginCombo(string current)
        {
            var opts = new[]
            {
                new OriginOption("AdaptiveMachineTranslation", "AdaptiveMT 自动化翻译（AI 标识）"),
                new OriginOption("MachineTranslation", "自动翻译"),
                new OriginOption("TM", "翻译记忆库"),
                new OriginOption("Unknown", "无标识（像普通译文）"),
                new OriginOption("Alignment", "自动对齐"),
                new OriginOption("ContextTM", "上下文匹配"),
            };
            OriginCombo.ItemsSource = opts;
            var want = ToolkitConfig.NormalizeLlmOrigin(current);
            OriginCombo.SelectedItem = Array.Find(opts, o => o.Tag == want) ?? opts[0];
        }

        private class OriginOption
        {
            public OriginOption(string tag, string label) { Tag = tag; Label = label; }
            public string Tag { get; }
            public string Label { get; }
            public override string ToString() => Label;
        }

        public bool HasTm => !string.IsNullOrEmpty(ToolkitConfig.Load().TmUrl);
        public bool HasLlm => !string.IsNullOrWhiteSpace(BaseUrlBox.Text);

        public string BaseUrl => BaseUrlBox.Text.Trim();
        public string Model => ModelBox.Text.Trim();

        public bool UsePreGlossary => PreGlossaryCheck.IsChecked == true;
        public bool UsePostGlossary => PostGlossaryCheck.IsChecked == true;
        public bool SupportsTags => TagsCheck.IsChecked == true;

        private void OpenGlossaryManager(object sender, RoutedEventArgs e)
        {
            new GlossaryManagerWindow(_defaultSrc, _defaultTgt) { Owner = this }.ShowDialog();
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.OpenFolder();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var typedKey = ApiKeyBox.Password.Trim();
            ToolkitLog.Info("ProviderConfigWindow OK: baseUrl=" + BaseUrl + " model=" + Model +
                            " hasTm=" + HasTm + " keyInput=" + (typedKey.Length > 0 ? "(新填,长度" + typedKey.Length + ")" : "(留空)") +
                            " pre=" + UsePreGlossary + " post=" + UsePostGlossary + " tags=" + SupportsTags);

            if (!HasTm && !HasLlm)
            {
                MessageBox.Show(this,
                    "TM 服务未配置，且未填写 LLM 参数，至少需要配置一项。\n" +
                    "TM：编辑 %APPDATA%\\TradosToolkit\\config.json 的 tmUrl。",
                    "TradosToolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (HasLlm)
            {
                if (string.IsNullOrWhiteSpace(Model))
                {
                    MessageBox.Show(this, "请填写模型名（例如 high_quality，此处不是 API Key）。",
                        "TradosToolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrEmpty(typedKey) && string.IsNullOrEmpty(ToolkitConfig.Load().ApiKey))
                {
                    MessageBox.Show(this, "请填写 API Key（将自动保存到 config.json，只需填一次）。",
                        "TradosToolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            try
            {
                ToolkitConfig.Save(
                    typedKey.Length > 0 ? typedKey : null,
                    HasLlm ? BaseUrl : null,
                    HasLlm ? Model : null,
                    llmOrigin: (OriginCombo.SelectedItem as OriginOption)?.Tag);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存配置失败：" + ex.Message, "TradosToolkit",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            DialogResult = true;
        }
    }
}
