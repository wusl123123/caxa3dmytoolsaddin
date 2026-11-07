using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dAttributeManageraddin
{
 public partial class MainForm : Form
 {
 // CAXA 3D 会话
 private CaxaSession _app;
 private string _elemName; // 设计树第一个元素名（连接状态区显示）
 private string _docName; // 文档名（定位文件用）

 // Excel 数据
 private DataTable excelData;
 private string tableFilePath = "";
 private bool isLoadingData = false; // 防止加载时触发 CellValueChanged
 private Dictionary<string, int> savedColumnWidths; // 保存用户手动调整的列宽（会话内）
 private Dictionary<int, string> resolvedPropNames; // 列号 -> 模型中实际属性名（刷新时解析）

 private readonly IZBaseApp _siteApp;   // Add-in 站点提供的应用对象
 private readonly string _pluginDirectory; // 插件所在目录（由 Host 注入，读取随插件分发的模板）

 // 窗口前置（铁律2：默认前置，点击可切换）
 private bool _topMost = true;
 private Button _btnTopMost;

 // ↓↓↓ 键盘归属抢救：Ctrl+C/V/X/A/Z 归本窗口自己的文本框（机理/开关/取证见 Caxa3dMyTools.Contracts.ChordRescue）
 protected override void OnActivated(EventArgs e) { base.OnActivated(e); Caxa3dMyTools.Contracts.ChordRescue.Install(this, msg => Diagnostics.Trace(msg)); }
 protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口失活"); }
 protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); Caxa3dMyTools.Contracts.ChordRescue.Uninstall(this, "窗口关闭"); }
 protected override void WndProc(ref Message m) { if (Caxa3dMyTools.Contracts.ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
 // ↑↑↑

 public MainForm(IZBaseApp siteApp, string pluginDirectory)
 {
     _siteApp = siteApp;
     _pluginDirectory = pluginDirectory;
     InitializeComponent();

     // === 铁律2：加窗口前置按钮到主面板顶部 ===
     BuildTopMostButton();
     TopMost = _topMost;

     Diagnostics.UiLogSink = AddLog;
     Diagnostics.Log("MainForm ctor enter");

     AddLog("程序启动成功（窗口默认前置）");
     AutoLoadDefaultTable();

     // 在 Add-in 加载阶段（InitSelf 线程）就预创建窗口句柄，
     // 防止推迟到功能区 OnClick 时跨窗口消息与 CAXA 主线程互等死锁
     if (!IsHandleCreated) CreateHandle();
 }

 /// <summary>在 mainPanel 顶部加窗口前置按钮栏。</summary>
 private void BuildTopMostButton()
 {
     var topBar = new Panel
     {
         Dock = DockStyle.Top,
         Height = 40,
         Padding = new Padding(10, 6, 10, 6),
         BackColor = Color.FromArgb(240, 240, 240)
     };
     _btnTopMost = new Button
     {
         Text = "窗口前置",
         Location = new Point(10, 6),
         Size = new Size(100, 28),
         BackColor = Color.FromArgb(0, 120, 212),
         ForeColor = Color.White,
         FlatStyle = FlatStyle.Flat
     };
     _btnTopMost.FlatAppearance.BorderSize = 0;
     _btnTopMost.Click += (s, e) =>
     {
         _topMost = !_topMost;
         TopMost = _topMost;
         if (_topMost)
         {
             _btnTopMost.BackColor = Color.FromArgb(0, 120, 212);
             _btnTopMost.ForeColor = Color.White;
         }
         else
         {
             _btnTopMost.BackColor = SystemColors.Control;
             _btnTopMost.ForeColor = SystemColors.ControlText;
         }
     };
     topBar.Controls.Add(_btnTopMost);

     // 插到 mainPanel 最前面（Dock=Top 会自动排在最顶）
     this.mainPanel.Controls.Add(topBar);
 }


 protected override void OnHandleCreated(EventArgs e)
 {
     base.OnHandleCreated(e);
     Diagnostics.Log("MainForm.HandleCreated");
     TryConnectInProcess();
 }

 private void TryConnectInProcess()
 {
     try
     {
         AddLog("正在建立 CAXA 3D 进程内连接...");
         _app = CaxaAutomation.ConnectFromSite(_siteApp);
         CaxaAutomation.ProbeSessionInfo(_app, out _docName, out _elemName);
         AddLog("连接成功（进程内）");
         UpdateConnectionDisplay(true);
     }
     catch (Exception ex)
     {
         AddLog($"连接失败: {ex.Message}");
         Diagnostics.Log("ConnectFromSite failed: " + ex);
     }
 }
 
 #region ===== 日志 =====

 private void AddLog(string message)
 {
 if (txtLog.InvokeRequired)
 {
 txtLog.Invoke(new Action(() => AddLog(message)));
 return;
 }
 string timestamp = DateTime.Now.ToString("HH:mm:ss");
 txtLog.AppendText($"[{timestamp}] {message}\n");
 txtLog.ScrollToCaret();
 }

 private void btnSaveLog_Click(object sender, EventArgs e)
 {
 if (string.IsNullOrWhiteSpace(txtLog.Text))
 {
 AddLog(" 日志为空，无需保存");
 return;
 }
 using (var sfd = new SaveFileDialog())
 {
 sfd.FileName = "log.txt";
 sfd.Filter = "Text Files (*.txt)|*.txt";
 if (sfd.ShowDialog() == DialogResult.OK)
 {
 try
 {
 File.WriteAllText(sfd.FileName, txtLog.Text, Encoding.UTF8);
 AddLog($" 日志已保存到: {sfd.FileName}");
 }
 catch (Exception ex)
 {
 AddLog($" 保存日志失败: {ex.Message}");
 }
 }
 }
 }

 private void btnClearLog_Click(object sender, EventArgs e)
 {
 txtLog.Clear();
 AddLog(" 日志已清空");
 }

 #endregion

 #region ===== CAXA 连接 =====

 private void ConnectToCaxa()
 {
 try
 {
 DropSession();
 _app = CaxaAutomation.ConnectFromSite(_siteApp);
 CaxaAutomation.ProbeSessionInfo(_app, out _docName, out _elemName);

 UpdateConnectionDisplay(true);
 AddLog(" 已连接到 CAXA 3D");
 if (string.IsNullOrEmpty(_docName))
 {
 AddLog(" CAXA 3D 已启动，但没有活动文档");
 }
 else
 {
 AddLog($" 当前文件: {FileLabelText()}");
 if (string.IsNullOrEmpty(_elemName))
 AddLog(" 未选中零件/装配体，请在 CAXA 3D 中选中目标零部件");
 }

 if (excelData != null)
 RefreshPropertyValues();
 }
 catch (Exception ex)
 {
 Diagnostics.Log("connect failed: " + ex);
 DropSession();
 UpdateConnectionDisplay(false);
 AddLog($" 连接 CAXA 3D 失败: {ex.Message}");
 
 }
 }

 /// <summary>文件标签显示格式：文件名.ICS----零件名（缺省部分自动省略）。</summary>
 private string FileLabelText()
 {
 string doc = FormatDocName(_docName);
 if (string.IsNullOrEmpty(doc)) return _elemName ?? "无";
 if (string.IsNullOrEmpty(_elemName)) return doc;
 return doc + "----" + _elemName;
 }

 /// <summary>文档名若为完整路径则只取文件名。</summary>
 private static string FormatDocName(string docName)
 {
 if (string.IsNullOrEmpty(docName)) return null;
 try
 {
 if (docName.IndexOfAny(new[] { '\\', '/' }) >= 0)
 return Path.GetFileName(docName);
 }
 catch { }
 return docName;
 }

 private void UpdateConnectionDisplay(bool connected)
 {
 if (lblConnectionStatus.InvokeRequired)
 {
 lblConnectionStatus.Invoke(new Action(() => UpdateConnectionDisplay(connected)));
 return;
 }
 if (connected)
 {
 lblConnectionStatus.Text = "连接状态: 已连接";
 lblConnectionStatus.ForeColor = Color.FromArgb(16, 124, 16);
 SetFileLabel("文件: " + FileLabelText());
 btnLocateFile.Enabled = true;
 }
 else
 {
 lblConnectionStatus.Text = "连接状态: 未连接";
 lblConnectionStatus.ForeColor = Color.FromArgb(209, 52, 56);
 SetFileLabel("文件: 无");
 btnLocateFile.Enabled = false;
 }
 }

 /// <summary>设置文件名标签，并同步完整文本提示（标签过窄时右侧自动省略）。</summary>
 private void SetFileLabel(string text)
 {
 lblFileName.Text = text;
 toolTip1.SetToolTip(lblFileName, text);
 }

 private void DropSession()
 {
 if (_app == null) return;
 try { _app.Dispose(); } catch { }
 _app = null;
 _elemName = null;
 _docName = null;
 }

 private bool IsConnected()
 {
 return _app != null && CaxaAutomation.Ping(_app);
 }

 private void btnRefresh_Click(object sender, EventArgs e)
 {
 AddLog(" 正在刷新连接状态...");
 btnRefresh.Enabled = false;
 try { ConnectToCaxa(); }
 finally
 {
 btnRefresh.Enabled = true;
 // EnsureFrameVisible 已不再激活 CAXA 窗口，焦点始终留在脚本界面
 }
 }

 private void timerCheck_Tick(object sender, EventArgs e)
 {
 try
 {
 if (_app == null) return;

 if (!CaxaAutomation.Ping(_app))
 {
 DropSession();
 UpdateConnectionDisplay(false);
 AddLog(" CAXA 3D 已关闭或连接已失效");
 return;
 }

 // 检查活动文档 / 设计树第一个元素是否变化
 string curDoc, curElem;
 CaxaAutomation.ProbeSessionInfo(_app, out curDoc, out curElem);
 if (curDoc != _docName || curElem != _elemName)
 {
 _docName = curDoc;
 _elemName = curElem;
 if (curElem != null || curDoc != null)
 {
 UpdateConnectionDisplay(true);
 AddLog($" 文件已切换: {FileLabelText()}");
 if (excelData != null)
 RefreshPropertyValues();
 }
 else
 {
 SetFileLabel("文件: 无");
 AddLog(" CAXA 3D 没有活动文档");
 }
 }
 }
 catch (Exception ex)
 {
 AddLog($" 检查连接状态时出错: {ex.Message}");
 }
 }

 private void btnLocateFile_Click(object sender, EventArgs e)
 {
 if (!string.IsNullOrEmpty(_docName) && File.Exists(_docName))
 {
 try
 {
 Process.Start("explorer.exe", "/select,\"" + _docName + "\"");
 AddLog($" 已打开文件所在文件夹: {Path.GetDirectoryName(_docName)}");
 }
 catch (Exception ex)
 {
 AddLog($" 打开文件夹失败: {ex.Message}");
 }
 }
 else
 {
 AddLog(" 文件路径无效或文件不存在（请先「刷新自定义属性」获取文档路径）");
 }
 }

 #endregion

 #region ===== 表格加载 =====

 private string GetDefaultTablePath()
 {
 // 模板随插件分发，只能用 Host 注入的插件目录定位（AppDomain.BaseDirectory 是 CAXA bin）
 if (string.IsNullOrEmpty(_pluginDirectory)) return null;
 return Path.Combine(_pluginDirectory, "caxa3dManagermodle.xlsx");
 }

 private void AutoLoadDefaultTable()
 {
 string defaultPath = GetDefaultTablePath();
 if (!string.IsNullOrEmpty(defaultPath) && File.Exists(defaultPath))
 {
 AddLog(" 检测到默认表格文件，自动加载中...");
 LoadTable(defaultPath);
 }
 else
 {
 AddLog($" 默认表格文件不存在: {defaultPath ?? "(插件目录未知)"}");
 AddLog(" 请手动点击「选择表格」按钮加载表格文件");
 }
 }

 private void btnSelectTable_Click(object sender, EventArgs e)
 {
 using (var ofd = new OpenFileDialog())
 {
 string defaultPath = GetDefaultTablePath();
 if (!string.IsNullOrEmpty(defaultPath))
 ofd.InitialDirectory = Path.GetDirectoryName(defaultPath);
 ofd.Filter = "Excel文件 (*.xlsx)|*.xlsx|所有文件 (*.*)|*.*";
 if (ofd.ShowDialog() == DialogResult.OK)
 {
 LoadTable(ofd.FileName);
 }
 }
 }

 private void btnReloadTable_Click(object sender, EventArgs e)
 {
 if (string.IsNullOrEmpty(tableFilePath) || !File.Exists(tableFilePath))
 {
 AddLog(" 表格文件路径无效，无法刷新");
 return;
 }
 AddLog(" 正在刷新表格数据...");
 try
 {
 LoadTable(tableFilePath);
 AddLog(" 表格数据已刷新");
 }
 catch (Exception ex)
 {
 AddLog($" 刷新表格失败: {ex.Message}");
 }
 }

 private void LoadTable(string filePath)
 {
 try
 {
 AddLog($" 正在加载表格: {Path.GetFileName(filePath)}");
 if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
 {
 AddLog(" 仅支持 .xlsx 格式表格（.xls 请先另存为 .xlsx）");
 return;
 }

 List<List<string>> grid = XlsxLite.ReadFirstSheet(filePath);
 if (grid.Count == 0)
 {
 AddLog(" 表格为空");
 return;
 }

 int maxCols = 0;
 foreach (var r in grid) if (r.Count > maxCols) maxCols = r.Count;

 var dt = new DataTable();
 for (int c = 0; c < maxCols; c++)
 dt.Columns.Add($"Col_{c}", typeof(string));
 foreach (var row in grid)
 {
 var dr = dt.NewRow();
 for (int c = 0; c < maxCols; c++)
 dr[c] = c < row.Count ? row[c] : "";
 dt.Rows.Add(dr);
 }
 excelData = dt;

 tableFilePath = filePath;
 lblTableFile.Text = $"表格文件: {filePath}";
 toolTip1.SetToolTip(lblTableFile, filePath);
 btnSaveTable.Enabled = true;
 btnRefreshValues.Enabled = true;
 btnLocateTable.Enabled = true;
 btnReloadTable.Enabled = true;

 DisplayTableData();
 RefreshPropertyValues();
 AddLog(" 表格加载成功");
 }
 catch (Exception ex)
 {
 AddLog($" 加载表格失败: {ex.Message}");
 MessageBox.Show($"加载表格失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
 }
 }

 private void btnLocateTable_Click(object sender, EventArgs e)
 {
 if (!string.IsNullOrEmpty(tableFilePath) && File.Exists(tableFilePath))
 {
 try
 {
 Process.Start("explorer.exe", "/select,\"" + tableFilePath + "\"");
 AddLog($" 已打开表格文件所在文件夹: {Path.GetDirectoryName(tableFilePath)}");
 }
 catch (Exception ex)
 {
 AddLog($" 打开文件夹失败: {ex.Message}");
 }
 }
 else
 {
 AddLog(" 表格文件路径无效或文件不存在");
 }
 }

 #endregion

 #region ===== 表格显示 =====

 private void DisplayTableData()
 {
 if (excelData == null || excelData.Rows.Count == 0) return;

 // 刷新前：保存当前用户手动调整过的列宽（按列名）
 if (dgvTable.Columns.Count > 0)
 {
 if (savedColumnWidths == null)
 savedColumnWidths = new Dictionary<string, int>();
 for (int c = 0; c < dgvTable.Columns.Count; c++)
 {
 var col = dgvTable.Columns[c];
 savedColumnWidths[$"Col_{c}"] = col.Width;
 }
 }

 isLoadingData = true;
 dgvTable.Columns.Clear();
 dgvTable.Rows.Clear();

 int maxCols = excelData.Columns.Count;

 for (int c = 0; c < maxCols; c++)
 {
 var col = new DataGridViewTextBoxColumn();
 col.Name = $"Col_{c}";
 col.HeaderText = ColIdxToExcel(c);
 col.SortMode = DataGridViewColumnSortMode.NotSortable;
 col.MinimumWidth = 40;

 if (savedColumnWidths != null && savedColumnWidths.TryGetValue(col.Name, out int savedWidth))
 col.Width = Math.Max(savedWidth, 40);
 else
 col.Width = 150;

 dgvTable.Columns.Add(col);
 }

 // 行高：按字体自动计算
 using (var g = dgvTable.CreateGraphics())
 {
 var cellFont = dgvTable.DefaultCellStyle.Font ?? new Font("Microsoft YaHei", 10F);
 var measuredSize = g.MeasureString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", cellFont);
 int vertPadding = dgvTable.DefaultCellStyle.Padding.Top + dgvTable.DefaultCellStyle.Padding.Bottom;
 dgvTable.RowTemplate.Height = (int)Math.Ceiling(measuredSize.Height) + vertPadding + 4;
 }

 // 填充数据行
 for (int r = 0; r < excelData.Rows.Count; r++)
 {
 var row = new DataGridViewRow();
 row.HeaderCell.Value = (r + 1).ToString();

 for (int c = 0; c < maxCols; c++)
 {
 var cell = new DataGridViewTextBoxCell();
 cell.Value = excelData.Rows[r][c] as string ?? "";
 row.Cells.Add(cell);
 }

 dgvTable.Rows.Add(row);

 for (int c = 0; c < maxCols; c++)
 {
 var cell = (DataGridViewTextBoxCell)row.Cells[c];

 if (r == 0)
 {
 cell.Style.BackColor = Color.FromArgb(0, 120, 212);
 cell.Style.ForeColor = Color.White;
 cell.Style.Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold);
 }
 else if (r == 1)
 {
 cell.Style.BackColor = Color.FromArgb(255, 255, 0);
 cell.Style.ForeColor = Color.Black;
 cell.ReadOnly = true;
 }

 if (c == 0)
 {
 if (r == 1)
 cell.Style.BackColor = Color.FromArgb(255, 200, 0);
 else if (r > 1)
 cell.Style.BackColor = Color.FromArgb(255, 255, 200);
 }
 }
 }

 isLoadingData = false;
 AddLog($" 表格已加载: {excelData.Rows.Count}行 x {maxCols}列");
 }

 private string ColIdxToExcel(int colIdx)
 {
 string result = "";
 int n = colIdx;
 while (n >= 0)
 {
 result = (char)(n % 26 + 65) + result;
 n = n / 26 - 1;
 }
 return result;
 }

 private void dgvTable_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
 {
 // 列标题自定义绘制，确保样式一致
 if (e.RowIndex < 0 && e.ColumnIndex >= 0)
 {
 e.PaintBackground(e.CellBounds, true);
 using (var brush = new SolidBrush(Color.FromArgb(51, 51, 51)))
 using (var sf = new StringFormat() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
 {
 e.Graphics.DrawString(e.Value?.ToString(), new Font("Microsoft YaHei", 9F, FontStyle.Bold), brush, e.CellBounds, sf);
 }
 e.Handled = true;
 }
 }

 private void dgvTable_CellValueChanged(object sender, DataGridViewCellEventArgs e)
 {
 if (isLoadingData || excelData == null) return;
 // 第一行属性名称被编辑时刷新属性值
 if (e.RowIndex == 0 && e.ColumnIndex >= 0)
 {
 var cell = dgvTable.Rows[e.RowIndex].Cells[e.ColumnIndex];
 string newPropName = cell.Value?.ToString()?.Trim() ?? "";
 string cellRef = $"{ColIdxToExcel(e.ColumnIndex)}1";
 AddLog($" 属性名称已修改: [{cellRef}] → '{newPropName}'");

 SyncCellToDataTable(e.RowIndex, e.ColumnIndex);
 RefreshPropertyValues();
 }
 else if (e.RowIndex >= 0 && e.ColumnIndex >= 0)
 {
 SyncCellToDataTable(e.RowIndex, e.ColumnIndex);
 }
 }

 private void SyncCellToDataTable(int row, int col)
 {
 if (excelData == null) return;
 var cell = dgvTable.Rows[row].Cells[col];
 string val = cell.Value?.ToString() ?? "";
 while (excelData.Rows.Count <= row)
 excelData.Rows.Add(excelData.NewRow());
 if (excelData.Columns.Count > col)
 excelData.Rows[row][col] = val;
 }

 #endregion

 #region ===== 右键菜单 =====

 private void dgvTable_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
 {
 if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
 {
 // 右键在已选区域内则保留 Shift 多选，否则塌缩为单选（标准资源管理器行为）
 var clicked = dgvTable.Rows[e.RowIndex].Cells[e.ColumnIndex];
 if (!clicked.Selected)
 {
 dgvTable.ClearSelection();
 clicked.Selected = true;
 }
 ShowCellContextMenu(e.RowIndex, e.ColumnIndex);
 }
 }

 private void dgvTable_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
 {
 if (e.Button == MouseButtons.Right && e.ColumnIndex >= 0)
 {
 ShowColumnHeaderMenu(e.ColumnIndex);
 }
 }

 private void dgvTable_RowHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
 {
 if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
 {
 ShowRowHeaderMenu(e.RowIndex);
 }
 }

 private void ShowCellContextMenu(int row, int col)
 {
 cmsCell.Items.Clear();
 bool hasAction = false;

 // 1) 插入单个单元格（被右键的单元格）
 if (row >= 2 && col >= 1)
 {
 var propCell = dgvTable.Rows[0].Cells[col];
 var valCell = dgvTable.Rows[row].Cells[col];
 if (propCell.Value != null && !string.IsNullOrWhiteSpace(propCell.Value.ToString())
 && valCell.Value != null && !string.IsNullOrWhiteSpace(valCell.Value.ToString()))
 {
 string targetName = ResolveTargetPropName(col);
 if (!string.IsNullOrEmpty(targetName))
 {
 string value = valCell.Value.ToString();
 var item = new ToolStripMenuItem($" 插入单个单元格（属性 '{targetName}'）");
 item.Click += (s, e) => InsertPropertyValue(targetName, value);
 cmsCell.Items.Add(item);
 hasAction = true;
 }
 }
 }

 // 2) 插入选中的多个单元格（Shift 多选）
 var validCells = GetValidSelectedCells();
 if (validCells.Count > 1)
 {
 var item = new ToolStripMenuItem($" 插入选中的多个单元格（{validCells.Count}个）");
 item.Click += (s, e) => InsertSelectedValues();
 cmsCell.Items.Add(item);
 hasAction = true;
 }

 // 3) 插入整行
 if (row >= 2)
 {
 int r = row;
 int count = CountRowValidEntries(r);
 var item = new ToolStripMenuItem($" 插入整行（第{r + 1}行，{count}个属性）");
 item.Click += (s, e) => InsertRowValues(r);
 cmsCell.Items.Add(item);
 hasAction = true;
 }

 if (hasAction)
 cmsCell.Show(dgvTable, dgvTable.PointToClient(Cursor.Position));
 }

 /// <summary>当前选区内的有效单元格（第3行起、B列起、表头与值均非空，不含零件名列）。</summary>
 private List<DataGridViewCell> GetValidSelectedCells()
 {
 return dgvTable.SelectedCells.Cast<DataGridViewCell>()
 .Where(c => c.RowIndex >= 2 && c.ColumnIndex >= 1
 && c.Value != null && !string.IsNullOrWhiteSpace(c.Value.ToString())
 && dgvTable.Rows[0].Cells[c.ColumnIndex].Value != null
 && !string.IsNullOrWhiteSpace(dgvTable.Rows[0].Cells[c.ColumnIndex].Value?.ToString())
 && !IsPartNameHeader(dgvTable.Rows[0].Cells[c.ColumnIndex].Value.ToString()))
 .ToList();
 }

 /// <summary>统计某行可插入的有效属性个数（不含零件名列）。</summary>
 private int CountRowValidEntries(int row)
 {
 int n = 0;
 for (int c = 1; c < dgvTable.Columns.Count; c++)
 {
 var p = dgvTable.Rows[0].Cells[c].Value;
 var v = dgvTable.Rows[row].Cells[c].Value;
 if (p != null && !string.IsNullOrWhiteSpace(p.ToString())
 && !IsPartNameHeader(p.ToString())
 && v != null && !string.IsNullOrWhiteSpace(v.ToString())) n++;
 }
 return n;
 }

 private void ShowColumnHeaderMenu(int col)
 {
 cmsColumnHeader.Items.Clear();
 string colLetter = ColIdxToExcel(col);

 var insertLeft = new ToolStripMenuItem($" 在列 {colLetter} 左侧插入列");
 insertLeft.Click += (s, e) => InsertColumnAt(col);
 cmsColumnHeader.Items.Add(insertLeft);

 var insertRight = new ToolStripMenuItem($" 在列 {colLetter} 右侧插入列");
 insertRight.Click += (s, e) => InsertColumnAt(col + 1);
 cmsColumnHeader.Items.Add(insertRight);

 cmsColumnHeader.Items.Add(new ToolStripSeparator());

 var deleteCol = new ToolStripMenuItem($" 删除列 {colLetter}");
 deleteCol.Click += (s, e) => DeleteColumnAt(col);
 cmsColumnHeader.Items.Add(deleteCol);

 cmsColumnHeader.Show(dgvTable, dgvTable.PointToClient(Cursor.Position));
 }

 private void ShowRowHeaderMenu(int row)
 {
 cmsRowHeader.Items.Clear();
 int rowNum = row + 1;

 var insertAbove = new ToolStripMenuItem($" 在第 {rowNum} 行上方插入行");
 insertAbove.Click += (s, e) => InsertRowAt(row);
 cmsRowHeader.Items.Add(insertAbove);

 var insertBelow = new ToolStripMenuItem($" 在第 {rowNum} 行下方插入行");
 insertBelow.Click += (s, e) => InsertRowAt(row + 1);
 cmsRowHeader.Items.Add(insertBelow);

 cmsRowHeader.Items.Add(new ToolStripSeparator());

 var deleteRow = new ToolStripMenuItem($" 删除第 {rowNum} 行");
 deleteRow.Click += (s, e) => DeleteRowAt(row);
 cmsRowHeader.Items.Add(deleteRow);

 cmsRowHeader.Show(dgvTable, dgvTable.PointToClient(Cursor.Position));
 }

 #endregion

 #region ===== 插入/删除行列 =====

 private void InsertColumnAt(int colIdx)
 {
 // 在DataTable中插入列
 if (excelData != null)
 {
 var newCol = new DataColumn($"Col_{colIdx}", typeof(string));
 excelData.Columns.Add(newCol);
 if (colIdx < excelData.Columns.Count - 1)
 newCol.SetOrdinal(colIdx);
 foreach (DataRow row in excelData.Rows)
 if (row.IsNull(newCol))
 row[newCol] = "";
 }

 // 在DataGridView中插入列
 dgvTable.Columns.Insert(colIdx, new DataGridViewTextBoxColumn()
 {
 Name = $"Col_{colIdx}",
 HeaderText = ColIdxToExcel(colIdx),
 SortMode = DataGridViewColumnSortMode.NotSortable,
 MinimumWidth = 40,
 Width = 120
 });

 RefreshColumnHeaders();
 AddLog($" 已在列 {ColIdxToExcel(colIdx)} 位置插入新列（共 {dgvTable.Columns.Count} 列）");
 }

 private void InsertRowAt(int rowIdx)
 {
 // 在DataTable中插入行
 if (excelData != null)
 {
 var newRow = excelData.NewRow();
 if (rowIdx <= excelData.Rows.Count)
 excelData.Rows.InsertAt(newRow, rowIdx);
 else
 excelData.Rows.Add(newRow);
 }

 // 重新显示数据（保持一致性）
 isLoadingData = true;
 dgvTable.Rows.Insert(rowIdx, 1);
 for (int c = 0; c < dgvTable.Columns.Count; c++)
 {
 dgvTable.Rows[rowIdx].Cells[c].Value = "";
 if (c == 0)
 dgvTable.Rows[rowIdx].Cells[c].Style.BackColor = Color.FromArgb(255, 255, 200);
 }
 isLoadingData = false;

 RefreshRowHeaders();
 AddLog($" 已在第 {rowIdx + 1} 行位置插入新行（共 {dgvTable.Rows.Count} 行）");
 }

 private void DeleteColumnAt(int colIdx)
 {
 if (dgvTable.Columns.Count <= 1)
 {
 AddLog(" 至少需要保留1列，无法删除");
 return;
 }
 string colLetter = ColIdxToExcel(colIdx);

 if (excelData != null && colIdx < excelData.Columns.Count)
 excelData.Columns.RemoveAt(colIdx);

 dgvTable.Columns.RemoveAt(colIdx);

 RefreshColumnHeaders();
 AddLog($" 已删除列 {colLetter}（共 {dgvTable.Columns.Count} 列）");
 }

 private void DeleteRowAt(int rowIdx)
 {
 if (dgvTable.Rows.Count <= 1)
 {
 AddLog(" 至少需要保留1行，无法删除");
 return;
 }
 int rowNum = rowIdx + 1;

 if (excelData != null && rowIdx < excelData.Rows.Count)
 excelData.Rows.RemoveAt(rowIdx);

 dgvTable.Rows.RemoveAt(rowIdx);

 RefreshRowHeaders();
 AddLog($" 已删除第 {rowNum} 行（共 {dgvTable.Rows.Count} 行）");
 }

 private void RefreshColumnHeaders()
 {
 for (int c = 0; c < dgvTable.Columns.Count; c++)
 dgvTable.Columns[c].HeaderText = ColIdxToExcel(c);
 }

 private void RefreshRowHeaders()
 {
 for (int r = 0; r < dgvTable.Rows.Count; r++)
 dgvTable.Rows[r].HeaderCell.Value = (r + 1).ToString();
 }

 #endregion

 #region ===== 属性值刷新 =====

 private void btnRefreshValues_Click(object sender, EventArgs e)
 {
 RefreshPropertyValues();
 }

 private void RefreshPropertyValues()
 {
 if (!IsConnected())
 {
 if (_app != null) { DropSession(); UpdateConnectionDisplay(false); }
 AddLog(" 未连接到 CAXA 3D，无法刷新属性值");
 return;
 }
 if (excelData == null)
 {
 AddLog(" 未加载表格，无法刷新属性值");
 return;
 }

 try
 {
 AddLog(" 正在刷新属性值...");

 // 收集属性名称（第1行，从B列即index=1开始）
 var propertyNames = new Dictionary<int, string>(); // colIndex -> 表头属性名
 for (int c = 1; c < excelData.Columns.Count; c++)
 {
 if (excelData.Rows.Count > 0)
 {
 var cellVal = excelData.Rows[0][c];
 if (cellVal != null && !string.IsNullOrWhiteSpace(cellVal.ToString()))
 propertyNames[c] = cellVal.ToString().Trim();
 }
 }

 if (propertyNames.Count == 0)
 {
 AddLog(" 表格第一行没有属性名称（请从B1开始填写）");
 return;
 }

 // 候选查询名：严格按表头原名，不再自动拼 SC 前缀
 // （之前 candidates 自动加 SC 前缀会导致表头 "图号" 偷偷回退查 "SC图号"）
 var candidates = new List<string>(propertyNames.Values);

 ElementCustomProps info = CaxaAutomation.ReadFirstElementCustomProps(_app, candidates);
 _docName = info.DocName;

 if (!info.HasElement)
 {
 AddLog(" 未在 CAXA 3D 中选中零件/装配体，请先选中目标零部件后再刷新");
 return;
 }

 resolvedPropNames = new Dictionary<int, string>();
 int updatedCount = 0;
 foreach (var kv in propertyNames)
 {
 int colIdx = kv.Key;
 string header = kv.Value;

 // 解析实际属性名：严格按表头原名，不再自动拼 SC 前缀 fallback
 string resolved = header;
 string evaluatedValue;
 if (IsPartNameHeader(header))
 {
 evaluatedValue = info.ElementName ?? "";
 resolved = null;
 }
 else
 {
 evaluatedValue = GetFieldValue(info, header);
 }
 resolvedPropNames[colIdx] = resolved;
 if (evaluatedValue == null) evaluatedValue = "";

 // 更新DataGridView（第二行即rowIndex=1）
 if (dgvTable.Rows.Count > 1 && colIdx < dgvTable.Columns.Count)
 {
 isLoadingData = true;
 dgvTable.Rows[1].Cells[colIdx].Value = evaluatedValue;
 isLoadingData = false;
 }

 // 更新DataTable
 while (excelData.Rows.Count <= 1)
 excelData.Rows.Add(excelData.NewRow());
 excelData.Rows[1][colIdx] = evaluatedValue;

 if (!string.IsNullOrEmpty(evaluatedValue))
 updatedCount++;
 }

 AddLog($" 属性值刷新完成（{updatedCount}/{propertyNames.Count} 个属性有值）");
 }
 catch (Exception ex)
 {
 Diagnostics.Log("refresh values failed: " + ex);
 if (!IsConnected()) { DropSession(); UpdateConnectionDisplay(false); }
 AddLog($" 刷新属性值失败: {ex.Message}");
 }
 }

 private static string GetFieldValue(ElementCustomProps info, string name)
 {
 FieldResult fr;
 if (info.Fields.TryGetValue(name, out fr)) return fr.Value;
 return null;
 }

 /// <summary>零件名列表头（该列填充选中零部件名，不作为自定义属性查询/写回）。</summary>
 private static bool IsPartNameHeader(string header)
 {
 if (string.IsNullOrWhiteSpace(header)) return false;
 string h = header.Trim();
 return h == "零件名" || h == "零件名称";
 }

 /// <summary>解析某列写入模型时使用的属性名。
 /// 严格按表头原名，不再自动拼 SC 前缀（旧 fallback 会把 "图号" 偷偷改成 "SC图号" 写回）。
 /// 返回 null 表示该列不可写入（零件名列）。</summary>
 private string ResolveTargetPropName(int colIdx)
 {
 string header = dgvTable.Rows[0].Cells[colIdx].Value?.ToString()?.Trim() ?? "";
 if (IsPartNameHeader(header)) return null;
 if (resolvedPropNames != null && resolvedPropNames.TryGetValue(colIdx, out string resolved)
 && !string.IsNullOrEmpty(resolved))
 return resolved;
 return header;
 }

 #endregion

 #region ===== 属性值插入 =====

 private bool EnsureConnectedForWrite()
 {
 if (!IsConnected())
 {
 if (_app != null) { DropSession(); UpdateConnectionDisplay(false); }
 AddLog(" 未连接到 CAXA 3D");
 MessageBox.Show("未连接到 CAXA 3D（请先点击「刷新连接」）", "警告",
 MessageBoxButtons.OK, MessageBoxIcon.Warning);
 return false;
 }
 return true;
 }

 private void InsertPropertyValue(string propertyName, string value)
 {
 if (!EnsureConnectedForWrite()) return;

 try
 {
 string strValue = value ?? "";
 string strPropName = propertyName.Trim();

 if (string.IsNullOrEmpty(strPropName))
 {
 AddLog(" 属性名称为空");
 return;
 }

 AddLog($" 正在插入属性值: {strPropName} = {strValue}");

 var values = new Dictionary<string, string> { { strPropName, strValue } };
 CaxaAutomation.WriteFirstElementCustomProps(_app, values);
 AddLog($" 属性值插入成功: {strPropName} = {strValue}");

 RefreshPropertyValues();
 }
 catch (Exception ex)
 {
 Diagnostics.Log("insert value failed: " + ex);
 AddLog($" 插入属性值失败: {ex.Message}");
 MessageBox.Show($"属性值插入失败: {propertyName}\n{ex.Message}", "警告",
 MessageBoxButtons.OK, MessageBoxIcon.Warning);
 }
 }

 private void InsertRowValues(int row)
 {
 if (!EnsureConnectedForWrite()) return;

 var entries = new List<(int row, int col, string propName, string value)>();
 for (int c = 1; c < dgvTable.Columns.Count; c++)
 {
 var propCell = dgvTable.Rows[0].Cells[c];
 var valCell = dgvTable.Rows[row].Cells[c];
 if (propCell.Value != null && !string.IsNullOrWhiteSpace(propCell.Value.ToString())
 && valCell.Value != null && !string.IsNullOrWhiteSpace(valCell.Value.ToString()))
 {
 string propName = ResolveTargetPropName(c);
 if (!string.IsNullOrEmpty(propName))
 entries.Add((row, c, propName, valCell.Value.ToString()));
 }
 }

 if (entries.Count == 0)
 {
 AddLog($" 第{row + 1}行没有可插入的有效值");
 return;
 }

 AddLog($" 开始插入整行（第{row + 1}行，共{entries.Count}个属性，从左到右逐个插入）...");
 var (success, fail) = InsertEntriesOneByOne(entries);
 AddLog($" 整行插入完成: 成功{success}个, 失败{fail}个");
 if (!IsConnected()) { DropSession(); UpdateConnectionDisplay(false); }
 RefreshPropertyValues();
 }

 private void InsertSelectedValues()
 {
 if (!EnsureConnectedForWrite()) return;

 // Shift 多选：从左到右、从上到下（行优先）排序
 var entries = GetValidSelectedCells()
 .Select(c => (row: c.RowIndex, col: c.ColumnIndex,
 propName: ResolveTargetPropName(c.ColumnIndex),
 value: c.Value.ToString()))
 .Where(e => !string.IsNullOrEmpty(e.propName))
 .OrderBy(e => e.row).ThenBy(e => e.col)
 .ToList();

 if (entries.Count == 0)
 {
 AddLog(" 所选单元格中没有有效值");
 return;
 }

 AddLog($" 开始插入选中的多个单元格（共{entries.Count}个，按从左到右、从上到下顺序逐个插入）...");
 foreach (var entry in entries)
 {
 string cellRef = $"{ColIdxToExcel(entry.col)}{entry.row + 1}";
 AddLog($" [{cellRef}] {entry.propName}");
 }

 var (success, fail) = InsertEntriesOneByOne(entries);
 AddLog($" 多个单元格插入完成: 成功{success}个, 失败{fail}个");
 if (!IsConnected()) { DropSession(); UpdateConnectionDisplay(false); }
 RefreshPropertyValues();
 }

 /// <summary>按给定顺序逐个写入属性值，单项失败不中断，逐条输出日志。</summary>
 private (int success, int fail) InsertEntriesOneByOne(
 List<(int row, int col, string propName, string value)> entries)
 {
 int success = 0, fail = 0;
 foreach (var entry in entries)
 {
 try
 {
 CaxaAutomation.WriteFirstElementCustomProps(_app,
 new Dictionary<string, string> { { entry.propName, entry.value } });
 AddLog($" {entry.propName} = {entry.value}");
 success++;
 }
 catch (Exception ex)
 {
 Diagnostics.Log("insert one failed: " + ex);
 AddLog($" {entry.propName} 插入失败: {ex.Message}");
 fail++;
 }
 }
 return (success, fail);
 }

 #endregion

 #region ===== 保存表格 =====

 private void btnSaveTable_Click(object sender, EventArgs e)
 {
 if (excelData == null || string.IsNullOrEmpty(tableFilePath))
 {
 AddLog(" 没有可保存的表格");
 return;
 }

 try
 {
 AddLog(" 正在保存表格...");

 // 从DataGridView同步数据到网格
 var grid = new List<List<string>>();
 for (int r = 0; r < dgvTable.Rows.Count; r++)
 {
 var row = new List<string>();
 for (int c = 0; c < dgvTable.Columns.Count; c++)
 {
 var cell = dgvTable.Rows[r].Cells[c];
 row.Add(cell.Value?.ToString() ?? "");
 }
 grid.Add(row);
 }

 XlsxLite.WriteFirstSheet(tableFilePath, grid);
 AddLog($" 表格已保存: {Path.GetFileName(tableFilePath)}（样式不保留）");
 }
 catch (Exception ex)
 {
 AddLog($" 保存表格失败: {ex.Message}");
 MessageBox.Show($"保存表格失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
 }
 }

 #endregion

 #region ===== 拖拽调整表格高度 =====

 private void resizeHandle_MouseDown(object sender, MouseEventArgs e)
 {
 isDragging = true;
 dragStartY = Cursor.Position.Y;
 dragStartHeight = dgvTable.Height;
 }

 private void resizeHandle_MouseMove(object sender, MouseEventArgs e)
 {
 if (!isDragging) return;
 int deltaY = Cursor.Position.Y - dragStartY;
 int newHeight = dragStartHeight + deltaY;
 newHeight = Math.Max(300, newHeight);
 dgvTable.Height = newHeight;

 int fixedExtra = 400; // 前置按钮栏 + 拖拽条 + 日志区 + 边距（grpConnection 已隐藏）
 int newWinH = newHeight + fixedExtra;
 newWinH = Math.Max(600, newWinH);
 this.Height = newWinH;
 }

 private void resizeHandle_MouseUp(object sender, MouseEventArgs e)
 {
 isDragging = false;
 }

 #endregion

 #region ===== 窗口事件 =====

 protected override void OnFormClosing(FormClosingEventArgs e)
 {
 timerCheck.Stop();
 DropSession();
 AddLog(" 程序退出");
 base.OnFormClosing(e);
 }

 #endregion
 }
}
