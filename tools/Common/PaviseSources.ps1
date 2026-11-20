# 各个工具共用的 Pavise 源码和测试文件查找函数
# 工具按文件名在根目录下任意深度去找  文件在文件夹之间搬来搬去也不影响工具
# 工具里用 dot source 引入本文件  每个函数的实际用法都能在 Test PaviseSources 里看到
# 所有函数都返回绝对路径  找不到或者找到不止一个都会直接抛异常

$script:PaviseCommonDirectory = $PSScriptRoot
$script:PaviseSourceIndex = @{}

function Get-PaviseRepoRoot {
    [IO.Path]::GetFullPath((Join-Path $script:PaviseCommonDirectory '..\..'))
}

function Get-PaviseFileIndex {
    param([Parameter(Mandatory = $true)][string]$Root, [switch]$Refresh)
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "Pavise lookup root does not exist: $full" }
    if ($Refresh -or -not $script:PaviseSourceIndex.ContainsKey($full)) {
        $files = [IO.FileInfo[]]@(Get-ChildItem -LiteralPath $full -Recurse -File)
        $names = [string[]]@($files | ForEach-Object { $_.Name })
        # 顺序跟以前平铺文件夹列出来的一样  按文件名比较 不分大小写
        [Array]::Sort($names, $files, [StringComparer]::OrdinalIgnoreCase)
        $script:PaviseSourceIndex[$full] = $files
    }
    , $script:PaviseSourceIndex[$full]
}

function Resolve-PaviseFile {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Root
    )
    # 名字前面可以带上最后几级文件夹  用来区分同名的文件
    $suffix = $Name.Replace('/', '\').TrimStart('\')
    $leaf = Split-Path -Leaf $suffix
    foreach ($refresh in @($false, $true)) {
        $index = Get-PaviseFileIndex -Root $Root -Refresh:$refresh
        $hits = @($index | Where-Object {
            $_.Name -ieq $leaf -and ($suffix -eq $leaf -or $_.FullName.EndsWith('\' + $suffix, [StringComparison]::OrdinalIgnoreCase))
        })
        if ($hits.Count -eq 1) { return $hits[0].FullName }
        if ($hits.Count -gt 1) {
            throw ("Pavise file name is not unique under {0}: {1} matches {2}" -f $Root, $Name, (($hits | ForEach-Object { $_.FullName }) -join ' ; '))
        }
    }
    throw ("Pavise file not found under {0}: {1}" -f $Root, $Name)
}

function Resolve-PaviseSource {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Root = (Join-Path (Get-PaviseRepoRoot) 'src')
    )
    Resolve-PaviseFile -Name $Name -Root $Root
}

# 返回源文件相对仓库根目录的路径 用正斜杠分隔  给维护相对路径清单的工具用
function Get-PaviseSourceRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Workspace = (Get-PaviseRepoRoot)
    )
    $root = [IO.Path]::GetFullPath($Workspace).TrimEnd('\')
    $full = Resolve-PaviseSource -Name $Name -Root (Join-Path $root 'src')
    $full.Substring($root.Length + 1).Replace('\', '/')
}

function Resolve-PaviseTest {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Root = (Join-Path (Get-PaviseRepoRoot) 'tests')
    )
    Resolve-PaviseFile -Name $Name -Root $Root
}

function Get-PaviseSources {
    param(
        [Parameter(Mandatory = $true)][string]$Filter,
        [string]$Root = (Join-Path (Get-PaviseRepoRoot) 'src'),
        [switch]$AllowEmpty
    )
    $hits = @()
    foreach ($refresh in @($false, $true)) {
        $index = Get-PaviseFileIndex -Root $Root -Refresh:$refresh
        $hits = @($index | Where-Object { $_.Name -like $Filter } | ForEach-Object { $_.FullName })
        if ($hits.Count -gt 0) { break }
    }
    # 通配符一个都没匹配上还不报错的话  工具编译出来的程序会比它以为的少文件
    if ($hits.Count -eq 0 -and -not $AllowEmpty) { throw ("No Pavise files under {0} match {1}" -f $Root, $Filter) }
    # 路径像列文件夹那样一条条输出  可能只命中一个的时候调用方要用数组包一层
    $hits
}

function Find-PaviseSourceBySignature {
    param(
        [Parameter(Mandatory = $true)][string]$Signature,
        [string]$Root = (Join-Path (Get-PaviseRepoRoot) 'src'),
        [string]$Filter = '*.cs',
        [switch]$Once
    )
    $index = Get-PaviseFileIndex -Root $Root -Refresh
    $hits = @()
    $total = 0
    foreach ($file in @($index | Where-Object { $_.Name -like $Filter })) {
        $text = [IO.File]::ReadAllText($file.FullName)
        $at = $text.IndexOf($Signature, [StringComparison]::Ordinal)
        if ($at -lt 0) { continue }
        $hits += $file.FullName
        while ($at -ge 0) { $total++; $at = $text.IndexOf($Signature, $at + $Signature.Length, [StringComparison]::Ordinal) }
    }
    if ($hits.Count -eq 0) { throw ("No Pavise file under {0} contains: {1}" -f $Root, $Signature) }
    if ($hits.Count -gt 1) { throw ("More than one Pavise file under {0} contains: {1} ; {2}" -f $Root, $Signature, ($hits -join ' ; ')) }
    if ($Once -and $total -ne 1) { throw ("Signature occurs {0} times in {1}: {2}" -f $total, $hits[0], $Signature) }
    $hits[0]
}
