# caxa3dapigetaddin-项目定位与需求

> 原总结标题：caxa3dapigetaddin（CAXA 3D 操作 API 抓取器）项目总结


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

## 四、功能行为

1. `Initialize` 时即创建（隐藏）`MainForm` 并启动事件订阅与轮询定时器；`Activate` 只负责 `Show()+Activate()`；`Shutdown` 关闭窗口并释放全部 `Advise`。
2. 界面按钮与原独立版一致：`窗口前置`（默认开）、`清空日志`、`保存日志`、`暂停记录`、`注册CRX模块`、`卸载CRX模块`；无「刷新连接」按钮。
3. 日志：`%TEMP%\caxa3dapigetaddin_trace.log`，每次启动清空；界面列表与文件日志分离，界面「暂停记录」只影响 UI。
4. CRX 注册 = 拷贝 `.crx` 到 `CAXADraft\Bin` + 在 `Modules\CxAutorun.cx` 追加登记项；需要管理员权限时转 PowerShell `runas` 提权，等待用 `DoEvents` 轮询（最长 60 秒），不阻塞 CAXA UI 线程。

