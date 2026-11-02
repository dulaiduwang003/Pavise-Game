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
                object entry = Activator.CreateInstance(typeof(SuppressionCore).GetNestedType("Entry", BindingFlags.NonPublic), true);
                LayoutSet(entry, "Name", p.Name); LayoutSet(entry, "Creation", p.Creation);
                LayoutSet(entry, "OrigPri", 32U); LayoutSet(entry, "OrigAff", 255UL);
                LayoutSet(entry, "OrigCpuSets", new uint[0]); LayoutSet(entry, "Applied", true);
                LayoutSet(entry, "Reasons", SuppressReason.Background);
                LayoutGet<IDictionary>(Core, "map").Add(pid, entry);
                return entry;
            }
            internal object Seed(int pid)
            {
                object entry = Add(pid);
                LayoutSet(entry, "AutomaticLayoutSets", new uint[] { 106, 107 });
                Processes[pid].Sets = new uint[] { 106, 107 };
                LayoutAssert((bool)typeof(SuppressionCore).GetMethod("SaveLayoutLocked", layoutFields).Invoke(Core, null),
                    "Could not seed an old owned layout journal");
                LayoutAssert(File.ReadAllText(Journal).Contains("|layout|106,107|0"), "Legacy ownership receipt missing");
                return entry;
            }
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
            LayoutAssert(typeof(GameMode).Assembly.GetType("PaviseApp.AutomaticCoreLayout") == null, "Retired planner still compiled");
            LayoutAssert(typeof(GameMode).GetProperty("AutomaticCoreLayoutEnabled", layoutFields) == null, "Retired public preference still offered");
            LayoutAssert(typeof(GameMode).GetMethod("TrySetAutomaticCoreLayoutEnabled", layoutFields) == null, "Retired preference setter still offered");
            LayoutAssert(typeof(GameMode).GetMethod("AutomaticLayoutAdmitted", layoutFields) == null, "Retired automatic admission remains");
            LayoutAssert(typeof(SuppressionCore).GetMethod("SyncAutomaticLayout", layoutFields) == null, "Retired layout can still acquire targets");
            LayoutAssert(typeof(SuppressionCore).GetMethod("AutomaticLayoutDesired", layoutFields) == null, "Old target sets can still be reapplied");
            using (var d = new LayoutDevice())
            {
                d.Add(1);
                LayoutAssert(d.Core.RestoreAutomaticLayouts(), "No legacy layout should be an immediate no-op");
                LayoutAssert(d.Opens == 0 && d.Writes == 0 && !d.Core.AutomaticLayoutPending, "Unowned background target was queried or placed");
            }
            using (var d = new LayoutDevice())
            {
                d.Seed(1001);
                LayoutAssert(d.Core.AutomaticLayoutPending && d.Writes == 0, "Old receipt not retained before restoration");
                LayoutAssert(d.Core.RestoreAutomaticLayouts(), "Owned old layout did not restore");
                LayoutAssert(d.Writes == 1 && d.Processes[1001].Sets.Length == 0, "Original CPU sets not restored exactly");
                LayoutAssert(d.Receipts[0].Contains("|layout|106,107|0"), "Receipt disappeared before restore write");
                LayoutAssert(!d.Core.AutomaticLayoutPending && !File.ReadAllText(d.Journal).Contains("|layout|"), "Verified restoration did not clear ownership");
                LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Writes == 1, "Settled retirement rewrote CPU sets");
                LayoutAssert(d.Opens == d.Closes, "Restoration leaked handles");
            }
            foreach (bool changeAffinity in new[] { false, true })
                using (var d = new LayoutDevice())
                {
                    object entry = d.Seed(1);
                    if (changeAffinity) d.Processes[1].Affinity = 127; else d.Processes[1].Sets = new uint[] { 999 };
                    LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Writes == 0, "Retirement overwrote external placement");
                    LayoutAssert(LayoutGet<bool>(entry, "AutomaticLayoutYielded"), "External placement ownership not released");
                    LayoutAssert(File.ReadAllText(d.Journal).Contains("|layout||1"), "External ownership release not durable");
                    LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Writes == 0, "Released placement was reacquired");
                }
            foreach (string unavailable in new[] { "open", "sets", "affinity", "identity" })
                using (var d = new LayoutDevice())
                {
                    d.Seed(1); string before = File.ReadAllText(d.Journal);
                    if (unavailable == "open") d.Processes[1].OpenOk = false;
                    if (unavailable == "sets") d.Processes[1].ReadOk = false;
                    if (unavailable == "affinity") d.Processes[1].AffinityOk = false;
                    if (unavailable == "identity") d.Processes[1].Creation++;
                    LayoutAssert(!d.Core.RestoreAutomaticLayouts() && d.Core.AutomaticLayoutPending && d.Writes == 0,
                        "Unavailable or mismatched identity authorized restoration: " + unavailable);
                    LayoutAssert(File.ReadAllText(d.Journal) == before, "Failed read lost old ownership");
                    d.Processes[1].OpenOk = d.Processes[1].ReadOk = d.Processes[1].AffinityOk = true;
                    d.Processes[1].Creation = 100001;
                    LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Processes[1].Sets.Length == 0, "Read failure could not recover");
                }
            using (var d = new LayoutDevice())
            {
                object entry = d.Seed(1); LayoutSet(entry, "RestoreInFlight", true);
                LayoutAssert(!d.Core.RestoreAutomaticLayouts() && d.Writes == 0, "Concurrent restore owner was bypassed");
                LayoutSet(entry, "RestoreInFlight", false);
                LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Writes == 1, "Settled normal restore blocked retirement");
            }
            foreach (bool partial in new[] { false, true })
                using (var d = new LayoutDevice { WriteOk = false, ApplyDespiteFailure = partial })
                {
                    d.Seed(1);
                    LayoutAssert(!d.Core.RestoreAutomaticLayouts() && d.Core.AutomaticLayoutPending, "Failed restore lost recovery debt");
                    LayoutAssert(File.ReadAllText(d.Journal).Contains("|layout|106,107|0"), "Failed restore cleared old receipt");
                    d.WriteOk = true;
                    LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Processes[1].Sets.Length == 0, "Partial restore could not settle");
                    LayoutAssert(d.Writes == (partial ? 1 : 2), "Partial restore was unnecessarily repeated");
                }
            using (var d = new LayoutDevice())
            {
                object entry = d.Seed(1); d.Core.AutomaticLayoutSaveForTest = delegate { return false; };
                LayoutAssert(!d.Core.RestoreAutomaticLayouts() && d.Core.AutomaticLayoutPending && d.Writes == 1, "Failed journal clear discarded debt");
                LayoutAssert(d.Processes[1].Sets.Length == 0 && LayoutGet<bool>(entry, "AutomaticLayoutRestoring"), "Native restore incorrectly re-applied old layout");
                d.Core.AutomaticLayoutSaveForTest = null;
                LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Writes == 1, "Journal-clear retry duplicated a native restore");
            }
            using (var d = new LayoutDevice())
            {
                d.Seed(1); d.AfterWrite = delegate { d.Processes[1].ReadOk = false; };
                LayoutAssert(!d.Core.RestoreAutomaticLayouts() && d.Core.AutomaticLayoutPending, "Unreadable restore verification cleared debt");
                d.AfterWrite = null; d.Processes[1].ReadOk = true;
                LayoutAssert(d.Core.RestoreAutomaticLayouts() && d.Writes == 1, "Verification retry re-applied old layout");
            }
            string legacy = "1|100001|" + Convert.ToBase64String(Encoding.UTF8.GetBytes("layout-fixture-1")) + "|32|255|-1|-1||-1|-1|-1|-1";
            LayoutAssert(LayoutParse(legacy) != null && LayoutParse(legacy + "|bg|127|0") != null, "Other legacy recovery formats lost");
            foreach (string suffix in new[] { "|layout||0", "|layout|106,106|0", "|layout|x|0", "|layout|106|2", "|unknown|106|0" })
                LayoutAssert(LayoutParse(legacy + suffix) == null, "Malformed old layout accepted: " + suffix);
            object receipt = LayoutParse(legacy + "|layout|106,107|0");
            LayoutAssert(receipt != null && LayoutGet<uint[]>(receipt, "AutomaticLayoutSets").Length == 2, "Old receipt not parsed");
            LayoutAssert(LayoutGet<bool>(LayoutParse(legacy + "|layout||1"), "AutomaticLayoutYielded"), "External release receipt not parsed");
            using (var d = new LayoutDevice())
            {
                d.Add(1); d.Processes[1].Sets = new uint[] { 106, 107 };
                MethodInfo restore = typeof(SuppressionCore).GetMethod("RestoreBackgroundPlacement", BindingFlags.Static | BindingFlags.NonPublic);
                LayoutAssert((bool)restore.Invoke(null, new object[] { new IntPtr(1), receipt }), "Crash old receipt did not restore CPU sets");
                LayoutAssert(d.Processes[1].Sets.Length == 0 && d.Writes == 1, "Crash restored values incorrect");
                receipt = LayoutParse(legacy + "|layout|106,107|0"); d.Processes[1].Sets = new uint[] { 900 };
                LayoutAssert((bool)restore.Invoke(null, new object[] { new IntPtr(1), receipt }) && d.Writes == 1
                    && LayoutGet<bool>(receipt, "AutomaticLayoutYielded"), "Crash restore overwrote external CPU sets");
            }
            string dir = Path.Combine(Path.GetTempPath(), "PaviseLayoutSettings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                foreach (bool automatic in new[] { false, true })
                {
                    Settings.UseTransientStoreForCurrentProcess(); Settings.Save(AutomaticPolicy.VersionKey, automatic);
                    Settings.Save("GmAutoCores", true); Settings.Save("AutoBackgroundCoreLayoutV1", true);
                    var mode = new GameMode(dir, new SuppressionCore(delegate { return SuppressionCore.RestoreResult.Restored; }, true));
                    string old;
                    LayoutAssert(Settings.TryLoadStr("AutoBackgroundCoreLayoutV1", out old) && old == "", "Old opt-in preference survived migration");
                    foreach (PerformancePreset preset in new[] { PerformancePreset.Standard, PerformancePreset.Competitive, PerformancePreset.Extreme, PerformancePreset.Handheld })
                    {
                        mode.Preset = preset;
                        LayoutAssert(typeof(GameMode).GetMethod("SyncAutomaticBackgroundLayout", layoutFields) == null, "A mode revived automatic placement");
                    }
                }
            }
            finally { Settings.UseTransientStoreForCurrentProcess(); Directory.Delete(dir, true); }
        }
#if PAVISE_LAYOUT_TEST_RUNNER
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("The layout regression runner cannot start the app"); }
        private static int Main()
        {
            Settings.UseTransientStoreForCurrentProcess(); Lang.Init(); Logger.WritesEnabled = false;
            try
            {
                RunAutomaticLayoutTests();
                Console.WriteLine("PASS retired automatic layout: " + layoutAssertions + " assertions; mocked legacy recovery; no new placement or runtime");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
#endif
    }
}
#endif