// 「重量」等 BOM 引擎字段的读取通道（11-06 用户指令 1）。
//
// 用户实机结论（本轮输入原文要点）：PartMass＝质量数值本身、PartMassType＝质量类型（计算 vs 指定），
// 「质量」只是 BOM 字典 IC_BOM_DIC.xml 给 PartMass 起的中文显示名；这族字段归 BOM 引擎（ISEngineAfx.dll）
// 与零件属性子系统（3isd31.dll），**不在自定义属性管理器里** ⇒ 共享表 GetCustomPropAsString("PartMass") 必然空。
// 所以属性批量读取遇到这族字段必须换通道，不再算「没这个属性」。
//
// 官方字典原文（本机实测，2024 与 2027 同一份内容，共 4 个位置）：
//   C:\Program Files\CAXA\CAXA 3D\<年>\bin\CAXADraft\Config\zh-CN\IC_BOM_DIC.xml  …\AppData\zh-cn\3DBOM\IC_BOM_DIC.xml
//     <Key Name="PartMass"      Description="质量"/>
//     <Key Name="PartMassType"   Description="质量来源"/>
//     <Key Name="MassDensity"    Description="密度"/>
//     <Key Name="PartSpecMass"   Description="用户指定质量"/>
//   ⇒ 冲突留痕：用户把 PartMass 的列名定为「重量」，官方字典写「质量」；按规则「判定类规则以用户定义为准」，
//     映射文件仍播种 `重量=PartMass`，本文件只负责「读到值」，不负责改列名。
//
// 通道阶梯（上一级能成不用下一级；两条都是 TLB 实测成员，2024/2027 同值、本工程 interop v26 全 PRESENT）：
//   ① BOM 导出（用户指定通道）：IZSceneElement.ExportSelfBOM —— memid 30 / oVft 368 / 槽 46，
//      签名 `void ExportSelfBOM(Int32& plCount, Object& Label, Object& Value, Object& Type, Object& Unit)`，
//      5 个 out ⇒ 规则「≥2 个 out 走 RCW」，本工程 interop v26 的 RCW 形态＝`void ExportSelfBOM(out int, out object, out object, out object, out object)`
//      （★ 踩坑留痕：`caxa-interop-check` 只答 PRESENT/MISSING 不答 by-ref 关键字；自写反射探针里 `if(IsOut) "out"` 被后面
//        `if(IsByRef) "ref"` 覆盖 ⇒ 打印成 ref、编译报 CS1620。判 out/ref 用**编译器**，别用自己的打印。）
//      它给的就是「CAXA 自己算出来的那一行 BOM 数据」（名称/值/类型/单位），所以是最忠实的一级。
//   ② 质量属性直读（校验/回落）：IZPartProperty / IZAssemblyProperty 的
//      CalculatedMass 槽 16、SpecifiedMass 槽 14、MassType 槽 19、MassDensity 槽 7、GetMassProperties 槽 11；
//      仓内先例＝caxa3dsetMaterial\MaterialProbe.cs:311,328-329（`elem as IZPartProperty` 后直取，实机跑通）。
//   ③ 两条都不给值＝第三态「读不到」，如实写日志，绝不写空串盖掉用户已有内容（与属性读取链同一口径）。
//
// ★ 本轮之前**没有任何项目调用过 ExportBOM/ExportSelfBOM**（全仓 grep 0 命中），也没进过已否决清单。
//   所以 ① 的返回形态（数组下界、元素是 string 还是 double、label 的确切拼写）是**待实机取证**项：
//   本文件把「拿到的全部 label 原文」写进 trace，实机第一轮就能定案，不需要再猜第二轮。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    /// <summary>一次 BOM 读取的结论（三态分开：命中 / 走了哪级通道 / 读不到）。</summary>
    [ComVisible(false)]
    internal sealed class BomReadResult
    {
        public bool Found;
        public string Value = "";
        public string Channel = "";     // BOM导出(ExportSelfBOM) / 质量属性(CalculatedMass) / ...
        public string Note = "";        // 给人看的一句话（含单位/原始 label 等取证信息）
        public string Describe()
        {
            return Found ? (Channel + "：" + Value + (string.IsNullOrEmpty(Note) ? "" : "｜" + Note))
                         : "读不到（" + Channel + "）" + (string.IsNullOrEmpty(Note) ? "" : "｜" + Note);
        }
    }

    /// <summary>
    /// 已知属于 BOM 引擎通道的内部字段（用户可编辑，规则 14.8）。
    /// 生效路径与映射文件同一套口径（UserRuleFiles：先 %APPDATA%\caxa3dBatchMaster\，程序目录只做播种源）。
    /// 现在读取已统一为「所有非固定列一律 ① 自定义属性共享表 → ② BOM 导出兜底」：本清单不再决定「跳不跳共享表」，
    /// 而是用于「列头注明（提示该列值来自 BOM）」与「质量属性直读这一最后兜底只对清单内字段生效」。
    /// 没列在这里的自定义列：仍走 ①→②，只是最后那道质量属性直读网不触发。
    /// </summary>
    internal static class BomFieldList
    {
        public const string FileName = "caxa3dBatchMasterBOM字段清单.txt";

        private const string Seed =
"# caxa3dBatchMaster BOM 引擎字段清单：一行一个「内部字段名」，这些列已知走 BOM 引擎通道。\r\n" +
"# 读取顺序（用户指令 3「修正」）：所有非固定列一律先试自定义属性共享表（①），共享表读不到再走 BOM 导出兜底（②）。\r\n" +
"# 本清单的作用＝① 列头注明「本列值来自 BOM」；② 让「质量属性直读」这最后一道兜底只对清单内字段生效。\r\n" +
"# 依据（用户实机结论 11-06）：PartMass/PartMassType 属于 BOM 引擎与零件属性子系统，不在自定义属性管理器里。\r\n" +
"# 字段名照官方字典 IC_BOM_DIC.xml 的 Name 原样抄（区分大小写），改完保存即生效（按文件修改时间热重载）。\r\n" +
"# 空行与 # / // 开头的行忽略。\r\n" +
"PartMass\r\n" +
"PartMassType\r\n" +
"PartSpecMass\r\n" +
"MassDensity\r\n" +
"日期\r\n" +            // 用户指令 2：日期列与「重量」同路，先试共享表、共享表空再走 BOM 导出兜底
"Date\r\n" +
"PartDate\r\n";

        private static HashSet<string> _set;
        private static string _path;
        private static DateTime _stamp = DateTime.MinValue;
        private static string _note = "";

        public static string EffectivePath { get { try { ReloadIfChanged(); } catch { } return _path ?? "<不可得>"; } }
        public static string LastNote { get { return _note; } }

        public static string ReloadIfChanged(bool force = false)
        {
            string known = _path;
            DateTime oldStamp = _stamp;
            DateTime stamp = DateTime.MinValue;
            if (known != null && File.Exists(known)) { try { stamp = File.GetLastWriteTime(known); } catch { stamp = DateTime.MinValue; } }
            if (!force && _set != null && stamp != DateTime.MinValue && stamp == oldStamp) return null;

            string note;
            string path = UserRuleFiles.Ensure(FileName, Seed, out note);
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (path != null && File.Exists(path))
                    foreach (string raw in File.ReadAllLines(path, System.Text.Encoding.UTF8))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)
                            || line.StartsWith("//", StringComparison.Ordinal)) continue;
                        set.Add(line);
                    }
            }
            catch (Exception ex) { note = "读取 BOM 字段清单失败：" + ex.Message + "｜" + note; set.Clear(); }
            if (set.Count == 0)
            {
                foreach (string line in Seed.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                    set.Add(t);
                }
                note += "｜清单为空，本轮用内置 4 条（PartMass/PartMassType/PartSpecMass/MassDensity）";
            }

            DateTime newStamp = DateTime.MinValue;
            if (path != null && File.Exists(path)) { try { newStamp = File.GetLastWriteTime(path); } catch { } }
            bool changed = _set == null || _set.Count != set.Count || newStamp != oldStamp || path != known;
            _set = set; _path = path; _stamp = newStamp; _note = note;
            return force || changed ? ("BOM 字段清单：" + note + "｜生效条数 " + set.Count) : null;
        }

        public static bool Contains(string field)
        {
            if (string.IsNullOrWhiteSpace(field)) return false;
            try { ReloadIfChanged(); } catch { }
            var s = _set;
            if (s == null) return false;
            return s.Contains(field.Trim());
        }

        public static string InitAndDescribe()
        {
            string text = ReloadIfChanged(true);
            return text ?? ("BOM 字段清单：生效路径＝" + EffectivePath);
        }
    }

    /// <summary>给表格列标题/日志用的「来源注明」（用户指令 1：重量列要特别注明从 BOM 导出）。</summary>
    internal static class BomFieldNotes
    {
        public const string Tip = "本列从 BOM 引擎导出，不走自定义属性共享表｜重量=PartMass（计算重量）";

        /// <summary>表头显示名 → 候选内部字段 → **任一**候选命中 BOM 清单才给注明；否则返回空串。</summary>
        public static string ToolTipFor(string header)
        {
            if (string.IsNullOrWhiteSpace(header)) return "";
            foreach (string c in PropFieldMap.Candidates(header.Trim()))
                if (BomFieldList.Contains(c)) return Tip;
            return "";
        }

        /// <summary>本轮实际按 BOM 通道读的列（进日志，用户要看得见「哪些列走了哪条道」）。</summary>
        public static string OfFields(ICollection<string> fields)
        {
            if (fields == null || fields.Count == 0) return "无";
            return string.Join("、", new List<string>(fields).ToArray());
        }
    }

    /// <summary>BOM 引擎字段的取值阶梯（① ExportSelfBOM → ② 质量属性直读 → ③ 第三态）。</summary>
    [ComVisible(false)]
    internal static class BomValueReader
    {
        // ==================================================== ① BOM 导出（用户指定通道，整行快照可复用）

        /// <summary>一次 ExportSelfBOM 返回的整行 BOM 数据（名称/值/类型/单位），解包后供「按候选多次匹配」复用，
        /// 避免每个自定义属性列都重算一次 BOM（大装配逐列重算会拖死主线程）。</summary>
        internal sealed class BomSnapshot
        {
            public bool Queried;                       // 是否成功向宿主要过一次 ExportSelfBOM 返回（哪怕 label 为空）
            public string Note = "";
            public List<object> Labels = new List<object>();
            public List<object> Values = new List<object>();
            public List<object> Types = new List<object>();
            public List<object> Units = new List<object>();
            public int PlCount;
            public string AllLabels = "<无>";

            /// <summary>在 BOM 行里找一个字段（精确同名 → 大小写不敏感；不拼候选名、不做模糊匹配，规则 14.1/14.2）。</summary>
            public BomReadResult Find(string field)
            {
                var r = new BomReadResult { Channel = "①BOM导出(ExportSelfBOM)" };
                if (!Queried) { r.Note = Note; return r; }
                if (Labels.Count == 0)
                {
                    r.Note = "plCount=" + PlCount + " 但 label 数组解包为 0 个（返回形态待实机定案，见 trace）";
                    return r;
                }
                int hit = IndexOf(field, false);
                string mode = "精确同名";
                if (hit < 0) { hit = IndexOf(field, true); mode = "大小写不敏感命中"; }
                if (hit < 0) { r.Note = "BOM 行里没有「" + field + "」（拿到名称：" + AllLabels + "）"; return r; }
                r.Found = true;
                r.Value = hit < Values.Count ? Text(Values[hit]) : "";
                r.Channel = "①BOM导出(ExportSelfBOM·" + mode + ")";
                string t = hit < Types.Count ? Text(Types[hit]) : "";
                string u = hit < Units.Count ? Text(Units[hit]) : "";
                r.Note = "类型=" + (string.IsNullOrEmpty(t) ? "<空>" : t)
                       + "｜单位=" + (string.IsNullOrEmpty(u) ? "<空>" : u)
                       + "｜plCount=" + PlCount;
                return r;
            }

            /// <summary>候选列表里找第一个命中（「自定义表没命中→BOM 兜底」用：依次试全部候选，命中即停）。</summary>
            public BomReadResult FindBest(string[] fields, out string matched)
            {
                matched = "";
                BomReadResult best = null;
                foreach (string f in fields)
                {
                    BomReadResult b = Find(f);
                    if (b.Found && (best == null || string.IsNullOrEmpty(best.Value))) { best = b; matched = f; }
                    if (best != null && !string.IsNullOrEmpty(best.Value)) { matched = f; break; }
                }
                return best ?? new BomReadResult
                {
                    Channel = "①BOM导出(ExportSelfBOM·兜底)",
                    Note = "候选在 BOM 均未命中（label：" + AllLabels + "）"
                };
            }

            private int IndexOf(string field, bool ignoreCase)
            {
                if (string.IsNullOrEmpty(field)) return -1;
                for (int i = 0; i < Labels.Count; i++)
                    if (string.Equals(Text(Labels[i]), field, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        return i;
                return -1;
            }
        }

        /// <summary>取一个元素整行 BOM 数据（① ExportSelfBOM），解包成可复用快照；只调一次宿主，后续按候选匹配。</summary>
        public static BomSnapshot GetBomSnapshot(IZElement elem)
        {
            var snap = new BomSnapshot();
            IZSceneElement se = elem as IZSceneElement;
            if (se == null) { snap.Note = "元素 QI 不到 IZSceneElement 表面"; return snap; }

            int count = 0;
            object labels = null, values = null, types = null, units = null;
            try
            {
                se.ExportSelfBOM(out count, out labels, out values, out types, out units);
            }
            catch (Exception ex)
            {
                snap.Note = "调用异常 hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message;
                Diagnostics.Trace("ExportSelfBOM EX: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                return snap;
            }

            snap.Labels = CaxaBatchService.UnpackArray(labels);
            snap.Values = CaxaBatchService.UnpackArray(values);
            snap.Types = CaxaBatchService.UnpackArray(types);
            snap.Units = CaxaBatchService.UnpackArray(units);
            snap.PlCount = count;
            snap.AllLabels = JoinLabels(snap.Labels);
            snap.Queried = true;
            Diagnostics.Trace(string.Format(
                "ExportSelfBOM: plCount={0} label={1} value={2} type={3} unit={4}｜labels={5}",
                count, snap.Labels.Count, snap.Values.Count, snap.Types.Count, snap.Units.Count, snap.AllLabels));
            return snap;
        }

        /// <summary>已知 BOM 质量字段（PartMass 等）：按候选顺序逐个试「质量属性直读」通道（②），命中即停。
        /// 仅覆盖 PartMass/PartSpecMass/PartMassType/MassDensity（见 <see cref="TryMassProperty"/>）；
        /// 这是 BOM 快照兜底（FindBest）之后的最后一道网，防个别元素 ExportSelfBOM 偶发缺该 label。
        /// 不重新 ExportSelfBOM，只是一次轻量 COM 直读。</summary>
        internal static BomReadResult TryMassPropertyCandidates(IZElement elem, string[] cands, out string matchedField)
        {
            matchedField = "";
            foreach (string c in cands)
            {
                BomReadResult p = TryMassProperty(elem, c);
                if (p.Found) { matchedField = c; return p; }
            }
            return new BomReadResult { Channel = "②质量属性", Note = "候选在质量属性通道均未命中（本通道只覆盖 PartMass/PartSpecMass/PartMassType/MassDensity）" };
        }

        private static string JoinLabels(List<object> ls)
        {
            if (ls == null || ls.Count == 0) return "<无>";
            var parts = new List<string>();
            for (int i = 0; i < ls.Count && i < 40; i++) parts.Add(Text(ls[i]));
            if (ls.Count > 40) parts.Add("…共 " + ls.Count + " 个");
            return string.Join(" | ", parts.ToArray());
        }

        // ==================================================== ② 质量属性直读（回落/校验）

        /// <summary>
        /// `elem as IZPartProperty` / `as IZAssemblyProperty` 后直取属性（仓内先例 MaterialProbe.cs:311,328-329）。
        /// 字段对应：PartMass→CalculatedMass(槽16)、PartSpecMass→SpecifiedMass(槽14)、
        /// MassDensity→MassDensity(槽7)、PartMassType→MassType(槽19，eZMassType：0=Z_MT_Calculated、1=Z_MT_Specified，enums.csv 实测)。
        /// </summary>
        private static BomReadResult TryMassProperty(IZElement elem, string field)
        {
            var r = new BomReadResult { Channel = "②质量属性" };
            IZPartProperty pp = elem as IZPartProperty;
            if (pp != null)
            {
                try
                {
                    switch (field)
                    {
                        case "PartMass": return MassOk(ref r, Num(pp.CalculatedMass), "CalculatedMass（槽 16）");
                        case "PartSpecMass": return MassOk(ref r, Num(pp.SpecifiedMass), "SpecifiedMass（槽 14）");
                        case "PartMassType": return MassOk(ref r, MassTypeText(pp.MassType), "MassType（槽 19）");
                        case "MassDensity": return MassOk(ref r, Num(pp.MassDensity), "MassDensity（槽 7）");
                    }
                }
                catch (Exception ex) { r.Note = "IZPartProperty 异常 hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message; return r; }
                r.Note = "IZPartProperty 表面里没有「" + field + "」这条（本通道只覆盖 PartMass/PartSpecMass/PartMassType/MassDensity）";
                return r;
            }

            IZAssemblyProperty ap = elem as IZAssemblyProperty;
            if (ap == null) { r.Note = "元素既不是 IZPartProperty 也不是 IZAssemblyProperty（不猜第三个表面）"; return r; }
            try
            {
                switch (field)
                {
                    case "PartMass": return MassOk(ref r, Num(ap.CalculatedMass), "Assembly.CalculatedMass（槽 14）");
                    case "PartSpecMass": return MassOk(ref r, Num(ap.SpecifiedMass), "Assembly.SpecifiedMass（槽 12）");
                    case "PartMassType": return MassOk(ref r, MassTypeText(ap.MassType), "Assembly.MassType（槽 17）");
                }
            }
            catch (Exception ex) { r.Note = "IZAssemblyProperty 异常 hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message; return r; }
            r.Note = "装配体表面没有「" + field + "」这条（IZAssemblyProperty 无 MassDensity 声明）";
            return r;
        }

        private static BomReadResult MassOk(ref BomReadResult r, string value, string src)
        {
            r.Found = true;
            r.Value = value;
            r.Channel = "②质量属性(" + src + ")";
            r.Note = "来源＝" + src + "｜数值按 COM 原样换算输出，单位未在界面核对【待验】";
            return r;
        }

        /// <summary>eZMassType → 用户界面上的两种叫法（枚举名是实测的，中文叫法取自用户本轮输入「计算 vs 指定」）。</summary>
        private static string MassTypeText(eZMassType t)
        {
            if (t == eZMassType.Z_MT_Calculated) return "计算";
            if (t == eZMassType.Z_MT_Specified) return "指定";
            return "未知(" + (int)t + ")";
        }

        private static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "<非数值>";
            return d.ToString("0.######", CultureInfo.InvariantCulture);
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
