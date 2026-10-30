using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace caxa3dTreeBom
{
    /// <summary>
    /// 表格服务（零依赖）：读取模板第 1 行表头、写出 BOM 工作表并把缩略图 PNG 嵌入对应单元格。
    /// 只用 System.IO.Compression + System.Xml.Linq 直接读写 xlsx 包，不引用 Excel/ClosedXML，
    /// 避免在 CAXA 3D 进程内加载第三方程序集引发的冲突。
    /// </summary>
    [ComVisible(false)]
    internal static class XlsxTableService
    {
        /// <summary>模板缺失时使用的固定表头（与 caxa3dTreeBomxls.xlsx 一致）。</summary>
        public static readonly string[] DefaultHeaders =
        {
            "层级", "是否有工程图", "缩略图", "SC图号", "SC名称", "SC规格", "SC材料", "SC备注", "数量", "总数量"
        };

        private static readonly XNamespace NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace NsXdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        private static readonly XNamespace NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";

        // ==================== 读取 ====================

        /// <summary>读取表格第 1 行表头（返回非空表头列表，顺序即列顺序）。</summary>
        public static List<string> LoadHeaders(string path)
        {
            var headers = new List<string>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return headers;

            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                string sheetPath = FirstSheetPath(zip) ?? "xl/worksheets/sheet1.xml";
                List<string> shared = ReadSharedStrings(zip);
                ZipArchiveEntry sheet = zip.GetEntry(sheetPath);
                if (sheet == null) return headers;

                XDocument doc;
                using (Stream s = sheet.Open()) doc = XDocument.Load(s);

                var byCol = new Dictionary<int, string>();
                int maxCol = -1;
                foreach (XElement row in doc.Root.Element(NsMain + "sheetData").Elements(NsMain + "row"))
                {
                    string r = (string)row.Attribute("r");
                    if (r != "1") continue;
                    foreach (XElement cell in row.Elements(NsMain + "c"))
                    {
                        int col = ColumnIndex((string)cell.Attribute("r"));
                        if (col < 0) continue;
                        byCol[col] = CellText(cell, shared);
                        if (col > maxCol) maxCol = col;
                    }
                    break;
                }

                for (int c = 0; c <= maxCol; c++)
                {
                    string v;
                    headers.Add(byCol.TryGetValue(c, out v) ? (v ?? "").Trim() : "");
                }
                while (headers.Count > 0 && string.IsNullOrWhiteSpace(headers[headers.Count - 1]))
                    headers.RemoveAt(headers.Count - 1);
            }
            return headers;
        }

        private static string FirstSheetPath(ZipArchive zip)
        {
            try
            {
                ZipArchiveEntry wb = zip.GetEntry("xl/workbook.xml");
                ZipArchiveEntry rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
                if (wb == null || rels == null) return null;

                XDocument wbDoc, relDoc;
                using (Stream s = wb.Open()) wbDoc = XDocument.Load(s);
                using (Stream s = rels.Open()) relDoc = XDocument.Load(s);

                XElement sheet = wbDoc.Root.Element(NsMain + "sheets").Elements(NsMain + "sheet").FirstOrDefault();
                if (sheet == null) return null;
                string rid = (string)sheet.Attribute(NsRel + "id");

                foreach (XElement rel in relDoc.Root.Elements(NsPkgRel + "Relationship"))
                {
                    if ((string)rel.Attribute("Id") != rid) continue;
                    string target = (string)rel.Attribute("Target");
                    if (string.IsNullOrEmpty(target)) return null;
                    return target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target.Replace("../", "");
                }
            }
            catch { }
            return null;
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            ZipArchiveEntry e = zip.GetEntry("xl/sharedStrings.xml");
            if (e == null) return list;
            try
            {
                XDocument doc;
                using (Stream s = e.Open()) doc = XDocument.Load(s);
                foreach (XElement si in doc.Root.Elements(NsMain + "si"))
                {
                    var sb = new StringBuilder();
                    foreach (XElement t in si.Descendants(NsMain + "t")) sb.Append(t.Value);
                    list.Add(sb.ToString());
                }
            }
            catch { }
            return list;
        }

        private static string CellText(XElement cell, List<string> shared)
        {
            string type = (string)cell.Attribute("t");
            if (type == "s")
            {
                XElement v = cell.Element(NsMain + "v");
                int idx;
                if (v != null && int.TryParse(v.Value, out idx) && idx >= 0 && idx < shared.Count)
                    return shared[idx];
                return "";
            }
            if (type == "inlineStr")
            {
                XElement isEl = cell.Element(NsMain + "is");
                return isEl == null ? "" : isEl.Value;
            }
            XElement vv = cell.Element(NsMain + "v");
            return vv == null ? "" : vv.Value;
        }

        /// <summary>"BC12" → 列索引（0 基）。</summary>
        private static int ColumnIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return -1;
            int col = 0, i = 0;
            while (i < cellRef.Length && char.IsLetter(cellRef[i]))
            {
                col = col * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1);
                i++;
            }
            return col - 1;
        }

        private static string ColumnName(int index)
        {
            var sb = new StringBuilder();
            int n = index;
            while (n >= 0)
            {
                sb.Insert(0, (char)('A' + n % 26));
                n = n / 26 - 1;
            }
            return sb.ToString();
        }

        // ==================== 写出 ====================

        /// <summary>
        /// 写出 BOM 表格。
        /// </summary>
        /// <param name="path">输出 xlsx 路径。</param>
        /// <param name="headers">表头（列顺序）。</param>
        /// <param name="rows">数据行，每行长度与 headers 一致。</param>
        /// <param name="thumbColumnIndex">缩略图列索引（无则 -1）。</param>
        /// <param name="thumbPathOf">按数据行索引取缩略图 PNG 路径（无则 null）。</param>
        public static void Save(string path, IList<string> headers, IList<string[]> rows,
                                int thumbColumnIndex, Func<int, string> thumbPathOf)
        {
            int colCount = headers == null ? 0 : headers.Count;
            int rowCount = rows == null ? 0 : rows.Count;

            var images = new List<ThumbImage>();
            if (thumbColumnIndex >= 0 && thumbColumnIndex < colCount && thumbPathOf != null)
            {
                for (int r = 0; r < rowCount; r++)
                {
                    string png = thumbPathOf(r);
                    if (string.IsNullOrEmpty(png) || !File.Exists(png)) continue;
                    images.Add(new ThumbImage { RowIndex = r, ColIndex = thumbColumnIndex, FilePath = png });
                }
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(path)) File.Delete(path);

            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteText(zip, "[Content_Types].xml", ContentTypes(images.Count > 0));
                WriteText(zip, "_rels/.rels", RootRels());
                WriteText(zip, "xl/workbook.xml", WorkbookXml(colCount > 0));
                WriteText(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
                WriteText(zip, "xl/styles.xml", StylesXml());

                var sheet = new StringBuilder();
                BuildSheet(sheet, headers, rows, thumbColumnIndex, images.Count > 0);
                WriteText(zip, "xl/worksheets/sheet1.xml", sheet.ToString());

                if (images.Count > 0)
                {
                    WriteText(zip, "xl/worksheets/_rels/sheet1.xml.rels", DrawingRel());
                    WriteText(zip, "xl/drawings/drawing1.xml", DrawingXml(images));
                    WriteText(zip, "xl/drawings/_rels/drawing1.xml.rels", DrawingRels(images.Count));
                    for (int i = 0; i < images.Count; i++)
                    {
                        ZipArchiveEntry entry = zip.CreateEntry("xl/media/image" + (i + 1) + ".png",
                                                                CompressionLevel.NoCompression);
                        using (Stream es = entry.Open())
                        using (FileStream src = new FileStream(images[i].FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            src.CopyTo(es);
                    }
                }
            }
        }

        /// <summary>用默认表头创建模板文件。</summary>
        public static void CreateDefaultTemplate(string path)
        {
            Save(path, DefaultHeaders, new List<string[]>(), -1, null);
        }

        private class ThumbImage
        {
            public int RowIndex;
            public int ColIndex;
            public string FilePath;
        }

        // ---------------- sheet ----------------

        private static void BuildSheet(StringBuilder sb, IList<string> headers, IList<string[]> rows,
                                       int thumbCol, bool hasDrawing)
        {
            int colCount = headers == null ? 0 : headers.Count;
            int rowCount = rows == null ? 0 : rows.Count;

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(NsMain).Append("\" xmlns:r=\"").Append(NsRel).Append("\">");
            sb.Append("<sheetPr/><dimension ref=\"A1\"/>");
            sb.Append("<sheetViews><sheetView tabSelected=\"1\" workbookViewId=\"0\">")
              .Append("<pane xSplit=\"0\" ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>")
              .Append("</sheetView></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"18\"/>");

            // 列宽
            if (colCount > 0)
            {
                sb.Append("<cols>");
                for (int c = 0; c < colCount; c++)
                {
                    double w = WidthFor(headers[c]);
                    sb.Append("<col min=\"").Append(c + 1).Append("\" max=\"").Append(c + 1)
                      .Append("\" width=\"").Append(w.ToString("0.##"))
                      .Append("\" customWidth=\"1\"/>");
                }
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");

            // 表头行
            if (colCount > 0)
            {
                sb.Append("<row r=\"1\" ht=\"22\" customHeight=\"1\">");
                for (int c = 0; c < colCount; c++)
                    sb.Append(TextCell(ColumnName(c) + "1", headers[c] ?? "", 1));
                sb.Append("</row>");
            }

            // 数据行
            for (int r = 0; r < rowCount; r++)
            {
                string[] data = rows[r] ?? new string[colCount];
                int excelRow = r + 2;
                int height = (thumbCol >= 0) ? 46 : 18;
                sb.Append("<row r=\"").Append(excelRow).Append("\" ht=\"").Append(height)
                  .Append("\" customHeight=\"1\">");

                for (int c = 0; c < colCount; c++)
                {
                    string v = c < data.Length ? (data[c] ?? "") : "";
                    if (string.IsNullOrEmpty(v)) continue;

                    string h = headers[c] == null ? "" : headers[c].Trim();
                    string refName = ColumnName(c) + excelRow;
                    bool numeric = (h == "数量" || h == "总数量");
                    int num;
                    if (numeric && int.TryParse(v, out num)) sb.Append(NumberCell(refName, num, 2));
                    else sb.Append(TextCell(refName, v, 2));
                }
                sb.Append("</row>");
            }

            sb.Append("</sheetData>");
            if (hasDrawing) sb.Append("<drawing r:id=\"rId1\"/>");
            sb.Append("<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>");
            sb.Append("</worksheet>");
        }

        private static double WidthFor(string header)
        {
            switch ((header ?? "").Trim())
            {
                case "层级": return 22;
                case "是否有工程图": return 13;
                case "缩略图": return 18;
                case "图号":
                case "SC图号": return 18;
                case "名称":
                case "SC名称": return 26;
                case "规格":
                case "SC规格": return 18;
                case "材料":
                case "SC材料": return 14;
                case "备注":
                case "SC备注": return 22;
                case "数量": return 8;
                case "总数量": return 9;
                default: return 18;
            }
        }

        private static string TextCell(string r, string value, int style)
        {
            return "<c r=\"" + r + "\" s=\"" + style + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">"
                + Xml(value) + "</t></is></c>";
        }

        private static string NumberCell(string r, int value, int style)
        {
            return "<c r=\"" + r + "\" s=\"" + style + "\"><v>" + value + "</v></c>";
        }

        private static string Xml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("\r", "").Replace("\n", "&#10;");
        }

        // ---------------- 固定部件 ----------------

        private static string ContentTypes(bool withDrawing)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            if (withDrawing) sb.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            if (withDrawing) sb.Append("<Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string RootRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"" + NsPkgRel + "\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                 + "</Relationships>";
        }

        private static string WorkbookXml(bool hasData)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<workbook xmlns=\"" + NsMain + "\" xmlns:r=\"" + NsRel + "\">"
                 + "<bookViews><workbookView xWindow=\"0\" yWindow=\"0\" windowWidth=\"24000\" windowHeight=\"12000\"/></bookViews>"
                 + "<sheets><sheet name=\"BOM\" sheetId=\"1\" r:id=\"rId1\"/></sheets>"
                 + "</workbook>";
        }

        private static string WorkbookRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"" + NsPkgRel + "\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                 + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                 + "</Relationships>";
        }

        private static string StylesXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<styleSheet xmlns=\"" + NsMain + "\">"
                 + "<fonts count=\"2\">"
                 + "<font><sz val=\"10\"/><name val=\"Microsoft YaHei\"/><charset val=\"134\"/></font>"
                 + "<font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Microsoft YaHei\"/><charset val=\"134\"/></font>"
                 + "</fonts>"
                 + "<fills count=\"3\">"
                 + "<fill><patternFill patternType=\"none\"/></fill>"
                 + "<fill><patternFill patternType=\"gray125\"/></fill>"
                 + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF0078D4\"/><bgColor indexed=\"64\"/></patternFill></fill>"
                 + "</fills>"
                 + "<borders count=\"2\">"
                 + "<border><left/><right/><top/><bottom/><diagonal/></border>"
                 + "<border><left style=\"thin\"><color auto=\"1\"/></left><right style=\"thin\"><color auto=\"1\"/></right>"
                 + "<top style=\"thin\"><color auto=\"1\"/></top><bottom style=\"thin\"><color auto=\"1\"/></bottom><diagonal/></border>"
                 + "</borders>"
                 + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                 + "<cellXfs count=\"3\">"
                 + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                 + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>"
                 + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>"
                 + "</cellXfs>"
                 + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
                 + "</styleSheet>";
        }

        private static string DrawingRel()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"" + NsPkgRel + "\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/>"
                 + "</Relationships>";
        }

        private static string DrawingRels(int count)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"").Append(NsPkgRel).Append("\">");
            for (int i = 1; i <= count; i++)
            {
                sb.Append("<Relationship Id=\"rId").Append(i)
                  .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/image")
                  .Append(i).Append(".png\"/>");
            }
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        /// <summary>缩略图按单元格左上角绝对定位（96×54 像素），不拉伸单元格。</summary>
        private static string DrawingXml(List<ThumbImage> images)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<xdr:wsDr xmlns:xdr=\"").Append(NsXdr).Append("\" xmlns:a=\"").Append(NsA)
              .Append("\" xmlns:r=\"").Append(NsRel).Append("\">");

            for (int i = 0; i < images.Count; i++)
            {
                ThumbImage img = images[i];
                sb.Append("<xdr:oneCellAnchor>");
                sb.Append("<xdr:from><xdr:col>").Append(img.ColIndex)
                  .Append("</xdr:col><xdr:colOff>38000</xdr:colOff><xdr:row>")
                  .Append(img.RowIndex + 1).Append("</xdr:row><xdr:rowOff>60000</xdr:rowOff></xdr:from>");
                sb.Append("<xdr:ext cx=\"914400\" cy=\"514350\"/>");
                sb.Append("<xdr:pic>");
                sb.Append("<xdr:nvPicPr><xdr:cNvPr id=\"").Append(i + 1)
                  .Append("\" name=\"缩略图").Append(i + 1)
                  .Append("\"/><xdr:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></xdr:cNvPicPr></xdr:nvPicPr>");
                sb.Append("<xdr:blipFill><a:blip r:embed=\"rId").Append(i + 1)
                  .Append("\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>");
                sb.Append("<xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"914400\" cy=\"514350\"/></a:xfrm>")
                  .Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr>");
                sb.Append("</xdr:pic>");
                sb.Append("<xdr:clientData/>");
                sb.Append("</xdr:oneCellAnchor>");
            }

            sb.Append("</xdr:wsDr>");
            return sb.ToString();
        }

        // ---------------- zip helper ----------------

        private static void WriteText(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream s = e.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false)))
                w.Write(content);
        }
    }
}
