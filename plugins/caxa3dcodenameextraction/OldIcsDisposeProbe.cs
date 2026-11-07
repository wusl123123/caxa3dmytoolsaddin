// 机械移植自工程 caxa3dexbtracker 的 OldIcsDisposeProbe.cs（全文件）。仅改 namespace 与项目内资源名，逻辑未动。
using System;
using System.IO;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 旧 ics 处置探针（2026-11-05 两路线探索，挂在 LinkRenameSync ④ 关闭失败分支自动续跑）。
    ///
    /// 背景：外链件改名（elem.SaveAs 回链新 ics）后，旧 ics 被一个"打开文档"占用，
    ///       Close2(true) 实测未生效（09:11/09:20），资源管理器删弹"文件正在使用"。
    ///
    /// 路线一 = 释放被占用旧 ics 后删除。依次试（每步成功即短路去删）：
    ///   P1 File.Move 试锁：能改名 ⇒ 旧文件可被"改名让路"（降级解：改名成 .deleting 待删）；
    ///   P2 Close2(false)（false 语义未实测过，可能= 不保存直接关）；
    ///   P3 Close2(true)（09:20 对照组，预期败）；
    ///   P4 app.CloseFile(doc)（坑 57 记为假成功，换对象再试）。
    ///
    /// 路线二 = 内存内直接替换：占用旧 ics 的文档 cast IZSceneDoc（外链源 ics 本身就是场景文档），
    ///   Q1 枚举其根元素（证明内存副本在手）；
    ///   Q2 doc.SaveAs(新ics, Z_LINKS_IGNORE, forceOverwrite=true) —— 文档自己切到新文件名
    ///      ⇒ 旧 ics 释放可删；场景 elem 链接此前已由 elem.SaveAs 回链到新 ics，二者统一。
    ///      ★ 链接选项必须 IGNORE：SAVE_ALL 对装配体会逐个保存子零件 ⇒ 穿透 + 弹「另存为」框群（09:52 实证）。
    ///      哨兵 clickYes 布防（覆盖确认框答"是"）。
    ///
    /// ★ 判成功只认副作用：FindOpenDoc 回查 + File.Delete 后 !File.Exists（铁律 3）。
    /// ★ 全程不 Save 场景（撤销权保留）；doc.SaveAs 只动占用文档自己的文件指向。
    /// </summary>
    internal static class OldIcsDisposeProbe
    {
        /// <summary>返回 true = 旧 ics 已删除（含中间某步释放后删除成功）。</summary>
        public static bool Run(IZBaseApp app, string oldIcs, string newIcs, Action<string> log)
        {
            if (log == null) log = delegate { };
            log("probe: ═══ 旧ics处置探针开始（路线一释放四连 / 路线二内存替换）═══");
            log("probe: 旧=" + oldIcs);
            log("probe: 新=" + newIcs + (File.Exists(newIcs) ? "（在盘）" : "（不在盘！）"));

            // ── P1：File.Move 试锁（无损：改名后立刻改回）──
            // 2026-11-05 同步自 caxa3ddiffrename 时删掉了上游的 `bool renameable` 死变量
            //（赋值后从不读，只产生 CS0219 警告；P1 的结论本来就由下面那行日志给出）。
            string probePath = oldIcs + ".probe";
            try
            {
                File.Move(oldIcs, probePath);
                try { File.Move(probePath, oldIcs); log("probe: P1 改名 ✔→改回 ✔ ⇒ 锁允许 rename（降级解：可改名让路后由用户删）"); }
                catch (Exception ex) { log("probe: P1 改回失败！" + ex.Message + "（文件现在叫 " + probePath + "，请手动处理）"); return false; }
            }
            catch { log("probe: P1 改名被拒 ⇒ 文件被独占打开（rename/delete 双禁）"); }

            // ── P2/P3/P4：三种关闭手法，每步回查 ──
            string[] steps = { "P2 Close2(false)", "P3 Close2(true)", "P4 app.CloseFile" };
            foreach (string step in steps)
            {
                IZDoc holder = LinkRenameSync.FindOpenDocInternal(app, oldIcs, log);
                if (holder == null) { log("probe: " + step + " 前回查：文档已不在 ⇒ 直接进入删除"); return TryDelete(oldIcs, log); }

                if (!IsSafeToClose(app, holder, oldIcs, log)) return false;

                log("probe: " + step + " 执行（哨兵布防，弹框点否）");
                RenamePopupSentry.Arm(log);
                try
                {
                    if (step.StartsWith("P4")) app.CloseFile(holder);
                    else holder.Close2(step.EndsWith("(false)"));
                }
                catch (Exception ex)
                {
                    log("probe: " + step + " 异常: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                }
                finally { RenamePopupSentry.Disarm(); }

                if (LinkRenameSync.FindOpenDocInternal(app, oldIcs, log) == null)
                {
                    log("probe: " + step + " ⇒ 文档已关 ✔（路线一命中）");
                    return TryDelete(oldIcs, log);
                }
                log("probe: " + step + " ⇒ 回查仍开着 ✘");
            }

            // ── Q1：占用文档身份与内存副本 ──
            IZDoc doc = LinkRenameSync.FindOpenDocInternal(app, oldIcs, log);
            if (doc == null) return TryDelete(oldIcs, log);
            IZSceneDoc sd = doc as IZSceneDoc;
            if (sd == null)
            {
                log("probe: Q1 占用文档不是场景文档（cast IZSceneDoc 失败），路线二不可行");
                return false;
            }
            try
            {
                object raw = sd.GetChildElements();
                Array arr = raw as Array;
                int n = arr == null ? 0 : arr.Length;
                log("probe: Q1 占用文档根元素数=" + n);
                for (int i = 0; i < Math.Min(n, 8); i++)
                {
                    interop.ICApiIronCAD.IZElement e = arr.GetValue(i) as interop.ICApiIronCAD.IZElement;
                    if (e == null) continue;
                    string cn = LinkRenameSync.TryGetInternal(() => e.Name, "?");
                    int ct = LinkRenameSync.TryGetInternal(() => (int)e.Type, -1);
                    log("probe: Q1   child[" + i + "] name=[" + cn + "] type=" + ct);
                }
            }
            catch (Exception ex) { log("probe: Q1 枚举根元素异常: " + ex.Message); }

            // ── Q2：内存内直接替换 ──
            if (string.IsNullOrEmpty(newIcs) || !File.Exists(newIcs))
            {
                log("probe: Q2 新 ics 不在盘上，跳过（内存替换需要一个已回链的目标文件）");
                return false;
            }
            log("probe: Q2 doc.SaveAs(新ics, Z_LINKS_IGNORE, forceOverwrite=true) —— 只保存本文档不碰子零件（哨兵 clickYes 布防）");
            RenamePopupSentry.Arm(log, true);
            try { sd.SaveAs(newIcs, eZLinksSaveOptions.Z_LINKS_IGNORE, true); }
            catch (Exception ex)
            {
                log("probe: Q2 SaveAs 异常: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
            }
            finally { RenamePopupSentry.Disarm(); }

            string docName = LinkRenameSync.TryGetInternal(() => doc.Name, "(取不到)");
            bool switched = string.Equals(docName, newIcs, StringComparison.OrdinalIgnoreCase);
            log("probe: Q2 doc.Name 回读 = [" + docName + "] ⇒ " + (switched ? "已切到新文件 ✔" : "未切 ✘"));
            if (switched)
            {
                log("probe: Q2 路线二命中：占用文档已整体切换到新 ics（内存替换完成），旧 ics 应已释放");
                return TryDelete(oldIcs, log);
            }

            // ── 全败结论 ──
            IZDoc still = LinkRenameSync.FindOpenDocInternal(app, oldIcs, log);
            log("probe: ═══ 探针结论 ═══ 两路线全部手段未释放旧 ics（占用" +
                (still != null ? "依旧" : "已消失但删除未完成") + "）——请把本段日志发回分析");
            return false;
        }

        private static bool IsSafeToClose(IZBaseApp app, IZDoc holder, string oldIcs, Action<string> log)
        {
            string holderName = LinkRenameSync.TryGetInternal(() => holder.Name, "");
            string scenePath = LinkRenameSync.TryGetInternal(delegate()
            {
                IZDoc d = app.ActiveDoc;
                return d != null ? d.Name : "";
            }, "");
            if (!string.IsNullOrEmpty(holderName) &&
                string.Equals(holderName, scenePath, StringComparison.OrdinalIgnoreCase))
            {
                log("probe: 占用者就是当前活动场景文档，拒绝关闭（会关掉用户的场景）");
                return false;
            }
            return true;
        }

        private static bool TryDelete(string oldIcs, Action<string> log)
        {
            if (!File.Exists(oldIcs)) { log("probe: 旧 ics 已不在盘上 ✔"); return true; }
            try { File.Delete(oldIcs); }
            catch (Exception ex)
            {
                log("probe: File.Delete 异常: " + ex.Message + " —— 保留旧文件");
                return false;
            }
            bool gone = !File.Exists(oldIcs);
            log("probe: 删除回查：" + (gone ? "旧 ics 已删除 ✔" : "旧 ics 仍在盘上 ✘"));
            return gone;
        }
    }
}
