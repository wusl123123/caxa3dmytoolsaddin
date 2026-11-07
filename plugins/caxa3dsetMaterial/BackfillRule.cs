using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace caxa3dsetMaterial
{
    /// <summary>
    /// 材料属性回填规则：读取 caxa3dsetMaterialaddfix.txt 中被双引号包裹的字段名。
    /// 铁律（用户要求）：
    ///  - 只回填双引号内列出的字段，**严格区分大小写**；
    ///  - 不兜底：规则为空/文件缺失时不回退到内置字段，也不写另一张属性表；
    ///  - 不组合：一个字段就是一个属性名，不派生、不拼接其它字段。
    /// 规则文件改动后无需重启插件：每次读取按最后写入时间自动重载。
    /// </summary>
    internal static class BackfillRule
    {
        public const string FileName = "caxa3dsetMaterialaddfix.txt";

        /// <summary>文件缺失且无播种文件时使用的最小模板（与项目内规则文件一致）。</summary>
        private const string DefaultText =
            "#规则说明,抓取\"\"之间的字段将材料名称回填到对应字段#\r\n" +
            "\"材料\"\r\n" +
            "\"SC材料\"\r\n";

        /// <summary>只匹配非空的双引号内容；空的 "" 不会被当成字段。</summary>
        private static readonly Regex Quoted = new Regex("\"([^\"]+)\"", RegexOptions.Compiled);

        private static readonly object Sync = new object();
        private static List<string> _fields = new List<string>();
        private static DateTime _stamp = DateTime.MinValue;
        private static string _loadedPath;
        private static string _error;

        /// <summary>用户可编辑的规则文件（%APPDATA%\caxa3dsetMaterial\，无管理员权限也能保存）。</summary>
        public static string FilePath
        {
            get { return Path.Combine(MaterialDb.DataDir, FileName); }
        }

        /// <summary>播种位置：DLL 所在目录（部署脚本会拷贝一份模板过去）。</summary>
        private static string SeedPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(BackfillRule).Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    string p = Path.Combine(dir, FileName);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>确保用户规则文件存在（首次从程序目录播种），返回其路径。</summary>
        public static string EnsureFile()
        {
            string path = FilePath;
            if (File.Exists(path)) return path;

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string seed = SeedPath();
            if (seed != null) File.Copy(seed, path, false);
            else File.WriteAllText(path, DefaultText, new UTF8Encoding(false));
            return path;
        }

        /// <summary>当前生效的字段（严格区分大小写，顺序=文件中的顺序）。</summary>
        public static IList<string> Fields
        {
            get { lock (Sync) { ReloadIfChanged(); return _fields; } }
        }

        public static int Count
        {
            get { lock (Sync) { ReloadIfChanged(); return _fields.Count; } }
        }

        public static string FieldsText
        {
            get { lock (Sync) { ReloadIfChanged(); return _fields.Count == 0 ? "<空>" : string.Join("、", _fields.ToArray()); } }
        }

        // 字段归属（2026-10-29 调整）：所有字段统一写入共享表，链接实例共享同一份属性；
        // 不再按 "SC" 前缀分流到零件表。

        /// <summary>用系统默认编辑器打开规则文件（不存在则按模板创建），返回路径。</summary>
        public static string OpenForEdit()
        {
            string path = EnsureFile();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return path;
        }

        private static void ReloadIfChanged()
        {
            string path;
            DateTime stamp;
            try
            {
                path = EnsureFile();
                stamp = File.GetLastWriteTimeUtc(path);
                if (_loadedPath == path && stamp == _stamp) return;
            }
            catch (Exception ex)
            {
                ReportError(ex);
                return;
            }

            try
            {
                List<string> fields = Parse(ReadText(path));
                _fields = fields;
                _loadedPath = path;
                _stamp = stamp;
                _error = null;
                Diagnostics.Trace("回填规则已加载（" + fields.Count + " 个字段，读的是 " + path + "）：" +
                                  string.Join("、", fields.ToArray()));
                if (fields.Count == 0)
                    Diagnostics.Log("回填规则为空（" + path + "）：按规则不兜底，不写任何自定义属性。");
                else WarnIfSeedDiffers(path);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        private static void ReportError(Exception ex)
        {
            if (_error != ex.Message) Diagnostics.Log("读取回填规则失败：" + ex.Message);
            _fields = new List<string>();
            _error = ex.Message;
        }

        /// <summary>
        /// 程序目录那份只是首次播种用的模板，之后插件只认运行期这一份；两边不一致时提示一句，
        /// 免得改了模板以为已生效（本项目的运行期文件在 %APPDATA%\caxa3dsetMaterial\）。
        /// </summary>
        private static void WarnIfSeedDiffers(string runtimePath)
        {
            try
            {
                string seed = SeedPath();
                if (seed == null || string.Equals(seed, runtimePath, StringComparison.OrdinalIgnoreCase)) return;

                byte[] a = File.ReadAllBytes(runtimePath);
                byte[] b = File.ReadAllBytes(seed);
                if (a.Length == b.Length)
                {
                    bool same = true;
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (a[i] != b[i]) { same = false; break; }
                    }
                    if (same) return;
                }

                Diagnostics.Log("注意：程序目录的规则模板与运行期文件内容不一致，生效的是运行期那份（" +
                                runtimePath + "）；改模板不会生效。");
            }
            catch { }
        }

        /// <summary>解析：跳过空行与 # 开头的整行注释，取每个双引号内的非空文本。</summary>
        private static List<string> Parse(string text)
        {
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            List<string> fields = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                foreach (Match m in Quoted.Matches(line))
                {
                    string name = m.Groups[1].Value.Trim();
                    if (name.Length == 0) continue;
                    if (seen.Add(name)) fields.Add(name); // Ordinal：大小写不同视为不同字段
                }
            }
            return fields;
        }

        /// <summary>读文件：优先按 UTF-8（无 BOM 也能读），非法 UTF-8 再按系统 ANSI 解码。</summary>
        private static string ReadText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(bytes);
            }
        }
    }
}
