param()
$ErrorActionPreference = 'Stop'
$resetWorkspace = Split-Path $PSScriptRoot -Parent
$resetOutput = Join-Path $env:TEMP ('Pavise-AppDataReset-Test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $resetOutput
$resetTarget = Join-Path $resetOutput 'link-target'
$null = New-Item -ItemType Directory -Path $resetTarget
$null = New-Item -ItemType Directory -Path (Join-Path $resetTarget 'data')
[IO.File]::WriteAllText((Join-Path $resetTarget 'Pavise.games.txt'), 'owned junction fixture target')
$null = New-Item -ItemType Junction -Path (Join-Path $resetOutput 'root-junction') -Target $resetTarget
$null = New-Item -ItemType Junction -Path (Join-Path $resetOutput 'ancestor-junction') -Target $resetTarget
Push-Location $resetWorkspace
try {
    $resetCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $resetExe = Join-Path $resetOutput 'Pavise.app-data-reset-check.exe'
    $resetInputs = @(Get-ChildItem -LiteralPath (Join-Path $resetWorkspace 'src') -Recurse -File -Filter '*.cs')
    $resetInputs += Get-Item -LiteralPath (Join-Path $resetWorkspace 'tests/AppDataResetRunner.cs')
    $resetBefore = @($resetInputs | ForEach-Object { [pscustomobject]@{Path=$_.FullName;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    & $resetCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_APP_DATA_RESET_RUNNER' -main:PaviseApp.AppDataResetRunner ('-out:' + $resetExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\AppDataResetRunner.cs
    if ($LASTEXITCODE -ne 0) { throw 'App data reset regression compilation failed.' }
    $resetStart = New-Object Diagnostics.ProcessStartInfo
    $resetStart.FileName = $resetExe
    $resetStart.Arguments = '"' + $resetOutput + '"'
    $resetStart.WorkingDirectory = $resetOutput
    $resetStart.UseShellExecute = $false
    $resetStart.CreateNoWindow = $true
    $resetStart.RedirectStandardOutput = $true
    $resetStart.RedirectStandardError = $true
    $resetProcess = New-Object Diagnostics.Process
    $resetProcess.StartInfo = $resetStart
    try {
        $null = $resetProcess.Start()
        $resetStdout = $resetProcess.StandardOutput.ReadToEndAsync()
        $resetStderr = $resetProcess.StandardError.ReadToEndAsync()
        if (-not $resetProcess.WaitForExit(30000)) { $resetProcess.Kill(); throw 'Owned app reset runner timed out.' }
        $resetProcess.WaitForExit()
        $resetResult = $resetStdout.Result + $resetStderr.Result
        [IO.File]::WriteAllText((Join-Path $resetOutput 'result.txt'), $resetResult)
        Write-Output $resetResult
        $resetChanged = @($resetBefore | Where-Object {-not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256})
        [pscustomobject]@{SourceUnchanged=$resetChanged.Count -eq 0;ProtectedInputs=$resetBefore.Count;ChangedInputs=$resetChanged;
            RuntimeStarted=$false;NativeSystemRestore=$false;Registry='transient';TaskScheduler='not_called';
            OutputDirectory=$resetOutput;ExitCode=$resetProcess.ExitCode} |
            ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $resetOutput 'verification.json') -Encoding utf8
        Write-Output ('OUTPUT ' + $resetOutput)
        if ($resetChanged.Count -ne 0) { throw 'Inputs changed; rerun after sources are frozen.' }
        if ($resetProcess.ExitCode -ne 0) { throw 'App data reset regression failed; inspect result.txt.' }
    } finally {$resetProcess.Dispose()}
} finally {Pop-Location}
