# Caxa3dcudeaddin-项目定位与需求

> 原总结标题：Caxa3dcudeaddin 项目总结


> 定位：CAXA 3D 进程内「立方体工具」——按输入的长/宽/高，在设计环境原点 (0,0,0) 生成块体。
> 形态：集合体子插件（2026-10-27 起），入口 `CubePlugin : IPlugin`，工具箱显示名「立方体工具」。
> 技术栈：C# / .NET Framework 4.8 / WinForms / x64 / ICAPI vtable 直调。

## 一、功能与入口

- 界面：长(X) / 宽(Y) / 高(Z)（mm，默认 10×10×10）、「建立立方体」、日志区（清空/复制）、状态行。
- 尺寸记忆：`%LocalAppData%\Caxa3dcudeaddin\last-dims.txt`，启动回填、关闭保存。
- 日志：`%TEMP%\Caxa3dcudeaddin_trace.log`（静态构造时清空），同时转发到界面；兜底子进程结果写 `%TEMP%\CaxaCreateCube_result.log`。
- 集合体形态（2026-10-27）：
  - `Initialize(IPluginHost)`：保存 `AddinSite`，预创建隐藏 `MainForm`；
  - `Activate()`：主工具窗口点击时 `Show()` + `Activate()`；
  - `Shutdown()`：关闭并释放窗口；
  - 不注册 Ribbon/ControlBar，功能区只保留 Host 的「我的工具箱」（集合体铁律 1/4）。

