# caxa3dTreeBom-关键实现与实证槽位接口

## 四、关键技术结论（本机实证）

1. **装配遍历**：`IZAssembly.GetChildrenZArray()`（元素侧 `IZElement.GetChildrenZArray()` 兜底）逐层递归，
   零件/装配体/钣金/面片/线框/轮廓用 `IZElement.Type` 判定，子件用 `IncludedInBOM` 过滤。
2. **选中元素**：`IZSceneDoc.SelectionMgr` → `SelectionsAvailable()` → `GetSelectedElementsZArray()`；
   非零部件类型（面/边/特征）直接过滤。选中为空时退回场景顶层。
3. **自定义属性**：`IZElement.GetCustomPropManager(selector)`，`selector=0` 零件级、`1` 共享（文档级）；
   用 `IsSharedMgr()` 定位。**`SC*` 系列属性（SC图号/SC名称/SC材料…）在共享表**，
   读取策略：按原名判定主表 → 另一张表兜底 → 去掉 `SC` 前缀再试 → 专用兜底（名称/材料/图号/备注）。
4. **材料**：先读 `GetMaterialNameProperty` 指向的属性值，再退回 `IZPartProperty/IZAssemblyProperty.GetMaterialName`。
5. **外链文件**：`IZPart/IZAssembly.GetExternallyLinkedInfo(out bool)`；虚拟件返回空 → 用元素名作为分组键。
6. **工程图判定**：同名 `.exb` 存在即 `★`（CAXA 3D 图纸环境格式；SolidWorks 侧为 `.slddrw`）。
7. **缩略图**：`IZBaseApp::FilePreviewImage(BSTR) → IPictureDisp**` 在 interop 中**未声明**，
   用 `IDispatch::GetIDsOfNames + Invoke(DISPATCH_PROPERTYGET)` 延迟绑定读取；
   失败回退 `IZBaseApp.WritePreviewToFile(src, out)`，解析 PNG/BMP/JPG/GIF，裸 DIB 补 14 字节 BMP 文件头。
   预览原始数据前 32 字节写入 trace，便于后续按实测调整。
8. **构建后处理不可省**：`mt.exe -managedassemblyname` → `mt.exe -outputresource:#2` → `sn.exe -R`。
   mt 嵌入清单会使强名称失效，不重签则 CAXA 拒绝加载。
9. **部署登记**：`IronCad.Addin.config` 的 `<AddIn>` + `IronCad.exe.manifest` 的 `<dependency>`
   （`name/version/publicKeyToken/processorArchitecture=amd64` 必须与内嵌清单逐字一致）。
10. **成功判定**：只认文件侧副作用——文件存在、字节数 > 0、xlsx 包可解析、表头回读列数一致；
    不使用「命令已执行」「编辑器空闲」之类推断。

## 十一、2026-10-29 界面优化：附加合并规则与顶部布局

1. 顶部功能按钮由「附件合并规则」更名为「附加合并规则」（日志、进度任务名、注释同步改名，概念统一为“附加”）。
2. 「表格文件」地址与抓取/生成时的跳动进度文字（`lblTableFile`）从「树形BOM表格」组框标题行上移到顶部按钮行，紧随「附加合并规则」右侧。
   实现：顶部条由 `Panel` 绝对定位改为 3 列 `TableLayoutPanel`（100 窗口前置 / 132 附加合并规则 / 100% 文字，`AutoSize=false + AutoEllipsis`，悬停看全路径）；表格标题行缩为单列只放按钮组，高度 38 不变。
3. 验证：`DrawToBitmap` 渲染顶部条，按钮与文字同排不重叠；最小宽度 1200 下文字右侧省略；`build.ps1` + `package.ps1` 0 错误并重新出包。

