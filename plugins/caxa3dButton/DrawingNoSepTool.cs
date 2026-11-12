// 图号分离（移植自 caxa3dtracker\DrawingNoSepTool.cs）：
//   把选中零部件的「名称」按所选规则（间隔符 + 字段顺序）拆段，回填到共享自定义属性表的
//   SC图号 / SC名称 / SC规格 / SC材料，并保存活动文档（.ics）。
//
// 本工程适配：
//   · 写通道用 SharedPropWriter.WriteNamedSharedProps（从 PartSpecFillTool 抽的共享表写入底座）
//   · RuleFileName 改为本工程名前缀（caxa3dButton图号分离规则.txt）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    internal static class DrawingNoSepTool
    {
        /// <summary>图号分离规则：分隔符 + 目标属性顺序（字段名已带 SC 前缀）。</summary>
        public sealed class Rule
        {
            public string Text;
            public char Separator;
            public string[] Fields;

            public override string ToString() { return Text; }
        }

        /// <summary>图号分离窗口当前所选规则（供「打开工程图」自动分离复用；为 null 时用规则 A）。</summary>
        public static Rule LastRule;

        private const string RuleFileName = "caxa3dButton图号分离规则.txt";

        // ============================================================ 规则加载

        /// <summary>加载规则：优先插件目录同名 .txt，解析到 ≥1 条则用；否则用内置 A~E。</summary>
        public static List<Rule> LoadRules()
        {
            var rules = new List<Rule>();
            string path = FindRuleFile();
            if (path != null)
            {
                try
                {
                    foreach (string line in File.ReadAllLines(path, System.Text.Encoding.UTF8))
                    {
                        Rule r;
                        if (TryParseRule(line, out r) && r != null) rules.Add(r);
                    }
                }
                catch (Exception ex) { Diagnostics.Log("drawing-nosep: 读取规则文件失败 " + ex.Message); }
            }
            if (rules.Count == 0) rules.AddRange(BuiltinRules());
            return rules;
        }

        private static IEnumerable<Rule> BuiltinRules()
        {
            yield return new Rule { Text = "A自制件.间隔符\" \"----\"SC图号 SC名称\"", Separator = ' ', Fields = new[] { "SC图号", "SC名称" } };
            yield return new Rule { Text = "B外购件.间隔符\"_\"----\"SC名称_SC规格_SC材料_SC图号\"", Separator = '_', Fields = new[] { "SC名称", "SC规格", "SC材料", "SC图号" } };
            yield return new Rule { Text = "C外购件.间隔符\"_\"----\"SC名称_SC规格_SC材料\"", Separator = '_', Fields = new[] { "SC名称", "SC规格", "SC材料" } };
            yield return new Rule { Text = "D外购件.间隔符\"_\"----\"SC名称_SC规格_SC图号\"", Separator = '_', Fields = new[] { "SC名称", "SC规格", "SC图号" } };
            yield return new Rule { Text = "E外购件.间隔符\"_\"----\"SC名称_SC规格\"", Separator = '_', Fields = new[] { "SC名称", "SC规格" } };
        }

        /// <summary>解析一行：A自制件.间隔符" "----"SC图号 SC名称"。字段名统一带 SC 前缀。</summary>
        public static bool TryParseRule(string line, out Rule rule)
        {
            rule = null;
            if (string.IsNullOrWhiteSpace(line)) return false;
            int p = line.IndexOf(".间隔符", StringComparison.Ordinal);
            if (p < 0) return false;
            string rest = line.Substring(p + 4).Trim();
            if (rest.Length < 3) return false;

            char quote = rest[0];
            if (quote != '"' && quote != '\'') return false;
            int endSep = rest.IndexOf(quote, 1);
            if (endSep <= 1) return false;
            string sepText = rest.Substring(1, endSep - 1);
            if (sepText.Length != 1) return false;
            char sep = sepText[0];

            int dash = rest.IndexOf("----", endSep, StringComparison.Ordinal);
            if (dash < 0) return false;
            string fieldPart = rest.Substring(dash + 4).Trim();
            if (fieldPart.Length < 2) return false;
            char q2 = fieldPart[0];
            int endFields = fieldPart.LastIndexOf(q2);
            if (endFields <= 0) return false;
            string fieldsText = fieldPart.Substring(1, endFields - 1);

            string[] raw = fieldsText.Split(sep);
            var fields = new List<string>();
            foreach (string f in raw)
            {
                string t = f.Trim();
                if (t.Length > 2 && t.StartsWith("SC", StringComparison.OrdinalIgnoreCase))
                    t = t.Substring(2);
                if (t.Length > 0) fields.Add("SC" + t);
            }
            if (fields.Count == 0) return false;

            rule = new Rule { Text = line.Trim(), Separator = sep, Fields = fields.ToArray() };
            return true;
        }

        /// <summary>在插件目录（程序集所在目录）逐级向上查找规则文件；找不到返回 null。</summary>
        private static string FindRuleFile()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                for (int i = 0; i < 4 && dir != null; i++)
                {
                    string p = Path.Combine(dir, RuleFileName);
                    if (File.Exists(p)) return p;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }

        // ============================================================ 分离

        /// <summary>按规则把名称拆段组成 SC 属性值；段数不符或规则为空时给出原因。</summary>
        public static bool TryBuildSepValues(string name, Rule rule, out Dictionary<string, string> values, out string error)
        {
            values = null;
            error = null;
            if (rule == null) { error = "请选择分离规则。"; return false; }

            string[] tokens = name.Split(rule.Separator);
            if (tokens.Length != rule.Fields.Length)
            {
                error = string.Format(
                    "按规则" + rule.Text + "应有 {0} 段（分隔符“{1}”），当前为 {2} 段。",
                    rule.Fields.Length, rule.Separator, tokens.Length);
                return false;
            }

            values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < tokens.Length; i++)
                values[rule.Fields[i]] = tokens[i].Trim();
            return true;
        }

        // ============================================================ 自动选规则

        /// <summary>
        /// 根据零件名自动选择图号分离规则：遍历规则，返回首个「分隔符出现在名称中 且 段数等于规则字段数 且 每段非空」的规则。
        /// 优先级按 rules 入参顺序（内置 A→B→C→D→E）。无匹配返回 null。
        /// </summary>
        public static Rule AutoSelectRule(string name, List<Rule> rules)
        {
            if (string.IsNullOrEmpty(name) || rules == null || rules.Count == 0) return null;
            foreach (Rule r in rules)
            {
                if (name.IndexOf(r.Separator) < 0) continue;
                string[] segs = name.Split(new[] { r.Separator });
                if (segs.Length != r.Fields.Length) continue;
                bool allNonEmpty = true;
                foreach (string s in segs) if (s.Trim().Length == 0) { allNonEmpty = false; break; }
                if (!allNonEmpty) continue;
                return r;
            }
            return null;
        }

        // ============================================================ 选件 + 执行

        /// <summary>取选中零件（任意件含虚拟件、含外部链接件均可）。返回选中元素名称。</summary>
        public static string GetSelectedPart(IZBaseApp app, out IZPart part, out string elemName, out string err)
        {
            part = null;
            elemName = "";
            err = null;

            string serr;
            IZSceneDoc scene = ProbeCommon.GetScene(app, out serr);
            if (scene == null) { err = "未连接到 CAXA 3D 设计环境：" + serr; return null; }

            List<object> sel = ProbeCommon.ReadSelectionRaw(scene, "drawing-nosep", m => Diagnostics.Log("drawing-nosep: " + m));
            if (sel.Count == 0) { err = "请先在 3D 设计树中选中要做图号分离的零部件。"; return null; }

            IZElement elem = null;
            foreach (object o in sel)
            {
                var el = o as IZElement;
                if (el == null) continue;
                int type = -1;
                try { type = (int)el.Type; } catch { }
                if (type == 2 /*Z_ELEMENT_ASSEMBLY*/) continue;   // 装配排除
                var p = o as IZPart;
                if (p == null) continue;
                elem = el; part = p;
                elemName = ProbeCommon.Safe(() => el.Name, "<无名>");
                break;
            }
            if (part == null) { err = "选中对象里没有零件（装配已排除）——请直接选中零部件后再点。"; return null; }
            return elemName;
        }

        /// <summary>执行图号分离（选中件流程）。</summary>
        public static string Run(IZBaseApp app, Rule rule, string sourceNameOverride, Action<string> log)
        {
            const string tag = "drawing-nosep";
            IZPart part;
            string elemName, err;
            string selName = GetSelectedPart(app, out part, out elemName, out err);
            if (err != null) return "图号分离失败：" + err;

            string name = string.IsNullOrEmpty(sourceNameOverride) ? selName : sourceNameOverride.Trim();
            if (name.Length == 0) return "图号分离失败：分离名称为空（选中件无名称且未手动输入）。";

            log(tag + ": 选中件「" + elemName + "」，分离名称=\"" + name + "\"，规则=" + (rule == null ? "<无>" : rule.Text));
            return SeparatePart(app, app.ActiveDoc, (IZElement)part, name, rule, log);
        }

        /// <summary>
        /// 核心：把 name 按 rule 拆段写共享属性表并保存 doc。doc 为空则用活动文档保存。
        /// </summary>
        public static string SeparatePart(IZBaseApp app, IZDoc doc, IZElement elem, string name, Rule rule, Action<string> log)
        {
            const string tag = "drawing-nosep";
            Dictionary<string, string> values;
            string bErr;
            if (!TryBuildSepValues(name, rule, out values, out bErr))
                return "图号分离失败（规则校验）：" + bErr;

            foreach (var kv in values) log(tag + ":   分离 → " + kv.Key + " = \"" + kv.Value + "\"");

            string wErr;
            if (!SharedPropWriter.WriteNamedSharedProps(elem, values, tag, log, out wErr))
                return "图号分离：属性组已生成但未写入共享表：" + wErr;

            // 保存文档（.ics），确保属性持久化
            try
            {
                if (doc == null) doc = app.ActiveDoc;
                if (doc != null) { doc.Save(); log(tag + ": 文档已保存"); }
                else log(tag + ": 文档为空，跳过保存");
            }
            catch (Exception ex) { log(tag + ": 保存文档 EX " + ProbeCommon.Hr(ex)); }

            return "图号分离完成：规则「" + rule.Text + "」写入 " + values.Count + " 个 SC 属性并保存文档。";
        }
    }
}
