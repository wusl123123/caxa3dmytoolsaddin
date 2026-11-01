using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;

namespace caxa3dmytoolsaddin
{
    /// <summary>
    /// 我的工具箱主窗口：
    ///   - 顶部只有「窗口前置」开关（默认开启，点击可切换）；
    ///   - 主体为三列表格：1.常用插件 / 2.独立插件 / 3.测试插件；
    ///   - 每个插件占一个单元格，单击单元格打开；单元格不折行、不缩小字体，
    ///     列最小宽度按最长文字实测，窗口最小尺寸随之放大，避免文字被遮挡。
    /// </summary>
    [System.Runtime.InteropServices.ComVisible(false)]
    public class MainToolboxForm : Form
    {
        private readonly PluginManager _manager;
        private bool _topMost = true;   // 默认前置

        private Button _btnTopMost;
        private Label _lblHint;
        private Panel _panelTop;
        private Panel _panelBody;
        private DataGridView _grid;

        private const int CellPaddingX = 12;
        private const int RowHeight = 36;
        private const int HeaderHeight = 38;
        private const int MinColumnWidth = 170;
        private const int BodyPadding = 12;

        private static readonly Color BtnAccent      = Color.FromArgb(0, 120, 212);
        private static readonly Color HeaderBack     = Color.FromArgb(238, 243, 250);
        private static readonly Color HeaderFore     = Color.FromArgb(0, 90, 158);
        private static readonly Color CellBack       = Color.White;
        private static readonly Color HoverBack      = Color.FromArgb(229, 241, 251);
        private static readonly Color EmptyFore      = Color.DimGray;
        private static readonly Color GridLine       = Color.FromArgb(210, 210, 210);
        private static readonly Color CellFore       = Color.FromArgb(30, 30, 30);

        private static readonly Font CellFont  = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular);
        private static readonly Font HeaderFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        private static readonly Font EmptyFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Italic);

        /// <summary>单元格 → 插件。键 = row * 3 + column；占位单元格不入表。</summary>
        private readonly Dictionary<int, IPlugin> _cellPlugins = new Dictionary<int, IPlugin>();
        private DataGridViewCell _hoverCell;

        private static readonly PluginCategory[] Columns =
        {
            PluginCategory.BuiltIn,
            PluginCategory.Independent,
            PluginCategory.Test
        };

        private static readonly string[] ColumnTitles =
        {
            "1. 常用插件",
            "2. 独立插件",
            "3. 测试插件"
        };

        /// <summary>主窗口标题严格为「中文名称-yyyyMMddHHmm」，版本 = 主程序 DLL 写入时间，编译即自动更新。</summary>
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

        public MainToolboxForm(PluginManager manager)
        {
            _manager = manager;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "我的工具箱-" + BuildStamp();
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 245, 245);
            TopMost = _topMost;

            // === 顶部：窗口前置 + 操作提示 ===
            _panelTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(BodyPadding, 9, BodyPadding, 9),
                BackColor = Color.White
            };
            _btnTopMost = new Button
            {
                Text = "窗口前置",
                Location = new Point(BodyPadding, 9),
                Size = new Size(100, 30),
                BackColor = BtnAccent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            _btnTopMost.FlatAppearance.BorderSize = 0;
            _btnTopMost.Click += (s, e) => ToggleTopMost();

            _lblHint = new Label
            {
                Dock = DockStyle.Right,
                Width = 220,
                Text = "单击单元格打开插件",
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei UI", 9F)
            };

            _panelTop.Controls.Add(_lblHint);
            _panelTop.Controls.Add(_btnTopMost);

            // === 主体：三列表格 ===
            _panelBody = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(BodyPadding),
                BackColor = Color.FromArgb(245, 245, 245)
            };
            _grid = BuildGrid();
            _panelBody.Controls.Add(_grid);

            Controls.Add(_panelBody);
            Controls.Add(_panelTop);

            FillTable();
            ApplyTableSize();
        }

        private DataGridView BuildGrid()
        {
            var grid = new DataGridView
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

        /// <summary>把插件按分类填入表格；空分类显示一行占位文字。</summary>
        private void FillTable()
        {
            var groups = new Dictionary<PluginCategory, List<IPlugin>>
            {
                { PluginCategory.BuiltIn, new List<IPlugin>() },
                { PluginCategory.Independent, new List<IPlugin>() },
                { PluginCategory.Test, new List<IPlugin>() }
            };

            foreach (IPlugin plugin in _manager.Plugins)
            {
                // 未知分类归入常用列，避免插件从界面上消失
                groups[NormalizeCategory(plugin)].Add(plugin);
            }

            int rowCount = 1;
            foreach (List<IPlugin> list in groups.Values)
                rowCount = Math.Max(rowCount, list.Count);

            for (int r = 0; r < rowCount; r++)
                _grid.Rows.Add();

            for (int c = 0; c < Columns.Length; c++)
            {
                List<IPlugin> list = groups[Columns[c]];
                for (int r = 0; r < rowCount; r++)
                {
                    DataGridViewCell cell = _grid.Rows[r].Cells[c];
                    if (r < list.Count)
                    {
                        IPlugin plugin = list[r];
                        cell.Value = plugin.DisplayName;
                        cell.ToolTipText = "打开：" + plugin.DisplayName;
                        _cellPlugins[r * Columns.Length + c] = plugin;
                    }
                    else if (r == 0 && list.Count == 0)
                    {
                        cell.Value = "（暂无插件）";
                        cell.Style.Font = EmptyFont;
                        cell.Style.ForeColor = EmptyFore;
                        cell.Style.SelectionBackColor = CellBack;
                        cell.Style.SelectionForeColor = EmptyFore;
                    }
                }
            }
        }

        /// <summary>按实测文字宽度设置列最小宽度与窗口最小尺寸，保证单元格文字不被遮挡。</summary>
        private void ApplyTableSize()
        {
            int totalMin = 0;
            for (int c = 0; c < Columns.Length; c++)
            {
                int need = TextRenderer.MeasureText(ColumnTitles[c], HeaderFont).Width + CellPaddingX * 2 + 24;
                foreach (IPlugin plugin in _manager.Plugins)
                {
                    PluginCategory cat = NormalizeCategory(plugin);
                    if (cat != Columns[c]) continue;
                    int w = TextRenderer.MeasureText(plugin.DisplayName, CellFont).Width + CellPaddingX * 2 + 24;
                    if (w > need) need = w;
                }
                if (need < MinColumnWidth) need = MinColumnWidth;
                _grid.Columns[c].MinimumWidth = need;
                totalMin += need;
            }

            int clientWidth = Math.Max(840, totalMin + 48);
            ClientSize = new Size(clientWidth, 480);
            MinimumSize = new Size(clientWidth + 16, 380);
        }

        private static PluginCategory NormalizeCategory(IPlugin plugin)
        {
            return plugin.Category == PluginCategory.Independent || plugin.Category == PluginCategory.Test
                ? plugin.Category
                : PluginCategory.BuiltIn;
        }

        private void Grid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            ActivateCell(e.RowIndex, e.ColumnIndex);
        }

        private void Grid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space) return;
            DataGridViewCell cell = _grid.CurrentCell;
            if (cell == null) return;
            ActivateCell(cell.RowIndex, cell.ColumnIndex);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void Grid_CellMouseEnter(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            int key = e.RowIndex * Columns.Length + e.ColumnIndex;
            if (!_cellPlugins.ContainsKey(key)) return;

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

        private void ActivateCell(int row, int column)
        {
            IPlugin plugin;
            if (!_cellPlugins.TryGetValue(row * Columns.Length + column, out plugin)) return;

            try
            {
                plugin.Activate();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("插件 Activate 失败 [" + plugin.Id + "]: " + ex);
                MessageBox.Show("打开 " + plugin.DisplayName + " 失败：\n" + ex.Message,
                    "我的工具箱", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>窗口前置开关：默认锁定在 CAXA 之上，再点一次解锁。</summary>
        private void ToggleTopMost()
        {
            _topMost = !_topMost;
            TopMost = _topMost;
            if (_topMost)
            {
                _btnTopMost.BackColor = BtnAccent;
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
    }
}
