// 备份/还原引擎：不操作 ICAPI COM，纯文件系统 + 注册表 + zip。
// 备份范围依据《caxa3d配置备份.md》本机实测落点：
//   - 注册表 HKCU\SOFTWARE\CAXA 整树（.ucx 等选项全部落注册表，无文件侧副本）
//   - %LOCALAPPDATA%\CAXA\CAXA 3D\<ver>\{PropertyTemplates,3DBOM,materials,Customization}
//   - %APPDATA%\CAXA\CAXA 3D\<ver>\{Customization, zh-CN\Customization}（3D 功能区/快捷键）
//   - %LOCALAPPDATA%\CAXA\CAXA CAD CXIC-INT *\<v>\：zh-CN\Config\*.xml + 根目录 *.acc/*.uik
//   - %APPDATA%\CAXA\CAXA CAD CXIC-INT *\<v>\zh-CN\Temp\*.uic
// 铁律：
//   - 还原前必须先做「还原前备份」zip，失败即中止，不覆盖任何在服文件
//   - reg export/import 走 reg.exe，轮询等待 + DoEvents，禁 WaitForExit
//   - 判成功只看副作用：目标文件存在 + reg exit=0 + SysConfig_GLOBAL.xml 的 AutoLoadModules 条目数
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace caxa3dsetbackup
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal static class BackupEngine
    {
        private const long MaxFileBytes = 20 * 1024 * 1024;   // 单文件上限，防运行期缓存/大体量库拖爆 zip
        private const string ManifestName = "manifest.json";

        public sealed class FileEntry
        {
            public string Archive;   // zip 内路径，如 files/0001_CustomTemplates.xml
            public string Target;    // 还原目标绝对路径
        }

        public sealed class RegEntry
        {
            public string File;      // zip 内路径，如 registry/CAXA.reg
            public string KeyPath;   // reg export 时的注册表键
        }

        // ---------------------------------------------------------- 备份

        /// <summary>探测所有在服配置并复制到暂存目录，返回暂存目录路径。</summary>
        public static string PrepareBackup(Action<string> log)
        {
            string stamp = DateTime.Now.ToString("yyyyMMddHHmm");
            string staging = Path.Combine(Path.GetTempPath(), "caxa3dsetbackup", "backup-" + stamp);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            var entries = new List<FileEntry>();
            var regs = new List<RegEntry>();
            int seq = 0;
            long totalBytes = 0;

            // 1) 注册表 HKCU\SOFTWARE\CAXA 整树
            if (Registry.CurrentUser.OpenSubKey(@"SOFTWARE\CAXA") != null)
            {
                string regDir = Path.Combine(staging, "registry");
                Directory.CreateDirectory(regDir);
                string regFile = Path.Combine(regDir, "CAXA.reg");
                RunReg("export \"HKEY_CURRENT_USER\\SOFTWARE\\CAXA\" \"" + regFile + "\" /y", log);
                log("注册表已导出：HKCU\\SOFTWARE\\CAXA (" + FormatSize(new FileInfo(regFile).Length) + ")");
                regs.Add(new RegEntry { File = "registry/CAXA.reg", KeyPath = @"HKEY_CURRENT_USER\SOFTWARE\CAXA" });
            }
            else
            {
                log("警告：注册表 HKCU\\SOFTWARE\\CAXA 不存在，跳过注册表备份");
            }

            // 2) 3D 侧文件
            string localCaxa = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAXA");
            string roamingCaxa = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CAXA");

            string local3d = Path.Combine(localCaxa, "CAXA 3D");
            if (Directory.Exists(local3d))
            {
                foreach (string verDir in Directory.GetDirectories(local3d))
                {
                    string ver = Path.GetFileName(verDir);
                    foreach (string sub in new[] { "PropertyTemplates", "3DBOM", "materials", "Customization" })
                    {
                        seq = AddDirRecursive(Path.Combine(verDir, sub), staging, entries, ref seq, ref totalBytes, log, "3D " + ver);
                    }
                    Diagnostics.Log("backup: 3D local " + ver + " collected");
                }
            }
            else
            {
                log("提示：未找到 " + local3d + "（本机无 CAXA 3D 用户配置）");
            }

            string roaming3d = Path.Combine(roamingCaxa, "CAXA 3D");
            if (Directory.Exists(roaming3d))
            {
                foreach (string verDir in Directory.GetDirectories(roaming3d))
                {
                    string ver = Path.GetFileName(verDir);
                    foreach (string sub in new[] { "Customization", Path.Combine("zh-CN", "Customization") })
                    {
                        seq = AddDirRecursive(Path.Combine(verDir, sub), staging, entries, ref seq, ref totalBytes, log, "3D " + ver);
                    }
                }
            }

            // 3) 图纸环境（2D / CAXADraft 内核）侧
            if (Directory.Exists(localCaxa))
            {
                foreach (string famDir in Directory.GetDirectories(localCaxa, "CAXA CAD CXIC-INT*"))
                {
                    string fam = Path.GetFileName(famDir);
                    foreach (string verDir in Directory.GetDirectories(famDir))
                    {
                        string ver = Path.GetFileName(verDir);
                        seq = AddDirRecursive(Path.Combine(verDir, "zh-CN", "Config"), staging, entries, ref seq, ref totalBytes, log, fam + " " + ver);
                        foreach (string acc in Directory.GetFiles(verDir, "*.acc"))
                            seq = AddFile(acc, staging, entries, ref seq, ref totalBytes, log, fam + " " + ver);
                        foreach (string uik in Directory.GetFiles(verDir, "*.uik"))
                            seq = AddFile(uik, staging, entries, ref seq, ref totalBytes, log, fam + " " + ver);
                    }
                }
            }
            if (Directory.Exists(roamingCaxa))
            {
                foreach (string famDir in Directory.GetDirectories(roamingCaxa, "CAXA CAD CXIC-INT*"))
                {
                    string fam = Path.GetFileName(famDir);
                    foreach (string verDir in Directory.GetDirectories(famDir))
                    {
                        string ver = Path.GetFileName(verDir);
                        seq = AddDirRecursive(Path.Combine(verDir, "zh-CN", "Temp"), staging, entries, ref seq, ref totalBytes, log, fam + " " + ver, "*.uic");
                    }
                }
            }

            // 4) 写 manifest
            var serializer = new JavaScriptSerializer();
            var manifest = new Dictionary<string, object>();
            manifest["tool"] = "caxa3dsetbackup";
            manifest["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var arr = new List<object>();
            foreach (var e in entries) arr.Add(new Dictionary<string, object> { { "a", e.Archive }, { "t", e.Target } });
            manifest["entries"] = arr;
            var rarr = new List<object>();
            foreach (var r in regs) rarr.Add(new Dictionary<string, object> { { "f", r.File }, { "k", r.KeyPath } });
            manifest["registry"] = rarr;
            File.WriteAllText(Path.Combine(staging, ManifestName),
                serializer.Serialize(manifest), new UTF8Encoding(false));

            log("共收集 " + entries.Count + " 个文件（" + FormatSize(totalBytes) + "）+ " + regs.Count + " 项注册表导出");
            Diagnostics.Log("backup prepared: files=" + entries.Count + " regs=" + regs.Count + " bytes=" + totalBytes);
            return staging;
        }

        /// <summary>把暂存目录打包成 zip。成功后删除暂存目录。</summary>
        public static void PackZip(string staging, string destZip, Action<string> log)
        {
            if (File.Exists(destZip)) File.Delete(destZip);
            using (var fs = new FileStream(destZip, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                foreach (string file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(staging.Length).TrimStart('\\', '/').Replace('\\', '/');
                    zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
                }
            }
            log("zip 已生成：" + destZip + "（" + FormatSize(new FileInfo(destZip).Length) + "）");
            Diagnostics.Log("backup zip written: " + destZip);
            try { Directory.Delete(staging, true); } catch { }
        }

        // ---------------------------------------------------------- 还原

        /// <summary>识别本程序备份的 zip 并还原。返回还原的文件数。</summary>
        public static int RestoreFromZip(string zipPath, Action<string> log)
        {
            // 1) 解包到临时目录并校验 manifest
            string stamp = DateTime.Now.ToString("yyyyMMddHHmm");
            string extract = Path.Combine(Path.GetTempPath(), "caxa3dsetbackup", "restore-" + stamp);
            if (Directory.Exists(extract)) Directory.Delete(extract, true);
            Directory.CreateDirectory(extract);

            var serializer = new JavaScriptSerializer();
            Dictionary<string, object> manifest;
            try
            {
                using (var fs = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read, false, Encoding.UTF8))
                {
                    var manifestEntry = zip.GetEntry(ManifestName);
                    if (manifestEntry == null)
                        throw new InvalidDataException("zip 内没有 " + ManifestName + "，不是本程序生成的备份包");
                    string json;
                    using (var sr = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
                        json = sr.ReadToEnd();
                    if (json.IndexOf("caxa3dsetbackup", StringComparison.Ordinal) < 0)
                        throw new InvalidDataException("manifest 校验失败：不是 caxa3dsetbackup 生成的备份包");
                    manifest = serializer.Deserialize<Dictionary<string, object>>(json);
                    foreach (var e in zip.Entries)
                    {
                        string rel = e.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string dest = Path.Combine(extract, rel);
                        string destDir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                        if (e.Name.Length == 0) continue;   // 目录条目
                        e.ExtractToFile(dest, true);
                    }
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException("读取备份包失败：" + ex.Message);
            }

            if (manifest == null || !manifest.ContainsKey("entries"))
                throw new InvalidDataException("manifest 格式不正确（缺少 entries）");
            log("备份包制作时间：" + (manifest.ContainsKey("created") ? Convert.ToString(manifest["created"]) : "未知"));

            try
            {
                // 2) 还原前安全备份（当前在服状态整体打包，失败即中止）
                string safetyZip = Path.Combine(Path.GetTempPath(), "caxa3dsetbackup",
                    "还原前备份-" + stamp + ".zip");
                log("正在生成还原前备份：" + safetyZip);
                string safetyStaging = PrepareBackup(m => { });
                PackZip(safetyStaging, safetyZip, m => { });
                log("还原前备份完成（" + FormatSize(new FileInfo(safetyZip).Length) + "）");

                // 3) 文件还原
                int ok = 0, fail = 0;
                var entries = GetManifestList(manifest, "entries");
                foreach (object o in entries)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string archive = Convert.ToString(d["a"]);
                    string target = Convert.ToString(d["t"]);
                    string src = Path.Combine(extract, archive.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (!File.Exists(src)) throw new FileNotFoundException("包内缺文件", archive);
                        string tdir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(tdir) && !Directory.Exists(tdir)) Directory.CreateDirectory(tdir);
                        CopyOverwrite(src, target);
                        ok++;
                        Diagnostics.Log("restore ok: " + target);
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        log("还原失败：" + target + " —— " + ex.Message);
                        Diagnostics.Log("restore FAIL: " + target + " : " + ex.Message);
                    }
                }
                log("文件还原：成功 " + ok + " 个，失败 " + fail + " 个");

                // 4) 注册表还原
                var rentries = GetManifestList(manifest, "registry");
                foreach (object o in rentries)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string f = Convert.ToString(d["f"]);
                    string k = Convert.ToString(d["k"]);
                    string src = Path.Combine(extract, f.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(src)) { log("注册表还原跳过（包内缺 " + f + "）"); continue; }
                    RunReg("import \"" + src + "\"", log);
                    log("注册表已导入：" + k);
                }

                // 5) 权威副作用校验：SysConfig_GLOBAL.xml 的 AutoLoadModules 条目数
                VerifyAutoLoadModules(extract, manifest, log);

                log("还原完成。CAXA 退出时可能回写部分界面配置（.uic），若还原未生效：先退出 CAXA，再重新还原一次。");
                return ok;
            }
            finally
            {
                try { Directory.Delete(extract, true); } catch { }
            }
        }

        /// <summary>判据：还原后的 SysConfig_GLOBAL.xml 必须仍含 CRX 自加载条目（本机实测应为 3 条）。</summary>
        private static void VerifyAutoLoadModules(string extract, Dictionary<string, object> manifest, Action<string> log)
        {
            try
            {
                var entries = GetManifestList(manifest, "entries");
                foreach (object o in entries)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string target = Convert.ToString(d["t"]);
                    if (Path.GetFileName(target) != "SysConfig_GLOBAL.xml") continue;
                    string src = Path.Combine(extract, Convert.ToString(d["a"]).Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(src)) continue;
                    string text = File.ReadAllText(src, Encoding.UTF8);
                    MatchCollection mods = Regex.Matches(text, "<Module\\s+Type=\"CRX\"[^>]*Param=\"([^\"]+)\"");
                    log("CRX 自加载校验：SysConfig_GLOBAL.xml 含 " + mods.Count + " 条 AutoLoadModules CRX 条目");
                    foreach (Match m in mods)
                        log("  - " + Path.GetFileName(m.Groups[1].Value));
                    if (mods.Count == 0)
                        log("警告：还原后的 SysConfig_GLOBAL.xml 没有 CRX 条目——带图插件的图纸侧加载链可能失效！");
                }
            }
            catch (Exception ex)
            {
                log("CRX 自加载校验异常：" + ex.Message);
            }
        }

        // ---------------------------------------------------------- 收集辅助

        private static int AddDirRecursive(string dir, string staging, List<FileEntry> entries,
            ref int seq, ref long totalBytes, Action<string> log, string tag, string pattern = "*")
        {
            if (!Directory.Exists(dir)) return seq;
            int before = seq;
            foreach (string file in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length > MaxFileBytes)
                    {
                        log("跳过过大文件（" + FormatSize(fi.Length) + "）：" + file);
                        continue;
                    }
                    seq = AddFile(file, staging, entries, ref seq, ref totalBytes, log, tag);
                }
                catch (Exception ex)
                {
                    log("收集失败：" + file + " —— " + ex.Message);
                }
            }
            if (seq > before)
                log("已收集 [" + tag + "] " + ShortPath(dir) + "（" + (seq - before) + " 个文件）");
            return seq;
        }

        private static int AddFile(string file, string staging, List<FileEntry> entries,
            ref int seq, ref long totalBytes, Action<string> log, string tag)
        {
            seq++;
            string archiveRel = "files/" + seq.ToString("0000") + "_" + Path.GetFileName(file);
            string dest = Path.Combine(staging, archiveRel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(file, dest, true);
            long len = new FileInfo(file).Length;
            totalBytes += len;
            entries.Add(new FileEntry { Archive = archiveRel, Target = file });
            Diagnostics.Log("backup collect [" + tag + "]: " + file + " (" + len + " B)");
            return seq;
        }

        private static void CopyOverwrite(string src, string target)
        {
            try { File.Copy(src, target, true); }
            catch (UnauthorizedAccessException)
            {
                File.SetAttributes(target, FileAttributes.Normal);
                File.Copy(src, target, true);
            }
        }

        private static string ShortPath(string path)
        {
            return path.Length > 70 ? "…" + path.Substring(path.Length - 70) : path;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        // JavaScriptSerializer 把 JSON 数组反序列化为 ArrayList（不是 object[]），
        // 因此 "as object[]" 永远返回 null，导致还原时一个文件都不处理。
        // 本方法统一把 manifest 中名为 key 的数组取回为字典列表。
        private static List<Dictionary<string, object>> GetManifestList(Dictionary<string, object> manifest, string key)
        {
            var list = new List<Dictionary<string, object>>();
            if (!manifest.ContainsKey(key)) return list;
            object raw = manifest[key];
            var al = raw as System.Collections.ArrayList;
            if (al != null)
            {
                foreach (object x in al)
                {
                    var d = x as Dictionary<string, object>;
                    if (d != null) list.Add(d);
                }
                return list;
            }
            var arr = raw as object[];
            if (arr != null)
            {
                foreach (object x in arr)
                {
                    var d = x as Dictionary<string, object>;
                    if (d != null) list.Add(d);
                }
            }
            return list;
        }

        // ---------------------------------------------------------- reg.exe 执行

        /// <summary>reg export/import。轮询等待 + DoEvents（禁 WaitForExit），兜底 10s（规则 16）。</summary>
        private static void RunReg(string args, Action<string> log)
        {
            var psi = new ProcessStartInfo("reg.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 10000 && !p.HasExited)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
                if (!p.HasExited)
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("reg.exe 超时：" + args);
                }
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (p.ExitCode != 0)
                {
                    Diagnostics.Log("reg fail: " + args + " exit=" + p.ExitCode + " " + stdout + " " + stderr);
                    throw new Exception("reg.exe 失败（exit=" + p.ExitCode + "）：" + (stdout + " " + stderr).Trim());
                }
                Diagnostics.Log("reg ok: " + args);
            }
        }
    }
}
