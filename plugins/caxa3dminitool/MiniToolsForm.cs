using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>
    /// 小工具合集主窗口：
    ///  - 顶部「窗口前置」是 TopMost 开关（默认锁定在最上层）；
    ///  - 增料/除料切换：选中特征后一键在增料/除料之间切换，并重建所属零件；
    ///  - 实时显示「当前设计环境.ics---零部件名（虚拟 / 外部链接）」；
    ///  - 日志区 + 保存/清理日志。
    /// 所有成功判定只看业务侧回读（OperationType 是否真的变成目标值）。
    /// </summary>
    [ComVisible(false)]
    internal sealed class MiniToolsForm : Form
    {
        private readonly IZBaseApp _app;

        private bool _topMost = true;
        private Button _btnTopMost;
        private Button _btnToggle;
        private Label _lblSelection;
        private Timer _selTimer;
        private const string BaseTitle = "CAXA3D小工具合集";

        // ---- 内核切换 ----
        private Button _btnKernelPara;
        private Button _btnKernelAcis;
        private Timer _kernelTimer;

        // ---- 元素工具（压缩 / 包围盒 / 内部链接） ----
        private Button _btnFindSuppressed;
        private Button _btnResetBox;
        private Button _btnSelectLinked;

        private Button _btnImportWire;

        private TextBox _txtLog;
        private List<IZElement> _kernelQueue;
        private List<IZElement> _kernelChanged;
        private int _kernelIndex;
        private int _kernelTotal;
        private int _kernelOk, _kernelAlready, _kernelSkipped, _kernelFailed;
        private eZKernelType _kernelTarget;
        private bool _kernelStopRequested;
        private bool _kernelRunning;

        public MiniToolsForm(IZBaseApp app)
        {
            _app = app;
            BuildUi();

            TopMost = _topMost;
            ApplyTopMostVisual();

            // 日志区（参照 caxa3dsetMaterial：底部只读 TextBox + UiLogSink）
            Diagnostics.UiLogSink = AppendLog;
            Diagnostics.Log("小工具合集窗口已打开。日志文件：" + Diagnostics.LogPath);

            // 在 Add-in 加载阶段预创建句柄，避免 OnClick 时跨窗口消息互等
            if (!IsHandleCreated) CreateHandle();
        }

        // ================================================================ 界面

        private void BuildUi()
        {
            Text = BaseTitle + "-" + BuildStamp();
            ClientSize = new Size(900, 458);
            MinimumSize = new Size(860, 440);
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);

            // ---- 顶部：窗口前置 + 当前选中 ----
            Panel top = new Panel { Dock = DockStyle.Top, Height = 70, Padding = new Padding(10, 8, 10, 4) };

            _btnTopMost = MakeButton("窗口前置", 10, 12, 100, 30);
            _btnTopMost.Click += (s, e) => ToggleTopMost();
            top.Controls.Add(_btnTopMost);

            _lblSelection = new Label
            {
                Text = "当前选中：读取中…",
                Left = 120, Top = 8, Width = 770, Height = 44,
                AutoSize = false,
                AutoEllipsis = true,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 90, 170)
            };
            top.Controls.Add(_lblSelection);

            // ---- 按钮行：全部平铺，不再分小控件 ----
            Panel tool = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(10, 6, 10, 6) };

            _btnToggle = MakeButton("增料除料切换", 10, 8, 168, 40);
            _btnToggle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _btnToggle.Click += (s, e) => ToggleSelectedFeature();
            tool.Controls.Add(_btnToggle);

            _btnKernelPara = MakeButton("切换Parasolid内核", 186, 8, 168, 40);
            _btnKernelPara.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _btnKernelPara.Click += (s, e) => StartKernelSwitch(eZKernelType.Z_KERNEL_PARASOLID);
            tool.Controls.Add(_btnKernelPara);

            _btnKernelAcis = MakeButton("切换ACIS内核", 362, 8, 168, 40);
            _btnKernelAcis.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _btnKernelAcis.Click += (s, e) => StartKernelSwitch(eZKernelType.Z_KERNEL_ACIS);
            tool.Controls.Add(_btnKernelAcis);

            Panel tool2 = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(10, 6, 10, 6) };

            _btnFindSuppressed = MakeButton("选中取消压缩", 10, 8, 168, 40);
            _btnFindSuppressed.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _btnFindSuppressed.Click += (s, e) => UnsuppressSelectedElements();
            tool2.Controls.Add(_btnFindSuppressed);

            _btnResetBox = MakeButton("更新包围盒", 186, 8, 168, 40);
            _btnResetBox.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _btnResetBox.Click += (s, e) => ResetSizeBoxes();
            tool2.Controls.Add(_btnResetBox);

            _btnSelectLinked = MakeButton("全选内部链接零件", 362, 8, 180, 40);
            _btnSelectLinked.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _btnSelectLinked.Click += (s, e) => SelectInternalLinkedParts();
            tool2.Controls.Add(_btnSelectLinked);

            Panel tool3 = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(10, 6, 10, 6) };

            _btnImportWire = MakeButton("导入3D曲线坐标数据", 10, 8, 192, 40);
            _btnImportWire.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            _btnImportWire.Click += (s, e) => ImportWireFromTxt();
            tool3.Controls.Add(_btnImportWire);

            Controls.Add(tool3);
            Controls.Add(tool2);
            Controls.Add(tool);
            Controls.Add(top);

            // ---- 日志区（Dock Bottom，剩余空间全给日志） ----
            Panel logHost = new Panel { Dock = DockStyle.Bottom, Height = 180, Padding = new Padding(10, 0, 10, 10) };
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
            Controls.Add(logHost);

            _selTimer = new Timer { Interval = 2000 };
            _selTimer.Tick += (s, e) => RefreshSelectionLabel();

            // 内核切换：定时器分片处理，保证界面可响应、停止按钮可用
            _kernelTimer = new Timer { Interval = 60 };
            _kernelTimer.Tick += (s, e) => OnKernelTimerTick();
        }

        private Button MakeButton(string text, int left, int top, int width, int height)
        {
            return new Button { Text = text, Left = left, Top = top, Width = width, Height = height };
        }

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

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshSelectionLabel();
            if (_selTimer != null) _selTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 关窗即置停止标志：不强行保存文件、不强关图纸
            _kernelStopRequested = true;
            _kernelRunning = false;
            if (_kernelTimer != null) { _kernelTimer.Stop(); _kernelTimer.Dispose(); _kernelTimer = null; }
            if (_selTimer != null) { _selTimer.Stop(); _selTimer.Dispose(); _selTimer = null; }
            Diagnostics.UiLogSink = null;
            base.OnFormClosed(e);
        }

        /// <summary>导入3D曲线坐标数据：选 txt → 解析每行 x y z（毫米）→ IZWire 生成插值样条。</summary>
        private void ImportWireFromTxt()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            string path = null;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择坐标数据文件（每行 X Y Z，单位毫米）";
                dlg.Filter = "坐标文本 (*.txt;*.dat;*.csv)|*.txt;*.dat;*.csv|所有文件 (*.*)|*.*";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }

            try
            {
                string error, detail;
                int pointCount, crvId;
                CurveTool.ImportWireFromText(_app, path, out error, out pointCount, out crvId, out detail);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[导入3D曲线] " + error);
                    return;
                }

                Diagnostics.Log("导入3D曲线成功：" + detail);
                RefreshSelectionLabel();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("导入3D曲线异常：" + ex);
            }
        }

        // ================================================================ 日志

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

        // ================================================================ 选中状态

        private void RefreshSelectionLabel()
        {
            if (IsDisposed) return;
            try
            {
                _lblSelection.Text = "当前选中：" + FeatureTool.DescribeSelection(_app);
            }
            catch (Exception ex)
            {
                _lblSelection.Text = "当前选中：读取失败 " + ex.Message;
            }
        }

        // ================================================================ 增料 / 除料切换

        private void ToggleSelectedFeature()
        {
            _btnToggle.Enabled = false;
            try
            {
                string error;
                List<FeatureTool.Outcome> outcomes = FeatureTool.ToggleSelected(_app, out error);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[未执行] " + error);
                    return;
                }

                int ok = 0, skipped = 0;
                foreach (FeatureTool.Outcome o in outcomes)
                {
                    if (o.Failed)
                    {
                        skipped++;
                        Diagnostics.Log(string.Format("[跳过] {0}：{1}", o.FeatureName, o.Note));
                        continue;
                    }

                    if (o.Changed)
                    {
                        ok++;
                        Diagnostics.Log(string.Format("[成功] {0} / 特征 {1}：{2}({3}) → {4}({5})，重建后回读一致",
                            string.IsNullOrEmpty(o.PartName) ? "?" : o.PartName, o.FeatureName,
                            FeatureTool.OperationText(o.Before), (int)o.Before,
                            FeatureTool.OperationText(o.After), (int)o.After));
                    }
                    else
                    {
                        skipped++;
                        Diagnostics.Log(string.Format("[未生效] {0} / 特征 {1}：{2} → 期望 {3}，重建后回读 {4}",
                            string.IsNullOrEmpty(o.PartName) ? "?" : o.PartName, o.FeatureName,
                            FeatureTool.OperationText(o.Before),
                            FeatureTool.OperationText(o.Expected),
                            FeatureTool.OperationText(o.After)) +
                            (string.IsNullOrEmpty(o.Note) ? "" : "；" + o.Note));
                    }
                }

                Diagnostics.Log(string.Format("增料除料切换结束：成功 {0} 个，跳过/未生效 {1} 个。", ok, skipped));
                RefreshSelectionLabel();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("增料除料切换异常：" + ex);
            }
            finally
            {
                _btnToggle.Enabled = true;
            }
        }

        // ================================================================ 内核切换（Parasolid / ACIS）

        private void StartKernelSwitch(eZKernelType target)
        {
            if (_kernelRunning) return;

            string error, summary;
            List<IZElement> leaves = KernelTool.GatherLeafParts(_app, out error, out summary);

            if (!string.IsNullOrEmpty(error))
            {
                Diagnostics.Log("[未执行] " + error);
                return;
            }

            Diagnostics.Log(string.Format("开始切换内核 → {0}：{1}",
                KernelTool.KernelText(target), summary));

            _kernelQueue = leaves;
            _kernelChanged = new List<IZElement>();
            _kernelIndex = 0;
            _kernelTotal = leaves.Count;
            _kernelOk = 0; _kernelAlready = 0; _kernelSkipped = 0; _kernelFailed = 0;
            _kernelTarget = target;
            _kernelStopRequested = false;
            _kernelRunning = true;

            _btnKernelPara.Enabled = false;
            _btnKernelAcis.Enabled = false;

            _kernelTimer.Start();
        }

        /// <summary>定时器分片：每 tick 处理一小批，界面保持可响应。</summary>
        private void OnKernelTimerTick()
        {
            if (!_kernelRunning)
            {
                _kernelTimer.Stop();
                return;
            }

            int processed = 0;
            while (_kernelIndex < _kernelTotal && processed < 25 && !_kernelStopRequested)
            {
                IZElement elem = _kernelQueue[_kernelIndex];
                _kernelIndex++;
                processed++;
                ApplyOneKernel(elem);
            }

            if (_kernelStopRequested || _kernelIndex >= _kernelTotal)
                FinishKernelSwitch(_kernelStopRequested);
        }

        private void ApplyOneKernel(IZElement elem)
        {
            eZKernelType before, after;
            string note;
            KernelTool.ApplyStatus st = KernelTool.ApplyKernel(elem, _kernelTarget, out before, out after, out note);
            string name = SafeName(elem);

            switch (st)
            {
                case KernelTool.ApplyStatus.Already:
                    _kernelAlready++;
                    Diagnostics.Log(string.Format("[已是] {0}：本来就是 {1}，跳过",
                        name, KernelTool.KernelText(before)));
                    break;

                case KernelTool.ApplyStatus.Changed:
                    _kernelOk++;
                    _kernelChanged.Add(elem);
                    Diagnostics.Log(string.Format("[成功] {0}：{1} → {2}（写入后回读一致）",
                        name, KernelTool.KernelText(before), KernelTool.KernelText(after)));
                    break;

                case KernelTool.ApplyStatus.NotPart:
                    _kernelSkipped++;
                    Diagnostics.Log(string.Format("[跳过] {0}：{1}", name, note));
                    break;

                default:
                    _kernelFailed++;
                    Diagnostics.Log(string.Format("[失败] {0}：{1} → 期望 {2}，实际 {3}{4}",
                        name, KernelTool.KernelText(before), KernelTool.KernelText(_kernelTarget),
                        KernelTool.KernelText(after),
                        string.IsNullOrEmpty(note) ? "" : "；" + note));
                    break;
            }
        }

        /// <summary>
        /// 收尾：统一触发一次场景重建（官方样例口径：全部写完再 Update），
        /// 然后对已切换零件逐一回读 KernelType 做权威判定。
        /// </summary>
        private void FinishKernelSwitch(bool stopped)
        {
            _kernelRunning = false;
            _kernelTimer.Stop();
            _btnKernelPara.Enabled = true;
            _btnKernelAcis.Enabled = true;

            int confirmed = 0, mismatch = 0;
            try
            {
                if (_kernelChanged.Count > 0)
                {
                    Application.DoEvents();

                    IZSceneDoc scene = FeatureTool.GetSceneDoc(_app);
                    if (scene != null)
                    {
                        try { scene.Update(); }
                        catch (Exception ex) { Diagnostics.Log("场景重建失败：" + ex.Message); }
                    }

                    foreach (IZElement elem in _kernelChanged)
                    {
                        try
                        {
                            IZPart part = elem as IZPart;
                            if (part != null && part.KernelType == _kernelTarget) confirmed++;
                            else mismatch++;
                        }
                        catch { mismatch++; }
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("内核切换收尾异常：" + ex.Message);
            }

            string head = stopped ? "已停止" : "切换完成";
            string body = string.Format("（目标 {0}）已切换 {1}，本就同内核 {2}，跳过 {3}，失败 {4}；重建后回读一致 {5}，不一致 {6}",
                KernelTool.KernelText(_kernelTarget), _kernelOk, _kernelAlready, _kernelSkipped, _kernelFailed,
                confirmed, mismatch);

            Diagnostics.Log(head + body);
            RefreshSelectionLabel();
        }

        private static string SafeName(IZElement elem)
        {
            if (elem == null) return "?";
            try { return string.IsNullOrEmpty(elem.Name) ? "?" : elem.Name; } catch { return "?"; }
        }

        // ================================================================ 元素工具（压缩 / 包围盒 / 内部链接）

        /// <summary>
        /// 选中取消压缩：按切换内核的装配体穿透规则抓零部件（无选中则穿透整棵树），
        /// 逐个解除压缩，回读 Z_SUPPRESSED 判定。
        /// </summary>
        private void UnsuppressSelectedElements()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            try
            {
                string error, summary;
                int total, unsuppressed, already, failed, verified;
                ElementsTool.UnsuppressSelected(_app, out error, out summary,
                    out total, out unsuppressed, out already, out failed, out verified);

                if (!string.IsNullOrEmpty(error))
                {
                    Diagnostics.Log("[选中取消压缩] " + error);
                    return;
                }

                Diagnostics.Log(string.Format(
                    "选中取消压缩：{0}；解除 {1}，本就未压缩 {2}，失败 {3}；回读已解除 {4}",
                    summary, unsuppressed, already, failed, verified));

                RefreshSelectionLabel();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("选中取消压缩异常：" + ex);
            }
        }

        /// <summary>更新包围盒：对选中元素（无选中则全树）QI 到 IZSizeBox 后 Reset。</summary>
        private void ResetSizeBoxes()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            try
            {
                string error;
                int reset, failed, noBox, verified;
                ElementsTool.ResetSizeBoxes(_app, out error, out reset, out failed, out noBox, out verified);
                if (!string.IsNullOrEmpty(error)) Diagnostics.Log("[更新包围盒] " + error);
                RefreshSelectionLabel();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("更新包围盒异常：" + ex);
            }
        }

        /// <summary>全选内部链接零件：有选中则以选中为种子，否则扫全树。</summary>
        private void SelectInternalLinkedParts()
        {
            if (_kernelRunning)
            {
                Diagnostics.Log("[未执行] 内核切换进行中，请稍后再试。");
                return;
            }

            try
            {
                string error;
                int selectedAfter;
                int total = ElementsTool.SelectInternalLinked(_app, out error, out selectedAfter);
                if (!string.IsNullOrEmpty(error)) Diagnostics.Log("[全选内部链接零件] " + error);
                if (total > 0)
                    Diagnostics.Log(string.Format("全选内部链接零件：命中 {0} 个，选区回读 {1} 个", total, selectedAfter));
                RefreshSelectionLabel();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("全选内部链接零件异常：" + ex);
            }
        }
    }
}
