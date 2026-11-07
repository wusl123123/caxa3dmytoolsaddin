# ============================================================
# caxa3dcodenameextraction —— 注册 CRX 桥（图纸环境命令执行模块）
# 作用：把 caxa3dcodenameextraction.crx 拷到每个已装 CAXA 3D 的
#       CAXADraft\bin，并在 Modules\CxAutorun.cx 中幂等追加登记项。
# 特点：只做"缺则追加"，绝不动其它插件/模块的条目（多插件共存铁律）。
# 用法：powershell -ExecutionPolicy Bypass -File caxa3dcodenameextraction-register-crx.ps1
# 供插件界面「注册CRX」按钮与 deploy.ps1 调用（自动提权）。
# ============================================================
param([string]$CaxaRoot = '', [switch]$NoPause)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $extra = ''
    if ($NoPause) { $extra = ' -NoPause' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -CaxaRoot `"$CaxaRoot`"$extra"
    exit
}
$ErrorActionPreference = 'Stop'

$crxName = 'caxa3dcodenameextraction.crx'
$scriptDir = Split-Path -Parent $PSCommandPath

# 源 CRX：安装后的目录（IronCAD\bin\CAXADraft\bin）→ 脚本目录 → C# 编译输出 → crx 工程输出
$candidates = @(
    (Join-Path $scriptDir "CAXADraft\bin\$crxName"),
    (Join-Path $scriptDir $crxName),
    (Join-Path $scriptDir "bin\x64\Release\net48\$crxName"),
    (Join-Path $scriptDir "caxa3dcodenameextraction-crx\bin\$crxName")
)
$crxSrc = $null
foreach ($c in $candidates) { if (Test-Path $c) { $crxSrc = $c; break } }
if (-not $crxSrc) {
    Write-Host "[错误] 找不到 $crxName"
    Write-Host "  已尝试：$($candidates -join ', ')"
    Write-Host "  请先编译 caxa3dcodenameextraction-crx 工程。"
    if (-not $NoPause) { Read-Host '按回车退出' }
    exit 1
}
Write-Host "源 CRX: $crxSrc"

# CxAutorun.cx 是共享清单：读写保留原 BOM，只做"缺则追加"
function Read-CxFile([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $off = 0; $hasBom = $false
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $off = 3; $hasBom = $true }
    [pscustomobject]@{ Text = [Text.Encoding]::UTF8.GetString($bytes, $off, $bytes.Length - $off); HasBom = $hasBom }
}

function Write-CxFile([string]$Path, [string]$Text, [bool]$HasBom) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($HasBom)))
}

function Find-CaxaVersions {
    param([string]$Root)
    $found = @()
    $roots = @()
    if ($Root) { $roots += $Root }
    else {
        $roots += (Join-Path $env:ProgramFiles 'CAXA\CAXA 3D')
        if (${env:ProgramFiles(x86)}) { $roots += (Join-Path ${env:ProgramFiles(x86)} 'CAXA\CAXA 3D') }
    }
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        foreach ($d in (Get-ChildItem $r -Directory -ErrorAction SilentlyContinue)) {
            $bin = Join-Path $d.FullName 'bin'
            $exe = Join-Path $bin 'IronCAD.exe'
            $draft = Join-Path $bin 'CAXADraft'
            if ((Test-Path $exe) -and (Test-Path $draft)) {
                $found += [pscustomobject]@{ Name = $d.Name; Root = $d.FullName; Bin = $bin; Draft = $draft }
            }
        }
        if ($Root -and $found.Count -gt 0) { break }
    }
    return $found
}

$versions = Find-CaxaVersions -Root $CaxaRoot
if (-not $versions -or $versions.Count -eq 0) {
    Write-Host "[错误] 未发现任何已安装的 CAXA 3D。"
    if (-not $NoPause) { Read-Host '按回车退出' }
    exit 1
}
Write-Host "发现 $($versions.Count) 个版本：$((($versions | ForEach-Object { $_.Name })) -join ', ')"

$failed = @()
foreach ($v in $versions) {
    Write-Host "———— CAXA 3D $($v.Name) ————"
    $crxBinDir = Join-Path $v.Draft 'bin'
    $autorun   = Join-Path $v.Draft 'Modules\CxAutorun.cx'
    $crxDst    = Join-Path $crxBinDir $crxName

    try {
        $same = ((Resolve-Path $crxSrc).Path -eq $crxDst)
        if ($same) { Write-Host "  [1/2] 已就位（跳过拷贝）$crxDst" }
        else { Copy-Item $crxSrc $crxDst -Force -ErrorAction Stop; Write-Host "  [1/2] 已拷贝 $crxDst" }
    }
    catch { Write-Host "  [1/2] [警告] 拷贝失败（CAXA 可能正在运行）：$($_.Exception.Message)"; $failed += $v.Name }

    $line = '  <File Template="ID_DFT_TMPL">' + $crxDst + '</File>'
    if (Test-Path $autorun) {
        $cx = Read-CxFile $autorun
        if ($cx.Text -notlike "*$crxDst*") {
            Write-CxFile $autorun ($cx.Text.Replace('</Modules>', $line + [Environment]::NewLine + '</Modules>')) $cx.HasBom
            Write-Host "  [2/2] 已登记 CxAutorun.cx"
        } else {
            Write-Host "  [2/2] CxAutorun.cx 已存在登记项（跳过）"
        }
    } else {
        $cx = @('<?xml version="1.0" encoding="utf-8"?>', '<Modules>', $line, '</Modules>', '') -join [Environment]::NewLine
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $autorun) | Out-Null
        Write-CxFile $autorun $cx $false
        Write-Host "  [2/2] 已新建 CxAutorun.cx 并登记"
    }
}

Write-Host "================ CRX 注册完成 ================"
if ($failed.Count -gt 0) { Write-Host "[注意] 部分版本失败：$($failed -join ', ')（退出 CAXA 后重试）" }
Write-Host "重启 CAXA 3D 后生效。"
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Read-Host '按回车退出' }
