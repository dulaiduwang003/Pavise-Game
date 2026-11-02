#if PAVISE_AC_ISOLATION_STUBS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    // Compile only with SuppressionCore, its three enums, and SuppressionAffinityPolicy.
    // Every native write below changes an in-memory fake; the product Native class is excluded.
    internal static class AntiCheatPlacementIsolationChecks
    {
        private static int checks;
        private static void Check(bool value, string detail) { checks++; if (!value) throw new Exception(detail); }
        private static readonly Type EntryType = typeof(SuppressionCore).GetNestedType("Entry", BindingFlags.NonPublic);
        private static object Call(object target, string method, params object[] args)
        {
            return typeof(SuppressionCore).GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic).Invoke(target, args);
        }
        private static object Entry(SuppressionCore core)
        {
            return ((IDictionary)typeof(SuppressionCore).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(core))[7];
        }
        private static void Set(object entry, string field, object value) { EntryType.GetField(field).SetValue(entry, value); }
        private static T Get<T>(object entry, string field) { return (T)EntryType.GetField(field).GetValue(entry); }
        private static string LegacyLine(string suffix)
        {
            return "7|123|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ac.exe")) + "|32|255|2|5||-1|-1|-1|0" + suffix;
        }
        private static SuppressionCore Acquired(string path)
        {
            Native.Reset();
            var core = new SuppressionCore(path);
            Check(core.Acquire(7, "ac.exe", SuppressReason.AntiCheat, "ACE", SuppressionLevel.Isolated, 123) != AcquireResult.ApplyFailed, "AC acquire failed");
            Check(Native.PlacementWrites == 0, "AC acquire wrote placement");
            Check(core.PrepareAntiCheatAffinity(7, 123, "ac.exe"), "new AC handoff failed");
            Check(Get<bool>(Entry(core), "AntiCheatPlacementExternal"), "new AC marker missing");
            return core;
        }
        public static int Main(string[] args)
        {
            string folder = args.Length == 0 ? Path.Combine(Path.GetTempPath(), "PaviseAcIsolation-" + Guid.NewGuid().ToString("N")) : args[0];
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "new.state");
                var core = Acquired(path);
                Check(File.ReadAllText(path).Contains("|ac-placement|1|0"), "handoff not persisted");
                Check(!core.PrepareAntiCheatAffinity(7, 124, "ac.exe"), "PID reuse passed prepare");
                Check(!core.PrepareAntiCheatAffinity(7, 123, "other.exe"), "name mismatch passed prepare");
                Check(!core.PrepareAntiCheatAffinity(8, 123, "ac.exe"), "untracked PID passed prepare");

                // Stand in for the independent lease and a later AC-owned CPU Sets change.
                Native.Affinity = 3; Native.Sets = new uint[] { 99 }; Native.PlacementWrites = 0;
                Check((bool)Call(core, "ApplyEntryValues", new IntPtr(7), Entry(core)), "AC reconcile failed");
                Check((bool)Call(core, "ThrottleMatches", new IntPtr(7), SuppressionLevel.Isolated, 32u, 255UL, new uint[0], -1, true, 255UL, false, 0), "AC match inspected lease placement");
                Check(Native.PlacementWrites == 0 && Native.Affinity == 3 && Native.Sets[0] == 99, "AC reconcile overrode external placement");

                core.Acquire(7, "ac.exe", SuppressReason.Background, null, SuppressionLevel.Restrained, 123);
                bool changed;
                Check(!core.SetSqueeze(7, 123, "ac.exe", 12, SuppressReason.Background, out changed), "shared background reason took placement");
                core.ReleaseReason(SuppressReason.AntiCheat);
                Check(Get<bool>(Entry(core), "AntiCheatPlacementExternal"), "reason removal lost durable marker");
                Check((bool)Call(core, "ApplyEntryValues", new IntPtr(7), Entry(core)), "shared background reconcile failed");
                Check(Native.PlacementWrites == 0 && Native.Affinity == 3, "background reapply restored old AC affinity");
                core.ReleaseReason(SuppressReason.Background);
                Check(Native.PlacementWrites == 0 && Native.Affinity == 3 && Native.Sets[0] == 99, "release restored lease placement");
                Check(Native.Priority == 32 && Native.Io == 2 && Native.Page == 5, "non-placement restoration regressed");

                // Crash recovery must independently restore suppression fields without undoing a lease.
                path = Path.Combine(folder, "recovery.state"); core = Acquired(path);
                Native.Affinity = 15; Native.Sets = new uint[] { 88 }; Native.PlacementWrites = 0;
                Check(SuppressionCore.HealFromCrash(path) == 1, "marked recovery failed");
                Check(Native.PlacementWrites == 0 && Native.Affinity == 15 && Native.Sets[0] == 88, "marked recovery touched lease placement");
                Check(Native.Priority == 32 && Native.Io == 2, "marked recovery skipped suppression restoration");

                // Old records remain recovery debt; recognition must not silently adopt them.
                foreach (string suffix in new string[] { "", "|bg|3|0", "|layout|42|0" })
                {
                    Native.Reset(); path = Path.Combine(folder, "legacy" + suffix.Length + ".state");
                    File.WriteAllLines(path, new string[] { "PAVISE_SUPPRESSION_V1", LegacyLine(suffix) });
                    core = new SuppressionCore(path);
                    object entry = Entry(core);
                    Check(Get<bool>(entry, "LegacyPlacementDebt") && !Get<bool>(entry, "AntiCheatPlacementExternal"), "legacy debt treated as transferred");
                    Set(entry, "Reasons", SuppressReason.AntiCheat);
                    Check(!core.PrepareAntiCheatAffinity(7, 123, "ac.exe"), "legacy debt silently discarded");
                    Check(Native.PlacementWrites == 0, "failed legacy prepare wrote placement");
                }
                Native.Reset(); path = Path.Combine(folder, "oldrecovery.state");
                File.WriteAllLines(path, new string[] { "PAVISE_SUPPRESSION_V1", LegacyLine("") });
                Native.Affinity = 3; Native.Sets = new uint[] { 42 };
                Check(SuppressionCore.HealFromCrash(path) == 1, "legacy recovery failed");
                Check(Native.Affinity == 255 && Native.Sets.Length == 0 && Native.PlacementWrites > 0, "legacy placement debt was ignored");

                // Ordinary background entries retain their original placement behavior.
                Native.Reset(); core = new SuppressionCore();
                core.Acquire(7, "ac.exe", SuppressReason.Background, null, SuppressionLevel.Restrained, 123);
                Check(!Get<bool>(Entry(core), "AntiCheatPlacementExternal"), "ordinary background entry marked AC");
                Native.Affinity = 3; Native.Sets = new uint[] { 42 }; Native.PlacementWrites = 0;
                Check((bool)Call(core, "ApplyEntryValues", new IntPtr(7), Entry(core)), "background reconcile failed");
                Check(Native.Affinity == 255 && Native.Sets.Length == 0 && Native.PlacementWrites > 0, "ordinary background placement behavior changed");

                // A prior background owned mask is restored before handing off, then kept separate.
                Native.Reset(); core = new SuppressionCore();
                core.Acquire(7, "ac.exe", SuppressReason.Background, null, SuppressionLevel.Restrained, 123);
                Check(core.SetSqueeze(7, 123, "ac.exe", 3, SuppressReason.Background, out changed), "background squeeze setup failed");
                Check(Native.Affinity == 3, "background squeeze not applied");
                core.Acquire(7, "ac.exe", SuppressReason.AntiCheat, "ACE", SuppressionLevel.Isolated, 123);
                Check(core.PrepareAntiCheatAffinity(7, 123, "ac.exe"), "background-to-AC transfer failed");
                Check(Native.Affinity == 255 && !Get<bool>(Entry(core), "BackgroundPlacementOwned"), "prior background debt not settled before transfer");
                Native.Affinity = 12; Native.PlacementWrites = 0;
                core.ReleaseReason(SuppressReason.AntiCheat); core.ReleaseReason(SuppressReason.Background);
                Check(Native.Affinity == 12 && Native.PlacementWrites == 0, "transferred background debt overwrote lease");

                // Recovery/neutralization and give-up paths share the same placement exemption.
                core = Acquired(Path.Combine(folder, "giveup.state"));
                Native.Affinity = 12; Native.Sets = new uint[] { 77 }; Native.PlacementWrites = 0;
                Check((bool)Call(core, "GiveUpAntiCheatLocked", new IntPtr(7), 7, Entry(core)), "give-up path failed");
                Check(Native.PlacementWrites == 0 && Native.Affinity == 12 && Native.Sets[0] == 77, "give-up path overwrote lease");
                core = Acquired(Path.Combine(folder, "blocked.state"));
                Native.Priority = 32; Native.Io = 2; Native.Page = 5;
                Native.Affinity = 12; Native.Sets = new uint[] { 77 }; Native.PlacementWrites = 0;
                typeof(SuppressionCore).GetField("lastApplyError", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, "priority-write,io-write,page-write");
                Check((bool)Call(core, "TryNeutralizeUnwritableLocked", new IntPtr(7), 7, Entry(core)), "neutralization treated external placement as suppression debt");
                Check(Native.PlacementWrites == 0 && Native.Affinity == 12 && Native.Sets[0] == 77, "neutralization overwrote lease");

                // A failed durable receipt never authorizes the hard-affinity lease.
                Native.Reset(); string badPath = Path.Combine(folder, "directory-is-not-a-receipt"); Directory.CreateDirectory(badPath);
                core = new SuppressionCore(badPath);
                Check(core.Acquire(7, "ac.exe", SuppressReason.AntiCheat, "ACE", SuppressionLevel.Isolated, 123) == AcquireResult.ApplyFailed, "failed journal incorrectly applied");
                Check(!core.PrepareAntiCheatAffinity(7, 123, "ac.exe"), "failed journal authorized lease");
                Check(Native.PlacementWrites == 0, "failed durable handoff wrote placement");

                object invalid = Call(null, "ParseJournalLine", LegacyLine("|ac-placement|2|0"), 0);
                Check(invalid == null, "unknown AC journal schema accepted");
                for (int reasons = 0; reasons < 4; reasons++)
                {
                    object gateEntry = Activator.CreateInstance(EntryType, true);
                    Set(gateEntry, "Reasons", (SuppressReason)reasons);
                    Set(gateEntry, "AntiCheatPlacementExternal", true);
                    Check(!(bool)Call(null, "RestorePlacement", gateEntry), "reason transition re-enabled placement restoration");
                    Check((bool)Call(null, "PlacementIsProtected", gateEntry), "reason transition re-enabled placement reconciliation");
                    Check(!(bool)Call(null, "SqueezeOwnedBy", gateEntry, SuppressReason.Background), "reason transition re-enabled background placement ownership");
                }
                Console.WriteLine("PASS anti-cheat placement isolation assertions=" + checks + " native=memory product_main=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }

    internal static class Native
    {
        internal static ulong Affinity;
        internal static uint[] Sets;
        internal static uint Priority;
        internal static int Io, Page, Boost, PlacementWrites;
        internal static bool PowerThrottlingSupported { get { return false; } }
        internal static void Reset() { Affinity = 255; Sets = new uint[0]; Priority = 32; Io = 2; Page = 5; Boost = 0; PlacementWrites = 0; }
        internal static IntPtr OpenProcess(uint access, bool inherit, int pid) { return new IntPtr(pid); }
        internal static void CloseHandle(IntPtr h) { }
        internal static string ImageName(IntPtr h) { return "ac.exe"; }
        internal static string ImagePath(IntPtr h) { return "C:\\test\\ac.exe"; }
        internal static bool QueryProcessSample(IntPtr h, out long creation, out long cpu, out ulong io) { creation = 123; cpu = 1; io = 0; return true; }
        internal static uint GetPriorityClass(IntPtr h) { return Priority; }
        internal static bool SetPriorityClass(IntPtr h, uint value) { Priority = value; return true; }
        internal static ulong QueryAffinity(IntPtr h) { return Affinity; }
        internal static bool TryQueryAffinity(IntPtr h, out ulong value) { value = Affinity; return true; }
        internal static bool SetProcessAffinityMask(IntPtr h, UIntPtr value) { PlacementWrites++; Affinity = value.ToUInt64(); return true; }
        internal static uint[] QueryCpuSets(IntPtr h) { return (uint[])Sets.Clone(); }
        internal static bool CpuSetsMatch(IntPtr h, uint[] value) { return CoreThreadPlacement.Same(Sets, value ?? new uint[0]); }
        internal static bool RestoreCpuSetsVerified(IntPtr h, uint[] value) { PlacementWrites++; Sets = value == null ? new uint[0] : (uint[])value.Clone(); return true; }
        internal static int QueryIoPriority(IntPtr h) { return Io; }
        internal static int QueryPagePriority(IntPtr h) { return Page; }
        internal static bool TrySetIoPriority(IntPtr h, int value) { Io = value; return true; }
        internal static bool TrySetPagePriority(IntPtr h, int value) { Page = value; return true; }
        internal static int QueryBoostDisabled(IntPtr h) { return Boost; }
        internal static bool TrySetBoostDisabled(IntPtr h, bool value) { Boost = value ? 1 : 0; return true; }
        internal static int D3DKMTGetProcessSchedulingPriorityClass(IntPtr h, out int cls) { cls = 2; return -1; }
        internal static int D3DKMTSetProcessSchedulingPriorityClass(IntPtr h, int cls) { return -1; }
        internal static bool TryQueryPowerThrottling(IntPtr h, out int control, out int state) { control = state = 0; return true; }
        internal static bool RestorePowerThrottling(IntPtr h, int control, int state) { return true; }
        internal static bool ApplyEcoQoS(IntPtr h, bool timer) { return true; }
        internal static uint EcoQoSWantMask(bool timer) { return timer ? 5u : 1u; }
        internal static bool StillActive(IntPtr h) { return true; }
        internal static int QueryProcessIdentityState(IntPtr h, long creation) { return creation == 123 ? 0 : 1; }
        internal static bool LastOpenProcessFailureWasNoSuchProcess() { return false; }
    }
    internal static class CpuTopology
    {
        internal static bool MultiGroup;
        internal static ulong AllMask = 255, ThrottleMask = 3, InactiveThrottleMask;
        internal static uint[] BackgroundCpuSetIds() { return new uint[] { 1 }; }
        internal static uint[] BackgroundYieldCpuSetIds() { return new uint[] { 2 }; }
        internal static uint[] InactiveBackgroundCpuSetIds() { return new uint[] { 3 }; }
        internal static ulong BackgroundSqueezeMask() { return 3; }
    }
    internal static class CrashGuard
    {
        internal static bool UncleanThrottleAtLaunch;
        internal static void MarkThrottle(ulong mask) { }
        internal static void ReleaseThrottle(ulong mask) { }
        internal static bool RestoreOwnedAffinity(IntPtr h, ulong original, ulong owned) { return Native.Affinity != owned || Native.SetProcessAffinityMask(h, (UIntPtr)original); }
    }
    internal static class CoreThreadPlacement
    {
        internal static bool Same(uint[] a, uint[] b) { if (a == null || b == null || a.Length != b.Length) return false; return new HashSet<uint>(a).SetEquals(b); }
    }
    internal static class ActionFailureStore
    {
        internal static bool IsRetryPending(string action, string target) { return false; }
        internal static bool TryBeginApply(string action, string target, out string reason) { reason = null; return true; }
        internal static void CancelApply(string action, string target) { }
        internal static void WithdrawRetry(string action, string target) { }
        internal static bool CompleteApply(string action, string target) { return true; }
        internal static void Trip(string action, string target, string detail) { }
    }
    internal static class WeGameRuntimeProtection { internal static bool IsProtectedRuntimeName(string name) { return false; } }
    internal static class PeripheralCatalog { internal static bool IsInputAudioLike(string name) { return false; } }
    internal static class SelfProtectedRoster { internal static bool Mark(string name) { return false; } }
    internal static class HagsTweak { internal static bool SchedulingAllowsGpuDemotionCached() { return false; } }
    internal static class Lang { internal static string T(string key) { return key; } }
    internal static class Logger { internal static void Log(string text) { } internal static void LogFailure(string text, Exception error) { } }
    internal static class ApplyFailureText { internal static string Of(string detail) { return detail; } }
    internal static class AtomicFile { internal static bool WriteLines(string path, string[] lines, string reason) { File.WriteAllLines(path, lines); return true; } }
}
#endif
