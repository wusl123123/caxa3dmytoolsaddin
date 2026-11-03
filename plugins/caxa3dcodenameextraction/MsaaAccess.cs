// MSAA（oleacc IAccessible）通道：读写 CAXA 的 BCGControlBar 自绘界面。
//
// 为什么需要它：实机取证（caxa3dcodenameextraction_uiprobe.txt，2026-11-01 12:4x）证明
// 「替换零件/装配」面板不是 #32770 对话框，「导入零件/装配」「重新附加约束」「约束元素」
// 只存在于 3iAdvFeatureLg.dll / IsEngineAfxLg.dll 的 RT_STRING 字符串表里，面板项是
// BCG 自绘条目 —— 没有子窗口、没有窗口文字、TB_* 取按钮数返回 0。
// 所以按 HWND 文字找控件这条路结构上走不通，必须换可访问性接口。
//
// 已实机验证（外部探针 _probe-tools\msaa_probe.exe，2026-11-01 13:05）：
//   CAXA 主框架 OBJID_CLIENT 可取到 IAccessible，childCount=67，BCG 条目有名字、
//   有屏幕矩形、默认动作是「Press」→ 既能命中文字，也能 accDoDefaultAction 代点。
//
// 两个必须照抄的坑（错了就全 E_NOINTERFACE / DISP_E_UNKNOWNNAME）：
//   1) IID_IAccessible = {618736E0-3C3D-11CF-810C-00AA00389B71}，不是 …36F0。
//   2) IAccessible 继承 IDispatch，但这里声明 InterfaceIsIUnknown 并用 4 个占位方法
//      顶掉 IDispatch 的槽 3~6，自定义方法才落在正确槽位；用 InterfaceIsIDispatch
//      会让 CLR 按名字走 IDispatch::Invoke → DISP_E_UNKNOWNNAME。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace caxa3dcodenameextraction
{
    /// <summary>一个可访问性条目：足够用来「认出并点它」。</summary>
    [ComVisible(false)]
    internal sealed class MsaaNode
    {
        public IAccessible Acc;         // 条目所属的 IAccessible
        public object ChildId;             // CHILDID_SELF 或子 id（int）
        public string Name = "";
        public string Action = "";
        public string State = "";
        public int Role = -1;              // ROLE_SYSTEM_*（-1 = 问不到）
        public int Left, Top, Width, Height;
    }

    /// <summary>
    /// IAccessible（继承 IDispatch）。这里仍声明 InterfaceIsIUnknown，但把 IDispatch 的 4 个方法
    /// 用占位顶掉，让自定义方法落在正确的 vtable 槽（3~6 属 IDispatch，get_accParent 才是槽 7）。
    /// 不能用 InterfaceIsIDispatch：那样 CLR 会按名字走 IDispatch::Invoke → DISP_E_UNKNOWNNAME。
    /// </summary>
    [ComImport]
    [ComVisible(false)]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAccessible
    {
        [PreserveSig] int _GetTypeInfoCount(out uint count);
        [PreserveSig] int _GetTypeInfo();
        [PreserveSig] int _GetIDsOfNames();
        [PreserveSig] int _Invoke();

        [PreserveSig] int get_accParent(out IAccessible parent);
        [PreserveSig] int get_accChildCount(out int count);
        [PreserveSig] int get_accChild([In, MarshalAs(UnmanagedType.Struct)] object child,
            [Out, MarshalAs(UnmanagedType.IDispatch)] out object accChild);
        [PreserveSig] int get_accName([In, MarshalAs(UnmanagedType.Struct)] object child,
            [MarshalAs(UnmanagedType.BStr)] out string name);
        [PreserveSig] int get_accValue([In, MarshalAs(UnmanagedType.Struct)] object child,
            [MarshalAs(UnmanagedType.BStr)] out string value);
        [PreserveSig] int get_accDescription([In, MarshalAs(UnmanagedType.Struct)] object child,
            [MarshalAs(UnmanagedType.BStr)] out string description);
        [PreserveSig] int get_accRole([In, MarshalAs(UnmanagedType.Struct)] object child,
            [Out, MarshalAs(UnmanagedType.Struct)] out object role);
        [PreserveSig] int get_accState([In, MarshalAs(UnmanagedType.Struct)] object child,
            [Out, MarshalAs(UnmanagedType.Struct)] out object state);
        [PreserveSig] int get_accHelp([In, MarshalAs(UnmanagedType.Struct)] object child,
            [MarshalAs(UnmanagedType.BStr)] out string help);
        [PreserveSig] int get_accHelpTopic([MarshalAs(UnmanagedType.BStr)] out string helpFile,
            [In, MarshalAs(UnmanagedType.Struct)] object child, out int helpContext);
        [PreserveSig] int get_accKeyboardShortcut([In, MarshalAs(UnmanagedType.Struct)] object child,
            [MarshalAs(UnmanagedType.BStr)] out string shortcut);
        [PreserveSig] int get_accFocus([Out, MarshalAs(UnmanagedType.Struct)] out object child);
        [PreserveSig] int get_accSelection([Out, MarshalAs(UnmanagedType.Struct)] out object children);
        [PreserveSig] int get_accDefaultAction([In, MarshalAs(UnmanagedType.Struct)] object child,
            [MarshalAs(UnmanagedType.BStr)] out string action);
        [PreserveSig] int accSelect(int flags, [In, MarshalAs(UnmanagedType.Struct)] object child);
        [PreserveSig] int accLocation(out int left, out int top, out int width, out int height,
            [In, MarshalAs(UnmanagedType.Struct)] object child);
        [PreserveSig] int accNavigate(int dir, [In, MarshalAs(UnmanagedType.Struct)] object child,
            [Out, MarshalAs(UnmanagedType.Struct)] out object endAt);
        [PreserveSig] int accHitTest(int x, int y, [Out, MarshalAs(UnmanagedType.Struct)] out object child);
        [PreserveSig] int accDoDefaultAction([In, MarshalAs(UnmanagedType.Struct)] object child);
        [PreserveSig] int put_accName([In, MarshalAs(UnmanagedType.Struct)] object child, string name);
        [PreserveSig] int put_accValue([In, MarshalAs(UnmanagedType.Struct)] object child, string value);
    }

    /// <summary>CAXA 主框架的 MSAA 取证与代点。</summary>
    [ComVisible(false)]
    internal static class Msaa
    {
        private const uint OBJID_CLIENT = 0xFFFFFFFC;
        private const int CHILDID_SELF = 0;
        private const int MaxNodes = 12000;
        private const int MaxDepth = 10;

        // ------------------------------------------------------------ 接口声明

        [DllImport("oleacc.dll", ExactSpelling = true)]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleChildren(IAccessible container, int start, int count,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children,
            out int obtained);

        // ------------------------------------------------------------ Win32（仅取证用）

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

        /// <summary>MSAA 状态位：STATE_SYSTEM_UNAVAILABLE（灰掉，点它等于点空）。</summary>
        private const int StateUnavailable = 0x00000001;

        /// <summary>条目是否处于不可用状态（state 在命中时已按十六进制字符串存好）。</summary>
        private static bool IsUnavailable(MsaaNode n)
        {
            int st;
            return int.TryParse(n.State, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out st) &&
                (st & StateUnavailable) != 0;
        }

        // ------------------------------------------------------------ 取根对象

        private static IAccessible RootOf(IntPtr hwnd)
        {
            return RootOf(hwnd, false);
        }

        private static IAccessible RootOf(IntPtr hwnd, bool quiet)
        {
            Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
            object o;
            int hr;
            try { hr = AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out o); }
            catch (Exception ex)
            {
                Diagnostics.Log("Msaa: AccessibleObjectFromWindow 异常 " + ex.Message);
                return null;
            }
            if (hr != 0 || o == null)
            {
                if (!quiet)
                    Diagnostics.Trace(string.Format("Msaa: 0x{0:X} 取不到 OBJID_CLIENT hr=0x{1:X8}",
                        hwnd.ToInt64(), hr));
                return null;
            }
            return o as IAccessible;
        }

        // ------------------------------------------------------------ 遍历

        /// <summary>
        /// 只在指定窗口的可访问性**子树**里找第一个命中：一次遍历问完所有名字，配额独立于框架树。
        /// 必须以 pane 自己为根——Walk 只沿 IAccessible 的孩子下钻、不跨子窗口句柄，
        /// 从框架根扫 2841 个节点也扫不到属性 pane 里的条目（实机 14:37 的漏判就是这么来的）。
        /// quiet=true 时未命中不写日志：轮询热路径每几百毫秒问一次，别刷屏。
        /// </summary>
        public static MsaaNode FirstIn(IntPtr hwnd, string[] needlesNormalized, bool exact,
            int maxNodes, bool quiet)
        {
            if (hwnd == IntPtr.Zero || needlesNormalized == null) return null;
            List<MsaaNode> hits = WalkRoot(hwnd, needlesNormalized, exact, true, maxNodes, "子树", quiet);
            return hits.Count > 0 ? hits[0] : null;
        }

        public static MsaaNode FirstIn(IntPtr hwnd, string[] needlesNormalized, bool exact, int maxNodes)
        {
            return FirstIn(hwnd, needlesNormalized, exact, maxNodes, true);
        }

        /// <summary>
        /// 列出一棵可访问性子树里所有**有名字**的条目（带矩形 / 角色 / 状态 / 默认动作）。
        /// 网格行（如「改变链接文件」里的 文件名 单元格）没有自己的 HWND，只能这样问出来；
        /// FirstIn 只回第一个命中，逐行操作需要整张表。
        /// </summary>
        public static List<MsaaNode> AllIn(IntPtr hwnd, int maxNodes)
        {
            return WalkRoot(hwnd, null, false, false, maxNodes, "子树", true);
        }

        private static List<MsaaNode> WalkRoot(IntPtr hwnd, string[] needles, bool exact,
            bool stopAfterFirst, int maxNodes, string tag, bool quiet)
        {
            var hits = new List<MsaaNode>();
            if (hwnd == IntPtr.Zero)
            {
                Diagnostics.Log("Msaa: 找不到 CAXA " + tag + "，无法走可访问性通道");
                return hits;
            }
            IAccessible root = RootOf(hwnd, quiet);
            if (root == null) return hits;
            Budget budget = new Budget { Left = maxNodes, StopAfterFirst = stopAfterFirst };
            try { Walk(root, 0, budget, hits, needles, exact); }
            catch (Exception ex)
            {
                // 遍历在 STA 等待循环里被反复调用，任何一次异常都不能打断业务流程
                Diagnostics.Log("Msaa: 遍历中断 " + ex.Message);
            }
            if (quiet && hits.Count == 0) return hits;
            Diagnostics.Trace("Msaa: " + tag + " 0x" + hwnd.ToInt64().ToString("X") +
                " 遍历 " + (maxNodes - budget.Left) + " 节点，命中「" +
                (needles == null ? "任意有名字条目" : string.Join("/", needles)) + "」" +
                hits.Count + " 个");
            return hits;
        }

        /// <summary>节点配额（整棵树共享一个计数器；值传递的话每层都会拿到副本，等于没有限流）。</summary>
        private sealed class Budget
        {
            public int Left = MaxNodes;
            public bool StopAfterFirst;
            public bool Stop;
        }

        private static void Walk(IAccessible acc, int depth, Budget budget,
            List<MsaaNode> hits, string[] needles, bool exact)
        {
            if (acc == null || depth > MaxDepth || budget.Left <= 0 || budget.Stop) return;
            int count;
            if (acc.get_accChildCount(out count) != 0 || count <= 0) return;
            int take = Math.Min(count, 200);
            object[] kids = new object[take];
            int got;
            if (AccessibleChildren(acc, 0, take, kids, out got) != 0) return;

            for (int i = 0; i < got && budget.Left > 0 && !budget.Stop; i++)
            {
                budget.Left--;
                object k = kids[i];
                if (k == null) continue;

                IAccessible childAcc = k as IAccessible;
                object childId = childAcc != null ? (object)CHILDID_SELF : k;
                IAccessible use = childAcc != null ? childAcc : acc;

                string name;
                if (use.get_accName(childId, out name) != 0 || name == null) name = "";

                string norm = name.Length > 0 ? UiWin.Normalize(name) : "";
                bool hit = norm.Length > 0 && (needles == null || Matches(norm, needles, exact));
                if (hit)
                {
                    MsaaNode node = new MsaaNode { Acc = use, ChildId = childId, Name = name };
                    string act;
                    if (use.get_accDefaultAction(childId, out act) == 0) node.Action = act ?? "";
                    object st;
                    if (use.get_accState(childId, out st) == 0 && st != null)
                        node.State = Convert.ToInt32(st).ToString("X");
                    object ro;
                    if (use.get_accRole(childId, out ro) == 0 && ro != null)
                    {
                        int r;
                        if (int.TryParse(Convert.ToString(ro), out r)) node.Role = r;
                    }
                    int l, t, w, h;
                    if (use.accLocation(out l, out t, out w, out h, childId) == 0)
                    {
                        node.Left = l; node.Top = t; node.Width = w; node.Height = h;
                    }
                    hits.Add(node);
                    if (budget.StopAfterFirst)
                    {
                        budget.Stop = true;
                        return;
                    }
                }

                // 只下钻真正的子 IAccessible；CHILDID 型条目属于父对象，再走一遍会重复枚举
                if (childAcc != null) Walk(childAcc, depth + 1, budget, hits, needles, exact);
            }
        }

        private static bool Matches(string norm, string[] needles, bool exact)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                string n = needles[i];
                if (string.IsNullOrEmpty(n)) continue;
                if (exact ? norm == n : norm.IndexOf(n, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // ------------------------------------------------------------ 代点

        /// <summary>
        /// 点一个可访问性条目：只走 accDoDefaultAction（BCG 自绘条目的正解，不依赖坐标）。
        ///
        /// ★ 2026-11-01 18:5x 去掉「失败就按矩形坐标点一下」的兜底 ★
        ///   18:52 那轮把它点到了**本插件窗口**上（「Default」的矩形是脏的，中心落在
        ///   0x4E05D0 class=WindowsForms10… 的 (174,301)），accDoDefaultAction 失败后
        ///   仍然把左键投过去 —— 等于为了"多一条兜底"去按自己的界面。
        ///   需要坐标点击的场合（列表行、pane 绿勾）都走 ClickAt 并**必须**带 owner 校验。
        /// </summary>
        public static bool Click(MsaaNode n, string why)
        {
            if (n == null) return false;
            if (IsUnavailable(n))
            {
                Diagnostics.Trace("Msaa: 「" + n.Name + "」状态不可用(state=0x" + n.State +
                    ")，不代点（" + why + "）");
                return false;
            }
            try
            {
                int hr = n.Acc.accDoDefaultAction(n.ChildId);
                if (hr == 0)
                {
                    Diagnostics.Trace("Msaa: accDoDefaultAction「" + n.Name + "」成功（" + why +
                        "）动作=[" + n.Action + "]");
                    return true;
                }
                Diagnostics.Trace("Msaa: accDoDefaultAction「" + n.Name + "」hr=0x" +
                    hr.ToString("X8") + "，没有别的通道可用（" + why + "）");
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("Msaa: accDoDefaultAction 异常 " + ex.Message + "（" + why + "）");
            }
            return false;
        }

        // ------------------------------------------------------------ 取证

        /// <summary>
        /// 把 <paramref name="hwnd"/> 这棵可访问性子树的名字清单写进取证块（只写有名字的条目，
        /// 上限 <paramref name="maxLines"/> 行）。
        /// 必须按 pane 取证：Collect 只沿 IAccessible 孩子下钻、**不跨子窗口句柄**，
        /// 所以从框架根扫 2841 个节点也看不到属性 pane 里的条目（实机 14:37 就是这么把还开着的
        /// 面板判成「不存在」的）。要问 pane 的内容，就把 pane 自己当根传进来。
        /// </summary>
        public static string NamesDump(IntPtr hwnd, int maxLines)
        {
            var sb = new StringBuilder();
            IntPtr root32 = hwnd == IntPtr.Zero ? UiWin.CadFrame() : hwnd;
            if (root32 == IntPtr.Zero) { sb.AppendLine("(找不到 CAXA 主框架)"); return sb.ToString(); }
            sb.AppendLine("---------- MSAA 条目清单（根 0x" + root32.ToInt64().ToString("X") +
                " class=" + ClassOf(root32) + " 文字=[" + TextOf(root32) + "]）----------");
            IAccessible root = RootOf(root32);
            if (root == null) { sb.AppendLine("(OBJID_CLIENT 取不到 IAccessible)"); return sb.ToString(); }
            int count;
            root.get_accChildCount(out count);
            sb.AppendLine("OBJID_CLIENT childCount=" + count);

            var seen = new List<string>();
            Budget budget = new Budget();
            Collect(root, 0, budget, seen);
            int written = 0;
            foreach (string s in seen)
            {
                if (written++ >= maxLines) { sb.AppendLine("…（其余省略，共 " + seen.Count + " 条）"); break; }
                sb.AppendLine(s);
            }
            return sb.ToString();
        }

        private static void Collect(IAccessible acc, int depth, Budget budget, List<string> outNames)
        {
            if (acc == null || depth > MaxDepth || budget.Left <= 0) return;
            int count;
            if (acc.get_accChildCount(out count) != 0 || count <= 0) return;
            int take = Math.Min(count, 200);
            object[] kids = new object[take];
            int got;
            if (AccessibleChildren(acc, 0, take, kids, out got) != 0) return;
            for (int i = 0; i < got && budget.Left > 0; i++)
            {
                budget.Left--;
                object k = kids[i];
                if (k == null) continue;
                IAccessible childAcc = k as IAccessible;
                object childId = childAcc != null ? (object)CHILDID_SELF : k;
                IAccessible use = childAcc != null ? childAcc : acc;
                string name;
                if (use.get_accName(childId, out name) == 0 && !string.IsNullOrEmpty(name))
                {
                    string act;
                    use.get_accDefaultAction(childId, out act);
                    int l, t, w, h;
                    use.accLocation(out l, out t, out w, out h, childId);
                    outNames.Add(new string(' ', depth * 2) + name +
                        (string.IsNullOrEmpty(act) ? "" : " 动作=[" + act + "]") +
                        " 矩形=" + l + "," + t + " " + w + "x" + h);
                }
                if (childAcc != null)
                    Collect(childAcc, depth + 1, budget, outNames);
            }
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TextOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
