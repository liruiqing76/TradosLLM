using System;
using System.Windows.Forms;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 术语源连接参数对话框（WinForms，由 Studio"术语库"对话框宿主调用）。
    /// 支持两套来源：本地 SQLite 术语库（可写）与线上术语服务（只读，需填 termBaseUrl）。
    /// 关键字段：来源类型、术语服务地址、源语言、目标语言、领域（默认取工作台配的全局领域）。
    /// </summary>
    public class NativeTerminologyDialog : Form
    {
        public string Kind => TermSourceKind.Normalize(RbOnline.Checked ? TermSourceKind.Online : TermSourceKind.Local);
        public string TermBaseUrl => TbBase.Text.Trim();
        public string SourceLang => TbSrc.Text.Trim();
        public string TargetLang => TbTgt.Text.Trim();
        public string Domain => TbDomain.Text.Trim();

        private readonly RadioButton RbLocal = new RadioButton();
        private readonly RadioButton RbOnline = new RadioButton();
        private readonly TextBox TbBase = new TextBox();
        private readonly TextBox TbSrc = new TextBox();
        private readonly TextBox TbTgt = new TextBox();
        private readonly TextBox TbDomain = new TextBox();

        public NativeTerminologyDialog()
        {
            Text = "TradosToolkit 术语源";
            Width = 520;
            Height = 320;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 7,
                Padding = new Padding(16, 16, 16, 10),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var cfg = ToolkitConfig.Load();
            var defaultDomain = string.IsNullOrWhiteSpace(cfg.Domain)
                                    ? Glossaries.DomainTree.DefaultDomain
                                    : cfg.Domain.Trim();

            // 来源类型：本地库（可写）/ 线上服务（只读）
            RbLocal.Text = "本地术语库（可写，直接用插件术语库数据）";
            RbLocal.Checked = true;
            RbLocal.AutoSize = true;
            RbOnline.Text = "线上术语服务（只读，需填地址）";
            RbOnline.AutoSize = true;
            var kindPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            kindPanel.Controls.Add(RbLocal);
            kindPanel.Controls.Add(RbOnline);
            AddRow(grid, 0, "来源类型:", kindPanel);

            TbBase.Width = 340;
            AddRow(grid, 1, "术语服务地址 (termBaseUrl):", TbBase, cfg.TermBaseUrl);
            AddRow(grid, 2, "源语言 (如 zh-CN):", TbSrc, string.IsNullOrWhiteSpace(cfg.InboxSourceLang) ? "zh-CN" : cfg.InboxSourceLang);
            AddRow(grid, 3, "目标语言 (如 en-US):", TbTgt, string.IsNullOrWhiteSpace(cfg.InboxTargetLang) ? "en-US" : cfg.InboxTargetLang);
            AddRow(grid, 4, "领域 (与翻译插件一致):", TbDomain, defaultDomain);

            var hint = new Label
            {
                AutoSize = true,
                ForeColor = System.Drawing.Color.Gray,
                Text = "本地库读写 %APPDATA%\\TradosToolkit\\glossary.db，与术语管理界面同一份数据；线上服务只读。",
                Margin = new Padding(0, 8, 0, 0),
            };
            grid.Controls.Add(hint, 0, 5);
            grid.SetColumnSpan(hint, 2);

            RbLocal.CheckedChanged += (s, e) => SyncKind();
            RbOnline.CheckedChanged += (s, e) => SyncKind();

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
            grid.Controls.Add(cell, 0, 6);
            grid.SetColumnSpan(cell, 2);

            Controls.Add(grid);
            SyncKind();
        }

        /// <summary>回填已有 Provider 的参数（编辑既有术语源时用）。</summary>
        public void Preset(string kind, string sourceLang, string targetLang, string domain)
        {
            var online = TermSourceKind.Normalize(kind) == TermSourceKind.Online;
            RbOnline.Checked = online;
            RbLocal.Checked = !online;
            if (!string.IsNullOrWhiteSpace(sourceLang)) TbSrc.Text = sourceLang;
            if (!string.IsNullOrWhiteSpace(targetLang)) TbTgt.Text = targetLang;
            if (!string.IsNullOrWhiteSpace(domain)) TbDomain.Text = domain;
            SyncKind();
        }

        /// <summary>本地源不需要地址，禁用输入框给用户明确反馈。</summary>
        private void SyncKind()
        {
            var online = RbOnline.Checked;
            TbBase.Enabled = online;
        }

        private static void AddRow(TableLayoutPanel grid, int row, string label, Control field, string placeholder = null)
        {
            var lbl = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 10, 0), Anchor = AnchorStyles.Left };
            field.Margin = new Padding(0, 4, 0, 4);
            field.Width = 340;
            if (placeholder != null && field is TextBox box) box.Text = placeholder;
            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(field, 1, row);
        }
    }
}
