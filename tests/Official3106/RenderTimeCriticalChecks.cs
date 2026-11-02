using System;
using System.Collections.Generic;
using System.Reflection;

namespace PaviseApp
{
    // Runs the production admission/journal/restore methods with in-memory settings
    // and identity-aware doubles. No product entry point, ETW or native setters run.
    internal static class RenderTimeCriticalChecks
    {
        private const int Pid = 505, Present = 101, Worker = 102;
        private const long Birth = 999, PresentBirth = 1001, WorkerBirth = 1002;
        private static int checks;
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

        private sealed class Fake : IFrameThreads, IFrameThreadMemory
        {
            internal int Owner = Pid, Attempts, FailAttempt;
            internal long ProcessBirth = Birth;
            internal bool Unavailable;
            internal Action<int, int> AfterSet;
            internal readonly Dictionary<int, int> Priorities = new Dictionary<int, int> { { Present, 0 }, { Worker, -1 } };
            internal readonly Dictionary<int, long> Births = new Dictionary<int, long> { { Present, PresentBirth }, { Worker, WorkerBirth } };
            internal readonly List<string> Writes = new List<string>();

            public int Read(FrameThreadReceipt identity, out int priority)
            {
                priority = 0;
                if (Unavailable) return -1;
                long actualBirth;
                if (identity.Pid != Owner || identity.ProcessBirth != ProcessBirth ||
                    !Births.TryGetValue(identity.Tid, out actualBirth) || actualBirth != identity.ThreadBirth) return 1;
                priority = Priorities[identity.Tid];
                return 0;
            }

            public bool Set(FrameThreadReceipt identity, int expected, int desired)
            {
                int current;
                if (Read(identity, out current) != 0 || current != expected) return false;
                Attempts++;
                Check(Settings.LoadStr(FrameCoordination.JournalKey, "").Length > 0,
                    "a priority write ran before its recovery journal existed");
                if (Attempts == FailAttempt) return false;
                Priorities[identity.Tid] = desired;
                Writes.Add(identity.Tid + ":" + expected + ">" + desired);
                if (AfterSet != null) AfterSet(identity.Tid, desired);
                return true;
            }

            public bool UnderPressure(int pid, long birth) { return false; }
            public int ReadMemory(FrameThreadReceipt identity, out int priority)
            { int ignored; int state = Read(identity, out ignored); priority = 1; return state; }
            public bool SetMemory(FrameThreadReceipt identity, int expected, int desired)
            { throw new InvalidOperationException("CPU priority changes unexpectedly wrote memory priority"); }
        }

        private static void Check(bool condition, string detail)
        { checks++; if (!condition) throw new InvalidOperationException(detail); }

        private static Fake Reset()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.BeforeRead = null;
            var fake = new Fake();
            typeof(FrameCoordination).GetField("platform", PrivateStatic).SetValue(null, fake);
            typeof(FrameMemoryProtection).GetField("platform", PrivateStatic).SetValue(null, fake);
            return fake;
        }

        private static FrameCoordinationSample Sample()
        {
            return new FrameCoordinationSample { Pid = Pid, Birth = Birth, PresentTid = Present, WorkerTid = Worker,
                Valid = true, Threads = new Dictionary<int, long> { { Present, PresentBirth }, { Worker, WorkerBirth } } };
        }

        private static bool Apply(FrameCoordinationSample sample)
        { return FrameCoordination.Apply(sample, delegate { return true; }); }

        private static bool Healthy()
        { return (bool)typeof(FrameCoordination).GetMethod("LeaseHealthy", PrivateStatic).Invoke(null, null); }

        private static string Row(int tid, long threadBirth, int original)
        { return Pid + "|" + Birth + "|" + tid + "|" + threadBirth + "|" + original; }

        private static string CurrentJournal()
        { return "v2;" + Row(Present, PresentBirth, 0) + "|15;" + Row(Worker, WorkerBirth, -1) + "|1"; }

        private static void AdmissionAndTargets()
        {
            Fake fake = Reset();
            Check(Apply(Sample()), "valid render/worker sample was not applied");
            Check(fake.Priorities[Present] == 15, "the presentation thread did not receive TimeCritical=15");
            Check(fake.Priorities[Worker] == 1, "the dependency thread no longer retains AboveNormal=1");
            Check(fake.Writes.Count == 2 && fake.Writes[0] == "101:0>15" && fake.Writes[1] == "102:-1>1",
                "the priority change affected an unexpected target or value");
            Check(Settings.LoadStr(FrameCoordination.JournalKey, "") == CurrentJournal(),
                "the v2 CPU journal did not preserve separate applied values");
            Check(Healthy(), "a healthy 15/1 lease was rejected");
            Check(FrameCoordination.Restore(), "the new render/worker priorities did not restore");
            Check(fake.Priorities[Present] == 0 && fake.Priorities[Worker] == -1,
                "restoration did not return both original priorities");
            Check(Settings.LoadStr(FrameCoordination.JournalKey, "") == "", "successful restoration retained the CPU journal");

            fake = Reset();
            FrameCoordinationSample same = Sample(); same.WorkerTid = Present;
            Check(Apply(same), "a render thread that is also the worker was rejected");
            Check(fake.Writes.Count == 1 && fake.Priorities[Present] == 15 && fake.Priorities[Worker] == -1,
                "the same TID was written twice or demoted to the worker target");
            Check(FrameCoordination.Restore() && fake.Priorities[Present] == 0, "the deduplicated render target did not restore");

            foreach (int gamePriority in new[] { 1, 2, 15 })
            {
                fake = Reset(); fake.Priorities[Present] = gamePriority;
                Check(Apply(Sample()), "an independently eligible worker was skipped");
                Check(fake.Priorities[Present] == gamePriority && fake.Priorities[Worker] == 1 && fake.Writes.Count == 1,
                    "the game-owned presentation priority was overwritten");
                List<FrameThreadReceipt> parsed;
                Check(FrameCoordination.Parse(Settings.LoadStr(FrameCoordination.JournalKey, ""), out parsed) &&
                    parsed.Count == 1 && parsed[0].Tid == Worker && parsed[0].Applied == 1,
                    "worker-only change was incorrectly recorded as a TimeCritical render change");
                Check(FrameCoordination.Restore() && fake.Priorities[Present] == gamePriority && fake.Priorities[Worker] == -1,
                    "worker-only restoration touched the game-owned render priority");
            }

            fake = Reset(); fake.Priorities[Present] = 2; fake.Priorities[Worker] = 1;
            FrameCoordinationSample unchanged = Sample();
            Check(!Apply(unchanged) && unchanged.NoChangeNeeded && fake.Writes.Count == 0,
                "game-managed priorities were no longer left alone");
            Check(Settings.LoadStr(FrameCoordination.JournalKey, "") == "", "a no-change sample produced recovery debt");
        }

        private static void JournalCompatibility()
        {
            Fake fake = Reset();
            string old = "v1;" + Row(Present, PresentBirth, 0) + ";" + Row(Worker, WorkerBirth, -1);
            List<FrameThreadReceipt> rows;
            Check(FrameCoordination.Parse(old, out rows) && rows.Count == 2 && rows[0].Applied == 1 && rows[1].Applied == 1,
                "the old v1 journal no longer implies AboveNormal=1");
            fake.Priorities[Present] = fake.Priorities[Worker] = 1;
            Settings.SaveStr(FrameCoordination.JournalKey, old);
            Check(Healthy(), "an intact legacy lease was rejected");
            Check(FrameCoordination.Restore() && fake.Priorities[Present] == 0 && fake.Priorities[Worker] == -1,
                "old v1 recovery did not restore priority 1");

            fake = Reset(); fake.Priorities[Present] = 15;
            Settings.SaveStr(FrameCoordination.JournalKey, "v1;" + Row(Present, PresentBirth, 0));
            Check(FrameCoordination.Restore() && fake.Priorities[Present] == 15 && fake.Writes.Count == 0,
                "legacy recovery claimed ownership of an external TimeCritical change");

            fake = Reset(); fake.Priorities[Present] = 15; fake.Priorities[Worker] = 1;
            Settings.SaveStr(FrameCoordination.JournalKey, CurrentJournal());
            Check(Healthy(), "the persisted v2 lease did not validate against its own applied priorities");
            Check(FrameCoordination.Restore() && fake.Priorities[Present] == 0 && fake.Priorities[Worker] == -1,
                "persisted v2 recovery did not restore TimeCritical=15");
            var receipt = new FrameThreadReceipt { Pid = Pid, ProcessBirth = Birth, Tid = Present,
                ThreadBirth = PresentBirth, Original = 3, Applied = 15 };
            Check(receipt.Encode() == Row(Present, PresentBirth, 3),
                "the five-column memory receipt was changed by CPU journal versioning");
        }

        private static void OwnershipAndLease()
        {
            Fake fake = Reset(); Check(Apply(Sample()), "lease fixture did not apply");
            fake.Priorities[Present] = 1;
            Check(!Healthy(), "render lease accepted old AboveNormal=1 in place of applied TimeCritical=15");
            Check(FrameCoordination.Restore() && fake.Priorities[Present] == 1 && fake.Priorities[Worker] == -1,
                "restore overwrote a later external render priority change");

            fake = Reset(); Check(Apply(Sample()), "worker lease fixture did not apply");
            fake.Priorities[Worker] = 15;
            Check(!Healthy(), "worker lease accepted TimeCritical=15 in place of its applied AboveNormal=1");
            Check(FrameCoordination.Restore() && fake.Priorities[Present] == 0 && fake.Priorities[Worker] == 15,
                "restore overwrote a later external worker priority change");

            foreach (int identityChange in new[] { 0, 1, 2 })
            {
                fake = Reset(); fake.Priorities[Present] = 15;
                Settings.SaveStr(FrameCoordination.JournalKey, "v2;" + Row(Present, PresentBirth, 0) + "|15");
                if (identityChange == 0) fake.Owner++;
                if (identityChange == 1) fake.ProcessBirth++;
                if (identityChange == 2) fake.Births[Present]++;
                Check(!Healthy(), "a changed target identity was accepted by the lease");
                Check(FrameCoordination.Restore() && fake.Priorities[Present] == 15 && fake.Writes.Count == 0,
                    "restoration wrote onto a reused process or thread identity");
            }

            fake = Reset(); Check(Apply(Sample()), "unavailable target fixture did not apply");
            fake.Unavailable = true;
            Check(!FrameCoordination.Restore() && Settings.LoadStr(FrameCoordination.JournalKey, "") != "",
                "an unreadable target was acknowledged as restored");
            fake.Unavailable = false;
            Check(FrameCoordination.Restore(), "recovery did not retry after target readability returned");
        }

        private static void RollbackAndCancellation()
        {
            Fake fake = Reset(); fake.FailAttempt = 2;
            Check(!Apply(Sample()), "a denied worker write was reported as successful");
            Check(fake.Priorities[Present] == 0 && fake.Priorities[Worker] == -1 && fake.Attempts == 3,
                "failure on the second write did not roll back the first TimeCritical write");
            Check(Settings.LoadStr(FrameCoordination.JournalKey, "") == "", "successful rollback retained recovery debt");

            fake = Reset();
            Check(!FrameCoordination.Apply(Sample(), delegate { return false; }) && fake.Writes.Count == 0,
                "a canceled admission changed thread priorities");
            bool alive = true;
            fake.AfterSet = delegate(int tid, int desired) { if (tid == Present && desired == 15) alive = false; };
            Check(!FrameCoordination.Apply(Sample(), delegate { return alive; }), "mid-apply cancellation was ignored");
            Check(fake.Priorities[Present] == 0 && fake.Priorities[Worker] == -1 && fake.Writes.Count == 2,
                "mid-apply cancellation did not restore the already changed render thread");
            Check(Settings.LoadStr(FrameCoordination.JournalKey, "") == "", "cancellation left a settled recovery journal");
        }

        private static void RejectMalformedJournals()
        {
            string p = Row(Present, PresentBirth, 0), w = Row(Worker, WorkerBirth, -1);
            foreach (string invalid in new[] { "corrupt", "v3;" + p + "|15", "v2;" + p, "v1;" + p + "|15",
                "v2;" + p + "|0", "v2;" + p + "|2", "v2;" + p + "|16", "v2;" + p + "|31",
                "v2;" + p + "|x", "v2;" + p + "|15;" + p + "|1", "v2;0|999|101|1001|0|15",
                "v2;505|0|101|1001|0|15", "v2;505|999|0|1001|0|15", "v2;505|999|101|0|0|15",
                "v2;" + Row(Present, PresentBirth, 1) + "|15", "v2;" + Row(Present, PresentBirth, -3) + "|15",
                "v2;" + p + "|15;506|999|102|1002|-1|1", "v2;" + p + "|15;505|998|102|1002|-1|1",
                "v2;" + p + "|15;" + w + "|1;505|999|103|1003|0|1" })
            {
                Fake fake = Reset(); fake.Priorities[Present] = 15;
                List<FrameThreadReceipt> rows;
                Check(!FrameCoordination.Parse(invalid, out rows), "malformed journal was accepted: " + invalid);
                Settings.SaveStr(FrameCoordination.JournalKey, invalid);
                Check(!FrameCoordination.Restore() && fake.Writes.Count == 0,
                    "a malformed journal caused a thread write or was acknowledged");
                Check(Settings.LoadStr(FrameCoordination.JournalKey, "") == invalid,
                    "unrecoverable malformed evidence was silently discarded");
            }
        }

        private static int Main()
        {
            object originalThreads = typeof(FrameCoordination).GetField("platform", PrivateStatic).GetValue(null);
            object originalMemory = typeof(FrameMemoryProtection).GetField("platform", PrivateStatic).GetValue(null);
            try
            {
                AdmissionAndTargets(); JournalCompatibility(); OwnershipAndLease(); RollbackAndCancellation(); RejectMalformedJournals();
                Console.WriteLine("PASS render TimeCritical checks=" + checks + "; native writes=0; ETW=0; product startup=0");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
            finally
            {
                Settings.UseTransientStoreForCurrentProcess();
                typeof(FrameCoordination).GetField("platform", PrivateStatic).SetValue(null, originalThreads);
                typeof(FrameMemoryProtection).GetField("platform", PrivateStatic).SetValue(null, originalMemory);
            }
        }
    }
}
