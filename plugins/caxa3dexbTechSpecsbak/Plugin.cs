using System;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

namespace caxa3dexbTechSpecsbak
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序反射加载，落在主工具箱「3. 系统工具」列。
    /// 原独立 Add-in 的窗口与备份/还原逻辑一字不改地带过来，只把 Ribbon 按钮 + 控制栏换成工具箱单元格；
    /// 本 DLL 不注册 COM、不内嵌 SxS 清单（集合体只有 Host 一个入口）。
    /// </summary>
    public sealed class TechSpecsPlugin : IPlugin
    {
        private IPluginHost _host;
        private ZAddinSite _site;
        private MainForm _form;

        public string Id { get { return "caxa3dexbTechSpecsbak"; } }

        public string DisplayName { get { return "工程图技术要求备份"; } }

        public PluginCategory Category { get { return PluginCategory.SystemTool; } }

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
                // 铁律：句柄必须在加载阶段预创建（MainForm 构造函数内部已 CreateHandle），
                // 推迟到点击时建窗口会与 CAXA 主线程互等死锁。
                _form = new MainForm(_site.Application);
            }
            catch (Exception ex)
            {
                host.Log("MainForm init failed: " + ex);
                MessageBox.Show("工程图技术要求备份 初始化失败：" + ex.Message,
                    "caxa3dexbTechSpecsbak", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>主工具窗口点击单元格时调用：显示/激活本插件窗口。</summary>
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
                MessageBox.Show("打开工程图技术要求备份失败：" + ex.Message,
                    "caxa3dexbTechSpecsbak", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
