// 「另存为dwg」UI 代点（代点法）——2026-11-11 定案：
//   API 两路均已实机判死（SAVE_EXB saveAs 同路径 eOk 但 mtime 不变；EXPORT 兜底 saveAs 不写 exb 本体），
//   改走宿主自己的 UI：WM_COMMAND ID_FILE_SAVE / ID_FILE_SAVE_AS + 另存文件框控件代点。
// 跨进程安全口径（USER32 会封送）：WM_COMMAND / WM_GETTEXT / WM_SETTEXT / CB_GETCOUNT / CB_SETCURSEL / BM_CLICK。
// ★ 绝不用 CB_GETLBTEXT / LB_GETTEXT 跨进程读下拉项（缓冲区指针不封送，远端进程 AV）——
//   用「CB_SETCURSEL 逐项探测 + WM_GETTEXT 读选中文本」替代（dropdownlist 组合框选中文本即条目文本）。
// 命令路由实证：CRX SaveDocByCommand 用 WM_COMMAND 0xE103(ID_FILE_SAVE) 保存已落盘（与手动 Ctrl+S
//   同一条 MFC 命令路由）；另存文件框 = 0xE104(ID_FILE_SAVE_AS，与 Ctrl+Shift+S 同一条命令)。
// ★ 一律 PostMessage（异步）：SendMessage 会等 Draft 命令处理器返回——另存框的模态消息循环
//   要等对话框关掉才返回，同步发 = 自己把自己堵死。等待一律轮询副作用（mtime / 窗口出现）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dButton
{
    internal static class SaveAsDwgUi
    {
        private const uint WM_SETTEXT = 0x000C;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;
        private const uint WM_COMMAND = 0x0111;
        private const uint CB_GETCOUNT = 0x0146;
        private const uint CB_SETCURSEL = 0x014E;
        private const uint CB_SHOWDROPDOWN = 0x014F;
        private const int CBN_SELCHANGE = 1;
        private const int CBN_CLOSEUP = 8;
        private const int CBN_SELENDOK = 9;
        private const uint ID_FILE_SAVE = 0xE103;     // 保存（Ctrl+S 同一条 MFC 命令）
        private const uint ID_FILE_SAVE_AS = 0xE104;  // 另存文件框（Ctrl+Shift+S 同一条 MFC 命令）

        private const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_S = 0x53;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageSb(IntPtr h, uint msg, IntPtr w, StringBuilder l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageStr(IntPtr h, uint msg, IntPtr w, string l);
        [DllImport("user32.dll", EntryPoint = "GetTopWindow")]
        private static extern IntPtr GetTopWindow(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "GetWindow")]
        private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", EntryPoint = "FindWindowExW")]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr h);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "EnumChildWindows")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr l);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        private const uint GW_CHILD = 5, GW_HWNDNEXT = 2;

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TitleOf(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>WM_GETTEXT（跨进程封送安全）：普通控件=标题，dropdownlist 组合框=选中项文本。</summary>
        private static string TextViaMsg(IntPtr h)
        {
            int len = SendMessage(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            SendMessageSb(h, WM_GETTEXT, (IntPtr)(len + 1), sb);
            return sb.ToString();
        }

        /// <summary>对齐 UiWin.Normalize：去 &amp; 与空白、转小写。</summary>
        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '&' || c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>窗口标题命中目标文档：全名或去扩展名 stem 任一含即可（对齐 CRX TitleMatchesDoc）。</summary>
        private static bool TitleMatchesDoc(string title, string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return true;
            if (!string.IsNullOrEmpty(title) &&
                title.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            int dot = fileName.LastIndexOf('.');
            string stem = dot > 0 ? fileName.Substring(0, dot) : fileName;
            return !string.IsNullOrEmpty(stem) && !string.IsNullOrEmpty(title) &&
                   title.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IntPtr MdiClientOf(IntPtr top)
        {
            IntPtr mdi = FindWindowEx(top, IntPtr.Zero, "MDIClient", null);
            if (mdi != IntPtr.Zero) return mdi;
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(top, delegate(IntPtr h, IntPtr l)
            {
                if (ClassOf(h) == "MDIClient") { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// 定位 exb 图纸的 MDI 子窗口（移植 CRX SaveDocByCommand 定位段）。
        /// ★ 绝不按 pid 过滤：exb 在 Draft 进程(CDRAFT_M_IC)，与加载插件的 IronCAD 不同 pid；
        ///   匹配由 TitleMatchesDoc（全名/stem）精确把关。找不到返回 Zero。
        /// </summary>
        public static IntPtr FindExbMdiChild(string exbFullPath)
        {
            if (string.IsNullOrEmpty(exbFullPath)) return IntPtr.Zero;
            string fileName = System.IO.Path.GetFileName(exbFullPath);
            for (IntPtr top = GetTopWindow(IntPtr.Zero); top != IntPtr.Zero; top = GetWindow(top, GW_HWNDNEXT))
            {
                if (!IsWindowVisible(top)) continue;
                IntPtr mdi = MdiClientOf(top);
                if (mdi == IntPtr.Zero) continue;   // 主框架（无 MDI 结构）跳过，绝不误投
                for (IntPtr c = GetWindow(mdi, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT))
                {
                    string title = TitleOf(c);
                    if (TitleMatchesDoc(title, fileName)) return c;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 保存落盘：PostMessage(WM_COMMAND, ID_FILE_SAVE) 到图纸 MDI 子窗口（= 手动 Ctrl+S 同一条命令），
        /// 然后轮询 exb 的 LastWriteTime 变化。判成功只认落盘（mtime 真的变了），命令投递不算数。
        /// 等待期间顺手收掉可能出现的保存框（文档无路径时会弹另存为——本流程 exb 有路径，不该出现）。
        /// </summary>
        public static bool SaveViaCommand(IntPtr mdi, string exbFullPath, int timeoutMs, Action<string> log)
        {
            if (mdi == IntPtr.Zero || string.IsNullOrEmpty(exbFullPath)) return false;
            DateTime t0;
            try { t0 = System.IO.File.GetLastWriteTime(exbFullPath); }
            catch { t0 = DateTime.MinValue; }
            if (!PostMessage(mdi, WM_COMMAND, (IntPtr)ID_FILE_SAVE, IntPtr.Zero))
            {
                if (log != null) log("保存命令投递失败（PostMessage）");
                return false;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    DateTime t = System.IO.File.GetLastWriteTime(exbFullPath);
                    if (t != t0)
                    {
                        if (log != null) log("保存落盘 OK（mtime " + t0.ToString("HH:mm:ss.fff") +
                                             " → " + t.ToString("HH:mm:ss.fff") + "，" +
                                             (int)sw.ElapsedMilliseconds + "ms）");
                        return true;
                    }
                }
                catch { }
                Pump();
                Thread.Sleep(300);
            }
            if (log != null) log("保存未落盘（" + timeoutMs + "ms 内 mtime 无变化）");
            return false;
        }

        /// <summary>
        /// 等待期消息泵钩子：批量转换把本类方法跑在 CAXA UI 线程时挂 Application.DoEvents
        /// （Thread.Sleep 堵主线程会饿死宿主消息泵——历史闪退教训）；后台线程跑时保持 null。
        /// </summary>
        public static Action PumpDuringWait;

        private static void Pump()
        {
            var pump = PumpDuringWait;
            if (pump != null) { try { pump(); } catch { } }
        }

        /// 打开「另存文件」框：主通道 PostMessage(WM_COMMAND, ID_FILE_SAVE_AS)（= Ctrl+Shift+S 同一条命令）；
        /// 兜底 keybd_event Ctrl+Shift+S（frame 已前台的场合）。轮询标题含「另存文件/另存为」的对话框出现。
        /// ★ 堵路自救（2026-11-11 批量转换实机）：顶层装配图纸另存时 CAXA 会先弹「确认另存为」等
        ///   自家框，代点之后若仍有**非预期模态框**堵着，真正的另存框永远出不来。
        ///   处理：发现非预期 #32770 就记日志；同一张框挂满 8 秒（PopupAutoYes 早该吃掉它了还活着
        ///   = 代点覆盖不了）点「取消/否」关掉并重投 ID_FILE_SAVE_AS；全程绝不点「是/确定/保存」。
        /// </summary>
        public static IntPtr OpenSaveAsDialog(IntPtr mdi, IntPtr frame, int timeoutMs, Action<string> log)
        {
            if (mdi != IntPtr.Zero)
                PostMessage(mdi, WM_COMMAND, (IntPtr)ID_FILE_SAVE_AS, IntPtr.Zero);

            // ★ 冻结检测（2026-11-11 批量转换实机根因）：批量链路跑在 CAXA UI 线程上，Pump()=DoEvents
            //   会把刚投的 ID_FILE_SAVE_AS 在本线程派发出去——CAXA 对顶层装配图纸的另存为处理很重，
            //   期间弹的「确认另存为」等模态链会把 DoEvents 冻在模态循环里几十秒（本线程冻结期间
            //   本循环一次都跑不了，只有 PopupAutoYes 后台线程能点框解冻）。解冻回来一看就"超时"，
            //   而真正的另存框其实马上就要出来了 ⇒ 凡是迭代间隔出现 >3s 的跳变（=被冻结过），
            //   就把等待预算重新给满（最多续 3 次），并把冻结事实落日志。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long budget = timeoutMs;
            long lastIterMs = 0;
            int renewals = 0;
            const int MaxRenewals = 3;
            IntPtr reportedBlocker = IntPtr.Zero;
            long blockerSinceMs = -1;
            bool dismissed = false;
            while (true)
            {
                long nowMs = sw.ElapsedMilliseconds;
                long dt = nowMs - lastIterMs;
                lastIterMs = nowMs;
                if (dt > 3000)
                {
                    if (renewals < MaxRenewals)
                    {
                        renewals++;
                        budget = nowMs + timeoutMs;
                        if (log != null)
                            log("等待期泵消息被模态链冻结 " + (dt / 1000) + " 秒（CAXA 正在处理另存为：" +
                                "大图纸会先弹「确认另存为」再开另存框），等待预算已续期 " + renewals + "/" + MaxRenewals);
                    }
                    else if (log != null)
                    {
                        log("等待期再次被模态链冻结且续期已满（" + MaxRenewals + " 次），放弃本次另存框等待");
                        break;
                    }
                }
                if (nowMs >= budget) break;

                IntPtr dlg = FindSaveDialogOnce();
                if (dlg != IntPtr.Zero)
                {
                    if (log != null) log("另存文件框已出现（" + (int)nowMs + "ms）class=" +
                                         ClassOf(dlg) + " 标题=[" + TitleOf(dlg) + "]");
                    return dlg;
                }

                // 堵路检测：等待期出现的非预期模态框（既不是另存文件/另存为，也不是文档主窗口）
                var unexpected = UiWin.FindModalDialogs("另存文件", "另存为");
                IntPtr blocker = unexpected.Count > 0 ? unexpected[0] : IntPtr.Zero;
                if (blocker != IntPtr.Zero)
                {
                    if (blocker != reportedBlocker)
                    {
                        reportedBlocker = blocker;
                        blockerSinceMs = sw.ElapsedMilliseconds;
                        if (log != null) log("另存框等待期发现非预期模态框（可能堵住 ID_FILE_SAVE_AS）：" +
                                             "title=[" + TitleOf(blocker) + "] class=" + ClassOf(blocker));
                    }
                    else if (!dismissed && sw.ElapsedMilliseconds - blockerSinceMs > 8000)
                    {
                        // 同一张框挂满 8 秒还活着 = PopupAutoYes 覆盖不了 ⇒ 点「取消/否」关掉（零副作用原则）
                        var btn = UiWin.FindButtonInSubtree(blocker, new[] { "取消", "否(&N)", "否(N)", "否" });
                        if (btn != null)
                        {
                            UiWin.Click(btn, "关掉堵住另存框的非预期模态框（取消/否）");
                            dismissed = true;
                            if (log != null) log("已点掉堵路模态框，重投 ID_FILE_SAVE_AS 继续等另存框");
                            if (mdi != IntPtr.Zero)
                                PostMessage(mdi, WM_COMMAND, (IntPtr)ID_FILE_SAVE_AS, IntPtr.Zero);
                        }
                        else if (log != null)
                            log("堵路模态框上找不到「取消/否」按钮，不乱点（title=[" + TitleOf(blocker) + "]）");
                    }
                }
                else { reportedBlocker = IntPtr.Zero; blockerSinceMs = -1; }

                // 到一半还没出：试一次键盘兜底（须图纸窗口前台；SetForegroundWindow 可能被系统限制，尽力而为）
                if (frame != IntPtr.Zero && sw.ElapsedMilliseconds > 4000 && sw.ElapsedMilliseconds < 4500)
                {
                    try { SetForegroundWindow(frame); } catch { }
                    Thread.Sleep(300);
                    SendCtrlShiftS();
                    if (log != null) log("ID_FILE_SAVE_AS 未见效，已改发键盘 Ctrl+Shift+S 兜底");
                }
                Pump();
                Thread.Sleep(300);
            }

            if (log != null)
            {
                var sb = new StringBuilder();
                foreach (IntPtr h in UiWin.FindModalDialogs())
                    sb.Append(ClassOf(h)).Append("[").Append(TitleOf(h)).Append("] ");
                log("另存文件框未能打开（" + timeoutMs + "ms）；结束时白名单进程可见顶层 #32770：\r\n" +
                    (sb.Length == 0 ? "（无 —— 没有模态框堵路，命令可能没送达）" : sb.ToString()));
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 找一次另存文件框：标题含「另存文件」优先，「另存为」兜底（本进程 + CAXADraft 双进程口径）。
        /// ★「确认另存为」是 ID_FILE_SAVE_AS 的前置确认框（#32770，标题含"确认"）——
        ///   标题同样含「另存为」子串，把它当另存框返回，FillAndSave 会在确认框上找类型下拉必然失败。
        ///   由 PopupAutoYes（120ms 巡检）代点「是」后真正的另存框才会出现。
        /// </summary>
        public static IntPtr FindSaveDialogOnce()
        {
            IntPtr dlg = UiWin.FindTopByTitle("另存文件");
            if (dlg != IntPtr.Zero) return dlg;
            dlg = UiWin.FindTopByTitle("另存为");
            if (dlg != IntPtr.Zero && Norm(TitleOf(dlg)).Contains("确认")) return IntPtr.Zero;
            return dlg;
        }

        /// <summary>
        /// 填另存文件框并点保存（dwg/dxf 通用，outExt="dwg"/"dxf" 不带点）：
        /// ① 选保存类型（探测条目文本含 2004+outExt，找不到退而求含 outExt 条目）
        /// ② 文件名框填目标全路径（WM_SETTEXT）③ 点「保存」按钮。每步落日志，失败时窗口留在原地供人工检查。
        /// </summary>
        public static bool FillAndSave(IntPtr dlg, string outFullPath, string outExt, Action<string> log, string dwgVersion = "2007")
        {
            if (dlg == IntPtr.Zero || string.IsNullOrEmpty(outFullPath)) return false;
            string ext = (outExt ?? "dwg").TrimStart('.').ToLowerInvariant();
            UiWin.DumpWindow(dlg, "另存文件框");

            // ① 保存类型下拉：枚举 ComboBox 子控件，逐个探测条目
            // ★ 11-11 修：版本号不再硬编码 "2004"，改用调用方传入的 dwgVersion（批量 UI 下拉）
            var combos = new List<IntPtr>();
            EnumChildWindows(dlg, delegate(IntPtr h, IntPtr l)
            {
                if (ClassOf(h) == "ComboBox") combos.Add(h);
                return true;
            }, IntPtr.Zero);
            IntPtr typeCombo = IntPtr.Zero;
            int targetIdx = -1;
            string chosen = "";
            string probeTrace = "";
            foreach (IntPtr combo in combos)
            {
                int n = SendMessage(combo, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                int idx = -1; string hit = ""; string trace = "";
                for (int i = 0; i < n; i++)
                {
                    SendMessage(combo, CB_SETCURSEL, (IntPtr)i, IntPtr.Zero);
                    string t = TextViaMsg(combo);
                    if (t.Length > 0) trace += "[" + i + "]" + t + " ";
                    string ln = Norm(t);
                    if (idx < 0 && ln.Contains(dwgVersion) && ln.Contains(ext)) { idx = i; hit = t; break; }
                }
                if (trace.Length > 0) probeTrace += "combo0x" + combo.ToInt64().ToString("X") +
                                                    " n=" + n + "：" + trace + "\r\n";
                if (idx >= 0)
                {
                    typeCombo = combo; targetIdx = idx; chosen = hit; break;
                }
            }
            if (typeCombo == IntPtr.Zero)
            {
                // 退而求其次：任一条目含目标扩展名的组合框
                foreach (IntPtr combo in combos)
                {
                    int n = SendMessage(combo, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    for (int i = 0; i < n; i++)
                    {
                        SendMessage(combo, CB_SETCURSEL, (IntPtr)i, IntPtr.Zero);
                        string t = TextViaMsg(combo);
                        if (Norm(t).Contains(ext)) { typeCombo = combo; targetIdx = i; chosen = t; break; }
                    }
                    if (typeCombo != IntPtr.Zero) break;
                }
                if (typeCombo != IntPtr.Zero && log != null)
                    log("未找到「2004+" + ext + "」条目，退选含 " + ext + " 条目：" + chosen);
            }
            if (typeCombo == IntPtr.Zero)
            {
                if (log != null) log("保存类型下拉定位失败（combos=" + combos.Count + "）探测明细：\r\n" + probeTrace);
                return false;
            }
            // ★ 2026-11-11 定案（还愿 10:44 成功版）：消息通道三连发 + MSAA 双保险——
            //   10:45 那次成功就是这套组合；10:54 去掉消息通道后 MSAA 单独不提交过滤器。
            SendMessage(typeCombo, CB_SETCURSEL, (IntPtr)targetIdx, IntPtr.Zero);
            int cid = GetDlgCtrlID(typeCombo);
            PostMessage(dlg, WM_COMMAND, (IntPtr)(((CBN_SELCHANGE & 0xFFFF) << 16) | (cid & 0xFFFF)), typeCombo);
            PostMessage(dlg, WM_COMMAND, (IntPtr)(((CBN_SELENDOK & 0xFFFF) << 16) | (cid & 0xFFFF)), typeCombo);
            PostMessage(dlg, WM_COMMAND, (IntPtr)(((CBN_CLOSEUP & 0xFFFF) << 16) | (cid & 0xFFFF)), typeCombo);
            if (log != null) log("保存类型(消息通道) → [" + chosen + "]（idx=" + targetIdx + " id=" + cid + "）");

            // MSAA 通道：放下类型下拉，对目标条目 accDoDefaultAction（真实双击 → 过滤器提交）
            SelectTypeViaMsaa(dlg, typeCombo, chosen, ext, log);
            Thread.Sleep(400);   // 给对话框时间提交过滤器

            // ② 文件名框：第一个可见 Edit，填目标全路径
            IntPtr edit = UiWin.FirstEdit(dlg);
            if (edit == IntPtr.Zero)
            {
                if (log != null) log("文件名 Edit 定位失败");
                return false;
            }
            SendMessageStr(edit, WM_SETTEXT, IntPtr.Zero, outFullPath);
            if (log != null) log("文件名 → " + outFullPath + "（edit id=" + GetDlgCtrlID(edit) + "）");

            // ③ 保存按钮：候选精确命中（避开「保存类型」静态标签）
            UiCtrl saveBtn = UiWin.FindButtonInSubtree(dlg,
                new[] { "保存(&S)", "保存(S)", "保存" });
            if (saveBtn == null)
            {
                UiCtrl any = UiWin.FindInSubtree(dlg, "保存");
                if (any != null && any.Class != null &&
                    any.Class.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                    saveBtn = any;
            }
            if (saveBtn == null)
            {
                if (log != null) log("保存按钮定位失败");
                return false;
            }
            UiWin.Click(saveBtn, "另存文件框点「保存」（类型 " + chosen + "）");
            return true;
        }

        /// <summary>
        /// MSAA 选保存类型：把类型下拉放下（CB_SHOWDROPDOWN，消息本身跨进程安全），
        /// 在组合框（不行再退对话框根）的 MSAA 子树里找名字与目标条目完全一致（Normalize 后）
        /// 的节点，accDoDefaultAction 真实双击它 —— 走对话框自己的选择处理器，过滤器才会提交。
        /// 只代点、不按坐标丢鼠标（对齐项目铁律：MSAA 拿不到就放弃，绝不乱丢点）。
        /// </summary>
        private static bool SelectTypeViaMsaa(IntPtr dlg, IntPtr combo, string wantText, string ext, Action<string> log)
        {
            try
            {
                string want = Norm(wantText);
                SendMessage(combo, CB_SHOWDROPDOWN, (IntPtr)1, IntPtr.Zero);
                Thread.Sleep(300);

                MsaaNode pick = null, anyExt = null;
                foreach (IntPtr root in new IntPtr[] { combo, dlg })   // 先问组合框自己，不行退对话框根
                {
                    foreach (MsaaNode it in Msaa.AllIn(root, 4000))
                    {
                        string n = UiWin.Normalize(it.Name ?? "");
                        if (n.Length == 0) continue;
                        if (n.Contains(ext) && anyExt == null) anyExt = it;
                        if (want.Length > 0 && n == want) { pick = it; break; }
                    }
                    if (pick != null) break;
                }
                if (pick == null) pick = anyExt;   // 精确没中，退任意含目标扩展名条目（2018 也好过 exb）
                if (pick == null)
                {
                    SendMessage(combo, CB_SHOWDROPDOWN, IntPtr.Zero, IntPtr.Zero);
                    if (log != null) log("MSAA：类型下拉里没找到 " + ext + " 条目，放弃 MSAA 选择（只留消息通道）");
                    return false;
                }
                bool ok = Msaa.Click(pick, "另存文件框选保存类型（MSAA 真实双击条目）");
                Thread.Sleep(300);
                SendMessage(combo, CB_SHOWDROPDOWN, IntPtr.Zero, IntPtr.Zero);   // 还开着就收起
                if (ok && log != null) log("MSAA：已代选类型条目「" + pick.Name + "」（动作=[" + pick.Action + "]）");
                return ok;
            }
            catch (Exception ex)
            {
                try { SendMessage(combo, CB_SHOWDROPDOWN, IntPtr.Zero, IntPtr.Zero); } catch { }
                if (log != null) log("MSAA 选类型异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>文件有效产出：存在且 &gt;0。</summary>
        private static bool FileOk(string path)
        {
            try { var fi = new System.IO.FileInfo(path); return fi.Exists && fi.Length > 0; }
            catch { return false; }
        }

        /// <summary>
        /// 等另存产物，带「保存类型未提交」自动重试（2026-11-11 10:29 实机判据）。
        /// 判成功 = 目标 dwg 存在 且 另存文件框已关闭（10:30 的假阳性就是框还开着时把
        /// 人工保存的产物认成了自动化产物）。
        /// 失败标记 = 出现 outDwg+".exb"（exb 过滤器吃掉文件名 ⇒ 类型没提交）→ 立即重选重存。
        /// 每次重试：错误产物清掉 → 另存框不在就重投 ID_FILE_SAVE_AS → FillAndSave 全套重来。
        /// </summary>
        public static bool WaitProductWithRetry(IntPtr mdi, string outPath, string outExt, int perTryMs,
            int maxAttempts, Action<string> log)
        {
            string ext = (outExt ?? "dwg").TrimStart('.').ToLowerInvariant();
            string marker = outPath + ".exb";
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int lastLogged = 0;
                bool retry = false;
                while (sw.ElapsedMilliseconds < perTryMs)
                {
                    bool product = FileOk(outPath);
                    bool dialogOpen = FindSaveDialogOnce() != IntPtr.Zero;
                    if (product && !dialogOpen)
                    {
                        if (log != null) log(ext + " 已产出且另存框已关闭（第 " + attempt + " 次尝试，" +
                                             (int)sw.ElapsedMilliseconds + "ms）→ " + outPath);
                        return true;
                    }
                    if (FileOk(marker))
                    {
                        if (log != null) log("出现 " + System.IO.Path.GetFileName(marker) +
                                             " → 保存类型未提交（exb 过滤器），第 " + attempt + " 次重试");
                        retry = true; break;
                    }
                    if (!product && !dialogOpen && sw.ElapsedMilliseconds > 2000)
                    {
                        // 框没了也没产物：另存框被静默关掉（校验失败/代点没吃掉）→ 重试
                        if (log != null) log("另存框已消失且无产物（" + (int)sw.ElapsedMilliseconds + "ms）→ 重试");
                        retry = true; break;
                    }
                    int el = (int)sw.ElapsedMilliseconds;
                    if (log != null && el - lastLogged >= 10000)
                    {
                        lastLogged = el;
                        log("等 " + ext + " 产物 " + el + "ms（产物=" + (product ? "有" : "无") +
                            " 另存框=" + (dialogOpen ? "开" : "关") + "）");
                    }
                    Pump();
                    Thread.Sleep(400);
                }
                if (attempt == maxAttempts) break;
                if (!retry) retry = true;   // 超时也走重试路径

                // —— 重试驱动 ——
                IntPtr cur = FindSaveDialogOnce();
                if (cur == IntPtr.Zero)
                {
                    if (log != null) log("重试：另存框不在 → 重投 ID_FILE_SAVE_AS 重开");
                    if (mdi != IntPtr.Zero)
                        PostMessage(mdi, WM_COMMAND, (IntPtr)ID_FILE_SAVE_AS, IntPtr.Zero);
                    cur = OpenSaveAsDialog(mdi, IntPtr.Zero, 15000, log);
                    if (cur == IntPtr.Zero)
                    {
                        if (log != null) log("重试：另存框重开失败，放弃（已 " + attempt + " 次）");
                        return false;
                    }
                }
                try { if (System.IO.File.Exists(marker)) System.IO.File.Delete(marker); } catch { }
                if (!FillAndSave(cur, outPath, ext, log))
                {
                    if (log != null) log("重试：填框代点失败（对话框留在原地，详见取证日志）");
                    return false;
                }
            }
            return FileOk(outPath) && FindSaveDialogOnce() == IntPtr.Zero;
        }

        /// <summary>
        /// 取消**本流程自己**迟到的另存框（2026-11-12 -07管 闪退实证：C# 侧产物判据已过、放弃该格式后，
        /// 另存框才迟弹出并一直开着 → 此时投 CLOSEDOC 的同步保存会插进未退干净的模态栈，原生层闪退）。
        /// 只在收尾静默期由 ProcessExb 调用：框是本流程开的（另存参数都是本流程填的），点「取消」零副作用。
        /// 返回 true=找到并点了取消；false=没有另存框。
        /// </summary>
        public static bool CancelLingeringSaveDialog(Action<string> log)
        {
            IntPtr dlg = FindSaveDialogOnce();
            if (dlg == IntPtr.Zero) return false;
            var btn = UiWin.FindButtonInSubtree(dlg, new[] { "取消", "Cancel" });
            if (btn == null)
            {
                if (log != null) log("迟到另存框无「取消」按钮，不乱点 title=[" + TitleOf(dlg) + "]");
                return true;   // 框还在（点了也白点的情况），让上层继续等
            }
            if (log != null) log("点「取消」关闭迟到另存框 title=[" + TitleOf(dlg) + "]");
            UiWin.Click(btn, "收尾静默：取消本流程迟到的另存框");
            Pump();
            return true;
        }

        /// <summary>
        /// 流程收尾清障（2026-11-11 批量转换实机）：批量跑完后若仍有**非预期模态框**堵着
        /// （CAXA 主框架被禁用 = 整个宿主卡死），逐张点「取消/否」关掉并记日志。
        /// 绝不点「是/确定/保存」（零副作用原则）；「另存文件/另存为」框不动（那是用户可能正用的）。
        /// </summary>
        public static void DismissLingeringModals(Action<string> log)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var dialogs = UiWin.FindModalDialogs("另存文件", "另存为");
                if (dialogs.Count == 0) return;
                bool clickedAny = false;
                foreach (IntPtr dlg in dialogs)
                {
                    var btn = UiWin.FindButtonInSubtree(dlg, new[] { "取消", "否(&N)", "否(N)", "否" });
                    if (btn != null)
                    {
                        if (log != null) log("清障：点「" + btn.Text + "」关闭堵路模态框 title=[" +
                                             TitleOf(dlg) + "] class=" + ClassOf(dlg));
                        UiWin.Click(btn, "流程收尾清障（点取消/否关闭非预期模态框）");
                        clickedAny = true;
                    }
                    else if (log != null)
                        log("清障：模态框无「取消/否」按钮，不乱点，请人工处理 title=[" +
                            TitleOf(dlg) + "] class=" + ClassOf(dlg));
                }
                if (!clickedAny) return;
                Pump();
                Thread.Sleep(800);
            }
        }

        /// <summary>等打开期弹窗消化：模态计数连续 ~2 秒为 0 或超时。</summary>
        public static void WaitPopupsSettled(int timeoutMs, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int quiet = 0;
            int lastLogged = -1;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                int modal = PopupAutoYes.ModalCount;
                if (modal == 0)
                {
                    quiet += 400;
                    if (quiet >= 2000) return;
                }
                else quiet = 0;
                int el = (int)sw.ElapsedMilliseconds;
                if (log != null && modal > 0 && el - lastLogged >= 3000)
                {
                    lastLogged = el;
                    log("等打开期弹窗消化 " + el + "ms（模态框 " + modal + " 个，代点中）");
                }
                Pump();
                Thread.Sleep(400);
            }
        }

        private static void SendCtrlShiftS()
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
            keybd_event(VK_S, 0, 0, UIntPtr.Zero);
            keybd_event(VK_S, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
