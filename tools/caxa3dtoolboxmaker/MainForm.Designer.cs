using System;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Windows.Forms;

namespace Caxa3dToolboxMaker
{
    partial class MainForm
    {
        private IContainer components = null;

        // 文件区
        internal GroupBox gbFile;
        internal TextBox tbXml, tbIcs, tbCache;
        // Tab 区
        internal TabControl tc;
        internal DataGridView dgvRow, dgvParam, dgvCur, dgvCache;
        internal DataTable dtRow, dtParam, dtCur, dtCache;
        // 状态
        internal Label lblStatus;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new Container();

            this.Text = "CAXA 3D 标准件库 XML 编辑器 - toolboxmaker";
            this.Font = F;
            // ★ 必须 None：默认 AutoScaleMode=Font 会以 MS Sans Serif 8pt 为基线缩放子控件，
            //   宋体9pt 与之度量比≈1.83，把表格从 1150×472 撑到 2108×875，列宽(96逻辑)反被视口盖过 → 滚动条不出现。
            this.AutoScaleMode = AutoScaleMode.None;
            this.BackColor = SystemColors.Control;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimumSize = new Size(940, 620);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(1190, 700);

            BuildFileArea();
            BuildTabArea();
            BuildBottomButtons();
        }

        private void BuildFileArea()
        {
            gbFile = new GroupBox();
            gbFile.Text = "文件（XML 必选；ICS / CACHE 可留空）";
            gbFile.Font = F; gbFile.FlatStyle = FlatStyle.System;
            gbFile.Location = new Point(12, 10); gbFile.Size = new Size(1166, 106);
            gbFile.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(gbFile);

            Button bXml, bIcs, bCache;
            tbXml = AddFileRow(gbFile, 24, "XML：", out bXml);
            tbIcs = AddFileRow(gbFile, 52, "ICS：", out bIcs);
            tbCache = AddFileRow(gbFile, 80, "CACHE：", out bCache);
            bXml.Click += delegate { Pick(tbXml, "XML 文件|*.XML;*.xml|所有文件|*.*"); InferSiblings(); };
            bIcs.Click += delegate { Pick(tbIcs, "ICS 文件|*.ics|所有文件|*.*"); InferSiblingsFromIcs(); };
            bCache.Click += delegate { Pick(tbCache, "CACHE 索引|*.cache|所有文件|*.*"); LoadCache(); };
            // 手动粘贴/输入地址栏也自动联动（浏览按钮那条路径会重复触发，但推导幂等无害）
            tbXml.TextChanged += delegate { InferSiblings(); };
            tbIcs.TextChanged += delegate { InferSiblingsFromIcs(); };
        }

        private void BuildTabArea()
        {
            tc = new TabControl();
            tc.Font = F; tc.Location = new Point(12, 124); tc.Size = new Size(1166, 498);
            tc.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(tc);

            TabPage tp1 = new TabPage("规格表 DataTable（电子表格：增删改行列 / 双击编辑 / 返填 XML）");
            tp1.Font = F;
            dtRow = new DataTable();
            dgvRow = MakeGrid(dtRow, new Point(4, 38), new Size(1150, 472), true);
            dgvRow.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            tp1.Controls.Add(dgvRow);
            // 电子表格工具栏：增删行、增删列、重载
            AddTabButton(tp1, "＋ 新增行", 6, 4, delegate { AddRow(); });
            AddTabButton(tp1, "＋ 新增列", 104, 4, delegate { AddColumn(); });
            AddTabButton(tp1, "－ 删除行", 202, 4, delegate { DelRow(); });
            AddTabButton(tp1, "－ 删除列", 300, 4, delegate { DelColumn(); });
            AddTabButton(tp1, "↺ 重新加载", 398, 4, delegate { LoadXml(); });
            tc.Controls.Add(tp1);

            TabPage tp2 = new TabPage("参数表 ConfigureTable（AddtoDisplayName / Grid_item_state 等）");
            tp2.Font = F;
            dtParam = new DataTable();
            dgvParam = MakeGrid(dtParam, new Point(4, 38), new Size(1150, 472), true);
            dgvParam.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            tp2.Controls.Add(dgvParam);
            // 参数表工具栏：增删行（行序与 paramKeyOrder 同步，保存时返填 XML）
            AddTabButton(tp2, "＋ 新增行", 6, 4, delegate { AddParamRow(); });
            AddTabButton(tp2, "－ 删除行", 104, 4, delegate { DelParamRow(); });
            tc.Controls.Add(tp2);

            TabPage tp3 = new TabPage("族信息 CurNodeInfo（name / display_name / user_name / GUID 等）");
            tp3.Font = F;
            dtCur = new DataTable();
            dtCur.Columns.Add("属性"); dtCur.Columns.Add("值");
            dgvCur = MakeGrid(dtCur, new Point(4, 4), new Size(560, 490), true);
            dgvCur.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            if (dgvCur.Columns.Count > 0) dgvCur.Columns[0].ReadOnly = true;
            tp3.Controls.Add(dgvCur);

            Button bAddRow = new Button(); bAddRow.Text = "新增规格行"; bAddRow.Font = F; bAddRow.FlatStyle = FlatStyle.System;
            bAddRow.Location = new Point(580, 20); bAddRow.Size = new Size(120, 30);
            bAddRow.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bAddRow.Click += delegate { AddRow(); }; tp3.Controls.Add(bAddRow);
            Button bDelRow = new Button(); bDelRow.Text = "删除选中行"; bDelRow.Font = F; bDelRow.FlatStyle = FlatStyle.System;
            bDelRow.Location = new Point(580, 60); bDelRow.Size = new Size(120, 30);
            bDelRow.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            bDelRow.Click += delegate { DelRow(); }; tp3.Controls.Add(bDelRow);
            Label tip = new Label(); tip.Font = F; tip.Text = "显示名要点（规则第46节）：\r\n前缀回退链 user_name→display_name→name→ICSName\r\n参数层唯一 AddtoDisplayName=TRUE 给目标字段\r\n目标字段须 Grid_item_state=DIM_CTRL_SHOW";
            tip.Location = new Point(580, 110); tip.Size = new Size(560, 130);
            tip.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            tp3.Controls.Add(tip);
            tc.Controls.Add(tp3);

            TabPage tp4 = new TabPage("目录索引 .cache（Folder 条目：可增删改 / 双击打开族 XML / 保存回写）");
            tp4.Font = F;
            dtCache = new DataTable();
            // 2026-11-21 起开放编辑：单元格可直接改写 name / display_name / type / GUID / protected
            dgvCache = MakeGrid(dtCache, new Point(4, 38), new Size(1150, 430), true);
            dgvCache.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            // 双击留给单元格编辑；打开族 XML 改由工具栏按钮触发（避免与编辑冲突）
            tp4.Controls.Add(dgvCache);
            AddTabButton(tp4, "↺ 重新加载", 6, 4, delegate { LoadCache(); });
            AddTabButton(tp4, "打开族XML", 104, 4, delegate { OpenSelectedFamilyXml(); });
            AddTabButton(tp4, "＋ 新增条目", 202, 4, delegate { AddCacheRow(); });
            AddTabButton(tp4, "－ 删除条目", 300, 4, delegate { DelCacheRow(); });
            AddTabButton(tp4, "保存 .cache", 398, 4, delegate { SaveCache(); });
            Label cacheTip = new Label(); cacheTip.Font = F;
            cacheTip.Text = "索引可编辑：双击单元格改属性，工具栏「保存 .cache」写回（自动备份 + 保格式）。「打开族XML」按 name 定位同目录族 XML（找不到按第三态提示）。";
            cacheTip.Location = new Point(500, 10); cacheTip.Size = new Size(650, 22);
            cacheTip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            tp4.Controls.Add(cacheTip);
            tc.Controls.Add(tp4);
        }

        private void BuildBottomButtons()
        {
            AddButton("打开文件", 12, 632, delegate { LoadXml(); }, 112);
            AddButton("保存 XML（自动备份）", 132, 632, delegate { SaveXml(); }, 176);
            AddButton("导出规格 CSV", 316, 632, delegate { ExportCsv(); }, 130);
            AddButton("导出表格(Excel)", 452, 632, delegate { ExportTableExcel(); }, 148);
            AddButton("导入表格(Excel)", 606, 632, delegate { ImportTableExcel(); }, 148);
            AddButton("关闭", 760, 632, delegate { this.Close(); }, 100);

            lblStatus = new Label(); lblStatus.Font = F; lblStatus.Text = "就绪。";
            lblStatus.Location = new Point(12, 672); lblStatus.Size = new Size(1166, 22);
            lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            lblStatus.BorderStyle = BorderStyle.Fixed3D;
            Controls.Add(lblStatus);
        }

        TextBox AddFileRow(GroupBox gb, int y, string label, out Button btn)
        {
            Label l = new Label(); l.Text = label; l.Font = F; l.Location = new Point(10, y + 3); l.Size = new Size(52, 20);
            gb.Controls.Add(l);
            TextBox tb = new TextBox(); tb.Font = F; tb.BorderStyle = BorderStyle.Fixed3D;
            tb.Location = new Point(64, y); tb.Size = new Size(1040, 22);
            tb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gb.Controls.Add(tb);
            Button b = new Button(); b.Text = "浏览"; b.Font = F; b.FlatStyle = FlatStyle.System;
            b.Location = new Point(1112, y - 2); b.Size = new Size(48, 26);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            gb.Controls.Add(b);
            btn = b; return tb;
        }

        DataGridView MakeGrid(DataTable dt, Point loc, Size size, bool editable)
        {
            DataGridView g = new DataGridView();
            g.Font = F; g.Location = loc; g.Size = size;
            g.DataSource = dt;
            g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false;
            g.ReadOnly = !editable;
            g.BackgroundColor = SystemColors.Window;
            g.GridColor = Color.FromArgb(170, 170, 170);
            g.CellBorderStyle = DataGridViewCellBorderStyle.Single;
            // 不用 DisplayedCells：它会把列宽压缩到刚好塞进可见区，导致数据超宽时水平滚动条也不出现（本轮实证）。
            // 改 None + ApplyGridColumnWidths 给每列设最小宽度，超宽/超高时双向滚动条才如实出现。
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            g.SelectionMode = DataGridViewSelectionMode.CellSelect;
            if (editable) g.ScrollBars = ScrollBars.Both;
            // 列在 DataSource 绑定后才生成；表格尺寸在 Show/缩放后还会变，故 DataBindingComplete + Resize 都重算
            if (editable)
            {
                g.DataBindingComplete += delegate { ApplyWidthsToOne(g); };
                g.Resize += delegate { ApplyWidthsToOne(g); };
            }
            // 电子表格观感：列头加粗灰底、行号列、交替行底色
            g.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
            g.ColumnHeadersDefaultCellStyle.Font = new Font(F, FontStyle.Bold);
            g.RowHeadersVisible = true;
            g.RowHeadersWidth = 44;
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            return g;
        }

        void AddTabButton(TabPage page, string text, int x, int y, EventHandler h)
        {
            Button b = new Button(); b.Text = text; b.Font = F; b.FlatStyle = FlatStyle.System;
            b.Location = new Point(x, y); b.Size = new Size(94, 28);
            b.Click += h; page.Controls.Add(b);
        }

        void AddButton(string text, int x, int y, EventHandler h, int width)
        {
            Button b = new Button(); b.Text = text; b.Font = F; b.FlatStyle = FlatStyle.System;
            b.Location = new Point(x, y); b.Size = new Size(width, 32);
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            b.Click += h; Controls.Add(b);
        }
    }
}
