param()
$ErrorActionPreference = 'Stop'
$restoreWorkspace = Split-Path $PSScriptRoot -Parent
$restoreOutput = Join-Path $env:TEMP ('Pavise-AutoIgnoreRegression-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $restoreOutput
Push-Location $restoreWorkspace
try {
    $restoreCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $restoreExe = Join-Path $restoreOutput 'Pavise.auto-ignore-check.exe'
    $restoreInputs = @(Get-ChildItem -LiteralPath (Join-Path $restoreWorkspace 'src') -Recurse -File -Filter '*.cs')
    $restoreInputs += Get-Item -LiteralPath (Join-Path $restoreWorkspace 'tests/AutoIgnoreRegressionRunner.cs')
    $restoreBefore = @($restoreInputs | ForEach-Object { [pscustomobject]@{Path=$_.FullName;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    & $restoreCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_AUTO_IGNORE_REGRESSION' `
        -main:PaviseApp.AutoIgnoreRegressionRunner ('-out:' + $restoreExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\AutoIgnoreRegressionRunner.cs
    if ($LASTEXITCODE -ne 0) { throw 'Auto-ignore regression compilation failed.' }
    $restoreStart = New-Object Diagnostics.ProcessStartInfo
    $restoreStart.FileName = $restoreExe
    $restoreStart.Arguments = '"' + $restoreOutput + '"'
    $restoreStart.WorkingDirectory = $restoreOutput
    $restoreStart.UseShellExecute = $false
    $restoreStart.CreateNoWindow = $true
    $restoreStart.RedirectStandardOutput = $true
    $restoreStart.RedirectStandardError = $true
    $restoreProcess = New-Object Diagnostics.Process
    $restoreProcess.StartInfo = $restoreStart
    try {
        $null = $restoreProcess.Start()
        $restoreStdout = $restoreProcess.StandardOutput.ReadToEndAsync()
        $restoreStderr = $restoreProcess.StandardError.ReadToEndAsync()
        if (-not $restoreProcess.WaitForExit(50000)) { $restoreProcess.Kill(); throw 'Auto-ignore runner timed out.' }
        $restoreProcess.WaitForExit()
        $restoreResult = $restoreStdout.Result + $restoreStderr.Result
        [IO.File]::WriteAllText((Join-Path $restoreOutput 'result.txt'), $restoreResult)
        Write-Output $restoreResult
        $restoreChanged = @($restoreBefore | Where-Object {-not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256})
        [pscustomobject]@{SourceUnchanged=$restoreChanged.Count -eq 0;ProtectedInputs=$restoreBefore.Count;ChangedInputs=$restoreChanged;
            RuntimeStarted=$false;NativeSystemRestore=$false;OutputDirectory=$restoreOutput;ExitCode=$restoreProcess.ExitCode} |
            ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $restoreOutput 'verification.json') -Encoding utf8
        Write-Output ('OUTPUT ' + $restoreOutput)
        if ($restoreChanged.Count -ne 0) { throw 'Inputs changed; rerun after sources are frozen.' }
        if ($restoreProcess.ExitCode -ne 0) { throw 'Auto-ignore regression failed; inspect result.txt.' }
    } finally {$restoreProcess.Dispose()}
} finally {Pop-Location}
