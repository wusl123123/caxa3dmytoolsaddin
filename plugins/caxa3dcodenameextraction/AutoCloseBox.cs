using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 本插件自己的提示框：模态，但**一定会自己消失**。
    ///
    /// 为什么必须这么写：MessageBox.Show 是模态阻塞调用，user32 的规矩是
    /// 「禁用调用线程的所有顶层窗口（除对话框自己拥有的）」—— 而本插件的流程就跑在
    /// CAXA 主线程上（见 MainForm.WaitUntil 里为什么要泵消息），所以一张提示框会把
    /// **CAXA 3D 主界面 + 脚本界面一起禁用**。用户只要没注意到这个小框（流程收尾
    /// RestoreDesignFocus 刚把焦点切回设计环境，小框很容易被主窗口挡在后面），
    /// 看到的就是「3D 主界面和脚本界面全部点不动」。
    ///
    /// 实机 2026-11-01 18:42 那轮：替换其实已经成功（设计树命中、元素已换），
    /// 收尾弹了「替换元素探针 完成。」，18:42:30 的巡检快照里主框架与插件窗口都是(禁用)、
    /// 只剩这张 #32770，日志里主线程定时器还在跳（模态循环照常派发 WM_TIMER）——
    /// 用户找不到可点的东西，只能把 CAXA 杀掉。
    ///
    /// 两道保险：
    /// 1) Show：挂看门狗线程，到点自己点「确定」（并写日志留证）→ 代码层面不再可能永久阻塞。
    /// 2) <see cref="SweepSelfModal"/>：常驻兜底，本插件标题的 #32770 挂着超过 <see cref="SweepAfterMs"/>
    ///    就点掉 —— 覆盖别的路径（如 Addin 注册失败提示）漏进来的模态框。
    /// </summary>
    internal static class AutoCloseBox
    {
        /// <summary>看门狗等提示框「出现」的最长时间：MessageBox.Show 之后窗口通常 200ms 内可见。</summary>
        private const int AppearTimeoutMs = 15000;

        /// <summary>
        /// 常驻兜底的阈值：自家标题的模态框存在这么久还没人点，就认为把界面冻住了。
        ///
        /// 2026-11-01 19:06 实机把 20 秒的代价暴露得很清楚：BtnRename_Click 收尾那张裸
        /// MessageBox 卡了 62 秒（20 秒 × 3 次尝试），用户全程「3D 主界面和脚本窗口都点不动」。
        /// 兜底不该让用户等这么久。取 8 秒：既能让人读完一条前置校验提示（「请先选中零件」这类
        /// 用户主动点按钮才弹出的框），又远小于原来那 20 秒。**正常成功路径上根本不弹框**
        /// （完成一律改状态栏 + 任务栏闪烁），所以这条只剩「漏网框」才会触发。
        /// </summary>
        private const int SweepAfterMs = 8000;

        private static readonly string[] OkTexts = { "确定", "ok", "&ok", "是", "yes", "关闭", "close" };

        // ---------------------------------------------------------------- Show

        /// <summary>
        /// 显示提示框，<paramref name="autoCloseMs"/> 毫秒后自动点掉「确定」。
        /// 用户自己点掉则看门狗立即退出（窗口没了），不会误点后面的框。
        /// </summary>
        public static void Show(string text, int autoCloseMs)
        {
            Thread watch = new Thread(delegate() { Watch(MainForm.AppTitleBase, autoCloseMs); });
            watch.IsBackground = true;
            watch.Name = "AutoCloseBox";
            Diagnostics.Log("AutoCloseBox: 提示框「" + Cut(text) + "」→ " +
                (autoCloseMs / 1000) + " 秒后自动关闭（模态框会禁用 CAXA 主界面与脚本窗口）");
            watch.Start();
            try
            {
                MessageBox.Show(text, MainForm.AppTitleBase,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                Diagnostics.Log("AutoCloseBox: 提示框已关闭（自动或用户点击）");
            }
        }

        private static void Watch(string caption, int autoCloseMs)
        {
            IntPtr dlg = IntPtr.Zero;
            int seenAt = 0;
            int giveUp = Environment.TickCount + AppearTimeoutMs;

            while (true)
            {
                Thread.Sleep(150);

                if (dlg == IntPtr.Zero)
                {
                    dlg = FindSelfDialog(caption);
                    if (dlg == IntPtr.Zero)
                    {
                        if ((Environment.TickCount - giveUp) > 0) return;   // 一直没出现（用户抢先点了/被别处关了）
                        continue;
                    }
                    seenAt = Environment.TickCount;
                    Diagnostics.Log("AutoCloseBox: 抓到提示框 0x" + dlg.ToInt64().ToString("X") +
                        "，开始计时");
                    continue;
                }

                if (!IsWindow(dlg)) return;                    // 用户自己点掉了
                if ((Environment.TickCount - seenAt) < autoCloseMs) continue;

                CloseDialog(dlg, "到点自动关闭");
                return;
            }
        }

        // ---------------------------------------------------------------- 常驻兜底

        private static readonly Dictionary<long, int> _sweepSeen = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> _sweepRetry = new Dictionary<long, int>();
        private static int _lastSweepTick;

        /// <summary>
        /// 常驻兜底（由 PopupAutoYes 的巡检线程调用）：本进程里标题等于 AppTitleBase 的可见 #32770
        /// 若已经挂了 <see cref="SweepAfterMs"/> 还没人点，就关掉它并写日志。
        ///
        /// 只认**自己标题**的框，绝不碰用户的对话框，也不碰 CAXA 的业务框 —— 这条守则和
        /// PopupAutoYes 其它代点逻辑一致。代价是提示框最多活 4 秒：正文同时写在日志区，
        /// 用户不会因此丢信息，但界面不会被它冻住。
        /// </summary>
        public static void SweepSelfModal()
        {
            int now = Environment.TickCount;
            if ((now - _lastSweepTick) < 500) return;          // 每 0.5 秒最多扫一次
            _lastSweepTick = now;

            try
            {
                uint selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                var live = new List<long>();
                IntPtr victim = IntPtr.Zero;
                string why = null;

                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    uint wpid;
                    GetWindowThreadProcessId(h, out wpid);
                    if (wpid != selfPid) return true;
                    if (GetClass(h) != "#32770") return true;
                    if (GetText(h) != MainForm.AppTitleBase) return true;
                    if (!IsWindowVisible(h)) return true;

                    long key = h.ToInt64();
                    live.Add(key);

                    int since;
                    if (!_sweepSeen.TryGetValue(key, out since))
                    {
                        _sweepSeen[key] = now;                 // 第一次见到：记时间，下一轮再判
                        Diagnostics.Log("AutoCloseBox: 发现本插件模态框 0x" + key.ToString("X") +
                            "，超过 " + (SweepAfterMs / 1000) + " 秒没人点就自动关");
                        return true;
                    }
                    if ((now - since) < SweepAfterMs) return true;
                    if (!IsWindowEnabled(h)) return true;       // 已在关闭中

                    // 关不掉的框隔 5 秒再试，别每轮都刷日志（19:06 那三轮 20 秒就是这么攒出来的）
                    int nextTry;
                    if (_sweepRetry.TryGetValue(key, out nextTry) && (now - nextTry) < 0) return true;

                    if (victim == IntPtr.Zero)
                    {
                        victim = h;
                        why = "挂了 " + (SweepAfterMs / 1000) + " 秒无人点";
                    }
                    return true;
                }, IntPtr.Zero);

                // 关闭动作放在枚举之外做：CloseDialog 内部要 Sleep 验证，放回调里会拖住整个枚举
                if (victim != IntPtr.Zero && !CloseDialog(victim, why))
                    _sweepRetry[victim.ToInt64()] = now + 5000;
                else if (victim != IntPtr.Zero)
                    _sweepSeen.Remove(victim.ToInt64());

                // 已经消失的框从计时表里清掉（句柄会复用，留着会误判）
                if (_sweepSeen.Count > 0)
                {
                    var dead = new List<long>();
                    foreach (var kv in _sweepSeen)
                        if (!live.Contains(kv.Key)) dead.Add(kv.Key);
                    foreach (long k in dead) { _sweepSeen.Remove(k); _sweepRetry.Remove(k); }
                }
            }
            catch (Exception ex) { Diagnostics.Trace("AutoCloseBox.SweepSelfModal: " + ex.Message); }
        }

        // ---------------------------------------------------------------- 实现细节

        /// <summary>IDOK：MessageBox 的「确定」永远是 1 号按钮，文字匹配失效时靠它兜底。</summary>
        private const int IdOk = 1;
        private const uint WM_COMMAND = 0x0111;

        /// <summary>
        /// 关掉一张提示框，三条通道依次试，每步都验证是否真没了。
        ///
        /// 为什么不能只投一次 BM_CLICK：实机 19:06:23 / 19:06:43 两次「已点「确定」(0x4E0D64)」
        /// 之后框**仍然在**（下一轮巡检又发现同一个 0x59109E），直到 19:07:04 第三次才关掉 ——
        /// 一次点击不生效就把用户继续锁在模态里，所以必须验证 + 换通道。
        /// </summary>
        private static bool CloseDialog(IntPtr dlg, string why)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (!IsWindow(dlg)) return true;

                if (attempt == 1)
                {
                    IntPtr ok = FindOkButton(dlg);
                    if (ok != IntPtr.Zero)
                    {
                        PostMessage(ok, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Diagnostics.Log("AutoCloseBox: " + why + "，已点「确定」(0x" +
                            ok.ToInt64().ToString("X") + ")");
                    }
                    else
                    {
                        PostMessage(dlg, WM_COMMAND, (IntPtr)IdOk, IntPtr.Zero);
                        Diagnostics.Log("AutoCloseBox: " + why + "，没找到「确定」按钮，改投 IDOK 命令");
                    }
                }
                else if (attempt == 2)
                {
                    PostMessage(dlg, WM_COMMAND, (IntPtr)IdOk, IntPtr.Zero);
                    Diagnostics.Log("AutoCloseBox: 框还在（" + why + "），第 2 次改投 IDOK 命令");
                }
                else
                {
                    PostMessage(dlg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    Diagnostics.Log("AutoCloseBox: 框还在（" + why + "），第 3 次改投 WM_CLOSE");
                }

                for (int i = 0; i < 12; i++)          // 最多等 600ms 看是否真关掉
                {
                    Thread.Sleep(50);
                    if (!IsWindow(dlg)) return true;
                }
            }
            Diagnostics.Log("AutoCloseBox: 三条通道都没关掉这张框，5 秒后重试；" +
                "它还在禁用 CAXA 主界面，需要手工点一下");
            return false;
        }

        private static IntPtr FindSelfDialog(string caption)
        {
            uint selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint wpid;
                GetWindowThreadProcessId(h, out wpid);
                if (wpid != selfPid) return true;
                if (GetClass(h) != "#32770") return true;
                if (!IsWindowVisible(h)) return true;
                if (GetText(h) != caption) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static IntPtr FindOkButton(IntPtr dlg)
        {
            IntPtr byText = IntPtr.Zero;
            IntPtr byId = IntPtr.Zero;
            EnumChildWindows(dlg, delegate(IntPtr h, IntPtr l)
            {
                if (GetClass(h) != "Button") return true;
                // 文字匹配失效时（自绘/本地化异常）靠控件 id 兜底：MessageBox 的「确定」恒为 IDOK=1
                if (byId == IntPtr.Zero && GetDlgCtrlID(h) == IdOk) byId = h;
                string raw = GetText(h);
                if (raw.Length == 0 || raw.Length > 16) return true;
                string norm = raw.Replace("&", "").Replace("(&", "(").Trim().ToLowerInvariant();
                foreach (string t in OkTexts)
                {
                    string cand = t.Replace("&", "").ToLowerInvariant();
                    if (norm == cand || norm.StartsWith(cand, StringComparison.Ordinal))
                    {
                        byText = h;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return byText != IntPtr.Zero ? byText : byId;
        }

        private static string Cut(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }

        // ---------------------------------------------------------------- Win32

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        // 返回值按 int 声明：声明成 IntPtr 时 x64 会把负 id 零扩展，ToInt32() 直接抛
        // OverflowException（2026-11-01 17:38 的卡死就是这条，见项目总结坑 35）
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hWnd);

        private const uint BM_CLICK = 0x00F5;
        private const uint WM_CLOSE = 0x0010;

        private static string GetClass(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetText(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
