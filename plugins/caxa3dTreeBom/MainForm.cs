using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dTreeBom
{
    /// <summary>
    /// 主窗体（界面与 C:\code\SwTreeBom 的 Form1 保持一致）：
    /// CAXA 3D 连接状态区 / 树形BOM表格区 / 操作日志区 三段式。
    /// 功能：从当前选中装配体生成树形BOM、读取自定义属性、抓取缩略图、保存/输出BOM表格。
    /// 额外增加：长任务可见进度 + 停止按钮（不保存、不改图纸）。
    /// </summary>
    [ComVisible(false)]
    internal class MainForm : Form
    {
        // ===== 专用列名（不按自定义属性抓取） =====
        private const string HC_Level = "层级";
        private const string HC_Drawing = "是否有工程图";
        private const string HC_Thumb = "缩略图";
        private const string HC_Qty = "数量";
        private const string HC_TotalQty = "总数量";

        private readonly CaxaService _svc;

        private string _currentFilePath = "";
        private string _currentElemName = "";
        private string _tablePath = "";
        private List<string> _headers = new List<string>();
        private List<BomRow> _rows = new List<BomRow>();
        private int _thumbCol = -1;
        private bool _loading;

        // 窗口前置（铁律2：默认前置，点击可切换）
        private bool _topMost = true;
        private Button _btnTopMost;
        private Panel _rightPanel;

        // 附加合并规则（字段来自用户可编辑的 caxa3dtreebomaddfix.txt；为空则不做附加合并）
        private readonly List<string> _mergeFields = new List<string>();
        private Button _btnAddMerge;

        private CancellationTokenSource _cts;
        private readonly System.Windows.Forms.Timer _logTimer = new System.Windows.Forms.Timer();
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();

        /// <summary>缩略图缓存（键 = PNG 路径），避免同名文件重复加载。</summary>
        private readonly Dictionary<string, Image> _thumbImages = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        /// <summary>默认模板表格路径（用户目录，CAXA bin 目录无写权限）。</summary>
        private static string DefaultTablePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "caxa3dTreeBom");
                string p = Path.Combine(dir, "caxa3dTreeBomxls.xlsx");
                if (!File.Exists(p))
                {
                    // 首次运行：从插件目录播种模板（合并到工具箱后没有独立程序目录）
                    try
                    {
                        string seedDir = Path.GetDirectoryName(typeof(MainForm).Assembly.Location);
                        if (string.IsNullOrEmpty(seedDir)) seedDir = AppContext.BaseDirectory;
                        string seed = Path.Combine(seedDir, "caxa3dTreeBomxls.xlsx");
                        if (!File.Exists(seed)) seed = Path.Combine(AppContext.BaseDirectory, "caxa3dTreeBomxls.xlsx");
                        if (File.Exists(seed))
                        {
                            Directory.CreateDirectory(dir);
                            File.Copy(seed, p, false);
                        }
                    }
                    catch { }
                }
                return p;
            }
        }

        // ===== 控件声明（名称与 SwTreeBom 一致） =====
        private Panel mainPanel;
        private GroupBox grpConnection;
        private Label lblConnectionStatus;
        private Button btnRefresh;
        private Label lblFileName;
        private Button btnLocateFile;
        private Panel tableContainer;
        private GroupBox grpTable;
        private Label lblTableFile;
        private Button btnGenerate;
        private Button btnReadProps;
        private Button btnThumb;
        private Button btnSaveTable;
        private Button btnExport;
        private Button btnLocateTable;
        private Button btnSelectTable;
        private Button btnReloadTable;
        private Button btnStop;
        private DataGridView dgvTable;
        private Panel resizeHandle;
        private GroupBox grpLog;
        private RichTextBox txtLog;
        private Button btnSaveLog;
        private Button btnClearLog;
        private ContextMenuStrip cmsCell;
        private ContextMenuStrip cmsColumnHeader;
        private System.Windows.Forms.Timer timerCheck;
        private ToolTip toolTip1;

        // 拖拽相关
        private bool isDragging = false;
        private int dragStartY;
        private int dragStartHeight;

        private int _menuRow = -1;
        private int _menuCol = -1;

        public MainForm(IZBaseApp app)
        {
            _svc = new CaxaService(app);

            // 附加合并规则：从用户可编辑的规则文件读取（缺失则按默认规则生成）
            try
            {
                string rulePath = MergeRule.EnsureFile();
                _mergeFields.AddRange(MergeRule.Load(rulePath));
            }
            catch { }

            InitializeComponent();

            BuildTopMostButton();
            TopMost = _topMost;

            Diagnostics.UiLogSink = AddLog;

            BuildContextMenus();
            AddLog("caxa3dTreeBom 已启动。");
            if (_mergeFields.Count > 0)
                AddLog("📝 附加合并规则字段：" + string.Join("、", _mergeFields.ToArray()));
            else
                AddLog("📝 附加合并规则未配置字段，总数量按同名件统计。");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                Activate();
                SetForegroundWindow(Handle);
            }
            catch { }
            ConnectToCaxa();
            AutoLoadDefaultTable();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                AddLog("窗口关闭：已请求停止后台任务（不保存、不改图纸）。");
            }
            if (timerCheck != null) timerCheck.Stop();
            if (_logTimer != null) _logTimer.Stop();
            ClearThumbs();
            Diagnostics.UiLogSink = null;
            base.OnFormClosing(e);
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // ==================================================================== 界面构造（照搬 SwTreeBom）

        private void InitializeComponent()
        {
            SuspendLayout();

            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1400, 1050);
            MinimumSize = new Size(1200, 700);
            Text = "caxa3dTreeBom - CAXA 3D 树形结构 BOM 清单工具";
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9F);

            // ===== 主面板 =====
            mainPanel = new Panel();
            mainPanel.Dock = DockStyle.Fill;
            mainPanel.AutoScroll = true;
            mainPanel.Padding = new Padding(15);
            mainPanel.BackColor = Color.FromArgb(240, 240, 240);

            // ===== CAXA 3D 连接状态区域 =====
            grpConnection = new GroupBox();
            grpConnection.Text = "CAXA 3D 连接状态";
            grpConnection.Font = new Font("Microsoft YaHei", 11F, FontStyle.Bold);
            grpConnection.ForeColor = Color.FromArgb(0, 120, 212);
            grpConnection.Height = 60;
            grpConnection.Dock = DockStyle.Top;
            grpConnection.Padding = new Padding(8, 6, 8, 6);

            var connPanel = new Panel();
            connPanel.Dock = DockStyle.Fill;

            lblConnectionStatus = new Label();
            lblConnectionStatus.Text = "连接状态: 未连接";
            lblConnectionStatus.Font = new Font("Microsoft YaHei", 11F, FontStyle.Bold);
            lblConnectionStatus.ForeColor = Color.FromArgb(209, 52, 56);
            lblConnectionStatus.AutoSize = true;
            lblConnectionStatus.TextAlign = ContentAlignment.MiddleLeft;

            btnRefresh = MakeButton("刷新连接", 90, 28, Color.FromArgb(0, 120, 212));
            btnRefresh.Click += btnRefresh_Click;

            lblFileName = new Label();
            lblFileName.Text = "文件: 无";
            lblFileName.Font = new Font("Microsoft YaHei", 11F);
            lblFileName.ForeColor = Color.FromArgb(51, 51, 51);
            lblFileName.AutoSize = true;
            lblFileName.TextAlign = ContentAlignment.MiddleLeft;

            btnLocateFile = MakeButton("定位文件", 90, 28, Color.FromArgb(0, 120, 212));
            btnLocateFile.Margin = new Padding(6, 0, 6, 0);
            btnLocateFile.Enabled = false;
            btnLocateFile.Click += btnLocateFile_Click;

            var connFlow = new FlowLayoutPanel();
            connFlow.Dock = DockStyle.Fill;
            connFlow.FlowDirection = FlowDirection.LeftToRight;
            connFlow.WrapContents = false;
            connFlow.Padding = new Padding(0, 2, 0, 0);
            connFlow.Controls.Add(lblConnectionStatus);
            connFlow.Controls.Add(btnRefresh);

            _rightPanel = new Panel();
            _rightPanel.Dock = DockStyle.Right;
            _rightPanel.Width = 500;
            var rightFlow = new FlowLayoutPanel();
            rightFlow.Dock = DockStyle.Fill;
            rightFlow.FlowDirection = FlowDirection.RightToLeft;
            rightFlow.WrapContents = false;
            rightFlow.Padding = new Padding(0, 2, 0, 0);
            rightFlow.Controls.Add(btnLocateFile);
            rightFlow.Controls.Add(lblFileName);
            _rightPanel.Controls.Add(rightFlow);

            connPanel.Controls.Add(connFlow);
            connPanel.Controls.Add(_rightPanel);
            grpConnection.Controls.Add(connPanel);

            // ===== 表格容器 =====
            tableContainer = new Panel();
            tableContainer.Dock = DockStyle.Fill;
            tableContainer.Padding = new Padding(0, 10, 0, 0);

            // ===== 表格区域 =====
            grpTable = new GroupBox();
            grpTable.Text = "树形BOM表格";
            grpTable.Font = new Font("Microsoft YaHei", 11F, FontStyle.Bold);
            grpTable.ForeColor = Color.FromArgb(0, 120, 212);
            grpTable.Dock = DockStyle.Fill;
            grpTable.Padding = new Padding(10);

            var tableInfoPanel = new TableLayoutPanel();
            tableInfoPanel.Dock = DockStyle.Top;
            tableInfoPanel.Height = 38;
            tableInfoPanel.ColumnCount = 1;
            tableInfoPanel.RowCount = 1;
            tableInfoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableInfoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));

            // 表格文件地址 / 任务进度提示已按需求上移到顶部「附加合并规则」按钮右侧
            lblTableFile = new Label();
            lblTableFile.Text = "表格文件: 未选择";
            lblTableFile.Font = new Font("Microsoft YaHei", 10F);
            lblTableFile.ForeColor = Color.FromArgb(102, 102, 102);
            lblTableFile.AutoSize = false;
            lblTableFile.AutoEllipsis = true;
            lblTableFile.Dock = DockStyle.Fill;
            lblTableFile.TextAlign = ContentAlignment.MiddleLeft;

            var tableBtnFlow = new FlowLayoutPanel();
            tableBtnFlow.Dock = DockStyle.Fill;
            tableBtnFlow.FlowDirection = FlowDirection.RightToLeft;
            tableBtnFlow.WrapContents = false;
            tableBtnFlow.AutoSize = false;
            tableBtnFlow.Padding = new Padding(0, 3, 0, 0);

            btnExport = MakeButton("输出bom表格", 110, 30, Color.FromArgb(0, 120, 212));
            btnExport.Click += btnExport_Click;

            btnGenerate = MakeButton("生成BOM", 118, 30, Color.FromArgb(16, 124, 16));
            btnGenerate.Click += btnGenerate_Click;

            btnReadProps = MakeButton("读取属性", 96, 30, Color.FromArgb(0, 120, 212));
            btnReadProps.Click += btnReadProps_Click;

            btnThumb = MakeButton("抓取缩略图", 110, 30, Color.FromArgb(0, 120, 212));
            btnThumb.Click += btnThumb_Click;

            btnSaveTable = MakeButton("保存表格", 96, 30, Color.FromArgb(0, 120, 212));
            btnSaveTable.Click += btnSaveTable_Click;

            btnLocateTable = MakeButton("定位表格", 96, 30, Color.FromArgb(0, 120, 212));
            btnLocateTable.Click += btnLocateTable_Click;

            btnSelectTable = MakeButton("选择表格", 96, 30, Color.FromArgb(0, 120, 212));
            btnSelectTable.Click += btnSelectTable_Click;

            btnReloadTable = MakeButton("刷新表格", 96, 30, Color.FromArgb(0, 120, 212));
            btnReloadTable.Click += btnReloadTable_Click;

            // 长任务中止入口（CAXA 侧新增，不改变其它布局）
            btnStop = MakeButton("停止", 70, 30, Color.FromArgb(209, 52, 56));
            btnStop.Enabled = false;
            btnStop.Click += btnStop_Click;

            tableBtnFlow.Controls.Add(btnExport);
            tableBtnFlow.Controls.Add(btnGenerate);
            tableBtnFlow.Controls.Add(btnReadProps);
            tableBtnFlow.Controls.Add(btnThumb);
            tableBtnFlow.Controls.Add(btnSaveTable);
            tableBtnFlow.Controls.Add(btnLocateTable);
            tableBtnFlow.Controls.Add(btnSelectTable);
            tableBtnFlow.Controls.Add(btnReloadTable);
            tableBtnFlow.Controls.Add(btnStop);

            tableInfoPanel.Controls.Add(tableBtnFlow, 0, 0);

            dgvTable = new DataGridView();
            dgvTable.Dock = DockStyle.Fill;
            dgvTable.AllowUserToAddRows = false;
            dgvTable.AllowUserToDeleteRows = false;
            dgvTable.AllowUserToResizeColumns = true;
            dgvTable.AllowUserToResizeRows = true;
            dgvTable.RowHeadersWidth = 70;
            dgvTable.ColumnHeadersHeight = 30;
            dgvTable.DefaultCellStyle.Font = new Font("Microsoft YaHei", 10F);
            dgvTable.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            dgvTable.DefaultCellStyle.Padding = new Padding(3);
            dgvTable.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            dgvTable.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 232, 232);
            dgvTable.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(51, 51, 51);
            dgvTable.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            dgvTable.RowHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            dgvTable.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 232, 232);
            dgvTable.RowHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            dgvTable.GridColor = Color.FromArgb(224, 224, 224);
            dgvTable.BorderStyle = BorderStyle.None;
            dgvTable.BackgroundColor = Color.White;
            dgvTable.SelectionMode = DataGridViewSelectionMode.CellSelect;
            dgvTable.MultiSelect = true;
            dgvTable.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            dgvTable.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
            dgvTable.CellMouseClick += dgvTable_CellMouseClick;
            dgvTable.CellValueChanged += dgvTable_CellValueChanged;
            dgvTable.ColumnHeaderMouseClick += dgvTable_ColumnHeaderMouseClick;
            dgvTable.CellPainting += dgvTable_CellPainting;

            try
            {
                typeof(DataGridView).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(dgvTable, true, null);
            }
            catch { }

            resizeHandle = new Panel();
            resizeHandle.Height = 10;
            resizeHandle.Dock = DockStyle.Bottom;
            resizeHandle.BackColor = Color.FromArgb(0, 120, 212);
            resizeHandle.Cursor = Cursors.SizeNS;
            resizeHandle.MouseDown += resizeHandle_MouseDown;
            resizeHandle.MouseMove += resizeHandle_MouseMove;
            resizeHandle.MouseUp += resizeHandle_MouseUp;

            grpTable.Controls.Add(dgvTable);
            grpTable.Controls.Add(resizeHandle);
            grpTable.Controls.Add(tableInfoPanel);

            // ===== 日志区域 =====
            grpLog = new GroupBox();
            grpLog.Text = "操作日志";
            grpLog.Font = new Font("Microsoft YaHei", 11F, FontStyle.Bold);
            grpLog.ForeColor = Color.FromArgb(0, 120, 212);
            grpLog.Dock = DockStyle.Bottom;
            grpLog.Height = 220;
            grpLog.Padding = new Padding(8);

            txtLog = new RichTextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.ReadOnly = true;
            txtLog.BackColor = Color.FromArgb(30, 30, 30);
            txtLog.ForeColor = Color.FromArgb(212, 212, 212);
            txtLog.Font = new Font("Consolas", 10F);
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            txtLog.HideSelection = false;

            var logBtnPanel = new Panel();
            logBtnPanel.Dock = DockStyle.Bottom;
            logBtnPanel.Height = 40;

            btnSaveLog = MakeButton("保存日志", 100, 32, Color.FromArgb(0, 120, 212));
            btnSaveLog.Location = new Point(0, 4);
            btnSaveLog.Click += btnSaveLog_Click;

            btnClearLog = MakeButton("清空日志", 100, 32, Color.FromArgb(209, 52, 56));
            btnClearLog.Location = new Point(108, 4);
            btnClearLog.Click += btnClearLog_Click;

            logBtnPanel.Controls.Add(btnSaveLog);
            logBtnPanel.Controls.Add(btnClearLog);
            grpLog.Controls.Add(txtLog);
            grpLog.Controls.Add(logBtnPanel);

            tableContainer.Controls.Add(grpTable);
            tableContainer.Controls.Add(grpLog);

            mainPanel.Controls.Add(tableContainer);
            mainPanel.Controls.Add(grpConnection);

            Controls.Add(mainPanel);

            cmsCell = new ContextMenuStrip();
            cmsColumnHeader = new ContextMenuStrip();

            toolTip1 = new ToolTip();
            toolTip1.AutoPopDelay = 15000;
            toolTip1.InitialDelay = 500;
            toolTip1.ReshowDelay = 100;

            // 日志批量刷新（后台线程写队列 → UI 定时消费，避免跨线程刷屏卡死）
            _logTimer.Interval = 250;
            _logTimer.Tick += delegate { FlushLog(); };
            _logTimer.Start();

            // 定时检查连接
            timerCheck = new System.Windows.Forms.Timer();
            timerCheck.Interval = 2000;
            timerCheck.Tick += timerCheck_Tick;
            timerCheck.Start();

            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>按统一风格创建按钮。</summary>
        private static Button MakeButton(string text, int width, int height, Color back)
        {
            var btn = new Button();
            btn.Text = text;
            btn.Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold);
            btn.BackColor = back;
            btn.ForeColor = Color.White;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Size = new Size(width, height);
            btn.Margin = new Padding(3, 0, 3, 0);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        // ==================================================================== 窗口前置（逻辑对齐 caxa3dmytoolsaddin）

        /// <summary>
        /// 铁律2：窗口默认前置，在主面板顶部放「窗口前置」「附加合并规则」两个按钮，
        /// 两个按钮右侧顺排「表格文件」地址与任务进度提示（不占用表格标题行）。
        /// </summary>
        private void BuildTopMostButton()
        {
            var topBar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                ColumnCount = 3,
                RowCount = 1,
                Padding = new Padding(10, 6, 10, 6),
                Margin = new Padding(0),
                BackColor = Color.FromArgb(240, 240, 240)
            };
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106F));
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132F));
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            topBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _btnTopMost = new Button
            {
                Text = "窗口前置",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 6, 0),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnTopMost.FlatAppearance.BorderSize = 0;
            _btnTopMost.Click += delegate { ToggleTopMost(); };

            _btnAddMerge = new Button
            {
                Text = "附加合并规则",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 0),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnAddMerge.FlatAppearance.BorderSize = 0;
            _btnAddMerge.Click += delegate { OpenMergeRuleFile(); };
            toolTip1.SetToolTip(_btnAddMerge, "点击打开规则文件编辑（抓取引号内的字段，# 开头为说明行）。编辑保存后自动重算总数量。");

            // 表格文件地址 + 抓取/生成进度信息：紧随「附加合并规则」右侧（过长时右侧省略，悬停看全路径）
            lblTableFile.Margin = new Padding(12, 0, 0, 0);

            topBar.Controls.Add(_btnTopMost, 0, 0);
            topBar.Controls.Add(_btnAddMerge, 1, 0);
            topBar.Controls.Add(lblTableFile, 2, 0);

            // Dock=Top 追加到 mainPanel 末尾 → 排在最顶部（grpConnection 之下推）
            mainPanel.Controls.Add(topBar);
            ApplyTopMostVisual();
        }

        private void ToggleTopMost()
        {
            _topMost = !_topMost;
            TopMost = _topMost;
            ApplyTopMostVisual();
            AddLog(_topMost ? "📌 窗口前置已开启。" : "📌 窗口前置已关闭。");
        }

        private void ApplyTopMostVisual()
        {
            if (_btnTopMost == null) return;
            if (_topMost)
            {
                _btnTopMost.BackColor = Color.FromArgb(0, 120, 212);
                _btnTopMost.ForeColor = Color.White;
                _btnTopMost.FlatStyle = FlatStyle.Flat;
                _btnTopMost.FlatAppearance.BorderSize = 0;
            }
            else
            {
                _btnTopMost.BackColor = SystemColors.Control;
                _btnTopMost.ForeColor = SystemColors.ControlText;
                _btnTopMost.FlatStyle = FlatStyle.Standard;
            }
        }

        // ==================================================================== 附加合并规则（功能按钮：打开规则文件编辑）

        /// <summary>
        /// 「附加合并规则」功能按钮：打开规则文件让用户编辑；保存关闭后自动重新读取规则并重算总数量。
        /// </summary>
        private async void OpenMergeRuleFile()
        {
            ApplyMergeRules();      // 先应用磁盘上已有规则（含上次编辑结果）

            string path = MergeRule.EnsureFile();
            AddLog("📝 打开附加合并规则文件：" + path);

            Process p = null;
            try
            {
                p = Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                AddLog("❌ 打开规则文件失败：" + ex.Message);
                MessageBox.Show(this, "打开规则文件失败：" + ex.Message + Environment.NewLine + path,
                    "规则文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (p != null)
            {
                await Task.Run(delegate { try { p.WaitForExit(); } catch { } });
                try { p.Dispose(); } catch { }
            }

            ApplyMergeRules();      // 编辑保存后再应用
        }

        /// <summary>重新读取规则文件；字段缺失时补读属性，然后按新规则重算总数量。</summary>
        private void ApplyMergeRules()
        {
            List<string> old = new List<string>(_mergeFields);
            _mergeFields.Clear();
            try { _mergeFields.AddRange(MergeRule.Load(MergeRule.RuleFilePath)); }
            catch { }

            bool same = old.Count == _mergeFields.Count;
            if (same)
            {
                for (int i = 0; i < old.Count; i++)
                {
                    if (!string.Equals(old[i], _mergeFields[i], StringComparison.Ordinal)) { same = false; break; }
                }
            }

            if (_mergeFields.Count == 0)
                AddLog("📝 规则文件未解析到任何字段（# 开头为说明行），附加合并已停用，总数量按同名件统计。");
            else
                AddLog("📝 附加合并规则字段：" + string.Join("、", _mergeFields.ToArray()));

            if (_rows.Count == 0) return;

            if (_mergeFields.Count == 0 || AllRowsHaveMergeFields(_rows))
            {
                RecomputeTotals();
                AddLog("🔢 总数量已重算（" + _rows.Count + " 行）。");
                return;
            }

            if (same) return;                  // 规则未变且属性已齐：上面已重算过

            if (!EnsureConnected()) return;
            List<BomRow> rows = _rows;
            List<string> propHeaders = GetPropertyHeaders();
            List<string> fields = new List<string>(_mergeFields);

            RunOnUiThread("附加合并规则", delegate(CancellationToken token)
            {
                var reader = new BomBuilder(_svc, AddLog)
                {
                    PropertyHeaders = propHeaders,
                    MergeFields = fields,
                    Cancelled = delegate { return token.IsCancellationRequested; },
                    Progress = delegate(int done, string msg)
                    {
                        PostStatus("正在读取合并属性… " + msg);
                        PumpUi();
                    }
                };
                int cnt = reader.RefreshProperties(rows);
                DisplayTableData();
                AddLog("🔢 已补读合并属性并重算总数量：" + cnt + " 行。");
            });
        }

        private bool AllRowsHaveMergeFields(List<BomRow> rows)
        {
            foreach (BomRow r in rows)
                foreach (string f in _mergeFields)
                    if (!r.Properties.ContainsKey(f)) return false;
            return true;
        }

        /// <summary>按当前规则字段重算总数量并刷新表格。</summary>
        private void RecomputeTotals()
        {
            var builder = new BomBuilder(_svc, AddLog) { MergeFields = new List<string>(_mergeFields) };
            builder.RecomputeTotals(_rows);
            DisplayTableData();
        }

        // ==================================================================== 日志与连接

        /// <summary>追加日志（线程安全，后台线程只入队）。</summary>
        private void AddLog(string message)
        {
            if (message == null) return;
            _logQueue.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
        }

        private void FlushLog()
        {
            if (_logQueue.IsEmpty || txtLog == null || txtLog.IsDisposed) return;
            var sb = new System.Text.StringBuilder();
            string s;
            int n = 0;
            while (n < 300 && _logQueue.TryDequeue(out s))
            {
                sb.Append(s).Append(Environment.NewLine);
                n++;
            }
            if (sb.Length == 0) return;
            txtLog.AppendText(sb.ToString());
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        private void ConnectToCaxa()
        {
            bool ok = _svc.IsConnected;
            UpdateConnectionDisplay(ok);
            if (ok) AddLog("✅ " + _svc.VersionText);
            else AddLog("❌ " + _svc.VersionText);
        }

        private void UpdateConnectionDisplay(bool connected)
        {
            if (lblConnectionStatus.InvokeRequired)
            {
                lblConnectionStatus.Invoke(new Action<bool>(UpdateConnectionDisplay), connected);
                return;
            }

            string file = connected ? _svc.CurrentFilePath : "";
            string elemName = connected ? _svc.GetSelectedElementName() : "";
            _currentFilePath = file;
            _currentElemName = elemName;

            lblConnectionStatus.Text = connected ? "连接状态: 已连接" : "连接状态: 未连接";
            lblConnectionStatus.ForeColor = connected ? Color.FromArgb(16, 124, 16) : Color.FromArgb(209, 52, 56);

            lblFileName.Text = "文件: " + FileLabelText(file, elemName);
            btnLocateFile.Enabled = connected && !string.IsNullOrEmpty(file);
            FitRightPanel();
        }

        /// <summary>文件标签格式：文件名.ics----零件名（缺省部分自动省略，语义同 caxa3dAttributeManageraddin）。</summary>
        private static string FileLabelText(string file, string elemName)
        {
            string doc = "";
            if (!string.IsNullOrEmpty(file))
            {
                try { doc = Path.GetFileName(file); }
                catch { doc = file; }
            }
            if (string.IsNullOrEmpty(doc)) return string.IsNullOrEmpty(elemName) ? "无" : elemName;
            if (string.IsNullOrEmpty(elemName)) return doc;
            return doc + "----" + elemName;
        }

        /// <summary>
        /// 按文件名实际文字宽度调整右侧面板宽度，保证「文件: xxx」完整显示（布局参考 caxa3dAttributeManageraddin）。
        /// </summary>
        private void FitRightPanel()
        {
            if (_rightPanel == null || lblFileName == null || btnLocateFile == null) return;
            int need = lblFileName.PreferredWidth
                     + btnLocateFile.Width
                     + btnLocateFile.Margin.Horizontal
                     + 20;   // FlowLayout 内边距 + 面板左右余量
            int max = Math.Max(500, (int)(ClientSize.Width * 0.70));
            _rightPanel.Width = Math.Max(360, Math.Min(need, max));
        }

        private bool EnsureConnected()
        {
            if (!_svc.IsConnected)
            {
                UpdateConnectionDisplay(false);
                AddLog("❌ " + _svc.VersionText);
                return false;
            }
            UpdateConnectionDisplay(true);
            return true;
        }

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            AddLog("🔄 正在刷新连接状态...");
            ConnectToCaxa();
        }

        private void timerCheck_Tick(object sender, EventArgs e)
        {
            try
            {
                bool ok = _svc.IsConnected;
                if (!ok)
                {
                    if (!string.IsNullOrEmpty(_currentFilePath) || !string.IsNullOrEmpty(_currentElemName))
                    {
                        UpdateConnectionDisplay(false);
                        AddLog("⚠️ CAXA 3D 场景已关闭");
                    }
                    return;
                }

                // 文档路径或"选中的零部件"任一变化都刷新显示（语义同 caxa3dAttributeManageraddin）
                string path = _svc.CurrentFilePath;
                string elem = _svc.GetSelectedElementName();
                bool pathChanged = !string.Equals(path, _currentFilePath, StringComparison.OrdinalIgnoreCase);
                bool elemChanged = !string.Equals(elem, _currentElemName, StringComparison.Ordinal);

                if (pathChanged || elemChanged)
                {
                    UpdateConnectionDisplay(true);
                    if (pathChanged)
                        AddLog(string.IsNullOrEmpty(path) ? "⚠️ 当前无打开文件" : ("📄 文件已切换: " + Path.GetFileName(path)));
                    else
                        AddLog(string.IsNullOrEmpty(elem) ? "🎯 当前未选中零部件" : ("🎯 当前选中: " + elem));
                }
            }
            catch (Exception ex)
            {
                AddLog("⚠️ 检查连接状态时出错: " + ex.Message);
            }
        }

        private void btnLocateFile_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentFilePath) && File.Exists(_currentFilePath))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _currentFilePath + "\"");
                    AddLog("📂 已定位文件: " + _currentFilePath);
                }
                catch (Exception ex) { AddLog("❌ 定位文件失败: " + ex.Message); }
            }
            else AddLog("⚠️ 文件路径无效或文件不存在");
        }

        // ==================================================================== 表格加载

        private void AutoLoadDefaultTable()
        {
            string path = DefaultTablePath;
            if (!File.Exists(path))
            {
                try
                {
                    XlsxTableService.CreateDefaultTemplate(path);
                    AddLog("📄 未找到默认模板，已按标准表头创建: " + path);
                }
                catch (Exception ex)
                {
                    AddLog("❌ 创建默认模板失败: " + ex.Message);
                }
            }
            LoadTable(path);
        }

        private void btnSelectTable_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog
            {
                InitialDirectory = Path.GetDirectoryName(DefaultTablePath),
                Filter = "Excel表格 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*"
            })
            {
                if (ofd.ShowDialog(this) == DialogResult.OK) LoadTable(ofd.FileName);
            }
        }

        private void btnReloadTable_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_tablePath) || !File.Exists(_tablePath))
            {
                AddLog("⚠️ 表格文件路径无效，无法刷新");
                return;
            }
            AddLog("🔄 正在刷新表格数据...");
            LoadTable(_tablePath);
        }

        private void LoadTable(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    AddLog("⚠️ 表格文件不存在: " + path);
                    return;
                }

                AddLog("📊 正在加载表格: " + Path.GetFileName(path));
                List<string> headers = XlsxTableService.LoadHeaders(path);
                if (headers.Count == 0)
                {
                    AddLog("⚠️ 表格第 1 行未检测到表头，已使用默认表头（层级/是否有工程图/缩略图/SC图号/SC名称/SC规格/SC材料/SC备注/数量/总数量）。");
                    headers = new List<string>(XlsxTableService.DefaultHeaders);
                }

                _headers = headers;
                _thumbCol = _headers.FindIndex(h => string.Equals(h.Trim(), HC_Thumb, StringComparison.Ordinal));
                _rows.Clear();
                ClearThumbs();

                _tablePath = path;
                lblTableFile.Text = "表格文件: " + path;
                toolTip1.SetToolTip(lblTableFile, path);

                DisplayTableData();
                AddLog("✅ 表格加载成功: " + _headers.Count + " 列 | 表头: " + string.Join(" / ", _headers));
            }
            catch (Exception ex)
            {
                AddLog("❌ 加载表格失败: " + ex.Message);
            }
        }

        private void btnLocateTable_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_tablePath) && File.Exists(_tablePath))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _tablePath + "\"");
                    AddLog("📂 已定位表格: " + _tablePath);
                }
                catch (Exception ex) { AddLog("❌ 定位表格失败: " + ex.Message); }
            }
            else AddLog("⚠️ 表格文件路径无效或文件不存在");
        }

        // ==================================================================== 表格显示

        /// <summary>重建表格列与行（第 1 行 = 字段名，数据自第 2 行起；列头显示 Excel 字母，行头显示行号）。</summary>
        private void DisplayTableData()
        {
            _loading = true;
            try
            {
                int colCount = Math.Max(1, _headers.Count);
                dgvTable.Columns.Clear();
                dgvTable.Rows.Clear();

                for (int c = 0; c < colCount; c++)
                {
                    string header = c < _headers.Count ? _headers[c] : "";
                    var col = new DataGridViewTextBoxColumn
                    {
                        Name = "Col_" + c,
                        HeaderText = ColIdxToExcel(c),
                        SortMode = DataGridViewColumnSortMode.NotSortable,
                        MinimumWidth = 40,
                        Width = WidthFor(header)
                    };
                    dgvTable.Columns.Add(col);
                }

                // 表头行（Excel 第 1 行）
                int hr = dgvTable.Rows.Add();
                dgvTable.Rows[hr].HeaderCell.Value = "1";
                for (int c = 0; c < colCount; c++)
                {
                    var cell = dgvTable.Rows[hr].Cells[c];
                    cell.Value = c < _headers.Count ? _headers[c] : "";
                    cell.Style.BackColor = Color.FromArgb(0, 120, 212);
                    cell.Style.ForeColor = Color.White;
                    cell.Style.Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold);
                    cell.Style.SelectionBackColor = Color.FromArgb(0, 120, 212);
                    cell.Style.SelectionForeColor = Color.White;
                    cell.ReadOnly = true;
                }
                dgvTable.Rows[hr].Height = 30;

                // 数据行
                for (int i = 0; i < _rows.Count; i++)
                {
                    int r = dgvTable.Rows.Add();
                    dgvTable.Rows[r].HeaderCell.Value = (i + 2).ToString();
                    dgvTable.Rows[r].Height = 46;
                    for (int c = 0; c < colCount; c++)
                        dgvTable.Rows[r].Cells[c].Value = c < _headers.Count ? GetCellValue(_rows[i], c) : "";
                }

                dgvTable.RowTemplate.Height = 46;
                dgvTable.Invalidate();
                lblTableFile.Text = _rows.Count == 0
                    ? ("表格文件: " + _tablePath + "（暂无数据，请点击\"生成BOM\"）")
                    : ("表格文件: " + _tablePath + "（" + _rows.Count + " 行 × " + _headers.Count + " 列）");
                toolTip1.SetToolTip(lblTableFile, _tablePath);
            }
            finally
            {
                _loading = false;
            }
        }

        private static int WidthFor(string header)
        {
            switch ((header ?? "").Trim())
            {
                case HC_Level: return 150;
                case HC_Drawing: return 100;
                case HC_Thumb: return 124;
                case "图号":
                case "SC图号": return 130;
                case "名称":
                case "SC名称": return 170;
                case "规格":
                case "SC规格": return 130;
                case "材料":
                case "SC材料": return 100;
                case "备注":
                case "SC备注": return 150;
                case HC_Qty: return 70;
                case HC_TotalQty: return 80;
                default: return 120;
            }
        }

        /// <summary>取某行某列应显示的文本。</summary>
        private string GetCellValue(BomRow row, int col)
        {
            if (row == null || col < 0 || col >= _headers.Count) return "";
            string h = _headers[col].Trim();
            switch (h)
            {
                case HC_Level: return row.TreeText;
                case HC_Drawing: return row.HasDrawing ? "★" : "";
                case HC_Thumb: return "";
                case HC_Qty: return row.Quantity.ToString();
                case HC_TotalQty: return row.ShowTotal ? row.TotalQuantity.ToString() : "---";
                default:
                    string v;
                    return row.Properties.TryGetValue(h, out v) ? (v ?? "") : "";
            }
        }

        /// <summary>取需按自定义属性抓取的表头（排除专用列）；开启附加合并规则时补上四项比对属性。</summary>
        private List<string> GetPropertyHeaders()
        {
            var list = new List<string>();
            foreach (string h in _headers)
            {
                string t = (h ?? "").Trim();
                if (t.Length == 0) continue;
                if (t == HC_Level || t == HC_Drawing || t == HC_Thumb || t == HC_Qty || t == HC_TotalQty) continue;
                list.Add(t);
            }

            if (_mergeFields.Count > 0)
            {
                foreach (string f in _mergeFields)
                {
                    if (!list.Exists(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase)))
                        list.Add(f);
                }
            }
            return list;
        }

        private static string ColIdxToExcel(int colIdx)
        {
            string result = "";
            int n = colIdx;
            while (n >= 0)
            {
                result = (char)(n % 26 + 65) + result;
                n = n / 26 - 1;
            }
            return result;
        }

        // ==================================================================== 长任务基础设施

        private void SetBusy(bool busy, string status)
        {
            if (btnGenerate == null) return;
            btnGenerate.Enabled = !busy;
            btnReadProps.Enabled = !busy;
            btnThumb.Enabled = !busy;
            btnSaveTable.Enabled = !busy;
            btnExport.Enabled = !busy;
            btnSelectTable.Enabled = !busy;
            btnReloadTable.Enabled = !busy;
            btnStop.Enabled = busy;
            if (_btnAddMerge != null) _btnAddMerge.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (!string.IsNullOrEmpty(status))
                lblTableFile.Text = status;
        }

        /// <summary>更新进度文字并立即重绘（长任务已改在 UI 线程执行）。</summary>
        private void PostStatus(string message)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(delegate { if (!IsDisposed) lblTableFile.Text = message; }));
                    return;
                }
                lblTableFile.Text = message;
                lblTableFile.Refresh();
            }
            catch { }
        }

        private DateTime _lastPump = DateTime.MinValue;

        /// <summary>
        /// 泵一次 UI 消息：日志入框 + 窗口重绘 + 停止按钮可响应。
        /// 长任务在 UI 线程跑时必须靠它保证「可见进度 + 可中止」。
        /// 限流 40ms，避免 DoEvents 过于频繁拖慢遍历。
        /// </summary>
        private void PumpUi()
        {
            if (IsDisposed || !IsHandleCreated) return;
            DateTime now = DateTime.Now;
            if ((now - _lastPump).TotalMilliseconds < 40) return;
            _lastPump = now;
            try
            {
                FlushLog();
                Application.DoEvents();
            }
            catch { }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            CancellationTokenSource cts = _cts;
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            AddLog("⏹️ 用户点击停止：正在安全退出，不保存、不修改图纸。");
            btnStop.Enabled = false;
            lblTableFile.Text = "正在停止…";
        }

        /// <summary>
        /// 执行一个长任务，统一处理取消/异常/按钮状态。
        /// **必须留在 UI（STA）线程执行**：CAXA 的 COM 对象只能在宿主主线程调用，
        /// 用 Task.Run 放到后台线程会让 ActiveDoc 取到 null（实测现象就是
        /// 「当前活动文档不是 3D 场景文档」+「场景顶层 0 个零部件」）。
        /// 进度显示与停止按钮依赖 PumpUi 消息泵维持。
        /// </summary>
        private void RunOnUiThread(string name, Action<CancellationToken> work)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            SetBusy(true, name + "…");
            AddLog("▶️ 开始：" + name);

            try
            {
                work(cts.Token);
            }
            catch (OperationCanceledException)
            {
                AddLog("⏹️ " + name + "：已按用户请求停止。");
            }
            catch (Exception ex)
            {
                AddLog("❌ " + name + "失败：" + ex.Message);
                Diagnostics.Trace(ex.ToString());
            }
            finally
            {
                if (_cts == cts) _cts = null;
                bool cancelled = cts.IsCancellationRequested;
                SetBusy(false, null);
                FlushLog();
                cts.Dispose();
                AddLog("⏹️ " + name + (cancelled ? "：已停止" : "：结束"));
                FlushLog();
            }
        }

        // ==================================================================== BOM 生成

        private void btnGenerate_Click(object sender, EventArgs e)
        {
            if (!EnsureConnected()) return;

            if (_svc.SceneDoc == null)
            {
                AddLog("⚠️ 当前活动文档不是 3D 场景文档，请先切换到设计环境。");
                AddLog("🔎 " + _svc.DescribeActiveDoc());
                return;
            }

            List<string> propHeaders = GetPropertyHeaders();
            List<string> fields = new List<string>(_mergeFields);

            RunOnUiThread("生成BOM", delegate(CancellationToken token)
            {
                AddLog("🔧 开始遍历装配结构生成树形BOM...");
                AddLog("🔎 " + _svc.DescribeActiveDoc());

                string err;
                List<IZElement> roots = _svc.GetSelectedElements(out err);
                if (roots.Count > 0)
                    AddLog("🎯 遍历起点：当前选中 " + roots.Count + " 个零部件。");
                else
                {
                    if (!string.IsNullOrEmpty(err)) AddLog("⚠️ " + err);
                    roots = _svc.GetTopLevelElements();
                    AddLog("🎯 遍历起点：场景顶层 " + roots.Count + " 个零部件。");
                }

                if (roots.Count == 0)
                {
                    AddLog("⚠️ 未找到可遍历的零部件，生成终止。");
                    return;
                }

                var builder = new BomBuilder(_svc, AddLog)
                {
                    PropertyHeaders = propHeaders,
                    MergeFields = fields,
                    Cancelled = delegate { return token.IsCancellationRequested; },
                    Progress = delegate(int done, string msg)
                    {
                        PostStatus("正在生成BOM… " + msg + "（已处理 " + done + " 个节点）");
                        PumpUi();
                    }
                };

                List<BomRow> rows = builder.Build(roots);
                bool cancelled = token.IsCancellationRequested;

                _rows = rows;
                ClearThumbs();
                DisplayTableData();
                if (!cancelled && rows.Count > 0)
                    AddLog("✅ 树形BOM已生成：" + rows.Count + " 行（层级编码 1 / 1.1 / 1.1.1 …）。");
            });
        }

        // ==================================================================== 读取属性

        private void btnReadProps_Click(object sender, EventArgs e)
        {
            if (_rows.Count == 0) { AddLog("⚠️ 请先点击\"生成BOM\"。"); return; }
            if (!EnsureConnected()) return;

            List<string> propHeaders = GetPropertyHeaders();
            if (propHeaders.Count == 0) { AddLog("⚠️ 当前表头中无按自定义属性抓取的列。"); return; }

            List<BomRow> rows = _rows;
            List<string> fields = new List<string>(_mergeFields);

            RunOnUiThread("读取属性", delegate(CancellationToken token)
            {
                AddLog("🔍 正在重新读取自定义属性与工程图标记...");
                var reader = new BomBuilder(_svc, AddLog)
                {
                    PropertyHeaders = propHeaders,
                    MergeFields = fields,
                    Cancelled = delegate { return token.IsCancellationRequested; },
                    Progress = delegate(int done, string msg)
                    {
                        PostStatus("正在读取属性… " + msg);
                        PumpUi();
                    }
                };

                int cnt = reader.RefreshProperties(rows);
                DisplayTableData();
                AddLog("✅ 属性读取完成：共处理 " + cnt + " 行。");
            });
        }

        // ==================================================================== 抓取缩略图

        private void btnThumb_Click(object sender, EventArgs e)
        {
            if (_rows.Count == 0) { AddLog("⚠️ 请先点击\"生成BOM\"。"); return; }
            if (!EnsureConnected()) return;
            if (_thumbCol < 0) { AddLog("⚠️ 表头中未包含\"缩略图\"列，无法回填缩略图。"); return; }

            List<BomRow> rows = _rows;

            RunOnUiThread("抓取缩略图", delegate(CancellationToken token)
            {
                AddLog("🖼️ 开始抓取缩略图（按文件预览图导出 PNG）...");
                var builder = new BomBuilder(_svc, AddLog)
                {
                    PropertyHeaders = new List<string>(),
                    Cancelled = delegate { return token.IsCancellationRequested; },
                    Progress = delegate(int done, string msg)
                    {
                        PostStatus("正在抓取缩略图… " + msg);
                        PumpUi();
                    }
                };

                builder.CaptureThumbnails(rows);
                ReloadThumbs();
                dgvTable.Invalidate();
            });
        }

        // ==================================================================== 保存 / 输出BOM表格

        private void btnSaveTable_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_tablePath)) _tablePath = DefaultTablePath;
            SaveTo(_tablePath);
        }

        private void btnExport_Click(object sender, EventArgs e)
        {
            if (_rows.Count == 0) { AddLog("⚠️ 暂无可导出的数据，请先生成BOM。"); return; }

            string title = Path.GetFileNameWithoutExtension(_currentFilePath);
            if (string.IsNullOrEmpty(title)) title = "caxa3dTreeBom";
            string dir = Path.GetDirectoryName(_currentFilePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            using (var sfd = new SaveFileDialog
            {
                Title = "导出 BOM 表格",
                Filter = "Excel表格 (*.xlsx)|*.xlsx",
                InitialDirectory = dir,
                FileName = title + " BOM_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                if (SaveTo(sfd.FileName))
                    AddLog("📤 已导出到: " + sfd.FileName);
            }
        }

        private bool SaveTo(string path)
        {
            if (_headers.Count == 0) { AddLog("⚠️ 无表头，无法保存。"); return false; }

            Cursor = Cursors.WaitCursor;
            try
            {
                var dataRows = new List<string[]>(_rows.Count);
                for (int i = 0; i < _rows.Count; i++)
                {
                    var arr = new string[_headers.Count];
                    for (int c = 0; c < _headers.Count; c++) arr[c] = GetCellValue(_rows[i], c);
                    dataRows.Add(arr);
                }

                Func<int, string> thumbOf = delegate(int rowIndex)
                {
                    if (rowIndex < 0 || rowIndex >= _rows.Count) return null;
                    return _rows[rowIndex].ThumbnailPath;
                };

                XlsxTableService.Save(path, _headers, dataRows, _thumbCol, thumbOf);

                // 成功判定只看文件侧权威副作用：存在 / 非 0 字节 / 包可解析 / 表头回读一致
                string verify = VerifySaved(path, _headers);
                AddLog("💾 " + verify + (_thumbCol >= 0 ? "（含缩略图嵌入）" : ""));
                if (verify.StartsWith("❌"))
                {
                    MessageBox.Show(this, verify, "保存校验", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                AddLog("❌ 保存表格失败: " + ex.Message);
                MessageBox.Show(this, "保存表格失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private static string VerifySaved(string path, List<string> expectedHeaders)
        {
            if (!File.Exists(path)) return "❌ 校验失败：文件未生成（" + path + "）";
            long len = new FileInfo(path).Length;
            if (len <= 0) return "❌ 校验失败：文件为 0 字节（" + path + "）";
            try
            {
                List<string> back = XlsxTableService.LoadHeaders(path);
                if (back.Count != expectedHeaders.Count)
                    return "⚠️ 校验警告：写入 " + len + " 字节，但表头列数 " + back.Count + " 与预期 " + expectedHeaders.Count + " 不一致";
                return "✅ 保存成功：写入 " + len + " 字节，表头 " + back.Count + " 列已回读校验（" + path + "）";
            }
            catch (Exception ex)
            {
                return "❌ 校验失败：文件无法解析（" + ex.Message + "）";
            }
        }

        private void btnSaveLog_Click(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog
            {
                Title = "保存日志",
                Filter = "文本文件 (*.txt)|*.txt",
                FileName = "caxa3dTreeBom_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(sfd.FileName, txtLog.Text, System.Text.Encoding.UTF8);
                    AddLog("💾 日志已保存: " + sfd.FileName);
                }
                catch (Exception ex) { AddLog("❌ 保存日志失败: " + ex.Message); }
            }
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            txtLog.Clear();
        }

        // ==================================================================== 缩略图缓存

        private void ClearThumbs()
        {
            foreach (var img in _thumbImages.Values) img.Dispose();
            _thumbImages.Clear();
        }

        private void ReloadThumbs()
        {
            ClearThumbs();
            foreach (var r in _rows)
            {
                if (string.IsNullOrEmpty(r.ThumbnailPath) || !File.Exists(r.ThumbnailPath)) continue;
                if (_thumbImages.ContainsKey(r.ThumbnailPath)) continue;
                try
                {
                    using (var fs = File.Open(r.ThumbnailPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var tmp = Image.FromStream(fs))
                        _thumbImages[r.ThumbnailPath] = new Bitmap(tmp);
                }
                catch { /* 忽略单张失败 */ }
            }
            dgvTable.Invalidate();
        }

        private Image ThumbOfRow(int gridRowIndex)
        {
            int idx = gridRowIndex - 1;              // 第 1 行为表头
            if (idx < 0 || idx >= _rows.Count) return null;
            string p = _rows[idx].ThumbnailPath;
            if (string.IsNullOrEmpty(p)) return null;
            Image img;
            return _thumbImages.TryGetValue(p, out img) ? img : null;
        }

        // ==================================================================== 表格绘制与交互

        private void dgvTable_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            // 列头（Excel 字母）
            if (e.RowIndex < 0 && e.ColumnIndex >= 0)
            {
                e.PaintBackground(e.CellBounds, true);
                using (var brush = new SolidBrush(Color.FromArgb(51, 51, 51)))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (var font = new Font("Microsoft YaHei", 9F, FontStyle.Bold))
                {
                    e.Graphics.DrawString(e.Value == null ? "" : e.Value.ToString(), font, brush, e.CellBounds, sf);
                }
                e.Handled = true;
                return;
            }

            // 缩略图列自绘
            if (_thumbCol >= 0 && e.ColumnIndex == _thumbCol && e.RowIndex >= 1)
            {
                e.PaintBackground(e.CellBounds, true);
                Image img = ThumbOfRow(e.RowIndex);
                if (img != null)
                {
                    Rectangle r = e.CellBounds;
                    r.Inflate(-3, -3);
                    float scale = Math.Min((float)r.Width / img.Width, (float)r.Height / img.Height);
                    int w = Math.Max(1, (int)(img.Width * scale));
                    int h = Math.Max(1, (int)(img.Height * scale));
                    int x = r.X + (r.Width - w) / 2;
                    int y = r.Y + (r.Height - h) / 2;
                    var old = e.Graphics.InterpolationMode;
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.DrawImage(img, new Rectangle(x, y, w, h));
                    e.Graphics.InterpolationMode = old;
                }
                e.Handled = true;
            }
        }

        private void dgvTable_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_loading) return;
            if (e.RowIndex < 1 || e.ColumnIndex < 0) return;

            int idx = e.RowIndex - 1;
            if (idx < 0 || idx >= _rows.Count) return;
            if (e.ColumnIndex >= _headers.Count) return;

            string h = _headers[e.ColumnIndex].Trim();
            var cellValue = dgvTable.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            string v = cellValue == null ? "" : cellValue.ToString();

            switch (h)
            {
                case HC_Drawing: _rows[idx].HasDrawing = v.Contains("★"); break;
                case HC_Qty:
                    int q;
                    if (int.TryParse(v, out q)) _rows[idx].Quantity = q;
                    break;
                case HC_TotalQty: break;
                case HC_Level: break;
                case HC_Thumb: break;
                default: _rows[idx].Properties[h] = v; break;
            }
        }

        // ==================================================================== 右键菜单

        private void BuildContextMenus()
        {
            var miCopy = new ToolStripMenuItem("复制单元格");
            miCopy.Click += delegate { CopyCell(); };
            var miCopyRow = new ToolStripMenuItem("复制整行");
            miCopyRow.Click += delegate { CopyRow(); };
            cmsCell.Items.Add(miCopy);
            cmsCell.Items.Add(miCopyRow);

            var miInsertLeft = new ToolStripMenuItem("左插入列");
            miInsertLeft.Click += delegate { InsertColumn(_menuCol); };
            var miInsertRight = new ToolStripMenuItem("右插入列");
            miInsertRight.Click += delegate { InsertColumn(_menuCol + 1); };
            var miDelCol = new ToolStripMenuItem("删除列");
            miDelCol.Click += delegate { DeleteColumn(); };
            cmsColumnHeader.Items.Add(miInsertLeft);
            cmsColumnHeader.Items.Add(miInsertRight);
            cmsColumnHeader.Items.Add(miDelCol);
        }

        private void dgvTable_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (e.RowIndex < 1 || e.ColumnIndex < 0) return;
            _menuRow = e.RowIndex;
            _menuCol = e.ColumnIndex;
            cmsCell.Show(Cursor.Position);
        }

        private void dgvTable_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.ColumnIndex < 0) return;
            _menuCol = e.ColumnIndex;
            cmsColumnHeader.Show(Cursor.Position);
        }

        private void CopyCell()
        {
            if (_menuRow < 0 || _menuCol < 0) return;
            try
            {
                var v = dgvTable.Rows[_menuRow].Cells[_menuCol].Value;
                Clipboard.SetText(v == null ? "" : v.ToString());
                AddLog("📋 已复制单元格内容到剪贴板。");
            }
            catch { }
        }

        private void CopyRow()
        {
            if (_menuRow < 1) return;
            var parts = new List<string>();
            for (int c = 0; c < dgvTable.ColumnCount; c++)
            {
                var v = dgvTable.Rows[_menuRow].Cells[c].Value;
                parts.Add(v == null ? "" : v.ToString());
            }
            try
            {
                Clipboard.SetText(string.Join("\t", parts));
                AddLog("📋 已复制整行内容到剪贴板。");
            }
            catch { }
        }

        // ==================================================================== 插入列 / 删除列

        private void InsertColumn(int atIndex)
        {
            if (_headers.Count == 0) { AddLog("⚠️ 当前无表头，无法插入列。"); return; }
            if (_menuCol < 0) { AddLog("⚠️ 未定位到列，无法插入。"); return; }

            string def = NextNewColumnName();
            string name = AskText("插入列", "新列名称:", def);
            if (name == null) return;

            name = name.Trim();
            if (name.Length == 0) name = def;
            if (_headers.Any(h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                AddLog("⚠️ 列名 \"" + name + "\" 已存在，未插入。");
                return;
            }

            int idx = Math.Max(0, Math.Min(atIndex, _headers.Count));
            _headers.Insert(idx, name);

            _thumbCol = _headers.FindIndex(h => string.Equals(h.Trim(), HC_Thumb, StringComparison.Ordinal));

            DisplayTableData();
            AddLog("➕ 已在 " + ColIdxToExcel(_menuCol) + " 列" + (idx > _menuCol ? "右" : "左")
                 + "侧插入新列 \"" + name + "\"（现位于 " + ColIdxToExcel(idx) + "）。");
        }

        private string NextNewColumnName()
        {
            for (int i = 1; ; i++)
            {
                string n = "新列" + i;
                if (!_headers.Any(h => string.Equals(h.Trim(), n, StringComparison.OrdinalIgnoreCase))) return n;
            }
        }

        private string AskText(string title, string prompt, string defaultValue)
        {
            using (var form = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ClientSize = new Size(340, 130),
                Font = new Font("Microsoft YaHei", 9F)
            })
            {
                var lbl = new Label { Text = prompt, Location = new Point(18, 22), AutoSize = true };
                var txt = new TextBox { Location = new Point(18, 48), Size = new Size(300, 26), Text = defaultValue ?? "" };
                var ok = new Button { Text = "确定", Location = new Point(140, 88), Size = new Size(85, 30), DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", Location = new Point(233, 88), Size = new Size(85, 30), DialogResult = DialogResult.Cancel };

                form.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                return form.ShowDialog(this) == DialogResult.OK ? txt.Text : null;
            }
        }

        private void DeleteColumn()
        {
            if (_menuCol < 0 || _menuCol >= _headers.Count)
            {
                AddLog("⚠️ 未定位到列，无法删除。");
                return;
            }

            string name = _headers[_menuCol];
            string at = ColIdxToExcel(_menuCol);

            _headers.RemoveAt(_menuCol);
            foreach (var r in _rows) r.Properties.Remove(name);

            _thumbCol = _headers.FindIndex(h => string.Equals(h.Trim(), HC_Thumb, StringComparison.Ordinal));

            DisplayTableData();
            AddLog("🗑️ 已删除 " + at + " 列 \"" + name + "\"。");
        }

        // ==================================================================== 日志区高度拖拽

        private void resizeHandle_MouseDown(object sender, MouseEventArgs e)
        {
            isDragging = true;
            dragStartY = Cursor.Position.Y;
            dragStartHeight = grpLog.Height;
        }

        private void resizeHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging) return;
            int delta = Cursor.Position.Y - dragStartY;
            int newHeight = dragStartHeight - delta;
            int max = Math.Max(120, tableContainer.Height - 160);
            grpLog.Height = Math.Max(80, Math.Min(newHeight, max));
        }

        private void resizeHandle_MouseUp(object sender, MouseEventArgs e)
        {
            isDragging = false;
        }
    }
}
