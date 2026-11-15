// 视图更新提示守护（2026-11-15 移植自 caxa3dqrcode\NativeQrDialog.cs，逻辑一字不改、仅换命名空间）。
//
// ★ 职责：视图更新提示守护。
//   宿主在插图/生成后弹「当前有3D视图数据源更新，要更新对应所有视图吗？」，
//   这个 #32770 占住主线程 ⇒ 命令停在等待输入、delta 恒 0，下一轮还挡在 OpenFile2 前面。
//   守护只在 ArmViewGuard(150 s) 的激活窗口内动手，只认明确白名单文本，不干预其它对话框：
//     ①「3D视图…数据源更新」→ 点「是」（重算视图由宿主决定）；
//     ②「输入信息为空」→ 点「确定」。
//   风险留痕：点「是」触发视图重生成，耗时比「否」长且有渲染表面 AV 历史（坑 16）；
//   若实机退出 CAXA，按「trace 断点 + _crx.log 末行 + dmp 时间戳」取证。
//
// 纪律：后台线程绝不碰任何 WinForms 控件；日志直接落 Diagnostics（文件日志）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    internal static class NativeQrDialog
    {
        private const int WM_COMMAND = 0x0111;
        private const int BM_CLICK = 0x00F5;
        private const uint GW_OWNER = 4;

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);

        private sealed class Ctl
        {
            public IntPtr H; public string Cls; public string Text; public int Id;
        }

        // 不能用 volatile long（CS0677：volatile 只支持 4 字节及以下标量）；改走 Interlocked 读写。
        private static long _guardUntilTicks;
        private static int _guardStarted;
        private static volatile int _viewPromptsHandled;
        public static int ViewPromptsHandled { get { return _viewPromptsHandled; } }

        private static void Q(string s)
        {
            Diagnostics.Log(DateTime.Now.ToString("HH:mm:ss.fff") + " native-qr: " + s);
        }

        private static string ReadText(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string CleanButtonText(string s)
        {
            return (s ?? "").Replace("&", "").Replace("(", "").Replace(")", "")
                            .Replace("（", "").Replace("）", "").Replace(" ", "");
        }

        /// <summary>在 Run 开头调用：激活守护窗口（每轮刷新），并一次性启动后台线程。</summary>
        public static void ArmViewGuard(int seconds, Action<string> log)
        {
            Interlocked.Exchange(ref _guardUntilTicks, DateTime.UtcNow.AddSeconds(seconds).Ticks);
            _viewPromptsHandled = 0;
            if (Interlocked.Exchange(ref _guardStarted, 1) == 0)
            {
                Thread t = new Thread(ViewGuardLoop);
                t.IsBackground = true;
                t.Name = "QrViewGuard";
                t.Start();
            }
            Q("视图更新提示守护已激活 " + seconds + " s（白名单：「3D视图数据源更新」→点「" +
              kViewPromptChoice + "」；「输入信息为空」→点「确定」；不干预其它对话框）");
            if (log != null)
            {
                try { log("视图更新提示守护已激活 " + seconds + " s（白名单自动点「" + kViewPromptChoice + "」）"); }
                catch { }
            }
        }

        /// <summary>主线程同步查询：当前是否还挂着白名单内的模态框。</summary>
        public static bool ViewPromptPresent()
        {
            List<Ctl> ignored;
            return FindViewPrompt(out ignored) != IntPtr.Zero;
        }

        private const string kViewPromptChoice = "是";

        /// <summary>只认两个白名单：①「3D视图…数据源更新」+「是」按钮；②「输入信息为空」+「确定」按钮。</summary>
        private static IntPtr FindViewPrompt(out List<Ctl> ctlsOut)
        {
            IntPtr found = IntPtr.Zero;
            List<Ctl> hit = null;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (found != IntPtr.Zero) return false;
                if (!IsWindowVisible(h)) return true;
                var clsB = new StringBuilder(128);
                GetClassName(h, clsB, clsB.Capacity);
                string cls = clsB.ToString();
                string title = ReadText(h);
                // 快速过滤：候选只能是标准对话框，或「CAXA 3D 实体设计2027」这类短消息框标题。
                // 主框架标题一般带 " - <文件名>"，必须排除，否则每 250 ms 要枚举几百个子窗口。
                bool titleCandidate =
                    (title.IndexOf("CAXA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     title.IndexOf("实体设计", StringComparison.Ordinal) >= 0) &&
                    title.Length <= 40 && title.IndexOf(" - ", StringComparison.Ordinal) < 0;
                if (cls != "#32770" && !titleCandidate) return true;
                IntPtr owner = GetWindow(h, GW_OWNER);
                if (owner != IntPtr.Zero)
                {
                    var oc = new StringBuilder(128);
                    GetClassName(owner, oc, oc.Capacity);
                    if (oc.ToString().StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase)) return true;
                }
                List<Ctl> cs = ListControls(h);
                bool viewHint = false, emptyHint = false, hasYes = false, hasOk = false;
                foreach (Ctl c in cs)
                {
                    if ((c.Text.IndexOf("3D视图", StringComparison.Ordinal) >= 0 &&
                         c.Text.IndexOf("更新", StringComparison.Ordinal) >= 0) ||
                        c.Text.IndexOf("数据源更新", StringComparison.Ordinal) >= 0) viewHint = true;
                    if (c.Text.IndexOf("输入信息为空", StringComparison.Ordinal) >= 0) emptyHint = true;
                    if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string t = CleanButtonText(c.Text);
                        // 「是(Y)」→「是Y」；排除「否(N)」及「全是(A)」「都不是」等含「是」干扰项
                        if (t.StartsWith("是", StringComparison.Ordinal) &&
                            t.IndexOf("否", StringComparison.Ordinal) < 0) hasYes = true;
                        if (t.IndexOf("确定", StringComparison.Ordinal) >= 0) hasOk = true;
                    }
                }
                if ((viewHint && hasYes) || (emptyHint && hasOk))
                {
                    found = h;
                    hit = cs;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            ctlsOut = hit;
            return found;
        }

        private static void ViewGuardLoop()
        {
            var tried = new Dictionary<IntPtr, int>();
            while (true)
            {
                if (Interlocked.Read(ref _guardUntilTicks) < DateTime.UtcNow.Ticks)
                {
                    Thread.Sleep(300);
                    tried.Clear();
                    continue;
                }
                List<Ctl> ctls;
                IntPtr dlg = FindViewPrompt(out ctls);
                if (dlg == IntPtr.Zero) { Thread.Sleep(250); continue; }

                int n;
                tried.TryGetValue(dlg, out n);
                if (n >= 3) { Thread.Sleep(400); continue; }
                tried[dlg] = n + 1;
                _viewPromptsHandled++;
                Q("视图更新提示命中 hwnd=0x" + dlg.ToInt64().ToString("X") + " 第 " + (n + 1) + " 次（已代点）");

                bool viewHint = false, emptyHint = false;
                if (ctls != null)
                    foreach (Ctl c in ctls)
                    {
                        if (c.Text.IndexOf("数据源更新", StringComparison.Ordinal) >= 0 ||
                            c.Text.IndexOf("3D视图", StringComparison.Ordinal) >= 0) viewHint = true;
                        if (c.Text.IndexOf("输入信息为空", StringComparison.Ordinal) >= 0) emptyHint = true;
                    }

                Ctl btn = viewHint ? FindButton(ctls, kViewPromptChoice) :
                          emptyHint ? FindButton(ctls, "确定") : null;
                if (btn == null)
                {
                    Q("白名单命中但没找到「" + (viewHint ? kViewPromptChoice : "确定") +
                      "」按钮（下一轮按实机 ID 精确化）");
                }
                else
                {
                    PostMessage(dlg, WM_COMMAND, new IntPtr(btn.Id & 0xFFFF), btn.H);
                    PostMessage(btn.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Q("已代点「" + btn.Text + "」 id=" + btn.Id + " hwnd=0x" +
                      btn.H.ToInt64().ToString("X") + "（双通道）");
                }
                Thread.Sleep(600);   // 给宿主关框 + 命令继续的时间，避免同一帧连点
            }
        }

        private static Ctl FindButton(List<Ctl> ctls, string keyword)
        {
            if (ctls == null) return null;
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string t = CleanButtonText(c.Text);
                // 「是」易撞上「全是(A)」「都不是」等干扰项：只认以「是」开头的按钮文本
                if (keyword == "是")
                {
                    if (t.StartsWith("是", StringComparison.Ordinal) &&
                        t.IndexOf("否", StringComparison.Ordinal) < 0) return c;
                }
                else if (t.IndexOf(keyword, StringComparison.Ordinal) >= 0) return c;
            }
            return null;
        }

        private static List<Ctl> ListControls(IntPtr dlg)
        {
            var list = new List<Ctl>();
            EnumChildWindows(dlg, delegate (IntPtr h, IntPtr l)
            {
                var cls = new StringBuilder(256);
                var txt = new StringBuilder(512);
                GetClassName(h, cls, cls.Capacity);
                GetWindowText(h, txt, txt.Capacity);
                list.Add(new Ctl { H = h, Cls = cls.ToString(), Text = txt.ToString(), Id = GetDlgCtrlID(h) });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
    }
}
