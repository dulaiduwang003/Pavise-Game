param(
    [string]$BenchPath = (Join-Path $env:TEMP 'Pavise-GameBench-bin/BenchGame.exe'),
    [string]$OutputDirectory = (Join-Path $env:TEMP ('Pavise-GameBench-validation-' + [Guid]::NewGuid().ToString('N'))),
    [switch]$ConfirmedUserNotified,
    [switch]$Hardware,
    [switch]$AllScenes,
    [ValidateSet('none','cpu','io','gpu')][string]$BackgroundLoad = 'none'
)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmedUserNotified) { throw 'Notify the user before any test, then pass -ConfirmedUserNotified. Building does not run tests.' }
$BenchPath=(Resolve-Path -LiteralPath $BenchPath).ProviderPath
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).ProviderPath
function Read-TaskTsv([string]$Path) {
    $taskValues=@{}
    foreach ($taskLine in [IO.File]::ReadAllLines($Path)) {
        $taskPair=$taskLine.Split([char]9,2)
        if ($taskPair.Count -eq 2) {
            if ($taskValues.ContainsKey($taskPair[0])) { throw "Duplicate manifest key: $($taskPair[0])" }
            $taskValues[$taskPair[0]]=$taskPair[1]
        }
    }
    return $taskValues
}
function Invoke-TaskRun([string]$Name,[string[]]$Options) {
    $taskNonce=[Guid]::NewGuid().ToString('N')
    $taskRunDir=Join-Path $OutputDirectory $Name
    $taskSeconds=if ($Options -contains 'switch') { 4.5 } else { 0.5 }
    $taskArgs=@('--output',$taskRunDir,'--nonce',$taskNonce,'--hidden','--width','320','--height','240','--seconds',$taskSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))+$Options
    $taskInfo=[Diagnostics.ProcessStartInfo]::new()
    $taskInfo.FileName=$BenchPath;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.WindowStyle='Hidden'
    $taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
    $taskInfo.Arguments=($taskArgs | ForEach-Object { '"'+$_+'"' }) -join ' '
    $taskProcess=[Diagnostics.Process]::new();$taskProcess.StartInfo=$taskInfo
    if (-not $taskProcess.Start()) { throw "Unable to start $Name" }
    $taskStdout=$taskProcess.StandardOutput.ReadToEndAsync();$taskStderr=$taskProcess.StandardError.ReadToEndAsync()
    if (-not $taskProcess.WaitForExit(90000)) { $taskProcess.Kill();throw "$Name exceeded 90 seconds" }
    $taskOut=$taskStdout.GetAwaiter().GetResult();$taskErr=$taskStderr.GetAwaiter().GetResult()
    if ($taskProcess.ExitCode -ne 0) { throw "$Name failed ($($taskProcess.ExitCode)): $taskErr $taskOut" }
    $taskManifest=Read-TaskTsv (Join-Path $taskRunDir 'manifest.tsv')
    if ($taskManifest.schema -ne '1' -or $taskManifest.nonce -ne $taskNonce -or $taskManifest.completed -ne '1' -or $taskManifest.foreground_valid -ne '0') { throw "$Name has invalid manifest" }
    $taskCsvPath=Join-Path $taskRunDir 'frames.csv'
    if ((Get-FileHash -LiteralPath $taskCsvPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskManifest.frames_sha256) { throw "$Name CSV integrity failed" }
    $taskGpuSamples=0;$taskMarkerCount=0;$taskFocusCount=0;$taskRows=@()
    if ($Options -notcontains '--self-test') {
        if ([IO.File]::ReadLines($taskCsvPath).GetEnumerator().MoveNext() -eq $false) { throw "$Name missing CSV header" }
        if ((Get-Content -LiteralPath $taskCsvPath -TotalCount 1) -ne 'sequence,qpc,frame_ms,cpu_ms,gpu_ms,latency_ms,valid,phase') { throw "$Name has unexpected CSV schema" }
        if ([double]$taskManifest.measurement_seconds -lt $taskSeconds -or [Math]::Abs([double]$taskManifest.measurement_seconds-[double]$taskManifest.foreground_seconds-[double]$taskManifest.controlled_background_seconds) -gt 0.02) { throw "$Name invalid phase duration accounting" }
        if ((Get-FileHash -LiteralPath (Join-Path $taskRunDir 'input-events.csv') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskManifest.input_events_sha256) { throw "$Name input event integrity failed" }
        $taskRows=@(Import-Csv -LiteralPath $taskCsvPath)
        if ($taskRows.Count -lt 2) { throw "$Name did not render enough frames" }
        if ([long]$taskManifest.frame_count -ne $taskRows.Count -or [long]$taskManifest.measured_frame_count -ne ($taskRows.Count-1) -or $taskManifest.first_frame_role -ne 'warmup_boundary_not_statistics') { throw "$Name invalid frame boundary accounting" }
        $taskFrequency=[double]$taskManifest.qpc_frequency
        $taskBegin=[long]$taskManifest.measurement_start_qpc;$taskEnd=[long]$taskManifest.measurement_end_qpc
        $taskPrevious=[long]$taskManifest.frame_interval_start_qpc
        if ($taskFrequency -le 0 -or $taskBegin -le $taskPrevious -or $taskEnd -le $taskBegin -or [Math]::Abs(($taskEnd-$taskBegin)/$taskFrequency-[double]$taskManifest.measurement_seconds) -gt 0.00001) { throw "$Name invalid QPC measurement window" }
        $taskFocusPath=Join-Path $taskRunDir 'focus-events.csv'
        if ((Get-FileHash -LiteralPath $taskFocusPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskManifest.focus_events_sha256 -or (Get-Content -LiteralPath $taskFocusPath -TotalCount 1) -ne 'kind,sequence,qpc,target') { throw "$Name focus event integrity/schema failed" }
        $taskFocus=@(Import-Csv -LiteralPath $taskFocusPath);$taskFocusCount=$taskFocus.Count
        if ($taskFocusCount -lt 2 -or $taskFocus[0].kind -ne 'initial' -or [long]$taskFocus[0].qpc -ne $taskBegin -or [long]$taskFocus[0].sequence -ne 0 -or $taskFocus[0].target -ne 'renderer' -or $taskFocus[-1].kind -ne 'end' -or [long]$taskFocus[-1].qpc -ne $taskEnd -or [long]$taskFocus[-1].sequence -ne $taskRows.Count) { throw "$Name invalid focus boundaries" }
        $taskForeground=0.0;$taskBackground=0.0
        for ($taskFocusIndex=0;$taskFocusIndex -lt $taskFocusCount-1;$taskFocusIndex++) {
            $taskFocusItem=$taskFocus[$taskFocusIndex];$taskFocusNext=$taskFocus[$taskFocusIndex+1]
            if ($taskFocusItem.target -notin @('renderer','helper') -or [long]$taskFocusNext.qpc -le [long]$taskFocusItem.qpc -or [long]$taskFocusNext.sequence -le [long]$taskFocusItem.sequence) { throw "$Name nonmonotonic focus proof" }
            if ($taskFocusIndex -gt 0 -and ($taskFocusItem.kind -ne 'transition' -or $taskFocusItem.target -eq $taskFocus[$taskFocusIndex-1].target)) { throw "$Name invalid focus transition" }
            $taskPhaseSeconds=([long]$taskFocusNext.qpc-[long]$taskFocusItem.qpc)/$taskFrequency
            if ($taskFocusItem.target -eq 'helper') { $taskBackground+=$taskPhaseSeconds } else { $taskForeground+=$taskPhaseSeconds }
        }
        if ([Math]::Abs($taskForeground-[double]$taskManifest.foreground_seconds) -gt 0.00001 -or [Math]::Abs($taskBackground-[double]$taskManifest.controlled_background_seconds) -gt 0.00001 -or $taskFocus[-1].target -ne $taskFocus[-2].target) { throw "$Name focus duration proof mismatch" }
        if ($Options -contains 'switch') { if ($taskFocusCount -lt 4 -or $taskBackground -lt 1.9) { throw "$Name did not exercise both planned switch boundaries" } } elseif ($taskFocusCount -ne 2 -or $taskBackground -ne 0) { throw "$Name unexpected background focus" }
        $taskFocusIndex=0
        for ($taskIndex=0;$taskIndex -lt $taskRows.Count;$taskIndex++) {
            $taskRow=$taskRows[$taskIndex]
            if ([long]$taskRow.sequence -ne $taskIndex -or $taskRow.valid -ne '0') { throw "$Name has invalid sequence/visibility flags" }
            if ([double]$taskRow.frame_ms -le 0 -or ($taskIndex -gt 0 -and [long]$taskRow.qpc -le [long]$taskRows[$taskIndex-1].qpc)) { throw "$Name nonmonotonic frame timing" }
            if ($taskRow.phase -notin @('foreground','controlled_background')) { throw "$Name invalid frame phase" }
            $taskQpc=[long]$taskRow.qpc
            if ($taskQpc -lt $taskBegin -or $taskQpc -gt $taskEnd -or [Math]::Abs(($taskQpc-$taskPrevious)*1000/$taskFrequency-[double]$taskRow.frame_ms) -gt 0.001) { throw "$Name inconsistent frame QPC interval" }
            $taskPrevious=$taskQpc
            while ($taskFocusIndex -lt $taskFocusCount-2 -and [long]$taskFocus[$taskFocusIndex+1].sequence -le $taskIndex) { $taskFocusIndex++ }
            $taskExpectedPhase=if ($taskFocus[$taskFocusIndex].target -eq 'helper') { 'controlled_background' } else { 'foreground' }
            if ($taskRow.phase -ne $taskExpectedPhase -or $taskQpc -lt [long]$taskFocus[$taskFocusIndex].qpc -or $taskQpc -gt [long]$taskFocus[$taskFocusIndex+1].qpc) { throw "$Name frame/focus association failed" }
            if ($taskRow.gpu_ms -ne '') { if ([double]$taskRow.gpu_ms -lt 0) { throw "$Name negative GPU time" };$taskGpuSamples++ }
            if ($taskRow.latency_ms -ne '' -and [double]$taskRow.latency_ms -lt 0) { throw "$Name negative software marker time" }
        }
        if ($taskGpuSamples -lt 1) { throw "$Name produced no GPU timestamp samples" }
        if ($taskManifest.latency_source -ne 'marker_to_present_return_not_display_latency' -or $taskManifest.hidden -ne '1' -or $taskManifest.device_lost -ne '0' -or ($Options -contains '--warp' -and $taskManifest.software -ne '1') -or ($Options -notcontains '--warp' -and $taskManifest.software -ne '0')) { throw "$Name invalid measurement source or adapter flags" }
        $taskInputs=@(Import-Csv -LiteralPath (Join-Path $taskRunDir 'input-events.csv'));$taskMarkerCount=$taskInputs.Count;$taskLastTag=0L;$taskMarkerFrames=@{}
        foreach ($taskInput in $taskInputs) {
            $taskSequence=[long]$taskInput.sequence;$taskPosted=[long]$taskInput.posted_qpc;$taskReceived=[long]$taskInput.received_qpc;$taskSampled=[long]$taskInput.simulation_sample_qpc;$taskPresented=[long]$taskInput.present_return_qpc
            if ($taskSequence -lt 0 -or $taskSequence -ge $taskRows.Count -or [long]$taskInput.tag -le $taskLastTag -or $taskMarkerFrames.ContainsKey($taskSequence) -or $taskPosted -lt $taskBegin -or $taskReceived -lt $taskPosted -or $taskSampled -lt $taskReceived -or $taskPresented -lt $taskSampled) { throw "$Name invalid marker identity/timeline" }
            $taskMarkerFrames[$taskSequence]=$true;$taskLastTag=[long]$taskInput.tag;$taskMarkedRow=$taskRows[$taskSequence]
            $taskActualLatency=($taskPresented-$taskPosted)*1000/$taskFrequency
            $taskTarget=if ($taskMarkedRow.phase -eq 'controlled_background') { 'renderer_controlled_background_marker' } else { 'renderer_material_marker' }
            if ([long]$taskMarkedRow.qpc -lt $taskReceived -or [long]$taskMarkedRow.qpc -gt $taskSampled -or $taskMarkedRow.latency_ms -eq '' -or $taskInput.target -ne $taskTarget -or [Math]::Abs($taskActualLatency-[double]$taskMarkedRow.latency_ms) -gt 0.001 -or [Math]::Abs($taskActualLatency-[double]$taskInput.marker_to_present_return_ms) -gt 0.001) { throw "$Name marker/frame association failed" }
        }
        if ($taskMarkerCount -lt 1 -or @($taskRows | Where-Object { $_.latency_ms -ne '' }).Count -ne $taskMarkerCount) { throw "$Name unmatched software latency evidence" }
        if ($Options -contains 'memory') {
            if ([long]$taskManifest.memory_pretouch_frames -ne ([long]$taskManifest.vram_allocated_texture_bytes/262144) -or $taskManifest.material_cycle_start -ne '0' -or $taskManifest.memory_preparation -ne 'fixed_complete_ring_upload_sample_drain_then_cursor_reset') { throw "$Name inconsistent fixed memory preparation" }
        }
        $taskReady=Read-TaskTsv (Join-Path $taskRunDir 'ready.tsv')
        if ($taskReady.nonce -ne $taskNonce -or $taskReady.pid -ne $taskManifest.pid -or $taskReady.birth -ne $taskManifest.birth -or $taskReady.path -ne $BenchPath) { throw "$Name invalid main ready identity" }
        foreach ($taskRole in @('cpu','io','gpu','ui')) {
            if ([long]$taskReady[($taskRole+'_pid')] -le 0 -or [long]$taskReady[($taskRole+'_birth')] -le 0 -or $taskReady[($taskRole+'_path')] -ne $BenchPath) { throw "$Name missing child identity: $taskRole" }
            $taskChildPath=Join-Path $taskRunDir ('child-result-'+$taskRole+'.tsv')
            if ((Get-FileHash -LiteralPath $taskChildPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskManifest[($taskRole+'_result_sha256')]) { throw "$Name child SHA binding failed: $taskRole" }
            $taskChildResult=Read-TaskTsv $taskChildPath
            if ($taskChildResult.nonce -ne $taskNonce -or $taskChildResult.role -ne $taskRole -or $taskChildResult.completed -ne '1' -or [double]$taskChildResult.elapsed_seconds -le 0) { throw "$Name invalid child result: $taskRole" }
            if ($taskChildResult.pid -ne $taskReady[($taskRole+'_pid')] -or $taskChildResult.birth -ne $taskReady[($taskRole+'_birth')] -or $taskChildResult.path -ne $BenchPath -or $taskChildResult.parent_pid -ne $taskManifest.pid -or $taskReady[($taskRole+'_parent_pid')] -ne $taskManifest.pid) { throw "$Name child identity binding failed: $taskRole" }
            if ($taskChildResult.active -eq '0' -and $taskChildResult.operations -ne '0') { throw "$Name inactive child reported work: $taskRole" }
            if ($taskChildResult.active -eq '1' -and [long]$taskChildResult.operations -le 0) { throw "$Name active child made no progress: $taskRole" }
            if ($taskRole -eq 'ui' -and ($taskChildResult.hwnd -ne $taskReady.ui_hwnd -or [long]$taskChildResult.samples -lt 1 -or [long]$taskChildResult.progress_count -lt 1 -or [double]$taskChildResult.p95_ms -lt 0 -or [long]$taskChildResult.foreground_samples -ne 0)) { throw "$Name invalid hidden UI helper evidence" }
            $taskOwnedProcess=Get-Process -Id ([int]$taskChildResult.pid) -ErrorAction SilentlyContinue
            if ($taskOwnedProcess -and $taskOwnedProcess.StartTime.ToUniversalTime().ToFileTimeUtc() -eq [long]$taskChildResult.birth) { throw "$Name left an owned child alive: $taskRole" }
        }
        if ([long]$taskReady.ui_hwnd -le 0) { throw "$Name missing UI helper HWND" }
    }
    $taskProcess.Dispose()
    return [ordered]@{name=$Name;status='pass';run=$taskRunDir;foreground_valid=0;hardware_certificate=$false;adapter_luid=$taskManifest.adapter_luid;adapter_name=$taskManifest.adapter_name;frames=$taskRows.Count;gpu_samples=$taskGpuSamples;markers=$taskMarkerCount;focus_events=$taskFocusCount}
}
$taskResults=@()
$taskResults += Invoke-TaskRun 'self-test' @('--self-test')
$taskScenes=if ($AllScenes) {@('main','workers','gpu','mixed','stream','memory','startup','switch','wake','thermal')} else {@('mixed')}
$taskChosenAdapter=$null
foreach ($taskApi in @('dx11','dx12')) {
    foreach ($taskScene in $taskScenes) {
        $taskRunOptions=@('--api',$taskApi,'--scene',$taskScene,'--seed','1977','--background-load',$BackgroundLoad)
        if (-not $Hardware) { $taskRunOptions += '--warp' }
        $taskResult=Invoke-TaskRun ($taskApi+'-'+$taskScene) $taskRunOptions
        if ($Hardware) {
            if ($taskResult.adapter_luid -notmatch '^[0-9a-f]{16}$') { throw 'Hardware validation lacks an actual adapter LUID' }
            if ($null -eq $taskChosenAdapter) { $taskChosenAdapter=$taskResult.adapter_luid }
            elseif ($taskChosenAdapter -ne $taskResult.adapter_luid) { throw 'DX11/DX12/scenes selected different physical adapters' }
        }
        $taskResults += $taskResult
    }
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'validation.json'),($taskResults | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$taskResults | ForEach-Object { Write-Output ($_.name+': '+$_.status) }
Write-Output $OutputDirectory
