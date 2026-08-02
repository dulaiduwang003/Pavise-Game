// @author bdth 2074055628@qq.com
// 冻结骨架：崩溃日志唤醒、身份复用防护、挂起不重入。

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static Process StartFreezeVictim()
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c pause")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            Process victim = Process.Start(psi);
            Thread.Sleep(250);
            return victim;
        }

        private static long CreationOf(Process p)
        {
            return p.StartTime.ToUniversalTime().ToFileTimeUtc();
        }

        private static string FreezeJournalLine(int pid, long creation, string name, bool frozen)
        {
            return pid + "|" + creation + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(name))
                + "|" + Native.NORMAL_PRIORITY_CLASS + "|" + CpuTopology.AllMask + "|2|5||-1|-1|-1|"
                + (frozen ? 1 : 0);
        }

        private static void TestFrozenJournalThaw()
        {
            Process victim = StartFreezeVictim();
            string journal = Path.Combine(Path.GetTempPath(),
                "PaviseFreezeThaw_" + Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N") + ".state");
            try
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, victim.Id);
                if (h == IntPtr.Zero) throw new TestSkippedException("cannot open the victim with suspend access");
                try
                {
                    if (Native.NtSuspendProcess(h) != 0)
                        throw new TestSkippedException("NtSuspendProcess was refused for the victim");
                }
                finally { Native.CloseHandle(h); }

                File.WriteAllLines(journal, new[]
                {
                    "PAVISE_SUPPRESSION_V1",
                    FreezeJournalLine(victim.Id, CreationOf(victim), "cmd", true)
                }, new UTF8Encoding(false));

                SuppressionCore.HealFromCrash(journal);

                victim.StandardInput.Close();
                if (!victim.WaitForExit(5000))
                    throw new Exception("the victim stayed suspended after crash recovery");
                if (File.Exists(journal))
                    throw new Exception("a fully recovered journal should have been deleted");
            }
            finally
            {
                try { if (!victim.HasExited) victim.Kill(); } catch { }
                try { victim.Dispose(); } catch { }
                try { if (File.Exists(journal)) File.Delete(journal); } catch { }
            }
        }

        private static void TestFrozenJournalRejectsPidReuse()
        {
            Process victim = StartFreezeVictim();
            string journal = Path.Combine(Path.GetTempPath(),
                "PaviseFreezeReuse_" + Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N") + ".state");
            try
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, victim.Id);
                if (h == IntPtr.Zero) throw new TestSkippedException("cannot open the victim with suspend access");
                try
                {
                    if (Native.NtSuspendProcess(h) != 0)
                        throw new TestSkippedException("NtSuspendProcess was refused for the victim");
                }
                finally { Native.CloseHandle(h); }

                File.WriteAllLines(journal, new[]
                {
                    "PAVISE_SUPPRESSION_V1",
                    FreezeJournalLine(victim.Id, CreationOf(victim) - 999999, "cmd", true)
                }, new UTF8Encoding(false));

                SuppressionCore.HealFromCrash(journal);

                victim.StandardInput.Close();
                if (victim.WaitForExit(1500))
                    throw new Exception("crash recovery resumed a process whose identity did not match the record");
            }
            finally
            {
                try
                {
                    IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, victim.Id);
                    if (h != IntPtr.Zero) { Native.NtResumeProcess(h); Native.CloseHandle(h); }
                }
                catch { }
                try { if (!victim.HasExited) victim.Kill(); } catch { }
                try { victim.Dispose(); } catch { }
                try { if (File.Exists(journal)) File.Delete(journal); } catch { }
            }
        }

        private static void TestFreezeDwellGate()
        {
            var gate = new FreezeDwellTracker();
            long t = DateTime.UtcNow.Ticks;
            long second = TimeSpan.TicksPerSecond;
            long busyPerSecond = (long)(second * 0.5);

            Eq(false, gate.Observe(100, "probe", 7, 0, t));

            long cpu = 0;
            for (int i = 1; i <= FreezeDwellTracker.DwellSeconds; i++)
                Eq(i >= FreezeDwellTracker.DwellSeconds + 1,
                    gate.Observe(100, "probe", 7, cpu, t + second * i));
            Eq(true, gate.Observe(100, "probe", 7, cpu, t + second * (FreezeDwellTracker.DwellSeconds + 1)));

            cpu += busyPerSecond;
            long busyAt = t + second * (FreezeDwellTracker.DwellSeconds + 2);
            Eq(false, gate.Observe(100, "probe", 7, cpu, busyAt));
            Eq(false, gate.Observe(100, "probe", 7, cpu, busyAt + second * 5));

            var reuse = new FreezeDwellTracker();
            Eq(false, reuse.Observe(101, "probe", 7, 0, t));
            Eq(false, reuse.Observe(101, "probe", 9, 0, t + second * 60));
        }

        private static void TestAntiCheatNeverFreezes()
        {
            var core = new SuppressionCore();
            using (Process probe = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
            { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true }))
            {
                Thread.Sleep(250);
                try
                {
                    core.Acquire(probe.Id, probe.ProcessName, SuppressReason.AntiCheat, null,
                        SuppressionLevel.Frozen);
                    SuppressionLevel actual = core.LevelOf(probe.Id, SuppressReason.AntiCheat);
                    if (actual >= SuppressionLevel.Frozen)
                        throw new Exception("an anti-cheat reason reached the frozen tier: " + actual);
                    if (core.LevelOf(probe.Id) >= SuppressionLevel.Frozen)
                        throw new Exception("the effective tier reached frozen through an anti-cheat reason");
                    probe.StandardInput.Close();
                    if (!probe.WaitForExit(5000))
                        throw new Exception("the anti-cheat probe was suspended despite the guard");
                }
                finally
                {
                    core.Release(probe.Id, SuppressReason.AntiCheat);
                    try { if (!probe.HasExited) probe.Kill(); } catch { }
                }
            }
        }

        private static void TestSuspendIsNotReentrant()
        {
            Process victim = StartFreezeVictim();
            try
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, victim.Id);
                if (h == IntPtr.Zero) throw new TestSkippedException("cannot open the victim with suspend access");
                try
                {
                    if (Native.NtSuspendProcess(h) != 0)
                        throw new TestSkippedException("NtSuspendProcess was refused for the victim");
                    Native.NtResumeProcess(h);
                }
                finally { Native.CloseHandle(h); }

                victim.StandardInput.Close();
                if (!victim.WaitForExit(5000))
                    throw new Exception("a single resume failed to wake a singly-suspended process");
            }
            finally
            {
                try { if (!victim.HasExited) victim.Kill(); } catch { }
                try { victim.Dispose(); } catch { }
            }
        }
    }
}
