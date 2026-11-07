using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace caxa3didcskill
{
    public partial class MainForm : Form
    {
        private bool _topMost = true;   // 默认前置
        private bool _autoRunning;
        private bool _cleanupRunning;
        private CancellationTokenSource _cts;

        public MainForm()
        {
            InitializeComponent();
            TopMost = _topMost;
            ApplyTopMostVisual();
            Diagnostics.LineAppended += OnTraceLine;
            FormClosed += delegate { Diagnostics.LineAppended -= OnTraceLine; };
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            Diagnostics.Log("MainForm.Load（窗口默认前置）");
            lblStatus.Text = "就绪：只清理无可见窗口的 IRONCAD 后台进程";
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Diagnostics.Log("窗口关闭，原因=" + e.CloseReason);
            StopAuto();
            if (_cleanupRunning && _cts != null)
            {
                _cts.Cancel();
                Diagnostics.Log("已请求停止清理（不强制结束进程）");
            }
        }

        // ====== 窗口前置 ======

        private void btnTopMost_Click(object sender, EventArgs e)
        {
            _topMost = !_topMost;
            TopMost = _topMost;
            ApplyTopMostVisual();
            Diagnostics.Log(_topMost ? "窗口已锁定在最上层" : "窗口已取消最上层");
        }

        private void ApplyTopMostVisual()
        {
            btnTopMost.UseVisualStyleBackColor = !_topMost;
            if (_topMost)
            {
                btnTopMost.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
                btnTopMost.ForeColor = System.Drawing.Color.White;
                btnTopMost.FlatStyle = FlatStyle.Flat;
                btnTopMost.FlatAppearance.BorderSize = 0;
            }
            else
            {
                btnTopMost.BackColor = System.Drawing.SystemColors.Control;
                btnTopMost.ForeColor = System.Drawing.SystemColors.ControlText;
                btnTopMost.FlatStyle = FlatStyle.Standard;
            }
        }

        // ====== 日志 ======

        private void OnTraceLine(string line)
        {
            AppendUiLog(line);
        }

        private void AppendUiLog(string line)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action<string>(AppendUiLog), line);
                    return;
                }
                if (txtLog.TextLength > 30000)
                    txtLog.Text = txtLog.Text.Substring(15000);
                txtLog.AppendText(line + Environment.NewLine);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // ====== 清理 ======

        private void btnManual_Click(object sender, EventArgs e)
        {
            if (_cleanupRunning)
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    Diagnostics.Log("手动停止：已请求停止清理");
                }
                return;
            }
            StartCleanup("手动运行");
        }

        private async void StartCleanup(string source)
        {
            if (_cleanupRunning)
            {
                Diagnostics.Log(source + "：已有清理在执行，跳过本次");
                return;
            }

            _cleanupRunning = true;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            UpdateUiState();
            lblStatus.Text = source + "：清理中…";

            try
            {
                // 清理含确认扫描与退出等待，必须在后台线程执行，UI 线程只泵消息
                CleanupResult result = await Task.Run(() => ProcessKiller.Run(token, Diagnostics.Log));

                if (result.Canceled)
                {
                    lblStatus.Text = source + "：已停止";
                }
                else
                {
                    lblStatus.Text = DateTime.Now.ToString("HH:mm:ss") + " " + result.SummaryShort();
                    Diagnostics.Log(source + "完成：" + result.Summary());
                    if (result.StuckExiting.Count > 0)
                    {
                        Diagnostics.Log("注意：「卡在退出」＝系统已给出退出码但进程仍在进程表里，杀进程对它无效，"
                            + "详见上面每个 pid 的取证行。");
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Exception(source + "失败", ex);
                lblStatus.Text = source + "失败：" + ex.Message;
            }
            finally
            {
                _cleanupRunning = false;
                if (_cts != null) { _cts.Dispose(); _cts = null; }
                UpdateUiState();
            }
        }

        private void btnAuto_Click(object sender, EventArgs e)
        {
            if (_autoRunning) { StopAuto(); return; }

            double minutes;
            if (!TryParseMinutes(txtInterval.Text, out minutes))
            {
                MessageBox.Show(this, "请输入 1~1440 之间的数字（单位：分钟）。",
                    "间隔无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            tmrAuto.Interval = (int)Math.Round(minutes * 60 * 1000);
            tmrAuto.Start();
            _autoRunning = true;
            UpdateUiState();
            Diagnostics.Log(string.Format("自动运行已启动：每 {0:0.##} min 清理一次", minutes));
        }

        private void StopAuto()
        {
            if (!_autoRunning) return;
            tmrAuto.Stop();
            _autoRunning = false;
            UpdateUiState();
            Diagnostics.Log("自动运行已停止");
        }

        private void tmrAuto_Tick(object sender, EventArgs e)
        {
            if (_cleanupRunning)
            {
                Diagnostics.Log("自动运行：上一次清理尚未结束，跳过本次");
                return;
            }
            StartCleanup("自动运行");
        }

        private void UpdateUiState()
        {
            if (IsDisposed) return;
            btnManual.Text = _cleanupRunning ? "停止清理" : "手动运行";
            btnManual.Enabled = !_autoRunning || _cleanupRunning;
            btnAuto.Text = _autoRunning ? "停止自动" : "自动运行";
            btnAuto.Enabled = !_cleanupRunning;
            txtInterval.Enabled = !_autoRunning && !_cleanupRunning;
        }

        private static bool TryParseMinutes(string text, out double minutes)
        {
            minutes = 0;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out minutes) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out minutes))
                return false;
            return minutes >= 1 && minutes <= 1440;
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            txtLog.Clear();
        }

        private void btnCopyLog_Click(object sender, EventArgs e)
        {
            try { Clipboard.SetText(txtLog.Text.Length > 0 ? txtLog.Text : "(日志为空)"); } catch { }
        }
    }
}
