using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TradosToolkit.Glossaries;

namespace TradosToolkit.TranslationProvider.UI
{
    /// <summary>
    /// 单条术语的新增/修改表单：替换前、替换为、领域；修改时额外只读展示创建/更新时间。
    /// 表单只负责收集输入，结果放在 Result 交回调用方落库（本窗口不碰数据库）。
    /// 与术语管理页同线程（专用 STA 线程）以 ShowDialog 弹出，模态嵌套泵自带 TranslateMessage，输入正常。
    /// </summary>
    public partial class TermDialog : Window
    {
        private readonly GlossaryEntry _editing;

        /// <summary>确定后的表单结果；取消/关闭时为 null。</summary>
        public GlossaryEntry Result { get; private set; }

        /// <param name="editing">修改时的原条目；新增传 null。</param>
        /// <param name="selectDomain">新增时默认选中的领域。</param>
        public TermDialog(GlossaryEntry editing, string kindLabel, string pairLabel,
                          IEnumerable<string> domains, string selectDomain)
        {
            InitializeComponent();
            _editing = editing;

            TitleText.Text = editing == null ? "新增术语" : "修改术语";
            SubText.Text = string.Format("{0} · {1}", kindLabel, pairLabel);

            var list = (domains ?? Enumerable.Empty<string>()).ToList();
            DomCombo.ItemsSource = list;
            var want = editing == null ? selectDomain : editing.Domain;
            DomCombo.SelectedItem = list.FirstOrDefault(d =>
                                        string.Equals(d, want, StringComparison.OrdinalIgnoreCase))
                                    ?? list.FirstOrDefault();

            if (editing == null)
            {
                // 新增：时间由落库时生成，隐藏时间区并把领域行收到卡片底边（不留多余间距）
                MetaPanel.Visibility = Visibility.Collapsed;
                DomLabel.Margin = new Thickness(0, 0, 10, 0);
                DomCombo.Margin = new Thickness(0);
            }
            else
            {
                FromBox.Text = editing.From ?? "";
                ToBox.Text = editing.To ?? "";
                CreatedText.Text = "创建时间：" + editing.CreatedAtText;
                UpdatedText.Text = "更新时间：" + editing.UpdatedAtText;
            }

            Loaded += (s, e) =>
            {
                FromBox.Focus();
                FromBox.SelectAll();
            };
        }

        private void Ok(object sender, RoutedEventArgs e)
        {
            var from = (FromBox.Text ?? "").Trim();
            if (from.Length == 0)
            {
                ErrText.Text = "「替换前 (from)」不能为空。";
                FromBox.Focus();
                return;
            }

            var dom = DomCombo.SelectedItem as string;
            Result = new GlossaryEntry
            {
                Id = _editing == null ? 0 : _editing.Id,
                From = from,
                To = (ToBox.Text ?? "").Trim(),
                Domain = string.IsNullOrWhiteSpace(dom) ? DomainTree.DefaultDomain : dom,
                CreatedAt = _editing == null ? (DateTime?)null : _editing.CreatedAt,
                UpdatedAt = _editing == null ? (DateTime?)null : _editing.UpdatedAt,
            };
            DialogResult = true;
        }
    }
}
