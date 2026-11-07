// 图纸环境「个人技术要求」库（NSpecLib.xml）的定位、按年份分组、备份与还原。
//
// 背景（已实测，勿凭记忆改）：
//   · CAXA 安装目录下的 NSpecLib.xml 是**共享/内置库**（Program Files\CAXA\CAXA 3D\<年>\bin\CAXADraft\Data\zh-CN），
//     只含官方分类，**不含**用户在「我的技术要求」里自建的表格。
//   · 用户自建部分落在**每用户**副本：
//       %LOCALAPPDATA%\CAXA\CAXA CAD CXIC-INT <年> (x64)\<主版本.次版本>\zh-CN\Data\NSpecLib.xml
//     其中 <CLASSIFICATION name="个人技术要求表"> 即「我的技术要求 → 个人技术要求表」。
//   · 一台机器可能装了多个年份（2024 / 2027 …），每个年份各有自己的一份个人库，
//     因此必须**按年份分组、按年份备份、按年份还原**，不能只挑"最高分"那一份。
//
// 铁律：路径一律**反查**，不得硬编码用户名 / 年份 / 版本号。
//   反查链：环境变量 → 家族目录(CAXA CAD* / CAXA 3D*) → 版本目录(数字) → 语言目录(zh-CN 等) → Data\NSpecLib.xml
//   年份：① 家族目录名命中注册表反查出的已安装年份 → ② 家族目录名里的 20xx → ③ 记为「年份未知」
//
// 判成功只看副作用：备份后回读 zip 内每个条目比对 SHA256；还原后回读文件比对长度 + SHA256。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace caxa3dexbTechSpecsbak
{
    /// <summary>一份个人技术要求库副本（按年份归属）。</summary>
    internal sealed class SpecItem
    {
        public string FilePath;
        public string Root;                 // %LOCALAPPDATA%\CAXA 或 %APPDATA%\CAXA
        public string Family;               // CAXA CAD CXIC-INT 2027 (x64)
        public string Version;              // 27.0
        public string Lang;                 // zh-CN
        public string Year;                 // 2027；无法判定时为空串
        public bool Exists;
        public long Length;
        public DateTime LastWrite = DateTime.MinValue;
        public int Score;
        public bool Active;                 // 同年份里打分最高的那一份（当前生效）

        public string YearLabel { get { return string.IsNullOrEmpty(Year) ? "年份未知" : Year; } }

        public string Key
        {
            get { return Year + "|" + Family + "|" + Version + "|" + Lang; }
        }

        /// <summary>勾选列表框里的一行。</summary>
        public string Display
        {
            get
            {
                return YearLabel + "  |  " + Family + "  |  " + Version +
                       (string.IsNullOrEmpty(Lang) ? "" : "  |  " + Lang) +
                       "  |  " + SpecLibrary.FormatSize(Length) +
                       "  |  " + (LastWrite == DateTime.MinValue ? "时间未知" : LastWrite.ToString("yyyy-MM-dd HH:mm")) +
                       (Active ? "  |  当前生效" : "");
            }
        }
    }

    /// <summary>备份包内的一条记录（= 一个年份下的一份库）。</summary>
    internal sealed class PackEntry
    {
        public string Archive;              // libs/2027/1/NSpecLib.xml
        public string Year;
        public string Family;
        public string Version;
        public string Lang;
        public string Source;               // 备份时原路径
        public string Sha256;
        public long Length;

        public string YearLabel { get { return string.IsNullOrEmpty(Year) ? "年份未知" : Year; } }

        public string Key
        {
            get { return Year + "|" + Family + "|" + Version + "|" + Lang; }
        }

        public string Display
        {
            get
            {
                return YearLabel + "  |  " + Family + "  |  " + Version +
                       (string.IsNullOrEmpty(Lang) ? "" : "  |  " + Lang) +
                       "  |  " + SpecLibrary.FormatSize(Length);
            }
        }
    }

    /// <summary>单条还原结果。</summary>
    internal sealed class RestoreOutcome
    {
        public PackEntry Entry;
        public string TargetPath;
        public bool Ok;
        public bool Skipped;
        public bool Created;
        public bool PathChanged;
        public string SafetyBackupPath;
        public string Detail;
    }

    internal static class SpecLibrary
    {
        internal const string FileName = "NSpecLib.xml";
        internal const string ToolId = "caxa3dexbTechSpecsbak";
        internal const string ManifestName = "manifest.txt";

        private static readonly Regex FamilyPattern = new Regex(@"^CAXA\s+(CAD|3D)", RegexOptions.IgnoreCase);
        private static readonly Regex VersionPattern = new Regex(@"^\d+(\.\d+)*$");
        private static readonly Regex YearPattern = new Regex(@"^20\d{2}$");

        // ------------------------------------------------------------ 反查

        private static string[] GetRoots()
        {
            return new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAXA"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CAXA")
            };
        }

        /// <summary>
        /// 反查已安装的 CAXA 3D 年份（如 2024 / 2027）。
        /// 来源一：注册表 HKLM/HKCU\SOFTWARE\CAXA\CAXA 3D\&lt;ver&gt;\InstallDir → 末级目录名。
        /// 来源二：InstallDir 的父目录下逐个子目录（防注册表只有一个版本条目）。
        /// 来源三：Program Files\CAXA\CAXA 3D 下的年份目录（防注册表整体缺失）。
        /// </summary>
        internal static List<string> GetInstalledYears(Action<string> log)
        {
            var years = new List<string>();

            foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string sk in new[] { @"SOFTWARE\CAXA\CAXA 3D", @"SOFTWARE\WOW6432Node\CAXA\CAXA 3D" })
                {
                    try
                    {
                        using (RegistryKey k = hive.OpenSubKey(sk))
                        {
                            if (k == null) continue;

                            foreach (string name in k.GetSubKeyNames())
                            {
                                try
                                {
                                    using (RegistryKey vk = k.OpenSubKey(name))
                                    {
                                        if (vk == null) continue;
                                        string dir = vk.GetValue("InstallDir") as string;
                                        if (string.IsNullOrEmpty(dir)) continue;

                                        dir = dir.TrimEnd('\\', '/');
                                        string year = Path.GetFileName(dir);
                                        if (YearPattern.IsMatch(year)) AddYear(years, year, log, "注册表 InstallDir");

                                        try
                                        {
                                            string parent = Path.GetDirectoryName(dir);
                                            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                                            {
                                                foreach (string sub in Directory.GetDirectories(parent))
                                                {
                                                    string n = Path.GetFileName(sub);
                                                    if (YearPattern.IsMatch(n)) AddYear(years, n, log, "安装根同级目录");
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch (Exception ex) { log("读取注册表 " + name + " 失败：" + ex.Message); }
                            }
                        }
                    }
                    catch (Exception ex) { log("打开注册表 " + sk + " 失败（" + ex.GetType().Name + "）"); }
                }
            }

            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string baseDir = Path.Combine(pf, "CAXA", "CAXA 3D");
                if (Directory.Exists(baseDir))
                {
                    foreach (string sub in Directory.GetDirectories(baseDir))
                    {
                        string n = Path.GetFileName(sub);
                        if (YearPattern.IsMatch(n)) AddYear(years, n, log, "Program Files 目录");
                    }
                }
            }
            catch (Exception ex) { log("扫描 Program Files 失败：" + ex.Message); }

            if (years.Count == 0) log("提示：未反查到已安装的 CAXA 3D 年份，年份将按目录名推断");
            return years;
        }

        private static void AddYear(List<string> years, string year, Action<string> log, string from)
        {
            if (years.Contains(year)) return;
            years.Add(year);
            log("反查到已安装版本年份：" + year + "（来源：" + from + "）");
        }

        /// <summary>
        /// 扫描所有已存在的个人技术要求库副本，按年份归属并打分。
        /// 返回列表按「年份倒序 → 分数倒序」排列，同一年份里 Active=true 的是当前生效那份。
        /// </summary>
        internal static List<SpecItem> Scan(Action<string> log, List<string> installedYears)
        {
            var list = new List<SpecItem>();

            foreach (string root in GetRoots())
            {
                if (!Directory.Exists(root)) continue;

                string[] famDirs;
                try { famDirs = Directory.GetDirectories(root); }
                catch (Exception ex) { log("枚举 " + root + " 失败：" + ex.Message); continue; }

                foreach (string famDir in famDirs)
                {
                    string fam = Path.GetFileName(famDir);
                    if (!FamilyPattern.IsMatch(fam)) continue;

                    string[] verDirs;
                    try { verDirs = Directory.GetDirectories(famDir); }
                    catch { continue; }

                    foreach (string verDir in verDirs)
                    {
                        string ver = Path.GetFileName(verDir);
                        if (!VersionPattern.IsMatch(ver)) continue;

                        try
                        {
                            foreach (string langDir in Directory.GetDirectories(verDir))
                            {
                                string lang = Path.GetFileName(langDir);
                                string dataDir = Path.Combine(langDir, "Data");
                                if (!Directory.Exists(dataDir)) continue;
                                SpecItem t = Probe(Path.Combine(dataDir, FileName), root, fam, ver, lang, installedYears);
                                if (t != null) list.Add(t);
                            }
                        }
                        catch { }

                        SpecItem t2 = Probe(Path.Combine(verDir, "Data", FileName), root, fam, ver, "", installedYears);
                        if (t2 != null) list.Add(t2);
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<SpecItem>();
            foreach (SpecItem t in list)
            {
                if (seen.Add(t.FilePath)) unique.Add(t);
            }

            // 同年份里标记 Active（分数最高，同分取最新）
            var byYear = new Dictionary<string, List<SpecItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (SpecItem t in unique)
            {
                if (!byYear.ContainsKey(t.YearLabel)) byYear[t.YearLabel] = new List<SpecItem>();
                byYear[t.YearLabel].Add(t);
            }
            foreach (KeyValuePair<string, List<SpecItem>> kv in byYear)
            {
                SpecItem best = null;
                foreach (SpecItem t in kv.Value)
                {
                    if (best == null || t.Score > best.Score ||
                        (t.Score == best.Score && t.LastWrite > best.LastWrite)) best = t;
                }
                if (best != null) best.Active = true;
            }

            unique.Sort(delegate(SpecItem a, SpecItem b)
            {
                int c = string.Compare(b.Year, a.Year, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = b.Score.CompareTo(a.Score);
                if (c != 0) return c;
                return b.LastWrite.CompareTo(a.LastWrite);
            });
            return unique;
        }

        /// <summary>把年份串映射成 2000+ 的年份（2027 → 2027，27 → 2027）。</summary>
        internal static string NormalizeYear(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            raw = raw.Trim();
            if (YearPattern.IsMatch(raw)) return raw;
            if (raw.Length == 2 && Regex.IsMatch(raw, @"^\d{2}$")) return "20" + raw;
            Match m = Regex.Match(raw, @"(20\d{2})");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static SpecItem Probe(string path, string root, string family, string version,
                                      string lang, List<string> installedYears)
        {
            if (!File.Exists(path)) return null;

            var t = new SpecItem
            {
                FilePath = path,
                Root = root,
                Family = family,
                Version = version,
                Lang = lang,
                Exists = true
            };

            t.Year = YearOf(family, installedYears);

            try
            {
                var fi = new FileInfo(path);
                t.Length = fi.Length;
                t.LastWrite = fi.LastWriteTime;
            }
            catch { }

            t.Score = ScoreOf(t);
            return t;
        }

        /// <summary>年份判定：先认已安装年份（家族名里出现即为该年），再取家族名里的 20xx。</summary>
        private static string YearOf(string family, List<string> installedYears)
        {
            foreach (string y in installedYears)
            {
                if (family.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0) return y;
            }
            Match m = Regex.Match(family, @"20\d{2}");
            return m.Success ? m.Value : "";
        }

        /// <summary>
        /// 打分只用于"同一年份内哪份生效"和排序：年份已定 +100；版本主号与年份后两位一致 +50；
        /// 位于 %LOCALAPPDATA%（个人库真实落点）+10。
        /// </summary>
        private static int ScoreOf(SpecItem t)
        {
            int score = 0;
            if (!string.IsNullOrEmpty(t.Year))
            {
                score += 100;
                if (t.Version.Split('.')[0] == t.Year.Substring(2)) score += 50;
            }
            if (t.Root.IndexOf("Local", StringComparison.OrdinalIgnoreCase) >= 0) score += 10;
            return score;
        }

        // ------------------------------------------------------------ 备份

        /// <summary>
        /// 按年份打包：libs/&lt;年份&gt;/&lt;序号&gt;/NSpecLib.xml + manifest.txt。
        /// 返回实际写入的条目数。
        /// </summary>
        internal static int Backup(List<SpecItem> items, string destZip, Action<string> log)
        {
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("未勾选任何年份");

            if (File.Exists(destZip)) File.Delete(destZip);

            var valid = new List<SpecItem>();
            foreach (SpecItem t in items)
            {
                if (t == null) continue;
                if (!File.Exists(t.FilePath))
                {
                    log("跳过（文件已不存在）：" + t.FilePath);
                    continue;
                }
                valid.Add(t);
            }
            if (valid.Count == 0) throw new InvalidOperationException("勾选的年份对应的文件都不存在了");

            var indexInYear = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var manifest = new StringBuilder();
            manifest.Append("tool=").Append(ToolId).Append("\r\n");
            manifest.Append("format=2\r\n");
            manifest.Append("created=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");
            manifest.Append("machine=").Append(Environment.MachineName).Append("\r\n");
            manifest.Append("user=").Append(Environment.UserName).Append("\r\n");
            manifest.Append("count=").Append(valid.Count).Append("\r\n");

            var written = new List<PackEntry>();

            using (var fs = new FileStream(destZip, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                int seq = 0;
                foreach (SpecItem t in valid)
                {
                    string yk = t.YearLabel;
                    int n = indexInYear.ContainsKey(yk) ? indexInYear[yk] + 1 : 1;
                    indexInYear[yk] = n;

                    string archive = "libs/" + SafeSeg(yk) + "/" + n + "/" + FileName;
                    byte[] data = File.ReadAllBytes(t.FilePath);
                    string sha = Sha256(data);

                    ZipArchiveEntry e = zip.CreateEntry(archive, CompressionLevel.Optimal);
                    using (Stream s = e.Open()) s.Write(data, 0, data.Length);

                    seq++;
                    manifest.Append("\r\n[").Append(seq).Append("]\r\n");
                    manifest.Append("archive=").Append(archive).Append("\r\n");
                    manifest.Append("year=").Append(t.YearLabel).Append("\r\n");
                    manifest.Append("family=").Append(t.Family).Append("\r\n");
                    manifest.Append("ver=").Append(t.Version).Append("\r\n");
                    manifest.Append("lang=").Append(t.Lang).Append("\r\n");
                    manifest.Append("source=").Append(t.FilePath).Append("\r\n");
                    manifest.Append("length=").Append(data.Length).Append("\r\n");
                    manifest.Append("sha256=").Append(sha).Append("\r\n");

                    var pe = new PackEntry
                    {
                        Archive = archive, Year = t.YearLabel, Family = t.Family, Version = t.Version,
                        Lang = t.Lang, Source = t.FilePath, Length = data.Length, Sha256 = sha
                    };
                    written.Add(pe);

                    log("已写入 [" + t.YearLabel + "] " + t.FilePath + "（" + data.Length + " B）");
                    Diagnostics.Log("backup add: year=" + t.YearLabel + " src=" + t.FilePath + " bytes=" + data.Length);
                }

                byte[] mb = new UTF8Encoding(false).GetBytes(manifest.ToString());
                ZipArchiveEntry me = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                using (Stream s = me.Open()) s.Write(mb, 0, mb.Length);
            }

            // 判成功只看副作用：回读每个条目，字节数与 SHA256 必须与写入时一致
            int bad = 0;
            using (var fs = File.OpenRead(destZip))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                foreach (PackEntry pe in written)
                {
                    ZipArchiveEntry e = zip.GetEntry(pe.Archive);
                    if (e == null) { log("校验失败：包内缺少 " + pe.Archive); bad++; continue; }
                    byte[] back;
                    using (Stream s = e.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        back = ms.ToArray();
                    }
                    if (back.Length != pe.Length || !string.Equals(Sha256(back), pe.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        log("校验失败：[" + pe.YearLabel + "] 回读内容不一致（" + back.Length + " B）");
                        bad++;
                    }
                }
            }

            var fi = new FileInfo(destZip);
            if (bad > 0)
            {
                log("错误：" + bad + " 个条目校验未通过，备份包不可信");
                Diagnostics.Log("backup verify FAIL: " + destZip + " bad=" + bad);
                return 0;
            }

            log("备份包已生成：" + destZip + "（" + FormatSize(fi.Length) + "）");
            log("共 " + written.Count + " 个年份库：" + JoinYears(written));
            Diagnostics.Log("backup ok: " + destZip + " entries=" + written.Count);
            return written.Count;
        }

        // ------------------------------------------------------------ 还原

        /// <summary>读取备份包清单（不落盘）。</summary>
        internal static List<PackEntry> PeekPackage(string zipPath, Action<string> log)
        {
            Dictionary<string, string> head;
            List<Dictionary<string, string>> sections;
            ParseManifest(zipPath, out head, out sections);

            string tool;
            head.TryGetValue("tool", out tool);
            if (!string.Equals(tool, ToolId, StringComparison.Ordinal))
                throw new InvalidDataException("备份包来源不是本程序（manifest.tool=" + (tool ?? "空") + "）");

            var list = new List<PackEntry>();

            if (sections.Count > 0)
            {
                foreach (Dictionary<string, string> s in sections)
                {
                    var e = new PackEntry
                    {
                        Archive = Get(s, "archive"),
                        Year = NormalizeYear(Get(s, "year")),
                        Family = Get(s, "family"),
                        Version = Get(s, "ver"),
                        Lang = Get(s, "lang"),
                        Source = Get(s, "source"),
                        Sha256 = Get(s, "sha256")
                    };
                    long len;
                    long.TryParse(Get(s, "length"), out len);
                    e.Length = len;

                    if (string.IsNullOrEmpty(e.Archive)) continue;
                    if (!PackageHasEntry(zipPath, e.Archive)) continue;

                    // 老包或手改包：年份缺失时从 source 路径里再捞一次
                    if (string.IsNullOrEmpty(e.Year)) e.Year = NormalizeYear(FromPathYear(e.Source));

                    list.Add(e);
                }
            }
            else if (PackageHasEntry(zipPath, FileName))
            {
                // 兼容 format=1（单文件包）：根目录 NSpecLib.xml
                var e = new PackEntry
                {
                    Archive = FileName,
                    Year = NormalizeYear(Get(head, "year")),
                    Family = Get(head, "family"),
                    Version = Get(head, "version"),
                    Lang = Get(head, "lang"),
                    Source = Get(head, "source"),
                    Sha256 = Get(head, "sha256")
                };
                long len;
                long.TryParse(Get(head, "length"), out len);
                e.Length = len;
                if (string.IsNullOrEmpty(e.Year)) e.Year = NormalizeYear(FromPathYear(e.Source));
                log("检测到 format=1（单文件）备份包，按单条目处理");
                list.Add(e);
            }

            if (list.Count == 0) throw new InvalidDataException("备份包里没有可还原的 " + FileName);
            return list;
        }

        /// <summary>读取备份包的制作时间（仅用于界面显示）。</summary>
        internal static string PeekCreated(string zipPath)
        {
            try
            {
                Dictionary<string, string> head;
                List<Dictionary<string, string>> sections;
                ParseManifest(zipPath, out head, out sections);
                string c = Get(head, "created");
                return string.IsNullOrEmpty(c) ? "未知" : c;
            }
            catch { return "未知"; }
        }

        /// <summary>
        /// 按年份还原：对每条包内记录先算出本机落点（PlanTarget），再写入并回读校验（WriteOne）。
        /// </summary>
        internal static List<RestoreOutcome> Restore(string zipPath, List<PackEntry> entries, Action<string> log)
        {
            var outcomes = new List<RestoreOutcome>();
            if (entries == null || entries.Count == 0) throw new InvalidOperationException("未选择要还原的年份");

            List<string> installedYears = GetInstalledYears(log);
            List<SpecItem> locals = Scan(log, installedYears);

            foreach (PackEntry e in entries)
            {
                SpecItem target = PlanTarget(e, locals, installedYears);
                if (target == null)
                {
                    var skip = new RestoreOutcome
                    {
                        Entry = e,
                        Skipped = true,
                        Detail = "本机没有 " + e.YearLabel + " 的图纸环境目录，跳过"
                    };
                    log("跳过 [" + e.YearLabel + "] " + skip.Detail);
                    outcomes.Add(skip);
                    continue;
                }
                outcomes.Add(WriteOne(zipPath, e, target, log));
            }

            return outcomes;
        }

        /// <summary>
        /// 算出这条包记录在本机的落点（纯计算，不写盘）；返回 null = 本机没有该年份的目录。
        /// 优先同 Key（年+家族+版本+语言）→ 同家族同年份 → 同年份最高分；都没有再按目录结构推演。
        /// </summary>
        internal static SpecItem PlanTarget(PackEntry e, List<SpecItem> locals, List<string> installedYears)
        {
            SpecItem t = MatchLocal(e, locals);
            if (t == null) t = ExpectedTarget(e, installedYears);
            return t;
        }

        /// <summary>
        /// 落盘一条记录：校验包内容 → 目标已存在则先 .bak 安全备份 → 写入 → 回读长度 + SHA256 比对。
        /// 安全备份失败即中止，绝不覆盖在服文件。
        /// </summary>
        internal static RestoreOutcome WriteOne(string zipPath, PackEntry e, SpecItem target, Action<string> log)
        {
            var oc = new RestoreOutcome { Entry = e, TargetPath = target.FilePath };

            try
            {
                byte[] data = ReadEntryBytes(zipPath, e.Archive);
                if (data == null) throw new InvalidDataException("包内缺少 " + e.Archive);

                string text = new UTF8Encoding(false).GetString(data)
                    .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                if (text.IndexOf("<SPECLIB", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException("不是有效的技术要求库（缺 <SPECLIB> 根节点）");

                if (!string.IsNullOrEmpty(e.Sha256) &&
                    !string.Equals(Sha256(data), e.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("包内内容与 manifest 记录的 SHA256 不一致，包可能已损坏");

                if (!string.IsNullOrEmpty(e.Source) &&
                    !string.Equals(Normalize(e.Source), Normalize(target.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    oc.PathChanged = true;
                    log("[" + e.YearLabel + "] 本机位置与备份时不同，还原到：" + target.FilePath);
                }

                string dir = Path.GetDirectoryName(target.FilePath);
                bool existed = File.Exists(target.FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (existed)
                {
                    string bak = target.FilePath + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try
                    {
                        File.Copy(target.FilePath, bak, true);
                        oc.SafetyBackupPath = bak;
                        log("[" + e.YearLabel + "] 当前文件已另存为：" + bak);
                    }
                    catch (Exception ex)
                    {
                        throw new IOException("还原前安全备份失败，已中止（未覆盖任何文件）：" + ex.Message, ex);
                    }
                }
                else
                {
                    oc.Created = true;
                    log("[" + e.YearLabel + "] 本机无该文件，将新建：" + target.FilePath);
                }

                File.WriteAllBytes(target.FilePath, data);

                byte[] back = File.ReadAllBytes(target.FilePath);
                if (back.Length != data.Length ||
                    !string.Equals(Sha256(back), Sha256(data), StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("还原校验失败：回读 " + back.Length + " B（期望 " + data.Length +
                                          " B），SHA256 不一致");
                }

                oc.Ok = true;
                log("[" + e.YearLabel + "] 还原完成：" + target.FilePath + "（" + data.Length + " B，SHA256 一致）");
                Diagnostics.Log("restore ok: year=" + e.YearLabel + " -> " + target.FilePath);
            }
            catch (Exception ex)
            {
                oc.Ok = false;
                oc.Detail = ex.Message;
                log("[" + e.YearLabel + "] 还原失败：" + ex.Message);
                Diagnostics.Log("restore FAIL: year=" + e.YearLabel + " : " + ex.Message);
            }

            return oc;
        }

        /// <summary>本机匹配：同 Key → 同家族 → 同年份（同年份取分数最高那份）。</summary>
        private static SpecItem MatchLocal(PackEntry e, List<SpecItem> locals)
        {
            foreach (SpecItem t in locals)
                if (string.Equals(t.Key, e.Key, StringComparison.OrdinalIgnoreCase)) return t;

            foreach (SpecItem t in locals)
                if (string.Equals(t.Family, e.Family, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.Year, e.Year, StringComparison.OrdinalIgnoreCase)) return PickBest(locals, e.Year);

            return PickBest(locals, e.Year);
        }

        private static SpecItem PickBest(List<SpecItem> locals, string year)
        {
            if (string.IsNullOrEmpty(year)) return null;
            SpecItem best = null;
            foreach (SpecItem t in locals)
            {
                if (!string.Equals(t.Year, year, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || t.Score > best.Score) best = t;
            }
            return best;
        }

        /// <summary>本机没有该年份的文件时，按家族目录 + 版本目录推演一个可创建的位置。</summary>
        private static SpecItem ExpectedTarget(PackEntry e, List<string> installedYears)
        {
            if (string.IsNullOrEmpty(e.Year)) return null;

            string major = e.Year.Substring(2);
            foreach (string root in GetRoots())
            {
                if (!Directory.Exists(root)) continue;

                string[] fams;
                try { fams = Directory.GetDirectories(root); }
                catch { continue; }

                foreach (string famDir in fams)
                {
                    string fam = Path.GetFileName(famDir);
                    if (!FamilyPattern.IsMatch(fam)) continue;
                    if (fam.IndexOf(e.Year, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string[] vers;
                    try { vers = Directory.GetDirectories(famDir); }
                    catch { continue; }

                    foreach (string verDir in vers)
                    {
                        string ver = Path.GetFileName(verDir);
                        if (!VersionPattern.IsMatch(ver)) continue;
                        if (!string.Equals(ver, e.Version, StringComparison.OrdinalIgnoreCase) &&
                            ver.Split('.')[0] != major) continue;

                        string lang = e.Lang;
                        if (string.IsNullOrEmpty(lang) || !Directory.Exists(Path.Combine(verDir, lang)))
                        {
                            lang = "zh-CN";
                            try
                            {
                                foreach (string d in Directory.GetDirectories(verDir))
                                {
                                    string n = Path.GetFileName(d);
                                    if (Directory.Exists(Path.Combine(d, "Data"))) { lang = n; break; }
                                }
                            }
                            catch { }
                        }

                        return new SpecItem
                        {
                            FilePath = Path.Combine(verDir, lang, "Data", FileName),
                            Root = root, Family = fam, Version = ver, Lang = lang, Year = e.Year,
                            Exists = false, Score = 0
                        };
                    }
                }
            }
            return null;
        }

        // ------------------------------------------------------------ zip / manifest / 杂项

        private static void ParseManifest(string zipPath, out Dictionary<string, string> head,
                                          out List<Dictionary<string, string>> sections)
        {
            head = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sections = new List<Dictionary<string, string>>();

            byte[] raw = ReadEntryBytes(zipPath, ManifestName);
            if (raw == null) throw new InvalidDataException("备份包内没有 " + ManifestName + "，不是本程序生成的备份");

            string text = new UTF8Encoding(false).GetString(raw);
            Dictionary<string, string> current = head;

            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = line.Trim();
                if (s.Length == 0) continue;
                if (s.StartsWith("[", StringComparison.Ordinal))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections.Add(current);
                    continue;
                }
                int i = s.IndexOf('=');
                if (i <= 0) continue;
                current[s.Substring(0, i).Trim()] = s.Substring(i + 1).Trim();
            }
        }

        private static string FromPathYear(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            Match m = Regex.Match(path, @"(20\d{2})");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static bool PackageHasEntry(string zipPath, string entryName)
        {
            try
            {
                using (var fs = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false, Encoding.UTF8))
                {
                    return zip.GetEntry(entryName) != null;
                }
            }
            catch { return false; }
        }

        private static byte[] ReadEntryBytes(string zipPath, string entryName)
        {
            using (var fs = File.OpenRead(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                ZipArchiveEntry e = zip.GetEntry(entryName);
                if (e == null) return null;
                using (Stream s = e.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        private static string Get(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : "";
        }

        private static string SafeSeg(string s)
        {
            if (string.IsNullOrEmpty(s)) s = "unknown";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static string Normalize(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
            catch { return (p ?? "").TrimEnd('\\', '/'); }
        }

        private static string JoinYears(List<PackEntry> list)
        {
            var seen = new List<string>();
            foreach (PackEntry e in list) if (!seen.Contains(e.YearLabel)) seen.Add(e.YearLabel);
            return string.Join("、", seen.ToArray());
        }

        internal static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }
    }
}
