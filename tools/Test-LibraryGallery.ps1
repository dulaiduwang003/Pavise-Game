param([string]$OutputDirectory, [switch]$SingleCase, [switch]$GalleryOnly, [switch]$CompileOnly, [switch]$PhotoOnly, [string]$PhotoProfileFile)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$galleryWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('Pavise-LibraryGallery-' + [Guid]::NewGuid().ToString('N')) }
$galleryOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $galleryOutput -Force
$gallerySources = @(& rg --files (Join-Path $galleryWorkspace 'src') -g '*.cs')
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate product sources' }
foreach ($relative in @('tests/LibraryGalleryChecks.cs')) {
    $gallerySources += Join-Path $galleryWorkspace $relative
}
$galleryInputs = @($gallerySources | ForEach-Object {
    $original = [IO.Path]::GetFullPath($_)
    $relative = $original.Substring($galleryWorkspace.Length + 1)
    $frozen = Join-Path (Join-Path $galleryOutput 'frozen-inputs') $relative
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($frozen)) -Force
    [IO.File]::WriteAllBytes($frozen, [IO.File]::ReadAllBytes($original))
    [pscustomobject]@{ Path = $relative; Original = $original; Frozen = $frozen; SHA256 = (Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$galleryInputs | Export-Csv -LiteralPath (Join-Path $galleryOutput 'input-sha256.tsv') -Delimiter ([char]9) -NoTypeInformation
[IO.File]::WriteAllBytes((Join-Path $galleryOutput 'Test-LibraryGallery.ps1'), [IO.File]::ReadAllBytes($PSCommandPath))
function Invoke-GalleryHidden([string]$File, [string]$Arguments, [int]$LimitMs) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $File; $start.Arguments = $Arguments; $start.WorkingDirectory = $galleryOutput
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = 'Hidden'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $start.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
    $child = New-Object Diagnostics.Process; $child.StartInfo = $start
    try {
        $null = $child.Start()
        $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
        $watch = [Diagnostics.Stopwatch]::StartNew(); $timedOut = $false
        while (-not $child.WaitForExit(1000)) {
            if ($watch.ElapsedMilliseconds -gt $LimitMs) { $timedOut = $true; $child.Kill(); break }
        }
        $child.WaitForExit()
        [pscustomobject]@{ Exit = $(if ($timedOut) { 124 } else { $child.ExitCode }); Text = $stdout.Result + $stderr.Result; TimedOut = $timedOut }
    } finally { $child.Dispose() }
}
$galleryCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$galleryExe = Join-Path $galleryOutput 'LibraryGalleryChecks.exe'
[IO.File]::WriteAllBytes(($galleryExe + '.config'), [IO.File]::ReadAllBytes((Join-Path $galleryWorkspace 'Pavise.exe.config')))
$galleryManifest = Join-Path $galleryOutput 'fixture.manifest'
$galleryManifestText = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
 <assemblyIdentity version="1.0.0.0" name="Pavise.LibraryGalleryChecks" />
 <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false" /></requestedPrivileges></security></trustInfo>
 <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
  <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
  <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
 </windowsSettings></application>
 <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /></application></compatibility>
</assembly>
'@
[IO.File]::WriteAllText($galleryManifest, $galleryManifestText, (New-Object Text.UTF8Encoding($false)))
$galleryRsp = Join-Path $galleryOutput 'sources.rsp'
$galleryArgs = @('/nologo', '/unsafe+', '/target:exe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output', '/nowarn:0162,0649',
    '/define:PAVISE_SELFTEST;PAVISE_LIBRARY_GALLERY_CHECKS',
    '/main:PaviseApp.LibraryGalleryChecks', ('/out:"' + $galleryExe + '"'),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Management.dll', '/reference:System.Xml.dll', ('/win32manifest:"' + $galleryManifest + '"'))
$galleryArgs += @($galleryInputs | ForEach-Object { '"' + $_.Frozen + '"' })
[IO.File]::WriteAllLines($galleryRsp, $galleryArgs, (New-Object Text.UTF8Encoding($false)))
$galleryResults = New-Object 'Collections.Generic.List[object]'
try {
    $compile = Invoke-GalleryHidden $galleryCompiler ('@"' + $galleryRsp + '"') 60000
    [IO.File]::WriteAllText((Join-Path $galleryOutput 'compile.log'), $compile.Text)
    $galleryResults.Add([pscustomobject]@{ Step = 'compile'; Exit = $compile.Exit; Pass = ($compile.Exit -eq 0) })
    if ($compile.Exit -ne 0) { throw ('Library gallery fixture did not compile: ' + $compile.Text) }
    if (-not $CompileOnly) {
        $mode = if ($SingleCase) { ' --single' } else { '' }
        if ($GalleryOnly) { $mode += ' --gallery-only' }
        if ($PhotoOnly) {
            if (-not $PhotoProfileFile) { $PhotoProfileFile = Join-Path $env:APPDATA 'PaviseAuto/Pavise.profiles.dat' }
            $mode += ' --photo "' + [IO.Path]::GetFullPath($PhotoProfileFile) + '"'
        }
        $run = Invoke-GalleryHidden $galleryExe ('"' + $galleryOutput + '"' + $mode) 360000
        [IO.File]::WriteAllText((Join-Path $galleryOutput 'result.txt'), $run.Text); Write-Output $run.Text
        $pass = $run.Exit -eq 0 -and $run.Text -match 'PASS library gallery assertions='
        $galleryResults.Add([pscustomobject]@{ Step = 'private desktop UI'; Exit = $run.Exit; TimedOut = $run.TimedOut; Pass = $pass })
        if (-not $pass) { throw 'Library gallery UI checks failed or timed out' }
        $negative = Invoke-GalleryHidden $galleryExe '--negative-control' 10000
        [IO.File]::WriteAllText((Join-Path $galleryOutput 'negative-control.txt'), $negative.Text)
        $negativePass = $negative.Exit -eq 3 -and $negative.Text.Contains('intentional original-icon mismatch')
        $galleryResults.Add([pscustomobject]@{ Step = 'intentional negative control'; Exit = $negative.Exit; Pass = $negativePass })
        if (-not $negativePass) { throw 'Library gallery negative control did not fail as expected' }
    } else { Write-Output 'PASS library gallery compile-only; fixture not executed' }
} finally {
    $changed = @($galleryInputs | Where-Object { (Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.SHA256 })
    [pscustomobject]@{ SingleCase = [bool]$SingleCase; GalleryOnly = [bool]$GalleryOnly; CompileOnly = [bool]$CompileOnly; PhotoOnly = [bool]$PhotoOnly; TestedFrozenSourceCount = $galleryInputs.Count;
        WorkspaceMatchesSnapshot = ($changed.Count -eq 0); ChangedSources = @($changed | ForEach-Object { $_.Path });
        Screenshots = @(Get-ChildItem -LiteralPath $galleryOutput -File -Filter '*.png' | ForEach-Object { $_.Name })
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $galleryOutput 'verification.json') -Encoding UTF8
    $galleryResults | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $galleryOutput 'results.json') -Encoding UTF8
    Write-Output ('OUTPUT ' + $galleryOutput)
}
if ($changed.Count -ne 0) { throw 'Source changed during checks; results apply only to retained frozen inputs' }
