param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$updateUiWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('Pavise-UpdateUi-' + [Guid]::NewGuid().ToString('N')) }
$updateUiOutput = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $updateUiOutput -Force
$updateUiExe = Join-Path $updateUiOutput 'UpdateUiChecks.exe'
$updateUiCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$updateUiArguments = '-nologo -target:exe -platform:x64 -codepage:65001 -define:SELF_UPDATE_UI_FIXTURE -main:PaviseApp.SelfUpdateUiChecks -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll "-out:' + $updateUiExe + '" "' + (Join-Path $updateUiWorkspace 'src/PaviseApp/UpdateDialog.cs') + '" "' + (Join-Path $updateUiWorkspace 'src/PaviseApp/SelfUpdateGate.cs') + '" "' + (Join-Path $updateUiWorkspace 'tests/SelfUpdateUiChecks.cs') + '"'
function Invoke-UpdateUiHidden([string]$Executable, [string]$Arguments) {
    $updateUiInfo = New-Object Diagnostics.ProcessStartInfo
    $updateUiInfo.FileName = $Executable; $updateUiInfo.Arguments = $Arguments
    $updateUiInfo.UseShellExecute = $false; $updateUiInfo.CreateNoWindow = $true
    $updateUiInfo.RedirectStandardOutput = $true; $updateUiInfo.RedirectStandardError = $true
    $updateUiProcess = New-Object Diagnostics.Process; $updateUiProcess.StartInfo = $updateUiInfo
    try {
        if (-not $updateUiProcess.Start()) { throw 'Could not start own update UI checker' }
        $updateUiOut = $updateUiProcess.StandardOutput.ReadToEndAsync(); $updateUiErr = $updateUiProcess.StandardError.ReadToEndAsync()
        if (-not $updateUiProcess.WaitForExit(30000)) { $updateUiProcess.Kill(); throw 'Own isolated update UI checker timed out' }
        $updateUiProcess.WaitForExit()
        return [pscustomobject]@{ Exit = $updateUiProcess.ExitCode; Text = $updateUiOut.Result + $updateUiErr.Result }
    } finally { $updateUiProcess.Dispose() }
}
$updateUiCompile = Invoke-UpdateUiHidden $updateUiCompiler $updateUiArguments
[IO.File]::WriteAllText((Join-Path $updateUiOutput 'compile.log'), $updateUiCompile.Text)
if ($updateUiCompile.Exit -ne 0) { throw $updateUiCompile.Text }
$updateUiRun = Invoke-UpdateUiHidden $updateUiExe ''
[IO.File]::WriteAllText((Join-Path $updateUiOutput 'result.log'), $updateUiRun.Text)
Write-Output $updateUiRun.Text
Write-Output ('OUTPUT ' + $updateUiOutput)
if ($updateUiRun.Exit -ne 0) { throw 'Isolated update UI checks failed' }
