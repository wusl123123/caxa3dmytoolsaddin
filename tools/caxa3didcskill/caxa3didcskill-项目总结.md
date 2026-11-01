# caxa3didcskill 项目总结

## 1. 定位

清理 CAXA 3D 残留的**无窗口后台进程**（任务管理器显示为 “IRONCAD Design Collaboration Suite”，实际进程名同为 `IRONCAD.exe`），带可见窗口的前台实例一律保护。

## 2. 形态与迁移（2026-10-31）

- **现行形态**：独立 WinForms EXE，源码 `tools\caxa3didcskill\`，不实现 `IPlugin`、不由 Host 进程内加载。界面入口由 `plugins\caxa3didcskill\` 的**启动器插件**提供（工具箱「2. 独立插件」列）；EXE 随安装包部署到 `<CAXA bin>\caxa3dmytoolsaddin\plugins\caxa3didcskill\`，与启动器 DLL 同目录。
- **迁移原因**：规则 16.6 / 第 17 节明确「进程清理只用于独立 EXE/外部脚本；进程内 addin 不需要也不应做」。原实现在 CAXA 进程内杀同名进程存在自杀风险，且规则要求删除。
- **迁移动作**：
  - `plugins\caxa3didcskill` → `tools\caxa3didcskill`（git 保留历史）；删除 `Plugin.cs`（不再实现 `IPlugin`）、`Lib\`、自身强签名与 `Contracts` 引用。
  - `build.ps1` 的 `$plugins` 删除该项；`src\plugins\caxa3didcskill` 已清理。
  - 部署侧无需改脚本：`deploy.ps1` 每次部署会先删掉整个 `plugins\` 目标目录再复制当前 8 个插件，旧插件在下次 `安装.bat` 时消失。
  - `AGENTS.md`、`README.md`、`caxa3dmytoolsaddin-项目总结.md` 同步为「8 个子插件 + 1 个独立工具」。
- **接回界面（同日后续）**：迁出后「2. 独立插件」列一直是「（暂无插件）」，工具在界面里彻底消失。新增 `plugins\caxa3didcskill\` 启动器插件（`Category=Independent`，只拉起本 EXE、单实例前台化），`build/deploy/restore/package` 四脚本同步管理 EXE；详见 [plugins\caxa3didcskill\caxa3didcskill-项目总结.md](../../plugins/caxa3didcskill/caxa3didcskill-项目总结.md)。

## 3. 功能

1. **手动运行**：立即执行一次清理；运行中按钮变为「停止清理」。
2. **自动运行**：按分钟间隔定时清理；间隔限制 1~1440 min（原实现允许任意 >0，已收紧）；自动运行中可再次点击停止。
3. **窗口前置**：默认置顶，蓝色高亮表示已锁定，可取消。
4. **状态栏**：显示「清理中 / 上次结果（杀死、失败、保护前台数量）」，不再每次弹模态框。
5. **日志区**：清空、复制；同时落盘 `%TEMP%\caxa3didcskill_trace.log`。

## 4. 安全策略（P0 / P1）

| # | 策略 | 说明 |
| --- | --- | --- |
| 1 | 排除自身 PID | 工具是独立进程，但显式排除，防同名/边界情况 |
| 2 | 可见窗口保护 | 一次 `EnumWindows` 快照；`IsWindowVisible` + 非空标题；排除 `WindowsForms10.*` 类名（规则 16.4） |
| 3 | 会话隔离 | 只处理与工具同 `SessionId` 的进程；其他登录用户/服务会话不碰 |
| 4 | 启动保护 60 s | 规则 16.7 实测主窗口约 30~45 s 才出现，原 15 s 不足；60 s 内的实例一律不杀 |
| 5 | 连续 3 次扫描确认 | 每次间隔 1.5 s，仍无可见窗口才进入清理阶段；进入杀进程前再做一次最终快照，若窗口已出现则改为保护 |
| 6 | PID + 启动时间双比对 | 扫描时记录 `StartTime`，杀前再比对，防 PID 复用 |
| 7 | 杀后重新枚举验证 | 判成功不看 `Kill()` 返回值：退出等待最多 5 s，再按 PID+启动时间确认已消失 |
| 8 | 不做阻塞 UI 的清理 | 清理在 `Task.Run` 后台线程执行；UI 线程只做按钮状态与日志追加 |
| 9 | 读不到 `StartTime` 不杀 | 权限不足时跳过并记录，不冒险 |

> 未按「进程名全杀」处理：前台与后台同名，只能靠窗口可见性区分（规则 16.3）。

## 5. P2 清理项

- 删除隐藏的「链接状态」区（`grpLink`）与 `tmrMonitor` 定时器；删除 `ProcessKiller` 中未使用的 `HasForegroundCaxa`、`CountForegroundCaxa`、`GetMainWindowTitle`。
- 日志统一到 `Diagnostics`：单锁写文件、启动清空、超过 2 MB 轮转为 `.1`；界面通过 `LineAppended` 事件订阅。
- 去掉每次运行结束的模态 `MessageBox`，改为状态栏 + 日志。
- 自动间隔下限 1 min。
- 关闭窗口时取消进行中的清理并记录，不强制收尾、不阻塞关闭。

## 6. 实现摘要

- `ProcessKiller.Run(token, log)`：扫描候选（排除自身/前台/异会话/年轻实例）→ 连续 3 次确认 → 终检 → `Kill` → 退出等待 → 重新枚举验证 → 汇总 `CleanupResult`。
- `Diagnostics`：唯一日志出口，线程安全；`LineAppended` 由界面 `BeginInvoke` 回 UI 线程。
- `MainForm`：`async void` 启动后台清理；`btnManual` / `btnAuto` / `txtInterval` 由 `UpdateUiState()` 统一控制可用性；`FormClosing` 停止自动并取消进行中的清理。

## 7. 编译与验证（2026-10-31）

```powershell
dotnet build tools\caxa3didcskill\caxa3didcskill.csproj -c Release -p:Platform=x64
```

- 产物：`tools\caxa3didcskill\bin\x64\Release\net48\caxa3didcskill.exe`（24576 B，PE Machine=0x8664）。
- 编译：0 warning / 0 error。
- 启动冒烟：窗口正常渲染（标题、前置按钮、操作区、状态区、日志区），trace 记录 `启动 v2.0.0.0` + `MainForm.Load`，正常关闭。
- Host 回归：`build.ps1` 全量编译通过，`src\plugins` 暂存 8 个插件（无 idcskill）。
- `caxa-slot-check.ps1`：总数 174，OK=124，MISMATCH=3，NOTFOUND=0，UNMAPPED=47 —— 与迁移前基线一致；3 处 MISMATCH 均为 `plugins\caxa3dcodenameextraction\SheetMetalOps.cs` 的既有死常量。本工具不调用任何 ICAPI，不新增槽位。

## 8. 已知边界与待实测

- **未在真机上执行过实际清理**（避免误杀用户当前环境）：待验证项 = ①前台 CAXA 有窗口时零误杀；②后台残留实例被清理且日志确认；③自动运行跨分钟触发；④权限不足场景记录失败而不是崩溃。
- 只处理进程名 `IRONCAD`；其他残留进程不在范围。
- 无窗口但拥有其他可见顶层窗口的实例会被保守保护（宁可漏杀，不误杀）。
- 不处理其他会话（服务/其他登录用户）的 IRONCAD。
- 部署形态：随安装包落到 `plugins\caxa3didcskill\caxa3didcskill.exe`，从工具箱「2. 独立插件 → 后台进程清理」启动；也可直接双击 EXE 运行（不依赖 Host）。启动器只负责拉起/前台化，不做任何清理逻辑。

## 9. 里程碑

| 日期 | 变更 |
| --- | --- |
| 2026-10-27 | 并入集合体，改为进程内子插件（`IdcSkillPlugin`） |
| 2026-10-31 | 按规则 16.6 迁出为 `tools\caxa3didcskill` 独立 EXE（v2.0.0.0），完成 P0~P2 加固；Host 注销该插件 |
| 2026-10-31 | 新增 `plugins\caxa3didcskill` 启动器插件接入「2. 独立插件」列；EXE 随包部署到插件目录，四脚本同步管理（工具本体未改一行代码） |
