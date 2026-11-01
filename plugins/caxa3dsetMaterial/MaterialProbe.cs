using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dsetMaterial
{
    /// <summary>
    /// 材料注入探针：读取 / 写入选中元素的材料名与密度，并用业务侧副作用做成功判定
    /// （回读名称、回读密度、密度×体积≈计算重量），不看 API 返回值、不看“命令执行过”。
    /// </summary>
    internal static class MaterialProbe
    {
        /// <summary>体积/重量计算精度请求值（文档样例用 0.0001）。</summary>
        private const double Accuracy = 0.0001;

        // ================================================================ 选中元素

        public static IZSceneDoc GetSceneDoc(IZBaseApp app)
        {
            if (app == null) return null;
            IZDoc doc = app.ActiveDoc;
            return doc as IZSceneDoc;
        }

        /// <summary>获取当前选中元素；失败时 error 返回原因。</summary>
        public static List<IZElement> GetSelection(IZBaseApp app, out string error)
        {
            error = null;
            List<IZElement> list = new List<IZElement>();

            IZSceneDoc scene = GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是场景（设计环境）文档，请切换到 3D 场景后再试。";
                return list;
            }

            IZSelectionMgr selMgr = scene.SelectionMgr;
            if (selMgr == null)
            {
                error = "取不到 SelectionMgr。";
                return list;
            }

            ZArray arr = selMgr.GetSelectedElementsZArray();
            if (arr == null)
            {
                error = "取不到选中元素列表（GetSelectedElementsZArray 返回空）。";
                return list;
            }

            int count;
            arr.Count(out count);
            for (int i = 0; i < count; i++)
            {
                object raw;
                arr.Get(i, out raw);
                IZElement elem = raw as IZElement;
                if (elem != null) list.Add(elem);
            }

            if (list.Count == 0)
                error = "当前没有选中任何元素，请先在 CAXA 里选中零件（含虚拟件）。";
            return list;
        }

        // ================================================================ 设计树遍历

        /// <summary>
        /// 从当前设计树（IZSceneDoc）根开始穿透所有装配体层级，
        /// 返回所有叶子零件（非装配体）。不需要用户选中任何元素。
        /// </summary>
        public static List<IZElement> GetAllLeafElements(IZBaseApp app, out string error)
        {
            error = null;
            List<IZElement> result = new List<IZElement>();
            IZSceneDoc scene = GetSceneDoc(app);
            if (scene == null) { error = "当前活动文档不是场景（设计环境）文档。"; return result; }

            Diagnostics.Log("开始穿透整个设计树...");
            CollectLeavesFromNode(scene, result, new HashSet<IZElement>());
            Diagnostics.Log("全树穿透结束，叶子零件=" + result.Count);
            if (result.Count == 0) error = "设计树里没有可上色的叶子零件。";
            return result;
        }

        /// <summary>
        /// 从一组选中节点开始穿透：
        ///  - 零件节点 → 直接加入；
        ///  - 装配体节点 → 穿透拿其所有叶子；
        ///  - 自动去重（防止同一零件因多选父装配体而重复）。
        /// </summary>
        public static List<IZElement> GetLeafElementsFromNodes(IEnumerable<IZElement> nodes)
        {
            List<IZElement> result = new List<IZElement>();
            HashSet<IZElement> seen = new HashSet<IZElement>();
            int asmCount = 0, partCount = 0;
            foreach (IZElement node in nodes)
            {
                bool isAsm = IsAssembly(node);
                if (isAsm) asmCount++; else partCount++;
                CollectLeavesFromNode(node, result, seen);
            }
            Diagnostics.Log(string.Format(
                "选中节点穿透：{0} 个装配体 + {1} 个零件 → {2} 个唯一叶子零件",
                asmCount, partCount, result.Count));
            return result;
        }

        /// <summary>判断 IZElement 是否为装配体（type 名含 Assembly 或能转为 IZAssembly）。</summary>
        private static bool IsAssembly(IZElement elem)
        {
            if (elem == null) return false;
            if (elem is IZAssembly || elem is IZAssemblyProperty) return true;
            // 运行时 elem.Type 是 eZElementType 枚举，枚举名含 Assembly 就是装配体系
            try
            {
                string tname = elem.Type.ToString();
                if (tname.IndexOf("Assembly", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 遍历单个节点：
        ///  - IZAssembly / 装配体类型 IZElement → 用 GetAllLeafParts 穿透；
        ///  - 普通 IZElement → 直接加入；
        ///  - IZSceneDoc（根） → 取子元素递归。
        /// seen 用于跨调用去重。
        /// </summary>
        private static void CollectLeavesFromNode(object node, List<IZElement> result, HashSet<IZElement> seen)
        {
            if (node == null) return;

            // ---- 装配体 → 穿透拿叶子 ----
            IZAssembly asm = node as IZAssembly;
            bool handled = false;
            if (asm == null)
            {
                IZElement elem = node as IZElement;
                if (elem != null && IsAssembly(elem))
                    asm = elem as IZAssembly;
            }
            if (asm != null)
            {
                handled = true;
                List<IZElement> leaves = GetAssemblyLeafParts(asm);
                foreach (IZElement l in leaves)
                {
                    if (seen.Add(l)) result.Add(l);
                }
            }

            if (!handled)
            {
                IZElement elem = node as IZElement;
                if (elem != null)
                {
                    // 零件 / 板金 / 面片等叶子类型 → 直接加入
                    if (seen.Add(elem)) result.Add(elem);
                    handled = true;
                }
            }

            // IZSceneDoc（设计树根）→ 取子元素递归
            if (!handled)
            {
                IZSceneDoc scene = node as IZSceneDoc;
                if (scene != null)
                {
                    List<object> children = GetSceneChildren(scene);
                    foreach (object c in children) CollectLeavesFromNode(c, result, seen);
                }
            }
        }

        /// <summary>
        /// 取装配体的所有叶子零件。
        /// 实测 GetAllLeafParts 返回 int[]（元素 Id），无 managed 映射，故直接用 GetChildrenZArray 递归穿透。
        /// </summary>
        private static List<IZElement> GetAssemblyLeafParts(IZAssembly asm)
        {
            List<IZElement> result = RecurseChildren(asm);
            Diagnostics.Trace("GetAssemblyLeafParts: " + result.Count + " 个叶子");
            return result;
        }

        /// <summary>
        /// 递归穿透装配体：IZAssembly/IZElement 都有 GetChildrenZArray()。
        /// 装配体 → 递归子元素；零件 → 直接加入。
        /// 用 ZArray 索引器（Get(i, out var)）解包，不受 SAFEARRAY 元素类型影响。
        /// </summary>
        private static List<IZElement> RecurseChildren(object parent)
        {
            List<IZElement> result = new List<IZElement>();
            ZArray zarr = null;
            try
            {
                if (parent is IZAssembly asm) zarr = asm.GetChildrenZArray();
                else if (parent is IZElement elem) zarr = elem.GetChildrenZArray();
            }
            catch { }

            if (zarr == null) return result;

            int count;
            zarr.Count(out count);
            for (int i = 0; i < count; i++)
            {
                object rawChild;
                zarr.Get(i, out rawChild);
                IZElement child = rawChild as IZElement;
                if (child == null) continue;

                if (IsAssembly(child))
                {
                    List<IZElement> sub = RecurseChildren(child);
                    foreach (IZElement l in sub) result.Add(l);
                }
                else
                {
                    result.Add(child);
                }
            }
            return result;
        }

        /// <summary>IZSceneDoc.GetChildElements() → 返回 ZArray/object[]，解包为子元素列表。</summary>
        private static List<object> GetSceneChildren(IZSceneDoc scene)
        {
            List<object> result = new List<object>();
            try
            {
                object raw = scene.GetChildElements();
                UnpackArray(raw, result);
                Diagnostics.Trace("GetSceneChildren: " + result.Count + " 个子元素");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("GetSceneChildren 异常：" + ex.Message);
            }
            return result;
        }

        /// <summary>把接口返回的 Object（可能是 ZArray / object[] / 单个对象）解包到列表。</summary>
        private static void UnpackArray(object raw, List<object> result)
        {
            if (raw == null) return;
            ZArray zarr = raw as ZArray;
            if (zarr != null)
            {
                int count; zarr.Count(out count);
                for (int i = 0; i < count; i++)
                {
                    object o; zarr.Get(i, out o);
                    if (o != null) result.Add(o);
                }
                return;
            }
            if (raw is object[] arr)
            {
                foreach (object o in arr) if (o != null) result.Add(o);
                return;
            }
            result.Add(raw);
        }

        // ================================================================ 读取

        /// <summary>读取并格式化单个元素的材料状态（多行）。</summary>
        public static string Describe(IZElement elem)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("元素：{0}  类型={1}  Id={2}",
                SafeName(elem), elem.Type, elem.Id);

            string nameProp = null;
            try { elem.GetMaterialNameProperty(out nameProp); }
            catch (Exception ex) { nameProp = "<读取失败: " + ex.Message + ">"; }
            sb.AppendFormat("  GetMaterialNameProperty=\"{0}\"", nameProp);

            // 自定义属性：只读回填规则里列出的字段（严格区分大小写，不兜底查另一张表）
            foreach (string propName in BackfillRule.Fields)
            {
                string v; int which;
                try
                {
                    if (TryGetPropStrict(elem, propName, out v, out which))
                        sb.AppendFormat("  [{0}]=\"{1}\"({2})", propName, v ?? "",
                                        which == 1 ? "共享" : "零件");
                    else
                        sb.AppendFormat("  [{0}]=<无>", propName);
                }
                catch (Exception ex)
                {
                    sb.AppendFormat("  [{0}]=<读取失败:{1}>", propName, ex.Message);
                }
            }

            // 颜色
            string colorDetail;
            int? color = GetColor(elem, out colorDetail);
            sb.AppendFormat("  颜色={0}（{1}）",
                color.HasValue ? "#" + color.Value.ToString("X6") : "<无>",
                colorDetail ?? "未取到");

            IZPartProperty partProp = elem as IZPartProperty;
            if (partProp != null)
            {
                string matName;
                bool onlyThis;
                try
                {
                    partProp.GetMaterialName(out matName, out onlyThis);
                    sb.AppendFormat("\r\n  零件材料名=\"{0}\"  仅此零件={1}", matName, onlyThis);
                }
                catch (Exception ex)
                {
                    sb.Append("\r\n  零件材料名读取失败: " + ex.Message);
                    return sb.ToString();
                }

                sb.AppendFormat("\r\n  密度={0} kg/m³  质量模式={1}  计算重量={2} kg  指定重量={3} kg",
                    Fmt(partProp.MassDensity), partProp.MassType,
                    Fmt(partProp.CalculatedMass), Fmt(partProp.SpecifiedMass));

                double achieved, vol;
                try
                {
                    vol = partProp.GetVolume(Accuracy, out achieved);
                    sb.AppendFormat("\r\n  体积={0} m³（实际精度 {1}）  密度×体积={2} kg",
                        Fmt(vol), Fmt(achieved), Fmt(vol * partProp.MassDensity));
                }
                catch (Exception ex)
                {
                    sb.Append("\r\n  体积读取失败: " + ex.Message);
                }
                return sb.ToString();
            }

            IZAssemblyProperty asmProp = elem as IZAssemblyProperty;
            if (asmProp != null)
            {
                string matName;
                bool onlyThis;
                try
                {
                    asmProp.GetMaterialName(out matName, out onlyThis);
                    sb.AppendFormat("\r\n  装配材料名=\"{0}\"  仅此零件={1}", matName, onlyThis);
                }
                catch (Exception ex)
                {
                    sb.Append("\r\n  装配材料名读取失败: " + ex.Message);
                    return sb.ToString();
                }

                double achieved, vol;
                try
                {
                    vol = asmProp.GetVolume(Accuracy, out achieved);
                    sb.AppendFormat("\r\n  体积={0} m³  计算重量={1} kg",
                        Fmt(vol), Fmt(asmProp.CalculatedMass));
                }
                catch (Exception ex)
                {
                    sb.Append("\r\n  体积读取失败: " + ex.Message);
                }
                return sb.ToString();
            }

            sb.Append("\r\n  该元素不支持 IZPartProperty / IZAssemblyProperty（无法注入材料）");
            return sb.ToString();
        }

        // ================================================================ 写入 + 校验

        /// <summary>
        /// 对单个元素写入材料名、密度、颜色（null 表示该保持原样），随后回读校验。
        /// 材料名按回填规则文件里列出的字段逐个写入（严格匹配，不兜底、不组合）。
        /// 返回 true 表示业务侧校验通过。
        /// </summary>
        public static bool Apply(IZElement elem, string materialName, double? density,
                                 int? colorRgb, bool onlyThisPart, StringBuilder report)
        {
            string tag = "[" + SafeName(elem) + "]";
            report.AppendLine("---- " + tag + " 写入前 ----");
            report.AppendLine(Describe(elem));

            IZPartProperty partProp = elem as IZPartProperty;
            IZAssemblyProperty asmProp = null;
            if (partProp == null) asmProp = elem as IZAssemblyProperty;

            if (partProp == null && asmProp == null)
            {
                report.AppendLine(tag + " 跳过：该元素不支持材料属性接口。");
                return false;
            }

            // 写入名称（密度必须在名称之后写：命中材料库时名称会带出库内密度）
            try
            {
                if (partProp != null) partProp.SetMaterialName(materialName, onlyThisPart);
                else asmProp.SetMaterialName(materialName, onlyThisPart);
                Diagnostics.Trace(tag + " SetMaterialName(\"" + materialName + "\", " + onlyThisPart + ")");
            }
            catch (Exception ex)
            {
                report.AppendLine(tag + " SetMaterialName 抛出异常：" + ex.Message);
                Diagnostics.Log(tag + " SetMaterialName 异常: " + ex);
                return false;
            }

            if (density.HasValue && partProp != null)
            {
                try
                {
                    partProp.MassDensity = density.Value;
                    Diagnostics.Trace(tag + " MassDensity=" + density.Value);
                }
                catch (Exception ex)
                {
                    report.AppendLine(tag + " MassDensity 抛出异常：" + ex.Message);
                    Diagnostics.Log(tag + " MassDensity 异常: " + ex);
                }
            }

            // 自定义属性"材料"/"SC材料"
            WriteMaterialProps(elem, materialName, report);

            // 颜色
            if (colorRgb.HasValue)
                ApplyColor(elem, colorRgb.Value, report);

            // 让场景刷新（浏览器/图形），不依赖 API 返回值
            try
            {
                IZSceneDoc scene = GetSceneDoc(elem.Application);
                if (scene != null) scene.Update();
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("scene.Update 跳过: " + ex.Message);
            }

            report.AppendLine();
            report.AppendLine("---- " + tag + " 写入后（回读） ----");
            report.AppendLine(Describe(elem));

            // ---- 业务侧判据 ----
            bool ok = true;
            string backName;
            bool backOnly;
            try
            {
                if (partProp != null) partProp.GetMaterialName(out backName, out backOnly);
                else asmProp.GetMaterialName(out backName, out backOnly);

                bool nameOk = string.Equals((backName ?? "").Trim(), materialName.Trim(),
                                            StringComparison.OrdinalIgnoreCase);
                bool flagOk = backOnly == onlyThisPart;
                report.AppendLine(string.Format("{0} 校验-材料名: 回读=\"{1}\" 期望=\"{2}\" => {3}",
                    tag, backName, materialName, nameOk ? "通过" : "不通过"));
                report.AppendLine(string.Format("{0} 校验-仅此零件: 回读={1} 期望={2} => {3}",
                    tag, backOnly, onlyThisPart, flagOk ? "通过" : "不通过"));
                ok &= nameOk && flagOk;
            }
            catch (Exception ex)
            {
                report.AppendLine(tag + " 校验-材料名读取异常：" + ex.Message);
                ok = false;
            }

            if (partProp != null && density.HasValue)
            {
                double backDensity = partProp.MassDensity;
                bool densOk = Math.Abs(backDensity - density.Value) <= Math.Max(1e-9, Math.Abs(density.Value) * 1e-6);
                report.AppendLine(string.Format("{0} 校验-密度: 回读={1} 期望={2} => {3}",
                    tag, Fmt(backDensity), Fmt(density.Value), densOk ? "通过" : "不通过"));
                ok &= densOk;

                if (densOk)
                {
                    try
                    {
                        double achieved, vol;
                        vol = partProp.GetVolume(Accuracy, out achieved);
                        double expectMass = vol * backDensity;
                        double calcMass = partProp.CalculatedMass;
                        bool massOk = Math.Abs(expectMass - calcMass) <=
                                      Math.Max(1e-9, Math.Abs(expectMass) * 0.01);
                        report.AppendLine(string.Format(
                            "{0} 校验-重量: 密度×体积={1} kg  CalculatedMass={2} kg => {3}",
                            tag, Fmt(expectMass), Fmt(calcMass), massOk ? "通过" : "不通过（质量模式可能为“指定重量”）"));
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine(tag + " 校验-重量计算异常：" + ex.Message);
                    }
                }
            }

            // ---- 自定义属性校验（只校验回填规则列出的字段，严格区分大小写） ----
            foreach (string propName in BackfillRule.Fields)
            {
                try
                {
                    string v; int which;
                    bool found = TryGetPropStrict(elem, propName, out v, out which);
                    bool propOk = found && string.Equals((v ?? "").Trim(), materialName.Trim(),
                                                        StringComparison.Ordinal);
                    report.AppendLine(string.Format("{0} 校验-[{1}]: 回读={2}{3} 期望=\"{4}\" => {5}",
                        tag, propName,
                        found ? "\"" + v + "\"" : "<无>",
                        found ? "(" + (which == 1 ? "共享表" : "零件表") + ")" : "",
                        materialName,
                        propOk ? "通过" : "不通过"));
                    ok &= propOk;
                }
                catch (Exception ex)
                {
                    report.AppendLine(tag + " 校验-[" + propName + "]异常：" + ex.Message);
                    ok = false;
                }
            }

            // ---- 颜色校验 ----
            if (colorRgb.HasValue)
            {
                int? back = GetColor(elem);
                bool colorOk = back.HasValue && (back.Value & 0xFFFFFF) == (colorRgb.Value & 0xFFFFFF);
                report.AppendLine(string.Format("{0} 校验-颜色: 回读={1} 期望=#{2} => {3}",
                    tag,
                    back.HasValue ? "#" + back.Value.ToString("X6") : "<无>",
                    (colorRgb.Value & 0xFFFFFF).ToString("X6"),
                    colorOk ? "通过" : "不通过"));
                ok &= colorOk;
            }

            return ok;
        }

        // ================================================================ 自定义属性 / 颜色

        /// <summary>
        /// 取元素的自定义属性管理器。0=零件级，1=共享（文档级，SC* 系列属性在此）。
        /// 必须走 GetCustomPropManager(selector)：本机 get_CustomPropMgr 的封送描述有误（RPC_E_FAULT）。
        /// </summary>
        public static IZCustomPropMgr GetPropMgr(IZElement elem, int shared)
        {
            try
            {
                return elem.GetCustomPropManager(shared) as IZCustomPropMgr;
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("GetCustomPropManager(" + shared + ") 失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 按 IsSharedMgr() 动态定位共享表（参照 caxa3dAttributeManageraddin：
        /// selector 依次试 0、1，取第一个自报为共享的管理器）。
        /// </summary>
        public static IZCustomPropMgr GetSharedPropMgr(IZElement elem, out int selector)
        {
            IZCustomPropMgr fallback = null;
            foreach (int s in new[] { 0, 1 })
            {
                IZCustomPropMgr mgr = GetPropMgr(elem, s);
                if (mgr == null) continue;
                try
                {
                    if (mgr.IsSharedMgr())
                    {
                        selector = s;
                        return mgr;
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Trace("IsSharedMgr(selector=" + s + ") 异常: " + ex.Message);
                }
                if (fallback == null) fallback = mgr;
            }
            selector = 1;
            if (fallback != null)
                Diagnostics.Trace("GetSharedPropMgr: 未确认共享表，回退首个管理器");
            return fallback;
        }

        /// <summary>
        /// 写单个属性：优先 SetValueByName（覆盖已有值），失败再 AddCustomProp 新建。
        /// 与参考插件一致：CAXA 模板里已定义的属性走 SetValueByName，未定义才新建。
        /// </summary>
        private static bool WriteProp(IZCustomPropMgr mgr, string propName, string value, string tableName,
                                      StringBuilder report, string tag)
        {
            try
            {
                mgr.SetValueByName(propName, value);
                Diagnostics.Trace(tag + " 自定义属性[" + propName + "]='" + value + "' 已写入（" + tableName + "）");
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Trace(tag + " SetValueByName[" + propName + "] 失败: " + ex.Message);
            }

            try
            {
                mgr.AddCustomProp(propName, value, true);
                Diagnostics.Trace(tag + " 自定义属性[" + propName + "]='" + value + "' 已新建（" + tableName + "）");
                return true;
            }
            catch (Exception ex)
            {
                report.AppendLine(tag + " 写自定义属性[" + propName + "]（" + tableName + "）异常：" + ex.Message);
                Diagnostics.Log(tag + " 写自定义属性[" + propName + "]异常: " + ex);
                return false;
            }
        }

        /// <summary>
        /// 按回填规则写入材料名：所有字段统一写入共享表，链接实例共享同一份属性；
        /// 严格区分大小写，不组合新字段。
        /// </summary>
        private static void WriteMaterialProps(IZElement elem, string materialName, StringBuilder report)
        {
            string tag = "[" + SafeName(elem) + "]";

            IList<string> fields = BackfillRule.Fields;
            if (fields.Count == 0)
            {
                report.AppendLine(tag + " 回填规则为空：按规则不兜底，跳过自定义属性写入。");
                return;
            }

            int sel;
            IZCustomPropMgr shared = GetSharedPropMgr(elem, out sel);
            if (shared == null)
            {
                report.AppendLine(tag + " 自定义属性写入失败：取不到共享表。");
                return;
            }

            foreach (string propName in fields)
            {
                if (!WriteProp(shared, propName, materialName, "共享表", report, tag))
                    report.AppendLine(tag + " 自定义属性[" + propName + "] 未成功写入。");
            }
        }

        /// <summary>
        /// 读属性（严格模式）：只读共享表，不查零件表兜底；shared 恒为 1。
        /// </summary>
        public static bool TryGetPropStrict(IZElement elem, string propName, out string value, out int shared)
        {
            value = null;
            shared = -1;
            int sel;
            IZCustomPropMgr mgr = GetSharedPropMgr(elem, out sel);
            if (mgr == null) return false;

            try
            {
                string v; bool found;
                mgr.GetCustomPropAsString(propName, out v, out found);
                if (!found) return false;
                value = v;
                shared = 1;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ================================================================ 颜色

        // 实测（2026-10-28，三组对照）：IZSurfaceFinish.Color 用的是 COLORREF，
        // 即 0xBBGGRR。写 0xFF0000 显示蓝色、写 0x0000FF 显示红色、写 0xFFFF00 显示青色。
        /// <summary>UI 的 0xRRGGBB → API 的 COLORREF 0xBBGGRR。</summary>
        public static int RgbToApi(int rgb)
        {
            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            return (b << 16) | (g << 8) | r;
        }

        /// <summary>API 的 COLORREF 0xBBGGRR → UI 的 0xRRGGBB。</summary>
        public static int ApiToRgb(int api)
        {
            int b = (api >> 16) & 0xFF, g = (api >> 8) & 0xFF, r = api & 0xFF;
            return (r << 16) | (g << 8) | b;
        }

        /// <summary>读取零件外观颜色（0xRRGGBB）；不支持时返回 null。</summary>
        public static int? GetColor(IZElement elem)
        {
            string detail;
            return GetColor(elem, out detail);
        }

        /// <summary>
        /// 读取零件外观颜色（0xRRGGBB），detail 回传是哪条路径取值/失败原因（接日志用）。
        /// IZPart.SurfaceFinish / IZFacetPart.SurfaceFinish 均返回 ZSurfaceFinish（IZSurfaceFinish 的 coclass）。
        /// </summary>
        public static int? GetColor(IZElement elem, out string detail)
        {
            detail = null;
            try
            {
                object sf = null;
                string src = null;

                IZPart part = elem as IZPart;
                if (part != null)
                {
                    sf = part.SurfaceFinish;
                    src = "IZPart";
                }
                else
                {
                    IZFacetPart facet = elem as IZFacetPart;
                    if (facet != null)
                    {
                        sf = facet.SurfaceFinish;
                        src = "IZFacetPart";
                    }
                }

                if (sf == null)
                {
                    detail = "元素(类型=" + elem.Type + ")不支持外观接口，无颜色可读";
                    Diagnostics.Trace("读颜色失败：元素 类型=" + elem.Type + " 既非 IZPart 也非 IZFacetPart");
                    return null;
                }

                IZSurfaceFinish finish = sf as IZSurfaceFinish;
                if (finish == null)
                {
                    detail = src + ".SurfaceFinish 无法转为 IZSurfaceFinish（运行时类型=" + sf.GetType().Name + "）";
                    Diagnostics.Trace("读颜色失败：" + detail);
                    return null;
                }

                int raw = finish.Color;
                detail = src + " 原始=0x" + raw.ToString("X8");
                return ApiToRgb(raw);
            }
            catch (Exception ex)
            {
                detail = "读颜色异常：" + ex.Message;
                Diagnostics.Trace("读颜色失败: " + ex);
                return null;
            }
        }

        /// <summary>设置零件外观颜色（入参 0xRRGGBB），并回读校验由调用方完成。</summary>
        public static void ApplyColor(IZElement elem, int rgb, StringBuilder report)
        {
            string tag = "[" + SafeName(elem) + "]";
            int api = RgbToApi(rgb);
            try
            {
                IZPart part = elem as IZPart;
                if (part != null)
                {
                    part.SurfaceFinish.Color = api;
                    part.Update();
                    return;
                }

                IZFacetPart facet = elem as IZFacetPart;
                if (facet != null)
                {
                    IZSurfaceFinish sf = facet.SurfaceFinish;
                    sf.Color = api;
                    return;
                }

                if (report != null)
                    report.AppendLine(tag + " 该元素不支持颜色设置（既不是 IZPart 也不是 IZFacetPart）。");
            }
            catch (Exception ex)
            {
                if (report != null) report.AppendLine(tag + " 设置颜色异常：" + ex.Message);
                Diagnostics.Log(tag + " 设置颜色异常: " + ex);
            }
        }

        /// <summary>生成一个柔和但可区分的随机颜色（RGB 0xRRGGBB）：HSV 限定 S/V 区间，避开过白过黑。</summary>
        public static int RandomRgb(Random rnd)
        {
            double h = rnd.NextDouble() * 6.0;          // 0..6
            double s = 0.65 + rnd.NextDouble() * 0.30;  // 0.65..0.95
            double v = 0.60 + rnd.NextDouble() * 0.35;  // 0.60..0.95
            int i = (int)Math.Floor(h);
            double f = h - i;
            double p = v * (1 - s);
            double q = v * (1 - s * f);
            double t = v * (1 - s * (1 - f));
            double r, g, b;
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            int ri = (int)Math.Round(r * 255), gi = (int)Math.Round(g * 255), bi = (int)Math.Round(b * 255);
            return (ri << 16) | (gi << 8) | bi;
        }

        /// <summary>只写颜色并回读校验（不改材料/密度）。</summary>
        public static bool ApplyColorOnly(IZElement elem, int rgb, StringBuilder report)
        {
            string tag = "[" + SafeName(elem) + "]";
            ApplyColor(elem, rgb, report);

            int? back = GetColor(elem);
            bool ok = back.HasValue && (back.Value & 0xFFFFFF) == (rgb & 0xFFFFFF);
            report.AppendLine(string.Format("{0} 颜色: 写入=#{1} 回读={2} => {3}",
                tag, (rgb & 0xFFFFFF).ToString("X6"),
                back.HasValue ? "#" + (back.Value & 0xFFFFFF).ToString("X6") : "<无>",
                ok ? "通过" : "不通过"));
            return ok;
        }

        /// <summary>给单个元素上随机颜色并回读校验，返回是否通过（out 回传实际颜色）。</summary>
        public static bool ApplyRandomColor(IZElement elem, Random rnd, StringBuilder report, out int rgb)
        {
            string tag = "[" + SafeName(elem) + "]";
            rgb = RandomRgb(rnd);
            ApplyColor(elem, rgb, report);

            int? back = GetColor(elem);
            bool ok = back.HasValue && (back.Value & 0xFFFFFF) == (rgb & 0xFFFFFF);
            report.AppendLine(string.Format("{0} 随机颜色: 写入=#{1} 回读={2} => {3}",
                tag, rgb.ToString("X6"),
                back.HasValue ? "#" + back.Value.ToString("X6") : "<无>",
                ok ? "通过" : "不通过"));
            return ok;
        }


        public static string SafeName(IZElement elem)
        {
            try
            {
                string n = elem.Name;
                return string.IsNullOrEmpty(n) ? ("<未命名 Id=" + elem.Id + ">") : n;
            }
            catch
            {
                return "<取名称失败>";
            }
        }

        public static string Fmt(double v)
        {
            return v.ToString("0.################", CultureInfo.InvariantCulture);
        }
    }
}

