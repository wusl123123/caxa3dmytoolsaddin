// 改名/另存期间的宿主对话框哨兵（11-06 全盘移植 caxa3dcodenameextraction\LinkRenameSync.cs 的 RenamePopupSentry）。
// 为什么必须后台线程：宿主弹的是**模态框**，它自带消息泵占住 CAXA 主线程 ⇒ 主线程轮询结构性不返回（规则第 9/20 节、坑 152）。
// 为什么本项目比参考多一道「排除自家窗口」：本插件自己也有弹窗（图号分离、装配体导入选项、替换零部件），
//   它们现在是 WinForms 类名、不是 #32770，但和宿主弹框同属一个进程；一旦哪天换成原生框就会「自己点自己」，
//   所以 Arm 时在 UI 线程抓一份自家顶层窗口句柄快照，后台线程按 owner 链排除（规则：弹窗代点必须排除自身窗口）。
// 答案口径（照抄参考的语义，不自己发明）：
//   · clickYes=false（默认）→ 点「取消/否」：改名语境里宿主任何弹框都不该答「是」（坑 149：答「是」＝CAXA 用改名框回滚名字）；
//   · clickYes=true → 点「是/确定」：只给 doc.SaveAs(force=true) 的「是否覆盖」用；
//   · 每次命中都把「框标题 + 要点的按钮」落日志；找不到可点按钮才投 WM_CLOSE（并在日志里写清是兜底）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    internal static class RenamePopupSentry
    {
        private static volatile bool _armed;
        private static volatile bool _clickYes;
        private static Thread _worker;
        private static HashSet<IntPtr> _ownTop;   // 只在 Arm 的 UI 线程写，后台线程只读

        /// <summary>布防：后台线程每 120 ms 扫本进程的可见 #32770，命中就代点目标按钮，最多盯 30 秒。</summary>
        public static void Arm(Action<string> log, bool clickYes)
        {
            if (_armed) return;                       // 已布防＝沿用现有答案口径，不重开线程
            _clickYes = clickYes;

            // 在调用线程（＝CAXA 主线程）采集自家窗口句柄；后台线程只用这份快照，不碰 Application.OpenForms
            var own = new HashSet<IntPtr>();
            try
            {
                foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms)
                {
                    if (f != null && f.IsHandleCreated) own.Add(f.Handle);
                }
            }
            catch (Exception ex) { Diagnostics.Trace("sentry: 采集自家窗口失败（按无自家窗口继续）：" + ex.Message); }
            _ownTop = own;

            _armed = true;
            Action<string> sink = log ?? delegate { };
            _worker = new Thread(delegate()
            {
                var seen = new HashSet<IntPtr>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_armed && sw.ElapsedMilliseconds < 30000)
                {
                    IntPtr dlg = FindHostDialog();
                    if (dlg != IntPtr.Zero && seen.Add(dlg))
                    {
                        string title = GetWndText(dlg);
                        sink("sentry: 发现宿主对话框 [" + title + "] hwnd=0x" +
                            dlg.ToInt64().ToString("X") + "，尝试点「" + (_clickYes ? "是/确定" : "取消/否") + "」");
                        bool clicked = ClickButtonLike(dlg, _clickYes);
                        if (!clicked)
                        {
                            PostMessage(dlg, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                            sink("sentry: 没找到可点按钮，已投 WM_CLOSE（兜底，不算已代点）");
                        }
                    }
                    Thread.Sleep(120);
                }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        /// <summary>撤防：线程见 _armed=false 后 120 ms 内自行退出（最长留 30 秒空转，不 Join——主线程禁阻塞）。</summary>
        public static void Disarm()
        {
            _armed = false;
            _worker = null;
            _ownTop = null;
        }

        // ---------------- Win32 ----------------

        private delegate bool EnumWndProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWndProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwnd, EnumWndProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageText(IntPtr hwnd, int msg, IntPtr wParam, StringBuilder lParam);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_GETTEXT = 0x000D;
        private const int WM_GETTEXTLENGTH = 0x000E;
        private const int BM_CLICK = 0x00F5;
        private const uint GW_OWNER = 4;

        /// <summary>本进程可见 #32770，且不是自家窗体/自家窗体的 owner 后代。</summary>
        private static IntPtr FindHostDialog()
        {
            uint selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr hit = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString() != "#32770" || !IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != selfPid) return true;
                if (IsOwnWindow(h)) return true;     // 自家弹框绝不动手
                hit = h;
                return false;
            }, IntPtr.Zero);
            return hit;
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

        /// <summary>框内找目标按钮代点（PostMessage BM_CLICK，同进程内安全）。
        /// yesMode=false 找「取消/否/Cancel/No」；yesMode=true 找「是/确定/Yes/OK」。</summary>
        private static bool ClickButtonLike(IntPtr dlg, bool yesMode)
        {
            bool clicked = false;
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
                bool hit;
                if (yesMode)
                    hit = t.IndexOf("是", StringComparison.Ordinal) >= 0 ||
                          t.IndexOf("确定", StringComparison.Ordinal) >= 0 ||
                          t.IndexOf("Yes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          t.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0;
                else
                    hit = t.IndexOf("取消", StringComparison.Ordinal) >= 0 ||
                          t.IndexOf("否", StringComparison.Ordinal) >= 0 ||
                          t.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          t.IndexOf("No", StringComparison.Ordinal) == 0;
                if (hit)
                {
                    PostMessage(h, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    clicked = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return clicked;
        }
    }
}
