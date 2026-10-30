using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using interop.ICApiIronCAD;

// 程序集级 COM 可见：CAXA 通过免注册 COM（clrClass）创建本 Add-in
[assembly: System.Runtime.InteropServices.ComVisible(true)]

namespace caxa3dmytoolsaddin
{
    /// <summary>
    /// CAXA 3D 内置 Add-in 主程序（插件集合体）。
    /// 本程序只注册一个功能区按钮「我的工具箱」，弹出主工具窗口；
    /// 子插件按钮全部汇总到主窗口内，不再各自注册 Ribbon 按钮。
    /// </summary>
    [Guid("6F1A9C24-3B57-4E8D-9A02-5C7D1E8B4F36"),
     ClassInterface(ClassInterfaceType.None),
     ProgId("caxa3dmytoolsaddin.Addin")]
    public class Addin : IZAddinServer
    {
        private ZAddinSite _site;
        private PluginManager _manager;
        private ZCommandHandler _toolboxButton;
        private MainToolboxForm _toolboxForm;

        /// <summary>CAXA 加载 Add-in 时调用。</summary>
        public void InitSelf(ZAddinSite piAddinSite)
        {
            _site = piAddinSite;
            if (_site == null) return;

            Diagnostics.Log("InitSelf: host loaded by CAXA");

            try
            {
                _manager = new PluginManager(_site);
                int count = _manager.LoadAll();
                Diagnostics.Log("子插件加载完成：" + count + " 个");
            }
            catch (Exception ex)
            {
                Diagnostics.Log("LoadAll failed: " + ex);
            }

            try
            {
                CreateToolboxButton();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("CreateToolboxButton failed: " + ex);
                MessageBox.Show("我的工具箱 注册功能区按钮失败：\n" + ex.Message,
                    "caxa3dmytoolsaddin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>CAXA 卸载 Add-in 时调用。</summary>
        public void DeInitSelf()
        {
            try
            {
                if (_toolboxForm != null && !_toolboxForm.IsDisposed)
                {
                    _toolboxForm.Close();
                    _toolboxForm.Dispose();
                }
            }
            catch { }
            _toolboxForm = null;

            try
            {
                if (_manager != null) _manager.ShutdownAll();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("ShutdownAll failed: " + ex.Message);
            }

            _manager = null;
            _toolboxButton = null;
            _site = null;
            Diagnostics.Log("DeInitSelf: host unloaded");
        }

        /// <summary>注册单一「我的工具箱」按钮（ControlBar + RibbonBar，仅文字无图标）。</summary>
        private void CreateToolboxButton()
        {
            IZBaseApp app = _site.Application;
            if (app == null) return;

            _toolboxButton = _site.CreateCommandHandler(
                "caxa3dmytoolsaddin",
                "我的工具箱",
                "我的工具箱",
                "打开插件集合窗口，里面汇总了所有子插件功能",
                null, null);

            _toolboxButton.Enabled = true;
            _toolboxButton.OnClick += new _IZCommandEvents_OnClickEventHandler(Toolbox_OnClick);
            _toolboxButton.OnUpdate += new _IZCommandEvents_OnUpdateEventHandler(Toolbox_OnUpdate);

            ZEnvironmentMgr envMgr = app.EnvironmentMgr;
            IZEnvironment env = envMgr.get_Environment(eZEnvType.Z_ENV_SCENE);
            ZControlBar bar = env.AddControlBar(_site, "我的工具箱");
            bar.Controls.Add(ezControlType.Z_CONTROL_BUTTON, _toolboxButton.ControlDescriptor, null);

            ZRibbonBar ribbon = env.GetRibbonBar(eZRibbonBarType.Z_RIBBONBAR);
            ribbon.AddButton(_toolboxButton.ControlDescriptor);
        }

        private void Toolbox_OnUpdate()
        {
            if (_toolboxButton != null) _toolboxButton.Enabled = true;
        }

        private void Toolbox_OnClick()
        {
            try
            {
                Diagnostics.Log("Toolbox_OnClick: enter");
                if (_toolboxForm == null || _toolboxForm.IsDisposed)
                    _toolboxForm = new MainToolboxForm(_manager);

                if (_toolboxForm.Visible) _toolboxForm.Activate();
                else { _toolboxForm.Show(); _toolboxForm.Activate(); }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Toolbox_OnClick failed: " + ex);
                MessageBox.Show("打开我的工具箱失败：" + ex.Message,
                    "我的工具箱", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
