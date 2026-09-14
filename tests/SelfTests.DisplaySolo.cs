// File purpose Display path count, topology query and topology write are all injected
// No native display APIs, registry or windows
// Single display in match has been retired, this only regresses the restore path for crash residue, the snapshot is seeded directly by the test
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int displaySoloChecks;

        internal static int RunDisplaySoloRegressionTests()
        {
            Action[] tests =
            {
                DisplaySoloRestoreSwitchesBackAndClears,
                DisplaySoloRestoreAcceptsUnpluggedSecondDisplay,
                DisplaySoloRestoreAcceptsRejectedWriteOnSingleDisplay,
                DisplaySoloRemoteSessionDoesNotSettle,
                DisplaySoloCorruptSnapshotStaysAsDebt,
                DisplaySoloCrashHealRestores
            };
            displaySoloChecks = 0;
            foreach (Action test in tests)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Lang.Cur = 0;
                DisplaySolo.ResetForTest();
                try { test(); }
                finally { DisplaySolo.ResetForTest(); }
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS display-solo assertions=" + displaySoloChecks
                + " native_display=mocked settings=transient windows_shown=false");
            return tests.Length;
        }

        private static void SoloCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Display solo regression: " + message);
            Interlocked.Increment(ref displaySoloChecks);
        }

        // Fake of a machine after an old version crashed mid-match: topology stuck at internal-only, writes really change the topology later queries return
        private sealed class DisplaySoloFake
        {
            internal int Paths = 1;
            internal uint Topology = DisplaySolo.TopologyInternal;
            internal bool QueryFail, SetFail, IgnoreSet, Remote;
            internal readonly List<uint> Sets = new List<uint>();

            internal void Install()
            {
                DisplaySolo.RemoteForTest = delegate { return Remote; };
                DisplaySolo.PathCountForTest = delegate { return Paths; };
                DisplaySolo.TopologyForTest = delegate(out uint topology)
                { topology = Topology; return !QueryFail; };
                DisplaySolo.SetForTest = delegate(uint topology)
                {
                    if (SetFail) return false;
                    Sets.Add(topology);
                    if (!IgnoreSet) Topology = topology;
                    return true;
                };
            }
        }

        // Snapshot seeding = residue left by an old version crashing while single display was active
        private static void SeedCrashSnapshot(uint original)
        {
            Settings.SaveStr(DisplaySolo.SnapKey, original.ToString());
        }

        private static void DisplaySoloRestoreSwitchesBackAndClears()
        {
            var fake = new DisplaySoloFake { Paths = 2 };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(DisplaySolo.Restore(), "restoring a healthy dual desktop must succeed");
            SoloCheck(fake.Topology == DisplaySolo.TopologyExtend && !DisplaySolo.HasResidue(),
                "restore must return to the recorded topology and clear the journal");
            SoloCheck(DisplaySolo.Restore() && fake.Sets.Count == 1,
                "a second restore must be a no-op");
        }

        private static void DisplaySoloRestoreAcceptsUnpluggedSecondDisplay()
        {
            // Secondary display unplugged: switching back to extend cannot verify extend, but the machine is single-display now, the physical world wins
            var fake = new DisplaySoloFake { IgnoreSet = true };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(DisplaySolo.Restore(), "restore on a now-single display must settle");
            SoloCheck(!DisplaySolo.HasResidue(), "a settled restore must clear the journal");
        }

        private static void DisplaySoloRestoreAcceptsRejectedWriteOnSingleDisplay()
        {
            // After unplugging the secondary the API may reject the multi-display topology write outright; same as "write succeeded but unverifiable", settle as single-display
            var fake = new DisplaySoloFake { SetFail = true };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(DisplaySolo.Restore(), "a rejected restore write on a single display must settle");
            SoloCheck(!DisplaySolo.HasResidue(), "the settled restore must clear the journal");
        }

        // Crash residue + catch-up restore at startup over RDP + remote has only one path: must not treat it as single-display and wipe the physical machine's original topology record
        private static void DisplaySoloRemoteSessionDoesNotSettle()
        {
            var fake = new DisplaySoloFake { Remote = true, IgnoreSet = true };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(!DisplaySolo.Restore() && DisplaySolo.HasResidue(),
                "a remote session settled the snapshot against the remote display config");
            // Only back on the physical machine can it settle by the real path count
            fake.Remote = false;
            fake.IgnoreSet = false;
            fake.Paths = 2;
            SoloCheck(DisplaySolo.Restore() && !DisplaySolo.HasResidue(),
                "returning to the console must settle normally");
        }

        private static void DisplaySoloCorruptSnapshotStaysAsDebt()
        {
            var fake = new DisplaySoloFake();
            fake.Install();
            Settings.SaveStr(DisplaySolo.SnapKey, "garbage");
            SoloCheck(!DisplaySolo.Restore(), "an unparsable snapshot must not restore blindly");
            SoloCheck(DisplaySolo.HasResidue() && fake.Sets.Count == 0,
                "an unparsable snapshot must stay recorded and never reach the display");
        }

        private static void DisplaySoloCrashHealRestores()
        {
            var fake = new DisplaySoloFake { Paths = 2 };
            fake.Install();
            SeedCrashSnapshot(DisplaySolo.TopologyExtend);
            SoloCheck(DisplaySolo.HealFromCrash(), "crash healing must settle the leftover switch");
            SoloCheck(fake.Topology == DisplaySolo.TopologyExtend && !DisplaySolo.HasResidue(),
                "crash healing must return to the recorded topology and clear the journal");
        }
    }
}
#endif
