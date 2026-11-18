// 主窗口：窗口前置 + 刷新列表 + 全选/全不选 + 备份 + 还原 + 停止 + 保存/清理日志
//
// 铁律：
//  - 所有按钮文字纯汉字，禁用 emoji / 符号 / 图标
//  - _running / _stopRequested 必须是 static（同一 CAXA 进程内只允许一条流程）
//  - FormClosing 必须置停止标志并中断流程（否则旧流程变僵尸）
//  - 向已释放控件写日志时抛 OperationStoppedException 打停流程，不许静默吞掉
//
// 按版本工作：
//  - 启动即扫描全部已安装版本的用户设计库 ToolBox 目录，逐条列在勾选列表里（默认全选）
//  - 备份只打包勾选的版本，包内按 toolboxes/<版本>/<序号>/files/<相对路径> 分层存储
//  - 还原先让用户选包，再在弹窗里勾选要还原的版本，逐个落到本机同版本的位置
//
// 本插件只读写用户目录下的 ToolBox 目录，不触碰 ICAPI COM 业务对象；
// 路径全部由 ToolboxLibrary 反查（环境变量 + 目录结构），不写死用户名/版本/语言/盘符。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dmytoolboxbak
{
    // OperationStoppedException 统一定义在 CaxaAutomation.cs，此处不再重复声明（否则 CS0101/CS0111）。

    [System.Runtime.InteropServices.ComVisible(false)]
    public class MainForm : Form
    {
        private static volatile bool _running;
        private static volatile bool _stopRequested;

        private readonly IZBaseApp _app;
        private readonly TextBox _log = new TextBox();
        private readonly CheckedListBox _list = new CheckedListBox();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private int _menuIndex = -1;          // 右键菜单对应的列表行号（菜单打开时定位）
        private readonly Label _tip = new Label();
        private readonly Button _btnTop = new Button();
        private readonly Button _btnRefresh = new Button();
        private readonly Button _btnAll = new Button();
        private readonly Button _btnNone = new Button();
        private readonly Button _btnBackup = new Button();
        private readonly Button _btnRestore = new Button();
        private readonly Button _btnStop = new Button();
        private readonly Button _btnSaveLog = new Button();
        private readonly Button _btnClearLog = new Button();

        private List<ToolItem> _items = new List<ToolItem>();

        public MainForm(IZBaseApp app)
        {
            _app = app;
            Diagnostics.Reset();
            BuildUi();

            // 铁律：句柄必须在 InitSelf 线程预创建（窗口初始隐藏也要建句柄），
            // 否则等到 OnClick 才建，跨线程 Show 会与 CAXA 主线程互等死锁。
            if (!IsHandleCreated) CreateHandle();
        }

        private void BuildUi()
        {
            Text = "自定义设计库备份";
            ClientSize = new Size(880, 620);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;                       // 默认置顶，前置按钮显示为已锁定
            FormClosing += OnFormClosing;

            int x = 12, y = 12, w = 84, h = 28, gap = 6;

            _btnTop.SetBounds(x, y, w, h);
            _btnTop.Text = "窗口前置";
            _btnTop.BackColor = Color.LightSkyBlue;
            _btnTop.Click += OnTopMostClick;
            x += w + gap;

            _btnRefresh.SetBounds(x, y, w, h);
            _btnRefresh.Text = "刷新列表";
            _btnRefresh.Click += OnRefreshClick;
            x += w + gap;

            _btnAll.SetBounds(x, y, w, h);
            _btnAll.Text = "全选";
            _btnAll.Click += OnAllClick;
            x += w + gap;

            _btnNone.SetBounds(x, y, w, h);
            _btnNone.Text = "全不选";
            _btnNone.Click += OnNoneClick;
            x += w + gap;

            _btnBackup.SetBounds(x, y, w, h);
            _btnBackup.Text = "备份";
            _btnBackup.BackColor = Color.LightGreen;
            _btnBackup.Click += OnBackupClick;
            x += w + gap;

            _btnRestore.SetBounds(x, y, w, h);
            _btnRestore.Text = "还原";
            _btnRestore.BackColor = Color.Khaki;
            _btnRestore.Click += OnRestoreClick;
            x += w + gap;

            _btnStop.SetBounds(x, y, w, h);
            _btnStop.Text = "停止";
            _btnStop.Enabled = false;
            _btnStop.Click += OnStopClick;
            x += w + gap;

            _btnSaveLog.SetBounds(x, y, w, h);
            _btnSaveLog.Text = "保存日志";
            _btnSaveLog.Click += OnSaveLogClick;
            x += w + gap;

            _btnClearLog.SetBounds(x, y, w, h);
            _btnClearLog.Text = "清理日志";
            _btnClearLog.Click += OnClearLogClick;

            _tip.SetBounds(12, 50, 856, 20);
            _tip.Text = "扫描到的用户设计库 ToolBox（勾选版本，可多选；只有勾选的版本会被备份）：";
            _tip.TextAlign = ContentAlignment.MiddleLeft;

            _list.SetBounds(12, 72, 856, 132);
            _list.CheckOnClick = true;
            _list.IntegralHeight = false;
            _list.HorizontalScrollbar = true;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            // 右键「打开所在文件夹」：打开时先定位到鼠标所指条目，再打开该条目的 ToolBox 目录
            _list.ContextMenuStrip = _menu;
            _menu.Items.Add("打开所在文件夹", null, OnOpenFolderClick);
            _menu.Opening += OnMenuOpening;

            _log.SetBounds(12, 214, 856, 394);
            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.BackColor = SystemColors.Window;
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            Controls.AddRange(new Control[]
            {
                _btnTop, _btnRefresh, _btnAll, _btnNone, _btnBackup, _btnRestore, _btnStop,
                _btnSaveLog, _btnClearLog, _tip, _list, _log
            });

            AddLog("自定义设计库备份——按版本备份 / 还原 CAXA 3D 用户自定义设计库 ToolBox");
            AddLog("备份对象：当前 Windows 用户下的 ToolBox 目录（含子目录与 .cache 等隐藏文件）");
            AddLog("路径不写死：按用户目录 + 安装版本反查，换机器 / 换版本后自动重新定位");
        }

        // ---------------------------------------------------------------- UI

        private void OnTopMostClick(object sender, EventArgs e)
        {
            TopMost = !TopMost;
            _btnTop.BackColor = TopMost ? Color.LightSkyBlue : SystemColors.Control;
        }

        private void OnAllClick(object sender, EventArgs e)
        {
            for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true);
        }

        private void OnNoneClick(object sender, EventArgs e)
        {
            for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, false);
        }

        private void OnStopClick(object sender, EventArgs e)
        {
            _stopRequested = true;
            AddLog("已请求停止，等待当前步收尾…");
        }

        private void OnSaveLogClick(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog { Filter = "文本文件|*.txt", FileName = "自定义设计库备份-日志.txt" })
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

        /// <summary>每次窗口显示时自动扫描一次（用户可能刚在 CAXA 里改过设计库）。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_running) return;
            try
            {
                ScanList();
            }
            catch (Exception ex)
            {
                AddLog("扫描失败：" + ex.Message);
                Diagnostics.Log("scan on shown failed: " + ex, 5000);
            }
        }

        /// <summary>UI 日志。控件已释放时抛 OperationStoppedException 打停流程。</summary>
        public void AddLog(string message)
        {
            if (IsDisposed || _log.IsDisposed) throw new OperationStoppedException();

            if (InvokeRequired) { BeginInvoke(new Action<string>(AddLog), message); return; }

            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + message + "\r\n");
            if (_log.TextLength > 30000) _log.Text = _log.Text.Substring(_log.TextLength - 20000);
        }

        // ---------------------------------------------------------------- 扫描列表

        private void OnRefreshClick(object sender, EventArgs e)
        {
            if (_running) { AddLog("已有流程在运行"); return; }

            _running = true;
            _stopRequested = false;
            SetBusy(true);
            try
            {
                ScanList();
            }
            catch (OperationStoppedException)
            {
                AddLog("已停止");
            }
            catch (Exception ex)
            {
                AddLog("扫描失败：" + ex.Message);
                Diagnostics.Log("scan failed: " + ex, 5000);
            }
            finally
            {
                _running = false;
                _stopRequested = false;
                SetBusy(false);
            }
        }

        /// <summary>扫描全部版本的用户设计库 ToolBox 目录并刷新勾选列表（默认全选）。</summary>
        private void ScanList()
        {
            AddLog("=== 扫描用户设计库 ToolBox（按版本）===");

            ToolboxLibrary.GetInstalledVersions(AddLog);
            _items = ToolboxLibrary.Scan(AddLog);

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (ToolItem t in _items) _list.Items.Add(t.Display, true);
            }
            finally
            {
                _list.EndUpdate();
            }

            if (_items.Count == 0)
            {
                AddLog("未发现任何 ToolBox 目录；请确认 CAXA 3D 已安装并至少打开过一次设计环境");
                Diagnostics.Log("scan: 0 item");
                return;
            }

            var seen = new List<string>();
            foreach (ToolItem t in _items) if (!seen.Contains(t.VersionLabel)) seen.Add(t.VersionLabel);
            AddLog("共 " + _items.Count + " 份，涉及版本：" + string.Join("、", seen.ToArray()) + "（已默认全选）");
            foreach (ToolItem t in _items)
            {
                if (t.Active) AddLog("当前生效：" + t.VersionLabel + " → " + t.DirPath);
            }
            Diagnostics.Log("scan: items=" + _items.Count + " versions=" + string.Join(",", seen.ToArray()));
        }

        /// <summary>取当前勾选项对应的库列表。</summary>
        private List<ToolItem> GetChecked()
        {
            var picked = new List<ToolItem>();
            for (int i = 0; i < _list.Items.Count && i < _items.Count; i++)
            {
                if (_list.GetItemChecked(i)) picked.Add(_items[i]);
            }
            return picked;
        }

        // ---------------------------------------------------------------- 右键打开所在文件夹

        /// <summary>右键列表时先定位到鼠标所指条目；指不到任何条目就取消菜单。</summary>
        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _menuIndex = _list.IndexFromPoint(_list.PointToClient(Cursor.Position));
            if (_menuIndex < 0 || _menuIndex >= _items.Count) { _menuIndex = -1; e.Cancel = true; return; }
            _list.SelectedIndex = _menuIndex;
        }

        /// <summary>打开当前条目的 ToolBox 目录本身（条目就是目录）。</summary>
        private void OnOpenFolderClick(object sender, EventArgs e)
        {
            if (_menuIndex < 0 || _menuIndex >= _items.Count) return;
            ToolItem it = _items[_menuIndex];
            try
            {
                if (string.IsNullOrEmpty(it.DirPath)) { AddLog("该条目没有目录路径，无法打开"); return; }
                if (!Directory.Exists(it.DirPath)) { AddLog("目录不存在：" + it.DirPath); return; }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + it.DirPath + "\"");
                AddLog("已打开所在文件夹：" + it.DirPath);
                Diagnostics.Log("open folder: " + it.DirPath);
            }
            catch (Exception ex)
            {
                AddLog("打开文件夹失败：" + it.DirPath + " → " + ex.Message);
                Diagnostics.Log("open folder failed: " + ex, 5000);
            }
        }

        // ---------------------------------------------------------------- 备份

        private void OnBackupClick(object sender, EventArgs e)
        {
            if (_running) { AddLog("已有流程在运行"); return; }

            if (_items.Count == 0)
            {
                AddLog("请先点「刷新列表」扫描用户设计库");
                return;
            }

            List<ToolItem> picked = GetChecked();
            if (picked.Count == 0)
            {
                AddLog("未勾选任何版本，备份已取消");
                return;
            }

            var versions = new List<string>();
            foreach (ToolItem t in picked) if (!versions.Contains(t.VersionLabel)) versions.Add(t.VersionLabel);
            AddLog("本次备份版本：" + string.Join("、", versions.ToArray()) + "（共 " + picked.Count + " 份）");

            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            string dest = null;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            using (var dlg = new SaveFileDialog
            {
                Filter = "设计库备份包|*.zip",
                FileName = "caxa3dmytoolboxbak-" + stamp + ".zip",
                Title = "选择备份包存放位置",
                InitialDirectory = Directory.Exists(desktop) ? desktop : ""
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) dest = dlg.FileName;
            }

            if (dest == null)
            {
                AddLog("已取消（未选择存放位置）");
                Diagnostics.Log("backup cancelled by user");
                return;
            }

            _running = true;
            _stopRequested = false;
            SetBusy(true);

            try
            {
                AddLog("=== 开始备份 ===");
                if (_stopRequested) throw new OperationStoppedException();

                int n = ToolboxLibrary.Backup(picked, dest, AddLog);
                if (n > 0)
                {
                    AddLog("=== 备份完成：共 " + n + " 个版本 ===");
                }
                else
                {
                    AddLog("=== 备份失败：备份包校验未通过 ===");
                }
            }
            catch (OperationStoppedException)
            {
                AddLog("已停止（备份未完成）");
            }
            catch (Exception ex)
            {
                AddLog("备份失败：" + ex.Message);
                Diagnostics.Log("backup failed: " + ex, 5000);
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
            using (var dlg = new OpenFileDialog
            {
                Filter = "设计库备份包|*.zip",
                Title = "选择本程序生成的备份包"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) zipPath = dlg.FileName;
            }
            if (zipPath == null) { AddLog("已取消（未选择备份包）"); return; }

            List<PackEntry> entries;
            try
            {
                entries = ToolboxLibrary.PeekPackage(zipPath, AddLog);
            }
            catch (Exception ex)
            {
                AddLog("无法读取备份包：" + ex.Message);
                Diagnostics.Log("restore peek failed: " + ex, 5000);
                return;
            }

            List<PackEntry> picked;
            string created = ToolboxLibrary.PeekCreated(zipPath);
            using (var pick = new VersionPickForm(entries, zipPath, created))
            {
                if (pick.ShowDialog(this) != DialogResult.OK) { AddLog("已取消还原"); return; }
                picked = pick.Selected;
            }
            if (picked == null || picked.Count == 0) { AddLog("已取消还原"); return; }

            var versions = new List<string>();
            foreach (PackEntry pe in picked) if (!versions.Contains(pe.VersionLabel)) versions.Add(pe.VersionLabel);

            DialogResult r = MessageBox.Show(this,
                "将用备份包覆盖本机以下版本的用户设计库：\n  " + string.Join("、", versions.ToArray()) + "\n\n" +
                "每个已存在的 ToolBox 目录都会先另存为 .bak-时间戳（同父目录），失败可回滚。\n" +
                "若 CAXA 正在使用设计库，请先关闭相关窗口再还原。\n\n是否继续？",
                "还原确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) { AddLog("已取消还原"); return; }

            _running = true;
            _stopRequested = false;
            SetBusy(true);

            try
            {
                AddLog("=== 开始还原：" + zipPath + " ===");
                List<RestoreOutcome> outcomes = ToolboxLibrary.Restore(zipPath, picked, AddLog);
                if (_stopRequested) throw new OperationStoppedException();

                int ok = 0, skip = 0, fail = 0;
                foreach (RestoreOutcome oc in outcomes)
                {
                    if (oc.Ok) ok++;
                    else if (oc.Skipped) skip++;
                    else fail++;
                }

                AddLog("=== 还原结束：成功 " + ok + " 个版本，跳过 " + skip + " 个，失败 " + fail + " 个 ===");
                if (skip > 0) AddLog("跳过原因见上方逐条日志（通常是本机没装该版本）");
                if (ok > 0) AddLog("提示：还原后请重启 CAXA 3D，让设计环境重新加载设计库");

                ScanList();
            }
            catch (OperationStoppedException)
            {
                AddLog("已停止（还原可能未完成）");
            }
            catch (Exception ex)
            {
                AddLog("还原失败：" + ex.Message);
                Diagnostics.Log("restore failed: " + ex, 5000);
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
            _btnRefresh.Enabled = !busy;
            _btnAll.Enabled = !busy;
            _btnNone.Enabled = !busy;
            _btnStop.Enabled = busy;
        }
    }
}
