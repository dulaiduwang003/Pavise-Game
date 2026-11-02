param([string]$SourceRoot,[string]$OutputDirectory,[switch]$SingleCase,[switch]$CompileOnly,[switch]$BackdropPreview,[string]$PreviewCover,[ValidateSet(0,96,144,192)][int]$DpiCase=0)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $SourceRoot){$SourceRoot=Join-Path $workspace 'src/PaviseApp'}
$source=[IO.Path]::GetFullPath($SourceRoot)
if(Test-Path -LiteralPath (Join-Path $source 'PaviseApp/PanelForm.cs')){$source=Join-Path $source 'PaviseApp'}
if(-not (Test-Path -LiteralPath (Join-Path $source 'PanelForm.cs'))){throw "Official workspace source not found: $source"}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Pavise-OfficialWorkspace-'+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$frozen=Join-Path $output 'frozen'
if(Test-Path -LiteralPath $frozen){throw 'Choose a fresh OutputDirectory; an existing frozen snapshot is retained as validation evidence.'}
$null=New-Item -ItemType Directory -Path $frozen -Force
$manifest=New-Object 'Collections.Generic.List[object]'
$sources=@(& rg --files $source -g '*.cs')
$parent=Split-Path -Parent $source
$sources+=@(Get-ChildItem -LiteralPath $parent -File -Filter '*.cs' | Select-Object -ExpandProperty FullName)
foreach($original in $sources){
 $original=[IO.Path]::GetFullPath($original)
 $relative=if($original.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){'PaviseApp/'+$original.Substring($source.Length+1)}else{[IO.Path]::GetFileName($original)}
 $copy=Join-Path $frozen $relative
 $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($copy)) -Force
 [IO.File]::WriteAllBytes($copy,[IO.File]::ReadAllBytes($original))
 $manifest.Add([pscustomobject]@{Path=$relative;Original=$original;Frozen=$copy;SHA256=(Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash.ToLowerInvariant()})
}
$runtimeConfig=Join-Path $workspace 'Pavise.exe.config';$frozenConfig=Join-Path $frozen 'Pavise.exe.config'
[IO.File]::WriteAllBytes($frozenConfig,[IO.File]::ReadAllBytes($runtimeConfig))
$manifest.Add([pscustomobject]@{Path='Pavise.exe.config';Original=$runtimeConfig;Frozen=$frozenConfig;SHA256=(Get-FileHash -LiteralPath $runtimeConfig -Algorithm SHA256).Hash.ToLowerInvariant()})
$manifest | Export-Csv -LiteralPath (Join-Path $output 'source-manifest.tsv') -Delimiter ([char]9) -NoTypeInformation
$whitelistReference=Join-Path $workspace 'tests/Official3106/2223-preset-whitelist.txt'
[IO.File]::WriteAllBytes((Join-Path $output '2223-preset-whitelist.txt'),[IO.File]::ReadAllBytes($whitelistReference))
function Find-ClosingBrace([string]$Text,[int]$Start){
 $depth=0;$state='code'
 for($i=$Start;$i -lt $Text.Length;$i++){
  $c=$Text[$i];$next=if($i+1 -lt $Text.Length){$Text[$i+1]}else{[char]0}
  if($state -eq 'line'){if($c -eq "`n"){$state='code'};continue}
  if($state -eq 'block'){if($c -eq '*' -and $next -eq '/'){$state='code';$i++};continue}
  if($state -eq 'verbatim'){if($c -eq '"'){if($next -eq '"'){$i++}else{$state='code'}};continue}
  if($state -eq 'string' -or $state -eq 'char'){if($c -eq '\'){$i++}elseif(($state -eq 'string' -and $c -eq '"') -or ($state -eq 'char' -and $c -eq "'")){$state='code'};continue}
  if($c -eq '/' -and $next -eq '/'){$state='line';$i++;continue}
  if($c -eq '/' -and $next -eq '*'){$state='block';$i++;continue}
  if($c -eq '@' -and $next -eq '"'){$state='verbatim';$i++;continue}
  if($c -eq '"'){$state='string';continue}
  if($c -eq "'"){$state='char';continue}
  if($c -eq '{'){$depth++}elseif($c -eq '}'){$depth--;if($depth -eq 0){return $i}}
 }
 throw 'Unbalanced fixture source braces'
}
function Set-FixtureBody([string]$Text,[string]$Signature,[string]$Body){
 if(([regex]::Matches($Text,[regex]::Escape($Signature))).Count -ne 1){throw "Fixture seam must match exactly once: $Signature"}
 $start=$Text.IndexOf($Signature,[StringComparison]::Ordinal);$open=$Text.IndexOf('{',$start);$close=Find-ClosingBrace $Text $open
 return $Text.Substring(0,$open+1)+"`n"+$Body+"`n"+$Text.Substring($close)
}
function Write-Utf8([string]$Path,[string]$Text){[IO.File]::WriteAllText($Path,$Text,(New-Object Text.UTF8Encoding($false)))}
# These seams affect only the frozen fixture. Production sources remain unchanged.
$settingsPath=Join-Path $frozen 'PaviseApp/Settings.cs'
$settings=[IO.File]::ReadAllText($settingsPath)
$memoryMembers=' private static readonly Dictionary<string, object> uiFixtureMemory = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); internal static string UiFixtureRejectedWrite; internal static void ResetUiFixtureMemory() { uiFixtureMemory.Clear(); UiFixtureRejectedWrite=null; BumpMutationGeneration(); } private static bool UiFixtureSave(string name, object value) { if(name==UiFixtureRejectedWrite)return false; uiFixtureMemory[name]=value; BumpMutationGeneration(); return true; } '
$settings=[regex]::Replace($settings,'(internal static class Settings\s*\{)','$1'+$memoryMembers,1)
foreach($signature in @('private static RegistryKey ProfiledOpen()','private static RegistryKey ProfiledCreate()','private static RegistryKey ProfiledOpenWritable()')){$settings=Set-FixtureBody $settings $signature 'throw new InvalidOperationException("UI fixture must never access the real settings registry");'}
$settings=Set-FixtureBody $settings 'private static object ReadValue(string name)' 'object value; return uiFixtureMemory.TryGetValue(name, out value) ? value : null;'
$settings=Set-FixtureBody $settings 'public static bool Save(string name, bool val)' 'return UiFixtureSave(name, val ? 1 : 0);'
$settings=Set-FixtureBody $settings 'internal static bool SaveDurable(string name, bool val)' 'return UiFixtureSave(name, val ? 1 : 0);'
$settings=Set-FixtureBody $settings 'private static bool SaveString(string name, string val, bool durable)' 'return UiFixtureSave(name, val ?? "");'
$settings=Set-FixtureBody $settings 'public static void Remove(string name)' 'uiFixtureMemory.Remove(name); BumpMutationGeneration();'
$settings=Set-FixtureBody $settings 'internal static bool TryRemove(string name, bool durable)' 'uiFixtureMemory.Remove(name); BumpMutationGeneration(); return true;'
$settings=Set-FixtureBody $settings 'private static bool TryLoadRawNames(string prefix, out string[] names)' 'var list=new List<string>(); foreach(string key in uiFixtureMemory.Keys) if(key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) list.Add(key); names=list.ToArray(); return true;'
$settings=Set-FixtureBody $settings 'private static bool TryLoadRawStr(string name, out string value, out bool present)' 'object raw; present=uiFixtureMemory.TryGetValue(name,out raw); value=present ? Convert.ToString(raw,System.Globalization.CultureInfo.InvariantCulture) : ""; return true;'
$settings=Set-FixtureBody $settings 'internal static bool SaveGpuIdentityReceipt(string value)' 'return UiFixtureSave(GpuIdentityReceiptKey, value ?? "");'
Write-Utf8 $settingsPath $settings
$dpiPath=Join-Path $frozen 'PaviseApp/Dpi.cs'
$dpi=[IO.File]::ReadAllText($dpiPath)
if(([regex]::Matches($dpi,[regex]::Escape('Screen.PrimaryScreen.WorkingArea'))).Count -ne 1){throw 'The legacy primary-screen DPI boundary must occur exactly once.'}
$dpi=$dpi.Replace('Screen.PrimaryScreen.WorkingArea','UiFixturePrimaryArea')
$dpi=[regex]::Replace($dpi,'(internal static class Dpi\s*\{)','$1 internal static Rectangle UiFixturePrimaryArea = Screen.PrimaryScreen.WorkingArea; internal static int UiFixtureWindowDpi = 96; ',1)
if(-not $dpi.Contains('using System.Drawing;')){$dpi='using System.Drawing;'+"`n"+$dpi}
$dpi=Set-FixtureBody $dpi 'public static int WindowDpi(IntPtr hwnd)' 'return hwnd == IntPtr.Zero ? 0 : UiFixtureWindowDpi;'
Write-Utf8 $dpiPath $dpi
$brandPath=Join-Path $frozen 'PaviseApp/WorkspaceBrand.cs'
$brand=[IO.File]::ReadAllText($brandPath)
$brand=[regex]::Replace($brand,'(internal sealed class WorkspaceBrand : Control\s*\{)','$1 internal Action UiFixturePaintFault; ',1)
if(([regex]::Matches($brand,'private void PaintBrand\(Graphics graphics\)\s*\{')).Count -ne 1){throw 'Brand paint fault seam must match exactly once'}
$brand=[regex]::Replace($brand,'(private void PaintBrand\(Graphics graphics\)\s*\{)','$1 if(UiFixturePaintFault!=null)UiFixturePaintFault(); ',1)
Write-Utf8 $brandPath $brand
$panelPath=Join-Path $frozen 'PaviseApp/PanelForm.cs'
$panel=[IO.File]::ReadAllText($panelPath)
$panel=Set-FixtureBody $panel 'private void SyncUiActivity()' 'uiActive=false; uiActivityKnown=true;'
$panel=Set-FixtureBody $panel 'private void QueueUiStateWork(Action work)' '// UI-only fixture never schedules background system inspection.'
$panel=[regex]::Replace($panel,'(partial class PanelForm\s*:\s*Form\s*\{)','$1 internal readonly List<Action> UiFixtureOverviewWork = new List<Action>(); ',1)
if(-not $panel.Contains('UiFixtureOverviewWork =')){throw 'Overview work fixture insertion did not match PanelForm'}
$panel=Set-FixtureBody $panel 'private bool QueueOverviewTelemetryWork(Action work)' 'UiFixtureOverviewWork.Add(work); return true;'
$panel=[regex]::Replace($panel,'(private void OnFitTick\(object sender, EventArgs e\)\s*\{)','$1 if(Dpi.NoFit)return; // Synthetic-DPI static fixture; the native window protocol still runs the original fitter. ',1)
Write-Utf8 $panelPath $panel
$privacyPath=Join-Path $frozen 'PaviseApp/PrivacyOptionsPanel.cs'
$privacy=[IO.File]::ReadAllText($privacyPath)
$privacy=Set-FixtureBody $privacy 'private bool InspectionVisible' 'get { return false; }'
Write-Utf8 $privacyPath $privacy
$dialogPath=Join-Path $frozen 'PaviseApp/PaviseDialog.cs'
$dialog=[IO.File]::ReadAllText($dialogPath)
$dialog=[regex]::Replace($dialog,'(internal sealed class PaviseDialog : Form\s*\{)','$1 internal static Action<IWin32Window, string, string, Control, int> UiFixtureShow; ',1)
$dialog=Set-FixtureBody $dialog 'public static void Show(IWin32Window owner, string title, string body, Control content, int width)' 'if(UiFixtureShow==null)throw new InvalidOperationException("UI fixture must never display a real dialog"); UiFixtureShow(owner,title,body,content,width);'
Write-Utf8 $dialogPath $dialog
Write-Utf8 (Join-Path $output 'test-only-seams.txt') 'Frozen fixture only: in-memory Settings with rejected-write injection; real settings registry access guarded; PanelForm.SyncUiActivity inactive; QueueUiStateWork disabled; PrivacyOptionsPanel.InspectionVisible=false; PaviseDialog.Show replaced by a controlled callback; Dpi.Fit uses a synthetic primary working area without changing its algorithm, and WindowDpi returns a controlled read-only fixture value. No Pavise runtime launch or actual performance setting writes. Overview readings are controlled presentation fixtures, not actual sensor validation.'
[IO.File]::AppendAllText((Join-Path $output 'test-only-seams.txt'),' WorkspaceBrand.PaintBrand has a frozen-fixture-only fault callback for persistent GDI+ failures; production sources contain no fault hook. Disposed logo/path/font cache checks use actual GDI+ resources and the production recovery code.')
[IO.File]::AppendAllText((Join-Path $output 'test-only-seams.txt'),' OnFitTick is suppressed only for the synthetic static layout fixture (Dpi.NoFit=true), preventing native working-area constraints from rebuilding and disposing the controls under test. The separate native window protocol keeps the original fitter (Dpi.NoFit=false).')
if($BackdropPreview){
 $lolPath=Join-Path $frozen 'PaviseApp/LolGameExtension.cs';$lol=[IO.File]::ReadAllText($lolPath)
 $lol=Set-FixtureBody $lol 'public override GameCardExtension CreateCard(GameProfile profile)' 'return new LolCardPanel(Service);'
 Write-Utf8 $lolPath $lol
 $lolServicePath=Join-Path $frozen 'PaviseApp/LolOptimizationService.cs';$lolService=[IO.File]::ReadAllText($lolServicePath)
 $lolService=Set-FixtureBody $lolService 'public void Start()' '// Screenshot fixture never starts a game-monitoring worker.'
 $lolService=Set-FixtureBody $lolService 'public LolOptimizationSnapshot GetSnapshot()' 'return new LolOptimizationSnapshot(true,true,false,false,@"C:\PaviseUiPreview\WeGameApps\英雄联盟",@"C:\PaviseUiPreview\WeGame",true,true,false,false,false,"None",false,0,0,0,0,0L,"","",new DateTime(2026,10,28),DateTime.MinValue,false);'
 Write-Utf8 $lolServicePath $lolService
 $lolCardPath=Join-Path $frozen 'PaviseApp/LolCardPanel.cs';$lolCard=[IO.File]::ReadAllText($lolCardPath)
 $lolCardStart=$lolCard.IndexOf('private void RefreshState(bool inspectFiles)',[StringComparison]::Ordinal)
 if($lolCardStart -lt 0){throw 'LoL screenshot inspection seam missing'}
 $lolCard=$lolCard.Insert($lolCard.IndexOf('{',$lolCardStart)+1,"`n inspectFiles=false; // Screenshot only: no file inspection.`n")
 Write-Utf8 $lolCardPath $lolCard
}
$fixture=Join-Path $workspace 'tests/Official3106/UiWorkspaceChecks.cs'
$fixtureCopy=Join-Path $output 'UiWorkspaceChecks.cs'
[IO.File]::WriteAllBytes($fixtureCopy,[IO.File]::ReadAllBytes($fixture))
$notesFixture=Join-Path $output 'ReleaseNotesOnceChecks.cs'
[IO.File]::WriteAllBytes($notesFixture,[IO.File]::ReadAllBytes((Join-Path $workspace 'tests/Official3106/ReleaseNotesOnceChecks.cs')))
$dialogFixture=Join-Path $output 'DialogReadabilityChecks.cs'
[IO.File]::WriteAllBytes($dialogFixture,[IO.File]::ReadAllBytes((Join-Path $workspace 'tests/Official3106/DialogReadabilityChecks.cs')))
$aiPromptFixture=Join-Path $output 'AiTuningPromptChecks.cs'
[IO.File]::WriteAllBytes($aiPromptFixture,[IO.File]::ReadAllBytes((Join-Path $workspace 'tests/Official3106/AiTuningPromptChecks.cs')))
$presetPagePath=Join-Path $frozen 'PaviseApp/PanelForm.ConfigurationPresets.cs'
$presetPage=[IO.File]::ReadAllText($presetPagePath)
# Guide presentation is intercepted; applying still uses the injected fake preset backend.
Write-Utf8 $presetPagePath ($presetPage.Replace('#if PAVISE_SELFTEST','#if true'))
$sizingFixture=Join-Path $output 'LegacyWindowSizingChecks.cs'
[IO.File]::WriteAllBytes($sizingFixture,[IO.File]::ReadAllBytes((Join-Path $workspace 'tests/Official3106/LegacyWindowSizingChecks.cs')))
function Invoke-HiddenFixture([string]$File,[string]$Arguments,[int]$LimitMs){
 $info=New-Object Diagnostics.ProcessStartInfo;$info.FileName=$File;$info.Arguments=$Arguments;$info.WorkingDirectory=$output
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle='Hidden';$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $child=New-Object Diagnostics.Process;$child.StartInfo=$info
 try{$null=$child.Start();$stdout=$child.StandardOutput.ReadToEndAsync();$stderr=$child.StandardError.ReadToEndAsync();$clock=[Diagnostics.Stopwatch]::StartNew();$timedOut=$false
  while(-not $child.WaitForExit(500)){if($clock.ElapsedMilliseconds -gt $LimitMs){$timedOut=$true;$child.Kill();break}}
  $child.WaitForExit();return [pscustomobject]@{Exit=$(if($timedOut){124}else{$child.ExitCode});Text=$stdout.Result+$stderr.Result;TimedOut=$timedOut}
 }finally{$child.Dispose()}
}
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe=Join-Path $output 'Official3106UiChecks.exe';$rsp=Join-Path $output 'sources.rsp'
$arguments=@('/nologo','/unsafe+','/target:exe','/platform:x64','/optimize+','/codepage:65001','/nowarn:0162,0649,0414,0675,1718','/main:PaviseApp.Official3106UiChecks',('/out:"'+$exe+'"'),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Management.dll','/reference:System.Xml.dll','/reference:System.Security.dll')
$arguments+=@(Get-ChildItem -LiteralPath $frozen -Recurse -File -Filter '*.cs' | ForEach-Object{'"'+$_.FullName+'"'})
$backdropFixture=Join-Path $output 'BackdropPreviewChecks.cs'
[IO.File]::WriteAllBytes($backdropFixture,[IO.File]::ReadAllBytes((Join-Path $workspace 'tests/Official3106/BackdropPreviewChecks.cs')))
$arguments+='"'+$backdropFixture+'"';$arguments+='"'+$fixtureCopy+'"';$arguments+='"'+$notesFixture+'"';$arguments+='"'+$dialogFixture+'"';$arguments+='"'+$aiPromptFixture+'"';$arguments+='"'+$sizingFixture+'"';[IO.File]::WriteAllLines($rsp,$arguments,(New-Object Text.UTF8Encoding($false)))
$compile=Invoke-HiddenFixture $compiler ('@"'+$rsp+'"') 60000;Write-Utf8 (Join-Path $output 'compile.log') $compile.Text
if($compile.Exit -ne 0){throw ('Official UI fixture compilation failed: '+$compile.Text)}
[IO.File]::WriteAllBytes(($exe+'.config'),[IO.File]::ReadAllBytes($frozenConfig))
$run=$null;$window=$null;$negative=$null;$notes=$null;$dialogs=$null
if(-not $CompileOnly){
  $mode=if($BackdropPreview){' --backdrop-preview "'+$PreviewCover+'"'}elseif($DpiCase){' --dpi '+$DpiCase}elseif($SingleCase){' --single'}else{''}
  $run=Invoke-HiddenFixture $exe ('"'+$output+'"'+$mode) $(if($SingleCase -or $BackdropPreview -or $DpiCase){60000}else{240000})
  Write-Utf8 (Join-Path $output 'result.txt') $run.Text;Write-Output $run.Text
 if($run.Exit -ne 0 -or $run.Text -notmatch 'PASS (official6 UI assertions=|backdrop preview checks=)'){throw ('Official UI fixture failed: '+$run.Text)}
 # Native protocol owns a fresh, strictly DWM-cloaked/nonactivating process on
 # the input desktop; static synthetic-DPI layout uses a private desktop.
 $window=Invoke-HiddenFixture $exe ('"'+$output+'" --window-only') 60000;Write-Utf8 (Join-Path $output 'window-result.txt') $window.Text;Write-Output $window.Text
 if($window.Exit -ne 0 -or $window.Text -notmatch 'PASS official6 UI assertions=.*window_protocol_cases=1'){throw ('Official window protocol fixture failed: '+$window.Text)}
 $notes=Invoke-HiddenFixture $exe ('"'+$output+'" --release-notes-only') 60000;Write-Utf8 (Join-Path $output 'release-notes-result.txt') $notes.Text;Write-Output $notes.Text
 if($notes.Exit -ne 0 -or $notes.Text -notmatch 'PASS release notes once assertions='){throw ('Release notes fixture failed: '+$notes.Text)}
 $dialogs=Invoke-HiddenFixture $exe ('"'+$output+'" --dialog-only') 60000;Write-Utf8 (Join-Path $output 'dialog-result.txt') $dialogs.Text;Write-Output $dialogs.Text
 if($dialogs.Exit -ne 0 -or $dialogs.Text -notmatch 'PASS dialog readability assertions='){throw ('Dialog readability fixture failed: '+$dialogs.Text)}
 $negative=Invoke-HiddenFixture $exe '--negative-control' 10000;Write-Utf8 (Join-Path $output 'negative-control.txt') $negative.Text
 if($negative.Exit -ne 3 -or $negative.Text -notmatch 'FAIL .*intentional UI migration negative control'){throw 'Intentional UI negative control did not fail correctly'}
}
$changed=@($manifest|Where-Object{(Get-FileHash -LiteralPath $_.Original -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.SHA256})
$verification=[pscustomobject]@{SourceRoot=$source;CompileOnly=[bool]$CompileOnly;SingleCase=[bool]$SingleCase;DpiCase=$DpiCase;FrozenSources=$manifest.Count;WorkspaceMatchesSnapshot=($changed.Count -eq 0);ChangedSources=@($changed|ForEach-Object{$_.Path});StaticLayoutIsolation='private desktop';WindowProtocolIsolation='DWM cloaked; nonactivating; input desktop';ReleaseNotesIsolation='private desktop; callback presenter; in-memory settings';DialogIsolation='private desktop; real controls; no product workers';ProductWorkersStarted=$false;RunExit=$(if($run){$run.Exit}else{$null});WindowProtocolExit=$(if($window){$window.Exit}else{$null});ReleaseNotesExit=$(if($notes){$notes.Exit}else{$null});DialogExit=$(if($dialogs){$dialogs.Exit}else{$null});NegativeControlExit=$(if($negative){$negative.Exit}else{$null});Screenshots=@(Get-ChildItem -LiteralPath $output -File -Filter '*.png' | Select-Object -ExpandProperty Name)}
$verification|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'verification.json') -Encoding UTF8
Write-Output ('OUTPUT '+$output)
if($changed.Count -ne 0){throw 'Sources changed while the frozen UI fixture ran; rerun against the final source'}
