// 零依赖 xlsx 读写（仅第一个工作表，纯值，不保留样式）。
// 移植自 caxa3dAttributeManageraddin/XlsxLite.cs（已验证可编译、可在进程内使用）。
// 读取支持 inlineStr / sharedStrings / 数字；写出全部为 inlineStr 或数字。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace caxa3dBatchMaster
{
    internal static class XlsxLite
    {
        /// <summary>读取 xlsx 第一个工作表为字符串网格（行列从 0 开始，空单元格为 ""）。</summary>
        public static List<List<string>> ReadFirstSheet(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                List<string> shared = ReadSharedStrings(zip);
                string sheetEntry = FindFirstSheetEntry(zip);

                ZipArchiveEntry sheet = zip.GetEntry(sheetEntry);
                if (sheet == null)
                    throw new InvalidDataException("xlsx 中找不到工作表: " + sheetEntry);

                XmlDocument doc = new XmlDocument();
                using (Stream s = sheet.Open()) doc.Load(s);

                XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

                var grid = new List<List<string>>();
                int maxCols = 0;

                XmlNodeList rows = doc.SelectNodes("//x:sheetData/x:row", ns);
                foreach (XmlNode rowNode in rows)
                {
                    foreach (XmlNode cell in rowNode.SelectNodes("x:c", ns))
                    {
                        string refAttr = cell.Attributes != null && cell.Attributes["r"] != null
                            ? cell.Attributes["r"].Value : null;
                        if (string.IsNullOrEmpty(refAttr)) continue;
                        int col = ColIndexFromRef(refAttr);
                        int row = RowIndexFromRef(refAttr);
                        if (col < 0 || row < 0) continue;

                        while (grid.Count <= row) grid.Add(new List<string>());
                        List<string> gridRow = grid[row];
                        while (gridRow.Count <= col) gridRow.Add("");

                        gridRow[col] = ReadCellText(cell, ns, shared);
                        if (col + 1 > maxCols) maxCols = col + 1;
                    }
                }

                foreach (List<string> gridRow in grid)
                    while (gridRow.Count < maxCols) gridRow.Add("");

                return grid;
            }
        }

        /// <summary>把字符串网格写回 xlsx（覆盖第一个工作表内容，样式不保留）。</summary>
        public static void WriteFirstSheet(string path, List<List<string>> grid)
        {
            string sheetXml = BuildSheetXml(grid);

            string tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (FileStream fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "</Types>");
                WriteEntry(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                WriteEntry(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                    "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "</Relationships>");
                WriteEntry(zip, "xl/worksheets/sheet1.xml", sheetXml);
            }
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        #region 内部实现

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry e = zip.CreateEntry(name);
            using (Stream s = e.Open())
            {
                byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        private static string BuildSheetXml(List<List<string>> grid)
        {
            int rows = grid.Count;
            int cols = 0;
            foreach (List<string> r in grid) if (r.Count > cols) cols = r.Count;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (rows > 0 && cols > 0)
                sb.Append("<dimension ref=\"A1:").Append(ColName(cols - 1)).Append(rows).Append("\"/>");
            sb.Append("<sheetData>");

            for (int r = 0; r < rows; r++)
            {
                sb.Append("<row r=\"").Append(r + 1).Append("\">");
                for (int c = 0; c < grid[r].Count; c++)
                {
                    string v = grid[r][c];
                    if (string.IsNullOrEmpty(v)) continue;
                    string cellRef = ColName(c) + (r + 1).ToString(CultureInfo.InvariantCulture);
                    double num;
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out num) &&
                        v.Trim() == num.ToString("R", CultureInfo.InvariantCulture))
                    {
                        sb.Append("<c r=\"").Append(cellRef).Append("\"><v>")
                          .Append(num.ToString("R", CultureInfo.InvariantCulture)).Append("</v></c>");
                    }
                    else
                    {
                        sb.Append("<c r=\"").Append(cellRef).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                          .Append(Escape(v)).Append("</t></is></c>");
                    }
                }
                sb.Append("</row>");
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static string ColName(int colIdx)
        {
            string result = "";
            int n = colIdx;
            while (n >= 0)
            {
                result = (char)(n % 26 + 'A') + result;
                n = n / 26 - 1;
            }
            return result;
        }

        private static int ColIndexFromRef(string cellRef)
        {
            int col = 0;
            int i = 0;
            while (i < cellRef.Length && cellRef[i] >= 'A' && cellRef[i] <= 'Z')
            {
                col = col * 26 + (cellRef[i] - 'A' + 1);
                i++;
            }
            return i == 0 ? -1 : col - 1;
        }

        private static int RowIndexFromRef(string cellRef)
        {
            int i = 0;
            while (i < cellRef.Length && cellRef[i] >= 'A' && cellRef[i] <= 'Z') i++;
            int row;
            if (i >= cellRef.Length) return -1;
            if (!int.TryParse(cellRef.Substring(i), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out row)) return -1;
            return row - 1;
        }

        private static string ReadCellText(XmlNode cell, XmlNamespaceManager ns, List<string> shared)
        {
            string type = cell.Attributes != null && cell.Attributes["t"] != null
                ? cell.Attributes["t"].Value : null;

            if (type == "inlineStr")
            {
                XmlNode t = cell.SelectSingleNode("x:is/x:t", ns);
                return t != null ? t.InnerText : "";
            }

            XmlNode v = cell.SelectSingleNode("x:v", ns);
            if (v == null) return "";
            string raw = v.InnerText;

            if (type == "s")
            {
                int idx;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) &&
                    idx >= 0 && idx < shared.Count)
                    return shared[idx];
                return "";
            }
            return raw;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            ZipArchiveEntry entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return list;

            XmlDocument doc = new XmlDocument();
            using (Stream s = entry.Open()) doc.Load(s);
            XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

            foreach (XmlNode si in doc.SelectNodes("//x:si", ns))
            {
                XmlNodeList ts = si.SelectNodes(".//x:t", ns);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (XmlNode t in ts) sb.Append(t.InnerText);
                list.Add(sb.ToString());
            }
            return list;
        }

        private static string FindFirstSheetEntry(ZipArchive zip)
        {
            ZipArchiveEntry wbEntry = zip.GetEntry("xl/workbook.xml");
            if (wbEntry == null) throw new InvalidDataException("xlsx 中找不到 xl/workbook.xml");

            XmlDocument wb = new XmlDocument();
            using (Stream s = wbEntry.Open()) wb.Load(s);
            XmlNamespaceManager ns = new XmlNamespaceManager(wb.NameTable);
            ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            ns.AddNamespace("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

            XmlNode sheet = wb.SelectSingleNode("//x:sheets/x:sheet", ns);
            if (sheet == null) throw new InvalidDataException("xlsx 工作簿中没有工作表");
            string rid = sheet.Attributes["r:id"] != null
                ? sheet.Attributes["r:id"].Value
                : sheet.Attributes["id"] != null ? sheet.Attributes["id"].Value : null;
            if (string.IsNullOrEmpty(rid)) throw new InvalidDataException("工作表缺少 r:id");

            ZipArchiveEntry relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (relsEntry == null) throw new InvalidDataException("xlsx 中找不到 xl/_rels/workbook.xml.rels");
            XmlDocument rels = new XmlDocument();
            using (Stream s = relsEntry.Open()) rels.Load(s);

            foreach (XmlNode rel in rels.SelectNodes("//*[local-name()='Relationship']"))
            {
                if (rel.Attributes["Id"] != null && rel.Attributes["Id"].Value == rid &&
                    rel.Attributes["Target"] != null)
                {
                    string target = rel.Attributes["Target"].Value;
                    if (target.StartsWith("/")) target = target.Substring(1);
                    else if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                        target = "xl/" + target;
                    return target;
                }
            }
            throw new InvalidDataException("找不到工作表对应的关系: " + rid);
        }

        #endregion
    }
}
