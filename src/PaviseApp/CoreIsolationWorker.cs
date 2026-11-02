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

		internal static bool Recover(ICoreIsolationStore journal)
		{
			try
			{
				string value;
				if (!journal.Read(out value))
				{
					return false;
				}
				if (string.IsNullOrEmpty(value))
				{
					return true;
				}
				using (Mutex mutex = Acquire())
				{
					if (mutex == null)
					{
						return false;
					}
					try
					{
						using (CoreIsolationEngine coreIsolationEngine = new CoreIsolationEngine(new CoreIsolationNative(), journal))
						{
							return coreIsolationEngine.Recover();
						}
					}
					finally
					{
						mutex.ReleaseMutex();
					}
				}
			}
			catch
			{
				return false;
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
			using (CoreIsolationEngine coreIsolationEngine = new CoreIsolationEngine(new CoreIsolationNative(), journal))
			{
				if (!coreIsolationEngine.Recover() || !Native.EnsureBoostPrivilege())
				{
					Console.WriteLine("ERR recovery-or-privilege");
					return false;
				}
				BlockingCollection<string> commands = new BlockingCollection<string>(32);
				Thread thread = new Thread((ThreadStart)delegate
				{
					try
					{
						string text;
						while ((text = Console.ReadLine()) != null && text.Length <= 256 && commands.TryAdd(text))
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
							bool flag = coreIsolationEngine.Restore();
							Console.WriteLine(flag ? "OK" : ("ERR " + coreIsolationEngine.Error));
							return flag;
						}
						bool flag2 = Execute(coreIsolationEngine, item);
						Console.WriteLine(flag2 ? "OK" : ("ERR " + (coreIsolationEngine.Error ?? "protocol")));
						if (!flag2)
						{
							break;
						}
					}
				}
				finally
				{
					coreIsolationEngine.Restore();
				}
				return !coreIsolationEngine.Pending;
			}
		}

		private static bool Execute(CoreIsolationEngine engine, string line)
		{
			if (line == "PING")
			{
				return engine.Audit();
			}
			string[] array = line.Split(' ');
			ulong result;
			int result2;
			long result3;
			ulong result4;
			if (array.Length == 5 && (array[0] == "BEGIN" || array[0] == "MANUAL") && ulong.TryParse(array[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result) && int.TryParse(array[2], out result2) && long.TryParse(array[3], out result3) && ulong.TryParse(array[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result4))
			{
				return engine.Begin(result, result2, result3, result4, array[0] == "MANUAL");
			}
			if (array.Length == 4 && array[0] == "ALLOW" && int.TryParse(array[1], out result2) && long.TryParse(array[2], out result3) && ulong.TryParse(array[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result4))
			{
				if (engine.Allow(result2, result3, result4))
				{
					return engine.Audit();
				}
				return false;
			}
			if (array.Length == 3 && array[0] == "DROP" && int.TryParse(array[1], out result2) && long.TryParse(array[2], out result3))
			{
				return engine.Drop(result2, result3);
			}
			return false;
		}
	}
}
