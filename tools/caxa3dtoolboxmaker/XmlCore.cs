using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Caxa3dToolboxMaker
{
    // ---------- 数据模型 ----------
    public class Attr
    {
        public string Key;
        public string Value;
        public Attr(string k, string v) { Key = k; Value = v; }
    }

    public enum CheckState { Same, Diff, Unreadable, Na }

    // ---------- XML 保格式内核 ----------
    // 字符串级解析/重建，不用 XmlDocument：保留 UTF-8 无 BOM、CRLF、属性顺序与自闭合结构
    public static class XmlCore
    {
        static readonly Regex AttrRx = new Regex(@"(?<k>[^\s=/]+)\s*=\s*""(?<v>[^""""]*)""", RegexOptions.Compiled);

        public static List<Attr> ParseAttrs(string attrText)
        {
            List<Attr> list = new List<Attr>();
            foreach (Match m in AttrRx.Matches(attrText))
                list.Add(new Attr(m.Groups["k"].Value, m.Groups["v"].Value));
            return list;
        }

        public static string Get(List<Attr> attrs, string key)
        {
            foreach (Attr a in attrs) if (a.Key == key) return a.Value;
            return null;
        }

        public static void Set(List<Attr> attrs, string key, string value)
        {
            foreach (Attr a in attrs) if (a.Key == key) { a.Value = value; return; }
            attrs.Add(new Attr(key, value));
        }

        // 整段读入内存模型
        public class Doc
        {
            public string Raw;                          // 原始文本（用于定位结构）
            public List<Attr> Cur;                      // CurNodeInfo 属性（有序）
            public List<List<Attr>> Params;             // ConfigureTable 的每个 Parameter（有序）
            public List<List<Attr>> Rows;               // DataTable 的每个 Row（有序）
            public List<string> ParamKeysUnion;         // 所有 Parameter 出现过的属性名并集（保序）
            public List<string> RowKeys;                // Row 列顺序（取首行）
        }

        public static Doc Load(string path)
        {
            string text = File.ReadAllText(path, new UTF8Encoding(false));
            Doc d = new Doc();
            d.Raw = text;

            // CurNodeInfo
            int i = text.IndexOf("<CurNodeInfo ");
            if (i < 0) throw new Exception("未找到 <CurNodeInfo>");
            int j = text.IndexOf('>', i);
            d.Cur = ParseAttrs(text.Substring(i, j - i));

            // Parameter 段
            d.Params = new List<List<Attr>>();
            d.ParamKeysUnion = new List<string>();
            Regex prx = new Regex(@"<Parameter\b[^>]*>[\s\S]*?</Parameter>");
            foreach (Match m in prx.Matches(text))
            {
                int a = m.Value.IndexOf(' ');
                int b = m.Value.IndexOf('>');
                List<Attr> pa = ParseAttrs(m.Value.Substring(a, b - a));
                d.Params.Add(pa);
                foreach (Attr at in pa) if (!d.ParamKeysUnion.Contains(at.Key)) d.ParamKeysUnion.Add(at.Key);
            }

            // Row 段
            d.Rows = new List<List<Attr>>();
            d.RowKeys = new List<string>();
            Regex rrx = new Regex(@"<Row\b[^>]*>[\s\S]*?</Row>");
            foreach (Match m in rrx.Matches(text))
            {
                int a = m.Value.IndexOf(' ');
                int b = m.Value.IndexOf('>');
                List<Attr> ra = ParseAttrs(m.Value.Substring(a, b - a));
                d.Rows.Add(ra);
                if (d.RowKeys.Count == 0) foreach (Attr at in ra) d.RowKeys.Add(at.Key);
            }
            return d;
        }

        static string BuildTag(string tag, List<Attr> attrs, List<string> onlyKeys)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('<').Append(tag);
            IEnumerable<Attr> seq = onlyKeys == null ? (IEnumerable<Attr>)attrs : FilterByKeys(attrs, onlyKeys);
            // 标签名后先补一个空格（与原格式一致：<CurNodeInfo name="...">）
            bool hasAny = false;
            foreach (Attr a in seq) { hasAny = true; break; }
            if (hasAny) sb.Append(' ');
            bool first = true;
            foreach (Attr a in seq)
            {
                if (!first) sb.Append(' ');
                sb.Append(a.Key).Append("=\"").Append(a.Value).Append('"');
                first = false;
            }
            sb.Append('>');
            return sb.ToString();
        }

        static IEnumerable<Attr> FilterByKeys(List<Attr> attrs, List<string> keys)
        {
            // 按 keys 给定顺序输出；attrs 里没有的键跳过（用户删掉的属性不重建）
            foreach (string k in keys)
                foreach (Attr a in attrs)
                    if (a.Key == k) { yield return a; break; }
        }

        // 保格式重建并写盘：先备份，再写 UTF-8 无 BOM + CRLF
        public static void Save(string path, Doc d, string backupDir)
        {
            string text = Render(d);

            // 备份（写盘前）
            if (!string.IsNullOrEmpty(backupDir) && File.Exists(path))
            {
                Directory.CreateDirectory(backupDir);
                string bak = Path.Combine(backupDir, Path.GetFileName(path) + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                File.Copy(path, bak, true);
            }
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        // round-trip 自检用：重建到字符串（不写盘）
        public static string Render(Doc d)
        {
            string text = d.Raw;

            // 1) 替换 CurNodeInfo 开标签
            int ci = text.IndexOf("<CurNodeInfo ");
            int cj = text.IndexOf('>', ci);
            text = text.Substring(0, ci) + BuildTag("CurNodeInfo", d.Cur, null) + text.Substring(cj + 1);

            // 2) 整段重建 Parameter（支持增删行）：首个 <Parameter 到末个 </Parameter> 一次替换。
            //    分隔串从原文捕获（本格式 </Parameter> 与下一个 <Parameter> 之间为空串），
            //    无增删时逐标签重建 + 原分隔串 = 字节级 round-trip 不变。
            Regex prx = new Regex(@"<Parameter\b[^>]*>[\s\S]*?</Parameter>");
            MatchCollection pm = prx.Matches(text);
            if (pm.Count > 0)
            {
                int pstart = pm[0].Index;
                int pend = pm[pm.Count - 1].Index + pm[pm.Count - 1].Length;
                string sep = pm.Count > 1
                    ? text.Substring(pm[0].Index + pm[0].Length, pm[1].Index - (pm[0].Index + pm[0].Length))
                    : "";
                StringBuilder pb = new StringBuilder();
                for (int i = 0; i < d.Params.Count; i++)
                {
                    if (i > 0) pb.Append(sep);
                    pb.Append(BuildTag("Parameter", d.Params[i], null));
                    pb.Append("\r\n</Parameter>");
                }
                text = text.Substring(0, pstart) + pb.ToString() + text.Substring(pend);
            }
            else if (d.Params.Count > 0)
            {
                // 原文无 Parameter：插到 </ConfigureTable> 之前
                int ci2 = text.IndexOf("</ConfigureTable>", StringComparison.Ordinal);
                if (ci2 < 0) throw new Exception("未找到 </ConfigureTable>，无法插入新增参数");
                StringBuilder pb = new StringBuilder();
                for (int i = 0; i < d.Params.Count; i++)
                {
                    pb.Append(BuildTag("Parameter", d.Params[i], null));
                    pb.Append("\r\n</Parameter>");
                }
                text = text.Substring(0, ci2) + pb.ToString() + text.Substring(ci2);
            }

            // 3) 整体重建 DataTable 主体（<DataTable ...>\r\n rows </DataTable>）
            int di = text.IndexOf("<DataTable ");
            int dj = text.IndexOf('>', di);
            int dclose = text.IndexOf("</DataTable>", dj, StringComparison.Ordinal);
            StringBuilder body = new StringBuilder();
            body.Append("\r\n");
            foreach (List<Attr> row in d.Rows)
            {
                body.Append(BuildTag("Row", row, d.RowKeys));
                body.Append("\r\n</Row>");
            }
            return text.Substring(0, dj + 1) + body.ToString() + text.Substring(dclose);
        }
    }
}
