// SC物料描述=零件名（用户 2026-11-18 需求；按钮位于主窗「装配体排序」右侧）。
//
// 流程：活动场景顶层零部件（零件(1)/装配(2)/钣金(20)/多面体(25)/线架(28)/型材(29)，展开件除外）
//   ① SharedPropWriter.TryReadSharedProp 读共享表「SC物料描述」
//      （铁律 7 / 规则第 14 节 7：判据只有 IsSharedMgr()=true，三候选全试，全否即读不到，绝不兜底换表）；
//   ② 值非空且 ≠ 当前零件名 ⇒ IZElement.set_Name（槽 10，caxa3dBatchMaster 先例）
//      → get_Name（槽 9）Ordinal 回读（坑 147 口径：set 后立即回读，HRESULT 不算成功）；
//   ③ 全部处理完保存活动文档（与图号分离 SeparatePart 同口径）。
//
// 边界说明：
//   · 只处理场景顶层（不穿透装配），与「打开/关闭设计环境」的 ProcessComponents 同口径。
//   · 「SC物料描述」读不到 / 空串 ⇒ 跳过该件并单独记账（第三态，不折算成失败也不算成功）。
//   · 改名后 零件名 ≠ 文件名 ⇒ 后续「文件名=零件名」校验（规格/材料回填）将不再命中，属业务预期。
//   · 改名涉及设计树节点名，若宿主对带约束/外链件弹改名确认框，需实机验证（坑 149 同族警示）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal static class ScDescNameTool
    {
        private const string PropName = "SC物料描述";

        private const int SlotElemGetName = 9;   // IZElement::get_Name（members.csv：IZElement 槽9）
        private const int SlotElemSetName = 10;  // IZElement::set_Name（members.csv：IZElement 槽10）

        private static readonly Guid IID_IZElement = new Guid("69EF5583-A26D-4913-A2F6-DBFF73E1B8A3");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnElemGetName(IntPtr self, [MarshalAs(UnmanagedType.BStr)] out string pbstrName);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnElemSetName(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string pbstrName);

        /// <summary>执行「SC物料描述=零件名」，返回中文判词；全程落 trace。UI 线程同步跑（本工程探针口径）。</summary>
        public static string Apply(IZBaseApp app, Action<string> log)
        {
            const string tag = "sc-desc";
            if (log == null) log = m => Diagnostics.Log(m);
            log(tag + ": ==== SC物料描述=零件名 ====");

            string err;
            IZSceneDoc scene = ProbeCommon.GetScene(app, out err);
            if (scene == null) return "SC物料描述=零件名 中止：" + err;

            object kidsRaw;
            try { kidsRaw = scene.GetChildElements(); }   // 槽7
            catch (Exception ex) { return "SC物料描述=零件名 中止：枚举零部件失败 " + ProbeCommon.Hr(ex); }
            var kids = kidsRaw as object[];
            if (kids == null || kids.Length == 0) return "SC物料描述=零件名：设计环境内未找到任何顶层元素。";

            int nComp = 0, nRenamed = 0, nSame = 0, nNoProp = 0, nFail = 0;
            var fails = new List<string>();
            var preview = new List<string>();

            foreach (object o in kids)
            {
                var el = o as IZElement;
                if (el == null) continue;
                int type = -1;
                try { type = (int)el.Type; } catch { }
                if (!IsComponentType(type)) continue;

                string name = ProbeCommon.Safe(() => el.Name, "<无名>");
                if (name.IndexOf("展开", StringComparison.Ordinal) >= 0)
                {
                    log(tag + ": 跳过展开件「" + name + "」（钣金展开态配置产物）");
                    continue;
                }
                nComp++;
                log(tag + ": 处理「" + name + "」（类型 " + type + "）");

                // ① 读共享表 SC物料描述
                string desc, rErr;
                if (!SharedPropWriter.TryReadSharedProp(el, PropName, tag, log, out desc, out rErr))
                {
                    nNoProp++;
                    log(tag + ": 「" + name + "」读不到 SC物料描述（" + rErr + "），跳过");
                    continue;
                }
                if (string.IsNullOrEmpty(desc))
                {
                    nNoProp++;
                    log(tag + ": 「" + name + "」SC物料描述为空串，跳过");
                    continue;
                }

                // ② 已一致则不重复写
                if (string.Equals(desc, name, StringComparison.Ordinal))
                {
                    nSame++;
                    log(tag + ": 「" + name + "」零件名与 SC物料描述 已一致，跳过改名");
                    continue;
                }

                // ③ set_Name + Ordinal 回读
                string rnErr;
                if (RenameElement(el, desc, tag, log, out rnErr))
                {
                    nRenamed++;
                    if (preview.Count < 12) preview.Add("「" + name + "」→「" + desc + "」");
                }
                else
                {
                    nFail++;
                    fails.Add("「" + name + "」" + rnErr);
                }
            }

            // ③ 保存文档（确保改名持久化；与 SeparatePart 保存口径一致）
            try
            {
                IZDoc doc = app.ActiveDoc;
                if (doc != null) { doc.Save(); log(tag + ": 文档已保存"); }
                else log(tag + ": 活动文档为空，跳过保存");
            }
            catch (Exception ex) { log(tag + ": 保存文档 EX " + ProbeCommon.Hr(ex)); }

            var sb = new System.Text.StringBuilder();
            sb.Append("SC物料描述=零件名 完成：顶层零部件 ").Append(nComp)
              .Append(" 个，改名成功 ").Append(nRenamed).Append(" 个");
            if (nSame > 0) sb.Append("，已一致跳过 ").Append(nSame).Append(" 个");
            if (nNoProp > 0) sb.Append("，读不到 SC物料描述（共享表无此属性/空值）跳过 ").Append(nNoProp).Append(" 个");
            if (nFail > 0) sb.Append("，改名失败 ").Append(nFail).Append(" 个：");
            else sb.Append("。");
            foreach (string f in fails) sb.AppendLine().Append("  ★ " + f);
            if (preview.Count > 0) sb.AppendLine().Append("改名明细：").Append(string.Join("、", preview.ToArray()));
            sb.Append("判据＝set_Name 后 get_Name Ordinal 回读一致；请在设计树中确认新名。");
            return sb.ToString();
        }

        /// <summary>IZElement.set_Name（槽 10 直调）→ get_Name（槽 9）Ordinal 回读；回读不一致按第三态失败处理。</summary>
        private static bool RenameElement(IZElement el, string newName, string tag, Action<string> log, out string err)
        {
            err = null;
            IntPtr pUnk = IntPtr.Zero, pElem = IntPtr.Zero;
            try { pUnk = Marshal.GetIUnknownForObject(el); }
            catch (Exception ex) { err = "GetIUnknownForObject EX " + ProbeCommon.Hr(ex); return false; }
            try
            {
                Guid iid = IID_IZElement;
                int qi = Marshal.QueryInterface(pUnk, ref iid, out pElem);
                if (qi != 0 || pElem == IntPtr.Zero) { err = "QI IZElement 失败 " + ProbeCommon.Hr(qi); return false; }

                int hrSet = ProbeCommon.Fn<FnElemSetName>(pElem, SlotElemSetName)(pElem, newName);
                if (hrSet != 0)
                {
                    err = "set_Name 失败 " + ProbeCommon.Hr(hrSet) + "（HRESULT 不算成功）";
                    log(tag + ": " + err);
                    return false;
                }
                string cur;
                int hrGet = ProbeCommon.Fn<FnElemGetName>(pElem, SlotElemGetName)(pElem, out cur);
                bool ok = hrGet == 0 && string.Equals(cur, newName, StringComparison.Ordinal);
                log(tag + ": set_Name(\"" + newName + "\") hr=" + ProbeCommon.Hr(hrSet) +
                    "，回读=\"" + (cur ?? "<空>") + "\" hr=" + ProbeCommon.Hr(hrGet) +
                    "（" + (ok ? "一致" : "不一致") + "）");
                if (!ok) err = "改名后回读=\"" + (cur ?? "<空>") + "\"与新名不一致（第三态）";
                return ok;
            }
            catch (Exception ex)
            {
                err = "改名调用异常 " + ProbeCommon.Hr(ex) + "：" + ex.Message;
                log(tag + ": " + err);
                return false;
            }
            finally
            {
                if (pElem != IntPtr.Zero) Marshal.Release(pElem);
                Marshal.Release(pUnk);
            }
        }

        /// <summary>零部件类型（与 AssemblySortTool.IsComponent / DesignEnvTool.IsComponentType 同口径）。</summary>
        private static bool IsComponentType(int t)
        {
            return t == 1 || t == 2 || t == 20 || t == 25 || t == 28 || t == 29;
        }
    }
}
