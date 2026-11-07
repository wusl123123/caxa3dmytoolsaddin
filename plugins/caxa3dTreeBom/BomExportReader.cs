// ================================================================ BOM 导出兜底通道（表头列取值）
//
// 用户指令（2026-11-06 23:37）：「表头列属性……优先从自定义属性中获取，没有数据时候兜底从 BOM 导出」。
// 本文件只负责「② BOM 导出」这一级通道与「表头 → 候选字段名」的映射；① 自定义属性共享表仍由
// CaxaService.TryGetCustomProp 走（共享表唯一铁律＝规则 14.7/14.9，本文件一律不碰非共享表）。
//
// 通道（TLB/interop 实测，非估算）：
//   IZSceneElement.ExportSelfBOM(out Int32 plCount, out Object Label, out Object Value, out Object Type, out Object Unit)
//     IID 75DCDA23-5D9E-4D01-8049-2CFC7A1D40F9｜DispId 46｜memid 30｜槽 39
//     来源：_probe-tools\out\tlb\2027\members.csv；interop 侧 PRESENT 由 caxa-interop-check.ps1 实测
//     （本工程 Lib\interop.ICApiIronCAD.dll v26.0.0.0，sha256 6CF1D0F9…5025）。
//   5 个 out ⇒ 规则「≥2 个 out 走 RCW」，故这里用 RCW 直调，不用槽位直调。
//
// 兄弟工程 caxa3dBatchMaster 已把同一通道跑通实机（11-06 19:00 trace，51 行命中）：
//   plCount 实测 55~77，label=value=type=unit 四数组长度全等于 plCount；
//   label 是**英文内部键与中文显示名混排**（实测出现过 PartMass/MassDensity/Material/"Part Number"/PartName/
//   PartVolume/Sizebox*/PartQuantity/ModelGUID，以及 SC图号/SC名称/单位/名称/单重/主关件）
//   ⇒ 所以匹配顺序＝「表头原名精确同名」优先，再由映射表补英文键；大小写不敏感只作第二档。
//   同一轮还留了一条坑：日志里的 labels 串被截断（…共 55 个），导致「label 集合里到底有没有某键」读不到
//   ⇒ 本文件对**本次运行的第一个元素**把 labels 全量落 trace，之后每个元素只记计数，避免再犯。
//
// 单位/精度：值一律按 COM 原样转字符串输出，不做单位换算（质量/密度等字段的单位界面对照＝待实机）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dTreeBom
{
    /// <summary>
    /// 官方 BOM 字典的中英对照（表头显示名 ↔ BOM 内部键）。
    /// 唯一来源＝本机 CAXA 2027 的 IC_BOM_DIC.xml（2024 与 2027 内容同一份）：
    ///   C:\Program Files\CAXA\CAXA 3D\2027\AppData\zh-cn\3DBOM\IC_BOM_DIC.xml
    ///   C:\Program Files\CAXA\CAXA 3D\2027\bin\CAXADraft\Config\zh-CN\IC_BOM_DIC.xml
    /// 读取口径＝规则「数字/名称必须是查得到来源的实测值」：本表逐条照字典 Name/Description 抄，不猜。
    /// 另外三条（重量/图号/材质）是**用户与本机界面的叫法**，字典里没有，单列在 Synonyms 里并注明。
    /// </summary>
    [ComVisible(false)]
    internal static class BomDictMap
    {
        // 字典 Description（中文显示名，小写归一）→ 字典 Name（BOM 内部键，原样大小写）
        private static readonly Dictionary<string, string[]> CnToKey =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "名称", new[] { "PartName" } },
            { "备注", new[] { "Description" } },
            { "代号", new[] { "Part Number" } },
            { "质量", new[] { "PartMass" } },
            { "质量来源", new[] { "PartMassType" } },
            { "密度", new[] { "MassDensity" } },
            { "用户指定质量", new[] { "PartSpecMass" } },
            { "材料", new[] { "Material" } },
            { "体积", new[] { "PartVolume" } },
            { "数量", new[] { "PartQuantity" } },
            { "包围盒长度", new[] { "SizeboxLength" } },
            { "包围盒宽度", new[] { "SizeboxWidth" } },
            { "包围盒高度", new[] { "SizeboxHeight" } },
            { "三维曲线长度", new[] { "3DCurveLength" } },
            { "起始拔模角度", new[] { "StartTaper" } },
            { "终止拔模角度", new[] { "EndTaper" } },
            { "总计", new[] { "Amount" } },
            { "输出", new[] { "Output" } },
        };

        // 界面/用户叫法（字典外）→ 候选内部键。每条都在注释里写来源，别当字典原文。
        private static readonly Dictionary<string, string[]> Synonyms =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // 用户 11-06 定「重量＝PartMass」（caxa3dBatchMaster 同一口径：判定类规则以用户定义为准，
            // 与字典 Description「质量」冲突留痕，两个叫法都给候选）
            { "重量", new[] { "PartMass" } },
            // 「图号」是 CAXA 图纸/本插件表头的常用叫法；BOM 字典里对应的是「代号」＝Part Number
            { "图号", new[] { "Part Number" } },
            { "零件号", new[] { "Part Number" } },
            { "零件编号", new[] { "Part Number" } },
            { "零件名", new[] { "PartName" } },
            { "零件名称", new[] { "PartName" } },
            { "材质", new[] { "Material" } },
            { "描述", new[] { "Description" } },
            { "单重", new[] { "PartMass" } },   // caxa3dBatchMaster 实机 labels 里出现过「单重」
        };

        /// <summary>显示名 → BOM 内部键候选（没有映射返回空数组，不猜）。</summary>
        public static string[] KeysFor(string display)
        {
            string d = (display ?? "").Trim();
            if (d.Length == 0) return new string[0];
            string[] k;
            if (CnToKey.TryGetValue(d, out k)) return k;
            if (Synonyms.TryGetValue(d, out k)) return k;
            return new string[0];
        }
    }

    /// <summary>
    /// 「表头列 → BOM 兜底候选名」映射（用户可编辑，热重载，口径与 caxa3dBatchMaster 的
    /// caxa3dBatchMaster内部字段映射.txt 一致，但**文件名带本项目前缀**、落在本项目自己的 %APPDATA% 目录）。
    /// 一行一条 `表头名=候选1,候选2`；候选按书写顺序试、命中即停。
    /// 值写 `无` / `-` / `none` ＝ 该列禁用 BOM 兜底（＝用户要「只认自定义属性」的那一列的开关）。
    /// 没配的列＝自动候选（原名 → 去 SC 前缀 → 官方字典内部键），见 <see cref="Candidates"/>。
    /// </summary>
    [ComVisible(false)]
    internal static class HeaderBomMap
    {
        public const string FileName = "caxa3dTreeBom表头字段映射.txt";

        private const string Dis1 = "无";
        private const string Dis2 = "-";
        private const string Dis3 = "none";

        private static Dictionary<string, string[]> _map;   // null 值＝该列禁用兜底
        private static string _path;
        private static DateTime _stamp = DateTime.MinValue;
        private static string _note = "";

        public static string RuleFilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "caxa3dTreeBom");
                return Path.Combine(dir, FileName);
            }
        }

        /// <summary>首次或文件变更时重载；返回一句要进日志的说明（未变更返回 null）。种子文件优先取插件 DLL 同目录。</summary>
        public static string ReloadIfChanged(bool force = false)
        {
            string known = _path;
            DateTime oldStamp = _stamp;
            int oldCount = _map == null ? -1 : _map.Count;
            DateTime stamp = DateTime.MinValue;
            if (known != null && File.Exists(known))
            {
                try { stamp = File.GetLastWriteTime(known); } catch { stamp = DateTime.MinValue; }
            }
            if (!force && _map != null && stamp != DateTime.MinValue && stamp == oldStamp) return null;

            string note = "";
            string path = RuleFilePath;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(path))
                {
                    string seed = SeedText();
                    if (seed != null) { File.WriteAllText(path, seed, new System.Text.UTF8Encoding(false)); note = "已播种默认映射文件"; }
                    else note = "程序目录无种子文件，已按内置默认生成";
                }
            }
            catch (Exception ex) { note = "映射文件不可写（本次只用内置默认）：" + ex.Message; }

            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            int disabled = 0, bad = 0;
            try
            {
                if (File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path, System.Text.Encoding.UTF8))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)
                            || line.StartsWith("//", StringComparison.Ordinal)) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) { bad++; continue; }
                        string key = line.Substring(0, eq).Trim();
                        string val = line.Substring(eq + 1).Trim();
                        if (key.Length == 0) { bad++; continue; }
                        if (val.Length == 0 || string.Equals(val, Dis1, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(val, Dis2, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(val, Dis3, StringComparison.OrdinalIgnoreCase))
                        { map[key] = null; disabled++; continue; }
                        string[] parts = val.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
                        var list = new List<string>();
                        foreach (string p in parts)
                        {
                            string t = (p ?? "").Trim();
                            if (t.Length == 0) continue;
                            bool dup = false;
                            foreach (string q in list) if (string.Equals(q, t, StringComparison.Ordinal)) { dup = true; break; }
                            if (!dup) list.Add(t);
                        }
                        if (list.Count == 0) { map[key] = null; disabled++; }
                        else map[key] = list.ToArray();
                    }
                }
            }
            catch (Exception ex) { note = "读取映射文件失败（本次只用内置默认）：" + ex.Message + "｜" + note; map.Clear(); }

            DateTime newStamp = DateTime.MinValue;
            if (File.Exists(path)) { try { newStamp = File.GetLastWriteTime(path); } catch { } }
            bool changed = oldCount < 0 || map.Count != oldCount || newStamp != oldStamp || path != known;
            _map = map; _path = path; _stamp = newStamp; _note = note;
            string text = "表头 BOM 映射：生效路径＝" + SafePath() + "｜生效列 " + map.Count
                + "｜禁用 " + disabled + (bad > 0 ? "｜格式不合跳过 " + bad + " 行" : "")
                + (string.IsNullOrEmpty(note) ? "" : "｜" + note);
            return force || changed ? text : null;
        }

        public static string InitAndDescribe() { return ReloadIfChanged(true); }

        public static string SafePath()
        {
            try { ReloadIfChanged(); } catch { }
            return _path ?? "<不可得>";
        }

        /// <summary>该表头是否被用户显式禁用 BOM 兜底。</summary>
        public static bool IsDisabled(string header)
        {
            try { ReloadIfChanged(); } catch { }
            string[] v;
            return _map != null && _map.TryGetValue((header ?? "").Trim(), out v) && v == null;
        }

        /// <summary>
        /// 表头 → BOM 侧候选名列表（顺序＝原名 → 去 SC 前缀 → 官方字典内部键；用户在映射文件里
        /// 写过的列则**完全按用户候选**，不再自动追加，避免把用户明确排除的键又塞回去）。
        /// 返回空数组＝该列禁用兜底。候选只查名字、不做模糊匹配（规则 14.1/14.2）。
        /// </summary>
        public static string[] Candidates(string header)
        {
            string h = (header ?? "").Trim();
            if (h.Length == 0) return new string[0];

            try { ReloadIfChanged(); } catch { }
            string[] user;
            if (_map != null && _map.TryGetValue(h, out user))
                return user ?? new string[0];

            var list = new List<string>();
            Action<string> add = delegate (string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                foreach (string q in list) if (string.Equals(q, s, StringComparison.Ordinal)) return;
                list.Add(s);
            };
            add(h);                                                   // 实机 labels 里确有 SC图号/SC名称 这类中文名
            add(CaxaService.StripScPrefix(h));                        // SC材料 → 材料
            foreach (string k in BomDictMap.KeysFor(CaxaService.StripScPrefix(h))) add(k);
            foreach (string k in BomDictMap.KeysFor(h)) add(k);
            return list.ToArray();
        }

        /// <summary>种子内容：内置默认全是注释（自动候选已够用），用户按需加行覆盖。</summary>
        private static string SeedText()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(HeaderBomMap).Assembly.Location);
                if (string.IsNullOrEmpty(dir)) dir = AppDomain.CurrentDomain.BaseDirectory;
                string seedFile = Path.Combine(dir ?? "", FileName);
                if (File.Exists(seedFile)) return File.ReadAllText(seedFile, System.Text.Encoding.UTF8);
            }
            catch { }

            return
"# caxa3dTreeBom 表头列 → BOM 导出兜底候选名映射\r\n" +
"# 一行一条：表头名=候选1,候选2（候选按顺序试，命中即停；逗号支持半角/全角）。\r\n" +
"# 表头名不区分大小写；候选名区分大小写（照 CAXA BOM 里的写法抄，不要猜）。\r\n" +
"# 值写「无」或「-」或「none」＝这一列禁用 BOM 兜底（只认自定义属性，读不到就留第三态）。\r\n" +
"# 没在这一行出现的列＝自动候选：表头原名 → 去掉 SC 前缀 → 官方字典 IC_BOM_DIC.xml 的内部键\r\n" +
"#   （名称=PartName 备注=Description 代号/图号=Part Number 材料=Material 质量/重量=PartMass\r\n" +
"#     密度=MassDensity 体积=PartVolume 数量=PartQuantity 包围盒长度/宽度/高度=SizeboxLength/Width/Height）。\r\n" +
"# 改完保存即生效（按文件修改时间热重载，不必重启 CAXA）。\r\n" +
"# 实机取证：抓取后看 %TEMP%\\caxa3dTreeBom_trace.log 里 ExportSelfBOM 的 labels 全集，\r\n" +
"#   再决定要不要为某一列写显式候选（例如日期/供应商这类非字典列）。\r\n";
        }
    }

    /// <summary>一次 ExportSelfBOM 的整行快照（四数组按 label 对齐），供同一元素的多个候选列复用。</summary>
    [ComVisible(false)]
    internal sealed class BomSnapshot
    {
        public bool Queried;
        public string Note = "";
        public int PlCount;
        public List<string> Labels = new List<string>();
        public List<string> Values = new List<string>();
        public List<string> Types = new List<string>();
        public List<string> Units = new List<string>();
        public string AllLabels = "<无>";

        /// <summary>在 BOM 行里按候选顺序找一个**有值**的字段：精确同名 → 大小写不敏感 → 下一个候选。
        /// 「命中但值为空白」算没数据（用户指令「没有数据时候兜底」＝空白也继续兜底），但把这件事写进 note，
        /// 让「读不到」与「读到空」在 trace 里分家。</summary>
        public BomHit Find(string[] candidates)
        {
            if (candidates == null || candidates.Length == 0)
                return new BomHit { Note = "无候选名" };
            if (!Queried)
                return new BomHit { Note = "BOM 未取到（" + Note + "）" };

            var emptyHit = new List<string>();
            foreach (string c in candidates)
            {
                int i = IndexOf(c, false);
                string mode = "精确同名";
                if (i < 0) { i = IndexOf(c, true); mode = "大小写不敏感"; }
                if (i < 0) continue;

                string v = i < Values.Count ? (Values[i] ?? "") : "";
                if (v.Trim().Length == 0) { emptyHit.Add(c); continue; }   // 有这一项但空值＝继续兜底

                string t = i < Types.Count ? (Types[i] ?? "") : "";
                string u = i < Units.Count ? (Units[i] ?? "") : "";
                return new BomHit
                {
                    Found = true,
                    Value = v.Trim(),
                    Label = c,
                    Mode = mode,
                    Note = "类型=" + (t.Length == 0 ? "<空>" : t)
                         + "｜单位=" + (u.Length == 0 ? "<空>" : u)
                         + "｜plCount=" + PlCount
                };
            }

            string why = "候选在 BOM 行里都没有"
                + (emptyHit.Count > 0 ? "（有名称但值为空：" + string.Join("、", emptyHit.ToArray()) + "）" : "")
                + "｜labels=" + AllLabels;
            return new BomHit { Note = why };
        }

        private int IndexOf(string field, bool ignoreCase)
        {
            if (string.IsNullOrEmpty(field)) return -1;
            for (int i = 0; i < Labels.Count; i++)
                if (string.Equals(Labels[i], field, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return i;
            return -1;
        }
    }

    /// <summary>BOM 快照的单次命中结果。</summary>
    [ComVisible(false)]
    internal sealed class BomHit
    {
        public bool Found;
        public string Value = "";
        public string Label = "";
        public string Mode = "";
        public string Note = "";
    }

    [ComVisible(false)]
    internal static class BomExportReader
    {
        // 本次进程第一次成功调用把 labels 全量落 trace（caxa3dBatchMaster 的坑：截断上限导致
        // 「集合里到底有没有某个键」读不到）。之后每个元素只记计数，避免大装配刷爆日志。
        private static bool _labelsDumped;
        // 首调空手而归（BOM 引擎冷初始化）只重试一次，口径见文件头注释。
        private static bool _coldRetryUsed;

        /// <summary>取一个元素的整行 BOM 数据（一次宿主调用，所有候选列复用）。失败＝第三态，不返回 null 而是 Queried=false＋Note。</summary>
        public static BomSnapshot GetSnapshot(IZElement elem)
        {
            var snap = new BomSnapshot();
            if (elem == null) { snap.Note = "元素为空"; return snap; }

            IZSceneElement se = elem as IZSceneElement;
            if (se == null) { snap.Note = "元素 QI 不到 IZSceneElement 表面"; return snap; }

            SnapshotOnce(se, snap);

            if (snap.Queried && snap.Labels.Count == 0 && !_coldRetryUsed)
            {
                _coldRetryUsed = true;
                Diagnostics.Trace("ExportSelfBOM 首取 0 项，重试一次（BOM 引擎冷初始化嫌疑，caxa3dBatchMaster 同族现象）");
                SnapshotOnce(se, snap);
            }
            return snap;
        }

        private static void SnapshotOnce(IZSceneElement se, BomSnapshot snap)
        {
            int count = 0;
            object labels = null, values = null, types = null, units = null;
            try
            {
                se.ExportSelfBOM(out count, out labels, out values, out types, out units);
            }
            catch (Exception ex)
            {
                snap.Queried = false;
                snap.Note = "调用异常 hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message;
                Diagnostics.Trace("ExportSelfBOM EX: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                return;
            }

            snap.Labels = ToStringList(CaxaService.UnpackArray(labels));
            snap.Values = ToStringList(CaxaService.UnpackArray(values));
            snap.Types = ToStringList(CaxaService.UnpackArray(types));
            snap.Units = ToStringList(CaxaService.UnpackArray(units));
            snap.PlCount = count;
            snap.AllLabels = Join(snap.Labels);
            snap.Queried = true;
            snap.Note = "";

            if (!_labelsDumped && snap.Labels.Count > 0)
            {
                _labelsDumped = true;
                Diagnostics.Trace("ExportSelfBOM labels 全集（本次运行首次，共 " + snap.Labels.Count + " 项）：" + string.Join(" | ", snap.Labels.ToArray()));
            }
            Diagnostics.Trace(string.Format(
                "ExportSelfBOM: plCount={0} label={1} value={2} type={3} unit={4}",
                count, snap.Labels.Count, snap.Values.Count, snap.Types.Count, snap.Units.Count));
        }

        private static string Join(List<string> ls)
        {
            if (ls == null || ls.Count == 0) return "<无>";
            return string.Join(" | ", ls.ToArray());
        }

        private static List<string> ToStringList(List<object> raw)
        {
            var list = new List<string>();
            if (raw == null) return list;
            foreach (object o in raw) list.Add(Text(o));
            return list;
        }

        private static string Text(object o)
        {
            if (o == null) return "";
            try
            {
                if (o is IConvertible) return Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
                return o.ToString() ?? "";
            }
            catch { return ""; }
        }
    }
}
