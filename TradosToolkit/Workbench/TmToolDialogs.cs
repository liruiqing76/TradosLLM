using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TradosToolkit.TranslationMemories;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 记忆库工具的小型模态对话框（纯代码构建，跟随工作台浅色风格）。
    /// 新建库 / 合并库（含冲突策略）/ 重复条目结果列表。
    /// </summary>
    public static class TmToolDialogs
    {
        private static readonly string[] CommonLanguages =
            { "zh-CN", "zh-TW", "en-US", "en-GB", "ja-JP", "ko-KR", "de-DE", "fr-FR", "ru-RU", "es-ES", "pt-BR", "it-IT" };

        /// <summary>新建空库对话框；true = 已创建（out 回填信息）。</summary>
        public static bool CreateTm(Window owner, out string filePath, out string name,
            out CultureInfo source, out CultureInfo target)
        {
            filePath = name = null; source = target = null;
            string picked = null;
            string donePath = null, doneName = null;
            CultureInfo doneSrc = null, doneTgt = null;

            var (win, addRow) = BuildDialog(owner, UiText.T("WB_Mem_Tool_New"), 480);
            var pathBox = AddPathRow(addRow, UiText.T("WB_Mem_New_Path"), out var browse);
            var nameBox = AddTextRow(addRow, UiText.T("WB_Mem_New_Name"), string.Empty);
            var srcBox = AddLangRow(addRow, UiText.T("WB_Mem_New_Src"), "zh-CN");
            var tgtBox = AddLangRow(addRow, UiText.T("WB_Mem_New_Tgt"), "en-US");
            var error = NewError();
            var (ok, cancel, buttons) = BuildFooter(UiText.T("WB_Mem_Ok"), UiText.T("WB_Mem_Cancel"));

            browse.Click += (s, e) =>
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "Trados TM (*.sdltm)|*.sdltm",
                    FileName = (string.IsNullOrWhiteSpace(nameBox.Text) ? "new_tm" : nameBox.Text.Trim()) + ".sdltm",
                    Title = UiText.T("WB_Mem_Tool_New"),
                };
                if (dlg.ShowDialog(owner) == true)
                {
                    picked = dlg.FileName;
                    pathBox.Text = picked;
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                        nameBox.Text = Path.GetFileNameWithoutExtension(picked);
                }
            };
            ok.Click += (s, e) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(picked)) throw new InvalidOperationException(UiText.T("WB_Mem_Err_NoPath"));
                    if (File.Exists(picked)) throw new InvalidOperationException(UiText.T("WB_Mem_Err_Exists"));
                    var src = new CultureInfo(srcBox.Text.Trim());
                    var tgt = new CultureInfo(tgtBox.Text.Trim());
                    if (string.Equals(src.Name, tgt.Name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(UiText.T("WB_Mem_Err_Pair"));
                    TmToolkit.CreateNew(picked, nameBox.Text.Trim(), src, tgt);
                    donePath = picked; doneName = nameBox.Text.Trim(); doneSrc = src; doneTgt = tgt;
                    win.DialogResult = true;
                }
                catch (Exception ex)
                {
                    error.Text = ex.Message;
                }
            };

            Finish(win, error, buttons);
            if (win.ShowDialog() != true) return false;
            filePath = donePath; name = doneName; source = doneSrc; target = doneTgt;
            return true;
        }

        /// <summary>合并向导；true = 用户点了确定（policy/targetPath 已回填）。</summary>
        public static bool MergeDialog(Window owner, IList<LocalTmInfo> sources, out string targetPath,
            out MergeConflictPolicy policy)
        {
            targetPath = null; policy = MergeConflictPolicy.KeepNewest;
            string picked = null;
            string doneTarget = null;
            int donePolicy = 0;

            var (win, addRow) = BuildDialog(owner, UiText.T("WB_Mem_Tool_Merge"), 520);
            var sourceList = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 130,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF6)),
                Text = UiText.Tf("WB_Mem_Merge_Count", sources.Count) + "\n" +
                       string.Join("\n", sources.Select(s => "· " + s.Name + "  (" + s.LanguagePair + ")")),
            };
            addRow(sourceList, UiText.T("WB_Mem_Merge_Sources"));
            var pathBox = AddPathRow(addRow, UiText.T("WB_Mem_Merge_Target"), out var browse);
            var policyBox = new ComboBox();
            policyBox.Items.Add(UiText.T("WB_Policy_Newest"));
            policyBox.Items.Add(UiText.T("WB_Policy_Oldest"));
            policyBox.Items.Add(UiText.T("WB_Policy_First"));
            policyBox.SelectedIndex = 0;
            addRow(policyBox, UiText.T("WB_Mem_Merge_Policy"));
            var error = NewError();
            var (ok, cancel, buttons) = BuildFooter(UiText.T("WB_Mem_Ok"), UiText.T("WB_Mem_Cancel"));

            browse.Click += (s, e) =>
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "Trados TM (*.sdltm)|*.sdltm",
                    FileName = "merged_" + Path.GetFileNameWithoutExtension(sources[0].Name) + ".sdltm",
                    Title = UiText.T("WB_Mem_Merge_Target"),
                };
                if (dlg.ShowDialog(owner) == true) { picked = dlg.FileName; pathBox.Text = picked; }
            };
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(picked)) { error.Text = UiText.T("WB_Mem_Err_NoPath"); return; }
                if (sources.Any(n => string.Equals(Path.GetFileName(n.FilePath), Path.GetFileName(picked),
                        StringComparison.OrdinalIgnoreCase)))
                { error.Text = UiText.T("WB_Mem_Err_TargetIsSource"); return; }
                doneTarget = picked;
                donePolicy = Math.Max(0, policyBox.SelectedIndex);
                win.DialogResult = true;
            };

            Finish(win, error, buttons);
            if (win.ShowDialog() != true) return false;
            targetPath = doneTarget;
            policy = (MergeConflictPolicy)donePolicy;
            return true;
        }

        /// <summary>重复条目结果窗口。</summary>
        public static void ShowDuplicates(Window owner, string tmName, IList<TmDuplicateGroup> groups)
        {
            var (win, addRow) = BuildDialog(owner, UiText.T("WB_Mem_Tool_Dupes") + " · " + tmName, 640);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddHeader(grid, UiText.T("WB_Mem_Dupes_Count"), 0);
            AddHeader(grid, UiText.T("WB_Mem_Dupes_Source"), 1);
            AddHeader(grid, UiText.T("WB_Mem_Dupes_Targets"), 2);
            for (var i = 0; i < groups.Count && i < 500; i++)
            {
                var g = groups[i];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddCell(grid, g.Count.ToString("N0"), i + 1, 0);
                AddCell(grid, Clip(g.SourceText, 90), i + 1, 1);
                AddCell(grid, Clip(string.Join(" ⧸ ", g.Targets), 140), i + 1, 2);
            }
            var scroller = new ScrollViewer
            {
                Content = grid,
                MaxHeight = 340,
                Margin = new Thickness(0, 4, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            addRow(scroller, UiText.Tf("WB_Mem_Result_Dupes", groups.Count));
            var close = new Button
            {
                Content = UiText.T("WB_Mem_Close"),
                IsDefault = true,
                MinWidth = 84,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(12, 5, 12, 5),
            };
            close.Click += (s, e) => win.Close();
            ((StackPanel)win.Content).Children.Add(close);
            win.ShowDialog();
        }

        // ---------- helpers ----------

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "-";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static (Window win, Action<UIElement, string> addRow) BuildDialog(Window owner, string title, double width)
        {
            var win = new Window
            {
                Title = title,
                Width = width,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                Owner = owner,
                ShowInTaskbar = false,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 13,
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFD)),
                ResizeMode = ResizeMode.NoResize,
            };
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
            win.Content = root;
            Action<UIElement, string> addRow = (control, label) =>
            {
                if (!string.IsNullOrEmpty(label))
                    root.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 3) });
                root.Children.Add(control);
            };
            return (win, addRow);
        }

        private static void Finish(Window win, TextBlock error, UIElement buttons)
        {
            ((StackPanel)win.Content).Children.Add(error);
            ((StackPanel)win.Content).Children.Add(buttons);
        }

        private static TextBlock NewError() => new TextBlock
        {
            Foreground = Brushes.Firebrick,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        private static (Button ok, Button cancel, StackPanel panel) BuildFooter(string okText, string cancelText)
        {
            var ok = new Button { Content = okText, IsDefault = true, MinWidth = 84, Padding = new Thickness(12, 5, 12, 5) };
            var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 84, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            panel.Children.Add(ok);
            panel.Children.Add(cancel);
            return (ok, cancel, panel);
        }

        private static TextBox AddTextRow(Action<UIElement, string> addRow, string label, string text)
        {
            var box = new TextBox { Text = text, FontSize = 12, Padding = new Thickness(4, 5, 4, 5) };
            addRow(box, label);
            return box;
        }

        /// <summary>只读路径框 + 浏览按钮一行；返回路径 TextBox，out 回传按钮供挂 Click。</summary>
        private static TextBox AddPathRow(Action<UIElement, string> addRow, string label, out Button browse)
        {
            var box = new TextBox
            {
                IsReadOnly = true,
                FontSize = 12,
                Padding = new Thickness(4, 5, 4, 5),
                Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF6)),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            browse = new Button { Content = UiText.T("WB_Mem_Btn_Browse"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(box, 0);
            Grid.SetColumn(browse, 1);
            grid.Children.Add(box);
            grid.Children.Add(browse);
            addRow(grid, label);
            return box;
        }

        private static ComboBox AddLangRow(Action<UIElement, string> addRow, string label, string initial)
        {
            var box = new ComboBox { IsEditable = true, Text = initial };
            foreach (var lang in CommonLanguages) box.Items.Add(lang);
            addRow(box, label);
            return box;
        }

        private static void AddHeader(Grid grid, string text, int col)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 8, 4) };
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }

        private static void AddCell(Grid grid, string text, int row, int col)
        {
            var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 3, 8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
    }
}
