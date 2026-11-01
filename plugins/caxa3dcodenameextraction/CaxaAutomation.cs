using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 进程内 CAXA 3D (IRONCAD 内核) 会话。
    /// Z 系列接口对象是“单 IDispatch 多表面”：QueryInterface 任何接口均成功，
    /// 但 IDispatch 只认默认接口成员，因此必须保存原始接口指针并走 vtable 直调。
    /// </summary>
    internal sealed class CaxaSession : IDisposable
    {
        public object AppObject;   // 站点提供的 IZBaseApp RCW（保活）
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

    /// <summary>单个目标字段的读取结果。</summary>
    internal sealed class FieldResult
    {
        public string Value;    // null = 未找到
        public string Source;   // 来源描述（用于日志/验证）
    }

    /// <summary>选中元素的自定义属性读取结果。</summary>
    internal sealed class ElementCustomProps
    {
        public string DocName = "";
        public string ElementName = "";
        public string ElementKind = "元素";   // 零件 / 装配体 / 元素
        /// <summary>是否命中了有效的选中零部件（false = 未选中，调用方不应展示/写回）。</summary>
        public bool HasElement = false;
        /// <summary>目标字段名 -> 读取结果（值 + 来源）。</summary>
        public Dictionary<string, FieldResult> Fields = new Dictionary<string, FieldResult>();
    }

    /// <summary>
    /// CAXA 3D COM 自动化封装（进程内 vtable 直调版）。
    /// vtable 槽位号取自类型库 FUNCDESC（x64 下偏移 = 槽位 × 8）。
    ///   IZBaseApp:        ActiveDoc = 11
    ///   IZDoc:            get_Name = 11
    ///   IZSceneDoc:       get_SelectionMgr = 27
    ///   IZSelectionMgr:   GetSelectedElements = 10, SelectionsAvailable = 17,
    ///                     GetSelectedElementsZArray = 19
    ///   IZArray:          Count = 7, Get = 9
    ///   IZElement:        get_Type = 7, get_Name = 9, get_CustomPropMgr = 24,
    ///                     GetCustomPropManager = 42
    ///   IZCustomPropMgr:  IsSharedMgr = 7, GetValueByName = 9,
    ///                     SetValueByName = 10, RemoveByName = 13
    ///
    /// 已知兼容性问题（2026-10 实测）：部分 CAXA 3D 安装的内嵌类型库对
    /// IZElement::CustomPropMgr 的 [out,retval] 描述多了一层间接，因此获取共享
    /// CustomPropMgr 优先走 GetCustomPropManager(selector)+IsSharedMgr，
    /// 失败再回退 CustomPropMgr(vbShared=TRUE)。
    /// </summary>
    internal static class CaxaAutomation
    {
        // ---- 接口 GUID ----
        private static readonly Guid IID_IZBaseApp = new Guid("A5B9AF7D-228B-4ED1-BA42-D51318F7EA20");
        private static readonly Guid IID_IZSceneDoc = new Guid("3845C6DA-89D6-43F7-8B10-4BE36E0E758B");
        private static readonly Guid IID_IZDoc = new Guid("408F2326-F171-443B-A9EF-4AB250A2B1A8");
        private static readonly Guid IID_IZElement = new Guid("69EF5583-A26D-4913-A2F6-DBFF73E1B8A3");
        private static readonly Guid IID_IZPart = new Guid("F20F2411-05C4-4CA6-B2B4-5DB29531EDD7");
        private static readonly Guid IID_IZAssembly = new Guid("FF7C2F47-F21F-4225-9027-58E66CC01860");

        // ---- vtable 槽位（已通过本机 ICApiIronCAD.tlb 与内嵌 TLB 的 FUNCDESC 核对）----
        private const int SlotActiveDoc = 11;            // IZBaseApp
        private const int SlotDocGetName = 11;           // IZDoc
        private const int SlotSceneSelectionMgr = 27;    // IZSceneDoc::get_SelectionMgr -> IZSelectionMgr
        private const int SlotSelMgrGetSelectedElements = 10;       // -> VARIANT(SAFEARRAY)（仅 ZArray 不可用时回退）
        private const int SlotSelMgrGetSelectedElementsZArray = 19; // -> IZArray*（主路径）
        private const int SlotArrayCount = 7;            // IZArray::Count(int*)
        private const int SlotArrayGet = 9;              // IZArray::Get(int index, out IZElement*)
        private const int SlotElemGetType = 7;           // IZElement
        private const int SlotElemGetName = 9;           // IZElement
        private const int SlotElemGetCustomPropMgr = 24; // IZElement
        private const int SlotElemGetCustomPropManager = 42; // IZElement（类型库描述正确，优先使用）
        private const int SlotCpmIsSharedMgr = 7;        // IZCustomPropMgr
        private const int SlotCpmGetValueByName = 9;     // IZCustomPropMgr
        private const int SlotCpmSetValueByName = 10;    // IZCustomPropMgr
        private const int SlotCpmRemoveByName = 13;       // IZCustomPropMgr
        private const int SlotDocSave = 9;                // IZDoc::Save
        private const int SlotElemSetName = 10;           // IZElement::set_Name
        private const int SlotPartGetExtLinkedInfo = 17;  // IZPart::GetExternallyLinkedInfo
        private const int SlotPartRegenerate = 18;        // IZPart::Regenerate()
        private const int SlotAsmGetExtLinkedInfo = 21;   // IZAssembly::GetExternallyLinkedInfo
        private const int SlotAsmRegenerateParts = 17;    // IZAssembly::RegenerateParts(VARIANT_BOOL)
        private const int SlotSceneRegenerateParts = 21;  // IZSceneDoc::RegenerateParts(VARIANT_BOOL)
        private const int SlotSceneUpdate = 36;           // IZSceneDoc::Update()
        private const int SlotSceneUpdateGraphics = 83;   // IZSceneDoc::UpdateGraphics(UInt32)
        private const int SlotSceneUpdateBrowser = 84;    // IZSceneDoc::UpdateBrowser(UInt32)

        // ZIronCADApp 属性在默认接口上的 DISPID
        private const int DispidZIronCADApp = 0x2A;

        private const int HR_NOINTERFACE = unchecked((int)0x80004002);
        private const int HR_FAIL = unchecked((int)0x80004005);

        private const short VT_BSTR = 8;

        /// <summary>需要在界面上展示的四个目标字段（自定义属性精确名）。</summary>
        public static readonly string[] TargetFields = { "SC图号", "SC名称", "SC规格", "SC材料" };

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
        private delegate int FnGetBstr(IntPtr self, out IntPtr pbstr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetInt(IntPtr self, out int value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetCustomPropMgr(IntPtr self, short vbShared, out IntPtr ppMgr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetCustomPropManager(IntPtr self, int selector, out IntPtr ppMgr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetShort(IntPtr self, out short value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnArrayGet(IntPtr self, int index, out IntPtr ppElem);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetVariantResult(IntPtr self, IntPtr pvarResult);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnBstrArg(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string bstrName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetValueByName(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pvarValue);

        // SetValueByName(BSTR, VARIANT) 的 VARIANT 按值传递；x64 下 >8 字节结构
        // 实为“指向调用方临时副本的指针”，因此用 IntPtr 声明与原生 ABI 一致。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetValueByName(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pvarValue);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnNoArg(IntPtr self);

        /// <summary>单参数 VARIANT_BOOL（short）方法，如 RegenerateParts(vbAllParts)。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnShortArg(IntPtr self, short value);

        /// <summary>单参数 UInt32 方法，如 UpdateGraphics/UpdateBrowser(options)。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnUIntArg(IntPtr self, uint value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetBstrProp(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string value);

        /// <summary>GetExternallyLinkedInfo(VARIANT_BOOL* linked, BSTR* retval)。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetLinkedInfo(IntPtr self, out short linked, out IntPtr pbstrName);

        [DllImport("oleaut32.dll")] private static extern int VariantClear(IntPtr pvarg);
        [DllImport("oleaut32.dll")] private static extern int SafeArrayGetLBound(IntPtr psa, int nDim, out int pLbound);
        [DllImport("oleaut32.dll")] private static extern int SafeArrayGetUBound(IntPtr psa, int nDim, out int pUbound);
        [DllImport("oleaut32.dll")] private static extern int SafeArrayGetElement(IntPtr psa, ref int rgIndices, out IntPtr pvData);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQuery(IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

        /// <summary>
        /// 调用前探测：vtable 槽位上的函数指针必须落在已提交的可执行内存内。
        /// 用于识别 IUnknown-only 短 vtable 代理（其越界槽位读出的是垃圾指针，
        /// 直接调用会导致进程崩溃 —— 实测教训）。
        /// </summary>
        private static bool LooksCallable(IntPtr fn)
        {
            if (fn == IntPtr.Zero) return false;
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery(fn, out mbi,
                    (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero)
                return false;
            const uint MEM_COMMIT = 0x1000;
            return mbi.State == MEM_COMMIT && (mbi.Protect & 0xF0) != 0; // PAGE_EXECUTE*
        }

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
        /// 调用单参数 VARIANT_BOOL 方法（如 IZAssembly::RegenerateParts）。
        /// 槽位函数指针无效时返回 E_FAIL，不抛异常。
        /// </summary>
        private static int TryInvokeShortArg(IntPtr pIface, int slot, short value, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效（短 vtable 代理）");
                    return HR_FAIL;
                }
                FnShortArg f = (FnShortArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnShortArg));
                int hr = f(pIface, value);
                Diagnostics.Trace(string.Format("{0} hr=0x{1:X}", what, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        /// <summary>
        /// 调用单参数 UInt32 方法（如 IZSceneDoc::UpdateBrowser）。
        /// 槽位函数指针无效时返回 E_FAIL，不抛异常。
        /// </summary>
        private static int TryInvokeUIntArg(IntPtr pIface, int slot, uint value, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效（短 vtable 代理）");
                    return HR_FAIL;
                }
                FnUIntArg f = (FnUIntArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnUIntArg));
                int hr = f(pIface, value);
                Diagnostics.Trace(string.Format("{0} hr=0x{1:X}", what, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        /// <summary>
        /// 刷新设计场景界面：IZSceneDoc::Update + UpdateGraphics + UpdateBrowser。
        /// 实测：改名后仅 IZPart::Regenerate(hr=0) 不足以刷新设计树缓存的节点显示名，
        /// 人工“重新生成”命令还会触发 DocModifyNotify ×N + 视图重绘；
        /// 这里补齐场景级刷新，让设计树/图形区同步显示新零件名。静默执行，失败仅记日志。
        /// </summary>
        private static void RefreshSceneUi(IntPtr pScene)
        {
            try
            {
                IntPtr fn = Slot(pScene, SlotSceneUpdate);
                if (LooksCallable(fn))
                {
                    FnNoArg f = (FnNoArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnNoArg));
                    int hr = f(pScene);
                    Diagnostics.Trace(string.Format("IZSceneDoc.Update hr=0x{0:X}", hr));
                }
                TryInvokeUIntArg(pScene, SlotSceneUpdateGraphics, 0, "IZSceneDoc.UpdateGraphics");
                TryInvokeUIntArg(pScene, SlotSceneUpdateBrowser, 0, "IZSceneDoc.UpdateBrowser");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("RefreshSceneUi EX: " + ex.Message);
            }
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

        #endregion

        #region 会话

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
            Diagnostics.Trace(string.Format("validate ActiveDoc hr=0x{0:X}", hr));
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
        /// 进程内通常恒真；保留该检查用于异常兜底。
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

        #endregion

        #region 读取辅助

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

        /// <summary>读取接口上的 BSTR 属性（get_Name 形态，带槽位防护）。失败返回 null。</summary>
        private static string TryReadBstrProp(IntPtr pIface, int slot, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效（短 vtable 代理）");
                    return null;
                }
                FnGetBstr f = (FnGetBstr)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetBstr));
                IntPtr pBstr;
                int hr = f(pIface, out pBstr);
                Diagnostics.Trace(string.Format("{0} hr=0x{1:X}", what, hr));
                if (hr != 0 || pBstr == IntPtr.Zero) return null;
                string s = Marshal.PtrToStringBSTR(pBstr);
                Marshal.FreeBSTR(pBstr);
                return s;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return null;
            }
        }

        /// <summary>eZElementType -> 中文类别名。</summary>
        private static string MapElementKind(int t)
        {
            switch (t)
            {
                case 1: return "零件";
                case 2: return "装配体";
                case 20: return "钣金零件";
                case 25: return "刻面零件";
                case 28: return "线框零件";
                case 29: return "轮廓零件";
                default: return "元素(type=" + t + ")";
            }
        }

        /// <summary>读取接口上的 int 输出属性（get_Type 形态，带槽位防护）。失败返回 false。</summary>
        private static bool TryReadIntProp(IntPtr pIface, int slot, string what, out int value)
        {
            value = 0;
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效（短 vtable 代理）");
                    return false;
                }
                FnGetInt f = (FnGetInt)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetInt));
                int hr = f(pIface, out value);
                Diagnostics.Trace(string.Format("{0} hr=0x{1:X} value={2}", what, hr, value));
                return hr == 0;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return false;
            }
        }

        /// <summary>获取元素上的 CustomPropMgr（IZElement::get_CustomPropMgr，带槽位防护）。</summary>
        private static IntPtr GetCustomPropMgr(IntPtr pElem, short vbShared, string tag)
        {
            IntPtr fn = Slot(pElem, SlotElemGetCustomPropMgr);
            if (!LooksCallable(fn))
            {
                Diagnostics.Log("get_CustomPropMgr(" + tag + ") 跳过：函数指针无效");
                return IntPtr.Zero;
            }
            FnGetCustomPropMgr f = (FnGetCustomPropMgr)Marshal.GetDelegateForFunctionPointer(
                fn, typeof(FnGetCustomPropMgr));
            IntPtr pMgr;
            int hr = f(pElem, vbShared, out pMgr);
            Diagnostics.Trace(string.Format("get_CustomPropMgr({0}, vbShared={1}) hr=0x{2:X} ptr=0x{3:X}",
                    tag, vbShared, hr, pMgr.ToInt64()));
            if (hr != 0) return IntPtr.Zero;
            return pMgr;
        }

        /// <summary>IZElement::GetCustomPropManager(int)（槽位 42，类型库描述正确）。</summary>
        private static IntPtr TryGetCustomPropManager(IntPtr pElem, int selector, string tag)
        {
            try
            {
                IntPtr fn = Slot(pElem, SlotElemGetCustomPropManager);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log("GetCustomPropManager(" + tag + ") 跳过：函数指针无效");
                    return IntPtr.Zero;
                }
                FnGetCustomPropManager f = (FnGetCustomPropManager)Marshal.GetDelegateForFunctionPointer(
                    fn, typeof(FnGetCustomPropManager));
                IntPtr pMgr;
                int hr = f(pElem, selector, out pMgr);
                Diagnostics.Trace(string.Format("GetCustomPropManager({0}, selector={1}) hr=0x{2:X} ptr=0x{3:X}",
                    tag, selector, hr, pMgr.ToInt64()));
                if (hr != 0) return IntPtr.Zero;
                return pMgr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("GetCustomPropManager(" + tag + ") EX: " + ex.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>IZCustomPropMgr::IsSharedMgr（槽位 7）。调用失败返回 false。</summary>
        private static bool TryIsSharedMgr(IntPtr pMgr, string tag)
        {
            try
            {
                IntPtr fn = Slot(pMgr, SlotCpmIsSharedMgr);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log("IsSharedMgr(" + tag + ") 跳过：函数指针无效");
                    return false;
                }
                FnGetShort f = (FnGetShort)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetShort));
                short shared;
                int hr = f(pMgr, out shared);
                Diagnostics.Trace(string.Format("IsSharedMgr({0}) hr=0x{1:X} value={2}", tag, hr, shared));
                return hr == 0 && shared != 0;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("IsSharedMgr(" + tag + ") EX: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 获取元素上的“共享”CustomPropMgr。
        /// 优先走 GetCustomPropManager(selector)+IsSharedMgr 校验；
        /// 都拿不到共享管理器时回退 get_CustomPropMgr(vbShared=TRUE)。
        /// </summary>
        private static IntPtr GetSharedCustomPropMgr(IntPtr pElem, string tag)
        {
            IntPtr firstFallback = IntPtr.Zero;
            foreach (int selector in new int[] { 0, 1 })
            {
                IntPtr pMgr = TryGetCustomPropManager(pElem, selector, tag);
                if (pMgr == IntPtr.Zero) continue;
                if (TryIsSharedMgr(pMgr, tag + ",selector=" + selector)) return pMgr;
                if (firstFallback == IntPtr.Zero) firstFallback = pMgr;
                else Marshal.Release(pMgr);
            }
            // 回退路径：旧属性 getter（多数机器可用）
            IntPtr pProp = GetCustomPropMgr(pElem, -1, tag);
            if (pProp != IntPtr.Zero)
            {
                if (firstFallback != IntPtr.Zero) Marshal.Release(firstFallback);
                return pProp;
            }
            if (firstFallback != IntPtr.Zero)
                Diagnostics.Log("GetSharedCustomPropMgr(" + tag + "): 两个主路径均未确认共享，" +
                    "使用 GetCustomPropManager 返回的第一个管理器");
            return firstFallback;
        }

        /// <summary>
        /// 通过 GetValueByName 按名读取属性值（带槽位防护）。
        /// 返回 true 表示调用成功（值为空字符串也属成功）。
        /// </summary>
        private static bool TryGetValueByName(IntPtr pMgr, string name, out string value)
        {
            value = null;
            IntPtr fn = Slot(pMgr, SlotCpmGetValueByName);
            if (!LooksCallable(fn))
            {
                Diagnostics.Log("GetValueByName(" + name + ") 跳过：函数指针无效");
                return false;
            }
            FnGetValueByName f = (FnGetValueByName)Marshal.GetDelegateForFunctionPointer(
                fn, typeof(FnGetValueByName));
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                int hr = f(pMgr, name, pVar);
                Diagnostics.Trace(string.Format("GetValueByName({0}) hr=0x{1:X}", name, hr));
                if (hr != 0) return false;
                value = VariantToString(pVar);
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("GetValueByName(" + name + ") EX: " + ex.Message);
                return false;
            }
            finally
            {
                VariantClear(pVar);
                Marshal.FreeCoTaskMem(pVar);
            }
        }

        /// <summary>把 VARIANT（x64，16 字节）转成显示字符串。</summary>
        private static string VariantToString(IntPtr pVar)
        {
            short vt = Marshal.ReadInt16(pVar);
            switch (vt)
            {
                case 0: return "";                    // VT_EMPTY
                case 1: return "";                    // VT_NULL
                case 2: return Marshal.ReadInt16(pVar, 8).ToString();   // VT_I2
                case 3: return Marshal.ReadInt32(pVar, 8).ToString();   // VT_I4
                case 8:                               // VT_BSTR
                    {
                        IntPtr pBstr = Marshal.ReadIntPtr(pVar, 8);
                        return pBstr == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(pBstr);
                    }
                case 11: return Marshal.ReadInt16(pVar, 8) != 0 ? "True" : "False"; // VT_BOOL
                case 4:  // VT_R4
                    {
                        byte[] b = new byte[4];
                        Marshal.Copy(pVar + 8, b, 0, 4);
                        return BitConverter.ToSingle(b, 0).ToString(CultureInfo.InvariantCulture);
                    }
                case 5:  // VT_R8
                    {
                        byte[] b = new byte[8];
                        Marshal.Copy(pVar + 8, b, 0, 8);
                        return BitConverter.ToDouble(b, 0).ToString(CultureInfo.InvariantCulture);
                    }
                case 7:  // VT_DATE
                    {
                        byte[] b = new byte[8];
                        Marshal.Copy(pVar + 8, b, 0, 8);
                        try { return DateTime.FromOADate(BitConverter.ToDouble(b, 0)).ToString("yyyy-MM-dd HH:mm:ss"); }
                        catch { return "<日期解析失败>"; }
                    }
                case 17: return Marshal.ReadByte(pVar, 8).ToString();   // VT_UI1
                case 18: return ((ushort)Marshal.ReadInt16(pVar, 8)).ToString();  // VT_UI2
                case 19: return (Marshal.ReadInt32(pVar, 8) & 0xFFFFFFFFL).ToString(); // VT_UI4
                case 20: return Marshal.ReadInt64(pVar, 8).ToString();  // VT_I8
                case 21: return Marshal.ReadInt64(pVar, 8).ToString();  // VT_UI8
                case 22: return Marshal.ReadInt32(pVar, 8).ToString();  // VT_INT
                case 23: return (Marshal.ReadInt32(pVar, 8) & 0xFFFFFFFFL).ToString(); // VT_UINT
                case 9:  return "<IDispatch>";        // VT_DISPATCH
                case 13: return "<IUnknown>";         // VT_UNKNOWN
                default:
                    if ((vt & 0x2000) != 0) return "<数组 vt=0x" + vt.ToString("X") + ">";
                    return "<vt=0x" + vt.ToString("X") + ">";
            }
        }

        /// <summary>
        /// 定位活动设计场景：ActiveDoc → IZSceneDoc。
        /// 调用方负责 Release 返回的非零指针；无活动文档时两者均为 Zero。
        /// </summary>
        private static void OpenActiveScene(CaxaSession session,
            out IntPtr pDoc, out IntPtr pScene)
        {
            pDoc = IntPtr.Zero; pScene = IntPtr.Zero;
            int hr = TryCallGetObj(session.BaseApp, SlotActiveDoc, out pDoc);
            Diagnostics.Trace(string.Format("ActiveDoc hr=0x{0:X} ptr=0x{1:X}", hr, pDoc.ToInt64()));
            if (hr != 0)
                throw new InvalidOperationException(string.Format("读取活动文档失败 hr=0x{0:X}", hr));
            if (pDoc == IntPtr.Zero) return; // 无活动文档
            pScene = QIRequired(pDoc, IID_IZSceneDoc, "IZSceneDoc");
        }

        /// <summary>零部件类元素类型（零件/装配体/钣金/刻面/线框/轮廓）。</summary>
        private static bool IsPartKind(int t)
        {
            return t == 1 || t == 2 || t == 20 || t == 25 || t == 28 || t == 29;
        }

        /// <summary>
        /// 获取用户当前选中的第一个“零部件类”元素（IZElement 指针，调用方负责 Release）。
        /// 路径（全部 vtable 直调）：IZSceneDoc::get_SelectionMgr(27)
        ///   A) SelectionsAvailable(17) → GetSelectedElementsZArray(19) → IZArray::Count(7)/Get(9)
        ///   B) 回退：GetSelectedElements(10) → VARIANT(SAFEARRAY of VT_DISPATCH/VT_UNKNOWN)
        /// 逐个按类型过滤，面/边/特征等非零部件选择被忽略。无有效选中或接口不可用时返回 Zero。
        /// </summary>
        private static IntPtr GetSelectedElement(IntPtr pScene, string tag)
        {
            IntPtr fn = Slot(pScene, SlotSceneSelectionMgr);
            if (!LooksCallable(fn))
            {
                Diagnostics.Log("get_SelectionMgr(" + tag + ") 跳过：函数指针无效");
                return IntPtr.Zero;
            }
            FnGetObj f = (FnGetObj)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetObj));
            IntPtr pSelMgr;
            int hr = f(pScene, out pSelMgr);
            Diagnostics.Trace(string.Format("get_SelectionMgr({0}) hr=0x{1:X} ptr=0x{2:X}", tag, hr, pSelMgr.ToInt64()));
            if (hr != 0 || pSelMgr == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                // 主路径：ZArray（元素以类型化指针返回，代理完整）
                fn = Slot(pSelMgr, SlotSelMgrGetSelectedElementsZArray);
                if (LooksCallable(fn))
                {
                    FnGetObj za = (FnGetObj)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetObj));
                    IntPtr pArr;
                    hr = za(pSelMgr, out pArr);
                    Diagnostics.Trace(string.Format("GetSelectedElementsZArray({0}) hr=0x{1:X} ptr=0x{2:X}",
                        tag, hr, pArr.ToInt64()));
                    if (hr == 0 && pArr != IntPtr.Zero)
                    {
                        try
                        {
                            // ZArray 可用时以它为准（即使没选中零部件也不再重复抓取）
                            return PickPartFromZArray(pArr, tag);
                        }
                        finally
                        {
                            Marshal.Release(pArr);
                        }
                    }
                }

                // 回退路径：仅当 ZArray 接口不可用/调用失败时才尝试 SAFEARRAY
                return GetSelectedElementViaSafeArray(pSelMgr, tag);
            }
            finally
            {
                Marshal.Release(pSelMgr);
            }
        }

        /// <summary>遍历 IZArray，返回第一个零部件类元素（IZElement 指针，调用方负责 Release）。</summary>
        private static IntPtr PickPartFromZArray(IntPtr pArr, string tag)
        {
            IntPtr fn = Slot(pArr, SlotArrayCount);
            if (!LooksCallable(fn))
            {
                Diagnostics.Log("IZArray.Count(" + tag + ") 跳过：函数指针无效");
                return IntPtr.Zero;
            }
            FnGetInt cnt = (FnGetInt)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetInt));
            int count;
            int hr = cnt(pArr, out count);
            Diagnostics.Trace(string.Format("IZArray.Count({0}) hr=0x{1:X} value={2}", tag, hr, count));
            if (hr != 0 || count <= 0) return IntPtr.Zero;
            if (count > 1000) count = 1000;

            IntPtr fnGet = Slot(pArr, SlotArrayGet);
            if (!LooksCallable(fnGet))
            {
                Diagnostics.Log("IZArray.Get(" + tag + ") 跳过：函数指针无效");
                return IntPtr.Zero;
            }
            FnArrayGet get = (FnArrayGet)Marshal.GetDelegateForFunctionPointer(fnGet, typeof(FnArrayGet));
            for (int i = 0; i < count; i++)
            {
                IntPtr pRaw;
                hr = get(pArr, i, out pRaw);
                Diagnostics.Trace(string.Format("IZArray.Get({0},{1}) hr=0x{2:X} ptr=0x{3:X}", tag, i, hr, pRaw.ToInt64()));
                if (hr != 0 || pRaw == IntPtr.Zero) continue;
                IntPtr pElem = FilterPartElement(pRaw, i, tag);
                if (pElem != IntPtr.Zero) return pElem;
            }
            return IntPtr.Zero;
        }

        /// <summary>QI(IZElement) + 零部件类型过滤。命中返回 IZElement 指针（调用方负责 Release），
        /// 未命中释放 pRaw 并返回 Zero。</summary>
        private static IntPtr FilterPartElement(IntPtr pRaw, int index, string tag)
        {
            try
            {
                IntPtr pElem;
                if (TryQI(pRaw, IID_IZElement, out pElem) != 0 || pElem == IntPtr.Zero)
                {
                    Diagnostics.Trace("selected[" + index + "](" + tag + "): QI(IZElement) 失败，跳过");
                    return IntPtr.Zero;
                }
                int etype;
                if (TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(sel)", out etype)
                    && IsPartKind(etype))
                    return pElem; // 命中：所有权移交调用方
                Diagnostics.Trace("selected[" + index + "](" + tag + ") type=" + etype + " 非零部件，跳过");
                Marshal.Release(pElem);
                return IntPtr.Zero;
            }
            finally
            {
                Marshal.Release(pRaw);
            }
        }

        /// <summary>路径 B：GetSelectedElements → VARIANT(SAFEARRAY of VT_DISPATCH/VT_UNKNOWN)。</summary>
        private static IntPtr GetSelectedElementViaSafeArray(IntPtr pSelMgr, string tag)
        {
            IntPtr fn = Slot(pSelMgr, SlotSelMgrGetSelectedElements);
            if (!LooksCallable(fn))
            {
                Diagnostics.Trace("GetSelectedElements(" + tag + ") 跳过：函数指针无效");
                return IntPtr.Zero;
            }
            FnGetVariantResult f = (FnGetVariantResult)Marshal.GetDelegateForFunctionPointer(
                fn, typeof(FnGetVariantResult));
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                int hr = f(pSelMgr, pVar);
                short vt = Marshal.ReadInt16(pVar, 0);
                Diagnostics.Trace(string.Format("GetSelectedElements({0}) hr=0x{1:X} vt=0x{2:X}", tag, hr, vt));
                if (hr != 0) return IntPtr.Zero;
                // VT_ARRAY|VT_DISPATCH(0x2009) 或 VT_ARRAY|VT_UNKNOWN(0x200D)
                if ((vt & 0x2000) == 0 || (vt & 0xFFF) != 9 && (vt & 0xFFF) != 13) return IntPtr.Zero;
                IntPtr psa = Marshal.ReadIntPtr(pVar, 8);
                if (psa == IntPtr.Zero) return IntPtr.Zero;

                int lbound, ubound;
                if (SafeArrayGetLBound(psa, 1, out lbound) != 0 ||
                    SafeArrayGetUBound(psa, 1, out ubound) != 0) return IntPtr.Zero;
                Diagnostics.Trace(string.Format("selection safearray({0}) bounds {1}..{2}", tag, lbound, ubound));
                for (int i = lbound; i <= ubound; i++)
                {
                    IntPtr pRaw;
                    if (SafeArrayGetElement(psa, ref i, out pRaw) != 0 || pRaw == IntPtr.Zero) continue;
                    IntPtr pElem = FilterPartElement(pRaw, i, tag);
                    if (pElem != IntPtr.Zero) return pElem;
                }
                return IntPtr.Zero;
            }
            finally
            {
                VariantClear(pVar);
                Marshal.FreeCoTaskMem(pVar);
            }
        }

        #endregion

        #region 对外功能

        /// <summary>
        /// 一次 COM 往返探测：活动文档名（IZDoc::get_Name）与用户当前选中的零部件名。
        /// 无活动文档时两者均为 null；未选中有效零部件时 elemName 为 null。
        /// </summary>
        public static void ProbeSessionInfo(CaxaSession session, out string docName, out string elemName)
        {
            docName = null; elemName = null;
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            try
            {
                if (pDoc == IntPtr.Zero) return;
                IntPtr pZDoc;
                if (TryQI(pDoc, IID_IZDoc, out pZDoc) == 0 && pZDoc != IntPtr.Zero)
                {
                    try { docName = TryReadBstrProp(pZDoc, SlotDocGetName, "IZDoc.get_Name"); }
                    finally { Marshal.Release(pZDoc); }
                }
                IntPtr pElem = GetSelectedElement(pScene, "probe");
                if (pElem != IntPtr.Zero)
                {
                    try { elemName = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name"); }
                    finally { Marshal.Release(pElem); }
                }
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        public static ElementCustomProps ReadFirstElementCustomProps(CaxaSession session)
        {
            return ReadFirstElementCustomProps(session, TargetFields);
        }

        /// <summary>
        /// 读取用户当前选中的零部件（零件/装配体等）的自定义属性。
        /// 路径：SelectionMgr 拿选中元素 → get_Type/get_Name → 共享 CustomPropMgr → GetValueByName × N。
        /// 未选中有效零部件时不读取，直接返回（HasElement=false）。
        /// fields 为 null 时读取四个默认目标字段。
        /// </summary>
        public static ElementCustomProps ReadFirstElementCustomProps(CaxaSession session,
            IEnumerable<string> fields)
        {
            if (fields == null) fields = TargetFields;
            // 去重并保持顺序
            var fieldList = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in fields)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                if (seen.Add(f)) fieldList.Add(f);
            }
            ElementCustomProps result = new ElementCustomProps();

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pDoc == IntPtr.Zero)
                throw new InvalidOperationException(
                    "CAXA 3D 当前没有活动文档。请先打开一个设计场景（含零件/装配体）后再读取。");

            try
            {
                // 文档名（IZDoc::get_Name，失败不影响主流程）
                IntPtr pZDoc;
                if (TryQI(pDoc, IID_IZDoc, out pZDoc) == 0 && pZDoc != IntPtr.Zero)
                {
                    try { result.DocName = TryReadBstrProp(pZDoc, SlotDocGetName, "IZDoc.get_Name") ?? ""; }
                    finally { Marshal.Release(pZDoc); }
                }
                Diagnostics.Trace("doc name: " + result.DocName);

                IntPtr pElem = GetSelectedElement(pScene, "read");
                if (pElem == IntPtr.Zero)
                {
                    Diagnostics.Log("read: 未选中零部件，跳过读取");
                    return result; // HasElement=false
                }
                try
                {
                    result.HasElement = true;

                    int elemType;
                    if (TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type", out elemType))
                        result.ElementKind = MapElementKind(elemType);

                    result.ElementName = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name") ?? "";
                    Diagnostics.Log("selected element: [" + result.ElementKind + "] " + result.ElementName);

                    // 目标字段实测都在共享 CustomPropMgr 上
                    IntPtr pMgr = GetSharedCustomPropMgr(pElem, "shared");
                    if (pMgr == IntPtr.Zero)
                        throw new InvalidOperationException("获取共享 CustomPropMgr 失败");
                    try
                    {
                        foreach (string field in fieldList)
                        {
                            FieldResult fr = new FieldResult();
                            string v;
                            if (TryGetValueByName(pMgr, field, out v))
                            {
                                fr.Value = v;
                                fr.Source = "GetValueByName(shared)";
                            }
                            else
                            {
                                fr.Source = "未找到";
                            }
                            result.Fields[field] = fr;
                            Diagnostics.Trace("field " + field + ": " +
                                (fr.Value == null ? "<未找到>" : "\"" + fr.Value + "\""));
                        }
                    }
                    finally
                    {
                        Marshal.Release(pMgr);
                    }
                }
                finally
                {
                    Marshal.Release(pElem);
                }
            }
            finally
            {
                Marshal.Release(pScene);
                Marshal.Release(pDoc);
            }
            return result;
        }

        /// <summary>
        /// 把给定字段的值写回用户当前选中零部件的共享 CustomPropMgr（SetValueByName）。
        /// 写入集合由调用方字典的键决定；全部成功才返回，否则抛异常。
        /// </summary>
        public static void WriteFirstElementCustomProps(CaxaSession session,
            IDictionary<string, string> values)
        {
            if (values == null || values.Count == 0)
                throw new ArgumentException("没有需要写入的属性。", "values");
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pDoc == IntPtr.Zero)
                throw new InvalidOperationException(
                    "CAXA 3D 当前没有活动文档。请先打开一个设计场景后再反填。");

            try
            {
                IntPtr pElem = GetSelectedElement(pScene, "write");
                if (pElem == IntPtr.Zero)
                    throw new InvalidOperationException(
                        "未在 CAXA 3D 中选中任何零件/装配体，请先选中目标零部件。");
                try
                {
                    IntPtr pMgr = GetSharedCustomPropMgr(pElem, "shared");
                    if (pMgr == IntPtr.Zero)
                        throw new InvalidOperationException("获取共享 CustomPropMgr 失败");
                    try
                    {
                        int okCount = 0;
                        int total = 0;
                        foreach (KeyValuePair<string, string> kv in values)
                        {
                            total++;
                            if (TrySetValueByName(pMgr, kv.Key, kv.Value ?? "")) okCount++;
                        }
                        Diagnostics.Log(string.Format("write: done, {0}/{1} fields written",
                            okCount, total));
                        if (okCount < total)
                            throw new InvalidOperationException("部分字段写入失败（详见日志）。");
                    }
                    finally
                    {
                        Marshal.Release(pMgr);
                    }
                }
                finally
                {
                    Marshal.Release(pElem);
                }
            }
            finally
            {
                Marshal.Release(pScene);
                Marshal.Release(pDoc);
            }
        }

        /// <summary>通过 SetValueByName 按名写入字符串属性值（带槽位防护）。</summary>
        private static bool TrySetValueByName(IntPtr pMgr, string name, string value)
        {
            IntPtr fn = Slot(pMgr, SlotCpmSetValueByName);
            if (!LooksCallable(fn))
            {
                Diagnostics.Log("SetValueByName(" + name + ") 跳过：函数指针无效");
                return false;
            }
            FnSetValueByName f = (FnSetValueByName)Marshal.GetDelegateForFunctionPointer(
                fn, typeof(FnSetValueByName));
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                // VT_BSTR 字符串值（vt 在偏移 0，数据指针在偏移 8）
                Marshal.WriteInt16(pVar, 0, VT_BSTR);
                Marshal.WriteIntPtr(pVar, 8, Marshal.StringToBSTR(value ?? ""));
                int hr = f(pMgr, name, pVar);
                Diagnostics.Trace(string.Format("SetValueByName({0}) hr=0x{1:X}", name, hr));
                if (hr == 0) return true;
                // 本版本对空字符串返回 E_INVALIDARG：改用 RemoveByName 实现“清空”语义
                if ((value ?? "").Length == 0)
                    return TryRemoveByName(pMgr, name);
                return false;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SetValueByName(" + name + ") EX: " + ex.Message);
                return false;
            }
            finally
            {
                VariantClear(pVar);
                Marshal.FreeCoTaskMem(pVar);
            }
        }

        /// <summary>IZCustomPropMgr::RemoveByName（槽位 13），用于空值“清空”语义。</summary>
        private static bool TryRemoveByName(IntPtr pMgr, string name)
        {
            IntPtr fn = Slot(pMgr, SlotCpmRemoveByName);
            if (!LooksCallable(fn))
            {
                Diagnostics.Log("RemoveByName(" + name + ") 跳过：函数指针无效");
                return false;
            }
            try
            {
                FnBstrArg rm = (FnBstrArg)Marshal.GetDelegateForFunctionPointer(
                    fn, typeof(FnBstrArg));
                int hr = rm(pMgr, name);
                Diagnostics.Trace(string.Format("RemoveByName({0}) hr=0x{1:X}（空值回退）", name, hr));
                return hr == 0;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("RemoveByName(" + name + ") EX: " + ex.Message);
                return false;
            }
        }

        #region 重命名与外部链接

        /// <summary>选中零部件信息（连接状态显示与重命名流程使用）。</summary>
        internal sealed class SelectedPartInfo
        {
            public bool HasElement;
            public string ElementName = "";
            public string ElementKind = "元素";
            public int ElementType;
            public bool ExternallyLinked;
            public string LinkedFile = "";
        }

        /// <summary>读取当前选中零部件：类型、名称、外部链接文件。</summary>
        public static SelectedPartInfo GetSelectedPartInfo(CaxaSession session)
        {
            SelectedPartInfo info = new SelectedPartInfo();
            if (session == null) return info;

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            try
            {
                if (pDoc == IntPtr.Zero) return info;
                IntPtr pElem = GetSelectedElement(pScene, "info");
                if (pElem == IntPtr.Zero) return info;
                try
                {
                    info.HasElement = true;
                    int t;
                    if (TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(info)", out t))
                    {
                        info.ElementType = t;
                        info.ElementKind = MapElementKind(t);
                    }
                    info.ElementName = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name(info)") ?? "";
                    TryReadExternalLink(pElem, info.ElementType, info);
                }
                finally
                {
                    Marshal.Release(pElem);
                }
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
            return info;
        }

        /// <summary>IZPart/IZAssembly::GetExternallyLinkedInfo → 外部链接 .ics 路径。</summary>
        private static void TryReadExternalLink(IntPtr pElem, int elemType, SelectedPartInfo info)
        {
            // 装配体走 IZAssembly，其余（零件/钣金/刻面/线框/轮廓等）一律尝试 IZPart
            Guid iid = elemType == 2 ? IID_IZAssembly : IID_IZPart;
            int slot = elemType == 2 ? SlotAsmGetExtLinkedInfo : SlotPartGetExtLinkedInfo;
            IntPtr pObj;
            if (TryQI(pElem, iid, out pObj) != 0 || pObj == IntPtr.Zero) return;
            try
            {
                IntPtr fn = Slot(pObj, slot);
                if (!LooksCallable(fn)) return;
                FnGetLinkedInfo f = (FnGetLinkedInfo)Marshal.GetDelegateForFunctionPointer(
                    fn, typeof(FnGetLinkedInfo));
                short linked;
                IntPtr pbstr;
                int hr = f(pObj, out linked, out pbstr);
                Diagnostics.Trace(string.Format("GetExternallyLinkedInfo(type={0}) hr=0x{1:X} linked={2}",
                    elemType, hr, linked));
                if (hr == 0 && pbstr != IntPtr.Zero)
                {
                    try { info.LinkedFile = Marshal.PtrToStringBSTR(pbstr) ?? ""; }
                    finally { Marshal.FreeBSTR(pbstr); }
                }
                info.ExternallyLinked = hr == 0 && linked != 0
                    && !string.IsNullOrEmpty(info.LinkedFile);
            }
            finally
            {
                Marshal.Release(pObj);
            }
        }

        /// <summary>把新零件名写回当前选中零部件（IZElement::set_Name）。</summary>
        public static void RenameSelectedElement(CaxaSession session, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("新零件名为空。", "newName");

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pDoc == IntPtr.Zero)
                throw new InvalidOperationException("CAXA 3D 当前没有活动文档。");

            try
            {
                IntPtr pElem = GetSelectedElement(pScene, "rename");
                if (pElem == IntPtr.Zero)
                    throw new InvalidOperationException("未在 CAXA 3D 中选中任何零件/装配体。");
                try
                {
                    IntPtr fn = Slot(pElem, SlotElemSetName);
                    if (!LooksCallable(fn))
                        throw new InvalidOperationException("IZElement::set_Name 函数指针无效。");
                    FnSetBstrProp f = (FnSetBstrProp)Marshal.GetDelegateForFunctionPointer(
                        fn, typeof(FnSetBstrProp));
                    int hr = f(pElem, newName);
                    Diagnostics.Log(string.Format("RenameSelectedElement: \"{0}\" hr=0x{1:X}", newName, hr));
                    if (hr != 0)
                        throw new InvalidOperationException(
                            string.Format("修改零件名失败 hr=0x{0:X}", hr));
                }
                finally
                {
                    Marshal.Release(pElem);
                }
            }
            finally
            {
                Marshal.Release(pScene);
                Marshal.Release(pDoc);
            }
        }

        /// <summary>保存活动文档（IZDoc::Save）。</summary>
        public static void SaveActiveDoc(CaxaSession session)
        {
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pScene != IntPtr.Zero) Marshal.Release(pScene);
            if (pDoc == IntPtr.Zero)
                throw new InvalidOperationException("CAXA 3D 当前没有活动文档。");
            try
            {
                IntPtr pZDoc = QIRequired(pDoc, IID_IZDoc, "IZDoc");
                try
                {
                    IntPtr fn = Slot(pZDoc, SlotDocSave);
                    if (!LooksCallable(fn))
                        throw new InvalidOperationException("IZDoc::Save 函数指针无效。");
                    FnNoArg f = (FnNoArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnNoArg));
                    int hr = f(pZDoc);
                    Diagnostics.Log(string.Format("SaveActiveDoc hr=0x{0:X}", hr));
                    if (hr != 0)
                        throw new InvalidOperationException(string.Format("保存文档失败 hr=0x{0:X}", hr));
                }
                finally
                {
                    Marshal.Release(pZDoc);
                }
            }
            finally
            {
                Marshal.Release(pDoc);
            }
        }

        /// <summary>
        /// 当前活动文档是否为可承载零部件设计场景的文档（QI IZSceneDoc 成功）。
        /// 用于在图纸关闭后等待焦点切回设计环境，避免对图纸文档执行重新生成。
        /// </summary>
        public static bool IsActiveSceneDocument(CaxaSession session)
        {
            if (session == null || session.BaseApp == IntPtr.Zero) return false;
            try
            {
                IntPtr pDoc;
                int hr = TryCallGetObj(session.BaseApp, SlotActiveDoc, out pDoc);
                if (hr != 0 || pDoc == IntPtr.Zero) return false;
                try
                {
                    IntPtr pScene;
                    int hrQi = TryQI(pDoc, IID_IZSceneDoc, out pScene);
                    bool ok = hrQi == 0 && pScene != IntPtr.Zero;
                    if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                    return ok;
                }
                finally
                {
                    Marshal.Release(pDoc);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 重新生成当前选中零部件（等价 CAXA 菜单命令“重新生成”），
        /// 用于让设计树上显示的名称与改名后的实际零件名同步。
        /// 零件走 IZPart::Regenerate(槽 18)；装配体走 IZAssembly::RegenerateParts(槽 17, vbAllParts=TRUE)；
        /// 选中已失效（改名/开关图纸后常见）时回退整场景 IZSceneDoc::RegenerateParts(槽 21)。
        /// 返回实际执行对象的中文描述；全部失败时抛异常。
        /// </summary>
        public static string RegenerateSelectedElement(CaxaSession session)
        {
            if (session == null)
                throw new InvalidOperationException("尚未连接 CAXA 3D。");

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pDoc == IntPtr.Zero)
                throw new InvalidOperationException("CAXA 3D 当前没有活动设计文档。");

            try
            {
                IntPtr pElem = GetSelectedElement(pScene, "regen");
                if (pElem != IntPtr.Zero)
                {
                    try
                    {
                        int elemType;
                        if (TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(regen)", out elemType))
                        {
                            if (elemType == 2)
                            {
                                IntPtr pAsm;
                                int hrQi = TryQI(pElem, IID_IZAssembly, out pAsm);
                                if (hrQi == 0 && pAsm != IntPtr.Zero)
                                {
                                    try
                                    {
                                        int hr = TryInvokeShortArg(pAsm, SlotAsmRegenerateParts, -1,
                                            "IZAssembly.RegenerateParts");
                                        if (hr == 0)
                                        {
                                            RefreshSceneUi(pScene);
                                            return "选中的装配体";
                                        }
                                    }
                                    finally { Marshal.Release(pAsm); }
                                }
                                Diagnostics.Log("IZAssembly.RegenerateParts 失败，回退整场景重新生成");
                            }
                            else if (IsPartKind(elemType))
                            {
                                IntPtr pPart;
                                int hrQi = TryQI(pElem, IID_IZPart, out pPart);
                                if (hrQi == 0 && pPart != IntPtr.Zero)
                                {
                                    try
                                    {
                                        IntPtr fn = Slot(pPart, SlotPartRegenerate);
                                        if (LooksCallable(fn))
                                        {
                                            FnNoArg f = (FnNoArg)Marshal.GetDelegateForFunctionPointer(
                                                fn, typeof(FnNoArg));
                                            int hr = f(pPart);
                                            Diagnostics.Log(string.Format(
                                                "IZPart.Regenerate hr=0x{0:X}", hr));
                                            if (hr == 0)
                                            {
                                                RefreshSceneUi(pScene);
                                                return "选中的零件";
                                            }
                                        }
                                        else
                                        {
                                            Diagnostics.Log("IZPart.Regenerate 跳过：函数指针无效");
                                        }
                                    }
                                    finally { Marshal.Release(pPart); }
                                }
                                Diagnostics.Log("IZPart.Regenerate 失败，回退整场景重新生成");
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(pElem);
                    }
                }

                // 回退：整场景重新生成（未选中 / 零部件接口调用失败）
                int hrScene = TryInvokeShortArg(pScene, SlotSceneRegenerateParts, -1,
                    "IZSceneDoc.RegenerateParts");
                if (hrScene != 0)
                    throw new InvalidOperationException(
                        string.Format("重新生成失败 hr=0x{0:X}", hrScene));
                RefreshSceneUi(pScene);
                return "设计场景全部零部件（未选中零部件）";
            }
            finally
            {
                Marshal.Release(pScene);
                Marshal.Release(pDoc);
            }
        }

        #endregion

        #endregion
    }
}
