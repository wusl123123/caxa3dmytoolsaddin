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
        public int ProtectedForeground;
        public int SkippedYoung;
        public int SkippedOtherSession;
        public bool Canceled;

        public string Summary()
        {
            return string.Format(
                "杀死 {0} 个，失败 {1} 个，保护前台 {2} 个，跳过(启动保护/其他会话) {3}/{4}",
                KilledPids.Count, Failures.Count, ProtectedForeground,
                SkippedYoung, SkippedOtherSession);
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
                }
                catch (InvalidOperationException) { }
                catch (Exception ex) { if (verbose) log("扫描进程出错：" + ex.Message); }
                finally { p.Dispose(); }
            }

            if (foreground > result.ProtectedForeground) result.ProtectedForeground = foreground;
            if (verbose) log(string.Format(
                "扫描：IRONCAD 进程 {0} 个，保护前台 {1} 个，候选 {2} 个",
                procs.Length, foreground, candidates.Count));
            return candidates;
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
                    p = Process.GetProcessById(cand.Pid);
                    if (p.HasExited)
                    {
                        log(string.Format("pid={0} 已自行退出", cand.Pid));
                        continue;
                    }
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
