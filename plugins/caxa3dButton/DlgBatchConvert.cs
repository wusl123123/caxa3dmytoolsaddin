﻿﻿﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    /// <summary>
    /// 「批量格式转换」次级界面（Win2000 极简风，对齐本工程其余界面）。
    ///
    /// 需求口径（2026-11-10）：
    ///   · 第一行 = 文件夹选择；第二行 = 输出文件夹选择（不选 = 与源文件同文件夹；
    ///     指定后不存在会自动创建，创建失败拒绝开始）。勾选项逐文件执行：
    ///       exb → dxf / dwg / pdf（图纸导出）；ics 第一个对象 → x_t / stp / stl（模型导出）。
    ///   · ★ ics 只处理**场景第一个对象**（GetTopElement），**不穿透**：不递归子件、
    ///     不给子件单独出文件，整个顶层对象只出一套输出。
    ///   · ★ exb 转换前必须先做**图纸更新**（口径参考 caxa3dCXDFTViewUpdate：先更新再干活）。
    ///     这里走本项目已实证的纯 API 通道 IZBaseApp.UpdateDraftViews（Update3DBomApi，memid=72），
    ///     目标图未打开时静默且当场改写 exb —— 更新失败一律不转换（"更新后才能转换"）。
    ///
    /// 通道选择（能力调研 2026-11-10 离线取证 + interop 差集复核，全部 RCW 直调级 PRESENT）：
    ///   模型导出  IZAssembly::Export(槽13) / IZPart::Export(槽15)，(文件名, eZModelType)；
    ///             eZModelType：X_T=0 / STEP=3 / STL(Z_MODEL_STEREOLITHOGRAPHY)=12。
    ///   图纸导出  ★ 2026-11-10 第二轮实机实锤：exb 文档在 Draft 进程（CDRAFT_M_IC）里，
    ///             Scene 侧 GetAllOpenDocs 52 个全是 ics ⇒ IZDrawingDoc 在 Scene 侧**根本拿不到**，
    ///             纯 API 导出通道不存在。改走 CRX 桥新命令 EXPORT_DWGDXF（本工程 Bridge/CRX）：
    ///             Draft 进程内 CRxDbDatabase::saveAs(outPath, false, dwgVer)，扩展名定 dwg/dxf，
    ///             dwgVer = CRxDb::CRxDbDwgVersion（R13=19/R14=21/R2000=23/R2004=25/R2007=27/
    ///             R2010=29/R2013=31）。
    ///   ★ 2026-11-10 第三轮（用户指路）：图纸环境另有**专用转换器**
    ///             IZTDExportControl::Export(bstrFileName, eZExportDrawingType)，
    ///             经文档 IDispatch 后期绑定调用 ⇒ 已升为主通道（CRX 命令 CONVERT_DXF），
    ///             dxf/dwg/pdf 全覆盖（eZExportDrawingType 实测：PDF=19、DWG_R2018=28、DXF_R2018=29…）；
    ///             数据层 saveAs 降级为 dxf/dwg 的备用通道。
    ///   拿文档对象：GetAllOpenDocs(槽45) 枚举按 doc.Name 匹配（对 ics 权威）；GetOpenDocByName(9) 仅兜底
    ///             —— 它有 get-or-open 语义（实机实锤：从未打开的文件也"命中"=背后静默开图）。
    ///   STL       已按用户要求剔除（实机两轮均静默无输出，宿主 STL 翻译器不走数据层）。
    ///   SAT       eZModelType.Z_MODEL_SAT=1（enums.csv 实测）。
    ///   关图纸    不走 CRX 桥（文件桥等待不能在 UI 线程内联做，桥等待必须放后台线程）——
    ///             直接对图纸 MDI 子窗口投 WM_CLOSE（DrawingProbe.RequestClose）+ 代点「否」
    ///             （DrawingProbe.AnswerNoOnSaveDialog，口径同预热载体图纸：**不保存**。
    ///             exb 的更新已由 UpdateDraftViews 写盘，关闭期不保存 = 转换器对源文件零副作用）。
    ///   关 ics    关闭链逐级加码 + 权威核验（回查 GetAllOpenDocs 已无此文档才算关掉，不看返回值）：
    ///             app.CloseFile(doc)（TLB 槽 35）→ 没关掉上 IZDoc::Close2(-1)（槽 23 vtable，
    ///             DesignEnvTool.CloseDocByPtr；外链子零件 ics 只有 Close2 关得掉，
    ///             CloseFile/doc.Close() 对外链 ics 都是假成功）→ 最后才 doc.Close() 兜底。
    ///
    /// 判成功只认权威副作用：输出文件存在且 Length&gt;0（导出文件名按帮助 = 全路径+主名、
    /// 不带扩展名，宿主按类型自动补；若宿主补出的扩展名与预期不符，按目录新文件扫描
    /// + 改名兜底，两种行为都能落地）。
    ///
    /// 线程纪律：全部 COM/界面操作都在 UI 线程（= CAXA 主线程）同步做，等待一律
    /// WaitUntil + DoEvents（Thread.Sleep 堵主线程会饿死宿主消息泵，历史闪退教训）。
    /// 代点借还：打开/导出期借 PopupAutoYes（数据源更新等弹框点「是」），导出完归还，
    /// 关图纸期**绝不带着代点租约**（否则保存框会被它抢点「是」，与 AnswerNoOnSaveDialog 打架）。
    /// </summary>
    internal sealed class DlgBatchConvert : Form
    {
        private readonly IZBaseApp _app;

        private Button _btnPick;
        private Button _btnOutPick;
        private Button _btnStart;
        private Button _btnStop;
        private Label _lblFolder;
        private Label _lblOut;
        private CheckBox _chkDxf, _chkDwg, _chkPdf;
        private CheckBox _chkXt, _chkStp, _chkSat;
        private ComboBox _cmbVersion;
        private ListView _list;
        private Label _lblStatus;
        private TextBox _txtLog;

        private volatile bool _running;
        private volatile bool _stopRequested;
        private string _folder;
        private string _outFolder;   // 输出文件夹；null = 与源文件同文件夹

        // ── eZModelType（enums.csv 实测值；用整型常量 + 调用处转型，避免 interop 枚举名差异）──
        private const int ModelTypeXT = 0;    // Z_MODEL_X_T
        private const int ModelTypeSat = 1;   // Z_MODEL_SAT（ACIS）
        private const int ModelTypeStep = 3;  // Z_MODEL_STEP

        private const int OpenReadyTimeoutMs = 40000;   // 等图纸窗口出现（含 Draft 冷启动）
        private const int CloseTimeoutMs = 15000;       // 等 WM_CLOSE 后窗口消失

        private static readonly Color RowYellow = Color.FromArgb(0xFF, 0xFA, 0xC8);
        private static readonly Color RowRed = Color.FromArgb(0xFF, 0xC8, 0xC8);
        private static readonly Color RowGreen = Color.FromArgb(0xD8, 0xF0, 0xD8);

        // 批量开始前宿主已打开的文档路径集合。整个批量期间用它判断 wasOpenBefore——
        // 不在 baseline 里的（不管是 OpenFile2 直接开的还是装配体自动加载的子零件）都算"我们开的"，
        // 批量收尾必须关。修 bug a：exb 装配体 OpenFile2 会把子零件 ics 加载进内存，
        // 这些子零件在 GetAllOpenDocs 里出现但不该留在宿主里（11-11 压力测试实锤）。
        private static HashSet<string> _baselineOpenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 批量转换期 EXB To PDF.drv 属性面板是否已配置过（驱动属性同会话内保持，第一张图配一次就够）
        private static bool _pdfPropsConfigured;

        public DlgBatchConvert(IZBaseApp app)
        {
            _app = app;
            Text = "批量格式转换";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(880, 680);
            ShowInTaskbar = false;

            var top = new Panel { Dock = DockStyle.Top, Height = 150 };

            _btnPick = MakeButton("选择文件夹", 10, 12, 100);
            _btnPick.Click += BtnPick_Click;
            _lblFolder = new Label
            {
                Text = "文件夹：未选择",
                AutoSize = false, AutoEllipsis = true,
                Location = new Point(118, 16), Size = new Size(450, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _btnStart = MakeButton("开始转换", 690, 12, 84);
            _btnStart.Enabled = false;
            _btnStart.Click += BtnStart_Click;
            _btnStop = MakeButton("停止", 782, 12, 84);
            _btnStop.Enabled = false;
            _btnStop.Click += delegate { _stopRequested = true; _lblStatus.Text = "正在停止……（当前文件做完后停下）"; };

            // 第二行：输出文件夹（不选 = 与源文件同文件夹）
            _btnOutPick = MakeButton("选择输出文件夹", 10, 46, 100);
            _btnOutPick.Click += BtnOutPick_Click;
            _lblOut = new Label
            {
                Text = "输出到：与源文件同文件夹（可选）",
                AutoSize = false, AutoEllipsis = true,
                Location = new Point(118, 50), Size = new Size(450, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var lblExb = new Label { Text = "exb 转换：", Location = new Point(10, 84), AutoSize = true };
            _chkDxf = MakeCheck("exb → dxf", 88, 84, OnCheckChanged);
            _chkDwg = MakeCheck("exb → dwg", 190, 84, OnCheckChanged);
            _chkPdf = MakeCheck("exb → pdf", 292, 84, OnCheckChanged);
            var lblVer = new Label { Text = "DWG/DXF 版本：", Location = new Point(402, 84), AutoSize = true };
            _cmbVersion = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(512, 81), Size = new Size(100, 24)
            };
            _cmbVersion.Items.AddRange(new object[] { "R13", "R14", "R2000", "R2004", "R2007", "R2010", "R2013" });
            _cmbVersion.SelectedIndex = 4;   // R2007

            var lblIcs = new Label { Text = "ics 转换：", Location = new Point(10, 114), AutoSize = true };
            _chkXt = MakeCheck("第一个对象 → x_t", 88, 114, OnCheckChanged);
            _chkStp = MakeCheck("第一个对象 → stp", 230, 114, OnCheckChanged);
            _chkSat = MakeCheck("第一个对象 → sat", 372, 114, OnCheckChanged);
            var lblNote = new Label
            {
                Text = "（不穿透；exb 先更新）",
                Location = new Point(514, 114), AutoSize = true,
                ForeColor = Color.DimGray
            };

            top.Controls.AddRange(new Control[]
            {
                _btnPick, _lblFolder, _btnOutPick, _lblOut, _btnStart, _btnStop,
                lblExb, _chkDxf, _chkDwg, _chkPdf, lblVer, _cmbVersion,
                lblIcs, _chkXt, _chkStp, _chkSat, lblNote
            });

            _list = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details,
                FullRowSelect = true, GridLines = true, MultiSelect = false
            };
            _list.Columns.Add("序号", 46);
            _list.Columns.Add("文件", 250);
            _list.Columns.Add("操作", 96);
            _list.Columns.Add("状态", 100);
            _list.Columns.Add("耗时", 62);
            _list.Columns.Add("信息", 300);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 152 };
            _lblStatus = new Label
            {
                Text = "就绪：先选文件夹、勾选项，再点「开始转换」",
                AutoSize = false, Dock = DockStyle.Top, Height = 24,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0),
                ForeColor = Color.DimGray
            };
            _txtLog = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 9F)
            };
            bottom.Controls.Add(_txtLog);
            bottom.Controls.Add(_lblStatus);

            Controls.Add(_list);
            Controls.Add(top);
            Controls.Add(bottom);
            _list.BringToFront();
        }

        private void OnCheckChanged(object sender, EventArgs e)
        {
            _btnStart.Enabled = !_running && _folder != null && AnyChecked();
        }

        private bool AnyChecked()
        {
            return _chkDxf.Checked || _chkDwg.Checked || _chkPdf.Checked ||
                   _chkXt.Checked || _chkStp.Checked || _chkSat.Checked;
        }

        private static Button MakeButton(string text, int x, int y, int w)
        {
            return new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 28) };
        }

        private static CheckBox MakeCheck(string text, int x, int y, EventHandler handler)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Location = new Point(x, y) };
            c.CheckedChanged += handler;
            return c;
        }

        private void Log(string s)
        {
            Diagnostics.Log("bc: " + s);
            UiPost(delegate
            {
                try
                {
                    if (_txtLog.TextLength > 60000) _txtLog.Clear();
                    _txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + s + Environment.NewLine);
                }
                catch { }
            });
        }

        // ================================================================ 选择文件夹

        /// <summary>选输出文件夹；选了源文件夹本身 = 恢复"与源文件同文件夹"默认。</summary>
        private void BtnOutPick_Click(object sender, EventArgs e)
        {
            if (_running) return;
            using (var dlg = new FolderBrowserDialog
            {
                Description = "选择输出文件夹（转换结果存放位置；不选 = 与源文件同文件夹）",
                ShowNewFolderButton = true
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!string.IsNullOrEmpty(_folder) &&
                    string.Equals(dlg.SelectedPath, _folder, StringComparison.OrdinalIgnoreCase))
                {
                    _outFolder = null;
                    _lblOut.Text = "输出到：与源文件同文件夹（可选）";
                    Log("输出文件夹 = 源文件夹本身，已恢复默认（与源文件同文件夹）");
                    return;
                }
                _outFolder = dlg.SelectedPath;
                _lblOut.Text = "输出到：" + _outFolder;
                Log("已选输出文件夹：" + _outFolder);
            }
        }

        /// <summary>某源文件的输出目录：指定了输出文件夹就用它，否则用源文件所在目录。</summary>
        private string OutDirFor(string srcPath)
        {
            if (!string.IsNullOrEmpty(_outFolder)) return _outFolder;
            try { return Path.GetDirectoryName(srcPath) ?? ""; } catch { return ""; }
        }

        /// <summary>
        /// 批次收尾统一删除 .bak 备份文件（用户 2026-11-11 指令）。
        /// 来源：CAXA 保存链落盘时对已存在的输出文件自动生成同名 .bak（如「xx.dwg」旁的「xx.bak」）。
        /// 清理范围：① 本批每个源文件在其输出目录里的 stem.bak（定向删，精确到本批产物）；
        /// ② 指定了输出文件夹时，再对输出文件夹顶层做一次 *.bak 清扫（兜住多轮同名产物留下的备份）。
        /// 只删文件不递归子目录；删除失败仅记日志不报错（.bak 属副产物，绝不影响批次结果）。
        /// </summary>
        private void CleanupBakFiles(List<string> touchedSrc)
        {
            int n = 0;
            var errors = new List<string>();
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ① 定向删：本批源文件的输出目录下 stem.bak
            if (touchedSrc != null)
                foreach (string src in touchedSrc)
                {
                    string stem;
                    try { stem = Path.GetFileNameWithoutExtension(src); } catch { continue; }
                    if (string.IsNullOrEmpty(stem)) continue;
                    string dir = OutDirFor(src);
                    if (string.IsNullOrEmpty(dir)) continue;
                    dirs.Add(dir);
                    TryDelBak(Path.Combine(dir, stem + ".bak"), ref n, errors);
                }

            // ② 指定输出文件夹时顶层 *.bak 清扫（含 ics 轮等未走 ① 的遗漏）
            if (!string.IsNullOrEmpty(_outFolder) && Directory.Exists(_outFolder))
            {
                dirs.Add(_outFolder);
                try
                {
                    foreach (string bak in Directory.GetFiles(_outFolder, "*.bak", SearchOption.TopDirectoryOnly))
                        TryDelBak(bak, ref n, errors);
                }
                catch (Exception ex) { errors.Add(_outFolder + " :: " + ex.Message); }
            }

            Log("清理 .bak 备份文件：删除 " + n + " 个" +
                (errors.Count > 0 ? "（" + errors.Count + " 个失败：" +
                 string.Join("; ", errors.GetRange(0, Math.Min(3, errors.Count)).ToArray()) + "）" : ""));
        }

        private static void TryDelBak(string path, ref int n, List<string> errors)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                n++;
            }
            catch (Exception ex) { errors.Add(Path.GetFileName(path) + " :: " + ex.Message); }
        }

        private void BtnPick_Click(object sender, EventArgs e)
        {
            if (_running) return;
            using (var dlg = new FolderBrowserDialog
            {
                Description = "选择包含 exb / ics 文件的文件夹（本级优先，找不到时含子文件夹）",
                ShowNewFolderButton = false
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _folder = dlg.SelectedPath;
                _lblFolder.Text = "文件夹：" + _folder;
                int nExb = Enumerate(_folder, "*.exb").Length;
                int nIcs = Enumerate(_folder, "*.ics").Length;
                Log("已选文件夹：" + _folder + "（exb " + nExb + " 个，ics " + nIcs + " 个）");
                _btnStart.Enabled = AnyChecked();
                _lblStatus.Text = "已选文件夹，共 exb " + nExb + " / ics " + nIcs + "；请勾选转换项后点「开始转换」";
            }
        }

        /// <summary>枚举：本级优先；本级 0 个时递归子文件夹（跳过无权限目录）。</summary>
        private static string[] Enumerate(string folder, string pattern)
        {
            var list = new List<string>();
            try
            {
                list.AddRange(Directory.GetFiles(folder, pattern, SearchOption.TopDirectoryOnly));
                if (list.Count == 0)
                {
                    var stack = new Stack<string>();
                    stack.Push(folder);
                    while (stack.Count > 0)
                    {
                        string dir = stack.Pop();
                        try { list.AddRange(Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly)); }
                        catch { }
                        try
                        {
                            var subs = Directory.GetDirectories(dir);
                            for (int i = subs.Length - 1; i >= 0; i--) stack.Push(subs[i]);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Trace.WriteLine("枚举异常 " + pattern + ": " + ex.Message); }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }

        // ================================================================ 批处理主循环

        private void BtnStart_Click(object sender, EventArgs e)
        {
            if (_running) return;
            if (string.IsNullOrEmpty(_folder) || !Directory.Exists(_folder))
            {
                MessageBox.Show("请先点「选择文件夹」。", "批量格式转换",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!AnyChecked())
            {
                MessageBox.Show("请至少勾选一项转换内容。", "批量格式转换",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 输出文件夹：指定了就必须可用（不存在则尝试创建；创建失败=拒绝开始）
            if (!string.IsNullOrEmpty(_outFolder))
            {
                try
                {
                    if (!Directory.Exists(_outFolder)) Directory.CreateDirectory(_outFolder);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("输出文件夹不可用（创建失败）：\n" + _outFolder + "\n\n" + ex.Message,
                        "批量格式转换", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            bool wantExb = _chkDxf.Checked || _chkDwg.Checked || _chkPdf.Checked;
            bool wantIcs = _chkXt.Checked || _chkStp.Checked || _chkSat.Checked;
            var files = new List<string>();
            if (wantExb) files.AddRange(Enumerate(_folder, "*.exb"));
            if (wantIcs) files.AddRange(Enumerate(_folder, "*.ics"));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            if (files.Count == 0)
            {
                MessageBox.Show("该文件夹（含子文件夹）下没有可处理的 exb / ics 文件：\n" + _folder,
                    "批量格式转换", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _list.Items.Clear();
            for (int i = 0; i < files.Count; i++)
            {
                var item = new ListViewItem((i + 1).ToString());
                item.SubItems.Add(Path.GetFileName(files[i]));
                item.SubItems.Add(files[i].EndsWith(".exb", StringComparison.OrdinalIgnoreCase) ? "exb" : "ics");
                item.SubItems.Add("等待"); item.SubItems.Add(""); item.SubItems.Add("");
                _list.Items.Add(item);
            }

            // ★ 快照批量开始前宿主已打开的文档集合——后续 ProcessIcs 用它判断 wasOpenBefore，
            //   修 bug a：exb 装配体 OpenFile2 会把子零件 ics 加载进内存，子零件在 GetAllOpenDocs
            //   里出现但不该留在宿主里（11-11 压力测试实锤：50+ ics 被判"本来就已打开"没关）。
            try
            {
                _baselineOpenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                object openDocs = _app.GetAllOpenDocs();
                if (openDocs is System.Collections.IEnumerable en)
                {
                    foreach (object o in en)
                    {
                        try
                        {
                            var d = o as IZDoc;
                            if (d != null) _baselineOpenNames.Add(SafeName(d));
                        }
                        catch { }
                    }
                }
                Log("宿主快照：批量开始前已有 " + _baselineOpenNames.Count + " 个打开文档（这些本批次不动）");
            }
            catch (Exception ex) { Log("宿主快照采集失败：" + ex.Message + "（所有文档都视为需要关）"); _baselineOpenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

            _running = true; _stopRequested = false;
            _btnPick.Enabled = false;
            _btnStart.Enabled = false;
            _btnStop.Enabled = true;

            // ★ CRX 预检（毫秒级 marker 检查）：exb 全流程依赖图纸环境 CRX，
            // 图纸环境未开时 marker 不存在 → IsCrxLoaded() 立即 false → 不再等 45 秒，
            // 整批 exb 在 UI 行上直接标「跳过」并计入 skip 数（不计 fail），继续处理 ICS。
            // ICS 是 Scene 侧 RCW 直调，不依赖 CRX → 只要勾选了 x_t/stp/sat 就能正常跑。
            bool hasExb = files.Any(f => f.EndsWith(".exb", StringComparison.OrdinalIgnoreCase));
            bool hasIcs = files.Any(f => f.EndsWith(".ics", StringComparison.OrdinalIgnoreCase));
            int skipExb = 0;
            if (hasExb && !ExbBridge.IsCrxLoaded())
            {
                Log("预检：CRX 未就绪（图纸环境未开或 CRX 未加载）——本批次跳过所有 exb（" +
                    "请先进入图纸环境再跑 exb；ICS 不依赖 CRX，仍会处理）");
                for (int i = 0; i < files.Count; i++)
                {
                    if (!files[i].EndsWith(".exb", StringComparison.OrdinalIgnoreCase)) continue;
                    _list.Items[i].SubItems[3].Text = "跳过(CRX)";
                    _list.Items[i].SubItems[5].Text =
                        "CRX 未就绪：先进入图纸环境（主界面「图纸环境预热」）再跑 exb；ICS 正常";
                    _list.Items[i].BackColor = Color.Yellow;
                    skipExb++;
                }
            }

            Log("==== 批量格式转换开始：" + _folder + "，共 " + files.Count + " 个文件" +
                (skipExb > 0 ? "（exb 跳过 " + skipExb + "，ICS 仍处理）" : "") + " ====");
            Log("输出目录：" + (string.IsNullOrEmpty(_outFolder) ? "与源文件同文件夹" : _outFolder));

            // 「请更新3D文件位置」改点「否」（链接保持陈旧，不让打开期弹文件框打断批量）；
            // 旧的值结束后恢复。这是全局标志，只在批次内置位（口径对齐 V1.1 流程的用法）。
            bool oldDecline = PopupAutoYes.DeclineUpdate3dLocation;
            PopupAutoYes.DeclineUpdate3dLocation = true;

            int ok = 0, fail = 0, skip = skipExb;
            var touchedSrc = new List<string>();   // 本批处理过的源文件（供收尾 .bak 清理）
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (_stopRequested) { MarkRemaining(i, "已取消"); break; }
                    string f = files[i];
                    // ★ 已预检标记跳过的 exb（CRX 未就绪）——直接 continue，不再等 3 秒
                    if (_list.Items[i].SubItems[3].Text == "跳过(CRX)") { skip++; continue; }
                    touchedSrc.Add(f);
                    _lblStatus.Text = "处理中 (" + (i + 1) + "/" + files.Count + ") " + Path.GetFileName(f);
                    bool fOk;
                    try
                    {
                        fOk = f.EndsWith(".exb", StringComparison.OrdinalIgnoreCase)
                            ? RunExbOnWorker(f, _list.Items[i])
                            : ProcessIcs(f, _list.Items[i]);
                    }
                    catch (Exception ex)
                    {
                        Log("文件异常：" + Path.GetFileName(f) + " → " + ex.GetType().Name + ": " + ex.Message);
                        SetRow(_list.Items[i], "异常", "");
                        SetInfo(_list.Items[i], ex.Message);
                        SetRowColor(_list.Items[i], RowRed);
                        fOk = false;
                    }
                    if (fOk) ok++; else fail++;
                    if (_stopRequested) { MarkRemaining(i + 1, "已取消"); break; }
                }
            }
            finally
            {
                PopupAutoYes.DeclineUpdate3dLocation = oldDecline;
                CleanupBakFiles(touchedSrc);   // 用户要求：输出文件夹里的 .bak 备份文件统一删除
                _pdfPropsConfigured = false;   // 下一次批次重新配属性面板（驱动可能已被重启 CAXA 重置）
                _running = false;
                _btnPick.Enabled = true;
                _btnStop.Enabled = false;
                _btnStart.Enabled = _folder != null && AnyChecked();
                string summary = (_stopRequested ? "已停止。" : "全部处理完。") +
                    "成功 " + ok + " 个，失败 " + fail + " 个" +
                    (skip > 0 ? "，跳过 " + skip + " 个（exb 需图纸环境）" : "") + "。";
                _lblStatus.Text = summary;
                Log("==== 批量格式转换结束：" + summary + " ====");
                // 用户要求：批次结束不再弹窗，结果看状态栏与列表即可。
            }
        }

        // ── ListView 行辅助：列 0=序号 1=文件 2=操作 3=状态 4=耗时 5=信息
        //    （可能从后台工作线程调用——一律经 UiPost 封送到 UI 线程）──

        private void SetRow(ListViewItem row, string state, string cost)
        {
            UiPost(delegate
            {
                row.SubItems[3].Text = state;
                if (!string.IsNullOrEmpty(cost)) row.SubItems[4].Text = cost;
                try { row.EnsureVisible(); } catch { }
            });
        }

        private void SetInfo(ListViewItem row, string info)
        {
            UiPost(delegate { row.SubItems[5].Text = info; });
        }

        /// <summary>整行标色（UseItemStyleForSubItems 默认 true，BackColor 会覆盖所有子项）。</summary>
        private void SetRowColor(ListViewItem row, Color back)
        {
            UiPost(delegate { row.BackColor = back; });
        }

        private void MarkRemaining(int fromIndex, string state)
        {
            for (int i = fromIndex; i < _list.Items.Count; i++)
                if (_list.Items[i].SubItems[3].Text == "等待")
                    _list.Items[i].SubItems[3].Text = state;
        }

        // ── 后台桥任务线程（2026-11-11 14:2x 定案，口径同「另存为dwg」RunBridgeTask）──

        /// <summary>
        /// UI 线程安全投递：后台工作线程里对窗体/列表的所有改动经此封送。
        /// BeginInvoke 异步不阻塞工作线程（模态框开着时同步 Invoke 会把工作线程卡死）；
        /// 封送的消息在 UI 线程的 DoEvents 泵里被处理。
        /// </summary>
        private void UiPost(Action a)
        {
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
            else a();
        }

        /// <summary>
        /// exb 单文件处理跑**后台线程**。★ 为什么必须后台：批量原先整条链跑在 CAXA 主线程
        /// （本插件 UI 线程）上，另存/转换弹窗的模态循环会把 DoEvents 冻死在本线程里
        /// （13:33/13:53 实机：另存框等待循环被冻结 26~40 秒、醒来即误判超时）。
        /// 后台线程只做跨进程消息与文件桥等待；CAXA 主线程保持自由泵消息——CRX 的 WM_TIMER、
        /// 另存框模态循环都正常运转，代点轮询永不被冻结（与单按钮 9.6s 成功条件完全同构）。
        /// UI 线程在本方法里泵 DoEvents 等工作线程结束（界面/停止按钮保持响应，
        /// UiPost 的界面封送也在这段泵里被处理）。ics 仍走 UI 线程（COM STA）。
        /// </summary>
        private bool RunExbOnWorker(string path, ListViewItem row)
        {
            bool ok = false;
            Exception err = null;
            var worker = new Thread(delegate()
            {
                try { ok = ProcessExb(path, row); }
                catch (Exception ex) { err = ex; }
            });
            worker.IsBackground = true;
            worker.Name = "BatchExbFlow";
            worker.Start();
            while (worker.IsAlive)
            {
                try { Application.DoEvents(); } catch { }
                Thread.Sleep(60);
            }
            if (err != null) throw err;
            return ok;
        }

        // ================================================================ 单文件：exb → dxf/dwg/pdf

        /// <summary>
        /// 单个 exb（**后台线程**执行，RunExbOnWorker；口径 = 「另存为dwg」成熟方案 v1 产线通道）：
        /// CRX 图纸侧打开（OPEN_EXB）→ 弹窗消化（不更新图纸）→ 保存落盘（WM_COMMAND，硬判据 mtime）→
        /// pdf 走桥 CONVERT_DXF → dxf/dwg 走另存文件框代点（消息+MSAA 双保险选类型）→
        /// CLOSEDOC（保存并关闭，前台 exb 关闭为基准）。
        /// ★★ 全线只走 CRX 桥 + 跨进程安全消息：caxa3d(Scene) 侧的 IZBaseApp.OpenFile2 对业务 exb
        ///    是退役路线（内嵌 CRxView3D 是 AV 触发面）。
        /// ★★ 为什么后台线程：另存/转换弹窗的模态循环会冻死 UI 线程上的 DoEvents 等待循环
        ///    （13:33/13:53 实机冻结 26~40 秒、醒来即误判超时）；后台线程 + 主线程自由 =
        ///    单按钮 9.6s 成功的同构条件。桥「转换」通道本机判死（GetIDispatch 恒空，14:22 实机）。
        /// 返回 = 本文件勾选的全部格式是否都导出成功。
        /// </summary>
        private bool ProcessExb(string path, ListViewItem row)
        {
            var sw = Stopwatch.StartNew();
            string fileName = Path.GetFileName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string info = "";
            bool openedByUs = false;   // 这张图是本次流程开出来的（收尾才允许关，绝不动用户自己开的图）

            try
            {
                // ── 0) 已打开的文件不做（转换前必须先更新，而打开态更新会弹框，交给用户手工处理）──
                SetRow(row, "检查", "");
                if (FindDrawingWindow(fileName) != IntPtr.Zero)
                {
                    SetInfo(row, "该 exb 已在图纸环境打开：请先关闭它再重跑（打开态无法静默更新）");
                    SetRow(row, "跳过", sw.Elapsed.ToString(@"mm\:ss"));
                    SetRowColor(row, RowYellow);
                    Log("跳过（已打开）：" + fileName);
                    return false;
                }

                // ── 1) 冷态等图纸环境/CRX 就绪（后台不可用，这里在 UI 线程用泵消息等）──
                //      exb 全流程只走 CRX 桥，绝不退回 Scene 侧 API
                SetRow(row, "等图纸环境", "");
                // ★ CRX 快速判死：IsCrxLoaded() 是 marker 文件毫秒级检查，图纸环境未开时 marker 根本不存在，
                // 45 秒傻等毫无意义（用户误以为卡死点停止，循环 break → ICS 也没机会跑）。
                // 3 秒足够 CRX 初始化（图纸环境刚开后 marker 几秒内写入）；超时即跳过本 exb，继续 ICS。
                if (!WaitUntil("等 CRX 就绪", 3000, delegate { return ExbBridge.IsCrxLoaded(); }, 300))
                {
                    SetInfo(row, "CRX 3 秒内未就绪：请先进入图纸环境（点主界面「图纸环境预热」）再跑 exb；ics 导出不依赖 CRX，本批次仍会处理");
                    SetRow(row, "跳过(CRX)", sw.Elapsed.ToString(@"mm\:ss"));
                    SetRowColor(row, RowYellow);
                    Log(fileName + "：CRX 未就绪，跳过（exb 打开只能在图纸进程内完成）");
                    return false;
                }
                ExbBridge.ResetSession();   // 图纸模块初始化时 CRX 可能重载过 → 重新握手

                // 本流程跑在后台桥任务线程（RunExbOnWorker，口径同「另存为dwg」RunBridgeTask）：
                // 等待一律 Thread.Sleep + 轮询副作用，绝不泵消息（泵是 CAXA 主线程的事——它自由时
                // CRX 的 WM_TIMER、另存框模态循环都正常转）；UI 改动一律走 UiPost 封送。
                ExbBridge.PumpDuringWait = null;
                SaveAsDwgUi.PumpDuringWait = null;
                try
                {
                    // 借 PopupAutoYes 代点（口径同「另存为dwg」成熟方案 v1 按钮）：
                    // 打开期「更新3d图形」→是、尺寸检查→取消。归还点 = 导出结束；
                    // 关图纸期绝不带租约（外层 finally 的 AnswerNoOnSaveDialog「否」会与代点打架）。
                    using (PopupAutoYes.Borrow("批量格式转换·代点" + fileName))
                    {
                        // ── 2) 图纸侧打开 exb（CRX OPEN_EXB：在册即激活，不在册 appContextOpenDocument）──
                        SetRow(row, "打开", "");
                        BridgeResponse op = ExbBridge.RequestOpenExb(path);
                        if (op == null || op.Status != "OK")
                        {
                            SetInfo(row, "图纸侧打开失败：" + (op == null ? "无回包" : op.Status + " " + op.Message));
                            SetRow(row, "打开失败", sw.Elapsed.ToString(@"mm\:ss"));
                            SetRowColor(row, RowRed);
                            Log(fileName + "：OPEN_EXB 失败 → " + (op == null ? "null" : op.Status + " " + op.Message));
                            return false;
                        }
                        Log(fileName + "：图纸侧已打开（" + op.Message + "）");
                        openedByUs = true;

                        // ── 3) 成熟方案（v1 产线通道，11:58 实测 9.6s）：
                        //       等打开期弹窗消化 → C# 直发 WM_COMMAND(ID_FILE_SAVE) 保存落盘，
                        //       硬判据 = exb mtime 变化（保存 OK = 本文件可以继续后续的同步信号）──
                        SetRow(row, "保存落盘", "");
                        SaveAsDwgUi.WaitPopupsSettled(30000, delegate(string m) { Log(fileName + "：" + m); });
                        Log(fileName + "：打开期弹窗已消化（模态计数=0）");

                        IntPtr mdi = SaveAsDwgUi.FindExbMdiChild(path);
                        if (mdi == IntPtr.Zero)
                        {
                            SetInfo(row, "找不到标题含本 exb 的图纸 MDI 子窗口（保存/另存无处可投）");
                            SetRow(row, "定位失败", sw.Elapsed.ToString(@"mm\:ss"));
                            SetRowColor(row, RowRed);
                            Log(fileName + "：FindExbMdiChild 未命中");
                            return false;
                        }
                        if (!SaveAsDwgUi.SaveViaCommand(mdi, path, 20000,
                                delegate(string m) { Log(fileName + "：" + m); }))
                        {
                            SetInfo(row, "保存未落盘（20 秒内 exb mtime 无变化）——按成熟方案口径中止");
                            SetRow(row, "保存失败", sw.Elapsed.ToString(@"mm\:ss"));
                            SetRowColor(row, RowRed);
                            return false;
                        }

                        // ── 4) 导出（2026-11-11 第三轮）：
                        //   pdf：打印对话框代点法（抄单按钮「打印pdf」成熟方案：CX_print → 图幅/打印机/纸张代点 → 保存框 → 轮询产物）。
                        //   dxf/dwg：另存文件框代点（成熟方案 v1，与「另存为dwg」按钮 9.6s 链路完全同构，
                        //   后台线程 + CAXA 主线程自由）。
                        //   ★ pdf 必须先于对话框导出（另存会把文档关联名切走，打印命令按活动文档找）。
                        //   ★ 不走 CRX 桥 CONVERT_DXF（本机 14:22 实机判死：GetIDispatch 恒空，假成功）。
                        //   判成功 = PDF 产物存在且 >0 且大小稳定；dxf/dwg = 另存产物存在且 >0 且框已关闭。
                        // 版本号：UI 下拉 "R2007" → "2007"（Substring(1)）
                        string dwgVer = (_cmbVersion.SelectedItem ?? "R2007").ToString();
                        string verDigits = dwgVer.Length > 1 ? dwgVer.Substring(1) : dwgVer;
                        SetRow(row, "导出中", "");
                        var pieces = new List<string>();
                        bool allOk = true;
                        if (_chkPdf.Checked)
                        {
                            if (_stopRequested) { pieces.Add("pdf 停止"); allOk = false; }
                            else
                            {
                                string pdfOut = Path.Combine(OutDirFor(path), stem + ".pdf");
                                bool needProps = !_pdfPropsConfigured;
                                bool r = ExportPdfViaPrint(path, pdfOut, mdi, needProps,
                                    delegate(string m) { Log(fileName + "：" + m); });
                                if (r && needProps) { _pdfPropsConfigured = true; Log("pdf属性面板已配置一次，后续不再进"); }
                                pieces.Add("pdf" + (r ? "√" : "×"));
                                allOk &= r;
                            }
                        }
                        if (_chkDxf.Checked)
                        {
                            if (_stopRequested) { pieces.Add("dxf 停止"); allOk = false; }
                            else
                            {
                                bool r = ExportViaDialog(mdi, path, stem, "dxf", verDigits);
                                pieces.Add("dxf" + (r ? "√" : "×"));
                                allOk &= r;
                            }
                        }
                        if (_chkDwg.Checked)
                        {
                            if (_stopRequested) { pieces.Add("dwg 停止"); allOk = false; }
                            else
                            {
                                bool r = ExportViaDialog(mdi, path, stem, "dwg", verDigits);
                                pieces.Add("dwg" + (r ? "√" : "×"));
                                allOk &= r;
                            }
                        }
                        info = string.Join(" ", pieces.ToArray());

                        SetRow(row, allOk ? "完成" : "部分失败", sw.Elapsed.ToString(@"mm\:ss"));
                        SetInfo(row, info);
                        SetRowColor(row, allOk ? RowGreen : RowRed);
                        Log(fileName + "：" + (allOk ? "导出完成" : "部分失败") + "（" + info + "）");
                        return allOk;
                    }
                }
                finally
                {
                    ExbBridge.PumpDuringWait = null;
                    SaveAsDwgUi.PumpDuringWait = null;
                }
            }
            finally
            {
                // ── 5) 收尾关图纸：CLOSEDOC = 保存 + WM_CLOSE（更新结果落盘，不留脏图）。
                //      关不掉（仍不空闲/被模态框挡住）再退回 WM_CLOSE+代点「否」，绝不强关。
                if (openedByUs)
                {
                    // 5.0) 收尾前静默（2026-11-12 -07管 闪退实证，caxa3dBatchMaster 同款加固）：
                    //      另存是模态命令，「产物落盘」≠「命令退栈」。C# 产物判据已过时，迟弹出的
                    //      另存框可能还开着，此时 CLOSEDOC 的同步保存命令插进未退干净的模态栈
                    //      → IRONCAD 原生闪退（事件日志/WER 全空）。等到「无另存框且无待消化模态，
                    //      持续 2 秒」或 30s 超时才许进关图。
                    try { SettleBeforeClose(fileName); }
                    catch (Exception ex) { Log(fileName + "：收尾静默异常：" + ex.Message); }

                    try
                    {
                        ExbBridge.PumpDuringWait = PumpBridge;
                        BridgeResponse rc = ExbBridge.RequestCloseDoc(fileName);
                        if (rc == null || rc.Status != "OK")
                            Log(fileName + "：CLOSEDOC 未成功（" +
                                (rc == null ? "无回包" : rc.Status + " " + rc.Message) + "）→ 退回 WM_CLOSE 不保存");
                        else
                            Log(fileName + "：已保存并关闭（" + rc.Message + "）");
                    }
                    catch (Exception ex) { Log("关闭图纸异常：" + ex.Message); }
                    finally { ExbBridge.PumpDuringWait = null; }

                    try { CloseExbDrawing(fileName); } catch (Exception ex) { Log("关闭图纸异常：" + ex.Message); }

                    // ── 6) 兜底清障（2026-11-11 实机：跑完后仍有非预期模态框堵着 ⇒ 整个 CAXA 卡死）：
                    //       点「取消/否」关掉，绝不点「是/确定/保存」。
                    try { SaveAsDwgUi.DismissLingeringModals(delegate(string m) { Log(m); }); }
                    catch (Exception ex) { Log("清障异常：" + ex.Message); }
                }
            }
        }

        /// <summary>关掉我们打开的图纸：WM_CLOSE → 有保存框代点「否」（不保存）→ 等窗口消失。
        /// 必须在 PopupAutoYes 租约归还之后调用，否则「是/否」会打架。</summary>
        /// <summary>
        /// 收尾前静默（2026-11-12 -07管 闪退对策，caxa3dBatchMaster 同款）：等「无另存框且无待消化
        /// 模态」持续 2 秒（或 30s 超时）才允许进 CLOSEDOC。期间若发现本流程迟到的另存框
        /// （C# 已按产物判据放弃、框才弹出来的），点「取消」跳过该格式——框是本流程开的，取消零副作用。
        /// </summary>
        private void SettleBeforeClose(string fileName)
        {
            var sw = Stopwatch.StartNew();
            int quiet = 0;
            while (sw.ElapsedMilliseconds < 30000)
            {
                if (SaveAsDwgUi.CancelLingeringSaveDialog(m => Log(fileName + "：" + m)))
                {
                    quiet = 0;                       // 刚处理过迟到的另存框，重新计静默
                    Thread.Sleep(600);
                    continue;
                }
                if (PopupAutoYes.ModalCount > 0)
                {
                    quiet = 0;
                    Thread.Sleep(400);
                    continue;
                }
                quiet += 400;
                if (quiet >= 2000) return;           // 连续 2 秒干净 → 模态栈已退净
                Thread.Sleep(400);
            }
            Log(fileName + "：收尾前静默等待 30s 超时（仍有弹窗），按现状进关图（风险自担）");
        }

        private void CloseExbDrawing(string fileName)
        {
            IntPtr h = FindDrawingWindow(fileName);
            if (h == IntPtr.Zero) return;
            DrawingProbe.RequestClose(h);
            bool gone = WaitUntil("等图纸关闭", CloseTimeoutMs, delegate
            {
                bool alive = DrawingProbe.IsAlive(h) || FindDrawingWindow(fileName) != IntPtr.Zero;
                if (alive)
                {
                    // 口径同预热载体图纸：不保存。exb 的更新已由 UpdateDraftViews 写盘，
                    // 关闭期不保存 = 转换器对源文件零副作用。
                    string handled = DrawingProbe.AnswerNoOnSaveDialog();
                    if (handled != null) Log(handled);
                }
                return !alive;
            }, 250);
            Log(fileName + "：" + (gone ? "已不保存关闭" : "15 秒内没关掉（可能被别的模态框挡住），请手工关闭"));
        }

        /// <summary>
        /// 经 CRX 桥导出一次图纸。主通道＝图纸环境**专用转换器**
        /// `IZTDExportControl::Export(bstrFileName, eZExportDrawingType)`（Draft 自己的 dxf/dwg/pdf 导出），
        /// 失败再退回数据层 saveAs（仅 dxf/dwg）。判成功一律以输出文件存在且 >0 为准。
        /// </summary>
        private bool ExportViaBridge(string srcPath, string stem, string ext, int ver, string label)
        {
            string outPath = Path.Combine(OutDirFor(srcPath), stem + ext);
            DateTime since = DateTime.Now;
            bool existedBefore = false;
            try { existedBefore = File.Exists(outPath); } catch { }

            int exportType = ExportTypeOf(ext, ver);
            Log("导出 " + label + " → " + outPath +
                "（TDExportControl type=" + exportType + "，走 CRX 桥）");

            BridgeResponse r = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (_stopRequested) return false;
                r = ExbBridge.RequestConvertDxf(srcPath, outPath, exportType);
                if (r != null && r.Status == "OK") break;
                Log(label + "：专用转换器第 " + attempt + "/3 次失败：" +
                    (r == null ? "回包 null" : r.Status + " " + r.Message));
                PumpMessages(600);
            }

            // 回退：数据层 saveAs（pdf 没有 saveAs 通道）
            if ((r == null || r.Status != "OK") && !ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                Log(label + "：退回数据层 saveAs（dwgVer=" + ver + "）");
                for (int attempt = 1; attempt <= 8; attempt++)
                {
                    r = ExbBridge.RequestExportDwgDxf(srcPath, outPath, ver);
                    if (r != null && r.Status == "OK") break;
                    bool busy = r != null && r.Status == "ERR" && (r.Message ?? "").StartsWith("BUSY");
                    bool nocrx = r != null && r.Status == "NOCRX";
                    if (!busy && !nocrx) break;
                    if (_stopRequested) return false;
                    Log(label + "：图纸" + (nocrx ? "桥未就绪" : "不空闲") +
                        "（第 " + attempt + "/8 次），泵 " + (nocrx ? 2000 : 1000) + "ms 后重试");
                    PumpMessages(nocrx ? 2000 : 1000);
                }
            }

            if (r == null || r.Status != "OK")
            {
                Log(label + "：CRX 导出失败：" + (r == null ? "回包 null" : r.Status + " " + r.Message));
                return false;
            }
            Log(label + "：CRX 导出回包 OK（" + r.Message + "）");

            string outp = VerifyWithRetry(Path.GetDirectoryName(outPath),
                Path.GetFileNameWithoutExtension(outPath), ext, since, existedBefore);
            if (outp == null)
            {
                Log(label + "：saveAs 报 OK 但输出文件不存在（判失败）");
                return false;
            }
            try { Log("导出 " + label + " 完成：" + outp + "（" + new FileInfo(outp).Length + " B）"); }
            catch { }
            return true;
        }

        /// <summary>
        /// exb→pdf 打印对话框代点法（2026-11-11 第三轮，抄单按钮「打印pdf」成熟方案 v2）。
        /// 跑在后台线程（RunExbOnWorker），只做跨进程消息 + 文件轮询，不泵消息（泵是 CAXA 主线程的事）。
        /// 前置条件：图纸已打开、已保存落盘、mdi 子窗口句柄已拿到、PopupAutoYes 租约已借。
        /// 判成功 = PDF 产物存在且 >0 且大小稳定（不认命令回包）。
        /// ★ 不走 CRX 桥 CONVERT_DXF（本机 14:22 实机判死：GetIDispatch 恒空，假成功）。
        /// </summary>
        private bool ExportPdfViaPrint(string exbPath, string outPath, IntPtr mdi, bool configureProps, Action<string> log)
        {
            string fileName = Path.GetFileName(exbPath);
            // ① 清旧产物（PDF 保存框覆盖提示会卡住；旧文件先记账再删）
            try
            {
                if (File.Exists(outPath))
                {
                    var old = new FileInfo(outPath);
                    log("删除旧产物 " + outPath + "（" + old.Length + " 字节）");
                    old.Delete();
                }
            }
            catch (Exception ex) { log("删除旧产物失败（忽略）：" + ex.Message); }

            // ② 前置图纸窗口（确保键盘兜底 Ctrl+P 能打到正确的目标）
            IntPtr frame = FindDrawingWindow(fileName);
            if (frame != IntPtr.Zero)
            {
                try { SetForegroundWindow(frame); Thread.Sleep(200); }
                catch { }
            }

            // ③ 派发打印命令（CRX 白名单 CX_ 前缀；Commands.xml 实证 Identify=CX_print）
            BridgeResponse pr = ExbBridge.RequestExecNative("CX_print");
            if (pr == null || pr.Status != "OK")
            {
                log("CX_print 未投出：" + (pr == null ? "无回包" : pr.Status + " " + pr.Message));
                return false;
            }
            log("CX_print 已投出（" + (pr.Message ?? "") + "）");

            // ④ 等打印对话框；投递无反应时 Ctrl+P 兜底
            IntPtr dlg = PrintPdfUi.WaitPrintDialog(30000, log);
            if (dlg == IntPtr.Zero)
            {
                PrintPdfUi.TriggerPrintByShortcut(frame, log);
                dlg = PrintPdfUi.WaitPrintDialog(15000, log);
            }
            if (dlg == IntPtr.Zero)
            {
                log("打印对话框未能打开（CX_print / Ctrl+P 两路均未见「打印对话框」）");
                return false;
            }

            // ⑤ 对话框参数代点：图幅 → PDF 打印机 → 纸张大小 → 黑白 → 横向 → 标准图形 → 点「打印」
            string sheet = "";
            bool configured = PrintPdfUi.ConfigureAndPrint(dlg, out sheet, log, configureProps);
            if (!configured)
            {
                log("打印对话框代点失败（图幅=" + (sheet.Length == 0 ? "未识别" : sheet) + "；对话框留在原地）");
                return false;
            }

            // ⑥ PDF 保存框：填同名 .pdf → 点「保存」
            IntPtr save = PrintPdfUi.WaitPdfSaveDialog(30000, dlg, log);
            if (save != IntPtr.Zero)
            {
                if (!PrintPdfUi.FillSaveDialog(save, outPath, log))
                {
                    log("PDF 保存框代点失败（对话框留在原地）");
                    return false;
                }
            }
            else
            {
                log("未出现 PDF 保存框，按驱动直接落盘继续等产物");
            }

            // ⑦ 轮询 PDF 产物（驱动异步写盘，要求大小连续稳定 1.2s）
            bool produced = PrintPdfUi.WaitPdfProduct(outPath, 60000, log);

            // ⑧ 收尾：轮询打印对话框退场（模态框里动文档是踩坑 31 「关图闪退」的同一触发面）
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000 && PrintPdfUi.IsVisibleWindow(dlg))
                Thread.Sleep(300);
            if (PrintPdfUi.IsVisibleWindow(dlg))
                log("打印对话框 15 秒仍未退场，按已提交继续（关图前请留意）");

            if (produced)
            {
                try { log("PDF 已产出：" + outPath + "（" + new FileInfo(outPath).Length + " 字节，图幅=" + sheet + "）"); }
                catch { }
                return true;
            }
            log("PDF 未产出（图幅=" + (sheet.Length == 0 ? "未识别" : sheet) + "）");
            return false;
        }

        /// <summary>桥等待期的消息泵（挂在 ExbBridge.PumpDuringWait 上；UI 线程纪律）。</summary>
        private void PumpBridge()
        {
            try { Application.DoEvents(); } catch { }
        }

        /// <summary>
        /// 对话框代点导出一次（成熟方案 v1「另存为dwg/dxf」产线通道，2026-11-11 14:2x 恢复）。
        /// 预删输出与错误产物标记 → PostMessage ID_FILE_SAVE_AS 开「另存文件」框（键盘兜底）→
        /// FillAndSave（消息通道 + MSAA 双保险选「2004+ext」类型）→ WaitProductWithRetry（3 次重试，
        /// 判成功 = 产物存在 且 另存框已关闭）。★ 本方法必须跑在后台线程（RunExbOnWorker）：
        /// 泵钩子为 null，等待轮询不被模态循环冻结；CAXA 主线程自由处理另存框。
        /// ★ 另存会把文档关联名切到输出文件，mdi 子窗口句柄不变仍可继续投递（可连续多格式导出）。
        /// </summary>
        private bool ExportViaDialog(IntPtr mdi, string srcPath, string stem, string ext, string dwgVersion)
        {
            string outPath = Path.Combine(OutDirFor(srcPath), stem + "." + ext);
            try
            {
                if (File.Exists(outPath)) File.Delete(outPath);
                if (File.Exists(outPath + ".exb")) File.Delete(outPath + ".exb");
            }
            catch { }
            Log("导出 " + ext + " → " + outPath + "（另存文件框代点，版本=" + dwgVersion + "）");

            IntPtr frame = FindDrawingWindow(Path.GetFileName(srcPath));
            IntPtr dlg = SaveAsDwgUi.OpenSaveAsDialog(mdi, frame, 20000,
                delegate(string m) { Log(m); });
            if (dlg == IntPtr.Zero)
            {
                Log(ext + "：另存文件框未能打开（ID_FILE_SAVE_AS / Ctrl+Shift+S 两路均未见对话框）");
                return false;
            }
            if (!SaveAsDwgUi.FillAndSave(dlg, outPath, ext, delegate(string m) { Log(m); }, dwgVersion))
            {
                Log(ext + "：另存文件框代点失败（对话框留在原地，详见取证日志）");
                return false;
            }
            bool ok = SaveAsDwgUi.WaitProductWithRetry(mdi, outPath, ext, 30000, 3,
                delegate(string m) { Log(m); });
            if (ok)
            {
                try { Log("导出 " + ext + " 完成：" + outPath + "（" + new FileInfo(outPath).Length + " B）"); }
                catch { }
            }
            else
            {
                Log(ext + "：未产出（3 次尝试）：保存类型提交/覆盖提示/校验弹窗问题，详见取证日志");
            }
            return ok;
        }

        /// <summary>核对输出文件：预期名命中（新写或本轮前不存在）→ 直接返回；
        /// 没命中则扫目录里 since 之后新生成、主名前缀相同的文件，扩展名不符就改名兜底。</summary>
        private static string VerifyOutput(string dir, string stem, string ext, DateTime since, bool existedBefore)
        {
            string expected = Path.Combine(dir ?? "", stem + ext);
            try
            {
                var fi = new FileInfo(expected);
                if (fi.Exists && fi.Length > 0 && (!existedBefore || fi.LastWriteTime >= since.AddMilliseconds(-200)))
                    return expected;
            }
            catch { }

            // 扫新文件兜底：主名前缀 + since 之后写盘
            try
            {
                string best = null;
                DateTime bestT = DateTime.MinValue;
                foreach (string f in Directory.GetFiles(dir ?? ""))
                {
                    var fi = new FileInfo(f);
                    if (!fi.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                    if (fi.LastWriteTime < since) continue;
                    if (best == null || fi.LastWriteTime > bestT) { best = f; bestT = fi.LastWriteTime; }
                }
                if (best != null)
                {
                    if (!string.Equals(Path.GetExtension(best), ext, StringComparison.OrdinalIgnoreCase))
                    {
                        string renamed = expected;
                        for (int i = 0; i < 5; i++)
                        {
                            try
                            {
                                if (File.Exists(renamed)) File.Delete(renamed);
                                File.Move(best, renamed);
                                return renamed;
                            }
                            catch { Thread.Sleep(300); }   // 宿主可能还握着句柄，稍候重试
                        }
                        return best;   // 改名失败也认（文件确实生成了，只是名字带宿主自己的扩展名）
                    }
                    return best;
                }
            }
            catch { }
            return null;
        }

        /// <summary>DWG/DXF 版本下拉框 → CRxDb::CRxDbDwgVersion 整型值（crxdb.h 实测：
        /// R13=19/R14=21/R2000=23/R2004=25/R2007=27/R2010=29/R2013=31）。</summary>
        private static int DraftVerCode(string comboText)
        {
            switch (comboText)
            {
                case "R13": return 19;    // kDHL_1012
                case "R14": return 21;    // kDHL_1014
                case "R2000": return 23;  // kDHL_1015
                case "R2004": return 25;  // kDHL_1800
                case "R2010": return 29;  // kDHL_1024
                case "R2013": return 31;  // kDHL_1027
                default: return 27;       // R2007（默认，kDHL_1021）
            }
        }

        /// <summary>DWG/DXF 版本码（CRxDb::CRxDbDwgVersion）→ eZExportDrawingType 整型值。
        /// 实测来源 _probe-tools\out\tlb\2027\enums.csv:683-712（专用转换器 Export 的第 2 形参）：
        ///   R13 DWG=5/DXF=6、R14 7/8、R2000 17/18、R2004 20/21、R2007 22/23、
        ///   R2010 24/25、R2013 26/27、R2018 28/29、PDF=19。
        /// ★ 两个枚举域的数字不通用（saveAs 的 29=R2010，Export 的 29=DXF_R2018），只能用版本码本身做键。</summary>
        private static int ExportTypeOf(string ext, int ver)
        {
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return ExbBridge.Z_TD_EXPORT_PDF;
            bool dwg = ext.Equals(".dwg", StringComparison.OrdinalIgnoreCase);
            switch (ver)
            {
                case 19: return dwg ? 5 : 6;      // R13
                case 21: return dwg ? 7 : 8;      // R14
                case 23: return dwg ? 17 : 18;    // R2000
                case 25: return dwg ? 20 : 21;    // R2004
                case 29: return dwg ? 24 : 25;    // R2010
                case 31: return dwg ? 26 : 27;    // R2013
                default: return dwg ? 22 : 23;    // R2007（默认）
            }
        }

        // ================================================================ 单文件：ics → x_t/stp/stl

        /// <summary>
        /// 单个 ics：打开（已打开则复用，且不关）→ GetTopElement 取**场景第一个对象**（不穿透）→
        /// 逐勾选项 Export → 我们开的就 CloseFile。判成功只认输出文件存在且 &gt;0。
        /// </summary>
        private bool ProcessIcs(string path, ListViewItem row)
        {
            var sw = Stopwatch.StartNew();
            string fileName = Path.GetFileName(path);
            string stem = Path.GetFileNameWithoutExtension(path);

            try
            {
                // ── 1) 打开（已打开就复用：绝不为关一个本来就在的文档动用户的环境）──
                // ★ "已打开"探测用**批量开始前的 baseline 快照**（不是当前 GetAllOpenDocs）。
                //   修 bug a：exb 装配体 OpenFile2 会把子零件 ics 加载进内存，子零件在当前
                //   GetAllOpenDocs 里出现但不该留在宿主里——它们不在 baseline 里，所以 wasOpenBefore=false，
                //   收尾必须 CloseFile。baseline 口径 = "批量开始前宿主里有的"。
                SetRow(row, "打开", "");
                IZDoc doc = FindOpenDoc(path, fileName, stem);
                bool wasOpenBefore = _baselineOpenNames.Contains(path);   // ★ 快照口径！
                if (doc != null && !wasOpenBefore)
                    Log(fileName + "：在 GetAllOpenDocs 里命中但不在 baseline 里（批量期间装配体加载的子零件）→ 仍需关闭");
                else if (doc != null && wasOpenBefore)
                    Log(fileName + "：本来就已打开（baseline 命中 Name=" + SafeName(doc) + "），复用且结束时不动它");
                if (doc == null)
                {
                    using (PopupAutoYes.Borrow("批量格式转换·打开" + fileName))
                    {
                        bool ao;
                        try { doc = _app.OpenFile2(path, false, true, out ao); }
                        catch (Exception ex) { Log("OpenFile2 异常：" + ex.Message); doc = null; }
                        if (doc == null) doc = FindOpenDoc(path, fileName, stem);   // 开了但没返回对象：枚举里找
                        if (doc == null)
                        {
                            PumpMessages(800);   // 打开是异步铺开的，给点时间再找一次
                            doc = FindOpenDoc(path, fileName, stem);
                        }
                        if (doc == null) doc = TryGetOpenDoc(path, fileName, stem);   // 最后兜底（此时若它静默开图，算我们开的）
                    }
                    if (doc == null)
                    {
                        DumpOpenDocNames(fileName);
                        SetInfo(row, "OpenFile2 后拿不到文档对象（返回值 null / GetAllOpenDocs 未命中）");
                        SetRow(row, "打开失败", sw.Elapsed.ToString(@"mm\:ss"));
                        SetRowColor(row, RowRed);
                        return false;
                    }
                    Log(fileName + "：已拿到文档对象（doc.Name=" + SafeName(doc) + "）");
                }

                IZSceneDoc scene = doc as IZSceneDoc;
                if (scene == null)
                {
                    SetInfo(row, "文档对象不是 IZSceneDoc（可能没打开成功），详见日志");
                    SetRow(row, "打开失败", sw.Elapsed.ToString(@"mm\:ss"));
                    SetRowColor(row, RowRed);
                    return false;
                }

                // ── 2) 场景第一个对象（顶层元素；不穿透）──
                SetRow(row, "导出中", "");
                IZElement top = null;
                try { top = scene.GetTopElement(); } catch (Exception ex) { Log("GetTopElement 异常：" + ex.Message); }
                if (top == null)
                {
                    SetInfo(row, "场景里没有对象（GetTopElement = null）");
                    SetRow(row, "导出失败", sw.Elapsed.ToString(@"mm\:ss"));
                    SetRowColor(row, RowRed);
                    return false;
                }
                string topName = "";
                int topType = -1;
                try { topName = top.Name ?? ""; } catch { }
                try { topType = (int)top.Type; } catch { }
                Log(fileName + "：第一个对象「" + topName + "」Type=" + topType + "（不穿透，只导这一个）");

                string baseNoExt = Path.Combine(OutDirFor(path), stem);
                var pieces = new List<string>();
                bool allOk = true;
                using (PopupAutoYes.Borrow("批量格式转换·导出" + fileName))
                {
                    if (_chkXt.Checked)
                    {
                        bool r = ExportModel(top, baseNoExt, ".x_t", ModelTypeXT, "x_t");
                        pieces.Add("x_t" + (r ? "√" : "×")); allOk &= r;
                    }
                    if (_chkStp.Checked)
                    {
                        bool r = ExportModel(top, baseNoExt, ".stp", ModelTypeStep, "stp");
                        pieces.Add("stp" + (r ? "√" : "×")); allOk &= r;
                    }
                    if (_chkSat.Checked)
                    {
                        bool r = ExportModel(top, baseNoExt, ".sat", ModelTypeSat, "sat");
                        pieces.Add("sat" + (r ? "√" : "×")); allOk &= r;
                    }
                }

                // ── 3) 收尾：只有"本来没开、是我们打开的"才关（CloseFile 不走保存链）──
                // 实机 11-12 坑：外链子零件 ics（总装 OpenFile2 时被宿主连带加载）CloseFile 抛
                // E_FAIL，退 doc.Close() 是假成功（不报错但文档留在宿主 → 界面留一张没关的 ics）。
                // 真正关得掉外链 ics 的是 IZDoc::Close2(true)（槽 23，DesignEnvTool.CloseDocByPtr
                // 实测定案，参考 caxa3ddiffrename）。关闭链逐级加码，判成功只看权威副作用
                // （回查 GetAllOpenDocs 已无此文档），不看任何返回值。
                if (!wasOpenBefore)
                {
                    try { _app.CloseFile(doc); Log(fileName + "：CloseFile 已投（待核验）"); }
                    catch (Exception ex) { Log("CloseFile 异常（外链 ics 常见），转 Close2：" + ex.Message); }

                    PumpMessages(400);
                    bool closed = (FindOpenDoc(path, fileName, stem) == null);
                    if (!closed)
                    {
                        Log(fileName + "：CloseFile 后回查仍在宿主 → Close2(-1)（槽 23）");
                        DesignEnvTool.CloseDocByPtr(doc, Log, "bc");
                        PumpMessages(400);
                        closed = (FindOpenDoc(path, fileName, stem) == null);
                    }
                    if (!closed)
                    {
                        Log(fileName + "：Close2 后回查仍在宿主 → 最后兜底 doc.Close()");
                        try { doc.Close(); } catch (Exception ex2) { Log("doc.Close 也异常：" + ex2.Message); }
                        PumpMessages(400);
                        closed = (FindOpenDoc(path, fileName, stem) == null);
                    }
                    Log(fileName + "：收尾关闭" + (closed
                        ? "已核验成功（宿主里已无此文档）"
                        : "未生效，文档仍留在宿主（请手动关闭）"));
                }

                string info = string.Join(" ", pieces.ToArray());
                SetRow(row, allOk ? "完成" : "部分失败", sw.Elapsed.ToString(@"mm\:ss"));
                SetInfo(row, info);
                SetRowColor(row, allOk ? RowGreen : RowRed);
                Log(fileName + "：" + (allOk ? "导出完成" : "部分失败") + "（" + info + "）");
                return allOk;
            }
            finally
            {
                // 清理在方法体内做完了（COM 对象不能跨 finally 状态糊涂账），这里无需动作
            }
        }

        /// <summary>模型导出一次（第一个对象，不穿透）：IZAssembly 优先判（装配体走槽13），
        /// 零件走 IZPart::Export（槽15）；两个都不是 ⇒ 明确失败，绝不猜。
        /// （STL 通道已按用户要求剔除：实机两轮均静默无输出，宿主 STL 翻译器不走数据层。）</summary>
        private bool ExportModel(IZElement top, string baseNoExt, string ext, int modelType, string label)
        {
            var swT = Stopwatch.StartNew();
            DateTime since = DateTime.Now;
            bool existedBefore = false;
            try { existedBefore = File.Exists(baseNoExt + ext); } catch { }

            try
            {
                Log("导出 " + label + " → " + baseNoExt + ext);
                if (!CallModelExport(top, baseNoExt, modelType, label)) return false;
            }
            catch (Exception ex)
            {
                Log("Export(" + label + ") 抛异常：" + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            string outp = VerifyWithRetry(Path.GetDirectoryName(baseNoExt), Path.GetFileName(baseNoExt), ext, since, existedBefore);
            if (outp == null)
            {
                Log(label + "：导出后没找到输出文件（判失败）");
                return false;
            }
            try { Log("导出 " + label + " 完成：" + outp + "（" + new FileInfo(outp).Length + " B，" + swT.ElapsedMilliseconds + " ms）"); }
            catch { }
            return true;
        }

        /// <summary>实际发起模型导出调用。返回 false = 对象类型不支持（既非装配体也非零件）。</summary>
        private bool CallModelExport(IZElement top, string name, int modelType, string label)
        {
            var asm = top as IZAssembly;
            if (asm != null) { asm.Export(name, (eZModelType)modelType); PumpMessages(400); return true; }
            var part = top as IZPart;
            if (part != null) { part.Export(name, (eZModelType)modelType); PumpMessages(400); return true; }
            int t = -1;
            try { t = (int)top.Type; } catch { }
            Log("第一个对象既不是装配体也不是零件（Type=" + t + "），无法导出 " + label);
            return false;
        }

        /// <summary>VerifyOutput 带重试：宿主写盘可能慢于调用返回，扫 3 轮（每轮间隔泵 500ms）。</summary>
        private string VerifyWithRetry(string dir, string stem, string ext, DateTime since, bool existedBefore)
        {
            for (int i = 0; i < 3; i++)
            {
                string outp = VerifyOutput(dir, stem, ext, since, existedBefore);
                if (outp != null) return outp;
                PumpMessages(500);
            }
            return null;
        }

        // ================================================================ 文档对象获取 / 窗口取证

        /// <summary>宿主当前打开文档快照（GetAllOpenDocs，TLB 槽 45）。
        /// ★ GetOpenDocByName 有 get-or-open 语义（2026-11-10 实机实锤：从未打开的文件它也"命中"
        /// 且导出内容正确 = 它在背后静默开图），绝不能当"是否已打开"探针；探测一律用本枚举。</summary>
        private IZDoc[] OpenDocsSnapshot()
        {
            var list = new List<IZDoc>();
            try
            {
                object o = _app.GetAllOpenDocs();
                if (o is System.Collections.IEnumerable en && !(o is string))
                {
                    foreach (object x in en)
                    {
                        var d = x as IZDoc;
                        if (d != null) list.Add(d);
                    }
                }
            }
            catch (Exception ex) { Log("GetAllOpenDocs 异常：" + ex.Message); }
            return list.ToArray();
        }

        /// <summary>文档名是否命中目标文件：全路径相等 → 以「\文件名」结尾 → 文件名（带扩展名）相等
        /// → 主名（去扩展名）相等（图纸文档可能按无扩展名标题登记，实机待验，枚举名单会进日志）。</summary>
        private static bool DocMatches(IZDoc d, string fullPath, string fileName, string stem)
        {
            string n;
            try { n = d.Name ?? ""; } catch { return false; }
            if (n.Length == 0) return false;
            if (string.Equals(n, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
            if (n.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                if (string.Equals(Path.GetFileName(n), fileName, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(Path.GetFileNameWithoutExtension(n), stem, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        /// <summary>在当前打开文档里按名找目标文档；找不到返回 null。</summary>
        private IZDoc FindOpenDoc(string fullPath, string fileName, string stem)
        {
            foreach (IZDoc d in OpenDocsSnapshot())
                if (DocMatches(d, fullPath, fileName, stem)) return d;
            return null;
        }

        /// <summary>诊断：把宿主当前打开的文档名清单写进日志（找文档失败时看图纸/场景到底按什么名字登记）。</summary>
        private void DumpOpenDocNames(string context)
        {
            IZDoc[] docs = OpenDocsSnapshot();
            Log("打开文档清单（" + docs.Length + " 个；找 \"" + context + "\" 未命中）：");
            for (int i = 0; i < docs.Length && i < 15; i++)
                Log("  [" + i + "] " + SafeName(docs[i]));
        }

        /// <summary>GetOpenDocByName 依次试：全路径 → 文件名 → 主名（仅作枚举找不到时的最后兜底；
        /// 注意它可能静默开图，凡走到这里拿到的文档一律视为"我们开的"，收尾要关）。</summary>
        private IZDoc TryGetOpenDoc(string fullPath, string fileName, string stem)
        {
            string[] cands = { fullPath, fileName, stem };
            foreach (string c in cands)
            {
                if (string.IsNullOrEmpty(c)) continue;
                try
                {
                    IZDoc d = _app.GetOpenDocByName(c);
                    if (d != null)
                    {
                        Log("GetOpenDocByName(" + c + ") 命中，Name=" + SafeName(d));
                        return d;
                    }
                }
                catch (Exception ex) { Log("GetOpenDocByName(" + c + ") 异常：" + ex.Message); }
            }
            return null;
        }

        private static string SafeName(IZDoc d)
        {
            try { return d == null ? "(null)" : (d.Name ?? "(空)"); }
            catch (Exception ex) { return "(读Name失败:" + ex.Message + ")"; }
        }

        /// <summary>
        /// 找图纸文档窗口。规则 20 口径：**全名 OR 文件名 stem**，两者都命中才算 ——
        /// CAXA「保存更改→是」后会改写 MDI 标题（去 .exb / 加路径前缀），只按全名子串匹配必失配。
        /// stem 命中要求边界（stem 后跟「.」或标题以 stem 结尾），避免"壳体"误命中"壳体2"。
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
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static string WindowTextOf(IntPtr h)
        {
            try
            {
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        // ================================================================ 等待原语

        /// <summary>条件轮询（期间泵消息，绝不堵 CAXA 主线程）；「停止」立即中断返回 false。</summary>
        private bool WaitUntil(string what, int timeoutMs, Func<bool> condition, int pollMs)
        {
            var sw = Stopwatch.StartNew();
            while (!_stopRequested && sw.ElapsedMilliseconds < timeoutMs)
            {
                try { Application.DoEvents(); } catch { }
                bool ok = false;
                try { ok = condition(); } catch { }
                if (ok) return true;
                Thread.Sleep(pollMs);
            }
            if (!_stopRequested) Log(what + "：超时 " + timeoutMs + "ms");
            return false;
        }

        private static void PumpMessages(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                try { Application.DoEvents(); } catch { }
                Thread.Sleep(30);
            }
        }
    }
}
