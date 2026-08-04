// @author bdth 2074055628@qq.com
// 文件用途 自测子进程 轮询 断言与性能探针基础设施

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static long MeasureWork(int milliseconds)
        {
            Stopwatch sw = Stopwatch.StartNew();
            long work = 0;
            ulong x = 0x9E3779B97F4A7C15UL;
            while (sw.ElapsedMilliseconds < milliseconds)
            {
                for (int i = 0; i < 4096; i++)
                {
                    x ^= x << 7;
                    x ^= x >> 9;
                    x *= 0xD6E8FEB86659FD93UL;
                }
                work += 4096;
            }
            GC.KeepAlive(x);
            return work;
        }

        private static void RunCpuBurn()
        {
            ulong x = 0xA0761D6478BD642FUL;
            while (true)
            {
                x ^= x << 11;
                x ^= x >> 13;
                x *= 0xE7037ED1A0B428DBUL;
                if ((x & 0xFFFFF) == 7) Thread.SpinWait(1);
            }
        }

        private static void RunProbe(string path)
        {
            int n = 0;
            while (true)
            {
                try { File.WriteAllText(path, (++n).ToString()); }
                catch { }
                Thread.Sleep(80);
            }
        }

        private static Process StartProbe(string beat)
        {
            Process process = Process.Start(Hidden(
                "--test-heartbeat-probe " + Quote(beat)));
            if (process == null) throw new Exception("probe did not start");
            return process;
        }

        private static ProcessStartInfo Hidden(string args)
        {
            return new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }

        private static void StopOwned(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch { }
        }

        private static int ReadCounter(string path)
        {
            try
            {
                int value;
                return int.TryParse(File.ReadAllText(path), out value) ? value : -1;
            }
            catch { return -1; }
        }

        private static void WaitAdvance(string path, int old, int timeout)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeout)
            {
                if (ReadCounter(path) > old) return;
                Thread.Sleep(40);
            }
            throw new Exception("probe heartbeat did not advance");
        }

        private static void WaitFile(string path, int timeout)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeout)
            {
                if (File.Exists(path)) return;
                Thread.Sleep(40);
            }
            throw new Exception("expected file not created: " + Path.GetFileName(path));
        }

        private static void WaitGone(string path, int timeout)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeout)
            {
                if (!File.Exists(path)) return;
                Thread.Sleep(40);
            }
            throw new Exception("recovery journal was not cleared");
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static void Eq<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception("expected " + expected + ", actual " + actual);
        }

        private static void Skip(string reason)
        {
            throw new TestSkippedException(reason);
        }
    }
}
