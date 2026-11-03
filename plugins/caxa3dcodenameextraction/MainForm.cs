using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 主窗口：带图改名及图号分离。
    /// 流程：选中零部件 → 填新零件名 → 重命名 → 按规则回填属性 → 保存 →
    ///       更新工程图链接（打开同名 exb 代点后保存关闭）→ 焦点回设计环境。
    /// </summary>
    [System.Runtime.InteropServices.ComVisible(false)]
    public class MainForm : Form
    {
        private readonly IZBaseApp _siteApp;
        private CaxaSession _app;
        private CaxaAutomation.SelectedPartInfo _partInfo;
        private string _docName;
        // 进程级守卫：同一 CAXA 进程内只允许一条流程（跨窗口见 FlowGate）。
        // 原来用实例字段，关窗口再开会话后 Busy 归 false，旧流程成僵尸与新流程并发，
        // 抢同一个 CRX 请求文件导致 CLOSEDOC/CLOSE 被覆盖（30s+15s 双超时，见规则坑 77）。
        private string _lastOldName = "";
        private bool _suppressNewNameChanged;
        private bool _topMost;
        private bool _sepEnabled = true;
        private List<Rule> _rules = new List<Rule>();

        private Label _lblConnection;
        private Label _lblOldName;
        private Label _lblOldFlag;
        private TextBox _txtOldName;
        private Label _lblNewName;
        private Label _lblNewFlag;
        private TextBox _txtNewName;
        private ComboBox _cboRule;
        private Button _btnSep;
        private Button _btnManualSep;
        private Button _btnDiff;
        private Button _btnRename;
        private Button _btnReplaceProbe;
        private Button _btnChangeLinks;
        private Button _btnSheetMetal;
        private Button _btnTitleBlock;
        private Button _btnSheetSize;
        private Button _btnWarmUp;
        private bool _warmCarrierOpen;       // 预热图纸还开着 = 热状态载体（图纸环境跟着它活，开完目标 exb 再关）
        private string _warmChannel = "";    // 载体的来历（只进日志）
        /// <summary>本会话成功预热过（图纸随后已按用户要求立即关掉）。
        /// 用途只有界面状态灯：探针 marker 在 CRX 卸载时被删，所以"图纸关了"和"没预热过"
        /// 在 IsCrxLoaded 上分不出来，靠这个标记把两者区分开，免得按钮一闪变红像失败。</summary>
        private bool _warmedOnce;
        private Button _btnRegen;
        private Button _btnStop;
        private readonly SheetSizeRule _sheetSizeRule = SheetSizeRule.Load();
        private Button _btnTopMost;
        private Button _btnRules;
        private Button _btnHelp;
        private Button _btnRefresh;
        private Button _btnRegCrx;
        private Button _btnUnregCrx;
        private Button _btnSaveLog;
        private Button _btnClearLog;
        private TextBox _txtLog;
        private Label _lblStatus;
        private System.Windows.Forms.Timer _timer;

        internal static readonly string AppTitleBase = "带图改名及图号分离";

        public MainForm(IZBaseApp siteApp)
        {
            _siteApp = siteApp;
            // 关窗口必须打停后台流程：否则流程成僵尸，与新会话并发抢 CRX 请求文件（规则坑 77）。
            // 不阻塞关闭，只置停止标志，由流程在下一个断点自行退出；
            // 归属判定用 FlowGate.IsBusyBy，别关掉另一个窗口（带图独立）正在跑的流程。
            FormClosing += (sender, e) =>
            {
                Diagnostics.UiLogSink -= AddLog;
                if (FlowGate.IsBusyBy(AppTitleBase))
                {
                    FlowGate.StopRequested = true;
                    Diagnostics.Log("MainForm.FormClosing: 已请求停止后台流程");
                }
            };
            BuildUi();
            Diagnostics.UiLogSink += AddLog;
            if (!IsHandleCreated) CreateHandle();
            LoadRules();
            AddLog("程序启动成功");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryConnect();
        }

        // ================================================================ 界面

        private void BuildUi()
        {
            Text = AppTitleBase + "-" + BuildVersion();
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 620);
            // 「就绪」那行改成不折行的整条显示（AutoSize），宽度按最长一句状态字留够：
            // 重命名按钮右边界 258 + 一句带图号的状态字（实机最长约 690 px）⇒ 客户区 1000 起。
            MinimumSize = new Size(1016, 580);
            MaximizeBox = true;

            var panelTop = new Panel { Dock = DockStyle.Top, Height = 44 };
            _btnTopMost = MakeButton("窗口前置", 10, 8, 80);
            // 多插件集合体铁律 2：工具窗口默认置顶，用户可用「窗口前置」按钮解除
            _topMost = true;
            TopMost = true;
            _btnTopMost.BackColor = Color.FromArgb(0, 120, 212);
            _btnTopMost.ForeColor = Color.White;
            _btnTopMost.FlatStyle = FlatStyle.Flat;
            _btnTopMost.FlatAppearance.BorderSize = 0;
            _btnRules = MakeButton("图号分离规则", 96, 8, 120);
            _btnHelp = MakeButton("使用说明", 222, 8, 100);
            _btnRefresh = MakeButton("刷新连接", 328, 8, 90);
            _btnRegCrx = MakeButton("注册CRX", 424, 8, 90);
            _btnUnregCrx = MakeButton("卸载CRX", 520, 8, 90);
            _btnSaveLog = MakeButton("保存日志", 616, 8, 90);
            _btnClearLog = MakeButton("清理日志", 712, 8, 90);
            panelTop.Controls.AddRange(new Control[] {
                _btnTopMost, _btnRules, _btnHelp, _btnRefresh,
                _btnRegCrx, _btnUnregCrx, _btnSaveLog, _btnClearLog });

            // 高度跟着按钮区收：4 列 2 行最后一行底边 284，留 32 给底部边距；
            // 省下的 32 px 直接还给下面的日志区。
            var panelBody = new Panel { Dock = DockStyle.Top, Height = 316, Padding = new Padding(10) };

            _lblConnection = new Label
            {
                Text = "连接状态: 未连接",
                Location = new Point(10, 10),
                Size = new Size(780, 24),
                ForeColor = Color.FromArgb(209, 52, 56),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            // 多插件集合体铁律 3：不显示连接状态标签（连接状态由 CAXA 自身反映）
            _lblConnection.Visible = false;

            _lblOldName = new Label { Text = "原零件名:", Location = new Point(10, 54), Size = new Size(80, 24) };
            _txtOldName = new TextBox
            {
                Location = new Point(96, 50),
                Size = new Size(560, 26),
                ReadOnly = true,
                BackColor = Color.White
            };
            var menuOld = new ContextMenuStrip();
            menuOld.Items.Add("复制", null, (s, e) => CopyOldName());
            _txtOldName.ContextMenuStrip = menuOld;
            _lblOldFlag = new Label
            {
                Text = "",
                Location = new Point(664, 54),
                Size = new Size(140, 24),
                ForeColor = Color.DimGray
            };

            _lblNewName = new Label { Text = "新零件名:", Location = new Point(10, 96), Size = new Size(80, 24) };
            _txtNewName = new TextBox { Location = new Point(96, 92), Size = new Size(560, 26) };
            var menuNew = new ContextMenuStrip();
            menuNew.Items.Add("复制", null, (s, e) => CopyNewName());
            menuNew.Items.Add("粘贴", null, (s, e) => PasteNewName());
            menuNew.Items.Add("全选", null, (s, e) => _txtNewName.SelectAll());
            _txtNewName.ContextMenuStrip = menuNew;
            _lblNewFlag = new Label
            {
                Text = "",
                Location = new Point(664, 96),
                Size = new Size(140, 24),
                ForeColor = Color.DimGray
            };

            var lblRule = new Label { Text = "分离规则:", Location = new Point(10, 138), Size = new Size(90, 24) };
            _cboRule = new ComboBox
            {
                Location = new Point(110, 134),
                Size = new Size(495, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _btnSep = new Button
            {
                Text = "自动图号分离",
                Location = new Point(613, 133),
                Size = new Size(120, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };

            // 「带图独立」在「重命名」左侧；两个按钮与「自动图号分离」同尺寸（120x28），
            // 整块按钮区从窗口左边界（x=10）起排，和上方「窗口前置」对齐。
            _btnDiff = new Button
            {
                Text = "带图独立",
                Location = new Point(10, 174),
                Size = new Size(120, 28),
                FlatStyle = FlatStyle.Standard
            };
            _btnRename = new Button
            {
                Text = "重命名",
                Location = new Point(138, 174),
                Size = new Size(120, 28),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnRename.FlatAppearance.BorderSize = 0;

            // 按钮区表格化：8 个按钮排成 **4 列 × 2 行**，等宽等距。
            // 原来按文案长短逐个手写 (x,y,w)，改一次文案或换台机器就歪；现在只改列宽/列距两处。
            // 列宽 238 + 列距 8 ⇒ 4*238+3*8 = 976，右边界 986，客户区 1000 之内。
            const int BtnW = 238, GapX = 8, Col0 = 10;
            const int Row1 = 218, Row2 = Row1 + 34;
            _btnManualSep = MakeButton("手动图号分离", Col0, Row1, BtnW);
            _btnSheetMetal = MakeButton("钣金件设计环境设默认", Col0 + (BtnW + GapX), Row1, BtnW);
            _btnTitleBlock = MakeButton("工程图标题栏设默认", Col0 + 2 * (BtnW + GapX), Row1, BtnW);
            _btnStop = MakeButton("停止", Col0 + 3 * (BtnW + GapX), Row1, BtnW);
            // 「替换零部件」：UI 代点驱动原生「替换零件/装配」面板，不改名不保存
            _btnReplaceProbe = MakeButton("替换零部件", Col0, Row2, BtnW);
            // 迁自 caxa3dsheetsizewrite
            _btnSheetSize = MakeButton("钣金件尺寸填写", Col0 + (BtnW + GapX), Row2, BtnW);
            // 「更新工程图链接文件」：进 零件名.exb，把图纸里的外链 ics 改指目标 ics（虚拟件也算）
            _btnChangeLinks = MakeButton("更新工程图链接文件", Col0 + 2 * (BtnW + GapX), Row2, BtnW);
            // 「零部件重新生成」：选中件的 IZPart/IZAssembly::Update(槽 7) 手工出口（改名没提交时用）
            _btnRegen = MakeButton("零部件重新生成", Col0 + 3 * (BtnW + GapX), Row2, BtnW);
            _btnStop.Enabled = false;

            // 「图纸环境预热」：放在「窗口前置」正下方（panelBody 顶行、同一左边界），
            // 底色就是状态灯 —— 绿=本会话已进过图纸环境（CRX 探针在），红=还没进（开图纸有崩的风险）。
            _btnWarmUp = MakeButton("图纸环境预热", 10, 6, 150);

            _lblStatus = new Label
            {
                Text = "就绪",
                AutoSize = true,                     // 单行、不折行，文字多长都整条显示
                Location = new Point(_btnRename.Right + 12, _btnRename.Top + 5),
                ForeColor = Color.DimGray
            };

            panelBody.Controls.AddRange(new Control[] {
                _lblConnection, _lblOldName, _txtOldName, _lblOldFlag,
                _lblNewName, _txtNewName, _lblNewFlag, lblRule, _cboRule,
                _btnSep, _btnDiff, _btnRename, _btnManualSep, _btnReplaceProbe,
                _btnChangeLinks, _btnWarmUp, _btnRegen,
                _btnSheetMetal, _btnTitleBlock, _btnSheetSize,
                _btnStop, _lblStatus });

            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 9F)
            };

            Controls.Add(_txtLog);
            Controls.Add(panelBody);
            Controls.Add(panelTop);

            _btnTopMost.Click += (s, e) => ToggleTopMost();
            _btnSep.Click += (s, e) => ToggleSep();
            ApplySepVisual();
            _btnRules.Click += (s, e) => OpenResource("caxa3dcodenameextraction图号分离规则.txt");
            _btnHelp.Click += (s, e) => OpenResource("caxa3dcodenameextraction使用说明.txt");
            _btnRefresh.Click += (s, e) => { AddLog("正在刷新连接状态..."); TryConnect(); };
            _btnRegCrx.Click += (s, e) => RunCrxScript(true);
            _btnUnregCrx.Click += (s, e) => RunCrxScript(false);
            _btnSaveLog.Click += BtnSaveLog_Click;
            _btnClearLog.Click += (s, e) => { _txtLog.Clear(); };
            _txtNewName.TextChanged += (s, e) =>
            {
                if (_suppressNewNameChanged) return;
                UpdateNewNameFlag();
            };
            _btnRename.Click += BtnRename_Click;
            _btnDiff.Click += BtnDiff_Click;
            _btnReplaceProbe.Click += BtnReplaceProbe_Click;
            _btnChangeLinks.Click += BtnUpdateDrawingLinks_Click;
            _btnWarmUp.Click += BtnWarmUp_Click;
            _btnRegen.Click += BtnRegen_Click;
            _btnManualSep.Click += BtnManualSep_Click;
            _btnSheetMetal.Click += BtnSheetMetal_Click;
            _btnTitleBlock.Click += BtnTitleBlock_Click;
            _btnSheetSize.Click += BtnSheetSize_Click;
            _btnStop.Click += (s, e) => StopFlow();

            _timer = new System.Windows.Forms.Timer { Interval = 800 };
            _timer.Tick += (s, e) =>
            {
                UpdateWarmUpVisual();
                if (!Visible || FlowGate.Busy) return;
                // 800 ms 一次的界面刷新只是为了让「选中件」跟手，不是流程步骤：
                // 不压住细粒度 COM 日志的话，一行流程图要配几百行 hr=0x0
                //（实机 18:51 那轮 3221 行 trace 里 90% 是这个定时器打的）。
                // 压住后仍保留「选中件」变化快照行，状态一变照样有记录。
                Diagnostics.PollQuiet = true;
                try { RefreshInfo(false); }
                finally { Diagnostics.PollQuiet = false; }
            };
            _timer.Start();
            UpdateWarmUpVisual();
        }

        private static Button MakeButton(string text, int x, int y, int w)
        {
            return new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 28) };
        }

        /// <summary>
        /// 「图纸环境预热」按钮当状态灯用，三态：
        ///   绿 = CRX 探针在，开 exb 走热路径；
        ///   黄绿 = 本会话预热过，但预热图纸已按要求立即关掉（marker 随卸载删除，探针不在）；
        ///   红 = 本会话根本没进过图纸环境（冷态直接开图纸有崩 dataexchange.dll 的先例）。
        /// 判据只有探针这一个观测点，不猜 —— 手工开一次图纸后探针照样变绿。
        /// </summary>
        private void UpdateWarmUpVisual()
        {
            bool warm;
            try { warm = CrxBridge.IsCrxLoaded(); }
            catch { warm = false; }
            int state = warm ? 1 : (_warmedOnce ? 2 : 0);
            if (state == _warmShown) return;
            _warmShown = state;
            _btnWarmUp.BackColor = state == 1 ? Color.FromArgb(46, 160, 67)
                                 : state == 2 ? Color.FromArgb(122, 140, 60)
                                              : Color.FromArgb(216, 66, 66);
            _btnWarmUp.ForeColor = Color.White;
            _btnWarmUp.FlatStyle = FlatStyle.Flat;
            _btnWarmUp.FlatAppearance.BorderSize = 0;
            _btnWarmUp.Text = state == 1 ? "图纸环境：已预热"
                            : state == 2 ? "图纸环境：已预热(图纸已关)"
                                         : "图纸环境：未预热";
        }
        private int _warmShown = -1;   // -1 = 还没染过色，第一次刷新必染

        /// <summary>窗口前置开关：点一次锁定在 CAXA 之上（方便边选零部件边看本窗口），再点一次解锁。</summary>
        private void ToggleTopMost()
        {
            _topMost = !_topMost;
            TopMost = _topMost;
            _btnTopMost.BackColor = _topMost ? Color.FromArgb(0, 120, 212) : SystemColors.Control;
            _btnTopMost.ForeColor = _topMost ? Color.White : SystemColors.ControlText;
            _btnTopMost.FlatStyle = FlatStyle.Flat;
            _btnTopMost.FlatAppearance.BorderSize = _topMost ? 0 : 1;
            AddLog(_topMost ? "窗口已锁定在最上层" : "窗口已取消最上层");
        }

        /// <summary>图号分离开关：默认开启；关闭后重命名只做改名/保存/图纸同步，不回填 SC 属性。</summary>
        private void ToggleSep()
        {
            _sepEnabled = !_sepEnabled;
            ApplySepVisual();
            AddLog(_sepEnabled ? "图号分离已开启（将按规则回填 SC 属性）"
                               : "图号分离已关闭（仅重命名，不回填属性）");
        }

        private void ApplySepVisual()
        {
            _btnSep.BackColor = _sepEnabled ? Color.FromArgb(0, 120, 212) : Color.White;
            _btnSep.ForeColor = _sepEnabled ? Color.White : SystemColors.ControlText;
            _btnSep.FlatStyle = FlatStyle.Flat;
            _btnSep.FlatAppearance.BorderSize = _sepEnabled ? 0 : 1;
            _cboRule.Enabled = _sepEnabled;
        }

        /// <summary>标题版本号：取 DLL 写入时间 yyyyMMddHHmm（编译即更新）。</summary>
        private static string BuildVersion()
        {
            try
            {
                return File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location)
                    .ToString("yyyyMMddHHmm");
            }
            catch { return "000000000000"; }
        }

        // ================================================================ 复制/粘贴

        /// <summary>
        /// 只读的原零件名输入框默认吞掉 Ctrl+C，这里显式支持快捷键复制；
        /// 新零件名输入框同时支持 Ctrl+C / Ctrl+V / Ctrl+A，避免 CAXA 宿主抢占按键。
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
            {
                Control focused = ActiveControl;
                TextBox tb = focused as TextBox;
                if (tb == null && focused is ContainerControl)
                    tb = ((ContainerControl)focused).ActiveControl as TextBox;
                if (tb == _txtOldName) { CopyOldName(); return true; }
                if (tb == _txtNewName) { CopyNewName(); return true; }
            }
            else if (keyData == (Keys.Control | Keys.V))
            {
                TextBox tb = ActiveControl as TextBox;
                if (tb != null && tb == _txtNewName) { PasteNewName(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CopyOldName()
        {
            try
            {
                if (_txtOldName.TextLength > 0) Clipboard.SetText(_txtOldName.Text);
            }
            catch (Exception ex) { Diagnostics.Trace("CopyOldName: " + ex.Message); }
        }

        private void CopyNewName()
        {
            try
            {
                if (_txtNewName.SelectedText.Length > 0) Clipboard.SetText(_txtNewName.SelectedText);
                else if (_txtNewName.TextLength > 0) Clipboard.SetText(_txtNewName.Text);
            }
            catch (Exception ex) { Diagnostics.Trace("CopyNewName: " + ex.Message); }
        }

        private void PasteNewName()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string text = Clipboard.GetText().Trim();
                if (text.Length == 0) return;
                _txtNewName.SelectedText = text;
                UpdateNewNameFlag();
            }
            catch (Exception ex) { Diagnostics.Trace("PasteNewName: " + ex.Message); }
        }

        // ================================================================ 连接

        private void TryConnect()
        {
            try
            {
                if (_app != null) _app.Dispose();
                _app = CaxaAutomation.ConnectFromSite(_siteApp);
                Diagnostics.Log("ConnectFromSite ok");
                RefreshInfo(false);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ConnectFromSite failed: " + ex);
                _app = null;
                _lblConnection.Text = "连接状态: 连接失败";
                _lblConnection.ForeColor = Color.FromArgb(209, 52, 56);
                AddLog("连接 CAXA 3D 失败: " + ex.Message);
            }
        }

        /// <summary>刷新文档/选中零部件显示。silent=true 时只更新界面，不写日志。</summary>
        private void RefreshInfo(bool silent)
        {
            if (_app == null) return;
            try
            {
                string docName, elemName;
                CaxaAutomation.ProbeSessionInfo(_app, out docName, out elemName);
                _docName = docName;
                _partInfo = CaxaAutomation.GetSelectedPartInfo(_app);

                string doc = string.IsNullOrEmpty(docName) ? "" : Path.GetFileName(docName);
                if (string.IsNullOrEmpty(doc))
                {
                    _lblConnection.Text = "连接状态: 已连接（无活动文档）";
                    _lblConnection.ForeColor = Color.FromArgb(209, 52, 56);
                }
                else if (_partInfo == null || !_partInfo.HasElement)
                {
                    _lblConnection.Text = "连接状态: " + doc + " ----- 未选中零部件";
                    _lblConnection.ForeColor = Color.FromArgb(180, 120, 0);
                }
                else
                {
                    string partText = _partInfo.ExternallyLinked
                        ? Path.GetFileName(_partInfo.LinkedFile)
                        : (_partInfo.ElementName + "(虚拟)");
                    _lblConnection.Text = "连接状态: " + doc + " ----- " + partText;
                    _lblConnection.ForeColor = Color.FromArgb(16, 124, 16);
                }

                bool has = _partInfo != null && _partInfo.HasElement;
                string oldName = has ? _partInfo.ElementName : "";
                if (_txtOldName.Text != oldName) _txtOldName.Text = oldName;

                // 选中零部件发生变化时，新零件名自动填入原零件名（用户再按需修改）。
                // 若外链名与元素名已经不一致（替换/独立做完、只差改名），就填外链名：
                // 填元素名等于填一个文件夹里已存在且不属于本件的名字，按钮立刻被判「重名」禁用。
                if (oldName != _lastOldName)
                {
                    _lastOldName = oldName;
                    string suggest = oldName;
                    if (has && _partInfo.ExternallyLinked)
                    {
                        string linkBase = Path.GetFileNameWithoutExtension(_partInfo.LinkedFile);
                        if (linkBase.Length > 0 &&
                            !string.Equals(linkBase, oldName, StringComparison.OrdinalIgnoreCase))
                            suggest = linkBase;
                    }
                    SetNewNameText(suggest);
                }

                bool hasDrawing = has && HasDrawingFile();
                _lblOldFlag.Text = !has ? "" : (hasDrawing ? "存在工程图" : "不存在工程图");
                _lblOldFlag.ForeColor = hasDrawing ? Color.FromArgb(16, 124, 16) : Color.DimGray;

                UpdateActionState();
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("RefreshInfo: " + ex.Message);
            }
        }

        /// <summary>程序化改写新零件名（不触发“用户已编辑”标记）。</summary>
        private void SetNewNameText(string text)
        {
            _suppressNewNameChanged = true;
            try { _txtNewName.Text = text ?? ""; }
            finally { _suppressNewNameChanged = false; }
            UpdateNewNameFlag();
        }

        /// <summary>
        /// 新零件名是否与文件夹内已有 ics 重名。
        /// 例外：那个 ics 正是当前选中件自己的外链文件时必须放行——「带图独立」和「替换零部件」
        /// 都是先复制/另存出 零件B.ics 再换链接，元素名还停在 零件A，收尾靠的就是把名字改成 零件B；
        /// 按老口径判「重名」会把这一步永远锁死（实机 14:22 替换成功、重命名却点不动，即此）。
        /// </summary>
        private bool NewNameExists()
        {
            string folder = PartFolder();
            string name = _txtNewName.Text.Trim();
            if (folder == null || name.Length == 0) return false;
            if (IsOwnLinkedFile(name)) return false;
            try { return File.Exists(Path.Combine(folder, name + ".ics")); }
            catch { return false; }
        }

        /// <summary>输入的名字是否就是选中件当前外链文件名（不含扩展名）——即「替换已完成、只差改名」的状态。</summary>
        private bool IsOwnLinkedFile(string name)
        {
            if (_partInfo == null || !_partInfo.ExternallyLinked || name.Length == 0) return false;
            try
            {
                return string.Equals(Path.GetFileNameWithoutExtension(_partInfo.LinkedFile), name,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>统一刷新提示文字与“重命名”按钮可用状态。</summary>
        private void UpdateActionState()
        {
            bool has = _partInfo != null && _partInfo.HasElement;
            bool linked = has && _partInfo.ExternallyLinked;
            bool conflict = NewNameExists();
            string name = _txtNewName.Text.Trim();

            if (has && !linked)
            {
                _lblStatus.Text = "虚拟零部件：请先保存为外部文件（另存为 ics）后再重命名";
                _lblStatus.ForeColor = Color.FromArgb(180, 120, 0);
            }
            else if (conflict)
            {
                _lblStatus.Text = "零件名已存在，不允许重命名";
                _lblStatus.ForeColor = Color.FromArgb(209, 52, 56);
            }
            else if (linked && name.Length > 0 && IsOwnLinkedFile(name) &&
                     !string.Equals(name, _partInfo.ElementName, StringComparison.OrdinalIgnoreCase))
            {
                _lblStatus.Text = "外链已是 " + name + ".ics，元素名还是 " + _partInfo.ElementName +
                    "，点「重命名」即可对齐";
                _lblStatus.ForeColor = Color.FromArgb(16, 124, 16);
            }
            else if (linked && name.Length > 0)
            {
                _lblStatus.Text = "就绪";
                _lblStatus.ForeColor = Color.DimGray;
            }
            else
            {
                _lblStatus.Text = has ? "请输入新零件名" : "请在 CAXA 3D 中选中零部件";
                _lblStatus.ForeColor = Color.DimGray;
            }

            _btnRename.Enabled = has && linked && !FlowGate.Busy && name.Length > 0 && !conflict;
            _btnDiff.Enabled = has && linked && !FlowGate.Busy && name.Length > 0 && !conflict;
            _btnReplaceProbe.Enabled = has && linked && !FlowGate.Busy && name.Length > 0 && !conflict;
            _btnManualSep.Enabled = has && !FlowGate.Busy;
            // 「更新工程图链接文件」只要选中了零部件就能跑（虚拟件也算：它改的是图纸里的链接指向）
            _btnChangeLinks.Enabled = has && !FlowGate.Busy;
            _btnSheetMetal.Enabled = !FlowGate.Busy;
            _btnTitleBlock.Enabled = !FlowGate.Busy;
            _btnWarmUp.Enabled = !FlowGate.Busy;
            // 重新生成对虚拟件也有意义（它提交的是文档里的元素），所以不卡 ExternallyLinked
            _btnRegen.Enabled = has && !FlowGate.Busy;
        }

        private string PartFolder()
        {
            if (_partInfo == null || !_partInfo.ExternallyLinked) return null;
            try { return Path.GetDirectoryName(_partInfo.LinkedFile); }
            catch { return null; }
        }

        /// <summary>
        /// 带图独立的落点文件夹：按需求取「设计环境.ics 所在文件夹」，
        /// 活动文档名不带路径或目录不存在时回退外链件所在文件夹（两者通常同一个）。
        /// </summary>
        private string IndependenceFolder()
        {
            try
            {
                if (!string.IsNullOrEmpty(_docName))
                {
                    string dir = Path.GetDirectoryName(_docName);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch (Exception ex) { Diagnostics.Log("IndependenceFolder: 设计环境目录解析失败 " + ex.Message); }
            return PartFolder();
        }

        /// <summary>零部件所在文件夹内是否有同名 exb。</summary>
        private bool HasDrawingFile()
        {
            if (_partInfo == null || !_partInfo.ExternallyLinked) return false;
            try
            {
                string folder = Path.GetDirectoryName(_partInfo.LinkedFile);
                if (string.IsNullOrEmpty(folder)) return false;
                string baseName = Path.GetFileNameWithoutExtension(_partInfo.LinkedFile);
                return File.Exists(Path.Combine(folder, baseName + ".exb"));
            }
            catch { return false; }
        }

        private void UpdateNewNameFlag()
        {
            string folder = PartFolder();
            string name = _txtNewName.Text.Trim();
            if (folder == null)
            {
                _lblNewFlag.Text = _partInfo != null && _partInfo.HasElement ? "虚拟零部件" : "";
                _lblNewFlag.ForeColor = Color.FromArgb(180, 120, 0);
            }
            else if (name.Length == 0)
            {
                _lblNewFlag.Text = "";
            }
            else
            {
                try
                {
                    bool exists = File.Exists(Path.Combine(folder, name + ".ics"));
                    _lblNewFlag.Text = exists ? "零件名已存在" : "零件不存在";
                    _lblNewFlag.ForeColor = exists ? Color.FromArgb(209, 52, 56) : Color.DimGray;
                }
                catch { _lblNewFlag.Text = ""; }
            }
            UpdateActionState();
        }

        // ================================================================ 规则

        /// <summary>图号分离规则：分隔符 + 目标属性顺序。</summary>
        private sealed class Rule
        {
            public string Text;
            public char Separator;
            public string[] Fields;

            public override string ToString() { return Text; }
        }

        private void LoadRules()
        {
            _rules.Clear();
            _cboRule.Items.Clear();
            string path = FindResource("caxa3dcodenameextraction图号分离规则.txt");
            if (path == null)
            {
                AddLog("未找到图号分离规则文件，使用内置规则");
                _rules.AddRange(BuiltinRules());
            }
            else
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    TryParseRule(line);
                if (_rules.Count == 0) _rules.AddRange(BuiltinRules());
            }
            foreach (Rule r in _rules) _cboRule.Items.Add(r);
            if (_cboRule.Items.Count > 0) _cboRule.SelectedIndex = 0;
            AddLog("已加载 " + _cboRule.Items.Count + " 条图号分离规则");
        }

        private static IEnumerable<Rule> BuiltinRules()
        {
            yield return new Rule { Text = "A自制件.间隔符\" \"----\"SC图号 SC名称\"", Separator = ' ', Fields = new[] { "SC图号", "SC名称" } };
            yield return new Rule { Text = "B外购件.间隔符\"_\"----\"SC名称_SC规格_SC材料_SC图号\"", Separator = '_', Fields = new[] { "SC名称", "SC规格", "SC材料", "SC图号" } };
            yield return new Rule { Text = "C外购件.间隔符\"_\"----\"SC名称_SC规格_SC材料\"", Separator = '_', Fields = new[] { "SC名称", "SC规格", "SC材料" } };
            yield return new Rule { Text = "D外购件.间隔符\"_\"----\"SC名称_SC规格_SC图号\"", Separator = '_', Fields = new[] { "SC名称", "SC规格", "SC图号" } };
            yield return new Rule { Text = "E外购件.间隔符\"_\"----\"SC名称_SC规格\"", Separator = '_', Fields = new[] { "SC名称", "SC规格" } };
        }

        /// <summary>解析一行：A自制件.间隔符" "----"SC图号 SC名称"。</summary>
        private void TryParseRule(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            int p = line.IndexOf(".间隔符", StringComparison.Ordinal);
            if (p < 0) return;
            string rest = line.Substring(p + 4).Trim();
            if (rest.Length < 3) return;

            char quote = rest[0];
            if (quote != '"' && quote != '\'') return;
            int endSep = rest.IndexOf(quote, 1);
            if (endSep <= 1) return;
            string sepText = rest.Substring(1, endSep - 1);
            if (sepText.Length != 1) return;
            char sep = sepText[0];

            int dash = rest.IndexOf("----", endSep, StringComparison.Ordinal);
            if (dash < 0) return;
            string fieldPart = rest.Substring(dash + 4).Trim();
            if (fieldPart.Length < 2) return;
            char q2 = fieldPart[0];
            int endFields = fieldPart.LastIndexOf(q2);
            if (endFields <= 0) return;
            string fieldsText = fieldPart.Substring(1, endFields - 1);

            string[] raw = fieldsText.Split(sep);
            var fields = new List<string>();
            foreach (string f in raw)
            {
                string t = f.Trim();
                if (t.Length > 2 && (t.StartsWith("SC", StringComparison.OrdinalIgnoreCase)))
                    t = t.Substring(2);
                if (t.Length > 0) fields.Add("SC" + t);
            }
            if (fields.Count == 0) return;

            _rules.Add(new Rule { Text = line.Trim(), Separator = sep, Fields = fields.ToArray() });
        }

        // ================================================================ 重命名主流程

        private void BtnRename_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            if (_app == null) { MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase); return; }
            if (_partInfo == null || !_partInfo.HasElement)
            {
                MessageBox.Show("请先在 CAXA 3D 中选中要改名的零部件。", AppTitleBase);
                return;
            }
            if (!_partInfo.ExternallyLinked)
            {
                MessageBox.Show(
                    "当前选中的是虚拟零部件（没有外部 .ics 文件）。\n" +
                    "请先在 CAXA 3D 中把该零部件“另存为”外部文件（保存成 ics），\n" +
                    "再选中它点击重命名。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string newName = _txtNewName.Text.Trim();
            if (newName.Length == 0)
            {
                MessageBox.Show("请先填写新零件名。", AppTitleBase);
                return;
            }
            if (NewNameExists())
            {
                MessageBox.Show(
                    "文件夹内已存在同名零件：" + newName + ".ics\n不允许重命名，请换一个零件名。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (newName.IndexOf(c) >= 0)
                {
                    MessageBox.Show("新零件名包含非法字符：" + c, AppTitleBase);
                    return;
                }
            }

            Dictionary<string, string> values = null;
            if (_sepEnabled)
            {
                string sepError;
                if (!TryBuildSepValues(newName, out values, out sepError))
                {
                    MessageBox.Show(sepError, AppTitleBase);
                    return;
                }
            }

            RunLongFlow("重命名及图号分离", delegate { RenameCore(newName, values, null); });
        }

        /// <summary>
        /// 重命名核心（不带前置校验，可直接被「带图独立」复用）：
        ///   1 回填图号分离属性 → 2 回填零件名 → 3 保存并等 ics 落盘 →
        ///   4 有同名 exb 就进图纸环境走一遍「打开—保存—关闭」 → 5 等回到设计环境。
        /// 属性必须在改名前写：改名后 CAXA 可能清空选择（见 1/6 原注释）。
        /// </summary>
        private void RenameCore(string newName, Dictionary<string, string> values, string folderHint)
        {
            string oldBase = null;
            // folderHint：由「带图独立」传入。替换之后 _partInfo 还是替换前的旧快照
            // （旧 ics 在 srcFolder），照它推文件夹会把老图纸当成目标图纸再开一遍。
            string folder = string.IsNullOrEmpty(folderHint) ? PartFolder() : folderHint;
            if (string.IsNullOrEmpty(folderHint) && _partInfo != null && _partInfo.ExternallyLinked)
            {
                oldBase = Path.GetFileNameWithoutExtension(_partInfo.LinkedFile);
                if (string.IsNullOrEmpty(folder))
                    folder = Path.GetDirectoryName(_partInfo.LinkedFile);
            }
            string newIcs = folder != null ? Path.Combine(folder, newName + ".ics") : null;
            string newExb = folder != null ? Path.Combine(folder, newName + ".exb") : null;
            string oldExb = (folder != null && oldBase != null) ? Path.Combine(folder, oldBase + ".exb") : null;

            PopupAutoYes.FileDialogTarget = newIcs;
            Diagnostics.Log("==== 重命名核心开始: " +
                (_partInfo != null ? _partInfo.ElementName : "?") + " -> " + newName);

            // 1) 先回填图号分离属性（此时选中项一定有效；改名后 CAXA 可能清空选择）
            if (_sepEnabled)
            {
                AddLog("1/6 回填图号分离属性");
                CaxaAutomation.WriteFirstElementCustomProps(_app, values);
                foreach (var kv in values) AddLog("    " + kv.Key + " = " + kv.Value);
            }
            else
            {
                AddLog("1/6 图号分离已关闭，跳过属性回填");
            }

            // 2) 回填零件名
            // 「带图独立」进来的时候第一步已经把外链换成 零件B.ics，所以新名文件已经在盘上。
            // 这时 CAXA 还会弹「你是否要修改和保存这个外链文件的文件名?」——那一笔它要去动的是
            // 老文件路径（零件A.ics），实机 11-02 14:50:02 带约束件撞出「保存文档失败/共享冲突」，
            // 整笔改名一起回滚（14:50:20 起的三次「零部件重新生成」里元素名还是旧名）。
            // 所以：新名文件已存在 ⇒ 这张框点「取消」；只有需要 CAXA 自己去改文件名的场合才点「确定」。
            bool linkFileAlreadyNew = newIcs != null && File.Exists(newIcs);
            AddLog("2/6 回填零件名: " + newName +
                   (linkFileAlreadyNew ? "（外链文件已是新名，CAXA 的「改名并保存」这张框点取消）" : ""));

            AddLog("3/6 保存零部件 ics...");
            string failure = ApplyRenameAndSave(newName, linkFileAlreadyNew);

            // 改名到底落没落上，只看两处证据：CAXA 自己有没有弹「保存文档失败」，
            // 以及元素名回读等不等于目标 —— hr=0 两者都不算数（实机 14:50:02.804 SaveActiveDoc hr=0x0
            // 而 14:50:20 的「零部件重新生成」里元素名还是旧的「SC08020102.01-52 板」）。
            string after = ReadSelectedElementName();
            bool lost = failure != null || (after.Length > 0 && after != newName);
            if (lost)
            {
                AddLog("    改名没落上" +
                       (failure != null ? "（CAXA 报过：" + failure + "）" : "") +
                       (after.Length > 0 && after != newName ? "（回读=「" + after + "」）" : "") +
                       "，补一次改名与保存");
                PumpMessages(600, "等上一笔收尾消息跑完再补改名");
                failure = ApplyRenameAndSave(newName, linkFileAlreadyNew);
                after = ReadSelectedElementName();
            }

            if (newIcs != null)
            {
                bool renamed = WaitUntil("等待 ics 改名", 15000, () => File.Exists(newIcs));
                AddLog(renamed ? "    本地 ics 已改名: " + Path.GetFileName(newIcs)
                               : "    警告：未检测到新 ics 文件，请检查 CAXA 2027 选项设置");
            }

            // 4) 更新工程图链接
            string exb = null;
            if (newExb != null && File.Exists(newExb)) exb = newExb;
            else if (oldExb != null && File.Exists(oldExb)) exb = oldExb;

            if (exb == null)
            {
                AddLog("4/6 未找到同名 exb，仅完成重命名和图号分离");
            }
            else
            {
                AddLog("4/6 打开工程图: " + Path.GetFileName(exb));
                OpenAndCloseDrawing(exb);
            }

            // 流程结束：不再执行 CAXA“重新生成”（多零件装配下极慢，用户已要求去掉）。
            WaitUntil("等待回到设计环境", 10000,
                () => CaxaAutomation.IsActiveSceneDocument(_app));

            bool nameStuck = after.Length == 0 ||
                             string.Equals(after, newName, StringComparison.Ordinal);
            _lblStatus.Text = nameStuck ? "完成: " + newName : "改名没生效，元素名仍是「" + after + "」";
            _lblStatus.ForeColor = nameStuck ? Color.DimGray : Color.FromArgb(209, 52, 56);
            AddLog((nameStuck ? "完成: " : "未完成（改名没生效）: ") + newName);
            if (nameStuck)
            {
                // 更新后：记录新名（避免刷新时又把“已存在的新名”填进输入框并报重名）
                _lastOldName = newName;
                SetNewNameText("");
            }
        }

        /// <summary>
        /// 改名+保存一趟。返回 CAXA 自己弹过的「保存文档失败」正文，没弹过返回 null。
        /// declineLinkRename=true 时，「你是否要修改和保存这个外链文件的文件名?」由代点线程点「取消」
        /// （外链文件名已经对上，让 CAXA 再去动老路径必然撞它自己的文件锁）。
        /// </summary>
        private string ApplyRenameAndSave(string newName, bool declineLinkRename)
        {
            PopupAutoYes.DeclineLinkFileRename = declineLinkRename;
            PopupAutoYes.ResetSaveFailure();
            try
            {
                CaxaAutomation.RenameSelectedElement(_app, newName);
                CaxaAutomation.SaveActiveDoc(_app,
                    ms => PumpMessages(ms, "保存吃 E_FAIL，等宿主把替换/重建收尾跑完再重试"));
                return PopupAutoYes.LastSaveFailure;
            }
            finally { PopupAutoYes.DeclineLinkFileRename = false; }
        }

        /// <summary>当前选中元素的名字（读不到返回空串，空串当"没法校验"处理，不当失败）。</summary>
        private string ReadSelectedElementName()
        {
            try
            {
                var info = CaxaAutomation.GetSelectedPartInfo(_app);
                return info != null ? (info.ElementName ?? "") : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 「带图独立」与「替换零部件」共用的前置校验：
        /// 已连接 + 选中零部件 + 必须是外链件 + 外链文件名==零件名 + 新零件名合法 + 目录可取。
        /// 不通过时自己弹提示并返回 false，保证两个入口口径一致。
        /// </summary>
        private bool TryPrepareIndependence(out string oldBase, out string newName,
            out string srcFolder, out string dstFolder, out string newIcs)
        {
            oldBase = null; newName = null; srcFolder = null; dstFolder = null; newIcs = null;
            if (_app == null) { MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase); return false; }
            if (_partInfo == null || !_partInfo.HasElement)
            {
                MessageBox.Show("请先在 CAXA 3D 中选中要独立的零件。", AppTitleBase);
                return false;
            }
            if (!_partInfo.ExternallyLinked)
            {
                MessageBox.Show("本功能只处理带外部链接的零件。\n当前选中的是虚拟零部件，请先“另存为”外部 ics。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            oldBase = Path.GetFileNameWithoutExtension(_partInfo.LinkedFile);
            if (!string.Equals(oldBase, _partInfo.ElementName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("要求外链文件名与零件名一致，当前是 “" + oldBase + ".ics - " +
                    _partInfo.ElementName + "”。\n请先用「重命名」把两者对齐，再做独立。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            newName = _txtNewName.Text.Trim();
            if (newName.Length == 0)
            {
                MessageBox.Show("请填写新零件名（例如 零件B）。\n它将成为新外链文件名 零件B.ics。", AppTitleBase);
                return false;
            }
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (newName.IndexOf(c) >= 0)
                {
                    MessageBox.Show("新零件名包含非法字符：" + c, AppTitleBase);
                    return false;
                }
            }
            if (string.Equals(newName, oldBase, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("新零件名与原零件名相同，无需独立。", AppTitleBase);
                return false;
            }

            srcFolder = PartFolder();          // 零件A.ics / 零件A.exb 真正所在（外链路径）
            dstFolder = IndependenceFolder();  // 需求要求复制到「设计环境.ics 所在文件夹」
            if (srcFolder == null || dstFolder == null)
            {
                MessageBox.Show("取不到零件/设计环境所在文件夹（外链路径异常：" + _partInfo.LinkedFile + "）。", AppTitleBase);
                return false;
            }
            newIcs = Path.Combine(dstFolder, newName + ".ics");
            return true;
        }

        /// <summary>
        /// 带图独立（11-02 用户定稿的一体化流程）：选中零部件 + 填新零件名，一把跑完三步 ——
        ///   第一步「替换零部件」：复制 零件A.ics/.exb → 零件B.ics/.exb，代点原生「替换零件/装配」面板；
        ///   第二步「重命名」：图号分离属性 → 零件名 → 保存 ics；
        ///   第三步「更新工程图链接文件」：进 零件B.exb 派发 CX_DFT_ChangeLinks，把图纸外链指到 零件B.ics。
        /// 三步调的就是那三个按钮各自的实现（ReplacePartCore / RenameCore / UpdateDrawingLinksCore），
        /// 单独点按钮和走这条链口径一致，不再各修一份。
        /// ★ 早先的主路线 IZPart/IZAssembly::SaveAs(回链=true) 已被这条链取代（11-02 用户指定）。
        ///   「钣金件只能走 SaveAs、替换必降级」的说法也已作废：13:35 实机那轮钣金件
        ///   （折弯板 → 折弯板44）走完整条链，替换后设计树仍报 钣金零件(type=20)，
        ///   属性回填、保存、换图纸外链全部成功。前提是那两张业务弹窗真选了条目（见 LinkPickDialog）。
        /// </summary>
        private void BtnDiff_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            string oldBase, newName, srcFolder, dstFolder, newIcs;
            if (!TryPrepareIndependence(out oldBase, out newName, out srcFolder, out dstFolder, out newIcs))
                return;

            Dictionary<string, string> values = null;
            if (_sepEnabled)
            {
                string sepError;
                if (!TryBuildSepValues(newName, out values, out sepError))
                {
                    MessageBox.Show(sepError, AppTitleBase);
                    return;
                }
            }

            RunLongFlow("带图独立（替换零部件 + 重命名 + 更新工程图链接文件）", delegate
            {
                // 11-02 用户口径：带图独立不再自成一条路线，就是一键串起那三个按钮的逻辑，
                // 每一步都用同一份实现（口径不会和单独点按钮时分叉）。
                AddLog("—— 第一步 替换零部件 ——");
                ReplacePartCore(oldBase, newName, srcFolder, dstFolder, newIcs);

                DiffRenameTool.CheckStop();
                AddLog("");
                AddLog("—— 第二步 重命名（图号分离按界面开关）——");
                // 改名按「零件B.ics 已经躺在目标文件夹」这个事实来算路径：
                // 替换后 _partInfo 还是选中之前的旧快照（旧 ics 在 srcFolder），
                // 拿它推 folder 会把老图纸当成目标图纸开一遍。
                RenameCore(newName, values, dstFolder);

                DiffRenameTool.CheckStop();
                AddLog("");
                AddLog("—— 第三步 更新工程图链接文件 ——");
                string exb = Path.Combine(dstFolder, newName + ".exb");
                if (!File.Exists(exb))
                    AddLog("目标文件夹没有 " + newName + ".exb，第三步跳过（只完成替换与改名）");
                else
                    UpdateDrawingLinksCore(newIcs, exb, dstFolder);
            });
        }

        /// <summary>
        /// 替换链路的判据 + 取证：判据不变（＝设计树里出现指向 newIcs 的外链元素），
        /// 只在未命中时每 10 秒把**场景根层**子项（类型 / 名称 / 外链）整段写进日志。
        /// 目的：区分「替换真没生效」与「走查被 4000 节点上限截断造成假阴性」——
        /// 11-03 两次 88 s / 15 s 的 ⑥ 空转就是这两者分不清，只能按停止。
        /// </summary>
        private Func<bool> MakeReplaceJudge(string newIcs, string newName)
        {
            int tick = 0;
            DateTime nextDump = DateTime.Now;
            return delegate
            {
                tick++;
                bool ok = CaxaAutomation.FindByLinkedFile(_app, newIcs) != null;
                if (ok) return true;
                if (DateTime.Now >= nextDump)
                {
                    nextDump = DateTime.Now.AddSeconds(10);
                    CaxaAutomation.DumpTopLevelScene(_app, newName + " 第" + tick + "轮");
                }
                return false;
            };
        }

        /// <summary>
        /// 「替换零部件」核心（不带前置校验，按钮和「带图独立」共用）：
        ///   1/4 复制 零件A.ics + 同名 零件A.exb → 零件B.ics / 零件B.exb（目标已存在即中止，绝不覆盖）；
        ///   2/4 IZSelectionMgr::AddElementToSelection 把选中件按回活选择集，
        ///       WM_COMMAND 53851 叫出原生「替换零件/装配」面板；
        ///   3/4 ReplacePanelDriver 代点：导入零件/装配 → 文件框填路径点打开 → 面板「✓」提交 → 重新附加约束；
        ///   4/4 遍历设计树确认外链真换成了 零件B.ics，并把新元素按回活选择（CAXA 收尾需要这次写选择）。
        /// 权威判据只看设计树，不读活选择：替换期间 CAXA 会清空选择集，换完也不会自动选回（实机 15:53）。
        /// </summary>
        private void ReplacePartCore(string oldBase, string newName, string srcFolder,
            string dstFolder, string newIcs)
        {
            int origType = _partInfo != null ? _partInfo.ElementType : 0;
            string origKind = _partInfo != null ? _partInfo.ElementKind : "?";
            ReplacePanelDriver.Wait = WaitQuietly;
            ReplacePanelDriver.Log = AddLog;

            AddLog("1/4 复制外链模型与同名图纸 " + oldBase + ".ics/.exb -> " + newName + ".ics/.exb");
            bool hasExb = DiffRenameTool.CopyLinkedFiles(srcFolder, dstFolder, oldBase, newName, AddLog);

            AddLog("2/4 把选中件按回 CAXA 活选择集（" + origKind +
                   "），再投递原生「替换零件/装配」");
            string selError;
            if (!CaxaAutomation.ReassertNativeSelection(_app, out selError))
                AddLog("    警告：重断选择集没成功（" + selError + "），照旧投递，可能要手工点一次");
            PopupAutoYes.FileDialogTarget = newIcs;
            string postError;
            if (!DiffRenameTool.PostReplacePartCommand(out postError))
                AddLog("    投递失败：" + postError);

            AddLog("3/4 UI 代点驱动替换面板，目标 " + newName + ".ics");
            ReplacePanelDriver.Run(newIcs, MakeReplaceJudge(newIcs, newName));

            // 只定位，写选择失败也不判整轮失败（见 CaxaAutomation.SelectLinkedElement 的注释）
            CaxaAutomation.LinkedElementHit hit =
                CaxaAutomation.FindByLinkedFile(_app, newIcs);
            if (hit == null)
            {
                AddLog("4/4 设计树里没有指向 " + newName + ".ics 的元素 —— 替换未生效");
                throw new Exception("替换未生效：设计树里找不到外链指向 " + newName + ".ics 的元素。\n" +
                    "复制出的新文件仍在文件夹内，请手工替换后重试，或直接删掉这两个新文件。");
            }
            AddLog("4/4 设计树定位：" + hit.ElementName + " 类型 " + hit.ElementKind +
                   "(type=" + hit.ElementType + ") 链接=" + hit.LinkedFile);

            // 收尾必须替 CAXA 把新元素按回活选择：删掉这一步（17:11/17:23 两轮）流程一结束
            // 主界面连插件窗口一起点不动，而收尾快照里既没有模态框、主框架也没被禁用。
            string selErr;
            bool reselected = CaxaAutomation.SelectLinkedElement(_app, newIcs, out selErr);
            AddLog(reselected ? "    已把 " + newName + " 按回活选择（CAXA 收尾需要这次写选择）"
                              : "    警告：按回活选择没成功（" + selErr + "），若界面点不动，" +
                                "请在设计树里点一下该元素解锁");
            if (hit.ElementType != origType)
                AddLog("    警告：元素类型从 " + origKind + "(type=" + origType + ") 变成 " +
                       hit.ElementKind + "(type=" + hit.ElementType +
                       ") —— 原生替换会换掉元素对象，钣金/共享属性可能一起丢");
            else
                AddLog("    元素类型仍是 " + origKind + "（若成立，说明这条路线不降级）");
            AddLog(hasExb
                ? "    " + newName + ".exb 已一并复制；它内部的 3D 引用仍指向 " + oldBase +
                  ".ics，第三步「更新工程图链接文件」就是去改这一列。"
                : "    源文件夹没有 " + oldBase + ".exb，本次只复制了 ics。");
        }

        /// <summary>
        /// 「替换零部件」按钮：前置校验 + 调用 <see cref="ReplacePartCore"/>。
        /// 单点这个按钮到此为止 —— 不改名、不做图号分离、不保存；
        /// 设计树现在应显示 零件B.ics - 零件A，确认无误后用「重命名」修正，或直接改用「带图独立」一把跑完。
        /// </summary>
        private void BtnReplaceProbe_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            string oldBase, newName, srcFolder, dstFolder, newIcs;
            if (!TryPrepareIndependence(out oldBase, out newName, out srcFolder, out dstFolder, out newIcs))
                return;

            RunLongFlow("替换零部件", delegate
            {
                ReplacePartCore(oldBase, newName, srcFolder, dstFolder, newIcs);
                AddLog("    本按钮到此为止：不改名、不分离、不保存。");
            });
        }

        /// <summary>
        /// 「更新工程图链接文件」前置校验（11-02 用户口径）。虚拟零部件**允许**：
        /// 本功能只改图纸里视图的外链指向，不动 3D 元素对象。两种选法的目标列不同：
        ///   外链件「设计环境A.ics --- 零件名B」→ 在文件夹＝**外链 ics 所在目录**，文件名＝零件名B.ics
        ///   虚拟件「零件名C.ics --- 零件名C」  → 在文件夹＝**设计环境文档所在目录**，文件名＝零件名C.ics
        ///     虚拟件本身没有外链，它的外链就是设计环境文档，所以**必须要求文档名==零件名**，
        ///     否则无从判断图纸该指到哪个 ics（直接拒绝，不猜）。
        /// 还要有同名 exb 才进得去图纸。
        /// </summary>
        private bool TryPrepareChangeLinks(out string icsPath, out string exbPath, out string folder)
        {
            icsPath = null; exbPath = null; folder = null;
            if (_app == null) { MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase); return false; }
            if (_partInfo == null || !_partInfo.HasElement)
            {
                MessageBox.Show("请先在 CAXA 3D 设计环境里选中要处理的零部件。", AppTitleBase);
                return false;
            }
            string name = _partInfo.ElementName;
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("取不到选中零部件的名字。", AppTitleBase);
                return false;
            }

            if (_partInfo.ExternallyLinked)
            {
                try { folder = Path.GetDirectoryName(_partInfo.LinkedFile); }
                catch { folder = null; }
            }
            else
            {
                string docBase = string.IsNullOrEmpty(_docName)
                    ? null : Path.GetFileNameWithoutExtension(_docName);
                if (docBase == null)
                {
                    MessageBox.Show("取不到设计环境文档名，无法为虚拟零部件判定目标 ics。", AppTitleBase);
                    return false;
                }
                if (!string.Equals(docBase, name, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("选中虚拟零部件时要求「设计环境 == 零件名」。\n当前是 " + docBase +
                        ".ics --- " + name + "（虚拟）。\n请先把这个虚拟件另存为同名 " + name +
                        ".ics 的设计环境，或改选一个同名件。", AppTitleBase);
                    return false;
                }
                try { folder = Path.GetDirectoryName(_docName); }
                catch { folder = null; }
                if (string.IsNullOrEmpty(folder)) folder = IndependenceFolder();
            }
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                MessageBox.Show("取不到目标文件夹（外链路径异常：" + _partInfo.LinkedFile + "）。",
                    AppTitleBase);
                return false;
            }

            icsPath = Path.Combine(folder, name + ".ics");
            if (!File.Exists(icsPath))
            {
                MessageBox.Show("目标链接文件不存在：" + icsPath + "\n" +
                    "「改变链接文件」要指的就是它，请先按同名 ics/exb 的口径把它存出来。", AppTitleBase);
                return false;
            }
            exbPath = FindDrawingFile(name, folder);
            if (exbPath == null)
            {
                MessageBox.Show("找不到图纸 " + name + ".exb（找过：" + folder + "、" +
                    IndependenceFolder() + "、" + PartFolder() + "）。", AppTitleBase);
                return false;
            }
            return true;
        }

        /// <summary>同名图纸优先在目标文件夹里找，找不到再退到设计环境/外链件所在文件夹。</summary>
        private string FindDrawingFile(string name, string folder)
        {
            string[] dirs = { folder, IndependenceFolder(), PartFolder() };
            for (int i = 0; i < dirs.Length; i++)
            {
                if (string.IsNullOrEmpty(dirs[i])) continue;
                string cand = Path.Combine(dirs[i], name + ".exb");
                if (File.Exists(cand)) return cand;
            }
            return null;
        }
        /// <summary>
        /// 「更新工程图链接文件」按钮：前置校验 + 调用 <see cref="UpdateDrawingLinksCore"/>。
        ///
        /// ★ 主线程这里**不能**等对话框出现 ★
        ///   对话框是模态的：投完命令后主线程被它的嵌套消息泵接管，任何 WaitUntil 都醒不过来
        ///   （实机 11-02 08:00 那轮就是"卡在对话框界面"的真相）。主线程只等 CRX 的
        ///   commandEnded —— 它恰好是对话框关掉的那一刻，也就是需求 C-3 要的「CRX 确认执行完成」。
        /// </summary>
        private void BtnUpdateDrawingLinks_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            string icsPath, exbPath, folder;
            if (!TryPrepareChangeLinks(out icsPath, out exbPath, out folder)) return;

            RunLongFlow("更新工程图链接文件", delegate { UpdateDrawingLinksCore(icsPath, exbPath, folder); });
        }

        /// <summary>
        /// 更新工程图链接核心（不带前置校验，按钮和「带图独立」共用）：
        /// 1/4 进图纸环境 → 2/4 派发 CX_DFT_ChangeLinks，后台线程逐行改「在文件夹」「文件名」并点「确定」
        /// → 3/4 等 CRX 报命令结束（= 对话框关掉那一刻，权威完成信号）→ 4/4 保存并关闭这张图纸。
        /// 后台线程的逐行反馈实时并进主程序日志（ChangeLinksProbe.Log）。
        /// </summary>
        private void UpdateDrawingLinksCore(string icsPath, string exbPath, string folder)
        {
            string fileName = Path.GetFileName(exbPath);
            ChangeLinksProbe.Log = AddLogLive;

            AddLog("1/4 进图纸环境 " + fileName);
            DateTime openStart = DateTime.Now;
            int pingSeq = CrxBridge.PostCommand("CMD:PING");
            string pingAck = "REQ.ack | seq=" + pingSeq;
            if (!EnsureDrawingOpened(exbPath))
                throw new Exception("图纸没打开，链接不动。冷启动时自动打开会在 dataexchange.dll 崩掉 CAXA，" +
                    "请先在 CAXA 里手工打开 " + fileName + " 再重跑。");
            WaitUntil("等图纸就绪", 10000, delegate
            {
                if (LogCount(pingAck) == 0 && !File.Exists(CrxBridge.RequestPath))
                    pingAck = "REQ.ack | seq=" + CrxBridge.PostCommand("CMD:PING");
                return OpenEndedSince(openStart) && PopupAutoYes.ModalCount == 0;
            });
            WaitUntil("等编辑器稳定", 3000, delegate
            {
                return PopupAutoYes.ModalCount == 0 && CrxBridge.LogQuiet(200);
            });

            AddLog("2/4 派发「改变链接文件」：在文件夹=" + folder + " 文件名=" +
                   Path.GetFileName(icsPath));
            int endedBase = LogCount("commandEnded | CX_DFT_ChangeLinks");
            ChangeLinksProbe.Begin(icsPath, folder);
            CrxBridge.PostCommand("CMD:SENDSTR|CX_DFT_ChangeLinks");

            // 权威完成信号：CRX 报 commandEnded —— 模态对话框关掉它才会结束（实机 07:58:16
            // 用户手工走一遍就是 4 秒）。主线程这段时间停在嵌套消息泵里，醒不过来也不需要醒。
            // 给到 600 秒：一行都没改到时会把对话框原样留着交还用户手工做。
            bool ended = WaitUntil("等 CRX 报「改变链接文件」执行完成", 600000,
                () => LogCount("commandEnded | CX_DFT_ChangeLinks") > endedBase, 300);
            int reported = ChangeLinksProbe.End().Count;
            AddLog(ended
                ? "3/4 CRX 已确认「改变链接文件」执行完成（后台共报 " + reported + " 条）"
                : "3/4 警告：600 秒内 CRX 没报命令结束 —— 对话框还开着，后续保存/关闭跳过，" +
                  "请你手工点完「确定」后再保存关闭这张图纸");

            if (!ended)
            {
                _lblStatus.Text = "「改变链接文件」对话框仍开着，等你手工完成";
                FlashCadWindow();
                return;
            }

            if (PopupAutoYes.ModalCount > 0)
                WaitUntil("等「请更新3D文件位置」等确认框点掉", 10000,
                    () => PopupAutoYes.ModalCount == 0);

            AddLog("4/4 保存并关闭图纸 " + fileName);
            bool closed = CloseDrawingByName(fileName);
            AddLog(closed ? "    工程图已保存并关闭" : "    工程图未确认关闭，请人工检查这张图纸");

            WaitUntil("等回到设计环境", 10000, () => CaxaAutomation.IsActiveSceneDocument(_app));
        }

        /// <summary>按当前分离规则把名称拆段组成 SC 属性值；段数不符或规则未选时给出原因。</summary>
        private bool TryBuildSepValues(string name, out Dictionary<string, string> values, out string error)
        {
            values = null;
            error = null;

            Rule rule = _cboRule.SelectedItem as Rule;
            if (rule == null)
            {
                error = "请选择分离规则。";
                return false;
            }

            string[] tokens = name.Split(rule.Separator);
            if (tokens.Length != rule.Fields.Length)
            {
                error = string.Format(
                    "按规则“{0}”应有 {1} 段（分隔符“{2}”），当前为 {3} 段。\n使用的名称：{4}",
                    rule.Text, rule.Fields.Length, rule.Separator, tokens.Length, name);
                return false;
            }

            values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < tokens.Length; i++)
                values[rule.Fields[i]] = tokens[i].Trim();
            return true;
        }

        /// <summary>
        /// 冷启动预热：代点 CAXA 功能区里的「新图纸」把 Draft 子系统焐起来，
        /// 判据是 CRX 探针自己出现（CRX 随图纸环境加载，它出现才算真进去过）。
        ///
        /// ★ 为什么入口是 UI 代点，不是 ICAPI ★（两条都是实机结论，别再改回去）
        ///   ① 不能走 CRX 派发（v3.15 的 CMD:EXEC|CX_NEW_EB_INT，已推翻）：CRX 是随图纸环境
        ///      加载的模块（marker 写于 On_kInitAppMsg、删于 On_kUnloadAppMsg），而冷启动的判据
        ///      恰恰是"CRX 没加载"—— 没人消费请求文件，只会留一条陈旧请求，等用户手工开图时被
        ///      当新命令补做一次（坑 146、77）。冷启动只允许无副作用的 CMD:PING。
        ///   ② 不能用 OpenFile2（槽 30）开已有 exb 来焐热：实机崩点 dataexchange!
        ///      DllGetActivationFactory+0x87 / c0000005，托管 catch 接不住（坑 148）。
        ///   ③ ICAPI 建图入口**建得出图纸文档，建不出图纸环境**（坑 151，两轮实机 16:30 / 19:02）：
        ///      OpenNewDoc(槽25)/CreateNewDoc(槽66) 非交互每次都抛 E_FAIL 或建出文档后等不到探针，
        ///      OpenNewDrawing(槽8) 两次分别是"返回文档但探针不起"和"hr=0 返回空指针"，
        ///      显式 .icd 模板通道同样不通 ⇒ 这些入口连同桥接代码一起在 v14 删除。
        ///   ④ 19:02:21 自动按下功能区「新的图纸环境」→ 19:02:23 CRX module loaded → 19:02:24
        ///      commandEnded | CX_new_Impl，3 秒起来 —— **该代点入口已于 2026-11-03 按用户要求
        ///      整体退役**（依赖 MSAA 可访问性树 + 界面语言，且 07:05/07:24 两轮载体都接管不到）。
        ///   ⑤ 仍不碰 IZIronCADApp::CreateDrawingFromScene（槽 10，2027 TLB 新增、v26 interop
        ///      MISSING）：造图=建视图，正是 ② 那条 AV 路径，在用户可能有未存盘的会话里自动赌它
        ///      崩了就是丢工作。
        ///   ⑥ 唯一入口（用户 2026-11-03 指定）：直接 OpenFile2 打开项目里的「caxa3d图纸环境预热.exb」
        ///      让 Draft 随这张专用图纸初始化，目标 exb 打开后不保存直接关掉（见 WarmByOpenWarmupExb）。
        ///      冷态 OpenFile2 仍有坑 148 那条 dataexchange AV 的先例（崩的是带 3D 视图链接的业务
        ///      exb，专用预热图纸赌它不踩）；07:24 实机 4 秒焐热、没崩。失败不重试、也不回落 ——
        ///      提示人工开一次图纸焐热。
        ///
        /// ★ 预热图纸的生死按场景分两种（用户 08:09 定：预热后自动关闭，不判闲置）★
        ///   ① 单独点「预热」：焐热成功 → **立即**经 CRX 桥 CLOSERAW 不保存关掉，界面上不留道具窗口。
        ///      代价：最后一张图纸文档一关，CRX 就在 On_kUnloadAppMsg 里删掉 marker ⇒ IsCrxLoaded 回 false，
        ///      这次预热的收益只剩"本进程内 Draft 子系统初始化过一次（DLL 常驻）"。
        ///   ② 流程内预热（EnsureDrawingOpened）：载体必须活到目标 exb 真打开（CloseWarmDoc），
        ///      提前关=把随后的 OpenFile2 踹回冷启动，预热白做。
        ///   GetAllOpenDocs 对图纸文档看不见（07:24 实测差集恒空）⇒ 关闭一律经 CRX 桥按 MDI 子窗口标题匹配
        ///   （判据对齐 caxa3dCXDFTViewUpdate：close-done / CX_DFT_ViewClose / 窗口消失）。
        /// </summary>
        private bool WarmUpDrawingEnv(bool closeRightAway)
        {
            DateTime t0 = DateTime.Now;
            if (_warmCarrierOpen && CrxBridge.IsCrxLoaded())
            {
                Warm("    状态：CRX 探针在、预热那张图纸还开着（走「" + _warmChannel + "」打开的）—— 已是热态，不用重复预热");
                return true;
            }
            if (CrxBridge.IsCrxLoaded())
            {
                Warm("    状态：CRX 探针已在 —— 本会话已经进过图纸环境（热态），不需要再预热");
                return true;
            }
            Warm("    状态：CRX 探针不在 —— 本会话还没进过图纸环境（冷态），开始预热");
            if (_warmCarrierOpen)
                DiscardWarmDoc("重开预热前先收掉上一张载体");   // 探针没了=环境已退出，旧载体留着只会多一个窗口

            // ── 唯一入口（用户 2026-11-03 指定）：直接打开项目预热图纸；代点法已退役 ──
            string warmExb = FindWarmupExb();
            if (warmExb == null)
            {
                Warm("预热失败：没找到「caxa3d图纸环境预热.exb」（找过插件目录并逐级向上、以及开发机项目目录）—— " +
                     "请把该文件放到插件目录，或在 CAXA 里手工开一次图纸焐热环境");
                UpdateWarmUpVisual();
                return false;
            }
            Warm("预热入口：直接打开项目预热图纸「" + Path.GetFileName(warmExb) + "」（" + warmExb + "）");
            bool ok = WarmByOpenWarmupExb(warmExb, closeRightAway);
            UpdateWarmUpVisual();
            return ok;
        }

        /// <summary>
        /// 唯一预热入口（用户 2026-11-03 指定）：OpenFile2 直接打开项目里的「caxa3d图纸环境预热.exb」，
        /// Draft 子系统随这张专用图纸初始化，CRX 探针出现 = 焐热。图纸当热状态载体保持打开，
        /// 目标 exb 打开后由 CloseWarmDoc 经 CRX 桥 CLOSERAW 不保存直接关掉。
        ///
        /// ★ 不再做文档集合快照/接管 ★（用户 2026-11-03 指定"快照体系"整体摘除）
        ///   07:05 / 07:24 / 07:51 三轮实测：GetAllOpenDocs 对图纸文档恒看不见（差集永远为空，
        ///   空图纸和从盘上打开的 exb 都一样）⇒ GrabDrawingCarrier 的接管永远不会成功，
        ///   保留它只是每轮多打几行"差集为空"。载体存在与否改用 _warmCarrierOpen 记，
        ///   关闭一律走 CRX 桥按 MDI 子窗口标题匹配（与 caxa3dCXDFTViewUpdate 同一套判据）。
        ///
        /// ★ 已知风险（坑 148）★ 冷态 OpenFile2 曾在 dataexchange.dll 崩过 CAXA（11-01 19:14，
        /// 崩的是带 3D 视图链接的业务 exb）。用户改用这张专用预热图纸赌它不踩那条路径；
        /// 崩溃是原生 AV 托管接不住 —— 07:24/07:51 实机 3~4 秒焐热、都没崩。
        /// </summary>
        private bool WarmByOpenWarmupExb(string warmExb, bool closeRightAway)
        {
            DateTime t0 = DateTime.Now;
            // 泵 800ms：让主线程把改名/保存的收尾消息跑完再动图纸环境
            PumpMessages(800, "让主线程把改名/保存的收尾消息跑完");
            try
            {
                bool ao;
                _siteApp.OpenFile2(warmExb, false, true, out ao);
            }
            catch (Exception ex)
            {
                // COM 对 exb 的返回不可靠：热路径下正常也抛 "Invalid file format."，文档其实已打开
                Warm("    OpenFile2 返回异常（正常）: " + ex.Message);
            }
            Warm("    已投打开请求，等图纸环境起来（CRX 探针，最多 30 秒）...");
            if (!WaitUntil("等 CRX 探针出现（预热图纸）", 30000, () => CrxBridge.IsCrxLoaded(), 200))
            {
                Warm("    预热失败：预热图纸打开后 30 秒 CRX 探针没出现（不重试）—— " +
                     "请在 CAXA 里手工开一次图纸焐热环境；" +
                     "若界面多出一张「caxa3d图纸环境预热」图纸，请手工关掉");
                return false;
            }
            _warmCarrierOpen = true;
            _warmChannel = "直接打开项目预热图纸";
            _warmedOnce = true;
            bool openEnded = OpenEndedSince(t0);
            Warm("    预热成功：耗时 " + (int)(DateTime.Now - t0).TotalSeconds + " 秒 —— CRX 探针出现" +
                 (openEnded ? "、CX_open_Impl 已结束" : "") + "。预热图纸「" + Path.GetFileName(warmExb) + "」" +
                 (closeRightAway ? "随即不保存关掉" : "保持开着当热状态载体（目标图纸打开后再关）"));
            PopupAutoYes.WatchLatePopups(30000, "图纸环境预热");

            if (closeRightAway)
            {
                // 用户 08:09 定：预热完就关，不留道具窗口、不判闲置。
                // 代价已在上方类注释写明：marker 随卸载删除 ⇒ 这次预热只保证 Draft 初始化过一次。
                AddLog("    预热图纸立即收尾（不保存直接关）—— 界面不留道具窗口");
                CloseWarmDocByCrx(false, 15000);
                Warm("    预热结束：图纸环境未常驻（探针随文档关闭卸掉 marker）；" +
                     "随后跑流程时会按需再预热一次并留载体到底");
            }
            return true;
        }

        /// <summary>
        /// 找预热图纸：先按资源规则（插件目录并逐级向上，deploy.ps1 会把它拷进 CAXA bin），
        /// 找不到再试开发机项目目录（兜底，命中会带着完整路径进日志）。
        /// </summary>
        private static string FindWarmupExb()
        {
            string p = FindResource("caxa3d图纸环境预热.exb");
            if (p != null) return p;
            string dev = @"C:\code\ICAPI\caxa3dmytoolsaddin\plugins\caxa3dcodenameextraction\caxa3d图纸环境预热.exb";
            return File.Exists(dev) ? dev : null;
        }

        /// <summary>预热判据行：必须同时进文件日志和界面 —— 用户回传的是文件日志，
        /// 逐条原因只进界面等于没取证（v12 一轮 9 条入口，文件里只剩 3 行 hr）。</summary>
        private void Warm(string msg)
        {
            Diagnostics.Log(msg);
        }

        /// <summary>
        /// 关掉预热用的那张图纸 —— 只在目标 exb 确实开起来之后动手（载体一关 CRX 可能随之退出，
        /// 提前关等于把随后的 OpenFile2 又踹回冷启动）。
        /// 一律走 CRX 桥 CLOSERAW 按 MDI 子窗口标题匹配、不保存直接关；
        /// 快照/COM 引用路线已按用户要求整体摘除（GetAllOpenDocs 对图纸文档恒看不见）。
        /// </summary>
        private void CloseWarmDoc(string exbFileName, DateTime openSince)
        {
            bool exbUp = WaitUntil("等 " + exbFileName + " 打开完成", 20000,
                                   () => OpenEndedSince(openSince), 200);
            if (_warmCarrierOpen) CloseWarmDocByCrx(exbUp);
        }

        /// <summary>
        /// 插件没拿到预热图纸 COM 引用时的收尾：经 CRX 文件桥投 CMD:CLOSERAW|文件名。
        /// CRX 只对「标题含该文件名、且 class 是图纸视图」的 MDI 子窗口投 WM_CLOSE ——
        /// 与 CLOSEDOC 同一套匹配铁律，但不走 CX_save（预热图纸没动过，无存盘框）。
        /// 判据对齐 caxa3dCXDFTViewUpdate 三路：REQ.close-done / CX_DFT_ViewClose ended /
        /// 窗口消失（MDIClient+EnumChildWindows，EnumWindows 看不到 MDI 子窗口）。
        /// 有意**不做 CMD:CLOSE 兜底**：它关的是活动视图，此刻活动的是目标图纸，会关错。
        /// </summary>
        private void CloseWarmDocByCrx(bool exbUp, int timeoutMs = 30000)
        {
            const string warmName = "caxa3d图纸环境预热.exb";
            if (!CrxBridge.IsCrxLoaded())
            {
                // 桥不在绝不能投请求：没人消费会留陈旧请求，等下次开图时被当新命令补做（坑 146/77）
                Warm("    CRX 桥不在，没法经桥关预热图纸「" + warmName + "」—— 请手工关掉");
                _warmChannel = "";
                _warmCarrierOpen = false;
                return;
            }
            int doneBase = LogCount("close-done");
            int viewBase = LogCount("commandEnded | CX_DFT_ViewClose");
            int blindBase = LogCount("CLOSEDOC.name-mismatch") + LogCount("CLOSEDOC.no-window");
            try
            {
                bool docSeen = DocWindowExists(warmName);
                Warm("    预热图纸没拿到 COM 引用 —— 改经 CRX 桥按窗口标题不保存直接关" +
                     (exbUp ? "（目标图纸已打开）" : "（目标图纸未确认打开，仍按计划关）") +
                     "；窗口探测=" + (docSeen ? "看得见预热图纸窗口" : "看不见（窗口探测失灵或已关）"));
                CrxBridge.PostCommand("CMD:CLOSERAW|" + warmName);

                bool closed = WaitUntil("不保存关闭预热图纸 " + warmName, timeoutMs, () =>
                    LogCount("close-done") > doneBase
                    || LogCount("commandEnded | CX_DFT_ViewClose") > viewBase
                    || (docSeen && !DocWindowExists(warmName)));
                // 等窗口真的销毁（通常 <1s），避免后续步骤在旧文档关闭过程中抢进来（CXDFTViewUpdate 第 6 步同款）
                if (closed && docSeen)
                    WaitUntil("等预热图纸窗口消失", 5000, () => !DocWindowExists(warmName), 50);
                if (closed)
                    Warm("    预热图纸「" + warmName + "」已不保存直接关掉（CRX 确认 / 窗口已消失）");
                else if (LogCount("CLOSEDOC.name-mismatch") + LogCount("CLOSEDOC.no-window") > blindBase)
                    Warm("    预热图纸「" + warmName + "」没匹配到可关窗口（" +
                         (LastLogLine("CLOSEDOC.no-window").Length > 0
                             ? LastLogLine("CLOSEDOC.no-window")
                             : LastLogLine("CLOSEDOC.name-mismatch")) + "）—— 请手工关掉");
                else
                    Warm("    警告：预热图纸关闭未确认（" + (timeoutMs / 1000) + " 秒超时）—— 若还开着请手工关掉");
            }
            catch (Exception ex)
            {
                // 收尾逻辑本身出错绝不能外抛：它常在 RunLongFlow 的 finally 里被调，抛出去会把
                // 流程善后整段打断（08:12 实机就是这样：DocWindowExists 抛 EntryPointNotFound，
                // 后续收尾全没做）。这里只记账，标志照常复位。
                Warm("    警告：关闭预热图纸时出错（" + ex.GetType().Name + ": " + ex.Message + "）—— 若还开着请手工关掉");
                Diagnostics.Log("CloseWarmDocByCrx failed: " + ex);
            }
            finally
            {
                _warmChannel = "";
                _warmCarrierOpen = false;
                UpdateWarmUpVisual();
            }
        }

        /// <summary>图纸文档窗口是否存在。坑：EnumWindows 只枚举【顶层】窗口，而
        /// CAXADraft 的文档窗口 AfxFrameOrView140u 是 MDIClient 的【子窗口】，顶层枚举永远
        /// 看不到（caxa3dCXDFTViewUpdate 19:18 实测）。所以：顶层找一遍（SDI 兜底）→
        /// 找到 MDIClient 后 EnumChildWindows 找子窗口。标题含文件名即算命中。
        ///
        /// 探测是**辅助判据**，绝不能因为它失败就不关图纸：08:12 实机 EntryPointNotFoundException
        /// 就是从这里抛出、把 CLOSERAW 整条链路截断（图纸只能手工关）。这里一律吞掉异常、按"看不见"返回。
        /// </summary>
        private static bool DocWindowExists(string fileName)
        {
            try { return DocWindowExistsCore(fileName); }
            catch (Exception ex)
            {
                Diagnostics.Log("DocWindowExists 探测失败（按看不见处理，不影响关闭）: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        private static bool DocWindowExistsCore(string fileName)
        {
            bool found = false;
            IntPtr mdi = IntPtr.Zero;
            EnumWindowsForDoc((h, l) =>
            {
                if (!IsWindowVisibleForDoc(h)) return true;
                if (IsDocWindowTitle(h, fileName)) { found = true; return false; }
                var cls = new System.Text.StringBuilder(64);
                GetClassNameForDoc(h, cls, cls.Capacity);
                if (cls.ToString().Equals("MDIClient", StringComparison.OrdinalIgnoreCase)) mdi = h;
                return true;
            }, IntPtr.Zero);
            if (found || mdi == IntPtr.Zero) return found;
            EnumChildWindowsForDoc(mdi, (h, l) =>
            {
                if (IsWindowVisibleForDoc(h) && IsDocWindowTitle(h, fileName)) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsDocWindowTitle(IntPtr h, string fileName)
        {
            var txt = new System.Text.StringBuilder(512);
            GetWindowTextForDoc(h, txt, txt.Capacity);
            return txt.ToString().IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private delegate bool EnumDocProc(IntPtr hWnd, IntPtr lParam);
        // ★ 入口点必须显式指定 ★ 方法名为了避开同类冲突加了 ForDoc 后缀，天然对不上导出名，
        //   漏写 EntryPoint 就是运行时 EntryPointNotFoundException（08:12 实机：预热成功后
        //   DocWindowExists 一抛异常，CLOSERAW 根本没投出去，图纸只能手工关）。
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "EnumWindows")]
        private static extern bool EnumWindowsForDoc(EnumDocProc cb, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "EnumChildWindows")]
        private static extern bool EnumChildWindowsForDoc(IntPtr hWndParent, EnumDocProc cb, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        private static extern bool IsWindowVisibleForDoc(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowTextW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowTextForDoc(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetClassNameW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassNameForDoc(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        /// <summary>流程中止/失败收尾：把自留的预热图纸收掉（它不是用户的文档，留着只是干扰窗口）。
        /// 经 CRX 桥 CLOSERAW 关；等窗短些（15 秒），中止路径不拖流程。</summary>
        private void DiscardWarmDoc(string why)
        {
            if (_warmCarrierOpen) CloseWarmDocByCrx(false, 15000);
        }

        /// <summary>
        /// 保证 exb 图纸在图纸环境里打开着。冷启动（本会话没进过 Draft）时先自动预热一次
        /// （WarmUpDrawingEnv：进程内建一张空图纸、让它带着图纸环境活着，把 Draft 初始化掉），
        /// 预热不成才回落到人工开图。空图纸由 CloseWarmDoc 在 exb 打开后收掉。
        ///
        /// ★ 崩点就在下面的 `OpenFile2`（实机 11-01 19:14 / 11-02 12:57 两轮）★
        ///   19:14 那次日志到 `OpenFile2` 就断，crx.log 停在 `CX_open_Impl` 的 commandWillStart；
        ///   WER 应用程序错误 1000：IRONCAD.exe 29.0.0.20507 / 故障模块 dataexchange.dll
        ///   c0000005 / 偏移 0x7997 = `dataexchange!DllGetActivationFactory+0x87`（手工解 dump：
        ///   实栈 dataexchange→combase→rpcrt4→ole32→KERNELBASE→uxtheme→mfc140u→CxFC3510u142→
        ///   3iISNeb→user32/dwmapi→FrmAssembly，没有几何库帧也没有托管帧，托管 catch 接不住）。
        ///
        /// ★ 「热/冷就是分水岭」这句已被 11-02 推翻 ★
        ///   12:57:19 崩的那次图纸环境**是热的**（CRX 12:54:16 loaded、PING seq=8 已 ack、同会话
        ///   此前已开过 3 张图纸），且崩在 `CX_open_Impl` 已 commandEnded（24 实体、4 个
        ///   CRxDbView3D 全建好）之后约 2 秒 —— 不是"建渲染表面时崩"。同一张钣金 exb 在 13:35
        ///   预热过的会话里连开 4 次（13:35:16/20/40/52，同样 24 实体 4 视图）全平安。
        ///   ⇒ 站得住的观测只有"图纸环境先被进去过一次 ↔ 没崩"，机制未证；预热降概率不消除，
        ///   所以最坏情况仍由人工开图兜底（用户点开走 CAXA 自己的打开路径，实测一直不崩）。
        /// </summary>
        private bool EnsureDrawingOpened(string exbPath)
        {
            string fileName = Path.GetFileName(exbPath);

            if (!CrxBridge.IsCrxLoaded())
            {
                AddLog("    本会话还没进过图纸环境（冷启动）：先试一次自动预热 —— " +
                       "打开预热图纸把 Draft 初始化，再走自动打开");
                // 流程内必须留载体：目标 exb 打开后才由 CloseWarmDoc 撤（提前关=把 OpenFile2 踹回冷启动）
                if (!WarmUpDrawingEnv(false))
                {
                    AddLog("    预热没成：自动打开会在 dataexchange.dll 崩掉 CAXA（19:14 实机闪退就是这条），" +
                           "改为由你在 CAXA 里打开 " + fileName + "，插件只负责保存并关闭");
                    _lblStatus.Text = "等你打开图纸: " + fileName;
                    FlashCadWindow();
                    DateTime since = DateTime.Now.AddSeconds(-1);
                    int beat = 0;
                    bool byUser = WaitUntil("等你手工打开 " + fileName, 120000,
                        () =>
                        {
                            if (CrxBridge.IsCrxLoaded() && OpenEndedSince(since)) return true;
                            // 冷启动这一步只能等人：没有心跳就长得像死锁（实机 11-02 08:34 用户等了
                            // 25 秒以为卡住，点了停止）
                            int secs = (int)(DateTime.Now - since).TotalSeconds;
                            if (secs >= beat + 10)
                            {
                                beat = secs;
                                AddLog("    仍在等你在 CAXA 里打开 " + fileName + "（已等 " + secs +
                                       " 秒；打开后自动继续，点「停止」可中止）");
                                FlashCadWindow();
                            }
                            return false;
                        }, 300);
                    AddLog(byUser
                        ? "    已检测到你手工打开图纸，继续保存与关闭"
                        : "    警告：120 秒内没等到图纸打开，本步跳过（ics 已改名，图纸请手工更新源）");
                    return byUser;
                }
            }

            DateTime openSince = DateTime.Now;
            try
            {
                // 先泵一会儿消息：19:14 那次从 SaveActiveDoc 到 OpenFile2 只隔 22 ms，
                // 改名/保存的收尾消息还没处理完就去建图纸渲染表面，等于把主线程逼到最坏时机。
                PumpMessages(1200, "让主线程把改名收尾消息跑完");
                bool ao;
                _siteApp.OpenFile2(exbPath, false, true, out ao);
            }
            catch (Exception ex)
            {
                // COM 对 exb 的返回不可靠：热路径下正常也会抛 "Invalid file format."，文档其实已打开
                Diagnostics.Log("OpenFile2 返回异常（正常）: " + ex.Message);
            }
            // 预热那张空图纸是热状态载体：等 exb 真打开了再撤，提前关会把环境一起带走
            CloseWarmDoc(fileName, openSince);
            return true;
        }

        /// <summary>
        /// 打开 exb（弹窗自动点是/自动指向新 ics）→ 等就绪 → CRX 原生保存并关闭图纸。
        /// </summary>
        private void OpenAndCloseDrawing(string exbPath)
        {
            string fileName = Path.GetFileName(exbPath);
            DateTime openStart = DateTime.Now;
            int clickBase = PopupAutoYes.ClickCount;

            int pingSeq = CrxBridge.PostCommand("CMD:PING");
            string pingAck = "REQ.ack | seq=" + pingSeq;

            if (!EnsureDrawingOpened(exbPath)) return;      // 冷启动没等到用户开图：本步整体跳过
            AddLog("5/6 工程图已打开，处理更新弹窗...");

            // 等 CRX 就绪（冷启动时 init 会删除请求文件，需补投 PING）
            bool readyCrx = WaitUntil("等CRX就绪", 120000, () =>
            {
                if (LogCount(pingAck) > 0) return true;
                if (!File.Exists(CrxBridge.RequestPath))
                    pingAck = "REQ.ack | seq=" + CrxBridge.PostCommand("CMD:PING");
                return false;
            });
            if (!readyCrx) AddLog("    警告：CRX 未就绪（可点“注册CRX”后再试）");

            bool ready = WaitUntil("等图纸就绪", 30000, () =>
                PopupAutoYes.ClickCount > clickBase
                || (OpenEndedSince(openStart) && PopupAutoYes.ModalCount == 0));
            AddLog(ready ? "    图纸就绪" : "    警告：等图纸就绪超时");

            AddLog("6/6 工程图" + (CloseDrawingByName(fileName) ? "已保存并关闭" : "未关闭，请人工检查"));
        }

        /// <summary>
        /// 保存并关闭指定图纸：CRX 只对「标题含该文件名、且 class 是图纸视图」的 MDI 子窗口投 WM_CLOSE。
        ///
        /// ★ 这里不再有 CMD:CLOSE 兜底 ★
        ///   11-02 11:29:17 冷启动那轮：改名没落盘，请求关 …内牙接头6666666666.exb，实际开着的是旧名，
        ///   于是旧版 CRX 的「退化为关闭全部 MDI 子窗口」把设计环境（SC08020102.01 壳体.ics）一起关掉了。
        ///   CMD:CLOSE 走 CX_DFT_ViewClose，关的是「当前活动视图」——改名没生效时活动视图正是设计环境，
        ///   同样是关错东西。所以：没匹配上就什么都不关，把实开的窗口报出来交给人。
        /// </summary>
        private bool CloseDrawingByName(string fileName)
        {
            int doneBase = LogCount("close-done");
            int viewBase = LogCount("commandEnded | CX_DFT_ViewClose");
            int blindBase = LogCount("CLOSEDOC.name-mismatch") + LogCount("CLOSEDOC.no-window");

            // 投命令前的安全闸门：确认这张图纸窗口真的在。
            // 老版 CRX 的 CloseDocWindows 还留着「文件名没匹配上 ⇒ 退化为关全部 MDI 子窗口」的兜底，
            // 一旦目标图纸根本没开（改名没落盘 / 冷启动打开失败），投下去会把 3D 设计环境一起关掉
            //（规则坑 141：11:29:17 实机就是这样整个设计环境被连带关掉）。
            // 新版 CRX 已按名匹配、匹配不上一个都不关；这里再做一层，两版都安全。
            if (!WaitUntil("确认图纸窗口在 " + fileName, 3000, () => DocWindowExists(fileName), 150))
            {
                AddLog("    图纸「" + fileName + "」现在没开着 —— 不投关闭命令（避免误关设计环境），请人工处理这一张");
                return false;
            }

            CrxBridge.PostCommand("CMD:CLOSEDOC|" + fileName);

            bool closed = WaitUntil("保存并关闭图纸 " + fileName, 30000, () =>
                LogCount("close-done") > doneBase
                || LogCount("commandEnded | CX_DFT_ViewClose") > viewBase);

            if (!closed)
            {
                if (LogCount("CLOSEDOC.name-mismatch") + LogCount("CLOSEDOC.no-window") > blindBase)
                {
                    string why = LastLogLine("CLOSEDOC.name-mismatch");
                    if (why.Length == 0) why = LastLogLine("CLOSEDOC.no-window");
                    AddLog("    图纸「" + fileName + "」其实没开着 —— CRX 按铁律一个窗口都没关。实开窗口：" + why);
                    AddLog("    多半是上一步改名/打开没落盘（冷启动需要人工先打开这张图），请人工处理这一张");
                }
                else
                    AddLog("    警告：关闭未确认（30 秒超时），图纸可能仍开着，请人工检查 " + fileName);
            }
            return closed;
        }

        /// <summary>取 CRX 日志里最后一条含 marker 的行（marker 之后的正文），供把诊断回显到界面。</summary>
        private static string LastLogLine(string marker)
        {
            try
            {
                if (!File.Exists(CrxBridge.LogPath)) return "";
                var lines = new List<string>();
                using (var fs = new FileStream(CrxBridge.LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null) lines.Add(line);
                }
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    int p = lines[i].IndexOf(marker);
                    if (p >= 0) return lines[i].Substring(p + marker.Length).TrimStart(' ', '|', ' ');
                }
            }
            catch { }
            return "";
        }

        // ================================================================ 独立功能：钣金件 / 工程图标题栏

        /// <summary>长流程公共外壳：锁定按钮、开启代点、统一收尾、三支提示（已停止/失败/完成）。</summary>
        private void RunLongFlow(string flowName, Action body)
        {
            if (!FlowGate.TryAcquire(AppTitleBase, flowName))
            {
                MessageBox.Show("已有流程在运行（另一个工具窗口正在处理），请等它结束或点「停止」。", AppTitleBase);
                return;
            }
            _btnRename.Enabled = false;
            _btnDiff.Enabled = false;
            _btnReplaceProbe.Enabled = false;
            _btnChangeLinks.Enabled = false;
            _btnManualSep.Enabled = false;
            _btnSheetMetal.Enabled = false;
            _btnTitleBlock.Enabled = false;
            _btnSheetSize.Enabled = false;
            _btnWarmUp.Enabled = false;
            _btnRegen.Enabled = false;
            _btnStop.Enabled = true;
            PopupAutoYes.Enabled = true;
            Cursor = Cursors.WaitCursor;
            AddLog("");
            AddLog("========== " + flowName + " 开始 ==========");

            string error = null;
            bool stopped = false;
            try { body(); }
            catch (OperationStoppedException) { stopped = true; }
            catch (Exception ex)
            {
                Diagnostics.Log(flowName + " failed: " + ex);
                error = ex.Message;
            }
            finally
            {
                PopupAutoYes.Enabled = false;
                PopupAutoYes.FileDialogTarget = null;
                PopupAutoYes.Suspend = false;
                // 点「停止」只中断主线程，探针状态机跑在代点线程上、还在往「打开」里填路径：
                // 实机 09:11 停止后它继续跑到 09:11:50，残留的 FileDialogTarget 把冷启动期
                // 一张无关「打开」框填成了我们的 ics 路径。收尾一律解除武装。
                // End() 会清空缓冲：正常走完主流程已在 3/4 处播过，这里只会捞到"中止前剩下的尾巴"。
                // 逐行反馈现在由 ChangeLinksProbe.Log 实时并进主日志（11-02 用户口径），
                // 这里只负责解除武装、不再回放 —— 回放正是 v6 前"同一段话打两遍"的成因。
                ChangeLinksProbe.End();
                // 代点开关关掉不等于没框要处理：原生替换的插入例程常在我们判出成功、释放独占之后
                // 才弹「选择用于外部链接的零件或装配」「链接的配置」「重新附加约束」，没人代点就一直
                // 模态挂着 —— 实机 17:11:58 那轮整台 CAXA 连本插件窗口一起点不动 2 分 21 秒。
                // 再盯 30 秒，且只认这几张业务框（本插件自己的提示框、用户自己开的框一概不碰）。
                PopupAutoYes.WatchLatePopups(30000, flowName + " 收尾");
                // 中止/失败时把预热那张空图纸收掉：它是本插件自己的道具，留着只会多一个干扰窗口
                //（正常走完由 CloseWarmDoc 在目标 exb 打开后撤，见 EnsureDrawingOpened）。
                if (stopped || error != null) DiscardWarmDoc(flowName);
                _btnStop.Enabled = false;
                _btnSheetSize.Enabled = true;
                FlowGate.Release();
                Cursor = Cursors.Default;
                RefreshInfo(false);
                RestoreDesignFocus();
            }

            // 提示框放在关闭代点之后：否则本进程的 MessageBox 会被 PopupAutoYes 自动点掉
            Thread.Sleep(300);
            // 三支提示一律走 AutoCloseBox（模态但会自动消失），**不再用裸 MessageBox.Show**：
            // 流程跑在 CAXA 主线程上，裸 MessageBox 会把 CAXA 主界面和脚本窗口一起禁用，
            // 用户没注意到那张小框就是「整台点不动」（实机 18:42 那轮的直接原因）。
            if (stopped)
            {
                _lblStatus.Text = "已停止";
                AddLog("已停止");
                AutoCloseBox.Show("已停止。若图纸已经打开且未保存，请到 CAXA 图纸环境手工保存并关闭。",
                    20000);
            }
            else if (error != null)
            {
                _lblStatus.Text = "失败";
                AddLog("失败: " + error);
                AutoCloseBox.Show(flowName + " 失败：\n" + error +
                    "\n\n（此窗口 " + 30 + " 秒后自动关闭，正文已写进日志区）", 30000);
            }
            else
            {
                _lblStatus.Text = "完成";
                AddLog("完成: " + flowName);
                // 完成不弹模态框：成功不需要用户做任何动作，弹框只会禁用主界面等一次点击。
                // 改成状态栏 + 任务栏闪烁 + 日志，界面立刻可继续操作。
                Diagnostics.Trace("流程完成：" + flowName + "（不弹模态框，改状态栏+任务栏闪烁）");
                FlashCadWindow();
            }
        }

        /// <summary>「停止」：只置标志并让 CRX 停派发，不强行保存、不关图。</summary>
        private void StopFlow()
        {
            if (!FlowGate.IsBusyBy(AppTitleBase)) return;
            FlowGate.StopRequested = true;
            AddLog("已请求停止，正在中止当前流程...");
            try { CrxBridge.PostCommand("CMD:TB_ABORT"); }
            catch (Exception ex) { Diagnostics.Trace("StopFlow PostCommand: " + ex.Message); }
        }

        /// <summary>固定时长消息泵（短时刷新 UI，不承担等待职责）。</summary>
        private void PumpMessages(int ms)
        {
            int elapsed = 0;
            while (elapsed < ms)
            {
                Application.DoEvents();
                Thread.Sleep(30);
                elapsed += 30;
                if (FlowGate.StopRequested) return;
            }
        }

        /// <summary>
        /// 带说明的固定时长消息泵：本来就不是在等什么条件，别用 WaitUntil(() =&gt; false)——
        /// 那条路每轮都会落一行「超时 1200ms」，实机 10:36/10:38 各一条，全是假警报。
        /// </summary>
        private void PumpMessages(int ms, string why)
        {
            Diagnostics.Log(why + "：主动让出 " + ms + " ms");
            PumpMessages(ms);
        }

        /// <summary>两个独立功能的公共前置检查：连接、选中零部件、外部 .ics（可选同名工程图）。</summary>
        private bool CheckLinkedSelection(bool needDrawing)
        {
            if (_app == null)
            {
                MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase);
                return false;
            }
            if (_partInfo == null || !_partInfo.HasElement)
            {
                MessageBox.Show("请先在 CAXA 3D 中选中要处理的零部件。", AppTitleBase);
                return false;
            }
            if (!_partInfo.ExternallyLinked)
            {
                MessageBox.Show("当前选中的是虚拟零部件（没有外部 .ics 文件）。\n请先在 CAXA 3D 中把它“另存为”外部文件。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (needDrawing && !HasDrawingFile())
            {
                MessageBox.Show("零件所在文件夹内没有同名工程图（.exb），无法处理标题栏。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        private string DrawingPath()
        {
            string folder = PartFolder();
            if (folder == null || _partInfo == null || string.IsNullOrEmpty(_partInfo.LinkedFile)) return null;
            return Path.Combine(folder, Path.GetFileNameWithoutExtension(_partInfo.LinkedFile) + ".exb");
        }

        /// <summary>手动图号分离：只对选中件按规则回填 SC 属性并保存，不改名。
        /// 任意零部件均可（含虚拟件，无外部 .ics 限制）。
        /// 名称来源：优先用「新零件名」输入框内容，为空则用原零件名。</summary>
        private void BtnManualSep_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            if (_app == null)
            {
                MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase);
                return;
            }
            if (_partInfo == null || !_partInfo.HasElement)
            {
                MessageBox.Show("请先在 CAXA 3D 中选中要做图号分离的零部件。", AppTitleBase);
                return;
            }

            Rule rule = _cboRule.SelectedItem as Rule;
            if (rule == null)
            {
                MessageBox.Show("请选择分离规则。", AppTitleBase);
                return;
            }

            string name = _txtNewName.Text.Trim();
            if (name.Length == 0) name = _partInfo.ElementName;
            string[] tokens = name.Split(rule.Separator);
            if (tokens.Length != rule.Fields.Length)
            {
                MessageBox.Show(string.Format(
                    "按规则“{0}”应有 {1} 段（分隔符“{2}”），当前为 {3} 段。\n用于分离的名称：{4}",
                    rule.Text, rule.Fields.Length, rule.Separator, tokens.Length, name),
                    AppTitleBase);
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < tokens.Length; i++)
                values[rule.Fields[i]] = tokens[i].Trim();

            RunLongFlow("手动图号分离", delegate
            {
                AddLog("回填图号分离属性（来源名称: " + name + "，不改名）");
                CaxaAutomation.WriteFirstElementCustomProps(_app, values);
                foreach (var kv in values) AddLog("    " + kv.Key + " = " + kv.Value);
                AddLog("保存零部件 ics...");
                CaxaAutomation.SaveActiveDoc(_app,
                    ms => PumpMessages(ms, "保存吃 E_FAIL，等宿主收尾后重试"));
            });
        }

        /// <summary>钣金件设计环境设默认：只跑「打开 → 展开 → 改名 → 配置还原默认 → 保存 → 关闭」六步。</summary>
        private void BtnSheetMetal_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            if (!CheckLinkedSelection(false)) return;
            if (_partInfo.ElementType != 20)
            {
                MessageBox.Show("当前选中的不是钣金件（元素类型 " + _partInfo.ElementType + "）。\n该功能只对钣金件生效。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RunLongFlow("钣金件设计环境设默认", delegate
            {
                SheetMetalOps.NameUpdateOptions opt = new SheetMetalOps.NameUpdateOptions();
                opt.OpenDrawingFirst = false;
                opt.SaveIcs = true;
                opt.CloseIcs = true;
                opt.SaveAndCloseDrawing = false;
                opt.UpdateTitleBlock = false;
                opt.WaitCondition = delegate(string what, int timeoutMs, Func<bool> cond)
                {
                    return WaitUntil(what, timeoutMs, cond);
                };

                SheetMetalOps.SheetMetalProbeResult r = SheetMetalOps.RunSheetMetalProbeInLinkedDoc(
                    _app, true, true, AddLog, delegate { return FlowGate.StopRequested; }, PumpMessages, opt);

                AddLog("");
                AddLog("---------- 执行结果 ----------");
                AddLog("链接文档        : " + r.TargetDocName);
                AddLog("链接文档中钣金件: " + (string.IsNullOrEmpty(r.FoundSheetMetalName)
                    ? "（未找到）" : r.FoundSheetMetalName));
                AddLog("展开件名        : " + r.AfterFlatPartName);
                AddLog("改名            : " + (r.NameChanged ? "成功" : "未成功"));
                AddLog(".ics 保存       : " + (r.IcsSaved ? "成功" : "失败/未执行") + " " + r.IcsSaveHr);
                AddLog(".ics 关闭       : " + (r.IcsClosed ? "已关闭" : "未关闭") + " " + r.IcsCloseHr);
                foreach (string note in r.Notes) AddLog("注意: " + note);

                if (FlowGate.StopRequested) throw new OperationStoppedException();
                if (!r.NameChanged)
                    throw new Exception(r.Notes.Count > 0 ? r.Notes[0] : "展开件名未发生改变，详见日志。");
            });
        }

        /// <summary>工程图标题栏设默认：打开同名 exb → 派发 CX_DFT_TITLEBLOCK_3DSET → 保存并关闭。</summary>
        private void BtnTitleBlock_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            if (!CheckLinkedSelection(true)) return;

            string exb = DrawingPath();
            if (exb == null || !File.Exists(exb))
            {
                MessageBox.Show("未找到同名工程图（.exb）。", AppTitleBase,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RunLongFlow("工程图标题栏设默认", delegate { UpdateTitleBlock(exb); });
        }

        /// <summary>
        /// 「图纸环境预热」：单独把"先焐热图纸环境"这一步做成按钮，并监控预热状态。
        ///
        /// 为什么要单独一个按钮（11-02 实机）：崩的那次（12:57:19，dump IRONCAD.exe.15156.dmp）
        /// 故障点是 dataexchange!DllGetActivationFactory+0x87 / c0000005，发生在 CX_open_Impl
        /// 已经 commandEnded（24 实体、4 个 CRxDbView3D 都建好了）之后约 2 秒；而只要图纸环境
        /// 先被进去过一次，同一张钣金 exb 连开 4 次（13:35:16/20/40/52，同样 24 实体 4 视图）
        /// 全部平安。把焐热拆出来手动做，比塞在流程里更容易验证、也更容易复位。
        /// </summary>
        private void BtnWarmUp_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            if (_siteApp == null)
            {
                MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase);
                return;
            }

            RunLongFlow("图纸环境预热", delegate
            {
                _lblStatus.Text = "正在预热图纸环境...";
                // 手动预热：焐热后立刻不保存关掉，界面上不留道具窗口（用户 08:09 指定，不判闲置）
                bool warm = WarmUpDrawingEnv(true);
                // 不弹模态框：按钮本身就是状态灯，弹一次框等于把 CAXA 主界面锁 8 秒
                _lblStatus.Text = warm ? "图纸环境已预热，预热图纸已不保存关掉（跑流程时会按需再预热一次）"
                                       : "预热没成：日志里有失败原因，请手工开一次图纸";
                _lblStatus.ForeColor = warm ? Color.FromArgb(16, 124, 16) : Color.FromArgb(209, 52, 56);
            });
        }

        /// <summary>
        /// 「零部件重新生成」：对选中的零部件调 IZPart/IZAssembly::Update(槽 7) 提交重建。
        /// 这是 CAXA 菜单「重新生成」的手工出口，也是"改名没提交"的补救动作——
        /// 实机 11-02 13:51 焊接底板：set_Name 回 hr=0 但回读仍是旧名，随后 Save 0x80004005。
        /// 钣金件 Update 后会被切到展开配置，这里按坑 111 自动退回折叠态（Refold，退路激活默认配置）。
        /// </summary>
        private void BtnRegen_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            if (_app == null)
            {
                MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase);
                return;
            }
            if (_partInfo == null || !_partInfo.HasElement)
            {
                MessageBox.Show("请先在 CAXA 3D 里选中要重新生成的零件或装配体。", AppTitleBase);
                return;
            }

            RunLongFlow("零部件重新生成", delegate
            {
                AddLog("1/1 对选中的" + _partInfo.ElementKind + "「" + _partInfo.ElementName +
                       "」调 Update 提交重建");
                string report = CaxaAutomation.UpdateSelectedComponent(_app);
                AddLog("    " + report);
                _lblStatus.Text = "已重新生成";
            });
        }

        /// <summary>
        /// 钣金件尺寸填写（迁自 caxa3dsheetsizewrite）：
        /// 虚拟钣金件 → 展开件最小包围盒取长宽 → 组合板厚/材料 → 回填 SC规格/SC材料 → 恢复默认设计环境 → 保存。
        /// </summary>
        private void BtnSheetSize_Click(object sender, EventArgs e)
        {
            if (FlowGate.Busy) return;
            if (_siteApp == null)
            {
                MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase);
                return;
            }

            SheetSizeValidateResult v;
            try
            {
                v = SheetSizeTool.Validate(_siteApp);
            }
            catch (Exception ex)
            {
                AddLog("钣金件尺寸填写校验异常：" + ex.Message);
                Diagnostics.Log("sheet size validate failed: " + ex);
                MessageBox.Show(ex.Message, AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!v.Ok)
            {
                AddLog("钣金件尺寸填写未通过校验：" + v.Message);
                MessageBox.Show(v.Message, AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RunLongFlow("钣金件尺寸填写", delegate
            {
                SheetSizeExecuteResult r = SheetSizeTool.Execute(_siteApp, _sheetSizeRule, AddLog,
                    delegate { return FlowGate.StopRequested; });

                AddLog("");
                AddLog("---------- 执行结果 ----------");
                AddLog("规格            : " + r.Spec);
                AddLog("材料            : " + r.MaterialSpec);
                AddLog("默认配置恢复    : " + (r.ConfigRestored ? "是（" + r.DefaultConfigName + "）" : "否"));
                AddLog("保存            : " + (r.Saved ? "是" : "否"));
                if (r.MaterialFallback) AddLog("注意：未读取到材料字段，SC材料只含板厚。");

                if (FlowGate.StopRequested) throw new OperationStoppedException();
                if (!r.Saved) throw new Exception("文档保存未确认成功，请检查日志。");
            });
        }

        /// <summary>
        /// 标题栏 3D 信息设默认。判据链照抄 caxa3dtitleupdate 已验证实现，
        /// 仅把兜底延迟压到 10s 以内（用户要求）。
        /// </summary>
        private void UpdateTitleBlock(string exbPath)
        {
            string fileName = Path.GetFileName(exbPath);
            DateTime openStart = DateTime.Now;

            // 0) 预投 PING 只作冷启动探针，不阻塞等 ack（权威就绪信号是 CX_open_Impl 结束）
            int pingSeq = CrxBridge.PostCommand("CMD:PING");
            string pingAck = "REQ.ack | seq=" + pingSeq;

            AddLog("1/5 打开工程图: " + fileName);
            // 冷启动保护同 OpenAndCloseDrawing：那条路径实测会 AV 崩在 dataexchange.dll
            if (!EnsureDrawingOpened(exbPath))
            {
                AddLog("1/5 图纸未打开，标题栏 3D 信息本步跳过");
                return;
            }

            bool ready = WaitUntil("等图纸就绪", 10000, () =>
            {
                // 冷启动时 CRX init 会删掉请求文件，补投 PING（只作就绪探针，不参与判定）
                if (CrxBridge.LogCount(pingAck) == 0 && !File.Exists(CrxBridge.RequestPath))
                    pingAck = "REQ.ack | seq=" + CrxBridge.PostCommand("CMD:PING");
                return OpenEndedSince(openStart) && PopupAutoYes.ModalCount == 0;
            });
            if (!ready)
            {
                if (LogCount(pingAck) == 0)
                    AddLog("2/5 警告：CRX 未就绪（可点「注册CRX」后重开 CAXA 再试）");
                else
                    AddLog("2/5 警告：等图纸就绪超时");
            }
            else AddLog("2/5 工程图已打开并就绪");

            // 3) 短稳定等待：避开打开瞬间的事件突发
            bool settled = WaitUntil("等编辑器短暂稳定", 3000,
                () => PopupAutoYes.ModalCount == 0 && CrxBridge.LogQuiet(200));
            AddLog(settled ? "3/5 编辑器已稳定，准备派发标题栏命令"
                           : "3/5 编辑器仍在忙，直接派发并由 CRX 重试");

            // 4) 派发 CX_DFT_TITLEBLOCK_3DSET（「设置3D信息」对话框由 Dlg3DInfo 自动填写）
            int tbResultBase = LogCount("REQ.tb-result");
            int tbGiveupBase = LogCount("REQ.tb-giveup");
            int tbDoneBase = LogCount("REQ.tb-done");
            int appliedBase = Dlg3DInfo.AppliedCount;
            CrxBridge.PostCommand("CMD:TITLEBLOCK_3DSET");
            bool tbReached = WaitUntil("等标题栏 3D 信息写入结果", 10000,
                () => LogCount("REQ.tb-result") > tbResultBase
                   || LogCount("REQ.tb-giveup") > tbGiveupBase
                   || (Dlg3DInfo.AppliedCount > appliedBase && LogCount("REQ.tb-done") > tbDoneBase),
                150);
            bool tbOk = LogCount("REQ.tb-result") > tbResultBase;
            bool dialogApplied = Dlg3DInfo.AppliedCount > appliedBase;

            // 收尾：无论成败都让 CRX 停止通道轮换，避免把命令派发到已关闭的文档上
            try { CrxBridge.PostCommand("CMD:TB_ABORT"); } catch { }

            if (!tbReached)
                throw new Exception("标题栏 3D 信息设置未在 10 秒内得到结果（CRX 侧未确认标题栏信息变化），本次不保存、不关图。");
            if (!tbOk)
            {
                if (dialogApplied)
                    AddLog("4/5 设置3D信息 对话框已按默认配置填写并确认；标题栏信息无变化（原本就是默认配置）");
                else
                    throw new Exception("标题栏 3D 信息没有设置成功：「设置3D信息」对话框的配置/自定义设置未能自动填完，"
                        + "或确认后标题栏信息没有变化。本次不保存、不关图。");
            }
            else AddLog("4/5 标题栏 3D 信息已按默认配置写入（已由标题栏信息差异确认）");

            if (PopupAutoYes.ModalCount > 0)
                WaitUntil("等确认对话框关闭", 3000, () => PopupAutoYes.ModalCount == 0);

            // 5) 保存并关闭工程图
            int doneBase = LogCount("close-done");
            int viewBase = LogCount("commandEnded | CX_DFT_ViewClose");
            CrxBridge.PostCommand("CMD:CLOSEDOC|" + fileName);
            bool closed = WaitUntil("保存并关闭工程图", 10000,
                () => LogCount("close-done") > doneBase
                   || LogCount("commandEnded | CX_DFT_ViewClose") > viewBase);
            if (!closed)
            {
                // 不再补投 CMD:CLOSE（CX_DFT_ViewClose 打在「当前激活视图」上）：
                // 图纸没开着时它作用到 3D 设计环境，11:29 那次就是把设计环境关掉的元凶之一。
                AddLog("    图纸「" + fileName + "」保存/关闭未确认；按铁律不再向当前视图补投关闭命令。");
            }
            if (!closed) throw new Exception("工程图保存/关闭未确认完成，请人工检查该图纸状态。");
            AddLog("5/5 工程图已保存并关闭");

            WaitUntil("等回到设计环境", 10000, () => CaxaAutomation.IsActiveSceneDocument(_app));
        }

        /// <summary>焦点切回 CAXA 3D 设计环境（Afx: 主框架，不是本插件窗体）。</summary>
        private static void RestoreDesignFocus()
        {
            try
            {
                IntPtr h = UiWin.CadFrame();
                if (h == IntPtr.Zero) return;
                // SW_RESTORE 会把最大化窗口强制还原成普通窗口，仅在最小化时才还原
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
            }
            catch (Exception ex) { Diagnostics.Trace("RestoreDesignFocus: " + ex.Message); }
        }

        /// <summary>
        /// 流程完成时让任务栏图标闪一下（代替原来那张「XX 完成。」模态框）。
        /// 用户切在别的程序里也能看到流程结束了，而 CAXA 主界面不会被禁用。
        /// </summary>
        private static void FlashCadWindow()
        {
            try
            {
                IntPtr h = UiWin.CadFrame();
                if (h == IntPtr.Zero) return;
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(FLASHWINFO));
                fi.hwnd = h;
                fi.dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG;
                fi.uCount = 4;
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            catch (Exception ex) { Diagnostics.Trace("FlashCadWindow: " + ex.Message); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
        private const int SW_RESTORE = 9;
        private const uint FLASHW_TRAY = 0x2;
        private const uint FLASHW_TIMERNOFG = 0xC;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public int cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public int dwTimeout;
        }

        // ================================================================ 等待原语

        /// <summary>条件轮询（100ms），期间泵消息，避免阻塞 CAXA 主线程；命中「停止」即抛异常中断整条链。</summary>
        private bool WaitUntil(string what, int timeoutMs, Func<bool> condition, int pollMs = 100)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { Application.DoEvents(); } catch { }
                if (FlowGate.StopRequested) throw new OperationStoppedException();
                try { if (condition()) return true; } catch { }
                Thread.Sleep(pollMs);
            }
            Diagnostics.Log(what + ": 超时 " + timeoutMs + "ms");
            return false;
        }

        /// <summary>
        /// 长轮询专用：等待期间抑制逐次 COM 调用的细粒度诊断（实机 100 ms 轮询 3 分钟能刷出 60 万行，
        /// 关键行被埋），只保留 CaxaAutomation 的「选中件」变化快照行。
        /// </summary>
        private bool WaitQuietly(string what, int timeoutMs, Func<bool> condition, int pollMs)
        {
            Diagnostics.PollQuiet = true;
            try { return WaitUntil(what, timeoutMs, condition, pollMs); }
            finally { Diagnostics.PollQuiet = false; }
        }

        private static int LogCount(string marker)
        {
            try
            {
                if (!File.Exists(CrxBridge.LogPath)) return 0;
                string text;
                using (var fs = new FileStream(CrxBridge.LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();
                int n = 0, i = 0;
                while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
                { n++; i += marker.Length; }
                return n;
            }
            catch { return 0; }
        }

        private static bool OpenEndedSince(DateTime openStart)
        {
            const string marker = "commandEnded | CX_open_Impl";
            try
            {
                if (!File.Exists(CrxBridge.LogPath)) return false;
                string text;
                using (var fs = new FileStream(CrxBridge.LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();

                TimeSpan startT = openStart.TimeOfDay;
                int i = 0;
                while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
                {
                    int lineStart = text.LastIndexOf('\n', i) + 1;
                    if (i - lineStart >= 8 &&
                        TimeSpan.TryParseExact(text.Substring(lineStart, 8), @"hh\:mm\:ss",
                            System.Globalization.CultureInfo.InvariantCulture, out var ts)
                        && ts >= startT - TimeSpan.FromSeconds(1))
                        return true;
                    i += marker.Length;
                }
                return false;
            }
            catch { return false; }
        }

        // ================================================================ 资源/日志/CRX

        /// <summary>从输出目录逐级向上查找项目文件（不写死绝对路径）。</summary>
        private static string FindResource(string fileName)
        {
            var dirs = new List<string>();
            try { dirs.Add(AppDomain.CurrentDomain.BaseDirectory); } catch { }
            try { dirs.Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)); } catch { }
            foreach (string d in dirs.ToArray())
            {
                string cur = d;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
                {
                    string p = Path.Combine(cur, fileName);
                    if (File.Exists(p)) return p;
                    cur = Path.GetDirectoryName(cur);
                }
            }
            return null;
        }

        private void OpenResource(string fileName)
        {
            string path = FindResource(fileName);
            if (path == null)
            {
                MessageBox.Show("未找到文件：" + fileName, AppTitleBase);
                return;
            }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, AppTitleBase); }
        }

        private void RunCrxScript(bool register)
        {
            string script = FindResource(register ? "caxa3dcodenameextraction-register-crx.ps1" : "caxa3dcodenameextraction-unregister-crx.ps1");
            if (script == null)
            {
                MessageBox.Show("未找到 CRX " + (register ? "注册" : "卸载") + "脚本（caxa3dcodenameextraction-register-crx.ps1 / caxa3dcodenameextraction-unregister-crx.ps1）。",
                    AppTitleBase);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','" + script + "'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                AddLog("已请求" + (register ? "注册" : "卸载") + " CRX（如弹出 UAC 请允许）");
            }
            catch (Exception ex)
            {
                MessageBox.Show("执行脚失败：" + ex.Message, AppTitleBase);
            }
        }

        private void AddLog(string message)
        {
            // 窗口已关闭 / 控件已释放：置停止标志让流程尽快退出，并且不再往已释放控件写字。
            // 这里不抛异常：AddLog 常被 catch/finally 调用，抛出去会越过 catch 逃到线程顶层，
            // 在 net48 下等于未捕获异常（可能崩 CAXA）。流程会在 WaitUntil/停止检查点
            // 通过 `if (FlowGate.StopRequested) throw new OperationStoppedException()` 正常中止。
            if (IsDisposed || _txtLog == null || _txtLog.IsDisposed)
            {
                FlowGate.StopRequested = true;
                Diagnostics.Trace("AddLog: 窗口已释放，请求停止；丢弃消息=" + message);
                return;
            }
            if (_txtLog.InvokeRequired)
            {
                try { _txtLog.Invoke(new Action<string>(AddLog), message); } catch { }
                return;
            }
            _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
            _txtLog.ScrollToCaret();
        }
        /// <summary>
        /// 供后台线程实时写日志区：**只 BeginInvoke，绝不同步等**。
        /// 代点线程同步 Invoke 时若主线程正好卡在 CAXA 不泵消息的原生调用里，两边互等 ——
        /// 对话框没人填、主线程白等 600 秒。宁可丢一行也不能卡住。
        /// </summary>
        private void AddLogLive(string message)
        {
            if (IsDisposed || _txtLog == null || _txtLog.IsDisposed) return;
            try { _txtLog.BeginInvoke(new Action<string>(AddLog), message); }
            catch { }
        }


        private void BtnSaveLog_Click(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog())
            {
                sfd.FileName = AppTitleBase + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                sfd.Filter = "文本文件 (*.txt)|*.txt";
                if (sfd.ShowDialog() != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(sfd.FileName, _txtLog.Text, Encoding.UTF8);
                    AddLog("日志已保存: " + sfd.FileName);
                }
                catch (Exception ex) { MessageBox.Show("保存日志失败：" + ex.Message, AppTitleBase); }
            }
        }
    }

    /// <summary>用户点「停止」后由 WaitUntil 抛出，用于中断整条长流程。</summary>
    internal sealed class OperationStoppedException : Exception
    {
        public OperationStoppedException() : base("用户已停止") { }
    }
}
