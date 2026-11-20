// CAXA 3D 用户自定义设计库（ToolBox 目录）的定位、按版本分组、目录级备份与还原。
//
// 背景（2026-11-16 本机实测）：
//   用户自定义设计库是一个【目录】而非单文件，真实落点：
//       %LOCALAPPDATA%\CAXA\CAXA 3D\<版本号>\<语言>\ToolBox
//     本机实例：C:\Users\Administrator\AppData\Local\CAXA\CAXA 3D\29.0\zh-CN\ToolBox
//       （用户给的 ADMINI~1 即 Administrator 的 8.3 短名 = %LOCALAPPDATA%）
//     内含子目录（如「默认\Hex bolts GB_T5783-2016」）与隐藏文件 .cache，必须整目录打包。
//   ★ 版本号（29.0）不是年份（2027）：3D 的版本号体系与图纸内核年份不同，
//     本库按【版本号目录】分组，扫描时发现几个就备份几个，绝不写死 29.0。
//
// 铁律：路径一律反查，不得硬编码用户名 / 版本号 / 语言 / 盘符。
//   反查链：环境变量 LOCALAPPDATA/APPDATA → 家族目录(CAXA 3D*) → 版本目录(数字) → 语言目录 → ToolBox
//
// 判成功只看副作用：备份后回读 zip 内每个文件比对 SHA256；还原后回读落盘文件比对长度 + SHA256。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace caxa3dToolboxBackup
{
    /// <summary>一份本机 ToolBox 目录（按版本号归属）。</summary>
    internal sealed class ToolItem
    {
        public string DirPath;              // ...\CAXA 3D\29.0\zh-CN\ToolBox
        public string Root;                 // %LOCALAPPDATA%\CAXA 或 %APPDATA%\CAXA
        public string Family;               // CAXA 3D
        public string Version;              // 29.0
        public string Lang;                 // zh-CN
        public int FileCount;
        public long TotalBytes;
        public DateTime LastWrite = DateTime.MinValue;
        public int Score;
        public bool Active;                 // 同版本里打分最高的那份

        public string VersionLabel { get { return string.IsNullOrEmpty(Version) ? "版本未知" : Version; } }

        public string Key
        {
            get { return Version + "|" + Family + "|" + Lang; }
        }

        public string Display
        {
            get
            {
                return VersionLabel + "  |  " + Family + "  |  " + Lang +
                       "  |  " + FileCount + " 文件 / " + ToolboxLibrary.FormatSize(TotalBytes) +
                       "  |  " + (LastWrite == DateTime.MinValue ? "时间未知" : LastWrite.ToString("yyyy-MM-dd HH:mm")) +
                       (Active ? "  |  当前生效" : "");
            }
        }
    }

    /// <summary>备份包内的一条文件记录。</summary>
    internal sealed class PackFile
    {
        public string Rel;                  // 相对 ToolBox 根的路径，用 / 分隔
        public long Length;
        public string Sha256;
    }

    /// <summary>备份包内的一条记录（= 一个版本下的整份 ToolBox 目录）。</summary>
    internal sealed class PackEntry
    {
        public string ArchivePrefix;        // toolboxes/29.0/1/
        public string Version;
        public string Family;
        public string Lang;
        public string Source;               // 备份时的原目录路径
        public int FileCount;
        public long TotalBytes;
        public List<PackFile> Files = new List<PackFile>();

        public string VersionLabel { get { return string.IsNullOrEmpty(Version) ? "版本未知" : Version; } }

        public string Key
        {
            get { return Version + "|" + Family + "|" + Lang; }
        }

        public string Display
        {
            get
            {
                return VersionLabel + "  |  " + Family + "  |  " + Lang +
                       "  |  " + FileCount + " 文件 / " + ToolboxLibrary.FormatSize(TotalBytes);
            }
        }
    }

    /// <summary>单条还原结果。</summary>
    internal sealed class RestoreOutcome
    {
        public PackEntry Entry;
        public string TargetDir;
        public bool Ok;
        public bool Skipped;
        public bool Created;
        public bool PathChanged;
        public string SafetyBackupPath;
        public string Detail;
    }

    internal static class ToolboxLibrary
    {
        internal const string DirName = "ToolBox";
        internal const string ToolId = "caxa3dToolboxBackup";
        internal const string ManifestName = "manifest.txt";

        private static readonly Regex FamilyPattern = new Regex(@"^CAXA\s+3D", RegexOptions.IgnoreCase);
        private static readonly Regex VersionPattern = new Regex(@"^\d+(\.\d+)*$");

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
        /// 反查已安装的 CAXA 3D 版本号（如 29.0）——仅用于日志与打分辅助。
        /// 来源：注册表 HKLM/HKCU\SOFTWARE\CAXA\CAXA 3D\&lt;ver&gt;\InstallDir 的子键名，
        /// 以及 <ProgramFiles>\CAXA\CAXA 3D 下各版本目录；找不到也不影响主流程（主流程靠目录结构扫描）。
        /// </summary>
        internal static List<string> GetInstalledVersions(Action<string> log)
        {
            var vers = new List<string>();

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
                                if (VersionPattern.IsMatch(name)) AddVersion(vers, name, log, "注册表版本键");
                            }
                        }
                    }
                    catch (Exception ex) { log("读取注册表 " + sk + " 失败（" + ex.GetType().Name + "）"); }
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
                        if (VersionPattern.IsMatch(n)) AddVersion(vers, n, log, "Program Files 目录");
                    }
                }
            }
            catch (Exception ex) { log("扫描 Program Files 失败：" + ex.Message); }

            if (vers.Count == 0) log("提示：未从注册表反查到版本号，版本将按目录名识别（不影响备份）");
            return vers;
        }

        private static void AddVersion(List<string> vers, string ver, Action<string> log, string from)
        {
            if (vers.Contains(ver)) return;
            vers.Add(ver);
            log("反查到已安装版本号：" + ver + "（来源：" + from + "）");
        }

        /// <summary>
        /// 扫描所有已存在的 ToolBox 目录，按版本号归属并打分。
        /// 返回列表按「版本号倒序 → 分数倒序」排列，同版本里 Active=true 的是当前生效那份。
        /// </summary>
        internal static List<ToolItem> Scan(Action<string> log)
        {
            var list = new List<ToolItem>();

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

                        // 常见布局：<ver>\<lang>\ToolBox
                        try
                        {
                            foreach (string langDir in Directory.GetDirectories(verDir))
                            {
                                string lang = Path.GetFileName(langDir);
                                Probe(Path.Combine(langDir, DirName), root, fam, ver, lang, list, log);
                            }
                        }
                        catch { }

                        // 兼容布局：<ver>\ToolBox（语言串为空）
                        Probe(Path.Combine(verDir, DirName), root, fam, ver, "", list, log);
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<ToolItem>();
            foreach (ToolItem t in list)
            {
                if (seen.Add(t.DirPath)) unique.Add(t);
            }

            // 同版本里标记 Active
            var byVer = new Dictionary<string, List<ToolItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (ToolItem t in unique)
            {
                if (!byVer.ContainsKey(t.VersionLabel)) byVer[t.VersionLabel] = new List<ToolItem>();
                byVer[t.VersionLabel].Add(t);
            }
            foreach (KeyValuePair<string, List<ToolItem>> kv in byVer)
            {
                ToolItem best = null;
                foreach (ToolItem t in kv.Value)
                {
                    if (best == null || t.Score > best.Score ||
                        (t.Score == best.Score && t.LastWrite > best.LastWrite)) best = t;
                }
                if (best != null) best.Active = true;
            }

            unique.Sort(delegate(ToolItem a, ToolItem b)
            {
                int c = CompareVersion(b.Version, a.Version);
                if (c != 0) return c;
                c = b.Score.CompareTo(a.Score);
                if (c != 0) return c;
                return b.LastWrite.CompareTo(a.LastWrite);
            });
            return unique;
        }

        /// <summary>版本号倒序比较：29.0 &gt; 26.0 &gt; 9.0，逐段按数值比。</summary>
        private static int CompareVersion(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
            if (string.IsNullOrEmpty(a)) return -1;
            if (string.IsNullOrEmpty(b)) return 1;
            string[] pa = a.Split('.'), pb = b.Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int ia = i < pa.Length ? ParseInt(pa[i]) : 0;
                int ib = i < pb.Length ? ParseInt(pb[i]) : 0;
                if (ia != ib) return ia.CompareTo(ib);
            }
            return 0;
        }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }

        private static void Probe(string dir, string root, string family, string version,
                                  string lang, List<ToolItem> list, Action<string> log)
        {
            if (!Directory.Exists(dir)) return;

            var t = new ToolItem
            {
                DirPath = dir,
                Root = root,
                Family = family,
                Version = version,
                Lang = lang
            };

            try
            {
                string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                t.FileCount = files.Length;
                long sum = 0;
                DateTime last = DateTime.MinValue;
                foreach (string f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        sum += fi.Length;
                        if (fi.LastWriteTime > last) last = fi.LastWriteTime;
                    }
                    catch { }
                }
                t.TotalBytes = sum;
                t.LastWrite = last;
            }
            catch (Exception ex) { log("统计 " + dir + " 失败：" + ex.Message); }

            t.Score = ScoreOf(t);
            list.Add(t);
            log("发现 ToolBox：" + dir + "（" + t.FileCount + " 文件 / " + FormatSize(t.TotalBytes) + "）");
        }

        private static int ScoreOf(ToolItem t)
        {
            int score = 0;
            if (!string.IsNullOrEmpty(t.Version)) score += 100;
            if (t.Root.IndexOf("Local", StringComparison.OrdinalIgnoreCase) >= 0) score += 10;
            return score;
        }

        // ------------------------------------------------------------ 备份

        /// <summary>
        /// 按版本打包：toolboxes/&lt;版本&gt;/&lt;序号&gt;/files/&lt;相对路径&gt; + manifest.txt。
        /// 返回实际写入的版本条目数。
        /// </summary>
        internal static int Backup(List<ToolItem> items, string destZip, Action<string> log)
        {
            if (items == null || items.Count == 0)
                throw new InvalidOperationException("未勾选任何版本");

            if (File.Exists(destZip)) File.Delete(destZip);

            var valid = new List<ToolItem>();
            foreach (ToolItem t in items)
            {
                if (t == null) continue;
                if (!Directory.Exists(t.DirPath))
                {
                    log("跳过（目录已不存在）：" + t.DirPath);
                    continue;
                }
                valid.Add(t);
            }
            if (valid.Count == 0) throw new InvalidOperationException("勾选的版本对应的 ToolBox 目录都不存在了");

            var manifest = new StringBuilder();
            manifest.Append("tool=").Append(ToolId).Append("\r\n");
            manifest.Append("format=1\r\n");
            manifest.Append("created=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");
            manifest.Append("machine=").Append(Environment.MachineName).Append("\r\n");
            manifest.Append("user=").Append(Environment.UserName).Append("\r\n");
            manifest.Append("count=").Append(valid.Count).Append("\r\n");

            var written = new List<PackEntry>();
            int seq = 0;

            using (var fs = new FileStream(destZip, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                foreach (ToolItem t in valid)
                {
                    seq++;
                    string prefix = "toolboxes/" + SafeSeg(t.VersionLabel) + "/" + seq + "/";
                    var pe = new PackEntry
                    {
                        ArchivePrefix = prefix,
                        Version = t.Version,
                        Family = t.Family,
                        Lang = t.Lang,
                        Source = t.DirPath
                    };

                    string[] files;
                    try { files = Directory.GetFiles(t.DirPath, "*", SearchOption.AllDirectories); }
                    catch (Exception ex) { log("跳过（无法枚举）：" + t.DirPath + "：" + ex.Message); continue; }

                    foreach (string f in files)
                    {
                        byte[] data;
                        try { data = File.ReadAllBytes(f); }
                        catch (Exception ex) { log("跳过（读取失败）：" + f + "：" + ex.Message); continue; }

                        string rel = f.Substring(t.DirPath.Length).TrimStart('\\', '/').Replace('\\', '/');
                        string zipName = prefix + "files/" + rel;
                        string sha = Sha256(data);

                        ZipArchiveEntry e = zip.CreateEntry(zipName, CompressionLevel.Optimal);
                        using (Stream s = e.Open()) s.Write(data, 0, data.Length);

                        pe.Files.Add(new PackFile { Rel = rel, Length = data.Length, Sha256 = sha });
                        pe.TotalBytes += data.Length;
                    }

                    pe.FileCount = pe.Files.Count;
                    if (pe.FileCount == 0)
                    {
                        log("跳过（目录为空或无可读文件）：" + t.DirPath);
                        continue;
                    }

                    manifest.Append("\r\n[").Append(seq).Append("]\r\n");
                    manifest.Append("archive=").Append(prefix).Append("\r\n");
                    manifest.Append("version=").Append(t.VersionLabel).Append("\r\n");
                    manifest.Append("family=").Append(t.Family).Append("\r\n");
                    manifest.Append("lang=").Append(t.Lang).Append("\r\n");
                    manifest.Append("source=").Append(t.DirPath).Append("\r\n");
                    manifest.Append("fileCount=").Append(pe.FileCount).Append("\r\n");
                    manifest.Append("totalBytes=").Append(pe.TotalBytes).Append("\r\n");
                    foreach (PackFile pf in pe.Files)
                    {
                        manifest.Append("file=").Append(pf.Rel).Append("|").Append(pf.Length)
                                .Append("|").Append(pf.Sha256).Append("\r\n");
                    }

                    written.Add(pe);
                    log("已写入 [" + t.VersionLabel + "] " + t.DirPath +
                        "（" + pe.FileCount + " 文件 / " + FormatSize(pe.TotalBytes) + "）");
                    Diagnostics.Log("backup add: ver=" + t.VersionLabel + " src=" + t.DirPath +
                                    " files=" + pe.FileCount);
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
                    foreach (PackFile pf in pe.Files)
                    {
                        string zipName = pe.ArchivePrefix + "files/" + pf.Rel;
                        ZipArchiveEntry e = zip.GetEntry(zipName);
                        if (e == null) { log("校验失败：包内缺少 " + zipName); bad++; continue; }
                        byte[] back = ReadEntry(e);
                        if (back.Length != pf.Length ||
                            !string.Equals(Sha256(back), pf.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            log("校验失败：[" + pe.VersionLabel + "] " + pf.Rel + " 回读内容不一致");
                            bad++;
                        }
                    }
                }
            }

            var fi = new FileInfo(destZip);
            if (bad > 0)
            {
                log("错误：" + bad + " 个文件校验未通过，备份包不可信");
                Diagnostics.Log("backup verify FAIL: " + destZip + " bad=" + bad);
                return 0;
            }

            log("备份包已生成：" + destZip + "（" + FormatSize(fi.Length) + "）");
            log("共 " + written.Count + " 个版本：" + JoinVersions(written));
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
            if (sections.Count == 0) throw new InvalidDataException("备份包里没有任何版本条目");

            foreach (Dictionary<string, string> s in sections)
            {
                var e = new PackEntry
                {
                    ArchivePrefix = Get(s, "archive"),
                    Version = Get(s, "version"),
                    Family = Get(s, "family"),
                    Lang = Get(s, "lang"),
                    Source = Get(s, "source")
                };
                int fc; int.TryParse(Get(s, "fileCount"), out fc);
                e.FileCount = fc;
                long tb; long.TryParse(Get(s, "totalBytes"), out tb);
                e.TotalBytes = tb;

                string fileBlob;
                if (s.TryGetValue("file", out fileBlob) && !string.IsNullOrEmpty(fileBlob))
                {
                    // ParseManifest 把重复的 file= 键以 \n 连接保留（见 MultiValueDict）
                    foreach (string line in fileBlob.Split('\n'))
                    {
                        string[] parts = line.Split('|');
                        if (parts.Length < 3) continue;
                        long len; long.TryParse(parts[1], out len);
                        e.Files.Add(new PackFile { Rel = parts[0], Length = len, Sha256 = parts[2] });
                    }
                }

                if (string.IsNullOrEmpty(e.ArchivePrefix)) continue;
                list.Add(e);
            }

            if (list.Count == 0) throw new InvalidDataException("备份包里没有可还原的版本条目");
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

        /// <summary>按版本还原：每条先算本机落点（PlanTarget），再整目录安全备份 + 写入 + 回读校验（WriteOne）。</summary>
        internal static List<RestoreOutcome> Restore(string zipPath, List<PackEntry> entries, Action<string> log)
        {
            var outcomes = new List<RestoreOutcome>();
            if (entries == null || entries.Count == 0) throw new InvalidOperationException("未选择要还原的版本");

            List<ToolItem> locals = Scan(log);
            foreach (PackEntry e in entries)
            {
                ToolItem target = PlanTarget(e, locals);
                if (target == null)
                {
                    var skip = new RestoreOutcome
                    {
                        Entry = e,
                        Skipped = true,
                        Detail = "本机没有版本 " + e.VersionLabel + " 的 CAXA 3D 目录，跳过（未写入任何文件）"
                    };
                    log("跳过 [" + e.VersionLabel + "] " + skip.Detail);
                    outcomes.Add(skip);
                    continue;
                }
                outcomes.Add(WriteOne(zipPath, e, target, log));
            }
            return outcomes;
        }

        /// <summary>
        /// 算这条包记录在本机的落点（纯计算，不写盘）：同 Key → 同家族 → 同版本最高分 → 目录推演。
        /// </summary>
        internal static ToolItem PlanTarget(PackEntry e, List<ToolItem> locals)
        {
            foreach (ToolItem t in locals)
                if (string.Equals(t.Key, e.Key, StringComparison.OrdinalIgnoreCase)) return t;

            foreach (ToolItem t in locals)
                if (string.Equals(t.Family, e.Family, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.Version, e.Version, StringComparison.OrdinalIgnoreCase)) return t;

            foreach (ToolItem t in locals)
                if (string.Equals(t.Version, e.Version, StringComparison.OrdinalIgnoreCase)) return t;

            return ExpectedTarget(e);
        }

        /// <summary>本机连版本目录都没有时，按家族 + 版本 + 语言推演一个可创建的位置；目录不存在则返回 null。</summary>
        private static ToolItem ExpectedTarget(PackEntry e)
        {
            if (string.IsNullOrEmpty(e.Version)) return null;
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
                    string verDir = Path.Combine(famDir, e.Version);
                    if (!Directory.Exists(verDir)) continue;
                    string lang = e.Lang;
                    if (string.IsNullOrEmpty(lang) || !Directory.Exists(Path.Combine(verDir, lang)))
                    {
                        lang = "zh-CN";
                        try
                        {
                            foreach (string d in Directory.GetDirectories(verDir))
                            {
                                if (Directory.Exists(d) && Path.GetFileName(d) != DirName) { lang = Path.GetFileName(d); break; }
                            }
                        }
                        catch { }
                    }
                    return new ToolItem
                    {
                        DirPath = Path.Combine(verDir, lang, DirName),
                        Root = root, Family = fam, Version = e.Version, Lang = lang,
                        FileCount = 0, TotalBytes = 0, Score = 0
                    };
                }
            }
            return null;
        }

        /// <summary>
        /// 落盘一条版本：先校验包内所有文件 SHA256 → 整目录 .bak 安全备份 → 写入临时目录并回读 → 替换为正式目录。
        /// 安全备份失败即中止，绝不覆盖在服数据；写失败则回滚到 .bak。
        /// </summary>
        internal static RestoreOutcome WriteOne(string zipPath, PackEntry e, ToolItem target, Action<string> log)
        {
            var oc = new RestoreOutcome { Entry = e, TargetDir = target.DirPath };
            string tmpDir = null;
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");

            try
            {
                // 1) 先在内存里校验包内所有文件，绝不边解压边写盘
                var payload = new List<KeyValuePair<string, byte[]>>();
                using (var fs = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false, Encoding.UTF8))
                {
                    foreach (PackFile pf in e.Files)
                    {
                        string zipName = e.ArchivePrefix + "files/" + pf.Rel;
                        ZipArchiveEntry ze = zip.GetEntry(zipName);
                        if (ze == null) throw new InvalidDataException("包内缺少 " + zipName);
                        byte[] data = ReadEntry(ze);
                        if (data.Length != pf.Length ||
                            !string.Equals(Sha256(data), pf.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("包内内容与 manifest 的 SHA256 不一致：" + pf.Rel);
                        payload.Add(new KeyValuePair<string, byte[]>(pf.Rel, data));
                    }
                }

                // 2) 路径变化提示
                if (!string.IsNullOrEmpty(e.Source) &&
                    !string.Equals(Normalize(e.Source), Normalize(target.DirPath), StringComparison.OrdinalIgnoreCase))
                {
                    oc.PathChanged = true;
                    log("[" + e.VersionLabel + "] 本机位置与备份时不同，还原到：" + target.DirPath);
                }

                // 3) 整目录安全备份：重命名现有 ToolBox（不复制，快且完整）
                string parent = Path.GetDirectoryName(target.DirPath);
                bool existed = Directory.Exists(target.DirPath);
                if (existed && !Directory.Exists(parent))
                    throw new IOException("目标父目录不存在：" + parent);

                if (existed)
                {
                    string bak = target.DirPath + ".bak-" + stamp;
                    int dup = 1;
                    while (Directory.Exists(bak)) bak = target.DirPath + ".bak-" + stamp + "-" + (dup++);
                    try
                    {
                        Directory.Move(target.DirPath, bak);
                        oc.SafetyBackupPath = bak;
                        log("[" + e.VersionLabel + "] 当前 ToolBox 已另存为：" + bak);
                    }
                    catch (Exception ex)
                    {
                        throw new IOException("还原前安全备份失败，已中止（未覆盖任何文件）：" + ex.Message, ex);
                    }
                }
                else
                {
                    oc.Created = true;
                    log("[" + e.VersionLabel + "] 本机无该目录，将新建：" + target.DirPath);
                }

                // 4) 先写临时目录（正式目录的兄弟目录），全部回读通过再改名为正式目录
                tmpDir = target.DirPath + ".tmp-" + stamp;
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                Directory.CreateDirectory(tmpDir);

                foreach (KeyValuePair<string, byte[]> kv in payload)
                {
                    string rel = kv.Key.Replace('/', Path.DirectorySeparatorChar);
                    string outPath = Path.Combine(tmpDir, rel);
                    string outDir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
                    File.WriteAllBytes(outPath, kv.Value);
                }

                // 5) 回读校验临时目录
                int checkedCount = 0;
                foreach (PackFile pf in e.Files)
                {
                    string outPath = Path.Combine(tmpDir, pf.Rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(outPath)) throw new IOException("还原校验失败：缺少 " + pf.Rel);
                    byte[] back = File.ReadAllBytes(outPath);
                    if (back.Length != pf.Length ||
                        !string.Equals(Sha256(back), pf.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("还原校验失败：" + pf.Rel + " SHA256 不一致");
                    checkedCount++;
                }

                // 6) 临时目录改名为正式目录（同盘 rename，瞬时）
                if (Directory.Exists(target.DirPath))
                {
                    // 极端竞态：改名期间又出现了正式目录，保留它，把临时目录当安全副本留下
                    log("[" + e.VersionLabel + "] 还原期间目标目录被重建，临时副本保留在：" + tmpDir);
                    tmpDir = null;
                    throw new IOException("目标目录在还原期间被重新创建，已保留临时副本，未覆盖");
                }
                Directory.Move(tmpDir, target.DirPath);
                tmpDir = null;

                oc.Ok = true;
                log("[" + e.VersionLabel + "] 还原完成：" + target.DirPath +
                    "（" + checkedCount + " 文件，SHA256 全部一致）");
                Diagnostics.Log("restore ok: ver=" + e.VersionLabel + " -> " + target.DirPath);
            }
            catch (Exception ex)
            {
                oc.Ok = false;
                oc.Detail = ex.Message;
                log("[" + e.VersionLabel + "] 还原失败：" + ex.Message);

                // 回滚：删掉临时目录，把安全备份移回原位
                try { if (tmpDir != null && Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
                catch { }
                if (!string.IsNullOrEmpty(oc.SafetyBackupPath) && !Directory.Exists(target.DirPath))
                {
                    try
                    {
                        Directory.Move(oc.SafetyBackupPath, target.DirPath);
                        log("[" + e.VersionLabel + "] 已回滚到安全备份：" + oc.SafetyBackupPath);
                    }
                    catch (Exception rb) { log("[" + e.VersionLabel + "] 回滚失败（安全备份仍在：" + oc.SafetyBackupPath + "）：" + rb.Message); }
                }
                Diagnostics.Log("restore FAIL: ver=" + e.VersionLabel + " : " + ex.Message);
            }

            return oc;
        }

        // ------------------------------------------------------------ zip / manifest / 杂项

        private static void ParseManifest(string zipPath, out Dictionary<string, string> head,
                                          out List<Dictionary<string, string>> sections)
        {
            head = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sections = new List<Dictionary<string, string>>();

            byte[] raw = ReadEntryBytes(zipPath, ManifestName);
            if (raw == null) throw new InvalidDataException("备份包内没有 " + ManifestName + "，不是本程序生成的备份");

            string text = new UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF');
            Dictionary<string, string> current = head;

            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = line.Trim();
                if (s.Length == 0) continue;
                if (s.StartsWith("[", StringComparison.Ordinal))
                {
                    current = new MultiValueDict(StringComparer.OrdinalIgnoreCase);
                    sections.Add(current);
                    continue;
                }
                int i = s.IndexOf('=');
                if (i <= 0) continue;
                string key = s.Substring(0, i).Trim();
                string val = s.Substring(i + 1).Trim();
                if (current is MultiValueDict) ((MultiValueDict)current).AddValue(key, val);
                else current[key] = val;
            }
        }

        /// <summary>section 里的 file= 键会出现多次，用这个字典保留全部值（以 \n 连接，读时再拆）。</summary>
        private sealed class MultiValueDict : Dictionary<string, string>
        {
            public MultiValueDict(StringComparer cmp) : base(cmp) { }
            public void AddValue(string key, string val)
            {
                string old;
                if (TryGetValue(key, out old)) { if (old.IndexOf('\n') < 0 && key != "file") this[key] = old; else this[key] = old + "\n" + val; }
                else this[key] = val;
            }
        }

        private static byte[] ReadEntry(ZipArchiveEntry e)
        {
            using (Stream s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static byte[] ReadEntryBytes(string zipPath, string entryName)
        {
            using (var fs = File.OpenRead(zipPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false, Encoding.UTF8))
            {
                ZipArchiveEntry e = zip.GetEntry(entryName);
                return e == null ? null : ReadEntry(e);
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
            return s.Replace('.', '_');
        }

        private static string Normalize(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
            catch { return (p ?? "").TrimEnd('\\', '/'); }
        }

        private static string JoinVersions(List<PackEntry> list)
        {
            var seen = new List<string>();
            foreach (PackEntry e in list) if (!seen.Contains(e.VersionLabel)) seen.Add(e.VersionLabel);
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
