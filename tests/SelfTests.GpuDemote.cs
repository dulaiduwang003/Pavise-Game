// @author bdth 2074055628@qq.com
// GPU scheduling demotion: tier mapping, journal gpu field, native roundtrip, GPU-less tolerance.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestGpuDemoteMapping()
        {
            Eq(-1, SuppressionCore.DesiredGpuClass(true, SuppressReason.Background, SuppressionLevel.Isolated, -1));
            Eq(2, SuppressionCore.DesiredGpuClass(false, SuppressReason.Background, SuppressionLevel.Isolated, 2));
            Eq(2, SuppressionCore.DesiredGpuClass(true, SuppressReason.AntiCheat, SuppressionLevel.Isolated, 2));
            Eq(2, SuppressionCore.DesiredGpuClass(true, SuppressReason.Background, SuppressionLevel.Eco, 2));
            Eq(Native.GpuPriorityBelowNormal, SuppressionCore.DesiredGpuClass(
                true, SuppressReason.Background, SuppressionLevel.Restrained, 2));
            Eq(Native.GpuPriorityIdle, SuppressionCore.DesiredGpuClass(
                true, SuppressReason.Background, SuppressionLevel.Isolated, 2));
            Eq(Native.GpuPriorityIdle, SuppressionCore.DesiredGpuClass(
                true, SuppressReason.AntiCheat | SuppressReason.Background, SuppressionLevel.Isolated, 4));
        }

        private static void TestGpuJournalField()
        {
            string name = Convert.ToBase64String(Encoding.UTF8.GetBytes("probe.exe"));
            Eq("4242|1|0|1", SuppressionCore.ProbeJournalLine(
                "4242|123456789|" + name + "|32|255|2|5|1,3|1|0|1"));
            Eq("4242|1|0|-1", SuppressionCore.ProbeJournalLine(
                "4242|123456789|" + name + "|32|255|2|5|1,3|1|0"));
            Eq("4242|-1|-1|-1", SuppressionCore.ProbeJournalLine(
                "4242|123456789|" + name + "|32|255|2|5"));
            Eq("null", SuppressionCore.ProbeJournalLine(
                "4242|123456789|" + name + "|32|255|2|5|1,3|1|0|1|9"));
        }

        private static void TestGpuPriorityRoundtrip()
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, Process.GetCurrentProcess().Id);
            if (h == IntPtr.Zero) throw new TestSkippedException("cannot open self with set-information access");
            try
            {
                int orig;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out orig) != 0)
                    throw new TestSkippedException("no GPU scheduling state for this process");
                int target = orig == Native.GpuPriorityBelowNormal
                    ? Native.GpuPriorityNormal : Native.GpuPriorityBelowNormal;
                if (Native.D3DKMTSetProcessSchedulingPriorityClass(h, target) != 0)
                    throw new TestSkippedException("GPU scheduling class rejected the write");
                try
                {
                    int now;
                    Eq(0, Native.D3DKMTGetProcessSchedulingPriorityClass(h, out now));
                    Eq(target, now);
                }
                finally { Native.D3DKMTSetProcessSchedulingPriorityClass(h, orig); }
                int restored;
                Eq(0, Native.D3DKMTGetProcessSchedulingPriorityClass(h, out restored));
                Eq(orig, restored);
            }
            finally { Native.CloseHandle(h); }
        }

        private static void RunGpuDemoteProbe(int pid, string output)
        {
            var log = new List<string>();
            bool previous = SuppressionCore.GpuDemoteEnabled;
            string state = Path.Combine(Path.GetTempPath(),
                "PaviseGpuDemoteProbe_" + Process.GetCurrentProcess().Id + ".state");
            var core = new SuppressionCore(state);
            try
            {
                using (Process target = Process.GetProcessById(pid))
                {
                    SuppressionCore.GpuDemoteEnabled = true;
                    string name = target.ProcessName;
                    log.Add("initial=" + QueryGpuClass(pid));
                    log.Add("acquire=" + core.Acquire(
                        pid, name, SuppressReason.Background, null, SuppressionLevel.Isolated));
                    log.Add("afterAcquire=" + QueryGpuClass(pid));
                    DateTime deadline = DateTime.UtcNow.AddSeconds(25);
                    string cls = QueryGpuClass(pid);
                    while (cls != "0" && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(500);
                        core.Reconcile(pid, name, SuppressReason.Background, true);
                        cls = QueryGpuClass(pid);
                    }
                    log.Add("afterReconcile=" + cls);
                    log.Add("released=" + core.ReleaseReason(SuppressReason.Background));
                    log.Add("afterRelease=" + QueryGpuClass(pid));
                }
                File.WriteAllLines(output, log.ToArray(), Encoding.UTF8);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                log.Add("ERROR|" + ex.Message);
                try { File.WriteAllLines(output, log.ToArray(), Encoding.UTF8); } catch { }
                Environment.ExitCode = 4;
            }
            finally
            {
                try { core.ReleaseReason(SuppressReason.Background); } catch { }
                SuppressionCore.GpuDemoteEnabled = previous;
                try { if (File.Exists(state)) File.Delete(state); } catch { }
            }
        }

        private static string QueryGpuClass(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "open-failed";
            try
            {
                int cls;
                return Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0
                    ? cls.ToString() : "none";
            }
            finally { Native.CloseHandle(h); }
        }

        private static void TestGpuDemoteGpulessProcess(string root)
        {
            string beat = Path.Combine(root, "gpu-demote.beat");
            string state = Path.Combine(root, "gpu-demote.state");
            bool previous = SuppressionCore.GpuDemoteEnabled;
            using (Process probe = StartProbe(beat))
            {
                var core = new SuppressionCore(state);
                try
                {
                    SuppressionCore.GpuDemoteEnabled = true;
                    WaitAdvance(beat, -1, 4000);
                    Eq(AcquireResult.NewlyThrottled, core.Acquire(
                        probe.Id, probe.ProcessName, SuppressReason.Background, null, SuppressionLevel.Isolated));
                    probe.Refresh();
                    Eq(ProcessPriorityClass.Idle, probe.PriorityClass);

                    string[] lines = File.ReadAllLines(state);
                    Eq(2, lines.Length);
                    string[] parts = lines[1].TrimEnd('\r').Split('|');
                    Eq(11, parts.Length);
                    int journaledGpu = int.Parse(parts[10]);

                    IntPtr hp = Native.OpenProcess(
                        Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                        | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, probe.Id);
                    if (hp == IntPtr.Zero) throw new Exception("cannot reopen probe");
                    try
                    {
                        int cls;
                        int status = Native.D3DKMTGetProcessSchedulingPriorityClass(hp, out cls);
                        if (journaledGpu >= 0)
                        {
                            Eq(0, status);
                            Eq(Native.GpuPriorityIdle, cls);
                        }
                        else Eq(true, status != 0);

                        Eq(1, core.ReleaseReason(SuppressReason.Background));
                        probe.Refresh();
                        Eq(ProcessPriorityClass.Normal, probe.PriorityClass);
                        if (journaledGpu >= 0)
                        {
                            Eq(0, Native.D3DKMTGetProcessSchedulingPriorityClass(hp, out cls));
                            Eq(journaledGpu, cls);
                        }
                    }
                    finally { Native.CloseHandle(hp); }
                }
                finally
                {
                    SuppressionCore.GpuDemoteEnabled = previous;
                    core.ReleaseReason(SuppressReason.Background);
                    StopOwned(probe);
                }
            }
        }
    }
}
