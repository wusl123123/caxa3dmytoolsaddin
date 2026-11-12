// 图纸环境窗口取证（只做窗口判定，不碰 COM）
// 移植自 caxa3dtracker\DrawingProbe.cs 的最小可用子集，只保留「图纸环境预热」所需：
//   ListDraftDocs / HasActiveDrawing / RequestClose / IsAlive / AnswerNoOnSaveDialog。
// 判据来源（v2027 实测）：
//   图纸环境文档 → MDIClient 子窗口类名含 AfxFrameOrView140u；
//   3D 场景文档   → 类名 Afx:00007FF7...:b:...，标题「设计1」。
//   两者都是 MDIClient 的直接子窗口，只能按类名区分，禁用「有没有子窗口」当判据。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace caxa3dButton
{
    internal static class DrawingProbe
    {
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;
        private const int WM_CLOSE = 0x0010;
        private const int WM_COMMAND = 0x0111;
        private const int IDNO = 7;          // 模态框「否」

        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
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

        /// <summary>轻量判据：本进程主框架的 MDIClient 下是否已有 Draft 图纸窗口。</summary>
        public static bool HasActiveDrawing()
        {
            return ListDraftDocs().Count > 0;
        }

        /// <summary>列出当前所有 Draft 图纸文档窗口句柄（MDIClient 直接子窗口，类名含 AfxFrameOrView）。</summary>
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

        /// <summary>对 MDI 子窗口投 WM_CLOSE（异步；是否真关掉要调用方轮询 IsAlive）。</summary>
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

        /// <summary>若 CAXA 弹了「是否保存」模态框，替用户点「否」；同一张框 3 秒内不重复投。</summary>
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
                    return false;   // 只处理最上面一张
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

        /// <summary>主框架：优先「含 MDIClient 的可见顶层窗口」，否则取面积最大的可见带标题窗口。</summary>
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
                    return false;                       // 找到主框架，停止枚举
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
