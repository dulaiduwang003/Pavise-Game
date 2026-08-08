// @author bdth 2074055628@qq.com
// 文件用途 量化工作集被清空后的回读代价 并验证硬性最小工作集能否防住外部清空

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private const uint QuotaMinEnable = 0x1;
        private const uint QuotaMaxDisable = 0x8;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(IntPtr process,
            IntPtr min, IntPtr max, uint flags);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process,
            out ProcessMemoryCounters counters, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public int cb;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
        }

        private static long WorkingSetOf(Process p)
        {
            ProcessMemoryCounters c;
            return K32GetProcessMemoryInfo(p.Handle, out c, Marshal.SizeOf(typeof(ProcessMemoryCounters)))
                ? (long)c.WorkingSetSize : -1;
        }

        internal static void RunMemLockChild(string mbArg, string cmdFile, string repFile)
        {
            int mb;
            if (!int.TryParse(mbArg, out mb) || mb < 16) mb = 300;
            int bytes = mb * 1024 * 1024;
            var block = new byte[bytes];
            var rnd = new Random(11);
            rnd.NextBytes(block);
            File.WriteAllText(repFile, "ready 0", Encoding.ASCII);
            int lastSeq = 0;
            long sink = 0;
            while (true)
            {
                Thread.Sleep(50);
                string cmd;
                try { cmd = File.ReadAllText(cmdFile, Encoding.ASCII); }
                catch { continue; }
                string[] parts = cmd.Split(' ');
                if (parts.Length < 2) continue;
                int seq;
                if (!int.TryParse(parts[1], out seq) || seq <= lastSeq) continue;
                lastSeq = seq;
                if (parts[0] == "exit") return;
                if (parts[0] == "walk")
                {
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < bytes; i += 4096) sink += block[i];
                    sw.Stop();
                    File.WriteAllText(repFile,
                        "walked " + seq + " " + sw.Elapsed.TotalMilliseconds.ToString("0.0"),
                        Encoding.ASCII);
                }
                if (sink == long.MinValue) Console.Write("");
            }
        }

        private static double MemLockAsk(string cmdFile, string repFile, int seq, string verb)
        {
            File.WriteAllText(cmdFile, verb + " " + seq, Encoding.ASCII);
            for (int i = 0; i < 600; i++)
            {
                Thread.Sleep(50);
                string rep;
                try { rep = File.ReadAllText(repFile, Encoding.ASCII); }
                catch { continue; }
                string[] parts = rep.Split(' ');
                if (parts.Length >= 3 && parts[0] == "walked" && parts[1] == seq.ToString())
                {
                    double ms;
                    if (double.TryParse(parts[2], out ms)) return ms;
                }
            }
            return -1;
        }

        private static void MemLockPressure(int mb)
        {
            var blocks = new System.Collections.Generic.List<byte[]>();
            try
            {
                for (int i = 0; i < mb / 256; i++)
                {
                    var b = new byte[256 * 1024 * 1024];
                    for (int off = 0; off < b.Length; off += 4096) b[off] = 1;
                    blocks.Add(b);
                }
            }
            catch (OutOfMemoryException) { }
            blocks.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static void RunMemLockLab(string output, string mbArg, string pressureArg)
        {
            int mb, pressureMb;
            if (!int.TryParse(mbArg ?? "", out mb) || mb < 64) mb = 300;
            if (!int.TryParse(pressureArg ?? "", out pressureMb) || pressureMb < 0) pressureMb = 4096;

            var sb = new StringBuilder();
            sb.AppendLine("=== 工作集锁定台架 ===");
            sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "  受害块 " + mb + "MB  压力 " + pressureMb + "MB");
            string dir = Path.Combine(Path.GetTempPath(), "PaviseMemLock_" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            string cmdFile = Path.Combine(dir, "cmd.txt");
            string repFile = Path.Combine(dir, "rep.txt");
            File.WriteAllText(cmdFile, "none 0", Encoding.ASCII);
            string self = Process.GetCurrentProcess().MainModule.FileName;
            Process child = null;
            try
            {
                child = Process.Start(new ProcessStartInfo(self,
                    "--memlock-child " + mb + " \"" + cmdFile + "\" \"" + repFile + "\"")
                { UseShellExecute = false, CreateNoWindow = true });
                for (int i = 0; i < 200 && !File.Exists(repFile); i++) Thread.Sleep(50);
                Thread.Sleep(500);

                int seq = 1;
                double warm = MemLockAsk(cmdFile, repFile, seq++, "walk");
                double warm2 = MemLockAsk(cmdFile, repFile, seq++, "walk");
                sb.AppendLine("热遍历基线：" + warm.ToString("0.0") + " / " + warm2.ToString("0.0") + " ms");

                long wsBefore = WorkingSetOf(child);
                EmptyWorkingSet(child.Handle);
                Thread.Sleep(300);
                if (pressureMb > 0) MemLockPressure(pressureMb);
                Thread.Sleep(300);
                long wsTrimmed = WorkingSetOf(child);
                double cold = MemLockAsk(cmdFile, repFile, seq++, "walk");
                sb.AppendLine("清空工作集（" + (wsBefore / 1048576) + "MB → " + (wsTrimmed / 1048576)
                    + "MB）+ " + pressureMb + "MB 压力后遍历：" + cold.ToString("0.0") + " ms");

                MemLockAsk(cmdFile, repFile, seq++, "walk");
                Thread.Sleep(500);
                long wsRestored = WorkingSetOf(child);
                if (pressureMb > 0) MemLockPressure(pressureMb);
                Thread.Sleep(500);
                long wsAutoTrim = WorkingSetOf(child);
                double autoTrimWalk = MemLockAsk(cmdFile, repFile, seq++, "walk");
                sb.AppendLine("无锁 + 纯内存压力：工作集 " + (wsRestored / 1048576) + "MB → "
                    + (wsAutoTrim / 1048576) + "MB，遍历 " + autoTrimWalk.ToString("0.0") + " ms");

                MemLockAsk(cmdFile, repFile, seq++, "walk");
                long lockBytes = (long)(mb + 64) * 1024 * 1024;
                bool lockOk = SetProcessWorkingSetSizeEx(child.Handle,
                    (IntPtr)lockBytes, (IntPtr)(lockBytes * 2), QuotaMinEnable | QuotaMaxDisable);
                MemLockAsk(cmdFile, repFile, seq++, "walk");
                long wsLocked = WorkingSetOf(child);
                sb.AppendLine("硬性最小工作集设置 " + (lockBytes / 1048576) + "MB："
                    + (lockOk ? "成功" : "失败 err=" + Marshal.GetLastWin32Error())
                    + "，当前工作集 " + (wsLocked / 1048576) + "MB");

                if (pressureMb > 0) MemLockPressure(pressureMb);
                Thread.Sleep(500);
                long wsLockedAfter = WorkingSetOf(child);
                double lockedWalk = MemLockAsk(cmdFile, repFile, seq++, "walk");
                sb.AppendLine("锁定 + 纯内存压力：工作集 " + (wsLockedAfter / 1048576)
                    + "MB，遍历 " + lockedWalk.ToString("0.0") + " ms");

                sb.AppendLine();
                bool coldHurts = warm > 0 && cold > warm * 3;
                long tenth = (long)mb * 1024 * 1024 / 10;
                bool autoTrimmed = wsAutoTrim < wsRestored - tenth;
                bool held = wsLockedAfter >= (long)mb * 1024 * 1024 * 8 / 10;
                sb.AppendLine("换出伤害（显式清空+压力模拟最坏情况）：回读代价 "
                    + (cold / Math.Max(0.1, warm)).ToString("F0") + " 倍于热遍历"
                    + (coldHurts ? "" : "（不显著）"));
                if (!autoTrimmed)
                    sb.AppendLine("自动裁剪：本机内存充裕，" + pressureMb
                        + "MB 压力未触发系统对受害进程的自动裁剪——收益只在内存紧张机型上出现");
                else
                    sb.AppendLine("自动裁剪：压力使工作集掉到 " + (wsAutoTrim / 1048576)
                        + "MB，锁定后" + (held ? "顶住（" + (wsLockedAfter / 1048576) + "MB）" : "仍未顶住"));
                bool worth = lockOk && (!autoTrimmed || held);
                sb.AppendLine("结论：" + (autoTrimmed
                    ? (held ? "机制有效，值得实现" : "锁定顶不住自动裁剪，不值得实现")
                    : "本机无法触发自动裁剪，机制真实性未证伪；换出代价实测 "
                        + (cold / Math.Max(0.1, warm)).ToString("F0")
                        + " 倍，建议实现但只对内存紧张机型启用（可用内存低于阈值才锁）"));
                Environment.ExitCode = worth ? 0 : 1;
                File.WriteAllText(cmdFile, "exit " + seq, Encoding.ASCII);
                child.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR=" + ex.Message);
                Environment.ExitCode = 1;
            }
            finally
            {
                try { if (child != null && !child.HasExited) child.Kill(); }
                catch { }
                if (child != null) child.Dispose();
                try { Directory.Delete(dir, true); } catch { }
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            }
        }
    }
}
