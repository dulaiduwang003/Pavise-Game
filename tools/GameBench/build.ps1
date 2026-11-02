param(
    [string]$OutputDirectory = (Join-Path $env:TEMP 'Pavise-GameBench-bin'),
    [string]$ZigPath = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { throw 'OutputDirectory is required.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
if (-not $ZigPath) {
    $taskZigCommand = Get-Command zig.exe -ErrorAction SilentlyContinue
    $taskZigCandidates = @()
    if ($taskZigCommand) { $taskZigCandidates += $taskZigCommand.Source }
    foreach ($taskZigVersion in '0.16.0', '0.15.2') {
        $taskZigCandidates += (Join-Path $env:TEMP "Pavise-Zig-$taskZigVersion/zig-x86_64-windows-$taskZigVersion/zig.exe")
    }
    $ZigPath = $taskZigCandidates | Where-Object {
        (Test-Path -LiteralPath $_ -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path (Split-Path $_) 'lib/libc/include/any-windows-any/windows.h') -PathType Leaf)
    } | Select-Object -First 1
}
if (-not $ZigPath -or -not (Test-Path -LiteralPath $ZigPath -PathType Leaf)) { throw 'Pass -ZigPath to a complete official Zig distribution (tested: 0.16.0 and 0.15.2), including its lib directory.' }
$ZigPath = (Resolve-Path -LiteralPath $ZigPath).ProviderPath
$taskSources = @('Bench.cpp','BenchCommon.hpp','Dx11.cpp','Dx12.cpp','build.ps1')
$taskSourceManifest = @($taskSources | ForEach-Object {
    $taskSourcePath = Join-Path $PSScriptRoot $_
    [ordered]@{ name=$_; path=$taskSourcePath; sha256=(Get-FileHash -LiteralPath $taskSourcePath -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$taskOutput = Join-Path $OutputDirectory 'BenchGame.exe'
$taskManifestPath = Join-Path $OutputDirectory 'build-manifest.json'
$taskVersion = (& $ZigPath version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to query Zig version.' }
$taskArguments = @('c++','-target','x86_64-windows-gnu','-std=c++17','-O2','-g0','-municode','-DUNICODE','-D_UNICODE','-D_WIN32_WINNT=0x0a00','-DWINVER=0x0a00',
    (Join-Path $PSScriptRoot 'Bench.cpp'),(Join-Path $PSScriptRoot 'Dx11.cpp'),(Join-Path $PSScriptRoot 'Dx12.cpp'),
    '-o',$taskOutput,'-ld3d11','-ld3d12','-ldxgi','-ld3dcompiler_47','-luser32','-lgdi32','-lbcrypt','-luuid','-lole32')
$taskManifest = [ordered]@{schema=1;status='building';started_utc=[DateTime]::UtcNow.ToString('o');completed_utc=$null;
    compiler=[ordered]@{name='Zig C++';version=$taskVersion;path=$ZigPath;sha256=(Get-FileHash -LiteralPath $ZigPath -Algorithm SHA256).Hash.ToLowerInvariant();target='x86_64-windows-gnu'};
    arguments=$taskArguments;sources=$taskSourceManifest;artifact=$taskOutput;artifact_sha256=$null}
function Write-TaskManifest { [IO.File]::WriteAllText($taskManifestPath,($taskManifest | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false)) }
Write-TaskManifest
$taskPreviousLocal = [Environment]::GetEnvironmentVariable('ZIG_LOCAL_CACHE_DIR','Process')
$taskPreviousGlobal = [Environment]::GetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR','Process')
try {
    [Environment]::SetEnvironmentVariable('ZIG_LOCAL_CACHE_DIR',(Join-Path $OutputDirectory 'zig-local-cache'),'Process')
    [Environment]::SetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR',(Join-Path $env:TEMP 'Pavise-Zig-global-cache'),'Process')
    & $ZigPath @taskArguments
    if ($LASTEXITCODE -ne 0) { throw "Native GameBench compilation failed ($LASTEXITCODE)." }
    foreach ($taskSource in $taskSourceManifest) {
        if ((Get-FileHash -LiteralPath $taskSource.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskSource.sha256) { throw "Source changed during compilation: $($taskSource.name)" }
    }
    $taskManifest.artifact_sha256=(Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskManifest.status='success'
} catch { $taskManifest.status='failed'; throw }
finally {
    $taskManifest.completed_utc=[DateTime]::UtcNow.ToString('o');Write-TaskManifest
    [Environment]::SetEnvironmentVariable('ZIG_LOCAL_CACHE_DIR',$taskPreviousLocal,'Process')
    [Environment]::SetEnvironmentVariable('ZIG_GLOBAL_CACHE_DIR',$taskPreviousGlobal,'Process')
}
Write-Output $taskOutput
Write-Output $taskManifestPath
