using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace caxa3dTreeBom
{
    /// <summary>
    /// “取值来源通道”呈现层：只写 DataGridView 单元格/列头的 ToolTipText 与一行日志汇总文本。
    /// 不触碰值、样式、列宽、字体；无 COM、无 P/Invoke、无线程、无 MessageBox、不持有任何状态。
    /// 来源串词表（数据层保证只有四种形态）：
    ///   "属性"＝自定义属性共享表命中；"BOM:&lt;label&gt;"＝BOM 导出兜底命中；
    ///   "专用:&lt;词&gt;"＝原有专用兜底；空串＝三通道均未取到（读不到，与“取到但空白”分开表述，坑 155）。
    /// 每格命中自哪条通道必须写进提示（坑 179：日志/tooltip 必须点名通道）。
    /// </summary>
    [ComVisible(false)]
    internal static class ColumnSourceUi
    {
        private const string SrcProp = "属性";
        private const string SrcBomPrefix = "BOM:";
        private const string SrcSpecPrefix = "专用:";
        private const int MaxNamesInSummary = 8;

        // ==================================================================== 公开方法

        /// <summary>单元格悬停提示：表头｜来源｜值。入参异常一律返回空串，绝不抛出。</summary>
        internal static string CellToolTip(string header, string value, string source)
        {
            try
            {
                if (header == null) return "";
                string v = OneLine((value ?? "").Trim());
                string shown = v.Length == 0 ? "（空）" : v;
                return "表头：" + OneLine(header.Trim()) + "｜来源：" + SourceDesc(source) + "｜值：" + shown;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 一次抓取/回读完成后的一行日志汇总（纯文本、单行、带分母）。
        /// srcByHeader 里查不到的列按“读不到”计入；blankHeaders＝全表没取到值的表头（单独一段，不与“读不到”混写）。
        /// </summary>
        internal static string BuildSummary(IList<string> headers,
                                            IDictionary<string, string> srcByHeader,
                                            ICollection<string> blankHeaders,
                                            int rowCount)
        {
            try
            {
                int n = rowCount < 0 ? 0 : rowCount;

                // 桶：0=属性 1=BOM 2=专用 3=读不到 4=未识别来源（第三态，不折算进任何桶）
                var names = new List<string>[]
                {
                    new List<string>(), new List<string>(), new List<string>(),
                    new List<string>(), new List<string>()
                };
                int dataCols = 0;

                if (headers != null)
                {
                    foreach (string h in headers)
                    {
                        string t = (h ?? "").Trim();
                        if (t.Length == 0 || IsStructuralHeader(t)) continue;
                        dataCols++;
                        names[Classify(LookupLabel(srcByHeader, t))].Add(t);
                    }
                }

                var blankNames = new List<string>();
                if (blankHeaders != null)
                {
                    foreach (string b in blankHeaders)
                    {
                        string t = (b ?? "").Trim();
                        if (t.Length == 0 || IsStructuralHeader(t)) continue;
                        blankNames.Add(t);
                    }
                }

                if (dataCols == 0)
                    return "取值来源｜数据列 0 个｜共 " + n + " 行｜本次没有参与属性取值的表头列";

                var sb = new StringBuilder();
                sb.Append("取值来源（数据列 ").Append(dataCols)
                  .Append(" 个｜共 ").Append(n).Append(" 行）：");
                AppendBucket(sb, "属性", names[0], dataCols, false);
                sb.Append("｜");
                AppendBucket(sb, "BOM 兜底", names[1], dataCols, true);
                sb.Append("｜");
                AppendBucket(sb, "专用", names[2], dataCols, true);
                sb.Append("｜");
                AppendBucket(sb, "读不到", names[3], dataCols, true);
                if (names[4].Count > 0)
                {
                    sb.Append("｜");
                    AppendBucket(sb, "未识别来源", names[4], dataCols, true);
                }
                if (blankNames.Count > 0)
                {
                    sb.Append("｜取到值但全表空白 ").Append(blankNames.Count)
                      .Append(" 列（").Append(JoinNames(blankNames)).Append("）");
                }
                return sb.ToString();
            }
            catch
            {
                return "取值来源：汇总生成失败（呈现层异常已吞掉，不影响数据与抓取结果）";
            }
        }

        /// <summary>
        /// 把来源落到单元格 ToolTipText（只设 ToolTipText，不改 Value/样式/列宽）。
        /// 表格布局约定：第 0 行是表头行，数据行从第 1 行起、与 rows[i] 对齐；列头 HeaderText 是 Excel 字母，
        /// 真实表头文字在 headers[c]。来源取 BomRow.Sources（与 Properties 平行、键同为表头显示名，
        /// 由 CaxaService.ReadRowProperties 逐列写入）。
        /// </summary>
        internal static void ApplyCellTooltips(DataGridView dgv,
                                               IList<string> headers,
                                               IList<BomRow> rows)
        {
            try
            {
                if (dgv == null || headers == null || headers.Count == 0) return;
                if (rows == null || rows.Count == 0) return;
                if (dgv.Columns.Count == 0 || dgv.Rows.Count < 2) return; // 表还没建好/只有表头行

                int maxCols = Math.Min(headers.Count, dgv.Columns.Count);
                for (int i = 0; i < rows.Count; i++)
                {
                    int r = i + 1; // 数据行从第 1 行开始，与 rows[i] 对齐
                    if (r >= dgv.Rows.Count) break;
                    BomRow row = rows[i];
                    if (row == null) continue;

                    IDictionary<string, string> src = row.Sources;
                    for (int c = 0; c < maxCols; c++)
                    {
                        string h = (headers[c] ?? "").Trim();
                        if (h.Length == 0 || IsStructuralHeader(h)) continue;

                        DataGridViewCell cell = dgv.Rows[r].Cells[c];
                        object val = cell.Value;
                        string value = (val == null) ? "" : val.ToString();
                        string source = LookupSource(src, h, headers[c]);
                        cell.ToolTipText = CellToolTip(h, value, source);
                    }
                }
            }
            catch
            {
                // 定时器/回调里抛出会拖死宿主消息泵：静默返回，界面保持原样
            }
        }

        /// <summary>列级“本列主要来源”落到列头 ToolTipText。srcByHeader 为 null 时不设置（同上，不伪造“读不到”）。</summary>
        internal static void ApplyColumnTooltips(DataGridView dgv,
                                                 IList<string> headers,
                                                 IDictionary<string, string> srcByHeader)
        {
            try
            {
                if (dgv == null || headers == null || headers.Count == 0) return;
                if (srcByHeader == null) return;
                if (dgv.Columns.Count == 0) return;

                int maxCols = Math.Min(headers.Count, dgv.Columns.Count);
                for (int c = 0; c < maxCols; c++)
                {
                    string h = (headers[c] ?? "").Trim();
                    if (h.Length == 0 || IsStructuralHeader(h)) continue;
                    string label = LookupLabel(srcByHeader, h);
                    dgv.Columns[c].ToolTipText = "表头：" + h + "｜本列主要来源：" + SourceDesc(label);
                }
            }
            catch
            {
            }
        }

        // ==================================================================== 来源串解析（词表固定四种形态）

        private static string SourceDesc(string source)
        {
            string s = (source ?? "").Trim();
            if (s.Length == 0) return "三条通道均未取到（读不到，非空值）";
            if (string.Equals(s, SrcProp, StringComparison.Ordinal)) return "自定义属性共享表命中（第一优先级）";
            if (s.StartsWith(SrcBomPrefix, StringComparison.Ordinal))
            {
                string label = s.Substring(SrcBomPrefix.Length).Trim();
                return label.Length == 0
                    ? "BOM 导出兜底命中（命中名称缺失）"
                    : "BOM 导出兜底命中「" + OneLine(label) + "」";
            }
            if (s.StartsWith(SrcSpecPrefix, StringComparison.Ordinal))
            {
                string word = s.Substring(SrcSpecPrefix.Length).Trim();
                return word.Length == 0
                    ? "专用兜底（通道词缺失）"
                    : "专用兜底：" + OneLine(word);
            }
            // 词表之外的形态＝数据层异常，原样带出，不折算成任何已知通道
            return "未识别的来源标识：" + OneLine(s);
        }

        /// <summary>分类：0=属性 1=BOM 2=专用 3=读不到 4=未识别（词表外的原样值）。</summary>
        private static int Classify(string label)
        {
            string s = (label ?? "").Trim();
            if (s.Length == 0) return 3;
            if (string.Equals(s, SrcProp, StringComparison.Ordinal)) return 0;
            if (s.StartsWith(SrcBomPrefix, StringComparison.Ordinal)) return 1;
            if (s.StartsWith(SrcSpecPrefix, StringComparison.Ordinal)) return 2;
            return 4;
        }

        // ==================================================================== 内部工具（无状态，全部纯函数）

        // 与 MainForm 的 HC_Level/HC_Drawing/HC_Thumb/HC_Qty/HC_TotalQty 保持同步：这些列不走三通道取数
        private static bool IsStructuralHeader(string trimmed)
        {
            return trimmed == "层级" || trimmed == "是否有工程图" || trimmed == "缩略图"
                || trimmed == "数量" || trimmed == "总数量";
        }

        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        }

        private static void AppendBucket(StringBuilder sb, string title, List<string> names, int total, bool withNames)
        {
            sb.Append(title).Append(' ').Append(names.Count).Append('/').Append(total).Append(" 列");
            if (withNames && names.Count > 0)
                sb.Append("（").Append(JoinNames(names)).Append("）");
        }

        private static string JoinNames(List<string> names)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < names.Count && i < MaxNamesInSummary; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(OneLine(names[i]));
            }
            if (names.Count > MaxNamesInSummary)
                sb.Append("、…共 ").Append(names.Count).Append(" 列");
            return sb.ToString();
        }

        private static string LookupLabel(IDictionary<string, string> map, string header)
        {
            if (map == null || header == null) return null;
            string v;
            if (map.TryGetValue(header, out v)) return v;
            foreach (KeyValuePair<string, string> kv in map)
                if (string.Equals(kv.Key, header, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        // 数据层写入键有两种形态（Properties 里 raw 与 trim 混用），先 trim 再 raw 各试一次
        private static string LookupSource(IDictionary<string, string> src, string headerTrimmed, string headerRaw)
        {
            if (src == null) return "";
            string v;
            if (src.TryGetValue(headerTrimmed, out v)) return v ?? "";
            if (headerRaw != null && !ReferenceEquals(headerRaw, headerTrimmed)
                && src.TryGetValue(headerRaw, out v)) return v ?? "";
            return "";
        }

    }
}
