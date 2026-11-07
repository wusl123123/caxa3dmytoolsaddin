using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace Caxa3dcudeaddin
{
    internal sealed partial class MainForm : Form
    {
        private readonly IZBaseApp _siteApp;   // Addin 站点提供的应用对象
        private CaxaSession _session;
        private string _versionText;

        /// <summary>上次输入尺寸（mm）的保存路径，启动时回填。</summary>
        private static readonly string DimsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Caxa3dcudeaddin", "last-dims.txt");

        // ↓↓↓ 键盘归属抢救：Ctrl+C/V/X/A/Z 归本窗口自己的文本框（机理/开关/取证见 Caxa3dMyTools.Contracts.ChordRescue）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Caxa3dMyTools.Contracts.ChordRescue.Install(this, msg => Diagnostics.Log(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口关闭"); }
        protected override void WndProc(ref Message m) { if (Caxa3dMyTools.Contracts.ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑↑↑

        public MainForm(IZBaseApp siteApp)
        {
            _siteApp = siteApp;
            InitializeComponent();
            Diagnostics.UiLogSink = AppendUiLog;
            this.FormClosing += MainForm_FormClosing;

            // 对齐参考项目：在 Add-in 加载阶段（InitSelf 线程）就预创建窗口句柄。
            // 若推迟到功能区 OnClick 回调里才创建句柄，窗口创建/激活过程中的跨窗口
            // 消息会与 CAXA 主线程（正同步等待事件回调返回）互等 → 直接死锁
            // （2026-10-22 实测：点击按钮后 CAXA 无响应，日志停在 Button_OnClick 之前）。
            if (!IsHandleCreated) CreateHandle();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Diagnostics.Log("MainForm.HandleCreated");
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Diagnostics.Log("Form closing, reason=" + e.CloseReason);
            SaveDims();
            // 进程内会话不释放 RCW（对象归 CAXA 所有），只释放接口指针
            DropSession();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            Diagnostics.Log("MainForm.Load");
            LoadDims();
            lblStatus.ForeColor = System.Drawing.Color.DimGray;
            lblStatus.Text = "已连接（进程内）";
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Diagnostics.Log("MainForm.Shown");
        }

        /// <summary>解析三个尺寸输入（mm，需为正数）。失败时弹出提示并返回 false。</summary>
        private bool TryReadDims(out double lx, out double ly, out double lz)
        {
            lx = ly = lz = 0;
            if (!TryParseMm(txtLength.Text, out lx) ||
                !TryParseMm(txtWidth.Text, out ly) ||
                !TryParseMm(txtHeight.Text, out lz))
            {
                ShowError("请输入有效的长、宽、高（大于 0 的数字，单位 mm）。");
                return false;
            }
            return true;
        }

        private static bool TryParseMm(string text, out double mm)
        {
            // 同时接受当前区域与不变区域的小数点写法
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out mm) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out mm))
                return false;
            return mm > 0 && mm <= 100000;
        }

        private void LoadDims()
        {
            try
            {
                if (!File.Exists(DimsFile)) return;
                string[] parts = File.ReadAllText(DimsFile).Trim().Split(',');
                if (parts.Length != 3) return;
                double lx, ly, lz;
                if (TryParseMm(parts[0], out lx) && TryParseMm(parts[1], out ly) && TryParseMm(parts[2], out lz))
                {
                    txtLength.Text = parts[0].Trim();
                    txtWidth.Text = parts[1].Trim();
                    txtHeight.Text = parts[2].Trim();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("LoadDims failed: " + ex.Message);
            }
        }

        private void SaveDims()
        {
            try
            {
                double lx, ly, lz;
                if (!TryReadDimsSilent(out lx, out ly, out lz)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(DimsFile));
                File.WriteAllText(DimsFile, string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2}", lx, ly, lz));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SaveDims failed: " + ex.Message);
            }
        }

        private bool TryReadDimsSilent(out double lx, out double ly, out double lz)
        {
            lx = ly = lz = 0;
            return TryParseMm(txtLength.Text, out lx) &&
                   TryParseMm(txtWidth.Text, out ly) &&
                   TryParseMm(txtHeight.Text, out lz);
        }

        /// <summary>
        /// 确保进程内会话可用。会话通常与 CAXA 同生命周期；
        /// Ping 失败（服务器内部状态异常）时丢弃旧会话并重建。
        /// </summary>
        private void EnsureConnected()
        {
            if (_session != null && CaxaAutomation.Ping(_session))
            {
                SetConnectedStatus();
                return;
            }
            DropSession();
            Diagnostics.Log("EnsureConnected: building in-process session");

            _session = CaxaAutomation.ConnectFromSite(_siteApp);
            _versionText = CaxaAutomation.GetConnectedVersionText(_session);
            SetConnectedStatus();
            Diagnostics.Log("EnsureConnected: done, version=" + (_versionText ?? "<unknown>"));
        }

        private void SetConnectedStatus()
        {
            lblStatus.ForeColor = System.Drawing.Color.Black;
            lblStatus.Text = string.IsNullOrEmpty(_versionText)
                ? "已连接（进程内）"
                : "已连接（进程内）: " + _versionText;
        }

        private void DropSession()
        {
            if (_session == null) return;
            try { _session.Dispose(); } catch { }
            _session = null;
            _versionText = null;
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void btnCreate_Click(object sender, EventArgs e)
        {
            Diagnostics.Log("btnCreate.Click");
            double lx, ly, lz;
            if (!TryReadDims(out lx, out ly, out lz)) return;
            Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                "dims: L(X)={0} W(Y)={1} H(Z)={2} mm", lx, ly, lz));
            btnCreate.Enabled = false;

            try
            {
                // 最多两轮：第一轮失败且服务器状态异常时，重建会话后重试一次
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        EnsureConnected();
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Log("Connect failed: " + ex);
                        lblStatus.ForeColor = System.Drawing.Color.Firebrick;
                        lblStatus.Text = "CAXA 3D 会话建立失败（重新打开窗口重试）";
                        ShowError("建立 CAXA 3D 会话失败：" + Environment.NewLine + ex.Message);
                        return;
                    }

                    lblStatus.ForeColor = System.Drawing.Color.Black;
                    lblStatus.Text = attempt > 1 ? "会话已重建，正在建立立方体 …" : "正在建立立方体 …";
                    lblResult.Text = string.Empty;
                    try
                    {
                        CaxaAutomation.CreateCubeAtOrigin(_session, lx, ly, lz);
                        SaveDims();
                        SetConnectedStatus();
                        lblResult.ForeColor = System.Drawing.Color.Green;
                        lblResult.Text = string.Format(CultureInfo.InvariantCulture,
                            "立方体建立成功（{0} × {1} × {2} mm，原点）", lx, ly, lz);
                        return;
                    }
                    catch (Exception ex)
                    {
                        bool serverDead = !CaxaAutomation.Ping(_session);
                        Diagnostics.Log(string.Format(
                            "Create attempt {0} failed (serverDead={1}): {2}", attempt, serverDead, ex));
                        if (attempt == 1 && serverDead)
                        {
                            DropSession();
                            lblStatus.Text = "检测到服务器状态异常，正在重建会话 …";
                            continue;
                        }
                        if (_session != null) SetConnectedStatus();
                        else
                        {
                            lblStatus.ForeColor = System.Drawing.Color.Firebrick;
                            lblStatus.Text = "CAXA 3D 会话失效（重新打开窗口重试）";
                        }
                        lblResult.ForeColor = System.Drawing.Color.Firebrick;
                        lblResult.Text = "立方体建立失败";
                        ShowError("建立立方体失败：" + Environment.NewLine + ex.Message);
                        return;
                    }
                }
            }
            finally
            {
                btnCreate.Enabled = true;
            }
        }

        /// <summary>线程安全地把日志追加到界面日志区。</summary>
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
                // 控件容量保护：超过 30000 字符时裁掉前半部分
                if (txtLog.TextLength > 30000)
                    txtLog.Text = txtLog.Text.Substring(15000);
                txtLog.AppendText(line + Environment.NewLine);
            }
            catch
            {
                // 界面日志失败不影响主流程
            }
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            txtLog.Clear();
        }

        private void btnCopyLog_Click(object sender, EventArgs e)
        {
            try
            {
                Clipboard.SetText(txtLog.Text.Length > 0 ? txtLog.Text : "(日志为空)");
                lblResult.ForeColor = System.Drawing.Color.DimGray;
                lblResult.Text = "日志已复制到剪贴板";
            }
            catch
            {
                // 剪贴板偶尔被占用，忽略
            }
        }
    }
}
