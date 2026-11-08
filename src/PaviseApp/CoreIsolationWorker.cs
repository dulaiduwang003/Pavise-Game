using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
	internal static class CoreIsolationWorker
	{
		internal const string Argument = "--core-isolation-worker";

		private const string MutexName = "Global\\Pavise.CoreIsolation.V1";

		private static Mutex Acquire()
		{
			Mutex mutex = new Mutex(false, "Global\\Pavise.CoreIsolation.V1");
			try
			{
				if (mutex.WaitOne(0))
				{
					return mutex;
				}
			}
			catch (AbandonedMutexException)
			{
				return mutex;
			}
			mutex.Dispose();
			return null;
		}

        private static readonly object recoveryGate = new object();
        private static readonly Dictionary<object, CoreIsolationEngine> recoveryOwners = new Dictionary<object, CoreIsolationEngine>();
        private static long automaticSession;
        internal static void SyncAutomaticSession(long session)
        {
            lock (recoveryGate)
            {
                if (session <= automaticSession) return;
                automaticSession = session;
                foreach (CoreIsolationEngine owner in recoveryOwners.Values) owner.SyncAutomaticSession(session);
            }
        }
        private static CoreIsolationEngine CreateEngine(ICoreIsolationStore journal)
        { return new CoreIsolationEngine(new CoreIsolationNative(), journal); }
        internal static bool Recover(ICoreIsolationStore journal)
        {
            lock (recoveryGate)
            {
                object key = journal is CoreIsolationSettingsStore ? (object)typeof(CoreIsolationSettingsStore) : journal;
                CoreIsolationEngine engine;
                if (!recoveryOwners.TryGetValue(key, out engine))
                {
                    engine = CreateEngine(journal); engine.SyncAutomaticSession(automaticSession);
                    recoveryOwners[key] = engine;
                }
                if (!engine.RecoveryDue) return false;
                try
                {
                    using (Mutex mutex = Acquire())
                    {
                        if (mutex == null) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
                        try
                        {
                            if (!engine.Recover()) return false;
                            engine.Dispose(); recoveryOwners.Remove(key); return true;
                        }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
                catch { return false; }
            }
        }
        private static void RecoveryWait() { Thread.Sleep(1000); }
        private static void DrainRecovery(CoreIsolationEngine engine)
        {
            // Once commands fail, this guardian keeps its handles and mutex. Only
            // due finite attempts run; after exhaustion it observes target lifetime.
            while (engine.Pending)
            {
                try { if (engine.RecoveryDue && engine.Restore()) return; }
                catch { }
                RecoveryWait();
            }
        }

		internal static bool TryHandleArgs(string[] args)
		{
			if (args.Length == 0 || args[0] != "--core-isolation-worker")
			{
				return false;
			}
			Environment.ExitCode = 1;
			try
			{
				int result;
				long result2;
				if (args.Length != 3 || !int.TryParse(args[1], out result) || result <= 0 || !long.TryParse(args[2], out result2) || result2 <= 0)
				{
					return true;
				}
				ICoreIsolationStore journal = new CoreIsolationSettingsStore();
				IntPtr intPtr = Native.OpenProcess(4096, false, result);
				if (intPtr == IntPtr.Zero)
				{
					return true;
				}
				try
				{
					long creation;
					long cpu;
					ulong io;
					if (!Native.QueryProcessSample(intPtr, out creation, out cpu, out io) || creation != result2)
					{
						return true;
					}
					using (Mutex mutex = Acquire())
					{
						if (mutex == null)
						{
							Console.WriteLine("ERR busy");
							return true;
						}
						try
						{
							Environment.ExitCode = ((!Run(intPtr, journal)) ? 1 : 0);
						}
						finally
						{
							mutex.ReleaseMutex();
						}
					}
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
			}
			catch
			{
				try
				{
					Console.WriteLine("ERR worker-exception");
				}
				catch
				{
				}
			}
			return true;
		}

		private static bool Run(IntPtr parent, ICoreIsolationStore journal)
		{
            CoreIsolationEngine coreIsolationEngine = CreateEngine(journal);
            try { return RunOwned(parent, coreIsolationEngine); }
            finally { DrainRecovery(coreIsolationEngine); coreIsolationEngine.Dispose(); }
        }
        private static bool RunOwned(IntPtr parent, CoreIsolationEngine coreIsolationEngine)
        {
				if (!coreIsolationEngine.Recover() || !Native.EnsureBoostPrivilege())
				{
					Console.WriteLine("ERR recovery-or-privilege");
                    DrainRecovery(coreIsolationEngine);
					return false;
				}
				BlockingCollection<string> commands = new BlockingCollection<string>(32);
				Thread thread = new Thread((ThreadStart)delegate
				{
					try
					{
						string line;
						while ((line = Console.ReadLine()) != null && line.Length <= 256 && commands.TryAdd(line))
						{
						}
					}
					catch
					{
					}
					finally
					{
						commands.CompleteAdding();
					}
				});
				thread.IsBackground = true;
				thread.Start();
				Console.WriteLine("READY");
				Stopwatch stopwatch = Stopwatch.StartNew();
				try
				{
					while (Native.StillActive(parent) && stopwatch.ElapsedMilliseconds < 15000)
					{
						string item;
						if (!commands.TryTake(out item, 500))
						{
							if (commands.IsCompleted || (coreIsolationEngine.Active && !coreIsolationEngine.Audit()))
							{
								break;
							}
							continue;
						}
						stopwatch.Restart();
						if (item == "STOP")
						{
							bool restoreSucceeded = coreIsolationEngine.Restore();
							Console.WriteLine(restoreSucceeded ? "OK" : ("ERR " + coreIsolationEngine.Error));
							return restoreSucceeded;
						}
						bool executeSucceeded = Execute(coreIsolationEngine, item);
						Console.WriteLine(executeSucceeded ? "OK" : ("ERR " + (coreIsolationEngine.Error ?? "protocol")));
						if (!executeSucceeded)
						{
							break;
						}
					}
				}
				finally
				{
                    DrainRecovery(coreIsolationEngine);
				}
				return !coreIsolationEngine.Pending;
		}

		private static bool Execute(CoreIsolationEngine engine, string line)
		{
			if (line == "PING")
			{
				return engine.Audit();
			}
			string[] lineParts = line.Split(' ');
			ulong result;
			int result2;
			long result3;
			ulong result4;
			if (lineParts.Length == 5 && (lineParts[0] == "BEGIN" || lineParts[0] == "MANUAL") && ulong.TryParse(lineParts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result) && int.TryParse(lineParts[2], out result2) && long.TryParse(lineParts[3], out result3) && ulong.TryParse(lineParts[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result4))
			{
				return engine.Begin(result, result2, result3, result4, lineParts[0] == "MANUAL");
			}
			if (lineParts.Length == 4 && lineParts[0] == "ALLOW" && int.TryParse(lineParts[1], out result2) && long.TryParse(lineParts[2], out result3) && ulong.TryParse(lineParts[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result4))
			{
				if (engine.Allow(result2, result3, result4))
				{
					return engine.Audit();
				}
				return false;
			}
			if (lineParts.Length == 3 && lineParts[0] == "DROP" && int.TryParse(lineParts[1], out result2) && long.TryParse(lineParts[2], out result3))
			{
				return engine.Drop(result2, result3);
			}
			return false;
		}
	}
}
