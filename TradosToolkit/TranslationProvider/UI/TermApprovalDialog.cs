using System.Windows;
using System.Windows.Controls;

namespace TradosToolkit.TranslationProvider.UI
{
    /// <summary>
    /// 术语挖掘审批确认对话框：批准前让用户编辑"最终术语译法"。
    /// 纯代码构建（无 XAML），避免在 csproj 加 Page 项。
    /// 预填 LLM 建议译法（ProposedTerm），用户可修改后确认。
    /// </summary>
    internal class TermApprovalDialog : Window
    {
        private readonly TextBox _termBox;
        public string FinalTerm { get; private set; }

        internal TermApprovalDialog(string candidateTerm, string proposedTerm, string example)
        {
            Title = "批准术语";
            Width = 420;
            Height = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock
            {
                Text = "源术语（候选）：",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            panel.Children.Add(new TextBlock
            {
                Text = candidateTerm ?? string.Empty,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            });

            if (!string.IsNullOrEmpty(example))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "例句：",
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 4),
                });
                var exBox = new TextBox
                {
                    Text = example.Length > 200 ? example.Substring(0, 200) + "…" : example,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12),
                    BorderBrush = System.Windows.Media.Brushes.LightGray,
                };
                panel.Children.Add(exBox);
            }

            panel.Children.Add(new TextBlock
            {
                Text = "最终术语译法（可编辑）：",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            _termBox = new TextBox
            {
                Text = proposedTerm ?? string.Empty,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 16),
                Padding = new Thickness(4),
            };
            panel.Children.Add(_termBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var okBtn = new Button
            {
                Content = "✓ 确认批准",
                Padding = new Thickness(16, 4, 16, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
            };
            okBtn.Click += (s, e) =>
            {
                FinalTerm = _termBox.Text?.Trim();
                DialogResult = true;
            };

            var cancelBtn = new Button
            {
                Content = "取消",
                Padding = new Thickness(16, 4, 16, 4),
                IsCancel = true,
            };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            Content = panel;
        }
    }
}
