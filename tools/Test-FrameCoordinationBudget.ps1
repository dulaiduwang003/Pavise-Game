param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('Pavise-FrameBudget-' + [Guid]::NewGuid().ToString('N')) }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $taskOutput -Force
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$taskExe = Join-Path $taskOutput 'Pavise.frame-budget-check.exe'
$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$taskSources += Join-Path $taskWorkspace 'tests/SelfTests.FrameCoordination.cs'
$taskSources += Join-Path $taskWorkspace 'tests/FrameCoordinationBudgetRunner.cs'
$taskResponse = Join-Path $taskOutput 'sources.rsp'
[IO.File]::WriteAllLines($taskResponse, [string[]]@($taskSources | ForEach-Object { '"' + $_ + '"' }), [Text.UTF8Encoding]::new($true))

$taskProtected = @($taskSources) + @($PSCommandPath)
foreach ($taskCandidate in @((Join-Path $taskWorkspace 'Pavise.exe'), (Join-Path $taskWorkspace 'build/Pavise.exe'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Pavise.exe'))) {
    if (Test-Path -LiteralPath $taskCandidate -PathType Leaf) { $taskProtected += $taskCandidate }
}
$taskProtected = @($taskProtected | Sort-Object -Unique)
function Get-TaskProtectedHashes {
    $taskRows = foreach ($taskFile in $taskProtected) {
        [pscustomobject]@{ Path = $taskFile; SHA256 = (Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash }
    }
    return @($taskRows)
}
$taskBefore = @(Get-TaskProtectedHashes)
$taskBefore | Export-Csv -LiteralPath (Join-Path $taskOutput 'protected-before.csv') -NoTypeInformation -Encoding UTF8
try {
    & $taskCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_FRAME_BUDGET' -main:PaviseApp.FrameCoordinationBudgetRunner ('-out:' + $taskExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        ('@' + $taskResponse) *> (Join-Path $taskOutput 'compile.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Frame budget regression build failed; inspect compile.txt.' }
    $taskStart = New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName = $taskExe
    $taskStart.Arguments = '"' + $taskOutput + '"'
    $taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true; $taskStart.RedirectStandardError = $true
    $taskProcess = New-Object Diagnostics.Process
    $taskProcess.StartInfo = $taskStart
    try {
        $null = $taskProcess.Start()
        $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync(); $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit(47000)) { $taskProcess.Kill(); throw 'Isolated frame budget regression timed out.' }
        $taskProcess.WaitForExit()
        $taskResult = $taskStdout.Result + $taskStderr.Result
        [IO.File]::WriteAllText((Join-Path $taskOutput 'result.txt'), $taskResult)
        Write-Output $taskResult
        if ($taskProcess.ExitCode -ne 0) { throw 'Frame budget regression failed; inspect result.txt.' }
    } finally { $taskProcess.Dispose() }
} finally {
    $taskAfter = @(Get-TaskProtectedHashes)
    $taskAfter | Export-Csv -LiteralPath (Join-Path $taskOutput 'protected-after.csv') -NoTypeInformation -Encoding UTF8
    $taskHashDifference = @(Compare-Object $taskBefore $taskAfter -Property Path, SHA256)
    $taskHashDifference | Export-Csv -LiteralPath (Join-Path $taskOutput 'protected-difference.csv') -NoTypeInformation -Encoding UTF8
    Write-Output ('OUTPUT ' + $taskOutput)
    if ($taskHashDifference.Count -ne 0) { throw 'Protected sources or application binaries changed during the regression.' }
    Write-Output 'PASS protected source/application SHA256 unchanged'
}
