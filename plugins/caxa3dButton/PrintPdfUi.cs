// 「打印pdf」UI 代点（2026-11-11 参考「另存为dwg」定案）：
//   打开/保存/关闭全部沿用 SaveAsDwgUi；中间那步「另存为 dwg」换成**打印对话框**代点。
//
// 打印命令来源（不猜）：CAXADraft\Config\zh-CN\ICIntegrate\Commands.xml
//   <Identify>CX_print</Identify> <CLSID>{B0D493C6-DA5D-47A8-8894-C87E004BA08C}</CLSID>
//   <Priority>CMDPRIORITY_MODAL</Priority> <LocalName>打印</LocalName>
//   ⇒ 走 CRX 桥 EXEC_NATIVE（白名单前缀 CX_ 放行），由图纸进程自己开框。
//
// 对话框（实机截图 11-11）：标题「打印对话框」，打印机名选择「EXB To PDF.drv」，
// 纸张大小 = 图幅，图形方向 = 横向，黑白打印 = 勾选，输出图形 = 标准图形。
// ★ 控件不硬编码未实测的 id：一律按**文字/下拉条目内容**识别；首次到框把整棵控件树 +
//   MSAA 清单写 trace（UiWin.DumpWindow），下一次按证据收窄，不靠估算。
// 跨进程安全口径同 SaveAsDwgUi：WM_GETTEXT / WM_SETTEXT / CB_GETCOUNT / CB_SETCURSEL /
//   BM_GETCHECK / BM_CLICK / PostMessage(WM_COMMAND)；绝不用 CB_GETLBTEXT 跨进程读条目
//   （缓冲区指针不封送），用「CB_SETCURSEL 逐项 + WM_GETTEXT 读选中文本」替代。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace caxa3dButton
{
    internal static class PrintPdfUi
    {
        private const uint WM_SETTEXT = 0x000C;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_GETTEXTLENGTH = 0x000E;
        private const uint WM_COMMAND = 0x0111;
        private const uint CB_GETCOUNT = 0x0146;
        private const uint CB_GETCURSEL = 0x0147;
        private const uint CB_SETCURSEL = 0x014E;
        private const uint CB_SHOWDROPDOWN = 0x014F;
        private const uint BM_GETCHECK = 0x00F0;
        private const uint BM_CLICK = 0x00F5;
        private const int CBN_SELCHANGE = 1;
        private const int CBN_CLOSEUP = 8;
        private const int CBN_SELENDOK = 9;

        private const byte VK_CONTROL = 0x11, VK_P = 0x50;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageSb(IntPtr h, uint msg, IntPtr w, StringBuilder l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageStr(IntPtr h, uint msg, IntPtr w, string l);
        [DllImport("user32.dll", EntryPoint = "EnumWindows")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "EnumChildWindows")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr h);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);

        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>一个子控件快照（文字已按跨进程安全口径 WM_GETTEXT 取）。</summary>
        private sealed class Ctl
        {
            public IntPtr H;
            public string Cls = "";
            public string Txt = "";
            public int Id;
            public bool Enabled;
        }

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

        private static string Norm(string s) { return UiWin.Normalize(s); }

        /// <summary>
        /// 本模块全部跑在后台桥任务线程（RunBridgeTask），只做跨进程消息 + 文件轮询，
        /// 不碰本进程 UI；主线程的模态/等待一律由 MainForm 的 WaitUntil+DoEvents 负责。
        /// </summary>

        // ============================================================ 进程白名单

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

        /// <summary>本进程 + 图纸进程（2027=CAXADraft / 2024=CDRAFT_M_IC，两个都认）。</summary>
        private static bool WatchedPid(uint pid)
        {
            if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return true;
            string n = ProcNameOf(pid);
            return string.Equals(n, "CAXADraft", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(n, "CDRAFT_M_IC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(n, "IronCAD", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================ 找打印对话框

        /// <summary>打印对话框判据：可见顶层 + 白名单进程 + 标题含「打印对话框」/等于「打印」。</summary>
        public static IntPtr FindPrintDialogOnce()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!WatchedPid(pid)) return true;
                string raw = TitleOf(h);
                if (raw.Length == 0) return true;
                if (raw.IndexOf(" - [", StringComparison.Ordinal) >= 0) return true;   // 文档主窗口
                string t = Norm(raw);
                bool hit = t.IndexOf("打印对话框", StringComparison.Ordinal) >= 0 ||
                           t == "打印" ||
                           (t.IndexOf("打印", StringComparison.Ordinal) >= 0 &&
                            t.IndexOf("打印到", StringComparison.Ordinal) < 0 &&
                            t.IndexOf("打印设置", StringComparison.Ordinal) < 0);
                if (!hit) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>等打印对话框出现（打开命令已投递后调用）。</summary>
        public static IntPtr WaitPrintDialog(int timeoutMs, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int lastLogged = -1;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                IntPtr dlg = FindPrintDialogOnce();
                if (dlg != IntPtr.Zero)
                {
                    if (log != null)
                        log("打印对话框已出现（" + (int)sw.ElapsedMilliseconds + "ms）class=" + ClassOf(dlg) +
                            " 标题=[" + TitleOf(dlg) + "]");
                    return dlg;
                }
                int el = (int)sw.ElapsedMilliseconds;
                if (log != null && el - lastLogged >= 5000)
                {
                    lastLogged = el;
                    log("等打印对话框 " + el + "ms");
                }
                Thread.Sleep(300);
            }
            if (log != null) log("打印对话框未出现（" + timeoutMs + "ms）：" + VisibleTopDump());
            return IntPtr.Zero;
        }

        /// <summary>超时取证：白名单进程所有可见顶层窗口（类名+标题）。</summary>
        private static string VisibleTopDump()
        {
            var sb = new StringBuilder();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!WatchedPid(pid)) return true;
                string t = TitleOf(h);
                if (t.Length == 0) return true;
                sb.Append(ClassOf(h)).Append('[').Append(t).Append("] ");
                return true;
            }, IntPtr.Zero);
            return sb.Length == 0 ? "（无可见顶层窗口）" : sb.ToString();
        }

        /// <summary>键盘兜底：Ctrl+P（前端窗口须为图纸；SetForegroundWindow 可能被系统限制，尽力而为）。</summary>
        public static void TriggerPrintByShortcut(IntPtr frame, Action<string> log)
        {
            try
            {
                if (frame != IntPtr.Zero) SetForegroundWindow(frame);
                Thread.Sleep(300);
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_P, 0, 0, UIntPtr.Zero);
                keybd_event(VK_P, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (log != null) log("打印命令未开出对话框，已发键盘 Ctrl+P 兜底");
            }
            catch (Exception ex)
            {
                if (log != null) log("Ctrl+P 兜底异常：" + ex.Message);
            }
        }

        /// <summary>窗口是否仍可见（关图前确认打印对话框已退出，避免在模态里动文档）。</summary>
        public static bool IsVisibleWindow(IntPtr h)
        {
            try { return h != IntPtr.Zero && IsWindowVisible(h); }
            catch { return false; }
        }

        // ============================================================ 控件枚举

        private static List<Ctl> Children(IntPtr root)
        {
            var list = new List<Ctl>();
            int guard = 0;
            EnumChildWindows(root, delegate(IntPtr h, IntPtr l)
            {
                if (++guard > 4000) return false;
                if (h == root) return true;
                list.Add(new Ctl
                {
                    H = h,
                    Cls = ClassOf(h),
                    Txt = TextViaMsg(h),
                    Id = GetDlgCtrlID(h),
                    Enabled = IsWindowEnabled(h)
                });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static bool IsComboClass(string cls)
        {
            return cls.IndexOf("Combo", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsButtonClass(string cls)
        {
            return cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>逐项选过去读文本（CB_SETCURSEL 不发通知，安全；读完恢复原选中项）。</summary>
        private static List<string> ComboItems(IntPtr combo, out int selected, out int count)
        {
            selected = SendMessage(combo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt32();
            count = SendMessage(combo, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
            var items = new List<string>();
            for (int i = 0; i < count && i < 200; i++)
            {
                SendMessage(combo, CB_SETCURSEL, (IntPtr)i, IntPtr.Zero);
                items.Add(TextViaMsg(combo));
            }
            if (count > 0)
                SendMessage(combo, CB_SETCURSEL, (IntPtr)(selected >= 0 && selected < count ? selected : 0),
                            IntPtr.Zero);
            return items;
        }

        /// <summary>设置下拉选中项并补发 CBN_* 通知（对话框自己的选择处理器才会跑）。</summary>
        private static void SetComboIndex(IntPtr dlg, IntPtr combo, int idx)
        {
            SendMessage(combo, CB_SETCURSEL, (IntPtr)idx, IntPtr.Zero);
            int cid = GetDlgCtrlID(combo);
            PostMessage(dlg, WM_COMMAND, (IntPtr)(((CBN_SELCHANGE & 0xFFFF) << 16) | (cid & 0xFFFF)), combo);
            PostMessage(dlg, WM_COMMAND, (IntPtr)(((CBN_SELENDOK & 0xFFFF) << 16) | (cid & 0xFFFF)), combo);
            PostMessage(dlg, WM_COMMAND, (IntPtr)(((CBN_CLOSEUP & 0xFFFF) << 16) | (cid & 0xFFFF)), combo);
        }

        /// <summary>MSAA 兜底：放下下拉 → 按名字找条目 → accDoDefaultAction（真实双击）。</summary>
        private static bool SelectComboViaMsaa(IntPtr dlg, IntPtr combo, string wantText, Action<string> log)
        {
            try
            {
                string want = Norm(wantText);
                if (want.Length == 0) return false;
                SendMessage(combo, CB_SHOWDROPDOWN, (IntPtr)1, IntPtr.Zero);
                Thread.Sleep(300);
                MsaaNode pick = null;
                foreach (IntPtr root in new IntPtr[] { combo, dlg })
                {
                    foreach (MsaaNode it in Msaa.AllIn(root, 4000))
                    {
                        string n = Norm(it.Name ?? "");
                        if (n.Length > 0 && n == want) { pick = it; break; }
                    }
                    if (pick != null) break;
                }
                if (pick == null)
                {
                    SendMessage(combo, CB_SHOWDROPDOWN, IntPtr.Zero, IntPtr.Zero);
                    return false;
                }
                bool ok = Msaa.Click(pick, "打印对话框选下拉条目（MSAA）");
                Thread.Sleep(300);
                SendMessage(combo, CB_SHOWDROPDOWN, IntPtr.Zero, IntPtr.Zero);
                if (log != null) log("MSAA：已代选条目「" + pick.Name + "」ok=" + ok);
                return ok;
            }
            catch (Exception ex)
            {
                try { SendMessage(combo, CB_SHOWDROPDOWN, IntPtr.Zero, IntPtr.Zero); } catch { }
                if (log != null) log("MSAA 选条目异常：" + ex.Message);
                return false;
            }
        }

        // ============================================================ 图幅 / 纸张

        // CAXA 2027 实机：「当前幅面:」与「A3」拆成两个独立 Static（取证 id=2992 / 2993），
        // 后面跟着的是「加长系数: 0 / 宽度 420mm / 高度 297mm / 宽高比 1.41」——都是干扰。
        private static readonly Regex PurePaper = new Regex(@"^[A-Za-z]{1,2}\s?\d{1,2}$");

        /// <summary>
        /// 识别图幅：先找文字恰等於「当前幅面:」的 Static，取它后面紧挨着那个纯纸幅格式的 Static（2027 实机 = id=2992 后的 id=2993=「A3」）。
        /// 读不到返回空（第三态）；图幅未识别时调用方应退回兜底并记账。
        /// </summary>
        public static string ReadSheetSize(IntPtr dlg, Action<string> log)
        {
            List<Ctl> all = Children(dlg);
            // 先定位锚点 Static：文字=「当前幅面:」或「当前幅面」
            int anchorIdx = -1;
            for (int i = 0; i < all.Count; i++)
            {
                string t = all[i].Txt.Trim();
                if (all[i].Cls.IndexOf("Static", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t == "当前幅面:" || t == "当前幅面") { anchorIdx = i; break; }
            }
            if (anchorIdx < 0)
            {
                if (log != null) log("图幅锚点 Static（当前幅面:）未找到 —— 控件树遍历里没有这个文字");
                return "";
            }
            // 从锚点往后找第一个纯纸幅格式的 Static（跳过「加长系数: 0 / 宽度 420mm / 高度 297mm」等干扰）
            for (int j = anchorIdx + 1; j < all.Count; j++)
            {
                Ctl c = all[j];
                if (c.Cls.IndexOf("Static", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string t = c.Txt.Trim();
                if (PurePaper.IsMatch(t))
                {
                    if (log != null) log("识别图幅（相邻 Static）：锚点=[" + all[anchorIdx].Txt + "] 命中=[" + t + "] id=" + c.Id);
                    return t.Replace(" ", "");
                }
                // 碰到加长系数 / 宽度 / 高度 / 宽高比这类干扰，且当前控件文字看起来不像是纸幅，直接停（不再往后扫了）
                if (t.Length > 0 && !PurePaper.IsMatch(t) &&
                    (t.IndexOf("加长", StringComparison.Ordinal) >= 0 ||
                     t.IndexOf("宽度", StringComparison.Ordinal) >= 0 ||
                     t.IndexOf("高度", StringComparison.Ordinal) >= 0 ||
                     t.IndexOf("宽高比", StringComparison.Ordinal) >= 0))
                    break;
            }
            // 再来一手兜底：锚点所在行附近、id 紧跟锚点、且文字是纯纸幅的任何 Static（跨 id 不连续的情况）
            int anchorId = all[anchorIdx].Id;
            for (int j = 0; j < all.Count; j++)
            {
                if (j == anchorIdx) continue;
                Ctl c = all[j];
                if (c.Cls.IndexOf("Static", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (c.Id <= anchorId || c.Id > anchorId + 50) continue;
                string t = c.Txt.Trim();
                if (PurePaper.IsMatch(t))
                {
                    if (log != null) log("识别图幅（兜底按 id 邻域）：锚点 id=" + anchorId + " 命中=[" + t + "] id=" + c.Id);
                    return t.Replace(" ", "");
                }
            }
            if (log != null) log("图幅未识别（锚点 Static 后面没有纸幅格式的 Static）");
            return "";
        }

        private static bool LooksLikePaper(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return Regex.IsMatch(s.Trim(), @"^[A-Za-z]{1,2}\s?\d{1,2}$");
        }

        /// <summary>
        /// 选 PDF 打印机：条目含 EXB To PDF 优先，其次含 PDF。
        /// 选不到就返回 false（绝不把图纸打到物理打印机上）。
        /// </summary>
        private static bool SelectPrinter(IntPtr dlg, Action<string> log, out IntPtr printerCombo)
        {
            printerCombo = IntPtr.Zero;
            IntPtr fallbackCombo = IntPtr.Zero, pdfCombo = IntPtr.Zero;
            int fallbackIdx = -1, pdfIdx = -1;
            string fallbackTxt = "", pdfTxt = "";
            var trace = new StringBuilder();

            foreach (Ctl c in Children(dlg))
            {
                if (!IsComboClass(c.Cls)) continue;
                int sel, n;
                List<string> items = ComboItems(c.H, out sel, out n);
                if (n <= 0) continue;
                trace.Append("combo0x").Append(c.H.ToInt64().ToString("X")).Append(" n=").Append(n)
                     .Append(" 选中=").Append(sel >= 0 && sel < items.Count ? items[sel] : "")
                     .Append(" 条目=");
                for (int i = 0; i < items.Count && i < 24; i++) trace.Append('[').Append(i).Append(']').Append(items[i]).Append(' ');
                trace.AppendLine();

                for (int i = 0; i < items.Count; i++)
                {
                    string ln = Norm(items[i]);
                    if (pdfCombo == IntPtr.Zero && ln.Contains("exbtopdf")) { pdfCombo = c.H; pdfIdx = i; pdfTxt = items[i]; }
                    if (fallbackCombo == IntPtr.Zero && ln.Contains("pdf")) { fallbackCombo = c.H; fallbackIdx = i; fallbackTxt = items[i]; }
                }
                if (pdfCombo != IntPtr.Zero) break;
            }

            IntPtr combo = pdfCombo != IntPtr.Zero ? pdfCombo : fallbackCombo;
            int idx = pdfCombo != IntPtr.Zero ? pdfIdx : fallbackIdx;
            string chosen = pdfCombo != IntPtr.Zero ? pdfTxt : fallbackTxt;
            if (combo == IntPtr.Zero)
            {
                if (log != null) log("打印机下拉里没有 PDF 条目（拒绝打到物理打印机）：\r\n" + trace);
                return false;
            }

            printerCombo = combo;
            int cur, cnt;
            ComboItems(combo, out cur, out cnt);
            if (cur == idx)
            {
                if (log != null) log("打印机已是 PDF（无需切换）：[" + chosen + "]");
                return true;
            }

            SetComboIndex(dlg, combo, idx);
            Thread.Sleep(700);
            string now = TextViaMsg(combo);
            if (Norm(now) != Norm(chosen))
            {
                if (log != null) log("消息通道未提交打印机（当前=[" + now + "]），试 MSAA");
                SelectComboViaMsaa(dlg, combo, chosen, log);
                Thread.Sleep(400);
                now = TextViaMsg(combo);
            }
            if (log != null) log("打印机 → [" + now + "]（目标 [" + chosen + "] idx=" + idx + "）");
            return Norm(now) == Norm(chosen) || Norm(now).Contains("pdf");
        }

        /// <summary>
        /// 选纸张大小 = 图幅：在除打印机下拉以外的组合框里找条目。
        /// 条目清单 CAXA 2027 = A0 x 3 / A0 x 2 / A0 / A1 x 4 / ... / A3 x 7 / A3 x 6 / ... / A3 / A4 x 9 / ... / A4 / ...
        /// （加长版按加长系数倒序排在精确条目前面）。
        /// 匹配优先级：精确等于 want > 前缀等于 want 且不含加长版标记「x N」和括号后缀。
        /// 找不到返回 false（宁可不打，也不按错误幅面输出）。
        /// </summary>
        private static bool SelectPaper(IntPtr dlg, IntPtr printerCombo, string sheet, Action<string> log)
        {
            if (sheet.Length == 0) return false;
            string want = Norm(sheet);
            var trace = new StringBuilder();
            IntPtr combo = IntPtr.Zero;
            int idx = -1;
            string chosen = "";
            int hits = 0;

            foreach (Ctl c in Children(dlg))
            {
                if (!IsComboClass(c.Cls)) continue;
                if (c.H == printerCombo) continue;
                int sel, n;
                List<string> items = ComboItems(c.H, out sel, out n);
                if (n <= 0) continue;
                trace.Append("combo0x").Append(c.H.ToInt64().ToString("X")).Append(" n=").Append(n)
                     .Append(" 选中=[").Append(sel >= 0 && sel < items.Count ? items[sel] : "").Append("] 条目=");
                for (int i = 0; i < items.Count && i < 24; i++) trace.Append('[').Append(i).Append(']').Append(items[i]).Append(' ');
                trace.AppendLine();

                // 两级命中：精确等于 > 前缀等于且不含加长/括号后缀
                for (int i = 0; i < items.Count; i++)
                {
                    string ni = Norm(items[i]);
                    if (ni.Length == 0) continue;
                    // 级 1：精确命中
                    if (ni == want) { combo = c.H; idx = i; chosen = items[i]; hits = 1; break; }
                    // 级 2：前缀匹配但排除加长版（A3 x 7）和括号后缀（B4 (JIS)）
                    if (combo == IntPtr.Zero && ni.StartsWith(want, StringComparison.Ordinal) &&
                        !Regex.IsMatch(items[i], @"\s*x\s*\d+\b") &&
                        !items[i].Contains("("))
                    {
                        combo = c.H; idx = i; chosen = items[i]; hits = 2;
                    }
                }
                if (hits == 1) break;   // 精确命中直接收队
            }

            if (combo == IntPtr.Zero)
            {
                // 兜底：某组合框当前选中值就等于图幅（例如对话框本来已按图幅给好纸张）——保持不变
                foreach (Ctl c in Children(dlg))
                {
                    if (!IsComboClass(c.Cls) || c.H == printerCombo) continue;
                    if (Norm(TextViaMsg(c.H)) == want)
                    {
                        if (log != null) log("纸张大小已是图幅 " + sheet + "（无需切换）");
                        return true;
                    }
                }
                if (log != null) log("纸张大小下拉里找不到图幅 " + sheet + "（拒绝按未知幅面打印）：\r\n" + trace);
                return false;
            }

            SetComboIndex(dlg, combo, idx);
            Thread.Sleep(500);
            string now = TextViaMsg(combo);
            if (Norm(now) != Norm(chosen))
            {
                if (log != null) log("消息通道未提交纸张（当前=[" + now + "]），试 MSAA");
                SelectComboViaMsaa(dlg, combo, chosen, log);
                Thread.Sleep(400);
                now = TextViaMsg(combo);
            }
            bool ok = Norm(now) == want || Norm(now) == Norm(chosen);
            if (log != null) log("纸张大小 → [" + now + "]（图幅 " + sheet + "，目标条目 [" + chosen + "] idx=" + idx + "）ok=" + ok);
            return ok;
        }

        // ============================================================ 单选 / 复选

        /// <summary>MSAA 兜底点名字命中的条目。</summary>
        private static bool ClickMsaaByName(IntPtr dlg, string contains, Action<string> log)
        {
            try
            {
                string want = Norm(contains);
                foreach (MsaaNode it in Msaa.AllIn(dlg, 6000))
                {
                    string n = Norm(it.Name ?? "");
                    if (n.Length == 0 || n.IndexOf(want, StringComparison.Ordinal) < 0) continue;
                    bool ok = Msaa.Click(it, "打印对话框按名字代点「" + contains + "」");
                    if (log != null) log("MSAA：命中「" + it.Name + "」动作=[" + it.Action + "] ok=" + ok);
                    return ok;
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("MSAA 代点异常：" + ex.Message);
            }
            return false;
        }

        /// <summary>MSAA 精确名字点击（避免「打印」命中「打印到文件」）；找不到再退回包含匹配。</summary>
        private static bool ClickMsaaExact(IntPtr dlg, string exactName, Action<string> log)
        {
            try
            {
                string want = Norm(exactName);
                MsaaNode hit = null;
                foreach (MsaaNode it in Msaa.AllIn(dlg, 8000))
                {
                    if (Norm(it.Name ?? "") == want) { hit = it; break; }
                }
                if (hit == null) return ClickMsaaByName(dlg, exactName, log);
                bool ok = Msaa.Click(hit, "打印对话框代点「" + exactName + "」（MSAA 精确）");
                if (log != null) log("MSAA 精确命中「" + hit.Name + "」动作=[" + hit.Action + "] ok=" + ok);
                return ok;
            }
            catch (Exception ex)
            {
                if (log != null) log("MSAA 精确代点异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>读某名字条目的 MSAA 状态串（「选中」/「checked」判勾选；读不到返回空＝第三态）。</summary>
        private static string MsaaStateByName(IntPtr dlg, string contains)
        {
            try
            {
                string want = Norm(contains);
                foreach (MsaaNode it in Msaa.AllIn(dlg, 6000))
                {
                    string n = Norm(it.Name ?? "");
                    if (n.Length == 0 || n.IndexOf(want, StringComparison.Ordinal) < 0) continue;
                    return it.State ?? "";
                }
            }
            catch { }
            return "";
        }

        private static bool StateSelected(string state)
        {
            if (string.IsNullOrEmpty(state)) return true;   // 读不到状态：按动作已成功记账（日志另行标注第三态）
            string s = Norm(state);
            if (s.IndexOf("未选中", StringComparison.Ordinal) >= 0) return false;
            if (s.IndexOf("unchecked", StringComparison.Ordinal) >= 0) return false;
            return s.IndexOf("选中", StringComparison.Ordinal) >= 0 ||
                   s.IndexOf("checked", StringComparison.Ordinal) >= 0;
        }

        /// <summary>确保某个单选被选中（文字含 contains）。BM_GETCHECK 读状态，BM_CLICK 选，不成再 MSAA。</summary>
        private static bool EnsureRadio(IntPtr dlg, string contains, Action<string> log)
        {
            foreach (Ctl c in Children(dlg))
            {
                if (!IsButtonClass(c.Cls) || c.Txt.Length == 0) continue;
                string n = Norm(c.Txt);
                if (n.IndexOf(Norm(contains), StringComparison.Ordinal) < 0) continue;
                int st = SendMessage(c.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (st != 0)
                {
                    if (log != null) log("「" + c.Txt + "」已是选中态（check=" + st + "）");
                    return true;
                }
                PostMessage(c.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(250);
                st = SendMessage(c.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (log != null) log("选「" + c.Txt + "」→ check=" + st + " id=" + c.Id);
                if (st != 0) return true;
                if (ClickMsaaByName(dlg, contains, log))
                {
                    st = SendMessage(c.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    if (st != 0) return true;
                    string state = MsaaStateByName(dlg, contains);
                    if (log != null) log("MSAA 状态=[" + state + "]");
                    if (StateSelected(state)) return true;
                }
                return false;
            }
            if (ClickMsaaByName(dlg, contains, log))
            {
                string state = MsaaStateByName(dlg, contains);
                if (log != null) log("MSAA（无文字控件）状态=[" + state + "]");
                if (StateSelected(state)) return true;
            }
            if (log != null) log("找不到文字含「" + contains + "」的单选（第三态：未确认状态）");
            return false;
        }

        /// <summary>确保勾选状态（文字含 contains）；want=false 表示确保取消勾选。</summary>
        private static bool EnsureCheck(IntPtr dlg, string contains, bool want, Action<string> log, bool required)
        {
            foreach (Ctl c in Children(dlg))
            {
                if (!IsButtonClass(c.Cls) || c.Txt.Length == 0) continue;
                string n = Norm(c.Txt);
                if (n.IndexOf(Norm(contains), StringComparison.Ordinal) < 0) continue;
                int st = SendMessage(c.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32();
                bool on = st != 0;
                if (on == want)
                {
                    if (log != null) log("「" + c.Txt + "」已是" + (want ? "勾选" : "未勾选") + "态（check=" + st + "）");
                    return true;
                }
                PostMessage(c.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(250);
                st = SendMessage(c.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (log != null) log((want ? "勾选" : "取消") + "「" + c.Txt + "」→ check=" + st + " id=" + c.Id);
                if ((st != 0) == want) return true;
                if (ClickMsaaByName(dlg, contains, log))
                {
                    st = SendMessage(c.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    if ((st != 0) == want) return true;
                    string state = MsaaStateByName(dlg, contains);
                    if (log != null) log("MSAA 状态=[" + state + "]");
                    if (StateSelected(state) == want) return true;
                }
                return false;
            }
            if (log != null) log("找不到文字含「" + contains + "」的复选框" + (required ? "（第三态：无法确认）" : "（按不勾处理）"));
            return !required;
        }

        // ============================================================ EXB To PDF.drv 属性面板

        /// <summary>
        /// 点打印对话框的「属性」按钮，等属性面板出现。
        /// 属性面板判据：白名单进程 + 可见 + 标题含「EXB To PDF」。
        /// </summary>
        public static IntPtr OpenPropertiesDialog(IntPtr printDlg, int timeoutMs, Action<string> log)
        {
            // ★ 抢在点击属性按钮之前告诉 PopupAutoYes：这张面板我自己处理，你别碰它的「确定」！
            // 2026-11-11 实机：面板刚弹出 PopupAutoYes 就抢点了确定，我们的取消勾选动作还没发出去。
            PopupAutoYes.AddExcludedTitle("EXB To PDF");

            // ① 点「属性」按钮（trace 取证 id=1025，文字=[属性]）
            foreach (Ctl c in Children(printDlg))
            {
                if (!IsButtonClass(c.Cls)) continue;
                string bn = Norm(c.Txt);
                if (!(bn == "属性" || Regex.IsMatch(bn, @"^属性[\(（&]"))) continue;
                var ui = new UiCtrl
                {
                    Hwnd = c.H, Class = c.Cls, Text = c.Txt, Id = c.Id,
                    Top = printDlg, TopTitle = TitleOf(printDlg), TopClass = ClassOf(printDlg), Enabled = c.Enabled
                };
                UiWin.Click(ui, "打印对话框点「属性」");
                if (log != null) log("已点「属性」按钮 id=" + c.Id);
                break;
            }

            // ② 等属性对话框出现
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                IntPtr found = IntPtr.Zero;
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!IsWindowVisible(h)) return true;
                    if (h == printDlg) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (!WatchedPid(pid)) return true;
                    string nt = Norm(TitleOf(h));
                    if (nt.IndexOf("EXB To PDF", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (nt.IndexOf("PDF.drv", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         nt.IndexOf("属性", StringComparison.Ordinal) >= 0))
                    {
                        found = h; return false;
                    }
                    return true;
                }, IntPtr.Zero);
                if (found != IntPtr.Zero)
                {
                    if (log != null) log("属性对话框已出现（" + (int)sw.ElapsedMilliseconds + "ms）标题=[" + TitleOf(found) + "]");
                    return found;
                }
                Thread.Sleep(300);
            }
            if (log != null) log("属性对话框未出现（" + timeoutMs + "ms）：" + VisibleTopDump());
            return IntPtr.Zero;
        }

        /// <summary>
        /// 在属性面板里确保取消「PDF打印结果在查看器中显示」（用户反馈：每次 CAXA 重启此勾选被强制重置，
        /// 所以每次打印都必须处理一次）。点「确定」后返回。
        /// </summary>
        public static bool ConfigurePropertiesDialog(IntPtr propDlg, Action<string> log)
        {
            if (propDlg == IntPtr.Zero) return false;
            UiWin.DumpWindow(propDlg, "PDF属性对话框");   // 首次跑必须取证，落 trace 留底

            // 目标勾选框文字：截图实锤 = 「PDF打印结果在查看器中显示」
            // 其他相关勾选项一并记账（「创建图层信息」「嵌入字体到PDF中」用户没提暂不动）
            bool viewerOk = EnsureCheck(propDlg, "PDF打印结果在查看器中显示", false, log, false);
            // 如果用户后续反馈还需要取消其他勾选项，加在这里

            // 找「确定」按钮（标准 Button 类 id=1 或 id=IDOK=1；MSAA 动作=[按]）
            foreach (Ctl c in Children(propDlg))
            {
                if (!IsButtonClass(c.Cls)) continue;
                string bn = Norm(c.Txt);
                if (!(bn == "确定" || Regex.IsMatch(bn, @"^确定[\(（&]"))) continue;
                var ui = new UiCtrl
                {
                    Hwnd = c.H, Class = c.Cls, Text = c.Txt, Id = c.Id,
                    Top = propDlg, TopTitle = TitleOf(propDlg), TopClass = ClassOf(propDlg), Enabled = c.Enabled
                };
                UiWin.Click(ui, "PDF属性对话框点「确定」");
                if (log != null) log("PDF属性对话框：确定按钮已点 id=" + c.Id + " viewerOk=" + viewerOk);
                return true;
            }
            // 兜底：MSAA 找名字=「确定」
            if (ClickMsaaExact(propDlg, "确定", log)) return true;
            if (log != null) log("PDF属性对话框里找不到「确定」按钮");
            return false;
        }

        /// <summary>等属性对话框点确定后关闭（轮询可见性）。</summary>
        public static bool WaitPropertiesDialogClosed(IntPtr propDlg, int timeoutMs, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && IsVisibleWindow(propDlg))
                Thread.Sleep(250);
            bool closed = !IsVisibleWindow(propDlg);
            if (log != null) log(closed
                ? "PDF属性对话框已关闭（" + (int)sw.ElapsedMilliseconds + "ms）"
                : "PDF属性对话框未关闭（" + timeoutMs + "ms）");
            return closed;
        }

        // ============================================================ 主配置 + 点打印

        /// <summary>
        /// 打印对话框全参数代点：图幅 → PDF 打印机 → **属性面板（取消查看器勾选，仅 configureProperties=true 时）** → 纸张大小 → 黑白打印 → 横向 → 标准图形 → 点「打印」。
        /// 任何一项读不到/设不上就返回 false（不按未知参数输出）；返回 true 时 sheet 为识别到的图幅。
        /// configureProperties：是否进属性面板。批量转换只第一张图 true（驱动属性在同会话内保持），后续传 false 省时间。
        /// </summary>
        public static bool ConfigureAndPrint(IntPtr dlg, out string sheet, Action<string> log, bool configureProperties = true)
        {
            sheet = "";
            if (dlg == IntPtr.Zero) return false;

            UiWin.DumpWindow(dlg, "打印对话框");   // 首次到框：整棵控件树 + MSAA 清单落 trace

            sheet = ReadSheetSize(dlg, log);
            IntPtr printer;
            if (!SelectPrinter(dlg, log, out printer)) return false;
            Thread.Sleep(600);   // 换打印机后纸列表/版式会重排

            // ★ 属性面板：仅 configureProperties=true 时才执行（批量转换第一张图后驱动属性在同会话内保持，
            //  后续图不用重复进属性面板）。失败不中止主流程——记日志继续。
            if (configureProperties)
            {
                try
                {
                    IntPtr propDlg = OpenPropertiesDialog(dlg, 15000, log);
                    if (propDlg != IntPtr.Zero)
                    {
                        bool propOk = ConfigurePropertiesDialog(propDlg, log);
                        if (!WaitPropertiesDialogClosed(propDlg, 10000, log))
                            Thread.Sleep(500);
                        if (propOk) Thread.Sleep(400);
                    }
                    else
                    {
                        if (log != null) log("属性对话框未出现（跳过属性面板配置）");
                    }
                }
                finally
                {
                    PopupAutoYes.RemoveExcludedTitle("EXB To PDF");   // 撤掉排除，后续弹窗 PopupAutoYes 正常处理
                }
            }

            if (sheet.Length == 0)
            {
                // 第三态不算失败：图幅静态文本读不到时，用纸张下拉当前值推断（截图上对话框默认就是当前幅面）
                foreach (Ctl c in Children(dlg))
                {
                    if (!IsComboClass(c.Cls) || c.H == printer) continue;
                    string cur = TextViaMsg(c.H);
                    if (LooksLikePaper(cur)) { sheet = cur.Trim(); break; }
                }
                if (log != null)
                    log(sheet.Length > 0
                        ? "图幅静态文本未读到；按纸张下拉当前值推断为 " + sheet
                        : "图幅未识别（第三态：静态文本与纸张下拉都没读出）");
            }
            if (sheet.Length == 0) return false;

            if (!SelectPaper(dlg, printer, sheet, log)) return false;

            if (!EnsureCheck(dlg, "黑白打印", true, log, true)) return false;
            // 「打印到文件」若被勾选，输出会变成 .prn（非 PDF），必须取消
            EnsureCheck(dlg, "打印到文件", false, log, false);
            if (!EnsureRadio(dlg, "横向", log)) return false;
            if (!EnsureRadio(dlg, "标准图", log)) return false;

            foreach (Ctl c in Children(dlg))
            {
                if (!IsButtonClass(c.Cls)) continue;
                string bn = Norm(c.Txt);
                // 恰为「打印」或「打印(&P)」这类带快捷键的写法；「打印到文件」不符合
                if (!(bn == "打印" || Regex.IsMatch(bn, @"^打印[\(（&]"))) continue;
                var ui = new UiCtrl
                {
                    Hwnd = c.H, Class = c.Cls, Text = c.Txt, Id = c.Id,
                    Top = dlg, TopTitle = TitleOf(dlg), TopClass = ClassOf(dlg), Enabled = c.Enabled
                };
                // 标准 BUTTON 类走 BM_CLICK；自绘按钮类（BCGPButton 等）BM_CLICK 无效，直接 MSAA
                bool ok = string.Equals(c.Cls, "Button", StringComparison.OrdinalIgnoreCase)
                    ? UiWin.Click(ui, "打印对话框点「打印」（图幅 " + sheet + "）")
                    : ClickMsaaExact(dlg, "打印", log);
                if (log != null) log("点「打印」ok=" + ok + " id=" + c.Id);
                return ok;
            }
            if (log != null) log("打印对话框里找不到文字恰为「打印」的按钮");
            return false;
        }

        // ============================================================ PDF 保存框

        /// <summary>
        /// 等 PDF 保存框：EXB To PDF 驱动在点「打印」后弹自己的保存框（帮助文档 9-41）。
        /// 判据 = 白名单进程里新出现的可见 #32770，且有文件名 Edit。
        /// </summary>
        public static IntPtr WaitPdfSaveDialog(int timeoutMs, IntPtr printDlg, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int lastLogged = -1;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                IntPtr found = IntPtr.Zero;
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!IsWindowVisible(h)) return true;
                    if (h == printDlg) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (!WatchedPid(pid)) return true;
                    string title = TitleOf(h);
                    if (title.Length == 0) return true;
                    if (title.IndexOf(" - [", StringComparison.Ordinal) >= 0) return true;   // 文档主窗口
                    string nt = Norm(title);
                    if (nt.IndexOf("打印对话框", StringComparison.Ordinal) >= 0) return true;
                    string cls = ClassOf(h);
                    if (cls.IndexOf("WindowsForms10", StringComparison.OrdinalIgnoreCase) >= 0) return true;   // 本插件工具窗
                    IntPtr edit = UiWin.FirstEdit(h);
                    if (edit == IntPtr.Zero) return true;   // 没有文件名输入框 ⇒ 不是保存框
                    bool titleHit = nt.IndexOf("保存", StringComparison.Ordinal) >= 0 ||
                                    nt.IndexOf("另存", StringComparison.Ordinal) >= 0 ||
                                    nt.IndexOf("pdf", StringComparison.Ordinal) >= 0 ||
                                    nt.IndexOf("输出", StringComparison.Ordinal) >= 0;
                    // 标题不像就退而看有没有「保存」按钮（自定义保存框的实证判据）
                    if (!titleHit && UiWin.FindButtonInSubtree(h, new[] { "保存(&S)", "保存(S)", "保存" }) == null)
                        return true;
                    found = h;
                    return false;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    if (log != null)
                        log("PDF 保存框已出现（" + (int)sw.ElapsedMilliseconds + "ms）标题=[" + TitleOf(found) + "]");
                    return found;
                }
                int el = (int)sw.ElapsedMilliseconds;
                if (log != null && el - lastLogged >= 5000)
                {
                    lastLogged = el;
                    log("等 PDF 保存框 " + el + "ms");
                }
                Thread.Sleep(300);
            }
            if (log != null) log("PDF 保存框未出现（" + timeoutMs + "ms）");
            return IntPtr.Zero;
        }

        /// <summary>保存框填全路径并点「保存」。</summary>
        public static bool FillSaveDialog(IntPtr dlg, string outFullPath, Action<string> log)
        {
            if (dlg == IntPtr.Zero || string.IsNullOrEmpty(outFullPath)) return false;
            UiWin.DumpWindow(dlg, "PDF保存框");
            IntPtr edit = UiWin.FirstEdit(dlg);
            if (edit == IntPtr.Zero)
            {
                if (log != null) log("PDF 保存框找不到文件名 Edit");
                return false;
            }
            SendMessageStr(edit, WM_SETTEXT, IntPtr.Zero, outFullPath);
            if (log != null) log("PDF 文件名 → " + outFullPath + "（edit id=" + GetDlgCtrlID(edit) + "）");

            UiCtrl saveBtn = UiWin.FindButtonInSubtree(dlg, new[] { "保存(&S)", "保存(S)", "保存" });
            if (saveBtn == null)
            {
                if (log != null) log("PDF 保存框找不到「保存」按钮");
                return false;
            }
            UiWin.Click(saveBtn, "PDF 保存框点「保存」");
            return true;
        }

        /// <summary>等 PDF 产物：存在、&gt;0、大小连续稳定（驱动异步落盘）。</summary>
        public static bool WaitPdfProduct(string outPath, int timeoutMs, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastLen = -1;
            long stableMs = 0;
            int lastLogged = -1;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var fi = new FileInfo(outPath);
                    if (fi.Exists && fi.Length > 0)
                    {
                        if (fi.Length == lastLen) stableMs += 400;
                        else { stableMs = 0; lastLen = fi.Length; }
                        if (stableMs >= 1200)
                        {
                            if (log != null)
                                log("PDF 已产出并稳定：" + outPath + "（" + fi.Length + " 字节，" +
                                    (int)sw.ElapsedMilliseconds + "ms）");
                            return true;
                        }
                    }
                }
                catch { }
                int el = (int)sw.ElapsedMilliseconds;
                if (log != null && el - lastLogged >= 10000)
                {
                    lastLogged = el;
                    log("等 PDF 产物 " + el + "ms");
                }
                Thread.Sleep(400);
            }
            bool ok = false;
            try { var fi = new FileInfo(outPath); ok = fi.Exists && fi.Length > 0; } catch { }
            if (log != null) log("PDF 产物等待结束（" + timeoutMs + "ms）：" + (ok ? "已存在但未稳定" : "不存在"));
            return ok;
        }
    }
}
