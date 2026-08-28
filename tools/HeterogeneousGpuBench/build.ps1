param(
    [string]$OutputDirectory = "$env:TEMP\Pavise-HeterogeneousGpuBench-bin",
    [string]$ZigPath = ""
)

$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "HeterogeneousGpuBench.cpp"
$probeSource = Join-Path $PSScriptRoot "D3D12CrossAdapterProbe.cpp"
$d3d12BenchSource = Join-Path $PSScriptRoot "D3D12ComputeBench.cpp"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw "OutputDirectory must not be empty."
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
$output = Join-Path $OutputDirectory "Pavise.HeterogeneousGpuBench.exe"
$probeOutput = Join-Path $OutputDirectory "Pavise.D3D12CrossAdapterProbe.exe"
$d3d12BenchOutput = Join-Path $OutputDirectory "Pavise.HeterogeneousGpuBench.D3D12.exe"
$buildTargets = @(
    @{ Source = $source; Output = $output; Libraries = @("d3d11", "dxgi", "d3dcompiler", "ole32") },
    @{ Source = $probeSource; Output = $probeOutput; Libraries = @("d3d12", "dxgi") },
    @{ Source = $d3d12BenchSource; Output = $d3d12BenchOutput; Libraries = @("d3d12", "dxgi", "d3dcompiler") }
)
$manifestPath = Join-Path $OutputDirectory "build-manifest.json"
$buildManifest = [ordered]@{
    schema_version = 1
    status = "building"
    started_utc = [DateTime]::UtcNow.ToString("o")
    completed_utc = $null
    compiler = $null
    build_script_path = $PSCommandPath
    build_script_sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    targets = @()
}

function Write-BuildManifest {
    param([System.Collections.IDictionary]$Manifest, [string]$Path)
    $json = ConvertTo-Json -InputObject $Manifest -Depth 8
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Add-TargetManifest {
    param([System.Collections.IDictionary]$Target, [string]$SourceHash)
    $sourceHashAfter = (Get-FileHash -LiteralPath $Target.Source -Algorithm SHA256).Hash
    if ($sourceHashAfter -ne $SourceHash) {
        throw "The source changed during compilation; this build cannot be attested: $($Target.Source)"
    }
    $buildManifest.targets += [ordered]@{
        source_path = $Target.Source
        source_sha256 = $SourceHash
        exe_path = $Target.Output
        exe_sha256 = (Get-FileHash -LiteralPath $Target.Output -Algorithm SHA256).Hash
        built_utc = [DateTime]::UtcNow.ToString("o")
    }
}

function Find-VcVars {
    if (${env:ProgramFiles(x86)}) {
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

function Find-SdkLibrary {
    if (-not ${env:ProgramFiles(x86)}) { return $null }
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Lib"
    if (-not (Test-Path -LiteralPath $kitsRoot)) { return $null }
    return Get-ChildItem -LiteralPath $kitsRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "um\x64" } |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_ "d3dcompiler.lib")) -and
            (Test-Path -LiteralPath (Join-Path $_ "d3d12.lib")) -and
            (Test-Path -LiteralPath (Join-Path $_ "d3d11.lib")) -and
            (Test-Path -LiteralPath (Join-Path $_ "dxgi.lib")) -and
            (Test-Path -LiteralPath (Join-Path $_ "ole32.lib"))
        } |
        Select-Object -First 1
}

# An explicit compiler path wins over auto-discovery. No downloads or installs.
$vcvars = $null
$zig = $null
if ($ZigPath) {
    if (-not (Test-Path -LiteralPath $ZigPath -PathType Leaf)) {
        throw "The supplied Zig executable was not found: $ZigPath"
    }
    $zig = (Resolve-Path -LiteralPath $ZigPath).ProviderPath
} else {
    $vcvars = Find-VcVars
    if (-not $vcvars) {
        $zigCommand = Get-Command zig.exe -ErrorAction SilentlyContinue
        if ($zigCommand) { $zig = $zigCommand.Source }
    }
}
if (-not $vcvars -and -not $zig) {
    throw "No C++ compiler was found. Use Visual Studio C++ tools or pass -ZigPath to a portable Zig distribution."
}

$previousZigLocalCache = [Environment]::GetEnvironmentVariable("ZIG_LOCAL_CACHE_DIR", "Process")
$previousZigGlobalCache = [Environment]::GetEnvironmentVariable("ZIG_GLOBAL_CACHE_DIR", "Process")
Push-Location -LiteralPath $OutputDirectory
try {
    if ($vcvars) {
        $discoveryCommand = 'call "{0}" >nul && where.exe cl.exe' -f $vcvars
        $compilerPaths = @(& $env:ComSpec /d /s /c $discoveryCommand)
        if ($LASTEXITCODE -ne 0) { throw "Unable to locate cl.exe after running vcvars64.bat." }
        $compilerPath = $compilerPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not $compilerPath) { throw "vcvars64.bat did not expose a usable cl.exe." }
        $buildManifest.compiler = [ordered]@{
            name = "MSVC"
            path = $compilerPath
            version = (Get-Item -LiteralPath $compilerPath).VersionInfo.FileVersion
            target = "x86_64-windows-msvc"
            imports = "Windows SDK selected by vcvars64.bat"
        }
        Write-BuildManifest -Manifest $buildManifest -Path $manifestPath
        Write-Host "Compiler: MSVC; all build outputs: $OutputDirectory"
        foreach ($target in $buildTargets) {
            $sourceHash = (Get-FileHash -LiteralPath $target.Source -Algorithm SHA256).Hash
            $baseName = [IO.Path]::GetFileNameWithoutExtension($target.Output)
            $objectPath = Join-Path $OutputDirectory ($baseName + ".obj")
            $debugPath = Join-Path $OutputDirectory ($baseName + ".pdb")
            $libraries = ($target.Libraries | ForEach-Object { $_ + ".lib" }) -join " "
            $command = 'call "{0}" >nul && cl.exe /nologo /std:c++17 /O2 /EHsc /W4 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0a00 /DWINVER=0x0a00 "{1}" /Fe:"{2}" /Fo"{3}" /Fd"{4}" /link /INCREMENTAL:NO {5}' -f `
                $vcvars, $target.Source, $target.Output, $objectPath, $debugPath, $libraries
            & $env:ComSpec /d /s /c $command
            if ($LASTEXITCODE -ne 0) { throw "MSVC build failed with exit code $LASTEXITCODE for $($target.Source)" }
            Add-TargetManifest -Target $target -SourceHash $sourceHash
        }
    } else {
        # Zig ships MinGW headers and Windows import-library definitions, so a
        # separate Windows SDK is optional. Its bundled compiler import uses
        # the versioned name d3dcompiler_47, not the SDK's d3dcompiler.lib name.
        $sdkLibrary = Find-SdkLibrary
        $sdkArguments = @()
        $shaderCompilerLibrary = "d3dcompiler_47"
        if ($sdkLibrary) {
            $sdkArguments = @("-L", $sdkLibrary)
            $shaderCompilerLibrary = "d3dcompiler"
            Write-Host "Compiler: Zig with Windows SDK import libraries ($sdkLibrary)"
        } else {
            Write-Host "Compiler: Zig with bundled MinGW headers/import libraries (no Windows SDK required)"
        }
        $compilerVersion = @(& $zig version)
        if ($LASTEXITCODE -ne 0) { throw "Unable to read the Zig compiler version." }
        $buildManifest.compiler = [ordered]@{
            name = "Zig"
            path = $zig
            version = ($compilerVersion -join " ").Trim()
            target = "x86_64-windows-gnu"
            imports = $(if ($sdkLibrary) { $sdkLibrary } else { "bundled-mingw" })
        }
        Write-BuildManifest -Manifest $buildManifest -Path $manifestPath
        [Environment]::SetEnvironmentVariable("ZIG_LOCAL_CACHE_DIR", (Join-Path $OutputDirectory "zig-local-cache"), "Process")
        [Environment]::SetEnvironmentVariable("ZIG_GLOBAL_CACHE_DIR", (Join-Path $OutputDirectory "zig-global-cache"), "Process")
        foreach ($target in $buildTargets) {
            $sourceHash = (Get-FileHash -LiteralPath $target.Source -Algorithm SHA256).Hash
            $libraries = @($target.Libraries | ForEach-Object {
                if ($_ -eq "d3dcompiler") { "-l$shaderCompilerLibrary" } else { "-l$_" }
            })
            $compilerArguments = @("c++", "-target", "x86_64-windows-gnu", "-std=c++17", "-O2",
                "-municode", "-DUNICODE", "-D_UNICODE", "-D_WIN32_WINNT=0x0a00", "-DWINVER=0x0a00",
                $target.Source, "-o", $target.Output) + $sdkArguments + $libraries
            & $zig @compilerArguments
            if ($LASTEXITCODE -ne 0) { throw "Zig build failed with exit code $LASTEXITCODE for $($target.Source)" }
            Add-TargetManifest -Target $target -SourceHash $sourceHash
        }
    }
    $buildManifest.status = "success"
    $buildManifest.completed_utc = [DateTime]::UtcNow.ToString("o")
    Write-BuildManifest -Manifest $buildManifest -Path $manifestPath
} catch {
    $buildManifest.status = "failed"
    $buildManifest.completed_utc = [DateTime]::UtcNow.ToString("o")
    Write-BuildManifest -Manifest $buildManifest -Path $manifestPath
    throw
} finally {
    [Environment]::SetEnvironmentVariable("ZIG_LOCAL_CACHE_DIR", $previousZigLocalCache, "Process")
    [Environment]::SetEnvironmentVariable("ZIG_GLOBAL_CACHE_DIR", $previousZigGlobalCache, "Process")
    Pop-Location
}

Write-Output $output
Write-Output $probeOutput
Write-Output $d3d12BenchOutput
Write-Output $manifestPath
