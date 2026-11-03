# caxa3dAttributeManageraddin-交付状态与验证结果

## 五、验证结果

- 构建：`dotnet build -c Release -p:Platform=x64` → 0 错误；
- 部署：`deploy.ps1 -CaxaRoot 'C:\Program Files\CAXA\CAXA 3D'` → 部署完成；
- CAXA 3D 2027 启动后 trace 日志：
  ```
  13:42:47.891 InitSelf: addin loaded by CAXA
  13:42:48.396 MainForm constructed
  ```
  → 插件被 CAXA 正常加载，主窗口已预创建（隐藏）。
- 早前带自动显示验证时已确认进程内会话建立成功：
  `session accepted (in-process)`、`ActiveDoc hr=0x0`。

## 九、卸载链路修复（第四轮）

**现象**：卸载提示"完成"却零效果 / 还原后 CAXA 无法启动。

**根因链与修复**（deploy.ps1 / restore.ps1）：

1. **BOM 丢失 = 静默崩溃秒退**：bat 调用的 PS5.1 对无 BOM UTF-8 按 GBK 解码，
   中文后引号/换行被吞 → 解析出错静默退出。本次修复过程中再次复现
   （编辑工具保存 .ps1 会丢 BOM），已固化流程：每次编辑后用
   `[IO.File]::WriteAllText` + `UTF8Encoding($true)` 重写单一 BOM，
   并用 PS5.1 `Parser::ParseFile` 校验 0 错误。
2. **脏备份**：原 deploy 无条件备份，部署后再装所拍备份含本插件条目；
   restore"最新优先"取备份还原 → 残留指向已删 DLL 的条目 → SxS 解析失败。
   修复：deploy 只做**干净备份**（配置/清单无本插件条目时才备份，重装不覆盖）。
3. **无条件兜底清理**：restore 备份还原后**一律**再执行手术式清理
   （XML 移除 → 解析失败降级正则文本移除），脏备份也无法留下残留。
4. **try/catch 全覆盖**：DLL/PDB 删除、注册表删除、去只读、备份还原、XML 保存
   均有 catch，CAXA 进程锁文件不再中断整个卸载流程，重跑即可补删。
5. **内嵌清单检测**：deploy 新增 `Test-EmbeddedManifest`（kernel32
   `FindResourceW` 查 RT_MANIFEST ID=1/2），无内嵌清单的 DLL 不登记
   manifest dependency，并反向清理历史遗留 dependency。

**附带发现的 PS 语法坑**：XmlNodeList 空集合布尔判定为真（须 `Count -gt 0`）；
.NET 方法调用参数内不能直接内嵌 PS 命令（须 `$()` 子表达式）。

**验证**：PS5.1 解析 0 错误 + 单一 BOM；临时目录模拟 15 项断言全部通过
（脏备份还原后零残留、损坏 XML 正则降级、干净文件零改动、内嵌清单检测真假两例、
无备份直接清理）。脚本已同步 bin 输出目录。

## 十一、并入 caxa3dmytoolsaddin 集合体（2026-10-27）

- 入口类由 `IZAddinServer` 改为 `AttributeManagerPlugin : IPlugin`（仍位于 `Addin.cs`）：
  `Initialize(IPluginHost)` 保存 `AddinSite` 与 `PluginDirectory`、预建隐藏窗口；`Activate()` 显示；`Shutdown()` 释放。
- 删除 Ribbon 注册（`CreateCommandHandler` + `ControlBar` + `RibbonBar.AddButton`）及图标资源；功能区只保留 Host 的「我的工具箱」（集合体铁律 1/4）。
- 删除 `[Guid]` / `[ProgId]` / 类级 `[ComVisible(true)]`；程序集级 `ComVisible(false)`，插件 DLL 不内嵌 SxS 清单。
- 默认表格 `caxa3dManagermodle.xlsx` 改为优先用 `IPluginHost.PluginDirectory` 定位（合并后 `AppDomain.BaseDirectory` 是 CAXA bin）；首次运行仍复制到 `%LocalAppData%\caxa3dAttributeManageraddin\`。
- 主窗口默认 `TopMost=true`，顶部「窗口前置」蓝/灰态开关；隐藏“连接状态”显示（集合体铁律 2/3）。

