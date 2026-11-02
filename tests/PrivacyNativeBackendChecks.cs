#if PAVISE_SELFTEST && PAVISE_PRIVACY_NATIVE_RUNNER
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application runtime unavailable in privacy backend checks"); }
    }
    internal static class PrivacyNativeBackendChecks
    {
        private const string A = "Example.AppA_123456789abcd", X = "Example.AppX_123456789abcd";
        private const string Deny = @"reg|LM|SOFTWARE\Policies\Microsoft\Windows\AppPrivacy|LetAppsRunInBackground_ForceDenyTheseApps";
        private const string Allow = @"reg|LM|SOFTWARE\Policies\Microsoft\Windows\AppPrivacy|LetAppsRunInBackground_ForceAllowTheseApps";
        private const string UserChoice = @"reg|LM|SOFTWARE\Policies\Microsoft\Windows\AppPrivacy|LetAppsRunInBackground_UserInControlOfTheseApps";
        private const string Global = @"reg|LM|SOFTWARE\Policies\Microsoft\Windows\AppPrivacy|LetAppsRunInBackground";
        private const string HomeGlobal = @"reg|CU|SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications|GlobalUserDisabled";
        private const string AppraiserTask = @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser";
        private const string CeipTask = @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator";
        private static readonly string[] Editions = { "Core", "CoreN", "CoreSingleLanguage", "CoreCountrySpecific",
            "Professional", "ProfessionalN", "Enterprise", "EnterpriseN", "EnterpriseS", "Education", "EducationN", "IoTEnterprise", "IoTEnterpriseS" };
        private static readonly string Identity = PrivacyNativeBackend.B64("stable-service-identity");
        private static readonly string PcaIdentity = PrivacyNativeBackend.B64("stable-pca-identity");
        private static readonly string TaskHash = Convert.ToBase64String(new byte[32]);
        private static Dictionary<string, string> values, services, tasks;
        private static List<string> writes;
        private static PrivacyPlatform platform;
        private static PrivacyNativeBackend native;
        private static string machine, user;
        private static int count;
        private static Action<string, string> afterWrite;
        private static string root;
        private static void Check(bool condition, string detail) { count++; if (!condition) throw new Exception(detail); }
        private static string Get(Dictionary<string, string> data, string key, string fallback)
        { string value; return data.TryGetValue(key, out value) ? value : fallback; }
        private static void Reset()
        {
            PrivacyControl.ResetForTest(); Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest();
            values = new Dictionary<string, string>(); values[Deny] = "absent";
            services = new Dictionary<string, string> { { "DiagTrack", "v:4:" + Identity }, { "DiagTrack.Start", "c:2:" + Identity },
                { "PcaSvc", "v:4:" + PcaIdentity }, { "PcaSvc.Start", "c:3:" + PcaIdentity } };
            tasks = new Dictionary<string, string>(); writes = new List<string>(); afterWrite = null;
            platform = new PrivacyPlatform { Build = 26100, Edition = "Professional" };
            machine = "11111111111111111111111111111111"; user = "S-1-5-21-1-2-3-1001";
            PrivacyNativeBackend.PlatformForTest = delegate { return platform; };
            PrivacyNativeBackend.MachineForTest = delegate { return machine; };
            PrivacyNativeBackend.UserForTest = delegate { return user; };
            PrivacyNativeBackend.RegistryReadForTest = delegate(string target) { return Get(values, target, "absent"); };
            PrivacyNativeBackend.RegistryWriteForTest = delegate(string target, string value) {
                writes.Add(target); if (value == "absent") values.Remove(target); else values[target] = value;
                if (afterWrite != null) afterWrite(target, value); return true;
            };
            PrivacyNativeBackend.ServiceReadForTest = delegate(string target) { return Get(services, target, "missing"); };
            PrivacyNativeBackend.ServiceWriteForTest = delegate(string target, string value) {
                if (value.StartsWith("v:4:", StringComparison.Ordinal)
                    && Get(services, target + ".Start", "missing").StartsWith("c:4:", StringComparison.Ordinal)) return false;
                if (target.EndsWith(".Start", StringComparison.Ordinal) && value.StartsWith("c:4:", StringComparison.Ordinal)
                    && !Get(services, target.Substring(0, target.Length - 6), "missing").StartsWith("v:1:", StringComparison.Ordinal)) return false;
                writes.Add(target); services[target] = value; if (afterWrite != null) afterWrite(target, value); return true;
            };
            PrivacyNativeBackend.TaskReadForTest = delegate(string target) { return Get(tasks, target, "missing"); };
            PrivacyNativeBackend.TaskWriteForTest = delegate(string target, string value) { writes.Add(target); tasks[target] = value; return true; };
            PrivacyControl.QueueForTest = delegate(System.Threading.WaitCallback callback) { callback(null); return true; };
            PrivacyControl.BootForTest = delegate { return BootSessionIdentity.Create(new Guid("10203040-5060-7080-90a0-b0c0d0e0f001"), 1); };
            EnvironmentEnhancementEligibility.SampleForTest = delegate { return new EnvironmentEnhancementEligibility.MemorySnapshot { Installed = 32 * EnvironmentEnhancementEligibility.GiB, Total = 31 * EnvironmentEnhancementEligibility.GiB, Available = 8 * EnvironmentEnhancementEligibility.GiB }; };
            native = new PrivacyNativeBackend(); PrivacyControl.Bind(native);
        }
        private static void Set(string action, bool enabled, string target = "")
        { Check(PrivacyControl.Set(action, enabled, target, null), "Set was not admitted: " + action); }
        private static string Canonical(PrivacyTarget target) { return PrivacyControl.CanonicalTarget(target.Key); }
        private static void Target(PrivacyPlan plan, string key, string wanted, string detail)
        {
            int matches = 0;
            foreach (PrivacyTarget target in plan.Targets) if (Canonical(target) == key)
            { matches++; Check(target.Wanted == wanted, detail + " wrong value: " + key); }
            Check(matches == 1, detail + " missing or duplicate target: " + key);
        }
        private static string Scope(string target)
        { return "ctx|" + machine + "|" + (target.StartsWith("reg|CU|", StringComparison.Ordinal) ? user : "system") + "|" + target; }
        // Historical fixtures are seeded as receipts, never by calling a retired apply entry point.
        private static List<PrivacyTarget> HistoricalTargets(string action)
        {
            var result = new List<PrivacyTarget>();
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            Array settings = (Array)typeof(PrivacyNativeBackend).GetMethod("SettingsFor", flags).Invoke(null, new object[] { action, platform });
            foreach (object entry in settings)
            {
                string key = (string)entry.GetType().GetField("Target", flags | System.Reflection.BindingFlags.Instance).GetValue(entry);
                int value = (int)entry.GetType().GetField("Value", flags | System.Reflection.BindingFlags.Instance).GetValue(entry);
                result.Add(new PrivacyTarget { Key = Scope(key), Wanted = "d:" + value });
            }
            string service = action == PrivacyActionCatalog.Service ? "DiagTrack"
                : action == PrivacyActionCatalog.Compatibility && !platform.PolicyCapable ? "PcaSvc" : null;
            if (service != null)
            {
                string identity = service == "DiagTrack" ? Identity : PcaIdentity;
                result.Add(new PrivacyTarget { Key = Scope("svc|" + service), Wanted = "v:1:" + identity });
                result.Add(new PrivacyTarget { Key = Scope("start|" + service), Wanted = "c:4:" + identity });
            }
            string task = action == PrivacyActionCatalog.Ceip ? CeipTask : action == PrivacyActionCatalog.Appraiser ? AppraiserTask : null;
            if (task != null && tasks.ContainsKey(task)) result.Add(new PrivacyTarget { Key = Scope("task|" + task), Wanted = "t:0:" + TaskHash });
            return result;
        }
        private static List<PrivacyTarget> SeedHistory(string action)
        {
            List<PrivacyTarget> targets = HistoricalTargets(action);
            foreach (PrivacyTarget target in targets)
            {
                string original = native.Read(action, target.Key);
                if (original == target.Wanted) continue;
                var receipt = new PerformanceActionStore.Receipt { Id = action, Target = target.Key,
                    Original = original, Expected = target.Wanted, Committed = true };
                Check(Settings.SaveStrDurable(PerformanceActionStore.Slot(action, target.Key), PerformanceActionStore.Pack(receipt)), "Historical receipt could not be persisted");
                string key = Canonical(target);
                if (key.StartsWith("reg|")) values[key] = target.Wanted;
                else if (key.StartsWith("svc|")) services[key.Substring(4)] = target.Wanted;
                else if (key.StartsWith("start|")) services[key.Substring(6) + ".Start"] = target.Wanted;
                else if (key.StartsWith("task|")) tasks[key.Substring(5)] = target.Wanted;
            }
            Check(Settings.SaveStrDurable(PrivacyControl.PreferencePrefix + action, "1"), "Historical preference fixture failed");
            return targets;
        }
        private static void PlatformMatrix()
        {
            Check(PrivacyActionCatalog.All.Length == 2 && PrivacyActionCatalog.All[0].Key == PrivacyActionCatalog.NtfsMemory && PrivacyActionCatalog.All[1].Key == PrivacyActionCatalog.BackgroundGlobal, "Active environment catalog includes retired actions");
            foreach (int build in new[] { 19041,19045,22000,26100 })
            for (int index = 0; index < Editions.Length; index++)
            {
                Reset(); platform.Build = build; platform.Edition = Editions[index];
                string detail = platform.Edition + " " + build;
                Check(platform.PolicyCapable == (index >= 4) && platform.Enterprise == (index >= 6), detail + " edition classification");
                PrivacyPlan plan = native.Inspect(PrivacyActionCatalog.NtfsMemory, "");
                Check(plan.Supported && !plan.Managed && plan.Targets.Length == 1 && plan.Targets[0].RestartRequired, detail + " NTFS support or restart scope");
                Target(plan, @"reg|LM|SYSTEM\CurrentControlSet\Control\FileSystem|NtfsMemoryUsage", "d:2", detail);
                Check(PrivacyControl.Refresh(PrivacyActionCatalog.NtfsMemory, "") && writes.Count == 0 && PerformanceActionStore.All().Count == 0, "Readonly NTFS inspection wrote state");
                foreach (PrivacyAction action in PrivacyActionCatalog.Retired)
                {
                    PrivacyPlan retired = native.Inspect(action.Key, "");
                    Check(!retired.Supported && retired.UnavailableReason == "retired" && retired.Targets.Length == 0, "Retired control was re-offered: " + action.Key);
                }
            }
            Reset(); platform.Build = 19040;
            PrivacyPlan unsupported = native.Inspect(PrivacyActionCatalog.NtfsMemory, "");
            Check(!unsupported.Supported && unsupported.UnavailableReason == "system", "Minimum OS build bypassed");
        }
        private static void PlatformRoundTrips()
        {
            foreach (string edition in new[] { "CoreSingleLanguage", "ProfessionalN", "IoTEnterpriseS" })
            foreach (int build in new[] { 19041, 19045, 22000, 26100 })
            foreach (bool originallyAbsent in new[] { true, false })
            foreach (PrivacyAction action in PrivacyActionCatalog.All)
            {
                Reset(); platform.Edition = edition; platform.Build = build;
                tasks[AppraiserTask] = "t:1:" + TaskHash; tasks[CeipTask] = "t:1:" + TaskHash;
                PrivacyPlan plan = native.Inspect(action.Key, "");
                if (!plan.Supported) continue;
                var original = new Dictionary<string, string>();
                foreach (PrivacyTarget target in plan.Targets)
                {
                    string key = Canonical(target);
                    if (!key.StartsWith("reg|", StringComparison.Ordinal)) continue;
                    string value = originallyAbsent ? "absent" : target.Wanted == "d:0" ? "d:1" : "d:0";
                    if (!originallyAbsent) values[key] = value;
                    original[key] = value;
                }
                Set(action.Key, true);
                Check(PrivacyControl.State(action.Key).Configured && PerformanceActionStore.All().Count > 0,
                    edition + " " + build + " did not capture originals: " + action.Key);
                foreach (PrivacyTarget target in plan.Targets)
                    Check(native.Read(action.Key, target.Key) == target.Wanted, "Roundtrip failed to apply: " + target.Key);
                Set(action.Key, false);
                foreach (KeyValuePair<string, string> pair in original)
                    Check(Get(values, pair.Key, "absent") == pair.Value, "Roundtrip lost original: " + pair.Key);
                Check(PerformanceActionStore.All().Count == 0 && !PrivacyControl.State(action.Key).Configured, "Roundtrip retained receipts: " + action.Key);
                Check(tasks[AppraiserTask] == "t:1:" + TaskHash && tasks[CeipTask] == "t:1:" + TaskHash,
                    "Roundtrip failed to restore existing task");
                Check(services["DiagTrack"] == "v:4:" + Identity && services["DiagTrack.Start"] == "c:2:" + Identity,
                    "Roundtrip failed to restore service");
                Check(services["PcaSvc"] == "v:4:" + PcaIdentity && services["PcaSvc.Start"] == "c:3:" + PcaIdentity,
                    "Roundtrip failed to restore PCA service");
            }
        }
        private static void PlatformChangeRecovery()
        {
            string[] from = { "Core", "CoreCountrySpecific", "EnterpriseS", "IoTEnterpriseS", "ProfessionalN" };
            string[] to = { "Professional", "IoTEnterpriseS", "Core", "ProfessionalN", "CoreSingleLanguage" };
            int[] fromBuild = { 19041, 26100, 26100, 26100, 22000 }, toBuild = { 26100, 19045, 19045, 19041, 26100 };
            for (int index = 0; index < from.Length; index++)
            foreach (PrivacyAction action in PrivacyActionCatalog.All)
            {
                Reset(); platform.Edition = from[index]; platform.Build = fromBuild[index];
                tasks[AppraiserTask] = "t:1:" + TaskHash;
                PrivacyPlan plan = native.Inspect(action.Key, ""); if (!plan.Supported) continue;
                Set(action.Key, true); int receipts = PerformanceActionStore.All().Count;
                Check(receipts > 0, "No receipts before platform transition");
                platform.Edition = to[index]; platform.Build = toBuild[index];
                Settings.SaveStrDurable(PerformanceActionStore.ListKey, "");
                Set(action.Key, false);
                Check(PerformanceActionStore.All().Count == 0, "History not restored after " + from[index] + " -> " + to[index] + ": " + action.Key);
                foreach (PrivacyTarget target in plan.Targets)
                {
                    string key = Canonical(target);
                    if (key.StartsWith("reg|", StringComparison.Ordinal)) Check(Get(values, key, "absent") == "absent", "Platform transition lost original: " + key);
                }
                Check(tasks[AppraiserTask] == "t:1:" + TaskHash, "Platform transition task restore failed");
                Check(services["PcaSvc"] == "v:4:" + PcaIdentity && services["PcaSvc.Start"] == "c:3:" + PcaIdentity,
                    "Platform transition failed to restore PCA service");
            }
        }
        private static void InspectionFailures()
        {
            Reset(); PrivacyNativeBackend.RegistryReadForTest = delegate(string target) { throw new UnauthorizedAccessException("registry access denied fixture"); };
            Check(PrivacyControl.Refresh(PrivacyActionCatalog.NtfsMemory, ""), "NTFS inspection was not admitted");
            Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "unverified" && writes.Count == 0, "Readonly failure became successful configuration");
            Set(PrivacyActionCatalog.NtfsMemory, true);
            Check(writes.Count == 0 && PerformanceActionStore.All().Count == 0, "Unreadable NTFS original was overwritten");
            foreach (bool throws in new[] { false,true })
            {
                Reset(); EnvironmentEnhancementEligibility.SampleForTest = throws
                    ? (Func<EnvironmentEnhancementEligibility.MemorySnapshot>)delegate { throw new IOException("memory sample failed"); } : null;
                PrivacyPlan plan = native.Inspect(PrivacyActionCatalog.NtfsMemory, "");
                Check(!plan.Supported && plan.UnavailableReason == "memory" && plan.Targets.Length == 0, "Missing memory evidence enabled NTFS");
                Set(PrivacyActionCatalog.NtfsMemory, true);
                Check(writes.Count == 0 && PerformanceActionStore.All().Count == 0, "Failed memory sampling reached system writes");
            }
            Reset(); var live = native.Inspect(PrivacyActionCatalog.NtfsMemory, "");
            EnvironmentEnhancementEligibility.SampleForTest = null; bool blocked = false;
            try { native.Write(PrivacyActionCatalog.NtfsMemory, live.Targets[0].Key, "d:2"); } catch (NotSupportedException) { blocked = true; }
            Check(blocked && writes.Count == 0, "Memory loss after inspection bypassed native admission");
            foreach (PrivacyAction action in PrivacyActionCatalog.Retired)
            {
                Reset(); int discoveries = 0;
                PrivacyNativeBackend.PlatformForTest = delegate { discoveries++; throw new InvalidOperationException("Retired platform discovery forbidden"); };
                PrivacyNativeBackend.RegistryReadForTest = delegate(string target) { discoveries++; throw new InvalidOperationException("Retired registry discovery forbidden"); };
                PrivacyNativeBackend.ServiceReadForTest = delegate(string target) { discoveries++; throw new InvalidOperationException("Retired service discovery forbidden"); };
                PrivacyNativeBackend.TaskReadForTest = delegate(string target) { discoveries++; throw new InvalidOperationException("Retired task discovery forbidden"); };
                Check(!native.Inspect(action.Key, "").Supported && PrivacyControl.Refresh(action.Key, ""), "Retired inspection was not safely represented");
                Check(!PrivacyControl.Set(action.Key, true, "", null) && !native.Write(action.Key, "invalid-target", "d:1") && discoveries == 0 && writes.Count == 0,
                    "Retired application reached component discovery or mutation: " + action.Key);
            }
        }
        private static void NativeEntryPoints()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            object[] args = { null, IntPtr.Zero, 0 };
            try
            {
                uint result = (uint)typeof(PrivacyNativeBackend).GetMethod("PrivacyNetGetJoinInformation", flags).Invoke(null, args);
                Check(result == 0 && (int)args[2] >= 1 && (int)args[2] <= 3, "Windows join status could not be read");
            }
            finally
            {
                if ((IntPtr)args[1] != IntPtr.Zero)
                    Check((uint)typeof(PrivacyNativeBackend).GetMethod("PrivacyNetApiBufferFree", flags).Invoke(null, new[] { args[1] }) == 0,
                        "Windows join status buffer could not be released");
            }
            object[] managementArgs = { false, (uint)0, IntPtr.Zero };
            Check((int)typeof(PrivacyNativeBackend).GetMethod("PrivacyIsDeviceRegisteredWithManagement", flags).Invoke(null, managementArgs) == 0,
                "Windows device management registration could not be read");
        }
        private static void GlobalBackground()
        {
            string action = PrivacyActionCatalog.BackgroundGlobal;
            foreach (string edition in new[] { "Core", "CoreSingleLanguage", "Professional", "Enterprise" })
            foreach (string original in new[] { "absent","d:0","d:1","d:2" })
            {
                Reset(); platform.Edition = edition;
                string target = platform.PolicyCapable ? Global : HomeGlobal;
                if (original != "absent") values[target] = original;
                values[Deny] = PrivacyNativeBackend.PackRegistry(new[] { X }, RegistryValueKind.MultiString);
                values[Allow] = PrivacyNativeBackend.PackRegistry(new[] { A }, RegistryValueKind.MultiString);
                values[UserChoice] = "s:" + PrivacyNativeBackend.B64("external policy");
                string deny = values[Deny], allow = values[Allow], choice = values[UserChoice];
                SeedHistory(action);
                int owned = PerformanceActionStore.All().Count;
                Set(action, true);
                Check(PrivacyControl.State(action).Configured && writes.Count == 0, "Existing matching background receipt was unnecessarily rewritten");
                Set(action, false);
                Check(Get(values,target,"absent") == original && PerformanceActionStore.All().Count == 0, "Historical background original lost");
                Check(values[Deny] == deny && values[Allow] == allow && values[UserChoice] == choice, "Restoration changed unrelated per-app exceptions");
                Check(writes.Count == owned, "Settled background value was rewritten");
                if (owned != 0) Check(PrivacyControl.State(action).State == "restore-pending-reboot", "Historical background restore lost reboot tracking");
            }
            Reset(); SeedHistory(action); values[Global] = "d:0"; Set(action,false);
            Check(values[Global] == "d:0" && writes.Count == 0 && PerformanceActionStore.All().Count == 0, "External background setting overwritten");
            foreach (string edition in new[] { "Core","Professional" })
            {
                Reset(); platform.Edition = edition; SeedHistory(action); platform.Edition = edition == "Core" ? "Professional" : "Core";
                Settings.SaveStrDurable(PerformanceActionStore.ListKey, ""); Set(action,false);
                Check(PerformanceActionStore.All().Count == 0, "Edition migration discarded historical background restoration");
            }
        }
        private static void Services()
        { RestoreService(PrivacyActionCatalog.Service,"DiagTrack",Identity,"c:2:"); }
        private static void PcaServices()
        { RestoreService(PrivacyActionCatalog.Compatibility,"PcaSvc",PcaIdentity,"c:3:"); }
        private static void RestoreService(string action, string service, string identity, string startup)
        {
            foreach (bool direct in new[] { false,true })
            foreach (bool loseIndex in new[] { false,true })
            {
                Reset(); if (service == "PcaSvc") platform.Edition = "Core";
                SeedHistory(action); Check(writes.Count == 0, "Historical fixture issued native writes");
                PerformanceActionStore.Receipt runtime = null;
                foreach (var receipt in PerformanceActionStore.All()) if (PrivacyControl.CanonicalTarget(receipt.Target) == "svc|"+service) runtime = receipt;
                Check(runtime != null, "Historical runtime original not recorded");
                if (loseIndex) Settings.SaveStrDurable(PerformanceActionStore.ListKey, "");
                if (direct) Check(PrivacyControl.RestoreReceipt(runtime), "Direct historical service restore failed"); else Set(action,false);
                Check(writes.Count == 2 && writes[0] == service+".Start" && writes[1] == service, "Service recovery ran before startup recovery");
                Check(services[service] == "v:4:"+identity && services[service+".Start"] == startup+identity && PerformanceActionStore.All().Count == 0, "Historical service originals lost");
            }
            Reset(); if (service == "PcaSvc") platform.Edition = "Core";
            SeedHistory(action); string replacement = PrivacyNativeBackend.B64("replacement-service");
            services[service] = "v:1:"+replacement; services[service+".Start"] = "c:4:"+replacement; Set(action,false);
            Check(writes.Count == 0 && PerformanceActionStore.All().Count == 0 && services[service] == "v:1:"+replacement, "Replacement service overwritten during recovery");
            Reset(); if (service == "PcaSvc") platform.Edition = "Core";
            SeedHistory(action); PrivacyNativeBackend.ServiceWriteForTest = delegate(string target,string value) { return false; }; Set(action,false);
            Check(PerformanceActionStore.All().Count == 2 && PrivacyControl.State(action).State == "restore-failed", "Failed service restoration discarded originals");
        }
        private static void ContextOwnership()
        {
            foreach (string action in new[] { PrivacyActionCatalog.Personalized,PrivacyActionCatalog.Input,PrivacyActionCatalog.BackgroundGlobal })
            {
                Reset(); if (action == PrivacyActionCatalog.BackgroundGlobal) platform.Edition = "Core";
                SeedHistory(action); int owned = PerformanceActionStore.All().Count; user = "S-1-5-21-1-2-3-1002";
                Set(action,false); int remaining = PerformanceActionStore.All().Count;
                Check(remaining == (action == PrivacyActionCatalog.Input ? 1 : owned), "Foreign account originals discarded");
                foreach (string written in writes) Check(!written.StartsWith("reg|CU|"), "Another account's registry modified");
                writes.Clear();
                if (action == PrivacyActionCatalog.BackgroundGlobal) Set(action,true);
                else Check(!PrivacyControl.Set(action,true,"",null), "Retired preference was reactivated");
                Check(writes.Count == 0 && PerformanceActionStore.All().Count == remaining, "New application replaced foreign-account debt");
                user = "S-1-5-21-1-2-3-1001"; Set(action,false);
                Check(PerformanceActionStore.All().Count == 0, "Original account could not recover originals");
            }
            foreach (string action in new[] { PrivacyActionCatalog.Diagnostics,PrivacyActionCatalog.Service,PrivacyActionCatalog.BackgroundGlobal,PrivacyActionCatalog.NtfsMemory })
            {
                Reset(); if (action == PrivacyActionCatalog.NtfsMemory) Set(action,true); else SeedHistory(action);
                int owned = PerformanceActionStore.All().Count; Check(owned > 0, "Device originals not captured");
                writes.Clear(); machine = "22222222222222222222222222222222"; Set(action,false);
                Check(writes.Count == 0 && PerformanceActionStore.All().Count == owned, "Foreign device originals modified or discarded");
                if (!PrivacyActionCatalog.IsRetired(action)) Set(action,true); else Check(!PrivacyControl.Set(action,true,"",null), "Retired device apply accepted");
                Check(writes.Count == 0 && PerformanceActionStore.All().Count == owned, "New application replaced foreign-device debt");
                machine = "11111111111111111111111111111111"; Set(action,false);
                Check(PerformanceActionStore.All().Count == 0, "Original machine could not recover originals");
            }
            Reset(); machine = "invalid"; bool refused = false;
            try { native.Inspect(PrivacyActionCatalog.NtfsMemory,""); } catch (InvalidOperationException) { refused = true; }
            Check(refused && writes.Count == 0, "Missing machine identity admitted NTFS settings");
        }
        private static void PolicyAndTasks()
        {
            foreach (Exception missing in new Exception[] { new FileNotFoundException(), new DirectoryNotFoundException(),
                new System.Runtime.InteropServices.COMException("missing", unchecked((int)0x80070002)),
                new System.Reflection.TargetInvocationException(new FileNotFoundException()) })
                Check(PrivacyNativeBackend.MissingTask(missing), "Missing historical task was misclassified as a read fault");
            foreach (Exception failure in new Exception[] { null, new UnauthorizedAccessException(), new InvalidOperationException(),
                new System.Reflection.TargetInvocationException(new UnauthorizedAccessException()) })
                Check(!PrivacyNativeBackend.MissingTask(failure), "Historical task access failure was silently treated as absence");
            Reset(); platform.Managed = true; Set(PrivacyActionCatalog.NtfsMemory,true);
            Check(writes.Count == 0 && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "managed", "Organization policy overridden");
            Reset(); PrivacyPlan plan = native.Inspect(PrivacyActionCatalog.NtfsMemory,""); platform.Managed = true; bool blocked = false;
            try { native.Write(PrivacyActionCatalog.NtfsMemory,plan.Targets[0].Key,"d:2"); } catch (NotSupportedException) { blocked = true; }
            Check(blocked && writes.Count == 0,"Management change after inspection allowed mutation");
            foreach (PrivacyAction action in PrivacyActionCatalog.Retired)
            foreach (string edition in new[] { "Core","Professional","IoTEnterpriseS" })
            {
                if (action.Key == PrivacyActionCatalog.Archives) continue;
                Reset(); platform.Edition = edition; tasks[AppraiserTask] = "t:1:"+TaskHash; tasks[CeipTask] = "t:1:"+TaskHash;
                SeedHistory(action.Key); Set(action.Key,false);
                Check(PerformanceActionStore.All().Count == 0, "Retired action failed to recover on "+edition+": "+action.Key);
                foreach (string value in values.Values) Check(value == "absent", "Retired registry original did not return to absence");
                Check(tasks[AppraiserTask] == "t:1:"+TaskHash && tasks[CeipTask] == "t:1:"+TaskHash, "Historical task originals not restored");
            }
            string xml = "<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Settings><Enabled>false</Enabled></Settings><Actions><Exec><Command>trusted.exe</Command></Exec></Actions></Task>";
            Check(PrivacyNativeBackend.TaskIdentity(xml) == PrivacyNativeBackend.TaskIdentity(xml.Replace("false","true")), "Task enabled flag changed its identity");
            Check(PrivacyNativeBackend.TaskIdentity(xml) != PrivacyNativeBackend.TaskIdentity(xml.Replace("trusted","replacement")), "Task action replacement preserved identity");
            bool rejected = false; try { PrivacyNativeBackend.TaskIdentity("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/test'>]><x>&e;</x>"); } catch { rejected = true; }
            Check(rejected,"Task identity allowed external entities");
            Reset(); tasks[CeipTask] = "t:1:"+TaskHash; SeedHistory(PrivacyActionCatalog.Ceip);
            string external = "t:0:"+PrivacyNativeBackend.B64("replacement-task"); tasks[CeipTask] = external; Set(PrivacyActionCatalog.Ceip,false);
            Check(tasks[CeipTask] == external,"Replacement task modified during historical restoration");
            Reset(); string target = Scope(@"reg|LM|SYSTEM\Bad|Start");
            var forged = new PerformanceActionStore.Receipt { Id=PrivacyActionCatalog.Diagnostics,Target=target,Original="absent",Expected="d:1",Committed=true };
            Check(Settings.SaveStrDurable(PerformanceActionStore.Slot(forged.Id,forged.Target),PerformanceActionStore.Pack(forged)),"Forged fixture persistence failed");
            Check(!PrivacyControl.RestoreReceipt(forged) && writes.Count == 0 && PerformanceActionStore.All().Count == 1,"Historical restore escaped target allowlist");
            Reset(); Set(PrivacyActionCatalog.NtfsMemory,true); EnvironmentEnhancementEligibility.SampleForTest = null; Set(PrivacyActionCatalog.NtfsMemory,false);
            Check(PerformanceActionStore.All().Count == 0,"Low memory prevented NTFS original recovery");
        }
        private static int Main()
        {
            root = Path.Combine(Path.GetTempPath(), "PavisePrivacyNativeFixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Logger.LogPath = ""; Lang.Cur = 0;
            try
            {
                count += PrivacyServiceScopeChecks.Run(); Reset();
                NativeEntryPoints(); GlobalBackground(); Services(); PcaServices(); PolicyAndTasks(); ContextOwnership();
                PlatformMatrix(); PlatformRoundTrips(); PlatformChangeRecovery(); InspectionFailures();
                Console.WriteLine("PASS privacy native planning + real core/recovery integration assertions=" + count
                    + " settings=transient registry/services/tasks=mocked application_started=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
