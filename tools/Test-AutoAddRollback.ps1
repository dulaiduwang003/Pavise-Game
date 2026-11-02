param([switch]$NativeGpuSmoke)
$ErrorActionPreference = 'Stop'
$autoAddWorkspace = Split-Path $PSScriptRoot -Parent
$autoAddOutput = Join-Path $env:TEMP ('Pavise-AutoAddRollback-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $autoAddOutput
Push-Location $autoAddWorkspace
try {
    $autoAddCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $autoAddExe = Join-Path $autoAddOutput 'Pavise.autoadd-check.exe'
    $autoAddProtected = @(Get-ChildItem -LiteralPath (Join-Path $autoAddWorkspace 'src') -Recurse -File -Filter '*.cs')
    $autoAddProtected += Get-Item -LiteralPath (Join-Path $autoAddWorkspace 'tests/AutoAddRollbackRunner.cs')
    $autoAddBefore = @($autoAddProtected | ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    & $autoAddCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_AUTOADD_ROLLBACK' `
        -main:PaviseApp.AutoAddRollbackRunner ('-out:' + $autoAddExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\AutoAddRollbackRunner.cs
    if ($LASTEXITCODE -ne 0) { throw 'Automatic library regression build failed.' }
    $autoAddStart = New-Object Diagnostics.ProcessStartInfo
    $autoAddStart.FileName = $autoAddExe
    $autoAddStart.Arguments = '"' + $autoAddOutput + '"'
    if ($NativeGpuSmoke) { $autoAddStart.Arguments += ' --native-gpu' }
    $autoAddStart.WorkingDirectory = $autoAddOutput
    $autoAddStart.UseShellExecute = $false
    $autoAddStart.CreateNoWindow = $true
    $autoAddStart.RedirectStandardOutput = $true
    $autoAddStart.RedirectStandardError = $true
    $autoAddProcess = New-Object Diagnostics.Process
    $autoAddProcess.StartInfo = $autoAddStart
    try {
        $null = $autoAddProcess.Start()
        $autoAddStdout = $autoAddProcess.StandardOutput.ReadToEndAsync()
        $autoAddStderr = $autoAddProcess.StandardError.ReadToEndAsync()
        if (-not $autoAddProcess.WaitForExit(50000)) { $autoAddProcess.Kill(); throw 'Isolated automatic library regression timed out.' }
        $autoAddProcess.WaitForExit()
        $autoAddResult = $autoAddStdout.Result + $autoAddStderr.Result
        [IO.File]::WriteAllText((Join-Path $autoAddOutput 'result.txt'), $autoAddResult)
        Write-Output $autoAddResult
        $autoAddChanged = @($autoAddBefore | Where-Object { -not (Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Sha256 })
        [pscustomobject]@{ SourceUnchanged = $autoAddChanged.Count -eq 0; ProtectedInputs = $autoAddBefore.Count; ChangedInputs = $autoAddChanged;
            RuntimeStarted = $false; SystemTuningCalled = $false; GpuSamplingCalled = $NativeGpuSmoke.IsPresent; OutputDirectory = $autoAddOutput; ExitCode = $autoAddProcess.ExitCode } |
            ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $autoAddOutput 'verification.json') -Encoding utf8
        Write-Output ('OUTPUT ' + $autoAddOutput)
        if ($autoAddChanged.Count -ne 0) { throw 'Production/test source changed during the run; rerun after sources are frozen.' }
        if ($autoAddProcess.ExitCode -ne 0) { throw 'Automatic library regression failed; inspect result.txt.' }
    } finally { $autoAddProcess.Dispose() }
} finally { Pop-Location }
