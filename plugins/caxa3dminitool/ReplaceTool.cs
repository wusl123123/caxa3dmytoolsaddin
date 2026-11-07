// 「零件置换」：按选中顺序取两个零部件 —— 第一个（A）＝提供模型的，它的外链 ics 就是原生
// 「导入零件/装配」文件框里要填的目标；第二个（B）＝被置换掉的，它的外链换成 A 正在用的那个文件。
// 用户口径（2026-11-03 定案）：**两个都必须带外链**，虚拟件一律拒绝。
//
// 为什么走 UI 代点：ICAPI 里没有「替换零件/装配」的公开接口（规则第 35 节 / 技能 03-com-api 第 11 节，
// ICM Tools-API 映射第 34 条：替换元素 → IPROExtLinkMgr / OnDragDropReplaceSelectedElement → 无公开接口）。
// 整条代点链（WM_COMMAND 53851 → 「导入零件/装配」→ 文件框 → 面板「✓」→「约束元素」）
// 与权威判据（只看设计树，不读活选择）都来自 caxa3ddiffrename 的实机定案，本文件只换「替换目标」的来源。
//
// 与 caxa3ddiffrename「替换零部件」的区别：那边先把源件**复制成一个新文件**再换链接（判据是
// 「设计树里出现指向那个新文件的元素」）；这边指向的是**「A」本来就在用的那个文件**，
// 替换前设计树里本来就有元素指向它 ⇒ 判据只能是**该文件的实例数 +1**，照抄原判据会第一步就误判「无需替换」。
//
// 不做的事（按用户口径）：不复制文件、不改名、不保存。替换完的场景留在未保存状态，由用户在 CAXA 里存盘。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dminitool
{
    /// <summary>用户点「停止」或关窗后，长流程用它脱身（不算失败，日志记「已停止」）。</summary>
    [ComVisible(false)]
    internal sealed class OperationStoppedException : Exception
    {
        public OperationStoppedException() : base("用户已停止") { }
    }

    /// <summary>一个候选零部件（A＝提供模型 / B＝被置换）的读取快照。</summary>
    [ComVisible(false)]
    internal sealed class ReplacePick
    {
        public IZElement Elem;
        public string Name = "";
        public string Kind = "";
        public int Type;
        public bool Linked;
        /// <summary>外链文件全路径（虚拟件为空串）。</summary>
        public string LinkPath = "";
        /// <summary>外链文件基名（不含目录与扩展名），设计树判据按它比对。</summary>
        public string LinkBase = "";

        public string Describe
        {
            get
            {
                return Name + "（" + Kind + " type=" + Type + "）" +
                    (Linked ? " ← " + LinkPath : " ← 虚拟件，无外链");
            }
        }
    }

    [ComVisible(false)]
    internal static class ReplaceTool
    {
        /// <summary>原生「替换零件/装配」的 WM_COMMAND 命令号（caxa3ddiffrename 实机取证：投给 Afx: 主框架，面板随即出现）。</summary>
        public const int CmdReplacePart = 53851;

        private const uint WM_COMMAND = 0x0111;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 设计树走查的节点上限与深度上限（caxa3ddiffrename 实机教训 4002 截断：
        /// 上限太小会让 DFS 在根层第一个子项里耗尽预算，替换目标永远轮不到，而「截断」与「真没找到」同值）。
        /// </summary>
        private const int WalkNodeCap = 12000;
        private const int WalkDepthCap = 32;

        // ---- 走查取证快照（供 ReplacePanelDriver 的 ④/⑥ 进度行读，不参与判定）----
        public static int LastWalkNodes;
        public static int LastWalkParts;
        public static int LastWalkQiFail;
        public static string LastWalkTruncated = "无";

        public static void CheckStop()
        {
            if (FlowGate.StopRequested) throw new OperationStoppedException();
        }

        // ================================================================ 1) 读选中（A / B）

        /// <summary>
        /// 读活选择里的零部件，按 <c>GetSelectedElementsZArray</c> 的返回顺序取：
        /// <b>第一个＝A（提供模型的那个），第二个＝B（要被置换掉的那个）</b>。
        /// ★ 顺序是否等于用户点选顺序，ICAPI 没有文档说明、本机也没实测过（两轮日志倾向「先点的排前面」，未定案）⇒
        ///   调用方必须把「谁当来源、谁当被置换件」在确认框里摊开让用户核对，点「否」即交换，不能默认顺序就是对的。
        /// </summary>
        public static List<ReplacePick> ReadSelectedComponents(IZBaseApp app, out string error)
        {
            error = null;
            var picks = new List<ReplacePick>();

            IZSceneDoc scene = FeatureTool.GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是设计（场景）环境，请切回 3D 设计环境后再试。";
                return picks;
            }

            string selError;
            List<IZElement> sel = FeatureTool.GetSelection(app, out selError);
            foreach (IZElement elem in sel)
            {
                if (!ElementsTool.IsComponentKind(elem)) continue;
                picks.Add(ToPick(elem));
            }

            if (picks.Count == 0)
            {
                error = "当前选中没有零部件（选中 " + sel.Count + " 个对象：" +
                        DescribeAll(sel) + "）。\n" +
                        "请在设计树里先点「A」（提供模型的零部件），再 Ctrl+点「B」（要被置换掉的零部件）。" +
                        (string.IsNullOrEmpty(selError) ? "" : "\n读取选中项时：" + selError);
                return picks;
            }

            if (picks.Count != 2)
            {
                error = "「零件置换」要求**恰好选中两个零部件**（先 A、后 B），现在是 " + picks.Count +
                        " 个：" + DescribePicks(picks) +
                        "\n（非零部件的特征/草图/面已自动忽略，共选中 " + sel.Count + " 个对象）";
                return picks;
            }
            return picks;
        }

        private static ReplacePick ToPick(IZElement elem)
        {
            ReplacePick p = new ReplacePick();
            p.Elem = elem;
            p.Name = SafeName(elem);
            p.Type = (int)elem.Type;
            p.Kind = KindOf(elem);
            bool linked;
            string path = ElementsTool.LinkedFilePath(elem, out linked);
            p.Linked = linked && !string.IsNullOrEmpty(path);
            p.LinkPath = p.Linked ? path : "";
            p.LinkBase = p.Linked ? Path.GetFileNameWithoutExtension(path) : "";
            return p;
        }

        /// <summary>
        /// 前置校验（用户 2026-11-03 定案）：**两个选中件都必须带外链** ——
        /// 「A」的外链 ics 是原生文件框里要填的目标，「B」是要被这条链顶掉的那一个；
        /// 虚拟件没有链可换，一律拒绝（原先放行「B 是虚拟件」的口径作废）。
        /// 外链同一个文件则没有可置换的东西；两个选中项读到同一个元素同样要拦。
        /// </summary>
        public static string Validate(ReplacePick pickA, ReplacePick pickB)
        {
            if (!pickA.Linked)
                return "「A」（" + pickA.Name + "）是虚拟零部件，没有外部 .ics 文件。\n" +
                    "零件置换要求**两个选中件都带外链**：A 的链就是要填进「导入零件/装配」文件框的目标，" +
                    "请重选一个带外链的零部件当 A（或在 CAXA 里先把它另存为外部文件）。";
            if (!pickB.Linked)
                return "「B」（" + pickB.Name + "）是虚拟零部件，它根本没有外部链接，" +
                    "没有东西可置换。\n零件置换要求**两个选中件都带外链**，" +
                    "请重选一个带外链的零部件当 B（或在 CAXA 里先把它另存为外部文件）。";
            if (!File.Exists(pickA.LinkPath))
                return "「A」的外链文件在盘上找不到：\n" + pickA.LinkPath +
                    "\n（可能已被移动或改名，置换无从下手）";
            if (string.Equals(pickB.LinkBase, pickA.LinkBase, StringComparison.OrdinalIgnoreCase))
                return "「A」与「B」外链同一个文件（" + pickA.LinkBase + "），" +
                    "它们是同一模型的实例，置换没有可做的事。";
            if (SameElement(pickB.Elem, pickA.Elem))
                return "「A」与「B」读到了同一个元素，请重新选一次（先点 A、再 Ctrl+点 B）。";
            return null;
        }

        /// <summary>两个 RCW 是否指向同一个 COM 元素（RCW 缓存不保证命中，按 IUnknown 指针比）。</summary>
        private static bool SameElement(IZElement a, IZElement b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            IntPtr pa = IntPtr.Zero, pb = IntPtr.Zero;
            try
            {
                pa = Marshal.GetIUnknownForObject(a);
                pb = Marshal.GetIUnknownForObject(b);
                return pa != IntPtr.Zero && pa == pb;
            }
            catch { return false; }
            finally
            {
                if (pa != IntPtr.Zero) Marshal.Release(pa);
                if (pb != IntPtr.Zero) Marshal.Release(pb);
            }
        }

        // ================================================================ 2) 权威判据：设计树里指向某文件的元素数

        /// <summary>
        /// 上一次走查收集到的命中元素（最多 <see cref="WalkHitCap"/> 个）。
        /// ★ 收尾的「选回新元素 + 类型降级判定」必须复用它，绝不能再走一遍树：
        ///   实机 11-03 19:11 那轮替换已经生效（4/4 复核 1→2、1→0 都打出来了），
        ///   却卡在收尾为了"按名字找人"而做的 <c>CollectAllElements</c> 整树走查上 ——
        ///   那次走查既无节点上限也无停止检查，而且找的还是个永不命中的条件（见 <see cref="FindReplacedInstance"/>）。
        /// </summary>
        public static List<IZElement> LastHits = new List<IZElement>();

        /// <summary>命中元素收集上限：正常装配里同一个 ics 不会有几个实例，收多了只会把引用攥在手里。</summary>
        private const int WalkHitCap = 8;

        /// <summary>
        /// 统计设计树里外链基名等于 <paramref name="targetBase"/> 的零部件个数，并把命中的元素本身收进
        /// <see cref="LastHits"/>（判据走查已经把它们读到了，收集是零成本的）。
        /// 两遍走：① 根层自身（个位数 COM 调用，装配最外层的外链件就在这一层）；② 带上限 DFS 兜底。
        /// 每次重新取活动场景文档：替换期间场景在重建，握着的旧指针随时可能作废（实机 SEHException 0x80004005）。
        /// </summary>
        public static int CountLinkedTo(IZBaseApp app, string targetBase)
        {
            LastWalkNodes = 0;
            LastWalkParts = 0;
            LastWalkQiFail = 0;
            LastWalkTruncated = "无";
            LastHits = new List<IZElement>();
            if (string.IsNullOrEmpty(targetBase)) return 0;

            IZSceneDoc scene = FeatureTool.GetSceneDoc(app);
            if (scene == null)
            {
                LastWalkTruncated = "场景文档读不到";
                return -1;      // 「读不到」不是「为零」，调用方按三态处理
            }

            List<IZElement> roots = new List<IZElement>();
            try { ElementsTool.AppendAll(scene.GetChildElements(), roots, new HashSet<IZElement>(), 0); }
            catch (Exception ex)
            {
                LastWalkTruncated = "根层读取失败 " + ex.Message;
                return -1;
            }

            int hits = 0;
            HashSet<IZElement> seen = new HashSet<IZElement>();
            var pending = new List<IZElement>();
            foreach (IZElement elem in roots)
            {
                if (!seen.Add(elem)) continue;
                LastWalkNodes++;
                if (LinksTo(elem, targetBase)) hits++;
                pending.Add(elem);
            }

            Stack<Entry> stack = new Stack<Entry>();
            foreach (IZElement elem in pending) stack.Push(new Entry(elem, 1));

            while (stack.Count > 0)
            {
                if (LastWalkNodes >= WalkNodeCap)
                {
                    LastWalkTruncated = "节点>" + WalkNodeCap + "（栈里还剩 " + stack.Count + " 项）";
                    break;
                }
                if (FlowGate.StopRequested)
                {
                    LastWalkTruncated = "已请求停止（走查中断）";
                    break;
                }
                Entry e = stack.Pop();
                if (e.Depth > WalkDepthCap)
                {
                    if (LastWalkTruncated == "无") LastWalkTruncated = "深度>" + WalkDepthCap;
                    continue;
                }

                List<IZElement> kids = new List<IZElement>();
                try
                {
                    ElementsTool.AppendAll(e.Elem.GetChildrenZArray(), kids, seen, e.Depth);
                }
                catch (Exception ex)
                {
                    LastWalkQiFail++;
                    Diagnostics.Trace("(walk) 子节点读取失败 " + SafeName(e.Elem) + "：" + ex.Message);
                    continue;
                }

                foreach (IZElement kid in kids)
                {
                    LastWalkNodes++;
                    if (LinksTo(kid, targetBase)) hits++;
                    stack.Push(new Entry(kid, e.Depth + 1));
                }
            }

            Diagnostics.LogPoll("设计树走查 " + targetBase + "：命中 " + hits + "，节点 " +
                LastWalkNodes + "，零部件 " + LastWalkParts + "，QI失败 " + LastWalkQiFail +
                "，截断 " + LastWalkTruncated);
            return hits;
        }

        private struct Entry
        {
            public readonly IZElement Elem;
            public readonly int Depth;
            public Entry(IZElement elem, int depth) { Elem = elem; Depth = depth; }
        }

        /// <summary>这个元素自己的外链基名是否就是 targetBase（顺带记走查计数、收命中元素供收尾复用）。</summary>
        private static bool LinksTo(IZElement elem, string targetBase)
        {
            if (elem == null) return false;
            try
            {
                if (!ElementsTool.IsComponentKind(elem)) return false;
                LastWalkParts++;
                bool linked;
                string path = ElementsTool.LinkedFilePath(elem, out linked);
                if (!linked || string.IsNullOrEmpty(path)) return false;
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), targetBase,
                        StringComparison.OrdinalIgnoreCase)) return false;
                if (LastHits.Count < WalkHitCap) LastHits.Add(elem);
                return true;
            }
            catch
            {
                LastWalkQiFail++;
                return false;
            }
        }

        // ================================================================ 3) 活选择读写

        /// <summary>
        /// 把活选择清成只剩 <paramref name="elem"/>（原生「替换零件/装配」认的就是这一步的活选择）。
        /// 回读只作取证，不作放行条件：读不到不等于没写进去，但一定要在日志里分态写清。
        /// </summary>
        public static bool ReassertSelection(IZBaseApp app, IZElement elem, out string error)
        {
            error = null;
            IZSceneDoc scene = FeatureTool.GetSceneDoc(app);
            if (scene == null) { error = "取不到场景文档。"; return false; }
            IZSelectionMgr sel = ElementsTool.GetSelectionMgr(scene);
            if (sel == null) { error = "取不到 SelectionMgr。"; return false; }

            try
            {
                sel.RemoveAllFromSelection();
                sel.AddElementToSelection(elem, true);
            }
            catch (Exception ex)
            {
                error = "写入活选择异常：" + ex.Message;
                return false;
            }

            string readError;
            List<IZElement> back = FeatureTool.GetSelection(app, out readError);
            if (back.Count == 0)
            {
                error = "写入后回读为空（读不到，不代表没写进去）" +
                        (string.IsNullOrEmpty(readError) ? "" : "：" + readError);
                return false;
            }
            foreach (IZElement e in back)
                if (SameElement(e, elem) || SafeName(e) == SafeName(elem)) return true;
            error = "写入后回读到 " + back.Count + " 个对象，其中没有「" + SafeName(elem) + "」：" +
                    DescribeAll(back);
            return false;
        }

        /// <summary>
        /// 收尾：从判据走查收集到的「外链＝A 的文件」元素里，挑出<b>不是「A」本体</b>的那一个 —— 它就是置换出来的新元素。
        ///
        /// ★ 为什么不再按「元素名＝B 的名字」找人（实机 11-03 19:10 铁证，原 `SelectReplacedElement`/
        /// `FindByLinkAndName` 已删）：原生替换连元素名一起换成新 ics 的名字，设计树里两个实例都叫
        /// 「SC08020102.01-21 折弯板」，按旧名匹配恒为 0 命中（日志原话：「设计树里没有『外链=… 且 名字=…』
        /// 的元素，没按回活选择」）。而为了这个永不命中的判据各做一次 <c>CollectAllElements</c> 整树走查
        /// （无节点上限、无停止检查），正是 19:11 那轮把主线程堵死的地方 —— 零收益还带卡死风险的入口，
        /// 按规则坑 156 连支撑代码一起删。
        ///
        /// 只比指针，不再读一次树：指针比对用的是判据已经攥在手里的 RCW。
        /// </summary>
        public static IZElement FindReplacedInstance(List<IZElement> hits, IZElement pickAElem, out string note)
        {
            note = null;
            if (hits == null || hits.Count == 0)
            {
                note = "判据走查没收到任何指向「A」的元素，定位不了替换出来的那一个";
                return null;
            }
            var others = new List<IZElement>();
            foreach (IZElement h in hits)
                if (!SameElement(h, pickAElem)) others.Add(h);

            if (others.Count == 1) { note = "命中 " + hits.Count + " 个实例，排除「A」本体后剩 1 个"; return others[0]; }
            if (others.Count > 1)
            {
                note = "排除「A」本体后还剩 " + others.Count + " 个（同一 ics 有多个实例），取第一个选回";
                return others[0];
            }
            // 一个都不剩＝指针全对上「A」（或 RCW 已作废、比对不上号）。老实说定位不了，别硬猜。
            note = "指向「A」的 " + hits.Count + " 个实例都被认成「A」本体，区分不出新元素";
            return null;
        }

        // ================================================================ 4) 投递原生命令

        /// <summary>
        /// WM_COMMAND 53851 投给 CAXA 主框架（class 以 Afx: 开头的顶层窗口）。
        /// ★ 绝不能投 Process.MainWindowHandle：那是本插件自己的 WinForms 窗口，
        ///   投进去等于投给自己，面板永远不出（实机空等 74 秒的根因）。
        /// ★ 返回值只代表「已入队」，不代表面板出现，后续一切看面板与判据。
        /// </summary>
        public static bool PostReplacePartCommand(out string error)
        {
            error = null;
            IntPtr hwnd = UiWin.CadFrame();
            if (hwnd == IntPtr.Zero)
            {
                error = "取不到 CAXA 主框架句柄（宿主应为 IronCAD.exe，且主框架需可见）。";
                Diagnostics.Log("PostReplacePartCommand: CAXA 主框架为空");
                return false;
            }
            bool posted;
            try { posted = PostMessage(hwnd, WM_COMMAND, (IntPtr)CmdReplacePart, IntPtr.Zero); }
            catch (Exception ex)
            {
                error = "投递命令异常：" + ex.Message;
                Diagnostics.Log("PostReplacePartCommand 异常: " + ex);
                return false;
            }
            Diagnostics.Trace(string.Format("PostReplacePartCommand: hwnd=0x{0:X} cmd={1} posted={2}",
                hwnd.ToInt64(), CmdReplacePart, posted));
            if (!posted)
                error = "投递「替换零件/装配」失败，Win32 错误 " + Marshal.GetLastWin32Error() + "。";
            return posted;
        }

        // ================================================================ 5) 主流程

        /// <summary>
        /// 长流程本体，由 MiniToolsForm.RunLongFlow 在里面调用（FlowGate 已独占、代点已启用）。
        /// 做的事：把 <paramref name="pickB"/>（后选的那个）的外链换成 <paramref name="pickA"/>（先选的那个）
        /// 正在用的 ics —— 即「先选 A、再选 B，把 B 置换成 A」。
        /// 判据：<b>「A」的外链文件在设计树里的实例数比置换前多</b> —— 置换前它本来就有「A」自己那一个。
        /// 两个件都带外链已由 <see cref="Validate"/> 卡死，所以这里的基线统计不再有「虚拟件」分支。
        /// </summary>
        public static void Execute(IZBaseApp app, ReplacePick pickA, ReplacePick pickB,
            Action<string> log)
        {
            ReplacePanelDriver.Wait = MiniToolsForm.WaitQuietly;
            ReplacePanelDriver.Log = log;

            string baseA = pickA.LinkBase;

            int beforeA = CountLinkedTo(app, baseA);
            int beforeB = CountLinkedTo(app, pickB.LinkBase);
            if (beforeA < 0)
                throw new Exception("置换前统计设计树失败（场景文档读不到），无法建立判据基线。");
            log("1/4 置换前基线：设计树里外链指向「A」的 " + baseA + " 的元素 " + BeforeText(beforeA) +
                " 个；指向「B」的 " + pickB.LinkBase + " 的元素 " + BeforeText(beforeB) +
                " 个。判据＝" + baseA + " 的实例数比 " + beforeA + " 多");

            Func<bool> isDone = delegate
            {
                int now = CountLinkedTo(app, baseA);
                return now >= 0 && now > beforeA;
            };

            log("2/4 把「B」单独按回 CAXA 活选择集，再投递原生「替换零件/装配」");
            string selError;
            if (!ReassertSelection(app, pickB.Elem, out selError))
                log("    警告：活选择没能确认成「B」（" + selError + "），照旧投递；" +
                    "若面板里换的是别的件，请立刻点「停止」");
            PopupAutoYes.FileDialogTarget = pickA.LinkPath;
            string postError;
            if (!PostReplacePartCommand(out postError))
                throw new Exception("投递「替换零件/装配」失败：" + postError +
                    "\n（先看跟踪日志 " + Diagnostics.LogPath + " 里 PostReplacePartCommand 那行的 hwnd 是不是 Afx: 主框架）");

            log("3/4 UI 代点驱动替换面板：导入「" + pickA.LinkPath + "」→ 面板「✓」→（有则）「约束元素」");
            log("    面板选项一概不动（按 CAXA 默认），要换「所有实例」请手工做");
            ReplacePanelDriver.Run(pickA.LinkPath, isDone);

            int afterA = CountLinkedTo(app, baseA);
            // 判据走查已经把「指向 A」的元素收进 LastHits 了，趁第二次走查把它们覆盖掉之前拿下来
            List<IZElement> hitsA = LastHits;
            int afterB = CountLinkedTo(app, pickB.LinkBase);
            log("4/4 复核：外链指向 " + baseA + " 的元素 " + BeforeText(beforeA) + " → " + BeforeText(afterA) +
                "；指向 " + pickB.LinkBase + " 的元素 " + BeforeText(beforeB) + " → " + BeforeText(afterB));

            // 收尾三件事全部复用上面那一次走查的结果，不再多走一遍树（实机 19:11 就是卡在这）
            string hitNote;
            IZElement replaced = FindReplacedInstance(hitsA, pickA.Elem, out hitNote);
            if (replaced == null)
            {
                log("    " + hitNote + " —— 不影响置换结果，请在 CAXA 里手工点一下那个零部件" +
                    "（原生替换完 CAXA 会把选择清空，不选回界面会点不动，坑 132）");
            }
            else
            {
                string backError;
                bool selected = ReassertSelection(app, replaced, out backError);
                log("    " + (selected
                        ? "已把置换出来的「" + SafeName(replaced) + "」按回活选择（" + hitNote + "）"
                        : "按回活选择没确认（" + backError + "），不影响置换结果，请手工点一下那个零部件"));

                // 类型降级判定：拿得到新元素就必须报，别再像前两轮那样因为"人没找到"而静默跳过
                int afterType;
                string afterKind;
                try { afterType = (int)replaced.Type; afterKind = KindOf(replaced); }
                catch (Exception ex)
                {
                    afterType = -1; afterKind = "读不到（" + ex.Message + "）";
                }
                if (afterType < 0)
                    log("    新元素类型读不到，未做降级比对");
                else if (afterType != pickB.Type)
                    log("    警告：元素类型从 " + pickB.Kind + "(type=" + pickB.Type + ") 变成 " +
                        afterKind + "(type=" + afterType + ") —— 原生替换会换掉元素对象，" +
                        "钣金/共享属性可能一起丢（caxa3ddiffrename 实机 15:53 就是这么把钣金件换成装配体的）");
                else
                    log("    元素类型仍是 " + afterKind + "（未降级）");
            }

            log("    完成：「" + pickB.Name + "」已指向 " + pickA.LinkPath +
                "（＝「" + pickA.Name + "」正在用的文件）。本按钮不改名、不复制文件、不保存 —— 请在 CAXA 里自行保存。");
        }

        /// <summary>计数读不到（-1）时写「读不到」，别把它当 0（三态观测，坑 155）。</summary>
        private static string BeforeText(int count)
        {
            return count < 0 ? "读不到" : count.ToString();
        }

        // ================================================================ 文本helper

        public static string SafeName(IZElement elem)
        {
            if (elem == null) return "?";
            try { return string.IsNullOrEmpty(elem.Name) ? "?" : elem.Name; } catch { return "?"; }
        }

        public static string KindOf(IZElement elem)
        {
            try
            {
                switch (elem.Type)
                {
                    case eZElementType.Z_ELEMENT_PART: return "零件";
                    case eZElementType.Z_ELEMENT_ASSEMBLY: return "装配体";
                    case eZElementType.Z_ELEMENT_SHEETMETAL_PART: return "钣金零件";
                    case eZElementType.Z_ELEMENT_FACET_PART: return "刻面零件";
                    case eZElementType.Z_ELEMENT_WIRE_PART: return "线框零件";
                    case eZElementType.Z_ELEMENT_PROFILE_PART: return "轮廓零件";
                    default: return elem.Type.ToString();
                }
            }
            catch { return "?"; }
        }

        private static string DescribeAll(List<IZElement> list)
        {
            var parts = new List<string>();
            for (int i = 0; i < list.Count && i < 8; i++)
                parts.Add(SafeName(list[i]) + "(" + KindOf(list[i]) + ")");
            if (list.Count > 8) parts.Add("…共 " + list.Count);
            return string.Join("、", parts.ToArray());
        }

        public static string DescribePicks(List<ReplacePick> picks)
        {
            var parts = new List<string>();
            foreach (ReplacePick p in picks) parts.Add(p.Describe);
            return string.Join(" ｜ ", parts.ToArray());
        }
    }
}
