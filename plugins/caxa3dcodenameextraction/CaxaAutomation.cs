using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    ///   IZSceneDoc:       get_SelectionMgr = 27, GetFirstChild = 24, GetNextChild = 25,
    ///                     GetTopElement = 58
    ///   IZSelectionMgr:   GetSelectedElements = 10, SelectionsAvailable = 17,
    ///                     GetSelectedElementsZArray = 19, AddElementToSelection = 20
    ///   IZElement:        GetChildrenZArray = 21
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
        private static readonly Guid IID_IZDrawingDoc = new Guid("A7C6ECCB-D616-45F4-8E79-AF5DE63D3895");
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
        // 原生「替换零件/装配」只认 CAXA 自己的活选择集，投递前用它把选中件重新加入选择集
        private const int SlotSelMgrAddElementToSelection = 20;     // IZSelectionMgr::AddElementToSelection(IZElement, Boolean vbIgnoreFilter)
        private const int SlotSelMgrRemoveAllFromSelection = 22;    // IZSelectionMgr::RemoveAllFromSelection
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
        private const int SlotPartUpdate = 7;             // IZPart::Update()
        private const int SlotPartRegenerate = 18;        // IZPart::Regenerate()
        private const int SlotAsmGetExtLinkedInfo = 21;   // IZAssembly::GetExternallyLinkedInfo
        private const int SlotAsmUpdate = 7;              // IZAssembly::Update()
        private const int SlotAsmRegenerateParts = 17;    // IZAssembly::RegenerateParts(VARIANT_BOOL)
        private const int SlotSceneRegenerateParts = 21;  // IZSceneDoc::RegenerateParts(VARIANT_BOOL)
        private const int SlotSceneUpdate = 36;           // IZSceneDoc::Update()
        private const int SlotSceneUpdateGraphics = 83;   // IZSceneDoc::UpdateGraphics(UInt32)
        private const int SlotSceneUpdateBrowser = 84;    // IZSceneDoc::UpdateBrowser(UInt32)
        // 钣金件 Update 后会被切到展开配置（坑 111），收尾必须回折叠态：先 Refold，失败退激活默认配置
        private const int SlotSmRefold = 8;               // IZSheetMetalPart::Refold()
        private const int SlotSceneGetConfigurationMgr = 40; // IZSceneDoc::get_ConfigurationMgr
        private const int SlotCfgMgrDefault = 12;         // IZConfigurationMgr::get_DefaultConfiguration
        private const int SlotCfgGetName = 7;             // IZConfiguration::get_Name
        private const int SlotCfgActivate = 15;           // IZConfiguration::Activate(Boolean bUpdateAllConfigsFirst)
        // 来源：_probe-tools\out\tlb\2027\members.csv（2024 表同槽，跨版本一致）
        //   IZPart::SaveAs(String bstrFileName, Boolean vbLinkToScene)  → 槽 16
        //   IZAssembly::SaveAs(同上)                                     → 槽 14
        private const int SlotPartSaveAs = 16;            // IZPart::SaveAs
        private const int SlotAsmSaveAs = 14;             // IZAssembly::SaveAs
        // 设计树遍历（不读活选择）：_probe-tools\out\tlb\2027\members.csv 同表核对
        //   GetFirstChild / GetNextChild 是**无参**成对迭代器，游标状态挂在文档对象上
        //   GetTopElement 用作迭代器不可用时的兜底根
        private const int SlotSceneGetFirstChild = 24;        // IZSceneDoc::GetFirstChild
        private const int SlotSceneGetNextChild = 25;         // IZSceneDoc::GetNextChild
        private const int SlotSceneGetTopElement = 58;        // IZSceneDoc::GetTopElement
        private const int SlotElemGetChildrenZArray = 21;     // IZElement::GetChildrenZArray

        // ZIronCADApp 属性在默认接口上的 DISPID
        private const int DispidZIronCADApp = 0x2A;

        private const int HR_NOINTERFACE = unchecked((int)0x80004002);
        private const int HR_FAIL = unchecked((int)0x80004005);

        private const short VT_BSTR = 8;

        /// <summary>需要在界面上展示的四个目标字段（自定义属性精确名）。</summary>
        public static readonly string[] TargetFields = { "SC图号", "SC名称", "SC规格", "SC材料" };

        #region 原始 COM 互操作

        private const int DISPID_UNKNOWN = -1;

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
                string str = Marshal.PtrToStringBSTR(pb);
                Marshal.FreeBSTR(pb);
                return str;
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

        /// <summary>只读主窗口标题（移植自 caxa3dexbtracker，供 DrawingProbe 交叉校验），失败返回 null。</summary>
        public static string GetWindowTitle(CaxaSession session)
        {
            if (session == null || session.AppObject == null) return null;
            try { return TryDispatchGetString(session.AppObject, "WindowTitle"); }
            catch { return null; }
        }

        /// <summary>从 CAXA 标题 "[工程图文档4]" 里取文档名。</summary>
        public static string ExtractDocName(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;
            int l = title.LastIndexOf('[');
            int r = title.LastIndexOf(']');
            if (l >= 0 && r > l) return title.Substring(l + 1, r - l - 1).Trim();
            return title.Trim();
        }

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
        private delegate int FnAddElementToSelection(IntPtr self, IntPtr pElement, short vbIgnoreFilter);

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

        /// <summary>SaveAs(BSTR 文件名, VARIANT_BOOL 回链到场景)。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSaveAsLinked(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string bstrFileName, short vbLinkToScene);

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
        /// 带槽位防护的“[out,retval] 接口指针”无参调用：越界槽位（短 vtable 代理）直接判失败，
        /// 绝不发起调用。用于遍历设计树这类要连打十几个槽位的热路径。
        /// </summary>
        private static bool TryCallObjSafe(IntPtr pIface, int slot, string what, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Trace(what + " 跳过：slot " + slot + " 函数指针无效（短 vtable 代理）");
                    return false;
                }
                FnGetObj f = (FnGetObj)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetObj));
                int hr = f(pIface, out ppv);
                Diagnostics.Trace(string.Format("{0} hr=0x{1:X} ptr=0x{2:X}",
                    what, hr, ppv.ToInt64()));
                if (hr != 0 && ppv != IntPtr.Zero)
                {
                    Marshal.Release(ppv);
                    ppv = IntPtr.Zero;
                }
                return ppv != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                ppv = IntPtr.Zero;
                return false;
            }
        }

        /// <summary>IZArray::Count（槽 7）。失败或越界槽位返回 0。</summary>
        private static int ZArrayCount(IntPtr pArr)
        {
            IntPtr fn = Slot(pArr, SlotArrayCount);
            if (!LooksCallable(fn)) return 0;
            FnGetInt cnt = (FnGetInt)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetInt));
            int count;
            return cnt(pArr, out count) == 0 && count > 0 ? count : 0;
        }

        /// <summary>IZArray::Get（槽 9）。返回的指针由调用方 Release。</summary>
        private static bool ZArrayGet(IntPtr pArr, int index, out IntPtr pRaw)
        {
            pRaw = IntPtr.Zero;
            IntPtr fn = Slot(pArr, SlotArrayGet);
            if (!LooksCallable(fn)) return false;
            FnArrayGet get = (FnArrayGet)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnArrayGet));
            int hr = get(pArr, index, out pRaw);
            return hr == 0 && pRaw != IntPtr.Zero;
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
        /// 这个文档是不是图纸文档（QI IZDrawingDoc）。
        /// 为什么还要单独问一次：功能区代点「新图纸」之后插件手上没有对象引用，只能从
        /// GetAllOpenDocs 的差集里挑，而挑错一张就会在收尾时关掉用户自己的文档
        /// （实机 19:02/19:42 证明图纸环境里 ActiveDoc 回的是模型文档「设计1」，
        /// 靠它判据根本不成立）⇒ 类型 + QI + 差集三条同时过才接管。
        /// </summary>
        public static bool IsDrawingDoc(IZDoc doc)
        {
            if (doc == null) return false;
            IntPtr pUnk = IntPtr.Zero, pDrawing = IntPtr.Zero;
            try
            {
                pUnk = Marshal.GetIUnknownForObject(doc);
                Guid iid = IID_IZDrawingDoc;
                return Marshal.QueryInterface(pUnk, ref iid, out pDrawing) == 0 &&
                       pDrawing != IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("IsDrawingDoc 异常：" + ex.Message);
                return false;
            }
            finally
            {
                if (pDrawing != IntPtr.Zero) Marshal.Release(pDrawing);
                if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
            }
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
                    Diagnostics.LogPoll("选中件: type=" + info.ElementType + "(" + info.ElementKind +
                        ") name=[" + info.ElementName + "] 链接=" + (info.ExternallyLinked ? info.LinkedFile : "(无)"));
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

        /// <summary>
        /// IZPart/IZAssembly::GetExternallyLinkedInfo → 外部链接 .ics 路径。
        /// 只有「hr=0 且 linked=true 且路径非空」才返回路径，其余一律 null。
        /// </summary>
        private static string ReadLinkedFile(IntPtr pElem, int elemType)
        {
            // 装配体走 IZAssembly，其余（零件/钣金/刻面/线框/轮廓等）一律尝试 IZPart
            Guid iid = elemType == 2 ? IID_IZAssembly : IID_IZPart;
            int slot = elemType == 2 ? SlotAsmGetExtLinkedInfo : SlotPartGetExtLinkedInfo;
            IntPtr pObj;
            if (TryQI(pElem, iid, out pObj) != 0 || pObj == IntPtr.Zero) return null;
            try
            {
                IntPtr fn = Slot(pObj, slot);
                if (!LooksCallable(fn)) return null;
                FnGetLinkedInfo f = (FnGetLinkedInfo)Marshal.GetDelegateForFunctionPointer(
                    fn, typeof(FnGetLinkedInfo));
                short linked;
                IntPtr pbstr;
                int hr = f(pObj, out linked, out pbstr);
                // 逐节点审计只在 VerboseWalk 打开时落盘：11-03 实测一次走查 4002 节点全打，
                // 单轮日志 1.3 万行里 84% 是这一行，把「设计树定位」和 ④/⑥ 取证全埋掉了。
                if (Diagnostics.VerboseWalk)
                    Diagnostics.Trace(string.Format("GetExternallyLinkedInfo(type={0}) hr=0x{1:X} linked={2}",
                        elemType, hr, linked));
                string path = null;
                if (hr == 0 && pbstr != IntPtr.Zero)
                {
                    try { path = Marshal.PtrToStringBSTR(pbstr); }
                    finally { Marshal.FreeBSTR(pbstr); }
                }
                if (hr != 0 || linked == 0 || string.IsNullOrEmpty(path)) return null;
                return path;
            }
            finally
            {
                Marshal.Release(pObj);
            }
        }

        /// <summary>把元素的外链信息写进 SelectedPartInfo（不改变“未链接”时的空值语义）。</summary>
        private static void TryReadExternalLink(IntPtr pElem, int elemType, SelectedPartInfo info)
        {
            string path = ReadLinkedFile(pElem, elemType);
            if (path == null) return;
            info.LinkedFile = path;
            info.ExternallyLinked = true;
        }

        /// <summary>
        /// 带图独立第 ③ 步的**首选**做法：把选中零部件另存为新文件，并让**当前这个元素**回链到它
        /// （IZPart::SaveAs(file, true) / IZAssembly::SaveAs(file, true)）。
        /// 与原生「替换零件/装配」命令的差别（实机日志 11:39:56 实证）：那条命令会换一个元素对象，
        /// 钣金零件(type=20) 被替成普通零件(type=1)，共享属性管理器随之 SetValueByName=E_FAIL；
        /// 本调用不动元素对象，类型、位姿、父子关系与属性管理器全部保留，其它共享旧链接的实例仍指旧文件。
        /// 返回 hr==0 只代表调用被接受，最终判据仍要看链接是否真变（调用方轮询）。
        /// </summary>
        public static bool SaveSelectedPartToNewFileAndRelink(CaxaSession session, string newPath, out string error)
        {
            error = null;
            if (session == null || string.IsNullOrEmpty(newPath))
            {
                error = "参数为空。";
                return false;
            }

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            try
            {
                if (pDoc == IntPtr.Zero) { error = "没有活动文档。"; return false; }
                IntPtr pElem = GetSelectedElement(pScene, "saveas");
                if (pElem == IntPtr.Zero) { error = "当前没有选中零部件。"; return false; }
                try
                {
                    int elemType;
                    if (!TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(saveas)", out elemType))
                        elemType = 1;

                    // 装配体走 IZAssembly(槽 14)，其余（零件/钣金/刻面/线框/轮廓）走 IZPart(槽 16)
                    Guid iid = elemType == 2 ? IID_IZAssembly : IID_IZPart;
                    int slot = elemType == 2 ? SlotAsmSaveAs : SlotPartSaveAs;
                    IntPtr pObj;
                    if (TryQI(pElem, iid, out pObj) != 0 || pObj == IntPtr.Zero)
                    {
                        error = "选中元素不支持 " + (elemType == 2 ? "IZAssembly" : "IZPart") + "::SaveAs。";
                        return false;
                    }
                    try
                    {
                        IntPtr fn = Slot(pObj, slot);
                        if (!LooksCallable(fn)) { error = "slot " + slot + " 不可调用。"; return false; }
                        FnSaveAsLinked f = (FnSaveAsLinked)Marshal.GetDelegateForFunctionPointer(
                            fn, typeof(FnSaveAsLinked));
                        int hr = f(pObj, newPath, (short)-1);   // VARIANT_BOOL true = -1
                        Diagnostics.Trace(string.Format(
                            "SaveAs(type={0} 槽{1}) \"{2}\" hr=0x{3:X}", elemType, slot, newPath, hr));
                        if (hr != 0)
                        {
                            error = "IZ" + (elemType == 2 ? "Assembly" : "Part") + "::SaveAs 返回 0x" +
                                hr.ToString("X");
                            return false;
                        }
                        return true;
                    }
                    finally { Marshal.Release(pObj); }
                }
                finally { Marshal.Release(pElem); }
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        /// <summary>
        /// 把当前选中的零部件重新按回 CAXA 自己的选择集（IZSelectionMgr::AddElementToSelection，槽 20）。
        /// 原生「替换零件/装配」的帮助串是「替换选择的外部链接零件/装配」——它只认活选择，
        /// 投递前不补这一步，命令会安静地什么都不做（实机 11:38 那轮 74 秒空等）。
        /// </summary>
        public static bool ReassertNativeSelection(CaxaSession session, out string error)
        {
            error = null;
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            try
            {
                if (pDoc == IntPtr.Zero) { error = "没有活动文档。"; return false; }
                IntPtr fn = Slot(pScene, SlotSceneSelectionMgr);
                if (!LooksCallable(fn)) { error = "get_SelectionMgr 不可调用。"; return false; }
                FnGetObj fSel = (FnGetObj)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetObj));
                IntPtr pSelMgr;
                int hr = fSel(pScene, out pSelMgr);
                if (hr != 0 || pSelMgr == IntPtr.Zero)
                {
                    error = "取不到 IZSelectionMgr（hr=0x" + hr.ToString("X") + "）。";
                    return false;
                }
                try
                {
                    IntPtr pElem = GetSelectedElement(pScene, "reassert");
                    if (pElem == IntPtr.Zero)
                    {
                        error = "当前没有选中零部件，原生替换命令没有作用对象。";
                        return false;
                    }
                    try
                    {
                        IntPtr f2 = Slot(pSelMgr, SlotSelMgrAddElementToSelection);
                        if (!LooksCallable(f2)) { error = "槽 20 不可调用。"; return false; }
                        FnAddElementToSelection add =
                            (FnAddElementToSelection)Marshal.GetDelegateForFunctionPointer(
                                f2, typeof(FnAddElementToSelection));
                        int hr2 = add(pSelMgr, pElem, (short)-1);   // vbIgnoreFilter = true
                        Diagnostics.Trace(string.Format(
                            "ReassertNativeSelection: AddElementToSelection(槽20) hr=0x{0:X}", hr2));
                        if (hr2 != 0) error = "AddElementToSelection 返回 0x" + hr2.ToString("X");
                        return hr2 == 0;
                    }
                    finally { Marshal.Release(pElem); }
                }
                finally { Marshal.Release(pSelMgr); }
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        /// <summary>
        /// 替换刚结束时把新元素按回活选择（IZSelectionMgr::RemoveAllFromSelection 槽 22 +
        /// AddElementToSelection 槽 20）。**这条写选择等于替 CAXA 收尾**：实机 16:40~16:42 五轮
        /// 都做了这一步，流程走完主界面照常可点；17:11/17:23 两轮把它删掉，流程一释放独占
        /// 主界面与插件窗口就一起点不动（收尾快照里没有模态框、主框架也没被禁用）。
        /// 但它确实会失败：实机 16:38:51 场景正在重建，指针作废抛 SEHException(0x80004005)。
        /// 所以这里**只记日志、绝不抛异常**，失败绝不能把已经成功的替换判成失败。
        /// </summary>
        public static bool SelectLinkedElement(CaxaSession session, string icsPath, out string error)
        {
            // 为什么要重试：原生「替换零件/装配」的插入例程是在我们点掉「确定」之后还在继续跑的。
            // 实机 18:41:59.942 投出确定 → 标出 59.961 设计树已能命中 → 59.997 写选择就抛
            // SEHException（整个场景里只剩 55 ms，元素指针已经被替换例程作废）。
            // 每一步都用**重新遍历设计树**拿到的当前指针，所以隔一会儿再来一次就能成，
            // 而这一步成不成，直接决定流程结束后主界面还点不点得动（见方法注释）。
            string last = null;
            error = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (SelectLinkedElementOnce(session, icsPath, out last)) return true;
                Diagnostics.Log("SelectLinkedElement: 第 " + attempt + " 次没成（" + last +
                    "），等一下重来");
                if (attempt < 3)
                {
                    // 场景重建：给 CAXA 时间把替换例程走完，再重新取指针写选择
                    System.Threading.Thread.Sleep(500);
                }
            }
            error = last;
            return false;
        }

        private static bool SelectLinkedElementOnce(CaxaSession session, string icsPath, out string error)
        {
            error = null;
            IntPtr pDoc, pScene;
            try
            {
                OpenActiveScene(session, out pDoc, out pScene);
            }
            catch (Exception ex) { error = ex.Message; return false; }
            try
            {
                if (pDoc == IntPtr.Zero || pScene == IntPtr.Zero)
                {
                    error = "没有活动文档。";
                    return false;
                }
                _walkNodes = 0;
                IntPtr pElem = FindLinkedElementPointer(pScene,
                    Path.GetFileNameWithoutExtension(icsPath));
                if (pElem == IntPtr.Zero)
                {
                    error = "设计树里没有该元素。";
                    return false;
                }
                try
                {
                    IntPtr pSelMgr;
                    if (!TryCallObjSafe(pScene, SlotSceneSelectionMgr,
                            "get_SelectionMgr(select)", out pSelMgr))
                    {
                        error = "取不到 IZSelectionMgr。";
                        return false;
                    }
                    try
                    {
                        IntPtr fDel = Slot(pSelMgr, SlotSelMgrRemoveAllFromSelection);
                        if (!LooksCallable(fDel)) { error = "槽 22 不可调用。"; return false; }
                        FnNoArg del = (FnNoArg)Marshal.GetDelegateForFunctionPointer(fDel, typeof(FnNoArg));
                        int hr1 = del(pSelMgr);
                        Diagnostics.LogPoll(string.Format(
                            "SelectLinkedElement: RemoveAllFromSelection(槽22) hr=0x{0:X}", hr1));

                        IntPtr fAdd = Slot(pSelMgr, SlotSelMgrAddElementToSelection);
                        if (!LooksCallable(fAdd)) { error = "槽 20 不可调用。"; return false; }
                        FnAddElementToSelection add =
                            (FnAddElementToSelection)Marshal.GetDelegateForFunctionPointer(
                                fAdd, typeof(FnAddElementToSelection));
                        int hr2 = add(pSelMgr, pElem, (short)-1);   // vbIgnoreFilter = true
                        Diagnostics.LogPoll(string.Format(
                            "SelectLinkedElement: AddElementToSelection(槽20) hr=0x{0:X}", hr2));
                        if (hr2 != 0) error = "hr=0x" + hr2.ToString("X");
                        return hr2 == 0;
                    }
                    finally { Marshal.Release(pSelMgr); }
                }
                finally { Marshal.Release(pElem); }
            }
            catch (Exception ex)
            {
                // SEHException / AccessViolation 都只当"这一步没做成"，不打断已经成功的替换
                error = "写选择异常：" + ex.Message;
                Diagnostics.Log("SelectLinkedElement EX: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        /// <summary>设计树定位结果（不依赖活选择）。</summary>
        internal sealed class LinkedElementHit
        {
            public int ElementType;
            public string ElementKind = "";
            public string ElementName = "";
            public string LinkedFile = "";
        }

        /// <summary>
        /// 轮询期遍历设计树的节点上限与深度上限。
        /// 4000 曾把「上支架」这类装配整棵树在 747 个零部件处截断（实机 4002 节点），
        /// 截断又与「真没找到」同值返回 ⇒ 判据假阴性。现在根层先扫一遍，DFS 只作兜底，
        /// 所以上限放到 12000 让它有机会走完；真截断时由 LastWalkTruncated 单独报出来。
        /// </summary>
        private const int WalkNodeCap = 12000;
        private const int WalkDepthCap = 32;
        private static int _walkNodes;

        // ---- 走查取证快照（只读，供 ④/⑥ 的进度行使用；不参与任何判定）----
        // 为什么需要：11-03 那两轮 88 s / 15 s 的 ⑥ 空转，日志里只有「未命中（遍历 4002 节点）」被
        // LogPoll 去重掉，既看不出走查被上限截断，也看不出命中与否 —— 整段是黑箱。
        public static int LastWalkNodes;
        public static int LastWalkParts;
        public static int LastWalkQiFail;
        public static string LastWalkTruncated = "无";
        private static int _walkParts;
        private static int _walkQiFail;
        private static string _walkTruncated = "无";

        /// <summary>
        /// 在整棵设计树里找外链指向 <paramref name="icsPath"/> 的元素，**不读活选择、也不写活选择**。
        /// 为什么必须有这条路：原生「替换零件/装配」执行期间 CAXA 会清空活选择，换完也不会把新元素
        /// 选回来（实机 15:53 那轮：IZArray.Count=0 持续 65 秒，直到用户手工点了一下替换后的件，
        /// 判据才读到新元素、探针才算「完成」）。
        /// 为什么这里绝不再顺手 AddElementToSelection：实机 16:38:51 替换已经生效，收尾把设计树走出来的
        /// 元素指针按回选择集时抛 SEHException(0x80004005)，整条流程被判失败 —— 替换刚结束时场景正在
        /// 重建，那个指针随时可能作废。要选中请让用户在设计树里点一下。
        /// 槽位：根层 IZSceneDoc::GetFirstChild(24)/GetNextChild(25) 是**无参**成对迭代器，
        /// 游标状态挂在文档对象上，必须在同一个 pScene 指针上连打；不可用时退回 GetTopElement(58)。
        /// 下钻 IZElement::GetChildrenZArray(21) → IZArray::Count(7)/Get(9)。
        /// </summary>
        public static LinkedElementHit FindByLinkedFile(CaxaSession session, string icsPath)
        {
            if (session == null || string.IsNullOrEmpty(icsPath)) return null;
            string targetBase = Path.GetFileNameWithoutExtension(icsPath);

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            try
            {
                if (pDoc == IntPtr.Zero || pScene == IntPtr.Zero) return null;

                _walkNodes = 0;
                _walkParts = 0;
                _walkQiFail = 0;
                _walkTruncated = "无";
                IntPtr pElem = FindLinkedElementPointer(pScene, targetBase);
                LastWalkNodes = _walkNodes;
                LastWalkParts = _walkParts;
                LastWalkQiFail = _walkQiFail;
                LastWalkTruncated = _walkTruncated;
                Diagnostics.LogPoll("设计树定位 " + targetBase + "：" +
                    (pElem == IntPtr.Zero ? "未命中" : "命中") + "（遍历 " + _walkNodes + " 节点，" +
                    "零部件 " + _walkParts + "，QI失败 " + _walkQiFail +
                    "，截断 " + _walkTruncated + "）");
                if (pElem == IntPtr.Zero) return null;

                LinkedElementHit hit = new LinkedElementHit();
                try
                {
                    int t;
                    if (TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(walk)", out t))
                    {
                        hit.ElementType = t;
                        hit.ElementKind = MapElementKind(t);
                    }
                    hit.ElementName = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name(walk)") ?? "";
                    hit.LinkedFile = ReadLinkedFile(pElem, hit.ElementType) ?? "";
                }
                finally { Marshal.Release(pElem); }
                return hit;
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        /// <summary>
        /// 取证：把场景**根层**子项（类型 / 名称 / 外链）整段写进日志。
        /// 只为区分两件事：替换提交后根层到底有没有出现新元素（替换真没生效）
        /// 还是新元素在但判据没读到（判据假阴性）。只读，不改选择、不改文档。
        /// </summary>
        public static void DumpTopLevelScene(CaxaSession session, string tag)
        {
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                if (pDoc == IntPtr.Zero || pScene == IntPtr.Zero)
                {
                    sb.AppendLine("  场景指针不可用");
                }
                else
                {
                    IntPtr pChild;
                    int i = 0;
                    if (TryCallObjSafe(pScene, SlotSceneGetFirstChild, "IZSceneDoc.GetFirstChild(顶层取证)", out pChild))
                    {
                        do
                        {
                            IntPtr pElem;
                            int qi = TryQI(pChild, IID_IZElement, out pElem);
                            Marshal.Release(pChild);
                            if (qi == 0 && pElem != IntPtr.Zero)
                            {
                                try
                                {
                                    int t;
                                    TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(顶层取证)", out t);
                                    string nm = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name(顶层取证)") ?? "";
                                    string lk = "";
                                    if (IsPartKind(t))
                                        lk = ReadLinkedFile(pElem, t) ?? "";
                                    sb.AppendLine(string.Format("  [{0}] type={1} name={2} link={3}", i, t, nm, lk));
                                }
                                finally { Marshal.Release(pElem); }
                            }
                            else sb.AppendLine("  [" + i + "] QI(IZElement) 失败 hr=0x" + qi.ToString("X"));
                            i++;
                        } while (TryCallObjSafe(pScene, SlotSceneGetNextChild, "IZSceneDoc.GetNextChild(顶层取证)", out pChild));
                        sb.AppendLine("  根层子项数=" + i);
                    }
                    else sb.AppendLine("  根层迭代器不可用");
                }
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
            Diagnostics.Dump("场景根层/" + tag, sb.ToString());
        }

        /// <summary>
        /// 只查**根层**子项自身的外链，不下钻（命中时所有权移交调用方）。
        /// 为什么必须有这一遍：11-03 09:09 实机——场景根层只有 2 个子项
        /// `[0] SC08020103.00 P80油槽`（747 个零部件）与 `[1] SC08020102.03 上支架`（替换目标），
        /// 而深度优先会先把 [0] 整棵树走完、撞上 4000 节点上限就返回 Zero，
        /// 结果 [1] 永远轮不到：外链在 09:09:59 就已经换成 `上支架678.ics`（根层取证已证实），
        /// 判据却一路报「未命中（截断 节点>4000）」，⑥ 空转满 120 秒。
        /// 装配最外层的外链件本来就挂在这一层，先扫一层就能把这类判定压到个位数 COM 调用。
        /// </summary>
        private static IntPtr ScanRootLinks(IntPtr pScene, string targetBase)
        {
            IntPtr pChild;
            if (!TryCallObjSafe(pScene, SlotSceneGetFirstChild,
                    "IZSceneDoc.GetFirstChild(根层)", out pChild)) return IntPtr.Zero;
            do
            {
                IntPtr pElem;
                int qi = TryQI(pChild, IID_IZElement, out pElem);
                Marshal.Release(pChild);
                if (qi != 0 || pElem == IntPtr.Zero) continue;

                bool match = false;
                try
                {
                    int t;
                    TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(根层)", out t);
                    if (IsPartKind(t))
                    {
                        string link = ReadLinkedFile(pElem, t);
                        match = link != null && string.Equals(
                            Path.GetFileNameWithoutExtension(link),
                            targetBase, StringComparison.OrdinalIgnoreCase);
                    }
                }
                finally { if (!match) Marshal.Release(pElem); }
                if (match) return pElem;
            } while (TryCallObjSafe(pScene, SlotSceneGetNextChild,
                "IZSceneDoc.GetNextChild(根层)", out pChild));
            return IntPtr.Zero;
        }

        /// <summary>在已取到的场景指针上遍历设计树，交出命中元素的 IZElement 指针（调用方 Release）。</summary>
        private static IntPtr FindLinkedElementPointer(IntPtr pScene, string targetBase)
        {
            // 第一遍：根层（个位数调用，命中就是毫秒级）
            IntPtr pHit = ScanRootLinks(pScene, targetBase);
            if (pHit != IntPtr.Zero) return pHit;

            IntPtr pChild;
            bool iterated = false;
            if (TryCallObjSafe(pScene, SlotSceneGetFirstChild, "IZSceneDoc.GetFirstChild", out pChild))
            {
                iterated = true;
                do
                {
                    IntPtr pElem = SearchForLink(pChild, targetBase, 0);
                    if (pElem != IntPtr.Zero) return pElem;
                } while (TryCallObjSafe(pScene, SlotSceneGetNextChild, "IZSceneDoc.GetNextChild", out pChild));
            }
            if (!iterated)
            {
                IntPtr top;
                if (TryCallObjSafe(pScene, SlotSceneGetTopElement, "IZSceneDoc.GetTopElement", out top))
                {
                    Diagnostics.Trace("设计树定位：根层迭代器不可用，改走 GetTopElement 兜底");
                    return SearchForLink(top, targetBase, 0);
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 深度优先：命中返回 IZElement 指针（所有权移交调用方），未命中返回 Zero（入参已释放）。
        /// </summary>
        private static IntPtr SearchForLink(IntPtr pRaw, string targetBase, int depth)
        {
            if (pRaw == IntPtr.Zero) return IntPtr.Zero;
            if (depth > WalkDepthCap || ++_walkNodes > WalkNodeCap)
            {
                // 取证：把「被上限截断」单独记下来。以前它和「真的没找到」返回同一个 Zero，
                // 汇总行只写「未命中」，88 秒空转时完全看不出走查根本没走完。
                if (_walkTruncated == "无")
                    _walkTruncated = depth > WalkDepthCap ? "深度>" + WalkDepthCap : "节点>" + WalkNodeCap;
                Marshal.Release(pRaw);
                return IntPtr.Zero;
            }

            IntPtr pElem;
            int qi = TryQI(pRaw, IID_IZElement, out pElem);
            Marshal.Release(pRaw);
            if (qi != 0 || pElem == IntPtr.Zero)
            {
                _walkQiFail++;
                return IntPtr.Zero;
            }

            int etype;
            TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(walk)", out etype);
            if (IsPartKind(etype))
            {
                _walkParts++;
                string link = ReadLinkedFile(pElem, etype);
                if (link != null && string.Equals(Path.GetFileNameWithoutExtension(link),
                        targetBase, StringComparison.OrdinalIgnoreCase))
                    return pElem;
            }

            // 只在「场景根(0) + 零部件类」里继续下钻：特征/面/边这些子树不可能挂外链件，
            // 全走一遍会让 500ms 一次的轮询变成整棵特征树的重复扫描。
            IntPtr pArr;
            if ((etype == 0 || IsPartKind(etype)) &&
                TryCallObjSafe(pElem, SlotElemGetChildrenZArray,
                    "IZElement.GetChildrenZArray(walk)", out pArr))
            {
                try
                {
                    int count = ZArrayCount(pArr);
                    for (int i = 0; i < count && _walkNodes <= WalkNodeCap; i++)
                    {
                        IntPtr pKid;
                        if (!ZArrayGet(pArr, i, out pKid)) continue;
                        IntPtr hit = SearchForLink(pKid, targetBase, depth + 1);
                        if (hit != IntPtr.Zero) return hit;
                    }
                }
                finally { Marshal.Release(pArr); }
            }
            Marshal.Release(pElem);
            return IntPtr.Zero;
        }

        /// <summary>
        /// 提交重建单个零部件：零件族 IZPart::Update(槽 7)，装配 IZAssembly::Update(槽 7)。
        ///
        /// 为什么是 Update 而不是 Regenerate(槽 18)：规则第 9 节 / 第 32 节 B —— set_* 之后必须
        /// Update 才把改动落到文档（实机 11-02 13:51：焊接底板 set_Name 回 hr=0 但回读仍是旧名，
        /// 随后 IZDoc::Save 直接 0x80004005；同表兄弟 13:35 折弯板那轮提交成功、保存 hr=0）。
        /// 另据否决 62：单条写入后 Regenerate 会把激活对象切到展开态零件，Update 才是提交动作。
        ///
        /// 钣金（type=20）Update 后会被切到「展开」配置（坑 111），所以收尾必须退回折叠态。
        /// </summary>
        private static string UpdateComponent(IntPtr pElem, int elemType, IntPtr pScene, string tag)
        {
            bool isAsm = elemType == 2;
            IntPtr pComp;
            int hrQi = TryQI(pElem, isAsm ? IID_IZAssembly : IID_IZPart, out pComp);
            if (hrQi != 0 || pComp == IntPtr.Zero)
            {
                // 类型与实际接口不一定对得上（装配里混挂的零件、集合体件），换另一个再试一次
                isAsm = !isAsm;
                hrQi = TryQI(pElem, isAsm ? IID_IZAssembly : IID_IZPart, out pComp);
                if (hrQi != 0 || pComp == IntPtr.Zero)
                    throw new InvalidOperationException(
                        string.Format("选中的元素既不是零件也不是装配体（QI 失败 hr=0x{0:X}）。", hrQi));
            }

            try
            {
                string iface = isAsm ? "IZAssembly.Update" : "IZPart.Update";
                int slot = isAsm ? SlotAsmUpdate : SlotPartUpdate;
                IntPtr fn = Slot(pComp, slot);
                if (!LooksCallable(fn))
                    throw new InvalidOperationException(
                        string.Format("{0} 函数指针无效（slot {1}）。", iface, slot));
                FnNoArg f = (FnNoArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnNoArg));
                int hr;
                try
                {
                    hr = f(pComp);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(iface + " 调用异常：" + ex.Message);
                }
                Diagnostics.Trace(string.Format("{0}({1}) hr=0x{2:X}", iface, tag, hr));
                if (hr != 0)
                    throw new InvalidOperationException(
                        string.Format("{0} 失败 hr=0x{1:X}", iface, hr));

                // 调用方（UpdateSelectedComponent / 改名补提交）已经在前面拼了元素类型，
                // 这里再说一遍「零件」会印成「钣金零件「X」零件已 Update 提交」（实机 14:54:15 日志）。
                string report = "已 Update 提交";
                if (elemType == 20)
                    report += "；钣金折叠态恢复：" + RestoreFoldedSheetMetal(pElem, pScene);
                return report;
            }
            finally
            {
                Marshal.Release(pComp);
            }
        }

        /// <summary>
        /// 把钣金件退回折叠配置：优先 IZSheetMetalPart::Refold(槽 8)（更贴近 UI 流程），
        /// 不可用或失败再激活场景默认配置。
        /// </summary>
        private static string RestoreFoldedSheetMetal(IntPtr pElem, IntPtr pScene)
        {
            // 「Refold 不可用」要能分清是哪一步不可用：实机 14:54:15 只给了一句结论，
            // 到底是 QI 失败、槽位取不到、还是调用回了错，无从判断（三种走法完全不同）。
            string why;
            IntPtr pSm;
            int qiHr = TryQI(pElem, IID_IZSheetMetalPart, out pSm);
            if (qiHr == 0 && pSm != IntPtr.Zero)
            {
                try
                {
                    IntPtr fn = Slot(pSm, SlotSmRefold);
                    if (LooksCallable(fn))
                    {
                        FnNoArg f = (FnNoArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnNoArg));
                        int hr = f(pSm);
                        Diagnostics.Trace(string.Format("IZSheetMetalPart.Refold hr=0x{0:X}", hr));
                        if (hr == 0) return "Refold 成功";
                        why = "Refold 回 hr=0x" + hr.ToString("X");
                    }
                    else why = "槽 " + SlotSmRefold + " 函数指针无效";
                }
                finally { Marshal.Release(pSm); }
            }
            else why = "QI(IZSheetMetalPart) 失败 hr=0x" + qiHr.ToString("X");
            Diagnostics.Log("钣金折叠态恢复：" + why + "，改用激活默认配置");
            return "Refold 未成（" + why + "），回退激活默认配置（" + ActivateDefaultConfiguration(pScene) + "）";
        }

        /// <summary>激活场景默认配置（IZConfigurationMgr::get_DefaultConfiguration → IZConfiguration::Activate）。</summary>
        private static string ActivateDefaultConfiguration(IntPtr pScene)
        {
            IntPtr pMgrRaw;
            if (!TryCallObjSafe(pScene, SlotSceneGetConfigurationMgr,
                    "IZSceneDoc.get_ConfigurationMgr(restore)", out pMgrRaw))
                return "取不到 ConfigurationMgr";

            IntPtr pMgr = IntPtr.Zero, pDef = IntPtr.Zero, pCfg = IntPtr.Zero;
            try
            {
                if (TryQI(pMgrRaw, IID_IZConfigurationMgr, out pMgr) != 0 || pMgr == IntPtr.Zero)
                    return "QI(IZConfigurationMgr) 失败";
                if (!TryCallObjSafe(pMgr, SlotCfgMgrDefault,
                        "cfgMgr.get_DefaultConfiguration(restore)", out pDef))
                    return "默认配置对象为空";
                if (TryQI(pDef, IID_IZConfiguration, out pCfg) != 0 || pCfg == IntPtr.Zero)
                    return "QI(IZConfiguration) 失败";

                string defName = TryReadBstrProp(pCfg, SlotCfgGetName, "IZConfiguration.get_Name(restore)");
                int hr = TryInvokeShortArg(pCfg, SlotCfgActivate, unchecked((short)-1),
                    "IZConfiguration.Activate(默认)");
                return hr == 0
                    ? "已激活默认配置「" + defName + "」"
                    : "激活默认配置「" + defName + "」失败 hr=0x" + hr.ToString("X");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ActivateDefaultConfiguration EX: " + ex.Message);
                return "异常：" + ex.Message;
            }
            finally
            {
                if (pCfg != IntPtr.Zero) Marshal.Release(pCfg);
                if (pDef != IntPtr.Zero) Marshal.Release(pDef);
                if (pMgr != IntPtr.Zero) Marshal.Release(pMgr);
                Marshal.Release(pMgrRaw);
            }
        }

        /// <summary>
        /// 「零部件重新生成」：对当前选中的零部件调 IZPart/IZAssembly::Update(槽 7) 提交重建，
        /// 钣金件退回折叠态，最后刷新设计树与图形区。返回给界面显示的中文说明。
        /// </summary>
        public static string UpdateSelectedComponent(CaxaSession session)
        {
            if (session == null)
                throw new InvalidOperationException("尚未连接 CAXA 3D。");

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pDoc == IntPtr.Zero)
                throw new InvalidOperationException("CAXA 3D 当前没有活动设计文档。");

            try
            {
                IntPtr pElem = GetSelectedElement(pScene, "update");
                if (pElem == IntPtr.Zero)
                    throw new InvalidOperationException("请先在 CAXA 里选中一个零件或装配体（设计树或图形区点一下）。");
                try
                {
                    int elemType;
                    TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(update)", out elemType);
                    string report = UpdateComponent(pElem, elemType, pScene, "update");
                    RefreshSceneUi(pScene);
                    string name = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name(update)");
                    return MapElementKind(elemType) + "「" + name + "」" + report;
                }
                finally { Marshal.Release(pElem); }
            }
            finally
            {
                Marshal.Release(pScene);
                Marshal.Release(pDoc);
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
                    Diagnostics.Trace(string.Format("RenameSelectedElement: \"{0}\" hr=0x{1:X}", newName, hr));
                    if (hr != 0)
                        throw new InvalidOperationException(
                            string.Format("修改零件名失败 hr=0x{0:X}", hr));

                    // 写不等于提交（规则第 32 节 B）。实机 11-02 13:51 焊接底板：set_Name 回 hr=0，
                    // 但 13:51:26.027 探针读回还是旧名「SC08020102.11-02 焊接底板」，而链接已经换成
                    // …4444.ics —— 名旧链新的文档 IZDoc::Save 直接 0x80004005。
                    // 所以回读不一致时补一次 IZPart::Update 提交（钣金顺带回折叠态），再看一眼。
                    string readback = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name(改名回读)");
                    if (readback != null && readback != newName)
                    {
                        Diagnostics.Log(string.Format(
                            "改名回读=「{0}」≠目标「{1}」，补 IZPart.Update 提交", readback, newName));
                        int elemType;
                        TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(改名提交)", out elemType);
                        string committed;
                        try
                        {
                            committed = UpdateComponent(pElem, elemType, pScene, "改名提交");
                        }
                        catch (Exception uex)
                        {
                            Diagnostics.Log("改名后补提交异常: " + uex.Message);
                            committed = "补提交失败：" + uex.Message;
                        }
                        RefreshSceneUi(pScene);
                        readback = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name(改名回读2)");
                        if (readback != null && readback != newName)
                            throw new InvalidOperationException(string.Format(
                                "改名没有落到元素上（目标「{0}」，回读「{1}」；{2}）。已中止、未保存，" +
                                "可在 CAXA 里选中该件点「零部件重新生成」后再试。",
                                newName, readback, committed));
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

        /// <summary>
        /// 保存活动文档（IZDoc::Save）。
        ///
        /// E_FAIL（0x80004005）在替换/改名之后常见，且多半是宿主还在收尾——换外链 + 重附约束实测
        /// 最长约 20 秒（坑 128），这时文档处于半提交状态，直接 Save 就吃 E_FAIL。所以带**有界重试**：
        /// 只在 E_FAIL 时重试，最多 3 次，间隔由调用方给的 waitBeforeRetry 决定（主线程必须泵消息，
        /// 绝不能干等）。其它错误码是硬失败，不重试。
        /// </summary>
        public static void SaveActiveDoc(CaxaSession session, Action<int> waitBeforeRetry = null)
        {
            int lastHr = HR_FAIL;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                bool noDoc;
                lastHr = TrySaveActiveDocOnce(session, out noDoc);
                if (noDoc)
                    throw new InvalidOperationException("CAXA 3D 当前没有活动文档。");
                if (lastHr == 0)
                {
                    if (attempt > 1)
                        Diagnostics.Log("SaveActiveDoc 第 " + attempt + " 次重试成功");
                    return;
                }
                if (lastHr != HR_FAIL || waitBeforeRetry == null || attempt == 3) break;
                Diagnostics.Log(string.Format(
                    "SaveActiveDoc hr=0x{0:X}（第 {1} 次），等宿主把替换/重建收尾跑完再重试", lastHr, attempt));
                waitBeforeRetry(1200);
            }
            throw new InvalidOperationException(string.Format("保存文档失败 hr=0x{0:X}", lastHr));
        }

        /// <summary>打一次 IZDoc::Save，返回 HRESULT；noDoc=true 表示当前没有活动文档。</summary>
        private static int TrySaveActiveDocOnce(CaxaSession session, out bool noDoc)
        {
            noDoc = false;
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pScene != IntPtr.Zero) Marshal.Release(pScene);
            if (pDoc == IntPtr.Zero)
            {
                noDoc = true;
                return HR_FAIL;
            }
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
                    Diagnostics.Trace(string.Format("SaveActiveDoc hr=0x{0:X}", hr));
                    return hr;
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
