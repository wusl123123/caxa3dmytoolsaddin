using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace caxa3dsheetmaster
{
    /// <summary>
    /// K 因子计算器（纯计算工具，不读写 CAXA 数据）。
    /// 公式：K = (4R + 4T - 2X) / (π·T) - R/T
    /// 数据填写区就是一张表格；点「计算」按钮才把结果算进「计算结果」列。
    /// 布局用 Dock + TableLayoutPanel，避免高 DPI / 缩放下控件被裁切。
    /// </summary>
    [ComVisible(false)]
    internal sealed class KFactorCalcForm : Form
    {
        private const int RowCount = 10;
        private const int ColT = 0;
        private const int ColR = 1;
        private const int ColX = 2;
        private const int ColK = 3;

        private DataGridView _grid;
        private static float _scale = 1f;

        public KFactorCalcForm()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                _scale = g.DpiX / 96f;
            if (_scale < 1f) _scale = 1f;

            Text = "K 因子计算器";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = DpiSize(520, 460);
            ClientSize = DpiSize(520, 620);
            AutoScaleMode = AutoScaleMode.None;
            Font = SafeFont(9F);
            BackColor = Color.White;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12, 10, 12, 10),
                BackColor = Color.White
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi(30)));   // 公式说明
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));        // 表格
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi(24)));    // 参考提示
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi(42)));    // 按钮
            Controls.Add(root);

            var head = new Label
            {
                Text = "K = (4R + 4T - 2X) / (π·T) - R/T      （单位 mm；仅计算，不写入数据）",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(90, 90, 90),
                AutoEllipsis = true
            };
            root.Controls.Add(head, 0, 0);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = Dpi(30),
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                EditMode = DataGridViewEditMode.EditOnEnter,
                RowTemplate = { Height = Dpi(24) }
            };
            _grid.DefaultCellStyle.Font = SafeFont(9F);
            _grid.ColumnHeadersDefaultCellStyle.Font = SafeFont(9F);
            _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            AddColumn("板厚 T(mm)", 22);
            AddColumn("内半径 R(mm)", 22);
            AddColumn("折弯扣除 X(mm)", 24);
            AddColumn("计算结果 K", 32);

            // 首行给一组示例值，便于对照公式（T=3、R=3、X=6 → 0.2732）
            _grid.Rows.Add("3.000", "3.000", "6.000", "");
            for (int i = 1; i < RowCount; i++) _grid.Rows.Add("", "", "", "");

            for (int r = 0; r < _grid.Rows.Count; r++)
            {
                _grid.Rows[r].Cells[ColK].ReadOnly = true;
                _grid.Rows[r].Cells[ColK].Style.BackColor = Color.FromArgb(245, 245, 245);
                _grid.Rows[r].Cells[ColK].Style.ForeColor = Color.FromArgb(0, 120, 60);
            }
            _grid.CellValueChanged += (s, e) =>
            {
                // 输入变化后把该行结果作废，等待重新点「计算」
                if (e.RowIndex < 0 || e.ColumnIndex == ColK) return;
                _grid.Rows[e.RowIndex].Cells[ColK].Value = "";
            };
            root.Controls.Add(_grid, 0, 1);

            var tip = new Label
            {
                Text = "参考：T=3、R=3、X=6 时 K≈0.273（等价 2 倍折弯扣除）",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(130, 130, 130),
                AutoEllipsis = true
            };
            root.Controls.Add(tip, 0, 2);

            var buttonBar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi(210)));
            buttonBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var right = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            var btnCalc = new Button
            {
                Text = "计算",
                Size = new Size(Dpi(96), Dpi(30)),
                FlatStyle = FlatStyle.System,
                Margin = new Padding(0, 0, 8, 0)
            };
            btnCalc.Click += (s, e) => CalculateAll();
            var btnClose = new Button
            {
                Text = "关闭",
                Size = new Size(Dpi(90), Dpi(30)),
                FlatStyle = FlatStyle.System,
                Margin = new Padding(0)
            };
            btnClose.Click += (s, e) => Close();
            right.Controls.Add(btnCalc);
            right.Controls.Add(btnClose);

            buttonBar.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 0);
            buttonBar.Controls.Add(right, 1, 0);
            root.Controls.Add(buttonBar, 0, 3);
        }

        /// <summary>按当前屏幕 DPI 缩放像素尺寸。</summary>
        private static int Dpi(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private static Size DpiSize(int w, int h)
        {
            return new Size(Dpi(w), Dpi(h));
        }

        private void AddColumn(string header, float percent)
        {
            var col = new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = percent,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                MinimumWidth = 80,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight
                }
            };
            _grid.Columns.Add(col);
        }

        private static Font SafeFont(float size)
        {
            return new Font("Microsoft YaHei UI", size, FontStyle.Regular);
        }

        /// <summary>点「计算」：逐行把 T/R/X 代入公式，写进结果列。</summary>
        private void CalculateAll()
        {
            for (int r = 0; r < _grid.Rows.Count; r++)
            {
                DataGridViewCell kCell = _grid.Rows[r].Cells[ColK];
                double t, radius, deduction;

                if (!TryParseMm(_grid.Rows[r].Cells[ColT].Value, out t)
                    || !TryParseMm(_grid.Rows[r].Cells[ColR].Value, out radius)
                    || !TryParseMm(_grid.Rows[r].Cells[ColX].Value, out deduction))
                {
                    kCell.Value = "";
                    continue;
                }

                if (t <= 0 || radius <= 0 || deduction <= 0)
                {
                    kCell.Value = "";
                    continue;
                }

                double k = (4.0 * radius + 4.0 * t - 2.0 * deduction) / (Math.PI * t) - radius / t;
                if (k < 0 || k > 1)
                {
                    kCell.Value = k.ToString("0.0000") + " 超范围";
                    kCell.Style.ForeColor = Color.FromArgb(180, 100, 0);
                }
                else
                {
                    kCell.Value = k.ToString("0.0000");
                    kCell.Style.ForeColor = Color.FromArgb(0, 120, 60);
                }
            }
        }

        private static bool TryParseMm(object raw, out double value)
        {
            value = 0;
            string text = Convert.ToString(raw) ?? "";
            text = text.Trim().Replace(" ", "");
            if (text.Length == 0) return false;
            return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out value)
                || double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }
}
