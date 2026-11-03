# caxa3dcodenameextraction-架构图与目录结构

## 2. 目录结构
| 路径 | 作用 |
| --- | --- |
| `Addin.cs` | Add-in 入口（IZAddinServer），注册功能区按钮、隐藏主窗口 |
| `MainForm.cs` | 主界面 + 重命名主流程 + 等待原语 + 焦点回归 |
| `CaxaAutomation.cs` | CAXA COM 底层调用（vtable 槽位、选中元素、外部链接、属性、改名、保存） |
| `SizeBoxTool.cs` | 选中元素包围盒读取（迁自 `caxa3dsizeboxread`；只读，ICAPI 接口直调） |
| `SheetSizeTool.cs` | 钣金件尺寸填写核心（迁自 `caxa3dsheetsizewrite`：校验/展开件/板厚/回填/默认配置/保存） |
| `caxa3dsheetsizewriteaddfix.txt` | 尺寸回填规则（SC规格 / SC材料 / 材料字段名），随插件输出 |
| `CrxBridge.cs` | C# ↔ CRX 文件桥（请求文件 + 日志增量读取 + seq 回执） |
| `PopupAutoYes.cs` | 后台代点「是/确定」、文件对话框自动填入目标 ics |
| `caxa3dcodenameextraction-crx/` | CRX 桥工程（CX_open / CX_save / CX_DFT_ViewSave / CX_DFT_ViewClose） |
| `deploy.ps1` / `restore.ps1` | 一键安装 / 卸载（配置、清单、注册表、CRX） |
| `register-crx.ps1` / `unregister-crx.ps1` | 单独注册 / 卸载 CRX（界面按钮调用） |
| `Lib/interop.ICApiIronCAD.dll`、`Lib/ICApiAddin.snk` | CAXA 类型库、强命名密钥 |
| `caxa3d2027关键设置\` | CAXA 3D 2027 人工配置文件（ucx/属性模板/BOM/2D配置）+ 关键设置截图，随编译输出打包供人工导入/参考；安装脚本不复制进 CAXA 安装目录 |

编译：`dotnet build caxa3dcodenameextraction.csproj -c Release -p:Platform=x64`
产物：`bin\x64\Release\net48\`（Release 后处理自动 mt 生成清单 → 剥离 interop clrClass → 嵌 RT_MANIFEST #2 → sn 重签）

