# caxa3dAttributeManageraddin-架构图与目录结构

## 三、目录结构

```
caxa3dAttributeManageraddin/
├─ Addin.cs                     Add-in 入口 + 功能区按钮
├─ CaxaAutomation.cs            进程内会话 + vtable 直调读写
├─ MainForm.cs / .Designer.cs   工具主窗口（属性表 UI）
├─ Diagnostics.cs               trace 日志（%TEMP%）
├─ ConvertImage.cs              Image → IPictureDisp（按钮图标）
├─ XlsxLite.cs                  xlsx 零依赖读写
├─ caxa3dAttributeManageraddin.csproj
├─ deploy.ps1 / restore.ps1 / strip-manifest.ps1
├─ 安装.bat / 卸载.bat
├─ Icons\  Lib\（ICApiAddin.snk、interop.ICApiIronCAD.dll）
└─ caxa3dManagermodle.xlsx     默认表格模板
```

