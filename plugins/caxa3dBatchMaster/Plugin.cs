using System;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 原独立 Add-in（自带功能区按钮 + 控制栏）迁入集合体后，入口只剩「工具箱里的一个按钮」；
    /// 窗口、业务逻辑与资源路径全部保持原样，不再注册任何 Ribbon / ControlBar / COM。
    /// </summary>
    public sealed class BatchMasterPlugin : IPlugin
    {
        private IPluginHost _host;
        private ZAddinSite _site;
        private MainForm _form;

        public string Id { get { return "caxa3dBatchMaster"; } }

        /// <summary>工具箱按钮标题（与主窗体标题「CAXA3D批量大师」一致的口径）。</summary>
        public string DisplayName { get { return "批量大师"; } }

        /// <summary>用户指定：放入「1. 常用插件」列。</summary>
        public PluginCategory Category { get { return PluginCategory.BuiltIn; } }

        /// <summary>Host 加载插件时调用：保存站点并按铁律在加载阶段预创建窗口句柄。</summary>
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
                Diagnostics.Trace("plugin: MainForm init failed: " + ex);
                MessageBox.Show("批量大师初始化失败：" + ex.Message,
                    "caxa3dBatchMaster", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                Diagnostics.Trace("plugin: Activate failed: " + ex);
                MessageBox.Show("打开批量大师失败：" + ex.Message,
                    "caxa3dBatchMaster", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
