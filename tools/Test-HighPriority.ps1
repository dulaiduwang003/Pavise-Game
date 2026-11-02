param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$priorityWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $priorityWorkspace ('build\HighPriorityChecks-' + [Guid]::NewGuid().ToString('N')) }
$priorityOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $priorityOutput -Force
$priorityInputs = @(Get-ChildItem -LiteralPath (Join-Path $priorityWorkspace 'src') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -ne (Join-Path $priorityWorkspace 'src\PaviseApp\Settings.cs') } |
    Select-Object -ExpandProperty FullName)
$priorityInputs += Join-Path $priorityWorkspace 'tests\Official3106\MemorySettings.cs'
$priorityInputs += Join-Path $priorityWorkspace 'tests\Official3106\HighPriorityChecks.cs'
$priorityBefore = @($priorityInputs | ForEach-Object { [pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$priorityBefore | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $priorityOutput 'inputs.json') -Encoding UTF8
$priorityCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$priorityExe = Join-Path $priorityOutput 'HighPriorityChecks.exe'
$priorityResponse = Join-Path $priorityOutput 'sources.rsp'
[IO.File]::WriteAllLines($priorityResponse, [string[]]@($priorityInputs | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
& $priorityCompiler -nologo -unsafe+ -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
    -main:PaviseApp.HighPriorityChecks ('-out:' + $priorityExe) -reference:System.dll -reference:System.Core.dll `
    -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll `
    -reference:System.Xml.dll -reference:System.Security.dll ('@' + $priorityResponse) *> (Join-Path $priorityOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $priorityOutput 'compile.log'); throw 'High priority checks did not compile.' }
$priorityStart = New-Object Diagnostics.ProcessStartInfo
$priorityStart.FileName = $priorityExe
$priorityStart.UseShellExecute = $false; $priorityStart.CreateNoWindow = $true; $priorityStart.WindowStyle = 'Hidden'
$priorityStart.RedirectStandardOutput = $true; $priorityStart.RedirectStandardError = $true
$priorityProcess = [Diagnostics.Process]::Start($priorityStart)
try {
    $priorityOut = $priorityProcess.StandardOutput.ReadToEndAsync(); $priorityErr = $priorityProcess.StandardError.ReadToEndAsync()
    if (-not $priorityProcess.WaitForExit(30000)) { $priorityProcess.Kill(); throw 'High priority checks timed out.' }
    $priorityProcess.WaitForExit()
    $priorityResult = $priorityOut.Result + $priorityErr.Result
    [IO.File]::WriteAllText((Join-Path $priorityOutput 'result.txt'), $priorityResult)
    Write-Output $priorityResult
    if ($priorityProcess.ExitCode -ne 0) { throw 'High priority checks failed.' }
    $priorityChanged = @($priorityBefore | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 })
    if ($priorityChanged.Count) { throw 'Priority source inputs changed during verification; rerun against stable inputs.' }
    [pscustomobject]@{ExitCode=$priorityProcess.ExitCode;InputCount=$priorityBefore.Count;ChangedInputs=0;PaviseStarted=$false;NativePriorityWrites=$false;Output=$priorityOutput} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $priorityOutput 'verification.json') -Encoding UTF8
} finally { $priorityProcess.Dispose(); Write-Output ('OUTPUT ' + $priorityOutput) }
