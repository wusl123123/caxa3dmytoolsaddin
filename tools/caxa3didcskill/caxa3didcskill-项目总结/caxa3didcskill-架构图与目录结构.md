# caxa3didcskill-架构图与目录结构

## 2. 形态与迁移（2026-10-31）

- **现行形态**：独立 WinForms EXE，源码 `tools\caxa3didcskill\`，不实现 `IPlugin`、不由 Host 进程内加载。界面入口由 `plugins\caxa3didcskill\` 的**启动器插件**提供（工具箱「2. 独立插件」列）；EXE 随安装包部署到 `<CAXA bin>\caxa3dmytoolsaddin\plugins\caxa3didcskill\`，与启动器 DLL 同目录。
- **迁移原因**：规则 16.6 / 第 17 节明确「进程清理只用于独立 EXE/外部脚本；进程内 addin 不需要也不应做」。原实现在 CAXA 进程内杀同名进程存在自杀风险，且规则要求删除。
- **迁移动作**：
  - `plugins\caxa3didcskill` → `tools\caxa3didcskill`（git 保留历史）；删除 `Plugin.cs`（不再实现 `IPlugin`）、`Lib\`、自身强签名与 `Contracts` 引用。
  - `build.ps1` 的 `$plugins` 删除该项；`src\plugins\caxa3didcskill` 已清理。
  - 部署侧无需改脚本：`deploy.ps1` 每次部署会先删掉整个 `plugins\` 目标目录再复制当前 8 个插件，旧插件在下次 `安装.bat` 时消失。
  - `AGENTS.md`、`README.md`、`caxa3dmytoolsaddin-项目总结.md` 同步为「8 个子插件 + 1 个独立工具」。
- **接回界面（同日后续）**：迁出后「2. 独立插件」列一直是「（暂无插件）」，工具在界面里彻底消失。新增 `plugins\caxa3didcskill\` 启动器插件（`Category=Independent`，只拉起本 EXE、单实例前台化），`build/deploy/restore/package` 四脚本同步管理 EXE；详见 [plugins\caxa3didcskill\caxa3didcskill-项目总结.md](../../plugins/caxa3didcskill/caxa3didcskill-项目总结.md)。

