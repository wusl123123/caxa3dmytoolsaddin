# caxa3dapigetaddin-里程碑记录表

## 二、里程碑记录表

| 时间 | 内容 |
| --- | --- |
| 2026-10-31 | 迁入集合体：新增 `Plugin.cs`（`ApiGetPlugin : IPlugin`），`MainForm` 构造签名改为 `(IZBaseApp, string pluginDirectory)`，事件订阅随 `Initialize` 立即启动 |
| 2026-10-31 | 修复 `EventSinks.cs`：删除 `[assembly: ComVisible(true)]`，改为类/接口级 `[ComVisible(true)]`，程序集级 `[assembly: ComVisible(false)]` 迁到 `Plugin.cs` |
| 2026-10-31 | `MainForm` 的 CRX 资源查找改为 `IPluginHost.PluginDirectory`（移除 `AppDomain.BaseDirectory` 向上扫描） |
| 2026-10-31 | 两处提权等待由 `Process.WaitForExit(60000)` 改为 `DoEvents` 轮询（STA 主线程禁阻塞） |
| 2026-10-31 | 删除无用字段 `_lastDocType`，编译 0 警告 0 错误；`build.ps1` 增加 `Extra = @('.crx')` |

