// 装配体排序（用户 2026-11-13 需求；第四版 = 排序根改「选中装配体」）
//
// ★ 14:16 实机 trace 定案的语义（用户口径）★
//   「只排第一层」= **选中装配体的直接子零部件**排序，不是场景顶层；
//   「穿透到底」= 在同一个根上逐层进入子装配。
//   定案依据：设计树形态是「全局坐标系 + 唯一顶层装配」，`IZSceneDoc.GetChildElements` 只回 1 项（那 1 个装配），
//   所以在场景根上「只排第一层」恒为 0 个零件 ⇒ 旧版必然「没反应」；而「穿透到底」自动进到该装配内部所以正常。
//   根的取法（ResolveRoot，逐级回落，每级一行 trace）：
//     ① 选中集里有装配体(type=2) ⇒ 用它（多个取第一个并点名）；
//     ② 没选中 ⇒ 场景第一层只有唯一装配体时自动进入；
//     ③ 否则 ⇒ 场景根（排第一层）。
//   多通道读同级已于 14:16 实机验证：A GetChildren(13) / B / C / D / E 五路全回 **114 项**（与设计树一致），
//   旧 F 链只回 4 项 ⇒ 漏节点坐实（坑 A8）。
//
// ★ 11-13 14:00 实机 trace 定案的缺陷（不是写通道的问题，是读通道的问题）★
//   现象：用户明确「同级有多个零部件（≥2）」，但 trace 每层只读到 1 个零件：
//     ---- 层级 1 父「SC08020103.00 P80油槽」同级 4 项 ----
//     不参与（非零部件）「板料」type=21 / 「折弯」type=22 / 「造型1」type=4 / 「」type=19
//     参与排序的零部件 1 项 ⇒ 「SC08020102.01-01 折弯板」
//     ⇒ 「少于 2 项，无需排序」×每层 ⇒ 实际移动 0 次（写通道 MoveToReferenceLocation 从未被调用）
//   根因：旧版读同级只走 IZElement.GetFirstChild(19)/GetNextChild(20) 这条链，它**漏节点**
//         （caxa3dmcp\ToolsBom.cs 的 BOM 树遍历实证用的是 IZElement.GetChildren(13)，
//           返回 ZArray 且含外链零件；GetFirstChild 链对链接/实例子件不完整）。
//   解法：同级读取改为**多通道对照 + 取最长**，逐通道打项数进 trace：
//     A IZElement.GetChildren(13) → Object（ZArray 或 object[]）
//     B IZElement.GetChildrenZArray(21) → ZArray
//     C IZElement.GetChildrenEx(0)  (32，eZElemExclude Z_EXCL_NONE=0)
//     D IZAssembly.GetChildren(11) / E IZAssembly.GetChildrenZArray(24)（父是装配时）
//     F IZElement.GetFirstChild(19)/GetNextChild(20) 链（旧通道，降为兜底，仍记账）
//     交叉校验 IZElement.GetChildrenCount(15) / IZSceneDoc.GetChildrenElementsCount(18)，
//     计数与数组不一致时打 ★ 行，但**以通道实际数组为准**。
//
// ★ 写侧（未变）★
//   IZElement.MoveToReferenceLocation(IZElement ref, Boolean vbAfter)＝槽 39 / memid 31，v26 interop PRESENT，
//   官方原文「piReferenceElement must be in the same level of this element. Does not work for features.」
//   判成功＝每步移动后重读同级（同一通道），第 k 个参与元素必须变成目标元素；首步未变即判通道死并中止。
//
// ★ 参与成员口径（14:2x 用户口径：装配、零件、钣金等都算零部件）★
//   参与 = 装配(2) / 零件(1) / 钣金件(20) / 多面体(25) / 线架(28) / 型材(29)；
//   不参与 = 装配特征容器(39)、板料(21)、折弯(22)、造型/轮廓(19)、各类特征(3~17) 与坐标系，只占位不下场，
//   故「第 k 个参与元素」的下标会漂移 —— 定位一律按 Id 认成员，不按固定槽位。
//
// ★ 日志体积 ★
//   14:24 那轮 3.2 MB（99 次移动 × 每步重读 6 通道全打）⇒ 通道明细只在每层首次读取时打，
//   重排过程中的重读静默（`verbose=false`），失败行仍照打。
//
// ★ 第三态纪律 ★
//   通道读不到 / Type 读不到 / Id 读不到一律写 <读不到> 并单独记账；截断计 Truncated。
using System;
using System.Collections.Generic;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal static class AssemblySortTool
    {
        // eZElementType（_probe-tools\out\tlb\2027\enums.csv）
        private const int Z_ELEMENT_PART = 1;
        private const int Z_ELEMENT_ASSEMBLY = 2;
        private const int Z_ELEMENT_SHEETMETAL_PART = 20;
        private const int Z_ELEMENT_FACET_PART = 25;
        private const int Z_ELEMENT_WIRE_PART = 28;
        private const int Z_ELEMENT_PROFILE_PART = 29;

        private const int MaxDepth = 32;
        private const int MaxChildren = 4000;
        private const int MaxNodes = 12000;

        internal sealed class Options
        {
            public bool Ascending = true;         // 与降序互斥
            public bool PenetrateAll = false;     // false=只排第一层（选中装配体的直接子零部件）/ true=穿透到底（互斥）
        }

        private sealed class Report
        {
            public int Levels;
            public int Parts;
            public int Moves;
            public int Failures;
            public int Truncated;
            public int Nodes;
            public bool ChannelDead;
            public string ChannelReason = "";
            public string RootName = "<场景根>";
            public readonly List<string> SortedPreview = new List<string>();
            public readonly List<IZElement> TouchedAssemblies = new List<IZElement>();
        }

        // ================================================================ 入口

        /// <summary>执行排序，返回中文判词；全程落 trace。UI 线程同步跑（本工程探针口径）。</summary>
        public static string Apply(IZBaseApp app, Options opt, Action<string> log)
        {
            const string tag = "asm-sort";
            if (log == null) log = m => Diagnostics.Log(m);
            if (opt == null) opt = new Options();

            log(tag + ": ==== 装配体排序 ==== 全零部件 方向=" + (opt.Ascending ? "升序" : "降序") +
                " 穿透=" + (opt.PenetrateAll ? "到底" : "第一层（选中装配体的直接子零部件）"));

            string err;
            IZSceneDoc scene = ProbeCommon.GetScene(app, out err);
            if (scene == null) return "装配体排序中止：" + err;

            var rep = new Report();
            IZElement root = ResolveRoot(scene, rep, log);
            SortLevel(scene, root, 0, opt, rep, log);

            if (!rep.ChannelDead) RefreshScene(scene, rep, log);

            return Verdict(rep, opt);
        }

        /// <summary>
        /// 排序根（＝要排其直接子零部件的那一级）：
        /// ① 当前选中里有装配体（type=2）⇒ 用它（多个时取选择集里的第一个，并点名）；
        /// ② 没选中装配体 ⇒ 场景第一层只有一个装配体时自动进入它（设计树常见形态：全局坐标系 + 唯一顶层装配）；
        /// ③ 其余情况 ⇒ 场景根（排第一层）。
        /// 「只排第一层」在这个根上只排它的直接子零部件，不下钻；「穿透到底」再逐层进入子装配。
        /// </summary>
        private static IZElement ResolveRoot(IZSceneDoc scene, Report rep, Action<string> log)
        {
            const string tag = "asm-sort";
            List<object> sel = ProbeCommon.ReadSelectionRaw(scene, tag, log);
            IZElement chosen = null;
            int nSel = 0, nAsm = 0;
            foreach (object o in sel)
            {
                IZElement e = o as IZElement;
                if (e == null) continue;
                nSel++;
                if (SafeType(e) != Z_ELEMENT_ASSEMBLY) continue;
                nAsm++;
                if (chosen == null) chosen = e;
            }
            if (chosen != null)
            {
                rep.RootName = "「" + SafeName(chosen) + "」（选中装配体）";
                log(tag + ": 排序根＝选中的装配体「" + SafeName(chosen) + "」，选中 " + nSel + " 个对象、其中装配体 " + nAsm +
                    " 个" + (nAsm > 1 ? "，取选择集里第一个" : ""));
                return chosen;
            }
            if (nSel > 0)
                log(tag + ": 选中 " + nSel + " 个对象但都不是装配体（type≠2）⇒ 退回场景第一层");

            bool readOk;
            List<IZElement> top = ReadLevel(scene, null, log, out readOk, true);
            if (readOk && top.Count == 1 && SafeType(top[0]) == Z_ELEMENT_ASSEMBLY)
            {
                rep.RootName = "「" + SafeName(top[0]) + "」（场景唯一顶层装配，未选中自动进入）";
                log(tag + ": 未选中装配体 ⇒ 自动进入场景唯一顶层装配「" + SafeName(top[0]) + "」");
                return top[0];
            }
            log(tag + ": 排序根＝场景根（第一层 " + (readOk ? top.Count.ToString() : "<读不到>") + " 项）");
            return null;
        }

        private static string Verdict(Report rep, Options opt)
        {
            var sb = new System.Text.StringBuilder();
            if (rep.ChannelDead)
            {
                sb.Append("装配体排序未生效：").Append(rep.ChannelReason)
                  .Append("。已中止，未继续对其余节点重复调用（详见 trace）。");
                return sb.ToString();
            }

            sb.Append("装配体排序完成：排序根").Append(rep.RootName)
              .Append(" / 层级 ").Append(rep.Levels)
              .Append(" 层 / 参与零部件 ").Append(rep.Parts)
              .Append(" 项 / 实际移动 ").Append(rep.Moves).Append(" 次");
            if (rep.Failures > 0) sb.Append("；回读不一致 ").Append(rep.Failures).Append(" 处");
            if (rep.Truncated > 0) sb.Append("；★遍历截断 ").Append(rep.Truncated).Append(" 处（结果不完整）");
            if (rep.SortedPreview.Count > 0)
                sb.Append("；首层目标顺序：").Append(string.Join("、", rep.SortedPreview.ToArray()));
            sb.Append("。判据＝每步移动后重读同级链，不认 HR。请在 CAXA 设计树中确认显示顺序。");
            return sb.ToString();
        }

        // ================================================================ 分层排序

        /// <summary>
        /// 排一层并（穿透到底时）下钻子装配。parent=null 表示场景根。
        /// </summary>
        private static void SortLevel(IZSceneDoc scene, IZElement parent, int depth, Options opt,
            Report rep, Action<string> log)
        {
            const string tag = "asm-sort";
            if (rep.ChannelDead) return;
            if (depth > MaxDepth) { rep.Truncated++; log(tag + ": 深度超过 " + MaxDepth + "，截断"); return; }
            if (rep.Nodes >= MaxNodes)
            {
                rep.Truncated++;
                log(tag + ": 节点数撞预算 " + MaxNodes + "（截断单独记账），本层不再处理");
                return;
            }
            if (parent != null) AddTouched(rep, parent);

            bool readOk;
            List<IZElement> kids = ReadLevel(scene, parent, log, out readOk, true);
            if (!readOk)
            {
                log(tag + ": 同级链读不到（第三态），本层跳过");
                return;
            }
            if (kids.Count >= MaxChildren) { rep.Truncated++; log(tag + ": 同级超过 " + MaxChildren + " 项，截断单独记账"); }

            rep.Nodes += kids.Count;
            rep.Levels++;
            log(tag + ": ---- 层级 " + depth + " 父「" + (parent == null ? "<场景根>" : SafeName(parent)) +
                "」同级 " + kids.Count + " 项 ----");

            var participants = new List<IZElement>();
            var memberIds = new HashSet<int>();
            foreach (IZElement k in kids)
            {
                if (!IsComponent(k, log))
                {
                    log(tag + ": 不参与（特征/板料/折弯等）「" + SafeName(k) + "」type=" + SafeType(k));
                    continue;
                }
                int id = SafeId(k);
                if (id >= 0 && memberIds.Contains(id))
                {
                    log(tag + ": 「" + SafeName(k) + "」Id=" + id + " 在本层重复出现，只计一次");
                    continue;
                }
                participants.Add(k);
                if (id >= 0) memberIds.Add(id);
            }
            rep.Parts += participants.Count;
            log(tag + ": 参与排序的零部件（装配+零件+钣金等）" + participants.Count + " 项 ⇒ " + Names(participants));

            if (participants.Count < 2)
            {
                log(tag + ": 少于 2 项，无需排序");
            }
            else
            {
                List<IZElement> sorted = SortByName(participants, opt.Ascending, log);
                if (rep.SortedPreview.Count == 0)
                    foreach (IZElement p in sorted)
                        if (rep.SortedPreview.Count < 30) rep.SortedPreview.Add(SafeName(p));
                ReorderSiblings(scene, parent, sorted, memberIds, rep, log);
            }

            if (opt.PenetrateAll && !rep.ChannelDead)
            {
                List<IZElement> after = ReadLevel(scene, parent, log, out readOk, false);
                if (!readOk)
                {
                    log(tag + ": 子级重读失败（第三态），本层不再下钻");
                    return;
                }
                foreach (IZElement k in after)
                {
                    if (rep.ChannelDead) break;
                    if (IsAssembly(k, log)) SortLevel(scene, k, depth + 1, opt, rep, log);
                }
            }
        }

        /// <summary>按 Name 排序；同名用原始次序做稳定 tie-break；中文用当前区域比较（拼音习惯）。</summary>
        private static List<IZElement> SortByName(List<IZElement> src, bool ascending, Action<string> log)
        {
            var idx = new List<int>();
            for (int i = 0; i < src.Count; i++) idx.Add(i);
            var names = new string[src.Count];
            for (int i = 0; i < src.Count; i++) names[i] = SafeName(src[i]);
            int sign = ascending ? 1 : -1;
            idx.Sort(delegate (int a, int b)
            {
                int c = string.Compare(names[a], names[b], StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c * sign;
                return a.CompareTo(b);   // 同名保持原始次序，降序也稳定
            });
            var result = new List<IZElement>();
            foreach (int i in idx) result.Add(src[i]);
            log("asm-sort: 排序键=IZElement.Name，" + (ascending ? "升序" : "降序") + " ⇒ " + Names(result));
            return result;
        }

        /// <summary>
        /// 选择排序式重排：第 k 轮把 sorted[k] 挪到「当前同级里第 k 个参与元素」的位置之前。
        /// 每轮重读实际同级（同一通道），不依赖固定槽位——夹杂不参与的子装配/特征时槽位会漂移。
        /// vbAfter=false ⇒ 移到参照之前（官方语义），参照与自身同级。
        /// </summary>
        private static void ReorderSiblings(IZSceneDoc scene, IZElement parent, List<IZElement> sorted,
            HashSet<int> memberIds, Report rep, Action<string> log)
        {
            const string tag = "asm-sort";
            for (int k = 0; k < sorted.Count; k++)
            {
                bool ok;
                List<IZElement> cur = ReadLevel(scene, parent, log, out ok, false);
                if (!ok) { FailChannel(rep, log, "第 " + k + " 轮同级链读不到（第三态）"); return; }

                int slot = IndexOfKthMember(cur, memberIds, k, log);
                if (slot < 0) { FailChannel(rep, log, "第 " + k + " 轮找不到第 " + k + " 个参与元素"); return; }
                if (SameElement(cur[slot], sorted[k], log))
                {
                    log(tag + ": 第 " + k + " 项「" + SafeName(sorted[k]) + "」已在目标位，跳过");
                    continue;
                }

                string refName = SafeName(cur[slot]);
                try
                {
                    sorted[k].MoveToReferenceLocation(cur[slot], false);
                    rep.Moves++;
                    log(tag + ": 第 " + k + " 项「" + SafeName(sorted[k]) + "」→ 移到「" + refName + "」之前（vbAfter=false）");
                }
                catch (Exception ex)
                {
                    FailChannel(rep, log, "MoveToReferenceLocation 抛异常 " + ProbeCommon.Hr(ex) +
                        "：" + ex.Message + "（官方限制：参照必须同级、对特征无效）");
                    return;
                }

                // 权威判据：回读顺序必须真的变了
                bool ok2;
                List<IZElement> after = ReadLevel(scene, parent, log, out ok2, false);
                int slotAfter = ok2 ? IndexOfKthMember(after, memberIds, k, log) : -1;
                bool changed = ok2 && after.Count == cur.Count && slotAfter >= 0 &&
                               SameElement(after[slotAfter], sorted[k], log);
                if (!changed)
                {
                    rep.Failures++;
                    if (k == 0)
                    {
                        FailChannel(rep, log, "首步移动后回读顺序未变 ⇒ 该通道在本宿主版本未实现或未影响设计树顺序（HR=0 不算干活）");
                        return;
                    }
                    log(tag + ": 第 " + k + " 项回读不一致，继续下一项（不宣称该项成功）");
                }
            }
        }

        private static void FailChannel(Report rep, Action<string> log, string reason)
        {
            rep.ChannelDead = true;
            rep.ChannelReason = reason;
            log("asm-sort: ★ 通道判死：" + reason);
        }

        /// <summary>返回 cur 中第 k 个「参与元素」的下标；参与集合按 Id 认，读不到 Id 时退回 Type。</summary>
        private static int IndexOfKthMember(List<IZElement> cur, HashSet<int> memberIds, int k, Action<string> log)
        {
            int n = 0;
            for (int i = 0; i < cur.Count; i++)
            {
                int id = SafeId(cur[i]);
                bool member;
                if (id >= 0) member = memberIds.Contains(id);
                else
                {
                    // Id 读不到＝第三态：退回类型判定，并当场点名，不静默折算
                    member = IsComponent(cur[i], log);
                    log("asm-sort: 参与定位「" + SafeName(cur[i]) + "」Id <读不到>，改用 Type 兜底=" + member);
                }
                if (!member) continue;
                if (n == k) return i;
                n++;
            }
            return -1;
        }

        // ================================================================ 刷新

        /// <summary>
        /// 刷新：逐装配 IZAssembly.Update(槽7) + 场景 scene.Update() 一次。
        /// ★ 不用 UpdateBrowser(槽84)：坑 100 已定案单独调用无效；RegenerateParts(槽21) 2027 运行时 E_NOTIMPL。
        /// </summary>
        private static void RefreshScene(IZSceneDoc scene, Report rep, Action<string> log)
        {
            const string tag = "asm-sort";
            foreach (IZElement t in rep.TouchedAssemblies)
            {
                try
                {
                    IZAssembly a = t as IZAssembly;
                    if (a == null) { log(tag + ": 「" + SafeName(t) + "」QI IZAssembly 为空，跳过 Update"); continue; }
                    a.Update();
                    log(tag + ": IZAssembly.Update「" + SafeName(t) + "」调用完成（HR=0，不算排序判据）");
                }
                catch (Exception ex) { log(tag + ": IZAssembly.Update FAIL " + ProbeCommon.Hr(ex)); }
            }
            try { scene.Update(); log(tag + ": IZSceneDoc.Update 调用完成"); }
            catch (Exception ex) { log(tag + ": IZSceneDoc.Update FAIL " + ProbeCommon.Hr(ex)); }
        }

        private static void AddTouched(Report rep, IZElement asm)
        {
            int id = SafeId(asm);
            foreach (IZElement e in rep.TouchedAssemblies)
            {
                if (id >= 0 && SafeId(e) == id) return;
                if (id < 0 && ReferenceEquals(e, asm)) return;
            }
            rep.TouchedAssemblies.Add(asm);
        }

        // ================================================================ 同级读取（多通道对照）

        /// <summary>
        /// 读一层同级：parent=null ⇒ 场景第一层（IZSceneDoc.GetChildElements 槽7）；
        /// 否则并跑 A~F 六个通道、取项数最多的一路，逐通道项数全部落 trace。
        /// </summary>
        private static List<IZElement> ReadLevel(IZSceneDoc scene, IZElement parent, Action<string> log,
            out bool readOk, bool verbose)
        {
            const string tag = "asm-sort";
            readOk = false;

            if (parent == null)
            {
                object raw;
                try { raw = scene.GetChildElements(); }   // 槽7
                catch (Exception ex)
                {
                    log(tag + ": GetChildElements FAIL " + ProbeCommon.Hr(ex) + "（第三态）");
                    return new List<IZElement>();
                }
                List<IZElement> got = Unpack(raw, log, "IZSceneDoc.GetChildElements");
                int cnt = -1;
                try { cnt = scene.GetChildrenElementsCount(); }   // 槽18
                catch (Exception ex) { log(tag + ": GetChildrenElementsCount FAIL " + ProbeCommon.Hr(ex)); }
                if (verbose)
                {
                    log(tag + ": 根层通道 IZSceneDoc.GetChildElements → " + got.Count + " 项；GetChildrenElementsCount=" +
                        (cnt >= 0 ? cnt.ToString() : "<读不到>") + NameList(got));
                    if (cnt >= 0 && cnt != got.Count)
                        log(tag + ": ★ 根层计数接口=" + cnt + " 与数组=" + got.Count + " 不一致（以数组为准并记账）");
                }
                readOk = raw != null;
                return got;
            }

            var best = new List<IZElement>();
            string bestName = "";
            Collect(delegate { return parent.GetChildren(); }, "A IZElement.GetChildren", best, ref bestName, log, verbose);           // 槽13
            Collect(delegate { return parent.GetChildrenZArray(); }, "B IZElement.GetChildrenZArray", best, ref bestName, log, verbose); // 槽21
            Collect(delegate { return parent.GetChildrenEx(0); }, "C IZElement.GetChildrenEx(0)", best, ref bestName, log, verbose);     // 槽32
            IZAssembly asm = parent as IZAssembly;
            if (asm != null)
            {
                Collect(delegate { return asm.GetChildren(); }, "D IZAssembly.GetChildren", best, ref bestName, log, verbose);            // 槽11
                Collect(delegate { return asm.GetChildrenZArray(); }, "E IZAssembly.GetChildrenZArray", best, ref bestName, log, verbose); // 槽24
            }
            List<IZElement> chain = ReadChain(parent, log, verbose);
            NoteChannel("F GetFirstChild/GetNextChild 链", chain, best, ref bestName, log, verbose);

            int count = -1;
            try { count = parent.GetChildrenCount(); } catch (Exception ex) { log(tag + ": GetChildrenCount FAIL " + ProbeCommon.Hr(ex)); }
            if (verbose)
            {
                if (count >= 0 && count != best.Count)
                    log(tag + ": ★「" + SafeName(parent) + "」计数接口 GetChildrenCount=" + count +
                        " 与最佳通道(" + bestName + ")=" + best.Count + " 不一致（以通道数组为准并记账）");
                else
                    log(tag + ": 「" + SafeName(parent) + "」采用通道 " + bestName + "，项数 " + best.Count +
                        "，GetChildrenCount=" + (count >= 0 ? count.ToString() : "<读不到>"));
            }

            readOk = best.Count > 0 || count == 0;
            return best;
        }

        private static void Collect(Func<object> call, string name, List<IZElement> best, ref string bestName,
            Action<string> log, bool verbose)
        {
            object raw;
            try { raw = call(); }
            catch (Exception ex)
            {
                if (verbose) log("asm-sort: 通道 " + name + " FAIL " + ProbeCommon.Hr(ex));
                return;
            }
            List<IZElement> got = Unpack(raw, verbose ? log : Noop, name);
            NoteChannel(name, got, best, ref bestName, log, verbose);
        }

        private static void NoteChannel(string name, List<IZElement> got, List<IZElement> best, ref string bestName,
            Action<string> log, bool verbose)
        {
            if (verbose) log("asm-sort: 通道 " + name + " → " + got.Count + " 项" + NameList(got));
            if (got.Count > best.Count)
            {
                best.Clear();
                best.AddRange(got);
                bestName = name;
            }
        }

        private static void Noop(string m) { }

        /// <summary>旧通道：GetFirstChild(19)/GetNextChild(20) 链（14:00 实机证它漏节点，降为对照/兜底）。</summary>
        private static List<IZElement> ReadChain(IZElement parent, Action<string> log, bool verbose)
        {
            var list = new List<IZElement>();
            IZElement child;
            try { child = parent.GetFirstChild(); }
            catch (Exception ex)
            {
                if (verbose) log("asm-sort: 通道 F GetFirstChild FAIL " + ProbeCommon.Hr(ex));
                return list;
            }
            int guard = 0;
            while (child != null)
            {
                if (++guard > MaxChildren) { if (verbose) log("asm-sort: 通道 F 超过 " + MaxChildren + " 项，截断"); break; }
                list.Add(child);
                IZElement next;
                try { next = child.GetNextChild(); }
                catch (Exception ex)
                {
                    if (verbose) log("asm-sort: 通道 F GetNextChild FAIL " + ProbeCommon.Hr(ex) + "，链到此为止");
                    break;
                }
                child = next;
            }
            return list;
        }

        /// <summary>解包 ICAPI 返回值：ZArray（caxa3dmcp\McpTool.cs 实证写法）→ object[] → 单元素。</summary>
        private static List<IZElement> Unpack(object raw, Action<string> log, string name)
        {
            var list = new List<IZElement>();
            if (raw == null) return list;

            ZArray za = raw as ZArray;
            if (za != null)
            {
                int n;
                try { za.Count(out n); }
                catch (Exception ex) { log("asm-sort: " + name + " ZArray.Count FAIL " + ProbeCommon.Hr(ex)); return list; }
                for (int i = 0; i < n; i++)
                {
                    object o;
                    try { za.Get(i, out o); }
                    catch (Exception ex) { log("asm-sort: " + name + " ZArray.Get(" + i + ") FAIL " + ProbeCommon.Hr(ex)); break; }
                    AddOne(list, o, log, name + "[" + i + "]");
                }
                return list;
            }

            object[] arr = raw as object[];
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++) AddOne(list, arr[i], log, name + "[" + i + "]");
                return list;
            }

            IZElement single = raw as IZElement;
            if (single != null)
            {
                list.Add(single);
                return list;
            }
            log("asm-sort: " + name + " 返回 " + raw.GetType().FullName + "（无法解包，第三态）");
            return list;
        }

        private static void AddOne(List<IZElement> list, object o, Action<string> log, string where)
        {
            if (o == null) return;
            IZElement e = o as IZElement;
            if (e != null) { list.Add(e); return; }
            log("asm-sort: " + where + " 不是 IZElement：" + o.GetType().FullName + "（跳过并记账）");
        }

        // ================================================================ 基础读取

        private static bool IsAssembly(IZElement e, Action<string> log)
        {
            int t = SafeType(e);
            if (t == Z_ELEMENT_ASSEMBLY) return true;
            if (t >= 0) return false;
            log("asm-sort: 元素「" + SafeName(e) + "」Type <读不到>，不判为装配（第三态）");
            return false;
        }

        /// <summary>
        /// 参与排序的「零部件」＝设计树上会跟着搬家的那些节点（用户 14:2x 口径：装配、零件、钣金等）：
        /// 装配(2) / 零件(1) / 钣金件(20) / 多面体(25) / 线架(28) / 型材(29)。
        /// 不参与＝装配特征容器(39)、板料(21)、折弯(22)、造型/轮廓(19)、各类特征(3~17) 与坐标系。
        /// </summary>
        private static bool IsComponent(IZElement e, Action<string> log)
        {
            int t = SafeType(e);
            if (t == Z_ELEMENT_ASSEMBLY || t == Z_ELEMENT_PART || t == Z_ELEMENT_SHEETMETAL_PART ||
                t == Z_ELEMENT_FACET_PART || t == Z_ELEMENT_WIRE_PART || t == Z_ELEMENT_PROFILE_PART)
                return true;
            if (t >= 0) return false;   // 其它已知类型（板料/折弯/特征等）不参与
            log("asm-sort: 元素「" + SafeName(e) + "」Type <读不到>，不判为零部件（第三态）");
            return false;
        }

        private static int SafeType(IZElement e)
        {
            try { return (int)e.Type; } catch { return -1; }
        }

        private static int SafeId(IZElement e)
        {
            try { return (int)e.Id; } catch { return -1; }
        }

        private static string SafeName(IZElement e)
        {
            try { return e.Name ?? ""; } catch { return "<读不到>"; }
        }

        private static bool SameElement(IZElement a, IZElement b, Action<string> log)
        {
            int ia = SafeId(a), ib = SafeId(b);
            if (ia >= 0 && ib >= 0) return ia == ib;
            if (ReferenceEquals(a, b)) return true;
            // Id 读不到时不拿 Marshal.GetIUnknownForObject 做比较（AddRef 需配对 Release），
            // 只记第三态并返回 false ⇒ 至多多做一次移动，绝不误判成功。
            log("asm-sort: 同一性比较 Id <读不到>，本轮按「未命中」处理（第三态）");
            return false;
        }

        private static string Names(List<IZElement> list)
        {
            var a = new List<string>();
            foreach (IZElement e in list) a.Add("「" + SafeName(e) + "」");
            return string.Join("、", a.ToArray());
        }

        /// <summary>通道日志用：最多 12 个名字，超出只报总数（避免大装配把 trace 撑爆）。</summary>
        private static string NameList(List<IZElement> list)
        {
            if (list.Count == 0) return "";
            var a = new List<string>();
            int n = Math.Min(list.Count, 12);
            for (int i = 0; i < n; i++) a.Add("「" + SafeName(list[i]) + "」");
            if (list.Count > n) a.Add("…共 " + list.Count + " 项");
            return "：" + string.Join("、", a.ToArray());
        }
    }
}
