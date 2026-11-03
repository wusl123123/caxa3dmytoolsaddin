using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 钣金件替换路上那两张业务弹窗的专用填写器（通用代点器只管简单确认框）：
    ///   「选择用于外部链接的零件或装配」——树：文件根节点 → 未展开件 / 展开件
    ///   「链接的配置」——列表：缺省 / 展开xxx
    /// 用户的手工做法（截图 3/4）是先选中**未展开 / 缺省**那一项再点确定。空着点确定，
    /// CAXA 要么把整个场景插进来（钣金零件 type=20 降级成装配体 type=2），
    /// 要么干脆不让框关（实机 16:38 那张「链接的配置」被连点 6 次确定都没关）。
    ///
    /// ★ 2026-11-01 17:38 实机卡死的根因（本轮修复，见规则 09 坑点表）★
    ///   17:38:04.363 接管「链接的配置」后日志**戛然而止**，同一张框在 17:37:25 那轮
    ///   7 毫秒后留下一行 `LinkPickDialog EX: 算术运算导致溢出。` —— DumpChildren 里
    ///   `GetDlgCtrlID(h).ToInt32()` 抛 OverflowException：GetDlgCtrlID 原生返回 int，
    ///   P/Invoke 却声明成 IntPtr，x64 下**负的控件 id 被零扩展**（实机同一轮
    ///   「导入零件/装配」的 id = -516845568 → 0x00000000E12A…），ToInt32() 一碰即抛。
    ///   更致命的是 `_tried` 在干活**之前**就置了位：异常一抛，后面每轮 Tick 都当成
    ///   「已尽力过」直接 return ⇒ 再也不会点「确定」⇒ 模态框永远挂着，把 CAXA 主线程
    ///   （连同主线程上那次 accDoDefaultAction）一起堵死，用户看到的就是「卡在截图位置，
    ///   只能手工点确定，点完过 21 秒 / 56 秒才继续」。
    ///   所以现在：① id 用 int 声明、LRESULT 用 unchecked 取；② 取证单独 try/catch，
    ///   **取证失败绝不允许影响代点**；③ 改成「有次数上限的重试」而不是「只试一次就认命」，
    ///   ④ 每条通道都单独 try，最后无论如何都把「确定」点出去。
    ///
    /// 为什么主通道是控件句柄而不是 MSAA（实机 16:38~16:42 六轮取证 + 17:37 复现）：
    ///   MSAA 在这两张 MFC 框上只回**窗口装饰条目**（「系统」「输入法」「最小化」「最大化」
    ///   「垂直滚动条」「上一行」…共 148 条，一条真正的树/列表条目都没有），照它择优 = 每轮
    ///   选到「系统」、点不动、再 4 秒兜底点确定，一张框刷 42 秒日志。控件消息通道里
    ///   **不读文字、只递句柄**的那几条才是可靠的：TVM_SELECTITEM（树）、LVM_SETITEMSTATE
    ///   （列表视图）、LB_SETCURSEL（ListBox）、以及在列表控件第一行投一次真实左键
    ///   （「链接的配置」是自绘列表，LB_GETCOUNT 回 0，只能按坐标点）。
    ///
    /// ★ 2026-11-01 18:5x 把 MSAA 兜底整段删掉 ★
    ///   18:52 那轮日志留下了铁证：树/列表通道都成功之后，MSAA 通道仍然跑了一遍，把 84 条窗口装饰
    ///   记进日志，还把「Default」那一项的矩形中心算到了**本插件窗口**上（0x4E05D0
    ///   class=WindowsForms10…），accDoDefaultAction 失败后又改成按坐标往那儿投了一次左键 ——
    ///   位置 174,301 正好落在插件窗体上。等于为了"多一条兜底"去点自己的界面。
    ///   这两张框的条目选择只有「递句柄给控件」这一条正确通道，选不上就点确定（CAXA 会用缺省项），
    ///   不再保留这个既误点又刷屏的兜底。
    /// </summary>
    internal static class LinkPickDialog
    {
        private static readonly string[] OwnedTitles =
        {
            "选择用于外部链接的零件或装配",
            "链接的配置"
        };

        /// <summary>同一句柄 + 同一标题的框，两次 Tick 间隔小于它就当还是那一次；超过就视为新实例。</summary>
        private const int InstanceGapMs = 1500;

        /// <summary>同一张框两次尝试之间的间隔：给 CAXA 处理「确定」的时间，也压住日志刷屏。</summary>
        private const int RetryGapMs = 1200;

        /// <summary>
        /// 上一轮「确定」已经发出去了、框还在时，隔多久才补点第二次。
        /// 必须比 RetryGapMs 长得多：CAXA 的 OnOK 里可能同步跑插入，框会在屏幕上多留好几秒，
        /// 1 秒级的补点等于对同一个提交按两遍（会插两次）。6 秒后还在 = 真的没被接住。
        /// </summary>
        private const int StuckGapMs = 6000;

        /// <summary>一张框最多点几次确定。模态框不关就会把 CAXA 冻住，所以宁可多点几次。</summary>
        private const int MaxAttempts = 3;

        private static IntPtr _dlg;
        private static string _title = "";
        private static int _lastSeenTick;
        private static int _attempts;
        private static int _lastAttemptTick;
        private static bool _acceptPosted;   // 上一轮「确定」是否真的投出去了（决定补点间隔）
        private static bool _gaveUp;
        private static string _lastError;

        /// <summary>标题是否属于本填写器（命中后通用代点器必须让路）。</summary>
        public static bool IsMine(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            for (int i = 0; i < OwnedTitles.Length; i++)
                if (title.IndexOf(OwnedTitles[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>由 PopupAutoYes 的后台扫描线程调用（约每 120ms 一次）。</summary>
        public static void Tick(IntPtr dlg)
        {
            try { TickCore(dlg); }
            catch (Exception ex)
            {
                // 异常只记一次：代点循环 120ms 一轮，刷屏会把关键日志埋掉
                LogOnce("LinkPickDialog EX: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void TickCore(IntPtr dlg)
        {
            string title = TextOf(dlg);
            if (string.IsNullOrEmpty(title)) return;   // 句柄已失效（框已关）：不接管、不刷日志
            int now = Environment.TickCount;
            bool sameInstance = dlg == _dlg && title == _title && now - _lastSeenTick < InstanceGapMs;
            _lastSeenTick = now;

            if (!sameInstance)
            {
                _dlg = dlg; _title = title; _attempts = 0; _gaveUp = false; _acceptPosted = false;
                uint pid;
                GetWindowThreadProcessId(dlg, out pid);
                // 只有「不是本进程」才值得记：那种情况下带指针的控件消息必然读废，日志必须留证；
                // 同进程只留标题一行，够定位是哪张框（hwnd/pid 对用户没有意义）。
                Diagnostics.Log("LinkPickDialog: 接管「" + title + "」" +
                    (pid == GetCurrentProcessId() ? "" : "（pid=" + pid + " 不是本进程，控件消息读不到）"));
            }
            else if (_attempts >= MaxAttempts)
            {
                if (!_gaveUp)
                {
                    _gaveUp = true;
                    Diagnostics.Log("LinkPickDialog: 警告：这张框已点 " + MaxAttempts +
                        " 次确定仍未关闭，停止代点。请手工选中「缺省/未展开」那一项再点确定。" +
                        "（只要这张框还开着，CAXA 主线程就一直堵着）");
                }
                return;          // 已尽力：不再点、也不再刷日志
            }
            else if (now - _lastAttemptTick < (_acceptPosted ? StuckGapMs : RetryGapMs))
            {
                return;          // 两次尝试之间留时间给 CAXA 响应（点过确定的要等更久，见 StuckGapMs）
            }

            _attempts++;
            _lastAttemptTick = now;

            string picked = null;
            try { picked = PickEntry(dlg); }
            catch (Exception ex)
            {
                LogOnce("LinkPickDialog 选条目失败(第 " + _attempts + " 次)： " +
                    ex.GetType().Name + " " + ex.Message);
            }

            if (picked != null)
            {
                Diagnostics.Log("LinkPickDialog: 第 " + _attempts + " 次尝试，已选中「" + picked + "」→ 点确定");
            }
            else
            {
                Diagnostics.Log("LinkPickDialog: 第 " + _attempts + " 次尝试：认不出「未展开/缺省」条目，直接点确定。" +
                    "点了不关就请手工选中未展开（缺省）那一项再点确定——空着点确定插进来的是整个场景。");
            }

            _acceptPosted = PostAccept(dlg);
        }

        private static void LogOnce(string msg)
        {
            if (string.Equals(msg, _lastError, StringComparison.Ordinal)) return;
            _lastError = msg;
            Diagnostics.Log(msg);
        }

        /// <summary>
        /// 在框里找未展开/缺省条目并选中它，回其名字。按「确定性」排序，全部读文字以外的机制：
        ///   ① 树句柄 TVM_SELECTITEM（实机唯一验证过能保住 type=20 的通道）
        ///   ② 列表视图 LVM_SETITEMSTATE 选中第 1 行
        ///   ③ ListBox LB_SETCURSEL 选中第 1 项
        ///   ④ 列表控件第一行投真实左键（「链接的配置」是自绘列表，LB_GETCOUNT 回 0）
        /// 选不上就返回 null，由调用方空着点确定（CAXA 用缺省项）——不再保留 MSAA 兜底，见类注释。
        /// </summary>
        private static string PickEntry(IntPtr dlg)
        {
            string tree = PickTreeFirstChildByHandle(dlg);
            if (tree != null) return tree;

            IntPtr list = IntPtr.Zero;
            string listCls = "";
            EnumChildWindows(dlg, (h, l) =>
            {
                string c = ClassOf(h);
                if (c == "SysListView32" || c == "ListBox" || c == "SysListView")
                {
                    list = h; listCls = c;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (list != IntPtr.Zero) return PickListFirstRow(list, listCls);
            return null;
        }

        // ------------------------------------------------------------ 树通道

        private const uint TVM_GETNEXTITEM = 0x110A;
        private const uint TVM_SELECTITEM = 0x110B;
        private const uint TVM_EXPAND = 0x1112;
        private static readonly IntPtr TVGN_ROOT = IntPtr.Zero;
        private static readonly IntPtr TVGN_CHILD = (IntPtr)4;
        private static readonly IntPtr TVGN_CARET = (IntPtr)9;
        private static readonly IntPtr TVE_EXPAND = (IntPtr)2;

        /// <summary>
        /// 根节点是 .ics 文件本身（选它等于「整个场景」），所以选中它下面第一个子项。
        /// 这条通道只递句柄、不读文字，实机六轮保住 type=20 靠的就是它。
        /// </summary>
        private static string PickTreeFirstChildByHandle(IntPtr dlg)
        {
            IntPtr tv = IntPtr.Zero;
            EnumChildWindows(dlg, (h, l) =>
            {
                if (ClassOf(h) != "SysTreeView32") return true;
                tv = h;
                return false;
            }, IntPtr.Zero);
            if (tv == IntPtr.Zero) return null;

            IntPtr root = Send(tv, TVM_GETNEXTITEM, TVGN_ROOT, IntPtr.Zero);
            if (root == IntPtr.Zero) return null;
            Send(tv, TVM_EXPAND, TVE_EXPAND, root);
            IntPtr first = Send(tv, TVM_GETNEXTITEM, TVGN_CHILD, root);
            if (first == IntPtr.Zero) return null;
            if (Send(tv, TVM_SELECTITEM, TVGN_CARET, first) == IntPtr.Zero) return null;
            return "树：根下第 1 子项";
        }

        // ------------------------------------------------------------ 列表通道

        private const uint LB_GETCOUNT = 0x0181;
        private const uint LB_SETCURSEL = 0x0186;
        private const uint LVM_GETITEMCOUNT = 0x1004;   // LVM_FIRST + 4
        private const uint LVM_SETITEMSTATE = 0x102B;   // LVM_FIRST + 43
        private const uint LVIS_FOCUSED = 0x0001;
        private const uint LVIS_SELECTED = 0x0002;

        /// <summary>「链接的配置」第一行（缺省）的选中。自绘列表只能按坐标点，等同于用户手点。</summary>
        private static string PickListFirstRow(IntPtr list, string cls)
        {
            if (cls == "SysListView32" || cls == "SysListView")
            {
                int n = Lr(Send(list, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero));
                if (n > 0 && SetLvItemState(list, 0, LVIS_SELECTED | LVIS_FOCUSED, LVIS_SELECTED | LVIS_FOCUSED))
                    return "列表：第 1 项";
                return ClickFirstRow(list);
            }

            int cnt = Lr(Send(list, LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero));
            if (cnt > 0)
            {
                Send(list, LB_SETCURSEL, IntPtr.Zero, IntPtr.Zero);
                return "列表：第 1 项";
            }
            // 实机：这张框的 LB_GETCOUNT 回 0 —— 条目是自绘的，控件自己都不承认有条目，
            // 只能按用户的做法在列表第一行投一次左键。
            return ClickFirstRow(list);
        }

        /// <summary>
        /// 在列表控件客户区第一行投一次真实左键。只点控件内部靠左上的一点（+14,+10）。
        /// 目标窗口的取法：先 WindowFromPoint，命中不在本框里就**退回列表控件自己** ——
        /// 它就是从这张框里枚举出来的子控件，投给它一定不会点到框外（18:52 那轮的点就丢在插件窗体上）。
        /// </summary>
        private static string ClickFirstRow(IntPtr list)
        {
            RECT r;
            if (!GetWindowRect(list, out r)) return null;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 24 || h < 12) return null;
            POINT pt = new POINT { X = r.Left + 14, Y = r.Top + 10 };
            IntPtr target = WindowFromPoint(pt);
            if (target == IntPtr.Zero || (target != list && target != _dlg && !IsChild(_dlg, target)))
            {
                Diagnostics.Trace("LinkPickDialog: 列表左上命中的窗口不在本框内，改投列表控件自己");
                target = list;
            }
            POINT cl = new POINT { X = pt.X, Y = pt.Y };
            ScreenToClient(target, ref cl);
            IntPtr lp = (IntPtr)((cl.Y << 16) | (cl.X & 0xFFFF));
            PostMessage(target, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
            PostMessage(target, WM_LBUTTONUP, IntPtr.Zero, lp);
            return "列表：第一行坐标点击";
        }

        /// <summary>LVM_SETITEMSTATE 需要一个能跨进控件进程的 LVITEM：同一进程内直接给本地缓冲即可。</summary>
        private static bool SetLvItemState(IntPtr lv, int index, uint state, uint mask)
        {
            var item = new LVITEM
            {
                stateMask = mask,
                state = state,
                iItem = index,
                iSubItem = 0
            };
            IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(LVITEM)));
            try
            {
                Marshal.StructureToPtr(item, buf, false);
                IntPtr ret = Send(lv, LVM_SETITEMSTATE, (IntPtr)index, buf);
                return ret != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("LinkPickDialog: LVM_SETITEMSTATE 异常 " + ex.Message);
                return false;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LVITEM
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public uint cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        /// <summary>点「确定」：只按文字命中，绝不去碰「取消」。返回是否真的把点击投出去了。</summary>
        private static bool PostAccept(IntPtr dlg)
        {
            IntPtr ok = IntPtr.Zero;
            EnumChildWindows(dlg, (h, l) =>
            {
                if (ClassOf(h) != "Button") return true;
                string raw = TextOf(h);
                if (raw.Length == 0 || raw.Length > 32) return true;
                string norm = raw.ToLowerInvariant().Replace("&", "").Replace("(", "");
                if (norm.StartsWith("确定") || norm == "ok" || norm.StartsWith("yes"))
                {
                    ok = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (ok == IntPtr.Zero)
            {
                Diagnostics.Log("LinkPickDialog: 没找到「确定」按钮，交给手工");
                return false;
            }
            // PostMessage 而非 SendMessage：点完确定 CAXA 会同步跑插入流程，
            // 同步等返回会把本扫描线程卡在模态循环里，后面第二张框就再也点不到。
            bool posted = PostMessage(ok, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            if (posted) PopupAutoYes.CountExternalClick();
            return posted;
        }

        // ------------------------------------------------------------ P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "EnumChildWindows")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr l);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        // 必须显式写 EntryPoint：user32 只导出 SendMessageW/A（见规则 05 §4.4）
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr Send(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr h);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);

        private const uint BM_CLICK = 0x00F5;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint MK_LBUTTON = 0x0001;

        /// <summary>LRESULT → int：直接 ToInt32() 在 x64 上遇到高位为 1 的值就抛溢出。</summary>
        private static int Lr(IntPtr v)
        {
            try { return unchecked((int)v.ToInt64()); } catch { return 0; }
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
