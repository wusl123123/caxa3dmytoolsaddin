# caxa3dminitool-探针与工具归纳

## 功能清单（第 3 次迭代：元素工具三连）

| 控件/按钮 | 行为 |
|---|---|
| 「选中取消压缩」 | 按**切换内核的装配体穿透规则**抓零部件（无选中则穿透整棵树）→ 逐个判 `GetStateStatus(Z_SUPPRESSED)` → `SetSuppressState(false)` → `IZSceneDoc.Update()` → **回读 Z_SUPPRESSED 判定** |
| 「更新包围盒」 | 选中元素（无选中则全树）逐个 `as IZSizeBox` → 检查 `PermitResetSizeBox` → `Reset(false)` → `IZSceneDoc.Update()`；回读 `Length/Width/Height` 判定 |
| 「全选内部链接零件」 | 种子=选中节点（无选中则全树）→ `IZSceneElement.HasInternalLinks(out bool)` + `GetInternallyLinkedElements()` 收集 → 清空选区后逐个加入；回读选区计数判定 |
| 日志区（第 4 次迭代） | 参照 caxa3dsetMaterial：底部 Dock=Bottom 只读 TextBox（Consolas 9），`Diagnostics.UiLogSink = AppendLog`，所有步骤/判定实时可见，同时写 trace 文件 |
| 「导入3D曲线坐标数据」（第 5 次迭代） | 选 txt → 逐行解析 X Y Z（空格/逗号/分号/制表符分隔，`#` `//` 注释，单位毫米）→ `IZSceneDoc.CreateWire()` → `IZWire.CreateInterpolationSpline(double[] 平铺XYZ, dir4, dir4, 0)`（官方样例 SelectionTool.cs:486-490 的写法）；毫米按 ×0.001 换算内部单位；失败回退 `CreateLines`；判定=曲线数增加 + `GetInterpolationSplinePointsCount` 回读 |
| 坐标文件示例 | `坐标数据示例.txt`（项目根目录）：25 点螺旋线，附格式说明注释 |
| 「移除透明度」 | 已按用户要求移除（按钮、处理逻辑、`ElementsTool.RemoveTransparency` 全部删除；API 映射仍保留在 `icm\Catalogs\ICM Utils-API映射.md` U10 供以后复用） |
| 「锚点移动到重心」（第 6 次迭代，原名「三维球移至重心」） | 选中零件（或装配体）→ `IZPartProperty.GetMassProperties(density=1.0, false, 1e-4, out acc, out mass, out center, out inertia)` 取重心（失败自动改 density=0 用零件自身密度；装配体走 `IZAssemblyProperty`）→ `IZSceneElement` 移锚（⚠零件**不实现** `IZEDOSiteElement`，见 M14）。移锚两策略：A `AnchorTransform` 平移 += Rᵀ·(重心−锚)（几何不动）；B `PositionTransform` 平移 = 重心。判定=**锚父系位置≈重心（≤0.05mm）且 `GetTransformToGlobal` 平移变化≈0（≤0.1mm）**；B 若把几何搬走立即回滚 `PositionTransform`。**2026-10-31 按用户要求去掉 `ActivateTriball` 三维球复位，只做重心计算 + 移锚**（按钮文案同步改为「锚点移动到重心」）。**同日整体移除**（按钮 + `MoveTriBallToCog` + `AnchorTool.cs`）；**同日 10:57 再从源工程移植新版「锚点移至重心」**：双写补偿方案（`posM.t := 重心`、`anchorM.t += Δ`，两项位移在实测模型下严格抵消），不再有三维球复位，方法名 `MoveAnchorToCog`、按钮字段 `_btnAnchorToCog`，失败回滚两矩阵并回读确认 |
| 「获取点坐标」 | 拾取顶点在选择集中返回的是**零件**而非顶点对象；正确链路=`IZSelectionMgr.GetSelectedVerticesCount(elem)`→`GetSelectedVertices(elem)`（顶点ID数组）→`IZPart.GetBody(true)`→`body as IZBodyVertex`→`GetPosition(vid)`（体局部坐标米）→`IZSceneElement.GetTransformToGlobal` 矩阵合成全局。首个成功点全局坐标复制到剪贴板 |
| 「将零部件添加到BOM」（**开发暂停**） | `IZPart/IZAssembly.IncludedInBOM = true` + 回读确认。⚠实测（2026-10-31）：put 调用成功但值**静默不生效**，三通道回读（属性/Update后/GetBOMSetting 返回值）全为 false，已回滚至简单版并暂停，见 M15。「输出范围」单选对应隐藏成员 `SetPartLinkAllIns(bool)`（文档 0 收录，ildasm 挖出，DispId 42~46） |

ICM 原条目（同一 ProgID `IPROShapeUtils.ShapeUtils`，本机未注册，只能走 API）：

| ICM 名称 | ICM 方法 | API 映射 |
|---|---|---|
| 查找压缩元素 | `OnDragDropManageSuppressedParts` | `IZElement.GetStateStatus(Z_SUPPRESSED)`(槽17) / `SetSuppressState`(槽16) / `IZUtility.SetSuppressStateMultiple`(槽16) |
| 更新包围盒 | `OnDragDropResetSizeBox` | `IZSizeBox.Reset(bool)`(槽30) + `PermitResetSizeBox`(槽26)，元素 QI 得 IZSizeBox |
| 全选内部链接零件 | `OnDragDropSelectLinkedElements` | `IZSceneElement.HasInternalLinks`(槽23) / `GetInternallyLinkedElements` / `IZSelectionMgr.AddElementToSelection`(槽20) |

## 界面形态（2026-10-31：与主工具箱同款四列表格）

- 原三行平铺按钮（10 个）改为 `DataGridView` 四列表格：**列 = 分类，单元格 = 工具，单击（或 Enter/Space）执行**，与主工具箱 `MainToolboxForm` 同一套视觉与交互规则（行高 36/表头 38、`WrapMode=False`、列宽按最长文字实测 `MinimumWidth`、窗口尺寸由此推导、悬停高亮 + 手型光标、空单元格浅灰留白）。
- 四列分组（3+3+2+2，共 10 个工具，顺序保持历史习惯）：
  | 列 | 工具 |
  | --- | --- |
  | 1. 特征与内核 | 增料除料切换 / 切换Parasolid内核 / 切换ACIS内核 |
  | 2. 元素处理 | 选中取消压缩 / 更新包围盒 / 全选内部链接零件 |
  | 3. 几何与坐标 | 锚点移至重心 / 获取点坐标 |
  | 4. 数据与BOM | 导入3D曲线坐标数据 / 将零部件添加到BOM |
- 实现要点：
  - 工具清单集中在 `BuildToolGroups()`（`ToolEntry{Title,Tip,Run}`），加新工具 = 加一行 `ToolEntry`，表格自动扩行；单元格 `ToolTipText` 放悬停说明。
  - 长任务统一走 `SetToolsEnabled(bool)` 禁用/恢复表格（替代原来逐按钮 `Enabled`），内核切换分片定时器不受影响；`RunCell()` 里再做一次 `_kernelRunning` 重入拦截。
  - 每次执行前写一行 `---- 执行：<工具名> ----` 便于在 trace 里对齐用户操作。
- 离屏截图验证（`DrawToBitmap`，构造函数传 `null` 站点即可建窗）：4 列 3 行、列头/单元格无折行无遮挡、空单元格浅灰、日志区正常，0 warning 0 error。

