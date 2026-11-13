// 基体模型建立窗口（原「立方体」窗口，用户 15:09 改版；15:41 增加圆管行；16:05 裁定删除除料路线「圆管」按钮，只留「抽壳圆管」）
// 独立窗口：由主窗口「基体模型建立」按钮弹出；三行图素建模：
//   长方体 = CreateBlock(19)（底面中心与原点重合）；圆柱体 = CreateCylinder(20)；
//   抽壳圆管 = 外圆柱 CreateCylinder(20) + GetBody/GetFaceIds 找上下底 + CreateShellFeature(12) 抽壁厚
//     （默认壁厚=外径×0.25 随外径联动；校验 0<壁厚<外径/2 防报错）。
//     除料路线（CreateTubeAtOrigin，内圆柱 OperationType=Z_SUBTRACT）保留在 CaxaAutomation 作为备用库函数，
//     UI 已于 16:05 按用户裁定移除入口按钮。
// 共用链：Update → IZElement.set_Name 首特征命名 → IZSizeBox.SetDisplayHandle 四勾选 →
//   Reset 重置包围盒 → GetPartType 回读。（拉伸方式 TryCreateCubePart 已删除，实证记录在已否决路线表）
// 日志已移除（用户 15:09 裁定）：日志统一进追踪工具主窗日志区，本窗口只留状态栏＋结果行。
// 铁律：
//  - 按钮文字纯汉字，无 emoji / 图标
//  - 构造期不连 COM、不探测（会话推迟到点击）
//  - 键盘 Ctrl+C/V/X/A/Z 走 ChordRescue（WH_GETMESSAGE），见 ChordRescue.cs 头部说明
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal sealed class CubeForm : Form
    {
        private readonly IZBaseApp _siteApp;
        private CaxaSession _session;
        /// <summary>长流程防重入：建立基体与智能标注互斥。</summary>
        private bool _busy;

        /// <summary>上次输入尺寸（mm）的保存路径，打开时回填。</summary>
        private static readonly string DimsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "caxa3dButton", "last-dims.txt");
        private static readonly string CylDimsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "caxa3dButton", "last-cyl.txt");
        private static readonly string TubeDimsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "caxa3dButton", "last-tube.txt");

        /// <summary>壁厚默认系数（用户 15:41 需求：默认壁厚=外径×0.25）。</summary>
        private const double WallRatio = 0.25;

        // ---- 第一行：长方体（长/宽/高）----
        private readonly Label _lblLength = new Label();
        private readonly Label _lblWidth = new Label();
        private readonly Label _lblHeight = new Label();
        private readonly TextBox _txtLength = new TextBox();
        private readonly TextBox _txtWidth = new TextBox();
        private readonly TextBox _txtHeight = new TextBox();
        private readonly Button _btnBox = new Button();

        // ---- 第二行：圆柱体（直径/高）----
        private readonly Label _lblDia = new Label();
        private readonly Label _lblCylHeight = new Label();
        private readonly TextBox _txtDia = new TextBox();
        private readonly TextBox _txtCylHeight = new TextBox();
        private readonly Button _btnCylinder = new Button();

        // ---- 第三行：圆管（外径/壁厚/高）----
        private readonly Label _lblTubeDia = new Label();
        private readonly Label _lblTubeWall = new Label();
        private readonly Label _lblTubeHeight = new Label();
        private readonly TextBox _txtTubeDia = new TextBox();
        private readonly TextBox _txtTubeWall = new TextBox();
        private readonly TextBox _txtTubeHeight = new TextBox();
        /// <summary>抽壳路线圆管按钮（用户 16:05 裁定：删除除料路线「圆管」按钮，只保留本按钮）。</summary>
        private readonly Button _btnTubeShell = new Button();
        /// <summary>壁厚是否被用户手动改过：没改过就随外径联动（默认=外径×0.25）。</summary>
        private bool _wallEdited;
        /// <summary>联动回填壁厚时防 TextChanged 递归。</summary>
        private bool _autoFillingWall;

        private readonly Button _btnAnnotate = new Button();
        private readonly Label _lblStatus = new Label();
        private readonly Label _lblResult = new Label();

        internal CubeForm(IZBaseApp siteApp, bool topMost)
        {
            _siteApp = siteApp;
            BuildUi();
            TopMost = topMost;
            if (!IsHandleCreated) CreateHandle();
        }

        // ↓ 键盘归属抢救（规则第 37 节；开关见 ChordRescue.cs 头部）
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ChordRescue.Install(this, msg => Diagnostics.Log(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑

        private void BuildUi()
        {
            Text = "基体模型建立";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(748, 224);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            // ---- 第一行：长方体 ----
            const int y0 = 12;
            _lblLength.SetBounds(12, y0 + 4, 52, 20);
            _lblLength.Text = "长(mm)";
            _txtLength.SetBounds(64, y0, 90, 24);
            _txtLength.Text = CaxaAutomation.DefaultSizeMm.ToString(CultureInfo.InvariantCulture);

            _lblWidth.SetBounds(166, y0 + 4, 52, 20);
            _lblWidth.Text = "宽(mm)";
            _txtWidth.SetBounds(218, y0, 90, 24);
            _txtWidth.Text = CaxaAutomation.DefaultSizeMm.ToString(CultureInfo.InvariantCulture);

            _lblHeight.SetBounds(320, y0 + 4, 52, 20);
            _lblHeight.Text = "高(mm)";
            _txtHeight.SetBounds(372, y0, 90, 24);
            _txtHeight.Text = CaxaAutomation.DefaultSizeMm.ToString(CultureInfo.InvariantCulture);

            _btnBox.SetBounds(486, y0 - 2, 118, 30);
            _btnBox.Text = "长方体";
            _btnBox.Click += BtnBox_Click;

            // ---- 第二行：圆柱体 ----
            const int y1 = 48;
            _lblDia.SetBounds(12, y1 + 4, 52, 20);
            _lblDia.Text = "直径(mm)";
            _txtDia.SetBounds(64, y1, 90, 24);
            _txtDia.Text = CaxaAutomation.DefaultSizeMm.ToString(CultureInfo.InvariantCulture);

            _lblCylHeight.SetBounds(166, y1 + 4, 52, 20);
            _lblCylHeight.Text = "高(mm)";
            _txtCylHeight.SetBounds(218, y1, 90, 24);
            _txtCylHeight.Text = CaxaAutomation.DefaultSizeMm.ToString(CultureInfo.InvariantCulture);

            _btnCylinder.SetBounds(486, y1 - 2, 118, 30);
            _btnCylinder.Text = "圆柱体";
            _btnCylinder.Click += BtnCylinder_Click;

            // ---- 第三行：圆管（外径/壁厚/高；默认壁厚=外径×0.25，随外径联动）----
            const int y2 = 84;
            _lblTubeDia.SetBounds(12, y2 + 4, 52, 20);
            _lblTubeDia.Text = "外径(mm)";
            _txtTubeDia.SetBounds(64, y2, 90, 24);
            _txtTubeDia.Text = CaxaAutomation.DefaultSizeMm.ToString(CultureInfo.InvariantCulture);

            _lblTubeWall.SetBounds(166, y2 + 4, 52, 20);
            _lblTubeWall.Text = "壁厚(mm)";
            _txtTubeWall.SetBounds(218, y2, 90, 24);
            _txtTubeWall.Text = (CaxaAutomation.DefaultSizeMm * WallRatio).ToString(CultureInfo.InvariantCulture);

            _lblTubeHeight.SetBounds(320, y2 + 4, 52, 20);
            _lblTubeHeight.Text = "高(mm)";
            _txtTubeHeight.SetBounds(372, y2, 90, 24);
            _txtTubeHeight.Text = CaxaAutomation.DefaultSizeMm.ToString(CultureInfo.InvariantCulture);

            _btnTubeShell.SetBounds(486, y2 - 2, 126, 30);
            _btnTubeShell.Text = "抽壳圆管";
            _btnTubeShell.Click += BtnTubeShell_Click;

            _txtTubeDia.TextChanged += TxtTubeDia_TextChanged;
            _txtTubeWall.TextChanged += TxtTubeWall_TextChanged;

            _lblStatus.SetBounds(12, y2 + 36, 596, 20);
            _lblStatus.ForeColor = Color.DimGray;
            _lblStatus.Text = "未连接（点击按钮时建立会话）";

            _lblResult.SetBounds(12, y2 + 58, 596, 20);
            _lblResult.Text = string.Empty;

            _btnAnnotate.SetBounds(12, y2 + 84, 100, 28);
            _btnAnnotate.Text = "智能标注";
            _btnAnnotate.Click += BtnAnnotate_Click;

            Controls.AddRange(new Control[] {
                _lblLength, _txtLength, _lblWidth, _txtWidth, _lblHeight, _txtHeight, _btnBox,
                _lblDia, _txtDia, _lblCylHeight, _txtCylHeight, _btnCylinder,
                _lblTubeDia, _txtTubeDia, _lblTubeWall, _txtTubeWall, _lblTubeHeight, _txtTubeHeight,
                _btnTubeShell,
                _lblStatus, _lblResult, _btnAnnotate });
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LoadDims();
            LoadCylDims();
            LoadTubeDims();
            Diagnostics.Log("primitive: 基体模型建立窗口已打开");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveDims();
            SaveCylDims();
            SaveTubeDims();
            DropSession();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ChordRescue.Uninstall(this, "窗口关闭");
            base.OnFormClosed(e);
        }

        // ---------------------------------------------------------------- 尺寸输入

        private static bool TryParseMm(string text, out double mm)
        {
            // 同时接受当前区域与不变区域的小数点写法
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out mm) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out mm))
                return false;
            return mm > 0 && mm <= 100000;
        }

        private void LoadDims()
        {
            try
            {
                if (!File.Exists(DimsFile)) return;
                string[] parts = File.ReadAllText(DimsFile).Trim().Split(',');
                if (parts.Length != 3) return;
                double lx, ly, lz;
                if (TryParseMm(parts[0], out lx) && TryParseMm(parts[1], out ly) && TryParseMm(parts[2], out lz))
                {
                    _txtLength.Text = parts[0].Trim();
                    _txtWidth.Text = parts[1].Trim();
                    _txtHeight.Text = parts[2].Trim();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("LoadDims failed: " + ex.Message);
            }
        }

        private void SaveDims()
        {
            try
            {
                double lx, ly, lz;
                if (!TryParseMm(_txtLength.Text, out lx) || !TryParseMm(_txtWidth.Text, out ly) ||
                    !TryParseMm(_txtHeight.Text, out lz)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(DimsFile));
                File.WriteAllText(DimsFile, string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", lx, ly, lz));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SaveDims failed: " + ex.Message);
            }
        }

        private void LoadCylDims()
        {
            try
            {
                if (!File.Exists(CylDimsFile)) return;
                string[] parts = File.ReadAllText(CylDimsFile).Trim().Split(',');
                if (parts.Length != 2) return;
                double dia, h;
                if (TryParseMm(parts[0], out dia) && TryParseMm(parts[1], out h))
                {
                    _txtDia.Text = parts[0].Trim();
                    _txtCylHeight.Text = parts[1].Trim();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("LoadCylDims failed: " + ex.Message);
            }
        }

        private void SaveCylDims()
        {
            try
            {
                double dia, h;
                if (!TryParseMm(_txtDia.Text, out dia) || !TryParseMm(_txtCylHeight.Text, out h)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(CylDimsFile));
                File.WriteAllText(CylDimsFile, string.Format(CultureInfo.InvariantCulture, "{0},{1}", dia, h));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SaveCylDims failed: " + ex.Message);
            }
        }

        private void LoadTubeDims()
        {
            try
            {
                if (!File.Exists(TubeDimsFile)) return;
                string[] parts = File.ReadAllText(TubeDimsFile).Trim().Split(',');
                if (parts.Length != 3) return;
                double dia, wall, h;
                if (TryParseMm(parts[0], out dia) && TryParseMm(parts[1], out wall) && TryParseMm(parts[2], out h))
                {
                    _txtTubeDia.Text = parts[0].Trim();
                    _txtTubeWall.Text = parts[1].Trim();
                    _txtTubeHeight.Text = parts[2].Trim();
                    _wallEdited = true;   // 回填的记忆壁厚视为用户值，不再联动覆盖
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("LoadTubeDims failed: " + ex.Message);
            }
        }

        private void SaveTubeDims()
        {
            try
            {
                double dia, wall, h;
                if (!TryParseMm(_txtTubeDia.Text, out dia) || !TryParseMm(_txtTubeWall.Text, out wall) ||
                    !TryParseMm(_txtTubeHeight.Text, out h)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(TubeDimsFile));
                File.WriteAllText(TubeDimsFile, string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", dia, wall, h));
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SaveTubeDims failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 圆管壁厚联动（默认=外径×0.25）

        /// <summary>外径变化时，壁厚未被用户手改过就联动重填为 外径×0.25（防报错：不产生非法壁厚）。</summary>
        private void TxtTubeDia_TextChanged(object sender, EventArgs e)
        {
            if (_wallEdited || _autoFillingWall) return;
            double dia;
            if (!TryParseMm(_txtTubeDia.Text, out dia)) return;
            _autoFillingWall = true;
            try { _txtTubeWall.Text = (dia * WallRatio).ToString(CultureInfo.InvariantCulture); }
            finally { _autoFillingWall = false; }
        }

        private void TxtTubeWall_TextChanged(object sender, EventArgs e)
        {
            if (_autoFillingWall) return;
            _wallEdited = true;
        }

        /// <summary>「抽壳圆管」按钮（用户 16:05 裁定保留的唯一圆管路线）：抽壳方式（上下底开口抽壁厚）。</summary>
        private void BtnTubeShell_Click(object sender, EventArgs e)
        {
            double dia, wall, h;
            if (!TryReadTubeDims(out dia, out wall, out h)) return;
            Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                "dims[抽壳圆管]: 外径={0} 壁厚={1} 高={2} mm", dia, wall, h));
            RunCreate("抽壳圆管", "正在建立抽壳圆管（外圆柱＋抽壳）…",
                delegate { return CaxaAutomation.CreateTubeShellAtOrigin(_session, dia, wall, h); },
                string.Format(CultureInfo.InvariantCulture,
                    "已建立 外径{0} × 壁厚{1} × 高{2} mm（抽壳路线，底面中心与原点重合）", dia, wall, h));
        }

        /// <summary>「抽壳圆管」按钮的输入读取与防报错校验（0 &lt; 壁厚 &lt; 外径/2）。</summary>
        private bool TryReadTubeDims(out double dia, out double wall, out double h)
        {
            dia = wall = h = 0;
            if (!TryParseMm(_txtTubeDia.Text, out dia) || !TryParseMm(_txtTubeWall.Text, out wall) ||
                !TryParseMm(_txtTubeHeight.Text, out h))
            {
                ShowError("请输入有效的外径、壁厚、高（大于 0 的数字，单位 mm）。");
                return false;
            }
            if (wall >= dia / 2.0)
            {
                ShowError("壁厚必须小于外径的一半（否则内孔消失，无法成管）。" +
                    Environment.NewLine + string.Format(CultureInfo.InvariantCulture,
                        "当前：外径 {0} mm ⇒ 壁厚需 < {1} mm。", dia, dia / 2.0));
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 会话

        /// <summary>确保进程内会话可用；Ping 失败时丢弃旧会话并重建。</summary>
        private void EnsureConnected()
        {
            if (_session != null && CaxaAutomation.Ping(_session))
            {
                SetConnectedStatus();
                return;
            }
            DropSession();
            Diagnostics.Log("EnsureConnected: building in-process session");

            _session = CaxaAutomation.ConnectFromSite(_siteApp);
            SetConnectedStatus();
            Diagnostics.Log("EnsureConnected: done");
        }

        private void SetConnectedStatus()
        {
            _lblStatus.ForeColor = Color.Black;
            _lblStatus.Text = "已连接（进程内）";
        }

        private void DropSession()
        {
            if (_session == null) return;
            try { _session.Dispose(); } catch { }
            _session = null;
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ---------------------------------------------------------------- 建立基体（图素路线共用壳）

        /// <summary>
        /// 两条图素路线共用执行壳（长方体/圆柱体），失败两轮会话重建兜底。
        /// 结果只进状态栏与结果行；详细过程日志统一由 Diagnostics 落主窗日志区＋trace。
        /// </summary>
        private void RunCreate(string shapeName, string busyText, Func<string> build, string resultText)
        {
            if (_busy) return;
            Diagnostics.Log("btnCreate.Click shape=" + shapeName);
            _busy = true;
            _btnBox.Enabled = false;
            _btnCylinder.Enabled = false;
            _btnTubeShell.Enabled = false;
            _btnAnnotate.Enabled = false;

            try
            {
                // 最多两轮：第一轮失败且服务器状态异常时，重建会话后重试一次
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try
                    {
                        EnsureConnected();
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Log("Connect failed: " + ex);
                        _lblStatus.ForeColor = Color.Firebrick;
                        _lblStatus.Text = "CAXA 3D 会话建立失败（重新打开窗口重试）";
                        ShowError("建立 CAXA 3D 会话失败：" + Environment.NewLine + ex.Message);
                        return;
                    }

                    _lblStatus.ForeColor = Color.Black;
                    _lblStatus.Text = attempt > 1 ? "会话已重建，" + busyText : busyText;
                    _lblResult.Text = string.Empty;
                    try
                    {
                        string verdict = build();
                        SetConnectedStatus();
                        _lblResult.ForeColor = Color.Green;
                        _lblResult.Text = resultText + "｜" + verdict;
                        return;
                    }
                    catch (Exception ex)
                    {
                        bool serverDead = !CaxaAutomation.Ping(_session);
                        Diagnostics.Log(string.Format(
                            "Create attempt {0} failed (serverDead={1}): {2}", attempt, serverDead, ex));
                        if (attempt == 1 && serverDead)
                        {
                            DropSession();
                            _lblStatus.Text = "检测到服务器状态异常，正在重建会话 …";
                            continue;
                        }
                        if (_session != null) SetConnectedStatus();
                        else
                        {
                            _lblStatus.ForeColor = Color.Firebrick;
                            _lblStatus.Text = "CAXA 3D 会话失效（重新打开窗口重试）";
                        }
                        _lblResult.ForeColor = Color.Firebrick;
                        _lblResult.Text = shapeName + "建立失败";
                        ShowError("建立" + shapeName + "失败：" + Environment.NewLine + ex.Message);
                        return;
                    }
                }
            }
            finally
            {
                _busy = false;
                if (!IsDisposed)
                {
                    _btnBox.Enabled = true;
                    _btnCylinder.Enabled = true;
                    _btnTubeShell.Enabled = true;
                    _btnAnnotate.Enabled = true;
                }
            }
        }

        /// <summary>第一行「长方体」：按用户输入的长×宽×高（mm）在原点建立，首特征命名「长方体」。</summary>
        private void BtnBox_Click(object sender, EventArgs e)
        {
            double lx, ly, lz;
            if (!TryParseMm(_txtLength.Text, out lx) || !TryParseMm(_txtWidth.Text, out ly) ||
                !TryParseMm(_txtHeight.Text, out lz))
            {
                ShowError("请输入有效的长、宽、高（大于 0 的数字，单位 mm）。");
                return;
            }
            Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                "dims[长方体]: L(X)={0} W(Y)={1} H(Z)={2} mm", lx, ly, lz));
            RunCreate("长方体", "正在建立长方体（图素）…",
                delegate { return CaxaAutomation.CreateBoxAtOrigin(_session, lx, ly, lz); },
                string.Format(CultureInfo.InvariantCulture, "已建立 {0} × {1} × {2} mm（底面中心与原点重合）", lx, ly, lz));
        }

        /// <summary>第二行「圆柱体」：按用户输入的直径×高（mm）在原点建立，首特征命名「圆柱体」（参考长方体功能）。</summary>
        private void BtnCylinder_Click(object sender, EventArgs e)
        {
            double dia, h;
            if (!TryParseMm(_txtDia.Text, out dia) || !TryParseMm(_txtCylHeight.Text, out h))
            {
                ShowError("请输入有效的直径、高（大于 0 的数字，单位 mm）。");
                return;
            }
            Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                "dims[圆柱体]: 直径={0} 高={1} mm", dia, h));
            RunCreate("圆柱体", "正在建立圆柱体（图素）…",
                delegate { return CaxaAutomation.CreateCylinderAtOrigin(_session, dia, h); },
                string.Format(CultureInfo.InvariantCulture, "已建立 直径{0} × 高{1} mm（底面中心与原点重合）", dia, h));
        }

        // ---------------------------------------------------------------- 智能标注

        /// <summary>
        /// 草图智能标注（Phase 1）：TLB 无「创建尺寸」写接口 ⇒ 打开显示开关并回读 + 枚举草图尺寸关系。
        /// 收尾只走状态栏／结果行，不弹模态框（模态会禁用 CAXA 主界面）。
        /// </summary>
        private void BtnAnnotate_Click(object sender, EventArgs e)
        {
            Diagnostics.Log("btnAnnotate.Click");
            if (_busy) return;

            try
            {
                EnsureConnected();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Connect failed: " + ex);
                _lblStatus.ForeColor = Color.Firebrick;
                _lblStatus.Text = "CAXA 3D 会话建立失败（重新打开窗口重试）";
                return;
            }

            _busy = true;
            _btnAnnotate.Enabled = false;
            _btnBox.Enabled = false;
            _btnCylinder.Enabled = false;
            _btnTubeShell.Enabled = false;
            try
            {
                _lblStatus.ForeColor = Color.Black;
                _lblStatus.Text = "正在执行智能标注（显示 + 读取）…";
                _lblResult.Text = string.Empty;
                string verdict = CaxaAutomation.AnnotateLastSketch(_session);
                SetConnectedStatus();
                _lblResult.ForeColor = Color.Green;
                _lblResult.Text = verdict;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Annotate failed: " + ex);
                _lblResult.ForeColor = Color.Firebrick;
                _lblResult.Text = "智能标注失败";
            }
            finally
            {
                _busy = false;
                if (!IsDisposed)
                {
                    _btnAnnotate.Enabled = true;
                    _btnBox.Enabled = true;
                    _btnCylinder.Enabled = true;
                    _btnTubeShell.Enabled = true;
                }
            }
        }
    }
}
