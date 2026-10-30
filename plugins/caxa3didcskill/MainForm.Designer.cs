namespace caxa3didcskill
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.Label lblTitle;
        private System.Windows.Forms.GroupBox grpLink;
        private System.Windows.Forms.Label lblLinkStatus;
        private System.Windows.Forms.Button btnRefreshStatus;
        private System.Windows.Forms.Button btnMonitor;
        private System.Windows.Forms.TextBox txtMonitorInterval;
        private System.Windows.Forms.Label lblMonitorUnit;
        private System.Windows.Forms.Timer tmrMonitor;
        private System.Windows.Forms.GroupBox grpAction;
        private System.Windows.Forms.Button btnManual;
        private System.Windows.Forms.Button btnAuto;
        private System.Windows.Forms.TextBox txtInterval;
        private System.Windows.Forms.Label lblIntervalUnit;
        private System.Windows.Forms.GroupBox grpLog;
        private System.Windows.Forms.TextBox txtLog;
        private System.Windows.Forms.Button btnClearLog;
        private System.Windows.Forms.Button btnCopyLog;
        private System.Windows.Forms.Timer tmrAuto;
        private System.Windows.Forms.Button btnTopMost;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.lblTitle = new System.Windows.Forms.Label();
            this.grpLink = new System.Windows.Forms.GroupBox();
            this.lblLinkStatus = new System.Windows.Forms.Label();
            this.btnRefreshStatus = new System.Windows.Forms.Button();
            this.btnMonitor = new System.Windows.Forms.Button();
            this.txtMonitorInterval = new System.Windows.Forms.TextBox();
            this.lblMonitorUnit = new System.Windows.Forms.Label();
            this.grpAction = new System.Windows.Forms.GroupBox();
            this.btnManual = new System.Windows.Forms.Button();
            this.btnAuto = new System.Windows.Forms.Button();
            this.txtInterval = new System.Windows.Forms.TextBox();
            this.lblIntervalUnit = new System.Windows.Forms.Label();
            this.grpLog = new System.Windows.Forms.GroupBox();
            this.txtLog = new System.Windows.Forms.TextBox();
            this.btnClearLog = new System.Windows.Forms.Button();
            this.btnCopyLog = new System.Windows.Forms.Button();
            this.tmrAuto = new System.Windows.Forms.Timer(this.components);
            this.tmrMonitor = new System.Windows.Forms.Timer(this.components);
            this.btnTopMost = new System.Windows.Forms.Button();
            this.grpLink.SuspendLayout();
            this.grpAction.SuspendLayout();
            this.grpLog.SuspendLayout();
            this.SuspendLayout();
            //
            // btnTopMost
            //
            this.btnTopMost.Location = new System.Drawing.Point(12, 10);
            this.btnTopMost.Name = "btnTopMost";
            this.btnTopMost.Size = new System.Drawing.Size(100, 28);
            this.btnTopMost.TabIndex = 4;
            this.btnTopMost.Text = "窗口前置";
            this.btnTopMost.UseVisualStyleBackColor = false;
            this.btnTopMost.Click += new System.EventHandler(this.btnTopMost_Click);
            //
            // lblTitle
            //
            this.lblTitle.Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Bold);
            this.lblTitle.Location = new System.Drawing.Point(120, 12);
            this.lblTitle.Name = "lblTitle";
            this.lblTitle.Size = new System.Drawing.Size(320, 26);
            this.lblTitle.TabIndex = 0;
            this.lblTitle.Text = "CAXA 3D 后台进程清理工具";
            this.lblTitle.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // grpLink（隐藏：铁律——不显示连接状态）
            //
            this.grpLink.Controls.Add(this.lblLinkStatus);
            this.grpLink.Controls.Add(this.btnRefreshStatus);
            this.grpLink.Controls.Add(this.btnMonitor);
            this.grpLink.Controls.Add(this.txtMonitorInterval);
            this.grpLink.Controls.Add(this.lblMonitorUnit);
            this.grpLink.Location = new System.Drawing.Point(12, 44);
            this.grpLink.Name = "grpLink";
            this.grpLink.Size = new System.Drawing.Size(428, 116);
            this.grpLink.TabIndex = 1;
            this.grpLink.TabStop = false;
            this.grpLink.Text = "链接状态";
            this.grpLink.Visible = false;
            //
            // lblLinkStatus
            //
            this.lblLinkStatus.ForeColor = System.Drawing.Color.DimGray;
            this.lblLinkStatus.Location = new System.Drawing.Point(16, 26);
            this.lblLinkStatus.Name = "lblLinkStatus";
            this.lblLinkStatus.Size = new System.Drawing.Size(396, 22);
            this.lblLinkStatus.TabIndex = 0;
            this.lblLinkStatus.Text = "未检测 — 点击「刷新状态」";
            this.lblLinkStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // btnRefreshStatus
            //
            this.btnRefreshStatus.Location = new System.Drawing.Point(16, 62);
            this.btnRefreshStatus.Name = "btnRefreshStatus";
            this.btnRefreshStatus.Size = new System.Drawing.Size(96, 30);
            this.btnRefreshStatus.TabIndex = 1;
            this.btnRefreshStatus.Text = "刷新状态";
            this.btnRefreshStatus.UseVisualStyleBackColor = true;
            this.btnRefreshStatus.Click += new System.EventHandler(this.btnRefreshStatus_Click);
            //
            // btnMonitor
            //
            this.btnMonitor.Location = new System.Drawing.Point(120, 62);
            this.btnMonitor.Name = "btnMonitor";
            this.btnMonitor.Size = new System.Drawing.Size(110, 30);
            this.btnMonitor.TabIndex = 2;
            this.btnMonitor.Text = "启动监控";
            this.btnMonitor.UseVisualStyleBackColor = true;
            this.btnMonitor.Click += new System.EventHandler(this.btnMonitor_Click);
            //
            // txtMonitorInterval
            //
            this.txtMonitorInterval.Location = new System.Drawing.Point(242, 66);
            this.txtMonitorInterval.Name = "txtMonitorInterval";
            this.txtMonitorInterval.Size = new System.Drawing.Size(56, 23);
            this.txtMonitorInterval.TabIndex = 3;
            this.txtMonitorInterval.Text = "15";
            this.txtMonitorInterval.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            //
            // lblMonitorUnit
            //
            this.lblMonitorUnit.Location = new System.Drawing.Point(302, 66);
            this.lblMonitorUnit.Name = "lblMonitorUnit";
            this.lblMonitorUnit.Size = new System.Drawing.Size(104, 22);
            this.lblMonitorUnit.TabIndex = 4;
            this.lblMonitorUnit.Text = "min（分钟）";
            this.lblMonitorUnit.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // grpAction
            //
            this.grpAction.Controls.Add(this.btnManual);
            this.grpAction.Controls.Add(this.btnAuto);
            this.grpAction.Controls.Add(this.txtInterval);
            this.grpAction.Controls.Add(this.lblIntervalUnit);
            this.grpAction.Location = new System.Drawing.Point(12, 48);
            this.grpAction.Name = "grpAction";
            this.grpAction.Size = new System.Drawing.Size(428, 76);
            this.grpAction.TabIndex = 2;
            this.grpAction.TabStop = false;
            this.grpAction.Text = "操作";
            //
            // btnManual
            //
            this.btnManual.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold);
            this.btnManual.Location = new System.Drawing.Point(16, 26);
            this.btnManual.Name = "btnManual";
            this.btnManual.Size = new System.Drawing.Size(110, 36);
            this.btnManual.TabIndex = 0;
            this.btnManual.Text = "手动运行";
            this.btnManual.UseVisualStyleBackColor = true;
            this.btnManual.Click += new System.EventHandler(this.btnManual_Click);
            //
            // btnAuto
            //
            this.btnAuto.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold);
            this.btnAuto.Location = new System.Drawing.Point(136, 26);
            this.btnAuto.Name = "btnAuto";
            this.btnAuto.Size = new System.Drawing.Size(110, 36);
            this.btnAuto.TabIndex = 1;
            this.btnAuto.Text = "自动运行";
            this.btnAuto.UseVisualStyleBackColor = true;
            this.btnAuto.Click += new System.EventHandler(this.btnAuto_Click);
            //
            // txtInterval
            //
            this.txtInterval.Location = new System.Drawing.Point(262, 32);
            this.txtInterval.Name = "txtInterval";
            this.txtInterval.Size = new System.Drawing.Size(56, 23);
            this.txtInterval.TabIndex = 2;
            this.txtInterval.Text = "15";
            this.txtInterval.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            //
            // lblIntervalUnit
            //
            this.lblIntervalUnit.Location = new System.Drawing.Point(322, 32);
            this.lblIntervalUnit.Name = "lblIntervalUnit";
            this.lblIntervalUnit.Size = new System.Drawing.Size(80, 22);
            this.lblIntervalUnit.TabIndex = 3;
            this.lblIntervalUnit.Text = "min（分钟）";
            this.lblIntervalUnit.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            //
            // grpLog
            //
            this.grpLog.Controls.Add(this.txtLog);
            this.grpLog.Controls.Add(this.btnClearLog);
            this.grpLog.Controls.Add(this.btnCopyLog);
            this.grpLog.Location = new System.Drawing.Point(12, 132);
            this.grpLog.Name = "grpLog";
            this.grpLog.Size = new System.Drawing.Size(428, 378);
            this.grpLog.TabIndex = 3;
            this.grpLog.TabStop = false;
            this.grpLog.Text = "日志";
            //
            // txtLog
            //
            this.txtLog.BackColor = System.Drawing.Color.White;
            this.txtLog.Font = new System.Drawing.Font("Consolas", 8.75F);
            this.txtLog.HideSelection = false;
            this.txtLog.Location = new System.Drawing.Point(12, 24);
            this.txtLog.Multiline = true;
            this.txtLog.Name = "txtLog";
            this.txtLog.ReadOnly = true;
            this.txtLog.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtLog.Size = new System.Drawing.Size(404, 282);
            this.txtLog.TabIndex = 0;
            this.txtLog.WordWrap = false;
            //
            // btnClearLog
            //
            this.btnClearLog.Location = new System.Drawing.Point(12, 312);
            this.btnClearLog.Name = "btnClearLog";
            this.btnClearLog.Size = new System.Drawing.Size(86, 28);
            this.btnClearLog.TabIndex = 1;
            this.btnClearLog.Text = "清空日志";
            this.btnClearLog.UseVisualStyleBackColor = true;
            this.btnClearLog.Click += new System.EventHandler(this.btnClearLog_Click);
            //
            // btnCopyLog
            //
            this.btnCopyLog.Location = new System.Drawing.Point(104, 312);
            this.btnCopyLog.Name = "btnCopyLog";
            this.btnCopyLog.Size = new System.Drawing.Size(86, 28);
            this.btnCopyLog.TabIndex = 2;
            this.btnCopyLog.Text = "复制日志";
            this.btnCopyLog.UseVisualStyleBackColor = true;
            this.btnCopyLog.Click += new System.EventHandler(this.btnCopyLog_Click);
            //
            // tmrAuto
            //
            this.tmrAuto.Tick += new System.EventHandler(this.tmrAuto_Tick);
            //
            // tmrMonitor
            //
            this.tmrMonitor.Tick += new System.EventHandler(this.tmrMonitor_Tick);
            //
            // MainForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(452, 522);
            this.Controls.Add(this.btnTopMost);
            this.Controls.Add(this.lblTitle);
            this.Controls.Add(this.grpLink);
            this.Controls.Add(this.grpAction);
            this.Controls.Add(this.grpLog);
            this.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "CAXA 3D 后台进程清理工具";
            this.Load += new System.EventHandler(this.MainForm_Load);
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.MainForm_FormClosing);
            this.grpLink.ResumeLayout(false);
            this.grpAction.ResumeLayout(false);
            this.grpAction.PerformLayout();
            this.grpLog.ResumeLayout(false);
            this.grpLog.PerformLayout();
            this.ResumeLayout(false);
        }
    }
}
