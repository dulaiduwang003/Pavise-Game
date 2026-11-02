param([ValidateSet('Manual','Bootstrap','Legacy')][string]$Suite='Manual',[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-SpiMutations-'+[Guid]::NewGuid().ToString('N'))}
$spiMutationOutput=[IO.Path]::GetFullPath($OutputDirectory)
$spiMutationOptions=@{OutputDirectory=$spiMutationOutput}
if($Suite -ne 'Manual'){$spiMutationOptions[$Suite]=$true}
& (Join-Path $PSScriptRoot 'Test-ManualSpiOwnership.ps1') @spiMutationOptions
$spiMutationConfig=Get-Content -LiteralPath (Join-Path $spiMutationOutput 'compile-config.json') -Raw|ConvertFrom-Json
if($spiMutationConfig.Entry -notin @('PaviseApp.ManualSpiOwnershipChecks','PaviseApp.SpiBootstrapChecks','PaviseApp.LegacySpiMigrationChecks') -or !$spiMutationConfig.Defines.Contains('PAVISE_SELFTEST')){throw 'Only explicit isolated SPI test entrypoints may run'}
$spiMutationCases=@()
if($Suite -eq 'Manual'){
    $spiMutationCases=@(
        @{Name='drop-original-before-close';File='Core/Tweaks/PerformanceActionStore.VisualOwnership.cs';Old='return Save(key, Pack(terminal)); // Keep originals through owner-slot close publication.';New='return Clear(key, Pack(receipt)); // Mutation: drop original before owner publication.';Expected='FAIL: First restored receipt retains originals while another field is active'},
        @{Name='skip-original-close-readback';File='Core/Tweaks/SpiOwnerEvidence.Manual.cs';Old='if (PerformanceActionBackend.Read(receipt.Id, receipt.Target) != receipt.Original)';New='if (false)';Expected='FAIL: Owner closure rechecks original readback after durable terminal publication'},
        @{Name='ignore-closed-manifest-hash';File='Core/Tweaks/SpiOwnership.cs';Old='if (bytes == null || SpiOwnerRecord.Digest(bytes) != record.ClosureDigest)';New='if (bytes == null)';Expected='FAIL: Even a removed already-cleared manifest member changes the bound closure proof'}
    )
}elseif($Suite -eq 'Bootstrap'){
    $spiMutationCases=@(
        @{Name='drop-version2-initialization';File='Core/Tweaks/SpiOwnerFileStore.cs';Old='if (lines.Count == 8 && lines[0] == "pavise-spi-initialization-2")';New='if (false)';Expected='FAIL: Version 2 initialization is still a valid existing owner'},
        @{Name='skip-main-after-sentinel';File='Core/Tweaks/SharedSpiBootstrap.cs';Old='string digest = store.PublishInitialization(proof.InitializationBytes()); proof.RequireCurrent();';New='string digest = store.PublishInitialization(proof.InitializationBytes());';Expected='FAIL: Revoked original Main fact has no ownership publication'}
    )
}elseif($Suite -eq 'Legacy'){
    $spiMutationCases=@(
        @{Name='unknown-legacy-value-overwritten';File='Core/Tweaks/SharedSpiLegacyMigration.cs';Old='if (value != row.Expected || !PerformanceActionBackend.Write';New='if (!PerformanceActionBackend.Write';Expected='FAIL: Unknown current value cannot be guessed as Original or Expected'},
        @{Name='drop-legacy-original-before-initialization';File='Core/Tweaks/SharedSpiLegacyMigration.cs';Old='return Save(Slot(row.Id, row.Target), Pack(new Receipt { Id = row.Id, Target = row.Target, Original = row.Original, Expected = row.Original, Committed = true, VisualRestored = true }));';New='return Clear(Slot(row.Id, row.Target), Pack(row));';Expected='Registered legacy SPI original is missing before closure'},
        @{Name='skip-final-legacy-readback';File='Core/Tweaks/SharedSpiLegacyMigration.cs';Old='if (!row.VisualRestored || PerformanceActionBackend.Read(row.Id, row.Target) != row.Original)';New='if (false)';Expected='FAIL: Legacy initialization rechecks actual originals after terminal commit'}
    )
}
$spiMutationRows=New-Object 'Collections.Generic.List[string]';$spiMutationRows.Add("case`tcompile_exit`trun_exit`tnamed_failure")
foreach($case in $spiMutationCases){
    $target=if($case.Extra){Join-Path $spiMutationOutput $case.Extra}else{Join-Path $spiMutationConfig.SourceRoot $case.File}
    $bytes=[IO.File]::ReadAllBytes($target);$original=[IO.File]::ReadAllText($target);$expectedCount=if($case.ContainsKey('Count')){$case['Count']}else{1}
    if([regex]::Matches($original,[regex]::Escape($case.Old)).Count -ne $expectedCount){throw ('Mutation anchor count differs '+$case.Name)}
    try{
        [IO.File]::WriteAllText($target,$original.Replace($case.Old,$case.New),[Text.UTF8Encoding]::new($false))
        $compileArguments=@($spiMutationConfig.Arguments|ForEach-Object{[string]$_})
        & $spiMutationConfig.Compiler @compileArguments *> (Join-Path $spiMutationOutput ($case.Name+'.compile.log'))
        if($LASTEXITCODE -ne 0){throw ('Compile failure is not a semantic mutation detection: '+$case.Name)}
        $data=Join-Path $env:TEMP ('PM-'+[Guid]::NewGuid().ToString('N').Substring(0,8));[IO.File]::WriteAllText((Join-Path $spiMutationOutput ($case.Name+'.data-root.txt')),$data)
        $start=[Diagnostics.ProcessStartInfo]::new([string]$spiMutationConfig.Executable,('"'+$data+'"'));$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $process=[Diagnostics.Process]::new();$process.StartInfo=$start
        try{
            $null=$process.Start();$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();if(!$process.WaitForExit(20000)){$process.Kill();throw 'Mutation timeout is not semantic detection'};$process.WaitForExit();$result=$stdout.Result+$stderr.Result
            [IO.File]::WriteAllText((Join-Path $spiMutationOutput ($case.Name+'.result.txt')),$result)
            if($process.ExitCode -ne 1 -or !$result.Contains($case.Expected)){throw ('Mutant failed to hit its named semantic check: '+$case.Name+' '+$result)}
            $spiMutationRows.Add($case.Name+"`t0`t1`t"+$case.Expected);Write-Output ('DETECTED '+$case.Name)
        }finally{$process.Dispose()}
    }finally{[IO.File]::WriteAllBytes($target,$bytes)}
}
[IO.File]::WriteAllLines((Join-Path $spiMutationOutput 'mutations.tsv'),$spiMutationRows)
$spiMutationChanged=@(Import-Csv -LiteralPath (Join-Path $spiMutationOutput 'sources.csv')|Where-Object{(Get-FileHash -LiteralPath $_.Frozen).Hash -ne $_.SHA256})
if($spiMutationChanged.Count -ne 0){throw 'Frozen source was not restored after mutations'}
Write-Output ('PASS '+$Suite+' baseline + '+$spiMutationCases.Count+' compiled semantic mutants; sources restored; '+$spiMutationOutput)
