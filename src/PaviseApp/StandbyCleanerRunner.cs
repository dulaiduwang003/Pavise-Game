using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
	internal sealed class StandbyCleanerRunner
	{
		private sealed class Request
		{
			internal readonly int Generation;

			internal readonly StandbyCleanerOptions Options;

			internal readonly Func<bool> MayContinue;
			internal readonly string Cycle;

			internal Request(int generation, StandbyCleanerOptions options, Func<bool> mayContinue, string cycle)
			{
				Generation = generation;
				Options = options;
				MayContinue = mayContinue;
				Cycle = cycle;
			}
		}

		internal const int PurgeCooldownFloorMilliseconds = 60000;

		internal const int PurgeCooldownIntervals = 8;

		private readonly object gate = new object();

		private readonly object operationGate = new object();

		private readonly AutoResetEvent wake = new AutoResetEvent(false);

		private readonly StandbyCleanerEngine engine;

		private readonly Action<int, StandbyCleanerResult, int> onFault;

		private readonly Func<long> clock;

		private volatile Request current;

		private volatile bool closing;

		private bool closed;

		private readonly HashSet<string> faultedCycles = new HashSet<string>(StringComparer.Ordinal);

		private static string CycleIdentity(StandbyCleanerOptions options)
		{
			return PerformanceTuning.RecoveryCycleSerial + "\n" + options.Serialize();
		}

		private int inFlight;

		private Thread worker;

		private bool purgeCooldownArmed;

		private long lastPurgeMilliseconds;

		internal bool HasInFlight
		{
			get
			{
				return Volatile.Read(ref inFlight) != 0;
			}
		}

		internal StandbyCleanerRunner(StandbyCleanerEngine engine, Action<int, StandbyCleanerResult, int> onFault, Func<long> clock = null)
		{
			if (engine == null)
			{
				throw new ArgumentNullException("engine");
			}
			this.engine = engine;
			this.onFault = onFault;
			this.clock = clock ?? new Func<long>(MonotonicMilliseconds);
		}

		private static long MonotonicMilliseconds()
		{
			return (long)((double)Stopwatch.GetTimestamp() * (1000.0 / (double)Stopwatch.Frequency));
		}

		internal static long CooldownMilliseconds(StandbyCleanerOptions options)
		{
			return Math.Max(60000L, (long)options.PollingMilliseconds * 8L);
		}

		internal bool Update(int generation, StandbyCleanerOptions options, Func<bool> mayContinue)
		{
			lock (gate)
			{
				if (closing)
				{
					return false;
				}
				if (options == null || !options.IsValid)
				{
					current = null;
					wake.Set();
					return false;
				}
				string cycle = CycleIdentity(options);
				ActionFailureStore.BeginAutomaticCycle("GmStandbyCleanerV2", "system", cycle);
				if (ActionFailureStore.IsBlocked("GmStandbyCleanerV2", "system"))
				{
					return false;
				}
				if (faultedCycles.Contains(cycle))
				{
					return false;
				}
				Request request = current;
				if (request != null && request.Generation == generation && request.Cycle == cycle && SameOptions(request.Options, options))
				{
					return true;
				}
				current = new Request(generation, options, mayContinue, cycle);
				if (worker == null)
				{
					Thread thread = new Thread(Loop);
					thread.IsBackground = true;
					thread.Name = "Pavise.StandbyCleaner";
					worker = thread;
					try
					{
						thread.Start();
					}
					catch
					{
						faultedCycles.Add(cycle);
						ActionFailureStore.Trip("GmStandbyCleanerV2", "system", "Standby worker could not start");
						worker = null;
						current = null;
						return false;
					}
				}
				wake.Set();
				return true;
			}
		}

		private static bool SameOptions(StandbyCleanerOptions left, StandbyCleanerOptions right)
		{
			if (left.ListMegabytes == right.ListMegabytes && left.FreeMegabytes == right.FreeMegabytes)
			{
				return left.PollingMilliseconds == right.PollingMilliseconds;
			}
			return false;
		}

		internal void Pause()
		{
			lock (gate)
			{
				current = null;
				if (!closed)
				{
					wake.Set();
				}
			}
		}

		private bool Admitted(Request request)
		{
			if (!closing && object.ReferenceEquals(current, request))
			{
				return StandbyCleanerEngine.MayContinue(request.MayContinue);
			}
			return false;
		}

		private void Loop()
		{
			while (!closing)
			{
				Request request = current;
				StandbyCleanerResult standbyCleanerResult;
				int nativeStatus;
				bool flag;
				if (request == null)
				{
					wake.WaitOne();
				}
				else
				{
					if (wake.WaitOne(request.Options.PollingMilliseconds))
					{
						continue;
					}
					if (!Admitted(request))
					{
						lock (gate)
						{
							if (object.ReferenceEquals(current, request))
							{
								current = null;
							}
						}
					}
					else
					{
						if (purgeCooldownArmed && clock() - lastPurgeMilliseconds < CooldownMilliseconds(request.Options))
						{
							continue;
						}
						bool admittedPurge = false;
						if (ActionFailureStore.IsBlocked("GmStandbyCleanerV2", "system"))
						{
							lock (gate)
							{
								if (object.ReferenceEquals(current, request))
								{
									current = null;
								}
							}
							continue;
						}
						lock (operationGate)
						{
							Interlocked.Exchange(ref inFlight, 1);
							try
							{
								StandbyMemorySnapshot snapshot;
								standbyCleanerResult = engine.Poll(request.Options, () => Admitted(request), out snapshot, out nativeStatus, delegate
								{
									string reason;
									admittedPurge = Admitted(request) && ActionFailureStore.TryBeginApply("GmStandbyCleanerV2", "system", out reason);
									return admittedPurge;
								});
							}
							catch
							{
								standbyCleanerResult = StandbyCleanerResult.QueryFailed;
								nativeStatus = -1073741823;
							}
							finally
							{
								Interlocked.Exchange(ref inFlight, 0);
							}
						}
						if (standbyCleanerResult == StandbyCleanerResult.Purged && nativeStatus == 0)
						{
							purgeCooldownArmed = true;
							lastPurgeMilliseconds = clock();
							if (!ActionFailureStore.CompleteApply("GmStandbyCleanerV2", "system"))
							{
								standbyCleanerResult = StandbyCleanerResult.PurgeFailed;
								nativeStatus = -1073741823;
							}
						}
						else if (standbyCleanerResult == StandbyCleanerResult.Cancelled || standbyCleanerResult == StandbyCleanerResult.BelowThreshold || standbyCleanerResult == StandbyCleanerResult.InvalidOptions)
						{
							if (admittedPurge)
							{
								ActionFailureStore.CancelApply("GmStandbyCleanerV2", "system");
							}
						}
						else
						{
							ActionFailureStore.Trip("GmStandbyCleanerV2", "system", string.Concat(standbyCleanerResult, " | 0x", nativeStatus.ToString("X8")));
						}
						flag = false;
						lock (gate)
						{
							if (closing || !object.ReferenceEquals(current, request))
							{
								continue;
							}
							if (standbyCleanerResult == StandbyCleanerResult.Cancelled)
							{
								current = null;
							}
							else if (standbyCleanerResult == StandbyCleanerResult.QueryFailed || standbyCleanerResult == StandbyCleanerResult.PurgeFailed || (standbyCleanerResult == StandbyCleanerResult.Purged && nativeStatus != 0))
							{
								current = null;
								faultedCycles.Add(request.Cycle);
								flag = true;
							}
							else if (standbyCleanerResult == StandbyCleanerResult.InvalidOptions)
							{
								current = null;
							}
							goto ReportCleanerFault;
						}
					}
				}
				continue;
				ReportCleanerFault:
				if (flag && onFault != null)
				{
					try
					{
						onFault(request.Generation, standbyCleanerResult, nativeStatus);
					}
					catch
					{
					}
				}
			}
		}

		internal bool Drain(int timeoutMs)
		{
			if (timeoutMs < 0 || Monitor.IsEntered(operationGate) || !Monitor.TryEnter(operationGate, timeoutMs))
			{
				return false;
			}
			Monitor.Exit(operationGate);
			return true;
		}

		internal bool Close(int timeoutMs)
		{
			Thread thread;
			lock (gate)
			{
				if (closed)
				{
					return true;
				}
				closing = true;
				current = null;
				wake.Set();
				thread = worker;
			}
			if (timeoutMs < 0 || thread == Thread.CurrentThread || (thread != null && !thread.Join(timeoutMs)))
			{
				return false;
			}
			lock (gate)
			{
				if (!closed)
				{
					closed = true;
					worker = null;
					wake.Close();
				}
			}
			return true;
		}
	}
}
