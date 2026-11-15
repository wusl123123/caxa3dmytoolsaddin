// 「工程图附加二维码」弹窗（2026-11-15 用户指令：分类打包右侧新增按钮，参考 caxa3dqrcode 功能）。
//   点「确定」后对表格「列表识别的行」（D 列零件名非空的数据行）逐行：
//     ① 按属性模板从表头同名列取值组合（与「分类打包」同口径，H 列起，需先「属性批量读取」）；
//     ② QRCoder 制作二维码 PNG；
//     ③ 打开该行同名 exb（C 列目录 + D/F 列零件名）→ 比例自动识别补偿 → 定位规则算插入点
//        → 宿主「插入图片」命令落图 → 保存 →（勾选时）关闭图纸。
//   明细一律进本窗日志 + CSV 清单（「二维码清单-时间戳.csv」，落第一张图纸所在目录）。
// UI 铁律：宋体 9pt、SystemColors.Control、不启用视觉主题、纯汉字按钮、Fixed3D 边框、
//          固定锚 Top|Left、长流程「停止」按钮（停止时不强行保存、不关图）。
// 构造函数不碰 app（探索经验 48：可反射 ctor.Invoke 离屏拉起做界面断言）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    // internal：CaxaBatchService 是 internal，public 构造函数会把它带进签名报 CS0051（同程序集内用不到 public）
    internal sealed class PopupQrCode : Form
    {
        private readonly DataGridView _grid;
        private readonly CaxaBatchService _svc;
        private readonly Action<string> _log;
        private readonly TextBox _tpl = new TextBox();
        private readonly TextBox _size = new TextBox();
        private readonly CheckBox _chkClose = new CheckBox();
        private readonly TextBox _txtLog = new TextBox();
        private readonly Button _ok = new Button();
        private readonly Button _stop = new Button();
        private readonly Button _top = new Button();
        private readonly Button _cancel = new Button();

        private volatile bool _stopRequested;
        private bool _running;

        public PopupQrCode(DataGridView grid, CaxaBatchService svc, Action<string> log)
        {
            _grid = grid;
            _svc = svc;
            _log = log;
            BuildUi();
            if (!IsHandleCreated) CreateHandle();
        }

        private void BuildUi()
        {
            Text = "工程图附加二维码";
            Font = new Font("宋体", 9F);
            BackColor = SystemColors.Control;
            ForeColor = SystemColors.ControlText;
            ClientSize = new Size(780, 440);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            FormClosing += delegate { _stopRequested = true; };

            var grpProp = new GroupBox();
            grpProp.SetBounds(12, 10, 756, 92);
            grpProp.Text = "二维码设置";
            Controls.Add(grpProp);

            var lblTpl = new Label();
            lblTpl.SetBounds(12, 26, 66, 20);
            lblTpl.Text = "属性模板:";
            grpProp.Controls.Add(lblTpl);

            _tpl.SetBounds(84, 24, 470, 22);
            _tpl.Text = "SC图号_SC名称_SC规格_SC材料";
            _tpl.BorderStyle = BorderStyle.Fixed3D;
            grpProp.Controls.Add(_tpl);

            var lblSize = new Label();
            lblSize.SetBounds(566, 26, 78, 20);
            lblSize.Text = "二维码大小:";
            grpProp.Controls.Add(lblSize);

            _size.SetBounds(648, 24, 96, 22);
            _size.Text = "20x20";
            _size.BorderStyle = BorderStyle.Fixed3D;
            grpProp.Controls.Add(_size);

            _chkClose.SetBounds(84, 56, 500, 22);
            _chkClose.Text = "每行落图保存成功后关闭该图纸（不勾＝图纸留在窗口里）";
            _chkClose.Checked = true;
            grpProp.Controls.Add(_chkClose);

            var grpLog = new GroupBox();
            grpLog.SetBounds(12, 110, 756, 286);
            grpLog.Text = "日志";
            Controls.Add(grpLog);

            _txtLog.SetBounds(10, 20, 736, 256);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BorderStyle = BorderStyle.Fixed3D;
            _txtLog.BackColor = Color.White;
            grpLog.Controls.Add(_txtLog);

            _ok.SetBounds(12, 404, 90, 28); _ok.Text = "确定"; _ok.Click += OnOk;
            _stop.SetBounds(117, 404, 90, 28); _stop.Text = "停止"; _stop.Enabled = false; _stop.Click += OnStop;
            _top.SetBounds(222, 404, 100, 28); _top.Text = "窗口前置"; _top.BackColor = Color.LightSkyBlue; _top.Click += OnTop;
            _cancel.SetBounds(337, 404, 90, 28); _cancel.Text = "取消"; _cancel.Click += delegate { Close(); };
            var rule = new Button();
            rule.SetBounds(442, 404, 100, 28); rule.Text = "定位规则"; rule.Click += OnRule;
            Controls.AddRange(new Control[] { _ok, _stop, _top, _cancel, rule });

            AddLog("就绪。点「确定」后对表格里零件名非空的行逐行执行：");
            AddLog("① 按属性模板从表头同名列（H 列起）取值组合二维码内容（需先点过「属性批量读取」）；");
            AddLog("② 制作二维码 → 打开该行同名 exb → 图纸比例自动识别补偿 → 定位规则算插入点 → 宿主「插入图片」落图 → 保存。");
            AddLog("★ 插入点/比例自动识别失败时兜底：比例 1:1、插入点 0,0（绝不瞎折算）；「定位规则」按钮可编辑规则文件。");
        }

        private void OnTop(object sender, EventArgs e)
        {
            TopMost = !TopMost;
            _top.BackColor = TopMost ? Color.LightSkyBlue : SystemColors.Control;
            AddLog(TopMost ? "窗口前置：已开启" : "窗口前置：已关闭");
        }

        private void OnStop(object sender, EventArgs e)
        {
            _stopRequested = true;
            AddLog("已请求停止，当前行收尾后退出（不会强行保存、不会关闭图纸）");
        }

        /// <summary>「定位规则」按钮：记事本打开规则文件，编辑保存后下一次运行自动生效。</summary>
        private void OnRule(object sender, EventArgs e)
        {
            string path = InsertRule.OpenForEdit(AddLog);
            Diagnostics.Log("qr-rule: open " + path);
        }

        // ============================================================ 行收集与主循环

        private sealed class QrJob
        {
            public int RowIndex;        // 表格行号（1 基，与 UI 一致）
            public string PartName = "";
            public string ExbPath = "";
            public string Payload = "";
            public string Note = "";
        }

        private int RowCount => _grid.AllowUserToAddRows ? _grid.Rows.Count - 1 : _grid.Rows.Count;
        private string Cell(int r, int c)
        {
            if (r < 0 || r >= _grid.Rows.Count || c < 0 || c >= _grid.ColumnCount) return "";
            return _grid.Rows[r].Cells[c].Value?.ToString() ?? "";
        }

        /// <summary>模板字段 → 列号：表头精确匹配优先，唯一包含匹配兜底（与分类打包 FindPropertyColumn 同口径）。</summary>
        private int FindColumn(string name)
        {
            for (int c = TableService.PropertyColumnStart; c < _grid.ColumnCount; c++)
                if (string.Equals(Cell(0, c).Trim(), name, StringComparison.OrdinalIgnoreCase)) return c;
            int hit = -1, count = 0;
            for (int c = TableService.PropertyColumnStart; c < _grid.ColumnCount; c++)
            {
                string h = Cell(0, c).Trim();
                if (h.Length == 0) continue;
                if (h.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                { hit = c; count++; }
            }
            return count == 1 ? hit : -1;
        }

        private List<QrJob> CollectJobs(string template)
        {
            var jobs = new List<QrJob>();
            string[] fields = (template ?? "").Split(new char[] { '_' }, StringSplitOptions.None);

            // 源目录兜底：C 列空时，用当前设计环境文档所在目录（与「分类打包」同口径）
            string fallbackDir = "";
            try
            {
                string docPath = _svc?.ActiveDocFilePath ?? "";
                if (!string.IsNullOrEmpty(docPath)) fallbackDir = Path.GetDirectoryName(docPath) ?? "";
            }
            catch { }

            for (int r = 1; r < RowCount; r++)
            {
                if (_grid.Rows[r].IsNewRow) continue;
                string partName = Cell(r, TableService.ColPartName).Trim();
                if (partName.Length == 0) continue;   // 「列表识别的行」＝零件名非空的数据行

                var job = new QrJob { RowIndex = r + 1, PartName = partName };

                // ① 属性组合：模板字段查表头同名列，值取本行（缺列按空串参与组合，全空判失败）
                var vals = new List<string>();
                int hitCnt = 0, missCnt = 0;
                var missing = new List<string>();
                foreach (string f in fields)
                {
                    string name = f.Trim();
                    if (name.Length == 0) { vals.Add(""); missCnt++; continue; }
                    int col = FindColumn(name);
                    if (col < 0) { vals.Add(""); missCnt++; missing.Add(name + "(无此列)"); continue; }
                    string v = Cell(r, col).Trim();
                    if (v.Length == 0) { vals.Add(""); missCnt++; missing.Add(name + "(空)"); continue; }
                    vals.Add(v); hitCnt++;
                }
                job.Payload = string.Join("_", vals.ToArray());
                job.Note = "属性命中 " + hitCnt + " / 缺失 " + missCnt +
                           (missing.Count > 0 ? "（" + string.Join("、", missing.ToArray()) + "）" : "");
                if (hitCnt == 0)
                {
                    job.ExbPath = "";   // 属性全空：不进主链
                    jobs.Add(job);
                    continue;
                }

                // ② 同名 exb：C 列目录 + D 列零件名；D 列不存在时按 F 列新零件名兜底（与分类打包同口径）
                string dir = Cell(r, TableService.ColFilePath).Trim();
                if (dir.Length == 0) dir = fallbackDir;
                if (dir.Length == 0 || !Directory.Exists(dir))
                {
                    job.Note += "；C 列目录不可用" + (dir.Length == 0 ? "（空）" : "：" + dir);
                    jobs.Add(job);
                    continue;
                }
                string exb = Path.Combine(dir, partName + ".exb");
                if (!File.Exists(exb))
                {
                    string newName = Cell(r, TableService.ColNewPartName).Trim();
                    if (newName.Length > 0 && !string.Equals(newName, partName, StringComparison.Ordinal))
                    {
                        string alt = Path.Combine(dir, newName + ".exb");
                        if (File.Exists(alt)) { exb = alt; job.Note += "；D 列无同名 exb，按 F 列「" + newName + "」兜底"; }
                    }
                }
                job.ExbPath = File.Exists(exb) ? exb : "";
                if (job.ExbPath.Length == 0) job.Note += "；同名 exb 不存在（" + exb + "）";
                jobs.Add(job);
            }
            return jobs;
        }

        private void OnOk(object sender, EventArgs e)
        {
            if (_running) { AddLog("已有流程在运行"); return; }
            string tpl = _tpl.Text.Trim();
            if (tpl.Length == 0) { AddLog("属性模板不能为空"); return; }
            Match m = Regex.Match(_size.Text.Trim(), @"^(\d+)\s*[*xX]\s*(\d+)$");
            if (!m.Success) { AddLog("二维码尺寸格式错误，示例 20x20"); return; }
            int w = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (w <= 0 || h <= 0) { AddLog("二维码尺寸必须大于 0"); return; }

            var jobs = CollectJobs(tpl);
            if (jobs.Count == 0) { AddLog("表格里没有零件名非空的数据行（先「从装配体导入」或核对 D 列），未执行。"); return; }

            // CSV 落点：第一张可定位图纸所在目录，兜底 %TEMP%（不落在只读的插件目录）
            string csvDir = "";
            foreach (QrJob j in jobs) if (j.ExbPath.Length > 0) { csvDir = Path.GetDirectoryName(j.ExbPath); break; }
            if (string.IsNullOrEmpty(csvDir) || !Directory.Exists(csvDir)) csvDir = Path.GetTempPath();
            string csvPath = Path.Combine(csvDir, "二维码清单-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".csv");

            _running = true; _stopRequested = false;
            _ok.Enabled = false; _stop.Enabled = true;
            int okCnt = 0, propFail = 0, qrFail = 0, exbFail = 0, stopCnt = 0;
            var csv = new List<string>();
            csv.Add("行号,零件名,二维码内容,图纸,结果,说明");
            try
            {
                AddLog("开始：模板=\"" + tpl + "\"，尺寸=" + w + "x" + h + " mm，识别行 " + jobs.Count +
                       " 行，插入后关图=" + (_chkClose.Checked ? "是" : "否") + "，清单=" + csvPath);
                for (int i = 0; i < jobs.Count; i++)
                {
                    if (_stopRequested) { stopCnt = jobs.Count - i; AddLog("已停止：剩余 " + stopCnt + " 行未处理。"); break; }
                    QrJob job = jobs[i];
                    AddLog("── 第 " + (i + 1) + "/" + jobs.Count + " 行（表格第 " + job.RowIndex + " 行）「" +
                           job.PartName + "」：" + job.Note);
                    string outcome;
                    if (job.Payload.Length == 0 || job.Payload.Trim('_').Length == 0)
                    {
                        propFail++;
                        outcome = "属性全空";
                        AddLog("属性组合全空（模板字段在表头/本行都没取到值），本行跳过。");
                    }
                    else if (job.ExbPath.Length == 0)
                    {
                        exbFail++;
                        outcome = "缺图纸";
                        AddLog("没有可用的同名 exb，本行跳过（未写图纸）。");
                    }
                    else
                    {
                        QrRunResult rr = QrBatchTool.Run(_svc != null ? _svc.App : null, job.ExbPath, job.Payload,
                            job.PartName, w, h, 1.0, "0,0", _chkClose.Checked, AddLog, delegate { return _stopRequested; });
                        if (rr.InsertVerified) { okCnt++; outcome = "成功"; }
                        else if (rr.PngPath.Length == 0) { qrFail++; outcome = "二维码失败"; }
                        else { exbFail++; outcome = "插图未verified"; }
                        AddLog("本行结果：" + rr.Summary);
                        Diagnostics.Log("qr-row: row=" + job.RowIndex + " part=" + job.PartName +
                            " | " + rr.Summary + " | payload=" + rr.Payload);
                    }
                    csv.Add(CsvLine(job.RowIndex.ToString(), job.PartName, job.Payload,
                                    job.ExbPath.Length > 0 ? job.ExbPath : "", outcome, job.Note));
                    try { Application.DoEvents(); } catch { }
                }
            }
            catch (Exception ex)
            {
                AddLog("异常：" + ex.GetType().Name + " " + ex.Message);
                Diagnostics.Log("qr-run EX " + ex);
            }
            finally
            {
                // CSV 一律落盘（权威证据），失败不掩盖已有结果
                try
                {
                    using (var sw = new StreamWriter(csvPath, false, new UTF8Encoding(true)))
                        foreach (string line in csv) sw.WriteLine(line);
                    AddLog("清单已写入：" + csvPath);
                }
                catch (Exception ex) { AddLog("清单写入失败：" + ex.Message); }

                AddLog("结束：成功 " + okCnt + "｜属性全空 " + propFail + "｜二维码失败 " + qrFail +
                       "｜图纸/插图问题 " + exbFail + (stopCnt > 0 ? "｜停止未处理 " + stopCnt : ""));
                try { _log?.Invoke("工程图附加二维码：成功 " + okCnt + " / 识别 " + jobs.Count + " 行｜清单 " + csvPath); }
                catch { }
                _running = false; _stopRequested = false;
                _ok.Enabled = true; _stop.Enabled = false;
                Application.DoEvents();
            }
        }

        public void AddLog(string message)
        {
            if (IsDisposed || _txtLog.IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(AddLog), message); return; }
            _txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + message + "\r\n");
            if (_txtLog.TextLength > 60000) _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 40000);
            Diagnostics.Log("qr-ui: " + message);
        }

        private static string CsvLine(params string[] fields)
        {
            var parts = new string[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                string v = fields[i] ?? "";
                if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                    v = "\"" + v.Replace("\"", "\"\"") + "\"";
                parts[i] = v;
            }
            return string.Join(",", parts);
        }
    }
}
