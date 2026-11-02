#if PAVISE_SELFTEST && PAVISE_AUTO_BENCH
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
namespace PaviseApp
{
#if PAVISE_CORE_DRAFT_AUDIT
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Ordinary runtime is unavailable in this audit"); }
    }
#endif
    internal sealed class CoreEditorAuditForm : PanelForm
    {
        internal CoreEditorAuditForm(Tamer tamer, GameMode mode, Icon icon) : base(tamer, mode, icon, true) { }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnShown(EventArgs e) { }
    }
    internal static class CoreEditorDraftRegressionRunner
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        private static int checks;
        private static T Field<T>(object target, string name) { return (T)typeof(PanelForm).GetField(name, Hidden).GetValue(target); }
        private static void Call(object target, string method, params object[] args) { typeof(PanelForm).GetMethod(method, Hidden).Invoke(target,args); }
        private static void Check(bool value, string message) { if(!value) throw new Exception(message); checks++; }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [STAThread]
        private static int Main(string[] args)
        {
            // 主线程已持有窗口资源，不能切换桌面；与总自测相同，在新的 STA 线程上隔离运行。
            int result = 1;
            var worker = new System.Threading.Thread(delegate() { result = RunIsolated(); });
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start(); worker.Join();
            return result;
        }
        private static int RunIsolated()
        {
            // 界面与可能出现的提示框都放在私有桌面，不能出现在用户屏幕上；无法隔离则不运行。
            IntPtr desktop = CreateDesktop("PaviseCoreEditor-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
            if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop))
            {
                Console.Error.WriteLine("Private test desktop unavailable; no window was created");
                if (desktop != IntPtr.Zero) CloseDesktop(desktop);
                return 1;
            }
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                PanelForm.SuppressUiWorkersForTest = true; Dpi.NoFit = true;
                AutoCores.AllocatedForTest = delegate { return 0; }; AutoCores.SupportedForTest = true;
                CpuTopology.InjectTopologyForTest(0xffff, new ulong[] {3,12,48,192,768,3072,12288,49152},new ulong[0],0,0,0,0,false,false);
                CpuTopology.InjectCacheDomainsForTest(new System.Collections.Generic.KeyValuePair<uint,ulong>[] {new System.Collections.Generic.KeyValuePair<uint,ulong>(33554432,0xffff)});
                Dpi.Init(); Dpi.Update(96); Lang.Init(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                foreach(int dpi in new[]{96,144}) foreach(bool guard in new[]{false,true}) Run(dpi,guard);
                Console.WriteLine("PASS core editor interactions assertions="+checks+" runtime_started=false; workers_active=false; settings=transient; profile_writes=build_fixtures_only; desktop=private");
                return 0;
            }
            catch(Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { CloseDesktop(desktop); }
        }
        private static void Rebuild(CoreEditorAuditForm form)
        { Call(form,"RebuildUi"); Call(form,"BeginMoveSizeLoop"); Call(form,"StopPageReveal"); Call(form,"SyncCorePage"); }
        private static void Run(int dpi,bool guard)
        {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled=false; Lang.Set(0); Dpi.Update(dpi); Theme.DropFontCache();
                string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CoreEditorDraftRegressionRunner-fixture",dpi+"-"+guard); Directory.CreateDirectory(directory);
                var profile=new GameProfile{Id="draft-audit",Name="Draft audit",Root=directory,ExecutablePath=Path.Combine(directory,"fixture.exe")}; profile.Entries.Add("fixture");
                var other=new GameProfile{Id="draft-other",Name="Other audit",Root=directory,ExecutablePath=Path.Combine(directory,"other.exe")}; other.Entries.Add("other");
                Check(new GameProfileStore(directory).Save(new[]{profile,other}),"fixture save failed");
                var core=new SuppressionCore(); var mode=new GameMode(directory,core); mode.InitializeAutomation();
                typeof(GameMode).GetField("enabled",Hidden).SetValue(mode,guard);
                using(var icon=IconArt.MakeIcon(24))
                using(var form=new CoreEditorAuditForm(new Tamer(core),mode,icon))
                {
                    form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); form.Show();
                    Call(form,"BeginMoveSizeLoop"); Call(form,"OpenGameCoreScheduling",profile.Id); Call(form,"StopPageReveal");
                    var matrix=Field<CoreMatrix>(form,"coreEditMatrix"); matrix.ToggleSelection(1UL);
                    Check(PolicyResolver.For(mode.GetProfiles()[0]).ManualCores.GameMask==1,"Single logical CPU was not saved");
                    Check(Field<Toggle>(form,"coreEnableToggle").Enabled,"Single logical CPU cannot enable scheduling");
                    Check(Field<Label>(form,"coreEditorResult").Text==Lang.T("schedule.manual.savedoff"),"Single CPU still shows an invalid-selection warning");
                    CheckCorePreview(form, profile.Id, directory);
                    var loadedSingle=new GameProfileStore(directory).LoadProfiles(false);
                    Check(PolicyResolver.For(loadedSingle[0]).ManualCores.GameMask==1,"Single CPU save did not reach disk");
                    Rebuild(form);
                    Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==1,"Rebuild discarded the saved single CPU");
                    matrix=Field<CoreMatrix>(form,"coreEditMatrix"); matrix.ToggleSelection(1UL);
                    ulong before=matrix.Selected; Rebuild(form);
                    ulong after=Field<CoreMatrix>(form,"coreEditMatrix").Selected;
                    Check(before==0&&after==0,"Rebuild discarded the empty core draft");
                    Check(PolicyResolver.For(mode.GetProfiles()[0]).ManualCores.GameMask==1,"Empty selection overwrote the saved single CPU");
                    // 场景只保留智能/竞技；退休场景不在界面提供，由 TrySetPreset 直接拒绝，不走界面提示框。
                    foreach(PerformancePreset preset in new[]{PerformancePreset.Standard,PerformancePreset.Competitive})
                    {
                        Call(form,"ChooseGlobalMode",preset);
                        Call(form,"SyncCorePage");
                        Check(mode.ActivePreset==preset&&Field<CoreMatrix>(form,"coreEditMatrix").Selected==0,"Mode change disabled or cleared empty selection requested="+preset+" actual="+mode.ActivePreset+" selected="+Field<CoreMatrix>(form,"coreEditMatrix").Selected+" profile="+Field<string>(form,"coreDraftProfile"));
                    }
                    PerformancePreset kept=mode.ActivePreset;
                    foreach(PerformancePreset retired in new[]{PerformancePreset.Extreme,PerformancePreset.Handheld})
                    {
                        Check(!mode.TrySetPreset(retired)&&mode.ActivePreset==kept,"Retired scene was accepted: "+retired);
                        Call(form,"SyncCorePage");
                        Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==0,"Rejected retired scene cleared the empty selection: "+retired);
                    }
                    Theme.SetLight(true); Rebuild(form);
                    Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==0,"Light theme discarded draft");
                    Theme.SetLight(false); Rebuild(form);
                    Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==0,"Dark theme discarded draft");
                    Lang.Set(1); Rebuild(form);
                    Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==0,"English rebuild discarded draft");
                    Lang.Set(0); Rebuild(form);
                    Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==0,"Chinese rebuild discarded draft");
                    Dpi.Update(dpi==96?144:96); Theme.DropFontCache(); Rebuild(form);
                    Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==0,"DPI rebuild discarded draft");
                    matrix=Field<CoreMatrix>(form,"coreEditMatrix"); matrix.ToggleSelection(3UL);
                    Check(PolicyResolver.For(mode.GetProfiles()[0]).ManualCores.GameMask==3,"Second core click did not save");
                    string savedPath=Path.Combine(directory,GameProfileStore.FileName);
                    using(var locked=new FileStream(savedPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                    {
                        matrix.ToggleSelection(4UL);
                        Check(matrix.Selected==3&&Field<string>(form,"coreSaveError")=="schedule.manual.savefailed","Save failure did not restore persisted mask and warning");
                        Rebuild(form);
                        Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==3&&Field<string>(form,"coreSaveError")=="schedule.manual.savefailed","Rebuild lost failed-save warning or mask");
                        var enabled=Field<Toggle>(form,"coreEnableToggle"); enabled.Checked=!enabled.Checked;
                        Check(!enabled.Checked&&!PolicyResolver.For(mode.GetProfiles()[0]).AutoCoresAllowed,"Failed scheduling toggle was left optimistic");
                    }
                    var isolation=Field<Toggle>(form,"coreIsolationToggle"); isolation.Checked=true;
                    Check(PolicyResolver.For(mode.GetProfiles()[0]).ManualCores.Isolation,"Isolation click did not persist");
                    foreach(int language in new[]{0,1,2})
                    {
                        Lang.Set(language); Rebuild(form);
                        var editor=Field<RoundPanel>(form,"coreEditor");
                        var result=Field<Label>(form,"coreEditorResult");
                        Check(editor.Controls.Find("coreManualHardAffinity",true).Length==0,"Hard-affinity toggle survived a language rebuild");
                        Check(result.Top==Field<Label>(form,"coreEditorIsolation").Bottom+Theme.S(10)&&result.Bottom<editor.Height,"Removed affinity explanation leaves a gap or clips the footer");
                        if(dpi==96&&!guard) using(var bitmap=new Bitmap(editor.Width,editor.Height))
                        {
                            editor.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                            bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"hard-affinity-"+language+".png"));
                        }
                    }
                    Lang.Set(0); Rebuild(form); isolation=Field<Toggle>(form,"coreIsolationToggle");
                    using(var locked=new FileStream(savedPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                    {
                        isolation.Checked=false;
                        Check(isolation.Checked&&PolicyResolver.For(mode.GetProfiles()[0]).ManualCores.Isolation,"Failed isolation toggle was left optimistic");
                        Check(Field<RoundPanel>(form,"coreEditor").Controls.Find("coreManualHardAffinity",true).Length==0,"Retired hard-affinity switch remains");
                    }
                    var activeProfile=mode.GetProfiles().Find(delegate(GameProfile p){return p.Id==profile.Id;});
                    string oldRaw=ManualCorePlan.Raw(activeProfile);
                    Check(mode.SaveManualCorePlan(profile.Id,oldRaw,new ManualCorePlan(12,false,ManualCorePlan.CurrentStamp()))==null,"external replacement fixture failed");
                    Rebuild(form);
                    Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==12&&Field<string>(form,"coreSaveError")=="schedule.manual.changed","Same-ID external replacement was overwritten by old draft");
                    Call(form,"OpenGameCoreScheduling",other.Id);
                    Check(Field<CoreMatrix>(form,"coreEditMatrix").Selected==0&&Field<string>(form,"coreDraftProfile")==other.Id,"Draft leaked into different game");
                    Field<CoreMatrix>(form,"coreEditMatrix").ToggleSelection(1UL);
                    var profiles=(System.Collections.Generic.List<GameProfile>)typeof(GameMode).GetField("profiles",Hidden).GetValue(mode); profiles.RemoveAll(delegate(GameProfile p){return p.Id==other.Id;});
                    Rebuild(form);
                    Check(Field<string>(form,"coreDraftProfile")==null&&!Field<RoundPanel>(form,"coreEditor").Visible,"Deleted profile retained an editable draft");
                    Check(!mode.IsActive&&!form.UiActive,"Editor started runtime or UI workers");
                    form.Hide();
                }
                Console.WriteLine("PASS core editor rebuild lifecycle dpi="+dpi+" guard="+guard);
        }
        private static void CheckCorePreview(CoreEditorAuditForm form, string profile, string directory)
        {
            var preview = Field<PlanComparison>(form, "corePlanPreview");
            Check(preview.CurrentText.Contains("尚未确认") && preview.SavedText.Contains("当前关闭"), "Saved-but-disabled selection is presented as a running plan");
            Check(!Field<Label>(form, "coreEditorRuntime").Visible, "Verbose affinity evidence is expanded by default");
            var toggle = Field<PillButton>(form, "coreEvidenceToggle");
            toggle.PerformClick();
            Check(Field<Label>(form, "coreEditorRuntime").Visible && Field<Label>(form, "coreEditorRuntime").Height > 0, "Affinity evidence cannot be disclosed");
            toggle.PerformClick();
            Check(!Field<Label>(form, "coreEditorRuntime").Visible, "Affinity evidence cannot be collapsed");
            foreach (int language in new[] { 0, 1 }) foreach (bool light in new[] { false, true })
            {
                Lang.Set(language); Theme.SetLight(light); Rebuild(form);
                preview = Field<PlanComparison>(form, "corePlanPreview");
                AutoCores.Publish(new AutoCoreStatus { ProfileId = profile, Watching = true, Manual = true,
                    ExpectedMask = 3, ObservedMask = 3, AffinityRead = true, AffinityVerified = true });
                Call(form, "SyncCorePage");
                Check(preview.CurrentText.Contains(CpuTopology.DescribeMask(3)) && preview.CurrentText.Contains(language == 0 ? "已回读确认" : "Readback verified"), "Readback is not shown as the actual current range");
                Check(preview.SavedText.Contains(CpuTopology.DescribeMask(1)) && !preview.SavedText.Contains(CpuTopology.DescribeMask(3)), "Saved range replaced the live readback preview");
                // A saved plan is not live merely because the target was set; rejected access wins over stale flags.
                AutoCores.Publish(new AutoCoreStatus { ProfileId = profile, Watching = true, Manual = true,
                    ExpectedMask = 3, ObservedMask = 3, AffinityRead = true, AffinityVerified = true, Blocked = "schedule.auto.external" });
                Call(form, "SyncCorePage");
                Check(preview.CurrentText.Contains(Lang.T("schedule.auto.external")) && !preview.CurrentText.Contains(language == 0 ? "已回读确认" : "Readback verified"), "Blocked affinity still promises that the plan is verified");
                AutoCores.Publish(new AutoCoreStatus { ProfileId = "another-game", Watching = true, Manual = true,
                    ExpectedMask = 3, ObservedMask = 3, AffinityRead = true, AffinityVerified = true });
                Call(form, "SyncCorePage");
                Check(preview.CurrentText.Contains(language == 0 ? "尚未确认" : "Not verified yet"), "Another game's runtime evidence leaked into this preview");
                Check(preview.Bottom < Field<PillButton>(form, "coreEvidenceToggle").Top
                    && Field<PillButton>(form, "coreEvidenceToggle").Bottom < Field<Panel>(form, "coreQuickPanel").Top, "Preview covers core selection or evidence disclosure");
                foreach (Control pane in preview.Controls)
                {
                    Check(preview.ClientRectangle.Contains(pane.Bounds), "Preview pane clipped");
                    foreach (Control label in pane.Controls) Check(pane.ClientRectangle.Contains(label.Bounds), "Preview text clipped at this scale/language");
                }
                using (var bitmap = new Bitmap(preview.Width, preview.Height))
                { preview.DrawToBitmap(bitmap, preview.ClientRectangle); bitmap.Save(Path.Combine(directory, "preview-" + language + "-" + light + ".png")); }
            }
            AutoCores.Publish(null); Lang.Set(0); Theme.SetLight(false); Rebuild(form);
        }
    }
}
#endif
