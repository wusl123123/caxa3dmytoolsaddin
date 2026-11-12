// 图号分离窗口（移植自 caxa3dtracker\DrawingNoSepForm.cs）：
//   · 规则下拉框：A~E 五条内置规则（可外部 .txt 覆盖）
//   · 「手动图号分离」：用下方输入框的名称分离（未输则自动取选中件名称）
// 铁律：构造期不连 COM；收尾只走状态栏/日志；禁 MessageBox。
using System;
using System.Drawing;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal sealed class DrawingNoSepForm : Form
    {
        private readonly IZBaseApp _siteApp;
        private bool _busy;

        private readonly ComboBox _cboRule = new ComboBox();
        private readonly TextBox _txtName = new TextBox();
        private readonly Button _btnManual = new Button();
        private readonly TextBox _txtLog = new TextBox();
        private readonly Label _lblStatus = new Label();

        internal DrawingNoSepForm(IZBaseApp siteApp, bool topMost)
        {
            _siteApp = siteApp;
            BuildUi();
            TopMost = topMost;
            if (!IsHandleCreated) CreateHandle();
        }

        // ↓ 键盘归属抢救（规则第 37 节）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ChordRescue.Install(this, msg => Diagnostics.Log(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑

        private void BuildUi()
        {
            Text = "图号分离";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var lblRule = new Label { Text = "分离规则:", Location = new Point(12, 14), Size = new Size(60, 20) };
            _cboRule.SetBounds(78, 12, 350, 24);
            _cboRule.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (DrawingNoSepTool.Rule r in DrawingNoSepTool.LoadRules()) _cboRule.Items.Add(r);
            if (_cboRule.Items.Count > 0) _cboRule.SelectedIndex = 0;
            _cboRule.SelectedIndexChanged += delegate { DrawingNoSepTool.LastRule = SelectedRule; };
            DrawingNoSepTool.LastRule = SelectedRule;

            var lblName = new Label { Text = "分离名称:", Location = new Point(12, 44), Size = new Size(60, 20) };
            _txtName.SetBounds(78, 42, 350, 24);
            _txtName.Text = "";

            _btnManual.SetBounds(12, 74, 416, 30);
            _btnManual.Text = "手动图号分离";
            _btnManual.Click += BtnManual_Click;

            _txtLog.SetBounds(12, 114, 416, 144);
            _txtLog.Multiline = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.ReadOnly = true;
            _txtLog.WordWrap = false;
            _txtLog.BackColor = Color.White;

            _lblStatus.SetBounds(12, 264, 416, 20);
            _lblStatus.ForeColor = Color.DimGray;
            _lblStatus.Text = "先在视口/场景树选中零部件；或手动输入名称后点「手动」。";

            Controls.AddRange(new Control[] { lblRule, _cboRule, lblName, _txtName, _btnManual, _txtLog, _lblStatus });
        }

        private void AppendLog(string m)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(AppendLog), m); } catch { } return; }
            _txtLog.AppendText(m + "\r\n");
        }

        private void SetStatus(string text, Color color)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string, Color>(SetStatus), text, color); } catch { } return; }
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        private DrawingNoSepTool.Rule SelectedRule { get { return _cboRule.SelectedItem as DrawingNoSepTool.Rule; } }

        private void BtnManual_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true; _btnManual.Enabled = false;
            try
            {
                string src = _txtName.Text.Trim();
                if (src.Length == 0)
                {
                    IZPart part;
                    string elemName, err;
                    DrawingNoSepTool.GetSelectedPart(_siteApp, out part, out elemName, out err);
                    if (err != null) { SetStatus(err, Color.Firebrick); AppendLog(err); return; }
                    src = elemName;
                    _txtName.Text = src;
                }
                AppendLog("手动分离名称：\"" + src + "\"");
                DrawingNoSepTool.LastRule = SelectedRule;

                string result = DrawingNoSepTool.Run(_siteApp, SelectedRule, src, AppendLog);
                AppendLog(result);
                SetStatus(result.Split('\n')[0], Color.DarkGreen);
            }
            catch (Exception ex)
            {
                AppendLog("异常：" + ex.Message);
                SetStatus("图号分离异常：" + ex.Message, Color.Firebrick);
            }
            finally { _busy = false; _btnManual.Enabled = true; }
        }
    }
}
