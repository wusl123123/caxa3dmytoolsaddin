using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Caxa3dMyTools.Contracts
{
    /// <summary>
    /// 键盘归属抢救：让插件自己窗口里的文本框重新能用 Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+A / Ctrl+Z。
    ///
    /// 为什么要它（实机定案，来源 caxa3ddiffrename 11-06 两轮 trace）：
    ///   Add-in 窗口跑在 IronCAD.exe 主线程上，CAXA 是 MFC/BCGP 程序。它的
    ///   CWinThread::PumpMessage 在 DispatchMessage **之前**先跑 PreTranslateMessage，
    ///   其中的 TranslateAccelerator 用宿主加速键表把 Ctrl+C/V/X/A/Z 整条吃掉，
    ///   于是这些键变成"对设计环境里的元素操作"，而不是对插件文本框操作。
    ///   结果是 WinForms 的 ProcessCmdKey / KeyPress 结构性不触发（实机 ProcessCmdKey 到达行 = 0），
    ///   所以 KeyPreview、控件 KeyDown、MessageFilter 这类常规写法在这里全部无效。
    ///
    /// 唯一被实测有效的通道：WH_GETMESSAGE **线程钩子**。它在消息"出队那一刻"被调用，
    /// 早于宿主 PreTranslateMessage，因此能在吞键之前拿到原始 MSG：
    ///   ① 认目标：从 msg.hwnd 沿 GetParent 链向上走，走到本工具窗口句柄 ⇒ 这个键本来就该归我们；
    ///      （不能用 IsChild —— IsChild 只测直接子窗口，文本框一旦嵌在 Panel/TabPage 里就判不出来）
    ///   ② 抢回来：把 msg.message 改成 WM_NULL（宿主加速键表匹配不到 WM_NULL，不再操作设计环境），
    ///      再 PostMessage(WM_APP_CHORD, vk, 目标 hwnd) 给本工具窗口，由窗口的 WndProc 调 Route 落地；
    ///   ③ 落地：优先用托管 TextBoxBase.Copy/Cut/Paste/SelectAll；句柄不是 TextBoxBase（ComboBox 的
    ///      内部编辑框、第三方编辑框）时按窗口类发原生 WM_COPY/WM_CUT/WM_PASTE/EM_SETSEL/EM_UNDO。
    ///
    /// 开关（%TEMP% 空文件，都是"存在才生效"，默认 = 抢救开着）：
    ///   caxa3dmytoolsaddin.nokeyrescue —— 存在 ⇒ 整体关闭（钩子都不装，行为与接线前逐句一致）
    ///   caxa3dmytoolsaddin.keyprobe     —— 存在 ⇒ 额外记录非目标按键（默认只记 Ctrl 命中键）
    ///
    /// 铁律对账：
    ///   · 钩子只在窗口 Activated 时装、Deactivate/FormClosed 时必拆；不在 Initialize/InitSelf 里装，
    ///     不建常驻隐藏顶层窗口，不订阅 Application.ThreadException。
    ///   · 回调内不碰 COM、不阻塞、不弹框、不抛异常；只做直读直写 + 一次 PostMessage。
    ///   · 委托句柄存静态字段防 GC（回调 thunk 被回收 = 原生 AV）。
    ///   · 一次按键钩子被调 3 次（2× PM_NOREMOVE + 1× PM_REMOVE）⇒ 只在 REMOVE 那次动手并写日志，
    ///     且**改过 msg 才写回 lParam**（NOREMOVE 那份不许动）。
    ///   · 判成功只认权威副作用：文本框内容真的变了（复制⇒剪贴板有内容；粘贴⇒框文本变长）。
    /// </summary>
    public static class ChordRescue
    {
        private const string MarkerOff = "caxa3dmytoolsaddin.nokeyrescue";
        private const string MarkerProbe = "caxa3dmytoolsaddin.keyprobe";

        private const int WH_GETMESSAGE = 3;
        private const int WM_NULL = 0x0000;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_COPY = 0x0003;
        private const int WM_CUT = 0x0300;
        private const int WM_PASTE = 0x0302;
        private const int EM_SETSEL = 0x00B1;         // EDIT：wParam=0, lParam=-1 ⇒ 全选
        private const int EM_UNDO_EDIT = 0x00C7;      // EDIT 的撤销
        private const int EM_UNDO_RICH = 0x0454;      // RICHEDIT 的撤销 = WM_USER + 84（不是 0x00C7）
        private const int PM_NOREMOVE = 0;
        private const int PM_REMOVE = 1;
        private const int VK_CONTROL = 0x11;
        private const int MAX_TREE_DEPTH = 64;

        /// <summary>转发给宿主窗口 WndProc 的私有消息：wParam = VK 码，lParam = 目标编辑框句柄。</summary>
        public const int WM_APP_CHORD = 0x400 + 71;   // 沿用 caxa3ddiffrename 已实机验证的同号常量

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        private delegate IntPtr GetMsgProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, GetMsgProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        // 必须持有：只把 lambda 交给原生钩子而不引用它，GC 会连原生 thunk 一起回收 ⇒ 下一次按键跳进已回收的 thunk。
        private static GetMsgProc _proc;
        private static IntPtr _hook = IntPtr.Zero;
        private static Form _ownerForm;
        private static IntPtr _owner = IntPtr.Zero;
        private static Action<string> _sink;
        private static bool _enabled = true;
        private static bool _verbose;
        private static int _seq;
        private static int _noremoveSeen;
        private static volatile bool _inCallback;

        /// <summary>钩子当前是否装着（只读判据；装了不等于一定收得到键，还要看线程口径）。</summary>
        public static bool IsHooked { get { return _hook != IntPtr.Zero; } }

        /// <summary>当前钩子服务的窗口（null = 没装）。</summary>
        public static Form CurrentOwner { get { return _ownerForm; } }

        /// <summary>
        /// 在窗口的 OnActivated 里调用（不要在 Initialize / InitSelf 里调）。
        /// %TEMP%\caxa3dmytoolsaddin.nokeyrescue 存在 ⇒ 直接返回 false，行为与接线前逐句一致。
        /// </summary>
        /// <param name="owner">要抢救快捷键的插件窗口</param>
        /// <param name="log">该插件自己的日志转发口（各插件 Diagnostics 方法名不同，所以由调用方给委托）</param>
        public static bool Install(Form owner, Action<string> log)
        {
            if (owner == null) return false;
            string temp = Path.GetTempPath();
            _enabled = !File.Exists(Path.Combine(temp, MarkerOff));
            _verbose = File.Exists(Path.Combine(temp, MarkerProbe));
            if (!_enabled)
            {
                if (_verbose) Emit(log, "ChordRescue: 已关闭（" + MarkerOff + " 存在），不装钩子");
                return false;
            }

            _sink = log;
            _ownerForm = owner;
            _owner = owner.IsHandleCreated ? owner.Handle : IntPtr.Zero;
            _seq = 0;
            _noremoveSeen = 0;
            if (_owner == IntPtr.Zero) { Emit(log, "ChordRescue.Install: 窗口句柄还没建，本次跳过"); return false; }
            if (_hook != IntPtr.Zero) { Emit(log, "ChordRescue.Install: 钩子已在，切换属主为 " + owner.GetType().Name); return true; }

            // 钩子按线程装 ⇒ 必须装"本窗口所属的那个线程"。两者不等时结论只能是"读不到"，
            // 所以两个口径都打出来（osTid = 当前调用线程，winTid = 窗口消息线程）。
            uint pid;
            uint winTid = GetWindowThreadProcessId(_owner, out pid);
            uint osTid = GetCurrentThreadId();
            _proc = HookCallback;
            _hook = SetWindowsHookExW(WH_GETMESSAGE, _proc, IntPtr.Zero, winTid);
            Emit(log, "ChordRescue.Install: rescue=Y verbose=" + _verbose +
                " 窗口=" + owner.GetType().Name + "/" + Hex(_owner) +
                " hook=" + (_hook == IntPtr.Zero ? ("FAIL gle=" + Marshal.GetLastWin32Error()) : "OK") +
                " 窗口线程=" + winTid + " 调用线程=" + osTid +
                (winTid != osTid ? " 【不同线程：钩子看不见这个窗口的消息检索，结论只能是读不到】" : ""));
            return _hook != IntPtr.Zero;
        }

        /// <summary>
        /// 在窗口的 OnDeactivate 与 OnFormClosed 里调用。
        /// 两个窗口的 Activated / Deactivate 到达顺序不确定 ⇒ 只有"当前属主"才真的拆，
        /// 否则新窗口刚装好的钩子会被旧窗口的失活事件拆掉（实测一轮会话里装拆各 8 次）。
        /// </summary>
        public static void Uninstall(Form owner, string why)
        {
            if (owner == null || !ReferenceEquals(_ownerForm, owner)) return;
            if (_hook == IntPtr.Zero) { _ownerForm = null; _owner = IntPtr.Zero; return; }
            bool ok = UnhookWindowsHookEx(_hook);
            Emit(_sink, "ChordRescue.Uninstall(" + why + "): " + (ok ? "OK" : "FAIL gle=" + Marshal.GetLastWin32Error()));
            _hook = IntPtr.Zero;
            _proc = null;
            _ownerForm = null;
            _owner = IntPtr.Zero;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
            if (_inCallback) return CallNextHookEx(_hook, nCode, wParam, lParam);
            _inCallback = true;
            try
            {
                MSG m = (MSG)Marshal.PtrToStructure(lParam, typeof(MSG));
                bool changed = false;
                if (m.message == WM_KEYDOWN || m.message == WM_SYSKEYDOWN)
                {
                    uint vk = (uint)m.wParam.ToInt64();
                    bool ours = IsOurKey(vk);
                    bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                    if (ours && ctrl) changed = ReportAndSwallow(ref m, vk, wParam);
                    else if (_verbose && (ours || vk == VK_CONTROL)) NoteOther(vk, ctrl, wParam, m.hwnd);
                }
                if (changed) Marshal.StructureToPtr(m, lParam, false);   // 改过才写回
            }
            catch { /* 钩子内不许抛：抛出 = 原生调用栈上的未处理异常 */ }
            finally { _inCallback = false; }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsOurKey(uint vk)
        {
            return vk == 0x43 || vk == 0x56 || vk == 0x58 || vk == 0x41 || vk == 0x5A;  // C V X A Z
        }

        private static void NoteOther(uint vk, bool ctrl, IntPtr wParam, IntPtr hwnd)
        {
            int pm = wParam.ToInt32();
            if (pm != PM_REMOVE) return;
            Emit(_sink, "ChordRescue[" + (++_seq) + "] 未接管 key=" + VkName(vk) +
                " ctrl=" + ctrl + " hwnd=" + Hex(hwnd) + " cls=" + Clss(hwnd));
        }

        /// <summary>返回 true = 已改写 MSG（调用方才需要写回 lParam）。</summary>
        private static bool ReportAndSwallow(ref MSG m, uint vk, IntPtr wParam)
        {
            int pm = wParam.ToInt32();
            if (pm != PM_REMOVE) { _noremoveSeen++; return false; }   // NOREMOVE 那份只计数，不写日志、不动结构体

            int depth;
            bool inTree = IsInOwnerTree(_owner, m.hwnd, out depth);
            IntPtr focus = GetFocus();

            StringBuilder sb = new StringBuilder();
            sb.Append("ChordRescue[").Append(++_seq).Append("] key=").Append(VkName(vk))
              .Append(" pm=REMOVE(另跳过 NOREMOVE ").Append(_noremoveSeen).Append(" 次)")
              .Append(" msg=0x").Append(m.message.ToString("X4"))
              .Append(" hwnd=").Append(Hex(m.hwnd))
              .Append(" cls=").Append(Clss(m.hwnd))
              .Append(" txt=\"").Append(Text(m.hwnd)).Append('"')
              .Append(" 在本工具窗口子树=").Append(inTree ? "Y" : "n").Append("(深度=").Append(depth).Append(')')
              .Append(" 焦点=").Append(Hex(focus)).Append('/').Append(Clss(focus));

            if (!inTree)
            {
                sb.Append(" 动作=放行(键本来就不归我们)");
                Emit(_sink, sb.ToString());
                return false;
            }

            m.message = WM_NULL;                      // 宿主加速键表匹配不到 WM_NULL ⇒ 不再操作设计环境
            bool posted = PostMessageW(_owner, (uint)WM_APP_CHORD, (IntPtr)(int)vk, m.hwnd);
            sb.Append(" 动作=吞掉并转发 post=").Append(posted ? "OK" : "FAIL gle=" + Marshal.GetLastWin32Error());
            Emit(_sink, sb.ToString());
            return true;
        }

        /// <summary>
        /// 沿 GetParent 链判断 hwnd 是否落在 ownerHwnd 这棵子树里（含直接子窗口与孙窗口）。
        /// 输出深度是为了把"隔了几层"写进日志：IsChild 只能测直接子窗口，嵌在 Panel/TabPage 里就漏判。
        /// </summary>
        private static bool IsInOwnerTree(IntPtr ownerHwnd, IntPtr hwnd, out int depth)
        {
            depth = 0;
            if (ownerHwnd == IntPtr.Zero || hwnd == IntPtr.Zero) return false;
            IntPtr h = hwnd;
            for (int i = 0; i < MAX_TREE_DEPTH; i++)
            {
                if (h == ownerHwnd) { depth = i; return true; }
                IntPtr p = GetParent(h);
                if (p == IntPtr.Zero || p == h) return false;
                h = p;
            }
            return false;
        }

        /// <summary>
        /// 在窗口的 WndProc 里调用：
        /// <c>protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }</c>
        /// </summary>
        /// <returns>true = 本消息已处理，调用方不要再交给 base.WndProc。</returns>
        public static bool Route(Form owner, ref Message m)
        {
            if (m.Msg != WM_APP_CHORD) return false;
            uint vk = (uint)(long)m.WParam;
            IntPtr target = m.LParam;

            int depth;
            if (!IsInOwnerTree(owner.Handle, target, out depth))
            {
                Emit(_sink, "ChordRescue.Route: 忽略 vk=" + VkName(vk) + " 目标不在本窗口子树 hwnd=" + Hex(target));
                return false;
            }

            string how;
            try
            {
                how = Apply(target, vk);
            }
            catch (Exception ex)
            {
                how = "异常 " + ex.GetType().Name + ": " + ex.Message;
            }
            Emit(_sink, "ChordRescue.Route: vk=" + VkName(vk) + " 目标=" + Hex(target) + '/' + Clss(target) +
                " 深度=" + depth + " 落地=" + how);
            return true;
        }

        /// <summary>把按键落到目标编辑框上，返回一句"怎么落地的"（这句子就是取证结论，不许返回空）。</summary>
        private static string Apply(IntPtr target, uint vk)
        {
            Control c = Control.FromHandle(target);
            TextBoxBase tb = c as TextBoxBase;
            if (tb != null)
            {
                switch (vk)
                {
                    case 0x43: tb.Copy(); return "托管 TextBoxBase.Copy";
                    case 0x58:
                        if (tb.ReadOnly) return "只读框，不执行 Cut";
                        tb.Cut(); return "托管 TextBoxBase.Cut";
                    case 0x56:
                        if (tb.ReadOnly) return "只读框，不执行 Paste";
                        if (!Clipboard.ContainsText()) return "剪贴板无文本，未粘贴";
                        tb.Paste(); return "托管 TextBoxBase.Paste";
                    case 0x41: tb.SelectAll(); return "托管 TextBoxBase.SelectAll";
                    case 0x5A: return UndoNative(target, Clss(target)) + "（托管层无 Undo 方法）";
                }
                return "未识别 vk=" + VkName(vk);
            }

            // 托管层拿不到控件对象（ComboBox 的内部编辑框、第三方编辑框）⇒ 直接按窗口类发原生消息。
            string cls = Clss(target);
            switch (vk)
            {
                case 0x43: SendMessageW(target, WM_COPY, IntPtr.Zero, IntPtr.Zero); return "原生 WM_COPY(" + cls + ")";
                case 0x58: SendMessageW(target, WM_CUT, IntPtr.Zero, IntPtr.Zero); return "原生 WM_CUT(" + cls + ")";
                case 0x56: SendMessageW(target, WM_PASTE, IntPtr.Zero, IntPtr.Zero); return "原生 WM_PASTE(" + cls + ")";
                case 0x41: SendMessageW(target, EM_SETSEL, IntPtr.Zero, (IntPtr)(-1)); return "原生 EM_SETSEL(0,-1)(" + cls + ")";
                case 0x5A: return UndoNative(target, cls);
            }
            return "未识别 vk=" + VkName(vk);
        }

        private static string UndoNative(IntPtr target, string cls)
        {
            // EDIT 与 RICHEDIT 的撤销号不通用：EDIT = 0x00C7，RICHEDIT = WM_USER+84 = 0x0454。
            bool rich = cls != null && cls.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase);
            uint msg = rich ? (uint)EM_UNDO_RICH : (uint)EM_UNDO_EDIT;
            SendMessageW(target, msg, IntPtr.Zero, (IntPtr)(-1));
            return (rich ? "原生 EM_UNDO(RICHEDIT 0x0454)" : "原生 EM_UNDO(EDIT 0x00C7)");
        }

        private static void Emit(Action<string> sink, string text)
        {
            if (sink == null) return;
            try { sink(text); } catch { /* 日志口不能把按键流程带崩 */ }
        }

        private static string Hex(IntPtr h) { return "0x" + ((long)h).ToString("X"); }

        private static string Clss(IntPtr h)
        {
            if (h == IntPtr.Zero) return "-";
            StringBuilder b = new StringBuilder(128);
            int n = GetClassNameW(h, b, b.Capacity);
            return n > 0 ? b.ToString(0, n) : "?";
        }

        private static string Text(IntPtr h)
        {
            if (h == IntPtr.Zero) return "-";
            StringBuilder b = new StringBuilder(64);
            int n = GetWindowTextW(h, b, b.Capacity);
            return n > 0 ? b.ToString(0, n) : "";
        }

        public static string VkName(uint vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk == VK_CONTROL) return "CTRL";
            return "0x" + vk.ToString("X2");
        }
    }
}
