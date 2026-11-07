// 还原前选择年份的小窗：列出备份包里包含的年份库，勾选要还原的那几个（可多选）。
// 铁律：按钮文字纯汉字；窗口只做选择，不做任何文件写入。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace caxa3dexbTechSpecsbak
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal sealed class YearPickForm : Form
    {
        private readonly CheckedListBox _list = new CheckedListBox();
        private readonly List<PackEntry> _entries;
        private readonly Button _btnAll = new Button();
        private readonly Button _btnNone = new Button();
        private readonly Button _btnOk = new Button();
        private readonly Button _btnCancel = new Button();
        private readonly Label _info = new Label();

        /// <summary>用户勾选的条目；取消时为空列表。</summary>
        public List<PackEntry> Selected { get; private set; }

        public YearPickForm(List<PackEntry> entries, string zipPath, string created)
        {
            _entries = entries ?? new List<PackEntry>();
            Selected = new List<PackEntry>();
            BuildUi(zipPath, created);
        }

        private void BuildUi(string zipPath, string created)
        {
            Text = "选择要还原的年份";
            ClientSize = new Size(660, 380);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            _info.SetBounds(12, 10, 636, 38);
            _info.Text = "备份包：" + zipPath + "\r\n制作时间：" + created + "　　共 " + _entries.Count + " 个年份库";

            _list.SetBounds(12, 52, 636, 264);
            _list.CheckOnClick = true;
            _list.IntegralHeight = false;
            _list.HorizontalScrollbar = true;
            foreach (PackEntry e in _entries)
            {
                _list.Items.Add(e.Display, true);      // 默认全选
            }

            _btnAll.SetBounds(12, 328, 88, 28);
            _btnAll.Text = "全选";
            _btnAll.Click += OnAllClick;

            _btnNone.SetBounds(108, 328, 88, 28);
            _btnNone.Text = "全不选";
            _btnNone.Click += OnNoneClick;

            _btnOk.SetBounds(464, 328, 88, 28);
            _btnOk.Text = "确定";
            _btnOk.DialogResult = DialogResult.OK;
            _btnOk.Click += OnOkClick;

            _btnCancel.SetBounds(560, 328, 88, 28);
            _btnCancel.Text = "取消";
            _btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { _info, _list, _btnAll, _btnNone, _btnOk, _btnCancel });
            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void OnAllClick(object sender, EventArgs e)
        {
            for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true);
        }

        private void OnNoneClick(object sender, EventArgs e)
        {
            for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, false);
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            Selected.Clear();
            for (int i = 0; i < _list.Items.Count && i < _entries.Count; i++)
            {
                if (_list.GetItemChecked(i)) Selected.Add(_entries[i]);
            }
            if (Selected.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一个年份", "未选择", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            }
        }
    }
}
