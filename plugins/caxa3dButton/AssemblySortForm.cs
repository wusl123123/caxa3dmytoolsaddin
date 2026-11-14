// 装配体排序 条件窗（用户 2026-11-13 需求；11-14 13:28 优化：新增「优先排序特征符号」区）。
// 11-14 13:39 修复「文字显示不全」：
//   - 原因＝各说明 Label 写死 Height（20 / 54 / 34），文案一换行就把后面的行裁掉；
//     结果判词含「首层目标顺序」，长度不定，34px 只放得下两行，尾部被按钮压住。
//   - 对策①：所有说明 Label 用 TextRenderer.MeasureText 按可用宽度实测行数 ⇒ 动态高度 + y 级联；
//     窗体 ClientSize 高度由布局累加得出，不再写死。
//   - 对策②：结果判词改「只读多行 TextBox + 垂直滚动条」，可选中/复制/滚动，永不截断。
//   - 对策③：OnLoad 再排一次，覆盖 DPI / AutoScale 缩放后的实际字体（布局结论取真实坐标，不取截图）。
// 条件分组（自上而下）：
//   ⓪ 优先排序特征符号：用户输入特征符号，默认一个空格 " "；名称含该符号的零部件优先排前，
//      组内仍按零件名升/降序；符号可改，留空＝不做优先分组（退回纯按名排序）。
//   ① 排序方式：依据固定＝零件名；升序 / 降序【互斥】
//   ② 穿透范围：只排第一层 / 穿透到底【互斥】
// 铁律（与主工具窗一致）：
//  - 按钮文字纯汉字，禁用 emoji / 图标
//  - 构造期不连 COM、不探测（会话推迟到点「执行排序」）
//  - 收尾只走状态栏 + trace，不弹模态框（模态会禁用 CAXA 主界面）
//  - ★ 本窗有可编辑文本框 ⇒ 必须接 ChordRescue 键盘抢救（规则第 37 节）：
//    CAXA 的 PreTranslateMessage 会吞掉 Ctrl+C/V/X/A/Z，不装 WH_GETMESSAGE 钩子则粘贴不了符号。
using System;
using System.Drawing;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal sealed class AssemblySortForm : Form
    {
        private readonly IZBaseApp _siteApp;
        private bool _busy;
        private bool _layoutLogged;      // 布局自检只在加载后记一次日志

        private readonly Label _lblHint = new Label();

        private readonly GroupBox _grpPriority = new GroupBox();
        private readonly Label _lblPriorityNote = new Label();
        private readonly TextBox _txtPrioritySymbol = new TextBox();
        private readonly Label _lblPriorityCurrent = new Label();

        private readonly GroupBox _grpOrder = new GroupBox();
        private readonly Label _lblKey = new Label();
        private readonly RadioButton _rbAsc = new RadioButton();
        private readonly RadioButton _rbDesc = new RadioButton();

        private readonly GroupBox _grpDepth = new GroupBox();
        private readonly RadioButton _rbOneLevel = new RadioButton();
        private readonly RadioButton _rbAllLevels = new RadioButton();

        private readonly Label _lblOptionNote = new Label();
        // 结果判词长度不定（含首层目标顺序清单）⇒ 只读多行框 + 滚动条，不用会裁字的 Label
        private readonly TextBox _txtStatus = new TextBox();
        private readonly Button _btnApply = new Button();
        private readonly Button _btnClose = new Button();

        internal AssemblySortForm(IZBaseApp siteApp, bool topMost)
        {
            _siteApp = siteApp;
            BuildUi();
            if (!IsHandleCreated) CreateHandle();
            TopMost = topMost;
        }

        // ↓ 键盘归属抢救（规则第 37 节）：有 TextBox 才需要；属主切换由 ChordRescue 内部按窗口判定
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ChordRescue.Install(this, m => Diagnostics.Log(m)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        protected override void OnFormClosed(FormClosedEventArgs e) { ChordRescue.Uninstall(this, "装配体排序窗关闭"); base.OnFormClosed(e); }
        // ↑

        // AutoScale / DPI 缩放后字体会变，OnLoad 时按实际字体重排一次（幂等）
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LayoutUi();
            LogLayoutEvidence();
        }

        /// <summary>
        /// 布局自检：把每个说明 Label「文本实测所需高度 / 实有高度」写进 trace。
        /// 用户实机打开本窗即可从日志判断有没有裁字，不依赖截图。
        /// </summary>
        private void LogLayoutEvidence()
        {
            if (_layoutLogged) return;
            _layoutLogged = true;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("asm-sort ui: ClientSize=").Append(ClientSize.Width).Append("x").Append(ClientSize.Height);
            AppendFit(sb, "hint", _lblHint);
            AppendFit(sb, "priorityNote", _lblPriorityNote);
            AppendFit(sb, "optionNote", _lblOptionNote);
            sb.Append(" / status=").Append(_txtStatus.Bounds)
              .Append(" ReadOnly=").Append(_txtStatus.ReadOnly)
              .Append(" ScrollBars=").Append(_txtStatus.ScrollBars);
            Diagnostics.Log(sb.ToString());
        }

        private static void AppendFit(System.Text.StringBuilder sb, string tag, Label l)
        {
            Size want = TextRenderer.MeasureText(l.Text ?? string.Empty, l.Font,
                new Size(l.Width - 6, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            int need = want.Height + 8;
            sb.Append(" / ").Append(tag).Append(" need=").Append(need)
              .Append(" actual=").Append(l.Height)
              .Append(l.Height >= need ? " OK" : " CLIPPED");
        }

        private void BuildUi()
        {
            Text = "装配体排序";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            _lblHint.Text = "先在设计树里选中一个装配体，工具按零件名重排其子零部件；未选中时自动取场景唯一顶层装配。";
            _lblHint.ForeColor = Color.DimGray;

            // ---- ⓪ 优先排序特征符号（用户 11-14 13:28 需求，放在排序方式上面）
            _grpPriority.Text = "优先排序特征符号";
            _lblPriorityNote.Text = "名称含该符号的零部件排在最前；组内仍按零件名升 / 降序排序。";
            _lblPriorityNote.TextAlign = ContentAlignment.MiddleLeft;
            _lblPriorityNote.ForeColor = Color.DimGray;

            _txtPrioritySymbol.Text = " ";          // 默认＝一个空格（用户 13:28 口径）
            _txtPrioritySymbol.MaxLength = 32;
            _txtPrioritySymbol.TextChanged += delegate { UpdatePriorityCurrent(); };

            _lblPriorityCurrent.TextAlign = ContentAlignment.MiddleLeft;
            _lblPriorityCurrent.ForeColor = Color.DimGray;
            UpdatePriorityCurrent();

            _grpPriority.Controls.AddRange(new Control[] {
                _lblPriorityNote, _txtPrioritySymbol, _lblPriorityCurrent });

            // ---- ① 排序方式（互斥）
            _grpOrder.Text = "排序方式";
            _lblKey.Text = "排序依据：零件名";
            _lblKey.TextAlign = ContentAlignment.MiddleLeft;
            _rbAsc.Text = "升序";
            _rbAsc.Checked = true;
            _rbDesc.Text = "降序";
            _grpOrder.Controls.AddRange(new Control[] { _lblKey, _rbAsc, _rbDesc });

            // ---- ② 穿透范围（互斥）
            _grpDepth.Text = "穿透范围";
            _rbOneLevel.Text = "只排第一层（选中装配体的直接子零部件）";
            _rbOneLevel.Checked = true;
            _rbAllLevels.Text = "穿透到底（逐层进入子装配）";
            _grpDepth.Controls.AddRange(new Control[] { _rbOneLevel, _rbAllLevels });

            _lblOptionNote.Text = "优先分组不受升 / 降序影响：含符号的一组恒在前，两组内部各自按名排序。\n" +
                "排序对象＝设计树里选中的装配体（未选中时取场景唯一顶层装配）。\n" +
                "参与排序＝装配体、零件、钣金件、多面体、线架、型材；其余特征保持原位。";
            _lblOptionNote.ForeColor = Color.DimGray;

            _txtStatus.Text = "就绪";
            _txtStatus.Multiline = true;
            _txtStatus.ReadOnly = true;
            _txtStatus.WordWrap = true;
            _txtStatus.ScrollBars = ScrollBars.Vertical;
            _txtStatus.BorderStyle = BorderStyle.FixedSingle;
            _txtStatus.BackColor = SystemColors.Window;
            _txtStatus.ForeColor = Color.DimGray;
            _txtStatus.TabStop = false;

            _btnApply.Text = "执行排序";
            _btnApply.Click += OnApplyClick;
            _btnClose.Text = "关闭";
            _btnClose.Click += delegate { Close(); };

            Controls.AddRange(new Control[] {
                _lblHint, _grpPriority, _grpOrder, _grpDepth, _lblOptionNote, _txtStatus,
                _btnApply, _btnClose });

            LayoutUi();
        }

        /// <summary>
        /// 按「实测文字所需行数」级联布局，绝不写死说明区高度。
        /// 所有 y 由上一个控件的真实 Bottom 推得 ⇒ 控件不重叠、文字不裁。
        /// </summary>
        private void LayoutUi()
        {
            const int formWidth = 470;
            const int textWidth = 442;    // formWidth - 左右各 14
            const int groupWidth = 446;   // formWidth - 左右各 12
            const int innerWidth = 414;   // GroupBox 内可用宽（左右各留 16）
            int y = 12;

            FitLabel(_lblHint, 14, y, textWidth);
            y += _lblHint.Height + 10;

            // ⓪ 优先符号组：组高由说明文字实测高度决定
            FitLabel(_lblPriorityNote, 16, 20, innerWidth);
            int boxY = 20 + _lblPriorityNote.Height + 6;
            _txtPrioritySymbol.SetBounds(16, boxY, 100, 24);
            _lblPriorityCurrent.SetBounds(126, boxY, innerWidth - 110, 24);
            int priorityHeight = boxY + 24 + 12;
            _grpPriority.SetBounds(12, y, groupWidth, priorityHeight);
            y += priorityHeight + 10;

            // ① 排序方式（内容不换行，固定 76 足够）
            _grpOrder.SetBounds(12, y, groupWidth, 76);
            _lblKey.SetBounds(18, 22, innerWidth - 4, 20);
            _rbAsc.SetBounds(18, 46, 150, 22);
            _rbDesc.SetBounds(190, 46, 150, 22);
            y += 76 + 10;

            // ② 穿透范围
            _grpDepth.SetBounds(12, y, groupWidth, 84);
            _rbOneLevel.SetBounds(18, 26, innerWidth - 4, 22);
            _rbAllLevels.SetBounds(18, 52, innerWidth - 4, 22);
            y += 84 + 10;

            // 说明区（三行，按实测高度，行数随字体/DPI 变化自动加高）
            FitLabel(_lblOptionNote, 14, y, textWidth);
            y += _lblOptionNote.Height + 10;

            // 结果区：固定视口 + 垂直滚动，长判词可完整查看/复制
            const int statusHeight = 96;
            _txtStatus.SetBounds(14, y, textWidth, statusHeight);
            y += statusHeight + 12;

            int buttonWidth = (textWidth - 14) / 2;
            _btnApply.SetBounds(14, y, buttonWidth, 32);
            _btnClose.SetBounds(14 + buttonWidth + 14, y, buttonWidth, 32);
            y += 32 + 14;

            ClientSize = new Size(formWidth, y);
        }

        /// <summary>用 GDI TextRenderer 实测「按该宽度折行后」的文本高度，留 8px 余量防 DPI 误差。</summary>
        private static void FitLabel(Label lbl, int x, int y, int width)
        {
            lbl.AutoSize = false;
            Size want = TextRenderer.MeasureText(lbl.Text ?? string.Empty, lbl.Font,
                new Size(width - 6, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            int h = want.Height + 8;
            if (h < 20) h = 20;
            lbl.SetBounds(x, y, width, h);
        }

        /// <summary>空格在文本框里不可见 ⇒ 旁边用可读文字回显当前符号（用户可确认默认值）。</summary>
        private void UpdatePriorityCurrent()
        {
            string s = _txtPrioritySymbol.Text;
            if (s.Length == 0) _lblPriorityCurrent.Text = "当前：未设置（不做优先分组）";
            else if (s == " ") _lblPriorityCurrent.Text = "当前：一个空格（默认）";
            else _lblPriorityCurrent.Text = "当前：「" + s + "」";
        }

        private void SetStatus(string text, Color color)
        {
            if (_txtStatus.IsDisposed) return;
            _txtStatus.Text = text;
            _txtStatus.ForeColor = color;
            _txtStatus.SelectionStart = 0;     // 每次都从判词开头显示，别停在上一轮的滚动位置
            _txtStatus.ScrollToCaret();
        }

        private void OnApplyClick(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            _btnApply.Enabled = false;
            SetStatus("正在按零件名（含优先符号）排序全部零部件 …", Color.DarkOrange);
            try
            {
                var opt = new AssemblySortTool.Options
                {
                    Ascending = _rbAsc.Checked,
                    PenetrateAll = _rbAllLevels.Checked,
                    PrioritySymbol = _txtPrioritySymbol.Text,
                };
                string verdict = AssemblySortTool.Apply(_siteApp, opt, m => Diagnostics.Log(m));
                Diagnostics.Log("asm-sort form: " + verdict);
                bool bad = verdict.IndexOf("中止", StringComparison.Ordinal) >= 0 ||
                           verdict.IndexOf("未生效", StringComparison.Ordinal) >= 0;
                SetStatus(verdict, bad ? Color.Firebrick : Color.DarkGreen);
            }
            catch (Exception ex)
            {
                SetStatus("装配体排序失败：" + ex.Message, Color.Firebrick);
                Diagnostics.Log("asm-sort form: apply EX " + ProbeCommon.Hr(ex) + " " + ex);
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) _btnApply.Enabled = true;
            }
        }
    }
}
