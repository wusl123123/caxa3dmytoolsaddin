using System;
using Caxa3dMyTools.Contracts;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    /// <summary>
    /// 「1. 常用插件」列：按钮工具（原独立 Add-in caxa3dButton 移植为子插件）。
    ///
    /// 移植说明（2026-11-09）：
    ///   * 原 Addin.cs 的 COM 入口（IZAddinServer / CreateCommandHandler / AddControlBar /
    ///     AddButton）整条删除 —— 集合体只有 Host 注册功能区按钮，子插件不再各自注册 Ribbon；
    ///   * InitSelf 的三件事收敛为两件：缓存站点 + 主线程预创建 MainForm（不弹框、不探测）；
    ///   * safe 开关（caxa3dButton.safe）随 COM 入口一起取消，出问题用整个集合体的基线差分定位；
    ///   * MainForm / 各工具类源码原样随迁，图纸环境预热载体 caxa3d图纸环境预热.exb
    ///     由 csproj 链接进输出目录、build.ps1 Extra 带上（FindWarmupExb 从程序集目录逐级向上找）。
    /// </summary>
    public sealed class ButtonToolPlugin : IPlugin
    {
        private IPluginHost _host;
        private MainForm _form;   // 主线程预创建；点击时只显示/激活

        public string Id { get { return "caxa3dButton"; } }

        public string DisplayName { get { return "按钮工具"; } }

        /// <summary>日常业务工具，归入「1. 常用插件」列。</summary>
        public PluginCategory Category { get { return PluginCategory.BuiltIn; } }

        /// <summary>Host 加载时缓存站点并预创建窗口（与原 InitSelf 纪律一致，不碰环境 COM 探测）。</summary>
        public void Initialize(IPluginHost host)
        {
            _host = host;
            Diagnostics.Reset();

            ZAddinSite site = GetSite();
            if (site == null)
            {
                Log("site or Application is null, 按钮工具不可用");
                return;
            }

            try
            {
                _form = new MainForm(site.Application);
            }
            catch (Exception ex)
            {
                Log("MainForm init failed: " + ex);
            }
        }

        /// <summary>工具箱点击：窗口已建则显示/激活，未建成（或已释放）时现场重建。</summary>
        public void Activate()
        {
            if (_form == null || _form.IsDisposed)
            {
                ZAddinSite site = GetSite();
                if (site == null)
                    throw new InvalidOperationException("无有效的 Add-in 站点，无法打开按钮工具");
                _form = new MainForm(site.Application);
            }

            if (_form.Visible) _form.Activate();
            else { _form.Show(); _form.Activate(); }
        }

        /// <summary>Host 卸载时关闭窗口并解除引用。</summary>
        public void Shutdown()
        {
            try
            {
                if (_form != null && !_form.IsDisposed) { _form.Close(); _form.Dispose(); }
            }
            catch { }
            _form = null;
            _host = null;
        }

        // ================================================================ 内部实现

        /// <summary>Host 与插件两侧内嵌 interop 按 TypeIdentifier 等价，可直接转换（见 Host PluginHost 注释）。</summary>
        private ZAddinSite GetSite()
        {
            if (_host == null) return null;
            ZAddinSite site = _host.AddinSite as ZAddinSite;
            if (site == null || site.Application == null) return null;
            return site;
        }

        private void Log(string message)
        {
            if (_host != null) _host.Log(message);
        }
    }
}
