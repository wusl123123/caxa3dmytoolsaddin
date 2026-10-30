using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dcodenameextraction
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

        private static int _clickCount;      // 已代点次数
        private static int _modalCount;      // 当前可见的模态 #32770 弹窗数（白名单进程）

        /// <summary>代点次数。点击用 PostMessage 投递（不阻塞本线程），
        /// 所以计数只代表"已发出点击"，弹窗是否处理完要靠 CAXA 的日志/窗口消失另行判断。</summary>
        public static int ClickCount { get { return Volatile.Read(ref _clickCount); } }

        /// <summary>当前可见模态弹窗数。0 = 没有弹窗堵着。</summary>
        public static int ModalCount { get { return Volatile.Read(ref _modalCount); } }

        /// <summary>允许代点的进程：本进程（IronCAD.exe）+ 图纸进程（CAXADraft.exe）。
        /// 项目铁律：CAXA 3D 2027 是双进程，exb 图纸的「是否保存修改」弹窗
        /// 弹在 CAXADraft.exe 里，只扫本进程永远点不到它。</summary>
        private static readonly HashSet<string> AllowedProcesses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CAXADraft", "IronCAD" };

        private static readonly Dictionary<uint, string> _procNameCache = new Dictionary<uint, string>();

        /// <summary>命中的按钮文字（去掉 &amp; 后小写比较）。</summary>
        private static readonly string[] YesTexts =
        {
            "是(&y)", "是(y)", "是", "yes", "&yes",
            "确定", "ok", "&ok",
            "全是(&a)", "全是(y)", "全是", "yes to all"
        };

        /// <summary>文件选择对话框的确认按钮文字。</summary>
        private static readonly string[] OpenTexts =
        {
            "打开(&o)", "打开(o)", "打开", "open", "&open"
        };

        /// <summary>取消/关闭类按钮文字（用于关掉无关的元素库浏览框）。</summary>
        private static readonly string[] CancelTexts =
        {
            "取消(&c)", "取消(c)", "取消", "cancel", "&cancel", "关闭(&c)", "关闭(c)", "关闭", "close"
        };

        /// <summary>
        /// 文件选择对话框的目标路径（非空时启用「自动填入并打开」）：
        /// CAXA 打开 exb 时若需要指向改名后的 ics，会自动填这个路径并点打开。
        /// </summary>
        public static volatile string FileDialogTarget;

        /// <summary>挂起图纸进程（CAXADraft）的代点：本进程弹窗照常点，只暂停对图纸进程的处理。</summary>
        public static volatile bool Suspend;

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
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

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
            _modalCountLocal = 0;

            EnumWindows((h, l) =>
            {
                uint wpid;
                GetWindowThreadProcessId(h, out wpid);
                if (wpid != selfPid && !IsWatchedProcess(wpid)) return true;
                if (!IsWindowVisible(h)) return true;

                // 只处理真正的对话框窗口（#32770）。绝不能把 CAXA 主窗口也当对话框扫描：
                // 主窗口里同样有 Edit / ComboBox / 「打开」工具栏按钮，一旦被误填路径并点“打开”，
                // 就会触发「选择设计元素库文件(*.icc)」以及随后的「查找外链文件」弹窗，
                // 导致原本正确的“是否更新外链文件名”确认框还要人工点。
                string cls = GetClass(h);
                if (cls != "#32770") return true;
                string title = GetText(h);
                if (title.IndexOf(" - [", StringComparison.Ordinal) >= 0) return true;  // 文档主窗口
                // 本插件自己的 MessageBox（标题 = AppTitleBase）绝不能代点，否则确认框被自动点掉
                if (title == MainForm.AppTitleBase) return true;
                // 挂起期（图纸已打开、.ics 流程未完成）：图纸进程的弹窗留到后续阶段统一处理
                if (Suspend && wpid != selfPid) return true;

                _modalCountLocal++;
                dialogs.Add(h);
                return true;
            }, IntPtr.Zero);

            Volatile.Write(ref _modalCount, _modalCountLocal);

            // 先做一次收尾检查：上一轮接管的「设置3D信息」对话框是否已被确认关闭
            Dlg3DInfo.Sweep();

            foreach (IntPtr dlg in dialogs)
            {
                string dlgKey = dlg.ToInt64().ToString();

                // 「设置3D信息」是标题栏更新流程必须填完的业务对话框，不是要“代点掉”的确认框，
                // 交给专用填写器（选缺省配置 + 勾默认 + 确定），不参与下面的通用代点逻辑。
                if (Dlg3DInfo.IsMine(GetText(dlg)))
                {
                    Dlg3DInfo.Tick(dlg);
                    continue;
                }
                // 同一窗口只点一次（点完后窗口通常消失；句柄复用时允许再点）
                if (_clickedOnce.Contains(dlgKey)) continue;

                IntPtr yesBtn = IntPtr.Zero;
                IntPtr openBtn = IntPtr.Zero;
                IntPtr cancelBtn = IntPtr.Zero;
                IntPtr editCtl = IntPtr.Zero;
                var comboTexts = new List<string>();
                string hitText = null;
                EnumChildWindows(dlg, (h, l) =>
                {
                    string cls = GetClass(h);
                    string raw = GetText(h);
                    if (cls == "Button")
                    {
                        if (raw.Length == 0 || raw.Length > 32) return true;
                        string norm = raw.ToLowerInvariant();
                        foreach (string y in YesTexts)
                        {
                            if (norm == y)
                            {
                                yesBtn = h;
                                hitText = raw;
                                return false;   // 停止枚举（确定/是 优先级最高）
                            }
                        }
                        if (openBtn == IntPtr.Zero)
                        {
                            foreach (string o in OpenTexts)
                            {
                                if (norm == o) { openBtn = h; break; }
                            }
                        }
                        if (cancelBtn == IntPtr.Zero)
                        {
                            foreach (string c in CancelTexts)
                            {
                                if (norm == c) { cancelBtn = h; break; }
                            }
                        }
                    }
                    else if (cls == "Edit" && editCtl == IntPtr.Zero)
                    {
                        editCtl = h;   // 文件名输入框（可能为空，仍要拿到句柄用于填入）
                    }
                    else if (cls == "ComboBox" && raw.Length > 0 && raw.Length <= 64)
                    {
                        comboTexts.Add(raw);   // 文件类型下拉（用于识别“设计元素库/icc”对话框）
                    }
                    return true;
                }, IntPtr.Zero);

                string title = GetText(dlg);
                uint wpid;
                GetWindowThreadProcessId(dlg, out wpid);
                string proc = ProcName(wpid);

                if (yesBtn != IntPtr.Zero)
                {
                    Diagnostics.Log("PopupAutoYes: 点击「" + hitText + "」@「" + title + "」[" + proc + "]");
                    // 用 PostMessage：点「是」后 CAXA 可能紧接着弹出“选择更新后文件名”对话框，
                    // 同步 SendMessage 会卡在该模态循环里，导致本线程无法再代点下一个弹窗。
                    PostMessage(yesBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Interlocked.Increment(ref _clickCount);
                    _clickedOnce.Add(dlgKey);
                }
                else if (openBtn != IntPtr.Zero)
                {
                    // 判定是否“设计元素库(icc)”浏览框：CAXA 改名后解析外链时会弹它，
                    // 它与“是否修改保存外链文件名”确认框同时出现，属于无关窗口，
                    // 实测直接「取消」即可让流程继续（绝不能往 icc 筛选框里填 ics 路径）。
                    bool isElementLib = false;
                    foreach (string ct in comboTexts)
                    {
                        if (ct.IndexOf("icc", StringComparison.OrdinalIgnoreCase) >= 0)
                        { isElementLib = true; break; }
                    }

                    if (isElementLib)
                    {
                        if (cancelBtn != IntPtr.Zero)
                        {
                            Diagnostics.Log("PopupAutoYes: 取消元素库浏览框「" + title + "」[" + proc + "]");
                            PostMessage(cancelBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                            Interlocked.Increment(ref _clickCount);
                            _clickedOnce.Add(dlgKey);
                        }
                    }
                    else if (FileDialogTarget != null && editCtl != IntPtr.Zero && comboTexts.Count > 0)
                    {
                        // 真正的文件选择框才允许填路径：必须同时有文件名 Edit 和“文件类型”下拉
                        string target = FileDialogTarget;
                        Diagnostics.Log("PopupAutoYes: 文件对话框指定「" + target + "」@「" + title + "」");
                        SetWindowTextW(editCtl, target);
                        PostMessage(openBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Interlocked.Increment(ref _clickCount);
                        _clickedOnce.Add(dlgKey);
                    }
                    else if (cancelBtn != IntPtr.Zero)
                    {
                        Diagnostics.Log("PopupAutoYes: 取消无关文件框「" + title + "」[" + proc + "]");
                        PostMessage(cancelBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Interlocked.Increment(ref _clickCount);
                        _clickedOnce.Add(dlgKey);
                    }
                }
            }
        }

        private static int _modalCountLocal;


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
