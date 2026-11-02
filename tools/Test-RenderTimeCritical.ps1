param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$renderWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $renderWorkspace ('build\RenderTimeCriticalChecks-' + [Guid]::NewGuid().ToString('N')) }
$renderOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $renderOutput -Force
$renderInputs = @(Get-ChildItem -LiteralPath (Join-Path $renderWorkspace 'src') -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -ne (Join-Path $renderWorkspace 'src\PaviseApp\Settings.cs') } |
    Select-Object -ExpandProperty FullName)
$renderInputs += Join-Path $renderWorkspace 'tests\Official3106\MemorySettings.cs'
$renderInputs += Join-Path $renderWorkspace 'tests\Official3106\RenderTimeCriticalChecks.cs'
$renderBefore = @($renderInputs | ForEach-Object { [pscustomobject]@{Path=$_;SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
$renderBefore | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $renderOutput 'inputs.json') -Encoding UTF8
$renderCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$renderExe = Join-Path $renderOutput 'RenderTimeCriticalChecks.exe'
$renderResponse = Join-Path $renderOutput 'sources.rsp'
[IO.File]::WriteAllLines($renderResponse, [string[]]@($renderInputs | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
& $renderCompiler -nologo -unsafe+ -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
    -main:PaviseApp.RenderTimeCriticalChecks ('-out:' + $renderExe) -reference:System.dll -reference:System.Core.dll `
    -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll `
    -reference:System.Xml.dll -reference:System.Security.dll ('@' + $renderResponse) *> (Join-Path $renderOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $renderOutput 'compile.log'); throw 'Render TimeCritical checks did not compile.' }
$renderStart = New-Object Diagnostics.ProcessStartInfo
$renderStart.FileName = $renderExe
$renderStart.UseShellExecute = $false; $renderStart.CreateNoWindow = $true; $renderStart.WindowStyle = 'Hidden'
$renderStart.RedirectStandardOutput = $true; $renderStart.RedirectStandardError = $true
$renderProcess = [Diagnostics.Process]::Start($renderStart)
try {
    $renderOut = $renderProcess.StandardOutput.ReadToEndAsync(); $renderErr = $renderProcess.StandardError.ReadToEndAsync()
    if (-not $renderProcess.WaitForExit(30000)) { $renderProcess.Kill(); throw 'Render TimeCritical checks timed out.' }
    $renderProcess.WaitForExit()
    $renderResult = $renderOut.Result + $renderErr.Result
    [IO.File]::WriteAllText((Join-Path $renderOutput 'result.txt'), $renderResult)
    Write-Output $renderResult
    if ($renderProcess.ExitCode -ne 0) { throw 'Render TimeCritical checks failed.' }
    $renderChanged = @($renderBefore | Where-Object { (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256 })
    if ($renderChanged.Count) { throw 'Render TimeCritical source inputs changed during verification; rerun against stable inputs.' }
    [pscustomobject]@{ExitCode=$renderProcess.ExitCode;InputCount=$renderBefore.Count;ChangedInputs=0;PaviseStarted=$false;NativePriorityWrites=$false;EtwStarted=$false;Output=$renderOutput} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $renderOutput 'verification.json') -Encoding UTF8
} finally { $renderProcess.Dispose(); Write-Output ('OUTPUT ' + $renderOutput) }
