// @author bdth 2074055628@qq.com
// 文件用途 2.1.3.x 新功能的收益证伪台架 无窗口控制台 A/B 对照出数字
//   memshield 硬工作集下限在裁剪与内存压力下保住驻留
//   cachewarm 顺序预读进待机缓存后 再读是否显著快于冷读
//   adaptive  隔离档旋钮(IDLE+EcoQoS+关睿频)压住饱和后台后 前台吞吐与尾延迟的变化
//   只对自己拉起的子进程与自己创建的临时文件动手 不需要管理员 不碰系统持久状态
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseBench
{
    internal static class GainBench2133
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: GainBench2133 memshield|cachewarm|adaptive|victim|hog");
                return 2;
            }
            try
            {
                switch (args[0])
                {
                    case "memshield": return MemShieldBench();
                    case "cachewarm": return CacheWarmBench();
                    case "adaptive": return AdaptiveBench();
                    case "selfcost": return SelfCostBench();
                    case "victim": return VictimMain(args);
                    case "hog": return HogMain(args);
                    default: Console.Error.WriteLine("unknown: " + args[0]); return 2;
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL " + error);
                return 1;
            }
        }

        private static string Self { get { return Process.GetCurrentProcess().MainModule.FileName; } }

        // ===================== 通用互操作 =====================

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, IntPtr min, IntPtr max, uint flags);
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr process);
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint cb);
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr process, int cls, ref PowerThrottlingState state, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessPriorityBoost(IntPtr process, bool disable);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr file, IntPtr buffer, uint count, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [StructLayout(LayoutKind.Sequential)]
        private struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValueW(string system, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returned);

        private static void TryEnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0028, out token)) return;
            try
            {
                Luid luid;
                if (!LookupPrivilegeValueW(null, name, out luid)) return;
                var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x00000002 };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(token); }
        }

        private static ulong AvailPhys()
        {
            var st = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            GlobalMemoryStatusEx(ref st);
            return st.AvailPhys;
        }

        private static ProcessMemoryCounters MemOf(Process p)
        {
            ProcessMemoryCounters c;
            GetProcessMemoryInfo(p.Handle, out c, (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters)));
            return c;
        }

        private static string Mb(ulong bytes) { return (bytes / 1048576.0).ToString("F0", CultureInfo.InvariantCulture) + "MB"; }

        // ===================== memshield =====================
        // 与 MemShield 相同的旋钮 QUOTA_LIMITS_HARDWS_MIN_ENABLE|QUOTA_LIMITS_HARDWS_MAX_DISABLE
        // 两臂各一个受害者子进程 触碰 1200MB 私有页后 先裁剪工作集 再施加有界内存压力
        // 压力吃掉被裁页的待机份额 逼它们真的换出 然后量重触耗时与缺页数

        private const int VictimMb = 1200;

        private static int MemShieldBench()
        {
            TryEnablePrivilege("SeIncreaseWorkingSetPrivilege");
            TryEnablePrivilege("SeIncreaseBasePriorityPrivilege");
            ulong avail = AvailPhys();
            long pressureMb = Math.Min(20480L, (long)(avail / 1048576) - 10240);
            Console.WriteLine("# memshield  victim=" + VictimMb + "MB  avail=" + Mb(avail)
                + "  pressure=" + pressureMb + "MB (为你的工作预留 10GB)");
            if (pressureMb < 4096)
                Console.WriteLine("# 警告 可用内存不足 压力低于 4GB 结论置信度下降");

            var r0 = MemShieldArm(false, pressureMb);
            var r1 = MemShieldArm(true, pressureMb);
            Console.WriteLine();
            Console.WriteLine("裁剪后工作集    未锁 " + Mb(r0.WsAfterTrim) + "   锁定 " + Mb(r1.WsAfterTrim));
            Console.WriteLine("压力后重触耗时  未锁 " + r0.RetouchMs + "ms   锁定 " + r1.RetouchMs + "ms");
            Console.WriteLine("重触新增缺页    未锁 " + r0.RetouchFaults + "   锁定 " + r1.RetouchFaults);
            bool held = r1.WsAfterTrim > (ulong)VictimMb * 1048576 * 9 / 10;
            Console.WriteLine("硬下限真的挡住裁剪: " + (held ? "是" : "否 (锁没落地或被系统忽略)"));
            return 0;
        }

        private sealed class MemArmResult { public ulong WsAfterTrim; public long RetouchMs; public uint RetouchFaults; }

        private static MemArmResult MemShieldArm(bool locked, long pressureMb)
        {
            Console.WriteLine("== " + (locked ? "锁定臂 (护盾开)" : "对照臂 (护盾关)"));
            var result = new MemArmResult();
            Process victim = StartChild("victim " + VictimMb);
            try
            {
                Expect(victim, "READY");
                if (locked)
                {
                    long min = (long)VictimMb * 1048576 + (64L << 20);
                    // 0x1 HARDWS_MIN_ENABLE  0x8 HARDWS_MAX_DISABLE 与正式代码同一组合
                    bool ok = SetProcessWorkingSetSizeEx(victim.Handle, new IntPtr(min), new IntPtr(min + (256L << 20)), 0x1 | 0x8);
                    Console.WriteLine("  施加硬下限 " + Mb((ulong)min) + " -> " + (ok ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));
                }
                EmptyWorkingSet(victim.Handle);
                Thread.Sleep(500);
                result.WsAfterTrim = MemOf(victim).WorkingSetSize.ToUInt64();
                Console.WriteLine("  裁剪后工作集 " + Mb(result.WsAfterTrim));

                var pressure = new List<byte[]>();
                var sw = Stopwatch.StartNew();
                for (long done = 0; done < pressureMb; done += 256)
                {
                    var block = new byte[256 << 20];
                    for (int i = 0; i < block.Length; i += 4096) block[i] = 1;
                    pressure.Add(block);
                }
                Console.WriteLine("  压力 " + pressureMb + "MB 就位 " + sw.ElapsedMilliseconds + "ms");
                Thread.Sleep(2000);

                uint faultsBefore = MemOf(victim).PageFaultCount;
                victim.StandardInput.WriteLine("touch");
                string line = victim.StandardOutput.ReadLine();
                result.RetouchMs = long.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture);
                result.RetouchFaults = MemOf(victim).PageFaultCount - faultsBefore;
                Console.WriteLine("  压力下重触 " + result.RetouchMs + "ms  新增缺页 " + result.RetouchFaults);
                pressure.Clear();
                GC.Collect(2, GCCollectionMode.Forced, true);
            }
            finally { try { victim.StandardInput.WriteLine("quit"); victim.WaitForExit(3000); } catch { } try { if (!victim.HasExited) victim.Kill(); } catch { } }
            Thread.Sleep(1500);
            return result;
        }

        private static int VictimMain(string[] args)
        {
            int mb = int.Parse(args[1], CultureInfo.InvariantCulture);
            var buffer = new byte[(long)mb << 20];
            for (long i = 0; i < buffer.LongLength; i += 4096) buffer[i] = 1;
            Console.WriteLine("READY");
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (line == "quit") break;
                if (line != "touch") continue;
                var sw = Stopwatch.StartNew();
                long acc = 0;
                for (long i = 0; i < buffer.LongLength; i += 4096) acc += buffer[i];
                Console.WriteLine("TOUCHED " + sw.ElapsedMilliseconds + " " + acc);
            }
            GC.KeepAlive(buffer);
            return 0;
        }

        // ===================== cachewarm =====================
        // 两套等大的文件集都用无缓冲写落盘 保证不在缓存里
        // A 集直接计时读(冷读 = 不预热的首次加载)
        // B 集先按正式代码的口径预热(64KB 顺序 后台线程模式) 再计时读
        // 差值就是预热对后续读取的实际收益

        private const int WarmFiles = 6;
        private const int WarmFileMb = 256;

        private static int CacheWarmBench()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PaviseGainBench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Console.WriteLine("# cachewarm  " + WarmFiles + "x" + WarmFileMb + "MB x2 套  目录 " + dir);
            try
            {
                string[] setA = WriteColdSet(dir, "a");
                string[] setB = WriteColdSet(dir, "b");

                long coldMs = TimedRead(setA);
                Console.WriteLine("冷读 A 集(不预热的加载)      " + coldMs + "ms");

                var sw = Stopwatch.StartNew();
                var warmThread = new Thread(delegate ()
                {
                    // 0x00010000 THREAD_MODE_BACKGROUND_BEGIN 与礼貌预读同一姿态
                    SetThreadPriority(GetCurrentThread(), 0x00010000);
                    var chunk = new byte[64 * 1024];
                    foreach (string path in setB)
                        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                            while (s.Read(chunk, 0, chunk.Length) > 0) { }
                    SetThreadPriority(GetCurrentThread(), 0x00020000);
                });
                warmThread.Start(); warmThread.Join();
                Console.WriteLine("预热 B 集(64KB 顺序 后台模式) " + sw.ElapsedMilliseconds + "ms");

                long warmMs = TimedRead(setB);
                Console.WriteLine("预热后读 B 集(命中待机缓存)   " + warmMs + "ms");
                Console.WriteLine();
                Console.WriteLine("加载耗时 冷读 " + coldMs + "ms -> 预热后 " + warmMs + "ms  ("
                    + (coldMs > 0 ? (100 - warmMs * 100 / coldMs) : 0) + "% 降幅)");
                long total = (long)WarmFiles * WarmFileMb;
                Console.WriteLine("折算带宽 冷 " + (coldMs > 0 ? total * 1000 / coldMs : 0)
                    + "MB/s -> 热 " + (warmMs > 0 ? total * 1000 / warmMs : 0) + "MB/s");
                return 0;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static string[] WriteColdSet(string dir, string tag)
        {
            var rng = new Random(20260901);
            var block = new byte[1 << 20];
            rng.NextBytes(block);
            IntPtr native = VirtualAlloc(IntPtr.Zero, new UIntPtr((uint)block.Length), 0x3000, 0x04);
            Marshal.Copy(block, 0, native, block.Length);
            var paths = new string[WarmFiles];
            try
            {
                for (int f = 0; f < WarmFiles; f++)
                {
                    paths[f] = Path.Combine(dir, tag + f + ".bin");
                    // FILE_FLAG_NO_BUFFERING|WRITE_THROUGH 写入不经过缓存 保证随后是真正的冷读
                    IntPtr h = CreateFileW(paths[f], 0x40000000, 0, IntPtr.Zero, 2, 0x20000000 | 0x80000000, IntPtr.Zero);
                    if (h == new IntPtr(-1)) throw new IOException("create " + paths[f] + " err=" + Marshal.GetLastWin32Error());
                    try
                    {
                        for (int m = 0; m < WarmFileMb; m++)
                        {
                            uint written;
                            if (!WriteFile(h, native, (uint)block.Length, out written, IntPtr.Zero) || written != block.Length)
                                throw new IOException("write err=" + Marshal.GetLastWin32Error());
                        }
                    }
                    finally { CloseHandle(h); }
                }
            }
            finally { VirtualFree(native, UIntPtr.Zero, 0x8000); }
            return paths;
        }

        private static long TimedRead(string[] paths)
        {
            var chunk = new byte[1 << 20];
            var sw = Stopwatch.StartNew();
            foreach (string path in paths)
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
                    while (s.Read(chunk, 0, chunk.Length) > 0) { }
            return sw.ElapsedMilliseconds;
        }

        // ===================== adaptive =====================
        // 前台=本进程 6 线程定量算子 记每秒完成量与 p99 单量耗时
        // 后台=2 个子进程各 16 线程满载 两臂分别为
        //   A 后台不压(智能档对有窗口后台的原始待遇)
        //   B 后台按隔离档旋钮压(升档后的待遇) IDLE 优先级 + EcoQoS + 关睿频
        // 差值就是升档能追回的余量上限

        private const int GameThreads = 6;
        private const int HogProcs = 2;
        private const int HogThreadsEach = 16;
        private const int MeasureSeconds = 10;

        private static int AdaptiveBench()
        {
            Console.WriteLine("# adaptive  前台 " + GameThreads + " 线程  后台 " + HogProcs + "x" + HogThreadsEach
                + " 线程满载  每臂预热 2s + 测量 " + MeasureSeconds + "s  (期间整机满载约 25s)");
            double calib = Quantum();
            var a = AdaptiveArm(false);
            var b = AdaptiveArm(true);
            GC.KeepAlive(calib);
            Console.WriteLine();
            Console.WriteLine("前台吞吐(量子/s)  后台不压 " + a.Throughput.ToString("F0") + "   隔离档 " + b.Throughput.ToString("F0")
                + "   (+" + ((b.Throughput / Math.Max(1, a.Throughput) - 1) * 100).ToString("F0") + "%)");
            Console.WriteLine("单量耗时 p50      后台不压 " + a.P50.ToString("F2") + "ms  隔离档 " + b.P50.ToString("F2") + "ms");
            Console.WriteLine("单量耗时 p99      后台不压 " + a.P99.ToString("F2") + "ms  隔离档 " + b.P99.ToString("F2") + "ms"
                + "   (" + ((1 - b.P99 / Math.Max(0.001, a.P99)) * 100).ToString("F0") + "% 降幅)");
            return 0;
        }

        private sealed class AdaptiveResult { public double Throughput, P50, P99; }

        private static AdaptiveResult AdaptiveArm(bool isolate)
        {
            Console.WriteLine("== " + (isolate ? "隔离臂 (升档待遇)" : "对照臂 (后台不压)"));
            var hogs = new List<Process>();
            try
            {
                for (int i = 0; i < HogProcs; i++)
                {
                    Process hog = StartChild("hog " + HogThreadsEach);
                    hogs.Add(hog);
                    if (isolate)
                    {
                        hog.PriorityClass = ProcessPriorityClass.Idle;
                        var throttling = new PowerThrottlingState { Version = 1, ControlMask = 1, StateMask = 1 };
                        SetProcessInformation(hog.Handle, 4, ref throttling, Marshal.SizeOf(typeof(PowerThrottlingState)));
                        SetProcessPriorityBoost(hog.Handle, true);
                    }
                }
                Thread.Sleep(2000);

                var times = new List<double>[GameThreads];
                var stop = new ManualResetEvent(false);
                var workers = new Thread[GameThreads];
                for (int t = 0; t < GameThreads; t++)
                {
                    int idx = t;
                    times[idx] = new List<double>(20000);
                    workers[idx] = new Thread(delegate ()
                    {
                        var mine = times[idx];
                        while (!stop.WaitOne(0))
                        {
                            var sw = Stopwatch.StartNew();
                            Quantum();
                            mine.Add(sw.Elapsed.TotalMilliseconds);
                        }
                    });
                    workers[idx].Start();
                }
                Thread.Sleep(MeasureSeconds * 1000);
                stop.Set();
                foreach (Thread w in workers) w.Join();

                var all = new List<double>();
                foreach (var list in times) all.AddRange(list);
                all.Sort();
                var result = new AdaptiveResult
                {
                    Throughput = (double)all.Count / MeasureSeconds,
                    P50 = all.Count > 0 ? all[all.Count / 2] : 0,
                    P99 = all.Count > 0 ? all[(int)(all.Count * 0.99)] : 0
                };
                Console.WriteLine("  量子数 " + all.Count + "  吞吐 " + result.Throughput.ToString("F0")
                    + "/s  p50 " + result.P50.ToString("F2") + "ms  p99 " + result.P99.ToString("F2") + "ms");
                return result;
            }
            finally
            {
                foreach (Process hog in hogs) { try { hog.Kill(); } catch { } }
                Thread.Sleep(1000);
            }
        }

        // ===================== selfcost =====================
        // 守护默认路径的每个原语在本机的真实开销 与正式代码同机制
        //   进程快照 NtQuerySystemInformation 单调用
        //   可见窗口 EnumWindows 全枚举
        //   巡检单元 OpenProcess+查优先级+关句柄
        //   CPU 饱和 GetSystemTimes
        //   显存溢出 PDH GPU Process Memory 通配收集
        // 按对局节奏折算成每秒微秒预算

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int length, out int required);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lparam);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        private static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string source, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        private static double TimeUs(int rounds, Action action)
        {
            action();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < rounds; i++) action();
            return sw.Elapsed.TotalMilliseconds * 1000.0 / rounds;
        }

        private static int SelfCostBench()
        {
            Console.WriteLine("# selfcost  守护默认路径原语实测  进程数 " + Process.GetProcesses().Length);

            IntPtr buffer = Marshal.AllocHGlobal(4 << 20);
            double snap = TimeUs(200, delegate
            {
                int required;
                NtQuerySystemInformation(5, buffer, 4 << 20, out required);
            });
            Marshal.FreeHGlobal(buffer);

            double windows = TimeUs(200, delegate
            {
                var pids = new HashSet<uint>();
                EnumWindows(delegate(IntPtr hwnd, IntPtr state)
                {
                    if (IsWindowVisible(hwnd)) { uint pid; GetWindowThreadProcessId(hwnd, out pid); pids.Add(pid); }
                    return true;
                }, IntPtr.Zero);
            });

            int self = Process.GetCurrentProcess().Id;
            double reconcile = TimeUs(500, delegate
            {
                IntPtr h = OpenProcess(0x1000, false, self);
                if (h != IntPtr.Zero) { GetPriorityClass(h); CloseHandle(h); }
            });

            double sysTimes = TimeUs(2000, delegate
            {
                long i2, k, u;
                GetSystemTimes(out i2, out k, out u);
            });

            IntPtr query, counter;
            double pdh = -1;
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) == 0)
            {
                if (PdhAddEnglishCounterW(query, "\\GPU Process Memory(*)\\Shared Usage", IntPtr.Zero, out counter) == 0)
                {
                    PdhCollectQueryData(query);
                    pdh = TimeUs(20, delegate { PdhCollectQueryData(query); });
                }
                PdhCloseQuery(query);
            }

            Console.WriteLine();
            Console.WriteLine("进程快照(NtQSI 单调用)      " + snap.ToString("F1") + " us");
            Console.WriteLine("可见窗口枚举(EnumWindows)    " + windows.ToString("F1") + " us");
            Console.WriteLine("巡检单元(开句柄查优先级关)   " + reconcile.ToString("F1") + " us");
            Console.WriteLine("CPU 饱和采样(GetSystemTimes) " + sysTimes.ToString("F2") + " us");
            Console.WriteLine("显存溢出 PDH 单次收集        " + (pdh < 0 ? "不可用" : pdh.ToString("F0") + " us"));
            Console.WriteLine();
            // 轮询模式对局中 500ms 一轮 事件模式 1000ms 巡检按 74 个被压进程 4s 起的退避摊到每秒约 5 个
            double perTick = snap + windows + sysTimes * 2;
            double budget500 = perTick * 2 + reconcile * 5 + (pdh > 0 ? pdh / 20 : 0) + (pdh > 0 ? pdh / 15 : 0);
            double budget1000 = perTick + reconcile * 5 + (pdh > 0 ? pdh / 20 : 0) + (pdh > 0 ? pdh / 15 : 0);
            Console.WriteLine("每秒预算 轮询模式(500ms 轮)  " + budget500.ToString("F0") + " us/s = 单核 "
                + (budget500 / 10000).ToString("F3") + "%");
            Console.WriteLine("每秒预算 事件模式(1000ms 轮) " + budget1000.ToString("F0") + " us/s = 单核 "
                + (budget1000 / 10000).ToString("F3") + "%");
            Console.WriteLine("(不含 Sweep/Boost 纯计算部分 那是内存内逻辑 量级远小于系统调用)");
            return 0;
        }

        private static double Quantum()
        {
            ulong x = 88172645463325252UL;
            for (int i = 0; i < 400000; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; }
            return x;
        }

        private static int HogMain(string[] args)
        {
            int n = int.Parse(args[1], CultureInfo.InvariantCulture);
            for (int i = 0; i < n; i++)
                new Thread(delegate () { while (true) Quantum(); }) { IsBackground = true }.Start();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        private static Process StartChild(string args)
        {
            var info = new ProcessStartInfo(Self, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };
            return Process.Start(info);
        }

        private static void Expect(Process p, string token)
        {
            string line = p.StandardOutput.ReadLine();
            if (line != token) throw new InvalidOperationException("期待 " + token + " 得到 " + line);
        }
    }
}
