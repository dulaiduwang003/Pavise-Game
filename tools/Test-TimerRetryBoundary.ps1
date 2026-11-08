param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$timerRetryWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $timerRetryWorkspace ('build/TimerRetryBoundary-' + [Guid]::NewGuid().ToString('N')) }
$timerRetryOutput = [IO.Path]::GetFullPath($OutputDirectory)
$timerRetryFrozen = Join-Path $timerRetryOutput 'frozen'
if (Test-Path -LiteralPath $timerRetryFrozen) { throw 'Choose a fresh output directory; previous evidence is retained.' }
$null = New-Item -ItemType Directory -Path $timerRetryFrozen -Force
$timerRetryInputs = @(Get-ChildItem -LiteralPath (Join-Path $timerRetryWorkspace 'src') -Recurse -File -Filter '*.cs' | ForEach-Object { $_.FullName })
$timerRetryInputs += @((Join-Path $timerRetryWorkspace 'tests/TimerRetryBoundaryChecks.cs'), $PSCommandPath)
$timerRetryManifest = @($timerRetryInputs | ForEach-Object {
    $relative = $_.Substring($timerRetryWorkspace.Length + 1); $copy = Join-Path $timerRetryFrozen $relative
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($copy)) -Force
    [IO.File]::WriteAllBytes($copy, [IO.File]::ReadAllBytes($_))
    [pscustomobject]@{Original=$_;Frozen=$copy;SHA256=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash}
})
$timerRetryManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $timerRetryOutput 'source-manifest.json') -Encoding utf8
$timerRetrySources = @($timerRetryManifest | Where-Object { $_.Frozen.EndsWith('.cs') } | ForEach-Object { '"' + $_.Frozen + '"' })
$timerRetryRsp = Join-Path $timerRetryOutput 'sources.rsp'
[IO.File]::WriteAllLines($timerRetryRsp, [string[]]$timerRetrySources, [Text.UTF8Encoding]::new($true))
$timerRetryExe = Join-Path $timerRetryOutput 'Pavise.timer-retry-boundary-check.exe'
$timerRetryCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $timerRetryCompiler -nologo -unsafe+ -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 -define:PAVISE_SELFTEST -main:PaviseApp.TimerRetryBoundaryChecks ('-out:'+$timerRetryExe) -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll ('@'+$timerRetryRsp) *> (Join-Path $timerRetryOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $timerRetryOutput 'compile.log'); throw 'Timer retry fixture compilation failed.' }
$timerRetryStart = [Diagnostics.ProcessStartInfo]::new()
$timerRetryStart.FileName=$timerRetryExe; $timerRetryStart.WorkingDirectory=$timerRetryOutput
$timerRetryStart.UseShellExecute=$false; $timerRetryStart.CreateNoWindow=$true; $timerRetryStart.WindowStyle='Hidden'
$timerRetryStart.RedirectStandardOutput=$true; $timerRetryStart.RedirectStandardError=$true
$timerRetryProcess=[Diagnostics.Process]::Start($timerRetryStart)
try {
    $timerRetryOut=$timerRetryProcess.StandardOutput.ReadToEndAsync(); $timerRetryErr=$timerRetryProcess.StandardError.ReadToEndAsync()
    if (!$timerRetryProcess.WaitForExit(30000)) { $timerRetryProcess.Kill(); throw 'Hidden timer retry fixture exceeded 30 seconds.' }
    $timerRetryProcess.WaitForExit(); $timerRetryCode=$timerRetryProcess.ExitCode
    $timerRetryResult=$timerRetryOut.Result+$timerRetryErr.Result
    [IO.File]::WriteAllText((Join-Path $timerRetryOutput 'result.txt'), $timerRetryResult, [Text.UTF8Encoding]::new($true))
    Write-Output $timerRetryResult
} finally { $timerRetryProcess.Dispose() }
$timerRetryDrift=@($timerRetryManifest | Where-Object { (Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash -ne $_.SHA256 } | ForEach-Object { $_.Original })
[pscustomobject]@{Passed=$timerRetryCode -eq 0;ExitCode=$timerRetryCode;SourceInputs=$timerRetryManifest.Count;SourceDrift=$timerRetryDrift;ProductStartup=0;NativeMutations=0;Settings='transient';UiWindows=0} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $timerRetryOutput 'verification.json') -Encoding utf8
Write-Output ('OUTPUT '+$timerRetryOutput)
if ($timerRetryCode -ne 0) { throw 'Timer retry boundary checks failed.' }
if ($timerRetryDrift.Count -ne 0) { throw 'Sources changed during frozen validation; rerun against final sources.' }
