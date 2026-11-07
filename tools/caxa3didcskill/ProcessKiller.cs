using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3didcskill
{
    internal sealed class CleanupResult
    {
        public readonly List<int> KilledPids = new List<int>();
        public readonly List<string> Failures = new List<string>();
        public readonly List<string> StuckExiting = new List<string>();
        public readonly List<int> Vanished = new List<int>();
        public int ProtectedForeground;
        public int SkippedYoung;
        public int SkippedOtherSession;
        public bool Canceled;

        public string Summary()
        {
            return string.Format(
                "杀死 {0} 个，失败 {1} 个，卡在退出 {2} 个，自行退出 {3} 个，保护前台 {4} 个，跳过(启动保护/其他会话) {5}/{6}",
                KilledPids.Count, Failures.Count, StuckExiting.Count, Vanished.Count,
                ProtectedForeground, SkippedYoung, SkippedOtherSession);
        }

        /// <summary>状态行用的短口径：396 像素宽的单行标签放不下全口径，只保留六档计数。</summary>
        public string SummaryShort()
        {
            return string.Format(
                "杀死 {0}｜失败 {1}｜卡在退出 {2}｜自行退出 {3}｜保护 {4}｜跳过 {5}/{6}",
                KilledPids.Count, Failures.Count, StuckExiting.Count, Vanished.Count,
                ProtectedForeground, SkippedYoung, SkippedOtherSession);
        }
    }

    /// <summary>
    /// 清理 CAXA 3D 残留的无窗口后台进程（任务管理器显示为
    /// "IRONCAD Design Collaboration Suite"，进程名同为 IRONCAD.exe）。
    /// 安全策略：带可见窗口的前台实例一律保护；排除自身 PID；只处理当前会话；
    /// 启动 60 秒内的实例保护（规则 16.7：主窗口约 30~45 秒才出现）；
    /// 连续 3 次扫描仍无窗口才杀，杀后重新枚举校验。
    /// </summary>
    internal static class ProcessKiller
    {
        private const string TargetProcessName = "IRONCAD";
        private const int YoungProtectSeconds = 60;
        private const int ConfirmScans = 3;
        private const int ScanIntervalMs = 1500;
        private const int ExitWaitMs = 5000;

        private sealed class WindowInfo
        {
            public string ClassName;
            public string Title;
        }

        private sealed class Candidate
        {
            public int Pid;
            public DateTime StartTime;
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // ===== Win32 显式取证（三态判据，规则 16.8）=====
        // 背景：Process.GetProcessesByName 能枚举到 ≠ 进程一定还活着；
        //       .NET HasExited 遇 OpenProcess 被拒时抛 Win32Exception（实测），
        //       它返回 true 是 OS 真实报告的退出态。故“活着/已退出/打不开”必须分开记。

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint SYNCHRONIZE = 0x00100000;
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_TIMEOUT = 258;
        private const uint WAIT_FAILED = 0xFFFFFFFF;
        private const uint STILL_ACTIVE = 259;   // 与“合法退出码恰好是 259”同值，歧义已在日志中注明

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags,
            StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        // 注意：必须显式 CharSet.Unicode。缺省时 DllImport 走 ANSI 入口点，
        // 会把 ACP 字节写进 ByValTStr(260) 的 WCHAR 数组，szExeFile 全成乱码
        // （实测：IRONCAD.exe 在表里，名字匹配却报 0 个）。整型字段偏移不受影响。
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        /// <summary>一个 pid 的原生三态快照。所有字段读不到时保持默认值并记入 Note，绝不猜。</summary>
        private sealed class NativeState
        {
            public bool InToolhelpTable;      // Toolhelp32 进程表里有这个 pid
            public uint OsThreads;            // 进程表里报告的线程数（未命中则 0＝未知）
            public uint ParentPid;
            public string ExeFile = "";
            public bool HandleOpened;
            public int OpenErr;
            public bool Signalled;            // WaitForSingleObject(h,0)==WAIT_OBJECT_0
            public bool WaitFailed;
            public int WaitErr;
            public uint ExitCode;
            public bool ExitCodeKnown;
            public string ImagePath = "";
            public string Verdict = "未知";

            public string Describe()
            {
                return string.Format(
                    "进程表={0} 线程={1} 父pid={2} 句柄={3}(err={4}) 等待={5} 退出码={6} 镜像={7} 判定={8}",
                    InToolhelpTable ? "有" : "无",
                    InToolhelpTable ? OsThreads.ToString() : "?",
                    ParentPid,
                    HandleOpened ? "可开" : "打不开", OpenErr,
                    WaitFailed ? "FAILED(" + WaitErr + ")" : (Signalled ? "已退出" : "活着"),
                    ExitCodeKnown ? ExitCode.ToString() : "?",
                    string.IsNullOrEmpty(ImagePath) ? (string.IsNullOrEmpty(ExeFile) ? "?" : ExeFile) : ImagePath,
                    Verdict);
            }
        }

        /// <summary>Toolhelp32 进程表：pid → 条目。这是“任务管理器里看没看得见”的同源判据。</summary>
        private static PROCESSENTRY32? FindInProcessTable(int pid)
        {
            IntPtr snap = CreateToolhelp32Snapshot(0x00000002 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == IntPtr.Zero || (long)snap == -1) return null;
            try
            {
                PROCESSENTRY32 e = new PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snap, ref e)) return null;
                do
                {
                    if (e.th32ProcessID == (uint)pid) return e;
                }
                while (Process32Next(snap, ref e));
                return null;
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        private static NativeState ProbeNative(int pid)
        {
            NativeState s = new NativeState();

            PROCESSENTRY32? entry = FindInProcessTable(pid);
            if (entry.HasValue)
            {
                s.InToolhelpTable = true;
                s.OsThreads = entry.Value.cntThreads;
                s.ParentPid = entry.Value.th32ParentProcessID;
                s.ExeFile = entry.Value.szExeFile;
            }

            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, (uint)pid);
            if (h == IntPtr.Zero)
            {
                s.OpenErr = Marshal.GetLastWin32Error();
            }
            else
            {
                s.HandleOpened = true;
                try
                {
                    uint w = WaitForSingleObject(h, 0);
                    if (w == WAIT_FAILED)
                    {
                        s.WaitFailed = true;
                        s.WaitErr = Marshal.GetLastWin32Error();
                    }
                    else
                    {
                        s.Signalled = (w == WAIT_OBJECT_0);
                    }

                    uint code;
                    if (GetExitCodeProcess(h, out code))
                    {
                        s.ExitCodeKnown = true;
                        s.ExitCode = code;
                    }

                    var sb = new StringBuilder(1024);
                    uint len = (uint)sb.Capacity;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref len)) s.ImagePath = sb.ToString();
                }
                finally
                {
                    CloseHandle(h);
                }
            }

            s.Verdict = Classify(s);
            return s;
        }

        private static string Classify(NativeState s)
        {
            // 进程表（Toolhelp32＝任务管理器同源）与句柄是两个独立事实，必须分开说：
            //   表里没有＋句柄报 87/3 ⇒ 真没了；表里没有＋句柄仍可开 ⇒ 对象被引用的退出态；
            //   表里有＋句柄打不开 ⇒ 第三态（权限），绝不写成“已退出”。
            const int ERROR_ACCESS_DENIED = 5;
            const int ERROR_INVALID_PARAMETER = 87;
            const int ERROR_FILE_NOT_FOUND = 2;
            const int ERROR_PATH_NOT_FOUND = 3;

            if (!s.InToolhelpTable)
            {
                if (s.HandleOpened) return "已退出，进程表已无（对象仍被引用）";
                if (s.OpenErr == ERROR_INVALID_PARAMETER || s.OpenErr == ERROR_FILE_NOT_FOUND ||
                    s.OpenErr == ERROR_PATH_NOT_FOUND) return "已退出且已从进程表消失";
                return "进程表已无且句柄打不开(err=" + s.OpenErr + ")";
            }

            if (!s.HandleOpened)
                return s.OpenErr == ERROR_ACCESS_DENIED ? "打不开(拒绝访问，需管理员)" : "打不开(第三态 err=" + s.OpenErr + ")";

            bool exited = s.Signalled || (s.ExitCodeKnown && s.ExitCode != STILL_ACTIVE);
            if (!exited) return "活着";
            return s.OsThreads > 0
                ? "已置退出码但仍有 " + s.OsThreads + " 个线程（退出挂起）"
                : "已置退出码、无残留线程但仍在进程表（对象被引用）";
        }

        /// <summary>.NET HasExited 的真值：异常也算“读不到”，不当成“已退出”。</summary>
        private static bool DotNetHasExited(int pid, out string note)
        {
            note = "";
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    try { return p.HasExited; }
                    catch (Exception ex) { note = "HasExited 异常：" + ex.GetType().Name + "(" + ex.Message + ")"; return false; }
                }
            }
            catch (Exception ex)
            {
                note = "GetProcessById 异常：" + ex.GetType().Name + "(" + ex.Message + ")";
                return false;
            }
        }

        /// <summary>显式 TerminateProcess。返回是否投递成功；成功不等于进程消失，调用方必须再取证。</summary>
        private static bool ForceTerminate(int pid, out bool openFailed)
        {
            openFailed = false;
            IntPtr h = OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION,
                false, (uint)pid);
            if (h == IntPtr.Zero) { openFailed = true; return false; }
            try { return TerminateProcess(h, 0); }
            finally { CloseHandle(h); }
        }

        /// <summary>一次 EnumWindows 取全量快照：pid → 可见且带标题的顶层窗口列表。</summary>
        private static Dictionary<int, List<WindowInfo>> CaptureVisibleTopWindows()
        {
            var map = new Dictionary<int, List<WindowInfo>>();
            EnumWindowsProc cb = delegate (IntPtr hWnd, IntPtr lParam)
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    int len = GetWindowTextLength(hWnd);
                    if (len <= 0) return true;

                    int pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == 0) return true;

                    var title = new StringBuilder(len + 1);
                    GetWindowText(hWnd, title, title.Capacity);

                    var cls = new StringBuilder(256);
                    GetClassName(hWnd, cls, cls.Capacity);

                    List<WindowInfo> list;
                    if (!map.TryGetValue(pid, out list))
                    {
                        list = new List<WindowInfo>();
                        map[pid] = list;
                    }
                    list.Add(new WindowInfo { ClassName = cls.ToString(), Title = title.ToString() });
                }
                catch { }
                return true;
            };

            try { EnumWindows(cb, IntPtr.Zero); } catch { }
            return map;
        }

        /// <summary>规则 16.4：同进程内的 WinForms 窗口（如工具自身）不算 CAXA 主窗口。</summary>
        private static bool IsProtectiveWindow(WindowInfo w)
        {
            return !w.ClassName.StartsWith("WindowsForms10.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasProtectiveWindow(List<WindowInfo> windows)
        {
            if (windows == null) return false;
            foreach (WindowInfo w in windows)
            {
                if (IsProtectiveWindow(w)) return true;
            }
            return false;
        }

        /// <summary>精确比对 PID + 启动时间，防 PID 复用。</summary>
        private static bool IsSameProcessAlive(int pid, DateTime startTime)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) return false;
                    try { return Math.Abs((p.StartTime - startTime).TotalSeconds) < 1; }
                    catch { return true; }
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch { return true; }
        }

        private static bool WaitExit(Process p, int timeoutMs, CancellationToken token)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                try { if (p.HasExited) return true; } catch { return true; }
                if (token.IsCancellationRequested) return false;

                int step = Math.Min(250, timeoutMs - waited);
                try { if (p.WaitForExit(step)) return true; } catch { return true; }
                waited += step;
            }
            try { return p.HasExited; } catch { return true; }
        }

        private static Dictionary<int, Candidate> ScanCandidates(
            int selfPid, int sessionId, CleanupResult result, Action<string> log, bool verbose)
        {
            Dictionary<int, List<WindowInfo>> snapshot = CaptureVisibleTopWindows();
            var candidates = new Dictionary<int, Candidate>();

            Process[] procs;
            try { procs = Process.GetProcessesByName(TargetProcessName); }
            catch (Exception ex)
            {
                log("枚举 IRONCAD 进程失败：" + ex.Message);
                return candidates;
            }

            int foreground = 0;
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id == selfPid) continue;

                    List<WindowInfo> wins;
                    snapshot.TryGetValue(p.Id, out wins);
                    if (HasProtectiveWindow(wins))
                    {
                        foreground++;
                        if (verbose) log(string.Format("保护前台 pid={0}（可见窗口：{1}）", p.Id, FirstTitle(wins)));
                        continue;
                    }

                    if (p.SessionId != sessionId)
                    {
                        if (verbose)
                        {
                            result.SkippedOtherSession++;
                            log(string.Format("跳过 pid={0}：会话 {1}，与当前会话 {2} 不同",
                                p.Id, p.SessionId, sessionId));
                        }
                        continue;
                    }

                    DateTime start;
                    try { start = p.StartTime; }
                    catch (Exception ex)
                    {
                        if (verbose) log(string.Format("跳过 pid={0}：无法读取启动时间（{1}）", p.Id, ex.Message));
                        continue;
                    }

                    if ((DateTime.Now - start).TotalSeconds < YoungProtectSeconds)
                    {
                        if (verbose)
                        {
                            result.SkippedYoung++;
                            log(string.Format(
                                "跳过 pid={0}：启动不足 {1} 秒，可能在加载主窗口", p.Id, YoungProtectSeconds));
                        }
                        continue;
                    }

                    candidates[p.Id] = new Candidate { Pid = p.Id, StartTime = start };
                    if (verbose)
                    {
                        NativeState st = ProbeNative(p.Id);
                        log(string.Format("候选 pid={0}：{1}", p.Id, st.Describe()));
                    }
                }
                catch (InvalidOperationException) { }
                catch (Exception ex) { if (verbose) log("扫描进程出错：" + ex.Message); }
                finally { p.Dispose(); }
            }

            if (foreground > result.ProtectedForeground) result.ProtectedForeground = foreground;
            if (verbose) log(string.Format(
                "扫描：IRONCAD 进程 {0} 个，保护前台 {1} 个，候选 {2} 个",
                procs.Length, foreground, candidates.Count));
            if (verbose) LogIronFamily(snapshot, log);
            return candidates;
        }

        /// <summary>
        /// 名字含 IRON/CAXA 的全部进程对照（含可见窗口数）。
        /// 用途：任务管理器显示的“IRONCAD Design Collaboration Suite”未必是 IRONCAD.exe，
        /// 先把“到底是哪个进程没走”钉死，再谈杀不杀。
        /// </summary>
        private static void LogIronFamily(Dictionary<int, List<WindowInfo>> snapshot, Action<string> log)
        {
            IntPtr snap = CreateToolhelp32Snapshot(0x00000002, 0);
            if (snap == IntPtr.Zero || (long)snap == -1)
            {
                log("对照快照：Toolhelp32 失败(Win32=" + Marshal.GetLastWin32Error() + ")");
                return;
            }
            try
            {
                PROCESSENTRY32 e = new PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snap, ref e))
                {
                    log("对照快照：Process32First 失败(Win32=" + Marshal.GetLastWin32Error() + ")");
                    return;
                }
                int n = 0;
                do
                {
                    string name = e.szExeFile == null ? "" : e.szExeFile;
                    if (name.IndexOf("IRON", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("CAXA", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    List<WindowInfo> wins;
                    snapshot.TryGetValue((int)e.th32ProcessID, out wins);
                    int winCount = wins == null ? 0 : wins.Count;
                    log(string.Format(
                        "对照 pid={0} 名={1} 线程={2} 父pid={3} 可见顶层窗口={4}{5}",
                        e.th32ProcessID, name, e.cntThreads, e.th32ParentProcessID, winCount,
                        winCount > 0 ? "（首个：" + wins[0].Title + "）" : ""));
                    n++;
                }
                while (Process32Next(snap, ref e));
                log(string.Format("对照快照：名字含 IRON/CAXA 的进程共 {0} 个", n));
            }
            catch (Exception ex)
            {
                log("对照快照异常：" + ex.Message);
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        private static string FirstTitle(List<WindowInfo> windows)
        {
            if (windows == null) return "";
            foreach (WindowInfo w in windows)
            {
                if (IsProtectiveWindow(w)) return w.Title;
            }
            return "";
        }

        public static CleanupResult Run(CancellationToken token, Action<string> log)
        {
            var result = new CleanupResult();
            int selfPid = Process.GetCurrentProcess().Id;
            int sessionId = Process.GetCurrentProcess().SessionId;

            log(string.Format("清理开始：自身 pid={0}，会话 {1}，目标进程名 {2}",
                selfPid, sessionId, TargetProcessName));

            // 连续扫描确认：窗口尚未出现的“正在启动”实例需要跨时间观察
            Dictionary<int, Candidate> pending = null;
            for (int i = 1; i <= ConfirmScans; i++)
            {
                if (token.IsCancellationRequested)
                {
                    result.Canceled = true;
                    log("已在扫描阶段停止，未杀任何进程");
                    return result;
                }

                Dictionary<int, Candidate> scan = ScanCandidates(selfPid, sessionId, result, log, i == 1);

                if (pending == null)
                {
                    pending = scan;
                }
                else
                {
                    var keep = new Dictionary<int, Candidate>();
                    foreach (KeyValuePair<int, Candidate> kv in pending)
                    {
                        Candidate now;
                        if (scan.TryGetValue(kv.Key, out now) &&
                            Math.Abs((now.StartTime - kv.Value.StartTime).TotalSeconds) < 1)
                        {
                            keep[kv.Key] = now;
                        }
                    }
                    pending = keep;
                }

                log(string.Format("第 {0}/{1} 次确认扫描：仍无可见窗口的候选 {2} 个",
                    i, ConfirmScans, pending.Count));

                if (pending.Count == 0)
                {
                    log("没有需要清理的后台进程");
                    return result;
                }

                if (i < ConfirmScans && token.WaitHandle.WaitOne(ScanIntervalMs))
                {
                    result.Canceled = true;
                    log("已在确认阶段停止，未杀任何进程");
                    return result;
                }
            }

            foreach (Candidate cand in pending.Values)
            {
                if (token.IsCancellationRequested)
                {
                    result.Canceled = true;
                    log("已在清理阶段停止");
                    return result;
                }

                // 最终快照：确认窗口仍未出现（避免杀掉刚好启动完成的前台实例）
                Dictionary<int, List<WindowInfo>> finalSnap = CaptureVisibleTopWindows();
                List<WindowInfo> wins;
                if (finalSnap.TryGetValue(cand.Pid, out wins) && HasProtectiveWindow(wins))
                {
                    log(string.Format("pid={0} 已出现可见窗口，改为保护", cand.Pid));
                    continue;
                }

                Process p = null;
                try
                {
                    // 原生三态取证先行：进程表(Toolhelp32) / 句柄 / 事件状态 / 退出码 / 线程数 / 镜像路径
                    NativeState st = ProbeNative(cand.Pid);
                    string dotNote;
                    bool dotExited = DotNetHasExited(cand.Pid, out dotNote);
                    log(string.Format("取证 pid={0}：{1} ｜ .NET HasExited={2}{3}",
                        cand.Pid, st.Describe(), dotExited,
                        string.IsNullOrEmpty(dotNote) ? "" : "（" + dotNote + "）"));

                    if (!st.InToolhelpTable)
                    {
                        // 进程表里已经没有了：真的自行退出，不算失败也不算杀死
                        result.Vanished.Add(cand.Pid);
                        log(string.Format("pid={0} 已不在进程表，确认自行退出（不计入失败）", cand.Pid));
                        continue;
                    }

                    if (!st.HandleOpened)
                    {
                        result.Failures.Add(string.Format(
                            "pid={0}：进程表里有但句柄打不开（Win32={1}），需以管理员身份运行",
                            cand.Pid, st.OpenErr));
                        log(result.Failures[result.Failures.Count - 1]);
                        continue;
                    }

                    bool alive = !st.Signalled && (!st.ExitCodeKnown || st.ExitCode == STILL_ACTIVE);
                    if (!alive)
                    {
                        // OS 已给出退出码，但进程仍占着进程表 ⇒ 该进程卡在“正在退出”，
                        // 残留线程没走完（本机实测：正常退出的进程即使被持有句柄也不会留在表里）。
                        // 此处仍必须试一次 TerminateProcess，否则“杀不掉”只是断言不是实证。
                        string why = st.OsThreads > 0
                            ? "仍有 " + st.OsThreads + " 个线程未结束（退出挂起）"
                            : "无残留线程但仍在进程表";
                        bool termOpenFailed;
                        bool term = ForceTerminate(cand.Pid, out termOpenFailed);
                        int termWin32 = term ? 0 : Marshal.GetLastWin32Error();
                        if (token.WaitHandle.WaitOne(1500))
                        {
                            result.Canceled = true;
                            log("已在退出挂起处理阶段停止");
                            return result;
                        }
                        NativeState after2 = ProbeNative(cand.Pid);
                        result.StuckExiting.Add(string.Format("pid={0}", cand.Pid));
                        log(string.Format(
                            "pid={0} 已置退出码（{1}）但仍在进程表：TerminateProcess={2}，1.5 秒后进程表={3}（线程 {4}）",
                            cand.Pid, why,
                            term ? "已投递" : "失败(Win32=" + termWin32 + ")",
                            after2.InToolhelpTable ? "仍在" : "已消失",
                            after2.InToolhelpTable ? after2.OsThreads.ToString() : "?"));
                        if (after2.InToolhelpTable)
                        {
                            log(string.Format(
                                "pid={0} 无法由本工具清除：它已在退出流程中，杀进程对它无效。请查它卡在什么资源上" +
                                "（网络盘/USB/驱动/DLL 卸载），或注销/重启系统。", cand.Pid));
                        }
                        continue;
                    }

                    p = Process.GetProcessById(cand.Pid);
                    if (!string.Equals(p.ProcessName, TargetProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        log(string.Format("跳过 pid={0}：进程名已变化，疑似 PID 复用", cand.Pid));
                        continue;
                    }
                    if (Math.Abs((p.StartTime - cand.StartTime).TotalSeconds) >= 1)
                    {
                        log(string.Format("跳过 pid={0}：启动时间不匹配，疑似 PID 复用", cand.Pid));
                        continue;
                    }

                    log(string.Format("杀死后台进程 pid={0}（连续 {1} 次扫描无可见窗口）",
                        cand.Pid, ConfirmScans));
                    p.Kill();

                    if (!WaitExit(p, ExitWaitMs, token))
                    {
                        result.Failures.Add(string.Format("pid={0}：{1} 秒内未退出", cand.Pid, ExitWaitMs / 1000));
                        log(result.Failures[result.Failures.Count - 1]);
                        continue;
                    }

                    // 判成功以“重新枚举后确已消失”为准，不看 Kill() 返回值
                    if (IsSameProcessAlive(cand.Pid, cand.StartTime))
                    {
                        result.Failures.Add(string.Format("pid={0}：退出后仍能枚举到", cand.Pid));
                        log(result.Failures[result.Failures.Count - 1]);
                        continue;
                    }

                    result.KilledPids.Add(cand.Pid);
                    log(string.Format("pid={0} 已确认退出", cand.Pid));
                }
                catch (Exception ex)
                {
                    result.Failures.Add(string.Format("pid={0}：{1}", cand.Pid, ex.Message));
                    log(string.Format("杀死 pid={0} 失败：{1}", cand.Pid, ex));
                }
                finally
                {
                    if (p != null) p.Dispose();
                }
            }

            log("清理结束：" + result.Summary());
            return result;
        }
    }
}
