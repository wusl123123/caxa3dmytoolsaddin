using System;
using System.Collections.Generic;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>
    /// 「获取点坐标」：对应 ICM 的 OnDragDropDisplayCoordinates（映射文档 #33 / S5）。
    ///
    /// 实测结论（2027）：拾取零件顶点时选择集返回的是零件本身，顶点必须从 SelectionMgr 取：
    ///  1) IZSelectionMgr.GetSelectedVerticesCount(elem) / GetSelectedVertices(elem) → 被拾取的顶点 ID；
    ///  2) IZPart.GetBody(true) → IZBody → QI IZBodyVertex → GetPosition(vertexId) → 体局部坐标（米）；
    ///  3) 全局 = IZSceneElement.GetTransformToGlobal 矩阵 × 局部坐标（R·p + T）。
    /// 注意：零件不实现 IZEDOSiteElement（那是 EDO 用的），位置/锚一律用 IZSceneElement。
    /// 内部单位米，显示毫米（×1000）。
    /// </summary>
    internal static class PointTool
    {
        internal sealed class Outcome
        {
            public string Name;
            public bool Ok;
            public string Note;
            public double[] Local;    // 米，所属体坐标系
            public double[] Global;   // 米，全局坐标系
            public string Source;
        }

        // ================================================================ 主流程

        /// <summary>
        /// 读取当前拾取点的坐标。返回每个顶点的结果；
        /// error 非空表示整体没跑起来（未选中 / 非场景环境等）。
        /// </summary>
        public static List<Outcome> GetPickedPointCoordinates(IZBaseApp app, out string error, out string summary)
        {
            error = null;
            summary = "";

            List<Outcome> results = new List<Outcome>();

            IZSceneDoc scene = FeatureTool.GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是设计（场景）环境，请切回 3D 设计环境后再试。";
                return results;
            }

            IZSelectionMgr selMgr = null;
            try { selMgr = scene.SelectionMgr; } catch { }
            if (selMgr == null)
            {
                error = "取不到 SelectionMgr，无法读取拾取的顶点。";
                return results;
            }

            string selError;
            List<IZElement> sel = FeatureTool.GetSelection(app, out selError);
            if (!string.IsNullOrEmpty(selError) && sel.Count == 0)
            {
                error = selError;
                return results;
            }
            if (sel.Count == 0)
            {
                error = "请先在场景中拾取顶点（或选中零件后再拾取顶点）后再点此按钮。";
                return results;
            }

            int ok = 0;
            foreach (IZElement elem in sel)
            {
                List<Outcome> one = ReadElementVertices(elem, selMgr);
                if (one.Count == 0)
                {
                    // 该元素上没有拾取到顶点
                    Outcome o = new Outcome();
                    o.Name = SafeName(elem);
                    o.Ok = false;
                    o.Note = "该元素上没有拾取到顶点（请用顶点捕捉拾取具体点，而不是只选中零件）。";
                    one.Add(o);
                }
                foreach (Outcome o in one)
                {
                    if (o.Ok) ok++;
                    results.Add(o);
                }
            }

            summary = string.Format("拾取顶点 {0} 个，读出坐标 {1} 个", results.Count, ok);
            return results;
        }

        // ================================================================ 单个元素上的顶点

        /// <summary>返回该元素上所有被拾取的顶点结果；没有拾取到顶点则返回空列表。</summary>
        private static List<Outcome> ReadElementVertices(IZElement elem, IZSelectionMgr selMgr)
        {
            List<Outcome> list = new List<Outcome>();
            string name = SafeName(elem);

            // ---- 1) 该元素上被拾取的顶点 ID ----
            int cnt = 0;
            try { cnt = selMgr.GetSelectedVerticesCount(elem); }
            catch (Exception ex) { Diagnostics.Trace("GetSelectedVerticesCount failed: " + ex.Message); }

            if (cnt <= 0) return list;

            object idsObj = null;
            try { idsObj = selMgr.GetSelectedVertices(elem); }
            catch (Exception ex) { Diagnostics.Trace("GetSelectedVertices failed: " + ex.Message); }

            int[] ids = ToInts(idsObj);
            if (ids == null || ids.Length == 0) return list;

            // ---- 2) 取体：IZPart.GetBody ----
            IZBody body = null;
            IZPart part = elem as IZPart;
            if (part == null)
            {
                try
                {
                    IZElement owner = elem.GetOwnerPart();
                    part = owner as IZPart;
                }
                catch { }
            }
            if (part != null)
            {
                try { body = part.GetBody(true); }
                catch (Exception ex) { Diagnostics.Trace("GetBody failed: " + ex.Message); }
            }

            // ---- 3) 逐顶点取坐标 ----
            IZSceneElement se = null;
            try { se = elem as IZSceneElement; } catch { }
            IZMathMatrix globalM = null;
            if (se != null)
            {
                try { globalM = se.GetTransformToGlobal() as IZMathMatrix; } catch { }
            }

            IZBodyVertex bv = body as IZBodyVertex;

            foreach (int vid in ids)
            {
                Outcome o = new Outcome();
                o.Name = name + " / 顶点" + vid;
                o.Source = "SelectionMgr.GetSelectedVertices + IZBodyVertex.GetPosition";

                double[] local = null;
                if (bv != null)
                {
                    try
                    {
                        object pos = bv.GetPosition(vid);
                        local = ParsePoint(pos);
                    }
                    catch (Exception ex)
                    {
                        o.Note = "GetPosition(" + vid + ") 失败：" + ex.Message;
                    }
                }
                else
                {
                    o.Note = "取不到 IZBodyVertex（GetBody 失败或元素不是零件）。";
                }

                if (local == null)
                {
                    o.Ok = false;
                    list.Add(o);
                    continue;
                }

                o.Local = local;
                if (globalM != null)
                {
                    o.Global = ApplyMatrix(globalM, local);
                    o.Source += " + IZSceneElement.GetTransformToGlobal";
                }
                else
                {
                    o.Global = local;
                    o.Source += "（无全局变换，体坐标按全局给出）";
                    o.Note = "未能换算到全局坐标；";
                }
                o.Ok = true;

                Diagnostics.Trace(string.Format(
                    "[点坐标] {0} 局部=({1:F6},{2:F6},{3:F6}) 全局=({4:F6},{5:F6},{6:F6})",
                    o.Name, local[0], local[1], local[2], o.Global[0], o.Global[1], o.Global[2]));

                list.Add(o);
            }

            return list;
        }

        // ================================================================ 小工具

        private static int[] ToInts(object v)
        {
            if (v == null) return null;
            Array a = v as Array;
            if (a == null) return null;
            try
            {
                int[] ids = new int[a.Length];
                for (int i = 0; i < a.Length; i++) ids[i] = Convert.ToInt32(a.GetValue(i));
                return ids;
            }
            catch { return null; }
        }

        /// <summary>Position 可能是 IZMathPoint、double[3]、object[3]。</summary>
        private static double[] ParsePoint(object v)
        {
            if (v == null) return null;

            try
            {
                IZMathPoint mp = v as IZMathPoint;
                if (mp != null) return new double[] { mp.X, mp.Y, mp.Z };
            }
            catch { }

            try
            {
                Array a = v as Array;
                if (a != null && a.Length >= 3)
                    return new double[]
                    {
                        Convert.ToDouble(a.GetValue(0)),
                        Convert.ToDouble(a.GetValue(1)),
                        Convert.ToDouble(a.GetValue(2))
                    };
            }
            catch { }

            return null;
        }

        /// <summary>global = R·p + T（行 r 列 c 由 GetValue(r,c) 给定）。</summary>
        private static double[] ApplyMatrix(IZMathMatrix m, double[] p)
        {
            double tx, ty, tz;
            GetTrans(m, out tx, out ty, out tz);

            return new double[]
            {
                m.GetValue(0, 0) * p[0] + m.GetValue(0, 1) * p[1] + m.GetValue(0, 2) * p[2] + tx,
                m.GetValue(1, 0) * p[0] + m.GetValue(1, 1) * p[1] + m.GetValue(1, 2) * p[2] + ty,
                m.GetValue(2, 0) * p[0] + m.GetValue(2, 1) * p[1] + m.GetValue(2, 2) * p[2] + tz
            };
        }

        private static void GetTrans(IZMathMatrix m, out double x, out double y, out double z)
        {
            double vx = 0, vy = 0, vz = 0;
            m.GetTranslation(out vx, out vy, out vz);
            x = vx; y = vy; z = vz;
        }

        private static string SafeName(IZElement elem)
        {
            if (elem == null) return "?";
            try { return string.IsNullOrEmpty(elem.Name) ? "?" : elem.Name; } catch { return "?"; }
        }
    }
}
