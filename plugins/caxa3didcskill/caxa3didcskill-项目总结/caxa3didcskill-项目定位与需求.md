# caxa3didcskill-项目定位与需求

> 原总结标题：caxa3didcskill 项目总结（独立插件列启动器）


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

