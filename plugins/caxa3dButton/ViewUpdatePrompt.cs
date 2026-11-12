// 「打开工程图」后 CAXA 弹出的「当前有3D视图数据源更新，要更新对应所有视图吗？」自动点「是」哨兵。
// 设计对齐本工程既有哨兵（PopupAutoYes / DlgUpdate3DBom）：
//   · 只在「打开工程图」流程期间布防（Arm 在 OpenPartDrawing 之前 —— 因为 OpenFile2 打开 exb 时此框以
//     模态方式卡住主线程 COM 调用，必须由后台线程去点，否则主线程永远回不来）；
//   · 窄匹配：只认正文含「3D视图数据源更新」的 #32770 对话框，绝不误点用户在 CAXA 里的其它「是/否」框；
//   · 点「是」用 PostMessage(BM_CLICK)（不阻塞哨兵线程）；点过之后等框消失即判完成，最多守 30s；
//   · 扫描 IronCAD / CAXADraft 两个进程（图纸环境在某些配置下是独立进程，框可能弹在 CAXADraft.exe）。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dButton
{
    /// <summary>
    /// 「打开工程图」配套：CAXA 打开 exb 后若 3D 数据源有更新会弹
    /// 「当前有3D视图数据源更新，要更新对应所有视图吗？」（是/否），
    /// 本哨兵在用户调用「打开工程图」期间自动点「是(Y)」。
    /// </summary>
    internal static class ViewUpdatePrompt
    {
        /// <summary>正文匹配串（足够窄，不会误伤其它确认框）。</summary>
        private const string BodyNeedle = "3D视图数据源更新";

        private const int PollMs = 150;
        private const int ArmTimeoutMs = 30000;     // 从布防起最多守 30 秒
        private const int ReClickCooldownMs = 600;  // 同一张框两次点击之间的最小间隔

        private static volatile bool _armed;
        private static Thread _thread;
        private static int _gen;

        /// <summary>哨兵收尾回调（**后台线程触发**，UI 侧需自行切回 UI 线程再改控件）。</summary>
        internal static Action<string> Finished;

        private static void Notify(string msg)
        {
            try { if (Finished != null) Finished(msg); } catch { }
        }

        public static bool IsArmed { get { return _armed; } }

        /// <summary>布防（重复调用会先撤掉上一个哨兵，同一时刻只允许一个）。</summary>
        public static void Arm()
        {
            Disarm();
            _armed = true;
            int gen = ++_gen;
            _thread = new Thread(delegate () { Loop(gen); });
            _thread.IsBackground = true;
            _thread.Name = "ViewUpdatePrompt";
            _thread.Start();
            Diagnostics.Log("view-update: 已布防自动点「是」（匹配正文含 \"" + BodyNeedle + "\"，时限 " +
                            (ArmTimeoutMs / 1000) + " 秒）");
        }

        public static void Disarm()
        {
            if (!_armed) return;
            _armed = false;
            Diagnostics.Log("view-update: 撤防");
        }

        private static void Loop(int gen)
        {
            var sw = Stopwatch.StartNew();
            bool clicked = false;
            int lastClick = 0;
            while (_armed && gen == _gen && sw.ElapsedMilliseconds < ArmTimeoutMs)
            {
                IntPtr dlg = FindDialog();
                if (dlg == IntPtr.Zero)
                {
                    if (clicked) break;          // 已点过且框已消失 ⇒ 完成
                    Thread.Sleep(PollMs);
                    continue;
                }
                // 同一张框最多每 ReClickCooldownMs 点一次（首点不生效时补点，避免狂点）
                if (!clicked || (Environment.TickCount - lastClick) > ReClickCooldownMs)
                {
                    if (ClickYes(dlg))
                    {
                        clicked = true;
                        lastClick = Environment.TickCount;
                        Diagnostics.Log("view-update: 已自动点「是」@「" + TextOf(dlg) + "」");
                    }
                }
                Thread.Sleep(PollMs);
            }
            if (_armed) _armed = false;
            Notify(clicked ? "已自动点「是」关闭「更新视图」对话框"
                           : (ArmTimeoutMs / 1000) + " 秒内未出现「更新视图」对话框（可能本图无 3D 数据源更新）");
        }

        // ============================================================ 找对话框

        /// <summary>
        /// 找正文含 <see cref="BodyNeedle"/> 的可见 #32770 对话框（IronCAD / CAXADraft 两进程都认）。
        /// 先查对话框自身标题，再查其子 Static 正文：截图里标题栏是主程序名，
        /// 真正能定位的是那行提示文字，所以以 Static 正文为准。
        /// </summary>
        private static IntPtr FindDialog()
        {
            IntPtr found = IntPtr.Zero;
            int myPid = Process.GetCurrentProcess().Id;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (found != IntPtr.Zero) return false;
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
                if (GetClass(h) != "#32770") return true;

                // ① 标题直接命中（少数版本会把提示文字放进标题）
                if (TextOf(h).IndexOf(BodyNeedle, StringComparison.Ordinal) >= 0) { found = h; return false; }
                // ② 子 Static 正文命中
                bool hit = false;
                EnumChildWindows(h, delegate (IntPtr c, IntPtr l2)
                {
                    if (hit) return false;
                    if (GetClass(c) != "Static") return true;
                    if (TextOf(c).IndexOf(BodyNeedle, StringComparison.Ordinal) >= 0) { hit = true; return false; }
                    return true;
                }, IntPtr.Zero);
                if (hit) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>找对话框里的「是(Y)」按钮并投 BM_CLICK。返回是否真的投出去了。</summary>
        private static bool ClickYes(IntPtr dlg)
        {
            IntPtr btn = FindYesButton(dlg);
            if (btn == IntPtr.Zero) return false;
            PostMessage(btn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return true;
        }

        /// <summary>
        /// 找文字以「是」开头的按钮（去掉 &amp; 与首尾空格后比较）。
        /// 「否(N)」之类不含「是」前缀，不会被命中 —— 只点「是」。
        /// </summary>
        private static IntPtr FindYesButton(IntPtr dlg)
        {
            IntPtr hit = IntPtr.Zero;
            EnumChildWindows(dlg, delegate (IntPtr h, IntPtr l)
            {
                if (hit != IntPtr.Zero) return false;
                if (GetClass(h) != "Button") return true;
                string raw = TextOf(h);
                if (raw.Length == 0 || raw.Length > 24) return true;
                string norm = raw.Replace("&", "").Trim();
                if (norm.StartsWith("是", StringComparison.Ordinal) ||
                    norm.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    hit = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return hit;
        }

        private static string ProcessNameOf(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch { return ""; }
        }

        // ============================================================ Win32

        private const uint BM_CLICK = 0x00F5;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr p, EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        private static string GetClass(IntPtr h)
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
    }
}
