param([string]$OutputDirectory = "", [string]$ZigPath = "")
$ErrorActionPreference = 'Stop'
function Scaling-Sha([string]$Path) {
    $scalingStream = [IO.File]::OpenRead($Path)
    $scalingAlgorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($scalingAlgorithm.ComputeHash($scalingStream)).Replace('-', '') }
    finally { $scalingAlgorithm.Dispose(); $scalingStream.Dispose() }
}
$scalingRepo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $scalingRepo 'build\scaling' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$scalingSource = Join-Path $scalingRepo 'native\Scaling\ScaleHost.cpp'
$scalingOutput = Join-Path $OutputDirectory 'Pavise.ScaleHost.exe'
$scalingInputs = @(Get-ChildItem -LiteralPath (Split-Path -Parent $scalingSource) -File | Where-Object { $_.Extension -in '.cpp','.h' })
$scalingSignature = 'build:' + (Scaling-Sha $PSCommandPath) + ';' + (($scalingInputs | Sort-Object Name | ForEach-Object { $_.Name + ':' + (Scaling-Sha $_.FullName) }) -join ';')
$scalingReceipt = Join-Path $OutputDirectory 'build.json'
if ((Test-Path -LiteralPath $scalingReceipt) -and (Test-Path -LiteralPath $scalingOutput)) {
    try {
        $scalingPrevious = Get-Content -LiteralPath $scalingReceipt -Raw | ConvertFrom-Json
        if ($scalingPrevious.sources -eq $scalingSignature -and $scalingPrevious.binary -eq (Scaling-Sha $scalingOutput)) {
            Write-Output $scalingOutput
            return
        }
    } catch { }
}
$scalingVcvars = $null
if (-not $ZigPath) {
    $scalingVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $scalingVswhere) {
        $scalingVs = & $scalingVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($scalingVs) { $scalingVcvars = Join-Path ($scalingVs | Select-Object -First 1) 'VC\Auxiliary\Build\vcvars64.bat' }
    }
    if (-not $scalingVcvars) {
        $scalingZig = Get-Command zig.exe -ErrorAction SilentlyContinue
        if ($scalingZig) { $ZigPath = $scalingZig.Source }
        elseif ($env:PAVISE_ZIG_PATH) { $ZigPath = $env:PAVISE_ZIG_PATH }
        elseif (Test-Path -LiteralPath (Join-Path $scalingRepo 'build\toolchain\zig-x86_64-windows-0.15.2\zig.exe')) {
            $ZigPath = Join-Path $scalingRepo 'build\toolchain\zig-x86_64-windows-0.15.2\zig.exe'
        }
        else {
            # Reuse the portable toolchain already used by Pavise's native benches.
            $scalingPortable = Join-Path $env:TEMP 'Pavise-Zig-0.15.2\zig-x86_64-windows-0.15.2\zig.exe'
            if (Test-Path -LiteralPath $scalingPortable) { $ZigPath = $scalingPortable }
        }
    }
}
if ($scalingVcvars) {
    $scalingCommand = 'call "{0}" >nul && cl /nologo /std:c++17 /O2 /EHsc /MT /W4 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0a00 "{1}" /Fo"{2}" /Fe:"{3}" /link /SUBSYSTEM:WINDOWS d3d11.lib dxgi.lib d3dcompiler.lib dwmapi.lib ole32.lib user32.lib gdi32.lib shell32.lib' -f $scalingVcvars,$scalingSource,(Join-Path $OutputDirectory 'ScaleHost.obj'),$scalingOutput
    & $env:ComSpec /d /s /c $scalingCommand
    if ($LASTEXITCODE -ne 0) { throw 'Scaling host MSVC build failed.' }
} elseif ($ZigPath -and (Test-Path -LiteralPath $ZigPath)) {
    & $ZigPath c++ -target x86_64-windows-gnu -std=c++17 -O2 -municode '-Wl,--subsystem,windows' -DUNICODE -D_UNICODE -D_WIN32_WINNT=0x0a00 $scalingSource -o $scalingOutput -ld3d11 -ldxgi -ld3dcompiler_47 -ldwmapi -lole32 -luser32 -lgdi32 -lshell32
    if ($LASTEXITCODE -ne 0) { throw 'Scaling host Zig build failed.' }
} else {
    throw 'Build requires MSVC C++ tools or portable Zig. Set PAVISE_ZIG_PATH or pass -ZigPath. No software is downloaded automatically.'
}
$scalingMeta = @{ sources=$scalingSignature; binary=(Scaling-Sha $scalingOutput) }
[IO.File]::WriteAllText($scalingReceipt, ($scalingMeta | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
Write-Output $scalingOutput
