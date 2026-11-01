using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dCXDFTViewUpdate
{
    /// <summary>
    /// 弹窗自动确认：批处理期间后台线程每 250ms 枚举本进程所有顶层窗口，
    /// 找到带「是 / Yes / 确定 / OK」按钮的对话框即模拟点击（BM_CLICK）。
    ///
    /// 仅在批处理运行期间启用（Enabled=true），处理完立即关闭，
    /// 避免误点用户在 CAXA 里的其他正常对话框。
    /// </summary>
    internal static class PopupAutoYes
    {
        private static volatile bool _enabled;
        private static Thread _thread;
        private static readonly HashSet<string> _clickedOnce = new HashSet<string>();

        private static int _clickCount;      // 已代点次数（SendMessage 返回后才 +1 = 弹窗处理完毕）
        private static int _modalCount;      // 当前可见的模态 #32770 弹窗数（白名单进程）

        /// <summary>代点次数。注意：<see cref="SendMessage"/> 是同步调用，返回即代表
        /// 对话框的按钮处理逻辑（含 CAXA 自己的 CX_DFT_ViewUpdate）已经跑完，
        /// 所以这个计数可以直接当作"弹窗已处理完"的权威信号，替代固定 sleep。</summary>
        public static int ClickCount { get { return Volatile.Read(ref _clickCount); } }

        /// <summary>当前可见模态弹窗数。0 = 没有弹窗堵着。</summary>
        public static int ModalCount { get { return Volatile.Read(ref _modalCount); } }

        /// <summary>允许代点的进程：本进程（IronCAD.exe）+ 图纸进程（CAXADraft.exe）。
        /// 项目铁律：CAXA 3D 2027 是双进程，exb 图纸的「是否保存修改」弹窗
        /// 弹在 CAXADraft.exe 里，只扫本进程永远点不到它。</summary>
        private static readonly HashSet<string> AllowedProcesses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CAXADraft", "IronCAD" };

        private static readonly Dictionary<uint, string> _procNameCache = new Dictionary<uint, string>();

        /// <summary>命中的按钮文字（去掉 & 后小写比较）。</summary>
        private static readonly string[] YesTexts =
        {
            "是(&y)", "是(y)", "是", "yes", "&yes",
            "确定", "ok", "&ok",
            "全是(&a)", "全是(y)", "全是", "yes to all"
        };

        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                if (value)
                {
                    _clickedOnce.Clear();
                    EnsureThread();
                }
            }
        }

        private static void EnsureThread()
        {
            if (_thread != null && _thread.IsAlive) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "PopupAutoYes" };
            _thread.Start();
        }

        private static void Loop()
        {
            while (true)
            {
                // 120ms：弹窗被点得越快，批处理单文件耗时越短（每文件都要等一次弹窗）
                Thread.Sleep(120);
                if (!_enabled) continue;
                try { ScanAndClick(); }
                catch (Exception ex) { Diagnostics.Trace("PopupAutoYes: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------ Win32

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint BM_CLICK = 0x00F5;

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

        private static void ScanAndClick()
        {
            uint selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var dialogs = new List<IntPtr>();

            EnumWindows((h, l) =>
            {
                uint wpid;
                GetWindowThreadProcessId(h, out wpid);
                if (wpid != selfPid && !IsWatchedProcess(wpid)) return true;
                if (!IsWindowVisible(h)) return true;
                // 只认标准 Win32 对话框类，避免误碰 CAXA 自绘窗口
                if (GetClass(h) == "#32770") dialogs.Add(h);
                return true;
            }, IntPtr.Zero);

            Volatile.Write(ref _modalCount, dialogs.Count);

            foreach (IntPtr dlg in dialogs)
            {
                string dlgKey = dlg.ToInt64().ToString();
                // 同一对话框只点一次（点是之后对话框应消失；不消失的下轮再说——
                // 所以这里记录的是"本句柄已点过"，句柄复用时允许再点）
                if (_clickedOnce.Contains(dlgKey)) continue;

                IntPtr yesBtn = IntPtr.Zero;
                string hitText = null;
                EnumChildWindows(dlg, (h, l) =>
                {
                    if (GetClass(h) != "Button") return true;
                    string raw = GetText(h);
                    if (raw.Length == 0 || raw.Length > 32) return true;
                    string norm = raw.ToLowerInvariant();
                    foreach (string y in YesTexts)
                    {
                        if (norm == y)
                        {
                            yesBtn = h;
                            hitText = raw;
                            return false;   // 停止枚举
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (yesBtn != IntPtr.Zero)
                {
                    string title = GetText(dlg);
                    uint wpid;
                    GetWindowThreadProcessId(dlg, out wpid);
                    string proc = ProcName(wpid);
                    Diagnostics.Log("PopupAutoYes: 点击「" + hitText + "」@「" + title + "」[" + proc + "]");
                    // 同步发送：返回时 CAXA 已经在按钮处理函数里跑完了视图更新
                    SendMessage(yesBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Interlocked.Increment(ref _clickCount);
                    _clickedOnce.Add(dlgKey);
                }
            }
        }

        /// <summary>对话框所属进程是否在代点白名单里（本进程 / CAXADraft）。</summary>
        private static bool IsWatchedProcess(uint pid)
        {
            string name = ProcName(pid);
            return name.Length > 0 && AllowedProcesses.Contains(name);
        }

        private static string ProcName(uint pid)
        {
            lock (_procNameCache)
            {
                string cached;
                if (_procNameCache.TryGetValue(pid, out cached)) return cached;
            }
            string name = "";
            try { using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) name = p.ProcessName; }
            catch { name = ""; }
            lock (_procNameCache) { _procNameCache[pid] = name; }
            return name;
        }
    }
}
