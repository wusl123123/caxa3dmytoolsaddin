using System;
using System.Collections.Generic;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>
    /// 「将零部件添加到BOM」：对应映射文档 #12（ICM 的 OnDragDropSetIncludeInBOM）。
    ///
    /// 选中零部件 → IZPart / IZAssembly.IncludedInBOM = true → 回读确认。
    /// IncludedInBOM 是 BOM 纳入的权威开关（映射文档 #12 有接口判定），
    /// 不动件号/描述/数量等其余 BOM 设置。
    /// </summary>
    internal static class BomTool
    {
        internal sealed class Outcome
        {
            public string Name;
            public string Kind;
            public bool Ok;
            public bool WasIncluded;      // 之前是否已纳入
            public string Note;
        }

        // ================================================================ 主流程

        /// <summary>
        /// 把选中的零部件全部纳入 BOM（IncludedInBOM = true）。
        /// error 仅在整体无法执行时返回；单个结果在返回列表中。
        /// </summary>
        public static List<Outcome> AddSelectedToBom(IZBaseApp app, out string error, out string summary)
        {
            error = null;
            summary = "";

            List<Outcome> results = new List<Outcome>();

            string selError;
            List<IZElement> sel = FeatureTool.GetSelection(app, out selError);
            if (!string.IsNullOrEmpty(selError) && sel.Count == 0)
            {
                error = selError;
                return results;
            }
            if (sel.Count == 0)
            {
                error = "请先在场景中选中零部件后再点此按钮。";
                return results;
            }

            int ok = 0;
            foreach (IZElement elem in sel)
            {
                Outcome o = new Outcome();
                o.Name = SafeName(elem);
                try
                {
                    RunOne(elem, o);
                }
                catch (Exception ex)
                {
                    o.Ok = false;
                    o.Note = "异常：" + ex.Message;
                    Diagnostics.Log("[BOM] " + o.Name + " 异常：" + ex);
                }
                if (o.Ok) ok++;
                results.Add(o);
            }

            summary = string.Format("目标 {0} 个，纳入 {1} 个（本就纳入 {2} 个）",
                results.Count, ok, results.FindAll(x => x.Ok && x.WasIncluded).Count);
            return results;
        }

        // ================================================================ 单个元素

        private static void RunOne(IZElement elem, Outcome o)
        {
            IZPart part = elem as IZPart;
            IZAssembly asm = elem as IZAssembly;

            // 选中的可能是特征：往上找所属零件
            if (part == null && asm == null)
            {
                try
                {
                    IZElement owner = elem.GetOwnerPart();
                    if (owner != null)
                    {
                        part = owner as IZPart;
                        asm = owner as IZAssembly;
                    }
                }
                catch (Exception ex) { Diagnostics.Trace("GetOwnerPart failed: " + ex.Message); }
            }

            if (part == null && asm == null)
            {
                o.Ok = false;
                o.Note = "该元素不是零部件（零件/装配体），无法纳入 BOM。";
                return;
            }
            o.Kind = (asm != null) ? "装配体" : "零件";

            bool included = part != null ? part.IncludedInBOM : asm.IncludedInBOM;
            o.WasIncluded = included;

            if (included)
            {
                o.Ok = true;
                o.Note = "本就已纳入 BOM";
                return;
            }

            if (part != null) part.IncludedInBOM = true;
            else asm.IncludedInBOM = true;

            // 权威判定：回读
            bool after = part != null ? part.IncludedInBOM : asm.IncludedInBOM;
            if (after)
            {
                o.Ok = true;
            }
            else
            {
                o.Ok = false;
                o.Note = "写入 IncludedInBOM=true 后回读仍为 false";
            }
        }

        private static string SafeName(IZElement elem)
        {
            if (elem == null) return "?";
            try { return string.IsNullOrEmpty(elem.Name) ? "?" : elem.Name; } catch { return "?"; }
        }
    }
}
