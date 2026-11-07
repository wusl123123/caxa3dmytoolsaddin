// 文件改名 V2.0 联动链（11-06 用户指令 5 + 通道选定原文「全盘复刻 V2.0 联动链」）。
// 权威来源：C:\code\ICAPI\caxa3dmytoolsaddin\plugins\caxa3dcodenameextraction\LinkRenameSync.cs（871 行，11-05 定案）
//   与其项目总结第 3.4 节「重命名时序已反转（V2.0）」。本文件是把那条链搬进本插件的表格驱动形态，
//   步骤、闸、日志锚点（═══ 结论（外链件…）与参考逐段对齐，只改「新名从哪来」和「属性写哪张表」两处。
//
// 与参考的三处有意差异（其余照抄）：
//   ① 新名不弹输入框：逐行取表格 F「新零件名」（参考 11-05 也去掉了输入框，改成读界面文本框，语义一致）；
//   ② 改名前的 SC* 回填走本项目的图号分离规则文件＋共享表铁律（CaxaPropWrite 只认 IsSharedMgr=true 的表），
//      不照抄参考的 WriteFirstElementCustomProps（那条有「拿不到共享表就退回别的表」的老口径，v003.049 已作废）；
//   ③ 不接参考的 OldIcsDisposeProbe（那是探索路线的探针，不是交付链的一环）：旧 ics 删不掉就留在盘上、
//      在日志里点名文件，绝不为了「删干净」去试没实测过的释放手法。
//
// 成员签名全部按本工程 Lib\interop（v26，919040 B）反射实测后书写，不照抄 v29/2027：
//   IZSceneDoc.SaveAs(String, eZLinksSaveOptions, Boolean) / IZAssembly.SaveAs(String, Boolean) /
//   IZPart.SaveAs(String, Boolean) / IZDoc.Name·Close2(Boolean) / IZBaseApp.GetAllOpenDocs()->Object /
//   IZAssembly·IZPart.GetExternallyLinkedInfo(out Boolean)->String / IZElement.Name(get_9/set_10)·Type(get_7) /
//   eZLinksSaveOptions.Z_LINKS_IGNORE=5（实测枚举值，SAVE_ALL 会穿透子零件弹一串「另存为」框）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;   // [ComVisible(false)] 用（本类不导出 COM 类型）
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    internal static class LinkRenameV2
    {
        /// <summary>一次改名的全部判据。只有 Concluded=true 才代表链跑完（那行「═══ 结论（外链件…」印出来才算）。</summary>
        internal sealed class Result
        {
            public bool Concluded;          // 权威判据：结论行是否打出
            public bool Mutated;            // 过了双闸、开始动手（此后才可能有半成品）
            public bool PropsPrepared;      // 改名前的 SC* 回填这一步执行过（命中规则才执行）
            public bool DocSwitched;        // ①A-2 占用文档切到新 ics
            public bool LinkFollowed;       // ①A-3 场景外链自动跟上／补链跟上
            public bool LinkOk;             // 最终外链指向==新 ics
            public bool NameOk;             // set_Name 后回读==新名（Ordinal）
            public bool ExbRenamed;         // ③ 同名 exb 改名成功（或本来就没有 exb＝视为安全）
            public string OldIcsState = ""; // ④ 旧 ics 处置结论原文（已删除/保留原因/不在盘上）
            public string OldIcs = "";
            public string NewIcs = "";
            public string SkipReason = "";  // 没动手的原因（闸拒绝/校验失败）
            public int ElementType;         // IZElement.Type：1=零件 2=装配 20=钣金，其余（含读不到的 -1）＝不在支持范围
            public string DrawingStepState = "";  // 图纸侧后续步骤的执行结论（分流表里 ②③④ 那几步）
            public string Conclusion = "";  // 结论行原文，调用方按它记账
        }

        /// <summary>元素类型（IZElement.get_Type，槽 8 位；1=零件 2=装配 20=钣金，其余一律按「不在支持范围」处理）。
        /// 读不到＝-1（第三态），也归「不在支持范围」，绝不猜成零件。判据来源：参考工程
        /// caxa3dcodenameextraction LinkRenameSync.cs:444-446 的同一字段与同一注释口径。</summary>
        public static int ElementTypeOf(IZElement elem)
        {
            return TryGet(delegate { return (int)elem.Type; }, -1);
        }

        /// <summary>类型码 → 中文类别；不支持的返回 null＝调用方按「不在支持范围」中止该行（不猜）。</summary>
        public static string KindTextOf(int etype)
        {
            switch (etype)
            {
                case 1: return "零件";
                case 2: return "装配";
                case 20: return "钣金";
                default: return null;
            }
        }

        /// <summary>该类型的图纸侧后续步骤（用户 11-06 指令 2c 的分流表原文照搬）。</summary>
        public static string[] ChainOf(int etype)
        {
            switch (etype)
            {
                case 1: return new[] { "重命名-V2.0", "更新工程图链接文件" };
                case 2: return new[] { "重命名-V2.0", "更新工程图链接文件", "更新3dbom（自己开一次图纸环境）" };
                case 20: return new[] { "重命名-V2.0", "钣金件设默认", "更新工程图链接文件", "标题栏设默认（自己开一次图纸环境）" };
                default: return null;    // 0 / 3 / -1 等＝NULL，调用方中止
            }
        }

        /// <summary>
        /// 虚拟件（无外链 .ics）的**最小链**（11-06 用户指令 2d「虚拟件只修改零件名，和进行图号分离」）：
        /// ⓪ 图号分离规则回填 SC*（与外链件同一个回调、同一张共享表）→ ① set_Name ＋ 同元素回读。
        /// 与 V2.0 的差别＝**一个文件都不碰**：不 SaveAs、不改 exb、不删 ics、不进图纸侧的任何后续步骤
        /// （那些步骤的前提是「有外链源文件」，虚拟件没有，硬走就是在猜）。
        /// 判成功只认回读：NameOk &amp;&amp; Concluded；改名框由 RenamePopupSentry 统一答「否/取消」。
        /// </summary>
        public static Result RunVirtual(IZElement elem, string newName,
            Action<IZElement, string> preWriteProps, Action<string> log)
        {
            var res = new Result();
            if (log == null) log = delegate { };
            if (elem == null) { res.SkipReason = "目标元素为空"; log("v2虚拟: " + res.SkipReason + "，中止"); return res; }

            string oldName = TryGet(delegate { return elem.Name; }, "(取不到名字)");
            int etype = ElementTypeOf(elem);
            res.ElementType = etype;
            log("v2虚拟: 目标 name=[" + oldName + "] type=" + etype +
                "（1=零件 2=装配 20=钣金）｜无外链 .ics＝虚拟件 ⇒ 只改零件名＋图号分离，不碰任何文件、不进图纸侧步骤");

            string nn = CleanNewName(newName);
            string err = ValidateNewName(nn, (oldName ?? "").Trim());
            if (err != null) { res.SkipReason = err; log("v2虚拟: " + err + "，中止"); return res; }

            // ⓪ 图号分离回填（与外链件同一个回调；用户指令 2d 明确虚拟件也要做图号分离）
            if (preWriteProps != null)
            {
                res.PropsPrepared = true;
                log("v2虚拟: ⓪ 先按图号分离规则把 SC* 写进共享表");
                try { preWriteProps(elem, nn); }
                catch (Exception ex) { log("v2虚拟: ⓪ 属性回填异常（不中止改名）：" + ex.Message); }
            }

            res.Mutated = true;
            bool nameOk = RenameElement(elem, nn, log, "虚拟-①");
            res.NameOk = nameOk;
            string after = TryGet(delegate { return elem.Name; }, "(取不到)");
            res.Conclusion = "v2虚拟: ═══ 结论（虚拟件·只改名）═══ 零件名：" +
                (nameOk ? "✔ [" + after + "]" : "✘ 仍为 [" + after + "]") +
                "；文件：未触碰（虚拟件无外链 .ics，SaveAs/exb/删旧 ics 全部不适用）";
            log(res.Conclusion);
            Diagnostics.Trace(string.Format("LinkRenameV2.RunVirtual verdict: old={0} new={1} nameOk={2} propsPrepared={3}",
                oldName, nn, nameOk, res.PropsPrepared));
            res.Concluded = true;
            return res;
        }

        /// <summary>
        /// 主入口（对位参考 LinkRenameSync.Run）。顺序固定：
        /// 场景校验 → 闸1（外链件）→ 闸2（场景文档≠外链源）→ 新名校验 → 目标存在性 → 属性回填 → ①A 反转时序 → ①B 回落 → ③ exb → ④ 旧 ics → 结论。
        /// 双闸没过＝一个字节都不动（含不写属性），这是参考特意把 prompt/回填放在闸后的原因。
        /// </summary>
        /// <param name="preWriteProps">改名前的属性回填回调（(元素, 新名)）。闸全过、任何改动之前调用。</param>
        public static Result Run(IZBaseApp app, IZElement elem, string newName,
            Action<IZElement, string> preWriteProps, Action<string> log)
        {
            var res = new Result();
            if (log == null) log = delegate { };
            if (app == null || elem == null)
            {
                res.SkipReason = "活动文档或目标元素为空";
                log("v2: " + res.SkipReason + "，中止");
                return res;
            }

            // ── 0. 前台必须是 3D 设计环境 ──
            IZDoc doc = null;
            try { doc = app.ActiveDoc; }
            catch (Exception ex) { log("v2: ActiveDoc 异常: " + ex.Message); }
            IZSceneDoc scene = doc as IZSceneDoc;
            if (scene == null)
            {
                res.SkipReason = "前台不是 3D 设计环境（活动文档不是场景文档）";
                log("v2: " + res.SkipReason + "，中止");
                return res;
            }

            string name = TryGet(delegate { return elem.Name; }, "(取不到名字)");
            int etype = TryGet(delegate { return (int)elem.Type; }, -1);
            res.ElementType = etype;
            string oldPath = CaxaBatchService.GetLinkedFile(elem);
            log("v2: 目标元素 name=[" + name + "] type=" + etype +
                "，外链源=[" + (string.IsNullOrEmpty(oldPath) ? "(无＝虚拟件/内嵌件)" : oldPath) + "]");

            // ── 准入闸 1：只允许外链件（虚拟件/内建件一律拒绝）──
            if (string.IsNullOrEmpty(oldPath))
            {
                res.SkipReason = "闸1拒绝：该元素没有外链 .ics（虚拟件/内嵌件）＝请先单独打开它的 ics 处理";
                log("v2: " + res.SkipReason);
                return res;
            }
            if (!File.Exists(oldPath))
            {
                res.SkipReason = "闸1拒绝：外链源不在盘上：" + oldPath + "（表格与磁盘已不一致，不做破坏性操作）";
                log("v2: " + res.SkipReason);
                return res;
            }

            // ── 准入闸 2：当前场景文档不能就是外链源文件本身（不能在零件A自己被打开时改它）──
            string scenePath = TryGet(delegate { return doc != null ? doc.Name : null; }, null);
            if (string.IsNullOrEmpty(scenePath))
            {
                res.SkipReason = "取不到场景文档名，闸2 无法校验＝按不通过处理（破坏性操作识别不到就拒绝）";
                log("v2: 警告：" + res.SkipReason);
                return res;
            }
            bool samePath = string.Equals(NormalizePath(scenePath), NormalizePath(oldPath), StringComparison.OrdinalIgnoreCase);
            bool sameName = string.Equals(Path.GetFileName(scenePath), Path.GetFileName(oldPath), StringComparison.OrdinalIgnoreCase);
            if (samePath || sameName)
            {
                res.SkipReason = "闸2拒绝：当前场景文档 [" + scenePath + "] 就是外链源 [" + oldPath +
                    "]＝口径是「零件B.ics---零件A(外链)」，请在装配体里选中零件A 再执行";
                log("v2: " + res.SkipReason);
                return res;
            }
            log("v2: 准入 ✔ 场景文档 [" + scenePath + "] ≠ 外链源 [" + Path.GetFileName(oldPath) + "]，双闸通过");

            // ── 新名校验（参考口径：目标 ics 不该存在；带扩展名先剥掉）──
            string dir = SafeDir(oldPath);
            string oldBase = Path.GetFileNameWithoutExtension(oldPath);
            string ext = Path.GetExtension(oldPath);
            string nn = CleanNewName(newName);
            string err = ValidateNewName(nn, oldBase);
            if (err != null)
            {
                res.SkipReason = err;
                log("v2: " + err + "，中止");
                return res;
            }
            string newIcs = Path.Combine(dir, nn + ext);
            res.OldIcs = oldPath;
            res.NewIcs = newIcs;
            if (File.Exists(newIcs))
            {
                res.SkipReason = "目标 ics 已存在，拒绝覆盖：" + newIcs + "（需求口径：目标不该存在）";
                log("v2: " + res.SkipReason);
                return res;
            }

            // ── 属性回填：闸后、任何改动之前（参考把这段放在 prompt 回调里，同一个位置）──
            if (preWriteProps != null)
            {
                res.PropsPrepared = true;
                log("v2: ⓪ 改名前先按图号分离规则把 SC* 写进共享表（此后 set_Name/SaveAs 可能清空选区，属性必须趁还能定位时写）");
                try { preWriteProps(elem, nn); }
                catch (Exception ex) { log("v2: ⓪ 属性回填异常（不中止改名，主动作是改名）：" + ex.Message); }
            }

            res.Mutated = true;

            // ══ ①A 反转时序：先改零件名，再让占用旧 ics 的文档自己切到新 ics ══
            // 实证前提（参考 11-05 09:35/09:52）：占用文档与场景外链元素是同一份内存内容（根元素名随 set_Name 同步），
            // 它关不掉但可以被 doc.SaveAs 切换文件指向——前提是目标 ics 尚不存在（目标被占用＝E_FAIL）。
            IZDoc holder = FindOpenDoc(app, oldPath, log);
            IZSceneDoc holderScene = holder as IZSceneDoc;
            string holderPath = holder != null ? TryGet(delegate { return holder.Name; }, "") : "";
            string activePath = TryGet(delegate
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
                log("v2: ①A 反转时序：占用旧 ics 的文档在手（[" + holderPath + "]），先改零件名再让它自己切到新 ics");

                nameOk = RenameElement(elem, nn, log, "①A-1");
                if (!nameOk)
                {
                    log("v2: ①A 零件名未改成功 ⇒ 中止（不动任何文件，无半成品）");
                    res.NameOk = false;
                    res.SkipReason = "①A-1 set_Name 回读不等于新名（多半是改名框回滚）";
                    return res;
                }

                // ★ 链接选项必须 Z_LINKS_IGNORE：SAVE_ALL 会对装配体「保存全部链接」＝逐个写子零件
                //   ＝弹一串「另存为」框（实机 30+ 框实证）。IGNORE＝只保存本文档，子零件文件原封不动。
                log("v2: ①A-2 doc.SaveAs(" + newIcs + ", Z_LINKS_IGNORE, force=true) —— 只保存本文档、不碰子零件（哨兵 clickYes 布防）");
                RenamePopupSentry.Arm(log, true);
                try { holderScene.SaveAs(newIcs, eZLinksSaveOptions.Z_LINKS_IGNORE, true); }
                catch (Exception ex)
                {
                    log("v2: ①A-2 SaveAs 异常: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                }
                finally { RenamePopupSentry.Disarm(); }

                string holderAfter = TryGet(delegate { return holder.Name; }, "");
                docSwitched = string.Equals(holderAfter, newIcs, StringComparison.OrdinalIgnoreCase);
                log("v2: ①A-2 doc.Name 回读 = [" + holderAfter + "] ⇒ " +
                    (docSwitched ? "已切换到新 ics ✔（旧 ics 应已释放）" : "未切换 ✘ → 回落 elem.SaveAs 路线"));

                if (docSwitched)
                {
                    string rp = CaxaBatchService.GetLinkedFile(elem);
                    linkFollowed = !string.IsNullOrEmpty(rp) &&
                                   string.Equals(rp, newIcs, StringComparison.OrdinalIgnoreCase);
                    log("v2: ①A-3 回查场景链接 = [" + (string.IsNullOrEmpty(rp) ? "(linked=false)" : rp) + "] ⇒ " +
                        (linkFollowed ? "自动跟上 ✔（内存内直接替换闭环，无需 elem.SaveAs）" : "未自动跟上 ✘"));
                    if (!linkFollowed)
                    {
                        // 补链尝试：目标已存在且被 holderScene 占用 ⇒ 可能 E_FAIL，失败不致命
                        log("v2: ①A-3 尝试 elem.SaveAs(新ics, true) 补链（目标已被文档占用，可能 E_FAIL，失败不致命）");
                        try
                        {
                            SaveAsLinked(elem, newIcs, log, "①A-3");
                            string rp2 = CaxaBatchService.GetLinkedFile(elem);
                            linkFollowed = !string.IsNullOrEmpty(rp2) &&
                                           string.Equals(rp2, newIcs, StringComparison.OrdinalIgnoreCase);
                            log("v2: ①A-3 补链回读 = [" + (string.IsNullOrEmpty(rp2) ? "(linked=false)" : rp2) + "] ⇒ " +
                                (linkFollowed ? "补链成功 ✔" : "补链未生效 ✘"));
                        }
                        catch (Exception ex)
                        {
                            log("v2: ①A-3 补链异常: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                        }
                    }
                }
            }
            else
            {
                log("v2: ①A 反转时序不可用（占用文档" + (holder == null ? "不存在" :
                    holderScene == null ? "不是场景文档" : "是当前活动场景") + "）→ 走回落路线");
            }

            bool linkOk = docSwitched && linkFollowed;

            // ══ ①B 回落：elem.SaveAs 另存新 ics 并回链 ══
            if (!linkOk)
            {
                log("v2: ①B SaveAs(" + newIcs + ", vbLinkToScene=true) 开始（回落路线，会向磁盘写新 ics）");
                try { SaveAsLinked(elem, newIcs, log, "①B"); }
                catch (Exception ex)
                {
                    log("v2: ①B SaveAs 异常: " + ex.GetType().Name + " hr=0x" +
                        ex.HResult.ToString("X8") + " msg=" + ex.Message);
                    log("v2: 中止 —— 未做任何后续改名（链接仍是旧路径，无半成品）");
                    res.SkipReason = "①B elem.SaveAs 失败（" + ex.Message + "）";
                    return res;
                }

                string rb = CaxaBatchService.GetLinkedFile(elem);
                linkOk = !string.IsNullOrEmpty(rb) && string.Equals(rb, newIcs, StringComparison.OrdinalIgnoreCase);
                log("v2: ①B 回读链接 = [" + (string.IsNullOrEmpty(rb) ? "(linked=false)" : rb) + "] ⇒ " +
                    (linkOk ? "已回链到新 ics ✔" : "未变 ✘ —— 中止，无半成品"));
                if (!linkOk) { res.SkipReason = "①B 回读外链没等于新 ics"; return res; }
                log("v2: ①B 新 ics 已落盘：" + File.Exists(newIcs) +
                    (File.Exists(newIcs) ? "（" + new FileInfo(newIcs).Length + " B）" : ""));

                if (!nameOk) nameOk = RenameElement(elem, nn, log, "①B-2");
            }

            res.DocSwitched = docSwitched;
            res.LinkFollowed = linkFollowed;
            res.LinkOk = linkOk;
            res.NameOk = nameOk;

            // ── ③ 同步改名 exb ──
            bool exbSafe = RenameExb(dir, oldBase, nn, log);
            res.ExbRenamed = exbSafe;

            // ── ④ 删除旧 ics ──
            if (docSwitched && !linkFollowed)
            {
                res.OldIcsState = "保留（文档已切到新 ics 但场景链接没跟上，删了外链解析会断＝保存/重开场景后再删）";
                log("v2: ④ " + res.OldIcsState);
            }
            else
            {
                res.OldIcsState = DeleteOldIcs(app, oldPath, exbSafe, log);
            }

            // ── 结论（权威判据行；调用方只认这行的字段，不认 hr）──
            string nameAfter = TryGet(delegate { return elem.Name; }, "(取不到)");
            res.Conclusion = "v2: ═══ 结论（外链件" + (docSwitched ? "·反转时序" : "·回落") + "）═══ 链接：" +
                (linkOk ? "✔ 已指向 " + newIcs : "✘") +
                "；零件名：" + (nameOk ? "✔ [" + nameAfter + "]" : "✘ 仍为 [" + nameAfter + "]") +
                "；exb：" + (exbSafe ? "✔ 已同步改名" : "✘ 未改名（见 ③ 日志）") +
                "；旧 ics：" + res.OldIcsState;
            log(res.Conclusion);
            Diagnostics.Trace(string.Format(
                "LinkRenameV2 verdict: old={0} new={1} route={2} docSwitched={3} linkFollowed={4} linkOk={5} nameOk={6} exbSafe={7} oldIcs={8}",
                oldPath, newIcs, docSwitched ? "反转时序" : "回落",
                docSwitched, linkFollowed, linkOk, nameOk, exbSafe, res.OldIcsState));
            res.Concluded = true;
            return res;
        }

        // ================================================================ 公共步骤

        /// <summary>set_Name（IZElement 槽 10）+ 哨兵 + 回读。返回回读是否 Ordinal 等于新名。tag＝日志步骤号。</summary>
        private static bool RenameElement(IZElement elem, string newName, Action<string> log, string tag)
        {
            log("v2: " + tag + " set_Name(" + newName + ") 开始（哨兵布防：改名框只能答「取消」，答「是」＝CAXA 用改名框回滚名字）");
            RenamePopupSentry.Arm(log, false);
            try
            {
                try { elem.Name = newName; }
                catch (Exception ex)
                {
                    log("v2: " + tag + " set_Name 异常: " + ex.GetType().Name + " hr=0x" +
                        ex.HResult.ToString("X8") + " msg=" + ex.Message);
                }
            }
            finally { RenamePopupSentry.Disarm(); }

            string after = TryGet(delegate { return elem.Name; }, "(取不到)");
            bool ok = string.Equals(after, newName, StringComparison.Ordinal);
            log("v2: " + tag + " 回读零件名 = [" + after + "] ⇒ " + (ok ? "✔" : "✘（名字没跟上）"));
            return ok;
        }

        /// <summary>IZAssembly 槽走 IZAssembly 表面、其余走 IZPart 表面（同一语义不同 QI 表面）。</summary>
        private static void SaveAsLinked(IZElement elem, string newIcs, Action<string> log, string tag)
        {
            IZAssembly asm = elem as IZAssembly;
            if (asm != null) { asm.SaveAs(newIcs, true); return; }
            IZPart part = elem as IZPart;
            if (part == null) throw new InvalidOperationException(tag + " 元素既不是 IZAssembly 也不是 IZPart，不猜别的表面");
            part.SaveAs(newIcs, true);
        }

        /// <summary>exb 同步改名：旧 exb 存在且未占用才动，绝不删除任何文件。
        /// 返回 exb 侧是否「安全」（旧 exb 不存在或已改名 ⇒ 旧 ics 可删；false＝旧 exb 还指着旧 ics）。</summary>
        private static bool RenameExb(string dir, string oldBase, string newName, Action<string> log)
        {
            string oldExb = Path.Combine(dir, oldBase + ".exb");
            string newExb = Path.Combine(dir, newName + ".exb");
            log("v2: ③ exb 同步改名：" + oldExb + " → " + newExb);
            if (!File.Exists(oldExb)) { log("v2: ③ 旧 exb 不存在，跳过（该零件没有同名图纸）"); return true; }
            if (File.Exists(newExb)) { log("v2: ③ 目标 exb 已存在，跳过：" + newExb + "（旧 exb 保留）"); return false; }
            try
            {
                File.Move(oldExb, newExb);
                log("v2: ③ exb 改名 ✔ " + File.Exists(newExb));
                return true;
            }
            catch (Exception ex)
            {
                log("v2: ③ exb 改名失败（多半图纸正开着）：" + ex.Message);
                log("v2: ③ 请关闭该图纸后手动改名，或单独重跑「刷新工程图」那条链");
                return false;
            }
        }

        /// <summary>④ 删除旧 ics：占用者＝当前活动场景时拒绝关闭；否则 Close2(true)（槽 23，实测 CloseFile 是假成功）后删。
        /// 返回处置结论原文（进日志也进 Result.OldIcsState，不给「删了」含糊话）。</summary>
        private static string DeleteOldIcs(IZBaseApp app, string oldIcs, bool exbSafe, Action<string> log)
        {
            log("v2: ④ 删除旧 ics：" + oldIcs);
            if (!exbSafe) return "保留（旧 exb 还没改名，删了图纸会断链）";
            if (!File.Exists(oldIcs)) return "不在盘上（跳过）";

            IZDoc holder = FindOpenDoc(app, oldIcs, log);
            if (holder != null)
            {
                string holderName = TryGet(delegate { return holder.Name; }, "");
                string scenePath = TryGet(delegate
                {
                    IZDoc d = app.ActiveDoc;
                    return d != null ? d.Name : "";
                }, "");
                if (!string.IsNullOrEmpty(holderName) &&
                    string.Equals(holderName, scenePath, StringComparison.OrdinalIgnoreCase))
                    return "保留（占用者就是当前活动场景文档，拒绝关闭用户的场景）";

                log("v2: ④ 旧 ics 被打开文档占用（[" + holderName + "]），Close2(true) 关闭（哨兵布防，「是否保存」答否）");
                RenamePopupSentry.Arm(log, false);
                try { holder.Close2(true); }
                catch (Exception ex) { log("v2: ④ Close2 异常: hr=0x" + ex.HResult.ToString("X8") + " " + ex.Message); }
                finally { RenamePopupSentry.Disarm(); }

                if (FindOpenDoc(app, oldIcs, log) != null)
                    return "保留（回查文档仍开着＝Close2 未生效；参考侧的处置探针属探索路线，本链不接）";
                log("v2: ④ 占用文档已关闭 ✔");
            }
            else
            {
                log("v2: ④ 打开文档里没有占用旧 ics 的（SaveAs 可能已切换文档），直接删");
            }

            try { File.Delete(oldIcs); }
            catch (Exception ex) { return "删除失败（" + ex.Message + "）＝保留待手动删"; }
            string verdict = File.Exists(oldIcs) ? "删除后回查仍在盘上 ✘" : "已删除 ✔";
            log("v2: ④ 删除回查：" + verdict);
            return verdict;
        }

        /// <summary>枚举打开文档找指定路径的 doc：先全路径比，退化按文件名比（命中方式写日志）。
        /// 用 CaxaBatchService.UnpackArray 解包，ZArray / object[] / 单对象三种返回形态都吃得下。</summary>
        private static IZDoc FindOpenDoc(IZBaseApp app, string fullPath, Action<string> log)
        {
            try
            {
                object raw = app.GetAllOpenDocs();
                List<object> docs = CaxaBatchService.UnpackArray(raw);
                if (docs.Count == 0) return null;

                string want = NormalizePath(fullPath);
                foreach (object o in docs)
                {
                    IZDoc d = o as IZDoc;
                    if (d == null) continue;
                    string n = TryGet(delegate { return d.Name; }, "");
                    if (!string.IsNullOrEmpty(n) &&
                        string.Equals(NormalizePath(n), want, StringComparison.OrdinalIgnoreCase)) return d;
                }
                string fname = Path.GetFileName(fullPath);
                foreach (object o in docs)
                {
                    IZDoc d = o as IZDoc;
                    if (d == null) continue;
                    string n = TryGet(delegate { return d.Name; }, "");
                    if (!string.IsNullOrEmpty(n) &&
                        string.Equals(Path.GetFileName(n), fname, StringComparison.OrdinalIgnoreCase))
                    {
                        log("v2: 文档按文件名命中（全路径未命中）：[" + n + "]");
                        return d;
                    }
                }
            }
            catch (Exception ex) { log("v2: 枚举打开文档失败: " + ex.Message); }
            return null;
        }

        // ================================================================ 小工具

        /// <summary>带扩展名输入（用户顺手写了 abc.ics）先剥掉；零件名本身允许有点（SC08020102.01）所以只剥这两个已知扩展名。
        /// 参考的 ValidateNewName 只在内部剥、外面仍用原串拼 newIcs（会拼出 abc.ics.ics），这里改成先洗后校验再拼。</summary>
        private static string CleanNewName(string raw)
        {
            string n = (raw ?? "").Trim();
            if (n.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith(".exb", StringComparison.OrdinalIgnoreCase))
                n = Path.GetFileNameWithoutExtension(n);
            return n;
        }

        private static string ValidateNewName(string newName, string oldBase)
        {
            string n = (newName ?? "").Trim();
            if (n.Length == 0) return "新名为空";
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "新名含文件名非法字符：" + n;
            if (string.Equals(n, oldBase, StringComparison.OrdinalIgnoreCase)) return "新名与旧名相同：" + n;
            return null;
        }

        private static string NormalizePath(string p)
        {
            try { return Path.GetFullPath(p ?? "").TrimEnd(Path.DirectorySeparatorChar); }
            catch { return (p ?? "").TrimEnd(Path.DirectorySeparatorChar); }
        }

        private static string SafeDir(string path)
        {
            try { return Path.GetDirectoryName(path) ?? ""; }
            catch { return ""; }
        }

        private static T TryGet<T>(Func<T> f, T def)
        {
            try { return f(); }
            catch { return def; }
        }
    }
}
