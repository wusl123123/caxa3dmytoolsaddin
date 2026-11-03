using System;
using System.Windows.Forms;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

namespace caxa3dsetbackup
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 原独立 Add-in「设置备份」的内核原样移植，只把 Ribbon 按钮换成工具箱「3. 系统工具」列的一个单元格。
    /// 仅移植内置插件（DLL 形态）：不注册 COM、不注册 Ribbon/ControlBar、不内嵌 SxS 清单；
    /// 原 exe\ 独立 EXE 子工程不随本插件移植。
    /// </summary>
    public sealed class SetBackupPlugin : IPlugin
    {
        private IPluginHost _host;
        private ZAddinSite _site;
        private MainForm _form;

        public string Id { get { return "caxa3dsetbackup"; } }

        public string DisplayName { get { return "设置备份"; } }

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
                // 铁律：句柄必须在加载阶段预创建，避免推迟到按钮点击时与 CAXA 主线程互等
                _form = new MainForm(_site.Application);
            }
            catch (Exception ex)
            {
                host.Log("MainForm init failed: " + ex);
                MessageBox.Show("设置备份 初始化失败：" + ex.Message,
                    "caxa3dsetbackup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>主工具窗口点击单元格时调用：显示/激活本插件窗口。</summary>
        public void Activate()
        {
            try
            {
                if (_site == null)
                    _site = _host != null ? _host.AddinSite as ZAddinSite : null;
                if (_site == null)
                    throw new InvalidOperationException("AddinSite 不可用");

                if (_form == null || _form.IsDisposed)
                    _form = new MainForm(_site.Application);

                if (_form.Visible) _form.Activate();
                else { _form.Show(); _form.Activate(); }
            }
            catch (Exception ex)
            {
                if (_host != null) _host.Log("Activate failed: " + ex);
                MessageBox.Show("打开设置备份失败：" + ex.Message,
                    "caxa3dsetbackup", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
