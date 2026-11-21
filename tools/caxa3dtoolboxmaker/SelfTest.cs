using System;
using System.Data;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Caxa3dToolboxMaker
{
    // ---------- 自检入口（控制台版 --selftest） ----------
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
                int k = 0; int mn = Math.Min(d.Raw.Length, rendered.Length);
                while (k < mn && d.Raw[k] == rendered[k]) k++;
                Console.WriteLine("[DUMP] 首个差异位置=" + k + " raw.len=" + d.Raw.Length + " render.len=" + rendered.Length);
                Console.WriteLine("[DUMP] raw:  " + Esc(d.Raw, k));
                Console.WriteLine("[DUMP] rend: " + Esc(rendered, k));
            }
            chk("保格式round-trip字节一致", rendered == d.Raw);
            string tmp = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_selftest.xml");
            File.WriteAllText(tmp, rendered, new UTF8Encoding(false));
            XmlCore.Doc reload = XmlCore.Load(tmp);
            chk("重载行数一致", reload.Rows.Count == d.Rows.Count);
            chk("重载参数数一致", reload.Params.Count == d.Params.Count);
            chk("重载GUID一致", MetaProbe.NormGuid(XmlCore.Get(reload.Cur, "GUID")) == guid);

            // ===== 电子表格核心：新增列/行并返填 XML =====
            System.Collections.Generic.List<string> keysPlus = new System.Collections.Generic.List<string>(reload.RowKeys);
            keysPlus.Add("新增属性X");
            reload.RowKeys = keysPlus;
            foreach (System.Collections.Generic.List<Attr> row in reload.Rows)
                if (XmlCore.Get(row, "新增属性X") == null) row.Add(new Attr("新增属性X", ""));
            reload.Rows.Add(new System.Collections.Generic.List<Attr>()); // 追加一行
            foreach (string k in reload.RowKeys) reload.Rows[reload.Rows.Count - 1].Add(new Attr(k, "返填测试"));
            string rendered2 = XmlCore.Render(reload);
            string tmp2 = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_coltest.xml");
            File.WriteAllText(tmp2, rendered2, new UTF8Encoding(false));
            XmlCore.Doc re2 = XmlCore.Load(tmp2);
            chk("新增列返填XML(列数+1)", re2.RowKeys.Count == d.RowKeys.Count + 1);
            chk("新增列XML可重载", XmlCore.Get(re2.Rows[0], "新增属性X") != null);
            chk("新增行返填XML(行数+1)", re2.Rows.Count == d.Rows.Count + 1);
            chk("新增行内容可读", XmlCore.Get(re2.Rows[re2.Rows.Count - 1], "新增属性X") == "返填测试");
            // 删除列返填
            re2.RowKeys.Remove("新增属性X");
            foreach (System.Collections.Generic.List<Attr> row in re2.Rows)
            {
                Attr hit = null;
                foreach (Attr a in row) if (a.Key == "新增属性X") { hit = a; break; }
                if (hit != null) row.Remove(hit);
            }
            string tmp3 = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_delcol.xml");
            File.WriteAllText(tmp3, XmlCore.Render(re2), new UTF8Encoding(false));
            XmlCore.Doc re3 = XmlCore.Load(tmp3);
            chk("删除列返填XML(列数还原)", re3.RowKeys.Count == d.RowKeys.Count);
            chk("删除列后属性消失", XmlCore.Get(re3.Rows[0], "新增属性X") == null);

            // ===== 参数表增删行返填 XML（Render 整段重建） =====
            int pn0 = reload.Params.Count;
            System.Collections.Generic.List<Attr> np = new System.Collections.Generic.List<Attr>();
            np.Add(new Attr("Dimension", ""));
            np.Add(new Attr("Custom_property", "参数新增测试"));
            np.Add(new Attr("Display_name", "参数新增测试"));
            np.Add(new Attr("Grid_item_type", "Dim_String_Edit"));
            reload.Params.Add(np);
            string tmp4 = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_addparam.xml");
            File.WriteAllText(tmp4, XmlCore.Render(reload), new UTF8Encoding(false));
            XmlCore.Doc p2 = XmlCore.Load(tmp4);
            chk("参数新增行返填XML(参数数+1)", p2.Params.Count == pn0 + 1);
            chk("参数新增行属性可读", XmlCore.Get(p2.Params[pn0], "Custom_property") == "参数新增测试");
            chk("参数新增行保序(末行)", XmlCore.Get(p2.Params[p2.Params.Count - 1], "Display_name") == "参数新增测试");
            p2.Params.RemoveAt(0);
            string tmp5 = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_delparam.xml");
            File.WriteAllText(tmp5, XmlCore.Render(p2), new UTF8Encoding(false));
            XmlCore.Doc p3 = XmlCore.Load(tmp5);
            chk("参数删除行返填XML(参数数还原)", p3.Params.Count == pn0);
            chk("参数删除后首行是原第二行", XmlCore.Get(p3.Params[0], "Dimension") == "d");
            chk("参数增删后其余参数保序", XmlCore.Get(p3.Params[p3.Params.Count - 1], "Custom_property") == "参数新增测试");
            chk("参数增删不伤规格行", p3.Rows.Count == reload.Rows.Count); // reload 在前段列测试已追加 1 行，以其为基线

            // ===== .cache 目录索引解析（合成样本自包含 + 坏样本反证） =====
            string cacheTmp = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_selftest.cache");
            File.WriteAllText(cacheTmp,
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Root>\r\n" +
                "<Version version=\"1\">\r\n</Version><FolderList>\r\n" +
                "<Folder name=\"FamilyA\" display_name=\"六角螺栓 A\" type=\"TB_PART\" GUID=\"{B4AFBADB-DD59-4B48-893C-4A5109B37A2F}\" protected=\"TRUE\">\r\n" +
                "</Folder><Folder name=\"FamilyB\" display_name=\"六角螺栓 B\" type=\"TB_PART\" GUID=\"{75CDDC6E-57A5-4E9A-9590-50D713DDB453}\" protected=\"FALSE\">\r\n" +
                "</Folder></FolderList></Root>", new UTF8Encoding(false));
            CacheCore.CacheDoc cd = CacheCore.Load(cacheTmp);
            chk("cache条目数=2", cd.Entries.Count == 2);
            chk("cache版本=1", cd.Version == "1");
            chk("cache GUID可读", XmlCore.Get(cd.Entries[0].Attrs, "GUID") == "{B4AFBADB-DD59-4B48-893C-4A5109B37A2F}");
            chk("cache中文显示名不乱码", XmlCore.Get(cd.Entries[0].Attrs, "display_name") == "六角螺栓 A");
            chk("cache列序保序(5列name首GUID第4)", cd.KeysUnion.Count == 5 && cd.KeysUnion[0] == "name" && cd.KeysUnion[3] == "GUID");
            chk("cache protected可读", XmlCore.Get(cd.Entries[1].Attrs, "protected") == "FALSE");
            // —— 保格式写回内核验证（2026-11-21）：未编辑 round-trip 逐字节一致 + 改值/增删条目 ——
            string cacheRaw = File.ReadAllText(cacheTmp, Encoding.UTF8);
            chk("cache未编辑round-trip逐字节一致", CacheCore.Render(cd) == cacheRaw);
            XmlCore.Set(cd.Entries[0].Attrs, "display_name", "六角螺栓 A改");
            string cacheEdited = CacheCore.Render(cd);
            chk("cache改值后旧值消失", !cacheEdited.Contains("display_name=\"六角螺栓 A\""));
            chk("cache改值后新值在", cacheEdited.Contains("display_name=\"六角螺栓 A改\""));
            chk("cache改值不影响其他属性", cacheEdited.Contains("GUID=\"{B4AFBADB-DD59-4B48-893C-4A5109B37A2F}\""));
            Func<string, CacheCore.CacheDoc> reloadCache = delegate (string s)
            {
                string rp = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_cache_reload.cache");
                File.WriteAllText(rp, s, new UTF8Encoding(false));
                return CacheCore.Load(rp);
            };
            chk("cache改值后仍可重载", reloadCache(cacheEdited).Entries.Count == 2);
            chk("cache改值重载新值正确", XmlCore.Get(reloadCache(cacheEdited).Entries[0].Attrs, "display_name") == "六角螺栓 A改");
            CacheCore.CacheDoc cd2 = CacheCore.Load(cacheTmp);
            cd2.Entries.Add(new CacheCore.Entry());
            XmlCore.Set(cd2.Entries[2].Attrs, "name", "FamilyC");
            XmlCore.Set(cd2.Entries[2].Attrs, "display_name", "六角螺栓 C");
            string cacheAdded = CacheCore.Render(cd2);
            chk("cache新增条目写回", cacheAdded.Contains("name=\"FamilyC\"") && cacheAdded.Contains("display_name=\"六角螺栓 C\""));
            chk("cache新增条目后条目=3", reloadCache(cacheAdded).Entries.Count == 3);
            cd2.Entries.RemoveAt(1);
            string cacheDel = CacheCore.Render(cd2);
            chk("cache删除条目写回", !cacheDel.Contains("FamilyB"));
            chk("cache删除条目后条目=2", reloadCache(cacheDel).Entries.Count == 2);
            // 权威判据：Save 实际写盘后的字节头（EF BB BF = 带 BOM，此处必须不是）
            string cacheSaveTmp = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_cache_save.cache");
            CacheCore.Save(cacheSaveTmp, cd2, null);
            byte[] cacheBytes = File.ReadAllBytes(cacheSaveTmp);
            chk("cache保存文件UTF8无BOM", !(cacheBytes.Length >= 3 && cacheBytes[0] == 0xEF && cacheBytes[1] == 0xBB && cacheBytes[2] == 0xBF));
            chk("cache保存文件用CRLF", cacheDel.Contains("\r\n") && !cacheDel.Replace("\r\n", "").Contains("\n"));
            // 坏样本①：族 XML（根 ParentType）不是索引，必须拒绝
            bool threwRoot = false;
            try { CacheCore.Load(tmp); } catch { threwRoot = true; }
            chk("cache拒绝非Root文件(坏样本)", threwRoot);
            // 坏样本②：不存在的路径必须抛 FileNotFoundException
            bool threwMissing = false;
            try { CacheCore.Load(Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_不存在.cache")); }
            catch (FileNotFoundException) { threwMissing = true; }
            chk("cache不存在文件抛异常(坏样本)", threwMissing);
            // 官方真实 .cache（第三态：找不到只记 INFO，不判失败）
            string officialCache = @"C:\Program Files\CAXA\CAXA 3D\2027\AppData\zh-cn\ToolBox\GB\Fasteners\Bolts\Hexagon headed bolts\.cache";
            if (File.Exists(officialCache))
            {
                CacheCore.CacheDoc oc = CacheCore.Load(officialCache);
                Console.WriteLine("[INFO] 官方.cache 条目=" + oc.Entries.Count + " version=" + (oc.Version ?? "<none>"));
                chk("官方.cache解析条目>0", oc.Entries.Count > 0);
            }
            else Console.WriteLine("[INFO] 官方.cache 未找到（跳过）：" + officialCache);

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

    // ---------- 界面离屏验证：实例化 MainForm，反射读控件断言 + DrawToBitmap 截图 ----------
    public static class UiTest
    {
        public static int Run(string[] args)
        {
            int fail = 0;
            Action<string, bool> chk = delegate(string name, bool ok)
            { Console.WriteLine((ok ? "[OK] " : "[FAIL] ") + name); if (!ok) fail++; };

            Type t = typeof(MainForm);
            System.Reflection.FieldInfo xmlFi = t.GetField("tbXml", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            System.Reflection.FieldInfo icsFi = t.GetField("tbIcs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            System.Reflection.FieldInfo cacheFi = t.GetField("tbCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // 新增回归：启动地址栏必须留空（内部默认数据已移除）；浏览默认目录来自动态反查的系统 ToolBox
            MainForm blank = new MainForm(new string[0]);
            chk("启动XML地址栏为空", ((TextBox)xmlFi.GetValue(blank)).Text.Length == 0);
            chk("启动ICS地址栏为空", ((TextBox)icsFi.GetValue(blank)).Text.Length == 0);
            chk("启动CACHE地址栏为空", ((TextBox)cacheFi.GetValue(blank)).Text.Length == 0);

            // 临时根验证：不写死 29.0 / zh-CN，多版本时取最高版本，同版本优先 zh-CN
            string fakeRoot = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_toolboxprobe");
            try
            {
                if (Directory.Exists(fakeRoot)) Directory.Delete(fakeRoot, true);
                string probeLow = Path.Combine(fakeRoot, "CAXA 3D", "28.0", "en-US", ToolboxPath.DirName);
                string probeHighZh = Path.Combine(fakeRoot, "CAXA 3D", "29.0", "zh-CN", ToolboxPath.DirName);
                string probeHighFr = Path.Combine(fakeRoot, "CAXA 3D", "29.0", "fr-FR", ToolboxPath.DirName);
                Directory.CreateDirectory(probeLow);
                Directory.CreateDirectory(probeHighZh);
                Directory.CreateDirectory(probeHighFr);
                string fakeHit = ToolboxPath.Resolve(new string[] { fakeRoot });
                chk("动态反查最高版本+首选语言", fakeHit == probeHighZh);
            }
            finally
            {
                try { if (Directory.Exists(fakeRoot)) Directory.Delete(fakeRoot, true); } catch { }
            }

            string sysToolbox = ToolboxPath.Resolve();
            Console.WriteLine("[INFO] 系统ToolBox=" + (sysToolbox ?? "<未发现>"));
            chk("系统ToolBox路径有效", string.IsNullOrEmpty(sysToolbox) || Directory.Exists(sysToolbox));
            string expectDir = sysToolbox;
            if (string.IsNullOrEmpty(expectDir))
            {
                expectDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAXA");
                if (!Directory.Exists(expectDir)) expectDir = AppDomain.CurrentDomain.BaseDirectory;
            }
            chk("浏览默认目录=系统ToolBox", MainForm.DefaultBrowseDirectory() == expectDir);
            blank.Dispose();

            string sampleXml = args.Length > 1 ? args[1] : @"C:\code\ICAPI\自定义设计库\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783\全牙外六角螺栓_8.8级-发黑_碳钢_GB／T5783.XML";
            MainForm f = new MainForm(new string[] { sampleXml });
            Func<string, object> field = delegate(string n)
            {
                System.Reflection.FieldInfo fi = t.GetField(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return fi == null ? null : fi.GetValue(f);
            };

            chk("窗体创建成功", f != null);
            chk("ClientSize=1190x700", f.ClientSize.Width == 1190 && f.ClientSize.Height == 700);
            chk("窗体可缩放(Sizable)", f.FormBorderStyle == FormBorderStyle.Sizable);
            chk("允许最大化", f.MaximizeBox);

            DataGridView dgRow = field("dgvRow") as DataGridView;
            DataGridView dgParam = field("dgvParam") as DataGridView;
            DataGridView dgCur = field("dgvCur") as DataGridView;
            DataGridView dgCache = field("dgvCache") as DataGridView;
            DataTable dtRowT = field("dtRow") as DataTable;
            DataTable dtParamT = field("dtParam") as DataTable;
            DataTable dtCurT = field("dtCur") as DataTable;
            DataTable dtCacheT = field("dtCache") as DataTable;
            chk("三个DataGridView齐全", dgRow != null && dgParam != null && dgCur != null);
            chk("三个DataTable齐全", dtRowT != null && dtParamT != null && dtCurT != null);
            chk("族信息表2列", dtCurT.Columns.Count == 2);
            chk("目录索引表控件齐全", dgCache != null && dtCacheT != null);
            chk("目录索引表可编辑", dgCache != null && !dgCache.ReadOnly);
            chk("目录索引表双向滚动条启用", dgCache != null
                && (dgCache.ScrollBars & ScrollBars.Horizontal) == ScrollBars.Horizontal
                && (dgCache.ScrollBars & ScrollBars.Vertical) == ScrollBars.Vertical);

            System.Reflection.MethodInfo load = t.GetMethod("LoadXml", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            load.Invoke(f, null);
            chk("规格表330行", dtRowT.Rows.Count == 330);
            chk("规格表21列", dtRowT.Columns.Count == 21);
            Console.WriteLine("[INFO] 参数表列数=" + dtParamT.Columns.Count);
            chk("参数表列数>=10", dtParamT.Columns.Count >= 10);
            chk("族信息表12行", dtCurT.Rows.Count == 12);
            chk("参数表22行", dtParamT.Rows.Count == 22);

            // ===== 新增列联动参数行（模板回退链：本样本无 SC图厚 → SC规格） =====
            int tplIdx = -1;
            for (int i = 0; i < dtParamT.Rows.Count; i++)
                if (Convert.ToString(dtParamT.Rows[i]["Custom_property"]) == "SC规格") { tplIdx = i; break; }
            chk("样本含SC规格模板行", tplIdx >= 0);
            System.Reflection.MethodInfo addCol = t.GetMethod("AddColumnCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            addCol.Invoke(f, new object[] { "测试新列" });
            chk("新增列后规格表22列", dtRowT.Columns.Count == 22);
            chk("新增列联动参数行(23行)", dtParamT.Rows.Count == 23);
            DataRow newPr = dtParamT.Rows[22];
            chk("新参数行名称随列名", Convert.ToString(newPr["Custom_property"]) == "测试新列" && Convert.ToString(newPr["Display_name"]) == "测试新列");
            bool tplSame = tplIdx >= 0;
            if (tplSame)
                foreach (string k in new string[] { "Grid_item_type", "Grid_item_state", "unit_type", "AddtoDisplayName", "HasDelimiters", "hasBeenAdded" })
                    if (Convert.ToString(newPr[k]) != Convert.ToString(dtParamT.Rows[tplIdx][k])) tplSame = false;
            chk("新参数行默认值=模板行", tplSame);
            System.Collections.Generic.List<System.Collections.Generic.List<string>> pko =
                field("paramKeyOrder") as System.Collections.Generic.List<System.Collections.Generic.List<string>>;
            chk("paramKeyOrder与行序同步(23)", pko != null && pko.Count == 23);
            // 保存链路：SyncFromUi → Render → 重载，新参数行必须落盘
            System.Reflection.MethodInfo sync = t.GetMethod("SyncFromUi", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            sync.Invoke(f, null);
            XmlCore.Doc docUi = field("doc") as XmlCore.Doc;
            string tmpUi = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_uiparam.xml");
            File.WriteAllText(tmpUi, XmlCore.Render(docUi), new UTF8Encoding(false));
            XmlCore.Doc reUi = XmlCore.Load(tmpUi);
            chk("界面新增参数行落盘(23参数)", reUi.Params.Count == 23);
            chk("落盘新参数名称可读", XmlCore.Get(reUi.Params[22], "Custom_property") == "测试新列");
            chk("落盘规格行含新列", XmlCore.Get(reUi.Rows[0], "测试新列") != null);

            GroupBox gbf = null; TabControl tabc = null;
            foreach (Control c in f.Controls)
            {
                GroupBox gb = c as GroupBox;
                if (gb != null && gb.Text.Contains("XML 必选")) gbf = gb;
                TabControl tcx = c as TabControl;
                if (tcx != null) tabc = tcx;
            }
            bool hasCheckArea = false;
            foreach (Control c in f.Controls)
            {
                GroupBox gb = c as GroupBox;
                if (gb != null && gb.Text.Contains("三方比对")) hasCheckArea = true;
            }
            chk("比对区已删除(顶层无三方比对GroupBox)", !hasCheckArea);
            chk("文件区在Tab区上方", gbf != null && tabc != null && gbf.Bottom <= tabc.Top);
            chk("文件区不超出客户区", gbf != null && gbf.Right <= f.ClientSize.Width && gbf.Bottom <= f.ClientSize.Height);
            chk("Tab区不超出客户区", tabc != null && tabc.Right <= f.ClientSize.Width + 20 && tabc.Bottom <= f.ClientSize.Height + 20);
            // 文件区控件树里现有 XML + ICS + CACHE 三个 TextBox（本轮新增 CACHE 选择栏）
            int addrTb = 0;
            if (gbf != null) foreach (Control cc in gbf.Controls) if (cc is TextBox) addrTb++;
            chk("文件区XML/ICS/CACHE三个地址栏", addrTb == 3);
            chk("Tab区共4页(含目录索引)", tabc != null && tabc.TabPages.Count == 4);
            // 行标题：数据表首列存在（冻结列是 DataGridViewColumn.Frozen，未布局时列集合为空，实机由 LoadXml 设置）
            chk("规格表数据表已建列(行标题首列存在)", dtRowT != null && dtRowT.Columns.Count > 0);
            chk("规格表水平滚动条启用", dgRow != null && (dgRow.ScrollBars & ScrollBars.Horizontal) == ScrollBars.Horizontal);
            chk("参数表水平滚动条启用", dgParam != null && (dgParam.ScrollBars & ScrollBars.Horizontal) == ScrollBars.Horizontal);
            chk("规格表垂直滚动条启用", dgRow != null && (dgRow.ScrollBars & ScrollBars.Vertical) == ScrollBars.Vertical);
            chk("参数表垂直滚动条启用", dgParam != null && (dgParam.ScrollBars & ScrollBars.Vertical) == ScrollBars.Vertical);
            chk("列宽不自适应(滚动条可出现)", dgRow != null && dgRow.AutoSizeColumnsMode == DataGridViewAutoSizeColumnsMode.None
                && dgParam != null && dgParam.AutoSizeColumnsMode == DataGridViewAutoSizeColumnsMode.None);
            if (gbf != null) Console.WriteLine("[INFO] 文件区 Bounds=" + gbf.Bounds);
            if (tabc != null) Console.WriteLine("[INFO] Tab区 Bounds=" + tabc.Bounds);

            try
            {
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(150);
                Application.DoEvents();

                // ===== 滚动条几何断言（Show 后列集合已生成）：列总宽/高必须超出视口，滚动条才真的有内容可滚 =====
                int rowColsW = 0; foreach (DataGridViewColumn c in dgRow.Columns) if (c.Visible) rowColsW += c.Width;
                chk("规格表列已生成", dgRow.Columns.Count >= 21);
                chk("参数表列已生成", dgParam.Columns.Count >= 10);
                // 固定列宽：规格表每列 100px（首列120）、参数表 150px；列总宽超出视口即出水平滚动条
                chk("规格表列总宽超出视口(水平可滚)", rowColsW > dgRow.ClientSize.Width);
                chk("规格表行总高超出视口(垂直可滚)", (long)dgRow.Rows.Count * dgRow.RowTemplate.Height > dgRow.ClientSize.Height);
                // HorizontalScrollBar/VerticalScrollBar 是受保护成员，用反射读 Visible（离屏验证程序集内部状态的标准手法）
                Func<DataGridView, string, bool> scrollVisible = delegate (DataGridView g, string prop)
                {
                    System.Reflection.PropertyInfo pi = typeof(DataGridView).GetProperty(prop,
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    object sb = pi == null ? null : pi.GetValue(g, null);
                    System.Reflection.PropertyInfo vp = sb == null ? null : sb.GetType().GetProperty("Visible");
                    return vp != null && (bool)vp.GetValue(sb, null);
                };
                chk("规格表水平滚动条实际可见", scrollVisible(dgRow, "HorizontalScrollBar"));
                chk("规格表垂直滚动条实际可见", scrollVisible(dgRow, "VerticalScrollBar"));

                // ===== 参数表删行/增行（CurrentCell 定位，CellSelect 坑 13） =====
                System.Reflection.MethodInfo delPr = t.GetMethod("DelParamRow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                System.Reflection.MethodInfo addPr = t.GetMethod("AddParamRow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                tabc.SelectedIndex = 1; Application.DoEvents();
                dgParam.CurrentCell = dgParam.Rows[22].Cells[0];
                delPr.Invoke(f, null);
                chk("参数表删行(23→22)", dtParamT.Rows.Count == 22);
                addPr.Invoke(f, null);
                chk("参数表增行(22→23)", dtParamT.Rows.Count == 23);
                dgParam.CurrentCell = dgParam.Rows[22].Cells[0];
                delPr.Invoke(f, null);
                chk("参数表再删行(23→22)", dtParamT.Rows.Count == 22);
                System.Collections.Generic.List<System.Collections.Generic.List<string>> pko2 =
                    field("paramKeyOrder") as System.Collections.Generic.List<System.Collections.Generic.List<string>>;
                chk("增删后paramKeyOrder同步(22)", pko2 != null && pko2.Count == 22);
                tabc.SelectedIndex = 0; Application.DoEvents();

                // ===== 宽度随动 / 表格区随最大化：缩小→放大→断言→还原 =====
                // ★ 目标尺寸必须按屏幕工作区自适应（11-21 实证：本机 1366x768 / 工作区 738，
                //   写死 1400x860 时窗体先被系统钳到工作区、ClientSize 再回写请求值但不重跑 Layout，
                //   底部锚定按钮停在钳制阶段位置 ⇒ 假失败。先缩到 1000x620 保证两个方向都有真实增量）。
                Button saveBtn0 = null;
                foreach (Control c in f.Controls) { Button bb = c as Button; if (bb != null && bb.Text.Contains("保存 XML")) saveBtn0 = bb; }
                f.ClientSize = new Size(1000, 620);
                Application.DoEvents();
                Rectangle wa = Screen.FromControl(f).WorkingArea;
                Size nc = new Size(f.Width - f.ClientSize.Width, f.Height - f.ClientSize.Height);
                Size big = new Size(wa.Width - nc.Width, wa.Height - nc.Height);
                Console.WriteLine("[INFO] 放大前 ClientSize=" + f.ClientSize + " 工作区=" + wa +
                    " 目标=" + big + " saveBtn=" + (saveBtn0 == null ? "<null>" : saveBtn0.Bounds.ToString()));
                chk("放大目标两向均大于基线", big.Width > f.ClientSize.Width && big.Height > f.ClientSize.Height);
                int rowW0 = dgRow.Width, rowH0 = dgRow.Height, tabH0 = tabc.Height;
                f.ClientSize = big;
                Application.DoEvents();
                chk("放大后文件区宽度随动", gbf != null && gbf.Width > 1166);
                chk("放大后Tab区随动", tabc != null && tabc.Width > 1166 && tabc.Height > tabH0);
                chk("放大后规格表随动", dgRow.Width > rowW0 && dgRow.Height > rowH0);
                // 参数表此时非活动 Tab（离屏下非活动页不参与布局、尺寸不变是 WinForms 正常行为），
                // 先切到参数表页让它参与布局，再断言"参数表与规格表同一布局基准"——两者都被 ClampGridsToParent 钳到相同尺寸
                tabc.SelectedIndex = 1;
                Application.DoEvents();
                chk("参数表与规格表布局基准一致", dgParam.Width == dgRow.Width && dgParam.Height == dgRow.Height);
                tabc.SelectedIndex = 0;
                Application.DoEvents();
                Button saveBtn = null;
                foreach (Control c in f.Controls) { Button bb = c as Button; if (bb != null && bb.Text.Contains("保存 XML")) saveBtn = bb; }
                Console.WriteLine("[INFO] 放大后 ClientSize=" + f.ClientSize + " saveBtn=" +
                    (saveBtn == null ? "<null>" : saveBtn.Bounds.ToString() + " Anchor=" + saveBtn.Anchor));
                chk("放大后底部按钮贴底", saveBtn != null && saveBtn.Bottom <= f.ClientSize.Height && saveBtn.Bottom >= f.ClientSize.Height - 60);
                f.ClientSize = new Size(1190, 700);
                Application.DoEvents();
                chk("还原后ClientSize=1190x700", f.ClientSize.Width == 1190 && f.ClientSize.Height == 700);

                string dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_dump");
                Directory.CreateDirectory(dumpDir);

                // ===== Excel 元数据与 .cache 对齐测试基线（11-21 用户要求）=====
                // 后续导入测试 B/C 的 B1/B2 必须能在 .cache 定位且一致（各用唯一 GUID 避免歧义）
                string xcachePath = Path.Combine(dumpDir, "excel_meta_check.cache");
                File.WriteAllText(xcachePath,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Root>\r\n<Version version=\"1\">\r\n</Version><FolderList>\r\n" +
                    "<Folder name=\"导入测试\" display_name=\"导入测试\" type=\"TB_PART\" GUID=\"{75CDDC6E-57A5-4E9A-9590-50D713DDB453}\" protected=\"FALSE\">\r\n</Folder>" +
                    "<Folder name=\"导入测试2\" display_name=\"导入测试2\" type=\"TB_PART\" GUID=\"{B4AFBADB-DD59-4B48-893C-4A5109B37A2F}\" protected=\"FALSE\">\r\n</Folder>" +
                    "</FolderList></Root>\r\n", new UTF8Encoding(false));
                System.Reflection.FieldInfo xcdFi = t.GetField("cacheDoc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                xcdFi.SetValue(f, CacheCore.Load(xcachePath));

                // ===== Excel 导出/导入 round-trip：零依赖 XlsxIO 写→读，比对行数/列数/单元格 =====
                System.Collections.Generic.List<string> xh = new System.Collections.Generic.List<string>();
                xh.Add("标准件库零件名"); xh.Add("公称长度"); xh.Add("螺纹规格"); xh.Add("SC物料描述");
                System.Collections.Generic.List<System.Collections.Generic.List<string>> xd =
                    new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
                for (int r = 0; r < dtRowT.Rows.Count; r++)
                {
                    System.Collections.Generic.List<string> one = new System.Collections.Generic.List<string>();
                    for (int c = 0; c < dtRowT.Columns.Count && c < xh.Count; c++)
                        one.Add(Convert.ToString(dtRowT.Rows[r][c]) ?? "");
                    xd.Add(one);
                }
                string xlsxOut = Path.Combine(dumpDir, "roundtrip.xlsx");
                XlsxIO.WriteTemplate(xlsxOut, "测试零件名", "{75CDDC6E-57A5-4E9A-9590-50D713DDB453}", xh, xd);
                chk("Excel导出文件生成", File.Exists(xlsxOut) && new FileInfo(xlsxOut).Length > 1000);
                System.Collections.Generic.List<System.Collections.Generic.List<string>> back = XlsxIO.ReadSheet(xlsxOut);
                chk("Excel读回行数=数据+5行头", back.Count == xd.Count + 5);
                chk("Excel读回行1零件名", back.Count > 0 && back[0].Count > 1 && back[0][0] == "标准件库零件名" && back[0][1] == "测试零件名");
                chk("Excel读回行2GUID", back.Count > 1 && back[1].Count > 1 && back[1][1] == "{75CDDC6E-57A5-4E9A-9590-50D713DDB453}");
                chk("Excel读回行4表头", back.Count > 3 && back[3].Count >= xh.Count && back[3][2] == "螺纹规格");
                chk("Excel读回行5说明", back.Count > 4 && back[4].Count > 0 && back[4][0].StartsWith("请从下一行开始填写数据"));
                chk("Excel读回首行数据一致", back.Count > 5 && back[5][2] == Convert.ToString(dtRowT.Rows[0][2]));
                chk("Excel读回末行数据一致", back.Count == xd.Count + 5 && back[back.Count - 1][2] == Convert.ToString(dtRowT.Rows[xd.Count - 1][2]));
                // UI 列映射：Dimension→Display_name（l→公称长度），中文列原样
                System.Reflection.MethodInfo rkeh = t.GetMethod("RowKeyToHeader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                string hL = rkeh.Invoke(f, new object[] { "l" }) as string;
                string hScrew = rkeh.Invoke(f, new object[] { "螺纹规格" }) as string;
                chk("列映射 Dimension→中文(l=公称长度)", hL == "公称长度");
                chk("列映射中文列原样(螺纹规格)", hScrew == "螺纹规格");
                System.Reflection.MethodInfo htok = t.GetMethod("HeaderToRowKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                string kLen = htok.Invoke(f, new object[] { "公称长度" }) as string;
                string kScrew = htok.Invoke(f, new object[] { "螺纹规格" }) as string;
                chk("反查映射 中文→Dimension(公称长度=l)", kLen == "l");
                chk("反查映射 中文列原样(螺纹规格)", kScrew == "螺纹规格");

                // ===== 列增删联动 + 导入结构同步（本轮新增需求）=====
                System.Reflection.MethodInfo delColM = t.GetMethod("DelColumnCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                System.Reflection.MethodInfo importCore = t.GetMethod("ImportTableExcelCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                // A. 删除列联动（自包含：先加一列，再删之，验证参数表同步删行）
                int colA0 = dtRowT.Columns.Count, parA0 = dtParamT.Rows.Count;
                addCol.Invoke(f, new object[] { "自检删列测试" });
                chk("删除列前置→参数表已加行", dtParamT.Rows.Count == parA0 + 1);
                delColM.Invoke(f, new object[] { "自检删列测试" });
                chk("删除列→规格表列数复原", dtRowT.Columns.Count == colA0);
                chk("删除列→参数表行数复原(联动)", dtParamT.Rows.Count == parA0);
                chk("删除列→RowKeys已移除", !(field("doc") as XmlCore.Doc).RowKeys.Contains("自检删列测试"));
                chk("删除列→paramKeyOrder复原", (field("paramKeyOrder") as System.Collections.Generic.List<System.Collections.Generic.List<string>>).Count == parA0);

                // B. 导入"列增加 + 行增减"：构造比当前多 1 列、仅 3 行数据的 Excel
                System.Collections.Generic.List<string> ih = new System.Collections.Generic.List<string>();
                foreach (DataColumn dc in dtRowT.Columns) ih.Add((string)rkeh.Invoke(f, new object[] { dc.ColumnName }));
                ih.Add("自检导入列");
                System.Collections.Generic.List<System.Collections.Generic.List<string>> idataB =
                    new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
                for (int r = 0; r < 3; r++)
                {
                    System.Collections.Generic.List<string> one = new System.Collections.Generic.List<string>();
                    for (int c = 0; c < ih.Count; c++)
                        one.Add(c == 0 ? "M6x20-导入" + r : (c == ih.Count - 1 ? "X" + r : "值" + r + "_" + c));
                    idataB.Add(one);
                }
                string importB = Path.Combine(dumpDir, "import_cols_add.xlsx");
                XlsxIO.WriteTemplate(importB, "导入测试", "{75CDDC6E-57A5-4E9A-9590-50D713DDB453}", ih, idataB);
                int colB0 = dtRowT.Columns.Count, parB0 = dtParamT.Rows.Count;
                importCore.Invoke(f, new object[] { importB });
                chk("导入增列→规格表列数+1", dtRowT.Columns.Count == colB0 + 1);
                chk("导入增列→参数表行数+1(联动)", dtParamT.Rows.Count == parB0 + 1);
                chk("导入增列→新列存在", dtRowT.Columns.Contains("自检导入列"));
                chk("导入行同步→规格表3行", dtRowT.Rows.Count == 3);
                chk("导入首行首列数据正确", Convert.ToString(dtRowT.Rows[0][0]) == "M6x20-导入0");
                chk("导入新列数据正确", Convert.ToString(dtRowT.Rows[0]["自检导入列"]) == "X0");

                // C. 导入"列减少 + 行再变化"：去掉新增列，仅 2 行数据
                System.Collections.Generic.List<string> ih2 = new System.Collections.Generic.List<string>(ih);
                ih2.Remove("自检导入列");
                System.Collections.Generic.List<System.Collections.Generic.List<string>> idataC =
                    new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
                for (int r = 0; r < 2; r++)
                {
                    System.Collections.Generic.List<string> one = new System.Collections.Generic.List<string>();
                    for (int c = 0; c < ih2.Count; c++) one.Add(c == 0 ? "M8x30-减列" + r : "C" + r + "_" + c);
                    idataC.Add(one);
                }
                string importC = Path.Combine(dumpDir, "import_cols_del.xlsx");
                XlsxIO.WriteTemplate(importC, "导入测试2", "{B4AFBADB-DD59-4B48-893C-4A5109B37A2F}", ih2, idataC);
                int colC0 = dtRowT.Columns.Count, parC0 = dtParamT.Rows.Count;
                importCore.Invoke(f, new object[] { importC });
                chk("导入删列→规格表列数-1", dtRowT.Columns.Count == colC0 - 1);
                chk("导入删列→参数表行数-1(联动)", dtParamT.Rows.Count == parC0 - 1);
                chk("导入删列→新列已移除", !dtRowT.Columns.Contains("自检导入列"));
                chk("导入行再同步→规格表2行", dtRowT.Rows.Count == 2);

                // ===== 本轮新增：导出 B1/B2 取 .cache 行；导入 B1/B2 与 .cache 不一致拒绝 =====
                string exportCorePath = Path.Combine(dumpDir, "export_cache_meta.xlsx");
                System.Reflection.MethodInfo exportCore = t.GetMethod("ExportTableExcelCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                chk("导出核心方法存在", exportCore != null);
                XmlCore.Doc curDoc = field("doc") as XmlCore.Doc;
                string xmlName = XmlCore.Get(curDoc.Cur, "name") ?? "";
                string xmlGuid = XmlCore.Get(curDoc.Cur, "GUID") ?? "";
                string expectName = "CACHE权威零件名";
                // 定位锚点 GUID 与 XML 相同（双处登记语义），仅 name 不同 ⇒ 验证导出取 .cache 的 name 而非 XML 名
                string metaCache = Path.Combine(dumpDir, "export_meta.cache");
                File.WriteAllText(metaCache,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Root>\r\n<Version version=\"1\">\r\n</Version><FolderList>\r\n" +
                    "<Folder name=\"" + expectName + "\" display_name=\"x\" type=\"TB_PART\" GUID=\"" + xmlGuid + "\" protected=\"FALSE\">\r\n</Folder>" +
                    "</FolderList></Root>\r\n", new UTF8Encoding(false));
                CacheCore.CacheDoc metaCd = CacheCore.Load(metaCache);
                xcdFi.SetValue(f, metaCd);                    // 内存 .cache = 单行权威行（GUID 锚点=XML，name≠XML）
                exportCore.Invoke(f, new object[] { exportCorePath });
                System.Collections.Generic.List<System.Collections.Generic.List<string>> exBack = XlsxIO.ReadSheet(exportCorePath);
                chk("导出B1取.cache零件名(非XML)", exBack.Count > 0 && exBack[0][1] == expectName);
                chk("导出B2取.cache GUID(=XML锚点)", exBack.Count > 1 && exBack[1][1] == xmlGuid);
                chk("导出B1确与XML名不同", expectName != xmlName);

                // 导入拒绝：B1/B2 与 .cache 单行不一致
                System.Collections.Generic.List<string> badH = new System.Collections.Generic.List<string>();
                foreach (DataColumn dc in dtRowT.Columns) badH.Add((string)rkeh.Invoke(f, new object[] { dc.ColumnName }));
                System.Collections.Generic.List<System.Collections.Generic.List<string>> badData =
                    new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
                System.Collections.Generic.List<string> badOne = new System.Collections.Generic.List<string>();
                for (int c = 0; c < badH.Count; c++) badOne.Add(c == 0 ? "M6x20-不该导入" : "V" + c);
                badData.Add(badOne);
                string badXlsx = Path.Combine(dumpDir, "import_reject.xlsx");
                // GUID 锚点=cache 行（能定位），零件名错 ⇒ 走"不一致"拒绝分支（非"无对应行"）
                XlsxIO.WriteTemplate(badXlsx, "错误零件名", xmlGuid, badH, badData);
                int rowsBeforeReject = dtRowT.Rows.Count;
                importCore.Invoke(f, new object[] { badXlsx });
                chk("导入不一致被拒→行数不变", dtRowT.Rows.Count == rowsBeforeReject);
                Label stRej = field("lblStatus") as Label;
                chk("导入拒绝状态栏点名不一致", stRej != null && stRej.Text.Contains("不一致"));

                // 恢复基线 .cache（供后续 cache 界面链路测试）
                xcdFi.SetValue(f, CacheCore.Load(xcachePath));
                // 恢复原数据供截图（导入测试改了规格表结构/行数）
                load.Invoke(f, null);

                // ===== .cache 目录索引界面链路：加载→显示→定位族XML→找不到第三态 =====
                string cacheRoot = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_cacheprobe");
                try
                {
                    if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true);
                    Directory.CreateDirectory(cacheRoot);
                    string famDir = Path.Combine(cacheRoot, "探针族A");
                    Directory.CreateDirectory(famDir);
                    string famXml = Path.Combine(famDir, "探针族A.XML");
                    File.Copy(sampleXml, famXml);
                    string cacheProbe = Path.Combine(cacheRoot, ".cache");
                    File.WriteAllText(cacheProbe,
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Root>\r\n" +
                        "<Version version=\"1\">\r\n</Version><FolderList>\r\n" +
                        "<Folder name=\"探针族A\" display_name=\"探针族 A\" type=\"TB_PART\" GUID=\"{B4AFBADB-DD59-4B48-893C-4A5109B37A2F}\" protected=\"TRUE\">\r\n" +
                        "</Folder><Folder name=\"不存在族B\" display_name=\"探针族 B\" type=\"TB_PART\" GUID=\"{75CDDC6E-57A5-4E9A-9590-50D713DDB453}\" protected=\"FALSE\">\r\n" +
                        "</Folder></FolderList></Root>", new UTF8Encoding(false));
                    TextBox tbCacheT = field("tbCache") as TextBox;
                    tbCacheT.Text = cacheProbe;
                    System.Reflection.MethodInfo loadCache = t.GetMethod("LoadCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    loadCache.Invoke(f, null);
                    chk("cache加载2条", dtCacheT.Rows.Count == 2);
                    chk("cache表5列", dtCacheT.Columns.Count == 5);
                    chk("cache GUID列值正确", Convert.ToString(dtCacheT.Rows[0]["GUID"]) == "{B4AFBADB-DD59-4B48-893C-4A5109B37A2F}");
                    chk("cache中文显示名不乱码", Convert.ToString(dtCacheT.Rows[0]["display_name"]) == "探针族 A");
                    tabc.SelectedIndex = 3; Application.DoEvents();
                    chk("cache列已生成(5列)", dgCache.Columns.Count == 5);
                    chk("cache首列冻结", dgCache.Columns.Count > 0 && dgCache.Columns[0].Frozen);
                    System.Reflection.MethodInfo openFam = t.GetMethod("OpenSelectedFamilyXml", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    dgCache.CurrentCell = dgCache.Rows[0].Cells[0];
                    openFam.Invoke(f, null);
                    chk("定位打开族XML地址栏", ((TextBox)xmlFi.GetValue(f)).Text == famXml);
                    chk("定位后规格表330行", dtRowT.Rows.Count == 330);
                    chk("定位后跳回规格表页", tabc.SelectedIndex == 0);
                    // 第三态：条目目录不存在 → 地址栏不变、状态点名，不猜不假成功
                    tabc.SelectedIndex = 3; Application.DoEvents();
                    string before = ((TextBox)xmlFi.GetValue(f)).Text;
                    dgCache.CurrentCell = dgCache.Rows[1].Cells[0];
                    openFam.Invoke(f, null);
                    chk("目录不存在不打开(第三态)", ((TextBox)xmlFi.GetValue(f)).Text == before);
                    Label status = field("lblStatus") as Label;
                    chk("第三态状态栏点名目录不存在", status != null && status.Text.Contains("目录不存在"));

                    // ===== .cache 编辑写回界面链路：改单元格→同步→保存→重读盘上文件 =====
                    tabc.SelectedIndex = 3; Application.DoEvents();
                    System.Reflection.MethodInfo syncCache = t.GetMethod("SyncCacheFromUi", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    System.Reflection.MethodInfo saveCache = t.GetMethod("SaveCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    chk("cache编辑方法齐全", syncCache != null && saveCache != null);
                    dtCacheT.Rows[0]["display_name"] = "探针族 A改名";
                    dtCacheT.AcceptChanges();
                    syncCache.Invoke(f, null);
                    System.Reflection.FieldInfo cacheDocFi = t.GetField("cacheDoc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    CacheCore.CacheDoc memDoc = cacheDocFi.GetValue(f) as CacheCore.CacheDoc;
                    chk("cache表格改动同步到内存文档", memDoc != null && XmlCore.Get(memDoc.Entries[0].Attrs, "display_name") == "探针族 A改名");
                    saveCache.Invoke(f, null);
                    CacheCore.CacheDoc reread = CacheCore.Load(cacheProbe);
                    chk("cache保存到盘上可重读", reread.Entries.Count == 2);
                    chk("cache保存新值落盘", XmlCore.Get(reread.Entries[0].Attrs, "display_name") == "探针族 A改名");
                    chk("cache保存未伤其他条目 GUID", XmlCore.Get(reread.Entries[1].Attrs, "GUID") == "{75CDDC6E-57A5-4E9A-9590-50D713DDB453}");
                    chk("cache保存状态栏点名已保存", status != null && status.Text.Contains("已保存 .cache"));
                    // 新增/删除条目按钮方法
                    System.Reflection.MethodInfo addCacheRow = t.GetMethod("AddCacheRow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    System.Reflection.MethodInfo delCacheRow = t.GetMethod("DelCacheRow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    chk("cache增删行方法齐全", addCacheRow != null && delCacheRow != null);
                    addCacheRow.Invoke(f, null);
                    chk("cache新增行后3条", dtCacheT.Rows.Count == 3);
                    delCacheRow.Invoke(f, null);
                    chk("cache删除行后2条", dtCacheT.Rows.Count == 2);

                    tabc.SelectedIndex = 0; Application.DoEvents();
                    // 恢复样本地址供截图
                    ((TextBox)xmlFi.GetValue(f)).Text = sampleXml;
                    load.Invoke(f, null);
                }
                finally
                {
                    try { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true); } catch { }
                }

                // ===== XML / ICS / CACHE 三栏自动联动（11-21 用户要求：同名互选 + 上一级 .cache） =====
                string linkRoot = Path.Combine(Path.GetTempPath(), "caxa3dtoolboxmaker_linkprobe");
                try
                {
                    if (Directory.Exists(linkRoot)) Directory.Delete(linkRoot, true);
                    string famDir = Path.Combine(linkRoot, "族X");
                    Directory.CreateDirectory(famDir);
                    string linkXml = Path.Combine(famDir, "族X.XML");
                    string linkIcs = Path.Combine(famDir, "族X.ics");
                    string linkCache = Path.Combine(linkRoot, ".cache");
                    File.Copy(sampleXml, linkXml);
                    File.WriteAllText(linkIcs, "probe");
                    File.WriteAllText(linkCache, "probe");
                    TextBox xb = (TextBox)xmlFi.GetValue(f);
                    TextBox ib = (TextBox)icsFi.GetValue(f);
                    TextBox cb = (TextBox)cacheFi.GetValue(f);

                    // 选 XML → 同名 ICS + 上一级 .cache（设 Text 触发 TextChanged 自动联动）
                    xb.Text = linkXml;
                    chk("XML联动同名ICS", ib.Text == linkIcs);
                    chk("XML联动上一级CACHE", cb.Text == linkCache);

                    // 选 ICS → 反推同名 XML + 上一级 .cache（先清空 XML/ICS 证明是反推而非沿用；
                    //   ICS 需先清空再设，WinForms 对相同文本不触发 TextChanged）
                    xb.Text = "";
                    ib.Text = "";
                    ib.Text = linkIcs;
                    chk("ICS反推同名XML", xb.Text == linkXml);
                    chk("ICS联动上一级CACHE", cb.Text == linkCache);

                    // 第三态：同名 ICS 不存在时不臆造路径
                    File.Delete(linkIcs);
                    ib.Text = "";
                    cb.Text = "";
                    xb.Text = linkXml;
                    chk("无同名ICS不臆造", ib.Text == "");
                    // 第三态：上一级无 .cache 时不强填
                    File.Delete(linkCache);
                    cb.Text = "";
                    xb.Text = "";
                    xb.Text = linkXml;
                    chk("无缓存时CACHE留空", cb.Text == "");
                }
                finally
                {
                    try { if (Directory.Exists(linkRoot)) Directory.Delete(linkRoot, true); } catch { }
                }

                Bitmap bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                string png = Path.Combine(dumpDir, "mainform.png");
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                bmp.Dispose();
                f.Hide();
                Console.WriteLine("[INFO] 截图=" + png);
                chk("截图成功", File.Exists(png) && new FileInfo(png).Length > 1000);
            }
            catch (Exception ex) { chk("截图成功(false):" + ex.Message, false); }

            // Show() 过的窗体必须 Close() 再 Dispose，否则 WinForms 内部句柄残留、进程不退出（会锁住 exe）
            try { f.Close(); } catch { }
            f.Dispose();
            Console.WriteLine("=== UITEST " + (fail == 0 ? "PASS" : "FAIL(" + fail + ")") + " ===");
            return fail == 0 ? 0 : 1;
        }
    }
}
