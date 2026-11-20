# 把托管代码和原生 ETW 组件链成一个 x64 exe 原生代码直接在 exe 里 不写出 DLL
#   csc 编成 netmodule  从 NativeEtwImage.cs 和 NativeCpuImage.cs 取出 Zig 编好的目标文件  rc 生成图标 清单和版本资源  link.exe 链接
# 需要 MSVC 的 link.exe lib.exe 和 Windows SDK 的 rc.exe kernel32.lib 按下面的顺序找
#   PAVISE_MSVC_ENV 指向的环境脚本  vswhere 找到的 vcvars64.bat  已经在 PATH 里的 MSVC 工具
# 只允许导入 KERNEL32.dll 和 mscoree.dll 不依赖 VC 运行库
param(
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)][string]$Manifest,
    [ValidateSet('winexe', 'exe')][string]$Kind = 'winexe',
    [string]$Entry = 'PaviseApp.Program.Main',
    [string]$Icon = '',
    [string[]]$Define = @(),
    [string[]]$ExtraSources = @(),
    [string]$SourceRoot = '',
    [switch]$ProductResources
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common\PaviseSources.ps1')
$root = Split-Path -Parent $PSScriptRoot
$Output = [IO.Path]::GetFullPath($Output)
# 测试用冻结或变异的源码副本 默认就是工作区
$sourceRoot = if ($SourceRoot) { [IO.Path]::GetFullPath($SourceRoot) } else { $root }
$work = Join-Path $root ('build\mixed-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$ascii = New-Object Text.ASCIIEncoding
$utf8 = New-Object Text.UTF8Encoding($false)

function Quote([string]$value) { if ($value -match '[\s",]') { return '"' + $value.Replace('"', '\"') + '"' } return $value }

function Invoke-Tool([string]$program, [string[]]$arguments, [string]$label) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $program
    $info.Arguments = ($arguments | ForEach-Object { Quote $_ }) -join ' '
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.WorkingDirectory = $root
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = ($stdout.Result + $stderr.Result).Trim()
        if ($process.ExitCode -ne 0) { if ($text) { Write-Output $text }; throw ($label + ' failed with exit code ' + $process.ExitCode) }
        return $text
    } finally { $process.Dispose() }
}

# 环境脚本跑完后把它设的变量搬进当前进程
function Import-Environment([string]$script) {
    $lines = & cmd.exe /d /c ('call "' + $script + '" >nul 2>&1 && set')
    if ($LASTEXITCODE -ne 0) { throw ('MSVC environment script failed: ' + $script) }
    foreach ($line in $lines) {
        $at = $line.IndexOf('=')
        if ($at -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $at), $line.Substring($at + 1)) }
    }
}

# Git 自带的 coreutils 也有 link.exe 只认输出里带微软链接器字样的那个
function Find-Linker {
    foreach ($candidate in @(Get-Command link.exe -All -ErrorAction SilentlyContinue)) {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $candidate.Source; $info.Arguments = '/?'
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        try {
            $process = [Diagnostics.Process]::Start($info)
            $stderr = $process.StandardError.ReadToEndAsync(); $banner = $process.StandardOutput.ReadToEnd()
            $process.WaitForExit(); $null = $stderr.Result; $process.Dispose()
        } catch { $banner = '' }
        if ($banner -match 'Microsoft \(R\) Incremental Linker') { return $candidate.Source }
    }
    return $null
}

function Use-Msvc {
    $script = $env:PAVISE_MSVC_ENV
    if (-not $script) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
            if ($install) { $script = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat' }
        }
    }
    if ($script) {
        if (-not (Test-Path -LiteralPath $script)) { throw ('MSVC environment script not found: ' + $script) }
        Import-Environment $script
    }
    $linker = Find-Linker
    if (-not $linker) { throw 'MSVC x64 link.exe was not found. Install Visual Studio Build Tools (MSVC x64 tools and a Windows SDK) or set PAVISE_MSVC_ENV to an environment script.' }
    $bin = Split-Path -Parent $linker
    $lib = Join-Path $bin 'lib.exe'
    if (-not (Test-Path -LiteralPath $lib)) { throw ('lib.exe is missing next to ' + $linker) }
    $rc = Get-Command rc.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $rc) { throw 'rc.exe from the Windows SDK was not found on PATH.' }
    if (-not $env:LIB) { throw 'LIB is not set; the MSVC environment did not provide kernel32.lib.' }
    return @{ Link = $linker; Lib = $lib; Rc = $rc.Source }
}

function Get-Sha256([byte[]]$bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $sha.Dispose() }
}

# 生成文件里的常量 Build-Native 写的格式固定
function Get-Constant([string]$text, [string]$name, [string]$image) {
    $match = [regex]::Match($text, 'internal const (?:string|int) ' + $name + ' = "?([0-9a-f]+)"?;')
    if (-not $match.Success) { throw ($image + ' has no ' + $name + '; run tools/Build-Native.ps1') }
    return $match.Groups[1].Value
}

# 每个原生组件一份生成文件 源码改了没重新生成就拒绝链接
function Write-NativeObject([string]$image, [string]$sourceName, [string]$path) {
    $imageText = [IO.File]::ReadAllText((Resolve-PaviseSource -Root (Join-Path $sourceRoot 'src') -Name $image))
    # 检出时 Git 可能改写行尾 两种行尾的哈希都认 源码内容本身变了才算过期
    $sourceText = [IO.File]::ReadAllText((Join-Path $sourceRoot ('src\Native\' + $sourceName)))
    $sourceLf = $sourceText.Replace("`r`n", "`n")
    $rawBytes = [IO.File]::ReadAllBytes((Join-Path $sourceRoot ('src\Native\' + $sourceName)))
    $lfBytes = $utf8.GetBytes($sourceLf)
    $crlfBytes = $utf8.GetBytes($sourceLf.Replace("`n", "`r`n"))
    $sourceHashes = @((Get-Sha256 $rawBytes), (Get-Sha256 $lfBytes), (Get-Sha256 $crlfBytes))
    if ($sourceHashes -notcontains (Get-Constant $imageText 'SourceSha256' $image)) { throw ($image + ' was not generated from the current ' + $sourceName + '; run tools/Build-Native.ps1') }
    $start = $imageText.IndexOf('internal const string ObjectBase64 =')
    if ($start -lt 0) { throw ($image + ' has no object image; run tools/Build-Native.ps1') }
    $end = $imageText.IndexOf('";', $start)
    $pieces = [regex]::Matches($imageText.Substring($start, $end + 2 - $start), '"([A-Za-z0-9+/=]*)"') | ForEach-Object { $_.Groups[1].Value }
    $bytes = [Convert]::FromBase64String(($pieces -join ''))
    if ($bytes.Length -ne [int](Get-Constant $imageText 'ObjectLength' $image) -or (Get-Sha256 $bytes) -ne (Get-Constant $imageText 'ObjectSha256' $image)) { throw ('The native object image in ' + $image + ' is damaged.') }
    [IO.File]::WriteAllBytes($path, $bytes)
}

function Get-AssemblyText([string]$text, [string]$name) {
    $match = [regex]::Match($text, '\[assembly: ' + $name + '\("([^"]*)"\)\]')
    if ($match.Success) { return $match.Groups[1].Value }
    return ''
}

# 直接编 exe 时 csc 用 -win32icon -win32manifest 和程序集特性生成这些资源 链接 netmodule 时要自己写
function Write-Resources([string]$path, [string]$manifestPath) {
    $appText = [IO.File]::ReadAllText((Join-Path $sourceRoot 'src\App\Lifecycle\App.cs'))
    $version = [regex]::Match($appText, '(?m)^\s*public const string Version = "([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)";').Groups[1].Value
    if (-not $version) { throw 'App.Version was not found.' }
    $info = [IO.File]::ReadAllText((Join-Path $sourceRoot 'src\Properties\AssemblyInfo.cs'))
    $fileName = [IO.Path]::GetFileName($Output)
    $values = [ordered]@{
        'Comments' = Get-AssemblyText $info 'AssemblyDescription'; 'CompanyName' = Get-AssemblyText $info 'AssemblyCompany'
        'FileDescription' = Get-AssemblyText $info 'AssemblyTitle'; 'FileVersion' = $version; 'InternalName' = $fileName
        'LegalCopyright' = Get-AssemblyText $info 'AssemblyCopyright'; 'OriginalFilename' = $fileName
        'ProductName' = Get-AssemblyText $info 'AssemblyProduct'; 'ProductVersion' = $version; 'Assembly Version' = $version }
    $numbers = $version.Replace('.', ',')
    $lines = New-Object System.Collections.Generic.List[string]
    if ($Icon) { $lines.Add('32512 ICON "' + ([IO.Path]::GetFullPath((Join-Path $sourceRoot $Icon))).Replace('\', '/') + '"') }
    $lines.Add('1 24 "' + $manifestPath.Replace('\', '/') + '"')
    $lines.Add('1 VERSIONINFO')
    $lines.Add(' FILEVERSION ' + $numbers)
    $lines.Add(' PRODUCTVERSION ' + $numbers)
    $lines.Add(' FILEFLAGSMASK 0x3fL')
    $lines.Add(' FILEFLAGS 0x0L')
    $lines.Add(' FILEOS 0x40004L')
    $lines.Add(' FILETYPE 0x1L')
    $lines.Add(' FILESUBTYPE 0x0L')
    $lines.Add('BEGIN')
    $lines.Add('    BLOCK "StringFileInfo"')
    $lines.Add('    BEGIN')
    $lines.Add('        BLOCK "000004b0"')
    $lines.Add('        BEGIN')
    foreach ($key in $values.Keys) {
        $value = [string]$values[$key]
        if ($value -match '[^\x20-\x7e]' -or $value.Contains('"')) { throw ('Version resource value must be plain ASCII: ' + $key) }
        $lines.Add('            VALUE "' + $key + '", "' + $value + '"')
    }
    $lines.Add('        END')
    $lines.Add('    END')
    $lines.Add('    BLOCK "VarFileInfo"')
    $lines.Add('    BEGIN')
    $lines.Add('        VALUE "Translation", 0x0, 1200')
    $lines.Add('    END')
    $lines.Add('END')
    [IO.File]::WriteAllText($path, (($lines -join "`r`n") + "`r`n"), $ascii)
}

# PE 读取 导入表和导出表
function Get-PeLayout([byte[]]$pe) {
    $peOffset = [BitConverter]::ToInt32($pe, 0x3c)
    if ([BitConverter]::ToUInt32($pe, $peOffset) -ne 0x4550) { throw 'Not a PE image.' }
    $optional = $peOffset + 24
    if ([BitConverter]::ToUInt16($pe, $optional) -ne 0x20b) { throw 'Not a PE32+ image.' }
    return @{ Pe = $peOffset; Optional = $optional; Sections = [BitConverter]::ToUInt16($pe, $peOffset + 6);
        Table = $optional + [BitConverter]::ToUInt16($pe, $peOffset + 20) }
}
function Get-FileOffset([byte[]]$pe, $layout, [uint32]$rva) {
    for ($i = 0; $i -lt $layout.Sections; $i++) {
        $entry = $layout.Table + 40 * $i
        $va = [BitConverter]::ToUInt32($pe, $entry + 12)
        $size = [Math]::Max([BitConverter]::ToUInt32($pe, $entry + 8), [BitConverter]::ToUInt32($pe, $entry + 16))
        if ($rva -ge $va -and $rva -lt $va + $size) { return [int]($rva - $va + [BitConverter]::ToUInt32($pe, $entry + 20)) }
    }
    throw ('RVA outside sections: ' + $rva)
}
function Get-AsciiAt([byte[]]$pe, [int]$at) { $end = $at; while ($pe[$end] -ne 0) { $end++ }; return [Text.Encoding]::ASCII.GetString($pe, $at, $end - $at) }
function Get-Imports([byte[]]$pe) {
    $layout = Get-PeLayout $pe
    $rva = [BitConverter]::ToUInt32($pe, $layout.Optional + 112 + 8)
    $names = @()
    if ($rva -eq 0) { return $names }
    $descriptor = Get-FileOffset $pe $layout $rva
    while (($nameRva = [BitConverter]::ToUInt32($pe, $descriptor + 12)) -ne 0) {
        $names += Get-AsciiAt $pe (Get-FileOffset $pe $layout $nameRva)
        $descriptor += 20
    }
    return $names
}
function Get-Exports([byte[]]$pe) {
    $layout = Get-PeLayout $pe
    $rva = [BitConverter]::ToUInt32($pe, $layout.Optional + 112)
    $names = @()
    if ($rva -eq 0) { return $names }
    $directory = Get-FileOffset $pe $layout $rva
    $count = [BitConverter]::ToUInt32($pe, $directory + 24)
    $table = Get-FileOffset $pe $layout ([BitConverter]::ToUInt32($pe, $directory + 32))
    for ($i = 0; $i -lt $count; $i++) { $names += Get-AsciiAt $pe (Get-FileOffset $pe $layout ([BitConverter]::ToUInt32($pe, $table + 4 * $i))) }
    return $names
}

try {
    $tools = Use-Msvc
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) { throw 'csc.exe not found - install .NET Framework 4.x' }
    $module = Join-Path $work 'Pavise.netmodule'
    $defines = @('PAVISE_EMBEDDED_NATIVE') + $Define
    # netmodule 必须是 anycpu link.exe 只接受这种 最终映像由 /MACHINE:X64 决定
    $cscArguments = @('-nologo', '-target:module', '-platform:anycpu', '-optimize+', '-codepage:65001', '-unsafe+',
        ('-define:' + ($defines -join ';')), ('-out:' + $module),
        '-reference:System.dll', '-reference:System.Drawing.dll', '-reference:System.Windows.Forms.dll', '-reference:System.Core.dll',
        '-reference:System.Management.dll', '-reference:System.Xml.dll', '-reference:System.Security.dll', ('-recurse:' + (Join-Path $sourceRoot 'src\*.cs'))) + $ExtraSources
    $null = Invoke-Tool $csc $cscArguments 'csc'

    $objects = @()
    foreach ($component in @(@{ Image = 'NativeEtwImage.cs'; Source = 'PaviseEtw.cpp'; Object = 'PaviseEtw.obj' },
        @{ Image = 'NativeCpuImage.cs'; Source = 'PaviseCpu.cpp'; Object = 'PaviseCpu.obj' })) {
        $object = Join-Path $work $component.Object
        Write-NativeObject $component.Image $component.Source $object
        $objects += $object
    }
    $def = Join-Path $work 'mscoree.def'
    [IO.File]::WriteAllText($def, "LIBRARY mscoree.dll`r`nEXPORTS`r`n    _CorExeMain`r`n", $ascii)
    $mscoree = Join-Path $work 'mscoree.lib'
    $null = Invoke-Tool $tools.Lib @('/nologo', ('/def:' + $def), '/machine:x64', ('/out:' + $mscoree)) 'lib'
    $rcFile = Join-Path $work 'Pavise.rc'
    Write-Resources $rcFile ([IO.Path]::GetFullPath($Manifest))
    $res = Join-Path $work 'Pavise.res'
    $null = Invoke-Tool $tools.Rc @('/nologo', ('/fo' + $res), $rcFile) 'rc'

    $subsystem = if ($Kind -eq 'winexe') { 'WINDOWS' } else { 'CONSOLE' }
    $linkLines = New-Object System.Collections.Generic.List[string]
    foreach ($argument in @('/nologo', '/MACHINE:X64', '/LTCG', '/NODEFAULTLIB', '/OPT:NOREF', '/NOIMPLIB', '/NOEXP', '/DYNAMICBASE', '/NXCOMPAT', '/HIGHENTROPYVA',
        ('/ENTRY:' + $Entry), ('/SUBSYSTEM:' + $subsystem))) { $linkLines.Add($argument) }
    $linkLines.Add('/OUT:"' + $Output + '"')
    foreach ($part in @(@($module) + $objects + @($res, $mscoree))) { $linkLines.Add('"' + $part + '"') }
    $linkLines.Add('kernel32.lib')
    if ($ProductResources) {
        # csc 的 -resource 放进 netmodule 后会被 link.exe 丢掉 托管资源改由链接器嵌入
        $linkLines.Add('/ASSEMBLYRESOURCE:"' + (Join-Path $sourceRoot 'tools\Uninstall-Rtss.ps1') + '",Pavise.LegacyCleanup.RtssUninstall.ps1')
        foreach ($plan in @(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'src\Assets\PowerPlans') -Filter '*.pow' | Sort-Object Name)) {
            $linkLines.Add('/ASSEMBLYRESOURCE:"' + $plan.FullName + '",Pavise.PowerPlans.' + $plan.Name)
        }
    }
    $linkResponse = Join-Path $work 'link.rsp'
    [IO.File]::WriteAllLines($linkResponse, [string[]]$linkLines, $utf8)
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output)
    if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
    $null = Invoke-Tool $tools.Link @(('@' + $linkResponse)) 'link'

    $image = [IO.File]::ReadAllBytes($Output)
    $imports = @(Get-Imports $image)
    foreach ($name in $imports) { if ($name -ne 'KERNEL32.dll' -and $name -ne 'mscoree.dll') { throw ('Unexpected import in the linked exe: ' + $name) } }
    $exports = @(Get-Exports $image)
    foreach ($name in @('PvEtwAbi', 'PvEtwLayout', 'PvEtwCreate', 'PvEtwCallback', 'PvEtwDestroy', 'PaviseCpuAbi', 'PaviseCpuidEx')) {
        if ($exports -notcontains $name) { throw ('Native export missing from the linked exe: ' + $name) }
    }
    'linked ' + $Output + ' imports=' + ($imports -join ',') + ' native_exports=' + $exports.Count
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
