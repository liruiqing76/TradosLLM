using System;
using System.Windows.Forms;
using TradosToolkit.Glossaries;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 术语条目编辑器（WinForms）。供 Studio 术语视图的添加/修改回调使用，
    /// 字段完整：源/目标术语、词性、状态、定义、例句、备注，以及两语言的同义词。
    /// 确定后 Entry 属性即为待落库的完整 TermEntry。
    /// </summary>
    public class TermEntryEditorDialog : Form
    {
        public TermEntry Entry { get; private set; }

        private readonly TextBox TbFrom = new TextBox();
        private readonly TextBox TbTo = new TextBox();
        private readonly ComboBox CbPos = new ComboBox();
        private readonly ComboBox CbStatus = new ComboBox();
        private readonly TextBox TbDefinition = new TextBox { Multiline = true, Height = 56, ScrollBars = ScrollBars.Vertical };
        private readonly TextBox TbExample = new TextBox { Multiline = true, Height = 48, ScrollBars = ScrollBars.Vertical };
        private readonly TextBox TbSynonymsSrc = new TextBox();
        private readonly TextBox TbSynonymsTgt = new TextBox();
        private readonly TextBox TbNote = new TextBox();

        public TermEntryEditorDialog(TermEntry entry)
        {
            Entry = entry ?? new TermEntry();
            Text = Entry.Id > 0 ? "编辑术语" : "新建术语";
            Width = 560;
            Height = 520;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 11,
                Padding = new Padding(16, 16, 16, 10),
                AutoScroll = true,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddRow(grid, 0, "源术语:", TbFrom);
            AddRow(grid, 1, "目标术语:", TbTo);

            foreach (var pos in PartOfSpeech.All())
                CbPos.Items.Add(new ComboItem(pos, PartOfSpeech.Label(pos)));
            CbPos.DropDownStyle = ComboBoxStyle.DropDownList;
            AddRow(grid, 2, "词性:", CbPos);

            foreach (var st in TermStatus.All())
                CbStatus.Items.Add(new ComboItem(st, TermStatus.Label(st)));
            CbStatus.DropDownStyle = ComboBoxStyle.DropDownList;
            AddRow(grid, 3, "状态:", CbStatus);

            AddRow(grid, 4, "源语言同义词:", TbSynonymsSrc);
            AddRow(grid, 5, "目标语言同义词:", TbSynonymsTgt);
            AddRow(grid, 6, "定义:", TbDefinition);
            AddRow(grid, 7, "例句:", TbExample);
            AddRow(grid, 8, "备注:", TbNote);

            var hint = new Label
            {
                AutoSize = true,
                ForeColor = System.Drawing.Color.Gray,
                Text = "同义词多个用分号 ; 或逗号 , 分隔。",
                Margin = new Padding(0, 6, 0, 0),
            };
            grid.Controls.Add(hint, 0, 9);
            grid.SetColumnSpan(hint, 2);

            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 90 };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90 };
            var btnPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            btnPanel.Controls.Add(ok);
            btnPanel.Controls.Add(cancel);
            ok.Margin = new Padding(8, 0, 0, 0);
            AcceptButton = ok;
            CancelButton = cancel;
            var cell = new Panel { Dock = DockStyle.Fill };
            cell.Controls.Add(btnPanel);
            grid.Controls.Add(cell, 0, 10);
            grid.SetColumnSpan(cell, 2);

            Controls.Add(grid);

            LoadEntry(Entry);
            ok.Click += (s, e) => Collect();
        }

        private void LoadEntry(TermEntry entry)
        {
            TbFrom.Text = entry.FromTerm ?? string.Empty;
            TbTo.Text = entry.ToTerm ?? string.Empty;
            SelectByValue(CbPos, entry.PartOfSpeech ?? PartOfSpeech.None);
            SelectByValue(CbStatus, TermStatus.Normalize(entry.Status));
            TbDefinition.Text = entry.Definition ?? string.Empty;
            TbExample.Text = entry.Example ?? string.Empty;
            TbNote.Text = entry.Note ?? string.Empty;
            TbSynonymsSrc.Text = string.Join("；", entry.SynonymsFor(entry.SourceLang));
            TbSynonymsTgt.Text = string.Join("；", entry.SynonymsFor(entry.TargetLang));
        }

        private void Collect()
        {
            var entry = Entry ?? new TermEntry();
            entry.FromTerm = TbFrom.Text.Trim();
            entry.ToTerm = TbTo.Text.Trim();
            entry.PartOfSpeech = (CbPos.SelectedItem as ComboItem)?.Value ?? PartOfSpeech.None;
            entry.Status = (CbStatus.SelectedItem as ComboItem)?.Value ?? TermStatus.Preferred;
            entry.Definition = TbDefinition.Text.Trim();
            entry.Example = TbExample.Text.Trim();
            entry.Note = TbNote.Text.Trim();

            entry.Synonyms.Clear();
            AddSynonyms(entry, entry.SourceLang, TbSynonymsSrc.Text);
            AddSynonyms(entry, entry.TargetLang, TbSynonymsTgt.Text);
            Entry = entry;
        }

        private static void AddSynonyms(TermEntry entry, string lang, string text)
        {
            if (string.IsNullOrWhiteSpace(lang) || string.IsNullOrWhiteSpace(text)) return;
            foreach (var part in text.Split(new[] { ';', '；', ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var term = part.Trim();
                if (term.Length == 0) continue;
                entry.Synonyms.Add(new TermSynonym { Lang = lang, Term = term });
            }
        }

        private static void SelectByValue(ComboBox box, string value)
        {
            foreach (ComboItem item in box.Items)
            {
                if (string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static void AddRow(TableLayoutPanel grid, int row, string label, Control field)
        {
            var lbl = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 10, 0), Anchor = AnchorStyles.Left };
            field.Margin = new Padding(0, 4, 0, 4);
            field.Width = 380;
            field.Dock = DockStyle.Fill;
            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(field, 1, row);
        }

        private class ComboItem
        {
            public readonly string Value;
            private readonly string _label;
            public ComboItem(string value, string label) { Value = value; _label = label; }
            public override string ToString() => _label;
        }
    }
}
