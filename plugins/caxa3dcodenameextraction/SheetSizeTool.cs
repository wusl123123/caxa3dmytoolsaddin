using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 自定义属性回填规则（来源：caxa3dsheetsizewriteaddfix.txt）
    /// 只取前两项：SC规格（长x宽） / SC材料（&lt;材料&gt;板T=厚）。
    /// </summary>
    internal sealed class SheetSizeRule
    {
        public string SpecField = "SC规格";
        public string MaterialField = "SC材料";
        public string SourcePath = "(内置默认)";

        public static SheetSizeRule Load()
        {
            SheetSizeRule cfg = new SheetSizeRule();
            foreach (string path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    List<string> items = new List<string>();
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int first = line.IndexOf('"');
                        int last = line.LastIndexOf('"');
                        if (first >= 0 && last > first) line = line.Substring(first + 1, last - first - 1);
                        line = line.Trim();
                        if (line.Length > 0) items.Add(line);
                    }
                    if (items.Count >= 2)
                    {
                        cfg.SpecField = items[0];
                        cfg.MaterialField = items[1];
                        cfg.SourcePath = path;
                    }
                    break;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("读取回填规则失败 " + path + "：" + ex.Message);
                }
            }
            return cfg;
        }

        private static IEnumerable<string> CandidatePaths()
        {
            List<string> list = new List<string>();
            try { list.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "caxa3dsheetsizewriteaddfix.txt")); } catch { }
            try
            {
                string asmDir = Path.GetDirectoryName(typeof(SheetSizeRule).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                    list.Add(Path.Combine(asmDir, "caxa3dsheetsizewriteaddfix.txt"));
            }
            catch { }
            try { list.Add(Path.Combine(Directory.GetCurrentDirectory(), "caxa3dsheetsizewriteaddfix.txt")); } catch { }
            return list;
        }
    }

    internal sealed class SheetSizeValidateResult
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

    internal sealed class SheetSizeExecuteResult
    {
        public bool Ok;
        public string Message = "";

        public string DocName = "";
        public string PartName = "";
        public string FlatPartName = "";
        public string Spec = "";
        public string Material = "";
        public double ThicknessMm;
        public string MaterialSpec = "";
        public string BoxSource = "";
        public double LengthMm;
        public double WidthMm;
        public double ThicknessFromBoxMm;

        public bool Unfolded;
        public bool ConfigRestored;
        public string DefaultConfigName = "";
        public bool Saved;
        public bool MaterialFallback;
    }

    /// <summary>
    /// 钣金件尺寸填写核心（迁自独立工具 caxa3dsheetsizewrite）：
    /// 选中「文件名.ics---零件名（虚拟）」的钣金件 → 找展开件 → 读最小包围盒两最大尺寸
    /// → 与板厚/材料组合 → 回填自定义属性 → 恢复钣金默认设计环境 → 保存。
    /// 所有长度从 API 的「米」换算为 mm，按 1 位小数四舍五入并消零。
    /// </summary>
    internal static class SheetSizeTool
    {
        private const int ElemTypeSheetMetalPart = 20;

        // ================================================================ 校验

        public static SheetSizeValidateResult Validate(IZBaseApp app)
        {
            SheetSizeValidateResult r = new SheetSizeValidateResult();
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
            if (r.DocBaseName.Length == 0)
            {
                r.Message = "当前文档没有有效的 .ics 文件名。";
                return r;
            }

            string selError;
            List<IZElement> selection = SizeBoxTool.GetSelection(app, out selError);
            if (selection.Count == 0)
            {
                r.Message = "请先在 3D 设计树中选中钣金件（文件名.ics---零件名）。";
                return r;
            }
            if (selection.Count > 1)
            {
                r.Message = "一次只能选中一个钣金件，请取消其余选中项。";
                return r;
            }

            IZElement elem = selection[0];
            r.Element = elem;

            try { r.ElementType = (int)elem.Type; }
            catch (Exception ex) { r.Message = "读取元素类型失败：" + ex.Message; return r; }

            if (r.ElementType != ElemTypeSheetMetalPart)
            {
                r.Message = "当前选中的不是钣金件（元素类型 " + r.ElementType + "）。";
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
        public static string DocBaseName(string docName)
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

        public static SheetSizeExecuteResult Execute(IZBaseApp app, SheetSizeRule rule,
            Action<string> log, Func<bool> shouldStop)
        {
            if (log == null) log = delegate { };
            if (shouldStop == null) shouldStop = delegate { return false; };
            if (rule == null) rule = SheetSizeRule.Load();

            SheetSizeExecuteResult res = new SheetSizeExecuteResult();
            SheetSizeValidateResult v = Validate(app);
            if (!v.Ok) throw new InvalidOperationException(v.Message);

            res.DocName = v.DocName;
            res.PartName = v.PartName;
            log("文档：" + v.DocName);
            log("零件：" + v.PartName + "（虚拟，钣金件）");
            log("规则：" + rule.SourcePath + " → [" + rule.SpecField + "] / [" + rule.MaterialField + "]");

            CheckStop(shouldStop);

            // ---- 1) 材料：取零件的材料名称属性
            string material = "";
            try
            {
                string matName;
                v.Element.GetMaterialNameProperty(out matName);
                if (!string.IsNullOrWhiteSpace(matName)) material = matName.Trim();
            }
            catch (Exception ex) { Diagnostics.Log("GetMaterialNameProperty 失败：" + ex.Message); }
            if (material.Length == 0)
            {
                res.MaterialFallback = true;
                log("警告：未读取到材料名称，SC材料将只填写板厚。");
            }
            res.Material = material;
            log("材料：" + (material.Length == 0 ? "（空）" : material));

            // ---- 2) 板厚：取板料属性（mm）
            string thickDetail;
            res.ThicknessMm = ReadThicknessMm(v.SheetMetal, out thickDetail);
            log("板厚：" + FormatMm(res.ThicknessMm) + " mm（" + thickDetail + "）");
            if (res.ThicknessMm <= 0)
                throw new InvalidOperationException("读取板厚失败（板料板厚为 0）。");

            CheckStop(shouldStop);

            // ---- 3) 找展开件（名为「展开<零件名>」），必要时 unfold 取得
            bool unfolded;
            IZElement flat = FindFlatPart(v, log, shouldStop, out unfolded);
            res.Unfolded = unfolded;
            res.FlatPartName = SafeName(flat);
            log("展开件：" + (res.FlatPartName.Length == 0 ? "（未命名）" : res.FlatPartName));

            CheckStop(shouldStop);

            // ---- 4) 展开件最小包围盒 → 两最大尺寸（长、宽）
            SizeBoxTool.SizeInfo box = SizeBoxTool.ReadOne(app, flat);
            if (!box.HasBox)
                throw new InvalidOperationException("读取展开件包围盒失败：" +
                    (string.IsNullOrEmpty(box.Note) ? "展开件没有实体几何" : box.Note));

            double x = box.HasTight ? box.TightX : box.SizeX;
            double y = box.HasTight ? box.TightY : box.SizeY;
            double z = box.HasTight ? box.TightZ : box.SizeZ;

            double[] dimsMm = { Math.Abs(x) * 1000.0, Math.Abs(y) * 1000.0, Math.Abs(z) * 1000.0 };
            Array.Sort(dimsMm);   // 升序：dimsMm[2]=长（大）、dimsMm[1]=宽（次大）、dimsMm[0]=厚（最小）
            res.LengthMm = dimsMm[2];
            res.WidthMm = dimsMm[1];
            res.ThicknessFromBoxMm = dimsMm[0];
            res.BoxSource = box.Source;

            // 板厚必须与包围盒最小边一致，否则说明展开件方向/选取有误 → 中止，不写属性
            const double ThicknessTolMm = 0.1;
            if (Math.Abs(res.ThicknessFromBoxMm - res.ThicknessMm) > ThicknessTolMm)
                throw new InvalidOperationException(string.Format(
                    "包围盒最小尺寸 {0} mm 与板厚 {1} mm 不一致（容差 {2} mm），无法确认展开件方向，已中止（未写入属性）。",
                    FormatMm(res.ThicknessFromBoxMm), FormatMm(res.ThicknessMm), ThicknessTolMm));

            res.Spec = FormatMm(res.LengthMm) + "x" + FormatMm(res.WidthMm);
            res.MaterialSpec = (material.Length == 0 ? "" : material) + "板T=" + FormatMm(res.ThicknessMm);

            log(string.Format("最小包围盒：X={0} Y={1} Z={2} mm（{3}）",
                FormatMm(dimsMm[0]), FormatMm(dimsMm[1]), FormatMm(dimsMm[2]),
                box.HasTight ? "紧致盒" : "整体盒"));
            log("长宽判定：长=" + FormatMm(res.LengthMm) + "（最大边）　宽=" + FormatMm(res.WidthMm) + "（次大边）　厚=" + FormatMm(res.ThicknessFromBoxMm));
            log("板厚校验：包围盒最小边 " + FormatMm(res.ThicknessFromBoxMm) + " mm = 板厚 " + FormatMm(res.ThicknessMm) + " mm，通过。");
            log("SC规格 = " + res.Spec);
            log("SC材料 = " + res.MaterialSpec);

            CheckStop(shouldStop);

            // ---- 5) 回填自定义属性（选中钣金件的共享属性管理器）
            WriteProp(v.Element, rule.SpecField, res.Spec, log);
            WriteProp(v.Element, rule.MaterialField, res.MaterialSpec, log);

            CheckStop(shouldStop);

            // ---- 6) 钣金件设计环境恢复默认（折叠配置）
            res.ConfigRestored = RestoreDefaultConfiguration(v.Scene, v.SheetMetal, unfolded, log,
                out res.DefaultConfigName);

            // ---- 6.5) 配置切换可能重建元素状态，复核属性并按需补写
            RecheckAndFixProp(v.Element, rule.SpecField, res.Spec, log);
            RecheckAndFixProp(v.Element, rule.MaterialField, res.MaterialSpec, log);

            // ---- 7) 保存文档
            v.Doc.Save();
            FileInfo fi = new FileInfo(v.DocName);
            res.Saved = fi.Exists && fi.Length > 0;
            log("已保存：" + (res.Saved ? v.DocName + "（" + fi.Length + "B）" : "文件不存在或为空"));

            res.Ok = true;
            res.Message = "完成：" + res.Spec + "  |  " + res.MaterialSpec;
            return res;
        }

        private static void CheckStop(Func<bool> shouldStop)
        {
            if (shouldStop()) throw new OperationStoppedException();
            System.Windows.Forms.Application.DoEvents();
        }

        /// <summary>带停止检查的短等待（100ms 轮询 + 泵消息），不依赖 CaxaAutomation 的静态停止标志。</summary>
        private static bool WaitFor(Func<bool> condition, int timeoutMs, Func<bool> shouldStop)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (shouldStop()) throw new OperationStoppedException();
                try { System.Windows.Forms.Application.DoEvents(); } catch { }
                try { if (condition()) return true; } catch { }
                System.Threading.Thread.Sleep(100);
            }
            return false;
        }

        // ================================================================ 展开件

        private static IZElement FindFlatPart(SheetSizeValidateResult v, Action<string> log,
            Func<bool> shouldStop, out bool unfolded)
        {
            unfolded = false;
            string expected = "展开" + v.PartName;

            // 1) 设计树中按「展开<零件名>」精确查找
            IZElement found = FindByName(v.Scene, expected, shouldStop);
            if (found != null)
            {
                log("在设计树中找到展开件「" + expected + "」");
                return found;
            }

            // 2) IZSheetMetalPart.GetFlatPart
            IZElement flat = v.SheetMetal.GetFlatPart();
            if (flat != null)
            {
                log("GetFlatPart 返回展开件「" + SafeName(flat) + "」");
                return flat;
            }

            // 3) 兜底：Unfold 后再取（参考 caxa3dcodenameextraction 的展开流程）
            log("未找到展开件，调用 Unfold() 展开…");
            try { v.SheetMetal.Unfold(); }
            catch (Exception ex) { log("Unfold 失败：" + ex.Message); }
            unfolded = true;

            WaitFor(delegate
            {
                try { flat = v.SheetMetal.GetFlatPart(); }
                catch { flat = null; }
                return flat != null;
            }, 5000, shouldStop);

            if (flat == null)
                throw new InvalidOperationException("找不到展开件：设计树无「" + expected + "」，GetFlatPart 也为空。");
            log("Unfold 后取得展开件「" + SafeName(flat) + "」");
            return flat;
        }

        private static IZElement FindByName(IZSceneDoc scene, string name, Func<bool> shouldStop)
        {
            IZElement top = null;
            try { top = scene.GetTopElement(); } catch { }
            if (top == null) return null;

            Stack<KeyValuePair<IZElement, int>> stack = new Stack<KeyValuePair<IZElement, int>>();
            stack.Push(new KeyValuePair<IZElement, int>(top, 0));
            int visited = 0;

            while (stack.Count > 0)
            {
                if (shouldStop()) throw new OperationStoppedException();
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

        // ================================================================ 自定义属性

        // 共享表铁律（规则 14.7/14.9）：只认 IsSharedMgr()==true 的管理器；三个候选都没确认
        // 就返回 null 让调用方判失败——绝不把零件级表当兜底，也不按属性名猜表。
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
            catch (Exception ex) { Diagnostics.Log("get_CustomPropMgr 失败：" + ex.Message); }

            Diagnostics.Log("GetSharedPropMgr: 三个候选都没有 IsSharedMgr=true，" +
                "按共享表铁律本次不读写任何其它表");
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
                Diagnostics.Log("读取属性「" + name + "」失败：" + ex.Message);
                return null;
            }
        }

        private static void WriteProp(IZElement elem, string name, string value, Action<string> log)
        {
            if (string.IsNullOrEmpty(name)) return;
            IZCustomPropMgr mgr = GetSharedPropMgr(elem);
            if (mgr == null) throw new InvalidOperationException("取不到共享自定义属性管理器。");

            mgr.SetValueByName(name, value ?? "");
            log("已回填 [" + name + "] = " + value);

            try
            {
                object back = mgr.GetValueByName(name);
                string backText = back == null ? "" : Convert.ToString(back, CultureInfo.InvariantCulture);
                if (!string.Equals(backText, value, StringComparison.Ordinal))
                    log("警告：[" + name + "] 回读不一致：\"" + backText + "\"");
            }
            catch (Exception ex) { Diagnostics.Log("回读属性「" + name + "」失败：" + ex.Message); }
        }

        // 配置切换可能重建元素状态：回读不一致就补写一次
        private static void RecheckAndFixProp(IZElement elem, string name, string value, Action<string> log)
        {
            if (string.IsNullOrEmpty(name)) return;
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
                Diagnostics.Log("复核属性「" + name + "」失败：" + ex.Message);
                log("复核属性 [" + name + "] 失败：" + ex.Message);
            }
        }

        // ================================================================ 默认配置
        private static bool RestoreDefaultConfiguration(IZSceneDoc scene, IZSheetMetalPart sm,
            bool unfolded, Action<string> log, out string defaultName)
        {
            defaultName = "";

            if (unfolded && sm != null)
            {
                try { sm.Refold(); log("已调用 Refold() 回到折叠态。"); }
                catch (Exception ex) { log("Refold 失败：" + ex.Message); }
            }

            IZConfigurationMgr mgr = null;
            try { mgr = scene.ConfigurationMgr as IZConfigurationMgr; }
            catch (Exception ex) { Diagnostics.Log("取 ConfigurationMgr 失败：" + ex.Message); }
            if (mgr == null)
            {
                log("取不到 ConfigurationMgr，无法恢复默认设计环境。");
                return false;
            }

            IZConfiguration def = null;
            try { def = mgr.DefaultConfiguration as IZConfiguration; }
            catch (Exception ex) { Diagnostics.Log("取 DefaultConfiguration 失败：" + ex.Message); }
            if (def == null)
            {
                log("默认配置对象为空，无法恢复默认设计环境。");
                return false;
            }

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
                Diagnostics.Log("校验活动配置失败：" + ex.Message);
                return true;
            }
        }

        // ================================================================ 数值格式

        /// <summary>mm、1 位小数、四舍五入、末尾消零（123.0 → 123，123.35 → 123.4）。</summary>
        public static string FormatMm(double mm)
        {
            double r = Math.Round(mm, 1, MidpointRounding.AwayFromZero);
            if (Math.Abs(r) < 0.05) r = 0;
            return r.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
