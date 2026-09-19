using System;
using System.Collections.Generic;
using System.Windows.Controls;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 翻译中心 View 自带左栏（GetExplorerBarControl 宿主）：常用地址列表。
    /// 点击=浏览器打开；右键条目可删除；"收藏当前页"存 config.json 的 translationCenterBookmarks。
    /// </summary>
    public partial class TranslationCenterNavControl : UserControl
    {
        private readonly TranslationCenterBrowserControl _browser;
        private List<ToolkitConfig.BookmarkItem> _bookmarks;

        public TranslationCenterNavControl(TranslationCenterBrowserControl browser)
        {
            ToolkitLog.Info("翻译中心左栏构建");
            _browser = browser;
            InitializeComponent();
            Reload();
        }

        private void Reload()
        {
            try
            {
                _bookmarks = ToolkitConfig.Load().Bookmarks;
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心左栏读取收藏失败，用默认", ex);
                _bookmarks = ToolkitConfig.DefaultBookmarks();
            }
            Rebuild();
        }

        private void Rebuild()
        {
            BookmarkPanel.Children.Clear();
            foreach (var item in _bookmarks)
            {
                var bookmark = item;
                var label = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(bookmark.name) ? bookmark.url : bookmark.name,
                    TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
                };
                var button = new Button
                {
                    Content = label,
                    Style = (System.Windows.Style)Resources["BookmarkButton"],
                    ToolTip = bookmark.url,
                };
                button.Click += (s, e) => _browser.NavigateFromBookmark(bookmark.url);
                var remove = new MenuItem { Header = "删除此地址" };
                remove.Click += (s, e) => RemoveBookmark(bookmark);
                var menu = new ContextMenu();
                menu.Items.Add(remove);
                button.ContextMenu = menu;
                BookmarkPanel.Children.Add(button);
            }
        }

        private void RemoveBookmark(ToolkitConfig.BookmarkItem bookmark)
        {
            _bookmarks.Remove(bookmark);
            try
            {
                ToolkitConfig.Save(bookmarks: _bookmarks);
                NavStatus.Text = "已删除：" + (bookmark.name ?? bookmark.url);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心左栏删除收藏保存失败", ex);
                NavStatus.Text = "保存失败：" + ex.Message;
            }
            Rebuild();
        }

        private void AddBookmark_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            string message;
            var added = _browser.TryBookmarkCurrentPage(out message);
            NavStatus.Text = message;
            if (added) Reload();
        }
    }
}
