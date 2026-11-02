param()
$ErrorActionPreference = 'Stop'
$proofWorkspace = Split-Path $PSScriptRoot -Parent
$proofOutput = Join-Path $env:TEMP ('Pavise-ShutdownWriterProof-Test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $proofOutput
Push-Location $proofWorkspace
try {
    $proofCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $proofExe = Join-Path $proofOutput 'Pavise.shutdown-writer-proof.exe'
    $proofInputs = @(Get-ChildItem -LiteralPath (Join-Path $proofWorkspace 'src') -Recurse -File -Filter '*.cs')
    $proofInputs += Get-Item -LiteralPath (Join-Path $proofWorkspace 'tests/ShutdownWriterProofRunner.cs')
    $proofBefore = @($proofInputs | ForEach-Object { [pscustomobject]@{Path=$_.FullName;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    & $proofCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_SHUTDOWN_WRITER_PROOF_RUNNER' -main:PaviseApp.ShutdownWriterProofRunner ('-out:' + $proofExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\ShutdownWriterProofRunner.cs
    if ($LASTEXITCODE -ne 0) { throw 'Shutdown writer proof compilation failed.' }
    $proofStart = New-Object Diagnostics.ProcessStartInfo
    $proofStart.FileName = $proofExe
    $proofStart.Arguments = '"' + $proofOutput + '"'
    $proofStart.WorkingDirectory = $proofOutput
    $proofStart.UseShellExecute = $false
    $proofStart.CreateNoWindow = $true
    $proofStart.RedirectStandardOutput = $true
    $proofStart.RedirectStandardError = $true
    $proofProcess = New-Object Diagnostics.Process
    $proofProcess.StartInfo = $proofStart
    try {
        $null = $proofProcess.Start()
        $proofStdout = $proofProcess.StandardOutput.ReadToEndAsync()
        $proofStderr = $proofProcess.StandardError.ReadToEndAsync()
        if (-not $proofProcess.WaitForExit(25000)) { $proofProcess.Kill(); throw 'Owned shutdown writer proof runner timed out.' }
        $proofProcess.WaitForExit()
        $proofResult = $proofStdout.Result + $proofStderr.Result
        [IO.File]::WriteAllText((Join-Path $proofOutput 'result.txt'), $proofResult)
        Write-Output $proofResult
        $proofChanged = @($proofBefore | Where-Object {-not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256})
        [pscustomobject]@{SourceUnchanged=$proofChanged.Count -eq 0;ProtectedInputs=$proofBefore.Count;ChangedInputs=$proofChanged;
            RuntimeStarted=$false;NativeCalls=0;Registry='transient';
            OutputDirectory=$proofOutput;ExitCode=$proofProcess.ExitCode} |
            ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $proofOutput 'verification.json') -Encoding utf8
        Write-Output ('OUTPUT ' + $proofOutput)
        if ($proofChanged.Count -ne 0) { throw 'Inputs changed; rerun after sources are frozen.' }
        if ($proofProcess.ExitCode -ne 0) { throw 'Shutdown writer proof regression failed; inspect result.txt.' }
    } finally {$proofProcess.Dispose()}
} finally {Pop-Location}
