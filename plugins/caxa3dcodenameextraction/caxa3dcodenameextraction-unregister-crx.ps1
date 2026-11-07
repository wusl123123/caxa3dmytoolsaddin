# ============================================================
# caxa3dcodenameextraction —— 卸载 CRX 桥
# 作用：从每个已装 CAXA 3D 的 CAXADraft\bin 删除 caxa3dcodenameextraction.crx，
#       并从 Modules\CxAutorun.cx 中只移除本插件那一行（绝不整体重写共享清单）。
# 用法：powershell -ExecutionPolicy Bypass -File caxa3dcodenameextraction-unregister-crx.ps1
# 供插件界面「卸载CRX」按钮调用（自动提权）。
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

# CxAutorun.cx 是共享清单：读写保留原 BOM，只删自己那一行
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
    Write-Host "[警告] 未发现任何已安装的 CAXA 3D，无需卸载。"
    if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Read-Host '按回车退出' }
    exit 0
}

$hadError = $false
foreach ($v in $versions) {
    Write-Host "———— CAXA 3D $($v.Name) ————"
    $crxDst  = Join-Path $v.Draft "bin\$crxName"
    $autorun = Join-Path $v.Draft 'Modules\CxAutorun.cx'

    if (Test-Path $crxDst) {
        try { Remove-Item $crxDst -Force -ErrorAction Stop; Write-Host "  [1/2] 已删除 $crxDst" }
        catch { Write-Host "  [1/2] 删除失败（CAXA 占用？退出后重试）：$crxDst"; $hadError = $true }
    } else { Write-Host "  [1/2] 不存在（跳过）$crxDst" }

    if (Test-Path $autorun) {
        $cx   = Read-CxFile $autorun
        $line = '  <File Template="ID_DFT_TMPL">' + $crxDst + '</File>'
        if ($cx.Text.Contains($line)) {
            Write-CxFile $autorun ($cx.Text.Replace($line, '')) $cx.HasBom
            Write-Host "  [2/2] 已移除 CxAutorun.cx 中的登记项"
        } elseif ($cx.Text -like "*$crxName*") {
            $kept = @($cx.Text -split "`r?`n" | Where-Object { -not ($_ -like "*$crxName*" -and $_ -like '*<File*') })
            Write-CxFile $autorun ($kept -join [Environment]::NewLine) $cx.HasBom
            Write-Host "  [2/2] 已按行移除 CxAutorun.cx 中的登记项"
        } else {
            Write-Host "  [2/2] CxAutorun.cx 无本插件登记项（跳过）"
        }
    } else { Write-Host "  [2/2] 无 CxAutorun.cx（跳过）" }
}

Write-Host "================ CRX 卸载完成 ================"
if ($hadError) { Write-Host "[注意] 有步骤失败，请退出 CAXA 后重跑本脚本。" }
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { Read-Host '按回车退出' }
