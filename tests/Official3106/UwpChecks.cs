using System;
using System.Reflection;
using System.Threading;
namespace PaviseApp
{
    internal static class OfficialUwpChecks
    {
        private static int checks;
        private static readonly string[] Editions={"Core","CoreN","CoreSingleLanguage","CoreCountrySpecific","Professional","ProfessionalN","Enterprise","EnterpriseN","EnterpriseS","Education","EducationN","IoTEnterprise","IoTEnterpriseS"};
        private const string Pro=@"reg|LM|SOFTWARE\Policies\Microsoft\Windows\AppPrivacy|LetAppsRunInBackground";
        private const string Home=@"reg|CU|SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications|GlobalUserDisabled";
        private const string Exception=@"reg|LM|SOFTWARE\Policies\Microsoft\Windows\AppPrivacy|LetAppsRunInBackground_ForceAllowTheseApps";
        private static void Check(bool ok,string detail) {checks++;if(!ok)throw new System.Exception(detail);}
        private static bool Throws(Action action) {try{action();return false;}catch{return true;}}
        private static void MappingAndGuards()
        {
            var native=new PrivacyNativeBackend();
            Check(!EnvironmentEnhancementEligibility.Allowed,"memory primitive failed to simulate 8GB low-memory machine");
            Check(!PrivacyActionCatalog.IsRetired(PrivacyActionCatalog.BackgroundGlobal),"background restriction still retired");
            foreach(string edition in Editions)foreach(int build in new[]{19045,26100})
            {
                Settings.UseTransientStoreForCurrentProcess();
                OfficialPlatformMocks.Privacy=new PrivacyPlatform{Edition=edition,Build=build};
                OfficialPlatformMocks.Registry.Clear();OfficialPlatformMocks.Writes.Clear();
                OfficialPlatformMocks.Registry[Exception]="m:preserved-app-exception";
                var plan=native.Inspect(PrivacyActionCatalog.BackgroundGlobal,"");
                bool home=edition.StartsWith("Core",StringComparison.Ordinal);
                string local=home?Home:Pro, wanted=home?"d:1":"d:2";
                Check(plan.Supported&&plan.Limited&&plan.Targets.Length==1,"unsupported or broad UWP plan: "+edition);
                Check(PrivacyControl.CanonicalTarget(plan.Targets[0].Key)==local&&plan.Targets[0].Wanted==wanted,"wrong edition mapping: "+edition);
                Check(plan.Targets[0].RestartRequired,"UWP change omitted restart requirement");
                Check(native.Write(PrivacyActionCatalog.BackgroundGlobal,plan.Targets[0].Key,wanted),"correct UWP write rejected");
                Check(OfficialPlatformMocks.Writes.Count==1&&OfficialPlatformMocks.Writes[0]==local&&OfficialPlatformMocks.Registry[Exception]=="m:preserved-app-exception","UWP write touched app exceptions");
                Check(!native.Write(PrivacyActionCatalog.BackgroundGlobal,plan.Targets[0].Key,home?"d:0":"d:1"),"noncanonical UWP value accepted");
                var memory=native.Inspect(PrivacyActionCatalog.NtfsMemory,"");
                Check(!memory.Supported&&memory.UnavailableReason=="memory","NTFS gate did not remain limited to NTFS");
                string foreign=plan.Targets[0].Key.Replace(OfficialPlatformMocks.Machine,"22222222222222222222222222222222");
                Check(Throws(delegate{native.Write(PrivacyActionCatalog.BackgroundGlobal,foreign,wanted);}),"foreign machine target admitted");
                if(home) {string wrongUser=plan.Targets[0].Key.Replace(OfficialPlatformMocks.User,"S-1-5-21-1-2-3-1002");Check(Throws(delegate{native.Write(PrivacyActionCatalog.BackgroundGlobal,wrongUser,wanted);}),"foreign account target admitted");}
                OfficialPlatformMocks.Privacy.Managed=true;
                Check(native.Inspect(PrivacyActionCatalog.BackgroundGlobal,"").Managed,"managed machine was not gated");
                Check(Throws(delegate{native.Write(PrivacyActionCatalog.BackgroundGlobal,plan.Targets[0].Key,wanted);}),"managed policy write accepted");
                OfficialPlatformMocks.Privacy.Managed=false;OfficialPlatformMocks.Privacy.Build=18363;
                Check(!native.Inspect(PrivacyActionCatalog.BackgroundGlobal,"").Supported,"unsupported build was admitted");
            }
        }
        private static void ProductionControlAtLowMemory()
        {
            Settings.UseTransientStoreForCurrentProcess();
            OfficialPlatformMocks.Registry.Clear();OfficialPlatformMocks.Writes.Clear();
            OfficialPlatformMocks.Privacy=new PrivacyPlatform{Edition="Professional",Build=19045};
            PrivacyControl.Bind(new PrivacyNativeBackend());PrivacyControl.AdmissionAllowed=delegate{return true;};
            using(var completed=new ManualResetEvent(false))
            {
                Check(PrivacyControl.Set(PrivacyActionCatalog.BackgroundGlobal,true,"",delegate{completed.Set();}),"UWP production control request rejected");
                Check(completed.WaitOne(5000),"UWP production control request did not complete");
                var state=PrivacyControl.State(PrivacyActionCatalog.BackgroundGlobal);
                Check(OfficialPlatformMocks.ReadRegistry(Pro)=="d:2"&&OfficialPlatformMocks.Writes.Count==1,"low-memory machine failed UWP control apply: "+state.State);
                Check(PrivacyControl.PreferenceEnabled(PrivacyActionCatalog.BackgroundGlobal),"UWP preference was not committed");
                completed.Reset();
                Check(PrivacyControl.Set(PrivacyActionCatalog.BackgroundGlobal,false,"",delegate{completed.Set();})&&completed.WaitOne(5000),"UWP production restore did not complete");
                Check(OfficialPlatformMocks.ReadRegistry(Pro)=="absent"&&!PrivacyControl.PreferenceEnabled(PrivacyActionCatalog.BackgroundGlobal),"UWP restore lost exact original or preference");
                Check(PerformanceActionStore.All().Count==0,"UWP restore left unexplained durable debt");
            }
        }
        private static int Main()
        {
            try {Logger.WritesEnabled=false;MappingAndGuards();ProductionControlAtLowMemory();Console.WriteLine("PASS official UWP migration assertions="+checks+" native_registry=mocked RAM=8GB policy_writes=false application_started=false");return 0;}
            catch(System.Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
