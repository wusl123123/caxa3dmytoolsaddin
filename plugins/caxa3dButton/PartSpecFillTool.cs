// 自制件规格材料填写（用户 2026-11-08 19:59 规则 + 20:12 增补：返填通道与圆柱体校验）：
//   识别选中零件的第一特征，按特征名走三规则，把规格/材料返填到共享自定义属性表的 SC规格/SC材料
//   （BOM 截图两列即此表来源；参考项目 = C:\code\ICAPI\addin\caxa3dAttributeManager，其 GetValueByName/
//   SetValueByName 通道已实机验证）。
//
// 规则（所有数据 mm，保留 1 位小数如 123.1）：
//   ① 第一特征名「长方体」：盒三维降序（长≥宽≥高）；
//      SC规格 = "<长>x<宽>"（小写 x，与钣金件规格同款 100x50）；SC材料 = "<材料>板T=<高>"（材料从 BOM 导入数据）
//   ② 第一特征名「圆柱体」：抓取第一特征包围盒三尺寸，两个近似相等（tol=0.5mm）者为直径、
//      第三个不同者为高（圆钢长度）；无两两相等或三者全相等则拒填（非圆柱体判定）；
//      SC规格 = "L=<高>"；SC材料 = "<材料>圆钢φ<直径>"
//   ③ 第一特征名「抽壳圆管/抽壳圆钢」：盒 长=宽（=外径）、高=长度；第二特征名「抽壳1」抓壁厚；
//      SC规格 = "L=<高>"；SC材料 = "<材料>圆管φ<长>x<壁厚>"
//
// 返填通道（2026-11-08 20:12 定案，全抄参考项目实机先例；本工程进程内 vtable 直调全家有先例）：
//   共享属性表获取 → 零件 QI IZElement(69EF5583) → GetCustomPropManager(selector 0/1)（IZElement 槽42，
//                    类型库描述正确，优先）→ IsSharedMgr（IZCustomPropMgr 槽7）=true 才用；
//                    全失败回退 get_CustomPropMgr(vbShared=-1)（IZElement 槽24）+ 同样 IsSharedMgr 校验；
//                    【共享表铁律】三候选都拿不到 IsSharedMgr=true 就一根手指都不动（参考项目 14.7/14.9 先例）
//   属性读         → IZCustomPropMgr.GetValueByName（槽9，BSTR in + VARIANT out）
//   属性写         → IZCustomPropMgr.SetValueByName（槽10，BSTR in + VARIANT 按值；x64 下 VARIANT 以指针
//                    传临时副本 ABI——参考项目 FnSetValueByName 同款）；失败（属性不存在）→ AddCustomProp
//                    （槽14，vbReplaceExistingOne=true）建立——caxa3dBatchMaster CaxaPropWrite.WriteOne 回退先例
//   材料（20:35 定案，参考 caxa3dBatchMaster 取「材料」）→ ① 共享表「材料/Material/材质」精确读；
//                    ② GetMaterialNameProperty（API 一手，diffrename SheetSizeTool 先例）；③ 参数表回落（只读）
//   数值格式（20:35 定案）→ mm 1 位小数四舍五入末尾消零（FormatMm：123.0→"123"；
//                    参考 caxa3ddiffrename 钣金件尺寸填写）
// 铁律：写入判据 = SetValueByName 后 GetValueByName 回读逐字一致；HRESULT 不算成功。
//       v26 interop IZFeature 无 Name 属性（CS1061 先例族）⇒ 特征名 QI IZElement 槽9 直调。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    internal static class PartSpecFillTool
    {
        // 特征名读取：QI IZElement 槽直调（RenameFeature 同款：IID_IZElement=69EF5583，get_Name=槽9）
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnElementGetName(IntPtr self, [MarshalAs(UnmanagedType.BStr)] out string pbstrName);

        private static readonly Guid IID_IZElement = new Guid("69EF5583-A26D-4913-A2F6-DBFF73E1B8A3");
        private const int SlotElementGetName = 9;

        // ---- 共享 CustomPropMgr 通道（槽位与 IZElement/IZCustomPropMgr vtable 对齐，参考项目同源）----
        private const int SlotElemGetCustomPropManager = 42; // IZElement::GetCustomPropManager(int)（优先，类型库描述正确）
        private const int SlotElemGetCustomPropMgr = 24;     // IZElement::get_CustomPropMgr(vbShared)（回退）
        private const int SlotCpmIsSharedMgr = 7;            // IZCustomPropMgr::IsSharedMgr
        private const int SlotCpmGetValueByName = 9;         // IZCustomPropMgr::GetValueByName
        private const int SlotCpmSetValueByName = 10;        // IZCustomPropMgr::SetValueByName
        private const int SlotCpmAddCustomProp = 14;         // IZCustomPropMgr::AddCustomProp（属性不存在时的回退）

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetShort(IntPtr self, out short pv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetCustomPropManager(IntPtr self, int selector, out IntPtr ppMgr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetCustomPropMgr(IntPtr self, short vbShared, out IntPtr ppMgr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetValueByName(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pVar);
        // SetValueByName(BSTR, VARIANT)：VARIANT 按值传递；x64 下 >8 字节结构实为「指向调用方临时副本的指针」，
        // 因此用 IntPtr 声明与原生 ABI 一致（参考项目同款注释与做法）。
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetValueByName(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pVar);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnAddCustomProp(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pVar, short vbReplaceExistingOne);

        private const short VT_BSTR = 8;

        [DllImport("oleaut32.dll")]
        private static extern int VariantClear(IntPtr pvarg);

        /// <summary>读特征名（QI IZElement 槽9）；失败返回 "&lt;读不到&gt;"。</summary>
        /// <summary>取文档纯文件名（去目录、去 .ics 后缀），与钣金件尺寸填写同款逻辑。</summary>
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

        private static string FeatureName(IZFeature feat)
        {
            IntPtr pUnk = IntPtr.Zero;
            try { pUnk = Marshal.GetIUnknownForObject(feat); }
            catch { return "<读不到>"; }
            IntPtr pElem;
            try
            {
                if (ProbeCommon.QI(pUnk, IID_IZElement, out pElem) != 0 || pElem == IntPtr.Zero)
                    return "<QI失败>";
                try
                {
                    string name;
                    int hr = ProbeCommon.Fn<FnElementGetName>(pElem, SlotElementGetName)(pElem, out name);
                    return hr == 0 && name != null ? name : "<hr=" + hr.ToString("X8") + ">";
                }
                finally { Marshal.Release(pElem); }
            }
            finally { Marshal.Release(pUnk); }
        }

        public static string Run(IZBaseApp app)
        {
            const string tag = "spec-fill";
            Action<string> log = msg => Diagnostics.Log(msg);

            string err;
            IZSceneDoc scene = ProbeCommon.GetScene(app, out err);
            if (scene == null) return "填写失败：" + err;

            List<object> sel = ProbeCommon.ReadSelectionRaw(scene, tag, log);
            if (sel.Count == 0)
                return "未选中任何元素——请先选中自制件零件（单选），再点「自制件规格材料填写」。";

            // 取第一个零件（多选时只处理第一个，其余提示）
            IZPart part = null;
            string partName = "";
            int extra = 0;
            foreach (object o in sel)
            {
                var el = o as IZElement;
                if (el == null) continue;
                int type = -1;
                try { type = (int)el.Type; } catch { }
                if (type == 2 /*Z_ELEMENT_ASSEMBLY*/) { extra++; continue; }   // 装配排除（口径同 ComponentTool）
                var p = o as IZPart;
                if (p == null) { extra++; continue; }
                if (part == null) { part = p; partName = ProbeCommon.Safe(() => el.Name, "<无名>"); }
                else extra++;
            }
            if (part == null)
                return "选中对象里没有零件（装配已排除）——请直接选中自制件零件后再点。";

            log(tag + ": 目标零件「" + partName + "」");

            // ---- 0) 虚拟件状态校验（参考钣金件尺寸填写，只允许虚拟零部件填写）----
            string docBase = "";
            try { var adoc = app.ActiveDoc; if (adoc != null) docBase = DocBaseName(adoc.Name ?? ""); }
            catch (Exception ex) { log(tag + ": 读取活动文档名 EX " + ProbeCommon.Hr(ex)); }
            string reason;
            if (!PassVirtualCheck(part, partName, docBase, log, out reason))
                return reason;

            bool ok;
            return CoreFill(app, part, partName, log, out ok);
        }

        // ---------------------------------------------------------------- 核心（供 Run 与 FillSpecForElement 共用）

        /// <summary>核心：特征枚举 → 包围盒 → 材料 → 三规则 → 共享表返填 + 回读判据（供 Run 与 FillSpecForElement 共用）。
        /// ok=true 仅表示共享表 SC规格/SC材料 已写入且回读一致；其余所有分支 ok=false（含中止/第三态）。</summary>
        private static string CoreFill(IZBaseApp app, IZPart part, string partName, Action<string> log, out bool ok)
        {
            ok = false;   // 默认失败；下面所有提前 return 均保持 false
            const string tag = "spec-fill";
            // ---- 1) 特征枚举（零件 QI IZPartFeatureMgr）----
            IZPartFeatureMgr fm;
            try { fm = part as IZPartFeatureMgr; }
            catch { fm = null; }
            if (fm == null)
                return "零件「" + partName + "」QI IZPartFeatureMgr 失败（第三态）——特征枚举通道不可用，详见 trace。";

            int fcount = 0;
            try { fcount = fm.GetFeaturesCount(); }   // 槽1
            catch (Exception ex) { log(tag + ": GetFeaturesCount EX " + ProbeCommon.Hr(ex)); }
            log(tag + ": 特征数=" + fcount);
            if (fcount < 1) return "零件「" + partName + "」没有特征（空零件？）——无法按规则填写。";

            var featNames = new List<string>();
            for (int i = 0; i < fcount && i < 16; i++)
            {
                try { featNames.Add(FeatureName(fm.GetFeatureAt(i))); }   // 槽3 + QI IZElement get_Name 槽9
                catch { featNames.Add("<EX>"); }
            }
            log(tag + ": 特征名表=[" + string.Join(", ", featNames.ToArray()) + "]");

            string name0 = featNames[0];

            // ---- 2) 第一特征包围盒（局部 AABB 6 值，米→mm，降序=长宽高）----
            IZFeature f0 = fm.GetFeatureAt(0);
            double[] box;
            string boxErr;
            if (!TryFeatureBoxMm(f0, out box, out boxErr))
                return "第一特征「" + name0 + "」包围盒读不到：" + boxErr + "——无法按规则填写（第三态）。";

            double[] d = { box[3] - box[0], box[4] - box[1], box[5] - box[2] };   // dx,dy,dz（mm）
            for (int i = 0; i < 3; i++) d[i] = Math.Abs(d[i]);
            Array.Sort(d);                                                        // 升序
            // 降序：长=d[2] 宽=d[1] 高=d[0]
            double L = d[2], W = d[1], H = d[0];
            log(tag + ": 第一特征「" + name0 + "」盒 mm dx/dy/dz=(" + fmt1(box[3] - box[0]) + ", "
                + fmt1(box[4] - box[1]) + ", " + fmt1(box[5] - box[2]) + ") → 降序 长/宽/高=("
                + fmt1(L) + ", " + fmt1(W) + ", " + fmt1(H) + ")");

            // ---- 3) 材料读取（BOM 导入落点：共享属性表「材料」，回落零件参数表）----
            string material = ReadMaterial(part, tag, log);

            // ---- 4) 三规则生成规格/材料（圆柱体含硬校验）----
            string spec, mat;
            if (name0.IndexOf("长方体", StringComparison.Ordinal) >= 0)
            {
                // ① 长方体：SC规格=长x宽（小写 x，与钣金件规格统一 100x50）；SC材料=材料板T=高
                spec = fmt1(L) + "x" + fmt1(W);
                mat = material + "板T=" + fmt1(H);
            }
            else if (name0.IndexOf("圆柱体", StringComparison.Ordinal) >= 0)
            {
                // ② 圆柱体（用户 07:34 口径）：抓取第一特征包围盒三尺寸（d0≤d1≤d2，mm），
                //    其中两个近似相等者为直径、第三个不同者为高（圆钢长度）。
                const double tol = 0.5;
                double d0 = d[0], d1 = d[1], d2 = d[2];
                bool pairHi = Math.Abs(d2 - d1) <= tol;   // 大两个相等
                bool pairLo = Math.Abs(d1 - d0) <= tol;   // 小两个相等
                double diam, height;
                if (pairHi && pairLo)
                    return "圆柱体校验不过：三尺寸均近似相等（" + fmt1(d0) + "/" + fmt1(d1) + "/" + fmt1(d2) +
                        "mm），无法确定直径与高（疑似立方体/球）——SC规格/SC材料 未写。";
                else if (pairHi)        { diam = d2; height = d0; }   // 直径=大二相等值，高=最小（不同）维
                else if (pairLo)        { diam = d1; height = d2; }   // 直径=小二相等值，高=最大（不同）维
                else
                    return "圆柱体校验不过：三尺寸无两个近似相等（" + fmt1(d0) + "/" + fmt1(d1) + "/" + fmt1(d2) +
                        "mm），不满足圆柱体判定（需两维=直径、一维=长度）——SC规格/SC材料 未写。";
                log(tag + ": 圆柱体判定：三尺寸升序(" + fmt1(d0) + "," + fmt1(d1) + "," + fmt1(d2) +
                    ") → 直径=" + fmt1(diam) + "，高(长度)=" + fmt1(height) + " ✓");
                spec = "L=" + fmt1(height);
                mat = material + "圆钢φ" + fmt1(diam);
            }
            else if (name0.IndexOf("抽壳", StringComparison.Ordinal) >= 0)
            {
                // ③ 抽壳圆管/抽壳圆钢：规格=L=高；材料=材料圆管φ长x壁厚（第二特征「抽壳1」抓壁厚）
                if (Math.Abs(L - W) > 0.5)
                    log(tag + ": ★抽壳校验：长(" + fmt1(L) + ")≠宽(" + fmt1(W) + ")>0.5mm——仍以长=外径填写（第三态留痕）");
                double wall;
                if (!TryReadWallThickness(fm, fcount, tag, log, out wall))
                    return "第一特征「" + name0 + "」识别成功（盒 长/宽/高=" + fmt1(L) + "/" + fmt1(W) + "/" + fmt1(H) +
                        "），但第二特征壁厚读不到（需特征名含「抽壳」且 QI IZShellFeature 成功）——SC规格/SC材料 未写（第三态）。";
                spec = "L=" + fmt1(H);
                mat = material + "圆管φ" + fmt1(L) + "x" + fmt1(wall);
            }
            else
            {
                return "第一特征名「" + name0 + "」不在规则表（长方体/圆柱体/抽壳圆管/抽壳圆钢）——SC规格/SC材料 未写。特征名表见 trace，如需扩展规则请告知特征名。";
            }

            log(tag + ": 规则生成 SC规格=\"" + spec + "\" SC材料=\"" + mat + "\"");

            // ---- 5) 返填共享属性表（SC规格/SC材料）＋回读判据 ----
            IntPtr pMgr = GetSharedCustomPropMgr((IZElement)part, tag, log);
            if (pMgr == IntPtr.Zero)
                return "共享自定义属性表（SC规格/SC材料 所在表，BOM 两列的数据源）获取失败（三个候选都无 IsSharedMgr=true）——按共享表铁律不写（第三态），详见 trace。";
            try
            {
                string wErr;
                if (!WriteSharedProps(pMgr, spec, mat, tag, log, out wErr))
                    return "规格/材料已生成（SC规格=\"" + spec + "\" SC材料=\"" + mat + "\"）但共享属性表返填失败：" + wErr;
            }
            finally { Marshal.Release(pMgr); }

            ok = true;   // 唯一成功分支：共享表回读一致
            return "「" + partName + "」已返填共享属性表：SC规格=\"" + spec + "\"；SC材料=\"" + mat + "\"（回读一致" +
                (material.Length == 0 ? "；★材料未读到（共享表/参数表均无「材料」——请确认 BOM 已导入），材料字段缺材料名" : "") + "）";
        }

        /// <summary>虚拟件状态校验（供 Run 与 FillSpecForElement 共用）：非外部链接件，且（docBase 非空时）零件名=文件名。
        /// 通过返回 true；否则返回 false 并带出 reason（完整结论文案）。</summary>
        private static bool PassVirtualCheck(IZPart part, string partName, string docBase, Action<string> log, out string reason)
        {
            reason = null;
            bool linked;
            string linkedFile;
            try { linkedFile = part.GetExternallyLinkedInfo(out linked) ?? ""; }
            catch { linked = false; linkedFile = ""; }
            if (linked && !string.IsNullOrEmpty(linkedFile))
            {
                reason = "「" + partName + "」是外部链接件（" + Path.GetFileName(linkedFile) + "），只允许虚拟零部件填写——SC规格/SC材料 未写。";
                log("spec-fill: 虚拟件校验：" + reason);
                return false;
            }
            if (docBase.Length > 0)
            {
                if (!string.Equals(docBase, partName, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "零件名「" + partName + "」与文件名「" + docBase + ".ics」不一致（必须满足 文件名=零件名），只允许虚拟件状态下填写——SC规格/SC材料 未写。";
                    log("spec-fill: 虚拟件校验：" + reason);
                    return false;
                }
                log("spec-fill: 虚拟件校验通过：文件名=零件名（" + docBase + "），非外部链接 ✓");
            }
            else
            {
                log("spec-fill: docBase 为空（取不到文件名，或调用方主动跳过同名校验，如装配内子件/打开工程图）——不做 文件名=零件名 校验，按非外部链接件放行");
            }
            return true;
        }

        /// <summary>
        /// 供「打开/关闭设计环境」调用：对指定元素执行自制件规格材料填写（不读选区、不依赖活动文档选区）。
        /// expectedDocBase 非空时强制 零件名=文件名 校验（只认同名虚拟件）；空串跳过该校验。
        /// 返回 null 表示不适用/跳过（非错误，如装配、非零件、外部链接、名称不符）；否则返回填写结论文案。
        /// </summary>
        public static string FillSpecForElement(IZBaseApp app, IZElement elem, string expectedDocBase, Action<string> log, out bool ok)
        {
            ok = false;
            int type = -1;
            try { type = (int)elem.Type; } catch { }
            if (type == 2)
            {
                log("spec-fill: 「" + ProbeCommon.Safe(() => elem.Name, "<无名>") + "」是装配，跳过自制件规格材料填写（不穿透装配）");
                return null;
            }

            var part = elem as IZPart;
            if (part == null) { log("spec-fill: 元素非零件（无 IZPart），跳过"); return null; }

            string partName = ProbeCommon.Safe(() => elem.Name, "<无名>");
            string reason;
            if (!PassVirtualCheck(part, partName, expectedDocBase, log, out reason))
            {
                log("spec-fill: 自制件规格材料填写跳过「" + partName + "」：" + reason);
                return null;
            }

            log("spec-fill: 对同名虚拟件「" + partName + "」执行自制件规格材料填写");
            return CoreFill(app, part, partName, log, out ok);
        }

        // ---------------------------------------------------------------- 特征包围盒

        /// <summary>IZFeature.GetBoundingBox(槽13) → 6 值局部 AABB（米）→ mm。</summary>
        private static bool TryFeatureBoxMm(IZFeature feat, out double[] boxMm, out string err)
        {
            boxMm = null;
            err = null;
            try
            {
                double[] v = ProbeCommon.ToDoubleArray(feat.GetBoundingBox());
                if (v == null || v.Length < 6) { err = "返回 " + (v == null ? "null" : v.Length + " 值") + "（需 6 值）"; return false; }
                boxMm = new double[6];
                for (int i = 0; i < 6; i++) boxMm[i] = v[i] * 1000.0;
                return true;
            }
            catch (Exception ex) { err = ProbeCommon.Hr(ex) + " " + ex.Message; return false; }
        }

        // ---------------------------------------------------------------- 抽壳壁厚

        /// <summary>第二特征（名含「抽壳」）QI IZShellFeature → get_Thickness（米）→ mm。</summary>
        private static bool TryReadWallThickness(IZPartFeatureMgr fm, int fcount, string tag, Action<string> log, out double wallMm)
        {
            wallMm = 0;
            if (fcount < 2) { log(tag + ": 壁厚：特征数<2，无第二特征"); return false; }
            IZFeature f1;
            try { f1 = fm.GetFeatureAt(1); }
            catch (Exception ex) { log(tag + ": 壁厚：GetFeatureAt(1) EX " + ProbeCommon.Hr(ex)); return false; }
            string n1 = FeatureName(f1);
            log(tag + ": 第二特征名=\"" + n1 + "\"");
            if (n1.IndexOf("抽壳", StringComparison.Ordinal) < 0)
            {
                log(tag + ": 壁厚：第二特征名不含「抽壳」（用户口径=「抽壳1」）——如实不取");
                // 仍尝试 QI（名字判据不否决通道）
            }
            try
            {
                var shell = f1 as IZShellFeature;
                if (shell == null) { log(tag + ": 壁厚：第二特征 QI IZShellFeature 失败（iid=6F58AED4）"); return false; }
                double t = shell.Thickness;   // 槽1，米
                wallMm = t * 1000.0;
                log(tag + ": 壁厚 get_Thickness=" + fmt1(wallMm) + " mm（COM 原值=米）");
                return wallMm > 0;
            }
            catch (Exception ex) { log(tag + ": 壁厚 get_Thickness EX " + ProbeCommon.Hr(ex)); return false; }
        }

        // ---------------------------------------------------------------- 共享 CustomPropMgr（返填通道）

        /// <summary>
        /// 获取零件上的「共享」CustomPropMgr（参考项目 GetSharedCustomPropMgr 同款）：
        /// 优先 GetCustomPropManager(selector 0/1)+IsSharedMgr 校验；回退 get_CustomPropMgr(vbShared=-1)+同校验。
        /// 三候选都拿不到 IsSharedMgr=true 返回 Zero——共享表铁律：绝不拿零件级表凑数。
        /// </summary>
        private static IntPtr GetSharedCustomPropMgr(IZElement elem, string tag, Action<string> log)
        {
            IntPtr pUnk = IntPtr.Zero;
            try { pUnk = Marshal.GetIUnknownForObject(elem); }
            catch (Exception ex) { log(tag + ": 共享表：GetIUnknownForObject EX " + ProbeCommon.Hr(ex)); return IntPtr.Zero; }

            IntPtr pElem = IntPtr.Zero;
            try
            {
                if (ProbeCommon.QI(pUnk, IID_IZElement, out pElem) != 0 || pElem == IntPtr.Zero)
                {
                    log(tag + ": 共享表：零件 QI IZElement(69EF5583) 失败");
                    return IntPtr.Zero;
                }

                // 优先：GetCustomPropManager(selector)（IZElement 槽42，类型库描述正确）
                foreach (int selector in new int[] { 0, 1 })
                {
                    IntPtr pMgr;
                    try
                    {
                        int hr = ProbeCommon.Fn<FnGetCustomPropManager>(pElem, SlotElemGetCustomPropManager)(pElem, selector, out pMgr);
                        log(tag + ": 共享表：GetCustomPropManager(selector=" + selector + ") hr=0x" + hr.ToString("X") + " ptr=0x" + pMgr.ToInt64().ToString("X"));
                        if (hr != 0 || pMgr == IntPtr.Zero) continue;
                        if (IsSharedMgr(pMgr, tag, log)) return pMgr;
                        Marshal.Release(pMgr);
                    }
                    catch (Exception ex) { log(tag + ": 共享表：GetCustomPropManager(" + selector + ") EX " + ProbeCommon.Hr(ex)); }
                }

                // 回退：get_CustomPropMgr(vbShared=-1)（IZElement 槽24；部分安装类型库描述有误，名义结果不作数）
                try
                {
                    IntPtr pMgr;
                    int hr = ProbeCommon.Fn<FnGetCustomPropMgr>(pElem, SlotElemGetCustomPropMgr)(pElem, (short)(-1), out pMgr);
                    log(tag + ": 共享表：get_CustomPropMgr(vbShared=-1) hr=0x" + hr.ToString("X") + " ptr=0x" + pMgr.ToInt64().ToString("X"));
                    if (hr == 0 && pMgr != IntPtr.Zero)
                    {
                        if (IsSharedMgr(pMgr, tag, log)) return pMgr;
                        Marshal.Release(pMgr);
                    }
                }
                catch (Exception ex) { log(tag + ": 共享表：get_CustomPropMgr EX " + ProbeCommon.Hr(ex)); }

                log(tag + ": 共享表：三个候选都没有 IsSharedMgr=true——按共享表铁律不读写");
                return IntPtr.Zero;
            }
            finally
            {
                if (pElem != IntPtr.Zero) Marshal.Release(pElem);
                Marshal.Release(pUnk);
            }
        }

        /// <summary>IZCustomPropMgr::IsSharedMgr（槽7）。调用失败=false。</summary>
        private static bool IsSharedMgr(IntPtr pMgr, string tag, Action<string> log)
        {
            try
            {
                short shared;
                int hr = ProbeCommon.Fn<FnGetShort>(pMgr, SlotCpmIsSharedMgr)(pMgr, out shared);
                log(tag + ": 共享表：IsSharedMgr hr=0x" + hr.ToString("X") + " value=" + shared);
                return hr == 0 && shared != 0;
            }
            catch (Exception ex) { log(tag + ": 共享表：IsSharedMgr EX " + ProbeCommon.Hr(ex)); return false; }
        }

        /// <summary>GetValueByName（槽9，VARIANT out）→ 字符串。ok=true 表示调用成功（空值也算）。</summary>
        private static bool SharedGetValueByName(IntPtr pMgr, string name, out string value)
        {
            value = null;
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                int hr = ProbeCommon.Fn<FnGetValueByName>(pMgr, SlotCpmGetValueByName)(pMgr, name, pVar);
                if (hr != 0) { Diagnostics.Log("spec-fill: 共享表：GetValueByName(" + name + ") hr=0x" + hr.ToString("X")); return false; }
                value = VariantToString(pVar);
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("spec-fill: 共享表：GetValueByName(" + name + ") EX " + ProbeCommon.Hr(ex));
                return false;
            }
            finally { VariantClear(pVar); Marshal.FreeCoTaskMem(pVar); }
        }

        /// <summary>SetValueByName（槽10，VT_BSTR VARIANT 按值→指针临时副本 ABI）。</summary>
        private static bool SharedSetValueByName(IntPtr pMgr, string name, string value)
        {
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                Marshal.WriteInt16(pVar, 0, VT_BSTR);                       // vt 在偏移 0
                Marshal.WriteIntPtr(pVar, 8, Marshal.StringToBSTR(value ?? ""));   // 数据指针在偏移 8
                int hr = ProbeCommon.Fn<FnSetValueByName>(pMgr, SlotCpmSetValueByName)(pMgr, name, pVar);
                Diagnostics.Log("spec-fill: 共享表：SetValueByName(" + name + ") hr=0x" + hr.ToString("X"));
                return hr == 0;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("spec-fill: 共享表：SetValueByName(" + name + ") EX " + ProbeCommon.Hr(ex));
                return false;
            }
            finally { VariantClear(pVar); Marshal.FreeCoTaskMem(pVar); }
        }

        /// <summary>x64 VARIANT（16 字节，此处分配 24 留余量）→ 显示字符串（常用 vt 全覆盖）。</summary>
        private static string VariantToString(IntPtr pVar)
        {
            short vt = Marshal.ReadInt16(pVar);
            switch (vt)
            {
                case 0: case 1: return "";                                 // VT_EMPTY / VT_NULL
                case 2: return Marshal.ReadInt16(pVar, 8).ToString();      // VT_I2
                case 3: return Marshal.ReadInt32(pVar, 8).ToString();      // VT_I4
                case 4:                                                    // VT_R4
                    {
                        byte[] b = new byte[4];
                        Marshal.Copy(pVar + 8, b, 0, 4);
                        return BitConverter.ToSingle(b, 0).ToString(CultureInfo.InvariantCulture);
                    }
                case 5:                                                    // VT_R8
                    {
                        byte[] b = new byte[8];
                        Marshal.Copy(pVar + 8, b, 0, 8);
                        return BitConverter.ToDouble(b, 0).ToString(CultureInfo.InvariantCulture);
                    }
                case 8:                                                    // VT_BSTR
                    {
                        IntPtr pBstr = Marshal.ReadIntPtr(pVar, 8);
                        return pBstr == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(pBstr);
                    }
                case 11: return Marshal.ReadInt16(pVar, 8) != 0 ? "True" : "False";   // VT_BOOL
                default: return "<vt=0x" + vt.ToString("X") + ">";
            }
        }

        /// <summary>SC规格/SC材料 返填：SetValueByName 写入 → GetValueByName 回读逐字比对。</summary>
        private static bool WriteSharedProps(IntPtr pMgr, string spec, string mat, string tag, Action<string> log, out string err)
        {
            err = null;
            if (!EnsureSharedProp(pMgr, "SC规格", spec, tag, log, out err)) return false;
            return EnsureSharedProp(pMgr, "SC材料", mat, tag, log, out err);
        }

        /// <summary>
        /// 公开：把一组「字段名→值」写进零件的共享自定义属性表（图号分离移植用）。
        /// 复用本类已实机验证的 GetSharedCustomPropMgr + EnsureSharedProp（SetValueByName→AddCustomProp 回退→回读判据）。
        /// 任一字段写失败即返回 false 并带出 err；共享表拿不到按铁律不动。
        /// </summary>
        public static bool WriteNamedSharedProps(IZElement elem, Dictionary<string, string> values, string tag, Action<string> log, out string err)
        {
            err = null;
            IntPtr pMgr = GetSharedCustomPropMgr(elem, tag, log);
            if (pMgr == IntPtr.Zero)
            {
                err = "共享自定义属性表获取失败（三候选都无 IsSharedMgr=true），按共享表铁律不写";
                return false;
            }
            try
            {
                foreach (var kv in values)
                {
                    string e;
                    if (!EnsureSharedProp(pMgr, kv.Key, kv.Value, tag, log, out e)) { err = e; return false; }
                }
                return true;
            }
            finally { Marshal.Release(pMgr); }
        }

        private static bool EnsureSharedProp(IntPtr pMgr, string name, string value, string tag, Action<string> log, out string err)
        {
            err = null;
            string before;
            bool readOk = SharedGetValueByName(pMgr, name, out before);
            log(tag + ": 「" + name + "」原值=" + (readOk ? "\"" + before + "\"" : "<读不到/不存在>"));

            if (!SharedSetValueByName(pMgr, name, value))
            {
                // 属性名在该元素上还不存在时 SetValueByName 会失败 → AddCustomProp（槽14，
                // vbReplaceExistingOne=true 同名覆盖；BatchMaster CaxaPropWrite.WriteOne 实机回退先例）
                log(tag + ": SetValueByName(" + name + ") 失败 → 回退 AddCustomProp(槽14)");
                if (!SharedAddCustomProp(pMgr, name, value))
                {
                    err = "SetValueByName 与 AddCustomProp(槽14) 均失败（hr 见 trace）";
                    log(tag + ": " + err);
                    return false;
                }
            }

            // 回读判据：逐字一致
            string got;
            bool ok = SharedGetValueByName(pMgr, name, out got) && string.Equals(got, value, StringComparison.Ordinal);
            log(tag + ": 「" + name + "」回读=\"" + got + "\"（" + (ok ? "一致" : "不一致") + "）");
            if (!ok) err = "「" + name + "」写后回读不一致（回读=\"" + got + "\"）";
            return ok;
        }

        /// <summary>AddCustomProp（槽14，VT_BSTR VARIANT + vbReplaceExistingOne=true）：属性不存在时建立。</summary>
        private static bool SharedAddCustomProp(IntPtr pMgr, string name, string value)
        {
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                Marshal.WriteInt16(pVar, 0, VT_BSTR);
                Marshal.WriteIntPtr(pVar, 8, Marshal.StringToBSTR(value ?? ""));
                int hr = ProbeCommon.Fn<FnAddCustomProp>(pMgr, SlotCpmAddCustomProp)(pMgr, name, pVar, (short)(-1));
                Diagnostics.Log("spec-fill: 共享表：AddCustomProp(" + name + ") hr=0x" + hr.ToString("X"));
                return hr == 0;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("spec-fill: 共享表：AddCustomProp(" + name + ") EX " + ProbeCommon.Hr(ex));
                return false;
            }
            finally { VariantClear(pVar); Marshal.FreeCoTaskMem(pVar); }
        }

        // ---------------------------------------------------------------- 材料读取

        /// <summary>
        /// 材料读取三级（用户 20:35 指令：获取「材料」属性参考 caxa3dBatchMaster）：
        /// ① 共享属性表「材料/Material/材质」（BatchMaster 通道=共享表铁律内按名精确读）；
        /// ② GetMaterialNameProperty（API 一手材料名，caxa3ddiffrename SheetSizeTool 实机先例）；
        /// ③ 零件参数表回落（旧通道，只读）。都读不到返回空串并在结果注明。
        /// </summary>
        private static string ReadMaterial(IZPart part, string tag, Action<string> log)
        {
            string[] candidates = { "材料", "Material", "材质" };

            // 第一优先：共享属性表（与 SC规格/SC材料 同表；BatchMaster TryGetCustomProp 同语义）
            IntPtr pMgr = GetSharedCustomPropMgr((IZElement)part, tag + "-mat", log);
            if (pMgr != IntPtr.Zero)
            {
                try
                {
                    foreach (string c in candidates)
                    {
                        string v;
                        if (SharedGetValueByName(pMgr, c, out v) && v.Length > 0)
                        {
                            log(tag + ": 材料（共享表「" + c + "」）=\"" + v + "\"");
                            return v;
                        }
                    }
                    log(tag + ": 材料：共享表候选全未命中（" + string.Join("/", candidates) + "）");
                }
                finally { Marshal.Release(pMgr); }
            }

            // 第二优先：零件材料名称属性（API 一手）
            try
            {
                string matName;
                ((IZElement)part).GetMaterialNameProperty(out matName);
                if (!string.IsNullOrWhiteSpace(matName))
                {
                    log(tag + ": 材料（GetMaterialNameProperty）=\"" + matName.Trim() + "\"");
                    return matName.Trim();
                }
                log(tag + ": 材料：GetMaterialNameProperty 返回空");
            }
            catch (Exception ex) { log(tag + ": GetMaterialNameProperty EX " + ProbeCommon.Hr(ex)); }

            // 回落：零件参数表（旧通道，只读不写）
            IZParameterMgr pm = PartParamMgr(part, tag, log);
            if (pm != null)
            {
                foreach (string c in candidates)
                {
                    string v = ReadTextParam(pm, c, tag, log);
                    if (v.Length > 0) { log(tag + ": 材料（参数表「" + c + "」）=\"" + v + "\""); return v; }
                }

                // 全枚举留痕（校准 BOM 导入后材料的实际参数名）
                try
                {
                    int n = pm.Count;
                    log(tag + ": 材料未命中；零件参数表 Count=" + n + "：");
                    for (int i = 0; i < n && i < 32; i++)
                    {
                        IZParameter p = pm.GetParameter(i);
                        if (p == null) continue;
                        string pn = ProbeCommon.Safe(() => p.Name, "<读不到>");
                        string tx = ProbeCommon.Safe(() => p.TextParameterValue, "");
                        log(tag + ":   param[" + i + "] Name=\"" + pn + "\" Text=\"" + tx + "\"");
                    }
                }
                catch (Exception ex) { log(tag + ": 参数表全枚举 EX " + ProbeCommon.Hr(ex)); }
            }
            return "";
        }

        private static IZParameterMgr PartParamMgr(IZPart part, string tag, Action<string> log)
        {
            var se = part as IZSceneElement;
            if (se == null) { log(tag + ": 零件 QI IZSceneElement 失败——参数表回落通道不可用"); return null; }
            try { return se.ParameterMgr; }   // 槽19（ParameterTableProbe 读先例）
            catch (Exception ex) { log(tag + ": get_ParameterMgr EX " + ProbeCommon.Hr(ex)); return null; }
        }

        private static string ReadTextParam(IZParameterMgr pm, string name, string tag, Action<string> log)
        {
            try
            {
                IZParameter p = pm.GetParameterByName(name);   // 槽3
                if (p == null) return "";
                string t = ProbeCommon.Safe(() => p.TextParameterValue, "");   // 槽9
                if (t.Length > 0) return t;
                return ProbeCommon.Safe(() => p.Value.ToString(CultureInfo.InvariantCulture), "");   // 数值参数兜底
            }
            catch (Exception ex) { log(tag + ": GetParameterByName(\"" + name + "\") EX " + ProbeCommon.Hr(ex)); return ""; }
        }

        /// <summary>mm、1 位小数、四舍五入、末尾消零（123.0→"123"、123.35→"123.4"）——
        /// 用户 20:35 指令：消零处理参考 caxa3ddiffrename 钣金件尺寸填写（SheetSizeTool.FormatMm 原样）。</summary>
        private static string fmt1(double v)
        {
            double r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
            if (Math.Abs(r) < 0.05) r = 0;
            return r.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
