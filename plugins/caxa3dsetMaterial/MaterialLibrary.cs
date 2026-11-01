using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace caxa3dsetMaterial
{
    /// <summary>
    /// 材料库 XML 扫描：确认安装目录 materials\Materials 下的库文件、分类数、材料数，
    /// 并统计重名材料（SetMaterialName 按名称定位时会歧义）。
    /// </summary>
    internal static class MaterialLibrary
    {
        /// <summary>定位材料库目录：优先按安装目录推导，最后回退到固定路径。</summary>
        public static string ResolveMaterialsDir(string installFolder)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(installFolder))
            {
                candidates.Add(Path.Combine(installFolder, @"AppData\zh-cn\materials\Materials"));
                string parent = Path.GetDirectoryName(installFolder.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent))
                    candidates.Add(Path.Combine(parent, @"AppData\zh-cn\materials\Materials"));
            }
            candidates.Add(@"C:\Program Files\CAXA\CAXA 3D\2027\AppData\zh-cn\materials\Materials");

            foreach (string c in candidates)
            {
                if (Directory.Exists(c)) return c;
            }
            return null;
        }

        /// <summary>扫描并生成报告文本。</summary>
        public static string Scan(string dir)
        {
            StringBuilder sb = new StringBuilder();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                sb.AppendLine("材料库目录不存在：" + (dir ?? "<未定位>"));
                return sb.ToString();
            }

            sb.AppendLine("材料库目录：" + dir);
            string[] files = Directory.GetFiles(dir, "*.xml");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            Dictionary<string, List<string>> nameIndex =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int totalMaterials = 0;
            int totalClasses = 0;

            foreach (string file in files)
            {
                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(file);

                    XmlElement root = doc.DocumentElement;
                    string libType = root != null ? root.GetAttribute("Type") : "<无根节点>";
                    XmlNodeList classes = doc.SelectNodes("/MaterialLib/classification");
                    XmlNodeList materials = doc.SelectNodes("//material");

                    totalClasses += classes.Count;
                    totalMaterials += materials.Count;
                    sb.AppendFormat("  {0}\r\n      Type=\"{1}\"  分类={2}  材料={3}",
                        Path.GetFileName(file), libType, classes.Count, materials.Count);

                    foreach (XmlNode m in materials)
                    {
                        string name = m.Attributes != null && m.Attributes["name"] != null
                            ? m.Attributes["name"].Value : "<无名>";
                        List<string> where;
                        if (!nameIndex.TryGetValue(name, out where))
                        {
                            where = new List<string>();
                            nameIndex[name] = where;
                        }
                        where.Add(Path.GetFileName(file));
                    }
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendFormat("  {0}  解析失败：{1}\r\n", Path.GetFileName(file), ex.Message);
                }
            }

            sb.AppendFormat("合计：{0} 个库文件，{1} 个分类，{2} 条材料。\r\n",
                files.Length, totalClasses, totalMaterials);

            List<string> dup = new List<string>();
            foreach (KeyValuePair<string, List<string>> kv in nameIndex)
            {
                if (kv.Value.Count > 1) dup.Add(kv.Key);
            }
            if (dup.Count > 0)
            {
                dup.Sort(StringComparer.OrdinalIgnoreCase);
                sb.AppendFormat("重名材料 {0} 个（按名称写入会有歧义，建议用 GUID 或“名称+分类”定位）：\r\n  {1}\r\n",
                    dup.Count, string.Join("、", dup.ToArray()));
            }
            else
            {
                sb.AppendLine("没有重名材料。");
            }
            return sb.ToString();
        }
    }
}
