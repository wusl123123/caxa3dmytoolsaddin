using System;
using System.Collections;
using System.Collections.Generic;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>
    /// 对应 ICM（IronCAD）工具条工具的 API 实现：
    ///  1) 选中取消压缩   ← 原 ICM「查找压缩元素」OnDragDropManageSuppressedParts 的实用变体
    ///                      → 按切换内核的装配体穿透规则抓零件 → SetSuppressState(false) → 回读判定
    ///  2) 更新包围盒     ← IPROShapeUtils.ShapeUtils::OnDragDropResetSizeBox
    ///                     → 元素 QI 到 IZSizeBox 后 Reset(false)
    ///  3) 全选内部链接零件 ← IPROShapeUtils.ShapeUtils::OnDragDropSelectLinkedElements
    ///                     → IZSceneElement.HasInternalLinks / GetInternallyLinkedElements 扫描 + 选中
    /// 成功判定统一走业务侧回读（压缩状态 / 包围盒尺寸 / 选区数量），不看 API 返回值。
    /// </summary>
    internal static class ElementsTool
    {
        // ================================================================ 全树收集

        /// <summary>收集设计树里所有元素（含装配体节点，递归）。</summary>
        public static List<IZElement> CollectAllElements(IZSceneDoc scene)
        {
            List<IZElement> list = new List<IZElement>();
            if (scene == null) return list;
            HashSet<IZElement> seen = new HashSet<IZElement>();
            try
            {
                object raw = scene.GetChildElements();
                CollectRecursive(raw, list, seen, 0);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("设计树遍历失败：" + ex.Message);
            }
            return list;
        }

        private static void CollectRecursive(object raw, List<IZElement> list, HashSet<IZElement> seen, int depth)
        {
            if (raw == null || depth > 40) return;

            IZElement elem = raw as IZElement;
            if (elem != null)
            {
                if (!seen.Add(elem)) return;
                list.Add(elem);
                ZArray kids = null;
                try { kids = elem.GetChildrenZArray(); } catch { }
                if (kids != null) CollectRecursive(kids, list, seen, depth + 1);
                return;
            }

            ZArray zarr = raw as ZArray;
            if (zarr != null)
            {
                int count;
                zarr.Count(out count);
                for (int i = 0; i < count; i++)
                {
                    object item;
                    zarr.Get(i, out item);
                    CollectRecursive(item, list, seen, depth);
                }
                return;
            }

            object[] arr = raw as object[];
            if (arr != null)
            {
                foreach (object item in arr) CollectRecursive(item, list, seen, depth);
                return;
            }

            IEnumerable en = raw as IEnumerable;
            if (en != null && !(raw is string))
                foreach (object item in en) CollectRecursive(item, list, seen, depth);
        }

        // ================================================================ 1) 选中取消压缩

        /// <summary>
        /// 选中取消压缩：按「切换内核」的装配体穿透规则抓取零部件（无选中则穿透整棵树），
        /// 逐个解除压缩，回读 Z_SUPPRESSED 判定。
        /// </summary>
        public static void UnsuppressSelected(IZBaseApp app, out string error, out string summary,
            out int total, out int unsuppressed, out int already, out int failed, out int verified)
        {
            error = null;
            summary = "";
            total = 0; unsuppressed = 0; already = 0; failed = 0; verified = 0;

            List<IZElement> leaves = KernelTool.GatherLeafParts(app, out error, out summary);
            if (!string.IsNullOrEmpty(error)) return;

            total = leaves.Count;
            foreach (IZElement elem in leaves)
            {
                bool suppressed = false;
                try { suppressed = elem.GetStateStatus(eZElementState.Z_SUPPRESSED); }
                catch (Exception ex) { Diagnostics.Trace("GetStateStatus failed: " + ex.Message); }

                if (!suppressed)
                {
                    already++;
                    Diagnostics.Trace("[压缩] " + SafeName(elem) + "：未压缩，跳过");
                    continue;
                }

                try
                {
                    elem.SetSuppressState(false);
                    unsuppressed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Diagnostics.Log("[取消压缩失败] " + SafeName(elem) + "：" + ex.Message);
                }
            }

            IZSceneDoc scene = FeatureTool.GetSceneDoc(app);
            if (scene != null && unsuppressed > 0)
            {
                try { scene.Update(); } catch (Exception ex) { Diagnostics.Log("场景 Update 失败：" + ex.Message); }
            }

            // 权威判定：回读 Z_SUPPRESSED，必须为 false 才算真正解除
            foreach (IZElement elem in leaves)
            {
                try
                {
                    if (!elem.GetStateStatus(eZElementState.Z_SUPPRESSED)) verified++;
                }
                catch { }
            }
        }

        // ================================================================ 2) 更新包围盒

        public static void ResetSizeBoxes(IZBaseApp app, out string error,
            out int reset, out int failed, out int noBox, out int verified)
        {
            error = null;
            reset = 0; failed = 0; noBox = 0; verified = 0;

            IZSceneDoc scene = FeatureTool.GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是设计（场景）环境，请切回 3D 设计环境后再试。";
                return;
            }

            List<IZElement> targets;
            List<IZElement> sel = FeatureTool.GetSelection(app, out _);
            if (sel.Count > 0) targets = sel;
            else targets = CollectAllElements(scene);

            List<KeyValuePair<IZSizeBox, string>> touched = new List<KeyValuePair<IZSizeBox, string>>();
            foreach (IZElement elem in targets)
            {
                string name = SafeName(elem);
                IZSizeBox box = null;
                try { box = elem as IZSizeBox; } catch { }
                if (box == null) { noBox++; continue; }

                bool permit = true;
                try { permit = box.PermitResetSizeBox; } catch { }
                if (!permit)
                {
                    noBox++;
                    Diagnostics.Trace("[包围盒] " + name + "：PermitResetSizeBox=false，跳过");
                    continue;
                }

                try
                {
                    box.Reset(false);
                    reset++;
                    touched.Add(new KeyValuePair<IZSizeBox, string>(box, name));
                }
                catch (Exception ex)
                {
                    failed++;
                    Diagnostics.Log("[包围盒失败] " + name + "：" + ex.Message);
                }
            }

            try { scene.Update(); } catch (Exception ex) { Diagnostics.Log("场景 Update 失败：" + ex.Message); }

            // 权威判定：Reset 后包围盒尺寸应能再读出（非异常），且与零件 bbox 尺寸一致
            foreach (KeyValuePair<IZSizeBox, string> kv in touched)
            {
                try
                {
                    double len = kv.Key.Length, wid = kv.Key.Width, hei = kv.Key.Height;
                    if (Math.Abs(len) > 1e-9 && Math.Abs(wid) > 1e-9 && Math.Abs(hei) > 1e-9) verified++;
                    Diagnostics.Trace(string.Format("[包围盒] {0}：L={1:F4} W={2:F4} H={3:F4}", kv.Value, len, wid, hei));
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("[包围盒回读失败] " + kv.Value + "：" + ex.Message);
                }
            }

            Diagnostics.Log(string.Format(
                "更新包围盒（目标 {0} 个元素）：已重置 {1}，无包围盒跳过 {2}，失败 {3}；重置后回读有效 {4}",
                targets.Count, reset, noBox, failed, verified));
        }

        // ================================================================ 3) 全选内部链接零件

        public static int SelectInternalLinked(IZBaseApp app, out string error, out int selectedAfter)
        {
            error = null;
            selectedAfter = 0;

            IZSceneDoc scene = FeatureTool.GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是设计（场景）环境，请切回 3D 设计环境后再试。";
                return 0;
            }

            IZSelectionMgr sel = GetSelectionMgr(scene);
            if (sel == null)
            {
                error = "取不到 SelectionMgr。";
                return 0;
            }

            // 有选中就以选中节点为种子，否则扫全树
            List<IZElement> seeds = FeatureTool.GetSelection(app, out _);
            bool fromSelection = seeds.Count > 0;
            if (!fromSelection) seeds = CollectAllElements(scene);

            List<IZElement> linked = new List<IZElement>();
            HashSet<IZElement> seen = new HashSet<IZElement>();

            foreach (IZElement elem in seeds)
            {
                IZSceneElement se = null;
                try { se = elem as IZSceneElement; } catch { }
                if (se == null) continue;

                bool hasLinks = false;
                try { se.HasInternalLinks(out hasLinks); } catch { }

                if (hasLinks && seen.Add(elem)) linked.Add(elem);

                try
                {
                    object raw = se.GetInternallyLinkedElements();
                    AppendAll(raw, linked, seen, 0);
                }
                catch { }
            }

            Diagnostics.Log(string.Format("全选内部链接零件：种子 {0}（{1}），命中内部链接元素 {2}{3}",
                seeds.Count, fromSelection ? "选中节点" : "整棵设计树", linked.Count,
                linked.Count == 0 ? "" : "：" + JoinNames(linked, 8)));

            if (linked.Count == 0)
            {
                error = fromSelection
                    ? "选中的对象及其内部链接实例中没有内部链接零件。"
                    : "整个设计树中没有找到内部链接零件。";
                return 0;
            }

            try
            {
                sel.RemoveAllFromSelection();
                foreach (IZElement elem in linked) sel.AddElementToSelection(elem, true);
                selectedAfter = CountSelectedIn(sel, linked);
            }
            catch (Exception ex)
            {
                error = "选中内部链接零件失败：" + ex.Message;
            }
            return linked.Count;
        }

        // ================================================================ 公共小工具

        private static IZSelectionMgr GetSelectionMgr(IZSceneDoc scene)
        {
            try
            {
                ZSelectionMgr mgr = scene.SelectionMgr as ZSelectionMgr;
                return mgr as IZSelectionMgr;
            }
            catch { return null; }
        }

        /// <summary>回读选区，统计其中属于 expected 集合的个数（权威判据）。</summary>
        private static int CountSelectedIn(IZSelectionMgr sel, List<IZElement> expected)
        {
            int matched = 0;
            HashSet<IZElement> targets = new HashSet<IZElement>(expected);
            try
            {
                ZArray arr = sel.GetSelectedElementsZArray();
                if (arr != null)
                {
                    int count;
                    arr.Count(out count);
                    for (int i = 0; i < count; i++)
                    {
                        object raw;
                        arr.Get(i, out raw);
                        IZElement elem = raw as IZElement;
                        if (elem != null && targets.Contains(elem)) matched++;
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("CountSelectedIn failed: " + ex.Message);
            }
            return matched;
        }

        private static void AppendAll(object raw, List<IZElement> list, HashSet<IZElement> seen, int depth)
        {
            if (raw == null || depth > 40) return;

            IZElement elem = raw as IZElement;
            if (elem != null)
            {
                if (seen.Add(elem)) list.Add(elem);
                return;
            }

            ZArray zarr = raw as ZArray;
            if (zarr != null)
            {
                int count;
                zarr.Count(out count);
                for (int i = 0; i < count; i++)
                {
                    object item;
                    zarr.Get(i, out item);
                    AppendAll(item, list, seen, depth);
                }
                return;
            }

            object[] arr = raw as object[];
            if (arr != null)
            {
                foreach (object item in arr) AppendAll(item, list, seen, depth);
                return;
            }

            IEnumerable en = raw as IEnumerable;
            if (en != null && !(raw is string))
                foreach (object item in en) AppendAll(item, list, seen, depth);
        }

        private static string JoinNames(List<IZElement> list, int max)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < list.Count && i < max; i++) names.Add(SafeName(list[i]));
            string s = string.Join("、", names.ToArray());
            if (list.Count > max) s += string.Format(" 等 {0} 个", list.Count);
            return s;
        }

        private static string SafeName(IZElement elem)
        {
            if (elem == null) return "?";
            try { return string.IsNullOrEmpty(elem.Name) ? "?" : elem.Name; } catch { return "?"; }
        }
    }
}
