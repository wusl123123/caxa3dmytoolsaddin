using System;
using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Text.RegularExpressions;

namespace Caxa3dToolboxMaker
{
    // ---------- 三方比对探针（ics 内部名 / xlsx GUID） ----------
    public static class MetaProbe
    {
        // 扫描 OLE2 字节流里 UTF-16LE 可读串，取以 .ics 结尾且含 '\' 的路径末尾文件名
        public static string ReadIcsInnerName(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            byte[] d;
            try { d = File.ReadAllBytes(path); }
            catch { return null; }

            for (int offset = 0; offset <= 1; offset++)
            {
                StringBuilder sb = new StringBuilder();
                string best = null;
                for (int i = offset; i + 1 < d.Length; i += 2)
                {
                    int cp = d[i] | (d[i + 1] << 8);
                    bool ok = (cp >= 0x20 && cp <= 0x7e) || (cp >= 0x4e00 && cp <= 0x9fff)
                              || (cp >= 0xff00 && cp <= 0xffef) || (cp >= 0x3000 && cp <= 0x303f);
                    if (ok) { sb.Append((char)cp); continue; }
                    if (sb.Length >= 8)
                    {
                        string s = sb.ToString();
                        if (s.IndexOf('\\') >= 0 && s.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
                        {
                            string n = s.Substring(s.LastIndexOf('\\') + 1);
                            if (n.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) best = n;
                        }
                    }
                    sb.Length = 0;
                }
                if (best != null) return best;
            }
            return null; // 读不到 = 第三态
        }

        // ics 内是否有 GUID 字段（实测本样本任何端序/编码均 0 命中 -> 恒 Na）
        public static string ReadIcsGuid(string path)
        {
            return null; // 规则第46节未登记 ics 内 GUID；本轮实测 0 命中，如实返回"读不到"
        }

        // xlsx 元数据 GUID：读 sharedStrings.xml / sheet1.xml 里的 {8-4-4-4-12}
        public static string ReadXlsxGuid(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using (Package pkg = Package.Open(path, FileMode.Open, FileAccess.Read))
                {
                    string[] parts = { "/xl/sharedStrings.xml", "/xl/worksheets/sheet1.xml" };
                    Regex rg = new Regex(@"\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?");
                    foreach (string pn in parts)
                    {
                        Uri u = new Uri(pn, UriKind.Relative);
                        if (!pkg.PartExists(u)) continue;
                        using (StreamReader sr = new StreamReader(pkg.GetPart(u).GetStream()))
                        {
                            Match m = rg.Match(sr.ReadToEnd());
                            if (m.Success) return NormGuid(m.Value);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static string NormGuid(string s)
        {
            if (s == null) return null;
            return s.Trim().Trim('{', '}').ToUpperInvariant();
        }
    }
}
