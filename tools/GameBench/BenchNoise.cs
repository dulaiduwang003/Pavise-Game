// Bench noise: a stand-in background program for suppression checks. Burns a duty-cycled share of CPU on N
// threads and optionally reads/writes a small owned file every second. C# 5 (csc v4).
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace PaviseBench
{
    internal static class BenchNoise
    {
        private static volatile bool stop;
        private static long work;

        private static void Burn(object state)
        {
            int duty = (int)state;
            var clock = Stopwatch.StartNew();
            double x = 1.0001;
            while (!stop)
            {
                long windowStart = clock.ElapsedTicks;
                long busy = Stopwatch.Frequency / 100 * duty / 100;
                while (clock.ElapsedTicks - windowStart < busy) { x = x * 1.0000001 + 0.0000001; Interlocked.Increment(ref work); }
                int rest = 10 - 10 * duty / 100;
                if (rest > 0) Thread.Sleep(rest);
            }
            if (x == 0) Console.WriteLine(x);
        }

        private static int Main(string[] args)
        {
            int threads = 2, duty = 50; double seconds = 60; bool io = false; string stopFlag = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--threads") threads = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--duty") duty = Math.Max(1, Math.Min(100, int.Parse(args[++i], CultureInfo.InvariantCulture)));
                else if (args[i] == "--seconds") seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--io") io = true;
                else if (args[i] == "--stop-flag") stopFlag = args[++i];
            }
            for (int i = 0; i < threads; i++) new Thread(Burn) { IsBackground = true }.Start(duty);
            string file = Path.Combine(Path.GetTempPath(), "BenchNoise-" + Process.GetCurrentProcess().Id + ".bin");
            var data = new byte[1 << 20];
            var clock = Stopwatch.StartNew();
            try
            {
                while (clock.Elapsed.TotalSeconds < seconds && (stopFlag == null || !File.Exists(stopFlag)))
                {
                    if (io)
                    {
                        File.WriteAllBytes(file, data);
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.WriteThrough)) fs.Read(data, 0, data.Length);
                    }
                    Thread.Sleep(1000);
                }
            }
            finally { stop = true; try { File.Delete(file); } catch { } }
            Console.WriteLine("work=" + Interlocked.Read(ref work));
            return 0;
        }
    }
}
