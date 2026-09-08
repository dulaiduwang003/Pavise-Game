using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal static class CoreIsolationWorker
    {
        internal const string Argument = "--core-isolation-worker";
        private const string MutexName = @"Global\Pavise.CoreIsolation.V1";
        private static Mutex Acquire()
        {
            var mutex = new Mutex(false, MutexName);
            try { if (mutex.WaitOne(0)) return mutex; }
            catch (AbandonedMutexException) { return mutex; }
            mutex.Dispose(); return null;
        }

        internal static bool Recover(ICoreIsolationStore journal)
        {
            try
            {
                string value;
                if (!journal.Read(out value)) return false;
                if (string.IsNullOrEmpty(value)) return true;
                using (Mutex mutex = Acquire())
                {
                    if (mutex == null) return false;
                    try { using (var engine = new CoreIsolationEngine(new CoreIsolationNative(), journal)) return engine.Recover(); }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch { return false; }
        }

        internal static bool TryHandleArgs(string[] args)
        {
            if (args.Length == 0 || args[0] != Argument
#if PAVISE_SELFTEST
                && args[0] != "--core-isolation-test-worker"
#endif
                ) return false;
            Environment.ExitCode = 1;
            try
            {
                int pid; long creation;
                if (args.Length != 3 || !int.TryParse(args[1], out pid) || pid <= 0
                    || !long.TryParse(args[2], out creation) || creation <= 0) return true;
                ICoreIsolationStore journal = new CoreIsolationSettingsStore();
#if PAVISE_SELFTEST
                if (args[0] == "--core-isolation-test-worker") journal = new CoreIsolationTestStore();
#endif
                IntPtr parent = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (parent == IntPtr.Zero) return true;
                try
                {
                    long actual, cpu; ulong io;
                    if (!Native.QueryProcessSample(parent, out actual, out cpu, out io) || actual != creation) return true;
                    using (Mutex mutex = Acquire())
                    {
                        if (mutex == null) { Console.WriteLine("ERR busy"); return true; }
                        try { Environment.ExitCode = Run(parent, journal) ? 0 : 1; }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
                finally { Native.CloseHandle(parent); }
            }
            catch { try { Console.WriteLine("ERR worker-exception"); } catch { } }
            return true;
        }

        private static bool Run(IntPtr parent, ICoreIsolationStore journal)
        {
            using (var engine = new CoreIsolationEngine(new CoreIsolationNative(), journal))
            {
                if (!engine.Recover() || !Native.EnsureBoostPrivilege())
                { Console.WriteLine("ERR recovery-or-privilege"); return false; }
                var commands = new BlockingCollection<string>(32);
                var reader = new Thread(delegate()
                {
                    try { string line; while ((line = Console.ReadLine()) != null)
                        { if (line.Length > 256 || !commands.TryAdd(line)) break; } }
                    catch { }
                    finally { commands.CompleteAdding(); }
                });
                reader.IsBackground = true; reader.Start();
                Console.WriteLine("READY");
                var silence = Stopwatch.StartNew();
                try
                {
                    while (Native.StillActive(parent) && silence.ElapsedMilliseconds < 15000)
                    {
                        string command;
                        if (!commands.TryTake(out command, 500))
                        {
                            if (commands.IsCompleted) break;
                            if (engine.Active && !engine.Audit()) break;
                            continue;
                        }
                        silence.Restart();
                        if (command == "STOP")
                        {
                            bool clean = engine.Restore();
                            Console.WriteLine(clean ? "OK" : "ERR " + engine.Error);
                            return clean;
                        }
                        bool ok = Execute(engine, command);
                        Console.WriteLine(ok ? "OK" : "ERR " + (engine.Error ?? "protocol"));
                        if (!ok) break;
                    }
                }
                finally { engine.Restore(); }
                return !engine.Pending;
            }
        }

        private static bool Execute(CoreIsolationEngine engine, string line)
        {
            if (line == "PING") return engine.Audit();
            string[] p = line.Split(' '); int pid; long creation; ulong game, iso;
            if (p.Length == 5 && p[0] == "BEGIN"
                && ulong.TryParse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out iso)
                && int.TryParse(p[2], out pid) && long.TryParse(p[3], out creation)
                && ulong.TryParse(p[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out game))
                return engine.Begin(iso, pid, creation, game);
            if (p.Length == 4 && p[0] == "ALLOW" && int.TryParse(p[1], out pid)
                && long.TryParse(p[2], out creation)
                && ulong.TryParse(p[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out game))
                return engine.Allow(pid, creation, game) && engine.Audit();
            return p.Length == 3 && p[0] == "DROP" && int.TryParse(p[1], out pid)
                && long.TryParse(p[2], out creation) && engine.Drop(pid, creation);
        }
    }

#if PAVISE_SELFTEST
    // Live integration runs use a separate durable receipt beside the test exe.
    // They never touch the user's HKCU settings or normal crash recovery journal.
    internal sealed class CoreIsolationTestStore : ICoreIsolationStore
    {
        internal static string PathName { get { return typeof(CoreIsolationTestStore).Assembly.Location + ".isolation-journal"; } }
        public bool Read(out string value)
        {
            value = "";
            try { if (System.IO.File.Exists(PathName)) value = System.IO.File.ReadAllText(PathName); return true; }
            catch { return false; }
        }
        public bool Write(string value)
        {
            try
            {
                string temp = PathName + ".tmp";
                using (var f = new System.IO.FileStream(temp, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
                { byte[] b = System.Text.Encoding.UTF8.GetBytes(value); f.Write(b, 0, b.Length); f.Flush(true); }
                if (System.IO.File.Exists(PathName)) System.IO.File.Replace(temp, PathName, null);
                else System.IO.File.Move(temp, PathName);
                return true;
            }
            catch { return false; }
        }
    }
#endif
}
