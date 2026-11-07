using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using interop.ICApiIronCAD;

namespace caxa3dsetMaterial
{
    /// <summary>
    /// 色卡窗体（参考 SolidWorks 宏 usfSetColor.frm 逻辑与 颜色参考\2222.PNG 界面）：
    ///  - 色卡标准：RAL（经典系列）/ GSB05-1426-2001（漆膜颜色标准样卡），数据来自「色卡.xml」（DLL 同目录，只读）；
    ///  - 下拉选中 → RGB 文本框与预览面板联动；RGB 也可直接手输微调；
    ///  - 「应用颜色」对 CAXA 当前选中的每个零部件写外观色并回读校验
    ///    （复用 MaterialProbe.ApplyColorOnly，0xRRGGBB→COLORREF 转换已实测）；
    ///  - 应用成功后经回调回写主窗体颜色输入框；只改颜色，不动材料/密度。
    /// </summary>
    internal sealed class ColorCardForm : Form
    {
        private sealed class CardEntry
        {
            public string Display;
            public int Rgb;
            public override string ToString() { return Display; }
        }

        private readonly IZBaseApp _app;
        private readonly Action<int> _onApplied;

        private RadioButton _rbRal;
        private RadioButton _rbGsb;
        private ComboBox _cmbRal;
        private ComboBox _cmbGsb;
        private TextBox _txtR;
        private TextBox _txtG;
        private TextBox _txtB;
        private Panel _preview;
        private Label _lblPick;
        private Label _lblStatus;
        private bool _syncing;

        private readonly List<CardEntry> _ral = new List<CardEntry>();
        private readonly List<CardEntry> _gsb = new List<CardEntry>();

        // ↓↓↓ 键盘归属抢救：Ctrl+C/V/X/A/Z 归本窗口自己的文本框（机理/开关/取证见 Caxa3dMyTools.Contracts.ChordRescue）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Caxa3dMyTools.Contracts.ChordRescue.Install(this, msg => Diagnostics.Trace(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口关闭"); }
        protected override void WndProc(ref Message m) { if (Caxa3dMyTools.Contracts.ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑↑↑

        public ColorCardForm(IZBaseApp app, Action<int> onApplied)
        {
            _app = app;
            _onApplied = onApplied;
            BuildUi();
            LoadCards();
            TopMost = true;
        }

        // ================================================================ 界面

        private void BuildUi()
        {
            Text = "零件涂色（色卡）";
            ClientSize = new Size(460, 300);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);

            // ---- 色卡标准 ----
            GroupBox grpCard = new GroupBox { Text = "色卡标准", Left = 10, Top = 8, Width = 322, Height = 102 };

            _rbRal = new RadioButton { Text = "RAL", Left = 14, Top = 26, Width = 70, Checked = true };
            _rbRal.CheckedChanged += (s, e) => SwitchSeries();
            grpCard.Controls.Add(_rbRal);
            _cmbRal = new ComboBox
            {
                Left = 90, Top = 24, Width = 218, DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems
            };
            _cmbRal.SelectedIndexChanged += (s, e) => ComboPicked(_cmbRal);
            _cmbRal.Leave += (s, e) => ComboPicked(_cmbRal);
            grpCard.Controls.Add(_cmbRal);

            _rbGsb = new RadioButton { Text = "GSB05-1426-2001", Left = 14, Top = 62, Width = 140 };
            grpCard.Controls.Add(_rbGsb);
            _cmbGsb = new ComboBox
            {
                Left = 160, Top = 60, Width = 148, DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems
            };
            _cmbGsb.SelectedIndexChanged += (s, e) => ComboPicked(_cmbGsb);
            _cmbGsb.Leave += (s, e) => ComboPicked(_cmbGsb);
            grpCard.Controls.Add(_cmbGsb);
            Controls.Add(grpCard);

            // ---- RGB 色值 ----
            GroupBox grpRgb = new GroupBox { Text = "RGB色值", Left = 10, Top = 118, Width = 322, Height = 64 };
            _txtR = AddRgbBox(grpRgb, "R", 14, Color.FromArgb(220, 0, 0));
            _txtG = AddRgbBox(grpRgb, "G", 114, Color.FromArgb(0, 170, 0));
            _txtB = AddRgbBox(grpRgb, "B", 214, Color.FromArgb(40, 40, 220));
            Controls.Add(grpRgb);

            // ---- 预览 ----
            _preview = new Panel { Left = 344, Top = 12, Width = 106, Height = 170, BackColor = Color.Black, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(_preview);

            _lblPick = new Label { Text = "未选择色卡", Left = 10, Top = 190, Width = 440, Height = 22, ForeColor = Color.FromArgb(70, 70, 70) };
            Controls.Add(_lblPick);

            // ---- 按钮 ----
            Button btnApply = new Button { Text = "应用颜色", Left = 250, Top = 252, Width = 96, Height = 30 };
            btnApply.Click += (s, e) => ApplyToSelection();
            Controls.Add(btnApply);

            Button btnExit = new Button { Text = "退出", Left = 356, Top = 252, Width = 94, Height = 30 };
            btnExit.Click += (s, e) => Close();
            Controls.Add(btnExit);

            _lblStatus = new Label { Text = "", Left = 10, Top = 222, Width = 440, Height = 20, ForeColor = Color.FromArgb(0, 100, 0) };
            Controls.Add(_lblStatus);
        }

        private TextBox AddRgbBox(Control parent, string name, int left, Color labelColor)
        {
            Label lbl = new Label
            {
                Text = name, Left = left, Top = 28, Width = 20, Height = 22,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = labelColor, ForeColor = Color.White
            };
            parent.Controls.Add(lbl);
            TextBox txt = new TextBox { Left = left + 24, Top = 25, Width = 66, Text = "0" };
            txt.TextChanged += (s, e) => RgbEdited();
            parent.Controls.Add(txt);
            return txt;
        }

        // ================================================================ 数据

        private void LoadCards()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(dir ?? "", "色卡.xml");
            try
            {
                if (!File.Exists(path))
                {
                    _lblPick.Text = "未找到色卡.xml（应与 DLL 同目录），请重新运行安装脚本";
                    _lblPick.ForeColor = Color.FromArgb(160, 0, 0);
                    Diagnostics.Log("色卡加载失败：不存在 " + path);
                    return;
                }

                XmlDocument doc = new XmlDocument();
                doc.Load(path);
                XmlNodeList nodes = doc.SelectNodes("/ColorCardDB/color");
                if (nodes != null)
                {
                    foreach (XmlNode n in nodes)
                    {
                        string series = Attr(n, "series");
                        string display = Attr(n, "display");
                        int rgb;
                        if (!TryParseHex(Attr(n, "rgb"), out rgb)) continue;
                        CardEntry e = new CardEntry { Display = display, Rgb = rgb };
                        if (series == "RAL") _ral.Add(e);
                        else _gsb.Add(e);
                    }
                }

                _cmbRal.Items.AddRange(_ral.ToArray());
                _cmbGsb.Items.AddRange(_gsb.ToArray());
                Diagnostics.Log("色卡已载入：RAL " + _ral.Count + " 色，GSB " + _gsb.Count + " 色（" + path + "）");

                if (_cmbRal.Items.Count > 0) _cmbRal.SelectedIndex = 0;
                SwitchSeries();
            }
            catch (Exception ex)
            {
                _lblPick.Text = "色卡.xml 解析失败：" + ex.Message;
                _lblPick.ForeColor = Color.FromArgb(160, 0, 0);
                Diagnostics.Log("色卡加载异常：" + ex);
            }
        }

        private static string Attr(XmlNode n, string name)
        {
            XmlAttribute a = n.Attributes == null ? null : n.Attributes[name];
            return a == null ? "" : a.Value;
        }

        private static bool TryParseHex(string text, out int rgb)
        {
            rgb = 0;
            if (string.IsNullOrEmpty(text)) return false;
            int v;
            if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out v)) return false;
            rgb = v & 0xFFFFFF;
            return true;
        }

        // ================================================================ 交互

        private void SwitchSeries()
        {
            bool ral = _rbRal.Checked;
            _cmbRal.Enabled = ral;
            _cmbGsb.Enabled = !ral;
            ComboPicked(ral ? _cmbRal : _cmbGsb);
        }

        private void ComboPicked(ComboBox combo)
        {
            if (_syncing) return;
            CardEntry e = combo.SelectedItem as CardEntry;
            if (e == null)
            {
                // 用户手输了文字：尝试精确匹配显示名
                foreach (CardEntry it in combo.Items)
                {
                    if (string.Equals(it.Display, (combo.Text ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        e = it;
                        break;
                    }
                }
            }
            if (e == null) return;

            _syncing = true;
            try
            {
                _txtR.Text = ((e.Rgb >> 16) & 0xFF).ToString();
                _txtG.Text = ((e.Rgb >> 8) & 0xFF).ToString();
                _txtB.Text = (e.Rgb & 0xFF).ToString();
                _preview.BackColor = Color.FromArgb((e.Rgb >> 16) & 0xFF, (e.Rgb >> 8) & 0xFF, e.Rgb & 0xFF);
                _lblPick.ForeColor = Color.FromArgb(70, 70, 70);
                _lblPick.Text = "当前色卡：" + e.Display + "  (#" + e.Rgb.ToString("X6") + ")";
            }
            finally { _syncing = false; }
        }

        private void RgbEdited()
        {
            if (_syncing) return;
            int r, g, b;
            if (!Clamp255(_txtR.Text, out r) || !Clamp255(_txtG.Text, out g) || !Clamp255(_txtB.Text, out b)) return;
            _preview.BackColor = Color.FromArgb(r, g, b);
            _lblPick.ForeColor = Color.FromArgb(70, 70, 70);
            _lblPick.Text = "自定义 RGB：" + r + "," + g + "," + b + "  (#" + ((r << 16) | (g << 8) | b).ToString("X6") + ")";
        }

        private static bool Clamp255(string text, out int v)
        {
            v = 0;
            if (!int.TryParse((text ?? "").Trim(), out v)) return false;
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return true;
        }

        // ================================================================ 应用

        private void ApplyToSelection()
        {
            int r, g, b;
            if (!Clamp255(_txtR.Text, out r) || !Clamp255(_txtG.Text, out g) || !Clamp255(_txtB.Text, out b))
            {
                _lblStatus.Text = "RGB 输入无效（0-255）";
                _lblStatus.ForeColor = Color.FromArgb(160, 0, 0);
                return;
            }
            int rgb = (r << 16) | (g << 8) | b;

            try
            {
                string error;
                List<IZElement> elems = MaterialProbe.GetSelection(_app, out error);
                if (elems.Count == 0)
                {
                    Diagnostics.Log("色卡上色失败：" + (error ?? "没有选中元素。"));
                    _lblStatus.Text = "未选中元素，请先在 CAXA 中选中零件";
                    _lblStatus.ForeColor = Color.FromArgb(160, 0, 0);
                    return;
                }

                Diagnostics.Log("色卡上色：" + _lblPick.Text + "，目标 " + elems.Count + " 个元素");
                StringBuilder report = new StringBuilder();
                int pass = 0, fail = 0;
                foreach (IZElement elem in elems)
                {
                    if (MaterialProbe.ApplyColorOnly(elem, rgb, report)) pass++; else fail++;
                }
                Diagnostics.Log(report.ToString().TrimEnd());
                Diagnostics.Log(string.Format("色卡上色结束：通过 {0} 个，未通过 {1} 个。", pass, fail));

                _lblStatus.Text = string.Format("已应用 #{0}：通过 {1} / 未通过 {2}", rgb.ToString("X6"), pass, fail);
                _lblStatus.ForeColor = fail == 0 ? Color.FromArgb(0, 100, 0) : Color.FromArgb(160, 80, 0);

                if (pass > 0 && _onApplied != null) _onApplied(rgb);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("色卡上色异常：" + ex);
                _lblStatus.Text = "应用异常：" + ex.Message;
                _lblStatus.ForeColor = Color.FromArgb(160, 0, 0);
            }
        }
    }
}
