using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
	internal sealed class ManualMemoryCleaner
	{
		internal const int StatusUnsuccessful = -1073741823;
		internal const int StatusGameProtected = -1073741536;

		internal static readonly ManualMemoryStep[] OrderedSteps = new ManualMemoryStep[6]
		{
			ManualMemoryStep.CombinePages,
			ManualMemoryStep.WorkingSets,
			ManualMemoryStep.FileCache,
			ManualMemoryStep.RegistryCache,
			ManualMemoryStep.ModifiedPages,
			ManualMemoryStep.StandbyLists
		};

		private readonly IManualMemoryNative native;

		private readonly Action<int> wait;
		private readonly Func<bool> gamePresent;

		internal ManualMemoryCleaner(IManualMemoryNative native, Action<int> wait = null, Func<bool> gamePresent = null)
		{
			if (native == null)
			{
				throw new ArgumentNullException("native");
			}
			this.native = native;
			this.wait = wait ?? new Action<int>(Thread.Sleep);
			this.gamePresent = gamePresent;
		}

		internal static bool ProtectGame(Func<bool> predicate)
		{
			try { return predicate != null && predicate(); }
			catch { return true; }
		}

		internal ManualMemoryReport Run(Func<bool> mayContinue, Action<int, ManualMemoryStep> progress)
		{
			ManualMemoryReport manualMemoryReport = new ManualMemoryReport();
			if (Interlocked.CompareExchange(ref StandbyMemoryControl.ManualCleanupActive, 1, 0) != 0)
			{
				manualMemoryReport.Busy = true;
				return manualMemoryReport;
			}
			Stopwatch stopwatch = Stopwatch.StartNew();
			bool runReady = false;
			try
			{
				while (!(runReady = Monitor.TryEnter(StandbyMemoryControl.OperationGate, 100)))
				{
					if (!MayContinue(mayContinue))
					{
						manualMemoryReport.Cancelled = true;
						return manualMemoryReport;
					}
				}
				manualMemoryReport.Before = Sample(mayContinue);
				ManualMemoryStep[] orderedSteps = OrderedSteps;
				for (int stepIndex = 0; stepIndex < orderedSteps.Length; stepIndex++)
				{
					if (!MayContinue(mayContinue))
					{
						manualMemoryReport.Cancelled = true;
						break;
					}
					if (!manualMemoryReport.GameProtected && ProtectGame(gamePresent))
					{
						manualMemoryReport.GameProtected = true;
						manualMemoryReport.EscalatedToGameProtection = manualMemoryReport.Steps.Count > 0;
						orderedSteps = new[] { ManualMemoryStep.PaviseMemory };
						stepIndex = 0;
						manualMemoryReport.ExpectedSteps = 1;
					}
					ManualMemoryStep manualMemoryStep = orderedSteps[stepIndex];
					if (progress != null)
					{
						try
						{
							progress(stepIndex + 1, manualMemoryStep);
						}
						catch
						{
						}
					}
					if (!MayContinue(mayContinue))
					{
						manualMemoryReport.Cancelled = true;
						break;
					}
					Stopwatch stopwatch2 = Stopwatch.StartNew();
					// Progress callbacks and privilege acquisition can yield; recheck before each global action.
					if (!manualMemoryReport.GameProtected && ProtectGame(gamePresent))
					{
						manualMemoryReport.GameProtected = true;
						manualMemoryReport.EscalatedToGameProtection = manualMemoryReport.Steps.Count > 0;
						manualMemoryReport.ExpectedSteps = 1;
						orderedSteps = new[] { ManualMemoryStep.PaviseMemory };
						stepIndex = -1;
						continue;
					}
					int executeResult;
					try
					{
						executeResult = native.Execute(manualMemoryStep);
					}
					catch
					{
						executeResult = -1073741823;
					}
					manualMemoryReport.Steps.Add(new ManualMemoryStepResult
					{
						Step = manualMemoryStep,
						NativeStatus = executeResult,
						State = Classify(executeResult),
						ElapsedMilliseconds = stopwatch2.ElapsedMilliseconds
					});
					if (!manualMemoryReport.GameProtected && (executeResult == StatusGameProtected || ProtectGame(gamePresent)))
					{
						manualMemoryReport.GameProtected = true;
						manualMemoryReport.EscalatedToGameProtection = true;
						orderedSteps = new[] { ManualMemoryStep.PaviseMemory };
						manualMemoryReport.ExpectedSteps = 1;
						stepIndex = -1;
						continue;
					}
					if (manualMemoryStep == ManualMemoryStep.ModifiedPages && executeResult >= 0 && !Pause(500, mayContinue))
					{
						manualMemoryReport.Cancelled = true;
						break;
					}
				}
				if (!manualMemoryReport.Cancelled && Pause(400, mayContinue))
				{
					manualMemoryReport.After = Sample(mayContinue);
				}
				if (!MayContinue(mayContinue))
				{
					manualMemoryReport.Cancelled = true;
				}
				return manualMemoryReport;
			}
			finally
			{
				manualMemoryReport.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
				if (runReady)
				{
					Monitor.Exit(StandbyMemoryControl.OperationGate);
				}
				Interlocked.Exchange(ref StandbyMemoryControl.ManualCleanupActive, 0);
			}
		}

		private ManualMemorySnapshot Sample(Func<bool> mayContinue)
		{
			List<ManualMemorySnapshot> list = new List<ManualMemorySnapshot>();
			for (int i = 0; i < 3; i++)
			{
				if (!MayContinue(mayContinue))
				{
					break;
				}
				try
				{
					ManualMemorySnapshot snapshot;
					if (native.TryQuery(out snapshot) && snapshot != null && snapshot.IsValid)
					{
						list.Add(snapshot);
					}
				}
				catch
				{
				}
				if (i < 2 && !Pause(100, mayContinue))
				{
					break;
				}
			}
			if (list.Count < 2)
			{
				return null;
			}
			ulong totalBytes = list[0].TotalBytes;
			foreach (ManualMemorySnapshot item in list)
			{
				if (item.TotalBytes != totalBytes)
				{
					return null;
				}
			}
			list.Sort((ManualMemorySnapshot a, ManualMemorySnapshot b) => a.AvailableBytes.CompareTo(b.AvailableBytes));
			return list[list.Count / 2];
		}

		private bool Pause(int milliseconds, Func<bool> mayContinue)
		{
			for (int i = 0; i < milliseconds; i += 100)
			{
				if (!MayContinue(mayContinue))
				{
					return false;
				}
				wait(Math.Min(100, milliseconds - i));
			}
			return MayContinue(mayContinue);
		}

		private static bool MayContinue(Func<bool> predicate)
		{
			try
			{
				return predicate == null || predicate();
			}
			catch
			{
				return false;
			}
		}

		internal static ManualMemoryStepState Classify(int status)
		{
			if (status >= 0)
			{
				return ManualMemoryStepState.Succeeded;
			}
			switch (status)
			{
			case -1073741822:
			case -1073741821:
			case -1073741637:
				return ManualMemoryStepState.Unsupported;
			case -2147024891:
			case -2147023582:
			case -1073741790:
			case -1073741727:
				return ManualMemoryStepState.PermissionDenied;
			default:
				return ManualMemoryStepState.Failed;
			}
		}
	}
}
