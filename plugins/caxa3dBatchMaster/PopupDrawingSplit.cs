// 「图号分离」弹窗（11-06 用户指令 6）：两个按钮——
//   打开图纸分离规则文件 ＝ 用系统关联程序打开**当前生效路径**那份（%APPDATA%\caxa3dBatchMaster\），改完保存即热重载；
//   执行图号分离 ＝ 逐行切 D 列零件名、填回 SC* 列并三色标记（不写 3D 属性、不保存装配体）。
// 界面沿用本工程 Win2000 极简风；长流程走主窗体的 RunLong（置运行态＋使「停止」可点，铁律 6），
// 收尾不弹 MessageBox（模态框会禁用 CAXA 主界面），结果只写这里的状态行＋主窗体日志。
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;   // ChordRescue 唯一实现（规则第 37 节）

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    public class PopupDrawingSplit : Form
    {
        // ↓↓↓ 键盘归属抢救：规则文件路径 TextBox 的 Ctrl+C/V/X/A/Z 归本弹窗（机理/开关/取证见 ChordRescue，规则第 37 节）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ChordRescue.Install(this, msg => Diagnostics.Trace(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); ChordRescue.Uninstall(this, "窗口关闭"); }
        protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑↑↑

        private readonly TextBox _txtPath = new TextBox();
        private readonly ListBox _lstRules = new ListBox();
        private readonly Label _lblStatus = new Label();
        private readonly Button _btnOpen = new Button();
        private readonly Button _btnRun = new Button();
        private readonly Button _btnStop = new Button();
        private readonly Button _btnClose = new Button();

        private readonly Action<Action> _runLong;     // 主窗体的 RunLong 包装（置 _running／使停止键可用）
        private readonly Func<int> _execute;          // BatchProcessor.SplitDrawingNumbers
        private readonly Action<string> _log;
        private readonly Action _requestStop;

        public PopupDrawingSplit(Action<Action> runLong, Func<int> execute, Action<string> log, Action requestStop)
        {
            _runLong = runLong;
            _execute = execute;
            _log = log;
            _requestStop = requestStop;

            Font = new Font("宋体", 9F);
            Text = "图号分离";
            ClientSize = new Size(620, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MinimumSize = new Size(620, 300);

            var lblPath = new Label { Text = "规则文件（生效路径）" };
            lblPath.SetBounds(14, 12, 120, 20);
            _txtPath.SetBounds(140, 10, 350, 22);
            _txtPath.ReadOnly = true;
            _txtPath.BackColor = Color.White;

            _btnOpen.SetBounds(496, 9, 110, 24);
            _btnOpen.Text = "打开规则文件";
            _btnOpen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnOpen.Click += BtnOpen_Click;

            var lblList = new Label { Text = "生效规则（按 A→E 顺序自动试配段数，第一条段数吻合即用）" };
            lblList.SetBounds(14, 42, 480, 20);
            _lstRules.SetBounds(14, 64, 592, 122);
            _lstRules.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lstRules.HorizontalScrollbar = true;
            _lstRules.IntegralHeight = false;

            _lblStatus.SetBounds(14, 194, 592, 40);
            _lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lblStatus.Text = "分离来源＝表格 D 列（零件名）；目标＝表头写着 SC图号／SC名称／SC规格／SC材料 的那些列。"
                + "只填表格单元格并染色（成功绿／失败红），不写 3D 属性、不保存装配体。";

            _btnRun.SetBounds(236, 248, 110, 30);
            _btnRun.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnRun.Text = "执行图号分离";
            _btnRun.Click += BtnRun_Click;

            // 铁律 6：本窗体是模态的，主窗体的「停止」在此期间点不到，长流程必须在本地给一个停止口
            _btnStop.SetBounds(352, 248, 70, 30);
            _btnStop.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnStop.Text = "停止";
            _btnStop.BackColor = Color.MistyRose;
            _btnStop.Enabled = false;
            _btnStop.Click += (s, e) =>
            {
                _lblStatus.Text = "已请求停止：当前行处理完即中断（已填进表格的值保留）。";
                _requestStop?.Invoke();
            };

            var btnReload = new Button { Text = "重读规则" };
            btnReload.SetBounds(428, 248, 84, 30);
            btnReload.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnReload.Click += (s, e) => RefreshRules("已重新读取规则文件。");

            _btnClose.SetBounds(518, 248, 88, 30);
            _btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnClose.Text = "关闭";
            _btnClose.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblPath, _txtPath, _btnOpen, lblList, _lstRules, _lblStatus, _btnRun, _btnStop, btnReload, _btnClose });
            CancelButton = _btnClose;

            Shown += (s, e) => RefreshRules(null);
        }

        private void RefreshRules(string prefix)
        {
            try
            {
                string note = SplitRules.InitAndDescribe();
                if (note != null) _log?.Invoke(note);
                _txtPath.Text = SplitRules.EffectivePath;
                _lstRules.Items.Clear();
                foreach (SplitRule r in SplitRules.Current())
                    _lstRules.Items.Add(r.Text + "    【" + r.Describe() + "】");
                if (!string.IsNullOrEmpty(prefix)) _lblStatus.Text = prefix + " 生效条数 " + _lstRules.Items.Count
                    + "｜文件 " + SplitRules.EffectivePath;
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "读取规则文件失败：" + ex.Message;
                _log?.Invoke("图号分离规则读取失败：" + ex.Message);
            }
        }

        private void BtnOpen_Click(object sender, EventArgs e)
        {
            string err;
            if (SplitRules.OpenEffectiveFile(out err))
                _lblStatus.Text = "已用系统关联程序打开：" + SplitRules.EffectivePath + "　（改完保存→点「重读规则」或重新打开本窗口即生效）";
            else
            {
                _lblStatus.Text = "打开规则文件失败：" + err;
                _log?.Invoke("打开图号分离规则文件失败：" + err);
            }
        }

        private void BtnRun_Click(object sender, EventArgs e)
        {
            // 规则可能是用户在外部改过的：执行前无条件重读一次，保证「看到的规则」＝「跑的策略」
            RefreshRules(null);
            _btnRun.Enabled = false;
            _btnOpen.Enabled = false;
            _btnStop.Enabled = true;
            try
            {
                int matched = 0;
                Action run = () => { matched = _execute(); };
                if (_runLong != null) _runLong(run); else run();
                _lblStatus.Text = "图号分离结束：命中 " + matched + " 行（明细见主窗体「日志」按钮）。颜色＝绿填上／红没填上。";
                Flash();
            }
            catch (Exception ex)
            {
                _lblStatus.Text = "图号分离异常：" + ex.Message;
                _log?.Invoke("图号分离异常：" + ex.Message);
            }
            finally
            {
                _btnRun.Enabled = true;
                _btnOpen.Enabled = true;
                _btnStop.Enabled = false;
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
                    dwFlags = 3 | 12,   // FLASHW_ALL(标题栏+任务栏) | FLASHW_TIMERNOFG(持续到窗口获得前台焦点)
                    uCount = 3,
                    dwTimeout = 0
                };
                FlashWindowEx(ref fi);
            }
            catch { }
        }
    }
}
