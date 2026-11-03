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

        /// <summary>
        /// 专用填写器（如 LinkPickDialog）自己代点了一次确定，计一次数：
        /// ClickCount 是各流程判断「弹窗有没有被处理掉」的依据，漏记会让等待白等。
        /// </summary>
        public static void CountExternalClick()
        {
            Interlocked.Increment(ref _clickCount);
        }

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

        /// <summary>
        /// 改名时 CAXA 自弹的「你是否要修改和保存这个外链文件的文件名?」，置了本标志就改点「取消」。
        /// 为什么（实机 11-02 14:49:58 带约束件「SC08020102.01-52 板」）：「带图独立」第一步已经把
        /// 外链换成 零件B.ics，元素名一改，CAXA 仍去按老路径 零件A.ics 改名保存 ⇒
        /// 「保存文档失败 … 原因: 发生共享冲突」⇒ 整个改名事务回滚（14:50:20 之后的「零部件重新生成」
        /// 里元素名还是旧名）。点「确定」等于让 CAXA 做一件它做不成、还会把改名带倒的事。
        /// </summary>
        public static volatile bool DeclineLinkFileRename;

        /// <summary>CAXA 自己弹过「保存文档失败」的正文；hr=0 不代表存上了，以这条为准。
        /// 每次保存前由 <see cref="ResetSaveFailure"/> 清空。</summary>
        private static string _lastSaveFailure;
        public static string LastSaveFailure
        {
            get { return Volatile.Read(ref _lastSaveFailure); }
            private set { Volatile.Write(ref _lastSaveFailure, value); }
        }

        public static void ResetSaveFailure() { LastSaveFailure = null; }

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
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(cls).Append('[').Append(Cut(GetText(h))).Append(']')
                  .Append(vis ? "" : "(隐藏)")
                  .Append(IsWindowEnabled(h) ? "" : "(禁用)");
                // #32770 只写标题分不清是「完成」还是「失败」提示：把正文第一行也带上，
                // 下次再遇到「整台点不动」能当场看出是谁的框、说什么（18:42 那轮就吃过这个亏）。
                if (cls == "#32770")
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
                // 120ms：弹窗被点得越快，批处理单文件耗时越短（每文件都要等一次弹窗）
                Thread.Sleep(120);
                // 常驻兜底：本插件自己的提示框（标题 = AppTitleBase）若挂着没人点，会把 CAXA
                // 主界面和脚本窗口一起禁用；超过 20 秒就点掉。只在流程期间与收尾期都会跑。
                AutoCloseBox.SweepSelfModal();
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
                try { ScanAndClick(!enabled); }
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

        /// <param name="tailOnly">true = 流程收尾巡检：只处理 <see cref="TailTitles"/> 里那几张业务框，
        /// 简单确认框一概不动 —— 这段时间用户可能已经在自己操作 CAXA。</param>
        private static void ScanAndClick(bool tailOnly)
        {
            uint selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var dialogs = new List<IntPtr>();
            var probeDialogs = new List<IntPtr>();
            _modalCountLocal = 0;

            EnumWindows((h, l) =>
            {
                uint wpid;
                GetWindowThreadProcessId(h, out wpid);
                if (wpid != selfPid && !IsWatchedProcess(wpid)) return true;
                if (!IsWindowVisible(h)) return true;

                string cls = GetClass(h);
                string title = GetText(h);

                // 探针自己驱动的两张框（「改变链接文件」/「浏览文件夹」）：
                // ① 不能只看 cls=="#32770"——MFC 自绘框的窗口类未必是它，漏了状态机就永不推进；
                // ② 一律不进 dialogs、不计入 ModalCount，通用代点绝不可能碰到它的确定/取消。
                if (ChangeLinksProbe.IsMine(title) ||
                    (ChangeLinksProbe.IsArmed && ChangeLinksProbe.IsFolderBrowse(title)))
                {
                    probeDialogs.Add(h);
                    return true;
                }
                // 探针交还用户期间：「打开」「浏览文件夹」只挡不动。
                // 通用代点在 FileDialogTarget 为空时会给文件框点「取消」，
                // 那正好把用户手工改链接要用的框关掉（实机 08:00 的 取消无关文件框「打开」×3）。
                if (ChangeLinksProbe.HandedBack && cls == "#32770" &&
                    (ChangeLinksProbe.IsFolderBrowse(title) || ChangeLinksProbe.IsFileDialog(title)))
                    return true;

                // 只处理真正的对话框窗口（#32770）。绝不能把 CAXA 主窗口也当对话框扫描：
                // 主窗口里同样有 Edit / ComboBox / 「打开」工具栏按钮，一旦被误填路径并点“打开”，
                // 就会触发「选择设计元素库文件(*.icc)」以及随后的「查找外链文件」弹窗，
                // 导致原本正确的“是否更新外链文件名”确认框还要人工点。
                if (cls != "#32770") return true;
                if (title.IndexOf(" - [", StringComparison.Ordinal) >= 0) return true;  // 文档主窗口
                // 本插件自己的 MessageBox（标题 = AppTitleBase）绝不能代点，否则确认框被自动点掉
                if (title == MainForm.AppTitleBase) return true;
                // 收尾巡检期只盯业务框，其余一概不碰
                if (tailOnly && !IsTailTitle(title)) return true;
                // 挂起期（图纸已打开、.ics 流程未完成）：图纸进程的弹窗留到后续阶段统一处理
                if (Suspend && wpid != selfPid) return true;

                _modalCountLocal++;
                dialogs.Add(h);
                return true;
            }, IntPtr.Zero);

            Volatile.Write(ref _modalCount, _modalCountLocal);

            // 先做一次收尾检查：上一轮接管的「设置3D信息」对话框是否已被确认关闭
            Dlg3DInfo.Sweep();

            // 探针的框在主线程被模态泵接管期间只有本线程看得见，状态机必须每轮推进一次。
            for (int i = 0; i < probeDialogs.Count; i++)
            {
                string pTitle = GetText(probeDialogs[i]);
                if (ChangeLinksProbe.IsMine(pTitle)) ChangeLinksProbe.Tick(probeDialogs[i]);
                else if (ChangeLinksProbe.IsFolderBrowse(pTitle))
                    ChangeLinksProbe.TickFolderBrowse(probeDialogs[i]);
            }

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
                // 钣金替换路上的两张配置弹窗：必须先选中「未展开」的条目再点确定，
                // 否则 CAXA 按“整个场景”插入，钣金零件会被换成装配体元素（实机 15:53）。
                if (LinkPickDialog.IsMine(GetText(dlg)))
                {
                    LinkPickDialog.Tick(dlg);
                    continue;
                }
                // 「改变链接文件」「浏览文件夹」两张框已在 EnumWindows 回调里分流到
                // probeDialogs（不看窗口类，也不计入 ModalCount），到这里不可能再出现。
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
                    // 先把「CAXA 自己报的保存失败」记下来：这类框点掉之后 hr 照样是 0，
                    // 只看 HRESULT 会把没存上的文档判成存好了（实机 14:50:02.545 / 14:50:02.804）。
                    if (BodyHas(staticTexts, "保存文档失败"))
                    {
                        LastSaveFailure = JoinBody(staticTexts);
                        Diagnostics.Log("PopupAutoYes: 记下 CAXA 自报保存失败 @「" + title + "」" +
                                        BodySuffix(staticTexts));
                    }

                    // 「是否要修改和保存这个外链文件的文件名?」：改名流程已经把外链换成了新名文件，
                    // CAXA 再去动那个名字必然撞共享冲突并把改名一起回滚 ⇒ 这一张要点「取消」。
                    if (DeclineLinkFileRename && cancelBtn != IntPtr.Zero &&
                        BodyHas(staticTexts, "是否要修改和保存这个外链文件"))
                    {
                        Diagnostics.Log("PopupAutoYes: 取消「修改并保存外链文件名」（" + title +
                                        "）—— 外链文件名已由流程落到新名，让 CAXA 再改一次会撞共享冲突" +
                                        BodySuffix(staticTexts));
                        PostMessage(cancelBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Interlocked.Increment(ref _clickCount);
                        _clickedOnce.Add(dlgKey);
                        continue;
                    }

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
                        // 探针 v6 每行第一步「试探读值」弹的「打开」也从这里取消。
                        // 那句「无关文件框」听起来像出了岔子，其实是设计动作（实机 10:36 的日志就这样被当成 bug）。
                        bool probePeek = ChangeLinksProbe.IsArmed && ChangeLinksProbe.IsFileDialog(title);
                        Diagnostics.Log("PopupAutoYes: " + (probePeek
                            ? "取消试探现值用的「" + title + "」（现值已读，本行不动）"
                            : "取消无关文件框「" + title + "」") + "[" + proc + "]");
                        PostMessage(cancelBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Interlocked.Increment(ref _clickCount);
                        _clickedOnce.Add(dlgKey);
                    }
                }
            }
        }

        /// <summary>
        /// 把弹窗正文（非按钮控件文字）压成一段短后缀拼进代点日志。
        /// 上限 3 条、总长 180 字，避免把「改变链接文件」这类控件多的框刷成日志墙。
        /// </summary>
        /// <summary>弹窗正文里是否出现某个短语（正文条目形如「Static|正文」，逐条比）。</summary>
        private static bool BodyHas(List<string> texts, string marker)
        {
            if (texts == null) return false;
            for (int i = 0; i < texts.Count; i++)
                if (texts[i].IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>把正文拼成一行（存证用，别再折行）。</summary>
        private static string JoinBody(List<string> texts)
        {
            if (texts == null || texts.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (string t in texts)
            {
                if (sb.Length > 0) sb.Append(" ｜ ");
                sb.Append(t.Replace("\r", " ").Replace("\n", " "));
                if (sb.Length > 200) { sb.Append(" …"); break; }
            }
            return sb.ToString();
        }

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
