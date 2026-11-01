using System;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

namespace Caxa3dcudeaddin
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 不再自行注册 Ribbon 按钮——按钮由主工具窗口汇总区动态生成。
    /// </summary>
    public sealed class CubePlugin : IPlugin
    {
        private IPluginHost _host;
        private ZAddinSite _site;
        private MainForm _form;

        public string Id { get { return "Caxa3dcudeaddin"; } }

        public string DisplayName { get { return "立方体工具"; } }

        public PluginCategory Category { get { return PluginCategory.Test; } }

        /// <summary>Host 加载插件时调用：保存引用并创建隐藏窗口。</summary>
        public void Initialize(IPluginHost host)
        {
            _host = host;
            _site = host.AddinSite as ZAddinSite;
            if (_site == null)
            {
                host.Log("AddinSite 类型不匹配，插件未初始化");
                return;
            }

            try
            {
                _form = new MainForm(_site.Application);
            }
            catch (Exception ex)
            {
                host.Log("MainForm init failed: " + ex);
                MessageBox.Show("立方体工具初始化失败：" + ex.Message,
                    "Caxa3dcudeaddin", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>主工具窗口点击按钮时调用：显示/激活本插件窗口。</summary>
        public void Activate()
        {
            try
            {
                if (_form == null || _form.IsDisposed)
                    _form = new MainForm(_site.Application);

                if (_form.Visible) _form.Activate();
                else { _form.Show(); _form.Activate(); }
            }
            catch (Exception ex)
            {
                if (_host != null) _host.Log("Activate failed: " + ex);
                MessageBox.Show("打开立方体工具失败：" + ex.Message,
                    "Caxa3dcudeaddin", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            _site = null;
            _host = null;
        }
    }
}
