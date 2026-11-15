// 定位规则引擎（2026-11-15 移植自 caxa3dqrcode\InsertRule.cs；规则文件名换成本项目前缀）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace caxa3dBatchMaster
{
    /// <summary>
    /// 定位规则引擎（11-15 用户「参考坐标规则,按实际位置定位二维码」）。
    /// 规则文件格式（# 开头=注释）：
    ///   A4竖-----(-105,148.5)
    ///   A3横-----(-210,148.5)
    /// 规则坐标 = 1:1 图幅时的插入点（图幅左上角）。
    /// ★ 比例补偿与尺寸补偿同一纪律：图幅比例 1:N 时图纸坐标系是物理 mm 的 N 倍
    ///   （实测：1:2 图纸落 40 个单位才有 20 mm 成品）⇒ 插入点 = 规则坐标 × 分母 N。
    /// 图幅匹配：CRX getPaperSize 给物理宽高（mm），按「格式短边×长边 + 名称里的竖/横」
    ///   推出规则应有的 (w,h)，与实机值比对（±2 mm 容差）。
    /// </summary>
    internal static class InsertRule
    {
        public const string FileName = "caxa3dBatchMaster二维码定位规则.txt";

        public sealed class Entry
        {
            public string Name;         // 规则名（原样，如 A3横）
            public double X, Y;         // 1:1 基准坐标
            public double ExpectW, ExpectH; // 该规则对应的图幅物理宽高（mm）
        }

        /// <summary>ISO A 系短边×长边（mm）。</summary>
        private static readonly string[][] IsoSizes = new[]
        {
            new[] { "A4", "210", "297" },
            new[] { "A3", "297", "420" },
            new[] { "A2", "420", "594" },
            new[] { "A1", "594", "841" },
            new[] { "A0", "841", "1189" },
        };

        /// <summary>规则行解析：`名称-----（x,y)`（全半角括号/逗号都容错）。</summary>
        private static readonly Regex LineRx = new Regex(
            @"^\s*(?<name>[^#=\r\n]+?)\s*[-—]{2,}\s*[(（]\s*(?<x>-?\d+(?:\.\d+)?)\s*[,，]\s*(?<y>-?\d+(?:\.\d+)?)\s*[)）]\s*$",
            RegexOptions.Compiled);

        /// <summary>内置默认规则（两处都没有规则文件时播种用；与 caxa3dqrcode 基准一致）。</summary>
        public const string SeedContent =
            "#下面是1:1时候二维码定位坐标\r\n" +
            "A4竖-----(-105,148.5)\r\nA4横-----(-148.5,105)\r\n" +
            "A3横-----(-210,148.5)\r\nA2横-----(-297,210)\r\n" +
            "A1横-----(-420.5,297)\r\nA0横-----(-594.5,420.5)\r\n";

        /// <summary>
        /// 规则文件路径解析（2026-11-15 改按本项目 UserRuleFiles 两段式纪律，坑 178）：
        /// ① %APPDATA%\caxa3dBatchMaster\（用户可写，优先）；② 程序目录那份上拷/播种。
        /// 都不可得 = 规则不可用（第三态，回落兜底插入点）。
        /// </summary>
        public static string ResolvePath()
        {
            string note;
            string p = UserRuleFiles.Ensure(FileName, SeedContent, out note);
            if (!string.IsNullOrEmpty(p))
            {
                try { Diagnostics.Log("qr-rule: " + note); } catch { }
                return p;
            }
            return "";
        }

        /// <summary>加载并解析规则；返回 null = 规则不可用（err 说明原因）。</summary>
        public static List<Entry> Load(out string path, out string err)
        {
            path = ResolvePath();
            err = "";
            if (string.IsNullOrEmpty(path)) { err = "找不到规则文件（" + FileName + "）"; return null; }
            var list = new List<Entry>();
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) { err = "规则文件读取失败：" + ex.Message; return null; }
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                Match m = LineRx.Match(line);
                if (!m.Success) { err += "第 " + (i + 1) + " 行格式无法解析（已跳过）；"; continue; }
                Entry e = new Entry
                {
                    Name = m.Groups["name"].Value.Trim(),
                    X = double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture),
                    Y = double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                };
                FillExpect(e);
                if (e.ExpectW <= 0) { err += "第 " + (i + 1) + " 行名称「" + e.Name + "」认不出图幅规格（已跳过）；"; continue; }
                list.Add(e);
            }
            if (list.Count == 0) { err = err.Length == 0 ? "规则文件没有可解析的条目" : err; return null; }
            return list;
        }

        /// <summary>按规则名推图幅尺寸：名称含 A0~A4 + 竖/横（缺省按横）。</summary>
        private static void FillExpect(Entry e)
        {
            string name = e.Name ?? "";
            string fmt = null;
            foreach (string[] iso in IsoSizes)
                if (name.IndexOf(iso[0], StringComparison.OrdinalIgnoreCase) >= 0) { fmt = iso[0]; break; }
            if (fmt == null) return;
            double shortSide = 0, longSide = 0;
            foreach (string[] iso in IsoSizes)
                if (iso[0] == fmt) { shortSide = double.Parse(iso[1], CultureInfo.InvariantCulture); longSide = double.Parse(iso[2], CultureInfo.InvariantCulture); break; }
            bool portrait = name.IndexOf("竖", StringComparison.Ordinal) >= 0;
            e.ExpectW = portrait ? shortSide : longSide;
            e.ExpectH = portrait ? longSide : shortSide;
        }

        /// <summary>
        /// 按实机图幅物理宽高匹配规则。±2 mm 容差；多条命中取第一条（同名冲突时先到先得，
        /// 日志会把命中的条目与坐标如实打出）。
        /// </summary>
        public static bool TryMatch(List<Entry> rules, double paperW, double paperH,
            out Entry hit, out string why)
        {
            hit = null; why = "";
            if (rules == null || rules.Count == 0) { why = "规则列表为空"; return false; }
            if (paperW <= 1 || paperH <= 1) { why = "实机图幅宽高无效（w=" + paperW + " h=" + paperH + "）"; return false; }
            foreach (Entry e in rules)
            {
                if (Math.Abs(e.ExpectW - paperW) <= 2 && Math.Abs(e.ExpectH - paperH) <= 2)
                { hit = e; why = "命中「" + e.Name + "」（图幅 " + Format(paperW) + "x" + Format(paperH) + " mm）"; return true; }
            }
            why = "图幅 " + Format(paperW) + "x" + Format(paperH) + " mm 不在规则表中（现有 " +
                  rules.Count + " 条：" + Names(rules) + "）";
            return false;
        }

        private static string Names(List<Entry> rules)
        {
            var sb = new System.Text.StringBuilder();
            foreach (Entry e in rules) sb.Append(e.Name).Append(' ');
            return sb.ToString().TrimEnd();
        }

        public static string Format(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>「定位规则」按钮：用记事本打开生效路径的规则文件（不存在则先播种再开）。</summary>
        public static string OpenForEdit(Action<string> log)
        {
            string path = ResolvePath();
            if (string.IsNullOrEmpty(path))
            {
                try
                {
                    string dir = UserRuleFiles.AppDataDirectory;
                    if (string.IsNullOrEmpty(dir)) dir = UserRuleFiles.AssemblyDirectory;
                    path = Path.Combine(dir, FileName);
                    File.WriteAllText(path, SeedContent, new UTF8Encoding(true));
                    if (log != null) log("规则文件不存在，已按默认规则创建：" + path);
                }
                catch (Exception ex)
                {
                    if (log != null) log("规则文件创建失败：" + ex.Message);
                    return "";
                }
            }
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception ex) { if (log != null) log("打开记事本失败：" + ex.Message); return path; }
            if (log != null) log("已用记事本打开定位规则：" + path + "（编辑保存后，下一次运行自动生效）");
            return path;
        }
    }
}
