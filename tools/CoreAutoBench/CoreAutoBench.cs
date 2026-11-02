// Copyright (C) 2026 bdth. SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class CoreAutoBenchJournal : ICoreIsolationStore
    {
        private readonly string path;
        internal CoreAutoBenchJournal(string file) { path = file; }
        public bool Read(out string value)
        {
            value = "";
            try { if (File.Exists(path)) value = File.ReadAllText(path); return true; }
            catch { return false; }
        }
        public bool Write(string value)
        {
            try { File.WriteAllText(path, value); return true; }
            catch { return false; }
        }
    }

    internal static class CoreAutoBench
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

        private static string Exe { get { return typeof(CoreAutoBench).Assembly.Location; } }
        private const int WorldInts = 16 * 1024 * 1024;
        private static int[] world;

        private static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--game") return Game(args);
            if (args.Length >= 1 && args[0] == "--noise") return Noise(args);
            return Controller(args);
        }

        private static void FillWorld(int[] data, uint seed)
        {
            for (int i = 0; i < data.Length; i++) { seed = seed * 1664525u + 1013904223u; data[i] = (int)seed; }
        }

        private static long Work(int[] data, long units, ref uint seed)
        {
            long acc = 0;
            int mask = data.Length - 1;
            for (long i = 0; i < units; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                acc += data[(int)(seed >> 8) & mask] * (i | 1);
                acc ^= acc << 13; acc ^= acc >> 7; acc ^= acc << 17;
            }
            return acc;
        }

        private static long UnitsPerMs(int logical)
        {
            var data = new int[WorldInts];
            FillWorld(data, 7);
            IntPtr thread = GetCurrentThread();
            UIntPtr old = SetThreadAffinityMask(thread, (UIntPtr)(1UL << logical));
            try
            {
                uint seed = 11;
                Work(data, 2000000, ref seed);
                var sw = Stopwatch.StartNew();
                Work(data, 4000000, ref seed);
                return (long)(4000000 / sw.Elapsed.TotalMilliseconds);
            }
            finally { SetThreadAffinityMask(thread, old); }
        }

        private static int Game(string[] args)
        {
            double seconds = double.Parse(args[1], CultureInfo.InvariantCulture);
            long mainUnits = long.Parse(args[2], CultureInfo.InvariantCulture);
            long jobUnits = long.Parse(args[3], CultureInfo.InvariantCulture);
            int workers = int.Parse(args[4], CultureInfo.InvariantCulture);
            ulong perf = ulong.Parse(args[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            world = new int[WorldInts];
            FillWorld(world, 3);
            var start = new Barrier(workers + 1);
            var end = new Barrier(workers + 1);
            bool stop = false;
            long jobs = 0, offPerf = 0, jobTicks = 0;
            var lpHits = new long[64];
            for (int w = 0; w < workers; w++)
            {
                uint seed = (uint)(w * 7919 + 17);
                var t = new Thread(delegate()
                {
                    while (true)
                    {
                        start.SignalAndWait();
                        if (Volatile.Read(ref stop)) break;
                        uint cpu = GetCurrentProcessorNumber();
                        Interlocked.Increment(ref jobs);
                        if (cpu < 64) Interlocked.Increment(ref lpHits[cpu]);
                        if (cpu >= 64 || ((perf >> (int)cpu) & 1UL) == 0) Interlocked.Increment(ref offPerf);
                        long t0 = Stopwatch.GetTimestamp();
                        Work(world, jobUnits, ref seed);
                        Interlocked.Add(ref jobTicks, Stopwatch.GetTimestamp() - t0);
                        end.SignalAndWait();
                    }
                });
                t.IsBackground = true; t.Start();
            }
            Console.WriteLine("READY");
            if (Console.ReadLine() != "go") return 2;
            var frames = new List<double>(200000);
            uint mainSeed = 99;
            var total = Stopwatch.StartNew();
            var frame = new Stopwatch();
            while (total.Elapsed.TotalSeconds < seconds)
            {
                frame.Restart();
                start.SignalAndWait();
                uint cpu = GetCurrentProcessorNumber();
                Interlocked.Increment(ref jobs);
                if (cpu < 64) Interlocked.Increment(ref lpHits[cpu]);
                if (cpu >= 64 || ((perf >> (int)cpu) & 1UL) == 0) Interlocked.Increment(ref offPerf);
                Work(world, mainUnits, ref mainSeed);
                end.SignalAndWait();
                frames.Add(frame.Elapsed.TotalMilliseconds);
            }
            Volatile.Write(ref stop, true);
            start.SignalAndWait();
            frames.Sort();
            int n = frames.Count;
            double sum = 0; foreach (double f in frames) sum += f;
            Console.WriteLine("RESULT " + string.Join(" ", new[]
            {
                n.ToString(CultureInfo.InvariantCulture),
                (1000.0 * n / sum).ToString("F2", CultureInfo.InvariantCulture),
                LowFps(frames, 0.01).ToString("F2", CultureInfo.InvariantCulture),
                LowFps(frames, 0.001).ToString("F2", CultureInfo.InvariantCulture),
                Pct(frames, 0.50).ToString("F3", CultureInfo.InvariantCulture),
                Pct(frames, 0.99).ToString("F3", CultureInfo.InvariantCulture),
                Pct(frames, 0.999).ToString("F3", CultureInfo.InvariantCulture),
                frames[n - 1].ToString("F3", CultureInfo.InvariantCulture),
                (jobs == 0 ? 0 : 100.0 * offPerf / jobs).ToString("F1", CultureInfo.InvariantCulture),
                (jobs <= n ? 0 : 1000.0 * jobTicks / Stopwatch.Frequency / (jobs - n)).ToString("F3", CultureInfo.InvariantCulture),
                LpHistogram(lpHits)
            }));
            return 0;
        }

        private static string LpHistogram(long[] hits)
        {
            long total = 0; foreach (long h in hits) total += h;
            var sb = new StringBuilder();
            for (int i = 0; i < hits.Length; i++)
                if (hits[i] * 100 >= total && total > 0) sb.Append(sb.Length == 0 ? "" : ",").Append(i).Append(':').Append(hits[i] * 100 / total);
            return sb.Length == 0 ? "-" : sb.ToString();
        }

        private static double Pct(List<double> sorted, double q)
        {
            int i = (int)Math.Ceiling(q * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, i))];
        }

        private static double LowFps(List<double> sorted, double fraction)
        {
            int take = Math.Max(1, (int)(sorted.Count * fraction));
            double sum = 0;
            for (int i = sorted.Count - take; i < sorted.Count; i++) sum += sorted[i];
            return 1000.0 * take / sum;
        }

        private static int Noise(string[] args)
        {
            int threads = int.Parse(args[1], CultureInfo.InvariantCulture);
            int duty = int.Parse(args[2], CultureInfo.InvariantCulture);
            int maxBurst = int.Parse(args[3], CultureInfo.InvariantCulture);
            long unitsPerMs = long.Parse(args[4], CultureInfo.InvariantCulture);
            bool stop = false;
            long done = 0;
            var list = new List<Thread>();
            for (int t = 0; t < threads; t++)
            {
                int id = t;
                var th = new Thread(delegate()
                {
                    var data = new int[4 * 1024 * 1024];
                    FillWorld(data, (uint)(id + 1));
                    var rng = new Random(id * 31 + 5);
                    uint seed = (uint)id;
                    while (!Volatile.Read(ref stop))
                    {
                        int burst = rng.Next(1, maxBurst + 1);
                        Work(data, unitsPerMs * burst, ref seed);
                        Interlocked.Add(ref done, burst);
                        int rest = duty >= 100 ? 0 : burst * (100 - duty) / Math.Max(1, duty);
                        if (rest > 0) Thread.Sleep(rng.Next(0, 2 * rest + 1));
                    }
                });
                th.IsBackground = true; th.Start(); list.Add(th);
            }
            Console.WriteLine("READY");
            Console.ReadLine();
            Volatile.Write(ref stop, true);
            foreach (Thread th in list) th.Join(5000);
            Console.WriteLine("DONE " + Interlocked.Read(ref done).ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        private sealed class Config
        {
            public string Name, Noise;
            public bool GameHigh, NoiseLow, Place, Isolate, ExactAffinity, DefaultSets;
            public int Workers = 7;
        }

        private sealed class Sample
        {
            public string Config;
            public int Round;
            public double Frames, AvgFps, Low1, Low01, P50, P99, P999, Max, OffPerf, NoiseWork, JobMs;
            public string Lps = "";
            public string Note = "";
        }

        private static Process Child(string arguments)
        {
            return Process.Start(new ProcessStartInfo(Exe, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
        }

        private static int Controller(string[] args)
        {
            string output = args.Length >= 1 ? Path.GetFullPath(args[0]) : Path.GetDirectoryName(Exe);
            double seconds = args.Length >= 2 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 12;
            int rounds = args.Length >= 3 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 3;
            Directory.CreateDirectory(output);
            Settings.UseTransientStoreForCurrentProcess();
            Lang.Init();
            var log = new StreamWriter(Path.Combine(output, "core-auto-bench.log"), false, new UTF8Encoding(false));
            log.AutoFlush = true;
            var journal = new CoreAutoBenchJournal(Path.Combine(output, "isolation-journal.txt"));
            bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            var os = new CoreIsolationNative();
            try
            {
                using (var recover = new CoreIsolationEngine(os, journal))
                    log.WriteLine("journal recovery " + recover.Recover() + " " + recover.Error);
                IsolationCpuState state = os.ReadSystem(IntPtr.Zero);
                ulong all = state.All, pre = state.Allocated;
                ulong perf = CpuTopology.Hybrid ? CpuTopology.PerfMask : all;
                ulong gameMask = perf;
                ulong isolation = 0;
                foreach (ulong core in state.Physical)
                    if ((core & perf) == core && (core & 1UL) == 0 && (core & pre) == 0) isolation |= core;
                long upm = UnitsPerMs(6);
                long mainUnits = upm * 2, jobUnits = upm * 12 / 10;
                log.WriteLine("admin=" + admin + " all=" + all.ToString("X") + " pre=" + pre.ToString("X") + " perf=" + perf.ToString("X")
                    + " game=" + gameMask.ToString("X") + " isolation=" + isolation.ToString("X") + " unitsPerMs=" + upm
                    + " hybrid=" + CpuTopology.Hybrid + " seconds=" + seconds + " rounds=" + rounds);
                var configs = new List<Config>
                {
                    new Config { Name = "idle-unmanaged", Noise = null },
                    new Config { Name = "idle-B-priority", Noise = null, GameHigh = true },
                    new Config { Name = "idle-C-placed", Noise = null, GameHigh = true, Place = true },
                    new Config { Name = "idle-D1-exact", Noise = null, GameHigh = true, Place = true, Isolate = true, ExactAffinity = true },
                    new Config { Name = "idle-D2-sets", Noise = null, GameHigh = true, Place = true, Isolate = true, DefaultSets = true },
                    new Config { Name = "heavy-A-unmanaged", Noise = "heavy" },
                    new Config { Name = "heavy-B-priority", Noise = "heavy", GameHigh = true, NoiseLow = true },
                    new Config { Name = "heavy-C-placed", Noise = "heavy", GameHigh = true, NoiseLow = true, Place = true },
                    new Config { Name = "heavy-D0-naive", Noise = "heavy", GameHigh = true, NoiseLow = true, Place = true, Isolate = true },
                    new Config { Name = "heavy-D1-exact", Noise = "heavy", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, ExactAffinity = true },
                    new Config { Name = "heavy-D2-sets", Noise = "heavy", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, DefaultSets = true },
                    new Config { Name = "medium-A-unmanaged", Noise = "medium" },
                    new Config { Name = "medium-B-priority", Noise = "medium", GameHigh = true, NoiseLow = true },
                    new Config { Name = "medium-C-placed", Noise = "medium", GameHigh = true, NoiseLow = true, Place = true },
                    new Config { Name = "medium-D1-exact", Noise = "medium", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, ExactAffinity = true },
                    new Config { Name = "medium-D2-sets", Noise = "medium", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, DefaultSets = true },
                    new Config { Name = "heavy-C1-exact", Noise = "heavy", GameHigh = true, NoiseLow = true, Place = true, ExactAffinity = true },
                    new Config { Name = "medium-C1-exact", Noise = "medium", GameHigh = true, NoiseLow = true, Place = true, ExactAffinity = true },
                    new Config { Name = "light-idle-A", Noise = null, Workers = 3 },
                    new Config { Name = "light-idle-B", Noise = null, GameHigh = true, Workers = 3 },
                    new Config { Name = "light-idle-D1", Noise = null, GameHigh = true, Place = true, Isolate = true, ExactAffinity = true, Workers = 3 },
                    new Config { Name = "five-idle-B", Noise = null, GameHigh = true, Workers = 4 },
                    new Config { Name = "five-idle-D1", Noise = null, GameHigh = true, Place = true, Isolate = true, ExactAffinity = true, Workers = 4 },
                    new Config { Name = "six-idle-B", Noise = null, GameHigh = true, Workers = 5 },
                    new Config { Name = "six-idle-D1", Noise = null, GameHigh = true, Place = true, Isolate = true, ExactAffinity = true, Workers = 5 },
                    new Config { Name = "light-heavy-A", Noise = "heavy", Workers = 3 },
                    new Config { Name = "light-heavy-B", Noise = "heavy", GameHigh = true, NoiseLow = true, Workers = 3 },
                    new Config { Name = "light-heavy-C1", Noise = "heavy", GameHigh = true, NoiseLow = true, Place = true, ExactAffinity = true, Workers = 3 },
                    new Config { Name = "light-heavy-D1", Noise = "heavy", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, ExactAffinity = true, Workers = 3 },
                    new Config { Name = "light-medium-B", Noise = "medium", GameHigh = true, NoiseLow = true, Workers = 3 },
                    new Config { Name = "light-medium-C1", Noise = "medium", GameHigh = true, NoiseLow = true, Place = true, ExactAffinity = true, Workers = 3 },
                    new Config { Name = "light-medium-D1", Noise = "medium", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, ExactAffinity = true, Workers = 3 },
                    new Config { Name = "five-heavy-B", Noise = "heavy", GameHigh = true, NoiseLow = true, Workers = 4 },
                    new Config { Name = "five-heavy-D1", Noise = "heavy", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, ExactAffinity = true, Workers = 4 },
                    new Config { Name = "five-medium-B", Noise = "medium", GameHigh = true, NoiseLow = true, Workers = 4 },
                    new Config { Name = "five-medium-D1", Noise = "medium", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, ExactAffinity = true, Workers = 4 },
                    new Config { Name = "six-heavy-B", Noise = "heavy", GameHigh = true, NoiseLow = true, Workers = 5 },
                    new Config { Name = "six-heavy-D1", Noise = "heavy", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, ExactAffinity = true, Workers = 5 },
                    new Config { Name = "six-medium-B", Noise = "medium", GameHigh = true, NoiseLow = true, Workers = 5 },
                    new Config { Name = "six-medium-D1", Noise = "medium", GameHigh = true, NoiseLow = true, Place = true, Isolate = true, ExactAffinity = true, Workers = 5 }
                };
                if (args.Length >= 4)
                {
                    var wanted = new HashSet<string>(args[3].Split(','));
                    configs = configs.FindAll(delegate(Config c) { return wanted.Contains(c.Name); });
                }
                var samples = new List<Sample>();
                var rng = new Random(20260927);
                for (int round = 1; round <= rounds; round++)
                {
                    var order = new List<Config>(configs);
                    for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); Config t = order[i]; order[i] = order[j]; order[j] = t; }
                    foreach (Config c in order)
                    {
                        if (c.Isolate && (!admin || isolation == 0)) continue;
                        Sample s = Run(c, round, seconds, mainUnits, jobUnits, upm, perf, gameMask, isolation, os, journal, log);
                        samples.Add(s);
                        log.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "round {0} {1,-20} fps {2,8:F1} 1%low {3,8:F1} 0.1%low {4,8:F1} p50 {5,6:F3} p99 {6,7:F3} p999 {7,7:F3} max {8,8:F3} offPerf {9,5:F1}% job {10,6:F3}ms noise {11,8:F0} lps {12} {13}",
                            round, c.Name, s.AvgFps, s.Low1, s.Low01, s.P50, s.P99, s.P999, s.Max, s.OffPerf, s.JobMs, s.NoiseWork, s.Lps, s.Note));
                    }
                }
                WriteSummary(output, configs, samples, log);
                IsolationCpuState after = os.ReadSystem(IntPtr.Zero);
                log.WriteLine("final allocated=" + after.Allocated.ToString("X") + " expected=" + pre.ToString("X"));
                return after.Allocated == pre ? 0 : 3;
            }
            catch (Exception e) { log.WriteLine("FAIL " + e); return 1; }
            finally
            {
                using (var recover = new CoreIsolationEngine(os, journal)) recover.Recover();
                log.Dispose();
            }
        }

        private static Sample Run(Config c, int round, double seconds, long mainUnits, long jobUnits, long upm,
            ulong perf, ulong gameMask, ulong isolation, CoreIsolationNative os, ICoreIsolationStore journal, StreamWriter log)
        {
            var s = new Sample { Config = c.Name, Round = round };
            Process noise = null, game = null;
            CoreIsolationEngine engine = null;
            try
            {
                if (c.Noise != null)
                {
                    noise = Child(c.Noise == "heavy" ? "--noise 32 90 20 " + upm : "--noise 16 45 8 " + upm);
                    if (noise.StandardOutput.ReadLine() != "READY") throw new InvalidOperationException("noise start");
                    if (c.NoiseLow) noise.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                game = Child("--game " + seconds.ToString(CultureInfo.InvariantCulture) + " " + mainUnits + " " + jobUnits + " " + c.Workers + " " + perf.ToString("X"));
                if (game.StandardOutput.ReadLine() != "READY") throw new InvalidOperationException("game start");
                if (c.GameHigh) game.PriorityClass = ProcessPriorityClass.High;
                ulong affinity = c.ExactAffinity ? isolation : gameMask;
                if (c.Isolate)
                {
                    long creation, cpu; ulong io;
                    Native.QueryProcessSample(game.Handle, out creation, out cpu, out io);
                    engine = new CoreIsolationEngine(os, journal);
                    if (!engine.Begin(isolation, game.Id, creation, affinity)) s.Note += "isolation-failed:" + engine.Error + " ";
                }
                if (c.Place && !Native.SetProcessAffinityMask(game.Handle, (UIntPtr)affinity)) s.Note += "place-failed ";
                if (c.DefaultSets && !Native.TrySetCpuSets(game.Handle, CpuSetIds(isolation))) s.Note += "sets-failed ";
                Thread.Sleep(1000);
                game.StandardInput.WriteLine("go");
                string line = game.StandardOutput.ReadLine();
                if (line == null || !line.StartsWith("RESULT ")) throw new InvalidOperationException("game result: " + line);
                string[] p = line.Substring(7).Split(' ');
                s.Frames = D(p[0]); s.AvgFps = D(p[1]); s.Low1 = D(p[2]); s.Low01 = D(p[3]);
                s.P50 = D(p[4]); s.P99 = D(p[5]); s.P999 = D(p[6]); s.Max = D(p[7]); s.OffPerf = D(p[8]);
                s.JobMs = D(p[9]); s.Lps = p[10];
                if (engine != null && engine.Active && !engine.Audit()) s.Note += "audit-failed:" + engine.Error + " ";
            }
            finally
            {
                if (engine != null) { if (!engine.Restore()) s.Note += "restore-failed:" + engine.Error + " "; engine.Dispose(); }
                if (noise != null)
                {
                    try
                    {
                        noise.StandardInput.WriteLine("exit");
                        string done = noise.StandardOutput.ReadLine();
                        if (done != null && done.StartsWith("DONE ")) s.NoiseWork = D(done.Substring(5));
                        if (!noise.WaitForExit(8000)) noise.Kill();
                    }
                    catch { try { noise.Kill(); } catch { } }
                    noise.Dispose();
                }
                if (game != null) { try { if (!game.WaitForExit(8000)) game.Kill(); } catch { } game.Dispose(); }
            }
            return s;
        }

        private static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(byte[] b, int n, out int r, IntPtr h, int flags);

        private static uint[] CpuSetIds(ulong mask)
        {
            int n; GetSystemCpuSetInformation(null, 0, out n, IntPtr.Zero, 0);
            var b = new byte[n];
            if (!GetSystemCpuSetInformation(b, n, out n, IntPtr.Zero, 0)) return new uint[0];
            var ids = new List<uint>();
            for (int i = 0; i < n;)
            {
                uint size = BitConverter.ToUInt32(b, i);
                if (BitConverter.ToUInt16(b, i + 12) == 0 && b[i + 14] < 64 && ((mask >> b[i + 14]) & 1UL) != 0)
                    ids.Add(BitConverter.ToUInt32(b, i + 8));
                i += (int)size;
            }
            return ids.ToArray();
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            values.Sort();
            int m = values.Count / 2;
            return values.Count % 2 == 1 ? values[m] : (values[m - 1] + values[m]) / 2;
        }

        private static void WriteSummary(string output, List<Config> configs, List<Sample> samples, StreamWriter log)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| config | runs | avg fps | 1% low | 0.1% low | p99 ms | p99.9 ms | max ms | jobs off P-cores | noise work |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (Config c in configs)
            {
                var mine = samples.FindAll(delegate(Sample x) { return x.Config == c.Name; });
                if (mine.Count == 0) continue;
                Func<Func<Sample, double>, double> med = delegate(Func<Sample, double> pick)
                {
                    var v = new List<double>(); foreach (Sample x in mine) v.Add(pick(x)); return Median(v);
                };
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2:F1} | {3:F1} | {4:F1} | {5:F3} | {6:F3} | {7:F3} | {8:F1}% | {9:F0} |",
                    c.Name, mine.Count, med(x => x.AvgFps), med(x => x.Low1), med(x => x.Low01), med(x => x.P99),
                    med(x => x.P999), med(x => x.Max), med(x => x.OffPerf), med(x => x.NoiseWork)));
            }
            File.WriteAllText(Path.Combine(output, "core-auto-bench-summary.md"), sb.ToString(), new UTF8Encoding(false));
            log.WriteLine(sb.ToString());
        }
    }
}
