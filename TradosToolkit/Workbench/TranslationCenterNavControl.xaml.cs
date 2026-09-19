using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 翻译中心 View 自带左栏（GetExplorerBarControl 宿主）：Chrome 式书签管理。
    /// 目录可新建/重命名/删除，"☆收藏"把当前页存进选中目录，"⇩导出"生成
    /// Chrome/Edge 可导入的 Netscape 书签 HTML；全部即时持久化 config.json。
    /// </summary>
    public partial class TranslationCenterNavControl : UserControl
    {
        private readonly TranslationCenterBrowserControl _browser;
        private List<ToolkitConfig.BookmarkFolder> _folders;
        private ToolkitConfig.BookmarkFolder _selected;
        private readonly HashSet<ToolkitConfig.BookmarkFolder> _collapsed =
            new HashSet<ToolkitConfig.BookmarkFolder>();

        public TranslationCenterNavControl(TranslationCenterBrowserControl browser)
        {
            ToolkitLog.Info("翻译中心书签左栏构建");
            _browser = browser;
            InitializeComponent();
            Reload();
        }

        private void Reload()
        {
            try
            {
                _folders = ToolkitConfig.Load().Folders;
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心书签读取失败，用默认", ex);
                _folders = ToolkitConfig.DefaultFolders();
            }
            if (_folders.Count == 0) _folders.Add(new ToolkitConfig.BookmarkFolder { name = "常用地址" });
            if (_selected == null || !_folders.Contains(_selected)) _selected = _folders[0];
            Rebuild();
        }

        private void Persist()
        {
            try
            {
                ToolkitConfig.Save(folders: _folders);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心书签保存失败", ex);
                NavStatus.Text = "保存失败：" + ex.Message;
            }
        }

        private static readonly Brush SelectedBrush =
            new SolidColorBrush(Color.FromRgb(0xCC, 0xE6, 0xFF));
        private static readonly Brush NormalBrush = Brushes.Transparent;

        private void Rebuild()
        {
            FolderPanel.Children.Clear();
            foreach (var folder in _folders)
            {
                var f = folder;
                var arrow = _collapsed.Contains(f) ? "▸" : "▾";
                var header = new Button
                {
                    Content = arrow + " " + f.name + "（" + f.items.Count + "）",
                    Style = (Style)Resources["FolderHeaderButton"],
                    Background = ReferenceEquals(f, _selected) ? SelectedBrush : NormalBrush,
                    ToolTip = "点击选中目录（收藏进这里）；右键可重命名/删除",
                };
                header.Click += (s, e) => { _selected = f; Rebuild(); };
                var rename = new MenuItem { Header = "重命名目录" };
                rename.Click += (s, e) => RenameFolder(f);
                var delete = new MenuItem { Header = "删除目录" };
                delete.Click += (s, e) => DeleteFolder(f);
                var fMenu = new ContextMenu();
                fMenu.Items.Add(rename);
                fMenu.Items.Add(delete);
                header.ContextMenu = fMenu;
                FolderPanel.Children.Add(header);

                if (!_collapsed.Contains(f))
                {
                    foreach (var item in f.items)
                    {
                        var b = item;
                        var label = new TextBlock
                        {
                            Text = string.IsNullOrWhiteSpace(b.name) ? b.url : b.name,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        };
                        var button = new Button
                        {
                            Content = label,
                            Style = (Style)Resources["BookmarkButton"],
                            ToolTip = b.url,
                            Margin = new Thickness(14, 0, 0, 4),
                        };
                        button.Click += (s, e) => _browser.NavigateFromBookmark(b.url);
                        var remove = new MenuItem { Header = "删除此地址" };
                        remove.Click += (s, e) => { f.items.Remove(b); Persist(); Rebuild(); };
                        var bMenu = new ContextMenu();
                        bMenu.Items.Add(remove);
                        button.ContextMenu = bMenu;
                        FolderPanel.Children.Add(button);
                    }
                }
            }
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var name = InputDialog.Show(Window.GetWindow(this), "新建目录", "目录名称：", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            var folder = new ToolkitConfig.BookmarkFolder { name = name.Trim() };
            _folders.Add(folder);
            _selected = folder;
            Persist();
            Rebuild();
            NavStatus.Text = "已新建目录：" + folder.name;
        }

        private void RenameFolder(ToolkitConfig.BookmarkFolder folder)
        {
            var name = InputDialog.Show(Window.GetWindow(this), "重命名目录", "目录名称：", folder.name);
            if (string.IsNullOrWhiteSpace(name)) return;
            folder.name = name.Trim();
            Persist();
            Rebuild();
            NavStatus.Text = "已重命名为：" + folder.name;
        }

        private void DeleteFolder(ToolkitConfig.BookmarkFolder folder)
        {
            var confirm = MessageBox.Show(
                "删除目录“" + folder.name + "”及其 " + folder.items.Count + " 个书签？",
                "翻译中心书签", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            _folders.Remove(folder);
            _collapsed.Remove(folder);
            Persist();
            Reload();
            NavStatus.Text = "已删除目录：" + folder.name;
        }

        private void AddBookmark_Click(object sender, RoutedEventArgs e)
        {
            string url, title;
            if (!_browser.TryGetCurrentPage(out url, out title))
            {
                NavStatus.Text = "浏览器还没就绪或当前没有页面。";
                return;
            }
            var target = _selected ?? _folders[0];
            if (target.items.Exists(b => string.Equals(b.url, url, StringComparison.OrdinalIgnoreCase)))
            {
                NavStatus.Text = "“" + target.name + "”里已有当前页。";
                return;
            }
            target.items.Add(new ToolkitConfig.BookmarkItem { name = title, url = url });
            _collapsed.Remove(target);
            Persist();
            Rebuild();
            NavStatus.Text = "已收藏到“" + target.name + "”：" + title;
            ToolkitLog.Info("翻译中心收藏: 目录=" + target.name + " " + title + " = " + url);
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出全部书签",
                    FileName = "trados-toolkit-书签.html",
                    Filter = "书签 HTML（Chrome/Edge 可导入）|*.html",
                };
                if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
                File.WriteAllText(dialog.FileName, BuildChromeHtml(), Encoding.UTF8);
                NavStatus.Text = "已导出 " + _folders.Count + " 个目录到 " + dialog.FileName;
                ToolkitLog.Info("翻译中心书签导出: " + dialog.FileName);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心书签导出失败", ex);
                NavStatus.Text = "导出失败：" + ex.Message;
            }
        }

        /// <summary>Netscape 书签文件格式：Chrome/Edge 书签管理器"导入"直接识别。</summary>
        private string BuildChromeHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\r\n");
            sb.Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\r\n");
            sb.Append("<TITLE>Bookmarks</TITLE>\r\n");
            sb.Append("<H1>Bookmarks</H1>\r\n");
            sb.Append("<DL><p>\r\n");
            foreach (var folder in _folders)
            {
                sb.Append("    <DT><H3>").Append(Escape(folder.name)).Append("</H3>\r\n");
                sb.Append("    <DL><p>\r\n");
                foreach (var item in folder.items)
                {
                    sb.Append("        <DT><A HREF=\"").Append(EscapeAttr(item.url)).Append("\">")
                      .Append(Escape(string.IsNullOrWhiteSpace(item.name) ? item.url : item.name))
                      .Append("</A>\r\n");
                }
                sb.Append("    </DL><p>\r\n");
            }
            sb.Append("</DL><p>\r\n");
            return sb.ToString();
        }

        private static string Escape(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string EscapeAttr(string s) =>
            Escape(s).Replace("\"", "&quot;");
    }

    /// <summary>轻量输入框（新建/重命名目录用），取消返回 null。</summary>
    internal static class InputDialog
    {
        public static string Show(Window owner, string title, string prompt, string initial)
        {
            var box = new TextBox
            {
                Text = initial ?? "",
                Margin = new Thickness(0, 6, 0, 12),
                MinHeight = 26,
                Padding = new Thickness(4, 2, 4, 2),
            };
            var ok = false;
            var okButton = new Button { Content = "确定", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "取消", MinWidth = 72, IsCancel = true };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = prompt });
            panel.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            panel.Children.Add(buttons);

            var window = new Window
            {
                Title = title,
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = owner,
                MinWidth = 280,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            okButton.Click += (s, e) => { ok = true; window.Close(); };
            box.Focus();
            box.SelectAll();
            window.ShowDialog();
            return ok && !string.IsNullOrWhiteSpace(box.Text) ? box.Text : null;
        }
    }
}
