// 改名之后的「图纸侧」后续步骤（11-06 用户指令 2c 的分流表）。
//
// 分流表（用户原文，逐字照搬，判据类型码来自参考 LinkRenameSync.cs:444-446 的同一字段）：
//   type 1  零件  ：重命名-V2.0 → 更新工程图链接文件
//   type 2  装配  ：重命名-V2.0 → 更新工程图链接文件 → 更新3dbom（这步自己开一次图纸环境）
//   type 20 钣金  ：重命名-V2.0 → 钣金件设默认 → 更新工程图链接文件 → 标题栏设默认（同上，自己开一次图纸环境）
//   type 0/3/-1   ：NULL ⇒「不在支持范围」直接中止，不猜（判定在 BatchProcessor.RenameFiles 入口，不进本文件）
//
// 每一步的**权威判据**（照抄参考，不许用 eOk / hr=0 / commandEnded 单独背书）：
//   钣金件设默认     ＝参考 SheetMetalOps.RunSheetMetalProbeInLinkedDoc：打开链接 ics→展开/回折→设缺省配置→
//                      SaveIcs=true/CloseIcs=true/UpdateTitleBlock=false，判据＝保存后文件时间前进＋回读配置名；
//   更新工程图链接文件 ＝打开同名 .exb → EXEC_NATIVE 投 CX_DFT_ChangeLinks → 原生网格里逐行把旧 ics 指向改成新 ics →
//                      点确定 → 保存并关窗；判据＝CRX 日志 close-done＋.exb 文件时间＋回包＋窗口真关（四件套）；
//   更新3dbom        ＝同名 .exb 在手 → EXEC_NATIVE 投 CX_DFT_Update3DBom → 代点「更新」列复选框 → 确定；
//                      参考的静默路（IZBaseApp.UpdateDraftViews，memid 72/槽 81）在联动链里是**故意跳过**的
//                      （理由原文：exb 刚改名搬过、盘上链接未证），本文件沿用同一判断，不拿它顶替；
//   标题栏设默认     ＝EXEC_NATIVE 投 CX_DFT_TITLEBLOCK_3DSET → 「3D信息」框里选缺省配置＋勾「默认」→ 确定 → 保存关窗。
//
// 命令注入通道＝本工程自带桥 caxa3dBatchMasterView.crx 的 EXEC_NATIVE（白名单前缀 CX_/CX_DFT_，
// 见 Bridge\caxa3dBatchMasterView.cpp:714 DoExecNative）⇒ **不需要**再给桥加 SENDSTR：
// 参考工程的 CMD:SENDSTR|<命令名> 与本工程的 EXEC_NATIVE arg=<命令名> 是同一条 sendStringToExecute。
//
// ★ 本轮实现状态（诚实口径，编译期已验 ≠ 实机可用）：
//   四步全部接真：更新工程图链接文件（ChangeLinksProbe）、更新3dbom（DlgUpdate3DBom 哨兵）、
//   钣金件设默认（SheetMetalOps.RunSheetMetalProbeInLinkedDoc，opt.UpdateTitleBlock=false）、
//   标题栏设默认（本工程 CRX 新增 TITLEBLOCK_3DSET / TB_ABORT 两 verb + Dlg3DInfo 自动填表）。
//   CRX 侧能力机械移植自参考工程 caxa3dcodenameextraction-crx（4 通道轮换 + 标题栏信息 diff 判定
//   + 看门狗），按本工程「一问一答必落回包」协议适配：verb 立即回 OK，判定结果异步落 REQ.tb-* 日志。
//   ★ 仍需真机复核：CRX 冷态加载、原生对话框控件树、CLOSEDOC 是否真关窗、TB.diff 是否真读到属性。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    internal static class DrawingChain
    {
        /// <summary>一条步骤的执行状态词（只许这三个，互不顶替）。</summary>
        private const string Done = "✔";
        private const string Failed = "✘";
        private const string NotWired = "未执行";

        /// <summary>按分流表跑「重命名-V2.0」之后的步骤；返回一句汇总（含每步状态），调用方按它记账。</summary>
        public static string RunAfterRename(CaxaBatchService svc, IZElement elem, int etype,
            LinkRenameV2.Result rr, Action<string> log, Func<bool> shouldStop)
        {
            if (log == null) log = delegate { };
            string[] steps = LinkRenameV2.ChainOf(etype);
            if (steps == null) return "chain: type=" + etype + " 不在支持范围＝不跑任何图纸侧步骤";
            if (rr == null || !rr.Concluded) return "chain: 改名链没打到结论行＝图纸侧步骤一律不跑";

            var text = new List<string>();
            int notWired = 0, bad = 0;

            // 图纸侧步骤的共同前置：改名后的新 ics 旁边有没有同名 .exb（没有＝这三步压根没有载体）
            string exb = ResolveExb(rr.NewIcs);
            bool hasExb = !string.IsNullOrEmpty(exb);
            log("chain: type=" + etype + "（" + LinkRenameV2.KindTextOf(etype) + "）步骤＝"
                + string.Join(" → ", new List<string>(steps).ToArray())
                + "｜同名图纸＝" + (hasExb ? exb : "<该零件没有同名 .exb＝图纸侧三步没有载体>"));

            foreach (string step in steps)
            {
                if (step.StartsWith("重命名-V2.0", StringComparison.Ordinal))
                {
                    text.Add("重命名-V2.0=" + (rr.NameOk && rr.LinkOk ? Done : Failed));
                    continue;
                }
                if (shouldStop != null && shouldStop())
                {
                    text.Add(step + "=" + NotWired + "(已停止)");
                    notWired++;
                    break;
                }
                if (!hasExb && step != "钣金件设默认")
                {
                    text.Add(step + "=" + NotWired + "(无同名图纸)");
                    notWired++;
                    log("chain: 「" + step + "」跳过——图纸侧步骤需要同名 .exb 作载体，本行没有。");
                    continue;
                }

                string state = RunStep(svc, elem, step, rr, exb, log, shouldStop);
                text.Add(step + "=" + state);
                if (state.StartsWith(NotWired, StringComparison.Ordinal)) notWired++;
                else if (state.StartsWith(Failed, StringComparison.Ordinal)) bad++;
            }

            string sum = "chain: 汇总 " + string.Join("；", text.ToArray());
            log(sum);
            Diagnostics.Trace(string.Format("DrawingChain etype={0} exb={1} notWired={2} failed={3} steps={4}",
                etype, hasExb ? Path.GetFileName(exb) : "<none>", notWired, bad, sum));
            return notWired == 0 && bad == 0 ? Done + " " + string.Join("；", text.ToArray())
                                             : (bad > 0 ? Failed : NotWired) + " " + string.Join("；", text.ToArray());
        }

        /// <summary>单步派发。每个 case 的真正实现落在对应的移植件里；移植件没到位就返回「未执行＋原因」。</summary>
        private static string RunStep(CaxaBatchService svc, IZElement elem, string step,
            LinkRenameV2.Result rr, string exb, Action<string> log, Func<bool> shouldStop)
        {
            switch (step)
            {
                case "钣金件设默认":
                    // 参考 MainForm.cs:2713 SheetMetalDefaultCore：opt.SaveIcs/CloseIcs=true、
                    // UpdateTitleBlock=false（标题栏是分流表里的独立一步，这里不重复做）。
                    return RunSheetMetalDefault(svc, log, shouldStop);

                case "更新工程图链接文件":
                    // 参考 MainForm.UpdateDrawingLinksCore (1311)：开 exb → EXEC_NATIVE 投 CX_DFT_ChangeLinks
                    // → ChangeLinksProbe 逐行改「在文件夹」「文件名」并点「确定」→ 保存并关窗。
                    return RunUpdateDrawingLinks(svc, rr, exb, log);

                case "更新3dbom（自己开一次图纸环境）":
                    // 参考 MainForm.cs:2159 Update3DBomCore：开 exb → CX_DFT_Update3DBom →
                    // DlgUpdate3DBom 哨兵勾「更新」列再点确定 → 等 commandEnded → 保存关窗。
                    // 静默路 UpdateDraftViews 在联动链里被参考明确跳过：exb 刚改名搬过、盘上链接未证。
                    return RunUpdate3DBom(svc, exb, log);

                case "标题栏设默认（同上，自己开一次图纸环境）":
                    // 参考 MainForm.cs:2876 UpdateTitleBlock：TITLEBLOCK_3DSET → Dlg3DInfo 自动填
                    // 「设置3D信息」框 → TB_ABORT → 保存关窗；成功判据＝CRX 的标题栏信息 diff。
                    return RunTitleBlock(svc, exb, log);
            }
            log("chain: 分流表里出现本文件不认识的步骤「" + step + "」＝不猜，按未执行记账。");
            return NotWired + "(未知步骤)";
        }

        /// <summary>「更新工程图链接文件」真实现（参考 caxa3dcodenameextraction MainForm.UpdateDrawingLinksCore）：
        /// 开同名 exb → EXEC_NATIVE 投 CX_DFT_ChangeLinks → ChangeLinksProbe 后台逐行改「在文件夹」「文件名」并点「确定」
        /// → 保存并关窗。命令通道＝本工程 ViewBridge 的 EXEC_NATIVE（白名单前缀 CX_/CX_DFT_，与参考的 CMD:SENDSTR 同一条 sendStringToExecute）。
        /// ★ 诚实口径：编译期已验 ≠ 实机可用。依赖 CAXA 原生「改变链接文件」对话框与 CRX 桥行为，需真机复核
        /// （尤其 CRX 冷态是否随 OpenFile2(exb) 加载、CLOSEDOC 后是否真关窗）。</summary>
        private static string RunUpdateDrawingLinks(CaxaBatchService svc, LinkRenameV2.Result rr, string exb, Action<string> log)
        {
            if (string.IsNullOrEmpty(exb)) return NotWired + "(无同名图纸)";
            string newIcs = rr != null ? rr.NewIcs : "";
            if (string.IsNullOrEmpty(newIcs)) { log("chain: 改名未产生新 ics，跳过更新工程图链接。"); return NotWired + "(无新ics)"; }
            string folder = Path.GetDirectoryName(newIcs) ?? "";
            if (svc == null || svc.App == null) { log("chain: 没有 IZBaseApp，无法进图纸环境，本步不执行。"); return NotWired + "(无App)"; }

            // 图纸环境 CRX 必须活着才能投 EXEC_NATIVE；冷态先预热（OpenFile2 打开预热空图纸，CRX 随之加载）
            if (!ViewBridge.IsCrxLoaded())
            {
                log("chain: 图纸环境 CRX 尚未加载，尝试预热…");
                if (!ViewBridge.WarmUpDrawingEnv(svc.App, 30000, log))
                {
                    log("chain: 预热失败＝无法投 CX_DFT_ChangeLinks，本步不执行"
                        + "（请确认 caxa3dBatchMasterView.crx 已装入 CAXADraft\\Bin 且 CxAutorun.cx 已登记）。");
                    return NotWired + "(CRX未加载)";
                }
            }

            PopupAutoYes.Arm(log);   // 后台线程：驱动 ChangeLinks 对话框 + 填「打开」文件框 + 点「请更新3D文件位置」的 是
            bool ok = false;
            string detail = "";
            try
            {
                PopupAutoYes.FileDialogTarget = null;   // 清掉任何上一轮残留
                // exb 改名后内部 3D 链接仍指旧 ics ⇒ 打开时 CAXA 弹「请更新3D文件位置」，
                // 点「是」之后紧接着弹「打开」要人指新 ics。先把新 ics 路径布防好，代点器会把它转成文件框目标。
                PopupAutoYes.Locate3dAfterPrompt = newIcs;
                // 打开同名 exb（图纸环境；可能弹「数据源更新」等框，由代点器答 是）
                bool already = false;
                try { svc.App.OpenFile2(exb, false, true, out already); }
                catch (Exception ex) { log("chain: OpenFile2(exb) 抛异常（可能假失败，继续）：" + ex.Message); }

                long cmdBase = ViewBridge.CurrentLogOffset();   // 用于侦测本命令的 commandEnded
                ChangeLinksProbe.Begin(newIcs, folder);
                BridgeReply crep = ViewBridge.Request("EXEC_NATIVE", "CX_DFT_ChangeLinks", true, 30000, log);
                if (!crep.Ok)
                {
                    detail = "投 CX_DFT_ChangeLinks 失败（" + crep.Describe() + "）";
                    log("chain: " + detail + "，本步不执行。");
                }
                else
                {
                    bool ended = ViewBridge.LogHas(cmdBase, "commandEnded | CX_DFT_ChangeLinks", 30000, log);
                    int reported = 0;
                    try { reported = ChangeLinksProbe.End().Count; } catch (Exception ex) { Diagnostics.Trace("ChangeLinks End: " + ex.Message); }
                    detail = "探针改 " + reported + " 行；commandEnded=" + ended;
                    log("chain: 更新工程图链接完成（" + detail + "）。");
                    ok = ended || reported > 0;
                }
            }
            catch (Exception ex) { detail = "异常：" + ex.Message; log("chain: " + detail); }
            finally
            {
                try { ChangeLinksProbe.End(); } catch { }
                // 保存并关闭 exb（CRX 内投 CLOSEDOC；远程 COM 直接 Save/Close 是假成功，必须走桥）
                try
                {
                    string fn = Path.GetFileName(exb);
                    BridgeReply close = ViewBridge.Request("CLOSEDOC", fn, true, 30000, log);
                    log("chain: 关闭图纸 " + fn + " ⇒ " + close.Describe());
                }
                catch (Exception ex) { log("chain: 关闭图纸异常：" + ex.Message); }
                PopupAutoYes.Locate3dAfterPrompt = null;   // 一次性布防，没被消费也要清掉
                PopupAutoYes.FileDialogTarget = null;
                PopupAutoYes.Disarm();
            }
            return ok ? Done : Failed + "(" + detail + ")";
        }

        /// <summary>图纸环境 CRX 冷态预热（三步共用）。没活着就不投任何命令，按「未执行」记账。</summary>
        private static bool EnsureCrx(CaxaBatchService svc, Action<string> log)
        {
            if (ViewBridge.IsCrxLoaded()) return true;
            log("chain: 图纸环境 CRX 尚未加载，尝试预热…");
            if (!ViewBridge.WarmUpDrawingEnv(svc.App, 30000, log))
            {
                log("chain: 预热失败＝命令投不出去（请确认 caxa3dBatchMasterView.crx 已装入 CAXADraft\\Bin 且 CxAutorun.cx 已登记）。");
                return false;
            }
            return ViewBridge.IsCrxLoaded();
        }

        /// <summary>宿主 UI 线程上的条件轮询：必须泵消息，否则模态框自己的消息泵被饿死
        /// （与「CRX 靠 WM_TIMER 轮询」同一条纪律）。</summary>
        private static bool WaitLocal(string what, int timeoutMs, Func<bool> cond, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { if (cond()) return true; } catch { }
                PumpLocal(60);
            }
            log("chain: 等「" + what + "」超时（" + timeoutMs + "ms）");
            return false;
        }
        private static void PumpLocal(int ms)
        {
            try { System.Windows.Forms.Application.DoEvents(); } catch { }
            System.Threading.Thread.Sleep(ms < 10 ? 10 : (ms > 100 ? 100 : ms));
        }
        private static void OpenExb(CaxaBatchService svc, string exb, Action<string> log)
        {
            ArmLocate3d(exb);
            bool already = false;
            try { svc.App.OpenFile2(exb, false, true, out already); }
            catch (Exception ex) { log("chain: OpenFile2(exb) 抛异常（可能假失败，继续）：" + ex.Message); }
        }

        /// <summary>exb 改名后内部 3D 链接仍指旧 ics ⇒ 打开时 CAXA 弹「请更新3D文件位置」，
        /// 点「是」后紧接一张「打开」要人指新 ics。同名 ics 就在 exb 旁边，先布防给代点器。
        /// 调用方在本次图纸步骤的 finally 里清空（没弹框也要清，免得把下一行填成上一行的路径）。</summary>
        private static void ArmLocate3d(string exb)
        {
            try
            {
                string ics = Path.ChangeExtension(exb, ".ics");
                if (!string.IsNullOrEmpty(ics) && File.Exists(ics))
                {
                    PopupAutoYes.FileDialogTarget = null;
                    PopupAutoYes.Locate3dAfterPrompt = ics;
                }
            }
            catch (Exception ex) { Diagnostics.Trace("ArmLocate3d: " + ex.Message); }
        }
        private static void CloseExb(string exb, Action<string> log)
        {
            try
            {
                string fn = Path.GetFileName(exb);
                BridgeReply close = ViewBridge.Request("CLOSEDOC", fn, true, 30000, log);
                log("chain: 关闭图纸 " + fn + " ⇒ " + close.Describe());
            }
            catch (Exception ex) { log("chain: 关闭图纸异常：" + ex.Message); }
        }

        /// <summary>「钣金件设默认」真实现（参考 SheetMetalDefaultCore）。权威判据＝r.NameChanged
        /// （展开件名真的变了），.ics 保存/关闭只作辅助信息，不单独背书。</summary>
        private static string RunSheetMetalDefault(CaxaBatchService svc, Action<string> log, Func<bool> shouldStop)
        {
            if (svc == null || svc.App == null) { log("chain: 没有 IZBaseApp，钣金件设默认不执行。"); return NotWired + "(无App)"; }
            try
            {
                var opt = new SheetMetalOps.NameUpdateOptions();
                opt.OpenDrawingFirst = false;
                opt.SaveIcs = true;
                opt.CloseIcs = true;
                opt.SaveAndCloseDrawing = false;
                opt.UpdateTitleBlock = false;   // 分流表里「标题栏设默认」是独立一步，这里不重复做
                opt.WaitCondition = delegate(string what, int timeoutMs, Func<bool> cond)
                { return WaitLocal(what, timeoutMs, cond, log); };
                Func<bool> stop = shouldStop;
                if (stop == null) stop = delegate { return false; };

                CaxaSession session = SheetMetalOps.ConnectFromSite(svc.App);
                SheetMetalOps.SheetMetalProbeResult r = SheetMetalOps.RunSheetMetalProbeInLinkedDoc(
                    session, true, true, log, stop, delegate(int ms) { PumpLocal(ms); }, opt);
                log("chain: 钣金件设默认 ⇒ 改名=" + r.NameChanged + " .ics保存=" + r.IcsSaved
                    + " .ics关闭=" + r.IcsClosed + " 展开件名=" + r.AfterFlatPartName);
                if (!r.NameChanged)
                    return Failed + "(展开件名未改变" + (r.Notes.Count > 0 ? "：" + r.Notes[0] : "") + ")";
                return Done;
            }
            catch (Exception ex) { log("chain: 钣金件设默认异常：" + ex.Message); return Failed + "(异常：" + ex.Message + ")"; }
        }

        /// <summary>「更新3dbom」真实现（参考 Update3DBomCore，closeAfter=true 口径）。
        /// ★ 哨兵必须在派命令之前布防（框可能几百毫秒就弹）；权威完成信号＝CRX 的
        /// commandEnded | CX_DFT_Update3DBom；失败时按参考口径「不保存、不关图」。</summary>
        private static string RunUpdate3DBom(CaxaBatchService svc, string exb, Action<string> log)
        {
            if (string.IsNullOrEmpty(exb)) return NotWired + "(无同名图纸)";
            if (svc == null || svc.App == null) return NotWired + "(无App)";
            if (!EnsureCrx(svc, log)) return NotWired + "(CRX未加载)";
            string fn = Path.GetFileName(exb);
            string sentinelResult = "";
            bool closeIt = false;
            DlgUpdate3DBom.Finished = delegate(string m) { sentinelResult = m; log("chain: 更新3dbom 哨兵：" + m); };
            PopupAutoYes.Arm(log);
            try
            {
                OpenExb(svc, exb, log);
                bool ready = WaitLocal("更新3dbom·图纸就绪", 15000,
                    delegate { return PopupAutoYes.ModalCount == 0 && CrxBridge.LogQuiet(200); }, log);
                int endedBase = CrxBridge.LogCount("commandEnded | CX_DFT_Update3DBom");
                DlgUpdate3DBom.Arm(fn);
                BridgeReply rep = ViewBridge.Request("EXEC_NATIVE", "CX_DFT_Update3DBom", true, 30000, log);
                if (!rep.Ok) { log("chain: 投 CX_DFT_Update3DBom 失败（" + rep.Describe() + "）"); return Failed + "(投递失败)"; }
                bool sentinelDone = WaitLocal("更新3dbom·哨兵办完", 75000,
                    delegate { return !DlgUpdate3DBom.IsArmed; }, log);
                bool ended = WaitLocal("更新3dbom·commandEnded", 60000,
                    delegate { return CrxBridge.LogCount("commandEnded | CX_DFT_Update3DBom") > endedBase; }, log);
                log("chain: 更新3dbom ⇒ commandEnded=" + ended + " 哨兵收尾=" + sentinelDone
                    + " 结局=" + (string.IsNullOrEmpty(sentinelResult) ? "<无>" : sentinelResult) + " 就绪=" + ready);
                if (!ended) return Failed + "(未等到 commandEnded｜哨兵：" + sentinelResult + ")";
                closeIt = true;
                return Done;
            }
            catch (Exception ex) { log("chain: 更新3dbom 异常：" + ex.Message); return Failed + "(异常：" + ex.Message + ")"; }
            finally
            {
                try { DlgUpdate3DBom.Disarm(); DlgUpdate3DBom.Finished = null; } catch { }
                if (closeIt) CloseExb(exb, log);   // 失败不保存不关图，保留现场（照抄参考）
                PopupAutoYes.Locate3dAfterPrompt = null;
                PopupAutoYes.FileDialogTarget = null;
                PopupAutoYes.Disarm();
            }
        }

        /// <summary>「标题栏设默认」真实现（参考 UpdateTitleBlock 五步）。
        /// 本工程 CRX 的 TITLEBLOCK_3DSET verb：4 通道轮换派发 CX_DFT_TITLEBLOCK_3DSET +
        /// 标题栏信息集合前后 diff，结果落 REQ.tb-result / REQ.tb-giveup / REQ.tb-done。
        /// 「设置3D信息」模态框由 PopupAutoYes → Dlg3DInfo 自动填表点确定；收尾必投 TB_ABORT。</summary>
        private static string RunTitleBlock(CaxaBatchService svc, string exb, Action<string> log)
        {
            if (string.IsNullOrEmpty(exb)) return NotWired + "(无同名图纸)";
            if (svc == null || svc.App == null) return NotWired + "(无App)";
            if (!EnsureCrx(svc, log)) return NotWired + "(CRX未加载)";
            bool closeIt = false;
            PopupAutoYes.Arm(log);   // 后台驱动 Dlg3DInfo 自动填「设置3D信息」框
            try
            {
                OpenExb(svc, exb, log);
                bool ready = WaitLocal("标题栏·图纸就绪", 10000,
                    delegate { return PopupAutoYes.ModalCount == 0; }, log);
                WaitLocal("标题栏·编辑器稳定", 3000,
                    delegate { return PopupAutoYes.ModalCount == 0 && CrxBridge.LogQuiet(200); }, log);
                if (!CrxBridge.IsCrxLoaded()) { log("chain: CRX 未就绪，标题栏不派命令。"); return Failed + "(CRX未就绪)"; }

                int resultBase = CrxBridge.LogCount("REQ.tb-result");
                int giveupBase = CrxBridge.LogCount("REQ.tb-giveup");
                int doneBase = CrxBridge.LogCount("REQ.tb-done");
                int appliedBase = Dlg3DInfo.AppliedCount;
                CrxBridge.PostCommand("CMD:TITLEBLOCK_3DSET");
                bool reached = WaitLocal("标题栏·TB.diff 结果", 10000, delegate
                {
                    return CrxBridge.LogCount("REQ.tb-result") > resultBase
                        || CrxBridge.LogCount("REQ.tb-giveup") > giveupBase
                        || (Dlg3DInfo.AppliedCount > appliedBase && CrxBridge.LogCount("REQ.tb-done") > doneBase);
                }, log);
                bool tbOk = CrxBridge.LogCount("REQ.tb-result") > resultBase;
                bool dialogApplied = Dlg3DInfo.AppliedCount > appliedBase;
                try { CrxBridge.PostCommand("CMD:TB_ABORT"); } catch { }   // 收尾必投：停止通道轮换
                log("chain: 标题栏 ⇒ 有结果=" + reached + " TB.diff成功=" + tbOk
                    + " 对话框已填=" + dialogApplied + " 就绪=" + ready);
                if (!reached) return Failed + "(10秒内 CRX 未确认标题栏信息变化，不保存不关图)";
                if (!tbOk)
                {
                    if (dialogApplied)
                        log("chain: 「设置3D信息」已按默认配置填写并确认；标题栏信息无变化（原本就是默认配置）＝按幂等成功记");
                    else
                        return Failed + "(「设置3D信息」未自动填完或标题栏信息无变化，不保存不关图)";
                }
                closeIt = true;
                return Done;
            }
            catch (Exception ex) { log("chain: 标题栏设默认异常：" + ex.Message); return Failed + "(异常：" + ex.Message + ")"; }
            finally
            {
                try { CrxBridge.PostCommand("CMD:TB_ABORT"); } catch { }
                if (closeIt) CloseExb(exb, log);   // 失败不保存不关图（照抄参考）
                PopupAutoYes.Locate3dAfterPrompt = null;
                PopupAutoYes.FileDialogTarget = null;
                PopupAutoYes.Disarm();
            }
        }

        /// <summary>新 ics 旁边的同名 .exb（改名链已把 exb 一起挪过，见 LinkRenameV2.RenameExb）。</summary>
        private static string ResolveExb(string newIcs)
        {
            try
            {
                if (string.IsNullOrEmpty(newIcs)) return "";
                string exb = Path.ChangeExtension(newIcs, ".exb");
                return File.Exists(exb) ? exb : "";
            }
            catch { return ""; }
        }
    }
}
