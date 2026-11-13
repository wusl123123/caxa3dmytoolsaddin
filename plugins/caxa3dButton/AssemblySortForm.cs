// 装配体排序 条件窗（用户 2026-11-13 需求；第二版：去掉目标装配体 / 只选中零部件 / 仅预览三个选项）。
// 条件分组：
//   ① 排序方式：依据固定＝零件名；升序 / 降序【互斥】
//   ② 穿透范围：只排第一层 / 穿透到底【互斥】
// 默认处理当前设计环境中的全部零部件，用户不需要选中任何对象。
// 铁律（与主工具窗一致）：
//  - 按钮文字纯汉字，禁用 emoji / 图标
//  - 构造期不连 COM、不探测（会话推迟到点「执行排序」）
//  - 收尾只走状态栏 + trace，不弹模态框（模态会禁用 CAXA 主界面）
//  - 本窗无可编辑文本框 ⇒ 不接 ChordRescue 键盘抢救（规则第 37 节适用前提是「有可编辑文本框」）
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

        private readonly Label _lblHint = new Label();

        private readonly GroupBox _grpOrder = new GroupBox();
        private readonly Label _lblKey = new Label();
        private readonly RadioButton _rbAsc = new RadioButton();
        private readonly RadioButton _rbDesc = new RadioButton();

        private readonly GroupBox _grpDepth = new GroupBox();
        private readonly RadioButton _rbOneLevel = new RadioButton();
        private readonly RadioButton _rbAllLevels = new RadioButton();

        private readonly Label _lblOptionNote = new Label();
        private readonly Label _lblStatus = new Label();
        private readonly Button _btnApply = new Button();
        private readonly Button _btnClose = new Button();

        internal AssemblySortForm(IZBaseApp siteApp, bool topMost)
        {
            _siteApp = siteApp;
            BuildUi();
            if (!IsHandleCreated) CreateHandle();
            TopMost = topMost;
        }

        private void BuildUi()
        {
            Text = "装配体排序";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(440, 376);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            _lblHint.Text = "先在设计树里选中一个装配体，本工具按零件名重排它的子零部件（未选中时自动用场景唯一顶层装配）。";
            _lblHint.SetBounds(14, 12, 412, 38);
            _lblHint.ForeColor = Color.DimGray;

            // ---- ① 排序方式（互斥）
            _grpOrder.Text = "排序方式";
            _grpOrder.SetBounds(12, 56, 416, 76);

            _lblKey.Text = "排序依据：零件名";
            _lblKey.SetBounds(18, 22, 200, 20);
            _lblKey.TextAlign = ContentAlignment.MiddleLeft;

            _rbAsc.Text = "升序";
            _rbAsc.SetBounds(18, 46, 150, 22);
            _rbAsc.Checked = true;

            _rbDesc.Text = "降序";
            _rbDesc.SetBounds(190, 46, 150, 22);

            _grpOrder.Controls.AddRange(new Control[] { _lblKey, _rbAsc, _rbDesc });

            // ---- ② 穿透范围（互斥）
            _grpDepth.Text = "穿透范围";
            _grpDepth.SetBounds(12, 140, 416, 84);

            _rbOneLevel.Text = "只排第一层（选中装配体的直接子零部件）";
            _rbOneLevel.SetBounds(18, 26, 380, 22);
            _rbOneLevel.Checked = true;

            _rbAllLevels.Text = "穿透到底（逐层进入子装配）";
            _rbAllLevels.SetBounds(18, 52, 380, 22);

            _grpDepth.Controls.AddRange(new Control[] { _rbOneLevel, _rbAllLevels });

            _lblOptionNote.Text = "排序对象＝设计树里选中的那个装配体（未选中时自动用场景唯一顶层装配）。\n" +
                "参与排序＝装配体、零件、钣金件、多面体、线架、型材；板料/折弯/造型/特征保持原位。";
            _lblOptionNote.SetBounds(14, 232, 412, 38);
            _lblOptionNote.ForeColor = Color.DimGray;

            _lblStatus.Text = "就绪";
            _lblStatus.SetBounds(14, 274, 412, 48);
            _lblStatus.ForeColor = Color.DimGray;
            _lblStatus.AutoSize = false;

            _btnApply.Text = "执行排序";
            _btnApply.SetBounds(14, 332, 200, 32);
            _btnApply.Click += OnApplyClick;

            _btnClose.Text = "关闭";
            _btnClose.SetBounds(228, 332, 200, 32);
            _btnClose.Click += delegate { Close(); };

            Controls.AddRange(new Control[] {
                _lblHint, _grpOrder, _grpDepth, _lblOptionNote, _lblStatus,
                _btnApply, _btnClose });
        }

        private void SetStatus(string text, Color color)
        {
            if (_lblStatus.IsDisposed) return;
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        private void OnApplyClick(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            _btnApply.Enabled = false;
            SetStatus("正在按零件名排序全部零部件 …", Color.DarkOrange);
            try
            {
                var opt = new AssemblySortTool.Options
                {
                    Ascending = _rbAsc.Checked,
                    PenetrateAll = _rbAllLevels.Checked,
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
