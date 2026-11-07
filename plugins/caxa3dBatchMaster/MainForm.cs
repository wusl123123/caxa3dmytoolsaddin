// 主窗体：复刻 SwBatchMaster 的界面布局（功能区按钮 / 表格区域 / 表格显示区 / 日志区），
// 改用 CAXA3D 的 ICAPI。Win2000 极简风：不启用视觉主题、宋体 9pt、系统色、纯汉字按钮、固定锚定。
// 铁律：_running/_stopRequested 静态；FormClosing 置停止标志；控件释放时抛 OperationStoppedException。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;   // ChordRescue 唯一实现（规则第 37 节）
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    public class MainForm : Form
    {
        // ↓↓↓ 键盘归属抢救：Ctrl+C/V/X/A/Z 归本窗口自己的文本框（机理/开关/取证见 ChordRescue，规则第 37 节）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ChordRescue.Install(this, msg => Diagnostics.Trace(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); ChordRescue.Uninstall(this, "窗口关闭"); }
        protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑↑↑

        private static volatile bool _running;
        private static volatile bool _stopRequested;
        public static bool IsStopRequested => _stopRequested;

        private readonly IZBaseApp _app;
        private readonly CaxaBatchService _svc;
        private BatchProcessor _processor;

        private readonly TextBox _txtTablePath = new TextBox();
        private readonly DataGridView _dgv = new DataGridView();
        private readonly Label _lblStatus = new Label();
        private readonly Label _lblInfo = new Label();

        private readonly List<string> _logLines = new List<string>();
        private LogForm _logForm;

        private readonly Button _btnImportAssembly = new Button();
        private readonly Button _btnRename = new Button();
        private readonly Button _btnReadProps = new Button();
        private readonly Button _btnWriteProps = new Button();
        private readonly Button _btnClear = new Button();
        private readonly Button _btnRefreshDrw = new Button();
        private readonly Button _btnSplit = new Button();
        private readonly Button _btnTop = new Button();
        private readonly Button _btnRefreshTable = new Button();
        private readonly Button _btnSelectTable = new Button();
        private readonly Button _btnLocateTable = new Button();
        private readonly Button _btnStop = new Button();
        private readonly Button _btnLog = new Button();

        private readonly ContextMenuStrip _colMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _rowMenu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer _selSyncTimer = new System.Windows.Forms.Timer { Interval = 200 };
        private string _tablePath = "";

        public MainForm(IZBaseApp app)
        {
            _app = app;
            _svc = new CaxaBatchService(app);
            Diagnostics.Reset();
            BuildUi();
            _processor = new BatchProcessor(_svc, _dgv, AppendLog, () => _stopRequested);
            if (!IsHandleCreated) CreateHandle();
        }

        /// <summary>默认附属表格路径（程序同目录）。</summary>
        private string DefaultTablePath => Path.Combine(AssemblyDirectory, "caxa3dBatchMaster属性表格.xlsx");

        private static string AssemblyDirectory
        {
            get
            {
                try { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory; }
                catch { return AppContext.BaseDirectory; }
            }
        }

        // ---------------------------------------------------------------- UI 布局

        private void BuildUi()
        {
            Font = new Font("宋体", 9F);
            Text = "CAXA3D批量大师";
            ClientSize = new Size(1100, 660);
            MinimumSize = new Size(1100, 420);  // 顶栏为固定坐标布局：窗口只允许放大，缩小会导致贴右控件与左侧按钮重叠
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            FormClosing += OnFormClosing;

            // 功能区按钮行（纯汉字，禁 emoji / 图标）
            _btnImportAssembly.SetBounds(12, 8, 108, 28); _btnImportAssembly.Text = "从装配体导入";
            _btnRename.SetBounds(126, 8, 88, 28); _btnRename.Text = "文件改名";
            _btnReadProps.SetBounds(220, 8, 108, 28); _btnReadProps.Text = "属性批量读取";
            _btnWriteProps.SetBounds(334, 8, 108, 28); _btnWriteProps.Text = "属性批量写入";
            _btnClear.SetBounds(448, 8, 88, 28); _btnClear.Text = "屏幕清理";
            _btnRefreshDrw.SetBounds(542, 8, 88, 28); _btnRefreshDrw.Text = "刷新工程图";
            // 用户指令 6：「窗口前置」左侧加「图号分离」；整行右移一档，仍与贴右的 _lblStatus(828) 留 10px 间隙
            _btnSplit.SetBounds(636, 8, 88, 28); _btnSplit.Text = "图号分离";
            _btnTop.SetBounds(730, 8, 88, 28); _btnTop.Text = "窗口前置"; _btnTop.BackColor = Color.LightSkyBlue;
            _lblStatus.SetBounds(828, 12, 160, 22); _lblStatus.Text = "CAXA 3D：未连接";
            _btnStop.SetBounds(992, 8, 92, 28); _btnStop.Text = "停止"; _btnStop.BackColor = Color.MistyRose; _btnStop.Enabled = false;
            _lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;   // 最大化时贴右，不孤悬左侧
            _btnStop.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            // 表格区域行：路径框左右拉伸，右侧按钮组整体贴右（两者间隙恒为 8px，不会重叠）
            _txtTablePath.SetBounds(12, 44, 540, 22); _txtTablePath.ReadOnly = true;
            _btnRefreshTable.SetBounds(560, 42, 80, 26); _btnRefreshTable.Text = "刷新表格";
            _btnSelectTable.SetBounds(646, 42, 80, 26); _btnSelectTable.Text = "选择表格";
            _btnLocateTable.SetBounds(732, 42, 80, 26); _btnLocateTable.Text = "定位表格";
            _btnLog.SetBounds(818, 42, 80, 26); _btnLog.Text = "日志";
            _lblInfo.SetBounds(904, 44, 80, 22); _lblInfo.Text = ""; _lblInfo.ForeColor = Color.Red;
            _txtTablePath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _btnRefreshTable.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnSelectTable.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnLocateTable.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _lblInfo.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            // 表格显示区：四边锚定，窗口缩放/最大化时宽高一起跟随（缺 Bottom 会导致下方白边）
            _dgv.SetBounds(12, 76, ClientSize.Width - 24, ClientSize.Height - 88);
            _dgv.AllowUserToAddRows = true;
            _dgv.AllowUserToDeleteRows = true;
            _dgv.RowHeadersVisible = true;
            _dgv.ColumnHeadersVisible = true;
            _dgv.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _dgv.ScrollBars = ScrollBars.Both;
            _dgv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _dgv.ColumnHeaderMouseClick += Dgv_ColumnHeaderMouseClick;
            _dgv.CellFormatting += Dgv_CellFormatting;
            _dgv.RowPostPaint += Dgv_RowPostPaint;
            _dgv.RowHeaderMouseClick += Dgv_RowHeaderMouseClick;
            // 三色状态（用户指令 3）：进入编辑记快照，结束编辑比对→黄
            _dgv.CellBeginEdit += Dgv_CellBeginEdit;
            _dgv.CellEndEdit += Dgv_CellEndEdit;
            // 行选中联动（用户指令 2）：SelectionChanged 一次点击会连发多帧（旧格取消＋新格选中），
            // 用 200 ms 合并计时器收口，避免每次连发都投一轮 COM 选择。
            _selSyncTimer.Tick += SelSyncTimer_Tick;
            _dgv.SelectionChanged += Dgv_SelectionChanged;

            Controls.AddRange(new Control[] {
                _btnImportAssembly, _btnRename, _btnReadProps, _btnWriteProps,
                _btnClear, _btnRefreshDrw, _btnSplit, _btnTop, _btnStop, _btnLog, _lblStatus,
                _txtTablePath, _btnRefreshTable, _btnSelectTable, _btnLocateTable, _lblInfo,
                _dgv });

            // 列标题右键菜单：升序排序 / 突出重复值 / 替换
            var miSort = new ToolStripMenuItem("按升序排序"); miSort.Click += MiSort_Click;
            var miDup = new ToolStripMenuItem("突出重复值"); miDup.Click += MiHighlightDup_Click;
            var miReplace = new ToolStripMenuItem("替换"); miReplace.Click += MiReplace_Click;
            _colMenu.Items.AddRange(new ToolStripItem[] { miSort, miDup, miReplace });

            // 行标题右键菜单：打开本行零部件
            var miOpen = new ToolStripMenuItem("打开本行零部件"); miOpen.Click += MiOpenPart_Click;
            _rowMenu.Items.Add(miOpen);

            // 事件
            _btnImportAssembly.Click += BtnImportAssembly_Click;
            _btnRename.Click += BtnRename_Click;
            _btnReadProps.Click += BtnReadProps_Click;
            _btnWriteProps.Click += BtnWriteProps_Click;
            _btnClear.Click += BtnClear_Click;
            _btnRefreshDrw.Click += BtnRefreshDrw_Click;
            _btnSplit.Click += BtnSplit_Click;
            _btnTop.Click += BtnTop_Click;
            _btnRefreshTable.Click += BtnRefreshTable_Click;
            _btnSelectTable.Click += BtnSelectTable_Click;
            _btnLocateTable.Click += BtnLocateTable_Click;
            _btnStop.Click += BtnStop_Click;
            _btnLog.Click += BtnLog_Click;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateStatus();
            // 规则 14.8：字段名映射由用户可编辑文件驱动，启动即打印生效路径＋条数（取证必须落日志）
            try { AppendLog(PropFieldMap.InitAndDescribe()); }
            catch (Exception ex) { AppendLog("内部字段映射加载失败（属性名将按表头原样使用）：" + ex.Message); }
            // 用户指令 1：PartMass 这族字段从 BOM 引擎取，走哪些列由这份可编辑清单决定
            try { AppendLog(BomFieldList.InitAndDescribe()); }
            catch (Exception ex) { AppendLog("BOM 字段清单加载失败（本轮全部按共享表读取）：" + ex.Message); }
            try { AppendLog(SplitRules.InitAndDescribe()); }
            catch (Exception ex) { AppendLog("图号分离规则加载失败：" + ex.Message); }
            LoadDefaultTable();
        }

        // ---------------------------------------------------------------- 状态 / 日志

        private void UpdateStatus()
        {
            _lblStatus.Text = _svc.VersionText;
        }

        public void AppendLog(string msg)
        {
            if (IsDisposed) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + msg;
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), msg); return; }
            _logLines.Add(line);
            if (_logForm != null && !_logForm.IsDisposed) _logForm.RefreshLog();
        }

        /// <summary>点击「日志」按钮：弹出独立日志窗口（含保存/清空）。窗口复用，关闭后下次重新创建。</summary>
        private void BtnLog_Click(object sender, EventArgs e)
        {
            if (_logForm == null || _logForm.IsDisposed)
            {
                _logForm = new LogForm(() => _logLines, () => _logLines.Clear());
                _logForm.FormClosed += (s, ev) => _logForm = null;
            }
            if (_logForm.Visible) _logForm.BringToFront();
            else _logForm.Show(this);
        }

        private void MarkDirty()
        {
            _lblInfo.Text = "表格已修改，尚未保存";
        }

        // ---------------------------------------------------------------- 长流程 + 停止

        private void BtnStop_Click(object sender, EventArgs e)
        {
            _stopRequested = true;
            CaxaAutomation.StopRequested = true;
            AppendLog("已请求停止，当前批次处理完成后将退出。");
        }

        /// <summary>长等待链包装：置运行态 + 清除停止标志，结束后复位；循环内泵消息使停止键可点（铁律 6）。</summary>
        private void RunLong(Action action)
        {
            if (_running) { AppendLog("已有任务在运行，请先等待或点击「停止」。"); return; }
            _running = true;
            _stopRequested = false;
            CaxaAutomation.StopRequested = false;
            _btnStop.Enabled = true;
            // 11-07 用户指令：执行期间取消「窗口前置」。本窗口置顶会盖住 CAXA 的模态框/文件框，
            // 挡住后台代点要看的现场；收尾（含异常/停止）一律恢复原状态。
            bool wasTopMost = TopMost;
            if (wasTopMost)
            {
                TopMost = false;
                _btnTop.BackColor = SystemColors.Control;
                AppendLog("执行期间已临时取消窗口前置（任务结束后自动恢复）。");
            }
            try { action(); }
            finally
            {
                _running = false;
                _stopRequested = false;
                CaxaAutomation.StopRequested = false;
                _btnStop.Enabled = false;
                if (wasTopMost)
                {
                    TopMost = true;
                    _btnTop.BackColor = Color.LightSkyBlue;
                    AppendLog("任务结束，窗口前置已恢复。");
                }
            }
        }

        // ---------------------------------------------------------------- 表格加载 / 保存

        private void LoadDefaultTable()
        {
            string path = DefaultTablePath;
            if (!File.Exists(path)) { CreateDefaultTable(path); AppendLog("未找到附属表格，已自动创建：" + path); }
            LoadTable(path);
        }

        private void CreateDefaultTable(string path)
        {
            int cols = TableService.FixedColumnCount;
            var data = new object[1, cols];
            for (int c = 0; c < cols; c++) data[0, c] = TableService.FixedHeaders[c];
            TableService.Save(path, data);
        }

        private void LoadTable(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { AppendLog("表格文件不存在，无法加载。"); return; }
            // 整表重建期间：编辑跟踪与选中联动一律关掉（Rows.Clear/Add 会连发 SelectionChanged，
            // 每一帧都去投一轮 COM 选择＝打开一张大表能把界面钉死）
            _suppressEditTracking = true; _suppressSelSync = true;
            try { LoadTableCore(path); }
            finally
            {
                _suppressEditTracking = false; _suppressSelSync = false;
                _selSyncTimer.Stop(); _pendingSyncRow = -1;
                _processor.ResetSelectionSync();
            }
        }

        private void LoadTableCore(string path)
        {
            object[,] data = null;
            try { data = TableService.Load(path); }
            catch (Exception ex) { AppendLog("加载表格失败：" + ex.Message); return; }

            int rows = data?.GetLength(0) ?? 0;
            int cols = Math.Max(TableService.FixedColumnCount, data?.GetLength(1) ?? 0);

            // 【11-06 用户指令 3】所有列初始列宽统一 20 mm（原「文件路径 300 / A 列 110 / 其余 90 px」整条作废）。
            // 20 mm 是物理量，控件的 Width 是像素 ⇒ 换算必须用运行期实测 DPI，不许写死 96（用户机器可能是 125%/150% 缩放）。
            int colWidthPx = InitialColumnWidthPx();
            _dgv.Columns.Clear();
            for (int c = 0; c < cols; c++)
            {
                var col = new DataGridViewTextBoxColumn
                {
                    Name = "col" + c,
                    HeaderText = TableService.ColumnLetter(c),
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                    Resizable = DataGridViewTriState.True,
                    Width = colWidthPx
                };
                // 「重量」这类走 BOM 引擎的列：在列标题挂一句来源注明（用户指令 1「特别注明从 BOM 导出」）。
                string note = BomFieldNotes.ToolTipFor(c < data.GetLength(1) ? (data[0, c]?.ToString() ?? "") : "");
                if (!string.IsNullOrEmpty(note)) col.ToolTipText = note;
                _dgv.Columns.Add(col);
            }

            _dgv.Rows.Clear();
            for (int r = 0; r < rows; r++)
            {
                int idx = _dgv.Rows.Add();
                for (int c = 0; c < cols; c++)
                {
                    string v = (c < data.GetLength(1)) ? (data[r, c]?.ToString() ?? "") : "";
                    _dgv.Rows[idx].Cells[c].Value = v;
                }
            }

            // 换表＝上一张表的三色状态全部失效（连带清掉编辑快照，避免拿旧行的值来比对新行）
            CellStates.ClearAll(_dgv);
            _editSnapshots.Clear();
            // 旧数据基准也跟着旧表作废：新表必须先「属性批量读取」才有可比的旧值（否则写侧按「非空即算变化」）
            _processor.ResetOldValues();
            if (_dgv.Rows.Count == 0) _processor.ClearTable();
            else _processor.EnsureHeaderRow();

            _tablePath = path;
            _txtTablePath.Text = path;
            _lblInfo.Text = "";
            AppendLog($"已加载表格：{path}（{rows} 行 × {cols} 列）");
        }

        /// <summary>20 mm → 像素（用户指令 3 的初始列宽）。DPI 取运行期实测值，取不到才回退 96 并写日志说明。</summary>
        private static int InitialColumnWidthPx()
        {
            double dpi = 0;
            string src;
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))   // 屏幕 DC：不需要控件句柄，任何时点可读
                { dpi = g.DpiX; src = "屏幕 DC 实测"; }
            }
            catch (Exception ex)
            {
                dpi = 96.0; src = "取 DPI 失败（" + ex.Message + "）回退 96";
            }
            int px = (int)Math.Round(20.0 / 25.4 * dpi, MidpointRounding.AwayFromZero);
            if (px < 24) px = 24;    // DataGridView 列宽下限保护（DPI 异常时不至于把列压没）
            Diagnostics.Trace(string.Format("列宽换算：20 mm × DPI {0:0.##} px/in ÷ 25.4 = {1} px（{2}）", dpi, px, src));
            return px;
        }

        private void SaveTable(string path)
        {
            try
            {
                int rows = _dgv.Rows.Count - (_dgv.AllowUserToAddRows ? 1 : 0);
                int cols = _dgv.ColumnCount;
                if (rows == 0) { AppendLog("表格为空，无需保存。"); return; }

                var data = new object[rows, cols];
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        if (r == 0 && c < TableService.FixedColumnCount) data[r, c] = TableService.FixedHeaders[c];
                        else data[r, c] = _dgv.Rows[r].Cells[c].Value?.ToString() ?? "";
                    }
                TableService.Save(path, data);
                _lblInfo.Text = "";
                AppendLog("已保存表格：" + path);
            }
            catch (Exception ex) { AppendLog("保存表格失败：" + ex.Message); }
        }

        // ---------------------------------------------------------------- 表格区域按钮

        private void BtnRefreshTable_Click(object sender, EventArgs e)
        {
            if (_lblInfo.Text.Length > 0 &&
                MessageBox.Show("表格有未保存的修改，刷新将丢失这些修改。是否继续？", "刷新表格",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            LoadTable(_tablePath);
        }

        private void BtnSelectTable_Click(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择表格文件",
                Filter = "Excel 表格|*.xlsx;*.xls|所有文件|*.*",
                InitialDirectory = Path.GetDirectoryName(_tablePath) ?? AssemblyDirectory
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (_lblInfo.Text.Length > 0 &&
                MessageBox.Show("表格有未保存的修改，切换表格将丢失这些修改。是否继续？", "选择表格",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            LoadTable(dlg.FileName);
        }

        private void BtnLocateTable_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_tablePath) || !File.Exists(_tablePath)) { AppendLog("表格文件不存在，无法定位。"); return; }
            try { Process.Start("explorer.exe", "/select,\"" + _tablePath + "\""); }
            catch (Exception ex) { AppendLog("定位表格失败：" + ex.Message); }
        }

        // ---------------------------------------------------------------- 行选中联动（用户指令 2）

        private bool _suppressSelSync;      // 加载/清表期间整表重建会连发 SelectionChanged，这段时间不联动
        private int _pendingSyncRow = -1;

        private void Dgv_SelectionChanged(object sender, EventArgs e)
        {
            if (_suppressSelSync || _running) return;
            int row = _dgv.CurrentCell?.OwningRow?.Index ?? -1;
            if (row < 1) return;                            // 表头行/无当前格＝不联动
            _pendingSyncRow = row;
            _selSyncTimer.Stop();
            _selSyncTimer.Start();                          // 连发多帧收口成一次
        }

        private void SelSyncTimer_Tick(object sender, EventArgs e)
        {
            _selSyncTimer.Stop();
            int row = _pendingSyncRow;
            _pendingSyncRow = -1;
            if (row < 1 || _running || _svc == null || !_svc.IsConnected) return;
            try
            {
                string msg = _processor?.SyncSelectionFromRow(row);
                if (msg != null) AppendLog(msg);            // 只在失败/未执行时占日志；成功由设计树自己说话
            }
            catch (Exception ex)
            {
                AppendLog("选中联动异常（不影响表格操作）：" + ex.Message);
                Diagnostics.Trace("选中联动 EX：" + ex);
            }
        }

        // ---------------------------------------------------------------- 功能区按钮

        private void BtnImportAssembly_Click(object sender, EventArgs e)
        {
            using var dlg = new PopupAssemblyOptions();
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (!_svc.IsConnected) { AppendLog("请先在 CAXA 3D 打开装配体（设计环境）再导入。"); return; }
            UpdateStatus();
            bool exSup = dlg.ExcludeSuppressed;
            bool exBom = dlg.ExcludeBomExcluded;
            // 用户指定：导入后做场景级重建并保存装配体（写盘动作，日志会给出元素数/脏计数/文件时间三条证据）
            AppendLog("导入后将执行场景级重建（RegenerateParts/Update/UpdateGraphics/Redraw）并保存装配体文件。");
            RunLong(() => _processor.ImportAssembly(exSup, exBom, true));
            MarkDirty();
        }

        /// <summary>「文件改名」＝点击后直接运行（11-06 用户指令 2 B「不要再弹提示窗」，原 Yes/No 确认框整段删除）。
        /// 破坏性说明不再用模态框挡在前面，改由处理层逐行日志＋收尾状态行承担；
        /// 唯一的「闸」是代码里的准入闸（外链件/场景文档/目标名冲突），拒绝的行只记账不动手。</summary>
        private void BtnRename_Click(object sender, EventArgs e)
        {
            RunLong(() => _processor.RenameFiles());
            MarkDirty();
        }

        private void BtnReadProps_Click(object sender, EventArgs e)
        {
            if (!_svc.IsConnected) UpdateStatus();
            RunLong(() => _processor.ReadProperties());
            MarkDirty();
            // 属性批量读取后回到第二行（装配体）
            try
            {
                int asmRow = 1;
                if (_dgv.Rows.Count > asmRow && !_dgv.Rows[asmRow].IsNewRow
                    && _dgv.Rows[asmRow].Cells.Count > 0)
                {
                    _dgv.CurrentCell = _dgv.Rows[asmRow].Cells[0];
                    _dgv.FirstDisplayedScrollingRowIndex = asmRow;
                    AppendLog("属性读取完成，已定位到第二行装配体。");
                }
            }
            catch (Exception ex) { AppendLog("读取后定位装配体失败：" + ex.Message); }
        }

        private void BtnWriteProps_Click(object sender, EventArgs e)
        {
            // 用户 11-07 指令：不再输入「属性名=值」，改为逐格比对「属性批量读取」记下的旧数据，
            // 只把用户改过（与旧值不一致）的格子写进对应行零部件的自定义属性共享表，属性名取列头。
            // 指令 2（11-07 追加）：点击后**不弹窗**，直接执行（长流程本身有「停止」按钮兜底）。
            if (!_svc.IsConnected) UpdateStatus();
            int n = 0;
            RunLong(() => { n = _processor.WriteChangedProperties(); });
            // 三色状态替代原 H 列 ●：回读一致→绿（处理层按单元格逐个标），一行都没成功→不动颜色只写日志
            if (n == 0) AppendLog("本轮没有任何变化格通过共享表回读确认，单元格不标绿（详见日志与 trace）。");
            MarkDirty();
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("将清空表格中的数据行（保留第 1 行表头）。是否继续？", "屏幕清理",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _processor.ClearTable();
            MarkDirty();
        }

        private void BtnRefreshDrw_Click(object sender, EventArgs e)
        {
            // 破坏性前置确认：这条链会往磁盘上的图纸里写东西并关窗（保存＝不可撤销地覆盖现有 .exb）。
            // 通道是「自带 CRX 桥」（11-06 用户选定原文），所以还要如实告诉用户桥不可用时会直接中止、不退回远程 COM。
            if (MessageBox.Show("将逐个打开表格里的同名工程图 .exb，处理宿主弹窗，保存并关闭图纸。\n" +
                "破坏性：会覆盖磁盘上的 .exb 文件（保存即不可撤销）。\n" +
                "通道：图纸进程内的 CRX 桥（caxa3dBatchMasterView.crx）。桥不可用时本功能直接中止，" +
                "不会退回远程 COM 保存（那条路实测是假成功，只写出 xxx.exb.ics）。\n" +
                "首次使用会先打开一张专用空图纸做预热，随后不保存自动关掉。\n" +
                "刚装完或刚更新过本插件：必须先重启 CAXA 3D，桥才会加载。是否继续？",
                "刷新工程图", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RunLong(() => _processor.RefreshDrawings());
        }

        /// <summary>「图号分离」弹窗（用户指令 6）：两个按钮——打开规则文件 / 执行分离。执行走 RunLong，停止键可用。</summary>
        private void BtnSplit_Click(object sender, EventArgs e)
        {
            using var dlg = new PopupDrawingSplit(
                action => RunLong(action),
                () => _processor.SplitDrawingNumbers(),
                AppendLog,
                () => { _stopRequested = true; CaxaAutomation.StopRequested = true; });
            dlg.ShowDialog(this);
            MarkDirty();
        }

        private void BtnTop_Click(object sender, EventArgs e)
        {
            TopMost = !TopMost;
            _btnTop.BackColor = TopMost ? Color.LightSkyBlue : SystemColors.Control;
        }

        // ---------------------------------------------------------------- 单元格三色状态（用户指令 3）

        // 编辑快照：key = "行,列"（编辑结束即移除）。只在用户真的按 Enter/移开焦点结束编辑时比对。
        private readonly Dictionary<string, string> _editSnapshots = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _suppressEditTracking;

        private static string SnapKey(int row, int col) { return row + "," + col; }

        private void Dgv_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (_suppressEditTracking || e.RowIndex < 1) return;      // 第 1 行是表头行，不参与三色
            _editSnapshots[SnapKey(e.RowIndex, e.ColumnIndex)] = CellValue(e.RowIndex, e.ColumnIndex);
        }

        private void Dgv_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 1) return;
            string key = SnapKey(e.RowIndex, e.ColumnIndex);
            string before;
            bool known = _editSnapshots.TryGetValue(key, out before);
            _editSnapshots.Remove(key);
            if (_suppressEditTracking) return;
            string after = CellValue(e.RowIndex, e.ColumnIndex);
            if (!known) before = "";                                   // 极少见（程序化进入编辑）：按空值比
            // 一致→状态回到「无色」（改回原值就不算改动）；不一致→黄（用户改过，等待返填确认）
            CellStates.Set(_dgv, e.RowIndex, e.ColumnIndex,
                string.Equals(before, after, StringComparison.Ordinal) ? CellState.None : CellState.Edited);
            if (!string.Equals(before, after, StringComparison.Ordinal)) MarkDirty();
        }

        private string CellValue(int row, int col)
        {
            if (row < 0 || row >= _dgv.Rows.Count) return "";
            if (col < 0 || col >= _dgv.ColumnCount) return "";
            return _dgv.Rows[row].Cells[col].Value?.ToString() ?? "";
        }

        // ---------------------------------------------------------------- 表格显示样式

        private void Dgv_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (e.RowIndex == 0)
            {
                e.CellStyle.BackColor = Color.FromArgb(240, 240, 240);
                e.CellStyle.Font = new Font(_dgv.Font, FontStyle.Bold);
                e.CellStyle.SelectionBackColor = Color.FromArgb(240, 240, 240);
                e.CellStyle.SelectionForeColor = Color.Black;
                return;
            }
            // 突出重复值：优先级最高（否则被导入时刷的绿/红状态色盖住，看着像没生效）
            if (_processor != null && _processor.IsDuplicateCell(e.RowIndex, e.ColumnIndex))
            {
                e.CellStyle.BackColor = Color.Pink;
                e.CellStyle.SelectionBackColor = Color.FromArgb(255, 150, 150);
                e.CellStyle.SelectionForeColor = Color.Black;
                return;
            }
            // 三色状态：黄=用户改过 / 绿=返填成功 / 红=返填失败（原 H 列「属性已更改」● 已删除）
            CellStates.ApplyToStyle(e, CellStates.Of(_dgv.Rows[e.RowIndex].Cells[e.ColumnIndex]));
        }

        private void Dgv_RowPostPaint(object sender, DataGridViewRowPostPaintEventArgs e)
        {
            var grid = (DataGridView)sender;
            var bounds = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth, e.RowBounds.Height);
            TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), grid.Font, bounds,
                grid.RowHeadersDefaultCellStyle.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }

        private void Dgv_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            _colMenu.Tag = e.ColumnIndex;
            var local = _dgv.PointToClient(Control.MousePosition);
            _colMenu.Show(_dgv, local);
        }

        private void MiSort_Click(object sender, EventArgs e)
        {
            if (_colMenu.Tag is int col) _processor.SortColumnAscending(col);
        }

        private void MiHighlightDup_Click(object sender, EventArgs e)
        {
            if (_colMenu.Tag is int col) _processor.HighlightDuplicateValues(col);
        }

        // ---------------------------------------------------------------- 行标题右键：打开本行零部件

        private void Dgv_RowHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (e.RowIndex < 1) return;                                  // 表头行(0)无文件路径
            if (e.RowIndex >= _dgv.Rows.Count || _dgv.Rows[e.RowIndex].IsNewRow) return; // 末尾空行
            _rowMenu.Tag = e.RowIndex;
            var local = _dgv.PointToClient(Control.MousePosition);
            _rowMenu.Show(_dgv, local);
        }

        private void MiOpenPart_Click(object sender, EventArgs e)
        {
            if (_rowMenu.Tag is int row) OpenRowPart(row);
        }

        /// <summary>按行打开零部件文件：路径 = 文件路径(列C) + 零件名(列D) + .ics。
        /// E 列「文件类型」是设计环境类别（零件/装配/钣金，给人看的展示信息），**不能当文件扩展名用**——
        /// 否则会拼出「xxx.钣金」这种不存在的路径（与 BatchProcessor.RowFile 的既定口径保持一致；
        /// CAXA 3D 的零件/装配/钣金文档一律 .ics，图纸才是 .exb）。</summary>
        private void OpenRowPart(int row)
        {
            if (row < 1 || row >= _dgv.Rows.Count || _dgv.Rows[row].IsNewRow) return;
            string dir = _dgv.Rows[row].Cells[TableService.ColFilePath].Value?.ToString() ?? "";
            string partName = _dgv.Rows[row].Cells[TableService.ColPartName].Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(partName)) { AppendLog("该行零件名为空，无法打开。"); return; }
            string full = Path.Combine(dir, partName + ".ics");
            if (!File.Exists(full)) { AppendLog("文件不存在，无法打开：" + full); return; }
            try { Process.Start(full); AppendLog("已打开本行零部件：" + full); }
            catch (Exception ex) { AppendLog("打开失败：" + ex.Message); }
        }

        // ---------------------------------------------------------------- 列标题右键：替换

        private void MiReplace_Click(object sender, EventArgs e)
        {
            if (_colMenu.Tag is int col)
            {
                using var dlg = new ReplaceDialog(_dgv, col, MarkDirty, AppendLog);
                dlg.ShowDialog(this);
            }
        }

        // ---------------------------------------------------------------- 关闭

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _stopRequested = true;
        }
    }
}
