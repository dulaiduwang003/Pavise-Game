param([string]$OutputDirectory,[switch]$Bootstrap,[switch]$Legacy)
$ErrorActionPreference='Stop'
if(([int][bool]$Bootstrap+[int][bool]$Legacy) -gt 1){throw 'Select one manual SPI suite'}
$manualSpiWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-ManualSpi-'+[Guid]::NewGuid().ToString('N'))}
$manualSpiOutput=[IO.Path]::GetFullPath($OutputDirectory);$null=New-Item -ItemType Directory -Path $manualSpiOutput -Force
$manualSpiSnapshot=Join-Path $manualSpiOutput 'src';Copy-Item -LiteralPath (Join-Path $manualSpiWorkspace 'src') -Destination $manualSpiSnapshot -Recurse
$manualSpiTestName=if($Bootstrap){'SpiBootstrapChecks.cs'}elseif($Legacy){'LegacySpiMigrationChecks.cs'}else{'ManualSpiOwnershipChecks.cs'}
$manualSpiEntry=if($Bootstrap){'PaviseApp.SpiBootstrapChecks'}elseif($Legacy){'PaviseApp.LegacySpiMigrationChecks'}else{'PaviseApp.ManualSpiOwnershipChecks'}
$manualSpiDefines=if($Bootstrap){'PAVISE_SELFTEST;PAVISE_SPI_BOOTSTRAP_CHECKS;PAVISE_MAIN_INSTANCE_BOOTSTRAP_CHECKS'}elseif($Legacy){'PAVISE_SELFTEST;PAVISE_LEGACY_SPI_CHECKS;PAVISE_MAIN_INSTANCE_BOOTSTRAP_CHECKS'}else{'PAVISE_SELFTEST;PAVISE_MANUAL_SPI_CHECKS'}
$manualSpiTest=Join-Path $manualSpiOutput $manualSpiTestName;Copy-Item -LiteralPath (Join-Path $manualSpiWorkspace ('tests/'+$manualSpiTestName)) -Destination $manualSpiTest
$manualSpiExtraSources=@()
$manualSpiInputFiles=@(Get-ChildItem -LiteralPath $manualSpiSnapshot -Recurse -File)+@(Get-Item -LiteralPath $manualSpiTest)+@($manualSpiExtraSources|ForEach-Object{Get-Item -LiteralPath $_})
$manualSpiInputFiles|ForEach-Object{[pscustomobject]@{Frozen=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}}|Export-Csv -LiteralPath (Join-Path $manualSpiOutput 'sources.csv') -NoTypeInformation
$manualSpiCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe';$manualSpiExe=Join-Path $manualSpiOutput 'ManualSpiChecks.exe'
$manualSpiArguments=@('-nologo','-target:exe','-platform:x64','-unsafe+','-optimize+','-codepage:65001',('-define:'+$manualSpiDefines),('-main:'+$manualSpiEntry),('-out:'+$manualSpiExe),'-reference:System.dll','-reference:System.Core.dll','-reference:System.Drawing.dll','-reference:System.Windows.Forms.dll','-reference:System.Management.dll','-reference:System.Xml.dll',('-recurse:'+(Join-Path $manualSpiSnapshot '*.cs')),$manualSpiTest)+$manualSpiExtraSources
[ordered]@{Compiler=$manualSpiCompiler;Arguments=$manualSpiArguments;Entry=$manualSpiEntry;Defines=$manualSpiDefines;Executable=$manualSpiExe;SourceRoot=$manualSpiSnapshot}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $manualSpiOutput 'compile-config.json') -Encoding UTF8
& $manualSpiCompiler @manualSpiArguments *> (Join-Path $manualSpiOutput 'compile.log')
if($LASTEXITCODE -ne 0){throw ('Manual SPI isolated compile failed '+$manualSpiOutput)}
$manualSpiData=Join-Path $env:TEMP ('PS-'+[Guid]::NewGuid().ToString('N').Substring(0,8));[IO.File]::WriteAllText((Join-Path $manualSpiOutput 'data-root.txt'),$manualSpiData)
$manualSpiStart=[Diagnostics.ProcessStartInfo]::new($manualSpiExe,('"'+$manualSpiData+'"'));$manualSpiStart.UseShellExecute=$false;$manualSpiStart.CreateNoWindow=$true;$manualSpiStart.WindowStyle='Hidden';$manualSpiStart.RedirectStandardOutput=$true;$manualSpiStart.RedirectStandardError=$true
$manualSpiProcess=[Diagnostics.Process]::new();$manualSpiProcess.StartInfo=$manualSpiStart
try{$null=$manualSpiProcess.Start();$manualSpiOut=$manualSpiProcess.StandardOutput.ReadToEndAsync();$manualSpiErr=$manualSpiProcess.StandardError.ReadToEndAsync();if(!$manualSpiProcess.WaitForExit(20000)){$manualSpiProcess.Kill();throw 'Manual SPI isolated timeout'};$manualSpiProcess.WaitForExit();$manualSpiText=$manualSpiOut.Result+$manualSpiErr.Result;[IO.File]::WriteAllText((Join-Path $manualSpiOutput 'result.txt'),$manualSpiText);Write-Output $manualSpiText;if($manualSpiProcess.ExitCode -ne 0){throw 'Manual SPI ownership checks failed'}}finally{$manualSpiProcess.Dispose();Write-Output ('OUTPUT '+$manualSpiOutput)}
