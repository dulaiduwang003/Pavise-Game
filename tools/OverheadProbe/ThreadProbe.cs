// @author bdth 2074055628@qq.com
// 文件用途 按线程拆解目标进程的 CPU 与上下文切换 定位开销归属

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseThreadProbe
{
    internal sealed class ThreadRow
    {
        public int Tid;
        public long Cpu;
        public uint ContextSwitches;
        public ulong StartAddress;
        public int Priority;
        public int State;
        public int WaitReason;
    }

    internal static class Program
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int len, out int ret);

        private const int SystemProcessInformation = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const int ProcessStructSize = 0x100;
        private const int ThreadStructSize = 0x50;

        private static int Main(string[] args)
        {
            string target = "Pavise";
            int seconds = 30;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--name" && i + 1 < args.Length) target = args[++i];
                else if (args[i] == "--seconds" && i + 1 < args.Length)
                    seconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }

            Console.WriteLine("thread probe: name~" + target + " for " + seconds + "s");

            Dictionary<int, ThreadRow> first = Snapshot(target);
            if (first == null) { Console.WriteLine("target not found"); return 1; }
            var sw = Stopwatch.StartNew();
            Thread.Sleep(seconds * 1000);
            Dictionary<int, ThreadRow> second = Snapshot(target);
            double elapsedMs = sw.Elapsed.TotalMilliseconds;
            if (second == null) { Console.WriteLine("target vanished"); return 1; }

            var rows = new List<KeyValuePair<ThreadRow, double>>();
            double totalMs = 0;
            long totalSwitches = 0;
            foreach (KeyValuePair<int, ThreadRow> pair in second)
            {
                ThreadRow before;
                double cpuMs;
                uint switches;
                if (first.TryGetValue(pair.Key, out before))
                {
                    cpuMs = (pair.Value.Cpu - before.Cpu) / 10000.0;
                    switches = pair.Value.ContextSwitches >= before.ContextSwitches
                        ? pair.Value.ContextSwitches - before.ContextSwitches : 0;
                }
                else { cpuMs = pair.Value.Cpu / 10000.0; switches = pair.Value.ContextSwitches; }
                if (cpuMs < 0) cpuMs = 0;
                totalMs += cpuMs;
                totalSwitches += switches;
                pair.Value.ContextSwitches = switches;
                rows.Add(new KeyValuePair<ThreadRow, double>(pair.Value, cpuMs));
            }
            rows.Sort(delegate(KeyValuePair<ThreadRow, double> a, KeyValuePair<ThreadRow, double> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            Console.WriteLine();
            Console.WriteLine("elapsed " + (elapsedMs / 1000.0).ToString("F1") + "s   threads " + rows.Count);
            Console.WriteLine("TOTAL cpu " + totalMs.ToString("F1") + "ms = "
                + (totalMs / elapsedMs * 100).ToString("F4") + "% of one core"
                + "   ctxsw " + totalSwitches + " (" + (totalSwitches / (elapsedMs / 1000.0)).ToString("F1") + "/s)");
            Console.WriteLine();
            Console.WriteLine(string.Format("{0,8} {1,10} {2,9} {3,10} {4,9} {5,18}",
                "tid", "cpu_ms", "cpu_pct", "ctxsw", "ctxsw/s", "start_addr"));
            foreach (KeyValuePair<ThreadRow, double> pair in rows)
            {
                if (pair.Value < 0.5 && pair.Key.ContextSwitches < 20) continue;
                Console.WriteLine(string.Format("{0,8} {1,10} {2,9} {3,10} {4,9} {5,18}",
                    pair.Key.Tid,
                    pair.Value.ToString("F1", CultureInfo.InvariantCulture),
                    (pair.Value / elapsedMs * 100).ToString("F4", CultureInfo.InvariantCulture) + "%",
                    pair.Key.ContextSwitches,
                    (pair.Key.ContextSwitches / (elapsedMs / 1000.0)).ToString("F1", CultureInfo.InvariantCulture),
                    "0x" + pair.Key.StartAddress.ToString("X")));
            }
            return 0;
        }

        private static Dictionary<int, ThreadRow> Snapshot(string targetName)
        {
            int size = 512 * 1024;
            IntPtr buf = IntPtr.Zero;
            try
            {
                while (true)
                {
                    if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                    buf = Marshal.AllocHGlobal(size);
                    int need;
                    int status = NtQuerySystemInformation(SystemProcessInformation, buf, size, out need);
                    if (status == 0) break;
                    if (status != StatusInfoLengthMismatch) return null;
                    size = need + 128 * 1024;
                }

                IntPtr cursor = buf;
                while (true)
                {
                    int nextOffset = Marshal.ReadInt32(cursor, 0);
                    int threadCount = Marshal.ReadInt32(cursor, 4);
                    ushort nameLen = (ushort)Marshal.ReadInt16(cursor, 0x38);
                    IntPtr namePtr = Marshal.ReadIntPtr(cursor, 0x40);
                    string name = namePtr != IntPtr.Zero && nameLen > 0
                        ? Marshal.PtrToStringUni(namePtr, nameLen / 2) : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - 4);

                    if (name.StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        var result = new Dictionary<int, ThreadRow>();
                        IntPtr t = new IntPtr(cursor.ToInt64() + ProcessStructSize);
                        for (int i = 0; i < threadCount; i++)
                        {
                            var row = new ThreadRow
                            {
                                Cpu = Marshal.ReadInt64(t, 0x00) + Marshal.ReadInt64(t, 0x08),
                                StartAddress = (ulong)Marshal.ReadIntPtr(t, 0x20).ToInt64(),
                                Tid = Marshal.ReadIntPtr(t, 0x30).ToInt32(),
                                Priority = Marshal.ReadInt32(t, 0x38),
                                ContextSwitches = (uint)Marshal.ReadInt32(t, 0x40),
                                State = Marshal.ReadInt32(t, 0x44),
                                WaitReason = Marshal.ReadInt32(t, 0x48),
                            };
                            result[row.Tid] = row;
                            t = new IntPtr(t.ToInt64() + ThreadStructSize);
                        }
                        return result;
                    }
                    if (nextOffset == 0) return null;
                    cursor = new IntPtr(cursor.ToInt64() + nextOffset);
                }
            }
            catch { return null; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }
    }
}
