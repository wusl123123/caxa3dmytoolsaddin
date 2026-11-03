# caxa3dminitool-交付状态与验证结果

## 技术突破点 / 新验证的 API 用途

- `IZFeature.OperationType` 读写可用：`get_`/`put_OperationType`，`Z_UNITE=1`、`Z_SUBTRACT=2`（与需求文档实测值一致）；切换后必须 `IZPart.Update()` 才反映到几何。
- `IZElement.GetOwnerPart()` 返回 `IZElement`，QI 到 `IZPart` 即可拿到所属零件，用于批量重建（同一零件只 Update 一次）。
- `IZPart.GetExternallyLinkedInfo(out bool)` 返回外链路径，可用于选中状态里区分「虚拟 / 外部链接」。
- `IZSceneDoc.SelectionMgr.GetSelectedElementsZArray()` 在部分上下文取不到真实选中项，`GetTrueSelectedElements()` 作为兜底（两者都做过空集判断）。
- 特征类型白名单（`eZElementType`）覆盖拉伸/旋转/扫描/放样/抽壳/圆角/倒角/布尔/曲面/钣金；不在白名单的元素直接提示「不是可切换的特征」，避免无意义写入。
- 内核 API：`IZPart.KernelType`（槽 get=11 / put=12，`eZKernelType`：`NOKERNEL=0, ACIS=1, PARASOLID=2, LITE=3`）；新建零件默认内核走 `IZBaseAppOptions.Parts` → `IZPartsOptions.DefaultKernelType`（get=11 / put=12）。官方样例 `ModelingHelperSupport.cpp::ConvertElementKernel` 即 `put_KernelType` + 递归装配 + 最后 `IZSceneDoc.Update()`。
- CAXA 内核切换 UI 的实际承载者是内置 Add-in `SwitchKernel.dll`（`IronCad.Addin.config` 里 `system=true`，HKCU `Applications\Switch Kernel`），其命令名（`SwitchKernel.All.PK/.AK`、`SwitchKernel.Sel.ACIS`、`SwitchKernel.Selected.PK`、`SwitchKernel.Suppress.*`、`SwitchKernel.loadAllPart`）只存在于 DLL 字符串里，命令表查不到 → 只能走 API。

## 部署说明

1. 双击 `C:\code\ICAPI\caxa3dminitool\bin\x64\Release\net48\安装.bat`（会自动 UAC 提权，脚本会自我复制到 `%ProgramData%\caxa3dminitool` 再运行）。
2. 启动 CAXA 3D，在功能区看到「小工具合集」按钮；点开窗口后布局：顶部「窗口前置」+ 选中状态条，中部为四列工具表格（单击单元格执行），下部日志。
3. 日志：`%TEMP%\caxa3dminitool_trace.log`。
4. 卸载双击 `卸载.bat`，会删除 DLL/PDB、配置节点、清单依赖与注册表键，并打印残留检查。

