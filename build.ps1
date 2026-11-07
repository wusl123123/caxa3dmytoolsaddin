<#
.SYNOPSIS
    一键编译：契约 -> 子插件 -> 主程序，并把子插件输出汇总到 src\plugins。
.NOTES
    在仓库根目录执行：powershell -ExecutionPolicy Bypass -File build.ps1
#>

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# PowerShell 5.1 对无 BOM 的 UTF-8 按 GBK 解码，中文脚本会静默崩溃。
# 每次编译前把仓库内脚本统一成“单个 UTF-8 BOM”。
function Set-SingleBom([string]$path) {
    if (-not (Test-Path $path)) { return }
    $bytes = [IO.File]::ReadAllBytes($path)
    $i = 0
    while (($i + 2) -lt $bytes.Length -and $bytes[$i] -eq 0xEF -and $bytes[$i + 1] -eq 0xBB -and $bytes[$i + 2] -eq 0xBF) { $i += 3 }
    if ($i -eq 3) { return }
    $body = if ($i -gt 0 -and $i -lt $bytes.Length) { [byte[]]$bytes[$i..($bytes.Length - 1)] } elseif ($i -eq 0) { $bytes } else { [byte[]]@() }
    [IO.File]::WriteAllBytes($path, ([byte[]](0xEF, 0xBB, 0xBF) + $body))
}

foreach ($s in @(
        (Join-Path $root 'build.ps1'),
        (Join-Path $root 'deploy.ps1'),
        (Join-Path $root 'restore.ps1'),
        (Join-Path $root 'package.ps1'),
        (Join-Path $root 'src\Host\strip-manifest.ps1'))) {
    Set-SingleBom $s
}

$contracts = Join-Path $root 'src\Contracts\Contracts.csproj'
$hostProj  = Join-Path $root 'src\Host\caxa3dmytoolsaddin.csproj'
$stage     = Join-Path $root 'src\plugins'

# 独立工具（进程外 EXE，规则 16.6）：由「独立插件」列的启动器 DLL 拉起，随安装包一起部署
$idcToolProj = Join-Path $root 'tools\caxa3didcskill\caxa3didcskill.csproj'
$idcToolExe  = Join-Path $root 'tools\caxa3didcskill\bin\x64\Release\net48\caxa3didcskill.exe'

# 子插件：项目文件 + 暂存目录名（与插件 Id 一致）
$plugins = @(
    @{ Name = 'caxa3dAttributeManageraddin'; Project = (Join-Path $root 'plugins\caxa3dAttributeManageraddin\caxa3dAttributeManageraddin.csproj') },
    @{ Name = 'Caxa3dcudeaddin';             Project = (Join-Path $root 'plugins\Caxa3dcudeaddin\Caxa3dcudeaddin.csproj') },
    # 操作 API 抓取器：除 DLL 外还要带图纸环境 CRX 桥接模块
    @{ Name = 'caxa3dapigetaddin';           Project = (Join-Path $root 'plugins\caxa3dapigetaddin\caxa3dapigetaddin.csproj');
       Extra = @('.crx') },
    # 带图改名及图号分离：除 DLL 外还要带规则/说明/控件基线/CRX 注册脚本/CRX 桥本体，
    # 以及图纸环境预热用的载体图纸 .exb（缺它冷启动预热直接失效，见插件总结「图纸环境预热」）
    @{ Name = 'caxa3dcodenameextraction';    Project = (Join-Path $root 'plugins\caxa3dcodenameextraction\caxa3dcodenameextraction.csproj');
       Extra = @('.crx', '.txt', '.ini', '.exe', '.ps1', '.exb') },
    # 材料工具：需带色卡.xml 与资料库种子 自定义材料.xml，以及回填规则模板 caxa3dsetMaterialaddfix.txt
    # （模板缺席时 BackfillRule 会静默回落到内置 DefaultText，改了项目内模板也不会生效）
    @{ Name = 'caxa3dsetMaterial';           Project = (Join-Path $root 'plugins\caxa3dsetMaterial\caxa3dsetMaterial.csproj');
       Extra = @('.xml', '.txt') },
    # 刷新图纸视图：除 DLL 外还要带图纸环境 CRX 桥本体与 CRX 注册/卸载脚本
    @{ Name = 'caxa3dCXDFTViewUpdate';       Project = (Join-Path $root 'plugins\caxa3dCXDFTViewUpdate\caxa3dCXDFTViewUpdate.csproj');
       Extra = @('.crx', '.ps1') },
    # 树形BOM：需带默认表头模板 .xlsx 与合并规则种子 .txt
    @{ Name = 'caxa3dTreeBom';               Project = (Join-Path $root 'plugins\caxa3dTreeBom\caxa3dTreeBom.csproj');
       Extra = @('.txt') },
    # 小工具合集：需带坐标数据示例 .txt，以及「删除所有布局(除模型)」用的图纸环境 CRX 桥 .crx
    @{ Name = 'caxa3dminitool';              Project = (Join-Path $root 'plugins\caxa3dminitool\caxa3dminitool.csproj');
       Extra = @('.txt', '.crx') },
    # 折弯管理大师：需带使用说明 .txt
    @{ Name = 'caxa3dsheetmaster';           Project = (Join-Path $root 'plugins\caxa3dsheetmaster\caxa3dsheetmaster.csproj');
       Extra = @('.txt') },
    # 工程图技术要求备份（系统工具列）：纯文件 + 注册表操作，无自带资源，只有 DLL
    @{ Name = 'caxa3dexbTechSpecsbak';       Project = (Join-Path $root 'plugins\caxa3dexbTechSpecsbak\caxa3dexbTechSpecsbak.csproj') },
    # 批量大师（常用列）：需带默认属性表格 .xlsx、两张用户规则 .txt、图纸环境 CRX 桥
    # caxa3dBatchMasterView.crx（第 5 登记点，缺它「刷新工程图」整链不可用）与预热图纸 .exb
    @{ Name = 'caxa3dBatchMaster';           Project = (Join-Path $root 'plugins\caxa3dBatchMaster\caxa3dBatchMaster.csproj');
       Extra = @('.txt', '.crx', '.exb') },
    # 后台进程清理（独立插件列）：DLL 只是启动器，真正干活的是随包部署的 caxa3didcskill.exe
    @{ Name = 'caxa3didcskill';              Project = (Join-Path $root 'plugins\caxa3didcskill\caxa3didcskill.csproj');
       Extra = @('.exe'); ExtraSrc = @($idcToolExe) }
)

function Build-Project([string]$project) {
    Write-Host ""
    Write-Host "==== 编译 $(Split-Path -Leaf $project) ====" -ForegroundColor Cyan
    & dotnet build $project -c Release -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "编译失败：$project" }
}

Build-Project $contracts
# 先编译独立工具 EXE：启动器插件编译后会把 EXE 一起拷进自己的输出目录
Build-Project $idcToolProj
foreach ($p in $plugins) { Build-Project $p.Project }
Build-Project $hostProj

# ---------- 汇总插件输出 ----------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

foreach ($p in $plugins) {
    $out = Join-Path (Split-Path -Parent $p.Project) 'bin\x64\Release\net48'
    if (-not (Test-Path $out)) { throw "找不到插件输出目录：$out" }

    $dst = Join-Path $stage $p.Name
    New-Item -ItemType Directory -Path $dst | Out-Null

    # 契约程序集/符号不随插件分发：Host 侧那份才是唯一实例
    $extra = if ($p.Extra) { $p.Extra } else { @() }
    Get-ChildItem $out -File | Where-Object {
        ($_.Extension -in (@('.dll', '.pdb', '.xlsx', '.json', '.config') + $extra)) -and
        ($_.Name -notlike 'Caxa3dMyTools.Contracts.*')
    } | Copy-Item -Destination $dst -Force

    # 非本工程产出的运行期文件（如独立工具 EXE）显式补拷：缺件直接失败，不要静默少件
    if ($p.ExtraSrc) {
        foreach ($src in $p.ExtraSrc) {
            if (-not (Test-Path $src)) { throw "缺少额外产物：$src（先编译对应工程）" }
            Copy-Item $src $dst -Force
        }
    }

    Write-Host "已暂存插件：$($p.Name) -> $dst"
}

Write-Host ""
Write-Host "编译与暂存完成。" -ForegroundColor Green
Write-Host "  Host : $(Join-Path $root 'src\Host\bin\x64\Release\net48\caxa3dmytoolsaddin.dll')"
Write-Host "  插件 : $stage"
Write-Host "  独立工具 : $idcToolExe -> $(Join-Path $stage 'caxa3didcskill')"
Write-Host "安装 : 运行仓库根目录的 安装.bat；卸载：运行 卸载.bat；打包：运行 打包.bat"
