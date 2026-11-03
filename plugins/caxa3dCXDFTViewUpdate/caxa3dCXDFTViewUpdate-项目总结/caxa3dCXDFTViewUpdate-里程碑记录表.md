# caxa3dCXDFTViewUpdate-里程碑记录表

## 二、里程碑记录表

| 时间 | 内容 |
| --- | --- |
| 2026-10-22~25 | （原独立项目）POC 17 轮攻关定下 OpenFile2 + CRX 桥链路；SendKeys 方案在主项目失败并回滚，实证「addin 进程内发按键」结构性不可行 |
| 2026-11-01 | 迁入集合体：入口换 `IPlugin`、去 Ribbon/COM/清单；CRX 注册从「在进程内直接写 Program Files」改为调用随包 `register-crx.ps1`（自提权）；`build.ps1` 加第 11 项（`Extra=.crx/.ps1`），`deploy.ps1` 的 CRX 步骤由单插件硬编码改为 `$crxItems` 列表，`restore.ps1` 三处登记（legacy 数组 / `$crxNames` / 残留检查） |
| 2026-11-01 | 离线取证全部通过：插件 0 warning 0 error、无内嵌清单、`sn -vf` 有效、反射冒烟 `Id/DisplayName/Category` 正确、槽位检查与基线一致（174/OK 124/MISMATCH 3）、假根（3098/3099）安装+卸载幂等且零残留。证据归档 `C:\code\ICAPI\_probe-tools\out\caxa3dCXDFTViewUpdate\` |

