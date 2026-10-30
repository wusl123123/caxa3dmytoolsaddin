using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3didcskill
{
    /// <summary>
    /// 清理 CAXA 3D 设计环境残留的无窗口后台进程。
    /// 任务管理器中这类进程显示为"IRONCAD Design Collaboration Suite"，
    /// 实际进程名同为 IRONCAD.exe，但没有可见主窗口。
    /// 仅杀死无可见窗口的后台实例，带窗口的前台 CAXA 3D 一律保护。
    /// </summary>
    internal static class ProcessKiller
    {
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3didcskill_trace.log");

        /// <summary>前台 CAXA 3D 启动后尚在加载、窗口未出现的保护窗口（秒）。</summary>
        private const int YoungProtectSeconds = 15;

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>自包含日志（不再依赖原 Program.cs）。</summary>
        private static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [tid " +
                Thread.CurrentThread.ManagedThreadId + "] " + msg;
            try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        }

        private static bool ProcessHasVisibleTopWindow(int pid)
        {
            bool found = false;
            EnumWindowsProc cb = delegate (IntPtr hWnd, IntPtr lParam)
            {
                int wp;
                GetWindowThreadProcessId(hWnd, out wp);
                if (wp == pid && IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0)
                {
                    found = true;
                    return false;
                }
                return true;
            };
            try { EnumWindows(cb, IntPtr.Zero); } catch { }
            return found;
        }

        public static string GetMainWindowTitle(int pid)
        {
            string title = null;
            EnumWindowsProc cb = delegate (IntPtr hWnd, IntPtr lParam)
            {
                int wp;
                GetWindowThreadProcessId(hWnd, out wp);
                if (wp == pid && IsWindowVisible(hWnd))
                {
                    int len = GetWindowTextLength(hWnd);
                    if (len > 0)
                    {
                        var sb = new StringBuilder(len + 1);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        title = sb.ToString();
                        return false;
                    }
                }
                return true;
            };
            try { EnumWindows(cb, IntPtr.Zero); } catch { }
            return title;
        }

        public static bool HasForegroundCaxa()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("IRONCAD"))
                {
                    try
                    {
                        if (ProcessHasVisibleTopWindow(p.Id)) return true;
                    }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex) { Log("HasForegroundCaxa failed: " + ex.Message); }
            return false;
        }

        public static int CountForegroundCaxa()
        {
            int n = 0;
            try
            {
                foreach (Process p in Process.GetProcessesByName("IRONCAD"))
                {
                    try
                    {
                        if (ProcessHasVisibleTopWindow(p.Id)) n++;
                    }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex) { Log("CountForegroundCaxa failed: " + ex.Message); }
            return n;
        }

        public static int KillBackgroundIroncadProcesses()
        {
            int killed = 0;
            foreach (Process p in Process.GetProcessesByName("IRONCAD"))
            {
                try
                {
                    if (ProcessHasVisibleTopWindow(p.Id))
                    {
                        Log(string.Format("cleanup: 保护前台 CAXA 3D pid={0}（有可见窗口）", p.Id));
                        continue;
                    }

                    try
                    {
                        if (p.StartTime > DateTime.Now.AddSeconds(-YoungProtectSeconds))
                        {
                            Log(string.Format(
                                "cleanup: 跳过刚启动的 IRONCAD pid={0}（启动 {1} 秒内，避免误杀正在加载的前台实例）",
                                p.Id, YoungProtectSeconds));
                            continue;
                        }
                    }
                    catch { }

                    Log(string.Format(
                        "cleanup: 杀死后台进程 pid={0}（无可见窗口，IRONCAD Design Collaboration Suite）", p.Id));
                    p.Kill();
                    p.WaitForExit(5000);
                    killed++;
                }
                catch (Exception ex)
                {
                    Log("cleanup: kill failed: " + ex.Message);
                }
                finally { p.Dispose(); }
            }
            return killed;
        }
    }
}
