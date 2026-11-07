// CAXA 3D 批量大师 —— 进程内服务层（替代 SolidWorksService）
// 负责：连接宿主、活动场景文档、设计树遍历穿透、选中/根元素、自定义属性读写、
//       外部链接文件、同名工程图判定、按路径打开文档、文件改名。
// 读取/遍历走 RCW 强类型（interop 已声明），写入走 vtable 直调（SetValueByName 槽 10，
//   2027 TLB 实测稳定）；所有 COM 调用均异常隔离，单个元素失败不影响整批。
// 能力边界：本类覆盖「编译期已验」的 API 调用；涉及"改名后刷新图框/更新引用"的宿主侧
//   副作用需实机验证（见规则第 35 节 / 坑 149），相关步骤在代码中以【待验】标注。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    /// <summary>单个组件信息（从装配体导入时去重与计数）。</summary>
    public class ComponentInfo
    {
        public string Path;
        public string Name;
        public string Ext;
        public int Count = 1;
    }

    /// <summary>
    /// 一次装配体穿透走查的结果（2026-11-06 新增，属性批量读取/写入改为「设计环境内直接读写」的地基）。
    /// 两件事：① 表格用的去重组件清单 Components（与旧的导入逻辑完全同源同规则）；
    /// ② 元素索引 ElementsByLink＝「外链文件全路径 → 该文件在装配体内的全部实例元素」。
    /// 索引收录**所有**拿到外链路径的元素、不受表格的两个排除开关影响，这样表里已有的行一定能查到对应元素；
    /// 排除开关只决定「这一行是否进表格」，不决定「这个元素能不能被读写」。
    /// </summary>
    internal sealed class AssemblyWalk
    {
        /// <summary>路径键（小写全路径）→ 组件信息，供 Components 与计数使用。</summary>
        public readonly Dictionary<string, ComponentInfo> Map =
            new Dictionary<string, ComponentInfo>(StringComparer.OrdinalIgnoreCase);
        public readonly List<ComponentInfo> Components = new List<ComponentInfo>();
        public readonly Dictionary<string, List<IZElement>> ElementsByLink =
            new Dictionary<string, List<IZElement>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 零件名（无扩展名，大小写不敏感）→ 该名字在装配体里的**全部实例元素**（11-06 用户指令 2：行选中联动）。
        /// 与 ElementsByLink 的两点区别：
        ///   ① 这里**包含无外链的虚拟件/内嵌件**——用户要的是「零件、装配体、钣金等」按名字全选中，不是只选有文件的那批；
        ///   ② 键是名字不是路径，所以同名不同路径的多个文件会并进同一桶（选中联动本来就该按名，不分路径）。
        /// 登记范围＝走查访问到的每个 `IsPartKind` 元素（零件/装配体/钣金/刻面/线框/轮廓），不受两个排除开关影响。
        /// </summary>
        public readonly Dictionary<string, List<IZElement>> ElementsByPartName =
            new Dictionary<string, List<IZElement>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前装配体文件路径（IZDoc.get_Name 槽 11；读不到＝第三态，空串）。</summary>
        public string AssemblyPath = "";
        /// <summary>场景第一个顶层零部件元素＝装配体行（表格第 2 行）的兜底读写目标。</summary>
        public IZElement RootElement;

        public int Visited;              // 走查过的零部件元素数
        public int Linked;               // 其中拿到外链路径的（去重前的实例数）
        public int SkippedSuppressed;    // 因「排除压缩件」跳过的元素（只影响表格）
        public int SkippedBom;           // 因「排除不在 BOM」跳过的元素（只影响表格）
        // 注：走查本身不挂「停止」钩子（只读元数据遍历，成本与「从装配体导入」同一量级，一次点击只走一遍）；
        //     逐行的读写循环才有停止检查，见 BatchProcessor。

        public int ElementCount { get { return ElementsByLink.Count; } }
        public int TotalInstances
        {
            get
            {
                int n = 0;
                foreach (var kv in ElementsByLink) n += kv.Value.Count;
                return n;
            }
        }
    }

    /// <summary>
    /// CAXA 3D（IronCAD）进程内服务封装。IZBaseApp 由 Addin 站点提供，本类不持有其生命周期。
    /// </summary>
    internal sealed class CaxaBatchService
    {
        private readonly IZBaseApp _app;

        public CaxaBatchService(IZBaseApp app)
        {
            _app = app;
        }

        public IZBaseApp App { get { return _app; } }

        public bool IsConnected
        {
            get
            {
                try { return _app != null && _app.ActiveDoc != null; }
                catch { return false; }
            }
        }

        /// <summary>连接状态文本（界面显示）。</summary>
        public string VersionText
        {
            get
            {
                if (_app == null) return "CAXA 3D 未连接";
                try
                {
                    string prod = _app.ApplicationVersion;
                    return string.IsNullOrEmpty(prod) ? "CAXA 3D 已连接" : ("CAXA 3D 已连接 (" + prod + ")");
                }
                catch { return "CAXA 3D 已连接"; }
            }
        }

        // ============================================================ 活动文档

        /// <summary>活动文档（IZBaseApp.get_ActiveDoc，槽 11）。</summary>
        public IZDoc ActiveDoc
        {
            get
            {
                try { return _app?.ActiveDoc; }
                catch { return null; }
            }
        }

        /// <summary>活动文档转 3D 场景文档；非场景文档返回 null。</summary>
        public IZSceneDoc ActiveSceneDoc
        {
            get
            {
                IZDoc doc = ActiveDoc;
                if (doc == null) return null;
                try { return doc as IZSceneDoc; }
                catch { return null; }
            }
        }

        // ============================================================ 设计树遍历（穿透）

        /// <summary>零部件类元素类型（零件/装配体/钣金/刻面/线框/轮廓）。</summary>
        public static bool IsPartKind(IZElement e)
        {
            if (e == null) return false;
            try
            {
                eZElementType t = e.Type;
                return t == eZElementType.Z_ELEMENT_PART
                    || t == eZElementType.Z_ELEMENT_ASSEMBLY
                    || t == eZElementType.Z_ELEMENT_SHEETMETAL_PART
                    || t == eZElementType.Z_ELEMENT_FACET_PART
                    || t == eZElementType.Z_ELEMENT_WIRE_PART
                    || t == eZElementType.Z_ELEMENT_PROFILE_PART;
            }
            catch { return false; }
        }

        /// <summary>解包 ZArray / object[] / 单个对象到列表。</summary>
        public static List<object> UnpackArray(object raw)
        {
            var result = new List<object>();
            if (raw == null) return result;
            ZArray zarr = raw as ZArray;
            if (zarr != null)
            {
                int count;
                zarr.Count(out count);
                for (int i = 0; i < count; i++)
                {
                    object o;
                    zarr.Get(i, out o);
                    if (o != null) result.Add(o);
                }
                return result;
            }
            object[] arr = raw as object[];
            if (arr != null)
            {
                foreach (object o in arr) if (o != null) result.Add(o);
                return result;
            }
            result.Add(raw);
            return result;
        }

        /// <summary>取元素的直接子元素（装配体/场景根/零件）。</summary>
        public List<IZElement> GetChildren(object parent)
        {
            var list = new List<IZElement>();
            if (parent == null) return list;
            ZArray arr = null;
            try
            {
                IZAssembly asm = parent as IZAssembly;
                if (asm != null) arr = asm.GetChildrenZArray();
                else
                {
                    IZElement elem = parent as IZElement;
                    if (elem != null) arr = elem.GetChildrenZArray();
                }
            }
            catch (Exception ex) { Diagnostics.Trace("GetChildrenZArray 失败：" + ex.Message); }

            if (arr == null)
            {
                try
                {
                    IZSceneDoc scene = parent as IZSceneDoc;
                    if (scene != null)
                        foreach (object o in UnpackArray(scene.GetChildElements()))
                        {
                            IZElement e = o as IZElement;
                            if (e != null) list.Add(e);
                        }
                }
                catch { }
                return list;
            }
            int count;
            arr.Count(out count);
            for (int i = 0; i < count; i++)
            {
                object raw;
                arr.Get(i, out raw);
                IZElement e = raw as IZElement;
                if (e != null) list.Add(e);
            }
            return list;
        }

        /// <summary>取场景顶层元素（穿透起点）。</summary>
        public List<IZElement> GetTopLevelElements()
        {
            var list = new List<IZElement>();
            IZSceneDoc scene = ActiveSceneDoc;
            if (scene == null) return list;
            try
            {
                foreach (object o in UnpackArray(scene.GetChildElements()))
                {
                    IZElement e = o as IZElement;
                    if (e != null && IsPartKind(e)) list.Add(e);
                }
            }
            catch (Exception ex) { Diagnostics.Log("读取场景顶层元素失败：" + ex.Message); }
            return list;
        }

        /// <summary>外部链接的 .ics 文件路径（虚拟件/未链接返回空）。</summary>
        public static string GetLinkedFile(IZElement e)
        {
            if (e == null) return "";
            try
            {
                bool linked;
                string file;
                IZAssembly asm = e as IZAssembly;
                if (asm != null) file = asm.GetExternallyLinkedInfo(out linked);
                else
                {
                    IZPart part = e as IZPart;
                    if (part == null) return "";
                    file = part.GetExternallyLinkedInfo(out linked);
                }
                if (!linked || string.IsNullOrEmpty(file)) return "";
                return file.Trim();
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("GetExternallyLinkedInfo 失败：" + ex.Message);
                return "";
            }
        }

        /// <summary>
        /// 从当前「打开的装配体文件」穿透遍历，收集全部有外链文件（.ics）的真实零件/装配体
        /// （含装配体本身；虚拟件/内嵌件无外链文件，不抓取入表）。
        /// 按 路径+文件名 去重汇总数量。
        /// 返回 ComponentInfo 列表（已去重，Count 为重复次数）。
        /// 【编译期已验：遍历/链接读取 API；装配体根路径取自 IZDoc.Name，需实机核对】
        /// </summary>
        public List<ComponentInfo> TraverseActiveAssembly(bool excludeSuppressed, bool excludeBomExcluded)
        {
            return WalkActiveAssembly(excludeSuppressed, excludeBomExcluded).Components;
        }

        /// <summary>
        /// 穿透走查当前装配体：一次遍历同时产出「表格用的组件清单」与「元素索引」（设计环境内直接读写属性用）。
        /// 读写链调用时把两个排除开关都传 false，让索引成为任何一次导入结果的超集（见 AssemblyWalk 注释）。
        /// 【编译期已验：走查/外链读取 API 与导入链同一套；元素索引命中与否需实机核对】
        /// </summary>
        public AssemblyWalk WalkActiveAssembly(bool excludeSuppressed, bool excludeBomExcluded)
        {
            var w = new AssemblyWalk();
            IZSceneDoc scene = ActiveSceneDoc;
            if (scene == null) return w;

            // 1) 装配体本身（当前打开的装配体文件）
            string asmPath = GetActiveDocFilePath();
            string asmName = string.IsNullOrEmpty(asmPath) ? "" : Path.GetFileNameWithoutExtension(asmPath);
            w.AssemblyPath = asmPath ?? "";
            if (!string.IsNullOrEmpty(asmPath))
                AddComponent(w.Map, Path.GetDirectoryName(asmPath) ?? "", Path.GetFileName(asmPath),
                    Path.GetExtension(asmPath).TrimStart('.').ToLowerInvariant());

            // 2) 穿透遍历全部子元素（虚拟件/无外链节点本身不抓取，但其下真实零件仍需遍历）
            foreach (IZElement top in GetTopLevelElements())
            {
                if (w.RootElement == null) w.RootElement = top;
                Collect(top, w, asmName, excludeSuppressed, excludeBomExcluded);
            }

            w.Components.AddRange(w.Map.Values);
            Diagnostics.Trace(string.Format(
                "WalkActiveAssembly: asm={0} 走查元素={1} 有外链={2} 索引键={3} 实例合计={4} 表格行={5} 排除压缩={6} 排除非BOM={7}",
                string.IsNullOrEmpty(asmPath) ? "<读不到>" : asmPath, w.Visited, w.Linked, w.ElementsByLink.Count,
                w.TotalInstances, w.Components.Count, excludeSuppressed, excludeBomExcluded));
            return w;
        }

        /// <summary>文件路径 → 索引键：全路径归一化（大小写不敏感）；GetFullPath 失败就退回原串小写（第三态，不抛）。</summary>
        public static string KeyOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string t = path.Trim().Trim('"');
            try { return Path.GetFullPath(t).ToLowerInvariant(); }
            catch { return t.ToLowerInvariant(); }
        }

        /// <summary>
        /// 用走查索引为表格某一行找到设计环境里的元素（不再打开该文件）。三级阶梯，每级都把依据写进 trace：
        ///   ① 全路径归一化后精确命中；
        ///   ② 行就是装配体自己（文件名相同）→ 用场景顶层第一个元素（与旧链「打开装配体取根元素」同一目标）；
        ///   ③ 按文件名唯一命中（路径写法不一致时的降级，同名多义则不猜）。
        /// 返回 false＝设计环境里没有这个文件的实例（记账用，不打开文档兜底——那是本轮明确要取消的行为）。
        /// </summary>
        public static bool TryFindElement(AssemblyWalk w, string fullPath, out IZElement elem, out string how)
        {
            elem = null; how = "";
            if (w == null || string.IsNullOrWhiteSpace(fullPath)) return false;

            string key = KeyOf(fullPath);
            List<IZElement> list;
            if (w.ElementsByLink.TryGetValue(key, out list) && list.Count > 0)
            {
                elem = list[0];
                how = list.Count > 1 ? ("全路径命中(" + list.Count + " 个实例，取第 1 个；共享表写入对全部实例生效)") : "全路径命中";
                return true;
            }

            try
            {
                string fn = Path.GetFileNameWithoutExtension(fullPath);
                string asmFn = Path.GetFileNameWithoutExtension(w.AssemblyPath ?? "");
                if (!string.IsNullOrEmpty(fn) && string.Equals(fn, asmFn, StringComparison.OrdinalIgnoreCase)
                    && w.RootElement != null)
                {
                    elem = w.RootElement; how = "装配体行·用场景顶层元素"; return true;
                }

                // 阶梯③：按「文件名」在索引里找。计数口径 = 命中的**不同路径键**个数，不是实例个数
                //          （同一文件在装配体里出现多次仍是一个键，不该因为实例多就拒绝匹配）。
                int keysMatched = 0; IZElement cand = null; string candKey = "";
                foreach (var kv in w.ElementsByLink)
                {
                    string nameNoExt = "";
                    try { nameNoExt = Path.GetFileNameWithoutExtension(kv.Key); } catch { }
                    if (!string.Equals(nameNoExt, fn, StringComparison.OrdinalIgnoreCase)) continue;
                    if (kv.Value.Count == 0) continue;
                    keysMatched++;
                    cand = kv.Value[0]; candKey = kv.Key;
                    if (keysMatched > 1) break;
                }
                if (keysMatched == 1)
                {
                    elem = cand;
                    how = "按文件名唯一命中(路径写法不一致的降级，索引侧=" + candKey + ")";
                    return true;
                }
                if (keysMatched > 1) { how = "索引里有多个同名不同路径的文件，不猜"; return false; }
            }
            catch (Exception ex) { Diagnostics.Trace("TryFindElement EX: " + ex.Message); }
            how = "索引里没有该文件＝它不是当前装配体的实例";
            return false;
        }

        /// <summary>是否压缩件（Z_SUPPRESSED）。【编译期已验：IZElement.GetStateStatus 槽17 / eZElementState.Z_SUPPRESSED=0】</summary>
        public static bool IsSuppressed(IZElement e)
        {
            try { return e.GetStateStatus(eZElementState.Z_SUPPRESSED); }
            catch (Exception ex) { Diagnostics.Trace("GetStateStatus 失败：" + ex.Message); return false; }
        }

        /// <summary>元素名（只用于日志；读不到返回占位串，不抛）。</summary>
        private static string SafeElemLabel(IZElement e)
        {
            if (e == null) return "<null>";
            try { return string.IsNullOrEmpty(e.Name) ? "<无名>" : e.Name; }   // IZElement::get_Name 槽 9（2027 members.csv 实测）
            catch { return "<名字读不到>"; }
        }

        /// <summary>是否在明细表中输出（纳入 BOM）。false=不在BOM，应排除。【编译期已验：IZPart/IZAssembly.IncludedInBOM 槽33，参照 caxa3dminitool BomTool】</summary>
        public static bool IsIncludedInBOM(IZElement e)
        {
            try
            {
                var part = e as IZPart;
                if (part != null) return part.IncludedInBOM;
                var asm = e as IZAssembly;
                if (asm != null) return asm.IncludedInBOM;
            }
            catch (Exception ex) { Diagnostics.Trace("IncludedInBOM 读取失败：" + ex.Message); }
            return true; // 读不到默认视为纳入（保守，不排除）
        }

        private void Collect(IZElement elem, AssemblyWalk w, string asmName,
            bool excludeSuppressed, bool excludeBomExcluded)
        {
            if (elem == null) return;
            w.Visited++;

            // 按零件名登记（选中联动索引用）：外链文件名＋元素名两个键都进，命中面最大。
            // 放在排除判断**之前**：压缩件/非 BOM 件在设计树里照样是可见可选的实例，按名选中时不该漏掉它们。
            if (IsPartKind(elem))
            {
                string linkForName = GetLinkedFile(elem);
                AddNameIndex(w, string.IsNullOrEmpty(linkForName) ? "" : Path.GetFileNameWithoutExtension(linkForName), elem);
                try { AddNameIndex(w, Path.GetFileNameWithoutExtension(elem.Name ?? ""), elem); }
                catch (Exception ex) { Diagnostics.Trace("元素名读取失败（只影响按名选中，不影响读写）：" + ex.Message); }
            }

            // ① 排除压缩件：压缩件本身及整条分支不导入、不计数（语义与导入链一致，走查同样在这里止步）。
            //    读写链调用时传 false，所以「勾选排除压缩件导入」之后仍然能读写压缩件属性的情况不会发生——
            //    那一行本来就不在表里。
            if (excludeSuppressed && IsSuppressed(elem))
            {
                w.SkippedSuppressed++;
                Diagnostics.Trace("跳过压缩件（不计数，整条分支不再下探）：" + (SafeElemLabel(elem)));
                return;
            }

            // 仅抓取有外部链接文件（.ics）的真实零件/装配体；虚拟件/内嵌件（无外链）不抓取
            string linked = GetLinkedFile(elem);
            if (!string.IsNullOrEmpty(linked))
            {
                w.Linked++;
                // 元素索引先登记，不受下面的 BOM 排除开关影响（排除只决定这一行进不进表格）
                string linkKey = KeyOf(linked);
                List<IZElement> bucket;
                if (!w.ElementsByLink.TryGetValue(linkKey, out bucket))
                {
                    bucket = new List<IZElement>();
                    w.ElementsByLink[linkKey] = bucket;
                }
                bucket.Add(elem);

                string linkedName = Path.GetFileNameWithoutExtension(linked);
                // 与选中装配体同零件名的虚拟件（节点名与装配体根文件同名）不导入表格；
                // 装配体根已单独入表，此处跳过不会丢失信息
                if (!string.IsNullOrEmpty(asmName) &&
                    string.Equals(linkedName, asmName, StringComparison.OrdinalIgnoreCase))
                {
                    Diagnostics.Trace("跳过与装配体同名的虚拟件：" + linked);
                }
                // ② 排除不在 BOM 的零件（"在明细表中输出"未勾选）；不计数（不入表）
                else if (excludeBomExcluded && !IsIncludedInBOM(elem))
                {
                    w.SkippedBom++;
                    Diagnostics.Trace("跳过不在BOM的零件（不计数）：" + linked);
                }
                else
                {
                    AddComponent(w.Map, Path.GetDirectoryName(linked) ?? "", Path.GetFileName(linked),
                        Path.GetExtension(linked).TrimStart('.').ToLowerInvariant());
                }
            }

            // 虚拟件/装配体继续递归子元素（穿透）：虚拟件节点本身不入表，但其下真实零件仍需遍历获取
            if (elem.Type == eZElementType.Z_ELEMENT_ASSEMBLY)
            {
                foreach (IZElement child in GetChildren(elem))
                    if (IsPartKind(child)) Collect(child, w, asmName, excludeSuppressed, excludeBomExcluded);
            }
        }

        /// <summary>把元素登记进「按零件名」索引（空名/重复登记都跳过；同一元素同时挂在文件名和节点名下是预期的）。</summary>
        private static void AddNameIndex(AssemblyWalk w, string name, IZElement elem)
        {
            if (string.IsNullOrWhiteSpace(name) || elem == null) return;
            string key = name.Trim();
            if (key.Length == 0) return;
            List<IZElement> bucket;
            if (!w.ElementsByPartName.TryGetValue(key, out bucket))
            {
                bucket = new List<IZElement>();
                w.ElementsByPartName[key] = bucket;
            }
            if (!bucket.Contains(elem)) bucket.Add(elem);
        }

        /// <summary>
        /// 在**当前设计环境**里按零件名选中全部同名零部件实例（11-06 用户指令 2）。
        /// 通道（本工程 interop v26 反射实测 PRESENT，槽号取 2027 members.csv 实测值）：
        ///   IZSceneDoc.get_SelectionMgr 槽 27 → IZSelectionMgr.RemoveAllFromSelection 槽 22
        ///   → IZSelectionMgr.AddElementToSelection(IZElement, Boolean) 槽 20
        /// vbIgnoreFilter 传 **true**：该参数的语义是「无视选择过滤器」，选零件时过滤器可能处于面/边/曲线模式，
        /// 传 false 会静默选不中——这是本链唯一会让「点了没反应」的开关，必须为 true。【待验：实机看是否真能选中】
        /// 权威判据＝回读 `GetSelectedElementsZArray()` 的个数（槽 19），不是 Void 调用没抛异常。
        /// 分片泵消息（铁律：大装配一次投几百个元素会占死主线程）。
        /// </summary>
        public SelectionSyncResult SelectElementsByName(AssemblyWalk w, string partName, Func<bool> stopCheck)
        {
            var res = new SelectionSyncResult();
            if (w == null || string.IsNullOrWhiteSpace(partName))
            {
                res.Detail = "零件名为空或没有走查索引＝本次不联动";
                return res;
            }
            List<IZElement> bucket;
            if (!w.ElementsByPartName.TryGetValue(partName.Trim(), out bucket) || bucket.Count == 0)
            {
                res.Detail = "索引里没有零件名「" + partName + "」（走查 " + w.Visited
                    + " 个元素、" + w.ElementsByPartName.Count + " 个名字键）";
                return res;
            }
            res.Candidates = bucket.Count;

            IZSceneDoc scene = ActiveSceneDoc;
            if (scene == null) { res.Detail = "当前活动文档不是 3D 场景文档（可能焦点在图纸环境）＝不联动"; return res; }
            // 反射实测：IZSceneDoc 上可用的是**属性** `SelectionMgr`，类型是 coclass `ZSelectionMgr`（不是 IZSelectionMgr），
            // 所以对 RCW 再做一次 as 转换拿接口（QI 到 IZSelectionMgr，IID 3EF4AD5F-…-CCFFBB2405B8）；
            // 拿不到接口就原样返回，不去猜别的通道。
            IZSelectionMgr sel;
            try { sel = scene.SelectionMgr as IZSelectionMgr; }      // 属性 → get_SelectionMgr 槽 27
            catch (Exception ex) { res.Detail = "SelectionMgr 取值失败：" + ex.Message; return res; }
            if (sel == null) { res.Detail = "SelectionMgr 返回空/不是 IZSelectionMgr＝不联动"; return res; }

            try { sel.RemoveAllFromSelection(); }                         // 槽 22
            catch (Exception ex) { Diagnostics.Trace("RemoveAllFromSelection 失败：" + ex.Message); }

            int added = 0, err = 0, chunk = 0;
            foreach (IZElement e in bucket)
            {
                if (stopCheck != null && stopCheck()) { res.Detail = "收到停止信号，联动中断"; break; }
                try { sel.AddElementToSelection(e, true); added++; }       // 槽 20，vbIgnoreFilter=true
                catch (Exception ex) { err++; if (err <= 3) Diagnostics.Trace("AddElementToSelection 失败：" + ex.Message); }
                // 每 25 个泵一次消息（分片铁律）；选中是瞬时 UI 操作，不做 Sleep
                if (++chunk >= 25) { chunk = 0; try { System.Windows.Forms.Application.DoEvents(); } catch { } }
            }
            res.Added = added; res.Errors = err;

            // 回读选中数（Void 调用不算成功，选中数才算）
            try
            {
                object raw = sel.GetSelectedElementsZArray();              // 槽 19
                res.SelectedNow = UnpackArray(raw).Count;
            }
            catch (Exception ex)
            { res.SelectedNow = -1; Diagnostics.Trace("GetSelectedElementsZArray 失败（第三态，不判成败）：" + ex.Message); }

            res.Ok = res.Added > 0 && res.SelectedNow >= res.Added;
            res.Detail = "候选 " + res.Candidates + "｜投递 " + res.Added + "｜失败 " + res.Errors
                + "｜回读选中 " + (res.SelectedNow < 0 ? "<读不到>" : res.SelectedNow.ToString());
            Diagnostics.Trace("选中联动「" + partName + "」：" + res.Detail);
            return res;
        }

        /// <summary>选中联动的结果（每个字段都是实测计数，-1＝第三态读不到）。</summary>
        internal sealed class SelectionSyncResult
        {
            public bool Ok;
            public int Candidates;
            public int Added;
            public int Errors;
            public int SelectedNow = -1;
            public string Detail = "";
        }

        private static void AddComponent(Dictionary<string, ComponentInfo> map, string path, string name, string ext)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            string key = ((path ?? "") + "\\" + name).ToLowerInvariant();
            if (map.TryGetValue(key, out var info)) info.Count++;
            else map[key] = new ComponentInfo { Path = path ?? "", Name = name, Ext = ext ?? "" };
        }

        /// <summary>当前打开文档的文件路径（IZDoc.get_Name，槽 11）。【待验：部分版本可能返回标题而非全路径】</summary>
        private string GetActiveDocFilePath()
        {
            try { return ActiveDoc?.Name ?? ""; }
            catch (Exception ex) { Diagnostics.Trace("读取活动文档路径失败：" + ex.Message); return ""; }
        }

        // ============================================================ 自定义属性

        /// <summary>取元素的自定义属性管理器（RCW 强类型，0=零件级 1=共享级）。</summary>
        public static IZCustomPropMgr GetPropMgr(IZElement elem, int selector)
        {
            try { return elem.GetCustomPropManager(selector) as IZCustomPropMgr; }
            catch (Exception ex)
            {
                Diagnostics.Trace("GetCustomPropManager(" + selector + ") 失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>按属性名读取：共享表铁律（规则 14.7/14.9）——只查 IsSharedMgr()==true 的那张表，
        /// 不分 SC/非 SC 前缀，不再「先零件表再共享表」；共享表里没有就是第三态（返回 false），
        /// 绝不换一张表再试一次。精确同名、不拼候选名的老规矩不变（规则 14.1/14.2）。
        /// 【编译期已验：GetCustomPropManager 槽 42 / IsSharedMgr 槽 7 / GetCustomPropAsString 槽 18，本工程 interop v26 全部 PRESENT】</summary>
        public static bool TryGetCustomProp(IZElement elem, string propName, out string value)
        {
            value = null;
            if (elem == null || string.IsNullOrEmpty(propName)) return false;

            IZCustomPropMgr mgr = GetSharedPropMgr(elem);
            if (mgr == null)
            {
                Diagnostics.Trace("TryGetCustomProp(" + propName + ")：selector 0、1 两轮取管理器都没有 IsSharedMgr=true 的表");
                return false;
            }
            try
            {
                string v; bool found;
                mgr.GetCustomPropAsString(propName, out v, out found);   // 槽 18：(BSTR 名, String& 值, Boolean& 命中)
                if (found) { value = v ?? ""; return true; }             // 命中即算读到，空串是合法值（第三态只给「没命中」）
            }
            catch (Exception ex) { Diagnostics.Trace("GetCustomPropAsString(" + propName + ") EX: " + ex.Message); }
            return false;
        }

        /// <summary>共享表管理器（按 IsSharedMgr 认，依次试 selector 0/1）。</summary>
        public static IZCustomPropMgr GetSharedPropMgr(IZElement elem)
        {
            for (int s = 0; s <= 1; s++)
            {
                IZCustomPropMgr mgr = GetPropMgr(elem, s);
                if (mgr == null) continue;
                try { if (mgr.IsSharedMgr()) return mgr; } catch { }
            }
            return null;
        }

        /// <summary>只在共享表里读一个属性（写入链的回读判据必须与写入通道同表：
        /// 写走的是共享管理器＝规则 14.7「强制写共享表、对所有链接实例生效」，
        /// 若回读用 TryGetCustomProp（非 SC 名先查零件表）就会「写进去了却回读不一致」假阳性）。
        /// 共享表拿不到指针返回 false＝第三态（如实说明读不到，不判成写失败）。</summary>
        public static bool TryGetSharedProp(IZElement elem, string propName, out string value, out string mgrState)
        {
            value = null; mgrState = "";
            if (elem == null || string.IsNullOrEmpty(propName)) { mgrState = "elem/属性名为空"; return false; }
            IZCustomPropMgr mgr = GetSharedPropMgr(elem);
            if (mgr == null)
            {
                mgrState = "共享管理器未取到";
                Diagnostics.Trace("TryGetSharedProp(" + propName + ")：selector 0、1 两轮取管理器都没有 IsSharedMgr=true 的表");
                return false;
            }
            mgrState = "共享表";
            try
            {
                string v; bool found;
                mgr.GetCustomPropAsString(propName, out v, out found);
                if (found) { value = v ?? ""; return true; }
                return false;
            }
            catch (Exception ex)
            {
                mgrState = "读取异常";
                Diagnostics.Trace("TryGetSharedProp(" + propName + ") EX: " + ex.Message);
                return false;
            }
        }

        // 共享表铁律（规则 14.7/14.9）：本工程原有的「零件级/非共享表」获取函数已删除，
        // 读与写都只允许走 GetSharedPropMgr；确认不到共享表＝失败/第三态，绝不兜底换表。

        /// <summary>按表头名读取一行所需属性（H 列起的自定义属性名）。</summary>
        public Dictionary<string, string> ReadRowProperties(IZElement elem, IList<string> headers)
        {
            return ReadRowProperties(elem, headers, null);
        }

        /// <summary>同上，并把「没命中」的表头名收集进 missingOut（第三态：读不到 ≠ 空值，两者分开记账）。
        /// 【11-06 用户指令 0＋规则 14.8】表头显示名先过「内部字段映射」（caxa3dBatchMaster内部字段映射.txt，
        /// 例：重量→PartMass）再取通道；映射值**原样**使用，不猜大小写、不拼候选名（规则 14.1/14.2）。
        /// 【通道阶梯·11-06 用户指令 1＋指令 3】每个自定义属性列按「① 共享表（自定义表）优先 → ② BOM 导出兜底」取值：
        ///   ① 共享表铁律（14.7/14.9）仍全权适用——先 GetCustomPropAsString 查共享表；
        ///   ② 共享表没命中时，再拿整行 BOM 快照（ExportSelfBOM）按候选匹配兜底（用户指令 3：1.自定义表 2.bom导出）。
        ///   候选里只要有一个属于「BOM 字段清单」（caxa3dBatchMasterBOM字段清单.txt，默认 PartMass/PartMassType/
        ///   PartSpecMass/MassDensity），则**跳过共享表直走 BOM**（用户实机结论：这族字段属于 BOM 引擎/零件属性子系统，
        ///   GetCustomPropAsString 必然空，直走 BOM 省一次必然落空的共享表查询）。同一元素只向宿主 ExportSelfBOM 一次，
        ///   全部候选列（含兜底）复用该快照（大装配逐列重算会拖死主线程）。
        /// 返回字典仍按**显示名**为键（表格回填用），missingOut 也回显示名（三态计数按列对齐）。</summary>
        /// <summary>本轮哪些**表头列**走过 BOM 引擎通道（供读取完成的日志逐列点名；由 ResetBomColumnTrack 清零）。</summary>
        private readonly List<string> _bomColumns = new List<string>();
        public ICollection<string> BomColumns { get { return _bomColumns; } }
        public void ResetBomColumnTrack() { _bomColumns.Clear(); }

        public Dictionary<string, string> ReadRowProperties(IZElement elem, IList<string> headers, List<string> missingOut)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var bomChannels = new List<string>();     // 本次调用里走过 BOM 通道的表头（并集进 _bomColumns）
            if (headers == null) return result;
            if (elem == null)
            {
                foreach (string h in headers)
                {
                    result[h] = "";
                    if (missingOut != null && !string.IsNullOrWhiteSpace(h)) missingOut.Add(h);
                }
                return result;
            }

            // BOM 快照懒加载：同一元素只向宿主要一次 ExportSelfBOM，所有候选列（含兜底）复用（大装配逐列重算会拖死）。
            BomValueReader.BomSnapshot bomSnap = null;
            bool bomSnapBuilt = false;
            BomValueReader.BomSnapshot EnsureBom()
            {
                if (!bomSnapBuilt) { bomSnap = BomValueReader.GetBomSnapshot(elem); bomSnapBuilt = true; }
                return bomSnap;
            }

            foreach (string raw in headers)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string h = raw.Trim();
                // 表头显示名 → 候选内部字段（caxa3dBatchMaster内部字段映射.txt，例：重量→PartMass；一列可配多候选，按顺序试、命中即停）。
                string[] cands = PropFieldMap.Candidates(h);

                // —— 统一通道：所有非固定列（从 H 列起到底）一律「① 自定义属性共享表 → ② BOM 导出兜底」——
                // 【用户指令 3「修正」】之前重量/日期这类「BOM 字段清单点名」的列会**跳过共享表直走 BOM**，与
                // 「所有非固定列都 ①→②」不一致；现统一为：先试共享表（铁律 14.7/14.9，命中即停），
                // 共享表读不到再拿整行 BOM 快照（ExportSelfBOM）按候选匹配兜底。用户实机结论：PartMass 等只在 BOM
                // 子系统，共享表必然空，所以重量/日期实际仍由 ② 兜底取到——顺序变了、结果不变。
                // 同一元素只向宿主 ExportSelfBOM 一次，所有候选列（含兜底）复用该快照（大装配逐列重算会拖死主线程）。
                bool found = false; string val = ""; string hitField = cands[0]; string missNote = "";
                string sharedVal = null; string sharedField = null;   // ① 命中但值为空时占位（属性存在但空，属第三态之一）
                for (int ci = 0; ci < cands.Length; ci++)
                {
                    string cv;
                    if (TryGetCustomProp(elem, cands[ci], out cv))
                    {
                        if (!string.IsNullOrEmpty(cv)) { found = true; val = cv; hitField = cands[ci]; break; }  // ① 命中且非空才停
                        if (sharedVal == null) { sharedVal = cv; sharedField = cands[ci]; }   // 命中但空：记下继续，不阻断 ②
                    }
                    else
                    {
                        missNote += (ci > 0 ? "、" : "") + cands[ci] + "＝没这个属性";
                    }
                    val = "";
                }
                string channelNote = "";
                if (!found)
                {
                    // ② BOM 导出兜底：共享表没这个属性，再拿整行 BOM 快照按候选匹配（一次性取，复用）
                    BomValueReader.BomSnapshot snap = EnsureBom();
                    BomReadResult b = snap.FindBest(cands, out string matched);
                    if (b.Found && !string.IsNullOrEmpty(b.Value))
                    {
                        found = true; val = b.Value; hitField = matched;
                        channelNote = "｜BOM 兜底命中「" + matched + "」值=[" + b.Value + "]";
                        bomChannels.Add(h);
                    }
                    else
                    {
                        // ② 没拿到非空值：若 ① 曾「命中但空」（属性存在但空），退回 ① 的空（保持第三态，不进 missingOut）；
                        // 否则继续「质量属性直读」最后兜底。
                        if (sharedVal != null)
                        {
                            found = true; val = sharedVal; hitField = sharedField;
                            channelNote = "（①共享表「" + sharedField + "」命中但值为空，②BOM 也未取到非空值）";
                        }
                        else
                        {
                            // 最后一道兜底：已知 BOM 质量字段（PartMass/PartSpecMass/PartMassType/MassDensity）再试
                            // 「质量属性直读」通道（IZPartProperty.CalculatedMass 等），防个别元素 ExportSelfBOM 偶发缺该 label。
                            // 不重新 ExportSelfBOM，只是一次轻量直读。
                            bool isMassField = false;
                            foreach (string c in cands) if (BomFieldList.Contains(c)) { isMassField = true; break; }
                            if (isMassField)
                            {
                                BomReadResult p = BomValueReader.TryMassPropertyCandidates(elem, cands, out string mField);
                                if (p.Found) { found = true; val = p.Value ?? ""; hitField = mField; channelNote = "｜质量属性直读命中「" + mField + "」"; bomChannels.Add(h); }
                            }
                            if (!found) missNote += (missNote.Length > 0 ? "、" : "") + "BOM 也未命中";
                        }
                    }
                }
                result[raw] = found ? (val ?? "") : "";
                if (!found && missingOut != null) missingOut.Add(h);   // 第三态：共享表 + BOM 都没命中
                if (cands.Length > 1 || !string.Equals(h, hitField, StringComparison.Ordinal) || sharedVal != null)
                    Diagnostics.Trace("字段映射：表头「" + h + "」→ 候选「" + string.Join("、", cands) + "」"
                        + (found ? " 读到（命中「" + hitField + "」值=[" + (val ?? "") + "]" + channelNote + "）" : " 全部候选都没这个属性（" + missNote + "）"));
            }
            foreach (string b in bomChannels)
                if (!_bomColumns.Contains(b)) _bomColumns.Add(b);
            return result;
        }

        // ============================================================ 同名工程图

        /// <summary>是否存在同名工程图（.exb）。</summary>
        public static bool HasDrawing(string modelFile)
        {
            if (string.IsNullOrEmpty(modelFile)) return false;
            try
            {
                string dir = Path.GetDirectoryName(modelFile);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                string baseName = Path.GetFileNameWithoutExtension(modelFile);
                if (string.IsNullOrEmpty(baseName)) return false;
                return File.Exists(Path.Combine(dir, baseName + ".exb"));
            }
            catch { return false; }
        }

        /// <summary>是否存在同名外链（.ics）。规则：与零件名同名的 .ics 文件 = 外部链接（有外链）。</summary>
        public static bool HasExternalLink(string modelFile)
        {
            if (string.IsNullOrEmpty(modelFile)) return false;
            try
            {
                string dir = Path.GetDirectoryName(modelFile);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                string baseName = Path.GetFileNameWithoutExtension(modelFile);
                if (string.IsNullOrEmpty(baseName)) return false;
                return File.Exists(Path.Combine(dir, baseName + ".ics"));
            }
            catch { return false; }
        }

        // ============================================================ 打开/保存（按路径）

        /// <summary>按文件路径打开文档（IZBaseApp.OpenFile，槽 7）。
        /// 【已改用守卫链】属性批量读取/写入不再走这里：OpenFile 没有 out pbAlreadyOpen，无法区分
        /// 「宿主早就打开着」与「本次新开」，无条件 Save+Close 会把第 2 行装配体自己关掉（用户实机反馈）。
        /// 现在由 <see cref="CaxaDocGuard.Open"/>（OpenFile2 槽 30）+ Finish/Restore 负责。
        /// 【11-06 起本方法已无调用者】刷新工程图链换成图纸进程内的 CRX 桥（`ViewBridge`）之后，
        /// 远程 COM 的 `OpenFile`/`doc.Save` 那条路整体退役（对 .exb 是实测过的假成功），不要再接回去。</summary>
        public IZDoc OpenDocument(string filePath)
        {
            if (_app == null || string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            try { return _app.OpenFile(filePath, false); }
            catch (Exception ex)
            {
                Diagnostics.Log("OpenFile 失败：" + filePath + " -> " + ex.Message);
                return null;
            }
        }

        /// <summary>取文档根元素（场景文档顶层零部件）。</summary>
        public List<IZElement> GetDocRootElements(IZDoc doc)
        {
            var list = new List<IZElement>();
            if (doc == null) return list;
            try
            {
                IZSceneDoc scene = doc as IZSceneDoc;
                if (scene == null) return list;
                foreach (object o in UnpackArray(scene.GetChildElements()))
                {
                    IZElement e = o as IZElement;
                    if (e != null && IsPartKind(e)) list.Add(e);
                }
            }
            catch (Exception ex) { Diagnostics.Log("读取文档根元素失败：" + ex.Message); }
            return list;
        }

        /// <summary>保存并关闭文档（IZDoc.Save 槽 9 + Close 槽 13）。
        /// 【本轮已无调用者】属性批量读取/写入的收尾换成 CaxaDocGuard.Finish（判脏保存 + Close2 槽 23；
        /// 已实测：外链 .ics 用 IZBaseApp.CloseFile 是假成功，Close2(true) 才是关窗口的正确入口）。
        /// 保留本方法只为对照旧行为；刷新工程图链自 11-06 起改走 CRX 桥（`ViewBridge.Request` 的 SAVE/CLOSEDOC），
        /// 不再用本方法——远程 COM 对 .exb 的 Save/Close 是假成功，hr=0 只写出 xxx.exb.ics。</summary>
        public bool SaveAndClose(IZDoc doc)
        {
            if (doc == null) return false;
            try { doc.Save(); }
            catch (Exception ex) { Diagnostics.Log("Save 失败：" + ex.Message); }
            try { doc.Close(); return true; }
            catch (Exception ex) { Diagnostics.Log("Close 失败：" + ex.Message); return false; }
        }

        // ============================================================ 文件改名
        // 磁盘级改名（旧 RenameFileOnDisk：File.Move ics + 同名 exb）已整条删除，不保留对照实现：
        //   它只挪文件，设计环境里的外链指向仍是旧路径、旧 ics 的占用者也不会释放，
        //   11-06 用户指令 5 选定「全盘复刻 V2.0 联动链」后由 LinkRenameV2 + BatchProcessor.RenameFiles 取代。
        //   再提「直接改磁盘文件名」属于已否决路线（本项目 3.3 节）。
    }
}
