using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dCXDFTViewUpdate
{
    /// <summary>
    /// 批量刷新图纸视图：
    ///   C# Addin（跑在 IronCAD.exe 里，永远只能拿到 3D 场景文档）
    ///   → OpenFile2 触发打开 exb（IronCAD 会让 CAXADraft.exe 加载图纸）
    ///   → 等 CRX 命令桥就绪（CAXADraft 进程内）
    ///   → 投递 CRX 自定义命令 CXDFTRefreshViews
    ///     （CRX 在 CAXADraft 进程里能拿到真正的图纸文档，做 Update+Save+Close）
    ///   → 等 CRX 日志标记命令完成 → 下一个文件
    /// </summary>
    public class MainForm : Form
    {
        private readonly IZBaseApp _app;

        // ---------------------------------------------------------------- UI 控件
        private readonly Button _btnPick;
        private readonly Button _btnStart;
        private readonly Button _btnStop;
        private readonly Button _btnClear;
        private readonly Button _btnSaveLog;
        private readonly Button _btnRegCrx;
        private readonly Button _btnUnregCrx;
        private readonly Label _lblFolder;
        private readonly ListView _list;
        private readonly ColumnHeader _colNo;
        private readonly ColumnHeader _colFile;
        private readonly ColumnHeader _colState;
        private readonly ColumnHeader _colCost;
        private readonly ColumnHeader _colInfo;
        private readonly Label _lblStatus;
        private readonly CheckBox _chkBackground;          // 方案 A：后台化（不抢前台）

        // ---------------------------------------------------------------- 批处理状态
        private volatile bool _running;
        private volatile bool _stopRequested;
        private string _folder;                            // 「选择文件夹」选定的目录（「开始运行」用）

        private const int OpenReadyTimeoutMs = 30000;      // 等「文档窗口出现 + 弹窗点掉」（冷启动首开较慢）
        private const int CloseTimeoutMs = 60000;          // 原生关闭（含「是否保存」弹窗）等 60s
        private const int StampWaitMs = 1500;              // 时间戳轮询上限（通常几十毫秒就变）
        private int savedCount;                            // 本批次里"文件时间戳被刷新"的个数

        public MainForm(IZBaseApp app)
        {
            _app = app;
            // 标题带版本号：取 DLL 本身的编译/部署时间（yyyyMMddHHmm），每次编译部署自动更新
            string ver;
            try
            {
                string asm = System.Reflection.Assembly.GetExecutingAssembly().Location;
                ver = File.GetLastWriteTime(asm).ToString("yyyyMMddHHmm");
            }
            catch { ver = "unknown"; }
            Text = "批量刷新图纸视图-" + ver;
            Width = 900; Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = false;

            var panel = new Panel { Dock = DockStyle.Top, Height = 78 };
            Controls.Add(panel);

            // 「选择文件夹」只做选择 + 列出清单，「开始运行」才真正干活（两步分离）
            _btnPick = new Button { Text = "选择文件夹", Width = 110, Height = 30, Location = new Point(10, 10) };
            _btnStart = new Button { Text = "开始运行", Width = 100, Height = 30, Location = new Point(128, 10), Enabled = false };
            _btnStop = new Button { Text = "停止", Width = 70, Height = 30, Location = new Point(236, 10), Enabled = false };
            _btnClear = new Button { Text = "清空日志", Width = 90, Height = 30, Location = new Point(314, 10) };
            _btnSaveLog = new Button { Text = "保存日志", Width = 90, Height = 30, Location = new Point(412, 10) };
            _btnRegCrx = new Button { Text = "注册CRX", Width = 90, Height = 30, Location = new Point(510, 10) };
            _btnUnregCrx = new Button { Text = "卸载CRX", Width = 90, Height = 30, Location = new Point(608, 10) };

            // 方案 A：后台化（不抢前台）——批处理期间把图纸环境进程（CDRAFT_M_IC.exe）压到后台，
            // 既不让它抢前台，也不让它占屏幕。默认开启；一旦怀疑它引起异常，取消勾选即可当场关掉
            // （不必重新编译），关掉后的行为与改造前完全一致。
            _chkBackground = new CheckBox
            {
                Text = "后台化（不抢前台）", Width = 176, Height = 24,
                Location = new Point(706, 13), Checked = true
            };
            _chkBackground.CheckedChanged += (s, e) => { ForegroundGuard.Enabled = _chkBackground.Checked && _running; };

            _lblFolder = new Label
            {
                Text = "文件夹：未选择",
                AutoSize = false, AutoEllipsis = true,
                Location = new Point(12, 47), Size = new Size(860, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            panel.Controls.AddRange(new Control[] { _btnPick, _btnStart, _btnStop, _btnClear, _btnSaveLog, _btnRegCrx, _btnUnregCrx, _chkBackground, _lblFolder });

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details,
                FullRowSelect = true, GridLines = true
            };
            _colNo = new ColumnHeader { Text = "序号", Width = 50 };
            _colFile = new ColumnHeader { Text = "文件", Width = 380 };
            _colState = new ColumnHeader { Text = "状态", Width = 110 };
            _colCost = new ColumnHeader { Text = "耗时", Width = 80 };
            _colInfo = new ColumnHeader { Text = "信息", Width = 240 };
            _list.Columns.AddRange(new[] { _colNo, _colFile, _colState, _colCost, _colInfo });
            Controls.Add(_list);

            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 26 };
            _lblStatus = new Label { Text = "就绪", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
            statusPanel.Controls.Add(_lblStatus);
            Controls.Add(statusPanel);
            _list.BringToFront();

            _btnPick.Click += BtnPick_Click;
            _btnStart.Click += BtnStart_Click;
            _btnStop.Click += (s, e) => { _stopRequested = true; _lblStatus.Text = "正在停止……"; };
            _btnClear.Click += (s, e) => { _list.Items.Clear(); };
            _btnSaveLog.Click += BtnSaveLog_Click;
            _btnRegCrx.Click += (s, e) => RunCrxScript(true);
            _btnUnregCrx.Click += (s, e) => RunCrxScript(false);
        }

        // ================================================================ 入口

        /// <summary>只负责"选择文件夹 + 列出待处理 exb"，不自动开始。
        /// 开始由「开始运行」按钮触发（用户要求两步分离）。</summary>
        public void SelectFolder()
        {
            if (_running)
            {
                MessageBox.Show("正在处理中，请先停止或等待完成。", "批量刷新图纸视图",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Diagnostics.Log("SelectFolder: 打开文件夹选择框");
            using (var dlg = new FolderBrowserDialog
            {
                Description = "选择包含 exb 图纸文件的文件夹（本级优先，找不到时含子文件夹）",
                ShowNewFolderButton = false
            })
            {
                DialogResult dr;
                try { dr = dlg.ShowDialog(this); }
                catch (Exception ex)
                {
                    Diagnostics.Log("SelectFolder: 选择框异常 " + ex.Message);
                    MessageBox.Show("打开文件夹选择框失败：" + ex.Message, "批量刷新图纸视图",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (dr != DialogResult.OK) { Diagnostics.Log("SelectFolder: 用户取消"); return; }
                Diagnostics.Log("SelectFolder: 选中 " + dlg.SelectedPath);
                LoadFolder(dlg.SelectedPath);
            }
        }

        private void BtnPick_Click(object sender, EventArgs e) { SelectFolder(); }

        /// <summary>把文件夹里的 exb 列进清单，并让「开始运行」可用。不启动批处理。
        /// 枚举：先找本级；本级 0 个时递归全部子文件夹。</summary>
        private void LoadFolder(string folder)
        {
            try
            {
                _folder = folder;
                _lblFolder.Text = "文件夹：" + folder;

                string[] files = SafeGetExbFiles(folder, false);
                if (files.Length == 0)
                {
                    files = SafeGetExbFiles(folder, true);
                    Diagnostics.Log("LoadFolder: 本级 0 个，已递归子文件夹，共 " + files.Length + " 个");
                }

                _list.Items.Clear();
                for (int i = 0; i < files.Length; i++)
                {
                    var item = new ListViewItem((i + 1).ToString());
                    item.SubItems.Add(Path.GetFileName(files[i]));
                    item.SubItems.Add("等待"); item.SubItems.Add(""); item.SubItems.Add(files[i]);
                    _list.Items.Add(item);
                }

                if (files.Length == 0)
                {
                    _btnStart.Enabled = false;
                    _lblStatus.Text = "该文件夹（含子文件夹）下没有 exb 文件";
                    Diagnostics.Log("LoadFolder: 结果 0 个 exb —— " + folder);
                    MessageBox.Show("该文件夹（含子文件夹）下没有找到 exb 文件：\n" + folder,
                        "批量刷新图纸视图", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _btnStart.Enabled = true;
                _lblStatus.Text = "已选择 " + files.Length + " 个 exb，点「开始运行」开始处理";
                Diagnostics.Log("已选择文件夹：" + folder + "（" + files.Length + " 个 exb，等待开始）");
            }
            catch (Exception ex)
            {
                _list.Items.Clear();
                _btnStart.Enabled = false;
                _lblStatus.Text = "读取文件夹失败";
                Diagnostics.Log("LoadFolder 失败：" + folder + " → " + ex);
                MessageBox.Show("读取文件夹失败：" + ex.Message, "批量刷新图纸视图",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>枚举 exb：recursive=false 只找本级；true 逐层深入（跳过无权限的目录）。</summary>
        private static string[] SafeGetExbFiles(string folder, bool recursive)
        {
            var list = new List<string>();
            try
            {
                if (!recursive)
                {
                    list.AddRange(Directory.GetFiles(folder, "*.exb", SearchOption.TopDirectoryOnly));
                }
                else
                {
                    var stack = new Stack<string>();
                    stack.Push(folder);
                    while (stack.Count > 0)
                    {
                        string dir = stack.Pop();
                        try { list.AddRange(Directory.GetFiles(dir, "*.exb", SearchOption.TopDirectoryOnly)); }
                        catch (Exception ex) { Diagnostics.Trace("枚举跳过 " + dir + ": " + ex.Message); }
                        try
                        {
                            var subs = Directory.GetDirectories(dir);
                            for (int i = subs.Length - 1; i >= 0; i--) stack.Push(subs[i]);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("枚举异常(" + (recursive ? "递归" : "本级") + "): " + ex.Message);
            }
            // 去掉 CAXA 自动备份（.bak）等非 exb 干扰；pattern 本身已限定，这里再按扩展名过滤一次
            list.RemoveAll(f => !string.Equals(Path.GetExtension(f), ".exb", StringComparison.OrdinalIgnoreCase));
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }

        private void BtnStart_Click(object sender, EventArgs e)
        {
            if (_running)
            {
                MessageBox.Show("正在处理中，请先停止或等待完成。", "批量刷新图纸视图",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder))
            {
                MessageBox.Show("请先点「选择文件夹」选定要处理的文件夹。", "批量刷新图纸视图",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StartBatch(_folder);
        }

        // ================================================================ 批处理

        private void StartBatch(string folder)
        {
            string[] files = SafeGetExbFiles(folder, false);
            if (files.Length == 0) files = SafeGetExbFiles(folder, true);
            if (files.Length == 0)
            {
                MessageBox.Show("该文件夹（含子文件夹）下没有找到 exb 文件：\n" + folder,
                    "批量刷新图纸视图", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _list.Items.Clear();
            for (int i = 0; i < files.Length; i++)
            {
                var item = new ListViewItem((i + 1).ToString());
                item.SubItems.Add(Path.GetFileName(files[i]));
                item.SubItems.Add("等待"); item.SubItems.Add(""); item.SubItems.Add(files[i]);
                _list.Items.Add(item);
            }

            _folder = folder;
            _lblFolder.Text = "文件夹：" + folder;
            _running = true; _stopRequested = false; savedCount = 0;
            _btnPick.Enabled = false; _btnStart.Enabled = false; _btnStop.Enabled = true;
PopupAutoYes.Enabled = true;
            // 方案 A：批处理期间抑制前台切换。图纸进程冷启动时会把前台抢过去（前台进程 IRONCAD
            // 派生的子进程有权设置前台窗口），这里把它压回后台；关掉勾选即回到改造前的行为。
            ForegroundGuard.Enabled = _chkBackground.Checked;
            Diagnostics.Log("==== 批处理开始：" + folder + "（共 " + files.Length + " 个 exb）"
                + "，后台化=" + (_chkBackground.Checked ? "开" : "关") + " ====");

            int ok = 0, fail = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (_stopRequested) { MarkRemaining(i, "已取消"); break; }
                _lblStatus.Text = "处理中 (" + (i + 1) + "/" + files.Length + ") " + Path.GetFileName(files[i]);
                if (ProcessOneFile(files[i], _list.Items[i])) ok++; else fail++;
                if (_stopRequested) { MarkRemaining(i + 1, "已取消"); break; }
            }

            ForegroundGuard.Enabled = false;
            PopupAutoYes.Enabled = false;
            _running = false;
            _btnPick.Enabled = true;
            _btnStop.Enabled = false;
            _btnStart.Enabled = _list.Items.Count > 0 && !string.IsNullOrEmpty(_folder);
            string summary = _stopRequested ? "已停止。完成 " + ok + " 个，失败 " + fail + " 个。"
                                             : "全部处理完。成功 " + ok + " 个，失败 " + fail + " 个。";
            summary += "（其中已存盘 " + savedCount + " 个）";
            _lblStatus.Text = summary;
            Diagnostics.Log("==== 批处理结束：" + summary + " ====");
            MessageBox.Show(summary, "批量刷新图纸视图", MessageBoxButtons.OK,
                fail == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        /// <summary>流程（全走 CAXA 原生路径，自己不派发任何内部命令；无固定 sleep）：
        ///   1) OpenFile2 打开 exb —— 它会阻塞到图纸真正打开（含"数据源更新→是"弹窗）
        ///   2) PopupAutoYes 点"是"（SendMessage 同步，返回=CAXA 的视图更新已跑完）
        ///   3) 就绪判定：文档窗口出现 +（弹窗已点掉 或 无弹窗且 CX_open_Impl 已结束）
        ///   4) CRX 向文档窗口投 WM_CLOSE → CAXA 原生弹"是否保存" → PopupAutoYes 点"是" → 保存并关闭
        ///   5) 检测到窗口消失/close-done 立刻进入下一个文件；时间戳轮询做客观校验
        /// 返回是否成功。</summary>
        private bool ProcessOneFile(string path, ListViewItem row)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string fileName = Path.GetFileName(path);
            DateTime beforeStamp = DateTime.MinValue;
            try { beforeStamp = File.GetLastWriteTime(path); } catch { }
            try
            {
                // 基准要在动手之前取
                int clickBase = PopupAutoYes.ClickCount;
                DateTime openStart = DateTime.Now;   // 用于"时间戳法"判定本次打开是否结束

                SetRow(row, "打开中", "");
                Diagnostics.Log("打开：" + path);

                // —— 1) 先投一条 PING 再打开 ——
                // 请求文件是单槽的，PING 会一直躺在那里；若 CRX 已加载（第 2 个文件起）
                // 会在打开期间就被取走并回 ack。若 CAXADraft 还没启动，这条 PING 会被
                // CRX 的 init 删掉（防残留命令），由下面第 3 步的补投循环兜底。
                int pingSeq0 = CrxBridge.PostCommand("CMD:PING");
                string pingAck0 = "REQ.ack | seq=" + pingSeq0;

                // —— 2) OpenFile2（COM 对 exb 返回不可靠，异常一律吞）——
                // 实测：它会阻塞到图纸真正打开（含"数据源更新→是"弹窗处理完）才返回/抛异常
                try
                {
                    bool ao;
                    _app.OpenFile2(path, false, true, out ao);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("OpenFile2 报错（正常）：" + ex.Message);
                }

                // —— 3) 等 CRX 就绪（PING 的 ack；首开时等于等 CAXADraft 冷启动）——
                // 坑：CRX 的 On_kInitAppMsg 会删除请求文件（防上次会话残留命令误执行），
                // 所以"打开前投的 PING"会在冷启动时被 init 吞掉、永远没有回执
                //（17:27 实测：第一个文件等满 120s 报 CRX 未就绪，第二个文件才成功）。
                // 对策：发现请求文件不在就补投一条 PING，直到收到 ack 为止。
                string pingAck = pingAck0;
                bool readyCrx = WaitUntil("等CRX就绪", 120000, () =>
                {
                    if (LogCount(pingAck) > 0) return true;
                    if (!File.Exists(CrxBridge.RequestPath))
                        pingAck = "REQ.ack | seq=" + CrxBridge.PostCommand("CMD:PING");
                    return false;
                }, 1000);
                if (!readyCrx)
                {
                    SetRow(row, "CRX未就绪", sw.Elapsed.ToString(@"mm\:ss"));
                    SetRowColor(row, RowRed);
                    Diagnostics.Log("CRX 未就绪（PING 无回执）：" + path);
                    return false;
                }
                // 基准必须在 CRX 加载完（日志已重建）之后才取，否则计数会错位
                int doneBase = LogCount("close-done");
                int viewBase = LogCount("commandEnded | CX_DFT_ViewClose");

                // —— 4) 就绪判定（三路并行信号，任一命中即走）——
                // 坑①（19:18 实测）：EnumWindows 只枚举顶层窗口，看不到 MDI 子窗口，
                //     DocWindowExists 曾永远 False —— 已改为 MDIClient + EnumChildWindows。
                // 坑②（19:41 实测）：PING 的 ack 可能【晚于】本次打开结束（warm start 时
                //     ack 比打开结束晚约 1s），在 ack 后取 openBase 会把"本次打开的结束"
                //     算进基准 → openEnded 永远不成立 → 没有弹窗的文件干等 30s 超时。
                //     改用【时间戳法】：只认 openStart 时刻之后出现的 CX_open_Impl ended。
                SetRow(row, "等就绪", "");
                bool docSeen = false;
                int calm = 0;
                bool ready = WaitUntil("等就绪", OpenReadyTimeoutMs, () =>
                {
                    if (!docSeen && DocWindowExists(fileName)) docSeen = true;
                    // a) 弹窗已被点掉：SendMessage 返回 = CAXA 的按钮处理（含视图更新）已跑完
                    if (PopupAutoYes.ClickCount > clickBase) return true;
                    // b) CRX 权威信号：openStart 之后的打开命令已结束，且当前无模态弹窗堵着
                    bool openEnded = OpenEndedSince(openStart);
                    if (docSeen && openEnded && PopupAutoYes.ModalCount == 0) return true;
                    // c) 兜底：窗口探测失灵时，打开已结束 + 无弹窗 + 连续 5 轮（约 0.5s）平静
                    if (openEnded && PopupAutoYes.ModalCount == 0) { if (++calm >= 5) return true; }
                    else calm = 0;
                    return false;
                });
                Diagnostics.Log("  步:就绪=" + ready + " 文档窗口=" + docSeen
                    + " t=" + sw.Elapsed.TotalSeconds.ToString("F1") + "s");

                // —— 5) 原生「保存并关闭」——
                // 向文档窗口投 WM_CLOSE → CAXA 原生弹「是否保存修改」→ PopupAutoYes 点「是」
                // → CAXA 自己执行 CX_save_Impl → CX_DFT_ViewSave → CX_DFT_ViewClose 并关闭文档。
                SetRow(row, "保存并关闭", "");
                // MOD.state 基准：CRX 在保存前记录文档 IsModified，C# 据此区分
                // 「有改动已存盘 / 已存盘」（基准在 CLOSEDOC 前取，只认新增行）
                int modBase1 = LogCount("MOD.state | modified=1");
                int modBaseT = LogCount("MOD.state");
                CrxBridge.PostCommand("CMD:CLOSEDOC|" + fileName);

                bool closeOk = WaitUntil("保存并关闭", CloseTimeoutMs, () =>
                    LogCount("close-done") > doneBase                        // CRX 权威信号
                    || LogCount("commandEnded | CX_DFT_ViewClose") > viewBase
                    || (docSeen && !DocWindowExists(fileName)));             // 窗口消失 = 已关，立刻下一个

                if (!closeOk)
                {
                    // 兜底：旧的原生命令派发（历史上有极小概率生效）
                    SetRow(row, "关闭兜底", "");
                    int d2 = LogCount("close-done");
                    int v2 = LogCount("commandEnded | CX_DFT_ViewClose");
                    CrxBridge.PostCommand("CMD:CLOSE");
                    closeOk = WaitUntil("关闭兜底", 20000,
                        () => LogCount("close-done") > d2 || LogCount("commandEnded | CX_DFT_ViewClose") > v2);
                }

                // —— 6) 等文档窗口真的销毁（通常 <1s），避免下一个文件在旧文档关闭过程中抢进来 ——
                if (closeOk && docSeen)
                    WaitUntil("等窗口消失", 5000, () => !DocWindowExists(fileName), 50);

                // —— 6) 存盘校验 + 结果显示 ——
                // 有改动判定：CRX 保存前写了 MOD.state | modified=1/0，只认本次新增的行
                bool modSeen = LogCount("MOD.state") > modBaseT;
                bool modified = modSeen && LogCount("MOD.state | modified=1") > modBase1;
                bool touched = WaitStampChange(path, beforeStamp, StampWaitMs);
                if (touched) savedCount++;

                sw.Stop();
                string info;
                Color rowColor = Color.Empty;
                if (!closeOk)
                {
                    info = touched ? "已存盘 / 关闭超时" : "关闭超时";
                    rowColor = RowRed;                       // 超时 → 标红
                }
                else if (modified)
                {
                    info = touched ? "有改动已存盘 / 已关闭" : "有改动-未存盘(建议人工检查)";
                    if (!touched) rowColor = RowRed;
                }
                else
                {
                    // 坑（10-24 实证）：touched=False 时 CAXA 并没有存盘——
                    // CRX 日志显示只有 CX_DFT_ViewSave 跑了、CX_save_Impl 没出现，
                    // 即 CAXA 保存核心只认它内部修改标志，认为无改动就跳过写盘。
                    // 所以绝不能显示「已保存」，必须作为警告标黄提示人工检查。
                    info = touched ? "已存盘 / 已关闭" : "文件未变化(警告) / 已关闭";
                    if (!touched) rowColor = RowYellow;
                }
                if (!ready) rowColor = RowRed;               // 打开就绪超时也算超时报错
                SetRow(row, closeOk ? (modified ? "完成·有改动" : "完成") : "部分失败",
                       sw.Elapsed.ToString(@"mm\:ss"));
                SetInfo(row, info);
                if (rowColor != Color.Empty) SetRowColor(row, rowColor);
                Diagnostics.Log((closeOk ? "完成" : "部分失败") + "：" + fileName
                    + " CLOSE=" + closeOk + " 改动=" + (modSeen ? (modified ? "1" : "0") : "?")
                    + " 存盘=" + touched
                    + " 耗时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
                return closeOk;
            }
            catch (Exception ex)
            {
                sw.Stop();
                SetRow(row, "失败", sw.Elapsed.ToString(@"mm\:ss"));
                SetRowColor(row, RowRed);
                // 记完整异常（类型+消息+栈首行），便于下次直接定位抛点
                string where = ex.StackTrace != null
                    ? " @" + ex.StackTrace.Split('\n')[0].Trim()
                    : "";
                Diagnostics.Log("失败：" + path + " → " + ex.GetType().Name
                    + ": " + ex.Message + where);
                return false;
            }
        }

        // ================================================================ 等待原语

        /// <summary>条件轮询。默认 100ms 一轮：CRX 取命令 100ms、窗口状态看门狗 100ms，
        /// 所以 100ms 足够灵敏，又不至于把 CPU 打满。命中后立刻返回，不做任何固定等待。</summary>
        private bool WaitUntil(string stateText, int timeoutMs, Func<bool> condition, int pollMs = 100)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!_stopRequested && sw.ElapsedMilliseconds < timeoutMs)
            {
                // 坑④（20:07 实测）：DoEvents 会泵 CAXA 内部消息，关文档等时机偶发抛
                // COM 异常（"外部组件发生异常"），曾把本已成功关闭的文件判成"失败"。
                // 这类异常与我们的流程无关，吞掉继续轮询。
                try { Application.DoEvents(); } catch (Exception ex)
                { Diagnostics.Trace("WaitUntil.DoEvents 吞掉异常：" + ex.Message); }
                bool ok = false;
                try { ok = condition(); } catch { }
                if (ok) return true;
                Thread.Sleep(pollMs);
            }
            return false;
        }

        /// <summary>统计 CRX 日志里某标记出现次数（共享读，不阻塞 CRX 追加写）。
        /// 用"计数是否增加"判断新事件，避免增量偏移在日志被重建时错位。</summary>
        private static int LogCount(string marker)
        {
            try
            {
                if (!File.Exists(CrxBridge.LogPath)) return 0;
                string text;
                using (var fs = new FileStream(CrxBridge.LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, System.Text.Encoding.UTF8))
                    text = sr.ReadToEnd();

                int n = 0, i = 0;
                while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
                { n++; i += marker.Length; }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>openStart 时刻之后，CRX 日志里是否出现过 CX_open_Impl 的 ended。
        /// 不与基准计数比较：warm start 时 PING 的 ack 晚于本次打开结束（19:41 实测），
        /// 在 ack 后取基准会把本次打开算进去，导致"打开已结束"永远不成立 → 干等 30s。
        /// CRX 日志行首是 HH:mm:ss 时间戳，逐行比对即可，跨文件也不会互相污染
        /// （上一个文件的打开结束一定早于本文件的 openStart）。</summary>
        private static bool OpenEndedSince(DateTime openStart)
        {
            const string marker = "commandEnded | CX_open_Impl";
            try
            {
                if (!File.Exists(CrxBridge.LogPath)) return false;
                string text;
                using (var fs = new FileStream(CrxBridge.LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, System.Text.Encoding.UTF8))
                    text = sr.ReadToEnd();

                TimeSpan startT = openStart.TimeOfDay;
                int i = 0;
                while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
                {
                    int lineStart = text.LastIndexOf('\n', i) + 1;      // 行首（\n 后）
                    // 坑③（20:07 实测）：CRX 日志时间戳只有【秒】精度，而 openStart 带毫秒。
                    // 1 秒内开完的小文件，其 ended 行时间（整秒）会早于 openStart（如
                    // 19:56:18.000 < 19:56:18.103）→ 永远判不中 → 干等 30s。
                    // 对策：留 1 秒容差。上一文件的 open-ended 距本文件 openStart 至少
                    // 1.5s（实测单文件最快 2.3s），不会误命中。
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

        /// <summary>轮询文件时间戳是否被刷新。存盘一般几十毫秒内可见，
        /// 所以一旦变化立刻返回，不再固定等 2s。</summary>
        private static bool WaitStampChange(string path, DateTime before, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { if (File.GetLastWriteTime(path) > before) return true; } catch { }
                Thread.Sleep(50);
            }
            try { return File.GetLastWriteTime(path) > before; } catch { return false; }
        }

        // ================================================================ 跨进程窗口探测

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        /// <summary>该句柄是否为文档窗口：class 含 AfxFrameOrView、标题含 exb 文件名。</summary>
        private static bool IsDocWindow(IntPtr h, string fileName)
        {
            var cls = new System.Text.StringBuilder(64);
            GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString().IndexOf("AfxFrameOrView", StringComparison.OrdinalIgnoreCase) < 0) return false;
            var txt = new System.Text.StringBuilder(512);
            GetWindowTextW(h, txt, txt.Capacity);
            return txt.ToString().IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>图纸文档窗口是否存在。坑：EnumWindows 只枚举【顶层】窗口，而
        /// CAXADraft 的文档窗口 AfxFrameOrView140u 是 MDIClient 的【子窗口】，
        /// 顶层枚举永远看不到（19:18 实测：就绪判定因此干等 30s 超时）。
        /// 所以必须：顶层找一遍（SDI 兜底）→ 找到 MDIClient 后 EnumChildWindows 找子窗口。</summary>
        private static bool DocWindowExists(string fileName)
        {
            bool found = false;
            IntPtr mdi = IntPtr.Zero;

            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                if (IsDocWindow(h, fileName)) { found = true; return false; }   // SDI / 顶层兜底

                var cls = new System.Text.StringBuilder(64);
                GetClassNameW(h, cls, cls.Capacity);
                if (cls.ToString().Equals("MDIClient", StringComparison.OrdinalIgnoreCase)) mdi = h;
                return true;
            }, IntPtr.Zero);

            if (found) return true;
            if (mdi == IntPtr.Zero) return false;

            // MDIClient 的子窗口里找（MDI 结构，CAXADraft 实际走这条路）
            EnumChildWindows(mdi, (h, l) =>
            {
                if (IsWindowVisible(h) && IsDocWindow(h, fileName)) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // ================================================================ CRX 注册 / 卸载

        /// <summary>注册/卸载 CRX 命令桥模块：调用随插件分发的 register-crx.ps1 / unregister-crx.ps1
        /// （脚本自身提权，扫描本机全部 CAXA 3D 版本，把 caxa3dCXDFTViewUpdate.crx 拷进
        /// CAXADraft\bin 并幂等登记 Modules\CxAutorun.cx；只动本插件自己的条目，
        /// 不影响其它插件共存）。下次 CAXADraft（图纸环境）启动时生效。</summary>
        private void RunCrxScript(bool register)
        {
            if (_running)
            {
                MessageBox.Show("正在处理中，请先停止或等待完成。", "刷新图纸视图",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string script = FindResource(register ? "register-crx.ps1" : "unregister-crx.ps1");
            if (script == null)
            {
                MessageBox.Show("未找到 CRX " + (register ? "注册" : "卸载") +
                    "脚本（register-crx.ps1 / unregister-crx.ps1）。", "刷新图纸视图",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                using (var proc = new System.Diagnostics.Process())
                {
                    proc.StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','" + script + "'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    proc.Start();
                }
                Diagnostics.Log("CRX " + (register ? "注册" : "卸载") + "：已请求执行（如弹 UAC 请允许）" + script);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("CRX 脚本启动失败：" + ex);
                MessageBox.Show("执行脚本失败：" + ex.Message + "\n\n脚本：" + script,
                    "刷新图纸视图", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>从输出目录逐级向上查找随插件分发的文件（不写死绝对路径）。</summary>
        private static string FindResource(string fileName)
        {
            var dirs = new List<string>();
            try { dirs.Add(AppDomain.CurrentDomain.BaseDirectory); } catch { }
            try { dirs.Add(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)); } catch { }
            foreach (string d in dirs.ToArray())
            {
                string cur = d;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
                {
                    string cand = Path.Combine(cur, fileName);
                    if (File.Exists(cand)) return cand;
                    cur = Path.GetDirectoryName(cur);
                }
            }
            return null;
        }

        // ================================================================ UI 辅助

        private void SetRow(ListViewItem row, string state, string cost)
        {
            SafeUi(() => { row.SubItems[2].Text = state; if (cost.Length > 0) row.SubItems[3].Text = cost; row.EnsureVisible(); });
        }

        private void SetInfo(ListViewItem row, string info)
        {
            SafeUi(() => { row.SubItems[4].Text = info; });
        }

        /// <summary>整行标色（UseItemStyleForSubItems 默认 true，BackColor 会覆盖所有子项）。</summary>
        private void SetRowColor(ListViewItem row, Color back)
        {
            SafeUi(() => { row.BackColor = back; });
        }

        // 行警示色：文件未变化=黄，超时/报错=红
        private static readonly Color RowYellow = Color.FromArgb(0xFF, 0xFA, 0xC8);
        private static readonly Color RowRed    = Color.FromArgb(0xFF, 0xC8, 0xC8);

        private void MarkRemaining(int fromIndex, string state)
        {
            SafeUi(() => { for (int i = fromIndex; i < _list.Items.Count; i++) if (_list.Items[i].SubItems[2].Text == "等待") _list.Items[i].SubItems[2].Text = state; });
        }

        private void SafeUi(Action act)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(act); } catch { } }
            else act();
        }

        private void BtnSaveLog_Click(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog
            {
                Filter = "文本文件|*.txt",
                FileName = "刷新图纸视图日志_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (var w = new StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8))
                        foreach (ListViewItem item in _list.Items)
                            w.WriteLine(item.SubItems[0].Text + "\t" + item.SubItems[1].Text + "\t" + item.SubItems[2].Text + "\t" + item.SubItems[3].Text + "\t" + item.SubItems[4].Text);
                    MessageBox.Show("日志已保存。", "批量刷新图纸视图", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show("保存失败：" + ex.Message, "批量刷新图纸视图", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
