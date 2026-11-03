# caxa3dTreeBom-踩坑与教训

## 六、踩坑与规避

1. **后台启动 CAXA 无法验证 Add-in 加载**：命令行 `Start-Process IronCad.exe` 起来的进程里，
   连已长期使用的 `caxa3dsetMaterial` 等托管插件也未加载；说明加载时机依赖前台交互/环境初始化，
   不能据此判定本插件失败。验证必须以用户桌面正常启动为准。
2. **CAXA 程序目录不可写**：默认模板和导出默认路径一律放 `%APPDATA%\caxa3dTreeBom`，
   模板从程序目录播种（安装脚本也播种），避免「保存表格」直接失败。
3. **备份策略**：仅当配置文件**不含本插件条目**时才备份，避免把已注入条目的「脏文件」当成原始备份；
   还原时再叠加正则兜底移除，保证卸载干净。
4. **日志跨线程**：后台线程只 `Enqueue`，UI `Timer` 每 250ms 批量刷入 `RichTextBox`，
   避免高频 `BeginInvoke` 拖死界面（大装配遍历时节点数可达数千）。
5. **缩略图缓存**：按 PNG 路径缓存 `Image`，切换表格/重新生成时统一 `Dispose`，防止 GDI 句柄泄漏。
6. **元素失效**：属性回读时若 `IZElement` 已失效（场景重开/元素删除），保留原值并只告警一次，
   不因单行失败中断整批。
7. **同名件合并**：同层按「外链文件 + 元素名称」合并；总数量按「文件名（虚拟件用元素名）」汇总，
   仅首次出现的行填数值，其余填 `---`（与 SwTreeBom 语义一致）。
8. **SC 列禁止兜底（实证）**：`SC*` 表头必须只认**精确同名**的自定义属性。曾经实现的
   「去 SC 前缀 → 名称/材料/图号/备注 兜底」会让 `SC备注` 在没有该属性时被 `GetBOMSetting`
   的描述（desc）填充，造成"SC备注被填备注信息"的混淆。参考项目
   `caxa3dAttributeManageraddin` 的 `TargetFields` 只有 `SC图号/SC名称/SC规格/SC材料`，
   且逐字精确读取、不做任何回退。结论：SC 列读不到就留空。
9. **脚本重复 BOM**：`deploy.ps1/restore.ps1` 曾被补写 UTF-8 BOM 两次（`EF BB BF EF BB BF`），
   Windows PowerShell 5.1 会把它当成脚本首行的非法符号（`术语 '﻿#' 不是…`）并报错（脚本仍能继续执行）。
   规则：给脚本补 BOM 前先剥离已有 BOM，最终只保留一个；本轮已修复项目根与 `bin\...\net48` 下四份脚本。
10. **规则文件说明与规则必须隔离（实证）**：说明行 `#规则说明,抓取""之间的字段#` 本身含两个双引号，
    若按"抓所有引号内文本"解析会生成一个空字段并破坏比对。`MergeRule.Load` 的处理是：
    先按行判断首字符 `#` → 整行跳过；只对非说明行提取 `"..."`，空字段丢弃。
11. **第一行装配体的工程图不在 ics 同级**：顶层装配体保存时工程图常放在与 ics 同名的文件夹里
    （`<目录>\<基名>\<基名>.exb`），只查同级同名 .exb 会误判"无工程图"，须追加同名文件夹查找。
12. **CAXA COM 对象只能在宿主 STA 主线程调用（实证）**：`Task.Run` 的后台线程里调
    `IZBaseApp.ActiveDoc` 会拿到 null（不抛异常也不崩），于是 `SceneDoc == null`，
    日志表现为「当前活动文档不是 3D 场景文档」紧接「场景顶层 0 个零部件」；
    同一进程里 UI 线程的 2s 轮询却能正常读到文件名和选中零部件，极易误判为"环境不对"。
    解决：长任务改 `RunOnUiThread` 同步跑在 UI 线程，进度回调里 `Application.DoEvents()`
    （40ms 限流）维持进度可见与停止按钮可点；不要再用后台线程碰任何 CAXA API。
13. **现场机器整份日志写不出来**：日志固定写 `%TEMP%` 时，有的机器 TEMP 被重定向/无写权限，
    `File.AppendAllText` 抛异常被静默 catch，现场就"无法生成 log"。改为三级降级
    （%TEMP% → `%APPDATA%\caxa3dTreeBom` → DLL 目录），并在窗口启动时把实际日志路径打进日志区。

## 十、2026-10-29 其他机器无法生成 BOM（COM STA 线程模型被 Task.Run 破坏）—— 详细记录

> 对应里程碑 17、踩坑 12。集合体层规则见铁律 13/14。

### 现象
树形BOM 插件在**本机**（开发者机器）能正常生成 BOM、读属性、抓缩略图，但**部署到其他机器**后，点击「生成BOM」无反应或日志显示“场景顶层 0 个零部件”、“当前活动文档不是 3D 场景文档”。同一台机器同一流程，本机 OK、他人机器直接失效。

### 根因
把独立 Add-in 迁入集合体时，把所有长任务的执行框架从 **`RunOnUiThread`（UI 线程同步）** 改成了 **`RunAsync` + `Task.Run`（后台线程）**。CAXA 的 COM 对象（`IZBaseApp / IZSceneDoc / IZSelectionMgr` 等）是 **STA（Single Threaded Apartment）** 模型，只能在创建它们的线程（UI 主线程）访问。把 RCW 放到 `Task.Run` 里跨公寓访问，**其他机器 COM 严格模式下** `_app.ActiveDoc` 会返回 null、`scene.SelectionMgr` 直接抛异常、装配遍历空列表。

**本机能跑纯属时序巧合**：.NET 的 RCW 代理在某些时机可以容忍跨线程访问（.NET Framework 4.8 的 COM 互操作对 STA 违规不是 100% 强制阻断），加上本地缓存/时序幸运，让这个致命 bug 在本机被掩盖了。

### 修复内容

| 文件 | 改前 | 改后 |
| --- | --- | --- |
| `CaxaService.cs` `SceneDoc` | `_app.ActiveDoc as IZSceneDoc` 单步 return | 完整版：含 `NoteDocState` 去重 trace、`DescribeActiveDoc()` 诊断方法、异常时记 trace |
| `MainForm.cs` 执行框架 | `RunAsync(string, Func<CancellationToken, Task>)` → `Task.Run(async ...)` | `RunOnUiThread(string, Action<CancellationToken>)` → 直接 `work(token)` |
| `MainForm.cs` 进度维持 | `BeginInvoke` 跨线程更新 `lblTableFile` | `PostStatus` + `PumpUi()`（限流 40ms 的 `Application.DoEvents()`），UI 线程同步更新 |
| `btnGenerate_Click` | `async void` + `await RunAsync` + `BeginInvoke` 包 UI 更新 | `void` + `RunOnUiThread` + PumpUi + 直接 UI 更新 |
| `btnReadProps_Click` | 同上 | 同上 |
| `btnThumb_Click` | 同上 | 同上 |
| `ApplyMergeRulesAsync` | `async Task` + `RunAsync` | 改名 `ApplyMergeRules`（同步）+ `RunOnUiThread` |
| BOM 生成诊断 | 无 | 日志输出 `🔎 DescribeActiveDoc()`（文档类型/名称/可转接口/打开数） |

### 反面 / 正面教材

```csharp
// ❌ 危险：跨线程访问 STA COM 对象
await RunAsync("生成BOM", delegate(CancellationToken token) {
    // 这里 _svc.SceneDoc / GetSelectedElements 会取到 null 或抛异常
    var roots = _svc.GetSelectedElements(out err);
    ...
    BeginInvoke(new Action(delegate { DisplayTableData(); }));  // UI 还要回切
    return Task.FromResult(true);
});
```

```csharp
// ✅ 安全：留在 UI 线程同步执行
RunOnUiThread("生成BOM", delegate(CancellationToken token) {
    var roots = _svc.GetSelectedElements(out err);   // STA 合法
    ...
    DisplayTableData();   // UI 线程直接调用
    PumpUi();             // 保持可响应 + 停止按钮可点击
});
```

### 待实机确认
1. 在之前“无法生成 BOM”的那台机器上部署修复后的 DLL，验证：
   - 点击「生成BOM」后日志显示 `🎯 遍历起点：场景顶层 N 个零部件`（N > 0）；
   - 表格区出现树形结构（层级编码 1 / 1.1 / 1.1.1 …）；
   - 缩略图能正常抓取、属性能正常读取。
2. 在本机同样部署，确认无回归（已编译通过，0 错误 0 警告）。

