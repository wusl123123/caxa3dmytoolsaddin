// 打开/关闭设计环境（用户 11-09 需求）：
//   打开设计环境：若当前设计环境内选中了外部链接件 → 直接 OpenFile2 打开其外链文件（逐级钻入装配外链子件）；
//       其余情况 → Win 打开选择框选 .ics；打开后遍历该设计环境【所有顶层零部件】（不穿透装配）：
//       ① 对每个非装配件按零件名自动选择图号分离规则并执行（复用 DrawingNoSepTool.SeparatePart + AutoSelectRule）；
//       ② 对与文件名同名的虚拟件回填规格/材料（零件→PartSpecFillTool 自制件规格材料填写；钣金→SheetSizeTool 钣金件尺寸填写）。
//   关闭设计环境：弹选择「关闭当前 / 关闭所有」：
//       关闭当前 ＝ 前台（活动）.ics；关闭所有 ＝ 本工具记录 ＋ GetOpenDocs 枚举的手动 .ics 合并，按时间线从新到旧逐个关闭
//               （手动打开无记录时间者排在末尾并标注"手动打开"）；关闭前对文档做同样的 ProcessComponents（图号分离＋同名虚拟件规格/材料回填），再 Close2 关文档。
// 通道取证（零猜测）：
//   打开   → IZBaseApp.OpenFile2（槽30，与预热/打开工程图同款；热路径异常只记日志，按活动文档名差集判定）
//   关闭   → IZDoc.Close2(-1)（槽23，参考 caxa3ddiffrename SheetMetalOps：Close2 真正关得掉，CloseFile 假成功）
//   首个件 → IZSceneDoc.GetChildElements（槽7），取首个 QI IZPart（装配退而取首个 IZElement）
//   比对   → 零件名 vs DocBaseName(文档名)；虚拟件＝GetExternallyLinkedInfo 为 false
//   枚举   → IZBaseApp.GetOpenDocs（槽8，返回 SAFEARRAY of IZDoc；按 Name 以 .ics 结尾过滤，补齐手动打开的文档）
// 铁律：判据只认回读/窗口取证；关闭不依赖 CloseFile 返回值，Close2 hr=0 即成功（参考项目口径）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    internal static class DesignEnvTool
    {
        /// <summary>本工具打开的设计环境记录（用于「关闭所有」按时间线从新到旧关闭）。</summary>
        public sealed class OpenedDoc
        {
            public IZDoc Doc;
            public string Path;
            public DateTime When;
        }

        private static readonly List<OpenedDoc> _opened = new List<OpenedDoc>();
        private static readonly object _lock = new object();

        private const int SlotDocClose2 = 23;   // IZDoc::Close2(VARIANT_BOOL)（members.csv：IZDoc 槽23）
        private const int ElemTypeAssembly = 2;          // Z_ELEMENT_ASSEMBLY
        private const int ElemTypeSheetMetalPart = 20;   // Z_ELEMENT_SHEETMETALPART

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnClose2(IntPtr self, int vbCloseImmediately);

        // ================================================================ 打开设计环境

        /// <summary>打开设计环境：选 .ics → 打开 → 比对首个零部件 →（相等则）图号分离。owner 用于文件框/选择框归属。</summary>
        public static string OpenDesignEnv(IZBaseApp app, IWin32Window owner, Action<string> log)
        {
            const string tag = "open-env";

            // 优先：当前设计环境内选中了外部链接件 → 直接打开其外链文件（便于逐级钻入装配外链子件）
            string path = TryGetSelectedExternalLink(app, log, tag);
            if (string.IsNullOrEmpty(path))
            {
                // 其余情况：弹出文件选择框选 .ics
                using (var dlg = new OpenFileDialog
                {
                    Filter = "CAXA 3D 文档 (*.ics)|*.ics|所有文件 (*.*)|*.*",
                    Title = "打开设计环境（.ics）",
                    CheckFileExists = true,
                    RestoreDirectory = true
                })
                {
                    if (dlg.ShowDialog(owner) != DialogResult.OK) return "已取消打开。";
                    path = dlg.FileName;
                    if (!path.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
                        return "只支持打开 .ics 设计环境文档（当前选择：" + Path.GetFileName(path) + "）。";
                }
            }
            else
            {
                log(tag + ": 检测到选中外部链接件，直接打开外链文件「" + Path.GetFileName(path) + "」");
            }

            log(tag + ": 打开 " + path);
            try { bool ao; app.OpenFile2(path, false, true, out ao); log(tag + ": OpenFile2 返回 ao=" + ao); }
            catch (Exception ex) { log(tag + ": OpenFile2 异常（热路径常态，窗口差集另行判定）: " + ex.Message); }

            // 等到该文件成为活动文档（按活动文档名含路径判定）
            IZDoc adoc;
            if (!WaitForActiveDoc(app, path, out adoc))
                return "打开超时：15 秒内「" + Path.GetFileName(path) + "」未成为活动文档（详见 trace，可能 CAXA 已弹窗或打开失败）。";

            // 记录打开时间线（去重：同路径不重复记）
            lock (_lock)
            {
                if (_opened.Find(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)) == null)
                    _opened.Add(new OpenedDoc { Doc = adoc, Path = path, When = DateTime.Now });
            }

            return ProcessComponents(app, adoc, path, log, tag);
        }

        /// <summary>若当前活动设计环境内选中了外部链接件，返回其外链文件路径；否则返回 null（走文件框）。</summary>
        private static string TryGetSelectedExternalLink(IZBaseApp app, Action<string> log, string tag)
        {
            IZDoc adoc;
            try { adoc = app.ActiveDoc; } catch { return null; }
            if (adoc == null) return null;
            var scene = adoc as IZSceneDoc;
            if (scene == null) return null;

            var sel = ProbeCommon.ReadSelectionRaw(scene, tag, log);
            if (sel == null || sel.Count == 0) { log(tag + ": 当前无选中件，走文件选择框"); return null; }
            foreach (object o in sel)
            {
                var part = o as IZPart;
                if (part == null) continue;
                string lf; bool linked;
                try { lf = part.GetExternallyLinkedInfo(out linked) ?? ""; }
                catch { continue; }
                if (linked && !string.IsNullOrEmpty(lf))
                {
                    log(tag + ": 选中件是外部链接件（" + Path.GetFileName(lf) + "），取外链文件");
                    return lf;
                }
            }
            log(tag + ": 选中件均非外部链接件，走文件选择框");
            return null;
        }

        private static bool WaitForActiveDoc(IZBaseApp app, string path, out IZDoc adoc)
        {
            adoc = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000)
            {
                try { Application.DoEvents(); } catch { }
                IZDoc d = null; try { d = app.ActiveDoc; } catch { }
                if (d != null)
                {
                    string nm = ProbeCommon.Safe(() => d.Name, "");
                    if (!string.IsNullOrEmpty(nm) && nm.IndexOf(path, StringComparison.OrdinalIgnoreCase) >= 0)
                    { adoc = d; return true; }
                }
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        /// <summary>
        /// 处理设计环境内所有顶层零部件（不穿透装配）：
        /// ① 对每个非装配件按零件名自动选图号分离规则并执行（DrawingNoSepTool.AutoSelectRule + SeparatePart）；
        /// ② 对与文件名同名的虚拟件回填规格/材料（零件→PartSpecFillTool 自制件规格材料填写；
        ///    钣金→SheetSizeTool 钣金件尺寸填写）。供「打开设计环境」与「关闭设计环境」共用。
        /// </summary>
        private static string ProcessComponents(IZBaseApp app, IZDoc adoc, string path, Action<string> log, string tag)
        {
            IZSceneDoc scene = adoc as IZSceneDoc;
            if (scene == null) return "打开的文档不是 3D 场景（可能是图纸 exb），无法处理零部件。";

            object kidsRaw;
            try { kidsRaw = scene.GetChildElements(); }
            catch (Exception ex) { Diagnostics.Log(tag + ": GetChildElements FAIL " + ProbeCommon.Hr(ex)); return "枚举零部件失败：" + ProbeCommon.Hr(ex); }
            var kids = kidsRaw as object[];
            if (kids == null || kids.Length == 0) return "设计环境内未找到任何顶层元素。";

            string docBase = DocBaseName(path);
            var rules = DrawingNoSepTool.LoadRules();

            var sb = new System.Text.StringBuilder();
            int nParts = 0, nSep = 0, nFill = 0, nFillFail = 0, nSkip = 0;
            var fillFails = new List<string>();

            foreach (object o in kids)
            {
                var el = o as IZElement;
                if (el == null) continue;
                int type = -1;
                try { type = (int)el.Type; } catch { }
                if (type == ElemTypeAssembly)
                {
                    log(tag + ": 跳过装配「" + ProbeCommon.Safe(() => el.Name, "<无名>") + "」（不穿透）");
                    nSkip++;
                    continue;
                }

                var part = o as IZPart;
                if (part == null)
                {
                    log(tag + ": 跳过非零件元素（类型 " + type + "）");
                    nSkip++;
                    continue;
                }

                string partName = ProbeCommon.Safe(() => el.Name, "<无名>");

                // 展开件是钣金件的「展开态配置产物」，不是独立零部件（日志实证：它同样出现在
                // GetChildElements 顶层、类型 20）。若参与图号分离会写出「展开SC08020102.01-04」
                // 这类错误图号污染 BOM —— 与 SheetSizeTool.Validate「名称含展开即拒填」同口径，跳过。
                if (partName.IndexOf("展开", StringComparison.Ordinal) >= 0)
                {
                    log(tag + ": 跳过展开件「" + partName + "」（钣金展开态配置产物，非独立零部件）");
                    nSkip++;
                    continue;
                }

                nParts++;
                log(tag + ": 处理零部件「" + partName + "」（类型 " + type + "）");

                // ① 图号分离：按零件名自动选规则（无匹配则跳过该件分离）
                DrawingNoSepTool.Rule rule = DrawingNoSepTool.AutoSelectRule(partName, rules);
                if (rule != null)
                {
                    log(tag + ": 自动选规则「" + rule.Text + "」对「" + partName + "」进行图号分离");
                    string s = DrawingNoSepTool.SeparatePart(app, adoc, el, partName, rule, log);
                    log(tag + ": 图号分离：" + s);
                    nSep++;
                }
                else
                {
                    log(tag + ": 「" + partName + "」无匹配图号分离规则（按名称无空格/下划线分隔或不满足段数），跳过分离");
                }

                // ② 规格/材料回填：只认同名虚拟件（非外部链接）
                if (string.Equals(partName, docBase, StringComparison.OrdinalIgnoreCase))
                {
                    bool linked; string lf = part.GetExternallyLinkedInfo(out linked) ?? "";
                    if (linked && !string.IsNullOrEmpty(lf))
                    {
                        log(tag + ": 「" + partName + "」是外部链接件，跳过规格/材料回填");
                    }
                    else if (type == ElemTypeSheetMetalPart)
                    {
                        log(tag + ": 同名虚拟钣金件「" + partName + "」→ 钣金件尺寸填写");
                        bool ok;
                        string f = SheetSizeTool.FillSheetSizeForElement(app, adoc, el, docBase, log, out ok);
                        if (f != null) log(tag + ": " + f);
                        if (ok) nFill++;
                        else { nFillFail++; fillFails.Add("「" + partName + "」" + (f ?? "钣金件尺寸填写未完成")); }
                    }
                    else
                    {
                        log(tag + ": 同名虚拟件「" + partName + "」（类型 " + type + "）→ 自制件规格材料填写");
                        bool ok;
                        string f = PartSpecFillTool.FillSpecForElement(app, el, docBase, log, out ok);
                        if (f != null) log(tag + ": " + f);
                        if (ok) nFill++;
                        else { nFillFail++; fillFails.Add("「" + partName + "」" + (f ?? "自制件规格材料填写未完成")); }
                    }
                }
                else
                {
                    log(tag + ": 「" + partName + "」≠文件名「" + docBase + "」，不做规格/材料回填（只认同名虚拟件）");
                }
            }

            // 兜底保存（图号分离/钣金填写已各自保存，这里确保属性落盘）
            try { if (adoc != null) { adoc.Save(); log(tag + ": 设计环境已保存"); } }
            catch (Exception ex) { log(tag + ": 保存文档 EX " + ProbeCommon.Hr(ex)); }

            sb.AppendLine("共处理 " + nParts + " 个零部件（跳过 " + nSkip + " 个装配/展开件/非零件）：图号分离 " + nSep +
                " 个，规格/材料回填成功 " + nFill + " 个（同名虚拟件）" +
                (nFillFail == 0 ? "。" : "，失败 " + nFillFail + " 个："));
            foreach (string ff in fillFails) sb.AppendLine("  ★ " + ff);
            return sb.ToString().TrimEnd();
        }

        // ================================================================ 关闭设计环境

        public enum CloseScope { Cancel, Current, All }

        /// <summary>弹选择框：关闭当前 / 关闭所有 / 取消。</summary>
        public static CloseScope AskCloseScope(IWin32Window owner)
        {
            var result = CloseScope.Cancel;
            using (var f = new Form
            {
                Text = "关闭设计环境",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new System.Drawing.Size(300, 116)
            })
            {
                var bCur = new Button { Text = "关闭当前设计环境", Location = new System.Drawing.Point(20, 18), Size = new System.Drawing.Size(260, 32) };
                var bAll = new Button { Text = "关闭所有设计环境", Location = new System.Drawing.Point(20, 60), Size = new System.Drawing.Size(260, 32) };
                bCur.Click += (s, e) => { result = CloseScope.Current; f.Close(); };
                bAll.Click += (s, e) => { result = CloseScope.All; f.Close(); };
                f.Controls.AddRange(new Control[] { bCur, bAll });
                f.ShowDialog(owner);
            }
            return result;
        }

        /// <summary>关闭设计环境：closeAll=false 关当前前台 .ics；closeAll=true 按本工具打开时间线从新到旧逐个关闭。</summary>
        public static string CloseDesignEnv(IZBaseApp app, bool closeAll, Action<string> log)
        {
            const string tag = "close-env";

            if (closeAll)
            {
                // 合并：本工具记录的（带 When）＋ GetOpenDocs 枚举中未记录的（手动打开的 .ics）
                var toClose = new List<OpenedDoc>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lock (_lock)
                {
                    foreach (var od in _opened) { toClose.Add(od); seen.Add(od.Path); }
                }
                string enumErr;
                var manual = EnumerateOpenIcs(app, seen, log, out enumErr);
                if (manual != null) toClose.AddRange(manual);

                if (toClose.Count == 0)
                {
                    string baseMsg = "没有需要关闭的设计环境（本工具未打开，且枚举打开文档未发现 .ics）。";
                    if (enumErr != null) baseMsg += "（枚举所有文档失败：" + enumErr + "）";
                    return baseMsg;
                }
                // 排序：本工具记录（有 When）按新→旧；手动打开（When=MinValue，无记录时间）排末尾
                toClose.Sort((a, b) =>
                {
                    if (a.When == DateTime.MinValue && b.When == DateTime.MinValue) return 0;
                    if (a.When == DateTime.MinValue) return 1;
                    if (b.When == DateTime.MinValue) return -1;
                    return b.When.CompareTo(a.When);
                });

                var sb = new System.Text.StringBuilder();
                int closed = 0;
                foreach (OpenedDoc od in toClose)
                {
                    string note = (od.When == DateTime.MinValue) ? "（手动打开，无记录时间）" : "";
                    sb.AppendLine(Path.GetFileName(od.Path) + note + "：" + CloseOne(app, od, log, tag));
                    closed++;
                    lock (_lock) { _opened.RemoveAll(x => string.Equals(x.Path, od.Path, StringComparison.OrdinalIgnoreCase)); }
                }
                return "已关闭 " + closed + " 个设计环境（本工具打开＋手动打开的 .ics，按时间线新→旧，手动无时间的排末尾）：\n" + sb.ToString().TrimEnd();
            }

            // 关闭当前（前台）.ics
            IZDoc adoc;
            try { adoc = app.ActiveDoc; }
            catch (Exception ex) { return "读取活动文档失败：" + ex.Message; }
            if (adoc == null) return "当前没有活动的设计环境。";
            string nm = ProbeCommon.Safe(() => adoc.Name, "");
            if (string.IsNullOrEmpty(nm) || !nm.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
                return "当前活动文档不是 .ics 设计环境（" + (string.IsNullOrEmpty(nm) ? "<空>" : nm) + "），不关闭。";

            var od2 = new OpenedDoc { Doc = adoc, Path = nm, When = DateTime.Now };
            string r = CloseOne(app, od2, log, tag);
            lock (_lock) { _opened.RemoveAll(x => string.Equals(x.Path, nm, StringComparison.OrdinalIgnoreCase)); }
            return "已关闭当前设计环境「" + Path.GetFileName(nm) + "」：" + r;
        }

        /// <summary>枚举 GetOpenDocs 中所有 .ics 设计环境，返回未在 seen 中的（即手动打开、本工具未记录的）。</summary>
        private static List<OpenedDoc> EnumerateOpenIcs(IZBaseApp app, HashSet<string> seen, Action<string> log, out string err)
        {
            err = null;
            var list = new List<OpenedDoc>();
            object raw;
            try { raw = app.GetOpenDocs(); }   // IZBaseApp 槽8：返回所有打开文档（SAFEARRAY of IZDoc）
            catch (Exception ex) { err = ex.Message; return null; }
            object[] arr = raw as object[];
            if (arr == null)
            {
                if (raw != null) arr = new object[] { raw };   // 单文档兜底
                else return list;
            }
            foreach (object o in arr)
            {
                var doc = o as IZDoc;
                if (doc == null) continue;
                string nm = ProbeCommon.Safe(() => doc.Name, "");   // IZDoc 槽11：含完整路径的文档名
                if (string.IsNullOrEmpty(nm) || !nm.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) continue;  // 只关 .ics 设计环境
                if (seen.Contains(nm)) continue;   // 本工具已记录，避免重复关
                seen.Add(nm);
                list.Add(new OpenedDoc { Doc = doc, Path = nm, When = DateTime.MinValue });
            }
            if (list.Count > 0 && log != null)
                log("close-env: 枚举 GetOpenDocs 发现 " + list.Count + " 个手动打开的 .ics（无记录时间，将排在最后关闭）");
            return list;
        }

        /// <summary>单个文档：先做 ProcessComponents（图号分离＋同名虚拟件规格/材料回填），再 Close2 关闭。</summary>
        private static string CloseOne(IZBaseApp app, OpenedDoc od, Action<string> log, string tag)
        {
            string proc = "";
            IZSceneDoc scene = od.Doc as IZSceneDoc;
            if (scene != null)
            {
                proc = ProcessComponents(app, od.Doc, od.Path, log, tag);
            }
            string close = CloseDocByPtr(od.Doc, log, tag);
            if (string.IsNullOrEmpty(proc)) return close;
            return proc + "\n关闭：" + close;
        }

        /// <summary>Close2(-1) 立即关闭文档（IZDoc 接口指针直接调槽23，不依赖 CloseFile 返回值）。
        /// internal 供 DlgBatchConvert 的 ics 收尾关闭链复用（外链 ics 只有 Close2 关得掉）。</summary>
        internal static string CloseDocByPtr(IZDoc doc, Action<string> log, string tag)
        {
            IntPtr pDocIf = IntPtr.Zero;
            try { pDocIf = Marshal.GetComInterfaceForObject(doc, typeof(IZDoc)); }
            catch (Exception ex)
            {
                log(tag + ": 取 IZDoc 接口指针失败 " + ex.Message);
                return "取接口指针失败（文档可能已关闭）";
            }
            try
            {
                int hr = ProbeCommon.Fn<FnClose2>(pDocIf, SlotDocClose2)(pDocIf, -1);   // -1 = VARIANT_TRUE，CloseImmediately
                log(tag + ": Close2 hr=0x" + hr.ToString("X"));
                if (hr != 0) return "Close2 返回 0x" + hr.ToString("X") + "（可能未关闭，请手动关闭）";
                return "已关闭（Close2 成功）";
            }
            catch (Exception ex)
            {
                log(tag + ": Close2 EX " + ex.Message);
                return "Close2 异常：" + ex.Message;
            }
            finally { Marshal.Release(pDocIf); }
        }

        // ================================================================ 工具

        private static string DocBaseName(string docName)
        {
            if (string.IsNullOrEmpty(docName)) return "";
            string n = docName;
            int slash = Math.Max(n.LastIndexOf('\\'), n.LastIndexOf('/'));
            if (slash >= 0) n = n.Substring(slash + 1);
            if (n.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n;
        }
    }
}
