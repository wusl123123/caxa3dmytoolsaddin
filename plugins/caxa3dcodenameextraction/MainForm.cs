using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 主窗口：带图改名及图号分离。
    /// 流程：选中零部件 → 填新零件名 → 重命名 → 按规则回填属性 → 保存 →
    ///       更新工程图链接（打开同名 exb 代点后保存关闭）→ 焦点回设计环境。
    /// </summary>
    [System.Runtime.InteropServices.ComVisible(false)]
    public class MainForm : Form
    {
        private readonly IZBaseApp _siteApp;
        private CaxaSession _app;
        private CaxaAutomation.SelectedPartInfo _partInfo;
        private string _docName;
        // 进程级守卫（static）：同一 CAXA 进程内只允许一条流程。
        // 原来用实例字段，关窗口再开会话后 `_running` 归 false，旧流程成僵尸与新流程并发，
        // 抢同一个 CRX 请求文件导致 CLOSEDOC/CLOSE 被覆盖（30s+15s 双超时，见规则坑 77）。
        private static volatile bool _running;
        private static volatile bool _stopRequested;
        private string _lastOldName = "";
        private bool _suppressNewNameChanged;
        private bool _topMost;
        private bool _sepEnabled = true;
        private List<Rule> _rules = new List<Rule>();

        private Label _lblConnection;
        private Label _lblOldName;
        private Label _lblOldFlag;
        private TextBox _txtOldName;
        private Label _lblNewName;
        private Label _lblNewFlag;
        private TextBox _txtNewName;
        private ComboBox _cboRule;
        private Button _btnSep;
        private Button _btnManualSep;
        private Button _btnRename;
        private Button _btnSheetMetal;
        private Button _btnTitleBlock;
        private Button _btnStop;
        private Button _btnTopMost;
        private Button _btnRules;
        private Button _btnHelp;
        private Button _btnRefresh;
        private Button _btnRegCrx;
        private Button _btnUnregCrx;
        private Button _btnSaveLog;
        private Button _btnClearLog;
        private TextBox _txtLog;
        private Label _lblStatus;
        private System.Windows.Forms.Timer _timer;

        internal static readonly string AppTitleBase = "带图改名及图号分离";

        public MainForm(IZBaseApp siteApp)
        {
            _siteApp = siteApp;
            // 关窗口必须打停后台流程：否则流程成僵尸，与新会话并发抢 CRX 请求文件（规则坑 77）。
            // 不阻塞关闭，只置停止标志，由流程在下一个断点自行退出。
            FormClosing += (sender, e) =>
            {
                if (_running)
                {
                    _stopRequested = true;
                    Diagnostics.Log("MainForm.FormClosing: 已请求停止后台流程");
                }
            };
            BuildUi();
            Diagnostics.UiLogSink = AddLog;
            if (!IsHandleCreated) CreateHandle();
            LoadRules();
            AddLog("程序启动成功");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryConnect();
        }

        // ================================================================ 界面

        private void BuildUi()
        {
            Text = AppTitleBase + "-" + BuildVersion();
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 586);
            // 最小宽度按“新零件名输入框 560 + 右侧标记 140”定，避免缩小时右标记被裁掉
            MinimumSize = new Size(836, 546);
            MaximizeBox = true;

            var panelTop = new Panel { Dock = DockStyle.Top, Height = 44 };
            _btnTopMost = MakeButton("窗口前置", 10, 8, 80);
            // 多插件集合体铁律 2：工具窗口默认置顶，用户可用「窗口前置」按钮解除
            _topMost = true;
            TopMost = true;
            _btnTopMost.BackColor = Color.FromArgb(0, 120, 212);
            _btnTopMost.ForeColor = Color.White;
            _btnTopMost.FlatStyle = FlatStyle.Flat;
            _btnTopMost.FlatAppearance.BorderSize = 0;
            _btnRules = MakeButton("图号分离规则", 96, 8, 120);
            _btnHelp = MakeButton("使用说明", 222, 8, 100);
            _btnRefresh = MakeButton("刷新连接", 328, 8, 90);
            _btnRegCrx = MakeButton("注册CRX", 424, 8, 90);
            _btnUnregCrx = MakeButton("卸载CRX", 520, 8, 90);
            _btnSaveLog = MakeButton("保存日志", 616, 8, 90);
            _btnClearLog = MakeButton("清理日志", 712, 8, 90);
            panelTop.Controls.AddRange(new Control[] {
                _btnTopMost, _btnRules, _btnHelp, _btnRefresh,
                _btnRegCrx, _btnUnregCrx, _btnSaveLog, _btnClearLog });

            var panelBody = new Panel { Dock = DockStyle.Top, Height = 276, Padding = new Padding(10) };

            _lblConnection = new Label
            {
                Text = "连接状态: 未连接",
                Location = new Point(10, 10),
                Size = new Size(780, 24),
                ForeColor = Color.FromArgb(209, 52, 56),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            // 多插件集合体铁律 3：不显示连接状态标签（连接状态由 CAXA 自身反映）
            _lblConnection.Visible = false;

            _lblOldName = new Label { Text = "原零件名:", Location = new Point(10, 54), Size = new Size(80, 24) };
            _txtOldName = new TextBox
            {
                Location = new Point(96, 50),
                Size = new Size(560, 26),
                ReadOnly = true,
                BackColor = Color.White
            };
            var menuOld = new ContextMenuStrip();
            menuOld.Items.Add("复制", null, (s, e) => CopyOldName());
            _txtOldName.ContextMenuStrip = menuOld;
            _lblOldFlag = new Label
            {
                Text = "",
                Location = new Point(664, 54),
                Size = new Size(140, 24),
                ForeColor = Color.DimGray
            };

            _lblNewName = new Label { Text = "新零件名:", Location = new Point(10, 96), Size = new Size(80, 24) };
            _txtNewName = new TextBox { Location = new Point(96, 92), Size = new Size(560, 26) };
            var menuNew = new ContextMenuStrip();
            menuNew.Items.Add("复制", null, (s, e) => CopyNewName());
            menuNew.Items.Add("粘贴", null, (s, e) => PasteNewName());
            menuNew.Items.Add("全选", null, (s, e) => _txtNewName.SelectAll());
            _txtNewName.ContextMenuStrip = menuNew;
            _lblNewFlag = new Label
            {
                Text = "",
                Location = new Point(664, 96),
                Size = new Size(140, 24),
                ForeColor = Color.DimGray
            };

            var lblRule = new Label { Text = "分离规则:", Location = new Point(10, 138), Size = new Size(90, 24) };
            _cboRule = new ComboBox
            {
                Location = new Point(110, 134),
                Size = new Size(495, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            _btnSep = new Button
            {
                Text = "自动图号分离",
                Location = new Point(613, 133),
                Size = new Size(120, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };

            _btnRename = new Button
            {
                Text = "重命名",
                Location = new Point(96, 174),
                Size = new Size(200, 36),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnRename.FlatAppearance.BorderSize = 0;

            _btnManualSep = MakeButton("手动图号分离", 96, 218, 110);
            _btnSheetMetal = MakeButton("钣金件设计环境设默认", 214, 218, 200);
            _btnTitleBlock = MakeButton("工程图标题栏设默认", 422, 218, 200);
            _btnStop = MakeButton("停止", 630, 218, 70);
            _btnStop.Enabled = false;

            _lblStatus = new Label
            {
                Text = "就绪",
                Location = new Point(310, 182),
                Size = new Size(470, 24),
                ForeColor = Color.DimGray
            };

            panelBody.Controls.AddRange(new Control[] {
                _lblConnection, _lblOldName, _txtOldName, _lblOldFlag,
                _lblNewName, _txtNewName, _lblNewFlag, lblRule, _cboRule,
                _btnSep, _btnRename, _btnManualSep, _btnSheetMetal, _btnTitleBlock, _btnStop, _lblStatus });

            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 9F)
            };

            Controls.Add(_txtLog);
            Controls.Add(panelBody);
            Controls.Add(panelTop);

            _btnTopMost.Click += (s, e) => ToggleTopMost();
            _btnSep.Click += (s, e) => ToggleSep();
            ApplySepVisual();
            _btnRules.Click += (s, e) => OpenResource("caxa3dcodenameextraction图号分离规则.txt");
            _btnHelp.Click += (s, e) => OpenResource("caxa3dcodenameextraction使用说明.txt");
            _btnRefresh.Click += (s, e) => { AddLog("正在刷新连接状态..."); TryConnect(); };
            _btnRegCrx.Click += (s, e) => RunCrxScript(true);
            _btnUnregCrx.Click += (s, e) => RunCrxScript(false);
            _btnSaveLog.Click += BtnSaveLog_Click;
            _btnClearLog.Click += (s, e) => { _txtLog.Clear(); };
            _txtNewName.TextChanged += (s, e) =>
            {
                if (_suppressNewNameChanged) return;
                UpdateNewNameFlag();
            };
            _btnRename.Click += BtnRename_Click;
            _btnManualSep.Click += BtnManualSep_Click;
            _btnSheetMetal.Click += BtnSheetMetal_Click;
            _btnTitleBlock.Click += BtnTitleBlock_Click;
            _btnStop.Click += (s, e) => StopFlow();

            _timer = new System.Windows.Forms.Timer { Interval = 800 };
            _timer.Tick += (s, e) => { if (Visible && !_running) RefreshInfo(false); };
            _timer.Start();
        }

        private static Button MakeButton(string text, int x, int y, int w)
        {
            return new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 28) };
        }

        /// <summary>窗口前置开关：点一次锁定在 CAXA 之上（方便边选零部件边看本窗口），再点一次解锁。</summary>
        private void ToggleTopMost()
        {
            _topMost = !_topMost;
            TopMost = _topMost;
            _btnTopMost.BackColor = _topMost ? Color.FromArgb(0, 120, 212) : SystemColors.Control;
            _btnTopMost.ForeColor = _topMost ? Color.White : SystemColors.ControlText;
            _btnTopMost.FlatStyle = FlatStyle.Flat;
            _btnTopMost.FlatAppearance.BorderSize = _topMost ? 0 : 1;
            AddLog(_topMost ? "窗口已锁定在最上层" : "窗口已取消最上层");
        }

        /// <summary>图号分离开关：默认开启；关闭后重命名只做改名/保存/图纸同步，不回填 SC 属性。</summary>
        private void ToggleSep()
        {
            _sepEnabled = !_sepEnabled;
            ApplySepVisual();
            AddLog(_sepEnabled ? "图号分离已开启（将按规则回填 SC 属性）"
                               : "图号分离已关闭（仅重命名，不回填属性）");
        }

        private void ApplySepVisual()
        {
            _btnSep.BackColor = _sepEnabled ? Color.FromArgb(0, 120, 212) : Color.White;
            _btnSep.ForeColor = _sepEnabled ? Color.White : SystemColors.ControlText;
            _btnSep.FlatStyle = FlatStyle.Flat;
            _btnSep.FlatAppearance.BorderSize = _sepEnabled ? 0 : 1;
            _cboRule.Enabled = _sepEnabled;
        }

        /// <summary>标题版本号：取 DLL 写入时间 yyyyMMddHHmm（编译即更新）。</summary>
        private static string BuildVersion()
        {
            try
            {
                return File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location)
                    .ToString("yyyyMMddHHmm");
            }
            catch { return "000000000000"; }
        }

        // ================================================================ 复制/粘贴

        /// <summary>
        /// 只读的原零件名输入框默认吞掉 Ctrl+C，这里显式支持快捷键复制；
        /// 新零件名输入框同时支持 Ctrl+C / Ctrl+V / Ctrl+A，避免 CAXA 宿主抢占按键。
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
            {
                Control focused = ActiveControl;
                TextBox tb = focused as TextBox;
                if (tb == null && focused is ContainerControl)
                    tb = ((ContainerControl)focused).ActiveControl as TextBox;
                if (tb == _txtOldName) { CopyOldName(); return true; }
                if (tb == _txtNewName) { CopyNewName(); return true; }
            }
            else if (keyData == (Keys.Control | Keys.V))
            {
                TextBox tb = ActiveControl as TextBox;
                if (tb != null && tb == _txtNewName) { PasteNewName(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void CopyOldName()
        {
            try
            {
                if (_txtOldName.TextLength > 0) Clipboard.SetText(_txtOldName.Text);
            }
            catch (Exception ex) { Diagnostics.Trace("CopyOldName: " + ex.Message); }
        }

        private void CopyNewName()
        {
            try
            {
                if (_txtNewName.SelectedText.Length > 0) Clipboard.SetText(_txtNewName.SelectedText);
                else if (_txtNewName.TextLength > 0) Clipboard.SetText(_txtNewName.Text);
            }
            catch (Exception ex) { Diagnostics.Trace("CopyNewName: " + ex.Message); }
        }

        private void PasteNewName()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string text = Clipboard.GetText().Trim();
                if (text.Length == 0) return;
                _txtNewName.SelectedText = text;
                UpdateNewNameFlag();
            }
            catch (Exception ex) { Diagnostics.Trace("PasteNewName: " + ex.Message); }
        }

        // ================================================================ 连接

        private void TryConnect()
        {
            try
            {
                if (_app != null) _app.Dispose();
                _app = CaxaAutomation.ConnectFromSite(_siteApp);
                Diagnostics.Log("ConnectFromSite ok");
                RefreshInfo(false);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ConnectFromSite failed: " + ex);
                _app = null;
                _lblConnection.Text = "连接状态: 连接失败";
                _lblConnection.ForeColor = Color.FromArgb(209, 52, 56);
                AddLog("连接 CAXA 3D 失败: " + ex.Message);
            }
        }

        /// <summary>刷新文档/选中零部件显示。silent=true 时只更新界面，不写日志。</summary>
        private void RefreshInfo(bool silent)
        {
            if (_app == null) return;
            try
            {
                string docName, elemName;
                CaxaAutomation.ProbeSessionInfo(_app, out docName, out elemName);
                _docName = docName;
                _partInfo = CaxaAutomation.GetSelectedPartInfo(_app);

                string doc = string.IsNullOrEmpty(docName) ? "" : Path.GetFileName(docName);
                if (string.IsNullOrEmpty(doc))
                {
                    _lblConnection.Text = "连接状态: 已连接（无活动文档）";
                    _lblConnection.ForeColor = Color.FromArgb(209, 52, 56);
                }
                else if (_partInfo == null || !_partInfo.HasElement)
                {
                    _lblConnection.Text = "连接状态: " + doc + " ----- 未选中零部件";
                    _lblConnection.ForeColor = Color.FromArgb(180, 120, 0);
                }
                else
                {
                    string partText = _partInfo.ExternallyLinked
                        ? Path.GetFileName(_partInfo.LinkedFile)
                        : (_partInfo.ElementName + "(虚拟)");
                    _lblConnection.Text = "连接状态: " + doc + " ----- " + partText;
                    _lblConnection.ForeColor = Color.FromArgb(16, 124, 16);
                }

                bool has = _partInfo != null && _partInfo.HasElement;
                string oldName = has ? _partInfo.ElementName : "";
                if (_txtOldName.Text != oldName) _txtOldName.Text = oldName;

                // 选中零部件发生变化时，新零件名自动填入原零件名（用户再按需修改）
                if (oldName != _lastOldName)
                {
                    _lastOldName = oldName;
                    SetNewNameText(oldName);
                }

                bool hasDrawing = has && HasDrawingFile();
                _lblOldFlag.Text = !has ? "" : (hasDrawing ? "存在工程图" : "不存在工程图");
                _lblOldFlag.ForeColor = hasDrawing ? Color.FromArgb(16, 124, 16) : Color.DimGray;

                UpdateActionState();
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("RefreshInfo: " + ex.Message);
            }
        }

        /// <summary>程序化改写新零件名（不触发“用户已编辑”标记）。</summary>
        private void SetNewNameText(string text)
        {
            _suppressNewNameChanged = true;
            try { _txtNewName.Text = text ?? ""; }
            finally { _suppressNewNameChanged = false; }
            UpdateNewNameFlag();
        }

        /// <summary>新零件名是否与文件夹内已有 ics 重名（重名禁止重命名）。</summary>
        private bool NewNameExists()
        {
            string folder = PartFolder();
            string name = _txtNewName.Text.Trim();
            if (folder == null || name.Length == 0) return false;
            try { return File.Exists(Path.Combine(folder, name + ".ics")); }
            catch { return false; }
        }

        /// <summary>统一刷新提示文字与“重命名”按钮可用状态。</summary>
        private void UpdateActionState()
        {
            bool has = _partInfo != null && _partInfo.HasElement;
            bool linked = has && _partInfo.ExternallyLinked;
            bool conflict = NewNameExists();
            string name = _txtNewName.Text.Trim();

            if (has && !linked)
            {
                _lblStatus.Text = "虚拟零部件：请先保存为外部文件（另存为 ics）后再重命名";
                _lblStatus.ForeColor = Color.FromArgb(180, 120, 0);
            }
            else if (conflict)
            {
                _lblStatus.Text = "零件名已存在，不允许重命名";
                _lblStatus.ForeColor = Color.FromArgb(209, 52, 56);
            }
            else if (linked && name.Length > 0)
            {
                _lblStatus.Text = "就绪";
                _lblStatus.ForeColor = Color.DimGray;
            }
            else
            {
                _lblStatus.Text = has ? "请输入新零件名" : "请在 CAXA 3D 中选中零部件";
                _lblStatus.ForeColor = Color.DimGray;
            }

            _btnRename.Enabled = has && linked && !_running && name.Length > 0 && !conflict;
            _btnManualSep.Enabled = has && !_running;
            _btnSheetMetal.Enabled = !_running;
            _btnTitleBlock.Enabled = !_running;
        }

        private string PartFolder()
        {
            if (_partInfo == null || !_partInfo.ExternallyLinked) return null;
            try { return Path.GetDirectoryName(_partInfo.LinkedFile); }
            catch { return null; }
        }

        /// <summary>零部件所在文件夹内是否有同名 exb。</summary>
        private bool HasDrawingFile()
        {
            if (_partInfo == null || !_partInfo.ExternallyLinked) return false;
            try
            {
                string folder = Path.GetDirectoryName(_partInfo.LinkedFile);
                if (string.IsNullOrEmpty(folder)) return false;
                string baseName = Path.GetFileNameWithoutExtension(_partInfo.LinkedFile);
                return File.Exists(Path.Combine(folder, baseName + ".exb"));
            }
            catch { return false; }
        }

        private void UpdateNewNameFlag()
        {
            string folder = PartFolder();
            string name = _txtNewName.Text.Trim();
            if (folder == null)
            {
                _lblNewFlag.Text = _partInfo != null && _partInfo.HasElement ? "虚拟零部件" : "";
                _lblNewFlag.ForeColor = Color.FromArgb(180, 120, 0);
            }
            else if (name.Length == 0)
            {
                _lblNewFlag.Text = "";
            }
            else
            {
                try
                {
                    bool exists = File.Exists(Path.Combine(folder, name + ".ics"));
                    _lblNewFlag.Text = exists ? "零件名已存在" : "零件不存在";
                    _lblNewFlag.ForeColor = exists ? Color.FromArgb(209, 52, 56) : Color.DimGray;
                }
                catch { _lblNewFlag.Text = ""; }
            }
            UpdateActionState();
        }

        // ================================================================ 规则

        /// <summary>图号分离规则：分隔符 + 目标属性顺序。</summary>
        private sealed class Rule
        {
            public string Text;
            public char Separator;
            public string[] Fields;

            public override string ToString() { return Text; }
        }

        private void LoadRules()
        {
            _rules.Clear();
            _cboRule.Items.Clear();
            string path = FindResource("caxa3dcodenameextraction图号分离规则.txt");
            if (path == null)
            {
                AddLog("未找到图号分离规则文件，使用内置规则");
                _rules.AddRange(BuiltinRules());
            }
            else
            {
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    TryParseRule(line);
                if (_rules.Count == 0) _rules.AddRange(BuiltinRules());
            }
            foreach (Rule r in _rules) _cboRule.Items.Add(r);
            if (_cboRule.Items.Count > 0) _cboRule.SelectedIndex = 0;
            AddLog("已加载 " + _cboRule.Items.Count + " 条图号分离规则");
        }

        private static IEnumerable<Rule> BuiltinRules()
        {
            yield return new Rule { Text = "A自制件.间隔符\" \"----\"SC图号 SC名称\"", Separator = ' ', Fields = new[] { "SC图号", "SC名称" } };
            yield return new Rule { Text = "B外购件.间隔符\"_\"----\"SC名称_SC规格_SC材料_SC图号\"", Separator = '_', Fields = new[] { "SC名称", "SC规格", "SC材料", "SC图号" } };
            yield return new Rule { Text = "C外购件.间隔符\"_\"----\"SC名称_SC规格_SC材料\"", Separator = '_', Fields = new[] { "SC名称", "SC规格", "SC材料" } };
            yield return new Rule { Text = "D外购件.间隔符\"_\"----\"SC名称_SC规格_SC图号\"", Separator = '_', Fields = new[] { "SC名称", "SC规格", "SC图号" } };
            yield return new Rule { Text = "E外购件.间隔符\"_\"----\"SC名称_SC规格\"", Separator = '_', Fields = new[] { "SC名称", "SC规格" } };
        }

        /// <summary>解析一行：A自制件.间隔符" "----"SC图号 SC名称"。</summary>
        private void TryParseRule(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            int p = line.IndexOf(".间隔符", StringComparison.Ordinal);
            if (p < 0) return;
            string rest = line.Substring(p + 4).Trim();
            if (rest.Length < 3) return;

            char quote = rest[0];
            if (quote != '"' && quote != '\'') return;
            int endSep = rest.IndexOf(quote, 1);
            if (endSep <= 1) return;
            string sepText = rest.Substring(1, endSep - 1);
            if (sepText.Length != 1) return;
            char sep = sepText[0];

            int dash = rest.IndexOf("----", endSep, StringComparison.Ordinal);
            if (dash < 0) return;
            string fieldPart = rest.Substring(dash + 4).Trim();
            if (fieldPart.Length < 2) return;
            char q2 = fieldPart[0];
            int endFields = fieldPart.LastIndexOf(q2);
            if (endFields <= 0) return;
            string fieldsText = fieldPart.Substring(1, endFields - 1);

            string[] raw = fieldsText.Split(sep);
            var fields = new List<string>();
            foreach (string f in raw)
            {
                string t = f.Trim();
                if (t.Length > 2 && (t.StartsWith("SC", StringComparison.OrdinalIgnoreCase)))
                    t = t.Substring(2);
                if (t.Length > 0) fields.Add("SC" + t);
            }
            if (fields.Count == 0) return;

            _rules.Add(new Rule { Text = line.Trim(), Separator = sep, Fields = fields.ToArray() });
        }

        // ================================================================ 重命名主流程

        private void BtnRename_Click(object sender, EventArgs e)
        {
            if (_running) return;
            if (_app == null) { MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase); return; }
            if (_partInfo == null || !_partInfo.HasElement)
            {
                MessageBox.Show("请先在 CAXA 3D 中选中要改名的零部件。", AppTitleBase);
                return;
            }
            if (!_partInfo.ExternallyLinked)
            {
                MessageBox.Show(
                    "当前选中的是虚拟零部件（没有外部 .ics 文件）。\n" +
                    "请先在 CAXA 3D 中把该零部件“另存为”外部文件（保存成 ics），\n" +
                    "再选中它点击重命名。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string newName = _txtNewName.Text.Trim();
            if (newName.Length == 0)
            {
                MessageBox.Show("请先填写新零件名。", AppTitleBase);
                return;
            }
            if (NewNameExists())
            {
                MessageBox.Show(
                    "文件夹内已存在同名零件：" + newName + ".ics\n不允许重命名，请换一个零件名。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (newName.IndexOf(c) >= 0)
                {
                    MessageBox.Show("新零件名包含非法字符：" + c, AppTitleBase);
                    return;
                }
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_sepEnabled)
            {
                Rule rule = _cboRule.SelectedItem as Rule;
                if (rule == null)
                {
                    MessageBox.Show("请选择图号分离规则。", AppTitleBase);
                    return;
                }

                string[] tokens = newName.Split(rule.Separator);
                if (tokens.Length != rule.Fields.Length)
                {
                    MessageBox.Show(string.Format(
                        "按规则“{0}”应有 {1} 段（分隔符“{2}”），当前为 {3} 段。\n新零件名：{4}",
                        rule.Text, rule.Fields.Length, rule.Separator, tokens.Length, newName),
                        AppTitleBase);
                    return;
                }

                for (int i = 0; i < tokens.Length; i++)
                    values[rule.Fields[i]] = tokens[i].Trim();
            }

            string oldBase = null;
            string folder = PartFolder();
            if (_partInfo.ExternallyLinked)
            {
                oldBase = Path.GetFileNameWithoutExtension(_partInfo.LinkedFile);
                if (string.IsNullOrEmpty(folder))
                    folder = Path.GetDirectoryName(_partInfo.LinkedFile);
            }
            string newIcs = folder != null ? Path.Combine(folder, newName + ".ics") : null;
            string newExb = folder != null ? Path.Combine(folder, newName + ".exb") : null;
            string oldExb = (folder != null && oldBase != null) ? Path.Combine(folder, oldBase + ".exb") : null;

            _running = true;
            _stopRequested = false;   // 清掉上一次「停止」留下的标志，否则 WaitUntil 会立刻中断
            _btnRename.Enabled = false;
            _lblStatus.Text = "处理中...";
            PopupAutoYes.FileDialogTarget = newIcs;
            PopupAutoYes.Enabled = true;
            Diagnostics.Log("==== 重命名开始: " + _partInfo.ElementName + " -> " + newName);

            string errorMessage = null;
            try
            {
                // 1) 先回填图号分离属性（此时选中项一定有效；改名后 CAXA 可能清空选择）
                if (_sepEnabled)
                {
                    AddLog("1/6 回填图号分离属性");
                    CaxaAutomation.WriteFirstElementCustomProps(_app, values);
                    foreach (var kv in values) AddLog("    " + kv.Key + " = " + kv.Value);
                }
                else
                {
                    AddLog("1/6 图号分离已关闭，跳过属性回填");
                }

                // 2) 回填零件名
                AddLog("2/6 回填零件名: " + newName);
                CaxaAutomation.RenameSelectedElement(_app, newName);

                // 3) 保存 ics
                AddLog("3/6 保存零部件 ics...");
                CaxaAutomation.SaveActiveDoc(_app);
                if (newIcs != null)
                {
                    bool renamed = WaitUntil("等待 ics 改名", 15000, () => File.Exists(newIcs));
                    AddLog(renamed ? "    本地 ics 已改名: " + Path.GetFileName(newIcs)
                                   : "    警告：未检测到新 ics 文件，请检查 CAXA 2027 选项设置");
                }

                // 4) 更新工程图链接
                string exb = null;
                if (newExb != null && File.Exists(newExb)) exb = newExb;
                else if (oldExb != null && File.Exists(oldExb)) exb = oldExb;

                if (exb == null)
                {
                    AddLog("4/6 未找到同名 exb，仅完成重命名和图号分离");
                }
                else
                {
                    AddLog("4/6 打开工程图: " + Path.GetFileName(exb));
                    OpenAndCloseDrawing(exb);
                }

                // 流程结束：不再执行 CAXA“重新生成”（多零件装配下极慢，用户已要求去掉）。
                WaitUntil("等待回到设计环境", 10000,
                    () => CaxaAutomation.IsActiveSceneDocument(_app));

                _lblStatus.Text = "完成: " + newName;
                AddLog("完成: " + newName);
                // 更新后：记录新名（避免刷新时又把“已存在的新名”填进输入框并报重名）
                _lastOldName = newName;
                SetNewNameText("");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("rename flow failed: " + ex);
                _lblStatus.Text = "失败";
                AddLog("失败: " + ex.Message);
                errorMessage = ex.Message;
            }
            finally
            {
                PopupAutoYes.Enabled = false;
                PopupAutoYes.FileDialogTarget = null;
                _running = false;
                RefreshInfo(false);
                RestoreDesignFocus();
            }

            // 提示框放在关闭代点之后：否则本进程的 MessageBox 会被 PopupAutoYes 自动点掉
            Thread.Sleep(300);
            if (errorMessage != null)
                MessageBox.Show("重命名失败：\n" + errorMessage, AppTitleBase,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                MessageBox.Show((_sepEnabled ? "重命名与图号分离完成：\n" : "重命名完成（未做图号分离）：\n") + newName,
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 打开 exb（弹窗自动点是/自动指向新 ics）→ 等就绪 → CRX 原生保存并关闭图纸。
        /// </summary>
        private void OpenAndCloseDrawing(string exbPath)
        {
            string fileName = Path.GetFileName(exbPath);
            DateTime openStart = DateTime.Now;
            int clickBase = PopupAutoYes.ClickCount;

            int pingSeq = CrxBridge.PostCommand("CMD:PING");
            string pingAck = "REQ.ack | seq=" + pingSeq;

            try
            {
                bool ao;
                _siteApp.OpenFile2(exbPath, false, true, out ao);
                AddLog("5/6 工程图已打开，处理更新弹窗...");
            }
            catch (Exception ex)
            {
                // COM 对 exb 的返回不可靠，吞掉
                Diagnostics.Log("OpenFile2 返回异常（正常）: " + ex.Message);
            }

            // 等 CRX 就绪（冷启动时 init 会删除请求文件，需补投 PING）
            bool readyCrx = WaitUntil("等CRX就绪", 120000, () =>
            {
                if (LogCount(pingAck) > 0) return true;
                if (!File.Exists(CrxBridge.RequestPath))
                    pingAck = "REQ.ack | seq=" + CrxBridge.PostCommand("CMD:PING");
                return false;
            });
            if (!readyCrx) AddLog("    警告：CRX 未就绪（可点“注册CRX”后再试）");

            bool ready = WaitUntil("等图纸就绪", 30000, () =>
                PopupAutoYes.ClickCount > clickBase
                || (OpenEndedSince(openStart) && PopupAutoYes.ModalCount == 0));
            AddLog(ready ? "    图纸就绪" : "    警告：等图纸就绪超时");

            int doneBase = LogCount("close-done");
            int viewBase = LogCount("commandEnded | CX_DFT_ViewClose");
            CrxBridge.PostCommand("CMD:CLOSEDOC|" + fileName);
            bool closed = WaitUntil("保存并关闭图纸", 30000, () =>
                LogCount("close-done") > doneBase
                || LogCount("commandEnded | CX_DFT_ViewClose") > viewBase);

            if (!closed)
            {
                CrxBridge.PostCommand("CMD:CLOSE");
                closed = WaitUntil("关闭图纸兜底", 15000, () =>
                    LogCount("close-done") > doneBase
                    || LogCount("commandEnded | CX_DFT_ViewClose") > viewBase);
            }
            AddLog("6/6 工程图" + (closed ? "已保存并关闭" : "关闭超时，请人工检查"));
        }

        // ================================================================ 独立功能：钣金件 / 工程图标题栏

        /// <summary>长流程公共外壳：锁定按钮、开启代点、统一收尾、三支提示（已停止/失败/完成）。</summary>
        private void RunLongFlow(string flowName, Action body)
        {
            _running = true;
            _stopRequested = false;
            _btnRename.Enabled = false;
            _btnManualSep.Enabled = false;
            _btnSheetMetal.Enabled = false;
            _btnTitleBlock.Enabled = false;
            _btnStop.Enabled = true;
            PopupAutoYes.Enabled = true;
            Cursor = Cursors.WaitCursor;
            AddLog("");
            AddLog("========== " + flowName + " 开始 ==========");

            string error = null;
            bool stopped = false;
            try { body(); }
            catch (OperationStoppedException) { stopped = true; }
            catch (Exception ex)
            {
                Diagnostics.Log(flowName + " failed: " + ex);
                error = ex.Message;
            }
            finally
            {
                PopupAutoYes.Enabled = false;
                PopupAutoYes.FileDialogTarget = null;
                PopupAutoYes.Suspend = false;
                _btnStop.Enabled = false;
                _running = false;
                Cursor = Cursors.Default;
                RefreshInfo(false);
                RestoreDesignFocus();
            }

            // 提示框放在关闭代点之后：否则本进程的 MessageBox 会被 PopupAutoYes 自动点掉
            Thread.Sleep(300);
            if (stopped)
            {
                _lblStatus.Text = "已停止";
                AddLog("已停止");
                MessageBox.Show("已停止。若图纸已经打开且未保存，请到 CAXA 图纸环境手工保存并关闭。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (error != null)
            {
                _lblStatus.Text = "失败";
                AddLog("失败: " + error);
                MessageBox.Show(flowName + " 失败：\n" + error, AppTitleBase,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                _lblStatus.Text = "完成";
                AddLog("完成: " + flowName);
                MessageBox.Show(flowName + " 完成。", AppTitleBase,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>「停止」：只置标志并让 CRX 停派发，不强行保存、不关图。</summary>
        private void StopFlow()
        {
            if (!_running) return;
            _stopRequested = true;
            AddLog("已请求停止，正在中止当前流程...");
            try { CrxBridge.PostCommand("CMD:TB_ABORT"); }
            catch (Exception ex) { Diagnostics.Trace("StopFlow PostCommand: " + ex.Message); }
        }

        /// <summary>固定时长消息泵（短时刷新 UI，不承担等待职责）。</summary>
        private void PumpMessages(int ms)
        {
            int elapsed = 0;
            while (elapsed < ms)
            {
                Application.DoEvents();
                Thread.Sleep(30);
                elapsed += 30;
                if (_stopRequested) return;
            }
        }

        /// <summary>两个独立功能的公共前置检查：连接、选中零部件、外部 .ics（可选同名工程图）。</summary>
        private bool CheckLinkedSelection(bool needDrawing)
        {
            if (_app == null)
            {
                MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase);
                return false;
            }
            if (_partInfo == null || !_partInfo.HasElement)
            {
                MessageBox.Show("请先在 CAXA 3D 中选中要处理的零部件。", AppTitleBase);
                return false;
            }
            if (!_partInfo.ExternallyLinked)
            {
                MessageBox.Show("当前选中的是虚拟零部件（没有外部 .ics 文件）。\n请先在 CAXA 3D 中把它“另存为”外部文件。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (needDrawing && !HasDrawingFile())
            {
                MessageBox.Show("零件所在文件夹内没有同名工程图（.exb），无法处理标题栏。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        private string DrawingPath()
        {
            string folder = PartFolder();
            if (folder == null || _partInfo == null || string.IsNullOrEmpty(_partInfo.LinkedFile)) return null;
            return Path.Combine(folder, Path.GetFileNameWithoutExtension(_partInfo.LinkedFile) + ".exb");
        }

        /// <summary>手动图号分离：只对选中件按规则回填 SC 属性并保存，不改名。
        /// 任意零部件均可（含虚拟件，无外部 .ics 限制）。
        /// 名称来源：优先用「新零件名」输入框内容，为空则用原零件名。</summary>
        private void BtnManualSep_Click(object sender, EventArgs e)
        {
            if (_running) return;
            if (_app == null)
            {
                MessageBox.Show("尚未连接 CAXA 3D。", AppTitleBase);
                return;
            }
            if (_partInfo == null || !_partInfo.HasElement)
            {
                MessageBox.Show("请先在 CAXA 3D 中选中要做图号分离的零部件。", AppTitleBase);
                return;
            }

            Rule rule = _cboRule.SelectedItem as Rule;
            if (rule == null)
            {
                MessageBox.Show("请选择分离规则。", AppTitleBase);
                return;
            }

            string name = _txtNewName.Text.Trim();
            if (name.Length == 0) name = _partInfo.ElementName;
            string[] tokens = name.Split(rule.Separator);
            if (tokens.Length != rule.Fields.Length)
            {
                MessageBox.Show(string.Format(
                    "按规则“{0}”应有 {1} 段（分隔符“{2}”），当前为 {3} 段。\n用于分离的名称：{4}",
                    rule.Text, rule.Fields.Length, rule.Separator, tokens.Length, name),
                    AppTitleBase);
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < tokens.Length; i++)
                values[rule.Fields[i]] = tokens[i].Trim();

            RunLongFlow("手动图号分离", delegate
            {
                AddLog("回填图号分离属性（来源名称: " + name + "，不改名）");
                CaxaAutomation.WriteFirstElementCustomProps(_app, values);
                foreach (var kv in values) AddLog("    " + kv.Key + " = " + kv.Value);
                AddLog("保存零部件 ics...");
                CaxaAutomation.SaveActiveDoc(_app);
            });
        }

        /// <summary>钣金件设计环境设默认：只跑「打开 → 展开 → 改名 → 配置还原默认 → 保存 → 关闭」六步。</summary>
        private void BtnSheetMetal_Click(object sender, EventArgs e)
        {
            if (_running) return;
            if (!CheckLinkedSelection(false)) return;
            if (_partInfo.ElementType != 20)
            {
                MessageBox.Show("当前选中的不是钣金件（元素类型 " + _partInfo.ElementType + "）。\n该功能只对钣金件生效。",
                    AppTitleBase, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RunLongFlow("钣金件设计环境设默认", delegate
            {
                SheetMetalOps.NameUpdateOptions opt = new SheetMetalOps.NameUpdateOptions();
                opt.OpenDrawingFirst = false;
                opt.SaveIcs = true;
                opt.CloseIcs = true;
                opt.SaveAndCloseDrawing = false;
                opt.UpdateTitleBlock = false;
                opt.WaitCondition = delegate(string what, int timeoutMs, Func<bool> cond)
                {
                    return WaitUntil(what, timeoutMs, cond);
                };

                SheetMetalOps.SheetMetalProbeResult r = SheetMetalOps.RunSheetMetalProbeInLinkedDoc(
                    _app, true, true, AddLog, delegate { return _stopRequested; }, PumpMessages, opt);

                AddLog("");
                AddLog("---------- 执行结果 ----------");
                AddLog("链接文档        : " + r.TargetDocName);
                AddLog("链接文档中钣金件: " + (string.IsNullOrEmpty(r.FoundSheetMetalName)
                    ? "（未找到）" : r.FoundSheetMetalName));
                AddLog("展开件名        : " + r.AfterFlatPartName);
                AddLog("改名            : " + (r.NameChanged ? "成功" : "未成功"));
                AddLog(".ics 保存       : " + (r.IcsSaved ? "成功" : "失败/未执行") + " " + r.IcsSaveHr);
                AddLog(".ics 关闭       : " + (r.IcsClosed ? "已关闭" : "未关闭") + " " + r.IcsCloseHr);
                foreach (string note in r.Notes) AddLog("注意: " + note);

                if (_stopRequested) throw new OperationStoppedException();
                if (!r.NameChanged)
                    throw new Exception(r.Notes.Count > 0 ? r.Notes[0] : "展开件名未发生改变，详见日志。");
            });
        }

        /// <summary>工程图标题栏设默认：打开同名 exb → 派发 CX_DFT_TITLEBLOCK_3DSET → 保存并关闭。</summary>
        private void BtnTitleBlock_Click(object sender, EventArgs e)
        {
            if (_running) return;
            if (!CheckLinkedSelection(true)) return;

            string exb = DrawingPath();
            if (exb == null || !File.Exists(exb))
            {
                MessageBox.Show("未找到同名工程图（.exb）。", AppTitleBase,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RunLongFlow("工程图标题栏设默认", delegate { UpdateTitleBlock(exb); });
        }

        /// <summary>
        /// 标题栏 3D 信息设默认。判据链照抄 caxa3dtitleupdate 已验证实现，
        /// 仅把兜底延迟压到 10s 以内（用户要求）。
        /// </summary>
        private void UpdateTitleBlock(string exbPath)
        {
            string fileName = Path.GetFileName(exbPath);
            DateTime openStart = DateTime.Now;

            // 0) 预投 PING 只作冷启动探针，不阻塞等 ack（权威就绪信号是 CX_open_Impl 结束）
            int pingSeq = CrxBridge.PostCommand("CMD:PING");
            string pingAck = "REQ.ack | seq=" + pingSeq;

            AddLog("1/5 打开工程图: " + fileName);
            try
            {
                bool ao;
                _siteApp.OpenFile2(exbPath, false, true, out ao);
            }
            catch (Exception ex)
            {
                // COM 对 exb 的返回不可靠，文件实际已打开
                Diagnostics.Log("OpenFile2 返回异常（正常）: " + ex.Message);
            }

            bool ready = WaitUntil("等图纸就绪", 10000, () =>
            {
                // 冷启动时 CRX init 会删掉请求文件，补投 PING（只作就绪探针，不参与判定）
                if (CrxBridge.LogCount(pingAck) == 0 && !File.Exists(CrxBridge.RequestPath))
                    pingAck = "REQ.ack | seq=" + CrxBridge.PostCommand("CMD:PING");
                return OpenEndedSince(openStart) && PopupAutoYes.ModalCount == 0;
            });
            if (!ready)
            {
                if (LogCount(pingAck) == 0)
                    AddLog("2/5 警告：CRX 未就绪（可点「注册CRX」后重开 CAXA 再试）");
                else
                    AddLog("2/5 警告：等图纸就绪超时");
            }
            else AddLog("2/5 工程图已打开并就绪");

            // 3) 短稳定等待：避开打开瞬间的事件突发
            bool settled = WaitUntil("等编辑器短暂稳定", 3000,
                () => PopupAutoYes.ModalCount == 0 && CrxBridge.LogQuiet(200));
            AddLog(settled ? "3/5 编辑器已稳定，准备派发标题栏命令"
                           : "3/5 编辑器仍在忙，直接派发并由 CRX 重试");

            // 4) 派发 CX_DFT_TITLEBLOCK_3DSET（「设置3D信息」对话框由 Dlg3DInfo 自动填写）
            int tbResultBase = LogCount("REQ.tb-result");
            int tbGiveupBase = LogCount("REQ.tb-giveup");
            int tbDoneBase = LogCount("REQ.tb-done");
            int appliedBase = Dlg3DInfo.AppliedCount;
            CrxBridge.PostCommand("CMD:TITLEBLOCK_3DSET");
            bool tbReached = WaitUntil("等标题栏 3D 信息写入结果", 10000,
                () => LogCount("REQ.tb-result") > tbResultBase
                   || LogCount("REQ.tb-giveup") > tbGiveupBase
                   || (Dlg3DInfo.AppliedCount > appliedBase && LogCount("REQ.tb-done") > tbDoneBase),
                150);
            bool tbOk = LogCount("REQ.tb-result") > tbResultBase;
            bool dialogApplied = Dlg3DInfo.AppliedCount > appliedBase;

            // 收尾：无论成败都让 CRX 停止通道轮换，避免把命令派发到已关闭的文档上
            try { CrxBridge.PostCommand("CMD:TB_ABORT"); } catch { }

            if (!tbReached)
                throw new Exception("标题栏 3D 信息设置未在 10 秒内得到结果（CRX 侧未确认标题栏信息变化），本次不保存、不关图。");
            if (!tbOk)
            {
                if (dialogApplied)
                    AddLog("4/5 设置3D信息 对话框已按默认配置填写并确认；标题栏信息无变化（原本就是默认配置）");
                else
                    throw new Exception("标题栏 3D 信息没有设置成功：「设置3D信息」对话框的配置/自定义设置未能自动填完，"
                        + "或确认后标题栏信息没有变化。本次不保存、不关图。");
            }
            else AddLog("4/5 标题栏 3D 信息已按默认配置写入（已由标题栏信息差异确认）");

            if (PopupAutoYes.ModalCount > 0)
                WaitUntil("等确认对话框关闭", 3000, () => PopupAutoYes.ModalCount == 0);

            // 5) 保存并关闭工程图
            int doneBase = LogCount("close-done");
            int viewBase = LogCount("commandEnded | CX_DFT_ViewClose");
            CrxBridge.PostCommand("CMD:CLOSEDOC|" + fileName);
            bool closed = WaitUntil("保存并关闭工程图", 10000,
                () => LogCount("close-done") > doneBase
                   || LogCount("commandEnded | CX_DFT_ViewClose") > viewBase);
            if (!closed)
            {
                CrxBridge.PostCommand("CMD:CLOSE");
                closed = WaitUntil("关闭工程图兜底", 5000,
                    () => LogCount("close-done") > doneBase
                       || LogCount("commandEnded | CX_DFT_ViewClose") > viewBase);
            }
            if (!closed) throw new Exception("工程图保存/关闭未确认完成，请人工检查该图纸状态。");
            AddLog("5/5 工程图已保存并关闭");

            WaitUntil("等回到设计环境", 10000, () => CaxaAutomation.IsActiveSceneDocument(_app));
        }

        /// <summary>焦点切回 CAXA 3D 设计环境（本进程主窗口）。</summary>
        private static void RestoreDesignFocus()
        {
            try
            {
                IntPtr h = Process.GetCurrentProcess().MainWindowHandle;
                // SW_RESTORE 会把最大化窗口强制还原成普通窗口，仅在最小化时才还原
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
            }
            catch (Exception ex) { Diagnostics.Trace("RestoreDesignFocus: " + ex.Message); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        private const int SW_RESTORE = 9;

        // ================================================================ 等待原语

        /// <summary>条件轮询（100ms），期间泵消息，避免阻塞 CAXA 主线程；命中「停止」即抛异常中断整条链。</summary>
        private bool WaitUntil(string what, int timeoutMs, Func<bool> condition, int pollMs = 100)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { Application.DoEvents(); } catch { }
                if (_stopRequested) throw new OperationStoppedException();
                try { if (condition()) return true; } catch { }
                Thread.Sleep(pollMs);
            }
            Diagnostics.Log(what + ": 超时 " + timeoutMs + "ms");
            return false;
        }

        private static int LogCount(string marker)
        {
            try
            {
                if (!File.Exists(CrxBridge.LogPath)) return 0;
                string text;
                using (var fs = new FileStream(CrxBridge.LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();
                int n = 0, i = 0;
                while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
                { n++; i += marker.Length; }
                return n;
            }
            catch { return 0; }
        }

        private static bool OpenEndedSince(DateTime openStart)
        {
            const string marker = "commandEnded | CX_open_Impl";
            try
            {
                if (!File.Exists(CrxBridge.LogPath)) return false;
                string text;
                using (var fs = new FileStream(CrxBridge.LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();

                TimeSpan startT = openStart.TimeOfDay;
                int i = 0;
                while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
                {
                    int lineStart = text.LastIndexOf('\n', i) + 1;
                    if (i - lineStart >= 8 &&
                        TimeSpan.TryParseExact(text.Substring(lineStart, 8), @"hh\:mm\:ss",
                            System.Globalization.CultureInfo.InvariantCulture, out var ts)
                        && ts >= startT - TimeSpan.FromSeconds(1))
                        return true;
                    i += marker.Length;
                }
                return false;
            }
            catch { return false; }
        }

        // ================================================================ 资源/日志/CRX

        /// <summary>从输出目录逐级向上查找项目文件（不写死绝对路径）。</summary>
        private static string FindResource(string fileName)
        {
            var dirs = new List<string>();
            try { dirs.Add(AppDomain.CurrentDomain.BaseDirectory); } catch { }
            try { dirs.Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)); } catch { }
            foreach (string d in dirs.ToArray())
            {
                string cur = d;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
                {
                    string p = Path.Combine(cur, fileName);
                    if (File.Exists(p)) return p;
                    cur = Path.GetDirectoryName(cur);
                }
            }
            return null;
        }

        private void OpenResource(string fileName)
        {
            string path = FindResource(fileName);
            if (path == null)
            {
                MessageBox.Show("未找到文件：" + fileName, AppTitleBase);
                return;
            }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message, AppTitleBase); }
        }

        private void RunCrxScript(bool register)
        {
            string script = FindResource(register ? "register-crx.ps1" : "unregister-crx.ps1");
            if (script == null)
            {
                MessageBox.Show("未找到 CRX " + (register ? "注册" : "卸载") + "脚本（register-crx.ps1 / unregister-crx.ps1）。",
                    AppTitleBase);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','" + script + "'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                AddLog("已请求" + (register ? "注册" : "卸载") + " CRX（如弹出 UAC 请允许）");
            }
            catch (Exception ex)
            {
                MessageBox.Show("执行脚失败：" + ex.Message, AppTitleBase);
            }
        }

        private void AddLog(string message)
        {
            // 窗口已关闭 / 控件已释放：置停止标志让流程尽快退出，并且不再往已释放控件写字。
            // 这里不抛异常：AddLog 常被 catch/finally 调用，抛出去会越过 catch 逃到线程顶层，
            // 在 net48 下等于未捕获异常（可能崩 CAXA）。流程会在 WaitUntil/停止检查点
            // 通过 `if (_stopRequested) throw new OperationStoppedException()` 正常中止。
            if (IsDisposed || _txtLog == null || _txtLog.IsDisposed)
            {
                _stopRequested = true;
                Diagnostics.Trace("AddLog: 窗口已释放，请求停止；丢弃消息=" + message);
                return;
            }
            if (_txtLog.InvokeRequired)
            {
                try { _txtLog.Invoke(new Action<string>(AddLog), message); } catch { }
                return;
            }
            _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
            _txtLog.ScrollToCaret();
        }

        private void BtnSaveLog_Click(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog())
            {
                sfd.FileName = AppTitleBase + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                sfd.Filter = "文本文件 (*.txt)|*.txt";
                if (sfd.ShowDialog() != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(sfd.FileName, _txtLog.Text, Encoding.UTF8);
                    AddLog("日志已保存: " + sfd.FileName);
                }
                catch (Exception ex) { MessageBox.Show("保存日志失败：" + ex.Message, AppTitleBase); }
            }
        }
    }

    /// <summary>用户点「停止」后由 WaitUntil 抛出，用于中断整条长流程。</summary>
    internal sealed class OperationStoppedException : Exception
    {
        public OperationStoppedException() : base("用户已停止") { }
    }
}
