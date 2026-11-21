// caxa3dtoolboxmaker —— CAXA 3D 标准件库 XML 可视化编辑器（独立 EXE）
// 编译：csc v4.0.30319（C# 4 语法），经 Python subprocess 调用
// 格式保持：字符串级解析/重建，UTF-8 无 BOM，CRLF，属性顺序与自闭合结构原样保留
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.IO.Packaging;

namespace Caxa3dToolboxMaker
{
    // ---------- 数据模型 ----------
    [ComVisible(false)]
    public class Attr
    {
        public string Key;
        public string Value;
        public Attr(string k, string v) { Key = k; Value = v; }
    }

    [ComVisible(false)]
    public enum CheckState { Same, Diff, Unreadable, Na }

    // ---------- 三方比对探针（ics 内部名 / xlsx GUID） ----------
    [ComVisible(false)]
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

    // ---------- XML 保格式内核 ----------
    [ComVisible(false)]
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
            string text = d.Raw;

            // 1) 替换 CurNodeInfo 开标签
            int ci = text.IndexOf("<CurNodeInfo ");
            int cj = text.IndexOf('>', ci);
            text = text.Substring(0, ci) + BuildTag("CurNodeInfo", d.Cur, null) + text.Substring(cj + 1);

            // 2) 逐段替换 Parameter（保持 <Parameter ...>\r\n</Parameter> 结构）
            int pidx = 0;
            Regex prx = new Regex(@"<Parameter\b[^>]*>[\s\S]*?</Parameter>");
            text = prx.Replace(text, delegate(Match m)
            {
                if (pidx >= d.Params.Count) return m.Value;
                List<Attr> pa = d.Params[pidx++];
                return BuildTag("Parameter", pa, null) + "\r\n</Parameter>";
            });

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
            text = text.Substring(0, dj + 1) + body.ToString() + text.Substring(dclose);

            // 备份
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
            int ci = text.IndexOf("<CurNodeInfo ");
            int cj = text.IndexOf('>', ci);
            text = text.Substring(0, ci) + BuildTag("CurNodeInfo", d.Cur, null) + text.Substring(cj + 1);
            int pidx = 0;
            Regex prx = new Regex(@"<Parameter\b[^>]*>[\s\S]*?</Parameter>");
            text = prx.Replace(text, delegate(Match m)
            {
                if (pidx >= d.Params.Count) return m.Value;
                return BuildTag("Parameter", d.Params[pidx++], null) + "\r\n</Parameter>";
            });
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

    // ---------- 自检入口（控制台版 --selftest） ----------
    [ComVisible(false)]
    public static class SelfTest
    {
        public static int Run(string[] args)
        {
            string xml = args.Length > 1 ? args[1] : @"C:\code\ICAPI\自定义设计库\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783.XML";
            string ics = args.Length > 2 ? args[2] : @"C:\code\ICAPI\自定义设计库\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783.ics";
            string xlsx = args.Length > 3 ? args[3] : @"C:\code\ICAPI\自定义设计库\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783.xlsx";
            int fail = 0;
            Action<string, bool> chk = delegate(string name, bool ok) { Console.WriteLine((ok ? "[OK] " : "[FAIL] ") + name); if (!ok) fail++; };

            XmlCore.Doc d = XmlCore.Load(xml);
            Console.WriteLine("Cur字段数=" + d.Cur.Count + " 参数数=" + d.Params.Count + " 规格行数=" + d.Rows.Count);
            chk("CurNodeInfo 解析", d.Cur.Count >= 10);
            chk("参数数=22", d.Params.Count == 22);
            chk("规格行数=330", d.Rows.Count == 330);
            string guid = MetaProbe.NormGuid(XmlCore.Get(d.Cur, "GUID"));
            string icsName = XmlCore.Get(d.Cur, "ICSName");
            Console.WriteLine("XML.GUID=" + guid + " XML.ICSName=" + icsName);
            chk("GUID 非空", !string.IsNullOrEmpty(guid));
            chk("ICSName 非空", !string.IsNullOrEmpty(icsName));

            string diskName = Path.GetFileName(ics);
            string innerName = MetaProbe.ReadIcsInnerName(ics);
            string xlsxGuid = MetaProbe.ReadXlsxGuid(xlsx);
            Console.WriteLine("磁盘ics名=" + diskName);
            Console.WriteLine("ics内部名=" + (innerName ?? "<读不到>"));
            Console.WriteLine("xlsx.GUID=" + (xlsxGuid ?? "<读不到>"));
            chk("ics内部名可读", innerName != null);
            chk("xlsx GUID可读", xlsxGuid != null);
            chk("XML.GUID == xlsx.GUID", guid == xlsxGuid);
            chk("XML.ICSName == 磁盘ics名", icsName == diskName);
            chk("ics内部名是旧名(与当前不同)", innerName != null && innerName != icsName);

            string rendered = XmlCore.Render(d);
            if (rendered != d.Raw)
            {
                string dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_dump");
                Directory.CreateDirectory(dumpDir);
                File.WriteAllText(Path.Combine(dumpDir, "raw.xml"), d.Raw, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dumpDir, "rendered.xml"), rendered, new UTF8Encoding(false));
                Console.WriteLine("[DUMP] _dump/raw.xml 与 _dump/rendered.xml 已生成");
                // 定位首个差异
                int k = 0; int mn = Math.Min(d.Raw.Length, rendered.Length);
                while (k < mn && d.Raw[k] == rendered[k]) k++;
                Console.WriteLine("[DUMP] 首个差异位置=" + k + " raw.len=" + d.Raw.Length + " render.len=" + rendered.Length);
                Console.WriteLine("[DUMP] raw:  " + Esc(d.Raw, k));
                Console.WriteLine("[DUMP] rend: " + Esc(rendered, k));
            }
            chk("保格式round-trip字节一致", rendered == d.Raw);
            XmlCore.Doc d2 = new XmlCore.Doc();
            // 重新解析渲染结果校验行数
            string tmp = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_selftest.xml");
            File.WriteAllText(tmp, rendered, new UTF8Encoding(false));
            XmlCore.Doc reload = XmlCore.Load(tmp);
            chk("重载行数一致", reload.Rows.Count == d.Rows.Count);
            chk("重载参数数一致", reload.Params.Count == d.Params.Count);
            chk("重载GUID一致", MetaProbe.NormGuid(XmlCore.Get(reload.Cur, "GUID")) == guid);

            Console.WriteLine("=== SELFTEST " + (fail == 0 ? "PASS" : "FAIL(" + fail + ")") + " ===");
            return fail == 0 ? 0 : 1;
        }

        static string Esc(string s, int k)
        {
            int st = Math.Max(0, k - 30);
            string seg = s.Substring(st, Math.Min(90, s.Length - st));
            return seg.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }

    // ---------- 主界面 ----------
    [ComVisible(false)]
    public class MainForm : Form
    {
        TextBox tbXml, tbIcs, tbXlsx;
        DataGridView dgvCheck, dgvCur, dgvParam, dgvRow;
        DataTable dtCheck, dtCur, dtParam, dtRow;
        Label lblStatus;
        string xmlPath;
        XmlCore.Doc doc;
        List<List<string>> paramKeyOrder; // 每个 Parameter 的原始键顺序（保序重建）

        static readonly Font F = new Font("宋体", 9F);

        public MainForm(string[] args)
        {
            Text = "CAXA 3D 标准件库 XML 编辑器 - toolboxmaker";
            Font = F;
            BackColor = SystemColors.Control;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1190, 700);

            BuildUi();

            if (args != null && args.Length > 0 && File.Exists(args[0])) tbXml.Text = args[0];
            else
            {
                string def = @"C:\code\ICAPI\自定义设计库\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783.XML";
                if (File.Exists(def)) tbXml.Text = def;
            }
            InferSiblings();
        }

        void BuildUi()
        {
            // ===== 文件区 =====
            GroupBox gbFile = new GroupBox();
            gbFile.Text = "文件（XML 必选；ICS / XLSX 用于文件名与 GUID 比对，可留空）";
            gbFile.Font = F; gbFile.FlatStyle = FlatStyle.System;
            gbFile.Location = new Point(12, 10); gbFile.Size = new Size(1166, 104);
            Controls.Add(gbFile);

            Button bXml, bIcs, bXlsx;
            tbXml = AddFileRow(gbFile, 24, "XML：", out bXml);
            tbIcs = AddFileRow(gbFile, 52, "ICS：", out bIcs);
            tbXlsx = AddFileRow(gbFile, 80, "XLSX：", out bXlsx);
            bXml.Click += delegate { Pick(tbXml, "XML 文件|*.XML;*.xml|所有文件|*.*"); InferSiblings(); };
            bIcs.Click += delegate { Pick(tbIcs, "ICS 文件|*.ics|所有文件|*.*"); };
            bXlsx.Click += delegate { Pick(tbXlsx, "Excel 文件|*.xlsx|所有文件|*.*"); };

            // ===== 比对区 =====
            GroupBox gbCheck = new GroupBox();
            gbCheck.Text = "文件名与 GUID 三方比对（三态：一致 / 不一致 / 读不到）";
            gbCheck.Font = F; gbCheck.FlatStyle = FlatStyle.System;
            gbCheck.Location = new Point(12, 122); gbCheck.Size = new Size(1166, 148);
            Controls.Add(gbCheck);
            dtCheck = new DataTable();
            dtCheck.Columns.Add("检查项"); dtCheck.Columns.Add("XML 记录值");
            dtCheck.Columns.Add("磁盘 ICS"); dtCheck.Columns.Add("ICS 内部元数据");
            dtCheck.Columns.Add("XLSX 元数据"); dtCheck.Columns.Add("判定");
            dgvCheck = MakeGrid(dtCheck, new Point(10, 22), new Size(1146, 116), false);
            dgvCheck.CellFormatting += dgvCheck_CellFormatting;
            gbCheck.Controls.Add(dgvCheck);

            // ===== Tab 区 =====
            TabControl tc = new TabControl();
            tc.Font = F; tc.Location = new Point(12, 278); tc.Size = new Size(1166, 344);
            Controls.Add(tc);

            TabPage tp1 = new TabPage("规格表 DataTable（可增删改行 / 双击单元格编辑）");
            tp1.Font = F;
            dtRow = new DataTable();
            dgvRow = MakeGrid(dtRow, new Point(4, 4), new Size(1150, 330), true);
            tp1.Controls.Add(dgvRow); tc.Controls.Add(tp1);

            TabPage tp2 = new TabPage("参数表 ConfigureTable（AddtoDisplayName / Grid_item_state 等）");
            tp2.Font = F;
            dtParam = new DataTable();
            dgvParam = MakeGrid(dtParam, new Point(4, 4), new Size(1150, 330), true);
            tp2.Controls.Add(dgvParam); tc.Controls.Add(tp2);

            TabPage tp3 = new TabPage("族信息 CurNodeInfo（name / display_name / user_name / GUID 等）");
            tp3.Font = F;
            dtCur = new DataTable();
            dtCur.Columns.Add("属性"); dtCur.Columns.Add("值");
            dgvCur = MakeGrid(dtCur, new Point(4, 4), new Size(560, 330), true);
            if (dgvCur.Columns.Count > 0) dgvCur.Columns[0].ReadOnly = true;
            tp3.Controls.Add(dgvCur);

            Button bAddRow = new Button(); bAddRow.Text = "新增规格行"; bAddRow.Font = F; bAddRow.FlatStyle = FlatStyle.System;
            bAddRow.Location = new Point(580, 20); bAddRow.Size = new Size(120, 30);
            bAddRow.Click += delegate { AddRow(); }; tp3.Controls.Add(bAddRow);
            Button bDelRow = new Button(); bDelRow.Text = "删除选中行"; bDelRow.Font = F; bDelRow.FlatStyle = FlatStyle.System;
            bDelRow.Location = new Point(580, 60); bDelRow.Size = new Size(120, 30);
            bDelRow.Click += delegate { DelRow(); }; tp3.Controls.Add(bDelRow);
            Label tip = new Label(); tip.Font = F; tip.Text = "显示名要点（规则第46节）：\r\n前缀回退链 user_name→display_name→name→ICSName\r\n参数层唯一 AddtoDisplayName=TRUE 给目标字段\r\n目标字段须 Grid_item_state=DIM_CTRL_SHOW";
            tip.Location = new Point(580, 110); tip.Size = new Size(560, 130); tp3.Controls.Add(tip);
            tc.Controls.Add(tp3);

            // ===== 底部按钮 =====
            AddButton("打开文件", 12, 632, delegate { LoadXml(); });
            AddButton("重新比对", 128, 632, delegate { RefreshCheck(); });
            AddButton("保存 XML（自动备份）", 244, 632, delegate { SaveXml(); });
            AddButton("导出规格 CSV", 408, 632, delegate { ExportCsv(); });
            AddButton("关闭", 524, 632, delegate { this.Close(); });

            lblStatus = new Label(); lblStatus.Font = F; lblStatus.Text = "就绪。";
            lblStatus.Location = new Point(12, 672); lblStatus.Size = new Size(1166, 22);
            lblStatus.BorderStyle = BorderStyle.Fixed3D;
            Controls.Add(lblStatus);
        }

        TextBox AddFileRow(GroupBox gb, int y, string label, out Button btn)
        {
            Label l = new Label(); l.Text = label; l.Font = F; l.Location = new Point(10, y + 3); l.Size = new Size(52, 20);
            gb.Controls.Add(l);
            TextBox tb = new TextBox(); tb.Font = F; tb.BorderStyle = BorderStyle.Fixed3D;
            tb.Location = new Point(64, y); tb.Size = new Size(1040, 22); gb.Controls.Add(tb);
            Button b = new Button(); b.Text = "浏览"; b.Font = F; b.FlatStyle = FlatStyle.System;
            b.Location = new Point(1112, y - 2); b.Size = new Size(48, 26); gb.Controls.Add(b);
            btn = b; return tb;
        }

        DataGridView MakeGrid(DataTable dt, Point loc, Size size, bool editable)
        {
            DataGridView g = new DataGridView();
            g.Font = F; g.Location = loc; g.Size = size;
            g.DataSource = dt;
            g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false;
            g.ReadOnly = !editable;
            g.BackgroundColor = SystemColors.Window;
            g.BorderStyle = BorderStyle.Fixed3D;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            g.SelectionMode = DataGridViewSelectionMode.CellSelect;
            return g;
        }

        void AddButton(string text, int x, int y, EventHandler h)
        {
            Button b = new Button(); b.Text = text; b.Font = F; b.FlatStyle = FlatStyle.System;
            b.Location = new Point(x, y); b.Size = new Size(112, 32);
            b.Click += h; Controls.Add(b);
        }

        void Pick(TextBox tb, string filter)
        {
            OpenFileDialog ofd = new OpenFileDialog(); ofd.Filter = filter;
            if (ofd.ShowDialog(this) == DialogResult.OK) tb.Text = ofd.FileName;
        }

        void InferSiblings()
        {
            string x = tbXml.Text;
            if (string.IsNullOrEmpty(x) || !File.Exists(x)) return;
            string dir = Path.GetDirectoryName(x);
            string stem = Path.GetFileNameWithoutExtension(x);
            string ics = Path.Combine(dir, stem + ".ics");
            string xl = Path.Combine(dir, stem + ".xlsx");
            if (string.IsNullOrEmpty(tbIcs.Text) && File.Exists(ics)) tbIcs.Text = ics;
            if (string.IsNullOrEmpty(tbXlsx.Text) && File.Exists(xl)) tbXlsx.Text = xl;
        }

        void LoadXml()
        {
            string p = tbXml.Text;
            if (!File.Exists(p)) { lblStatus.Text = "XML 文件不存在：" + p; return; }
            try
            {
                xmlPath = p;
                doc = XmlCore.Load(p);
                FillCur(); FillParam(); FillRow();
                RefreshCheck();
                lblStatus.Text = "已加载：" + p + "  （" + doc.Rows.Count + " 规格行 / " + doc.Params.Count + " 参数）";
            }
            catch (Exception ex) { lblStatus.Text = "加载失败：" + ex.Message; }
        }

        void FillCur()
        {
            dtCur.Rows.Clear();
            foreach (Attr a in doc.Cur) dtCur.Rows.Add(a.Key, a.Value);
        }

        void FillParam()
        {
            dtParam.Rows.Clear(); dtParam.Columns.Clear();
            foreach (string k in doc.ParamKeysUnion) dtParam.Columns.Add(k);
            paramKeyOrder = new List<List<string>>();
            foreach (List<Attr> pa in doc.Params)
            {
                List<string> order = new List<string>();
                object[] cells = new object[dtParam.Columns.Count];
                for (int c = 0; c < dtParam.Columns.Count; c++)
                {
                    string k = dtParam.Columns[c].ColumnName;
                    string v = XmlCore.Get(pa, k);
                    cells[c] = v ?? "";
                    if (v != null && !order.Contains(k)) order.Add(k);
                }
                // 保留该 Parameter 原有的全部键顺序（即使值为空也保留键）
                foreach (Attr a in pa) if (!order.Contains(a.Key)) order.Add(a.Key);
                paramKeyOrder.Add(order);
                dtParam.Rows.Add(cells);
            }
        }

        void FillRow()
        {
            dtRow.Rows.Clear(); dtRow.Columns.Clear();
            foreach (string k in doc.RowKeys) dtRow.Columns.Add(k);
            foreach (List<Attr> row in doc.Rows)
            {
                object[] cells = new object[dtRow.Columns.Count];
                for (int c = 0; c < dtRow.Columns.Count; c++)
                    cells[c] = XmlCore.Get(row, dtRow.Columns[c].ColumnName) ?? "";
                dtRow.Rows.Add(cells);
            }
        }

        void AddRow()
        {
            if (dtRow.Columns.Count == 0) return;
            object[] cells = new object[dtRow.Columns.Count];
            for (int i = 0; i < cells.Length; i++) cells[i] = "";
            dtRow.Rows.Add(cells);
            lblStatus.Text = "已追加空规格行（保存后写入 XML）。";
        }

        void DelRow()
        {
            if (dgvRow.SelectedRows.Count == 0) return;
            List<DataGridViewRow> rm = new List<DataGridViewRow>();
            foreach (DataGridViewRow r in dgvRow.SelectedRows) if (!r.IsNewRow) rm.Add(r);
            foreach (DataGridViewRow r in rm) dtRow.Rows.RemoveAt(r.Index);
            lblStatus.Text = "已删除选中规格行（保存后写入 XML）。";
        }

        // 把界面 DataTable 写回内存模型（保存/比对前同步）
        void SyncFromUi()
        {
            if (doc == null) return;
            // Cur
            List<Attr> newCur = new List<Attr>();
            foreach (DataRow r in dtCur.Rows)
                newCur.Add(new Attr(Convert.ToString(r[0]), Convert.ToString(r[1])));
            doc.Cur = newCur;
            // Param：按每个参数原始键顺序，值取界面列
            List<List<Attr>> newParams = new List<List<Attr>>();
            for (int i = 0; i < dtParam.Rows.Count && i < paramKeyOrder.Count; i++)
            {
                List<Attr> pa = new List<Attr>();
                foreach (string k in paramKeyOrder[i])
                    pa.Add(new Attr(k, Convert.ToString(dtParam.Rows[i][k])));
                newParams.Add(pa);
            }
            doc.Params = newParams;
            // Row
            List<List<Attr>> newRows = new List<List<Attr>>();
            foreach (DataRow r in dtRow.Rows)
            {
                List<Attr> ra = new List<Attr>();
                foreach (string k in doc.RowKeys) ra.Add(new Attr(k, Convert.ToString(r[k])));
                newRows.Add(ra);
            }
            doc.Rows = newRows;
        }

        void SaveXml()
        {
            if (doc == null || string.IsNullOrEmpty(xmlPath)) { lblStatus.Text = "请先打开 XML。"; return; }
            try
            {
                SyncFromUi();
                string backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_backup_原始数据");
                XmlCore.Save(xmlPath, doc, backupDir);
                lblStatus.Text = "已保存：" + xmlPath + "（备份目录：" + backupDir + "）";
            }
            catch (Exception ex) { lblStatus.Text = "保存失败：" + ex.Message; }
        }

        void ExportCsv()
        {
            if (dtRow.Columns.Count == 0) return;
            SaveFileDialog sfd = new SaveFileDialog(); sfd.Filter = "CSV 文件|*.csv"; sfd.FileName = "规格表.csv";
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                for (int c = 0; c < dtRow.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(Csv(dtRow.Columns[c].ColumnName));
                }
                sb.Append("\r\n");
                foreach (DataRow r in dtRow.Rows)
                {
                    for (int c = 0; c < dtRow.Columns.Count; c++)
                    {
                        if (c > 0) sb.Append(',');
                        sb.Append(Csv(Convert.ToString(r[c])));
                    }
                    sb.Append("\r\n");
                }
                File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
                lblStatus.Text = "已导出：" + sfd.FileName;
            }
            catch (Exception ex) { lblStatus.Text = "导出失败：" + ex.Message; }
        }

        static string Csv(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        void RefreshCheck()
        {
            dtCheck.Rows.Clear();
            if (doc == null) { lblStatus.Text = "请先打开 XML。"; return; }
            SyncFromUi();
            string xmlName = XmlCore.Get(doc.Cur, "ICSName");
            string xmlGuid = MetaProbe.NormGuid(XmlCore.Get(doc.Cur, "GUID"));
            string diskName = File.Exists(tbIcs.Text) ? Path.GetFileName(tbIcs.Text) : null;
            string innerName = MetaProbe.ReadIcsInnerName(tbIcs.Text);
            string xlsxGuid = MetaProbe.ReadXlsxGuid(tbXlsx.Text);

            // 文件名三方
            int readable = 0; bool allSame = true;
            string a = xmlName, b = diskName, c = innerName;
            if (!string.IsNullOrEmpty(a)) readable++;
            if (!string.IsNullOrEmpty(b)) readable++;
            if (!string.IsNullOrEmpty(c)) readable++;
            if (readable >= 2)
            {
                string first = !string.IsNullOrEmpty(a) ? a : (!string.IsNullOrEmpty(b) ? b : c);
                if (!string.IsNullOrEmpty(a) && a != first) allSame = false;
                if (!string.IsNullOrEmpty(b) && b != first) allSame = false;
                if (!string.IsNullOrEmpty(c) && c != first) allSame = false;
            }
            CheckState fnState = (string.IsNullOrEmpty(b) || string.IsNullOrEmpty(c)) ? CheckState.Unreadable
                                 : (allSame ? CheckState.Same : CheckState.Diff);
            dtCheck.Rows.Add("ICS 文件名", a ?? "<空>", b ?? "<读不到>", c ?? "<读不到>", "—", StateText(fnState));

            // GUID
            CheckState gState;
            if (string.IsNullOrEmpty(xmlGuid) || string.IsNullOrEmpty(xlsxGuid)) gState = CheckState.Unreadable;
            else gState = (xmlGuid == xlsxGuid ? CheckState.Same : CheckState.Diff);
            dtCheck.Rows.Add("GUID", xmlGuid ?? "<空>", "—（实测 ics 内无 GUID 字段）", "<读不到>", xlsxGuid ?? "<读不到>", StateText(gState));

            lblStatus.Text = "比对完成：文件名=" + StateText(fnState) + "，GUID=" + StateText(gState)
                             + "。ics 内无 GUID 字段（实测 0 命中），如实报读不到。";
        }

        static string StateText(CheckState s)
        {
            switch (s)
            {
                case CheckState.Same: return "[一致]";
                case CheckState.Diff: return "[不一致]";
                case CheckState.Unreadable: return "[读不到]";
                default: return "[不适用]";
            }
        }

        void dgvCheck_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.ColumnIndex != 5 || e.RowIndex < 0) return;
            string v = Convert.ToString(e.Value);
            if (v == "[一致]") { e.CellStyle.ForeColor = Color.Green; e.CellStyle.Font = new Font(F, FontStyle.Bold); }
            else if (v == "[不一致]") { e.CellStyle.ForeColor = Color.Red; e.CellStyle.Font = new Font(F, FontStyle.Bold); }
            else if (v == "[读不到]") { e.CellStyle.ForeColor = Color.Gray; e.CellStyle.Font = new Font(F, FontStyle.Bold); }
        }
    }

    [ComVisible(false)]
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // UI 铁律：不调用 Application.EnableVisualStyles()，保持 Win2000 经典外观
            Application.Run(new MainForm(args));
        }
    }

    // 控制台自检入口：单独编译成 console exe（/main:Caxa3dToolboxMaker.SelftestMain），
    // 与 GUI 共用同一份源码，保证回归测的就是生产代码本体。
    [ComVisible(false)]
    public static class SelftestMain
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--uitest")
                return UiTest.Run(args);
            return SelfTest.Run(args);
        }
    }

    // 界面离屏验证：实例化 MainForm（不进消息循环），反射读控件断言 + DrawToBitmap 截图
    [ComVisible(false)]
    public static class UiTest
    {
        public static int Run(string[] args)
        {
            int fail = 0;
            Action<string, bool> chk = delegate(string name, bool ok)
            { Console.WriteLine((ok ? "[OK] " : "[FAIL] ") + name); if (!ok) fail++; };

            MainForm f = new MainForm(new string[0]);
            // 句柄在 Form 构造中读取 ClientSize 时已惰性创建；CreateHandle() 是 protected 不可直接调（坑）
            Type t = typeof(MainForm);
            Func<string, object> field = delegate(string n)
            {
                System.Reflection.FieldInfo fi = t.GetField(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return fi == null ? null : fi.GetValue(f);
            };
            Action<string, object> setf = delegate(string n, object v)
            {
                System.Reflection.FieldInfo fi = t.GetField(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (fi != null) fi.SetValue(f, v);
            };

            chk("窗体创建成功", f != null);
            chk("ClientSize=1190x700", f.ClientSize.Width == 1190 && f.ClientSize.Height == 700);

            DataGridView dgRow = field("dgvRow") as DataGridView;
            DataGridView dgParam = field("dgvParam") as DataGridView;
            DataGridView dgCur = field("dgvCur") as DataGridView;
            DataGridView dgCheck = field("dgvCheck") as DataGridView;
            DataTable dtRowT = field("dtRow") as DataTable;
            DataTable dtParamT = field("dtParam") as DataTable;
            DataTable dtCurT = field("dtCur") as DataTable;
            DataTable dtCheckT = field("dtCheck") as DataTable;
            chk("四个DataGridView齐全", dgRow != null && dgParam != null && dgCur != null && dgCheck != null);
            chk("四个DataTable齐全", dtRowT != null && dtParamT != null && dtCurT != null && dtCheckT != null);
            chk("比对表6列", dtCheckT.Columns.Count == 6);
            chk("族信息表2列", dtCurT.Columns.Count == 2);

            // 触发加载（默认路径已在构造函数探测）
            System.Reflection.MethodInfo load = t.GetMethod("LoadXml", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            load.Invoke(f, null);
            chk("规格表330行", dtRowT.Rows.Count == 330);
            chk("规格表21列", dtRowT.Columns.Count == 21);
            Console.WriteLine("[INFO] 参数表列数=" + dtParamT.Columns.Count);
            chk("参数表列数>=10", dtParamT.Columns.Count >= 10);
            chk("族信息表12行", dtCurT.Rows.Count == 12);
            chk("比对表2行", dtCheckT.Rows.Count == 2);

            // 比对结果断言（读 DataTable 而非 DataGridView，避免未布局时列未生成）
            string fnResult = Convert.ToString(dtCheckT.Rows[0][5]);
            string guidResult = Convert.ToString(dtCheckT.Rows[1][5]);
            Console.WriteLine("[INFO] 文件名判定=" + fnResult + " GUID判定=" + guidResult);
            chk("文件名判定为[不一致](ics内部旧名)", fnResult == "[不一致]");
            chk("GUID判定为[一致]", guidResult == "[一致]");

            // 坐标断言（布局结论来自 Bounds，不来自截图）
            // BuildUi 里 GroupBox/TabControl 是局部变量，改为遍历 Controls 按类型/文本定位
            GroupBox gbf = null, gbc = null; TabControl tabc = null;
            foreach (Control c in f.Controls)
            {
                GroupBox gb = c as GroupBox;
                if (gb != null && gb.Text.Contains("XML 必选")) gbf = gb;
                if (gb != null && gb.Text.Contains("三方比对")) gbc = gb;
                TabControl tcx = c as TabControl;
                if (tcx != null) tabc = tcx;
            }
            chk("文件区在比对区上方", gbf != null && gbc != null && gbf.Bottom <= gbc.Top);
            chk("比对区在Tab区上方", gbc != null && tabc != null && gbc.Bottom <= tabc.Top);
            chk("文件区不超出客户区", gbf != null && gbf.Right <= f.ClientSize.Width && gbf.Bottom <= f.ClientSize.Height);
            chk("Tab区不超出客户区", tabc != null && tabc.Right <= f.ClientSize.Width + 20 && tabc.Bottom <= f.ClientSize.Height + 20);
            if (gbf != null) Console.WriteLine("[INFO] 文件区 Bounds=" + gbf.Bounds);
            if (gbc != null) Console.WriteLine("[INFO] 比对区 Bounds=" + gbc.Bounds);
            if (tabc != null) Console.WriteLine("[INFO] Tab区 Bounds=" + tabc.Bounds);

            // 离屏截图：先 Show + DoEvents 让子控件真正绘制（未显示窗体 DrawToBitmap 只有背景）
            try
            {
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(150);
                Application.DoEvents();
                string dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_dump");
                Directory.CreateDirectory(dumpDir);
                System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                string png = Path.Combine(dumpDir, "mainform.png");
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                bmp.Dispose();
                f.Hide();
                Console.WriteLine("[INFO] 截图=" + png);
                chk("截图成功", File.Exists(png) && new FileInfo(png).Length > 1000);
            }
            catch (Exception ex) { chk("截图成功(false):" + ex.Message, false); }

            f.Dispose();
            Console.WriteLine("=== UITEST " + (fail == 0 ? "PASS" : "FAIL(" + fail + ")") + " ===");
            return fail == 0 ? 0 : 1;
        }
    }
}
