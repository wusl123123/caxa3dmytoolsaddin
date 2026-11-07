using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{

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
    internal static class SheetMetalOps
    {
        // ---- 接口 GUID ----
        private static readonly Guid IID_IZBaseApp = new Guid("A5B9AF7D-228B-4ED1-BA42-D51318F7EA20");
        private static readonly Guid IID_IZSceneDoc = new Guid("3845C6DA-89D6-43F7-8B10-4BE36E0E758B");
        private static readonly Guid IID_IZDoc = new Guid("408F2326-F171-443B-A9EF-4AB250A2B1A8");
        private static readonly Guid IID_IZElement = new Guid("69EF5583-A26D-4913-A2F6-DBFF73E1B8A3");
        private static readonly Guid IID_IZPart = new Guid("F20F2411-05C4-4CA6-B2B4-5DB29531EDD7");
        private static readonly Guid IID_IZAssembly = new Guid("FF7C2F47-F21F-4225-9027-58E66CC01860");
        private static readonly Guid IID_IZSheetMetalPart = new Guid("05667879-65CD-4417-AFB8-16E09A669D06");
        private static readonly Guid IID_IZConfigurationMgr = new Guid("4FDA96C8-058E-4977-8A1F-FA2CCDBFF315");
        private static readonly Guid IID_IZConfiguration = new Guid("7AFFA1E0-F43D-47DA-9AA2-9FC9C7F587D7");

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
        private const int SlotDocSave = 9;                // IZDoc::Save (DispId 3 + 6)
        private const int SlotDocClose = 13;              // IZDoc::Close (DispId 7 + 6)
        private const int SlotDocGetModified = 16;         // IZDoc::get_Modified (DispId 10 + 6)
        private const int SlotDocClose2 = 23;              // IZDoc::Close2(VARIANT_BOOL) (DispId 17 + 6)
        private const int SlotAppGetOpenDocsCount = 14;    // IZBaseApp::GetOpenDocsCount
        private const int SlotElemSetName = 10;           // IZElement::set_Name
        private const int SlotPartGetExtLinkedInfo = 17;  // IZPart::GetExternallyLinkedInfo
        private const int SlotPartRegenerate = 18;        // IZPart::Regenerate()
        private const int SlotAsmGetExtLinkedInfo = 21;   // IZAssembly::GetExternallyLinkedInfo
        private const int SlotAsmRegenerateParts = 17;    // IZAssembly::RegenerateParts(VARIANT_BOOL)
        private const int SlotSceneRegenerateParts = 21;  // IZSceneDoc::RegenerateParts(VARIANT_BOOL)
        private const int SlotSceneUpdate = 36;           // IZSceneDoc::Update()
        private const int SlotSceneUpdateGraphics = 83;   // IZSceneDoc::UpdateGraphics(UInt32)
        private const int SlotSceneUpdateBrowser = 84;    // IZSceneDoc::UpdateBrowser(UInt32)

        // ---- 钣金展开探针槽位（本机 ICApiIronCAD.tlb FUNCDESC 核对；槽位 = 元数据索引 + 7）----
        private const int SlotSceneGetTopElement = 58;    // IZSceneDoc::GetTopElement -> IZElement*
        private const int SlotElemGetId = 8;              // IZElement::get_Id
        private const int SlotElemGetOwningDoc = 11;      // IZElement::get_OwningDoc
        private const int SlotElemGetParent = 12;         // IZElement::GetParent -> IZElement*
        private const int SlotElemGetFirstChild = 19;     // IZElement::GetFirstChild -> IZElement*
        private const int SlotElemGetNextChild = 20;      // IZElement::GetNextChild -> IZElement*（同级下一个）
        private const int SlotElemGetSystemName = 25;     // IZElement::get_SystemName
        private const int SlotElemGetShowInBrowser = 30;  // IZElement::get_ShowInBrowser
        private const int SlotSmUnfold = 7;               // IZSheetMetalPart::Unfold()
        private const int SlotSmRefold = 8;               // IZSheetMetalPart::Refold()
        private const int SlotSmGetIsFolded = 9;          // IZSheetMetalPart::get_IsFolded(VARIANT_BOOL*)
        private const int SlotSmGetThickness = 10;        // IZSheetMetalPart::get_Thickness(double*)
        private const int SlotSmGetUnfoldConfiguration = 11; // IZSheetMetalPart::GetUnfoldConfiguration(LONG*)
        private const int SlotSmGetFlatPart = 12;         // IZSheetMetalPart::GetFlatPart -> IZElement*
        private const int SlotSmGetSiblingPart = 13;      // IZSheetMetalPart::GetSiblingPart -> IZElement*
        private const int SlotElemGetChildrenCount = 15;  // IZElement::GetChildrenCount(int*)

        // ---- 外部链接文档（同名 .ics）操作槽位 ----
        private const int SlotAppOpenFile2 = 30;          // IZBaseApp::OpenFile2(path,readonly,visible,out alreadyOpen) -> IZDoc*
        private const int SlotAppSetActiveDoc = 12;       // IZBaseApp::set_ActiveDoc(IZDoc*)
        private const int SlotAppGetOpenDocByName = 9;    // IZBaseApp::GetOpenDocByName(BSTR, IZDoc**)（元数据索引 2）
        private const int SlotAppCloseFile = 35;          // IZBaseApp::CloseFile(IZDoc*)（元数据索引 28）

        // ---- 多配置（CAXA 3D 2027 钣金件多配置坑点）----
        private const int SlotSceneGetConfigurationMgr = 40; // IZSceneDoc::get_ConfigurationMgr -> IZConfigurationMgr*
        private const int SlotCfgMgrGetById = 8;             // IZConfigurationMgr::GetConfigurationById(LONG) -> IZConfiguration*
        private const int SlotCfgMgrCount = 10;              // IZConfigurationMgr::get_Count
        private const int SlotCfgMgrDefault = 12;            // IZConfigurationMgr::get_DefaultConfiguration -> IZConfiguration*
        private const int SlotCfgMgrActive = 13;             // IZConfigurationMgr::get_ActiveConfiguration -> IZConfiguration*
        private const int SlotCfgGetName = 7;                // IZConfiguration::get_Name
        private const int SlotCfgGetIsActive = 9;            // IZConfiguration::get_IsActive
        private const int SlotCfgGetIsDefault = 10;          // IZConfiguration::get_IsDefault
        private const int SlotCfgGetIsFlat = 14;             // IZConfiguration::get_IsOfFlatSheetMetalPart
        private const int SlotCfgActivate = 15;              // IZConfiguration::Activate(VARIANT_BOOL bUpdateAllConfigsFirst)

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
        private delegate int FnGetDouble(IntPtr self, out double value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnArrayGet(IntPtr self, int index, out IntPtr ppElem);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetVariantResult(IntPtr self, IntPtr pvarResult);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnBstrArg(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string bstrName);

        /// <summary>BSTR 参数 + 接口指针返回值（如 IZBaseApp::GetOpenDocByName）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnBstrArgGetObj(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string bstrName, out IntPtr ppv);

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

        /// <summary>普通「指针参数」调用形态（如 IZBaseApp::set_ActiveDoc(IZDoc*)）。</summary>
        private delegate int FnPtrArg(IntPtr self, IntPtr arg);

        /// <summary>IZDoc::Close2(VARIANT_BOOL) — 关闭文档窗口（不保存）。</summary>
        private delegate int FnDocClose2(IntPtr self, short vbCloseImmediately);

        /// <summary>「int 参数 + 接口指针返回值」形态（如 IZConfigurationMgr::GetConfigurationById(LONG, IZConfiguration**)）。</summary>
        private delegate int FnIntArgObj(IntPtr self, int arg, out IntPtr ppv);

        /// <summary>
        /// IZBaseApp::OpenFile2(BSTR path, VARIANT_BOOL asReadOnly, VARIANT_BOOL visible,
        ///                      VARIANT_BOOL* alreadyOpen, IZDoc** retval)
        /// pbAlreadyOpen 用裸缓冲区传入：该出参为 2 字节 VARIANT_BOOL，用裸指针可避免托管 bool 宽度歧义。
        /// </summary>
        private delegate int FnOpenFile2(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string fileName,
            short asReadOnly, short visible, IntPtr pbAlreadyOpen, out IntPtr ppDoc);

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
            return new CaxaSession { AppObject = (IZBaseApp)appObj, BaseApp = pBase };
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
        /// 共享表铁律（规则 14.7/14.9）：候选表一律先经 IsSharedMgr()==true 确认，
        /// 确认不了就返回 Zero 并写日志——绝不拿未确认的表凑数，也不按属性名猜表。
        /// </summary>
        private static IntPtr GetSharedCustomPropMgr(IntPtr pElem, string tag)
        {
            foreach (int selector in new int[] { 0, 1 })
            {
                IntPtr pMgr = TryGetCustomPropManager(pElem, selector, tag);
                if (pMgr == IntPtr.Zero) continue;
                if (TryIsSharedMgr(pMgr, tag + ",selector=" + selector)) return pMgr;
                Marshal.Release(pMgr);
            }
            // 回退路径：旧属性 getter（vbShared=TRUE）；名义不算判据，同样要 IsSharedMgr 确认
            IntPtr pProp = GetCustomPropMgr(pElem, -1, tag);
            if (pProp != IntPtr.Zero)
            {
                if (TryIsSharedMgr(pProp, tag + ",vbShared=true")) return pProp;
                Marshal.Release(pProp);
            }
            Diagnostics.Log("GetSharedCustomPropMgr(" + tag + "): 三个候选都没有 IsSharedMgr=true，" +
                "按共享表铁律本次不读写任何其它表");
            return IntPtr.Zero;
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

        #region 钣金展开只读探针

        /// <summary>
        /// 钣金展开只读探针的完整取证结果。
        /// 用“只读 + 一次 Unfold”换取事实：展开到底会不会让展开件名同步、是否需要零件独立文档。
        /// </summary>
        internal sealed class SheetMetalProbeResult
        {
            public string DocName = "";
            public bool HasScene;
            public bool HasSelection;
            public string SelectionNote = "";

            public int SelectedType;
            public string SelectedKind = "";
            public string SelectedName = "";
            public bool SelectedIsSheetMetal;
            public bool ExternallyLinked;
            public string LinkedFile = "";

            public string IsFolded = "<未采集>";
            public string Thickness = "<未采集>";
            public string UnfoldConfiguration = "<未采集>";
            public string FlatPartName = "<未采集>";
            public string SiblingPartName = "<未采集>";

            // ---- 外部链接文档上下文（同名 .ics）----
            public string Mode = "当前活动文档上下文";
            public bool LinkedDocOpened;
            public string LinkedDocHr = "";
            public string TargetDocName = "";
            public string FoundSheetMetalName = "";
            public string PopupInfo = "";

            // ---- 多配置取证（CAXA 3D 2027 钣金件多配置坑点）----
            public string ConfigBefore = "";
            public string ConfigAfterUnfold = "";
            public string ActiveConfigBefore = "";
            public string ActiveConfigAfter = "";
            public string ConfigRestoreHr = "";
            public string ConfigAfterRestore = "";
            public string ActiveConfigAfterRestore = "";

            public bool UnfoldCalled;
            public int UnfoldHr;
            public string AfterSelectedName = "<未采集>";
            public string AfterFlatPartName = "<未采集>";
            public string AfterSiblingPartName = "<未采集>";
            public bool NameChanged;

            // ---- 正式流程：工程图（exb）先行 + .ics 保存 ----
            public bool DrawingFound;
            public string DrawingPath = "";
            public bool DrawingOpened;
            public string DrawingOpenHr = "";
            public int DrawingAutoClicks;
            public bool IcsSaved;
            public string IcsSaveHr = "";
            public bool DrawingSavedClosed;
            public string DrawingSaveInfo = "";
            public bool TitleBlockUpdated;
            public string TitleBlockInfo = "";
            public bool IcsClosed;
            public string IcsCloseHr = "";

            public readonly List<string> Tree = new List<string>();
            public readonly List<string> Notes = new List<string>();
        }

        /// <summary>正式流程开关。为 null 时 RunSheetMetalProbeInLinkedDoc 保持探针行为（不碰 exb、不保存）。</summary>
        internal sealed class NameUpdateOptions
        {
            /// <summary>先打开同名 .exb 工程图，并对其弹窗全部代点。</summary>
            public bool OpenDrawingFirst;
            /// <summary>展开 + 改名 + 还原默认配置后保存 .ics。</summary>
            public bool SaveIcs;
            /// <summary>回到 .exb → 代点 → CRX 保存并关闭图纸。</summary>
            public bool SaveAndCloseDrawing;
            /// <summary>关闭工程图前先更新标题栏 3D 信息（需 caxa3dtitleupdate 的 CRX）。</summary>
            public bool UpdateTitleBlock;
            /// <summary>.ics 保存后关闭本次新打开的 .ics（原本就在该文档里时不关）。</summary>
            public bool CloseIcs;
            /// <summary>UI 提供的条件轮询（内部泵消息），签名同 MainForm.WaitUntil。</summary>
            public WaitConditionFn WaitCondition;
        }

        /// <summary>条件轮询委托：what=用途说明，timeoutMs=超时，condition=条件；返回是否在超时前成立。</summary>
        internal delegate bool WaitConditionFn(string what, int timeoutMs, Func<bool> condition);

        /// <summary>带槽位防护地调用「无参、返回接口指针」的方法；失败返回 Zero（调用方负责 Release）。</summary>
        private static IntPtr CallGetObj(IntPtr pIface, int slot, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效（短 vtable 代理）");
                    return IntPtr.Zero;
                }
                FnGetObj f = (FnGetObj)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetObj));
                IntPtr pv;
                int hr = f(pIface, out pv);
                Diagnostics.Trace(string.Format("{0} hr=0x{1:X} ptr=0x{2:X}", what, hr, pv.ToInt64()));
                return hr == 0 ? pv : IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 对任意 IZElement 指针执行 set_Name（槽位 10），带槽位防护。
        /// 返回 HRESULT（0=成功），不抛异常。
        /// </summary>
        private static int TrySetElementName(IntPtr pElem, string newName, string what)
        {
            if (string.IsNullOrEmpty(newName)) return unchecked((int)0x80070057);
            try
            {
                IntPtr fn = Slot(pElem, SlotElemSetName);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：set_Name 函数指针无效");
                    return HR_FAIL;
                }
                FnSetBstrProp f = (FnSetBstrProp)Marshal.GetDelegateForFunctionPointer(
                    fn, typeof(FnSetBstrProp));
                int hr = f(pElem, newName);
                Diagnostics.Log(string.Format("{0} → \"{1}\" hr=0x{2:X}", what, newName, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        /// <summary>
        /// 调用「无参无返回值」方法（如 Refold()），带槽位防护。返回 HRESULT。
        /// </summary>
        private static int TryCallNoArg(IntPtr pIface, int slot, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效");
                    return HR_FAIL;
                }
                FnNoArg f = (FnNoArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnNoArg));
                int hr = f(pIface);
                Diagnostics.Log(string.Format("{0} hr=0x{1:X}", what, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        /// <summary>对任意 IZDoc 指针执行 Save（槽位 9），带槽位防护。返回 HRESULT。</summary>
        private static int TrySaveDoc(IntPtr pDoc, string what)
        {
            if (pDoc == IntPtr.Zero) return HR_FAIL;
            IntPtr pZDoc = IntPtr.Zero;
            try
            {
                if (TryQI(pDoc, IID_IZDoc, out pZDoc) != 0 || pZDoc == IntPtr.Zero)
                {
                    Diagnostics.Log(what + " 失败：QI IZDoc 失败");
                    return HR_FAIL;
                }
                return TryCallNoArg(pZDoc, SlotDocSave, what);
            }
            finally
            {
                if (pZDoc != IntPtr.Zero) Marshal.Release(pZDoc);
            }
        }

        /// <summary>按完整路径查找已打开的文档（IZBaseApp::GetOpenDocByName）。找不到返回 Zero。</summary>
        private static IntPtr FindOpenDoc(CaxaSession session, string fullPath)
        {
            if (session == null || session.BaseApp == IntPtr.Zero || string.IsNullOrEmpty(fullPath))
                return IntPtr.Zero;
            try
            {
                IntPtr fn = Slot(session.BaseApp, SlotAppGetOpenDocByName);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log("GetOpenDocByName 跳过：函数指针无效");
                    return IntPtr.Zero;
                }
                var f = (FnBstrArgGetObj)Marshal.GetDelegateForFunctionPointer(
                    fn, typeof(FnBstrArgGetObj));
                IntPtr pDoc;
                int hr = f(session.BaseApp, fullPath, out pDoc);
                if (hr == 0 && pDoc != IntPtr.Zero)
                {
                    Diagnostics.Log("GetOpenDocByName hr=0x0 命中 " + System.IO.Path.GetFileName(fullPath));
                    return pDoc;
                }
                Diagnostics.Log("GetOpenDocByName hr=0x" + hr.ToString("X8")
                    + " " + System.IO.Path.GetFileName(fullPath));
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("GetOpenDocByName EX: " + ex.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>关闭指定文档窗口（IZDoc::Close2(true)）。对外链 .ics，Close2 能关窗口但不会从 CAXA 内部文档列表卸载（装配体仍引用它）。返回 HRESULT。</summary>
        private static int TryCloseDoc(CaxaSession session, IntPtr pDoc, string what)
        {
            if (pDoc == IntPtr.Zero)
                return HR_FAIL;
            IntPtr fn = Slot(pDoc, SlotDocClose2);
            if (!LooksCallable(fn))
            {
                Diagnostics.Log(what + " 跳过：Close2 函数指针无效");
                return HR_FAIL;
            }
            try
            {
                var f = (FnDocClose2)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnDocClose2));
                int hr = f(pDoc, -1); // VARIANT_TRUE = -1，CloseImmediately
                Diagnostics.Log(string.Format("{0} Close2 hr=0x{1:X}", what, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " Close2 EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        // ---- 工程图阶段（独立一步，做完再进 .ics）----
        /// <summary>
        /// 工程图阶段：严格照 C:\code\ICAPI\caxa3dtitleupdate 的 UpdateTitleBlock 走完五步：
        ///   1/5 打开 exb（OpenFile2 返回不可信，异常也照常继续）
        ///   2/5 等就绪 = `commandEnded | CX_open_Impl` 自本次打开起出现过 且 无模态框（120s）
        ///   3/5 等编辑器短暂稳定 = 无模态框 且 CRX 日志静默 200ms（3s）
        ///   4/5 派发 CMD:TITLEBLOCK_3DSET，结果判据 REQ.tb-result / REQ.tb-giveup /
        ///       (Dlg3DInfo.AppliedCount 增加 且 REQ.tb-done)，最长 300s；收尾必发 CMD:TB_ABORT
        ///   5/5 保存关闭 = CMD:CLOSEDOC（close-done / CX_DFT_ViewClose，30s）→ CMD:CLOSE 兜底（15s）
        /// 关键差异（之前"卡 exb"的根因）：PING 只是冷启动探针，**不阻塞等 ack**；
        /// 就绪判据不用图纸窗口/默认命令，只认 CX_open_Impl 结束 + 无模态框。
        /// </summary>
        private static void RunDrawingStage(CaxaSession session, string exbPath, string exbName,
            Action<string> log, NameUpdateOptions opt, SheetMetalProbeResult r,
            Func<bool> shouldStop, int popupClickBase)
        {
            PopupAutoYes.Suspend = false;
            DateTime openStart = DateTime.Now;

            // 0) 预投 PING：只作冷启动探针（CRX 冷启动会删掉请求文件），不阻塞等 ack。
            int pingSeq = CrxBridge.PostCommand("CMD:PING");
            string pingAck = "REQ.ack | seq=" + pingSeq;

            // 1) 打开工程图
            log("");
            log("【工程图阶段】1/5 打开工程图：" + exbName);
            r.DrawingPath = exbPath;
            r.DrawingFound = true;
            IntPtr pDoc = IntPtr.Zero;
            int alreadyOpenDraw = 0;
            int hrD = -1;
            try
            {
                hrD = TryOpenFile2(session.BaseApp, exbPath, out pDoc, out alreadyOpenDraw);
            }
            catch (Exception ex)
            {
                // COM 对 exb 的返回不可靠（E_FAIL / Invalid file format），文件实际已打开
                log("  OpenFile2 返回异常（对 exb 属正常）：" + ex.Message);
            }
            r.DrawingOpenHr = "hr=0x" + hrD.ToString("X8")
                + (alreadyOpenDraw != 0 ? "（原本已打开）" : "");
            log("  OpenFile2 hr=" + r.DrawingOpenHr);

            // 2) 等图纸就绪：CX_open_Impl 结束且无模态框（"3D视图数据源更新"弹窗已被代点）
            bool ready = opt.WaitCondition != null && opt.WaitCondition("等图纸就绪", 120000,
                delegate
                {
                    if (shouldStop()) return true;
                    return CrxBridge.EventSince("commandEnded | CX_open_Impl", openStart)
                        && PopupAutoYes.ModalCount == 0;
                });
            if (!ready)
            {
                // 超时才回头看一眼 PING 回执，用于区分"CRX 没加载"与"图纸没打开"
                if (CrxBridge.LogCount(pingAck) == 0)
                    log("  2/5 警告：CRX 未就绪（可点「注册CRX」后重开 CAXA 再试）");
                else
                    log("  2/5 警告：等图纸就绪超时");
                // 超时诊断：图纸文档窗口到底有没有出现（区分"没打开"与"打开了但 CX_open_Impl 没结束"）
                log("  图纸文档窗口：" + (DrawingDocWindowExists(exbName) ? "已出现（图纸其实已打开）" : "未检出"));
            }
            else log("  2/5 工程图已打开并就绪");
            r.DrawingOpened = ready;

            if (!ready)
            {
                r.DrawingSaveInfo = "工程图就绪超时，标题栏与保存关闭均跳过";
                r.Notes.Add("工程图未就绪：请确认 " + exbName
                    + " 已在图纸进程 CDRAFT_M_IC 中打开（CRX 是否已注册）。");
                log("  " + r.DrawingSaveInfo);
                return;
            }

            // 3) 很短的一次稳定等待（避开打开瞬间的事件突发）。实测 200ms 足够：
            //    键入通道是"投进文档输入缓冲"，编辑器正忙也会排队，不需要等它闲下来。
            bool settled = opt.WaitCondition != null && opt.WaitCondition("等编辑器短暂稳定", 3000,
                delegate { return PopupAutoYes.ModalCount == 0 && CrxBridge.LogQuiet(200); });
            log(settled ? "  3/5 编辑器已稳定，准备派发标题栏命令"
                        : "  3/5 编辑器仍在忙（可能在跑视图数据源更新），直接派发并由 CRX 重试");
            r.DrawingAutoClicks = PopupAutoYes.ClickCount - popupClickBase;

            // 4) 派发标题栏 3D 信息设置命令
            if (opt.UpdateTitleBlock && !shouldStop())
                UpdateTitleBlockViaCrx(session, exbName, log, opt, r, shouldStop);

            // 命令收尾：确认类对话框（配置/确定）由代点器点掉后再保存关闭
            if (PopupAutoYes.ModalCount > 0 && opt.WaitCondition != null)
                opt.WaitCondition("等确认对话框关闭", 15000,
                    delegate { return PopupAutoYes.ModalCount == 0; });

            if (shouldStop()) return;

            // 5) 保存并关闭工程图（CX_save→CX_save_Impl→CX_DFT_ViewSave→CX_DFT_ViewClose）
            int doneBase = CrxBridge.LogCount("close-done");
            int viewBase = CrxBridge.LogCount("commandEnded | CX_DFT_ViewClose");
            int seq = CrxBridge.PostCommand("CMD:CLOSEDOC|" + exbName);
            Func<bool> closedCond = delegate
            {
                return CrxBridge.LogCount("close-done") > doneBase
                    || CrxBridge.LogCount("commandEnded | CX_DFT_ViewClose") > viewBase;
            };
            bool closed = opt.WaitCondition != null
                && opt.WaitCondition("保存并关闭工程图", 30000, closedCond);
            if (!closed)
            {
                // 不补投 CMD:CLOSE：CX_DFT_ViewClose 打在「当前激活视图」上，
                // 图纸没开着时会打到 3D 设计环境（11:29 事故同源）。
                log("  图纸「" + exbName + "」保存/关闭未确认；按铁律不再向当前视图补投关闭命令");
            }
            r.DrawingSavedClosed = closed;
            r.DrawingSaveInfo = closed
                ? "5/5 工程图已保存并关闭（CLOSEDOC seq=" + seq + "）"
                : "工程图保存/关闭未确认完成（CLOSEDOC seq=" + seq + "），请人工检查该图纸状态";
            log("  " + r.DrawingSaveInfo);
            if (!closed) r.Notes.Add("工程图保存/关闭未确认完成，请人工检查。");

            // 等回到设计环境（图纸关闭后焦点回到 IRONCAD）
            if (opt.WaitCondition != null)
                opt.WaitCondition("等回到设计环境", 15000,
                    delegate { return IsActiveSceneDocument(session); });
        }

        // ---- 标题栏更新（自己的 CRX 键入通道派发 CX_DFT_TITLEBLOCK_3DSET）----
        // 2026-10-26 实证：原先复用 caxa3dtitleupdate 的 CRX，跨插件请求两次均无回执
        // （对方是否取走不可控）；且 crxedCommand 直调通道对该命令全废，只有
        // sendStringToExecute 键入通道能真正跑起来 —— 故收进自己的 CRX，全链自控。

        /// <summary>
        /// 在工程图里更新标题栏 3D 信息：投 CMD:TITLEBLOCK_3DSET（CRX 键入通道 CX_DFT_TITLEBLOCK_3DSET），
        /// 「设置3D信息」对话框由 Dlg3DInfo 自动选缺省配置并确定。
        /// 判据严格照 caxa3dtitleupdate.UpdateTitleBlock：
        ///   ① REQ.tb-result（标题栏信息真的变了）→ 成功；
        ///   ② Dlg3DInfo.AppliedCount 增加 且 REQ.tb-done → 幂等成功（本来就是默认配置）；
        ///   ③ REQ.tb-giveup → CRX 放弃；④ 300s 无结果 → 超时。
        /// 不阻塞等 TITLEBLOCK 的 REQ.ack（请求可能被 CRX 排队后才取走，等 ack 只会白等）；
        /// 收尾无论成败都投 CMD:TB_ABORT，避免 CRX 继续轮换把命令又派到已关闭的文档上。
        /// </summary>
        private static void UpdateTitleBlockViaCrx(CaxaSession session, string exbFileName,
            Action<string> log, NameUpdateOptions opt, SheetMetalProbeResult r, Func<bool> shouldStop)
        {
            log("");
            log("  4/5 派发 CX_DFT_TITLEBLOCK_3DSET（标题栏 3D 信息 · 默认配置）…");

            int tbResultBase = CrxBridge.LogCount("REQ.tb-result");
            int doneBase = CrxBridge.LogCount("REQ.tb-done");
            int giveupBase = CrxBridge.LogCount("REQ.tb-giveup");
            int appliedBase = Dlg3DInfo.AppliedCount;

            CrxBridge.PostCommand("CMD:TITLEBLOCK_3DSET");
            bool reached = opt.WaitCondition != null && opt.WaitCondition(
                "等标题栏 3D 信息写入结果", 300000, delegate
                {
                    if (shouldStop()) return true;
                    return CrxBridge.LogCount("REQ.tb-result") > tbResultBase
                        || CrxBridge.LogCount("REQ.tb-giveup") > giveupBase
                        || (Dlg3DInfo.AppliedCount > appliedBase
                            && CrxBridge.LogCount("REQ.tb-done") > doneBase);
                });

            bool tbOk = CrxBridge.LogCount("REQ.tb-result") > tbResultBase;
            bool giveup = CrxBridge.LogCount("REQ.tb-giveup") > giveupBase;
            bool dialogApplied = Dlg3DInfo.AppliedCount > appliedBase;

            // 收尾：无论成败都让 CRX 停止通道轮换（08:24 教训：C# 判完就走，CRX 还在轮换，
            // 会把命令派发到已关闭的文档上，甚至抢跑到下一次运行）。
            try { CrxBridge.PostCommand("CMD:TB_ABORT"); } catch { }

            if (!reached)
            {
                r.TitleBlockInfo = "标题栏 3D 信息 300 秒内无结果（CRX 未确认变化）";
                r.Notes.Add("标题栏 3D 信息设置超时，请查看 %TEMP%\\caxa3dcodenameextraction_crx.log 里的 tb-* 记录。");
            }
            else if (tbOk)
            {
                r.TitleBlockUpdated = true;
                r.TitleBlockInfo = "标题栏 3D 信息已按默认配置写入（已由信息差异确认）";
            }
            else if (dialogApplied)
            {
                r.TitleBlockInfo = "「设置3D信息」已按默认配置填写并确认；标题栏信息无变化（原本就是默认）";
            }
            else if (giveup)
            {
                r.TitleBlockInfo = "CRX 放弃：命令未能跑起来（编辑器长期忙碌）";
                r.Notes.Add("标题栏命令派发放弃，请在工程图里人工执行一次「设置3D信息」。");
            }
            else
            {
                r.TitleBlockInfo = "标题栏 3D 信息设置未完成";
                r.Notes.Add("标题栏更新未完成，请人工核对标题栏。");
            }
            log("  " + r.TitleBlockInfo);

            if (PopupAutoYes.ModalCount > 0 && opt.WaitCondition != null)
                opt.WaitCondition("等确认对话框关闭", 15000,
                    delegate { return PopupAutoYes.ModalCount == 0; });
        }

        /// <summary>当前活动文档的文件名（含扩展名，取不到返回空串）。用于确认工程图是否已被关闭。</summary>
        public static string GetActiveDocName(CaxaSession session)
        {
            if (session == null || session.BaseApp == IntPtr.Zero) return "";
            IntPtr pDoc = IntPtr.Zero;
            try
            {
                int hr = TryCallGetObj(session.BaseApp, SlotActiveDoc, out pDoc);
                if (hr != 0 || pDoc == IntPtr.Zero) return "";
                IntPtr pZDoc;
                if (TryQI(pDoc, IID_IZDoc, out pZDoc) != 0 || pZDoc == IntPtr.Zero) return "";
                try { return TryReadBstrProp(pZDoc, SlotDocGetName, "activeDoc.get_Name") ?? ""; }
                finally { Marshal.Release(pZDoc); }
            }
            finally
            {
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        private static string ReadElementName(IntPtr pElem, string what)
        {
            return TryReadBstrProp(pElem, SlotElemGetName, what) ?? "";
        }

        /// <summary>读取 short 输出属性（get_IsFolded 形态，带槽位防护）。失败返回 false。</summary>
        private static bool TryReadShortProp(IntPtr pIface, int slot, string what, out short value)
        {
            value = 0;
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：函数指针无效");
                    return false;
                }
                FnGetShort f = (FnGetShort)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetShort));
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

        /// <summary>读取 double 输出属性（get_Thickness 形态，带槽位防护）。失败返回 false。</summary>
        private static bool TryReadDoubleProp(IntPtr pIface, int slot, string what, out double value)
        {
            value = 0;
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：函数指针无效");
                    return false;
                }
                FnGetDouble f = (FnGetDouble)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetDouble));
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

        /// <summary>
        /// 把「带自有引用」的原始指针规整成 IZElement 指针（释放原始指针，返回的新指针由调用方负责 Release）。
        /// QI 成功返回新引用、失败返回 Zero。
        /// </summary>
        private static IntPtr AsElementReleaseRaw(IntPtr pRaw)
        {
            if (pRaw == IntPtr.Zero) return IntPtr.Zero;
            IntPtr pElem;
            int hr = TryQI(pRaw, IID_IZElement, out pElem);
            Marshal.Release(pRaw);
            if (hr != 0 || pElem == IntPtr.Zero)
            {
                Diagnostics.Trace(string.Format("QI(IZElement) hr=0x{0:X} 失败", hr));
                return IntPtr.Zero;
            }
            return pElem;
        }

        /// <summary>读取（并释放）一个返回的关联元素指针，返回其名称显示文本。</summary>
        private static string DescribeRelatedElement(IntPtr pRaw, string tag)
        {
            if (pRaw == IntPtr.Zero) return "<空>";
            IntPtr pElem = IntPtr.Zero;
            int hrQi = TryQI(pRaw, IID_IZElement, out pElem);
            try
            {
                if (hrQi != 0 || pElem == IntPtr.Zero)
                    return "<QI 失败 hr=0x" + hrQi.ToString("X") + ">";
                return "\"" + ReadElementName(pElem, "IZElement.get_Name(" + tag + ")") + "\"";
            }
            finally
            {
                if (pElem != IntPtr.Zero) Marshal.Release(pElem);
                Marshal.Release(pRaw);
            }
        }

        /// <summary>读取选中零部件的外部链接（不抛异常）。</summary>
        private static void TryReadExternalLinkInto(IntPtr pElem, int elemType,
            out bool linked, out string file)
        {
            linked = false; file = "";
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
                short l; IntPtr pbstr;
                int hr = f(pObj, out l, out pbstr);
                Diagnostics.Trace(string.Format(
                    "GetExternallyLinkedInfo(probe,type={0}) hr=0x{1:X} linked={2}", elemType, hr, l));
                if (hr == 0 && pbstr != IntPtr.Zero)
                {
                    try { file = Marshal.PtrToStringBSTR(pbstr) ?? ""; }
                    finally { Marshal.FreeBSTR(pbstr); }
                }
                linked = hr == 0 && l != 0 && !string.IsNullOrEmpty(file);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("TryReadExternalLinkInto EX: " + ex.Message);
            }
            finally
            {
                Marshal.Release(pObj);
            }
        }

        /// <summary>采集 IZSheetMetalPart 的 IsFolded / Thickness / UnfoldConfiguration / Flat / Sibling。</summary>
        private static void ReadSheetMetalState(IntPtr pSm, SheetMetalProbeResult r, string tag)
        {
            short folded;
            if (TryReadShortProp(pSm, SlotSmGetIsFolded, "SM.get_IsFolded(" + tag + ")", out folded))
                // get_IsFolded 语义（API 文档：Determine if the part is flat or folded）：
                //   True  = 已折弯的成形钣金件；False = 平板/展开状态
                r.IsFolded = folded != 0
                    ? "True（折弯成形状态）"
                    : "False（平板/展开状态）";

            double th;
            if (TryReadDoubleProp(pSm, SlotSmGetThickness, "SM.get_Thickness(" + tag + ")", out th))
                r.Thickness = th.ToString(CultureInfo.InvariantCulture);

            int cfg;
            if (TryReadIntProp(pSm, SlotSmGetUnfoldConfiguration,
                    "SM.GetUnfoldConfiguration(" + tag + ")", out cfg))
                r.UnfoldConfiguration = cfg.ToString(CultureInfo.InvariantCulture);

            r.FlatPartName = DescribeRelatedElement(
                CallGetObj(pSm, SlotSmGetFlatPart, "SM.GetFlatPart(" + tag + ")"),
                "GetFlatPart(" + tag + ")");
            r.SiblingPartName = DescribeRelatedElement(
                CallGetObj(pSm, SlotSmGetSiblingPart, "SM.GetSiblingPart(" + tag + ")"),
                "GetSiblingPart(" + tag + ")");
        }

        private static void DumpSheetMetalState(SheetMetalProbeResult r, Action<string> log, string title)
        {
            log("【钣金状态 · " + title + "】");
            log("  IsFolded          : " + r.IsFolded);
            log("  Thickness         : " + r.Thickness);
            log("  UnfoldConfiguration: " + r.UnfoldConfiguration);
            log("  GetFlatPart 名    : " + r.FlatPartName);
            log("  GetSiblingPart 名 : " + r.SiblingPartName);
        }

        /// <summary>
        /// 递归 dump 设计树（缩进表示层级；仅零件/装配体继续向下展开）。
        /// 元素类型为钣金(20) 时附带 IsFolded / Flat / Sibling。
        /// </summary>
        private static void WalkElement(IntPtr pElem, int depth, SheetMetalProbeResult r,
            Action<string> log, Func<bool> shouldStop, int maxDepth, int maxNodes)
        {
            if (pElem == IntPtr.Zero || depth > maxDepth) return;
            if (r.Tree.Count >= maxNodes) return;

            int type = -1;
            TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(tree)", out type);
            string name = ReadElementName(pElem, "IZElement.get_Name(tree)");
            int childCount = -1;
            TryReadIntProp(pElem, SlotElemGetChildrenCount, "IZElement.GetChildrenCount(tree)", out childCount);
            string line = new string(' ', depth * 2) + "[" + MapElementKind(type) + "] " + name
                + (childCount >= 0 ? "   <子元素 " + childCount + ">" : "");

            if (type == 20)
            {
                IntPtr pSm;
                if (TryQI(pElem, IID_IZSheetMetalPart, out pSm) == 0 && pSm != IntPtr.Zero)
                {
                    try
                    {
                        short folded;
                        string foldedText = "?";
                        if (TryReadShortProp(pSm, SlotSmGetIsFolded, "tree.get_IsFolded", out folded))
                            foldedText = folded != 0 ? "True" : "False";
                        string flatName = DescribeRelatedElement(
                            CallGetObj(pSm, SlotSmGetFlatPart, "tree.GetFlatPart"), "tree.GetFlatPart");
                        string sibName = DescribeRelatedElement(
                            CallGetObj(pSm, SlotSmGetSiblingPart, "tree.GetSiblingPart"), "tree.GetSiblingPart");
                        line += "  {IsFolded=" + foldedText + "  Flat=" + flatName + "  Sibling=" + sibName + "}";
                    }
                    finally
                    {
                        Marshal.Release(pSm);
                    }
                }
            }

            r.Tree.Add(line);
            if (log != null) log(line);
            if (shouldStop != null && shouldStop()) return;
            // 继续向下展开的条件：场景顶层(root,type 0)、零件(1)、装配体(2)、钣金件(20)；
            // get_Type 读取失败(-1)时也尝试一层（此前的 type==1||2 判断导致顶层 type=0 时整棵树不再展开）。
            if (!(type == 0 || type == 1 || type == 2 || type == 20 || type < 0)) return;

            IntPtr child = CallGetObj(pElem, SlotElemGetFirstChild, "IZElement.GetFirstChild(d=" + depth + ")");
            int guard = 0;
            while (child != IntPtr.Zero && guard++ < maxNodes)
            {
                IntPtr pChild = IntPtr.Zero;
                if (TryQI(child, IID_IZElement, out pChild) != 0) pChild = IntPtr.Zero;
                if (pChild != IntPtr.Zero)
                {
                    try { WalkElement(pChild, depth + 1, r, log, shouldStop, maxDepth, maxNodes); }
                    finally { Marshal.Release(pChild); }
                }
                if ((shouldStop != null && shouldStop()) || r.Tree.Count >= maxNodes)
                {
                    Marshal.Release(child);
                    break;
                }
                IntPtr next = CallGetObj(child, SlotElemGetNextChild, "IZElement.GetNextChild(d=" + depth + ")");
                Marshal.Release(child);
                child = next;
            }
        }

        /// <summary>
        /// 在文档树中查找钣金零件(type 20)。优先返回名字不以「展开」开头的成品件(收进 preferred)，
        /// 否则收进 fallback。两者均为「带自有引用」的 IZElement 指针，由调用方负责 Release。
        /// </summary>
        private static void SearchSheetMetal(IntPtr pElem, int depth, int maxDepth,
            ref IntPtr preferred, ref IntPtr fallback, ref string preferredName, ref string fallbackName)
        {
            if (pElem == IntPtr.Zero || depth > maxDepth) return;
            int type = -1;
            TryReadIntProp(pElem, SlotElemGetType, "search.get_Type", out type);
            if (type == 20)
            {
                string nm = ReadElementName(pElem, "search.get_Name");
                IntPtr held = IntPtr.Zero;
                if (TryQI(pElem, IID_IZElement, out held) == 0 && held != IntPtr.Zero)
                {
                    bool isFlat = nm != null && nm.StartsWith("展开", StringComparison.Ordinal);
                    if (!isFlat && preferred == IntPtr.Zero) { preferred = held; preferredName = nm; }
                    else if (fallback == IntPtr.Zero) { fallback = held; fallbackName = nm; }
                    else Marshal.Release(held);
                }
            }
            if (depth >= maxDepth) return;
            IntPtr child = CallGetObj(pElem, SlotElemGetFirstChild, "search.GetFirstChild(d=" + depth + ")");
            int guard = 0;
            while (child != IntPtr.Zero && guard++ < 500)
            {
                IntPtr pChild = IntPtr.Zero;
                if (TryQI(child, IID_IZElement, out pChild) != 0) pChild = IntPtr.Zero;
                if (pChild != IntPtr.Zero)
                {
                    try
                    {
                        SearchSheetMetal(pChild, depth + 1, maxDepth,
                            ref preferred, ref fallback, ref preferredName, ref fallbackName);
                    }
                    finally { Marshal.Release(pChild); }
                }
                IntPtr next = CallGetObj(child, SlotElemGetNextChild, "search.GetNextChild(d=" + depth + ")");
                Marshal.Release(child);
                child = next;
            }
        }

        /// <summary>打开或定位外部链接文档（同名 .ics）。alreadyOpen 非 0 表示该文件此前已处于打开状态。</summary>
        private static int TryOpenFile2(IntPtr pBaseApp, string path, out IntPtr ppDoc, out int alreadyOpen)
        {
            ppDoc = IntPtr.Zero;
            alreadyOpen = 0;
            try
            {
                IntPtr fn = Slot(pBaseApp, SlotAppOpenFile2);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log("OpenFile2 跳过：函数指针无效（短 vtable 代理）");
                    return HR_FAIL;
                }
                FnOpenFile2 f = (FnOpenFile2)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnOpenFile2));
                IntPtr buf = Marshal.AllocHGlobal(8);
                try
                {
                    Marshal.WriteInt64(buf, 0);
                    IntPtr pDoc;
                    int hr = f(pBaseApp, path, (short)0, (short)1, buf, out pDoc);
                    alreadyOpen = Marshal.ReadInt16(buf);
                    Diagnostics.Log(string.Format("OpenFile2 hr=0x{0:X} alreadyOpen={1} ptr=0x{2:X}",
                        hr, alreadyOpen, pDoc.ToInt64()));
                    if (hr == 0 && pDoc != IntPtr.Zero) ppDoc = pDoc;
                    return hr;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("TryOpenFile2 EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        /// <summary>调用「单指针参数」的接口方法（如 IZBaseApp::set_ActiveDoc）。</summary>
        private static int TryInvokePtrArg(IntPtr pIface, int slot, IntPtr arg, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：函数指针无效");
                    return HR_FAIL;
                }
                FnPtrArg f = (FnPtrArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnPtrArg));
                int hr = f(pIface, arg);
                Diagnostics.Log(string.Format("{0} hr=0x{1:X}", what, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        // ---- 窗口取证（只读枚举，不改动任何窗口）----
        private delegate bool ProbeEnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "EnumWindows", SetLastError = true)]
        private static extern bool ProbeEnumWindows(ProbeEnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int ProbeGetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int ProbeGetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
        private static extern uint ProbeGetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", EntryPoint = "IsWindowVisible", SetLastError = true)]
        private static extern bool ProbeIsWindowVisible(IntPtr hWnd);

        /// <summary>
        /// 枚举 CAXA 相关进程（本进程 / IRONCAD / CDRAFT_M_IC）的可见顶层窗口，
        /// 记录类名/标题/进程，用于取证「展开被拒」时弹出的对话框身份（供后续 PopupAutoYes 规则参考）。
        /// </summary>
        private static string CaptureCaXaWindows(string tag)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                ProbeEnumWindows(delegate(IntPtr h, IntPtr lp)
                {
                    try
                    {
                        if (!ProbeIsWindowVisible(h)) return true;
                        System.Text.StringBuilder title = new System.Text.StringBuilder(512);
                        ProbeGetWindowText(h, title, title.Capacity);
                        if (title.Length == 0) return true;
                        uint pid;
                        ProbeGetWindowThreadProcessId(h, out pid);
                        string pname = "(未知)";
                        try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                        catch { }
                        bool mine = (pid == self)
                            || string.Equals(pname, "IRONCAD", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(pname, "CDRAFT_M_IC", StringComparison.OrdinalIgnoreCase);
                        if (!mine) return true;
                        System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
                        ProbeGetClassName(h, cls, cls.Capacity);
                        sb.AppendLine("    [" + tag + "] class=\"" + cls.ToString() + "\" title=\""
                            + title.ToString() + "\" pid=" + pid + " (" + pname + ")");
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                sb.AppendLine("    [" + tag + "] 窗口枚举失败：" + ex.Message);
            }
            if (sb.Length == 0) sb.AppendLine("    [" + tag + "] （无标题非空的 CAXA 相关窗口）");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>
        /// 文档是否仍以「标题含该文件名」的形式显示在屏幕上（跨进程可查）。
        ///
        /// 【为什么不用窗口句柄做关闭动作】CAXA 3D 设计环境是 MDI：.ics/.exb 都不是独立顶层窗口，
        /// 只存在于主框架窗口内部（MDI 子窗口不会出现在 EnumWindows 顶层列表里）。
        /// 主框架标题形如 `CAXA 3D 实体设计2027 - [xxx.ics]`，**天然包含文档文件名**。
        /// 所以按文件名匹配到的句柄永远是主框架窗口 —— 对它投 WM_CLOSE 会把整个 CAXA 3D 杀掉。
        /// （2026-10-26 15:28 实锤事故：关闭 .ics 后整个程序退出。）
        /// 故此函数只用于「状态判据」，任何关闭动作一律交给 COM/CRX，绝不对主框架窗口投 WM_CLOSE。
        /// </summary>
        private static bool IsDocTitleShown(string fileName, string procName)
        {
            bool shown = false;
            try
            {
                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                ProbeEnumWindows(delegate(IntPtr h, IntPtr lp)
                {
                    try
                    {
                        if (!ProbeIsWindowVisible(h)) return true;
                        System.Text.StringBuilder title = new System.Text.StringBuilder(512);
                        ProbeGetWindowText(h, title, title.Capacity);
                        if (title.Length == 0) return true;
                        if (title.ToString().IndexOf(fileName, StringComparison.OrdinalIgnoreCase) < 0)
                            return true;
                        uint pid;
                        ProbeGetWindowThreadProcessId(h, out pid);
                        if (string.IsNullOrEmpty(procName))
                        {
                            if (pid != self) return true;
                        }
                        else
                        {
                            string pn = "";
                            try { pn = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                            catch { }
                            if (!string.Equals(pn, procName, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        shown = true;
                        return false;
                    }
                    catch { return true; }
                }, IntPtr.Zero);
            }
            catch { }
            return shown;
        }

        /// <summary>
        /// 图纸文档窗口是否存在（跨进程，含 MDI 子窗口）——exb「真的打开了」的权威判据。
        /// 坑（caxa3dCXDFTViewUpdate 19:18 实测）：CAXADraft 的文档窗口是 MDIClient 的
        /// **子窗口**（class 含 AfxFrameOrView），EnumWindows 只枚举顶层，永远看不到它，
        /// 所以顶层兜底之外必须再枚举 MDIClient 的子窗口。
        /// 判据 = class 含 AfxFrameOrView 且标题含 exb 文件名（与 CRX 关闭时匹配的同一套）。
        /// </summary>
        private static bool DrawingDocWindowExists(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            bool found = false;
            IntPtr mdi = IntPtr.Zero;
            try
            {
                ProbeEnumWindows(delegate(IntPtr h, IntPtr lp)
                {
                    try
                    {
                        if (!ProbeIsWindowVisible(h)) return true;
                        if (LooksLikeDrawingDocWindow(h, fileName)) { found = true; return false; }
                        System.Text.StringBuilder cls = new System.Text.StringBuilder(64);
                        ProbeGetClassName(h, cls, cls.Capacity);
                        if (string.Equals(cls.ToString(), "MDIClient", StringComparison.OrdinalIgnoreCase))
                            mdi = h;
                        return true;
                    }
                    catch { return true; }
                }, IntPtr.Zero);
                if (found || mdi == IntPtr.Zero) return found;
                ProbeEnumChildWindows(mdi, delegate(IntPtr h, IntPtr lp)
                {
                    try
                    {
                        if (ProbeIsWindowVisible(h) && LooksLikeDrawingDocWindow(h, fileName))
                        { found = true; return false; }
                        return true;
                    }
                    catch { return true; }
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        private static bool LooksLikeDrawingDocWindow(IntPtr h, string fileName)
        {
            try
            {
                System.Text.StringBuilder cls = new System.Text.StringBuilder(64);
                ProbeGetClassName(h, cls, cls.Capacity);
                if (cls.ToString().IndexOf("AfxFrameOrView", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
                System.Text.StringBuilder txt = new System.Text.StringBuilder(512);
                ProbeGetWindowText(h, txt, txt.Capacity);
                return txt.ToString().IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        [DllImport("user32.dll", EntryPoint = "EnumChildWindows", SetLastError = true)]
        private static extern bool ProbeEnumChildWindows(IntPtr hWndParent, ProbeEnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "GetParent", SetLastError = true)]
        private static extern IntPtr ProbeGetParent(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        private static extern bool ProbePostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_CLOSE_MSG = 0x0010;

        /// <summary>
        /// 找出**文档子窗口**句柄（MDI 子窗口），按标题匹配文件名。
        /// 【只返回子窗口，绝不返回顶层主框架】——顶层主框架标题同样含文件名，
        /// 对它投 WM_CLOSE 会关闭整个 CAXA 3D（2026-10-26 15:28 实锤事故）。
        /// 判据：GetParent(h) != 0（任何顶层窗口的父都为 0），双保险再排除 h==顶层句柄。
        /// </summary>
        private static IntPtr FindDocChildWindow(string fileName, string procName)
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                ProbeEnumWindows(delegate(IntPtr top, IntPtr lp)
                {
                    if (found != IntPtr.Zero) return false;
                    if (!ProcessMatches(top, self, procName)) return true;

                    ProbeEnumChildWindows(top, delegate(IntPtr c, IntPtr lp2)
                    {
                        try
                        {
                            if (c == top) return true;
                            if (ProbeGetParent(c) == IntPtr.Zero) return true;  // 顶层窗口，禁用
                            if (!ProbeIsWindowVisible(c)) return true;
                            // 只认 MDI 文档窗口类（AfxMDIFrame*/MDIClient 等），
                            // 排除标签栏、状态栏等“标题里也带文件名”的控件——对它们投 WM_CLOSE 无意义且可能扰乱 UI
                            System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
                            ProbeGetClassName(c, cls, cls.Capacity);
                            if (cls.ToString().IndexOf("MDI", StringComparison.OrdinalIgnoreCase) < 0)
                                return true;
                            System.Text.StringBuilder title = new System.Text.StringBuilder(512);
                            ProbeGetWindowText(c, title, title.Capacity);
                            if (title.Length == 0) return true;
                            if (title.ToString().IndexOf(fileName, StringComparison.OrdinalIgnoreCase) < 0)
                                return true;
                            found = c;
                            return false;
                        }
                        catch { return true; }
                    }, IntPtr.Zero);
                    return found == IntPtr.Zero;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        private static bool ProcessMatches(IntPtr hWnd, uint self, string procName)
        {
            uint pid;
            ProbeGetWindowThreadProcessId(hWnd, out pid);
            if (string.IsNullOrEmpty(procName)) return pid == self;
            string pn = "";
            try { pn = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { }
            return string.Equals(pn, procName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 关闭文档并验证。
        /// 1) 先走 COM `CloseFile`（正常路径）；
        /// 2) 若文档仍显示，**只对 MDI 文档子窗口**投 WM_CLOSE（等同用户按 Ctrl+F4），
        ///    绝不碰顶层主框架窗口——否则会把整个 CAXA 3D 关掉。
        /// 判据 = 屏幕标题里不再出现该文件名。
        /// </summary>
        private static bool CloseDocSafely(CaxaSession session, IntPtr pDoc,
            string fileName, string procName, Action<string> log,
            NameUpdateOptions opt, string what)
        {
            // 新策略（探针实证）：Close2(true) 能关外链 .ics 的窗口，虽然 CAXA 内部计数不变。
            // 不再用 IsDocTitleShown / FindDocChildWindow / WM_CLOSE 这些旧判据（MDI 架构下窗口天然含装配体标题）。
            int hr = pDoc != IntPtr.Zero ? TryCloseDoc(session, pDoc, what) : HR_FAIL;
            if (pDoc != IntPtr.Zero)
                log("  " + what + " Close2 hr=0x" + hr.ToString("X8"));

            if (hr != 0)
            {
                log("  " + what + " 结果：Close2 返回失败 hr=0x" + hr.ToString("X8"));
                return false;
            }

            // 给窗口关闭一点时间
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();

            if (opt.WaitCondition != null)
                opt.WaitCondition("等「" + fileName + "」窗口关闭", 3000,
                    delegate { return true; /* Close2 hr==0 就是成功 */ });

            log("  " + what + " 结果：Close2 执行成功（窗口已关）");
            return true;
        }

        /// <summary>调用「int 参数 + 接口指针返回值」的方法（失败返回 Zero，调用方负责 Release）。</summary>
        private static IntPtr CallIntArgObj(IntPtr pIface, int slot, int arg, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：函数指针无效");
                    return IntPtr.Zero;
                }
                FnIntArgObj f = (FnIntArgObj)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnIntArgObj));
                IntPtr pv;
                int hr = f(pIface, arg, out pv);
                Diagnostics.Trace(string.Format("{0} hr=0x{1:X} ptr=0x{2:X}", what, hr, pv.ToInt64()));
                return hr == 0 ? pv : IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return IntPtr.Zero;
            }
        }

        /// <summary>读取配置名（IZConfiguration::get_Name）。</summary>
        private static string ReadConfigName(IntPtr pCfg)
        {
            try
            {
                IntPtr fn = Slot(pCfg, SlotCfgGetName);
                if (!LooksCallable(fn)) return "<未知>";
                FnGetBstr f = (FnGetBstr)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetBstr));
                IntPtr pbstr;
                int hr = f(pCfg, out pbstr);
                if (hr == 0 && pbstr != IntPtr.Zero)
                {
                    try { return Marshal.PtrToStringBSTR(pbstr) ?? ""; }
                    finally { Marshal.FreeBSTR(pbstr); }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ReadConfigName EX: " + ex.Message);
            }
            return "<未知>";
        }

        /// <summary>把一个配置描述成一行文本：名称 / 是否活动 / 是否默认 / 是否钣金展开配置。</summary>
        private static string DescribeConfiguration(IntPtr pCfg, string tag)
        {
            short isActive = 0, isDefault = 0, isFlat = 0;
            TryReadShortProp(pCfg, SlotCfgGetIsActive, "cfg.get_IsActive", out isActive);
            TryReadShortProp(pCfg, SlotCfgGetIsDefault, "cfg.get_IsDefault", out isDefault);
            TryReadShortProp(pCfg, SlotCfgGetIsFlat, "cfg.get_IsOfFlatSheetMetalPart", out isFlat);
            return "  " + tag + " \"" + ReadConfigName(pCfg) + "\""
                + "  活动=" + (isActive != 0 ? "是" : "否")
                + "  默认=" + (isDefault != 0 ? "是" : "否")
                + "  钣金展开配置=" + (isFlat != 0 ? "是" : "否");
        }

        /// <summary>
        /// 列出文档的全部配置（多配置坑点取证）。返回同时输出的多行文本；
        /// activeName/defaultName 给出当前活动配置与默认配置名，供展开前后对比。
        /// </summary>
        private static string DumpConfigurations(IntPtr pScene, Action<string> log, string title,
            out string activeName, out string defaultName, out int configCount)
        {
            activeName = ""; defaultName = ""; configCount = -1;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Action<string> put = delegate(string s) { sb.AppendLine(s); if (log != null) log(s); };

            IntPtr pMgrRaw = CallGetObj(pScene, SlotSceneGetConfigurationMgr,
                "IZSceneDoc.get_ConfigurationMgr");
            if (pMgrRaw == IntPtr.Zero)
            {
                put("【配置】" + title + "：取不到 ConfigurationMgr（该版本可能不提供此接口）");
                return sb.ToString().TrimEnd('\r', '\n');
            }
            IntPtr pMgr = IntPtr.Zero;
            try
            {
                if (TryQI(pMgrRaw, IID_IZConfigurationMgr, out pMgr) != 0 || pMgr == IntPtr.Zero)
                {
                    put("【配置】" + title + "：QI(IZConfigurationMgr) 失败");
                }
                else
                {
                    TryReadIntProp(pMgr, SlotCfgMgrCount, "cfgMgr.get_Count", out configCount);
                    put("【配置】" + title + "（共 " + configCount + " 个）");
                    for (int id = 0; id < (configCount > 0 ? configCount : 0); id++)
                    {
                        IntPtr pCfg = CallIntArgObj(pMgr, SlotCfgMgrGetById, id,
                            "cfgMgr.GetConfigurationById(" + id + ")");
                        if (pCfg == IntPtr.Zero) continue;
                        try { put(DescribeConfiguration(pCfg, "id=" + id)); }
                        finally { Marshal.Release(pCfg); }
                    }
                    IntPtr pA = CallGetObj(pMgr, SlotCfgMgrActive, "cfgMgr.get_ActiveConfiguration");
                    if (pA != IntPtr.Zero)
                    {
                        try { activeName = ReadConfigName(pA); }
                        finally { Marshal.Release(pA); }
                    }
                    IntPtr pD = CallGetObj(pMgr, SlotCfgMgrDefault, "cfgMgr.get_DefaultConfiguration");
                    if (pD != IntPtr.Zero)
                    {
                        try { defaultName = ReadConfigName(pD); }
                        finally { Marshal.Release(pD); }
                    }
                    put("  活动配置 = \"" + activeName + "\"   默认配置 = \"" + defaultName + "\"");
                }
            }
            finally
            {
                if (pMgr != IntPtr.Zero) Marshal.Release(pMgr);
                Marshal.Release(pMgrRaw);
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>
        /// 激活「默认配置」：CAXA 3D 2027 下钣金件为多配置，展开会把设计环境切到展开配置，
        /// 需要把默认配置重新激活，避免设计环境被留在展开态。
        /// </summary>
        private static string ActivateDefaultConfiguration(IntPtr pScene, Action<string> log)
        {
            IntPtr pMgrRaw = CallGetObj(pScene, SlotSceneGetConfigurationMgr,
                "IZSceneDoc.get_ConfigurationMgr(restore)");
            if (pMgrRaw == IntPtr.Zero) return "取不到 ConfigurationMgr";
            IntPtr pMgr = IntPtr.Zero;
            try
            {
                if (TryQI(pMgrRaw, IID_IZConfigurationMgr, out pMgr) != 0 || pMgr == IntPtr.Zero)
                    return "QI(IZConfigurationMgr) 失败";
                IntPtr pDef = CallGetObj(pMgr, SlotCfgMgrDefault, "cfgMgr.get_DefaultConfiguration");
                if (pDef == IntPtr.Zero) return "默认配置对象为空";
                try
                {
                    string defName = ReadConfigName(pDef);
                    IntPtr fn = Slot(pDef, SlotCfgActivate);
                    if (!LooksCallable(fn)) return "IZConfiguration.Activate 函数指针无效";
                    FnShortArg f = (FnShortArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnShortArg));
                    int hr = f(pDef, unchecked((short)-1));   // VARIANT_TRUE
                    Diagnostics.Log(string.Format(
                        "IZConfiguration.Activate(默认=\"{0}\") hr=0x{1:X}", defName, hr));
                    if (log != null)
                        log("  激活默认配置 \"" + defName + "\" hr=0x" + hr.ToString("X8"));
                    return "hr=0x" + hr.ToString("X8") + "（激活的默认配置=\"" + defName + "\"）";
                }
                finally { Marshal.Release(pDef); }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ActivateDefaultConfiguration EX: " + ex.Message);
                return "异常：" + ex.Message;
            }
            finally
            {
                if (pMgr != IntPtr.Zero) Marshal.Release(pMgr);
                Marshal.Release(pMgrRaw);
            }
        }

        /// <summary>
        /// 在「外部链接文档上下文」执行探针：装配体内的钣金件是外部链接的真实件，但在装配体上下文直接 Unfold 会弹
        /// 「这个零件被链接了…打开链接的设计环境然后展开它」并返回 hr=0x1。
        /// 因此本入口：读选中件外部链接路径 → OpenFile2 打开/定位该同名 .ics → 设为活动文档
        /// → 在该文档树中定位钣金件 → 读状态 →（可选）Unfold() → 前后名称 diff + 弹窗取证。
        /// 全程不保存、不关闭文档；打开的链接文档保持打开，由用户决定。
        /// </summary>
        public static SheetMetalProbeResult RunSheetMetalProbeInLinkedDoc(CaxaSession session, bool callUnfold,
            bool restoreConfig, Action<string> log, Func<bool> shouldStop, Action<int> pump,
            NameUpdateOptions opt)
        {
            if (log == null) log = delegate(string s) { Diagnostics.Log(s); };
            if (shouldStop == null) shouldStop = delegate { return false; };
            SheetMetalProbeResult r = new SheetMetalProbeResult();
            r.Mode = "外部链接文档上下文";

            if (session == null || session.BaseApp == IntPtr.Zero)
            {
                r.Notes.Add("会话为空：未连接 CAXA 3D。");
                return r;
            }

            IntPtr pLinkDoc = IntPtr.Zero;
            IntPtr pOrigDoc = IntPtr.Zero;  // 保存原始活动文档，结束后切回
            IntPtr pCurDocHeld = IntPtr.Zero;  // 如果当前活动文档就是目标链接文档
            string linkPath = "";
            string exbPath = "";
            string exbFileName = "";
            int popupClickBase = 0;
            bool icsClosedByUs = false;
            bool alreadyInLinkedDoc = false;
            bool prodFlow = (opt != null);
            IDisposable autoClick = null;   // 代点引擎的租约：只在正式流程借，finally 必还
            try
            {
                if (prodFlow)
                {
                    // 正式流程：整个流程期间允许代点（exb 与 ics 的「是/确定」弹窗）
                    PopupAutoYes.FileDialogTarget = null;
                    autoClick = PopupAutoYes.Borrow("钣金件设默认");
                }
                // ---- 1. 保存原始活动文档 + 检测当前是否已在目标链接文档 ----
                {
                    int hrOrig = TryCallGetObj(session.BaseApp, SlotActiveDoc, out pOrigDoc);
                    if (hrOrig != 0 || pOrigDoc == IntPtr.Zero)
                    {
                        r.Notes.Add("当前没有活动文档（请先打开一个设计场景）。");
                        return r;
                    }
                    IntPtr pZOrig;
                    string origDocName = "";
                    if (TryQI(pOrigDoc, IID_IZDoc, out pZOrig) == 0 && pZOrig != IntPtr.Zero)
                    {
                        try { origDocName = TryReadBstrProp(pZOrig, SlotDocGetName, "orig.IZDoc.get_Name") ?? ""; }
                        finally { Marshal.Release(pZOrig); }
                    }
                    log("【当前活动文档】" + origDocName);

                    // 获取原始文档内的选中项（用于判断是否有外部链接）
                    IntPtr pCurScene;
                    if (TryQI(pOrigDoc, IID_IZSceneDoc, out pCurScene) != 0 || pCurScene == IntPtr.Zero)
                    {
                        r.Notes.Add("当前活动文档不是三维设计场景。");
                        return r;
                    }
                    r.HasScene = true;

                    IntPtr pSel = GetSelectedElement(pCurScene, "link");
                    if (pSel == IntPtr.Zero)
                    {
                        // 没选中项 —— 检测是否已经在 .ics 文档里（可能是上一次探针切过来的）
                        // 如果是，直接用当前文档当链接文档，在里面自己找钣金件
                        string ext = System.IO.Path.GetExtension(origDocName).ToLowerInvariant();
                        if (ext == ".ics")
                        {
                            log("  未选中任何零部件，但当前活动文档是 .ics，" +
                                "假定已在目标链接文档，直接在其中找钣金件。");
                            alreadyInLinkedDoc = true;
                            pLinkDoc = pOrigDoc;  // 共用指针（注意 Release 时序）
                            pCurDocHeld = IntPtr.Zero;
                        }
                        else
                        {
                            r.Notes.Add("未选中零部件：请先在装配体设计树中选中目标钣金件。");
                            log("【选中】未选中任何零部件");
                            Marshal.Release(pCurScene);
                            return r;
                        }
                    }
                    else
                    {
                        // 有选中项 —— 走原始路径：判外链、取路径、打开链接文档
                        try
                        {
                            r.HasSelection = true;
                            int t;
                            if (TryReadIntProp(pSel, SlotElemGetType, "get_Type(link)", out t))
                            {
                                r.SelectedType = t;
                                r.SelectedKind = MapElementKind(t);
                            }
                            r.SelectedName = ReadElementName(pSel, "get_Name(link)");
                            r.SelectedIsSheetMetal = (r.SelectedType == 20);
                            TryReadExternalLinkInto(pSel, r.SelectedType, out r.ExternallyLinked, out linkPath);
                            r.LinkedFile = linkPath;
                            log("【选中】" + r.SelectedKind + " \"" + r.SelectedName + "\"");
                            log("        外部链接 = " + (r.ExternallyLinked ? ("是：" + linkPath) : "否"));
                            if (!r.ExternallyLinked || string.IsNullOrEmpty(linkPath))
                            {
                                r.Notes.Add("选中件没有外部链接：它就在当前文档内。");
                                Marshal.Release(pCurScene);
                                return r;
                            }

                            // 检测：如果当前活动文档路径 === 目标外链路径，
                            // 说明上一次探针已经把活动文档切到了链接 .ics
                            // 直接跳过 OpenFile2，在当前文档操作
                            if (string.Equals(origDocName, linkPath,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                log("  当前活动文档就是目标链接文档（上一次探针遗留），跳过 OpenFile2。");
                                alreadyInLinkedDoc = true;
                                pLinkDoc = pOrigDoc;  // 共用指针
                            }
                        }
                        finally { Marshal.Release(pSel); }
                    }
                    Marshal.Release(pCurScene);
                }
                if (shouldStop()) return r;

                // ---- 1.5 工程图阶段：挪到 .ics 全部完成之后（见本方法末尾）----
                // 2026-10-27 用户定案：先走完 ics 流程，再走 exb 流程；
                // exb 只按 caxa3dtitleupdate 的「更新标题栏」五步流程执行。
                // 顺序对调的理由：ics 改名保存关闭后，exb 打开时读到的就是改名后的 3D 数据，
                // 弹窗更少、CRX 上下文独占，不会被 ics 阶段的命令排队干扰。
                if (shouldStop()) return r;

                // ---- 2. 打开 / 定位外部链接文档（仅当尚未在链接文档中时）----
                if (!alreadyInLinkedDoc)
                {
                    log("");
                    log("【打开外部链接文档】" + linkPath);
                    int alreadyOpen;
                    int hrOpen = TryOpenFile2(session.BaseApp, linkPath, out pLinkDoc, out alreadyOpen);
                    r.LinkedDocHr = "hr=0x" + hrOpen.ToString("X8")
                        + (alreadyOpen != 0 ? "（该文档原本已打开，直接复用）" : "（本次新打开）");
                    log("  OpenFile2 " + r.LinkedDocHr);

                    // OpenFile2 对 .ics 和 .exb 都有假失败模式：返回 hr=0x80004005 ptr=0x0
                    // 但文件实际已经打开。Recovery：用 FindOpenDoc(GetOpenDocByName) 找回指针。
                    if (hrOpen != 0 || pLinkDoc == IntPtr.Zero)
                    {
                        log("  OpenFile2 返回值不可靠，尝试 FindOpenDoc 找回已打开的文档指针…");
                        System.Threading.Thread.Sleep(400);
                        Application.DoEvents();
                        pLinkDoc = FindOpenDoc(session, linkPath);
                        if (pLinkDoc != IntPtr.Zero)
                        {
                            log("  ✅ FindOpenDoc 命中！ptr=0x" + pLinkDoc.ToInt64().ToString("X"));
                            r.LinkedDocOpened = true;
                        }
                        else
                        {
                            log("  ❌ FindOpenDoc 也没找到 .ics 文档");
                            r.Notes.Add("OpenFile2 未能打开/定位外部链接文档，后续步骤跳过。");
                            return r;
                        }
                    }
                    else
                    {
                        r.LinkedDocOpened = true;
                    }
                    int hrAct = TryInvokePtrArg(session.BaseApp, SlotAppSetActiveDoc, pLinkDoc,
                        "IZBaseApp.set_ActiveDoc");
                    log("  set_ActiveDoc hr=0x" + hrAct.ToString("X8"));
                    // 原来是固定 pump(1200)：改为条件轮询，活动文档一旦切过去就继续（通常 <300ms）
                    string wantIcs = System.IO.Path.GetFileName(linkPath);
                    bool switched = opt.WaitCondition == null || opt.WaitCondition(
                        "等活动文档切到 .ics", 8000, delegate
                        {
                            string act = GetActiveDocName(session);
                            return act.Length > 0
                                && string.Equals(System.IO.Path.GetFileName(act), wantIcs,
                                    StringComparison.OrdinalIgnoreCase);
                        });
                    if (!switched) log("  活动文档切换确认超时，继续按链接文档处理");
                    if (shouldStop()) return r;
                }
                else
                {
                    r.LinkedDocOpened = true;
                    r.LinkedDocHr = "（已在目标链接文档中）";
                }

                // ---- 记录目标文档名 ----
                {
                    IntPtr pZDoc2;
                    if (TryQI(pLinkDoc, IID_IZDoc, out pZDoc2) == 0 && pZDoc2 != IntPtr.Zero)
                    {
                        try
                        {
                            r.TargetDocName = TryReadBstrProp(pZDoc2, SlotDocGetName, "IZDoc.get_Name(target)") ?? "";
                        }
                        finally { Marshal.Release(pZDoc2); }
                    }
                    if (string.IsNullOrEmpty(r.TargetDocName))
                        r.TargetDocName = string.IsNullOrEmpty(linkPath) ? "(当前活动文档)"
                            : System.IO.Path.GetFileName(linkPath);
                    log("  链接文档名：" + r.TargetDocName);
                }

                // ---- 3. 在链接文档中定位钣金件 ----
                IntPtr pScene2 = IntPtr.Zero;
                if (TryQI(pLinkDoc, IID_IZSceneDoc, out pScene2) != 0 || pScene2 == IntPtr.Zero)
                {
                    r.Notes.Add("链接文档不是三维设计场景（QI IZSceneDoc 失败）。");
                    return r;
                }
                IntPtr pSmElem = IntPtr.Zero;
                try
                {
                    // 多配置取证（展开前）
                    string cfgActiveBefore, cfgDefaultBefore;
                    int cfgCount;
                    log("");
                    r.ConfigBefore = DumpConfigurations(pScene2, log, "链接文档 · 展开前",
                        out cfgActiveBefore, out cfgDefaultBefore, out cfgCount);
                    r.ActiveConfigBefore = cfgActiveBefore;
                    if (shouldStop()) return r;

                    log("");
                    log("【链接文档设计树】");
                    IntPtr pTop = CallGetObj(pScene2, SlotSceneGetTopElement, "GetTopElement(link)");
                    if (pTop != IntPtr.Zero)
                    {
                        try { WalkElement(pTop, 0, r, log, shouldStop, 6, 400); }
                        finally { Marshal.Release(pTop); }
                    }
                    else
                    {
                        r.Notes.Add("链接文档 GetTopElement 返回空，无法 dump 设计树。");
                        log("  （GetTopElement 为空）");
                    }
                    log("（链接文档设计树共 " + r.Tree.Count + " 个元素）");
                    if (shouldStop()) return r;

                    IntPtr pref = IntPtr.Zero, fb = IntPtr.Zero;
                    string prefName = "", fbName = "";
                    IntPtr pTop2 = CallGetObj(pScene2, SlotSceneGetTopElement, "GetTopElement(link2)");
                    if (pTop2 != IntPtr.Zero)
                    {
                        try
                        {
                            SearchSheetMetal(pTop2, 0, 6, ref pref, ref fb, ref prefName, ref fbName);
                        }
                        finally { Marshal.Release(pTop2); }
                    }
                    if (pref != IntPtr.Zero)
                    {
                        pSmElem = pref;
                        r.FoundSheetMetalName = prefName;
                        if (fb != IntPtr.Zero) Marshal.Release(fb);
                    }
                    else if (fb != IntPtr.Zero)
                    {
                        pSmElem = fb;
                        r.FoundSheetMetalName = fbName;
                    }
                    if (pSmElem == IntPtr.Zero)
                    {
                        r.Notes.Add("链接文档里没有找到钣金零件(type 20)。");
                        log("  （未找到钣金零件）");
                        return r;
                    }
                    log("【链接文档中的钣金件】\"" + r.FoundSheetMetalName + "\"");
                    if (shouldStop()) return r;

                    // ---- 4. 读状态 →（可选）Unfold → 前后 diff ----
                    IntPtr pSm;
                    if (TryQI(pSmElem, IID_IZSheetMetalPart, out pSm) != 0 || pSm == IntPtr.Zero)
                    {
                        r.Notes.Add("链接文档中的钣金件 QI(IZSheetMetalPart) 失败。");
                        return r;
                    }
                    try
                    {
                        r.SelectedType = 20;
                        r.SelectedKind = MapElementKind(20);
                        r.SelectedName = r.FoundSheetMetalName;
                        r.SelectedIsSheetMetal = true;

                        ReadSheetMetalState(pSm, r, "link.before");
                        DumpSheetMetalState(r, log, "链接文档 · 展开前");

                        // 抓住展开件元素本身（若拿得到），展开后用同一元素读名——最直接的名称变化证据
                        IntPtr pFlatHeld = IntPtr.Zero;
                        string flatNameBefore = "<无>";
                        IntPtr pFlatBefore = CallGetObj(pSm, SlotSmGetFlatPart, "SM.GetFlatPart(link.before)");
                        if (pFlatBefore != IntPtr.Zero)
                        {
                            IntPtr pe;
                            if (TryQI(pFlatBefore, IID_IZElement, out pe) == 0 && pe != IntPtr.Zero)
                            {
                                pFlatHeld = pe;
                                flatNameBefore = ReadElementName(pe, "get_Name(flat.before)");
                            }
                            Marshal.Release(pFlatBefore);
                        }
                        try
                        {
                            string popupBefore = CaptureCaXaWindows("before");

                            // ---- Unfold 前的状态检查 ----
                            short currentIsFolded = -1;
                            TryReadShortProp(pSm, SlotSmGetIsFolded, "SM.get_IsFolded(pre-unfold)", out currentIsFolded);
                            log("  【展开前检查】IsFolded=" + currentIsFolded
                                + (currentIsFolded != 0 ? "（折弯态，可展开）" : "（已在展开态）"));

                            if (callUnfold && !shouldStop())
                            {
                                // 如果已经在展开态（IsFolded=False），先 Refold 回到默认配置再展开
                                if (currentIsFolded == 0)
                                {
                                    log("→ 已在展开态，先调 Refold() 回到默认配置…");
                                    int hrR = TryCallNoArg(pSm, SlotSmRefold, "Refold(before-unfold)");
                                    if (hrR == 0)
                                    {
                                        TryReadShortProp(pSm, SlotSmGetIsFolded,
                                            "SM.get_IsFolded(after-refold)", out currentIsFolded);
                                        log("  Refold 后 IsFolded=" + currentIsFolded);
                                    }
                                }

                                // 确保在折叠态再调 Unfold
                                log("→ 在链接文档中调用 IZSheetMetalPart.Unfold()（钣金展开）…");
                                int hrU = TryCallNoArg(pSm, SlotSmUnfold, "SM.Unfold(linked)");
                                r.UnfoldCalled = true;
                                r.UnfoldHr = hrU;
                                // 展开完成判据修正（2026-10-26 实证）：IsFolded 读的是折弯件属性，
                                // 展开后不会变 0（两次 15s 必超时死判据）；Unfold 实为同步返回，
                                // 真正的后置条件是 GetFlatPart 能取到展开件——改等它，通常立即成立。
                                if (hrU == 0 && opt.WaitCondition != null)
                                {
                                    opt.WaitCondition("等展开件可获取", 5000, delegate
                                    {
                                        IntPtr pF = CallGetObj(pSm, SlotSmGetFlatPart,
                                            "SM.GetFlatPart(poll)");
                                        if (pF == IntPtr.Zero) return false;
                                        Marshal.Release(pF);
                                        return true;
                                    });
                                }

                                // ---- 展开后：显式 set_Name 改名（核心！Unfold 只切配置，不改名字）----
                                if (hrU == 0 && !shouldStop())
                                {
                                    // 计算期望名："展开" + 成品件名
                                    string expectedFlatName = "展开" + r.SelectedName;
                                    // 2026-11-05 13:2x 实机：成品件名本身带「展开」（外链里当前配置不是默认的
                                    // 钣金件在设计树里就是这种名字）时，这个名字会变成「展开展开…」。
                                    // 只点名、不改判据 —— 宿主 Unfold 自己给出的也是同一个串。
                                    if (r.SelectedName.StartsWith("展开", StringComparison.Ordinal))
                                        log("⚠ 成品件名本身已带「展开」前缀 ⇒ 展开件名将叫 \"" + expectedFlatName +
                                            "\"（双前缀，源数据如此，本流程不改判据）");
                                    log("→ 展开成功，显式设置展开件名为：\"" + expectedFlatName + "\"");

                                    IntPtr pFlatNew = CallGetObj(pSm, SlotSmGetFlatPart,
                                        "SM.GetFlatPart(after-unfold)");
                                    if (pFlatNew != IntPtr.Zero)
                                    {
                                        IntPtr pFlatElem;
                                        if (TryQI(pFlatNew, IID_IZElement, out pFlatElem) == 0
                                            && pFlatElem != IntPtr.Zero)
                                        {
                                            try
                                            {
                                                string beforeRename = ReadElementName(pFlatElem,
                                                    "get_Name(flat.before-rename)");
                                                log("  改名前展开件名: \"" + beforeRename + "\"");
                                                int hrN = TrySetElementName(pFlatElem, expectedFlatName,
                                                    "set_Name(flat)");
                                                r.NameChanged = (hrN == 0);
                                                // 验证改名结果（set_Name 同步生效，不需要再等）
                                                string afterRename = ReadElementName(pFlatElem,
                                                    "get_Name(flat.after-rename)");
                                                log("  改名后展开件名: \"" + afterRename + "\"");
                                                if (pFlatHeld != IntPtr.Zero)
                                                {
                                                    // 同步更新 held 指针的名字（用于后面的对比）
                                                    Marshal.Release(pFlatHeld);
                                                }
                                                if (TryQI(pFlatNew, IID_IZElement, out pFlatHeld) == 0)
                                                {
                                                    // 重新拿一个 hold（QI 已经给了新引用）
                                                }
                                            }
                                            finally { Marshal.Release(pFlatElem); }
                                        }
                                        Marshal.Release(pFlatNew);
                                    }
                                    else
                                    {
                                        r.Notes.Add("展开后 GetFlatPart 返回空，无法 set_Name。");
                                        log("  展开后 GetFlatPart 返回空！");
                                    }

                                    // 刷新设计树/UI 缓存，让 set_Name 的效果可见（同步调用，无需再等）
                                    RefreshSceneUi(pScene2);
                                }
                            }

                            if (r.UnfoldCalled && !shouldStop())
                            {
                                r.AfterSelectedName = ReadElementName(pSmElem, "get_Name(sm.after)");
                                string flatNameAfter = (pFlatHeld != IntPtr.Zero)
                                    ? ReadElementName(pFlatHeld, "get_Name(flat.after)")
                                    : "<无（展开前未取到展开件元素）>";
                                r.AfterFlatPartName = DescribeRelatedElement(
                                    CallGetObj(pSm, SlotSmGetFlatPart, "SM.GetFlatPart(link.after-final)"),
                                    "GetFlatPart(link.after-final)");
                                r.AfterSiblingPartName = DescribeRelatedElement(
                                    CallGetObj(pSm, SlotSmGetSiblingPart, "SM.GetSiblingPart(link.after-final)"),
                                    "GetSiblingPart(link.after-final)");
                                // 如果 set_Name 成功了，NameChanged 应该已经被置 true

                                log("");
                                log("【展开后对比】");
                                log("  成品件名            : \"" + r.SelectedName + "\" -> \"" + r.AfterSelectedName + "\"");
                                log("  展开件名（同一元素）: \"" + flatNameBefore + "\" -> \"" + flatNameAfter + "\"");
                                log("  展开件名（重新获取）: " + r.AfterFlatPartName);
                                log("  兄弟件名（重新获取）: " + r.AfterSiblingPartName);
                                log("  名称是否变化        : " + (r.NameChanged ? "是" : "否"));
                                r.PopupInfo = CaptureCaXaWindows("after");
                                log("");
                                log("【窗口取证】");
                                log("  展开前：" + Environment.NewLine + popupBefore);
                                log("  展开后：" + Environment.NewLine + r.PopupInfo);

                                // 多配置取证（展开后）+ 可选恢复默认配置
                                log("");
                                string cfgActiveAfter, cfgDefaultAfter;
                                int cfgCountAfter;
                                r.ConfigAfterUnfold = DumpConfigurations(pScene2, log, "链接文档 · 展开后",
                                    out cfgActiveAfter, out cfgDefaultAfter, out cfgCountAfter);
                                r.ActiveConfigAfter = cfgActiveAfter;
                                if (restoreConfig && !shouldStop())
                                {
                                    log("→ 恢复默认配置：调 Refold()（比 Activate(default) 更贴近 UI 流程）…");
                                    int hrRefold = TryCallNoArg(pSm, SlotSmRefold, "Refold(restore-default)");
                                    r.ConfigRestoreHr = "Refold() hr=0x" + hrRefold.ToString("X8");
                                    if (hrRefold != 0)
                                    {
                                        log("  Refold 失败，回退 Activate(default)…");
                                        r.ConfigRestoreHr += " | Activate="
                                            + ActivateDefaultConfiguration(pScene2, log);
                                    }
                                    else
                                    {
                                        log("  Refold 成功，应已回到默认配置状态");
                                    }
                                    // 原来固定 pump(1000)+pump(500)：改成等「活动配置 == 默认配置」，
                                    // 并等折弯态回来（IsFolded=True），通常 200-400ms
                                    if (opt.WaitCondition != null)
                                    {
                                        opt.WaitCondition("等恢复默认配置生效", 10000, delegate
                                        {
                                            short f;
                                            if (!TryReadShortProp(pSm, SlotSmGetIsFolded,
                                                    "SM.get_IsFolded(restore-poll)", out f)) return false;
                                            return f != 0;
                                        });
                                    }
                                    string cfgActiveRestored, cfgDefaultRestored;
                                    int cfgCountRestored;
                                    r.ConfigAfterRestore = DumpConfigurations(pScene2, log,
                                        "链接文档 · 恢复默认配置后",
                                        out cfgActiveRestored, out cfgDefaultRestored, out cfgCountRestored);
                                    r.ActiveConfigAfterRestore = cfgActiveRestored;
                                }

                                // ---- 保存 .ics（正式流程）----
                                if (prodFlow && opt.SaveIcs && !shouldStop())
                                {
                                    log("");
                                    log("【保存 .ics】调用 IZDoc::Save …");
                                    int hrS = TrySaveDoc(pLinkDoc, "IZDoc.Save(ics)");
                                    r.IcsSaved = (hrS == 0);
                                    r.IcsSaveHr = "hr=0x" + hrS.ToString("X8");
                                    log("  Save " + r.IcsSaveHr + (r.IcsSaved ? "（已保存）" : "（保存失败）"));
                                    if (!r.IcsSaved) r.Notes.Add("保存 .ics 失败，改名结果未落盘。");
                                }

                                // ---- 关闭本次新打开的 .ics（原本就在该文档里时不关）----
                                // 判据只认「窗口真的消失」：14:57 实测 CloseFile 返回 0 但窗口仍在，
                                // 用户看到的就是"没关闭"。故 CloseFile 后再按窗口消失确认，必要时投 WM_CLOSE。
                                if (prodFlow && opt.CloseIcs && !alreadyInLinkedDoc && !shouldStop())
                                {
                                    log("");
                                    string icsName = System.IO.Path.GetFileName(linkPath);
                                    log("【关闭 .ics】" + icsName);

                                    // 关闭前恢复弹窗代点：万一弹「是否保存修改」不至于卡死
                                    PopupAutoYes.Suspend = false;

                                    bool goneIcs = CloseDocSafely(session, pLinkDoc, icsName,
                                        null, log, opt, "CloseFile(ics)");
                                    r.IcsClosed = goneIcs;
                                    r.IcsCloseHr = goneIcs ? "文档已关闭" : "文档仍在显示";
                                    if (goneIcs)
                                    {
                                        icsClosedByUs = true;   // 指针已失效，finally 不再 Release pLinkDoc
                                    }
                                    else
                                    {
                                        r.Notes.Add("自动关闭 .ics 未生效，请人工关闭该窗口。");
                                    }
                                }

                                // ---- 工程图阶段（.ics 已保存关闭，再打开 exb 更新标题栏）----
                                // 2026-10-27 用户定案：先走完 ics 流程，再走 exb 流程；
                                // exb 只按 caxa3dtitleupdate 的「更新标题栏」五步流程执行。
                                // 此时打开 exb 读到的就是改名后的 3D 数据，弹窗最少。
                                if (prodFlow && opt.OpenDrawingFirst && !alreadyInLinkedDoc
                                    && !string.IsNullOrEmpty(linkPath) && !shouldStop())
                                {
                                    exbPath = System.IO.Path.ChangeExtension(linkPath, ".exb");
                                    exbFileName = System.IO.Path.GetFileName(exbPath);
                                    if (System.IO.File.Exists(exbPath))
                                    {
                                        log("");
                                        log("【工程图阶段】刷新标题栏 → 保存关闭：" + exbPath);
                                        popupClickBase = PopupAutoYes.ClickCount;
                                        r.DrawingPath = exbPath;
                                        r.DrawingFound = true;
                                        RunDrawingStage(session, exbPath, exbFileName, log, opt, r,
                                            shouldStop, popupClickBase);
                                    }
                                    else
                                    {
                                        log("");
                                        log("【工程图】未找到同名 .exb（" + System.IO.Path.GetFileName(exbPath)
                                            + "），跳过工程图步骤。");
                                        r.Notes.Add("未找到同名 .exb，本次只更新 .ics。");
                                    }
                                }
                            }
                        }
                        finally
                        {
                            if (pFlatHeld != IntPtr.Zero) Marshal.Release(pFlatHeld);
                        }
                    }
                    finally { Marshal.Release(pSm); }
                }
                finally
                {
                    if (pSmElem != IntPtr.Zero) Marshal.Release(pSmElem);
                    if (pScene2 != IntPtr.Zero) Marshal.Release(pScene2);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("RunSheetMetalProbeInLinkedDoc EX: " + ex);
                r.Notes.Add("探针执行异常：" + ex.Message);
            }
            finally
            {
                // ---- 切回原始活动文档（如果之前切走过）----
                if (pOrigDoc != IntPtr.Zero && !alreadyInLinkedDoc
                    && session != null && session.BaseApp != IntPtr.Zero)
                {
                    int hrBack = TryInvokePtrArg(session.BaseApp, SlotAppSetActiveDoc, pOrigDoc,
                        "IZBaseApp.set_ActiveDoc(restore-orig)");
                    Diagnostics.Log("恢复原始活动文档 hr=0x" + hrBack.ToString("X8"));
                }
                // 指针释放：
                // - alreadyInLinkedDoc=true 时 pLinkDoc==pOrigDoc，只 Release pOrigDoc
                // - 已被 CloseFile 关闭的 .ics 指针已失效，绝不能再 Release
                if (alreadyInLinkedDoc || icsClosedByUs)
                {
                    // 不 Release pLinkDoc
                }
                else
                {
                    if (pLinkDoc != IntPtr.Zero) Marshal.Release(pLinkDoc);
                }
                if (pOrigDoc != IntPtr.Zero) Marshal.Release(pOrigDoc);

                if (prodFlow)
                {
                    PopupAutoYes.FileDialogTarget = null;
                    if (autoClick != null) autoClick.Dispose();   // 归零时代点关闭，只留迟到框巡检
                }
                // 无论成败都必须解除挂起，否则下次流程的 exb 弹窗永远不会被代点
                PopupAutoYes.Suspend = false;
            }
            return r;
        }

        #endregion
        #endregion
    }
}
