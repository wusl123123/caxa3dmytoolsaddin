// 日志窗口：由主窗体「日志」按钮拉起，显示运行日志，支持保存为文本文件与清空。
// Win2000 极简风：宋体 9F、系统色、纯汉字按钮、固定对话框（不最大化/最小化）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;   // ChordRescue 唯一实现（规则第 37 节）

namespace caxa3dBatchMaster
{
    /// <summary>独立日志窗口。getLines 提供当前日志行；onClear 清空底层缓冲。</summary>
    internal sealed class LogForm : Form
    {
        // ↓↓↓ 键盘归属抢救：日志框是只读 TextBox，Ctrl+A/Ctrl+C 仍要能用（Copy 走托管路径；Cut/Paste 由 ChordRescue 记「只读框，不执行」）。
        // 机理/开关/取证见 ChordRescue，规则第 37 节
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); ChordRescue.Install(this, msg => Diagnostics.Trace(msg)); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); ChordRescue.Uninstall(this, "窗口失活"); }
        protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); ChordRescue.Uninstall(this, "窗口关闭"); }
        protected override void WndProc(ref Message m) { if (ChordRescue.Route(this, ref m)) return; base.WndProc(ref m); }
        // ↑↑↑

        private readonly TextBox _txt = new TextBox();
        private readonly Button _btnSave = new Button();
        private readonly Button _btnClear = new Button();
        private readonly Func<IEnumerable<string>> _getLines;
        private readonly Action _onClear;

        public LogForm(Func<IEnumerable<string>> getLines, Action onClear)
        {
            _getLines = getLines ?? (() => new string[0]);
            _onClear = onClear ?? (() => { });

            Font = new Font("宋体", 9F);
            Text = "运行日志";
            ClientSize = new Size(720, 460);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            _txt.SetBounds(12, 12, 696, 392);
            _txt.Multiline = true;
            _txt.ScrollBars = ScrollBars.Both;
            _txt.ReadOnly = true;
            _txt.Font = new Font("Consolas", 9F);
            _txt.WordWrap = false;

            _btnSave.SetBounds(540, 416, 80, 28);
            _btnSave.Text = "保存";
            _btnSave.Click += BtnSave_Click;

            _btnClear.SetBounds(628, 416, 90, 28);
            _btnClear.Text = "清空日志";
            _btnClear.Click += BtnClear_Click;

            Controls.AddRange(new Control[] { _txt, _btnSave, _btnClear });

            RefreshLog();
        }

        /// <summary>刷新显示（线程安全：跨线程调用时封送回 UI 线程）。</summary>
        public void RefreshLog()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshLog)); return; }
            _txt.Text = string.Join("\r\n", _getLines());
            _txt.SelectionStart = _txt.Text.Length;
            _txt.ScrollToCaret();
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            // 用户 11-07 指令 4：保存的日志文件名带时间戳（年月日_小时分钟秒），避免多次保存同名覆盖
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            using var dlg = new SaveFileDialog
            {
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = "txt",
                FileName = "caxa3dBatchMaster_日志_" + stamp + ".txt"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllLines(dlg.FileName, _getLines());
                MessageBox.Show(this, "日志已保存：" + dlg.FileName, "日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "日志", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            _onClear();
            RefreshLog();
        }
    }
}
