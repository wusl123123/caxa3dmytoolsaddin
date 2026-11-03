# caxa3dcodenameextraction-项目定位与需求

> 原总结标题：caxa3dcodenameextraction-项目总结


## 1. 项目定位
CAXA 3D 2027（x64）内置 Add-in：**带图改名及图号分离**。
选中零部件 → 输入新零件名 → 按规则拆分图号/名称等 → 回填自定义属性 → 保存 ics →
打开同目录同名 exb 并自动确认更新 → 保存并关闭图纸 → 焦点回到设计环境。

技术栈：C# / .NET Framework 4.8 / WinForms / x64；图纸环境操作由 CRX 桥（C++，crxrxEntryPoint）完成。

