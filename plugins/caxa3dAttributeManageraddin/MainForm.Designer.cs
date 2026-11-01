﻿﻿﻿namespace caxa3dAttributeManageraddin
{
 partial class MainForm
 {
 private System.ComponentModel.IContainer components = null;

 protected override void Dispose(bool disposing)
 {
 if (disposing && (components != null))
 {
 components.Dispose();
 }
 base.Dispose(disposing);
 }

 #region Windows Form Designer generated code

 private void InitializeComponent()
 {
 this.components = new System.ComponentModel.Container();
 this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
 this.ClientSize = new System.Drawing.Size(1400, 1050);
 this.MinimumSize = new System.Drawing.Size(1200, 600);
 this.Text = "CAXA 3D自定义属性管理器";
 this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;

 // 主面板
 this.mainPanel = new System.Windows.Forms.Panel();
 this.mainPanel.Dock = System.Windows.Forms.DockStyle.Fill;
 this.mainPanel.AutoScroll = true;
 this.mainPanel.Padding = new System.Windows.Forms.Padding(15);
 this.mainPanel.BackColor = System.Drawing.Color.FromArgb(240, 240, 240);

 // ===== CAXA 3D连接状态区域 =====
 this.grpConnection = new System.Windows.Forms.GroupBox();
 this.grpConnection.Text = "CAXA 3D连接状态";
 this.grpConnection.Font = new System.Drawing.Font("Microsoft YaHei", 11F, System.Drawing.FontStyle.Bold);
 this.grpConnection.ForeColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.grpConnection.Height = 60;
 this.grpConnection.Dock = System.Windows.Forms.DockStyle.Top;
 this.grpConnection.Padding = new System.Windows.Forms.Padding(8, 6, 8, 6);

 this.lblConnectionStatus = new System.Windows.Forms.Label();
 this.lblConnectionStatus.Text = "连接状态: 未连接";
 this.lblConnectionStatus.Font = new System.Drawing.Font("Microsoft YaHei", 11F, System.Drawing.FontStyle.Bold);
 this.lblConnectionStatus.ForeColor = System.Drawing.Color.FromArgb(209, 52, 56);
 this.lblConnectionStatus.AutoSize = true;
 this.lblConnectionStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

 this.btnRefresh = new System.Windows.Forms.Button();
 this.btnRefresh.Text = "刷新连接";
 this.btnRefresh.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.btnRefresh.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.btnRefresh.ForeColor = System.Drawing.Color.White;
 this.btnRefresh.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
 this.btnRefresh.FlatAppearance.BorderSize = 0;
 this.btnRefresh.Size = new System.Drawing.Size(90, 28);
 this.btnRefresh.Margin = new System.Windows.Forms.Padding(6, 0, 6, 0);
 this.btnRefresh.Cursor = System.Windows.Forms.Cursors.Hand;
 this.btnRefresh.Click += new System.EventHandler(this.btnRefresh_Click);

 this.lblFileName = new System.Windows.Forms.Label();
 this.lblFileName.Text = "文件: 无";
 this.lblFileName.Font = new System.Drawing.Font("Microsoft YaHei", 10F);
 this.lblFileName.ForeColor = System.Drawing.Color.FromArgb(51, 51, 51);
 this.lblFileName.AutoSize = false;
 this.lblFileName.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
 this.lblFileName.AutoEllipsis = true;

 this.btnLocateFile = new System.Windows.Forms.Button();
 this.btnLocateFile.Text = "定位文件";
 this.btnLocateFile.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.btnLocateFile.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.btnLocateFile.ForeColor = System.Drawing.Color.White;
 this.btnLocateFile.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
 this.btnLocateFile.FlatAppearance.BorderSize = 0;
 this.btnLocateFile.Size = new System.Drawing.Size(90, 28);
 this.btnLocateFile.Margin = new System.Windows.Forms.Padding(6, 0, 6, 0);
 this.btnLocateFile.Cursor = System.Windows.Forms.Cursors.Hand;
 this.btnLocateFile.Enabled = false;
 this.btnLocateFile.Click += new System.EventHandler(this.btnLocateFile_Click);

 var connFlow = new System.Windows.Forms.FlowLayoutPanel();
 connFlow.AutoSize = true;
 connFlow.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
 connFlow.Dock = System.Windows.Forms.DockStyle.None;
 connFlow.Anchor = System.Windows.Forms.AnchorStyles.None;
 connFlow.FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight;
 connFlow.WrapContents = false;
 connFlow.Margin = new System.Windows.Forms.Padding(0);
 connFlow.Padding = new System.Windows.Forms.Padding(0, 2, 0, 0);
 connFlow.Controls.Add(this.lblConnectionStatus);
 connFlow.Controls.Add(this.btnRefresh);

 // ===== 连接状态行：左=连接状态，中=文件名（自适应宽度，右侧省略），右=定位文件按钮 =====
 var connLayout = new System.Windows.Forms.TableLayoutPanel();
 connLayout.Dock = System.Windows.Forms.DockStyle.Fill;
 connLayout.ColumnCount = 3;
 connLayout.RowCount = 1;
 connLayout.Margin = new System.Windows.Forms.Padding(0);
 connLayout.Padding = new System.Windows.Forms.Padding(0);
 connLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.AutoSize));
 connLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
 connLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 100F));
 connLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));

 this.lblFileName.Margin = new System.Windows.Forms.Padding(12, 0, 6, 0);
 this.lblFileName.Dock = System.Windows.Forms.DockStyle.Fill;
 this.btnLocateFile.Anchor = System.Windows.Forms.AnchorStyles.None;

 connLayout.Controls.Add(connFlow, 0, 0);
 connLayout.Controls.Add(this.lblFileName, 1, 0);
 connLayout.Controls.Add(this.btnLocateFile, 2, 0);

 this.grpConnection.Controls.Add(connLayout);

 // ===== 表格容器 =====
 this.tableContainer = new System.Windows.Forms.Panel();
 this.tableContainer.Dock = System.Windows.Forms.DockStyle.Fill;
 this.tableContainer.Padding = new System.Windows.Forms.Padding(0, 10, 0, 0);

 // 表格区域
 this.grpTable = new System.Windows.Forms.GroupBox();
 this.grpTable.Text = "自定义属性表格";
 this.grpTable.Font = new System.Drawing.Font("Microsoft YaHei", 11F, System.Drawing.FontStyle.Bold);
 this.grpTable.ForeColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.grpTable.Dock = System.Windows.Forms.DockStyle.Fill;
 this.grpTable.Padding = new System.Windows.Forms.Padding(10);

 // 表格文件信息行
 var tableInfoPanel = new System.Windows.Forms.TableLayoutPanel();
 tableInfoPanel.Dock = System.Windows.Forms.DockStyle.Top;
 tableInfoPanel.Height = 36;
 tableInfoPanel.ColumnCount = 2;
 tableInfoPanel.RowCount = 1;
 tableInfoPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
 tableInfoPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 620F));
 tableInfoPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 36F));

 this.lblTableFile = new System.Windows.Forms.Label();
 this.lblTableFile.Text = "表格文件: 未选择";
 this.lblTableFile.Font = new System.Drawing.Font("Microsoft YaHei", 10F);
 this.lblTableFile.ForeColor = System.Drawing.Color.FromArgb(102, 102, 102);
 this.lblTableFile.AutoSize = false;
 this.lblTableFile.AutoEllipsis = true;
 this.lblTableFile.Dock = System.Windows.Forms.DockStyle.Fill;
 this.lblTableFile.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

 var tableBtnFlow = new System.Windows.Forms.FlowLayoutPanel();
 tableBtnFlow.Dock = System.Windows.Forms.DockStyle.Fill;
 tableBtnFlow.FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft;
 tableBtnFlow.WrapContents = false;
 tableBtnFlow.AutoSize = true;
 tableBtnFlow.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;

 this.btnRefreshValues = new System.Windows.Forms.Button();
 this.btnRefreshValues.Text = "刷新自定义属性";
 this.btnRefreshValues.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.btnRefreshValues.BackColor = System.Drawing.Color.FromArgb(16, 124, 16);
 this.btnRefreshValues.ForeColor = System.Drawing.Color.White;
 this.btnRefreshValues.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
 this.btnRefreshValues.FlatAppearance.BorderSize = 0;
 this.btnRefreshValues.Size = new System.Drawing.Size(130, 30);
 this.btnRefreshValues.Cursor = System.Windows.Forms.Cursors.Hand;
 this.btnRefreshValues.Enabled = false;
 this.btnRefreshValues.Click += new System.EventHandler(this.btnRefreshValues_Click);

 this.btnSaveTable = new System.Windows.Forms.Button();
 this.btnSaveTable.Text = "保存表格";
 this.btnSaveTable.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.btnSaveTable.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.btnSaveTable.ForeColor = System.Drawing.Color.White;
 this.btnSaveTable.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
 this.btnSaveTable.FlatAppearance.BorderSize = 0;
 this.btnSaveTable.Size = new System.Drawing.Size(90, 30);
 this.btnSaveTable.Cursor = System.Windows.Forms.Cursors.Hand;
 this.btnSaveTable.Enabled = false;
 this.btnSaveTable.Click += new System.EventHandler(this.btnSaveTable_Click);

 this.btnLocateTable = new System.Windows.Forms.Button();
 this.btnLocateTable.Text = "定位表格";
 this.btnLocateTable.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.btnLocateTable.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.btnLocateTable.ForeColor = System.Drawing.Color.White;
 this.btnLocateTable.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
 this.btnLocateTable.FlatAppearance.BorderSize = 0;
 this.btnLocateTable.Size = new System.Drawing.Size(90, 30);
 this.btnLocateTable.Cursor = System.Windows.Forms.Cursors.Hand;
 this.btnLocateTable.Enabled = false;
 this.btnLocateTable.Click += new System.EventHandler(this.btnLocateTable_Click);

 this.btnSelectTable = new System.Windows.Forms.Button();
 this.btnSelectTable.Text = "选择表格";
 this.btnSelectTable.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.btnSelectTable.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.btnSelectTable.ForeColor = System.Drawing.Color.White;
 this.btnSelectTable.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
 this.btnSelectTable.FlatAppearance.BorderSize = 0;
 this.btnSelectTable.Size = new System.Drawing.Size(90, 30);
 this.btnSelectTable.Cursor = System.Windows.Forms.Cursors.Hand;
 this.btnSelectTable.Click += new System.EventHandler(this.btnSelectTable_Click);

 this.btnReloadTable = new System.Windows.Forms.Button();
 this.btnReloadTable.Text = "刷新表格";
 this.btnReloadTable.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.btnReloadTable.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.btnReloadTable.ForeColor = System.Drawing.Color.White;
 this.btnReloadTable.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
 this.btnReloadTable.FlatAppearance.BorderSize = 0;
 this.btnReloadTable.Size = new System.Drawing.Size(90, 30);
 this.btnReloadTable.Cursor = System.Windows.Forms.Cursors.Hand;
 this.btnReloadTable.Enabled = false;
 this.btnReloadTable.Click += new System.EventHandler(this.btnReloadTable_Click);

 tableBtnFlow.Controls.Add(this.btnRefreshValues);
 tableBtnFlow.Controls.Add(this.btnSaveTable);
 tableBtnFlow.Controls.Add(this.btnLocateTable);
 tableBtnFlow.Controls.Add(this.btnSelectTable);
 tableBtnFlow.Controls.Add(this.btnReloadTable);

 tableInfoPanel.Controls.Add(this.lblTableFile, 0, 0);
 tableInfoPanel.Controls.Add(tableBtnFlow, 1, 0);

 // DataGridView 表格
 this.dgvTable = new System.Windows.Forms.DataGridView();
 this.dgvTable.Dock = System.Windows.Forms.DockStyle.Fill;
 this.dgvTable.AllowUserToAddRows = false;
 this.dgvTable.AllowUserToDeleteRows = false;
 this.dgvTable.AllowUserToResizeColumns = true;
 this.dgvTable.AllowUserToResizeRows = true;
 this.dgvTable.RowHeadersWidth = 70;
 this.dgvTable.ColumnHeadersHeight = 30;
 this.dgvTable.DefaultCellStyle.Font = new System.Drawing.Font("Microsoft YaHei", 10F);
 this.dgvTable.DefaultCellStyle.WrapMode = System.Windows.Forms.DataGridViewTriState.False;
 this.dgvTable.DefaultCellStyle.Padding = new System.Windows.Forms.Padding(3);
 this.dgvTable.ColumnHeadersDefaultCellStyle.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.dgvTable.ColumnHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(232, 232, 232);
 this.dgvTable.ColumnHeadersDefaultCellStyle.ForeColor = System.Drawing.Color.FromArgb(51, 51, 51);
 this.dgvTable.ColumnHeadersDefaultCellStyle.WrapMode = System.Windows.Forms.DataGridViewTriState.False;
 this.dgvTable.RowHeadersDefaultCellStyle.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.dgvTable.RowHeadersDefaultCellStyle.BackColor = System.Drawing.Color.FromArgb(232, 232, 232);
 this.dgvTable.RowHeadersDefaultCellStyle.WrapMode = System.Windows.Forms.DataGridViewTriState.False;
 this.dgvTable.GridColor = System.Drawing.Color.FromArgb(224, 224, 224);
 this.dgvTable.BorderStyle = System.Windows.Forms.BorderStyle.None;
 this.dgvTable.BackgroundColor = System.Drawing.Color.White;
 this.dgvTable.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.CellSelect;
 this.dgvTable.MultiSelect = true;
 this.dgvTable.EditMode = System.Windows.Forms.DataGridViewEditMode.EditOnKeystrokeOrF2;
 this.dgvTable.CellMouseClick += new System.Windows.Forms.DataGridViewCellMouseEventHandler(this.dgvTable_CellMouseClick);
 this.dgvTable.CellValueChanged += new System.Windows.Forms.DataGridViewCellEventHandler(this.dgvTable_CellValueChanged);
 this.dgvTable.ColumnHeaderMouseClick += new System.Windows.Forms.DataGridViewCellMouseEventHandler(this.dgvTable_ColumnHeaderMouseClick);
 this.dgvTable.RowHeaderMouseClick += new System.Windows.Forms.DataGridViewCellMouseEventHandler(this.dgvTable_RowHeaderMouseClick);
 this.dgvTable.CellPainting += new System.Windows.Forms.DataGridViewCellPaintingEventHandler(this.dgvTable_CellPainting);

 // 拖拽条
 this.resizeHandle = new System.Windows.Forms.Panel();
 this.resizeHandle.Height = 10;
 this.resizeHandle.Dock = System.Windows.Forms.DockStyle.Bottom;
 this.resizeHandle.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.resizeHandle.Cursor = System.Windows.Forms.Cursors.SizeNS;
 this.resizeHandle.MouseDown += new System.Windows.Forms.MouseEventHandler(this.resizeHandle_MouseDown);
 this.resizeHandle.MouseMove += new System.Windows.Forms.MouseEventHandler(this.resizeHandle_MouseMove);
 this.resizeHandle.MouseUp += new System.Windows.Forms.MouseEventHandler(this.resizeHandle_MouseUp);

 this.grpTable.Controls.Add(this.dgvTable);
 this.grpTable.Controls.Add(this.resizeHandle);
 this.grpTable.Controls.Add(tableInfoPanel);

 // ===== 日志区域 =====
 this.grpLog = new System.Windows.Forms.GroupBox();
 this.grpLog.Text = "操作日志";
 this.grpLog.Font = new System.Drawing.Font("Microsoft YaHei", 11F, System.Drawing.FontStyle.Bold);
 this.grpLog.ForeColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.grpLog.Dock = System.Windows.Forms.DockStyle.Bottom;
 this.grpLog.Height = 220;
 this.grpLog.Padding = new System.Windows.Forms.Padding(8);

 this.txtLog = new System.Windows.Forms.RichTextBox();
 this.txtLog.Dock = System.Windows.Forms.DockStyle.Fill;
 this.txtLog.ReadOnly = true;
 this.txtLog.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
 this.txtLog.ForeColor = System.Drawing.Color.FromArgb(212, 212, 212);
 this.txtLog.Font = new System.Drawing.Font("Consolas", 10F);
 this.txtLog.BorderStyle = System.Windows.Forms.BorderStyle.None;
 this.txtLog.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.Vertical;

 var logBtnPanel = new System.Windows.Forms.Panel();
 logBtnPanel.Dock = System.Windows.Forms.DockStyle.Bottom;
 logBtnPanel.Height = 40;

 this.btnSaveLog = new System.Windows.Forms.Button();
 this.btnSaveLog.Text = "保存日志";
 this.btnSaveLog.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.btnSaveLog.BackColor = System.Drawing.Color.FromArgb(0, 120, 212);
 this.btnSaveLog.ForeColor = System.Drawing.Color.White;
 this.btnSaveLog.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
 this.btnSaveLog.FlatAppearance.BorderSize = 0;
 this.btnSaveLog.Size = new System.Drawing.Size(100, 32);
 this.btnSaveLog.Location = new System.Drawing.Point(0, 4);
 this.btnSaveLog.Cursor = System.Windows.Forms.Cursors.Hand;
 this.btnSaveLog.Click += new System.EventHandler(this.btnSaveLog_Click);

 this.btnClearLog = new System.Windows.Forms.Button();
 this.btnClearLog.Text = "清空日志";
 this.btnClearLog.Font = new System.Drawing.Font("Microsoft YaHei", 9F, System.Drawing.FontStyle.Bold);
 this.btnClearLog.BackColor = System.Drawing.Color.FromArgb(209, 52, 56);
 this.btnClearLog.ForeColor = System.Drawing.Color.White;
 this.btnClearLog.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
 this.btnClearLog.FlatAppearance.BorderSize = 0;
 this.btnClearLog.Size = new System.Drawing.Size(100, 32);
 this.btnClearLog.Location = new System.Drawing.Point(108, 4);
 this.btnClearLog.Cursor = System.Windows.Forms.Cursors.Hand;
 this.btnClearLog.Click += new System.EventHandler(this.btnClearLog_Click);

 logBtnPanel.Controls.Add(this.btnSaveLog);
 logBtnPanel.Controls.Add(this.btnClearLog);
 this.grpLog.Controls.Add(this.txtLog);
 this.grpLog.Controls.Add(logBtnPanel);

 this.tableContainer.Controls.Add(this.grpTable);
 this.tableContainer.Controls.Add(this.grpLog);

 // 组装主面板
 this.mainPanel.Controls.Add(this.tableContainer);
 this.mainPanel.Controls.Add(this.grpConnection);

 this.Controls.Add(this.mainPanel);

 // 右键菜单
 this.cmsCell = new System.Windows.Forms.ContextMenuStrip();
 this.cmsColumnHeader = new System.Windows.Forms.ContextMenuStrip();
 this.cmsRowHeader = new System.Windows.Forms.ContextMenuStrip();

 // ToolTip
 this.toolTip1 = new System.Windows.Forms.ToolTip(this.components);
 this.toolTip1.AutoPopDelay = 15000;
 this.toolTip1.InitialDelay = 500;
 this.toolTip1.ReshowDelay = 100;

 // 定时器检查 CAXA 连接
 this.timerCheck = new System.Windows.Forms.Timer(this.components);
 this.timerCheck.Interval = 2000;
 this.timerCheck.Tick += new System.EventHandler(this.timerCheck_Tick);
 this.timerCheck.Start();
 }

 #endregion

 // 控件声明
 private System.Windows.Forms.Panel mainPanel;
 private System.Windows.Forms.GroupBox grpConnection;
 private System.Windows.Forms.Label lblConnectionStatus;
 private System.Windows.Forms.Button btnRefresh;
 private System.Windows.Forms.Label lblFileName;
 private System.Windows.Forms.Button btnLocateFile;
 private System.Windows.Forms.Panel tableContainer;
 private System.Windows.Forms.GroupBox grpTable;
 private System.Windows.Forms.Label lblTableFile;
 private System.Windows.Forms.Button btnRefreshValues;
 private System.Windows.Forms.Button btnSaveTable;
 private System.Windows.Forms.Button btnLocateTable;
 private System.Windows.Forms.Button btnSelectTable;
 private System.Windows.Forms.Button btnReloadTable;
 private System.Windows.Forms.DataGridView dgvTable;
 private System.Windows.Forms.Panel resizeHandle;
 private System.Windows.Forms.GroupBox grpLog;
 private System.Windows.Forms.RichTextBox txtLog;
 private System.Windows.Forms.Button btnSaveLog;
 private System.Windows.Forms.Button btnClearLog;
 private System.Windows.Forms.ContextMenuStrip cmsCell;
 private System.Windows.Forms.ContextMenuStrip cmsColumnHeader;
 private System.Windows.Forms.ContextMenuStrip cmsRowHeader;
 private System.Windows.Forms.Timer timerCheck;
 private System.Windows.Forms.ToolTip toolTip1;

 // 拖拽相关
 private bool isDragging = false;
 private int dragStartY;
 private int dragStartHeight;
 }
}
