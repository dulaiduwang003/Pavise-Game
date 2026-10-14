#if PAVISE_SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int layoutAssertions;
        private static void LayoutAssert(bool value, string message)
        { layoutAssertions++; if (!value) throw new InvalidOperationException(message); }
        private static readonly BindingFlags layoutFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static void LayoutSet(object target, string name, object value)
        { target.GetType().GetField(name, layoutFields).SetValue(target, value); }
        private static T LayoutGet<T>(object target, string name)
        { return (T)target.GetType().GetField(name, layoutFields).GetValue(target); }
        private static ulong[] LayoutCores(int count, bool smt)
        {
            var cores = new ulong[count];
            for (int i = 0; i < count; i++) cores[i] = (1UL << i) | (smt ? 1UL << (i + count) : 0);
            return cores;
        }
        private static ulong LayoutUnion(ulong[] cores)
        { ulong all = 0; foreach (ulong core in cores) all |= core; return all; }
        private static ulong LayoutPlan(ulong[] cores, bool hybrid = false, ulong efficiency = 0,
            bool asymmetric = false, bool multiGroup = false, bool agree = true, ulong allocated = 0, ulong favored = 0)
        { return AutomaticCoreLayout.Plan(LayoutUnion(cores), cores, hybrid, efficiency, asymmetric, multiGroup, agree, allocated, favored); }

        private sealed class LayoutProcess
        {
            internal long Creation;
            internal string Name;
            internal uint[] Sets = new uint[0];
            internal ulong Affinity = 255;
            internal bool OpenOk = true, ReadOk = true, AffinityOk = true;
        }
        private sealed class LayoutDevice : SuppressionCore.IAutomaticLayoutPlatform, IDisposable
        {
            internal readonly string Dir = Path.Combine(Path.GetTempPath(), "PaviseLayoutMock-" + Guid.NewGuid().ToString("N"));
            internal readonly Dictionary<int, LayoutProcess> Processes = new Dictionary<int, LayoutProcess>();
            internal readonly SuppressionCore Core;
            internal readonly List<string> Receipts = new List<string>();
            internal int Writes, Opens, Closes;
            internal bool WriteOk = true, ApplyDespiteFailure;
            internal Action<uint[]> AfterWrite;
            internal string Journal { get { return Path.Combine(Dir, "state"); } }
            internal LayoutDevice()
            {
                Directory.CreateDirectory(Dir);
                Core = new SuppressionCore(delegate { throw new InvalidOperationException("Unexpected process recovery"); }, true);
                LayoutSet(Core, "allMask", 255UL); LayoutSet(Core, "journalPath", Journal);
                SuppressionCore.AutomaticLayoutPlatformForTest = this;
            }
            internal object Add(int pid)
            {
                var p = new LayoutProcess { Creation = 100000L + pid, Name = "layout-fixture-" + pid };
                Processes.Add(pid, p);
                Type entryType = typeof(SuppressionCore).GetNestedType("Entry", BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(entryType, true);
                LayoutSet(entry, "Name", p.Name); LayoutSet(entry, "Creation", p.Creation);
                LayoutSet(entry, "OrigPri", 32U); LayoutSet(entry, "OrigAff", 255UL);
                LayoutSet(entry, "OrigCpuSets", new uint[0]); LayoutSet(entry, "Applied", true);
                LayoutSet(entry, "Reasons", SuppressReason.Background);
                LayoutGet<IDictionary>(Core, "map").Add(pid, entry);
                return entry;
            }
            internal void Sync(HashSet<int> protectedPids = null, int foreground = 0, int renderer = 0,
                Func<bool> admitted = null, ulong mask = 192, uint[] ids = null)
            { Core.SyncAutomaticLayout(mask, ids ?? new uint[] { 106, 107 }, protectedPids ?? new HashSet<int>(), foreground, renderer, admitted ?? delegate { return true; }); }
            public IntPtr Open(int pid, long creation, string name)
            {
                Opens++; LayoutProcess p;
                return Processes.TryGetValue(pid, out p) && p.OpenOk && p.Creation == creation && p.Name == name
                    ? new IntPtr(pid) : IntPtr.Zero;
            }
            public uint[] ReadSets(IntPtr h)
            { var p = Processes[h.ToInt32()]; return p.ReadOk ? (uint[])p.Sets.Clone() : null; }
            public bool ReadAffinity(IntPtr h, out ulong mask)
            { var p = Processes[h.ToInt32()]; mask = p.Affinity; return p.AffinityOk; }
            public bool WriteSets(IntPtr h, uint[] sets)
            {
                Writes++; Receipts.Add(File.Exists(Journal) ? File.ReadAllText(Journal) : "");
                if (WriteOk || ApplyDespiteFailure) Processes[h.ToInt32()].Sets = (uint[])sets.Clone();
                if (AfterWrite != null) AfterWrite(sets);
                return WriteOk;
            }
            public void Close(IntPtr h) { Closes++; }
            internal uint[] Desired(int pid, object entry)
            { return (uint[])typeof(SuppressionCore).GetMethod("AutomaticLayoutDesired", layoutFields).Invoke(Core, new object[] { new IntPtr(pid), entry }); }
            public void Dispose()
            {
                SuppressionCore.AutomaticLayoutPlatformForTest = null;
                Core.AutomaticLayoutSaveForTest = null;
                Directory.Delete(Dir, true);
            }
        }
        private static object LayoutParse(string line)
        {
            object[] args = { line, 0 };
            return typeof(SuppressionCore).GetMethod("ParseJournalLine", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
        }
        internal static void RunAutomaticLayoutTests()
        {
            layoutAssertions = 0;
            foreach (bool smt in new[] { false, true })
            {
                foreach (int count in new[] { 4, 6, 8, 12, 16, 24 })
                {
                    ulong[] cores = LayoutCores(count, smt);
                    ulong result = LayoutPlan(cores);
                    int chosen = 0;
                    foreach (ulong core in cores)
                    { LayoutAssert((result & core) == 0 || (result & core) == core, "Split SMT siblings"); if ((result & core) != 0) chosen++; }
                    LayoutAssert(chosen == (count < 8 ? 0 : Math.Min(2, count / 8)), "Uniform CPU budget");
                    LayoutAssert(LayoutPlan(cores, asymmetric: true) == 0, "Asymmetric cache fallback");
                    LayoutAssert(LayoutPlan(cores, multiGroup: true) == 0, "Multi-group fallback");
                    LayoutAssert(LayoutPlan(cores, agree: false) == 0, "Inconsistent topology fallback");
                }
                ulong[] hybrid = LayoutCores(8, smt);
                var mixed = new List<ulong>(hybrid);
                ulong efficiency = 0;
                for (int i = smt ? 16 : 8; i < (smt ? 24 : 16); i++) { ulong bit = 1UL << i; mixed.Add(bit); efficiency |= bit; }
                LayoutAssert(LayoutPlan(mixed.ToArray(), true, efficiency) == efficiency, "Hybrid efficiency selection");
                LayoutAssert((LayoutPlan(mixed.ToArray(), true, efficiency, allocated: 1UL << (smt ? 16 : 8)) & (1UL << (smt ? 16 : 8))) == 0, "Allocated efficiency core");
                ulong performanceAllocated = hybrid[0] | hybrid[1] | hybrid[2] | hybrid[3] | hybrid[4];
                LayoutAssert(LayoutPlan(mixed.ToArray(), true, efficiency, allocated: performanceAllocated) == 0, "Insufficient performance cores");
            }
            ulong[] eight = LayoutCores(8, true);
            LayoutAssert(LayoutPlan(eight, favored: eight[7]) == eight[6], "Favored physical core preserved");
            LayoutAssert(LayoutPlan(eight, allocated: 1UL << 15) == 0, "Allocated SMT sibling excludes whole core");
            LayoutAssert(LayoutPlan(eight, true, 1UL << 15) == 0, "Partial efficiency SMT invalid");
            LayoutAssert(LayoutPlan(eight, true, 1UL << 15, allocated: eight[7]) == 0, "Malformed efficiency map cannot hide in an allocated core");
            LayoutAssert(LayoutPlan(eight, true, 1UL << 32) == 0, "Efficiency map outside topology rejected");
            LayoutAssert(LayoutPlan(new ulong[] { 1, 1 }) == 0, "Overlapping physical cores");
            LayoutAssert(LayoutPlan(new ulong[] { 0, 1 }) == 0, "Zero physical core");
            LayoutAssert(AutomaticCoreLayout.Plan(255, new ulong[] { 1, 2 }, false, 0, false, false, true, 0, 0) == 0, "Incomplete topology");
            var sparse = new ulong[] { 1, 4, 16, 64, 256, 1024, 4096, 1UL << 63 };
            LayoutAssert(LayoutPlan(sparse) == 1UL << 63, "Sparse logical numbering");

            using (var d = new LayoutDevice())
            {
                object e = d.Add(1001); d.Sync();
                LayoutAssert(d.Writes == 1 && d.Core.AutomaticLayoutPending, "Acquire CPU-set lease");
                LayoutAssert(d.Receipts[0].Contains("|layout|106,107|0"), "Durable receipt before native write");
                LayoutAssert(CoreThreadPlacement.Same(d.Desired(1001, e), new uint[] { 106, 107 }), "Patrol honors lease");
                d.Sync(); LayoutAssert(d.Writes == 1, "No repeated placement writes");
                d.Sync(mask: 0); LayoutAssert(d.Writes == 2 && d.Processes[1001].Sets.Length == 0, "Disable restores exact defaults");
                LayoutAssert(!d.Core.AutomaticLayoutPending && !File.ReadAllText(d.Journal).Contains("|layout|"), "Cleared ownership receipt");
                LayoutAssert(d.Opens == d.Closes, "Handle closure");
            }
            using (var d = new LayoutDevice())
            {
                for (int i = 1; i <= 13; i++) d.Add(i);
                var protectedPids = new HashSet<int> { 1, 2 };
                LayoutSet(LayoutGet<IDictionary>(d.Core, "map")[5], "Reasons", SuppressReason.Background | SuppressReason.AntiCheat);
                LayoutSet(LayoutGet<IDictionary>(d.Core, "map")[6], "Applied", false);
                LayoutSet(LayoutGet<IDictionary>(d.Core, "map")[7], "OrigCpuSets", new uint[] { 77 });
                LayoutSet(LayoutGet<IDictionary>(d.Core, "map")[8], "OrigAff", 127UL);
                LayoutSet(LayoutGet<IDictionary>(d.Core, "map")[9], "BackgroundPlacementOwned", true);
                LayoutSet(LayoutGet<IDictionary>(d.Core, "map")[10], "Name", "audiodg");
                LayoutSet(LayoutGet<IDictionary>(d.Core, "map")[11], "Name", "tgp_daemon");
                d.Processes[12].Sets = new uint[] { 200 };
                d.Processes[13].Creation++;
                d.Sync(protectedPids, foreground: 3, renderer: 4);
                LayoutAssert(d.Writes == 0, "Protected, foreground, renderer, anti-cheat, manual, audio and reused identities excluded");
            }
            using (var d = new LayoutDevice())
            {
                d.Add(1); d.Sync(admitted: delegate { return false; });
                d.Sync(ids: new uint[] { 1, 1 }); d.Sync(mask: 1);
                d.Sync(mask: 768);
                LayoutAssert(d.Writes == 0, "Admission and CPU-set mapping validation");
                d.Core.AutomaticLayoutSaveForTest = delegate { return false; }; d.Sync();
                LayoutAssert(d.Writes == 0 && !d.Core.AutomaticLayoutPending, "No write without persisted receipt");
            }
            using (var d = new LayoutDevice())
            {
                d.Add(1); int admissions = 0;
                d.Sync(admitted: delegate { return ++admissions < 3; });
                LayoutAssert(d.Writes == 0 && !d.Core.AutomaticLayoutPending, "Revoked admission rolls back before write");
            }
            using (var d = new LayoutDevice())
            {
                d.Add(1); d.Sync(); d.Sync(new HashSet<int> { 1 });
                LayoutAssert(d.Writes == 2 && !d.Core.AutomaticLayoutPending, "Newly protected process restored");
            }
            foreach (bool changeAffinity in new[] { false, true })
            using (var d = new LayoutDevice())
            {
                object e = d.Add(1); d.Sync();
                if (changeAffinity) d.Processes[1].Affinity = 127;
                else d.Processes[1].Sets = new uint[] { 999 };
                d.Desired(1, e);
                LayoutAssert(LayoutGet<bool>(e, "AutomaticLayoutYielded"), "External placement recognized");
                LayoutAssert(d.Core.RestoreAutomaticLayouts(), "External ownership released");
                d.Sync(); LayoutAssert(d.Writes == 1, "External placement retained and not reacquired");
                LayoutAssert(File.ReadAllText(d.Journal).Contains("|layout||1"), "Yield survives crash recovery");
            }
            using (var d = new LayoutDevice())
            {
                object e = d.Add(1); d.Sync(); d.Processes[1].ReadOk = false;
                LayoutAssert(!d.Core.RestoreAutomaticLayouts() && d.Core.AutomaticLayoutPending, "Unreadable state retains recovery debt");
                d.Desired(1, e); LayoutAssert(LayoutGet<bool>(e, "AutomaticLayoutRestoring"), "Patrol does not reapply during recovery");
                d.Processes[1].ReadOk = true;
                LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Processes[1].Sets.Length == 0, "Recover after read failure");
            }
            using (var d = new LayoutDevice())
            {
                object e = d.Add(1); d.Sync(); LayoutSet(e, "RestoreInFlight", true);
                d.Sync(mask: 0);
                LayoutAssert(!d.Core.RestoreAutomaticLayouts() && d.Writes == 1, "Normal recovery worker has exclusive restoration ownership");
                LayoutSet(e, "RestoreInFlight", false);
                LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Writes == 2, "Restore after normal worker settles");
            }
            using (var d = new LayoutDevice())
            {
                object e = d.Add(1); d.Sync();
                d.Core.AutomaticLayoutSaveForTest = delegate { return false; };
                LayoutAssert(!d.Core.RestoreAutomaticLayouts() && d.Core.AutomaticLayoutPending, "Failed receipt clear retains debt");
                LayoutAssert(d.Processes[1].Sets.Length == 0 && LayoutGet<bool>(e, "AutomaticLayoutRestoring"), "Native restore succeeds without reapply");
                d.Core.AutomaticLayoutSaveForTest = null;
                LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Writes == 2, "Receipt clear retry avoids extra native write");
            }
            using (var d = new LayoutDevice { WriteOk = false, ApplyDespiteFailure = true })
            {
                d.Add(1); d.Sync();
                LayoutAssert(d.Processes[1].Sets.Length == 0, "Failed apply with side effect immediately restores originals");
                LayoutAssert(d.Core.AutomaticLayoutPending, "Unverified restoration keeps receipt");
                d.WriteOk = true;
                LayoutAssert(d.Core.RestoreAutomaticLayouts() && !d.Core.AutomaticLayoutPending, "Pending restoration settled");
            }
            using (var d = new LayoutDevice())
            {
                for (int i = 1; i <= 40; i++) d.Add(i);
                d.Sync(); LayoutAssert(d.Writes == 8, "Eight apply attempts per pass");
                d.Sync(); d.Sync(); d.Sync(); d.Sync(); LayoutAssert(d.Writes == 32, "Bounded owner count");
                LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Writes == 64, "All leases restored");
            }
            string legacy = "1|100001|" + Convert.ToBase64String(Encoding.UTF8.GetBytes("layout-fixture-1")) + "|32|255|-1|-1||-1|-1|-1|-1";
            LayoutAssert(LayoutParse(legacy) != null && LayoutParse(legacy + "|bg|127|0") != null, "Legacy recovery formats retained");
            foreach (string suffix in new[] { "|layout||0", "|layout|106,106|0", "|layout|x|0", "|layout|106|2", "|unknown|106|0" })
                LayoutAssert(LayoutParse(legacy + suffix) == null, "Malformed layout rejected: " + suffix);
            object receipt = LayoutParse(legacy + "|layout|106,107|0");
            LayoutAssert(receipt != null && LayoutGet<uint[]>(receipt, "AutomaticLayoutSets").Length == 2, "Layout receipt parsed");
            LayoutAssert(LayoutGet<bool>(LayoutParse(legacy + "|layout||1"), "AutomaticLayoutYielded"), "Released external receipt parsed");
            using (var d = new LayoutDevice())
            {
                d.Add(1); d.Processes[1].Sets = new uint[] { 106, 107 };
                MethodInfo restore = typeof(SuppressionCore).GetMethod("RestoreBackgroundPlacement", BindingFlags.Static | BindingFlags.NonPublic);
                LayoutAssert((bool)restore.Invoke(null, new object[] { new IntPtr(1), receipt }), "Crash receipt restores exact CPU sets");
                LayoutAssert(d.Processes[1].Sets.Length == 0, "Crash restoration values");
                receipt = LayoutParse(legacy + "|layout|106,107|0"); d.Processes[1].Sets = new uint[] { 900 };
                LayoutAssert((bool)restore.Invoke(null, new object[] { new IntPtr(1), receipt }) && d.Writes == 1
                    && LayoutGet<bool>(receipt, "AutomaticLayoutYielded"), "Crash restore preserves external placement");
            }

            string dir = Path.Combine(Path.GetTempPath(), "PaviseLayoutSettings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Settings.Save("GmAutoCores", true);
                var mode = new GameMode(dir, new SuppressionCore(delegate { return SuppressionCore.RestoreResult.Restored; }, true));
                LayoutAssert(!mode.AutomaticCoreLayoutEnabled, "Legacy auto-core key never enables new feature");
                LayoutAssert(mode.TrySetAutomaticCoreLayoutEnabled(true) && mode.AutomaticCoreLayoutEnabled, "Opt-in saved");
                LayoutSet(mode, "enabled", true); LayoutSet(mode, "active", true);
                MethodInfo admitted = typeof(GameMode).GetMethod("AutomaticLayoutAdmitted", layoutFields);
                LayoutAssert((bool)admitted.Invoke(mode, null), "Active game admission");
                LayoutSet(mode, "coreManual", new ManualCorePlan(15, false, "test"));
                LayoutAssert(!(bool)admitted.Invoke(mode, null), "Manual game plan takes priority");
                LayoutSet(mode, "coreManual", null); LayoutSet(mode, "preset", PerformancePreset.Handheld);
                LayoutAssert((bool)admitted.Invoke(mode, null), "Handheld respects background placement opt-in");
                LayoutSet(mode, "preset", PerformancePreset.Standard); LayoutSet(mode, "panicReq", true);
                LayoutAssert(!(bool)admitted.Invoke(mode, null), "Panic cancels admission");
                LayoutSet(mode, "panicReq", false); LayoutSet(mode, "stopping", true);
                LayoutAssert(!mode.TrySetAutomaticCoreLayoutEnabled(false), "Stopping cancels preference write");
                LayoutSet(mode, "stopping", false); Settings.SuspendWritesForReset();
                LayoutAssert(!mode.TrySetAutomaticCoreLayoutEnabled(false) && mode.AutomaticCoreLayoutEnabled, "Failed preference save preserves state");
            }
            finally { Settings.UseTransientStoreForCurrentProcess(); Directory.Delete(dir, true); }
        }
#if PAVISE_LAYOUT_TEST_RUNNER
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("The layout regression runner cannot start the app"); }
        private static int Main()
        {
            Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
            try
            {
                RunAutomaticLayoutTests();
                Console.WriteLine("PASS automatic layout: " + layoutAssertions + " assertions; mocked processes, topology, recovery and settings");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
#endif
    }
}
#endif
