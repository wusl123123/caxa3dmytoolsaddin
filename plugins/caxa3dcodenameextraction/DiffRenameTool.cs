// 带图独立（caxa3dcodenameextraction）业务层
//
// 需求：在设计环境内，把选中的外链零件独立出来——不影响其他同链接件，
//       并把零件名与图号分离一并修正。
//
// 落地路线（2026-11-02 定稿，取代 11-01 的 SaveAs 主路线）：
//   ① 校验：必须是外链件，且「外链文件名 == 零件名」
//      （否则新文件名对不上号，替换后无法用文件名判副作用）
//   ② 复制图纸：把 零件A.exb 复制成 零件B.exb 落到设计环境 ics 所在文件夹（目标已存在即中止）
//   ③ 独立（唯一主路线，全自动）：复制 零件A.ics → 零件B.ics，把选中件按回活选择后
//      投递原生「替换零件/装配」(CmdReplacePart=53851)，UI 代点跑完面板。
//      判据走设计树遍历（不看活选择，替换期间 CAXA 会清空选择集）。
//      实机 11-02 13:35 钣金件（折弯板→折弯板44）走完整链，替换后仍是 钣金零件(type=20)；
//      11-01 15:53 那次降级成装配体(type=2)，是链路两张业务弹窗**空选项点了确定**
//      （CAXA 按“整个场景”插入），不是命令本身会换元素对象 ⇒ 见 LinkPickDialog。
//      早先的主路线 IZPart::SaveAs(vbLinkToScene=true) 槽 16 / IZAssembly::SaveAs 槽 14 已退役，
//      代码保留在 CaxaAutomation 里但不再作钣金件的替代方案。
//   ④ 修正：重命名（set_Name + 回读 + Update 提交）+ 图号分离回填 + 保存，修成「零件B.ics - 零件B」。
//
// 注意：零件B.exb 是从 零件A.exb 复制来的，其内部 3D 引用仍指向 零件A.ics；
//      exb 直改链接已被用户裁定否决（大图纸无法与官方保存产物一致），故不做改写。
//
// 铁律：COM 只在 CAXA 主线程（STA）访问；本文件所有方法都从 UI 线程直接调用，
//       不得开线程；等待/中止由 MainForm 的 WaitUntil + FlowGate 停止标志负责。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>选中零件的现场快照（改名替换前后各取一次，用来比对副作用）。</summary>
    [System.Runtime.InteropServices.ComVisible(false)]
    internal sealed class ElementSnapshot
    {
        public string Name = "";
        public string Kind = "";
        public bool Linked;
        public string LinkPath = "";
        public bool OkToUnlink;
        public ZMathMatrix TransformToParent;
        public string Note = "";

        public override string ToString()
        {
            return "名称=" + Name + " 类型=" + Kind +
                   " 外链=" + (Linked ? "是" : "否") + " 链接源=" + LinkPath +
                   " 可断开=" + (OkToUnlink ? "是" : "否") +
                   (string.IsNullOrEmpty(Note) ? "" : " 备注=" + Note);
        }
    }

    internal static class DiffRenameTool
    {
        // ================================================================ 选区

        /// <summary>取当前设计环境里选中的元素（先取选区数组，空则回退真实选中元素）。</summary>
        public static List<IZElement> GetSelection(IZBaseApp app, out string error)
        {
            error = null;
            var list = new List<IZElement>();

            IZSceneDoc scene = GetSceneDoc(app);
            if (scene == null)
            {
                error = "当前活动文档不是设计（场景）环境，请切回 3D 设计环境后再试。";
                return list;
            }

            IZSelectionMgr selMgr = null;
            try { selMgr = scene.SelectionMgr; } catch { }
            if (selMgr == null) { error = "取不到 SelectionMgr。"; return list; }

            try
            {
                ZArray arr = selMgr.GetSelectedElementsZArray();
                if (arr != null)
                {
                    int count;
                    arr.Count(out count);
                    for (int i = 0; i < count; i++)
                    {
                        object raw;
                        arr.Get(i, out raw);
                        AppendElement(raw, list);
                    }
                }
            }
            catch (Exception ex) { error = "读取选中元素失败：" + ex.Message; }

            if (list.Count == 0)
            {
                try { AppendElement(selMgr.GetTrueSelectedElements(), list); }
                catch (Exception ex) { if (error == null) error = "读取真实选中元素失败：" + ex.Message; }
            }
            return list;
        }

        private static void AppendElement(object raw, List<IZElement> list)
        {
            if (raw == null) return;

            var single = raw as IZElement;
            if (single != null)
            {
                if (!list.Contains(single)) list.Add(single);
                return;
            }

            var za = raw as ZArray;
            if (za != null)
            {
                int count;
                za.Count(out count);
                for (int i = 0; i < count; i++)
                {
                    object inner;
                    za.Get(i, out inner);
                    AppendElement(inner, list);
                }
            }
        }

        private static IZSceneDoc GetSceneDoc(IZBaseApp app)
        {
            if (app == null) return null;
            IZDoc doc = null;
            try { doc = app.ActiveDoc; } catch (Exception ex)
            {
                Diagnostics.Log("GetSceneDoc: ActiveDoc 异常 " + ex.Message);
                return null;
            }
            var scene = doc as IZSceneDoc;
            if (scene == null && doc != null)
                Diagnostics.Log("GetSceneDoc: 活动文档不是场景文档（" + doc.GetType().Name + "）");
            return scene;
        }

        // ================================================================ 现场快照

        /// <summary>只读快照：名称 / 类型 / 外链源 / 能否断开 / 相对父级位姿。不改图。</summary>
        public static ElementSnapshot Capture(IZElement elem)
        {
            var s = new ElementSnapshot();
            if (elem == null) { s.Note = "元素为空"; return s; }

            try { s.Name = elem.Name ?? ""; } catch (Exception ex) { s.Note = "读名称异常：" + ex.Message; }
            if (string.IsNullOrEmpty(s.Name)) s.Name = "(未命名)";

            var part = elem as IZPart;
            var asm  = elem as IZAssembly;
            if (part != null)      s.Kind = "零件";
            else if (asm != null)  s.Kind = "装配";
            else                   s.Kind = "其它";

            // 链接源路径：interop v26 只在 IZPart / IZAssembly 上声明了 GetExternallyLinkedInfo
            //（IZSceneElement 没有这个成员，写成 sceneElem.GetExternallyLinkedInfo 直接 CS1061）
            try
            {
                bool linked = false;
                string path = null;
                if (part != null)     path = part.GetExternallyLinkedInfo(out linked);
                else if (asm != null) path = asm.GetExternallyLinkedInfo(out linked);
                else s.Note = AppendNote(s.Note, "非零件/装配，读不到链接信息");
                s.Linked = linked;
                s.LinkPath = path ?? "";
            }
            catch (Exception ex) { s.Note = AppendNote(s.Note, "读链接信息异常：" + ex.Message); }

            // 断开许可与位姿：这两个成员在 IZSceneElement 上
            var sceneElem = elem as IZSceneElement;
            if (sceneElem != null)
            {
                try { sceneElem.IsOKToUnlinkInternal(out s.OkToUnlink); }
                catch (Exception ex) { s.Note = AppendNote(s.Note, "IsOKToUnlinkInternal 异常：" + ex.Message); }

                try { s.TransformToParent = sceneElem.GetTransformToParent(); }
                catch (Exception ex) { s.Note = AppendNote(s.Note, "读位姿异常：" + ex.Message); }
            }
            else s.Note = AppendNote(s.Note, "不是 IZSceneElement，无断开许可/位姿信息");

            return s;
        }

        private static string AppendNote(string old, string add)
        {
            return string.IsNullOrEmpty(old) ? add : old + "；" + add;
        }

        // ================================================================ 校验（安全闸门）

        /// <summary>
        /// 运行前校验：必须在设计环境、必须有选中、必须是单个外链零件、
        /// 且「外链文件名 == 零件名」（需求第 1 条）。
        /// 判不过就返回 false 并给出用户可读原因——宁可不做，不可做错。
        /// </summary>
        public static bool Validate(IZBaseApp app, out string error, out List<ElementSnapshot> snapshots)
        {
            snapshots = new List<ElementSnapshot>();
            error = null;

            string selError;
            List<IZElement> picked = GetSelection(app, out selError);
            if (picked.Count == 0)
            {
                error = selError ?? "没有选中任何元素，请先在设计环境中选中要独立的零件。";
                return false;
            }

            foreach (IZElement e in picked)
            {
                ElementSnapshot s = Capture(e);
                snapshots.Add(s);
                Diagnostics.Log("Validate: 选中 -> " + s);
            }

            if (picked.Count > 1)
            {
                error = "当前一次只处理 1 个零件，请只选中一个（现选中 " + picked.Count + " 个）。";
                return false;
            }

            var first = snapshots[0];
            if (first.Kind != "零件")
            {
                error = "选中的不是零件（" + first.Kind + "），本功能只处理零件。";
                return false;
            }
            if (!first.Linked || string.IsNullOrEmpty(first.LinkPath))
            {
                error = "选中零件不是外链件（没有外部 .ics 链接），无需独立。";
                return false;
            }
            if (!NameMatchesLinkFile(first, out error)) return false;

            if (!first.OkToUnlink)
                Diagnostics.Log("Validate: IsOKToUnlinkInternal=false（本功能走原生替换，不依赖断开，仅记录）");

            return true;
        }

        /// <summary>需求第 1 条：外链文件名必须等于零件名，否则替换后名称与文件对不上。</summary>
        public static bool NameMatchesLinkFile(ElementSnapshot s, out string error)
        {
            error = null;
            string baseName;
            try { baseName = Path.GetFileNameWithoutExtension(s.LinkPath); }
            catch (Exception ex)
            {
                error = "外链路径无法解析：" + s.LinkPath + "（" + ex.Message + "）";
                return false;
            }
            if (!string.Equals(baseName, s.Name, StringComparison.OrdinalIgnoreCase))
            {
                error = "外链文件名与零件名不一致（文件 " + baseName + ".ics / 零件名 " + s.Name +
                        "），本功能要求两者同名，请先在 CAXA 中修正后再独立。";
                return false;
            }
            return true;
        }

        /// <summary>外链文件所在文件夹（= 设计环境.ics 所在文件夹，复制新件的目标目录）。</summary>
        public static string LinkFolder(ElementSnapshot s)
        {
            if (s == null || string.IsNullOrEmpty(s.LinkPath)) return null;
            try { return Path.GetDirectoryName(s.LinkPath); }
            catch { return null; }
        }

        // ================================================================ 复制新件

        /// <summary>
        /// 后备路线用：把 零件A.ics 复制成 零件B.ics（srcFolder=外链件实际所在目录，
        /// dstFolder=设计环境.ics 所在目录）。目标已存在即中止，绝不覆盖。
        /// 首选路线不需要它——ics 由 IZPart::SaveAs 直接写出。
        /// </summary>
        public static void CopyLinkedModel(string srcFolder, string dstFolder,
            string oldBase, string newBase, Action<string> log)
        {
            if (string.IsNullOrEmpty(srcFolder)) throw new InvalidOperationException("取不到外链件所在文件夹。");
            if (string.IsNullOrEmpty(dstFolder)) dstFolder = srcFolder;
            if (string.IsNullOrEmpty(oldBase) || string.IsNullOrEmpty(newBase))
                throw new InvalidOperationException("原零件名或新零件名为空。");

            string oldIcs = Path.Combine(srcFolder, oldBase + ".ics");
            string newIcs = Path.Combine(dstFolder, newBase + ".ics");

            if (!File.Exists(oldIcs))
                throw new FileNotFoundException("找不到外链文件（" + oldIcs + "）", oldIcs);
            if (File.Exists(newIcs))
                throw new InvalidOperationException("目标已存在：" + newIcs + "，请换一个新零件名。");

            CheckStop();
            File.Copy(oldIcs, newIcs, false);
            log("    已复制 " + oldIcs + " -> " + newIcs + "（" + new FileInfo(newIcs).Length + " B）");
        }

        /// <summary>
        /// 首选路线的第 ② 步：只复制图纸 零件A.exb → 零件B.exb。
        /// ics 由 CAXA 自己写出（元素级 SaveAs 回链），预复制反而会让 SaveAs 面对已存在文件。
        /// 目标已存在即抛错，不覆盖。
        /// </summary>
        /// <returns>实际复制了 exb 返回 true；源文件夹没有 exb 返回 false（只提示，不算失败）。</returns>
        public static bool CopyLinkedDrawing(string srcFolder, string dstFolder,
            string oldBase, string newBase, Action<string> log)
        {
            if (string.IsNullOrEmpty(srcFolder)) throw new InvalidOperationException("取不到外链件所在文件夹。");
            if (string.IsNullOrEmpty(dstFolder)) dstFolder = srcFolder;

            string oldExb = Path.Combine(srcFolder, oldBase + ".exb");
            string newExb = Path.Combine(dstFolder, newBase + ".exb");
            if (!File.Exists(oldExb))
            {
                log("    源文件夹无 " + oldBase + ".exb，不涉及图纸");
                return false;
            }
            if (File.Exists(newExb))
                throw new InvalidOperationException("目标已存在：" + newExb + "，请换一个新零件名。");

            CheckStop();
            File.Copy(oldExb, newExb, false);
            log("    已复制 " + oldExb + " -> " + newExb + "（" + new FileInfo(newExb).Length +
                " B，注意：其内部 3D 引用仍指向 " + oldBase + ".ics）");
            return true;
        }

        /// <summary>
        /// 「替换元素探针」用：把 零件A.ics 与同名图纸 零件A.exb **一起**复制成 零件B.ics / 零件B.exb。
        ///
        /// 与 <see cref="CopyLinkedDrawing"/> 的区别只在 exb 是可选项：除「替换元素探针」之外的
        /// 流程都要求 exb 必须存在，而探针允许没有图纸的零件，所以这里缺失 exb 只提示、不报错。
        ///
        /// 两个目标**先全量校验再动手**：只复制 ics、再发现 零件B.exb 已存在而抛错的话，
        /// 会留下一个孤儿 ics，用户下次还得手工删。
        /// </summary>
        /// <returns>exb 也一并复制了返回 true；源文件夹没有 exb 返回 false。</returns>
        public static bool CopyLinkedFiles(string srcFolder, string dstFolder,
            string oldBase, string newBase, Action<string> log)
        {
            if (string.IsNullOrEmpty(srcFolder)) throw new InvalidOperationException("取不到外链件所在文件夹。");
            if (string.IsNullOrEmpty(dstFolder)) dstFolder = srcFolder;
            if (string.IsNullOrEmpty(oldBase) || string.IsNullOrEmpty(newBase))
                throw new InvalidOperationException("原零件名或新零件名为空。");

            string oldIcs = Path.Combine(srcFolder, oldBase + ".ics");
            string newIcs = Path.Combine(dstFolder, newBase + ".ics");
            string oldExb = Path.Combine(srcFolder, oldBase + ".exb");
            string newExb = Path.Combine(dstFolder, newBase + ".exb");

            if (!File.Exists(oldIcs))
                throw new FileNotFoundException("找不到外链文件（" + oldIcs + "）", oldIcs);

            bool hasExb = File.Exists(oldExb);
            if (File.Exists(newIcs))
                throw new InvalidOperationException("目标已存在：" + newIcs + "，请换一个新零件名。");
            if (hasExb && File.Exists(newExb))
                throw new InvalidOperationException("目标已存在：" + newExb + "，请换一个新零件名。");

            CheckStop();
            File.Copy(oldIcs, newIcs, false);
            log("    已复制 " + oldIcs + " -> " + newIcs + "（" + new FileInfo(newIcs).Length + " B）");

            if (!hasExb)
            {
                log("    源文件夹无 " + oldBase + ".exb，本次只复制模型");
                return false;
            }

            CheckStop();
            File.Copy(oldExb, newExb, false);
            log("    已复制 " + oldExb + " -> " + newExb + "（" + new FileInfo(newExb).Length +
                " B，其内部 3D 引用仍指向 " + oldBase + ".ics，重指向需在图纸环境更新源）");
            return true;
        }

        // ================================================================ 原生命令：替换零件/装配

        /// <summary>
        /// 「替换零件/装配」命令 ID = 53851（0xD25B）。
        /// 取证（CAXA 3D 2027，<c>code\ICAPI\_probe-tools\out\res_scan_20261101_110025.csv</c>）：
        ///   MENU 53851「替换零件/装配」在 3i2ULg.dll(menu137/0/20/1)、
        ///   3isdLg.dll(menu5008/0/32/6)、3ismLg.dll(menu135/0/30/6) 三处同号；
        ///   命令帮助串在 3iAdvFeatureLg.dll：8980「替换零件/装配」、8981「替换选择的外部链接零件/装配」。
        /// 投递本身不作成功判据——成功只看选中件的外链路径是否换成新 ics。
        /// </summary>
        public const int CmdReplacePart = 53851;

        private const uint WM_COMMAND = 0x0111;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 将「替换零件/装配」命令投给 CAXA 主框架（PostMessage，不阻塞 STA 主线程）。
        /// 目标必须用 <see cref="UiWin.CadFrame"/>：实机取证过，Process.MainWindowHandle 返回的是
        /// 本插件自己的 WinForms 窗口，命令投给自己 → CAXA 侧永远不出面板。
        /// </summary>
        public static bool PostReplacePartCommand(out string error)
        {
            error = null;
            IntPtr hwnd = UiWin.CadFrame();
            if (hwnd == IntPtr.Zero)
            {
                error = "取不到 CAXA 主框架句柄（本 DLL 宿主应为 IronCAD.exe，且主框架需可见）。";
                Diagnostics.Log("PostReplacePartCommand: CAXA 主框架为空");
                return false;
            }
            bool posted;
            try { posted = PostMessage(hwnd, WM_COMMAND, (IntPtr)CmdReplacePart, IntPtr.Zero); }
            catch (Exception ex)
            {
                error = "投递命令异常：" + ex.Message;
                Diagnostics.Log("PostReplacePartCommand 异常: " + ex);
                return false;
            }
            Diagnostics.Trace(string.Format("PostReplacePartCommand: hwnd=0x{0:X} cmd={1} posted={2}",
                hwnd.ToInt64(), CmdReplacePart, posted));
            if (!posted)
                error = "投递「替换零件/装配」失败，Win32 错误 " + Marshal.GetLastWin32Error() + "。";
            return posted;
        }

        // ================================================================ 执行入口

        /// <summary>
        /// 本窗口的只读取证入口：校验通过后给出可执行性与计划，不动图。
        /// 真正的「带图独立」在带图改名主窗口的「带图独立」按钮里执行
        /// （那里才有新零件名输入与分离规则）。
        /// </summary>
        public static void Execute(IZBaseApp app, IList<ElementSnapshot> before, Action<string> log)
        {
            if (before == null || before.Count == 0) throw new InvalidOperationException("没有可处理的选中元素");
            ElementSnapshot s = before[0];

            string nameError;
            bool nameOk = NameMatchesLinkFile(s, out nameError);

            log("选中：" + s);
            log("所在文件夹：" + (LinkFolder(s) ?? "取不到"));
            log("外链文件名=零件名：" + (nameOk ? "满足" : "不满足 -> " + nameError));
            log("执行入口：点主窗口「重命名」左侧的「带图独立」按钮（需在该窗口填新零件名）。");
            log("流程：复制 零件A.ics/.exb -> 零件B.ics/.exb → 原生「替换零件/装配」换链接源 → 重命名+图号分离 → 保存。");
            Diagnostics.Log("Execute(取证): 快照=" + s + " 同名判定=" + nameOk);
        }

        /// <summary>停止检查：由 FlowGate.StopRequested（进程级，关窗或点「停止」置位）驱动。</summary>
        public static void CheckStop()
        {
            if (FlowGate.StopRequested) throw new OperationStoppedException();
        }
    }
}
