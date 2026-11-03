# Caxa3dcudeaddin-关键实现与实证槽位接口

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

