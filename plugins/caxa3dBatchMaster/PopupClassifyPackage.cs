// 「分类打包」弹窗（11-10 用户指令）：用户先指定存放文件夹 + 自定义属性名，再执行。
//   · 属性名留空 ⇒ 全部列表文件复制到「打开的设计环境零件名」同名文件夹。
//   · 长流程走主窗体 RunLong（置运行态 + 主窗体「停止」可用）；本窗体是模态的，
//     所以本地必须再给一个「停止」按钮（铁律 6）。
//   · Win2000 极简风：不启用视觉主题、宋体 9pt、系统色、纯汉字按钮、固定锚定。
//   · 收尾不弹 MessageBox（模态框会禁用 CAXA 主界面），结果只写状态行 + 主窗体日志 + 目标目录 CSV。
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;   // ChordRescue 唯一实现（规则第 37 节）——子插件不迁本地副本

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    public class PopupClassifyPackage : Form
    {
        // ↓↓↓ 键盘归属抢救：两个 TextBox 的 Ctrl+C/V/X/A/Z 归本弹窗（机理/开关/取证见 ChordRescue，规则第 37 节）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ChordRescue.Install(this, msg => Diagnostics.Trace(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); ChordRescue.Uninstall(this, "窗口关闭"); }
        protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑↑↑

        /// <summary>同一次 CAXA 会话内记住上次存放文件夹，省得每次重新浏览。</summary>
        private static string _lastDestRoot = "";

        private readonly TextBox _txtDest = new TextBox();
        private readonly TextBox _txtProp = new TextBox();
        private readonly Label _lblStatus = new Label();
        private readonly Button _btnBrowse = new Button();
        private readonly Button _btnRun = new Button();
        private readonly Button _btnStop = new Button();
        private readonly Button _btnClose = new Button();

        // 11-10 追加（参考 caxa3dButton「批量格式转换」）：打包时一并勾选要复制/转换的格式。
        // ics/exb＝复制源（默认勾，保持原行为）；x_t/stp/sat/dwg/dxf/pdf＝转换产物（默认不勾）。
        private readonly CheckBox _chkIcs = new CheckBox();
        private readonly CheckBox _chkXt = new CheckBox();
        private readonly CheckBox _chkStp = new CheckBox();
        private readonly CheckBox _chkSat = new CheckBox();
        private readonly CheckBox _chkExb = new CheckBox();
        private readonly CheckBox _chkDwg = new CheckBox();
        private readonly CheckBox _chkDxf = new CheckBox();
        private readonly CheckBox _chkPdf = new CheckBox();
        private readonly GroupBox _grpFmt = new GroupBox();

        private readonly Action<Action> _runLong;
        private readonly Func<string, string, ConvertOptions, ClassifyResult> _execute;
        private readonly Action<string> _log;
        private readonly Action _requestStop;

        public PopupClassifyPackage(Action<Action> runLong, Func<string, string, ConvertOptions, ClassifyResult> execute,
            Action<string> log, Action requestStop)
        {
            _runLong = runLong;
            _execute = execute;
            _log = log;
            _requestStop = requestStop;

            Font = new Font("宋体", 9F);
            Text = "分类打包";
            ClientSize = new Size(680, 364);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MinimumSize = new Size(680, 364);

            var lblDest = new Label { Text = "存放文件夹" };
            lblDest.SetBounds(14, 16, 90, 20);
            _txtDest.SetBounds(110, 14, 440, 22);
            _txtDest.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _txtDest.Text = _lastDestRoot;
            _btnBrowse.SetBounds(558, 13, 108, 24);
            _btnBrowse.Text = "浏览";
            _btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnBrowse.Click += BtnBrowse_Click;

            var lblProp = new Label { Text = "自定义属性名" };
            lblProp.SetBounds(14, 48, 90, 20);
            _txtProp.SetBounds(110, 46, 440, 22);
            _txtProp.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var lblHint = new Label { Text = "留空＝全部复制到设计环境同名文件夹" };
            lblHint.SetBounds(558, 48, 108, 36);
            lblHint.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            var lblRule = new Label { Text = "规则说明" };
            lblRule.SetBounds(14, 78, 90, 20);
            var lblRuleText = new Label();
            lblRuleText.SetBounds(110, 78, 546, 34);
            lblRuleText.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            lblRuleText.Text =
                "按属性名在表格第 1 行找同名列，属性值相同的行进同一个文件夹，"
                + "每件的 .ics 与 .exb 一起复制（例如「SC材料」值「Q235A板T=2」）。"
                + "属性值为空归「未填写」；属性名留空则全量复制。同名不同内容另存副本，不覆盖已有文件。";

            // ---- 打包格式勾选区（参考 caxa3dButton「批量格式转换」） ----
            _grpFmt.SetBounds(14, 116, 650, 132);
            _grpFmt.Text = "打包格式（勾选＝复制 / 转换；3D 用 RCW 直导，2D 需先「图纸环境预热」）";
            _grpFmt.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            // GroupBox 内的子控件坐标都是“相对 GroupBox 客户区”，不是相对窗体（GroupBox 高仅 132，子控件 y 超出会被裁掉）
            var lbl3d = new Label { Text = "3D 格式" };
            lbl3d.SetBounds(12, 16, 60, 20);
            _chkIcs.SetBounds(12, 36, 96, 20); _chkIcs.Text = "复制 ics"; _chkIcs.Checked = true; _chkIcs.AutoCheck = true;
            _chkXt.SetBounds(130, 36, 70, 20); _chkXt.Text = "→ x_t"; _chkXt.AutoCheck = true;
            _chkStp.SetBounds(215, 36, 70, 20); _chkStp.Text = "→ stp"; _chkStp.AutoCheck = true;
            _chkSat.SetBounds(300, 36, 70, 20); _chkSat.Text = "→ sat"; _chkSat.AutoCheck = true;
            var lbl2d = new Label { Text = "2D 格式" };
            lbl2d.SetBounds(12, 64, 60, 20);
            _chkExb.SetBounds(12, 84, 96, 20); _chkExb.Text = "复制 exb"; _chkExb.Checked = true; _chkExb.AutoCheck = true;
            _chkDwg.SetBounds(130, 84, 70, 20); _chkDwg.Text = "→ dwg"; _chkDwg.AutoCheck = true;
            _chkDxf.SetBounds(215, 84, 70, 20); _chkDxf.Text = "→ dxf"; _chkDxf.AutoCheck = true;
            _chkPdf.SetBounds(300, 84, 70, 20); _chkPdf.Text = "→ pdf"; _chkPdf.AutoCheck = true;
            _grpFmt.Controls.AddRange(new Control[] { lbl3d, _chkIcs, _chkXt, _chkStp, _chkSat, lbl2d, _chkExb, _chkDwg, _chkDxf, _chkPdf });

            _lblStatus.SetBounds(14, 256, 646, 44);
            _lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lblStatus.Text = "结果明细见主窗体「日志」按钮，以及存放文件夹里的「分类打包清单-时间戳.csv」。";

            _btnRun.SetBounds(290, 318, 120, 30);
            _btnRun.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnRun.Text = "执行分类打包";
            _btnRun.Click += BtnRun_Click;

            // 铁律 6：本窗体模态，主窗体的「停止」点不到，长流程必须本地给停止口
            _btnStop.SetBounds(426, 318, 76, 30);
            _btnStop.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnStop.Text = "停止";
            _btnStop.BackColor = Color.MistyRose;
            _btnStop.Enabled = false;
            _btnStop.Click += (s, e) =>
            {
                _lblStatus.Text = "已请求停止：当前行处理完即中断（已复制的文件保留）。";
                _requestStop?.Invoke();
            };

            _btnClose.SetBounds(582, 318, 86, 30);
            _btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnClose.Text = "关闭";
            _btnClose.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] {
                lblDest, _txtDest, _btnBrowse, lblProp, _txtProp, lblHint,
                lblRule, lblRuleText, _grpFmt, _lblStatus, _btnRun, _btnStop, _btnClose });
            CancelButton = _btnClose;
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择分类打包的存放文件夹（程序会在其下按属性值建子文件夹）";
                dlg.ShowNewFolderButton = true;
                string cur = _txtDest.Text.Trim();
                if (cur.Length > 0 && Directory.Exists(cur)) dlg.SelectedPath = cur;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtDest.Text = dlg.SelectedPath;
                    _lblStatus.Text = "存放文件夹已指定：" + dlg.SelectedPath;
                }
            }
        }

        private void BtnRun_Click(object sender, EventArgs e)
        {
            string dest = _txtDest.Text.Trim();
            string prop = _txtProp.Text.Trim();
            if (dest.Length == 0)
            {
                _lblStatus.Text = "请先点「浏览」指定存放文件夹。";
                return;
            }
            _lastDestRoot = dest;

            _btnRun.Enabled = false;
            _btnBrowse.Enabled = false;
            _btnStop.Enabled = true;
            // 长流程期间禁掉「关闭」：DoEvents 泵消息时若让用户关掉本模态窗，收尾再写 _lblStatus 会撞已释放控件
            _btnClose.Enabled = false;
            try
            {
                ClassifyResult res = null;
                var opt = new ConvertOptions
                {
                    Ics = _chkIcs.Checked, Xt = _chkXt.Checked, Stp = _chkStp.Checked, Sat = _chkSat.Checked,
                    Exb = _chkExb.Checked, Dwg = _chkDwg.Checked, Dxf = _chkDxf.Checked, Pdf = _chkPdf.Checked
                };
                Action run = () => { res = _execute(dest, prop, opt); };
                if (_runLong != null) _runLong(run); else run();
                _lblStatus.Text = res == null ? "分类打包未返回结果，请看主窗体日志。" : res.Message;
                Flash();
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "分类打包异常：" + ex.Message;
                try { _log?.Invoke("分类打包异常：" + ex); } catch { }
            }
            finally
            {
                _btnRun.Enabled = true;
                _btnBrowse.Enabled = true;
                _btnStop.Enabled = false;
                _btnClose.Enabled = true;
            }
        }

        // 收尾不弹 MessageBox：让窗口在任务栏闪一下，把注意力带回弹窗本身
        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        private void Flash()
        {
            try
            {
                var fi = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                    hwnd = Handle,
                    dwFlags = 3 | 12,   // FLASHW_ALL | FLASHW_TIMERNOFG
                    uCount = 3,
                    dwTimeout = 0
                };
                FlashWindowEx(ref fi);
            }
            catch { }
        }
    }
}
