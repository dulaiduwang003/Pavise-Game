param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Executable $Executable
    exit $LASTEXITCODE
}
$identityPath = [IO.Path]::GetFullPath($Executable)
$identitySource = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/PaviseApp/App.cs'
$identitySourceText = [IO.File]::ReadAllText($identitySource)
$identityVersionMatches = [regex]::Matches($identitySourceText, '(?m)^\s*public const string Version = "([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)";\s*$')
if ($identityVersionMatches.Count -ne 1) { throw 'The source must declare exactly one canonical application version.' }
$identityExpectedVersion = $identityVersionMatches[0].Groups[1].Value
$identityFile = [Diagnostics.FileVersionInfo]::GetVersionInfo($identityPath)
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve({
    param($sender, $eventArgs)
    [Reflection.Assembly]::ReflectionOnlyLoad($eventArgs.Name)
})
$identityAssembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($identityPath)
$identityFlags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static'
if ($identityFile.FileVersion -ne $identityExpectedVersion -or $identityFile.ProductVersion -ne $identityExpectedVersion -or
    $identityAssembly.GetName().Version.ToString() -ne $identityExpectedVersion) {
    throw ('Expected application, file, informational and assembly version ' + $identityExpectedVersion + '.')
}
foreach ($identityType in @(
    'GamingHandheldDetection',
    'OverviewTelemetryReader',
    'OverviewMemoryCleanupControl',
    'ManualMemoryCleaner',
    'WindowsManualMemoryNative',
    'HardThreadGuardianLaunchPolicy',
    'GameConfigurationDialog',
    'WeGameGameExtension',
    'WeGameShellService',
    'WeGameCardPanel',
    'LaboratorySession',
    'LaboratoryPower',
    'LaboratoryGpu',
    'LaboratoryAudio',
    'LaboratoryEppEligibility',
    'LaboratoryManualActions',
    'LaboratoryFileFilter',
    'FileFilterNative',
    'LaboratoryClockRepair',
    'ClockRepairNative',
    'LaboratoryAudioEffects',
    'AudioEffectsNative',
    'AntiCheatAffinityLease',
    'AntiCheatAffinityTopology',
    'AntiCheatAffinityRuntime',
    'AntiCheatAffinityEligibility',
    'AntiCheatGroupMigration',
    'CriticalThreadStabilityPolicy',
    'GameAffinityJobNative',
    'GameAffinityJobLease',
    'GameAffinityJobGuard',
    'BackgroundGovernancePolicy',
    'StreamingToolCatalog',
    'AiTuningPrompt',
    'AiTuningPromptDialog',
    'AiTuningHardwareReport',
    'AiTuningFeatureCatalog',
    'SecurityProductSnapshot',
    'SecurityStartupProbe',
    'SecurityCompatibilityPresentation',
    'SecurityCompatibilityWarningIcon',
    'CpuMitigationPolicy',
    'QuantumRuntime',
    'QuantumLiveUpdate',
    'GpuInventoryDiscovery',
    'SelfUpdateManifest',
    'SelfUpdateDownload',
    'SelfUpdateInstaller',
    'SelfUpdateInstallerWindow',
    'SelfUpdateGate',
    'UpdateDialog',
    'LegacyMonitoringCleanup')) {
    if ($null -eq $identityAssembly.GetType('PaviseApp.' + $identityType)) { throw "Required implementation is missing: $identityType" }
}
foreach ($identityType in @(
    'HandheldDevice',
    'HandheldDeviceMode',
    'HandheldPowerPolicy',
    'HandheldPowerPlan',
    'MonitoringAmdTelemetry',
    'MonitoringGpuRouting',
    'MonitoringWddmSensors',
    'MonitoringConsoleSurface',
    'WeGameShellReadiness',
    'MonitoringFrameWindow',
    'MonitoringFrameStatistics',
    'PaviseNativeMonitor',
    'NativeMonitoringAvailability',
    'NativeMonitoringPackage',
    'NativeMonitoringChannel',
    'NativeSensorProvider',
    'NativeSensorDriver',
    'NativeSensorDriverSystem',
    'NativeVulkanRegistration',
    'MonitoringWindowOverlay',
    'MonitoringPresenterSelection',
    'MonitoringPresenterScope',
    'GameFrameSample',
    'PerformanceMonitoring',
    'MonitoringPreferences',
    'MonitoringHotkey',
    'MonitoringNativeSensors',
    'RtssComponent',
    'RtssOverlayClient',
    'RtssHardwareProvider',
    'RtssStartup',
    'RtssStartupPermissions',
    'RtssProcessElevation',
    'RtssTrayIcon',
    'MonitoringDisplayCache',
    'RtssNativeBenchmark',
    'GameRunReports',
    'GameRunReportStore',
    'GameRunObservation',
    'GameRunReportAnalysis',
    'GameRunReportExport',
    'GameReportTimeline',
    'MonitoringGpuReading',
    'GameRunGpuObservation',
    'GameRunGpuAdapter',
    'GameRunAdapterTrack',
    'BackgroundCpuCap',
    'BackgroundCpuCapEligibility',
    'CpuCapGuard',
    'CpuCapNative',
    'CpuCapRecord',
    'CpuCapRecovery',
    'CpuCapTargetSafety',
    'CpuCapProcessChannel',
    'BackgroundFreeze',
    'BackgroundFreezePolicy',
    'FreezeGuard',
    'FreezeLeaseSet',
    'FreezeNative',
    'FreezeTarget')) {
    if ($null -ne $identityAssembly.GetType('PaviseApp.' + $identityType)) { throw "Retired implementation is present: $identityType" }
}
foreach ($identityResource in $identityAssembly.GetManifestResourceNames()) {
    if ($identityResource -like 'Pavise.Native.*') { throw ('Retired native monitoring payload is embedded: ' + $identityResource) }
    if ($identityResource -match 'PresentMon|PawnIO') { throw ('Retired collector or driver payload is embedded: ' + $identityResource) }
}
foreach ($identityType in $identityAssembly.GetTypes()) {
    if ($identityType.Namespace -eq 'PaviseApp' -and $identityType.Name -match '^(Monitoring|PerformanceMonitoring|Rtss|GameRunReport|GameReport)') {
        throw ('Retired monitoring/report type is compiled: ' + $identityType.FullName)
    }
}
$identityWeGame = $identityAssembly.GetType('PaviseApp.WeGameShellService', $true)
if ($null -ne $identityWeGame.GetMethod('ConfirmReadyAndDetach', $identityFlags)) { throw 'Retired manual WeGame confirmation is present.' }
if ($null -eq $identityAssembly.GetType('PaviseApp.WeGameGameExtension', $true).GetMethod('CanAutoDetachRenderer', $identityFlags)) { throw 'Observed-renderer WeGame gate is missing.' }
$identityPanel = $identityAssembly.GetType('PaviseApp.PanelForm', $true)
if ($null -ne $identityAssembly.GetType('PaviseApp.PageId', $true).GetField('Monitoring', $identityFlags)) { throw 'Retired monitoring page is present.' }
if ($null -ne $identityPanel.GetMethod('BuildGamePerformancePage', $identityFlags)) { throw 'Retired game-performance workspace is present.' }
if ($null -eq $identityAssembly.GetManifestResourceStream('Pavise.LegacyCleanup.RtssUninstall.ps1')) { throw 'Ownership-checked legacy cleanup is missing.' }
$identityInputQueue = $identityAssembly.GetType('PaviseApp.InputMythTweak', $true)
foreach ($identityMethod in @('Repair', 'NeedsRepair', 'Describe', 'MouseQueueSize', 'KeyboardQueueSize', 'IsTampered')) {
    if ($null -ne $identityInputQueue.GetMethod($identityMethod, $identityFlags)) { throw "Retired input queue correction is present: $identityMethod" }
}
foreach ($identityMethod in @('Restore', 'HasResidue')) {
    if ($null -eq $identityInputQueue.GetMethod($identityMethod, $identityFlags)) { throw "Legacy input queue recovery is missing: $identityMethod" }
}
foreach ($identityMethod in @('PrepareWorkspaceLayout', 'HandleWorkspaceMessage', 'LayoutWorkspaceResizeGrips', 'RefreshWorkspaceMaximizedBounds')) {
    if ($null -ne $identityPanel.GetMethod($identityMethod, $identityFlags)) { throw "Retired adaptive window sizing is present: $identityMethod" }
}
foreach ($identityMethod in @('BuildOverviewHud', 'BuildCoreWorkspace', 'LayoutSettingsWorkspace',
    'StartManualMemoryCleanup', 'BuildLaboratoryOptions')) {
    if ($null -eq $identityPanel.GetMethod($identityMethod, $identityFlags)) { throw "Required workspace method is missing: $identityMethod" }
}
$identityVersion = $identityAssembly.GetType('PaviseApp.App', $true).GetField('Version', $identityFlags).GetRawConstantValue()
$identityUpdater = $identityAssembly.GetType('PaviseApp.SelfUpdateInstaller', $true)
foreach ($identityMethod in @('Prepare', 'LaunchHelper', 'CommitSafeExit', 'Abort', 'TryRun', 'AcknowledgeStartup',
    'RecordLaunch', 'IsUncoordinatedStartupBlocked', 'IsStartupUpdatePending', 'GetStartupManualFailure')) {
    if ($null -eq $identityUpdater.GetMethod($identityMethod, $identityFlags)) { throw "In-app update lifecycle is incomplete: $identityMethod" }
}
if ($identityVersion -ne $identityExpectedVersion) { throw 'Application version does not match the source and binary version.' }
$identityPower = $identityAssembly.GetType('PaviseApp.PowerPlan', $true)
foreach ($identityMethod in @('TuneLegacy3110Target', 'Legacy3110ManagedIntact', 'Legacy3110Fingerprint', 'CreateLegacy3110Native', 'SyncLegacy3110DisplayFeel')) {
    if ($null -eq $identityPower.GetMethod($identityMethod, $identityFlags)) { throw "3.1.1.0 managed power comparison implementation is missing: $identityMethod" }
}
if ($null -eq $identityAssembly.GetType('PaviseApp.PowerPlanProfile')) { throw '3.1.1.0 managed power architecture policy is missing.' }
$identityLegacySource = $identityPower.GetField('Legacy3110Source', $identityFlags)
if ($null -eq $identityLegacySource -or $identityLegacySource.GetRawConstantValue() -ne 'legacy-3.1.1.0') {
    throw 'Managed power comparison source identity does not match 3.1.1.0.'
}
foreach ($identityType in @('ManagedPowerPreset', 'ManagedPowerPresetSelection', 'ManagedPowerHardwareFacts', 'IManagedPowerPresetPlans')) {
    if ($null -eq $identityAssembly.GetType('PaviseApp.' + $identityType)) { throw "Managed power implementation is missing: $identityType" }
}
$identityPowerAssets = @(Get-ChildItem -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'src/Assets/PowerPlans') -File -Filter '*.pow')
if ($identityPowerAssets.Count -ne 7) { throw 'Expected exactly seven embedded power-plan assets.' }
foreach ($identityPowerAsset in $identityPowerAssets) {
    $identityPowerResource = $identityAssembly.GetManifestResourceStream('Pavise.PowerPlans.' + $identityPowerAsset.Name)
    if ($null -eq $identityPowerResource) { throw ('Missing original power-plan resource: ' + $identityPowerAsset.Name) }
    $identityResourceHash = [Security.Cryptography.SHA256]::Create()
    try { $identityEmbeddedHash = [BitConverter]::ToString($identityResourceHash.ComputeHash($identityPowerResource)).Replace('-', '') }
    finally { $identityPowerResource.Dispose(); $identityResourceHash.Dispose() }
    $identityAssetHasher = [Security.Cryptography.SHA256]::Create()
    try { $identityAssetHash = [BitConverter]::ToString($identityAssetHasher.ComputeHash([IO.File]::ReadAllBytes($identityPowerAsset.FullName))).Replace('-', '') }
    finally { $identityAssetHasher.Dispose() }
    if ($identityEmbeddedHash -ne $identityAssetHash) {
        throw ('Original power-plan resource differs from its verified source: ' + $identityPowerAsset.Name)
    }
}
$identityStream = [IO.File]::OpenRead($identityPath)
$identityHasher = [Security.Cryptography.SHA256]::Create()
try { $identityHash = [BitConverter]::ToString($identityHasher.ComputeHash($identityStream)).Replace('-', '') }
finally { $identityStream.Dispose(); $identityHasher.Dispose() }
Write-Output ('Identity verified: application ' + $identityExpectedVersion + ' and current workspace implementation; SHA256 ' + $identityHash)
