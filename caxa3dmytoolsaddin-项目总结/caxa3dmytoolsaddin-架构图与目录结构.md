# caxa3dmytoolsaddin-架构图与目录结构

## 二、架构与目录

```
caxa3dmytoolsaddin/
├── Lib/                       ICApiAddin.snk + interop.ICApiIronCAD.dll
├── build.ps1                  一键编译 + 汇总插件输出
├── deploy.ps1 / 安装.bat      一键部署（自动提权）
├── restore.ps1 / 卸载.bat     一键卸载（手术式清理）
├── package.ps1 / 打包.bat     无源码交付打包
├── src/
│   ├── Contracts/             契约类库（不引用 CAXA 类型）
│   ├── Host/                  caxa3dmytoolsaddin 主程序
│   └── plugins/               编译产物汇总（主程序运行时扫描）
└── plugins/
    ├── Caxa3dcudeaddin/                  立方体工具
    ├── caxa3dAttributeManageraddin/      自定义属性管理器
    ├── caxa3dcodenameextraction/         带图改名（含 CRX 桥子工程）
    ├── caxa3dsetMaterial/                材料工具
    ├── caxa3dTreeBom/                    树形BOM
    ├── caxa3dminitool/                   小工具合集
    ├── caxa3dsheetmaster/                折弯管理大师（含 CRX 桥子工程）
    ├── caxa3dexbtest/                    图纸环境矩形测试（含 CRX 桥子工程）
    ├── caxa3dapigetaddin/                操作 API 抓取器（含 CRX 桥子工程）
    ├── caxa3dCXDFTViewUpdate/            刷新图纸视图（含 CRX 桥子工程）
    └── caxa3didcskill/                  后台进程清理（DLL 只是启动器，真正干活的是进程外 EXE）

tools/
└── caxa3didcskill/                     独立工具源码：编译出 caxa3didcskill.exe，随包部署
```

运行期布局：

```
C:\Program Files\CAXA\CAXA 3D\2027\bin\
├── caxa3dmytoolsaddin.dll (+ .pdb)
├── Caxa3dMyTools.Contracts.dll
└── caxa3dmytoolsaddin\plugins\<插件名>\<插件名>.dll
    └── caxa3didcskill\   caxa3didcskill.dll（启动器）+ caxa3didcskill.exe（进程外工具）
```

## 七、子项目索引

| 子项目 | 总结 | 当前重点 |
| --- | --- | --- |
| Caxa3dcudeaddin | [Caxa3dcudeaddin-项目总结](plugins/Caxa3dcudeaddin/Caxa3dcudeaddin-项目总结.md) | 原点建块、vtable 槽位、子进程兜底 |
| caxa3dAttributeManageraddin | [caxa3dAttributeManageraddin-项目总结](plugins/caxa3dAttributeManageraddin/caxa3dAttributeManageraddin-项目总结.md) | xlsx 属性表、共享表精确匹配 |
| caxa3dcodenameextraction | [caxa3dcodenameextraction-项目总结](plugins/caxa3dcodenameextraction/caxa3dcodenameextraction-项目总结.md) | 带图改名、CRX 桥、17s 卡顿归因 |
| caxa3didcskill | [caxa3didcskill-项目总结](plugins/caxa3didcskill/caxa3didcskill-项目总结.md) | 独立插件列启动器：进程外 EXE 的发现、单实例前台化、随包部署 |
| caxa3didcskill（工具本体） | [caxa3didcskill-项目总结](tools/caxa3didcskill/caxa3didcskill-项目总结.md) | 独立 EXE：后台进程识别与保护 |
| caxa3dsetMaterial | [caxa3dsetMaterial-项目总结](plugins/caxa3dsetMaterial/caxa3dsetMaterial-项目总结.md) | 材料/密度/颜色、共享表、色卡 |
| caxa3dTreeBom | [caxa3dTreeBom-项目总结](plugins/caxa3dTreeBom/caxa3dTreeBom-项目总结.md) | 树形BOM、STA 线程模型、界面布局 |
| caxa3dapigetaddin | [caxa3dapigetaddin-项目总结](plugins/caxa3dapigetaddin/caxa3dapigetaddin-项目总结.md) | 操作 API 实时抓取、事件 sink、CRX 图纸事件桥 |
| caxa3dCXDFTViewUpdate | [caxa3dCXDFTViewUpdate-项目总结](plugins/caxa3dCXDFTViewUpdate/caxa3dCXDFTViewUpdate-项目总结.md) | OpenFile2 + CRX CLOSEDOC 批处理、弹窗代点、后台化 |
| caxa3dsetbackup | 原独立项目总结 `C:\code\ICAPI\addin\caxa3dsetbackup\caxa3dsetbackup-项目总结.md` | 系统工具列：环境配置备份/还原，仅移植内置插件内核 |

