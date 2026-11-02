param([switch]$Mutations)
$ErrorActionPreference='Stop'
$bootstrapWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bootstrapOutput=Join-Path $env:TEMP ('Pavise-MainInstanceBootstrap-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $bootstrapOutput
$bootstrapModule=Join-Path $bootstrapOutput 'Program.SingleInstanceBootstrap.cs'
Copy-Item -LiteralPath (Join-Path $bootstrapWorkspace 'src/Program.SingleInstanceBootstrap.cs') -Destination $bootstrapModule
$bootstrapTest=Join-Path $bootstrapOutput 'MainInstanceBootstrapChecks.cs'
Copy-Item -LiteralPath (Join-Path $bootstrapWorkspace 'tests/MainInstanceBootstrapChecks.cs') -Destination $bootstrapTest
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $bootstrapOutput 'driver.ps1')
$bootstrapSources=@(Get-ChildItem -LiteralPath $bootstrapOutput -File | ForEach-Object{[pscustomobject]@{Path=$_.Name;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$bootstrapSources|Export-Csv -LiteralPath (Join-Path $bootstrapOutput 'sources.csv') -NoTypeInformation
$bootstrapCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
function Invoke-BootstrapHidden([string]$File,[string]$Arguments){
    $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$bootstrapOutput
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process;$process.StartInfo=$info
    try {
        if(-not $process.Start()){throw 'Own bootstrap checker did not start'}
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(15000)){$process.Kill();throw 'Own bootstrap checker timed out'}
        $process.WaitForExit();return [pscustomobject]@{Exit=$process.ExitCode;Text=$stdout.Result+$stderr.Result}
    }finally{$process.Dispose()}
}
try {
    $bootstrapExe=Join-Path $bootstrapOutput 'Checks.exe'
    $bootstrapArguments='-nologo -target:exe -platform:x64 -optimize+ -codepage:65001 -define:PAVISE_MAIN_INSTANCE_BOOTSTRAP_CHECKS -main:PaviseApp.MainInstanceBootstrapChecks "-out:'+$bootstrapExe+'" "'+$bootstrapModule+'" "'+$bootstrapTest+'"'
    $compile=Invoke-BootstrapHidden $bootstrapCompiler $bootstrapArguments
    [IO.File]::WriteAllText((Join-Path $bootstrapOutput 'compile.log'),$compile.Text)
    if($compile.Exit -ne 0){throw $compile.Text}
    $result=Invoke-BootstrapHidden $bootstrapExe ''
    [IO.File]::WriteAllText((Join-Path $bootstrapOutput 'result.log'),$result.Text)
    Write-Output $result.Text
    if($result.Exit -ne 0){throw 'Main-instance isolated checks failed'}
    if($Mutations){
        $original=[IO.File]::ReadAllBytes($bootstrapModule);$source=[IO.File]::ReadAllText($bootstrapModule)
        $cases=@(
            @{Name='thread';Old='!object.ReferenceEquals(ownerThread, Thread.CurrentThread)';New='false';Expected="A worker cannot borrow Main's mutex ownership"},
            @{Name='lifetime';Old='Volatile.Read(ref active) != 1';New='false';Expected='A saved reference cannot outlive explicit revocation'},
            @{Name='single-use';Old='Interlocked.CompareExchange(ref claimed, 1, 0) != 0';New='false';Expected='A second bootstrap attempt cannot consume the original capability'},
            @{Name='wrapper';Old='!object.ReferenceEquals(originalMutex.SafeWaitHandle, originalHandle)';New='false';Expected='Even the same numeric handle in a substituted wrapper is not the original capability'}
        )
        $rows=New-Object 'Collections.Generic.List[string]';$rows.Add("case`tcompile_exit`trun_exit`tassertion")
        try {
            foreach($case in $cases){
                if([regex]::Matches($source,[regex]::Escape($case.Old)).Count -ne 1){throw ('Mutation anchor not exact: '+$case.Name)}
                [IO.File]::WriteAllText($bootstrapModule,$source.Replace($case.Old,$case.New),(New-Object Text.UTF8Encoding($false)))
                $built=Invoke-BootstrapHidden $bootstrapCompiler $bootstrapArguments
                [IO.File]::WriteAllText((Join-Path $bootstrapOutput ($case.Name+'.compile.log')),$built.Text)
                if($built.Exit -ne 0){throw ('Compile failure cannot kill mutation '+$case.Name)}
                $mutant=Invoke-BootstrapHidden $bootstrapExe ''
                [IO.File]::WriteAllText((Join-Path $bootstrapOutput ($case.Name+'.result.log')),$mutant.Text)
                if($mutant.Exit -ne 1 -or -not $mutant.Text.Contains('FAIL: '+$case.Expected)){throw ('Mutation did not hit its named assertion: '+$case.Name)}
                $rows.Add($case.Name+"`t0`t1`t"+$case.Expected)
            }
        }finally{[IO.File]::WriteAllBytes($bootstrapModule,$original)}
        [IO.File]::WriteAllLines((Join-Path $bootstrapOutput 'mutations.tsv'),$rows);Write-Output ($rows -join [Environment]::NewLine)
    }
}finally{Write-Output ('OUTPUT '+$bootstrapOutput)}
