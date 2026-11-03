# caxa3dmytoolsaddin-踩坑与教训

## 四、集合体铁律（1–14）

1. **Host 拥有唯一 Ribbon 按钮。** 子插件不得自行注册 Ribbon/ControlBar，否则破坏「功能区只有一个我的工具箱」。
2. **子插件窗口默认 `TopMost=true`。** 用户在 CAXA 内需边选零部件边看窗口；按钮提供开关。
3. **不显示连接状态。** 主窗口与子插件窗口都不显示 CAXA 连接状态；选中项显示格式：`当前设计环境.ics---零部件名`（无外链显示“(虚拟)”）。
4. **子插件实现 `Activate()` 而非 `CreateRibbonButton()`。** `Initialize` 只做资源准备，`Activate` 负责 `Show()+Activate()`。
5. **迁入必须删除原 `Addin.cs` / `Program.cs`。** 残留 `[ComVisible(true)]`、`[Guid]`/`[ProgId]` 会污染 Host 清单或造成 COM 注册冲突。
6. **资源文件用 `PluginDirectory` 或 `Assembly.Location` 解析。** 不用 `AppDomain.BaseDirectory`（CAXA bin）。
7. **mt.exe 清单必须用 `local-name()` 查询。** `$m.assembly.$tag` 在 manifest 带 xmlns 时全部返回空，会导致 interop 的 `clrClass/clrSurrogate` 静默残留 → SxS 合并失败、CAXA 起不来。
8. **部署后必须抽出 Host DLL 的内嵌清单验证。** `mt.exe -inputresource:host.dll;#2 -out:probe.xml`，确认 clrClass 只有 `caxa3dmytoolsaddin.Addin`；bin 下的 `.dll.manifest` 不能作为最终状态依据。
9. **`.ps1` 必须恰好一个 UTF-8 BOM。** 无 BOM → PS5.1 按 GBK 解码吞引号；多 BOM → 注释块/`param` 失效（`The term '<#' is not recognized`）。入口脚本无法自校正，打包/交付前做字节级检查；`package.ps1` 的归一化清单必须包含 `build.ps1`。
10. **卡顿先归因，再改代码。** 用日志时间戳 + 线程号判断阻塞在谁的调用里；数据必须自洽；磁盘占用低却长等待 = 等待型延迟；用纯手动流程作最终判据；环境问题不写进插件。
11. **新增目录库 = 文件 + config + 注册表登记（或放到注册表已指向的绝对路径），三者缺一不可。** 只加 `.icc.config` 一律视为未注册。
12. **接手外部 .icc 先解析 MemberData。** 含 `IPRO*` / 第三方程序集类型的条目，先确认程序集在盘、类型可加载，再谈目录显示。
13. **CAXA COM 对象只能在 UI 主线程访问，严禁 `Task.Run` / `BackgroundWorker`。** 长任务留在 UI 线程，用限流 40ms 的 `Application.DoEvents()`（`PumpUi`）保持响应 + `CancellationTokenSource` + 停止按钮。
14. **迁入集合体后禁止“顺手优化”独立 Add-in 的线程模型。** 可改资源路径、入口契约、删 Ribbon；不改长任务执行框架与 COM 调用上下文。

> 铁律 10 的实例见《caxa3dcodenameextraction-项目总结》第 10 节；铁律 13/14 的实例与正反代码见《caxa3dTreeBom-项目总结》第十节。

