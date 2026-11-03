# caxa3dCXDFTViewUpdate-项目定位与需求

> 原总结标题：CRX 单独注册/卸载（不进 CAXA 也能做）


> 来源：从独立 Add-in `C:\code\ICAPI\caxa3dCXDFTViewUpdate` 迁入集合体，作为第 11 个子插件，
> 归属主工具箱「1. 常用插件」列（`PluginCategory.BuiltIn`）。
> 批处理业务逻辑与界面保持原样，只做「独立 Add-in → 集合体子插件」的形态改造。

## 一、版本与工程

| 项目 | 值 |
| --- | --- |
| 插件 Id | `caxa3dCXDFTViewUpdate`（== 暂存目录名 == 程序集名，PluginManager 硬性要求） |
| 界面名称 | 刷新图纸视图 |
| 分类 | `PluginCategory.BuiltIn`（主工具箱「1. 常用插件」） |
| 入口类 | `Plugin.cs` → `ViewUpdatePlugin : IPlugin`，程序集级 `[assembly: ComVisible(false)]` |
| 工程 | `plugins\caxa3dCXDFTViewUpdate\caxa3dCXDFTViewUpdate.csproj` |
| 目标框架 / 平台 | net48 / x64（`Library`），`UseWindowsForms`，C# 10 |
| 强名称 | `Lib\ICApiAddin.snk`，`sn -vf` 有效，token `56647e1eee90f6d4`（与 Host 及全部兄弟插件同一密钥） |
| 依赖 | `..\..\src\Contracts\Contracts.csproj`、`Lib\interop.ICApiIronCAD.dll`（`EmbedInteropTypes=True`，md5 `29a23396bfbe966ba92cb4598f8c38f3` 与仓库 `Lib\` 同一份） |
| 已删除 | `Addin.cs`（CLSID `{C9F5A7B3-6D4E-4F8A-AB1C-3E4D5F6A7B8C}`、ProgId、Ribbon 注册）、自带 `deploy.ps1`/`restore.ps1`/`strip-manifest.ps1`/`一键编译部署.bat`/`安装.bat`/`卸载.bat`、`poc\`、`项目总结-2026-10-22.md`/`-23.md` 历史文档 |
| 保留模块 | `MainForm.cs`、`CrxBridge.cs`、`PopupAutoYes.cs`、`ForegroundGuard.cs`、`Diagnostics.cs`、`caxa3dCXDFTViewUpdate-crx\`（C++ 源码 + 预编译 `.crx` 127488 B） |
| 运行期资源 | `register-crx.ps1`、`unregister-crx.ps1`、`caxa3dCXDFTViewUpdate.crx`（csproj `CopyToOutputDirectory=PreserveNewest`，`.crx` 用 `Link` 平铺到输出根） |

命名空间本来就是 `caxa3dCXDFTViewUpdate`，与插件 Id 一致，迁入时未改任何命名空间。

