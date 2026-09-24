using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TradosToolkit.Common.Catalog;
using TradosToolkit.Glossaries;

namespace TradosToolkit.TranslationProvider.UI
{
    /// <summary>
    /// 单条术语条目的新增/修改表单（完整模型）：
    /// 源/目标术语、词性、状态、源/目标同义词、定义、例句、备注、领域；修改时另只读展示创建/更新时间。
    /// 表单只负责收集输入，结果放在 Result 交回调用方落库（本窗口不碰数据库）。
    /// 与术语管理页同线程（专用 STA 线程）以 ShowDialog 弹出，模态嵌套泵自带 TranslateMessage，输入正常。
    /// </summary>
    public partial class TermDialog : Window
    {
        private readonly TermEntry _editing;
        private readonly string _srcLang;
        private readonly string _tgtLang;

        /// <summary>确定后的表单结果；取消/关闭时为 null。</summary>
        public TermEntry Result { get; private set; }

        /// <summary>下拉项：存库值 + 界面中文标签。</summary>
        private class Option
        {
            public string Value;
            public string Label;
            public override string ToString() { return Label; }
        }

        /// <param name="editing">修改时的原条目；新增传 null。</param>
        /// <param name="kindLabel">术语类型（译前/译后），仅用于副标题展示。</param>
        /// <param name="pairLabel">语言对展示文本（如 zh-CN → en-US）。</param>
        /// <param name="domains">可选领域清单。</param>
        /// <param name="selectDomain">新增时默认选中的领域。</param>
        /// <param name="srcLang">源语言代码（同义词按此语言归档）。</param>
        /// <param name="tgtLang">目标语言代码。</param>
        public TermDialog(TermEntry editing, string kindLabel, string pairLabel,
                          IEnumerable<string> domains, string selectDomain,
                          string srcLang = null, string tgtLang = null)
        {
            InitializeComponent();
            _editing = editing;
            _srcLang = srcLang ?? editing?.SourceLang;
            _tgtLang = tgtLang ?? editing?.TargetLang;

            TitleText.Text = editing == null ? "新增术语" : "修改术语";
            SubText.Text = string.Format("{0} · {1}", kindLabel, pairLabel);

            // 词性 / 状态下拉：Value 落库、Label 显示
            PosCombo.ItemsSource = PartOfSpeech.All()
                .Select(v => new Option { Value = v, Label = Glossaries.PartOfSpeech.Label(v) }).ToList();
            StatusCombo.ItemsSource = TermStatus.All()
                .Select(v => new Option { Value = v, Label = TermStatus.Label(v) }).ToList();

            var list = (domains ?? Enumerable.Empty<string>()).ToList();
            DomCombo.ItemsSource = list;
            var want = editing == null ? selectDomain : editing.Domain;
            DomCombo.SelectedItem = list.FirstOrDefault(d =>
                                        string.Equals(d, want, StringComparison.OrdinalIgnoreCase))
                                    ?? list.FirstOrDefault();

            SrcSynLabel.Text = "源侧同义词（" + (_srcLang ?? "源语言") + "，" + "一行一个）";
            TgtSynLabel.Text = "目标侧同义词（" + (_tgtLang ?? "目标语言") + "，" + "一行一个）";

            if (editing == null)
            {
                PosCombo.SelectedIndex = 0;                          // （无）
                StatusCombo.SelectedIndex = 0;                       // 首选
                MetaPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                FromBox.Text = editing.FromTerm ?? "";
                ToBox.Text = editing.ToTerm ?? "";
                DefBox.Text = editing.Definition ?? "";
                ExampleBox.Text = editing.Example ?? "";
                NoteBox.Text = editing.Note ?? "";

                SelectOption(PosCombo, PartOfSpeech.Normalize(editing.PartOfSpeech ?? ""));
                SelectOption(StatusCombo, TermStatus.Normalize(editing.Status));

                SrcSynBox.Text = string.Join("\r\n", editing.SynonymsFor(_srcLang));
                TgtSynBox.Text = string.Join("\r\n", editing.SynonymsFor(_tgtLang));

                CreatedText.Text = "创建时间：" + editing.CreatedAtText;
                UpdatedText.Text = "更新时间：" + editing.UpdatedAtText;
            }

            Loaded += (s, e) =>
            {
                FromBox.Focus();
                FromBox.SelectAll();
            };
        }

        private static void SelectOption(System.Windows.Controls.ComboBox combo, string value)
        {
            var items = combo.ItemsSource as IEnumerable<Option>;
            var hit = items?.FirstOrDefault(o =>
                string.Equals(o.Value, value ?? "", StringComparison.OrdinalIgnoreCase));
            combo.SelectedItem = hit ?? (combo.Items.Count > 0 ? combo.Items[0] : null);
        }

        /// <summary>把多行文本按行拆成去空白、去重的同义词列表。</summary>
        private static List<string> SplitSynonyms(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var t = (raw ?? "").Trim();
                if (t.Length == 0) continue;
                if (!list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
            }
            return list;
        }

        private void Ok(object sender, RoutedEventArgs e)
        {
            var from = (FromBox.Text ?? "").Trim();
            if (from.Length == 0)
            {
                ErrText.Text = "「源术语」不能为空。";
                FromBox.Focus();
                return;
            }
            var to = (ToBox.Text ?? "").Trim();
            if (to.Length == 0)
            {
                ErrText.Text = "「目标术语」不能为空。";
                ToBox.Focus();
                return;
            }

            var dom = DomCombo.SelectedItem as string;
            var pos = (PosCombo.SelectedItem as Option)?.Value ?? PartOfSpeech.None;
            var status = (StatusCombo.SelectedItem as Option)?.Value ?? TermStatus.Preferred;

            var entry = new TermEntry
            {
                Id = _editing == null ? 0 : _editing.Id,
                SourceLang = _srcLang,
                TargetLang = _tgtLang,
                FromTerm = from,
                ToTerm = to,
                Domain = string.IsNullOrWhiteSpace(dom) ? DomainTree.DefaultDomain : dom,
                PartOfSpeech = pos,
                Status = status,
                Definition = (DefBox.Text ?? "").Trim(),
                Example = (ExampleBox.Text ?? "").Trim(),
                Note = (NoteBox.Text ?? "").Trim(),
                CreatedAt = _editing == null ? (DateTime?)null : _editing.CreatedAt,
                UpdatedAt = _editing == null ? (DateTime?)null : _editing.UpdatedAt,
            };

            if (!string.IsNullOrEmpty(_srcLang))
                foreach (var s in SplitSynonyms(SrcSynBox.Text))
                    entry.Synonyms.Add(new TermSynonym { Lang = _srcLang, Term = s });
            if (!string.IsNullOrEmpty(_tgtLang))
                foreach (var s in SplitSynonyms(TgtSynBox.Text))
                    entry.Synonyms.Add(new TermSynonym { Lang = _tgtLang, Term = s });

            Result = entry;
            DialogResult = true;
        }
    }
}
