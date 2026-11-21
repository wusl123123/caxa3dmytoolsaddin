using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Caxa3dToolboxMaker
{
    // 反查 CAXA 3D 安装后的用户 Toolbox 目录（参考 caxa3dmytoolboxbak\ToolboxLibrary.cs）。
    // 铁律：不硬编码用户名、版本号、语言、盘符。
    // 链路：%LOCALAPPDATA%\%APPDATA%\CAXA -> ^CAXA\s+3D -> ^\d+(\.\d+)*$ -> <语言>\ToolBox
    internal static class ToolboxPath
    {
        internal const string DirName = "ToolBox";

        static readonly Regex FamilyPattern = new Regex(@"^CAXA\s+3D", RegexOptions.IgnoreCase);
        static readonly Regex VersionPattern = new Regex(@"^\d+(\.\d+)*$");

        sealed class Candidate
        {
            internal string Path;
            internal int[] Version;
            internal int RootRank;
            internal int LangRank;
        }

        // 默认根：优先 Local（本机实例），再 Roaming；多版本时取版本号最高者。
        internal static string Resolve()
        {
            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CAXA");
            string roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CAXA");
            return Resolve(new string[] { local, roaming });
        }

        // 供回归测试注入临时根：验证“版本号/语言动态识别”，不依赖本机安装。
        internal static string Resolve(string[] roots)
        {
            if (roots == null) return null;
            List<Candidate> found = new List<Candidate>();

            for (int r = 0; r < roots.Length; r++)
            {
                string root = roots[r];
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                foreach (string famDir in SafeDirs(root))
                {
                    if (!FamilyPattern.IsMatch(Path.GetFileName(famDir))) continue;

                    foreach (string verDir in SafeDirs(famDir))
                    {
                        string ver = Path.GetFileName(verDir);
                        if (!VersionPattern.IsMatch(ver)) continue;

                        // 兼容布局：<版本>\ToolBox（无语言层）
                        Add(found, Path.Combine(verDir, DirName), ver, r, 2);

                        // 常见布局：<版本>\<语言>\ToolBox
                        foreach (string langDir in SafeDirs(verDir))
                        {
                            string lang = Path.GetFileName(langDir);
                            if (string.Equals(lang, DirName, StringComparison.OrdinalIgnoreCase)) continue;
                            int langRank = string.Equals(lang, "zh-CN", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                            Add(found, Path.Combine(langDir, DirName), ver, r, langRank);
                        }
                    }
                }
            }

            if (found.Count == 0) return null;
            found.Sort(delegate(Candidate a, Candidate b)
            {
                int c = CompareVersionDesc(a.Version, b.Version);
                if (c != 0) return c;
                c = a.RootRank.CompareTo(b.RootRank);
                if (c != 0) return c;
                c = a.LangRank.CompareTo(b.LangRank);
                if (c != 0) return c;
                return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return found[0].Path;
        }

        static void Add(List<Candidate> list, string path, string ver, int rootRank, int langRank)
        {
            if (!Directory.Exists(path)) return;
            Candidate c = new Candidate();
            c.Path = path;
            c.Version = ParseVersion(ver);
            c.RootRank = rootRank;
            c.LangRank = langRank;
            list.Add(c);
        }

        static string[] SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch { return new string[0]; }
        }

        static int[] ParseVersion(string s)
        {
            string[] parts = s.Split('.');
            int[] v = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int n;
                v[i] = int.TryParse(parts[i], out n) ? n : 0;
            }
            return v;
        }

        // 负 = a 版本更高（排前面），正 = b 更高；缺位按 0 处理。
        static int CompareVersionDesc(int[] a, int[] b)
        {
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int av = i < a.Length ? a[i] : 0;
                int bv = i < b.Length ? b[i] : 0;
                if (av != bv) return bv.CompareTo(av);
            }
            return 0;
        }
    }
}
