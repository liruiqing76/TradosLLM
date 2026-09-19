using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 翻译中心 View 自带左栏（GetExplorerBarControl 宿主）：Chrome 式书签树。
    /// 目录可无限嵌套（TreeView 原生折叠），右键=新建子目录/重命名/删除/收藏到此，
    /// "☆收藏"弹窗选目录，"⇩导出"生成 Chrome/Edge 可导入的 Netscape 书签 HTML；
    /// 全部即时持久化 config.json 的 translationCenterFolders。
    /// </summary>
    public partial class TranslationCenterNavControl : UserControl
    {
        private readonly TranslationCenterBrowserControl _browser;
        private List<ToolkitConfig.BookmarkFolder> _folders;

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

        private void Rebuild()
        {
            BookmarkTree.Items.Clear();
            foreach (var folder in _folders) BookmarkTree.Items.Add(BuildFolderNode(folder, true));
        }

        private static int CountAll(ToolkitConfig.BookmarkFolder f)
        {
            var n = f.items.Count;
            foreach (var sub in f.folders) n += CountAll(sub);
            return n;
        }

        private TreeViewItem BuildFolderNode(ToolkitConfig.BookmarkFolder folder, bool expanded)
        {
            var header = new TextBlock
            {
                Text = "📁 " + folder.name + "（" + CountAll(folder) + "）",
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var node = new TreeViewItem { Header = header, Tag = folder, IsExpanded = expanded };

            foreach (var sub in folder.folders) node.Items.Add(BuildFolderNode(sub, false));
            foreach (var item in folder.items) node.Items.Add(BuildBookmarkNode(folder, item));

            var newSub = new MenuItem { Header = "新建子目录" };
            newSub.Click += (s, e) => CreateFolder(folder);
            var favHere = new MenuItem { Header = "收藏当前页到此目录" };
            favHere.Click += (s, e) => BookmarkTo(folder);
            var rename = new MenuItem { Header = "重命名" };
            rename.Click += (s, e) => RenameFolder(folder);
            var delete = new MenuItem { Header = "删除目录" };
            delete.Click += (s, e) => DeleteFolder(folder);
            var menu = new ContextMenu();
            menu.Items.Add(newSub);
            menu.Items.Add(favHere);
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            node.ContextMenu = menu;
            return node;
        }

        private TreeViewItem BuildBookmarkNode(ToolkitConfig.BookmarkFolder owner, ToolkitConfig.BookmarkItem item)
        {
            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.name) ? item.url : item.name,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var url = item.url;
            // TreeViewItem 在 MouseDown 就捕获鼠标，Up 的事件源是节点本身——处理器必须挂在节点上
            var node = new TreeViewItem { Header = label, Tag = item, ToolTip = url };
            node.PreviewMouseLeftButtonUp += (s, e) => _browser.NavigateFromBookmark(url);
            var remove = new MenuItem { Header = "删除此地址" };
            remove.Click += (s, e) =>
            {
                owner.items.Remove(item);
                Persist();
                Rebuild();
                NavStatus.Text = "已删除：" + (item.name ?? url);
            };
            var menu = new ContextMenu();
            menu.Items.Add(remove);
            node.ContextMenu = menu;
            return node;
        }

        /// <summary>当前选中的目录节点；未选中或选的是书签返回 null。</summary>
        private ToolkitConfig.BookmarkFolder SelectedFolder()
        {
            var node = BookmarkTree.SelectedItem as TreeViewItem;
            return node?.Tag as ToolkitConfig.BookmarkFolder;
        }

        /// <summary>在树里找 target 的宿主列表（根或某父目录的 folders），找不到返回 null。</summary>
        private List<ToolkitConfig.BookmarkFolder> FindHostList(
            List<ToolkitConfig.BookmarkFolder> candidates, ToolkitConfig.BookmarkFolder target)
        {
            if (candidates.Contains(target)) return candidates;
            foreach (var f in candidates)
            {
                var found = FindHostList(f.folders, target);
                if (found != null) return found;
            }
            return null;
        }

        private void CreateFolder(ToolkitConfig.BookmarkFolder parent)
        {
            var name = ToolkitDialogs.Input(Window.GetWindow(this),
                parent == null ? "新建目录" : "新建子目录", 
                parent == null ? "目录名称：" : "在“" + parent.name + "”下新建，目录名称：", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            var folder = new ToolkitConfig.BookmarkFolder { name = name.Trim() };
            if (parent == null) _folders.Add(folder);
            else parent.folders.Add(folder);
            Persist();
            Rebuild();
            NavStatus.Text = parent == null
                ? "已新建目录：" + folder.name
                : "已在“" + parent.name + "”下新建：" + folder.name;
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e) => CreateFolder(SelectedFolder());

        private void RenameFolder(ToolkitConfig.BookmarkFolder folder)
        {
            var name = ToolkitDialogs.Input(Window.GetWindow(this), "重命名目录", "目录名称：", folder.name);
            if (string.IsNullOrWhiteSpace(name)) return;
            folder.name = name.Trim();
            Persist();
            Rebuild();
            NavStatus.Text = "已重命名为：" + folder.name;
        }

        private void DeleteFolder(ToolkitConfig.BookmarkFolder folder)
        {
            var confirm = ToolkitDialogs.Confirm(Window.GetWindow(this), "删除目录",
                "确定删除目录“" + folder.name + "”及其全部 " + CountAll(folder) + " 个书签（含子目录）吗？此操作不可撤销。",
                danger: true);
            if (!confirm) return;
            var host = FindHostList(_folders, folder);
            if (host == null) return;
            host.Remove(folder);
            Persist();
            Rebuild();
            NavStatus.Text = "已删除目录：" + folder.name;
        }

        private void AddBookmark_Click(object sender, RoutedEventArgs e)
        {
            var target = PickFolder();
            if (target != null) BookmarkTo(target);
        }

        private void BookmarkTo(ToolkitConfig.BookmarkFolder folder)
        {
            string url, title;
            if (!_browser.TryGetCurrentPage(out url, out title))
            {
                NavStatus.Text = "浏览器还没就绪或当前没有页面。";
                return;
            }
            if (folder.items.Exists(b => string.Equals(b.url, url, StringComparison.OrdinalIgnoreCase)))
            {
                NavStatus.Text = "“" + folder.name + "”里已有当前页。";
                return;
            }
            folder.items.Add(new ToolkitConfig.BookmarkItem { name = title, url = url });
            Persist();
            Rebuild();
            NavStatus.Text = "已收藏到“" + folder.name + "”：" + title;
            ToolkitLog.Info("翻译中心收藏: 目录=" + folder.name + " " + title + " = " + url);
        }

        /// <summary>弹窗选目录（书签树单选），取消返回 null。</summary>
        private ToolkitConfig.BookmarkFolder PickFolder()
        {
            var tree = new TreeView { Margin = new Thickness(10), MinHeight = 220 };
            foreach (var f in _folders) tree.Items.Add(BuildPickNode(f));
            var ok = false;
            var okButton = new Button { Content = "确定", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "取消", MinWidth = 72, IsCancel = true };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10, 0, 10, 10),
            };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            var panel = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);
            panel.Children.Add(tree);

            var window = new Window
            {
                Title = "收藏到目录",
                Content = panel,
                Width = 300,
                Height = 360,
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml"),
            });
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml"),
            });
            okButton.Style = (Style)window.TryFindResource("ButtonPrimary");
            cancelButton.Style = (Style)window.TryFindResource("ButtonDefault");
            okButton.Click += (s, e) => { ok = true; window.Close(); };
            window.ShowDialog();
            if (!ok) return null;
            var node = tree.SelectedItem as TreeViewItem;
            return node?.Tag as ToolkitConfig.BookmarkFolder;
        }

        private static TreeViewItem BuildPickNode(ToolkitConfig.BookmarkFolder folder)
        {
            var node = new TreeViewItem
            {
                Header = "📁 " + folder.name,
                Tag = folder,
                IsExpanded = true,
            };
            foreach (var sub in folder.folders) node.Items.Add(BuildPickNode(sub));
            return node;
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
                NavStatus.Text = "已导出 " + _folders.Count + " 个根目录到 " + dialog.FileName;
                ToolkitLog.Info("翻译中心书签导出: " + dialog.FileName);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心书签导出失败", ex);
                NavStatus.Text = "导出失败：" + ex.Message;
            }
        }

        /// <summary>Netscape 书签文件格式（嵌套 DL）：Chrome/Edge 书签管理器"导入"直接识别。</summary>
        private string BuildChromeHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\r\n");
            sb.Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\r\n");
            sb.Append("<TITLE>Bookmarks</TITLE>\r\n");
            sb.Append("<H1>Bookmarks</H1>\r\n");
            AppendFolderTree(sb, _folders, 1);
            return sb.ToString();
        }

        private void AppendFolderTree(StringBuilder sb, List<ToolkitConfig.BookmarkFolder> folders, int depth)
        {
            var pad = new string(' ', depth * 4);
            sb.Append(pad).Append("<DL><p>\r\n");
            foreach (var folder in folders) AppendFolder(sb, folder, depth);
            sb.Append(pad).Append("</DL><p>\r\n");
        }

        /// <summary>标准 Netscape 层级：H3 后跟该目录自己的 DL（条目+子目录都在其中）。</summary>
        private void AppendFolder(StringBuilder sb, ToolkitConfig.BookmarkFolder folder, int depth)
        {
            var pad = new string(' ', depth * 4);
            sb.Append(pad).Append("    <DT><H3>").Append(Escape(folder.name)).Append("</H3>\r\n");
            var inner = new string(' ', (depth + 1) * 4);
            sb.Append(inner).Append("<DL><p>\r\n");
            foreach (var sub in folder.folders) AppendFolder(sb, sub, depth + 1);
            foreach (var item in folder.items)
            {
                sb.Append(inner).Append("    <DT><A HREF=\"").Append(EscapeAttr(item.url)).Append("\">")
                  .Append(Escape(string.IsNullOrWhiteSpace(item.name) ? item.url : item.name))
                  .Append("</A>\r\n");
            }
            sb.Append(inner).Append("</DL><p>\r\n");
        }

        private static string Escape(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string EscapeAttr(string s) =>
            Escape(s).Replace("\"", "&quot;");
    }

    /// <summary>HandyControl 风格轻量弹窗（输入/确认），与配置窗口同款观感，替代原生 MessageBox。</summary>
    internal static class ToolkitDialogs
    {
        private static void ApplyTheme(Window window)
        {
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml"),
            });
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml"),
            });
        }

        private static StackPanel ButtonRow(params Button[] buttons)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
            foreach (var b in buttons)
            {
                b.MinWidth = 76;
                b.Margin = new Thickness(8, 0, 0, 0);
                b.Padding = new Thickness(10, 5, 10, 5);
                row.Children.Add(b);
            }
            return row;
        }

        private static Window MakeWindow(Window owner, string title, FrameworkElement content)
        {
            var window = new Window
            {
                Title = title,
                Content = content,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Owner = owner,
                MinWidth = 300,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            ApplyTheme(window);
            return window;
        }

        /// <summary>确认弹窗；danger=true 时主按钮为红色"删除"。取消/关闭返回 false。</summary>
        public static bool Confirm(Window owner, string title, string message, bool danger = false)
        {
            var panel = new StackPanel { Margin = new Thickness(18, 16, 18, 16), MaxWidth = 340 };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            });
            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
            });
            var ok = false;
            var okButton = new Button { Content = danger ? "删除" : "确定", IsDefault = true };
            var cancelButton = new Button { Content = "取消", IsCancel = true };
            panel.Children.Add(ButtonRow(okButton, cancelButton));
            var window = MakeWindow(owner, title, panel);
            // HC 样式在窗口合并字典里（Studio 的 App 级没有），必须建窗后再解析
            okButton.Style = (Style)window.TryFindResource(danger ? "ButtonDanger" : "ButtonPrimary");
            cancelButton.Style = (Style)window.TryFindResource("ButtonDefault");
            okButton.Click += (s, e) => { ok = true; window.Close(); };
            cancelButton.Focus();
            window.ShowDialog();
            return ok;
        }

        /// <summary>输入弹窗；取消或空白返回 null。</summary>
        public static string Input(Window owner, string title, string prompt, string initial)
        {
            var panel = new StackPanel { Margin = new Thickness(18, 16, 18, 16), MinWidth = 280 };
            panel.Children.Add(new TextBlock { Text = prompt, FontSize = 13 });
            var box = new TextBox
            {
                Text = initial ?? "",
                Margin = new Thickness(0, 8, 0, 0),
                MinHeight = 28,
                Padding = new Thickness(5, 3, 5, 3),
                FontSize = 13,
            };
            panel.Children.Add(box);
            var ok = false;
            var okButton = new Button { Content = "确定", IsDefault = true };
            var cancelButton = new Button { Content = "取消", IsCancel = true };
            panel.Children.Add(ButtonRow(okButton, cancelButton));
            var window = MakeWindow(owner, title, panel);
            okButton.Style = (Style)window.TryFindResource("ButtonPrimary");
            cancelButton.Style = (Style)window.TryFindResource("ButtonDefault");
            okButton.Click += (s, e) => { ok = true; window.Close(); };
            box.Focus();
            box.SelectAll();
            window.ShowDialog();
            return ok && !string.IsNullOrWhiteSpace(box.Text) ? box.Text : null;
        }
    }
}
