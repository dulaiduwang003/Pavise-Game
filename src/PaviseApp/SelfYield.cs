using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class SelfYield
	{
		private sealed class EmptyHelperScope : IDisposable
		{
			public void Dispose()
			{
			}
		}

		private sealed class HelperScope : IDisposable
		{
			internal IYieldChild Child;

			internal uint[] Original;

			internal uint[] Desired;

			internal bool EndRequested;

			internal bool Closed;

			internal int RestoreFailures;

			internal long RestoreAfter;
            internal bool Exited;

			internal bool Restore()
			{
				if (Closed)
				{
					return true;
				}
                if (Exited) { Child.Dispose(); Closed = true; return true; }
                if (RestoreFailures >= 3) { RecoveryAttemptTrace.Exhausted(ObserveExit); return false; }
                if (DateTime.UtcNow.Ticks < RestoreAfter) { RecoveryAttemptTrace.DeferredUntil(RestoreAfter); return false; }
				uint[] ids;
				int readResult = Child.Read(out ids);
				if (readResult < 0)
				{
					return Failed();
				}
				if (readResult == 0 && NativeYieldChild.Same(ids, Desired) && !Child.Write(Desired, Original))
				{
					return Failed();
				}
				Child.Dispose();
				Closed = true;
				return true;
			}

			private bool Failed()
			{
				RestoreFailures++;
				RestoreAfter = DateTime.UtcNow.AddSeconds(30).Ticks;
                if (RestoreFailures >= 3) RecoveryAttemptTrace.Exhausted(ObserveExit);
                else RecoveryAttemptTrace.DeferredUntil(RestoreAfter);
				Logger.Warn("SelfYield helper restore failed attempt=" + RestoreFailures + "/3; ownership retained");
				return false;
			}
            private bool ObserveExit()
            {
                lock (sync)
                {
                    if (Closed) return true;
                    uint[] ignored;
                    if (!Exited && Child.Read(out ignored) > 0) Exited = true;
                    return Exited;
                }
            }

			public void Dispose()
			{
				lock (sync)
				{
					EndRequested = true;
					if (Restore())
					{
						helpers.Remove(this);
					}
				}
			}
		}

		private static readonly object sync = new object();

		private static bool engaged;

		private static long session;
		private static int applyFailures;
		private static int restoreFailures;
		private static long applyAfter;
		private static long restoreAfter;

		internal static void BeginSession(long value)
		{
			lock (sync)
			{
				if (value <= session) return;
				session = value;
				applyFailures = restoreFailures = 0;
				applyAfter = restoreAfter = 0;
				foreach (HelperScope helper in helpers) { helper.RestoreFailures = 0; helper.RestoreAfter = 0; }
			}
		}

		private static uint origPriority;

		private static int origIo = -1;

		private static uint[] helperCpuSets;

		private static int helperRendererPid;

		private static long helperRendererBirth;

		private static readonly List<HelperScope> helpers = new List<HelperScope>();

		private static readonly IDisposable emptyHelper = new EmptyHelperScope();

		private static IntPtr Self
		{
			get
			{
				return (IntPtr)(-1);
			}
		}

		public static bool Engaged
		{
			get
			{
				lock (sync)
				{
					return engaged;
				}
			}
		}

        internal static bool HasResidue
        {
            get
            {
                lock (sync)
                {
                    if (engaged) return true;
                    foreach (HelperScope helper in helpers) if (!helper.Closed) return true;
                    return false;
                }
            }
        }

		public static void Engage()
		{
			lock (sync)
			{
				if (engaged)
				{
					return;
				}
				if (applyFailures >= 3 || DateTime.UtcNow.Ticks < applyAfter) return;
				IntPtr self = Self;
				uint priorityClass = Native.GetPriorityClass(self);
				if (priorityClass != 0)
				{
					origPriority = priorityClass;
					origIo = Native.QueryIoPriority(self);
					helperCpuSets = null;
					bool matchesSetPriorityClass = priorityClass == 16384 || Native.SetPriorityClass(self, 16384u);
					bool matchesTrySetIoPriority = origIo >= 0 && origIo != 1 && Native.TrySetIoPriority(self, 1);
					engaged = matchesSetPriorityClass || matchesTrySetIoPriority;
					if (engaged)
					{
						applyFailures = 0;
						Logger.Log(Lang.T("log.selfyield.1"));
					}
				}
				if (!engaged)
				{
					applyFailures++;
					applyAfter = DateTime.UtcNow.AddSeconds(30).Ticks;
					Logger.Warn("SelfYield apply failed attempt=" + applyFailures + "/3");
				}
			}
		}

		public static void Release()
		{
			lock (sync)
			{
				helperCpuSets = null;
				helperRendererPid = 0;
				helperRendererBirth = 0L;
				ReleaseHelpersLocked();
				if (engaged)
				{
                    if (restoreFailures >= 3) { RecoveryAttemptTrace.Exhausted(); return; }
                    if (DateTime.UtcNow.Ticks < restoreAfter) { RecoveryAttemptTrace.DeferredUntil(restoreAfter); return; }
					bool restored = true;
					IntPtr self = Self;
					if (origPriority != 0)
					{
						uint current = Native.GetPriorityClass(self);
						restored = current != 0 && (current != 16384 || current == origPriority || (Native.SetPriorityClass(self, origPriority) && Native.GetPriorityClass(self) == origPriority));
					}
					if (origIo >= 0)
					{
						int current = Native.QueryIoPriority(self);
						restored &= current >= 0 && (current != 1 || current == origIo || (Native.TrySetIoPriority(self, origIo) && Native.QueryIoPriority(self) == origIo));
					}
					if (!restored)
					{
						restoreFailures++;
						restoreAfter = DateTime.UtcNow.AddSeconds(30).Ticks;
                        RecoveryAttemptTrace.FailureSchedule(restoreFailures, 3, restoreAfter);
						Logger.Warn("SelfYield restore failed attempt=" + restoreFailures + "/3; ownership retained");
						return;
					}
					engaged = false;
					restoreFailures = 0;
					restoreAfter = 0;
					Logger.Log(Lang.T("log.selfyield.2"));
				}
			}
		}

		internal static bool HelperMasksSeparate(ulong game, ulong background, ulong all)
		{
			if (game != 0 && background != 0 && all != 0 && (game & ~all) == 0 && (background & ~all) == 0)
			{
				return (game & background) == 0;
			}
			return false;
		}

		private static bool HelperCoresSeparateLocked()
		{
			ulong background;
			return HelperCoresSeparateLocked(out background);
		}

		private static bool HelperCoresSeparateLocked(out ulong background)
		{
			background = 0uL;
			if (helperRendererPid <= 0 || helperRendererBirth <= 0 || helperCpuSets == null || CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
			{
				return false;
			}
			HashSet<uint> hashSet = new HashSet<uint>();
			ulong[] physicalCoreMasksResult = CpuTopology.PhysicalCoreMasks();
			foreach (ulong numericValueEntry in physicalCoreMasksResult)
			{
				uint[] cpuSetIdsForResult = CpuTopology.CpuSetIdsFor(numericValueEntry);
				if (cpuSetIdsForResult == null)
				{
					return false;
				}
				uint[] numericValueItems = cpuSetIdsForResult;
				foreach (uint numericValueEntry2 in numericValueItems)
				{
					uint[] helperCpuSetsSnapshot = helperCpuSets;
					foreach (uint numericValueEntry3 in helperCpuSetsSnapshot)
					{
						if (numericValueEntry2 == numericValueEntry3)
						{
							background |= numericValueEntry;
							hashSet.Add(numericValueEntry2);
						}
					}
				}
			}
			if (hashSet.Count != helperCpuSets.Length)
			{
				return false;
			}
			IntPtr intPtr = Native.OpenProcess(1052672, false, helperRendererPid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				long creation;
				long cpu;
				ulong io;
				ulong mask;
				return Native.WaitForSingleObject(intPtr, 0u) == 258 && Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation == helperRendererBirth && Native.TryQueryAffinity(intPtr, out mask) && HelperMasksSeparate(mask, background, CpuTopology.AllMask);
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		internal static void TrackHelperRenderer(int pid, long birth)
		{
			if (!OvernightPerformancePolicy.Enabled)
			{
				return;
			}
			lock (sync)
			{
				bool anyConditionMet = pid != helperRendererPid || birth != helperRendererBirth;
				helperRendererPid = pid;
				helperRendererBirth = birth;
				if (anyConditionMet || (helpers.Count > 0 && !HelperCoresSeparateLocked()))
				{
					ReleaseHelpersLocked();
				}
			}
		}

		private static void DrainFinishedHelpersLocked()
		{
			for (int helpersIndex = helpers.Count - 1; helpersIndex >= 0; helpersIndex--)
			{
				if (helpers[helpersIndex].EndRequested && helpers[helpersIndex].Restore())
				{
					helpers.RemoveAt(helpersIndex);
				}
			}
		}

		private static void ReleaseHelpersLocked()
		{
			foreach (HelperScope helper in helpers)
			{
				helper.EndRequested = true;
			}
			DrainFinishedHelpersLocked();
		}

		private static IDisposable BeginHelperLocked(IYieldChild child, uint[] desired)
		{
			DrainFinishedHelpersLocked();
			if (child == null)
			{
				return emptyHelper;
			}
			uint[] ids;
			if (!child.Owned || desired == null || desired.Length == 0 || !NativeYieldChild.Same(desired, desired) || helpers.Count >= 32 || child.Read(out ids) != 0 || ids == null || ids.Length != 0)
			{
				child.Dispose();
				return emptyHelper;
			}
			HelperScope helperScope = new HelperScope();
			helperScope.Child = child;
			helperScope.Original = ids;
			helperScope.Desired = (uint[])desired.Clone();
			HelperScope helperScope2 = helperScope;
			helpers.Add(helperScope2);
			if (!child.Write(ids, helperScope2.Desired))
			{
				helperScope2.EndRequested = true;
				if (helperScope2.Restore())
				{
					helpers.Remove(helperScope2);
				}
			}
			return helperScope2;
		}

		internal static IDisposable OwnHelperScope(IntPtr launchedChild)
		{
			if (!OvernightPerformancePolicy.Enabled)
			{
				return emptyHelper;
			}
			lock (sync)
			{
				ulong background;
				ulong mask;
				if (!engaged || helperCpuSets == null || !Native.CpuSetsMatch(Self, helperCpuSets) || !HelperCoresSeparateLocked(out background) || !Native.TryQueryAffinity(launchedChild, out mask) || (mask & background) == 0)
				{
					return emptyHelper;
				}
				return BeginHelperLocked(NativeYieldChild.Open(launchedChild), helperCpuSets);
			}
		}
	}
}
