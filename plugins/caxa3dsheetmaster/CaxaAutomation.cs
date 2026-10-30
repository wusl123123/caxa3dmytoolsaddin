using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dsheetmaster
{
    /// <summary>进程内 CAXA 会话：持有 IZBaseApp 原始接口指针。</summary>
    [ComVisible(false)]
    internal sealed class CaxaSession : IDisposable
    {
        public object AppObject;
        public IntPtr BaseApp;

        public void Dispose()
        {
            // 进程内会话归 CAXA 所有：只释放接口指针，禁止 ReleaseComObject。
            if (BaseApp != IntPtr.Zero)
            {
                try { Marshal.Release(BaseApp); } catch { }
                BaseApp = IntPtr.Zero;
            }
            AppObject = null;
        }
    }

    /// <summary>当前选中零部件的展示信息（规则 18：设计环境.ics---零部件名）。</summary>
    [ComVisible(false)]
    internal sealed class SelectedInfo
    {
        public bool HasElement;
        public string Name = "";
        public int Type;
        public string Kind = "元素";
        public bool ExternallyLinked;
    }

    /// <summary>一条折弯记录（扫描结果 / 待写目标）。</summary>
    [ComVisible(false)]
    internal sealed class BendRowInfo
    {
        public int PartId;
        public string PartName = "";
        public int BendId;
        public string BendName = "";
        public int BendType;
        /// <summary>系统名（IZElement::get_Name 槽 25），与设计树内部标识一致；读不到为空。</summary>
        public string SystemName = "";

        /// <summary>板厚（mm，已由 API 的米换算）。</summary>
        public double ThicknessMm;
        /// <summary>零件级 K 因子（无量纲）。</summary>
        public double PartKFactor;

        /// <summary>折弯角度（度，已由弧度换算）。</summary>
        public double AngleDeg;
        /// <summary>内折弯半径（mm）。</summary>
        public double RadiusMm;

        /// <summary>ezBendAllowanceSrc：0=显式展开，1=K因子，2=折弯扣除。</summary>
        public int AllowanceType = -1;
        public bool CustomKUsed;
        public double CustomK;
        /// <summary>折弯扣除（mm）。</summary>
        public double DeductionMm;
        /// <summary>零件级（从零件继承）的折弯扣除（mm）；该零件没有折弯扣除行时为 NaN。</summary>
        public double PartDeductionMm = double.NaN;

        /// <summary>零件级「最小折弯半径」（mm），来自 IZSheetMetalPart::get_MinBendRadius；未知为 NaN。</summary>
        public double PartMinRadiusMm = double.NaN;

        /// <summary>
        /// 「使用零件的最小折弯半径」状态：-1=零件最小折弯半径未知, 0=否（本折弯有自己的内半径）, 1=是（内半径等于零件最小折弯半径）。
        /// 类型库没有该勾选属性，按「本折弯内半径 vs 零件最小折弯半径」比较得出。
        /// </summary>
        public int UsePartMinRadius = -1;

        /// <summary>
        /// 「使用零件折弯扣除」状态：-1=不适用（系数类型不是折弯扣除）, 0=否（本折弯用自己的扣除值）, 1=是（等于零件继承值）。
        /// CAXA 类型库没有该勾选属性，这里按「本折弯扣除值 vs 零件继承值」比较得出。
        /// </summary>
        public int UsePartDeduction = -1;

        /// <summary>
        /// 「使用零件K系数」状态：-1=不适用（系数类型不是K因子）, 0=否（本折弯用自定义K）, 1=是（用零件K因子）。
        /// 直接对应 IZSheetMetalBend::get/set_CustomKFactorUsed（used=false 即使用零件K）。
        /// </summary>
        public int UsePartKFactor = -1;

        /// <summary>勾选状态展示文案。</summary>
        public static string FlagText(int v)
        {
            switch (v)
            {
                case 1: return "是";
                default: return "否";
            }
        }
        /// <summary>该折弯元素是否成功 QI 到 IZSheetMetalBend（不可写时置 false）。</summary>
        public bool Writable = true;
        public string ReadError = "";

        public bool IsCustomK
        {
            get { return AllowanceType == 1 && CustomKUsed; }
        }

        public string TypeText
        {
            get
            {
                if (!Writable) return "不可写";
                switch (AllowanceType)
                {
                    case 0: return "显式展开";
                    case 1: return CustomKUsed ? "自定义K因子" : "K因子";
                    case 2: return "折弯扣除";
                    default: return "未知(" + AllowanceType + ")";
                }
            }
        }

        /// <summary>界面上展示的系数数值（K 因子无量纲；折弯扣除单位 mm）。</summary>
        public string ValueText
        {
            get
            {
                if (!Writable) return "";
                switch (AllowanceType)
                {
                    case 0: return "";
                    case 1: return (CustomKUsed ? CustomK : PartKFactor).ToString("0.0000");
                    case 2: return DeductionMm.ToString("0.000");
                    default: return "";
                }
            }
        }
    }

    /// <summary>批量/单条写入的结果统计（全部以回读值判定）。</summary>
    [ComVisible(false)]
    internal sealed class ApplyResult
    {
        public int PartTotal;
        public int PartOk;
        public int BendTotal;
        public int BendOk;
        public int Failed;
        public readonly List<string> Failures = new List<string>();

        public void Fail(string message)
        {
            Failed++;
            if (Failures.Count < 12) Failures.Add(message);
        }

        public string Summary
        {
            get
            {
                if (PartTotal > 0)
                    return string.Format("零件 {0}/{1} 成功，折弯 {2}/{3} 成功，失败 {4} 项",
                        PartOk, PartTotal, BendOk, BendTotal, Failed);
                return string.Format("折弯 {0}/{1} 成功，失败 {2} 项", BendOk, BendTotal, Failed);
            }
        }
    }

    /// <summary>用户点「停止」时抛出，用于中断流程并冒泡回 UI。</summary>
    [ComVisible(false)]
    internal sealed class OperationStoppedException : Exception
    {
        public OperationStoppedException() : base("已停止") { }
        public OperationStoppedException(string message) : base(message) { }
    }

    /// <summary>
    /// CAXA 3D COM 自动化封装（进程内 vtable 直调版）。
    /// 槽位口径：槽 = 成员声明序号 + 7（IUnknown 3 + IDispatch 4）。
    /// 全部槽位以本机 IZSheetMetalPart / IZSheetMetalBend 成员表核对。
    /// 长度单位：CAXA 3D API 用「米」（实测 Thickness=0.002 即 2mm）；角度用弧度。
    /// </summary>
    [ComVisible(false)]
    internal static partial class CaxaAutomation
    {
        /// <summary>隐藏 NumericUpDown 的上下调整按钮，让用户直接用键盘输入。</summary>
        internal static void RemoveUpDownArrows(System.Windows.Forms.NumericUpDown box)
        {
            try
            {
                Type t = typeof(System.Windows.Forms.NumericUpDown);
                System.Reflection.FieldInfo fi = t.GetField("upDownButtons",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (fi != null)
                {
                    System.Windows.Forms.Control updown =
                        fi.GetValue(box) as System.Windows.Forms.Control;
                    if (updown != null) { updown.Visible = false; updown.Width = 0; }
                }
            }
            catch { }
        }

        // ---- 接口 IID ----
        private static readonly Guid IID_IZBaseApp = new Guid("A5B9AF7D-228B-4ED1-BA42-D51318F7EA20");
        private static readonly Guid IID_IZSceneDoc = new Guid("3845C6DA-89D6-43F7-8B10-4BE36E0E758B");
        private static readonly Guid IID_IZDoc = new Guid("408F2326-F171-443B-A9EF-4AB250A2B1A8");
        private static readonly Guid IID_IZElement = new Guid("69EF5583-A26D-4913-A2F6-DBFF73E1B8A3");
        private static readonly Guid IID_IZPart = new Guid("F20F2411-05C4-4CA6-B2B4-5DB29531EDD7");
        private static readonly Guid IID_IZAssembly = new Guid("FF7C2F47-F21F-4225-9027-58E66CC01860");
        private static readonly Guid IID_IZSheetMetalPart = new Guid("05667879-65CD-4417-AFB8-16E09A669D06");
        private static readonly Guid IID_IZSheetMetalBend = new Guid("F1100C88-8FBE-4ED2-AD8D-343D9D23B2C9");
        private static readonly Guid IID_IZConfigurationMgr = new Guid("4FDA96C8-058E-4977-8A1F-FA2CCDBFF315");
        private static readonly Guid IID_IZConfiguration = new Guid("7AFFA1E0-F43D-47DA-9AA2-9FC9C7F587D7");

        // ---- vtable 槽位 ----
        private const int SlotActiveDoc = 11;                 // IZBaseApp
        private const int SlotDocGetName = 11;                // IZDoc
        private const int SlotDocSave = 9;                    // IZDoc
        private const int SlotSceneSelectionMgr = 27;         // IZSceneDoc
        private const int SlotSelMgrGetSelectedElementsZArray = 19; // IZSelectionMgr
        private const int SlotArrayCount = 7;                 // IZArray
        private const int SlotArrayGet = 9;                   // IZArray
        private const int SlotSceneGetTopElement = 58;        // IZSceneDoc
        private const int SlotSceneRegenerateParts = 21;      // IZSceneDoc::RegenerateParts(VARIANT_BOOL)
        private const int SlotSceneUpdate = 36;               // IZSceneDoc::Update()
        private const int SlotSceneUpdateGraphics = 83;       // IZSceneDoc::UpdateGraphics(UInt32)
        private const int SlotSceneUpdateBrowser = 84;        // IZSceneDoc::UpdateBrowser(UInt32)

        // 多配置与选中（写完后从「展开配置」恢复默认配置、把焦点选回折叠件）
        private const int SlotSelMgrRemoveAll = 22;           // IZSelectionMgr::RemoveAllFromSelection()
        private const int SlotSelMgrAddElement = 20;          // IZSelectionMgr::AddElementToSelection(IZElement*, VARIANT_BOOL)
        private const int SlotSceneGetConfigurationMgr = 40;  // IZSceneDoc::get_ConfigurationMgr -> IZConfigurationMgr*
        private const int SlotCfgMgrGetDefault = 12;          // IZConfigurationMgr::get_DefaultConfiguration
        private const int SlotCfgGetName = 7;                 // IZConfiguration::get_Name
        private const int SlotCfgActivate = 15;               // IZConfiguration::Activate(VARIANT_BOOL)

        private const int SlotElemGetType = 7;                // IZElement
        private const int SlotElemGetId = 8;                  // IZElement
        private const int SlotElemGetName = 9;                // IZElement
        private const int SlotElemSetName = 10;               // IZElement（put_Name）
        private const int SlotElemGetParent = 12;             // IZElement::GetParent
        private const int SlotElemGetSystemName = 25;          // IZElement::get_SystemName（系统名，非 get_Name=9）
        private const int SlotElemGetShowInBrowser = 30;       // IZElement::get_ShowInBrowser
        private const int SlotElemGetFirstChild = 19;        // IZElement
        private const int SlotElemGetNextChild = 20;         // IZElement
        private const int SlotElemGetChildrenCount = 15;     // IZElement

        private const int SlotPartGetExtLinkedInfo = 17;      // IZPart::GetExternallyLinkedInfo
        private const int SlotAsmGetExtLinkedInfo = 21;       // IZAssembly::GetExternallyLinkedInfo

        // IZSheetMetalPart（成员声明序号 + 7）
        private const int SlotSmGetThickness = 10;
        private const int SlotSmGetKFactor = 17;
        private const int SlotSmSetKFactor = 18;

        // IZPart：Update 用于提交特征上的 set_*（未提交时回读仍是旧值）
        private const int SlotPartUpdate = 7;

        // IZSheetMetalBend（成员声明序号 + 7）
        private const int SlotBendGetAllowanceType = 15;
        private const int SlotBendSetAllowanceType = 16;
        private const int SlotBendGetCustomKUsed = 17;
        private const int SlotBendSetCustomKUsed = 18;
        private const int SlotBendGetCustomKValue = 19;
        private const int SlotBendSetCustomKValue = 20;
        private const int SlotBendGetDeductionValue = 23;
        private const int SlotBendSetDeductionValue = 24;
        private const int SlotBendGetBendAngle = 25;
        private const int SlotBendGetInsideRadius = 47;
        private const int SlotBendSetInsideRadius = 48;

        // 注：折弯线相关 API（GetBendLineIDs / GetBendLineInfoByID / GetBendLineSource）
        // 已按用户要求整体剔除，严禁用于内部折弯判断（见项目总结第 44 条）。

        // 探针专用只读属性（不参与业务流程）
        private const int SlotSmGetThickness2 = 15;
        private const int SlotSmGetMinBendRadius = 19;        // 零件最小折弯半径（米）
        private const int SlotSmGetUseDin6935 = 21;
        private const int SlotSmGetThicknessRefSide = 23;
        private const int SlotSmGetCustomUnfoldLength = 27;
        private const int SlotBendGetFirstBendLength = 7;
        private const int SlotBendGetSecondBendLength = 9;
        private const int SlotBendGetCustomUnfoldLength = 21;
        private const int SlotBendGetUseCustomUnfoldLengthOfPart = 36;
        private const int SlotBendGetUpdateBehavior = 38;

        // 展开件与归属（实测：GetBendLineIDs 只对「展开件」有效，折叠件返回 E_FAIL）
        private const int SlotSmUnfold = 7;
        private const int SlotSmGetIsFolded = 9;
        private const int SlotSmGetUnfoldConfiguration = 11;
        private const int SlotSmGetFlatPart = 12;
        private const int SlotSmGetSiblingPart = 13;
        private const int SlotSmGetStockProperties = 14;
        private const int SlotElemGetOwnerPart = 29;
        private const int SlotElemGetChildrenZArray = 21;
        private const int SlotElemGetCustomPropMgr = 24;
        private const int SlotPunchBendGetBendLineCurveIds = 8;

        private static readonly Guid IID_IZSheetMetalPunchBend = new Guid("C49A959D-923E-4D79-8CCA-615D52A7E4B2");
        private static readonly Guid IID_IZSheetMetalSketchBend = new Guid("C72DDC6E-15C0-4B79-AA60-3D5B2F2F87C8");
        private static readonly Guid IID_IZSheetMetalStock = new Guid("814194BB-D299-400B-9E3C-F6FF77688523");
        private static readonly Guid IID_IZPartFeatureMgr = new Guid("26D70B9B-291B-4727-9DA1-E2D7EEC1FA27");
        private static readonly Guid IID_IZFeature = new Guid("8A085025-34FB-45DA-877B-44FD550F0E94");

        // 特征管理器（设计树里「斜接法兰」「折弯」等特征的真身，槽位 = 声明序号 + 7）
        private const int SlotFmGetFeaturesCount = 7;
        private const int SlotFmGetFeatureAt = 9;
        private const int SlotFeatGetFeatureIndex = 8;
        private static readonly Guid IID_IZCustomPropMgr = new Guid("2ABEB1AE-964D-4259-AF70-2BC3E8E0AAB0");

        /// <summary>场景级折弯扫描的最大深度（装配体里折弯特征可能与零件同级）。</summary>
        private const int MaxSceneBendDepth = 12;

        private const int ElemTypeSheetMetalPart = 20;
        private const int ElemTypeBend = 22;
        private const int ElemTypePunchBend = 49;
        private const int ElemTypeSketchBend = 50;

        private const int BendTypeExplicit = 0;
        private const int BendTypeKFactor = 1;
        private const int BendTypeDeduction = 2;

        private const int DispidZIronCADApp = 0x2A;
        private const int HR_NOINTERFACE = unchecked((int)0x80004002);
        private const int HR_FAIL = unchecked((int)0x80004005);

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
        private delegate int FnGetShort(IntPtr self, out short value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetDouble(IntPtr self, out double value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnArrayGet(IntPtr self, int index, out IntPtr ppElem);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnNoArg(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnShortArg(IntPtr self, short value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnObjShortArg(IntPtr self, IntPtr pObj, short value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnUIntArg(IntPtr self, uint value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetDouble(IntPtr self, double value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetInt(IntPtr self, int value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetShort(IntPtr self, short value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetBstr(IntPtr self, IntPtr bstr);

        /// <summary>GetExternallyLinkedInfo(VARIANT_BOOL* linked, BSTR* retval)。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetLinkedInfo(IntPtr self, out short linked, out IntPtr pbstrName);

        /// <summary>返回 VARIANT 的无参方法（如 GetBendLineIDs）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetVariant(IntPtr self, IntPtr pVarOut);

        /// <summary>带一个 VARIANT_BOOL 入参、返回对象的属性（如 get_CustomPropMgr）。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetObjBool(IntPtr self, short flag, out IntPtr ppv);

        [DllImport("oleaut32.dll")]
        private static extern int VariantClear(IntPtr pvarg);

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

        /// <summary>调用前探测：vtable 槽位上的函数指针必须落在已提交的可执行内存内。</summary>
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

        private static int TryCallGetObj(IntPtr pIface, int slot, out IntPtr ppv)
        {
            FnGetObj fn = (FnGetObj)Marshal.GetDelegateForFunctionPointer(Slot(pIface, slot), typeof(FnGetObj));
            return fn(pIface, out ppv);
        }

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

        /// <summary>读取 BSTR 属性（get_Name 形态），失败返回 null。</summary>
        private static string TryReadBstrProp(IntPtr pIface, int slot, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效");
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

        private static bool TryReadIntProp(IntPtr pIface, int slot, string what, out int value)
        {
            value = 0;
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效");
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

        private static bool TryReadShortProp(IntPtr pIface, int slot, string what, out short value)
        {
            value = 0;
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效");
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

        private static bool TryReadDoubleProp(IntPtr pIface, int slot, string what, out double value)
        {
            value = 0;
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效");
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

        private static int TrySetDouble(IntPtr pIface, int slot, double value, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效");
                    return HR_FAIL;
                }
                FnSetDouble f = (FnSetDouble)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnSetDouble));
                int hr = f(pIface, value);
                if (hr == 0) Diagnostics.Trace(string.Format("{0} = {1} hr=0", what, value));
                else Diagnostics.Log(string.Format("{0} = {1} 失败 hr=0x{2:X}", what, value, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        private static int TrySetInt(IntPtr pIface, int slot, int value, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效");
                    return HR_FAIL;
                }
                FnSetInt f = (FnSetInt)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnSetInt));
                int hr = f(pIface, value);
                if (hr == 0) Diagnostics.Trace(string.Format("{0} = {1} hr=0", what, value));
                else Diagnostics.Log(string.Format("{0} = {1} 失败 hr=0x{2:X}", what, value, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        private static int TrySetShort(IntPtr pIface, int slot, short value, string what)
        {
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效");
                    return HR_FAIL;
                }
                FnSetShort f = (FnSetShort)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnSetShort));
                int hr = f(pIface, value);
                if (hr == 0) Diagnostics.Trace(string.Format("{0} = {1} hr=0", what, value));
                else Diagnostics.Log(string.Format("{0} = {1} 失败 hr=0x{2:X}", what, value, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
        }

        /// <summary>写入 BSTR 属性（put_Name 形态），hr 由调用方判定。</summary>
        private static int TrySetBstrProp(IntPtr pIface, int slot, string value, string what)
        {
            IntPtr bstr = IntPtr.Zero;
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    Diagnostics.Log(what + " 跳过：slot " + slot + " 函数指针无效");
                    return HR_FAIL;
                }
                bstr = Marshal.StringToBSTR(value ?? "");
                FnSetBstr f = (FnSetBstr)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnSetBstr));
                int hr = f(pIface, bstr);
                if (hr == 0) Diagnostics.Trace(string.Format("{0} = \"{1}\" hr=0", what, value));
                else Diagnostics.Log(string.Format("{0} = \"{1}\" 失败 hr=0x{2:X}", what, value, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
            finally
            {
                if (bstr != IntPtr.Zero) Marshal.FreeBSTR(bstr);
            }
        }

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
                Diagnostics.Log(string.Format("{0} hr=0x{1:X}", what, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(what + " EX: " + ex.Message);
                return HR_FAIL;
            }
        }

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
        /// 读取返回 VARIANT 的无参方法（GetBendLineIDs 等）。失败返回 null，detail 记录 hr/vt 供日志与探针。
        /// </summary>
        private static object CallGetVariantProp(IntPtr pIface, int slot, string what, out string detail)
        {
            detail = "";
            try
            {
                IntPtr fn = Slot(pIface, slot);
                if (!LooksCallable(fn))
                {
                    detail = "slot " + slot + " 函数指针无效（短 vtable 代理）";
                    Diagnostics.Log(what + " 跳过：" + detail);
                    return null;
                }
                FnGetVariant f = (FnGetVariant)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetVariant));
                IntPtr pVar = Marshal.AllocCoTaskMem(24);
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pVar, i, 0);
                try
                {
                    int hr = f(pIface, pVar);
                    ushort vt = (ushort)Marshal.ReadInt16(pVar, 0);
                    detail = string.Format("hr=0x{0:X} vt=0x{1:X4}", hr, vt);
                    if (hr != 0 || vt == 0 || vt == 1) // VT_EMPTY / VT_NULL
                    {
                        Diagnostics.Log(what + " " + detail);
                        return null;
                    }
                    object o = Marshal.GetObjectForNativeVariant(pVar);
                    if (o != null) detail += " ret=" + o.GetType().FullName;
                    Diagnostics.Log(what + " " + detail);
                    return o;
                }
                finally
                {
                    VariantClear(pVar);
                    Marshal.FreeCoTaskMem(pVar);
                }
            }
            catch (Exception ex)
            {
                detail = "EX: " + ex.Message;
                Diagnostics.Log(what + " EX: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 从「板料属性」(IZSheetMetalPart::GetStockProperties) 读板厚(mm)与 K 因子；
        /// 实测板料表数值单位为 mm（27.钢 gauge=1 → 3.0 对应 3mm），若与零件厚度差两个数量级则按 m 处理。
        /// </summary>
        private static void ReadStockThicknessK(IntPtr pSm, string partName,
            out double thicknessMm, out double kFactor, out string stockName)
        {
            thicknessMm = 0; kFactor = 0; stockName = "";

            double partMm = 0;
            double th;
            if (TryReadDoubleProp(pSm, SlotSmGetThickness, "SM.get_Thickness(" + partName + ")", out th))
                partMm = th * 1000.0;

            double stockTh = 0, stockK = 0;
            try
            {
                var sm = Marshal.GetObjectForIUnknown(pSm) as IZSheetMetalPart;
                if (sm != null)
                {
                    string name = "", code = "";
                    double weight = 0, minR = 0;
                    int gauge = 0;
                    sm.GetStockProperties(out name, out code, out weight, out gauge,
                        out stockTh, out stockK, out minR);
                    stockName = name ?? "";
                    Diagnostics.Log(string.Format(
                        "板料[{0}] 牌号={1} gauge={2} 厚={3:0.####} K={4:0.####} minR={5:0.####}",
                        partName, stockName, gauge, stockTh, stockK, minR));
                }
            }
            catch (Exception ex) { Diagnostics.Log("GetStockProperties EX: " + ex.Message); }

            if (stockTh > 0)
            {
                if (partMm <= 0) thicknessMm = stockTh;
                else if (Math.Abs(stockTh - partMm) <= 0.5) thicknessMm = stockTh;
                else if (Math.Abs(stockTh / 1000.0 - partMm) <= 0.5) thicknessMm = stockTh / 1000.0;
                else thicknessMm = partMm;
            }
            else thicknessMm = partMm;

            if (stockK > 0 && stockK <= 1) kFactor = stockK;
            else if (TryReadDoubleProp(pSm, SlotSmGetKFactor, "SM.get_KFactor(" + partName + ")", out th))
                kFactor = th;

            Diagnostics.Log(string.Format("板厚={0:0.###}mm K={1:0.####}（板料={2}）",
                thicknessMm, kFactor, stockName));
        }

        /// <summary>
        /// 通过 IZPartFeatureMgr 枚举零件特征（就是设计树里「斜接法兰」「折弯」这些节点的真身），
        /// 只保留实现 IZSheetMetalBend 的钣金折弯特征。这是唯一能稳定拿到折弯特征名的通道。
        /// </summary>
        private static int ForEachBendFeatureByFeatureMgr(IntPtr pElem, string partName,
            Action<uint, IntPtr, int, int, string> visit, Action<string> log)
        {
            if (log == null) log = s => { };
            IntPtr pFm;
            if (TryQI(pElem, IID_IZPartFeatureMgr, out pFm) != 0 || pFm == IntPtr.Zero)
            {
                log("  · 零件「" + partName + "」不支持 IZPartFeatureMgr");
                return 0;
            }
            try
            {
                int count;
                if (!TryReadIntProp(pFm, SlotFmGetFeaturesCount,
                        "FM.GetFeaturesCount(" + partName + ")", out count))
                {
                    log("  · 零件「" + partName + "」读特征数失败");
                    return 0;
                }
                log("  · 特征管理器[" + partName + "] 特征数=" + count);
                if (count <= 0) return 0;
                if (count > 2000) count = 2000;

                IntPtr fnAt = Slot(pFm, SlotFmGetFeatureAt);
                if (!LooksCallable(fnAt))
                {
                    log("  · IZPartFeatureMgr.GetFeatureAt 槽位不可调用");
                    return 0;
                }
                FnArrayGet getAt = (FnArrayGet)Marshal.GetDelegateForFunctionPointer(fnAt, typeof(FnArrayGet));

                // 第一遍：收集所有折弯特征，并记录「在设计树里是否可见」
                var bendOrdinals = new List<int>();
                var bendVisible = new List<bool>();
                var bendNames = new List<string>();
                int hidden = 0;
                for (int i = 0; i < count; i++)
                {
                    IntPtr pFeat;
                    int hr = getAt(pFm, i, out pFeat);
                    if (hr != 0 || pFeat == IntPtr.Zero) continue;
                    try
                    {
                        IntPtr pBend;
                        if (TryQI(pFeat, IID_IZSheetMetalBend, out pBend) != 0 || pBend == IntPtr.Zero)
                            continue;
                        Marshal.Release(pBend);

                        string fname = ReadFeatureName(pFeat, partName);
                        bool vis = ReadShowInBrowser(pFeat);
                        if (!vis) hidden++;
                        bendOrdinals.Add(i + 1);
                        bendVisible.Add(vis);
                        bendNames.Add(string.IsNullOrEmpty(fname) ? "折弯" : fname);
                    }
                    finally { Marshal.Release(pFeat); }
                }

                // 严格只加载设计树可见的折弯（不做兜底：内部折弯一律不入表）
                // 坑点：外链零件首次进入时设计树浏览器尚未构建，get_ShowInBrowser 会一律返回 true，
                // 导致内部隐藏折弯被误入表。唯一解是抓取前先对该零件做一次 IZPart.Update 重建
                //（见 ScanCtx.OnExit）；重建后若仍全为 true，说明本零件确实没有内部折弯。
                if (hidden == 0 && bendOrdinals.Count > 0 && _sceneForBrowser != IntPtr.Zero)
                {
                    int hrUpd = TryInvokeUIntArg(_sceneForBrowser, SlotSceneUpdateBrowser, 0,
                        "IZSceneDoc.UpdateBrowser(可见性重试)");
                    if (hrUpd == 0)
                    {
                        System.Threading.Thread.Sleep(25);
                        int hidden2 = 0;
                        for (int k = 0; k < bendOrdinals.Count; k++)
                        {
                            IntPtr pFeat;
                            if (getAt(pFm, bendOrdinals[k] - 1, out pFeat) != 0 || pFeat == IntPtr.Zero)
                                continue;
                            try
                            {
                                if (!ReadShowInBrowser(pFeat)) { bendVisible[k] = false; hidden2++; }
                            }
                            finally { Marshal.Release(pFeat); }
                        }
                        if (hidden2 > 0)
                        {
                            hidden = hidden2;
                            log("  · 折弯可见性首次全为「是」，刷新设计树浏览器后重读得到不可见 " +
                                hidden2 + " 个（已采用重读结果）");
                        }
                        else
                        {
                            log("  · 折弯可见性重读仍全为「是」（重建后仍如此，按无内部折弯处理）");
                        }
                    }
                }

                int visible = bendOrdinals.Count - hidden;
                log(string.Format("  · 特征管理器[{0}] 折弯 {1} 个，其中设计树不可见 {2} 个（内部折弯，不入表）；入表 {3} 个",
                    partName, bendOrdinals.Count, hidden, visible));

                int found = 0;
                var selected = new HashSet<int>();
                for (int k = 0; k < bendOrdinals.Count; k++)
                    if (bendVisible[k]) selected.Add(bendOrdinals[k]);

                for (int i = 0; i < count; i++)
                {
                    if (!selected.Contains(i + 1)) continue;
                    IntPtr pFeat;
                    int hr = getAt(pFm, i, out pFeat);
                    if (hr != 0 || pFeat == IntPtr.Zero) continue;
                    try
                    {
                        IntPtr pBend;
                        if (TryQI(pFeat, IID_IZSheetMetalBend, out pBend) != 0 || pBend == IntPtr.Zero)
                            continue;
                        try
                        {
                            int k = bendOrdinals.IndexOf(i + 1);
                            found++;
                            visit(0, pBend, i + 1, ElemTypeBend,
                                k >= 0 ? bendNames[k] : "折弯");
                        }
                        finally { Marshal.Release(pBend); }
                    }
                    finally { Marshal.Release(pFeat); }
                }
                log("  · 特征管理器[" + partName + "] 折弯特征=" + found + " 个");
                return found;
            }
            catch (Exception ex)
            {
                log("  · 特征管理器[" + partName + "] 枚举异常：" + ex.Message);
                return 0;
            }
            finally { Marshal.Release(pFm); }
        }


        /// <summary>读对象的父节点描述（IZElement::GetParent 槽 12），用于诊断折弯归属。</summary>
        private static string ReadParentText(IntPtr pObj)
        {
            IntPtr pEl;
            if (TryQI(pObj, IID_IZElement, out pEl) != 0 || pEl == IntPtr.Zero) return "";
            try
            {
                IntPtr pPar;
                if (TryCallGetObj(pEl, SlotElemGetParent, out pPar) != 0 || pPar == IntPtr.Zero) return "(无父节点)";
                try
                {
                    int t = -1, id = 0;
                    string n;
                    ReadElementIdentity(pPar, out id, out n);
                    TryReadIntProp(pPar, SlotElemGetType, null, out t);
                    return string.Format("type={0}({1}) id={2} name={3}", t, MapElementKind(t), id, n);
                }
                finally { Marshal.Release(pPar); }
            }
            catch { return ""; }
            finally { Marshal.Release(pEl); }
        }

        /// <summary>读特征名（QI IZElement 后 get_Name），失败返回空串。</summary>
        private static string ReadFeatureName(IntPtr pFeat, string what)
        {
            IntPtr pEl;
            if (TryQI(pFeat, IID_IZElement, out pEl) != 0 || pEl == IntPtr.Zero) return "";
            try { return TryReadBstrProp(pEl, SlotElemGetName, "Feat.get_Name(" + what + ")") ?? ""; }
            catch { return ""; }
            finally { Marshal.Release(pEl); }
        }

        /// <summary>
        /// 读「是否在设计树里显示」（IZElement::get_ShowInBrowser 槽 30）。
        /// 读失败按 true 处理，宁可多显示也不丢折弯。
        /// </summary>
        private static bool ReadShowInBrowser(IntPtr pFeat)
        {
            IntPtr pEl;
            if (TryQI(pFeat, IID_IZElement, out pEl) != 0 || pEl == IntPtr.Zero) return true;
            try
            {
                short v;
                if (!TryReadShortProp(pEl, SlotElemGetShowInBrowser, null, out v)) return true;
                return v != 0;
            }
            catch { return true; }
            finally { Marshal.Release(pEl); }
        }

        /// <summary>
        /// 「使用零件折弯扣除」列的判定（CAXA 类型库没有该勾选属性，按数据算）：
        /// · 系数类型不是「折弯扣除」→ 不适用（显示 —）；
        /// · 本折弯扣除值 == 零件继承值 → 是（统一使用从零件继承的扣除值）；
        /// · 本折弯扣除值 != 零件继承值 → 否（该折弯用自己的扣除值，才能被单独修改）。
        /// 零件继承值取该零件第一条「折弯扣除」行的扣除值。
        /// </summary>
        private static void ComputeUsePartDeduction(List<BendRowInfo> rows, int added)
        {
            if (rows == null || added <= 0) return;
            int start = rows.Count - added;

            double partDeduction = double.NaN;
            for (int i = start; i < rows.Count; i++)
            {
                if (rows[i].AllowanceType != BendTypeDeduction) continue;
                partDeduction = rows[i].DeductionMm;
                break;
            }

            bool anyDeduction = false;
            for (int i = start; i < rows.Count; i++)
            {
                BendRowInfo r = rows[i];
                r.PartDeductionMm = partDeduction;
                if (r.AllowanceType != BendTypeDeduction)
                {
                    r.UsePartDeduction = -1;      // 不适用
                    continue;
                }
                anyDeduction = true;
                r.UsePartDeduction = Math.Abs(r.DeductionMm - partDeduction) < 1e-6 ? 1 : 0;
            }
            if (anyDeduction && !double.IsNaN(partDeduction))
                Diagnostics.Log(string.Format(
                    "零件继承折弯扣除 = {0:0.###} mm（「使用零件折弯扣除」按「本值是否等于该值」判定）", partDeduction));
        }

        private static int CollectBendLines(IntPtr pSm, PartCtx part, List<BendRowInfo> rows,
            Action<string> log)
        {
            if (log == null) log = s => { };

            int added = 0;
            int total = ForEachBendFeatureByFeatureMgr(pSm, part.Name,
                (lineId, pBend, elemId, elemType, elemName) =>
                {
                    string displayName = string.IsNullOrEmpty(elemName)
                        ? "折弯特征" + elemId
                        : elemName;

                    var row = new BendRowInfo
                    {
                        PartId = part.Id,
                        PartName = part.Name,
                        BendId = elemId,
                        BendName = displayName,
                        SystemName = ReadSystemName(pBend),
                        BendType = elemType,
                        ThicknessMm = part.ThicknessMm,
                        PartKFactor = part.KFactor,
                        PartMinRadiusMm = part.MinBendRadiusMm
                    };
                    FillBendProps(pBend, row, row.BendName);
                    if (row.AllowanceType < 0)
                    {
                        log("  · 折弯特征「" + row.BendName + "」读不到系数类型，跳过");
                        return;
                    }
                    rows.Add(row);
                    added++;
                }, log);

            ComputeUsePartDeduction(rows, added);

            if (total == 0) log("钣金件[" + part.Name + "] 未找到钣金折弯特征（type=22）");
            else log(string.Format("钣金件[{0}] 折弯特征 {1} 个，入表 {2} 条", part.Name, total, added));
            return added;
        }

        /// <summary>读取元素所属零件（IZElement::GetOwnerPart，槽 29）。</summary>
        private static void ReadOwnerPart(IntPtr pElem, out int id, out string name)
        {
            id = 0; name = "";
            try
            {
                int hr = TryCallGetObj(pElem, SlotElemGetOwnerPart, out IntPtr pOwner);
                if (hr != 0 || pOwner == IntPtr.Zero) return;
                try { ReadElementIdentity(pOwner, out id, out name); }
                finally { Marshal.Release(pOwner); }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("GetOwnerPart EX: " + ex.Message);
            }
        }

        /// <summary>通过旧自动化接口的默认 IDispatch 读取 ZIronCADApp 属性（memid 0x2A）。</summary>
        private static object GetZAppObject(object app)
        {
            IRawDispatch d = (IRawDispatch)app;
            Guid empty = Guid.Empty;
            IntPtr pDp = Marshal.AllocCoTaskMem(48);
            for (int i = 0; i < 48; i++) Marshal.WriteByte(pDp, i, 0);
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

        #region 会话与环境

        /// <summary>从 Addin 站点提供的 IZBaseApp 构建进程内会话。失败时抛出异常。</summary>
        public static CaxaSession ConnectFromSite(IZBaseApp siteApp)
        {
            if (siteApp == null)
                throw new InvalidOperationException("Addin 站点未提供 Application 对象。");

            object appObj = (object)siteApp;
            IntPtr pBase = IntPtr.Zero;

            IntPtr pUnk = Marshal.GetIUnknownForObject(appObj);
            int hr = TryQI(pUnk, IID_IZBaseApp, out pBase);
            Marshal.Release(pUnk);

            if (hr == HR_NOINTERFACE)
            {
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

            IntPtr pDoc;
            hr = TryCallGetObj(pBase, SlotActiveDoc, out pDoc);
            if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            if (hr != 0)
            {
                Marshal.Release(pBase);
                throw new InvalidOperationException(
                    string.Format("IZBaseApp.ActiveDoc 验证调用失败 hr=0x{0:X}", hr));
            }

            Diagnostics.Log("session accepted (in-process)");
            return new CaxaSession { AppObject = appObj, BaseApp = pBase };
        }

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

        private static IntPtr QIRequired(IntPtr pUnk, Guid iid, string name)
        {
            IntPtr ppv;
            int hr = Marshal.QueryInterface(pUnk, ref iid, out ppv);
            if (hr != 0)
                throw new InvalidOperationException(
                    string.Format("QueryInterface({0}) 失败 hr=0x{1:X}", name, hr));
            return ppv;
        }

        /// <summary>定位活动设计场景：ActiveDoc → IZSceneDoc。调用方负责 Release 非零指针。</summary>
        private static void OpenActiveScene(CaxaSession session,
            out IntPtr pDoc, out IntPtr pScene)
        {
            pDoc = IntPtr.Zero; pScene = IntPtr.Zero;
            if (session == null || session.BaseApp == IntPtr.Zero)
                throw new InvalidOperationException("未连接 CAXA 3D。");
            int hr = TryCallGetObj(session.BaseApp, SlotActiveDoc, out pDoc);
            Diagnostics.Trace(string.Format("ActiveDoc hr=0x{0:X} ptr=0x{1:X}", hr, pDoc.ToInt64()));
            if (hr != 0)
                throw new InvalidOperationException(string.Format("读取活动文档失败 hr=0x{0:X}", hr));
            if (pDoc == IntPtr.Zero) return;
            pScene = QIRequired(pDoc, IID_IZSceneDoc, "IZSceneDoc");
        }

        private static void RefreshSceneUi(IntPtr pScene)
        {
            try
            {
                IntPtr fn = Slot(pScene, SlotSceneUpdate);
                if (LooksCallable(fn))
                {
                    FnNoArg f = (FnNoArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnNoArg));
                    f(pScene);
                }
                TryInvokeUIntArg(pScene, SlotSceneUpdateGraphics, 0, "IZSceneDoc.UpdateGraphics");
                TryInvokeUIntArg(pScene, SlotSceneUpdateBrowser, 0, "IZSceneDoc.UpdateBrowser");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("RefreshSceneUi EX: " + ex.Message);
            }
        }

        /// <summary>刷新设计场景界面（Update + UpdateGraphics + UpdateBrowser）。</summary>
        public static void RefreshScene(CaxaSession session)
        {
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            try
            {
                if (pScene == IntPtr.Zero) return;
                RefreshSceneUi(pScene);
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        /// <summary>重新生成零部件（IZSceneDoc::RegenerateParts(VARIANT_TRUE)）。大装配较慢。</summary>
        public static void RegenerateParts(CaxaSession session)
        {
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            try
            {
                if (pScene == IntPtr.Zero)
                    throw new InvalidOperationException("当前没有活动设计场景。");
                int hr = TryInvokeShortArg(pScene, SlotSceneRegenerateParts, -1, "IZSceneDoc.RegenerateParts");
                if (hr != 0)
                    throw new InvalidOperationException(string.Format("重新生成失败 hr=0x{0:X}", hr));
                RefreshSceneUi(pScene);
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        /// <summary>保存活动文档（IZDoc::Save）。hr==0 即视为落盘成功。</summary>
        public static void SaveActiveDoc(CaxaSession session)
        {
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pScene != IntPtr.Zero) { Marshal.Release(pScene); pScene = IntPtr.Zero; }
            if (pDoc == IntPtr.Zero)
                throw new InvalidOperationException("当前没有活动文档。");
            IntPtr pZDoc = IntPtr.Zero;
            try
            {
                pZDoc = QIRequired(pDoc, IID_IZDoc, "IZDoc");
                int hr = TryCallNoArg(pZDoc, SlotDocSave, "IZDoc.Save");
                if (hr != 0)
                    throw new InvalidOperationException(string.Format("保存失败 hr=0x{0:X}（未落盘）", hr));
            }
            finally
            {
                if (pZDoc != IntPtr.Zero) Marshal.Release(pZDoc);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        #endregion

        #region 元素读取

        private static string MapElementKind(int t)
        {
            switch (t)
            {
                case 1: return "零件";
                case 2: return "装配体";
                case 20: return "钣金零件";
                case 21: return "板料";
                case 22: return "折弯";
                case 25: return "刻面零件";
                case 28: return "线框零件";
                case 29: return "轮廓零件";
                case 49: return "冲压折弯";
                case 50: return "草图折弯";
                default: return "元素(type=" + t + ")";
            }
        }

        private static void ReadElementIdentity(IntPtr pElem, out int id, out string name)
        {
            id = 0;
            TryReadIntProp(pElem, SlotElemGetId, "IZElement.get_Id", out id);
            name = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name") ?? "";
        }

        /// <summary>获取用户当前选中的第一个零部件类元素（IZElement 指针，调用方负责 Release）。</summary>
        private static IntPtr GetSelectedElement(IntPtr pScene, string tag)
        {
            if (pScene == IntPtr.Zero) return IntPtr.Zero;
            IntPtr fn = Slot(pScene, SlotSceneSelectionMgr);
            if (!LooksCallable(fn)) return IntPtr.Zero;
            FnGetObj f = (FnGetObj)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetObj));
            IntPtr pSelMgr;
            int hr = f(pScene, out pSelMgr);
            if (hr != 0 || pSelMgr == IntPtr.Zero) return IntPtr.Zero;

            try
            {
                fn = Slot(pSelMgr, SlotSelMgrGetSelectedElementsZArray);
                if (!LooksCallable(fn)) return IntPtr.Zero;
                FnGetObj za = (FnGetObj)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetObj));
                IntPtr pArr;
                hr = za(pSelMgr, out pArr);
                if (hr != 0 || pArr == IntPtr.Zero) return IntPtr.Zero;
                try { return PickPartFromZArray(pArr, tag); }
                finally { Marshal.Release(pArr); }
            }
            finally
            {
                Marshal.Release(pSelMgr);
            }
        }

        private static IntPtr PickPartFromZArray(IntPtr pArr, string tag)
        {
            IntPtr fn = Slot(pArr, SlotArrayCount);
            if (!LooksCallable(fn)) return IntPtr.Zero;
            FnGetInt cnt = (FnGetInt)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetInt));
            int count;
            int hr = cnt(pArr, out count);
            if (hr != 0 || count <= 0) return IntPtr.Zero;
            if (count > 1000) count = 1000;

            IntPtr fnGet = Slot(pArr, SlotArrayGet);
            if (!LooksCallable(fnGet)) return IntPtr.Zero;
            FnArrayGet get = (FnArrayGet)Marshal.GetDelegateForFunctionPointer(fnGet, typeof(FnArrayGet));
            for (int i = 0; i < count; i++)
            {
                IntPtr pRaw;
                hr = get(pArr, i, out pRaw);
                if (hr != 0 || pRaw == IntPtr.Zero) continue;
                IntPtr pElem;
                bool keep = false;
                try
                {
                    if (TryQI(pRaw, IID_IZElement, out pElem) != 0 || pElem == IntPtr.Zero) continue;
                    int etype;
                    keep = TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(sel)", out etype)
                        && IsPartKind(etype);
                    if (!keep) Marshal.Release(pElem);
                }
                finally
                {
                    Marshal.Release(pRaw);
                }
                if (keep) return pElem;
            }
            return IntPtr.Zero;
        }

        private static bool IsPartKind(int t)
        {
            return t == 1 || t == 2 || t == 20 || t == 25 || t == 28 || t == 29;
        }

        private static void TryReadExternalLink(IntPtr pElem, int elemType, SelectedInfo info)
        {
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
                string path = "";
                if (hr == 0 && pbstr != IntPtr.Zero)
                {
                    try { path = Marshal.PtrToStringBSTR(pbstr) ?? ""; }
                    finally { Marshal.FreeBSTR(pbstr); }
                }
                info.ExternallyLinked = hr == 0 && linked != 0 && !string.IsNullOrEmpty(path);
            }
            finally
            {
                Marshal.Release(pObj);
            }
        }

        /// <summary>探测活动文档名与当前选中零部件（供连接状态区显示）。</summary>
        public static void ProbeSessionInfo(CaxaSession session, out string docName, out SelectedInfo info)
        {
            docName = null;
            info = new SelectedInfo();
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
                IntPtr pElem = GetSelectedElement(pScene, "info");
                if (pElem == IntPtr.Zero) return;
                try
                {
                    info.HasElement = true;
                    int t;
                    if (TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(info)", out t))
                    {
                        info.Type = t;
                        info.Kind = MapElementKind(t);
                    }
                    info.Name = TryReadBstrProp(pElem, SlotElemGetName, "IZElement.get_Name(info)") ?? "";
                    TryReadExternalLink(pElem, info.Type, info);
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
        }

        #endregion

        #region 设计树遍历

        private const int MaxTreeNodes = 12000;

        private delegate void TreeVisit(IntPtr pElem, int type, int depth);

        /// <summary>
        /// 递归遍历设计树。onEnter 在读取类型后调用（此时 pElem 是有效的 IZElement），
        /// onExit 在该节点的所有子节点处理完后调用；descend 决定是否继续向下展开。
        /// </summary>
        private static void WalkTree(IntPtr pElem, int depth, int maxDepth,
            TreeVisit onEnter, TreeVisit onExit, Func<int, bool> descend,
            Func<bool> shouldStop, Action<int> heartbeat, ref int nodes)
        {
            if (pElem == IntPtr.Zero || depth > maxDepth) return;
            if (nodes >= MaxTreeNodes) return;
            nodes++;

            int type = -1;
            TryReadIntProp(pElem, SlotElemGetType, "IZElement.get_Type(walk)", out type);

            if (onEnter != null) onEnter(pElem, type, depth);
            if (shouldStop != null && shouldStop()) return;
            if (heartbeat != null && (nodes % 20) == 0) heartbeat(nodes);

            bool go = descend == null || descend(type);
            if (go)
            {
                IntPtr child = CallGetObj(pElem, SlotElemGetFirstChild,
                    "IZElement.GetFirstChild(d=" + depth + ")");
                int guard = 0;
                while (child != IntPtr.Zero && guard++ < 2000)
                {
                    IntPtr pChild = IntPtr.Zero;
                    if (TryQI(child, IID_IZElement, out pChild) != 0) pChild = IntPtr.Zero;
                    if (pChild != IntPtr.Zero)
                    {
                        try
                        {
                            WalkTree(pChild, depth + 1, maxDepth, onEnter, onExit,
                                descend, shouldStop, heartbeat, ref nodes);
                        }
                        finally { Marshal.Release(pChild); }
                    }
                    if (shouldStop != null && shouldStop())
                    {
                        Marshal.Release(child);
                        child = IntPtr.Zero;
                        break;
                    }
                    if (nodes >= MaxTreeNodes)
                    {
                        Marshal.Release(child);
                        child = IntPtr.Zero;
                        break;
                    }
                    IntPtr next = CallGetObj(child, SlotElemGetNextChild,
                        "IZElement.GetNextChild(d=" + depth + ")");
                    Marshal.Release(child);
                    child = next;
                }
            }

            if (onExit != null) onExit(pElem, type, depth);
        }

        /// <summary>读取折弯对象上的系数属性（两种来源共用：设计树折弯元素 / 折弯线来源元素）。</summary>
        private static void FillBendProps(IntPtr pBend, BendRowInfo row, string tag)
        {
            int at;
            if (TryReadIntProp(pBend, SlotBendGetAllowanceType,
                    "Bend.get_BendAllowanceType(" + tag + ")", out at))
                row.AllowanceType = at;

            short ck;
            if (TryReadShortProp(pBend, SlotBendGetCustomKUsed,
                    "Bend.get_CustomKFactorUsed(" + tag + ")", out ck))
                row.CustomKUsed = ck != 0;

            double v;
            if (TryReadDoubleProp(pBend, SlotBendGetCustomKValue,
                    "Bend.get_CustomKFactorValue(" + tag + ")", out v))
                row.CustomK = v;

            if (TryReadDoubleProp(pBend, SlotBendGetDeductionValue,
                    "Bend.get_BendDeductionValue(" + tag + ")", out v))
                row.DeductionMm = v * 1000.0;

            if (row.AngleDeg == 0 &&
                TryReadDoubleProp(pBend, SlotBendGetBendAngle,
                    "Bend.get_BendAngle(" + tag + ")", out v))
                row.AngleDeg = v * 180.0 / Math.PI;

            if (TryReadDoubleProp(pBend, SlotBendGetInsideRadius,
                    "Bend.get_InsideRadius(" + tag + ")", out v))
                row.RadiusMm = v * 1000.0;

            // 「使用零件的最小折弯半径」：内半径等于零件最小折弯半径 → 是，否则 否
            row.UsePartMinRadius = double.IsNaN(row.PartMinRadiusMm)
                ? -1
                : (Math.Abs(row.RadiusMm - row.PartMinRadiusMm) < 1e-6 ? 1 : 0);

            // 「使用零件K系数」：非K因子类型不适用；K因子类型时 CustomKFactorUsed=false 即使用零件K
            row.UsePartKFactor = row.AllowanceType == 1 ? (row.CustomKUsed ? 0 : 1) : -1;
        }

        private sealed class PartCtx
        {
            public int Id;
            public string Name = "";
            public bool Skip;
            public bool SmValid;
            public double ThicknessMm;
            public double KFactor;
            /// <summary>零件最小折弯半径（mm，IZSheetMetalPart::get_MinBendRadius）；未知为 NaN。</summary>
            public double MinBendRadiusMm = double.NaN;
            /// <summary>零件特征管理器里的钣金折弯特征数（唯一采集通道）。</summary>
            public int BendLines;
            /// <summary>板料牌号（来自板料属性）。</summary>
            public string StockName = "";
        }

        /// <summary>扫描器上下文：维护「当前所在钣金件」，把折弯采集归属到正确的零件。</summary>
        private sealed class ScanCtx
        {
            public readonly List<BendRowInfo> Rows = new List<BendRowInfo>();
            public int PartCount;
            public int NodeCount;
            public Func<bool> ShouldStop;
            public Action<int> Heartbeat;
            public Action<string> Log;

            private readonly List<PartCtx> _stack = new List<PartCtx>();

            private PartCtx Current
            {
                get { return _stack.Count > 0 ? _stack[_stack.Count - 1] : null; }
            }

            public bool Descend(int type)
            {
                if (Current != null) return true;
                return type == 0 || type == 1 || type == 2 || type == ElemTypeSheetMetalPart;
            }

            public void OnEnter(IntPtr pElem, int type, int depth)
            {
                NodeCount++;
                if (type == ElemTypeSheetMetalPart)
                {
                    var pc = new PartCtx();
                    ReadElementIdentity(pElem, out pc.Id, out pc.Name);
                    pc.Skip = pc.Name.StartsWith("展开", StringComparison.Ordinal);
                    if (pc.Skip)
                    {
                        Log("跳过展开态零件（" + pc.Name + "）");
                    }
                    else
                    {
                        IntPtr pSm;
                        if (TryQI(pElem, IID_IZSheetMetalPart, out pSm) == 0 && pSm != IntPtr.Zero)
                        {
                            try
                            {
                                pc.SmValid = true;
                                // 板厚与 K 因子取自「板料属性」，读不到才回退零件属性
                                string stockName;
                                ReadStockThicknessK(pSm, pc.Name, out pc.ThicknessMm, out pc.KFactor,
                                    out stockName);
                                pc.StockName = stockName;

                                double minR;
                                if (TryReadDoubleProp(pSm, SlotSmGetMinBendRadius,
                                        "SM.get_MinBendRadius(" + pc.Name + ")", out minR) && minR > 0)
                                {
                                    pc.MinBendRadiusMm = minR * 1000.0;
                                    Log(string.Format("钣金件[{0}] 最小折弯半径 = {1:0.###} mm",
                                        pc.Name, pc.MinBendRadiusMm));
                                }
                            }
                            finally { Marshal.Release(pSm); }
                        }
                        else
                        {
                            Log("钣金件[" + pc.Name + "] QI(IZSheetMetalPart) 失败，无法读厚度/K因子");
                        }
                        PartCount++;
                    }
                    _stack.Add(pc);
                    return;
                }
            }

            public void OnExit(IntPtr pElem, int type, int depth)
            {
                if (type != ElemTypeSheetMetalPart) return;
                if (_stack.Count == 0) return;
                PartCtx pc = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);
                if (pc.Skip) return;

                // 折弯采集只有一条通道：零件特征管理器（API 设计树视图里没有折弯节点）
                if (pc.SmValid)
                {
                    IntPtr pSm;
                    if (TryQI(pElem, IID_IZSheetMetalPart, out pSm) == 0 && pSm != IntPtr.Zero)
                    {
                        try
                        {
                            // 抓取前先对目标零件做一次重新生成（IZPart.Update，槽 7）。
                            // 不重建时外链零件首次进入的 get_ShowInBrowser 全为 true，
                            // 内部折弯会被误入表；重建后属性才反映真实状态。
                            PartUpdate(pSm, "抓取前重建[" + pc.Name + "]");
                            System.Threading.Thread.Sleep(30);
                            pc.BendLines = CollectBendLines(pSm, pc, Rows, Log);
                        }
                        finally { Marshal.Release(pSm); }
                    }
                }

                Log(string.Format("钣金件[{0}] 折弯 {1} 个 板料={2}", pc.Name, pc.BendLines, pc.StockName));
                if (pc.ThicknessMm > 20 || (pc.ThicknessMm > 0 && pc.ThicknessMm < 0.15))
                    Log("  ⚠ 厚度换算异常 " + pc.ThicknessMm.ToString("0.###") + " mm，请核对单位");
            }
        }

        /// <summary>
        /// 扫描当前设计场景的全部钣金折弯（跳过名为「展开…」的展开态零件）。
        /// </summary>
        public static List<BendRowInfo> ScanBends(CaxaSession session, Action<string> log,
            Action<int, string> pump, Func<bool> shouldStop,
            out int partCount)
        {
            partCount = 0;

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pDoc == IntPtr.Zero || pScene == IntPtr.Zero)
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
                throw new InvalidOperationException("当前没有活动设计场景，请先打开一个装配或零件。");
            }

            IntPtr pRoot = IntPtr.Zero;
            try
            {
                _sceneForBrowser = pScene;

                // 场景级 RegenerateParts（槽 21）在本机返回 E_NOTIMPL，无法使用。
                // 改为在遍历到每个钣金件时对其单独 IZPart.Update 重建（见 ScanCtx.OnExit）。
                // 这里只先刷新一次性场景 UI，保证设计树基础结构可用。
                RefreshSceneUi(pScene);

                pRoot = CallGetObj(pScene, SlotSceneGetTopElement, "IZSceneDoc.GetTopElement");
                if (pRoot == IntPtr.Zero)
                    throw new InvalidOperationException("设计场景为空（GetTopElement 返回空）。");

                var ctx = new ScanCtx
                {
                    Log = log ?? (s => { }),
                    ShouldStop = shouldStop,
                    Heartbeat = n =>
                    {
                        if (pump != null) pump(n, "正在扫描设计树…已检查 " + n + " 个节点");
                    }
                };
                int nodes = 0;
                WalkTree(pRoot, 0, 8, ctx.OnEnter, ctx.OnExit, ctx.Descend,
                    shouldStop, ctx.Heartbeat, ref nodes);

                partCount = ctx.PartCount;
                if (nodes >= MaxTreeNodes)
                    (log ?? (s => { }))("⚠ 设计树节点超过 " + MaxTreeNodes + " 个上限，未完全扫描（请分批处理或改用单条修改）");
                if (pump != null) pump(nodes, "扫描完成：钣金件 " + partCount + " 个、折弯 " + ctx.Rows.Count + " 条");

                // 收尾：IZPart.Update 重建会把零件切到展开配置，必须恢复折叠态
                RestoreFoldState(pScene, pDoc, log);
                return ctx.Rows;
            }
            finally
            {
                _sceneForBrowser = IntPtr.Zero;
                if (pRoot != IntPtr.Zero) Marshal.Release(pRoot);
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        #endregion

        #region 写入折弯系数

        /// <summary>
        /// 统一设置：KFactor 模式 = 每个钣金件写零件级 K 因子，并让所有折弯改用零件 K（关闭自定义 K）；
        /// BendDeduction 模式 = 每个折弯写「折弯扣除」并写扣除值（mm 入参）。
        /// 成功判定一律为写入后的即时回读值。
        /// </summary>
        public static ApplyResult ApplyGlobal(CaxaSession session, bool kFactorMode, double valueMmOrK,
            Action<string> log, Action<int, string> pump, Func<bool> shouldStop)
        {
            var res = new ApplyResult();
            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pDoc == IntPtr.Zero || pScene == IntPtr.Zero)
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
                throw new InvalidOperationException("当前没有活动设计场景，请先打开一个装配或零件。");
            }

            IntPtr pRoot = IntPtr.Zero;
            try
            {
                pRoot = CallGetObj(pScene, SlotSceneGetTopElement, "IZSceneDoc.GetTopElement");
                if (pRoot == IntPtr.Zero)
                    throw new InvalidOperationException("设计场景为空（GetTopElement 返回空）。");

                var ctx = new ApplyCtx
                {
                    KMode = kFactorMode,
                    Value = valueMmOrK,
                    Result = res,
                    Log = log ?? (s => { }),
                    Pump = pump,
                    ShouldStop = shouldStop
                };
                int nodes = 0;
                _sceneForRestore = pScene;
                _sceneForBrowser = pScene;
                WalkTree(pRoot, 0, 8, ctx.OnEnter, ctx.OnExit, ctx.Descend,
                    shouldStop, ctx.Heartbeat, ref nodes);
                _sceneForRestore = IntPtr.Zero;
                _sceneForBrowser = IntPtr.Zero;

                // 写入只靠零件特征管理器（与采集口径完全一致），不再做场景级兜底
                // 收尾：恢复默认配置 + 把焦点选回折叠件（IZPart.Update 会把环境切到展开配置）
                RestoreFoldState(pScene, pDoc, log);
                return res;
            }
            finally
            {
                if (pRoot != IntPtr.Zero) Marshal.Release(pRoot);
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        /// <summary>批量写入的执行上下文（与扫描共用同一套遍历与回读校验）。</summary>
        private sealed class ApplyCtx
        {
            public bool KMode;
            public double Value;
            public ApplyResult Result;
            public Action<string> Log;
            public Action<int, string> Pump;
            public Func<bool> ShouldStop;

            private readonly List<PartCtx2> _stack = new List<PartCtx2>();

            private sealed class PartCtx2
            {
                public int Id;
                public string Name = "";
                public bool Skip;
                public bool SmValid;
                public IntPtr pSm;   // 持有到零件退出，退出时 Release
                /// <summary>板厚（mm，取自板料属性），折弯扣除范围校验用。</summary>
                public double ThicknessMm;
            }

            private PartCtx2 Current
            {
                get { return _stack.Count > 0 ? _stack[_stack.Count - 1] : null; }
            }

            public bool Descend(int type)
            {
                if (Current != null) return true;
                return type == 0 || type == 1 || type == 2 || type == ElemTypeSheetMetalPart;
            }

            public void Heartbeat(int nodes)
            {
                if (Pump != null)
                    Pump(nodes, "正在写入…已处理 " + nodes + " 个节点" +
                        (Result != null ? "（折弯成功 " + Result.BendOk + "/" + Result.BendTotal + "）" : ""));
            }

            public void OnEnter(IntPtr pElem, int type, int depth)
            {
                if (type == ElemTypeSheetMetalPart)
                {
                    var pc = new PartCtx2();
                    ReadElementIdentity(pElem, out pc.Id, out pc.Name);
                    pc.Skip = pc.Name.StartsWith("展开", StringComparison.Ordinal);
                    if (!pc.Skip)
                    {
                        IntPtr pSm;
                        if (TryQI(pElem, IID_IZSheetMetalPart, out pSm) == 0 && pSm != IntPtr.Zero)
                        {
                            pc.pSm = pSm;
                            pc.SmValid = true;
                            string stockName;
                            double kk;
                            ReadStockThicknessK(pSm, pc.Name, out pc.ThicknessMm, out kk, out stockName);
                        }
                        if (KMode)
                        {
                            Result.PartTotal++;
                            if (pc.SmValid)
                            {
                                int hr = TrySetDouble(pc.pSm, SlotSmSetKFactor, Value,
                                    "SM.set_KFactor(" + pc.Name + ")");
                                double back = double.NaN;
                                bool ok = hr == 0 && TryReadDoubleProp(pc.pSm, SlotSmGetKFactor,
                                    "SM.get_KFactor(verify " + pc.Name + ")", out back)
                                    && Math.Abs(back - Value) < 1e-9;
                                if (ok) Result.PartOk++;
                                else Result.Fail(string.Format("零件「{0}」K因子回读不符：期望 {1:0.####}，实际 {2:0.####}",
                                    pc.Name, Value, back));
                            }
                            else
                            {
                                Result.Fail("零件「" + pc.Name + "」QI(IZSheetMetalPart) 失败，无法写 K 因子");
                            }
                        }
                    }
                    _stack.Add(pc);
                    return;
                }
            }

            public void OnExit(IntPtr pElem, int type, int depth)
            {
                if (type != ElemTypeSheetMetalPart) return;
                if (_stack.Count == 0) return;
                PartCtx2 pc = _stack[_stack.Count - 1];
                _stack.RemoveAt(_stack.Count - 1);

                // 写入只有一条通道：零件特征管理器（与采集口径一致）
                if (pc.SmValid)
                    WriteBendFeatures(pc.pSm, pc.Name);

                if (pc.pSm != IntPtr.Zero) { Marshal.Release(pc.pSm); pc.pSm = IntPtr.Zero; }
            }

            /// <summary>
            /// 按折弯线索引写入一个主零件的钣金折弯特征（只认 type=22 且实现 IZSheetMetalBend）；返回特征数。
            /// </summary>
            private int WriteBendFeatures(IntPtr pSm, string partName)
            {
                int done = 0;
                string stockName;
                double kk, thicknessMm;
                ReadStockThicknessK(pSm, partName, out thicknessMm, out kk, out stockName);
                int total = ForEachBendFeatureByFeatureMgr(pSm, partName, (lineId, pBend, elemId, elemType, elemName) =>
                {
                    Result.BendTotal++;
                    string nm = string.IsNullOrEmpty(elemName) ? "折弯特征" + elemId : elemName;
                    string err = WriteOne(pBend, nm, KMode ? WriteCustomK : WriteDeduction, Value,
                        thicknessMm, pSm);
                    if (err == null) { Result.BendOk++; done++; }
                    else Result.Fail(err);
                }, Log);

                if (Log != null)
                    Log(string.Format("钣金件[{0}] 折弯特征 {1} 个，写入成功 {2} 个（累计 {3}/{4}）",
                        partName, total, done, Result.BendOk, Result.BendTotal));
                return total;
            }
        }

        private const int WriteCustomK = 1;     // 折弯级 K 因子（写入 CustomKFactorValue）
        private const int WriteDeduction = 2;   // 折弯扣除（mm）

        /// <summary>
        /// 切换「使用零件K系数」：是 → set_CustomKFactorUsed(false)；否 → 先写自定义 K 值再置 true。
        /// 判定只认回读的 CustomKFactorUsed（该属性可回读）。
        /// </summary>
        private static string WriteKFlag(IntPtr pBend, string bendName, bool usePart,
            double partK, IntPtr pSm)
        {
            int hrType = TrySetInt(pBend, SlotBendSetAllowanceType, BendTypeKFactor,
                "Bend.set_BendAllowanceType(" + bendName + ", Kflag)");
            if (usePart)
            {
                TrySetShort(pBend, SlotBendSetCustomKUsed, 0,
                    "Bend.set_CustomKFactorUsed(" + bendName + ", 是)");
            }
            else
            {
                if (partK > 0 && partK <= 2)
                    TrySetDouble(pBend, SlotBendSetCustomKValue, partK,
                        "Bend.set_CustomKFactorValue(" + bendName + ", Kflag)");
                TrySetShort(pBend, SlotBendSetCustomKUsed, -1,
                    "Bend.set_CustomKFactorUsed(" + bendName + ", 否)");
            }
            PartUpdate(pSm, bendName);

            int type2 = -1;
            short used = -1;
            bool hasUsed = TryReadShortProp(pBend, SlotBendGetCustomKUsed,
                "Bend.get_CustomKFactorUsed(verify Kflag)", out used);
            TryReadIntProp(pBend, SlotBendGetAllowanceType,
                "Bend.get_BendAllowanceType(verify Kflag)", out type2);

            if (hrType == 0 && hasUsed && type2 == BendTypeKFactor && (used == 0) == usePart)
                return null;
            return string.Format("折弯「{0}」切换「使用零件K系数」未生效（回读 type={1} used={2}）",
                bendName, type2, hasUsed ? used.ToString() : "不可读");
        }

        /// <summary>
        /// 对单个折弯写入并回读校验。成功返回 null，失败返回原因（规则 7：只认回读值）。
        /// 实测（2026-10-29 19:39 日志）：特征对象上的 set_* 全部返回 hr=0，但值要等零件 Update 才提交，
        /// 未提交时 get_* 读回仍是旧值 ⇒ 每次写入后必须先 IZPart.Update(slot 7) 再校验。
        /// K 模式：get_CustomKFactorValue 在特征对象上返回 E_FAIL，K 值无法回读，
        /// 改用「set hr=0 + type==1」判定，并在日志里明确标注 K 值未回读。
        /// 折弯扣除有物理上限：D ≤ 2×(R+T) − θ×R，超出时系统会拒绝写入，先拦截。
        /// </summary>
        private static string WriteOne(IntPtr pBend, string bendName, int mode,
            double valueMmOrK, double thicknessMm, IntPtr pSm)
        {
            if (mode == WriteCustomK)
            {
                int hr1 = TrySetInt(pBend, SlotBendSetAllowanceType, BendTypeKFactor,
                    "Bend.set_BendAllowanceType(" + bendName + ")");
                int hr2 = TrySetDouble(pBend, SlotBendSetCustomKValue, valueMmOrK,
                    "Bend.set_CustomKFactorValue(" + bendName + ")");
                TrySetShort(pBend, SlotBendSetCustomKUsed, -1,
                    "Bend.set_CustomKFactorUsed(" + bendName + ")");
                PartUpdate(pSm, bendName);

                int type2 = -1; short used; double kBack = double.NaN;
                bool hasType = TryReadIntProp(pBend, SlotBendGetAllowanceType,
                    "Bend.get_BendAllowanceType(verify)", out type2);
                TryReadShortProp(pBend, SlotBendGetCustomKUsed,
                    "Bend.get_CustomKFactorUsed(verify)", out used);
                bool hasK = TryReadDoubleProp(pBend, SlotBendGetCustomKValue,
                    "Bend.get_CustomKFactorValue(verify)", out kBack);

                if (hr1 == 0 && hr2 == 0 && hasType && type2 == BendTypeKFactor
                    && (!hasK || Math.Abs(kBack - valueMmOrK) < 1e-9))
                {
                    if (!hasK)
                        Diagnostics.Log(string.Format(
                            "折弯[{0}] K 值不可回读，按 set hr=0 + type=1 判定已生效（值 {1:0.####}）",
                            bendName, valueMmOrK));
                    return null;
                }
                return string.Format(
                    "折弯「{0}」K因子写入未生效（hr=0x{1:X}/0x{2:X}，回读 type={3} used={4} k={5}）",
                    bendName, hr1, hr2, type2, used,
                    hasK ? kBack.ToString("0.####") : "不可读");
            }

            double api = valueMmOrK / 1000.0;

            // 折弯扣除上限校验：D ≤ 2(R+T) − θR（θ 为弧度，R/T 单位米）
            double angleRad = 0, radiusM = 0;
            bool hasGeo = TryReadDoubleProp(pBend, SlotBendGetBendAngle,
                    "Bend.get_BendAngle(" + bendName + ")", out angleRad)
                && TryReadDoubleProp(pBend, SlotBendGetInsideRadius,
                    "Bend.get_InsideRadius(" + bendName + ")", out radiusM);
            if (hasGeo && thicknessMm > 0 && angleRad > 0)
            {
                double tM = thicknessMm / 1000.0;
                double maxD = 2.0 * (radiusM + tM) - angleRad * radiusM;
                if (maxD > 0 && api > maxD + 1e-9)
                {
                    Diagnostics.Log(string.Format(
                        "折弯[{0}] 扣除 {1:0.###}mm 超出允许范围（厚 {2:0.###} 内半径 {3:0.###} 角度 {4:0.##}° → 上限 {5:0.###}mm）",
                        bendName, valueMmOrK, thicknessMm, radiusM * 1000.0,
                        angleRad * 180.0 / Math.PI, maxD * 1000.0));
                    return string.Format(
                        "折弯「{0}」折弯扣除 {1:0.###}mm 超出该折弯允许范围（最大 {2:0.###}mm = 2×(R{3:0.###}+T{4:0.###}) − 角度×R，角度 {5:0.##}°）",
                        bendName, valueMmOrK, maxD * 1000.0, radiusM * 1000.0, thicknessMm,
                        angleRad * 180.0 / Math.PI);
                }
            }

            string err = WriteDeductionOnce(pBend, bendName, api, pSm);
            return err;
        }

        /// <summary>写折弯扣除（type→value），提交零件后回读；不生效则换写入顺序重试。</summary>
        private static string WriteDeductionOnce(IntPtr pBend, string bendName, double apiMeters,
            IntPtr pSm)
        {
            int hrA = TrySetInt(pBend, SlotBendSetAllowanceType, BendTypeDeduction,
                "Bend.set_BendAllowanceType(" + bendName + ")");
            int hrB = TrySetDouble(pBend, SlotBendSetDeductionValue, apiMeters,
                "Bend.set_BendDeductionValue(" + bendName + ")");
            PartUpdate(pSm, bendName);

            int typeBack = -1; double back = double.NaN;
            bool okT = TryReadIntProp(pBend, SlotBendGetAllowanceType,
                "Bend.get_BendAllowanceType(verify)", out typeBack) && typeBack == BendTypeDeduction;
            bool okV = TryReadDoubleProp(pBend, SlotBendGetDeductionValue,
                "Bend.get_BendDeductionValue(verify)", out back) && Math.Abs(back - apiMeters) < 1e-7;
            if (hrA == 0 && hrB == 0 && okT && okV) return null;

            Diagnostics.Log("折弯[" + bendName + "] 首次写入未生效，改试「先写值再写类型」");
            int hrC = TrySetDouble(pBend, SlotBendSetDeductionValue, apiMeters,
                "Bend.set_BendDeductionValue(" + bendName + ", retry)");
            int hrD = TrySetInt(pBend, SlotBendSetAllowanceType, BendTypeDeduction,
                "Bend.set_BendAllowanceType(" + bendName + ", retry)");
            PartUpdate(pSm, bendName);
            okT = TryReadIntProp(pBend, SlotBendGetAllowanceType,
                "Bend.get_BendAllowanceType(verify2)", out typeBack) && typeBack == BendTypeDeduction;
            okV = TryReadDoubleProp(pBend, SlotBendGetDeductionValue,
                "Bend.get_BendDeductionValue(verify2)", out back) && Math.Abs(back - apiMeters) < 1e-7;
            if (hrC == 0 && hrD == 0 && okT && okV) return null;

            // 两种顺序都写不进：多半是该折弯勾了「使用零件的折弯扣除」。
            // 先在对象上找到这个开关并关掉，再重写一次（找到真开关才动手，否则不动模型）。
            string switchName = TryTurnOffPartDeductionSwitch(pBend);
            if (!string.IsNullOrEmpty(switchName))
            {
                Diagnostics.Log("折弯[" + bendName + "] 已关掉开关 " + switchName + " 后重试写入");
                int hrE = TrySetInt(pBend, SlotBendSetAllowanceType, BendTypeDeduction,
                    "Bend.set_BendAllowanceType(" + bendName + ", sw)");
                int hrF = TrySetDouble(pBend, SlotBendSetDeductionValue, apiMeters,
                    "Bend.set_BendDeductionValue(" + bendName + ", sw)");
                PartUpdate(pSm, bendName);
                okT = TryReadIntProp(pBend, SlotBendGetAllowanceType,
                    "Bend.get_BendAllowanceType(verify3)", out typeBack) && typeBack == BendTypeDeduction;
                okV = TryReadDoubleProp(pBend, SlotBendGetDeductionValue,
                    "Bend.get_BendDeductionValue(verify3)", out back) && Math.Abs(back - apiMeters) < 1e-7;
                if (hrE == 0 && hrF == 0 && okT && okV) return null;
            }

            return string.Format(
                "折弯「{0}」折弯扣除写入未生效（hr=0x{1:X}/0x{2:X}/0x{3:X}/0x{4:X}，请求 {5:0.###}mm，回读 type={6} 扣除={7:0.###}mm）。"
                + (string.IsNullOrEmpty(switchName)
                    ? "该折弯正在使用零件继承的折弯扣除，且接口里没找到可写的「使用零件折弯扣除」开关（详见探针报告）。"
                    : "已关闭开关 " + switchName + " 但仍未落盘，请查看运行日志。"),
                bendName, hrA, hrB, hrC, hrD, apiMeters * 1000.0, typeBack, back * 1000.0);
        }

        /// <summary>
        /// 写入折弯内半径（IZSheetMetalBend::set_InsideRadius，槽 48），提交后回读校验。
        /// 「使用零件的最小折弯半径」=是 时 CAXA 会忽略单独写入，调用方需先拦下。
        /// </summary>
        private static string WriteInsideRadius(IntPtr pBend, string bendName,
            double radiusMm, IntPtr pSm)
        {
            double api = radiusMm / 1000.0;
            int hr = TrySetDouble(pBend, SlotBendSetInsideRadius, api,
                "Bend.set_InsideRadius(" + bendName + ", " + radiusMm.ToString("0.###") + "mm)");
            PartUpdate(pSm, bendName);

            double back = 0;
            bool ok = TryReadDoubleProp(pBend, SlotBendGetInsideRadius,
                "Bend.get_InsideRadius(verify radius)", out back) && Math.Abs(back - api) < 1e-7;
            if (hr == 0 && ok) return null;

            return string.Format("折弯「{0}」内半径写入未生效（hr=0x{1:X}，请求 {2:0.###}mm，回读 {3:0.###}mm）。"
                + "若该折弯正在使用零件的最小折弯半径，请先把该列改成「否」。",
                bendName, hr, radiusMm, back * 1000.0);
        }

        /// <summary>提交零件改动（IZPart::Update slot 7）。特征上的 set_* 只有 Update 后才会落盘。</summary>
        private static void PartUpdate(IntPtr pSm, string what)
        {
            if (pSm == IntPtr.Zero) return;
            TryCallNoArg(pSm, SlotPartUpdate, "IZPart.Update(" + what + ")");

            // 实测：Update 会把钣金件切到「展开」的多配置，此时回读值来自展开态（看不到刚写的值）。
            // 所以每次提交后立刻把默认（折叠）配置激活回来，再做回读校验。
            if (_sceneForRestore != IntPtr.Zero)
                ActivateDefaultConfiguration(_sceneForRestore, null);
        }

        /// <summary>写入期间记录活动场景指针，供 PartUpdate 之后立即恢复默认配置。</summary>
        private static IntPtr _sceneForRestore;

        /// <summary>采集/写入期间记录活动场景指针，供折弯可见性重试时强制刷新设计树浏览器。</summary>
        [ThreadStatic]
        private static IntPtr _sceneForBrowser;

        /// <summary>读折弯特征的系统名（IZElement::get_Name 槽 25），失败返回空串。</summary>
        private static string ReadSystemName(IntPtr pBend)
        {
            IntPtr pElem;
            if (TryQI(pBend, IID_IZElement, out pElem) != 0 || pElem == IntPtr.Zero) return "";
            try { return TryReadBstrProp(pElem, SlotElemGetSystemName, null) ?? ""; }
            catch { return ""; }
            finally { Marshal.Release(pElem); }
        }

        /// <summary>
        /// 单条折弯写入：按「零件 + 特征管理器序号」定位折弯特征后写入并回读校验。
        /// </summary>
        public static ApplyResult ApplyOneBend(CaxaSession session, BendRowInfo target,
            bool kFactorMode, double value, Action<string> log, out string error)
        {
            return ApplyOneBend(session, target, kFactorMode, value, 0, log, out error);
        }

        /// <summary>
        /// specialMode：0=按值写入系数；1=「使用零件K系数」改「是」；2=改「否」；3=写内半径（value=mm）。
        /// </summary>
        public static ApplyResult ApplyOneBend(CaxaSession session, BendRowInfo target,
            bool kFactorMode, double value, int specialMode, Action<string> log, out string error)
        {
            error = null;
            var res = new ApplyResult();
            if (target == null) { error = "未选择折弯行。"; return res; }
            if (log != null)
                log(string.Format("单条写入：零件「{0}」折弯「{1}」（特征序号 {2}）",
                    target.PartName, target.BendName, target.BendId));

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            if (pDoc == IntPtr.Zero || pScene == IntPtr.Zero)
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
                error = "当前没有活动设计场景。";
                return res;
            }

            IntPtr pRoot = IntPtr.Zero;
            try
            {
                // 写入过程中 IZPart.Update 会切到「展开」配置，需要记录场景以便立即切回
                _sceneForRestore = pScene;
                _sceneForBrowser = pScene;
                pRoot = CallGetObj(pScene, SlotSceneGetTopElement, "IZSceneDoc.GetTopElement");
                if (pRoot == IntPtr.Zero) { error = "设计场景为空。"; return res; }

                bool found = false;

                // 唯一通道：按「零件 + 特征管理器序号」定位折弯特征
                // （折弯不在设计树的 API 视图里，表格里的 BendId 就是特征管理器序号，与采集口径一致）
                if (target.BendId > 0)
                {
                    TreeVisit findPart = (pElem, type, depth) =>
                    {
                        if (found || type != ElemTypeSheetMetalPart) return;
                        int pid;
                        string pname;
                        ReadElementIdentity(pElem, out pid, out pname);
                        if (target.PartId > 0 && pid != target.PartId) return;

                        IntPtr pSm;
                        if (TryQI(pElem, IID_IZSheetMetalPart, out pSm) != 0 || pSm == IntPtr.Zero) return;
                        try
                        {
                            ForEachBendFeatureByFeatureMgr(pSm, pname, (lineId, pBend, featIdx, elemType, elemName) =>
                            {
                                if (found || featIdx != target.BendId) return;
                                res.BendTotal = 1;
                                string err = specialMode == 1 || specialMode == 2
                                    ? WriteKFlag(pBend, elemName, specialMode == 1, target.PartKFactor, pSm)
                                    : specialMode == 3
                                        ? WriteInsideRadius(pBend, elemName, value, pSm)
                                        : WriteOne(pBend, elemName,
                                            kFactorMode ? WriteCustomK : WriteDeduction, value,
                                            target.ThicknessMm, pSm);
                                if (err == null) res.BendOk = 1;
                                else res.Fail(err);
                                found = true;

                                // 写完后按回读值刷新该行（「使用零件折弯扣除」也随之变为 否）
                                FillBendProps(pBend, target, target.BendName);
                                if (target.AllowanceType != BendTypeDeduction)
                                    target.UsePartDeduction = -1;
                                else if (!double.IsNaN(target.PartDeductionMm))
                                    target.UsePartDeduction =
                                        Math.Abs(target.DeductionMm - target.PartDeductionMm) < 1e-6 ? 1 : 0;
                            }, s => { });
                        }
                        finally { Marshal.Release(pSm); }
                    };
                    int n1 = 0;
                    WalkTree(pRoot, 0, 8, findPart, null, t => true, null, null, ref n1);
                }

                if (!found)
                    error = "未在当前设计场景里找到该折弯（零件「" + target.PartName + "」/折弯「" +
                        target.BendName + "」）。请点「重新加载」后再试。";
                else if (res.BendOk == 0 && res.Failures.Count > 0) error = res.Failures[0];

                // 写入会把设计环境切到展开配置 —— 结束后恢复到默认配置并选回折叠件
                RestoreFoldState(pScene, pDoc, log);
                return res;
            }
            finally
            {
                _sceneForRestore = IntPtr.Zero;
                _sceneForBrowser = IntPtr.Zero;
                if (pRoot != IntPtr.Zero) Marshal.Release(pRoot);
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        #endregion

        #region 多配置恢复与选中回位

        /// <summary>
        /// 写完后把设计环境从「展开配置」恢复到钣金默认（折叠）配置，并把选中焦点放回
        /// 与 ics 文件同名的折弯件（名字不含「展开」）。
        /// 背景：IZPart::Update 会让钣金件切到展开的多配置，此时折弯回读值也来自展开态，
        /// 既看不到刚写进去的值，用户也会被留在展开件上。
        /// </summary>
        internal static void RestoreFoldState(IntPtr pScene, IntPtr pDoc, Action<string> log)
        {
            if (pScene == IntPtr.Zero) return;
            try
            {
                ActivateDefaultConfiguration(pScene, log);
                string docName = pDoc != IntPtr.Zero
                    ? (TryReadBstrProp(pDoc, SlotDocGetName, "IZDoc.get_Name(restore)") ?? "")
                    : "";
                SelectFoldedPart(pScene, docName, log);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("RestoreFoldState EX: " + ex.Message);
            }
        }

        /// <summary>激活钣金默认配置（IZConfigurationMgr.get_DefaultConfiguration → IZConfiguration.Activate）。</summary>
        private static void ActivateDefaultConfiguration(IntPtr pScene, Action<string> log)
        {
            IntPtr pMgrRaw = CallGetObj(pScene, SlotSceneGetConfigurationMgr,
                "IZSceneDoc.get_ConfigurationMgr");
            if (pMgrRaw == IntPtr.Zero) return;
            IntPtr pMgr = IntPtr.Zero;
            try
            {
                if (TryQI(pMgrRaw, IID_IZConfigurationMgr, out pMgr) != 0 || pMgr == IntPtr.Zero) return;
                IntPtr pDef = CallGetObj(pMgr, SlotCfgMgrGetDefault, "cfgMgr.get_DefaultConfiguration");
                if (pDef == IntPtr.Zero) return;
                try
                {
                    string name = TryReadBstrProp(pDef, SlotCfgGetName, "cfg.get_Name") ?? "";
                    IntPtr fn = Slot(pDef, SlotCfgActivate);
                    if (!LooksCallable(fn)) return;
                    var f = (FnShortArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnShortArg));
                    int hr = f(pDef, unchecked((short)-1));   // VARIANT_TRUE
                    Diagnostics.Log(string.Format("配置恢复：Activate(\"{0}\") hr=0x{1:X}", name, hr));
                    if (hr != 0 && log != null) log("  恢复到默认配置 \"" + name + "\" 失败 hr=0x" + hr.ToString("X8"));
                }
                finally { Marshal.Release(pDef); }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ActivateDefaultConfiguration EX: " + ex.Message);
            }
            finally
            {
                if (pMgr != IntPtr.Zero) Marshal.Release(pMgr);
                Marshal.Release(pMgrRaw);
            }
        }

        /// <summary>
        /// 选中与 ics 同名的折叠钣金件：优先按「文档基名」精确匹配，其次取第一个不含「展开」的钣金件。
        /// 路径：IZSceneDoc.get_SelectionMgr(27) → RemoveAllFromSelection(22) → AddElementToSelection(20)。
        /// </summary>
        private static void SelectFoldedPart(IntPtr pScene, string docName, Action<string> log)
        {
            IntPtr pTarget = IntPtr.Zero;
            IntPtr pFallback = IntPtr.Zero;
            int nodes = 0;
            try
            {
                string baseName = docName ?? "";
                int dot = baseName.LastIndexOf('.');
                if (dot > 0) baseName = baseName.Substring(0, dot);

                IntPtr pRoot = CallGetObj(pScene, SlotSceneGetTopElement, "IZSceneDoc.GetTopElement(restore)");
                if (pRoot == IntPtr.Zero) return;
                try
                {
                    TreeVisit onEnter = (pElem, type, depth) =>
                    {
                        if (type != ElemTypeSheetMetalPart) return;
                        int id; string name;
                        ReadElementIdentity(pElem, out id, out name);
                        if (name.StartsWith("展开", StringComparison.Ordinal)) return;
                        // WalkTree 会在递归返回后 Release 子节点，这里立即 QI 拿一份自己的引用
                        if (pTarget == IntPtr.Zero && baseName.Length > 0
                            && string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase))
                            TryQI(pElem, IID_IZElement, out pTarget);
                        else if (pFallback == IntPtr.Zero)
                            TryQI(pElem, IID_IZElement, out pFallback);
                    };
                    WalkTree(pRoot, 0, 8, onEnter, null,
                        t => t == 0 || t == 1 || t == 2 || t == ElemTypeSheetMetalPart,
                        null, null, ref nodes);
                }
                finally { Marshal.Release(pRoot); }

                IntPtr pPick = pTarget != IntPtr.Zero ? pTarget : pFallback;
                if (pPick == IntPtr.Zero) return;

                IntPtr pSelMgr = CallGetObj(pScene, SlotSceneSelectionMgr,
                    "IZSceneDoc.get_SelectionMgr(restore)");
                if (pSelMgr == IntPtr.Zero)
                {
                    Diagnostics.Log("恢复选中：get_SelectionMgr 返回空");
                    return;
                }
                try
                {
                    TryCallNoArg(pSelMgr, SlotSelMgrRemoveAll, "SelectionMgr.RemoveAllFromSelection");
                    IntPtr fn = Slot(pSelMgr, SlotSelMgrAddElement);
                    if (LooksCallable(fn))
                    {
                        var f = (FnObjShortArg)Marshal.GetDelegateForFunctionPointer(fn,
                            typeof(FnObjShortArg));
                        int hr = f(pSelMgr, pPick, unchecked((short)-1));
                        Diagnostics.Log(string.Format("恢复选中 hr=0x{0:X}", hr));
                        if (log != null) log("  已选中折叠钣金件" + (hr == 0 ? "" : "（hr=0x" + hr.ToString("X8") + "）"));
                    }
                }
                finally { Marshal.Release(pSelMgr); }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SelectFoldedPart EX: " + ex.Message);
            }
            finally
            {
                if (pTarget != IntPtr.Zero) Marshal.Release(pTarget);
                if (pFallback != IntPtr.Zero) Marshal.Release(pFallback);
            }
        }

        #endregion

        #region Dispatch 成员枚举（探针探索「使用零件折弯扣除」真实开关）

        /// <summary>
        /// 通过 ITypeInfo 枚举对象默认接口的全部成员名（含 memid / 读写权限）。
        /// interop 类型库没暴露的开关（如「使用零件折弯扣除」）也只有这条路能看到真名。
        /// </summary>
        internal static string DumpDispatchMembers(IntPtr pObj, string tag)
        {
            var sb = new System.Text.StringBuilder();
            if (pObj == IntPtr.Zero) return tag + "：对象为空";
            object rcw = null;
            try
            {
                rcw = Marshal.GetObjectForIUnknown(pObj);
                IRawDispatch disp = (IRawDispatch)rcw;
                int count;
                if (disp.GetTypeInfoCount(out count) != 0 || count <= 0)
                    return tag + "：没有类型信息（GetTypeInfoCount=" + count + "）";

                IntPtr pTi = IntPtr.Zero;
                int hr = disp.GetTypeInfo(0, 0x0400, out pTi);
                if (hr != 0 || pTi == IntPtr.Zero)
                    return tag + "：GetTypeInfo 失败 hr=0x" + hr.ToString("X8");

                object tiObj = null;
                try
                {
                    tiObj = Marshal.GetObjectForIUnknown(pTi);
                    var ti = (System.Runtime.InteropServices.ComTypes.ITypeInfo)tiObj;
                    IntPtr pAttr = IntPtr.Zero;
                    try
                    {
                        ti.GetTypeAttr(out pAttr);
                        var attr = (System.Runtime.InteropServices.ComTypes.TYPEATTR)
                            Marshal.PtrToStructure(pAttr, typeof(System.Runtime.InteropServices.ComTypes.TYPEATTR));

                        for (int i = 0; i < attr.cFuncs; i++)
                        {
                            IntPtr pFd = IntPtr.Zero;
                            try
                            {
                                ti.GetFuncDesc(i, out pFd);
                                var fd = (System.Runtime.InteropServices.ComTypes.FUNCDESC)
                                    Marshal.PtrToStructure(pFd,
                                        typeof(System.Runtime.InteropServices.ComTypes.FUNCDESC));
                                string name = "";
                                try
                                {
                                    string doc, help; int ctx;
                                    ti.GetDocumentation(fd.memid, out name, out doc, out ctx, out help);
                                }
                                catch { }
                                if (string.IsNullOrEmpty(name)) continue;
                                if (!IsInterestingDispatchName(name)) continue;
                                sb.AppendLine(string.Format("    memid={0,-4} {1,-10} {2}",
                                    fd.memid, InvokeKindText((int)fd.invkind), name));
                            }
                            catch { }
                            finally
                            {
                                if (pFd != IntPtr.Zero) ti.ReleaseFuncDesc(pFd);
                            }
                        }
                        for (int i = 0; i < attr.cVars; i++)
                        {
                            IntPtr pVar = IntPtr.Zero;
                            try
                            {
                                ti.GetVarDesc(i, out pVar);
                                var vd = (System.Runtime.InteropServices.ComTypes.VARDESC)
                                    Marshal.PtrToStructure(pVar,
                                        typeof(System.Runtime.InteropServices.ComTypes.VARDESC));
                                string name = "";
                                try
                                {
                                    string doc, help; int ctx;
                                    ti.GetDocumentation(vd.memid, out name, out doc, out ctx, out help);
                                }
                                catch { }
                                if (!string.IsNullOrEmpty(name) && IsInterestingDispatchName(name))
                                    sb.AppendLine(string.Format("    memid={0,-4} {1,-10} {2}", vd.memid, "变量", name));
                            }
                            catch { }
                            finally
                            {
                                if (pVar != IntPtr.Zero) ti.ReleaseVarDesc(pVar);
                            }
                        }
                    }
                    finally
                    {
                        if (pAttr != IntPtr.Zero)
                        {
                            try { ti.ReleaseTypeAttr(pAttr); } catch { }
                        }
                    }
                }
                finally
                {
                    if (tiObj != null) { try { Marshal.ReleaseComObject(tiObj); } catch { } }
                    try { Marshal.Release(pTi); } catch { }
                }
            }
            catch (Exception ex)
            {
                return tag + "：枚举异常 " + ex.Message;
            }
            finally
            {
                if (rcw != null) { try { Marshal.ReleaseComObject(rcw); } catch { } }
            }
            return sb.Length == 0 ? tag + "：没有与折弯/扣除/展开相关的成员" : sb.ToString().TrimEnd('\r', '\n');
        }

        private static string InvokeKindText(int kind)
        {
            switch (kind)
            {
                case 1: return "方法";
                case 2: return "只读";
                case 4: return "可写";
                case 8: return "读写";
                default: return "kind=" + kind;
            }
        }

        /// <summary>只打印与本次探索相关的成员，避免报告被上百个无关属性淹没。</summary>
        private static bool IsInterestingDispatchName(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("deduct") || n.Contains("unfold") || n.Contains("minbend")
                || n.Contains("minimal") || n.Contains("ofpart") || n.Contains("allowance")
                || n.Contains("bendradius") || n.Contains("kfactor") || n.Contains("fold");
        }

        /// <summary>
        /// 关掉「使用零件折弯扣除」开关：在 ITypeInfo 里找名字同时含 deduct + (part|use) 的可写属性，
        /// 用 IDispatch::Invoke(DISPATCH_PROPERTYPUT) 置 false。成功返回命中的属性名，找不到返回 null。
        /// 这条通道专治「写单独折弯扣除被静默忽略」—— 开关被关掉后单折弯扣除才落得下来。
        /// </summary>
        internal static string TryTurnOffPartDeductionSwitch(IntPtr pObj)
        {
            if (pObj == IntPtr.Zero) return null;
            object rcw = null;
            try
            {
                rcw = Marshal.GetObjectForIUnknown(pObj);
                IRawDispatch disp = (IRawDispatch)rcw;
                int count;
                if (disp.GetTypeInfoCount(out count) != 0 || count <= 0) return null;

                IntPtr pTi = IntPtr.Zero;
                if (disp.GetTypeInfo(0, 0x0400, out pTi) != 0 || pTi == IntPtr.Zero) return null;

                object tiObj = null;
                try
                {
                    tiObj = Marshal.GetObjectForIUnknown(pTi);
                    var ti = (System.Runtime.InteropServices.ComTypes.ITypeInfo)tiObj;
                    IntPtr pAttr = IntPtr.Zero;
                    try
                    {
                        ti.GetTypeAttr(out pAttr);
                        var attr = (System.Runtime.InteropServices.ComTypes.TYPEATTR)
                            Marshal.PtrToStructure(pAttr, typeof(System.Runtime.InteropServices.ComTypes.TYPEATTR));

                        for (int i = 0; i < attr.cFuncs; i++)
                        {
                            IntPtr pFd = IntPtr.Zero;
                            int memid = -1;
                            bool writable = false;
                            string name = "";
                            try
                            {
                                ti.GetFuncDesc(i, out pFd);
                                var fd = (System.Runtime.InteropServices.ComTypes.FUNCDESC)
                                    Marshal.PtrToStructure(pFd,
                                        typeof(System.Runtime.InteropServices.ComTypes.FUNCDESC));
                                memid = fd.memid;
                                writable = ((int)fd.invkind & 4) != 0;   // DISPATCH_PROPERTYPUT
                                if (!writable || memid < 0) continue;
                                string doc, help; int ctx;
                                ti.GetDocumentation(memid, out name, out doc, out ctx, out help);
                            }
                            catch { continue; }
                            finally
                            {
                                if (pFd != IntPtr.Zero) { try { ti.ReleaseFuncDesc(pFd); } catch { } }
                            }

                            if (string.IsNullOrEmpty(name)) continue;
                            string n = name.ToLowerInvariant();
                            if (!n.Contains("deduct") || !(n.Contains("part") || n.Contains("use"))) continue;

                            int hr = InvokeBoolPut(disp, memid, false);
                            Diagnostics.Log(string.Format(
                                "开关实验：{0} (memid={1}) 置 false → hr=0x{2:X}", name, memid, hr));
                            if (hr == 0) return name;
                        }
                    }
                    finally
                    {
                        if (pAttr != IntPtr.Zero) { try { ti.ReleaseTypeAttr(pAttr); } catch { } }
                    }
                }
                finally
                {
                    if (tiObj != null) { try { Marshal.ReleaseComObject(tiObj); } catch { } }
                    try { Marshal.Release(pTi); } catch { }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("TryTurnOffPartDeductionSwitch EX: " + ex.Message);
            }
            finally
            {
                if (rcw != null) { try { Marshal.ReleaseComObject(rcw); } catch { } }
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DispParams
        {
            public IntPtr rgvarg;
            public IntPtr rgdispidNamedArgs;
            public int cArgs;
            public int cNamedArgs;
        }

        /// <summary>IDispatch 属性写（VT_BOOL）：rgdispidNamedArgs 必须是 DISPID_PROPERTYPUT(-3)。</summary>
        private static int InvokeBoolPut(IRawDispatch disp, int memid, bool value)
        {
            IntPtr pVar = IntPtr.Zero, pNamed = IntPtr.Zero, pDp = IntPtr.Zero;
            try
            {
                pVar = Marshal.AllocHGlobal(24);
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pVar, i, 0);
                Marshal.WriteInt16(pVar, 0, (short)11);                        // VT_BOOL
                Marshal.WriteInt16(pVar, 8, value ? (short)-1 : (short)0);

                pNamed = Marshal.AllocHGlobal(4);
                Marshal.WriteInt32(pNamed, -3);                                // DISPID_PROPERTYPUT

                pDp = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(DispParams)));
                var dp = new DispParams { rgvarg = pVar, rgdispidNamedArgs = pNamed, cArgs = 1, cNamedArgs = 1 };
                Marshal.StructureToPtr(dp, pDp, false);

                Guid iidNull = Guid.Empty;
                return disp.Invoke(memid, ref iidNull, 0x0400, (ushort)4, pDp, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("InvokeBoolPut EX: " + ex.Message);
                return -1;
            }
            finally
            {
                if (pDp != IntPtr.Zero) Marshal.FreeHGlobal(pDp);
                if (pNamed != IntPtr.Zero) Marshal.FreeHGlobal(pNamed);
                if (pVar != IntPtr.Zero) Marshal.FreeHGlobal(pVar);
            }
        }

        #endregion
    }
}
