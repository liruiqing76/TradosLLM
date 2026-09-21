using System;
using System.Windows.Forms;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 术语服务连接参数输入对话框（WinForms，由 Studio 术语库对话框宿主调用）。
    /// 四字段：线上术语服务地址、源语言、目标语言、领域（默认取工作台配的全局领域）。
    /// </summary>
    public class NativeTerminologyDialog : Form
    {
        public string TermBaseUrl => TbBase.Text.Trim();
        public string SourceLang => TbSrc.Text.Trim();
        public string TargetLang => TbTgt.Text.Trim();
        public string Domain => TbDomain.Text.Trim();

        private readonly TextBox TbBase = new TextBox();
        private readonly TextBox TbSrc = new TextBox();
        private readonly TextBox TbTgt = new TextBox();
        private readonly TextBox TbDomain = new TextBox();

        public NativeTerminologyDialog()
        {
            Text = "TradosToolkit 术语服务";
            Width = 460;
            Height = 260;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(16, 16, 16, 10),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var defaultDomain = string.IsNullOrWhiteSpace(ToolkitConfig.Load().Domain)
                                    ? Glossaries.DomainTree.DefaultDomain
                                    : ToolkitConfig.Load().Domain.Trim();

            AddRow(grid, 0, "术语服务地址 (termBaseUrl):", TbBase, "http://127.0.0.1:8080");
            AddRow(grid, 1, "源语言 (如 zh-CN):", TbSrc, "zh-CN");
            AddRow(grid, 2, "目标语言 (如 ru-RU):", TbTgt, "ru-RU");
            AddRow(grid, 3, "领域 (与翻译插件一致):", TbDomain, defaultDomain);

            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 90, Anchor = AnchorStyles.Bottom };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90, Anchor = AnchorStyles.Bottom };
            var btnPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 0, 0, 0),
            };
            btnPanel.Controls.Add(ok);
            btnPanel.Controls.Add(cancel);
            ok.Margin = new Padding(8, 0, 0, 0);
            AcceptButton = ok;
            CancelButton = cancel;

            var cell = new Panel { Dock = DockStyle.Fill };
            cell.Controls.Add(btnPanel);
            grid.Controls.Add(cell, 0, 4);
            grid.SetColumnSpan(cell, 2);

            Controls.Add(grid);
        }

        private static void AddRow(TableLayoutPanel grid, int row, string label, TextBox box, string placeholder)
        {
            var lbl = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 10, 0), Anchor = AnchorStyles.Left };
            box.Width = 300;
            box.Margin = new Padding(0, 4, 0, 4);
            if (!string.IsNullOrEmpty(placeholder)) box.Text = placeholder;
            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(box, 1, row);
        }
    }
}