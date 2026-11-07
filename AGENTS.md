# AGENTS.md — caxa3dmytoolsaddin

CAXA 3D 集合体 Add-in（.NET Framework 4.8 / x64 / WinForms / C#）。

本文件只约定**项目合并与发布（同步到远端仓库）时的工作流程**。开发规范、构建命令、API 契约与踩坑记录不在本仓库，见本地规则目录（`C:\code\ICAPI\00-caxa3d-csharp-二次开发规则\`）与各项目本地总结文档。

---

## 1. 仓库与分支

| 远端 | 地址 | 分支 |
| --- | --- | --- |
| `origin`（Gitee） | https://gitee.com/px408231928/caxa3dmytoolsaddin | `master` |
| `github` | https://github.com/wusl123123/caxa3dmytoolsaddin | `main` |

- 日常开发与提交都在 `master`；`main` 只由 `master` 合并而来，合并后两分支文件树必须完全一致。
- 合并固定动作：`git checkout main` → `git merge master` → `git diff --stat master main` 确认无差异。

---

## 2. 推送

**Gitee**（git 协议直推）：

```bash
git push origin master
```

**GitHub**（`github.com` 的 git 协议在本环境不可用，统一走 Git Data API 脚本）：

```powershell
$env:GITHUB_TOKEN = "<fine-grained PAT，需 Contents: Read and write>"
python gh_api_push.py
```

脚本自动完成：读取远端 `main` 当前 SHA 作父提交 → 只为与远端有差异的文件创建 blob（其余按本地 SHA-1 直接引用）→ 从零建全量树 → 建提交 → 更新 `main` 引用。

- 推送后校验：Gitee 用 `git ls-remote origin refs/heads/master`；GitHub 用 API 比对远端树 SHA 与本地 `git rev-parse main^{tree}`。
- 令牌只在当次命令 / 环境变量中使用，不写入 `.git/config` 或任何配置文件；用过的令牌建议在平台上删除重建。
- GitHub 细粒度令牌走 git HTTPS 时用户名必须用**账号名**（`x-access-token` 会 Authentication failed）；本环境直接走 API 脚本，无此问题。

---

## 3. 不入库内容（.gitignore 已固化）

| 规则 | 说明 |
| --- | --- |
| `*项目总结*` | 本项目、各子插件与 tools 下的总结文档**只保留本地**，永不提交/上传。让已有文件退出仓库只能 `git rm --cached` 在原分支提交；**禁止靠跨分支合并落实删除**——会把磁盘上的文档一起删掉 |
| `*.snk` / `*.pfx` | 强名称私钥不入库，克隆后需自备 `Lib\ICApiAddin.snk` 及各插件 `Lib\` 副本（见 README 第四节） |
| `bin/ obj/ dist/ src/plugins/` | 构建产物 |
| `*.crx` | CRX 桥编译产物，仓库只保留 C++ 源码 |
| `.screenshots/` | 本地截图 |

---

## 4. 提交与发布核对单

1. `git status` 中不得出现：`.snk`、`bin/`、`obj/`、`项目总结`、`.crx`、`.screenshots/`。
2. 提交信息用 `feat:` / `fix:` / `chore:` 前缀，正文列要点。
3. 顺序：提交 `master` → 推 Gitee 并校验 → 合并进 `main` → API 推 GitHub 并校验 → 两边文件数一致。
