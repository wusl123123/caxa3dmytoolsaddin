using System;
using System.Drawing;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;

namespace caxa3dmytoolsaddin
{
    /// <summary>
    /// 我的工具箱主窗口：
    ///   - 顶部只有一个「窗口前置」按钮（默认开启，点击可切换）；
    ///   - 中间分两列：左列为「内置插件」，右列为「独立插件」；
    ///   - 按钮文字左对齐、宽度自适应文字长度、悬停高亮。
    /// </summary>
    [System.Runtime.InteropServices.ComVisible(false)]
    public class MainToolboxForm : Form
    {
        private readonly PluginManager _manager;
        private bool _topMost = true;   // 默认前置

        private Button _btnTopMost;
        private Panel _panelTop;
        private Panel _panelBuiltIn;      // 左列：内置插件按钮区
        private Panel _panelIndependent;  // 右列：独立插件按钮区

        private const int ButtonHeight = 36;
        private const int ButtonGap = 6;
        private const int PanelPadding = 12;
        private const int GroupLabelHeight = 30;
        private const int MinButtonWidth = 140;
        private const int MaxButtonWidth = 240;

        // 按钮配色
        private static readonly Color BtnBackColor    = Color.White;
        private static readonly Color BtnHoverColor   = Color.FromArgb(229, 241, 251);
        private static readonly Color BtnBorderColor  = Color.FromArgb(200, 200, 200);
        private static readonly Color BtnHoverBorder  = Color.FromArgb(0, 120, 212);
        private static readonly Color GroupLabelColor = Color.FromArgb(0, 120, 212);

        public MainToolboxForm(PluginManager manager)
        {
            _manager = manager;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "我的工具箱";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 400);
            MinimumSize = new Size(440, 300);
            TopMost = _topMost;
            BackColor = Color.FromArgb(245, 245, 245);

            // === 顶部按钮区：只有「窗口前置」一个按钮 ===
            _panelTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(PanelPadding, 8, PanelPadding, 8),
                BackColor = Color.White
            };
            _btnTopMost = new Button
            {
                Text = "窗口前置",
                Location = new Point(PanelPadding, 8),
                Size = new Size(100, 28),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            _btnTopMost.FlatAppearance.BorderSize = 0;
            _btnTopMost.Click += (s, e) => ToggleTopMost();
            _panelTop.Controls.Add(_btnTopMost);

            // === 两列插件区 ===
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.FromArgb(245, 245, 245)
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _panelBuiltIn = BuildPluginColumn("内置插件");
            _panelIndependent = BuildPluginColumn("独立插件");

            table.Controls.Add(_panelBuiltIn, 0, 0);
            table.Controls.Add(_panelIndependent, 1, 0);

            BuildPluginButtons();

            Controls.Add(table);
            Controls.Add(_panelTop);
        }

        /// <summary>构造一列：顶部组标签 + 下方可滚动按钮区（FlowLayoutPanel 左对齐自动换行）。</summary>
        private Panel BuildPluginColumn(string title)
        {
            var col = new Panel { Dock = DockStyle.Fill, Padding = new Padding(PanelPadding) };

            var lbl = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = GroupLabelHeight,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = GroupLabelColor
            };

            // FlowLayoutPanel：默认左对齐，按钮宽度自适应文字，超出宽度自动换行
            var buttonArea = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0),
                BackColor = Color.FromArgb(245, 245, 245)
            };

            col.Controls.Add(buttonArea);
            col.Controls.Add(lbl);
            col.Tag = buttonArea;
            return col;
        }

        /// <summary>构建子插件按钮：按 Category 分到两列。</summary>
        private void BuildPluginButtons()
        {
            var builtInArea = (FlowLayoutPanel)_panelBuiltIn.Tag;
            var independentArea = (FlowLayoutPanel)_panelIndependent.Tag;
            builtInArea.Controls.Clear();
            independentArea.Controls.Clear();

            if (_manager.Plugins.Count == 0)
            {
                var lbl = new Label
                {
                    Text = "未加载到任何子插件",
                    AutoSize = true,
                    Margin = new Padding(0, 8, 0, 0),
                    ForeColor = Color.DimGray
                };
                builtInArea.Controls.Add(lbl);
                return;
            }

            FillColumn(builtInArea, PluginCategory.BuiltIn);
            FillColumn(independentArea, PluginCategory.Independent);
        }

        /// <summary>把指定分类的插件按钮填充到某一列。</summary>
        private void FillColumn(FlowLayoutPanel area, PluginCategory category)
        {
            foreach (IPlugin plugin in _manager.Plugins)
            {
                if (plugin.Category != category) continue;

                Button btn = CreatePluginButton(plugin);
                area.Controls.Add(btn);
            }
        }

        /// <summary>创建单个插件按钮：文字左对齐、宽度自适应文字、悬停高亮。</summary>
        private Button CreatePluginButton(IPlugin plugin)
        {
            var btn = new Button
            {
                Text = plugin.DisplayName,
                Height = ButtonHeight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 14, 0),
                Margin = new Padding(0, 0, 0, ButtonGap),
                FlatStyle = FlatStyle.Flat,
                BackColor = BtnBackColor,
                ForeColor = Color.FromArgb(40, 40, 40),
                Font = new Font("Microsoft YaHei UI", 9.5F),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = BtnBorderColor;
            btn.FlatAppearance.BorderSize = 1;

            // 限制最小/最大宽度，避免过窄或过宽
            btn.MinimumSize = new Size(MinButtonWidth, ButtonHeight);
            btn.MaximumSize = new Size(MaxButtonWidth, ButtonHeight);

            // 悬停高亮
            btn.MouseEnter += (s, e) =>
            {
                btn.BackColor = BtnHoverColor;
                btn.FlatAppearance.BorderColor = BtnHoverBorder;
            };
            btn.MouseLeave += (s, e) =>
            {
                btn.BackColor = BtnBackColor;
                btn.FlatAppearance.BorderColor = BtnBorderColor;
            };

            string pluginId = plugin.Id;
            btn.Click += (s, e) =>
            {
                try
                {
                    plugin.Activate();
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("插件 Activate 失败 [" + pluginId + "]: " + ex);
                    MessageBox.Show("打开 " + plugin.DisplayName + " 失败：\n" + ex.Message,
                        "我的工具箱", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            return btn;
        }

        /// <summary>窗口前置开关：默认锁定在 CAXA 之上，再点一次解锁。</summary>
        private void ToggleTopMost()
        {
            _topMost = !_topMost;
            TopMost = _topMost;
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
    }
}
