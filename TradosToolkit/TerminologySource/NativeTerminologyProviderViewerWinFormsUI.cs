using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Sdl.Terminology.TerminologyProvider.Core;
using TradosToolkit.Glossaries;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// Studio 术语视图的写入承接者与展示面板。
    /// ITerminologyProvider 本身没有写入方法，编辑器里"添加术语/修改术语/新建条目"
    /// 是通过 ITerminologyProviderViewerWinFormsUI 回调到插件的；本类同时提供
    /// Control（术语列表 + 增删改按钮）供 Studio 术语视图内嵌显示。
    /// 仅本地源（kind=local）允许写入，落 SQLite 的 term_entries/term_synonyms；
    /// 线上源只读，写操作拒绝并提示。
    /// </summary>
    [TerminologyProviderViewerWinFormsUIAttribute]
    public class NativeTerminologyProviderViewerWinFormsUI : ITerminologyProviderViewerWinFormsUI
    {
        private ITerminologyProvider _provider;
        private CultureInfo _source;
        private CultureInfo _target;
        private ViewerPanel _panel;
        private IEntry _selectedTerm;

        public event EventHandler TermChanged;
        public event EventHandler<EntryEventArgs> SelectedTermChanged;

        /// <summary>Studio 术语视图内嵌的面板（首次访问时懒加载）。</summary>
        public Control Control
        {
            get
            {
                if (_panel == null)
                {
                    _panel = new ViewerPanel();
                    _panel.AddRequested += (s, e) => AddAndEditTerm(null, null, null);
                    _panel.EditRequested += (s, e) => EditTerm(_panel.CurrentIEntry);
                    _panel.DeleteRequested += (s, e) => DeleteCurrent();
                    BindPanel();
                }
                return _panel;
            }
        }

        public bool Initialized => _provider != null;

        public IEntry SelectedTerm
        {
            get { return _selectedTerm; }
            set
            {
                if (ReferenceEquals(_selectedTerm, value)) return;
                _selectedTerm = value;
                if (_panel != null) _panel.SetEntry(value);
                SelectedTermChanged?.Invoke(this, new EntryEventArgs(value));
            }
        }

        public void Initialize(ITerminologyProvider terminologyProvider, CultureInfo source, CultureInfo target)
        {
            _provider = terminologyProvider;
            _source = source;
            _target = target;
            BindPanel();
        }

        /// <summary>把 Provider 与语言对同步给内嵌面板（Initialize 或面板懒创建后调用）。</summary>
        private void BindPanel()
        {
            if (_panel == null) return;
            var local = _provider as NativeTerminologyProvider;
            if (local == null)
            {
                _panel.SetReadOnly(true);
                return;
            }
            _panel.Bind(local,
                        string.IsNullOrWhiteSpace(local.SourceLang) ? LangCode(_source) : local.SourceLang,
                        string.IsNullOrWhiteSpace(local.TargetLang) ? LangCode(_target) : local.TargetLang,
                        local.Domain);
        }

        public bool SupportsTerminologyProviderUri(Uri terminologyProviderUri)
        {
            return _provider != null && NativeTerminologyProviderHelper.Supports(terminologyProviderUri);
        }

        /// <summary>Studio 请求跳转到某条目（本实现把面板选中项同步过去）。</summary>
        public void JumpToTerm(IEntry entry)
        {
            SelectedTerm = entry;
        }

        /// <summary>快速添加：只给源/目标术语，落库后按默认值补齐其他字段。</summary>
        public void AddTerm(string source, string target)
        {
            var provider = RequireLocal();
            if (provider == null) return;

            provider.AddOrUpdateEntry(new TermEntry
            {
                SourceLang = LangCode(_source),
                TargetLang = LangCode(_target),
                FromTerm = source,
                ToTerm = target,
                Status = TermStatus.Preferred,
            });
            RefreshPanel();
            RaiseTermChanged();
        }

        /// <summary>修改既有条目：先从 IEntry 反解 TermEntry，再用对话框让用户改全字段。</summary>
        public void EditTerm(IEntry term)
        {
            var provider = RequireLocal();
            if (provider == null) return;
            if (term == null)
            {
                MessageBox.Show("请先选择一条术语。", "TradosToolkit",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var entry = FromIEntry(term, LangCode(_source), LangCode(_target));
            using (var dlg = new TermEntryEditorDialog(entry))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                provider.AddOrUpdateEntry(dlg.Entry);
            }
            RefreshPanel();
            RaiseTermChanged();
        }

        /// <summary>添加并立即编辑：Studio 新建条目时调用，带默认源/目标文本。</summary>
        public void AddAndEditTerm(IEntry term, string source, string target)
        {
            var provider = RequireLocal();
            if (provider == null) return;

            var entry = FromIEntry(term, LangCode(_source), LangCode(_target));
            if (string.IsNullOrWhiteSpace(entry.FromTerm)) entry.FromTerm = source;
            if (string.IsNullOrWhiteSpace(entry.ToTerm)) entry.ToTerm = target;
            entry.Id = 0;

            using (var dlg = new TermEntryEditorDialog(entry))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                provider.AddOrUpdateEntry(dlg.Entry);
            }
            RefreshPanel();
            RaiseTermChanged();
        }

        public void Release()
        {
            _provider = null;
            _source = null;
            _target = null;
            if (_panel != null)
            {
                _panel.Dispose();
                _panel = null;
            }
        }

        private void DeleteCurrent()
        {
            var provider = RequireLocal();
            if (provider == null) return;
            var entry = _panel?.CurrentEntry;
            if (entry == null || entry.Id <= 0)
            {
                MessageBox.Show("请先选择一条术语。", "TradosToolkit",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("确定删除术语「" + entry.FromTerm + "」？", "TradosToolkit",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            provider.RemoveEntry(entry.Id);
            RefreshPanel();
            RaiseTermChanged();
        }

        private void RefreshPanel()
        {
            _panel?.Reload();
        }

        private void RaiseTermChanged()
        {
            TermChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>取本地可写 Provider；线上源/未知源给出提示并返回 null。</summary>
        private NativeTerminologyProvider RequireLocal()
        {
            var provider = _provider as NativeTerminologyProvider;
            if (provider == null || !provider.IsLocal)
            {
                MessageBox.Show("当前术语源为只读的线上术语服务，请在术语管理界面或切换到本地术语库后再编辑。",
                                "TradosToolkit", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            return provider;
        }

        private static string LangCode(CultureInfo culture)
        {
            return culture != null ? culture.Name : null;
        }

        /// <summary>把 Studio 的 IEntry 反解为本地 TermEntry（取各语言第一个术语为主术语）。</summary>
        private static TermEntry FromIEntry(IEntry source, string srcLang, string tgtLang)
        {
            var entry = new TermEntry
            {
                SourceLang = srcLang,
                TargetLang = tgtLang,
                Status = TermStatus.Preferred,
            };
            if (source == null) return entry;

            long id;
            if (long.TryParse(Convert.ToString(source.Id), out id)) entry.Id = id;

            var languages = source.Languages;
            if (languages == null) return entry;
            foreach (var lang in languages)
            {
                if (lang == null || lang.Terms == null || lang.Terms.Count == 0) continue;
                var isTarget = lang.Locale != null && tgtLang != null &&
                               string.Equals(lang.Locale.Name, tgtLang, StringComparison.OrdinalIgnoreCase);
                if (isTarget && string.IsNullOrWhiteSpace(entry.ToTerm))
                    entry.ToTerm = lang.Terms[0].Value;
                else if (!isTarget && string.IsNullOrWhiteSpace(entry.FromTerm))
                    entry.FromTerm = lang.Terms[0].Value;
            }
            return entry;
        }

        /// <summary>
        /// Studio 术语视图内嵌面板：列出当前语言对的术语，支持新增/编辑/删除。
        /// 只读源（线上）隐藏编辑按钮。
        /// </summary>
        private class ViewerPanel : UserControl
        {
            public event EventHandler AddRequested;
            public event EventHandler EditRequested;
            public event EventHandler DeleteRequested;

            private readonly ListView _list = new ListView();
            private readonly Label _title = new Label();
            private readonly Button _add = new Button();
            private readonly Button _edit = new Button();
            private readonly Button _del = new Button();
            private List<TermEntry> _entries = new List<TermEntry>();
            private List<IEntry> _ientries = new List<IEntry>();
            private NativeTerminologyProvider _provider;
            private string _srcLang;
            private string _tgtLang;
            private string _domain;

            public TermEntry CurrentEntry
            {
                get
                {
                    if (_list.SelectedItems.Count == 0) return null;
                    var index = _list.SelectedItems[0].Index;
                    return index >= 0 && index < _entries.Count ? _entries[index] : null;
                }
            }

            public IEntry CurrentIEntry
            {
                get
                {
                    if (_list.SelectedItems.Count == 0) return null;
                    var index = _list.SelectedItems[0].Index;
                    return index >= 0 && index < _ientries.Count ? _ientries[index] : null;
                }
            }

            public ViewerPanel()
            {
                Dock = DockStyle.Fill;
                BuildUi();
            }

            /// <summary>注入 Provider 与语言对，随后立即拉一次数据。</summary>
            internal void Bind(NativeTerminologyProvider provider, string srcLang, string tgtLang, string domain)
            {
                _provider = provider;
                _srcLang = srcLang;
                _tgtLang = tgtLang;
                _domain = domain;
                Reload();
            }

            private void BuildUi()
            {
                _title.Dock = DockStyle.Top;
                _title.Height = 28;
                _title.TextAlign = ContentAlignment.MiddleLeft;
                _title.Font = new Font(Font, FontStyle.Bold);
                _title.Text = "术语列表";

                var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight };
                _add.Text = "新增";
                _edit.Text = "编辑";
                _del.Text = "删除";
                _add.Width = _edit.Width = _del.Width = 72;
                _add.Click += (s, e) => AddRequested?.Invoke(this, EventArgs.Empty);
                _edit.Click += (s, e) => EditRequested?.Invoke(this, EventArgs.Empty);
                _del.Click += (s, e) => DeleteRequested?.Invoke(this, EventArgs.Empty);
                _list.DoubleClick += (s, e) => EditRequested?.Invoke(this, EventArgs.Empty);
                bar.Controls.Add(_add);
                bar.Controls.Add(_edit);
                bar.Controls.Add(_del);

                _list.Dock = DockStyle.Fill;
                _list.View = View.Details;
                _list.FullRowSelect = true;
                _list.GridLines = true;
                _list.Columns.Add("源术语", 160);
                _list.Columns.Add("目标术语", 160);
                _list.Columns.Add("词性", 70);
                _list.Columns.Add("状态", 60);

                Controls.Add(_list);
                Controls.Add(bar);
                Controls.Add(_title);
            }

            internal void SetReadOnly(bool readOnly)
            {
                _add.Enabled = _edit.Enabled = _del.Enabled = !readOnly;
            }

            /// <summary>重新从本地库读取当前语言对术语并刷新列表。</summary>
            public void Reload()
            {
                _entries = new List<TermEntry>();
                _ientries = new List<IEntry>();
                _list.Items.Clear();
                if (_provider == null || !_provider.IsLocal)
                {
                    SetReadOnly(true);
                    _title.Text = "术语列表（只读：线上术语服务）";
                    return;
                }
                SetReadOnly(false);
                try
                {
                    var db = new GlossaryDb();
                    _entries = db.GetTermEntries(_srcLang, _tgtLang, _domain) ?? new List<TermEntry>();
                }
                catch { _entries = new List<TermEntry>(); }

                foreach (var e in _entries)
                {
                    var item = new ListViewItem(e.FromTerm ?? string.Empty);
                    item.SubItems.Add(e.ToTerm ?? string.Empty);
                    item.SubItems.Add(e.PartOfSpeechText);
                    item.SubItems.Add(e.StatusText);
                    _list.Items.Add(item);
                    _ientries.Add(NativeTerminologyProvider.BuildEntry(e, e.SourceLang, e.TargetLang));
                }
                _title.Text = "术语列表（" + _entries.Count + " 条）";
            }

            public void SetEntry(IEntry entry)
            {
                if (entry == null) return;
                var id = Convert.ToInt64(entry.Id);
                for (var i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Id != id) continue;
                    _list.Items[i].Selected = true;
                    _list.Items[i].EnsureVisible();
                    return;
                }
            }
        }
    }
}
