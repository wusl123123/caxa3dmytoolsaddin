// 单件格式转换（移植自 caxa3dexbtracker「批量格式转换」产线逻辑，按本工程约定改写）：
//   · 3D：ics 第一个对象（GetTopElement，不穿透）→ x_t/stp/sat（RCW 直调 IZAssembly/IZPart.Export）。
//   · 2D：exb → dwg/dxf/pdf。产线链路（2026-11-12 起）＝ CRX 桥 OPEN_EXB 图纸侧打开
//     → PopupAutoYes 借还代点消化弹窗（8D 视图更新→是 / 尺寸检查→取消）→ WM_COMMAND 保存落盘
//     （mtime 权威判据）→ pdf=打印对话框代点（先导）/ dxf/dwg=另存文件框代点 → CLOSEDOC 收尾。
//     ★ 业务 exb 禁走 Scene 侧 OpenFile2（Invalid file format 假失败 + AV 触发面 + 永远「未进册」）；
//     ★ 桥内 CONVERT_DXF 通道 2027 已判死（GetIDispatch(true) 恒 NULL＝规则总纲 29②，否决 #13/#15）。
//   · 2D 必须在图纸环境已预热时才能跑（CRX marker 判据）；未预热直接跳过并提示。
//   · 2D 整条链跑后台线程（另存/打印框模态循环会冻死 UI 线程等待，参考工程 13:33/13:53 实证）；
//     3D 仍走 UI 线程（COM STA）。判成功只认输出文件存在且 Length>0。
//   · 已打开的文档（批量开始前就在的，或用户开着的那张）绝不关闭：只关"本方法自己打开的"。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    /// <summary>格式转换勾选（弹窗 → Run 传入）。ics/exb 默认勾选＝保持原复制行为；其余默认不勾。</summary>
    // public：PopupClassifyPackage 构造委托签名要用，可见性须一致
    public sealed class ConvertOptions
    {
        public bool Ics = true;   // 复制 ics（源）
        public bool Xt;           // → x_t
        public bool Stp;          // → stp
        public bool Sat;          // → sat
        public bool Exb = true;   // 复制 exb（源）
        public bool Dwg;          // → dwg
        public bool Dxf;          // → dxf
        public bool Pdf;          // → pdf

        public bool Any3D => Xt || Stp || Sat;
        public bool Any2D => Dwg || Dxf || Pdf;
        public bool AnyConvert => Any3D || Any2D;

        /// <summary>复制源（ics/exb）是否仍需复制：任一勾选源即复制（默认都勾）。</summary>
        public bool CopyIcs => Ics;
        public bool CopyExb => Exb;
    }

    /// <summary>对分组文件夹内某零件的 ics + exb 执行勾选的转换。</summary>
    [ComVisible(false)]
    internal sealed class FormatConverter
    {
        private readonly IZBaseApp _app;
        private readonly Action<string> _log;
        private readonly Func<bool> _stop;

        // eZModelType（enums.csv 实测）：X_T=0 / SAT=1 / STEP=3
        private const int ModelTypeXT = 0;
        private const int ModelTypeSat = 1;
        private const int ModelTypeStep = 3;
        // （原 eZExportDrawingType 常量已删：桥内 CONVERT_DXF 通道 2027 判死＝规则总纲 29②，
        //   导出改走另存/打印对话框代点，见 Convert2D 注释。）

        public FormatConverter(IZBaseApp app, Action<string> log, Func<bool> stop)
        {
            _app = app; _log = log; _stop = stop;
        }

        private bool ShouldStop() { try { Application.DoEvents(); } catch { } return _stop != null && _stop(); }

        /// <summary>纯停止检查（后台线程专用：不泵消息——DoEvents 属 UI 线程）。</summary>
        private bool StopFlag() { return _stop != null && _stop(); }

        /// <summary>对一件执行勾选的转换。srcDir＝源 ics/exb 所在目录（可能不等同目标）；outDir＝转换产物落盘目录（打包目标文件夹）。
        /// 返回人类可读串（逐格式 √/×）。</summary>
        public string ConvertOne(string srcDir, string outDir, string baseName, ConvertOptions opt)
        {
            var pieces = new List<string>();
            if (opt.Any3D) pieces.Add(Convert3D(srcDir, outDir, baseName, opt));
            if (opt.Any2D) pieces.Add(Convert2D(srcDir, outDir, baseName, opt));
            return string.Join("  ", pieces.ToArray());
        }

        // ---------------------------------------------------------- 3D：ics → x_t/stp/sat（RCW 直调）
        private string Convert3D(string srcDir, string outDir, string baseName, ConvertOptions opt)
        {
            string ics = Path.Combine(srcDir, baseName + ".ics");
            if (!File.Exists(ics)) return "3D:源缺失";
            string fileName = Path.GetFileName(ics);
            IZDoc doc = FindOpenDoc(ics, fileName, baseName);
            bool openedByUs = false;
            try
            {
                if (doc == null)
                {
                    bool ao;
                    try { doc = _app.OpenFile2(ics, false, true, out ao); }
                    catch (Exception ex) { _log("3D 打开异常：" + ex.Message); doc = null; }
                    if (doc == null) doc = FindOpenDoc(ics, fileName, baseName);
                    if (doc == null) { Pump(800); doc = FindOpenDoc(ics, fileName, baseName); }
                    if (doc == null) return "3D:打开失败";
                    openedByUs = true;
                }
                IZSceneDoc scene = doc as IZSceneDoc;
                if (scene == null) return "3D:非场景文档";
                IZElement top = null;
                try { top = scene.GetTopElement(); } catch (Exception ex) { _log("GetTopElement 异常：" + ex.Message); }
                if (top == null) return "3D:无对象";

                string baseNoExt = Path.Combine(outDir, baseName);
                var res = new List<string>();
                if (opt.Xt) res.Add("x_t" + (ExportModel(top, baseNoExt, ".x_t", ModelTypeXT, "x_t") ? "√" : "×"));
                if (opt.Stp) res.Add("stp" + (ExportModel(top, baseNoExt, ".stp", ModelTypeStep, "stp") ? "√" : "×"));
                if (opt.Sat) res.Add("sat" + (ExportModel(top, baseNoExt, ".sat", ModelTypeSat, "sat") ? "√" : "×"));
                return "3D:" + string.Join("", res.ToArray());
            }
            catch (Exception ex) { return "3D:异常(" + ex.Message + ")"; }
            finally
            {
                if (openedByUs && doc != null)
                {
                    try { _app.CloseFile(doc); }
                    catch (Exception ex) { _log("CloseFile 异常：" + ex.Message); try { doc.Close(); } catch { } }
                    Pump(300);
                }
            }
        }

        private bool ExportModel(IZElement top, string baseNoExt, string ext, int modelType, string label)
        {
            DateTime since = DateTime.Now;
            bool existed = false; try { existed = File.Exists(baseNoExt + ext); } catch { }
            try
            {
                var asm = top as IZAssembly;
                if (asm != null) { asm.Export(baseNoExt, (eZModelType)modelType); Pump(400); }
                else
                {
                    var part = top as IZPart;
                    if (part != null) { part.Export(baseNoExt, (eZModelType)modelType); Pump(400); }
                    else { _log("第一个对象既非装配体也非零件，无法导出 " + label); return false; }
                }
            }
            catch (Exception ex) { _log("Export(" + label + ") 异常：" + ex.Message); return false; }
            string outp = VerifyWithRetry(Path.GetDirectoryName(baseNoExt), Path.GetFileName(baseNoExt), ext, since, existed);
            if (outp == null) { _log(label + "：导出后没找到输出文件（判失败）"); return false; }
            return true;
        }

        // ---------------------------------------------------------- 2D：exb → dwg/dxf/pdf
        // 产线链路（2026-11-12 移植自 caxa3dexbtracker「批量格式转换」v1 产线通道，参考工程
        // 14:38 实机 5/5 全绿）：
        //   OPEN_EXB（CRX 图纸侧打开；业务 exb 禁走 Scene 侧 OpenFile2 —— Invalid file format
        //   假失败 + 内嵌 CRxView3D 是 AV 触发面 + 图纸进了 Draft 但 Scene 永远「未进册」）
        //   → 弹窗消化（「8D视图数据更新」→是、「尺寸检查」→取消，PopupAutoYes 借还代点）
        //   → WM_COMMAND 保存落盘（mtime 变 = 权威判据，参考工程 11-11 定案）
        //   → 导出：pdf=打印对话框代点（必须先于另存，另存会切走文档关联名）
        //           dxf/dwg=另存文件框代点（消息 + MSAA 双保险选类型）
        //   → CLOSEDOC（保存并关闭）→ WM_CLOSE+代点「否」兜底 → 清障。
        //   ★ 桥内 CONVERT_DXF 通道 2027 已判死（GetIDispatch(true) 恒 NULL = 规则总纲 29②，
        //     参考工程否决 #13/#15）——不再作为导出通道调用。
        //   ★ 整条链跑后台线程：另存/打印框的模态循环会冻死 UI 线程上的 DoEvents 等待
        //     （参考工程 13:33/13:53 实证冻结 26~40s）；UI 线程只泵消息等 worker 结束，
        //     界面/停止按钮保持响应。判成功只认输出文件存在且 >0。
        private string Convert2D(string srcDir, string outDir, string baseName, ConvertOptions opt)
        {
            if (!ViewBridge.IsCrxLoaded())
                return "2D:图纸环境未预热(先点「图纸环境预热」)";
            string exb = Path.Combine(srcDir, baseName + ".exb");
            if (!File.Exists(exb)) return "2D:源缺失";
            string fileName = Path.GetFileName(exb);

            // 已打开的图不做：打开态弹更新框要人点，静默链路无法安全消化
            if (FindDrawingWindow(fileName) != IntPtr.Zero)
            {
                _log(fileName + "：该 exb 已在图纸环境打开，跳过（请先手工关闭再重跑）");
                return "2D:已打开(请先关闭再跑)";
            }

            // 后台线程执行 + UI 线程泵消息等结束（参考工程 RunExbOnWorker 同构）
            bool ok = false;
            string result = "";
            Exception err = null;
            var worker = new System.Threading.Thread(delegate()
            {
                try { result = Convert2DWorker(exb, outDir, baseName, opt, out ok); }
                catch (Exception ex) { err = ex; }
            });
            worker.IsBackground = true;
            worker.Name = "Classify2DFlow";
            worker.Start();
            while (worker.IsAlive)
            {
                try { Application.DoEvents(); } catch { }
                System.Threading.Thread.Sleep(60);
            }
            if (err != null) return "2D:异常(" + err.GetType().Name + ":" + err.Message + ")";
            return result;
        }

        /// <summary>2D 单件产线链路主体（后台线程执行；等待只轮询副作用，绝不泵消息）。</summary>
        private string Convert2DWorker(string exb, string outDir, string baseName, ConvertOptions opt, out bool ok)
        {
            string fileName = Path.GetFileName(exb);
            string stem = Path.GetFileNameWithoutExtension(exb);
            ok = false;
            bool openedByUs = false;   // 这张图是本流程开出来的（收尾才允许关，绝不动用户自己开的图）
            bool oldDecline = PopupAutoYes.DeclineUpdate3dLocation;
            try
            {
                // ── 0) CRX 就绪（冷启动 45s 内 marker 必须新鲜）──
                if (!ViewBridge.IsCrxLoaded())
                {
                    _log(fileName + "：CRX 桥未就绪（详见 " + ViewBridge.LogPath + "）");
                    return "2D:CRX未就绪";
                }
                ViewBridge.EnsureToken(15000, _log);   // OPEN_EXB/CLOSEDOC 要令牌，先惰性握手

                // 打开期「请更新3D文件位置」改点「否」（链接保持陈旧，不让打开期弹文件框打断批量；
                // 参考工程批量口径。旧值收尾恢复）
                PopupAutoYes.DeclineUpdate3dLocation = true;

                using (PopupAutoYes.Borrow("分类打包2D·" + fileName))
                {
                    // ── 1) 图纸侧打开（CRX OPEN_EXB：在册即激活，不在册三通道打开）──
                    var rep = ViewBridge.Request("OPEN_EXB", exb, true, 90000, _log);
                    if (!rep.Ok)
                    {
                        _log(fileName + "：OPEN_EXB 失败 → " + rep.Status + " " + rep.Message);
                        return "2D:打开失败(" + rep.Status + ")";
                    }
                    _log(fileName + "：图纸侧已打开（" + rep.Message + "）");
                    openedByUs = true;

                    // ── 2) 打开期弹窗消化（8D 视图更新→是 / 尺寸检查→取消，PopupAutoYes 代点）──
                    SaveAsDwgUi.PumpDuringWait = null;
                    SaveAsDwgUi.WaitPopupsSettled(30000, delegate(string m) { _log(fileName + "：" + m); });
                    _log(fileName + "：打开期弹窗已消化");

                    IntPtr mdi = SaveAsDwgUi.FindExbMdiChild(exb);
                    if (mdi == IntPtr.Zero)
                    {
                        _log(fileName + "：找不到标题含本 exb 的图纸 MDI 子窗口（保存/另存无处可投）");
                        return "2D:定位失败";
                    }

                    // ── 3) 保存落盘（视图更新结果写进源 exb；mtime 变 = 可继续的同步信号）──
                    if (!SaveAsDwgUi.SaveViaCommand(mdi, exb, 20000,
                            delegate(string m) { _log(fileName + "：" + m); }))
                    {
                        _log(fileName + "：保存未落盘（20 秒内 exb mtime 无变化）——按产线口径中止");
                        return "2D:保存失败";
                    }

                    // ── 4) 导出：pdf 先（另存会切走文档关联名，打印命令按活动文档找）──
                    var pieces = new List<string>();
                    bool allOk = true;
                    if (opt.Pdf)
                    {
                        if (StopFlag()) { pieces.Add("pdf停"); allOk = false; }
                        else
                        {
                            string pdfOut = Path.Combine(outDir, stem + ".pdf");
                            bool needProps = !_pdfPropsConfigured;
                            bool r = ExportPdfViaPrint(exb, pdfOut, mdi, needProps);
                            if (r && needProps) { _pdfPropsConfigured = true; _log("pdf 属性面板已配置一次，后续不再进"); }
                            pieces.Add("pdf" + (r ? "√" : "×"));
                            allOk &= r;
                        }
                    }
                    if (opt.Dxf)
                    {
                        if (StopFlag()) { pieces.Add("dxf停"); allOk = false; }
                        else
                        {
                            bool r = ExportViaDialog(mdi, exb, outDir, stem, "dxf", "2007");
                            pieces.Add("dxf" + (r ? "√" : "×"));
                            allOk &= r;
                        }
                    }
                    if (opt.Dwg)
                    {
                        if (StopFlag()) { pieces.Add("dwg停"); allOk = false; }
                        else
                        {
                            bool r = ExportViaDialog(mdi, exb, outDir, stem, "dwg", "2007");
                            pieces.Add("dwg" + (r ? "√" : "×"));
                            allOk &= r;
                        }
                    }
                    ok = allOk;
                    return string.Join("", pieces.ToArray());
                }
            }
            finally
            {
                SaveAsDwgUi.PumpDuringWait = null;
                PopupAutoYes.DeclineUpdate3dLocation = oldDecline;   // 批量口径标志用完即还

                // ── 5) 收尾关图纸：CLOSEDOC = 保存 + WM_CLOSE（不留脏图）；关不掉退回
                //       WM_CLOSE + 代点「否」，绝不强关；最后清障（非预期模态框点取消/否）。──
                if (openedByUs)
                {
                    // 5.0) 收尾前静默（2026-11-12 -07管 闪退实证）：另存是模态命令，「产物落盘」≠
                    //      「命令退栈」。C# 产物判据已过时，迟弹出的另存框可能还开着（-07 管：dwg 框
                    //      迟到且 C# 已放弃），此时 CLOSEDOC 的同步保存命令插进未退干净的模态栈
                    //      → 原生层闪退（IRONCAD 进程无痕消失，事件日志/WER 全空）。
                    //      等到「无另存框且无待消化模态，持续 2 秒」或 30s 超时才许进关图。
                    try { SettleBeforeClose(fileName); }
                    catch (Exception ex) { _log("收尾静默异常：" + ex.Message); }

                    try
                    {
                        var rc = ViewBridge.Request("CLOSEDOC", fileName, true, 60000, _log);
                        if (!rc.Ok)
                            _log(fileName + "：CLOSEDOC 未成功（" + rc.Status + " " + rc.Message + "）→ 退回 WM_CLOSE 不保存");
                        else
                            _log(fileName + "：已保存并关闭（" + rc.Message + "）");
                    }
                    catch (Exception ex) { _log("CLOSEDOC 异常：" + ex.Message); }

                    try { CloseExbDrawing(fileName); } catch (Exception ex) { _log("关闭图纸兜底异常：" + ex.Message); }

                    try { SaveAsDwgUi.DismissLingeringModals(delegate(string m) { _log(m); }); }
                    catch (Exception ex) { _log("清障异常：" + ex.Message); }

                    // ── 6) 清理 CAXA 保存链自动生成的同名 .bak 备份（仅输出文件夹；源文件 .bak 不碰）──
                    CleanupBakFor(outDir, stem);
                }
            }
        }

        /// <summary>
        /// CAXA 保存链对已存在的文件落盘时自动生成同名 .bak 备份（如「xx.dwg」旁的「xx.bak」，实测口径）。
        /// 每张图彻底完成后定向删除：仅输出目录下的 stem.bak（用户 2026-11-12 指令：源文件的 .bak 是
        /// 用户自己的备份，绝不碰，只清输出文件夹）。只删文件不递归；删除失败仅记日志不报错。
        /// </summary>
        /// <summary>
        /// 收尾前静默（2026-11-12 -07管 闪退对策）：等「无另存框且无待消化模态」持续 2 秒（或 30s 超时）
        /// 才允许进 CLOSEDOC。期间若发现本流程迟到的另存框（C# 已按产物判据放弃、框才弹出来的），
        /// 点「取消」跳过该格式——框是本流程开的，取消零副作用。
        /// </summary>
        private void SettleBeforeClose(string fileName)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int quiet = 0;
            while (sw.ElapsedMilliseconds < 30000)
            {
                if (SaveAsDwgUi.CancelLingeringSaveDialog(m => _log(fileName + "：" + m)))
                {
                    quiet = 0;                       // 刚处理过迟到的另存框，重新计静默
                    System.Threading.Thread.Sleep(600);
                    continue;
                }
                if (PopupAutoYes.ModalCount > 0)
                {
                    quiet = 0;
                    System.Threading.Thread.Sleep(400);
                    continue;
                }
                quiet += 400;
                if (quiet >= 2000) return;           // 连续 2 秒干净 → 模态栈已退净
                System.Threading.Thread.Sleep(400);
            }
            _log(fileName + "：收尾前静默等待 30s 超时（仍有弹窗），按现状进关图（风险自担）");
        }

        private void CleanupBakFor(string outDir, string stem)
        {
            if (string.IsNullOrEmpty(stem) || string.IsNullOrEmpty(outDir)) return;
            string bak = Path.Combine(outDir, stem + ".bak");
            try
            {
                if (File.Exists(bak))
                {
                    File.Delete(bak);
                    _log("已清理备份 " + bak);
                }
            }
            catch (Exception ex) { _log("清理备份失败（忽略）：" + bak + " → " + ex.Message); }
        }

        // 批量期间「EXB To PDF.drv」属性面板是否已配置过（同会话配一次就够）
        private static bool _pdfPropsConfigured;

        /// <summary>
        /// exb→pdf 打印对话框代点（参考工程 DlgBatchConvert.ExportPdfViaPrint 原样移植）。
        /// 跑在后台线程：只做跨进程消息 + 文件轮询，不泵消息。判成功 = PDF 产物存在且 >0 且大小稳定。
        /// </summary>
        private bool ExportPdfViaPrint(string exbPath, string outPath, IntPtr mdi, bool configureProps)
        {
            string fileName = Path.GetFileName(exbPath);
            // ① 清旧产物（PDF 保存框覆盖提示会卡住）
            try
            {
                if (File.Exists(outPath))
                {
                    _log("删除旧产物 " + outPath + "（" + new FileInfo(outPath).Length + " 字节）");
                    new FileInfo(outPath).Delete();
                }
            }
            catch (Exception ex) { _log("删除旧产物失败（忽略）：" + ex.Message); }

            // ② 前置图纸窗口（确保键盘兜底 Ctrl+P 能打到正确目标）
            IntPtr frame = FindDrawingWindow(fileName);
            if (frame != IntPtr.Zero)
            {
                try { SetForegroundWindow(frame); System.Threading.Thread.Sleep(200); }
                catch { }
            }

            // ③ 派发打印命令（CRX EXEC_NATIVE 白名单 CX_ 前缀）
            var pr = ViewBridge.Request("EXEC_NATIVE", "CX_print", false, 30000, _log);
            if (!pr.Ok)
            {
                _log("CX_print 未投出：" + pr.Status + " " + pr.Message);
                return false;
            }
            _log("CX_print 已投出（" + (pr.Message ?? "") + "）");

            // ④ 等打印对话框；投递无反应时 Ctrl+P 兜底
            IntPtr dlg = PrintPdfUi.WaitPrintDialog(30000, delegate(string m) { _log(m); });
            if (dlg == IntPtr.Zero)
            {
                PrintPdfUi.TriggerPrintByShortcut(frame, delegate(string m) { _log(m); });
                dlg = PrintPdfUi.WaitPrintDialog(15000, delegate(string m) { _log(m); });
            }
            if (dlg == IntPtr.Zero)
            {
                _log("打印对话框未能打开（CX_print / Ctrl+P 两路均未见「打印对话框」）");
                return false;
            }

            // ⑤ 对话框参数代点：图幅 → PDF 打印机 → 纸张 → 黑白 → 横向 → 标准图形 → 点「打印」
            string sheet = "";
            bool configured = PrintPdfUi.ConfigureAndPrint(dlg, out sheet, delegate(string m) { _log(m); }, configureProps);
            if (!configured)
            {
                _log("打印对话框代点失败（图幅=" + (sheet.Length == 0 ? "未识别" : sheet) + "；对话框留在原地）");
                return false;
            }

            // ⑥ PDF 保存框：填同名 .pdf → 点「保存」
            IntPtr save = PrintPdfUi.WaitPdfSaveDialog(30000, dlg, delegate(string m) { _log(m); });
            if (save != IntPtr.Zero)
            {
                if (!PrintPdfUi.FillSaveDialog(save, outPath, delegate(string m) { _log(m); }))
                {
                    _log("PDF 保存框代点失败（对话框留在原地）");
                    return false;
                }
            }
            else
            {
                _log("未出现 PDF 保存框，按驱动直接落盘继续等产物");
            }

            // ⑦ 轮询 PDF 产物（驱动异步写盘，要求大小连续稳定）
            bool produced = PrintPdfUi.WaitPdfProduct(outPath, 60000, delegate(string m) { _log(m); });

            // ⑧ 收尾：等打印对话框退场（模态框里动文档 = 关图闪退同触发面）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000 && PrintPdfUi.IsVisibleWindow(dlg))
                System.Threading.Thread.Sleep(300);
            if (PrintPdfUi.IsVisibleWindow(dlg))
                _log("打印对话框 15 秒仍未退场，按已提交继续（关图前请留意）");

            if (produced)
            {
                try { _log("PDF 已产出：" + outPath + "（" + new FileInfo(outPath).Length + " 字节，图幅=" + sheet + "）"); }
                catch { }
                return true;
            }
            _log("PDF 未产出（图幅=" + (sheet.Length == 0 ? "未识别" : sheet) + "）");
            return false;
        }

        /// <summary>
        /// 对话框代点导出一次（参考工程 DlgBatchConvert.ExportViaDialog 原样移植，「另存为dwg/dxf」产线通道）。
        /// 预删输出 → PostMessage ID_FILE_SAVE_AS 开「另存文件」框 → FillAndSave（消息通道 + MSAA
        /// 双保险选类型）→ WaitProductWithRetry（3 次重试，判成功 = 产物存在且另存框已关闭）。
        /// ★ 必须跑在后台线程：泵钩子为 null，等待轮询不被模态循环冻结。
        /// ★ 另存会把文档关联名切到输出文件，mdi 子窗口句柄不变仍可继续投递（可连续多格式导出）。
        /// </summary>
        private bool ExportViaDialog(IntPtr mdi, string exbPath, string outDir, string stem, string ext, string dwgVersion)
        {
            string outPath = Path.Combine(outDir, stem + "." + ext);
            try
            {
                if (File.Exists(outPath)) File.Delete(outPath);
                if (File.Exists(outPath + ".exb")) File.Delete(outPath + ".exb");
            }
            catch { }
            _log("导出 " + ext + " → " + outPath + "（另存文件框代点，版本=" + dwgVersion + "）");

            IntPtr frame = FindDrawingWindow(Path.GetFileName(exbPath));
            IntPtr dlg = SaveAsDwgUi.OpenSaveAsDialog(mdi, frame, 20000,
                delegate(string m) { _log(m); });
            if (dlg == IntPtr.Zero)
            {
                _log(ext + "：另存文件框未能打开（ID_FILE_SAVE_AS / Ctrl+Shift+S 两路均未见对话框）");
                return false;
            }
            if (!SaveAsDwgUi.FillAndSave(dlg, outPath, ext, delegate(string m) { _log(m); }, dwgVersion))
            {
                _log(ext + "：另存文件框代点失败（对话框留在原地，详见取证日志）");
                return false;
            }
            bool okR = SaveAsDwgUi.WaitProductWithRetry(mdi, outPath, ext, 30000, 3,
                delegate(string m) { _log(m); });
            if (okR)
            {
                try { _log("导出 " + ext + " 完成：" + outPath + "（" + new FileInfo(outPath).Length + " B）"); }
                catch { }
            }
            else
            {
                _log(ext + "：未产出（3 次尝试）：保存类型提交/覆盖提示/校验弹窗问题，详见取证日志");
            }
            return okR;
        }

        /// <summary>关掉我们打开的图纸：WM_CLOSE → 有保存框代点「否」（不保存）→ 等窗口消失。
        /// 必须在 PopupAutoYes 租约归还之后调用，否则「是/否」会打架。</summary>
        private void CloseExbDrawing(string fileName)
        {
            IntPtr h = FindDrawingWindow(fileName);
            if (h == IntPtr.Zero) return;
            DrawingProbe.RequestClose(h);
            bool gone = WaitUntil(delegate()
            {
                bool alive = DrawingProbe.IsAlive(h) || FindDrawingWindow(fileName) != IntPtr.Zero;
                if (alive)
                {
                    // 口径同预热载体图纸：不保存。更新已由 CLOSEDOC/SaveViaCommand 写盘，
                    // 关闭期不保存 = 转换器对源文件零副作用。
                    string handled = DrawingProbe.AnswerNoOnSaveDialog();
                    if (handled != null) _log(handled);
                }
                return !alive;
            }, 15000, 250);
            _log(fileName + "：" + (gone ? "已不保存关闭" : "15 秒内没关掉（可能被别的模态框挡住），请手工关闭"));
        }

        /// <summary>
        /// 找图纸文档窗口（参考工程规则 20 口径：**全名 OR 文件名 stem**，两者都命中才算——
        /// CAXA「保存更改→是」后会改写 MDI 标题（去 .exb / 加路径前缀），只按全名子串匹配必失配。
        /// stem 命中要求边界（stem 后跟「.」或标题以 stem 结尾），避免「壳体」误命中「壳体2」。
        /// </summary>
        private static IntPtr FindDrawingWindow(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return IntPtr.Zero;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            IntPtr byStem = IntPtr.Zero;
            try
            {
                foreach (IntPtr h in DrawingProbe.ListDraftDocs())
                {
                    string t = WindowTextOf(h);
                    if (string.IsNullOrEmpty(t)) continue;
                    if (t.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0) return h;   // 全名优先
                    if (byStem != IntPtr.Zero || stem.Length < 2) continue;
                    string tt = t.TrimEnd().TrimEnd('*').TrimEnd();
                    if (tt.EndsWith(stem, StringComparison.OrdinalIgnoreCase) ||
                        tt.IndexOf(stem + ".", StringComparison.OrdinalIgnoreCase) >= 0)
                        byStem = h;
                }
            }
            catch { }
            return byStem;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static string WindowTextOf(IntPtr h)
        {
            try
            {
                var sb = new System.Text.StringBuilder(512);
                GetWindowTextW(h, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>条件轮询（后台线程专用：绝不泵消息，泵是 CAXA 主线程的事）。</summary>
        private bool WaitUntil(Func<bool> condition, int timeoutMs, int pollMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool okC = false;
                try { okC = condition(); } catch { }
                if (okC) return true;
                System.Threading.Thread.Sleep(pollMs);
            }
            return false;
        }

        // ---------------------------------------------------------- 文档枚举 / 校验 / 泵消息
        private IZDoc FindOpenDoc(string fullPath, string fileName, string stem)
        {
            try
            {
                object o = _app.GetAllOpenDocs();
                if (o == null) return null;
                var arr = o as Array;
                if (arr != null)
                {
                    foreach (object d in arr)
                    {
                        var doc = d as IZDoc; if (doc == null) continue;
                        string n = ""; try { n = doc.Name ?? ""; } catch { }
                        if (string.Equals(n, fullPath, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(n, stem, StringComparison.OrdinalIgnoreCase)) return doc;
                    }
                }
            }
            catch (Exception ex) { _log("GetAllOpenDocs 异常：" + ex.Message); }
            return null;
        }

        private string VerifyWithRetry(string dir, string stem, string ext, DateTime since, bool existedBefore)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    string p = Path.Combine(dir, stem + ext);
                    if (File.Exists(p))
                    {
                        var fi = new FileInfo(p);
                        if (fi.Exists && fi.Length > 0) return fi.FullName;
                    }
                }
                catch { }
                Pump(500);
            }
            return null;
        }

        /// <summary>泵消息 + 分段 Sleep（不裸 Sleep 长等，避免饿死宿主消息泵）。</summary>
        private void Pump(int ms)
        {
            int steps = Math.Max(1, ms / 50);
            for (int i = 0; i < steps; i++)
            {
                try { Application.DoEvents(); } catch { }
                System.Threading.Thread.Sleep(50);
            }
        }
    }
}
