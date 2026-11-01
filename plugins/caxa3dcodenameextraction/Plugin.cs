using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

// 铁律 5/10：子插件不得残留 ComVisible(true)，否则会污染 CAXA 的内嵌 COM 清单
[assembly: ComVisible(false)]

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 业务逻辑与原独立 Add-in 完全一致，只是把入口从 Addin.cs 换成 IPlugin。
    /// </summary>
    public sealed class NameExtractionPlugin : IPlugin
    {
        private IPluginHost _host;
        private ZAddinSite _site;
        private MainForm _form;

        public string Id { get { return "caxa3dcodenameextraction"; } }

        public string DisplayName { get { return "带图改名及图号分离"; } }

        public PluginCategory Category { get { return PluginCategory.BuiltIn; } }

        /// <summary>Host 加载插件时调用：保存引用并创建窗口（不注册功能区按钮）。</summary>
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
                MessageBox.Show("带图改名及图号分离 初始化失败：" + ex.Message,
                    "带图改名及图号分离", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                MessageBox.Show("打开带图改名及图号分离失败：" + ex.Message,
                    "带图改名及图号分离", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
