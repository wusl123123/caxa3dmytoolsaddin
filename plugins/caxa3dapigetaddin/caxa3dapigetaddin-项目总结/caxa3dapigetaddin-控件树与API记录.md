# caxa3dapigetaddin-控件树与API记录

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

