// 图号分离（11-06 用户指令 6）：按「图号分离规则」把零件名切成若干段，逐段填回表格的属性列。
// 规则文件格式与 caxa3dcodenameextraction 完全一致（一行一条，例：A自制件.间隔符" "----"SC图号 SC名称"），
// 解析算法照抄参考实现（见 codenameextraction MainForm.cs 的 TryParseRule），不另起一套语法。
// 生效路径由 UserRuleFiles 统一管（%APPDATA%\caxa3dBatchMaster\ 优先，程序目录只做播种源）。
//
// 用户选定的两条口径（本轮问答原文）：
//   ·「按 A→E 顺序自动试配段数」＝每条规则用它自己的分隔符切一次，切出来的段数==该规则字段数即命中，第一条命中就用它；
//   ·「只填表格单元格＋三色标记」＝**不写 3D 自定义属性、不保存装配体**，结果只落表格，颜色走 CellState（成功绿／失败红）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace caxa3dBatchMaster
{
    /// <summary>一条图号分离规则：原文 + 分隔符 + 目标字段名（顺序即段位顺序）。</summary>
    internal sealed class SplitRule
    {
        public string Text = "";
        public char Separator;
        public string[] Fields = new string[0];

        public override string ToString() { return Text; }

        public string Describe()
        {
            return "分隔符「" + Separator + "」→ " + string.Join("、", Fields);
        }
    }

    /// <summary>一次分离的结果。</summary>
    internal sealed class SplitOutcome
    {
        public bool Matched;
        public SplitRule Rule;
        public int Segments;                       // 实际切出的段数（命中时==Rule.Fields.Length）
        public Dictionary<string, string> Values;  // 字段名 → 切出来的值（保持原文，不 Trim 掉空格以外的东西）
        public string Reason = "";                 // 没命中时：为什么没命中（试过几条规则、各切出几段）
    }

    internal static class SplitRules
    {
        public const string FileName = "caxa3dBatchMaster图号分离规则.txt";

        private const string Seed =
"A自制件.间隔符\" \"----\"SC图号 SC名称\"\r\n" +
"B外购件.间隔符\"_\"----\"SC名称_SC规格_SC材料_SC图号\"\r\n" +
"C外购件.间隔符\"_\"----\"SC名称_SC规格_SC材料\"\r\n" +
"D外购件.间隔符\"_\"----\"SC名称_SC规格_SC图号\"\r\n" +
"E外购件.间隔符\"_\"----\"SC名称_SC规格\"\r\n";

        private static List<SplitRule> _rules;
        private static string _path;
        private static DateTime _stamp = DateTime.MinValue;
        private static string _note = "";
        private static int _skipped;

        public static string EffectivePath { get { try { ReloadIfChanged(); } catch { } return _path ?? "<不可得>"; } }
        public static string LastNote { get { return _note; } }
        public static int SkippedLines { get { ReloadIfChanged(); return _skipped; } }

        /// <summary>文件变了或首次调用时重载；返回一句要进日志的说明（未变更返回 null）。</summary>
        public static string ReloadIfChanged(bool force = false)
        {
            string known = _path;
            DateTime oldStamp = _stamp;
            DateTime stamp = DateTime.MinValue;
            if (known != null && File.Exists(known))
            {
                try { stamp = File.GetLastWriteTime(known); } catch { stamp = DateTime.MinValue; }
            }
            if (!force && _rules != null && stamp != DateTime.MinValue && stamp == oldStamp) return null;

            string note;
            string path = UserRuleFiles.Ensure(FileName, Seed, out note);
            var list = new List<SplitRule>();
            int bad = 0;
            try
            {
                if (path != null && File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        SplitRule r;
                        if (TryParse(line, out r)) list.Add(r);
                        else bad++;
                    }
                }
            }
            catch (Exception ex) { note = "读取规则文件失败：" + ex.Message + "｜" + note; }

            if (list.Count == 0)
            {
                list.AddRange(Builtin());
                note += "｜文件里一条都没解析出来，本轮用内置 5 条（A-E）";
            }

            DateTime newStamp = DateTime.MinValue;
            if (path != null && File.Exists(path))
            {
                try { newStamp = File.GetLastWriteTime(path); } catch { newStamp = DateTime.MinValue; }
            }
            bool changed = _rules == null || list.Count != _rules.Count || newStamp != oldStamp || path != known;
            _rules = list; _path = path; _stamp = newStamp; _note = note; _skipped = bad;
            string text = "图号分离规则：" + note + "｜生效条数 " + list.Count
                + (bad > 0 ? "｜格式不合被跳过 " + bad + " 行" : "");
            return force || changed ? text : null;
        }

        public static List<SplitRule> Current()
        {
            try { ReloadIfChanged(); } catch { }
            return _rules ?? (_rules = new List<SplitRule>(Builtin()));
        }

        public static string InitAndDescribe()
        {
            string text = ReloadIfChanged(true);
            return text ?? ("图号分离规则：生效路径＝" + EffectivePath + "｜生效条数 " + Current().Count);
        }

        public static bool OpenEffectiveFile(out string error)
        {
            try { ReloadIfChanged(); } catch { }
            return UserRuleFiles.Open(_path, out error);
        }

        private static IEnumerable<SplitRule> Builtin()
        {
            foreach (string line in Seed.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                SplitRule r;
                if (TryParse(line, out r)) yield return r;
            }
        }

        /// <summary>解析一行：A自制件.间隔符" "----"SC图号 SC名称"（算法与 codenameextraction 一致）。</summary>
        public static bool TryParse(string line, out SplitRule rule)
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

            var fields = new List<string>();
            foreach (string f in fieldsText.Split(sep))
            {
                string t = f.Trim();
                // 去掉已有的 SC 前缀再统一补回：规则里写「图号」还是「SC图号」都归一成 SC图号
                if (t.Length > 2 && t.StartsWith("SC", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
                if (t.Length > 0) fields.Add("SC" + t);
            }
            if (fields.Count == 0) return false;

            rule = new SplitRule { Text = line.Trim(), Separator = sep, Fields = fields.ToArray() };
            return true;
        }
    }

    internal static class DrawingNoSplitter
    {
        /// <summary>按 A→E 顺序试配：用该规则自己的分隔符切一次，段数==字段数即命中，第一条命中就返回。
        /// 命中还要求每一段都非空（切出空串说明名字里根本没有那一截，属于假命中）。</summary>
        public static SplitOutcome TrySplit(string partName)
        {
            var res = new SplitOutcome { Values = new Dictionary<string, string>(StringComparer.Ordinal) };
            if (string.IsNullOrWhiteSpace(partName))
            {
                res.Reason = "零件名为空";
                return res;
            }
            string name = partName.Trim();
            var tried = new List<string>();
            foreach (SplitRule r in SplitRules.Current())
            {
                string[] seg = name.Split(r.Separator);
                if (seg.Length != r.Fields.Length)
                {
                    tried.Add(r.Text.Substring(0, Math.Min(1, r.Text.Length)) + "：" + seg.Length + "段≠" + r.Fields.Length);
                    continue;
                }
                bool anyEmpty = false;
                for (int i = 0; i < seg.Length; i++) if (string.IsNullOrWhiteSpace(seg[i])) { anyEmpty = true; break; }
                if (anyEmpty)
                {
                    tried.Add(r.Text.Substring(0, Math.Min(1, r.Text.Length)) + "：段数对但有空段");
                    continue;
                }
                res.Matched = true;
                res.Rule = r;
                res.Segments = seg.Length;
                for (int i = 0; i < seg.Length; i++) res.Values[r.Fields[i]] = seg[i].Trim();
                return res;
            }
            res.Reason = "试了 " + tried.Count + " 条规则都不合（" + string.Join("；", tried.ToArray()) + "）";
            return res;
        }
    }
}
