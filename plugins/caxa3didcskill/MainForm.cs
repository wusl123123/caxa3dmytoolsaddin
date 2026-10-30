using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace caxa3didcskill
{
    public partial class MainForm : Form
    {
        private bool _autoRunning;
        private bool _monitorRunning;
        private bool _topMost = true;   // 默认前置

        /// <summary>子插件独立日志路径（不依赖原 Program.cs）。</summary>
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3didcskill_trace.log");

        public MainForm()
        {
            InitializeComponent();
            // 默认窗口前置（铁律：所有内置子插件窗口默认置顶）
            TopMost = _topMost;
            ApplyTopMostVisual();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            Log("MainForm.Load（窗口默认前置）");
            // 原来的 RefreshLinkStatus 已按铁律取消（不显示连接状态）
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Log("Form closing, reason=" + e.CloseReason);
            StopAuto();
            StopMonitor();
        }

        /// <summary>窗口前置按钮点击：切换置顶状态。</summary>
        private void btnTopMost_Click(object sender, EventArgs e)
        {
            _topMost = !_topMost;
            TopMost = _topMost;
            ApplyTopMostVisual();
            Log(_topMost ? "窗口已锁定在最上层" : "窗口已取消最上层");
        }

        private void ApplyTopMostVisual()
        {
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

        // ====== 日志（自包含，不依赖原 Program.cs） ======
        private void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [tid " +
                Thread.CurrentThread.ManagedThreadId + "] " + msg;
            try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
            AppendUiLog(line);
        }

        private void AppendUiLog(string line)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => AppendUiLog(line)));
                    return;
                }
                if (txtLog.TextLength > 30000)
                    txtLog.Text = txtLog.Text.Substring(15000);
                txtLog.AppendText(line + Environment.NewLine);
            }
            catch { }
        }

        // ====== 操作按钮 ======

        private void btnRefreshStatus_Click(object sender, EventArgs e)
        {
            Log("btnRefreshStatus.Click");
            // 按铁律已取消状态显示
        }

        private void btnMonitor_Click(object sender, EventArgs e)
        {
            if (_monitorRunning) { StopMonitor(); return; }

            double minutes;
            if (!TryParseInterval(txtMonitorInterval.Text, out minutes))
            {
                MessageBox.Show(this, "请输入有效的监控间隔（大于0的数字，单位min）。",
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            int intervalMs = (int)Math.Round(minutes * 60 * 1000);
            tmrMonitor.Interval = intervalMs;
            tmrMonitor.Start();
            _monitorRunning = true;
            btnMonitor.Text = "停止监控";
            txtMonitorInterval.Enabled = false;
            Log(string.Format("前台状态监控已启动：间隔 {0} min", minutes));
        }

        private void StopMonitor()
        {
            if (!_monitorRunning) return;
            tmrMonitor.Stop();
            _monitorRunning = false;
            btnMonitor.Text = "启动监控";
            txtMonitorInterval.Enabled = true;
            Log("前台状态监控已停止");
        }

        private void tmrMonitor_Tick(object sender, EventArgs e) { /* 状态显示已取消 */ }

        private void btnManual_Click(object sender, EventArgs e)
        {
            Log("btnManual.Click: manual cleanup start");
            btnManual.Enabled = false; btnAuto.Enabled = false;
            try
            {
                int killed = ProcessKiller.KillBackgroundIroncadProcesses();
                Log(string.Format("手动运行完成：杀死 {0} 个后台进程", killed));
                MessageBox.Show(this,
                    string.Format("清理完成，共杀死 {0} 个后台进程。", killed),
                    "手动运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("manual cleanup failed: " + ex);
                MessageBox.Show(this, "清理失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { btnManual.Enabled = true; btnAuto.Enabled = true; }
        }

        private void btnAuto_Click(object sender, EventArgs e)
        {
            if (_autoRunning) { StopAuto(); return; }

            double minutes;
            if (!TryParseInterval(txtInterval.Text, out minutes))
            {
                MessageBox.Show(this, "请输入有效的时间间隔（大于0的数字，单位min）。",
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            int intervalMs = (int)Math.Round(minutes * 60 * 1000);
            tmrAuto.Interval = intervalMs;
            tmrAuto.Start();
            _autoRunning = true;
            btnAuto.Text = "停止自动";
            btnManual.Enabled = false;
            txtInterval.Enabled = false;
            Log(string.Format("自动运行已启动：间隔 {0} min", minutes));
        }

        private void StopAuto()
        {
            if (!_autoRunning) return;
            tmrAuto.Stop();
            _autoRunning = false;
            btnAuto.Text = "自动运行";
            btnManual.Enabled = true;
            txtInterval.Enabled = true;
            Log("自动运行已停止");
        }

        private void tmrAuto_Tick(object sender, EventArgs e)
        {
            Log("tmrAuto.Tick: scheduled cleanup start");
            try
            {
                int killed = ProcessKiller.KillBackgroundIroncadProcesses();
                Log(string.Format("自动运行完成：杀死 {0} 个后台进程", killed));
            }
            catch (Exception ex) { Log("auto cleanup failed: " + ex.Message); }
        }

        private static bool TryParseInterval(string text, out double minutes)
        {
            minutes = 0;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out minutes) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out minutes))
                return false;
            return minutes > 0 && minutes <= 1440;
        }

        private void btnClearLog_Click(object sender, EventArgs e) { txtLog.Clear(); }

        private void btnCopyLog_Click(object sender, EventArgs e)
        {
            try { Clipboard.SetText(txtLog.Text.Length > 0 ? txtLog.Text : "(日志为空)"); } catch { }
        }
    }
}
