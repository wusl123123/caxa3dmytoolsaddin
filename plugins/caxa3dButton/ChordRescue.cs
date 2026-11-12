using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace caxa3dButton
{
    /// <summary>
    /// 键盘归属抢救：让插件自己窗口里的文本框重新能用 Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+A / Ctrl+Z。
    ///
    /// 机理（实机定案）：CAXA 是 MFC/BCGP 程序，CWinThread::PumpMessage 在 DispatchMessage 之前
    /// 先跑 PreTranslateMessage，其中 TranslateAccelerator 用宿主加速键表把 Ctrl+C/V/X/A/Z 整条吃掉，
    /// 于是 WinForms 的 ProcessCmdKey / KeyPreview / KeyDown / IMessageFilter 结构性不触发。
    /// 唯一实测有效通道 = WH_GETMESSAGE 线程钩子：在消息出队那一刻拿到原始 MSG，
    /// 沿 GetParent 链确认目标在本窗口子树（禁用 IsChild），把 msg.message 改成 WM_NULL 吞掉宿主加速键，
    /// 再 PostMessage(WM_APP_CHORD) 给本窗口，由 WndProc 用 TextBoxBase 或原生消息落地。
    ///
    /// 开关（%TEMP% 空文件，存在才生效）：
    ///   caxa3dButton.nokeyrescue —— 存在 ⇒ 整体关闭（不装钩子）
    ///   caxa3dButton.keyprobe    —— 存在 ⇒ 额外记录非目标按键
    ///
    /// 铁律：只在 Activated 装、Deactivate/FormClosed 拆；回调内不碰 COM、不阻塞、不弹框、不抛异常；
    /// 委托存静态字段防 GC；一次按键钩子被调 3 次（2× PM_NOREMOVE + 1× PM_REMOVE）⇒ 只在 REMOVE 动手。
    /// </summary>
    internal static class ChordRescue
    {
        private const string MarkerOff = "caxa3dButton.nokeyrescue";
        private const string MarkerProbe = "caxa3dButton.keyprobe";

        private const int WH_GETMESSAGE = 3;
        private const int WM_NULL = 0x0000;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_COPY = 0x0003;
        private const int WM_CUT = 0x0300;
        private const int WM_PASTE = 0x0302;
        private const int EM_SETSEL = 0x00B1;         // EDIT：wParam=0, lParam=-1 ⇒ 全选
        private const int EM_UNDO_EDIT = 0x00C7;      // EDIT 的撤销
        private const int EM_UNDO_RICH = 0x0454;      // RICHEDIT 的撤销 = WM_USER + 84
        private const int PM_NOREMOVE = 0;
        private const int PM_REMOVE = 1;
        private const int VK_CONTROL = 0x11;
        private const int MAX_TREE_DEPTH = 64;

        /// <summary>转发给宿主窗口 WndProc 的私有消息：wParam = VK 码，lParam = 目标编辑框句柄。</summary>
        public const int WM_APP_CHORD = 0x400 + 71;   // 沿用已实机验证的同号常量

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

        /// <summary>钩子当前是否装着。</summary>
        public static bool IsHooked { get { return _hook != IntPtr.Zero; } }

        /// <summary>当前钩子服务的窗口（null = 没装）。</summary>
        public static Form CurrentOwner { get { return _ownerForm; } }

        /// <summary>在窗口的 OnActivated 里调用（不在 Initialize / InitSelf 里调）。</summary>
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

            uint pid;
            uint winTid = GetWindowThreadProcessId(_owner, out pid);
            uint osTid = GetCurrentThreadId();
            _proc = HookCallback;
            _hook = SetWindowsHookExW(WH_GETMESSAGE, _proc, IntPtr.Zero, winTid);
            Emit(log, "ChordRescue.Install: rescue=Y verbose=" + _verbose +
                " 窗口=" + owner.GetType().Name + "/" + Hex(_owner) +
                " hook=" + (_hook == IntPtr.Zero ? ("FAIL gle=" + Marshal.GetLastWin32Error()) : "OK") +
                " 窗口线程=" + winTid + " 调用线程=" + osTid);
            return _hook != IntPtr.Zero;
        }

        /// <summary>在窗口的 OnDeactivate 与 OnFormClosed 里调用；只拆「当前属主」，避免两个窗口互拆。</summary>
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
            catch { /* 钩子内不许抛 */ }
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

        private static bool ReportAndSwallow(ref MSG m, uint vk, IntPtr wParam)
        {
            int pm = wParam.ToInt32();
            if (pm != PM_REMOVE) { _noremoveSeen++; return false; }   // NOREMOVE 那份只计数，不动结构体

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

        /// <summary>在窗口的 WndProc 里调用。</summary>
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
            bool rich = cls != null && cls.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase);
            uint msg = rich ? (uint)EM_UNDO_RICH : (uint)EM_UNDO_EDIT;
            SendMessageW(target, msg, IntPtr.Zero, (IntPtr)(-1));
            return (rich ? "原生 EM_UNDO(RICHEDIT 0x0454)" : "原生 EM_UNDO(EDIT 0x00C7)");
        }

        private static void Emit(Action<string> sink, string text)
        {
            if (sink == null) return;
            try { sink(text); } catch { }
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
