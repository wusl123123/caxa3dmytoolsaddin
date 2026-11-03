// 主窗口：窗口前置 + 备份 + 还原 + 停止 + 保存/清理日志
// 铁律：
//  - 所有按钮文字纯汉字，禁用 emoji / 符号 / 图标
//  - _running / _stopRequested 必须是 static（同一 CAXA 进程内只允许一条流程）
//  - FormClosing 必须置停止标志并中断流程（否则旧流程变僵尸）
//  - 向已释放控件写日志时抛 OperationStoppedException 打停流程，不许静默吞掉
// 本插件纯文件/注册表操作，不触碰 ICAPI COM；备份/还原流程仍走 _running 互斥 + 停止入口。
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dsetbackup
{
    [System.Runtime.InteropServices.ComVisible(false)]
    public class MainForm : Form
    {
        private static volatile bool _running;
        private static volatile bool _stopRequested;

        private readonly IZBaseApp _app;
        private readonly TextBox _log = new TextBox();
        private readonly Button _btnTop = new Button();
        private readonly Button _btnBackup = new Button();
        private readonly Button _btnRestore = new Button();
        private readonly Button _btnStop = new Button();
        private readonly Button _btnSaveLog = new Button();
        private readonly Button _btnClearLog = new Button();

        public MainForm(IZBaseApp app)
        {
            _app = app;
            Diagnostics.Reset();
            BuildUi();

            // 铁律：句柄必须在 InitSelf 线程预创建（窗口初始隐藏也要建句柄），
            // 否则等到 OnClick 才建，跨线程 Show 会与 CAXA 主线程互等死锁。
            // 注意 CreateHandle 是 protected：只能在 Form 内部调，外部 Addin.cs 调不到（CS0122）。
            if (!IsHandleCreated) CreateHandle();
        }

        private void BuildUi()
        {
            Text = "caxa3d设置备份工具";
            ClientSize = new Size(620, 460);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;                       // 默认置顶，前置按钮显示为已锁定
            FormClosing += OnFormClosing;

            _btnTop.SetBounds(12, 12, 88, 28);
            _btnTop.Text = "窗口前置";
            _btnTop.BackColor = Color.LightSkyBlue;
            _btnTop.Click += OnTopMostClick;

            _btnBackup.SetBounds(112, 12, 88, 28);
            _btnBackup.Text = "备份";
            _btnBackup.BackColor = Color.LightGreen;
            _btnBackup.Click += OnBackupClick;

            _btnRestore.SetBounds(212, 12, 88, 28);
            _btnRestore.Text = "还原";
            _btnRestore.BackColor = Color.Khaki;
            _btnRestore.Click += OnRestoreClick;

            _btnStop.SetBounds(312, 12, 88, 28);
            _btnStop.Text = "停止";
            _btnStop.Enabled = false;
            _btnStop.Click += OnStopClick;

            _btnSaveLog.SetBounds(412, 12, 88, 28);
            _btnSaveLog.Text = "保存日志";
            _btnSaveLog.Click += OnSaveLogClick;

            _btnClearLog.SetBounds(512, 12, 88, 28);
            _btnClearLog.Text = "清理日志";
            _btnClearLog.Click += OnClearLogClick;

            _log.SetBounds(12, 50, 596, 398);
            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            Controls.AddRange(new Control[] { _btnTop, _btnBackup, _btnRestore, _btnStop, _btnSaveLog, _btnClearLog, _log });

            AddLog("caxa3d设置备份工具——备份/还原 CAXA 3D 设计环境与图纸环境配置");
            AddLog("备份范围：注册表 CAXA 全树、3D 属性模板/明细表/自定义材料/界面定制、图纸环境系统配置与界面定制");
        }

        // ---------------------------------------------------------------- UI

        private void OnTopMostClick(object sender, EventArgs e)
        {
            TopMost = !TopMost;
            _btnTop.BackColor = TopMost ? Color.LightSkyBlue : SystemColors.Control;
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            _stopRequested = true;
            AddLog("已请求停止，等待当前步收尾…");
        }

        private void OnSaveLogClick(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog { Filter = "文本文件|*.txt", FileName = "caxa3dsetbackup-log.txt" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dlg.FileName, _log.Text); AddLog("日志已保存：" + dlg.FileName); }
                catch (Exception ex) { AddLog("保存日志失败：" + ex.Message); }
            }
        }

        private void OnClearLogClick(object sender, EventArgs e)
        {
            _log.Clear();
            Diagnostics.Log("log cleared by user");
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _stopRequested = true;                 // 进程级：必须中断流程，不留僵尸
        }

        /// <summary>UI 日志。控件已释放时抛 OperationStoppedException 打停流程。</summary>
        public void AddLog(string message)
        {
            if (IsDisposed || _log.IsDisposed) throw new OperationStoppedException();

            if (InvokeRequired) { BeginInvoke(new Action<string>(AddLog), message); return; }

            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + message + "\r\n");
            if (_log.TextLength > 30000) _log.Text = _log.Text.Substring(_log.TextLength - 20000);
        }

        // ---------------------------------------------------------------- 备份

        private void OnBackupClick(object sender, EventArgs e)
        {
            if (_running) { AddLog("已有流程在运行"); return; }

            _running = true;
            _stopRequested = false;
            SetBusy(true);

            try
            {
                AddLog("=== 开始备份 ===");
                string staging = BackupEngine.PrepareBackup(AddLog);
                if (_stopRequested) throw new OperationStoppedException();

                string stamp = DateTime.Now.ToString("yyyyMMddHHmm");
                string dest = null;
                using (var dlg = new SaveFileDialog
                {
                    Filter = "caxa3d配置备份|*.zip",
                    FileName = "caxa3d配置备份-" + stamp + ".zip",
                    Title = "选择备份包存放位置"
                })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) dest = dlg.FileName;
                }

                if (dest == null)
                {
                    AddLog("已取消（未选择存放位置）");
                    Diagnostics.Log("backup cancelled by user");
                }
                else
                {
                    BackupEngine.PackZip(staging, dest, AddLog);
                    AddLog("=== 备份完成 ===");
                    Diagnostics.Log("backup done: " + dest);
                }
            }
            catch (OperationStoppedException)
            {
                AddLog("已停止（备份未完成）");
            }
            catch (Exception ex)
            {
                AddLog("备份失败：" + ex.Message);
                Diagnostics.Log("backup failed: " + ex, 10000);
            }
            finally
            {
                _running = false;
                _stopRequested = false;
                SetBusy(false);
            }
        }

        // ---------------------------------------------------------------- 还原

        private void OnRestoreClick(object sender, EventArgs e)
        {
            if (_running) { AddLog("已有流程在运行"); return; }

            string zipPath = null;
            using (var dlg = new OpenFileDialog { Filter = "caxa3d配置备份|*.zip", Title = "选择本程序生成的备份包" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) zipPath = dlg.FileName;
            }
            if (zipPath == null) return;

            var r = MessageBox.Show(this,
                "将用备份包覆盖当前配置：\n" + zipPath + "\n\n" +
                "建议先关闭所有打开的文档。\n" +
                "还原完成后请尽快退出 CAXA 且不要改动设置（退出时可能回写部分界面配置）。\n\n是否继续？",
                "还原确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;

            _running = true;
            _stopRequested = false;
            SetBusy(true);

            try
            {
                AddLog("=== 开始还原：" + zipPath + " ===");
                int ok = BackupEngine.RestoreFromZip(zipPath, AddLog);
                AddLog("=== 还原完成（成功 " + ok + " 个文件）===");
            }
            catch (OperationStoppedException)
            {
                AddLog("已停止（还原可能未完成）");
            }
            catch (Exception ex)
            {
                AddLog("还原失败：" + ex.Message);
                Diagnostics.Log("restore failed: " + ex, 10000);
            }
            finally
            {
                _running = false;
                _stopRequested = false;
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _btnBackup.Enabled = !busy;
            _btnRestore.Enabled = !busy;
            _btnStop.Enabled = busy;
        }
    }
}
