$ErrorActionPreference = 'Stop'
$libWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$libOutput = Join-Path ([IO.Path]::GetTempPath()) ('PaviseComponentLibrary-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $libOutput
$libSources = @(Get-ChildItem -LiteralPath (Join-Path $libWorkspace 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$libSources += Join-Path $libWorkspace 'tests/ComponentLibraryChecks.cs'
$libSources += Join-Path $libWorkspace 'tests/QuantumComponentChecks.cs'
$libSources += Join-Path $libWorkspace 'tests/ComponentLibraryRegressionRunner.cs'
$libBefore = foreach ($libSource in @($libSources) + @($PSCommandPath)) {
    [pscustomobject]@{ Path = $libSource; SHA256 = (Get-FileHash -LiteralPath $libSource -Algorithm SHA256).Hash }
}
$libBefore | Export-Csv -LiteralPath (Join-Path $libOutput 'sources-before.csv') -NoTypeInformation -Encoding UTF8
$libResponse = Join-Path $libOutput 'sources.rsp'
[IO.File]::WriteAllLines($libResponse, [string[]]@($libSources | ForEach-Object { '"' + $_ + '"' }), (New-Object Text.UTF8Encoding($true)))
$libCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$libExe = Join-Path $libOutput 'ComponentLibraryChecks.exe'
& $libCompiler /nologo /nowarn:0649 /target:exe /platform:x64 /unsafe+ /optimize+ /codepage:65001 /langversion:5 `
    '/define:PAVISE_SELFTEST;PAVISE_COMPONENT_LIBRARY' /main:PaviseApp.ComponentLibraryRegressionRunner ('/out:' + $libExe) `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /reference:System.Management.dll /reference:System.Xml.dll ('@' + $libResponse) *> (Join-Path $libOutput 'compile.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $libOutput 'compile.log'); throw ('Component library compilation failed: ' + $libOutput) }
$libStart = New-Object Diagnostics.ProcessStartInfo
$libStart.FileName = $libExe
$libStart.Arguments = '"' + $libOutput + '"'
$libStart.WorkingDirectory = $libOutput
$libStart.UseShellExecute = $false; $libStart.CreateNoWindow = $true
$libStart.RedirectStandardOutput = $true; $libStart.RedirectStandardError = $true
$libProcess = New-Object Diagnostics.Process
$libProcess.StartInfo = $libStart
try {
    $null = $libProcess.Start()
    $libStdout = $libProcess.StandardOutput.ReadToEndAsync(); $libStderr = $libProcess.StandardError.ReadToEndAsync()
    if (-not $libProcess.WaitForExit(180000)) {
        $libProcess.Kill(); $libProcess.WaitForExit();
        $libTimeoutResult = $libStdout.Result + $libStderr.Result;
        [IO.File]::WriteAllText((Join-Path $libOutput 'result.txt'), $libTimeoutResult);
        Write-Output $libTimeoutResult;
        throw ('Component library runner timed out: ' + $libOutput)
    }
    $libProcess.WaitForExit()
    $libResult = $libStdout.Result + $libStderr.Result
    [IO.File]::WriteAllText((Join-Path $libOutput 'result.txt'), $libResult)
    Write-Output $libResult
    if ($libProcess.ExitCode -ne 0 -or $libStderr.Result.Length -ne 0) { throw ('Component library verification failed: ' + $libOutput) }
} finally { $libProcess.Dispose() }
$libAfter = foreach ($libSource in @($libSources) + @($PSCommandPath)) {
    [pscustomobject]@{ Path = $libSource; SHA256 = (Get-FileHash -LiteralPath $libSource -Algorithm SHA256).Hash }
}
$libAfter | Export-Csv -LiteralPath (Join-Path $libOutput 'sources-after.csv') -NoTypeInformation -Encoding UTF8
$libDifference = @(Compare-Object $libBefore $libAfter -Property Path, SHA256)
$libDifference | Export-Csv -LiteralPath (Join-Path $libOutput 'sources-difference.csv') -NoTypeInformation -Encoding UTF8
if ($libDifference.Count -ne 0) { throw 'Source files changed during component library verification.' }
Write-Output ('PASS source SHA256 unchanged; OUTPUT ' + $libOutput)
