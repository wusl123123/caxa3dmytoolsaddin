# ============================================================
# caxa3dCXDFTViewUpdate —— 卸载 CRX 桥
# 作用：从每个已装 CAXA 3D 的 CAXADraft\bin 删除 caxa3dCXDFTViewUpdate.crx，
#       并从 Modules\CxAutorun.cx 中只移除本插件那一行（绝不整体重写共享清单）。
# 用法：powershell -ExecutionPolicy Bypass -File unregister-crx.ps1
# 供插件界面「卸载CRX」按钮调用（自动提权）。
# ============================================================
param([string]$CaxaRoot = '')

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -CaxaRoot `"$CaxaRoot`""
    exit
}
$ErrorActionPreference = 'Stop'

$crxName = 'caxa3dCXDFTViewUpdate.crx'

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
    if ($Host.Name -eq 'ConsoleHost') { Read-Host '按回车退出' }
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
        $cx = [IO.File]::ReadAllText($autorun)
        $line = '  <File Template="ID_DFT_TMPL">' + $crxDst + '</File>'
        if ($cx.Contains($line)) {
            [IO.File]::WriteAllText($autorun, $cx.Replace($line, ''))
            Write-Host "  [2/2] 已移除 CxAutorun.cx 中的登记项"
        } else {
            Write-Host "  [2/2] CxAutorun.cx 无本插件登记项（跳过）"
        }
    } else { Write-Host "  [2/2] 无 CxAutorun.cx（跳过）" }
}

Write-Host "================ CRX 卸载完成 ================"
if ($hadError) { Write-Host "[注意] 有步骤失败，请退出 CAXA 后重跑本脚本。" }
if ($Host.Name -eq 'ConsoleHost') { Read-Host '按回车退出' }
