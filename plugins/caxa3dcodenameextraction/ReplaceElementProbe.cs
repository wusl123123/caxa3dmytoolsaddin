// 「替换元素」探针：用 UI 代点方式驱动 CAXA 原生「替换零件/装配」面板。
//
// 手工链路（用户 2026-11-01 截图 1/2/3）：
//   选中零部件 → 右键「外链管理 → 替换零件/装配」→ 面板出现（要更换的零件/装配 +
//   仅选定/同一父装配下的所有实例/所有实例 + ☑重新附加约束）→ 点「导入零件/装配」→
//   文件框选 零件B.ics → 打开 → 面板确定 → 进入「重新附加约束」命令条 → 绿勾确定
//
// 为什么要探针：ICAPI 里没有「替换零件/装配」的公开 API（见 ICM Tools-API映射.md 第 34 条
// 「替换元素 → IPROExtLinkMgr / OnDragDropReplaceSelectedElement → 无公开接口」），
// 而 WM_COMMAND 53851 只能把命令叫起来，后面那串面板交互仍要人点（实机 11:38 那轮空等 74 秒）。
//
// 关键约束：这个面板不是 #32770 对话框，通用代点器 PopupAutoYes 看不见它，所以必须单独有
// 一套按文字找控件 + 代点的逻辑；文件框仍是 #32770，继续交给 PopupAutoYes 填路径点打开。
// 每一步都把看到的窗口/控件树作为取证块写进跟踪日志，下一轮据此把匹配收紧。
//
// 2026-11-01 实机三轮取证后的硬结论（都已在代码里落实）：
//   1) WM_COMMAND 的目标必须是 Afx: 开头的 CAXA 主框架（UiWin.CadFrame）。
//      Process.MainWindowHandle 返回的是本插件自己的 WinForms 窗口（取证 hwnd=0x120BA6
//      「带图改名及图号分离-…」），投进去等于投给自己，面板永远不会出。
//      13:50 修正后：hwnd=0x670E1E class=Afx:… → posted=True → 面板真的出来了。
//   2) 面板条目**是有窗口文字的普通子窗口**（`class=Button 文字=[导入零件/装配]`，
//      BM_CLICK 生效、文件框随即被 PopupAutoYes 填好并点「打开」）——12:47 那轮 0 命中
//      是因为当时投错窗口、面板压根没出现，不是自绘。MSAA 只作兜底（框架 pane 用得上）。
//   3) 提交靠 pane 顶部那条命令条的绿色「✓」，而**它就是 pane 根 MSAA 里的一个条目**
//      （15:19 取证：`TasksPane toolbar 矩形=34,219 625x25` 之下有 `确定 矩形=36,220 22x22`
//      和 `取消 矩形=58,220 22x22`，动作都是 `[Press]`）。`accDoDefaultAction` 实机生效，
//      整个 pane 根只遍历 2 个节点就命中——**这才是主通道**；✓ 自己没有 HWND，
//      所以退一步才按 pane 左上偏移投左键（正解偏移 = +13,+12，✗ 从 +24 起，候选 x 严格 <24）。
//      提交梯子：pane 内带文字按钮 → **以 pane 为根**的 MSAA「确定」→ pane 偏移点 ✓（三个候选）
//      → pane 父链 IDOK → 焦点回车；不再整框架扫描、也不再往主框架盲投 IDOK/回车。
//      注意动作名语言混杂：pane 的滚动条是中文 `[按]`，命令条按钮是英文 `[Press]` ⇒ 禁止按动作名匹配。
//   4) 实机 14:37 的卡死复盘（这一条决定了现在的定位方式）：MsaaAccess.Walk 只沿 IAccessible
//      的孩子下钻、**不跨子窗口句柄**，所以从框架根扫 2841 个节点也看不到属性 pane 里的条目——
//      于是「导入零件/装配」查不到 → panelGone 误判「面板已关」→ 一头扎进 120 秒权威等待；
//      同一次框架根扫到的第一个「确定」是别处的按钮，点了等于没点。用户看到的画面就是面板还开着、
//      插件却在干等。现在所有查找都限定在 pane 子树（ HWND 一次 EnumChildWindows + pane 根 MSAA ），
//      判「面板已关」要连问两次且 pane 仍在，绝不拿一次扫漏当证据。
//   5) 文件框不用「等它出现」：PopupAutoYes 从弹出到点掉只 ~0.3 秒，比第一轮轮询还快（14:37 白等 20 秒），
//      改看 PopupAutoYes.ClickCount 是否增加。
//   6) 15:19 实机全程 23.9 秒且零手工：控件获取 0.2 s（pane 按 id=59474 命中）→ BM_CLICK 导入 →
//      文件框 0.26 s 代点完 → pane 根 MSAA「确定」0.5 s 命中并提交 → 之后 20 s 是 CAXA 自己在换链接。
//      那 20 秒里活选择确实是空的（trace：15:19:19.227 `IZArray.Count=0` → 15:19:42.414 恢复为 1），
//      **外链换完的那一刻选择集才被还原**，所以判据只是「暂时读不到」而不是「永远读不到」——
//      权威等待要留够时长，但绝不能因为读不到就重投提交动作。
//
// 铁律：只在 CAXA 主线程（STA）调用；等待一律走 MainForm 注入的 Wait（内含 DoEvents 与停止检查）；
//       成功判据只有一个——选中件的外链路径真的换成了新 ics，代点动作本身不作数。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace caxa3dcodenameextraction
{
    /// <summary>一个窗口或子控件的快照（取证与代点都用它）。</summary>
    [ComVisible(false)]
    internal sealed class UiCtrl
    {
        public IntPtr Hwnd;
        public string Class = "";
        public string Text = "";
        public int Id;
        public IntPtr Top;            // 所属顶层窗口
        public string TopTitle = "";
        public string TopClass = "";
        public bool Enabled;
    }

    /// <summary>
    /// 本进程（IronCAD.exe）窗口/控件枚举 + 代点。
    /// 只扫本进程可见顶层窗口，且跳过插件自己的 WinForms 窗体（WindowsForms10.*），
    /// 避免把路径填进主窗口工具栏（坑：见 PopupAutoYes 同类注释）。
    /// </summary>
    [ComVisible(false)]
    internal static class UiWin
    {
        private const int MaxChildrenPerTop = 3000;

        // ------------------------------------------------------------ Win32

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TBBUTTON
        {
            public int iCommand;
            public int iBitmap;
            public byte fsState;
            public byte fsStyle;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public byte[] bReserved;
            public IntPtr dwData;
            public IntPtr iString;
        }

        [DllImport("user32.dll", EntryPoint = "EnumWindows")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "EnumChildWindows")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "GetClientRect")]
        private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageTb(IntPtr h, uint msg, IntPtr w, out TBBUTTON l);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
        private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")]
        private static extern IntPtr SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>GetGUIThreadInfo 的输入/输出结构（cbSize 必须先填，否则返回 false）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        private const uint BM_CLICK = 0x00F5;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint MK_LBUTTON = 0x0001;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint VK_RETURN = 0x0D;
        private const uint WM_COMMAND = 0x0111;
        private const uint TB_BUTTONCOUNT = 0x0418;
        private const uint TB_GETBUTTON = 0x0413;
        private const uint TB_GETBUTTONTEXTW = 0x0428;

        /// <summary>统一比较口径：去 &amp; 与空白、转小写（实机文案常带助记符）。</summary>
        public static string Normalize(string s)
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

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TextOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>
        /// CAXA 主框架句柄——所有 WM_COMMAND / 快捷键都必须投给它。
        /// 绝不能用 Process.MainWindowHandle：实机取证证明它返回的是本插件自己的 WinForms 窗口
        /// （0x120BA6「带图改名及图号分离-…」），53851 投进去等于投给自己，面板永远不出。
        /// 判据：本进程可见顶层 + 类名以 Afx: 开头（BCGControlBar 的 MFC 框架）+ 标题含 CAXA 者优先。
        /// </summary>
        public static IntPtr CadFrame()
        {
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var afx = new List<IntPtr>();
            var other = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != self || !IsWindowVisible(h)) return true;
                string cls = ClassOf(h);
                if (cls.StartsWith("WindowsForms10", StringComparison.Ordinal)) return true;
                if (cls.StartsWith("Afx:", StringComparison.Ordinal)) afx.Add(h);
                else other.Add(h);
                return true;
            }, IntPtr.Zero);
            foreach (IntPtr h in afx)
            {
                if (TextOf(h).IndexOf("CAXA", StringComparison.OrdinalIgnoreCase) >= 0) return h;
            }
            if (afx.Count > 0) return afx[0];
            foreach (IntPtr h in other)
            {
                string t = TextOf(h);
                if (t.IndexOf("CAXA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("实体设计", StringComparison.Ordinal) >= 0) return h;
            }
            return IntPtr.Zero;
        }

        /// <summary>抓取本进程所有可见顶层窗口及其全部后代控件。</summary>
        public static List<UiCtrl> Snapshot()
        {
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var list = new List<UiCtrl>();
            var tops = new List<IntPtr>();

            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != self || !IsWindowVisible(h)) return true;
                string cls = ClassOf(h);
                if (cls.StartsWith("WindowsForms10", StringComparison.Ordinal)) return true;
                tops.Add(h);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr top in tops)
            {
                string topCls = ClassOf(top);
                string topTitle = TextOf(top);
                list.Add(new UiCtrl
                {
                    Hwnd = top,
                    Class = topCls,
                    Text = topTitle,
                    Id = 0,
                    Top = top,
                    TopTitle = topTitle,
                    TopClass = topCls,
                    Enabled = IsWindowEnabled(top)
                });

                int guard = 0;
                EnumChildWindows(top, (h, l) =>
                {
                    if (++guard > MaxChildrenPerTop) return false;
                    if (h == top) return true;
                    string cls = ClassOf(h);
                    string txt = TextOf(h);
                    if (cls.Length == 0 && txt.Length == 0) return true;
                    list.Add(new UiCtrl
                    {
                        Hwnd = h,
                        Class = cls,
                        Text = txt,
                        Id = GetDlgCtrlID(h),
                        Top = top,
                        TopTitle = topTitle,
                        TopClass = topCls,
                        Enabled = IsWindowEnabled(h)
                    });
                    return true;
                }, IntPtr.Zero);
            }
            return list;
        }

        /// <summary>按文字包含命中（只作 pane 子树问不到时的一次性全局兜底）。</summary>
        public static UiCtrl FindByTextLike(string normalizedFragment)
        {
            if (normalizedFragment.Length == 0) return null;
            foreach (UiCtrl c in Snapshot())
            {
                if (c.Text.Length > 0 && Normalize(c.Text).IndexOf(normalizedFragment,
                        StringComparison.Ordinal) >= 0)
                    return c;
            }
            return null;
        }

        /// <summary>
        /// 按标题找可见顶层窗口，**本进程与图纸进程（CAXADraft）都算**。
        /// 图纸环境的对话框不一定挂在 IronCAD 上（PopupAutoYes 早就为此加了双进程白名单），
        /// 只扫本进程会永远「等不到对话框」。
        /// </summary>
        public static IntPtr FindTopByTitle(string normalizedFragment)
        {
            if (normalizedFragment.Length == 0) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                string title = TextOf(h);
                if (title.Length == 0) return true;
                if (Normalize(title).IndexOf(normalizedFragment, StringComparison.Ordinal) < 0) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!IsWatchedPid(pid)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static readonly Dictionary<uint, string> _pidNames = new Dictionary<uint, string>();

        private static string ProcNameOf(uint pid)
        {
            string name;
            lock (_pidNames) { if (_pidNames.TryGetValue(pid, out name)) return name; }
            try { using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) name = p.ProcessName; }
            catch { name = ""; }
            lock (_pidNames) _pidNames[pid] = name;
            return name;
        }

        /// <summary>可代点白名单与 PopupAutoYes 保持一致：本进程 + CAXADraft。</summary>
        private static bool IsWatchedPid(uint pid)
        {
            if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return true;
            return string.Equals(ProcNameOf(pid), "CAXADraft", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 只取证一个窗口：子控件（类名 / id / 文字 / 矩形 / 相对窗口左上 / 启用）
        /// + 以它为根的 MSAA 条目清单。第一次见面的对话框（如「改变链接文件」）靠这一份
        /// 定后续命中口径，别拿全窗口快照去猜。
        /// </summary>
        public static void DumpWindow(IntPtr top, string tag)
        {
            try
            {
                if (top == IntPtr.Zero) { Diagnostics.Log("UiWin: 没有窗口可取证（" + tag + "）"); return; }
                RECT tr;
                GetWindowRect(top, out tr);
                uint pid;
                GetWindowThreadProcessId(top, out pid);
                var sb = new StringBuilder();
                sb.AppendLine("========== 取证 " + tag + "  " +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ==========");
                sb.AppendLine("[窗口] 0x" + top.ToInt64().ToString("X") + " class=" + ClassOf(top) +
                    " 标题=[" + TextOf(top) + "] 进程=" + ProcNameOf(pid) +
                    " 矩形=" + tr.Left + "," + tr.Top + " " + (tr.Right - tr.Left) + "x" + (tr.Bottom - tr.Top) +
                    " 启用=" + (IsWindowEnabled(top) ? "Y" : "N"));
                int n = 0, guard = 0;
                EnumChildWindows(top, (h, l) =>
                {
                    if (++guard > MaxChildrenPerTop) return false;
                    if (h == top) return true;
                    RECT r;
                    GetWindowRect(h, out r);
                    sb.AppendLine("    0x" + h.ToInt64().ToString("X") + " class=" + ClassOf(h) +
                        " id=" + GetDlgCtrlID(h) + " 文字=[" + TextOf(h) + "]" +
                        " 矩形=" + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) +
                        " 相对=(" + (r.Left - tr.Left) + "," + (r.Top - tr.Top) + ")" +
                        " 启用=" + (IsWindowEnabled(h) ? "Y" : "N"));
                    n++;
                    return true;
                }, IntPtr.Zero);
                sb.AppendLine("    子控件 " + n + " 个");
                sb.AppendLine(Msaa.NamesDump(top, 400));
                Diagnostics.Dump(tag, sb.ToString());
                Diagnostics.Log("UiWin取证[" + tag + "]: class=" + ClassOf(top) + " 子控件 " + n +
                    " 个，明细见上一段取证块（跟踪日志）");
            }
            catch (Exception ex) { Diagnostics.Log("UiWin: 窗口取证失败 " + ex.Message); }
        }

        /// <summary>
        /// 「属性」pane（放「替换零件/装配」面板的那个 BCGPControlBar）。实机三次取证它的
        /// 控件 id 稳定为 59474、文字稳定为「属性」，所以按 id 命中、按文字兜底，命中后缓存。
        /// 有了它就不必再全窗口快照 + 整棵 MSAA 树（实机 404~590 控件 / 2841~6415 节点）。
        /// </summary>
        private static IntPtr _paneCache = IntPtr.Zero;

        public static IntPtr PropertyPane()
        {
            if (_paneCache != IntPtr.Zero && IsWindow(_paneCache)) return _paneCache;
            _paneCache = IntPtr.Zero;
            IntPtr frame = CadFrame();
            if (frame == IntPtr.Zero) return IntPtr.Zero;
            IntPtr byText = IntPtr.Zero;
            EnumChildWindows(frame, (h, l) =>
            {
                if (ClassOf(h).IndexOf("BCGPControlBar", StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (GetDlgCtrlID(h) == PaneControlId) { _paneCache = h; return false; }
                if (byText == IntPtr.Zero && Normalize(TextOf(h)) == Normalize("属性")) byText = h;
                return true;
            }, IntPtr.Zero);
            if (_paneCache != IntPtr.Zero) return LogPane(_paneCache, "按 id");
            _paneCache = byText;
            return LogPane(_paneCache, "按文字「属性」");
        }

        private static IntPtr LogPane(IntPtr pane, string how)
        {
            if (pane == IntPtr.Zero)
            {
                Diagnostics.Log("UiWin: 找不到「属性」pane（" + how + "），代点只能退回全局查找");
                return pane;
            }
            Diagnostics.Trace("UiWin: 属性 pane=" + how + "命中 0x" + pane.ToInt64().ToString("X") +
                " class=" + ClassOf(pane) + " id=" + GetDlgCtrlID(pane));
            return pane;
        }

        /// <summary>实机取证：属性 pane 的对话框控件 id（13:05 / 13:50 / 14:37 三轮一致）。</summary>
        private const int PaneControlId = 59474;

        /// <summary>
        /// 功能区（class 含 BCGPRibbonBar）自己的子 HWND —— 「主页」面板上的按钮只有从它这棵
        /// 可访问性子树才问得到（Msaa.Walk 不跨子窗口句柄，从框架根扫 2 万个节点也扫不到）。
        /// 实机 11-02 16:40 取证：`主页/新文件/新图纸`、`Quick Access Toolbar/新的图纸环境`
        /// 都是这里的 MSAA 条目，动作=[Press]、无自己的 HWND。
        /// </summary>
        private static IntPtr _ribbonCache = IntPtr.Zero;

        public static IntPtr RibbonBar()
        {
            if (_ribbonCache != IntPtr.Zero && IsWindow(_ribbonCache)) return _ribbonCache;
            _ribbonCache = IntPtr.Zero;
            IntPtr frame = CadFrame();
            if (frame == IntPtr.Zero) return IntPtr.Zero;
            EnumChildWindows(frame, (h, l) =>
            {
                if (ClassOf(h).IndexOf("BCGPRibbonBar", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _ribbonCache = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (_ribbonCache == IntPtr.Zero)
                Diagnostics.Log("UiWin: 找不到功能区窗口（class 含 BCGPRibbonBar），UI 代点没有落点");
            else
                Diagnostics.Trace("UiWin: 功能区 0x" + _ribbonCache.ToInt64().ToString("X") +
                    " class=" + ClassOf(_ribbonCache));
            return _ribbonCache;
        }


        /// <summary>只在指定窗口的子树里按文字包含找控件（一次 EnumChildWindows，几十个点）。</summary>
        public static UiCtrl FindInSubtree(IntPtr root, string normalizedFragment)
        {
            if (root == IntPtr.Zero || normalizedFragment.Length == 0) return null;
            UiCtrl found = null;
            int guard = 0;
            EnumChildWindows(root, (h, l) =>
            {
                if (++guard > MaxChildrenPerTop) return false;
                string txt = TextOf(h);
                if (txt.Length == 0) return true;
                if (Normalize(txt).IndexOf(normalizedFragment, StringComparison.Ordinal) >= 0)
                {
                    found = new UiCtrl
                    {
                        Hwnd = h,
                        Class = ClassOf(h),
                        Text = txt,
                        Id = GetDlgCtrlID(h),
                        Top = root,
                        TopTitle = TextOf(root),
                        TopClass = ClassOf(root),
                        Enabled = IsWindowEnabled(h)
                    };
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// 绿勾 ✓ 的候选偏移（相对「属性」pane 窗口矩形左上角，屏幕像素）。
        /// 实机 15:19 取证给了精确答案（pane 矩形=34,219；pane 根 MSAA 条目清单里：
        /// `TasksPane toolbar 矩形=34,219 625x25` 下有两个子条目 `确定 矩形=36,220 22x22` 与
        /// `取消 矩形=58,220 22x22`）⇒ 换成相对 pane 左上：✓ 占 x∈[2,24) y∈[1,23)，中心 (+13,+12)；
        /// ✗ 从 x=24 起。所以三个候选全部落在 ✓ 的 22x22 框内且 x 严格小于 24，绝不越到「取消」上。
        /// 这条通道只是兜底：正解是同一个 MSAA 条目 `确定` 的 `accDoDefaultAction`（15:19 实机生效，
        /// pane 根只遍历 2 个节点就命中）；MSAA 拿不到时才按这里的偏移投左键。
        /// 每个候选点先 WindowFromPoint 命中测试，命中的必须是 pane 或其子孙（实机命中
        /// `TasksPane toolbar` 那个子 HWND），否则放弃——宁可不动也不要把点丢到 3D 视图上。
        /// 取证块里 DumpPane 会把三个候选各命中谁写清楚。
        /// </summary>
        private static readonly int[][] AcceptOffsets = {
            new int[] { 13, 12 },   // ✓ 中心（36,220 22x22 的几何中心，相对 pane 左上）
            new int[] { 8, 9 },     // 左上侧抖动
            new int[] { 19, 16 }    // 右下侧抖动（x 仍 < 24，不碰 ✗）
        };

        public static int AcceptOffsetCount { get { return AcceptOffsets.Length; } }

        public static bool ClickPaneAccept(IntPtr pane, string why, int which)
        {
            try
            {
                if (pane == IntPtr.Zero || !IsWindow(pane))
                {
                    Diagnostics.Log("UiWin: 没有「属性」pane，跳过绿勾（" + why + "）");
                    return false;
                }
                if (which < 0 || which >= AcceptOffsets.Length) return false;
                RECT pr;
                if (!GetWindowRect(pane, out pr)) return false;
                POINT pt = new POINT { X = pr.Left + AcceptOffsets[which][0],
                                       Y = pr.Top + AcceptOffsets[which][1] };
                IntPtr hit = WindowFromPoint(pt);
                bool inside = hit != IntPtr.Zero && (hit == pane || IsDescendant(hit, pane));
                Diagnostics.Trace("UiWin: 绿勾候选#" + which + " 屏幕(" + pt.X + "," + pt.Y + ") = pane 左上 +" +
                    AcceptOffsets[which][0] + ",+" + AcceptOffsets[which][1] + "；pane=0x" +
                    pane.ToInt64().ToString("X") + " 矩形=" + pr.Left + "," + pr.Top + " " +
                    (pr.Right - pr.Left) + "x" + (pr.Bottom - pr.Top) +
                    "；命中=0x" + (hit == IntPtr.Zero ? "0" : hit.ToInt64().ToString("X")) +
                    (hit == IntPtr.Zero ? "" : " class=" + ClassOf(hit) + " id=" + GetDlgCtrlID(hit)) +
                    (inside ? "" : "（不在 pane 内，放弃）") + "（" + why + "）");
                if (!inside) return false;
                POINT cl = new POINT { X = pt.X, Y = pt.Y };
                ScreenToClient(hit, ref cl);
                IntPtr lp = (IntPtr)((cl.Y << 16) | (cl.X & 0xFFFF));
                PostMessage(hit, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
                PostMessage(hit, WM_LBUTTONUP, IntPtr.Zero, lp);
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("UiWin: 绿勾通道异常 " + ex.Message);
                return false;
            }
        }

        /// <summary>h 是否是 anc 的子孙（走 GetParent 链，最多 12 级）。</summary>
        public static bool IsDescendant(IntPtr h, IntPtr anc)
        {
            IntPtr cur = GetParent(h);
            int guard = 0;
            while (cur != IntPtr.Zero && guard++ < 12)
            {
                if (cur == anc) return true;
                cur = GetParent(cur);
            }
            return false;
        }

        /// <summary>
        /// 本进程是否开着文件框：只枚举顶层窗口的类名，不再 Snapshot（以前为判一个 bool
        /// 把 400+ 控件全抓一遍，还得靠标题口径，实机 13:50 因标题不是标准「打开」白等 20 秒）。
        /// </summary>
        public static bool FileDialogOpen()
        {
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            bool found = false;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != self) return true;
                if (ClassOf(h) != "#32770") return true;
                found = true;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>代点：标准 Button 用 BM_CLICK；自绘控件用客户端中心左键单击。</summary>
        public static bool Click(UiCtrl c, string why)
        {
            if (c == null || c.Hwnd == IntPtr.Zero) return false;
            try
            {
                if (c.Class.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bool ok = PostMessage(c.Hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Diagnostics.Log("UiWin: BM_CLICK「" + c.Text + "」id=" + c.Id +
                                    " class=" + c.Class + " ok=" + ok + "（" + why + "）");
                    return ok;
                }
                RECT rc;
                if (!GetClientRect(c.Hwnd, out rc) || rc.Right <= 0 || rc.Bottom <= 0)
                {
                    Diagnostics.Log("UiWin: 取不到客户区，点不动「" + c.Text + "」class=" + c.Class);
                    return false;
                }
                int cx = rc.Right / 2, cy = rc.Bottom / 2;
                IntPtr lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
                PostMessage(c.Hwnd, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
                PostMessage(c.Hwnd, WM_LBUTTONUP, IntPtr.Zero, lp);
                Diagnostics.Log("UiWin: 坐标点击「" + c.Text + "」@" + cx + "," + cy +
                                " class=" + c.Class + "（" + why + "）");
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("UiWin: 点击异常 " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 向「属性」pane 的所有祖先逐个投 WM_COMMAND IDOK。
        /// 实机 13:50 证明：投给主框架的 IDOK 没人接（属性页命令条的 ✓ 由 pane 自己处理），
        /// MFC 的 IDOK 沿祖先链找 handler，所以逐级投一遍。IDOK 无破坏性（≠ 取消/关闭）。
        /// </summary>
        public static void PostIdokToAncestors(IntPtr from, string why)
        {
            IntPtr cur = GetParent(from);
            int guard = 0;
            while (cur != IntPtr.Zero && guard++ < 12)
            {
                PostMessage(cur, WM_COMMAND, (IntPtr)1, IntPtr.Zero);
                Diagnostics.Log("UiWin: 向祖先 0x" + cur.ToInt64().ToString("X") +
                                " class=" + ClassOf(cur) + " 投 IDOK（" + why + "）");
                cur = GetParent(cur);
            }
        }

        /// <summary>
        /// 把回车发给**真正的焦点窗口**（GetGUIThreadInfo 的 hwndFocus），而不是顶层框架。
        /// 之前只往主框架投 VK_RETURN 无效：MFC 的键盘路由从焦点窗口开始。
        /// </summary>
        public static void EnterToFocus(string why)
        {
            try
            {
                IntPtr frame = CadFrame();
                if (frame != IntPtr.Zero) SetForegroundWindow(frame);
                GUITHREADINFO info = new GUITHREADINFO();
                info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                IntPtr focus = IntPtr.Zero, active = IntPtr.Zero;
                if (GetGUIThreadInfo(GetCurrentThreadId(), ref info))
                {
                    focus = info.hwndFocus;
                    active = info.hwndActive;
                }
                if (focus == IntPtr.Zero) focus = GetFocus();
                if (focus == IntPtr.Zero) focus = active;
                if (focus == IntPtr.Zero) focus = frame;
                if (focus == IntPtr.Zero) { Diagnostics.Log("UiWin: 没有焦点窗口，跳过后回车（" + why + "）"); return; }
                PostMessage(focus, WM_KEYDOWN, (IntPtr)VK_RETURN, (IntPtr)0x001C0001);
                PostMessage(focus, WM_KEYUP, (IntPtr)VK_RETURN, (IntPtr)0xC01C0001);
                Diagnostics.Log("UiWin: 已向焦点窗口 0x" + focus.ToInt64().ToString("X") +
                                " class=" + ClassOf(focus) + " 投回车（" + why + "）");
            }
            catch (Exception ex) { Diagnostics.Log("UiWin: 焦点回车失败 " + ex.Message); }
        }

        /// <summary>向上找所属 pane（BCGPControlBar）；找不到返回 0。</summary>
        private static IntPtr PaneOf(IntPtr h)
        {
            IntPtr cur = GetParent(h);
            int guard = 0;
            while (cur != IntPtr.Zero && guard++ < 12)
            {
                if (ClassOf(cur).IndexOf("BCGPControlBar", StringComparison.OrdinalIgnoreCase) >= 0)
                    return cur;
                cur = GetParent(cur);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 把 pane 锚点重挂到指定控件所属的 BCGPControlBar。
        /// 面板可能被 dock 到别的 pane（或缓存的 59474 已失效）——全局兜底命中后就重锚，
        /// 之后每一步仍然只问这一个子树，不必再回到全窗口快照。
        /// </summary>
        public static void ReanchorPane(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            IntPtr pane = h;
            if (ClassOf(pane).IndexOf("BCGPControlBar", StringComparison.OrdinalIgnoreCase) < 0)
                pane = PaneOf(h);
            if (pane == IntPtr.Zero) return;
            if (pane == _paneCache) return;
            Diagnostics.Trace("UiWin: pane 锚点重挂 0x" + pane.ToInt64().ToString("X") +
                " class=" + ClassOf(pane) + " 文字=[" + TextOf(pane) + "] id=" + GetDlgCtrlID(pane) +
                "（原 0x" + _paneCache.ToInt64().ToString("X") + "）");
            _paneCache = pane;
        }

        /// <summary>
        /// 「属性」pane 现在在不在（缓存失效会重找一次）。定位不到 pane 时**不能**断言面板已关——
        /// 那只会把「扫不到」说成「不在了」，正是实机 14:37 白等 120 秒的成因。
        /// </summary>
        public static bool PaneAlive()
        {
            return PropertyPane() != IntPtr.Zero;
        }

        /// <summary>在指定 pane 子树里按候选文字**精确**找按钮（一次 EnumChildWindows，几十个控件）。</summary>
        public static UiCtrl FindButtonInSubtree(IntPtr root, string[] candidates)
        {
            if (root == IntPtr.Zero || candidates == null) return null;
            UiCtrl found = null;
            int guard = 0;
            EnumChildWindows(root, (h, l) =>
            {
                if (++guard > MaxChildrenPerTop) return false;
                if (!IsWindowEnabled(h)) return true;
                string txt = TextOf(h);
                if (txt.Length == 0) return true;
                string n = Normalize(txt);
                foreach (string cand in candidates)
                {
                    if (n != Normalize(cand)) continue;
                    found = new UiCtrl
                    {
                        Hwnd = h,
                        Class = ClassOf(h),
                        Text = txt,
                        Id = GetDlgCtrlID(h),
                        Top = root,
                        TopTitle = TextOf(root),
                        TopClass = ClassOf(root),
                        Enabled = true
                    };
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>把当前窗口/控件树作为取证块写进跟踪日志，再发一条摘要行（取证与判据同一份文件，回传一次就够）。</summary>
        public static void Dump(string tag)
        {
            try
            {
                List<UiCtrl> all = Snapshot();
                var sb = new StringBuilder();
                sb.AppendLine("========== 取证 " + tag + "  " +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ==========");
                IntPtr curTop = IntPtr.Zero;
                int tops = 0;
                foreach (UiCtrl c in all)
                {
                    if (c.Top != curTop)
                    {
                        curTop = c.Top;
                        tops++;
                        sb.AppendLine("[窗口#" + tops + "] 0x" + c.Top.ToInt64().ToString("X") +
                            " class=" + c.TopClass + " 标题=[" + c.TopTitle + "]");
                    }
                    if (c.Hwnd == c.Top) continue;
                    sb.AppendLine("    class=" + c.Class + " id=" + c.Id +
                        " 文字=[" + c.Text + "] 启用=" + (c.Enabled ? "Y" : "N"));
                    if (c.Class.IndexOf("ToolBar", StringComparison.OrdinalIgnoreCase) >= 0)
                        DumpToolbar(sb, c.Hwnd);
                }
                sb.AppendLine();
                // 命令条 ✓ 与 pane 条目在窗口文字里是空的，MSAA 清单必须按 pane 子树取
                // （按框架根取永远看不到 pane 内容：Collect 不跨子窗口句柄）
                IntPtr pane = PropertyPane();
                if (pane == IntPtr.Zero)
                    sb.AppendLine("(没有「属性」pane，MSAA 清单退回框架根——注定看不到面板条目)");
                else DumpPane(sb, pane);
                try { sb.AppendLine(Msaa.NamesDump(pane, 200)); }
                catch (Exception ex) { sb.AppendLine("(MSAA 取证失败：" + ex.Message + ")"); }
                Diagnostics.Dump(tag, sb.ToString());

                var hits = new List<string>();
                foreach (UiCtrl c in all)
                {
                    if (c.Text.Length == 0 || c.Text.Length > 24) continue;
                    string n = Normalize(c.Text);
                    if (n.IndexOf("导入零件", StringComparison.Ordinal) >= 0 ||
                        n.IndexOf("替换", StringComparison.Ordinal) >= 0 ||
                        n.IndexOf("确定", StringComparison.Ordinal) >= 0 ||
                        n.IndexOf("约束", StringComparison.Ordinal) >= 0)
                        hits.Add("「" + c.Text + "」" + c.Class + "/id" + c.Id);
                }
                Diagnostics.Log("UiWin取证[" + tag + "]: " + tops + " 窗口 / " + all.Count +
                    " 控件，关键控件 " + hits.Count + " 个" +
                    (hits.Count > 0 ? "：" + string.Join(" | ", hits.ToArray()) : "") +
                    "，明细见上一段取证块（跟踪日志）");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("UiWin: 取证失败 " + ex.Message);
            }
        }

        /// <summary>
        /// pane 子树单独取证：矩形、绿勾推算点命中谁、以及 pane 里每个子控件连「相对 pane 左上」的偏移。
        /// 下一轮就是靠这些偏移把 ✓ 的定位收准的，比整窗口快照有用得多。
        /// </summary>
        private static void DumpPane(StringBuilder sb, IntPtr pane)
        {
            RECT pr;
            GetWindowRect(pane, out pr);
            sb.AppendLine("---------- 「属性」pane 0x" + pane.ToInt64().ToString("X") +
                " class=" + ClassOf(pane) + " id=" + GetDlgCtrlID(pane) +
                " 文字=[" + TextOf(pane) + "] 矩形=" + pr.Left + "," + pr.Top + " " +
                (pr.Right - pr.Left) + "x" + (pr.Bottom - pr.Top) + " ----------");
            for (int i = 0; i < AcceptOffsets.Length; i++)
            {
                POINT guess = new POINT { X = pr.Left + AcceptOffsets[i][0],
                                          Y = pr.Top + AcceptOffsets[i][1] };
                IntPtr hit = WindowFromPoint(guess);
                sb.AppendLine("    绿勾候选#" + i + " 屏幕(" + guess.X + "," + guess.Y + ") = pane 左上 +" +
                    AcceptOffsets[i][0] + ",+" + AcceptOffsets[i][1] + "；命中=" +
                    (hit == IntPtr.Zero ? "(空)" : "0x" + hit.ToInt64().ToString("X") + " class=" +
                     ClassOf(hit) + " id=" + GetDlgCtrlID(hit)) +
                    (hit == IntPtr.Zero || (hit != pane && !IsDescendant(hit, pane)) ? " ←不在 pane 内" : " ←在 pane 内"));
            }
            int n = 0;
            EnumChildWindows(pane, (h, l) =>
            {
                if (++n > MaxChildrenPerTop) return false;
                RECT r;
                GetWindowRect(h, out r);
                sb.AppendLine("    子控件#" + n + " class=" + ClassOf(h) + " id=" + GetDlgCtrlID(h) +
                    " 文字=[" + TextOf(h) + "] 矩形=" + r.Left + "," + r.Top + " " +
                    (r.Right - r.Left) + "x" + (r.Bottom - r.Top) +
                    " 相对pane=(" + (r.Left - pr.Left) + "," + (r.Top - pr.Top) + ")");
                return true;
            }, IntPtr.Zero);
            if (n == 0)
                sb.AppendLine("    (pane 子树没有任何子窗口：面板条目全是自绘，只能靠 pane 根 MSAA 或偏移点)");
        }

        private static void DumpToolbar(StringBuilder sb, IntPtr tb)
        {
            int n;
            try { n = SendMessage(tb, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32(); }
            catch { return; }
            sb.AppendLine("        [工具条] 按钮数=" + n);
            for (int i = 0; i < n && i < 64; i++)
            {
                try
                {
                    TBBUTTON b;
                    SendMessageTb(tb, TB_GETBUTTON, (IntPtr)i, out b);
                    sb.AppendLine("          #" + i + " id=" + b.iCommand +
                        " state=0x" + b.fsState.ToString("X2") +
                        " style=0x" + b.fsStyle.ToString("X2") +
                        " text=[" + ToolbarText(tb, b.iCommand) + "]");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("          #" + i + " 读取失败：" + ex.Message);
                    return;
                }
            }
        }

        private static string ToolbarText(IntPtr tb, int cmd)
        {
            char[] buf = new char[258];
            buf[0] = (char)256;   // 缓冲区首字（int）= 容量（字符数），小端
            buf[1] = (char)0;
            GCHandle gh = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                int len = SendMessage(tb, TB_GETBUTTONTEXTW, (IntPtr)cmd,
                    gh.AddrOfPinnedObject()).ToInt32();
                if (len <= 0 || len > 256) return "";
                return new string(buf, 0, len);
            }
            finally { gh.Free(); }
        }
    }

    /// <summary>
    /// 「替换零件/装配」面板的代点链：面板出现 → 点「导入零件/装配」→ 文件框（PopupAutoYes 填路径点打开）
    /// → 面板确定 → 「重新附加约束」命令条确定。每一步都允许手工补做，成功只认外链真的换成新 ics。
    /// </summary>
    [ComVisible(false)]
    internal static class ReplacePanelDriver
    {
        /// <summary>由 MainForm 注入（WaitUntil 的静默版，带 DoEvents 与停止检查）。</summary>
        public static Func<string, int, Func<bool>, int, bool> Wait;
        /// <summary>由 MainForm 注入的 UI 日志。</summary>
        public static Action<string> Log;

        private static readonly string[] ApplyTexts = { "确定", "应用", "替换", "完成" };
        // 「重新附加约束」在替换面板的「选项」里也是复选框文字，只有「约束元素」是这一步独有的
        private static readonly string AttachFingerprint = UiWin.Normalize("约束元素");
        private static readonly string ImportFingerprint = UiWin.Normalize("导入零件/装配");

        /// <summary>
        /// 连续「pane 子树里问不到面板指纹」的次数。面板是 pane 里的一页，一次问不到多半是时机问题，
        /// 必须连问两次 + pane 仍在，才允许判定「面板已关」（实机 14:37 就是被一次扫漏骗进 120 秒等待）。
        /// </summary>
        private static int _panelMisses;

        /// <summary>pane 子树的 MSAA 节点配额（实机：pane 子树几百个节点，框架整树 2841~3.6 万）。</summary>
        private const int PaneMsaaNodes = 1200;

        /// <summary>
        /// 面板指纹查找：一律先问「属性」pane 子树——一次 EnumChildWindows + 一次 **以 pane 为根**的
        /// MSAA；allowGlobal=true 时才再做一次全局窗口文字兜底（命中后把 pane 锚点重挂过去）。
        /// 为什么 MSAA 必须以 pane 为根：Walk 只沿 IAccessible 的孩子下钻、不跨子窗口句柄，
        /// 从框架根扫 2841 个节点也扫不到 pane 里的条目（实机 14:37 就是这么把还开着的面板判成
        /// 「不存在」，接着误入 120 秒权威等待——用户看到的「卡住」正是这里）。
        /// 同理，框架根扫到的第一个「确定」是别处的按钮，点它等于什么都没做。
        /// </summary>
        private static bool FindBoth(string needleNorm, bool allowGlobal,
            out UiCtrl ctrl, out MsaaNode node)
        {
            ctrl = null;
            node = null;
            IntPtr pane = UiWin.PropertyPane();
            if (pane != IntPtr.Zero)
            {
                ctrl = UiWin.FindInSubtree(pane, needleNorm);
                if (ctrl != null) return true;
                node = Msaa.FirstIn(pane, new string[] { needleNorm }, false, PaneMsaaNodes);
                if (node != null) return true;
            }
            if (!allowGlobal) return false;
            UiCtrl global = UiWin.FindByTextLike(needleNorm);
            if (global == null) return false;
            ctrl = global;
            UiWin.ReanchorPane(global.Hwnd);
            return true;
        }

        /// <summary>
        /// 提交类按钮的候选文字（MSAA 口径，一次问完避免重扫）。
        /// 这里用**包含**匹配而不是精确匹配：`Normalize` 会把「确定(&O)」洗成「确定(o)」，
        /// 精确匹配会漏；而搜索范围已经是「属性」pane 子树（几百个节点），
        /// 名字里带「确定/应用/完成」的条目不可能来自别处。
        /// 「替换」被剔除：pane 里有个「替换为」标签，名字包含「替换」却点不动，白等 15 秒。
        /// </summary>
        private static readonly string[] ApplyNormalized = {
            UiWin.Normalize("确定"), UiWin.Normalize("应用"), UiWin.Normalize("完成")
        };

        /// <summary>
        /// 面板已关闭、提交已被接住，但外链还在换 —— 不再重投提交动作，只做权威等待（期间手工补做同样算数）。
        /// </summary>
        private static void FinishAfterCommit(string stage, string newBase, Func<bool> isDone)
        {
            Log("    " + stage + "：面板已关闭、提交已被接住，外链还在换 → 直接转 ⑥ 权威等待");
            DiffRenameTool.CheckStop();

            // ⑥ 取证：每 3 秒一行进度（轮次 / 命中 / 走查节点 / 零部件 / QI失败 / 是否被上限截断）。
            // 只加日志，不改判定——11-03 那两轮 88 s、15 s 的空转就是因为整段一行输出都没有。
            int tick = 0;
            DateTime nextLine = DateTime.Now.AddSeconds(3);
            Func<bool> observed = delegate
            {
                tick++;
                bool ok = isDone();
                if (ok || DateTime.Now >= nextLine)
                {
                    nextLine = DateTime.Now.AddSeconds(3);
                    Diagnostics.Trace(string.Format(
                        "⑥ 取证 轮={0} 命中={1} 走查节点={2} 零部件={3} QI失败={4} 截断={5}",
                        tick, ok ? "Y" : "N",
                        CaxaAutomation.LastWalkNodes, CaxaAutomation.LastWalkParts,
                        CaxaAutomation.LastWalkQiFail, CaxaAutomation.LastWalkTruncated));
                }
                return ok;
            };

            Log("    ⑥ 等外链换成 " + newBase + ".ics（最长 20 秒，判据遍历设计树，不用再点零部件）");
            // 120 秒 → 20 秒：成功样本实测 ≤20 秒（11-03 09:09 那轮外链在 6 秒内就换好了）。
            // 原来的 120 秒是给「判据读不到」兜底，现在根层优先判据已是毫秒级，
            // 再等满 120 秒只会让用户看着一个已经不动的进度空耗。
            if (Wait("⑥ 等外链换成 " + newBase + ".ics", 20000, observed, 500))
            {
                Log("    ⑥ 外链已换成 " + newBase + ".ics ✔");
                return;
            }
            UiWin.Dump("提交后面板已关但外链仍未替换");
            throw new Exception("提交动作已生效（面板关闭）但外链始终没换成 " + newBase + ".ics。\n" +
                "请把跟踪日志 " + Diagnostics.LogPath + " 发回来定位（取证块就在它末尾）。");
        }

        /// <summary>
        /// 试完一条通道后看副作用：外链换了才算这条真起了作用。
        /// 实机 14:22 的教训：MSAA 点中「确定」之后 CAXA 换链接 + 重新附加约束跑了近 30 秒，
        /// 4 秒窗口里看不到副作用就继续往下投，等于对着已经提交的面板盲投 IDOK/回车。
        /// 所以现在等到 15 秒，并且把「面板已消失」也当作这一步走完了——剩下的交给 ⑥ 的 120 秒权威等待。
        /// </summary>
        private static bool Settled(string stage, Func<bool> isDone, Func<bool> panelGone)
        {
            int tick = 0;
            DateTime nextLine = DateTime.Now.AddSeconds(3);
            bool done = Wait(stage, 15000, delegate
            {
                tick++;
                bool ok = isDone();
                if (DateTime.Now >= nextLine)
                {
                    // ④ 取证：这条分支到底是被「外链已换」还是被「面板已关」判成立的
                    nextLine = DateTime.Now.AddSeconds(3);
                    Diagnostics.Trace(string.Format(
                        "④ 取证[{0}] 轮={1} 外链已换={2} 走查节点={3} 截断={4}",
                        stage, tick, ok ? "Y" : "N",
                        CaxaAutomation.LastWalkNodes, CaxaAutomation.LastWalkTruncated));
                }
                if (ok) return true;
                return panelGone != null && panelGone();
            }, 600);
            if (done)
                Log(isDone() ? "    ✔ " + stage + " 之后外链已换成新 ics"
                             : "    ✔ " + stage + " 之后面板已关闭，判定提交已被接住，转 ⑥ 权威等待");
            else Diagnostics.Trace("ReplacePanel: " + stage + " 无副作用");
            return done;
        }

        /// <summary>
        /// 提交当前面板。五条通道按「精度高、代价小」排序，每试一条只看权威判据（外链是否已换）：
        ///   ① pane 子树里带文字的确定类按钮（真对话框才有，面板一般没有）
        ///   ② **以 pane 为根**的 MSAA「确定/应用/替换/完成」——14:22 成功那轮走的就是这条路
        ///   ③ 按 pane 左上偏移点绿色 ✓（✓/✗ 没有 HWND、命令号也没有，只能几何点，偏差风险最高，
        ///     故排在 MSAA 之后；三个候选偏移依次试，每次之后都重新看权威判据）
        ///   ④ 沿 pane 父链投 IDOK ⑤ 焦点窗口回车
        /// 不再往主框架盲投 IDOK/回车：框架级消息作用在 CAXA 自己的界面上，不是这个面板，
        /// 而且面板已经提交时再投等于乱按（实机 14:22 的教训：提交后 CAXA 要跑近 30 秒）。
        /// </summary>
        private static bool TryAccept(string stage, IntPtr pane,
            Func<bool> isDone, Func<bool> panelGone)
        {
            DiffRenameTool.CheckStop();
            if (pane == IntPtr.Zero)
            {
                Log("    " + stage + "：定位不到「属性」pane，代点退回不了——请手工点一次面板里的确定/✓");
                Diagnostics.Log("ReplacePanel: " + stage + " 没有 pane 锚点");
                return false;
            }

            UiCtrl btn = UiWin.FindButtonInSubtree(pane, ApplyTexts);
            if (btn != null && UiWin.Click(btn, stage + " pane 内确定按钮"))
                if (Settled(stage + " pane 内确定按钮", isDone, panelGone)) return true;

            MsaaNode mn = Msaa.FirstIn(pane, ApplyNormalized, false, PaneMsaaNodes);
            if (mn != null && Msaa.Click(mn, stage + " pane MSAA 确定条目"))
                if (Settled(stage + " pane MSAA 确定条目", isDone, panelGone)) return true;

            for (int i = 0; i < UiWin.AcceptOffsetCount; i++)
            {
                if (!UiWin.ClickPaneAccept(pane, stage + " pane 绿勾#" + i, i)) continue;
                if (Settled(stage + " pane 绿勾#" + i, isDone, panelGone)) return true;
            }

            UiWin.PostIdokToAncestors(pane, stage + " pane 父链 IDOK");
            if (Settled(stage + " pane 父链 IDOK", isDone, panelGone)) return true;

            UiWin.EnterToFocus(stage + " 焦点回车");
            return Settled(stage + " 焦点回车", isDone, panelGone);
        }

        /// <summary>两个通道都在 pane 子树里命中时优先用窗口句柄：`BM_CLICK` 是实机验证过的（13:50）。</summary>
        private static bool ClickEither(UiCtrl ctrl, MsaaNode node, string why)
        {
            if (ctrl != null) return UiWin.Click(ctrl, why);
            return node != null && Msaa.Click(node, why);
        }

        private static void LogHit(string role, UiCtrl ctrl, MsaaNode node)
        {
            if (ctrl != null)
                Log("    " + role + "：命中「" + ctrl.Text + "」（窗口 " + ctrl.TopTitle + "）");
            else if (node != null)
                Log("    " + role + "：命中「" + node.Name + "」动作=[" + node.Action + "]");
        }

        private static void Require()
        {
            if (Wait == null || Log == null)
                throw new InvalidOperationException("ReplacePanelDriver 未接线（Wait/Log 为空）。");
        }

        /// <param name="newIcs">要替换成的新 ics 全路径（文件框里由 PopupAutoYes 填入）。</param>
        /// <param name="isDone">权威判据：设计树里是否已有元素外链到该 ics（不读活选择）。</param>
        public static void Run(string newIcs, Func<bool> isDone)
        {
            Require();
            DiffRenameTool.CheckStop();
            _panelMisses = 0;
            string newBase = Path.GetFileNameWithoutExtension(newIcs);

            if (isDone()) { Log("    外链已经是 " + newBase + ".ics，无需替换"); return; }

            // ① 面板出现：以「导入零件/装配」为指纹（面板标题会随停靠状态变，条目文字最稳）。
            //    实机 13:50：该条目是真 HWND（class=Button），所以窗口文字为主、MSAA 兜底。
            //    第 1 次允许全局快照兜底（顺带把 pane 锚点重挂到面板真正所属的 pane），
            //    之后每 5 次轮询才兜底一次，其余只问 pane 子树。
            Log("    ① 等「替换零件/装配」面板出现（指纹＝「导入零件/装配」，窗口文字优先）");
            UiCtrl import = null;
            MsaaNode importNode = null;
            int[] polls = new int[1];
            if (!Wait("① 等替换面板", 15000, delegate
            {
                if (FindBoth(ImportFingerprint, polls[0]++ % 5 == 0, out import, out importNode))
                    return true;
                return isDone();
            }, 400))
            {
                UiWin.Dump("替换面板未出现");
                throw new Exception("投递命令后 15 秒没等到「导入零件/装配」。\n" +
                    "先看跟踪日志里 PostReplacePartCommand: hwnd 是不是 Afx: 主框架" +
                    "（13:50 实机为 0x670E1E，面板随即出现）；若还是 WindowsForms10.* 说明装的是旧 DLL。\n" +
                    "hwnd 正确却仍不出面板，才是真的没有活的选中件（先在设计树里选中一个带外链的零部件）。\n" +
                    "取证块已写进跟踪日志：" + Diagnostics.LogPath);
            }
            if (import == null && importNode == null)
            {
                Log("    ① 面板未出现但外链已换好，跳过后续代点");
                return;
            }
            LogHit("①", import, importNode);

            // ② 点「导入零件/装配」→ 弹文件框
            DiffRenameTool.CheckStop();
            if (!ClickEither(import, importNode, "打开文件框"))
                Log("    警告：点不动「导入零件/装配」，请手工点一次，后面每一步手工补做都算数");

            // ③ 文件框：PopupAutoYes 负责填 零件B.ics 并点「打开」。
            //    实机 14:37 取证：文件框 27.561 秒被自动点掉，而这里还在等它「出现」，
            //    等满 20 秒才超时——它点得比第一轮轮询还快。改成直接看代点计数增量。
            Log("    ② 已点「导入零件/装配」，等文件框（会自动填入 " + newBase + ".ics 并点打开）");
            int clicksBefore = PopupAutoYes.ClickCount;
            bool handled = Wait("③ 等文件框自动点「打开」", 20000, delegate
            {
                return PopupAutoYes.ClickCount > clicksBefore || isDone();
            }, 200);
            Log(handled
                ? "    文件框已由自动代点处理（代点计数 " + clicksBefore + "→" +
                  PopupAutoYes.ClickCount + "）"
                : "    警告：20 秒没看到文件框被点掉（可能面板记住了上次路径直接生效），继续看外链");

            if (isDone()) { Log("    外链已换，替换生效"); return; }

            // ④ 提交替换面板（选完文件后停在属性页顶部的绿色 ✓）
            //   「两张面板都不在、文件框也关着」＝提交动作已经被接住：实机 14:22 就是
            //   「确定」点完 30 秒后外链才换，期间不该再往框架上盲投 IDOK/回车。
            //   判「面板已关」：热路径只问 pane 子树；每 5 次轮询（约 3 秒）才做一次全局窗口文字复查，
            //   且只有**连续两次全局复查都问不到**才允许说「关」——14:37 就是拿一次扫漏当了证据。
            int[] gonePolls = new int[1];
            Func<bool> panelGone = delegate
            {
                if (UiWin.FileDialogOpen()) { _panelMisses = 0; return false; }
                bool global = gonePolls[0]++ % 5 == 0 || !UiWin.PaneAlive();
                UiCtrl c; MsaaNode n;
                if (FindBoth(ImportFingerprint, global, out c, out n)) { _panelMisses = 0; return false; }
                if (FindBoth(AttachFingerprint, global, out c, out n)) { _panelMisses = 0; return false; }
                if (!global) return false;
                return ++_panelMisses >= 2;
            };
            if (TryAccept("④（替换面板提交）", UiWin.PropertyPane(), isDone, panelGone))
            {
                if (isDone()) return;      // Settled 已打过「✔ … 之后外链已换成新 ics」
                // 面板没了但外链还没换＝CAXA 还在换链接/重算约束，别再投第二遍提交
                FinishAfterCommit("④", newBase, isDone);
                return;
            }

            // ⑤ 重新附加约束界面：指纹取「约束元素」（面板 选项 里也有「重新附加约束」复选框，不能用它）。
            //    实机取证：普通零件根本不弹这一页，所以这步「没等到」是正常分支，不再据此再跑一遍提交梯子。
            UiCtrl attach = null;
            MsaaNode attachNode = null;
            bool attachSeen = false;
            int[] polls5 = new int[1];
            Wait("⑤ 等「约束元素」界面", 6000, delegate
            {
                if (isDone()) return true;
                attachSeen = FindBoth(AttachFingerprint, polls5[0]++ % 5 == 0,
                    out attach, out attachNode);
                return attachSeen;
            }, 300);
            if (isDone()) { Log("    外链已换，替换生效"); return; }

            if (attachSeen)
            {
                LogHit("⑤", attach, attachNode);
                UiWin.Dump("重新附加约束界面");
                _panelMisses = 0;
                if (TryAccept("⑤（重新附加约束提交）", UiWin.PropertyPane(), isDone, panelGone))
                {
                    if (isDone()) { Log("    ⑤ 外链已换，替换生效"); return; }
                    FinishAfterCommit("⑤", newBase, isDone);
                    return;
                }
            }
            else Log("    没有「约束元素」界面（普通零件本来就不弹这一页），直接等外链");

            // ⑥ 权威判定：外链真的换成新 ics 才算成功（判据遍历设计树，不读活选择）
            DiffRenameTool.CheckStop();
            Log("    ⑥ 等外链换成 " + newBase + ".ics（最长 120 秒，判据遍历设计树，不用再点零部件）");
            bool swapped = Wait("⑥ 等外链换成 " + newBase + ".ics", 120000, isDone, 500);
            if (!swapped)
            {
                UiWin.Dump("代点链走完仍未替换");
                throw new Exception("自动代点走完仍未在设计树里找到外链指向 " + newBase + ".ics 的元素。\n" +
                    "面板还差一次确定/✓ 的话，现在手工点一次即可（判据遍历设计树，不要求选中件）。\n" +
                    "请看设计树里是否已经是 \"" + newBase + ".ics - …\"，并把跟踪日志 " +
                    Diagnostics.LogPath + " 发回来定位（取证块就在它末尾）。");
            }
            Log("    ⑥ 外链已换成 " + newBase + ".ics ✔");
        }
    }
}
