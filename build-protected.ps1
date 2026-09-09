# @author bdth 2074055628@qq.com
# File purpose: build, protect, verify, and optionally sign the closed-source release binary.
[CmdletBinding()]
param(
    [string]$Output = "build\Pavise.protected.exe",
    [switch]$Force,
    [switch]$KeepStage,
    [switch]$SkipSmoke,
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.Drawing

$toolVersion = "1.6.0"
$toolUrl = "https://github.com/mkaring/ConfuserEx/releases/download/v1.6.0/ConfuserEx-CLI.zip"
$toolSha256 = "A00DE7CDDC740F7EDB1BAAB4C6C9073553DCC88F7E873D15B7FD34DDD33753D7"

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Assert-ChildPath([string]$Candidate, [string]$Parent) {
    $candidateFull = [IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    if (!$candidateFull.StartsWith($parentFull + "\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe path outside the expected parent: $candidateFull"
    }
    return $candidateFull
}

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $File"
    }
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace("-", "")
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Get-CscPath {
    $candidates = @(
        (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
        (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "csc.exe not found - install .NET Framework 4.x"
}

function Ensure-Confuser([string]$CacheRoot) {
    $versionDir = Join-Path $CacheRoot $toolVersion
    $cli = Join-Path $versionDir "Confuser.CLI.exe"
    if (Test-Path -LiteralPath $cli) { return $cli }

    New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
    if (Test-Path -LiteralPath $versionDir) {
        throw "Incomplete protector cache: $versionDir. Remove that exact directory and retry."
    }

    $zip = Join-Path $CacheRoot ("ConfuserEx-CLI-" + $toolVersion + ".zip")
    if (!(Test-Path -LiteralPath $zip)) {
        Write-Step "Downloading pinned protection tool"
        # Windows PowerShell 5.1 on a locked-down host may still default to
        # TLS 1.0/1.1, which github.com rejects. Add TLS 1.2 without dropping
        # any stronger protocol the OS already negotiates.
        [Net.ServicePointManager]::SecurityProtocol = `
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        # github.com downloads are unreachable on some networks even with TLS 1.2.
        # The pinned SHA-256 below makes untrusted mirrors safe, so walk the same
        # proxy routes the in-app update checker uses until one hash-verifies.
        $routes = @(
            $toolUrl,
            ("https://ghproxy.net/" + $toolUrl),
            ("https://gh-proxy.com/" + $toolUrl),
            ("https://ghfast.top/" + $toolUrl)
        )
        # Each route is tried twice: once through the system proxy, once with the
        # proxy bypassed. A configured-but-broken local proxy (e.g. a stale
        # 127.0.0.1:7890 entry) otherwise kills every HTTPS download while the
        # mirror hosts would be reachable directly.
        $downloaded = $false
        $lastError = $null
        $savedProxy = [Net.WebRequest]::DefaultWebProxy
        try {
            foreach ($route in $routes) {
                foreach ($direct in @($false, $true)) {
                    $label = if ($direct) { "$route (direct, no proxy)" } else { $route }
                    try {
                        if ($direct) { [Net.WebRequest]::DefaultWebProxy = New-Object Net.WebProxy }
                        else { [Net.WebRequest]::DefaultWebProxy = $savedProxy }
                        Invoke-WebRequest -UseBasicParsing -Uri $route -OutFile $zip -TimeoutSec 60
                        if ((Get-Sha256 $zip) -eq $toolSha256) { $downloaded = $true; break }
                        $lastError = "hash mismatch from $label"
                        Write-Host "Route served a different file, trying next: $label"
                        Remove-Item -LiteralPath $zip -Force
                    }
                    catch {
                        $lastError = $_.Exception.Message
                        Write-Host "Route failed: $label ($lastError)"
                        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
                    }
                }
                if ($downloaded) { break }
            }
        }
        finally { [Net.WebRequest]::DefaultWebProxy = $savedProxy }
        if (!$downloaded) {
            throw ("All download routes for the protection tool failed. Last error: $lastError`n" +
                "Offline fix: obtain ConfuserEx-CLI.zip v$toolVersion from any machine or mirror and place it at`n" +
                "  $zip`n" +
                "(SHA-256 must be $toolSha256 - the build verifies it, so the source does not need to be trusted.`n" +
                " Copying the whole cache folder from a machine that has built before also works: $CacheRoot)")
        }
    }
    $actual = Get-Sha256 $zip
    if ($actual -ne $toolSha256) {
        throw "Protection tool SHA-256 mismatch. Expected $toolSha256, got $actual"
    }

    New-Item -ItemType Directory -Path $versionDir | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $versionDir
    if (!(Test-Path -LiteralPath $cli)) {
        throw "Protection tool archive did not contain Confuser.CLI.exe"
    }
    return $cli
}

function New-ProtectionProject(
    [string]$Path,
    [string]$BaseDir,
    [string]$OutputDir,
    [string]$ModuleName,
    [string]$Seed
) {
    $baseXml = [Security.SecurityElement]::Escape($BaseDir)
    $outputXml = [Security.SecurityElement]::Escape($OutputDir)
    $moduleXml = [Security.SecurityElement]::Escape($ModuleName)
    $seedXml = [Security.SecurityElement]::Escape($Seed)
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<project outputDir="$outputXml" baseDir="$baseXml" seed="$seedXml">
  <rule pattern="true" preset="none" inherit="false">
    <protection id="anti ildasm" />
    <protection id="rename">
      <argument name="mode" value="unicode" />
      <argument name="flatten" value="true" />
    </protection>
    <protection id="constants">
      <argument name="mode" value="dynamic" />
      <argument name="elements" value="SNPI" />
    </protection>
    <protection id="ctrl flow">
      <argument name="type" value="switch" />
      <argument name="predicate" value="expression" />
      <argument name="intensity" value="75" />
      <argument name="depth" value="4" />
    </protection>
    <protection id="ref proxy">
      <argument name="mode" value="strong" />
      <argument name="encoding" value="expression" />
    </protection>
    <protection id="anti tamper">
      <argument name="mode" value="normal" />
    </protection>
  </rule>
  <module path="$moduleXml" />
</project>
"@
    [IO.File]::WriteAllText($Path, $xml, (New-Object Text.UTF8Encoding($false)))
}

function Compile-SmokeBinary([string]$Repo, [string]$OutputPath) {
    $csc = Get-CscPath
    $sourceFiles = Get-ChildItem -LiteralPath (Join-Path $Repo "src") -Filter "*.cs" -Recurse |
        ForEach-Object { $_.FullName }
    if ($sourceFiles.Count -eq 0) { throw "No source files found for smoke build" }
    $arguments = @(
        "-nologo", "-target:winexe", "-optimize+", "-codepage:65001",
        ("-out:" + $OutputPath),
        "-reference:System.dll", "-reference:System.Drawing.dll",
        "-reference:System.Windows.Forms.dll", "-reference:System.Core.dll",
        "-reference:System.Management.dll", "-reference:System.Xml.dll"
    ) + $sourceFiles
    Invoke-Checked $csc $arguments
}

function Protect-Binary(
    [string]$Cli,
    [string]$InputPath,
    [string]$OutputDir,
    [string]$Seed
) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
    $config = Join-Path ([IO.Path]::GetDirectoryName($InputPath)) (([IO.Path]::GetFileNameWithoutExtension($InputPath)) + ".crproj")
    New-ProtectionProject $config ([IO.Path]::GetDirectoryName($InputPath)) $OutputDir ([IO.Path]::GetFileName($InputPath)) $Seed
    # ConfuserEx 1.6 assumes it owns a Windows console for its colored output.
    # A short-lived hidden cmd.exe gives it the console it expects while stdout
    # is captured to a log. -n suppresses the end-of-run "press any key" pause
    # so the wrapper never depends on stdin redirection to unblock.
    $log = $config + ".log"
    $hostCmd = $config + ".cmd"
    $hostText = "@echo off`r`nchcp 65001 >nul`r`n`"$Cli`" -n `"$config`" > `"$log`" 2>&1`r`nexit /b %errorlevel%`r`n"
    [IO.File]::WriteAllText($hostCmd, $hostText, [Text.Encoding]::ASCII)
    $process = Start-Process -FilePath $env:ComSpec -ArgumentList @("/d", "/c", ('"' + $hostCmd + '"')) `
        -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit(180000)) {
        try { $process.Kill() } catch { }
        throw "Protector did not exit within three minutes"
    }
    $logText = if (Test-Path -LiteralPath $log) { [IO.File]::ReadAllText($log) } else { "" }
    if ($logText) { Write-Host $logText.TrimEnd() }
    if ($process.ExitCode -ne 0) {
        throw "Protector failed with exit code $($process.ExitCode). See $log"
    }
    $protected = Join-Path $OutputDir ([IO.Path]::GetFileName($InputPath))
    if (!(Test-Path -LiteralPath $protected)) { throw "Protector did not create $protected" }
    return $protected
}

function Assert-Obfuscation([string]$RawPath, [string]$ProtectedPath) {
    $rawBytes = [IO.File]::ReadAllBytes($RawPath)
    $protectedBytes = [IO.File]::ReadAllBytes($ProtectedPath)
    $rawText = [Text.Encoding]::ASCII.GetString($rawBytes)
    $protectedText = [Text.Encoding]::ASCII.GetString($protectedBytes)
    $markers = @("PaviseApp", "SuppressionCore", "RenderLane", "PolicyResolver", "GameSessionDetector")
    $removed = 0
    foreach ($marker in $markers) {
        if ($rawText.Contains($marker) -and !$protectedText.Contains($marker)) { $removed++ }
    }
    if ($removed -lt 4) {
        throw "Obfuscation verification failed: only $removed of $($markers.Count) metadata markers were removed"
    }
    if ($protectedBytes.Length -lt 65536) { throw "Protected output is unexpectedly small" }
    $rawAssembly = [Reflection.AssemblyName]::GetAssemblyName($RawPath)
    $protectedAssembly = [Reflection.AssemblyName]::GetAssemblyName($ProtectedPath)
    if ($rawAssembly.Version.ToString() -ne $protectedAssembly.Version.ToString()) {
        throw "Protection changed AssemblyVersion from $($rawAssembly.Version) to $($protectedAssembly.Version)"
    }
    $rawInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($RawPath)
    $protectedInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($ProtectedPath)
    if ($rawInfo.FileVersion -ne $protectedInfo.FileVersion) {
        throw "Protection changed FileVersion from $($rawInfo.FileVersion) to $($protectedInfo.FileVersion)"
    }
    if ($rawInfo.ProductVersion -ne $protectedInfo.ProductVersion) {
        throw "Protection changed ProductVersion from $($rawInfo.ProductVersion) to $($protectedInfo.ProductVersion)"
    }
    Write-Host "Metadata markers removed: $removed/$($markers.Count)"
    Write-Host "Raw bytes: $($rawBytes.Length); protected bytes: $($protectedBytes.Length)"
}

function Invoke-Smoke([string]$ExePath, [string]$StageDir) {
    $png = Join-Path $StageDir "smoke.png"
    $process = Start-Process -FilePath $ExePath -ArgumentList @("--geniconpng", $png) -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit(30000)) {
        try { $process.Kill() } catch { }
        throw "Protected smoke build did not exit within 30 seconds"
    }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $png)) {
        throw "Protected smoke build failed with exit code $($process.ExitCode)"
    }
    $image = [Drawing.Image]::FromFile($png)
    try {
        if ($image.Width -ne 256 -or $image.Height -ne 256) {
            throw "Protected smoke image has the wrong dimensions"
        }
    }
    finally { $image.Dispose() }

    # Exercise the full WinForms composition path, settings initialization,
    # topology probing, GameMode construction, and a much wider P/Invoke slice.
    $panel = Join-Path $StageDir "smoke-panel.png"
    $process = Start-Process -FilePath $ExePath -ArgumentList @("--screenshot", $panel, "0", "en") `
        -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit(45000)) {
        try { $process.Kill() } catch { }
        throw "Protected UI smoke build did not exit within 45 seconds"
    }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $panel)) {
        throw "Protected UI smoke build failed with exit code $($process.ExitCode)"
    }
    $image = [Drawing.Image]::FromFile($panel)
    try {
        if ($image.Width -lt 500 -or $image.Height -lt 400) {
            throw "Protected UI smoke image has implausible dimensions"
        }
    }
    finally { $image.Dispose() }
}

function Find-SignTool {
    if ($env:PAVISE_SIGNTOOL -and (Test-Path -LiteralPath $env:PAVISE_SIGNTOOL)) {
        return $env:PAVISE_SIGNTOOL
    }
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    return $null
}

function Sign-Output([string]$FilePath) {
    if (!$env:PAVISE_SIGN_CERT_SHA1) { return $false }
    $signTool = Find-SignTool
    if (!$signTool) { throw "PAVISE_SIGN_CERT_SHA1 is set but signtool.exe was not found" }
    $timestamp = $env:PAVISE_TIMESTAMP_URL
    if (!$timestamp) { $timestamp = "http://timestamp.digicert.com" }
    Invoke-Checked $signTool @(
        "sign", "/sha1", $env:PAVISE_SIGN_CERT_SHA1,
        "/fd", "SHA256", "/tr", $timestamp, "/td", "SHA256", $FilePath
    )
    Invoke-Checked $signTool @("verify", "/pa", "/v", $FilePath)
    return $true
}

$repo = [IO.Path]::GetFullPath($PSScriptRoot)
$outputPath = if ([IO.Path]::IsPathRooted($Output)) {
    [IO.Path]::GetFullPath($Output)
} else {
    [IO.Path]::GetFullPath((Join-Path $repo $Output))
}
if ((Test-Path -LiteralPath $outputPath) -and !$Force) {
    throw "Output already exists: $outputPath. Pass -Force to replace it."
}

$stageRoot = Join-Path $env:TEMP "Pavise-ProtectedBuild"
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
$stage = Assert-ChildPath (Join-Path $stageRoot ([Guid]::NewGuid().ToString("N"))) $stageRoot
New-Item -ItemType Directory -Path $stage | Out-Null
$succeeded = $false

try {
    $cacheRoot = Join-Path $env:LOCALAPPDATA "PaviseBuildTools\ConfuserEx"
    $protector = Ensure-Confuser $cacheRoot
    Write-Host "Protection tier: free managed-code obfuscation (ConfuserEx 1.6.0)"
    $seed = [Guid]::NewGuid().ToString("N") + [Guid]::NewGuid().ToString("N")

    Write-Step "Compiling release input"
    $inputDir = Join-Path $stage "input"
    New-Item -ItemType Directory -Path $inputDir | Out-Null
    $raw = Join-Path $inputDir "Pavise.exe"
    Invoke-Checked (Join-Path $repo "build.cmd") @("-b", "dev", $raw)

    Write-Step "Applying managed-code protection"
    $protectedDir = Join-Path $stage "protected"
    $protected = Protect-Binary $protector $raw $protectedDir $seed

    Write-Step "Verifying protected metadata and PE structure"
    Assert-Obfuscation $raw $protected

    if (!$SkipSmoke) {
        Write-Step "Running protected no-elevation smoke build"
        $smokeRaw = Join-Path $stage "Pavise.smoke.raw.exe"
        Compile-SmokeBinary $repo $smokeRaw
        $smokeProtected = Protect-Binary $protector $smokeRaw (Join-Path $stage "smoke-protected") ($seed + "smoke")
        Invoke-Smoke $smokeProtected $stage
    }

    Write-Step "Writing final artifact"
    $outputDir = [IO.Path]::GetDirectoryName($outputPath)
    if (!(Test-Path -LiteralPath $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }
    Copy-Item -LiteralPath $protected -Destination $outputPath -Force

    $signed = Sign-Output $outputPath
    if ($RequireSignature -and !$signed) {
        Remove-Item -LiteralPath $outputPath -Force
        throw "A signed release was required. Set PAVISE_SIGN_CERT_SHA1 and PAVISE_SIGNTOOL."
    }

    $hash = Get-Sha256 $outputPath
    $hashFile = $outputPath + ".sha256"
    [IO.File]::WriteAllText($hashFile, ($hash + "  " + [IO.Path]::GetFileName($outputPath) + [Environment]::NewLine), (New-Object Text.UTF8Encoding($false)))
    Write-Host ""
    Write-Host "Protected build OK -> $outputPath" -ForegroundColor Green
    Write-Host "SHA-256: $hash"
    Write-Host $(if ($signed) { "Authenticode: signed and verified" } else { "Authenticode: unsigned" })
    $succeeded = $true
}
finally {
    if (!$KeepStage) {
        try {
            $safeStage = Assert-ChildPath $stage $stageRoot
            if (Test-Path -LiteralPath $safeStage) {
                Remove-Item -LiteralPath $safeStage -Recurse -Force
            }
        }
        catch {
            Write-Warning "Could not remove protected-build stage: $($_.Exception.Message)"
        }
    }
    elseif (Test-Path -LiteralPath $stage) {
        Write-Host "Stage kept at $stage"
    }
}

if (!$succeeded) { exit 1 }
