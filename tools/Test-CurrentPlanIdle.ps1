param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace ('build/CurrentPlanIdle-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$sources = @(
    (Join-Path $workspace 'src/PaviseApp/LaboratoryPower.cs'),
    (Join-Path $workspace 'tests/LaboratoryPowerChecks.cs'),
    (Join-Path $workspace 'tests/Official3106/CurrentPlanIdleChecks.cs')
)
$manifest = @($sources | ForEach-Object { [pscustomobject]@{ Path=$_; SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-manifest.json') -Encoding UTF8
$exe = Join-Path $OutputDirectory 'CurrentPlanIdle.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler -nologo -target:exe -platform:x64 -codepage:65001 '-define:PAVISE_LAB_POWER_CHECKS;PAVISE_LAB_POWER_ISOLATED' '-main:PaviseApp.CurrentPlanIdleChecks' ('-out:' + $exe) $sources *> (Join-Path $OutputDirectory 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $OutputDirectory 'compile.log'); throw 'CurrentPlanIdle compilation failed' }
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName=$exe; $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle='Hidden'
$info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
$child=New-Object Diagnostics.Process
$child.StartInfo=$info
try {
    $null=$child.Start()
    $stdout=$child.StandardOutput.ReadToEndAsync(); $stderr=$child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(45000)) { $child.Kill(); throw 'CurrentPlanIdle timed out' }
    $child.WaitForExit(); $result=$stdout.Result + $stderr.Result
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.txt'),$result)
    Write-Output $result
    if ($child.ExitCode -ne 0) { throw 'CurrentPlanIdle failed' }
} finally { $child.Dispose() }
if (@($manifest | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 }).Count -gt 0) { throw 'Sources changed during tests' }
Write-Output ('OUTPUT ' + $OutputDirectory)
