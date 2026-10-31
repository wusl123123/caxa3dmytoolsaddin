using System;
using System.Collections.Generic;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>
    /// 「锚点移至重心」：只移动定位锚到零件重心，不动几何、不动三维球。
    ///
    /// 已验证方案（2026-10-31 10:35 / 10:47 日志连续通过，含元素旋转为单位阵与置换阵两种情况）：
    ///   锚的世界位置   = posM.t                       （PositionTransform 的平移）
    ///   几何的世界位置 = posM.t − anchorM.t            （anchorM 平移**按父系口径直减**，不做旋转换算）
    /// 因此同时写两个矩阵、让两项位移互相抵消：
    ///   ① posM.t   := 重心
    ///   ② anchorM.t += (重心 − posM.t原值)
    /// 判定（不看 API 返回值）：锚的父系位置 ≈ 重心（≤0.05 mm）且 GetTransformToGlobal 平移变化 ≈ 0（≤0.1 mm）。
    /// 不通过则把两个矩阵回滚到写入前快照，绝不留下被挪过的模型。
    /// 单位：接口内部为米，日志按毫米显示。
    /// </summary>
    internal static class AnchorTool
    {
        internal sealed class Outcome
        {
            public string Name;
            public string Kind;
            public bool Ok;
            public double[] Cog;              // 米
            public double[] AnchorAfter;      // 米
            public double GeometryShift;      // 米
            public string Note;
        }

        private const double PosTol = 5e-5;    // 锚到重心的允许误差 = 0.05 mm
        private const double MoveTol = 1e-4;   // 几何位移允许误差 = 0.1 mm

        // ================================================================ 主流程

        /// <summary>
        /// 对当前选中元素逐个执行：取重心 → 移锚到重心。
        /// 返回每个元素的处理结果；error 非空表示整体没跑起来（未选中 / 非场景环境等）。
        /// </summary>
        public static List<Outcome> MoveAnchorToCog(IZBaseApp app, out string error, out string summary)
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

            string selError;
            List<IZElement> sel = FeatureTool.GetSelection(app, out selError);
            if (!string.IsNullOrEmpty(selError) && sel.Count == 0)
            {
                error = selError;
                return results;
            }
            if (sel.Count == 0)
            {
                error = "请先在场景中选中零件（或装配体）后再点此按钮。";
                return results;
            }

            int ok = 0;
            foreach (IZElement elem in sel)
            {
                Outcome o = new Outcome();
                o.Name = SafeName(elem);
                o.Kind = "元素";
                try
                {
                    RunOne(elem, scene, o);
                }
                catch (Exception ex)
                {
                    o.Ok = false;
                    o.Note = "异常：" + ex.Message;
                    Diagnostics.Log("[重心] " + o.Name + " 异常：" + ex);
                }
                if (o.Ok) ok++;
                results.Add(o);
            }

            summary = string.Format("目标 {0} 个，成功 {1} 个", results.Count, ok);
            return results;
        }

        // ================================================================ 单个元素

        private static void RunOne(IZElement elem, IZSceneDoc scene, Outcome o)
        {
            IZElement target = elem;
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
                        target = owner;
                        part = owner as IZPart;
                        asm = owner as IZAssembly;
                    }
                }
                catch (Exception ex) { Diagnostics.Trace("GetOwnerPart failed: " + ex.Message); }
            }

            o.Kind = (asm != null) ? "装配体" : (part != null ? "零件" : "元素");

            double[] cog;
            double mass;
            string cogNote;
            if (!TryGetCog(target, out cog, out mass, out cogNote))
            {
                o.Ok = false;
                o.Note = cogNote;
                return;
            }
            o.Cog = cog;

            IZSceneElement site = target as IZSceneElement;
            if (site == null)
            {
                o.Ok = false;
                o.Note = "该元素不支持 IZSceneElement，无法读写定位锚。";
                return;
            }

            IZMathMatrix posM = null, anchorM = null, globalM = null;
            try
            {
                posM = site.GetPositionTransform() as IZMathMatrix;
                anchorM = site.GetAnchorTransform() as IZMathMatrix;
                globalM = site.GetTransformToGlobal() as IZMathMatrix;
            }
            catch (Exception ex)
            {
                o.Ok = false;
                o.Note = "读取锚变换失败：" + ex.Message;
                return;
            }

            if (posM == null)
            {
                o.Ok = false;
                o.Note = "GetPositionTransform 返回空，无法定位锚。";
                return;
            }

            double px, py, pz;
            GetTrans(posM, out px, out py, out pz);
            double gx = 0, gy = 0, gz = 0;
            if (globalM != null) GetTrans(globalM, out gx, out gy, out gz);

            Diagnostics.Trace(string.Format(
                "[重心] {0} 质量(密度1.0)={1:F6} 重心=({2:F4},{3:F4},{4:F4}) 锚=({5:F4},{6:F4},{7:F4}) 几何=({8:F4},{9:F4},{10:F4})",
                o.Name, mass, cog[0], cog[1], cog[2], px, py, pz, gx, gy, gz));

            o.Ok = MoveAnchor(site, scene, posM, anchorM, cog, gx, gy, gz, o);
        }

        // ================================================================ 已验证的唯一方案

        /// <summary>
        /// 双写补偿：posM.t := 重心；anchorM.t += (重心 − posM.t原值)。
        /// 两项位移在实测模型（几何 = posM.t − anchorM.t）下严格抵消，几何零位移、锚落到重心。
        /// 失败回滚两个矩阵。
        /// </summary>
        private static bool MoveAnchor(IZSceneElement site, IZSceneDoc scene,
            IZMathMatrix posM, IZMathMatrix anchorM, double[] cog,
            double gx, double gy, double gz, Outcome o)
        {
            if (anchorM == null)
            {
                o.Note = "GetAnchorTransform 返回空，无法补偿几何位移。";
                return false;
            }

            double px, py, pz;
            GetTrans(posM, out px, out py, out pz);
            double dx = cog[0] - px, dy = cog[1] - py, dz = cog[2] - pz;   // Δ = 重心 − posM.t原值

            // 写入前快照：回滚以它为准，避免元素端回读的是活引用而被写入污染
            IZMathMatrix posOrig = null, anchorOrig = null;
            try
            {
                posOrig = posM.Copy() as IZMathMatrix;
                anchorOrig = anchorM.Copy() as IZMathMatrix;
            }
            catch (Exception ex) { o.Note = "矩阵快照失败：" + ex.Message; return false; }
            if (posOrig == null || anchorOrig == null) { o.Note = "矩阵快照为空。"; return false; }

            double ax, ay, az;
            GetTrans(anchorOrig, out ax, out ay, out az);

            try
            {
                IZMathMatrix nextA = anchorOrig.Copy() as IZMathMatrix;
                IZMathMatrix nextP = posOrig.Copy() as IZMathMatrix;
                if (nextA == null || nextP == null) { o.Note = "矩阵 Copy 返回空。"; return false; }

                nextA.SetTranslation(ax + dx, ay + dy, az + dz);
                nextP.SetTranslation(cog[0], cog[1], cog[2]);   // 锚到重心

                ZMathMatrix za = nextA as ZMathMatrix, zp = nextP as ZMathMatrix;
                if (za == null || zp == null) { o.Note = "矩阵不是 ZMathMatrix，无法写回。"; return false; }

                site.SetAnchorTransform(za);
                site.SetPositionTransform(zp);
                scene.Update();
            }
            catch (Exception ex)
            {
                o.Note = "写入锚变换失败：" + ex.Message;
                return false;
            }

            if (Verify(site, cog, gx, gy, gz, o)) return true;

            // 校验不通过 → 回滚到写入前状态
            try
            {
                ZMathMatrix za0 = anchorOrig as ZMathMatrix, zp0 = posOrig as ZMathMatrix;
                if (za0 != null) site.SetAnchorTransform(za0);
                if (zp0 != null) site.SetPositionTransform(zp0);
                scene.Update();

                double rx = gx, ry = gy, rz = gz;
                IZMathMatrix t3 = site.GetTransformToGlobal() as IZMathMatrix;
                if (t3 != null) GetTrans(t3, out rx, out ry, out rz);
                double back = Dist(rx, ry, rz, gx, gy, gz);
                o.Note += back <= MoveTol ? "（已回滚）" : "（⚠回滚未归位 " + (back * 1000).ToString("F3") + " mm）";
            }
            catch (Exception ex)
            {
                o.Note += "（⚠回滚失败：" + ex.Message + "）";
            }
            return false;
        }

        /// <summary>权威回读：锚的父系位置 ≈ 重心，且几何位移 ≈ 0。</summary>
        private static bool Verify(IZSceneElement site, double[] cog,
            double gx, double gy, double gz, Outcome o)
        {
            double px = 0, py = 0, pz = 0;
            double tx = gx, ty = gy, tz = gz;

            try
            {
                IZMathMatrix p2 = site.GetPositionTransform() as IZMathMatrix;
                if (p2 != null) GetTrans(p2, out px, out py, out pz);

                IZMathMatrix t2 = site.GetTransformToGlobal() as IZMathMatrix;
                if (t2 != null) GetTrans(t2, out tx, out ty, out tz);
            }
            catch (Exception ex)
            {
                o.Note += "回读失败：" + ex.Message;
                return false;
            }

            double err = Dist(px, py, pz, cog[0], cog[1], cog[2]);
            double shift = Dist(tx, ty, tz, gx, gy, gz);

            o.AnchorAfter = new double[] { px, py, pz };
            o.GeometryShift = shift;

            bool anchorOk = err <= PosTol;
            bool geometryKept = shift <= MoveTol;

            Diagnostics.Trace(string.Format(
                "[重心] {0} 锚偏差 {1:F6} m，几何位移 {2:F6} m → {3}（锚=({4:F3},{5:F3},{6:F3}) 几何=({7:F3},{8:F3},{9:F3}) 重心=({10:F3},{11:F3},{12:F3}) mm）",
                o.Name, err, shift,
                (anchorOk && geometryKept) ? "通过" : "不通过",
                px * 1000.0, py * 1000.0, pz * 1000.0,
                tx * 1000.0, ty * 1000.0, tz * 1000.0,
                cog[0] * 1000.0, cog[1] * 1000.0, cog[2] * 1000.0));

            if (anchorOk && geometryKept) return true;

            if (!geometryKept)
                o.Note += string.Format("几何位移 {0:F3} mm 超差；", shift * 1000.0);
            else
                o.Note += string.Format("锚偏差 {0:F3} mm 超差；", err * 1000.0);
            return false;
        }

        // ================================================================ 重心

        /// <summary>取重心：优先 IZPartProperty，其次 IZAssemblyProperty（同一 COM 对象直接 QI）。</summary>
        private static bool TryGetCog(IZElement elem, out double[] cog, out double mass, out string note)
        {
            cog = null;
            mass = 0;
            note = null;

            if (elem == null) { note = "目标元素为空。"; return false; }

            IZPartProperty partProp = null;
            IZAssemblyProperty asmProp = null;
            try { partProp = elem as IZPartProperty; } catch { }
            if (partProp == null)
            {
                try { asmProp = elem as IZAssemblyProperty; } catch { }
            }
            if (partProp == null && asmProp == null)
            {
                note = "该元素没有 IZPartProperty / IZAssemblyProperty 接口，取不到重心。";
                return false;
            }

            // 密度先给 1.0（重心位置与密度无关，只影响质量数值），失败再试 0（用零件自身密度）
            double[] densities = new double[] { 1.0, 0.0 };
            string lastError = null;

            foreach (double dens in densities)
            {
                double acc = 0, m = 0;
                object center = null, inertia = null;
                try
                {
                    if (partProp != null)
                        partProp.GetMassProperties(dens, false, 1e-4, out acc, out m, out center, out inertia);
                    else
                        asmProp.GetMassProperties(dens, false, 1e-4, out acc, out m, out center, out inertia);

                    double[] pt = ToPoint(center);
                    if (pt == null)
                    {
                        lastError = "重心点返回空（pvarCenterPt 类型未知）";
                        continue;
                    }

                    cog = pt;
                    mass = m;
                    if (dens != 1.0) note = "密度 1.0 失败，已改用零件自身密度";
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Diagnostics.Trace("GetMassProperties(density=" + dens + ") failed: " + ex.Message);
                }
            }

            note = "GetMassProperties 取重心失败：" + (lastError == null ? "未知原因" : lastError);
            return false;
        }

        /// <summary>pvarCenterPt 可能是 double[3]、object[3] 或 IZMathPoint。</summary>
        private static double[] ToPoint(object v)
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

        // ================================================================ 小工具

        private static void GetTrans(IZMathMatrix m, out double x, out double y, out double z)
        {
            double vx = 0, vy = 0, vz = 0;
            m.GetTranslation(out vx, out vy, out vz);
            x = vx; y = vy; z = vz;
        }

        private static double Dist(double ax, double ay, double az, double bx, double by, double bz)
        {
            double dx = ax - bx, dy = ay - by, dz = az - bz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string SafeName(IZElement elem)
        {
            if (elem == null) return "?";
            try { return string.IsNullOrEmpty(elem.Name) ? "?" : elem.Name; } catch { return "?"; }
        }
    }
}
