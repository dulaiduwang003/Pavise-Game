param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Pavise-SourceCleanup-' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory.' }
$null = New-Item -ItemType Directory -Path $output
$sources = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$sources += Join-Path $workspace 'tests/SourceCleanupChecks.cs'
$before = @($sources | ForEach-Object { [pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_).Hash} })
$rsp = Join-Path $output 'sources.rsp'
[IO.File]::WriteAllLines($rsp, [string[]]@($sources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($false)))
$exe = Join-Path $output 'SourceCleanupChecks.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler -nologo -unsafe+ -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 -define:PAVISE_SELFTEST -main:PaviseApp.SourceCleanupChecks ('-out:'+$exe) -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll ('@'+$rsp) *> (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'compile.log'); throw 'Source cleanup checks failed to compile.' }
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName=$exe; $start.WorkingDirectory=$output; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'
$start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
$child = [Diagnostics.Process]::Start($start)
try {
    $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(20000)) { $child.Kill(); throw 'Owned cleanup fixture exceeded 20 seconds.' }
    $child.WaitForExit(); $result=$stdout.Result+$stderr.Result
    [IO.File]::WriteAllText((Join-Path $output 'result.txt'), $result)
    Write-Output $result
    if ($child.ExitCode -ne 0) { throw 'Source cleanup regression failed.' }
} finally { $child.Dispose() }
$drift = @($before | Where-Object { (Get-FileHash -LiteralPath $_.Path).Hash -ne $_.SHA256 })
if ($drift.Count) { throw 'Sources changed during the checks.' }
[pscustomobject]@{Passed=$true;SourceDrift=0;ProductStarted=$false;RegistryWrites=0;VisibleWindows=0} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
