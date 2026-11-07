// 用户可编辑规则文件的统一定位与热重载（规则 14.8：属性/字段名一律由用户可编辑的规则文件驱动）。
// 生效路径口径（本工程所有规则文件共用）：
//   ① %APPDATA%\caxa3dBatchMaster\<文件名> 存在 → 就用它（用户可写，不需要管理员）；
//   ② 只有程序目录（CAXA 插件目录）有 → **复制到 ①** 后用 ①，并在日志说明复制来源；
//      不直接回落到程序目录：部署目录在 Program Files 下，用户改不动，改了也会被下次安装覆盖。
//   ③ 两处都没有 → 按内置种子内容在 ① 创建。
// 每次取值前比较 LastWriteTime，变了就重载（热重载）；日志必须打印「生效路径＋条数」。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace caxa3dBatchMaster
{
    internal static class UserRuleFiles
    {
        public const string ProjectName = "caxa3dBatchMaster";

        /// <summary>%APPDATA%\caxa3dBatchMaster（不存在则创建；失败返回 null）。</summary>
        public static string AppDataDirectory
        {
            get
            {
                try
                {
                    string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    if (string.IsNullOrEmpty(root)) return null;
                    string dir = Path.Combine(root, ProjectName);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    return dir;
                }
                catch { return null; }
            }
        }

        public static string AssemblyDirectory
        {
            get
            {
                try
                {
                    return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                        ?? AppContext.BaseDirectory;
                }
                catch { return AppContext.BaseDirectory; }
            }
        }

        /// <summary>解析出生效路径（必要时播种/上拷）；note 是给日志看的一句话说明。</summary>
        public static string Ensure(string fileName, string seedContent, out string note)
        {
            string appData = AppDataDirectory;
            string appPath = string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, fileName);
            string progPath = Path.Combine(AssemblyDirectory, fileName);

            if (appPath != null && File.Exists(appPath))
            {
                note = "生效路径＝" + appPath + "（用户可编辑位置）";
                return appPath;
            }
            if (File.Exists(progPath))
            {
                if (appPath != null)
                {
                    try
                    {
                        File.Copy(progPath, appPath, true);
                        note = "已从程序目录上拷到可编辑位置：" + progPath + " → " + appPath + "，生效路径＝" + appPath;
                        return appPath;
                    }
                    catch (Exception ex)
                    {
                        note = "上拷失败（" + ex.Message + "），改用程序目录：" + progPath + "（该位置可能需要管理员权限才能编辑）";
                        return progPath;
                    }
                }
                note = "无法定位 %APPDATA% 目录，改用程序目录：" + progPath;
                return progPath;
            }
            if (appPath != null)
            {
                try
                {
                    File.WriteAllText(appPath, seedContent ?? "", new UTF8Encoding(true));
                    note = "两处都没有，已按内置默认创建：" + appPath;
                    return appPath;
                }
                catch (Exception ex) { note = "创建规则文件失败：" + ex.Message + "｜生效路径＝<不可得>"; return null; }
            }
            note = "无法定位 %APPDATA% 目录且程序目录无该文件｜生效路径＝<不可得>";
            return null;
        }

        /// <summary>用系统关联程序打开规则文件（返回 false 时给出原因，调用方写日志）。</summary>
        public static bool Open(string path, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(path)) { error = "生效路径不可得"; return false; }
            if (!File.Exists(path)) { error = "文件不存在：" + path; return false; }
            try
            {
                Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }

    /// <summary>
    /// 「表格列显示名 → CAXA 自定义属性内部字段名」映射（规则 14.8）。
    /// 文件：caxa3dBatchMaster内部字段映射.txt，一行一条 `显示名=内部字段名`，# 或 // 开头是注释。
    /// 表头名按**不区分大小写**匹配（显示名不是属性名，宽松匹配只影响查表，不影响写进 CAXA 的那串字符）；
    /// 命中后用**映射值原样**去共享表查/写（规则 14.1/14.2「精确同名、不拼候选名」对内部字段名仍然成立）。
    /// 未命中的列＝表头名原样当内部字段名用（保持老行为，不猜、不兜底）。
    /// 用户指定（11-06 第 0 条）：重量列的内部字段＝PartMass。
    /// 冲突留痕：官方 `CAXADraft\Config\zh-CN\IC_BOM_DIC.xml` 把 PartMass 的描述写作「质量」，
    /// 用户定义写作「重量」⇒ 判定类规则以用户定义为准，此处按「重量=PartMass」播种，「质量」列不自动映射。
    /// </summary>
    internal static class PropFieldMap
    {
        public const string FileName = "caxa3dBatchMaster内部字段映射.txt";

        private const string Seed =
"# caxa3dBatchMaster 内部字段映射：每行一条「表格列显示名=CAXA 自定义属性内部字段名」\r\n" +
"# 以 # 或 // 开头的行是注释；空行忽略；等号两侧空格自动去掉。\r\n" +
"# 生效位置：%APPDATA%\\caxa3dBatchMaster\\ ；改完保存即生效（按文件修改时间热重载，不必重启 CAXA）。\r\n" +
"# 没在这里出现的列＝直接拿列显示名当内部字段名用。\r\n" +
"# 注意：内部字段名区分大小写，请照 CAXA 属性对话框/TLB 里的写法抄，不要猜。\r\n" +
"重量=PartMass\r\n" +
"# 日期列内部字段名待实机确认：先按几种常见写法都试一遍（命中即停），跑完看 trace 里的 ExportSelfBOM.labels 再收敛\r\n" +
"日期=日期,Date,PartDate,CreateDate,ModifyDate,Created Date\r\n";

        private static Dictionary<string, string> _map;
        private static string _path;
        private static DateTime _stamp = DateTime.MinValue;
        private static int _count;
        private static string _note = "";

        public static string EffectivePath => _path;
        public static int Count => _count;
        public static string LastNote => _note;

        /// <summary>首次或文件变更时重载；返回一句要进日志的说明（未变更返回 null）。</summary>
        public static string ReloadIfChanged(bool force = false)
        {
            string known = _path;
            DateTime oldStamp = _stamp;
            DateTime stamp = DateTime.MinValue;
            if (known != null && File.Exists(known))
            {
                try { stamp = File.GetLastWriteTime(known); } catch { stamp = DateTime.MinValue; }
            }
            if (!force && _map != null && stamp != DateTime.MinValue && stamp == oldStamp) return null;

            string note;
            string path = UserRuleFiles.Ensure(FileName, Seed, out note);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int bad = 0;
            try
            {
                if (path != null && File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)
                            || line.StartsWith("//", StringComparison.Ordinal)) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0 || eq == line.Length - 1) { bad++; continue; }
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        if (key.Length == 0 || val.Length == 0) { bad++; continue; }
                        map[key] = val;
                    }
                }
            }
            catch (Exception ex)
            {
                note = "读取映射文件失败：" + ex.Message + "｜" + note;
                map.Clear();
            }

            DateTime newStamp = DateTime.MinValue;
            if (path != null && File.Exists(path))
            {
                try { newStamp = File.GetLastWriteTime(path); } catch { newStamp = DateTime.MinValue; }
            }
            bool changed = _map == null || map.Count != _count || newStamp != oldStamp || path != known;
            _map = map; _count = map.Count; _path = path; _stamp = newStamp; _note = note;
            string text = "内部字段映射：" + note + "｜生效条数 " + _count
                + (bad > 0 ? "｜格式不合被跳过 " + bad + " 行" : "");
            return force || changed ? text : null;
        }

        /// <summary>启动/首次使用时调一次，返回给日志的说明。</summary>
        public static string InitAndDescribe()
        {
            string text = ReloadIfChanged(true);
            return text ?? ("内部字段映射：生效路径＝" + SafePath() + "｜生效条数 " + _count);
        }

        public static string SafePath()
        {
            try { ReloadIfChanged(); } catch { }
            return _path ?? "<不可得>";
        }

        /// <summary>显示名 → **候选内部字段名列表**（11-06 15:43 用户在映射文件里写成「重量=PartMassType,PartMass,质量」后新增）。
        /// 分隔符支持半/全角逗号与分号；去空、去重、**保持书写顺序**；没配或没分隔符＝单元素（原样用列名）。
        /// 读侧按顺序试候选、命中即停；写侧只用第一个候选（见 <see cref="Resolve"/>）。</summary>
        public static string[] Candidates(string header)
        {
            string key = (header ?? "").Trim();
            if (key.Length == 0) return new[] { "" };
            string raw;
            try { ReloadIfChanged(); } catch { }
            var map = _map;
            raw = (map != null && map.TryGetValue(key, out var v)) ? v : key;
            string[] parts = raw.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<string>(parts.Length);
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (t.Length == 0) continue;
                bool dup = false;
                foreach (string q in list) if (string.Equals(q, t, StringComparison.Ordinal)) { dup = true; break; }
                if (!dup) list.Add(t);
            }
            return list.Count > 0 ? list.ToArray() : new[] { key };
        }

        /// <summary>显示名 → 内部字段名（取**第一个候选**）；没配就是原样（不猜、不兜底）。
        /// ★ 映射值含逗号时绝不能把整串「A,B,C」当字段名用——那是必然读不到的假字段（11-06 16:5x 实测用户的写法后定这条）。</summary>
        public static string Resolve(string header)
        {
            string[] c = Candidates(header);
            return c[0];
        }

        /// <summary>映射后的内部字段名写进日志/trace 时用这个，避免「重量读不到」看不出查的是 PartMass。</summary>
        public static string Describe(string header)
        {
            string key = (header ?? "").Trim();
            string[] c = Candidates(key);
            bool same = c.Length == 1 && string.Equals(c[0], key, StringComparison.Ordinal);
            return same ? key : key + "(内部字段=" + string.Join("→", c) + ")";
        }

        public static bool OpenEffectiveFile(out string error)
        {
            try { ReloadIfChanged(); } catch { }
            return UserRuleFiles.Open(_path, out error);
        }

        /// <summary>调试/自检用：当前生效映射的副本（键序不保证）。</summary>
        public static Dictionary<string, string> Snapshot()
        {
            try { ReloadIfChanged(); } catch { }
            return new Dictionary<string, string>(_map ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        }
    }
}
