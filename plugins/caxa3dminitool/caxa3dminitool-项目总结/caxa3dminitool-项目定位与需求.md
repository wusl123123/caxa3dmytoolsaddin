# caxa3dminitool-项目定位与需求

> 原总结标题：caxa3dminitool 项目总结


- 中文名称：CAXA3D小工具合集
- 交付形态：`caxa3dminitool.dll`（.NET Framework 4.8 / x64 / 强命名 / 内嵌 SxS 清单） + `安装.bat` / `卸载.bat`
- 目标版本：CAXA 3D 2027+（部署脚本会自动扫描 `C:\Program Files\CAXA\CAXA 3D\*` 下所有已装版本，当前本机 2024 / 2027 均已可部署）
- 工作目录：`C:\code\ICAPI\caxa3dminitool`
- 参考工程：`C:\code\ICAPI\caxa3dsetMaterial`（链接 CAXA 与界面、注册/卸载骨架）

## 功能清单（第 1 次迭代）

| 控件/按钮 | 行为 |
|---|---|
| 功能区按钮「小工具合集」 | 注册 `ZControlBar` + `ZRibbonBar`，点击才创建窗口（不在 CAXA 启动时弹窗） |
| 主窗口「窗口前置」 | TopMost 开关，默认锁定最上层，蓝色高亮表示已锁定 |
| 主窗口「增料除料切换」 | 取当前选中特征 → `IZFeature.OperationType` 在 `Z_UNITE(1)` / `Z_SUBTRACT(2)` 之间互换 → `IZPart.Update()` 重建 → **回读 OperationType 判定成功** |
| 选中状态条 | 实时（2s 定时）显示 `当前设计环境.ics---零部件名（虚拟 / 外部链接）`，零件附带「内核：ACIS/Parasolid」，特征则附「特征：名称、类型、增料/除料」 |
| 日志区 | 保存日志 / 清理日志；同时写 `%TEMP%\caxa3dminitool_trace.log`（进程启动时清空） |

## 功能清单（第 2 次迭代：内核切换）

| 控件/按钮 | 行为 |
|---|---|
| 「切换Parasolid内核」/「切换ACIS内核」 | 按 `caxa3dsetMaterial` 的装配体穿透规则抓叶子零件 → 逐个 `IZPart.KernelType = target` → 全部写完再 `IZSceneDoc.Update()` → 对每个已改零件**回读 KernelType 判定** |
| 界面形态（第 3 次迭代） | 只留按钮：窗口前置 + 三个功能按钮平铺（无分组框、无状态标签、无进度条、无日志区、无停止键）；结果全部写文件日志 `%TEMP%\caxa3dminitool_trace.log` |
| 计时器分片 | 定时器每 tick 处理 25 个零件，界面保持可响应；关窗即置停止标志，不强行保存、不强关图纸 |

穿透规则（与 caxa3dsetMaterial 同口径）：
- 装配体判定：`elem is IZAssembly / IZAssemblyProperty` 或 `elem.Type.ToString()` 含 `Assembly`；
- 装配体 → `GetChildrenZArray()` 递归；
- 叶子 → 直接收，`HashSet<IZElement>` 跨节点去重；
- 无选中 → `IZSceneDoc.GetChildElements()` 穿透整棵树；
- 轻量化零件写前 `GetBody(true)` 强制加载（对应内置工具的「加载所有零件」）。

