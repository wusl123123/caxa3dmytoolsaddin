using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

// 铁律 5/10：子插件不得残留 ComVisible(true)，否则会污染 CAXA 的内嵌 COM 清单
[assembly: ComVisible(false)]

namespace caxa3dCXDFTViewUpdate
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 业务逻辑与原独立 Add-in 完全一致，只是把功能区按钮换成工具箱「1. 常用插件」列的一行。
    /// </summary>
    public sealed class ViewUpdatePlugin : IPlugin
    {
        private IPluginHost _host;
        private ZAddinSite _site;
        private MainForm _form;

        public string Id { get { return "caxa3dCXDFTViewUpdate"; } }

        public string DisplayName { get { return "刷新图纸视图"; } }

        public PluginCategory Category { get { return PluginCategory.BuiltIn; } }

        /// <summary>Host 加载插件时调用：保存站点并预创建隐藏窗口。</summary>
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
                // 铁律：句柄必须在加载阶段预创建，避免推迟到按钮点击时与 CAXA 主线程互等
                _form = new MainForm(_site.Application);
            }
            catch (Exception ex)
            {
                host.Log("MainForm init failed: " + ex);
                MessageBox.Show("刷新图纸视图 初始化失败：" + ex.Message,
                    "刷新图纸视图", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>主工具窗口点击本插件行时调用：显示/激活批处理窗口。</summary>
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
                MessageBox.Show("打开刷新图纸视图窗口失败：" + ex.Message,
                    "刷新图纸视图", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Host 卸载插件时调用：关闭窗口释放资源。</summary>
        public void Shutdown()
        {
            try
            {
                if (_form != null && !_form.IsDisposed)
                {
                    ForegroundGuard.Enabled = false;
                    PopupAutoYes.Enabled = false;
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
