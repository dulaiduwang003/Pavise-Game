param([string]$OutputDirectory, [switch]$SingleCase, [switch]$ShortCase, [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$floatingWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('Pavise-FloatingWorkspace-' + [Guid]::NewGuid().ToString('N')) }
$floatingOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $floatingOutput -Force
$floatingSources = @(& rg --files (Join-Path $floatingWorkspace 'src') -g '*.cs')
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate product sources' }
foreach ($relative in @('tests/ManualModeChecks.cs', 'tests/PerformanceWorkspaceChecks.cs', 'tests/FloatingWorkspaceChecks.cs')) {
    $floatingSources += Join-Path $floatingWorkspace $relative
}
$floatingInputs = @($floatingSources | ForEach-Object {
    $original = [IO.Path]::GetFullPath($_)
    $relative = $original.Substring($floatingWorkspace.Length + 1)
    $frozen = Join-Path (Join-Path $floatingOutput 'frozen-inputs') $relative
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($frozen)) -Force
    [IO.File]::WriteAllBytes($frozen, [IO.File]::ReadAllBytes($original))
    [pscustomobject]@{ Path = $relative; Original = $original; Frozen = $frozen; SHA256 = (Get-FileHash -LiteralPath $frozen -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$floatingInputs | Export-Csv -LiteralPath (Join-Path $floatingOutput 'input-sha256.tsv') -Delimiter ([char]9) -NoTypeInformation
[IO.File]::WriteAllBytes((Join-Path $floatingOutput 'Test-FloatingWorkspace.ps1'), [IO.File]::ReadAllBytes($PSCommandPath))
function Invoke-FloatingHidden([string]$File, [string]$Arguments, [int]$LimitMs) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $File; $start.Arguments = $Arguments; $start.WorkingDirectory = $floatingOutput
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
$floatingCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$floatingExe = Join-Path $floatingOutput 'FloatingWorkspaceChecks.exe'
[IO.File]::WriteAllBytes(($floatingExe + '.config'), [IO.File]::ReadAllBytes((Join-Path $floatingWorkspace 'Pavise.exe.config')))
$floatingManifest = Join-Path $floatingOutput 'fixture.manifest'
$floatingManifestText = @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
 <assemblyIdentity version="1.0.0.0" name="Pavise.FloatingWorkspaceChecks" />
 <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false" /></requestedPrivileges></security></trustInfo>
 <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
  <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
  <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
 </windowsSettings></application>
 <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /></application></compatibility>
</assembly>
'@
[IO.File]::WriteAllText($floatingManifest, $floatingManifestText, (New-Object Text.UTF8Encoding($false)))
$floatingRsp = Join-Path $floatingOutput 'sources.rsp'
$floatingArgs = @('/nologo', '/unsafe+', '/target:exe', '/platform:x64', '/optimize+', '/codepage:65001', '/utf8output', '/nowarn:0162,0649',
    '/define:PAVISE_SELFTEST;PAVISE_MANUAL_MODE_CHECKS;PAVISE_PERFORMANCE_PAGE_CHECKS;PAVISE_FLOATING_WORKSPACE_CHECKS',
    '/main:PaviseApp.FloatingWorkspaceChecks', ('/out:"' + $floatingExe + '"'),
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Management.dll', '/reference:System.Xml.dll', ('/win32manifest:"' + $floatingManifest + '"'))
$floatingArgs += @($floatingInputs | ForEach-Object { '"' + $_.Frozen + '"' })
[IO.File]::WriteAllLines($floatingRsp, $floatingArgs, (New-Object Text.UTF8Encoding($false)))
$floatingResults = New-Object 'Collections.Generic.List[object]'
try {
    $compile = Invoke-FloatingHidden $floatingCompiler ('@"' + $floatingRsp + '"') 60000
    [IO.File]::WriteAllText((Join-Path $floatingOutput 'compile.log'), $compile.Text)
    $floatingResults.Add([pscustomobject]@{ Step = 'compile'; Exit = $compile.Exit; Pass = ($compile.Exit -eq 0) })
    if ($compile.Exit -ne 0) { throw ('Floating workspace fixture did not compile: ' + $compile.Text) }
    if (-not $CompileOnly) {
        $mode = if ($ShortCase) { ' --short' } elseif ($SingleCase) { ' --single' } else { '' }
        $run = Invoke-FloatingHidden $floatingExe ('"' + $floatingOutput + '"' + $mode) 360000
        [IO.File]::WriteAllText((Join-Path $floatingOutput 'result.txt'), $run.Text); Write-Output $run.Text
        $pass = $run.Exit -eq 0 -and $run.Text -match 'PASS floating workspace assertions='
        $floatingResults.Add([pscustomobject]@{ Step = 'private desktop UI'; Exit = $run.Exit; TimedOut = $run.TimedOut; Pass = $pass })
        if (-not $pass) { throw 'Floating workspace UI checks failed or timed out' }
        $negative = Invoke-FloatingHidden $floatingExe '--negative-control' 10000
        [IO.File]::WriteAllText((Join-Path $floatingOutput 'negative-control.txt'), $negative.Text)
        $negativePass = $negative.Exit -eq 3 -and $negative.Text.Contains('intentional Dock overflow')
        $floatingResults.Add([pscustomobject]@{ Step = 'intentional negative control'; Exit = $negative.Exit; Pass = $negativePass })
        if (-not $negativePass) { throw 'Floating workspace negative control did not fail as expected' }
    } else { Write-Output 'PASS floating workspace compile-only; fixture not executed' }
} finally {
    $changed = @($floatingInputs | Where-Object { (Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.SHA256 })
    [pscustomobject]@{ SingleCase = [bool]$SingleCase; ShortCase = [bool]$ShortCase; CompileOnly = [bool]$CompileOnly; TestedFrozenSourceCount = $floatingInputs.Count;
        WorkspaceMatchesSnapshot = ($changed.Count -eq 0); ChangedSources = @($changed | ForEach-Object { $_.Path });
        Screenshots = @(Get-ChildItem -LiteralPath $floatingOutput -File -Filter '*.png' | ForEach-Object { $_.Name })
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $floatingOutput 'verification.json') -Encoding UTF8
    $floatingResults | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $floatingOutput 'results.json') -Encoding UTF8
    Write-Output ('OUTPUT ' + $floatingOutput)
}
if ($changed.Count -ne 0) { throw 'Source changed during checks; results apply only to retained frozen inputs' }
