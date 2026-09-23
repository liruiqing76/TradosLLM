using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TradosToolkit.Common;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.FileConvert
{
    /// <summary>
    /// 「任意文件 → sdlxliff」的简单操作界面：选源文件、指定语向、可选输出/模板，一键转换并看日志。
    /// 界面只是外壳——转换逻辑在 <see cref="SdlxliffConverter"/>，与 POST /api/convert 共用同一份实现。
    /// </summary>
    public partial class ConvertWindow : Window
    {
        private static ConvertWindow _instance;
        private readonly List<LangItem> _langs;
        private string _lastOutput;

        public ConvertWindow()
        {
            InitializeComponent();
            InputProbe.Attach(this);

            _langs = LanguageCatalog.All();
            SrcCombo.ItemsSource = _langs;
            TgtCombo.ItemsSource = _langs;
            SrcCombo.SelectedValue = LanguageCatalog.DefaultSource;
            TgtCombo.SelectedValue = LanguageCatalog.DefaultTarget;

            Append("选择要转换的源文件，指定源语言/目标语言，点「开始转换」。");
            Append("输入必须是 Studio 文件类型系统支持的格式（docx/xlsx/pptx/txt/xml/html 等）。");
        }

        public static void ShowOrActivate()
        {
            var cur = _instance;
            if (cur != null)
            {
                cur.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    cur.Activate();
                    if (cur.WindowState == WindowState.Minimized) cur.WindowState = WindowState.Normal;
                }));
                return;
            }
            ToolkitLog.Info("文件转换：新建独立页（专用 UI 线程）");
            // 同术语管理/收件箱：Studio 宿主消息泵不为外挂顶层窗口 TranslateMessage，
            // 留在宿主线程 Show() 的窗口收不到 WM_CHAR（输入框敲不进字）。放专用 STA 线程跑 WPF 自己的泵。
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    var w = new ConvertWindow();
                    _instance = w;
                    w.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(_instance, w)) _instance = null;
                        w.Dispatcher.InvokeShutdown();
                    };
                    ready.Set();
                    w.Show();
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    ToolkitLog.Error("文件转换：独立线程创建失败", ex);
                    ready.Set();
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Name = "TradosToolkit.ConvertUI";
            t.Start();
            ready.WaitOne(TimeSpan.FromSeconds(15));
        }

        private void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要转换的源文件",
                Filter = "所有文件|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            InputBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(OutputBox.Text))
                OutputBox.Text = DefaultOutput(dlg.FileName);
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "选择输出 sdlxliff 位置",
                Filter = "SDLXLIFF|*.sdlxliff|所有文件|*.*",
                DefaultExt = ".sdlxliff",
                OverwritePrompt = true,
            };
            var input = InputBox.Text?.Trim();
            if (!string.IsNullOrEmpty(input))
                dlg.FileName = Path.GetFileName(DefaultOutput(input));
            if (dlg.ShowDialog(this) == true) OutputBox.Text = dlg.FileName;
        }

        private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 Studio 项目模板（可留空）",
                Filter = "Studio 项目模板|*.sdlpt;*.sdlproj|所有文件|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) == true) TemplateBox.Text = dlg.FileName;
        }

        private async void Convert_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var input = InputBox.Text?.Trim();
                var src = SrcCombo.SelectedValue as string;
                var tgt = TgtCombo.SelectedValue as string;

                if (string.IsNullOrEmpty(input) || !File.Exists(input))
                {
                    Append("× 请选择一个存在的输入文件");
                    return;
                }
                if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt))
                {
                    Append("× 请选择源语言和目标语言");
                    return;
                }
                if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase))
                {
                    Append("× 源语言与目标语言相同，无需转换");
                    return;
                }

                var output = OutputBox.Text?.Trim();
                var template = TemplateBox.Text?.Trim();
                var keep = KeepBox.IsChecked == true;

                ConvertBtn.IsEnabled = false;
                OpenOutBtn.IsEnabled = false;
                StatusText.Text = "转换中…";
                Append("开始转换：" + input);

                try
                {
                    var outcome = await Task.Run(() => SdlxliffConverter.Convert(
                        input, src, tgt, output, template, keep,
                        line => Dispatcher.BeginInvoke(new System.Action(() => Append("  · " + line)))));

                    _lastOutput = outcome.Output;
                    foreach (var m in outcome.Messages) Append("  ! " + m);
                    Append("√ 已产出：" + outcome.Output);
                    StatusText.Text = "完成";
                    OpenOutBtn.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    Append("× 转换失败：" + ex.Message);
                    StatusText.Text = "失败";
                    ToolkitLog.Error("ConvertWindow：转换失败", ex);
                }
                finally
                {
                    ConvertBtn.IsEnabled = true;
                }
            }
            catch (Exception ex) { ToolkitLog.Error("ConvertWindow.Convert_Click 异常", ex); }
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_lastOutput) && File.Exists(_lastOutput))
                    Process.Start("explorer.exe", "/select,\"" + _lastOutput + "\"");
                else if (!string.IsNullOrEmpty(_lastOutput))
                    Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(_lastOutput) + "\"");
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("ConvertWindow：打开输出位置失败", ex);
            }
        }

        private static string DefaultOutput(string input)
        {
            return Path.Combine(Path.GetDirectoryName(input) ?? ".",
                                Path.GetFileNameWithoutExtension(input) + ".sdlxliff");
        }

        private void Append(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
    }
}
