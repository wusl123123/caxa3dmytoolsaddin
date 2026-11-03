# caxa3dCXDFTViewUpdate-架构图与目录结构

## 五、界面结构

`选择文件夹 / 开始运行 / 停止 / 清空日志 / 保存日志 / 注册CRX / 卸载CRX` + 复选框「后台化（不抢前台）」（默认勾选）；
ListView 五列 = 序号 / 文件 / 状态 / 耗时 / 信息；底部状态栏。
「选择文件夹」只列清单（含子目录 exb），「开始运行」才动 COM —— 两步分离，批处理期间「停止」置 `_stopRequested`，
**不强存、不关图**，`FormClosing` 必置停止标志并关掉 `PopupAutoYes` / `ForegroundGuard`。
「注册CRX/卸载CRX」不在本进程里写 Program Files，而是 `FindResource` 定位随包脚本后用 `Start-Process powershell -Verb RunAs` 拉起（脚本自身也会再提权一次）。

