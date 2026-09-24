using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TradosToolkit.Common;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 从模板新建项目对话框：选 Studio 项目模板 → 填项目名 → 选语言对（可多目标）→
    /// 选源文件（可多个）→ 确认后由 NewFromTemplateAction 编程式创建项目。
    /// 界面见 NewFromTemplateDialog.xaml（视觉跟随工作台：HandyControl 主题 + Trados 蓝强调色）。
    /// </summary>
    public partial class NewFromTemplateDialog : Window
    {
        public class Result
        {
            public string ProjectName;
            public string StudioTemplatePath;
            public string SourceLang;
            public List<string> TargetLangs;
            public List<string> FilePaths;
            public string ProjectFolder;
        }

        /// <summary>目标语言勾选项：勾选状态存在数据项上，列表过滤（重建视图）不会丢选。</summary>
        public class LangPick : INotifyPropertyChanged
        {
            private bool _isSelected;

            public LangItem Lang { get; set; }
            public string Label => Lang == null ? string.Empty : Lang.Label;
            public string Code => Lang == null ? string.Empty : Lang.Code;

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly ObservableCollection<string> _selectedFiles = new ObservableCollection<string>();
        private List<LangPick> _picks;
        private ListCollectionView _targetView;
        private string _lastSrcCode;

        private NewFromTemplateDialog()
        {
            InitializeComponent();
            LoadLanguages();

            FilesList.ItemsSource = _selectedFiles;
            _selectedFiles.CollectionChanged += (s, e) => UpdateFilesCount();
            UpdateFilesCount();

            FolderBox.Text = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SDL", "SDL Trados Studio", "15", "Projects");
        }

        // ====================== 语言 ======================

        private void LoadLanguages()
        {
            var langs = LanguageCatalog.All();

            // 源语言：与术语管理/收件箱同一套「下拉内搜索」
            var srcView = new ListCollectionView(langs);
            SourceCombo.ItemsSource = srcView;
            AttachLangFilter(SourceCombo, srcView);
            var en = langs.FirstOrDefault(l => l.Code != null && l.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            SourceCombo.SelectedItem = en ?? langs.FirstOrDefault();
            _lastSrcCode = (SourceCombo.SelectedItem as LangItem)?.Code;

            // 目标语言：勾选式多选 + 过滤
            _picks = langs.Select(l => new LangPick { Lang = l }).ToList();
            foreach (var p in _picks)
                p.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(LangPick.IsSelected)) OnTargetSelectionChanged(); };
            _targetView = new ListCollectionView(_picks);
            TargetList.ItemsSource = _targetView;

            var zh = _picks.FirstOrDefault(p => p.Code != null && p.Code.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
            if (zh != null) zh.IsSelected = true;
            OnTargetSelectionChanged();
        }

        /// <summary>语言下拉"下拉内搜索框"过滤：与 GlossaryManagerWindow.AttachLangFilter 同一套实现。</summary>
        private void AttachLangFilter(ComboBox combo, ListCollectionView view)
        {
            TextBox search = null;
            combo.DropDownOpened += (s, e) =>
            {
                if (search == null)
                {
                    search = combo.Template.FindName("LangSearchBox", combo) as TextBox;
                    if (search != null)
                    {
                        search.TextChanged += (a, b) =>
                        {
                            var q = (search.Text ?? string.Empty).Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(q)) view.Filter = null;
                            else view.Filter = item =>
                            {
                                var l = item as LangItem;
                                if (l == null) return false;
                                return (l.Label ?? string.Empty).ToLowerInvariant().Contains(q) ||
                                       (l.Code ?? string.Empty).ToLowerInvariant().Contains(q);
                            };
                            view.Refresh();
                        };
                        search.PreviewKeyDown += (a, b) =>
                        {
                            if (b.Key == System.Windows.Input.Key.Enter)
                            {
                                var first = view.OfType<LangItem>().FirstOrDefault();
                                if (first != null) combo.SelectedItem = first;
                                combo.IsDropDownOpen = false;
                                b.Handled = true;
                            }
                        };
                    }
                }
                if (search != null)
                {
                    // 下拉打开后 ComboBox 会把焦点抢回列表选中项，延迟一帧再聚焦搜索框，否则敲不进字
                    combo.Dispatcher.BeginInvoke(new System.Action(() =>
                    {
                        search.Focus();
                        System.Windows.Input.Keyboard.Focus(search);
                        search.SelectAll();
                    }), System.Windows.Threading.DispatcherPriority.Input);
                }
            };
            combo.DropDownClosed += (s, e) =>
            {
                if (search != null) search.Text = string.Empty; // 触发 TextChanged → 清过滤
                view.Filter = null;
                view.Refresh();
                if (combo.SelectedItem == null)
                {
                    // 过滤期间选中项被清空且未点选：按上一次的语向恢复显示
                    var item = LanguageCatalog.All().FirstOrDefault(l =>
                        string.Equals(l.Code, _lastSrcCode, StringComparison.OrdinalIgnoreCase));
                    if (item != null) combo.SelectedItem = item;
                }
            };
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var code = (SourceCombo.SelectedItem as LangItem)?.Code;
            if (!string.IsNullOrEmpty(code)) _lastSrcCode = code;
            TryFillName();
        }

        private void TargetSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            TargetSearchHint.Visibility = string.IsNullOrEmpty(TargetSearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (_targetView == null) return;

            var q = (TargetSearchBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(q)) _targetView.Filter = null;
            else _targetView.Filter = item =>
            {
                var p = item as LangPick;
                if (p == null) return false;
                return (p.Label ?? string.Empty).ToLowerInvariant().Contains(q) ||
                       (p.Code ?? string.Empty).ToLowerInvariant().Contains(q);
            };
            _targetView.Refresh();
        }

        private void OnTargetSelectionChanged()
        {
            UpdateTargetCount();
            TryFillName();
        }

        private void UpdateTargetCount()
        {
            var n = _picks == null ? 0 : _picks.Count(p => p.IsSelected);
            TargetCountText.Text = "已选 " + n + " 个目标语言";
        }

        /// <summary>项目名留空时按语言对给默认值（用户手改过名称则不再覆盖）。</summary>
        private void TryFillName()
        {
            if (!string.IsNullOrWhiteSpace(NameBox.Text)) return;
            var src = (SourceCombo.SelectedItem as LangItem)?.Code;
            var tgt = _picks?.FirstOrDefault(p => p.IsSelected)?.Code;
            if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                NameBox.Text = src + "-" + tgt + "_" + DateTime.Now.ToString("yyyyMMdd");
        }

        // ====================== 文件 ======================

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = "可翻译文件|*.docx;*.doc;*.xlsx;*.pptx;*.txt;*.xml;*.html;*.csv;*.sdlxliff|所有文件|*.*",
            };
            if (dlg.ShowDialog(this) != true) return;
            foreach (var f in dlg.FileNames)
                if (!_selectedFiles.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase)))
                    _selectedFiles.Add(f);
        }

        private void RemoveFiles_Click(object sender, RoutedEventArgs e)
        {
            var picked = FilesList.SelectedItems.Cast<string>().ToList();
            foreach (var f in picked) _selectedFiles.Remove(f);
        }

        private void ClearFiles_Click(object sender, RoutedEventArgs e)
        {
            _selectedFiles.Clear();
        }

        private void UpdateFilesCount()
        {
            FilesCountText.Text = _selectedFiles.Count == 0
                ? "尚未选择文件（可不选，项目建好后在 Studio 里添加）"
                : "已选 " + _selectedFiles.Count + " 个文件";
        }

        // ====================== 提交 ======================

        private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Studio 项目模板|*.sdltpl|所有文件|*.*",
            };
            if (dlg.ShowDialog(this) == true)
                TemplateBox.Text = dlg.FileName;
        }

        private void ShowError(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { ShowError("请输入项目名称。"); return; }

            if (SourceCombo.SelectedItem == null) { ShowError("请选择源语言。"); return; }
            if (_picks == null || _picks.All(p => !p.IsSelected)) { ShowError("请至少选择一个目标语言。"); return; }

            DialogResult = true;
            Close();
        }

        public static Result Prompt()
        {
            var dlg = new NewFromTemplateDialog();
            if (dlg.ShowDialog() != true) return null;

            var src = (dlg.SourceCombo.SelectedItem as LangItem)?.Code;
            var tgts = dlg._picks.Where(p => p.IsSelected).Select(p => p.Code).ToList();

            return new Result
            {
                ProjectName = dlg.NameBox.Text.Trim(),
                StudioTemplatePath = dlg.TemplateBox.Text.Trim(),
                SourceLang = src ?? string.Empty,
                TargetLangs = tgts,
                FilePaths = dlg._selectedFiles.Count > 0 ? new List<string>(dlg._selectedFiles) : null,
                ProjectFolder = dlg.FolderBox.Text.Trim(),
            };
        }
    }
}
