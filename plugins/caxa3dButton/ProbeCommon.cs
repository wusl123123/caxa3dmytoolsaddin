// 取证探针公共底座（全部只读，不改图）。
// 从 caxa3dtracker\ProbeCommon.cs 精简而来，只保留 DesignEnvTool 所需的最小方法：
//   Hr / Safe / ReadSelectionRaw / Flatten / UnknownPtr / GetScene。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    internal static class ProbeCommon
    {
        public const double MeterToMm = 1000.0;

        // ---------------------------------------------------------------- 记账文案

        public static string Hr(Exception ex)
        {
            var c = ex as COMException;
            return c != null ? "0x" + c.ErrorCode.ToString("X8") : ex.GetType().Name;
        }

        public static string Hr(int hr)
        {
            return "0x" + hr.ToString("X8");
        }

        public static string Safe(Func<string> f, string fallback)
        {
            try { return f() ?? ""; }
            catch (Exception ex) { return fallback + "(" + Hr(ex) + ")"; }
        }

        // ---------------------------------------------------------------- 场景 / 选择集

        public static IZSceneDoc GetScene(IZBaseApp app, out string error)
        {
            error = null;
            if (app == null) { error = "未拿到 CAXA 站点对象（IZBaseApp）"; return null; }
            try
            {
                IZSceneDoc scene = app.ActiveDoc as IZSceneDoc;   // IZBaseApp.get_ActiveDoc 槽11
                if (scene == null) error = "活动文档不是 3D 场景（无活动文档或处于图纸环境）";
                return scene;
            }
            catch (Exception ex)
            {
                error = "读活动文档失败 " + Hr(ex) + "：" + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 四条选择通道全读（槽19/10/28/30），展平后按 IUnknown 去重。
        /// </summary>
        public static List<object> ReadSelectionRaw(IZSceneDoc scene, string tag, Action<string> log)
        {
            var result = new List<object>();
            var seen = new HashSet<IntPtr>();
            if (scene == null) return result;

            IZSelectionMgr sm = null;
            try { sm = scene.SelectionMgr; }   // IZSceneDoc.get_SelectionMgr 槽27
            catch (Exception ex) { log(tag + ": SelectionMgr FAIL " + Hr(ex)); }
            if (sm == null) { log(tag + ": SelectionMgr 为 null（第三态）"); return result; }

            ReadChannel(tag, "GetSelectedElementsZArray(19)", () => sm.GetSelectedElementsZArray(), result, seen, log);
            ReadChannel(tag, "GetSelectedElements(10)", () => sm.GetSelectedElements(), result, seen, log);
            ReadChannel(tag, "GetTrueSelectedElements(28)", () => sm.GetTrueSelectedElements(), result, seen, log);
            ReadChannel(tag, "GetSelectedCurves(30)", () => sm.GetSelectedCurves(), result, seen, log);

            log(tag + ": 选择集合计（去重后）" + result.Count + " 个对象");
            return result;
        }

        private static void ReadChannel(string tag, string what, Func<object> read,
            List<object> result, HashSet<IntPtr> seen, Action<string> log)
        {
            object raw;
            try { raw = read(); }
            catch (Exception ex) { log(tag + ": " + what + " FAIL " + Hr(ex) + "（第三态）"); return; }

            var flat = new List<object>();
            Flatten(raw, flat, 0);
            int added = 0;
            foreach (object o in flat)
            {
                IntPtr p = UnknownPtr(o);
                if (p != IntPtr.Zero && !seen.Add(p)) continue;
                if (p == IntPtr.Zero && result.Contains(o)) continue;
                result.Add(o);
                added++;
            }
            log(tag + ": " + what + " 展平 " + flat.Count + " 个 / 新增 " + added + " 个");
        }

        /// <summary>ZArray / object[] / 单个对象 一律展平；嵌套深度上限 8 防 runaway。</summary>
        public static void Flatten(object raw, List<object> into, int depth)
        {
            if (raw == null || depth > 8) return;

            var za = raw as ZArray;
            if (za != null)
            {
                int count;
                try { za.Count(out count); } catch { return; }
                for (int i = 0; i < count; i++)
                {
                    object inner;
                    try { za.Get(i, out inner); } catch { continue; }
                    Flatten(inner, into, depth + 1);
                }
                return;
            }

            var arr = raw as Array;
            if (arr != null && !(raw is byte[]))
            {
                foreach (object o in arr) Flatten(o, into, depth + 1);
                return;
            }

            if (raw is string || raw is double || raw is float || raw is int || raw is bool) return;
            into.Add(raw);
        }

        /// <summary>取 IUnknown 指针用于同一性比较；取完立刻 Release（不持有）。</summary>
        public static IntPtr UnknownPtr(object o)
        {
            if (o == null) return IntPtr.Zero;
            try
            {
                IntPtr p = Marshal.GetIUnknownForObject(o);
                if (p != IntPtr.Zero) Marshal.Release(p);
                return p;
            }
            catch { return IntPtr.Zero; }
        }

        // ---------------------------------------------------------------- vtable 底座

        /// <summary>槽位函数地址。槽号必须来自 members.csv（＝DeclIndex+7）。</summary>
        public static IntPtr SlotFn(IntPtr pIface, int slot)
        {
            IntPtr vt = Marshal.ReadIntPtr(pIface);
            return Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
        }

        public static T Fn<T>(IntPtr pIface, int slot) where T : class
        {
            return Marshal.GetDelegateForFunctionPointer(SlotFn(pIface, slot), typeof(T)) as T;
        }

        public static int QI(IntPtr pUnk, Guid iid, out IntPtr ppv)
        {
            return Marshal.QueryInterface(pUnk, ref iid, out ppv);
        }

        // ---------------------------------------------------------------- 包围盒底座（移植自 caxa3dtracker\ProbeCommon.cs）

        /// <summary>
        /// 把「6 个数的 VARIANT 数组」解成 double[]：minX,minY,minZ,maxX,maxY,maxZ。
        /// COM 可能给 float[] / double[] / object[] / SAFEARRAY / ZArray，全部兼容；解不出返回 null。
        /// </summary>
        public static double[] ToDoubleArray(object raw)
        {
            if (raw == null) return null;

            var d = raw as double[];
            if (d != null) return d;

            var f = raw as float[];
            if (f != null)
            {
                var r = new double[f.Length];
                for (int i = 0; i < f.Length; i++) r[i] = f[i];
                return r;
            }

            var za = raw as ZArray;
            if (za != null)
            {
                int count;
                za.Count(out count);
                var rz = new double[count];
                for (int i = 0; i < count; i++)
                {
                    object o;
                    za.Get(i, out o);
                    rz[i] = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                }
                return rz;
            }

            var a = raw as Array;
            if (a != null)
            {
                var ra = new double[a.Length];
                for (int i = 0; i < a.Length; i++) ra[i] = Convert.ToDouble(a.GetValue(i), CultureInfo.InvariantCulture);
                return ra;
            }
            return null;
        }

        /// <summary>
        /// 局部 AABB 的 8 个角点经 GetTransformToGlobal 搬到世界后再求 AABB，
        /// 这样旋转过的零件/特征也能给出世界轴向真实尺寸。变换失败返回 false（调用方退回局部值）。
        /// 移植自 addin\caxa3dsizeboxread\SizeBoxTool.cs（两工程 interop 字节一致，md5 29a23396…）。
        /// </summary>
        public static bool TryBoxToWorld(IZBaseApp app, object elem, double[] b, out double[] world6)
        {
            world6 = null;
            if (b == null || b.Length < 6) return false;

            var se = elem as IZSceneElement;
            var mu = app as IZMathUtility;
            if (se == null || mu == null) return false;

            ZMathMatrix m;
            try { m = se.GetTransformToGlobal(); } catch { return false; }
            if (m == null) return false;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
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

            if (Math.Abs(maxX - minX) < 1e-12 && Math.Abs(maxY - minY) < 1e-12 && Math.Abs(maxZ - minZ) < 1e-12)
                return false;
            world6 = new double[] { minX, minY, minZ, maxX, maxY, maxZ };
            return true;
        }

        private static bool TryTransformPoint(IZMathUtility mu, ZMathMatrix m,
            double x, double y, double z, out double ox, out double oy, out double oz)
        {
            ox = oy = oz = 0;
            try
            {
                ZMathPoint p;
                try { p = mu.CreateMathPoint(new double[] { x, y, z }); }
                catch { p = mu.CreateMathPoint(new object[] { x, y, z }); }
                if (p == null) return false;
                p.TransformBy(m);
                ox = p.X; oy = p.Y; oz = p.Z;
                return true;
            }
            catch { return false; }
        }
    }
}
