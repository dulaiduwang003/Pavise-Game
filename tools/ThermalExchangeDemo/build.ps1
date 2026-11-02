param(
    [string]$OutputDirectory = "$env:TEMP\Pavise-ThermalExchange-bin",
    [string]$ZigPath = ""
)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "ThermalExchangeDemo.cpp"
$loadSource = Join-Path $PSScriptRoot "ThermalGpuLoad.cpp"
$fieldSource = Join-Path $PSScriptRoot "ThermalFieldTester.cpp"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory "Pavise.ThermalExchangeDemo.exe"
$loadOutput = Join-Path $OutputDirectory "Pavise.ThermalGpuLoad.exe"
$fieldOutput = Join-Path $OutputDirectory "Pavise.ThermalExchange.FieldTest.exe"

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

    $editions = @("Community", "Professional", "Enterprise", "BuildTools")
    $years = @("2026", "2022", "2019")
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $root) { continue }
        foreach ($year in $years) {
            foreach ($edition in $editions) {
                $candidate = Join-Path $root "Microsoft Visual Studio\$year\$edition\VC\Auxiliary\Build\vcvars64.bat"
                if (Test-Path -LiteralPath $candidate) { return $candidate }
            }
        }
    }
    return $null
}

$vcvars = Find-VcVars
if ($vcvars) {
    $command = 'call "{0}" >nul && cl.exe /nologo /std:c++17 /O2 /EHsc /W4 /DUNICODE /D_UNICODE "{1}" /Fe:"{2}" /link /INCREMENTAL:NO' -f `
        $vcvars, $source, $output
    & $env:ComSpec /d /s /c $command
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $loadCommand = 'call "{0}" >nul && cl.exe /nologo /std:c++17 /O2 /EHsc /W4 /DUNICODE /D_UNICODE "{1}" /Fe:"{2}" /link /SUBSYSTEM:WINDOWS /INCREMENTAL:NO' -f `
        $vcvars, $loadSource, $loadOutput
    & $env:ComSpec /d /s /c $loadCommand
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $fieldCommand = 'call "{0}" >nul && cl.exe /nologo /std:c++17 /O2 /EHsc /W4 /DUNICODE /D_UNICODE "{1}" /Fe:"{2}" /link /SUBSYSTEM:WINDOWS /INCREMENTAL:NO' -f `
        $vcvars, $fieldSource, $fieldOutput
    & $env:ComSpec /d /s /c $fieldCommand
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Output $output
    Write-Output $loadOutput
    Write-Output $fieldOutput
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
    throw "No C++ compiler was found. Install the Visual Studio 'Desktop development with C++' workload, or pass -ZigPath to a portable zig.exe."
}

& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode $source -o $output `
    -lpowrprof -lpdh -ladvapi32 -lole32
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode $loadSource -o $loadOutput `
    -ld3d9 -lshell32 '-Wl,--subsystem,windows'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode $fieldSource -o $fieldOutput `
    -lpowrprof -lpdh -ladvapi32 -lole32 -lcomctl32 -lpsapi -lshell32 -lgdi32 '-Wl,--subsystem,windows'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Output $output
Write-Output $loadOutput
Write-Output $fieldOutput
