using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal static class CpuCapGuard
    {
        internal const string Argument = "--background-cpu-cap-guard";
        internal static bool TryHandleArgs(string[] args)
        {
            if (args.Length == 0 || args[0] != Argument) return false;
            Environment.ExitCode = 1;
            int pid; long creation;
            if (args.Length != 4 || !int.TryParse(args[1], out pid) || pid <= 4
                || !long.TryParse(args[2], out creation) || creation <= 0
                || !args[3].StartsWith(CpuCapNative.Prefix, StringComparison.Ordinal)
                || args[3].Length != CpuCapNative.Prefix.Length + 33) return true;
            IntPtr parent = Native.OpenProcess(0x100000 | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (parent == IntPtr.Zero) return true;
            try
            {
                long actual, cpu; ulong io;
                if (!Native.QueryProcessSample(parent, out actual, out cpu, out io) || actual != creation) return true;
                Environment.ExitCode = Run(parent, args[3]) ? 0 : 1;
            }
            catch { }
            finally { Native.CloseHandle(parent); }
            return true;
        }

        private static bool Run(IntPtr parent, string prefix)
        {
            var commands = new BlockingCollection<string>(32);
            var reader = new Thread(delegate()
            {
                try { string line; while ((line = Console.ReadLine()) != null)
                    { if (line.Length > 256 || !commands.TryAdd(line)) break; } }
                catch { }
                finally { commands.CompleteAdding(); }
            });
            reader.IsBackground = true; reader.Start();
            var jobs = new Dictionary<string, IntPtr>();
            var targets = new Dictionary<string, IntPtr>();
            bool clean = true;
            try
            {
                Console.WriteLine("READY");
                var silence = Stopwatch.StartNew();
                while (CpuCapNative.IsAlive(parent) && silence.ElapsedMilliseconds < 10000)
                {
                    var dead = new List<string>();
                    foreach (var target in targets)
                        if (!CpuCapNative.IsAlive(target.Value)) dead.Add(target.Key);
                    foreach (string name in dead)
                    { Native.CloseHandle(targets[name]); Native.CloseHandle(jobs[name]); targets.Remove(name); jobs.Remove(name); }
                    string command;
                    if (!commands.TryTake(out command, 250)) { if (commands.IsCompleted) break; continue; }
                    silence.Restart();
                    if (command == "PING") { Console.WriteLine("OK"); continue; }
                    string[] parts = command.Split(' ');
                    int pid; long creation;
                    if (parts.Length != 4 || parts[0] != "WATCH" || !parts[1].StartsWith(prefix, StringComparison.Ordinal)
                        || !int.TryParse(parts[2], out pid) || !long.TryParse(parts[3], out creation)
                        || parts[1] != prefix + pid + "." + creation || jobs.Count >= 256) break;
                    if (jobs.ContainsKey(parts[1])) { Console.WriteLine("OK"); continue; }
                    IntPtr targetHandle = CpuCapNative.OpenTarget(pid, creation);
                    IntPtr job = CpuCapNative.OpenJobObject(0x0002 | 0x0004, false, parts[1]);
                    if (job == IntPtr.Zero || targetHandle == IntPtr.Zero)
                    {
                        if (job != IntPtr.Zero) Native.CloseHandle(job);
                        if (targetHandle != IntPtr.Zero) Native.CloseHandle(targetHandle);
                        Console.WriteLine("ERR"); continue;
                    }
                    jobs.Add(parts[1], job); targets.Add(parts[1], targetHandle);
                    Console.WriteLine("OK");
                }
            }
            finally
            {
                foreach (IntPtr job in jobs.Values)
                {
                    bool restored = false;
                    for (int attempt = 0; attempt < 3 && !restored; attempt++)
                    { restored = CpuCapNative.SetRate(job, 0); if (!restored) Thread.Sleep(100); }
                    clean &= restored; Native.CloseHandle(job);
                }
                foreach (IntPtr target in targets.Values) Native.CloseHandle(target);
            }
            return clean;
        }
    }
}
