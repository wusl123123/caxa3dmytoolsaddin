// 机械移植自工程 caxa3dexbtracker 的 LinkRenameSync.cs（全文件）。仅改 namespace 与项目内资源名，逻辑未动。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 「外链改名-同步版」：设计环境内选中外链零部件，改它的零件名，并同步改它的 ics / exb 文件名。
    ///
    /// ★ 准入限制（2026-11-05 用户定案，双闸）：
    ///   闸 1 —— 只允许外链件：选中元素 GetExternallyLinkedInfo 必须 linked=True，
    ///           虚拟件（linked=False）一律拒绝（情况二路线已按坑 156 删除）；
    ///   闸 2 —— 当前场景文档 ≠ 外链源文件：口径是 零件B.ics---零件A(外链)，
    ///           若前台场景文档本身就是 零件A.ics（全路径或文件名相同），改名会动到用户
    ///           正打开的场景 ⇒ 拒绝执行。
    ///
    /// 流程（外链件）：零件B.ics 内有 零件A(外链→零件A.ics)，还有图纸 零件A.exb。
    ///   ① IZPart/IZAssembly.SaveAs(零件C.ics, vbLinkToScene=true)：新 ics 落盘 + 场景元素回链
    ///      （★ 08:16 实机首个成功样本：回读链接=新路径 ✔，新文件 69632 B ✔，耗时 ~0.8s）；
    ///   ② IZElement.set_Name(零件C) + 回读（带约束件可能弹改名框 ⇒ 哨兵自动答「取消」，坑 149）；
    ///   ③ 零件A.exb → 零件C.exb（File.Move；被占用=图纸开着 ⇒ 跳过并提示）；
    ///   ④ 删除旧 ics（先关占用文档 Close2(true)，再 File.Delete）。
    ///   结果：零件B.ics---零件C(外链→零件C.ics)，零件C.ics / 零件C.exb 为新建文件。
    ///
    /// ★ 判成功只认权威副作用信号（规则铁律 3）：
    ///   情况一 = GetExternallyLinkedInfo 回读 == 新路径；情况二 = 新 ics 文件在盘上 + doc.Name 回读。
    /// ★ 路线A set_ModelLinkPath 已判死（08:16 实机 set 同样 E_FAIL，读写两向全关），代码里不留（坑 156）。
    /// ★ 旧文件一律不删：改名后的 exb 内部链接仍指向旧 ics，删除会断图纸视图。
    ///   图纸侧用 2027 已通的「更新工程图链接文件」改完链接后，旧 ics/旧 exb 才可手动删除。
    /// ★ 情况一的场景改动在内存（未 Save 父文档）：保存零件B.ics 后才落盘；撤销 = 关闭不保存。
    /// ★ 全部 COM 调用在 UI 线程（= CAXA 主 STA 线程）；哨兵在后台线程代点。
    /// </summary>
    internal static class LinkRenameSync
    {
        /// <summary>
        /// 主入口。prompt(情况说明, 预填名) 由 UI 提供，返回新零件名（null/空 = 取消）。
        /// </summary>
        public static void Run(IZBaseApp app, Func<string, string, string> prompt, Action<string> log)
        {
            if (log == null) log = delegate { };
            if (prompt == null) { log("sync: prompt 为空，中止"); return; }
            if (app == null) { log("sync: IZBaseApp 为 null，中止"); return; }

            // ── 1. 场景与选区 ──
            IZDoc doc = null;
            try { doc = app.ActiveDoc; }
            catch (Exception ex) { log("sync: ActiveDoc 异常: " + ex.Message); }
            IZSceneDoc scene = doc as IZSceneDoc;
            if (scene == null)
            {
                log("sync: 前台不是 3D 设计环境（ActiveDoc 不是场景文档），中止");
                return;
            }

            List<IZElement> sel = GetSelection(scene, log);
            if (sel.Count == 0)
            {
                log("sync: 没有选中元素 —— 请在设计环境选中要改名的零部件后重试");
                return;
            }
            if (sel.Count > 1) log("sync: 选中 " + sel.Count + " 个元素，本功能只处理第一个");
            IZElement elem = sel[0];

            string name = TryGet(() => elem.Name, "(取不到名字)");
            int etype = TryGet(() => (int)elem.Type, -1);
            log("sync: 选中元素 name=[" + name + "] type=" + etype + "（1=零件 2=装配 20=钣金）");

            bool linked;
            string oldPath = LinkedFilePath(elem, out linked);
            log("sync: GetExternallyLinkedInfo: linked=" + linked + " path=[" + oldPath + "]");

            // ── 准入闸 1：只允许外链件（虚拟件/内建件一律拒绝）──
            if (!linked || string.IsNullOrEmpty(oldPath))
            {
                log("sync: 闸1拒绝：选中元素不是外链件（linked=False）。本功能只改外链件 " +
                    "（口径：零件B.ics---零件A(外链)），虚拟件请单独打开其 ics 处理");
                return;
            }

            // ── 准入闸 2：当前场景文档不能就是外链源文件本身（零件A 自己打开时拒绝改自己）──
            string scenePath = TryGet(() => doc != null ? doc.Name : null, null);
            if (!string.IsNullOrEmpty(scenePath))
            {
                bool samePath = string.Equals(NormalizePath(scenePath), NormalizePath(oldPath),
                    StringComparison.OrdinalIgnoreCase);
                bool sameName = string.Equals(Path.GetFileName(scenePath), Path.GetFileName(oldPath),
                    StringComparison.OrdinalIgnoreCase);
                if (samePath || sameName)
                {
                    log("sync: 闸2拒绝：当前场景文档 [" + scenePath + "] 就是外链源文件 " +
                        "[" + oldPath + "] —— 口径是 零件B.ics---零件A(外链)，" +
                        "不能在零件A自己被打开为场景时改它（请打开零件B.ics 后选中零件A 再执行）");
                    return;
                }
                log("sync: 准入 ✔ 场景文档 [" + scenePath + "] ≠ 外链源 [" + Path.GetFileName(oldPath) + "]，双闸通过");
            }
            else
            {
                log("sync: 警告：取不到场景文档名，闸 2 无法校验 —— 按不通过处理（破坏性操作识别不到就拒绝）");
                return;
            }

            RunCaseExternal(app, elem, scene, name, etype, oldPath, prompt, log);
        }

        // ================================================================ 外链件主流程

        private static void RunCaseExternal(IZBaseApp app, IZElement elem, IZSceneDoc scene, string name, int etype,
            string oldPath, Func<string, string, string> prompt, Action<string> log)
        {
            string dir = SafeDir(oldPath);
            string oldBase = Path.GetFileNameWithoutExtension(oldPath);
            string ext = Path.GetExtension(oldPath);

            string info = "外链件改名\r\n选中：[" + name + "]（type=" + etype + "）\r\n" +
                          "外链源：" + oldPath + "\r\n\r\n" +
                          "将执行：① SaveAs 另存新 ics 并回链  ② 改零件名  ③ 同步改名 .exb  ④ 删除旧 ics";
            string newName = prompt(info, oldBase);
            if (string.IsNullOrEmpty(newName))
            {
                log("sync: 未输入新名，已取消");
                return;
            }
            string err = ValidateNewName(newName, oldBase);
            if (err != null) { log("sync: " + err); return; }

            string newIcs = Path.Combine(dir, newName + ext);
            if (File.Exists(newIcs))
            {
                log("sync: 目标 ics 已存在，拒绝覆盖：" + newIcs + "（需求口径：目标不该存在）");
                return;
            }

            // ══ ①A 反转时序（方案A，2026-11-05 定案）：先改零件名，再让占用旧 ics 的文档自己切到新 ics ══
            // 09:35 实证：占用文档与场景外链元素是同一份内存内容（Q1 根元素名随 elem.set_Name 同步），
            // 它关不掉（P1-P4 全败）但可以被 doc.SaveAs 切换文件指向——前提是目标 ics 尚不存在（Q2 E_FAIL 根因 = 目标已被占用）。
            IZDoc holder = FindOpenDoc(app, oldPath, log);
            IZSceneDoc holderScene = holder as IZSceneDoc;
            string holderPath = holder != null ? TryGet(() => holder.Name, "") : "";
            string activePath = TryGet(delegate()
            {
                IZDoc d = app.ActiveDoc;
                return d != null ? d.Name : "";
            }, "");
            bool holderUsable = holderScene != null &&
                                !string.Equals(holderPath, activePath, StringComparison.OrdinalIgnoreCase);

            bool nameOk = false;
            bool docSwitched = false;
            bool linkFollowed = false;

            if (holderUsable)
            {
                log("sync: ①A 反转时序：占用旧 ics 的文档在手（[" + holderPath + "]），先改零件名再让它自己切到新 ics");

                // ①A-1 先改零件名（elem.set_Name 同步内存副本 → doc.SaveAs 写出的新 ics 里就是新名）
                nameOk = RenameElement(elem, newName, log, "①A-1");
                if (!nameOk)
                {
                    log("sync: ①A 零件名未改成功 ⇒ 中止（不动任何文件，无半成品）");
                    return;
                }

                // ①A-2 doc.SaveAs 切到新文件名（目标不存在 ⇒ 无占用冲突）
                // ★ 链接选项必须用 Z_LINKS_IGNORE：SAVE_ALL 会对装配体"保存全部链接"——
                //   逐个写子零件 ⇒ 弹一串「另存为」框 = 穿透子零件（09:52/09:53 实机 30+ 框实证）。
                //   IGNORE = 只保存装配体文档本身，子零件文件原封不动；零件场景无链接，不受影响。
                log("sync: ①A-2 doc.SaveAs(" + newIcs + ", Z_LINKS_IGNORE, force=true) —— 只保存本文档、不碰子零件（哨兵 clickYes 布防）");
                RenamePopupSentry.Arm(log, true);
                try { holderScene.SaveAs(newIcs, eZLinksSaveOptions.Z_LINKS_IGNORE, true); }
                catch (Exception ex)
                {
                    log("sync: ①A-2 SaveAs 异常: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                }
                finally { RenamePopupSentry.Disarm(); }

                string holderAfter = TryGet(() => holder.Name, "");
                docSwitched = string.Equals(holderAfter, newIcs, StringComparison.OrdinalIgnoreCase);
                log("sync: ①A-2 doc.Name 回读 = [" + holderAfter + "] ⇒ " +
                    (docSwitched ? "已切换到新 ics ✔（旧 ics 应已释放）" : "未切换 ✘ → 回落 elem.SaveAs 路线"));

                if (docSwitched)
                {
                    // ①A-3 关键回查：场景 elem 的外链是否自动跟上新路径（内存内直接替换的闭环判据）
                    bool lk;
                    string rp = LinkedFilePath(elem, out lk);
                    linkFollowed = lk && string.Equals(rp, newIcs, StringComparison.OrdinalIgnoreCase);
                    log("sync: ①A-3 回查场景链接 = [" + (lk ? rp : "(linked=false)") + "] ⇒ " +
                        (linkFollowed ? "自动跟上 ✔（内存内直接替换闭环，无需 elem.SaveAs）" : "未自动跟上 ✘"));
                    if (!linkFollowed)
                    {
                        // 补链尝试：目标已存在且被 holderScene 占用 ⇒ 可能 E_FAIL（Q2 实证），失败不致命
                        log("sync: ①A-3 尝试 elem.SaveAs(新ics, true) 补链（目标已被文档占用，可能 E_FAIL，失败不致命）");
                        try
                        {
                            IZAssembly asm = elem as IZAssembly;
                            if (asm != null) asm.SaveAs(newIcs, true);
                            else ((IZPart)elem).SaveAs(newIcs, true);
                            bool lk2;
                            string rp2 = LinkedFilePath(elem, out lk2);
                            linkFollowed = lk2 && string.Equals(rp2, newIcs, StringComparison.OrdinalIgnoreCase);
                            log("sync: ①A-3 补链回读 = [" + (lk2 ? rp2 : "(linked=false)") + "] ⇒ " +
                                (linkFollowed ? "补链成功 ✔" : "补链未生效 ✘"));
                        }
                        catch (Exception ex)
                        {
                            log("sync: ①A-3 补链异常: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                        }
                    }
                }
            }
            else
            {
                log("sync: ①A 反转时序不可用（占用文档" + (holder == null ? "不存在" :
                    holderScene == null ? "不是场景文档" : "是当前活动场景") + "）→ 走回落路线");
            }

            bool linkOk = docSwitched && linkFollowed;

            // ══ ①B 回落：elem.SaveAs 另存新 ics 并回链（实机已证的主路线）══
            if (!linkOk)
            {
                log("sync: ①B SaveAs(" + newIcs + ", vbLinkToScene=true) 开始（回落路线，会向磁盘写新 ics）");
                try
                {
                    IZAssembly asm = elem as IZAssembly;
                    if (asm != null) asm.SaveAs(newIcs, true);
                    else
                    {
                        IZPart part = elem as IZPart;
                        if (part == null) { log("sync: 既不是 IZAssembly 也不是 IZPart，中止"); return; }
                        part.SaveAs(newIcs, true);
                    }
                }
                catch (Exception ex)
                {
                    log("sync: ①B SaveAs 异常: " + ex.GetType().Name + " hr=0x" +
                        ex.HResult.ToString("X8") + " msg=" + ex.Message);
                    log("sync: 中止 —— 未做任何后续改名（链接仍是旧路径，无半成品）");
                    return;
                }

                bool linked;
                string rb = LinkedFilePath(elem, out linked);
                linkOk = linked && string.Equals(rb, newIcs, StringComparison.OrdinalIgnoreCase);
                log("sync: ①B 回读链接 = [" + (linked ? rb : "(linked=false)") + "] ⇒ " +
                    (linkOk ? "已回链到新 ics ✔" : "未变 ✘ —— 中止，无半成品"));
                if (!linkOk) return;
                log("sync: ①B 新 ics 已落盘：" + File.Exists(newIcs) +
                    (File.Exists(newIcs) ? "（" + new FileInfo(newIcs).Length + " B）" : ""));

                // 回落路线里零件名还没改
                if (!nameOk) nameOk = RenameElement(elem, newName, log);
            }

            // ── ③ 同步改名 exb ──
            bool exbSafe = RenameExb(dir, oldBase, newName, log);

            // ── ④ 删除旧 ics ──
            if (docSwitched && !linkFollowed)
            {
                log("sync: ④ 文档已切到新 ics 但场景链接未跟上 ⇒ 旧 ics 暂留（删了场景外链解析会断，待保存/重开场景后手动删）");
            }
            else
            {
                DeleteOldIcs(app, oldPath, newIcs, exbSafe, log);
            }

            // ── 结论 ──
            string nameAfter = TryGet(() => elem.Name, "(取不到)");
            log("sync: ═══ 结论（外链件" + (docSwitched ? "·反转时序" : "·回落") + "）═══ 链接：" +
                (linkOk ? "✔ 已指向 " + newIcs : "✘") +
                "；零件名：" + (nameOk ? "✔ [" + nameAfter + "]" : "✘ 仍为 [" + nameAfter + "]") +
                "；exb：" + (exbSafe ? "✔ 已同步改名" : "✘ 未改名（见 ③ 日志）"));
            log("sync: 场景改动在内存（未保存零件B.ics）：保存后链接才落盘；撤销 = 关闭不保存");
        }

        // ================================================================ 公共步骤

        /// <summary>set_Name + 哨兵 + 回读。返回回读是否等于新名。tag = 日志步骤号（反转时序时提前到 ①A-1 调用）。</summary>
        private static bool RenameElement(IZElement elem, string newName, Action<string> log, string tag = "②")
        {
            log("sync: " + tag + " set_Name(" + newName + ") 开始（哨兵已布防，防改名框回滚，坑 149）");
            RenamePopupSentry.Arm(log);
            try
            {
                try { elem.Name = newName; }
                catch (Exception ex)
                {
                    log("sync: " + tag + " set_Name 异常: " + ex.GetType().Name + " hr=0x" +
                        ex.HResult.ToString("X8") + " msg=" + ex.Message);
                }
            }
            finally { RenamePopupSentry.Disarm(); }

            string after = TryGet(() => elem.Name, "(取不到)");
            bool ok = string.Equals(after, newName, StringComparison.Ordinal);
            log("sync: " + tag + " 回读零件名 = [" + after + "] ⇒ " + (ok ? "✔" : "✘（改名框回滚？名字未跟上）"));
            return ok;
        }

        /// <summary>
        /// exb 同步改名：旧 exb 存在且未占用才动。绝不删除任何文件。
        /// 返回 exb 侧是否"安全"（旧 exb 不存在或已成功改名 ⇒ 旧 ics 可以删；
        /// 返回 false = 旧 exb 还在且没改名，删旧 ics 会让它断链）。
        /// </summary>
        private static bool RenameExb(string dir, string oldBase, string newName, Action<string> log)
        {
            string oldExb = Path.Combine(dir, oldBase + ".exb");
            string newExb = Path.Combine(dir, newName + ".exb");
            log("sync: ③ exb 同步改名：" + oldExb + " → " + newExb);
            if (!File.Exists(oldExb))
            {
                log("sync: ③ 旧 exb 不存在，跳过（该零件没有同名图纸）");
                return true;
            }
            if (File.Exists(newExb))
            {
                log("sync: ③ 目标 exb 已存在，跳过：" + newExb + "（旧 exb 保留）");
                return false;
            }
            try
            {
                File.Move(oldExb, newExb);
                log("sync: ③ exb 改名 ✔ " + File.Exists(newExb));
                return true;
            }
            catch (Exception ex)
            {
                log("sync: ③ exb 改名失败（多半图纸正开着）： " + ex.Message);
                log("sync: ③ 请关闭该图纸后手动改名，或重跑本功能（ics 已完成，exb 步骤可独立补）");
                return false;
            }
        }

        /// <summary>
        /// ④ 删除旧 ics（2026-11-05 用户需求：改名完成后旧 ics 成垃圾文件，资源管理器删会弹"文件正在使用"）。
        /// 方案（规则坑 57：外链 .ics 只有 Close2(true) 槽 23 真正关得掉，CloseFile 假成功）：
        ///   ① GetAllOpenDocs 枚举打开文档，按 doc.Name（全路径）找到占用旧 ics 的文档；
        ///   ② Close2(true) 关闭（哨兵布防，防"是否保存"框——点「否」）；
        ///   ③ 回查枚举确认已关（判成功只认副作用）；
        ///   ④ File.Delete + 回查 !File.Exists。
        /// 关闭失败 ⇒ 自动续跑 OldIcsDisposeProbe（路线一其余释放手法 + 路线二内存内直接替换，2026-11-05 定案探索）。
        /// 两道安全闸：exbSafe=false 不删（旧 exb 还指着它）；占用者=当前活动场景文档不关（会关掉用户的场景）。
        /// </summary>
        private static void DeleteOldIcs(IZBaseApp app, string oldIcs, string newIcs, bool exbSafe, Action<string> log)
        {
            log("sync: ④ 删除旧 ics：" + oldIcs);
            if (!exbSafe)
            {
                log("sync: ④ 旧 exb 未完成改名，保留旧 ics（防止图纸断链）—— 图纸侧更新链接后手动删");
                return;
            }
            if (!File.Exists(oldIcs))
            {
                log("sync: ④ 旧 ics 不在盘上（可能已删过），跳过");
                return;
            }

            IZDoc holder = FindOpenDoc(app, oldIcs, log);
            if (holder != null)
            {
                string holderName = TryGet(() => holder.Name, "");
                string scenePath = TryGet(() =>
                {
                    IZDoc d = app.ActiveDoc;
                    return d != null ? d.Name : "";
                }, "");
                if (!string.IsNullOrEmpty(holderName) &&
                    string.Equals(holderName, scenePath, StringComparison.OrdinalIgnoreCase))
                {
                    log("sync: ④ 占用者就是当前活动场景文档，拒绝关闭（会关掉用户的场景），保留旧 ics");
                    return;
                }
                log("sync: ④ 旧 ics 被打开文档占用（[" + holderName + "]），Close2(true) 关闭（哨兵布防）");
                RenamePopupSentry.Arm(log);
                try { holder.Close2(true); }
                catch (Exception ex)
                {
                    log("sync: ④ Close2 异常: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                }
                finally { RenamePopupSentry.Disarm(); }

                if (FindOpenDoc(app, oldIcs, log) != null)
                {
                    log("sync: ④ 回查文档仍开着 ⇒ Close2 未生效，续跑处置探针（路线一其余手法 + 路线二内存替换）");
                    if (OldIcsDisposeProbe.Run(app, oldIcs, newIcs, log))
                    {
                        log("sync: ④ 探针处置成功，旧 ics 已删除 ✔");
                        return;
                    }
                    log("sync: ④ 探针未删除旧 ics（结论见 probe: 日志），保留待手动处理");
                    return;
                }
                log("sync: ④ 占用文档已关闭 ✔");
            }
            else
            {
                log("sync: ④ 打开文档中没有占用旧 ics 的（SaveAs 可能已切换文档），直接删");
            }

            try { File.Delete(oldIcs); }
            catch (Exception ex)
            {
                log("sync: ④ File.Delete 异常: " + ex.Message + " —— 保留旧文件，请手动删除");
                return;
            }
            log("sync: ④ 删除回查：" + (!File.Exists(oldIcs) ? "旧 ics 已删除 ✔" : "旧 ics 仍在盘上 ✘"));
        }

        /// <summary>枚举打开文档找指定路径的 doc（先全路径比，退化按文件名比并记录命中方式）。探针共用。</summary>
        internal static IZDoc FindOpenDocInternal(IZBaseApp app, string fullPath, Action<string> log)
        {
            return FindOpenDoc(app, fullPath, log);
        }

        internal static T TryGetInternal<T>(Func<T> f, T def) { return TryGet(f, def); }

        /// <summary>
        /// 联动链分流用：只读当前选中元素的名字与类型（1=零件 2=装配 20=钣金），不做任何改动。
        /// 口径与 Run 第 1 步完全一致（同一个 GetSelection），免得两处判定打架。
        /// </summary>
        public static bool ProbeSelection(IZBaseApp app, Action<string> log, out int etype, out string name)
        {
            etype = -1; name = null;
            if (app == null) { log("link: IZBaseApp 为 null"); return false; }

            IZDoc doc = null;
            try { doc = app.ActiveDoc; } catch (Exception ex) { log("link: ActiveDoc 异常: " + ex.Message); }
            IZSceneDoc scene = doc as IZSceneDoc;
            if (scene == null) { log("link: 前台不是 3D 设计环境，判不了零部件类型"); return false; }

            List<IZElement> sel = GetSelection(scene, log);
            if (sel.Count == 0) { log("link: 没有选中元素 —— 请在设计树里选中要处理的零部件"); return false; }
            if (sel.Count > 1) log("link: 选中 " + sel.Count + " 个元素，联动链只处理第一个");

            IZElement elem = sel[0];
            name = TryGet(() => elem.Name, "");
            etype = TryGet(() => (int)elem.Type, -1);
            if (string.IsNullOrEmpty(name)) { log("link: 取不到选中零部件的名字"); return false; }
            log("link: 分流判定 name=[" + name + "] type=" + etype + "（1=零件 2=装配 20=钣金）");
            return true;
        }

        /// <summary>
        /// 「更新工程图链接文件」前置校验（口径对齐 caxa3dcodenameextraction 11-02 用户定案）：
        ///   外链件「设计环境A.ics --- 零件名B」→ 在文件夹＝**外链 ics 所在目录**，文件名＝零件名B.ics；
        ///   虚拟件「零件名C.ics --- 零件名C」  → 在文件夹＝**设计环境文档所在目录**，文件名＝零件名C.ics，
        ///     且必须 文档名==零件名（否则无从判断图纸该指到哪个 ics，直接拒绝不猜）。
        /// 目标 ics 与同名 exb 都必须真实存在（改链接要指的就是它们）。
        /// </summary>
        public static bool PrepareChangeLinksTarget(IZBaseApp app, Action<string> log,
            out string icsPath, out string exbPath, out string folder, out string elemName)
        {
            icsPath = exbPath = folder = elemName = null;
            if (app == null) { log("cl: IZBaseApp 为 null"); return false; }

            IZDoc doc = null;
            try { doc = app.ActiveDoc; } catch (Exception ex) { log("cl: ActiveDoc 异常: " + ex.Message); }
            IZSceneDoc scene = doc as IZSceneDoc;
            if (scene == null) { log("cl: 前台不是 3D 设计环境，中止"); return false; }

            List<IZElement> sel = GetSelection(scene, log);
            if (sel.Count == 0) { log("cl: 没有选中元素 —— 请先选中要更新链接的零部件"); return false; }
            IZElement elem = sel[0];

            string name = TryGet(() => elem.Name, "");
            if (string.IsNullOrEmpty(name)) { log("cl: 取不到选中零部件的名字"); return false; }
            elemName = name;

            bool linked;
            string oldPath = LinkedFilePath(elem, out linked);
            log("cl: 选中 [" + name + "] linked=" + linked + " path=[" + oldPath + "]");

            // ★ 目标文件名以**外链 ics 自己的文件名**为准，不用选中元素名（2026-11-05 13:2x 实机）★
            //   钣金件外链、且外链里当前配置不是默认时，设计树把这条外链显示成带「展开」的，
            //   两个名字会不一致：实机 13:26:42 选中名 [SC08020102.03-05 折弯板]，
            //   而盘上只有 展开SC08020102.03-05 折弯板.ics / .exb —— 按选中名拼路径就"目标不存在"，
            //   链在第 3 步白中止。「改变链接文件」要指的就是那个真实存在的 ics ⇒ 跟着文件走。
            string targetBase = name;

            if (linked && !string.IsNullOrEmpty(oldPath))
            {
                folder = SafeDir(oldPath);
                string fileStem = null;
                try { fileStem = Path.GetFileNameWithoutExtension(oldPath); } catch { }
                if (!string.IsNullOrEmpty(fileStem) &&
                    !string.Equals(fileStem, name, StringComparison.Ordinal))
                {
                    log("cl: 选中名 [" + name + "] ≠ 外链文件名 [" + fileStem +
                        "]（钣金展开配置外链的常态）⇒ 目标按文件名 " + fileStem);
                    targetBase = fileStem;
                }
            }
            else
            {
                // 虚拟件口径：外链就是设计环境文档本身
                string docPath = TryGet(() => doc != null ? doc.Name : null, null);
                string docBase = string.IsNullOrEmpty(docPath) ? null : Path.GetFileNameWithoutExtension(docPath);
                if (docBase == null) { log("cl: 取不到设计环境文档名，无法为虚拟零部件判定目标 ics"); return false; }
                if (!string.Equals(docBase, name, StringComparison.OrdinalIgnoreCase))
                {
                    log("cl: 选中虚拟零部件时要求「设计环境文档名 == 零件名」。当前是 " + docBase +
                        ".ics --- " + name + "（虚拟），拒绝执行（不猜）");
                    return false;
                }
                folder = SafeDir(docPath);
            }
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                log("cl: 取不到目标文件夹（外链路径异常：" + oldPath + "）"); return false;
            }

            icsPath = Path.Combine(folder, targetBase + ".ics");
            if (!File.Exists(icsPath))
            {
                log("cl: 目标链接文件不存在：" + icsPath + " —— 「改变链接文件」要指的就是它，" +
                    "请先按同名 ics 口径把它存出来（重命名-V2.0 改完名后新 ics 就在）");
                return false;
            }
            exbPath = Path.Combine(folder, targetBase + ".exb");
            if (!File.Exists(exbPath))
            {
                log("cl: 找不到图纸 " + targetBase + ".exb（找过：" + folder + "）"); return false;
            }
            log("cl: 前置 ✔ ics=" + icsPath + "；exb=" + exbPath);
            return true;
        }

        /// <summary>
        /// 「钣金件设计环境设默认」前置校验：只允许选中外链钣金件（type=20），
        /// 且当前场景文档 ≠ 外链源文件（同 重命名-V2.0 的闸2）。返回外链 ics 全路径与零件名。
        /// </summary>
        public static bool PrepareSheetMetalTarget(IZBaseApp app, Action<string> log,
            out string linkedIcs, out string elemName)
        {
            linkedIcs = null; elemName = null;
            if (app == null) { log("sm: IZBaseApp 为 null"); return false; }

            IZDoc doc = null;
            try { doc = app.ActiveDoc; } catch (Exception ex) { log("sm: ActiveDoc 异常: " + ex.Message); }
            IZSceneDoc scene = doc as IZSceneDoc;
            if (scene == null) { log("sm: 前台不是 3D 设计环境，中止"); return false; }

            List<IZElement> sel = GetSelection(scene, log);
            if (sel.Count == 0) { log("sm: 没有选中元素 —— 请先选中外链钣金件"); return false; }
            IZElement elem = sel[0];

            string name = TryGet(() => elem.Name, "");
            int etype = TryGet(() => (int)elem.Type, -1);
            if (string.IsNullOrEmpty(name)) { log("sm: 取不到选中零部件的名字"); return false; }
            if (etype != 20)
            {
                log("sm: 选中元素 type=" + etype + " 不是钣金件（20）—— 本功能只对钣金件生效，拒绝执行");
                return false;
            }
            elemName = name;

            bool linked;
            string oldPath = LinkedFilePath(elem, out linked);
            log("sm: 选中 [" + name + "] type=20(钣金) linked=" + linked + " path=[" + oldPath + "]");
            if (!linked || string.IsNullOrEmpty(oldPath))
            {
                log("sm: 闸1拒绝：不是外链件。本功能跑在外链源文档上下文里，" +
                    "需要 零件B.ics---零件A(外链) 口径");
                return false;
            }

            // 闸2：零件A 自己被打开为场景时不许操作（同 重命名-V2.0）
            string scenePath = TryGet(() => doc != null ? doc.Name : null, null);
            if (string.IsNullOrEmpty(scenePath))
            {
                log("sm: 闸2取不到场景文档名 —— 识别不到就拒绝，不放行");
                return false;
            }
            bool samePath = string.Equals(NormalizePath(scenePath), NormalizePath(oldPath),
                StringComparison.OrdinalIgnoreCase);
            bool sameName = string.Equals(Path.GetFileName(scenePath), Path.GetFileName(oldPath),
                StringComparison.OrdinalIgnoreCase);
            if (samePath || sameName)
            {
                log("sm: 闸2拒绝：当前场景文档 [" + scenePath + "] 就是外链源文件 —— " +
                    "请打开零件B.ics 后选中零件A 再执行");
                return false;
            }

            if (!File.Exists(oldPath))
            {
                log("sm: 外链源 ics 不在盘上：" + oldPath);
                return false;
            }

            linkedIcs = oldPath;
            log("sm: 前置 ✔ 外链源=" + linkedIcs);
            return true;
        }

        /// <summary>枚举打开文档找指定路径的 doc（先全路径比，退化按文件名比并记录命中方式）。</summary>
        private static IZDoc FindOpenDoc(IZBaseApp app, string fullPath, Action<string> log)
        {
            try
            {
                object raw = app.GetAllOpenDocs();
                Array arr = raw as Array;
                if (arr == null) return null;

                string want = NormalizePath(fullPath);
                foreach (object o in arr)
                {
                    IZDoc d = o as IZDoc;
                    if (d == null) continue;
                    string n = TryGet(() => d.Name, "");
                    if (!string.IsNullOrEmpty(n) &&
                        string.Equals(NormalizePath(n), want, StringComparison.OrdinalIgnoreCase)) return d;
                }
                string fname = Path.GetFileName(fullPath);
                foreach (object o in arr)
                {
                    IZDoc d = o as IZDoc;
                    if (d == null) continue;
                    string n = TryGet(() => d.Name, "");
                    if (!string.IsNullOrEmpty(n) &&
                        string.Equals(Path.GetFileName(n), fname, StringComparison.OrdinalIgnoreCase))
                    {
                        log("sync: 文档按文件名命中（全路径未命中）：[" + n + "]");
                        return d;
                    }
                }
            }
            catch (Exception ex) { log("sync: 枚举打开文档失败: " + ex.Message); }
            return null;
        }

        private static string NormalizePath(string p)
        {
            try { return Path.GetFullPath(p ?? "").TrimEnd(Path.DirectorySeparatorChar); }
            catch { return (p ?? "").TrimEnd(Path.DirectorySeparatorChar); }
        }

        // ================================================================ 小工具

        private static string ValidateNewName(string newName, string oldBase)
        {
            string n = newName.Trim();
            if (n.Length == 0) return "新名为空";
            // 用户顺手带了扩展名就剥掉（零件名里允许有点，如 SC08020102.01）
            if (n.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith(".exb", StringComparison.OrdinalIgnoreCase))
                n = Path.GetFileNameWithoutExtension(n);
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "新名含文件名非法字符：" + n;
            if (string.Equals(n, oldBase, StringComparison.OrdinalIgnoreCase))
                return "新名与旧名相同：" + n;
            return null;
        }

        private static string SafeDir(string path)
        {
            try { return Path.GetDirectoryName(path) ?? ""; }
            catch { return ""; }
        }

        private static List<IZElement> GetSelection(IZSceneDoc scene, Action<string> log)
        {
            var list = new List<IZElement>();
            try
            {
                IZSelectionMgr selMgr = scene.SelectionMgr;
                if (selMgr == null) { log("sync: 取不到 SelectionMgr"); return list; }

                ZArray arr = selMgr.GetSelectedElementsZArray();
                if (arr != null)
                {
                    int count;
                    arr.Count(out count);
                    for (int i = 0; i < count; i++)
                    {
                        object raw;
                        arr.Get(i, out raw);
                        IZElement e = raw as IZElement;
                        if (e != null && !list.Contains(e)) list.Add(e);
                    }
                }
                if (list.Count == 0)
                {
                    try
                    {
                        IZElement e = selMgr.GetTrueSelectedElements() as IZElement;
                        if (e != null) list.Add(e);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { log("sync: 读选区失败: " + ex.Message); }
            return list;
        }

        /// <summary>装配体走 IZAssembly、其余走 IZPart（同一语义不同 QI 表面，槽 21 vs 17）。</summary>
        internal static string LinkedFilePath(IZElement elem, out bool linked)
        {
            linked = false;
            try
            {
                bool ok;
                string path;
                IZAssembly asm = elem as IZAssembly;
                if (asm != null) path = asm.GetExternallyLinkedInfo(out ok);
                else
                {
                    IZPart part = elem as IZPart;
                    if (part == null) return "";
                    path = part.GetExternallyLinkedInfo(out ok);
                }
                linked = ok;
                return path ?? "";
            }
            catch { return ""; }
        }

        private static T TryGet<T>(Func<T> f, T def)
        {
            try { return f(); }
            catch { return def; }
        }
    }

    /// <summary>
    /// 改名框哨兵（坑 149 同族）：布防期间后台线程扫本进程可见 #32770 模态框，
    /// 命中就代点——默认点「取消/否」（改名词义下任何宿主弹框都不该答「是」）；
    /// clickYes=true 时点「是/确定/Yes/OK」（覆盖另存词义：doc.SaveAs(force=true) 弹"是否覆盖"要答是）。
    /// 每次命中都把框标题与所点按钮落日志。UI 线程被模态框占住 ⇒ 只能在后台线程代点（坑 152）。
    /// </summary>
    internal static class RenamePopupSentry
    {
        private static volatile bool _armed;
        private static volatile bool _clickYes;
        private static Thread _worker;

        public static void Arm(Action<string> log, bool clickYes = false)
        {
            if (_armed) return;
            _armed = true;
            _clickYes = clickYes;
            _worker = new Thread(delegate()
            {
                var seen = new HashSet<IntPtr>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (_armed && sw.ElapsedMilliseconds < 30000)
                {
                    IntPtr dlg = FindHostDialog();
                    if (dlg != IntPtr.Zero && seen.Add(dlg))
                    {
                        string title = GetWndText(dlg);
                        (log ?? delegate { })("sentry: 发现宿主对话框 [" + title + "] hwnd=0x" +
                            dlg.ToInt64().ToString("X") + "，尝试点「" + (_clickYes ? "是/确定" : "取消/否") + "」");
                        bool clicked = ClickButtonLike(dlg, _clickYes);
                        if (!clicked)
                        {
                            PostMessage(dlg, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                            (log ?? delegate { })("sentry: 没找到可点按钮，已投 WM_CLOSE");
                        }
                    }
                    Thread.Sleep(120);
                }
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        public static void Disarm()
        {
            _armed = false;
            _worker = null;   // 线程见 _armed=false 后 120ms 内自行退出；最多留 30s 空转
        }

        // ---------------- Win32 ----------------

        private delegate bool EnumWndProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWndProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwnd, EnumWndProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageText(IntPtr hwnd, int msg, IntPtr wParam, StringBuilder lParam);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_GETTEXT = 0x000D;
        private const int WM_GETTEXTLENGTH = 0x000E;
        private const int BM_CLICK = 0x00F5;

        private static IntPtr FindHostDialog()
        {
            uint selfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr hit = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString() != "#32770" || !IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == selfPid) { hit = h; return false; }
                return true;
            }, IntPtr.Zero);
            return hit;
        }

        private static string GetWndText(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            return sb.ToString();
        }

        /// <summary>
        /// 在框内找目标按钮代点（PostMessage BM_CLICK，本进程内安全）。
        /// yesMode=false 找「取消/否/Cancel/No」；yesMode=true 找「是/确定/Yes/OK」。
        /// </summary>
        private static bool ClickButtonLike(IntPtr dlg, bool yesMode)
        {
            bool clicked = false;
            EnumChildWindows(dlg, delegate(IntPtr h, IntPtr l)
            {
                var sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString().IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0) return true;
                int len = (int)SendMessage(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                if (len <= 0 || len > 200) return true;
                var txt = new StringBuilder(len + 2);
                SendMessageText(h, WM_GETTEXT, (IntPtr)(len + 1), txt);
                string t = txt.ToString();
                bool hit;
                if (yesMode)
                    hit = t.IndexOf("是", StringComparison.Ordinal) >= 0 ||
                          t.IndexOf("确定", StringComparison.Ordinal) >= 0 ||
                          t.IndexOf("Yes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          t.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0;
                else
                    hit = t.IndexOf("取消", StringComparison.Ordinal) >= 0 ||
                          t.IndexOf("否", StringComparison.Ordinal) >= 0 ||
                          t.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          t.IndexOf("No", StringComparison.Ordinal) == 0;
                if (hit)
                {
                    PostMessage(h, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    clicked = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return clicked;
        }
    }
}
