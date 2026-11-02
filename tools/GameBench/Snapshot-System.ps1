# Elevated system snapshot for apply/restore checks. SysSnap.exe covers the registry, services, power, timer and
# process attributes; this script adds power requests, scheme list, processor power indexes, scheduled tasks and QoS.
param([Parameter(Mandatory)][string]$Out, [string]$SysSnapExe = "$env:LOCALAPPDATA\PaviseSysSnap\SysSnap.exe", [string]$ThreadsOf = 'BenchGame')
$ErrorActionPreference = 'Continue'
$raw = "$Out.native.tsv"
& $SysSnapExe $raw $ThreadsOf
$lines = New-Object System.Collections.Generic.List[string]
if (Test-Path $raw) { $lines.AddRange([IO.File]::ReadAllLines($raw)); Remove-Item $raw }
function Add([string]$section, [string]$key, [string]$value) { $lines.Add("$section`t$($key -replace "`t",' ')`t$($value -replace "[`t`r`n]",' ')") }

# Power requests: one line per requester under each category.
$category = ''
foreach ($l in (powercfg /requests)) {
    if ($l -match '^([A-Z]+):$') { $category = $Matches[1]; continue }
    if ($l.Trim().Length -gt 0 -and $l.Trim() -ne 'None.') { Add 'request' ($category + ':' + $l.Trim()) '1' }
}
foreach ($l in (powercfg /list)) { if ($l -match '([0-9a-f-]{36})\s+\((.+?)\)') { Add 'scheme' $Matches[1] $Matches[2] } }
# /list can omit inactive built-in plans; query them directly.
foreach ($g in '381b4222-f694-41f0-9685-ff5bb260df2e', '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c', 'a1841308-3541-4fab-bc81-f71556f20b4a', 'e9a42b02-d5df-448d-aa00-03f14749eb61') {
    powercfg /query $g 2>&1 | Out-Null
    Add 'scheme-query' $g $(if ($LASTEXITCODE -eq 0) { 'exists' } else { 'missing' })
}
$setting = ''
foreach ($l in (powercfg /qh SCHEME_CURRENT SUB_PROCESSOR)) {
    if ($l -match 'Power Setting GUID: ([0-9a-f-]{36})\s+\((.*)\)') { $setting = $Matches[1] + ' ' + $Matches[2]; continue }
    if ($setting -and $l -match 'Current AC Power Setting Index: (0x[0-9a-f]+)') { Add 'cpupower' ($setting + ' AC') $Matches[1] }
    if ($setting -and $l -match 'Current DC Power Setting Index: (0x[0-9a-f]+)') { Add 'cpupower' ($setting + ' DC') $Matches[1] }
}
foreach ($l in (bcdedit /enum '{current}')) {
    if ($l -match '^(useplatformtick|disabledynamictick|useplatformclock|hypervisorlaunchtype|tscsyncpolicy)\s+(\S+)') { Add 'bcd' $Matches[1] $Matches[2] }
}
try { foreach ($t in Get-ScheduledTask -ErrorAction Stop) { Add 'task' ($t.TaskPath + $t.TaskName) ([string]$t.State) } } catch { Add 'task' '<error>' $_.Exception.Message }
try { foreach ($q in Get-NetQosPolicy -ErrorAction Stop) { Add 'qos' $q.Name ("dscp=" + $q.DSCPAction + " app=" + $q.AppPathNameMatchCondition + " store=" + $q.PolicyStore) } } catch { }
[IO.File]::WriteAllLines($Out, $lines, (New-Object Text.UTF8Encoding($false)))
