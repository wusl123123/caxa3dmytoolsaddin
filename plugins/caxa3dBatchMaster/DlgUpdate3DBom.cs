// 机械移植自工程 caxa3dexbtracker 的 DlgUpdate3DBom.cs（全文件）。仅改 namespace 与项目内资源名，逻辑未动。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dBatchMaster
{
    /// <summary>
    /// 「更新3D明细」原生对话框的**自动勾选器**（用户需求原文：打开的更新3dbom 内还需要
    /// 手动勾选「更新」后确定 —— 本类就是把这一步自动化）。
    ///
    /// 实机界面（用户截图 2026-11-04 16:2x / 控件树 16:38）：
    ///   标题「更新3D明细」；分组「更新文件的BOM:」下一张两列表（源文件 | 更新），
    ///   每行的「更新」列是一个**复选框**，默认**不勾**；右下角「确定(O)」「取消(C)」。
    ///   不勾「更新」直接点确定 = 什么都不更新（这就是必须自动勾选的原因）。
    ///
    /// ★★ 这张表**不是** SysListView32（16:38 控件树已证）★★
    ///   `class=MFCGridCtrlEx id=3648`（enabled=1），下面是同类的 id=3639（「导入设置」空表，enabled=0）。
    ///   它是 MFC 自绘网格：`LVM_*` 一条也不认，MSAA 只回 40 条窗口装饰（系统/最小化/滚动条），
    ///   「更新」那一格在可访问性树里**根本不存在**。所以：
    ///     · 旧通道（LVM_SETITEMSTATE / MSAA / BM_CLICK）在这张框上**永远勾不上**；
    ///     · 真按钮里那个"更新文件的BOM:"是**分组框**，`BM_CLICK` 点它只回 BM_GETCHECK=0
    ///       （16:38 实机日志："BUTTON 点击后 BM_GETCHECK 仍未选中"就是这么来的）。
    ///
    /// ★ 现在的通道（唯一可行且可验证的一条）★
    ///   ① **定位**：BitBlt 抓网格客户区像素 → 扫出「白底 + 灰边框」的方框（实机 21x21px，
    ///      位于「更新」列左侧、首行中部）——不写死坐标，分辨率/行数变了照样认得出；
    ///   ② **点击**：向网格投 `WM_LBUTTONDOWN/UP`（客户区坐标，取方框中心）；不生效再退
    ///      `SendInput` 真鼠标（模态框本就独占前台，点完把光标还原）；
    ///   ③ **验证**：重新抓图数方框内部**暗像素**（空框=纯白 0 个，打勾=十几个）——
    ///      这是唯一能回读的状态源，也是本类"不许假装勾上"的落脚点。
    ///   历史教训：靠"按比例算坐标盲点"必然打偏（分辨率/缩放一变就废），靠返回码必然误判。
    ///
    /// ★ 安全纪律（对齐本工程"识别不到就拒绝"）★
    ///   · 勾不上「更新」就**不能点确定**（等于假装成功，还会把用户的模态框关掉）；
    ///     这时点「取消」释放模态，并把控件树打进日志，等下一轮按真实结构修。
    ///   · 只在**用户点了「更新3dbom」按钮之后**才布防（有 60 s 时限、同一时刻只一个哨兵），
    ///     平时绝不扫描/点击任何对话框。
    ///   · 点确定用 `PostMessage(BM_CLICK)`：`SendMessage` 会同步等 CAXA 跑完明细更新，
    ///     把本线程卡在模态循环里（参考工程实机踩过）。
    /// </summary>
    internal static class DlgUpdate3DBom
    {
        /// <summary>标题片段：两个都要命中才算本框（防误伤「更新3D内容」之类的近名框）。</summary>
        private const string TitleA = "更新";
        private const string TitleB = "明细";

        private const int PollMs = 200;
        private const int ArmTimeoutMs = 60000;   // 从点按钮算起的布防总时长
        private const int StuckRetryMs = 6000;    // 点过「确定」后框还在，隔多久补点一次
        private const int MaxOkAttempts = 2;      // 同一张框最多点 2 次「确定」（多点等于重复提交）

        private static volatile bool _armed;
        private static Thread _thread;

        /// <summary>
        /// 布防代数：每次 Arm 递增。哨兵循环里比对代数，**过期哨兵一律自杀**。
        /// 为什么必须有：`TryCheckGrid` 现在会等画面（最长 5 s），这期间用户再点一次按钮，
        /// 旧哨兵醒来时对话框可能已经是新的一次的了 —— 不认代数就会两个哨兵对同一张框各点一次
        /// 「确定」，等于重复提交。
        /// </summary>
        private static int _gen;

        /// <summary>当前这张框属于哪个进程（CAXA 3D 可能是双进程，见下方 FindDialog 说明）。</summary>
        private static int _dlgPid;
        private static string _dlgProc = "";

        /// <summary>
        /// 哨兵收尾回调（**在后台线程触发**）：UI 侧收到后必须自己切回 UI 线程再改控件。
        /// 只用于把状态行从"正在等对话框"改成真实结局，不承担任何判定职责。
        /// </summary>
        internal static Action<string> Finished;

        private static void Notify(string msg)
        {
            try { if (Finished != null) Finished(msg); } catch { }
        }

        /// <summary>是否正在布防（UI 侧据此提示/避免重复布防）。</summary>
        public static bool IsArmed { get { return _armed; } }

        /// <summary>
        /// 只**观察**「更新3D明细」有没有弹出来（**不代点**）—— 代点交给已布防的哨兵线程。
        ///
        /// 为什么需要它：`UpdateDraftViews(exb, TRUE)` 实测**不是恒静默**。
        /// 2026-11-04 19:44:47（前台 ics、目标图未打开）→ 静默，exb 当场被改写；
        /// 2026-11-04 19:45:28（前台就是那张 exb、已打开）→ **弹框**。
        /// 所以 API 路径不能沿用"弹了就按取消"（那等于放弃更新），要让哨兵勾「更新」→ 点「确定」办完。
        ///
        /// ★ 循环里必须 `DoEvents()`：本方法跑在宿主 UI 线程上，`Thread.Sleep` 纯等会把模态框
        ///   自己的消息泵饿死，哨兵投进去的点击永远不被处理（同「CRX 靠 WM_TIMER 轮询」那条纪律）。
        /// </summary>
        /// <returns>true = 弹过（detail 说明结局）；false = 到点没弹</returns>
        internal static bool WatchDialog(int waitMs, out string detail)
        {
            detail = "";
            IntPtr seen = IntPtr.Zero;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < waitMs)
            {
                seen = FindDialog();
                if (seen != IntPtr.Zero) break;
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(60);
            }
            if (seen == IntPtr.Zero)
            {
                detail = "等 " + waitMs + " ms，没有出现「更新3D明细」对话框（确认无弹窗）";
                return false;
            }

            string hwndTxt = "hwnd=0x" + seen.ToInt64().ToString("X8");
            Diagnostics.Log("dlgbom: API 调用后「更新3D明细」被弹出来了（" + hwndTxt + "），交给哨兵勾「更新」");
            var sw2 = Stopwatch.StartNew();
            while (sw2.ElapsedMilliseconds < 15000)
            {
                if (FindDialog() == IntPtr.Zero)
                {
                    detail = "「更新3D明细」弹出来了（" + hwndTxt + "），已由哨兵勾「更新」并点「确定」关闭";
                    Diagnostics.Log("dlgbom: " + detail);
                    return true;
                }
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(60);
            }

            detail = "「更新3D明细」弹出来了（" + hwndTxt + "）但 15 秒内没关掉，请到界面上处理";
            Diagnostics.Log("dlgbom: " + detail);
            return true;
        }

        /// <summary>
        /// 布防：起后台哨兵，60 秒内找到「更新3D明细」就自动勾「更新」→ 点「确定」。
        /// 重复调用会先撤掉上一个哨兵（同一时刻只允许一个）。
        /// </summary>
        public static void Arm(string targetDesc)
        {
            Disarm();
            _armed = true;
            int gen = ++_gen;
            _thread = new Thread(delegate () { Loop(targetDesc, gen); });
            _thread.IsBackground = true;
            _thread.Name = "DlgUpdate3DBom";
            _thread.Start();
            Diagnostics.Log("dlgbom: 已布防自动勾选（目标=" + (targetDesc ?? "?") + "，时限 " +
                            (ArmTimeoutMs / 1000) + " 秒）");
        }

        /// <summary>撤防（窗口关闭 / 下一次布防前调用）。</summary>
        public static void Disarm()
        {
            if (!_armed) return;
            _armed = false;
            Diagnostics.Log("dlgbom: 撤防自动勾选");
        }

        // ============================================================ 哨兵主循环

        private static void Loop(string targetDesc, int gen)
        {
            var sw = Stopwatch.StartNew();
            IntPtr seenDlg = IntPtr.Zero;
            int okAttempts = 0;
            int lastOkTick = 0;
            bool reportedFail = false;

            while (_armed && gen == _gen && sw.ElapsedMilliseconds < ArmTimeoutMs)
            {
                IntPtr dlg = FindDialog();
                if (dlg == IntPtr.Zero)
                {
                    if (seenDlg != IntPtr.Zero)
                    {
                        Diagnostics.Log("dlgbom: 「更新3D明细」已关闭，哨兵退出");
                        _armed = false;          // 结束即落地：MainForm 的让位看门狗靠它判"流程已结束"
                        Notify(okAttempts > 0
                            ? "已勾「更新」并点「确定」，明细更新由宿主执行（界面结果为准）"
                            : (reportedFail
                                ? "没能勾上「更新」，已点「取消」释放对话框（明细未更新）"
                                : "对话框已关闭（未代点）"));
                        return;
                    }
                    Thread.Sleep(PollMs);
                    continue;
                }

                if (dlg != seenDlg)
                {
                    seenDlg = dlg;
                    okAttempts = 0;
                    reportedFail = false;
                    Diagnostics.Log("dlgbom: 捕获对话框 hwnd=0x" + dlg.ToInt64().ToString("X8") +
                                    " pid=" + _dlgPid + "(" + _dlgProc + ") title=[" + TextOf(dlg) + "]");
                    DumpTree(dlg);
                }

                // 已经点过确定、框还没关：等 CAXA 跑完；超过 StuckRetryMs 才补点
                if (okAttempts > 0 && Environment.TickCount - lastOkTick < StuckRetryMs)
                {
                    Thread.Sleep(PollMs);
                    continue;
                }
                if (okAttempts >= MaxOkAttempts)
                {
                    if (!reportedFail)
                    {
                        reportedFail = true;
                        Diagnostics.Log("dlgbom: 已点 " + MaxOkAttempts + " 次「确定」框仍未关闭，" +
                                        "停止代点，交回人工（明细更新可能正在后台跑，别重复点）");
                    }
                    Thread.Sleep(PollMs);
                    continue;
                }

                bool checkedOk = false;
                string how = "";
                try { checkedOk = EnsureUpdateChecked(dlg, out how); }
                catch (Exception ex) { Diagnostics.Log("dlgbom: 勾选异常 " + ex.Message); }

                // 等画面可能花上几秒，这期间若已重新布防，本哨兵立刻退场（别对新的框再点一次确定）
                if (!_armed || gen != _gen) { Diagnostics.Log("dlgbom: 已过期（重新布防），本哨兵退出"); return; }

                if (!checkedOk)
                {
                    if (!reportedFail)
                    {
                        reportedFail = true;
                        Diagnostics.Log("dlgbom: 没能勾上「更新」列（" + how + "）——按纪律**不点确定**，" +
                                        "改为点「取消」释放模态框。请把上面『控件树』几行发给开发定位。");
                        DumpMsaa(dlg);
                        if (!ClickButton(dlg, "取消")) ClickOkById(dlg, true);
                    }
                    Thread.Sleep(PollMs);
                    continue;
                }

                okAttempts++;
                lastOkTick = Environment.TickCount;
                Diagnostics.Log("dlgbom: 第 " + okAttempts + " 次提交：已勾「更新」（" + how + "）→ 点确定");
                if (!ClickButton(dlg, "确定")) ClickOkById(dlg, false);
                Thread.Sleep(PollMs);
            }

            if (_armed)
            {
                Diagnostics.Log("dlgbom: " + (ArmTimeoutMs / 1000) + " 秒内没等到「更新3D明细」对话框，" +
                                "哨兵自动退出");
                _armed = false;
                Notify("等 " + (ArmTimeoutMs / 1000) + " 秒没出现「更新3D明细」对话框，已停止代点");
            }
        }

        // ============================================================ 找对话框

        /// <summary>
        /// 找「更新3D明细」：标题同时含「更新」「明细」的可见顶层窗口。
        ///
        /// ★ 进程白名单而不是"只认本进程" ★
        ///   CAXA 3D 2027 存在双进程形态：某些配置下图纸环境是独立的 CAXADraft.exe，
        ///   框会弹在**那个**进程里（caxa3dcodenameextraction 实机踩过「是否保存修改」弹在
        ///   CAXADraft.exe 上、只扫本进程永远点不到）。所以这里认 IronCAD / CAXADraft 两个进程，
        ///   并把 pid 记下来：跨进程时控件消息通道不可用（LVITEM 缓冲在本地地址空间），
        ///   自动降级到「真按钮 BM_CLICK + MSAA」两条通道。
        /// 标题匹配足够窄（同进程里没有第二张「更新…明细」框），不会误伤用户别的对话框。
        /// </summary>
        private static IntPtr FindDialog()
        {
            IntPtr found = IntPtr.Zero;
            int myPid = Process.GetCurrentProcess().Id;
            int hitPid = 0;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != myPid)
                {
                    string pn = ProcessNameOf(pid);
                    if (!string.Equals(pn, "CAXADraft", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(pn, "IronCAD", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                string t = TextOf(h);
                if (t.Length == 0) return true;
                if (t.IndexOf(TitleA, StringComparison.Ordinal) < 0) return true;
                if (t.IndexOf(TitleB, StringComparison.Ordinal) < 0) return true;
                found = h;
                hitPid = pid;
                return false;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero && hitPid != _dlgPid)
            {
                _dlgPid = hitPid;
                _dlgProc = ProcessNameOf(hitPid);
            }
            return found;
        }

        private static string ProcessNameOf(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) return p.ProcessName;
            }
            catch { return ""; }
        }

        // ============================================================ 勾「更新」

        /// <summary>让「更新」列变成勾选态。返回 true = **已回读确认**勾上（不是"点过了"）。</summary>
        private static bool EnsureUpdateChecked(IntPtr dlg, out string how)
        {
            // ── ⓪ 主通道：MFCGridCtrlEx 自绘网格（实机真实结构）──
            IntPtr grid = FindGrid(dlg);
            if (grid != IntPtr.Zero)
            {
                if (TryCheckGrid(grid, out how)) return true;
                how = "网格(MFCGridCtrlEx id=" + GetDlgCtrlID(grid) + ")" + how;
                // 已经确认过是自绘网格：下面那些 SysListView 通道在这张框上一个都不会成立，
                // 再走一遍只会点到分组框（"更新文件的BOM:"），所以直接判失败、不点确定。
                return false;
            }

            // ── 以下旧通道只在"不是这张网格"的界面版本上兜底 ──
            how = "";
            bool cross = _dlgPid != Process.GetCurrentProcess().Id;
            if (cross) how = "跨进程(" + _dlgProc + ")：控件消息通道不可用，先走 MSAA";

            // ① 列表控件 state image（仅同进程：LVITEM 缓冲必须落在目标进程地址空间）
            IntPtr lv = cross ? IntPtr.Zero : FindDescendantByClass(dlg, "SysListView32");
            if (!cross && lv == IntPtr.Zero) lv = FindDescendantByClass(dlg, "SysListView");
            if (lv != IntPtr.Zero && Lr(Send(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero)) > 0)
            {
                int count = Lr(Send(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero));
                bool allChecked = true;
                for (int i = 0; i < count; i++)
                {
                    if (IsRowChecked(lv, i)) continue;
                    SetRowChecked(lv, i);
                    if (!IsRowChecked(lv, i))
                    {
                        ToggleRowByKeyboard(lv, i);
                        if (!IsRowChecked(lv, i)) { allChecked = false; break; }
                    }
                }
                if (allChecked)
                {
                    how = "LISTVIEW state image（共 " + count + " 行，已回读 LVM_GETITEMSTATE）";
                    return true;
                }
                how = "LISTVIEW 有 " + count + " 行但 state image 设不上（不是复选式列表）";
            }

            // ② MSAA
            MsaaHit hit = MsaaFindCheck(dlg, "更新");
            if (hit != null)
            {
                if (!hit.Checked) { MsaaDoDefault(hit); Thread.Sleep(180); hit = MsaaFindCheck(dlg, "更新"); }
                if (hit != null && hit.Checked)
                {
                    how = "MSAA「" + hit.Name + "」role=" + hit.Role + " accDoDefaultAction（已回读 state）";
                    return true;
                }
                how = "MSAA 命中「" + (hit == null ? "?" : hit.Name) + "」但勾选状态未变";
            }

            // ③ 真复选按钮（**排除分组框**：分组框文字以「更新」打头但不是复选按钮）
            IntPtr btn = FindCheckBoxButton(dlg);
            if (btn != IntPtr.Zero)
            {
                if (Lr(Send(btn, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero)) != BST_CHECKED)
                    Post(btn, BM_CLICK);
                Thread.Sleep(150);
                if (Lr(Send(btn, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero)) == BST_CHECKED)
                {
                    how = "BUTTON BM_CLICK（已回读 BM_GETCHECK）";
                    return true;
                }
                how = "BUTTON 点击后 BM_GETCHECK 仍未选中";
            }

            if (how.Length == 0) how = "既没有网格/列表/复选条目，也没有复选按钮";
            return false;
        }

        /// <summary>
        /// 找**真复选按钮**（`BS_AUTOCHECKBOX/BS_CHECKBOX`），并按文字含「更新」过滤。
        /// ★ 必须查样式位：那张框里的「更新文件的BOM:」是 **BS_GROUPBOX** 分组框，文字也以「更新」
        ///   打头，只按文字找必然点到分组框，然后 BM_GETCHECK 恒 0（16:38 实机日志的坑）。
        /// </summary>
        private static IntPtr FindCheckBoxButton(IntPtr dlg)
        {
            IntPtr hit = IntPtr.Zero;
            EnumChildWindows(dlg, delegate (IntPtr h, IntPtr l)
            {
                if (!ClassIs(h, "Button")) return true;
                long style = (uint)GetWindowLong(h, GWL_STYLE);
                long type = style & BS_TYPEMASK;
                if (type != BS_CHECKBOX && type != BS_AUTOCHECKBOX &&
                    type != BS_3STATE && type != BS_AUTO3STATE) return true;
                string raw = TextOf(h);
                if (raw.Length == 0 || raw.IndexOf("更新", StringComparison.Ordinal) < 0) return true;
                hit = h;
                return false;
            }, IntPtr.Zero);
            return hit;
        }

        private static bool IsRowChecked(IntPtr lv, int index)
        {
            IntPtr st = Send(lv, LVM_GETITEMSTATE, (IntPtr)index, (IntPtr)LVIS_STATEIMAGEMASK);
            int v = (int)((uint)Lr(st) & LVIS_STATEIMAGEMASK);
            return v == (int)INDEXTOSTATEIMAGEMASK(2);
        }

        /// <summary>改 state image：mask 只给 STATEIMAGEMASK，值给 2（= checked 图）。</summary>
        private static void SetRowChecked(IntPtr lv, int index)
        {
            var item = new LVITEM
            {
                stateMask = LVIS_STATEIMAGEMASK,
                state = INDEXTOSTATEIMAGEMASK(2),
                iItem = index,
                iSubItem = 0
            };
            IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LVITEM)));
            try
            {
                Marshal.StructureToPtr(item, buf, false);
                Send(lv, LVM_SETITEMSTATE, (IntPtr)index, buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>选中该行并投一次空格：复选框式 ListView 上等价于用户按空格切换。</summary>
        private static void ToggleRowByKeyboard(IntPtr lv, int index)
        {
            var item = new LVITEM
            {
                stateMask = LVIS_SELECTED | LVIS_FOCUSED,
                state = LVIS_SELECTED | LVIS_FOCUSED,
                iItem = index,
                iSubItem = 0
            };
            IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LVITEM)));
            try
            {
                Marshal.StructureToPtr(item, buf, false);
                Send(lv, LVM_SETITEMSTATE, (IntPtr)index, buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
            Post(lv, WM_KEYDOWN, (IntPtr)VK_SPACE, IntPtr.Zero);
            Post(lv, WM_KEYUP, (IntPtr)VK_SPACE, IntPtr.Zero);
            Thread.Sleep(120);
        }

        // ============================================================ ⓪ MFCGridCtrlEx 网格（主通道）

        /// <summary>
        /// 「更新文件的BOM:」下面那张表的控件 ID —— 实机 2026-11-04 16:38 控件树取证：
        /// `class=MFCGridCtrlEx id=3648 rect=632,260 543x130 enabled=1`；
        /// 下面「导入设置」那张同类表是 id=3639 且 enabled=0。按 ID 认最稳，认不到再退「第一个可用的网格」。
        /// </summary>
        private const int GridCtrlId = 3648;

        /// <summary>方框内部暗像素阈值：空框 ≈ 0（纯白），打勾 ≈ 十几~二十几个。</summary>
        private const int CheckedInk = 6;

        /// <summary>
        /// 等网格"画出来"的时间上限。★ 实机 17:01 两次失败的真正原因是**太早**：
        ///   捕获对话框 → 30 ms 后就抓帧判"没有复选框"，而 MFC 弹框后 `MFCGridCtrlEx`
        ///   还没跑过 WM_PAINT，抓到的是一块纯色空白（wxh 正常、内容全空）。
        ///   所以首帧判死是错的，必须"抓—等—再抓"，直到画面里真的出现结构。
        /// </summary>
        private const int PaintWaitMs = 5000;
        private const int RetryMs = 160;

        /// <summary>一次截图的灰度副本（只留亮度，够判"框/白/勾"三种状态）。</summary>
        private sealed class Shot
        {
            public int W, H;
            public byte[] Gray;
            public int At(int x, int y) { return Gray[y * W + x]; }
        }

        private static IntPtr FindGrid(IntPtr dlg)
        {
            IntPtr byId = IntPtr.Zero, firstUsable = IntPtr.Zero;
            EnumChildWindows(dlg, delegate (IntPtr h, IntPtr l)
            {
                if (ClassOf(h).IndexOf("MFCGridCtrl", StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (GetDlgCtrlID(h) == GridCtrlId) { byId = h; return false; }
                if (firstUsable == IntPtr.Zero && IsWindowVisible(h) && IsWindowEnabled(h)) firstUsable = h;
                return true;
            }, IntPtr.Zero);
            return byId != IntPtr.Zero ? byId : firstUsable;
        }

        /// <summary>
        /// 勾上网格里「更新」列的全部复选框：**等画面** → 抓图定位 → 点击 → **重抓图回读**确认。
        /// 返回 false 时 how 里写清失败在哪一步（供用户把日志发回来定位）。
        ///
        /// ★ 为什么要"等画面"★
        ///   17:01 实机：`捕获对话框` 到 `没能勾上` 只隔 34 ms，中间只抓了一帧。新弹出的 MFC 对话框里，
        ///   自绘网格的第一次 WM_PAINT 还没发生，BitBlt 抓到的是对话框底色（纯色空白）——
        ///   尺寸对、内容空，于是"找不到方框"。这不是算法错，是**抓得太早**。
        ///   现在改成最多等 PaintWaitMs，每 RetryMs 抓一帧，直到画面里出现结构为止。
        /// </summary>
        private static bool TryCheckGrid(IntPtr grid, out string how)
        {
            how = "";

            // ── A. 等画面（每帧都记诊断，最后失败时把诊断写进日志）──
            Shot s = null, lastOk = null;
            int hi = 255, shots = 0;
            List<int[]> boxes = null;
            string diag = "还没抓到有效帧";
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < PaintWaitMs)
            {
                shots++;
                Shot g = Grab(grid);
                if (g == null) { diag = "截图失败(BitBlt/GetDIBits)"; Thread.Sleep(RetryMs); continue; }
                if (g.W < 60 || g.H < 30) { diag = "尺寸异常 " + g.W + "x" + g.H; Thread.Sleep(RetryMs); continue; }

                int h2 = BackLum(g);
                var found = FindBoxes(g, h2);
                lastOk = g;
                if (found.Count > 0) { s = g; hi = h2; boxes = found; break; }

                diag = Describe(g, h2);
                Thread.Sleep(RetryMs);
            }

            if (boxes == null || boxes.Count == 0)
            {
                DumpGrid(lastOk, shots);
                how = " " + (lastOk == null ? "?" : lastOk.W + "x" + lastOk.H) + " 等 " + PaintWaitMs +
                      "ms（" + shots + " 帧）仍没扫出复选框（" + diag + "）；已存 %TEMP%\\caxa3dcodenameextraction_grid.bmp";
                return false;
            }

            // ── B. 报出这一刻看到的框（空框 ink≈0，已勾 ink≈十几个）──
            var sb = new StringBuilder();
            int need = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                int[] b = boxes[i];
                int ink = InkAround(s, (b[0] + b[2]) / 2, (b[1] + b[3]) / 2, Half(b), hi);
                sb.Append(" #").Append(i + 1).Append("{").Append(b[0]).Append(",").Append(b[1])
                  .Append(" ").Append(b[2] - b[0]).Append("x").Append(b[3] - b[1])
                  .Append(" ink=").Append(ink).Append(ink >= CheckedInk ? " 已勾" : "").Append("}");
                if (ink < CheckedInk) need++;
            }
            Diagnostics.Log("dlgbom: 网格 " + s.W + "x" + s.H + "（第 " + shots + " 帧，背景亮度 " + hi +
                            "，通道=" + _lastGrabVia + "）找到 " + boxes.Count + " 个复选框" + sb);

            if (need == 0)
            {
                how = " 网格里 " + boxes.Count + " 个框已是勾选态（像素回读）";
                return true;
            }

            // ── C. 逐个点：先 PostMessage（不抢焦点），不行再上真鼠标 ──
            int done = 0;
            int missStreak = 0;          // 连续点不动的框数：连着 2 个就说明"这帧是假的"，别再挨个空点
            for (int bi = 0; bi < boxes.Count; bi++)
            {
                int[] b = boxes[bi];
                int cx = (b[0] + b[2]) / 2, cy = (b[1] + b[3]) / 2;
                int half = Half(b);
                if (InkAround(s, cx, cy, half, hi) >= CheckedInk) { done++; continue; }

                bool ok = false;
                string tryLog = "";
                for (int attempt = 1; attempt <= 3 && !ok; attempt++)
                {
                    string via;
                    if (attempt == 1) { via = "WM_MOUSEMOVE+DOWN/UP"; PostClick(grid, cx, cy); Thread.Sleep(240); }
                    else { via = "SendInput 真鼠标"; RealClick(grid, cx, cy); Thread.Sleep(320); }

                    Shot now = Grab(grid);
                    if (now == null) { tryLog += " 第" + attempt + "次(" + via + ")后截图失败;"; continue; }
                    int ink = InkAround(now, cx, cy, half, hi);
                    tryLog += " 第" + attempt + "次(" + via + ")→ink=" + ink + ";";
                    if (ink >= CheckedInk)
                    {
                        ok = true;
                        s = now;                       // 用最新画面继续判后面的框
                        done++;
                        how += " [#" + (bi + 1) + " 于 " + cx + "," + cy + " " + via + " 勾上，ink=" + ink + "]";
                    }
                }
                if (!ok) Diagnostics.Log("dlgbom: 第 " + (bi + 1) + " 个框没勾上 →" + tryLog);

                missStreak = ok ? 0 : missStreak + 1;
                if (missStreak >= 2)
                {
                    // 两个框连点三次都没反应 ⇒ 不是"点得不准"，而是这一帧本身不可信
                    // （画面被遮挡抓错 / 网格还没画完 / 扫出来的是假框）。
                    // 继续对剩下的假框空点只会白等十几秒，直接判失败交给上层重来。
                    Diagnostics.Log("dlgbom: 连续 " + missStreak + " 个框点不动 —— 判定本帧画面不可信" +
                                    "（遮挡/未绘制/假框），停止继续点击");
                    break;
                }
            }

            if (done < boxes.Count)
            {
                how = " " + boxes.Count + " 个框只勾上 " + done + " 个" + how;
                return false;
            }

            // ── D. 提交前最终复检（自绘格重绘有延迟，不复检就可能"以为勾上了"）──
            Shot fin = Grab(grid);
            if (fin != null)
            {
                int still = 0;
                foreach (int[] b in boxes)
                    if (InkAround(fin, (b[0] + b[2]) / 2, (b[1] + b[3]) / 2, Half(b), hi) < CheckedInk) still++;
                if (still > 0)
                {
                    how = " " + boxes.Count + " 个框点完复检仍有 " + still + " 个未勾上" + how;
                    return false;
                }
            }
            return true;
        }

        private static int Half(int[] b)
        {
            int w = b[2] - b[0], h = b[3] - b[1];
            return Math.Max(4, Math.Min(w, h) / 2);
        }

        /// <summary>
        /// 数「框中心一定范围内」的**暗像素**（勾）与**亮像素**（白底）。
        ///
        /// 两个数都要：只有暗像素多可能是整格被选中/高亮的纯色填充（那也会让暗像素暴涨），
        /// 真勾选是"白底 + 一笔细勾"，所以亮像素必须还占一定比例。
        /// 宁可判失败（不点确定）也不许把"选中了"当成"勾上了"。
        /// </summary>
        private static int InkAround(Shot s, int cx, int cy, int half, int hi)
        {
            int darkMax = hi - 60;
            int lightMin = hi - 20;
            int inner = Math.Max(2, half - 4);
            int dark = 0, light = 0, total = 0;
            for (int y = cy - inner; y <= cy + inner; y++)
            {
                if (y < 0 || y >= s.H) continue;
                for (int x = cx - inner; x <= cx + inner; x++)
                {
                    if (x < 0 || x >= s.W) continue;
                    total++;
                    int v = s.At(x, y);
                    if (v <= darkMax) dark++;
                    else if (v >= lightMin) light++;
                }
            }
            if (total < 16) return 0;
            // 亮像素占比过低 ⇒ 整格被填充（选中态），不是勾选态
            if (light * 100 < total * 20) return 0;
            return dark;
        }

        /// <summary>
        /// 向网格投一次左键（客户区坐标）——**纯消息，不动真光标**，即"后台点击"。
        ///
        /// ★ 必须先投一次 `WM_MOUSEMOVE` ★
        ///   MFC 自绘网格的命中判定依赖控件内部"当前鼠标所在格/悬停态"（`m_CellBefore` 之类），
        ///   只投 down/up 时它可能认为鼠标不在格内，直接当无效点击丢掉——这就是实机
        ///   17:10:18「七连点、每次都 ink=0」的第二种可能来源。先移到位再按下，等价于真人一次点击。
        /// </summary>
        private static void PostClick(IntPtr hwnd, int x, int y)
        {
            IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
            PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, lp);
            Thread.Sleep(20);
            PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
            Thread.Sleep(30);
            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lp);
        }

        /// <summary>
        /// 兜底：移动真实光标点一下再还原。模态框本就独占前台，代价只是光标抖一下；
        /// 但 MFC 自绘控件有时只信真鼠标（SetCapture/悬停态），所以必须有这条。
        /// </summary>
        private static void RealClick(IntPtr hwnd, int x, int y)
        {
            POINT p; p.X = x; p.Y = y;
            if (!ClientToScreen(hwnd, ref p)) return;
            POINT old;
            bool hasOld = GetCursorPos(out old);
            SetCursorPos(p.X, p.Y);
            Thread.Sleep(30);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            if (hasOld) SetCursorPos(old.X, old.Y);
        }

        /// <summary>
        /// 抓控件客户区像素 → 灰度数组。
        ///
        /// ★★ 首选**后台抓**（PrintWindow），不再无条件抓屏幕 ★★
        ///   实机 17:10:18 的"7 个复选框只勾上 0 个"根因就在这里：Grab 以前只用
        ///   `GetDC(NULL) + BitBlt` —— 抓的是"那块屏幕像素"。本工具窗（常态置顶）一盖上去，
        ///   抓到的就是**它自己**（日志里 `背景亮度 250` 正是本窗日志框底色 #FAFAFA），
        ///   于是扫出一串假框（{78,2}{78,9}…x=78 整列），点它们当然毫无反应。
        ///   PrintWindow 让目标窗自己把画面渲染到内存 DC，**被遮挡/沉到后面也拿得到真画面**，
        ///   这条通道与窗口 Z 序彻底解耦。
        ///
        /// ★ 兜底★
        ///   MFC 自绘控件对 `WM_PRINTCLIENT` 的响应参差不齐，PrintWindow 可能回一张纯色空白；
        ///   所以抓到帧后用 `HasStructure` 验一次"到底有没有画东西"，没内容就退回屏幕 BitBlt
        ///   （模态框独占前台时它就等于用户看到的画面）。
        /// </summary>
        private static Shot Grab(IntPtr hwnd)
        {
            Shot back = GrabVia(hwnd, true);
            if (back != null && HasStructure(back)) { _lastGrabVia = "后台PrintWindow"; return back; }

            Shot scr = GrabVia(hwnd, false);
            if (scr != null && HasStructure(scr)) { _lastGrabVia = "屏幕BitBlt"; return scr; }
            _lastGrabVia = "无效帧";
            return back ?? scr;      // 两条通道都没"内容"：把最后一次抓到的交出去做取证
        }

        /// <summary>最近一次抓帧走的通道（只进日志，供复盘"到底抓到的是谁"）。</summary>
        private static volatile string _lastGrabVia = "?";

        /// <summary>一帧画面里到底有没有内容：纯色帧（全 255 / 全 0）判为没抓到。</summary>
        private static bool HasStructure(Shot s)
        {
            if (s == null || s.Gray == null || s.Gray.Length == 0) return false;
            int mn = 255, mx = 0, dark = 0;
            for (int i = 0; i < s.Gray.Length; i++)
            {
                int v = s.Gray[i];
                if (v < mn) mn = v;
                if (v > mx) mx = v;
                if (v < 100) dark++;
            }
            if (mx - mn < 8) return false;                    // 纯色 ⇒ 没画出来
            return (long)dark * 1000 >= s.Gray.Length;        // 至少 0.1% 深色笔画（文字/勾/边框）
        }

        /// <summary>
        /// 抓一帧。`printWindow=true` 走后台渲染；`false` 走屏幕 BitBlt。
        /// 两条通道共用同一套 DIB 提取（32bpp 自上而下 + 灰度化）。
        /// </summary>
        private static Shot GrabVia(IntPtr hwnd, bool printWindow)
        {
            RECT rc;
            if (!GetClientRect(hwnd, out rc)) return null;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0 || w > 4000 || h > 4000) return null;

            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
            IntPtr old = SelectObject(mem, bmp);

            bool blt;
            if (printWindow)
            {
                // PW_CLIENTONLY(1) | PW_RENDERFULLCONTENT(2)：只画客户区，且走 DWM 合成后的内容，
                // 被别的窗口压住也能拿到真画面。老系统不认 0x2 就退回普通 PrintWindow。
                blt = PrintWindow(hwnd, mem, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
                if (!blt) blt = PrintWindow(hwnd, mem, PW_CLIENTONLY);
                if (!blt) blt = PrintWindow(hwnd, mem, 0);
            }
            else
            {
                POINT org; org.X = 0; org.Y = 0;
                ClientToScreen(hwnd, ref org);
                blt = BitBlt(mem, 0, 0, w, h, screen, org.X, org.Y, SRCCOPY | CAPTUREBLT);
            }

            var bi = new BITMAPINFO();
            bi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;          // 负高度 = 自上而下
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0;      // BI_RGB

            byte[] buf = new byte[w * h * 4];
            int lines = blt ? GetDIBits(mem, bmp, 0, (uint)h, buf, ref bi, 0) : 0;

            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);

            if (lines != h) return null;

            var s = new Shot { W = w, H = h, Gray = new byte[w * h] };
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    s.Gray[o + x] = (byte)((buf[i + 2] * 30 + buf[i + 1] * 59 + buf[i] * 11) / 100);
                }
            }
            return s;
        }

        /// <summary>
        /// 扫出所有「白底 + 灰边框」的小方块（= 未勾/已勾的复选框）。
        ///
        /// 判据来自 16:38 截图（1.PNG）的**逐像素实测**，但**不用绝对阈值**：
        ///   上/左边线 160、下/右浮雕阴影 227、单元格底 255、网格空白区 227。
        ///   绝对阈值(245/240)只在"255 白底"的前提下成立；一旦主题/DPI/选中态变了就整体失准。
        ///   所以先算 `hi` = 画面亮部基准（P85，通常就是 255），再按相对量取判据：
        ///     边框 ≤ hi-10、内部亮 ≥ hi-14、勾 ≤ hi-60。
        ///   这等价于实测值 245/241/195，但换了配色也照样成立。
        ///
        /// ★ **必须看"外圈是否亮底"**：表头那些 3D 单元格边线拼出来的假框（19x17 等）
        ///   外圈是 227 的网格灰底，而真复选框外圈是 255 的行白底 —— 这一条把假框全滤掉。
        /// </summary>
        private static List<int[]> FindBoxes(Shot s, int hi)
        {
            // 常规只扫右半区（左半区是「源文件」长路径文字，笔画会拼出假框）；
            // 右半区一个都没有时才整幅扫一遍 —— 列顺序/列宽变了也不至于全盘失败。
            var list = ScanBoxes(s, hi, s.W / 2);
            if (list.Count == 0) list = ScanBoxes(s, hi, 0);
            return list;
        }

        private static List<int[]> ScanBoxes(Shot s, int hi, int x0)
        {
            var list = new List<int[]>();
            int edgeMax = hi - 10;
            for (int y = 2; y < s.H - MinSide; y++)
            {
                int x = x0;
                while (x < s.W - MinSide)
                {
                    if (s.At(x, y) > edgeMax) { x++; continue; }
                    int run = 0;
                    while (x + run < s.W && s.At(x + run, y) <= edgeMax) run++;
                    if (run < MinSide || run > MaxSide) { x += Math.Max(1, run); continue; }

                    for (int hh = MinSide; hh <= MaxSide && y + hh < s.H; hh++)
                    {
                        if (!FitsBox(s, x, y, run, hh, hi)) continue;
                        bool dup = false;
                        foreach (int[] b in list)
                            if (Math.Abs(b[0] - x) <= 6 && Math.Abs(b[1] - y) <= 6) { dup = true; break; }
                        if (!dup) list.Add(new int[] { x, y, x + run, y + hh });
                        break;                              // 一个左上角只认一个高度
                    }
                    x += Math.Max(1, run);                  // 跳过整条上边框
                }
            }
            return list;
        }

        private const int MinSide = 10;      // 方框最小边长（DPI 100% 时会缩到 13 左右，别卡 14）
        private const int MaxSide = 40;

        /// <summary>四条边基本是边框色、内部以亮底为主、**外圈也是亮底** ⇒ 是复选框。</summary>
        private static bool FitsBox(Shot s, int x, int y, int w, int h, int hi)
        {
            if (Math.Abs(w - h) > 3) return false;       // 近似正方

            int edgeMax = hi - 10;
            int fillMin = hi - 14;

            for (int i = 0; i < w; i++)
                if (s.At(x + i, y + h - 1) > edgeMax) return false;   // 底边连续
            int l = 0, r = 0;
            for (int i = 0; i < h; i++)
            {
                if (s.At(x, y + i) <= edgeMax) l++;
                if (s.At(x + w - 1, y + i) <= edgeMax) r++;
            }
            if (l < h - 1 || r < h - 1) return false;

            int pad = Math.Max(3, Math.Min(5, w / 4));
            int cnt = 0, fill = 0;
            for (int yy = y + pad; yy < y + h - pad; yy++)
                for (int xx = x + pad; xx < x + w - pad; xx++)
                {
                    cnt++;
                    if (s.At(xx, yy) >= fillMin) fill++;
                }
            if (cnt < 16 || fill * 100 < cnt * 45) return false;

            int ring = 0, ringN = 0;
            for (int d = 2; d <= 4; d++)
            {
                for (int xx = x - 2; xx < x + w + 2; xx++)
                {
                    ringN += 2;
                    if (InFill(s, xx, y + h - 1 + d, fillMin)) ring++;
                    if (InFill(s, xx, y - d, fillMin)) ring++;
                }
                for (int yy = y - 2; yy < y + h + 2; yy++)
                {
                    ringN += 2;
                    if (InFill(s, x - d, yy, fillMin)) ring++;
                    if (InFill(s, x + w - 1 + d, yy, fillMin)) ring++;
                }
            }
            return ringN > 0 && ring * 100 >= ringN * 70;
        }

        /// <summary>越界按"亮底"处理（方框外缘被客户区裁掉一点不算假框）。</summary>
        private static bool InFill(Shot s, int x, int y, int min)
        {
            if (x < 0 || y < 0 || x >= s.W || y >= s.H) return true;
            return s.At(x, y) >= min;
        }

        /// <summary>画面亮部基准：亮度直方图的 P85（浅色主题下通常 255，即单元格白底）。</summary>
        private static int BackLum(Shot s)
        {
            var hist = new int[256];
            for (int i = 0; i < s.Gray.Length; i++) hist[s.Gray[i]]++;
            int total = s.Gray.Length, acc = 0;
            for (int v = 0; v < 256; v++)
            {
                acc += hist[v];
                if ((long)acc * 100 >= (long)total * 85) return v;
            }
            return 255;
        }

        /// <summary>一帧画面的诊断一句话（失败时写进日志，省得再问用户要截图）。</summary>
        private static string Describe(Shot s, int hi)
        {
            int edge = 0, dark = 0;
            int edgeMax = hi - 10, darkMax = hi - 60;
            for (int i = 0; i < s.Gray.Length; i++)
            {
                int v = s.Gray[i];
                if (v <= edgeMax) edge++;
                if (v <= darkMax) dark++;
            }
            int n = Math.Max(1, s.Gray.Length);
            return "背景亮度=" + hi + " 边框色px=" + edge + "(" + (edge * 100 / n) + "%) 深色px=" + dark +
                   "(" + (dark * 100 / n) + "%)";
        }

        // ============================================================ 点按钮

        /// <summary>按文字点按钮（确定 / 取消）。返回是否真的投出去了。</summary>
        private static bool ClickButton(IntPtr dlg, string label)
        {
            IntPtr btn = FindButton(dlg, label);
            if (btn == IntPtr.Zero) return false;
            // PostMessage 而不是 SendMessage：点完确定 CAXA 会同步跑明细更新，
            // 同步等返回会把哨兵线程卡在模态循环里（参考工程实机踩过）。
            return Post(btn, BM_CLICK);
        }

        /// <summary>没有按钮文字时的兜底：WM_COMMAND/IDOK 或 IDCANCEL。</summary>
        private static void ClickOkById(IntPtr dlg, bool cancel)
        {
            Post(dlg, WM_COMMAND, (IntPtr)(cancel ? IDCANCEL : IDOK), IntPtr.Zero);
            Diagnostics.Log("dlgbom: 没找到「" + (cancel ? "取消" : "确定") + "」按钮，已改投 WM_COMMAND/" +
                            (cancel ? "IDCANCEL" : "IDOK"));
        }

        /// <summary>找文字以 label 开头的 Button（跳过「取消&」这类误命中：只按前缀 + 长度限制）。</summary>
        private static IntPtr FindButton(IntPtr dlg, string label)
        {
            IntPtr hit = IntPtr.Zero;
            EnumChildWindows(dlg, delegate (IntPtr h, IntPtr l)
            {
                if (!ClassIs(h, "Button")) return true;
                string raw = TextOf(h);
                if (raw.Length == 0 || raw.Length > 24) return true;
                string norm = raw.Replace("&", "").Trim();
                if (norm.StartsWith(label, StringComparison.Ordinal))
                {
                    hit = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return hit;
        }

        // ============================================================ 取证

        /// <summary>把对话框的控件树打进日志（class / ctrl id / 文字 / 矩形）。失败不影响主流程。</summary>
        private static void DumpTree(IntPtr dlg)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("dlgbom 控件树: ");
                int n = 0;
                EnumChildWindows(dlg, delegate (IntPtr h, IntPtr l)
                {
                    if (n >= 40) return true;
                    n++;
                    int id = GetDlgCtrlID(h);
                    RECT r;
                    GetWindowRect(h, out r);
                    sb.Append("\r\n  · class=").Append(ClassOf(h))
                      .Append(" id=").Append(id)
                      .Append(" text=[").Append(TextOf(h)).Append("]")
                      .Append(" rect=").Append(r.Left).Append(",").Append(r.Top)
                      .Append(" ").Append(r.Right - r.Left).Append("x").Append(r.Bottom - r.Top)
                      .Append(" vis=").Append(IsWindowVisible(h) ? 1 : 0)
                      .Append(" enabled=").Append(IsWindowEnabled(h) ? 1 : 0);
                    return true;
                }, IntPtr.Zero);
                Diagnostics.Log(sb.ToString());
            }
            catch (Exception ex) { Diagnostics.Log("dlgbom: 控件树取证失败 " + ex.Message); }
        }

        /// <summary>
        /// 失败现场留证：把最后一帧网格画面**以 ASCII 缩略图写进日志**，并把原图存成
        /// `%TEMP%\caxa3dcodenameextraction_grid.bmp`。这样"没找到复选框"到底是画面空白、
        /// 还是方框长得不一样，看日志就能判，不用再让用户手动截图。
        /// </summary>
        private static void DumpGrid(Shot s, int shots)
        {
            if (s == null) { Diagnostics.Log("dlgbom 网格取证: 一帧都没抓到（Grab 全部失败）"); return; }
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "caxa3dcodenameextraction_grid.bmp");
                WriteBmpGray(s, path);

                int bx = Math.Max(2, s.W / 80);
                string ramp = " .:-=+*#%@";
                var sb = new StringBuilder();
                sb.Append("dlgbom 网格灰度图 ").Append(s.W).Append("x").Append(s.H)
                  .Append("（共抓 ").Append(shots).Append(" 帧；每 ").Append(bx).Append("x").Append(bx)
                  .Append(" 块取最暗；原图已存 ").Append(path).Append("）:");
                for (int y = 0; y < s.H; y += bx)
                {
                    sb.Append("\r\n  ");
                    for (int x = 0; x < s.W; x += bx)
                    {
                        int mn = 255;
                        int ye = Math.Min(y + bx, s.H), xe = Math.Min(x + bx, s.W);
                        for (int yy = y; yy < ye; yy++)
                            for (int xx = x; xx < xe; xx++)
                                if (s.At(xx, yy) < mn) mn = s.At(xx, yy);
                        sb.Append(ramp[(255 - mn) * (ramp.Length - 1) / 255]);
                    }
                }
                Diagnostics.Log(sb.ToString());
            }
            catch (Exception ex) { Diagnostics.Log("dlgbom: 网格取证失败 " + ex.Message); }
        }

        /// <summary>灰度图存 8bpp BMP（自己写头，不引 System.Drawing —— 宿主里 GDI+ 状态不可控）。</summary>
        private static void WriteBmpGray(Shot s, string path)
        {
            int stride = ((s.W + 3) / 4) * 4;
            int imgSize = stride * s.H;
            int offBits = 14 + 40 + 256 * 4;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((byte)'B'); bw.Write((byte)'M');
                bw.Write(offBits + imgSize);
                bw.Write((short)0); bw.Write((short)0);
                bw.Write(offBits);
                bw.Write(40); bw.Write(s.W); bw.Write(s.H);
                bw.Write((short)1); bw.Write((short)8);
                bw.Write(0); bw.Write(imgSize);
                bw.Write(2835); bw.Write(2835);
                bw.Write(256); bw.Write(0);
                for (int i = 0; i < 256; i++) { bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)i); bw.Write((byte)0); }
                for (int y = s.H - 1; y >= 0; y--)                 // BMP 行序自下而上
                {
                    for (int x = 0; x < s.W; x++) bw.Write(s.Gray[y * s.W + x]);
                    for (int p = s.W; p < stride; p++) bw.Write((byte)0);
                }
            }
        }

        private static void DumpMsaa(IntPtr dlg)
        {
            try
            {
                var list = MsaaNames(dlg, 40);
                if (list.Count == 0) { Diagnostics.Log("dlgbom MSAA: 取不到或没有条目"); return; }
                Diagnostics.Log("dlgbom MSAA 条目(" + list.Count + "):\r\n  " + string.Join("\r\n  ", list.ToArray()));
            }
            catch (Exception ex) { Diagnostics.Log("dlgbom: MSAA 取证失败 " + ex.Message); }
        }

        // ============================================================ MSAA（兜底通道）

        private const int CHILDID_SELF = 0;
        private const int RoleSystemCheckButton = 44;    // ROLE_SYSTEM_CHECKBUTTON
        private const int RoleSystemCell = 29;           // ROLE_SYSTEM_CELL（表格单元格）
        private const int StateSystemChecked = 0x10;
        private const uint OBJID_CLIENT = 0xFFFFFFFC;

        private sealed class MsaaHit
        {
            public IAccessible Acc;
            public object Child;
            public string Name = "";
            public int Role = -1;
            public bool Checked;
        }

        /// <summary>
        /// MSAA（oleacc）。照抄 caxa3dcodenameextraction 的两条铁律：
        ///   ① IID 必须是 {618736E0-3C3D-11CF-810C-00AA00389B71}（不是 …36F0）；
        ///   ② 接口声明 InterfaceIsIUnknown + 用 4 个占位顶掉 IDispatch 槽位 3~6，
        ///      否则 CLR 会按名字走 IDispatch::Invoke → DISP_E_UNKNOWNNAME。
        /// </summary>
        [ComImport, ComVisible(false)]
        [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAccessible
        {
            [PreserveSig] int _GetTypeInfoCount(out uint count);
            [PreserveSig] int _GetTypeInfo();
            [PreserveSig] int _GetIDsOfNames();
            [PreserveSig] int _Invoke();

            [PreserveSig] int get_accParent(out IAccessible parent);
            [PreserveSig] int get_accChildCount(out int count);
            [PreserveSig] int get_accChild([In, MarshalAs(UnmanagedType.Struct)] object child,
                [Out, MarshalAs(UnmanagedType.IDispatch)] out object accChild);
            [PreserveSig] int get_accName([In, MarshalAs(UnmanagedType.Struct)] object child,
                [MarshalAs(UnmanagedType.BStr)] out string name);
            [PreserveSig] int get_accValue([In, MarshalAs(UnmanagedType.Struct)] object child,
                [MarshalAs(UnmanagedType.BStr)] out string value);
            [PreserveSig] int get_accDescription([In, MarshalAs(UnmanagedType.Struct)] object child,
                [MarshalAs(UnmanagedType.BStr)] out string description);
            [PreserveSig] int get_accRole([In, MarshalAs(UnmanagedType.Struct)] object child,
                [Out, MarshalAs(UnmanagedType.Struct)] out object role);
            [PreserveSig] int get_accState([In, MarshalAs(UnmanagedType.Struct)] object child,
                [Out, MarshalAs(UnmanagedType.Struct)] out object state);
            [PreserveSig] int get_accHelp([In, MarshalAs(UnmanagedType.Struct)] object child,
                [MarshalAs(UnmanagedType.BStr)] out string help);
            [PreserveSig] int get_accHelpTopic([MarshalAs(UnmanagedType.BStr)] out string helpFile,
                [In, MarshalAs(UnmanagedType.Struct)] object child, out int helpContext);
            [PreserveSig] int get_accKeyboardShortcut([In, MarshalAs(UnmanagedType.Struct)] object child,
                [MarshalAs(UnmanagedType.BStr)] out string shortcut);
            [PreserveSig] int get_accFocus([Out, MarshalAs(UnmanagedType.Struct)] out object child);
            [PreserveSig] int get_accSelection([Out, MarshalAs(UnmanagedType.Struct)] out object children);
            [PreserveSig] int get_accDefaultAction([In, MarshalAs(UnmanagedType.Struct)] object child,
                [MarshalAs(UnmanagedType.BStr)] out string action);
            [PreserveSig] int accSelect(int flags, [In, MarshalAs(UnmanagedType.Struct)] object child);
            [PreserveSig] int accLocation(out int left, out int top, out int width, out int height,
                [In, MarshalAs(UnmanagedType.Struct)] object child);
            [PreserveSig] int accNavigate(int dir, [In, MarshalAs(UnmanagedType.Struct)] object child,
                [Out, MarshalAs(UnmanagedType.Struct)] out object endAt);
            [PreserveSig] int accHitTest(int x, int y, [Out, MarshalAs(UnmanagedType.Struct)] out object child);
            [PreserveSig] int accDoDefaultAction([In, MarshalAs(UnmanagedType.Struct)] object child);
            [PreserveSig] int put_accName([In, MarshalAs(UnmanagedType.Struct)] object child, string name);
            [PreserveSig] int put_accValue([In, MarshalAs(UnmanagedType.Struct)] object child, string value);
        }

        [DllImport("oleacc.dll", ExactSpelling = true)]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleChildren(IAccessible container, int start, int count,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children,
            out int obtained);

        private static IAccessible MsaaRoot(IntPtr hwnd)
        {
            Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            object o;
            try
            {
                if (AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out o) != 0 || o == null)
                    return null;
                return o as IAccessible;
            }
            catch { return null; }
        }

        /// <summary>在对话框子树里找"名字含 needles 且是复选按钮（或状态带 checked 位）"的条目。</summary>
        private static MsaaHit MsaaFindCheck(IntPtr dlg, string needle)
        {
            MsaaHit found = null;
            Walk(MsaaRoot(dlg), 0, ref found, needle, new int[] { 600 });
            return found;
        }

        private static void Walk(IAccessible acc, int depth, ref MsaaHit found, string needle, int[] budget)
        {
            if (acc == null || found != null || depth > 8 || budget[0] <= 0) return;
            int count;
            if (acc.get_accChildCount(out count) != 0 || count <= 0) return;
            int take = Math.Min(count, 60);
            object[] kids = new object[take];
            int got;
            if (AccessibleChildren(acc, 0, take, kids, out got) != 0) return;

            for (int i = 0; i < got && budget[0] > 0 && found == null; i++)
            {
                budget[0]--;
                object k = kids[i];
                if (k == null) continue;
                var childAcc = k as IAccessible;
                object childId = childAcc != null ? (object)CHILDID_SELF : k;
                IAccessible use = childAcc != null ? childAcc : acc;

                string name;
                if (use.get_accName(childId, out name) != 0 || name == null) name = "";
                int role = -1;
                object ro;
                if (use.get_accRole(childId, out ro) == 0 && ro != null)
                {
                    int r;
                    if (int.TryParse(Convert.ToString(ro), out r)) role = r;
                }
                int state = 0;
                object st;
                if (use.get_accState(childId, out st) == 0 && st != null)
                {
                    int s;
                    if (int.TryParse(Convert.ToString(st), out s)) state = s;
                }

                bool nameHit = name.Length > 0 && name.IndexOf(needle, StringComparison.Ordinal) >= 0;
                // 认"能勾的条目"：复选按钮(44) / 表格单元格(29) / 状态里带 checked 位的条目。
                // 显式排除列头（role 25，名字也叫「更新」）—— 点列头等于排序，不是勾选。
                if (nameHit && (role == RoleSystemCheckButton || role == RoleSystemCell ||
                                (state & StateSystemChecked) != 0))
                {
                    found = new MsaaHit
                    {
                        Acc = use,
                        Child = childId,
                        Name = name,
                        Role = role,
                        Checked = (state & StateSystemChecked) != 0
                    };
                    return;
                }

                if (childAcc != null) Walk(childAcc, depth + 1, ref found, needle, budget);
            }
        }

        private static void MsaaDoDefault(MsaaHit hit)
        {
            try { hit.Acc.accDoDefaultAction(hit.Child); }
            catch (Exception ex) { Diagnostics.Log("dlgbom: accDoDefaultAction 异常 " + ex.Message); }
        }

        private static List<string> MsaaNames(IntPtr dlg, int max)
        {
            var outp = new List<string>();
            var root = MsaaRoot(dlg);
            if (root == null) return outp;
            CollectNames(root, 0, outp, max, new int[] { 800 });
            return outp;
        }

        private static void CollectNames(IAccessible acc, int depth, List<string> outp, int max, int[] budget)
        {
            if (acc == null || depth > 8 || budget[0] <= 0 || outp.Count >= max) return;
            int count;
            if (acc.get_accChildCount(out count) != 0 || count <= 0) return;
            int take = Math.Min(count, 60);
            object[] kids = new object[take];
            int got;
            if (AccessibleChildren(acc, 0, take, kids, out got) != 0) return;
            for (int i = 0; i < got && budget[0] > 0 && outp.Count < max; i++)
            {
                budget[0]--;
                object k = kids[i];
                if (k == null) continue;
                var childAcc = k as IAccessible;
                object childId = childAcc != null ? (object)CHILDID_SELF : k;
                IAccessible use = childAcc != null ? childAcc : acc;
                string name;
                if (use.get_accName(childId, out name) == 0 && !string.IsNullOrEmpty(name))
                {
                    object ro; int role = -1;
                    if (use.get_accRole(childId, out ro) == 0 && ro != null)
                    {
                        int r; if (int.TryParse(Convert.ToString(ro), out r)) role = r;
                    }
                    object st; int state = 0;
                    if (use.get_accState(childId, out st) == 0 && st != null)
                    {
                        int s; if (int.TryParse(Convert.ToString(st), out s)) state = s;
                    }
                    outp.Add(new string(' ', depth * 2) + name + " role=" + role +
                             " state=0x" + state.ToString("X"));
                }
                if (childAcc != null) CollectNames(childAcc, depth + 1, outp, max, budget);
            }
        }

        // ============================================================ 控件查找

        private static IntPtr FindDescendantByClass(IntPtr parent, string cls)
        {
            IntPtr hit = IntPtr.Zero;
            EnumChildWindows(parent, delegate (IntPtr h, IntPtr l)
            {
                if (ClassIs(h, cls)) { hit = h; return false; }
                return true;
            }, IntPtr.Zero);
            return hit;
        }

        // ============================================================ P/Invoke

        private const uint LVM_GETITEMCOUNT = 0x1004;
        private const uint LVM_GETITEMSTATE = 0x102C;
        private const uint LVM_SETITEMSTATE = 0x102B;
        private const uint LVIS_FOCUSED = 0x0001;
        private const uint LVIS_SELECTED = 0x0002;
        private const uint LVIS_STATEIMAGEMASK = 0xF000;
        private const uint BM_CLICK = 0x00F5;
        private const uint BM_GETCHECK = 0x00F0;
        private const int BST_CHECKED = 1;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_COMMAND = 0x0111;
        private const uint MK_LBUTTON = 0x0001;
        private const int VK_SPACE = 0x20;
        private const int IDOK = 1;
        private const int IDCANCEL = 2;

        // 按钮样式位（找"真复选按钮"用；分组框 BS_GROUPBOX=0x0007 必须排除）
        private const int GWL_STYLE = -16;
        private const long BS_TYPEMASK = 0x0000000FL;
        private const long BS_CHECKBOX = 0x00000002L;
        private const long BS_AUTOCHECKBOX = 0x00000003L;
        private const long BS_3STATE = 0x00000005L;
        private const long BS_AUTO3STATE = 0x00000006L;

        // 截屏 / 光标
        private const uint SRCCOPY = 0x00CC0020;
        private const uint CAPTUREBLT = 0x40000000;
        private const uint PW_CLIENTONLY = 0x00000001;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;    // Win8.1+：走 DWM 合成内容，被遮挡也能抓
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        private static uint INDEXTOSTATEIMAGEMASK(int i) { return (uint)(i << 12); }

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEM
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public uint cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        /// <summary>GetDIBits 要的位图信息头 + 1 个调色板占位（BI_RGB/32bpp 不用调色板，但结构体不能少这一项）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public int bmiColors;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr p, EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr extra);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h,
            IntPtr s, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start,
            uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
        // user32 只导出 SendMessageW/A，必须显式写 EntryPoint
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr Send(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

        private static bool Post(IntPtr h, uint msg)
        {
            return PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero);
        }

        private static bool Post(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            return PostMessage(h, msg, w, l);
        }

        /// <summary>LRESULT → int：x64 上高位为 1 直接 ToInt32() 会抛溢出（参考工程实机踩过）。</summary>
        private static int Lr(IntPtr v)
        {
            try { return unchecked((int)v.ToInt64()); } catch { return 0; }
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static bool ClassIs(IntPtr h, string cls)
        {
            return string.Equals(ClassOf(h), cls, StringComparison.OrdinalIgnoreCase);
        }

        private static string TextOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
