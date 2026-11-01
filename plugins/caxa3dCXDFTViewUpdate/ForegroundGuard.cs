using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dCXDFTViewUpdate
{
    /// <summary>
    /// 前台抑制器（方案 A）：批处理期间让图纸环境进程始终留在后台，不抢占前台。
    ///
    /// 背景（2026-10-25 取证结论）：更新 exb 时"切到 CAXA 3D 前台"不是本插件发出的——
    /// C# 与 CRX 两侧都没有任何设置前台窗口的调用。真正的原因是 CAXA 3D 冷启动图纸
    /// 环境进程（CDRAFT_M_IC.exe）时，新进程的主框架会激活自己：它由当时处于前台的
    /// IRONCAD 派生，按 Windows 规则「前台进程派生的子进程有权设置前台窗口」就能抢到，
    /// 于是整机前台被切过去。反之，当图纸进程早已存在（用户先做过 exb 操作）时，
    /// exb 经 DDE 投递进已有实例，只是在后台进程里开 MDI 子文档，没有前台继承权，
    /// 所以全程安静。
    ///
    /// 对策（只在本插件、本批次期间生效，跟批处理开关一起启停）：
    ///   1) 把图纸进程的可见顶层窗口最小化（跳过 #32770 对话框，弹窗仍交给 PopupAutoYes），
    ///      让它既不到前台、也不占屏幕；
    ///   2) 一旦发现前台窗口属于图纸进程，立刻把前台还给「被夺之前用户正在用的那个窗口」。
    /// 图纸进程本身不需要用户交互（命令走 CRX 文件桥，关闭走 WM_CLOSE 投递），
    /// 所以最小化不影响批处理功能。窗口操作一律用 ShowWindowAsync，不阻塞本线程。
    /// </summary>
    internal static class ForegroundGuard
    {
        private const int TickMs = 100;                 // 探测周期
        private const int ThrottleMs = 500;             // 同一窗口两次最小化请求的最小间隔
        private const int MaxRequestPerWindow = 100;    // 同一窗口最多请求多少次（防最小化/还原拉锯）
        private const int ProcCacheMs = 1000;           // 进程名缓存有效期
        private const int SW_MINIMIZE = 6;

        /// <summary>图纸环境进程名。可执行体是
        /// &lt;CAXA 3D 安装目录&gt;\&lt;版本&gt;\bin\CAXADraft\Bin\CDRAFT_M_IC.exe——
        /// 安装目录里并没有 CAXADraft.exe，不要按目录名写成 CAXADraft。</summary>
        private static readonly string[] DraftProcessNames = { "CDRAFT_M_IC" };

        private static volatile bool _enabled;
        private static Thread _thread;

        // ---- 以下状态只由抑制线程读写（Enabled 的 setter 不碰），因此无需加锁 ----
        private static readonly Dictionary<IntPtr, long> _lastRequest = new Dictionary<IntPtr, long>();
        private static readonly Dictionary<IntPtr, int> _requestCount = new Dictionary<IntPtr, int>();
        private static readonly HashSet<IntPtr> _gaveUp = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> _logOnce = new HashSet<IntPtr>();
        private static readonly Dictionary<uint, KeyValuePair<string, long>> _procCache =
            new Dictionary<uint, KeyValuePair<string, long>>();
        private static IntPtr _lastUserFg = IntPtr.Zero;   // 被夺之前用户正在用的前台窗口
        private static bool _inSteal;                      // 当前是否正处于"被夺"状态（用于去重计数）

        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (value == _enabled) return;
                _enabled = value;
                if (value) EnsureThread();     // 开/关的日志与状态复位由线程自己处理
            }
        }

        private static void EnsureThread()
        {
            if (_thread != null && _thread.IsAlive) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ForegroundGuard" };
            _thread.Start();
        }

        private static void Loop()
        {
            bool wasEnabled = false;
            int minReq = 0, steals = 0;       // 每批次的统计（只在本线程用）
            while (true)
            {
                Thread.Sleep(TickMs);
                try
                {
                    if (!_enabled)
                    {
                        if (wasEnabled)
                        {
                            wasEnabled = false;
                            Diagnostics.Log("前台抑制器：停用（本批次最小化窗口请求 " + minReq
                                + " 次，拦截抢前台 " + steals + " 次）");
                        }
                        continue;
                    }

                    if (!wasEnabled)
                    {
                        wasEnabled = true;
                        _lastRequest.Clear(); _requestCount.Clear(); _gaveUp.Clear();
                        _logOnce.Clear(); _procCache.Clear();
                        _inSteal = false; minReq = 0; steals = 0;
                        _lastUserFg = GetForegroundWindow();
                        Diagnostics.Log("前台抑制器：启用（匹配进程 " + string.Join("/", DraftProcessNames)
                            + "，当前图纸进程：" + DraftSnapshot()
                            + "，前台归还目标「" + Describe(_lastUserFg) + "」）");
                    }

                    Tick(ref minReq, ref steals);
                }
                catch (Exception ex) { Diagnostics.Trace("ForegroundGuard: " + ex.Message); }
            }
        }

        private static void Tick(ref int minReq, ref int steals)
        {
            IntPtr fg = GetForegroundWindow();
            bool fgIsDraft = fg != IntPtr.Zero && IsDraftWindow(fg);

            // 1) 图纸进程的可见顶层窗口一律压到最小化（对话框除外）
            minReq += MinimizeDraftWindows();

            if (fgIsDraft)
            {
                if (!_inSteal)                 // 只在"刚被夺走"这一刻计数与写日志，避免每 100ms 刷屏
                {
                    _inSteal = true;
                    steals++;
                    IntPtr back = _lastUserFg;
                    if (IsUsableTarget(back))
                    {
                        bool ok = ForceForeground(back);
                        Diagnostics.Log("前台抑制器：图纸进程抢到前台（" + Describe(fg)
                            + "）→ 已最小化，前台归还「" + Describe(back) + "」"
                            + (ok ? "" : "（归还请求未被系统接受，已尽力）"));
                    }
                    else
                    {
                        Diagnostics.Log("前台抑制器：图纸进程抢到前台（" + Describe(fg)
                            + "）→ 已最小化（无可用归还目标）");
                    }
                }
            }
            else
            {
                _inSteal = false;
                // 记住用户当前在用的窗口：下次被夺时就还给"它"（比固定还给某个窗口更贴合实际）
                if (fg != IntPtr.Zero) _lastUserFg = fg;
            }
        }

        /// <summary>最小化图纸进程的可见顶层窗口，返回本次发出的最小化请求数。跳过 #32770 对话框：
        /// 弹窗要留给 PopupAutoYes 点，最小化模态框会干扰 CAXA 自己的流程。</summary>
        private static int MinimizeDraftWindows()
        {
            int reqCount = 0;
            long now = Environment.TickCount;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;       // 本来就隐藏的不管
                if (IsIconic(h)) return true;               // 已经最小化
                if (!IsDraftWindow(h)) return true;         // 不是图纸进程的窗口
                if (GetClassText(h) == "#32770") return true;   // 对话框交给 PopupAutoYes

                if (_gaveUp.Contains(h)) return true;
                long last;
                if (_lastRequest.TryGetValue(h, out last) && unchecked(now - last) < ThrottleMs) return true;

                int n;
                _requestCount.TryGetValue(h, out n);
                if (n >= MaxRequestPerWindow)
                {
                    _gaveUp.Add(h);
                    Diagnostics.Log("前台抑制器：放弃继续最小化「" + Describe(h)
                        + "」（已请求 " + n + " 次仍未保持最小化，可能该窗口被 CAXA 反复还原）");
                    return true;
                }
                _requestCount[h] = n + 1;
                _lastRequest[h] = now;

                ShowWindowAsync(h, SW_MINIMIZE);            // 异步：图纸进程忙时不阻塞本线程
                reqCount++;
                if (_logOnce.Add(h))
                    Diagnostics.Log("前台抑制器：最小化图纸进程窗口「" + Describe(h) + "」");
                return true;
            }, IntPtr.Zero);
            return reqCount;
        }

        // ================================================================ 前台归还

        /// <summary>把前台交给 target。本进程此刻并不是前台进程，直接调用
        /// SetForegroundWindow 大概率被系统拒绝，所以先把本线程与当前前台线程的
        /// 输入队列临时合并（AttachThreadInput），再设置前台，最后解除合并。</summary>
        private static bool ForceForeground(IntPtr target)
        {
            bool ok = false;
            IntPtr cur = GetForegroundWindow();
            uint pid;
            uint other = cur != IntPtr.Zero ? GetWindowThreadProcessId(cur, out pid) : 0;
            uint mine = GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (other != 0 && other != mine) attached = AttachThreadInput(other, mine, true);
                ok = SetForegroundWindow(target);
            }
            catch { }
            finally
            {
                if (attached) { try { AttachThreadInput(other, mine, false); } catch { } }
            }
            return ok;
        }

        /// <summary>能否作为归还目标：窗口还在、没被最小化、且不属于图纸进程。</summary>
        private static bool IsUsableTarget(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!IsWindow(h)) return false;
                if (IsIconic(h)) return false;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (IsDraftPid(pid)) return false;
                return true;
            }
            catch { return false; }
        }

        // ================================================================ 进程/窗口识别

        private static bool IsDraftWindow(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return IsDraftPid(pid);
        }

        private static bool IsDraftPid(uint pid)
        {
            if (pid == 0) return false;
            string name = ProcNameCached(pid);
            if (name.Length == 0) return false;
            foreach (string n in DraftProcessNames)
                if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>进程名带 1 秒缓存：新出现的 pid 一定是缓存未命中 → 首次查询就是准确答案，
        /// 所以不会漏掉"刚冷启动的图纸进程"；缓存主要用于避免每 100ms 反复查询同一批窗口。</summary>
        private static string ProcNameCached(uint pid)
        {
            long now = Environment.TickCount;
            KeyValuePair<string, long> hit;
            if (_procCache.TryGetValue(pid, out hit))
                if (unchecked(now - hit.Value) < ProcCacheMs) return hit.Key;

            string name = "";
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) name = p.ProcessName ?? "";
            }
            catch { name = ""; }

            if (_procCache.Count > 512) _procCache.Clear();
            _procCache[pid] = new KeyValuePair<string, long>(name, now);
            return name;
        }

        /// <summary>当前在跑的图纸进程（供"启用"日志留证；冷启动时应为"无"）。</summary>
        private static string DraftSnapshot()
        {
            var sb = new StringBuilder();
            foreach (string n in DraftProcessNames)
            {
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(n))
                    {
                        try
                        {
                            if (sb.Length > 0) sb.Append("，");
                            sb.Append(p.ProcessName).Append("(pid=").Append(p.Id).Append(" 标题='")
                              .Append(p.MainWindowTitle ?? "").Append("')");
                        }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            }
            return sb.Length == 0 ? "无（本次为冷启动）" : sb.ToString();
        }

        private static string Describe(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(null)";
            try
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                string proc = ProcNameCached(pid);
                string title = GetText(h);
                if (title.Length > 48) title = title.Substring(0, 48) + "…";
                return "hwnd=0x" + h.ToInt64().ToString("X") + " [" + (proc.Length == 0 ? "?" : proc)
                     + "]「" + title + "」class=" + GetClassText(h);
            }
            catch { return "hwnd=0x" + h.ToInt64().ToString("X"); }
        }

        private static string GetClassText(IntPtr h)
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

        // ================================================================ Win32

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    }
}