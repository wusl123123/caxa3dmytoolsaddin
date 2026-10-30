param([Parameter(Mandatory = $true)][string]$ManifestPath)

# 剥离 mt.exe 从内嵌 interop 类型生成的 clrClass/clrSurrogate 条目。
# 原因：多个 .NET Add-in 各自内嵌同一 interop 类型库时，mt.exe 为这些
# 无显式 GUID 的类型按"程序集身份"生成不同 CLSID，但 progid（完全限定
# 类型名）相同。多个 Add-in 同处 IronCAD.exe 的一个 SxS 激活上下文时，
# 同名 progid 对应不同 CLSID 导致激活上下文合并失败：
# "应用程序并行配置不正确" → CAXA 无法启动。
# 免注册 COM 只需要本 Add-in 自己的 clrClass（Addin 类），interop 委托/
# 枚举不会被 COM 激活，剥离安全。
#
# 重要：mt.exe 生成的 manifest 带 xmlns="urn:schemas-microsoft-com:asm.v1"，
# 必须用 local-name() 忽略命名空间匹配，否则子节点查询全部返回空，
# interop 条目就会原样残留 → CAXA 无法启动！
if (-not (Test-Path $ManifestPath)) { exit 1 }

[xml]$m = Get-Content $ManifestPath -Raw -Encoding UTF8
$removed = 0

foreach ($tag in @('clrClass', 'clrSurrogate')) {
    # 用 local-name() 忽略命名空间，兼容 mt.exe 生成的 xmlns 版本
    $xpath = "//*[local-name()='$tag']"
    $nodes = $m.SelectNodes($xpath)
    foreach ($n in $nodes) {
        $nameAttr = $n.GetAttribute('name')
        if ($nameAttr -and $nameAttr.StartsWith('interop.', [System.StringComparison]::Ordinal)) {
            [void]$n.ParentNode.RemoveChild($n)
            $removed++
        }
    }
}

# 移除 xmlns 声明（去掉默认命名空间，避免后续 mt.exe sn.exe 处理时可能的问题）
$ns = New-Object System.Xml.XmlNamespaceManager($m.NameTable)
$ns.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
$root = $m.SelectSingleNode('//asm:assembly', $ns)
if ($root) { $root.RemoveAttribute('xmlns') }

$m.Save($ManifestPath)
Write-Host "strip-manifest: removed $removed interop entries from $ManifestPath"
