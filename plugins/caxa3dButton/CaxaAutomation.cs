// CAXA 3D 进程内自动化（移植自 caxa3dmytoolsaddin\plugins\Caxa3dcudeaddin\CaxaAutomation.cs）
//
// 三条口径：
//   1. Z 系列接口是「单 IDispatch 多表面」：QI 任何接口都成功，但 IDispatch 只认默认接口成员，
//      所以立方体路径必须保存原始接口指针走 vtable 直调；槽位 = 声明序号 + 7（x64 偏移 = 槽 × 8）。
//   2. 会话对象归 CAXA 所有：Dispose 只 Release 自己 QI 出来的接口指针，禁 ReleaseComObject。
//   3. 长度单位 = 米；界面输入为毫米，入口处统一 × 0.001。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    /// <summary>进程内会话：AppObject 保活 RCW，BaseApp 是本类自己 QI 出来的 IZBaseApp 接口指针。</summary>
    internal sealed class CaxaSession : IDisposable
    {
        public object AppObject;
        public IntPtr BaseApp;

        public void Dispose()
        {
            if (BaseApp != IntPtr.Zero)
            {
                Marshal.Release(BaseApp);
                BaseApp = IntPtr.Zero;
            }
            AppObject = null;
        }
    }

    internal static class CaxaAutomation
    {
        private const double MmToMeter = 0.001;

        /// <summary>界面默认边长（毫米）。</summary>
        public const double DefaultSizeMm = 10.0;

        // ---- 接口 GUID（来源 members.csv / ICApiIronCAD.tlb）----
        private static readonly Guid IID_IZBaseApp = new Guid("A5B9AF7D-228B-4ED1-BA42-D51318F7EA20");
        private static readonly Guid IID_IZSceneDoc = new Guid("3845C6DA-89D6-43F7-8B10-4BE36E0E758B");
        private static readonly Guid IID_IZPart = new Guid("F20F2411-05C4-4CA6-B2B4-5DB29531EDD7");
        private static readonly Guid IID_IZPartFeatureMgr = new Guid("26D70B9B-291B-4727-9DA1-E2D7EEC1FA27");
        private static readonly Guid IID_IZElement = new Guid("69EF5583-A26D-4913-A2F6-DBFF73E1B8A3");
        private static readonly Guid IID_IZSizeBox = new Guid("C672176C-5685-4009-8B58-24CBFF5D4EF2");
        private static readonly Guid IID_IZBody = new Guid("A22D1C80-96B5-45FD-9748-11B9DF96206D");
        private static readonly Guid IID_IZBodyFace = new Guid("690AB7C3-C247-4391-BCF9-C04E3B2DCF4B");

        // ---- vtable 槽位（= 接口成员声明序号 + 7）----
        private const int SlotActiveDoc = 11;          // IZBaseApp::get_ActiveDoc
        private const int SlotCreateNewDoc = 66;       // IZBaseApp::CreateNewDoc
        private const int SlotCreatePart = 9;          // IZSceneDoc::CreatePart
        private const int SlotCreateBlock = 19;        // IZPartFeatureMgr::CreateBlock（图素长方体）
        private const int SlotCreateCylinder = 20;     // IZPartFeatureMgr::CreateCylinder（图素圆柱/圆管内外圆柱）
        private const int SlotCreateShell = 12;        // IZPartFeatureMgr::CreateShellFeature（抽壳圆管路线）
        private const int SlotPartGetBody = 8;         // IZPart::GetBody(vbForceLoad) → ZBody
        private const int SlotBodyGetFaceIds = 8;      // IZBody::GetFaceIds → Object（面ID数组）
        private const int SlotBodyFaceGetBBox = 9;     // IZBodyFace::GetFaceBoundingBox(lFaceId) → Object
        private const int SlotPartUpdate = 7;          // IZPart::Update
        private const int SlotGetPartType = 51;        // IZPart::GetPartType
        private const int SlotElementRemove = 14;      // IZElement::Remove（探针失败清理垃圾零件）
        private const int SlotElementGetName = 9;      // IZElement::get_Name（命名回读）
        private const int SlotElementSetName = 10;     // IZElement::set_Name（首特征命名；caxa3dBatchMaster 槽位先例）
        private const int SlotSizeBoxSetHandle = 24;   // IZSizeBox::SetDisplayHandle(长,宽,高,包围盒)
        private const int SlotSizeBoxGetHandle = 25;   // IZSizeBox::GetDisplayHandle
        private const int SlotSizeBoxGetLength = 7;    // IZSizeBox::get_Length（回读校验）
        private const int SlotSizeBoxGetWidth = 9;     // IZSizeBox::get_Width
        private const int SlotSizeBoxGetHeight = 11;   // IZSizeBox::get_Height
        private const int SlotSizeBoxGetPermit = 26;   // IZSizeBox::get_PermitResetSizeBox（重置预检）
        private const int SlotSizeBoxReset = 30;       // IZSizeBox::Reset(vbScaleAllParas)（重置包围盒命令）

        // ---- 工程零件建模常量（enums.csv 2027）----
        private const int Z_PART_INNOVATION_PART = 16;       // eZPartType.Z_PART_INNOVATION_PART（创新零件）
        private const int Z_PART_STRUCTURED_PART = 32;       // eZPartType.Z_PART_STRUCTURED_PART（工程零件）

        // ---- 智能标注 Phase 1（eZRelationType / eSHOWOption，2027 enums.csv 569-593 / 2447-2483）----
        private const int Z_REL_DIMENSION = 1;               // eZRelationType.Z_RelationDimension（草图尺寸）
        private const int Z_SHOW_SMARTDIMENSIONS = 7;        // eSHOWOption.Z_SHOW_SMARTDIMENSIONS
        private const int Z_SHOW_DIMENSIONS = 9;             // eSHOWOption.Z_SHOW_DIMENSIONS
        private const int Z_SHOW_2D_DIMENSION_NAME = 23;     // eSHOWOption.Z_SHOW_2D_DIMENSION_NAME
        private const int Z_SHOW_CONSTRAINT_DIMS_DEPENDS = 30; // eSHOWOption.Z_SHOW_CONSTRAINT_DIMS_DEPENDS
        private const int Z_SHOW_SMARTDIM_ON_SELECTION = 31; // eSHOWOption.Z_SHOW_SMARTDIM_ON_SELECTION
        private const int Z_SHOW_2D_SKETCH = 34;             // eSHOWOption.Z_SHOW_2D_SKETCH

        /// <summary>上次识别的草图身份 Id，供「智能标注」判词引用；无草图时为 -1。</summary>
        private static int _lastProfileId = -1;
        private static readonly string SmartDimSketchCsvPath =
            Path.Combine(Path.GetTempPath(), "caxa3dButton_smartdim_sketch.csv");

        #region 原始 COM 互操作

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnCreateNewDoc(IntPtr self, int eType, int vbInteractive, int vbUseDefault,
            [MarshalAs(UnmanagedType.BStr)] string templateName, int vbVisible, out IntPtr ppDoc);

        // CreateBlock(Object varPt1, Object varPt2) → ZExtrudeFeature：两点(VARIANT double[3], 米)定长方体。
        // 调用口径与参考工程 Caxa3dcudeaddin 实机先例逐句一致（pt1=(0,0,0), pt2=(lx,ly,lz)）。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnCreateBlock(IntPtr self,
            [MarshalAs(UnmanagedType.Struct)] object pt1,
            [MarshalAs(UnmanagedType.Struct)] object pt2,
            out IntPtr ppFeature);

        // CreateCylinder(Double dRadius, Double dHeight, Object varAxisPt, Object varAxisVec) → ZExtrudeFeature：
        // 半径+高+轴点+轴向量（VARIANT double[3]，米）。TLB 2027 members.csv 槽20，与 CreateBlock(槽19) 同族图素命令。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnCreateCylinder(IntPtr self, double dRadius, double dHeight,
            [MarshalAs(UnmanagedType.Struct)] object axisPt,
            [MarshalAs(UnmanagedType.Struct)] object axisVec,
            out IntPtr ppFeature);

        // ---- 抽壳圆管通道取证（TLB 2027 members.csv；15:49 实机识别链全对、CreateShellFeature E_FAIL）----
        // IZPart.GetBody(Boolean vbForceLoad) → ZBody（槽8）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnPartGetBody(IntPtr self, short vbForceLoad, out IntPtr ppBody);

        // IZBody.GetFaceIds() → Object（面ID VARIANT 数组，槽8）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnBodyGetFaceIds(IntPtr self, [MarshalAs(UnmanagedType.Struct)] out object pvarFaceIds);

        // IZBodyFace.GetFaceBoundingBox(Int32 lFaceId) → Object（槽9；期望 6 个 Double 的 min/max 数组）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnBodyFaceGetBBox(IntPtr self, int lFaceId,
            [MarshalAs(UnmanagedType.Struct)] out object pvarBBox);

        // IZPartFeatureMgr.CreateShellFeature(Object varArrOpenFaceIds, Double dThickness) → ZShellFeature（槽12）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnCreateShell(IntPtr self,
            [MarshalAs(UnmanagedType.Struct)] object openFaceIds, double dThickness,
            out IntPtr ppFeature);

        // IZElement.get/set_Name(槽9/10)：首特征命名（用户 15:09 需求）。
        // 槽位先例：caxa3dBatchMaster（IZElement.set_Name=10 槽位直调）＋ LinkRenameV2 写后 Ordinal 回读口径。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnElementGetName(IntPtr self, [MarshalAs(UnmanagedType.BStr)] out string pbstrName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnElementSetName(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string pbstrName);

        // IZSizeBox.SetDisplayHandle(Boolean vbLength, vbWidth, vbHeight, vbBox)：
        // 四个 VARIANT_BOOL 对应属性页「包围盒→显示」的 长度操作柄/宽度操作柄/高度操作柄/包围盒 四个勾选。
        // TLB Boolean = VARIANT_BOOL（short，-1/0），沿用已证写法。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetDisplayHandle(IntPtr self, short vbLength, short vbWidth, short vbHeight, short vbBox);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetDisplayHandle(IntPtr self, out short pvbLength, out short pvbWidth, out short pvbHeight, out short pvbBox);

        // IZSizeBox 尺寸回读（校验用）：get_Length(7)、get_Width(9)、get_Height(11)。
        // TLB 2027 members.csv 实证；Double 单位与 CreateBlock 一致（米）。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetSizeBoxDim(IntPtr self, out double pdVal);

        // IZSizeBox.get_PermitResetSizeBox(槽26)：VARIANT_BOOL，false=禁止重置（参考工程实测口径：跳过）。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetSizeBoxBool(IntPtr self, out short pvbVal);

        // IZSizeBox.Reset(Boolean vbScaleAllParas)（槽30）：重置包围盒命令。
        // ★ 实机先例 ★：caxa3dminitool ElementsTool.ResetSizeBoxes——box.Reset(false) → scene.Update()
        // → 回读 L/W/H 非零有效。VB 参数 VARIANT_BOOL（short，-1/0）。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSizeBoxReset(IntPtr self, short vbScaleAllParas);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetPartType(IntPtr self, out int pPartType);

        private static IntPtr Slot(IntPtr pIface, int slot)
        {
            IntPtr vt = Marshal.ReadIntPtr(pIface);
            return Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
        }

        #endregion

        /// <summary>从 Addin 站点提供的 IZBaseApp 建立进程内会话；失败抛异常。</summary>
        public static CaxaSession ConnectFromSite(IZBaseApp siteApp)
        {
            if (siteApp == null)
                throw new InvalidOperationException("Addin 站点未提供 Application 对象。");

            object appObj = (object)siteApp;
            IntPtr pBase = IntPtr.Zero;

            IntPtr pUnk = Marshal.GetIUnknownForObject(appObj);
            int hr = ProbeCommon.QI(pUnk, IID_IZBaseApp, out pBase);
            Marshal.Release(pUnk);

            if (hr != 0 || pBase == IntPtr.Zero)
            {
                if (pBase != IntPtr.Zero) Marshal.Release(pBase);
                throw new InvalidOperationException(
                    string.Format("QueryInterface(IZBaseApp) 失败 hr=0x{0:X}", hr));
            }

            // 验证调用：确认服务器真实存活
            IntPtr pDoc;
            hr = ProbeCommon.CallGetObj(pBase, SlotActiveDoc, out pDoc);
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

        /// <summary>探测会话是否仍然有效（服务器真实响应）。</summary>
        public static bool Ping(CaxaSession session)
        {
            if (session == null || session.BaseApp == IntPtr.Zero) return false;
            try
            {
                IntPtr pDoc;
                int hr = ProbeCommon.CallGetObj(session.BaseApp, SlotActiveDoc, out pDoc);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>取当前活动场景文档；没有活动文档时新建一个场景设计文档。返回指针由调用方 Release。</summary>
        internal static IntPtr GetOrCreateSceneDoc(CaxaSession session)
        {
            IntPtr pDoc;
            int hr = ProbeCommon.CallGetObj(session.BaseApp, SlotActiveDoc, out pDoc);
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
        /// 图素基体（IntelliShape / 智能图素路线）：CreatePart 新建零件 → 图素命令 → Update →
        /// 首特征命名 → IZSizeBox 四勾选 → 包围盒重置 → GetPartType 回读。
        ///
        /// ★ 取证 ★：
        ///   长方体 = CreateBlock(槽19) 两点定长方体（用户 15:19 裁定：底部基准参考圆柱体——
        ///     底面矩形中心与原点重合，pt1=(-lx/2,-ly/2,0)、pt2=(+lx/2,+ly/2,+lz)，高沿 +Z）；
        ///   圆柱体 = CreateCylinder(槽20)（dRadius, dHeight, 轴点=(0,0,0), 轴向量=(0,0,1)，TLB 2027 members.csv）；
        ///   圆管   = 外圆柱（同圆柱口径）→ 内孔圆柱（r=外径/2−壁厚）转除料 Z_SUBTRACT（参考 caxa3dminitool
        ///     增料/除料命令，用户 15:52 裁定；CreateShellFeature 抽壳 15:49 实机 E_FAIL 已否决）。
        /// ★ 产物性质（已否决表 11-08 固化）★：图素路线天然产**创新零件**（掩码 0x10），只有 sizebox
        ///   驱动参数、无几何约束——这不是缺陷，是图素的本义；判据=零件落地+种类可回读。
        /// ★ 失败清理 ★：图素特征长在零件里，失败只 Remove 本次自建的零件，不碰用户已有元素。
        /// </summary>
        /// <param name="shapeName">「长方体」「圆柱体」「圆管」或「抽壳圆管」（同时用作首特征名）。</param>
        /// <param name="lx">长方体：长(m)；圆柱体：直径(m)；圆管/抽壳圆管：外径(m)。</param>
        /// <param name="ly">长方体：宽(m)；圆管/抽壳圆管：壁厚(m)；其余忽略。</param>
        /// <param name="lz">高(m)。</param>
        private static bool TryCreatePrimitivePart(IntPtr pScene, string shapeName, double lx, double ly, double lz, out string verdict)
        {
            verdict = null;
            IntPtr pPartRaw = IntPtr.Zero, pPart = IntPtr.Zero, pFm = IntPtr.Zero, pFeat = IntPtr.Zero;
            try
            {
                Diagnostics.Log("route: 图素方式（IntelliShape " + shapeName + "）");
                int maskBefore;

                // ---- 1) 新建零件（绝不往已有零件上追加）----
                int hr = ProbeCommon.CallGetObj(pScene, SlotCreatePart, out pPartRaw);
                Diagnostics.Log(string.Format("CreatePart hr={0} ptr=0x{1:X}", ProbeCommon.Hr(hr), pPartRaw.ToInt64()));
                if (hr != 0 || pPartRaw == IntPtr.Zero) return false;
                hr = ProbeCommon.QI(pPartRaw, IID_IZPart, out pPart);
                if (hr != 0 || pPart == IntPtr.Zero)
                {
                    Diagnostics.Log("QI(IZPart) failed " + ProbeCommon.Hr(hr));
                    RemoveCreatedGarbage(pPartRaw, IntPtr.Zero, false);
                    return false;
                }
                maskBefore = ReadPartTypeMask(pPart, "建模前");

                // ---- 2) 图素命令（IZPartFeatureMgr 槽19/20，单位米）----
                hr = ProbeCommon.QI(pPart, IID_IZPartFeatureMgr, out pFm);
                if (hr != 0 || pFm == IntPtr.Zero)
                {
                    Diagnostics.Log("QI(IZPartFeatureMgr) failed " + ProbeCommon.Hr(hr));
                    RemoveCreatedGarbage(pPart, IntPtr.Zero, false);
                    return false;
                }
                if (shapeName == "圆柱体" || shapeName == "圆管" || shapeName == "抽壳圆管")
                {
                    // 圆柱体/圆管/抽壳圆管外圆柱同口径：轴=+Z 过原点（底面中心与原点重合）
                    // 圆管/抽壳圆管时 lx=外径、lz=高；壁厚在 3.3 步用。
                    var fnCyl = ProbeCommon.Fn<FnCreateCylinder>(pFm, SlotCreateCylinder);
                    object axisPt = new double[] { 0.0, 0.0, 0.0 };
                    object axisVec = new double[] { 0.0, 0.0, 1.0 };   // 轴沿 +Z，与长方体朝向口径一致
                    hr = fnCyl(pFm, lx / 2.0, lz, axisPt, axisVec, out pFeat);
                    Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                        "CreateCylinder hr={0} ptr=0x{1:X}（r={2}m h={3}m，轴=+Z，{4}）",
                        ProbeCommon.Hr(hr), pFeat.ToInt64(), lx / 2.0, lz, shapeName));
                }
                else
                {
                    // 用户 15:19 裁定：长方体底部基准参考圆柱体——底面矩形中心与原点中心重合
                    //（原 pt1=(0,0,0) 对角点口径退役）；高仍沿 +Z。
                    object pt1 = new double[] { -lx / 2.0, -ly / 2.0, 0.0 };
                    object pt2 = new double[] { lx / 2.0, ly / 2.0, lz };
                    var fnBlock = ProbeCommon.Fn<FnCreateBlock>(pFm, SlotCreateBlock);
                    hr = fnBlock(pFm, pt1, pt2, out pFeat);
                    Diagnostics.Log(string.Format("CreateBlock hr={0} ptr=0x{1:X}（底面中心=原点，pt2=({2},{3},{4})m）",
                        ProbeCommon.Hr(hr), pFeat.ToInt64(), lx / 2.0, ly / 2.0, lz));
                }
                if (hr != 0 || pFeat == IntPtr.Zero)
                {
                    RemoveCreatedGarbage(pPart, IntPtr.Zero, false);
                    return false;
                }

                // ---- 3) Update（IZPart 槽7）让实体真落地 ----
                hr = ProbeCommon.Fn<ProbeCommon.FnVoid>(pPart, SlotPartUpdate)(pPart);
                Diagnostics.Log("IZPart.Update hr=" + ProbeCommon.Hr(hr));

                // ---- 3.3) 圆管成型（两种路线，用户 15:58 裁定：圆管按钮保留除料路线，另加「抽壳圆管」按钮）----
                //   圆管     = 内孔圆柱(r=外径/2−壁厚) 转除料 Z_SUBTRACT（参考 caxa3dminitool 增料/除料命令）
                //   抽壳圆管 = CreateShellFeature(槽12) 上下底开口抽壁厚（多形态重试，见 ShellTube）
                if (shapeName == "圆管")
                {
                    string tubeErr;
                    if (!CreateTubeInnerSubtract(pFm, pPart, lx, ly, lz, out tubeErr))
                    {
                        Diagnostics.Log("tube: 内孔除料失败（" + tubeErr + "）");
                        RemoveCreatedGarbage(pPart, IntPtr.Zero, false);
                        verdict = "圆管内孔除料失败：" + tubeErr;
                        return false;
                    }
                }
                else if (shapeName == "抽壳圆管")
                {
                    string shellErr;
                    if (!ShellTube(pFm, pPart, ly, out shellErr))
                    {
                        Diagnostics.Log("tube-shell: 抽壳失败（" + shellErr + "）");
                        RemoveCreatedGarbage(pPart, IntPtr.Zero, false);
                        verdict = "抽壳圆管抽壳失败：" + shellErr;
                        return false;
                    }
                }

                // ---- 3.4) 首特征命名（用户 15:09 需求：特征名=形状名）----
                string nameVerdict = RenameFeature(pFeat, shapeName);

                // ---- 3.5) 包围盒显示设置（用户 14:34 需求：按属性页截图勾选四项）----
                string sizeboxVerdict = EnableSizeBoxHandles(pFeat, pPart);

                // ---- 3.6) 包围盒更新（用户 14:53 裁定：Reset 重置命令路线）----
                // 圆管/抽壳圆管 sizebox 目标口径：L=W=外径、H=高（壁厚不是包围盒维度）
                bool isTube = shapeName == "圆管" || shapeName == "抽壳圆管";
                double sbW = isTube ? lx : ly;
                string updVerdict = UpdateSizeBox(pFeat, pPart, lx, sbW, lz);

                // ---- 4) 权威判据：回读零件种类 ----
                int maskAfter = ReadPartTypeMask(pPart, "建模后");
                verdict = PartTypeVerdict(maskAfter) + "；" + nameVerdict + "；" + sizeboxVerdict + "；" + updVerdict;
                Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                    "图素{0}回读：建模前掩码=0x{1:X} 建模后掩码=0x{2:X} ⇒ {3}",
                    shapeName, maskBefore, maskAfter, verdict));
                if (maskAfter < 0)
                {
                    verdict = shapeName + "已建立，但种类回读失败（第三态）";
                    RemoveCreatedGarbage(pPart, IntPtr.Zero, false);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("block route EX: " + ex);
                return false;
            }
            finally
            {
                if (pFeat != IntPtr.Zero) Marshal.Release(pFeat);
                if (pFm != IntPtr.Zero) Marshal.Release(pFm);
                if (pPart != IntPtr.Zero) Marshal.Release(pPart);
                if (pPartRaw != IntPtr.Zero) Marshal.Release(pPartRaw);
            }
        }

        /// <summary>
        /// 圆管内孔除料（用户 15:52 裁定的参考工程路线，实机先例 = caxa3dminitool FeatureTool.ToggleSelected）：
        ///   ① CreateCylinder(槽20) 建内孔圆柱（半径 = 外径/2 − 壁厚，即直径=外径−2×壁厚，同轴同高）；
        ///   ② RCW 直写 IZFeature.OperationType = Z_SUBTRACT（除料），写后立即回读校验（参考工程口径）；
        ///   ③ IZPart.Update 重建零件，除料反映到几何 ⇒ 成管。
        /// ★ 抽壳路线已否决 ★：CreateShellFeature(槽12) 槽直调 15:49 实机 hr=0x80004005（识别链全对仍 E_FAIL）。
        /// ★ 槽位取证 ★：IZFeature.get/set_OperationType = 槽9/10（TLB 2027 members.csv；RCW 属性直写与
        ///   参考工程逐句一致，参考工程与本工程共用同一 interop.ICApiIronCAD.dll）。
        /// 判据 = 内孔圆柱 hr=0 + OperationType 回读=Z_SUBTRACT + Update hr=0；任一失败返回 false（调用方清理零件）。
        /// </summary>
        /// <param name="outerDia">外径（米）。</param>
        /// <param name="wall">壁厚（米）；调用方已校验 &lt; 外径/2。</param>
        /// <param name="h">高（米）。</param>
        private static bool CreateTubeInnerSubtract(IntPtr pFm, IntPtr pPart, double outerDia, double wall, double h, out string err)
        {
            err = null;
            IntPtr pFeatInner = IntPtr.Zero;
            try
            {
                // 1) 内孔圆柱（r = R_out − wall，同轴 +Z、同高）
                double rInner = outerDia / 2.0 - wall;
                var fnCyl = ProbeCommon.Fn<FnCreateCylinder>(pFm, SlotCreateCylinder);
                object axisPt = new double[] { 0.0, 0.0, 0.0 };
                object axisVec = new double[] { 0.0, 0.0, 1.0 };
                int hr = fnCyl(pFm, rInner, h, axisPt, axisVec, out pFeatInner);
                Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                    "tube: CreateCylinder(内孔) hr={0} ptr=0x{1:X}（r={2}m =外径/2−壁厚，h={3}m）",
                    ProbeCommon.Hr(hr), pFeatInner.ToInt64(), rInner, h));
                if (hr != 0 || pFeatInner == IntPtr.Zero) { err = "内孔 CreateCylinder 失败"; return false; }

                // 2) 转除料：IZFeature.OperationType 写 Z_SUBTRACT，写后回读校验
                object innerObj = Marshal.GetObjectForIUnknown(pFeatInner);
                try
                {
                    var featIn = innerObj as IZFeature;
                    if (featIn == null) { err = "内孔特征不支持 IZFeature 表面（RCW as 失败）"; return false; }
                    eZOperationType before = featIn.OperationType;
                    featIn.OperationType = eZOperationType.Z_SUBTRACT;
                    eZOperationType wrote = featIn.OperationType;
                    Diagnostics.Log(string.Format("tube: OperationType {0} → Z_SUBTRACT，回读={1}（{2}）",
                        before, wrote, wrote == eZOperationType.Z_SUBTRACT ? "一致" : "不一致"));
                    if (wrote != eZOperationType.Z_SUBTRACT)
                    {
                        err = "OperationType 写后回读不一致（回读=" + wrote + "）";
                        return false;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(innerObj);
                }

                // 3) 重建零件让除料反映到几何（参考工程口径：切换后必须重建）
                hr = ProbeCommon.Fn<ProbeCommon.FnVoid>(pPart, SlotPartUpdate)(pPart);
                Diagnostics.Log("tube: 内孔除料后 IZPart.Update hr=" + ProbeCommon.Hr(hr));
                if (hr != 0) { err = "除料后 Update 失败"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                err = "内孔除料异常 " + ex.Message;
                return false;
            }
            finally
            {
                if (pFeatInner != IntPtr.Zero) Marshal.Release(pFeatInner);
            }
        }

        /// <summary>
        /// 抽壳圆管（「抽壳圆管」按钮路线，用户 15:58 需求）：
        ///   IZPart.GetBody(槽8) → IZBody；QI IZBodyFace（面辅助表面）；
        ///   IZBody.GetFaceIds(槽8) 拿全部面ID → IZBodyFace.GetFaceBoundingBox(槽9) 逐面取包围盒，
        ///   Z 向厚度≈0 的两个面即上下底（开口面）；CreateShellFeature(槽12, 开口面ID数组, 壁厚) 抽壳成管。
        /// ★ 15:49 实机教训 ★：识别链全对（开口面=[2,3] 精确命中）但 CreateShellFeature 以 double[] 形态
        ///   投递 hr=0x80004005 ⇒ 本轮加**多形态重试**：int[] → double[]（VARIANT 数组元素类型可能是 VT_I4）。
        /// 判据 = 恰好 2 个开口面 且 某形态 CreateShellFeature hr=0；全部形态失败记失败（调用方清理零件）。
        /// </summary>
        /// <param name="wall">壁厚（米）。</param>
        private static bool ShellTube(IntPtr pFm, IntPtr pPart, double wall, out string err)
        {
            err = null;
            IntPtr pBody = IntPtr.Zero, pBf = IntPtr.Zero, pShell = IntPtr.Zero;
            try
            {
                // 1) 零件体
                int hr = ProbeCommon.Fn<FnPartGetBody>(pPart, SlotPartGetBody)(pPart, 0, out pBody);
                Diagnostics.Log(string.Format("tube-shell: IZPart.GetBody hr={0} ptr=0x{1:X}", ProbeCommon.Hr(hr), pBody.ToInt64()));
                if (hr != 0 || pBody == IntPtr.Zero) { err = "GetBody 失败"; return false; }

                // 2) 面辅助表面
                hr = ProbeCommon.QI(pBody, IID_IZBodyFace, out pBf);
                Diagnostics.Log("tube-shell: QI(IZBodyFace) hr=" + ProbeCommon.Hr(hr));
                if (hr != 0 || pBf == IntPtr.Zero) { err = "QI(IZBodyFace) 失败"; return false; }

                // 3) 全部面ID
                object varIds;
                hr = ProbeCommon.Fn<FnBodyGetFaceIds>(pBody, SlotBodyGetFaceIds)(pBody, out varIds);
                Diagnostics.Log("tube-shell: IZBody.GetFaceIds hr=" + ProbeCommon.Hr(hr) +
                    (varIds == null ? "" : "（返回类型=" + varIds.GetType().Name + "）"));
                if (hr != 0) { err = "GetFaceIds 失败"; return false; }
                double[] ids = VariantToDoubles(varIds);
                if (ids == null || ids.Length == 0) { err = "面ID数组解析失败（类型=" + (varIds == null ? "<null>" : varIds.GetType().Name) + "）"; return false; }
                Diagnostics.Log("tube-shell: 面数=" + ids.Length);

                // 4) 逐面取包围盒，挑上下底（Z 向厚度≈0）
                var capIds = new List<int>();
                for (int i = 0; i < ids.Length; i++)
                {
                    int fid = (int)Math.Round(ids[i]);
                    object varBb;
                    int hrBb = ProbeCommon.Fn<FnBodyFaceGetBBox>(pBf, SlotBodyFaceGetBBox)(pBf, fid, out varBb);
                    if (hrBb != 0)
                    {
                        Diagnostics.Log(string.Format("tube-shell: 面{0} GetFaceBoundingBox hr={1}（跳过）", fid, ProbeCommon.Hr(hrBb)));
                        continue;
                    }
                    double[] bb = VariantToDoubles(varBb);
                    if (bb == null || bb.Length < 6)
                    {
                        Diagnostics.Log(string.Format("tube-shell: 面{0} 包围盒解析失败（{1} 项，跳过）",
                            fid, bb == null ? "<null>" : bb.Length.ToString()));
                        continue;
                    }
                    double zSpan = Math.Abs(bb[5] - bb[2]);
                    Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                        "tube-shell: 面{0} bbox=({1},{2},{3})~({4},{5},{6}) zSpan={7:E2}",
                        fid, bb[0], bb[1], bb[2], bb[3], bb[4], bb[5], zSpan));
                    if (zSpan < 1e-9) capIds.Add(fid);   // 上下底平面
                }

                if (capIds.Count != 2)
                {
                    err = "开口面识别=" + capIds.Count + "（期望 2 个上下底平面）";
                    return false;
                }

                // 5) 抽壳：多形态重试（int[] → double[]），逐形态留痕
                var fnShell = ProbeCommon.Fn<FnCreateShell>(pFm, SlotCreateShell);
                int[] idsInt = capIds.ToArray();
                var idsDbl = new double[idsInt.Length];
                for (int k = 0; k < idsInt.Length; k++) idsDbl[k] = idsInt[k];
                string[] formNames = { "int[]", "double[]" };
                object[] formArrays = { (object)idsInt, (object)idsDbl };
                int lastHr = -1;
                for (int a = 0; a < formArrays.Length; a++)
                {
                    hr = fnShell(pFm, formArrays[a], wall, out pShell);
                    Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                        "tube-shell: CreateShellFeature(开口面=[{0},{1}] 形态={2}, t={3}m) hr={4} ptr=0x{5:X}",
                        idsInt[0], idsInt[1], formNames[a], wall, ProbeCommon.Hr(hr), pShell.ToInt64()));
                    lastHr = hr;
                    if (hr == 0 && pShell != IntPtr.Zero) return true;
                    if (pShell != IntPtr.Zero) { Marshal.Release(pShell); pShell = IntPtr.Zero; }
                }
                err = "CreateShellFeature 全形态失败（末次 hr=" + ProbeCommon.Hr(lastHr) + "）";
                return false;
            }
            catch (Exception ex)
            {
                err = "抽壳异常 " + ex.Message;
                return false;
            }
            finally
            {
                if (pShell != IntPtr.Zero) Marshal.Release(pShell);
                if (pBf != IntPtr.Zero) Marshal.Release(pBf);
                if (pBody != IntPtr.Zero) Marshal.Release(pBody);
            }
        }

        /// <summary>VARIANT 数组 → double[]（兼容 double[]/int[]/short[]/object[]），失败返回 null。</summary>
        private static double[] VariantToDoubles(object o)
        {
            try
            {
                if (o is double[] d) return d;
                if (o is int[] i) return Array.ConvertAll(i, x => (double)x);
                if (o is short[] s) return Array.ConvertAll(s, x => (double)x);
                if (o is float[] f) return Array.ConvertAll(f, x => (double)x);
                var oa = o as object[];
                if (oa != null)
                {
                    var r = new double[oa.Length];
                    for (int k = 0; k < oa.Length; k++) r[k] = Convert.ToDouble(oa[k], CultureInfo.InvariantCulture);
                    return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 首特征命名（IZElement.set_Name 槽10 → get_Name 槽9 Ordinal 回读）。
        /// 槽位先例：caxa3dBatchMaster（set_Name=10 槽位直调）；判据=回读与新名 Ordinal 相等，失败只记第三态不影响建模成功。
        /// </summary>
        private static string RenameFeature(IntPtr pFeat, string newName)
        {
            IntPtr pElem;
            int hr = ProbeCommon.QI(pFeat, IID_IZElement, out pElem);
            if (hr != 0 || pElem == IntPtr.Zero)
                return "特征命名：QI(IZElement) 失败（第三态）" + ProbeCommon.Hr(hr);
            try
            {
                int hrSet = ProbeCommon.Fn<FnElementSetName>(pElem, SlotElementSetName)(pElem, newName);
                string cur = null;
                int hrGet = ProbeCommon.Fn<FnElementGetName>(pElem, SlotElementGetName)(pElem, out cur);
                Diagnostics.Log(string.Format("rename: set_Name(\"{0}\") hr={1}，回读=\"{2}\" hr={3}",
                    newName, ProbeCommon.Hr(hrSet), cur ?? "<空>", ProbeCommon.Hr(hrGet)));
                if (hrSet != 0)
                    return "特征命名：set_Name 失败（第三态）" + ProbeCommon.Hr(hrSet);
                bool ok = hrGet == 0 && string.Equals(cur, newName, StringComparison.Ordinal);
                return ok
                    ? "首特征已命名「" + newName + "」（回读一致）"
                    : "特征命名：写入后回读=\"" + (cur ?? "<空>") + "\"（第三态）";
            }
            finally
            {
                Marshal.Release(pElem);
            }
        }

        /// <summary>
        /// 路线失败时清理本次建出的场景垃圾（创新零件/孤立轮廓），防多次点击累积。
        /// Remove 只清 API 自建元素，绝不碰用户已有节点；任何失败只记日志。
        /// </summary>
        private static void RemoveCreatedGarbage(IntPtr pPart, IntPtr pProfile, bool profileAbsorbed)
        {
            try
            {
                if (pPart != IntPtr.Zero)
                {
                    IntPtr pElem;
                    if (ProbeCommon.QI(pPart, IID_IZElement, out pElem) == 0 && pElem != IntPtr.Zero)
                    {
                        int hr = ProbeCommon.Fn<ProbeCommon.FnVoid>(pElem, SlotElementRemove)(pElem);
                        Diagnostics.Log("cleanup: IZElement.Remove(part) hr=" + ProbeCommon.Hr(hr));
                        Marshal.Release(pElem);
                    }
                }
                // ABSORB 拉伸后轮廓已被零件吸收；未吸收时（早期失败）单独清轮廓
                if (!profileAbsorbed && pProfile != IntPtr.Zero)
                {
                    IntPtr pElem;
                    if (ProbeCommon.QI(pProfile, IID_IZElement, out pElem) == 0 && pElem != IntPtr.Zero)
                    {
                        int hr = ProbeCommon.Fn<ProbeCommon.FnVoid>(pElem, SlotElementRemove)(pElem);
                        Diagnostics.Log("cleanup: IZElement.Remove(profile) hr=" + ProbeCommon.Hr(hr));
                        Marshal.Release(pElem);
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("cleanup EX: " + ex.Message);
            }
        }

        /// <summary>读 GetPartType(槽51) 位掩码；读不到返回 -1（第三态，绝不折算 0）。</summary>
        private static int ReadPartTypeMask(IntPtr pPart, string when)
        {
            try
            {
                int mask;
                int hr = ProbeCommon.Fn<FnGetPartType>(pPart, SlotGetPartType)(pPart, out mask);
                Diagnostics.Log(string.Format("GetPartType({0}) hr={1} mask=0x{2:X}", when, ProbeCommon.Hr(hr), mask));
                return hr == 0 ? mask : -1;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("GetPartType(" + when + ") EX " + ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// 位掩码 → 中文种类。判据双语资源配对实证（res_scan_menus_all_1102.csv）：
        ///   3ish31.dll cmd37228 "Drop As Structured Part" ↔ 3ishLg.dll "作为工程模式零件" ⇒ 32
        ///   3ish31.dll cmd37223 "Drop As Innovative Part" ↔ 3ishLg.dll "作为创新零件拖放" ⇒ 16
        /// 内核（ACIS/Parasolid）与零件模式是**正交**两维（HKCU …\Model 下 Default Modeling Kernel
        /// 与 Default Part Type 是两个独立键），所以这里只认 GetPartType，不拿 KernelType 冒充。
        /// </summary>
        private static string PartTypeVerdict(int mask)
        {
            if (mask < 0) return "种类读不到";
            bool structured = (mask & Z_PART_STRUCTURED_PART) != 0;
            bool innovation = (mask & Z_PART_INNOVATION_PART) != 0;
            string kind = structured && !innovation ? "工程零件"
                        : innovation && !structured ? "创新零件"
                        : structured && innovation ? "工程+创新（两位都置）"
                        : "未判定（无工程/创新位）";
            return kind + "（掩码0x" + mask.ToString("X") + "）";
        }

        /// <summary>
        /// 在图素元素树上找 IZSizeBox，把「长度操作柄/宽度操作柄/高度操作柄/包围盒」四个显示勾选全置 true
        /// （用户 14:34 需求，对应「拉伸特征」属性页→包围盒→显示 组的四项）。
        /// 查找顺序：特征自身 → 零件 → 特征子树（IZElement.GetChildren 递归，深度≤3、每层≤16 节点）。
        /// 判据 = SetDisplayHandle(槽24) 写入后 GetDisplayHandle(槽25) 回读全 true；读不到/写不进记第三态，不折算成功。
        /// </summary>
        private static string EnableSizeBoxHandles(IntPtr pFeat, IntPtr pPart)
        {
            const string tag = "sizebox";
            try
            {
                IntPtr pSb = LocateSizeBox(pFeat, pPart);
                if (pSb == IntPtr.Zero)
                {
                    string msg = "包围盒显示：特征/零件元素树上都没找到 IZSizeBox（第三态，未设置）";
                    Diagnostics.Log(tag + ": " + msg);
                    return msg;
                }

                try
                {
                    var fnSet = ProbeCommon.Fn<FnSetDisplayHandle>(pSb, SlotSizeBoxSetHandle);
                    int hrSet = fnSet(pSb, -1, -1, -1, -1);   // VARIANT_BOOL true = -1
                    Diagnostics.Log(string.Format("{0}: SetDisplayHandle(T,T,T,T) hr={1}", tag, ProbeCommon.Hr(hrSet)));

                    short l, w, h, b;
                    var fnGet = ProbeCommon.Fn<FnGetDisplayHandle>(pSb, SlotSizeBoxGetHandle);
                    int hrGet = fnGet(pSb, out l, out w, out h, out b);
                    Diagnostics.Log(string.Format("{0}: GetDisplayHandle hr={1} 长={2} 宽={3} 高={4} 盒={5}",
                        tag, ProbeCommon.Hr(hrGet), l, w, h, b));

                    if (hrSet != 0 || hrGet != 0)
                        return "包围盒显示：Set/GetDisplayHandle 调用失败（第三态）" + ProbeCommon.Hr(hrSet != 0 ? hrSet : hrGet);
                    bool allOn = l != 0 && w != 0 && h != 0 && b != 0;
                    return allOn
                        ? "包围盒显示已勾选：长度操作柄/宽度操作柄/高度操作柄/包围盒（回读全 true）"
                        : string.Format("包围盒显示：写入后回读未全 true（长={0} 宽={1} 高={2} 盒={3}，第三态）", l, w, h, b);
                }
                finally
                {
                    Marshal.Release(pSb);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log(tag + ": EX " + ex.Message);
                return "包围盒显示：设置异常（第三态）" + ex.Message;
            }
        }

        /// <summary>特征树→零件树找 IZSizeBox；返回带引用计数的裸指针（调用方 Release），找不到返回 Zero。</summary>
        private static IntPtr LocateSizeBox(IntPtr pFeat, IntPtr pPart)
        {
            object featObj = Marshal.GetObjectForIUnknown(pFeat);
            IntPtr pSb = FindSizeBox(featObj, 0);
            if (pSb == IntPtr.Zero && pPart != IntPtr.Zero)
            {
                object partObj = Marshal.GetObjectForIUnknown(pPart);
                pSb = FindSizeBox(partObj, 0);
            }
            return pSb;
        }

        /// <summary>
        /// 包围盒更新 = 重置包围盒命令（用户 14:53 裁定：直接用参考工程 caxa3dminitool 的 Reset 路线）。
        /// ★ 实机先例 ★：caxa3dminitool ElementsTool.ResetSizeBoxes（对应 ICM OnDragDropResetSizeBox）：
        ///   PermitResetSizeBox 预检（false 跳过）→ box.Reset(false) → Update → 回读 L/W/H 非零有效。
        /// ★ 槽位取证 ★：get_PermitResetSizeBox(26)、Reset(30)（TLB 2027 members.csv）。
        /// 判据 = Reset 无异常 + IZPart.Update 后回读三向非零（参考工程口径），并留痕与目标值的偏差。
        /// </summary>
        private static string UpdateSizeBox(IntPtr pFeat, IntPtr pPart, double lx, double ly, double lz)
        {
            const string tag = "sizebox-upd";
            IntPtr pSb = LocateSizeBox(pFeat, pPart);
            if (pSb == IntPtr.Zero)
            {
                string msg = "包围盒更新：特征/零件元素树上都没找到 IZSizeBox（第三态，未重置）";
                Diagnostics.Log(tag + ": " + msg);
                return msg;
            }
            try
            {
                var fnGetDim = ProbeCommon.Fn<FnGetSizeBoxDim>(pSb, SlotSizeBoxGetLength);   // 槽7 回读示例
                var fnGetPermit = ProbeCommon.Fn<FnGetSizeBoxBool>(pSb, SlotSizeBoxGetPermit);
                var fnReset = ProbeCommon.Fn<FnSizeBoxReset>(pSb, SlotSizeBoxReset);

                // 1) 预检：PermitResetSizeBox=false 时 Reset 不做（参考工程实测口径：跳过）
                short permit;
                int hrPermit = fnGetPermit(pSb, out permit);
                Diagnostics.Log(tag + ": get_PermitResetSizeBox hr=" + ProbeCommon.Hr(hrPermit) +
                    " permit=" + permit);
                if (hrPermit == 0 && permit == 0)
                    return "包围盒更新：PermitResetSizeBox=false，禁止重置（第三态）";

                // 2) 重置前回读（留痕）
                double cL = 0, cW = 0, cH = 0;
                int hrBefore = fnGetDim(pSb, out cL);
                if (hrBefore == 0) hrBefore = ProbeCommon.Fn<FnGetSizeBoxDim>(pSb, SlotSizeBoxGetWidth)(pSb, out cW);
                if (hrBefore == 0) hrBefore = ProbeCommon.Fn<FnGetSizeBoxDim>(pSb, SlotSizeBoxGetHeight)(pSb, out cH);
                Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                    "{0}: 重置前 长={1} 宽={2} 高={3} m（hr={4}）", tag, cL, cW, cH, ProbeCommon.Hr(hrBefore)));

                // 3) 重置包围盒命令（Reset(false)，与参考工程逐句一致）
                int hrReset = fnReset(pSb, 0);
                Diagnostics.Log(tag + ": Reset(false) hr=" + ProbeCommon.Hr(hrReset));
                if (hrReset != 0)
                    return "包围盒更新：Reset 调用失败（第三态）" + ProbeCommon.Hr(hrReset);

                // 4) 零件 Update 让重置落地
                int hrUpd = ProbeCommon.Fn<ProbeCommon.FnVoid>(pPart, SlotPartUpdate)(pPart);
                Diagnostics.Log(tag + ": IZPart.Update hr=" + ProbeCommon.Hr(hrUpd));

                // 5) 权威判定：回读三向非零有效（参考工程口径），并记录与目标偏差
                double nL = 0, nW = 0, nH = 0;
                int hrGet = ProbeCommon.Fn<FnGetSizeBoxDim>(pSb, SlotSizeBoxGetLength)(pSb, out nL);
                if (hrGet == 0) hrGet = ProbeCommon.Fn<FnGetSizeBoxDim>(pSb, SlotSizeBoxGetWidth)(pSb, out nW);
                if (hrGet == 0) hrGet = ProbeCommon.Fn<FnGetSizeBoxDim>(pSb, SlotSizeBoxGetHeight)(pSb, out nH);
                Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                    "{0}: 重置后 长={1} 宽={2} 高={3} m（hr={4}，目标 {5}×{6}×{7}）",
                    tag, nL, nW, nH, ProbeCommon.Hr(hrGet), lx, ly, lz));

                if (hrGet != 0 || hrUpd != 0)
                    return "包围盒更新：重置后回读/Update 失败（第三态）" + ProbeCommon.Hr(hrGet != 0 ? hrGet : hrUpd);
                bool nonzero = Math.Abs(nL) > 1e-9 && Math.Abs(nW) > 1e-9 && Math.Abs(nH) > 1e-9;
                if (!nonzero)
                    return string.Format(CultureInfo.InvariantCulture,
                        "包围盒更新：重置后回读存在零值（长={0} 宽={1} 高={2}，第三态）", nL, nW, nH);
                bool match = Math.Abs(nL - lx) < 1e-9 && Math.Abs(nW - ly) < 1e-9 && Math.Abs(nH - lz) < 1e-9;
                return match
                    ? "包围盒已重置并更新（回读与目标一致）"
                    : string.Format(CultureInfo.InvariantCulture,
                        "包围盒已重置（回读 {0:F4}×{1:F4}×{2:F4} m，与目标有偏差，第三态）", nL, nW, nH);
            }
            catch (Exception ex)
            {
                Diagnostics.Log(tag + ": EX " + ex.Message);
                return "包围盒更新：设置异常（第三态）" + ex.Message;
            }
            finally
            {
                Marshal.Release(pSb);
            }
        }

        /// <summary>从元素树找第一个能 QI 出 IZSizeBox 的节点；返回带引用计数的裸指针（调用方 Release），找不到返回 Zero。</summary>
        private static IntPtr FindSizeBox(object node, int depth)
        {
            if (node == null || depth > 3) return IntPtr.Zero;
            IntPtr pUnk = Marshal.GetIUnknownForObject(node);
            IntPtr pSb;
            int hr = ProbeCommon.QI(pUnk, IID_IZSizeBox, out pSb);
            Marshal.Release(pUnk);
            if (hr == 0 && pSb != IntPtr.Zero) return pSb;

            var el = node as IZElement;
            if (el == null) return IntPtr.Zero;
            object[] kids = null;
            try { kids = el.GetChildren() as object[]; } catch (Exception ex) { Diagnostics.Log("sizebox: GetChildren FAIL " + ProbeCommon.Hr(ex)); }
            if (kids == null) return IntPtr.Zero;

            int n = 0;
            foreach (object k in kids)
            {
                if (k == null) continue;
                if (++n > 16) break;   // 预算护栏，防异常大子树
                IntPtr found = FindSizeBox(k, depth + 1);
                if (found != IntPtr.Zero) return found;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 在当前设计环境建立长方体（图素路线，底部基准参考圆柱体：底面矩形中心与原点重合，高沿 +Z）：
        /// CreatePart 新建零件 → IZPartFeatureMgr.CreateBlock(槽19) 两点定长方体 → Update →
        /// 首特征命名「长方体」→ IZSizeBox.SetDisplayHandle 四勾选 → Reset 重置包围盒 → GetPartType(51) 回读出中文判词。
        /// 失败抛异常，提示把 trace 回传。
        /// ★ 种类口径 ★：图素路线天然产创新零件（0x10，11-08 实机固化，用户 12:41 裁定接受）。
        /// </summary>
        public static string CreateBoxAtOrigin(CaxaSession session, double lxMm, double lyMm, double lzMm)
        {
            Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                "CreatePrimitive[长方体]: L(X)={0} W(Y)={1} H(Z)={2} mm", lxMm, lyMm, lzMm));
            return RunPrimitiveRoute(session, "长方体",
                lxMm * MmToMeter, lyMm * MmToMeter, lzMm * MmToMeter);
        }

        /// <summary>
        /// 在当前设计环境原点 (0,0,0) 建立圆柱体（图素路线，参考长方体功能）：
        /// CreatePart 新建零件 → IZPartFeatureMgr.CreateCylinder(槽20)（直径/高，轴=+Z 过原点）→
        /// Update → 首特征命名「圆柱体」→ IZSizeBox 四勾选 → Reset 重置包围盒 → GetPartType 回读。
        /// </summary>
        public static string CreateCylinderAtOrigin(CaxaSession session, double diaMm, double hMm)
        {
            Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                "CreatePrimitive[圆柱体]: 直径={0} 高={1} mm", diaMm, hMm));
            return RunPrimitiveRoute(session, "圆柱体",
                diaMm * MmToMeter, diaMm * MmToMeter, hMm * MmToMeter);
        }

        /// <summary>
        /// 在当前设计环境原点 (0,0,0) 建立圆管（图素路线，用户 15:41 需求）：
        /// CreatePart 新建零件 → CreateCylinder(槽20) 外圆柱（轴=+Z 过原点）→ Update →
        /// IZPart.GetBody → GetFaceIds 找上下底两平面 → CreateShellFeature(槽12) 抽壁厚 →
        /// 首特征命名「圆管」→ IZSizeBox 四勾选 → Reset 重置包围盒 → GetPartType 回读。
        /// </summary>
        /// <param name="outerDiaMm">外径（mm）。</param>
        /// <param name="wallMm">壁厚（mm）；调用方已校验 0 &lt; 壁厚 &lt; 外径/2。</param>
        public static string CreateTubeAtOrigin(CaxaSession session, double outerDiaMm, double wallMm, double hMm)
        {
            Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                "CreatePrimitive[圆管]: 外径={0} 壁厚={1} 高={2} mm", outerDiaMm, wallMm, hMm));
            // lx=外径、ly=壁厚、lz=高（TryCreatePrimitivePart 内圆管分支的参数口径）
            return RunPrimitiveRoute(session, "圆管",
                outerDiaMm * MmToMeter, wallMm * MmToMeter, hMm * MmToMeter);
        }

        /// <summary>
        /// 在当前设计环境原点 (0,0,0) 建立「抽壳圆管」（抽壳路线，用户 15:58 需求）：
        /// CreatePart → CreateCylinder(槽20) 外圆柱 → Update → GetBody/GetFaceIds 找上下底开口面 →
        /// CreateShellFeature(槽12) 抽壁厚（int[]/double[] 多形态重试）→ 首特征命名「抽壳圆管」→
        /// IZSizeBox 四勾选 → Reset 重置包围盒 → GetPartType 回读。
        /// ★ 15:49 实机：该命令以 double[] 形态直调 hr=0x80004005，本轮加 int[] 形态重试。
        /// </summary>
        public static string CreateTubeShellAtOrigin(CaxaSession session, double outerDiaMm, double wallMm, double hMm)
        {
            Diagnostics.Log(string.Format(CultureInfo.InvariantCulture,
                "CreatePrimitive[抽壳圆管]: 外径={0} 壁厚={1} 高={2} mm", outerDiaMm, wallMm, hMm));
            return RunPrimitiveRoute(session, "抽壳圆管",
                outerDiaMm * MmToMeter, wallMm * MmToMeter, hMm * MmToMeter);
        }

        /// <summary>两条图素路线共用壳：建场景文档 → TryCreatePrimitivePart → 失败抛异常。</summary>
        private static string RunPrimitiveRoute(CaxaSession session, string shapeName, double lx, double ly, double lz)
        {
            IntPtr pScene = GetOrCreateSceneDoc(session);
            string verdict;
            try
            {
                if (TryCreatePrimitivePart(pScene, shapeName, lx, ly, lz, out verdict))
                    return verdict;
                Diagnostics.Log(shapeName + " route 失败（" + (verdict ?? "<无判词>") + "）");
            }
            finally
            {
                Marshal.Release(pScene);
            }

            throw new InvalidOperationException(
                "无法建立" + shapeName + "。CreatePart + 图素命令未能产出可回读种类的零件。" +
                "请把 %TEMP%\\caxa3dButton_trace.log 回传继续分析。");
        }

        /// <summary>
        /// 草图智能标注（Phase 1，显示 + 读取）。
        /// ★ 能力边界（2027 TLB 实证）★：TLB 没有「创建草图尺寸/智能标注」的写接口
        ///   （IZShapeRelation 只有 GetValue / GetSmartDimPrefixAndPostfix / SetSmartDimPrefixAndPostfix，
        ///    IZRelationMgr 只有两个 getter，IZProfile 56 成员零尺寸）。所以本方法只做两件可验证的事：
        ///   ① IZSceneDocProperty.SetShowFlag 打开智能标注/尺寸显示，逐 flag 用 IsShowFlagOn 回读；
        ///   ② 枚举场景 RelationMgr ＋ 元素树上的 IZShapeRelation，把 Z_RelationDimension(1) 落 CSV。
        /// 判据：回读 true 才算显示成功；关系计数为 0 与 RelationMgr 读不到分成两句话，不折算成「0 条成功」。
        /// </summary>
        public static string AnnotateLastSketch(CaxaSession session)
        {
            const string tag = "annotate";
            Action<string> log = msg => Diagnostics.Log(msg);

            IZBaseApp app = session == null ? null : session.AppObject as IZBaseApp;
            string err;
            IZSceneDoc scene = ProbeCommon.GetScene(app, out err);
            if (scene == null)
            {
                log(tag + ": " + err);
                return err;
            }

            log(tag + ": ==== 草图智能标注：显示开关 + 关系读取（TLB 无创建接口）====");
            string flagVerdict = ToggleSmartDimFlags(scene, log);
            log(tag + ": " + flagVerdict);

            var rows = new List<string[]>();
            int relTotal = 0, dimTotal = 0, sceneCount = -1;

            IZRelationMgr rm = null;
            try { rm = scene.RelationMgr; }   // IZSceneDoc.get_RelationMgr 槽106
            catch (Exception ex) { log(tag + ": RelationMgr FAIL " + ProbeCommon.Hr(ex)); }
            if (rm != null)
            {
                try { sceneCount = rm.GetShapeRelationsCount(); }   // 槽8
                catch (Exception ex) { log(tag + ": GetShapeRelationsCount FAIL " + ProbeCommon.Hr(ex)); }
                log(tag + ": 场景 RelationMgr Count=" + ProbeCommon.Int(sceneCount));
                ReadRelations(rm, "场景RelationMgr", rows, log, ref relTotal, ref dimTotal);
            }

            // 草图关系可能挂在轮廓/零件层（实机已证场景层可恒 0）⇒ 元素树再扫一遍
            int truncated, profileNodes = 0;
            List<TreeNode> nodes = ProbeCommon.EnumerateTree(scene, tag, log, out truncated);
            if (truncated > 0) log(tag + ": 元素树截断 " + truncated + " 处（预算上限，不等于没有）");
            foreach (TreeNode n in nodes)
            {
                if (n.Type != null && n.Type.IndexOf("PROFILE", StringComparison.OrdinalIgnoreCase) >= 0) profileNodes++;
                var nrm = n.Raw as IZRelationMgr;
                if (nrm == null) continue;
                int count = -1;
                try { count = nrm.GetShapeRelationsCount(); }
                catch (Exception ex) { log(tag + ": " + n.Indent + "Count FAIL " + ProbeCommon.Hr(ex)); continue; }
                if (count <= 0) continue;
                log(tag + ": " + n.Indent + "节点 Id=" + n.Id + " Type=" + n.Type + " Count=" + ProbeCommon.Int(count));
                ReadRelations(nrm, "元素树 Id=" + n.Id + " " + n.Type, rows, log, ref relTotal, ref dimTotal);
            }

            try
            {
                ProbeCommon.WriteCsv(SmartDimSketchCsvPath,
                    "Source,RawObject," + ShapeRelationReader.CsvHeader, rows);
            }
            catch (Exception ex) { log(tag + ": 写 CSV 失败 " + ex.Message); }

            string summary;
            if (dimTotal > 0)
            {
                summary = string.Format(CultureInfo.InvariantCulture,
                    "智能标注：显示开关已处理；找到 {0} 条 Z_RelationDimension 草图尺寸（形状关系共 {1} 条）；CSV={2}",
                    dimTotal, relTotal, SmartDimSketchCsvPath);
            }
            else if (sceneCount < 0 && relTotal == 0)
            {
                summary = "智能标注：显示开关已处理，但 RelationMgr 读不到（第三态，不折算成 0 条）；CSV=" + SmartDimSketchCsvPath;
            }
            else
            {
                summary = string.Format(CultureInfo.InvariantCulture,
                    "智能标注：显示开关已处理，但未找到 Z_RelationDimension 草图尺寸（形状关系共 {0} 条）；" +
                    "可标注草图元素 {1} 个（本次草图 Id={2}，ABSORB 拉伸后可能已被吸收）；CSV={3}",
                    relTotal, profileNodes, _lastProfileId, SmartDimSketchCsvPath);
            }
            log(tag + ": " + summary);
            return summary;
        }

        /// <summary>按 eSHOWOption 逐 flag 打开显示并回读；回读 false 记第三态，不宣称成功。</summary>
        private static string ToggleSmartDimFlags(IZSceneDoc scene, Action<string> log)
        {
            const string tag = "annotate";
            IZSceneDocProperty prop = null;
            try { prop = scene as IZSceneDocProperty; }   // CLSID 6BE273BE… 实现该接口（classes.csv）
            catch (Exception ex) { log(tag + ": QI(IZSceneDocProperty) FAIL " + ProbeCommon.Hr(ex)); }
            if (prop == null) return "场景文档不支持 IZSceneDocProperty（第三态：显示开关读不到）";

            int[] flags = new int[]
            {
                Z_SHOW_SMARTDIMENSIONS, Z_SHOW_DIMENSIONS, Z_SHOW_2D_DIMENSION_NAME,
                Z_SHOW_CONSTRAINT_DIMS_DEPENDS, Z_SHOW_SMARTDIM_ON_SELECTION, Z_SHOW_2D_SKETCH
            };
            int ok = 0, fail = 0;
            foreach (int flag in flags)
            {
                bool before = false, readBefore = false;
                try { prop.IsShowFlagOn((eSHOWOption)flag, out before); readBefore = true; }
                catch (Exception ex) { log(tag + ": IsShowFlagOn(" + flag + ") FAIL " + ProbeCommon.Hr(ex)); }

                try
                {
                    prop.SetShowFlag((eSHOWOption)flag, true);
                    bool after = false;
                    prop.IsShowFlagOn((eSHOWOption)flag, out after);
                    log(string.Format("{0}: flag {1} 原={2} → 回读={3}", tag, flag,
                        readBefore ? (before ? "true" : "false") : "<读不到>", after ? "true" : "false"));
                    if (after) ok++;
                    else { fail++; log(tag + ": flag " + flag + " 写完回读 false（第三态）"); }
                }
                catch (Exception ex)
                {
                    fail++;
                    log(tag + ": flag " + flag + " SetShowFlag/回读 FAIL " + ProbeCommon.Hr(ex));
                }
            }

            try { scene.Update(); }
            catch (Exception ex) { log(tag + ": scene.Update FAIL " + ProbeCommon.Hr(ex)); }

            return fail == 0
                ? string.Format("智能标注显示开关：{0} 个 flag 写入并回读 true", ok)
                : string.Format("智能标注显示开关：回读 true {0} 个 / 失败 {1} 个（第三态，不宣称成功）", ok, fail);
        }

        /// <summary>枚举一个 RelationMgr 下的全部形状关系；Z_RelationDimension(1) 计入 dimTotal。</summary>
        private static void ReadRelations(IZRelationMgr rm, string source, List<string[]> rows, Action<string> log,
            ref int relTotal, ref int dimTotal)
        {
            object[] rels = null;
            try { rels = rm.GetShapeRelations() as object[]; }   // 槽7
            catch (Exception ex) { log("annotate: " + source + " GetShapeRelations FAIL " + ProbeCommon.Hr(ex)); return; }
            if (rels == null) { log("annotate: " + source + " 返回非数组/空（第三态）"); return; }
            log("annotate: " + source + " Len=" + rels.Length);

            foreach (object o in rels)
            {
                var r = o as IZShapeRelation;
                if (r == null) { log("annotate: " + source + " 非 IZShapeRelation " + ProbeCommon.Describe(o)); continue; }
                RelationInfo info = ShapeRelationReader.Read(r, log, "  ");
                if (info == null) continue;
                string[] tail = ShapeRelationReader.ToRow(info);
                var row = new string[tail.Length + 2];
                row[0] = source;
                row[1] = ProbeCommon.Describe(o);
                Array.Copy(tail, 0, row, 2, tail.Length);
                rows.Add(row);
                relTotal++;
                if (info.TypeValue == Z_REL_DIMENSION) dimTotal++;
                log("  " + ShapeRelationReader.LogLine(info));
            }
        }
    }

    /// <summary>用户点「停止」时抛出的中断信号（必须 [ComVisible(false)]）。</summary>
    [ComVisible(false)]
    public class OperationStoppedException : Exception
    {
        public OperationStoppedException() : base("操作已被用户停止") { }
    }
}
