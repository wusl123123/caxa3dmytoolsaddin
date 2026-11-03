# caxa3dTreeBom-项目定位与需求

> 原总结标题：caxa3dTreeBom 项目总结


> CAXA 3D（IronCAD）树形结构 BOM 工具 · 首次建档：2026-10-28
> 参考产品：`C:\code\SwTreeBom`（界面与其 Form1 完全对齐）

## 一、项目定位

在 CAXA 3D 场景中选中装配体（可多选），递归遍历整棵装配结构，生成带层级编码的树形 BOM：
`1 / 1.1 / 1.1.1 …`，并回填自定义属性、材料、数量/总数量、同名工程图标记、缩略图，最终写出 xlsx。

- 交付形态：`caxa3dTreeBom.dll`（x64 / net48 / 强签名）+ `安装.bat` / `卸载.bat`
- 入口：场景环境功能区按钮「树形BOM」
- 默认表头模板：`caxa3dTreeBomxls.xlsx`
  `层级 | 是否有工程图 | 缩略图 | SC图号 | SC名称 | SC规格 | SC材料 | SC备注 | 数量 | 总数量`
- 默认输出路径：`%APPDATA%\caxa3dTreeBom\caxa3dTreeBomxls.xlsx`（CAXA bin 目录不可写，不能放程序目录）

