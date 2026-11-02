// Bench probe: samples Pavise's own CPU cost at a fine interval and reads back what Pavise applied to the
// bench game and the noise processes. Read-only: it never writes to any process. C# 5 (csc v4).
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
    internal static class BenchProbe
    {
        [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int length, out int needed);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref int value, int length, IntPtr ret);
        [DllImport("ntdll.dll")] private static extern int NtQueryTimerResolution(out uint min, out uint max, out uint current);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, int tid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr process, out UIntPtr system);
        [DllImport("kernel32.dll")] private static extern bool GetProcessPriorityBoost(IntPtr h, out bool disabled);
        [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] private static extern bool GetProcessInformation(IntPtr h, int cls, ref PowerThrottling info, int size);
        [StructLayout(LayoutKind.Sequential)] private struct GroupAffinity { public UIntPtr Mask; public ushort Group, R0, R1, R2; }
        [DllImport("kernel32.dll")] private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
        [DllImport("kernel32.dll")] private static extern int GetThreadPriority(IntPtr thread);
        [DllImport("kernel32.dll")] private static extern bool GetThreadSelectedCpuSets(IntPtr thread, uint[] ids, uint count, out uint required);
        [DllImport("gdi32.dll")] private static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr process, out int priority);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
        [DllImport("kernel32.dll")] private static extern bool QueryProcessCycleTime(IntPtr h, out ulong cycles);
        [DllImport("kernel32.dll")] private static extern bool QueryThreadCycleTime(IntPtr h, out ulong cycles);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();

        private const uint QueryLimited = 0x1000, QueryInformation = 0x0400, ThreadQueryLimited = 0x0800;

        private sealed class ThreadSample { public long Cpu; public uint Switches; public int Priority; public ulong Cycles; }
        private sealed class ProcSample
        {
            public int Pid; public string Name; public long Cpu; public long Create; public ulong Cycles; public int Threads, Handles, Base; public long WorkingSet; public uint Switches;
            public readonly Dictionary<int, ThreadSample> ThreadTimes = new Dictionary<int, ThreadSample>();
        }

        private static string targetName = "BenchGame";
        private static readonly Dictionary<int, IntPtr> threadHandles = new Dictionary<int, IntPtr>();
        private static ulong ThreadCycles(int tid)
        {
            IntPtr h;
            if (!threadHandles.TryGetValue(tid, out h)) { h = OpenThread(ThreadQueryLimited, false, tid); threadHandles[tid] = h; }
            ulong c; return h != IntPtr.Zero && QueryThreadCycleTime(h, out c) ? c : 0;
        }
        private static IntPtr buffer = IntPtr.Zero;
        private static int bufferSize = 1 << 20;

        private static List<ProcSample> Snapshot(HashSet<string> names)
        {
            var result = new List<ProcSample>();
            int needed;
            for (;;)
            {
                if (buffer == IntPtr.Zero) buffer = Marshal.AllocHGlobal(bufferSize);
                int status = NtQuerySystemInformation(5, buffer, bufferSize, out needed);
                if (status == unchecked((int)0xC0000004)) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; bufferSize = Math.Max(bufferSize * 2, needed + 65536); continue; }
                if (status != 0) return result;
                break;
            }
            long offset = 0;
            for (;;)
            {
                IntPtr p = new IntPtr(buffer.ToInt64() + offset);
                int next = Marshal.ReadInt32(p, 0);
                int threads = Marshal.ReadInt32(p, 4);
                int nameLength = Marshal.ReadInt16(p, 0x38);
                IntPtr nameBuffer = Marshal.ReadIntPtr(p, 0x40);
                string name = nameBuffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(nameBuffer, nameLength / 2);
                string bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
                if (names.Contains(bare))
                {
                    var s = new ProcSample();
                    s.Name = bare;
                    s.Create = Marshal.ReadInt64(p, 0x20);
                    s.Cycles = (ulong)Marshal.ReadInt64(p, 0x18);
                    s.Cpu = Marshal.ReadInt64(p, 0x28) + Marshal.ReadInt64(p, 0x30);
                    s.Base = Marshal.ReadInt32(p, 0x48);
                    s.Pid = (int)Marshal.ReadIntPtr(p, 0x50).ToInt64();
                    s.Handles = Marshal.ReadInt32(p, 0x60);
                    s.WorkingSet = Marshal.ReadIntPtr(p, 0x90).ToInt64();
                    s.Threads = threads;
                    for (int i = 0; i < threads; i++)
                    {
                        IntPtr t = new IntPtr(p.ToInt64() + 0x100 + i * 0x50);
                        var ts = new ThreadSample();
                        ts.Cpu = Marshal.ReadInt64(t, 0) + Marshal.ReadInt64(t, 8);
                        int tid = (int)Marshal.ReadIntPtr(t, 0x30).ToInt64();
                        ts.Priority = Marshal.ReadInt32(t, 0x38);
                        ts.Switches = (uint)Marshal.ReadInt32(t, 0x40);
                        s.Switches += ts.Switches;
                        if (!string.Equals(bare, targetName, StringComparison.OrdinalIgnoreCase)) ts.Cycles = ThreadCycles(tid);
                        s.ThreadTimes[tid] = ts;
                    }
                    result.Add(s);
                }
                if (next == 0) break;
                offset += (uint)next;
            }
            return result;
        }

        private static string Attributes(int pid)
        {
            IntPtr h = OpenProcess(QueryLimited | QueryInformation, false, pid);
            bool full = h != IntPtr.Zero;
            if (h == IntPtr.Zero) h = OpenProcess(QueryLimited, false, pid);
            if (h == IntPtr.Zero) return "open_failed=" + Marshal.GetLastWin32Error();
            try
            {
                var sb = new StringBuilder();
                sb.Append("priority=0x").Append(GetPriorityClass(h).ToString("X"));
                UIntPtr pm, sm; if (GetProcessAffinityMask(h, out pm, out sm)) sb.Append(" affinity=0x").Append(pm.ToUInt64().ToString("X"));
                bool noBoost; if (GetProcessPriorityBoost(h, out noBoost)) sb.Append(" boost_disabled=").Append(noBoost ? 1 : 0);
                int io = -1; if (full && NtQueryInformationProcess(h, 33, ref io, 4, IntPtr.Zero) == 0) sb.Append(" io=").Append(io);
                int page = -1; if (full && NtQueryInformationProcess(h, 39, ref page, 4, IntPtr.Zero) == 0) sb.Append(" page=").Append(page);
                var pt = new PowerThrottling { Version = 1 };
                if (GetProcessInformation(h, 4, ref pt, Marshal.SizeOf(typeof(PowerThrottling)))) sb.Append(" eco_control=").Append(pt.ControlMask).Append(" eco_state=").Append(pt.StateMask);
                int gpu; if (D3DKMTGetProcessSchedulingPriorityClass(h, out gpu) == 0) sb.Append(" gpu=").Append(gpu);
                return sb.ToString();
            }
            finally { CloseHandle(h); }
        }

        private static string ThreadAttributes(int tid)
        {
            IntPtr h = OpenThread(ThreadQueryLimited, false, tid);
            if (h == IntPtr.Zero) return "open_failed";
            try
            {
                var sb = new StringBuilder();
                GroupAffinity a; if (GetThreadGroupAffinity(h, out a)) sb.Append("mask=0x").Append(a.Mask.ToUInt64().ToString("X"));
                sb.Append(" prio=").Append(GetThreadPriority(h));
                uint required; GetThreadSelectedCpuSets(h, null, 0, out required); sb.Append(" cpusets=").Append(required);
                return sb.ToString();
            }
            finally { CloseHandle(h); }
        }

        private static string PowerScheme()
        {
            IntPtr g; if (PowerGetActiveScheme(IntPtr.Zero, out g) != 0) return "unknown";
            try { return ((Guid)Marshal.PtrToStructure(g, typeof(Guid))).ToString(); } finally { LocalFree(g); }
        }

        private static int Main(string[] args)
        {
            string output = null, pavise = "Pavise", target = "BenchGame", noise = "BenchNoise";
            int intervalMs = 25; double seconds = 60;
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                if (args[i] == "--out") output = args[i + 1];
                else if (args[i] == "--interval-ms") intervalMs = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                else if (args[i] == "--seconds") seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
                else if (args[i] == "--target") { target = args[i + 1]; targetName = target; }
            }
            if (output == null) { Console.Error.WriteLine("--out required"); return 2; }
            Directory.CreateDirectory(output);
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            ulong c0, c1; QueryThreadCycleTime(GetCurrentThread(), out c0); var spin = Stopwatch.StartNew(); while (spin.ElapsedMilliseconds < 300) { } QueryThreadCycleTime(GetCurrentThread(), out c1);
            double cyclesPerMs = (c1 - c0) / spin.Elapsed.TotalMilliseconds;
            File.WriteAllText(Path.Combine(output, "probe-meta.tsv"), "cycles_per_ms\t" + cyclesPerMs.ToString("F0", CultureInfo.InvariantCulture) + "\nqpc_frequency\t" + Stopwatch.Frequency + "\nelevated\t" + (OpenProcess(QueryLimited, false, Process.GetCurrentProcess().Id) != IntPtr.Zero ? 1 : 0) + "\n");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pavise, target, noise, "Pavise.HardThreadGuardian" };
            string stopFlag = Path.Combine(output, "probe-stop.flag");
            var inv = CultureInfo.InvariantCulture;
            using (var procs = new StreamWriter(Path.Combine(output, "probe-process.csv"), false, new UTF8Encoding(false)))
            using (var threads = new StreamWriter(Path.Combine(output, "probe-threads.csv"), false, new UTF8Encoding(false)))
            using (var attrs = new StreamWriter(Path.Combine(output, "probe-attributes.tsv"), false, new UTF8Encoding(false)))
            {
                procs.WriteLine("qpc,name,pid,cpu_delta_100ns,threads,handles,working_set,switches_delta,base_priority,cycles_delta");
                threads.WriteLine("qpc,name,pid,tid,cpu_delta_100ns,switches_delta,priority,cycles_delta");
                var previous = new Dictionary<int, ProcSample>();
                var watch = Stopwatch.StartNew();
                long nextAttributes = 0;
                while (watch.Elapsed.TotalSeconds < seconds && !File.Exists(stopFlag))
                {
                    long qpc = Stopwatch.GetTimestamp();
                    List<ProcSample> now = Snapshot(names);
                    var seen = new Dictionary<int, ProcSample>();
                    foreach (ProcSample s in now)
                    {
                        seen[s.Pid] = s;
                        ProcSample old;
                        if (!previous.TryGetValue(s.Pid, out old) || old.Create != s.Create) continue;
                        long delta = s.Cpu - old.Cpu;
                        procs.WriteLine(string.Format(inv, "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}", qpc, s.Name, s.Pid, delta, s.Threads, s.Handles, s.WorkingSet, s.Switches - old.Switches, s.Base, s.Cycles - old.Cycles));
                        foreach (var kv in s.ThreadTimes)
                        {
                            ThreadSample before;
                            if (!old.ThreadTimes.TryGetValue(kv.Key, out before)) continue;
                            long d = kv.Value.Cpu - before.Cpu; uint sw = kv.Value.Switches - before.Switches; ulong cy = kv.Value.Cycles >= before.Cycles ? kv.Value.Cycles - before.Cycles : 0;
                            // Every Pavise/guardian thread that ran; the game only when a thread used >= 2 ms.
                            bool own = !string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase);
                            if ((own && (d > 0 || sw > 0 || cy > 0)) || d >= 20000)
                                threads.WriteLine(string.Format(inv, "{0},{1},{2},{3},{4},{5},{6},{7}", qpc, s.Name, s.Pid, kv.Key, d, sw, kv.Value.Priority, cy));
                        }
                    }
                    previous = seen;
                    if (watch.ElapsedMilliseconds >= nextAttributes)
                    {
                        nextAttributes = watch.ElapsedMilliseconds + 1000;
                        uint min, max, cur; NtQueryTimerResolution(out min, out max, out cur);
                        attrs.WriteLine("qpc\t" + qpc + "\ttimer_100ns\t" + cur + "\tpower\t" + PowerScheme());
                        foreach (ProcSample s in now)
                        {
                            if (s.Name.StartsWith("Pavise", StringComparison.OrdinalIgnoreCase)) continue;
                            attrs.WriteLine("qpc\t" + qpc + "\tproc\t" + s.Name + "\tpid\t" + s.Pid + "\t" + Attributes(s.Pid));
                            if (!string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase)) continue;
                            // The busiest threads of the game: the render thread binding shows up here.
                            var busiest = new List<KeyValuePair<int, long>>();
                            ProcSample old;
                            if (previous.TryGetValue(s.Pid, out old))
                                foreach (var kv in s.ThreadTimes) busiest.Add(new KeyValuePair<int, long>(kv.Key, kv.Value.Cpu));
                            busiest.Sort(delegate(KeyValuePair<int, long> a, KeyValuePair<int, long> b) { return b.Value.CompareTo(a.Value); });
                            for (int i = 0; i < Math.Min(4, busiest.Count); i++)
                                attrs.WriteLine("qpc\t" + qpc + "\tthread\t" + s.Name + "\ttid\t" + busiest[i].Key + "\tcpu_total_100ns\t" + busiest[i].Value + "\t" + ThreadAttributes(busiest[i].Key));
                        }
                        procs.Flush(); threads.Flush(); attrs.Flush();
                    }
                    long elapsed = (Stopwatch.GetTimestamp() - qpc) * 1000 / Stopwatch.Frequency;
                    int sleep = (int)Math.Max(1, intervalMs - elapsed);
                    Thread.Sleep(sleep);
                }
            }
            File.WriteAllText(Path.Combine(output, "probe-done.flag"), "done");
            return 0;
        }
    }
}
