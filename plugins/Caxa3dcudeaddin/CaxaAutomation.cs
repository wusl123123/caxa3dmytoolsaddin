using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace Caxa3dcudeaddin
{
    /// <summary>
    /// 进程内 CAXA 3D (IRONCAD 内核) 会话。
    /// Z 系列接口对象是“单 IDispatch 多表面”：QueryInterface 任何接口均成功，
    /// 但 IDispatch 只认默认接口成员，因此必须保存原始接口指针并走 vtable 直调。
    /// （vtable 槽位号取自类型库 FUNCDESC，x64 下偏移 = 槽位 × 8）
    /// </summary>
    internal sealed class CaxaSession : IDisposable
    {
        public object AppObject;   // IZBaseApp RCW（保活）
        public IntPtr BaseApp;     // IZBaseApp 接口指针

        public void Dispose()
        {
            if (BaseApp != IntPtr.Zero)
            {
                Marshal.Release(BaseApp);
                BaseApp = IntPtr.Zero;
            }
            AppObject = null;   // 进程内对象归 CAXA 所有，不 ReleaseComObject
        }
    }

    /// <summary>
    /// CAXA 3D COM 自动化封装（进程内 vtable 直调版）。
    /// 注意：ICAPI 内部长度单位为“米”，毫米需乘以 0.001。
    /// vtable 槽位号取自类型库 FUNCDESC：
    ///   IZBaseApp:        ActiveDoc = 11, CreateNewDoc = 66
    ///   IZSceneDoc:       GetChildElements = 7, CreatePart = 9
    ///   IZPartFeatureMgr: CreateBlock = 19
    /// </summary>
    internal static class CaxaAutomation
    {
        private const double MmToMeter = 0.001;

        /// <summary>界面默认边长（毫米）。</summary>
        public const double DefaultSizeMm = 10.0;

        // ---- 接口 GUID ----
        private static readonly Guid IID_IZBaseApp = new Guid("A5B9AF7D-228B-4ED1-BA42-D51318F7EA20");
        private static readonly Guid IID_IZSceneDoc = new Guid("3845C6DA-89D6-43F7-8B10-4BE36E0E758B");
        private static readonly Guid IID_IZPart = new Guid("F20F2411-05C4-4CA6-B2B4-5DB29531EDD7");
        private static readonly Guid IID_IZPartFeatureMgr = new Guid("26D70B9B-291B-4727-9DA1-E2D7EEC1FA27");

        // ---- vtable 槽位 ----
        private const int SlotActiveDoc = 11;
        private const int SlotCreateNewDoc = 66;
        private const int SlotGetChildElements = 7;
        private const int SlotCreatePart = 9;
        private const int SlotCreateBlock = 19;

        // ZIronCADApp 属性在默认接口上的 DISPID
        private const int DispidZIronCADApp = 0x2A;

        private const int HR_NOINTERFACE = unchecked((int)0x80004002);

        private const int DISPID_UNKNOWN = -1;
        private const short VT_BSTR = 8;

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

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetObj(IntPtr self, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetVariant(IntPtr self, IntPtr pVar);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnCreateNewDoc(IntPtr self, int eType, int vbInteractive, int vbUseDefault,
            [MarshalAs(UnmanagedType.BStr)] string templateName, int vbVisible, out IntPtr ppDoc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnCreateBlock(IntPtr self,
            [MarshalAs(UnmanagedType.Struct)] object pt1,
            [MarshalAs(UnmanagedType.Struct)] object pt2,
            out IntPtr ppFeature);

        [DllImport("oleaut32.dll")] private static extern int SafeArrayAccessData(IntPtr psa, out IntPtr ppvData);
        [DllImport("oleaut32.dll")] private static extern int SafeArrayUnaccessData(IntPtr psa);
        [DllImport("oleaut32.dll")] private static extern int SafeArrayGetLBound(IntPtr psa, uint nDim, out int plLbound);
        [DllImport("oleaut32.dll")] private static extern int SafeArrayGetUBound(IntPtr psa, uint nDim, out int plUbound);
        [DllImport("oleaut32.dll")] private static extern int SafeArrayDestroy(IntPtr psa);

        private static IntPtr Slot(IntPtr pIface, int slot)
        {
            IntPtr vt = Marshal.ReadIntPtr(pIface);
            return Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
        }

        private static int TryQI(IntPtr pUnk, Guid iid, out IntPtr ppv)
        {
            return Marshal.QueryInterface(pUnk, ref iid, out ppv);
        }

        /// <summary>调用“[out,retval] 接口指针”形态的无参方法，不抛异常，返回 HRESULT。</summary>
        private static int TryCallGetObj(IntPtr pIface, int slot, out IntPtr ppv)
        {
            FnGetObj fn = (FnGetObj)Marshal.GetDelegateForFunctionPointer(Slot(pIface, slot), typeof(FnGetObj));
            return fn(pIface, out ppv);
        }

        /// <summary>
        /// 通过旧自动化接口的默认 IDispatch 读取 ZIronCADApp 属性（memid 0x2A）。
        /// 进程内兜底路径：_site.Application 的 QI 意外失败时使用。
        /// </summary>
        private static object GetZAppObject(object app)
        {
            IRawDispatch d = (IRawDispatch)app;
            Guid empty = Guid.Empty;
            IntPtr pDp = Marshal.AllocCoTaskMem(32);
            for (int i = 0; i < 4; i++) Marshal.WriteInt64(pDp, i * 8, 0);
            IntPtr pResult = Marshal.AllocCoTaskMem(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(pResult, i, 0);
            IntPtr pExcep = Marshal.AllocCoTaskMem(80);
            for (int i = 0; i < 80; i++) Marshal.WriteByte(pExcep, i, 0);
            IntPtr pArgErr = Marshal.AllocCoTaskMem(4);
            try
            {
                const ushort INVOKE_PROPERTYGET = 2;
                int hr = d.Invoke(DispidZIronCADApp, ref empty, 0, INVOKE_PROPERTYGET, pDp, pResult, pExcep, pArgErr);
                if (hr != 0)
                    throw new InvalidOperationException(
                        string.Format("读取 ZIronCADApp 属性失败 hr=0x{0:X}", hr));
                IntPtr po = Marshal.ReadIntPtr(pResult, 8);
                if (po == IntPtr.Zero) return null;
                object o = Marshal.GetObjectForIUnknown(po);
                Marshal.Release(po);
                return o;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pDp);
                Marshal.FreeCoTaskMem(pResult);
                Marshal.FreeCoTaskMem(pExcep);
                Marshal.FreeCoTaskMem(pArgErr);
            }
        }

        /// <summary>
        /// 通过 IDispatch 读取 COM 对象的字符串属性（失败返回 false，不抛异常）。
        /// </summary>
        private static bool TryDispatchGetString(object comObj, string propName, out string value)
        {
            value = null;
            try
            {
                IRawDispatch d = (IRawDispatch)comObj;
                Guid empty = Guid.Empty;

                IntPtr pNames = Marshal.AllocCoTaskMem(IntPtr.Size);
                IntPtr pName = Marshal.StringToCoTaskMemUni(propName);
                IntPtr pIds = Marshal.AllocCoTaskMem(4);
                try
                {
                    Marshal.WriteIntPtr(pNames, pName);
                    int hr = d.GetIDsOfNames(ref empty, pNames, 1, 0, pIds);
                    if (hr != 0) return false;
                    int dispId = Marshal.ReadInt32(pIds);
                    if (dispId == DISPID_UNKNOWN) return false;

                    IntPtr pDp = Marshal.AllocCoTaskMem(32);
                    IntPtr pResult = Marshal.AllocCoTaskMem(24);
                    IntPtr pExcep = Marshal.AllocCoTaskMem(80);
                    IntPtr pArgErr = Marshal.AllocCoTaskMem(4);
                    try
                    {
                        for (int i = 0; i < 4; i++) Marshal.WriteInt64(pDp, i * 8, 0);
                        for (int i = 0; i < 24; i++) Marshal.WriteByte(pResult, i, 0);
                        for (int i = 0; i < 80; i++) Marshal.WriteByte(pExcep, i, 0);
                        const ushort INVOKE_PROPERTYGET = 2;
                        hr = d.Invoke(dispId, ref empty, 0, INVOKE_PROPERTYGET, pDp, pResult, pExcep, pArgErr);
                        if (hr != 0) return false;
                        short vt = Marshal.ReadInt16(pResult);
                        if (vt != VT_BSTR) return false;
                        IntPtr pBstr = Marshal.ReadIntPtr(pResult, 8);
                        if (pBstr == IntPtr.Zero) return false;
                        value = Marshal.PtrToStringBSTR(pBstr);
                        Marshal.FreeBSTR(pBstr);
                        return !string.IsNullOrEmpty(value);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pDp);
                        Marshal.FreeCoTaskMem(pResult);
                        Marshal.FreeCoTaskMem(pExcep);
                        Marshal.FreeCoTaskMem(pArgErr);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pName);
                    Marshal.FreeCoTaskMem(pNames);
                    Marshal.FreeCoTaskMem(pIds);
                }
            }
            catch
            {
                return false;
            }
        }

        #endregion

        /// <summary>
        /// 从 Addin 站点提供的 IZBaseApp 构建进程内会话。
        /// 进程内无需清理后台进程 / ROT / 窗口归属检查 —— 本代码就运行在 CAXA 主进程里。
        /// 失败时抛出异常。
        /// </summary>
        public static CaxaSession ConnectFromSite(IZBaseApp siteApp)
        {
            if (siteApp == null)
                throw new InvalidOperationException("Addin 站点未提供 Application 对象。");

            object appObj = (object)siteApp;
            IntPtr pBase = IntPtr.Zero;

            // 直接 QI IZBaseApp（进程内 QI 是服务器本地调用，不依赖 HKCR\Interface 注册）
            IntPtr pUnk = Marshal.GetIUnknownForObject(appObj);
            int hr = TryQI(pUnk, IID_IZBaseApp, out pBase);
            Marshal.Release(pUnk);

            if (hr == HR_NOINTERFACE)
            {
                // 兜底：经 IDispatch 读 ZIronCADApp 再 QI
                Diagnostics.Log("QI(IZBaseApp) E_NOINTERFACE, fallback to IDispatch ZIronCADApp");
                object zObj = GetZAppObject(appObj);
                if (zObj != null)
                {
                    pUnk = Marshal.GetIUnknownForObject(zObj);
                    hr = TryQI(pUnk, IID_IZBaseApp, out pBase);
                    Marshal.Release(pUnk);
                }
            }

            if (hr != 0 || pBase == IntPtr.Zero)
            {
                if (pBase != IntPtr.Zero) Marshal.Release(pBase);
                throw new InvalidOperationException(
                    string.Format("QueryInterface(IZBaseApp) 失败 hr=0x{0:X}", hr));
            }

            // 验证调用：确认服务器真实存活
            IntPtr pDoc;
            hr = TryCallGetObj(pBase, SlotActiveDoc, out pDoc);
            if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            Diagnostics.Log(string.Format("validate ActiveDoc hr=0x{0:X}", hr));
            if (hr != 0)
            {
                Marshal.Release(pBase);
                throw new InvalidOperationException(
                    string.Format("IZBaseApp.ActiveDoc 验证调用失败 hr=0x{0:X}", hr));
            }

            Diagnostics.Log("session accepted (in-process)");
            return new CaxaSession { AppObject = appObj, BaseApp = pBase };
        }

        /// <summary>
        /// 探测会话是否仍然有效（服务器真实响应）。
        /// 进程内通常恒真；保留该检查用于异常兜底（如服务器内部状态损坏）。
        /// </summary>
        public static bool Ping(CaxaSession session)
        {
            if (session == null || session.BaseApp == IntPtr.Zero) return false;
            try
            {
                IntPtr pDoc;
                int hr = TryCallGetObj(session.BaseApp, SlotActiveDoc, out pDoc);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取当前连接的 CAXA 3D 版本描述。
        /// 优先从 Application 对象读取 Name/Version 属性，失败时返回 null。
        /// </summary>
        public static string GetConnectedVersionText(CaxaSession session)
        {
            if (session == null || session.AppObject == null) return null;

            string name, version;
            TryDispatchGetString(session.AppObject, "Name", out name);
            TryDispatchGetString(session.AppObject, "Version", out version);
            Diagnostics.Log("version probe: Name=" + (name ?? "<n/a>") + " Version=" + (version ?? "<n/a>"));

            string comVer = ((name ?? "") + " " + (version ?? "")).Trim();
            return comVer.Length > 0 ? comVer : null;
        }

        /// <summary>
        /// 获取当前活动场景文档；没有活动文档时新建一个场景设计文档。
        /// 返回的指针由调用方负责 Release。
        /// </summary>
        private static IntPtr GetOrCreateSceneDoc(CaxaSession session)
        {
            IntPtr pDoc;
            int hr = TryCallGetObj(session.BaseApp, SlotActiveDoc, out pDoc);
            Diagnostics.Log(string.Format("ActiveDoc hr=0x{0:X} ptr=0x{1:X}", hr, pDoc.ToInt64()));

            if (hr != 0)
                throw new InvalidOperationException(
                    string.Format("读取活动文档失败 hr=0x{0:X}", hr));

            if (pDoc == IntPtr.Zero)
            {
                FnCreateNewDoc fn = (FnCreateNewDoc)Marshal.GetDelegateForFunctionPointer(
                    Slot(session.BaseApp, SlotCreateNewDoc), typeof(FnCreateNewDoc));
                const int Z_SCENE = 1;
                hr = fn(session.BaseApp, Z_SCENE, 0, -1, string.Empty, -1, out pDoc);
                Diagnostics.Log(string.Format("CreateNewDoc hr=0x{0:X} ptr=0x{1:X}", hr, pDoc.ToInt64()));
                if (hr != 0 || pDoc == IntPtr.Zero)
                    throw new InvalidOperationException(
                        string.Format("新建场景文档失败 hr=0x{0:X}", hr));
            }

            IntPtr pScene = QIRequired(pDoc, IID_IZSceneDoc, "IZSceneDoc");
            Marshal.Release(pDoc);
            return pScene;
        }

        /// <summary>QI 失败时抛出异常的封装。</summary>
        private static IntPtr QIRequired(IntPtr pUnk, Guid iid, string name)
        {
            IntPtr ppv;
            int hr = Marshal.QueryInterface(pUnk, ref iid, out ppv);
            if (hr != 0)
                throw new InvalidOperationException(
                    string.Format("QueryInterface({0}) 失败 hr=0x{1:X}", name, hr));
            return ppv;
        }

        /// <summary>
        /// 枚举场景子元素，返回所有 IZPart 元素指针（调用方负责 Release）。
        /// 用于在 CreatePart 服务器端报错（0x80010105）但实际已生成零件时回收该零件。
        /// </summary>
        private static List<IntPtr> FindAllParts(IntPtr pScene)
        {
            List<IntPtr> parts = new List<IntPtr>();
            FnGetVariant fn = (FnGetVariant)Marshal.GetDelegateForFunctionPointer(
                Slot(pScene, SlotGetChildElements), typeof(FnGetVariant));
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(pVar, i, 0);

            IntPtr psa = IntPtr.Zero;
            try
            {
                int hr = fn(pScene, pVar);
                if (hr != 0)
                    throw new InvalidOperationException(
                        string.Format("GetChildElements 失败 hr=0x{0:X}", hr));

                short vt = Marshal.ReadInt16(pVar);
                if ((vt & 0x2000) == 0) return parts; // 非数组

                psa = Marshal.ReadIntPtr(pVar, 8);
                int lb, ub;
                SafeArrayGetLBound(psa, 1, out lb);
                SafeArrayGetUBound(psa, 1, out ub);
                int n = ub - lb + 1;

                IntPtr pData;
                SafeArrayAccessData(psa, out pData);
                for (int i = 0; i < n; i++)
                {
                    IntPtr pElem = Marshal.ReadIntPtr(pData, i * 8);
                    if (pElem == IntPtr.Zero) continue;
                    IntPtr pPart;
                    if (TryQI(pElem, IID_IZPart, out pPart) == 0 && pPart != IntPtr.Zero)
                        parts.Add(pPart);
                    Marshal.Release(pElem);
                }
                SafeArrayUnaccessData(psa);
            }
            finally
            {
                if (psa != IntPtr.Zero) SafeArrayDestroy(psa);
                Marshal.FreeCoTaskMem(pVar);
            }
            return parts;
        }

        /// <summary>对一个零件尝试建块；失败返回 false 并清理，成功返回 true。
        /// 尺寸单位为米：lx→X（长）、ly→Y（宽）、lz→Z（高）。</summary>
        private static bool TryCreateBlockOnPart(IntPtr pPart, double lx, double ly, double lz)
        {
            IntPtr pFm;
            int hrQ = TryQI(pPart, IID_IZPartFeatureMgr, out pFm);
            if (hrQ != 0 || pFm == IntPtr.Zero)
            {
                Diagnostics.Log(string.Format("QI(IZPartFeatureMgr) failed on part 0x{0:X} hr=0x{1:X}",
                    pPart.ToInt64(), hrQ));
                return false;
            }
            try
            {
                object pt1 = new double[] { 0.0, 0.0, 0.0 };
                object pt2 = new double[] { lx, ly, lz };
                FnCreateBlock fn = (FnCreateBlock)Marshal.GetDelegateForFunctionPointer(
                    Slot(pFm, SlotCreateBlock), typeof(FnCreateBlock));
                IntPtr pFeat;
                int hr = fn(pFm, pt1, pt2, out pFeat);
                Diagnostics.Log(string.Format("CreateBlock hr=0x{0:X} ptr=0x{1:X}", hr, pFeat.ToInt64()));
                if (hr == 0)
                {
                    if (pFeat != IntPtr.Zero) Marshal.Release(pFeat);
                    return true;
                }
                return false;
            }
            finally
            {
                Marshal.Release(pFm);
            }
        }

        /// <summary>
        /// 在当前设计环境原点 (0,0,0) 建立 长(lxMm)×宽(lyMm)×高(lzMm) mm 的长方体。
        /// 长→X 轴，宽→Y 轴，高→Z 轴。
        /// 策略（进程内插件版）：
        ///   1. 零崩溃快路径：枚举场景中现有零件，从末尾向前直接尝试 CreateBlock
        ///      （不调用 CreatePart；在服务器健康时可行）；
        ///   1.5. 进程内 CreatePart+CreateBlock：vtable 直调 CAXA 真实对象
        ///      （CreatePart 槽位 9，签名与 FnGetObj 一致），无需子进程；
        ///   2. 兜底路径：STA PowerShell 子进程（CreatePart 新零件 + CreateBlock），
        ///      等待期间必须泵消息（DoEvents），否则进程内会堵死 CAXA 主线程，
        ///      导致子进程的 COM 回调死锁；
        ///   3. 仍失败：判定服务器 ICAPI 状态已损坏，提示重启 CAXA 3D。
        /// </summary>
        public static void CreateCubeAtOrigin(CaxaSession session, double lxMm, double lyMm, double lzMm)
        {
            double lx = lxMm * MmToMeter;
            double ly = lyMm * MmToMeter;
            double lz = lzMm * MmToMeter;
            Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                "CreateCube: L(X)={0} W(Y)={1} H(Z)={2} mm", lxMm, lyMm, lzMm));

            IntPtr pScene = GetOrCreateSceneDoc(session);
            try
            {
                // ---- 步骤 1：零崩溃快路径（不调用 CreatePart）----
                if (TryCreateBlockOnAnyPart(pScene, lx, ly, lz))
                    return;
                Diagnostics.Log("step1: no part accepted CreateBlock");

                // ---- 步骤 1.5：进程内 CreatePart + CreateBlock ----
                Diagnostics.Log("step1.5: in-process CreatePart+CreateBlock");
                if (TryCreatePartAndBlockInProcess(pScene, lx, ly, lz))
                    return;
                Diagnostics.Log("step1.5: in-process CreatePart+CreateBlock failed");

                // ---- 步骤 2：兜底 STA PowerShell 子进程（非阻塞等待）----
                Diagnostics.Log("step2: falling back to STA powershell child process");
                if (CreateCubeViaPowerShellChild(lx, ly, lz))
                    return;
            }
            finally
            {
                Marshal.Release(pScene);
            }

            // ---- 步骤 3：服务器 ICAPI 状态已损坏 ----
            throw new InvalidOperationException(
                "无法在零件上生成块特征。子进程路径也失败，ICAPI 状态可能已损坏，" +
                "请重启 CAXA 3D 后重试（详情见界面日志与 %TEMP%\\CaxaCreateCube_result.log）。");
        }

        /// <summary>
        /// 进程内 CreatePart + CreateBlock。
        /// CreatePart 槽位 9，签名与 FnGetObj 一致：(self, out IntPtr)。
        /// vtable 直调打到 CAXA 真实对象（无跨进程代理），CreatePart 后重新枚举
        /// 子元素找到新零件，再 CreateBlock。成功返回 true。
        /// </summary>
        private static bool TryCreatePartAndBlockInProcess(IntPtr pScene, double lx, double ly, double lz)
        {
            try
            {
                // 1. CreatePart（槽位 9，(self, out IntPtr) 形态，与 FnGetObj 同签名）
                IntPtr pNewPart;
                FnGetObj fnCreatePart = (FnGetObj)Marshal.GetDelegateForFunctionPointer(
                    Slot(pScene, SlotCreatePart), typeof(FnGetObj));
                int hr = fnCreatePart(pScene, out pNewPart);
                Diagnostics.Log(string.Format("CreatePart hr=0x{0:X} ptr=0x{1:X}", hr, pNewPart.ToInt64()));
                // CreatePart 返回的指针不直接使用（可能不是 IZPart 形态），
                // 改为枚举子元素找新零件（与已验证 PowerShell 脚本一致）。
                if (pNewPart != IntPtr.Zero) Marshal.Release(pNewPart);
                if (hr != 0) return false;

                // 2. 重新枚举子元素，找到新零件
                List<IntPtr> parts = FindAllParts(pScene);
                Diagnostics.Log("after CreatePart, parts found: " + parts.Count);
                try
                {
                    // 新零件在末尾，从最后往前尝试 CreateBlock
                    for (int i = parts.Count - 1; i >= 0; i--)
                    {
                        if (TryCreateBlockOnPart(parts[i], lx, ly, lz))
                            return true;
                    }
                    return false;
                }
                finally
                {
                    foreach (IntPtr p in parts) Marshal.Release(p);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("in-process CreatePart+Block EX: " + ex);
                return false;
            }
        }

        /// <summary>枚举场景所有零件并从末尾向前逐一尝试建块；成功返回 true。</summary>
        private static bool TryCreateBlockOnAnyPart(IntPtr pScene, double lx, double ly, double lz)
        {
            List<IntPtr> parts = FindAllParts(pScene);
            Diagnostics.Log("parts found: " + parts.Count);
            try
            {
                // 新零件通常在末尾，从最后一个往前尝试
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    if (TryCreateBlockOnPart(parts[i], lx, ly, lz))
                        return true;
                }
                return false;
            }
            finally
            {
                foreach (IntPtr p in parts) Marshal.Release(p);
            }
        }

        /// <summary>
        /// 兜底方案：读取内嵌的 PowerShell 脚本（与已验证探针完全一致），
        /// 用 powershell.exe -STA 子进程执行并检查标记文件。尺寸（米）通过命令行参数传入。
        /// 子进程内不做任何 Marshal.Release（实测清理阶段会 0xC0000005 崩溃），
        /// 建块成功后立即写标记文件，因此即使子进程在收尾时崩溃也能正确判定成功。
        /// </summary>
        private static bool CreateCubeViaPowerShellChild(double lx, double ly, double lz)
        {
            try
            {
                string resName = typeof(CaxaAutomation).Namespace + ".CreateCubePs1.ps1";
                string script;
                using (Stream stream = typeof(CaxaAutomation).Assembly.GetManifestResourceStream(resName))
                {
                    if (stream == null)
                        throw new InvalidOperationException("内嵌脚本缺失: " + resName);
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        script = reader.ReadToEnd();
                    }
                }
                string path = Path.Combine(Path.GetTempPath(), "CaxaCreateCube.ps1");
                File.WriteAllText(path, script, new UTF8Encoding(false));
                string markerFile = Path.Combine(Path.GetTempPath(), "CaxaCreateCube_marker.txt");
                string childLog = Path.Combine(Path.GetTempPath(), "CaxaCreateCube_result.log");
                try { File.Delete(markerFile); } catch { }
                try { File.Delete(childLog); } catch { }
                Diagnostics.Log("fallback script: " + path);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe");
                string argLx = lx.ToString("R", CultureInfo.InvariantCulture);
                string argLy = ly.ToString("R", CultureInfo.InvariantCulture);
                string argLz = lz.ToString("R", CultureInfo.InvariantCulture);
                psi.Arguments = "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + path +
                    "\" -Lx " + argLx + " -Ly " + argLy + " -Lz " + argLz;
                psi.UseShellExecute = false;
                // 不重定向 stdout/stderr：
                //   1) ReadToEnd() 会阻塞调用线程；
                //   2) 进程内插件运行在 CAXA 主线程（STA），子进程的 COM 回调
                //      （GetActiveObject / CreatePart / CreateBlock）必须由该 STA 线程
                //      泵消息才能完成。若此处同步阻塞，子进程的 COM 调用永远收不到响应，
                //      子进程不退出 → 本线程不返回 → 双方死锁，CAXA 卡死。
                //   诊断输出走子进程自己写入的 result.log（childLog）。
                psi.CreateNoWindow = true;

                using (Process p = Process.Start(psi))
                {
                    // 关键：等待期间持续泵消息，让 CAXA 主线程能处理子进程的 COM 回调。
                    DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                    while (!p.HasExited && DateTime.UtcNow < deadline)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        System.Threading.Thread.Sleep(50);
                    }

                    if (!p.HasExited)
                    {
                        try { p.Kill(); } catch { }
                        Diagnostics.Log("fallback timeout (killed child)");
                        return false;
                    }

                    Diagnostics.Log("fallback exit=" + p.ExitCode + " (0x" + p.ExitCode.ToString("X") + ")");

                    // 读子进程日志（诊断用，子进程自己写的 result.log）
                    try
                    {
                        if (File.Exists(childLog))
                        {
                            string childOut = File.ReadAllText(childLog);
                            if (childOut.Length > 0)
                                Diagnostics.Log("fallback childlog:\r\n" + childOut.TrimEnd());
                        }
                    }
                    catch { }

                    // 以标记文件为准（子进程崩溃于清理阶段时退出码不可信）
                    if (File.Exists(markerFile))
                    {
                        string marker = File.ReadAllText(markerFile).Trim();
                        Diagnostics.Log("fallback marker: " + marker);
                        return marker == "BLOCK CREATED";
                    }
                    Diagnostics.Log("fallback marker missing");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("fallback EX: " + ex);
                return false;
            }
        }
    }
}
