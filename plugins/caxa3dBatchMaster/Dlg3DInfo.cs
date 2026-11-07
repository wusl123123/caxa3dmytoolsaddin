using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dBatchMaster
{
    /// <summary>
    /// 「设置3D信息」模态对话框的自动填写器（按 2026-10-26 08:24 实测控件树定制）。
    ///
    /// 实测控件树（hwnd 由 PopupAutoYes 扫描发现，#32770，标题「设置3D信息」）：
    ///   Static    「文件」
    ///   ComboBox  id=2317  「C:\...\xxx.ics」        ← 3D 源文件（只读，不碰）
    ///   Edit      id=1001                            ← 上面那个的编辑子控件
    ///   Static    「配置」
    ///   ComboBox  id=4297  「展开SC08020102.01-06 折弯板」  ← **配置下拉**（要选「缺省/默认」）
    ///   Button    id=-1    「自定义设置」            ← 分组框
    ///   Button    id=4755  「默认」                  ← **单选，要选中**
    ///   Button    id=4756  「自定义」                ← 单选（不选）
    ///   SysTreeView32 id=4300 / Button id=4757「选择」 / Static id=4758「选中对象ID: 69」
    ///   Button    id=1     「确定(&O)」
    ///   Button    id=2     「取消(&C)」
    ///
    /// 流程（严格对齐手工操作：配置选缺省 → 自定义设置勾默认 → 确定）：
    ///   ① 选「配置」下拉里的 缺省/默认 项（`CB_SETCURSEL` + 给父窗口补 `CBN_SELCHANGE`）；
    ///   ② 选中「默认」单选（`BM_CLICK`）；
    ///   ③ 点「确定」；④ 没关就再点一次；⑤ 仍不关则放弃并留日志。
    ///
    /// 注意：**不再“先盲点确定”**。上一版先点确定，对话框 240ms 就关了，
    /// 但配置/自定义设置一个都没改（08:24:36 TB.diff changed=0）。
    /// </summary>
    [System.Runtime.InteropServices.ComVisible(false)]
    internal static class Dlg3DInfo
    {
        public const string TitleKey = "设置3D信息";

        // 实测控件 ID（找不到时退回 class/style 启发式）
        private const int IdConfigCombo = 4297;
        private const int IdFileCombo   = 2317;
        private const int IdDefaultRadio = 4755;
        private const int IdCustomRadio  = 4756;
        private const int IdOk = 1;
        private const int IdCancel = 2;

        // 「配置」下拉目标项的打分表：名字 → 分值（越高越优先）
        private static readonly string[] ConfigExact = { "缺省", "默认", "default" };
        private static readonly string[] ConfigLoose = { "缺省", "默认", "default" };

        private const uint WM_COMMAND   = 0x0111;
        private const uint CB_GETCOUNT  = 0x0146;
        private const uint CB_GETCURSEL = 0x0147;
        private const uint CB_GETLBTEXT = 0x0148;
        private const uint CB_SETCURSEL = 0x014E;
        private const uint BM_GETCHECK  = 0x00F0;
        private const uint BM_CLICK     = 0x00F5;
        private const int  CBN_SELCHANGE = 1;
        private const int  GWL_STYLE = -16;

        private static IntPtr _dlg = IntPtr.Zero;
        private static int _step;
        private static int _stepTick;
        private static bool _treeDumped;
        private static bool _gaveUp;
        private static bool _configOk;     // 配置下拉已指到 缺省/默认
        private static string _configNow = "";

        private static int _confirmCount;  // 对话框被关闭过几次（点了确定之后）
        private static int _appliedCount;  // 完整走完 配置→默认→确定 并被关闭的次数
        public static int ConfirmCount { get { return Volatile.Read(ref _confirmCount); } }
        public static int AppliedCount { get { return Volatile.Read(ref _appliedCount); } }

        public static bool IsMine(string title)
        {
            return !string.IsNullOrEmpty(title) && title.IndexOf(TitleKey, StringComparison.Ordinal) >= 0;
        }

        /// <summary>每次扫描都调用：上一轮接管的对话框是否已消失。</summary>
        public static void Sweep()
        {
            IntPtr dlg = _dlg;
            if (dlg == IntPtr.Zero || IsWindow(dlg)) return;

            int steps = _step;
            bool configOk = _configOk;
            _dlg = IntPtr.Zero;
            _step = 0;

            if (steps >= 1)
            {
                Interlocked.Increment(ref _confirmCount);
                if (steps >= 3 && configOk)
                {
                    Interlocked.Increment(ref _appliedCount);
                    Diagnostics.Log("Dlg3DInfo: 对话框已关闭，配置=" + _configNow + "、默认=已选 → 记一次完整填写成功");
                }
                else
                {
                    Diagnostics.Log("Dlg3DInfo: 对话框已关闭（step=" + steps + "，配置=" + _configNow +
                                    "）→ 视为未完整填写");
                }
            }
        }

        public static void Begin(IntPtr dlg)
        {
            if (_dlg == dlg) return;
            _dlg = dlg;
            _step = 0;
            _stepTick = Environment.TickCount;
            _treeDumped = false;
            _gaveUp = false;
            _configOk = false;
            _configNow = "";
            Diagnostics.Log("Dlg3DInfo: 接管「" + TitleKey + "」对话框 hwnd=0x" + dlg.ToInt64().ToString("X"));
        }

        public static void Tick(IntPtr dlg)
        {
            Begin(dlg);
            if (_gaveUp || !IsWindow(dlg)) return;

            // 任何一步出错都不许把扫描线程拖垮（08:38 那次 P/Invoke 抛异常 →
            // 120ms 一次、828 条异常，对话框一个动作也没做）。出错只记一次并放弃本对话框。
            try
            {
                TickCore(dlg);
            }
            catch (Exception ex)
            {
                _gaveUp = true;
                Diagnostics.Log("Dlg3DInfo: 自动填写异常，停止处理本对话框：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void TickCore(IntPtr dlg)
        {
            if (!_treeDumped)
            {
                _treeDumped = true;
                DumpTree(dlg);
            }

            int now = Environment.TickCount;
            switch (_step)
            {
                case 0:
                    SelectConfig(dlg);
                    Step(1);
                    break;

                case 1:
                    if (now - _stepTick < 150) return;
                    if (!IsWindow(dlg)) return;
                    CheckDefaultRadio(dlg);
                    Step(2);
                    break;

                case 2:
                    if (now - _stepTick < 150) return;
                    if (!IsWindow(dlg)) return;
                    ClickOk(dlg, "填写完成→确定");
                    Step(3);
                    break;

                case 3:
                    if (now - _stepTick < 800) return;
                    if (!IsWindow(dlg)) return;
                    ClickOk(dlg, "再点一次确定");
                    Step(4);
                    break;

                case 4:
                    if (now - _stepTick < 1500) return;
                    if (!IsWindow(dlg)) return;
                    _gaveUp = true;
                    Diagnostics.Log("Dlg3DInfo: 两次点确定后对话框仍未关闭 → 停止自动填写，请人工处理（控件树见上）");
                    break;
            }
        }

        private static void Step(int s)
        {
            _step = s;
            _stepTick = Environment.TickCount;
        }

        // ================================================================ ① 配置下拉

        private static void SelectConfig(IntPtr dlg)
        {
            var all = Enumerate(dlg);
            IntPtr combo = IntPtr.Zero;

            // 优先按实测 ID；否则取“下拉列表式”ComboBox（CBS_DROPDOWNLIST=3），排除文件选择框
            foreach (var c in all)
                if (c.Hwnd != IntPtr.Zero && c.Id == IdConfigCombo && c.Cls == "ComboBox") { combo = c.Hwnd; break; }
            if (combo == IntPtr.Zero)
            {
                foreach (var c in all)
                {
                    if (c.Cls != "ComboBox" || c.Id == IdFileCombo) continue;
                    if ((c.Style & 0xF) != 3) continue;      // 只认 CBS_DROPDOWNLIST
                    combo = c.Hwnd;
                    break;
                }
            }
            if (combo == IntPtr.Zero)
            {
                Diagnostics.Log("Dlg3DInfo: 没找到「配置」下拉（ComboBox）");
                return;
            }

            int count = (int)SendMessage(combo, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
            int cur   = (int)SendMessage(combo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
            var items = new List<string>();
            int best = -1, bestScore = 0;

            for (int i = 0; i < count && i < 200; i++)
            {
                string text = ComboItemText(combo, i);
                items.Add(text);
                int score = ScoreItem(text);
                if (score > bestScore) { bestScore = score; best = i; }
            }

            Diagnostics.Log("Dlg3DInfo: 配置下拉共 " + count + " 项，当前=" + cur +
                            "，候选=[" + string.Join(" | ", items.ToArray()) + "]");

            if (cur >= 0 && cur < items.Count) { _configNow = items[cur]; _configOk = ScoreItem(_configNow) > 0; }

            if (best < 0)
            {
                // 回退：下拉里没有 缺省/默认/default（实测：钣金件 -01 的 ics 无命名配置，
                // 下拉只有「全部零件」且 cur=-1）。此时不选直接点确定 = 无效操作，CAXA 弹提示、
                // 什么都不写 → CRX 侧 TB.diff 永远 changed=0 → 无限重试卡死（2026-10-27 07:26 实测）。
                // 策略：优先选含「全部」的项，否则选第 0 项 —— 对无命名配置的零件这就是正确选择。
                int fb = -1;
                for (int i = 0; i < items.Count; i++)
                    if (Normalize(items[i]).IndexOf("全部", StringComparison.Ordinal) >= 0) { fb = i; break; }
                if (fb < 0 && items.Count > 0) fb = 0;

                if (fb < 0)
                {
                    Diagnostics.Log("Dlg3DInfo: 配置下拉为空，无可选项 → 保持不改动");
                    return;
                }
                if (fb == cur)
                {
                    _configOk = true;
                    Diagnostics.Log("Dlg3DInfo: 下拉无 缺省/默认 项，当前已是「" + items[cur] + "」（回退接受）");
                    return;
                }

                SendMessage(combo, CB_SETCURSEL, (IntPtr)fb, IntPtr.Zero);
                IntPtr fbParent = GetParent(combo);
                int fbId = GetDlgCtrlID(combo);
                SendMessage(fbParent, WM_COMMAND, (IntPtr)((CBN_SELCHANGE << 16) | (fbId & 0xFFFF)), combo);

                _configNow = items[fb];
                _configOk = true;
                Diagnostics.Log("Dlg3DInfo: 下拉无 缺省/默认 项 → 回退选第 " + fb + " 项「" + items[fb] +
                                "」(" + cur + " → " + fb + "，已补发 CBN_SELCHANGE）");
                return;
            }
            if (best == cur)
            {
                _configOk = true;
                Diagnostics.Log("Dlg3DInfo: 配置已是第 " + cur + " 项「" + items[cur] + "」，无需切换");
                return;
            }

            SendMessage(combo, CB_SETCURSEL, (IntPtr)best, IntPtr.Zero);
            IntPtr parent = GetParent(combo);
            int id = GetDlgCtrlID(combo);
            SendMessage(parent, WM_COMMAND, (IntPtr)((CBN_SELCHANGE << 16) | (id & 0xFFFF)), combo);

            _configNow = items[best];
            _configOk = true;
            Diagnostics.Log("Dlg3DInfo: 配置下拉切换 " + cur + " → " + best + "「" + items[best] + "」（已补发 CBN_SELCHANGE）");
        }

        /// <summary>给下拉项打分：精确命中 缺省/默认/default 最高，包含次之。</summary>
        private static int ScoreItem(string text)
        {
            string norm = Normalize(text);
            if (norm.Length == 0) return 0;
            for (int i = 0; i < ConfigExact.Length; i++)
                if (norm == Normalize(ConfigExact[i])) return 100 - i;
            for (int i = 0; i < ConfigLoose.Length; i++)
                if (norm.IndexOf(Normalize(ConfigLoose[i]), StringComparison.Ordinal) >= 0) return 50 - i;
            return 0;
        }

        // ================================================================ ② 自定义设置 → 默认

        private static void CheckDefaultRadio(IntPtr dlg)
        {
            var all = Enumerate(dlg);
            IntPtr radio = IntPtr.Zero;
            string text = "";

            foreach (var c in all)
                if (c.Cls == "Button" && c.Id == IdDefaultRadio) { radio = c.Hwnd; text = c.Text; break; }

            if (radio == IntPtr.Zero)
            {
                foreach (var c in all)
                {
                    if (c.Cls != "Button") continue;
                    if (c.Id == IdOk || c.Id == IdCancel || c.Id == IdCustomRadio) continue;
                    if (!IsRadioOrCheck(c)) continue;
                    if (Normalize(c.Text) != Normalize("默认")) continue;
                    radio = c.Hwnd; text = c.Text; break;
                }
            }
            if (radio == IntPtr.Zero)
            {
                Diagnostics.Log("Dlg3DInfo: 没找到「默认」单选（id=4755）");
                return;
            }

            int state = (int)SendMessage(radio, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
            if (state != 0)
            {
                Diagnostics.Log("Dlg3DInfo: 「" + text + "」已是选中状态");
                return;
            }
            PostMessage(radio, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            Diagnostics.Log("Dlg3DInfo: 选中「" + text + "」id=" + GetDlgCtrlID(radio));
        }

        // ================================================================ ③ 确定

        private static void ClickOk(IntPtr dlg, string why)
        {
            var all = Enumerate(dlg);
            IntPtr btn = IntPtr.Zero;
            string text = "";

            foreach (var c in all)
                if (c.Cls == "Button" && c.Id == IdOk) { btn = c.Hwnd; text = c.Text; break; }

            if (btn == IntPtr.Zero)
            {
                foreach (var c in all)
                {
                    if (c.Cls != "Button" || c.Id == IdCancel || !c.Visible || !c.Enabled) continue;
                    if (IsRadioOrCheck(c)) continue;
                    string norm = Normalize(c.Text);
                    if (norm.Length == 0) continue;
                    if (norm.StartsWith("确定", StringComparison.Ordinal) || norm == "ok" ||
                        norm.StartsWith("确认", StringComparison.Ordinal) || norm == "应用")
                    { btn = c.Hwnd; text = c.Text; break; }
                }
            }
            if (btn == IntPtr.Zero)
            {
                Diagnostics.Log("Dlg3DInfo: " + why + " → 没找到确定按钮");
                return;
            }
            PostMessage(btn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            Diagnostics.Log("Dlg3DInfo: " + why + " → 点击「" + text + "」id=" + GetDlgCtrlID(btn));
        }

        // ================================================================ 枚举 / 日志

        private class Ctl
        {
            public IntPtr Hwnd;
            public string Cls;
            public string Text;
            public int Id;
            public long Style;
            public bool Visible;
            public bool Enabled;
        }

        private static bool IsRadioOrCheck(Ctl c)
        {
            long t = c.Style & 0xF;   // BS_TYPEMASK
            return t == 2 || t == 3 || t == 4 || t == 5 || t == 9 || t == 11;
        }

        private static List<Ctl> Enumerate(IntPtr dlg)
        {
            var list = new List<Ctl>();
            EnumChildWindows(dlg, (h, l) =>
            {
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, sb.Capacity);
                list.Add(new Ctl
                {
                    Hwnd = h,
                    Cls = GetClassW(h),
                    Text = sb.ToString(),
                    Id = GetDlgCtrlID(h),
                    Style = GetWindowLongPtr(h, GWL_STYLE).ToInt64(),
                    Visible = IsWindowVisible(h),
                    Enabled = IsWindowEnabled(h)
                });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static void DumpTree(IntPtr dlg)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(dlg, sb, sb.Capacity);
            Diagnostics.Trace("Dlg3DInfo: ===== 控件树 title=「" + sb + "」=====");
            foreach (var c in Enumerate(dlg))
            {
                Diagnostics.Trace(string.Format(
                    "Dlg3DInfo: ctl class={0} id={1} style=0x{2:X} vis={3} en={4} radio/check={5} text=「{6}」",
                    c.Cls, c.Id, c.Style, c.Visible ? 1 : 0, c.Enabled ? 1 : 0,
                    IsRadioOrCheck(c) ? 1 : 0, c.Text));
            }
            Diagnostics.Trace("Dlg3DInfo: ===== 控件树结束 =====");
        }

        private static string Normalize(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch == '&' || ch == ' ' || ch == '\t' || ch == '\u3000') continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        private static string ComboItemText(IntPtr combo, int index)
        {
            var sb = new StringBuilder(512);
            SendMessageStr(combo, CB_GETLBTEXT, (IntPtr)index, sb);
            return sb.ToString();
        }

        // ================================================================ Win32

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        // 注意：必须显式写 EntryPoint。user32 里只有 SendMessageW/A，没有叫 SendMessageStr 的导出；
        // 不写 EntryPoint 时 .NET 只会把 CharSet 追加成 "SendMessageStrW" 去查 —— 每次扫描都抛
        // EntryPointNotFoundException，对话框因此一个动作都没做（08:38 那次 828 条异常就是这么来的）。
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageStr(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private static string GetClassW(IntPtr h)
        {
            var sb = new StringBuilder(128);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
