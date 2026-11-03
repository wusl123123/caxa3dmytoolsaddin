# caxa3dTreeBom-交付状态与验证结果

## 二、交付物清单

| 路径 | 说明 |
| --- | --- |
| `bin\x64\Release\net48\caxa3dTreeBom.dll` | 编译产物（已内嵌 SxS 清单 + 重签名） |
| `bin\x64\Release\net48\安装.bat` / `卸载.bat` | 一键部署 / 还原（自动 UAC 提权） |
| `deploy.ps1` / `restore.ps1` | 部署脚本主体（兼容 PowerShell 5.1 与 7） |
| `caxa3dTreeBomxls.xlsx` | 默认表头模板（部署时播种到 `%APPDATA%`） |
| `%TEMP%\caxa3dTreeBom_trace.log` | 运行时调试日志（每次 CAXA 启动自动清空；TEMP 不可写时自动降级到 `%APPDATA%\caxa3dTreeBom\trace.log` 或 DLL 目录，启动时在日志区回显实际路径） |

## 九、并入 caxa3dmytoolsaddin 集合体（2026-10-29）

### 9.1 入口与资源
- 删除原 `Addin.cs`，新建 `Plugin.cs → TreeBomPlugin : IPlugin`：`Initialize` 预建窗口 / `Activate` 显示 / `Shutdown` 释放；不注册 Ribbon（集合体铁律 1/4）。
- 删除 `Icons\*.bmp`、`ConvertImage.cs`、`stdole` 引用（只服务 Ribbon 图标）。
- 种子资源改为 `Path.GetDirectoryName(typeof(X).Assembly.Location)` 优先、`AppContext.BaseDirectory` 兜底：
  - `caxa3dTreeBomxls.xlsx`（MainForm 播种默认表头）；
  - `caxa3dtreebomaddfix.txt`（`MergeRule.EnsureFile`）。
- 用户数据目录保持不变：`%APPDATA%\caxa3dTreeBom`；老用户规则文件/表格继续生效。

### 9.2 构建 / 打包 / 部署登记
- `build.ps1` 的 `$plugins` 清单增加本插件，声明 `Extra=@('.txt')`。
- `package.ps1` 扩展名白名单同步补充（同批加入 `.xml`，本插件自身无 xml 资源）。
- `deploy.ps1` 的 legacy 清单补原独立 Add-in 的 dll 名、目录名、CLSID `{2F6C8B41-...}`，安装时撤销独立登记。
- 冒烟：无 CAXA 上下文构造 `MainForm(null)`（1400×1050），种子资源成功播种到 `%APPDATA%`。

