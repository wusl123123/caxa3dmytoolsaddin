# Caxa3dcudeaddin 项目总结

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

## 二、关键实现（vtable 直调）

| 接口 | 用途 | 槽位 |
| --- | --- | --- |
| `IZBaseApp.ActiveDoc` | 取当前文档 | 11 |
| `IZBaseApp.CreateNewDoc` | 无活动文档时新建场景（`Z_SCENE=1`） | 66 |
| `IZSceneDoc.GetChildElements` | 枚举场景子元素（恢复/定位零件） | 7 |
| `IZSceneDoc.CreatePart` | 新建零件 | 9 |
| `IZPartFeatureMgr.CreateBlock` | 在零件上建块（两个角点） | 19 |

建块流程（`CaxaAutomation.CreateCubeAtOrigin`）：

1. `ActiveDoc` 为空则 `CreateNewDoc(Z_SCENE)`；QI 到 `IZSceneDoc`。
2. **快路径**：枚举现有零件，从末尾向前直接 `CreateBlock`（不新建零件）。
3. **进程内兜底**：`CreatePart`（槽 9）后重新枚举场景零件，从末尾找新零件再 `CreateBlock`。
   不直接使用 `CreatePart` 返回的指针（形态不保证是 `IZPart`）。
4. **子进程兜底**：STA PowerShell 子进程执行 `CreatePart + CreateBlock`；等待期间必须
   `Application.DoEvents()` 泵消息，禁止 `WaitForExit`/阻塞读。
5. 仍失败：判定 ICAPI 服务器状态损坏，提示重启 CAXA 3D。

尺寸换算：mm → m（×0.001）；长→X、宽→Y、高→Z。

## 三、踩坑

1. **必须在 InitSelf 线程预创建窗口句柄**：推迟到功能区 OnClick 才 `CreateHandle()`，窗口创建/激活的跨窗口消息会与正同步等待回调返回的 CAXA 主线程互等，直接死锁（2026-10-22 实测：点按钮后 CAXA 无响应，日志停在 OnClick 之前）。
2. **`CreatePart` 可能“报错但已建件”**：服务器端返回 `0x80010105`，实际零件已生成。不能只看 hr，需重新枚举场景零件、从末尾回收新零件后建块。
3. **子进程兜底不能阻塞等待**：进程内等待子进程时若不泵消息，CAXA 主线程被堵，子进程的 COM 回调随之死锁。
4. **指针生命周期**：`CreatePart` / `CreateBlock` 返回的指针只用于判断 hr，随即 `Release`；场景/零件指针在 `finally` 中释放。
5. **迁入集合体后不做 COM 入口**：删除 `[Guid]` / `[ProgId]` / 类级 `[ComVisible(true)]`，程序集级 `ComVisible(false)`，插件 DLL 不内嵌 SxS 清单。
6. **进程内会话不释放 RCW**：`CaxaSession.Dispose()` 只释放接口指针，CAXA 对象归宿主所有。

## 四、里程碑

| 时间 | 里程碑 |
| --- | --- |
| 2026-10-22 | 独立 Add-in 版：进程内 vtable 建块链路跑通，含 PowerShell 子进程兜底与尺寸记忆 |
| 2026-10-27 | 迁入集合体：`Addin.cs` → `CubePlugin(IPlugin)`，去掉 Ribbon 注册与图标，统一由主工具窗口入口 |

## 五、待实测 / 已知限制

- CAXA 3D 2027 上「建立立方体」实际出块及尺寸正确性（以设计树新增零件/块特征与包围盒为准）。
- ICAPI 状态损坏时只能提示重启 CAXA，无自动恢复路径。
- 已有零件快路径在空场景下必然失败，属预期；走进程内 `CreatePart` 兜底。
