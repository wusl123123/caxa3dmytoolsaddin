// 宿主「插入图片」文件对话框代点器（2026-11-15 移植自 caxa3dqrcode\NativeBitmapInsert.cs，逻辑一字不改、仅换命名空间）。
// 07:48 用户改线：弃 QR 命令键入喂点，走 CX_DFT_InsertBitmap 插图。
//
// 链路：C# 桥投 BITMAP_NATIVE → CRX sendStringToExecute("CX_DFT_InsertBitmap ")
//   → 宿主弹「插入图片」流程（预期：文件选择框 → 可能的参数框 → 取点提示）
//   → 本类后台线程代填 PNG 路径并点「打开」，其余对话框只 dump 控件树取证、不代点。
//
// 纪律（全部沿用 NativeQrDialog 已实机验证的手法，见其文件头）：
//   ① 后台线程绝不碰 WinForms 控件；日志走线程安全队列由主线程 DrainLogs；
//   ② 剪贴板一律原生 Win32（CF_UNICODETEXT + CF_TEXT/GBK），输入用 WM_PASTE（触发
//      EN_CHANGE，等价人工 Ctrl+V，不需要焦点）；WM_SETTEXT 只作兜底；
//   ③ 命中对话框先完整 dump 控件树再动手 —— 本轮命令的对话框结构未知，取证优先；
//   ④ 点「打开」按控件 ID 发 WM_COMMAND + BM_CLICK 双通道，不按像素不猜坐标；
//   ⑤ 全部动作只标「已发出 attempt」，成败唯一判据 = CRX QR_STATE 的 delta>0 + SAVE 后
//      exb 文件变化。
//   ⑥ 第二段「图像」参数框：插入点 X/Y、比例因子自动 WM_SETTEXT 填入 + 取消「在屏幕上指定」
//      + 代点「确定」（08:14/08:56/09:40 实机 verified）；键入坐标取点通道 07:48 已判死。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    internal static class NativeBitmapInsert
    {
        private const int WM_SETTEXT = 0x000C;
        private const int WM_COMMAND = 0x0111;
        private const int BM_CLICK = 0x00F5;
        private const int BM_GETCHECK = 0x00F0;
        private const int BM_SETCHECK = 0x00F1;
        private const int EM_SETSEL = 0x00B1;
        private const int WM_CLEAR = 0x0303;
        private const int WM_PASTE = 0x0302;
        private const int BN_CLICKED = 0;
        private const uint GW_OWNER = 4;
        private const uint CF_UNICODETEXT = 13;
        private const uint CF_TEXT = 1;
        private const uint GMEM_MOVEABLE = 0x0002;

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessagePtr(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr h);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint fmt);
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint fmt);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint fmt, IntPtr mem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, IntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private sealed class Ctl
        {
            public IntPtr H; public string Cls; public string Text; public int Id;
            public int Left, Top, Right, Bottom;
        }

        private static readonly object _logLock = new object();
        private static readonly List<string> _logQueue = new List<string>();
        private static Thread _th;
        private static volatile bool _run;
        private static volatile int _epoch;
        private static string _filePath;
        private static string _insertPoint;
        private static double _imgScale;
        private static Func<bool> _stop;

        public static volatile bool Found;      // 命中文件选择对话框
        public static volatile bool Filled;     // 路径已填入且回读一致
        public static volatile bool Opened;     // 已点「打开」
        public static volatile bool ImageDlgFound;   // 命中宿主「图像」参数框
        public static volatile bool PointFilled;     // 插入点 X/Y 已填入且回读一致
        public static volatile bool ScreenPickUnchecked; // 插入点「在屏幕上指定」已取消勾选
        public static volatile bool ScaleFilled;     // 比例因子已填入且回读一致
        public static volatile bool ScaleUnchecked;  // 比例组「在屏幕上指定」已确认未勾选/已取消
        public static volatile bool OkClicked;       // 已点「确定」
        public static volatile bool Done;       // 后台 Loop 已结束（含取证观察窗）
        public static volatile string LastError = "";
        /// <summary>点「打开」后 20 s 内新出现的对话框（取证：可能是插入参数框），句柄 → 控件树文本。</summary>
        public static readonly Dictionary<string, string> FollowUpDialogs = new Dictionary<string, string>();

        private static void Q(string s)
        {
            lock (_logLock) _logQueue.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " bmp-dlg: " + s);
        }

        public static void DrainLogs(Action<string> log)
        {
            string[] arr;
            lock (_logLock) { arr = _logQueue.ToArray(); _logQueue.Clear(); }
            foreach (string s in arr) { try { log(s); } catch { } }
        }

        public static void Start(string pngPath, string insertPoint, double imgScale,
            Action<string> log, Func<bool> stop)
        {
            Finish();
            lock (_logLock) _logQueue.Clear();
            Found = Filled = Opened = ImageDlgFound = PointFilled = ScreenPickUnchecked =
                ScaleFilled = ScaleUnchecked = OkClicked = Done = false;
            LastError = "";
            FollowUpDialogs.Clear();
            _filePath = pngPath ?? "";
            _insertPoint = insertPoint ?? "";
            _imgScale = imgScale;
            _stop = stop;
            _epoch++;
            _run = true;
            Q("代点器启动；目标=文件选择对话框（按「Edit+打开按钮」结构命中），png=" + _filePath +
              "；插入点=" + _insertPoint + "、比例因子=" + imgScale.ToString("0.####",
                  System.Globalization.CultureInfo.InvariantCulture) +
              "（第二段填入「图像」参数框）");
            _th = new Thread(Loop);
            _th.IsBackground = true;
            _th.Name = "NativeBitmapInsert";
            _th.Start();
            DrainLogs(log);
        }

        public static void Finish()
        {
            _run = false;
            _th = null;   // 不 Join：模态框占主线程时 Join 会死锁；线程是 background
        }

        private static bool Stopping()
        {
            try { return _stop != null && _stop(); } catch { return false; }
        }

        private static string ReadText(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            var sb = new StringBuilder(1024);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static void Loop()
        {
            try { LoopBody(); }
            finally { _run = false; Done = true; }
        }

        private static void LoopBody()
        {
            int myEpoch = _epoch;
            var sw = Stopwatch.StartNew();

            // ① 等文件选择对话框：结构判据 = 可见 #32770 + 有 Edit 子窗口 + 有「打开」按钮。
            //    标题不猜（可能是「打开图片/打开/选择图片文件」），只按结构命中并 dump 全树。
            IntPtr dlg = IntPtr.Zero;
            List<Ctl> ctls = null;
            while (_run && myEpoch == _epoch && !Stopping() && sw.ElapsedMilliseconds < 45000)
            {
                IntPtr hit;
                List<Ctl> hitCs;
                if (FindFileDialog(out hit, out hitCs)) { dlg = hit; ctls = hitCs; break; }
                Thread.Sleep(120);
            }
            if (dlg == IntPtr.Zero)
            {
                LastError = "45 s 内没有出现「Edit+打开按钮」结构的可见 #32770 文件对话框" +
                            "（命令可能没弹文件框——直接走命令行取点/参数流程，看 bmp-dlg 与 CRX 日志取证）";
                Q(LastError);
                SnapshotFollowUps(sw, myEpoch);
                return;
            }
            Found = true;
            Q("命中文件对话框 hwnd=0x" + dlg.ToInt64().ToString("X") +
              " 标题=\"" + ReadText(dlg) + "\"；控件树共 " + ctls.Count + " 项 ↓↓↓");
            foreach (Ctl c in ctls)
                Q("  id=" + c.Id + " cls=" + c.Cls + " text=\"" + c.Text + "\" rect=(" +
                  c.Left + "," + c.Top + "," + c.Right + "," + c.Bottom + ") hwnd=0x" + c.H.ToInt64().ToString("X"));

            // ② 路径 Edit：标准文件框是「Edit/ComboBox 内 Edit」；优先取最宽的可见 Edit
            IntPtr edit = FindPathEdit(ctls);
            if (edit == IntPtr.Zero)
            {
                LastError = "控件树里没找到可填路径的 Edit";
                Q(LastError);
                ClickCancel(dlg, ctls);
                WaitClosed(dlg, 8000);
                return;
            }
            Q("路径 Edit hwnd=0x" + edit.ToInt64().ToString("X") + " id=" + GetDlgCtrlID(edit) +
              " 当前文本=\"" + ReadText(edit) + "\"");

            // ③ 剪贴板置 PNG 路径 → WM_PASTE（触发 EN_CHANGE，等价人工 Ctrl+V）
            string savedClip = null;
            if (OpenClipboardRetry())
            {
                try { savedClip = ReadClipboardText(); } finally { CloseClipboard(); }
            }
            string clipErr;
            bool clipOk = PutClipboardText(_filePath, out clipErr);
            Q("剪贴板置 png 路径=" + clipOk + " " + clipErr);

            SendMessagePtr(edit, EM_SETSEL, IntPtr.Zero, new IntPtr(-1));
            SendMessagePtr(edit, WM_CLEAR, IntPtr.Zero, IntPtr.Zero);
            SendMessagePtr(edit, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
            string editText = ReadText(edit);
            if (editText.IndexOf(_filePath, StringComparison.Ordinal) < 0)
            {
                SendMessage(edit, WM_SETTEXT, IntPtr.Zero, _filePath);
                editText = ReadText(edit);
                Q("WM_PASTE 未进，兜底 WM_SETTEXT 回读=\"" + editText + "\"");
            }
            Filled = editText.IndexOf(_filePath, StringComparison.Ordinal) >= 0;
            Q("填路径结论 filled=" + Filled + " 回读=\"" + editText + "\"");

            // ④ 恢复用户剪贴板
            if (savedClip != null)
            {
                string restoreErr;
                if (!PutClipboardText(savedClip, out restoreErr)) Q("剪贴板恢复失败（非致命）：" + restoreErr);
            }

            // ⑤ 点「打开」：优先文本含「打开」的按钮，其次默认按钮 id=1；WM_COMMAND + BM_CLICK 双通道
            Ctl btn = FindOpenButton(ctls);
            if (btn == null)
            {
                LastError = "没找到「打开」按钮（控件树已 dump）";
                Q(LastError);
                ClickCancel(dlg, ctls);
                WaitClosed(dlg, 8000);
                return;
            }
            PostMessage(dlg, WM_COMMAND, new IntPtr(btn.Id & 0xFFFF), btn.H);
            PostMessage(btn.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            Opened = true;
            Q("已点「" + btn.Text + "」 id=" + btn.Id + " hwnd=0x" + btn.H.ToInt64().ToString("X") + "（双通道）");

            // ⑥ 等文件框消失 → 第二段：宿主「图像」参数框（08:01 实机取证标题="图像"，
            //    含 插入点 X/Y / 旋转 角度 / 比例 三组 + 确定/取消）——填插入点、
            //    取消「在屏幕上指定」、点「确定」，让图片落到指定坐标，不再人工取点。
            WaitClosed(dlg, 30000);
            Q("文件框 closed=" + (!IsWindow(dlg) || !IsWindowVisible(dlg)) + "（耗时 " + sw.ElapsedMilliseconds + " ms）");
            Phase2ImageDialog(myEpoch, sw);
            SnapshotFollowUps(sw, myEpoch);
        }

        /// <summary>
        /// 第二段：宿主「图像」参数框。08:01 实机控件树（id 按实机，位置按几何就近判）：
        ///   插入点组 = 组框「插入点」(id=-1) + 复选「在屏幕上指定」(id=1908) + X Edit(id=1644) + Y Edit(id=1633)
        ///   旋转组 = 「在屏幕上指定」(id=1924) + 角度 Edit(id=1642)；比例组 = (id=1915) + Edit(id=1623)
        ///   确定 id=1 / 取消 id=2；嵌入图片到文件中 id=2075（默认勾选，不动）。
        /// X/Y 是普通数字 Edit，宿主点「确定」时按 ID 读文本（无 EN_CHANGE 语义）⇒ WM_SETTEXT 直填。
        /// </summary>
        private static void Phase2ImageDialog(int myEpoch, Stopwatch sw)
        {
            long deadline = sw.ElapsedMilliseconds + 30000;
            IntPtr dlg = IntPtr.Zero;
            List<Ctl> ctls = null;
            while (_run && myEpoch == _epoch && !Stopping() && sw.ElapsedMilliseconds < deadline)
            {
                IntPtr hit; List<Ctl> hitCs;
                if (FindImageDialog(out hit, out hitCs)) { dlg = hit; ctls = hitCs; break; }
                Thread.Sleep(150);
            }
            if (dlg == IntPtr.Zero)
            {
                Q("第二段：30 s 内未出现「图像」参数框（可能宿主直接取点/已插完，看后续取证）");
                return;
            }
            ImageDlgFound = true;
            Q("命中「图像」参数框 hwnd=0x" + dlg.ToInt64().ToString("X") +
              "；控件树共 " + ctls.Count + " 项 ↓↓↓");
            foreach (Ctl c in ctls)
                Q("  id=" + c.Id + " cls=" + c.Cls + " text=\"" + c.Text + "\" rect=(" +
                  c.Left + "," + c.Top + "," + c.Right + "," + c.Bottom + ") hwnd=0x" + c.H.ToInt64().ToString("X"));

            // ①''' 11-15 实机坑（重跑同名场景）取证 + 兜底：
            //   (a) id=2072（文件路径 Static）若与本行 PNG 不符 = 宿主没认新文件（重跑特征，插入必卡）；
            //   (b) 「同名图片作为新对象」(id=33814) 勾上 = 同名图片也按新对象插入（本工具语义：永远新对象）。
            foreach (Ctl c in ctls)
            {
                if (c.Id == 2072 && c.Text.Length > 0)
                {
                    bool same = string.Equals(c.Text.Trim(), _filePath != null ? _filePath.Trim() : "",
                                              StringComparison.OrdinalIgnoreCase);
                    if (!same)
                        Q("⚠ 图像框文件路径与本行 PNG 不符：框内=\"" + c.Text + "\" 本行=\"" + _filePath +
                          "\"（宿主未认新文件，重跑同名特征；已勾「同名图片作为新对象」兜底，成败看 QR_STATE）");
                }
                if (c.Text.IndexOf("同名图片作为新对象", StringComparison.Ordinal) >= 0 &&
                    c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    IntPtr st = SendMessagePtr(c.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                    if (st == IntPtr.Zero || (int)st == 0)
                    {
                        PostMessage(c.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                        Thread.Sleep(150);
                        IntPtr st2 = SendMessagePtr(c.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                        Q("「同名图片作为新对象」原未勾选 → 已点选（id=" + c.Id + " 勾选态=" +
                          (st2 != IntPtr.Zero && (int)st2 != 0 ? "已勾" : "未变（第三态）") + "）");
                    }
                    else
                    {
                        Q("「同名图片作为新对象」本就勾选（id=" + c.Id + "）");
                    }
                }
            }

            // ① 解析插件界面「插入点」→ 填 X/Y（按 X/Y Static 几何就近找 Edit，不赌枚举顺序）
            double xs, ys;
            bool ptOk = TryParsePoint(_insertPoint, out xs, out ys);
            if (ptOk)
            {
                IntPtr xe = FindEditNearLabel(ctls, "X");
                IntPtr ye = FindEditNearLabel(ctls, "Y");
                if (xe != IntPtr.Zero && ye != IntPtr.Zero)
                {
                    string xv = FormatNum(xs), yv = FormatNum(ys);
                    SendMessage(xe, WM_SETTEXT, IntPtr.Zero, xv);
                    SendMessage(ye, WM_SETTEXT, IntPtr.Zero, yv);
                    string xr = ReadText(xe), yr = ReadText(ye);
                    PointFilled = xr == xv && yr == yv;
                    Q("插入点 X/Y 填入：" + xv + " / " + yv + " 回读 \"" + xr + "\" / \"" + yr +
                      "\"（WM_SETTEXT；宿主点「确定」时按 ID 读文本）filled=" + PointFilled);
                }
                else
                {
                    Q("⚠ 没找到 X/Y Edit（按 X/Y Static 就近判），跳过填入（第三态），位置交人工");
                }
            }
            else
            {
                Q("插入点「" + _insertPoint + "」解析失败（第三态），X/Y 保持宿主默认，位置交人工");
            }

            // ①' 比例因子填入（11-15 用户需求：图纸比例补偿，如 1:2.5 图纸需求 20x20 ⇒ 落图 50x50）。
            //    比例 Edit 按几何判组（「比例」组框内第一个 Edit），实机 id=1623 兜底。
            IntPtr scaleEdit = FindScaleEdit(ctls);
            if (scaleEdit != IntPtr.Zero)
            {
                string sv = FormatNum(_imgScale);
                SendMessage(scaleEdit, WM_SETTEXT, IntPtr.Zero, sv);
                string sr = ReadText(scaleEdit);
                ScaleFilled = sr == sv;
                Q("比例因子填入：" + sv + " 回读 \"" + sr + "\"（WM_SETTEXT）filled=" + ScaleFilled +
                  " id=" + GetDlgCtrlID(scaleEdit) + " hwnd=0x" + scaleEdit.ToInt64().ToString("X"));
            }
            else
            {
                Q("⚠ 没找到比例 Edit（几何判组+id=1623 兜底都未命中），比例保持宿主默认 1（第三态），大小交人工改");
            }

            // ①'' 比例组若勾着「在屏幕上指定」，比例框不生效且确定后可能要求取点 ⇒ 同法取消（只取消，不勾回）。
            Ctl scaleGrp = FindGroupBox(ctls, "比例");
            Ctl scaleCb = scaleGrp == null ? null : FindCheckInGroup(ctls, scaleGrp, "在屏幕上指定");
            if (scaleCb == null)
            {
                foreach (Ctl c in ctls)
                    if (c.Id == 1915 && c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0) { scaleCb = c; break; }
                if (scaleCb != null) Q("比例组几何判组未命中，按实机 id=1915 兜底命中「在屏幕上指定」");
            }
            if (scaleCb != null)
            {
                IntPtr st = SendMessagePtr(scaleCb.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                if (st != IntPtr.Zero && (int)st != 0)
                {
                    PostMessage(scaleCb.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(200);
                    IntPtr st2 = SendMessagePtr(scaleCb.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                    if (st2 != IntPtr.Zero && (int)st2 != 0)
                    {
                        SendMessagePtr(scaleCb.H, BM_SETCHECK, IntPtr.Zero, IntPtr.Zero);
                        PostMessage(dlg, WM_COMMAND, new IntPtr((BN_CLICKED << 16) | (scaleCb.Id & 0xFFFF)), scaleCb.H);
                    }
                    ScaleUnchecked = true;
                    Q("比例组「在屏幕上指定」原勾选 → 已取消（id=" + scaleCb.Id + "）");
                }
                else
                {
                    ScaleUnchecked = true;
                    Q("比例组「在屏幕上指定」本就未勾选，比例框直接生效");
                }
            }
            else
            {
                Q("⚠ 未找到比例组「在屏幕上指定」复选框（控件树已 dump）；若它仍勾着，比例框不生效");
            }

            // ② 取消「插入点」组内的「在屏幕上指定」勾选（勾着的话宿主点确定后要求屏幕取点，
            //    X/Y 不生效）。三个组都有同名复选框，按「中心点落在插入点组框内」判组。
            Ctl group = FindGroupBox(ctls, "插入点");
            Ctl cb = group == null ? null : FindCheckInGroup(ctls, group, "在屏幕上指定");
            if (cb == null)
            {
                // 实机 id=1908 兜底（几何判组失败的保险）
                foreach (Ctl c in ctls)
                    if (c.Id == 1908 && c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0) { cb = c; break; }
                if (cb != null) Q("几何判组未命中，按实机 id=1908 兜底命中「在屏幕上指定」");
            }
            if (cb != null)
            {
                IntPtr st = SendMessagePtr(cb.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                Q("插入点「在屏幕上指定」当前勾选=" + (st == IntPtr.Zero ? 0 : (int)st) +
                  " id=" + cb.Id + " hwnd=0x" + cb.H.ToInt64().ToString("X"));
                if (st != IntPtr.Zero && (int)st != 0)
                {
                    PostMessage(cb.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(200);
                    IntPtr st2 = SendMessagePtr(cb.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                    if (st2 != IntPtr.Zero && (int)st2 != 0)
                    {
                        SendMessagePtr(cb.H, BM_SETCHECK, IntPtr.Zero, IntPtr.Zero);
                        PostMessage(dlg, WM_COMMAND, new IntPtr((BN_CLICKED << 16) | (cb.Id & 0xFFFF)), cb.H);
                        Thread.Sleep(150);
                        st2 = SendMessagePtr(cb.H, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                    }
                    ScreenPickUnchecked = st2 == IntPtr.Zero || (int)st2 == 0;
                    Q("取消勾选结论 unchecked=" + ScreenPickUnchecked + "（BM_CLICK 优先，BM_SETCHECK+BN_CLICKED 兜底）");
                }
                else
                {
                    ScreenPickUnchecked = true;
                    Q("「在屏幕上指定」本就未勾选，直接用 X/Y");
                }
            }
            else
            {
                Q("⚠ 未找到插入点组「在屏幕上指定」复选框（控件树已 dump）；若它仍勾着，" +
                  "点「确定」后宿主会要求屏幕取点——位置交人工，delta 判据不变");
            }

            // ③ 点「确定」（id=1 / 文本含「确定」）；旋转组保持宿主默认（0°）
            Ctl ok = FindButtonByText(ctls, "确定");
            if (ok == null)
            {
                Q("⚠ 没找到「确定」按钮，参数框留人工处理（不代点取消——宿主可能已在等取点）");
                return;
            }
            PostMessage(dlg, WM_COMMAND, new IntPtr(ok.Id & 0xFFFF), ok.H);
            PostMessage(ok.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            OkClicked = true;
            Q("已点「确定」 id=" + ok.Id + " hwnd=0x" + ok.H.ToInt64().ToString("X") +
              "（pointFilled=" + PointFilled + " unchecked=" + ScreenPickUnchecked +
              " scaleFilled=" + ScaleFilled + " scaleUnchecked=" + ScaleUnchecked + "）");
            WaitClosed(dlg, 30000);
            Q("图像框 closed=" + (!IsWindow(dlg) || !IsWindowVisible(dlg)));
            // ★ 坑 41 防御：图像框 30 s 还没关（宿主拒绝参数/弹了错误框）⇒ 代点关闭。
            //   模态框只要开着，CRX UI 线程就被占住，桥上 PING 全 TIMEOUT，后续一切请求全废。
            if (IsWindow(dlg) && IsWindowVisible(dlg))
            {
                Q("⚠ 图像框 30 s 未关闭（宿主可能拒绝了参数），强制代点关闭（模态框堵桥元凶）");
                ClickCancel(dlg, ctls);
                WaitClosed(dlg, 5000);
            }
        }

        private static bool FindImageDialog(out IntPtr dlg, out List<Ctl> ctlsOut)
        {
            dlg = IntPtr.Zero; ctlsOut = null;
            IntPtr found = IntPtr.Zero;
            List<Ctl> hit = null;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (found != IntPtr.Zero) return false;
                if (!IsWindowVisible(h)) return true;
                var cls = new StringBuilder(128);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "#32770") return true;
                IntPtr owner = GetWindow(h, GW_OWNER);
                if (owner != IntPtr.Zero)
                {
                    var oc = new StringBuilder(128);
                    GetClassName(owner, oc, oc.Capacity);
                    if (oc.ToString().StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase)) return true;
                }
                string title = ReadText(h);
                var cs = ListControls(h);
                bool titled = title.IndexOf("图像", StringComparison.Ordinal) >= 0;
                bool hasInsertGrp = false;
                foreach (Ctl c in cs)
                    if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        CleanButtonText(c.Text) == "插入点") { hasInsertGrp = true; break; }
                // 标题="图像"（实机取证）或结构含「插入点」组框，二者其一命中
                if (!titled && !hasInsertGrp) return true;
                found = h; hit = cs;
                return false;
            }, IntPtr.Zero);
            dlg = found; ctlsOut = hit;
            return found != IntPtr.Zero;
        }

        private static bool TryParsePoint(string s, out double x, out double y)
        {
            x = y = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            return double.TryParse(parts[0].Trim(), out x) && double.TryParse(parts[1].Trim(), out y);
        }

        private static string FormatNum(double v)
        {
            return v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>按「X」/「Y」标签 Static 找同行右侧的 Edit（08:01 实机：标签在 Edit 左侧同一行）。</summary>
        private static IntPtr FindEditNearLabel(List<Ctl> ctls, string label)
        {
            Ctl lab = null;
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Static", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (c.Text.Trim() == label &&
                    (c.Right - c.Left) < 60)   // 排除把长文本误当 X/Y 标签
                { lab = c; break; }
            }
            if (lab == null) return IntPtr.Zero;
            int cy = (lab.Top + lab.Bottom) / 2;
            IntPtr best = IntPtr.Zero; int bestD = int.MaxValue;
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int ec = (c.Top + c.Bottom) / 2;
                int d = Math.Abs(ec - cy);
                if (c.Left >= lab.Right - 30 && d < bestD && d < 30) { best = c.H; bestD = d; }
            }
            return best;
        }

        /// <summary>找比例 Edit：「比例」组框内第一个 Edit 类控件；几何判组失败按实机 id=1623 兜底。</summary>
        private static IntPtr FindScaleEdit(List<Ctl> ctls)
        {
            Ctl g = FindGroupBox(ctls, "比例");
            if (g != null)
            {
                foreach (Ctl c in ctls)
                {
                    if (c.Cls.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int cx = (c.Left + c.Right) / 2, cy = (c.Top + c.Bottom) / 2;
                    if (cx >= g.Left && cx <= g.Right && cy >= g.Top && cy <= g.Bottom) return c.H;
                }
            }
            foreach (Ctl c in ctls)
                if (c.Id == 1623 && c.Cls.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) return c.H;
            return IntPtr.Zero;
        }

        private static Ctl FindGroupBox(List<Ctl> ctls, string caption)
        {
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (c.Id == -1 && CleanButtonText(c.Text) == caption &&
                    (c.Right - c.Left) > 80 && (c.Bottom - c.Top) > 40) return c;
            }
            return null;
        }

        private static Ctl FindCheckInGroup(List<Ctl> ctls, Ctl group, string caption)
        {
            int gx = (group.Left + group.Right) / 2, gy = (group.Top + group.Bottom) / 2;
            Ctl best = null; int bestD = int.MaxValue;
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (c.Id == -1) continue;                       // 组框本身排除
                if (CleanButtonText(c.Text).IndexOf(caption, StringComparison.Ordinal) < 0) continue;
                int cx = (c.Left + c.Right) / 2, cy = (c.Top + c.Bottom) / 2;
                if (cx < group.Left || cx > group.Right || cy < group.Top || cy > group.Bottom) continue;
                int d = Math.Abs(cx - gx) + Math.Abs(cy - gy);
                if (d < bestD) { best = c; bestD = d; }
            }
            return best;
        }

        private static Ctl FindButtonByText(List<Ctl> ctls, string caption)
        {
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (c.Id == -1) continue;
                if (CleanButtonText(c.Text).IndexOf(caption, StringComparison.Ordinal) >= 0) return c;
            }
            return null;
        }

        /// <summary>点「打开」后出现的可见 #32770 全部 dump 控件树（可能是参数框/警告框）。</summary>
        private static void SnapshotFollowUps(Stopwatch sw, int myEpoch)
        {
            // 09:06 实机排查：成功快车道上也空等 20 s 观察窗（09:06:31.852 图像框已关、
            // 宿主已落图，却干等到 09:06:52 才去轮询 QR_STATE）。成功旗标全置位时
            // 宿主要么不弹框、要么立即弹错误框 ⇒ 3 s 足够覆盖；失败路径保持 20 s。
            bool fast = OkClicked && ImageDlgFound && PointFilled && LastError.Length == 0;
            var deadline = sw.ElapsedMilliseconds + (fast ? 3000 : 20000);
            if (fast) Q("成功快车道：插入点/比例/确定已全部确认，观察窗缩为 3 s（防御性保留，捕获立即弹出的错误框）");
            var seen = new List<IntPtr>();
            while (_run && myEpoch == _epoch && !Stopping() && sw.ElapsedMilliseconds < deadline)
            {
                EnumWindows(delegate (IntPtr h, IntPtr l)
                {
                    if (!IsWindowVisible(h) || seen.Contains(h)) return true;
                    var cls = new StringBuilder(128);
                    GetClassName(h, cls, cls.Capacity);
                    if (cls.ToString() != "#32770") return true;
                    IntPtr owner = GetWindow(h, GW_OWNER);
                    if (owner != IntPtr.Zero)
                    {
                        var oc = new StringBuilder(128);
                        GetClassName(owner, oc, oc.Capacity);
                        if (oc.ToString().StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    seen.Add(h);
                    string title = ReadText(h);
                    var cs = ListControls(h);
                    var sb = new StringBuilder();
                    bool hasEdit = false, hasOk = false;
                    foreach (Ctl c in cs)
                    {
                        sb.Append("  id=").Append(c.Id).Append(" cls=").Append(c.Cls)
                          .Append(" text=\"").Append(c.Text).Append("\" hwnd=0x")
                          .Append(c.H.ToInt64().ToString("X")).AppendLine();
                        if (c.Cls.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) hasEdit = true;
                        if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 && c.Id != -1 &&
                            CleanButtonText(c.Text).IndexOf("确定", StringComparison.Ordinal) >= 0) hasOk = true;
                    }
                    lock (FollowUpDialogs)
                        FollowUpDialogs["0x" + h.ToInt64().ToString("X") + " \"" + title + "\""] = sb.ToString();
                    Q("★ 后续对话框 hwnd=0x" + h.ToInt64().ToString("X") + " 标题=\"" + title +
                      "\"（只取证不代点），控件树共 " + cs.Count + " 项 ↓↓↓");
                    foreach (Ctl c in cs)
                        Q("  id=" + c.Id + " cls=" + c.Cls + " text=\"" + c.Text + "\" hwnd=0x" +
                          c.H.ToInt64().ToString("X"));
                    // ★ 坑 41 防御：错误提示框（有「确定」按钮、无 Edit）= 宿主拒绝参数的模态框，
                    //   必须关掉，否则 CRX UI 线程被占、桥上一切请求全 TIMEOUT。
                    if (hasOk && !hasEdit)
                    {
                        ClickButtonByText(h, cs, "确定");
                        Q("★ 已自动代点「确定」关闭错误提示框（" + _lastClickWhy + "）");
                    }
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(300);
            }
        }

        private static IntPtr FindPathEdit(List<Ctl> ctls)
        {
            // 标准文件框：文件名 Edit 通常最宽；排除「查找范围」ComboBox 自己（不是 Edit 类）
            IntPtr best = IntPtr.Zero; int bestW = -1;
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int w = c.Right - c.Left;
                if (w > bestW && c.Text.IndexOf("查找", StringComparison.Ordinal) < 0) { best = c.H; bestW = w; }
            }
            return best;
        }

        private static Ctl FindOpenButton(List<Ctl> ctls)
        {
            // ★ 08:01 实机坑：文件框里「以只读方式打开(&R)」也含「打开」二字且先被枚举到，
            //   首轮误点了它。修正：① id==1 且文本含「打开」优先（标准文件框默认按钮）；
            //   ② 排除「只读」；③ 剩余按「清洁文本 == 打开O」精确匹配。
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0 || c.Id != 1) continue;
                string t = CleanButtonText(c.Text);
                if (t.IndexOf("打开", StringComparison.Ordinal) >= 0 && t.IndexOf("只读", StringComparison.Ordinal) < 0) return c;
            }
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string t = CleanButtonText(c.Text);
                if (t.IndexOf("打开", StringComparison.Ordinal) >= 0 && t.IndexOf("只读", StringComparison.Ordinal) < 0 &&
                    t.IndexOf("方式", StringComparison.Ordinal) < 0) return c;
            }
            foreach (Ctl c in ctls)
                if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0 && c.Id == 1) return c;
            return null;
        }

        private static bool FindFileDialog(out IntPtr dlg, out List<Ctl> ctlsOut)
        {
            dlg = IntPtr.Zero; ctlsOut = null;
            IntPtr found = IntPtr.Zero;
            List<Ctl> hit = null;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (found != IntPtr.Zero) return false;
                if (!IsWindowVisible(h)) return true;
                var cls = new StringBuilder(128);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "#32770") return true;
                IntPtr owner = GetWindow(h, GW_OWNER);
                if (owner != IntPtr.Zero)
                {
                    var oc = new StringBuilder(128);
                    GetClassName(owner, oc, oc.Capacity);
                    if (oc.ToString().StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase)) return true;
                }
                var cs = ListControls(h);
                bool hasEdit = false, hasOpen = false, hasCancel = false;
                foreach (Ctl c in cs)
                {
                    if (c.Cls.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) hasEdit = true;
                    if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string t = CleanButtonText(c.Text);
                        if (t.IndexOf("打开", StringComparison.Ordinal) >= 0) hasOpen = true;
                        if (t.IndexOf("取消", StringComparison.Ordinal) >= 0) hasCancel = true;
                    }
                }
                // 结构签名：Edit + 打开 + 取消 —— 标准文件选择框；纯消息框没有 Edit，不会误中
                if (hasEdit && hasOpen && hasCancel) { found = h; hit = cs; return false; }
                return true;
            }, IntPtr.Zero);
            dlg = found; ctlsOut = hit;
            return found != IntPtr.Zero;
        }

        private static void ClickCancel(IntPtr dlg, List<Ctl> ctls)
        {
            Ctl btn = ClickButtonByText(dlg, ctls, "取消");
            if (btn == null) btn = ClickButtonByText(dlg, ctls, "关闭");
            if (_lastClickWhy.Length > 0) Q("已代点关闭按钮：" + _lastClickWhy);
        }

        private static string _lastClickWhy = "";

        /// <summary>按文本找按钮代点（优先级按传入顺序）。命中返回 true。</summary>
        private static Ctl ClickButtonByText(IntPtr dlg, List<Ctl> ctls, string caption)
        {
            _lastClickWhy = "";
            foreach (Ctl c in ctls)
            {
                if (c.Cls.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (c.Id == -1) continue;
                if (CleanButtonText(c.Text).IndexOf(caption, StringComparison.Ordinal) < 0) continue;
                PostMessage(dlg, WM_COMMAND, new IntPtr(c.Id & 0xFFFF), c.H);
                PostMessage(c.H, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                _lastClickWhy = "「" + caption + "」 id=" + c.Id + " hwnd=0x" + c.H.ToInt64().ToString("X");
                return c;
            }
            return null;
        }

        /// <summary>
        /// ★ 停止/失败后清理宿主残留对话框（08:45 实机坑 41：第一轮中断后宿主「图像」框没人关，
        ///   CRX UI 线程被模态循环占住 ⇒ PING 全 TIMEOUT ⇒ 后续两轮全卡稳定闸，「插入失败」）。
        /// 枚举宿主可见 #32770（排除插件自家 WindowsForms owner 的框），按「取消→关闭→确定」代点关闭，
        /// 最多清 3 框、循环 3 轮。只在停止/失败路径调用。
        /// </summary>
        public static int CloseResidualDialogs()
        {
            int closed = 0;
            for (int round = 0; round < 3; round++)
            {
                IntPtr hit = IntPtr.Zero;
                List<Ctl> hitCs = null;
                EnumWindows(delegate (IntPtr h, IntPtr l)
                {
                    if (hit != IntPtr.Zero) return false;
                    if (!IsWindowVisible(h) || !IsWindow(h)) return true;
                    var cls = new StringBuilder(128);
                    GetClassName(h, cls, cls.Capacity);
                    if (cls.ToString() != "#32770") return true;
                    IntPtr owner = GetWindow(h, GW_OWNER);
                    if (owner != IntPtr.Zero)
                    {
                        var oc = new StringBuilder(128);
                        GetClassName(owner, oc, oc.Capacity);
                        if (oc.ToString().StartsWith("WindowsForms", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    hit = h; hitCs = ListControls(h);
                    return false;
                }, IntPtr.Zero);
                if (hit == IntPtr.Zero) break;
                string title = ReadText(hit);
                Ctl btn = ClickButtonByText(hit, hitCs, "取消") ??
                          ClickButtonByText(hit, hitCs, "关闭") ??
                          ClickButtonByText(hit, hitCs, "确定");
                Q("清理残留对话框 hwnd=0x" + hit.ToInt64().ToString("X") + " 标题=\"" + title + "\" → " +
                  (_lastClickWhy.Length > 0 ? _lastClickWhy : "没有可点的关闭按钮（控件树共 " + hitCs.Count + " 项）"));
                if (btn == null) break;          // 点不动就别死循环
                closed++;
                var w = Stopwatch.StartNew();
                while (w.ElapsedMilliseconds < 3000 && IsWindow(hit) && IsWindowVisible(hit)) Thread.Sleep(120);
            }
            return closed;
        }

        /// <summary>
        /// 11-15 实机坑（重跑同名场景）：宿主「插入图片」命令滞留（pending=1、delta=0 永挂）时，
        /// 向图纸窗口投 ESC 键取消滞留命令 ⇒ 宿主命令结束、桥 commandEnded 落地 ⇒
        /// QR_STATE 轮询按「pending=0 且 delta=0」快速判死，不吊满 180 s。
        /// </summary>
        public static void SendEsc(IntPtr hwnd)
        {
            const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, VK_ESCAPE = 0x1B;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)VK_ESCAPE, IntPtr.Zero);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)VK_ESCAPE, IntPtr.Zero);
        }

        private static void WaitClosed(IntPtr dlg, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (_run && !Stopping() && sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsWindow(dlg) || !IsWindowVisible(dlg)) break;
                Thread.Sleep(150);
            }
        }

        private static bool OpenClipboardRetry()
        {
            for (int i = 0; i < 8; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private static string ReadClipboardText()
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(h);
            string s = p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
            if (p != IntPtr.Zero) GlobalUnlock(h);
            return s;
        }

        private static bool PutClipboardText(string s, out string err)
        {
            err = "";
            if (!OpenClipboardRetry()) { err = "OpenClipboard 8×100 ms 全失败"; return false; }
            try
            {
                if (!EmptyClipboard()) { err = "EmptyClipboard 失败"; return false; }
                IntPtr hW = AllocGlobal(Encoding.Unicode.GetBytes(s + "\0"));
                if (hW == IntPtr.Zero) { err = "GlobalAlloc(CF_UNICODETEXT) 失败"; return false; }
                if (SetClipboardData(CF_UNICODETEXT, hW) == IntPtr.Zero)
                {
                    GlobalFree(hW);
                    err = "SetClipboardData(CF_UNICODETEXT) 失败";
                    return false;
                }
                try
                {
                    IntPtr hA = AllocGlobal(Encoding.GetEncoding(936).GetBytes(s + "\0"));
                    if (hA != IntPtr.Zero && SetClipboardData(CF_TEXT, hA) == IntPtr.Zero) GlobalFree(hA);
                }
                catch { }
                return true;
            }
            finally { CloseClipboard(); }
        }

        private static IntPtr AllocGlobal(byte[] bytes)
        {
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, new IntPtr(bytes.Length));
            if (h == IntPtr.Zero) return IntPtr.Zero;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return h; }
            Marshal.Copy(bytes, 0, p, bytes.Length);
            GlobalUnlock(h);
            return h;
        }

        private static List<Ctl> ListControls(IntPtr dlg)
        {
            var list = new List<Ctl>();
            EnumChildWindows(dlg, delegate (IntPtr h, IntPtr l)
            {
                var cls = new StringBuilder(256);
                var txt = new StringBuilder(512);
                GetClassName(h, cls, cls.Capacity);
                GetWindowText(h, txt, txt.Capacity);
                RECT rc;
                GetWindowRect(h, out rc);
                list.Add(new Ctl
                {
                    H = h, Cls = cls.ToString(), Text = txt.ToString(), Id = GetDlgCtrlID(h),
                    Left = rc.Left, Top = rc.Top, Right = rc.Right, Bottom = rc.Bottom
                });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static string CleanButtonText(string s)
        {
            return (s ?? "").Replace("&", "").Replace("(", "").Replace(")", "")
                            .Replace("（", "").Replace("）", "").Replace(" ", "");
        }
    }
}
