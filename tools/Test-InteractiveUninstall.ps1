param([string]$ResultDir)
$ErrorActionPreference = 'Stop'
$uninstallWorkspace = Split-Path $PSScriptRoot -Parent
$uninstallOutput = if ($ResultDir) { [IO.Path]::GetFullPath($ResultDir) } else { Join-Path $env:TEMP ('Pavise-InteractiveUninstall-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $uninstallOutput) { throw 'A unique fixture output directory is required.' }
$null = New-Item -ItemType Directory -Path $uninstallOutput
Push-Location $uninstallWorkspace
try {
    $uninstallCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $uninstallExe = Join-Path $uninstallOutput 'Pavise.interactive-uninstall-check.exe'
    $uninstallProductionExe = Join-Path $uninstallOutput 'Pavise.production-uninstall-fixture.exe'
    & $uninstallCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_STABILITY_COMPARISON' `
        -main:PaviseApp.Program ('-out:' + $uninstallProductionExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs'
    if ($LASTEXITCODE -ne 0) { throw 'Production finalizer fixture compilation failed.' }
    & $uninstallCompiler -unsafe+ -nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -warn:0 `
        '-define:PAVISE_SELFTEST;PAVISE_INTERACTIVE_UNINSTALL_TEST;PAVISE_STABILITY_COMPARISON' `
        -main:PaviseApp.InteractiveUninstallRunner ('-out:' + $uninstallExe) `
        -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll `
        -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll `
        '-recurse:src\*.cs' tests\InteractiveUninstallRunner.cs
    if ($LASTEXITCODE -ne 0) { throw 'Interactive uninstall test compilation failed.' }
    $uninstallInfo = New-Object Diagnostics.ProcessStartInfo
    $uninstallInfo.FileName = $uninstallExe
    $uninstallInfo.Arguments = '"' + $uninstallOutput + '" "' + $uninstallProductionExe + '"'
    $uninstallInfo.UseShellExecute = $false
    $uninstallInfo.CreateNoWindow = $true
    $uninstallInfo.RedirectStandardOutput = $true
    $uninstallInfo.RedirectStandardError = $true
    $uninstallProcess = New-Object Diagnostics.Process
    $uninstallProcess.StartInfo = $uninstallInfo
    try {
        $null = $uninstallProcess.Start()
        $uninstallStdout = $uninstallProcess.StandardOutput.ReadToEndAsync()
        $uninstallStderr = $uninstallProcess.StandardError.ReadToEndAsync()
        if (-not $uninstallProcess.WaitForExit(75000)) { $uninstallProcess.Kill(); throw 'Fixture runner timed out.' }
        $uninstallProcess.WaitForExit()
        $uninstallResult = $uninstallStdout.Result + $uninstallStderr.Result
        [IO.File]::WriteAllText((Join-Path $uninstallOutput 'result.txt'), $uninstallResult)
        Write-Output $uninstallResult
        [pscustomobject]@{NativeTuning=$false;TaskScheduler=$false;Settings='transient';ExecutableRemoval='disposable fixtures only';
            ProductRuntimeStarted=$false;OutputDirectory=$uninstallOutput;ExitCode=$uninstallProcess.ExitCode} |
            ConvertTo-Json | Out-File -LiteralPath (Join-Path $uninstallOutput 'verification.json') -Encoding utf8
        Write-Output ('OUTPUT ' + $uninstallOutput)
        if ($uninstallProcess.ExitCode -ne 0) { throw 'Interactive uninstall fixtures failed.' }
    } finally { $uninstallProcess.Dispose() }
} finally { Pop-Location }
