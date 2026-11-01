using System;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;

namespace caxa3dexbtest
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 验证性工具，归类为「测试插件」。
    /// </summary>
    public sealed class ExbTestPlugin : IPlugin
    {
        private IPluginHost _host;
        private MainForm _form;

        public string Id { get { return "caxa3dexbtest"; } }

        public string DisplayName { get { return "图纸环境矩形测试"; } }

        public PluginCategory Category { get { return PluginCategory.Test; } }

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
                MessageBox.Show("图纸环境矩形测试 初始化失败：" + ex.Message,
                    "caxa3dexbtest", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                MessageBox.Show("打开图纸环境矩形测试失败：" + ex.Message,
                    "caxa3dexbtest", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
