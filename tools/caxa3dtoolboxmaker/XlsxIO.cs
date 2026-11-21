using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Xml;

namespace Caxa3dToolboxMaker
{
    // ============ 零依赖 xlsx 读写（System.IO.Packaging + System.Xml，无 NuGet / 无 Excel COM） ============
    // 参考模板 123.xlsx（WPS 生成的标准 OOXML）：
    //   行1: A=标准件库零件名, B=零件名
    //   行2: A=GUID, B={...}
    //   行3: 空行
    //   行4: 22 列表头（Dimension→Display_name 映射后的中文名）
    //   行5: 填写说明
    //   行6..: 数据（与表头一一对应）
    public static class XlsxIO
    {
        const string NS_MAIN = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string REL_TYPE_OFFICE_DOC = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
        const string REL_TYPE_WORKSHEET = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
        const string REL_TYPE_SHARED_STRINGS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings";
        const string CT_XLSX = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
        const string CT_WORKSHEET = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
        const string CT_SHARED = "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml";

        // 列序号(0基) → Excel 列名 A/B/.../Z/AA
        public static string ColName(int index)
        {
            StringBuilder sb = new StringBuilder();
            int n = index + 1;
            while (n > 0) { int r = (n - 1) % 26; sb.Insert(0, (char)('A' + r)); n = (n - 1) / 26; }
            return sb.ToString();
        }

        // ---------- 读：把 sheet1 + sharedStrings 解析为二维字符串表（行优先，含空行占位） ----------
        public static List<List<string>> ReadSheet(string path)
        {
            List<List<string>> rows = new List<List<string>>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return rows;
            using (Package pkg = Package.Open(path, FileMode.Open, FileAccess.Read))
            {
                // 1) sharedStrings
                List<string> shared = new List<string>();
                Uri ssUri = new Uri("/xl/sharedStrings.xml", UriKind.Relative);
                if (pkg.PartExists(ssUri))
                {
                    XmlDocument ssDoc = new XmlDocument();
                    using (Stream s = pkg.GetPart(ssUri).GetStream()) ssDoc.Load(s);
                    XmlNamespaceManager ns = new XmlNamespaceManager(ssDoc.NameTable);
                    ns.AddNamespace("m", NS_MAIN);
                    foreach (XmlNode si in ssDoc.SelectNodes("/m:sst/m:si", ns))
                    {
                        StringBuilder t = new StringBuilder();
                        // <si><t>xxx</t></si> 或富文本 <si><r><t>..</t></r>...</si>
                        foreach (XmlNode tn in si.SelectNodes(".//m:t", ns)) t.Append(tn.InnerText);
                        shared.Add(t.ToString());
                    }
                }
                // 2) sheet1
                Uri shUri = new Uri("/xl/worksheets/sheet1.xml", UriKind.Relative);
                if (!pkg.PartExists(shUri)) return rows;
                XmlDocument shDoc = new XmlDocument();
                using (Stream s = pkg.GetPart(shUri).GetStream()) shDoc.Load(s);
                XmlNamespaceManager sns = new XmlNamespaceManager(shDoc.NameTable);
                sns.AddNamespace("m", NS_MAIN);
                foreach (XmlNode rowNode in shDoc.SelectNodes("/m:worksheet/m:sheetData/m:row", sns))
                {
                    int rowNo;
                    if (!int.TryParse(GetAttr(rowNode, "r"), out rowNo)) rowNo = rows.Count + 1;
                    while (rows.Count < rowNo) rows.Add(new List<string>()); // 空行占位
                    List<string> cells = rows[rowNo - 1];
                    foreach (XmlNode c in rowNode.SelectNodes("m:c", sns))
                    {
                        string refv = GetAttr(c, "r");
                        int col = ColIndex(refv);
                        if (col < 0) continue;
                        while (cells.Count <= col) cells.Add("");
                        string type = GetAttr(c, "t");
                        string val;
                        if (type == "inlineStr")
                        {
                            StringBuilder t = new StringBuilder();
                            foreach (XmlNode tn in c.SelectNodes(".//m:t", sns)) t.Append(tn.InnerText);
                            val = t.ToString();
                        }
                        else
                        {
                            XmlNode v = c.SelectSingleNode("m:v", sns);
                            string raw = v == null ? "" : v.InnerText;
                            if (type == "s")
                            {
                                int ix; val = int.TryParse(raw, out ix) && ix >= 0 && ix < shared.Count ? shared[ix] : "";
                            }
                            else val = raw; // n / b / str / 空
                        }
                        cells[col] = val ?? "";
                    }
                }
            }
            // 去尾部全空行
            while (rows.Count > 0 && IsEmptyRow(rows[rows.Count - 1])) rows.RemoveAt(rows.Count - 1);
            return rows;
        }

        static bool IsEmptyRow(List<string> row)
        {
            foreach (string s in row) if (!string.IsNullOrEmpty(s)) return false;
            return true;
        }

        static string GetAttr(XmlNode n, string name)
        {
            XmlAttribute a = n.Attributes == null ? null : n.Attributes[name];
            return a == null ? "" : a.Value;
        }

        // "AB12" → 列序号 0基
        static int ColIndex(string refv)
        {
            if (string.IsNullOrEmpty(refv)) return -1;
            int col = 0, i = 0;
            for (; i < refv.Length && char.IsLetter(refv[i]); i++)
                col = col * 26 + (char.ToUpperInvariant(refv[i]) - 'A' + 1);
            return col - 1;
        }

        // ---------- 写：生成标准 OOXML 包 ----------
        public static void WriteTemplate(string path, string partName, string guid,
                                         List<string> headers, List<List<string>> data)
        {
            // 构造所有单元格文本 → 共享字符串表（去重）
            List<string> shared = new List<string>();
            Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.Ordinal);
            // 预置固定文本
            AddShared(shared, map, "标准件库零件名");
            AddShared(shared, map, "GUID");
            AddShared(shared, map, partName ?? "");
            AddShared(shared, map, guid ?? "");
            AddShared(shared, map, "请从下一行开始填写数据，数据需与列一一对应.不要随意修改列的顺序，也不要随意修改列的名称，否则可能会导致无法正确导入数据.");
            if (headers != null) foreach (string h in headers) AddShared(shared, map, h ?? "");
            if (data != null) foreach (List<string> row in data) if (row != null)
                foreach (string v in row) AddShared(shared, map, v ?? "");

            StringBuilder sheet = new StringBuilder();
            sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sheet.Append("<worksheet xmlns=\"").Append(NS_MAIN).Append("\">");
            int maxCols = Math.Max(2, headers == null ? 0 : headers.Count);
            if (data != null) foreach (List<string> r in data) if (r != null) maxCols = Math.Max(maxCols, r.Count);
            sheet.Append("<dimension ref=\"A1:").Append(ColName(Math.Max(0, maxCols - 1)))
                 .Append(Math.Max(5, (data == null ? 0 : data.Count) + 5)).Append("\"/>");
            sheet.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sheet.Append("<sheetFormatPr defaultColWidth=\"9\" defaultRowHeight=\"13.5\"/>");
            sheet.Append("<sheetData>");
            // 行1 零件名
            sheet.Append("<row r=\"1\">");
            Cell(sheet, "A1", S("标准件库零件名", map));
            Cell(sheet, "B1", S(partName ?? "", map));
            sheet.Append("</row>");
            // 行2 GUID
            sheet.Append("<row r=\"2\">");
            Cell(sheet, "A2", S("GUID", map));
            Cell(sheet, "B2", S(guid ?? "", map));
            sheet.Append("</row>");
            // 行3 空行（跳过，不输出 row 节点）
            // 行4 表头
            sheet.Append("<row r=\"4\">");
            if (headers != null)
                for (int c = 0; c < headers.Count; c++)
                    Cell(sheet, ColName(c) + "4", S(headers[c] ?? "", map));
            sheet.Append("</row>");
            // 行5 说明
            sheet.Append("<row r=\"5\">");
            Cell(sheet, "A5", S("请从下一行开始填写数据，数据需与列一一对应.不要随意修改列的顺序，也不要随意修改列的名称，否则可能会导致无法正确导入数据.", map));
            sheet.Append("</row>");
            // 行6 起数据
            if (data != null)
                for (int r = 0; r < data.Count; r++)
                {
                    List<string> row = data[r];
                    if (row == null) continue;
                    sheet.Append("<row r=\"").Append(r + 6).Append("\">");
                    for (int c = 0; c < row.Count; c++)
                        Cell(sheet, ColName(c) + (r + 6), S(row[c] ?? "", map));
                    sheet.Append("</row>");
                }
            sheet.Append("</sheetData></worksheet>");

            StringBuilder ss = new StringBuilder();
            ss.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            ss.Append("<sst xmlns=\"").Append(NS_MAIN).Append("\" count=\"").Append(shared.Count)
              .Append("\" uniqueCount=\"").Append(shared.Count).Append("\">");
            foreach (string t in shared) ss.Append("<si><t>").Append(Esc(t)).Append("</t></si>");
            ss.Append("</sst>");

            WritePackage(path, sheet.ToString(), ss.ToString());
        }

        static void AddShared(List<string> list, Dictionary<string, int> map, string v)
        {
            if (!map.ContainsKey(v)) { map[v] = list.Count; list.Add(v); }
        }
        static string S(string v, Dictionary<string, int> map)
        {
            int ix; return map.TryGetValue(v ?? "", out ix) ? ix.ToString() : "0";
        }
        static void Cell(StringBuilder sb, string refv, string sharedIndex)
        {
            sb.Append("<c r=\"").Append(refv).Append("\" t=\"s\"><v>").Append(sharedIndex).Append("</v></c>");
        }
        static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        static void WritePackage(string path, string sheetXml, string sharedXml)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 删除旧文件：Package.Create 要求目标不存在
            if (File.Exists(path)) File.Delete(path);
            using (Package pkg = Package.Open(path, FileMode.Create, FileAccess.ReadWrite))
            {
                // 注意：[Content_Types].xml 由 System.IO.Packaging 在 Dispose 时自动生成，
                // 绝不能手动 CreatePart("/[Content_Types].xml")——否则 SaveToFile 与保留名冲突，
                // 抛"由于指定的文件名已在使用，无法创建文件"（实测坑）。
                // _rels/.rels
                AddPart(pkg, "/_rels/.rels", "application/vnd.openxmlformats-package.relationships+xml", null,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"" + REL_TYPE_OFFICE_DOC + "\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                // xl/workbook.xml
                AddPart(pkg, "/xl/workbook.xml", CT_XLSX, null,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"" + NS_MAIN + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                // xl/_rels/workbook.xml.rels
                AddPart(pkg, "/xl/_rels/workbook.xml.rels", "application/vnd.openxmlformats-package.relationships+xml", null,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"" + REL_TYPE_WORKSHEET + "\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"" + REL_TYPE_SHARED_STRINGS + "\" Target=\"sharedStrings.xml\"/>" +
                    "</Relationships>");
                // xl/worksheets/sheet1.xml
                AddPart(pkg, "/xl/worksheets/sheet1.xml", CT_WORKSHEET, null, sheetXml);
                // xl/sharedStrings.xml
                AddPart(pkg, "/xl/sharedStrings.xml", CT_SHARED, null, sharedXml);
            }
        }

        const string CT_TYPES = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet+xml";

        static void AddPart(Package pkg, string name, string contentType, string relType, string content)
        {
            Uri u = new Uri(name, UriKind.Relative);
            PackagePart part = pkg.CreatePart(u, contentType);
            using (StreamWriter sw = new StreamWriter(part.GetStream(FileMode.Create, FileAccess.Write), new UTF8Encoding(false)))
                sw.Write(content);
        }
    }
}
