// 主窗口：窗口前置 + 图纸环境预热 + 图号分离 + 日志 + 打开/关闭设计环境 + 打开工程图
//        + 小工具合集表格区（移植自 caxa3dminitool\MiniToolsForm.cs，2026-11-09 用户需求）
// 铁律：
//  - 所有按钮文字纯汉字，禁用 emoji / 符号 / 图标
//  - 构造期只搭界面，不连 COM、不探测、不开图纸
//  - 图纸环境预热判定只用窗口取证（DrawingProbe），后台线程探测
//  - 收尾不用裸 MessageBox（模态会禁用 CAXA 主界面）：状态文字 + 日志
//  - 日志按钮化：日志区默认隐藏，点「日志」展开，保存/清理随日志区显隐
//  - 长流程互斥走 FlowGate（进程级），「停止」只置标志不强行保存不关图
// 小工具表格区（与 caxa3dminitool 主窗口同款四列表格，单元格 = 一个工具）：
//  - 列 = 工具分类（1.特征与内核 / 2.元素处理 / 3.几何与坐标 / 4.数据与BOM），行 = 组内序号；
//  - 列宽按最长文字实测，不折行、不缩字号；悬停高亮；单击（或 Enter/Space）执行；
//  - 所有成功判定只看业务侧回读（OperationType / KernelType / IncludedInBOM 等是否真的变了）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal sealed class MainForm : Form
    {
        // 进程级互斥：同一 CAXA 进程内只允许一条长流程；FormClosing 置停
        private static volatile bool _running;
        private static volatile bool _stopRequested;

        internal static readonly string AppTitleBase = "按钮工具";

        private readonly IZBaseApp _siteApp;
        private DrawingNoSepForm _drawNoSepForm;
        private bool _topMost;
        private bool _busy;

        // 按钮
        private Button _btnTopMost;
        private Button _btnDrawNoSep;
        private Button _btnLog;
        private Button _btnSaveLog;
        private Button _btnClearLog;
        private Button _btnOpenEnv;
        private Button _btnCloseEnv;
        private Button _btnOpenDwg;
        private Button _btnCube;
        private Button _btnHideAllTypes;
        private Button _btnShowAllTypes;
        private Button _btnAssemblySort;
        private Button _btnStop;
        private CubeForm _cubeForm;
        private AssemblySortForm _assemblySortForm;

        private Label _lblStatus;
        private TextBox _txtLog;

        // ================================================================ 小工具表格区（caxa3dminitool 同款）
        //
        // 主体与主工具箱同款四列表格：单元格即一个工具，单击（或 Enter/Space）执行；
        // 列 = 工具分类，行 = 分组内序号，列宽按最长文字实测，不折行、不缩字号。

        private DataGridView _grid;
        private readonly Dictionary<int, ToolEntry> _cellTools = new Dictionary<int, ToolEntry>();
        private DataGridViewCell _hoverCell;

        private const int ColCount = 4;
        private const int CellPaddingX = 12;
        private const int RowHeight = 36;
        private const int HeaderHeight = 38;
        private const int MinColumnWidth = 170;
        private const int TableTop = 118;         // 表格区顶部（第三行按钮 80+28=108，留 10px）
        private int _tableHeight = HeaderHeight + 5 * RowHeight;   // 5 行（元素处理列最长）

        private static readonly string[] ColumnTitles =
        {
            "1. 特征与内核",
            "2. 元素处理",
            "3. 几何与坐标",
            "4. 数据与BOM"
        };

        private static readonly Color BtnAccent  = Color.FromArgb(0, 120, 212);
        private static readonly Color HeaderBack = Color.FromArgb(238, 243, 250);
        private static readonly Color HeaderFore = Color.FromArgb(0, 90, 158);
        private static readonly Color CellBack   = Color.White;
        private static readonly Color HoverBack  = Color.FromArgb(229, 241, 251);
        private static readonly Color GridLine   = Color.FromArgb(210, 210, 210);
        private static readonly Color CellFore   = Color.FromArgb(30, 30, 30);
        private static readonly Color EmptyCellBack = Color.FromArgb(248, 248, 248);

        private static readonly Font CellFont   = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
        private static readonly Font HeaderFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);

        /// <summary>单元格里的一个工具：标题 + 悬停说明 + 执行体。</summary>
        private sealed class ToolEntry
        {
            public string Title;
            public string Tip;
            public Action Run;
        }

        // 内核切换（定时器分片）
        private List<IZElement> _kernelQueue;
        private System.Windows.Forms.Timer _kernelTimer;
        private List<IZElement> _kernelChanged;
        private int _kernelIndex;
        private int _kernelTotal;
        private int _kernelOk, _kernelAlready, _kernelSkipped, _kernelFailed;
        private eZKernelType _kernelTarget;
        private bool _kernelStopRequested;
        private bool _kernelRunning;

        internal MainForm(IZBaseApp siteApp)
        {
            _siteApp = siteApp;
            Diagnostics.UiLogSink = AppendLog;
            BuildUi();
            if (!IsHandleCreated) CreateHandle();
            Diagnostics.Log("ui: 工具窗已创建");
        }

        // ↑ 键盘归属抢救（规则第 37 节）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ChordRescue.Install(this, msg => Diagnostics.Log(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑
        // 图纸环境预热已上收到集合体主界面（Host 统一预热铁律）：本子插件不再负责预热。

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _stopRequested = true;
            ChordRescue.Uninstall(this, "工具窗关闭");
            // 小工具收尾（caxa3dminitool 口径）：关窗即置停止标志，不强行保存文件、不强关图纸
            _kernelStopRequested = true;
            _kernelRunning = false;
            FlowGate.StopRequested = true;
            PopupAutoYes.FileDialogTarget = null;
            try { if (_kernelTimer != null) { _kernelTimer.Stop(); _kernelTimer.Dispose(); _kernelTimer = null; } } catch { }
            Diagnostics.UiLogSink = null;
            base.OnFormClosed(e);
        }

        // ================================================================ 界面

        private void BuildUi()
        {
            Text = AppTitleBase + " - " + BuildVersion();
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(680, 674);
            MinimumSize = new Size(696, 594);

            _topMost = true;
            TopMost = true;

            // 第一行：窗口前置 / 图纸环境 / 图号分离 / 日志（+保存/清理默认隐藏）
            _btnTopMost = MakeButton("窗口前置", 12, 12, 100);
            ApplyTopMostVisual(true);
            _btnTopMost.Click += delegate { ToggleTopMost(); };

            _btnDrawNoSep = MakeButton("图号分离", 122, 12, 110);
            _btnDrawNoSep.Click += OnDrawNoSepClick;

            _btnLog = MakeButton("日志", 242, 12, 80);
            _btnLog.Click += delegate { ToggleLog(); };

            _btnSaveLog = MakeButton("保存日志", 332, 12, 70);
            _btnSaveLog.Visible = false;
            _btnSaveLog.Click += OnSaveLogClick;

            _btnClearLog = MakeButton("清理日志", 412, 12, 70);
            _btnClearLog.Visible = false;
            _btnClearLog.Click += OnClearLogClick;

            // 第二行：打开设计环境 / 关闭设计环境 / 打开工程图 / 停止
            _btnOpenEnv = MakeButton("打开设计环境", 12, 46, 150);
            _btnOpenEnv.Click += OnOpenDesignEnvClick;

            _btnCloseEnv = MakeButton("关闭设计环境", 172, 46, 150);
            _btnCloseEnv.Click += OnCloseDesignEnvClick;

            _btnOpenDwg = MakeButton("打开工程图", 332, 46, 110);
            _btnOpenDwg.Click += OnOpenDwgClick;

            // 基体模型建立（移植自 caxa3dtracker）
            // 宽度按 6 个汉字留足（118 在部分 DPI 下会把末字「立」裁掉并与右侧「停止」贴边）
            _btnCube = MakeButton("基体模型建立", 452, 46, 132);
            _btnCube.Click += OnCubeClick;

            // 「停止」只在长流程期间可用（铁律 6：长等待链必须有停止入口）
            _btnStop = MakeButton("停止", 594, 46, 76);
            _btnStop.Enabled = false;
            _btnStop.BackColor = Color.FromArgb(200, 60, 60);
            _btnStop.ForeColor = Color.White;
            _btnStop.FlatStyle = FlatStyle.Flat;
            _btnStop.FlatAppearance.BorderSize = 0;
            _btnStop.Click += delegate { StopFlow(); };

            // 第三行：隐藏所有类型 / 显示所有类型（移植自 caxa3dtracker，走 IZSceneDocProperty 类别标志）
            _btnHideAllTypes = MakeButton("隐藏所有类型", 12, 80, 132);
            _btnHideAllTypes.Click += delegate { RunShowHideAll(false); };

            _btnShowAllTypes = MakeButton("显示所有类型", 154, 80, 132);
            _btnShowAllTypes.Click += delegate { RunShowHideAll(true); };

            // 装配体排序（移植自 caxa3dtracker）：弹条件窗，构造期不连 COM
            _btnAssemblySort = MakeButton("装配体排序", 296, 80, 110);
            _btnAssemblySort.Click += OnAssemblySortClick;

            // ---- 小工具表格区（四列，单元格 = 工具）----
            _grid = BuildGrid();
            _grid.Bounds = new Rectangle(12, TableTop, 656, _tableHeight);
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _lblStatus = new Label();
            _lblStatus.SetBounds(12, TableTop + _tableHeight + 8, 656, 20);
            _lblStatus.ForeColor = Color.DimGray;
            _lblStatus.Text = "就绪";
            _lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _txtLog = new TextBox();
            _txtLog.SetBounds(12, TableTop + _tableHeight + 32, 656, ClientSize.Height - (TableTop + _tableHeight + 32) - 8);
            _txtLog.Multiline = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.ReadOnly = true;
            _txtLog.WordWrap = false;
            _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _txtLog.BackColor = Color.White;
            _txtLog.Visible = false;

            Controls.AddRange(new Control[] {
                _btnTopMost, _btnDrawNoSep, _btnLog, _btnSaveLog, _btnClearLog,
                _btnOpenEnv, _btnCloseEnv, _btnOpenDwg, _btnCube, _btnStop,
                _btnHideAllTypes, _btnShowAllTypes, _btnAssemblySort,
                _grid, _lblStatus, _txtLog });

            FillTable();
            ApplyTableSize();

            // 内核切换：定时器分片处理，保证界面可响应、表格可用停止
            _kernelTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _kernelTimer.Tick += (s, e) => OnKernelTimerTick();
        }

        private static Button MakeButton(string text, int x, int y, int w)
        {
            return new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 28) };
        }

        private static string BuildVersion()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "" : ("v" + v.ToString(3));
            }
            catch { return ""; }
        }

        // ================================================================ 小工具表格

        private DataGridView BuildGrid()
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.None,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToOrderColumns = false,
                AllowUserToResizeColumns = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = HeaderHeight,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                CellBorderStyle = DataGridViewCellBorderStyle.Single,
                BorderStyle = BorderStyle.FixedSingle,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = false,
                EditMode = DataGridViewEditMode.EditProgrammatically,
                StandardTab = true,
                ShowCellToolTips = true,
                BackgroundColor = CellBack,
                GridColor = GridLine,
                EnableHeadersVisualStyles = false,
                Font = CellFont,
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = HeaderFont,
                    Alignment = DataGridViewContentAlignment.MiddleCenter,
                    BackColor = HeaderBack,
                    ForeColor = HeaderFore,
                    SelectionBackColor = HeaderBack,
                    SelectionForeColor = HeaderFore,
                    WrapMode = DataGridViewTriState.False
                },
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Font = CellFont,
                    WrapMode = DataGridViewTriState.False,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Padding = new Padding(CellPaddingX, 0, CellPaddingX, 0),
                    BackColor = CellBack,
                    ForeColor = CellFore,
                    SelectionBackColor = HoverBack,
                    SelectionForeColor = CellFore
                }
            };
            grid.RowTemplate.Height = RowHeight;

            for (int c = 0; c < ColumnTitles.Length; c++)
            {
                grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = ColumnTitles[c],
                    Name = "col" + c,
                    ReadOnly = true,
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                    MinimumWidth = MinColumnWidth
                });
            }

            grid.CellClick += Grid_CellClick;
            grid.CellMouseEnter += Grid_CellMouseEnter;
            grid.CellMouseLeave += Grid_CellMouseLeave;
            grid.KeyDown += Grid_KeyDown;
            return grid;
        }

        /// <summary>四列工具清单：列 = 分类，行 = 组内序号（与 caxa3dminitool 完全同款）。</summary>
        private List<ToolEntry>[] BuildToolGroups()
        {
            return new[]
            {
                // 1. 特征与内核
                new List<ToolEntry>
                {
                    new ToolEntry { Title = "增料除料切换", Tip = "选中特征后一键在增料/除料之间切换，并重建所属零件", Run = ToggleSelectedFeature },
                    new ToolEntry { Title = "切换Parasolid内核", Tip = "把选中的零件/整棵树切到 Parasolid 内核", Run = () => StartKernelSwitch(eZKernelType.Z_KERNEL_PARASOLID) },
                    new ToolEntry { Title = "切换ACIS内核", Tip = "把选中的零件/整棵树切到 ACIS 内核", Run = () => StartKernelSwitch(eZKernelType.Z_KERNEL_ACIS) }
                },
                // 2. 元素处理
                new List<ToolEntry>
                {
                    new ToolEntry { Title = "选中取消压缩", Tip = "按装配体穿透规则抓零部件，逐个解除压缩并回读确认", Run = UnsuppressSelectedElements },
                    new ToolEntry { Title = "更新包围盒", Tip = "对选中元素（无选中则全树）重置 IZSizeBox", Run = ResetSizeBoxes },
                    new ToolEntry { Title = "全选内部链接零件", Tip = "扫描树中内部链接零件并加入选区", Run = SelectInternalLinkedParts },
                    new ToolEntry { Title = "选择相同零部件", Tip = "以选中件的零件名为种子，穿透全树把同名零部件全部选回", Run = SelectSameNameParts },
                    new ToolEntry { Title = "零件置换", Tip = "先选中A（提供模型）再选中B，驱动原生「替换零件/装配」把B的外链换成A正在用的ics；两件都必须带外链", Run = ReplaceSwapBySelection }
                },
                // 3. 几何与坐标
                new List<ToolEntry>
                {
                    new ToolEntry { Title = "锚点移至重心", Tip = "把定位锚移到零件重心，几何不动", Run = MoveAnchorToCog },
                    new ToolEntry { Title = "获取点坐标", Tip = "读取选中点的全局/体坐标（毫米），首个点复制到剪贴板", Run = ShowPickedPointCoordinates }
                },
                // 4. 数据与BOM
                new List<ToolEntry>
                {
                    new ToolEntry { Title = "导入3D曲线坐标数据", Tip = "选 txt（每行 X Y Z 毫米）生成插值样条", Run = ImportWireFromTxt },
                    new ToolEntry { Title = "将零部件添加到BOM", Tip = "把选中零部件全部纳入 BOM 并回读 IncludedInBOM", Run = AddSelectedToBom },
                    new ToolEntry { Title = "删除所有布局(除模型)", Tip = "一键静默：识别图纸环境 → 桥握手 → 扫描 → 直接删除模型以外的全部布局（需先进入图纸环境；不可撤销）", Run = DeleteAllLayoutsExceptModel },
                    new ToolEntry { Title = "批量格式转换", Tip = "次级界面：exb→dxf/dwg/pdf（图纸环境代点）；ics第一个对象→x_t/stp/sat（RCW 直调）", Run = OnBatchConvertClick }
                }
            };
        }

        /// <summary>把工具按分类填入四列表格；空单元格留白并置灰底。</summary>
        private void FillTable()
        {
            List<ToolEntry>[] groups = BuildToolGroups();
            int rowCount = 1;
            foreach (List<ToolEntry> list in groups)
                rowCount = Math.Max(rowCount, list.Count);

            for (int r = 0; r < rowCount; r++)
                _grid.Rows.Add();

            for (int c = 0; c < ColCount && c < groups.Length; c++)
            {
                List<ToolEntry> list = groups[c];
                for (int r = 0; r < rowCount; r++)
                {
                    DataGridViewCell cell = _grid.Rows[r].Cells[c];
                    if (r < list.Count)
                    {
                        ToolEntry tool = list[r];
                        cell.Value = tool.Title;
                        cell.ToolTipText = tool.Tip;
                        _cellTools[r * ColCount + c] = tool;
                    }
                    else
                    {
                        cell.Value = string.Empty;
                        cell.Style.BackColor = EmptyCellBack;
                        cell.Style.SelectionBackColor = EmptyCellBack;
                    }
                }
            }
            _tableHeight = HeaderHeight + rowCount * RowHeight;
            _grid.Height = _tableHeight;
        }

        /// <summary>按实测文字宽度定列最小宽与窗口尺寸：文字不折行、不缩字号、不被遮挡。</summary>
        private void ApplyTableSize()
        {
            int totalMin = 0;
            for (int c = 0; c < ColCount; c++)
            {
                int need = TextRenderer.MeasureText(ColumnTitles[c], HeaderFont).Width + CellPaddingX * 2 + 24;
                foreach (KeyValuePair<int, ToolEntry> pair in _cellTools)
                {
                    if (pair.Key % ColCount != c) continue;
                    int w = TextRenderer.MeasureText(pair.Value.Title, CellFont).Width + CellPaddingX * 2 + 24;
                    if (w > need) need = w;
                }
                if (need < MinColumnWidth) need = MinColumnWidth;
                _grid.Columns[c].MinimumWidth = need;
                totalMin += need;
            }

            int clientWidth = Math.Max(680, totalMin + 48);
            ClientSize = new Size(clientWidth, 674);
            MinimumSize = new Size(clientWidth + 16, 594);
        }

        private void Grid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            RunCell(e.RowIndex, e.ColumnIndex);
        }

        private void Grid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space) return;
            DataGridViewCell cell = _grid.CurrentCell;
            if (cell == null) return;
            RunCell(cell.RowIndex, cell.ColumnIndex);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void Grid_CellMouseEnter(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (!_cellTools.ContainsKey(e.RowIndex * ColCount + e.ColumnIndex)) return;
            _hoverCell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            _hoverCell.Style.BackColor = HoverBack;
            _grid.Cursor = Cursors.Hand;
        }

        private void Grid_CellMouseLeave(object sender, DataGridViewCellEventArgs e)
        {
            if (_hoverCell != null)
            {
                _hoverCell.Style.BackColor = Color.Empty;
                _hoverCell = null;
            }
            _grid.Cursor = Cursors.Default;
        }

        /// <summary>执行单元格对应的工具：内核切换进行中只提示、不重入。</summary>
        private void RunCell(int row, int column)
        {
            ToolEntry tool;
            if (!_cellTools.TryGetValue(row * ColCount + column, out tool)) return;

            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。（" + tool.Title + "）");
                return;
            }

            try
            {
                Diagnostics.Log("---- 执行：" + tool.Title + " ----");
                tool.Run();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("执行 " + tool.Title + " 失败：" + ex);
            }
        }

        /// <summary>长任务期间禁用表格，避免重复点击（内核切换由分片定时器输出，不受影响）。</summary>
        private void SetToolsEnabled(bool enabled)
        {
            if (_grid != null && !_grid.IsDisposed) _grid.Enabled = enabled;
            if (_grid != null) _grid.Cursor = enabled ? Cursors.Default : Cursors.WaitCursor;
        }

        // ================================================================ 导入3D曲线坐标数据

        /// <summary>导入3D曲线坐标数据：选 txt → 解析每行 x y z（毫米）→ IZWire 生成插值样条。</summary>
        private void ImportWireFromTxt()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            string path = null;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择坐标数据文件（每行 X Y Z，单位毫米）";
                dlg.Filter = "坐标文本 (*.txt;*.dat;*.csv)|*.txt;*.dat;*.csv|所有文件 (*.*)|*.*";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }

            try
            {
                string error, detail;
                int pointCount, crvId;
                CurveTool.ImportWireFromText(_siteApp, path, out error, out pointCount, out crvId, out detail);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[导入3D曲线] " + error);
                    return;
                }

                Diagnostics.Log("导入3D曲线成功：" + detail);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("导入3D曲线异常：" + ex);
            }
        }

        // ================================================================ 锚点移至重心

        /// <summary>
        /// 取选中零件的重心（GetMassProperties）→ 把定位锚移到重心（不动几何、不动三维球）。
        /// 判定：锚位置≈重心 且 几何位移≈0。
        /// </summary>
        private void MoveAnchorToCog()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            SetToolsEnabled(false);
            try
            {
                string error, summary;
                List<AnchorTool.Outcome> list = AnchorTool.MoveAnchorToCog(_siteApp, out error, out summary);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[锚点移至重心] " + error);
                    return;
                }

                foreach (AnchorTool.Outcome o in list)
                {
                    if (o.Ok)
                    {
                        Diagnostics.Log(string.Format(
                            "[成功] {0}（{1}）：重心 ({2:F3}, {3:F3}, {4:F3}) mm，锚已移到重心，几何位移 {5:F3} mm",
                            o.Name, o.Kind,
                            o.Cog[0] * 1000.0, o.Cog[1] * 1000.0, o.Cog[2] * 1000.0,
                            o.GeometryShift * 1000.0));
                    }
                    else
                    {
                        string note = string.IsNullOrEmpty(o.Note) ? "锚未能移到重心" : o.Note;
                        Diagnostics.Log(string.Format("[失败] {0}（{1}）：{2}", o.Name, o.Kind, note.TrimEnd('；', ' ')));
                    }
                }

                Diagnostics.Log("锚点移至重心结束：" + summary);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("锚点移至重心异常：" + ex);
            }
            finally
            {
                SetToolsEnabled(true);
            }
        }

        // ================================================================ 将零部件添加到BOM

        /// <summary>
        /// 选中零部件全部纳入 BOM（IncludedInBOM = true），回读确认后输出日志。
        /// </summary>
        private void AddSelectedToBom()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            SetToolsEnabled(false);
            try
            {
                string error, summary;
                List<BomTool.Outcome> list = BomTool.AddSelectedToBom(_siteApp, out error, out summary);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[将零部件添加到BOM] " + error);
                    return;
                }

                foreach (BomTool.Outcome o in list)
                {
                    if (o.Ok && o.WasIncluded)
                        Diagnostics.Log(string.Format("[已是] {0}（{1}）：本就已纳入 BOM", o.Name, o.Kind));
                    else if (o.Ok)
                        Diagnostics.Log(string.Format("[成功] {0}（{1}）：已纳入 BOM（回读 IncludedInBOM=true）", o.Name, o.Kind));
                    else
                        Diagnostics.Log(string.Format("[失败] {0}：{1}", o.Name, (o.Note ?? "未知原因").TrimEnd('；', ' ')));
                }

                Diagnostics.Log("将零部件添加到BOM结束：" + summary);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("将零部件添加到BOM异常：" + ex);
            }
            finally
            {
                SetToolsEnabled(true);
            }
        }

        // ================================================================ 获取点坐标

        /// <summary>
        /// 读取选中点的坐标（IZVertex.Position + GetTransformToGlobal 换算全局），
        /// 日志显示毫米坐标，并把第一个成功点的全局坐标复制到剪贴板（x,y,z）。
        /// </summary>
        private void ShowPickedPointCoordinates()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            SetToolsEnabled(false);
            try
            {
                string error, summary;
                List<PointTool.Outcome> list = PointTool.GetPickedPointCoordinates(_siteApp, out error, out summary);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[获取点坐标] " + error);
                    return;
                }

                string firstGlobal = null;
                foreach (PointTool.Outcome o in list)
                {
                    if (o.Ok)
                    {
                        string line = string.Format("[点] {0}：全局 ({1:F3}, {2:F3}, {3:F3}) mm",
                            o.Name, o.Global[0] * 1000.0, o.Global[1] * 1000.0, o.Global[2] * 1000.0);
                        if (o.Local != null)
                            line += string.Format("；体坐标 ({0:F3}, {1:F3}, {2:F3}) mm",
                                o.Local[0] * 1000.0, o.Local[1] * 1000.0, o.Local[2] * 1000.0);
                        line += "（来源：" + o.Source + "）";
                        Diagnostics.Log(line);

                        if (firstGlobal == null)
                            firstGlobal = string.Format("{0:F3},{1:F3},{2:F3}",
                                o.Global[0] * 1000.0, o.Global[1] * 1000.0, o.Global[2] * 1000.0);
                    }
                    else
                    {
                        Diagnostics.Log("[失败] " + o.Name + "：" + (o.Note ?? "未知原因").TrimEnd('；', ' '));
                    }
                }

                if (firstGlobal != null)
                {
                    try
                    {
                        Clipboard.SetText(firstGlobal);
                        Diagnostics.Log("第一个点的全局坐标已复制到剪贴板：" + firstGlobal);
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Trace("剪贴板复制失败：" + ex.Message);
                    }
                }

                Diagnostics.Log("获取点坐标结束：" + summary);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("获取点坐标异常：" + ex);
            }
            finally
            {
                SetToolsEnabled(true);
            }
        }

        // ================================================================ 增料 / 除料切换

        private void ToggleSelectedFeature()
        {
            SetToolsEnabled(false);
            try
            {
                string error;
                List<FeatureTool.Outcome> outcomes = FeatureTool.ToggleSelected(_siteApp, out error);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[未执行] " + error);
                    return;
                }

                int ok = 0, skipped = 0;
                foreach (FeatureTool.Outcome o in outcomes)
                {
                    if (o.Failed)
                    {
                        skipped++;
                        Diagnostics.Log(string.Format("[跳过] {0}：{1}", o.FeatureName, o.Note));
                        continue;
                    }

                    if (o.Changed)
                    {
                        ok++;
                        Diagnostics.Log(string.Format("[成功] {0} / 特征 {1}：{2}({3}) → {4}({5})，重建后回读一致",
                            string.IsNullOrEmpty(o.PartName) ? "?" : o.PartName, o.FeatureName,
                            FeatureTool.OperationText(o.Before), (int)o.Before,
                            FeatureTool.OperationText(o.After), (int)o.After));
                    }
                    else
                    {
                        skipped++;
                        Diagnostics.Log(string.Format("[未生效] {0} / 特征 {1}：{2} → 期望 {3}，重建后回读 {4}",
                            string.IsNullOrEmpty(o.PartName) ? "?" : o.PartName, o.FeatureName,
                            FeatureTool.OperationText(o.Before),
                            FeatureTool.OperationText(o.Expected),
                            FeatureTool.OperationText(o.After)) +
                            (string.IsNullOrEmpty(o.Note) ? "" : "；" + o.Note));
                    }
                }

                Diagnostics.Log(string.Format("增料除料切换结束：成功 {0} 个，跳过/未生效 {1} 个。", ok, skipped));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("增料除料切换异常：" + ex);
            }
            finally
            {
                SetToolsEnabled(true);
            }
        }

        // ================================================================ 内核切换（Parasolid / ACIS）

        private void StartKernelSwitch(eZKernelType target)
        {
            if (_kernelRunning) return;

            string error, summary;
            List<IZElement> leaves = KernelTool.GatherLeafParts(_siteApp, out error, out summary);

            if (!string.IsNullOrEmpty(error))
            {
                Diagnostics.Log("[未执行] " + error);
                return;
            }

            Diagnostics.Log(string.Format("开始切换内核 → {0}：{1}",
                KernelTool.KernelText(target), summary));

            _kernelQueue = leaves;
            _kernelChanged = new List<IZElement>();
            _kernelIndex = 0;
            _kernelTotal = leaves.Count;
            _kernelOk = 0; _kernelAlready = 0; _kernelSkipped = 0; _kernelFailed = 0;
            _kernelTarget = target;
            _kernelStopRequested = false;
            _kernelRunning = true;

            SetToolsEnabled(false);

            _kernelTimer.Start();
        }

        /// <summary>定时器分片：每 tick 处理一小批，界面保持可响应。</summary>
        private void OnKernelTimerTick()
        {
            if (!_kernelRunning)
            {
                _kernelTimer.Stop();
                return;
            }

            int processed = 0;
            while (_kernelIndex < _kernelTotal && processed < 25 && !_kernelStopRequested)
            {
                IZElement elem = _kernelQueue[_kernelIndex];
                _kernelIndex++;
                processed++;
                ApplyOneKernel(elem);
            }

            if (_kernelStopRequested || _kernelIndex >= _kernelTotal)
                FinishKernelSwitch(_kernelStopRequested);
        }

        private void ApplyOneKernel(IZElement elem)
        {
            eZKernelType before, after;
            string note;
            KernelTool.ApplyStatus st = KernelTool.ApplyKernel(elem, _kernelTarget, out before, out after, out note);
            string name = SafeName(elem);

            switch (st)
            {
                case KernelTool.ApplyStatus.Already:
                    _kernelAlready++;
                    Diagnostics.Log(string.Format("[已是] {0}：本来就是 {1}，跳过",
                        name, KernelTool.KernelText(before)));
                    break;

                case KernelTool.ApplyStatus.Changed:
                    _kernelOk++;
                    _kernelChanged.Add(elem);
                    Diagnostics.Log(string.Format("[成功] {0}：{1} → {2}（写入后回读一致）",
                        name, KernelTool.KernelText(before), KernelTool.KernelText(after)));
                    break;

                case KernelTool.ApplyStatus.NotPart:
                    _kernelSkipped++;
                    Diagnostics.Log(string.Format("[跳过] {0}：{1}", name, note));
                    break;

                default:
                    _kernelFailed++;
                    Diagnostics.Log(string.Format("[失败] {0}：{1} → 期望 {2}，实际 {3}{4}",
                        name, KernelTool.KernelText(before), KernelTool.KernelText(_kernelTarget),
                        KernelTool.KernelText(after),
                        string.IsNullOrEmpty(note) ? "" : "；" + note));
                    break;
            }
        }

        /// <summary>
        /// 收尾：统一触发一次场景重建（官方样例口径：全部写完再 Update），
        /// 然后对已切换零件逐一回读 KernelType 做权威判定。
        /// </summary>
        private void FinishKernelSwitch(bool stopped)
        {
            _kernelRunning = false;
            _kernelTimer.Stop();
            SetToolsEnabled(true);

            int confirmed = 0, mismatch = 0;
            try
            {
                if (_kernelChanged.Count > 0)
                {
                    Application.DoEvents();

                    IZSceneDoc scene = FeatureTool.GetSceneDoc(_siteApp);
                    if (scene != null)
                    {
                        try { scene.Update(); }
                        catch (Exception ex) { Diagnostics.Log("场景重建失败：" + ex.Message); }
                    }

                    foreach (IZElement elem in _kernelChanged)
                    {
                        try
                        {
                            IZPart part = elem as IZPart;
                            if (part != null && part.KernelType == _kernelTarget) confirmed++;
                            else mismatch++;
                        }
                        catch { mismatch++; }
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("内核切换收尾异常：" + ex.Message);
            }

            string head = stopped ? "已停止" : "切换完成";
            string body = string.Format("（目标 {0}）已切换 {1}，本就同内核 {2}，跳过 {3}，失败 {4}；重建后回读一致 {5}，不一致 {6}",
                KernelTool.KernelText(_kernelTarget), _kernelOk, _kernelAlready, _kernelSkipped, _kernelFailed,
                confirmed, mismatch);

            Diagnostics.Log(head + body);
        }

        private static string SafeName(IZElement elem)
        {
            if (elem == null) return "?";
            try { return string.IsNullOrEmpty(elem.Name) ? "?" : elem.Name; } catch { return "?"; }
        }

        // ================================================================ 元素工具（压缩 / 包围盒 / 内部链接 / 同名）

        /// <summary>
        /// 选中取消压缩：按切换内核的装配体穿透规则抓零部件（无选中则穿透整棵树），
        /// 逐个解除压缩，回读 Z_SUPPRESSED 判定。
        /// </summary>
        private void UnsuppressSelectedElements()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            try
            {
                string error, summary;
                int total, unsuppressed, already, failed, verified;
                ElementsTool.UnsuppressSelected(_siteApp, out error, out summary,
                    out total, out unsuppressed, out already, out failed, out verified);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[选中取消压缩] " + error);
                    return;
                }

                Diagnostics.Log(string.Format(
                    "选中取消压缩：{0}；解除 {1}，本就未压缩 {2}，失败 {3}；回读已解除 {4}",
                    summary, unsuppressed, already, failed, verified));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("选中取消压缩异常：" + ex);
            }
        }

        /// <summary>更新包围盒：对选中元素（无选中则全树）QI 到 IZSizeBox 后 Reset。</summary>
        private void ResetSizeBoxes()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            try
            {
                string error;
                int reset, failed, noBox, verified;
                ElementsTool.ResetSizeBoxes(_siteApp, out error, out reset, out failed, out noBox, out verified);
                if (!string.IsNullOrEmpty(error)) Diagnostics.Log("[更新包围盒] " + error);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("更新包围盒异常：" + ex);
            }
        }

        /// <summary>全选内部链接零件：有选中则以选中为种子，否则扫全树。</summary>
        private void SelectInternalLinkedParts()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            try
            {
                string error;
                int selectedAfter;
                int total = ElementsTool.SelectInternalLinked(_siteApp, out error, out selectedAfter);
                if (!string.IsNullOrEmpty(error)) Diagnostics.Log("[全选内部链接零件] " + error);
                if (total > 0)
                    Diagnostics.Log(string.Format("全选内部链接零件：命中 {0} 个，选区回读 {1} 个", total, selectedAfter));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("全选内部链接零件异常：" + ex);
            }
        }

        /// <summary>
        /// 选择相同零部件：以当前选中件的名字为种子，穿透整棵设计树把同名零部件全部选回。
        /// 判据 = 选区回读命中数（不看 API 返回值）。
        /// </summary>
        private void SelectSameNameParts()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            try
            {
                string error, summary;
                int selectedAfter;
                ElementsTool.SelectSameNameComponents(_siteApp, out error, out summary, out selectedAfter);
                if (!string.IsNullOrEmpty(error)) Diagnostics.Log("[选择相同零部件] " + error);
                if (!string.IsNullOrEmpty(summary)) Diagnostics.Log("选择相同零部件：" + summary);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("选择相同零部件异常：" + ex);
            }
        }

        // ================================================================ 零件置换

        /// <summary>
        /// 「零件置换」：读活选择里的两个零部件 —— 第一个＝A（提供模型）、第二个＝B（要被置换掉），
        /// 确认后驱动 CAXA 原生「替换零件/装配」面板，把 B 的外链换成 A 正在用的那个 ics。
        /// 两个件都必须带外链，虚拟件由 ReplaceTool.Validate 当场拒绝（用户 2026-11-03 定案）。
        /// ★ 选中顺序是否等于用户点选顺序，ICAPI 没文档、本机也没实测 ⇒
        ///   把两个件的分配摊开让用户核对，点「否」即交换 A/B，不默认顺序一定对。
        /// </summary>
        private void ReplaceSwapBySelection()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }
            if (FlowGate.Busy)
            {
                Diagnostics.Log("[未执行] 已有流程在运行，请等它结束或点「停止」。");
                return;
            }

            string error;
            List<ReplacePick> picks = ReplaceTool.ReadSelectedComponents(_siteApp, out error);
            if (picks.Count != 2)
            {
                Diagnostics.Log("[零件置换] " + error);
                MessageBox.Show(error, AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ReplacePick pickA = picks[0];       // 先选中的：提供模型，它的外链 ics 是要填进文件框的目标
            ReplacePick pickB = picks[1];       // 后选中的：要被置换掉的那一个

            while (true)
            {
                string why = ReplaceTool.Validate(pickA, pickB);
                if (why != null)
                {
                    Diagnostics.Log("[零件置换] " + why);
                    MessageBox.Show(why, AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string msg = "A（先选中，提供模型）：" + pickA.Describe + "\n" +
                             "B（后选中，被置换掉）：" + pickB.Describe + "\n\n" +
                             "把 B 的外链换成 A 正在用的文件 " + Path.GetFileName(pickA.LinkPath) + "？\n\n" +
                             "　是　按上面分配执行\n　否　交换 A/B\n　取消　什么都不做";
                DialogResult dr = MessageBox.Show(msg, AppTitleBase, MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
                if (dr == DialogResult.Cancel)
                {
                    Diagnostics.Log("[零件置换] 用户取消");
                    return;
                }
                if (dr == DialogResult.Yes) break;

                ReplacePick t = pickA;          // 「否」＝交换 A/B，换完再校验一轮
                pickA = pickB;
                pickB = t;
            }

            Diagnostics.Log("零件置换 选中：" + ReplaceTool.DescribePicks(picks));
            RunLongFlow("零件置换：" + pickB.Name + " ← " + Path.GetFileName(pickA.LinkPath),
                delegate { ReplaceTool.Execute(_siteApp, pickA, pickB, msg => Diagnostics.Log(msg)); });
        }

        // ================================================================ 批量格式转换

        /// <summary>
        /// 打开「批量格式转换」次级界面（DlgBatchConvert，模态）。
        /// 移植自 caxa3dexbtracker 的成熟方案：
        ///   exb → dxf/dwg/pdf（图纸环境代点：CRX 桥 + 另存文件框 + 打印对话框）；
        ///   ics 第一个对象 → x_t/stp/sat（纯 RCW 直调：IZAssembly::Export / IZPart::Export）。
        /// </summary>
        private void OnBatchConvertClick()
        {
            if (FlowGate.Busy)
            {
                SetStatus("已有流程在运行，请等它结束或点「停止」。", Color.DimGray);
                Diagnostics.Log("[未执行] 批量格式转换：已有流程在运行");
                return;
            }
            try
            {
                SetStatus("打开批量格式转换界面 …", Color.DimGray);
                Diagnostics.Log("bc: 打开批量格式转换界面");
                using (var dlg = new DlgBatchConvert(_siteApp))
                {
                    dlg.TopMost = TopMost;
                    dlg.ShowDialog(this);
                }
                Diagnostics.Log("bc: 批量格式转换界面已关闭");
            }
            catch (Exception ex)
            {
                SetStatus("批量格式转换界面异常：" + ex.Message, Color.Firebrick);
                Diagnostics.Log("批量格式转换界面异常：" + ex);
            }
        }

        // ================================================================ 删除所有布局（除模型）
        //
        // 口径与 caxa3dminitool 完全一致：
        //   ① 识别前台图纸环境（DrawingProbeLite，纯窗口取证，不碰 COM/CRX）
        //   ② 图纸命令桥就绪（marker 时间 ≥ 进程启动 + PING 令牌握手）
        //   ③ 扫描布局（CRX 枚举 PaperTable，只读；模型项两侧各过滤一遍）
        //   ④ 静默删除（**无确认框**；CRX 侧仍校验 confirm=yes + 会话令牌 + 命令白名单）
        //
        // 权责边界：本进程只发指令，图纸数据库由图纸环境里的 caxa3dminitool.crx 改
        // （%TEMP% 桥文件前缀 caxa3dminitool_sub_* 与已部署 CRX 一致，两侧必须同源）。

        private void DeleteAllLayoutsExceptModel()
        {
            RunLongFlow("删除所有布局(除模型)", delegate { RunDeleteLayoutsSteps(); });
        }

        /// <summary>串行四步（UI 线程，可被「停止」中断）。任一步失败即中止并写明原因。</summary>
        private void RunDeleteLayoutsSteps()
        {
            // ── ① 识别前台图纸环境 ──
            DrawingProbeResult probe = DrawingProbeLite.Describe();
            foreach (string line in probe.Lines) Diagnostics.Log(line);
            if (!probe.Found)
            {
                Diagnostics.Log("① 未进入图纸环境 —— " + probe.Summary);
                Diagnostics.Log("删除布局：中止。请先在 CAXA 里打开一张图纸（进入图纸环境）后重试。");
                return;
            }
            Diagnostics.Log("① 前台图纸：" + probe.DocName +
                            (probe.DraftCount > 1
                                ? "（共 " + probe.DraftCount + " 张图纸打开，CRX 作用于宿主的活动文档）"
                                : ""));

            // ── ② 图纸命令桥就绪 ──
            if (!LayoutBridge.IsCrxLoaded())
            {
                string why = File.Exists(LayoutBridge.MarkerPath)
                    ? "marker 存在但早于本进程启动（上次会话遗留，不可信）"
                    : "marker 不存在";
                Diagnostics.Log("② 图纸命令桥未就绪（" + why + "）—— " + LayoutBridge.LoadHint);
                return;
            }
            BridgeResponse pong = LayoutBridge.Handshake();
            if (pong == null || pong.Status != "PONG")
            {
                Diagnostics.Log("② 桥握手失败：" + DescribeBridge(pong));
                return;
            }
            Diagnostics.Log("② 图纸命令桥已就绪（令牌 " + pong.Token + "）");

            // ── ③ 扫描布局 ──
            Diagnostics.Log("③ 扫描图纸布局 …（等待 CRX 回包）");
            BridgeResponse scan = LayoutBridge.RequestScan();
            if (scan == null || scan.Status != "SCAN")
            {
                Diagnostics.Log("③ 扫描失败：" + DescribeBridge(scan));
                return;
            }

            // 双保险：CRX 已过滤一次，C# 再按同一判据过一遍（防协议/版本错配）
            List<string> targets = new List<string>();
            int filtered = 0;
            foreach (string p in scan.Papers)
            {
                if (LayoutBridge.IsModelLayoutName(p)) { filtered++; continue; }
                targets.Add(p);
            }
            if (filtered > 0)
                Diagnostics.Log("③ CRX 回包含 " + filtered + " 个模型项，已剔除");

            string model = string.IsNullOrEmpty(scan.Model) ? "model" : scan.Model;
            if (targets.Count == 0)
            {
                Diagnostics.Log("③ 除模型（" + model + "）外没有其它布局，无需删除");
                return;
            }
            Diagnostics.Log("③ 扫描到 " + targets.Count + " 个非模型布局：" +
                            string.Join("、", targets.ToArray()) + "（模型=" + model + "）");

            // ── ④ 静默删除（不弹任何确认框）──
            Diagnostics.Log("④ 删除 " + targets.Count + " 个布局 中…（静默执行，模型「" + model +
                            "」保留；等待 CRX 回包）");
            BridgeResponse del = LayoutBridge.RequestDelete();
            if (del == null)
            {
                Diagnostics.Log("④ 删除失败：无响应（空结果）");
                return;
            }
            if (del.Status == "OK")
            {
                Diagnostics.Log("④ 完成：已删除 " + del.Deleted + " 个布局" +
                                (string.IsNullOrEmpty(del.Message) ? "" : "（" + del.Message + "）"));
            }
            else
            {
                Diagnostics.Log("④ 删除未全部成功：" + DescribeBridge(del));
            }
        }

        private static string DescribeBridge(BridgeResponse r)
        {
            if (r == null) return "无响应（空结果）";
            string msg = string.IsNullOrEmpty(r.Message) ? r.Status : r.Message;
            return r.Status == "TIMEOUT" ? "超时：" + msg : msg;
        }

        // ================================================================ 长流程骨架（铁律 6：进程级互斥 + 停止入口）

        /// <summary>
        /// 长流程外壳：独占闸门 → 禁表格、开代点、跑 body → 收尾一律解除武装。
        /// 结果只写日志区，不弹模态框：流程跑在 CAXA 主线程上，裸 MessageBox 会把 CAXA 主界面
        /// 和本插件窗口一起禁用（caxa3ddiffrename 实机 18:42 那轮的直接原因）。
        /// </summary>
        private void RunLongFlow(string flowName, Action body)
        {
            if (!FlowGate.TryAcquire(AppTitleBase, flowName))
            {
                MessageBox.Show("已有流程在运行，请等它结束或点「停止」。", AppTitleBase);
                return;
            }

            SetToolsEnabled(false);
            _btnStop.Enabled = true;
            // PopupAutoYes 现已改为借还模式（PopupAutoYes.Borrow/归还），不再允许直接 Enabled=true/false；
            // 具体需要代点的子步骤自行 Borrow，不在 RunLongFlow 入口全局开关。
            Cursor = Cursors.WaitCursor;
            Diagnostics.Log("========== " + flowName + " 开始 ==========");

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
                // PopupAutoYes.Enabled 只读（借还模式），不再全局关；FileDialogTarget/WatchLatePopups 由借还系统自动管理。
                _btnStop.Enabled = false;
                SetToolsEnabled(true);
                FlowGate.Release();
                Cursor = Cursors.Default;
            }

            if (stopped)
                Diagnostics.Log("已停止：" + flowName +
                    "（已经生效的代点不会自动回退，请在 CAXA 里检查场景当前状态）");
            else if (error != null)
                Diagnostics.Log("失败：" + error + "\n（正文与取证块在跟踪日志 " + Diagnostics.LogPath + "）");
            else
                Diagnostics.Log("完成：" + flowName);
        }

        /// <summary>「停止」：只置标志，不强行保存、不关图。</summary>
        private void StopFlow()
        {
            if (!FlowGate.IsBusyBy(AppTitleBase)) return;
            FlowGate.StopRequested = true;
            Diagnostics.Log("已请求停止，正在中止当前流程…（当前这一步走完即恢复界面）");
        }

        // ================================================================ 窗口前置

        private void ToggleTopMost()
        {
            _topMost = !_topMost;
            TopMost = _topMost;
            ApplyTopMostVisual(_topMost);
            SetStatus(_topMost ? "窗口已锁定在最上层" : "窗口已取消最上层", Color.DimGray);
            Diagnostics.Log(_topMost ? "窗口已锁定在最上层" : "窗口已取消最上层");
        }

        private void ApplyTopMostVisual(bool on)
        {
            _btnTopMost.BackColor = on ? Color.FromArgb(0, 120, 212) : SystemColors.Control;
            _btnTopMost.ForeColor = on ? Color.White : SystemColors.ControlText;
            _btnTopMost.FlatStyle = FlatStyle.Flat;
            _btnTopMost.FlatAppearance.BorderSize = on ? 0 : 1;
        }

        private void SetStatus(string text, Color color)
        {
            if (_lblStatus.IsDisposed) return;
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        // ================================================================ 日志

        private void ToggleLog()
        {
            bool show = !_txtLog.Visible;
            _txtLog.Visible = show;
            _btnSaveLog.Visible = show;
            _btnClearLog.Visible = show;
            _btnLog.BackColor = show ? Color.FromArgb(0, 120, 212) : SystemColors.Control;
            _btnLog.ForeColor = show ? Color.White : SystemColors.ControlText;
            _btnLog.FlatStyle = FlatStyle.Flat;
            _btnLog.FlatAppearance.BorderSize = show ? 0 : 1;
            Diagnostics.Log(show ? "日志区已展开" : "日志区已收起");
        }

        private void AppendLog(string message)
        {
            if (IsDisposed || _txtLog == null || _txtLog.IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(AppendLog), message); } catch { } return; }
            if (_txtLog.TextLength > 30000) _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 20000);
            _txtLog.AppendText(message + "\r\n");
        }

        private void OnSaveLogClick(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog { Filter = "文本文件|*.txt|CSV|*.csv", FileName = "caxa3dButton-log.txt" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, _txtLog.Text);
                    SetStatus("日志已保存：" + dlg.FileName, Color.DimGray);
                }
                catch (Exception ex)
                {
                    SetStatus("保存日志失败：" + ex.Message, Color.Firebrick);
                    Diagnostics.Log("保存日志失败：" + ex.Message);
                }
            }
        }

        private void OnClearLogClick(object sender, EventArgs e)
        {
            _txtLog.Clear();
            Diagnostics.Log("log cleared by user");
        }

        // ================================================================ 设计环境开关

        private void OnOpenDesignEnvClick(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            _btnOpenEnv.Enabled = false;
            try
            {
                SetStatus("正在打开设计环境 …", Color.DarkOrange);
                string verdict = DesignEnvTool.OpenDesignEnv(_siteApp, this, msg => Diagnostics.Log(msg));
                SetStatus(verdict, Color.DimGray);
                Diagnostics.Log("open-env：" + verdict);
            }
            catch (Exception ex)
            {
                SetStatus("打开设计环境失败：" + ex.Message, Color.Firebrick);
                Diagnostics.Log("open-env failed: " + ex);
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) _btnOpenEnv.Enabled = true;
            }
        }

        private void OnCloseDesignEnvClick(object sender, EventArgs e)
        {
            if (_busy) return;
            var scope = DesignEnvTool.AskCloseScope(this);
            if (scope == DesignEnvTool.CloseScope.Cancel) { SetStatus("已取消关闭设计环境", Color.DimGray); return; }

            _busy = true;
            _btnCloseEnv.Enabled = false;
            try
            {
                SetStatus(scope == DesignEnvTool.CloseScope.All ? "正在关闭所有设计环境 …" : "正在关闭当前设计环境 …", Color.DarkOrange);
                string verdict = DesignEnvTool.CloseDesignEnv(_siteApp, scope == DesignEnvTool.CloseScope.All, msg => Diagnostics.Log(msg));
                SetStatus(verdict, Color.DimGray);
                Diagnostics.Log("close-env：" + verdict);
            }
            catch (Exception ex)
            {
                SetStatus("关闭设计环境失败：" + ex.Message, Color.Firebrick);
                Diagnostics.Log("close-env failed: " + ex);
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) _btnCloseEnv.Enabled = true;
            }
        }

        // ================================================================ 图号分离 / 打开工程图

        private void OnDrawNoSepClick(object sender, EventArgs e)
        {
            if (_drawNoSepForm == null || _drawNoSepForm.IsDisposed)
                _drawNoSepForm = new DrawingNoSepForm(_siteApp, TopMost);
            _drawNoSepForm.Show(this);
            _drawNoSepForm.Activate();
        }

        private void OnOpenDwgClick(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            _btnOpenDwg.Enabled = false;
            // PopupAutoYes 现已改为借还模式（PopupAutoYes.Borrow/归还），这里不再全局开/关。
            // 打开工程图后 CAXA 可能弹「设置3D信息」「尺寸检查」由 ViewUpdatePrompt 单独处理。
            try
            {
                SetStatus("正在打开工程图 …", Color.DarkOrange);
                // 提前布防：OpenFile2 打开 exb 后 CAXA 会以模态框弹「是否更新所有视图」，
                // 该模态卡住主线程 COM 调用，必须由后台哨兵线程点「是」。
                ViewUpdatePrompt.Finished = OnViewUpdateHandled;
                ViewUpdatePrompt.Arm();
                List<IntPtr> beforeDraft;
                string exbPath, prepSummary, postAction;
                string err = ComponentTool.OpenPartDrawing(_siteApp, msg => Diagnostics.Log(msg),
                    out exbPath, out prepSummary, out beforeDraft, out postAction);

                if (err != null)
                {
                    SetStatus(err.Split('\n')[0], Color.Firebrick);
                    Diagnostics.Log("open-dwg failed: " + err);
                    return;
                }

                Diagnostics.Log("open-dwg: " + prepSummary);

                // 窗口差集判据（OpenFile2 可能抛异常但图纸其实已开；以 Draft 窗口差集为权威）
                var newOnes = new List<IntPtr>();
                bool opened = WaitUntil("等图纸窗口出现", 15000, delegate
                {
                    newOnes.Clear();
                    foreach (IntPtr h in DrawingProbe.ListDraftDocs())
                        if (!beforeDraft.Contains(h)) newOnes.Add(h);
                    return newOnes.Count > 0;
                }, 300);

                if (opened)
                {
                    SetStatus("工程图已打开（差集新增 " + newOnes.Count + " 个图纸窗口）" +
                        (postAction.Length > 0 ? "，正在执行" + (postAction == "titleblock" ? "标题栏设默认" : "更新3dbom") + " …" : ""), Color.DarkGreen);
                    Diagnostics.Log("open-dwg: 图纸窗口差集新增 " + newOnes.Count + " 个 → 打开成功");
                    if (postAction.Length > 0)
                        RunPostOpenAction(postAction, exbPath);
                }
                else
                {
                    SetStatus("工程图打开超时（15 秒内没出现新图纸窗口），请手动打开：" + Path.GetFileName(exbPath ?? ""), Color.DarkOrange);
                    Diagnostics.Log("open-dwg: 差集超时 — before=" + beforeDraft.Count + " after=" + DrawingProbe.ListDraftDocs().Count);
                }
            }
            catch (Exception ex)
            {
                SetStatus("打开工程图异常：" + ex.Message, Color.Firebrick);
                Diagnostics.Log("open-dwg EX: " + ex);
            }
            finally
            {
                ViewUpdatePrompt.Disarm();
                // PopupAutoYes 现已改为借还模式，不再全局关；WatchLatePopups 由借还系统自动管理。
                _busy = false;
                if (!IsDisposed) _btnOpenDwg.Enabled = true;
            }
        }

        /// <summary>「更新视图」对话框哨兵收尾回调（后台线程触发，先切回 UI 线程）。</summary>
        private void OnViewUpdateHandled(string msg)
        {
            Diagnostics.Log("open-dwg: " + msg);
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(OnViewUpdateHandled), msg); return; }
            // 仅当现在还停在「正在打开」状态时才追一行，避免覆盖后续 postAction 的状态
            if (_lblStatus != null && _lblStatus.Text.IndexOf("正在打开工程图", StringComparison.Ordinal) >= 0)
                SetStatus(_lblStatus.Text + "（已自动更新视图）", Color.DarkGreen);
        }

        // ================================================================ 基体模型建立（移植自 caxa3dtracker）

        /// <summary>单例窗口：弹出基体模型建立界面（长方体 / 圆柱体 / 抽壳圆管 + 智能标注）。</summary>
        private void OnCubeClick(object sender, EventArgs e)
        {
            try
            {
                if (_cubeForm == null || _cubeForm.IsDisposed) _cubeForm = new CubeForm(_siteApp, TopMost);
                if (_cubeForm.Visible) { _cubeForm.Activate(); }
                else { _cubeForm.Show(this); _cubeForm.Activate(); }
            }
            catch (Exception ex)
            {
                SetStatus("打开基体模型建立窗口失败：" + ex.Message, Color.Firebrick);
                Diagnostics.Log("open CubeForm failed: " + ex);
            }
        }

        // ================================================================ 显示 / 隐藏所有类型
        // 移植自 caxa3dtracker（2026-11-13）：走 IZSceneDocProperty 的 10 个类别标志，
        // 每类「原态 → 设定 → 回读」三态写 trace，回读不一致的类别原样点名。

        private void RunShowHideAll(bool show)
        {
            string label = show ? "显示所有类型" : "隐藏所有类型";
            SetStatus((show ? "正在显示" : "正在隐藏") + "所有类型 …", Color.DarkOrange);
            try
            {
                string verdict = show ? ShowHideTool.ShowAllTypes(_siteApp)
                                      : ShowHideTool.HideAllTypes(_siteApp);
                SetStatus(verdict, Color.DimGray);
                Diagnostics.Log(label + "：" + verdict);
            }
            catch (Exception ex)
            {
                SetStatus(label + "失败：" + ex.Message, Color.Firebrick);
                Diagnostics.Log(label + " click failed: " + ex);
            }
        }

        // ================================================================ 装配体排序
        // 移植自 caxa3dtracker（2026-11-13）：弹条件窗（升/降序 + 只排第一层/穿透到底），
        // 会话推迟到点「执行排序」；排序根＝设计树选中的装配体，未选中时用场景唯一顶层装配。

        private void OnAssemblySortClick(object sender, EventArgs e)
        {
            try
            {
                if (_assemblySortForm == null || _assemblySortForm.IsDisposed)
                    _assemblySortForm = new AssemblySortForm(_siteApp, TopMost);
                if (_assemblySortForm.Visible) { _assemblySortForm.Activate(); }
                else { _assemblySortForm.Show(this); _assemblySortForm.Activate(); }
            }
            catch (Exception ex)
            {
                SetStatus("打开装配体排序窗口失败：" + ex.Message, Color.Firebrick);
                Diagnostics.Log("open AssemblySortForm failed: " + ex);
            }
        }

        // ================================================================ 打开工程图联动动作
        // 钣金件 → 工程图标题栏设默认（走 codenameextraction CRX：CMD:TITLEBLOCK_3DSET）
        // 装配   → 更新3dbom（代点法，对齐 caxa3dexbtracker 口径：CRX 派发 CX_DFT_Update3DBom →
        //          「更新3D明细」原生模态框由 DlgUpdate3DBom 哨兵按屏幕像素勾「更新」并点「确定」；
        //          2026-11-10 用户定案：禁用 TLB 静默 UpdateDraftViews，原 API 通道已整条撤除）
        // 二者都只在「图纸已确认打开」之后才执行；任一环节失败只记日志、不阻断前面的打开结果。

        private void RunPostOpenAction(string postAction, string exbPath)
        {
            Action<string> log = msg => Diagnostics.Log(msg);
            if (postAction == "titleblock")
            {
                if (string.IsNullOrEmpty(exbPath)) { Diagnostics.Log("open-dwg: titleblock 中止 — exb 路径为空"); return; }
                bool ready = WaitUntil("等图纸命令桥(codenameextraction CRX)就绪", 25000,
                    CodenameBridge.IsCrxLoaded, 400);
                if (!ready)
                {
                    SetStatus("标题栏设默认中止：图纸命令桥 25s 内未就绪（请确认 caxa3dcodenameextraction.crx 已随图纸模块加载）",
                        Color.Firebrick);
                    Diagnostics.Log("open-dwg: titleblock 中止 — CRX 未就绪");
                    return;
                }
                int baseApplied = Dlg3DInfo.AppliedCount;
                int baseCount = CodenameBridge.LogCount("commandEnded | CX_DFT_TITLEBLOCK_3DSET");
                CodenameBridge.PostTitleBlockDefault();
                // 以「设置3D信息」被 Dlg3DInfo 完整填好并关闭为权威完成信号（避免 commandEnded 在
                // 对话框仍开着时就提前触发、本插件以为已完成却没点确定 —— 11-10 实机现象）。
                bool done = WaitUntil("等「设置3D信息」填好并确定", 30000,
                    () => Dlg3DInfo.AppliedCount > baseApplied, 250);
                if (!done)
                    done = CodenameBridge.WaitCommandEnded("CX_DFT_TITLEBLOCK_3DSET", baseCount, 8000, 300, log);
                SetStatus(done ? "工程图标题栏已设默认（设置3D信息已填并确定）"
                               : "标题栏设默认超时，请查看日志",
                    done ? Color.DarkGreen : Color.Firebrick);
            }
            else if (postAction == "update3dbom")
            {
                if (string.IsNullOrEmpty(exbPath) || !File.Exists(exbPath))
                { Diagnostics.Log("open-dwg: update3dbom 中止 — exb 路径无效：" + (exbPath ?? "")); return; }

                // ★ 代点法（颗粒度对齐 caxa3dexbtracker「更新3dbom」/ caxa3ddiffrename Update3DBomCore）★
                //   图纸已由 open-dwg 确认打开 → 等 CRX 就绪 → 让位 → 派发前先布防哨兵 →
                //   派 CX_DFT_Update3DBom → 等 CRX 接单 → 哨兵勾「更新」点「确定」→
                //   权威完成信号 = CRX 报 commandEnded | CX_DFT_Update3DBom（模态框关掉那一刻才结束）。
                bool ready = WaitUntil("等图纸命令桥(codenameextraction CRX)就绪", 25000,
                    CodenameBridge.IsCrxLoaded, 400);
                if (!ready)
                {
                    SetStatus("更新3dbom 中止：图纸命令桥 25s 内未就绪（请确认 codenameextraction CRX 已随图纸模块加载）",
                        Color.Firebrick);
                    Diagnostics.Log("open-dwg: update3dbom 中止 — CRX 未就绪");
                    return;
                }

                bool wasTopMost = TopMost;
                try
                {
                    // ① 让位（exbtracker 同款第一件事）：哨兵靠屏幕 BitBlt 定位复选框，
                    //    本窗置顶会压住模态框、抓到自己的画面 ⇒ 摘置顶 + 沉底，收尾再索回。
                    if (TopMost) TopMost = false;
                    try { SendToBack(); } catch { }
                    Diagnostics.Log("open-dwg: 工具窗已摘置顶沉底（流程期不遮挡「更新3D明细」）");

                    // ② 布防哨兵必须在派命令**之前**：框可能几百毫秒就弹，模态一弹起消息泵就归它。
                    //    回调在哨兵后台线程触发 ⇒ 改控件只能 BeginInvoke（坑 197）。
                    string sentinelMsg = null;
                    DlgUpdate3DBom.Finished = delegate(string m)
                    {
                        sentinelMsg = m;
                        try
                        {
                            if (IsDisposed) return;
                            BeginInvoke((MethodInvoker)delegate
                            {
                                bool bad = m != null && (m.StartsWith("没能勾上") || m.StartsWith("等 "));
                                SetStatus("更新3dbom：" + m, bad ? Color.Firebrick : Color.DarkGreen);
                            });
                        }
                        catch { }
                    };
                    string fileName = Path.GetFileName(exbPath);
                    DlgUpdate3DBom.Arm(fileName);

                    // ③ 派发（键入通道直投）并等 CRX 接单回执
                    Diagnostics.Log("open-dwg: 更新3dbom（代点法）→ 派发 CX_DFT_Update3DBom，目标图纸 " + fileName);
                    int endedBase = CodenameBridge.LogCount("commandEnded | CX_DFT_Update3DBom");
                    int seq = CodenameBridge.PostUpdate3DBom();
                    bool acked = WaitUntil("等 CRX 接单", 15000,
                        () => CodenameBridge.LogCount("REQ.ack | seq=" + seq) > 0, 100);
                    if (!acked)
                    {
                        DlgUpdate3DBom.Disarm();
                        SetStatus("更新3dbom 中止：CRX 15 秒内没接单（请求文件没被取走），已撤防哨兵", Color.Firebrick);
                        return;
                    }

                    // ④ 哨兵办框（75 秒布防总时长）与 CRX 权威结束信号（60 秒）两步都等完才判
                    WaitUntil("等「更新3D明细」由哨兵办完", 75000,
                        () => !DlgUpdate3DBom.IsArmed, 300);
                    bool ended = CodenameBridge.WaitCommandEnded("CX_DFT_Update3DBom", endedBase, 60000, 300, log);
                    if (DlgUpdate3DBom.IsArmed) DlgUpdate3DBom.Disarm();

                    string sentinel = sentinelMsg ?? "哨兵没回话";
                    Diagnostics.Log("open-dwg: 命令结束=" + (ended ? "✔ CRX 已确认" : "✘ 60 秒内没等到 commandEnded") +
                                    "；哨兵结局：" + sentinel);
                    if (ended)
                        SetStatus("工程图已更新3dbom（哨兵已勾「更新」点确定，CRX 已确认命令结束）", Color.DarkGreen);
                    else
                        SetStatus("更新3dbom 未等到 CRX 结束信号（哨兵结局：" + sentinel + "），请查看日志", Color.Firebrick);
                }
                finally
                {
                    if (DlgUpdate3DBom.IsArmed) DlgUpdate3DBom.Disarm();
                    if (!IsDisposed)
                    {
                        try
                        {
                            if (wasTopMost) TopMost = true;
                            BringToFront();
                            Activate();
                            Diagnostics.Log("open-dwg: 更新3dbom 收尾，工具窗已索回前台");
                        }
                        catch { }
                    }
                }
            }
        }

        // ================================================================ 等待原语

        private static bool WaitUntil(string what, int timeoutMs, Func<bool> condition, int pollMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { Application.DoEvents(); } catch { }
                try { if (condition()) return true; } catch { }
                Thread.Sleep(pollMs);
            }
            Diagnostics.Log(what + ": 超时 " + timeoutMs + "ms");
            return false;
        }

        private static void PumpMessages(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                try { Application.DoEvents(); } catch { }
                Thread.Sleep(50);
            }
        }
    }
}
