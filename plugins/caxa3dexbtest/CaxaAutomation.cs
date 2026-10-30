using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace caxa3dexbtest
{
    /// <summary>
    /// CAXA 3D 连接会话（只持有 IRONCAD.Application 的 RCW）。
    ///
    /// 重要：不要在该对象上做 vtable 直调。
    /// 实测（2026-10，CAXA 3D 2027）：QI(IZBaseApp) 虽返回 S_OK，但拿到的是
    /// ZIronCADApp 那个只有 11 个成员的短 vtable（OpenNewScene/OpenNewDrawing/
    /// ApplicationVersion/CreateDrawingFromScene + 7 个 IDispatch/IUnknown 槽），
    /// 槽位 11 实际是 OpenNewDrawing —— 按 IZBaseApp 的槽位直调会误开新图纸。
    /// 因此本工具只通过 IDispatch 按名字读取安全属性，图纸环境判断交给
    /// 进程内的 CRX 桥接模块（它只在图纸环境加载，且能直接问图纸数据库）。
    /// </summary>
    internal sealed class CaxaSession : IDisposable
    {
        public object AppObject;   // IRONCAD.Application RCW

        public void Dispose()
        {
            if (AppObject != null) { Marshal.ReleaseComObject(AppObject); AppObject = null; }
        }
    }

    /// <summary>连接与图纸环境检测结果。</summary>
    internal sealed class EnvInfo
    {
        public bool Connected;      // COM 会话可用
        public string WindowTitle;  // CAXA 主窗口标题（含文档名）
        public string DocName;      // 从标题解析出的文档名
        public bool BridgeOnline;   // CRX 桥接模块是否响应
        public bool IsDrawingEnv;   // 桥接模块确认当前在图纸环境

        public string Describe()
        {
            if (!Connected) return "未连接";
            string doc = string.IsNullOrEmpty(DocName) ? "(无)" : DocName;
            if (IsDrawingEnv) return "已连接 ｜ 文档: " + doc + " ｜ 已进入图纸环境 ｜ 桥接模块在线";
            if (BridgeOnline) return "已连接 ｜ 文档: " + doc + " ｜ 未进入图纸环境（桥接模块未报图纸）";
            return "已连接 ｜ 文档: " + doc + " ｜ 桥接模块离线（需重启 CAXA 3D 加载模块）";
        }
    }

    internal static class CaxaAutomation
    {
        private const string GenericProgId = "IRONCAD.Application";
        private static string s_appProgId;

        private static string AppProgId
        {
            get
            {
                if (s_appProgId != null) return s_appProgId;
                if (Type.GetTypeFromProgID(GenericProgId) != null) return s_appProgId = GenericProgId;
                try
                {
                    var found = new List<string>();
                    foreach (string name in Registry.ClassesRoot.GetSubKeyNames())
                    {
                        if (!name.StartsWith(GenericProgId, StringComparison.OrdinalIgnoreCase)) continue;
                        if (Type.GetTypeFromProgID(name) != null) found.Add(name);
                    }
                    if (found.Count > 0)
                    {
                        found.Sort(StringComparer.OrdinalIgnoreCase);
                        return s_appProgId = found[found.Count - 1];
                    }
                }
                catch (Exception ex) { Diagnostics.Log("progid scan failed: " + ex.Message); }
                return s_appProgId = GenericProgId;
            }
        }

        /// <summary>CRX 桥接命名管道名（与 Bridge\ExbRectBridge.cpp 一致）。</summary>
        public const string BridgePipeName = "Caxa3dExbRectBridge";

        // ZIronCADApp 属性在 IRONCAD.Application 默认接口上的 DISPID（仅诊断用）
        private const int DispidZIronCADApp = 0x2A;
        private const int DISPID_UNKNOWN = -1;

        #region 原始 COM 互操作

        [ComImport, Guid("00020400-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IRawDispatch
        {
            [PreserveSig] int GetTypeInfoCount(out int pcTInfo);
            [PreserveSig] int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo);
            [PreserveSig] int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, int cNames, int lcid, IntPtr rgDispId);
            [PreserveSig] int Invoke(int dispIdMember, ref Guid riid, int lcid, ushort wFlags,
                IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
        }

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>按名字通过 IDispatch 读取字符串属性（失败返回 null，不抛异常）。</summary>
        private static string TryDispatchGetString(object comObj, string propName)
        {
            if (comObj == null) return null;
            IntPtr pNames = IntPtr.Zero, pName = IntPtr.Zero, pIds = IntPtr.Zero;
            IntPtr pDp = IntPtr.Zero, pResult = IntPtr.Zero, pExcep = IntPtr.Zero, pArgErr = IntPtr.Zero;
            try
            {
                IRawDispatch d = (IRawDispatch)comObj;
                Guid empty = Guid.Empty;

                pNames = Marshal.AllocCoTaskMem(IntPtr.Size);
                pName = Marshal.StringToCoTaskMemUni(propName);
                pIds = Marshal.AllocCoTaskMem(4);
                Marshal.WriteIntPtr(pNames, pName);
                if (d.GetIDsOfNames(ref empty, pNames, 1, 0, pIds) != 0) return null;
                int dispId = Marshal.ReadInt32(pIds);
                if (dispId == DISPID_UNKNOWN) return null;

                pDp = Marshal.AllocCoTaskMem(32);
                pResult = Marshal.AllocCoTaskMem(24);
                pExcep = Marshal.AllocCoTaskMem(80);
                pArgErr = Marshal.AllocCoTaskMem(4);
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pDp, i, 0);
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pResult, i, 0);
                for (int i = 0; i < 80; i++) Marshal.WriteByte(pExcep, i, 0);

                // 同一成员可能是方法也可能是属性，两种调用标志都试
                int hr = d.Invoke(dispId, ref empty, 0, 1 /*INVOKE_FUNC*/, pDp, pResult, pExcep, pArgErr);
                if (hr != 0)
                {
                    for (int i = 0; i < 24; i++) Marshal.WriteByte(pResult, i, 0);
                    hr = d.Invoke(dispId, ref empty, 0, 2 /*INVOKE_PROPERTYGET*/, pDp, pResult, pExcep, pArgErr);
                }
                if (hr != 0) return null;

                short vt = Marshal.ReadInt16(pResult);
                if (vt != 8 /*VT_BSTR*/) return null;   // 某些属性返回非字符串，视为读取失败
                IntPtr pb = Marshal.ReadIntPtr(pResult, 8);
                if (pb == IntPtr.Zero) return null;
                string s = Marshal.PtrToStringBSTR(pb);
                Marshal.FreeBSTR(pb);
                return s;
            }
            catch { return null; }
            finally
            {
                if (pNames != IntPtr.Zero) Marshal.FreeCoTaskMem(pNames);
                if (pName != IntPtr.Zero) Marshal.FreeCoTaskMem(pName);
                if (pIds != IntPtr.Zero) Marshal.FreeCoTaskMem(pIds);
                if (pDp != IntPtr.Zero) Marshal.FreeCoTaskMem(pDp);
                if (pResult != IntPtr.Zero) Marshal.FreeCoTaskMem(pResult);
                if (pExcep != IntPtr.Zero) Marshal.FreeCoTaskMem(pExcep);
                if (pArgErr != IntPtr.Zero) Marshal.FreeCoTaskMem(pArgErr);
            }
        }

        private static string GetRegisteredServerPath()
        {
            try
            {
                using (RegistryKey k = Registry.ClassesRoot.OpenSubKey(AppProgId + "\\CLSID"))
                {
                    if (k == null) return null;
                    string clsid = k.GetValue(null) as string;
                    if (string.IsNullOrWhiteSpace(clsid)) return null;
                    using (RegistryKey s = Registry.ClassesRoot.OpenSubKey("CLSID\\" + clsid + "\\LocalServer32"))
                    {
                        if (s == null) return null;
                        string v = s.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(v)) return null;
                        v = v.Trim();
                        if (v.StartsWith("\""))
                        {
                            int end = v.IndexOf('"', 1);
                            if (end > 1) return v.Substring(1, end - 1);
                        }
                        int sp = v.IndexOf(' ');
                        return sp > 0 ? v.Substring(0, sp) : v;
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("registry read failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>判定指定进程是否拥有"可见且带标题"的顶层窗口（比 MainWindowHandle 可靠）。</summary>
        private static bool ProcessHasVisibleTopWindow(int pid)
        {
            bool found = false;
            EnumWindowsProc cb = delegate (IntPtr hWnd, IntPtr lParam)
            {
                int wp;
                GetWindowThreadProcessId(hWnd, out wp);
                if (wp == pid && IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0)
                {
                    found = true;
                    return false;
                }
                return true;
            };
            try { EnumWindows(cb, IntPtr.Zero); } catch { }
            return found;
        }

        private static int CountWindowedIroncadProcesses()
        {
            int n = 0;
            try
            {
                foreach (Process p in Process.GetProcessesByName("IRONCAD"))
                {
                    try { if (ProcessHasVisibleTopWindow(p.Id)) n++; }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex) { Diagnostics.Log("window probe failed: " + ex.Message); }
            return n;
        }

        /// <summary>结束无可见窗口的后台 IRONCAD 残留（避免 ROT 绑到后台实例）。</summary>
        public static int KillBackgroundIroncadProcesses()
        {
            int killed = 0;
            foreach (Process p in Process.GetProcessesByName("IRONCAD"))
            {
                try
                {
                    if (ProcessHasVisibleTopWindow(p.Id)) continue;
                    try
                    {
                        if (p.StartTime > DateTime.Now.AddSeconds(-15)) continue; // 保护刚启动的实例
                    }
                    catch { }
                    Diagnostics.Log(string.Format("cleanup: killing background IRONCAD pid={0}", p.Id));
                    p.Kill();
                    p.WaitForExit(5000);
                    killed++;
                }
                catch (Exception ex) { Diagnostics.Log("cleanup: kill failed: " + ex.Message); }
                finally { p.Dispose(); }
            }
            return killed;
        }

        #endregion

        #region 连接管理

        /// <summary>连接已运行的前台 CAXA 3D（不自动拉起新实例、不调用任何 vtable 槽位）。</summary>
        public static CaxaSession Connect()
        {
            int killed = 0;
            try { killed = KillBackgroundIroncadProcesses(); }
            catch (Exception ex) { Diagnostics.Log("cleanup failed: " + ex.Message); }
            if (killed > 0) Diagnostics.Log("cleanup: killed " + killed + " background IRONCAD process(es)");

            if (Type.GetTypeFromProgID(AppProgId) == null)
                throw new InvalidOperationException(
                    "未找到 CAXA 3D 的 COM 注册（ProgID: " + AppProgId + "）。请确认已安装 CAXA 3D。");

            if (CountWindowedIroncadProcesses() == 0)
                throw new InvalidOperationException(
                    "没有带可见主窗口的 CAXA 3D 前台进程。请先启动 CAXA 3D 并打开图纸。");

            object appObj;
            try
            {
                appObj = Marshal.GetActiveObject(AppProgId);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    "前台 CAXA 3D 未注册到 COM 运行对象表（GetActiveObject hr=0x" + ex.ErrorCode.ToString("X") +
                    "）。通常是后台残留实例抢占注册位（已由本工具清理）。请重启 CAXA 3D 后重试。");
            }

            // 用安全的 IDispatch 属性读取验证服务器真实存活（不碰 vtable）
            string title = TryDispatchGetString(appObj, "WindowTitle");
            if (title == null)
            {
                Marshal.ReleaseComObject(appObj);
                throw new InvalidOperationException(
                    "COM 对象已取得，但读取 WindowTitle 失败（服务器未响应）。请重启 CAXA 3D 后重试。");
            }
            Diagnostics.Log("connect: session established, title=[" + title + "]");
            return new CaxaSession { AppObject = appObj };
        }

        /// <summary>探测会话是否仍然有效。</summary>
        public static bool Ping(CaxaSession session)
        {
            if (session == null || session.AppObject == null) return false;
            try { return TryDispatchGetString(session.AppObject, "WindowTitle") != null; }
            catch { return false; }
        }

        /// <summary>恢复 CAXA 主窗口显示但不抢焦点（SW_SHOWNOACTIVATE）。</summary>
        public static void EnsureFrameVisible()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("IRONCAD"))
                {
                    if (p.MainWindowHandle == IntPtr.Zero) { p.Dispose(); continue; }
                    ShowWindow(p.MainWindowHandle, 4);
                    p.Dispose();
                    break;
                }
            }
            catch { }
        }

        #endregion

        #region 图纸环境检测

        /// <summary>
        /// 检测当前是否处于图纸环境。
        /// 判定依据是进程内 CRX 桥接模块的 STATUS 应答 —— 该模块只在图纸环境加载，
        /// 且能直接检查图纸数据库，比 COM 接口猜测可靠得多。
        /// </summary>
        public static EnvInfo DetectEnvironment(CaxaSession session)
        {
            var info = new EnvInfo();

            if (session == null || session.AppObject == null) return info;
            info.WindowTitle = TryDispatchGetString(session.AppObject, "WindowTitle");
            info.Connected = info.WindowTitle != null;
            info.DocName = ExtractDocName(info.WindowTitle);

            string status = BridgeQuery("STATUS", 1200);
            info.BridgeOnline = status != null;
            info.IsDrawingEnv = status != null && status.StartsWith("DRAWING");

            Diagnostics.Log(string.Format("env: connected={0} bridge={1} status={2} title=[{3}]",
                info.Connected, info.BridgeOnline, status ?? "(无应答)", info.WindowTitle ?? ""));
            return info;
        }

        /// <summary>从 CAXA 标题 "[工程图文档4]" 里取文档名。</summary>
        private static string ExtractDocName(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            int l = title.LastIndexOf('[');
            int r = title.LastIndexOf(']');
            if (l >= 0 && r > l) return title.Substring(l + 1, r - l - 1).Trim();
            return title.Trim();
        }

        #endregion

        #region CRX 桥接客户端（命名管道）

        /// <summary>请求桥接模块在原点画 100x100 矩形；返回 null 表示成功，否则为错误描述。</summary>
        public static string BridgeDrawRect(double x0, double y0, double x1, double y1, int timeoutMs)
        {
            string cmd = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "RECT {0} {1} {2} {3}", x0, y0, x1, y1);
            string reply;
            try
            {
                reply = BridgeQuery(cmd, timeoutMs);
            }
            catch (Exception ex)
            {
                return "连接桥接模块失败: " + ex.Message;
            }
            if (reply == null) return "桥接模块无响应";
            if (reply.StartsWith("OK")) return null;
            return reply.Trim();
        }

        private static string BridgeQuery(string command, int timeoutMs)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", BridgePipeName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMs);
                    byte[] req = Encoding.ASCII.GetBytes(command + "\n");
                    pipe.Write(req, 0, req.Length);
                    pipe.Flush();

                    var buf = new byte[512];
                    int read = pipe.Read(buf, 0, buf.Length);
                    if (read <= 0) return null;
                    return Encoding.ASCII.GetString(buf, 0, read).Trim();
                }
            }
            catch { return null; }
        }

        #endregion
    }
}
