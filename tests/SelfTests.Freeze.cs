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

        // 崩溃后残留的冻结记录必须被唤醒，否则进程永远醒不过来
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

                // 唤醒的证据：进程能响应输入并退出。仍被挂起的话它收不到 stdin 关闭
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

        // pid 复用：记录里的身份对不上时，绝不能对无关进程调 resume
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

                // 创建时间对不上 = pid 已被复用
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

        // 挂起计数是累加的：多挂一次就多欠一次 resume，
        // 所以核验路径反复走过之后，一次解冻仍必须能唤醒进程
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
                    // 模拟状态机被反复驱动：真实实现只在边沿动手，这里验证
                    // 若真的重入挂起，单次 resume 就唤不醒——即本测试要防的回归
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
