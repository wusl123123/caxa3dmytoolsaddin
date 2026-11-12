// 钣金件尺寸填写（2026-11-08 20:35 用户指令：整体移植自 C:\code\ICAPI\caxa3ddiffrename\SheetSizeTool.cs，
// 该工具在 diffrename 实机验证过；SizeBoxTool 依赖改用本工程 ProbeCommon 同源实现——
// ProbeCommon.TryBoxToWorld/ToDoubleArray 即移植自 caxa3dsizeboxread，两工程 interop 字节一致 md5 29a23396…）。
//
// 流程：选中「文件名.ics---零件名（虚拟）」的钣金件（元素类型 20）→ 取材料（GetMaterialNameProperty）
//   → 板料板厚（GetStockProperties）→ 找展开件（树查「展开<零件名>」→ GetFlatPart → Unfold 兜底）
//   → 展开件最小包围盒两最大尺寸（长/宽）→ 板厚与盒最小边一致性校验（±0.1mm）
//   → SC规格=长x宽 / SC材料=<材料>板T=厚 → 共享自定义属性表回填（SetValueByName+回读判据，
//     参考 caxa3dBatchMaster CaxaPropWrite：失败补 AddCustomProp 槽14）
//   → 恢复钣金默认设计环境（Refold + DefaultConfiguration.Activate）→ 复核补写 → 保存文档。
// 数值：mm、1 位小数、四舍五入、末尾消零（FormatMm：123.0→"123"、123.35→"123.4"）。
// 共享表铁律（规则 14.7/14.9）：只认 IsSharedMgr()==true 的管理器，三候选全无则判失败，绝不拿零件级表凑数。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    /// <summary>钣金件尺寸填写核心（移植自 caxa3ddiffrename，字段名取其规则文件默认值 SC规格/SC材料）。</summary>
    internal static class SheetSizeTool
    {
        private const int ElemTypeSheetMetalPart = 20;
        private const string SpecField = "SC规格";
        private const string MaterialField = "SC材料";

        // ================================================================ 执行入口（MainForm 按钮直调）

        public static string Execute(IZBaseApp app)
        {
            var log = new Action<string>(msg => Diagnostics.Log("sheet-size: " + msg));
            try
            {
                ExecuteResult r = ExecuteCore(app, log);
                return "「" + r.PartName + "」已填写：SC规格=\"" + r.Spec + "\"；SC材料=\"" + r.MaterialSpec +
                    "\"（板厚 " + FormatMm(r.ThicknessMm) + "mm" + (r.Unfolded ? "；曾自动展开并回折" : "") +
                    (r.ConfigRestored ? "；已恢复默认配置" : "") +
                    (r.MaterialFallback ? "；★材料未读到，SC材料 只有板厚" : "") + "）" +
                    (r.Saved ? "，文档已保存" : "，★文档保存未确认");
            }
            catch (InvalidOperationException ex)
            {
                return "钣金件尺寸填写中止：" + ex.Message;
            }
        }

        // ================================================================ 校验

        private sealed class ValidateResult
        {
            public bool Ok;
            public string Message = "";
            public IZDoc Doc;
            public IZSceneDoc Scene;
            public IZElement Element;
            public IZPart Part;
            public IZSheetMetalPart SheetMetal;
            public string DocName = "";
            public string DocBaseName = "";
            public string PartName = "";
            public int ElementType;
            public bool ExternallyLinked;
            public string LinkedFile = "";
        }

        private sealed class ExecuteResult
        {
            public string PartName = "";
            public string Spec = "";
            public string Material = "";
            public string MaterialSpec = "";
            public double ThicknessMm;
            public bool Unfolded;
            public bool ConfigRestored;
            public bool Saved;
            public bool MaterialFallback;
        }

        private static ValidateResult Validate(IZBaseApp app)
        {
            ValidateResult r = new ValidateResult();
            if (app == null) { r.Message = "未连接到 CAXA 3D。"; return r; }

            IZDoc doc = null;
            try { doc = app.ActiveDoc; }
            catch (Exception ex) { r.Message = "读取活动文档失败：" + ex.Message; return r; }
            if (doc == null) { r.Message = "当前没有活动文档，请先打开钣金件 .ics。"; return r; }
            r.Doc = doc;

            IZSceneDoc scene = doc as IZSceneDoc;
            if (scene == null) { r.Message = "当前活动文档不是 3D 设计环境。"; return r; }
            r.Scene = scene;

            try { r.DocName = doc.Name ?? ""; } catch { r.DocName = ""; }
            r.DocBaseName = DocBaseName(r.DocName);
            if (r.DocBaseName.Length == 0) { r.Message = "当前文档没有有效的 .ics 文件名。"; return r; }

            // 选区：本工程 ProbeCommon 统一通道（口径同其它按钮）
            string selError;
            IZSceneDoc sceneProbe = ProbeCommon.GetScene(app, out selError);
            List<object> raws = sceneProbe == null
                ? new List<object>()
                : ProbeCommon.ReadSelectionRaw(sceneProbe, "sheet-size", m => Diagnostics.Log("sheet-size: " + m));
            var selection = new List<IZElement>();
            foreach (object o in raws) { var el = o as IZElement; if (el != null) selection.Add(el); }

            if (selection.Count == 0) { r.Message = "请先在 3D 设计树中选中钣金件（文件名.ics---零件名）。"; return r; }
            if (selection.Count > 1) { r.Message = "一次只能选中一个钣金件，请取消其余选中项。"; return r; }

            IZElement elem = selection[0];
            r.Element = elem;

            try { r.ElementType = (int)elem.Type; }
            catch (Exception ex) { r.Message = "读取元素类型失败：" + ex.Message; return r; }
            if (r.ElementType != ElemTypeSheetMetalPart)
            {
                r.Message = "当前选中的不是钣金件（元素类型 " + r.ElementType + "，需 20=钣金件）。";
                return r;
            }

            try { r.PartName = elem.Name ?? ""; } catch { r.PartName = ""; }
            if (r.PartName.Length == 0) { r.Message = "选中件没有名称。"; return r; }
            if (r.PartName.IndexOf("展开", StringComparison.Ordinal) >= 0)
            {
                r.Message = "当前选中的是展开件，请选中折叠状态的钣金件。";
                return r;
            }

            r.Part = elem as IZPart;
            if (r.Part == null) { r.Message = "选中元素不是零件（无法取得 IZPart）。"; return r; }

            bool linked;
            r.LinkedFile = r.Part.GetExternallyLinkedInfo(out linked) ?? "";
            r.ExternallyLinked = linked && !string.IsNullOrEmpty(r.LinkedFile);
            if (r.ExternallyLinked)
            {
                r.Message = "选中件是外部链接件（" + Path.GetFileName(r.LinkedFile) + "），只允许虚拟零部件。";
                return r;
            }

            if (!string.Equals(r.DocBaseName, r.PartName, StringComparison.OrdinalIgnoreCase))
            {
                r.Message = string.Format("零件名「{0}」与文件名「{1}.ics」不一致，必须满足 文件名=零件名。",
                    r.PartName, r.DocBaseName);
                return r;
            }

            r.SheetMetal = elem as IZSheetMetalPart;
            if (r.SheetMetal == null) { r.Message = "选中件未实现 IZSheetMetalPart。"; return r; }

            r.Ok = true;
            r.Message = "可执行：" + r.DocBaseName + ".ics---" + r.PartName + "（虚拟）";
            return r;
        }

        /// <summary>取文档纯文件名（去目录、去 .ics 后缀）。</summary>
        private static string DocBaseName(string docName)
        {
            if (string.IsNullOrEmpty(docName)) return "";
            string n = docName;
            int slash = Math.Max(n.LastIndexOf('\\'), n.LastIndexOf('/'));
            if (slash >= 0) n = n.Substring(slash + 1);
            if (n.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 4);
            return n;
        }

        // ================================================================ 执行

        private static ExecuteResult ExecuteCore(IZBaseApp app, Action<string> log)
        {
            ValidateResult v = Validate(app);
            if (!v.Ok) throw new InvalidOperationException(v.Message);
            return FillSheetCore(app, v.Doc, v.Scene, v.Element, v.SheetMetal, v.PartName, log);
        }

        // ================================================================ 核心（供 Execute 与 FillSheetSizeForElement 共用）

        /// <summary>
        /// 核心：材料→板厚→展开件→包围盒→写共享表→恢复默认配置→保存（供按钮 Execute 与「打开/关闭设计环境」共用）。
        /// doc 为所属文档（用于保存）；scene 用于恢复钣金折叠配置；app 用于展开件包围盒世界变换。
        /// </summary>
        private static ExecuteResult FillSheetCore(IZBaseApp app, IZDoc doc, IZSceneDoc scene, IZElement elem, IZSheetMetalPart sheetMetal, string partName, Action<string> log)
        {
            ExecuteResult res = new ExecuteResult();
            res.PartName = partName;
            string docName = "";
            try { if (doc != null) docName = doc.Name ?? ""; } catch { }
            log("文档：" + docName);
            log("零件：" + partName + "（虚拟，钣金件）");

            // ---- 1) 材料：零件的材料名称属性（API 一手通道，diffrename 先例）
            string material = "";
            try
            {
                string matName;
                elem.GetMaterialNameProperty(out matName);
                if (!string.IsNullOrWhiteSpace(matName)) material = matName.Trim();
            }
            catch (Exception ex) { Diagnostics.Log("sheet-size: GetMaterialNameProperty 失败：" + ex.Message); }
            if (material.Length == 0)
            {
                res.MaterialFallback = true;
                log("警告：未读取到材料名称，SC材料将只填写板厚。");
            }
            res.Material = material;
            log("材料：" + (material.Length == 0 ? "（空）" : material));

            // ---- 2) 板厚：板料属性（mm）
            string thickDetail;
            res.ThicknessMm = ReadThicknessMm(sheetMetal, out thickDetail);
            log("板厚：" + FormatMm(res.ThicknessMm) + " mm（" + thickDetail + "）");
            if (res.ThicknessMm <= 0)
                throw new InvalidOperationException("读取板厚失败（板料板厚为 0）。");

            // ---- 3) 找展开件（名「展开<零件名>」），必要时 Unfold
            bool unfolded;
            IZElement flat = FindFlatPart(scene, sheetMetal, partName, log, out unfolded);
            log("展开件：" + (SafeName(flat).Length == 0 ? "（未命名）" : SafeName(flat)));

            // ---- 4) 展开件包围盒 → 两最大尺寸（长、宽）
            //      ★ 11-09 实机：文档由「打开设计环境」刚 OpenFile2 打开时处于折叠配置，
            //        展开件几何未激活 ⇒ GetBody E_FAIL（三级阶梯可能全败）。
            //        此时按规则第 28 节已验证通道 Unfold() 激活展开态 → 重取展开件 → 再读；
            //        置 unfolded=true，交由后面 RestoreDefaultConfiguration 负责 Refold + 激活默认配置。
            double[] dimsMm;
            try
            {
                dimsMm = ReadFlatDimsMm(app, flat, log);
            }
            catch (InvalidOperationException firstEx)
            {
                log("展开件包围盒首读失败（" + firstEx.Message + "）→ 尝试 Unfold() 激活展开态后重读");
                bool activated = false;
                try { sheetMetal.Unfold(); activated = true; log("Unfold() 已调用"); }
                catch (Exception ex) { log("Unfold() 失败：" + ex.Message); }

                IZElement flat2 = flat;
                if (activated)
                {
                    WaitFor(delegate
                    {
                        IZElement f = null;
                        try { f = sheetMetal.GetFlatPart(); } catch { }
                        return f != null;
                    }, 3000);
                    try { IZElement f2 = sheetMetal.GetFlatPart(); if (f2 != null) flat2 = f2; }
                    catch (Exception ex) { log("重取展开件失败：" + ex.Message); }
                    log("展开态激活后展开件：「" + SafeName(flat2) + "」");
                    unfolded = true;
                }
                dimsMm = ReadFlatDimsMm(app, flat2, log);   // 再败则如实抛（含三级 note），不写属性
            }
            res.Unfolded = unfolded;
            Array.Sort(dimsMm);   // 升序：[2]=长 [1]=宽 [0]=厚
            double lengthMm = dimsMm[2], widthMm = dimsMm[1], thicknessFromBox = dimsMm[0];

            const double ThicknessTolMm = 0.1;
            if (Math.Abs(thicknessFromBox - res.ThicknessMm) > ThicknessTolMm)
                throw new InvalidOperationException(string.Format(
                    "包围盒最小尺寸 {0} mm 与板厚 {1} mm 不一致（容差 {2} mm），无法确认展开件方向，已中止（未写入属性）。",
                    FormatMm(thicknessFromBox), FormatMm(res.ThicknessMm), ThicknessTolMm));

            res.Spec = FormatMm(lengthMm) + "x" + FormatMm(widthMm);
            res.MaterialSpec = (material.Length == 0 ? "" : material) + "板T=" + FormatMm(res.ThicknessMm);
            log("长宽判定：长=" + FormatMm(lengthMm) + " 宽=" + FormatMm(widthMm) + " 厚=" + FormatMm(thicknessFromBox));
            log("板厚校验：包围盒最小边 = 板料板厚，通过。");
            log("SC规格 = " + res.Spec);
            log("SC材料 = " + res.MaterialSpec);

            // ---- 5) 回填共享自定义属性表（BatchMaster CaxaPropWrite 姿势：SetValueByName→AddCustomProp 回退→回读判据）
            WriteProp(elem, SpecField, res.Spec, log);
            WriteProp(elem, MaterialField, res.MaterialSpec, log);

            // ---- 6) 钣金件设计环境恢复默认（折叠配置）
            res.ConfigRestored = RestoreDefaultConfiguration(scene, sheetMetal, unfolded, log);

            // ---- 6.5) 配置切换可能重建元素状态：复核并按需补写
            RecheckAndFixProp(elem, SpecField, res.Spec, log);
            RecheckAndFixProp(elem, MaterialField, res.MaterialSpec, log);

            // ---- 7) 保存文档
            try
            {
                if (doc != null)
                {
                    doc.Save();
                    FileInfo fi = new FileInfo(docName);
                    res.Saved = fi.Exists && fi.Length > 0;
                    log("已保存：" + (res.Saved ? docName + "（" + fi.Length + "B）" : "文件不存在或为空"));
                }
                else { res.Saved = false; log("文档为空，跳过保存"); }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("sheet-size: 保存文档 EX " + ex.Message);
                res.Saved = false;
                log("保存文档异常：" + ex.Message);
            }

            return res;
        }

        /// <summary>
        /// 供「打开/关闭设计环境」调用：对指定钣金元素执行尺寸填写（不读选区、不依赖活动文档选区）。
        /// expectedDocBase 非空时强制 零件名=文件名 校验（只认同名虚拟件）；空串跳过该校验。
        /// 返回 null 表示不适用/跳过（非错误，如非钣金、展开件、外部链接、名称不符）；否则返回填写结论文案。
        /// </summary>
        public static string FillSheetSizeForElement(IZBaseApp app, IZDoc doc, IZElement elem, string expectedDocBase, Action<string> log, out bool ok)
        {
            ok = false;   // 默认失败/跳过；只有共享表回读一致才置 true
            int type = -1;
            try { type = (int)elem.Type; } catch { }
            if (type != ElemTypeSheetMetalPart)
            { log("sheet-size: 元素类型 " + type + " 非钣金件（需 20），跳过钣金件尺寸填写"); return null; }

            string partName = ProbeCommon.Safe(() => elem.Name, "");
            if (partName.Length == 0) { log("sheet-size: 元素无名称，跳过"); return null; }
            if (partName.IndexOf("展开", StringComparison.Ordinal) >= 0)
            { log("sheet-size: 「" + partName + "」是展开件，跳过（请选中折叠态钣金）"); return null; }

            var part = elem as IZPart;
            if (part == null) { log("sheet-size: 元素非零件（无 IZPart），跳过"); return null; }

            bool linked;
            string linkedFile = part.GetExternallyLinkedInfo(out linked) ?? "";
            if (linked && !string.IsNullOrEmpty(linkedFile))
            { log("sheet-size: 「" + partName + "」是外部链接件（" + Path.GetFileName(linkedFile) + "），只允许虚拟件，跳过"); return null; }

            if (!string.IsNullOrEmpty(expectedDocBase) && !string.Equals(expectedDocBase, partName, StringComparison.OrdinalIgnoreCase))
            { log("sheet-size: 零件名「" + partName + "」≠文件名「" + expectedDocBase + "」，跳过（只认同名虚拟件）"); return null; }

            var sheetMetal = elem as IZSheetMetalPart;
            if (sheetMetal == null) { log("sheet-size: 元素未实现 IZSheetMetalPart，跳过"); return null; }

            IZSceneDoc scene = doc as IZSceneDoc;
            if (scene == null) { log("sheet-size: 文档非 3D 场景，跳过"); return null; }

            try
            {
                ExecuteResult r = FillSheetCore(app, doc, scene, elem, sheetMetal, partName, log);
                ok = true;   // FillSheetCore 全程无异常 ⇒ 共享表已写且回读一致（WriteProp 内置判据）
                return "「" + r.PartName + "」已填写：SC规格=\"" + r.Spec + "\"；SC材料=\"" + r.MaterialSpec + "\"（板厚 " +
                    FormatMm(r.ThicknessMm) + "mm" + (r.Unfolded ? "；曾自动展开并回折" : "") +
                    (r.ConfigRestored ? "；已恢复默认配置" : "") +
                    (r.MaterialFallback ? "；★材料未读到，SC材料 只有板厚" : "") + "）" +
                    (r.Saved ? "，文档已保存" : "，★文档保存未确认");
            }
            catch (InvalidOperationException ex)
            {
                string why = "钣金件尺寸填写中止：" + ex.Message;
                log("sheet-size: " + why);
                return why;   // 回传中止原因，供「打开/关闭设计环境」在状态栏/trace 明确显示（不静默吞掉）
            }
        }

        // ================================================================ 展开件

        private static IZElement FindFlatPart(IZSceneDoc scene, IZSheetMetalPart sm, string partName, Action<string> log, out bool unfolded)
        {
            unfolded = false;
            string expected = "展开" + partName;

            // 1) 设计树中按「展开<零件名>」精确查找
            IZElement found = FindByName(scene, expected);
            if (found != null)
            {
                log("在设计树中找到展开件「" + expected + "」");
                return found;
            }

            // 2) IZSheetMetalPart.GetFlatPart
            IZElement flat = sm.GetFlatPart();
            if (flat != null)
            {
                log("GetFlatPart 返回展开件「" + SafeName(flat) + "」");
                return flat;
            }

            // 3) 兜底：Unfold 后再取
            log("未找到展开件，调用 Unfold() 展开…");
            try { sm.Unfold(); }
            catch (Exception ex) { log("Unfold 失败：" + ex.Message); }
            unfolded = true;

            WaitFor(delegate
            {
                try { flat = sm.GetFlatPart(); }
                catch { flat = null; }
                return flat != null;
            }, 5000);

            if (flat == null)
                throw new InvalidOperationException("找不到展开件：设计树无「" + expected + "」，GetFlatPart 也为空。");
            log("Unfold 后取得展开件「" + SafeName(flat) + "」");
            return flat;
        }

        private static IZElement FindByName(IZSceneDoc scene, string name)
        {
            IZElement top = null;
            try { top = scene.GetTopElement(); } catch { }
            if (top == null) return null;

            var stack = new Stack<KeyValuePair<IZElement, int>>();
            stack.Push(new KeyValuePair<IZElement, int>(top, 0));
            int visited = 0;

            while (stack.Count > 0)
            {
                KeyValuePair<IZElement, int> item = stack.Pop();
                IZElement e = item.Key;
                int depth = item.Value;
                if (++visited > 5000) break;

                string n = SafeName(e);
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return e;
                if (depth >= 12) continue;

                IZElement child = null;
                try { child = e.GetFirstChild(); } catch { }
                while (child != null)
                {
                    stack.Push(new KeyValuePair<IZElement, int>(child, depth + 1));
                    IZElement next = null;
                    try { next = child.GetNextChild(); } catch { }
                    child = next;
                }
            }
            return null;
        }

        private static string SafeName(IZElement e)
        {
            if (e == null) return "";
            try { return e.Name ?? ""; } catch { return ""; }
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                try { Application.DoEvents(); } catch { }
                try { if (condition()) return true; } catch { }
                System.Threading.Thread.Sleep(100);
            }
            return false;
        }

        // ================================================================ 板厚

        private static double ReadThicknessMm(IZSheetMetalPart sm, out string detail)
        {
            detail = "";
            if (sm == null) { detail = "IZSheetMetalPart 为空"; return 0; }

            double stockTh = 0, stockK = 0, stockMinR = 0;
            string stockName = "", stockCode = "";
            int gauge = 0;
            sm.GetStockProperties(out stockName, out stockCode, out stockK, out gauge,
                out stockTh, out stockK, out stockMinR);

            if (stockTh <= 0) { detail = "板料「" + stockName + "」板厚为 0"; return 0; }
            detail = string.Format("板料「{0}」={1:0.###}mm", stockName, stockTh);
            if (stockTh > 20 || stockTh < 0.15) detail += "（警告：板厚异常）";
            return stockTh;
        }

        // ================================================================ 展开件包围盒（三级阶梯，照抄 caxa3ddiffrename\SizeBoxTool.ReadOne）
        //
        // 11-09 实机教训（用户报「回填失败」）：文档由「打开设计环境」OpenFile2 打开后默认处于【折叠配置】，
        //   展开件几何未激活 ⇒ IZPart.GetBody(true) 恒 E_FAIL（trace: 「GetBody 异常：HRESULT E_FAIL」）。
        //   原移植版只留了 IZBody 一级且 GetBody 一抛异常就整体中止 ⇒ SC规格/SC材料 一个都没写。
        //   参考工程 ReadOne 是三级阶梯，任一级成功即返回（全部为只读、不改文档）：
        //     ① IZPart.GetBody(true) → IZBody.GetBoundingBox(13)/GetTightBoundingBox(21) 局部 6 值 + TryBoxToWorld
        //     ② IZAssembly.GetBoundingBox(false)（槽53，直接世界坐标；本工程 FeatureBoxProbe 实证）
        //     ③ IZSizeBox.Length/Width/Height（槽7/9/11，参数化尺寸箱，单位米；非几何盒，
        //        最终仍受「板厚 ±0.1mm 一致性校验」把关，值不对就中止不写属性）
        //   每一级失败只记 note、不抛；三级全失败才抛，并把各级 note 全部带进消息（探针取证）。
        private static double[] ReadFlatDimsMm(IZBaseApp app, IZElement elem, Action<string> log)
        {
            var notes = new List<string>();

            // ---- ① IZBody 几何包围盒（最可信）
            IZPart part = elem as IZPart;
            if (part != null)
            {
                IZBody body = null;
                try { body = part.GetBody(true); }   // true = 轻量化零件也取实体
                catch (Exception ex)
                {
                    notes.Add("GetBody: " + ex.Message);
                    log("包围盒① GetBody 失败（" + ex.Message + "）→ 转②/③兜底");
                }
                if (body != null)
                {
                    double[] aabb = null, tight = null;
                    try { aabb = ProbeCommon.ToDoubleArray(body.GetBoundingBox()); }         // 槽13 局部
                    catch (Exception ex) { notes.Add("GetBoundingBox: " + ex.Message); }
                    try { tight = ProbeCommon.ToDoubleArray(body.GetTightBoundingBox()); }   // 槽21 局部紧致
                    catch (Exception ex) { notes.Add("GetTightBoundingBox: " + ex.Message); }

                    double[] world = null, worldTight = null;
                    if (aabb != null && aabb.Length >= 6) ProbeCommon.TryBoxToWorld(app, elem, aabb, out world);
                    if (tight != null && tight.Length >= 6) ProbeCommon.TryBoxToWorld(app, elem, tight, out worldTight);

                    double[] pick = null; string src = "";
                    if (worldTight != null) { pick = worldTight; src = "①IZBody 紧致盒（世界轴向）"; }
                    else if (world != null) { pick = world; src = "①IZBody 整体盒（世界轴向）"; }
                    else if (tight != null && tight.Length >= 6) { pick = tight; src = "①IZBody 紧致盒（局部值，变换到世界失败——展开件一般无旋转，照用）"; }
                    else if (aabb != null && aabb.Length >= 6) { pick = aabb; src = "①IZBody 整体盒（局部值，变换到世界失败——照用）"; }

                    if (pick != null) { log("包围盒：" + src); return DimsFromBox6(pick); }
                    notes.Add("IZBody 两个盒都读不到");
                }
            }
            else notes.Add("展开件 QI IZPart 失败");

            // ---- ② IZAssembly.GetBoundingBox(false)（槽53，世界坐标）
            IZAssembly asm = elem as IZAssembly;
            if (asm != null)
            {
                try
                {
                    double[] v = ProbeCommon.ToDoubleArray(asm.GetBoundingBox(false));
                    if (v != null && v.Length >= 6)
                    {
                        log("包围盒：②IZAssembly.GetBoundingBox(false)（世界坐标）");
                        return DimsFromBox6(v);
                    }
                    notes.Add("IZAssembly.GetBoundingBox 返回空/不足 6 值");
                }
                catch (Exception ex) { notes.Add("IZAssembly.GetBoundingBox: " + ex.Message); }
            }
            else notes.Add("展开件 QI IZAssembly 失败");

            // ---- ③ IZSizeBox 参数化尺寸箱（槽7/9/11 get_Length/Width/Height，单位米）
            IZSizeBox sb = elem as IZSizeBox;
            if (sb != null)
            {
                try
                {
                    double l = sb.Length, w = sb.Width, h = sb.Height;
                    log(string.Format(CultureInfo.InvariantCulture,
                        "包围盒③ IZSizeBox 尺寸箱：长={0} 宽={1} 高={2} m（参数化尺寸箱，非几何盒）", l, w, h));
                    if (Math.Abs(l) > 1e-9 || Math.Abs(w) > 1e-9 || Math.Abs(h) > 1e-9)
                        return new double[] { Math.Abs(l) * 1000.0, Math.Abs(w) * 1000.0, Math.Abs(h) * 1000.0 };
                    notes.Add("IZSizeBox 三向全 0");
                }
                catch (Exception ex) { notes.Add("IZSizeBox: " + ex.Message); }
            }
            else notes.Add("展开件 QI IZSizeBox 失败");

            throw new InvalidOperationException("展开件包围盒三级阶梯全失败：" + string.Join(" | ", notes.ToArray()));
        }

        /// <summary>6 值 AABB（米）→ 三向尺寸（mm）。</summary>
        private static double[] DimsFromBox6(double[] pick)
        {
            return new double[]
            {
                Math.Abs(pick[3] - pick[0]) * 1000.0,
                Math.Abs(pick[4] - pick[1]) * 1000.0,
                Math.Abs(pick[5] - pick[2]) * 1000.0
            };
        }

        // ================================================================ 自定义属性（共享表铁律，RCW 直调——diffrename 实机同款）

        private static IZCustomPropMgr GetSharedPropMgr(IZElement elem)
        {
            foreach (int selector in new int[] { 0, 1 })
            {
                IZCustomPropMgr mgr = null;
                try { mgr = elem.GetCustomPropManager(selector) as IZCustomPropMgr; } catch { }
                if (mgr == null) continue;
                bool shared = false;
                try { shared = mgr.IsSharedMgr(); } catch { }
                if (shared) return mgr;
            }

            try
            {
                IZCustomPropMgr prop = elem.get_CustomPropMgr(true) as IZCustomPropMgr;
                if (prop != null)
                {
                    bool confirmed = false;
                    try { confirmed = prop.IsSharedMgr(); } catch { }
                    if (confirmed) return prop;
                }
            }
            catch (Exception ex) { Diagnostics.Log("sheet-size: get_CustomPropMgr 失败：" + ex.Message); }

            Diagnostics.Log("sheet-size: GetSharedPropMgr 三个候选都没有 IsSharedMgr=true，按共享表铁律本次不读写任何其它表");
            return null;
        }

        private static string ReadCustomProp(IZElement elem, string name)
        {
            IZCustomPropMgr mgr = GetSharedPropMgr(elem);
            if (mgr == null) return null;
            try
            {
                object value = mgr.GetValueByName(name);
                return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("sheet-size: 读取属性「" + name + "」失败：" + ex.Message);
                return null;
            }
        }

        private static void WriteProp(IZElement elem, string name, string value, Action<string> log)
        {
            IZCustomPropMgr mgr = GetSharedPropMgr(elem);
            if (mgr == null) throw new InvalidOperationException("取不到共享自定义属性管理器（三候选均无 IsSharedMgr=true）。");

            bool ok = false;
            try { mgr.SetValueByName(name, value ?? ""); ok = true; }
            catch (Exception ex) { Diagnostics.Log("sheet-size: SetValueByName(" + name + ") EX " + ex.Message); }
            if (!ok)
            {
                // 属性名还不存在时 SetValueByName 会失败 → AddCustomProp（BatchMaster CaxaPropWrite 槽14 回退先例）
                try
                {
                    mgr.AddCustomProp(name, value ?? "", true);   // vbReplaceExistingOne=true
                    log("SetValueByName 失败，已走 AddCustomProp 建立「" + name + "」");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("写入「" + name + "」失败：SetValueByName 与 AddCustomProp 均未成功（" + ex.Message + "）");
                }
            }
            log("已回填 [" + name + "] = " + value);

            try
            {
                object back = mgr.GetValueByName(name);
                string backText = back == null ? "" : Convert.ToString(back, CultureInfo.InvariantCulture);
                if (!string.Equals(backText, value, StringComparison.Ordinal))
                    log("警告：[" + name + "] 回读不一致：\"" + backText + "\"");
            }
            catch (Exception ex) { Diagnostics.Log("sheet-size: 回读属性「" + name + "」失败：" + ex.Message); }
        }

        // 配置切换可能重建元素状态：回读不一致就补写一次
        private static void RecheckAndFixProp(IZElement elem, string name, string value, Action<string> log)
        {
            if (elem == null) return;
            try
            {
                string back = ReadCustomProp(elem, name);
                if (string.Equals(back, value, StringComparison.Ordinal)) return;
                log("恢复默认环境后 [" + name + "] 不一致（\"" + (back ?? "null") + "\"），重新写入。");
                WriteProp(elem, name, value, log);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("sheet-size: 复核属性「" + name + "」失败：" + ex.Message);
                log("复核属性 [" + name + "] 失败：" + ex.Message);
            }
        }

        // ================================================================ 默认配置

        private static bool RestoreDefaultConfiguration(IZSceneDoc scene, IZSheetMetalPart sm,
            bool unfolded, Action<string> log)
        {
            string defaultName = "";

            if (unfolded && sm != null)
            {
                try { sm.Refold(); log("已调用 Refold() 回到折叠态。"); }
                catch (Exception ex) { log("Refold 失败：" + ex.Message); }
            }

            IZConfigurationMgr mgr = null;
            try { mgr = scene.ConfigurationMgr as IZConfigurationMgr; }
            catch (Exception ex) { Diagnostics.Log("sheet-size: 取 ConfigurationMgr 失败：" + ex.Message); }
            if (mgr == null) { log("取不到 ConfigurationMgr，无法恢复默认设计环境。"); return false; }

            IZConfiguration def = null;
            try { def = mgr.DefaultConfiguration as IZConfiguration; }
            catch (Exception ex) { Diagnostics.Log("sheet-size: 取 DefaultConfiguration 失败：" + ex.Message); }
            if (def == null) { log("默认配置对象为空，无法恢复默认设计环境。"); return false; }

            try { defaultName = def.Name ?? ""; } catch { defaultName = ""; }

            try
            {
                def.Activate(true);
                log("钣金件设计环境已设为默认配置「" + defaultName + "」。");
            }
            catch (Exception ex)
            {
                log("激活默认配置失败：" + ex.Message);
                return false;
            }

            try
            {
                IZConfiguration active = mgr.ActiveConfiguration as IZConfiguration;
                string activeName = active != null ? (active.Name ?? "") : "";
                log("当前活动配置：" + activeName);
                return string.Equals(activeName, defaultName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("sheet-size: 校验活动配置失败：" + ex.Message);
                return true;
            }
        }

        // ================================================================ 数值格式

        /// <summary>mm、1 位小数、四舍五入、末尾消零（123.0 → 123，123.35 → 123.4）。（diffrename SheetSizeTool.FormatMm 原样）</summary>
        public static string FormatMm(double mm)
        {
            double r = Math.Round(mm, 1, MidpointRounding.AwayFromZero);
            if (Math.Abs(r) < 0.05) r = 0;
            return r.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
