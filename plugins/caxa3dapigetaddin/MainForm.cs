using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dapigetaddin
{
    /// <summary>
    /// 调试日志（原独立 EXE 版 Program.Log 的进程内版本）。
    /// 用于 COM 调用追踪、事件挂接诊断。
    /// </summary>
    internal static class Diagnostics
    {
        public static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dapigetaddin_trace.log");

        static Diagnostics()
        {
            // Add-in 每次加载时清空旧日志，便于定位本轮起始点
            try { if (File.Exists(LogPath)) File.Delete(LogPath); } catch { }
        }

        public static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [tid " +
                Thread.CurrentThread.ManagedThreadId +
                (Thread.CurrentThread.IsBackground ? "/bg" : "/ui") + "] " + msg;
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { }
        }
    }

    /// <summary>
    /// 主界面（与独立 EXE 版一致，去掉「刷新连接」按钮，连接状态永久为已连接）。
    /// 连接建立由 Addin.InitSelf 负责（进程内自动），本类只负责事件订阅 + UI 显示。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private Button _btnTopMost;
        private Button _btnClear;
        private Button _btnSave;
        private Button _btnCrx;
        private Button _btnCrxRemove;
        private CheckBox _chkPause;
        private Label _lblTitle;
        private Label _lblDocName;
        private Label _lblAttach;
        private ListView _lvLog;
        private Label _lblSummary;
        private System.Windows.Forms.Timer _timerPing;

        private readonly IZBaseApp _app;
        private readonly string _pluginDirectory;
        private EventMonitor _monitor;
        private bool _paused;
        private int _totalEvents;
        private const int MaxRows = 3000;

        public MainForm(IZBaseApp app, string pluginDirectory)
        {
            _app = app;
            _pluginDirectory = pluginDirectory;
            BuildUi();
            _timerPing = new System.Windows.Forms.Timer { Interval = 2000 };
            _timerPing.Tick += TimerPing_Tick;

            // 集合体铁律：子插件窗口默认前置
            TopMost = true;
            ApplyTopMostVisual();

            // 窗口由主工具箱打开（初始隐藏），但抓取必须随 Add-in 加载立即开始。
            // 强制创建句柄，保证隐藏状态下事件回调/重挂/轮询也能工作。
            if (!IsHandleCreated) CreateHandle();
            StartCapture();
            _timerPing.Start();
        }

        /// <summary>挂接全部事件源。Add-in 加载（窗口隐藏）时即执行。</summary>
        private void StartCapture()
        {
            Diagnostics.Log("MainForm.StartCapture —— 初始化事件订阅");
            try
            {
                _monitor = new EventMonitor(_app);
                _monitor.ApiEvent += OnApiEvent;
                _monitor.DocReattachRequested += OnDocReattachRequested;
                _monitor.Start();

                _lblTitle.Text = "连接状态：已连接（进程内）";
                _lblTitle.ForeColor = Color.FromArgb(0, 120, 40);
                AppendRaw(DateTime.Now, "系统", "抓取已启动 —— 正在捕获 CAXA 3D 自动化事件", "");
                UpdateStatusLabels();
            }
            catch (Exception ex)
            {
                _lblTitle.Text = "连接状态：事件订阅初始化失败";
                _lblTitle.ForeColor = Color.FromArgb(170, 30, 30);
                AppendRaw(DateTime.Now, "系统", "事件订阅失败：" + ex.Message, "");
                Diagnostics.Log("EventMonitor init failed: " + ex);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateStatusLabels();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timerPing.Stop();
            if (_monitor != null) { try { _monitor.Dispose(); } catch { } _monitor = null; }
            base.OnFormClosing(e);
        }

        // ================================================================ 界面

        private void BuildUi()
        {
            Text = "CAXA 3D 操作 API 抓取器";
            ClientSize = new Size(1000, 660);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            MinimumSize = new Size(760, 480);

            // ---- 顶部工具条（无刷新连接按钮）----
            var top = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(10, 8, 10, 8) };

            // 窗口前置开关：点一次置顶+高亮，再点一次解锁。3D 环境选零部件时本窗口不掉到后面。
            _btnTopMost = new Button { Text = "窗口前置", Width = 96, Height = 30, Location = new Point(10, 10) };
            _btnTopMost.Click += BtnTopMost_Click;

            _btnClear = new Button { Text = "清空日志", Width = 96, Height = 30, Location = new Point(116, 10) };
            _btnClear.Click += (s, e) => { _lvLog.Items.Clear(); _totalEvents = 0; _lblSummary.Text = ""; };

            _btnSave = new Button { Text = "保存日志", Width = 96, Height = 30, Location = new Point(222, 10) };
            _btnSave.Click += BtnSave_Click;

            _chkPause = new CheckBox { Text = "暂停记录", Width = 96, Height = 24, Location = new Point(328, 14) };
            _chkPause.CheckedChanged += (s, e) => { _paused = _chkPause.Checked; };

            // 注册 CRX 桥接模块（拷贝 .crx 到 CAXADraft\Bin + 写 Modules\*.info 登记）
            _btnCrx = new Button { Text = "注册CRX模块", Width = 110, Height = 30, Location = new Point(434, 10) };
            _btnCrx.Click += BtnCrx_Click;

            // 卸载 CRX 桥接模块（从 CxAutorun.cx 移除登记行；CAXA 退出后才能删 .crx 本体）
            _btnCrxRemove = new Button { Text = "卸载CRX模块", Width = 110, Height = 30, Location = new Point(552, 10) };
            _btnCrxRemove.Click += BtnCrxRemove_Click;

            _lblTitle = new Label
            {
                Text = "连接状态：初始化中...",
                Location = new Point(674, 16),
                AutoSize = true,
                ForeColor = Color.FromArgb(160, 110, 0)
            };

            _lblDocName = new Label
            {
                Text = "设计环境文件：（无）",
                Location = new Point(10, 48),
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 70, 140)
            };

            _lblAttach = new Label
            {
                Text = "事件订阅：—",
                Location = new Point(10, 70),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            };

            top.Controls.AddRange(new Control[] { _btnTopMost, _btnClear, _btnSave, _btnCrx, _btnCrxRemove, _chkPause, _lblTitle, _lblDocName, _lblAttach });
            Controls.Add(top);

            // ---- 底部汇总 ----
            _lblSummary = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
                ForeColor = Color.FromArgb(80, 80, 80)
            };
            Controls.Add(_lblSummary);

            // ---- 日志区 ----
            _lvLog = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                Font = new Font("Consolas", 9F),
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _lvLog.Columns.Add("时间", 100);
            _lvLog.Columns.Add("分类", 60);
            _lvLog.Columns.Add("API 事件（用户操作触发的自动化接口）", 420);
            _lvLog.Columns.Add("参数", 380);

            var ctx = new ContextMenuStrip();
            ctx.Items.Add("复制单元格", null, (s, e) => CopyCells(cellOnly: true));
            ctx.Items.Add("复制整行", null, (s, e) => CopyCells(cellOnly: false));
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("复制全部", null, (s, e) => CopyAll());
            _lvLog.ContextMenuStrip = ctx;
            // 记录鼠标点下的单元格，供「复制单元格」使用
            _lvLog.MouseDown += LvLog_MouseDown;
            // Ctrl+C 复制选中行（Tab 分隔）
            _lvLog.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C)
                {
                    CopyRows(_lvLog.SelectedItems);
                    e.Handled = true;
                }
            };

            Controls.Add(_lvLog);
            _lvLog.BringToFront();
        }

        // ================================================================ 复制 & 保存

        private int _ctxRow = -1;
        private int _ctxCol = -1;

        private void LvLog_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var info = _lvLog.HitTest(e.X, e.Y);
            if (info.Item != null)
            {
                _ctxRow = info.Item.Index;
                _ctxCol = info.Item.SubItems.IndexOf(info.SubItem);
            }
            else { _ctxRow = -1; _ctxCol = -1; }
        }

        /// <summary>复制：单元格（右键点到的那个）或整行（所有选中行，Tab 分隔）。</summary>
        private void CopyCells(bool cellOnly)
        {
            if (cellOnly)
            {
                if (_ctxRow < 0 || _ctxCol < 0) return;
                Clipboard.SetText(_lvLog.Items[_ctxRow].SubItems[_ctxCol].Text);
                return;
            }
            CopyRows(_lvLog.SelectedItems);
        }

        private void CopyAll()
        {
            var all = new System.Collections.Generic.List<ListViewItem>();
            foreach (ListViewItem it in _lvLog.Items) all.Add(it);
            CopyRows(all);
        }

        private void CopyRows(System.Collections.IList rows)
        {
            if (rows == null || rows.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            foreach (ListViewItem it in rows)
            {
                var cells = new string[it.SubItems.Count];
                for (int i = 0; i < it.SubItems.Count; i++) cells[i] = it.SubItems[i].Text;
                sb.AppendLine(string.Join("\t", cells));
            }
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }

        /// <summary>「窗口前置」开关：置顶时高亮，再点恢复。</summary>
        private void BtnTopMost_Click(object sender, EventArgs e)
        {
            TopMost = !TopMost;
            ApplyTopMostVisual();
        }

        private void ApplyTopMostVisual()
        {
            if (TopMost)
            {
                _btnTopMost.BackColor = Color.FromArgb(0, 120, 215);
                _btnTopMost.ForeColor = Color.White;
                _btnTopMost.FlatStyle = FlatStyle.Flat;
                _btnTopMost.Text = "窗口前置 ●";
            }
            else
            {
                _btnTopMost.BackColor = SystemColors.Control;
                _btnTopMost.ForeColor = SystemColors.ControlText;
                _btnTopMost.FlatStyle = FlatStyle.Standard;
                _btnTopMost.Text = "窗口前置";
            }
        }

        /// <summary>「保存日志」按钮：把当前列表 + 追踪日志导出到文本文件。</summary>
        private void BtnSave_Click(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "保存 API 抓取日志";
                dlg.Filter = "文本文件 (*.txt)|*.txt|CSV 文件 (*.csv)|*.csv";
                dlg.FileName = "caxa3d_api_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    bool csv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
                    string sep = csv ? "," : "\t";
                    var sb = new System.Text.StringBuilder();
                    // 表头
                    var heads = new string[_lvLog.Columns.Count];
                    for (int i = 0; i < heads.Length; i++) heads[i] = _lvLog.Columns[i].Text;
                    sb.AppendLine(string.Join(sep, heads));
                    // 数据行
                    foreach (ListViewItem it in _lvLog.Items)
                    {
                        var cells = new string[it.SubItems.Count];
                        for (int i = 0; i < cells.Length; i++)
                        {
                            string c = it.SubItems[i].Text ?? "";
                            if (csv && c.Contains(",")) c = "\"" + c.Replace("\"", "\"\"") + "\"";
                            cells[i] = c;
                        }
                        sb.AppendLine(string.Join(sep, cells));
                    }
                    File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
                    _lblSummary.Text = "已保存：" + dlg.FileName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("保存失败：" + ex.Message, "caxa3dapigetaddin",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ================================================================ CRX 桥接模块注册

        private const string CrxFileName = "caxa3dapicapture.crx";
        private const string AutorunFileName = "CxAutorun.cx";

        /// <summary>
        /// 注册 CRX 桥接模块：拷贝 .crx 到 CAXADraft\bin，并把路径写进 Modules\CxAutorun.cx 自动加载清单。
        /// 注意：CAXA 3D 2027 的自动加载清单是 CxAutorun.cx；在 Modules 下新建 Mdl*.info 不会被加载
        /// （内置模块的 .info 是模块环境/工具栏配置，不是加载清单）。
        /// </summary>
        private void BtnCrx_Click(object sender, EventArgs e)
        {
            _btnCrx.Enabled = false;
            try
            {
                string crx = FindCaptureCrx(_pluginDirectory);
                if (crx == null)
                {
                    MessageBox.Show(
                        "未找到 " + CrxFileName + "。\n请先编译本项目下的 caxa3dapicapture-crx（Release|x64）。",
                        "注册 CRX 模块", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string draftRoot = FindDraftRoot();
                if (draftRoot == null)
                {
                    MessageBox.Show("未在 %ProgramFiles%\\CAXA\\CAXA 3D 下找到 bin\\CAXADraft\\Modules 目录。",
                        "注册 CRX 模块", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string binDir = Path.Combine(draftRoot, "Bin");
                if (!Directory.Exists(binDir)) binDir = Path.Combine(draftRoot, "bin");
                string modulesDir = Path.Combine(draftRoot, "Modules");
                string autorun = Path.Combine(modulesDir, AutorunFileName);
                string badInfo = Path.Combine(modulesDir, "MdlCaxa3dApiCapture.info");

                string dstCrx = Path.Combine(binDir, CrxFileName);

                bool elevated = false, locked = false;
                try
                {
                    File.Copy(crx, dstCrx, true);
                    WriteAutorunEntry(autorun, dstCrx);
                    // 清掉早期用 .info 的错误登记方式，避免重复加载
                    if (File.Exists(badInfo)) File.Delete(badInfo);
                }
                catch (UnauthorizedAccessException)
                {
                    Diagnostics.Log("注册 CRX: 需要管理员权限，转用提权脚本");
                    RunElevatedRegister(crx, dstCrx, autorun);
                    elevated = true;
                }
                catch (IOException)
                {
                    // CAXA 正在运行并占用 .crx：模块已在内存里，跳过覆盖，只补齐登记项
                    locked = true;
                    WriteAutorunEntry(autorun, dstCrx);
                    Diagnostics.Log("注册 CRX: 目标 .crx 被 CAXA 占用，跳过拷贝，仅更新登记项");
                }

                Diagnostics.Log("注册 CRX: " + dstCrx + " -> " + autorun + (elevated ? " (提权)" : locked ? " (被占用)" : ""));
                _lblSummary.Text = "CRX 模块已注册：" + dstCrx;

                MessageBox.Show(
                    "CRX 模块已注册。\n\n" + dstCrx + "\n自动加载清单：" + autorun + "\n\n" +
                    (locked ? "注意：CAXA 正在运行占用 .crx，本次未覆盖新文件；\n如需更新模块，请关闭 CAXA 后重新注册或运行安装.bat。\n\n" : "") +
                    "模块在 CAXA 3D 启动时加载，请完全退出 CAXA 3D 后重新启动；\n" +
                    "启动后打开图纸文档，事件会写入：\n%TEMP%\\caxa3d_crx_events.log",
                    "注册 CRX 模块", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("注册 CRX 失败: " + ex);
                MessageBox.Show("注册失败：" + ex.Message, "注册 CRX 模块",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _btnCrx.Enabled = true; }
        }

        /// <summary>
        /// 卸载 CRX 桥接模块：从 CxAutorun.cx 移除登记行；CAXA 正在运行时 .crx 本体被占用，
        /// 只能移除登记项，.crx 文件留待 CAXA 退出后再删（或交给 restore.ps1 一起清理）。
        /// </summary>
        private void BtnCrxRemove_Click(object sender, EventArgs e)
        {
            _btnCrxRemove.Enabled = false;
            try
            {
                var confirm = MessageBox.Show(
                    "确定要卸载 CRX 桥接模块吗？\n\n" +
                    "会从 CxAutorun.cx 移除自动加载登记行；\n" +
                    ".crx 本体在 CAXA 运行时被占用，会在 CAXA 完全退出后由卸载脚本清理。\n\n" +
                    "卸载后需要重启 CAXA 3D 才会停止加载该模块。",
                    "卸载 CRX 模块", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;

                string draftRoot = FindDraftRoot();
                if (draftRoot == null)
                {
                    MessageBox.Show("未在 %ProgramFiles%\\CAXA\\CAXA 3D 下找到 bin\\CAXADraft\\Modules 目录。",
                        "卸载 CRX 模块", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string binDir = Path.Combine(draftRoot, "Bin");
                if (!Directory.Exists(binDir)) binDir = Path.Combine(draftRoot, "bin");
                string modulesDir = Path.Combine(draftRoot, "Modules");
                string autorun = Path.Combine(modulesDir, AutorunFileName);
                string dstCrx = Path.Combine(binDir, CrxFileName);
                // CAXA bin 下还有一份（deploy.ps1 同步复制过去的）
                string caxaBinCrx = Path.Combine(Directory.GetParent(draftRoot).FullName, CrxFileName);

                bool removedEntry = false, deletedCrx = false, elevated = false;
                try
                {
                    removedEntry = RemoveAutorunEntry(autorun, dstCrx);
                    if (File.Exists(dstCrx)) { File.Delete(dstCrx); deletedCrx = true; }
                    if (File.Exists(caxaBinCrx)) { try { File.Delete(caxaBinCrx); } catch (IOException) { } }
                }
                catch (UnauthorizedAccessException)
                {
                    Diagnostics.Log("卸载 CRX: 需要管理员权限，转用提权脚本");
                    RunElevatedUnregister(dstCrx, caxaBinCrx, autorun);
                    removedEntry = true;
                    elevated = true;
                }
                catch (IOException)
                {
                    // .crx 被 CAXA 占用：先保证登记项被移除，文件留给 restore.ps1 在 CAXA 退出后删
                    removedEntry = RemoveAutorunEntry(autorun, dstCrx);
                    Diagnostics.Log("卸载 CRX: 目标 .crx 被占用，仅移除登记项");
                }

                string msg = "CRX 模块已卸载" + (elevated ? "（提权）" : "") + "。\n\n" +
                             "自动加载清单：" + autorun + "\n" +
                             (removedEntry ? "登记项已移除。\n" : "登记项未变化（可能本就不存在）。\n") +
                             (deletedCrx ? "已删除 " + dstCrx + "\n" : dstCrx + " 仍被 CAXA 占用，\n将在 CAXA 完全退出后由卸载脚本清理。\n") +
                             "\n请完全退出并重启 CAXA 3D，使其不再加载本模块。";
                Diagnostics.Log("卸载 CRX: " + dstCrx + (elevated ? " (提权)" : ""));
                _lblSummary.Text = "CRX 模块已卸载，请重启 CAXA 3D";
                MessageBox.Show(msg, "卸载 CRX 模块", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("卸载 CRX 失败: " + ex);
                MessageBox.Show("卸载失败：" + ex.Message, "卸载 CRX 模块",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _btnCrxRemove.Enabled = true; }
        }

        /// <summary>从 CxAutorun.cx 移除指定 .crx 的登记行（幂等）。返回是否有变化。</summary>
        private static bool RemoveAutorunEntry(string autorunPath, string crxPath)
        {
            if (!File.Exists(autorunPath)) return false;
            string xml = File.ReadAllText(autorunPath);
            string line = "  <File Template=\"ID_DFT_TMPL\">" + crxPath + "</File>";
            if (!xml.Contains(line)) return false;
            // 移除该行（连同后面的换行）
            string newXml = xml.Replace(line + "\r\n", "").Replace(line + "\n", "").Replace(line, "");
            File.WriteAllText(autorunPath, newXml);
            return true;
        }

        /// <summary>Program Files 写不进去时，走 UAC 提权：移除登记行 + 尝试删除 .crx。</summary>
        private static void RunElevatedUnregister(string dstCrx, string caxaBinCrx, string autorunPath)
        {
            string script = Path.Combine(Path.GetTempPath(), "caxa3d_unregister_crx.ps1");
            string ps = string.Join("\r\n", new[]
            {
                "$ErrorActionPreference = 'Continue'",
                "$dst = '" + dstCrx + "'",
                "$caxaBin = '" + caxaBinCrx + "'",
                "$autorun = '" + autorunPath + "'",
                "if (Test-Path -LiteralPath $autorun) {",
                "  $c = Get-Content -LiteralPath $autorun -Raw -Encoding UTF8",
                "  $line = '  <File Template=\"ID_DFT_TMPL\">' + $dst + '</File>'",
                "  $c = $c.Replace($line + [Environment]::NewLine, '').Replace($line, '')",
                "  Set-Content -LiteralPath $autorun -Value $c -Encoding UTF8",
                "}",
                "if (Test-Path -LiteralPath $dst) { try { Remove-Item -Force -LiteralPath $dst } catch { } }",
                "if (Test-Path -LiteralPath $caxaBin) { try { Remove-Item -Force -LiteralPath $caxaBin } catch { } }",
                ""
            });
            File.WriteAllText(script, ps);

            var psi = new System.Diagnostics.ProcessStartInfo(
                "powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            var p = System.Diagnostics.Process.Start(psi);

            // 规则 8.1：STA UI 线程不阻塞等待，消息泵轮询至多 60 秒
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (!p.HasExited && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }

            if (File.Exists(autorunPath))
            {
                string txt = File.ReadAllText(autorunPath);
                if (txt.IndexOf(dstCrx, StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidOperationException("CxAutorun.cx 登记项未移除：" + autorunPath);
            }
        }

        /// <summary>查找可用的 caxa3dapicapture.crx：优先源码输出目录，其次已部署位置。</summary>
        private static string FindCaptureCrx(string pluginDirectory)
        {
            var candidates = new System.Collections.Generic.List<string>();

            // 部署后 .crx 与插件 DLL 同目录（Host 通过 PluginDirectory 传入）
            if (!string.IsNullOrEmpty(pluginDirectory))
            {
                candidates.Add(Path.Combine(pluginDirectory, CrxFileName));
                candidates.Add(Path.Combine(pluginDirectory, "caxa3dapicapture-crx", "bin", CrxFileName));
            }

            candidates.Add(@"C:\Program Files\CAXA\CAXA 3D\2027\bin\CAXADraft\bin\" + CrxFileName);
            candidates.Add(@"C:\Program Files\CAXA\CAXA 3D\2027\bin\CAXADraft\Bin\" + CrxFileName);

            foreach (string c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        /// <summary>定位 CAXADraft 根目录（含 bin 与 Modules）。</summary>
        private static string FindDraftRoot()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "CAXA", "CAXA 3D");
            if (!Directory.Exists(root)) return null;
            var vers = Directory.GetDirectories(root);
            Array.Sort(vers, (a, b) => string.Compare(
                Path.GetFileName(b), Path.GetFileName(a), StringComparison.OrdinalIgnoreCase));
            foreach (string v in vers)
            {
                string d = Path.Combine(v, "bin", "CAXADraft");
                if (Directory.Exists(Path.Combine(d, "Modules"))) return d;
            }
            return null;
        }

        /// <summary>把 .crx 路径合并进 CxAutorun.cx（保留已有条目，重复注册不会重复写）。</summary>
        private static void WriteAutorunEntry(string autorunPath, string crxPath)
        {
            string xml;
            if (File.Exists(autorunPath))
            {
                xml = File.ReadAllText(autorunPath);
                if (xml.IndexOf(crxPath, StringComparison.OrdinalIgnoreCase) >= 0) return;

                int idx = xml.IndexOf("</Modules>", StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    throw new InvalidOperationException("CxAutorun.cx 格式异常（缺少 </Modules>）：" + autorunPath);

                xml = xml.Insert(idx, "  <File Template=\"ID_DFT_TMPL\">" + crxPath + "</File>\r\n");
            }
            else
            {
                xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Modules>\r\n" +
                      "  <File Template=\"ID_DFT_TMPL\">" + crxPath + "</File>\r\n</Modules>\r\n";
            }
            File.WriteAllText(autorunPath, xml);
        }

        /// <summary>Program Files 写不进去时，走 UAC 提权：拷贝 .crx + 维护 CxAutorun.cx。</summary>
        private static void RunElevatedRegister(string srcCrx, string dstCrx, string autorunPath)
        {
            string script = Path.Combine(Path.GetTempPath(), "caxa3d_register_crx.ps1");
            string ps = string.Join("\r\n", new[]
            {
                "$ErrorActionPreference = 'Stop'",
                "$src = '" + srcCrx + "'",
                "$dst = '" + dstCrx + "'",
                "$autorun = '" + autorunPath + "'",
                "Copy-Item -Force -LiteralPath $src -Destination $dst",
                "if (Test-Path -LiteralPath $autorun) {",
                "  $c = Get-Content -LiteralPath $autorun -Raw -Encoding UTF8",
                "  if ($c -notlike ('*' + $dst + '*')) {",
                "    $line = '  <File Template=\"ID_DFT_TMPL\">' + $dst + '</File>'",
                "    $c = $c.Replace('</Modules>', $line + [Environment]::NewLine + '</Modules>')",
                "    Set-Content -LiteralPath $autorun -Value $c -Encoding UTF8",
                "  }",
                "} else {",
                "  $xml = @('<?xml version=\"1.0\" encoding=\"utf-8\"?>', '<Modules>', " +
                    "('  <File Template=\"ID_DFT_TMPL\">' + $dst + '</File>'), '</Modules>', '') " +
                    "-join [Environment]::NewLine",
                "  Set-Content -LiteralPath $autorun -Value $xml -Encoding UTF8",
                "}",
                "$bad = Join-Path (Split-Path $autorun) 'MdlCaxa3dApiCapture.info'",
                "if (Test-Path -LiteralPath $bad) { Remove-Item -Force -LiteralPath $bad }",
                ""
            });
            File.WriteAllText(script, ps);

            var psi = new System.Diagnostics.ProcessStartInfo(
                "powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            var p = System.Diagnostics.Process.Start(psi);

            // 规则 8.1：STA UI 线程不阻塞等待，消息泵轮询至多 60 秒
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (!p.HasExited && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }

            if (!File.Exists(dstCrx))
                throw new InvalidOperationException("提权注册未完成（用户取消或失败）。请以管理员身份运行 CAXA 3D 后重试。");
            string txt = File.ReadAllText(autorunPath);
            if (txt.IndexOf(dstCrx, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("CxAutorun.cx 未写入登记项：" + autorunPath);
        }

        // ================================================================ 轮询 & 重挂

        /// <summary>
        /// 每 2 秒探测：活动文档是否切换（切换时自动重挂文档级事件），并刷新状态 labels。
        /// 进程内连接不会断开，所以不做连通性检测。
        /// </summary>
        private void TimerPing_Tick(object sender, EventArgs e)
        {
            SafeUi(() =>
            {
                if (_monitor == null) return;
                try
                {
                    IZDoc doc = _app.ActiveDoc;
                    string id = null;
                    if (doc != null) id = doc.FileGUID ?? doc.Name;

                    bool changed = id != null
                        && _monitor.DocIdentity != null
                        && id != _monitor.DocIdentity;

                    if (!_monitor.IsDocAttached || changed)
                    {
                        if (changed) Diagnostics.Log("doc events: 活动文档已切换，重挂文档级事件");
                        _monitor.ReattachDocEvents();
                    }
                }
                catch (Exception ex) { Diagnostics.Log("TimerPing EX: " + ex.Message); }
                // 图纸环境轮询：COM 层拿不到图纸实体事件，靠计数器/快照取变化
                try { _monitor.PollDrawingEnv(); }
                catch (Exception ex) { Diagnostics.Log("PollDrawingEnv EX: " + ex.Message); }
                // CRX 桥接日志增量读取：图纸环境操作全走 CRX reactor，COM 层抓不到
                try { _monitor.ReadCrxEventsLog(); }
                catch (Exception ex) { Diagnostics.Log("ReadCrxEventsLog EX: " + ex.Message); }
                UpdateStatusLabels();
            });
        }

        private void UpdateStatusLabels()
        {
            try
            {
                string envName = "（无文档）";
                if (_monitor != null && _monitor.InDrawingEnv)
                    envName = "图纸环境（推断）";
                else
                {
                    IZDoc doc = _app.ActiveDoc;
                    try
                    {
                        if (doc != null)
                        {
                            var t = doc.Type;
                            if (t == eZDocType.Z_DRAWING) envName = "图纸环境";
                            else if (t == eZDocType.Z_SCENE) envName = "设计环境";
                            else envName = t.ToString();
                        }
                        else envName = "（无文档）";
                    }
                    catch { envName = "环境未知"; }
                }

                string name = null;
                try { var d = _app.ActiveDoc; if (d != null) name = d.Name; } catch { }
                _lblDocName.Text = envName + "：" + (string.IsNullOrEmpty(name) ? "（无文件）" : name);
            }
            catch { _lblDocName.Text = "环境：（无法读取）"; }

            // CRX 桥接模块状态：日志文件存在且非空 = 已加载
            string crxState;
            try
            {
                string p = Path.Combine(Path.GetTempPath(), "caxa3d_crx_events.log");
                crxState = File.Exists(p) ? "CRX：已加载" : "CRX：未加载（点「注册CRX模块」后重启 CAXA）";
            }
            catch { crxState = "CRX：未知"; }

            _lblAttach.Text = (_monitor == null ? "事件订阅：—" : "事件订阅：" + _monitor.Describe())
                              + "    |    " + crxState;
        }

        // ================================================================ 事件记录

        private void OnDocReattachRequested()
        {
            SafeUi(() =>
            {
                if (_monitor == null) return;
                _monitor.ReattachDocEvents();
                UpdateStatusLabels();
            });
        }

        private void OnApiEvent(string category, string api, string args)
        {
            SafeUi(() =>
            {
                if (_paused) { _totalEvents++; return; }
                AppendRaw(DateTime.Now, category, api, args);
            });
        }

        private void AppendRaw(DateTime time, string category, string api, string args)
        {
            _totalEvents++;
            var item = new ListViewItem(time.ToString("HH:mm:ss.fff"));
            item.SubItems.Add(category);
            item.SubItems.Add(api);
            item.SubItems.Add(args ?? "");
            if (_lvLog.Items.Count >= MaxRows) _lvLog.Items.RemoveAt(0);
            _lvLog.Items.Add(item);
            if (_lvLog.IsHandleCreated && _lvLog.Items.Count > 0)
                _lvLog.Items[_lvLog.Items.Count - 1].EnsureVisible();
            _lblSummary.Text = "已记录 API 事件：" + _totalEvents + " 条"
                + (_paused ? "（已暂停，新事件仅计数不显示）" : "");
        }

        /// <summary>在 UI 线程上安全执行。</summary>
        private void SafeUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (Exception ex) { Diagnostics.Log("SafeUi EX: " + ex.Message); }
        }
    }
}
