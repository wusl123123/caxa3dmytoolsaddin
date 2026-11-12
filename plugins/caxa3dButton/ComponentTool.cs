// 自制件工具（用户 15:25 需求）：
//   1) 识别选中零部件 —— 零件和钣金（可填写），装配排除，虚拟件允许。
//   2) 打开工程图 —— 11-09 增强：投递 OpenFile2 之前，先按「打开设计环境」口径对选中元素
//      做图号分离（按零件名自动选规则）＋ 规格/材料回填（钣金→钣金件尺寸填写 / 其余→自制件规格材料填写），
//      保存文档后再打开与零件同名、与当前设计环境同文件夹的 .exb（保证图纸引用到最新共享表属性）。
//      差集基线在投递前采集并带出，避免「投递后才取 before」导致的窗口差集必然超时误报。
//      14:04 优化：允许选中装配打开工程图（按装配名拼 exb）；装配体只做图号分离、不回填、不穿透子件。
// 通道取证（零猜测）：
//   选择集   → ProbeCommon.ReadSelectionRaw（四通道已证，本工程多轮实机）
//   元素类型 → eZElementType：Z_ELEMENT_ASSEMBLY=2（装配，排除）、Z_ELEMENT_SHEETMETAL_PART=20（钣金）
//   零件种类 → IZPart::GetPartType(槽51) 掩码（eZPartType 2027 enums.csv）：
//              0x2=钣金；0x1/0x4/0x8/0x10/0x20=零件族（实体/线材/型材/创新/工程）；
//              0x40(Z_PART_NULL) 或 0=虚拟件候选（几何位全空，判据待实机校准，trace 留痕）
//   文档路径 → IZDoc.get_Name(槽11)：已保存场景返回全路径；无路径分隔符＝未保存，如实报错
//   打开 exb → IZBaseApp.OpenFile2(槽30)（本工程预热通道已证；热路径可能抛 "Invalid file format."，
//              文档其实已开——沿用预热口径：异常只记日志，用图纸窗口差集做权威判定）
// 铁律：判据只认回读/窗口取证，HRESULT 与 PostMessage 返回值一律不算成功。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    internal static class ComponentTool
    {
        // IZPart::GetPartType(槽51)：HRESULT(stdcall) out Int32 掩码（CaxaAutomation 同款槽位先例）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetPartTypeRaw(IntPtr self, out int pPartType);

        private static readonly Guid IID_IZPart = new Guid("F20F2411-05C4-4CA6-B2B4-5DB29531EDD7");
        private const int SlotPartGetPartType = 51;

        private static int QueryPartInterface(IntPtr pUnk, out IntPtr pPart)
        {
            Guid iid = IID_IZPart;
            return Marshal.QueryInterface(pUnk, ref iid, out pPart);
        }

        // eZElementType（2027 enums.csv）
        private const int Z_ELEMENT_ASSEMBLY = 2;
        private const int Z_ELEMENT_SHEETMETAL_PART = 20;

        // eZPartType 位
        private const int PT_SHEET_METAL = 0x2;
        private const int PT_GEOMETRY_ANY = 0x1 | 0x2 | 0x4 | 0x8;   // 实体/钣金/线材/型材

        /// <summary>元素分类结果。</summary>
        private sealed class Item
        {
            public string Name;
            public string Kind;    // 零件 / 钣金 / 虚拟件 / 装配 / 其他
            public int Type;
            public int Mask;       // 零件种类掩码（-1=读不到）
            public string Note;
        }

        // ================================================================ 1) 识别选中零部件

        /// <summary>
        /// 识别当前选中零部件（零件和钣金可填写，装配排除，虚拟件允许）。
        /// 返回中文判词（识别结果列表），全程落 trace。UI 线程同步跑（探针口径）。
        /// </summary>
        public static string IdentifySelected(IZBaseApp app)
        {
            const string tag = "parts-identify";
            Action<string> log = msg => Diagnostics.Log(msg);

            string err;
            IZSceneDoc scene = ProbeCommon.GetScene(app, out err);
            if (scene == null) return "识别失败：" + err;

            List<object> sel = ProbeCommon.ReadSelectionRaw(scene, tag, log);
            if (sel.Count == 0)
                return "未选中任何元素——请先在场景树或视口选中零部件（零件/钣金/虚拟件），再点本按钮。";

            var items = new List<Item>();
            foreach (object o in sel)
            {
                Item it = Classify(o);
                if (it != null) items.Add(it);
            }
            if (items.Count == 0) return "选中了 " + sel.Count + " 个对象，但都不是可识别的元素（第三态），详见 trace。";

            var fill = new List<string>();    // 可填写：零件/钣金/虚拟件
            var asm = new List<string>();     // 排除：装配
            var other = new List<string>();
            foreach (Item it in items)
            {
                log(string.Format("{0}: name=\"{1}\" type={2} mask=0x{3:X} ⇒ {4}{5}",
                    tag, it.Name, it.Type, it.Mask, it.Kind, it.Note == null ? "" : "（" + it.Note + "）"));
                if (it.Kind == "装配") asm.Add(it.Name);
                else if (it.Kind == "其他") other.Add(it.Name);
                else fill.Add(it.Kind + "「" + it.Name + "」");
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("识别 ").Append(items.Count).Append(" 项：");
            sb.Append(fill.Count == 0 ? "无可填写零部件" : "可填写 " + fill.Count + " 项（" + string.Join("、", fill.ToArray()) + "）");
            if (asm.Count > 0) sb.Append("；装配已排除 " + asm.Count + " 项（" + string.Join("、", asm.ToArray()) + "）");
            if (other.Count > 0) sb.Append("；其他 " + other.Count + " 项");
            return sb.ToString();
        }

        /// <summary>单元素分类：读 Type/Name，装配按类型排除，其余 QI IZPart 读种类掩码定零件/钣金/虚拟件。</summary>
        private static Item Classify(object o)
        {
            var it = new Item { Mask = -1 };
            IZElement el = o as IZElement;
            if (el == null) { it.Kind = "其他"; it.Name = "<非元素>"; return it; }
            it.Name = ProbeCommon.Safe(() => el.Name, "<无名>");
            try { it.Type = (int)el.Type; } catch { it.Type = -1; }

            if (it.Type == Z_ELEMENT_ASSEMBLY) { it.Kind = "装配"; return it; }
            if (it.Type == Z_ELEMENT_SHEETMETAL_PART) { it.Kind = "钣金"; ReadMask(el, it); return it; }

            ReadMask(el, it);
            if (it.Mask < 0)
            {
                it.Kind = "其他";
                it.Note = "IZPart 掩码读不到";
                return it;
            }
            if ((it.Mask & PT_SHEET_METAL) != 0) { it.Kind = "钣金"; return it; }
            if ((it.Mask & PT_GEOMETRY_ANY) != 0) { it.Kind = "零件"; return it; }
            // 几何位全空（0x40 Z_PART_NULL 或 0）＝虚拟件候选；判据待实机校准，trace 已留掩码
            it.Kind = "虚拟件";
            it.Note = "几何位全空，虚拟件候选";
            return it;
        }

        private static void ReadMask(object elemObj, Item it)
        {
            IntPtr pUnk = IntPtr.Zero, pPart = IntPtr.Zero;
            try
            {
                pUnk = Marshal.GetIUnknownForObject(elemObj);
                if (QueryPartInterface(pUnk, out pPart) != 0 || pPart == IntPtr.Zero) return;
                int mask;
                if (ProbeCommon.Fn<FnGetPartTypeRaw>(pPart, SlotPartGetPartType)(pPart, out mask) == 0)
                    it.Mask = mask;
            }
            catch { }
            finally
            {
                if (pPart != IntPtr.Zero) Marshal.Release(pPart);
                if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
            }
        }

        // ================================================================ 2) 打开工程图

        /// <summary>
        /// 打开与零件/装配同名、与当前设计环境同文件夹的 .exb。
        /// 11-09 用户需求：投递 OpenFile2 之前，先按「打开设计环境」口径处理选中元素——
        ///   ① 按名称自动选规则做图号分离（共享表 SC图号/SC名称…）；
        ///   ② 钣金→钣金件尺寸填写 / 其余零件→自制件规格材料填写（SC规格/SC材料）；
        ///   ③ 保存文档，保证随后打开的工程图引用到最新属性。
        /// 11-09 14:04 优化：**允许选中装配打开工程图**（按装配名拼 exb）；装配体只做图号分离，
        ///   不做规格/材料回填、不穿透子件（SeparatePart 按 IZElement QI 共享表，装配本身可写）。
        /// 选件识别放宽：装配按类型 2 直接收（不要求 IZPart）；其余 QI IZPart 成功即可，不再因
        ///   GetPartType 掩码读不到而误判「其他」（实机 Z_SEL_ANY 下先例）；展开件/非零件仍跳过。
        /// expectedDocBase 传空串＝跳过「零件名=文件名」校验（装配里的子件名与 .ics 文件名本就不同）。
        /// beforeDraft 在 OpenFile2 投递前采集（预热同款差集口径；此前 MainForm 在投递后才取基线，导致必然超时误报）。
        /// 返回 null＝已投递；否则返回中文错误。
        /// </summary>
        public static string OpenPartDrawing(IZBaseApp app, Action<string> log, out string exbPath,
            out string prepSummary, out List<IntPtr> beforeDraft, out string postAction)
        {
            const string tag = "open-dwg";
            exbPath = null;
            prepSummary = "";
            beforeDraft = new List<IntPtr>();
            // 打开工程图后的联动动作（按首个元素的类型判定）：
            //   钣金件(type=20) → titleblock（工程图标题栏设默认）
            //   装配(type=2)    → update3dbom（更新3dbom）
            //   其余（零件/虚拟件等）→ ""（不联动）
            postAction = "";

            string err;
            IZSceneDoc scene = ProbeCommon.GetScene(app, out err);
            if (scene == null) return "打开工程图失败：" + err;

            // ---- 1) 设计环境文件路径（IZDoc.get_Name 槽11；ActiveDoc 本身就是 IZDoc 表面）----
            IZDoc sceneDoc = null;
            try { sceneDoc = app.ActiveDoc; } catch (Exception ex) { log(tag + ": ActiveDoc FAIL " + ProbeCommon.Hr(ex)); }
            string docName = sceneDoc == null ? null : ProbeCommon.Safe(() => sceneDoc.Name, null);
            log(tag + ": scene doc.Name=\"" + (docName ?? "<空>") + "\"");
            if (string.IsNullOrEmpty(docName) ||
                (docName.IndexOf('\\') < 0 && docName.IndexOf('/') < 0))
                return "打开工程图失败：设计环境尚未保存到磁盘（doc.Name=\"" + (docName ?? "<空>") +
                    "\"）——请先保存场景，保证工程图 exb 与它同文件夹。";
            string scenePath = docName.Replace('/', '\\');
            string folder = Path.GetDirectoryName(scenePath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return "打开工程图失败：设计环境文件夹不存在（" + folder + "）";

            // ---- 2) 选中元素（放宽识别：装配按类型2直接收；其余 IZPart 能 QI 到就算零部件）----
            List<object> sel = ProbeCommon.ReadSelectionRaw(scene, tag, log);
            var elements = new List<IZElement>();
            foreach (object o in sel)
            {
                IZElement el = o as IZElement;
                if (el == null) { log(tag + ": 跳过非元素选中项"); continue; }
                int type = -1;
                try { type = (int)el.Type; } catch { }
                string nm = ProbeCommon.Safe(() => el.Name, "");
                if (nm.Length == 0) { log(tag + ": 跳过无名元素"); continue; }
                if (nm.IndexOf("展开", StringComparison.Ordinal) >= 0)
                { log(tag + ": 跳过展开件「" + nm + "」（钣金展开态配置产物，非独立零部件）"); continue; }
                if (type == Z_ELEMENT_ASSEMBLY)
                {
                    // 11-09 14:04：允许选中装配打开工程图（按装配名找 exb）；
                    // 装配只做图号分离，不穿透子件、不做规格/材料回填（见第 3 步）。
                    log(tag + ": 选中装配「" + nm + "」（只做图号分离，不穿透、不回填）");
                }
                else if (o as IZPart == null)
                {
                    log(tag + ": 跳过非零件元素「" + nm + "」（类型 " + type + "）"); continue;
                }
                if (elements.Contains(el)) continue;
                elements.Add(el);
            }
            if (elements.Count == 0)
                return "打开工程图失败：选中对象里没有可处理的零件或装配（展开件/非零件已排除）——请在 3D 设计树中直接选中零件、钣金件或装配。";

            string partName = ProbeCommon.Safe(() => elements[0].Name, "");
            int firstType = -1;
            try { firstType = (int)elements[0].Type; } catch { }
            if (firstType == Z_ELEMENT_SHEETMETAL_PART) postAction = "titleblock";
            else if (firstType == Z_ELEMENT_ASSEMBLY) postAction = "update3dbom";
            log(tag + ": 目标零件=\"" + partName + "\"（选中 " + elements.Count + " 个元素[零件/装配]，全部做前置处理）" +
                (postAction.Length > 0 ? "；打开工程图后联动动作=" + postAction : ""));

            // ---- 3) 前置处理：图号分离 + 规格/材料回填（参考 DesignEnvTool.ProcessComponents）----
            var rules = DrawingNoSepTool.LoadRules();
            int nSep = 0, nFill = 0, nFillFail = 0, nAsm = 0;
            var fails = new List<string>();
            foreach (IZElement el in elements)
            {
                string name = ProbeCommon.Safe(() => el.Name, "");
                int type = -1;
                try { type = (int)el.Type; } catch { }
                log(tag + ": 前置处理「" + name + "」（类型 " + type + (type == Z_ELEMENT_ASSEMBLY ? " 装配" : "") + "）");

                // ① 图号分离：按名称自动选规则（无匹配跳过，不报错）；装配与零件同样处理
                DrawingNoSepTool.Rule rule = DrawingNoSepTool.AutoSelectRule(name, rules);
                if (rule != null)
                {
                    log(tag + ": 自动选规则「" + rule.Text + "」→ 图号分离");
                    string s = DrawingNoSepTool.SeparatePart(app, sceneDoc, el, name, rule, log);
                    log(tag + ": " + s);
                    if (s != null && s.StartsWith("图号分离完成", StringComparison.Ordinal)) nSep++;
                    else fails.Add("「" + name + "」图号分离未完成：" + (s ?? "<空>"));
                }
                else log(tag + ": 「" + name + "」无匹配图号分离规则，跳过分离");

                // ② 装配体只进行图号分离：不回填、不穿透子件，也不计入回填失败（用户 14:04 口径）
                if (type == Z_ELEMENT_ASSEMBLY)
                {
                    nAsm++;
                    log(tag + ": 装配「" + name + "」按口径只做图号分离，跳过规格/材料回填（不穿透子件）");
                    continue;
                }

                // ③ 规格/材料回填：钣金(20)→钣金件尺寸填写；其余→自制件规格材料填写
                //    传空 expectedDocBase，跳过「零件名=文件名」校验；外部链接件仍由 Fill 内部拒填。
                bool ok;
                string f;
                if (type == Z_ELEMENT_SHEETMETAL_PART)
                {
                    log(tag + ": 「" + name + "」→ 钣金件尺寸填写");
                    f = SheetSizeTool.FillSheetSizeForElement(app, sceneDoc, el, "", log, out ok);
                }
                else
                {
                    log(tag + ": 「" + name + "」→ 自制件规格材料填写");
                    f = PartSpecFillTool.FillSpecForElement(app, el, "", log, out ok);
                }
                if (f != null) log(tag + ": " + f);
                if (ok) nFill++;
                else { nFillFail++; fails.Add("「" + name + "」" + (f ?? "规格/材料回填未完成（外部链接件或第三态）")); }
            }
            // ④ 保存（SeparatePart / 钣金填写已各自保存，这里兜底确保属性落盘后才打开工程图）
            try { if (sceneDoc != null) { sceneDoc.Save(); log(tag + ": 前置处理完成，设计环境已保存"); } }
            catch (Exception ex) { log(tag + ": 保存文档 EX " + ProbeCommon.Hr(ex)); }

            var sb = new System.Text.StringBuilder();
            sb.Append("前置处理 ").Append(elements.Count).Append(" 个选中件：图号分离 ").Append(nSep)
              .Append(" 个，规格/材料回填成功 ").Append(nFill).Append(" 个");
            if (nAsm > 0) sb.Append("（其中装配 ").Append(nAsm).Append(" 个仅分离）");
            if (nFillFail > 0) sb.Append("、回填失败 ").Append(nFillFail).Append(" 个");
            if (fails.Count > 0) sb.Append("（").Append(string.Join("；", fails.ToArray())).Append("）");
            prepSummary = sb.ToString();
            log(tag + ": " + prepSummary);

            // ---- 4) 拼同文件夹 exb 并做存在性检查（精确同名 → 唯一同图号候选兜底）----
            foreach (char c in Path.GetInvalidFileNameChars()) partName = partName.Replace(c, '_');
            string exb = Path.Combine(folder, partName + ".exb");
            if (!File.Exists(exb))
            {
                string key = partName;
                int sp = partName.IndexOf(' ');
                if (sp > 0) key = partName.Substring(0, sp);
                var cand = new List<string>();
                try
                {
                    foreach (string f in Directory.GetFiles(folder, "*.exb"))
                        if (Path.GetFileNameWithoutExtension(f).StartsWith(key, StringComparison.OrdinalIgnoreCase))
                            cand.Add(f);
                }
                catch { }
                if (cand.Count == 1)
                {
                    log(tag + ": 同名 exb 不存在，采用唯一同图号候选「" + Path.GetFileName(cand[0]) + "」");
                    exb = cand[0];
                }
                else
                {
                    var names = new List<string>();
                    foreach (string f in cand) names.Add(Path.GetFileName(f));
                    return "未找到工程图：" + exb + Environment.NewLine +
                        "（要求与设计环境同文件夹、与零件同名，扩展名 .exb；" +
                        (cand.Count == 0 ? "同文件夹无同图号 .exb" : "同图号候选 " + cand.Count + " 个，请确认文件名：" + string.Join("、", names.ToArray())) + "）";
                }
            }
            exbPath = exb;
            try { FileInfo fi = new FileInfo(exb); log(tag + ": exb 存在，大小 " + fi.Length + "B，修改时间 " + fi.LastWriteTime); }
            catch { }

            // ---- 5) OpenFile2 投递（已证通道；热路径异常只记日志，窗口差集另行判定）----
            // ★ 差集基线必须在投递「之前」采：此前 MainForm 在 OpenPartDrawing 返回后才取 before，
            //   若图纸窗口在 OpenFile2 内同步出现，会被算进基线 → 10 秒差集必然超时误报。
            beforeDraft = DrawingProbe.ListDraftDocs();
            log(tag + ": 投递前已有 Draft 图纸窗口 " + beforeDraft.Count + " 个（差集基线）");
            log(tag + ": OpenFile2 投递 " + exb);
            try
            {
                bool ao;
                app.OpenFile2(exb, false, true, out ao);
                log(tag + ": OpenFile2 正常返回 alreadyOpen=" + ao);
            }
            catch (Exception ex)
            {
                // 预热同款怪癖：COM 对打开的返回不可靠，热路径正常也抛 "Invalid file format."，文档其实已开
                log(tag + ": OpenFile2 返回异常（热路径下正常）: " + ProbeCommon.Hr(ex) + " " + ex.Message);
            }
            return null;
        }
    }
}
