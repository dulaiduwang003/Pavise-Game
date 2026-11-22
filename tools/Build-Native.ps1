# 用 Zig 0.15.2 编译 src/Native 下的原生组件 并重新生成 src/Interop 里对应的内嵌镜像
#   Etw    PaviseEtw.cpp    得到 PaviseEtw.dll 和 PaviseEtw.obj    写入 NativeEtwImage.cs    完整采集的 ETW 前置处理
#   Cpu    PaviseCpu.cpp    得到 PaviseCpu.dll 和 PaviseCpu.obj    写入 NativeCpuImage.cs    核心类型备用探测的 CPUID 读取
#   Hid    PaviseHid.cpp    得到 PaviseHid.dll 和 PaviseHid.obj    写入 NativeHidImage.cs    外设接口和占用进程的原生核验
# 正式构建由 build.cmd 把 obj 链进 Pavise.exe 不落盘 测试构建没有链接这一步 仍按 DLL 镜像加载
# 不链接 C 运行库 只允许导入 KERNEL32.dll 生成的类只在没有 PAVISE_EMBEDDED_NATIVE 的测试构建里编译
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\Build-Native.ps1 [-Component Etw|Cpu|Hid|All] [-Zig <zig.exe>] [-Output <dir>] [-CheckOnly]
# -CheckOnly 只构建到 Output 已提交的镜像与新构建不一致时报错
# -Source 和 -ImagePath 供测试重建冻结或变异副本 不动工作区 只能配合单个组件使用
param(
    [ValidateSet('Etw', 'Cpu', 'Hid', 'All')]
    [string]$Component = 'All',
    [string]$Zig = '',
    [string]$Output = '',
    [string]$Source = '',
    [string]$ImagePath = '',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common\PaviseSources.ps1')
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'build\native' }
if (-not $Zig) {
    $zigCommand = Get-Command zig.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $zigCommand) { throw 'Add Zig 0.15.2 to PATH or pass -Zig with the path to zig.exe.' }
    $Zig = $zigCommand.Source
}
if (-not (Test-Path -LiteralPath $Zig -PathType Leaf)) { throw ('Zig not found: ' + $Zig) }
$Zig = (Resolve-Path -LiteralPath $Zig).Path
if (($Source -or $ImagePath) -and $Component -eq 'All') { throw 'Source and ImagePath need a single component.' }
$null = New-Item -ItemType Directory -Force -Path $Output
$zigRoot = Split-Path -Parent $Zig
$headers = Join-Path $zigRoot 'lib\libc\include\any-windows-any'
$cache = Join-Path $Output 'zig-cache'
$env:ZIG_GLOBAL_CACHE_DIR = $cache
$env:ZIG_LOCAL_CACHE_DIR = $cache
$utf8 = New-Object System.Text.UTF8Encoding($false)

$components = @(
    @{ Name = 'Etw'; Source = 'PaviseEtw.cpp'; Binary = 'PaviseEtw.dll'; Class = 'NativeEtwImage'; Subsystem = 2;
        Link = @('-shared', '-Wl,--entry=DllMainCRTStartup') },
    @{ Name = 'Cpu'; Source = 'PaviseCpu.cpp'; Binary = 'PaviseCpu.dll'; Class = 'NativeCpuImage'; Subsystem = 2;
        Link = @('-shared', '-Wl,--entry=PaviseCpuDllMain') }
    @{ Name = 'Hid'; Source = 'PaviseHid.cpp'; Binary = 'PaviseHid.dll'; Class = 'NativeHidImage'; Subsystem = 2;
        Link = @('-shared', '-Wl,--entry=PaviseHidDllMain') }
)

function Get-Sha256([byte[]]$bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '') } finally { $sha.Dispose() }
}

# PE 辅助函数 节内偏移和导入表（多出运行库依赖时构建失败）
# 以及链接时间戳归零 同一份源码总得到同样的字节
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

function Get-Imports([byte[]]$pe) {
    $layout = Get-PeLayout $pe
    $importRva = [BitConverter]::ToUInt32($pe, $layout.Optional + 112 + 8)
    $names = @()
    if ($importRva -eq 0) { return $names }
    $descriptor = Get-FileOffset $pe $layout $importRva
    while ($true) {
        $nameRva = [BitConverter]::ToUInt32($pe, $descriptor + 12)
        if ($nameRva -eq 0) { break }
        $at = Get-FileOffset $pe $layout $nameRva
        $end = $at
        while ($pe[$end] -ne 0) { $end++ }
        $names += [Text.Encoding]::ASCII.GetString($pe, $at, $end - $at)
        $descriptor += 20
    }
    return $names
}

function Clear-Bytes([byte[]]$pe, [int]$offset, [int]$count) { for ($i = 0; $i -lt $count; $i++) { $pe[$offset + $i] = 0 } }

# COFF 和调试目录时间戳 头部校验和 CodeView GUID 清零 加载器不读这些字段
function Set-Reproducible([byte[]]$pe) {
    $layout = Get-PeLayout $pe
    Clear-Bytes $pe ($layout.Pe + 8) 4
    Clear-Bytes $pe ($layout.Optional + 64) 4
    $debugRva = [BitConverter]::ToUInt32($pe, $layout.Optional + 112 + 6 * 8)
    $debugSize = [BitConverter]::ToUInt32($pe, $layout.Optional + 112 + 6 * 8 + 4)
    if ($debugRva -eq 0) { return }
    $directory = Get-FileOffset $pe $layout $debugRva
    for ($entry = $directory; $entry -lt $directory + $debugSize; $entry += 28) {
        Clear-Bytes $pe ($entry + 4) 4
        $type = [BitConverter]::ToUInt32($pe, $entry + 12)
        $dataSize = [BitConverter]::ToUInt32($pe, $entry + 16)
        $data = [BitConverter]::ToInt32($pe, $entry + 24)
        if ($type -eq 2 -and $dataSize -ge 24 -and [BitConverter]::ToUInt32($pe, $data) -eq 0x53445352) { Clear-Bytes $pe ($data + 4) 16 }
    }
}

function Add-Base64Lines($lines, [string]$text) {
    for ($i = 0; $i -lt $text.Length; $i += 120) {
        $piece = $text.Substring($i, [Math]::Min(120, $text.Length - $i))
        $suffix = if ($i + 120 -ge $text.Length) { ';' } else { ' +' }
        $lines.Add('            "' + $piece + '"' + $suffix)
    }
}

# 生成的镜像按类名在 src 下查找 放在哪个子目录都行 全新组件放进 src\Interop
function Get-NativeImagePath([string]$class) {
    $found = @(Get-PaviseSources -Root (Join-Path $root 'src') -Filter ($class + '.cs') -AllowEmpty)
    if ($found.Count -eq 1) { return $found[0] }
    if ($found.Count -gt 1) { throw ('More than one generated image named ' + $class) }
    $folder = Join-Path $root 'src\Interop'
    $null = New-Item -ItemType Directory -Force -Path $folder
    Join-Path $folder ($class + '.cs')
}

function Build-Component($item) {
    $source = if ($Source) { $Source } else { Join-Path $root ('src\Native\' + $item.Source) }
    $imagePath = if ($ImagePath) { $ImagePath } else { Get-NativeImagePath $item.Class }
    $binary = Join-Path $Output $item.Binary
    if (Test-Path -LiteralPath $binary) { Remove-Item -LiteralPath $binary -Force }
    $arguments = @('c++', '-target', 'x86_64-windows-gnu', '-O2', '-std=c++17',
        '-Wall', '-Wextra', '-Werror', '-fno-exceptions', '-fno-rtti', '-ffreestanding', '-nostdlib', '-fno-stack-protector',
        '-g0', '-isystem', $headers) + $item.Link + @('-Wl,--build-id=none', '-o', $binary, $source, '-lkernel32')
    & $Zig @arguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $binary)) { throw ('Native build failed: ' + $item.Name) }

    $bytes = [IO.File]::ReadAllBytes($binary)
    Set-Reproducible $bytes
    [IO.File]::WriteAllBytes($binary, $bytes)
    $layout = Get-PeLayout $bytes
    $subsystem = [BitConverter]::ToUInt16($bytes, $layout.Optional + 68)
    if ($subsystem -ne $item.Subsystem) { throw ('Unexpected PE subsystem ' + $subsystem + ': ' + $item.Name) }
    $imports = @(Get-Imports $bytes)
    foreach ($name in $imports) { if ($name -ne 'KERNEL32.dll') { throw ('Unexpected native import: ' + $name) } }
    $hash = Get-Sha256 $bytes
    # 源码指纹统一使用 LF 兼容 Git 检出时的行尾转换
    $sourceText = [IO.File]::ReadAllText($source).Replace("`r`n", "`n")
    $sourceHash = Get-Sha256 ($utf8.GetBytes($sourceText))
    $base64 = [Convert]::ToBase64String($bytes)

    # 同样的编译参数只编译不链接 得到给 build.cmd 链进 exe 的目标文件
    $objectPath = Join-Path $Output ([IO.Path]::ChangeExtension($item.Binary, '.obj'))
    if (Test-Path -LiteralPath $objectPath) { Remove-Item -LiteralPath $objectPath -Force }
    $objectArguments = @('c++', '-c', '-target', 'x86_64-windows-gnu', '-O2', '-std=c++17',
        '-Wall', '-Wextra', '-Werror', '-fno-exceptions', '-fno-rtti', '-ffreestanding', '-nostdlib', '-fno-stack-protector',
        '-g0', '-isystem', $headers, '-o', $objectPath, $source)
    & $Zig @objectArguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $objectPath)) { throw ('Native object build failed: ' + $item.Name) }
    $object = [IO.File]::ReadAllBytes($objectPath)
    if ([BitConverter]::ToUInt16($object, 0) -ne 0x8664) { throw ('Native object is not x64 COFF: ' + $item.Name) }
    # COFF 时间戳清零 同一份源码总得到同样的字节
    Clear-Bytes $object 4 4
    [IO.File]::WriteAllBytes($objectPath, $object)
    $objectHash = Get-Sha256 $object
    $objectBase64 = [Convert]::ToBase64String($object)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('// 由 tools/Build-Native.ps1 根据 src/Native/' + $item.Source + ' 生成 不要手改')
    $lines.Add('#if !PAVISE_EMBEDDED_NATIVE')
    $lines.Add('namespace PaviseApp')
    $lines.Add('{')
    $lines.Add('    internal static class ' + $item.Class)
    $lines.Add('    {')
    $lines.Add('        internal const string SourceSha256 = "' + $sourceHash + '";')
    $lines.Add('        internal const string Sha256 = "' + $hash + '";')
    $lines.Add('        internal const int Length = ' + $bytes.Length + ';')
    $lines.Add('        internal const string Base64 =')
    Add-Base64Lines $lines $base64
    $lines.Add('        internal const string ObjectSha256 = "' + $objectHash + '";')
    $lines.Add('        internal const int ObjectLength = ' + $object.Length + ';')
    $lines.Add('        internal const string ObjectBase64 =')
    Add-Base64Lines $lines $objectBase64
    $lines.Add('    }')
    $lines.Add('}')
    $lines.Add('#endif')
    $text = ($lines -join "`r`n") + "`r`n"

    if ($CheckOnly) {
        $current = if (Test-Path -LiteralPath $imagePath) { [IO.File]::ReadAllText($imagePath) } else { '' }
        if ($current.Replace("`r`n", "`n") -cne $text.Replace("`r`n", "`n")) { throw ($item.Class + '.cs does not match a fresh build of ' + $item.Source + ' (fresh sha256=' + $hash + ').') }
        'image up to date: ' + $item.Class + ' ' + $hash
    } else {
        [IO.File]::WriteAllText($imagePath, $text, $utf8)
        'wrote ' + $imagePath
    }
    $item.Binary + ' bytes=' + $bytes.Length + ' sha256=' + $hash + ' imports=' + ($imports -join ',')
    [IO.Path]::GetFileName($objectPath) + ' bytes=' + $object.Length + ' sha256=' + $objectHash
}

foreach ($item in $components) {
    if ($Component -eq 'All' -or $Component -eq $item.Name) { Build-Component $item }
}
