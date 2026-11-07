// 从当前装配体导入选项（Win2000 极简风）。
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    public class PopupAssemblyOptions : Form
    {
        private readonly CheckBox _cbSuppressed = new CheckBox();
        private readonly CheckBox _cbBomExcluded = new CheckBox();
        private readonly Button _btnOk = new Button();
        private readonly Button _btnCancel = new Button();

        public bool ExcludeSuppressed { get { return _cbSuppressed.Checked; } }
        public bool ExcludeBomExcluded { get { return _cbBomExcluded.Checked; } }

        public PopupAssemblyOptions()
        {
            Font = new Font("宋体", 9F);
            Text = "装配体导入选项";
            ClientSize = new Size(340, 168);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            _cbSuppressed.SetBounds(20, 18, 300, 24);
            _cbSuppressed.Text = "排除压缩件（且不计数）";
            _cbSuppressed.Checked = true;   // 默认勾选

            _cbBomExcluded.SetBounds(20, 50, 320, 24);
            _cbBomExcluded.Text = "排除不在BOM的零件（明细表未输出,不计数）";
            _cbBomExcluded.Checked = true;  // 默认勾选

            _btnOk.SetBounds(130, 108, 88, 28);
            _btnOk.Text = "确定";
            _btnOk.DialogResult = DialogResult.OK;
            _btnCancel.SetBounds(226, 108, 88, 28);
            _btnCancel.Text = "取消";
            _btnCancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { _cbSuppressed, _cbBomExcluded, _btnOk, _btnCancel });
            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }
    }
}
