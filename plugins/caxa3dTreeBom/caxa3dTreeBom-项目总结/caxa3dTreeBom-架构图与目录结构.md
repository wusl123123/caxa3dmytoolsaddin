# caxa3dTreeBom-架构图与目录结构

## 三、代码结构

| 文件 | 职责 |
| --- | --- |
| `Addin.cs` | `IZAddinServer` 入口；`CreateCommandHandler` + `AddControlBar` + `RibbonBar.AddButton` 注册按钮；窗口单例管理 |
| `MainForm.cs` | 与 SwTreeBom 的 Form1 同构：连接状态区 / 表格区 / 日志区；额外「停止」按钮 + 进度文字 |
| `CaxaService.cs` | CAXA API 封装：场景/选中/子元素/自定义属性/材料/BOM 设置/外链/工程图/缩略图 + IDispatch 延迟绑定 |
| `BomBuilder.cs` | 递归遍历与层级编码、同层同名合并计数、总数量汇总、属性回读、缩略图回填 |
| `BomModels.cs` | `BomRow`（Code/TreeText/Level/FilePath/FileName/ElementName/Quantity/TotalQuantity/Properties…） |
| `XlsxTableService.cs` | 零依赖 xlsx 读写（`System.IO.Compression` + `System.Xml.Linq`），支持内嵌 PNG 缩略图 |
| `Diagnostics.cs` | 文件日志 + UI 日志队列（后台线程只入队，UI 定时消费） |
| `ConvertImage.cs` | `Image → IPictureDisp`（AxHost 技法，按钮图标） |

