using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dminitool
{
    /// <summary>
    /// 弹窗自动确认（从 caxa3ddiffrename 移植，2026-11-03）：「零件置换」流程期间，
    /// 后台线程每 120ms 枚举本进程顶层窗口，代点简单确认框与「导入零件/装配」后的文件框
    /// （按 <see cref="FileDialogTarget"/> 填路径再点「打开」），并交给
    /// <see cref="LinkPickDialog"/> 专用填写器处理「选择用于外部链接的零件或装配」「链接的配置」。
    ///
    /// 为什么必须是后台线程：原生模态框自己占住主线程消息泵，主线程轮询永不返回 = 结构性死锁。
    /// 仅在流程期间启用（Enabled=true），收尾再巡检 30 秒，避免误点用户在 CAXA 里的其他正常对话框。
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

        /// <summary>
        /// 专用填写器（如 LinkPickDialog）自己代点了一次确定，计一次数：
        /// ClickCount 是各流程判断「弹窗有没有被处理掉」的依据，漏记会让等待白等。
        /// </summary>
        public static void CountExternalClick()
        {
            Interlocked.Increment(ref _clickCount);
        }

        /// <summary>允许代点的进程：只有本进程（IronCAD.exe）。
        /// 从 caxa3ddiffrename 移植时**故意删掉了 CAXADraft**：那份代码要为 exb 图纸流程点
        /// 图纸进程里的「是否保存修改」，而本插件的「零件置换」全程只动 3D 场景，
        /// 放进图纸进程白名单只会在用户自己开着图纸时误点他的对话框。</summary>
        private static readonly HashSet<string> AllowedProcesses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IronCAD" };

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
        /// 「零件置换」在点「导入零件/装配」前置成**「A」（先选中那个）的外链 ics 全路径**，
        /// 后台线程据此填文件名框并点「打开」。为空时文件框一律点「取消」。
        /// </summary>
        public static volatile string FileDialogTarget;

        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                if (value)
                {
                    _clickedOnce.Clear();
                    _stallReported = false;     // 新一轮流程要能重新报堵塞
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

        /// <summary>
        /// 流程收尾的「迟到业务框」巡检截止时间（Environment.TickCount 刻度，0=未启用）。
        /// 原生「替换零件/装配」的插入例程是在我们判出成功、关掉代点之后才弹
        /// 「选择用于外部链接的零件或装配」「链接的配置」这几张框的 —— 没人代点就一直模态挂着，
        /// 整台 CAXA 连本插件窗口一起点不动（实机 17:11:58 那轮：流程已「完成」，
        /// 之后 2 分 21 秒主窗口与脚本界面都无法操作，日志里只剩主线程定时器的轮询）。
        /// </summary>
        private static int _tailUntil;
        private static int _tailGen;           // 每开一次收尾巡检加 1
        private static int _lastTailGen;       // 只有扫描线程自己动 _clickedOnce，避免与主线程抢

        /// <summary>巡检期只认这几张业务框：它们只可能由本插件驱动的原生替换弹出，
        /// 绝不误点用户此刻自己打开的对话框（包括本插件自己的 MessageBox）。</summary>
        private static readonly string[] TailTitles =
        {
            "选择用于外部链接的零件或装配", "链接的配置", "重新附加约束", "约束元素"
        };

        private static bool IsTailTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            for (int i = 0; i < TailTitles.Length; i++)
                if (title.IndexOf(TailTitles[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>流程结束后再盯 <paramref name="ms"/> 毫秒的迟到业务框，并当场留一份顶层窗口取证。</summary>
        public static void WatchLatePopups(int ms, string why)
        {
            Volatile.Write(ref _tailUntil, Environment.TickCount + ms);
            Volatile.Write(ref _tailGen, Volatile.Read(ref _tailGen) + 1);
            EnsureThread();
            Diagnostics.Trace("PopupAutoYes: 收尾巡检已开 " + (ms / 1000) + " 秒（" + why + "）");
        }

        /// <summary>巡检期是否还活着（TickCount 相减取负，跨过 25 天回绕也算对）。</summary>
        private static bool TailAlive()
        {
            int until = Volatile.Read(ref _tailUntil);
            return until != 0 && (Environment.TickCount - until) < 0;
        }

        private static bool _tailExpiryReported;

        /// <summary>
        /// 巡检到点只报一次，而且**只在真出问题时**报：主框架还被禁用（＝还有模态框堵着）才留证据，
        /// 正常跑完就一声不响。原来这里固定打两行几千字的窗口/输入状态快照，成功时纯噪声。
        /// </summary>
        private static void ReportTailExpiry()
        {
            if (_tailExpiryReported || _lastTailGen == 0) return;
            _tailExpiryReported = true;
            IntPtr frame = UiWin.CadFrame();
            if (frame == IntPtr.Zero || IsWindowEnabled(frame)) return;
            Diagnostics.Log("PopupAutoYes: 收尾巡检到点，CAXA 主框架仍被禁用 —— 可能还有模态框堵着");
            LogTopWindows("收尾巡检到点后的");
        }

        /// <summary>把本进程 + 图纸进程当前可见的顶层窗口写成一行取证日志。
        /// 隐藏的 #32770 也一并列出并标「(隐藏)」：看不见却堵着的模态框正是「整台 CAXA 点不动、
        /// 日志还在跑」这种卡死的样子，只列可见窗口会把真凶漏掉。</summary>
        public static void LogTopWindows(string why)
        {
            uint selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var sb = new StringBuilder();
            EnumWindows((h, l) =>
            {
                uint wpid;
                GetWindowThreadProcessId(h, out wpid);
                if (wpid != selfPid && !IsWatchedProcess(wpid)) return true;
                string cls = GetClass(h);
                bool vis = IsWindowVisible(h);
                if (!vis && cls != "#32770") return true;

                // 取证一律走「带超时的问句」：GetWindowTextW 对本进程其他线程的窗口是跨线程 WM_GETTEXT，
                // 主线程卡死时它会一起卡死 —— 那正是唯一需要这份证据的时刻（19:11 就是这么丢掉取证的）。
                bool answered;
                string title = GetTextSafe(h, out answered);
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(cls).Append('[').Append(Cut(title)).Append(']')
                  .Append(vis ? "" : "(隐藏)")
                  .Append(IsWindowEnabled(h) ? "" : "(禁用)")
                  .Append(answered ? "" : "(无响应)");
                // #32770 只写标题分不清是「完成」还是「失败」提示：把正文第一行也带上，
                // 下次再遇到「整台点不动」能当场看出是谁的框、说什么（18:42 那轮就吃过这个亏）。
                // 标题都没问到的窗口别再往里 EnumChildWindows，那是同一个堵死的线程。
                if (cls == "#32770" && answered)
                {
                    string body = DialogBody(h);
                    if (body.Length > 0) sb.Append('{').Append(body).Append('}');
                }
                return true;
            }, IntPtr.Zero);
            Diagnostics.Log("PopupAutoYes: " + why + "顶层窗口：" +
                (sb.Length == 0 ? "(无)" : sb.ToString()));
        }

        private static string Cut(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }

        /// <summary>对话框正文（第一个 Static 的文字，压成一行、截断）：只用于取证日志。</summary>
        private static string DialogBody(IntPtr dlg)
        {
            string body = "";
            try
            {
                EnumChildWindows(dlg, (h, l) =>
                {
                    if (GetClass(h) != "Static") return true;
                    string t = GetText(h);
                    if (t.Length == 0) return true;
                    body = t.Replace("\r", " ").Replace("\n", " ");
                    return false;
                }, IntPtr.Zero);
            }
            catch { }
            if (body.Length > 60) body = body.Substring(0, 60) + "…";
            return body;
        }

        private static void Loop()
        {
            while (true)
            {
                // 120ms：弹窗被点得越快，代点链单轮耗时越短（实机 caxa3ddiffrename 口径）
                Thread.Sleep(120);
                bool enabled = _enabled;
                if (!enabled && !TailAlive()) { ReportTailExpiry(); continue; }
                _tailExpiryReported = false;
                int gen = Volatile.Read(ref _tailGen);
                if (gen != _lastTailGen)
                {
                    // 新一轮收尾巡检：上一轮「同一窗口只点一次」的账要清掉，
                    // 否则刚被点过又复用了同一句柄的迟到框会被当成已处理而放过。
                    _lastTailGen = gen;
                    _clickedOnce.Clear();
                }
                // 看门狗必须在 ScanAndClick **之前**跑：扫描里的 GetWindowTextW 对本进程其他线程的窗口
                // 是跨线程 WM_GETTEXT，主线程一旦堵死，扫描线程会跟着一起堵在取证上 ——
                // 实机 11-03 19:11 那轮就是主线程卡死而看门狗一行没报（上一轮加的取证通道自己也死了）。
                // 只在流程体还挂着时判心跳（收尾巡检期主线程本来就回到 CAXA 自己的消息环，那时"没心跳"是常态）。
                if (enabled) WatchStall();
                else _stallReported = false;

                try { ScanAndClick(!enabled); }
                catch (Exception ex) { Diagnostics.Trace("PopupAutoYes: " + ex.Message); }
            }
        }

        /// <summary>主线程连续多少秒没进过一次轮询就认定"堵住了"，值得留一份现场取证。</summary>
        private const int StallMs = 8000;
        private static bool _stallReported;

        /// <summary>
        /// 主线程堵塞取证：一次堵塞只报一行窗口快照（扫描线程 120ms 一轮，不然能刷上千行）。
        /// 只取证不动手 —— 点不点那张框仍由 <see cref="ScanAndClick"/> 与
        /// <see cref="LinkPickDialog"/> 的既有规则决定，绝不在这里新增代点通道。
        /// 恢复心跳后重新武装，下一轮堵塞还能再报。
        /// </summary>
        private static void WatchStall()
        {
            int idle = FlowGate.IdleMs();
            if (idle < StallMs) { _stallReported = false; return; }
            if (_stallReported) return;
            _stallReported = true;

            Diagnostics.Log("PopupAutoYes: 主线程已 " + (idle / 1000) +
                " 秒没有心跳（流程还在跑，界面此刻必然点不动）—— 下面是当时的顶层窗口");
            LogTopWindows("主线程堵塞时的");

            IntPtr frame = UiWin.CadFrame();
            if (frame != IntPtr.Zero && !IsWindowEnabled(frame))
                Diagnostics.Log("PopupAutoYes: CAXA 主框架处于禁用状态 ⇒ 有模态框堵着，" +
                    "对照上一行里带「(禁用)」或正文的那几张 #32770 找是哪一张");
            else
                Diagnostics.Log("PopupAutoYes: 主框架没有被禁用 ⇒ 不是模态框堵的，" +
                    "是主线程卡在某个调用里出不来（本轮就是卡在一次设计树 COM 走查上）");
        }

        // ------------------------------------------------------------ Win32

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam,
            StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        private const uint WM_GETTEXT = 0x000D;
        private const uint SMTO_NORMAL = 0x0000;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        /// <summary>问一句标题最多等多久：取证线程绝不能因为主线程堵死而一起堵死。</summary>
        private const uint TextTimeoutMs = 150;

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

        /// <summary>
        /// 取证专用：带超时地问窗口标题。<paramref name="answered"/> 回「问到没有」——false 就是
        /// 窗口所属线程（多半是主线程）在 150ms 内没答这句 WM_GETTEXT。
        /// 同进程跨线程的 <c>GetWindowText</c> 会 SendMessage 并一直等，主线程堵死时取证线程陪着堵；
        /// <c>SMTO_ABORTIFHUNG</c> + 超时让问不到就如实标出来，至少别把证据通道一起拖死（19:11 就是这么丢掉取证的）。
        /// </summary>
        private static string GetTextSafe(IntPtr h, out bool answered)
        {
            var sb = new StringBuilder(256);
            IntPtr result;
            IntPtr res;
            try
            {
                res = SendMessageTimeoutW(h, WM_GETTEXT, (IntPtr)sb.Capacity, sb,
                    SMTO_ABORTIFHUNG | SMTO_NORMAL, TextTimeoutMs, out result);
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("PopupAutoYes: 取标题异常 " + ex.Message);
                answered = false;
                return "";
            }
            answered = res != IntPtr.Zero;      // 返回 0 ＝ 超时或目标线程挂起（GetLastError 1460）
            return answered ? sb.ToString() : "";
        }

        /// <param name="tailOnly">true = 流程收尾巡检：只处理 <see cref="TailTitles"/> 里那几张业务框，
        /// 简单确认框一概不动 —— 这段时间用户可能已经在自己操作 CAXA。</param>
        private static void ScanAndClick(bool tailOnly)
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

                string cls = GetClass(h);
                string title = GetText(h);

                // 只处理真正的对话框窗口（#32770）。绝不能把 CAXA 主窗口也当对话框扫描：
                // 主窗口里同样有 Edit / ComboBox / 「打开」工具栏按钮，一旦被误填路径并点“打开”，
                // 就会触发「选择设计元素库文件(*.icc)」以及随后的「查找外链文件」弹窗。
                if (cls != "#32770") return true;
                if (title.IndexOf(" - [", StringComparison.Ordinal) >= 0) return true;  // 文档主窗口
                // 本插件自己的 MessageBox（标题 = AppTitleBase）绝不能代点，否则确认框被自动点掉
                if (title == MiniToolsForm.AppTitleBase) return true;
                // 收尾巡检期只盯业务框，其余一概不碰
                if (tailOnly && !IsTailTitle(title)) return true;

                _modalCountLocal++;
                dialogs.Add(h);
                return true;
            }, IntPtr.Zero);

            Volatile.Write(ref _modalCount, _modalCountLocal);

            foreach (IntPtr dlg in dialogs)
            {
                string dlgKey = dlg.ToInt64().ToString();

                // 「选择用于外部链接的零件或装配」「链接的配置」：原生替换的插入例程会弹这两张框，
                // 必须先选中「未展开 / 缺省」条目再点确定；空着点确定会把钣金零件降级成装配体（实机 15:53）。
                if (LinkPickDialog.IsMine(GetText(dlg)))
                {
                    LinkPickDialog.Tick(dlg);
                    continue;
                }
                // 同一窗口只点一次（点完后窗口通常消失；句柄复用时允许再点）
                if (_clickedOnce.Contains(dlgKey)) continue;

                IntPtr yesBtn = IntPtr.Zero;
                IntPtr openBtn = IntPtr.Zero;
                IntPtr cancelBtn = IntPtr.Zero;
                IntPtr editCtl = IntPtr.Zero;
                var comboTexts = new List<string>();
                // 弹窗正文（Static 控件文字）。崩前那张「是(&Y)」只记按钮名等于没取证，
                // 必须把问的是什么一并写进日志（实机 12:57:17.063 那条只有按钮名）。
                var staticTexts = new List<string>();
                string hitText = null;
                EnumChildWindows(dlg, (h, l) =>
                {
                    string cls = GetClass(h);
                    string raw = GetText(h);
                    if (cls == "Button")
                    {
                        if (raw.Length == 0 || raw.Length > 32) return true;
                        string norm = raw.ToLowerInvariant();
                        // 「是/确定」只认首个命中；但后续按钮仍要继续扫 —— 既要收正文，
                        // 也要拿到同一张框上的「取消」（改外链名的框就是确定+取消两张按钮）。
                        if (yesBtn == IntPtr.Zero)
                        {
                            foreach (string y in YesTexts)
                            {
                                if (norm == y)
                                {
                                    yesBtn = h;
                                    hitText = raw;
                                    break;
                                }
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
                    else if (raw.Length > 0 && raw.Length <= 300)
                    {
                        staticTexts.Add(cls + "|" + raw);
                    }
                    return true;
                }, IntPtr.Zero);

                string title = GetText(dlg);
                uint wpid;
                GetWindowThreadProcessId(dlg, out wpid);
                string proc = ProcName(wpid);

                if (yesBtn != IntPtr.Zero)
                {
                    Diagnostics.Log("PopupAutoYes: 点击「" + hitText + "」@「" + title + "」[" + proc + "]" + BodySuffix(staticTexts));
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
                        // FileDialogTarget 为空 = 这张「打开」框不是本次替换要的，直接取消，
                        // 免得模态框挂着把整台 CAXA（连本插件窗口一起）堵死。
                        Diagnostics.Log("PopupAutoYes: 取消无关文件框「" + title + "」[" + proc + "]");
                        PostMessage(cancelBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Interlocked.Increment(ref _clickCount);
                        _clickedOnce.Add(dlgKey);
                    }
                }
            }
        }

        /// <summary>
        /// 把弹窗正文（非按钮控件文字）压成一段短后缀拼进代点日志。
        /// 上限 3 条、总长 180 字，避免把控件多的框刷成日志墙。
        /// </summary>
        private static string BodySuffix(List<string> texts)        {
            if (texts == null || texts.Count == 0) return "　正文=（无静态文本，可能自绘）";
            var sb = new StringBuilder("　正文=");
            int taken = 0;
            foreach (string t in texts)
            {
                if (taken == 3) { sb.Append(" …"); break; }
                if (sb.Length > 9) sb.Append(" ｜ ");
                sb.Append(t);
                taken++;
                if (sb.Length >= 180) { sb.Append(" …"); break; }
            }
            return sb.ToString();
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
