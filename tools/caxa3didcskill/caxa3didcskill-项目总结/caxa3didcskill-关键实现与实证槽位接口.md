# caxa3didcskill-关键实现与实证槽位接口

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

## 6. 实现摘要

- `ProcessKiller.Run(token, log)`：扫描候选（排除自身/前台/异会话/年轻实例）→ 连续 3 次确认 → 终检 → `Kill` → 退出等待 → 重新枚举验证 → 汇总 `CleanupResult`。
- `Diagnostics`：唯一日志出口，线程安全；`LineAppended` 由界面 `BeginInvoke` 回 UI 线程。
- `MainForm`：`async void` 启动后台清理；`btnManual` / `btnAuto` / `txtInterval` 由 `UpdateUiState()` 统一控制可用性；`FormClosing` 停止自动并取消进行中的清理。

