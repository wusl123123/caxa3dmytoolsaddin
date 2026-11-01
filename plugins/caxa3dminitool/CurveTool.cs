using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>
    /// 「导入3D曲线坐标数据」实现：解析 txt 坐标 → 创建 3D 曲线（IZWire）。
    /// 依据官方 C# 样例 SelectionTool.cs:486-490：CreateInterpolationSpline 收 double[] 平铺 XYZ。
    /// 文件坐标按毫米书写，写入时换算为内部单位（×0.001）。
    /// </summary>
    internal static class CurveTool
    {
        /// <summary>毫米 → 内部长度单位。</summary>
        private const double MmToInternal = 0.001;

        public static int ImportWireFromText(IZBaseApp app, string path,
            out string error, out int pointCount, out int crvId, out string detail)
        {
            error = null;
            crvId = 0;
            pointCount = 0;
            detail = "";

            IZSceneDoc scene = FeatureTool.GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是设计（场景）环境，请切回 3D 设计环境后再试。";
                return 0;
            }

            if (!File.Exists(path))
            {
                error = "坐标文件不存在：" + path;
                return 0;
            }

            double[] flat;
            int skipped;
            if (!TryParsePoints(path, out flat, out pointCount, out skipped, out error)) return 0;

            IZWire wire = null;
            try { wire = scene.CreateWire(); } catch { }
            if (wire == null)
            {
                try
                {
                    IZAssembly tmp = scene.CreateAssembly();
                    if (tmp != null) wire = tmp.CreateWire();
                }
                catch { }
            }
            if (wire == null)
            {
                error = "创建 3D 曲线容器失败（IZSceneDoc.CreateWire 返回空）。";
                return 0;
            }

            int before = 0;
            try { before = wire.GetCurvesCount(); } catch { }

            double[] zeroDir = new double[] { 0, 0, 0, 0 };
            try
            {
                crvId = wire.CreateInterpolationSpline(flat, zeroDir, zeroDir, 0);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("CreateInterpolationSpline 失败，改用折线：" + ex.Message);
                crvId = 0;
            }

            if (crvId <= 0)
            {
                try { wire.CreateLines(flat); } catch (Exception ex) { error = "创建曲线失败：" + ex.Message; return 0; }
            }

            try { scene.Update(); } catch { }

            // 权威判定：曲线数增加 + 样条点数与文件点数一致
            int after = 0;
            try { after = wire.GetCurvesCount(); } catch { }
            int pts = 0;
            if (crvId > 0)
            {
                try { pts = wire.GetInterpolationSplinePointsCount(crvId); } catch { }
            }

            string name = "?";
            try { name = ((IZElement)wire).Name; } catch { }

            if (after <= before)
            {
                error = string.Format("曲线未生成（创建前 {0} 条，创建后 {1} 条）。", before, after);
                return 0;
            }

            detail = string.Format("文件 {0}，解析 {1} 个点{2}；曲线名 {3}，曲线数 {4}→{5}，样条点数 {6}",
                Path.GetFileName(path), pointCount,
                skipped > 0 ? string.Format("（跳过无效行 {0}）", skipped) : "",
                name, before, after, pts);

            Diagnostics.Trace(string.Format("导入 3D 曲线：{0}；ID={1}", detail, crvId));
            return pointCount;
        }

        /// <summary>
        /// 解析坐标文本：
        ///  每行一个点，x y z 用空格/逗号/分号/制表符分隔；# 或 // 开头为注释；空行忽略。
        ///  坐标按毫米，返回值为平铺 double[]（已换算成内部单位）。
        /// </summary>
        private static bool TryParsePoints(string path, out double[] flat,
            out int pointCount, out int skipped, out string error)
        {
            flat = null;
            pointCount = 0;
            skipped = 0;
            error = null;

            List<double> values = new List<double>();
            try
            {
                using (StreamReader reader = new StreamReader(path, System.Text.Encoding.Default, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string s = line.Trim();
                        if (s.Length == 0) continue;
                        if (s.StartsWith("#") || s.StartsWith("//")) continue;

                        string[] parts = s.Split(new char[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3) { skipped++; continue; }

                        double x, y, z;
                        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                        {
                            skipped++;
                            continue;
                        }

                        values.Add(x * MmToInternal);
                        values.Add(y * MmToInternal);
                        values.Add(z * MmToInternal);
                    }
                }
            }
            catch (Exception ex)
            {
                error = "读取坐标文件失败：" + ex.Message;
                return false;
            }

            if (values.Count < 6)
            {
                error = string.Format("有效坐标点不足（至少 2 个点，实际 {0} 个）。", values.Count / 3);
                return false;
            }

            pointCount = values.Count / 3;
            flat = values.ToArray();
            return true;
        }
    }
}
