namespace Caxa3dcudeaddin
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        private System.Windows.Forms.Label lblTitle;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.Label lblLength;
        private System.Windows.Forms.Label lblWidth;
        private System.Windows.Forms.Label lblHeight;
        private System.Windows.Forms.TextBox txtLength;
        private System.Windows.Forms.TextBox txtWidth;
        private System.Windows.Forms.TextBox txtHeight;
        private System.Windows.Forms.Button btnCreate;
        private System.Windows.Forms.Label lblResult;
        private System.Windows.Forms.Label lblUnit;
        private System.Windows.Forms.TextBox txtLog;
        private System.Windows.Forms.Button btnClearLog;
        private System.Windows.Forms.Button btnCopyLog;

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
            this.lblTitle = new System.Windows.Forms.Label();
            this.lblStatus = new System.Windows.Forms.Label();
            this.lblLength = new System.Windows.Forms.Label();
            this.lblWidth = new System.Windows.Forms.Label();
            this.lblHeight = new System.Windows.Forms.Label();
            this.txtLength = new System.Windows.Forms.TextBox();
            this.txtWidth = new System.Windows.Forms.TextBox();
            this.txtHeight = new System.Windows.Forms.TextBox();
            this.btnCreate = new System.Windows.Forms.Button();
            this.lblResult = new System.Windows.Forms.Label();
            this.lblUnit = new System.Windows.Forms.Label();
            this.txtLog = new System.Windows.Forms.TextBox();
            this.btnClearLog = new System.Windows.Forms.Button();
            this.btnCopyLog = new System.Windows.Forms.Button();
            this.SuspendLayout();
            //
            // lblTitle
            //
            this.lblTitle.Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Bold);
            this.lblTitle.Location = new System.Drawing.Point(16, 14);
            this.lblTitle.Name = "lblTitle";
            this.lblTitle.Size = new System.Drawing.Size(352, 26);
            this.lblTitle.TabIndex = 0;
            this.lblTitle.Text = "CAXA 3D 立方体建立工具";
            this.lblTitle.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // lblStatus
            //
            this.lblStatus.Location = new System.Drawing.Point(18, 46);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(350, 22);
            this.lblStatus.TabIndex = 1;
            this.lblStatus.Text = "已连接（进程内）";
            this.lblStatus.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // lblLength
            //
            this.lblLength.Location = new System.Drawing.Point(18, 74);
            this.lblLength.Name = "lblLength";
            this.lblLength.Size = new System.Drawing.Size(112, 20);
            this.lblLength.TabIndex = 2;
            this.lblLength.Text = "长 (X)  mm";
            //
            // lblWidth
            //
            this.lblWidth.Location = new System.Drawing.Point(138, 74);
            this.lblWidth.Name = "lblWidth";
            this.lblWidth.Size = new System.Drawing.Size(112, 20);
            this.lblWidth.TabIndex = 3;
            this.lblWidth.Text = "宽 (Y)  mm";
            //
            // lblHeight
            //
            this.lblHeight.Location = new System.Drawing.Point(258, 74);
            this.lblHeight.Name = "lblHeight";
            this.lblHeight.Size = new System.Drawing.Size(112, 20);
            this.lblHeight.TabIndex = 4;
            this.lblHeight.Text = "高 (Z)  mm";
            //
            // txtLength
            //
            this.txtLength.Location = new System.Drawing.Point(18, 96);
            this.txtLength.Name = "txtLength";
            this.txtLength.Size = new System.Drawing.Size(112, 23);
            this.txtLength.TabIndex = 5;
            this.txtLength.Text = "10";
            this.txtLength.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            //
            // txtWidth
            //
            this.txtWidth.Location = new System.Drawing.Point(138, 96);
            this.txtWidth.Name = "txtWidth";
            this.txtWidth.Size = new System.Drawing.Size(112, 23);
            this.txtWidth.TabIndex = 6;
            this.txtWidth.Text = "10";
            this.txtWidth.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            //
            // txtHeight
            //
            this.txtHeight.Location = new System.Drawing.Point(258, 96);
            this.txtHeight.Name = "txtHeight";
            this.txtHeight.Size = new System.Drawing.Size(112, 23);
            this.txtHeight.TabIndex = 7;
            this.txtHeight.Text = "10";
            this.txtHeight.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            //
            // btnCreate
            //
            this.btnCreate.Font = new System.Drawing.Font("Microsoft YaHei UI", 12F, System.Drawing.FontStyle.Bold);
            this.btnCreate.Location = new System.Drawing.Point(95, 132);
            this.btnCreate.Name = "btnCreate";
            this.btnCreate.Size = new System.Drawing.Size(190, 48);
            this.btnCreate.TabIndex = 8;
            this.btnCreate.Text = "建立立方体";
            this.btnCreate.UseVisualStyleBackColor = true;
            this.btnCreate.Click += new System.EventHandler(this.btnCreate_Click);
            //
            // lblUnit
            //
            this.lblUnit.ForeColor = System.Drawing.Color.DimGray;
            this.lblUnit.Location = new System.Drawing.Point(18, 188);
            this.lblUnit.Name = "lblUnit";
            this.lblUnit.Size = new System.Drawing.Size(350, 22);
            this.lblUnit.TabIndex = 9;
            this.lblUnit.Text = "在设计环境原点 (0,0,0) 建立 长(X) × 宽(Y) × 高(Z) mm";
            this.lblUnit.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // lblResult
            //
            this.lblResult.ForeColor = System.Drawing.Color.Green;
            this.lblResult.Location = new System.Drawing.Point(18, 212);
            this.lblResult.Name = "lblResult";
            this.lblResult.Size = new System.Drawing.Size(350, 22);
            this.lblResult.TabIndex = 10;
            this.lblResult.Text = "";
            this.lblResult.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // txtLog
            //
            this.txtLog.BackColor = System.Drawing.Color.White;
            this.txtLog.Font = new System.Drawing.Font("Consolas", 8.75F);
            this.txtLog.HideSelection = false;
            this.txtLog.Location = new System.Drawing.Point(12, 240);
            this.txtLog.Multiline = true;
            this.txtLog.Name = "txtLog";
            this.txtLog.ReadOnly = true;
            this.txtLog.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtLog.Size = new System.Drawing.Size(360, 182);
            this.txtLog.TabIndex = 11;
            this.txtLog.WordWrap = false;
            //
            // btnClearLog
            //
            this.btnClearLog.Location = new System.Drawing.Point(104, 430);
            this.btnClearLog.Name = "btnClearLog";
            this.btnClearLog.Size = new System.Drawing.Size(86, 28);
            this.btnClearLog.TabIndex = 12;
            this.btnClearLog.Text = "清空日志";
            this.btnClearLog.UseVisualStyleBackColor = true;
            this.btnClearLog.Click += new System.EventHandler(this.btnClearLog_Click);
            //
            // btnCopyLog
            //
            this.btnCopyLog.Location = new System.Drawing.Point(196, 430);
            this.btnCopyLog.Name = "btnCopyLog";
            this.btnCopyLog.Size = new System.Drawing.Size(86, 28);
            this.btnCopyLog.TabIndex = 13;
            this.btnCopyLog.Text = "复制日志";
            this.btnCopyLog.UseVisualStyleBackColor = true;
            this.btnCopyLog.Click += new System.EventHandler(this.btnCopyLog_Click);
            //
            // MainForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(384, 471);
            this.Controls.Add(this.lblTitle);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.lblLength);
            this.Controls.Add(this.lblWidth);
            this.Controls.Add(this.lblHeight);
            this.Controls.Add(this.txtLength);
            this.Controls.Add(this.txtWidth);
            this.Controls.Add(this.txtHeight);
            this.Controls.Add(this.btnCreate);
            this.Controls.Add(this.lblUnit);
            this.Controls.Add(this.lblResult);
            this.Controls.Add(this.txtLog);
            this.Controls.Add(this.btnClearLog);
            this.Controls.Add(this.btnCopyLog);
            this.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "CAXA 3D 立方体工具";
            this.Load += new System.EventHandler(this.MainForm_Load);
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
