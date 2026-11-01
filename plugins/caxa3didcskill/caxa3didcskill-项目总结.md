# caxa3didcskill 项目总结（独立插件列启动器）

> 目录：`plugins\caxa3didcskill\`。这是**启动器**，不是工具本体；工具本体见 [tools\caxa3didcskill\caxa3didcskill-项目总结.md](../../tools/caxa3didcskill/caxa3didcskill-项目总结.md)。
> 建档 2026-10-31。

## 1. 定位

主工具箱「2. 独立插件」列的入口。该列自三列表格上线后一直是「（暂无插件）」——因为唯一符合"独立"性质的 `caxa3didcskill` 当时只是仓库里的一个裸 EXE，Host 扫不到、界面里也没有入口。本插件把"进程外工具"接进界面，同时不改动 Host 一行代码。

```
工具箱点击「后台进程清理」
  └── caxa3didcskill.dll（本插件，进程内，只做启动）
        ├── 已有 caxa3didcskill 进程 → ShowWindow(SW_RESTORE) + SetForegroundWindow
        └── 无进程 → Process.Start(caxa3didcskill.exe, UseShellExecute=true)
              └── 独立进程内的 MainForm：扫描/保护/清理 IRONCAD 残留后台进程
```

## 2. 为什么必须拆成「DLL 启动器 + 独立 EXE」

- 规则 16.6 / 第 17 节：进程清理只允许**进程外**。在 CAXA 进程内按进程名杀同名进程有自杀风险，且规则明确要求删除该实现（2026-10-31 已按此迁出）。
- 但工具箱只认 `IPlugin`（Host 只扫 `plugins\*\*.dll`），裸 EXE 进不了界面。
- 结论：DLL 负责"接进界面"，EXE 负责"干活"，两者物理同目录、逻辑零耦合。启动器**不引用 `interop.ICApiIronCAD`**、不注册 COM/Ribbon、不建窗口 —— 契约铁律（一个 Add-in / 一个按钮 / 一个清单）全部不受影响。

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

## 4. 与四个脚本的联动（缺一环就"装了但点不动"）

| 脚本 | 本插件相关的改动 |
| --- | --- |
| `build.ps1` | ①先编译 `tools\caxa3didcskill`（EXE）再编译插件；②`$plugins` 增 `caxa3didcskill`（`Extra=@('.exe')`）；③新增 `ExtraSrc` 机制：把非本工程产出的 EXE 显式补拷进 `src\plugins\caxa3didcskill`，缺件直接 throw |
| `deploy.ps1` | 插件整目录随 `[3/5]` 拷贝；新增 `[3c/5]` 校验启动器 DLL 与 EXE 同时就位（缺一即 `$hadError`），并清掉早期手工散放在 `<CAXA bin>` 根目录的旧 EXE（避免两份 EXE 各自为政）；部署校验行追加 `独立插件=…/…`、`无散放EXE=…` |
| `restore.ps1` | 先 `Stop-Process -Name caxa3didcskill`（EXE 在跑就删不掉；只清理 IRONCAD 后台进程，结束它不影响 CAXA），再删插件目录 + bin 根散放 EXE；残留校验追加「独立插件目录 / bin 根 EXE / 进程仍在」三项 |
| `package.ps1` | 第二道缺件校验：`src\plugins\caxa3didcskill` 必须同时有 `caxa3didcskill.dll` 与 `caxa3didcskill.exe`（否则打包中止——界面有按钮但点击必报错，不能等上线才发现）；`.exe` 已在扩展名白名单内；README 追加独立插件说明 |

## 5. 验证（2026-10-31）

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

1. 编译：0 error；暂存目录 `src\plugins\caxa3didcskill\` = `caxa3didcskill.dll` + `caxa3didcskill.exe` + pdb；DLL token `56647e1eee90f6d4`。
2. 假宿主冒烟（临时 net48 控制台直接引用启动器 DLL，`IPluginHost` 用桩实现）：
   - `Id=caxa3didcskill`、`DisplayName=后台进程清理`、`Category=Independent` → 归类列判定为「2. 独立插件」；
   - `Initialize` 打印 `独立工具已就位：…\caxa3didcskill.exe`；
   - 先手动起一个实例再 `Activate()`：进程数 1 → 1（未重复启动，走前台化分支）；
   - 结束全部实例后 `Activate()`：进程数 0 → 1（成功拉起 EXE）；退出码 0。
3. 槽位检查 `caxa-slot-check.ps1`：174 / OK 124 / MISMATCH 3 / NOTFOUND 0 / UNMAPPED 47 —— 与基线一致（本插件不调用任何 ICAPI）。
4. 打包：zip 内含 `plugins\caxa3didcskill\caxa3didcskill.dll` + `caxa3didcskill.exe`；缺件校验两行均通过。
5. 安装/卸载实跑（`CAXA3D_ROOT` 指向临时模拟根，版本目录名取 `3099` → verTag `1101.0`，隔离真实注册表）：安装后 `plugins\caxa3didcskill\` 内 DLL+EXE 就位、bin 根无散放 EXE、`HKCU\...\CAXA 3D 1101.0\Applications\caxa3dmytoolsaddin` 已建；卸载后目录/EXE/注册表/进程零残留，末行「校验通过」。烟测现场（临时目录 + 1101.0 注册表键）已清理。

## 6. 待实测（真机 CAXA）

- CAXA 2027 内点击「后台进程清理」→ 独立 EXE 弹出、日志 `%TEMP%\caxa3didcskill_trace.log` 出现 `启动 v2.0.0.0`。
- 工具不带 `TopMost` 冲突：工具窗口默认置顶，点击后应压在主工具箱之上（未真机确认）。
- 双击单元格/回车键两条触发路径（Host 既有逻辑）在本插件上的表现。

## 7. 里程碑

| 日期 | 变更 |
| --- | --- |
| 2026-10-31 | 三列表格上线，「独立插件」列空置 |
| 2026-10-31 | 新建本启动器插件接入独立列；`build/deploy/restore/package` 四脚本同步管理独立工具；离线冒烟 + 模拟根安装/卸载全部通过 |
