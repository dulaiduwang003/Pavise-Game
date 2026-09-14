using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;

// Isolated synthetic workloads only No game handles drivers MSR writes or UI
public static unsafe class CpuMemoryBench
{
    const uint MEM_COMMIT_RESERVE = 0x3000, PAGE_READWRITE = 4;
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetLogicalProcessorInformationEx(int rel, IntPtr buffer, ref uint size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)] struct GroupAffinity { public UIntPtr Mask; public ushort Group; public ushort R0, R1, R2; }
    public sealed class Core { public int efficiency; public ulong mask; public int group; }
    public sealed class Cache { public int level, bytes, group; public ulong mask; }
    public sealed class Topology
    {
        public string machine, runtime, utc;
        public bool elevated;
        public ulong allowedMask;
        public List<Core> cores = new List<Core>();
        public List<Cache> caches = new List<Cache>();
    }
    public sealed class Config
    {
        public string id, foreground, background, placement;
        public int round;
        public ulong foregroundMask;
        public ulong[] backgroundMasks;
        public double warmupSeconds, measureSeconds;
        public int seed;
    }
    public sealed class WorkerResult { public ulong requestedMask, observedMask, originalMask, restoredMask; public int observedGroup; public long measuredSteps; public double cpuSeconds; public string error; }
    public sealed class Result
    {
        public Config config;
        public string utcStart, utcEnd, priorityClass;
        public double seconds, stepsPerSecond, systemCpuPercent, processCpuSeconds, foregroundCpuSeconds;
        public long steps, stopwatchFrequency, checksum;
        public int samples, pid;
        public ulong requestedForegroundMask, observedForegroundMask, originalForegroundMask, restoredForegroundMask;
        public WorkerResult[] workers;
        public string error;
    }
    static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
    static void Write(string path, object value) { File.WriteAllText(path, Json().Serialize(value)); }
    static long ThreadCpu()
    {
        long c, e, k, u;
        if (!GetThreadTimes(GetCurrentThread(), out c, out e, out k, out u)) throw new InvalidOperationException("GetThreadTimes failed");
        return k + u;
    }
    static GroupAffinity Affinity()
    {
        GroupAffinity a;
        if (!GetThreadGroupAffinity(GetCurrentThread(), out a)) throw new InvalidOperationException("GetThreadGroupAffinity failed");
        return a;
    }
    static ulong Pin(ulong mask)
    {
        ulong old = SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(mask)).ToUInt64();
        if (old == 0) throw new InvalidOperationException("SetThreadAffinityMask failed: " + Marshal.GetLastWin32Error());
        GroupAffinity a = Affinity();
        if (a.Group != 0 || a.Mask.ToUInt64() != mask) throw new InvalidOperationException("Affinity readback mismatch");
        return old;
    }
    static Topology Probe()
    {
        Topology t = new Topology();
        t.machine = Environment.MachineName; t.runtime = Environment.Version.ToString(); t.utc = DateTime.UtcNow.ToString("o");
        t.allowedMask = unchecked((ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64());
        t.elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        uint bytes = 0;
        GetLogicalProcessorInformationEx(0xffff, IntPtr.Zero, ref bytes);
        if (bytes == 0) throw new InvalidOperationException("Topology sizing failed");
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            uint capacity = bytes;
            if (!GetLogicalProcessorInformationEx(0xffff, buffer, ref bytes) || bytes > capacity) throw new InvalidOperationException("Topology read failed");
            for (int offset = 0; offset < bytes; )
            {
                IntPtr r = IntPtr.Add(buffer, offset);
                int rel = Marshal.ReadInt32(r), size = Marshal.ReadInt32(r, 4);
                if (size < 8 || offset + size > bytes) throw new InvalidOperationException("Invalid topology record");
                IntPtr u = IntPtr.Add(r, 8);
                if (rel == 0)
                {
                    if (size < 32) throw new InvalidOperationException("Short core record");
                    int groups = (ushort)Marshal.ReadInt16(u, 22);
                    if (32 + groups * 16 > size) throw new InvalidOperationException("Short core group list");
                    for (int g = 0; g < groups; g++)
                        t.cores.Add(new Core { efficiency = Marshal.ReadByte(u, 1), mask = unchecked((ulong)Marshal.ReadInt64(u, 24 + g * 16)), group = (ushort)Marshal.ReadInt16(u, 32 + g * 16) });
                }
                if (rel == 2)
                {
                    if (size < 56) throw new InvalidOperationException("Short cache record");
                    int groups = (ushort)Marshal.ReadInt16(u, 30);
                    if (groups == 0) groups = 1; // Windows 10 single-group layout
                    if (40 + groups * 16 > size) throw new InvalidOperationException("Short cache group list");
                    for (int g = 0; g < groups; g++)
                        t.caches.Add(new Cache { level = Marshal.ReadByte(u), bytes = Marshal.ReadInt32(u, 4), mask = unchecked((ulong)Marshal.ReadInt64(u, 32 + g * 16)), group = (ushort)Marshal.ReadInt16(u, 40 + g * 16) });
                }
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return t;
    }
    sealed class Buffer : IDisposable
    {
        public IntPtr address;
        public long* data { get { return (long*)address; } }
        public int length;
        public Buffer(int bytes)
        {
            address = VirtualAlloc(IntPtr.Zero, new UIntPtr((uint)bytes), MEM_COMMIT_RESERVE, PAGE_READWRITE);
            if (address == IntPtr.Zero) throw new OutOfMemoryException("VirtualAlloc failed");
            length = bytes / 8;
            for (int i = 0; i < length; i++) data[i] = i * 17L + 3;
        }
        public void Dispose()
        {
            if (address != IntPtr.Zero)
            {
                if (!VirtualFree(address, UIntPtr.Zero, 0x8000)) throw new InvalidOperationException("VirtualFree failed");
                address = IntPtr.Zero;
            }
        }
    }
    static void MakeCycle(Buffer buffer, int seed)
    {
        int nodes = buffer.length / 8;
        int[] order = new int[nodes];
        for (int i = 0; i < nodes; i++) order[i] = i;
        Random random = new Random(seed);
        for (int i = nodes - 1; i > 0; i--) { int j = random.Next(i + 1); int temp = order[i]; order[i] = order[j]; order[j] = temp; }
        for (int i = 0; i < nodes; i++) buffer.data[order[i] * 8] = order[(i + 1) % nodes] * 8L;
        // Validate one complete dependent cycle before timing
        long at = 0;
        for (int i = 0; i < nodes; i++) { at = buffer.data[at]; if (at < 0 || at >= buffer.length || (at & 7) != 0 || (at == 0 && i != nodes - 1)) throw new InvalidOperationException("Invalid pointer cycle"); }
        if (at != 0) throw new InvalidOperationException("Pointer cycle did not close");
    }
    static long Compute(long value, int count)
    {
        unchecked { for (int i = 0; i < count; i++) { value ^= value >> 13; value = value * 6364136223846793005L + 1442695040888963407L; } }
        return value;
    }
    static long Chase(long* data, long at, int count)
    {
        for (int i = 0; i < count; i += 8) { at = data[at]; at = data[at]; at = data[at]; at = data[at]; at = data[at]; at = data[at]; at = data[at]; at = data[at]; }
        return at;
    }
    sealed class Worker
    {
        public Thread thread;
        public WorkerResult result = new WorkerResult();
        public long steps;
        public long checksum;
        public volatile bool stop;
        public ManualResetEvent ready = new ManualResetEvent(false);
        readonly string kind;
        readonly ulong mask;
        public Worker(string kind, ulong mask) { this.kind = kind; this.mask = mask; thread = new Thread(Loop); thread.IsBackground = true; }
        void Loop()
        {
            ulong old = 0;
            Buffer buffer = null;
            try
            {
                old = Pin(mask);
                result.originalMask = old;
                GroupAffinity observed = Affinity();
                result.requestedMask = mask; result.observedMask = observed.Mask.ToUInt64(); result.observedGroup = observed.Group;
                if (kind == "stream") buffer = new Buffer(64 * 1024 * 1024);
                long cpuStart = ThreadCpu();
                ready.Set();
                long value = 987654321;
                int at = 0;
                while (!stop)
                {
                    if (kind == "compute") { value = Compute(value, 32768); Interlocked.Add(ref steps, 32768); }
                    else
                    {
                        long* data = buffer.data;
                        int end = at + 4096;
                        unchecked { for (int i = at; i < end; i += 8) value += data[i] + data[i + 1] + data[i + 2] + data[i + 3] + data[i + 4] + data[i + 5] + data[i + 6] + data[i + 7]; }
                        at = end == buffer.length ? 0 : end;
                        Interlocked.Add(ref steps, 4096);
                    }
                }
                checksum = value;
                result.cpuSeconds = (ThreadCpu() - cpuStart) / 1e7; // Includes warmup throughput uses measured counter deltas
            }
            catch (Exception ex) { result.error = ex.ToString(); ready.Set(); }
            finally
            {
                try { if (old != 0) { Pin(old); result.restoredMask = Affinity().Mask.ToUInt64(); } if (buffer != null) buffer.Dispose(); }
                catch (Exception ex) { result.error = (result.error ?? "") + ex.ToString(); }
            }
        }
    }
    static void Run(Config config, string output)
    {
        if (config.measureSeconds < 0.1 || config.measureSeconds > 15 || config.warmupSeconds < 0 || config.warmupSeconds > 5) throw new ArgumentException("Invalid timing");
        if (config.foreground != "compute" && config.foreground != "cache8" && config.foreground != "dram128") throw new ArgumentException("Invalid foreground");
        if (config.background != "none" && config.background != "compute" && config.background != "stream") throw new ArgumentException("Invalid background");
        Result result = new Result { config = config, utcStart = DateTime.UtcNow.ToString("o"), pid = Process.GetCurrentProcess().Id, priorityClass = Process.GetCurrentProcess().PriorityClass.ToString(), stopwatchFrequency = Stopwatch.Frequency, requestedForegroundMask = config.foregroundMask };
        List<Worker> workers = new List<Worker>();
        Buffer buffer = null;
        ulong original = 0;
        long[] samples = new long[250000];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0;
        try
        {
            original = Pin(config.foregroundMask);
            result.originalForegroundMask = original;
            result.observedForegroundMask = Affinity().Mask.ToUInt64();
            if (config.foreground != "compute") { buffer = new Buffer((config.foreground == "cache8" ? 8 : 128) * 1024 * 1024); MakeCycle(buffer, config.seed); }
            foreach (ulong mask in config.backgroundMasks) { Worker w = new Worker(config.background, mask); workers.Add(w); w.thread.Start(); }
            foreach (Worker w in workers) { if (!w.ready.WaitOne(15000) || w.result.error != null) throw new InvalidOperationException("Worker setup failed: " + w.result.error); }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int batch = config.foreground == "compute" ? 32768 : 2048;
            long value = config.foreground == "compute" ? 123456789 : 0;
            long warmEnd = Stopwatch.GetTimestamp() + (long)(config.warmupSeconds * Stopwatch.Frequency);
            do { value = buffer == null ? Compute(value, batch) : Chase(buffer.data, value, batch); } while (Stopwatch.GetTimestamp() < warmEnd);
            long idle0, kernel0, user0, idle1, kernel1, user1;
            if (!GetSystemTimes(out idle0, out kernel0, out user0)) throw new InvalidOperationException("GetSystemTimes failed");
            double process0 = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
            long cpu0 = ThreadCpu();
            long[] background0 = new long[workers.Count];
            for (int i = 0; i < workers.Count; i++) background0[i] = Interlocked.Read(ref workers[i].steps);
            long start = Stopwatch.GetTimestamp(), previous = start, now = start;
            long end = start + (long)(config.measureSeconds * Stopwatch.Frequency);
            int count = 0;
            while (now < end)
            {
                value = buffer == null ? Compute(value, batch) : Chase(buffer.data, value, batch);
                now = Stopwatch.GetTimestamp();
                if (count == samples.Length) throw new InvalidOperationException("Sample capacity exceeded");
                samples[count++] = now - previous;
                previous = now;
            }
            for (int i = 0; i < workers.Count; i++) workers[i].result.measuredSteps = Interlocked.Read(ref workers[i].steps) - background0[i];
            result.foregroundCpuSeconds = (ThreadCpu() - cpu0) / 1e7;
            result.processCpuSeconds = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds - process0;
            if (!GetSystemTimes(out idle1, out kernel1, out user1)) throw new InvalidOperationException("GetSystemTimes failed");
            long total = kernel1 - kernel0 + user1 - user0;
            result.systemCpuPercent = total <= 0 ? 0 : 100.0 * (total - (idle1 - idle0)) / total;
            result.seconds = (now - start) / (double)Stopwatch.Frequency;
            result.samples = count; result.steps = (long)count * batch; result.stepsPerSecond = result.steps / result.seconds; result.checksum = value;
            foreach (Worker w in workers) w.stop = true;
            using (StreamWriter writer = new StreamWriter(Path.Combine(output, "samples.csv")))
            {
                writer.WriteLine("batch_ticks");
                for (int i = 0; i < count; i++) writer.WriteLine(samples[i].ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) { result.error = ex.ToString(); }
        finally
        {
            foreach (Worker w in workers) w.stop = true;
            foreach (Worker w in workers)
            {
                if (!w.thread.Join(5000)) result.error = (result.error ?? "") + " Worker did not exit.";
                if (w.result.error != null) result.error = (result.error ?? "") + w.result.error;
                w.ready.Dispose();
            }
            try { if (original != 0) { Pin(original); result.restoredForegroundMask = Affinity().Mask.ToUInt64(); } if (buffer != null) buffer.Dispose(); }
            catch (Exception ex) { result.error = (result.error ?? "") + ex.ToString(); }
            result.workers = workers.ConvertAll(delegate(Worker w) { return w.result; }).ToArray();
            result.utcEnd = DateTime.UtcNow.ToString("o");
            Write(Path.Combine(output, "result.json"), result);
        }
        if (result.error != null) throw new InvalidOperationException(result.error);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        using (Timer watchdog = new Timer(delegate { Environment.Exit(124); }, null, 90000, Timeout.Infinite))
        {
            try
            {
                if (args.Length == 2 && args[0] == "probe") { Write(args[1], Probe()); return 0; }
                if (args.Length == 3 && args[0] == "run") { Run(Json().Deserialize<Config>(File.ReadAllText(args[1])), args[2]); return 0; }
                return 2;
            }
            catch (Exception ex)
            {
                if (args.Length == 3 && Directory.Exists(args[2])) File.WriteAllText(Path.Combine(args[2], "error.txt"), ex.ToString());
                return 1;
            }
        }
    }
}
