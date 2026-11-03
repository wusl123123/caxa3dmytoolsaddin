# caxa3dAttributeManageraddin-项目定位与需求

> 原总结标题：项目总结 - 2026-10-22


## 一、项目目标

把原跨进程独立工具 `D:\code\ICAPI\addout\caxa3dAttributeManager`（CAXA 3D 自定义属性管理器）
改制成 **CAXA 3D 内置 .NET Add-in 插件**，项目名 `caxa3dAttributeManageraddin`：

- CAXA 3D 2027 启动后自动加载，场景功能区出现按钮「**自定义属性管理器**」；
- 点击按钮打开原有工具窗口，直接在进程内读写 CAXA 3D 模型的自定义属性；
- 保留原工具全部功能：xlsx 属性表加载/编辑/保存、读取选中零件/装配体的
  `SC图号 / SC名称 / SC规格 / SC材料`、回填属性、行列增删、日志保存。

参考项目：`D:\code\ICAPI\caxa3dapigetaddin`（Add-in 注册与部署）
避坑参考：`D:\code\ICAPI\Caxa3dcudeaddin`（SxS 清单、强名称、InitSelf 预创建句柄等）
开发规则：`D:\code\ICAPI\caxa3d-csharp-二次开发规则.txt`

## 七、优化（第二轮）

1. **按钮“口”乱码**：根因是图标位图尺寸不符（原为 16×15 / 26×25），CAXA 在
   文字前画占位方块。改为标准 16×16 / 32×32 位图后按钮显示正常图标。
2. **连接区**：删除「连接状态: 已连接/未连接」标签；文件标签移至「定位文件」按钮
   左侧，用 `TextRenderer.MeasureText` 按实际文本长度设置宽度，过长时以按钮为界省略。
3. **API 抓取简化**：
   - 删除 `SelectionsAvailable` 诊断调用（语义随版本相反，无实际用途）；
   - `GetSelectedElementsZArray` 为主路径，成功即返回，SAFEARRAY 仅在 ZArray 不可用/失败时回退；
   - 定时器改为一次 `ProbeSessionInfo` 探测，失败即判会话失效，去掉重复 `Ping`。
4. **日志**：`Diagnostics.Trace` 只写文件不进 UI；UI 日志去掉 `[tid x/bg]`，仅保留时间与消息。

## 八、优化（第三轮）

1. **按钮“口”乱码（彻底方案）**：
   - 彻底移除图标：`CreateCommandHandler` 图标参数改回 `null, null`，使用 CAXA 默认图标；
   - 删除 `ConvertImage.cs`、`Icons/` 目录、csproj 里的 EmbeddedResource 条目；
   - 删除 `Addin.cs` 里的 `LoadIcon`、`IconStreams` 及相关 using。
   - 根因分析：CAXA 2027 对 `IPictureDisp` 类型的位图渲染要求苛刻（尺寸/格式/分辨率稍有不符即画占位方块），
     直接传 null 后走内置图标无此问题。

2. **严格按属性名精确匹配（大小写敏感）**：
   - 原逻辑自动为"图号"拼出候选"SC图号"，导致表头"图号"列错误读取"SC图号"属性值；
   - 已删除所有 SC 前缀自动回退逻辑（`RefreshPropertyValues` 与 `ResolveTargetPropName`）；
   - 表头写什么就查什么：表头是 "SC图号" → 读 CAXA 里的 "SC图号" 属性；表头是 "图号" → 读 CAXA 里的 "图号" 属性，
     两者独立，互不干扰。CAXA 自定义属性本身区分大小写，C# `Dictionary<string,T>` 默认为 Ordinal 比较，完全匹配。

