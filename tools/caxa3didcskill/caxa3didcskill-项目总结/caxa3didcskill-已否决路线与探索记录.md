# caxa3didcskill-已否决路线与探索记录

## 5. P2 清理项

- 删除隐藏的「链接状态」区（`grpLink`）与 `tmrMonitor` 定时器；删除 `ProcessKiller` 中未使用的 `HasForegroundCaxa`、`CountForegroundCaxa`、`GetMainWindowTitle`。
- 日志统一到 `Diagnostics`：单锁写文件、启动清空、超过 2 MB 轮转为 `.1`；界面通过 `LineAppended` 事件订阅。
- 去掉每次运行结束的模态 `MessageBox`，改为状态栏 + 日志。
- 自动间隔下限 1 min。
- 关闭窗口时取消进行中的清理并记录，不强制收尾、不阻塞关闭。

