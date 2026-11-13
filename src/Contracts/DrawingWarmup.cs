// 图纸环境预热引擎 —— 集合体唯一公共实现（AGENTS.md「预热铁律」配套）：
//   · 预热入口与自动预热只允许出现在 Host 主界面（MainToolboxForm）；
//   · 子插件不得自带预热按钮 / 状态灯 / 启动自动预热 / 预热载体 .exb；
//   · 子插件业务流程确需热环境时，可在主线程调 EnsureWarm —— 已热即秒回，不反复预热。
// 判据：窗口结构（MDIClient 子窗口类名含 AfxFrameOrView）——不依赖任何插件的 CRX marker；
//       主界面预热完载体图纸随即关闭、marker 随之消失，窗口判据不受影响。
// 唯一预热直线（规则铁律）：OpenFile2 打开专用空图纸「caxa3d图纸环境预热.exb」→ 等新 Draft
//       窗口出现 → WM_CLOSE 立即不保存关掉；绝不用业务 exb 当载体；失败判死不重试。
// 线程：只能在 CAXA 主线程（UI 线程）调用 —— COM 只在主线程访问，等待用 DoEvents 泵消息。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace Caxa3dMyTools.Contracts
{
    public static class DrawingWarmup
    {
        public const string WarmExbName = "caxa3d图纸环境预热.exb";

        /// <summary>本进程已成功预热过一次（进程级：工具窗关掉再打开不重复预热）。</summary>
        private static bool _warmedOnce;
        private static int _busy;

        public static bool WarmedOnce { get { return _warmedOnce; } }

        /// <summary>热态判定：本进程主框架 MDIClient 下已有 Draft 图纸窗口（窗口结构判据，毫秒级）。</summary>
        public static bool IsWarm()
        {
            return DraftWindowProbe.HasActiveDrawing();
        }

        /// <summary>
        /// 确保图纸环境热态：已热（窗口在 / 本进程预热过）立即返回 true，不开图不重复预热；
        /// 冷态才走唯一预热直线。UI 线程调用。
        /// </summary>
        public static bool EnsureWarm(IZBaseApp app, Action<string> log)
        {
            if (_warmedOnce || IsWarm())
            {
                _warmedOnce = true;
                Log(log, "warm: 已是热态（" + (IsWarm() ? "图纸窗口在" : "本进程预热过") + "）—— 不重复预热");
                return true;
            }
            return WarmNow(app, log);
        }

        /// <summary>强制执行一次预热直线（不做热态短路）。UI 线程调用；失败判死，不重试。</summary>
        public static bool WarmNow(IZBaseApp app, Action<string> log)
        {
            if (log == null) log = delegate { };
            if (app == null) { log("warm: 预热失败：没有 IZBaseApp（未连接 CAXA 站点）。"); return false; }
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                log("warm: 已有预热在进行 —— 跳过本次");
                return IsWarm();
            }
            try
            {
                if (IsWarm()) { _warmedOnce = true; log("warm: 已是热态（图纸窗口在）—— 不重复预热"); return true; }

                string exb = FindExb();
                if (exb == null)
                {
                    log("warm: 预热失败：没找到「" + WarmExbName + "」（找过程序集目录并逐级向上＋CAXA bin 兜底）——不猜别的载体。");
                    return false;
                }

                List<IntPtr> before = DraftWindowProbe.ListDraftDocs();
                log("warm: 冷态（无 Draft 窗口）—— OpenFile2 打开预热载体「" + Path.GetFileName(exb) + "」");

                bool ao = false;
                try { app.OpenFile2(exb, false, true, out ao); }
                catch (Exception ex) { log("warm: OpenFile2 返回异常（热路径正常）: " + ex.Message); }

                log("warm: 已投打开请求，等新图纸窗口出现（最多 20 秒）…");
                var fresh = new List<IntPtr>();
                bool appeared = WaitUntil(20000, delegate
                {
                    fresh.Clear();
                    foreach (IntPtr h in DraftWindowProbe.ListDraftDocs())
                        if (!before.Contains(h)) fresh.Add(h);
                    return fresh.Count > 0;
                }, 400, log);
                if (!appeared)
                {
                    log("warm: 预热失败 —— 20 秒内没出现新的图纸窗口（不重试）。");
                    return false;
                }

                _warmedOnce = true;
                log("warm: 预热成功 —— 出现 " + fresh.Count + " 个新图纸窗口，随即不保存关闭载体");
                foreach (IntPtr h in fresh) DraftWindowProbe.RequestClose(h);

                bool allGone = WaitUntil(15000, delegate
                {
                    bool alive = false;
                    foreach (IntPtr h in fresh) if (DraftWindowProbe.IsAlive(h)) { alive = true; break; }
                    if (alive)
                    {
                        string handled = DraftWindowProbe.AnswerNoOnSaveDialog();
                        if (handled != null) log("warm: " + handled);
                    }
                    return !alive;
                }, 250, log);

                log(allGone ? "warm: 载体图纸已不保存关闭 —— 图纸环境已焐热"
                            : "warm: 载体图纸 15 秒内没关掉（请手工关掉多出来的那张图纸）；预热本身成功");
                return true;
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        /// <summary>
        /// 找预热载体 .exb：从本程序集目录逐级向上（Host 根目录部署一份即可，插件目录不再各带副本），
        /// 最后兜底 CAXA bin（AppDomain.BaseDirectory）。找不到返回 null。
        /// </summary>
        public static string FindExb()
        {
            string cur = null;
            try { cur = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } catch { }
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(cur); i++)
            {
                string p = Path.Combine(cur, WarmExbName);
                if (File.Exists(p)) return p;
                cur = Path.GetDirectoryName(cur);
            }
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WarmExbName);
                if (File.Exists(p)) return p;
            }
            catch { }
            return null;
        }

        /// <summary>长等待：每步泵消息＋短睡（主线程禁裸长 Sleep），每 3 秒一行进度。</summary>
        private static bool WaitUntil(int timeoutMs, Func<bool> test, int stepMs, Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            long lastBeat = -1;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { if (test()) return true; } catch { }
                try { Application.DoEvents(); } catch { }
                Thread.Sleep(stepMs);
                long sec = sw.ElapsedMilliseconds / 3000;
                if (log != null && sec > lastBeat)
                {
                    lastBeat = sec;
                    log("warm: 等待中 " + (int)(sw.ElapsedMilliseconds / 1000) + "s（上限 " + (timeoutMs / 1000) + "s）");
                }
            }
            try { return test(); } catch { return false; }
        }

        private static void Log(Action<string> log, string msg)
        {
            if (log != null) log(msg);
        }
    }

    /// <summary>
    /// 图纸环境窗口取证（只做窗口判定，不碰 COM）。移植自 caxa3dButton\DrawingProbe.cs 的预热子集：
    /// ListDraftDocs / HasActiveDrawing / RequestClose / IsAlive / AnswerNoOnSaveDialog。
    /// 判据来源（v2027 实测）：图纸环境文档 → MDIClient 子窗口类名含 AfxFrameOrView140u；
    /// 3D 场景文档 → 类名 Afx:…:b:…，标题「设计1」。两者都是 MDIClient 直接子窗口，
    /// 只能按类名区分，禁用「有没有子窗口」当判据。
    /// </summary>
    internal static class DraftWindowProbe
    {
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;
        private const int WM_CLOSE = 0x0010;
        private const int WM_COMMAND = 0x0111;
        private const int IDNO = 7;          // 模态框「否」

        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string win);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

        public static bool HasActiveDrawing()
        {
            return ListDraftDocs().Count > 0;
        }

        public static List<IntPtr> ListDraftDocs()
        {
            var list = new List<IntPtr>();
            try
            {
                IntPtr frame = FindFrame(Process.GetCurrentProcess().Id);
                if (frame == IntPtr.Zero) return list;
                IntPtr mdi = FindMdiClient(frame);
                if (mdi == IntPtr.Zero) return list;
                for (IntPtr c = GetWindow(mdi, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT))
                    if (IsDraftWindow(c)) list.Add(c);
            }
            catch { }
            return list;
        }

        public static bool RequestClose(IntPtr h)
        {
            try { return PostMessageW(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
            catch { return false; }
        }

        public static bool IsAlive(IntPtr h)
        {
            try { return h != IntPtr.Zero && IsWindow(h); }
            catch { return false; }
        }

        public static string AnswerNoOnSaveDialog()
        {
            try
            {
                int pid = Process.GetCurrentProcess().Id;
                IntPtr hit = IntPtr.Zero;
                string title = null;
                EnumWindows(delegate (IntPtr h, IntPtr l)
                {
                    int p;
                    GetWindowThreadProcessId(h, out p);
                    if (p != pid || !IsWindowVisible(h)) return true;
                    if (!string.Equals(ClassName(h), "#32770", StringComparison.Ordinal)) return true;
                    hit = h;
                    title = WindowText(h);
                    return false;
                }, IntPtr.Zero);

                if (hit == IntPtr.Zero) return null;
                if (hit == _lastSaveDialog && (Environment.TickCount - _lastSaveDialogAt) < 3000) return null;

                _lastSaveDialog = hit;
                _lastSaveDialogAt = Environment.TickCount;
                PostMessageW(hit, WM_COMMAND, (IntPtr)IDNO, IntPtr.Zero);
                return "检测到模态框「" + (title ?? "") + "」→ 已代点「否」（不保存）";
            }
            catch { return null; }
        }

        private static IntPtr _lastSaveDialog;
        private static int _lastSaveDialogAt;

        private static IntPtr FindFrame(int pid)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                int wp;
                GetWindowThreadProcessId(h, out wp);
                if (wp != pid) return true;
                if (!IsWindowVisible(h)) return true;
                string t = WindowText(h);
                if (t.Length == 0) return true;

                if (FindMdiClient(h) != IntPtr.Zero)
                {
                    best = h;
                    return false;
                }

                RECT rc;
                if (GetClientRect(h, out rc))
                {
                    long area = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
                    if (area > bestArea) { bestArea = area; best = h; }
                }
                return true;
            }, IntPtr.Zero);

            return best;
        }

        private static IntPtr FindMdiClient(IntPtr top)
        {
            IntPtr mdi = FindWindowExW(top, IntPtr.Zero, "MDIClient", null);
            if (mdi != IntPtr.Zero) return mdi;

            IntPtr found = IntPtr.Zero;
            EnumChildWindows(top, delegate (IntPtr h, IntPtr l)
            {
                if (string.Equals(ClassName(h), "MDIClient", StringComparison.Ordinal)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsDraftWindow(IntPtr h)
        {
            return ClassName(h).IndexOf("AfxFrameOrView", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(128);
            try { GetClassNameW(h, sb, sb.Capacity); } catch { }
            return sb.ToString();
        }

        private static string WindowText(IntPtr h)
        {
            var sb = new StringBuilder(512);
            try { GetWindowTextW(h, sb, sb.Capacity); } catch { }
            return sb.ToString();
        }
    }
}
