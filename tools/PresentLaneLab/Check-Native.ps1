param([string]$ZigPath='zig',[string]$OutputDirectory='',[switch]$Elevated)
$ErrorActionPreference='Stop'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-FrameNative-'+[guid]::NewGuid().ToString('N'))}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    if($Elevated){throw 'Native ETW test elevation failed'}
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "{0}" -ZigPath "{1}" -OutputDirectory "{2}" -Elevated' -f $PSCommandPath,$ZigPath,$OutputDirectory
    $child=Start-Process powershell.exe -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    "RESULT_DIRECTORY=$OutputDirectory";"CONTROLLER_PID=$($child.Id)";return
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture=Join-Path $OutputDirectory 'DxgiFixture.exe'
$runner=Join-Path $OutputDirectory 'FrameNative.exe'
try {
    & $ZigPath c++ -target x86_64-windows-gnu -std=c++17 -O2 (Join-Path $PSScriptRoot 'DxgiFixture.cpp') -o $fixture -ld3d11 -ldxgi -ld3d9 -ladvapi32 -luser32 -lole32
    if($LASTEXITCODE -ne 0){throw 'Owned fixture compile failed'}
    $sources=@(Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Recurse -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
    $sources+=Join-Path $repo 'tests\FrameCoordinationNativeRunner.cs'
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc -unsafe+ -nologo -nowarn:0649 -target:exe -platform:x64 -optimize+ -codepage:65001 '-define:PAVISE_SELFTEST;PAVISE_FRAME_NATIVE' -main:PaviseApp.FrameCoordinationNativeRunner "-out:$runner" -reference:System.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Core.dll -reference:System.Management.dll -reference:System.Xml.dll $sources
    if($LASTEXITCODE -ne 0){throw 'Native regression compile failed'}
    $info=[Diagnostics.ProcessStartInfo]::new($runner,('"{0}" "{1}"' -f $fixture,$OutputDirectory))
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $owned=[Diagnostics.Process]::new();$owned.StartInfo=$info;$null=$owned.Start()
    $read=$owned.StandardOutput.ReadToEndAsync();$err=$owned.StandardError.ReadToEndAsync()
    if(-not $owned.WaitForExit(47000)){$owned.Kill();throw 'Owned native test timeout'}
    $read.Result | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stdout.txt')
    $err.Result | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stderr.txt')
    if($owned.ExitCode -ne 0){throw ('Native test failed: '+$err.Result)}
    $owned.Dispose()
    'PASS' | Set-Content -LiteralPath (Join-Path $OutputDirectory 'completed.txt')
} catch {$_ | Out-String | Set-Content -LiteralPath (Join-Path $OutputDirectory 'failed.txt');throw}
