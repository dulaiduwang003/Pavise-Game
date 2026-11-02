using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal static class FreezeGuard
	{
		internal const string Argument = "--background-freeze-guard";

		internal const int StaleMs = 12000;

		internal const int MaxTargets = 4096;

		internal static bool TryHandleArgs(string[] args)
		{
			if (args.Length == 0 || args[0] != "--background-freeze-guard")
			{
				return false;
			}
			Environment.ExitCode = 1;
			int result;
			long result2;
			if (args.Length != 3 || !int.TryParse(args[1], out result) || result <= 4 || !long.TryParse(args[2], out result2) || result2 <= 0 || !FreezeNative.Supported)
			{
				return true;
			}
			IntPtr intPtr = Native.OpenProcess(1052672, false, result);
			if (intPtr == IntPtr.Zero)
			{
				return true;
			}
			try
			{
				long creation;
				long cpu;
				ulong io;
				int sessionId;
				if (!Native.QueryProcessSample(intPtr, out creation, out cpu, out io) || creation != result2 || !Native.TryGetLiveProcessSessionId(intPtr, result, out sessionId) || sessionId == 0 || !string.Equals(Native.ImagePath(intPtr), typeof(FreezeGuard).Assembly.Location, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				Run(intPtr, result, sessionId);
			}
			catch
			{
				Environment.ExitCode = 7;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
			return true;
		}

		internal static bool Parse(string line, out int gamePid, out long creation, out Dictionary<int, FreezeTarget> wanted)
		{
			gamePid = 0;
			creation = 0L;
			wanted = new Dictionary<int, FreezeTarget>();
			if (line == null || line.Length > 1048576)
			{
				return false;
			}
			string[] array = line.Split(' ');
			if (array.Length < 3 || array.Length > 4099 || array[0] != "SYNC" || !int.TryParse(array[1], out gamePid) || gamePid <= 4 || !long.TryParse(array[2], out creation) || creation <= 0)
			{
				return false;
			}
			try
			{
				for (int i = 3; i < array.Length; i++)
				{
					string[] array2 = array[i].Split(':');
					int result;
					long result2;
					if (array2.Length != 3 || !int.TryParse(array2[0], out result) || result <= 4 || result == gamePid || !long.TryParse(array2[1], out result2) || result2 <= 0 || wanted.ContainsKey(result))
					{
						return false;
					}
					string @string = Encoding.UTF8.GetString(Convert.FromBase64String(array2[2]));
					if (string.IsNullOrEmpty(@string) || @string.Length > 32768 || @string.IndexOf('\0') >= 0)
					{
						return false;
					}
					wanted.Add(result, new FreezeTarget
					{
						Pid = result,
						Creation = result2,
						Path = @string
					});
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static void Run(IntPtr parent, int parentPid, int session, Func<FreezeTarget, IDisposable> testAcquire = null, int staleMs = 12000)
		{
			Environment.ExitCode = 0;
			FreezeTrace trace = new FreezeTrace();
			BlockingCollection<string> commands = new BlockingCollection<string>(2);
			Thread thread = new Thread((ThreadStart)delegate
			{
				try
				{
					string text;
					while ((text = Console.ReadLine()) != null && text.Length <= 1048576 && commands.TryAdd(text))
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
			IntPtr gameHandle = IntPtr.Zero;
			int helperPid;
			using (Process process = Process.GetCurrentProcess())
			{
				helperPid = process.Id;
			}
			Stopwatch silence = Stopwatch.StartNew();
			string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			long lastSync = (long)Native.GetTickCount64();
			ManualResetEvent watchdogStop = new ManualResetEvent(false);
			Thread thread2 = new Thread((ThreadStart)delegate
			{
				long num2 = (long)Native.GetTickCount64();
				while (!watchdogStop.WaitOne(100))
				{
					long tickCount = (long)Native.GetTickCount64();
					if (FreezeNative.IsAlive(parent))
					{
						num2 = tickCount;
					}
					if (tickCount - Interlocked.Read(ref lastSync) > (long)staleMs + 1000L || tickCount - num2 > 1000)
					{
						Environment.Exit(2);
					}
				}
			});
			thread2.IsBackground = true;
			thread2.Start();
			using (FreezeLeaseSet freezeLeaseSet = new FreezeLeaseSet())
			{
				try
				{
					freezeLeaseSet.Trace = trace.Add;
					Console.WriteLine("READY");
					Console.Out.Flush();
					Func<bool> func = () => !commands.IsCompleted && FreezeNative.IsAlive(parent) && silence.ElapsedMilliseconds < staleMs && gameHandle != IntPtr.Zero && FreezeNative.IsAlive(gameHandle);
					while (FreezeNative.IsAlive(parent) && silence.ElapsedMilliseconds < staleMs)
					{
						if (!func())
						{
							if (freezeLeaseSet.Count > 0)
							{
								trace.Add("gate-release reason=" + ((!FreezeNative.IsAlive(parent)) ? "parent-exited" : (commands.IsCompleted ? "input-closed" : ((silence.ElapsedMilliseconds >= staleMs) ? "stale-scan" : "game-exited-or-invalid"))));
							}
							freezeLeaseSet.Dispose();
						}
						string item;
						if (!commands.TryTake(out item, 100))
						{
							if (commands.IsCompleted)
							{
								break;
							}
							continue;
						}
						if (item == "REPORT")
						{
							Console.WriteLine(trace.Drain());
							Console.Out.Flush();
							continue;
						}
						if (item == "CLEAR")
						{
							trace.Add("gate-release reason=clear-command");
							freezeLeaseSet.Dispose();
							Console.WriteLine("OK");
							Console.Out.Flush();
							continue;
						}
						if (item.StartsWith("RELEASE ", StringComparison.Ordinal))
						{
							string[] array = item.Split(' ');
							int result;
							long result2;
							if (array.Length != 3 || !int.TryParse(array[1], out result) || result <= 4 || !long.TryParse(array[2], out result2) || result2 <= 0)
							{
								Environment.ExitCode = 6;
								break;
							}
							freezeLeaseSet.Release(result, result2);
							Console.WriteLine("OK");
							Console.Out.Flush();
							continue;
						}
						int gamePid;
						long creation;
						Dictionary<int, FreezeTarget> wanted;
						if (!Parse(item, out gamePid, out creation, out wanted))
						{
							Environment.ExitCode = 6;
							break;
						}
						if (gameHandle != IntPtr.Zero)
						{
							Native.CloseHandle(gameHandle);
						}
						gameHandle = Native.OpenProcess(1052672, false, gamePid);
						long creation2;
						long cpu;
						ulong io;
						if (gameHandle == IntPtr.Zero || !Native.QueryProcessSample(gameHandle, out creation2, out cpu, out io) || creation2 != creation)
						{
							trace.Add("anchor-rejected pid=" + gamePid + " born=" + creation + " reason=unavailable-or-identity-mismatch");
							if (gameHandle != IntPtr.Zero)
							{
								Native.CloseHandle(gameHandle);
							}
							gameHandle = IntPtr.Zero;
						}
						silence.Restart();
						Interlocked.Exchange(ref lastSync, (long)Native.GetTickCount64());
						freezeLeaseSet.Sync(wanted, func, delegate(FreezeTarget target)
						{
							if (target.Pid == parentPid || target.Pid == helperPid || FamilyBoundary.UnderRoot(target.Path, windows))
							{
								trace.Add("refused " + FreezeTrace.Identity(target) + " reason=guardian-protection fallback=job-candidate");
								return (IDisposable)null;
							}
							string detail;
							IDisposable disposable = FreezeNative.Acquire(target, session, parentPid, out detail);
							trace.Add(((disposable == null) ? "refused " : "acquired ") + FreezeTrace.Identity(target) + " result=" + detail + ((disposable == null) ? " fallback=job-candidate" : ""));
							return disposable;
						});
						List<string> list = new List<string>();
						int[] pids = freezeLeaseSet.Pids;
						foreach (int num in pids)
						{
							list.Add(num.ToString());
						}
						Console.WriteLine("OK " + string.Join(",", list.ToArray()));
						Console.Out.Flush();
					}
				}
				finally
				{
					if (Environment.ExitCode == 0)
					{
						Environment.ExitCode = ((!FreezeNative.IsAlive(parent)) ? 3 : ((silence.ElapsedMilliseconds >= staleMs) ? 5 : (commands.IsCompleted ? 4 : 0)));
					}
					freezeLeaseSet.Dispose();
					if (gameHandle != IntPtr.Zero)
					{
						Native.CloseHandle(gameHandle);
					}
					watchdogStop.Set();
					thread2.Join();
					watchdogStop.Dispose();
				}
			}
		}
	}
}
