using System;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;

namespace caxa3didcskill
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 不再自行注册 Ribbon 按钮——按钮由主工具窗口汇总区动态生成。
    /// </summary>
    public sealed class IdcSkillPlugin : IPlugin
    {
        private IPluginHost _host;
        private MainForm _form;

        public string Id { get { return "caxa3didcskill"; } }

        public string DisplayName { get { return "后台进程清理"; } }

        public PluginCategory Category { get { return PluginCategory.Independent; } }

        /// <summary>Host 加载插件时调用：保存引用并创建隐藏窗口。</summary>
        public void Initialize(IPluginHost host)
        {
            _host = host;
            try
            {
                _form = new MainForm();
            }
            catch (Exception ex)
            {
                host.Log("MainForm init failed: " + ex);
                MessageBox.Show("后台进程清理 初始化失败：" + ex.Message,
                    "caxa3didcskill", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>主工具窗口点击按钮时调用：显示/激活本插件窗口。</summary>
        public void Activate()
        {
            try
            {
                if (_form == null || _form.IsDisposed)
                    _form = new MainForm();

                if (_form.Visible) _form.Activate();
                else { _form.Show(); _form.Activate(); }
            }
            catch (Exception ex)
            {
                if (_host != null) _host.Log("Activate failed: " + ex);
                MessageBox.Show("打开后台进程清理失败：" + ex.Message,
                    "caxa3didcskill", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Host 卸载插件时调用：关闭窗口释放资源。</summary>
        public void Shutdown()
        {
            try
            {
                if (_form != null && !_form.IsDisposed)
                {
                    _form.Close();
                    _form.Dispose();
                }
            }
            catch { }
            _form = null;
            _host = null;
        }
    }
}
