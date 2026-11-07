// 列内容替换对话框（Win2000 极简风）：针对右键列进行查找 / 替换。
// 搜索范围限定为该列的全部数据行（不含表头行与末尾空行）。
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;   // ChordRescue 唯一实现（规则第 37 节）

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    public class ReplaceDialog : Form
    {
        // ↓↓↓ 键盘归属抢救：查找/替换两个 TextBox 的 Ctrl+C/V/X/A/Z 归本弹窗（机理/开关/取证见 ChordRescue，规则第 37 节）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ChordRescue.Install(this, msg => Diagnostics.Trace(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); ChordRescue.Uninstall(this, "窗口关闭"); }
        protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑↑↑

        private readonly DataGridView _grid;
        private readonly int _col;
        private readonly Action _onChanged;
        private readonly Action<string> _log;

        private readonly TextBox _txtFind = new TextBox();
        private readonly TextBox _txtReplace = new TextBox();
        private readonly CheckBox _chkCase = new CheckBox();
        private readonly CheckBox _chkWrap = new CheckBox();
        private readonly Button _btnFind = new Button();
        private readonly Button _btnReplace = new Button();
        private readonly Button _btnReplaceAll = new Button();
        private readonly Button _btnCancel = new Button();

        private int _lastFoundRow = -1;

        public ReplaceDialog(DataGridView grid, int col, Action onChanged, Action<string> log)
        {
            _grid = grid;
            _col = col;
            _onChanged = onChanged;
            _log = log;
            BuildUi();
        }

        private void BuildUi()
        {
            Font = new Font("宋体", 9F);
            Text = "替换";
            ClientSize = new Size(476, 168);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            AcceptButton = _btnFind;

            var lblFind = new Label { Text = "查找内容(&N):", Location = new Point(12, 18), Size = new Size(86, 20) };
            _txtFind.SetBounds(104, 16, 232, 20);
            var lblRep = new Label { Text = "替换为(&P):", Location = new Point(12, 48), Size = new Size(86, 20) };
            _txtReplace.SetBounds(104, 46, 232, 20);

            _btnFind.SetBounds(350, 12, 110, 28); _btnFind.Text = "查找下一个(&F)";
            _btnReplace.SetBounds(350, 46, 110, 28); _btnReplace.Text = "替换(&R)";
            _btnReplaceAll.SetBounds(350, 80, 110, 28); _btnReplaceAll.Text = "全部替换(&A)";
            _btnCancel.SetBounds(350, 114, 110, 28); _btnCancel.Text = "取消";
            _btnCancel.DialogResult = DialogResult.Cancel;

            _chkCase.SetBounds(12, 84, 120, 20); _chkCase.Text = "区分大小写(&C)";
            _chkWrap.SetBounds(12, 110, 120, 20); _chkWrap.Text = "循环(&R)";

            Controls.AddRange(new Control[] { lblFind, _txtFind, lblRep, _txtReplace,
                _btnFind, _btnReplace, _btnReplaceAll, _btnCancel, _chkCase, _chkWrap });

            _btnFind.Click += BtnFind_Click;
            _btnReplace.Click += BtnReplace_Click;
            _btnReplaceAll.Click += BtnReplaceAll_Click;
            _btnCancel.Click += (s, e) => Close();
        }

        private bool Match(string cellVal, string find)
        {
            if (string.IsNullOrEmpty(find)) return false;
            var cmp = _chkCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return (cellVal ?? "").IndexOf(find, cmp) >= 0;
        }

        private void SelectCell(int r)
        {
            if (r < 0 || r >= _grid.Rows.Count) return;
            if (_grid.Rows[r].IsNewRow) return;
            try
            {
                _grid.CurrentCell = _grid.Rows[r].Cells[_col];
                _grid.FirstDisplayedScrollingRowIndex = r;
            }
            catch { }
        }

        // 在 [from, toExclusive) 内查找首个匹配行；toExclusive<=0 表示直至末尾
        private int FindFrom(int from, int toExclusive = 0)
        {
            string find = _txtFind.Text;
            int max = toExclusive > 0 ? toExclusive : _grid.Rows.Count;
            for (int r = from; r < max; r++)
            {
                if (_grid.Rows[r].IsNewRow) continue;
                if (Match(_grid.Rows[r].Cells[_col].Value?.ToString(), find)) return r;
            }
            return -1;
        }

        private void BtnFind_Click(object sender, EventArgs e)
        {
            string find = _txtFind.Text;
            if (string.IsNullOrEmpty(find)) { MessageBox.Show(this, "请输入查找内容。", "替换"); return; }

            int start = _lastFoundRow >= 1 ? _lastFoundRow + 1 : 1;
            int found = FindFrom(start);
            if (found < 0 && _chkWrap.Checked) found = FindFrom(1, start);
            if (found < 0) { MessageBox.Show(this, "未找到匹配项。", "替换"); _lastFoundRow = -1; return; }

            _lastFoundRow = found;
            SelectCell(found);
        }

        private void BtnReplace_Click(object sender, EventArgs e)
        {
            string find = _txtFind.Text;
            if (string.IsNullOrEmpty(find)) { MessageBox.Show(this, "请输入查找内容。", "替换"); return; }

            // 当前无命中或命中格已不再匹配，先查找下一个
            if (_lastFoundRow < 1 || _lastFoundRow >= _grid.Rows.Count
                || !Match(_grid.Rows[_lastFoundRow].Cells[_col].Value?.ToString(), find))
            {
                BtnFind_Click(sender, e);
                if (_lastFoundRow < 1) return;
            }

            int r = _lastFoundRow;
            var cell = _grid.Rows[r].Cells[_col];
            string cur = cell.Value?.ToString() ?? "";
            cell.Value = ReplaceText(cur, find, _txtReplace.Text);
            _onChanged?.Invoke();
            _log?.Invoke($"已在列 {TableService.ColumnLetter(_col)} 第 {r + 1} 行替换。");
            BtnFind_Click(sender, e);   // 继续查找下一个
        }

        private void BtnReplaceAll_Click(object sender, EventArgs e)
        {
            string find = _txtFind.Text;
            if (string.IsNullOrEmpty(find)) { MessageBox.Show(this, "请输入查找内容。", "替换"); return; }
            string rep = _txtReplace.Text;
            int cnt = 0;
            for (int r = 1; r < _grid.Rows.Count; r++)
            {
                if (_grid.Rows[r].IsNewRow) continue;
                var cell = _grid.Rows[r].Cells[_col];
                string cur = cell.Value?.ToString() ?? "";
                if (Match(cur, find))
                {
                    cell.Value = ReplaceText(cur, find, rep);
                    cnt++;
                }
            }
            _onChanged?.Invoke();
            _log?.Invoke($"列 {TableService.ColumnLetter(_col)} 全部替换完成，共 {cnt} 个单元格。");
            MessageBox.Show(this, $"共替换 {cnt} 个单元格。", "替换");
            _lastFoundRow = -1;
        }

        private string ReplaceText(string input, string find, string rep)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(find)) return input;
            var cmp = _chkCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var sb = new StringBuilder();
            int i = 0;
            while (i < input.Length)
            {
                int idx = input.IndexOf(find, i, cmp);
                if (idx < 0) { sb.Append(input.Substring(i)); break; }
                sb.Append(input.Substring(i, idx - i));
                sb.Append(rep);
                i = idx + find.Length;
            }
            return sb.ToString();
        }
    }
}
