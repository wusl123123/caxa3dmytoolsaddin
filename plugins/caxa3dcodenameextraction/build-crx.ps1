# 重编译图纸桥 CRX（caxa3dcodenameextraction.crx）
#
# 为什么单独一条脚本：C# 侧（关图按名匹配、CLOSERAW、ChangeLinks 命令名）依赖 CRX 里的新逻辑，
# 而 CRX 是 C++ 工程（v143），只能用 VS 的 MSBuild 编译，dotnet CLI 建不动 vcxproj。
# 用法：双击同目录「重编CRX.bat」，或 powershell -ExecutionPolicy Bypass -File build-crx.ps1
#
# 编译成功后会做三件事：
#   1. 把 .crx 拷到插件输出目录 bin\x64\Release\net48（csproj 的资源项从这里取）
#   2. 若集合体已暂存过（src\plugins\<插件Id>\ 存在），同步覆盖一份
#   3. 打印产物大小与 SHA256，便于回传核对

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$crxProj = Join-Path $root 'caxa3dcodenameextraction-crx\caxa3dcodenameextraction.vcxproj'
$crxOut = Join-Path $root 'caxa3dcodenameextraction-crx\bin'
$crxFile = Join-Path $crxOut 'caxa3dcodenameextraction.crx'

function Find-MsBuild {
    $candidates = @()
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $inst = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($inst) { $candidates += (Join-Path $inst 'MSBuild\Current\Bin\MSBuild.exe') }
    }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $base) { continue }
        $vsRoot = Join-Path $base 'Microsoft Visual Studio'
        if (-not (Test-Path $vsRoot)) { continue }
        Get-ChildItem $vsRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            foreach ($ed in @('BuildTools', 'Community', 'Professional', 'Enterprise', 'Preview')) {
                $candidates += (Join-Path $_.FullName "$ed\MSBuild\Current\Bin\MSBuild.exe")
            }
        }
    }
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

if (-not (Test-Path $crxProj)) { Write-Host "[ERROR] 找不到工程：$crxProj" -ForegroundColor Red; exit 1 }

# CL 任务构造命令行时用的是 ProcessStartInfo.EnvironmentVariables（StringDictionary，键按大小写不敏感去重）。
# 本机同时存在 https_proxy 与 HTTPS_PROXY 这类只差大小写的变量时，它会抛
#   MSB6001 System.ArgumentException: 已添加项。字典中的关键字:“https_proxy”所添加的关键字:“HTTPS_PROXY”
# 与代码无关。这里把小写那批代理变量摘掉（MSBuild 只用大写），编译即可继续。
foreach ($n in @('http_proxy', 'https_proxy', 'all_proxy', 'no_proxy')) {
    if (Test-Path "env:$n") { Remove-Item "env:$n" -ErrorAction SilentlyContinue }
}

$msb = Find-MsBuild
if (-not $msb) {
    Write-Host '[ERROR] 本机找不到 Visual Studio 的 MSBuild（需要 v143 生成工具）。' -ForegroundColor Red
    Write-Host '        装 “Visual Studio 2022 生成工具 + 使用 C++ 的桌面开发” 后再跑本脚本。'
    exit 1
}

Write-Host "MSBuild: $msb"
Write-Host '=== [1/2] 编译 CRX ===' -ForegroundColor Cyan
& $msb $crxProj /p:Configuration=Release /p:Platform=x64 /t:Rebuild /v:minimal /nologo
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $crxFile)) {
    Write-Host '[ERROR] CRX 编译失败，看上面的编译错误。' -ForegroundColor Red
    exit 1
}

Write-Host '=== [2/2] 同步产物 ===' -ForegroundColor Cyan
$targets = @(
    (Join-Path $root 'bin\x64\Release\net48')
)
$staged = Join-Path $root '..\..\src\plugins\caxa3dcodenameextraction'
if (Test-Path $staged) { $targets += $staged }
$targets += (Join-Path $root 'bin\Release\net48')

foreach ($t in $targets) {
    if (-not (Test-Path $t)) { continue }
    Copy-Item $crxFile (Join-Path $t 'caxa3dcodenameextraction.crx') -Force
    Write-Host ("  已覆盖 {0}" -f (Join-Path $t 'caxa3dcodenameextraction.crx'))
}

$fi = Get-Item $crxFile
$sha = (Get-FileHash $crxFile -Algorithm SHA256).Hash
Write-Host ''
Write-Host ("CRX 完成：{0} B  SHA256={1}" -f $fi.Length, $sha) -ForegroundColor Green
Write-Host '下一步：跑仓库根的 build.ps1 再装（或直接 安装.bat，若已部署则重复一次覆盖）'
