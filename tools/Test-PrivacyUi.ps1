$ErrorActionPreference = 'Stop'
$privacyWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$privacyOutput = Join-Path ([IO.Path]::GetTempPath()) ('PavisePrivacyUi-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $privacyOutput
$privacySources = @(Get-ChildItem -LiteralPath (Join-Path $privacyWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$privacySources += Join-Path $privacyWorkspace 'tests/PrivacyUiChecks.cs'
$privacyBefore = foreach ($privacySource in @($privacySources) + @($PSCommandPath)) {
    [pscustomobject]@{ Path = $privacySource; SHA256 = (Get-FileHash -LiteralPath $privacySource -Algorithm SHA256).Hash }
}
$privacyBefore | Export-Csv -LiteralPath (Join-Path $privacyOutput 'sources-before.csv') -NoTypeInformation -Encoding UTF8
$privacyResponse = Join-Path $privacyOutput 'sources.rsp'
[IO.File]::WriteAllLines($privacyResponse, [string[]]@($privacySources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
$privacyCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$privacyExe = Join-Path $privacyOutput 'PrivacyUiChecks.exe'
& $privacyCompiler /nologo /nowarn:0649 /target:exe /platform:x64 /unsafe+ /optimize+ /codepage:65001 /langversion:5 `
    '/define:PAVISE_SELFTEST;PAVISE_PRIVACY_UI' /main:PaviseApp.PrivacyUiChecks ('/out:' + $privacyExe) `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Management.dll /reference:System.Xml.dll ('@' + $privacyResponse) *> (Join-Path $privacyOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $privacyOutput 'compile.log'); throw ('Privacy UI compilation failed: ' + $privacyOutput) }
$privacyStart = New-Object Diagnostics.ProcessStartInfo
$privacyStart.FileName = $privacyExe
$privacyStart.Arguments = '"' + $privacyOutput + '"'
$privacyStart.WorkingDirectory = $privacyOutput
$privacyStart.UseShellExecute = $false; $privacyStart.CreateNoWindow = $true
$privacyStart.RedirectStandardOutput = $true; $privacyStart.RedirectStandardError = $true
$privacyProcess = New-Object Diagnostics.Process
$privacyProcess.StartInfo = $privacyStart
try {
    $null = $privacyProcess.Start()
    $privacyStdout = $privacyProcess.StandardOutput.ReadToEndAsync(); $privacyStderr = $privacyProcess.StandardError.ReadToEndAsync()
    if (-not $privacyProcess.WaitForExit(180000)) {
        $privacyProcess.Kill(); $privacyProcess.WaitForExit()
        [IO.File]::WriteAllText((Join-Path $privacyOutput 'result.txt'), $privacyStdout.Result + $privacyStderr.Result)
        throw ('Privacy UI runner timed out: ' + $privacyOutput)
    }
    $privacyProcess.WaitForExit()
    $privacyResult = $privacyStdout.Result + $privacyStderr.Result
    [IO.File]::WriteAllText((Join-Path $privacyOutput 'result.txt'), $privacyResult)
    Write-Output $privacyResult
    if ($privacyProcess.ExitCode -ne 0 -or $privacyStderr.Result.Length -ne 0) { throw ('Privacy UI verification failed: ' + $privacyOutput) }
} finally { $privacyProcess.Dispose() }
$privacyAfter = foreach ($privacySource in @($privacySources) + @($PSCommandPath)) {
    [pscustomobject]@{ Path = $privacySource; SHA256 = (Get-FileHash -LiteralPath $privacySource -Algorithm SHA256).Hash }
}
$privacyAfter | Export-Csv -LiteralPath (Join-Path $privacyOutput 'sources-after.csv') -NoTypeInformation -Encoding UTF8
$privacyDifference = @(Compare-Object $privacyBefore $privacyAfter -Property Path, SHA256)
$privacyDifference | Export-Csv -LiteralPath (Join-Path $privacyOutput 'sources-difference.csv') -NoTypeInformation -Encoding UTF8
if ($privacyDifference.Count -ne 0) { throw 'Source files changed during privacy UI verification.' }
Write-Output ('PASS source SHA256 unchanged; OUTPUT ' + $privacyOutput)
