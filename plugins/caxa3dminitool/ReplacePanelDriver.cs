using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace caxa3dminitool
{

    /// <summary>
    /// 「替换零件/装配」面板的代点链：面板出现 → 点「导入零件/装配」→ 文件框（PopupAutoYes 填路径点打开）
    /// → 面板确定 → 「重新附加约束」命令条确定。每一步都允许手工补做，成功只认外链真的换成新 ics。
    /// </summary>
    [ComVisible(false)]
    internal static class ReplacePanelDriver
    {
        /// <summary>由 MiniToolsForm 注入（WaitUntil 的静默版，带 DoEvents 与停止检查）。</summary>
        public static Func<string, int, Func<bool>, int, bool> Wait;
        /// <summary>由 MiniToolsForm 注入的 UI 日志。</summary>
        public static Action<string> Log;

        private static readonly string[] ApplyTexts = { "确定", "应用", "替换", "完成" };
        // 「重新附加约束」在替换面板的「选项」里也是复选框文字，只有「约束元素」是这一步独有的
        private static readonly string AttachFingerprint = UiWin.Normalize("约束元素");
        private static readonly string ImportFingerprint = UiWin.Normalize("导入零件/装配");

        /// <summary>
        /// 连续「pane 子树里问不到面板指纹」的次数。面板是 pane 里的一页，一次问不到多半是时机问题，
        /// 必须连问两次 + pane 仍在，才允许判定「面板已关」（实机 14:37 就是被一次扫漏骗进 120 秒等待）。
        /// </summary>
        private static int _panelMisses;

        /// <summary>pane 子树的 MSAA 节点配额（实机：pane 子树几百个节点，框架整树 2841~3.6 万）。</summary>
        private const int PaneMsaaNodes = 1200;

        /// <summary>
        /// 面板指纹查找：一律先问「属性」pane 子树——一次 EnumChildWindows + 一次 **以 pane 为根**的
        /// MSAA；allowGlobal=true 时才再做一次全局窗口文字兜底（命中后把 pane 锚点重挂过去）。
        /// 为什么 MSAA 必须以 pane 为根：Walk 只沿 IAccessible 的孩子下钻、不跨子窗口句柄，
        /// 从框架根扫 2841 个节点也扫不到 pane 里的条目（实机 14:37 就是这么把还开着的面板判成
        /// 「不存在」，接着误入 120 秒权威等待——用户看到的「卡住」正是这里）。
        /// 同理，框架根扫到的第一个「确定」是别处的按钮，点它等于什么都没做。
        /// </summary>
        private static bool FindBoth(string needleNorm, bool allowGlobal,
            out UiCtrl ctrl, out MsaaNode node)
        {
            ctrl = null;
            node = null;
            IntPtr pane = UiWin.PropertyPane();
            if (pane != IntPtr.Zero)
            {
                ctrl = UiWin.FindInSubtree(pane, needleNorm);
                if (ctrl != null) return true;
                node = Msaa.FirstIn(pane, new string[] { needleNorm }, false, PaneMsaaNodes);
                if (node != null) return true;
            }
            if (!allowGlobal) return false;
            UiCtrl global = UiWin.FindByTextLike(needleNorm);
            if (global == null) return false;
            ctrl = global;
            UiWin.ReanchorPane(global.Hwnd);
            return true;
        }

        /// <summary>
        /// 提交类按钮的候选文字（MSAA 口径，一次问完避免重扫）。
        /// 这里用**包含**匹配而不是精确匹配：`Normalize` 会把「确定(&O)」洗成「确定(o)」，
        /// 精确匹配会漏；而搜索范围已经是「属性」pane 子树（几百个节点），
        /// 名字里带「确定/应用/完成」的条目不可能来自别处。
        /// 「替换」被剔除：pane 里有个「替换为」标签，名字包含「替换」却点不动，白等 15 秒。
        /// </summary>
        private static readonly string[] ApplyNormalized = {
            UiWin.Normalize("确定"), UiWin.Normalize("应用"), UiWin.Normalize("完成")
        };

        /// <summary>
        /// 面板已关闭、提交已被接住，但外链还在换 —— 不再重投提交动作，只做权威等待（期间手工补做同样算数）。
        /// </summary>
        private static void FinishAfterCommit(string stage, string newBase, Func<bool> isDone)
        {
            Log("    " + stage + "：面板已关闭、提交已被接住，外链还在换 → 直接转 ⑥ 权威等待");
            ReplaceTool.CheckStop();

            // ⑥ 取证：每 3 秒一行进度（轮次 / 命中 / 走查节点 / 零部件 / QI失败 / 是否被上限截断）。
            // 只加日志，不改判定——11-03 那两轮 88 s、15 s 的空转就是因为整段一行输出都没有。
            int tick = 0;
            DateTime nextLine = DateTime.Now.AddSeconds(3);
            Func<bool> observed = delegate
            {
                tick++;
                bool ok = isDone();
                if (ok || DateTime.Now >= nextLine)
                {
                    nextLine = DateTime.Now.AddSeconds(3);
                    Diagnostics.Trace(string.Format(
                        "⑥ 取证 轮={0} 命中={1} 走查节点={2} 零部件={3} QI失败={4} 截断={5}",
                        tick, ok ? "Y" : "N",
                        ReplaceTool.LastWalkNodes, ReplaceTool.LastWalkParts,
                        ReplaceTool.LastWalkQiFail, ReplaceTool.LastWalkTruncated));
                }
                return ok;
            };

            Log("    ⑥ 等外链换成 " + newBase + ".ics（最长 20 秒，判据遍历设计树，不用再点零部件）");
            // 120 秒 → 20 秒：成功样本实测 ≤20 秒（11-03 09:09 那轮外链在 6 秒内就换好了）。
            // 原来的 120 秒是给「判据读不到」兜底，现在根层优先判据已是毫秒级，
            // 再等满 120 秒只会让用户看着一个已经不动的进度空耗。
            if (Wait("⑥ 等外链换成 " + newBase + ".ics", 20000, observed, 500))
            {
                Log("    ⑥ 外链已换成 " + newBase + ".ics ✔");
                return;
            }
            UiWin.Dump("提交后面板已关但外链仍未替换");
            throw new Exception("提交动作已生效（面板关闭）但外链始终没换成 " + newBase + ".ics。\n" +
                "请把跟踪日志 " + Diagnostics.LogPath + " 发回来定位（取证块就在它末尾）。");
        }

        /// <summary>
        /// 试完一条通道后看副作用：外链换了才算这条真起了作用。
        /// 实机 14:22 的教训：MSAA 点中「确定」之后 CAXA 换链接 + 重新附加约束跑了近 30 秒，
        /// 4 秒窗口里看不到副作用就继续往下投，等于对着已经提交的面板盲投 IDOK/回车。
        /// 所以现在等到 15 秒，并且把「面板已消失」也当作这一步走完了——剩下的交给 ⑥ 的 120 秒权威等待。
        ///
        /// ★ 实机 11-03 17:32 卡死：原来这里在轮询成功**之后**又调了一次 `isDone()`，
        /// 只为挑「外链已换」还是「面板已关」那句日志文案。`isDone()` = 一整轮 1175 节点 /
        /// 110 零部件 / 500+ 次 QI 的设计树 COM 走查，而且正好落在 CAXA 刚提交完替换、
        /// 还在跑插入与重附约束的当口 —— 那一轮走查再也没返回，主线程连同界面一起堵死。
        /// 现在把轮询里**最后一次的观测**记下来直接复用：判定跑过就不再重跑。
        /// </summary>
        private enum Settle { None, Swapped, PanelClosed }

        private static Settle Settled(string stage, Func<bool> isDone, Func<bool> panelGone)
        {
            int tick = 0;
            bool[] saw = new bool[1];     // 轮询里最后一次观测：外链到底换了没有（换就没再看面板）
            bool[] gone = new bool[1];
            DateTime nextLine = DateTime.Now.AddSeconds(3);
            bool done = Wait(stage, 15000, delegate
            {
                tick++;
                saw[0] = isDone();
                gone[0] = false;
                if (DateTime.Now >= nextLine)
                {
                    // ④ 取证：这条分支到底是被「外链已换」还是被「面板已关」判成立的
                    nextLine = DateTime.Now.AddSeconds(3);
                    Diagnostics.Trace(string.Format(
                        "④ 取证[{0}] 轮={1} 外链已换={2} 走查节点={3} 截断={4}",
                        stage, tick, saw[0] ? "Y" : "N",
                        ReplaceTool.LastWalkNodes, ReplaceTool.LastWalkTruncated));
                }
                if (saw[0]) return true;
                gone[0] = panelGone != null && panelGone();
                return gone[0];
            }, 600);
            if (!done)
            {
                Diagnostics.Trace("ReplacePanel: " + stage + " 无副作用");
                return Settle.None;
            }
            Log(saw[0] ? "    ✔ " + stage + " 之后外链已换成新 ics"
                       : "    ✔ " + stage + " 之后面板已关闭，判定提交已被接住，转 ⑥ 权威等待");
            return saw[0] ? Settle.Swapped : Settle.PanelClosed;
        }

        /// <summary>
        /// 提交当前面板。五条通道按「精度高、代价小」排序，每试一条只看权威判据（外链是否已换）：
        ///   ① pane 子树里带文字的确定类按钮（真对话框才有，面板一般没有）
        ///   ② **以 pane 为根**的 MSAA「确定/应用/替换/完成」——14:22 成功那轮走的就是这条路
        ///   ③ 按 pane 左上偏移点绿色 ✓（✓/✗ 没有 HWND、命令号也没有，只能几何点，偏差风险最高，
        ///     故排在 MSAA 之后；三个候选偏移依次试，每次之后都重新看权威判据）
        ///   ④ 沿 pane 父链投 IDOK ⑤ 焦点窗口回车
        /// 不再往主框架盲投 IDOK/回车：框架级消息作用在 CAXA 自己的界面上，不是这个面板，
        /// 而且面板已经提交时再投等于乱按（实机 14:22 的教训：提交后 CAXA 要跑近 30 秒）。
        /// </summary>
        private static Settle TryAccept(string stage, IntPtr pane,
            Func<bool> isDone, Func<bool> panelGone)
        {
            ReplaceTool.CheckStop();
            if (pane == IntPtr.Zero)
            {
                Log("    " + stage + "：定位不到「属性」pane，代点退回不了——请手工点一次面板里的确定/✓");
                Diagnostics.Log("ReplacePanel: " + stage + " 没有 pane 锚点");
                return Settle.None;
            }

            UiCtrl btn = UiWin.FindButtonInSubtree(pane, ApplyTexts);
            if (btn != null && UiWin.Click(btn, stage + " pane 内确定按钮"))
            {
                Settle s = Settled(stage + " pane 内确定按钮", isDone, panelGone);
                if (s != Settle.None) return s;
            }

            MsaaNode mn = Msaa.FirstIn(pane, ApplyNormalized, false, PaneMsaaNodes);
            if (mn != null && Msaa.Click(mn, stage + " pane MSAA 确定条目"))
            {
                Settle s = Settled(stage + " pane MSAA 确定条目", isDone, panelGone);
                if (s != Settle.None) return s;
            }

            for (int i = 0; i < UiWin.AcceptOffsetCount; i++)
            {
                if (!UiWin.ClickPaneAccept(pane, stage + " pane 绿勾#" + i, i)) continue;
                Settle s = Settled(stage + " pane 绿勾#" + i, isDone, panelGone);
                if (s != Settle.None) return s;
            }

            UiWin.PostIdokToAncestors(pane, stage + " pane 父链 IDOK");
            Settle sid = Settled(stage + " pane 父链 IDOK", isDone, panelGone);
            if (sid != Settle.None) return sid;

            UiWin.EnterToFocus(stage + " 焦点回车");
            return Settled(stage + " 焦点回车", isDone, panelGone);
        }

        /// <summary>两个通道都在 pane 子树里命中时优先用窗口句柄：`BM_CLICK` 是实机验证过的（13:50）。</summary>
        private static bool ClickEither(UiCtrl ctrl, MsaaNode node, string why)
        {
            if (ctrl != null) return UiWin.Click(ctrl, why);
            return node != null && Msaa.Click(node, why);
        }

        private static void LogHit(string role, UiCtrl ctrl, MsaaNode node)
        {
            if (ctrl != null)
                Log("    " + role + "：命中「" + ctrl.Text + "」（窗口 " + ctrl.TopTitle + "）");
            else if (node != null)
                Log("    " + role + "：命中「" + node.Name + "」动作=[" + node.Action + "]");
        }

        private static void Require()
        {
            if (Wait == null || Log == null)
                throw new InvalidOperationException("ReplacePanelDriver 未接线（Wait/Log 为空）。");
        }

        /// <param name="newIcs">要替换成的新 ics 全路径（文件框里由 PopupAutoYes 填入）。</param>
        /// <param name="isDone">权威判据：设计树里是否已有元素外链到该 ics（不读活选择）。</param>
        public static void Run(string newIcs, Func<bool> isDone)
        {
            Require();
            ReplaceTool.CheckStop();
            _panelMisses = 0;
            string newBase = Path.GetFileNameWithoutExtension(newIcs);

            if (isDone()) { Log("    外链已经是 " + newBase + ".ics，无需替换"); return; }

            // ① 面板出现：以「导入零件/装配」为指纹（面板标题会随停靠状态变，条目文字最稳）。
            //    实机 13:50：该条目是真 HWND（class=Button），所以窗口文字为主、MSAA 兜底。
            //    第 1 次允许全局快照兜底（顺带把 pane 锚点重挂到面板真正所属的 pane），
            //    之后每 5 次轮询才兜底一次，其余只问 pane 子树。
            Log("    ① 等「替换零件/装配」面板出现（指纹＝「导入零件/装配」，窗口文字优先）");
            UiCtrl import = null;
            MsaaNode importNode = null;
            int[] polls = new int[1];
            if (!Wait("① 等替换面板", 15000, delegate
            {
                if (FindBoth(ImportFingerprint, polls[0]++ % 5 == 0, out import, out importNode))
                    return true;
                return isDone();
            }, 400))
            {
                UiWin.Dump("替换面板未出现");
                throw new Exception("投递命令后 15 秒没等到「导入零件/装配」。\n" +
                    "先看跟踪日志里 PostReplacePartCommand: hwnd 是不是 Afx: 主框架" +
                    "（13:50 实机为 0x670E1E，面板随即出现）；若还是 WindowsForms10.* 说明装的是旧 DLL。\n" +
                    "hwnd 正确却仍不出面板，才是真的没有活的选中件（先在设计树里选中一个带外链的零部件）。\n" +
                    "取证块已写进跟踪日志：" + Diagnostics.LogPath);
            }
            if (import == null && importNode == null)
            {
                Log("    ① 面板未出现但外链已换好，跳过后续代点");
                return;
            }
            LogHit("①", import, importNode);

            // ② 点「导入零件/装配」→ 弹文件框
            ReplaceTool.CheckStop();
            if (!ClickEither(import, importNode, "打开文件框"))
                Log("    警告：点不动「导入零件/装配」，请手工点一次，后面每一步手工补做都算数");

            // ③ 文件框：PopupAutoYes 负责填 零件B.ics 并点「打开」。
            //    实机 14:37 取证：文件框 27.561 秒被自动点掉，而这里还在等它「出现」，
            //    等满 20 秒才超时——它点得比第一轮轮询还快。改成直接看代点计数增量。
            Log("    ② 已点「导入零件/装配」，等文件框（会自动填入 " + newBase + ".ics 并点打开）");
            int clicksBefore = PopupAutoYes.ClickCount;
            bool handled = Wait("③ 等文件框自动点「打开」", 20000, delegate
            {
                return PopupAutoYes.ClickCount > clicksBefore || isDone();
            }, 200);
            Log(handled
                ? "    文件框已由自动代点处理（代点计数 " + clicksBefore + "→" +
                  PopupAutoYes.ClickCount + "）"
                : "    警告：20 秒没看到文件框被点掉（可能面板记住了上次路径直接生效），继续看外链");

            if (isDone()) { Log("    外链已换，替换生效"); return; }

            // ④ 提交替换面板（选完文件后停在属性页顶部的绿色 ✓）
            //   「两张面板都不在、文件框也关着」＝提交动作已经被接住：实机 14:22 就是
            //   「确定」点完 30 秒后外链才换，期间不该再往框架上盲投 IDOK/回车。
            //   判「面板已关」：热路径只问 pane 子树；每 5 次轮询（约 3 秒）才做一次全局窗口文字复查，
            //   且只有**连续两次全局复查都问不到**才允许说「关」——14:37 就是拿一次扫漏当了证据。
            int[] gonePolls = new int[1];
            Func<bool> panelGone = delegate
            {
                if (UiWin.FileDialogOpen()) { _panelMisses = 0; return false; }
                bool global = gonePolls[0]++ % 5 == 0 || !UiWin.PaneAlive();
                UiCtrl c; MsaaNode n;
                if (FindBoth(ImportFingerprint, global, out c, out n)) { _panelMisses = 0; return false; }
                if (FindBoth(AttachFingerprint, global, out c, out n)) { _panelMisses = 0; return false; }
                if (!global) return false;
                return ++_panelMisses >= 2;
            };
            Settle acc = TryAccept("④（替换面板提交）", UiWin.PropertyPane(), isDone, panelGone);
            if (acc != Settle.None)
            {
                if (acc == Settle.Swapped) return;    // Settled 已打过「✔ … 之后外链已换成新 ics」
                // 面板没了但外链还没换＝CAXA 还在换链接/重算约束，别再投第二遍提交
                FinishAfterCommit("④", newBase, isDone);
                return;
            }

            // ⑤ 重新附加约束界面：指纹取「约束元素」（面板 选项 里也有「重新附加约束」复选框，不能用它）。
            //    实机取证：普通零件根本不弹这一页，所以这步「没等到」是正常分支，不再据此再跑一遍提交梯子。
            UiCtrl attach = null;
            MsaaNode attachNode = null;
            bool attachSeen = false;
            bool[] swapped5 = new bool[1];
            int[] polls5 = new int[1];
            Wait("⑤ 等「约束元素」界面", 6000, delegate
            {
                swapped5[0] = isDone();
                if (swapped5[0]) return true;
                attachSeen = FindBoth(AttachFingerprint, polls5[0]++ % 5 == 0,
                    out attach, out attachNode);
                return attachSeen;
            }, 300);
            if (swapped5[0]) { Log("    外链已换，替换生效"); return; }

            if (attachSeen)
            {
                LogHit("⑤", attach, attachNode);
                UiWin.Dump("重新附加约束界面");
                _panelMisses = 0;
                Settle a5 = TryAccept("⑤（重新附加约束提交）", UiWin.PropertyPane(), isDone, panelGone);
                if (a5 != Settle.None)
                {
                    if (a5 == Settle.Swapped) { Log("    ⑤ 外链已换，替换生效"); return; }
                    FinishAfterCommit("⑤", newBase, isDone);
                    return;
                }
            }
            else Log("    没有「约束元素」界面（普通零件本来就不弹这一页），直接等外链");

            // ⑥ 权威判定：外链真的换成新 ics 才算成功（判据遍历设计树，不读活选择）
            ReplaceTool.CheckStop();
            Log("    ⑥ 等外链换成 " + newBase + ".ics（最长 120 秒，判据遍历设计树，不用再点零部件）");
            bool swapped = Wait("⑥ 等外链换成 " + newBase + ".ics", 120000, isDone, 500);
            if (!swapped)
            {
                UiWin.Dump("代点链走完仍未替换");
                throw new Exception("自动代点走完仍未在设计树里找到外链指向 " + newBase + ".ics 的元素。\n" +
                    "面板还差一次确定/✓ 的话，现在手工点一次即可（判据遍历设计树，不要求选中件）。\n" +
                    "请看设计树里是否已经是 \"" + newBase + ".ics - …\"，并把跟踪日志 " +
                    Diagnostics.LogPath + " 发回来定位（取证块就在它末尾）。");
            }
            Log("    ⑥ 外链已换成 " + newBase + ".ics ✔");
        }
    }
}
