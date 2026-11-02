param([string]$OutputDirectory='build\candidate-lab-20261005\edge-results')
$ErrorActionPreference='Stop'
$taskEdgeOutput=[IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
New-Item -ItemType Directory -Path $taskEdgeOutput -Force | Out-Null
$taskEdgePolicy='Software\Policies\Microsoft\Edge'
$taskEdgeNames=@('SleepingTabsEnabled','SleepingTabsTimeout')
$taskEdgeSnapshot=@()
$taskEdgeKey=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskEdgePolicy)
$taskEdgeKeyExisted=$null-ne$taskEdgeKey
try{
 foreach($taskEdgeName in $taskEdgeNames){
   $taskEdgeExisted=$taskEdgeKey-and($taskEdgeKey.GetValueNames()-contains$taskEdgeName)
   $taskEdgeSnapshot+=@{Name=$taskEdgeName;Exists=[bool]$taskEdgeExisted;Value=$(if($taskEdgeExisted){$taskEdgeKey.GetValue($taskEdgeName)}else{$null});Kind=$(if($taskEdgeExisted){[string]$taskEdgeKey.GetValueKind($taskEdgeName)}else{$null})}
 }
}finally{if($taskEdgeKey){$taskEdgeKey.Dispose()}}
if(@($taskEdgeSnapshot|Where-Object Exists).Count){throw 'Existing user sleeping-tab policy: do not overwrite in this experiment'}
foreach($taskEdgeHive in @([Microsoft.Win32.Registry]::LocalMachine)){
 $taskEdgeMachine=$taskEdgeHive.OpenSubKey($taskEdgePolicy)
 try{if($taskEdgeMachine-and@($taskEdgeMachine.GetValueNames()|Where-Object{$taskEdgeNames-contains$_}).Count){throw 'Existing machine policy takes precedence'}}finally{if($taskEdgeMachine){$taskEdgeMachine.Dispose()}}
}
$taskEdgeSnapshot|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $taskEdgeOutput 'original-policy.json') -Encoding UTF8
$taskEdgeApplied=@{}
try{
 $taskEdgeOrder=@('baseline','candidate','candidate','baseline')
 for($taskEdgeIndex=0;$taskEdgeIndex-lt$taskEdgeOrder.Count;$taskEdgeIndex++){
   $taskEdgeArm=$taskEdgeOrder[$taskEdgeIndex]
   $taskEdgeKey=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($taskEdgePolicy)
   try{
     $taskEdgeApplied.SleepingTabsEnabled=if($taskEdgeArm-eq'candidate'){1}else{0}
     $taskEdgeApplied.SleepingTabsTimeout=30
     foreach($taskEdgeName in $taskEdgeNames){$taskEdgeKey.SetValue($taskEdgeName,$taskEdgeApplied[$taskEdgeName],[Microsoft.Win32.RegistryValueKind]::DWord)}
   }finally{$taskEdgeKey.Dispose()}
   $taskEdgeRun=Join-Path $taskEdgeOutput "$taskEdgeIndex-$taskEdgeArm"
   & node (Join-Path $PSScriptRoot 'EdgeSleep.mjs') $taskEdgeRun $taskEdgeArm
   if($LASTEXITCODE-ne0){throw "Owned Edge $taskEdgeArm experiment failed"}
 }
}finally{
 $taskEdgeKey=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskEdgePolicy,$true)
 try{
   if($taskEdgeKey){foreach($taskEdgeOriginal in $taskEdgeSnapshot){
     $taskEdgeName=$taskEdgeOriginal.Name
     if($taskEdgeApplied.ContainsKey($taskEdgeName)-and($taskEdgeKey.GetValue($taskEdgeName)-eq$taskEdgeApplied[$taskEdgeName])){
       if($taskEdgeOriginal.Exists){$taskEdgeKey.SetValue($taskEdgeName,$taskEdgeOriginal.Value,[Microsoft.Win32.RegistryValueKind]$taskEdgeOriginal.Kind)}else{$taskEdgeKey.DeleteValue($taskEdgeName,$false)}
     }
   }}
 }finally{if($taskEdgeKey){$taskEdgeKey.Dispose()}}
 if(-not$taskEdgeKeyExisted){
   $taskEdgeKey=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskEdgePolicy)
   $taskEdgeEmpty=$false
   try{if($taskEdgeKey){$taskEdgeEmpty=$taskEdgeKey.ValueCount-eq0-and$taskEdgeKey.SubKeyCount-eq0}}finally{if($taskEdgeKey){$taskEdgeKey.Dispose()}}
   if($taskEdgeEmpty){[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($taskEdgePolicy,$false)}
 }
 $taskEdgeCheck=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($taskEdgePolicy)
 try{
   foreach($taskEdgeOriginal in $taskEdgeSnapshot){if($taskEdgeCheck-and($taskEdgeCheck.GetValueNames()-contains$taskEdgeOriginal.Name)){throw 'Policy restoration verification failed'}}
 }finally{if($taskEdgeCheck){$taskEdgeCheck.Dispose()}}
 'PASS exact original value absence restored'|Set-Content -LiteralPath (Join-Path $taskEdgeOutput 'restored.txt')
}
