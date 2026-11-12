using System;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

namespace caxa3dAttributeManageraddin
{
    /// <summary>
    /// 子插件入口：由 caxa3dmytoolsaddin 主程序通过反射加载。
    /// 不再自行注册 Ribbon 按钮——按钮由主工具窗口汇总区动态生成。
    ///
    /// v1.1 结构对齐（2026-11-12，对齐基准子插件 caxa3dBatchMaster / caxa3dButton 颗粒度）：
    ///   * 原 Addin.cs 改名 Plugin.cs（与基准子插件同名同位）；
    ///   * Initialize / Activate 里的 MessageBox.Show 全部移除，失败只写 Host 日志
    ///     ——「InitSelf 期不弹 MessageBox」纪律对子插件同样适用（模态框在启动期占住宿主消息泵）；
    ///   * 空的 Icons\ 目录与 csproj 里已无人引用的 stdole 引用一并删除
    ///     （图标参数固定 null, null，走 CAXA 内置图标，规则铁律 4）。
    /// </summary>
    public sealed class AttributeManagerPlugin : IPlugin
    {
        private IPluginHost _host;
        private ZAddinSite _site;
        private MainForm _form;

        public string Id { get { return "caxa3dAttributeManageraddin"; } }

        public string DisplayName { get { return "自定义属性管理器"; } }

        public PluginCategory Category { get { return PluginCategory.BuiltIn; } }

        /// <summary>Host 加载插件时调用：保存引用并创建隐藏窗口（失败只记日志，不弹框）。</summary>
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
                _form = null;
                host.Log("MainForm init failed: " + ex);
            }
        }

        /// <summary>主工具窗口点击按钮时调用：显示/激活本插件窗口（失败只记日志，不弹框）。</summary>
        public void Activate()
        {
            try
            {
                if (_form == null || _form.IsDisposed)
                {
                    if (_site == null)
                        throw new InvalidOperationException("无有效的 Add-in 站点，无法打开自定义属性管理器");
                    _form = new MainForm(_site.Application, _host.PluginDirectory);
                }

                if (_form.Visible) _form.Activate();
                else { _form.Show(); _form.Activate(); }
            }
            catch (Exception ex)
            {
                if (_host != null) _host.Log("Activate failed: " + ex);
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
