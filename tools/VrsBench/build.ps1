param(
    [string]$OutputDirectory = "$env:TEMP\Pavise-VrsBench-bin",
    [string]$ZigPath = ""
)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "VrsBench.cpp"
$shader = Join-Path $PSScriptRoot "VrsBench.hlsl"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory "Pavise.VrsBench.exe"
$vertexShader = Join-Path $OutputDirectory "VrsBench.vs.cso"
$pixelShader = Join-Path $OutputDirectory "VrsBench.ps.cso"

function Find-VcVars {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $installation = & $vswhere -latest -products * `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath
        if ($installation) {
            $candidate = Join-Path ($installation | Select-Object -First 1) "VC\Auxiliary\Build\vcvars64.bat"
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    return $null
}

$vcvars = Find-VcVars
if ($vcvars) {
    $command = 'call "{0}" >nul && cl.exe /nologo /std:c++17 /O2 /EHsc /W4 /DUNICODE /D_UNICODE "{1}" /Fe:"{2}" /link /SUBSYSTEM:WINDOWS /INCREMENTAL:NO d3d12.lib dxgi.lib shell32.lib' -f `
        $vcvars, $source, $output
    & $env:ComSpec /d /s /c $command
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$zig = $null
if ($ZigPath -and (Test-Path -LiteralPath $ZigPath)) {
    $zig = (Resolve-Path -LiteralPath $ZigPath).Path
} else {
    $zigCommand = Get-Command zig.exe -ErrorAction SilentlyContinue
    if ($zigCommand) { $zig = $zigCommand.Source }
}
if (-not $vcvars -and -not $zig) {
    throw "No C++ compiler found. Install Visual Studio C++ or pass -ZigPath to portable zig.exe."
}

$sdkLibRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Lib"
$sdkVersion = Get-ChildItem -LiteralPath $sdkLibRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "um\x64\d3d12.lib") } |
    Select-Object -First 1
if (-not $sdkVersion) {
    throw "Windows SDK x64 libraries were not found."
}
$sdkUm = Join-Path $sdkVersion.FullName "um\x64"
$dxc = Join-Path ${env:ProgramFiles(x86)} ("Windows Kits\10\bin\" + $sdkVersion.Name + "\x64\dxc.exe")
if (-not (Test-Path -LiteralPath $dxc)) { throw "Windows SDK DXC was not found." }

if (-not $vcvars) {
    & $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode $source -o $output `
        (Join-Path $sdkUm "d3d12.lib") `
        (Join-Path $sdkUm "dxgi.lib") `
        (Join-Path $sdkUm "shell32.lib") `
        '-Wl,--subsystem,windows'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $dxc -nologo -T vs_6_0 -E VSMain -O3 -Fo $vertexShader $shader
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dxc -nologo -T ps_6_0 -E PSMain -O3 -Fo $pixelShader $shader
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output $output
