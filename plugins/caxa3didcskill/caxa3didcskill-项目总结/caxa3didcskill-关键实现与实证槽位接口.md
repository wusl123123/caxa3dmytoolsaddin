# caxa3didcskill-关键实现与实证槽位接口

## 3. 实现要点

| 项 | 值 / 做法 |
| --- | --- |
| Id | `caxa3didcskill` |
| DisplayName | `后台进程清理` |
| Category | `PluginCategory.Independent` → 落在「2. 独立插件」列 |
| 强名称 | `Lib\ICApiAddin.snk`，token `56647e1eee90f6d4`（与 Host 一致） |
| 程序集级 | `[assembly: ComVisible(false)]`，DLL 无内嵌清单（不做 mt/sn 后处理） |
| EXE 解析 | ①`IPluginHost.PluginDirectory\caxa3didcskill.exe` ②启动器 DLL 所在目录 ③逐级向上 6 层找 `tools\caxa3didcskill\bin\x64\Release\net48`（开发期兜底） |
| 单实例 | 工具自带定时清理，多开会并发清理同一批进程 → 已有实例只前台化，不重复 `Start` |
| 生命周期 | `Shutdown()` **不结束**外部进程：用户可能正用它的「自动运行」，卸载时才由 `restore.ps1` 结束 |
| 失败提示 | EXE 缺失时 `Activate()` 抛 `FileNotFoundException`，主窗口统一弹「打开 后台进程清理 失败」，并提示重跑 `安装.bat` |

`Initialize()` 只解析路径 + 写一行日志，**不启动进程**：CAXA 启动阶段不该弹出工具窗口。

