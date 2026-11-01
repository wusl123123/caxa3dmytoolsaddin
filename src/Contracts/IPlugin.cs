using System;

namespace Caxa3dMyTools.Contracts
{
    /// <summary>
    /// 插件归类，决定主工具窗口表格把插件放在哪一列。
    /// </summary>
    public enum PluginCategory
    {
        /// <summary>常用插件（默认归类）：日常业务工具。</summary>
        BuiltIn = 0,

        /// <summary>独立插件：独立 EXE/外部进程方式运行、不依赖站点的工具。</summary>
        Independent = 1,

        /// <summary>测试插件：验证性、诊断性工具。</summary>
        Test = 2
    }

    /// <summary>
    /// 子插件统一契约。
    /// Host 只注册一个「我的工具箱」功能区按钮，弹出主工具窗口；
    /// 主工具窗口内动态汇总所有子插件按钮，点击按钮时调用 Activate()。
    /// 插件不再各自注册 Ribbon 按钮。
    /// 本接口不引用 CAXA interop 类型，保证 Host 与插件之间只有一个公共耦合点。
    /// </summary>
    public interface IPlugin
    {
        /// <summary>插件唯一标识（用于日志与插件目录定位）。</summary>
        string Id { get; }

        /// <summary>插件显示名（主工具窗口汇总区按钮标题）。</summary>
        string DisplayName { get; }

        /// <summary>插件归类：常用（BuiltIn）、独立（Independent）或测试（Test）。</summary>
        PluginCategory Category { get; }

        /// <summary>
        /// CAXA 加载 Host 时调用。
        /// 插件在此保存 host 引用、创建隐藏窗口等；不注册功能区按钮。
        /// </summary>
        void Initialize(IPluginHost host);

        /// <summary>
        /// 用户在主工具窗口点击本插件按钮时调用。
        /// 插件在此显示/激活自己的窗口（Show + Activate）。
        /// </summary>
        void Activate();

        /// <summary>
        /// CAXA 卸载 Host 时调用。
        /// 插件在此关闭窗口与释放资源。
        /// </summary>
        void Shutdown();
    }

    /// <summary>
    /// Host 提供给插件的运行环境。
    /// </summary>
    public interface IPluginHost
    {
        /// <summary>
        /// CAXA Add-in 站点对象，运行期实际类型为 interop.ICApiIronCAD.ZAddinSite。
        /// 插件以 <c>host.AddinSite as ZAddinSite</c> 取得（两侧内嵌 interop 类型按
        /// TypeIdentifier 等价，可直接转换）。
        /// </summary>
        object AddinSite { get; }

        /// <summary>当前插件所在目录（插件自带的表格/脚本等资源应从这里读取）。</summary>
        string PluginDirectory { get; }

        /// <summary>写入 Host 统一日志（会自动附加插件名）。</summary>
        void Log(string message);
    }
}
