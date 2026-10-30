using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace caxa3dexbtest
{
    /// <summary>
    /// 主窗口。进入程序不自动连接 CAXA（需求），由「刷新连接」显式建立/重建会话，
    /// 并检测是否处于图纸环境；「生成矩形」通过 CRX 桥接模块在当前图纸原点画 100x100 矩形。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly Button _btnRefresh;
        private readonly Button _btnRect;
        private readonly Button _btnInstall;
        private readonly Label _status;
        private readonly TextBox _log;

        private CaxaSession _session;

        public MainForm()
        {
            Text = "caxa3dexbtest — CAXA 3D 图纸环境 API 测试";
            Width = 760;
            Height = 500;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(600, 400);

            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 44,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(8, 8, 8, 4),
                WrapContents = false
            };

            _btnRefresh = new Button { Text = "刷新连接", Width = 110, Height = 28 };
            _btnRect = new Button { Text = "生成矩形", Width = 110, Height = 28 };
            _btnInstall = new Button { Text = "注册桥接模块", Width = 120, Height = 28 };
            _btnRefresh.Click += BtnRefresh_Click;
            _btnRect.Click += BtnRect_Click;
            _btnInstall.Click += BtnInstall_Click;
            panel.Controls.Add(_btnRefresh);
            panel.Controls.Add(_btnRect);
            panel.Controls.Add(_btnInstall);

            _status = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                ForeColor = Color.DimGray
            };

            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BackColor = Color.White
            };

            Controls.Add(_log);
            Controls.Add(_status);
            Controls.Add(panel);

            _status.Text = "未连接（进入程序不自动连接，请点击「刷新连接」）";
            Diagnostics.UiLogSink = AppendLog;
            Diagnostics.Log("UI ready — 等待用户点击「刷新连接」（按需求不自动连接 CAXA 3D）");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Diagnostics.UiLogSink = null;
            DisposeSession();
            base.OnFormClosed(e);
        }

        private void AppendLog(string line)
        {
            if (_log.IsDisposed) return;
            try
            {
                if (_log.InvokeRequired)
                {
                    _log.BeginInvoke(new Action<string>(AppendLog), line);
                    return;
                }
                _log.AppendText(line + Environment.NewLine);
            }
            catch { }
        }

        private void DisposeSession()
        {
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }
        }

        // ---- 刷新连接：重建会话并检测图纸环境 ----

        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            RunGuarded(delegate
            {
                DisposeSession();
                _session = CaxaAutomation.Connect();
                CaxaAutomation.EnsureFrameVisible();
                ReportEnvironment();
            });
        }

        private void ReportEnvironment()
        {
            EnvInfo env = CaxaAutomation.DetectEnvironment(_session);
            Color color;
            if (!env.Connected) color = Color.Firebrick;
            else if (env.IsDrawingEnv) color = Color.DarkGreen;
            else color = Color.DarkOrange;
            SetStatus(env.Describe(), color);
        }

        // ---- 生成矩形：原点 (0,0) 到 (100,100) ----

        private void BtnRect_Click(object sender, EventArgs e)
        {
            RunGuarded(delegate
            {
                if (_session == null || !CaxaAutomation.Ping(_session))
                {
                    Diagnostics.Log("rect: 无有效会话，先自动刷新连接");
                    DisposeSession();
                    _session = CaxaAutomation.Connect();
                }

                EnvInfo env = CaxaAutomation.DetectEnvironment(_session);
                if (!env.BridgeOnline)
                    throw new InvalidOperationException(
                        "CRX 桥接模块未在线。请点击「注册桥接模块」，然后重启 CAXA 3D 使其加载。");

                if (!env.IsDrawingEnv)
                    throw new InvalidOperationException(
                        "当前不在图纸环境。请在 CAXA 3D 中打开/新建一张工程图后再试。");

                string err = CaxaAutomation.BridgeDrawRect(0, 0, 100, 100, 12000);
                if (err == null)
                {
                    Diagnostics.Log("rect: OK — 已在当前图纸原点创建 100x100 矩形 (0,0)-(100,100)");
                    SetStatus("矩形已生成 ｜ (0,0)-(100,100) ｜ 文档: " +
                        (string.IsNullOrEmpty(env.DocName) ? "(未命名)" : env.DocName), Color.DarkGreen);
                }
                else
                {
                    throw new InvalidOperationException("生成矩形失败: " + err);
                }
            });
        }

        // ---- 注册桥接模块：.crx 放 CAXADraft\bin，Modules 下写 .info 登记 ----

        private void BtnInstall_Click(object sender, EventArgs e)
        {
            RunGuarded(delegate
            {
                string crx = FindBridgeCrx();
                if (crx == null)
                    throw new InvalidOperationException(
                        "未找到 ExbRectBridge.crx。请先编译 Bridge\\ExbRectBridge.vcxproj（Release|x64）。");

                string draftRoot = FindDraftRoot();
                if (draftRoot == null)
                    throw new InvalidOperationException(
                        "未在 %ProgramFiles%\\CAXA\\CAXA 3D 下找到 CAXADraft 目录。");

                string binDir = Path.Combine(draftRoot, "bin");
                string modulesDir = Path.Combine(draftRoot, "Modules");
                if (!Directory.Exists(binDir) || !Directory.Exists(modulesDir))
                    throw new InvalidOperationException("CAXADraft 目录结构不符合预期: " + draftRoot);

                string dstCrx = Path.Combine(binDir, "ExbRectBridge.crx");
                string dstInfo = Path.Combine(modulesDir, "MdlExbRectBridge.info");
                // 与内置模块登记格式一致：相对文件名，由 CAXADraft 到 bin 目录解析
                string infoXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                    "<Modules>\r\n  <File Template=\"ID_DFT_TMPL\">ExbRectBridge.crx</File>\r\n</Modules>\r\n";

                try
                {
                    File.Copy(crx, dstCrx, true);
                    File.WriteAllText(dstInfo, infoXml);
                }
                catch (UnauthorizedAccessException)
                {
                    Diagnostics.Log("install: 需要管理员权限，转用提权拷贝");
                    RunElevatedCopy(crx, dstCrx, infoXml, dstInfo);
                }

                Diagnostics.Log("install: 模块已登记 -> " + dstCrx);
                SetStatus("桥接模块已登记。请重启 CAXA 3D（模块在程序启动时加载），然后「刷新连接」。", Color.DarkOrange);
            });
        }

        private static string FindBridgeCrx()
        {
            // 作为子插件运行时，BaseDirectory 是 CAXA bin 目录，CRX 实际放在
            // 本插件 DLL 所在目录（与插件程序集同级），因此优先按程序集位置查找。
            string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(asmDir, "ExbRectBridge.crx"),
                Path.Combine(baseDir, "ExbRectBridge.crx"),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\Bridge\bin\ExbRectBridge.crx")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\Bridge\bin\ExbRectBridge.crx"))
            };
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        private static string FindDraftRoot()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "CAXA", "CAXA 3D");
            if (!Directory.Exists(root)) return null;
            var vers = Directory.GetDirectories(root);
            Array.Sort(vers, (a, b) => string.Compare(Path.GetFileName(b), Path.GetFileName(a), StringComparison.OrdinalIgnoreCase));
            foreach (string v in vers)
            {
                string d = Path.Combine(v, "bin", "CAXADraft");
                if (Directory.Exists(Path.Combine(d, "Modules"))) return d;
            }
            return null;
        }

        private static void RunElevatedCopy(string srcCrx, string dstCrx, string infoXml, string dstInfo)
        {
            string tmpInfo = Path.Combine(Path.GetTempPath(), "MdlExbRectBridge.info");
            File.WriteAllText(tmpInfo, infoXml);
            string args = string.Format(
                "-NoProfile -Command \"Copy-Item -Force '{0}' '{1}'; Copy-Item -Force '{2}' '{3}'\"",
                srcCrx, dstCrx, tmpInfo, dstInfo);
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", args)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            var p = System.Diagnostics.Process.Start(psi);
            p.WaitForExit(30000);
            if (!File.Exists(dstCrx) || !File.Exists(dstInfo))
                throw new InvalidOperationException("提权拷贝未完成（用户取消或失败）。请以管理员身份运行本程序后重试。");
        }

        // ---- 通用 ----

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
            Diagnostics.Log("status: " + text);
        }

        private void RunGuarded(Action action)
        {
            _btnRefresh.Enabled = _btnRect.Enabled = _btnInstall.Enabled = false;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ERROR: " + ex.Message);
                SetStatus(ex.Message, Color.Firebrick);
            }
            finally
            {
                _btnRefresh.Enabled = _btnRect.Enabled = _btnInstall.Enabled = true;
            }
        }
    }
}
