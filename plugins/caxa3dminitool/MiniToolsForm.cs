using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>
    /// 小工具合集主窗口：
    ///  - 顶部「窗口前置」是 TopMost 开关（默认锁定在最上层），右侧显示当前选中；
    ///  - 主体与主工具箱同款四列表格：单元格即一个工具，单击（或 Enter/Space）执行；
    ///    列 = 工具分类，行 = 分组内序号，列宽按最长文字实测，不折行、不缩字号；
    ///  - 日志区显示每一步的业务回读结果。
    /// 所有成功判定只看业务侧回读（OperationType / KernelType / IncludedInBOM 等是否真的变了）。
    /// </summary>
    [ComVisible(false)]
    internal sealed class MiniToolsForm : Form
    {
        private readonly IZBaseApp _app;

        private bool _topMost = true;
        private Button _btnTopMost;
        private Label _lblSelection;
        private Timer _selTimer;
        private const string BaseTitle = "CAXA3D小工具合集";

        // ---- 工具表格 ----
        private DataGridView _grid;
        private readonly Dictionary<int, ToolEntry> _cellTools = new Dictionary<int, ToolEntry>();
        private DataGridViewCell _hoverCell;

        private const int ColCount = 4;
        private const int CellPaddingX = 12;
        private const int RowHeight = 36;
        private const int HeaderHeight = 38;
        private const int MinColumnWidth = 170;
        private const int BodyPadding = 12;
        private const int TopPanelHeight = 70;
        private const int LogHeight = 180;

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

        private TextBox _txtLog;
        private List<IZElement> _kernelQueue;
        private Timer _kernelTimer;
        private List<IZElement> _kernelChanged;
        private int _kernelIndex;
        private int _kernelTotal;
        private int _kernelOk, _kernelAlready, _kernelSkipped, _kernelFailed;
        private eZKernelType _kernelTarget;
        private bool _kernelStopRequested;
        private bool _kernelRunning;

        public MiniToolsForm(IZBaseApp app)
        {
            _app = app;
            BuildUi();

            TopMost = _topMost;
            ApplyTopMostVisual();

            // 日志区（参照 caxa3dsetMaterial：底部只读 TextBox + UiLogSink）
            Diagnostics.UiLogSink = AppendLog;
            Diagnostics.Log("小工具合集窗口已打开。日志文件：" + Diagnostics.LogPath);

            // 在 Add-in 加载阶段预创建句柄，避免 OnClick 时跨窗口消息互等
            if (!IsHandleCreated) CreateHandle();
        }

        // ================================================================ 界面

        private void BuildUi()
        {
            Text = BaseTitle + "-" + BuildStamp();
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 245, 245);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);

            // ---- 顶部：窗口前置 + 当前选中 ----
            Panel top = new Panel
            {
                Dock = DockStyle.Top,
                Height = TopPanelHeight,
                Padding = new Padding(10, 8, 10, 4),
                BackColor = Color.White
            };

            _btnTopMost = new Button
            {
                Text = "窗口前置",
                Left = 10, Top = 12, Width = 100, Height = 30,
                BackColor = BtnAccent, ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            _btnTopMost.FlatAppearance.BorderSize = 0;
            _btnTopMost.Click += (s, e) => ToggleTopMost();
            top.Controls.Add(_btnTopMost);

            _lblSelection = new Label
            {
                Text = "当前选中：读取中…",
                Left = 120, Top = 8, Width = 900, Height = 44,
                AutoSize = false,
                AutoEllipsis = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 90, 170)
            };
            top.Controls.Add(_lblSelection);

            // ---- 主体：与主工具箱同款四列表格（单元格 = 工具） ----
            Panel body = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(BodyPadding),
                BackColor = Color.FromArgb(245, 245, 245)
            };
            _grid = BuildGrid();
            body.Controls.Add(_grid);

            // ---- 日志区（Dock Bottom，剩余空间给表格） ----
            Panel logHost = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = LogHeight,
                Padding = new Padding(10, 0, 10, 10)
            };
            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Color.FromArgb(252, 252, 252),
                Font = new Font("Consolas", 9F)
            };
            logHost.Controls.Add(_txtLog);

            Controls.Add(body);
            Controls.Add(logHost);
            Controls.Add(top);

            FillTable();
            ApplyTableSize();

            _selTimer = new Timer { Interval = 2000 };
            _selTimer.Tick += (s, e) => RefreshSelectionLabel();

            // 内核切换：定时器分片处理，保证界面可响应、表格可用停止
            _kernelTimer = new Timer { Interval = 60 };
            _kernelTimer.Tick += (s, e) => OnKernelTimerTick();
        }

        private DataGridView BuildGrid()
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Fill,
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

        /// <summary>四列工具清单：列 = 分类，行 = 组内序号。</summary>
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
                    new ToolEntry { Title = "全选内部链接零件", Tip = "扫描树中内部链接零件并加入选区", Run = SelectInternalLinkedParts }
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
                    new ToolEntry { Title = "将零部件添加到BOM", Tip = "把选中零部件全部纳入 BOM 并回读 IncludedInBOM", Run = AddSelectedToBom }
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

            int rowCount = Math.Max(1, _grid.Rows.Count);
            int gridHeight = HeaderHeight + rowCount * RowHeight + BodyPadding * 2 + 12;
            int clientWidth = Math.Max(900, totalMin + 48);
            ClientSize = new Size(clientWidth, TopPanelHeight + gridHeight + LogHeight + 8);
            MinimumSize = new Size(clientWidth + 16, TopPanelHeight + gridHeight + LogHeight);
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

        private static string BuildStamp()
        {
            try
            {
                string path = Assembly.GetExecutingAssembly().Location;
                return File.GetLastWriteTime(path).ToString("yyyyMMddHHmm");
            }
            catch
            {
                return DateTime.Now.ToString("yyyyMMddHHmm");
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshSelectionLabel();
            if (_selTimer != null) _selTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 关窗即置停止标志：不强行保存文件、不强关图纸
            _kernelStopRequested = true;
            _kernelRunning = false;
            if (_kernelTimer != null) { _kernelTimer.Stop(); _kernelTimer.Dispose(); _kernelTimer = null; }
            if (_selTimer != null) { _selTimer.Stop(); _selTimer.Dispose(); _selTimer = null; }
            Diagnostics.UiLogSink = null;
            base.OnFormClosed(e);
        }

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
                CurveTool.ImportWireFromText(_app, path, out error, out pointCount, out crvId, out detail);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[导入3D曲线] " + error);
                    return;
                }

                Diagnostics.Log("导入3D曲线成功：" + detail);
                RefreshSelectionLabel();
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
                List<AnchorTool.Outcome> list = AnchorTool.MoveAnchorToCog(_app, out error, out summary);

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
                RefreshSelectionLabel();
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
                List<BomTool.Outcome> list = BomTool.AddSelectedToBom(_app, out error, out summary);

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
                RefreshSelectionLabel();
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
                List<PointTool.Outcome> list = PointTool.GetPickedPointCoordinates(_app, out error, out summary);

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
                RefreshSelectionLabel();
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

        // ================================================================ 日志

        private void AppendLog(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(AppendLog), line); } catch { }
                return;
            }
            _txtLog.AppendText(line + Environment.NewLine);
        }

        // ================================================================ 窗口前置（TopMost 开关）

        private void ToggleTopMost()
        {
            _topMost = !_topMost;
            TopMost = _topMost;
            ApplyTopMostVisual();
            Diagnostics.Log(_topMost ? "窗口已锁定在最上层" : "窗口已取消最上层");
        }

        private void ApplyTopMostVisual()
        {
            _btnTopMost.BackColor = _topMost ? Color.FromArgb(0, 120, 212) : SystemColors.Control;
            _btnTopMost.ForeColor = _topMost ? Color.White : SystemColors.ControlText;
            _btnTopMost.FlatStyle = FlatStyle.Flat;
            _btnTopMost.FlatAppearance.BorderSize = _topMost ? 0 : 1;
        }

        // ================================================================ 选中状态

        private void RefreshSelectionLabel()
        {
            if (IsDisposed) return;
            try
            {
                _lblSelection.Text = "当前选中：" + FeatureTool.DescribeSelection(_app);
            }
            catch (Exception ex)
            {
                _lblSelection.Text = "当前选中：读取失败 " + ex.Message;
            }
        }

        // ================================================================ 增料 / 除料切换

        private void ToggleSelectedFeature()
        {
            SetToolsEnabled(false);
            try
            {
                string error;
                List<FeatureTool.Outcome> outcomes = FeatureTool.ToggleSelected(_app, out error);

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
                RefreshSelectionLabel();
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
            List<IZElement> leaves = KernelTool.GatherLeafParts(_app, out error, out summary);

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

                    IZSceneDoc scene = FeatureTool.GetSceneDoc(_app);
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
            RefreshSelectionLabel();
        }

        private static string SafeName(IZElement elem)
        {
            if (elem == null) return "?";
            try { return string.IsNullOrEmpty(elem.Name) ? "?" : elem.Name; } catch { return "?"; }
        }

        // ================================================================ 元素工具（压缩 / 包围盒 / 内部链接）

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
                ElementsTool.UnsuppressSelected(_app, out error, out summary,
                    out total, out unsuppressed, out already, out failed, out verified);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[选中取消压缩] " + error);
                    return;
                }

                Diagnostics.Log(string.Format(
                    "选中取消压缩：{0}；解除 {1}，本就未压缩 {2}，失败 {3}；回读已解除 {4}",
                    summary, unsuppressed, already, failed, verified));

                RefreshSelectionLabel();
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
                ElementsTool.ResetSizeBoxes(_app, out error, out reset, out failed, out noBox, out verified);
                if (!string.IsNullOrEmpty(error)) Diagnostics.Log("[更新包围盒] " + error);
                RefreshSelectionLabel();
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
                int total = ElementsTool.SelectInternalLinked(_app, out error, out selectedAfter);
                if (!string.IsNullOrEmpty(error)) Diagnostics.Log("[全选内部链接零件] " + error);
                if (total > 0)
                    Diagnostics.Log(string.Format("全选内部链接零件：命中 {0} 个，选区回读 {1} 个", total, selectedAfter));
                RefreshSelectionLabel();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("全选内部链接零件异常：" + ex);
            }
        }
    }
}
