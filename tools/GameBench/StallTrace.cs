// Reads an NT Kernel Logger trace (process, thread, img, cswitch, dpc, isr; perf clock) and reports, for the busiest
// thread of one process, every interval where it was runnable or preempted but not running for longer than a threshold,
// together with what ran on that processor meanwhile: threads by process, and DPC/ISR time by driver.
// Usage: StallTrace <trace.etl> <processName> [minStallMs=3] [out.tsv]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

internal static class StallTrace
{
    [StructLayout(LayoutKind.Sequential)] private struct EventDescriptor { public ushort Id; public byte Version, Channel, Level, Opcode; public ushort Task; public ulong Keyword; }
    [StructLayout(LayoutKind.Sequential)] private struct EventHeader
    { public ushort Size, HeaderType, Flags, EventProperty; public uint ThreadId, ProcessId; public long TimeStamp; public Guid ProviderId; public EventDescriptor Descriptor; public ulong ProcessorTime; public Guid ActivityId; }
    [StructLayout(LayoutKind.Sequential)] private struct EtwBufferContext { public byte ProcessorNumber, Alignment; public ushort LoggerId; }
    [StructLayout(LayoutKind.Sequential)] private struct EventRecord
    { public EventHeader Header; public EtwBufferContext BufferContext; public ushort ExtendedDataCount, UserDataLength; public IntPtr ExtendedData, UserData, UserContext; }
    private delegate void EventRecordCallback(ref EventRecord record);
    private delegate uint BufferCallback(IntPtr logfile);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TimeZoneInformation
    { public int Bias; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName; public SystemTime StandardDate; public int StandardBias;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName; public SystemTime DaylightDate; public int DaylightBias; }
    [StructLayout(LayoutKind.Sequential)] private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential)] private struct TraceLogfileHeader
    { public uint BufferSize; public uint Version; public uint ProviderVersion, NumberOfProcessors; public long EndTime; public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten, StartBuffers, PointerSize, EventsLost, CpuSpeedInMHz;
      public IntPtr LoggerName, LogFileName; public TimeZoneInformation TimeZone; public long BootTime, PerfFreq, StartTime; public uint ReservedFlags, BuffersLost; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct EventTraceLogfile
    { [MarshalAs(UnmanagedType.LPWStr)] public string LogFileName; [MarshalAs(UnmanagedType.LPWStr)] public string LoggerName; public long CurrentTime; public uint BuffersRead, ProcessTraceMode;
      public EventTrace CurrentEvent; public TraceLogfileHeader LogfileHeader; public BufferCallback BufferCallback; public uint BufferSize, Filled, EventsLost; public EventRecordCallback EventRecordCallback; public uint IsKernelTrace; public IntPtr Context; }
    [StructLayout(LayoutKind.Sequential)] private struct EventTrace { public EventTraceHeader Header; public uint InstanceId, ParentInstanceId; public Guid ParentGuid; public IntPtr MofData; public uint MofLength, ClientContext; }
    [StructLayout(LayoutKind.Sequential)] private struct EventTraceHeader { public ushort Size; public byte HeaderType, MarkerFlags; public uint Version; public uint ThreadId, ProcessId; public long TimeStamp; public Guid Guid; public uint KernelTime, UserTime; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ulong OpenTraceW(ref EventTraceLogfile logfile);
    [DllImport("advapi32.dll")] private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);

    private static readonly Guid ThreadGuid = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
    private static readonly Guid ProcessGuid = new Guid("3d6fa8d0-fe05-11d0-9dda-00c04fd7ba7c");
    private static readonly Guid PerfInfoGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
    private static readonly Guid ImageGuid = new Guid("2cb15d1d-5fc1-11d2-abe1-00a0c911f518");

    private sealed class Slice { public long Start, End; public int Tid; public string What; }
    private sealed class Image { public ulong Base, Size; public string Name; }
    private static readonly Dictionary<int, int> threadProcess = new Dictionary<int, int>();
    private static readonly Dictionary<int, string> processName = new Dictionary<int, string>();
    private static readonly List<Image> kernelImages = new List<Image>();
    private static readonly Dictionary<int, List<Slice>> running = new Dictionary<int, List<Slice>>();     // cpu -> thread slices
    private static readonly Dictionary<int, List<Slice>> interrupts = new Dictionary<int, List<Slice>>();  // cpu -> dpc/isr
    private static readonly Dictionary<int, long> sliceStart = new Dictionary<int, long>();
    private static readonly Dictionary<int, int> sliceTid = new Dictionary<int, int>();
    private struct Switch { public long At; public int Cpu, NewTid, OldTid; public sbyte OldState, OldWaitReason; }
    private static readonly List<Switch> switches = new List<Switch>();
    private static readonly List<Tuple<long, ulong, int, string>> pendingRoutines = new List<Tuple<long, ulong, int, string>>();
    private static long perfFreq;
    private static readonly EventRecordCallback callback = OnEvent;
    private static readonly Dictionary<int, int> imageOps = new Dictionary<int, int>();
    private static readonly Dictionary<int, string> providerNames = new Dictionary<int, string>();

    private static string ReadAnsi(IntPtr p, int max, out int used)
    { var sb = new StringBuilder(); used = 0; while (used < max) { byte b = Marshal.ReadByte(p, used++); if (b == 0) break; sb.Append((char)b); } return sb.ToString(); }
    private static string ReadUnicode(IntPtr p, int max)
    { var sb = new StringBuilder(); for (int i = 0; i + 1 < max; i += 2) { char c = (char)Marshal.ReadInt16(p, i); if (c == 0) break; sb.Append(c); } return sb.ToString(); }

    private static void OnEvent(ref EventRecord e)
    {
        Guid g = e.Header.ProviderId; byte op = e.Header.Descriptor.Opcode; IntPtr d = e.UserData; int len = e.UserDataLength;
        long ts = e.Header.TimeStamp; int cpu = e.BufferContext.ProcessorNumber;
        if (g != ThreadGuid && g != PerfInfoGuid)
        { int key = (g.GetHashCode() & 0x7FFF0000) | op; int seen; imageOps.TryGetValue(key, out seen); imageOps[key] = seen + 1; if (seen == 0) providerNames[key] = g + "/" + op + "/len" + len; }
        try
        {
            if (g == ThreadGuid && op == 36 && len >= 24)
            {
                var s = new Switch { At = ts, Cpu = cpu, NewTid = Marshal.ReadInt32(d, 0), OldTid = Marshal.ReadInt32(d, 4),
                    OldWaitReason = (sbyte)Marshal.ReadByte(d, 12), OldState = (sbyte)Marshal.ReadByte(d, 14) };
                switches.Add(s);
                long begun; int was;
                if (sliceStart.TryGetValue(cpu, out begun) && sliceTid.TryGetValue(cpu, out was))
                {
                    List<Slice> list; if (!running.TryGetValue(cpu, out list)) running[cpu] = list = new List<Slice>();
                    list.Add(new Slice { Start = begun, End = ts, Tid = was });
                }
                sliceStart[cpu] = ts; sliceTid[cpu] = s.NewTid;
            }
            else if (g == ThreadGuid && (op == 1 || op == 3) && len >= 8) threadProcess[Marshal.ReadInt32(d, 4)] = Marshal.ReadInt32(d, 0);
            else if (g == ProcessGuid && (op == 1 || op == 3) && len > 40)
            {
                int pid = Marshal.ReadInt32(d, 8);
                // UniqueProcessKey(8) Pid ParentId SessionId ExitStatus DirectoryTableBase(8) Flags, then TOKEN_USER (16) + SID, then ANSI image name.
                int off = 8 + 4 + 4 + 4 + 4 + 8 + 4;
                int nameAt;
                if (Marshal.ReadInt64(d, off) == 0) nameAt = off + 8;
                else { int sidStart = off + 16; nameAt = sidStart + 8 + 4 * Marshal.ReadByte(d, sidStart + 1); }
                int used; string name = nameAt < len ? ReadAnsi(d + nameAt, len - nameAt, out used) : "?";
                processName[pid] = name;
            }
            else if (g == ImageGuid && (op == 10 || op == 3 || op == 4) && len > 56)
            {
                ulong imageBase = (ulong)Marshal.ReadInt64(d, 0), size = (ulong)Marshal.ReadInt64(d, 8); int pid = Marshal.ReadInt32(d, 16);
                if (pid == 0 || imageBase >= 0xFFFF000000000000UL)
                    kernelImages.Add(new Image { Base = imageBase, Size = size, Name = Path.GetFileName(ReadUnicode(d + 56, len - 56)) });
            }
            else if (g == PerfInfoGuid && (op == 50 || op == 66 || op == 67 || op == 68 || op == 69) && len >= 16)
            {
                long initial = Marshal.ReadInt64(d, 0); ulong routine = (ulong)Marshal.ReadInt64(d, 8);
                string kind = op == 67 || op == 50 ? "ISR" : op == 69 ? "TimerDPC" : op == 66 ? "ThreadedDPC" : "DPC";
                List<Slice> list; if (!interrupts.TryGetValue(cpu, out list)) interrupts[cpu] = list = new List<Slice>();
                var slice = new Slice { Start = initial, End = ts, Tid = -1, What = kind };
                list.Add(slice);
                pendingRoutines.Add(Tuple.Create(initial, routine, cpu, kind));
                slice.What = kind + "@" + routine.ToString("X");
            }
        }
        catch { }
    }

    private static string StateName(sbyte state)
    {
        switch (state) { case 1: return "Ready(preempted)"; case 5: return "Waiting"; case 7: return "DeferredReady"; case 3: return "Standby"; default: return "state" + state; }
    }

    private static string Driver(ulong address)
    {
        foreach (Image i in kernelImages) if (address >= i.Base && address < i.Base + i.Size) return i.Name;
        return "?" + address.ToString("X");
    }

    private static string Describe(int tid)
    {
        int pid; string name;
        if (tid == 0) return "Idle";
        if (!threadProcess.TryGetValue(tid, out pid)) return "tid" + tid;
        if (!processName.TryGetValue(pid, out name)) name = "pid" + pid;
        return name + "(" + pid + ")/" + tid;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("StallTrace <trace.etl> <processName> [minStallMs] [out.tsv]"); return 2; }
        double minStall = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 3;
        var log = new EventTraceLogfile { LogFileName = args[0], ProcessTraceMode = 0x10000000 | 0x00001000, EventRecordCallback = callback };
        ulong h = OpenTraceW(ref log);
        if (h == ulong.MaxValue) { Console.Error.WriteLine("OpenTrace failed " + Marshal.GetLastWin32Error()); return 1; }
        perfFreq = log.LogfileHeader.PerfFreq;
        uint rc = ProcessTrace(new[] { h }, 1, IntPtr.Zero, IntPtr.Zero);
        CloseTrace(h);
        if (perfFreq <= 0) perfFreq = 10000000;
        if (args.Length > 4 && args[4] == "--dump")
        {
            long first = switches.Count > 0 ? switches[0].At : 0;
            double from = double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture), to = double.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture);
            int dumpCpu = int.Parse(args[7]);
            foreach (Switch s in switches)
            {
                double at = (s.At - first) / (double)perfFreq;
                if (at < from || at > to || s.Cpu != dumpCpu) continue;
                Console.WriteLine(at.ToString("F6") + " cpu" + s.Cpu + " old=" + Describe(s.OldTid) + " state=" + StateName(s.OldState) + " wr=" + s.OldWaitReason + " -> new=" + Describe(s.NewTid));
            }
            foreach (var kv in imageOps) Console.WriteLine("event " + providerNames[kv.Key] + " count " + kv.Value);
            return 0;
        }
        Console.WriteLine("events: switches=" + switches.Count + " threads=" + threadProcess.Count + " processes=" + processName.Count + " kernelImages=" + kernelImages.Count + " rc=" + rc + " perfFreq=" + perfFreq);
        string target = args[1];
        var targetPids = new HashSet<int>(processName.Where(p => string.Equals(Path.GetFileNameWithoutExtension(p.Value), target, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key));
        // Busiest thread of the target processes by running time.
        var busy = new Dictionary<int, long>();
        foreach (var cpuSlices in running.Values) foreach (Slice s in cpuSlices)
        { int pid; if (threadProcess.TryGetValue(s.Tid, out pid) && targetPids.Contains(pid)) { long v; busy.TryGetValue(s.Tid, out v); busy[s.Tid] = v + (s.End - s.Start); } }
        if (busy.Count == 0) { Console.WriteLine("target thread not found"); return 1; }
        int render = busy.OrderByDescending(x => x.Value).First().Key;
        Console.WriteLine("target thread " + Describe(render) + " ran " + (busy[render] * 1000.0 / perfFreq).ToString("F0") + " ms");
        // Off-CPU intervals of the render thread that began with an involuntary switch-out (Ready = preempted, state 1).
        if (args.Length > 4 && args[4] == "--driver")
        {
            // Every DPC/ISR of one driver lasting at least 0.1 ms, with the gap to the previous one.
            string wanted = args[5]; long first = switches.Count > 0 ? switches[0].At : 0, previous = 0;
            var hits = new List<Tuple<long, double, int, string>>();
            foreach (var kv in interrupts) foreach (Slice s in kv.Value)
            {
                ulong routine = ulong.Parse(s.What.Substring(s.What.IndexOf('@') + 1), System.Globalization.NumberStyles.HexNumber);
                if (!string.Equals(Driver(routine), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                double dur = (s.End - s.Start) * 1000.0 / perfFreq; if (dur < 0.1) continue;
                hits.Add(Tuple.Create(s.Start, dur, kv.Key, s.What.Substring(0, s.What.IndexOf('@'))));
            }
            int watch = args.Length > 6 ? int.Parse(args[6]) : 0;
            var watched = switches.Where(s => watch != 0 && s.NewTid == watch).Select(s => s.At).ToList();
            foreach (var hit in hits.OrderBy(x => x.Item1))
            {
                string near = "";
                if (watch != 0)
                {
                    // When the watched thread last started running before this DPC, and first after it.
                    long before = watched.LastOrDefault(x => x <= hit.Item1), after = watched.FirstOrDefault(x => x > hit.Item1);
                    near = "  watched-thread ran " + (before == 0 ? "-" : ((hit.Item1 - before) * 1000.0 / perfFreq).ToString("F2") + " ms before")
                        + ", " + (after == 0 ? "-" : ((after - hit.Item1) * 1000.0 / perfFreq).ToString("F2") + " ms after");
                }
                Console.WriteLine(((hit.Item1 - first) / (double)perfFreq).ToString("F3") + " s  " + hit.Item4 + " " + hit.Item2.ToString("F2") + " ms cpu" + hit.Item3
                    + (previous == 0 ? "" : "  gap " + ((hit.Item1 - previous) * 1000.0 / perfFreq).ToString("F0") + " ms") + near);
                previous = hit.Item1;
            }
            Console.WriteLine(wanted + " >= 0.1 ms: " + hits.Count + " over " + ((switches.Last().At - first) / (double)perfFreq).ToString("F1") + " s");
            return 0;
        }
        if (args.Length > 4 && args[4] == "--frames")
        {
            // frames.csv from the bench: sequence,qpc,frame_ms,cpu_ms,... Each stutter frame spans (previous start, start].
            var rows = File.ReadAllLines(args[5]).Skip(1).Select(l => l.Split(',')).Where(c => c.Length > 6 && c[6] == "1")
                .Select(c => new { Qpc = long.Parse(c[1]), Ms = double.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture), Cpu = double.Parse(c[3], System.Globalization.CultureInfo.InvariantCulture) }).ToList();
            var sorted = rows.Select(r => r.Ms).OrderBy(x => x).ToList(); double median = sorted[sorted.Count / 2], limit = Math.Max(median * 2, median + 4);
            var renderSwitches = switches.Where(s => s.OldTid == render || s.NewTid == render).ToList();
            foreach (var r in rows.Where(r => r.Ms > limit))
            {
                long hi = r.Qpc, lo = hi - (long)(r.Ms / 1000.0 * perfFreq);
                Console.WriteLine("stutter frame " + r.Ms.ToString("F2") + " ms (prev cpu " + r.Cpu.ToString("F2") + ") at qpc " + hi);
                // Where the render thread was: its switches inside the frame, and the CPU it ran on.
                var inside = renderSwitches.Where(s => s.At >= lo && s.At <= hi).ToList();
                var cpus = new HashSet<int>(inside.Select(s => s.Cpu));
                Switch last = renderSwitches.LastOrDefault(s => s.At < lo); if (last.At != 0) cpus.Add(last.Cpu);
                foreach (Switch s in inside.Where(s => s.OldTid != s.NewTid))
                    Console.WriteLine("   " + ((s.At - lo) * 1000.0 / perfFreq).ToString("F3") + " ms cpu" + s.Cpu + " " + Describe(s.OldTid) + " " + StateName(s.OldState) + " wr=" + s.OldWaitReason + " -> " + Describe(s.NewTid));
                foreach (int c in cpus)
                {
                    List<Slice> ints; if (!interrupts.TryGetValue(c, out ints)) continue;
                    foreach (Slice s in ints)
                    {
                        if (s.End < lo || s.Start > hi) continue;
                        double dur = (s.End - s.Start) * 1000.0 / perfFreq; if (dur < 0.05) continue;
                        ulong routine = ulong.Parse(s.What.Substring(s.What.IndexOf('@') + 1), System.Globalization.NumberStyles.HexNumber);
                        Console.WriteLine("   cpu" + c + " " + s.What.Substring(0, s.What.IndexOf('@')) + " " + Driver(routine) + " " + dur.ToString("F2") + " ms at +" + ((s.Start - lo) * 1000.0 / perfFreq).ToString("F3"));
                    }
                }
                // Long DPC/ISR anywhere during the frame.
                foreach (var kv in interrupts) foreach (Slice s in kv.Value)
                {
                    if (cpus.Contains(kv.Key) || s.End < lo || s.Start > hi) continue;
                    double dur = (s.End - s.Start) * 1000.0 / perfFreq; if (dur < 0.3) continue;
                    ulong routine = ulong.Parse(s.What.Substring(s.What.IndexOf('@') + 1), System.Globalization.NumberStyles.HexNumber);
                    Console.WriteLine("   (other) cpu" + kv.Key + " " + s.What.Substring(0, s.What.IndexOf('@')) + " " + Driver(routine) + " " + dur.ToString("F2") + " ms at +" + ((s.Start - lo) * 1000.0 / perfFreq).ToString("F3"));
                }
            }
            return 0;
        }
        // A switch from a thread to itself is a quantum end with nothing else ready, not an off-CPU interval.
        var outs = switches.Where(s => s.OldTid != s.NewTid && (s.OldTid == render || s.NewTid == render)).OrderBy(s => s.At).ToList();
        long t0 = switches.Count > 0 ? switches[0].At : 0;
        var report = new StringBuilder("start_s\tstall_ms\tstate\twait_reason\tcpu\twho_ran\tdpc_isr\n");
        int count = 0;
        for (int i = 0; i + 1 < outs.Count; i++)
        {
            Switch a = outs[i];
            if (a.OldTid != render) continue;
            Switch b = outs[i + 1];
            if (b.NewTid != render) continue;
            double ms = (b.At - a.At) * 1000.0 / perfFreq;
            if (ms < minStall) continue;
            count++;
            var who = new Dictionary<string, double>();
            List<Slice> slices;
            if (running.TryGetValue(a.Cpu, out slices))
                foreach (Slice s in slices)
                {
                    long lo = Math.Max(s.Start, a.At), hi = Math.Min(s.End, b.At);
                    if (hi <= lo) continue;
                    string k = Describe(s.Tid); double v; who.TryGetValue(k, out v); who[k] = v + (hi - lo) * 1000.0 / perfFreq;
                }
            var irq = new Dictionary<string, double>();
            List<Slice> ints;
            if (interrupts.TryGetValue(a.Cpu, out ints))
                foreach (Slice s in ints)
                {
                    long lo = Math.Max(s.Start, a.At), hi = Math.Min(s.End, b.At);
                    if (hi <= lo) continue;
                    ulong routine = ulong.Parse(s.What.Substring(s.What.IndexOf('@') + 1), System.Globalization.NumberStyles.HexNumber);
                    string k = s.What.Substring(0, s.What.IndexOf('@')) + ":" + Driver(routine); double v; irq.TryGetValue(k, out v); irq[k] = v + (hi - lo) * 1000.0 / perfFreq;
                }
            report.Append(((a.At - t0) / (double)perfFreq).ToString("F3")).Append('\t').Append(ms.ToString("F2")).Append('\t').Append(StateName(a.OldState)).Append('\t').Append(a.OldWaitReason)
                .Append('\t').Append(a.Cpu).Append('\t')
                .Append(string.Join("; ", who.OrderByDescending(x => x.Value).Take(4).Select(x => x.Key + " " + x.Value.ToString("F2"))))
                .Append('\t').Append(string.Join("; ", irq.OrderByDescending(x => x.Value).Take(4).Select(x => x.Key + " " + x.Value.ToString("F2")))).Append('\n');
        }
        // Longest DPC/ISR overall, by driver.
        var worst = new List<Tuple<double, string, int, double>>();
        foreach (var kv in interrupts) foreach (Slice s in kv.Value)
        {
            double ms = (s.End - s.Start) * 1000.0 / perfFreq;
            if (ms < 0.5 || ms > 1000) continue;
            ulong routine = ulong.Parse(s.What.Substring(s.What.IndexOf('@') + 1), System.Globalization.NumberStyles.HexNumber);
            worst.Add(Tuple.Create(ms, s.What.Substring(0, s.What.IndexOf('@')) + ":" + Driver(routine), kv.Key, (s.Start - t0) / (double)perfFreq));
        }
        Console.WriteLine("preemptions of the target thread >= " + minStall + " ms: " + count);
        Console.Write(report.ToString());
        Console.WriteLine("DPC/ISR >= 0.5 ms (top 25):");
        foreach (var w in worst.OrderByDescending(x => x.Item1).Take(25)) Console.WriteLine("  " + w.Item1.ToString("F2") + " ms  " + w.Item2 + "  cpu" + w.Item3 + "  t=" + w.Item4.ToString("F3"));
        var byDriver = worst.GroupBy(x => x.Item2).Select(x => x.Key + " n=" + x.Count() + " max=" + x.Max(y => y.Item1).ToString("F2")).ToArray();
        Console.WriteLine("by driver: " + string.Join(" | ", byDriver));
        if (args.Length > 3) File.WriteAllText(args[3], report.ToString());
        return 0;
    }
}
