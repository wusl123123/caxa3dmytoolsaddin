using System;
using System.Data;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Caxa3dToolboxMaker
{
    public partial class MainForm : Form
    {
        string xmlPath;
        string cachePath;
        bool _inferring;   // 三栏联动防重入：自动填 ICS/CACHE 会触发 TextChanged，避免来回推导
        CacheCore.CacheDoc cacheDoc;   // .cache 内存文档（编辑后从表格回写再保存）
        XmlCore.Doc doc;
        System.Collections.Generic.List<System.Collections.Generic.List<string>> paramKeyOrder; // 每个 Parameter 的原始键顺序（保序重建）

        static readonly Font F = new Font("宋体", 9F);

        public MainForm(string[] args)
        {
            InitializeComponent();
            Diagnostics.LineAppended += OnTraceLine;
            FormClosed += delegate { Diagnostics.LineAppended -= OnTraceLine; };

            // 内部不再预置默认数据：地址栏留空，仅命令行显式传入时才带入 XML
            if (args != null && args.Length > 0 && File.Exists(args[0])) tbXml.Text = args[0];
            InferSiblings();

            // ★ Show 后父容器尺寸才最终确定。构造期父容器(TabPage)还是默认 200x100，
            //   表格 1150x472 相对它产生负 margin；Anchor=All 在 Show 布局时会按负 margin
            //   把表格反算撑到 2108x875（超出窗口），滚动条画在屏幕外看不到。
            //   故 Show 后解除 Anchor→按父容器实际尺寸钳制→恢复 Anchor，强制 margin 归正；
            //   之后窗口缩放由 Anchor 正常随动，滚动条可见。
            Shown += delegate { ClampGridsToParent(); };
        }

        // 把表格钳制到父容器实际可用区（解除 Anchor 先设尺寸，再恢复 Anchor 让 margin 归正）
        void ClampGridsToParent()
        {
            ClampOne(dgvRow, 4, 38, 4, 4);
            ClampOne(dgvParam, 4, 38, 4, 4);
            ClampOne(dgvCache, 4, 38, 4, 4);
            // 族信息表 tp3：左侧表格 + 右侧按钮/tip，表格只占左边，不能用满宽
            if (dgvCur != null && dgvCur.Parent != null)
            {
                AnchorStyles a = dgvCur.Anchor;
                dgvCur.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                dgvCur.Size = new Size(560, Math.Max(200, dgvCur.Parent.ClientSize.Height - 8));
                dgvCur.Anchor = a;
            }
        }

        void ClampOne(DataGridView g, int left, int top, int right, int bottom)
        {
            if (g == null || g.Parent == null) return;
            AnchorStyles a = g.Anchor;
            g.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            int w = Math.Max(200, g.Parent.ClientSize.Width - left - right);
            int h = Math.Max(120, g.Parent.ClientSize.Height - top - bottom);
            g.Size = new Size(w, h);
            g.Anchor = a;
            Diagnostics.Log("钳制表格 " + (g == dgvRow ? "规格表" : (g == dgvParam ? "参数表" : "目录索引表")) +
                " → " + w + "x" + h + "（父容器 " + g.Parent.ClientSize.Width + "x" + g.Parent.ClientSize.Height + "）");
        }

        private void OnTraceLine(string line) { AppendUiLog(line); }

        private void AppendUiLog(string line)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action<string>(AppendUiLog), line); return; }
                lblStatus.Text = line;
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // 浏览按钮的默认目录：动态反查系统安装后的 CAXA 3D ToolBox；找不到时退到 CAXA 用户根，再退到程序目录
        internal static string DefaultBrowseDirectory()
        {
            string toolbox = ToolboxPath.Resolve();
            if (!string.IsNullOrEmpty(toolbox) && Directory.Exists(toolbox)) return toolbox;

            string caxaRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAXA");
            if (Directory.Exists(caxaRoot)) return caxaRoot;
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        void Pick(TextBox tb, string filter)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = filter;
            ofd.InitialDirectory = DefaultBrowseDirectory();
            ofd.RestoreDirectory = true;
            if (ofd.ShowDialog(this) == DialogResult.OK) tb.Text = ofd.FileName;
        }

        // 三栏联动（XML / ICS / CACHE）：
        //   - XML ↔ ICS 同名互选：选了 XML → 同目录同名 .ics 自动填 ICS；选了 ICS → 同目录同名 .XML 自动填 XML。
        //   - CACHE 选择规则：取 XML 所在文件夹的“上一级”目录内的 .cache（标准件库中族 XML 的上一级即类目 .cache）。
        // 只自动填“存在”的同名文件，文件不在就留空原值（第三态，不臆造路径）；_inferring 防止 TextChanged 循环回填。
        // 以 XML 为最后改动者（构造传参 / 选 XML / cache 定位族 XML 走这条）
        void InferSiblings()
        {
            if (_inferring) return;
            _inferring = true;
            try { InferFromXml(); InferCache(); }
            finally { _inferring = false; }
        }

        // 以 ICS 为最后改动者（选 ICS / 手动改 ICS 地址栏走这条）：只反推 XML，不用旧 XML 覆盖刚选的 ICS
        void InferSiblingsFromIcs()
        {
            if (_inferring) return;
            _inferring = true;
            try { InferFromIcs(); InferCache(); }
            finally { _inferring = false; }
        }

        // 以 XML 为准：同目录同名 ICS + 上一级目录的 .cache
        void InferFromXml()
        {
            string x = tbXml.Text;
            if (string.IsNullOrEmpty(x) || !File.Exists(x)) return;
            string dir = Path.GetDirectoryName(x);
            string stem = Path.GetFileNameWithoutExtension(x);
            string ics = Path.Combine(dir, stem + ".ics");
            if (File.Exists(ics) && !string.Equals(tbIcs.Text, ics, StringComparison.OrdinalIgnoreCase))
                tbIcs.Text = ics;
        }

        // 以 ICS 为准：同目录同名 XML（大小写不敏感，.XML/.xml 都认）+ 上一级目录的 .cache
        void InferFromIcs()
        {
            string i = tbIcs.Text;
            if (string.IsNullOrEmpty(i) || !File.Exists(i)) return;
            string dir = Path.GetDirectoryName(i);
            string stem = Path.GetFileNameWithoutExtension(i);
            // XML 必选栏已有有效值且与之同名时不覆盖（尊重用户当前选择），否则按同名回填
            string cur = tbXml.Text;
            if (!string.IsNullOrEmpty(cur) && File.Exists(cur) &&
                string.Equals(Path.GetFileNameWithoutExtension(cur), stem, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(cur), dir, StringComparison.OrdinalIgnoreCase))
                return;
            string[] xmls = Directory.GetFiles(dir, "*.xml"); // Windows 大小写不敏感，覆盖 .XML/.xml
            string pick = null;
            foreach (string x in xmls)
                if (string.Equals(Path.GetFileNameWithoutExtension(x), stem, StringComparison.OrdinalIgnoreCase)) { pick = x; break; }
            if (pick != null) tbXml.Text = pick;
        }

        // CACHE：XML（优先）或 ICS 所在文件夹的上一级目录内的 .cache；只在文件确实存在时填。
        void InferCache()
        {
            string anchor = tbXml.Text;
            if (string.IsNullOrEmpty(anchor) || !File.Exists(anchor)) anchor = tbIcs.Text;
            if (string.IsNullOrEmpty(anchor) || !File.Exists(anchor)) return;
            string dir = Path.GetDirectoryName(anchor);
            string parent = Path.GetDirectoryName(dir);   // XML 所在文件夹的上一级
            if (string.IsNullOrEmpty(parent)) return;
            string[] caches = Directory.GetFiles(parent, "*.cache");
            if (caches.Length == 0) return;
            // 类名 .cache 通常唯一；多个则优先名为 ".cache" 的，其余不猜
            string pick = null;
            foreach (string c in caches)
                if (string.Equals(Path.GetFileName(c), ".cache", StringComparison.OrdinalIgnoreCase)) { pick = c; break; }
            if (pick == null && caches.Length == 1) pick = caches[0];
            if (pick != null && !string.Equals(tbCache.Text, pick, StringComparison.OrdinalIgnoreCase))
                tbCache.Text = pick;
        }

        void LoadXml()
        {
            string p = tbXml.Text;
            if (!File.Exists(p)) { lblStatus.Text = "XML 文件不存在：" + p; return; }
            try
            {
                xmlPath = p;
                doc = XmlCore.Load(p);
                Diagnostics.Log("已加载 XML：" + p + "（" + doc.Rows.Count + " 行 / " + doc.Params.Count + " 参数）");
                FillCur(); FillParam(); FillRow();
                // 行标题：冻结首列（规格列），横向滚动时规格名始终可见（net48 用 column.Frozen）
                if (dgvRow.Columns.Count > 0) dgvRow.Columns[0].Frozen = true;
                lblStatus.Text = "已加载：" + p + "  （" + doc.Rows.Count + " 规格行 / " + doc.Params.Count + " 参数）";
            }
            catch (Exception ex) { lblStatus.Text = "加载失败：" + ex.Message; Diagnostics.Exception("加载 XML 失败", ex); }
        }

        // ===== .cache 目录索引：加载显示 + 按 name 定位族 XML（索引只读，不回写） =====
        void LoadCache()
        {
            string p = tbCache.Text;
            if (string.IsNullOrEmpty(p)) { lblStatus.Text = "请先选择 .cache 索引文件。"; return; }
            if (!File.Exists(p))
            {
                lblStatus.Text = ".cache 文件不存在：" + p;
                Diagnostics.Log("cache 加载失败 | 文件不存在 | " + p);
                return;
            }
            try
            {
                CacheCore.CacheDoc cd = CacheCore.Load(p);
                cachePath = p;
                cacheDoc = cd;
                dtCache.Rows.Clear(); dtCache.Columns.Clear();
                foreach (string k in cd.KeysUnion) dtCache.Columns.Add(k);
                foreach (CacheCore.Entry e in cd.Entries)
                {
                    object[] cells = new object[dtCache.Columns.Count];
                    for (int c = 0; c < dtCache.Columns.Count; c++)
                        cells[c] = XmlCore.Get(e.Attrs, dtCache.Columns[c].ColumnName) ?? "";
                    dtCache.Rows.Add(cells);
                }
                // 行标题：冻结首列（name），横向滚动时族名常驻（与规格表同口径）
                if (dgvCache.Columns.Count > 0) dgvCache.Columns[0].Frozen = true;
                Diagnostics.Log("已加载 .cache：" + p + "（version=" + (cd.Version ?? "<none>") + " / " + cd.Entries.Count + " 条）");
                lblStatus.Text = "已加载目录索引：" + p + "（" + cd.Entries.Count + " 条；双击行打开对应族 XML）";
            }
            catch (Exception ex) { lblStatus.Text = ".cache 加载失败：" + ex.Message; Diagnostics.Exception("加载 .cache 失败", ex); }
        }

        // 按选中条目的 name 在 .cache 同目录下找同名族文件夹里的 XML：
        // 优先文件名与 name 同名；仅一个 XML 时取它；多个且无同名 → 第三态提示，不猜。
        void OpenSelectedFamilyXml()
        {
            if (string.IsNullOrEmpty(cachePath) || dgvCache.CurrentCell == null || dtCache.Rows.Count == 0)
            { lblStatus.Text = "请先加载 .cache 并点选一行。"; return; }
            int idx = dgvCache.CurrentCell.RowIndex;
            if (idx < 0 || idx >= dtCache.Rows.Count) return;
            string name = dtCache.Columns.Contains("name") ? Convert.ToString(dtCache.Rows[idx]["name"]) : "";
            if (string.IsNullOrEmpty(name))
            {
                lblStatus.Text = "该条目无 name 属性，无法定位（第三态）。";
                Diagnostics.Log("cache 定位 | 条目无 name | 行 " + (idx + 1));
                return;
            }
            string dir = Path.Combine(Path.GetDirectoryName(cachePath), name);
            if (!Directory.Exists(dir))
            {
                lblStatus.Text = "未找到族目录（第三态）：" + dir;
                Diagnostics.Log("cache 定位 | 目录不存在 | " + dir);
                return;
            }
            string[] xmls = Directory.GetFiles(dir, "*.xml"); // Windows 大小写不敏感，覆盖 .XML/.xml
            string pick = null;
            foreach (string x in xmls)
                if (string.Equals(Path.GetFileNameWithoutExtension(x), name, StringComparison.OrdinalIgnoreCase)) { pick = x; break; }
            if (pick == null && xmls.Length == 1) pick = xmls[0];
            if (pick == null)
            {
                lblStatus.Text = (xmls.Length == 0 ? "族目录内无 XML" : "族目录内多个 XML 且无同名项，请手动打开") + "（第三态）：" + dir;
                Diagnostics.Log("cache 定位 | XML 未定 " + xmls.Length + " 个 | " + dir);
                return;
            }
            tbXml.Text = pick;
            InferSiblings();
            LoadXml();
            tc.SelectedIndex = 0;
            Diagnostics.Log("cache 定位 | 行 " + (idx + 1) + " " + name + " → " + pick);
        }

        // ===== .cache 编辑：新增 / 删除条目 + 从表格回写 + 保格式保存（2026-11-21） =====
        void AddCacheRow()
        {
            if (cacheDoc == null || dtCache.Columns.Count == 0)
            { lblStatus.Text = "请先加载 .cache 索引。"; return; }
            dgvCache.EndEdit();
            object[] cells = new object[dtCache.Columns.Count];
            for (int c = 0; c < cells.Length; c++) cells[c] = "";
            dtCache.Rows.Add(cells);
            int idx = dtCache.Rows.Count - 1;
            if (idx >= 0 && idx < dgvCache.Rows.Count) dgvCache.CurrentCell = dgvCache.Rows[idx].Cells[0];
            lblStatus.Text = "已新增空条目（共 " + dtCache.Rows.Count + " 条）；填好后点「保存 .cache」写回。GUID 请手工填写，工具不臆造。";
            Diagnostics.Log("cache 新增条目 | 行 " + (idx + 1));
        }

        void DelCacheRow()
        {
            if (cacheDoc == null || dgvCache.CurrentCell == null || dtCache.Rows.Count == 0)
            { lblStatus.Text = "请先加载 .cache 并点选一行。"; return; }
            // CellSelect 模式下 SelectedRows 恒为 0，删行取 CurrentCell.RowIndex（坑点 #13）
            int idx = dgvCache.CurrentCell.RowIndex;
            if (idx < 0 || idx >= dtCache.Rows.Count) return;
            string name = dtCache.Columns.Contains("name") ? Convert.ToString(dtCache.Rows[idx]["name"]) : "";
            dtCache.Rows.RemoveAt(idx);
            lblStatus.Text = "已删除条目：" + name + "（剩 " + dtCache.Rows.Count + " 条；点「保存 .cache」写回）。";
            Diagnostics.Log("cache 删除条目 | 行 " + (idx + 1) + " | " + name);
        }

        // 从 dtCache 回写到 cacheDoc.Entries：
        // 现有行按原属性顺序更新值（未编辑的属性保持原样，空值列不硬塞进原本没有该属性的条目）；
        // 新增行只写入有值的列。列序 = cacheDoc.KeysUnion，故属性顺序稳定。
        void SyncCacheFromUi()
        {
            if (cacheDoc == null) return;
            System.Collections.Generic.List<CacheCore.Entry> orig = cacheDoc.Entries;
            System.Collections.Generic.List<CacheCore.Entry> next = new System.Collections.Generic.List<CacheCore.Entry>();
            for (int i = 0; i < dtCache.Rows.Count; i++)
            {
                DataRow r = dtCache.Rows[i];
                CacheCore.Entry e = new CacheCore.Entry();
                if (i < orig.Count)
                {
                    foreach (Attr a in orig[i].Attrs)
                    {
                        string val = dtCache.Columns.Contains(a.Key)
                            ? (r[a.Key] == null ? "" : Convert.ToString(r[a.Key])) : a.Value;
                        e.Attrs.Add(new Attr(a.Key, val));
                    }
                    // 用户在新列（原本该条目没有的属性）填了值 → 追加到末尾
                    foreach (DataColumn col in dtCache.Columns)
                    {
                        if (XmlCore.Get(e.Attrs, col.ColumnName) != null) continue;
                        string val = r[col] == null ? "" : Convert.ToString(r[col]);
                        if (val.Length > 0) e.Attrs.Add(new Attr(col.ColumnName, val));
                    }
                }
                else
                {
                    foreach (DataColumn col in dtCache.Columns)
                    {
                        string val = r[col] == null ? "" : Convert.ToString(r[col]);
                        if (val.Length > 0) e.Attrs.Add(new Attr(col.ColumnName, val));
                    }
                }
                next.Add(e);
            }
            cacheDoc.Entries = next;
        }

        void SaveCache()
        {
            if (cacheDoc == null || string.IsNullOrEmpty(cachePath))
            { lblStatus.Text = "请先加载 .cache 索引。"; return; }
            dgvCache.EndEdit();
            try
            {
                SyncCacheFromUi();
                string backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_backup_原始数据");
                CacheCore.Save(cachePath, cacheDoc, backupDir);
                lblStatus.Text = "已保存 .cache：" + cachePath + "（" + cacheDoc.Entries.Count + " 条；备份：" + backupDir + "）";
                Diagnostics.Log("已保存 .cache（时间戳备份 + 保格式）：" + cachePath + " / " + cacheDoc.Entries.Count + " 条");
            }
            catch (Exception ex) { lblStatus.Text = ".cache 保存失败：" + ex.Message; Diagnostics.Exception("保存 .cache 失败", ex); }
        }

        // 固定列宽（用户要求）：列宽不随视口变化，列总宽超过视口即出水平滚动条；
        // 行高固定、行数多即出垂直滚动条。列在绑定后才生成，故挂 DataBindingComplete。
        void ApplyWidthsToOne(DataGridView g)
        {
            if (g == null || g.Columns.Count == 0) return;
            if (g == dgvRow) SetColumnsWidth(g, 100, 120);        // 规格表 21+ 列：每列 100px，首列（规格名）120
            else if (g == dgvParam) SetColumnsWidth(g, 150, 150); // 参数表：属性名/值都长
            else if (g == dgvCur) SetColumnsWidth(g, 150, 320);   // 族信息表：两列 属性/值
            else if (g == dgvCache)                               // 目录索引：name/display_name/GUID 都是长字段
            {
                foreach (DataGridViewColumn c in g.Columns)
                    c.Width = (c.Name == "GUID") ? 250 :
                        ((c.Name == "name" || c.Name == "display_name") ? 240 : 100);
            }
        }

        void SetColumnsWidth(DataGridView g, int width, int firstWidth)
        {
            for (int i = 0; i < g.Columns.Count; i++)
                g.Columns[i].Width = (i == 0) ? firstWidth : width;
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
            paramKeyOrder = new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
            foreach (System.Collections.Generic.List<Attr> pa in doc.Params)
            {
                System.Collections.Generic.List<string> order = new System.Collections.Generic.List<string>();
                object[] cells = new object[dtParam.Columns.Count];
                for (int c = 0; c < dtParam.Columns.Count; c++)
                {
                    string k = dtParam.Columns[c].ColumnName;
                    string v = XmlCore.Get(pa, k);
                    cells[c] = v ?? "";
                    if (v != null && !order.Contains(k)) order.Add(k);
                }
                foreach (Attr a in pa) if (!order.Contains(a.Key)) order.Add(a.Key);
                paramKeyOrder.Add(order);
                dtParam.Rows.Add(cells);
            }
        }

        void FillRow()
        {
            dtRow.Rows.Clear(); dtRow.Columns.Clear();
            foreach (string k in doc.RowKeys) dtRow.Columns.Add(k);
            foreach (System.Collections.Generic.List<Attr> row in doc.Rows)
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
            // CellSelect 模式下 SelectedRows 恒为 0，改用当前单元格所在行
            if (dgvRow.CurrentCell == null || dtRow.Rows.Count == 0) { lblStatus.Text = "请先点选要删除的行。"; return; }
            int idx = dgvRow.CurrentCell.RowIndex;
            if (idx < 0 || idx >= dtRow.Rows.Count) return;
            dtRow.Rows.RemoveAt(idx);
            lblStatus.Text = "已删除第 " + (idx + 1) + " 行（保存后写入 XML）。";
        }

        // ===== 电子表格：列增删（返填 doc.RowKeys，保存时逐行补/删属性） =====
        void AddColumn()
        {
            if (doc == null) { lblStatus.Text = "请先打开 XML。"; return; }
            string name = InputBox("新增列名", "输入新列名（XML 属性名，如 b2 / 新规格）：", "新列" + (dtRow.Columns.Count + 1));
            if (string.IsNullOrEmpty(name)) return;
            AddColumnCore(name.Trim());
        }

        // 新增列核心（无对话框，供界面与自检共用）：
        // 规格表加列 + 参数表同步追加同名参数行，默认值复制「SC图厚」行（缺行按回退链取模板）
        internal void AddColumnCore(string name)
        {
            if (doc == null) { lblStatus.Text = "请先打开 XML。"; return; }
            if (string.IsNullOrEmpty(name)) return;
            if (dtRow.Columns.Contains(name)) { lblStatus.Text = "列名已存在：" + name; return; }
            dtRow.Columns.Add(name);
            // 已有行补空值；同步到 doc.RowKeys 以便 SyncFromUi 逐行返填
            foreach (DataRow r in dtRow.Rows) r[name] = "";
            if (!doc.RowKeys.Contains(name)) doc.RowKeys.Add(name);

            // ===== 参数表联动：追加同名参数行 =====
            string tplSource;
            int ti = FindTemplateParamRow(out tplSource);
            object[] pc;
            System.Collections.Generic.List<string> order;
            if (ti >= 0)
            {
                pc = new object[dtParam.Columns.Count];
                for (int c = 0; c < dtParam.Columns.Count; c++)
                    pc[c] = Convert.ToString(dtParam.Rows[ti][c]) ?? "";
                order = new System.Collections.Generic.List<string>(paramKeyOrder[ti]);
            }
            else
            {
                // 无任何 SC 模板行：内置 SC 自定义属性行默认值（与样本 SC 行同形）
                string[][] def = new string[][] {
                    new string[] { "Dimension", "" }, new string[] { "Custom_property", name },
                    new string[] { "Display_name", name }, new string[] { "hasBeenAdded", "TRUE" },
                    new string[] { "Grid_item_type", "Dim_String_Edit" }, new string[] { "Grid_item_state", "DIM_CTRL_HIDE" },
                    new string[] { "unit_type", "None" }, new string[] { "AddtoDisplayName", "FALSE" },
                    new string[] { "HasDelimiters", "TRUE" }, new string[] { "Delimiters", "" } };
                order = new System.Collections.Generic.List<string>();
                foreach (string[] kv in def)
                {
                    if (!dtParam.Columns.Contains(kv[0])) dtParam.Columns.Add(kv[0]);
                    order.Add(kv[0]);
                }
                pc = new object[dtParam.Columns.Count]; // 列可能刚补建，数组按最终列数重建
                for (int c = 0; c < dtParam.Columns.Count; c++) pc[c] = "";
                foreach (string[] kv in def) pc[dtParam.Columns.IndexOf(kv[0])] = kv[1];
            }
            // 名称字段随新列名：模板是自定义属性行改 Custom_property，尺寸行改 Dimension；Display_name 一律随名
            if (dtParam.Columns.Contains("Custom_property") &&
                (ti < 0 || Convert.ToString(dtParam.Rows[ti]["Custom_property"]) != ""))
            {
                pc[dtParam.Columns.IndexOf("Custom_property")] = name;
                if (!order.Contains("Custom_property")) order.Add("Custom_property");
            }
            else if (dtParam.Columns.Contains("Dimension"))
            {
                pc[dtParam.Columns.IndexOf("Dimension")] = name;
                if (!order.Contains("Dimension")) order.Add("Dimension");
            }
            if (dtParam.Columns.Contains("Display_name"))
            {
                pc[dtParam.Columns.IndexOf("Display_name")] = name;
                if (!order.Contains("Display_name")) order.Add("Display_name");
            }
            dtParam.Rows.Add(pc);
            paramKeyOrder.Add(order);
            Diagnostics.Log("新增列 " + name + "：参数表同步追加参数行，模板=" + tplSource);
            lblStatus.Text = "已新增列：" + name + "（参数表已同步追加同名行，模板：" + tplSource + "；保存后写入 XML）。";
        }

        // 模板行回退链：SC图厚 → SC规格 → 首个 SC* 自定义属性行 → 无（-1，用内置默认）
        int FindTemplateParamRow(out string source)
        {
            source = "内置SC默认值";
            if (dtParam.Columns.Count == 0 || dtParam.Rows.Count == 0) return -1;
            string[] names = new string[] { "SC图厚", "SC规格" };
            foreach (string want in names)
            {
                for (int i = 0; i < dtParam.Rows.Count; i++)
                {
                    if (ParamCellIs(i, "Custom_property", want) || ParamCellIs(i, "Dimension", want) || ParamCellIs(i, "Display_name", want))
                    { source = want + " 行"; return i; }
                }
            }
            for (int i = 0; i < dtParam.Rows.Count; i++)
            {
                string cp = dtParam.Columns.Contains("Custom_property") ? Convert.ToString(dtParam.Rows[i]["Custom_property"]) : "";
                if (cp != null && cp.StartsWith("SC", StringComparison.Ordinal))
                { source = cp + " 行"; return i; }
            }
            return -1;
        }

        bool ParamCellIs(int row, string col, string value)
        {
            if (!dtParam.Columns.Contains(col)) return false;
            return string.Equals(Convert.ToString(dtParam.Rows[row][col]), value, StringComparison.Ordinal);
        }

        // ===== 参数表：增删行（paramKeyOrder 与行序同步，保存时返填 XML） =====
        void AddParamRow()
        {
            if (doc == null) { lblStatus.Text = "请先打开 XML。"; return; }
            object[] cells = new object[dtParam.Columns.Count];
            for (int i = 0; i < cells.Length; i++) cells[i] = "";
            dtParam.Rows.Add(cells);
            // 新行键序为空：保存时只写用户填了值的列（非空才落属性）
            paramKeyOrder.Add(new System.Collections.Generic.List<string>());
            lblStatus.Text = "已追加空参数行（填写后保存写入 XML；新行只写非空字段）。";
        }

        void DelParamRow()
        {
            // CellSelect 模式下 SelectedRows 恒为 0，改用当前单元格所在行（坑 13）
            if (dgvParam.CurrentCell == null || dtParam.Rows.Count == 0) { lblStatus.Text = "请先点选要删除的参数行。"; return; }
            int idx = dgvParam.CurrentCell.RowIndex;
            if (idx < 0 || idx >= dtParam.Rows.Count) return;
            dtParam.Rows.RemoveAt(idx);
            if (paramKeyOrder != null && idx < paramKeyOrder.Count) paramKeyOrder.RemoveAt(idx);
            lblStatus.Text = "已删除第 " + (idx + 1) + " 参数行（保存后写入 XML）。";
        }

        void DelColumn()
        {
            if (doc == null || dgvRow.CurrentCell == null) { lblStatus.Text = "请先点选要删除的列。"; return; }
            int ci = dgvRow.CurrentCell.ColumnIndex;
            if (ci < 0 || ci >= dtRow.Columns.Count) return;
            DelColumnCore(dtRow.Columns[ci].ColumnName);
        }

        // 删除列核心（无对话框，供界面与导入共用）：
        // 规格表删列 + doc.RowKeys 移除 + 参数表同步删除同名参数行（按 Dimension/Custom_property/Display_name 匹配）
        internal void DelColumnCore(string name)
        {
            if (doc == null || string.IsNullOrEmpty(name)) return;
            int ci = dtRow.Columns.IndexOf(name);
            if (ci < 0) return;
            if (dtRow.Columns.Count <= 1) { lblStatus.Text = "至少保留一列。"; return; }
            dtRow.Columns.RemoveAt(ci);
            doc.RowKeys.Remove(name);

            // 参数表联动：删除同名参数行（与 AddColumnCore 加行对称）
            int pi = FindParamRowByName(name);
            string paramInfo;
            if (pi >= 0)
            {
                dtParam.Rows.RemoveAt(pi);
                if (paramKeyOrder != null && pi < paramKeyOrder.Count) paramKeyOrder.RemoveAt(pi);
                paramInfo = "参数表已同步删除第 " + (pi + 1) + " 行";
            }
            else paramInfo = "参数表无同名行（跳过）";
            Diagnostics.Log("删除列 " + name + "：" + paramInfo);
            lblStatus.Text = "已删除列：" + name + "（" + paramInfo + "；保存后写入 XML）。";
        }

        // 参数表按列名找行：Dimension / Custom_property / Display_name 任一等于 name 即命中
        // （AddColumnCore 加行时名称字段随新列名；原始 XML 的尺寸行 Dimension=列名、自定义行 Custom_property=列名）
        int FindParamRowByName(string name)
        {
            if (string.IsNullOrEmpty(name) || dtParam.Columns.Count == 0) return -1;
            string[] cols = new string[] { "Dimension", "Custom_property", "Display_name" };
            for (int i = 0; i < dtParam.Rows.Count; i++)
                for (int k = 0; k < cols.Length; k++)
                    if (ParamCellIs(i, cols[k], name)) return i;
            return -1;
        }

        static string InputBox(string title, string prompt, string defaultValue)
        {
            using (Form box = new Form())
            {
                box.Text = title;
                box.FormBorderStyle = FormBorderStyle.FixedDialog;
                box.StartPosition = FormStartPosition.CenterParent;
                box.ClientSize = new Size(380, 110);
                box.MinimizeBox = false; box.MaximizeBox = false;
                Label lb = new Label(); lb.Text = prompt; lb.Location = new Point(12, 14); lb.Size = new Size(356, 22); box.Controls.Add(lb);
                TextBox tb = new TextBox(); tb.Text = defaultValue; tb.Location = new Point(12, 40); tb.Size = new Size(356, 24); box.Controls.Add(tb);
                Button ok = new Button(); ok.Text = "确定"; ok.DialogResult = DialogResult.OK; ok.Location = new Point(212, 76); ok.Size = new Size(74, 26); box.Controls.Add(ok);
                Button cancel = new Button(); cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(294, 76); cancel.Size = new Size(74, 26); box.Controls.Add(cancel);
                box.AcceptButton = ok; box.CancelButton = cancel;
                tb.SelectAll();
                return box.ShowDialog() == DialogResult.OK ? tb.Text : null;
            }
        }

        // 把界面 DataTable 写回内存模型（保存/比对前同步）
        void SyncFromUi()
        {
            if (doc == null) return;
            // Cur
            System.Collections.Generic.List<Attr> newCur = new System.Collections.Generic.List<Attr>();
            foreach (DataRow r in dtCur.Rows)
                newCur.Add(new Attr(Convert.ToString(r[0]), Convert.ToString(r[1])));
            doc.Cur = newCur;
            // Param：按每个参数原始键顺序，值取界面列；行数以界面为准（支持增删行）
            System.Collections.Generic.List<System.Collections.Generic.List<Attr>> newParams = new System.Collections.Generic.List<System.Collections.Generic.List<Attr>>();
            for (int i = 0; i < dtParam.Rows.Count; i++)
            {
                System.Collections.Generic.List<string> order =
                    (paramKeyOrder != null && i < paramKeyOrder.Count) ? paramKeyOrder[i] : null;
                System.Collections.Generic.List<Attr> pa = new System.Collections.Generic.List<Attr>();
                if (order != null)
                    foreach (string k in order)
                        if (dtParam.Columns.Contains(k))
                            pa.Add(new Attr(k, Convert.ToString(dtParam.Rows[i][k])));
                // 键序之外的列：非空才补写（新填字段可落盘；模板未含的空字段不污染原格式）
                foreach (DataColumn col in dtParam.Columns)
                {
                    if (order != null && order.Contains(col.ColumnName)) continue;
                    string v = Convert.ToString(dtParam.Rows[i][col]);
                    if (!string.IsNullOrEmpty(v)) pa.Add(new Attr(col.ColumnName, v));
                }
                newParams.Add(pa);
            }
            doc.Params = newParams;
            // Row
            System.Collections.Generic.List<System.Collections.Generic.List<Attr>> newRows = new System.Collections.Generic.List<System.Collections.Generic.List<Attr>>();
            foreach (DataRow r in dtRow.Rows)
            {
                System.Collections.Generic.List<Attr> ra = new System.Collections.Generic.List<Attr>();
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
                Diagnostics.Log("已保存 XML（含时间戳备份）：" + xmlPath);
            }
            catch (Exception ex) { lblStatus.Text = "保存失败：" + ex.Message; Diagnostics.Exception("保存 XML 失败", ex); }
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

        // ===== 规格表 Excel 导出/导入（零依赖 OOXML，格式参考 CAXA 标准件库导入模板 123.xlsx） =====

        // —— Excel 元数据（B1 零件名 / B2 GUID）与 .cache 对齐（11-21 用户要求） ——
        // 取证（项目总结第 3 节）：.cache 的 Folder 行以 name(=族名=族 XML 所在文件夹名) 与
        // GUID(=族 XML CurNodeInfo@GUID，逐字一致) 双处登记族身份 ⇒ 二者均可作定位锚点。
        class CacheIdentity
        {
            public string Name = "";
            public string Guid = "";
        }

        static string NormGuid(string s)
        {
            if (s == null) return "";
            return s.Trim().Trim('{', '}').Trim().ToUpperInvariant();
        }
        static bool SameGuid(string a, string b)
        {
            string x = NormGuid(a), y = NormGuid(b);
            return x.Length > 0 && x == y;
        }
        static bool SameName(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // 取可用 .cache：优先内存 cacheDoc（含未保存的表格编辑），否则按 CACHE 地址栏读盘
        bool TryGetCacheDoc(out CacheCore.CacheDoc cd, out string msg)
        {
            cd = null; msg = "";
            if (cacheDoc != null) { cd = cacheDoc; return true; }
            string p = tbCache.Text;
            if (string.IsNullOrEmpty(p) || !File.Exists(p))
            { msg = "CACHE 地址栏未指向有效 .cache 文件"; return false; }
            try { cd = CacheCore.Load(p); return true; }
            catch (Exception ex) { msg = "读取 .cache 失败：" + ex.Message; return false; }
        }

        // 在 .cache 中按 name / GUID 定位唯一行：两类命中须指同一行，任一命中重复即歧义（第三态不猜）
        CacheIdentity FindCacheRow(CacheCore.CacheDoc cd, string nameHint, string guidHint)
        {
            if (cd == null) return null;
            string ng = NormGuid(guidHint), nn = (nameHint ?? "").Trim();
            CacheIdentity byGuid = null, byName = null;
            int guidHits = 0, nameHits = 0;
            foreach (CacheCore.Entry e in cd.Entries)
            {
                string name = XmlCore.Get(e.Attrs, "name") ?? "";
                string guid = XmlCore.Get(e.Attrs, "GUID") ?? "";
                if (ng.Length > 0 && NormGuid(guid) == ng)
                { byGuid = new CacheIdentity { Name = name, Guid = guid }; guidHits++; }
                if (nn.Length > 0 && string.Equals((name ?? "").Trim(), nn, StringComparison.OrdinalIgnoreCase))
                { byName = new CacheIdentity { Name = name, Guid = guid }; nameHits++; }
            }
            if (guidHits > 1 || nameHits > 1) return null;
            if (byGuid != null && byName != null)
                return SameGuid(byGuid.Guid, byName.Guid) ? byGuid : null;
            return byGuid ?? byName;
        }

        // 当前 XML 对应的 .cache 行：族名(所在文件夹名) + CurNodeInfo@GUID 联合定位；
        // 目录名与族名不一致时（改过名，ics 取证坐实）以 GUID 为权威回退，再用 CurNodeInfo@name。
        CacheIdentity FindCacheRowForCurrentXml(CacheCore.CacheDoc cd)
        {
            string folder = "";
            if (!string.IsNullOrEmpty(xmlPath)) folder = Path.GetFileName(Path.GetDirectoryName(xmlPath)) ?? "";
            string xname = doc != null ? (XmlCore.Get(doc.Cur, "name") ?? "") : "";
            string xguid = doc != null ? (XmlCore.Get(doc.Cur, "GUID") ?? "") : "";
            CacheIdentity hit = FindCacheRow(cd, folder, xguid);
            if (hit != null) return hit;
            hit = FindCacheRow(cd, "", xguid);
            if (hit != null) return hit;
            return FindCacheRow(cd, xname, "");
        }

        // 规格表列名（XML 属性名/Dimension）→ Excel 中文表头（Display_name）
        // l→公称长度、d→公称直径 ...；螺纹规格/SC图号等本身即中文，原样返回
        string RowKeyToHeader(string rowKey)
        {
            if (doc == null || doc.Params == null) return rowKey;
            foreach (System.Collections.Generic.List<Attr> p in doc.Params)
            {
                string dim = XmlCore.Get(p, "Dimension");
                string disp = XmlCore.Get(p, "Display_name");
                if (!string.IsNullOrEmpty(dim) && dim == rowKey && !string.IsNullOrEmpty(disp)) return disp;
            }
            return rowKey; // 无 Dimension 映射（中文列名）原样
        }

        // Excel 中文表头 → 规格表列名（导入时用；优先 Dimension 反查，其次同名）
        string HeaderToRowKey(string header)
        {
            if (doc == null || doc.Params == null) return header;
            // 先看是否有 Parameter 的 Display_name 等于 header，取其 Dimension
            foreach (System.Collections.Generic.List<Attr> p in doc.Params)
            {
                string dim = XmlCore.Get(p, "Dimension");
                string disp = XmlCore.Get(p, "Display_name");
                if (!string.IsNullOrEmpty(disp) && disp == header && !string.IsNullOrEmpty(dim)) return dim;
            }
            return header; // 同名（螺纹规格/SC图号等）
        }

        void ExportTableExcel()
        {
            if (doc == null || dtRow.Columns.Count == 0) { lblStatus.Text = "请先打开 XML 再导出。"; return; }
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.Filter = "Excel 文件|*.xlsx";
            sfd.FileName = (XmlCore.Get(doc.Cur, "name") ?? "规格表") + ".xlsx";
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            ExportTableExcelCore(sfd.FileName);
        }

        // 导出核心（无对话框，供界面与自检共用）：B1/B2 零件名与 GUID 一律取 .cache 对应行
        internal void ExportTableExcelCore(string path)
        {
            try
            {
                CacheCore.CacheDoc cd; string msg;
                if (!TryGetCacheDoc(out cd, out msg))
                { lblStatus.Text = "导出失败：" + msg + "（无法保证零件名/GUID 与 .cache 一致）。"; return; }
                CacheIdentity id = FindCacheRowForCurrentXml(cd);
                if (id == null)
                { lblStatus.Text = "导出失败：.cache 中未唯一找到当前族对应行（第三态，不臆造零件名/GUID）。"; return; }

                string partName = id.Name;
                string guid = id.Guid;
                System.Collections.Generic.List<string> headers = new System.Collections.Generic.List<string>();
                for (int c = 0; c < dtRow.Columns.Count; c++)
                    headers.Add(RowKeyToHeader(dtRow.Columns[c].ColumnName));
                System.Collections.Generic.List<System.Collections.Generic.List<string>> data =
                    new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
                foreach (DataRow r in dtRow.Rows)
                {
                    System.Collections.Generic.List<string> row = new System.Collections.Generic.List<string>();
                    for (int c = 0; c < dtRow.Columns.Count; c++) row.Add(Convert.ToString(r[c]) ?? "");
                    data.Add(row);
                }
                XlsxIO.WriteTemplate(path, partName, guid, headers, data);
                lblStatus.Text = "已导出表格：" + path + "（B1/B2 取 .cache 行：" + partName + " / " + guid +
                    "；" + data.Count + " 行 / " + headers.Count + " 列）";
                Diagnostics.Log("导出表格 Excel：" + path + " B1/B2=.cache[" + partName + "/" + guid + "] " +
                    data.Count + "行" + headers.Count + "列");
            }
            catch (Exception ex) { lblStatus.Text = "导出表格失败：" + ex.Message; Diagnostics.Exception("导出表格失败", ex); }
        }

        void ImportTableExcel()
        {
            if (doc == null || dtRow.Columns.Count == 0) { lblStatus.Text = "请先打开 XML 再导入。"; return; }
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Filter = "Excel 文件|*.xlsx|所有文件|*.*";
            ofd.InitialDirectory = DefaultBrowseDirectory(); ofd.RestoreDirectory = true;
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            ImportTableExcelCore(ofd.FileName);
        }

        // 导入前校验：Excel B1/B2（零件名/GUID）必须与 .cache 对应行一致，否则拒绝导入（第三态，不假成功）
        // 返回 true=通过；false=已在状态栏/日志点名差异，调用方应中止。同时输出定位到的 .cache 身份供日志。
        bool ValidateExcelAgainstCache(System.Collections.Generic.List<System.Collections.Generic.List<string>> sheet, string path, out CacheIdentity id)
        {
            id = null;
            string xName = sheet.Count > 0 && sheet[0].Count > 1 ? sheet[0][1] : "";
            string xGuid = sheet.Count > 1 && sheet[1].Count > 1 ? sheet[1][1] : "";
            CacheCore.CacheDoc cd; string msg;
            if (!TryGetCacheDoc(out cd, out msg))
            {
                lblStatus.Text = "导入拒绝：" + msg + "（无法校验 Excel 零件名/GUID 与 .cache 一致）。";
                Diagnostics.Log("导入校验失败 | " + msg + " | " + path);
                return false;
            }
            id = FindCacheRow(cd, xName, xGuid);
            if (id == null)
            {
                lblStatus.Text = "导入拒绝：.cache 中未唯一找到 Excel 的零件名/GUID 对应行（第三态，不猜）。Excel B1=" +
                    xName + " B2=" + xGuid;
                Diagnostics.Log("导入校验失败 | .cache 无唯一对应行 | B1=" + xName + " B2=" + xGuid);
                return false;
            }
            bool nameOk = SameName(xName, id.Name);
            bool guidOk = SameGuid(xGuid, id.Guid);
            if (!nameOk || !guidOk)
            {
                lblStatus.Text = "导入拒绝：Excel 与 .cache 不一致 —— 零件名[" + xName + " vs " + id.Name +
                    "] GUID[" + xGuid + " vs " + id.Guid + "]。";
                Diagnostics.Log("导入校验失败 | 名称或GUID不一致 | Excel=" + xName + "/" + xGuid +
                    " .cache=" + id.Name + "/" + id.Guid);
                return false;
            }
            Diagnostics.Log("导入校验通过 | B1/B2 与 .cache 一致 | " + id.Name + " / " + id.Guid);
            return true;
        }

        // 导入核心（无对话框，供界面与自检共用）：
        // ① 按 Excel 表头同步规格表"列结构"——Excel 多出的列 AddColumnCore（参数表加行）、缺少的列 DelColumnCore（参数表删行）
        // ② 按 Excel 行数据清空重建 dtRow——行增减天然支持（保存时 SyncFromUi 以 dtRow 为准写回 doc.Rows）
        internal void ImportTableExcelCore(string path)
        {
            try
            {
                System.Collections.Generic.List<System.Collections.Generic.List<string>> sheet = XlsxIO.ReadSheet(path);
                // ★ 导入前比对：Excel B1 零件名 / B2 GUID 必须与 .cache 所在行一致，否则拒绝
                CacheIdentity cacheId;
                if (!ValidateExcelAgainstCache(sheet, path, out cacheId)) return;
                // 定位表头行：找到第一个含"螺纹规格"的行（模板第4行）；数据从其后第2行开始（跳过说明行）
                int headerRow = -1;
                for (int r = 0; r < sheet.Count && r < 10; r++)
                {
                    System.Collections.Generic.List<string> row = sheet[r];
                    for (int c = 0; c < row.Count; c++)
                        if (row[c] == "螺纹规格") { headerRow = r; break; }
                    if (headerRow >= 0) break;
                }
                if (headerRow < 0) { lblStatus.Text = "导入失败：未找到表头行（需含“螺纹规格”列）。"; return; }
                System.Collections.Generic.List<string> header = sheet[headerRow];

                // Excel 表头中文名 → Excel 列序号
                System.Collections.Generic.Dictionary<string, int> hmap =
                    new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
                for (int c = 0; c < header.Count; c++)
                    if (!string.IsNullOrEmpty(header[c]) && !hmap.ContainsKey(header[c])) hmap[header[c]] = c;

                // ===== ① 列结构同步：Excel 表头反查 RowKey，与当前规格表列比对 =====
                // Excel 列对应的规格表列名集合（保序、去重）
                System.Collections.Generic.List<string> targetKeys = new System.Collections.Generic.List<string>();
                for (int c = 0; c < header.Count; c++)
                {
                    string h = header[c];
                    if (string.IsNullOrEmpty(h)) continue;
                    string rk = HeaderToRowKey(h);
                    if (!string.IsNullOrEmpty(rk) && !targetKeys.Contains(rk)) targetKeys.Add(rk);
                }
                int added = 0, removed = 0;
                // 先删：当前规格表中 Excel 没有的列（倒序，避免索引错位）；参数表同步删行
                for (int c = dtRow.Columns.Count - 1; c >= 0; c--)
                {
                    string rk = dtRow.Columns[c].ColumnName;
                    if (!targetKeys.Contains(rk)) { DelColumnCore(rk); removed++; }
                }
                // 后加：Excel 有而规格表没有的列（按 Excel 表头顺序追加）；参数表同步加行
                foreach (string rk in targetKeys)
                {
                    if (!dtRow.Columns.Contains(rk)) { AddColumnCore(rk); added++; }
                }

                // ===== ② 行数据同步：每列按表头名找 Excel 列序号，清空重建 =====
                int[] srcCol = new int[dtRow.Columns.Count];
                for (int c = 0; c < dtRow.Columns.Count; c++)
                {
                    string h = RowKeyToHeader(dtRow.Columns[c].ColumnName);
                    int ix; srcCol[c] = hmap.TryGetValue(h, out ix) ? ix : -1;
                }
                int dataStart = headerRow + 2;
                dtRow.Rows.Clear();
                int imported = 0;
                for (int r = dataStart; r < sheet.Count; r++)
                {
                    System.Collections.Generic.List<string> src = sheet[r];
                    object[] cells = new object[dtRow.Columns.Count];
                    bool any = false;
                    for (int c = 0; c < dtRow.Columns.Count; c++)
                    {
                        string v = "";
                        if (srcCol[c] >= 0 && srcCol[c] < src.Count) v = src[srcCol[c]] ?? "";
                        cells[c] = v;
                        if (!string.IsNullOrEmpty(v)) any = true;
                    }
                    if (!any) continue; // 跳过全空行
                    dtRow.Rows.Add(cells); imported++;
                }
                lblStatus.Text = "已导入表格：" + path + "（" + imported + " 行 / " + dtRow.Columns.Count +
                    " 列；列 +" + added + " -" + removed + "，参数表已同步；保存 XML 后写入）";
                Diagnostics.Log("导入表格 Excel：" + path + " " + imported + "行" + dtRow.Columns.Count +
                    "列（列 +" + added + " -" + removed + "，参数表同步）");
            }
            catch (Exception ex) { lblStatus.Text = "导入表格失败：" + ex.Message; Diagnostics.Exception("导入表格失败", ex); }
        }

    }
}
