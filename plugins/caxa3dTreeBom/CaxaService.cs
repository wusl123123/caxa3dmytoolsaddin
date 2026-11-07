using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dTreeBom
{
    /// <summary>
    /// CAXA 3D（IronCAD）进程内 API 封装：
    ///  - 活动场景文档 / 选中元素
    ///  - 设计树子元素遍历（IZSceneDoc / IZAssembly / IZElement）
    ///  - 自定义属性（0=零件级 / 1=共享级，SC* 属性走共享表）
    ///  - 外部链接文件、材料名、BOM 设置、同名工程图判定
    ///  - 文件预览缩略图（IZBaseApp::FilePreviewImage 延迟绑定，WritePreviewToFile 兜底）
    /// 全部 API 调用都做了异常隔离：单个元素失败不影响整棵树。
    /// </summary>
    internal sealed class CaxaService
    {
        private readonly IZBaseApp _app;

        /// <summary>缩略图输出目录（PNG）。</summary>
        public static readonly string ThumbnailDir =
            Path.Combine(Path.GetTempPath(), "caxa3dTreeBom_thumbs");

        /// <summary>文件路径 → 已生成 PNG 路径（同名文件只取一次预览）。</summary>
        private readonly Dictionary<string, string> _thumbCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public CaxaService(IZBaseApp app)
        {
            _app = app;
            try { Directory.CreateDirectory(ThumbnailDir); } catch { }
        }

        public IZBaseApp App { get { return _app; } }

        /// <summary>连接状态文本（界面显示用）。</summary>
        public string VersionText
        {
            get
            {
                if (_app == null) return "CAXA 3D 未连接（Add-in 宿主不可用）";
                string api = "";
                try { api = _app.ApiVersion; } catch { }
                string prod = "";
                try { prod = _app.ApplicationVersion; } catch { }
                return string.IsNullOrEmpty(prod) ? ("CAXA 3D 已连接 (API " + api + ")")
                                                 : ("CAXA 3D 已连接 (版本 " + prod + ")");
            }
        }

        public bool IsConnected
        {
            get
            {
                try { return _app != null && _app.ActiveDoc != null; }
                catch { return false; }
            }
        }

        /// <summary>上一次记录的文档状态（相同状态只写一次 trace，避免 2s 轮询刷屏）。</summary>
        private string _lastDocState;

        public IZSceneDoc SceneDoc
        {
            get
            {
                if (_app == null) { NoteDocState("ActiveDoc 不可用：Add-in 宿主未连接"); return null; }
                try
                {
                    IZDoc doc = _app.ActiveDoc;
                    if (doc == null) { NoteDocState("ActiveDoc = null（无活动文档）"); return null; }
                    IZSceneDoc scene = doc as IZSceneDoc;
                    if (scene == null)
                        NoteDocState("ActiveDoc 不是场景文档：" + DescribeDoc(doc));
                    else
                        NoteDocState(null);   // 恢复正常，下次异常重新记录
                    return scene;
                }
                catch (Exception ex)
                {
                    NoteDocState("读取 ActiveDoc 失败：" + ex.Message);
                    return null;
                }
            }
        }

        private void NoteDocState(string state)
        {
            if (string.Equals(state, _lastDocState, StringComparison.Ordinal)) return;
            _lastDocState = state;
            if (!string.IsNullOrEmpty(state)) Diagnostics.Trace(state);
        }

        /// <summary>活动文档诊断文本：文档类型（Z_SCENE / Z_DRAWING）、名称、可转接口、打开文档数。</summary>
        public string DescribeActiveDoc()
        {
            var sb = new System.Text.StringBuilder();
            if (_app == null) return "未连接 CAXA 3D";
            try
            {
                IZDoc doc = _app.ActiveDoc;
                if (doc == null) sb.Append("ActiveDoc=null");
                else
                {
                    sb.Append("ActiveDoc 类型=").Append(SafeDocType(doc));
                    try { sb.Append("，名称=").Append(doc.Name); } catch { sb.Append("，名称读取失败"); }
                    sb.Append("，可转场景=").Append(doc as IZSceneDoc != null);
                    sb.Append("，可转图纸=").Append(doc as IZDrawingDoc != null);
                }
            }
            catch (Exception ex) { sb.Append("ActiveDoc 读取异常：").Append(ex.Message); }

            try { sb.Append("，打开文档数=").Append(_app.GetOpenDocsCount()); } catch { }
            return sb.ToString();
        }

        private static string SafeDocType(IZDoc doc)
        {
            try { return doc.Type.ToString(); } catch { return "未知"; }
        }

        private static string DescribeDoc(IZDoc doc)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("类型=").Append(SafeDocType(doc));
            try { sb.Append("，名称=").Append(doc.Name); } catch { }
            return sb.ToString();
        }

        /// <summary>活动文档文件路径（场景未保存时可能只是标题名）。</summary>
        public string CurrentFilePath
        {
            get
            {
                IZDoc doc = null;
                try { doc = _app == null ? null : _app.ActiveDoc; } catch { }
                if (doc == null) return "";
                string name = "";
                try { name = doc.Name; } catch { }
                return string.IsNullOrEmpty(name) ? "" : name.Trim();
            }
        }

        public string CurrentFileName
        {
            get
            {
                string p = CurrentFilePath;
                if (string.IsNullOrEmpty(p)) return "";
                try { return Path.GetFileName(p); } catch { return p; }
            }
        }

        // ================================================================ 选中 / 根节点

        /// <summary>取当前场景中选中的零部件类元素（面/边/特征被过滤）。</summary>
        public List<IZElement> GetSelectedElements(out string error)
        {
            error = null;
            var list = new List<IZElement>();

            IZSceneDoc scene = SceneDoc;
            if (scene == null)
            {
                error = "当前活动文档不是 3D 场景文档，请切换到设计环境后再试。";
                return list;
            }

            try
            {
                IZSelectionMgr sel = scene.SelectionMgr;
                if (sel == null) { error = "取不到 SelectionMgr。"; return list; }
                if (!sel.SelectionsAvailable()) return list;

                ZArray arr = sel.GetSelectedElementsZArray();
                if (arr == null) return list;

                int count;
                arr.Count(out count);
                for (int i = 0; i < count; i++)
                {
                    object raw;
                    arr.Get(i, out raw);
                    IZElement elem = raw as IZElement;
                    if (elem == null) continue;
                    if (!IsPartKind(elem)) { Diagnostics.Trace("选中[" + i + "] 非零部件类型，忽略"); continue; }
                    list.Add(elem);
                }
            }
            catch (Exception ex)
            {
                error = "读取选中元素失败：" + ex.Message;
                Diagnostics.Log(error);
            }
            return list;
        }

        /// <summary>
        /// 当前选中零部件的名称（未选中返回空串）。
        /// 连接状态区按「文件名----零件名」显示用（语义对齐 caxa3dAttributeManageraddin 的 ProbeSessionInfo）。
        /// </summary>
        public string GetSelectedElementName()
        {
            string err;
            List<IZElement> sel = GetSelectedElements(out err);
            if (sel.Count == 0) return "";
            return SafeElementName(sel[0]);
        }

        /// <summary>场景顶层元素（未选中任何东西时作为遍历起点）。</summary>
        public List<IZElement> GetTopLevelElements()
        {
            var list = new List<IZElement>();
            IZSceneDoc scene = SceneDoc;
            if (scene == null) return list;

            try
            {
                object raw = scene.GetChildElements();
                foreach (object o in UnpackArray(raw))
                {
                    IZElement e = o as IZElement;
                    if (e != null && IsPartKind(e)) list.Add(e);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("读取场景顶层元素失败：" + ex.Message);
            }
            return list;
        }

        // ================================================================ 元素类型 / 子元素

        /// <summary>零部件类元素类型（零件/装配体/钣金/面片/线框/轮廓）。</summary>
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

        public static bool IsAssemblyElement(IZElement e)
        {
            if (e == null) return false;
            try { return e.Type == eZElementType.Z_ELEMENT_ASSEMBLY; }
            catch { return false; }
        }

        /// <summary>取元素的直接子元素（装配体 / 场景根）。零件返回空。</summary>
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
            catch (Exception ex)
            {
                Diagnostics.Trace("GetChildrenZArray 失败：" + ex.Message);
            }

            if (arr == null)
            {
                try
                {
                    IZSceneDoc scene = parent as IZSceneDoc;
                    if (scene != null)
                    {
                        foreach (object o in UnpackArray(scene.GetChildElements()))
                        {
                            IZElement e = o as IZElement;
                            if (e != null) list.Add(e);
                        }
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

        /// <summary>把接口返回的 Object（可能是 ZArray / object[] / 单个对象）解包到列表。</summary>
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

        // ================================================================ 名称 / 外部链接 / BOM 设置

        public static string SafeElementName(IZElement e)
        {
            if (e == null) return "";
            try { return (e.Name ?? "").Trim(); } catch { return ""; }
        }

        /// <summary>外部链接的 .ics 文件路径（虚拟件返回空）。</summary>
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

        /// <summary>BOM 设置里的图号/描述（CAXA 的 BOM 属性，未设置时为空）。</summary>
        public static void GetBomSetting(IZElement e, out string partNum, out string desc)
        {
            partNum = ""; desc = "";
            if (e == null) return;
            try
            {
                string pn, ds; bool userQty; int qty;
                IZAssembly asm = e as IZAssembly;
                if (asm != null)
                {
                    if (asm.GetBOMSetting(out pn, out ds, out userQty, out qty))
                    {
                        partNum = (pn ?? "").Trim();
                        desc = (ds ?? "").Trim();
                    }
                    return;
                }
                IZPart part = e as IZPart;
                if (part != null && part.GetBOMSetting(out pn, out ds, out userQty, out qty))
                {
                    partNum = (pn ?? "").Trim();
                    desc = (ds ?? "").Trim();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("GetBOMSetting 失败：" + ex.Message);
            }
        }

        /// <summary>元素是否参与 BOM（读不到时按参与处理）。</summary>
        public static bool IncludedInBom(IZElement e)
        {
            try
            {
                IZAssembly asm = e as IZAssembly;
                if (asm != null) return asm.IncludedInBOM;
                IZPart part = e as IZPart;
                if (part != null) return part.IncludedInBOM;
            }
            catch { }
            return true;
        }

        // ================================================================ 自定义属性

        /// <summary>取元素的自定义属性管理器。0=零件级，1=共享（文档级，SC* 系列属性在此）。</summary>
        public static IZCustomPropMgr GetPropMgr(IZElement elem, int selector)
        {
            try { return elem.GetCustomPropManager(selector) as IZCustomPropMgr; }
            catch (Exception ex)
            {
                Diagnostics.Trace("GetCustomPropManager(" + selector + ") 失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>按 IsSharedMgr() 依次试 0/1 定位共享表。</summary>
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

        // 共享表铁律（规则 14.7/14.9）：本工程不再提供零件级（非共享）管理器。
        // 读不到共享表就是第三态，禁止换成另一张表再试一次。

        /// <summary>SC 开头的属性属于共享表（本机实证）。</summary>
        private static bool IsSharedPropName(string name)
        {
            return name.StartsWith("SC", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>表头去掉 "SC" 前缀后的名字（SC图号 → 图号）。</summary>
        public static string StripScPrefix(string header)
        {
            if (string.IsNullOrEmpty(header)) return header;
            string h = header.Trim();
            if (h.StartsWith("SC", StringComparison.OrdinalIgnoreCase) && h.Length > 2)
                return h.Substring(2);
            return h;
        }

        /// <summary>读取单个自定义属性：只读共享表（IsSharedMgr()==true 的那张），
        /// 读不到＝第三态；不查零件级表兜底，也不按属性名前缀换表（规则 14.7/14.9）。</summary>
        public static bool TryGetCustomProp(IZElement elem, string propName, out string value)
        {
            value = null;
            if (elem == null || string.IsNullOrEmpty(propName)) return false;

            IZCustomPropMgr mgr = GetSharedPropMgr(elem);
            if (mgr == null)
            {
                Diagnostics.Trace("TryGetCustomProp(" + propName + ")：GetCustomPropManager(0/1) 里没有 IsSharedMgr=true 的表");
                return false;
            }
            try
            {
                string v; bool found;
                mgr.GetCustomPropAsString(propName, out v, out found);
                if (found) { value = v ?? ""; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>材料名（先读 GetMaterialNameProperty 指向的自定义属性，再退回 IZPartProperty）。</summary>
        public static string GetMaterial(IZElement elem)
        {
            if (elem == null) return "";
            string propName = "";
            try { elem.GetMaterialNameProperty(out propName); } catch { }
            if (!string.IsNullOrEmpty(propName))
            {
                string v;
                if (TryGetCustomProp(elem, propName, out v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
            }

            try
            {
                IZPartProperty pp = elem as IZPartProperty;
                if (pp != null)
                {
                    string name; bool onlyThis;
                    pp.GetMaterialName(out name, out onlyThis);
                    if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                }
                IZAssemblyProperty ap = elem as IZAssemblyProperty;
                if (ap != null)
                {
                    string name; bool onlyThis;
                    ap.GetMaterialName(out name, out onlyThis);
                    if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("GetMaterialName 失败：" + ex.Message);
            }
            return "";
        }

        /// <summary>
        /// 按表头名读取一行所需的全部属性（旧签名，不关心取值来源时用）。
        /// </summary>
        public Dictionary<string, string> ReadRowProperties(IZElement elem, IList<string> headers)
        {
            Dictionary<string, string> src;
            return ReadRowProperties(elem, headers, out src);
        }

        /// <summary>
        /// 按表头名读取一行所需的全部属性，并回传每列的**取值来源**（sourcesOut，键＝表头原文）。
        ///
        /// 【通道阶梯＝用户指令 2026-11-06 23:37「优先从自定义属性中获取，没有数据时候兜底从 BOM 导出」】
        ///   ① 自定义属性**共享表**（唯一铁律，规则 14.7/14.9：只认 IsSharedMgr()==true 的那张表，
        ///      读不到＝第三态，不换表、不按前缀分流）→ 来源串 "属性"；
        ///   ② BOM 导出兜底（IZSceneElement.ExportSelfBOM 整行快照，按候选名匹配；同一元素只取一次快照，
        ///      所有列复用）→ 来源串 "BOM:&lt;命中的 label&gt;"；
        ///      候选名＝表头原名 → 去 SC 前缀 → 官方字典 IC_BOM_DIC.xml 的内部键，
        ///      用户可在 %APPDATA%\caxa3dTreeBom\caxa3dTreeBom表头字段映射.txt 里覆盖整列候选或写「无」禁用兜底。
        ///   ③ 原有的非 SC 专用兜底（名称/材料/图号/备注 → 元素名 / 材料通道 / BOM 设置）→ "专用:名称" 等。
        ///
        /// 行为变更留痕：②之前 SC* 表头「没有该属性就留空、绝不兜底」（本项目踩坑 8，针对的是
        /// GetBOMSetting 的描述串台）。本轮按用户新指令改为 **SC* 同样走 ②**，但 ② 只查 ExportSelfBOM 的
        /// 同名 label（实机 labels 里确有 SC图号/SC名称 这类中文列名），**不查 GetBOMSetting**，
        /// 所以原来的串台面不存在；③仍只对非 SC 表头开放。
        ///
        /// 「读不到」与「读到空值」分家：①②③ 都只认**非空白**才算命中，全空＝留第三态（空串＋空来源），
        /// 不写默认值、不用别的列凑数。
        /// </summary>
        public Dictionary<string, string> ReadRowProperties(IZElement elem, IList<string> headers,
                                                            out Dictionary<string, string> sourcesOut)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sourcesOut = sources;
            if (headers == null) return result;
            if (elem == null)
            {
                foreach (string h in headers) { result[h] = ""; sources[h] = ""; }
                return result;
            }

            string elemName = SafeElementName(elem);
            string bomPartNum, bomDesc;
            GetBomSetting(elem, out bomPartNum, out bomDesc);

            string material = null;

            // ② 的快照懒加载：只有真的需要兜底时才向宿主调一次 ExportSelfBOM，
            // 之后本行所有列复用（大装配逐列重算会拖死主线程）。
            BomSnapshot snap = null;
            bool snapBuilt = false;

            foreach (string raw in headers)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string h = raw.Trim();
                string val = "";

                // —— ① 自定义属性共享表（精确同名）——
                if (TryGetCustomProp(elem, h, out val) && !string.IsNullOrWhiteSpace(val))
                {
                    result[raw] = val.Trim();
                    sources[raw] = "属性";
                    continue;
                }

                // —— ② BOM 导出兜底（SC* 与非 SC 一视同仁；用户显式禁用的列跳过）——
                string[] cands = HeaderBomMap.IsDisabled(h) ? new string[0] : HeaderBomMap.Candidates(h);
                if (cands.Length > 0)
                {
                    if (!snapBuilt) { snap = BomExportReader.GetSnapshot(elem); snapBuilt = true; }
                    BomHit hit = snap.Find(cands);
                    if (hit.Found)
                    {
                        result[raw] = hit.Value;
                        sources[raw] = "BOM:" + hit.Label;
                        Diagnostics.Trace("BOM 兜底命中：表头「" + h + "」→ label「" + hit.Label + "」（"
                            + hit.Mode + "）｜" + hit.Note);
                        continue;
                    }
                    if (snapBuilt && !string.IsNullOrEmpty(hit.Note))
                        Diagnostics.Trace("BOM 兜底未命中：表头「" + h + "」｜候选=" + string.Join("、", cands)
                            + "｜" + hit.Note);
                }

                // —— ③ 非 SC 表头的专用兜底（SC* 到此为止：三条通道都没数据就留第三态）——
                if (IsSharedPropName(h))
                {
                    result[raw] = "";
                    sources[raw] = "";
                    continue;
                }

                string v2 = "";
                string src2 = "";
                switch (h)
                {
                    case "名称":
                    case "零件名称":
                    case "零件名":
                        v2 = elemName; src2 = "专用:名称";
                        break;
                    case "材料":
                    case "材质":
                        if (material == null) material = GetMaterial(elem);
                        v2 = material; src2 = "专用:材料";
                        break;
                    case "图号":
                    case "零件号":
                    case "零件编号":
                    case "代号":
                        v2 = bomPartNum; src2 = "专用:图号";
                        break;
                    case "备注":
                    case "描述":
                        v2 = bomDesc; src2 = "专用:备注";
                        break;
                }
                result[raw] = string.IsNullOrWhiteSpace(v2) ? "" : v2.Trim();
                sources[raw] = string.IsNullOrWhiteSpace(v2) ? "" : src2;
            }

            return result;
        }

        // ================================================================ 同名工程图

        /// <summary>是否存在同名工程图（.exb，CAXA 3D 图纸格式）。</summary>
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

        /// <summary>
        /// 同名 ics 文件夹内是否存在同名 .exb（顶层装配体的工程图常随装配件放在同名文件夹内）。
        /// 依次查 &lt;目录&gt;\&lt;基名&gt;\&lt;基名&gt;.exb 与 &lt;目录&gt;\&lt;基名&gt;.ics\&lt;基名&gt;.exb。
        /// </summary>
        public static bool HasDrawingInAssemblyFolder(string modelFile)
        {
            if (string.IsNullOrEmpty(modelFile)) return false;
            try
            {
                string dir = Path.GetDirectoryName(modelFile);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                string baseName = Path.GetFileNameWithoutExtension(modelFile);
                if (string.IsNullOrEmpty(baseName)) return false;
                string fileName = Path.GetFileName(modelFile);
                string exb = baseName + ".exb";
                string[] folders = { baseName, fileName };
                foreach (string f in folders)
                {
                    string sub = Path.Combine(dir, f);
                    if (!Directory.Exists(sub)) continue;
                    if (File.Exists(Path.Combine(sub, exb))) return true;
                }
                return false;
            }
            catch { return false; }
        }

        // ================================================================ 缩略图

        /// <summary>
        /// 抓取某个模型文件的预览图并存成 PNG；成功返回 PNG 路径，失败返回 null。
        /// 路径一：IZBaseApp::FilePreviewImage（interop 未声明，按 IDispatch 延迟调用）。
        /// 路径二：IZBaseApp::WritePreviewToFile → 解析预览流（BMP/PNG/JPG/DIB）。
        /// </summary>
        public string CaptureThumbnail(string filePath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(filePath)) { error = "没有可用文件路径"; return null; }

            string cached;
            if (_thumbCache.TryGetValue(filePath, out cached))
            {
                if (File.Exists(cached)) return cached;
                _thumbCache.Remove(filePath);
            }

            if (!File.Exists(filePath))
            {
                error = "文件不存在：" + filePath;
                return null;
            }

            Image img = null;
            string err1 = null, err2 = null;

            // ---- 路径一：FilePreviewImage ----
            try
            {
                object pic = ComLate.GetProperty(_app, "FilePreviewImage", filePath);
                if (pic != null) img = PictureToBitmap(pic);
                if (img == null) err1 = "FilePreviewImage 返回空或类型不支持";
            }
            catch (Exception ex)
            {
                err1 = "FilePreviewImage 失败：" + ex.Message;
                Diagnostics.Trace(err1);
            }

            // ---- 路径二：WritePreviewToFile ----
            if (img == null)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "caxa3dTreeBom_preview.bin");
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    _app.WritePreviewToFile(filePath, tmp);
                    if (File.Exists(tmp))
                    {
                        img = DecodePreviewFile(tmp, out err2);
                    }
                    else err2 = "WritePreviewToFile 未生成输出文件";
                }
                catch (Exception ex)
                {
                    err2 = "WritePreviewToFile 失败：" + ex.Message;
                    Diagnostics.Trace(err2);
                }
            }

            if (img == null)
            {
                error = err1 + " / " + err2;
                return null;
            }

            try
            {
                string png = Path.Combine(ThumbnailDir, Sanitize(Path.GetFileNameWithoutExtension(filePath)) + ".png");
                string png2 = UniquePath(png, filePath);
                using (img)
                {
                    using (var bmp = new Bitmap(img.Width, img.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.White);
                            g.DrawImage(img, 0, 0, img.Width, img.Height);
                        }
                        bmp.Save(png2, ImageFormat.Png);
                    }
                }
                _thumbCache[filePath] = png2;
                return png2;
            }
            catch (Exception ex)
            {
                error = "保存 PNG 失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>同名文件（不同目录）不覆盖：用路径哈希做后缀。</summary>
        private static string UniquePath(string png, string source)
        {
            if (!File.Exists(png)) return png;
            int h = StringComparer.OrdinalIgnoreCase.GetHashCode(source);
            string dir = Path.GetDirectoryName(png);
            string name = Path.GetFileNameWithoutExtension(png);
            return Path.Combine(dir, name + "_" + (h & 0x7FFFFFFF).ToString("X") + ".png");
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "thumb";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>把 IPictureDisp（延迟绑定得到的 RCW）转成 Bitmap。</summary>
        private static Bitmap PictureToBitmap(object picture)
        {
            try
            {
                int type = Convert.ToInt32(ComLate.GetProperty(picture, "Type"));
                int handle = Convert.ToInt32(ComLate.GetProperty(picture, "Handle"));
                if (handle == 0) return null;

                IntPtr h = new IntPtr(handle);
                if (type == 1)   // PICTYPE_BITMAP
                {
                    using (Image tmp = Image.FromHbitmap(h))
                        return new Bitmap(tmp);
                }
                if (type == 3)   // PICTYPE_ICON
                {
                    using (Icon ico = Icon.FromHandle(h))
                        return ico.ToBitmap();
                }
                Diagnostics.Trace("PictureToBitmap 不支持的图片类型：" + type);
                return null;
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("PictureToBitmap 失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>解析 WritePreviewToFile 的输出（BMP/PNG/JPG/GIF 直接解码；裸 DIB 补文件头）。</summary>
        private static Image DecodePreviewFile(string path, out string error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 16) { error = "预览数据过短(" + data.Length + " 字节)"; return null; }

                // 首次运行把文件头写进 trace，便于后续按实测结果调整
                Diagnostics.Trace("预览文件头: " + BytesToHex(data, Math.Min(32, data.Length))
                                  + "  size=" + data.Length);

                using (var ms = new MemoryStream(data))
                {
                    if (IsImageMagic(data))
                    {
                        using (Image tmp = Image.FromStream(ms))
                            return new Bitmap(tmp);
                    }
                }

                // 裸 DIB（BITMAPINFOHEADER 起始）→ 补 14 字节 BMP 文件头
                int biSize = BitConverter.ToInt32(data, 0);
                if (biSize == 40 || biSize == 108 || biSize == 124)
                {
                    int bitCount = BitConverter.ToInt16(data, 14);
                    int clrUsed = BitConverter.ToInt32(data, 32);
                    int palette = 0;
                    if (bitCount <= 8)
                        palette = (clrUsed > 0 ? clrUsed : (1 << bitCount)) * 4;

                    int offset = 14 + biSize + palette;
                    int total = 14 + data.Length;
                    var bmp = new byte[total];
                    bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
                    BitConverter.GetBytes(total).CopyTo(bmp, 2);
                    BitConverter.GetBytes(offset).CopyTo(bmp, 10);
                    data.CopyTo(bmp, 14);
                    using (var ms = new MemoryStream(bmp))
                    using (Image tmp = Image.FromStream(ms))
                        return new Bitmap(tmp);
                }

                error = "无法识别的预览格式（头部 " + BytesToHex(data, 8) + "）";
                return null;
            }
            catch (Exception ex)
            {
                error = "解析预览文件异常：" + ex.Message;
                return null;
            }
        }

        private static bool IsImageMagic(byte[] d)
        {
            if (d.Length < 8) return false;
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;   // PNG
            if (d[0] == 0x42 && d[1] == 0x4D) return true;                                   // BMP
            if (d[0] == 0xFF && d[1] == 0xD8) return true;                                   // JPG
            if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return true;                   // GIF
            return false;
        }

        private static string BytesToHex(byte[] d, int n)
        {
            var sb = new System.Text.StringBuilder(n * 3);
            for (int i = 0; i < n; i++) sb.Append(d[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// IDispatch 延迟调用（处理 interop 程序集未声明的成员，如 IZBaseApp::FilePreviewImage）。
    /// IZBaseApp 为 Dual 接口（含 IDispatch），可直接按名字取属性。
    /// </summary>
    internal static class ComLate
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPPARAMS
        {
            public IntPtr rgvarg;
            public IntPtr rgdispidNamedArgs;
            public int cArgs;
            public int cNamedArgs;
        }

        [ComImport, Guid("00020400-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDispatch
        {
            [PreserveSig] int GetTypeInfoCount(out uint pctinfo);
            [PreserveSig] int GetTypeInfo(uint iTInfo, uint lcid, out IntPtr ppTInfo);
            [PreserveSig] int GetIDsOfNames(ref Guid riid, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] rgszNames, uint cNames, uint lcid, [Out] int[] rgDispId);
            [PreserveSig] int Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags,
                                     ref DISPPARAMS pDispParams, IntPtr pVarResult,
                                     IntPtr pExcepInfo, IntPtr puArgErr);
        }

        private const ushort DISPATCH_PROPERTYGET = 2;
        private const int VariantSize = 24;

        /// <summary>按名字读取 COM 属性（支持带参数的属性，如 FilePreviewImage(path)）。</summary>
        public static object GetProperty(object comObject, string name, params object[] args)
        {
            if (comObject == null) throw new ArgumentNullException("comObject");

            IDispatch disp = comObject as IDispatch;
            if (disp == null) throw new InvalidCastException("对象不是 IDispatch 接口：" + comObject.GetType().FullName);

            var rgDispId = new int[1];
            Guid iidNull = Guid.Empty;
            int hr = disp.GetIDsOfNames(ref iidNull, new[] { name }, 1, 0, rgDispId);
            if (hr != 0) throw new COMException("GetIDsOfNames(" + name + ") 失败", hr);

            int argc = args == null ? 0 : args.Length;
            IntPtr argBlock = IntPtr.Zero;
            IntPtr varResult = IntPtr.Zero;
            try
            {
                if (argc > 0)
                {
                    argBlock = Marshal.AllocCoTaskMem(VariantSize * argc);
                    for (int i = 0; i < argc; i++)
                    {
                        // DISPPARAMS.rgvarg 是逆序
                        object a = args[argc - 1 - i];
                        Marshal.GetNativeVariantForObject(a, argBlock + i * VariantSize);
                    }
                }

                var dp = new DISPPARAMS
                {
                    rgvarg = argBlock,
                    rgdispidNamedArgs = IntPtr.Zero,
                    cArgs = argc,
                    cNamedArgs = 0
                };

                varResult = Marshal.AllocCoTaskMem(VariantSize);
                for (int i = 0; i < VariantSize; i++) Marshal.WriteByte(varResult, i, 0);

                hr = disp.Invoke(rgDispId[0], ref iidNull, 0, DISPATCH_PROPERTYGET,
                                 ref dp, varResult, IntPtr.Zero, IntPtr.Zero);
                if (hr != 0) throw new COMException("Invoke(" + name + ") 失败", hr);

                return Marshal.GetObjectForNativeVariant(varResult);
            }
            finally
            {
                if (varResult != IntPtr.Zero)
                {
                    try { Marshal.FreeCoTaskMem(varResult); } catch { }
                }
                if (argBlock != IntPtr.Zero)
                {
                    for (int i = 0; i < (args == null ? 0 : args.Length); i++)
                    {
                        try { VariantClear(argBlock + i * VariantSize); } catch { }
                    }
                    Marshal.FreeCoTaskMem(argBlock);
                }
            }
        }

        [DllImport("oleaut32.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int VariantClear(IntPtr pvarg);
    }
}
