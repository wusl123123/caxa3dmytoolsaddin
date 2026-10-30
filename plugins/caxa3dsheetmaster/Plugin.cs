using System;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

namespace caxa3dsheetmaster
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 原独立 Add-in 的功能与窗口保持一致，只是把 Ribbon 按钮换成工具箱里的一个按钮。
    /// </summary>
    public sealed class SheetMasterPlugin : IPlugin
    {
        private IPluginHost _host;
        private ZAddinSite _site;
        private MainForm _form;

        public string Id { get { return "caxa3dsheetmaster"; } }

        public string DisplayName { get { return "折弯管理大师"; } }

        public PluginCategory Category { get { return PluginCategory.BuiltIn; } }

        /// <summary>Host 加载插件时调用：保存站点并预创建隐藏窗口（规则 7#5）。</summary>
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
                // 规则 7#5：窗口在加载阶段一次建好（不 Show），避免点击时首次建窗导致重入
                _form = new MainForm(_site.Application);
            }
            catch (Exception ex)
            {
                host.Log("MainForm init failed: " + ex);
                MessageBox.Show("折弯管理大师 初始化失败：" + ex.Message,
                    "caxa3dsheetmaster", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                MessageBox.Show("打开折弯管理大师失败：" + ex.Message,
                    "caxa3dsheetmaster", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
