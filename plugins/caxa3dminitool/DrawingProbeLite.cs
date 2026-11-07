using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace caxa3dminitool
{
    /// <summary>前台图纸识别结果：Lines 逐行进日志区，Summary 上状态行。</summary>
    internal sealed class DrawingProbeResult
    {
        public readonly List<string> Lines = new List<string>();
        public string Summary = "未识别到前台图纸";
        public bool Found;
        /// <summary>活动图纸窗口标题（已去掉 * 修改标记）。</summary>
        public string DocName = "";
        public IntPtr DocHwnd = IntPtr.Zero;
        public int DraftCount;
    }

    /// <summary>
    /// 「识别前台图纸环境」——**纯窗口取证**，不碰 CRX、不改图纸数据库、不依赖 COM。
    ///
    /// 为什么走窗口而不是走 API：
    ///   · COM 侧 `get_ActiveDoc` 在图纸环境里恒回**3D 场景文档**（实机两轮取证），
    ///     拿它判"当前是不是图纸环境"结构性不可能成立；
    ///   · 图纸文档窗口是 MDI 结构：主框架 → MDIClient → 子窗口，EnumWindows 只看得到顶层，
    ///     直接枚举永远是空。
    ///   顺序固定：顶层找主框架 → 找 MDIClient（直接子优先，深搜兜底）→ 枚举其子窗口。
    ///
    /// ★ 认 Draft 只看**类名**：图纸文档 = `AfxFrameOrView140u`；
    ///   3D 场景 = `Afx:0000...:b:...`（标题「设计1」）。两者都是 MDIClient 的子窗口，
    ///   所以"有子窗口"≠"有图纸"——把前者当判据会得到恒真的假结论。
    ///
    /// 活动图纸判定三级兜底（越靠前越权威）：
    ///   1) WM_MDIGETACTIVE 问 MDIClient（MFC 官方语义，只读查询，500 ms 超时）；
    ///   2) 前台窗口沿 GetParent 上溯，父 == MDIClient 的那一层；
    ///   3) 子窗口里 Z 序最前的可见窗口。
    /// </summary>
    internal static class DrawingProbeLite
    {
        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;
        private const int WM_MDIGETACTIVE = 0x0229;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int MaxDocRows = 12;

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

        // ================================================================ 对外入口

        public static DrawingProbeResult Describe()
        {
            var r = new DrawingProbeResult();
            try { DescribeCore(r); }
            catch (Exception ex)
            {
                r.Lines.Add("probe: 探测异常 —— " + ex.Message);
                r.Summary = "识别失败：" + ex.Message;
                r.Found = false;
            }
            return r;
        }

        /// <summary>轻量判据：当前是否已有 Draft 图纸窗口（仅窗口结构，不依赖 CRX/COM）。</summary>
        public static bool HasDraft()
        {
            return ListDraftDocs().Count > 0;
        }

        /// <summary>列出所有 Draft 图纸文档窗口（MDI 子窗口，类名含 AfxFrameOrView）。</summary>
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

        private static void DescribeCore(DrawingProbeResult r)
        {
            r.Lines.Add("probe: ── 识别前台图纸环境 ──");

            int pid = Process.GetCurrentProcess().Id;
            IntPtr hFg = GetForegroundWindow();
            int pidFg;
            GetWindowThreadProcessId(hFg, out pidFg);
            r.Lines.Add(string.Format("probe: 前台窗口 : {0}", DescribeWindow(hFg)));
            r.Lines.Add(string.Format("probe: 前台进程 : pid={0}（{1}）｜本进程 pid={2}",
                pidFg, pidFg == pid ? "即本进程" : "非本进程", pid));

            string frameTitle;
            IntPtr frame = FindFrame(pid, out frameTitle);
            if (frame == IntPtr.Zero)
            {
                r.Lines.Add("probe: 主框架   : 未找到（本进程没有可见带标题的顶层窗口）");
                r.Summary = "未找到 CAXA 3D 主窗口";
                r.Found = false;
                return;
            }
            r.Lines.Add(string.Format("probe: 主框架   : hwnd=0x{0:X8} class={1} title=[{2}]",
                frame.ToInt64(), ClassName(frame), frameTitle));

            bool mdiDirect;
            IntPtr mdi = FindMdiClient(frame, out mdiDirect);
            if (mdi == IntPtr.Zero)
            {
                r.Lines.Add("probe: MDIClient：未找到 → 无法枚举文档窗口（可能还没进图纸环境）");
                AppendTopLevelCandidates(pid, r);
                r.Summary = "未识别到图纸窗口（MDIClient 缺失）";
                r.Found = false;
                return;
            }
            r.Lines.Add(string.Format("probe: MDIClient: hwnd=0x{0:X8}（{1}）",
                mdi.ToInt64(), mdiDirect ? "直接子窗口" : "深搜找到"));

            var docs = new List<DocWin>();
            for (IntPtr c = GetWindow(mdi, GW_CHILD); c != IntPtr.Zero; c = GetWindow(c, GW_HWNDNEXT))
                docs.Add(ReadDocWin(c));

            int draftCount = 0;
            foreach (DocWin d in docs) if (IsDraftWindow(d.H)) draftCount++;

            IntPtr active = QueryActiveByMdi(mdi);
            string how = "WM_MDIGETACTIVE";
            if (active == IntPtr.Zero) { active = ActiveByForeground(mdi, docs); how = how + "→前台窗口上溯"; }
            if (active == IntPtr.Zero) { active = FirstVisible(docs); how = how + "→Z 序"; }

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

            r.Lines.Add(string.Format(
                "probe: 文档窗口 : 共 {0} 个（其中 Draft 图纸 {1} 个，其余为 3D 场景）｜ 活动文档依据 = {2}",
                docs.Count, draftCount, how));

            // 活动文档只认 Draft 窗口：设计环境里活动的是「设计1」，不能拿它当图纸
            DocWin target = null;
            foreach (DocWin d in docs) if (d.H == active && IsDraftWindow(d.H)) { target = d; break; }
            if (target == null) foreach (DocWin d in docs) if (IsDraftWindow(d.H)) { target = d; break; }

            r.DraftCount = draftCount;
            if (target == null)
            {
                r.Summary = "当前只有 3D 场景窗口（共 " + docs.Count + " 个），还没进图纸环境";
                r.Found = false;
                return;
            }

            string name = CleanDocTitle(target.Title);
            r.DocName = name;
            r.DocHwnd = target.H;
            r.Lines.Add(string.Format("probe: 图纸名   : [{0}] 类名={1} 可见={2} 最大化={3} 客户区={4}x{5}",
                name, target.Cls, target.Visible ? 1 : 0, target.Zoomed ? 1 : 0, target.Cw, target.Ch));
            r.Lines.Add("probe: 修改标记 : " +
                (LooksModified(target.Title) ? "有（标题带 *，图纸未保存）" : "无（或 CAXA 不回写标题）"));
            if (name.IndexOf("\\", StringComparison.Ordinal) >= 0 || name.IndexOf("/", StringComparison.Ordinal) >= 0)
                r.Lines.Add("probe: 文档路径 : " + name);
            else
                r.Lines.Add("probe: 文档路径 : （标题只给文件名，CAXA 未在标题里回显路径）");

            r.Summary = "前台图纸: " + name + (draftCount > 1 ? "（共 " + draftCount + " 张图纸打开）" : "");
            r.Found = true;
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
            var sb = new StringBuilder(160);
            try { GetClassNameW(h, sb, sb.Capacity); } catch { }
            return sb.ToString();
        }

        private static string WindowText(IntPtr h)
        {
            var sb = new StringBuilder(512);
            try { GetWindowTextW(h, sb, sb.Capacity); } catch { }
            return sb.ToString();
        }

        // ================================================================ 主框架 / MDIClient

        /// <summary>
        /// 主框架：优先「含 MDIClient 的可见顶层窗口」，否则取面积最大的可见带标题窗口
        /// （面积判据避开本插件自己的 WinForms 小窗与阴影窗）。
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

        // ================================================================ 活动图纸

        private static IntPtr QueryActiveByMdi(IntPtr mdi)
        {
            IntPtr res;
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
                foreach (DocWin d in docs) if (d.H == h) return h;
                h = GetParent(h);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 是不是 Draft 的图纸文档窗口。实测（v2027）：
        ///   图纸文档 → 类名 AfxFrameOrView140u；3D 场景 → Afx:00007FF7...:b:...（标题「设计1」）。
        /// </summary>
        private static bool IsDraftWindow(IntPtr h)
        {
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
    }
}
