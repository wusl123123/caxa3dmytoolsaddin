// 取证探针公共底座（全部只读，不改图）。
// 口径：
//   1. ICAPI 长度＝米；CSV 同时给米原值与毫米（×1000），不折算、不丢精度。
//   2. 第三态纪律：读不到记 <读不到>/<FAIL:hr>/N/A，绝不折算成 0；空集与读失败分开记账。
//   3. 选择集按 IUnknown 指针去重：同一对象可能被多个通道各报一次。
//   4. 树走查有深度＋节点双预算，撞顶单独计「截断」，不与「真没有」混账。
// 槽位/IID 权威＝_probe-tools\out\tlb\2027\members.csv；interop 可用性＝caxa-interop-check.ps1。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    /// <summary>树上一个节点：原始 COM 对象 ＋ 已读到的身份（读不到的字段留第三态文案）。</summary>
    internal sealed class TreeNode
    {
        public object Raw;
        public int Depth;
        public IZElement Elem;          // QI 失败为 null（平面/原点等）
        public string Type = "<读不到>";
        public int Id = -1;
        public string Name = "<读不到>";
        public string Indent { get { return new string(' ', Depth * 2); } }
    }

    internal static class ProbeCommon
    {
        public const double MeterToMm = 1000.0;
        public const int MaxDepth = 12;
        public const int MaxNodes = 5000;

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

        /// <summary>米原值（R 格式，逐位可复核）。</summary>
        public static string Meters(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>米→毫米，6 位小数（与参数表探针同口径）。</summary>
        public static string Mm(double meters)
        {
            return (meters * MeterToMm).ToString("F6", CultureInfo.InvariantCulture);
        }

        public static string Int(int v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- CSV

        public static void WriteCsv(string path, string header, List<string[]> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(header);
            foreach (var r in rows)
            {
                for (int i = 0; i < r.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Csv(r[i])); }
                sb.AppendLine();
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // BOM：Excel 中文不乱码
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new char[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
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
        /// 每条通道各自记数与失败原因：某条读不到不影响其余三条（第三态不折算成空）。
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

            try
            {
                var ctx = sm.SelectionUIContext;   // 槽29：PROFILE(2)＝正在草图编辑态
                log(tag + ": SelectionUIContext=" + ctx + "(" + (int)ctx + ")" +
                    ((int)ctx == 2 ? " ⇒ 草图/轮廓编辑态" : " ⇒ 非草图编辑态"));
            }
            catch (Exception ex) { log(tag + ": SelectionUIContext FAIL " + Hr(ex)); }

            try { log(tag + ": SelectionFilterType=" + sm.SelectionFilterType); }   // 槽8
            catch (Exception ex) { log(tag + ": SelectionFilterType FAIL " + Hr(ex)); }

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

        /// <summary>对象身份描述：RCW  coclass 名 ＋ IUnknown 指针（实机排错时这两项就是铁证）。</summary>
        public static string Describe(object o)
        {
            if (o == null) return "<null>";
            string tn = Safe(() => o.GetType().FullName, "<读不到>");
            IntPtr p = UnknownPtr(o);
            return tn + " @0x" + p.ToInt64().ToString("X");
        }

        // ---------------------------------------------------------------- 元素树

        /// <summary>
        /// 从场景根递归走查元素树。GetChildren 对平面/原点/部分特征抛 E_FAIL ⇒ 第三态，
        /// 记一行日志跳过该分支，不伪造子节点。撞深度/节点预算记 truncated（由调用方打印）。
        /// </summary>
        public static List<TreeNode> EnumerateTree(IZSceneDoc scene, string tag, Action<string> log,
            out int truncated)
        {
            int trunc = 0;
            var list = new List<TreeNode>();
            object[] roots = null;
            try { roots = scene.GetChildElements() as object[]; }   // IZSceneDoc.GetChildElements 槽7
            catch (Exception ex) { log(tag + ": GetChildElements FAIL " + Hr(ex)); }
            if (roots == null) log(tag + ": GetChildElements 返回非数组/空（第三态）");
            else
            {
                int nodes = 0;
                foreach (object k in roots) Walk(k, 0, list, log, tag, ref nodes, ref trunc);
            }
            truncated = trunc;
            return list;
        }

        private static void Walk(object node, int depth, List<TreeNode> into, Action<string> log,
            string tag, ref int nodes, ref int truncated)
        {
            if (node == null) return;
            if (depth > MaxDepth || nodes >= MaxNodes) { truncated++; return; }
            nodes++;

            var tn = new TreeNode { Raw = node, Depth = depth, Elem = node as IZElement };
            if (tn.Elem != null)
            {
                tn.Type = Safe(() => tn.Elem.Type.ToString(), "<读不到>");
                try { tn.Id = tn.Elem.Id; } catch (Exception ex) { log(tag + ": Id FAIL " + Hr(ex)); }
                tn.Name = Safe(() => tn.Elem.Name, "<读不到>");
            }
            into.Add(tn);

            if (tn.Elem == null) return;
            object[] kids = null;
            try { kids = tn.Elem.GetChildren() as object[]; }   // IZElement.GetChildren 槽13
            catch (Exception ex) { log(tag + ": " + tn.Indent + "GetChildren FAIL（第三态，跳过）" + Hr(ex)); }
            if (kids == null) return;
            foreach (object k in kids) Walk(k, depth + 1, into, log, tag, ref nodes, ref truncated);
        }

        // ---------------------------------------------------------------- 包围盒数学

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

        // ---------------------------------------------------------------- vtable 底座

        /// <summary>槽位函数地址。槽号必须来自 members.csv（＝DeclIndex+7），绝不由 interop 元数据推算。</summary>
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

        /// <summary>把 COM 对象转成原始 IUnknown 指针（调用方负责 Release）。</summary>
        public static IntPtr ToUnknown(object o)
        {
            return o == null ? IntPtr.Zero : Marshal.GetIUnknownForObject(o);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int FnGetObj(IntPtr self, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int FnVoid(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int FnGetInt(IntPtr self, out int pv);

        /// <summary>调「无参 [out,retval] 接口指针」形态；失败返回 HRESULT，不抛。</summary>
        public static int CallGetObj(IntPtr pIface, int slot, out IntPtr ppv)
        {
            return Fn<FnGetObj>(pIface, slot)(pIface, out ppv);
        }
    }
}