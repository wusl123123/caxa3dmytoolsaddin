using System;
using System.Collections;
using System.Collections.Generic;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>
    /// 内核切换（Parasolid / ACIS）。
    /// 穿透规则沿用 caxa3dsetMaterial 实证：
    ///  - 装配体节点（IZAssembly 或 IZElement.Type 名含 Assembly）→ GetChildrenZArray() 递归穿透；
    ///  - 叶子节点 → 直接收集，跨调用用 HashSet 去重（同一零件不会因多选父装配体而重复处理）；
    ///  - 无选中时回退穿透整个设计树（IZSceneDoc.GetChildElements()）。
    /// 写入后必须回读 IZPart.KernelType 判定成功（不看 API 返回值）。
    /// </summary>
    internal static class KernelTool
    {
        public enum ApplyStatus
        {
            NotPart,   // 不是零件（草图/三维曲线等），跳过
            Already,   // 本来就是目标内核
            Changed,   // 写入后回读一致
            Failed     // 写入异常或回读不一致
        }

        // ================================================================ 收集叶子零件

        /// <summary>
        /// 从选中节点穿透拿叶子零件；没有选中则回退穿透整个设计树。
        /// </summary>
        public static List<IZElement> GatherLeafParts(IZBaseApp app, out string error, out string summary)
        {
            error = null;
            summary = "";
            List<IZElement> result = new List<IZElement>();
            HashSet<IZElement> seen = new HashSet<IZElement>();

            IZSceneDoc scene = FeatureTool.GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是设计（场景）环境，请切回 3D 设计环境后再试。";
                return result;
            }

            List<IZElement> nodes = FeatureTool.GetSelection(app, out _);
            if (nodes.Count > 0)
            {
                foreach (IZElement node in nodes)
                    CollectLeavesFromNode(node, result, seen);
                summary = string.Format("选中 {0} 个节点 → 穿透得到 {1} 个可用零件", nodes.Count, result.Count);
            }
            else
            {
                List<object> children = GetSceneChildren(scene);
                foreach (object c in children) CollectLeavesFromNode(c, result, seen);
                summary = string.Format("未选中任何节点 → 穿透整个设计树得到 {0} 个可用零件", result.Count);
            }

            if (result.Count == 0)
                error = "没有找到可切换内核的零件。请选中零件或装配体后再试。";
            return result;
        }

        /// <summary>判断 IZElement 是否为装配体（与 caxa3dsetMaterial 同口径）。</summary>
        public static bool IsAssembly(IZElement elem)
        {
            if (elem == null) return false;
            try
            {
                if (elem is IZAssembly || elem is IZAssemblyProperty) return true;
                string tname = elem.Type.ToString();
                if (tname.IndexOf("Assembly", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }

        private static void CollectLeavesFromNode(object node, List<IZElement> result, HashSet<IZElement> seen)
        {
            if (node == null) return;

            IZAssembly asm = node as IZAssembly;
            if (asm == null)
            {
                IZElement elem = node as IZElement;
                if (elem != null && IsAssembly(elem)) asm = elem as IZAssembly;
            }

            if (asm != null)
            {
                List<IZElement> leaves = RecurseChildren(asm);
                foreach (IZElement l in leaves)
                    if (seen.Add(l)) result.Add(l);
                return;
            }

            IZElement leaf = node as IZElement;
            if (leaf != null)
            {
                if (seen.Add(leaf)) result.Add(leaf);
                return;
            }

            IZSceneDoc scene = node as IZSceneDoc;
            if (scene != null)
            {
                foreach (object c in GetSceneChildren(scene)) CollectLeavesFromNode(c, result, seen);
            }
        }

        /// <summary>
        /// 递归穿透装配体：IZAssembly/IZElement 都有 GetChildrenZArray()。
        /// ZArray 用 Get(i, out var) 解包，不受 SAFEARRAY 元素类型影响。
        /// （GetAllLeafParts 返回 int[] 元素 Id，无 managed 映射，已废弃不用。）
        /// </summary>
        private static List<IZElement> RecurseChildren(object parent)
        {
            List<IZElement> result = new List<IZElement>();
            ZArray zarr = null;
            try
            {
                if (parent is IZAssembly asm) zarr = asm.GetChildrenZArray();
                else if (parent is IZElement elem) zarr = elem.GetChildrenZArray();
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("RecurseChildren GetChildrenZArray failed: " + ex.Message);
            }
            if (zarr == null) return result;

            int count;
            zarr.Count(out count);
            for (int i = 0; i < count; i++)
            {
                object rawChild;
                zarr.Get(i, out rawChild);
                IZElement child = rawChild as IZElement;
                if (child == null) continue;

                if (IsAssembly(child))
                {
                    List<IZElement> sub = RecurseChildren(child);
                    foreach (IZElement l in sub) result.Add(l);
                }
                else
                {
                    result.Add(child);
                }
            }
            return result;
        }

        private static List<object> GetSceneChildren(IZSceneDoc scene)
        {
            List<object> result = new List<object>();
            try
            {
                object raw = scene.GetChildElements();
                UnpackArray(raw, result);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("设计树取子元素失败：" + ex.Message);
            }
            return result;
        }

        private static void UnpackArray(object raw, List<object> result)
        {
            if (raw == null) return;
            ZArray zarr = raw as ZArray;
            if (zarr != null)
            {
                int count; zarr.Count(out count);
                for (int i = 0; i < count; i++)
                {
                    object o; zarr.Get(i, out o);
                    if (o != null) result.Add(o);
                }
                return;
            }
            object[] arr = raw as object[];
            if (arr != null)
            {
                foreach (object o in arr) if (o != null) result.Add(o);
                return;
            }
            if (!(raw is IEnumerable) || raw is string) { result.Add(raw); return; }
            foreach (object o in (IEnumerable)raw) if (o != null) result.Add(o);
        }

        // ================================================================ 单个零件切换

        /// <summary>
        /// 对单个元素设置内核。非零件类型返回 NotPart；
        /// Changed 只在写入后回读 == target 时成立。
        /// </summary>
        public static ApplyStatus ApplyKernel(IZElement elem, eZKernelType target,
            out eZKernelType before, out eZKernelType after, out string note)
        {
            before = eZKernelType.Z_KERNEL_NOKERNEL;
            after = eZKernelType.Z_KERNEL_NOKERNEL;
            note = null;

            IZPart part = elem as IZPart;
            if (part == null)
            {
                note = "不是实体零件（可能是草图 / 三维曲线），跳过";
                return ApplyStatus.NotPart;
            }

            try
            {
                before = part.KernelType;
                after = before;

                if (before == target) return ApplyStatus.Already;

                try { part.GetBody(true); } catch { }   // 轻量化零件先强制加载

                part.KernelType = target;
                after = part.KernelType;
                if (after != target)
                {
                    note = string.Format("写入后回读不一致：期望 {0}，实际 {1}", KernelText(target), KernelText(after));
                    return ApplyStatus.Failed;
                }
                return ApplyStatus.Changed;
            }
            catch (Exception ex)
            {
                note = "切换异常：" + ex.Message;
                Diagnostics.Trace("ApplyKernel failed: " + ex);
                return ApplyStatus.Failed;
            }
        }

        public static string KernelText(eZKernelType k)
        {
            switch (k)
            {
                case eZKernelType.Z_KERNEL_NOKERNEL: return "无内核";
                case eZKernelType.Z_KERNEL_ACIS: return "ACIS";
                case eZKernelType.Z_KERNEL_PARASOLID: return "Parasolid";
                case eZKernelType.Z_KERNEL_LITE: return "Lite(钣金)";
                default: return k.ToString();
            }
        }
    }
}
