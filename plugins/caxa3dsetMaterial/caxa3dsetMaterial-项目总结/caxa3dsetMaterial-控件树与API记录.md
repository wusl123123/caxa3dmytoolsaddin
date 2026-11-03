# caxa3dsetMaterial-控件树与API记录

## 二、进入材料页面的 API 情况

- **没有公开 API**：全量检索 ICAPIIRONCADLib / ICAPIHELP，与材料相关的只有 `IZPartProperty.GetMaterialName/SetMaterialName`、`IZAssemblyProperty.GetMaterialName/SetMaterialName`、`IZElement.GetMaterialNameProperty`，全部是读写名称，**不含打开对话框**。
- **没有命令枚举**：`IZBaseApp.RunCommand(eZCommand)` 的枚举只有 13 个值（缩放、隐藏、ghost、Excel 更新等），无材料项。
- 对话框归属已定位：右键菜单项"**编辑材质库...**"的字符串存在于 `bin\zh-cn\3isdLg.dll`（场景文档模块 `3isd31.dll` 的语言资源），"编辑材料"同时出现在 `3icmLg/3iuiaLg/3iAdvFeatureLg` 等资源里。
- 结论：程序化"进入材料页面"只能走 **UI 自动化**——在结构树/场景中右键选中零件，点击上下文菜单"编辑材质库..."（可复用 `caxa3dcodenameextraction` 的弹窗自动点击与焦点回归代码）。设计环境的右键菜单是原生 MFC 菜单，无法用 CRX 命令表投递。

## API 速查（源码索引）

- `ICAPIIRONCADLib.txt`：`IZPartProperty` 接口总表（含 GetMaterialName / SetMaterialName / MassDensity / MassType / GetMassProperties）
- `ICAPIIRONCADLib.txt`：`IZElement.GetMaterialNameProperty`
- `ICAPIIRONCADLib.txt`：`IZSurfaceFinish`、`IZBodyFace.SetSurfaceFinish`、`IZShaderMgr`、`IZBaseApp.ShaderMgr`
- `ICAPIIRONCADLib.txt`：`eZCommand`（13 个值，无材料命令）

