// @author bdth 2074055628@qq.com
// 文件用途 后台压制 崩溃恢复和竞争性能自测

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestSuppressionReapply(string root)
        {
            string beat = Path.Combine(root, "reapply.beat");
            using (Process probe = StartProbe(beat))
            {
                var core = new SuppressionCore(Path.Combine(root, "reapply.state"));
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    core.Acquire(probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Isolated);
                    probe.Refresh();
                    Eq(ProcessPriorityClass.Idle, probe.PriorityClass);
                    long stableWrites = core.ApplyOperations;
                    if (!core.Reconcile(probe.Id, probe.ProcessName, SuppressReason.Background, true))
                        throw new Exception("tracked suppression was not reconciled");
                    Eq(stableWrites, core.ApplyOperations);
                    probe.PriorityClass = ProcessPriorityClass.Normal;
                    probe.Refresh();
                    Eq(ProcessPriorityClass.Normal, probe.PriorityClass);
                    if (!core.Reconcile(probe.Id, probe.ProcessName, SuppressReason.Background, true))
                        throw new Exception("drifted suppression was not reconciled");
                    Eq(stableWrites + 1, core.ApplyOperations);
                    probe.Refresh();
                    Eq(ProcessPriorityClass.Idle, probe.PriorityClass);
                }
                finally
                {
                    core.ReleaseReason(SuppressReason.Background);
                    StopOwned(probe);
                }
            }
        }

        private static void TestSuppressionCrashRecovery(string root)
        {
            string beat = Path.Combine(root, "suppression-crash.beat");
            string state = Path.Combine(root, "suppression-crash.state");
            using (Process probe = StartProbe(beat))
            {
                var core = new SuppressionCore(state);
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    probe.Refresh();
                    ProcessPriorityClass priority = probe.PriorityClass;
                    IntPtr affinity = probe.ProcessorAffinity;
                    core.Acquire(probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Restrained);
                    probe.Refresh();
                    Eq(ProcessPriorityClass.BelowNormal, probe.PriorityClass);
                    if (!File.Exists(state)) throw new Exception("suppression recovery journal missing");
                    Eq(1, SuppressionCore.HealFromCrash(state));
                    probe.Refresh();
                    Eq(priority, probe.PriorityClass);
                    Eq((long)affinity, (long)probe.ProcessorAffinity);
                    if (File.Exists(state)) throw new Exception("suppression recovery journal remained");
                }
                finally
                {
                    core.ReleaseReason(SuppressReason.Background);
                    StopOwned(probe);
                }
            }
        }

        private static void TestFullyBlockedDetailJudgement()
        {
            if (!SuppressionCore.FullyBlockedDetail(
                    "priority-write,io-write,page-write,priority-readback,io-readback,page-readback"))
                throw new Exception("full write refusal must be recognized");
            if (SuppressionCore.FullyBlockedDetail("eco-readback"))
                throw new Exception("partial failure must not count as fully blocked");
            if (SuppressionCore.FullyBlockedDetail("priority-write,priority-readback"))
                throw new Exception("a single refused write must not count as fully blocked");
            if (SuppressionCore.FullyBlockedDetail(null))
                throw new Exception("null detail must not count as fully blocked");
        }

        private static void TestSnapshotMatchJudgement(string root)
        {
            string beat = Path.Combine(root, "snapshot-match.beat");
            using (Process probe = StartProbe(beat))
            {
                IntPtr h = IntPtr.Zero;
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                        | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, probe.Id);
                    if (h == IntPtr.Zero) throw new Exception("probe handle unavailable");
                    uint pri = Native.GetPriorityClass(h);
                    ulong aff = Native.QueryAffinity(h);
                    uint[] cpuSets = Native.QueryCpuSets(h);
                    int io = Native.QueryIoPriority(h);
                    int pg = Native.QueryPagePriority(h);
                    int qc, qs;
                    if (!Native.TryQueryPowerThrottling(h, out qc, out qs)) { qc = -1; qs = -1; }
                    if (!SuppressionCore.SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qc, qs, -1))
                        throw new Exception("untouched process must match its own snapshot");
                    probe.PriorityClass = ProcessPriorityClass.Idle;
                    if (SuppressionCore.SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qc, qs, -1))
                        throw new Exception("a drifted priority must break the match");
                }
                finally
                {
                    if (h != IntPtr.Zero) Native.CloseHandle(h);
                    StopOwned(probe);
                }
            }
        }

        private static void TestSelfProtectedRoster()
        {
            SelfProtectedRoster.Clear();
            try
            {
                if (SelfProtectedRoster.Contains("HipsTrayProbe"))
                    throw new Exception("empty roster must not contain anything");
                if (!SelfProtectedRoster.Mark("HipsTrayProbe"))
                    throw new Exception("first mark must report newly listed");
                if (SelfProtectedRoster.Mark("HipsTrayProbe"))
                    throw new Exception("second mark must be idempotent");
                if (!SelfProtectedRoster.Contains("hipstrayprobe"))
                    throw new Exception("roster lookup must be case-insensitive");
            }
            finally { SelfProtectedRoster.Clear(); }
        }

        private static void TestSuppressionJournalGate(string root)
        {
            string beat = Path.Combine(root, "journal-gate.beat");
            string unavailable = Path.Combine(root, "missing-journal-dir", "state");
            using (Process probe = StartProbe(beat))
            {
                var core = new SuppressionCore(unavailable);
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    probe.Refresh();
                    ProcessPriorityClass original = probe.PriorityClass;
                    Eq(AcquireResult.ApplyFailed, core.Acquire(
                        probe.Id, probe.ProcessName, SuppressReason.Background,
                        null, SuppressionLevel.Isolated));
                    Eq(0L, core.ApplyOperations);
                    probe.Refresh();
                    Eq(original, probe.PriorityClass);

                    Eq(false, core.Reconcile(
                        probe.Id, probe.ProcessName, SuppressReason.Background, true));
                    Eq(AcquireResult.ApplyFailed, core.Acquire(
                        probe.Id, probe.ProcessName, SuppressReason.Background,
                        null, SuppressionLevel.Isolated));
                    Eq(0L, core.ApplyOperations);

                    core.BeginBatch();
                    core.Acquire(probe.Id, probe.ProcessName,
                        SuppressReason.Background, null, SuppressionLevel.Isolated);
                    SuppressionCore.BatchResult batch = core.EndBatch();
                    Eq(false, batch.WasApplied(probe.Id));
                    Eq(0L, core.ApplyOperations);
                    probe.Refresh();
                    Eq(original, probe.PriorityClass);
                }
                finally
                {
                    core.ReleaseReason(SuppressReason.Background);
                    StopOwned(probe);
                }
            }
        }

        private static void TestCrashBoostReAdoption()
        {
            const string key = "Crash_BoostEntriesV2";
            const int pid = 424242;
            const long creation = 987654321;
            const string name = "PaviseBoostProbe";
            string previous = Settings.LoadStr(key, "");
            string encoded = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(name));
            try
            {
                Settings.SaveStr(key,
                    pid + "|" + creation + "|" + encoded
                    + "|32|255|2|5|1|7,9|1|0");
                CrashGuard.OriginalBoostState recovered;
                Eq(true, CrashGuard.MarkBoostProcess(
                    pid, creation, name,
                    128, 1, 3, 4, -1, new uint[0], 0, 0,
                    out recovered));
                if (recovered == null)
                    throw new Exception("existing crash snapshot was not adopted");
                Eq(32U, recovered.Priority);
                Eq(255UL, recovered.Affinity);
                Eq(2, recovered.Io);
                Eq(5, recovered.Page);
                Eq(1, recovered.Gpu);
                Eq(1, recovered.QoSControl);
                Eq(0, recovered.QoSState);
                Eq(2, recovered.CpuSets.Length);
                Eq(7U, recovered.CpuSets[0]);
                Eq(9U, recovered.CpuSets[1]);
                CrashGuard.ReleaseBoostProcess(pid, creation);
                Eq("", Settings.LoadStr(key, ""));
            }
            finally
            {
                CrashGuard.ReleaseBoostProcess(pid, creation);
                Settings.SaveStr(key, previous);
            }
        }

    }
}
