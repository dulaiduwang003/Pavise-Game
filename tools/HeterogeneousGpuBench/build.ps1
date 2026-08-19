param(
    [string]$OutputDirectory = "$env:TEMP\Pavise-HeterogeneousGpuBench-bin",
    [string]$ZigPath = ""
)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "HeterogeneousGpuBench.cpp"
$probeSource = Join-Path $PSScriptRoot "D3D12CrossAdapterProbe.cpp"
$d3d12BenchSource = Join-Path $PSScriptRoot "D3D12ComputeBench.cpp"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory "Pavise.HeterogeneousGpuBench.exe"
$probeOutput = Join-Path $OutputDirectory "Pavise.D3D12CrossAdapterProbe.exe"
$d3d12BenchOutput = Join-Path $OutputDirectory "Pavise.HeterogeneousGpuBench.D3D12.exe"

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

    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $root) { continue }
        foreach ($year in @("2026", "2022", "2019")) {
            foreach ($edition in @("Community", "Professional", "Enterprise", "BuildTools")) {
                $candidate = Join-Path $root "Microsoft Visual Studio\$year\$edition\VC\Auxiliary\Build\vcvars64.bat"
                if (Test-Path -LiteralPath $candidate) { return $candidate }
            }
        }
    }
    return $null
}

$vcvars = Find-VcVars
if ($vcvars) {
    $command = 'call "{0}" >nul && cl.exe /nologo /std:c++17 /O2 /EHsc /W4 /DUNICODE /D_UNICODE "{1}" /Fe:"{2}" /link /INCREMENTAL:NO d3d11.lib dxgi.lib d3dcompiler.lib ole32.lib' -f `
        $vcvars, $source, $output
    & $env:ComSpec /d /s /c $command
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $probeCommand = 'call "{0}" >nul && cl.exe /nologo /std:c++17 /O2 /EHsc /W4 /DUNICODE /D_UNICODE "{1}" /Fe:"{2}" /link /INCREMENTAL:NO d3d12.lib dxgi.lib' -f `
        $vcvars, $probeSource, $probeOutput
    & $env:ComSpec /d /s /c $probeCommand
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $d3d12Command = 'call "{0}" >nul && cl.exe /nologo /std:c++17 /O2 /EHsc /W4 /DUNICODE /D_UNICODE "{1}" /Fe:"{2}" /link /INCREMENTAL:NO d3d12.lib dxgi.lib d3dcompiler.lib' -f `
        $vcvars, $d3d12BenchSource, $d3d12BenchOutput
    & $env:ComSpec /d /s /c $d3d12Command
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Output $output
    Write-Output $probeOutput
    Write-Output $d3d12BenchOutput
    exit 0
}

$zig = $null
if ($ZigPath) {
    if (Test-Path -LiteralPath $ZigPath) { $zig = (Resolve-Path -LiteralPath $ZigPath).Path }
} else {
    $zigCommand = Get-Command zig.exe -ErrorAction SilentlyContinue
    if ($zigCommand) { $zig = $zigCommand.Source }
}
if (-not $zig) {
    throw "No C++ compiler was found. Install Visual Studio C++ tools or pass -ZigPath."
}

$sdkLibrary = $null
$kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Lib"
if (Test-Path -LiteralPath $kitsRoot) {
    $sdkLibrary = Get-ChildItem -LiteralPath $kitsRoot -Directory |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "um\x64" } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ "d3dcompiler.lib") } |
        Select-Object -First 1
}
if (-not $sdkLibrary) {
    throw "Windows SDK x64 libraries were not found (d3dcompiler.lib is required)."
}

& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode -DUNICODE -D_UNICODE `
    $source -o $output -L $sdkLibrary -ld3d11 -ldxgi -ld3dcompiler -lole32
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode -DUNICODE -D_UNICODE `
    $probeSource -o $probeOutput -L $sdkLibrary -ld3d12 -ldxgi
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode -DUNICODE -D_UNICODE `
    $d3d12BenchSource -o $d3d12BenchOutput -L $sdkLibrary -ld3d12 -ldxgi -ld3dcompiler
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output $output
Write-Output $probeOutput
Write-Output $d3d12BenchOutput
