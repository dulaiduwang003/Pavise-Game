$ErrorActionPreference = 'Stop'
$cardWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cardOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseLibraryCardMenu-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $cardOutput
$cardSources = @(Get-ChildItem -LiteralPath (Join-Path $cardWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$cardSources += Join-Path $cardWorkspace 'tests/LibraryCardMenuChecks.cs'
$cardBefore = foreach ($cardSource in @($cardSources) + @($PSCommandPath)) {
    [pscustomobject]@{ Path = $cardSource; SHA256 = (Get-FileHash -LiteralPath $cardSource -Algorithm SHA256).Hash }
}
$cardBefore | Export-Csv -LiteralPath (Join-Path $cardOutput 'sources-before.csv') -NoTypeInformation -Encoding UTF8
$cardResponse = Join-Path $cardOutput 'sources.rsp'
[IO.File]::WriteAllLines($cardResponse, [string[]]@($cardSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
$cardCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$cardExe = Join-Path $cardOutput 'LibraryCardMenuChecks.exe'
& $cardCompiler /nologo /nowarn:0649 /target:exe /platform:x64 /unsafe+ /optimize+ /codepage:65001 /langversion:5 `
    '/define:PAVISE_SELFTEST;PAVISE_LIBRARY_CARD_MENU' /main:PaviseApp.LibraryCardMenuChecks ('/out:' + $cardExe) `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Management.dll /reference:System.Xml.dll ('@' + $cardResponse) *> (Join-Path $cardOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $cardOutput 'compile.log'); throw ('Card menu compilation failed: ' + $cardOutput) }
$cardStart = New-Object Diagnostics.ProcessStartInfo
$cardStart.FileName = $cardExe
$cardStart.Arguments = '"' + $cardOutput + '"'
$cardStart.WorkingDirectory = $cardOutput
$cardStart.UseShellExecute = $false; $cardStart.CreateNoWindow = $true
$cardStart.RedirectStandardOutput = $true; $cardStart.RedirectStandardError = $true
$cardProcess = New-Object Diagnostics.Process
$cardProcess.StartInfo = $cardStart
try {
    $null = $cardProcess.Start()
    $cardStdout = $cardProcess.StandardOutput.ReadToEndAsync(); $cardStderr = $cardProcess.StandardError.ReadToEndAsync()
    if (-not $cardProcess.WaitForExit(45000)) { $cardProcess.Kill(); throw 'Owned card menu runner timed out.' }
    $cardProcess.WaitForExit()
    $cardResult = $cardStdout.Result + $cardStderr.Result
    [IO.File]::WriteAllText((Join-Path $cardOutput 'result.txt'), $cardResult)
    Write-Output $cardResult
    if ($cardProcess.ExitCode -ne 0 -or $cardStderr.Result.Length -ne 0) { throw ('Card menu verification failed: ' + $cardOutput) }
} finally { $cardProcess.Dispose() }
$cardAfter = foreach ($cardSource in @($cardSources) + @($PSCommandPath)) {
    [pscustomobject]@{ Path = $cardSource; SHA256 = (Get-FileHash -LiteralPath $cardSource -Algorithm SHA256).Hash }
}
$cardAfter | Export-Csv -LiteralPath (Join-Path $cardOutput 'sources-after.csv') -NoTypeInformation -Encoding UTF8
$cardDifference = @(Compare-Object $cardBefore $cardAfter -Property Path, SHA256)
$cardDifference | Export-Csv -LiteralPath (Join-Path $cardOutput 'sources-difference.csv') -NoTypeInformation -Encoding UTF8
if ($cardDifference.Count -ne 0) { throw 'Source files changed during card menu verification.' }
Write-Output ('PASS source SHA256 unchanged; OUTPUT ' + $cardOutput)
