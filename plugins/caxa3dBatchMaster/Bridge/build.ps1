# caxa3dBatchMasterView 编译脚本（Release x64 -> .crx）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File "C:\code\ICAPI\caxa3dBatchMaster\Bridge\build.ps1"
param(
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'caxa3dBatchMasterView.vcxproj'

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $p = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
        if ($p) { return @($p)[0] }
    }
    foreach ($c in @(
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe')) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

$msb = Find-MSBuild
if (-not $msb) { Write-Host '[ERROR] MSBuild not found (VS C++ workload required)' -ForegroundColor Red; exit 1 }

Write-Host "MSBuild: $msb"
& $msb $proj /p:Configuration=Release /p:Platform=x64 /t:Rebuild /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { Write-Host '[ERROR] CRX build failed' -ForegroundColor Red; exit $LASTEXITCODE }

$out = Join-Path $root 'bin\caxa3dBatchMasterView.crx'
if (-not (Test-Path $out)) { Write-Host '[ERROR] bin\caxa3dBatchMasterView.crx not produced' -ForegroundColor Red; exit 1 }
$item = Get-Item $out
Write-Host ("[OK] {0}  {1} B" -f $out, $item.Length) -ForegroundColor Green
$sha = (Get-FileHash -Algorithm SHA256 $out).Hash
Write-Host ("[SHA256] {0}" -f $sha) -ForegroundColor Green
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { }
