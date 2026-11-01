using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

// 集合体铁律：插件程序集默认不可见。
// 事件接收器类/接口在 EventSinks.cs 内显式标 ComVisible(true)，
// 否则 IConnectionPoint.Advise 拿不到 CCW，事件全部挂不上。
[assembly: ComVisible(false)]

namespace caxa3dapigetaddin
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 原独立的 caxa3dapigetaddin Add-in（Ribbon 按钮 + 自注册）改为集合体子插件，
    /// 抓取逻辑与界面保持不变；事件订阅随加载立即开始。
    /// </summary>
    public sealed class ApiGetPlugin : IPlugin
    {
        private IPluginHost _host;
        private ZAddinSite _site;
        private MainForm _form;

        public string Id { get { return "caxa3dapigetaddin"; } }

        public string DisplayName { get { return "操作 API 抓取器"; } }

        public PluginCategory Category { get { return PluginCategory.Test; } }

        /// <summary>Host 加载插件时调用：保存站点并创建隐藏窗口（抓取立即启动）。</summary>
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
                _form = new MainForm(_site.Application, host.PluginDirectory);
            }
            catch (Exception ex)
            {
                host.Log("MainForm init failed: " + ex);
                MessageBox.Show("操作 API 抓取器 初始化失败：" + ex.Message,
                    "操作 API 抓取器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>主工具窗口点击单元格时调用：显示/激活本插件窗口。</summary>
        public void Activate()
        {
            try
            {
                if (_site == null)
                {
                    MessageBox.Show("CAXA 站点不可用，无法打开抓取窗口。",
                        "操作 API 抓取器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (_form == null || _form.IsDisposed)
                    _form = new MainForm(_site.Application, _host.PluginDirectory);

                if (_form.Visible) _form.Activate();
                else { _form.Show(); _form.Activate(); }
            }
            catch (Exception ex)
            {
                if (_host != null) _host.Log("Activate failed: " + ex);
                MessageBox.Show("打开操作 API 抓取器失败：" + ex.Message,
                    "操作 API 抓取器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Host 卸载插件时调用：关闭窗口释放事件订阅。</summary>
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
