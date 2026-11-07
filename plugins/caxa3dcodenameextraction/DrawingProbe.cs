// 机械移植自工程 caxa3dexbtracker 的 DrawingProbe.cs（全文件）。仅改 namespace 与项目内资源名，逻辑未动。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace caxa3dcodenameextraction
{
    /// <summary>前台图纸识别结果：Lines 逐行写日志区，Summary 上状态行。</summary>
    internal sealed class DrawingProbeResult
    {
        public readonly List<string> Lines = new List<string>();
        public string Summary = "未识别到前台图纸";
        public bool Found;
    }

    /// <summary>前台环境类型（窗口取证结论）。</summary>
    internal enum ForegroundEnvKind
    {
        /// <summary>判定不出来（没找到 MDIClient / 没有文档子窗口）。</summary>
        Unknown = 0,
        /// <summary>前台是 3D 场景文档窗口 = 设计环境（.ics）。</summary>
        Scene = 1,
        /// <summary>前台是 Draft 图纸文档窗口 = 图纸环境（.exb）。</summary>
        Draft = 2
    }

    /// <summary>
    /// 「识别前台图纸环境」——纯 C# 窗口取证，**不碰 CRX、不改图纸数据库**。
    ///
    /// 为什么走窗口而不是走 API：
    ///   · COM 侧（IRONCAD.Application）只能读到主窗口标题，读不到"哪张图纸在前台"；
    ///   · 图纸文档窗口是 MDI 结构：主框架 → MDIClient → 子窗口（类名实测 AfxFrameOrView140u），
    ///     EnumWindows 只看得到【顶层】窗口，直接枚举永远是空（caxa3dCXDFTViewUpdate 项目踩过）。
    ///   所以顺序固定为：顶层找主框架 → 找 MDIClient（直接子优先，深搜兜底）→ 枚举其直接子窗口。
    ///
    /// 活动图纸判定三级兜底（越靠前越权威）：
    ///   1) WM_MDIGETACTIVE 问 MDIClient（MFC 官方语义，跨进程只读查询，500ms 超时）；
    ///   2) 前台窗口沿 GetParent 上溯，父 == MDIClient 的那一层就是活动文档窗口；
    ///   3) MDI 子窗口里 Z 序最前的可见窗口。
    ///
    /// 「任意前台图纸」= 不预设图纸类型：3D 场景窗口、工程图窗口都在 MDIClient 下，
    /// 这里全部列出并标注类名/标题/可见/最大化，由使用者判断，不做过滤猜测。
    /// </summary>
    internal static class DrawingProbe
    {
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;
        private const int WM_MDIGETACTIVE = 0x0229;
        private const int WM_CLOSE = 0x0010;
        private const int WM_COMMAND = 0x0111;
        private const int IDNO = 7;          // 模态框「否」
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int MaxDocRows = 20;   // MDI 子窗口最多列这么多行，防日志刷屏

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string win);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, int msg, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

        // ================================================================ 对外入口

        public static DrawingProbeResult Describe(CaxaSession session)
        {
            var r = new DrawingProbeResult();
            try { DescribeCore(session, r); }
            catch (Exception ex)
            {
                r.Lines.Add("probe: 探测异常 —— " + ex.Message);
                r.Summary = "识别失败：" + ex.Message;
                r.Found = false;
            }
            return r;
        }

        /// <summary>
        /// 轻量判据：当前进程主框架的 MDIClient 下是否已有图纸文档窗口，或深搜命中疑似图纸窗口。
        /// 用于「图纸环境预热」的成功判定与状态灯，完全基于窗口结构，不依赖已被移除的 CRX 探针。
        /// </summary>
        public static bool HasActiveDrawing()
        {
            return ListDraftDocs().Count > 0;
        }

        /// <summary>
        /// 列出当前所有 **Draft 图纸文档窗口** 的句柄（MDI 子窗口，类名含 AfxFrameOrView）。
        /// 3D 场景窗口不算，见 <see cref="IsDraftWindow"/>。
        /// 用途：预热前后取差集，精确找出"刚被打开的那张载体图纸"，不靠标题匹配。
        /// </summary>
        public static List<IntPtr> ListDraftDocs()
        {
            var list = new List<IntPtr>();
            try
            {
                string t;
                IntPtr frame = FindFrame(Process.GetCurrentProcess().Id, out t);
                if (frame == IntPtr.Zero) return list;
                bool dummy;
                IntPtr mdi = FindMdiClient(frame, out dummy);
                if (mdi == IntPtr.Zero) return list;
                for (IntPtr c = GetWindow(mdi, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT))
                    if (IsDraftWindow(c)) list.Add(c);
            }
            catch { }
            return list;
        }

        /// <summary>
        /// 按**文件名**在已打开的图纸窗口里找目标图（大小写不敏感，只要标题里含该文件名就算）。
        ///
        /// ★ 为什么必须有这个（11-04 19:30 实机）★
        ///   原来判断"exb 打开了没"只看 `ListDraftDocs().Count` 是否增加。可目标图**本来就已经打开**
        ///   时（第二次点按钮、或上一次流程刚把它打开），计数永远不增加 ⇒ 死等满 20 秒超时才继续
        ///   —— 用户感受到的就是"从设计环境跳到图纸环境后有延迟"。
        ///   所以"打开完成"的判据改成**目标文件的窗口存在**：已存在就是 0 延迟，不需要等。
        /// </summary>
        public static IntPtr FindDraftDocByFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return IntPtr.Zero;
            try
            {
                foreach (IntPtr h in ListDraftDocs())
                {
                    string t = WindowText(h);
                    if (string.IsNullOrEmpty(t)) continue;
                    // 标题形态：完整路径，或 "文件名 [路径]"，都可能带尾部 "*"（未保存）
                    if (t.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0) return h;
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        /// <summary>
        /// ★ 前台环境判定（「更新3dbom」两个分支的唯一入口）★
        ///
        /// 为什么不用 COM 的 ActiveDoc 判：
        ///   实机两轮取证（caxa3dminitool / 本工程同口径）：**在图纸环境里
        ///   `IZBaseApp.ActiveDoc` 恒回 3D 场景文档**，拿它判"前台是不是 exb"结构性不成立。
        ///   所以判据只认窗口：MDI 活动子窗口类名含 `AfxFrameOrView` ⇒ Draft（exb）；
        ///   否则是 3D 场景窗口 ⇒ Scene（ics）。
        ///
        /// 活动子窗口三级兜底（与 Describe 同一套，越靠前越权威）：
        ///   WM_MDIGETACTIVE → 前台窗口沿 GetParent 上溯到 MDIClient 直接子 → Z 序最前的可见子窗口。
        /// </summary>
        public static ForegroundEnvKind GetForegroundEnv(out IntPtr docHwnd, out string docTitle)
        {
            docHwnd = IntPtr.Zero;
            docTitle = "";
            try
            {
                string frameTitle;
                IntPtr frame = FindFrame(Process.GetCurrentProcess().Id, out frameTitle);
                if (frame == IntPtr.Zero) return ForegroundEnvKind.Unknown;

                bool mdiDirect;
                IntPtr mdi = FindMdiClient(frame, out mdiDirect);
                if (mdi == IntPtr.Zero) return ForegroundEnvKind.Unknown;

                IntPtr active = QueryActiveByMdi(mdi);
                if (active != IntPtr.Zero) active = ChildOfMdi(active, mdi);   // WM_MDIGETACTIVE 可能回嵌套视图
                if (active == IntPtr.Zero) active = ActiveByForegroundRaw(mdi);
                if (active == IntPtr.Zero)
                {
                    for (IntPtr c = GetWindow(mdi, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT))
                        if (IsWindowVisible(c)) { active = c; break; }
                }
                if (active == IntPtr.Zero) return ForegroundEnvKind.Unknown;

                docHwnd = active;
                docTitle = WindowText(active);
                return IsDraftWindow(active) ? ForegroundEnvKind.Draft : ForegroundEnvKind.Scene;
            }
            catch { return ForegroundEnvKind.Unknown; }
        }

        /// <summary>沿父链上溯，返回 MDIClient 的直接子窗口（找不到返回 0）。</summary>
        private static IntPtr ChildOfMdi(IntPtr h, IntPtr mdi)
        {
            for (int i = 0; i < 16 && h != IntPtr.Zero; i++)
            {
                if (GetParent(h) == mdi) return h;
                h = GetParent(h);
            }
            return IntPtr.Zero;
        }

        /// <summary>前台窗口沿父链上溯，命中 MDIClient 直接子窗口即为活动文档窗口。</summary>
        private static IntPtr ActiveByForegroundRaw(IntPtr mdi)
        {
            IntPtr h = GetForegroundWindow();
            for (int i = 0; i < 16 && h != IntPtr.Zero; i++)
            {
                if (GetParent(h) == mdi) return h;
                h = GetParent(h);
            }
            return IntPtr.Zero;
        }

        /// <summary>对 MDI 子窗口投 WM_CLOSE（异步，不阻塞；是否真关掉要调用方自己轮询 IsAlive）。</summary>
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

        /// <summary>
        /// 若 CAXA 弹了"是否保存"模态框，替用户点「否」（不保存）。
        /// 只在轮询里调用：每次最多投一次 IDNO，同一张框不重复刷日志。
        /// 返回非 null 表示本次真的处理了一张框（调用方可直接记日志）。
        /// </summary>
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

        private static void DescribeCore(CaxaSession session, DrawingProbeResult r)
        {
            r.Lines.Add("probe: ── 识别前台图纸环境 ──");

            // COM 标题只读一次：既做日志，也做「活动图纸」的交叉校验（非 MDI 结构下比 Z 序可靠）
            string comTitle = CaxaAutomation.GetWindowTitle(session);
            string comDoc = comTitle == null ? null : CleanDocTitle(CaxaAutomation.ExtractDocName(comTitle));

            IntPtr hFg = GetForegroundWindow();
            int pidFg;
            GetWindowThreadProcessId(hFg, out pidFg);
            bool fgIsIroncad = IsIroncadPid(pidFg);

            int pid = fgIsIroncad ? pidFg : FindIroncadPid();
            r.Lines.Add(string.Format("probe: 前台窗口 : {0}", DescribeWindow(hFg)));
            r.Lines.Add(string.Format("probe: 前台进程 : {0} pid={1}（{2}）",
                fgIsIroncad ? "IRONCAD" : "(非 IRONCAD)",
                pidFg,
                pidFg == Process.GetCurrentProcess().Id ? "即本进程" : "非本进程"));

            if (pid == 0)
            {
                r.Lines.Add("probe: 未找到 IRONCAD 进程 —— CAXA 3D 没在运行？");
                r.Summary = "未找到 CAXA 3D 进程";
                r.Found = false;
                AppendComSupplement(comTitle, comDoc, r);
                return;
            }

            string frameTitle;
            IntPtr frame = FindFrame(pid, out frameTitle);
            if (frame == IntPtr.Zero)
            {
                r.Lines.Add("probe: 主框架   : 未找到（该进程没有可见带标题的顶层窗口）");
                r.Summary = "未找到 CAXA 3D 主窗口";
                r.Found = false;
                AppendComSupplement(comTitle, comDoc, r);
                return;
            }
            r.Lines.Add(string.Format("probe: 主框架   : hwnd=0x{0:X8} class={1} title=[{2}]",
                frame.ToInt64(), ClassName(frame), frameTitle));

            bool mdiDirect;
            IntPtr mdi = FindMdiClient(frame, out mdiDirect);

            // ---- 收集「图纸文档窗口」：MDI 优先，非 MDI 结构退化为主框架深搜 ----
            var docs = new List<DocWin>();
            string containerDesc;
            bool byMdi = mdi != IntPtr.Zero;

            if (byMdi)
            {
                containerDesc = string.Format("MDIClient: hwnd=0x{0:X8}（{1}）",
                    mdi.ToInt64(), mdiDirect ? "直接子窗口" : "深搜找到");
                for (IntPtr c = GetWindow(mdi, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT))
                    docs.Add(ReadDocWin(c));
                if (docs.Count == 0)
                {
                    // 少数框架把文档窗口放到 MDIClient 的深层子级，递归兜底
                    EnumChildWindows(mdi, delegate (IntPtr h, IntPtr l)
                    {
                        if (docs.Count < 64) docs.Add(ReadDocWin(h));
                        return true;
                    }, IntPtr.Zero);
                }
                r.Lines.Add("probe: " + containerDesc);
            }
            else
            {
                containerDesc = "MDIClient: 未找到 → 退化为「主框架深搜」找疑似图纸窗口";
                r.Lines.Add("probe: " + containerDesc);
                docs = ScanDocWindows(frame);
                r.Lines.Add(string.Format("probe: 深搜结果 : 疑似图纸窗口 {0} 个（判据：标题含 .exb/.dwg/.ics/.icd 或类名含 AfxFrameOrView）",
                    docs.Count));
            }

            if (docs.Count == 0)
            {
                AppendTopLevelCandidates(pid, r);
                AppendComSupplement(comTitle, comDoc, r);
                r.Summary = "未识别到图纸窗口（MDIClient 缺失且深搜无命中，可能未进图纸环境）";
                r.Found = false;
                return;
            }

            // ---- 活动图纸：四级兜底（越靠前越权威）----
            string how;
            IntPtr active = byMdi ? QueryActiveByMdi(mdi) : IntPtr.Zero;
            if (active != IntPtr.Zero) how = "WM_MDIGETACTIVE";
            else
            {
                active = ActiveByForeground(mdi, docs);
                if (active != IntPtr.Zero) how = byMdi ? "前台窗口上溯" : "前台窗口命中候选";
                else
                {
                    // 主窗口标题里就写着当前文档名（CAXA 惯例 "... - [工程图文档4]"），拿它核对候选
                    active = ActiveByComTitle(comDoc, docs);
                    if (active != IntPtr.Zero) how = "主窗口标题匹配";
                    else
                    {
                        active = FirstVisible(docs);
                        how = active != IntPtr.Zero ? "文档窗口 Z 序" : "无";
                    }
                }
            }

            // 区分 Draft 图纸窗口与 3D 场景窗口（两者都是 MDIClient 子窗口，见 IsDraftWindow）
            int draftCount = 0;
            foreach (DocWin d in docs) if (IsDraftWindow(d.H)) draftCount++;

            int shown = 0;
            foreach (DocWin d in docs)
            {
                d.Active = (d.H == active);
                if (shown >= MaxDocRows) continue;
                string flag = d.Active ? "[★]" : (IsDraftWindow(d.H) ? "[ ]" : "[3D]");
                r.Lines.Add("probe:   " + flag + " " + d.Describe());
                shown++;
            }
            if (docs.Count > shown)
                r.Lines.Add(string.Format("probe:   … 另有 {0} 个窗口未列出", docs.Count - shown));

            // 活动文档只认 Draft 图纸窗口：设计环境里活动的是「设计1」，不能拿它当图纸
            DocWin target = null;
            foreach (DocWin d in docs) if (d.H == active && IsDraftWindow(d.H)) { target = d; break; }
            if (target == null) foreach (DocWin d in docs) if (IsDraftWindow(d.H)) { target = d; break; }

            r.Lines.Add(string.Format("probe: 文档窗口 : 共 {0} 个（其中 Draft 图纸 {1} 个，其余为 3D 场景）｜ 活动文档依据 = {2}",
                docs.Count, draftCount, how));

            if (target == null)
            {
                r.Summary = "当前只有 3D 场景窗口（共 " + docs.Count + " 个），还没进图纸环境";
                r.Found = false;
                AppendComSupplement(comTitle, comDoc, r);
                return;
            }
            else
            {
                string name = CleanDocTitle(target.Title);
                r.Lines.Add(string.Format("probe: 图纸名   : [{0}] 类名={1} 可见={2} 最大化={3} 客户区={4}x{5}",
                    name, target.Cls, target.Visible ? 1 : 0, target.Zoomed ? 1 : 0, target.Cw, target.Ch));
                r.Lines.Add(string.Format("probe: 修改标记 : {0}",
                    LooksModified(target.Title) ? "有（标题带 * ，图纸未保存）" : "无（或 CAXA 不回写标题）"));
                if (name.IndexOf("\\", StringComparison.Ordinal) >= 0 || name.IndexOf("/", StringComparison.Ordinal) >= 0)
                    r.Lines.Add("probe: 文档路径 : " + name);
                else
                    r.Lines.Add("probe: 文档路径 : （标题只给文件名，CAXA 未在标题里回显路径）");

                r.Summary = "前台图纸: " + name + (draftCount > 1 ? "（共 " + draftCount + " 张图纸打开）" : "");
                r.Found = true;
            }

            AppendComSupplement(comTitle, comDoc, r);
        }

        // ================================================================ 窗口只读信息

        private sealed class DocWin
        {
            public IntPtr H;
            public string Cls = "";
            public string Title = "";
            public bool Visible, Zoomed, Active;
            public int Cw, Ch;

            public string Describe()
            {
                return string.Format("hwnd=0x{0:X8} class={1} title=[{2}] vis={3} zoom={4} client={5}x{6}",
                    H.ToInt64(), Cls, Title, Visible ? 1 : 0, Zoomed ? 1 : 0, Cw, Ch);
            }
        }

        private static DocWin ReadDocWin(IntPtr h)
        {
            var d = new DocWin { H = h, Cls = ClassName(h), Title = WindowText(h) };
            d.Visible = IsWindowVisible(h);
            try { d.Zoomed = IsZoomed(h); } catch { }
            RECT rc;
            if (GetClientRect(h, out rc)) { d.Cw = rc.Right - rc.Left; d.Ch = rc.Bottom - rc.Top; }
            return d;
        }

        private static string DescribeWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(无前台窗口)";
            return string.Format("hwnd=0x{0:X8} class={1} title=[{2}] vis={3}",
                h.ToInt64(), ClassName(h), WindowText(h), IsWindowVisible(h) ? 1 : 0);
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

        // ================================================================ 进程 / 主框架 / MDIClient

        private static bool IsIroncadPid(int pid)
        {
            if (pid == 0) return false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    return p.ProcessName.Equals("IRONCAD", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>找带可见主窗口的 IRONCAD 进程（插件在 CAXA 内运行时命中本进程）。</summary>
        private static int FindIroncadPid()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("IRONCAD"))
                {
                    int id = p.Id;
                    p.Dispose();
                    if (id == Process.GetCurrentProcess().Id) return id;   // 本进程优先（进程内插件）
                }
                foreach (Process p in Process.GetProcessesByName("IRONCAD"))
                {
                    int id = p.Id;
                    p.Dispose();
                    return id;
                }
            }
            catch (Exception ex) { Diagnostics.Log("probe: 枚举 IRONCAD 进程失败: " + ex.Message); }
            return 0;
        }

        /// <summary>
        /// 主框架：优先「含 MDIClient 的可见顶层窗口」，否则取面积最大的可见带标题窗口
        /// （面积判据可避开本插件自己的 WinForms 小窗、阴影窗）。
        /// </summary>
        private static IntPtr FindFrame(int pid, out string title)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            string bestTitle = null;
            bool dummy;

            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                int wp;
                GetWindowThreadProcessId(h, out wp);
                if (wp != pid) return true;
                if (!IsWindowVisible(h)) return true;
                string t = WindowText(h);
                if (t.Length == 0) return true;

                if (FindMdiClient(h, out dummy) != IntPtr.Zero)
                {
                    best = h; bestTitle = t;
                    return false;                       // 找到主框架，停止枚举
                }

                RECT rc;
                if (GetClientRect(h, out rc))
                {
                    long area = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
                    if (area > bestArea) { bestArea = area; best = h; bestTitle = t; }
                }
                return true;
            }, IntPtr.Zero);

            title = bestTitle ?? "";
            return best;
        }

        private static IntPtr FindMdiClient(IntPtr top, out bool direct)
        {
            direct = true;
            IntPtr mdi = FindWindowExW(top, IntPtr.Zero, "MDIClient", null);
            if (mdi != IntPtr.Zero) return mdi;

            direct = false;
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(top, delegate (IntPtr h, IntPtr l)
            {
                if (string.Equals(ClassName(h), "MDIClient", StringComparison.Ordinal)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // ================================================================ 活动图纸判定

        private static IntPtr QueryActiveByMdi(IntPtr mdi)
        {
            IntPtr res;
            // WM_MDIGETACTIVE 只读查询；跨进程给 500ms 上限，宿主忙时直接放弃走下一级兜底
            IntPtr ok = SendMessageTimeoutW(mdi, WM_MDIGETACTIVE, IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, 500, out res);
            if (ok == IntPtr.Zero) return IntPtr.Zero;
            return res;
        }

        private static IntPtr ActiveByForeground(IntPtr mdi, List<DocWin> docs)
        {
            IntPtr h = GetForegroundWindow();
            for (int i = 0; i < 16 && h != IntPtr.Zero; i++)
            {
                if (mdi != IntPtr.Zero && GetParent(h) == mdi) return h;
                foreach (DocWin d in docs) if (d.H == h) return h;   // 非 MDI：祖先链命中候选即活动
                h = GetParent(h);
            }
            return IntPtr.Zero;
        }

        /// <summary>用主窗口标题里的文档名核对候选窗口（CAXA 标题惯例 "... - [工程图文档4]"）。</summary>
        private static IntPtr ActiveByComTitle(string comDoc, List<DocWin> docs)
        {
            if (string.IsNullOrEmpty(comDoc) || comDoc.Length < 2) return IntPtr.Zero;
            foreach (DocWin d in docs)
            {
                if (d.Title.Length == 0) continue;
                if (d.Title.IndexOf(comDoc, StringComparison.OrdinalIgnoreCase) >= 0) return d.H;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 无 MDIClient 时的兜底：深搜主框架后代，挑「像图纸文档」的窗口。
        /// 判据故意放宽（标题扩展名 或 MFC 文档框架类名），因为 CAXA 各版本/工程图与 3D 场景
        /// 的窗口类名不统一 —— 宁可多列几个候选，也不要在冷态误报"什么都没有"。
        /// 父子同标题只保留最外层（深搜会把文档窗口内部视图也带出来）。
        /// </summary>
        private static List<DocWin> ScanDocWindows(IntPtr frame)
        {
            var hs = new List<IntPtr>();
            EnumChildWindows(frame, delegate (IntPtr h, IntPtr l)
            {
                if (hs.Count < 256 && LooksLikeDocWindow(h)) hs.Add(h);
                return true;
            }, IntPtr.Zero);

            var set = new HashSet<IntPtr>(hs);
            var result = new List<DocWin>();
            foreach (IntPtr h in hs)
                if (!set.Contains(GetParent(h))) result.Add(ReadDocWin(h));
            return result;
        }

        /// <summary>
        /// 是不是 Draft 的图纸文档窗口。实测（v2027）：
        ///   图纸环境文档 → 类名 <c>AfxFrameOrView140u</c>（MFC 的 CFrameOrView）；
        ///   3D 场景文档   → 类名 <c>Afx:00007FF7...:b:...</c>（BCG 托管的 3D 视图宿主），标题「设计1」。
        /// 两者都是 MDIClient 的子窗口，只能按类名区分 —— 别再拿「有没有子窗口」当判据。
        /// </summary>
        private static bool IsDraftWindow(IntPtr h)
        {
            return ClassName(h).IndexOf("AfxFrameOrView", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeDocWindow(IntPtr h)
        {
            string t = WindowText(h);
            if (t.Length == 0) return false;
            string lower = t.ToLowerInvariant();
            if (lower.Contains(".exb") || lower.Contains(".dwg") ||
                lower.Contains(".ics") || lower.Contains(".icd")) return true;
            return ClassName(h).IndexOf("AfxFrameOrView", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IntPtr FirstVisible(List<DocWin> docs)
        {
            foreach (DocWin d in docs) if (d.Visible) return d.H;
            return docs.Count > 0 ? docs[0].H : IntPtr.Zero;
        }

        // ================================================================ 标题解析

        /// <summary>标题 → 图纸名：去掉 MDI 标题的 * 修改标记与首尾空白。</summary>
        private static string CleanDocTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "(无标题)";
            string s = title.Trim();
            if (s.StartsWith("*", StringComparison.Ordinal)) s = s.Substring(1).Trim();
            else if (s.StartsWith("▲", StringComparison.Ordinal)) s = s.Substring(1).Trim();
            return s.Length == 0 ? "(无标题)" : s;
        }

        private static bool LooksModified(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            string s = title.Trim();
            return s.StartsWith("*", StringComparison.Ordinal) || s.StartsWith("▲", StringComparison.Ordinal);
        }

        // ================================================================ 兜底诊断

        /// <summary>没有 MDIClient 时列出本进程顶层窗口，便于判断"图纸环境到底起没起"。</summary>
        private static void AppendTopLevelCandidates(int pid, DrawingProbeResult r)
        {
            var lines = new List<string>();
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                int wp;
                GetWindowThreadProcessId(h, out wp);
                if (wp != pid) return true;
                string t = WindowText(h);
                bool vis = IsWindowVisible(h);
                if (!vis && t.Length == 0) return true;
                if (lines.Count < 10)
                    lines.Add(string.Format("probe:   · hwnd=0x{0:X8} class={1} vis={2} title=[{3}]",
                        h.ToInt64(), ClassName(h), vis ? 1 : 0, t));
                return true;
            }, IntPtr.Zero);

            if (lines.Count == 0) r.Lines.Add("probe:   （该进程无任何可见/带标题顶层窗口）");
            else
            {
                r.Lines.Add("probe: 顶层窗口候选（前 10 个）:");
                r.Lines.AddRange(lines);
            }
        }

        // ================================================================ COM 侧补充信息

        /// <summary>
        /// COM 交叉校验：只读主窗口标题里的文档名，与窗口取证结果并排打印，方便互证。
        /// 不依赖任何桥接模块（CRX 方案已在 v2027 否决），取不到就明说"窗口探测不依赖它"。
        /// </summary>
        private static void AppendComSupplement(string comTitle, string comDoc, DrawingProbeResult r)
        {
            if (comTitle == null)
                r.Lines.Add("probe: COM 会话 : 未建立 / 标题读取失败（窗口探测不依赖它）");
            else
                r.Lines.Add(string.Format("probe: COM 会话 : title=[{0}] 文档=[{1}]",
                    comTitle, string.IsNullOrEmpty(comDoc) ? "(无)" : comDoc));
        }
    }
}
