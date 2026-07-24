// @author bdth 2074055628@qq.com
// Windows 进程恢复、优先级、亲和性与 CPU Sets 自测。

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AegisApp
{
    internal static partial class SelfTests
    {
        private static void TestCorruptJournal(string root)
        {
            string state = Path.Combine(root, "corrupt.state");
            string beat = Path.Combine(root, "corrupt.beat");
            File.WriteAllText(state, "not-a-valid-freeze-journal", Encoding.UTF8);
            Eq(0, FreezeGuard.RestoreJournal(state));
            if (!File.Exists(state)) throw new Exception("corrupt recovery evidence was deleted");
            using (Process probe = StartProbe(beat))
            {
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    var g = new FreezeGuard(state, false);
                    if (g.Freeze(probe.Id, probe.ProcessName, "must-not-overwrite"))
                        throw new Exception("freeze proceeded over corrupt recovery evidence");
                    int before = ReadCounter(beat);
                    WaitAdvance(beat, before, 2000);
                    if (File.ReadAllText(state, Encoding.UTF8) != "not-a-valid-freeze-journal")
                        throw new Exception("corrupt recovery evidence was overwritten");
                }
                finally { StopOwned(probe); }
            }
        }

        private static void TestNormalFreeze(string root)
        {
            string beat = Path.Combine(root, "normal.beat");
            string state = Path.Combine(root, "normal.state");
            using (Process probe = StartProbe(beat))
            {
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    var g = new FreezeGuard(state, false);
                    if (!g.Freeze(probe.Id, probe.ProcessName, "selftest")) throw new Exception("NtSuspendProcess failed");
                    Thread.Sleep(250);
                    int frozen = ReadCounter(beat);
                    Thread.Sleep(450);
                    Eq(frozen, ReadCounter(beat));
                    if (!File.Exists(state)) throw new Exception("recovery journal missing while frozen");
                    if (!g.Restore(probe.Id)) throw new Exception("NtResumeProcess failed");
                    WaitAdvance(beat, frozen, 3000);
                    if (File.Exists(state)) throw new Exception("journal remained after restore");
                }
                finally { StopOwned(probe); }
            }
        }

        private static void TestPidReuseJournal(string root)
        {
            string beat = Path.Combine(root, "reuse.beat");
            string state = Path.Combine(root, "reuse.state");
            using (Process probe = StartProbe(beat))
            {
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    long creation, cpu; ulong io;
                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, probe.Id);
                    if (h == IntPtr.Zero) throw new Exception("cannot query probe");
                    try { if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) throw new Exception("cannot sample probe"); }
                    finally { Native.CloseHandle(h); }

                    string name64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(probe.ProcessName));
                    string why64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("reuse-test"));
                    File.WriteAllLines(state, new[] { "AEGIS_FREEZE_V1", probe.Id + "|" + (creation + 1) + "|" + name64 + "|" + why64 }, Encoding.UTF8);
                    int before = ReadCounter(beat);
                    Eq(0, FreezeGuard.RestoreJournal(state));
                    WaitAdvance(beat, before, 2000);
                    if (File.Exists(state)) throw new Exception("stale identity journal was retained");
                }
                finally { StopOwned(probe); }
            }
        }

        private static void TestCrashRecovery(string root)
        {
            string beat = Path.Combine(root, "crash.beat");
            string state = Path.Combine(root, "crash.state");
            string armed = state + ".armed";
            using (Process probe = StartProbe(beat))
            {
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    int before = ReadCounter(beat);
                    var si = Hidden("--freeze-crash-parent " + probe.Id + " " + Quote(state));
                    using (Process parent = Process.Start(si))
                    {
                        if (parent == null) throw new Exception("crash-parent did not start");
                        if (!parent.WaitForExit(5000)) throw new Exception("crash-parent did not exit");
                        if (parent.ExitCode != 0) throw new Exception("crash-parent exit " + parent.ExitCode);
                    }
                    WaitFile(armed, 3000);
                    int afterOwnerExit = ReadCounter(beat);
                    if (afterOwnerExit < before) throw new Exception("invalid heartbeat after owner exit");
                    WaitAdvance(beat, afterOwnerExit, 5000);
                    WaitGone(state, 5000);
                }
                finally { StopOwned(probe); }
            }
        }

        private static void TestReportMigration(string root)
        {
            string dir = Path.Combine(root, "report-migration");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, SessionReportStore.FileName);
            File.WriteAllText(path, "2026-01-01 | Game | 60 FPS | 1% Low 40", Encoding.UTF8);
            Eq(Lang.T("report.none"), SessionReportStore.ReadTail(dir, 20));
            if (!File.Exists(path + ".telemetry.bak")) throw new Exception("legacy report backup missing");
            SessionReportStore.Append(dir, "Game", PerformancePreset.Competitive, TimeSpan.FromMinutes(3), true, 12, 1);
            string current = SessionReportStore.ReadTail(dir, 20);
            if (current.IndexOf(Lang.T("report.boost.ok"), StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("new report format missing verified boost state");
            if (current.IndexOf("FPS", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("legacy frame metrics leaked into current report");
        }

        private static void TestBoostReadback(string root)
        {
            string beat = Path.Combine(root, "boost-readback.beat");
            using (Process probe = StartProbe(beat))
            {
                IntPtr handle = IntPtr.Zero;
                uint originalPriority = Native.NORMAL_PRIORITY_CLASS;
                int originalIo = 2;
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    handle = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, probe.Id);
                    if (handle == IntPtr.Zero) throw new Exception("cannot open owned boost probe");
                    originalPriority = Native.GetPriorityClass(handle);
                    originalIo = Native.QueryIoPriority(handle);
                    uint actualPriority;
                    int actualIo, error;
                    if (!GameMode.ApplyAndVerifyBoostState(handle, out actualPriority, out actualIo, out error))
                        throw new Exception("readback failed: priority=0x" + actualPriority.ToString("X") + ", io=" + actualIo + ", error=" + error);
                    Eq(Native.HIGH_PRIORITY_CLASS, actualPriority);
                    Eq(3, actualIo);
                }
                finally
                {
                    if (handle != IntPtr.Zero)
                    {
                        Native.TrySetIoPriority(handle, originalIo >= 0 ? originalIo : 2);
                        Native.SetPriorityClass(handle, originalPriority == 0 ? Native.NORMAL_PRIORITY_CLASS : originalPriority);
                        Native.CloseHandle(handle);
                    }
                    StopOwned(probe);
                }
            }
        }

        private static void TestAffinityRestore(string root)
        {
            string beat = Path.Combine(root, "affinity.beat");
            using (Process probe = StartProbe(beat))
            {
                IntPtr h = IntPtr.Zero;
                ulong original = 0;
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, probe.Id);
                    if (h == IntPtr.Zero) throw new Exception("cannot open probe for affinity");
                    original = Native.QueryAffinity(h);
                    if (original == 0) throw new Exception("original affinity unavailable");
                    ulong one = original & (~original + 1UL);
                    uint[] ids = CpuTopology.CpuSetIdsFor(one);
                    if (ids == null || ids.Length == 0) throw new Exception("CPU Set IDs unavailable");
                    if (!Native.TrySetCpuSets(h, ids)) throw new Exception("cannot apply CPU Sets");
                    if (!Native.TryClearCpuSets(h)) throw new Exception("cannot clear CPU Sets");
                    if (!Native.SetProcessAffinityMask(h, (UIntPtr)one)) throw new Exception("cannot apply test affinity");
                    Eq(one, Native.QueryAffinity(h));
                    Native.TryClearCpuSets(h);
                    if (!Native.SetProcessAffinityMask(h, (UIntPtr)original)) throw new Exception("cannot restore affinity");
                    Eq(original, Native.QueryAffinity(h));
                }
                finally
                {
                    if (h != IntPtr.Zero)
                    {
                        if (original != 0) Native.SetProcessAffinityMask(h, (UIntPtr)original);
                        Native.TryClearCpuSets(h);
                        Native.CloseHandle(h);
                    }
                    StopOwned(probe);
                }
            }
        }

        private static void TestStagedSuppression(string root)
        {
            string beat = Path.Combine(root, "staged.beat");
            using (Process probe = StartProbe(beat))
            {
                string state = Path.Combine(root, "staged-suppression.state");
                var core = new SuppressionCore(state);
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    probe.Refresh();
                    ProcessPriorityClass originalPriority = probe.PriorityClass;
                    IntPtr originalAffinity = probe.ProcessorAffinity;
                    AcquireResult eco = core.Acquire(probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Eco);
                    if (eco != AcquireResult.NewlyThrottled)
                        throw new Exception("Eco stage did not acquire: " + eco + " [" + core.LastApplyError + "]");
                    probe.Refresh();
                    if (probe.PriorityClass == ProcessPriorityClass.Idle) throw new Exception("Eco stage isolated too early");
                    core.Acquire(probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Restrained);
                    probe.Refresh();
                    Eq(ProcessPriorityClass.BelowNormal, probe.PriorityClass);
                    core.Acquire(probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Isolated);
                    probe.Refresh();
                    Eq(ProcessPriorityClass.Idle, probe.PriorityClass);
                    core.Acquire(probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Eco);
                    probe.Refresh();
                    Eq(originalPriority, probe.PriorityClass);
                    Eq((long)originalAffinity, (long)probe.ProcessorAffinity);
                    core.Acquire(probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Isolated);
                    if (!core.Release(probe.Id, SuppressReason.Background)) throw new Exception("staged restore did not run");
                    probe.Refresh();
                    Eq(originalPriority, probe.PriorityClass);
                    Eq((long)originalAffinity, (long)probe.ProcessorAffinity);

                    core.BeginBatch();
                    try
                    {
                        core.Acquire(probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Isolated);
                        probe.Refresh();
                        Eq(originalPriority, probe.PriorityClass);
                    }
                    finally { core.EndBatch(); }
                    probe.Refresh(); Eq(ProcessPriorityClass.Idle, probe.PriorityClass);
                    if (!File.Exists(state)) throw new Exception("batched suppression journal missing");
                    core.Release(probe.Id, SuppressReason.Background);

                    core.Acquire(probe.Id, probe.ProcessName, SuppressReason.AntiCheat, "test", SuppressionLevel.Isolated);
                    core.Acquire(probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Eco);
                    Eq(SuppressionLevel.Eco, core.LevelOf(probe.Id, SuppressReason.Background));
                    Eq(SuppressionLevel.Isolated, core.LevelOf(probe.Id));
                    probe.Refresh(); Eq(ProcessPriorityClass.Idle, probe.PriorityClass);
                    core.Release(probe.Id, SuppressReason.AntiCheat);
                    probe.Refresh(); Eq(originalPriority, probe.PriorityClass);
                    core.Release(probe.Id, SuppressReason.Background);
                }
                finally
                {
                    core.ReleaseReason(SuppressReason.Background);
                    StopOwned(probe);
                }
            }
        }

        private static void TestExistingCpuSetRestore(string root)
        {
            uint[] available = CpuTopology.BackgroundCpuSetIds();
            if (available == null || available.Length == 0) Skip("process CPU Sets are unavailable");
            string beat = Path.Combine(root, "cpu-set-restore.beat");
            using (Process probe = StartProbe(beat))
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                    | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, probe.Id);
                if (h == IntPtr.Zero) throw new Exception("cannot open CPU Set probe");
                uint[] original = Native.QueryCpuSets(h);
                var custom = new[] { available[0] };
                var core = new SuppressionCore(Path.Combine(root, "cpu-set-restore.state"));
                try
                {
                    if (original == null || !Native.TrySetCpuSets(h, custom) || !Native.CpuSetsMatch(h, custom))
                        throw new Exception("cannot establish custom CPU Set baseline");
                    AcquireResult result = core.Acquire(probe.Id, probe.ProcessName,
                        SuppressReason.Background, null, SuppressionLevel.Isolated);
                    if (result != AcquireResult.NewlyThrottled) throw new Exception("suppression failed: " + result);
                    core.Release(probe.Id, SuppressReason.Background);
                    if (!Native.CpuSetsMatch(h, custom)) throw new Exception("custom CPU Sets were not restored");
                }
                finally
                {
                    core.ReleaseReason(SuppressReason.Background);
                    if (original != null) Native.RestoreCpuSets(h, original);
                    Native.CloseHandle(h);
                    StopOwned(probe);
                }
            }
        }

    }
}

