param(
    [string]$OutputDirectory = "$PSScriptRoot\dist",
    [string]$ZigPath = ""
)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "InterruptFabricFieldTester.cpp"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path $OutputDirectory "Pavise.InterruptFabric.FieldTest.exe"

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
    $command = 'call "{0}" >nul && cl.exe /nologo /std:c++17 /O2 /EHsc /W4 /DUNICODE /D_UNICODE "{1}" /Fe:"{2}" /link /SUBSYSTEM:WINDOWS /INCREMENTAL:NO advapi32.lib comctl32.lib psapi.lib shell32.lib gdi32.lib ole32.lib' -f `
        $vcvars, $source, $output
    & $env:ComSpec /d /s /c $command
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else {
    $zig = if ($ZigPath) { $ZigPath } else { (Get-Command zig.exe -ErrorAction SilentlyContinue).Source }
    if (-not $zig -or -not (Test-Path -LiteralPath $zig)) {
        throw "No C++ compiler found. Install Visual Studio C++ tools or pass -ZigPath."
    }
    & $zig c++ -target x86_64-windows-gnu -std=c++17 -O2 -Wno-nullability-completeness -municode $source -o $output `
        -ladvapi32 -lcomctl32 -lpsapi -lshell32 -lgdi32 -lole32 '-Wl,--subsystem,windows'
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
Write-Output $output
