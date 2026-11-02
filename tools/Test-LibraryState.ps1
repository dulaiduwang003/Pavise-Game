param()
$ErrorActionPreference = 'Stop'
$libraryWorkspace = Split-Path $PSScriptRoot -Parent
$libraryOutput = Join-Path $env:TEMP ('Pavise-LibraryStateRegression-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $libraryOutput
Push-Location $libraryWorkspace
try {
    $libraryCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $libraryExe = Join-Path $libraryOutput 'Pavise.library-state-check.exe'
    $libraryInputs = @(Get-ChildItem -LiteralPath (Join-Path $libraryWorkspace 'src') -Recurse -File -Filter '*.cs')
    $libraryInputs += Get-Item -LiteralPath (Join-Path $libraryWorkspace 'tests/LibraryStateRegressionRunner.cs')
    $libraryInputs += Get-Item -LiteralPath (Join-Path $libraryWorkspace 'tests/SelfTests.UiAsyncState.cs')
    $libraryBefore = @($libraryInputs | ForEach-Object { [pscustomobject]@{Path=$_.FullName;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    & $libraryCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_LIBRARY_STATE_REGRESSION' `
        -main:PaviseApp.LibraryStateRegressionRunner ('-out:' + $libraryExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\LibraryStateRegressionRunner.cs tests\SelfTests.UiAsyncState.cs
    if ($LASTEXITCODE -ne 0) { throw 'Library state regression compilation failed.' }
    $libraryStart = New-Object Diagnostics.ProcessStartInfo
    $libraryStart.FileName = $libraryExe
    $libraryStart.WorkingDirectory = $libraryOutput
    $libraryStart.UseShellExecute = $false
    $libraryStart.CreateNoWindow = $true
    $libraryStart.RedirectStandardOutput = $true
    $libraryStart.RedirectStandardError = $true
    $libraryProcess = New-Object Diagnostics.Process
    $libraryProcess.StartInfo = $libraryStart
    try {
        $null = $libraryProcess.Start()
        $libraryStdout = $libraryProcess.StandardOutput.ReadToEndAsync()
        $libraryStderr = $libraryProcess.StandardError.ReadToEndAsync()
        if (-not $libraryProcess.WaitForExit(35000)) { $libraryProcess.Kill(); throw 'Owned library state runner timed out.' }
        $libraryProcess.WaitForExit()
        $libraryResult = $libraryStdout.Result + $libraryStderr.Result
        [IO.File]::WriteAllText((Join-Path $libraryOutput 'result.txt'), $libraryResult)
        Write-Output $libraryResult
        $libraryChanged = @($libraryBefore | Where-Object {-not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256})
        [pscustomobject]@{SourceUnchanged=$libraryChanged.Count -eq 0;ProtectedInputs=$libraryBefore.Count;ChangedInputs=$libraryChanged;
            Settings='transient';RuntimeStarted=$false;NativeHandlesCreated=0;Queries='mocked';OutputDirectory=$libraryOutput;ExitCode=$libraryProcess.ExitCode} |
            ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $libraryOutput 'verification.json') -Encoding utf8
        Write-Output ('OUTPUT ' + $libraryOutput)
        if ($libraryChanged.Count -ne 0) { throw 'Inputs changed; rerun after sources are frozen.' }
        if ($libraryProcess.ExitCode -ne 0) { throw 'Library state regression failed; inspect result.txt.' }
    } finally {$libraryProcess.Dispose()}
} finally {Pop-Location}
