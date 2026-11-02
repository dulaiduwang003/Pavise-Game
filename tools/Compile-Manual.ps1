param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$Defines='PAVISE_STABILITY_COMPARISON',
    [string]$Target='winexe',
    [string]$Main='PaviseApp.Program',
    [string[]]$Tests=@(),
    [switch]$ProductionProfile
)
$ErrorActionPreference='Stop'
$solo=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
$manualBuildRoot=[IO.Path]::GetFullPath((Join-Path $solo 'build'))+[IO.Path]::DirectorySeparatorChar
if(-not $output.StartsWith($manualBuildRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must stay within the repository build directory'}
if(Test-Path -LiteralPath $output){throw 'Use a new output directory'}
$null=New-Item -ItemType Directory -Path $output,(Join-Path $output 'sources'),(Join-Path $output 'temp')
$env:TEMP=Join-Path $output 'temp';$env:TMP=$env:TEMP
$files=@(Get-ChildItem -LiteralPath (Join-Path $solo 'src') -Recurse -File -Filter '*.cs'|Sort-Object FullName|ForEach-Object {$_.FullName})
foreach($test in $Tests){$files += Join-Path $solo ('tests/'+$test)}
$rows=@();$index=0
foreach($file in $files){
 $hash=(Get-FileHash -LiteralPath $file).Hash
 $copy=Join-Path $output ('sources/'+$index.ToString('D4')+'-'+[IO.Path]::GetFileName($file));$index++
 [IO.File]::WriteAllBytes($copy,[IO.File]::ReadAllBytes($file))
 if((Get-FileHash -LiteralPath $copy).Hash -ne $hash){throw 'Source changed while freezing'}
 $rows += [pscustomobject]@{Source=$file;Frozen=$copy;Hash=$hash}
}
$rows|Export-Csv -LiteralPath (Join-Path $output 'sources.csv') -NoTypeInformation
$rsp=Join-Path $output 'compile.rsp'
[IO.File]::WriteAllLines($rsp,[string[]]@($rows|ForEach-Object {'"'+$_.Frozen+'"'}),(New-Object Text.UTF8Encoding($true)))
$extension=if($Target -eq 'library'){'.dll'}else{'.exe'}
$exe=Join-Path $output ('candidate'+$extension)
$profileArguments=''
if($ProductionProfile){
 if($Target -ne 'winexe' -or $Main -ne 'PaviseApp.Program' -or $Tests.Count -or $Defines -ne 'PAVISE_STABILITY_COMPARISON'){throw 'Production profile requires the product entry and definitions'}
 $exe=Join-Path $output 'Pavise.exe'
 $iconSource=Join-Path $solo 'build/Pavise.ico'
 $icon=Join-Path $output 'Pavise.ico';[IO.File]::Copy($iconSource,$icon)
 $manifest=Join-Path $output 'Pavise.manifest'
 $manifestXml=@'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
 <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="requireAdministrator" uiAccess="false"/></requestedPrivileges></security></trustInfo>
 <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings><dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware><dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness></windowsSettings></application>
 <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application></compatibility>
</assembly>
'@
 [IO.File]::WriteAllText($manifest,$manifestXml,(New-Object Text.UTF8Encoding($false)))
 $profileArguments=' "-win32icon:'+$icon+'" "-win32manifest:'+$manifest+'"'
}
$mainArg=if($Target -eq 'library'){''}else{'-main:'+$Main+' '}
$info=New-Object Diagnostics.ProcessStartInfo
$info.FileName=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$info.Arguments='-nologo -target:'+$Target+' '+$mainArg+'-unsafe+ -optimize+ -platform:x64 -codepage:65001 -define:'+$Defines+' -reference:System.dll -reference:System.Core.dll -reference:System.Drawing.dll -reference:System.Windows.Forms.dll -reference:System.Management.dll -reference:System.Xml.dll -reference:System.Security.dll "-out:'+$exe+'" "@'+$rsp+'"'
$info.Arguments+=$profileArguments
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($info)
try{
 $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
 if(-not $process.WaitForExit(240000)){$process.Kill();$process.WaitForExit();throw 'Compiler timed out'}
 $process.WaitForExit();$exit=$process.ExitCode;$log=$stdout.Result+$stderr.Result
}finally{$process.Dispose()}
[IO.File]::WriteAllText((Join-Path $output 'compile.log'),$log,(New-Object Text.UTF8Encoding($false)))
$errors=@($log -split [Environment]::NewLine|Where-Object {$_ -match ' error CS'})
$warnings=@($log -split [Environment]::NewLine|Where-Object {$_ -match ' warning CS'})
$drift=@($rows|Where-Object {(Get-FileHash -LiteralPath $_.Source).Hash -ne $_.Hash})
[pscustomobject]@{Exit=$exit;Inputs=$rows.Count;Errors=$errors.Count;Warnings=$warnings.Count;Drift=$drift.Count;ProductExecuted=$false;Artifact=$exe}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
if($exit -ne 0 -or $drift.Count){throw ($errors -join [Environment]::NewLine)}
if($ProductionProfile){
 $workspace=$solo
 foreach($name in @('Pavise.exe.config','LICENSE','NOTICE')){[IO.File]::Copy((Join-Path $workspace $name),(Join-Path $output $name))}
}
Get-Content -LiteralPath (Join-Path $output 'verification.json') -Raw
