using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dsetMaterial
{
    /// <summary>
    /// 材料工具主窗口（正式版）：
    ///  - 顶部「窗口前置」是 TopMost 开关（默认锁定在最上层，对齐 caxa3dmytoolsaddin 铁律）；
    ///  - 中部为材料库表格（自定义材料.xml），点行即填入材料/密度/颜色，可直接改单元格后保存；
    ///  - 按钮：写入材料和颜色 / 单独写入材料 / 单独写入颜色 / 随机颜色；
    ///  - 所有成功判定只看业务侧回读（材料名、密度、自定义属性、颜色）。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly IZBaseApp _app;
        private readonly MaterialDb _db;

        private bool _topMost = true;
        private Button _btnTopMost;
        private Button _btnRead;
        private Button _btnWriteBoth;
        private Button _btnWriteMaterial;
        private Button _btnWriteColor;
        private Button _btnRandom;
        private Button _btnSaveDb;
        private Button _btnColor;
        private TextBox _txtName;
        private TextBox _txtDensity;
        private TextBox _txtColor;
        private CheckBox _chkOnly;

        // ---- 材料库标签条（自建：原生 TabControl 装不下时会带出一对左右翻页按钮，且无法关掉） ----
        private Panel _toolbar;          // 滑动条 + 搜索框 + 加入常用 + 保存材料库
        private Panel _stripClip;        // 裁剪容器（不自动滚动，靠滑动条平移内层）
        private Panel _stripInner;       // 真正摆放标签按钮的长条
        private Panel _contentHost;      // 当前标签页的表格
        private HScrollBar _tabScroll;
        private TextBox _txtSearch;
        private Button _btnSearch;
        private Font _tabFontBold;
        private ToolTip _tip;
        private readonly List<TabEntry> _tabs = new List<TabEntry>();
        private int _currentTab = -1;

        // 「搜索结果」临时页的数据：只装上一次搜索命中的那些**对象本身**（与主库同一批实例），
        // 所以这一页只供参考、不增删行；重新搜索即替换，零命中时整页消失。
        private BindingList<MaterialItem> _searchResults;

        private Button _btnFav;
        private TextBox _txtLog;
        private Button _btnCard;
        private Button _btnRule;
        private ColorCardForm _colorCard;
        private readonly Dictionary<string, Label> _propLabels = new Dictionary<string, Label>();
        private const string BaseTitle = "CAXA 3D 材料工具";

        /// <summary>一个标签页 = 标签按钮 + 它自己的表格（数据源是主库的同一批对象）。</summary>
        private sealed class TabEntry
        {
            public string Title;
            public Button TabButton;
            public DataGridView Grid;
            public Func<MaterialItem, bool> Match;
            /// <summary>临时页（搜索结果）：不属于材料库语义，搜索定位时不当作候选落点。</summary>
            public bool Temporary;
        }

        // ↓↓↓ 键盘归属抢救：Ctrl+C/V/X/A/Z 归本窗口自己的文本框（机理/开关/取证见 Caxa3dMyTools.Contracts.ChordRescue）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Caxa3dMyTools.Contracts.ChordRescue.Install(this, msg => Diagnostics.Trace(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void WndProc(ref Message m) { if (Caxa3dMyTools.Contracts.ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑↑↑

        public MainForm(IZBaseApp app)
        {
            _app = app;
            _db = new MaterialDb();
            BuildUi();

            TopMost = _topMost;
            ApplyTopMostVisual();

            Diagnostics.UiLogSink = AppendLog;
            Diagnostics.Log("材料工具窗口已打开。材料库：" + _db.FilePath);

            string msg = _db.Load();
            RebuildTabs();
            if (!string.IsNullOrEmpty(msg)) AppendLog(msg);
            SetStatus("材料库 " + _db.Items.Count + " 条 / " + _tabs.Count + " 个标签页", true);

            // 在 Add-in 加载阶段预创建句柄，避免 OnClick 时跨窗口消息互等
            if (!IsHandleCreated) CreateHandle();
        }

        // ================================================================ 界面

        private void BuildUi()
        {
            Text = BaseTitle;
            ClientSize = new Size(940, 700);
            MinimumSize = new Size(820, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            _tabFontBold = new Font(Font, FontStyle.Bold);

            // ---- 顶部输入区（加入常用/保存材料库已移到标签条上方的工具栏） ----
            Panel top = new Panel { Dock = DockStyle.Top, Height = 122, Padding = new Padding(10, 8, 10, 4) };

            _btnTopMost = MakeButton("窗口前置", 10, 8, 100);
            _btnTopMost.Click += (s, e) => ToggleTopMost();
            top.Controls.Add(_btnTopMost);

            _btnRead = MakeButton("读取选中件材料", 120, 8, 124);
            _btnRead.Click += (s, e) => ReadSelection();
            top.Controls.Add(_btnRead);

            _btnRule = MakeButton("材料属性回填规则", 254, 8, 124);
            _btnRule.Click += (s, e) => OpenBackfillRule();
            top.Controls.Add(_btnRule);

            _btnCard = MakeButton("色卡", 388, 8, 70);
            _btnCard.Click += (s, e) => OpenColorCard();
            top.Controls.Add(_btnCard);

            Label lblHint = new Label
            {
                Text = "① 选中零件 ② 点表格行填入数据 ③ 点写入按钮（窗口默认置顶）",
                Left = 468, Top = 14, Width = 446, Height = 20,
                ForeColor = Color.FromArgb(90, 90, 90),
                AutoSize = false
            };
            top.Controls.Add(lblHint);

            Label lblName = new Label { Text = "材料", Left = 12, Top = 50, Width = 36, Height = 22 };
            top.Controls.Add(lblName);
            _txtName = new TextBox { Left = 50, Top = 47, Width = 230 };
            top.Controls.Add(_txtName);

            Label lblDensity = new Label { Text = "密度(kg/m³)", Left = 290, Top = 50, Width = 76, Height = 22 };
            top.Controls.Add(lblDensity);
            _txtDensity = new TextBox { Left = 368, Top = 47, Width = 88 };
            top.Controls.Add(_txtDensity);

            Label lblColor = new Label { Text = "颜色", Left = 466, Top = 50, Width = 36, Height = 22 };
            top.Controls.Add(lblColor);
            _btnColor = new Button { Left = 504, Top = 46, Width = 52, Height = 25, BackColor = Color.Gray };
            _btnColor.Click += (s, e) => PickColor();
            top.Controls.Add(_btnColor);
            _txtColor = new TextBox { Left = 562, Top = 47, Width = 80 };
            top.Controls.Add(_txtColor);

            _chkOnly = new CheckBox
            {
                Text = "仅此零件使用",
                Left = 652, Top = 48, Width = 104, Height = 22,
                Checked = false
            };
            top.Controls.Add(_chkOnly);

            _btnWriteBoth = MakeButton("写入材料和颜色", 10, 84, 132);
            _btnWriteBoth.Click += (s, e) => WriteSelection(true, true);
            top.Controls.Add(_btnWriteBoth);

            _btnWriteMaterial = MakeButton("单独写入材料", 150, 84, 112);
            _btnWriteMaterial.Click += (s, e) => WriteSelection(true, false);
            top.Controls.Add(_btnWriteMaterial);

            _btnWriteColor = MakeButton("单独写入颜色", 270, 84, 112);
            _btnWriteColor.Click += (s, e) => WriteSelection(false, true);
            top.Controls.Add(_btnWriteColor);

            _btnRandom = MakeButton("随机(选中)", 390, 84, 106);
            _btnRandom.Click += (s, e) => RandomColors(false);
            top.Controls.Add(_btnRandom);

            Button _btnRandomAll = MakeButton("全部随机颜色", 504, 84, 110);
            _btnRandomAll.Click += (s, e) => RandomColors(true);
            top.Controls.Add(_btnRandomAll);

            _btnFav = MakeButton("加入常用", 0, 0, 92);
            _btnFav.Click += (s, e) => ToggleFavorite();

            _btnSaveDb = MakeButton("保存材料库", 0, 0, 104);
            _btnSaveDb.Click += (s, e) => SaveDatabase();

            // 状态反馈移到窗口标题栏，不再单独放 Label 占空间（日志区已有完整反馈）

            // ---- 材料库标签区：滑动条 + 搜索工具栏 → 标签条 → 当前页表格 ----
            _tip = new ToolTip();

            _toolbar = new Panel { Dock = DockStyle.Top, Height = 34 };
            _tabScroll = new HScrollBar { SmallChange = 60 };
            _tabScroll.ValueChanged += (s, e) => OnTabScrollChanged();
            _tip.SetToolTip(_tabScroll, "左右滑动切换标签页（替代原生标签条两端的翻页按钮）");
            _toolbar.Controls.Add(_tabScroll);

            _txtSearch = new TextBox { Height = 23 };
            _txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoSearch(); }
            };
            _tip.SetToolTip(_txtSearch,
                "按通配符搜索「材料」列，材料名没命中的词再查「分类」列（如 铸钢 只存在于分类名）：* 任意长度、? 单个字符（如 Q235* 、 *不锈钢* ）；不含通配符时按包含匹配");
            _toolbar.Controls.Add(_txtSearch);

            _btnSearch = new Button { Text = "搜索", Height = 26 };
            _btnSearch.Click += (s, e) => DoSearch();
            _toolbar.Controls.Add(_btnSearch);
            _toolbar.Controls.Add(_btnFav);
            _toolbar.Controls.Add(_btnSaveDb);
            _toolbar.Resize += (s, e) => LayoutToolbar();

            _stripClip = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = Color.FromArgb(236, 236, 236)
            };
            _stripInner = new Panel { Left = 0, Top = 1, Height = 28 };
            _stripClip.Controls.Add(_stripInner);
            _stripClip.Resize += (s, e) => UpdateStripScroll();

            _contentHost = new Panel { Dock = DockStyle.Fill };

            Panel tabArea = new Panel { Dock = DockStyle.Fill };
            // Dock 布局按 z 序从后往前处理：先加 Fill，再加 Top，才能保证工具栏/标签条各占一条、表格吃剩余
            tabArea.Controls.Add(_contentHost);
            tabArea.Controls.Add(_stripClip);
            tabArea.Controls.Add(_toolbar);

            // 右侧：材料页「属性」表（只读镜像，数据来自材料库 XML；CAXA API 不提供这些量的读写）
            Panel detailPanel = new Panel
            {
                Dock = DockStyle.Right,
                Width = 262,
                Padding = new Padding(6, 4, 0, 0)
            };
            detailPanel.Controls.Add(BuildDetailPanel());

            Panel gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 6) };
            gridHost.Controls.Add(tabArea);
            gridHost.Controls.Add(detailPanel);

            // ---- 日志区 ----
            Panel logHost = new Panel { Dock = DockStyle.Bottom, Height = 168, Padding = new Padding(10, 0, 10, 10) };
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

            Controls.Add(gridHost);
            Controls.Add(logHost);
            Controls.Add(top);
        }

        private Button MakeButton(string text, int left, int top, int width)
        {
            return new Button { Text = text, Left = left, Top = top, Width = width, Height = 30 };
        }

        /// <summary>
        /// 右侧只读属性面板：镜像材料页右下「属性」表的字段。
        /// 这些值 CAXA API 既读不到也写不了（IZPartProperty 只有 MaterialName/MassDensity），
        /// 唯一来源是材料库 XML 的 physicalproperties，选中表格行即展示。
        /// </summary>
        private Control BuildDetailPanel()
        {
            Panel box = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 247, 247), BorderStyle = BorderStyle.FixedSingle };

            Label title = new Label
            {
                Text = "材料属性（只读 · 来自材料库）",
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Padding = new Padding(8, 0, 0, 0),
                BackColor = Color.FromArgb(230, 230, 230)
            };
            box.Controls.Add(title);

            string[][] rows =
            {
                new[] { "model",   "模型类型" },
                new[] { "desc",    "描述" },
                new[] { "ex",      "弹性模量 N/m²" },
                new[] { "nuxy",    "泊松比" },
                new[] { "gxy",     "抗剪模量 N/m²" },
                new[] { "alpx",    "热膨胀系数 /K" },
                new[] { "kx",      "热导率 W/(m·K)" },
                new[] { "c",       "比热 J/(kg·K)" },
                new[] { "sigxt",   "抗拉强度 N/m²" },
                new[] { "sigyld",  "屈服强度 N/m²" },
                new[] { "sigxc",   "抗压强度 N/m²" }
            };

            TableLayoutPanel tbl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = rows.Length,
                Padding = new Padding(6, 6, 6, 6)
            };
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < rows.Length; i++)
                tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            for (int i = 0; i < rows.Length; i++)
            {
                Label cap = new Label
                {
                    Text = rows[i][1],
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Color.FromArgb(80, 80, 80),
                    AutoEllipsis = true
                };
                Label val = new Label
                {
                    Text = "—",
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true
                };
                tbl.Controls.Add(cap, 0, i);
                tbl.Controls.Add(val, 1, i);
                _propLabels[rows[i][0]] = val;
            }

            box.Controls.Add(tbl);
            return box;
        }

        /// <summary>把选中材料的物理属性刷新到右侧面板。</summary>
        private void ShowDetail(MaterialItem it)
        {
            SetDetail("model", it == null ? "" : it.Model);
            SetDetail("desc", it == null ? "" : it.Description);
            SetDetail("ex", it == null ? "" : it.Ex);
            SetDetail("nuxy", it == null ? "" : it.NuXY);
            SetDetail("gxy", it == null ? "" : it.GXY);
            SetDetail("alpx", it == null ? "" : it.Alpx);
            SetDetail("kx", it == null ? "" : it.Kx);
            SetDetail("c", it == null ? "" : it.C);
            SetDetail("sigxt", it == null ? "" : it.SigXt);
            SetDetail("sigyld", it == null ? "" : it.SigYld);
            SetDetail("sigxc", it == null ? "" : it.SigXc);
        }

        private void SetDetail(string key, string text)
        {
            Label lbl;
            if (!_propLabels.TryGetValue(key, out lbl)) return;
            lbl.Text = string.IsNullOrEmpty(text) ? "—" : text;
        }

        /// <summary>日志里显示空值统一用 —。</summary>
        private static string Show(string v)
        {
            return string.IsNullOrEmpty(v) ? "—" : v;
        }

        /// <summary>新建一个材料表格：columns 固定，绑定给定数据源，选择变化即回填上方输入框。</summary>
        private DataGridView CreateGrid(object dataSource, bool editableAddDelete)
        {
            DataGridView grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = editableAddDelete,
                AllowUserToDeleteRows = editableAddDelete,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersWidth = 28,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                BackgroundColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F),
                DataSource = dataSource
            };
            AddGridColumn(grid, "分类", "Category", 80, 12, false);
            AddGridColumn(grid, "材料", "Name", 240, 40, false);
            AddGridColumn(grid, "密度(kg/m³)", "Density", 90, 12, false);
            AddGridColumn(grid, "颜色(RRGGBB)", "Color", 90, 12, false);

            grid.SelectionChanged += (s, e) => FillInputsFromGrid(s as DataGridView);
            return grid;
        }

        private static void AddGridColumn(DataGridView grid, string header, string prop,
                                          int minWidth, int fillWeight, bool readOnly)
        {
            DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = prop,
                MinimumWidth = minWidth,
                FillWeight = fillWeight,
                ReadOnly = readOnly
            };
            col.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.Columns.Add(col);
        }

        /// <summary>
        /// 重建标签条：常用 → 全部 → 各分类（按条数降序）。
        /// 每个页都是 MaterialViewList（与主库同一批对象），所以**任何页都能新增/删除**，
        /// 新增行会按本页语义补默认分类/常用标记，同时写回主库；保存写的是主库这一份。
        /// 重建按标题保留原选中页（保存/加常用后不跳回第一页），并摘掉旧视图对主库的联动订阅。
        /// </summary>
        private void RebuildTabs()
        {
            string keep = (_currentTab >= 0 && _currentTab < _tabs.Count) ? _tabs[_currentTab].Title : null;

            foreach (TabEntry en in _tabs)
            {
                MaterialViewList view = en.Grid == null ? null : en.Grid.DataSource as MaterialViewList;
                if (view != null) view.Detach();
                if (en.Grid != null) en.Grid.Dispose();
                if (en.TabButton != null) en.TabButton.Dispose();
            }
            _tabs.Clear();
            _stripInner.Controls.Clear();
            _contentHost.Controls.Clear();
            _currentTab = -1;

            List<MaterialItem> favorites = new List<MaterialItem>();
            Dictionary<string, List<MaterialItem>> byCat =
                new Dictionary<string, List<MaterialItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (MaterialItem it in _db.Items)
            {
                if (it.Favorite) favorites.Add(it);
                string cat = string.IsNullOrEmpty(it.Category) ? "未分类" : it.Category;
                List<MaterialItem> list;
                if (!byCat.TryGetValue(cat, out list)) { list = new List<MaterialItem>(); byCat[cat] = list; }
                list.Add(it);
            }

            AddTab("常用", it => it.Favorite, it => it.Favorite = true);
            AddTab("全部", null, null);
            if (_searchResults != null && _searchResults.Count > 0)
                AddTab("搜索结果 (" + _searchResults.Count + ")", null, null, _searchResults);

            List<KeyValuePair<string, List<MaterialItem>>> cats =
                new List<KeyValuePair<string, List<MaterialItem>>>(byCat);
            cats.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
            foreach (KeyValuePair<string, List<MaterialItem>> kv in cats)
            {
                string cat = kv.Key;
                AddTab(cat + " (" + kv.Value.Count + ")",
                    it => (string.IsNullOrEmpty(it.Category) ? "未分类" : it.Category)
                            .Equals(cat, StringComparison.OrdinalIgnoreCase),
                    it => it.Category = cat == "未分类" ? "" : cat);
            }

            LayoutStrip();
            int idx = keep == null ? -1 : _tabs.FindIndex(en => en.Title == keep);
            SelectTab(idx >= 0 ? idx : 0);

            Diagnostics.Trace("材料库标签页已重建：常用 " + favorites.Count + " 条，全部 " +
                              _db.Items.Count + " 条，分类 " + cats.Count + " 个");
        }

        /// <summary>建一页：一个标签按钮 + 一张常驻表格（隐藏/显示切换，保住各自的滚动位置与选中行）。</summary>
        private void AddTab(string title, Func<MaterialItem, bool> match, Action<MaterialItem> init)
        {
            AddTab(title, match, init, null);
        }

        /// <summary>
        /// direct != null 时这一页直接绑给定的列表（「搜索结果」临时页用），
        /// 不再包一层过滤视图——那种页的增删会写回主库，临时页不该有这个副作用。
        /// </summary>
        private void AddTab(string title, Func<MaterialItem, bool> match, Action<MaterialItem> init,
                            BindingList<MaterialItem> direct)
        {
            object ds;
            if (direct != null) ds = direct;
            else ds = match == null ? (object)_db.Items : new MaterialViewList(_db.Items, match, init);

            DataGridView grid = CreateGrid(ds, direct == null);
            grid.Visible = false;
            if (direct != null)
            {
                grid.AllowUserToDeleteRows = false;
                _tip.SetToolTip(grid, "临时页：上一次搜索的命中，重新搜索即替换；这一页不能增删行，改动材料库请用分类页或「全部」页");
            }
            _contentHost.Controls.Add(grid);

            Button b = new Button
            {
                Text = title,
                Top = 1,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                TabStop = false,
                BackColor = Color.FromArgb(228, 228, 228),
                ForeColor = Color.FromArgb(70, 70, 70)
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(190, 190, 190);
            if (direct != null)
                _tip.SetToolTip(b, "临时页：上一次搜索命中的 " + direct.Count +
                                  " 条材料，重新搜索即替换；这一页不能增删行，改动材料库请用分类页或「全部」页");
            int index = _tabs.Count;
            b.Click += (s, e) => SelectTab(index);
            _stripInner.Controls.Add(b);

            _tabs.Add(new TabEntry
            {
                Title = title,
                TabButton = b,
                Grid = grid,
                Match = match,
                Temporary = (direct != null)
            });
        }

        /// <summary>按加粗后的文字宽度排标签按钮（选中页会变粗，量宽要按最宽的那态算）。</summary>
        private void LayoutStrip()
        {
            int left = 2;
            foreach (TabEntry en in _tabs)
            {
                Size want = TextRenderer.MeasureText(en.Title, _tabFontBold);
                int w = Math.Max(52, want.Width + 18);
                en.TabButton.Left = left;
                en.TabButton.Width = w;
                left += w + 2;
            }
            _stripInner.Width = left + 2;
            UpdateStripScroll();
        }

        /// <summary>把标签条总宽和可视宽换算成滑动条量程，再同步内层偏移。</summary>
        private void UpdateStripScroll()
        {
            if (_tabScroll == null || _stripClip == null || _stripInner == null) return;
            int visible = _stripClip.ClientSize.Width;
            if (visible <= 0) return;

            int maxScroll = Math.Max(0, _stripInner.Width - visible);
            int large = Math.Max(1, visible);
            // WinForms 夹 LargeChange 的上限取的是“当前 Maximum”，所以必须先给足 Maximum 再设 LargeChange，
            // 反序会让 LargeChange 停在旧量程上，滑动条永远滚不到最后一个标签。
            _tabScroll.Maximum = maxScroll + large - 1;
            _tabScroll.LargeChange = large;

            int value = Math.Min(Math.Max(_tabScroll.Value, 0), maxScroll);
            _tabScroll.Value = value;
            _stripInner.Left = -value;
        }

        /// <summary>滑动条平移标签条（ValueChanged 是唯一入口，键盘/拖动/代码赋值都走这里）。</summary>
        private void OnTabScrollChanged()
        {
            if (_stripInner != null) _stripInner.Left = -_tabScroll.Value;
        }

        /// <summary>切到某一页：显示它的表格、重画标签按钮，并把该按钮滑进可视区。</summary>
        private void SelectTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _currentTab = index;
            for (int i = 0; i < _tabs.Count; i++)
            {
                bool on = (i == index);
                _tabs[i].Grid.Visible = on;
                PaintTab(_tabs[i], on);
            }
            EnsureTabVisible(index);
        }

        private void PaintTab(TabEntry en, bool selected)
        {
            Button b = en.TabButton;
            b.BackColor = selected ? Color.White : Color.FromArgb(228, 228, 228);
            b.ForeColor = selected ? Color.FromArgb(0, 90, 158) : Color.FromArgb(70, 70, 70);
            b.FlatAppearance.BorderColor = selected ? Color.FromArgb(0, 120, 212) : Color.FromArgb(190, 190, 190);
            b.FlatAppearance.BorderSize = selected ? 2 : 1;
            b.Font = selected ? _tabFontBold : Font;
        }

        /// <summary>把选中的标签按钮滚进可视区（搜索定位、加常用后都要用）。</summary>
        private void EnsureTabVisible(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            int visible = _stripClip.ClientSize.Width;
            if (visible <= 0) return;

            Button b = _tabs[index].TabButton;
            int value = _tabScroll.Value;
            if (b.Right > value + visible) value = b.Right - visible;
            if (b.Left < value) value = b.Left;

            int maxScroll = Math.Max(0, _stripInner.Width - visible);
            value = Math.Min(Math.Max(value, 0), maxScroll);
            if (value != _tabScroll.Value) _tabScroll.Value = value;
            OnTabScrollChanged();
        }

        /// <summary>工具栏一行从右往左排：保存材料库 → 加入常用 → 搜索 → 搜索框 → 剩下的宽度给滑动条。</summary>
        private void LayoutToolbar()
        {
            if (_toolbar == null || _btnSaveDb == null) return;
            int w = _toolbar.ClientSize.Width;
            if (w < 260) return;

            int x = w - 2;
            _btnSaveDb.SetBounds(x - 104, 4, 104, 26); x -= 104 + 6;
            _btnFav.SetBounds(x - 92, 4, 92, 26); x -= 92 + 12;
            _btnSearch.SetBounds(x - 56, 4, 56, 26); x -= 56 + 4;
            _txtSearch.SetBounds(x - 150, 6, 150, 23); x -= 150 + 10;
            _tabScroll.SetBounds(0, 9, Math.Max(40, x), 16);

            UpdateStripScroll();
        }

        // ================================================================ 搜索（通配符匹配「材料」列，材料名零命中时再查「分类」列）

        /// <summary>* / ? 转正则；不含通配符时按「包含」处理，避免用户必须记得打星号。</summary>
        private static Regex BuildWildcardRegex(string pattern)
        {
            bool hasWild = pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0;
            StringBuilder sb = new StringBuilder();
            sb.Append('^');
            if (!hasWild) sb.Append(".*");
            foreach (char ch in pattern)
            {
                if (ch == '*') sb.Append(".*");
                else if (ch == '?') sb.Append('.');
                else sb.Append(Regex.Escape(ch.ToString()));
            }
            if (!hasWild) sb.Append(".*");
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>通配符先搜「材料」列，材料名零命中的词再查「分类」列；命中后停在「搜索结果」页并选中第一条。</summary>
        private void DoSearch()
        {
            string pattern = (_txtSearch.Text ?? "").Trim();
            if (pattern.Length == 0)
            {
                Diagnostics.Log("搜索框为空：填材料名或通配符（* 任意长度、? 单个字符），如 Q235* 。");
                SetStatus("搜索条件为空", false);
                return;
            }

            Regex rx;
            try { rx = BuildWildcardRegex(pattern); }
            catch (Exception ex)
            {
                Diagnostics.Log("搜索式无法解析：" + ex.Message);
                SetStatus("搜索式无效", false);
                return;
            }

            DataGridView active = ActiveGrid;
            if (active != null) active.EndEdit();

            List<MaterialItem> hits = new List<MaterialItem>();
            List<MaterialItem> catOnly = new List<MaterialItem>();
            foreach (MaterialItem it in _db.Items)
            {
                if (it == null) continue;
                if (rx.IsMatch((it.Name ?? "").Trim())) hits.Add(it);
                else if (rx.IsMatch((it.Category ?? "").Trim())) catOnly.Add(it);
            }
            int nameHits = hits.Count;
            hits.AddRange(catOnly);   // 材料列命中的排前面：第一条永远是更强的匹配

            if (hits.Count == 0)
            {
                // 上一次的临时页必须跟着消失，否则留着过期结果会误导用户
                if (_searchResults != null && _searchResults.Count > 0)
                {
                    _searchResults.Clear();
                    RebuildTabs();
                }
                Diagnostics.Log("搜索「" + pattern + "」在「材料」列与「分类」列都零命中（材料库共 " + _db.Items.Count + " 条）。");
                SetStatus("搜索零命中", false);
                return;
            }

            ShowResults(hits);   // 必须先重建标签页，后面定位的才是新生成的表格

            MaterialItem hit = hits[0];
            int tab = ResultsTabIndex();
            SelectTab(tab);
            int row = LocateRow(_tabs[tab].Grid, hit);
            // 切页时表格自己会先把第 0 行报一次 SelectionChanged，之后代码改 CurrentCell 不再重复抛这个事件，
            // 所以定位完必须显式回填一次，否则输入框停在第 0 行。
            if (row >= 0) FillInputsFromGrid(_tabs[tab].Grid);

            // 命中来自哪一列要说清楚：像「铸钢」这种只存在于分类列的词，只搜材料名必然零命中
            string scope = catOnly.Count == 0 ? "材料列 " + nameHits + " 条"
                         : nameHits == 0 ? "全部来自「分类」列 " + catOnly.Count + " 条（材料名里没有这个词）"
                         : "材料列 " + nameHits + " 条 + 分类列 " + catOnly.Count + " 条";
            Diagnostics.Log(string.Format(
                "搜索「{0}」命中 {1} 条（{8}）→ 停在「{2}」页第 {3} 行：{4} / {5}  密度={6} 颜色={7}",
                pattern, hits.Count, _tabs[tab].Title, row + 1,
                Show(hit.Category), hit.Name, Show(hit.Density), Show(hit.Color), scope));
            if (hits.Count > 1)
                Diagnostics.Log("  其余 " + (hits.Count - 1) + " 条就在同一页往下翻；要收窄就加长通配式（如 *Q235* → Q235?）");
            SetStatus("搜索命中 " + hits.Count + " 条，已停在「" + _tabs[tab].Title + "」页", true);
        }

        /// <summary>
        /// 把本次命中写进「搜索结果」临时页。装的是主库里**同一批对象**，所以这一页里改单元格
        /// 等于改材料库（与分类页同语义），但增删行已在建表时关掉，临时页不动库。
        /// </summary>
        private void ShowResults(List<MaterialItem> hits)
        {
            if (_searchResults == null) _searchResults = new BindingList<MaterialItem>();
            _searchResults.Clear();
            foreach (MaterialItem it in hits) _searchResults.Add(it);
            RebuildTabs();
        }

        /// <summary>「搜索结果」临时页的下标（`ShowResults` 刚重建过标签页，这一页此时必然在）；兜回「全部」页。</summary>
        private int ResultsTabIndex()
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Temporary) return i;
            }
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Match == null) return i;   // 「全部」
            }
            return 0;
        }

        /// <summary>在页内按对象引用定位行，选中并滚到它上面。</summary>
        private static int LocateRow(DataGridView g, MaterialItem hit)
        {
            if (g == null) return -1;
            for (int i = 0; i < g.Rows.Count; i++)
            {
                if (g.Rows[i].IsNewRow) continue;
                if (!ReferenceEquals(g.Rows[i].DataBoundItem, hit)) continue;
                try
                {
                    g.ClearSelection();
                    g.CurrentCell = g.Rows[i].Cells[0];
                    g.Rows[i].Selected = true;
                    g.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 3);
                }
                catch (Exception ex) { Diagnostics.Trace("定位行失败：" + ex.Message); }
                return i;
            }
            return -1;
        }

        /// <summary>当前标签页里的表格。</summary>
        private DataGridView ActiveGrid
        {
            get { return (_currentTab >= 0 && _currentTab < _tabs.Count) ? _tabs[_currentTab].Grid : null; }
        }

        private MaterialItem SelectedItem
        {
            get
            {
                DataGridView g = ActiveGrid;
                if (g == null || g.CurrentRow == null || g.CurrentRow.IsNewRow) return null;
                return g.CurrentRow.DataBoundItem as MaterialItem;
            }
        }

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

        private void SetStatus(string text, bool ok)
        {
            if (IsDisposed) return;
            Text = BaseTitle + " — " + text;
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

        // ================================================================ 表格 → 输入框

        private void FillInputsFromGrid(DataGridView grid)
        {
            // 其余页的表格是常驻隐藏的，它们的数据联动也会触发 SelectionChanged，不能让它改输入框
            if (grid != ActiveGrid) return;
            if (grid == null || grid.CurrentRow == null || grid.CurrentRow.IsNewRow) return;
            MaterialItem it = grid.CurrentRow.DataBoundItem as MaterialItem;
            if (it == null) return;

            _txtName.Text = it.Name ?? "";
            _txtDensity.Text = it.Density ?? "";
            _txtColor.Text = it.Color ?? "";

            int? c = it.ColorValue();
            _btnColor.BackColor = c.HasValue
                ? Color.FromArgb((c.Value >> 16) & 0xFF, (c.Value >> 8) & 0xFF, c.Value & 0xFF)
                : Color.Gray;

            ShowDetail(it);
        }

        // ================================================================ 动作

        private void ReadSelection()
        {
            try
            {
                string error;
                List<IZElement> elems = MaterialProbe.GetSelection(_app, out error);
                if (elems.Count == 0)
                {
                    Diagnostics.Log(error ?? "没有选中元素。");
                    SetStatus("未选中元素", false);
                    return;
                }

                Diagnostics.Log("选中 " + elems.Count + " 个元素：");
                int withMaterial = 0;
                foreach (IZElement elem in elems)
                {
                    Diagnostics.Log(MaterialProbe.Describe(elem));

                    IZPartProperty prop = elem as IZPartProperty;
                    if (prop != null)
                    {
                        withMaterial++;
                        // 用读到的材料名回查材料库，补出该材料在材料页「属性」表里的物理量
                        string matName = null;
                        try
                        {
                            bool inLib;
                            prop.GetMaterialName(out matName, out inLib);
                        }
                        catch { }

                        MaterialItem hit = _db.FindByName(matName);
                        if (hit != null)
                        {
                            Diagnostics.Log(string.Format(
                                "  材料库命中[{0}]：{1} / {2} > 弹性模量={3} 泊松比={4} 抗剪模量={5} 热膨胀={6} 热导率={7} 比热={8} 抗拉={9} 屈服={10} 抗压={11}",
                                hit.Category, hit.Name, hit.Model,
                                Show(hit.Ex), Show(hit.NuXY), Show(hit.GXY), Show(hit.Alpx),
                                Show(hit.Kx), Show(hit.C), Show(hit.SigXt), Show(hit.SigYld), Show(hit.SigXc)));
                            ShowDetail(hit);
                        }
                        else if (!string.IsNullOrEmpty(matName))
                        {
                            Diagnostics.Log("  材料库无此材料[" + matName + "]，无法给出属性表值（API 不暴露这些量）");
                        }
                    }
                }
                SetStatus("读出 " + elems.Count + " 个元素，其中 " + withMaterial + " 个支持材料属性", true);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("读取选中件材料失败：" + ex.Message);
                SetStatus("读取失败", false);
            }
        }

        /// <summary>打开色卡窗体（单例，非模态，置顶）。</summary>
        private void OpenColorCard()
        {
            if (_colorCard == null || _colorCard.IsDisposed)
            {
                _colorCard = new ColorCardForm(_app, SetColorFromCard);
            }
            _colorCard.Show();
            _colorCard.Activate();
        }

        /// <summary>
        /// 打开材料属性回填规则文件（系统默认编辑器，用户可直接编辑）。
        /// 规则只认双引号内的字段，严格区分大小写；改完保存即生效，无需重启插件。
        /// </summary>
        private void OpenBackfillRule()
        {
            try
            {
                string path = BackfillRule.OpenForEdit();
                Diagnostics.Log("已打开材料属性回填规则：" + path);
                Diagnostics.Log("当前规则字段：" + BackfillRule.FieldsText + "（双引号内字段严格匹配，区分大小写）");
                SetStatus("回填规则 " + BackfillRule.Count + " 个字段", true);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("打开回填规则失败：" + ex.Message);
                MessageBox.Show("打开规则文件失败：" + ex.Message, "材料工具",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>色卡应用成功后回写主窗体颜色输入框（供随后「写入材料和颜色」使用）。</summary>
        private void SetColorFromCard(int rgb)
        {
            if (IsDisposed) return;
            _txtColor.Text = (rgb & 0xFFFFFF).ToString("X6");
            _btnColor.BackColor = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        private void PickColor()
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.AnyColor = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    int rgb = (dlg.Color.R << 16) | (dlg.Color.G << 8) | dlg.Color.B;
                    _txtColor.Text = rgb.ToString("X6");
                    _btnColor.BackColor = dlg.Color;
                }
            }
        }

        /// <summary>解析颜色输入：RRGGBB / #RRGGBB / 0xRRGGBB；空返回 false。</summary>
        private static bool TryParseColor(string text, out int rgb)
        {
            rgb = 0;
            string t = (text ?? "").Trim().TrimStart('#');
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            int v;
            if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
            {
                rgb = v;
                return true;
            }
            return false;
        }

        /// <summary>写入选中件：withMaterial=材料名+密度+材料/SC材料属性；withColor=外观颜色。</summary>
        private void WriteSelection(bool withMaterial, bool withColor)
        {
            string materialName = (_txtName.Text ?? "").Trim();
            double? density = null;
            int? colorRgb = null;

            if (withMaterial)
            {
                if (materialName.Length == 0)
                {
                    MessageBox.Show("请先填材料（或点表格一行自动填入）。", "材料工具",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string densText = (_txtDensity.Text ?? "").Trim();
                if (densText.Length > 0)
                {
                    double d;
                    if (!double.TryParse(densText, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || d <= 0)
                    {
                        MessageBox.Show("密度必须是正数（kg/m³），或留空表示不改密度。", "材料工具",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    density = d;
                }
            }

            if (withColor)
            {
                string colorText = (_txtColor.Text ?? "").Trim();
                if (colorText.Length == 0)
                {
                    MessageBox.Show("请先填颜色（RRGGBB），或点“颜色”按钮取色。", "材料工具",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                int c;
                if (!TryParseColor(colorText, out c))
                {
                    MessageBox.Show("颜色格式应为 RRGGBB（如 C0C0C0）。", "材料工具",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                colorRgb = c;
            }

            try
            {
                string error;
                List<IZElement> elems = MaterialProbe.GetSelection(_app, out error);
                if (elems.Count == 0)
                {
                    Diagnostics.Log(error ?? "没有选中元素。");
                    SetStatus("未选中元素", false);
                    return;
                }

                bool onlyThis = _chkOnly.Checked;
                Diagnostics.Log(string.Format("开始写入（{0}）：材料={1}，密度={2}，颜色={3}，仅此零件={4}，目标 {5} 个元素",
                    withMaterial && withColor ? "材料+颜色" : (withMaterial ? "仅材料" : "仅颜色"),
                    withMaterial ? "\"" + materialName + "\"" : "<不改>",
                    density.HasValue ? density.Value.ToString(CultureInfo.InvariantCulture) : "<不改>",
                    colorRgb.HasValue ? "#" + colorRgb.Value.ToString("X6") : "<不改>",
                    onlyThis, elems.Count));
                if (withMaterial)
                    Diagnostics.Log("回填规则字段：" + BackfillRule.FieldsText + "（双引号内字段严格匹配，区分大小写）");

                int pass = 0, fail = 0;
                StringBuilder report = new StringBuilder();
                foreach (IZElement elem in elems)
                {
                    bool ok;
                    if (!withMaterial)
                        ok = MaterialProbe.ApplyColorOnly(elem, colorRgb.Value, report);
                    else
                        ok = MaterialProbe.Apply(elem, materialName, density, colorRgb, onlyThis, report);

                    if (ok) pass++; else fail++;
                }
                Diagnostics.Log(report.ToString().TrimEnd());

                Diagnostics.Log(string.Format("写入结束：校验通过 {0} 个，未通过 {1} 个。", pass, fail));
                SetStatus(string.Format("写入完成：通过 {0} / 未通过 {1}", pass, fail), fail == 0);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("写入过程异常：" + ex.Message);
                SetStatus("写入异常", false);
            }
        }

        /// <summary>
        /// 随机上色：
        ///  - all=true  → 穿透整个设计树，对所有叶子零件上色（不需要选中）；
        ///  - all=false → 从用户选中的节点开始穿透：零件直接上色，装配体穿透其所有叶子；
        ///                如果没有任何选中，则回退到穿透整个设计树。
        /// </summary>
        private void RandomColors(bool all)
        {
            try
            {
                List<IZElement> elems;
                string error;

                if (all)
                {
                    elems = MaterialProbe.GetAllLeafElements(_app, out error);
                    Diagnostics.Log("全部随机颜色：穿透整个设计树 → " + elems.Count + " 个叶子零件"
                        + (error != null && elems.Count == 0 ? " — " + error : ""));
                }
                else
                {
                    List<IZElement> sel = MaterialProbe.GetSelection(_app, out error);
                    if (sel.Count == 0)
                    {
                        // 没选中任何东西 → 回退穿透全树
                        elems = MaterialProbe.GetAllLeafElements(_app, out error);
                        Diagnostics.Log("随机(选中)：未选中元素，回退穿透全设计树 → " + elems.Count + " 个叶子零件"
                            + (error != null && elems.Count == 0 ? " — " + error : ""));
                    }
                    else
                    {
                        // 从选中节点开始穿透：装配体穿透，零件直接用，去重
                        elems = MaterialProbe.GetLeafElementsFromNodes(sel);
                        Diagnostics.Log("随机(选中)：" + elems.Count + " 个叶子零件");
                    }
                }

                if (elems.Count == 0)
                {
                    SetStatus("没有可上色的零件", false);
                    return;
                }

                Random rnd = new Random(Environment.TickCount);
                StringBuilder report = new StringBuilder();
                int pass = 0, fail = 0;
                foreach (IZElement elem in elems)
                {
                    int rgb;
                    if (MaterialProbe.ApplyRandomColor(elem, rnd, report, out rgb)) pass++; else fail++;
                }
                Diagnostics.Log(report.ToString().TrimEnd());
                Diagnostics.Log(string.Format("随机上色结束：通过 {0} 个，未通过 {1} 个。", pass, fail));
                SetStatus(string.Format("随机上色：通过 {0} / 未通过 {1}", pass, fail), fail == 0);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("随机上色异常：" + ex);
                SetStatus("随机上色异常", false);
            }
        }

        private void SaveDatabase()
        {
            try
            {
                DataGridView g = ActiveGrid;
                if (g != null) g.EndEdit();

                // 清掉只点出空行没填材料名的占位行（否则会作为空条目写进 XML）
                int pruned = 0;
                for (int i = _db.Items.Count - 1; i >= 0; i--)
                {
                    if (string.IsNullOrEmpty((_db.Items[i].Name ?? "").Trim()))
                    {
                        _db.Items.RemoveAt(i);
                        pruned++;
                    }
                }
                if (pruned > 0) Diagnostics.Log("保存前清理空行 " + pruned + " 条");

                _db.Save();
                RebuildTabs();
                SetStatus("材料库已保存（" + _db.Items.Count + " 条）", true);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("保存材料库失败：" + ex.Message);
                SetStatus("保存材料库失败", false);
            }
        }

        /// <summary>把当前行加入/移出「常用」标签页。</summary>
        private void ToggleFavorite()
        {
            MaterialItem it = SelectedItem;
            if (it == null)
            {
                Diagnostics.Log("请先在表格里选中一行，再加入常用。");
                SetStatus("未选中材料行", false);
                return;
            }

            it.Favorite = !it.Favorite;
            RebuildTabs();
            Diagnostics.Log("[" + it.Name + "] " + (it.Favorite ? "已加入常用" : "已移出常用") +
                            "（记得点「保存材料库」持久化）");
            SetStatus(it.Favorite ? "已加入常用" : "已移出常用", true);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口关闭");
            if (_colorCard != null && !_colorCard.IsDisposed) _colorCard.Close();
            Diagnostics.UiLogSink = null;
            base.OnFormClosed(e);
        }
    }
}
