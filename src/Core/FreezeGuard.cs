using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    // 守护进程退出后，内核关闭状态句柄并解除挂起。
    internal static class FreezeGuard
    {
        internal const string Argument = "--background-freeze-guard";
        internal const int StaleMs = 12000;
        internal const int MaxTargets = 4096;
        internal static bool TryHandleArgs(string[] args)
        {
            if (args.Length == 0 || args[0] != Argument) return false;
            Environment.ExitCode = 1;
            int pid; long creation;
            if (args.Length != 3 || !int.TryParse(args[1], out pid) || pid <= 4
                || !long.TryParse(args[2], out creation) || creation <= 0 || !FreezeNative.Supported) return true;
            IntPtr parent = Native.OpenProcess(0x100000 | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (parent == IntPtr.Zero) return true;
            try
            {
                long actual, cpu; ulong io; int session;
                if (!Native.QueryProcessSample(parent, out actual, out cpu, out io) || actual != creation
                    || !Native.TryGetLiveProcessSessionId(parent, pid, out session) || session == 0
                    || !string.Equals(Native.ImagePath(parent), typeof(FreezeGuard).Assembly.Location, StringComparison.OrdinalIgnoreCase)) return true;
                Run(parent, pid, (int)session);
            }
            catch { Environment.ExitCode = 7; }
            finally { Native.CloseHandle(parent); }
            return true;
        }

        internal static bool Parse(string line, out int gamePid, out long creation, out Dictionary<int, FreezeTarget> wanted)
        {
            gamePid = 0; creation = 0; wanted = new Dictionary<int, FreezeTarget>();
            if (line == null || line.Length > 1024 * 1024) return false;
            string[] words = line.Split(' ');
            if (words.Length < 3 || words.Length > MaxTargets + 3 || words[0] != "SYNC"
                || !int.TryParse(words[1], out gamePid) || gamePid <= 4
                || !long.TryParse(words[2], out creation) || creation <= 0) return false;
            try
            {
                for (int i = 3; i < words.Length; i++)
                {
                    string[] fields = words[i].Split(':'); int pid; long born;
                    if (fields.Length != 3 || !int.TryParse(fields[0], out pid) || pid <= 4 || pid == gamePid
                        || !long.TryParse(fields[1], out born) || born <= 0 || wanted.ContainsKey(pid)) return false;
                    string path = Encoding.UTF8.GetString(Convert.FromBase64String(fields[2]));
                    if (string.IsNullOrEmpty(path) || path.Length > 32768 || path.IndexOf('\0') >= 0) return false;
                    wanted.Add(pid, new FreezeTarget { Pid = pid, Creation = born, Path = path });
                }
                return true;
            }
            catch { return false; }
        }

        private static void Run(IntPtr parent, int parentPid, int session,
            Func<FreezeTarget, IDisposable> testAcquire = null, int staleMs = StaleMs)
        {
            Environment.ExitCode = 0;
            var trace = new FreezeTrace();
            var commands = new BlockingCollection<string>(2);
            var reader = new Thread(delegate()
            {
                try { string line; while ((line = Console.ReadLine()) != null)
                    { if (line.Length > 1024 * 1024 || !commands.TryAdd(line)) break; } }
                catch { }
                finally { commands.CompleteAdding(); }
            });
            reader.IsBackground = true; reader.Start();
            IntPtr gameHandle = IntPtr.Zero;
            int helperPid; using (Process self = Process.GetCurrentProcess()) helperPid = self.Id;
            var silence = Stopwatch.StartNew();
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            long lastSync = (long)Native.GetTickCount64();
            var watchdogStop = new ManualResetEvent(false);
            var watchdog = new Thread(delegate()
            {
                long lastParentAlive = (long)Native.GetTickCount64();
                while (!watchdogStop.WaitOne(100))
                {
                    long now = (long)Native.GetTickCount64();
                    if (FreezeNative.IsAlive(parent)) lastParentAlive = now;
                    if (now - Interlocked.Read(ref lastSync) > staleMs + 1000L || now - lastParentAlive > 1000)
                        Environment.Exit(2);
                }
            });
            watchdog.IsBackground = true; watchdog.Start();
            using (var leases = new FreezeLeaseSet())
            try
            {
                leases.Trace = trace.Add;
                Console.WriteLine("READY"); Console.Out.Flush();
                Func<bool> allowed = delegate { return !commands.IsCompleted && FreezeNative.IsAlive(parent)
                    && silence.ElapsedMilliseconds < staleMs && gameHandle != IntPtr.Zero
                    && FreezeNative.IsAlive(gameHandle); };
                while (FreezeNative.IsAlive(parent) && silence.ElapsedMilliseconds < staleMs)
                {
                    if (!allowed())
                    {
                        if (leases.Count > 0) trace.Add("gate-release reason=" + (!FreezeNative.IsAlive(parent) ? "parent-exited"
                            : commands.IsCompleted ? "input-closed" : silence.ElapsedMilliseconds >= staleMs ? "stale-scan"
                            : "game-exited-or-invalid"));
                        leases.Dispose();
                    }
                    string command;
                    if (!commands.TryTake(out command, 100)) { if (commands.IsCompleted) break; continue; }
                    if (command == "REPORT")
                    { Console.WriteLine(trace.Drain()); Console.Out.Flush(); continue; }
                    if (command == "CLEAR")
                    { trace.Add("gate-release reason=clear-command"); leases.Dispose(); Console.WriteLine("OK"); Console.Out.Flush(); continue; }
                    if (command.StartsWith("RELEASE ", StringComparison.Ordinal))
                    {
                        string[] release = command.Split(' '); int pid; long creation;
                        if (release.Length != 3 || !int.TryParse(release[1], out pid) || pid <= 4
                            || !long.TryParse(release[2], out creation) || creation <= 0) { Environment.ExitCode = 6; break; }
                        leases.Release(pid, creation); Console.WriteLine("OK"); Console.Out.Flush(); continue;
                    }
                    int nextGame; long born; Dictionary<int, FreezeTarget> wanted;
                    if (!Parse(command, out nextGame, out born, out wanted)) { Environment.ExitCode = 6; break; }
                    // 旧游戏 PID 不能授权操作复用后的进程。
                    if (gameHandle != IntPtr.Zero) Native.CloseHandle(gameHandle);
                    gameHandle = Native.OpenProcess(0x100000 | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, nextGame);
                    long actual, cpu; ulong io;
                    if (gameHandle == IntPtr.Zero || !Native.QueryProcessSample(gameHandle, out actual, out cpu, out io) || actual != born)
                    {
                        trace.Add("anchor-rejected pid=" + nextGame + " born=" + born + " reason=unavailable-or-identity-mismatch");
                        if (gameHandle != IntPtr.Zero) Native.CloseHandle(gameHandle);
                        gameHandle = IntPtr.Zero;
                    }
                    silence.Restart();
                    Interlocked.Exchange(ref lastSync, (long)Native.GetTickCount64());
                    leases.Sync(wanted, allowed, delegate(FreezeTarget target)
                    {
                        if (target.Pid == parentPid || target.Pid == helperPid
                            || FamilyBoundary.UnderRoot(target.Path, windows))
                        { trace.Add("refused " + FreezeTrace.Identity(target) + " reason=guardian-protection fallback=job-candidate"); return null; }
                        IDisposable acquired;
                        string detail;
#if PAVISE_SELFTEST
                        if (testAcquire != null) { acquired = testAcquire(target); detail = "simulated-lease"; }
                        else
#endif
                            acquired = FreezeNative.Acquire(target, session, parentPid, out detail);
                        trace.Add((acquired == null ? "refused " : "acquired ") + FreezeTrace.Identity(target)
                            + " result=" + detail + (acquired == null ? " fallback=job-candidate" : ""));
                        return acquired;
                    });
                    var ids = new List<string>(); foreach (int id in leases.Pids) ids.Add(id.ToString());
                    Console.WriteLine("OK " + string.Join(",", ids.ToArray())); Console.Out.Flush();
                }
            }
            finally
            {
                if (Environment.ExitCode == 0)
                    Environment.ExitCode = !FreezeNative.IsAlive(parent) ? 3
                        : silence.ElapsedMilliseconds >= staleMs ? 5 : commands.IsCompleted ? 4 : 0;
                leases.Dispose();
                if (gameHandle != IntPtr.Zero) Native.CloseHandle(gameHandle);
                watchdogStop.Set(); watchdog.Join(); watchdogStop.Dispose();
            }
        }
#if PAVISE_SELFTEST
        internal static void RunIsolatedTestGuard(int parentPid, long creation,
            Func<FreezeTarget, IDisposable> acquire, int staleMs)
        {
            IntPtr parent = Native.OpenProcess(0x100000 | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, parentPid);
            if (parent == IntPtr.Zero) throw new InvalidOperationException("test-parent");
            try
            {
                long actual, cpu; ulong io; int session;
                if (!Native.QueryProcessSample(parent, out actual, out cpu, out io) || actual != creation
                    || !Native.TryGetLiveProcessSessionId(parent, parentPid, out session)) throw new InvalidOperationException("test-parent-identity");
                Run(parent, parentPid, session, acquire, staleMs);
            }
            finally { Native.CloseHandle(parent); }
        }
#endif
    }
}
