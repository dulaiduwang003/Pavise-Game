// @author bdth 2074055628@qq.com
// 文件用途 后台压制 崩溃恢复 Cross 清理和竞争性能自测

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal static partial class SelfTests
    {
        private static void TestLolCrossCleaner(string root)
        {
            string lol = Path.Combine(root, "lol-cross");
            string cross = Path.Combine(lol, "Cross");
            Directory.CreateDirectory(Path.Combine(lol, "TCLS"));
            Directory.CreateDirectory(cross);
            string payload = Path.Combine(cross, "community-overlay.bin");
            File.WriteAllBytes(payload, new byte[4096]);
            string probeExe = Path.Combine(lol, "LeagueClient.exe");
            File.Copy(Application.ExecutablePath, probeExe, true);
            Process running = null;
            try
            {
                running = Process.Start(new ProcessStartInfo(probeExe, "--cpu-burn")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (running == null) throw new Exception("cannot start LoL path probe");
                Thread.Sleep(250);
                if (!LolCross.AnyLolProcessAlive(lol)) throw new Exception("running client was not detected");
                CacheSweep.Result blocked = LolCross.Clean(lol);
                if (blocked.FailedFiles == 0 || !File.Exists(payload)) throw new Exception("cleaner ignored running-client guard");
            }
            finally { StopOwned(running); }

            CacheSweep.Result cleaned = LolCross.Clean(lol);
            if (cleaned.FreedBytes < 4096 || File.Exists(payload)) throw new Exception("Cross payload was not deleted");
            if (!Directory.Exists(cross)) throw new Exception("Cross root directory should be retained");
        }

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
                    probe.PriorityClass = ProcessPriorityClass.Normal;
                    probe.Refresh();
                    Eq(ProcessPriorityClass.Normal, probe.PriorityClass);
                    if (!core.Reapply(probe.Id, probe.ProcessName, SuppressReason.Background))
                        throw new Exception("tracked suppression was not re-applied");
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

        private static void TestReusedOwnerRecovery(string root)
        {
            string beat = Path.Combine(root, "owner-reuse.beat");
            string state = Path.Combine(root, "owner-reuse.state");
            using (Process probe = StartProbe(beat))
            {
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    var guard = new FreezeGuard(state, false);
                    if (!guard.Freeze(probe.Id, probe.ProcessName, "owner-reuse-selftest"))
                        throw new Exception("cannot freeze probe");
                    Thread.Sleep(200);
                    int frozen = ReadCounter(beat);
                    FreezeGuard.WatchOwnerAndRestore(Process.GetCurrentProcess().Id, 1, state);
                    WaitAdvance(beat, frozen, 3000);
                    if (File.Exists(state)) throw new Exception("watchdog retained journal after owner PID reuse");
                }
                finally { StopOwned(probe); }
            }
        }

        private static double TestContentionGain(string root)
        {
            string state = Path.Combine(root, "perf.state");
            Process burner = null;
            using (Process self = Process.GetCurrentProcess())
            {
                IntPtr original = self.ProcessorAffinity;
                ulong mask = (ulong)original.ToInt64();
                if (mask == 0) throw new Exception("test process affinity unavailable");
                ulong one = mask & (~mask + 1UL);
                var guard = new FreezeGuard(state, false);
                try
                {
                    self.ProcessorAffinity = (IntPtr)(long)one;
                    burner = Process.Start(Hidden("--cpu-burn"));
                    if (burner == null) throw new Exception("CPU contender did not start");
                    burner.ProcessorAffinity = (IntPtr)(long)one;
                    Thread.Sleep(250);
                    MeasureWork(120);
                    long contended = MeasureWork(600);
                    if (!guard.Freeze(burner.Id, burner.ProcessName, "performance-selftest"))
                        throw new Exception("cannot freeze CPU contender");
                    Thread.Sleep(80);
                    long exclusive = MeasureWork(600);
                    if (!guard.Restore(burner.Id)) throw new Exception("cannot restore CPU contender");
                    if (contended <= 0) throw new Exception("invalid contended throughput");
                    return (double)exclusive / contended;
                }
                finally
                {
                    guard.RestoreAll();
                    try { self.ProcessorAffinity = original; }
                    catch { }
                    if (burner != null)
                    {
                        StopOwned(burner);
                        burner.Dispose();
                    }
                }
            }
        }
    }
}
