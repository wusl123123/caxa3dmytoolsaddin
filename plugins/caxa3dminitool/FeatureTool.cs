using System;
using System.Collections;
using System.Collections.Generic;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>
    /// 增料/除料切换：把选中特征的 IZFeature.OperationType 在
    /// Z_UNITE（增料）与 Z_SUBTRACT（除料）之间互换，并触发所属零件重建。
    /// 成功判定只看业务侧回读（切换后 OperationType 是否真的变成目标值），
    /// 不看 API 返回值、也不看“命令执行过”。
    /// </summary>
    internal static class FeatureTool
    {
        internal sealed class Outcome
        {
            public string PartName;
            public string FeatureName;
            public eZOperationType Before;
            public eZOperationType After;
            public eZOperationType Expected;
            public bool Failed;
            /// <summary>切换并刷新后回读是否等于目标值（权威副作用信号）。</summary>
            public bool Changed;
            public string Note;
        }

        // ================================================================ 选中元素

        public static IZSceneDoc GetSceneDoc(IZBaseApp app)
        {
            if (app == null) return null;
            IZDoc doc = app.ActiveDoc;
            return doc as IZSceneDoc;
        }

        /// <summary>获取当前选中元素；失败时 error 返回原因。</summary>
        public static List<IZElement> GetSelection(IZBaseApp app, out string error)
        {
            error = null;
            List<IZElement> list = new List<IZElement>();

            IZSceneDoc scene = GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是设计（场景）环境，请切回 3D 设计环境后再试。";
                return list;
            }

            IZSelectionMgr selMgr = scene.SelectionMgr;
            if (selMgr == null)
            {
                error = "取不到 SelectionMgr。";
                return list;
            }

            try
            {
                ZArray arr = selMgr.GetSelectedElementsZArray();
                if (arr != null)
                {
                    int count;
                    arr.Count(out count);
                    for (int i = 0; i < count; i++)
                    {
                        object raw;
                        arr.Get(i, out raw);
                        AppendElement(raw, list);
                    }
                }
            }
            catch (Exception ex)
            {
                error = "读取选中元素失败：" + ex.Message;
            }

            // 兜底：部分选择上下文下 GetSelectedElementsZArray 拿不到真实选中项
            if (list.Count == 0)
            {
                try
                {
                    AppendElement(selMgr.GetTrueSelectedElements(), list);
                }
                catch (Exception ex)
                {
                    if (error == null) error = "读取真实选中元素失败：" + ex.Message;
                }
            }

            return list;
        }

        private static void AppendElement(object raw, List<IZElement> list)
        {
            if (raw == null) return;

            IZElement single = raw as IZElement;
            if (single != null)
            {
                if (!list.Contains(single)) list.Add(single);
                return;
            }

            ZArray za = raw as ZArray;
            if (za != null)
            {
                int count;
                za.Count(out count);
                for (int i = 0; i < count; i++)
                {
                    object item;
                    za.Get(i, out item);
                    AppendElement(item, list);
                }
                return;
            }

            IEnumerable en = raw as IEnumerable;
            if (en != null && !(raw is string))
            {
                foreach (object item in en) AppendElement(item, list);
            }
        }

        // ================================================================ 切换

        /// <summary>
        /// 对选中特征执行增料/除料切换。
        /// error 仅在“整体无法执行”时返回原因；单个元素的跳过/失败写进 Outcome。
        /// </summary>
        public static List<Outcome> ToggleSelected(IZBaseApp app, out string error)
        {
            error = null;
            List<Outcome> outcomes = new List<Outcome>();

            IZSceneDoc scene = GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是设计（场景）环境，请切回 3D 设计环境后再试。";
                return outcomes;
            }

            List<IZElement> elems = GetSelection(app, out error);
            if (elems.Count == 0)
            {
                if (string.IsNullOrEmpty(error))
                    error = "没有选中任何对象。请先在特征树中选中要切换的特征（拉伸/旋转/扫描/放样等）。";
                return outcomes;
            }

            List<KeyValuePair<IZFeature, Outcome>> featureRefs = new List<KeyValuePair<IZFeature, Outcome>>();
            List<IZPart> parts = new List<IZPart>();
            Dictionary<IZPart, List<Outcome>> partOutcomes = new Dictionary<IZPart, List<Outcome>>();

            foreach (IZElement elem in elems)
            {
                Outcome outcome = new Outcome();
                outcomes.Add(outcome);

                try
                {
                    outcome.FeatureName = SafeName(elem);
                    eZElementType elemType = elem.Type;

                    if (!IsFeatureType(elemType))
                    {
                        outcome.Failed = true;
                        outcome.Note = string.Format(
                            "选中的是 {0}（类型 {1}），不是可切换的特征。请在特征树中选中拉伸/旋转/扫描/放样等特征。",
                            outcome.FeatureName, elemType);
                        continue;
                    }

                    IZFeature feat = elem as IZFeature;
                    if (feat == null)
                    {
                        // 兜底：特征树元素本身不实现 IZFeature 时，到所属零件的特征表里按“类型+名称”找
                        feat = FindFeatureByElement(elem, elemType, outcome.FeatureName);
                    }
                    if (feat == null)
                    {
                        outcome.Failed = true;
                        outcome.Note = "该特征不支持 IZFeature 接口（无法读写增/除料类型），已跳过。";
                        continue;
                    }

                    eZOperationType before = feat.OperationType;
                    outcome.Before = before;
                    outcome.After = before;

                    if (before != eZOperationType.Z_UNITE && before != eZOperationType.Z_SUBTRACT)
                    {
                        outcome.Failed = true;
                        outcome.Note = string.Format(
                            "只有增料(Z_UNITE)/除料(Z_SUBTRACT)可切换，当前为 {0}，已跳过。", before);
                        continue;
                    }

                    eZOperationType expected = before == eZOperationType.Z_SUBTRACT
                        ? eZOperationType.Z_UNITE
                        : eZOperationType.Z_SUBTRACT;
                    outcome.Expected = expected;

                    feat.OperationType = expected;
                    eZOperationType wroteBack = feat.OperationType;
                    if (wroteBack != expected)
                    {
                        outcome.Failed = true;
                        outcome.After = wroteBack;
                        outcome.Note = string.Format("写入后立即回读不一致：期望 {0}，实际 {1}。", expected, wroteBack);
                        continue;
                    }

                    featureRefs.Add(new KeyValuePair<IZFeature, Outcome>(feat, outcome));

                    // 所属零件：切换后必须重建才能反映到几何
                    IZElement owner = null;
                    try { owner = elem.GetOwnerPart(); } catch { }
                    IZPart part = owner as IZPart;
                    if (part != null)
                    {
                        if (!parts.Contains(part)) parts.Add(part);
                        List<Outcome> list;
                        if (!partOutcomes.TryGetValue(part, out list))
                        {
                            list = new List<Outcome>();
                            partOutcomes[part] = list;
                        }
                        list.Add(outcome);
                        outcome.PartName = SafeName(owner);
                    }
                }
                catch (Exception ex)
                {
                    outcome.Failed = true;
                    outcome.Note = "处理该元素异常：" + ex.Message;
                    Diagnostics.Trace("ToggleSelected element exception: " + ex);
                }
            }

            // 批量重建：每个零件只 Update 一次
            foreach (IZPart part in parts)
            {
                try { part.Update(); }
                catch (Exception ex)
                {
                    Diagnostics.Log("零件 Update 失败：" + ex.Message);
                    List<Outcome> list = partOutcomes[part];
                    foreach (Outcome o in list) o.Note = "零件重建失败：" + ex.Message;
                }
            }
            if (parts.Count == 0 && featureRefs.Count > 0)
            {
                try { scene.Update(); } catch (Exception ex) { Diagnostics.Log("场景 Update 失败：" + ex.Message); }
            }

            // 权威判定：重建完成后再回读一次 OperationType
            foreach (KeyValuePair<IZFeature, Outcome> kv in featureRefs)
            {
                Outcome o = kv.Value;
                try
                {
                    o.After = kv.Key.OperationType;
                    o.Changed = o.After == o.Expected;
                    if (!o.Changed)
                        o.Note = string.Format("重建后回读不一致：期望 {0}，实际 {1}（几何可能未更新）。", o.Expected, o.After);
                }
                catch (Exception ex)
                {
                    o.Changed = false;
                    o.Note = "重建后回读异常：" + ex.Message;
                }
            }

            return outcomes;
        }

        /// <summary>
        /// 在所选中特征所属零件的特征表里按“类型 + 名称”定位同名特征对象。
        /// 仅当特征树元素本身 QI 不到 IZFeature 时才走这条路。
        /// </summary>
        private static IZFeature FindFeatureByElement(IZElement elem, eZElementType elemType, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                IZElement owner;
                try { owner = elem.GetOwnerPart(); } catch { return null; }
                IZPartFeatureMgr mgr = owner as IZPartFeatureMgr;
                if (mgr == null) return null;

                int count = mgr.GetFeaturesCount();
                for (int i = 0; i < count; i++)
                {
                    IZFeature f = null;
                    try { f = mgr.GetFeatureAt(i); } catch { continue; }
                    if (f == null) continue;

                    IZElement fe = f as IZElement;
                    if (fe == null) continue;

                    eZElementType t;
                    try { t = fe.Type; } catch { continue; }
                    if (t != elemType) continue;

                    string n;
                    try { n = fe.Name; } catch { continue; }
                    if (!string.Equals(n, name, StringComparison.Ordinal)) continue;

                    Diagnostics.Trace("FindFeatureByElement matched: " + name + " type=" + elemType);
                    return f;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("FindFeatureByElement failed: " + ex.Message);
            }
            return null;
        }

        // ================================================================ 描述
        /// <summary>
        /// 生成「当前设计环境.ics---零部件名（虚拟 / 外部链接）」的选中状态文本。
        /// 只读探测，任何异常都降级为可读提示，不影响主流程。
        /// </summary>
        public static string DescribeSelection(IZBaseApp app)
        {
            try
            {
                IZSceneDoc scene = GetSceneDoc(app);
                if (scene == null) return "当前不是 3D 设计环境";

                string docName = "";
                try { docName = (scene as IZDoc).Name ?? ""; } catch { }
                if (string.IsNullOrEmpty(docName)) docName = "当前设计环境";

                List<IZElement> elems = GetSelection(app, out _);
                if (elems.Count == 0) return docName + "---（未选中对象）";

                IZElement elem = elems[0];
                eZElementType elemType = elem.Type;

                if (IsFeatureType(elemType))
                {
                    string partName = "";
                    try
                    {
                        IZElement owner = elem.GetOwnerPart();
                        partName = SafeName(owner);
                    }
                    catch { }

                    string opText = "";
                    IZFeature feat = elem as IZFeature;
                    if (feat != null)
                    {
                        try { opText = OperationText(feat.OperationType); }
                        catch { }
                    }

                    return string.Format("{0}---{1}（特征：{2}，{3}，{4}）",
                        docName, string.IsNullOrEmpty(partName) ? "?" : partName,
                        SafeName(elem), elemType, opText);
                }

                string linkText = "（虚拟）";
                string kernelText = "";
                IZPart part = elem as IZPart;
                if (part != null)
                {
                    try
                    {
                        bool linked;
                        string path = part.GetExternallyLinkedInfo(out linked);
                        if (linked && !string.IsNullOrEmpty(path)) linkText = "（外部链接：" + path + "）";
                    }
                    catch { }
                    try { kernelText = "，内核：" + KernelTool.KernelText(part.KernelType); }
                    catch { }
                }

                string extra = elems.Count > 1 ? string.Format(" 等 {0} 项", elems.Count) : "";
                return string.Format("{0}---{1}{2}{3}{4}", docName, SafeName(elem), linkText, kernelText, extra);
            }
            catch (Exception ex)
            {
                return "选中状态读取失败：" + ex.Message;
            }
        }

        public static string OperationText(eZOperationType op)
        {
            switch (op)
            {
                case eZOperationType.Z_UNITE: return "增料";
                case eZOperationType.Z_SUBTRACT: return "除料";
                case eZOperationType.Z_INTERSECT: return "相交";
                case eZOperationType.Z_TRIM: return "裁剪";
                case eZOperationType.Z_STITCH: return "缝合";
                case eZOperationType.Z_NONE: return "无";
                default: return op.ToString();
            }
        }

        public static bool IsFeatureType(eZElementType t)
        {
            switch (t)
            {
                case eZElementType.Z_ELEMENT_FEATURE_COMPOUND:
                case eZElementType.Z_ELEMENT_FEATURE_EXTRUDE:
                case eZElementType.Z_ELEMENT_FEATURE_SWEEP:
                case eZElementType.Z_ELEMENT_FEATURE_SPIN:
                case eZElementType.Z_ELEMENT_FEATURE_LOFT:
                case eZElementType.Z_ELEMENT_FEATURE_BLEND:
                case eZElementType.Z_ELEMENT_FEATURE_CHAMFER:
                case eZElementType.Z_ELEMENT_FEATURE_BREP:
                case eZElementType.Z_ELEMENT_FEATURE_SHELL:
                case eZElementType.Z_ELEMENT_FEATURE_RULE_SURFACE:
                case eZElementType.Z_ELEMENT_FEATURE_EDGE_SURFACE:
                case eZElementType.Z_ELEMENT_FEATURE_MESH_SURFACE:
                case eZElementType.Z_ELEMENT_FEATURE_SPIN_SURFACE:
                case eZElementType.Z_ELEMENT_FEATURE_LOFT_SURFACE:
                case eZElementType.Z_ELEMENT_FEATURE_SWEEP_SURFACE:
                case eZElementType.Z_ELEMENT_FEATURE_FILLET_SURFACE:
                case eZElementType.Z_ELEMENT_FEATURE_EXTEND_SURFACE:
                case eZElementType.Z_ELEMENT_FEATURE_PATCH_SURFACE:
                case eZElementType.Z_ELEMENT_FEATURE_FACE_BLEND:
                case eZElementType.Z_ELEMENT_FEATURE_DRAFT:
                case eZElementType.Z_ELEMENT_FEATURE_SPLIT:
                case eZElementType.Z_ELEMENT_FEATURE_PATTERN:
                case eZElementType.Z_ELEMENT_SHEETMETAL_STOCK:
                case eZElementType.Z_ELEMENT_SHEETMETAL_BEND:
                case eZElementType.Z_ELEMENT_SHEETMETAL_PUNCH:
                case eZElementType.Z_ELEMENT_SHEETMETAL_FORM:
                case eZElementType.Z_ELEMENT_SHEETMETAL_CONIC:
                case eZElementType.Z_ELEMENT_SHEETMETAL_LOFT:
                case eZElementType.Z_ELEMENT_SHEETMETAL_CURVESTOCK:
                case eZElementType.Z_ELEMENT_SHEETMETAL_PUNCHBEND:
                case eZElementType.Z_ELEMENT_SHEETMETAL_SKETCHBEND:
                case eZElementType.Z_ELEMENT_PATTERN:
                    return true;
                default:
                    return false;
            }
        }

        private static string SafeName(IZElement elem)
        {
            if (elem == null) return "";
            try { return elem.Name ?? ""; } catch { return ""; }
        }
    }
}
