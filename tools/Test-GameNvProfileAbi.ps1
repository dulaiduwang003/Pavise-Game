param([Parameter(Mandatory=$true)][string]$HeaderDirectory,[string]$ZigPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
# Read the already-downloaded, byte-exact MIT NVIDIA headers. Never download SDK/driver binaries or execute a probe.
$nvRef='70d337db9186e968eab622f7e786de7e437faf3d'
$nvExpected=@{
    'nvapi.h'='4d42b0b5ed1221d87bf56d3189b79aa7df0a071d';'NvApiDriverSettings.h'='eac9de10c66587b25fc06822d98f89a69544ae90';
    'nvapi_interface.h'='93c4590ef44fcc7620bf830a1d1533f4ab94d5b6';'nvapi_lite_salstart.h'='c5099206f1964983cface88230041c855910a8d5';
    'nvapi_lite_common.h'='a70d464632c66b62bf7fa0cbbb92d0921f400925';'nvapi_lite_sli.h'='c55643ae20dcc35c5c6817dea561d2edb4a9337f';
    'nvapi_lite_surround.h'='c0686b004a3d878e1f2e6241d8354ac668ddde8f';'nvapi_lite_stereo.h'='7b6e642d07f75da220dbd3cd31532cb518be162e';
    'nvapi_lite_d3dext.h'='615f965f675036a982c13f4ec3ec7e9da43f70a7';'nvapi_lite_salend.h'='0e4de0c3e6939f22007e77061118ff70edc09503'
}
$nvAbiOutput=Join-Path $env:TEMP ('Pavise-GameNvAbi-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $nvAbiOutput
$nvAbiHeaders=Join-Path $nvAbiOutput 'headers';$null=New-Item -ItemType Directory -Path $nvAbiHeaders
$nvManifest=@()
foreach($name in ($nvExpected.Keys|Sort-Object)){
    $source=Join-Path $HeaderDirectory $name;$bytes=[IO.File]::ReadAllBytes($source);$prefix=[Text.Encoding]::ASCII.GetBytes('blob '+$bytes.Length+[char]0)
    $input=New-Object byte[] ($prefix.Length+$bytes.Length);[Array]::Copy($prefix,0,$input,0,$prefix.Length);[Array]::Copy($bytes,0,$input,$prefix.Length,$bytes.Length)
    $sha=[Security.Cryptography.SHA1]::Create();try{$blob=[BitConverter]::ToString($sha.ComputeHash($input)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    if($blob -ne $nvExpected[$name]){throw ('Header is not exact pinned NVIDIA source: '+$name+' got '+$blob)}
    $target=Join-Path $nvAbiHeaders $name;[IO.File]::WriteAllBytes($target,$bytes)
    $nvManifest+=[pscustomobject]@{File=$name;GitBlobSha=$blob;SHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant();Ref=$nvRef}
}
$nvManifest|Export-Csv -LiteralPath (Join-Path $nvAbiOutput 'vendor-source-sha256.tsv') -Delimiter "`t" -NoTypeInformation
$interface=[IO.File]::ReadAllText((Join-Path $nvAbiHeaders 'nvapi_interface.h'))
if($interface -notmatch '"NvAPI_DRS_GetCurrentGlobalProfile",\s*0x617bff9f' -or $interface -notmatch '"NvAPI_DRS_FindApplicationByName",\s*0xeee566b2' -or $interface -notmatch '"NvAPI_DRS_EnumApplications",\s*0x7fa2173a' -or $interface -notmatch '"NvAPI_DRS_GetProfileInfo",\s*0x61cd6fd6' -or $interface -notmatch '"NvAPI_DRS_GetSetting",\s*0x73bf8338'){throw 'Readonly NVAPI function ID contract differs'}
foreach($nvEntry in @('NvAPI_DRS_CreateProfile=cc176068','NvAPI_DRS_CreateApplication=4347a9de','NvAPI_DRS_SetSetting=577dd202','NvAPI_DRS_DeleteProfileSetting=e4a26362','NvAPI_DRS_DeleteApplication=2c694bc6','NvAPI_DRS_DeleteProfile=17093206','NvAPI_DRS_SaveSettings=fcbc7e14','NvAPI_DRS_EnumSettings=ae3039da')){
    $nvParts=$nvEntry.Split('=')
    if($interface -notmatch ('"'+$nvParts[0]+'",\s*0x0?'+$nvParts[1])){throw ('Writable function ID mismatch: '+$nvParts[0])}
}
if(-not $ZigPath){$ZigPath=Join-Path $env:TEMP 'Pavise-Calibration-Zig-0.15.2\zig-x86_64-windows-0.15.2\zig.exe'}
if((Get-FileHash -LiteralPath $ZigPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'd408dd38eed3e5204af841bcebf70502a4dbbb8399a3a3262be55059370bc018'){throw 'Unverified Zig compiler'}
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$cpp=Join-Path $nvAbiOutput 'GameNvProfileAbi.cpp'
[IO.File]::WriteAllBytes($cpp,[IO.File]::ReadAllBytes((Join-Path $workspace 'tests/GameNvProfileAbi.cpp')))
[IO.File]::WriteAllBytes((Join-Path $nvAbiOutput 'Test-GameNvProfileAbi.ps1'),[IO.File]::ReadAllBytes($PSCommandPath))
# The SDK's own __NVAPI_EMPTY_SAL suppresses SAL macro cleanup under MinGW. No structures, types, prototypes or header
# bytes are changed. Without it the nested SDK includes undefine the fallback __success annotation on non-MSVC hosts.
$arguments='c++ -target x86_64-windows-gnu -std=c++17 -O2 -g0 -Werror -D__NVAPI_EMPTY_SAL -I"'+$nvAbiHeaders+'" -c "'+$cpp+'" -o "'+(Join-Path $nvAbiOutput 'nvapi-abi.obj')+'"'
[IO.File]::WriteAllText((Join-Path $nvAbiOutput 'compile-command.txt'),$arguments)
$info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$ZigPath;$info.Arguments=$arguments;$info.WorkingDirectory=$nvAbiOutput
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$process=New-Object Diagnostics.Process;$process.StartInfo=$info
try{
    if(-not $process.Start()){throw 'ABI compiler did not start'};$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(60000)){$process.Kill();throw 'ABI compile timed out'};$process.WaitForExit();$log=$stdout.Result+$stderr.Result
    [IO.File]::WriteAllText((Join-Path $nvAbiOutput 'compile.log'),$log)
    if($process.ExitCode -ne 0){Write-Output $log;throw 'Official NVIDIA ABI assertions did not compile'}
    Write-Output 'PASS byte-exact NVIDIA headers/function IDs; official V4 selectors/layout/enums/prototypes object compile only'
}finally{$process.Dispose();Write-Output ('ARTIFACT='+$nvAbiOutput)}
