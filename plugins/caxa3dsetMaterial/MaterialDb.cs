using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace caxa3dsetMaterial
{
    /// <summary>
    /// 一行材料资料：分类 / 材料 / 密度 / 颜色 / 常用标记，
    /// 以及材料页右下「属性」表的那一组物理量（只能从资料库 XML 得到，API 不提供写入）。
    /// </summary>
    internal sealed class MaterialItem
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public string Density { get; set; }
        public string Color { get; set; }
        public bool Favorite { get; set; }

        // ---- 材料页属性表（只读展示，来源：官方材料库 XML 的 physicalproperties 节点） ----
        public string Model { get; set; }   // 模型类型，如 线性弹性各向同性
        public string Description { get; set; }
        public string Ex { get; set; }      // 弹性模量 N/m²
        public string NuXY { get; set; }    // 泊松比
        public string GXY { get; set; }     // 抗剪模量 N/m²
        public string Alpx { get; set; }    // 热膨胀系数 /K
        public string Kx { get; set; }      // 热导率 W/(m·K)
        public string C { get; set; }       // 比热 J/(kg·K)
        public string SigXt { get; set; }   // 抗拉强度 N/m²
        public string SigYld { get; set; }  // 屈服强度 N/m²
        public string SigXc { get; set; }   // 抗压强度 N/m²

        public MaterialItem()
        {
            Category = "";
            Name = "";
            Density = "";
            Color = "";
            Favorite = false;
            Model = "";
            Description = "";
            Ex = "";
            NuXY = "";
            GXY = "";
            Alpx = "";
            Kx = "";
            C = "";
            SigXt = "";
            SigYld = "";
            SigXc = "";
        }

        /// <summary>是否已带物理属性（老版本资料库条目为空）。</summary>
        public bool HasPhysics
        {
            get { return !string.IsNullOrEmpty(Ex) || !string.IsNullOrEmpty(NuXY) || !string.IsNullOrEmpty(Kx); }
        }

        /// <summary>密度转 double；空/非法返回 null。</summary>
        public double? DensityValue()
        {
            double d;
            if (!string.IsNullOrEmpty(Density) &&
                double.TryParse(Density.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d > 0)
                return d;
            return null;
        }

        /// <summary>颜色转 0xRRGGBB；空/非法返回 null（表示不改颜色）。</summary>
        public int? ColorValue()
        {
            string t = (Color ?? "").Trim().TrimStart('#');
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            int v;
            if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                return v;
            return null;
        }
    }

    /// <summary>
    /// 材料资料库：%APPDATA%\caxa3dsetMaterial\自定义材料.xml。
    /// 首次运行若不存在，则从 DLL 同目录的 自定义材料.xml 播种；都没有则建空库。
    /// </summary>
    internal sealed class MaterialDb
    {
        public const string FileName = "自定义材料.xml";

        public BindingList<MaterialItem> Items { get; private set; }
        public string FilePath { get; private set; }

        public MaterialDb()
        {
            Items = new BindingList<MaterialItem>();
            FilePath = ResolvePath();
        }

        public static string DataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "caxa3dsetMaterial");
            }
        }

        public static string ResolvePath()
        {
            return Path.Combine(DataDir, FileName);
        }

        /// <summary>种子文件位置：DLL 所在目录（部署时一起拷贝过去）。</summary>
        private static string SeedPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(MaterialDb).Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    string p = Path.Combine(dir, FileName);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>装载资料库；返回提示信息（空表示无需提示）。</summary>
        public string Load()
        {
            string msg = null;
            try
            {
                if (!File.Exists(FilePath))
                {
                    string seed = SeedPath();
                    if (seed != null)
                    {
                        Directory.CreateDirectory(DataDir);
                        File.Copy(seed, FilePath, true);
                        msg = "首次运行：已从程序目录播种资料库到 " + FilePath;
                        Diagnostics.Log(msg);
                    }
                    else
                    {
                        msg = "未找到资料库，已建立空库：" + FilePath;
                        Diagnostics.Log(msg);
                    }
                }

                if (File.Exists(FilePath))
                {
                    LoadFile(FilePath);
                    int filled = EnrichFromSeed();
                    Diagnostics.Log("资料库已载入：" + FilePath + "，共 " + Items.Count + " 条" +
                                    (filled > 0 ? "（其中 " + filled + " 条补齐了物理属性）" : ""));
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("载入资料库失败：" + ex.Message);
                msg = "载入资料库失败：" + ex.Message;
            }
            return msg;
        }

        private void LoadFile(string path)
        {
            Items = new BindingList<MaterialItem>();
            XmlDocument doc = new XmlDocument();
            doc.Load(path);

            XmlNodeList nodes = doc.SelectNodes("/MaterialDB/item");
            if (nodes == null) return;

            foreach (XmlNode n in nodes)
            {
                MaterialItem it = new MaterialItem
                {
                    Category = Attr(n, "category"),
                    Name = Attr(n, "name"),
                    Density = Attr(n, "density"),
                    Color = Attr(n, "color"),
                    Favorite = Attr(n, "favorite") == "1",
                    Model = Attr(n, "model"),
                    Description = Attr(n, "desc"),
                    Ex = Attr(n, "ex"),
                    NuXY = Attr(n, "nuxy"),
                    GXY = Attr(n, "gxy"),
                    Alpx = Attr(n, "alpx"),
                    Kx = Attr(n, "kx"),
                    C = Attr(n, "c"),
                    SigXt = Attr(n, "sigxt"),
                    SigYld = Attr(n, "sigyld"),
                    SigXc = Attr(n, "sigxc")
                };
                if (it.Name.Length == 0) continue;
                Items.Add(it);
            }
        }

        /// <summary>
        /// 老版本用户资料库没有物理属性字段：按材料名从程序目录的种子库补齐，只填空不覆盖。
        /// 返回补齐条数。
        /// </summary>
        private int EnrichFromSeed()
        {
            string seed = SeedPath();
            if (seed == null) return 0;

            try
            {
                Dictionary<string, MaterialItem> map =
                    new Dictionary<string, MaterialItem>(StringComparer.OrdinalIgnoreCase);
                XmlDocument doc = new XmlDocument();
                doc.Load(seed);
                XmlNodeList nodes = doc.SelectNodes("/MaterialDB/item");
                if (nodes == null) return 0;

                foreach (XmlNode n in nodes)
                {
                    string key = (Attr(n, "name") ?? "").Trim();
                    if (key.Length == 0 || map.ContainsKey(key)) continue;
                    map[key] = new MaterialItem
                    {
                        Model = Attr(n, "model"),
                        Description = Attr(n, "desc"),
                        Ex = Attr(n, "ex"),
                        NuXY = Attr(n, "nuxy"),
                        GXY = Attr(n, "gxy"),
                        Alpx = Attr(n, "alpx"),
                        Kx = Attr(n, "kx"),
                        C = Attr(n, "c"),
                        SigXt = Attr(n, "sigxt"),
                        SigYld = Attr(n, "sigyld"),
                        SigXc = Attr(n, "sigxc")
                    };
                }

                int filled = 0;
                foreach (MaterialItem it in Items)
                {
                    if (it.HasPhysics) continue;
                    MaterialItem s;
                    if (!map.TryGetValue((it.Name ?? "").Trim(), out s)) continue;
                    it.Model = s.Model;
                    it.Description = s.Description;
                    it.Ex = s.Ex;
                    it.NuXY = s.NuXY;
                    it.GXY = s.GXY;
                    it.Alpx = s.Alpx;
                    it.Kx = s.Kx;
                    it.C = s.C;
                    it.SigXt = s.SigXt;
                    it.SigYld = s.SigYld;
                    it.SigXc = s.SigXc;
                    filled++;
                }
                return filled;
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("从种子库补齐物理属性失败：" + ex.Message);
                return 0;
            }
        }

        private static string Attr(XmlNode n, string name)
        {
            if (n.Attributes == null) return "";
            XmlAttribute a = n.Attributes[name];
            return a == null ? "" : (a.Value ?? "");
        }

        /// <summary>保存资料库（覆盖写，UTF-8 无 BOM）。</summary>
        public void Save()
        {
            Directory.CreateDirectory(DataDir);

            XmlDocument doc = new XmlDocument();
            XmlDeclaration decl = doc.CreateXmlDeclaration("1.0", "utf-8", null);
            doc.AppendChild(decl);
            doc.AppendChild(doc.CreateComment(
                " caxa3dsetMaterial 材料资料库：界面表格直接读写本文件。字段：category=分类 name=材料名 density=密度(kg/m³) color=颜色(RRGGBB，空=不改) favorite=1 表示常用 "));

            XmlElement root = doc.CreateElement("MaterialDB");
            XmlAttribute ver = doc.CreateAttribute("version");
            ver.Value = "1";
            root.Attributes.Append(ver);
            doc.AppendChild(root);

            foreach (MaterialItem it in Items)
            {
                if (it == null || string.IsNullOrEmpty((it.Name ?? "").Trim())) continue;

                XmlElement e = doc.CreateElement("item");
                AppendAttr(doc, e, "category", it.Category);
                AppendAttr(doc, e, "name", it.Name);
                AppendAttr(doc, e, "density", it.Density);
                AppendAttr(doc, e, "color", it.Color);
                if (it.Favorite) AppendAttr(doc, e, "favorite", "1");
                AppendAttrIfAny(doc, e, "model", it.Model);
                AppendAttrIfAny(doc, e, "desc", it.Description);
                AppendAttrIfAny(doc, e, "ex", it.Ex);
                AppendAttrIfAny(doc, e, "nuxy", it.NuXY);
                AppendAttrIfAny(doc, e, "gxy", it.GXY);
                AppendAttrIfAny(doc, e, "alpx", it.Alpx);
                AppendAttrIfAny(doc, e, "kx", it.Kx);
                AppendAttrIfAny(doc, e, "c", it.C);
                AppendAttrIfAny(doc, e, "sigxt", it.SigXt);
                AppendAttrIfAny(doc, e, "sigyld", it.SigYld);
                AppendAttrIfAny(doc, e, "sigxc", it.SigXc);
                root.AppendChild(e);
            }

            XmlWriterSettings ws = new XmlWriterSettings();
            ws.Indent = true;
            ws.IndentChars = "  ";
            ws.Encoding = new UTF8Encoding(false);
            using (XmlWriter w = XmlWriter.Create(FilePath, ws))
            {
                doc.Save(w);
            }
            Diagnostics.Log("资料库已保存：" + FilePath + "，共 " + Items.Count + " 条");
        }

        private static void AppendAttr(XmlDocument doc, XmlElement e, string name, string value)
        {
            XmlAttribute a = doc.CreateAttribute(name);
            a.Value = value ?? "";
            e.Attributes.Append(a);
        }

        private static void AppendAttrIfAny(XmlDocument doc, XmlElement e, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            AppendAttr(doc, e, name, value);
        }

        /// <summary>按名称查一条资料（忽略大小写与首尾空格）。</summary>
        public MaterialItem FindByName(string name)
        {
            string key = (name ?? "").Trim();
            if (key.Length == 0) return null;
            foreach (MaterialItem it in Items)
            {
                if (string.Equals((it.Name ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return it;
            }
            return null;
        }
    }

    /// <summary>
    /// 某个标签页的视图列表：只显示满足 match 的主库条目（同一对象实例，改单元格即改主库）。
    /// 在视图里新增一行 → 先按 init 补默认值，再同步 Add 进主库；删除同理回写主库。
    /// 这样每个标签页都能加材料，保存时写的是主库这份唯一数据。
    /// </summary>
    internal sealed class MaterialViewList : BindingList<MaterialItem>
    {
        private readonly BindingList<MaterialItem> _master;
        private readonly Func<MaterialItem, bool> _match;
        private readonly Action<MaterialItem> _init;
        private bool _syncing;

        public MaterialViewList(BindingList<MaterialItem> master,
                                Func<MaterialItem, bool> match,
                                Action<MaterialItem> init)
        {
            _master = master;
            _match = match;
            _init = init;

            _syncing = true;
            foreach (MaterialItem it in master)
            {
                if (match(it)) Add(it);
            }
            _syncing = false;

            _master.ListChanged += OnMasterChanged;
        }

        protected override void InsertItem(int index, MaterialItem item)
        {
            if (!_syncing)
            {
                if (_init != null) _init(item);
                _syncing = true;
                try
                {
                    if (!_master.Contains(item)) _master.Add(item);
                }
                finally { _syncing = false; }
            }
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            MaterialItem item = this[index];
            base.RemoveItem(index);

            if (!_syncing)
            {
                _syncing = true;
                try { if (_master.Contains(item)) _master.Remove(item); }
                finally { _syncing = false; }
            }
        }

        private void OnMasterChanged(object sender, ListChangedEventArgs e)
        {
            if (_syncing) return;

            _syncing = true;
            try
            {
                if (e.ListChangedType == ListChangedType.ItemAdded)
                {
                    MaterialItem it = _master[e.NewIndex];
                    if (_match(it) && !Contains(it)) Add(it);
                }
                else if (e.ListChangedType == ListChangedType.ItemDeleted)
                {
                    // 主库已删除，视图里对应对象若还在则摘掉（找不到也不算错）
                    for (int i = Count - 1; i >= 0; i--)
                    {
                        if (!_master.Contains(this[i])) RemoveAt(i);
                    }
                }
            }
            finally { _syncing = false; }
        }
    }
}
