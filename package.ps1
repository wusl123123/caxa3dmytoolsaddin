<#
.SYNOPSIS
    打包成“无源码”的交付件：dist\caxa3dmytoolsaddin-<版本>.zip
.DESCRIPTION
    只收集运行期必需文件：
      caxa3dmytoolsaddin.dll            主程序（内嵌 SxS 清单）
      Caxa3dMyTools.Contracts.dll       契约
      plugins\<插件名>\<插件名>.dll      各子插件（编译产物，不含源码）
      plugins\<插件名>\<插件自带资源>    .crx/.txt/.ini/.exe/.exb/.ps1 等，按扩展名白名单收集
                                        （白名单见 $pluginResExt；新增资源类型必须同步，
                                         否则是静默缺件 —— 见下方“缺件校验”段）
      deploy.ps1 / restore.ps1 / 安装.bat / 卸载.bat / README.txt
    不含任何 .cs / .csproj / .sln / .snk / Lib 等源码或签名私钥。
.NOTES
    仓库根目录执行：powershell -ExecutionPolicy Bypass -File package.ps1
#>

param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$dist  = Join-Path $root 'dist'

function Set-SingleBom([string]$path) {
    if (-not (Test-Path $path)) { return }
    $bytes = [IO.File]::ReadAllBytes($path)
    $i = 0
    while (($i + 2) -lt $bytes.Length -and $bytes[$i] -eq 0xEF -and $bytes[$i + 1] -eq 0xBB -and $bytes[$i + 2] -eq 0xBF) { $i += 3 }
    if ($i -eq 3) { return }
    $body = if ($i -gt 0 -and $i -lt $bytes.Length) { [byte[]]$bytes[$i..($bytes.Length - 1)] } elseif ($i -eq 0) { $bytes } else { [byte[]]@() }
    [IO.File]::WriteAllBytes($path, ([byte[]](0xEF, 0xBB, 0xBF) + $body))
}

foreach ($s in @((Join-Path $root 'build.ps1'), (Join-Path $root 'deploy.ps1'), (Join-Path $root 'restore.ps1'))) { Set-SingleBom $s }

if (-not $NoBuild) {
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 失败" }
}

$hostOut  = Join-Path $root 'src\Host\bin\x64\Release\net48'
$hostDll  = Join-Path $hostOut 'caxa3dmytoolsaddin.dll'
$contract = Join-Path $hostOut 'Caxa3dMyTools.Contracts.dll'
$pluginSrc = Join-Path $root 'src\plugins'

foreach ($need in @($hostDll, $contract, $pluginSrc)) {
    if (-not (Test-Path $need)) { throw "缺少构建产物：$need" }
}

# ---------- 缺件校验：plugins\ 下的每个插件工程都必须已编译并暂存到 src\plugins ----------
# build.ps1 的 plugins 清单是手写的：新插件漏登记时既不编译也不暂存，
# package.ps1 只会打包 src\plugins 里已有的东西 —— 交付件静默缺件、全程 0 错误（规则坑 80）。
$pluginRoot = Join-Path $root 'plugins'
$pluginDirs = @(Get-ChildItem $pluginRoot -Directory | Where-Object {
    @(Get-ChildItem $_.FullName -Filter '*.csproj' -File).Count -gt 0 })
$missing = @()
foreach ($dir in $pluginDirs) {
    # Host 按「目录名 = 程序集名」加载，所以暂存目录里的同名 dll 必须存在
    $stagedDll = Join-Path (Join-Path $pluginSrc $dir.Name) ($dir.Name + '.dll')
    if (-not (Test-Path $stagedDll)) { $missing += $dir.Name }
}
if ($missing.Count -gt 0) {
    throw ("打包中止：以下插件工程未进入 src\plugins（先在 build.ps1 的 plugins 清单里登记再重跑 build）：" +
           [Environment]::NewLine + "  " + ($missing -join ([Environment]::NewLine + "  ")))
}
Write-Host ("缺件校验通过：{0} 个插件工程已全部暂存" -f $pluginDirs.Count) -ForegroundColor Green

# ---------- 独立插件（后台进程清理）缺件校验 ----------
# 「2. 独立插件」列 = 启动器 DLL + 进程外工具 EXE；少了 EXE 时界面照样有按钮，
# 但点击必定报错 —— 与其上线后才发现，不如打包时直接中止。
$idcDir = Join-Path $pluginSrc 'caxa3didcskill'
foreach ($pair in @(
        @{ Name = 'caxa3didcskill.dll'; Path = (Join-Path $idcDir 'caxa3didcskill.dll') },
        @{ Name = 'caxa3didcskill.exe'; Path = (Join-Path $idcDir 'caxa3didcskill.exe') })) {
    if (-not (Test-Path $pair.Path)) {
        throw ("打包中止：独立插件缺少 {0}（先编译 tools\caxa3didcskill 并重跑 build.ps1）：{1}" -f $pair.Name, $pair.Path)
    }
}
Write-Host "缺件校验通过：独立插件 caxa3didcskill（启动器 DLL + 工具 EXE）" -ForegroundColor Green

# ---------- 插件自带资源缺件校验（不算扩展名白名单，逐个点名） ----------
# 「图纸环境预热」的载体是一张真图纸：plugins\caxa3dcodenameextraction\caxa3d图纸环境预热.exb。
# 它被漏掉时功能不会报错，只会永远回落成“请人工开一张图纸”，属于最难查的静默降级。
$requiredRes = @(
    @{ Plugin = 'caxa3dcodenameextraction'; File = 'caxa3d图纸环境预热.exb'; Why = '图纸环境预热的载体图纸' },
    @{ Plugin = 'caxa3dcodenameextraction'; File = 'ExbLinkPatch.exe';          Why = 'exb 外链就地改写工具' },
    @{ Plugin = 'caxa3dsetMaterial';        File = 'caxa3dsetMaterialaddfix.txt'; Why = '材料属性回填规则模板（缺席时 BackfillRule 静默回落到内置 DefaultText，改项目内模板不生效）' },
    # 批量大师：三件缺一都会静默降级（CRX 缺席＝「刷新工程图」永远等不到图纸环境应答；
    # 表格缺席＝首次使用没有默认列；预热图纸缺席＝图纸环境只能回落人工开图）
    @{ Plugin = 'caxa3dBatchMaster';        File = 'caxa3dBatchMasterView.crx';      Why = '图纸环境 CRX 桥（第 5 登记点）' },
    @{ Plugin = 'caxa3dBatchMaster';        File = 'caxa3dBatchMaster属性表格.xlsx';  Why = '默认属性表格（首次使用的列定义）' },
    @{ Plugin = 'caxa3dBatchMaster';        File = 'caxa3d图纸环境预热.exb';          Why = '图纸环境预热载体（全仓唯一那份，Link 引用）' }
)
foreach ($r in $requiredRes) {
    $path = Join-Path (Join-Path $pluginSrc $r.Plugin) $r.File
    if (-not (Test-Path $path)) {
        throw ("打包中止：{0} 缺少自带资源 {1}（{2}）——检查 build.ps1 里该插件的 Extra 是否含对应扩展名：{3}" -f
               $r.Plugin, $r.File, $r.Why, $path)
    }
}
Write-Host "缺件校验通过：插件自带资源（预热图纸 .exb 等）" -ForegroundColor Green

# 版本与主窗口标题一致：主程序 DLL 写入时间 yyyyMMddHHmm（编译即自动更新）
$version = (Get-Item $hostDll).LastWriteTime.ToString('yyyyMMddHHmm')
$name    = "caxa3dmytoolsaddin-$version"
$stage   = Join-Path $dist $name
$zip     = Join-Path $dist "$name.zip"

# 暂存目录必须每次都是干净的：删不掉时**换一个唯一名字**，绝不在旧目录上追加，
# 否则 Compress-Archive 会把上一轮的残留文件一起打进交付件。
# （受管环境下一次删几十个文件会触发 safe-delete 阈值而失败，属于本机策略，不代表脏数据。）
if (Test-Path $stage) {
    try { Remove-Item $stage -Recurse -Force -ErrorAction Stop }
    catch {
        $stamp = Get-Date -Format 'HHmmss'
        $stage = "$stage-$stamp"
        Write-Host "旧暂存目录无法清理（批量删除受限），本轮改用新目录：$stage" -ForegroundColor Yellow
    }
}
New-Item -ItemType Directory -Path (Join-Path $stage 'plugins') -Force | Out-Null

# ---------- 主程序 + 契约（不含 pdb） ----------
Copy-Item $hostDll  $stage -Force
Copy-Item $contract $stage -Force

# ---------- 子插件（每个插件一个子目录，只取运行期文件） ----------
# 按扩展名白名单收集：**插件新增自带资源类型时必须同步这里**（.pdb 除外），
# 漏一个就是静默缺件 —— 交付件里少个文件、build 与打包全程 0 错误，
# 装上后表现为“某个功能永远走回落路径”（例如缺 caxa3d图纸环境预热.exb 时，
# 「图纸环境预热」只能回落成人工开图，日志里还看不出是缺件）。
$pluginResExt = @('.dll', '.xlsx', '.json', '.config', '.crx', '.txt', '.ini', '.exe', '.ps1', '.xml', '.info', '.exb')
foreach ($p in @(Get-ChildItem $pluginSrc -Directory)) {
    $dst = Join-Path (Join-Path $stage 'plugins') $p.Name
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    Get-ChildItem $p.FullName -File | Where-Object { $pluginResExt -contains $_.Extension.ToLower() } |
        Copy-Item -Destination $dst -Force
}

# ---------- 安装/卸载脚本 ----------
foreach ($f in @('deploy.ps1', 'restore.ps1', '安装.bat', '卸载.bat')) {
    Copy-Item (Join-Path $root $f) $stage -Force
}

# ---------- README（纯文本，UTF-8 BOM，任何编辑器都能正确显示中文） ----------
$readme = @"
caxa3dmytoolsaddin $version
==========================================
CAXA 3D 插件集合体（主程序 + 全部子插件，交付件不含源码）

安装
  1. 完全退出 CAXA 3D（含后台无窗口进程 IRONCAD Design Collaboration Suite）
  2. 双击 安装.bat，按提示同意管理员权限
  3. 启动 CAXA 3D，功能区会出现「我的工具箱」按钮，点击后在弹出窗口中选择各子插件

卸载
  双击 卸载.bat。若提示文件被占用，请完全退出 CAXA 3D 后重试。

日志
  %TEMP%\caxa3dmytoolsaddin_trace.log

说明
  - 本目录所有 .ps1 必须保持单个 UTF-8 BOM（bat 已自动校正）
  - 子插件放在 plugins\<插件名>\ 下，主程序启动时自动扫描加载
  - 「2. 独立插件」列的后台进程清理是进程外工具：plugins\caxa3didcskill\caxa3didcskill.exe，
    点击后以独立进程运行，只清理无可见窗口的 IRONCAD 残留进程（不杀带窗口的前台实例）
"@
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

# ---------- 暂存件端到端校验：白名单必须真的把自带资源收进来 ----------
# 只查 src\plugins 有文件还不够 —— 扩展名白名单没命中时源有、stage 没有，
# 所以打包前在这里再验一次最终交付目录。
foreach ($r in $requiredRes) {
    $staged = Join-Path (Join-Path (Join-Path $stage 'plugins') $r.Plugin) $r.File
    if (-not (Test-Path $staged)) {
        throw ("打包中止：{0} 没被收进交付件（扩展名白名单 `$pluginResExt 未包含 .{1}）：{2}" -f
               $r.File, ([IO.Path]::GetExtension($r.File).TrimStart('.')), $staged)
    }
}
Write-Host "暂存校验通过：自带资源已进入交付目录" -ForegroundColor Green

# ---------- 源码泄漏检查 ----------
$forbidden = @('.cs', '.csproj', '.sln', '.snk', '.resx', '.props', '.targets', '.suo', '.user')
$leaks = @(Get-ChildItem $stage -Recurse -File | Where-Object { $forbidden -contains $_.Extension.ToLower() })
if ($leaks.Count -gt 0) {
    $leaks | ForEach-Object { Write-Host "源码泄漏：$($_.FullName)" -ForegroundColor Red }
    throw "打包中止：交付件中混入了源码/签名文件"
}

# ---------- 压缩 ----------
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
try { Remove-Item $stage -Recurse -Force -ErrorAction Stop }
catch { Write-Host ("提示：暂存目录未清理，可手工删除：{0}" -f $stage) -ForegroundColor Yellow }

Write-Host ""
Write-Host "打包完成：$zip" -ForegroundColor Green
Write-Host "交付内容（解压后）："
Write-Host "  caxa3dmytoolsaddin.dll"
Write-Host "  Caxa3dMyTools.Contracts.dll"
Write-Host "  plugins\<插件名>\<插件名>.dll  (+ 插件自带资源，含 caxa3d图纸环境预热.exb)"
Write-Host "  plugins\caxa3didcskill\caxa3didcskill.exe  (独立插件列的后台进程清理工具)"
Write-Host "  deploy.ps1 / restore.ps1 / 安装.bat / 卸载.bat / README.txt"
