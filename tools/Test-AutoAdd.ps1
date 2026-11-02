param([switch]$NativeGpuSmoke)
& (Join-Path $PSScriptRoot 'Test-AutoAddRollback.ps1') -NativeGpuSmoke:$NativeGpuSmoke
