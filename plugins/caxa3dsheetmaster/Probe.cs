using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dsheetmaster
{
    /// <summary>
    /// 探针：把当前设计场景里与钣金折弯相关的 API 数据全量 dump 到桌面文件。
    /// 目的：在界面/列表异常时，用真实运行数据定位是哪个槽位、哪个接口、哪个属性不按预期返回。
    /// </summary>
    internal static partial class CaxaAutomation
    {
        /// <summary>导出探针报告，返回文件路径。</summary>
        public static string ProbeDump(CaxaSession session, Action<string> log,
            Action<int, string> pump, Func<bool> shouldStop)
        {
            if (log == null) log = s => { };

            var sb = new StringBuilder();
            sb.AppendLine("CAXA 3D 折弯管理大师 · 探针报告");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try
            {
                sb.AppendLine("程序集：" + Assembly.GetExecutingAssembly().Location);
                sb.AppendLine("版本：" + Assembly.GetExecutingAssembly().GetName().Version);
            }
            catch { }
            sb.AppendLine();

            IntPtr pDoc, pScene;
            OpenActiveScene(session, out pDoc, out pScene);
            try
            {
                sb.AppendLine("BaseApp 指针：0x" + (session != null ? session.BaseApp.ToInt64() : 0).ToString("X"));
                sb.AppendLine("ActiveDoc 指针：0x" + pDoc.ToInt64().ToString("X"));
                sb.AppendLine("IZSceneDoc QI：" + (pScene != IntPtr.Zero ? "ok" : "失败"));
                sb.AppendLine();

                // ---- 文档与选中信息 ----
                string docName = null;
                SelectedInfo info = new SelectedInfo();
                ProbeSessionInfo(session, out docName, out info);
                sb.AppendLine("[文档]");
                sb.AppendLine("  名称：" + (docName ?? "(空)"));
                sb.AppendLine("  选中：HasElement=" + info.HasElement +
                    "  Name=" + info.Name + "  Type=" + info.Type +
                    "  Kind=" + info.Kind + "  ExternallyLinked=" + info.ExternallyLinked);
                sb.AppendLine();

                if (pScene == IntPtr.Zero)
                {
                    sb.AppendLine("没有活动设计场景，探针到此结束。");
                    return WriteReport(sb, log);
                }

                // 折弯线 API 槽位整段已剔除（严禁用折弯线做内部折弯判断，见项目总结第 44 条）

                // ---- 设计树 ----
                IntPtr pRoot = CallGetObj(pScene, SlotSceneGetTopElement, "IZSceneDoc.GetTopElement");
                if (pRoot == IntPtr.Zero)
                {
                    sb.AppendLine("GetTopElement 返回空，设计场景为空。");
                    return WriteReport(sb, log);
                }

                sb.AppendLine("[设计树]");
                int nodes = 0;
                int partCount = 0;
                TreeVisit onEnter = (pElem, type, depth) =>
                {
                    var line = new StringBuilder();
                    line.Append(' ', Math.Min(depth, 20) * 2);
                    int id;
                    string name;
                    ReadElementIdentity(pElem, out id, out name);
                    line.Append(string.Format("type={0}({1}) id={2} name={3}",
                        type, MapElementKind(type), id, name));
                    sb.AppendLine(line.ToString());

                    // 逐节点接口矩阵：这是本轮最关键的数据（折弯到底实现了哪些接口）
                    if (depth <= 4) DumpInterfaceMatrix(pElem, sb);
                    if (depth <= 4) DumpCustomProps(pElem, name, sb);

                    if (type == ElemTypeSheetMetalPart)
                    {
                        partCount++;
                        if (!name.StartsWith("展开", StringComparison.Ordinal))
                        {
                            IntPtr pSm;
                            if (TryQI(pElem, IID_IZSheetMetalPart, out pSm) == 0 && pSm != IntPtr.Zero)
                            {
                                int dumped = 0;
                                try { dumped = DumpSheetMetalPart(pSm, name, sb, pump, shouldStop); }
                                finally { Marshal.Release(pSm); }
                                if (log != null)
                                    log(string.Format("探针：钣金件「{0}」折弯线 {1} 条", name, dumped));
                            }
                            else
                            {
                                sb.AppendLine("    QI(IZSheetMetalPart) 失败");
                            }
                        }
                    }

                    if (pump != null && (nodes % 20) == 0)
                        pump(nodes, "探针：正在 dump 设计树…已处理 " + nodes + " 个节点");
                    if (shouldStop != null && shouldStop())
                        throw new OperationStoppedException("已停止");
                };

                try
                {
                    // 不再按类型过滤：之前只放行 0/1/2/20，导致设计树看起来只有 4 个节点；
                    // 实际上折弯(22)、板料(21)等节点都在树里，表格就按树上的折弯条数采集。
                    WalkTree(pRoot, 0, 8, onEnter, null,
                        t => true,
                        shouldStop, null, ref nodes);
                }
                finally
                {
                    Marshal.Release(pRoot);
                }

                sb.AppendLine();
                sb.AppendLine("设计树节点数：" + nodes + "，钣金件：" + partCount);
                return WriteReport(sb, log);
            }
            finally
            {
                if (pScene != IntPtr.Zero) Marshal.Release(pScene);
                if (pDoc != IntPtr.Zero) Marshal.Release(pDoc);
            }
        }

        /// <summary>dump 一个钣金件的全部属性 + 全部折弯线明细；返回折弯线条数。</summary>
        private static int DumpSheetMetalPart(IntPtr pSm, string partName, StringBuilder sb,
            Action<int, string> pump, Func<bool> shouldStop)
        {
            sb.AppendLine("    [钣金件属性] " + partName);

            foreach (var item in new[]
            {
                new { Slot = SlotSmGetThickness, Name = "Thickness", Unit = "mm", Scale = 1000.0 },
                new { Slot = SlotSmGetThickness2, Name = "Thickness2", Unit = "mm", Scale = 1000.0 },
                new { Slot = SlotSmGetMinBendRadius, Name = "MinBendRadius", Unit = "mm", Scale = 1000.0 },
                new { Slot = SlotSmGetCustomUnfoldLength, Name = "CustomUnfoldLengthValue", Unit = "mm", Scale = 1000.0 }
            })
            {
                double v;
                if (TryReadDoubleProp(pSm, item.Slot, "probe SM.get_" + item.Name, out v))
                    sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = {2:0.######} {3}（原始 {4:0.########}）",
                        item.Name, item.Slot, v * item.Scale, item.Unit, v));
                else
                    sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = 读失败", item.Name, item.Slot));
            }

            double k;
            if (TryReadDoubleProp(pSm, SlotSmGetKFactor, "probe SM.get_KFactor", out k))
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = {2:0.######}", "KFactor", SlotSmGetKFactor, k));
            else
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = 读失败", "KFactor", SlotSmGetKFactor));

            short b;
            int i;
            if (TryReadShortProp(pSm, SlotSmGetUseDin6935, "probe SM.get_UseDIN6935", out b))
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = {2}", "UseDIN6935", SlotSmGetUseDin6935, b != 0));
            else
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = 读失败", "UseDIN6935", SlotSmGetUseDin6935));

            if (TryReadShortProp(pSm, SlotSmGetIsFolded, "probe SM.get_IsFolded", out b))
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = {2}", "IsFolded", SlotSmGetIsFolded, b != 0));
            else
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = 读失败", "IsFolded", SlotSmGetIsFolded));

            if (TryReadIntProp(pSm, SlotSmGetUnfoldConfiguration, "probe SM.GetUnfoldConfiguration", out i))
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = {2}", "UnfoldConfiguration", SlotSmGetUnfoldConfiguration, i));
            else
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = 读失败", "UnfoldConfiguration", SlotSmGetUnfoldConfiguration));

            // 库存属性 / 展开件 / 兄弟件一律走 RCW（Marshal.GetObjectForIUnknown + 托管接口调用）。
            // 实测闪退根因：GetStockProperties 有 7 个 out 参数，按「单 double*」裸槽位直调会破坏栈。
            IZSheetMetalPart smRcw = null;
            try { smRcw = Marshal.GetObjectForIUnknown(pSm) as IZSheetMetalPart; }
            catch (Exception ex) { sb.AppendLine("      RCW(IZSheetMetalPart) 失败：" + ex.Message); }

            if (smRcw == null)
            {
                sb.AppendLine("      RCW(IZSheetMetalPart) 为空，跳过库存属性/展开件/兄弟件");
            }
            else
            {
                try
                {
                    string sname = "", scode = "";
                    double weight = 0, sThick = 0, sK = 0, sMinR = 0;
                    int gauge = 0;
                    smRcw.GetStockProperties(out sname, out scode, out weight, out gauge,
                        out sThick, out sK, out sMinR);
                    sb.AppendLine(string.Format(
                        "      {0,-26} = name={1} code={2} gauge={3} 厚={4:0.###}mm K={5:0.####} minR={6:0.###}mm",
                        "GetStockProperties", sname, scode, gauge, sThick * 1000.0, sK, sMinR * 1000.0));
                }
                catch (Exception ex) { sb.AppendLine("      GetStockProperties 读失败：" + ex.Message); }

                try
                {
                    object flatObj = smRcw.GetFlatPart();
                    IntPtr pFlat = flatObj == null ? IntPtr.Zero : Marshal.GetIUnknownForObject(flatObj);
                    sb.AppendLine(string.Format("      {0,-26} = {1}", "GetFlatPart",
                        pFlat == IntPtr.Zero ? "空（无展开件）" : "0x" + pFlat.ToInt64().ToString("X")));
                    if (pFlat != IntPtr.Zero)
                    {
                        try
                        {
                            IntPtr pFlatSm;
                            int qi = TryQI(pFlat, IID_IZSheetMetalPart, out pFlatSm);
                            sb.AppendLine("        → QI(IZSheetMetalPart) hr=0x" + qi.ToString("X"));
                            if (qi == 0 && pFlatSm != IntPtr.Zero)
                            {
                                Marshal.Release(pFlatSm);
                            }
                        }
                        finally { Marshal.Release(pFlat); }
                    }
                }
                catch (Exception ex) { sb.AppendLine("      GetFlatPart EX: " + ex.Message); }

                try
                {
                    object sibObj = smRcw.GetSiblingPart();
                    IntPtr pSib = sibObj == null ? IntPtr.Zero : Marshal.GetIUnknownForObject(sibObj);
                    if (pSib == IntPtr.Zero)
                    {
                        sb.AppendLine(string.Format("      {0,-26} = 空", "GetSiblingPart"));
                    }
                    else
                    {
                        try
                        {
                            int sid;
                            string sname2;
                            ReadElementIdentity(pSib, out sid, out sname2);
                            sb.AppendLine(string.Format("      {0,-26} = id={1} name={2}",
                                "GetSiblingPart", sid, sname2));
                        }
                        finally { Marshal.Release(pSib); }
                    }
                }
                catch (Exception ex) { sb.AppendLine("      GetSiblingPart EX: " + ex.Message); }
            }

            if (TryReadIntProp(pSm, SlotSmGetThicknessRefSide, "probe SM.get_ThicknessRefSide", out i))
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = {2}", "ThicknessRefSide", SlotSmGetThicknessRefSide, i));
            else
                sb.AppendLine(string.Format("      {0,-26} slot {1,-3} = 读失败", "ThicknessRefSide", SlotSmGetThicknessRefSide));

            sb.AppendLine("    [特征管理器]（设计树特征的真正来源；表格就抓这里的钣金折弯特征）");
            int fmBends = ForEachBendFeatureByFeatureMgr(pSm, partName,
                (lineId, pBend, elemId, elemType, elemName) =>
                {
                    sb.AppendLine(string.Format("      · 折弯特征 idx={0} type={1} name={2} 设计树可见={3} 父节点={4}",
                        elemId, elemType, elemName, ReadShowInBrowser(pBend), ReadParentText(pBend)));
                    sb.AppendLine("          BendAllowanceType  = " + IntOrFail(pBend, SlotBendGetAllowanceType,
                        "probe FeatBend.get_BendAllowanceType", "（0=显式展开,1=K因子,2=折弯扣除）"));
                    sb.AppendLine("          CustomKFactorUsed  = " + BoolOrFail(pBend, SlotBendGetCustomKUsed,
                        "probe FeatBend.get_CustomKFactorUsed"));
                    sb.AppendLine("          CustomKFactorValue = " + DblOrFail(pBend, SlotBendGetCustomKValue,
                        "probe FeatBend.get_CustomKFactorValue", ""));
                    sb.AppendLine("          BendDeductionValue = " + DblOrFail(pBend, SlotBendGetDeductionValue,
                        "probe FeatBend.get_BendDeductionValue", " mm（×1000）"));
                    sb.AppendLine("          BendAngle          = " + DblOrFail(pBend, SlotBendGetBendAngle,
                        "probe FeatBend.get_BendAngle", "°（×180/π）"));
                    sb.AppendLine("          InsideRadius       = " + DblOrFail(pBend, SlotBendGetInsideRadius,
                        "probe FeatBend.get_InsideRadius", " mm（×1000）"));
                    // ITypeInfo 枚举真实成员（含类型库未声明的开关），用于定位「使用零件折弯扣除」开关
                    try
                    {
                        string dump = CaxaAutomation.DumpDispatchMembers(pBend, "折弯特征 idx=" + elemId);
                        if (!string.IsNullOrEmpty(dump)) sb.AppendLine(dump);
                    }
                    catch (Exception ex) { sb.AppendLine("          [Dispatch 成员清单] EX: " + ex.Message); }
                    // 折弯元素的自定义属性表：若「使用零件折弯扣除」是特征属性，会以属性名+值的形式出现在这里
                    DumpCustomProps(pBend, "折弯特征 idx=" + elemId, sb);
                }, null);
            sb.AppendLine("      特征管理器折弯特征共 " + fmBends + " 个");
            sb.AppendLine();
            // 折弯线相关探针已按用户要求整体剔除（严禁用折弯线做内部折弯判断依据，见项目总结第 44 条）
            return fmBends;
        }

        /// <summary>
        /// 逐节点打印「实现了哪些接口」——定位折弯对象的真实接口是排查的核心手段
        /// （ICAPI 类型库与运行时实现常不一致，必须实测 QI）。
        /// </summary>
        private static void DumpInterfaceMatrix(IntPtr pElem, StringBuilder sb)
        {
            var hit = new List<string>();
            IntPtr p;

            if (TryQI(pElem, IID_IZElement, out p) == 0 && p != IntPtr.Zero) { hit.Add("IZElement"); Marshal.Release(p); }
            if (TryQI(pElem, IID_IZPart, out p) == 0 && p != IntPtr.Zero) { hit.Add("IZPart"); Marshal.Release(p); }
            if (TryQI(pElem, IID_IZAssembly, out p) == 0 && p != IntPtr.Zero) { hit.Add("IZAssembly"); Marshal.Release(p); }
            if (TryQI(pElem, IID_IZSheetMetalPart, out p) == 0 && p != IntPtr.Zero) { hit.Add("IZSheetMetalPart"); Marshal.Release(p); }
            if (TryQI(pElem, IID_IZSheetMetalStock, out p) == 0 && p != IntPtr.Zero) { hit.Add("IZSheetMetalStock"); Marshal.Release(p); }
            if (TryQI(pElem, IID_IZSheetMetalBend, out p) == 0 && p != IntPtr.Zero) { hit.Add("IZSheetMetalBend"); Marshal.Release(p); }
            if (TryQI(pElem, IID_IZSheetMetalPunchBend, out p) == 0 && p != IntPtr.Zero) { hit.Add("IZSheetMetalPunchBend"); Marshal.Release(p); }
            if (TryQI(pElem, IID_IZSheetMetalSketchBend, out p) == 0 && p != IntPtr.Zero) { hit.Add("IZSheetMetalSketchBend"); Marshal.Release(p); }

            int oid;
            string oname;
            ReadOwnerPart(pElem, out oid, out oname);
            sb.AppendLine(string.Format("    QI: {0}  |  ownerId={1} owner={2}",
                hit.Count == 0 ? "（无）" : string.Join("/", hit.ToArray()), oid, oname));
        }

        /// <summary>
        /// 导出元素自定义属性（折弯扣除可能以自定义属性形式存放，必须在真实数据上确认）。
        /// </summary>
        private static void DumpCustomProps(IntPtr pElem, string tag, StringBuilder sb)
        {
            try
            {
                IntPtr fn = Slot(pElem, SlotElemGetCustomPropMgr);
                if (!LooksCallable(fn))
                {
                    sb.AppendLine("    [自定义属性] " + tag + "：槽位不可调用");
                    return;
                }
                var f = (FnGetObjBool)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetObjBool));
                IntPtr pMgr;
                // 共享表铁律（规则 14.7/14.9）：vbShared=TRUE（-1）取共享表；原来传 0 读到的是零件级表。
                int hr = f(pElem, -1, out pMgr);
                if (hr != 0 || pMgr == IntPtr.Zero)
                {
                    sb.AppendLine(string.Format("    [自定义属性] {0}：hr=0x{1:X}（无）", tag, hr));
                    return;
                }
                object mgrObj = Marshal.GetObjectForIUnknown(pMgr);
                Marshal.Release(pMgr);
                var mgr = mgrObj as IZCustomPropMgr;
                if (mgr == null)
                {
                    sb.AppendLine("    [自定义属性] " + tag + "：接口转换失败（IZCustomPropMgr）");
                    return;
                }
                // 参数名义不算判据，仍要 IsSharedMgr()==true 才继续读
                bool sharedTable = false;
                try { sharedTable = mgr.IsSharedMgr(); } catch { }
                if (!sharedTable)
                {
                    sb.AppendLine("    [自定义属性] " + tag + "：IsSharedMgr=false，按共享表铁律不读其它表");
                    return;
                }
                int count = mgr.Count;
                sb.AppendLine("    [自定义属性] " + tag + " 共 " + count + " 项");
                int n = Math.Min(count, 200);
                for (int i = 0; i < n; i++)
                {
                    string nm;
                    object val;
                    try { mgr.GetAt(i, out nm, out val); }
                    catch (Exception ex) { sb.AppendLine("      [" + i + "] 读取失败：" + ex.Message); continue; }
                    sb.AppendLine(string.Format("      [{0}] {1} = {2}", i, nm,
                        val == null ? "(null)" : val.ToString()));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("    [自定义属性] " + tag + " EX: " + ex.Message);
            }
        }

        private static string DblOrFail(IntPtr p, int slot, string tag, string suffix)
        {
            double v;
            if (!TryReadDoubleProp(p, slot, tag, out v))
                return "读失败（slot " + slot + "）";
            return v.ToString("0.########") + "  [mm: " + (v * 1000.0).ToString("0.######") + "]" + suffix;
        }

        private static string IntOrFail(IntPtr p, int slot, string tag, string suffix)
        {
            int v;
            if (!TryReadIntProp(p, slot, tag, out v))
                return "读失败（slot " + slot + "）";
            return v + " " + suffix;
        }

        private static string BoolOrFail(IntPtr p, int slot, string tag)
        {
            short v;
            if (!TryReadShortProp(p, slot, tag, out v))
                return "读失败（slot " + slot + "）";
            return (v != 0) ? "true" : "false";
        }

        /// <summary>把报告写到桌面（不可写时回退到 %TEMP%），返回路径。</summary>
        private static string WriteReport(StringBuilder sb, Action<string> log)
        {
            string file = "caxa3dsheetmaster_probe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
            string path = null;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                    path = Path.Combine(desktop, file);
            }
            catch { }
            if (path == null) path = Path.Combine(Path.GetTempPath(), file);

            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                log("探针报告已导出：" + path);
            }
            catch (Exception ex)
            {
                log("探针报告写入失败：" + ex.Message);
                return null;
            }
            return path;
        }
    }
}
