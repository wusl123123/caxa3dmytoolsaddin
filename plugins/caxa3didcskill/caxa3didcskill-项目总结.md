# caxa3didcskill 项目总结

## 项目概述
独立 C# WinForms 工具，识别并杀死 CAXA 3D 设计环境残留的无窗口后台进程（任务管理器中显示为 "IRONCAD Design Collaboration Suite"），同时严格保护带可见窗口的前台 CAXA 3D 程序。

## 架构与环境
- 语言：C#
- 框架：.NET Framework 4.8
- 平台：x64（CAXA 3D 为 64 位进程）
- 界面：WinForms
- 参考项目：D:\code\ICAPI\addout\Caxa3dcude（独立 exe，能正常链接 CAXA 3D）

## 功能说明
1. **链接状态区**：
   - 显示前台 CAXA 3D（带可见窗口的 IRONCAD 进程）是否在运行，运行中显示 "已保护"。
   - 「刷新状态」按钮：立即检测一次。
   - 「启动监控」按钮：按输入框设定的分钟间隔定时检测前台状态，再次点击变为「停止监控」。
   - 监控间隔输入框：默认 15 min，可修改，范围 (0, 1440]。
   - 程序启动时自动刷新一次状态。
2. **操作区（横向布局）**：
   - 手动运行：立即执行一次后台进程清理。
   - 自动运行：按输入框设定的分钟间隔定时清理；再次点击变为「停止自动」。
   - 输入框：默认 15 min，可修改，范围 (0, 1440]。
3. **日志区**：记录运行情况与杀死时间，支持清空和复制。

## 核心实现
### 后台进程识别与保护（ProcessKiller.cs）
- 遍历所有 `IRONCAD` 进程。
- 通过 `EnumWindows` + `GetWindowThreadProcessId` + `IsWindowVisible` + `GetWindowTextLength` 判断进程是否拥有可见且带标题的顶层窗口。
  - 有可见窗口 → 前台 CAXA 3D → **保护**，不杀。
  - 无可见窗口 → 后台 "IRONCAD Design Collaboration Suite" → 杀死。
- 启动 15 秒内的进程不杀，避免误杀正在加载、窗口尚未出现的前台实例。
- 比 `Process.MainWindowHandle` 更可靠（实测 IRONCAD 窗口偶尔闪断）。

### 自动运行
- 使用 `System.Windows.Forms.Timer`，间隔 = 分钟 × 60 × 1000 ms。
- 运行期间禁用手动运行按钮与输入框，防止并发清理。

### 前台状态监控
- 独立 `tmrMonitor` 定时器，与自动清理互不影响。
- 启动时 `MainForm_Load` 自动调用一次 `RefreshLinkStatus`，之后按用户设定间隔（默认 15 min）定时刷新。
- 监控运行期间锁定监控间隔输入框，停止后恢复可编辑。

### 日志
- `Program.Log` 同时写入 `%TEMP%\caxa3didcskill_trace.log` 和界面日志区（线程安全，UI 用 `BeginInvoke`）。
- 日志超过 30000 字符时裁剪前半部分，避免内存膨胀。

## 遇到的陷阱与注意事项
1. **后台进程与前台同名**：`IRONCAD Design Collaboration Suite` 实际进程名也是 `IRONCAD.exe`，不能按进程名直接杀，必须用窗口可见性区分。直接 `GetProcessesByName("IRONCAD")` 后全杀会误杀前台 CAXA 3D。
2. **`Process.MainWindowHandle` 不可靠**：对 IRONCAD 窗口判定偶尔闪断，改用 `EnumWindows` 枚举顶层窗口并校验可见性+标题长度。
3. **启动保护**：CAXA 3D 启动初期窗口尚未出现，若此时清理会误杀刚启动的前台实例，因此加 15 秒启动保护窗口。
4. **本程序不监控前台**：仅在链接状态区展示前台是否在运行，不做任何前台进程操作，符合需求 "只杀死无关后台进程"。
5. **x64 强制**：CAXA 3D 2024+ 为 64 位进程，csproj 中固定 `PlatformTarget=x64`、`Prefer32Bit=false`。

## 文件清单
- caxa3didcskill.csproj
- Program.cs（入口 + 日志）
- ProcessKiller.cs（进程识别与清理核心）
- MainForm.cs（界面逻辑）
- MainForm.Designer.cs（界面布局）

## 编译输出
- bin/Release/net48/win-x64/caxa3didcskill.exe（唯一输出，PE Machine=0x8664）
- csproj 固定 `RuntimeIdentifier=win-x64`、`PlatformTarget=x64`，不再产出 AnyCPU 版本。

## 交付说明
直接运行编译后的 exe 即可，无需安装。日志同时落盘到 `%TEMP%\caxa3didcskill_trace.log`，便于排查。

## 并入 caxa3dmytoolsaddin 集合体（2026-10-27）

- **交付形态变更**：由独立 EXE 改为集合体子插件；当前交付物是 `caxa3didcskill.dll`，不再直接运行 exe。
- 删除原 `Program.cs` 入口（`Program.Main`）；日志改由插件自包含：`%TEMP%\caxa3didcskill_trace.log` + 界面日志区。
- 新增 `Plugin.cs → IdcSkillPlugin : IPlugin`：`Initialize` 预建隐藏窗口 / `Activate` 显示 / `Shutdown` 释放；不注册 Ribbon。
- 主窗口默认 `TopMost=true`（顶部「窗口前置」蓝/灰态开关）；`grpLink`（链接状态区）**隐藏不显示**——集合体铁律 3 规定不显示 CAXA 连接状态；相关代码保留但界面不可见。
- 进程识别、前台保护、启动 15 秒保护、定时清理逻辑均未改动。
- 程序集级 `ComVisible(false)`，插件 DLL 不内嵌 SxS 清单；x64 固定不变。
