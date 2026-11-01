using System;
using System.Collections.Generic;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 读取选中元素的包围盒（世界轴 AABB）与最小（紧致）包围盒尺寸。
    /// 迁自独立工具 caxa3dsizeboxread，现作为「钣金件尺寸填写」（SheetSizeTool）的内部依赖。
    ///
    /// 实测签名（_probe-tools/out/tlb/2027/members.csv，非文档口径）：
    ///   IZPart     : Object GetBoundingBox(Boolean vbInLocalSpace)   slot 73（interop 未暴露）
    ///   IZAssembly : Object GetBoundingBox(Boolean vbInLocalSpace)   slot 53
    ///   IZBody     : Object GetBoundingBox()                         slot 13（局部坐标）
    ///   IZBody     : Object GetTightBoundingBox()                    slot 21（局部坐标，紧致）
    ///   IZSizeBox  : Double Length / Width / Height                  （参数化尺寸箱）
    /// 返回值统一是 "6 个 float 的 VARIANT 数组"：minX,minY,minZ,maxX,maxY,maxZ。
    /// 文档 ICAPIHELP 把 IZAssembly.GetBoundingBox 的 C# 签名写成 void（丢了返回值），不可信。
    ///
    /// 全部为只读操作，不改文档、不需要 Update()。
    /// </summary>
    internal static class SizeBoxTool
    {
        internal sealed class SizeInfo
        {
            public string Name = "";
            public string Kind = "";
            public string Source = "";
            public string Note = "";

            public bool HasBox;
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

            /// <summary>紧致包围盒三个方向的尺寸；HasTight=false 时无效。</summary>
            public bool HasTight;
            public double TightX, TightY, TightZ;
            public bool TightInWorld;

            public double SizeX { get { return MaxX - MinX; } }
            public double SizeY { get { return MaxY - MinY; } }
            public double SizeZ { get { return MaxZ - MinZ; } }
        }

        // ================================================================ 选区

        public static IZSceneDoc GetSceneDoc(IZBaseApp app)
        {
            if (app == null) return null;
            try
            {
                IZDoc doc = app.ActiveDoc;
                return doc as IZSceneDoc;
            }
            catch { return null; }
        }

        /// <summary>取当前选中元素：先 GetSelectedElementsZArray，空则退回 GetTrueSelectedElements。</summary>
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

            IZSelectionMgr selMgr = null;
            try { selMgr = scene.SelectionMgr; } catch { }
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

            if (list.Count == 0)
            {
                try { AppendElement(selMgr.GetTrueSelectedElements(), list); }
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
                    object inner;
                    za.Get(i, out inner);
                    AppendElement(inner, list);
                }
            }
        }

        // ================================================================ 读包围盒

        public static SizeInfo ReadOne(IZBaseApp app, IZElement elem)
        {
            SizeInfo info = new SizeInfo();
            if (elem == null) { info.Note = "元素为空"; return info; }

            try { info.Name = elem.Name ?? ""; } catch { }
            if (string.IsNullOrEmpty(info.Name)) info.Name = "(未命名)";

            // ---- 1) 零件：走 IZBody 的包围盒（interop 里 IZPart 没有暴露 GetBoundingBox，实测 CS1061）
            IZPart part = elem as IZPart;
            if (part != null)
            {
                info.Kind = "零件";
                IZBody body = null;
                try { body = part.GetBody(true); }   // true = 轻量化零件也取实体
                catch (Exception ex) { info.Note = "GetBody 异常：" + ex.Message; }

                if (body != null)
                {
                    double[] aabb = null, tight = null;
                    try { aabb = ToDoubleArray(body.GetBoundingBox()); } catch { }
                    try { tight = ToDoubleArray(body.GetTightBoundingBox()); } catch { }

                    if (aabb != null && aabb.Length >= 6) SetWorld(app, elem, info, aabb, true);
                    if (tight != null && tight.Length >= 6) SetTight(app, elem, info, tight);

                    if (info.HasTight)
                        info.Source = "IZBody.GetTightBoundingBox()（局部）+ GetTransformToGlobal";
                    else if (info.HasBox)
                        info.Source = "IZBody.GetBoundingBox()（局部）+ GetTransformToGlobal";
                    else
                        info.Note = "实体存在但读不到包围盒";
                }
                else if (string.IsNullOrEmpty(info.Note))
                {
                    info.Note = "该零件没有实体（曲线/空零件/仅特征）";
                }

                if (info.HasBox) return info;
            }

            // ---- 2) 装配体
            IZAssembly asm = elem as IZAssembly;
            if (asm != null)
            {
                info.Kind = "装配体";
                try
                {
                    double[] v = ToDoubleArray(asm.GetBoundingBox(false));
                    if (v != null && v.Length >= 6)
                    {
                        Fill(info, v);
                        info.Source = "IZAssembly.GetBoundingBox(false) 世界坐标";
                    }
                }
                catch (Exception ex) { info.Note = "GetBoundingBox 异常：" + ex.Message; }
                if (info.HasBox) return info;
            }

            // ---- 3) 兜底：IZSizeBox 参数化尺寸箱（无 min/max，只有三维尺寸）
            IZSizeBox box = null;
            try { box = elem as IZSizeBox; } catch { }
            if (box != null)
            {
                info.Kind = "元素";
                try
                {
                    double len = box.Length, wid = box.Width, hei = box.Height;
                    info.MinX = 0; info.MinY = 0; info.MinZ = 0;
                    info.MaxX = len; info.MaxY = wid; info.MaxZ = hei;
                    info.HasBox = Math.Abs(len) > 1e-9 || Math.Abs(wid) > 1e-9 || Math.Abs(hei) > 1e-9;
                    info.Source = "IZSizeBox.Length/Width/Height（局部坐标系尺寸箱，非几何包围盒）";
                    if (!info.HasBox) info.Note = "尺寸箱为 0";
                }
                catch (Exception ex) { info.Note = "读 IZSizeBox 失败：" + ex.Message; }
                return info;
            }

            info.Kind = "元素";
            info.Note = "该元素既不是零件/装配体，也没有 IZSizeBox";
            return info;
        }

        private static void Fill(SizeInfo info, double[] v)
        {
            info.MinX = v[0]; info.MinY = v[1]; info.MinZ = v[2];
            info.MaxX = v[3]; info.MaxY = v[4]; info.MaxZ = v[5];
            info.HasBox = true;
        }

        /// <summary>把局部包围盒 6 值当作世界包围盒填入（装配体接口直接给世界坐标时用）。</summary>
        private static void SetWorld(IZBaseApp app, IZElement elem, SizeInfo info, double[] local6, bool tryTransform)
        {
            double minX, minY, minZ, maxX, maxY, maxZ;
            if (tryTransform && TryBoxToWorld(app, elem, local6, out minX, out minY, out minZ, out maxX, out maxY, out maxZ))
            {
                info.MinX = minX; info.MinY = minY; info.MinZ = minZ;
                info.MaxX = maxX; info.MaxY = maxY; info.MaxZ = maxZ;
                info.HasBox = true;
                return;
            }

            Fill(info, local6);   // 变换失败：按局部值填，source 里会说明
        }

        /// <summary>
        /// 紧致包围盒是局部坐标：用元素的世界变换把 8 个角点搬过去，再取 AABB，
        /// 这样旋转过的零件也能给出世界轴向上的真实尺寸。变换失败就退回局部尺寸。
        /// </summary>
        private static void SetTight(IZBaseApp app, IZElement elem, SizeInfo info, double[] t)
        {
            info.HasTight = true;
            info.TightInWorld = false;
            info.TightX = Math.Abs(t[3] - t[0]);
            info.TightY = Math.Abs(t[4] - t[1]);
            info.TightZ = Math.Abs(t[5] - t[2]);

            double minX, minY, minZ, maxX, maxY, maxZ;
            if (!TryBoxToWorld(app, elem, t, out minX, out minY, out minZ, out maxX, out maxY, out maxZ)) return;

            info.TightInWorld = true;
            info.TightX = maxX - minX;
            info.TightY = maxY - minY;
            info.TightZ = maxZ - minZ;

            // 紧致盒可用时，如果没拿到整体包围盒，就用它兜底（局部变换已成功，是世界值）
            if (!info.HasBox)
            {
                info.MinX = minX; info.MinY = minY; info.MinZ = minZ;
                info.MaxX = maxX; info.MaxY = maxY; info.MaxZ = maxZ;
                info.HasBox = true;
            }
        }

        /// <summary>局部 AABB 的 8 个角点经 GetTransformToGlobal 变换后再求 AABB。</summary>
        private static bool TryBoxToWorld(IZBaseApp app, IZElement elem, double[] b,
            out double minX, out double minY, out double minZ,
            out double maxX, out double maxY, out double maxZ)
        {
            minX = minY = minZ = 0;
            maxX = maxY = maxZ = 0;
            if (b == null || b.Length < 6) return false;

            IZSceneElement se = elem as IZSceneElement;
            IZMathUtility mu = app as IZMathUtility;
            if (se == null || mu == null) return false;

            ZMathMatrix m = null;
            try { m = se.GetTransformToGlobal(); } catch { }
            if (m == null) return false;

            minX = double.MaxValue; minY = double.MaxValue; minZ = double.MaxValue;
            maxX = double.MinValue; maxY = double.MinValue; maxZ = double.MinValue;

            for (int i = 0; i < 8; i++)
            {
                double x = ((i & 1) == 0) ? b[0] : b[3];
                double y = ((i & 2) == 0) ? b[1] : b[4];
                double z = ((i & 4) == 0) ? b[2] : b[5];

                double ox, oy, oz;
                if (!TryTransformPoint(mu, m, x, y, z, out ox, out oy, out oz)) return false;
                if (ox < minX) minX = ox; if (ox > maxX) maxX = ox;
                if (oy < minY) minY = oy; if (oy > maxY) maxY = oy;
                if (oz < minZ) minZ = oz; if (oz > maxZ) maxZ = oz;
            }

            return Math.Abs(maxX - minX) > 1e-9 || Math.Abs(maxY - minY) > 1e-9 || Math.Abs(maxZ - minZ) > 1e-9;
        }

        private static bool TryTransformPoint(IZMathUtility mu, ZMathMatrix m,
            double x, double y, double z, out double ox, out double oy, out double oz)
        {
            ox = oy = oz = 0;
            try
            {
                ZMathPoint p = null;
                try { p = mu.CreateMathPoint(new double[] { x, y, z }); }
                catch { p = mu.CreateMathPoint(new object[] { x, y, z }); }
                if (p == null) return false;
                p.TransformBy(m);
                ox = p.X; oy = p.Y; oz = p.Z;
                return true;
            }
            catch { return false; }
        }

        // ================================================================ VARIANT 解包

        /// <summary>
        /// 把 "6 个 float 的 VARIANT 数组" 解成 double[]。
        /// COM 返回可能是 float[] / double[] / object[] / SAFEARRAY / ZArray，全部兼容。
        /// </summary>
        public static double[] ToDoubleArray(object raw)
        {
            if (raw == null) return null;

            double[] d = raw as double[];
            if (d != null) return d;

            float[] f = raw as float[];
            if (f != null)
            {
                double[] r = new double[f.Length];
                for (int i = 0; i < f.Length; i++) r[i] = f[i];
                return r;
            }

            ZArray za = raw as ZArray;
            if (za != null)
            {
                int count;
                za.Count(out count);
                double[] rz = new double[count];
                for (int i = 0; i < count; i++)
                {
                    object o;
                    za.Get(i, out o);
                    rz[i] = Convert.ToDouble(o);
                }
                return rz;
            }

            Array a = raw as Array;   // 含 object[]
            if (a != null)
            {
                double[] ra = new double[a.Length];
                for (int i = 0; i < a.Length; i++) ra[i] = Convert.ToDouble(a.GetValue(i));
                return ra;
            }

            return null;
        }
    }
}
