using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dsheetmaster
{
    /// <summary>
    /// 折弯管理大师主界面：连接状态区 + 折弯数据表（统一设置 / 单行修改）+ 运行日志区。
    /// 长任务统一使用进度提示 + 停止按钮，停止时绝不保存文件、绝不关闭图纸。
    /// </summary>
    [ComVisible(false)]
    public sealed class MainForm : Form
    {
        // 规则 19：运行态/停止态用静态字段，一个进程内同一时刻只允许一条长链路
        private static bool _running;
        private static volatile bool _stopRequested;

        private readonly IZBaseApp _siteApp;
        private CaxaSession _session;
        private DateTime _lastConnectAttempt = DateTime.MinValue;
        private readonly List<BendRowInfo> _rows = new List<BendRowInfo>();
        private bool _suppressFlagEdit;   // 程序回填勾选列时置位，避免重复触发写入
        private string _lastDocName;      // 上次探测到的设计环境（用于检测切换）
        private string _lastPartName;     // 上次探测到的选中零部件名（用于检测切换）

        private Label _lblConn;
        private Label _lblFile;
        private Button _btnTopMost;
        private Button _btnRefreshInfo;

        private ComboBox _cboGlobalMode;
        private NumericUpDown _numGlobalValue;
        private Button _btnApplyAll;
        private Button _btnReload;
        private Button _btnSave;
        private Button _btnRegen;
        private Button _btnKCalc;
        private Button _btnStop;
        private ProgressBar _progress;
        private Label _lblStatus;

        // 表格缓存：钣金件数量与是否已完成过一次扫描（用于第二行状态文案）
        private int _partsInList;
        private bool _scanned;

        private DataGridView _grid;
        private TextBox _txtLog;
        private Button _btnSaveLog;
        private Button _btnClearLog;
        private Timer _timer;

        // ↓↓↓ 键盘归属抢救：Ctrl+C/V/X/A/Z 归本窗口自己的文本框（机理/开关/取证见 Caxa3dMyTools.Contracts.ChordRescue）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Caxa3dMyTools.Contracts.ChordRescue.Install(this, msg => Diagnostics.Trace(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void WndProc(ref Message m) { if (Caxa3dMyTools.Contracts.ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑↑↑

        public MainForm(IZBaseApp siteApp)
        {
            _siteApp = siteApp;
            BuildUi();
            ApplyBuildStampToTitle();
        }

        #region 界面构建

        private void BuildUi()
        {
            Text = "CAXA 3D 折弯管理大师";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = new Size(1200, 780);
            MinimumSize = new Size(1000, 640);
            Font = SafeFont(9F);
            BackColor = Color.FromArgb(246, 248, 251);
            DoubleBuffered = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 168F));
            Controls.Add(root);

            root.Controls.Add(BuildInfoPanel(), 0, 0);
            root.Controls.Add(BuildGridPanel(), 0, 1);
            root.Controls.Add(BuildLogPanel(), 0, 2);

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => RefreshConnectionInfo(false);
            _timer.Start();

            Diagnostics.UiLogSink = AppendLog;
        }

        private Font SafeFont(float size)
        {
            try { return new Font("Microsoft YaHei UI", size); }
            catch
            {
                try { return new Font("Microsoft YaHei", size); }
                catch { return new Font(FontFamily.GenericSansSerif, size); }
            }
        }

        private Control BuildInfoPanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.White,
                Padding = new Padding(10, 6, 10, 6)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300F));

            _lblConn = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = "连接状态：正在检测…",
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _lblFile = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = "文件：—",
                ForeColor = Color.FromArgb(60, 60, 60),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            left.Controls.Add(_lblConn, 0, 0);
            left.Controls.Add(_lblFile, 0, 1);
            panel.Controls.Add(left, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 6, 0, 0)
            };

            _btnRefreshInfo = MakeButton("刷新状态", 96);
            _btnRefreshInfo.Click += (s, e) => RefreshConnectionInfo(true);
            _btnTopMost = MakeButton("窗口前置：开", 110);
            _btnTopMost.Click += (s, e) => ToggleTopMost();
            TopMost = true;
            ApplyTopMostVisual();

            buttons.Controls.Add(_btnRefreshInfo);
            buttons.Controls.Add(_btnTopMost);
            panel.Controls.Add(buttons, 1, 0);
            return panel;
        }

        private Control BuildGridPanel()
        {
            var group = new GroupBox
            {
                Text = "钣金折弯数据列表",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                BackColor = Color.White
            };

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight
            };

            bar.Controls.Add(new Label
            {
                Text = "统一设置：",
                AutoSize = true,
                Margin = new Padding(2, 10, 2, 0)
            });

            _cboGlobalMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 108,
                Margin = new Padding(0, 7, 6, 0)
            };
            _cboGlobalMode.Items.AddRange(new object[] { "K因子", "折弯扣除" });
            _cboGlobalMode.SelectedIndex = 0;
            _cboGlobalMode.SelectedIndexChanged += (s, e) => UpdateGlobalValueHint();
            bar.Controls.Add(_cboGlobalMode);

            _numGlobalValue = new NumericUpDown
            {
                DecimalPlaces = 4,
                Increment = 0.01M,
                Minimum = 0M,
                Maximum = 100M,
                Width = 110,
                TextAlign = HorizontalAlignment.Right,
                Margin = new Padding(0, 7, 8, 0),
                Value = 0.45M
            };
            CaxaAutomation.RemoveUpDownArrows(_numGlobalValue);
            // 直接键入：获得焦点/点击时全选，覆盖旧值即可输入
            _numGlobalValue.Enter += (s, e) => SelectAllGlobalValue();
            _numGlobalValue.MouseDown += (s, e) => SelectAllGlobalValue();
            bar.Controls.Add(_numGlobalValue);

            _btnApplyAll = MakeButton("统一修改", 96);
            _btnApplyAll.Click += (s, e) => ApplyAll();
            bar.Controls.Add(_btnApplyAll);

            _btnReload = MakeButton("重新加载", 96);
            _btnReload.Click += (s, e) => ReloadAll();
            bar.Controls.Add(_btnReload);

            _btnSave = MakeButton("保存图纸", 96);
            _btnSave.Click += (s, e) => SaveDoc();
            bar.Controls.Add(_btnSave);

            _btnRegen = MakeButton("重新生成", 96);
            _btnRegen.Click += (s, e) => Regenerate();
            bar.Controls.Add(_btnRegen);

            _btnKCalc = MakeButton("K因子计算", 100);
            _btnKCalc.Click += (s, e) => ShowKFactorCalc();
            bar.Controls.Add(_btnKCalc);

            _btnStop = MakeButton("停止", 72);
            _btnStop.ForeColor = Color.Firebrick;
            _btnStop.Enabled = false;
            _btnStop.Click += (s, e) => RequestStop();
            bar.Controls.Add(_btnStop);

            _progress = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Width = 130,
                Height = 16,
                Margin = new Padding(10, 12, 6, 0),
                Visible = false
            };
            bar.Controls.Add(_progress);

            _lblStatus = new Label
            {
                AutoSize = true,
                Text = "就绪",
                ForeColor = Color.FromArgb(80, 80, 80),
                Margin = new Padding(4, 10, 0, 0)
            };
            bar.Controls.Add(_lblStatus);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
                EditMode = DataGridViewEditMode.EditOnEnter,
                ColumnHeadersHeight = 46,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                RowTemplate = { Height = 26 },
                EnableHeadersVisualStyles = false
            };
            _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(238, 242, 248);
            _grid.ColumnHeadersDefaultCellStyle.Font = SafeFont(9F);
            _grid.DefaultCellStyle.Font = SafeFont(9F);
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(238, 242, 248);
            _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.Black;

            AddTextColumn("序号", 44, true, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn("折弯特征名称", 130, true, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn("系统名称", 150, true, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn("折弯角度(°)", 90, true, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn("板材厚度(mm)", 100, true, DataGridViewAutoSizeColumnMode.None);

            // 5 使用零件的最小折弯半径（可编辑下拉；选「是」自动把内半径写成零件最小半径）
            AddFlagColumn("使用零件的最小折弯半径", 170);
            // 6 内半径（用户可直接改，改完立即写入 CAXA）
            AddTextColumn("内半径(mm)", 96, false, DataGridViewAutoSizeColumnMode.None);

            // 7 使用零件K系数（下拉框，用户可直接改）
            AddFlagColumn("使用零件K系数", 120);

            AddTextColumn("折弯系数类型", 90, true, DataGridViewAutoSizeColumnMode.None);
            AddTextColumn("折弯系数数值", 90, true, DataGridViewAutoSizeColumnMode.None);

            var newTypeCol = new DataGridViewComboBoxColumn
            {
                HeaderText = "新类型",
                Width = 90,
                FlatStyle = FlatStyle.Flat,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            newTypeCol.Items.AddRange("K因子", "折弯扣除");
            _grid.Columns.Add(newTypeCol);

            AddTextColumn("新数值", 90, false, DataGridViewAutoSizeColumnMode.None);

            var btnCol = new DataGridViewButtonColumn
            {
                HeaderText = "修改",
                Text = "修改",
                UseColumnTextForButtonValue = true,
                Width = 64,
                FlatStyle = FlatStyle.System
            };
            _grid.Columns.Add(btnCol);

            AddTextColumn("结果", 120, true, DataGridViewAutoSizeColumnMode.None);

            _grid.CellContentClick += GridCellContentClick;
            _grid.CellEndEdit += GridCellEndEdit;

            var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            host.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            host.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            host.Controls.Add(bar, 0, 0);
            host.Controls.Add(_grid, 0, 1);
            group.Controls.Add(host);
            return group;
        }

        private void AddTextColumn(string header, int width, bool readOnly,
            DataGridViewAutoSizeColumnMode mode, float fillWeight = 100F)
        {
            var col = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                Width = width,
                ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                AutoSizeMode = mode
            };
            if (mode == DataGridViewAutoSizeColumnMode.Fill) col.FillWeight = fillWeight;
            _grid.Columns.Add(col);
        }

        /// <summary>勾选列（是/否），允许用户直接在下拉框里改，改完立即写入 CAXA。</summary>
        private void AddFlagColumn(string header, int width)
        {
            var col = new DataGridViewComboBoxColumn
            {
                HeaderText = header,
                Width = width,
                FlatStyle = FlatStyle.Flat,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox
            };
            col.Items.AddRange("是", "否");
            _grid.Columns.Add(col);
        }

        private Control BuildLogPanel()
        {
            var group = new GroupBox
            {
                Text = "运行日志",
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                BackColor = Color.White
            };

            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(252, 252, 252),
                Font = SafeFont(8.5F)
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            _btnSaveLog = MakeButton("保存日志", 96);
            _btnSaveLog.Click += (s, e) => SaveLogFile();
            _btnClearLog = MakeButton("清理日志", 96);
            _btnClearLog.Click += (s, e) => _txtLog.Clear();
            buttons.Controls.Add(_btnSaveLog);
            buttons.Controls.Add(_btnClearLog);

            group.Controls.Add(_txtLog);
            group.Controls.Add(buttons);
            return group;
        }

        private Button MakeButton(string text, int width)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = 28,
                Margin = new Padding(4, 6, 0, 0),
                FlatStyle = FlatStyle.System,
                UseVisualStyleBackColor = true
            };
        }

        private void ToggleTopMost()
        {
            TopMost = !TopMost;
            ApplyTopMostVisual();
            AppendLog(TopMost ? "窗口前置：已置顶。" : "窗口前置：已取消置顶。");
        }

        /// <summary>窗口前置按钮状态：置顶时蓝底白字（参考 caxa3dmytoolsaddin 工具箱）。</summary>
        private void ApplyTopMostVisual()
        {
            if (_btnTopMost == null) return;
            _btnTopMost.Text = TopMost ? "窗口前置：开" : "窗口前置：关";
            _btnTopMost.FlatStyle = FlatStyle.Flat;
            _btnTopMost.FlatAppearance.BorderSize = 1;
            if (TopMost)
            {
                _btnTopMost.BackColor = Color.FromArgb(0, 120, 212);
                _btnTopMost.ForeColor = Color.White;
                _btnTopMost.FlatAppearance.BorderColor = Color.FromArgb(0, 90, 158);
            }
            else
            {
                _btnTopMost.BackColor = SystemColors.Control;
                _btnTopMost.ForeColor = Color.Black;
                _btnTopMost.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
            }
        }

        /// <summary>数值框全选：用户点击后直接键入即可覆盖（不必先按退格删除）。</summary>
        private void SelectAllGlobalValue()
        {
            try
            {
                if (_numGlobalValue == null) return;
                TextBox tb = null;
                foreach (Control c in _numGlobalValue.Controls)
                {
                    tb = c as TextBox;
                    if (tb != null) break;
                }
                if (tb == null) return;
                BeginInvoke((Action)(() =>
                {
                    try { tb.Focus(); tb.SelectAll(); } catch { }
                }));
            }
            catch { }
        }

        private void ApplyBuildStampToTitle()
        {
            try
            {
                string path = Assembly.GetExecutingAssembly().Location;
                DateTime stamp = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
                if (stamp != DateTime.MinValue)
                    Text = "CAXA 3D 折弯管理大师   (build " +
                        stamp.ToString("yyyy-MM-dd HH:mm") + ")";
            }
            catch { }
        }

        private void UpdateGlobalValueHint()
        {
            bool kMode = _cboGlobalMode.SelectedIndex == 0;
            _numGlobalValue.DecimalPlaces = kMode ? 4 : 3;
            _numGlobalValue.Increment = kMode ? 0.01M : 0.05M;
            _numGlobalValue.Maximum = kMode ? 2M : 100M;
            if (kMode) _numGlobalValue.Value = 0.45M;
            else _numGlobalValue.Value = 0.8M;
        }

        #endregion

        #region 连接与状态

        private bool EnsureConnected()
        {
            if (_session != null && CaxaAutomation.Ping(_session)) return true;

            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }
            try
            {
                _lastConnectAttempt = DateTime.Now;
                _session = CaxaAutomation.ConnectFromSite(_siteApp);
                AppendLog("已连接 CAXA 3D（进程内 vtable 直调）。");
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("连接 CAXA 3D 失败：" + ex.Message);
                ReturnFocusToCaxa();
                return false;
            }
        }

        private void RefreshConnectionInfo(bool manual)
        {
            if (IsDisposed) return;
            if (_running) return;

            Diagnostics.Suppress = true;
            try
            {
                if (_session == null)
                {
                    // 定时探测失败后 5 秒内不重复尝试，避免日志刷屏
                    if (!manual &&
                        _lastConnectAttempt != DateTime.MinValue &&
                        (DateTime.Now - _lastConnectAttempt).TotalSeconds < 5)
                    {
                        SetConnState(false, "连接状态：未连接 CAXA 3D（可点「刷新状态」重试）");
                        return;
                    }
                    if (!EnsureConnected())
                    {
                        SetConnState(false, "连接状态：未连接 CAXA 3D（可点「刷新状态」重试）");
                        _lblFile.Text = "文件：—";
                        return;
                    }
                }

                if (!CaxaAutomation.Ping(_session))
                {
                    _session.Dispose();
                    _session = null;
                    SetConnState(false, "连接状态：连接已失效（CAXA 3D 可能已重启）");
                    _lblFile.Text = "文件：—";
                    return;
                }

                string docName;
                SelectedInfo info;
                CaxaAutomation.ProbeSessionInfo(_session, out docName, out info);

                if (string.IsNullOrEmpty(docName))
                {
                    SetConnState(true, "设计环境：未打开设计场景");
                    _lblFile.Text = "折弯列表：无法扫描（请先打开装配或钣金件）";
                    // 设计环境关闭时清空表格残留
                    if (_scanned && _rows.Count > 0) ClearGridData();
                    _lastDocName = null;
                    _lastPartName = null;
                    return;
                }

                string decorated = DecorateElementLine(docName, info);
                SetConnState(true, "设计环境：" + decorated);
                _lblFile.Text = ListSummary();
                if (manual) AppendLog("当前：" + decorated);

                // 选中零部件变化检测：满足加载条件就立即自动加载；否则清空旧数据
                string partKey = info.HasElement ? (info.Name ?? "") : "";
                bool selectionChanged = !string.Equals(docName, _lastDocName, StringComparison.Ordinal)
                    || !string.Equals(partKey, _lastPartName, StringComparison.Ordinal);
                _lastDocName = docName;
                _lastPartName = partKey;

                if (selectionChanged && !_running)
                {
                    if (CanLoadBends(docName, info))
                    {
                        AppendLog("检测到选中零部件变化（" + info.Name + "），自动加载折弯数据…");
                        ReloadAll();
                    }
                    else if (IsInnerFeature(info))
                    {
                        // 选中零部件内部特征（板料/草图/折弯等）时不动表格，沿用当前数据
                    }
                    else if (_rows.Count > 0)
                    {
                        ClearGridData();
                        AppendLog("当前选中对象不满足加载条件，旧数据已清空。");
                    }
                }
            }
            catch (Exception ex)
            {
                if (manual) AppendLog("状态探测失败：" + ex.Message);
            }
            finally
            {
                Diagnostics.Suppress = false;
            }
        }

        private static bool IsPartLike(int type)
        {
            return type == 1 || type == 2 || type == 20 || type == 25 || type == 28 || type == 29;
        }

        /// <summary>是否为「零部件内部特征」（板料/草图/折弯等），这类选中不应刷新或清空表格。</summary>
        private static bool IsInnerFeature(SelectedInfo info)
        {
            if (info == null || !info.HasElement) return false;
            // 0/1/2=场景或装配，20=钣金件（后者已在 CanLoadBends 里另判）
            return info.Type != 0 && info.Type != 1 && info.Type != 2 && info.Type != 20;
        }

        /// <summary>规则 18：文件.ics---零部件名；无外连接的元素补「（虚拟）」。</summary>
        private static string DecorateElementLine(string docName, SelectedInfo info)
        {
            if (!info.HasElement)
                return docName + "---未选中零部件";

            string line = docName + "---" + info.Name;
            if (info.ExternallyLinked) line += "（外部链接）";
            else if (IsPartLike(info.Type)) line += "（虚拟）";
            line += "  [" + info.Kind + "]";
            return line;
        }

        /// <summary>
        /// 严格的自动加载条件：文件名.ics---零件名（虚拟），且零件名 == 文件名、非外链、非展开件。
        /// </summary>
        private static bool CanLoadBends(string docName, SelectedInfo info)
        {
            if (string.IsNullOrEmpty(docName) || info == null || !info.HasElement) return false;
            if (info.Type != 20) return false;                 // 必须是钣金件
            if (info.ExternallyLinked) return false;           // 外链件不加载
            string name = info.Name ?? "";
            if (name.Length == 0) return false;
            if (name.IndexOf("展开", StringComparison.Ordinal) >= 0) return false;  // 展开件不加载
            return string.Equals(DocBaseName(docName), name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>取文档名的纯文件名（去目录、去 .ics 后缀）。</summary>
        private static string DocBaseName(string docName)
        {
            if (string.IsNullOrEmpty(docName)) return "";
            string n = docName;
            int slash = Math.Max(n.LastIndexOf('\\'), n.LastIndexOf('/'));
            if (slash >= 0) n = n.Substring(slash + 1);
            if (n.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 4);
            return n;
        }

        private void SetConnState(bool ok, string text)
        {
            _lblConn.Text = text;
            _lblConn.ForeColor = ok ? Color.FromArgb(0, 120, 60) : Color.Firebrick;
        }

        private void SetStatus(string text)
        {
            if (!IsDisposed) _lblStatus.Text = text;
        }

        /// <summary>第二行状态文案：折弯列表的加载汇总。</summary>
        private string ListSummary()
        {
            if (!_scanned)
                return "折弯列表：未加载（点「重新加载」扫描当前设计场景）";
            return string.Format("折弯列表：钣金件 {0} 个 / 折弯 {1} 条（点「重新加载」刷新）",
                _partsInList, _rows.Count);
        }

        private void SetBusy(bool busy)
        {
            _btnReload.Enabled = !busy;
            _btnApplyAll.Enabled = !busy;
            _btnSave.Enabled = !busy;
            _btnRegen.Enabled = !busy;
            _btnKCalc.Enabled = !busy;
            _cboGlobalMode.Enabled = !busy;
            _numGlobalValue.Enabled = !busy;
            _grid.Enabled = !busy;
            _btnStop.Enabled = busy;
            _progress.Visible = busy;
            Application.DoEvents();
        }

        private void RequestStop()
        {
            _stopRequested = true;
            AppendLog("已请求停止：将中断当前流程，不会保存文件、不会关闭图纸。");
            SetStatus("正在停止…");
            _btnStop.Enabled = false;
        }

        /// <summary>长任务节拍回调：刷新进度文字 + 抽空消息泵 + 响应停止。</summary>
        private void PumpProgress(int done, string message)
        {
            if (IsDisposed) throw new OperationStoppedException();
            if (_stopRequested) throw new OperationStoppedException("已停止");
            SetStatus(message);
            Application.DoEvents();
            if (_stopRequested) throw new OperationStoppedException("已停止");
        }

        private bool StopRequested
        {
            get { return _stopRequested || IsDisposed; }
        }

        #endregion

        #region 扫描与填充

        private void ReloadAll()
        {
            if (_running) return;
            if (!EnsureConnected()) return;

            // 载入限制：必须选中钣金件(type=20，无论是否虚拟)才允许加载
            Diagnostics.Suppress = true;
            string docName;
            SelectedInfo info;
            CaxaAutomation.ProbeSessionInfo(_session, out docName, out info);
            Diagnostics.Suppress = false;

            if (string.IsNullOrEmpty(docName))
            {
                ClearGridData();
                AppendLog("无活动设计场景，表格数据已清空。");
                _lblFile.Text = "折弯列表：无活动设计场景";
                return;
            }
            if (!info.HasElement || info.Type != 20)
            {
                ClearGridData();
                AppendLog(string.IsNullOrEmpty(info.Name)
                    ? "未选中钣金件（只选中设计环境或其他），表格数据已清空。"
                    : "当前选中「" + info.Name + "」（" + info.Kind + "），不是钣金件，表格数据已清空。");
                _lblFile.Text = "折弯列表：请在设计树中选中目标钣金件后再点「重新加载」";
                return;
            }
            if (info.ExternallyLinked)
            {
                ClearGridData();
                AppendLog("当前选中「" + info.Name + "」是外部链接件；请在对应 .ics 文件内选中该零件后再加载。");
                _lblFile.Text = "折弯列表：外链件不加载（请在 .ics 文件内选中零件）";
                return;
            }
            if ((info.Name ?? "").IndexOf("展开", StringComparison.Ordinal) >= 0)
            {
                ClearGridData();
                AppendLog("当前选中「" + info.Name + "」是展开件，不加载折弯数据。");
                _lblFile.Text = "折弯列表：展开件不加载";
                return;
            }
            if (!string.Equals(DocBaseName(docName), info.Name, StringComparison.OrdinalIgnoreCase))
            {
                ClearGridData();
                AppendLog(string.Format(
                    "零件名「{0}」与文件名「{1}」不一致（要求 文件名.ics---零件名 且零件名=文件名），不加载。",
                    info.Name, DocBaseName(docName)));
                _lblFile.Text = "折弯列表：零件名须与文件名一致";
                return;
            }

            _running = true;
            _stopRequested = false;
            SetBusy(true);
            SetStatus("正在扫描折弯数据…");
            try
            {
                int partCount;
                List<BendRowInfo> rows = CaxaAutomation.ScanBends(
                    _session,
                    msg => AppendLog(msg),
                    (done, msg) => PumpProgress(done, msg),
                    () => StopRequested,
                    out partCount);

                _rows.Clear();
                _rows.AddRange(rows);
                _partsInList = partCount;
                _scanned = true;
                FillGrid();
                if (!IsDisposed) _lblFile.Text = ListSummary();

                AppendLog(string.Format("扫描完成：钣金件 {0} 个，折弯 {1} 条。", partCount, _rows.Count));
                SetStatus("扫描完成：折弯 " + _rows.Count + " 条");
            }
            catch (OperationStoppedException)
            {
                AppendLog("已停止扫描（表格未更新）。");
                SetStatus("已停止");
            }
            catch (Exception ex)
            {
                AppendLog("扫描失败：" + ex.Message);
                SetStatus("扫描失败");
                ReturnFocusToCaxa();
            }
            finally
            {
                _running = false;
                _stopRequested = false;
                SetBusy(false);
            }
        }

        /// <summary>清空表格与缓存数据（设计环境切换/无选中钣金件时调用）。</summary>
        private void ClearGridData()
        {
            _rows.Clear();
            _partsInList = 0;
            _scanned = false;
            _grid.Rows.Clear();
        }

        private void FillGrid()
        {
            _grid.Rows.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                BendRowInfo row = _rows[i];
                int idx = _grid.Rows.Add(
                    (i + 1).ToString(),
                    row.BendName,
                    row.SystemName,
                    row.AngleDeg.ToString("0.##"),
                    row.ThicknessMm.ToString("0.###"),
                    BendRowInfo.FlagText(row.UsePartMinRadius),
                    RadiusText(row),
                    BendRowInfo.FlagText(row.UsePartKFactor),
                    row.TypeText,
                    row.ValueText,
                    row.AllowanceType == 2 ? "折弯扣除" : "K因子",
                    DefaultNewValue(row),
                    "修改",
                    "");
                _grid.Rows[idx].Tag = i;

                if (!row.Writable)
                {
                    _grid.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
                    for (int c = 10; c <= 12; c++) _grid.Rows[idx].Cells[c].ReadOnly = true;
                    _grid.Rows[idx].Cells[13].Value = string.IsNullOrEmpty(row.ReadError)
                        ? "不可写" : "不可写：" + row.ReadError;
                    if (!string.IsNullOrEmpty(row.ReadError))
                        _grid.Rows[idx].Cells[13].ToolTipText = row.ReadError;
                }

                // 不适用（非对应系数类型）的勾选列禁止用户改
                if (row.UsePartKFactor < 0) _grid.Rows[idx].Cells[7].ReadOnly = true;

                // 「使用零件的最小折弯半径」列：无数据时只读
                if (row.UsePartMinRadius < 0)
                    _grid.Rows[idx].Cells[5].ReadOnly = true;

                // 内半径列只读判定：
                //   UsePartMinRadius == 1（是）→ 只读（正在使用零件最小半径）
                //   UsePartMinRadius == 0（否） → 仅自定义K因子状态(IsCustomK)才允许改
                //   其他（如折弯扣除类型、用零件K因子）→ 只读
                bool radiusLocked;
                if (row.UsePartMinRadius == 1)
                    radiusLocked = true;
                else if (row.UsePartMinRadius == 0)
                    radiusLocked = !row.IsCustomK;
                else
                    radiusLocked = true;
                if (radiusLocked)
                {
                    _grid.Rows[idx].Cells[6].ReadOnly = true;
                    _grid.Rows[idx].Cells[6].Style.BackColor = Color.FromArgb(245, 245, 245);
                }
            }
            AutoFitColumnWidths();
            if (_rows.Count == 0)
                AppendLog("当前设计场景未发现钣金折弯（请确认已打开含钣金件的装配/零件）。");
        }

        private static string RadiusText(BendRowInfo row)
        {
            return row.Writable ? row.RadiusMm.ToString("0.###") : "";
        }

        /// <summary>
        /// 按表头文字与单元格实际内容计算列宽；总宽超出控件时由水平滚动条调节。
        /// 勾选框类列（是/否/—）不适合文本测量，给固定合理宽度。
        /// </summary>
        private void AutoFitColumnWidths()
        {
            if (_grid == null || _grid.Columns.Count == 0) return;
            for (int c = 0; c < _grid.Columns.Count; c++)
            {
                DataGridViewColumn col = _grid.Columns[c];
                string header = col.HeaderText ?? "";
                int headerNeed = TextRenderer.MeasureText(header,
                    _grid.ColumnHeadersDefaultCellStyle.Font).Width + 24;
                int cellNeed = 0;
                for (int r = 0; r < _grid.Rows.Count; r++)
                {
                    string text = Convert.ToString(_grid.Rows[r].Cells[c].Value) ?? "";
                    int w = TextRenderer.MeasureText(text, _grid.DefaultCellStyle.Font).Width + 18;
                    if (w > cellNeed) cellNeed = w;
                }
                // 表头过长时限制在 200px（表头可折行），实际内容宽度优先
                int need = Math.Max(cellNeed, Math.Min(headerNeed, 200));
                int min = col is DataGridViewButtonColumn ? 60
                    : (col is DataGridViewComboBoxColumn ? 96 : 52);
                col.Width = Math.Min(260, Math.Max(min, need));
            }
            AutoFitHeaderHeight();
        }

        /// <summary>
        /// 按列宽与表头文字实测行高：折行后自动加高表头，保证两行/三行文字都完整显示。
        /// </summary>
        private void AutoFitHeaderHeight()
        {
            if (_grid == null || _grid.Columns.Count == 0) return;
            Font font = _grid.ColumnHeadersDefaultCellStyle.Font ?? _grid.Font;
            int maxHeight = 0;
            for (int c = 0; c < _grid.Columns.Count; c++)
            {
                DataGridViewColumn col = _grid.Columns[c];
                string text = col.HeaderText ?? "";
                int avail = Math.Max(24, col.Width - 10);
                Size sz = TextRenderer.MeasureText(text, font, new Size(avail, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                if (sz.Height > maxHeight) maxHeight = sz.Height;
            }
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersHeight = Math.Max(30, Math.Min(maxHeight + 12, 96));
        }

        private static string DefaultNewValue(BendRowInfo row)
        {
            if (!row.Writable) return "";
            if (row.AllowanceType == 2) return row.DeductionMm.ToString("0.###");
            double k = row.CustomKUsed ? row.CustomK : row.PartKFactor;
            if (k <= 0 || k > 2) k = 0.45;
            return k.ToString("0.0000");
        }

        #endregion

        #region 统一修改 / 单行修改 / 保存 / 重新生成

        private void ApplyAll()
        {
            if (_running) return;
            if (!EnsureConnected()) return;

            bool kMode = _cboGlobalMode.SelectedIndex == 0;
            double value = (double)_numGlobalValue.Value;

            if (kMode && (value <= 0 || value > 2))
            {
                MessageBox.Show(this, "K因子应在 (0, 2] 之间。", "参数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!kMode && value <= 0)
            {
                MessageBox.Show(this, "折弯扣除必须大于 0（单位 mm）。", "参数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string tip = kMode
                ? string.Format("将把当前场景内所有钣金件的 K 因子统一设为 {0:0.####}，\n并让所有折弯改用零件 K（关闭折弯上的自定义 K）。\n\n是否继续？", value)
                : string.Format("将把当前场景内所有折弯的系数类型改为「折弯扣除」，扣除值统一为 {0:0.###} mm。\n\n是否继续？", value);
            if (MessageBox.Show(this, tip, "统一修改确认",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            _running = true;
            _stopRequested = false;
            SetBusy(true);
            SetStatus("正在写入折弯系数…");
            try
            {
                ApplyResult res = CaxaAutomation.ApplyGlobal(
                    _session, kMode, value,
                    msg => AppendLog(msg),
                    (done, msg) => PumpProgress(done, msg),
                    () => StopRequested);

                AppendLog("统一修改结果：" + res.Summary);
                foreach (string f in res.Failures) AppendLog("  · " + f);

                // 回读刷新表格，显示落盘后的真实值
                SetStatus("正在回读校验…");
                int partCount;
                List<BendRowInfo> rows = CaxaAutomation.ScanBends(
                    _session, msg => AppendLog(msg),
                    (done, msg) => PumpProgress(done, msg),
                    () => StopRequested, out partCount);
                _rows.Clear();
                _rows.AddRange(rows);
                _partsInList = partCount;
                _scanned = true;
                FillGrid();
                if (!IsDisposed) _lblFile.Text = ListSummary();

                SetStatus("统一修改完成：" + res.Summary);
                if (res.Failed > 0)
                    MessageBox.Show(this,
                        "部分折弯写入未生效，详情见运行日志。\n\n" + res.Summary,
                        "统一修改未全部成功", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (OperationStoppedException)
            {
                AppendLog("已停止写入（未保存文件）。已写入的部分如需保留，请点「保存图纸」。");
                SetStatus("已停止");
            }
            catch (Exception ex)
            {
                AppendLog("统一修改失败：" + ex.Message);
                SetStatus("统一修改失败");
                ReturnFocusToCaxa();
            }
            finally
            {
                _running = false;
                _stopRequested = false;
                SetBusy(false);
            }
        }

        private void GridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            // 0序号 1名称 2系统名 3角度 4板材厚度 5使用零件的最小折弯半径 6内半径
            // 7使用零件K系数 8当前类型 9当前数值
            // 10新类型 11新数值 12修改 13结果
            if (e.RowIndex < 0 || e.ColumnIndex != 12) return;
            ApplyRow(e.RowIndex);
        }

        private void ApplyRow(int gridRowIndex)
        {
            if (_running) return;
            if (!EnsureConnected()) return;
            if (gridRowIndex < 0 || gridRowIndex >= _grid.Rows.Count) return;

            object tag = _grid.Rows[gridRowIndex].Tag;
            int rowIndex = tag is int ? (int)tag : gridRowIndex;
            if (rowIndex < 0 || rowIndex >= _rows.Count) return;

            BendRowInfo row = _rows[rowIndex];
            if (!row.Writable) return;

            string modeText = Convert.ToString(_grid.Rows[gridRowIndex].Cells[10].Value);
            bool kMode = !string.Equals(modeText, "折弯扣除", StringComparison.Ordinal);
            string raw = Convert.ToString(_grid.Rows[gridRowIndex].Cells[11].Value);
            double value;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                MessageBox.Show(this, "新数值无法解析为数字：" + raw, "参数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (kMode && (value <= 0 || value > 2))
            {
                MessageBox.Show(this, "K因子应在 (0, 2] 之间。", "参数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!kMode && value <= 0)
            {
                MessageBox.Show(this, "折弯扣除必须大于 0（单位 mm）。", "参数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _running = true;
            _stopRequested = false;
            SetBusy(true);
            SetStatus("正在写入单条折弯…");
            try
            {
                string error;
                ApplyResult res = CaxaAutomation.ApplyOneBend(
                    _session, row, kMode, value, msg => AppendLog(msg), out error);

                DataGridViewCell statusCell = _grid.Rows[gridRowIndex].Cells[13];
                if (error == null)
                {
                    statusCell.Value = "已生效";
                    statusCell.Style.ForeColor = Color.FromArgb(0, 120, 60);

                    // 写入已回读校验：ApplyOneBend 已把折弯回读值刷进 row，
                    // 这里把「使用零件折弯扣除」「当前类型/当前数值」同步回表格
                    if (kMode && row.AllowanceType != 2)
                    {
                        row.AllowanceType = 1;
                        row.CustomKUsed = true;
                        row.CustomK = value;
                    }
                    _grid.Rows[gridRowIndex].Cells[5].Value = BendRowInfo.FlagText(row.UsePartMinRadius);
                    _grid.Rows[gridRowIndex].Cells[6].Value = RadiusText(row);
                    _grid.Rows[gridRowIndex].Cells[7].Value = BendRowInfo.FlagText(row.UsePartKFactor);
                    _grid.Rows[gridRowIndex].Cells[8].Value = row.TypeText;
                    _grid.Rows[gridRowIndex].Cells[9].Value = row.ValueText;

                    AppendLog(string.Format("单条修改成功：零件「{0}」折弯「{1}」→ {2} {3}",
                        row.PartName, row.BendName, kMode ? "K因子" : "折弯扣除", value));
                    SetStatus("单条修改已生效（回读确认）");
                }
                else
                {
                    statusCell.Value = "未生效";
                    statusCell.Style.ForeColor = Color.Firebrick;
                    AppendLog("单条修改未生效：" + error);
                    SetStatus("单条修改未生效");
                    MessageBox.Show(this, "写入未生效：\n\n" + error, "未生效",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ReturnFocusToCaxa();
                }
            }
            catch (Exception ex)
            {
                AppendLog("单条修改异常：" + ex.Message);
                SetStatus("单条修改异常");
                ReturnFocusToCaxa();
            }
            finally
            {
                _running = false;
                _stopRequested = false;
                SetBusy(false);
            }
        }

        /// <summary>
        /// 单条勾选/内半径改动立即写入 CAXA。
        /// 5=使用零件的最小折弯半径（下拉）；6=内半径（直接输入数值）；
        /// 7=使用零件K系数。
        /// </summary>
        private void GridCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppressFlagEdit) return;
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex != 5 && e.ColumnIndex != 6 && e.ColumnIndex != 7) return;

            object tag = _grid.Rows[e.RowIndex].Tag;
            int rowIndex = tag is int ? (int)tag : e.RowIndex;
            if (rowIndex < 0 || rowIndex >= _rows.Count) return;
            BendRowInfo row = _rows[rowIndex];
            int gridRow = e.RowIndex;
            int col = e.ColumnIndex;

            if (col == 6)
            {
                string radiusText = Convert.ToString(_grid.Rows[gridRow].Cells[6].Value) ?? "";
                BeginInvoke((Action)(() => ApplyRadius(gridRow, rowIndex, radiusText)));
                return;
            }

            string text = Convert.ToString(_grid.Rows[gridRow].Cells[col].Value) ?? "";
            if (text != "是" && text != "否") return;
            if (!row.Writable) { RefreshFlagRow(gridRow, row); return; }

            bool want = text == "是";

            // 第5列：使用零件的最小折弯半径（是/否切换）
            if (col == 5)
            {
                int current = row.UsePartMinRadius;
                if (current >= 0 && (current == 1) == want) return;   // 没变化
                BeginInvoke((Action)(() => ApplyMinRadiusFlag(gridRow, rowIndex, want)));
                return;
            }

            // 第7列：使用零件K系数（是/否切换）
            if (col == 7)
            {
                int cur = row.UsePartKFactor;
                if (cur >= 0 && (cur == 1) == want) return;
                BeginInvoke((Action)(() => ApplyFlag(gridRow, rowIndex, col, want)));
            }
        }

        /// <summary>用户在「内半径」列直接改数值：写 set_InsideRadius 并回读校验。</summary>
        private void ApplyRadius(int gridRowIndex, int rowIndex, string raw)
        {
            if (_running) return;
            BendRowInfo row = _rows[rowIndex];
            double radiusMm;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out radiusMm)
                || radiusMm <= 0)
            {
                MessageBox.Show(this, "内半径必须是大于 0 的数字（单位 mm）。", "参数无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshFlagRow(gridRowIndex, row);
                return;
            }
            if (Math.Abs(radiusMm - row.RadiusMm) < 1e-6) return;   // 没改

            // 内半径修改权限：UsePartMinRadius=否 + 自定义K因子状态才允许
            if (row.UsePartMinRadius == 1)
            {
                MessageBox.Show(this,
                    "该折弯正在使用零件的最小折弯半径（该列显示「是」），不允许单独修改内半径。\n"
                    + "请先把「使用零件的最小折弯半径」改成「否」。",
                    "不可修改", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshFlagRow(gridRowIndex, row);
                return;
            }
            if (row.UsePartMinRadius != 0)
            {
                MessageBox.Show(this,
                    "「使用零件的最小折弯半径」状态未知（「—」），无法安全修改内半径。",
                    "不可修改", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshFlagRow(gridRowIndex, row);
                return;
            }
            if (!row.IsCustomK)
            {
                string reason = row.AllowanceType == 2
                    ? "当前系数类型为「折弯扣除」，不是自定义K因子状态。"
                    : "当前使用零件K系数，不是自定义K因子状态。";
                MessageBox.Show(this,
                    "只有在「自定义K因子」状态下才允许修改内半径。\n" + reason,
                    "不可修改", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshFlagRow(gridRowIndex, row);
                return;
            }
            if (!EnsureConnected()) { RefreshFlagRow(gridRowIndex, row); return; }

            _running = true;
            _stopRequested = false;
            SetBusy(true);
            SetStatus("正在写入内半径…");
            try
            {
                string error;
                CaxaAutomation.ApplyOneBend(_session, row, false, radiusMm, 3,
                    msg => AppendLog(msg), out error);

                bool sameAsMin = !double.IsNaN(row.PartMinRadiusMm)
                    && Math.Abs(row.RadiusMm - row.PartMinRadiusMm) < 1e-6;
                row.UsePartMinRadius = sameAsMin ? 1 : (double.IsNaN(row.PartMinRadiusMm) ? -1 : 0);
                RefreshFlagRow(gridRowIndex, row);

                DataGridViewCell statusCell = _grid.Rows[gridRowIndex].Cells[13];
                if (error == null)
                {
                    statusCell.Value = sameAsMin ? "已生效（内半径=零件最小值）" : "已生效";
                    statusCell.Style.ForeColor = Color.FromArgb(0, 120, 60);
                    AppendLog(string.Format("内半径修改成功：零件「{0}」折弯「{1}」→ {2:0.###} mm",
                        row.PartName, row.BendName, radiusMm));
                    SetStatus("内半径已生效（回读确认）");
                }
                else
                {
                    statusCell.Value = "未生效";
                    statusCell.Style.ForeColor = Color.Firebrick;
                    AppendLog("内半径修改未生效：" + error);
                    SetStatus("内半径修改未生效");
                    MessageBox.Show(this, "写入未生效：\n\n" + error, "未生效",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                AppendLog("内半径修改异常：" + ex.Message);
                SetStatus("内半径修改异常");
                RefreshFlagRow(gridRowIndex, row);
            }
            finally
            {
                _running = false;
                _stopRequested = false;
                SetBusy(false);
            }
        }

        /// <summary>
        /// 用户切换「使用零件的最小折弯半径」：选「是」→ 把内半径写成零件最小折弯半径；
        /// 选「否」→ 类型库无该开关，仅解锁内半径列让用户直接改。
        /// </summary>
        private void ApplyMinRadiusFlag(int gridRowIndex, int rowIndex, bool want)
        {
            if (_running) return;
            BendRowInfo row = _rows[rowIndex];

            if (!row.Writable) { RefreshFlagRow(gridRowIndex, row); return; }
            if (double.IsNaN(row.PartMinRadiusMm))
            {
                // 零件没有最小折弯半径数据（板料 minR=0 或未配置）
                DataGridViewCell statusCell = _grid.Rows[gridRowIndex].Cells[13];
                statusCell.Value = "零件无最小半径数据";
                statusCell.Style.ForeColor = Color.FromArgb(180, 100, 0);
                AppendLog(string.Format(
                    "零件「{0}」无最小折弯半径数据（板料 minR=0 或未配置），无法切「是」。",
                    row.PartName));
                RefreshFlagRow(gridRowIndex, row);
                return;
            }

            if (want)
            {
                // 选「是」→ 把内半径写成零件最小折弯半径
                if (Math.Abs(row.RadiusMm - row.PartMinRadiusMm) < 1e-6)
                {
                    // 已经相等，直接刷新
                    row.UsePartMinRadius = 1;
                    RefreshFlagRow(gridRowIndex, row);
                    _grid.Rows[gridRowIndex].Cells[13].Value = "已是零件最小折弯半径";
                    _grid.Rows[gridRowIndex].Cells[13].Style.ForeColor = Color.FromArgb(0, 120, 60);
                    return;
                }
                if (!EnsureConnected()) { RefreshFlagRow(gridRowIndex, row); return; }

                _running = true;
                _stopRequested = false;
                SetBusy(true);
                SetStatus("正在写入内半径（改用零件最小折弯半径）…");
                try
                {
                    string error;
                    CaxaAutomation.ApplyOneBend(_session, row, false, row.PartMinRadiusMm, 3,
                        msg => AppendLog(msg), out error);

                    row.UsePartMinRadius = 1;
                    RefreshFlagRow(gridRowIndex, row);

                    DataGridViewCell statusCell = _grid.Rows[gridRowIndex].Cells[13];
                    if (error == null)
                    {
                        statusCell.Value = "已改用零件最小折弯半径 " + row.PartMinRadiusMm.ToString("0.###") + " mm";
                        statusCell.Style.ForeColor = Color.FromArgb(0, 120, 60);
                        AppendLog(string.Format(
                            "内半径已改用零件最小值：零件「{0}」折弯「{1}」→ {2:0.###} mm",
                            row.PartName, row.BendName, row.PartMinRadiusMm));
                        SetStatus("已改用零件最小折弯半径");
                    }
                    else
                    {
                        statusCell.Value = "未生效";
                        statusCell.Style.ForeColor = Color.Firebrick;
                        AppendLog("改用零件最小半径未生效：" + error);
                        SetStatus("改用零件最小半径未生效");
                        MessageBox.Show(this, "写入未生效：\n\n" + error, "未生效",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("切换「使用零件的最小折弯半径」异常：" + ex.Message);
                    SetStatus("切换异常");
                    RefreshFlagRow(gridRowIndex, row);
                }
                finally
                {
                    _running = false;
                    _stopRequested = false;
                    SetBusy(false);
                }
            }
            else
            {
                // 选「否」→ 类型库无该开关，只改界面状态让用户能改内半径
                row.UsePartMinRadius = 0;
                RefreshFlagRow(gridRowIndex, row);

                DataGridViewCell statusCell = _grid.Rows[gridRowIndex].Cells[13];
                if (Math.Abs(row.RadiusMm - row.PartMinRadiusMm) < 1e-6)
                {
                    // 内半径仍等于零件最小值，提示用户需要自己改
                    statusCell.Value = "请直接改内半径值（当前仍=零件最小值）";
                    statusCell.Style.ForeColor = Color.FromArgb(180, 100, 0);
                }
                else
                {
                    statusCell.Value = "已切换（内半径可编辑）";
                    statusCell.Style.ForeColor = Color.FromArgb(0, 120, 60);
                }
                AppendLog(string.Format(
                    "切换「使用零件的最小折弯半径」=否：零件「{0}」折弯「{1}」（类型库无此开关，仅解锁内半径列）",
                    row.PartName, row.BendName));
                SetStatus("已切换「否」，内半径可编辑");
            }
        }

        private void RefreshRadiusRow(int gridRowIndex, BendRowInfo row)
        {
            // 委托给 RefreshFlagRow（它已包含 Cells[5]/[6] 的刷新逻辑）
            RefreshFlagRow(gridRowIndex, row);
        }

        private void ApplyFlag(int gridRowIndex, int rowIndex, int col, bool want)
        {
            if (_running) return;
            if (!EnsureConnected()) { RefreshFlagRow(gridRowIndex, _rows[rowIndex]); return; }

            BendRowInfo row = _rows[rowIndex];
            // 仅剩「使用零件K系数」勾选列：是=沿用零件K；否=改成本折弯自定义K
            int kFlagSet = want ? 1 : 2;
            bool kMode = true;
            double value = 0;

            _running = true;
            _stopRequested = false;
            SetBusy(true);
            SetStatus("正在切换「使用零件K系数」…");
            try
            {
                string error;
                CaxaAutomation.ApplyOneBend(_session, row, kMode, value, kFlagSet,
                    msg => AppendLog(msg), out error);

                RefreshFlagRow(gridRowIndex, row);
                DataGridViewCell statusCell = _grid.Rows[gridRowIndex].Cells[13];
                if (error == null)
                {
                    _grid.Rows[gridRowIndex].Cells[8].Value = row.TypeText;
                    _grid.Rows[gridRowIndex].Cells[9].Value = row.ValueText;
                    statusCell.Value = "已生效";
                    statusCell.Style.ForeColor = Color.FromArgb(0, 120, 60);
                    AppendLog(string.Format("勾选修改成功：零件「{0}」折弯「{1}」使用零件K系数 → {2}",
                        row.PartName, row.BendName, want ? "是" : "否"));
                    SetStatus("勾选修改已生效（回读确认）");
                }
                else
                {
                    statusCell.Value = "未生效";
                    statusCell.Style.ForeColor = Color.Firebrick;
                    AppendLog("勾选修改未生效：" + error);
                    SetStatus("勾选修改未生效");
                    MessageBox.Show(this, "写入未生效：\n\n" + error, "未生效",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                AppendLog("勾选修改异常：" + ex.Message);
                SetStatus("勾选修改异常");
                RefreshFlagRow(gridRowIndex, row);
            }
            finally
            {
                _running = false;
                _stopRequested = false;
                SetBusy(false);
            }
        }

        /// <summary>把「使用零件的最小折弯半径 / 内半径 / 两列勾选」按回读结果刷回表格（不触发再次写入）。</summary>
        private void RefreshFlagRow(int gridRowIndex, BendRowInfo row)
        {
            if (_grid == null || gridRowIndex < 0 || gridRowIndex >= _grid.Rows.Count) return;
            _suppressFlagEdit = true;
            try
            {
                _grid.Rows[gridRowIndex].Cells[5].Value = BendRowInfo.FlagText(row.UsePartMinRadius);
                _grid.Rows[gridRowIndex].Cells[5].ReadOnly = row.UsePartMinRadius < 0;
                _grid.Rows[gridRowIndex].Cells[6].Value = RadiusText(row);

                // 内半径只读判定（与 FillGrid 一致）
                bool radiusLocked;
                if (row.UsePartMinRadius == 1)
                    radiusLocked = true;
                else if (row.UsePartMinRadius == 0)
                    radiusLocked = !row.IsCustomK;
                else
                    radiusLocked = true;
                _grid.Rows[gridRowIndex].Cells[6].ReadOnly = radiusLocked;
                _grid.Rows[gridRowIndex].Cells[6].Style.BackColor = radiusLocked
                    ? Color.FromArgb(245, 245, 245) : Color.White;

                _grid.Rows[gridRowIndex].Cells[7].Value = BendRowInfo.FlagText(row.UsePartKFactor);
                _grid.Rows[gridRowIndex].Cells[7].ReadOnly = row.UsePartKFactor < 0;
            }
            finally
            {
                _suppressFlagEdit = false;
            }
        }

        /// <summary>打开 K 因子计算器（纯计算，不写入任何数据）。</summary>
        private void ShowKFactorCalc()
        {
            using (var f = new KFactorCalcForm())
                f.ShowDialog(this);
        }

        private void SaveDoc()
        {
            if (_running) return;
            if (!EnsureConnected()) return;
            try
            {
                CaxaAutomation.SaveActiveDoc(_session);
                AppendLog("图纸已保存（IZDoc.Save 返回 hr=0）。");
                SetStatus("已保存");
            }
            catch (Exception ex)
            {
                AppendLog("保存失败：" + ex.Message);
                SetStatus("保存失败");
                MessageBox.Show(this, "保存失败：\n\n" + ex.Message, "保存失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                ReturnFocusToCaxa();
            }
        }

        private void Regenerate()
        {
            if (_running) return;
            if (!EnsureConnected()) return;

            if (MessageBox.Show(this,
                "将调用「重新生成」刷新所有零部件几何。\n大装配可能需要较长时间，期间界面会短暂无响应（该调用无法中断）。\n\n是否继续？",
                "重新生成确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            _running = true;
            _stopRequested = false;
            SetBusy(true);
            SetStatus("正在重新生成零部件（可能需要较长时间）…");
            DateTime begin = DateTime.Now;
            try
            {
                Application.DoEvents();
                CaxaAutomation.RegenerateParts(_session);
                CaxaAutomation.RefreshScene(_session);
                double sec = (DateTime.Now - begin).TotalSeconds;
                AppendLog(string.Format("重新生成完成，耗时 {0:0.0} 秒。", sec));
                SetStatus(string.Format("重新生成完成（{0:0.0}s）", sec));
            }
            catch (Exception ex)
            {
                AppendLog("重新生成失败：" + ex.Message);
                SetStatus("重新生成失败");
                ReturnFocusToCaxa();
            }
            finally
            {
                _running = false;
                SetBusy(false);
            }
        }

        #endregion

        #region 探针

        /// <summary>
        /// 导出探针报告：把当前场景的钣金件属性、折弯线、折弯对象属性全量 dump 到桌面文件。
        /// 用于折弯抓取异常时定位具体槽位/接口问题。
        /// </summary>
        private void RunProbe()
        {
            if (_running) return;
            if (!EnsureConnected()) return;

            _running = true;
            _stopRequested = false;
            SetBusy(true);
            SetStatus("正在导出探针数据…");
            try
            {
                string path = CaxaAutomation.ProbeDump(
                    _session,
                    msg => AppendLog(msg),
                    (done, msg) => PumpProgress(done, msg),
                    () => StopRequested);

                if (!string.IsNullOrEmpty(path))
                {
                    AppendLog("探针完成：" + path);
                    SetStatus("探针完成（报告已导出）");
                }
                else
                {
                    AppendLog("探针未产出报告，详见运行日志。");
                    SetStatus("探针失败");
                }
            }
            catch (OperationStoppedException)
            {
                AppendLog("已停止探针（报告可能未写入）。");
                SetStatus("已停止");
            }
            catch (Exception ex)
            {
                AppendLog("探针异常：" + ex.Message);
                SetStatus("探针异常");
                ReturnFocusToCaxa();
            }
            finally
            {
                _running = false;
                _stopRequested = false;
                SetBusy(false);
            }
        }

        #endregion

        #region 日志

        private void AppendLog(string message)
        {
            if (IsDisposed) return;
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine;

            if (_txtLog.InvokeRequired)
            {
                try { _txtLog.BeginInvoke((Action)(() => AppendLog(message))); } catch { }
                return;
            }
            _txtLog.AppendText(line);
            if (_txtLog.TextLength > 400000)
            {
                _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 300000);
                _txtLog.SelectionStart = _txtLog.TextLength;
            }
        }

        private void SaveLogFile()
        {
            using (var dlg = new SaveFileDialog
            {
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                FileName = "caxa3dsheetmaster_log_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, _txtLog.Text,
                        new System.Text.UTF8Encoding(true));
                    AppendLog("日志已导出：" + dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出日志失败：" + ex.Message, "导出失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        #endregion

        #region 焦点与生命周期

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// 把前台还给 CAXA 主窗口（规则 7#12）：不能 SetFocus 到 CAXA 子窗，否则会触发焦点风暴。
        /// </summary>
        private void ReturnFocusToCaxa()
        {
            try
            {
                IntPtr target = CaxaMainWindow();
                if (target != IntPtr.Zero && target != Handle)
                    SetForegroundWindow(target);
            }
            catch { }
        }

        private static IntPtr CaxaMainWindow()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                {
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            AppendLog("折弯管理大师已启动。临时日志：" + Diagnostics.LogPath);
            // RefreshConnectionInfo 内部已按「选中零部件变化」自动加载，这里不再重复 ReloadAll
            RefreshConnectionInfo(true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _stopRequested = true;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口关闭");
            try { if (_timer != null) { _timer.Stop(); _timer.Dispose(); } } catch { }
            Diagnostics.UiLogSink = null;
            if (_session != null) { _session.Dispose(); _session = null; }
            base.OnFormClosed(e);
        }

        #endregion
    }
}
