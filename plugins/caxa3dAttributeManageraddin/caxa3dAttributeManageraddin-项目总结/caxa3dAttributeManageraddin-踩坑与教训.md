# caxa3dAttributeManageraddin-踩坑与教训

## 四、踩坑与修复

1. **PowerShell 中文乱码 / Unexpected token**
   Write 工具默认 UTF-8 无 BOM，PowerShell 5.1 按 ANSI 解析 → 所有 `.ps1` 改存 UTF-8 **with BOM**。
2. **`-CaxaRoot` 传错层级**：要传父目录 `C:\Program Files\CAXA\CAXA 3D`，不是 `...\2027`。
3. **Add-in 未加载（无 trace 日志）**：根因是 `IronCad.Addin.config` 的
   `<displayname><default>` 必须与 `HKCU\...\Applications\<键名>` 一致。
   修正为 `default=caxa3dAttributeManageraddin`、`zh-cn=自定义属性管理器` 后加载成功。
4. **点击按钮前死锁风险**：窗口句柄必须在 `InitSelf` 线程预创建（`new MainForm` 构造里
   `CreateHandle()`），不能等 OnClick 回调再创建，否则与 CAXA 主线程互等。
5. **窗口超出 1366×768 屏幕**：`ClientSize` 由 1264×1040 改为 1264×700。
6. **DataTable 插列重名异常**：`InsertColumnAt` 使用唯一列名 `Col_{idx}_{Guid:N}`。
7. **插入行越界**：`InsertRowAt` 增加 `rowIdx >= Rows.Count` 时 `Rows.Add` 分支。
8. **无活动文档时刷新报红**：改为提示「当前没有活动文档，打开设计场景后再刷新」。

## 十、SC 前缀自动回退残留修复（2026-10-23）

**现象**：插件依然将 "SC图号" 内容填写到 "图号" 列。

**根因**：之前只删了两处 SC 回退逻辑，漏了 MainForm.cs 里三处关键残留——
RefreshPropertyValues 的 candidates 列表自动拼 SC 前缀、
刷新循环内的 scName fallback 查询、
ResolveTargetPropName 的 fallback 也拼 SC 前缀。
三者配合导致：表头 "图号" → 插件偷偷回退查 "SC图号" 拿到值 → resolvedPropNames
缓存成 "SC图号" → 后续写回也用 "SC图号"。

**修复**（MainForm.cs 三处，严格精确匹配）：
1. `candidates` 列表改为直接 `new List<string>(propertyNames.Values)`，不再自动拼 SC 前缀
2. 删除 scName 构造和 fallback 查询块，`resolvedPropNames[colIdx] = header` 始终存表头原名
3. `ResolveTargetPropName` fallback 从 `header.StartsWith("SC") ? header : "SC" + header` 改为直接 `return header`

**验证**：dotnet build Release x64 0 错误，DLL 已部署到 CAXA 3D 2027 bin 目录。

