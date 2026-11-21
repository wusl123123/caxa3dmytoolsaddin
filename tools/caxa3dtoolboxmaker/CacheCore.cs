using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Caxa3dToolboxMaker
{
    // ============ .cache 类目级索引读写（ToolBox 目录树的族身份键登记表） ============
    // 实测格式（11-21，官方 Hexagon headed bolts\.cache，4856 B / 24 条 + 本机 29.0 版）：
    //   <?xml version="1.0" encoding="UTF-8" standalone="yes"?>\r\n
    //   <Root>\r\n\t<Version version="1">\r\n\t</Version><FolderList>\r\n
    //   \t\t<Folder name="…" display_name="…" type="TB_PART" GUID="{…}" protected="TRUE">\r\n
    //   \t\t</Folder><Folder name="…" …>\r\n\t\t</Folder>……</FolderList></Root>\r\n
    //   —— UTF-8 无 BOM + CRLF + \t 缩进；Folder 成对标签，相邻条目间无分隔串。
    // 族 XML 的 CurNodeInfo@GUID 与 .cache 的 Folder@GUID 逐字一致（双处登记）。
    // 11-21 起开放编辑：Raw + 结构片段全保留，字符串级定点重建，未编辑时逐字节 round-trip 不变。
    public static class CacheCore
    {
        public class Entry
        {
            public List<Attr> Attrs = new List<Attr>(); // 保原始属性顺序（含未知键，不丢字段）
        }

        public class CacheDoc
        {
            public string Raw;                          // 原始文本（结构定位基准）
            public string Version;                      // <Version version="…">，读不到为 null（第三态）
            public List<Entry> Entries = new List<Entry>();
            public List<string> KeysUnion = new List<string>(); // 属性名并集，按首次出现保序（= 表格列序）
            // —— 以下为保格式重建片段（从原文捕获，不硬编码） ——
            public string Prefix;                       // 开头 → 首个 <Folder（含其前缩进）
            public string Inner;                        // Folder 开标签 > → </Folder> 之间（实测 "\r\n\t\t"）
            public string FolderSep;                    // 相邻 </Folder> 与 <Folder 之间（实测空串）
            public string Suffix;                       // 末个 </Folder> → 文件尾（"</FolderList></Root>\r\n"）
        }

        static readonly Regex FolderOpenRx = new Regex(@"<Folder\b[^\s>]*", RegexOptions.Compiled);
        static readonly Regex FolderCloseRx = new Regex(@"</Folder>", RegexOptions.Compiled);

        public static CacheDoc Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException(".cache 文件不存在", path);
            string text = File.ReadAllText(path, Encoding.UTF8); // 按 BOM 探测读；按 XML 声明编码解码中文
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1); // 防御：去掉可能残留的 BOM，写回统一无 BOM

            // XmlDocument 只做合法性校验 + 实体解码（坏样本根非 Root 在此被拒），不用于写回
            XmlDocument xd = new XmlDocument();
            xd.LoadXml(text);
            XmlElement root = xd.DocumentElement;
            if (root == null || root.Name != "Root")
                throw new InvalidDataException("不是 ToolBox .cache 索引（根节点不是 Root）：" +
                    (root == null ? "<无根节点>" : root.Name));

            CacheDoc d = new CacheDoc();
            d.Raw = text;
            XmlNode ver = root.SelectSingleNode("Version");
            if (ver != null && ver.Attributes != null)
            {
                XmlAttribute va = ver.Attributes["version"];
                if (va != null) d.Version = va.Value;
            }

            XmlNodeList folders = root.SelectNodes("FolderList/Folder");
            if (folders != null)
            {
                foreach (XmlElement f in folders)
                {
                    Entry e = new Entry();
                    foreach (XmlAttribute a in f.Attributes)
                    {
                        e.Attrs.Add(new Attr(a.Name, a.Value));
                        if (!d.KeysUnion.Contains(a.Name)) d.KeysUnion.Add(a.Name);
                    }
                    d.Entries.Add(e);
                }
            }
            CaptureStructure(d, text);
            return d;
        }

        // 从原文捕获保格式片段；空 FolderList（0 条）时给一套与官方一致的默认片段
        static void CaptureStructure(CacheDoc d, string text)
        {
            Match first = FolderOpenRx.Match(text);
            if (first.Success)
            {
                MatchCollection closes = FolderCloseRx.Matches(text, first.Index);
                if (closes.Count == 0) throw new InvalidDataException(".cache 结构异常：有 <Folder> 无 </Folder>");
                int tagEnd = text.IndexOf('>', first.Index);
                Match last = closes[closes.Count - 1];
                d.Prefix = text.Substring(0, first.Index);
                d.Inner = text.Substring(tagEnd + 1, closes[0].Index - tagEnd - 1);
                d.Suffix = text.Substring(last.Index + last.Length);
                d.FolderSep = closes.Count > 1 ? text.Substring(closes[0].Index + closes[0].Length,
                    FolderOpenRx.Match(text, closes[0].Index + closes[0].Length).Index -
                    (closes[0].Index + closes[0].Length)) : "";
            }
            else
            {
                int fl = text.IndexOf("</FolderList>", StringComparison.Ordinal);
                if (fl < 0) throw new InvalidDataException("不是 ToolBox .cache 索引（无 FolderList）");
                d.Prefix = text.Substring(0, fl);
                d.Inner = "\r\n\t\t";
                d.FolderSep = "";
                d.Suffix = text.Substring(fl);
            }
        }

        static string Encode(string v)
        {
            if (v == null) return "";
            return v.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        static string BuildFolderTag(List<Attr> attrs)
        {
            StringBuilder sb = new StringBuilder("<Folder");
            foreach (Attr a in attrs)
                sb.Append(' ').Append(a.Key).Append("=\"").Append(Encode(a.Value)).Append('"');
            sb.Append('>');
            return sb.ToString();
        }

        // 字符串级重建：未编辑时 Prefix + 原属性 + 原 Inner/Sep/Suffix = 逐字节一致
        public static string Render(CacheDoc d)
        {
            if (d.Prefix == null) throw new InvalidDataException(".cache 未加载或结构片段缺失");
            StringBuilder sb = new StringBuilder(d.Prefix);
            for (int i = 0; i < d.Entries.Count; i++)
            {
                if (i > 0) sb.Append(d.FolderSep ?? "");
                sb.Append(BuildFolderTag(d.Entries[i].Attrs));
                sb.Append(d.Inner ?? "\r\n\t\t");
                sb.Append("</Folder>");
            }
            sb.Append(d.Suffix ?? "</FolderList></Root>\r\n");
            return sb.ToString();
        }

        // 保格式写盘：写前备份到 backupDir（时间戳），UTF-8 无 BOM 落盘
        public static void Save(string path, CacheDoc d, string backupDir)
        {
            string text = Render(d);
            if (!string.IsNullOrEmpty(backupDir) && File.Exists(path))
            {
                Directory.CreateDirectory(backupDir);
                string bak = Path.Combine(backupDir, Path.GetFileName(path) + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                File.Copy(path, bak, true);
            }
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }
}
