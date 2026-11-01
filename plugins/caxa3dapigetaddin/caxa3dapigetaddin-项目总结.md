# caxa3dapigetaddin（CAXA 3D 操作 API 抓取器）项目总结

> 来源：从独立 Add-in `C:\code\ICAPI\caxa3dapigetaddin` 迁入集合体，作为第 9 个子插件，归属主工具箱「3. 测试插件」列。
> 抓取逻辑与界面保持原样，仅做「独立 Add-in → 集合体子插件」的形态改造。

## 一、版本与工程

| 项目 | 值 |
| --- | --- |
| 插件 Id | `caxa3dapigetaddin` |
| 界面名称 | 操作 API 抓取器 |
| 分类 | `PluginCategory.Test`（主工具箱「3. 测试插件」） |
| 工程 | `plugins\caxa3dapigetaddin\caxa3dapigetaddin.csproj` |
| 目标框架 / 平台 | net48 / x64（`Library`） |
| 强名称 | `Lib\ICApiAddin.snk`，token `56647e1eee90f6d4` |
| 依赖 | `..\..\src\Contracts\Contracts.csproj`、`Lib\interop.ICApiIronCAD.dll`（`EmbedInteropTypes=True`） |
| 已删除 | `Addin.cs`、Ribbon/ControlBar 注册、`Icons`、`ConvertImage.cs`、`stdole` 引用、自带 `install/uninstall` 脚本、mt/sn 后处理 |
| 附属模块 | `caxa3dapicapture-crx\bin\caxa3dapicapture.crx`（135680 B，图纸环境事件桥） |

`Lib\interop.ICApiIronCAD.dll` 与仓库 `Lib\` 同一份（md5 `29a23396bfbe966ba92cb4598f8c38f3`），因此与 Host 及兄弟插件共用同一 interop 类型。

## 二、里程碑记录表

| 时间 | 内容 |
| --- | --- |
| 2026-10-31 | 迁入集合体：新增 `Plugin.cs`（`ApiGetPlugin : IPlugin`），`MainForm` 构造签名改为 `(IZBaseApp, string pluginDirectory)`，事件订阅随 `Initialize` 立即启动 |
| 2026-10-31 | 修复 `EventSinks.cs`：删除 `[assembly: ComVisible(true)]`，改为类/接口级 `[ComVisible(true)]`，程序集级 `[assembly: ComVisible(false)]` 迁到 `Plugin.cs` |
| 2026-10-31 | `MainForm` 的 CRX 资源查找改为 `IPluginHost.PluginDirectory`（移除 `AppDomain.BaseDirectory` 向上扫描） |
| 2026-10-31 | 两处提权等待由 `Process.WaitForExit(60000)` 改为 `DoEvents` 轮询（STA 主线程禁阻塞） |
| 2026-10-31 | 删除无用字段 `_lastDocType`，编译 0 警告 0 错误；`build.ps1` 增加 `Extra = @('.crx')` |

## 三、抓取能力（事件源）

`EventMonitor` 按 DIID 手动 `IConnectionPoint.Advise`，DIID 与原独立版完全一致（取自 `ICApiIronCAD.tlb`）：

| 事件源 | DIID |
| --- | --- |
| `_IZApplicationEvents`（挂在 `IZBaseApp`） | `BF00132F-57F2-458B-A734-D9E0C883C525` |
| `_IZDocEvents` | `0E18FE8F-174F-4D46-AB72-C8A65D783FE4` |
| `OperationEvents` | `2D640B7A-511A-46CD-8050-958AEA611416` |
| `HandleEvents` | `32CCD216-A79E-4E32-BB57-5E914B57686D` |
| `_IZCatalogEvents`（场景 / 图纸目录） | `40A3FE22-B015-421B-AF36-85EE3C9D1BAF` |
| `DrawEvents` | `10388D65-F5B8-484C-8A3B-AE3BBCCEA5BE` |
| `SmartAssemblyEx` | `72D765CB-CC07-4A70-8427-FE3E72B85BAE` |
| `MechanismMode` | `BA24D8C4-DD72-4219-8926-D7F65D8CE33A` |
| `_IZDrawingDocEvents` | `5EF3E988-1529-4AF2-B0BD-F87407F28C93` |

图纸环境没有实体级事件，`EventMonitor` 每 2 秒轮询 `GetDrawingNotifyCounter` / 视图数 / 图纸数快照做增量判定（首次进入只记基线），并 tail `%TEMP%\caxa3d_crx_events.log` 取 CRX 侧事件。

## 四、功能行为

1. `Initialize` 时即创建（隐藏）`MainForm` 并启动事件订阅与轮询定时器；`Activate` 只负责 `Show()+Activate()`；`Shutdown` 关闭窗口并释放全部 `Advise`。
2. 界面按钮与原独立版一致：`窗口前置`（默认开）、`清空日志`、`保存日志`、`暂停记录`、`注册CRX模块`、`卸载CRX模块`；无「刷新连接」按钮。
3. 日志：`%TEMP%\caxa3dapigetaddin_trace.log`，每次启动清空；界面列表与文件日志分离，界面「暂停记录」只影响 UI。
4. CRX 注册 = 拷贝 `.crx` 到 `CAXADraft\Bin` + 在 `Modules\CxAutorun.cx` 追加登记项；需要管理员权限时转 PowerShell `runas` 提权，等待用 `DoEvents` 轮询（最长 60 秒），不阻塞 CAXA UI 线程。

## 五、界面结构

- 顶部一行操作按钮 + 「暂停记录」勾选；第二行「连接状态：已连接（进程内）」与「设计环境文件：…」；主体为事件列表 `ListView`；底部汇总 `已记录 N 条事件`。
- 标题：`CAXA 3D 操作 API 抓取器`；窗口默认 `TopMost=true`。

## 六、踩坑与结论

1. **事件 sink 必须类级 `[ComVisible(true)]`。** 集合体要求插件程序集级 `ComVisible(false)`，但 `Advise` 需要 CCW；只在 sink 类/接口上开可见性，不要恢复程序集级 true，否则 Host 清单可能被污染。
2. **CRX 路径不能靠 `AppDomain.BaseDirectory`。** 插件运行在 CAXA bin 下，该目录没有 `.crx`；必须用 `IPluginHost.PluginDirectory`，否则「注册CRX模块」永远找不到文件。
3. **提权等待不能 `WaitForExit`。** 在 STA UI 线程阻塞会让 CAXA 假死；改为 `DoEvents + Sleep(100)` 轮询。
4. **`build.ps1` 需带 `Extra = @('.crx')`。** 否则只暂存 DLL，运行期缺 CRX，图纸环境事件采集静默缺失。
5. **插件 DLL 不内嵌清单。** 迁入后删除原 mt/sn 后处理，`mt.exe -inputresource:...#2` 读不到 manifest 才是正确状态。

## 七、未验证项（下次实测必查）

1. 真机：主工具箱「3. 测试插件」列出现「操作 API 抓取器」，单击能打开且「窗口前置」可用。
2. 真机：`OperationEvents / HandleEvents / DrawEvents / 智能装配 / 机构` 五类事件能持续产生记录（这是迁入进程内的根本目的）。
3. 真机：点击「注册CRX模块」后 `.crx` 落到 `CAXADraft\Bin`、`CxAutorun.cx` 有登记行，重启 CAXA 后 `%TEMP%\caxa3d_crx_events.log` 有内容。
4. 真机：「卸载CRX模块」后 CAXA 3D 仍能正常启动（历史上卸载流程曾导致无法启动，需重点回归）。
5. `%TEMP%\caxa3dapigetaddin_trace.log` 随 Host 卸载/重装正确轮转，无句柄残留。
