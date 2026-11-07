// 图纸环境弹窗代点器（11-06 用户指令 4「刷新工程图」的「处理弹窗」那一步）。
// 同构基准 = caxa3dmytoolsaddin\plugins\caxa3dCXDFTViewUpdate\PopupAutoYes.cs（实机跑过的那份），
// 本项目相对参考多加了一条：**排除自家窗口**（本插件有 WinForms 弹窗，也有确认用 MessageBox；
// MessageBox 的类名正是 #32770，不排除就会自己点自己）。参考里没有这条，不是它不需要，是它当时没有自家 MessageBox。
//
// 为什么必须后台线程（规则第 9/20 节、坑 152）：宿主弹的是模态框，MFC 主线程被它自己的消息泵占住，
//   主线程轮询「有没有框」结构性不返回 ⇒ 代点只能跑在独立线程上（嵌套消息泵占住主线程时它照样能干活）。
// 节拍 120 ms 是延迟地板：一次命中＝只点一次（seen 集合去重），点不到的框最多记一行日志。
// 答案口径：图纸链上的框语义都是「继续/接受」，一律找 是/确定/Yes/OK/全是；
//   改名链不用这个（那条的答案只能是「取消」，见 RenamePopupSentry）——两套答案不许合并成一个通用代点器。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    internal static class PopupAutoYes
    {
        private static volatile bool _armed;
        private static Thread _worker;
        private static int _clicked;
        private static int _seenDialogs;
        private static Action<string> _sink = delegate { };
        private static HashSet<IntPtr> _ownTop;      // Arm 时在 UI 线程采集的自家顶层窗口句柄快照
        private static readonly object Lock = new object();

        private const int MaxArmMs = 10 * 60 * 1000;   // 布防硬上限：调用方忘撤防也不会留永久僵尸线程
        private static readonly string[] AllowedProcessNames = { "IronCAD", "CAXADraft" };

        public static int ClickedCount { get { lock (Lock) return _clicked; } }
        public static int SeenCount { get { lock (Lock) return _seenDialogs; } }

        /// <summary>探针（ChangeLinksProbe）专用的「打开」文件框代填：流程把正确 ics 全路径放这里，
        /// 代点器发现「打开」框且本字段非空，就填进去并点「打开」；用后即清，避免误填别人的「打开」框。</summary>
        public static volatile string FileDialogTarget;

        /// <summary>「请更新3D文件位置」的后续布防（**整步有效**，不是一次性）：exb 改名/搬走后内部 3D 链接仍指旧 ics，
        /// CAXA 打开图纸时弹这张框；点「是」之后**紧接着**再弹一张「打开」要人指新 ics。
        /// 流程把新 ics 全路径放这里，点「是」时转进 <see cref="FileDialogTarget"/>，
        /// 下一张真文件框自动填并点「打开」；本字段整步不消费、由流程 finally 清空。
        /// 不布防就是 11-07 09:23 实机的样子：点是 → 那张「打开」被当无关文件框取消 → 链接永远没更新上。</summary>
        public static volatile string Locate3dAfterPrompt;

        /// <summary>总代点次数（含「是/确定」「打开」「取消」），ChangeLinksProbe 借此判断「打开」框是否被吃掉了。</summary>
        public static int ClickCount { get { lock (Lock) return _clicked; } }

        /// <summary>当前可见的模态 #32770 弹窗数（白名单进程，排除本插件自家窗口）。
        /// SheetMetalOps 的「等图纸就绪」用 ModalCount==0 判无模态框。</summary>
        private static int _modalCount;
        public static int ModalCount { get { return Volatile.Read(ref _modalCount); } }

        /// <summary>挂起：true 时后台线程不代点其它进程的弹窗（图纸已打开、.ics 流程未完成阶段用），
        /// 留给后续阶段统一处理；本插件自家窗口照常。机械照搬参考工程 PopupAutoYes.Suspend 语义。</summary>
        public static volatile bool Suspend;

        /// <summary>探针在后台线程点掉一个框时调它，让 ClickCount 与 _clicked 同步累加（与通用代点共用同一计数器）。</summary>
        public static void CountExternalClick() { lock (Lock) { _clicked++; } }

        /// <summary>已处理的「打开」文件框句柄集合：避免同一框被重复填/取消（点完到真正关闭之间还会被扫到）。</summary>
        private static readonly HashSet<IntPtr> _fileHandled = new HashSet<IntPtr>();

        /// <summary>清掉已销毁窗口的记录（句柄会被下一行的框复用，不清就等于把新框当旧框跳过）。</summary>
        private static void PruneDeadFileHandled()
        {
            if (_fileHandled.Count == 0) return;
            var dead = new List<IntPtr>();
            foreach (IntPtr h in _fileHandled) { if (!IsWindow(h)) dead.Add(h); }
            for (int i = 0; i < dead.Count; i++) _fileHandled.Remove(dead[i]);
        }

        /// <summary>布防：后台线程每 120 ms 扫一次可见 #32770，命中就代点「是/确定」。重复 Arm 只换日志出口，不起第二个线程。
        /// 计数器是**进程级累计、Arm 不清零**：调用方一律用「先取基准、再取差值」的方式统计本段命中数，
        /// 否则一次流程里多次布防会把前面的证据抹掉。</summary>
        public static void Arm(Action<string> log)
        {
            if (log != null) _sink = log;
            var own = new HashSet<IntPtr>();
            try
            {
                foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms)
                {
                    if (f != null && f.IsHandleCreated) own.Add(f.Handle);
                }
            }
            catch (Exception ex) { Diagnostics.Trace("PopupAutoYes: 采集自家窗口失败（按无自家窗口继续）：" + ex.Message); }
            _ownTop = own;

            if (_armed) return;
            _armed = true;
            _worker = new Thread(Loop);
            _worker.IsBackground = true;
            _worker.Name = "caxa3dBatchMaster.PopupAutoYes";
            _worker.Start();
            _sink("popup: 代点器已布防（120 ms 节拍，只动白名单进程的 #32770，自家窗口除外）");
        }

        public static void Disarm()
        {
            _armed = false;
            _worker = null;
            _ownTop = null;
        }

        /// <summary>作用域内布防代点器（参考工程 PopupAutoYes.Borrow 的简化可重入版）。
        /// 已布防则不再重复布防；只有「本次借入才布防」的那一层 Dispose 才撤防，
        /// 避免嵌套借入把外层布防误撤（DrawingChain 已在步骤外包了一次 Arm）。</summary>
        public static IDisposable Borrow(string why)
        {
            bool armedByMe = false;
            if (!_armed) { Arm(null); armedByMe = true; }
            return new BorrowToken(armedByMe);
        }
        private sealed class BorrowToken : IDisposable
        {
            private readonly bool _armedByMe;
            public BorrowToken(bool armedByMe) { _armedByMe = armedByMe; }
            public void Dispose() { if (_armedByMe) Disarm(); }
        }

        private static void Loop()
        {
            var seen = new HashSet<IntPtr>();
            var sw = Stopwatch.StartNew();
            while (_armed && sw.ElapsedMilliseconds < MaxArmMs)
            {
                try
                {
                    // ---- 探针派发：ChangeLinksProbe 驱动的两张框（MFC 自绘，窗口类未必是 #32770，按标题认）----
                    // 这两张框状态机必须由本后台线程每轮推进一次；它们不进通用代点，绝不会被点「确定/取消」。
                    if (ChangeLinksProbe.IsArmed)
                    {
                        var probeHits = new List<IntPtr>();
                        EnumWindows(delegate(IntPtr h, IntPtr l)
                        {
                            if (!IsWindowVisible(h)) return true;
                            string t = GetWndText(h);
                            if (ChangeLinksProbe.IsMine(t) || ChangeLinksProbe.IsFolderBrowse(t)) probeHits.Add(h);
                            return true;
                        }, IntPtr.Zero);
                        for (int i = 0; i < probeHits.Count; i++)
                        {
                            string t = GetWndText(probeHits[i]);
                            if (ChangeLinksProbe.IsMine(t)) ChangeLinksProbe.Tick(probeHits[i]);
                            else if (ChangeLinksProbe.IsFolderBrowse(t)) ChangeLinksProbe.TickFolderBrowse(probeHits[i]);
                        }
                    }

                    // ---- 文件框优先（探针布防期、已布防目标、或「请更新3D文件位置」整步布防期）----
                    // 同一 hwnd 可能被探针连续复用，不进 seen 去重
                    if (ChangeLinksProbe.IsArmed || FileDialogTarget != null || !string.IsNullOrEmpty(Locate3dAfterPrompt))
                    {
                        // 已处理记录只按「窗口还活着」去重：窗口销毁后句柄可能被下一行的框复用，
                        // 不清掉会把下一行的「打开」永远当成处理过（多行批量时断链）。
                        PruneDeadFileHandled();
                        IntPtr fd = FindFileOpenDialog();
                        if (fd != IntPtr.Zero && TryHandleFileDialog(fd, GetWndText(fd)))
                        {
                            Thread.Sleep(120);
                            continue;   // 这轮不再走通用代点，避免误点
                        }
                    }

                    // ---- 模态计数 + 标题栏「设置3D信息」探针驱动（每张框都扫一次）----
                    ScanModals();
                    try { Dlg3DInfo.Sweep(); } catch (Exception ex) { _sink("popup: Dlg3DInfo.Sweep 异常（继续）：" + ex.Message); }

                    // ---- 通用代点：找 是/确定/Yes/OK/全是 ----
                    IntPtr dlg = FindTargetDialog();
                    if (dlg != IntPtr.Zero && seen.Add(dlg))
                    {
                        string title = GetWndText(dlg);
                        // 「设置3D信息」是业务对话框（标题栏设默认用），交给 Dlg3DInfo 自动填表，不参与通用代点
                        if (Dlg3DInfo.IsMine(title)) { try { Dlg3DInfo.Tick(dlg); } catch (Exception ex) { _sink("popup: Dlg3DInfo.Tick 异常：" + ex.Message); } continue; }
                        lock (Lock) { _seenDialogs++; }
                        IntPtr yesBtn;
                        string hitText;
                        bool clicked = FindYesLike(dlg, out yesBtn, out hitText);
                        if (clicked)
                        {
                            string body = BodyText(dlg, 120);
                            // 「请更新3D文件位置」：点是后 CAXA 立刻弹「打开」要人指新 ics。
                            // 必须先布防 FileDialogTarget 再投点击。**布防保持整步有效**：同一个 exb 里
                            // 可能有多条链接都指旧 ics（11-07 第 2 轮 09:44:07 就连弹了第二张），
                            // 一次性消费只救第一张，第二张的「打开」就敞着把主线程钉 19 秒。
                            if (Locate3dAfterPrompt != null && body.IndexOf("请更新3D文件位置", StringComparison.Ordinal) >= 0)
                            {
                                FileDialogTarget = Locate3dAfterPrompt;
                                _sink("popup: 「请更新3D文件位置」已布防，随后的「打开」将指向「" + Locate3dAfterPrompt + "」");
                            }
                            else if (Locate3dAfterPrompt == null && body.IndexOf("请更新3D文件位置", StringComparison.Ordinal) >= 0)
                            {
                                _sink("popup: 「请更新3D文件位置」出现但本步没布防目标路径（链忘了 ArmLocate3d）＝"
                                    + "这张「打开」只能人工指，或者点「取消」");
                            }
                            // 点击一律 PostMessage：SendMessage 会在宿主模态泵里同步等待，
                            // 点完这张紧跟下一张时会把代点器自己卡死（参考工程实测结论，照抄）。
                            PostMessage(yesBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                            lock (Lock) { _clicked++; }
                            _sink("popup: 已答 [" + title + "] 按钮「" + hitText + "」 (hwnd=0x" +
                                dlg.ToInt64().ToString("X") + ") 正文＝「" + body + "」");
                        }
                        else
                        {
                            // 不 WM_CLOSE 兜底：图纸链上「关掉框」不等于「答是」，语义不同；只记日志，让流程自己走完
                            _sink("popup: 发现框 [" + title + "] (hwnd=0x" + dlg.ToInt64().ToString("X") +
                                ") 但没找到 是/确定/Yes/OK 按钮＝不代点（可能是要填内容的框，交给专用填写器）");
                        }
                    }
                }
                catch (Exception ex) { _sink("popup: 扫描异常（继续）：" + ex.Message); }
                Thread.Sleep(120);
            }
            if (sw.ElapsedMilliseconds >= MaxArmMs) _sink("popup: 代点器到达 " + MaxArmMs / 1000 + " 秒硬上限，自动收工（调用方没撤防）");
        }

        /// <summary>扫描一次当前可见的模态 #32770 弹窗数（白名单进程、排除本插件自家窗口），写入 _modalCount。
        /// 挂起期（Suspend=true）不计其它进程的弹窗，留给后续阶段统一处理。供 SheetMetalOps 的「等图纸就绪」判定。</summary>
        private static void ScanModals()
        {
            uint selfPid = (uint)Process.GetCurrentProcess().Id;
            int n = 0;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint wpid;
                GetWindowThreadProcessId(h, out wpid);
                if (wpid != selfPid && !IsAllowedProcess(wpid)) return true;
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString() != "#32770") return true;
                if (IsOwnWindow(h)) return true;
                if (Suspend && wpid != selfPid) return true;   // 挂起期：其它进程的弹窗留给后续阶段
                n++;
                return true;
            }, IntPtr.Zero);
            Volatile.Write(ref _modalCount, n);
        }

        /// <summary>找一个标题命中 ChangeLinksProbe.IsFileDialog 的「打开」文件框（#32770、白名单进程、非自家窗体）。</summary>
        private static IntPtr FindFileOpenDialog()
        {
            uint selfPid = (uint)Process.GetCurrentProcess().Id;
            IntPtr hit = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString() != "#32770" || !IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != selfPid && !IsAllowedProcess(pid)) return true;
                if (IsOwnWindow(h)) return true;
                if (ChangeLinksProbe.IsFileDialog(GetWndText(h))) { hit = h; return false; }
                return true;
            }, IntPtr.Zero);
            return hit;
        }

        /// <summary>「打开」文件框代填：探针布防了目标路径就填全路径+点「打开」；探针试探读值弹的「打开」就点「取消」。
        /// 返回 true 表示已处理（不要再走通用 是/确定 代点）。仅在 ChangeLinksProbe 布防期被调用。</summary>
        private static bool TryHandleFileDialog(IntPtr dlg, string title)
        {
            if (_fileHandled.Contains(dlg)) return false;   // 本框已处理过，等它关掉即可
            IntPtr openBtn = IntPtr.Zero, cancelBtn = IntPtr.Zero, editCtl = IntPtr.Zero;
            bool hasCombo = false;
            EnumChildWindows(dlg, delegate(IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                string cls = sb.ToString();
                if (cls == "Button")
                {
                    int len = (int)SendMessage(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                    if (len > 0 && len <= 32)
                    {
                        var tx = new StringBuilder(len + 2);
                        SendMessageText(h, WM_GETTEXT, (IntPtr)(len + 1), tx);
                        // 归一化同 NormalizeBtn：标准文件框按钮是「打开(O)」「取消」，
                        // 旧版只删括号留下 O ⇒ norm=「打开O」⇒ 永远填不进去（11-07 实机同类）。
                        string norm = NormalizeBtn(tx.ToString());
                        if (norm == "打开" || norm == "open") openBtn = h;
                        else if (norm == "取消" || norm == "cancel") cancelBtn = h;
                    }
                }
                else if (cls == "Edit" && editCtl == IntPtr.Zero) editCtl = h;
                else if (cls == "ComboBox") hasCombo = true;
                return true;
            }, IntPtr.Zero);

            // 目标路径两种来源：① 探针/流程显式放的 FileDialogTarget；② 回落到本步的
            // Locate3dAfterPrompt（开 exb 期间任何「打开」都是「请更新3D文件位置」后续那张，
            // 不管前面那张提示框正文写的是什么）。探针一旦布防就不许回落，否则会把探针试探读值的框填坏。
            string target = FileDialogTarget;
            bool fromArm = false;
            if (target == null && !ChangeLinksProbe.IsArmed && !string.IsNullOrEmpty(Locate3dAfterPrompt))
            {
                target = Locate3dAfterPrompt;
                fromArm = true;
            }
            if (target != null && openBtn != IntPtr.Zero && editCtl != IntPtr.Zero && hasCombo)
            {
                if (!fromArm) FileDialogTarget = null;   // 用完即清，别去填别人的「打开」
                SetWindowTextW(editCtl, target);
                PostMessage(openBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                lock (Lock) { _clicked++; }
                _fileHandled.Add(dlg);
                _sink("popup: 文件框已填「" + target + "」并点「打开」(hwnd=0x" + dlg.ToInt64().ToString("X") +
                    ")" + (fromArm ? "｜来源＝「请更新3D文件位置」整步布防" : ""));
                return true;
            }
            // 探针试探读值用的「打开」框：没有目标路径就取消，让 Tick 继续
            if (ChangeLinksProbe.IsFileDialog(title) && cancelBtn != IntPtr.Zero)
            {
                PostMessage(cancelBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                lock (Lock) { _clicked++; }
                _fileHandled.Add(dlg);
                _sink("popup: 取消探针试探读值用的「打开」框 (hwnd=0x" + dlg.ToInt64().ToString("X") + ")");
                return true;
            }
            return false;
        }

        // ---------------- Win32 ----------------

        private delegate bool EnumWndProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWndProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumWndProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] private static extern IntPtr SendMessageText(IntPtr hwnd, int msg, IntPtr wParam, StringBuilder lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")] private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

        private const int WM_GETTEXT = 0x000D;
        private const int WM_GETTEXTLENGTH = 0x000E;
        private const int BM_CLICK = 0x00F5;
        private const uint GW_OWNER = 4;

        /// <summary>可见 #32770 且进程在白名单内（本进程 or IronCAD/CAXADraft），且不是自家窗体/自家窗体的 owner 后代。</summary>
        private static IntPtr FindTargetDialog()
        {
            uint selfPid = (uint)Process.GetCurrentProcess().Id;
            IntPtr hit = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString() != "#32770" || !IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != selfPid && !IsAllowedProcess(pid)) return true;
                if (IsOwnWindow(h)) return true;
                // 「更新3D明细」是业务对话框：必须由 DlgUpdate3DBom 哨兵先勾「更新」再点确定。
                // 通用代点器若抢先点「确定」＝提交了一个没勾更新的空表，明细根本不会刷新。
                string bt = GetWndText(h);
                if (bt.IndexOf("更新", StringComparison.Ordinal) >= 0 &&
                    bt.IndexOf("明细", StringComparison.Ordinal) >= 0) return true;
                // 探针驱动的两张框（改变链接文件 / 浏览文件夹）与「打开」文件框不在通用代点范围：
                // 前者由 Loop 的探针派发走 ChangeLinksProbe.Tick，后者由 TryHandleFileDialog 填路径点打开/取消。
                if (ChangeLinksProbe.IsArmed)
                {
                    string pt = GetWndText(h);
                    if (ChangeLinksProbe.IsMine(pt) || ChangeLinksProbe.IsFolderBrowse(pt) || ChangeLinksProbe.IsFileDialog(pt)) return true;
                }
                hit = h;
                return false;
            }, IntPtr.Zero);
            return hit;
        }

        private static bool IsAllowedProcess(uint pid)
        {
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    string n = p.ProcessName;
                    foreach (string a in AllowedProcessNames)
                        if (string.Equals(n, a, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { /* 进程查不到＝不放行 */ }
            return false;
        }

        /// <summary>沿 GW_OWNER 链最多走 4 级：命中 Arm 时采集的自家顶层句柄就算自家框。</summary>
        private static bool IsOwnWindow(IntPtr hwnd)
        {
            HashSet<IntPtr> own = _ownTop;
            if (own == null || own.Count == 0) return false;
            IntPtr h = hwnd;
            for (int i = 0; i < 4 && h != IntPtr.Zero; i++)
            {
                if (own.Contains(h)) return true;
                h = GetWindow(h, GW_OWNER);
            }
            return false;
        }

        private static string GetWndText(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            return sb.ToString();
        }

        /// <summary>框内找「是/确定/Yes/OK/全是」按钮（**只找不点**：调用方要先布防 FileDialogTarget，再 PostMessage）。
        /// 归一化必须吃掉助记键括号：CAXA 的按钮实机是「是(Y)」「否(N)」（11-07 09:23 用户截图），
        /// 旧版只删 &amp; 与 ( ) 却留下 Y ⇒ norm=「是Y」⇒ 永远命中不了 ⇒ 弹窗堵死到流程卡住。</summary>
        private static bool FindYesLike(IntPtr dlg, out IntPtr btn, out string hitText)
        {
            IntPtr found = IntPtr.Zero;
            string foundText = "";
            EnumChildWindows(dlg, delegate(IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString().IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) return true;
                int len = (int)SendMessage(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                if (len <= 0 || len > 200) return true;
                var txt = new StringBuilder(len + 2);
                SendMessageText(h, WM_GETTEXT, (IntPtr)(len + 1), txt);
                string t = txt.ToString();
                if (!IsYesLike(t)) return true;
                found = h; foundText = t;
                return false;
            }, IntPtr.Zero);
            btn = found; hitText = foundText;
            return found != IntPtr.Zero;
        }

        /// <summary>按钮文字归一化：去 &amp;、去「(Y)/(y)/（Y）」这类助记键括号（含括号内容）、转小写。
        /// 例：是(Y)/是(&amp;Y)/是（Y）→「是」；打开(O) →「打开」。</summary>
        private static string NormalizeBtn(string t)
        {
            string s = (t ?? "").Replace("&", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[(（][^)）]*[)）]", "");
            return s.Trim().ToLowerInvariant();
        }

        private static bool IsYesLike(string raw)
        {
            string n = NormalizeBtn(raw);
            return n == "是" || n == "确定" || n == "全是" || n == "yes" || n == "ok" ||
                   n == "yestoall" || n == "all" ||
                   n.IndexOf("是到全部", StringComparison.Ordinal) >= 0;
        }

        /// <summary>框正文全文（正文控件是 Static/其他类，不是 Button）。</summary>
        private static string BodyText(IntPtr dlg, int max)
        {
            var all = new StringBuilder();
            EnumChildWindows(dlg, delegate(IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString().IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                int len = (int)SendMessage(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                if (len <= 0 || len > 1024) return true;
                var txt = new StringBuilder(len + 2);
                SendMessageText(h, WM_GETTEXT, (IntPtr)(len + 1), txt);
                string t = txt.ToString();
                if (t.Length > 0) all.Append(t).Append(' ');
                return all.Length <= max;
            }, IntPtr.Zero);
            string s = all.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        /// <summary>框正文里有没有这句。</summary>
        private static bool BodyHas(IntPtr dlg, string phrase)
        {
            return BodyText(dlg, 1024).IndexOf(phrase, StringComparison.Ordinal) >= 0;
        }
    }
}
