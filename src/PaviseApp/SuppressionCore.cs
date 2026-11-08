using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal sealed class SuppressionCore
	{
		internal interface IAutomaticLayoutPlatform
		{
			IntPtr Open(int pid, long creation, string name);

			uint[] ReadSets(IntPtr handle);

			bool ReadAffinity(IntPtr handle, out ulong mask);

			bool WriteSets(IntPtr handle, uint[] sets);

			void Close(IntPtr handle);
		}

		private sealed class AutomaticLayoutPlatform : IAutomaticLayoutPlatform
		{
			public IntPtr Open(int pid, long creation, string name)
			{
				IntPtr intPtr = Native.OpenProcess(12800, false, pid);
				if (intPtr == IntPtr.Zero)
				{
					return intPtr;
				}
				long creation2;
				long cpu;
				ulong io;
				if (Native.QueryProcessSample(intPtr, out creation2, out cpu, out io) && creation2 == creation && SameName(Native.ImageName(intPtr), name) && Native.StillActive(intPtr))
				{
					return intPtr;
				}
				Native.CloseHandle(intPtr);
				return IntPtr.Zero;
			}

			public uint[] ReadSets(IntPtr h)
			{
				return Native.QueryCpuSets(h);
			}

			public bool ReadAffinity(IntPtr h, out ulong mask)
			{
				return Native.TryQueryAffinity(h, out mask);
			}

			public bool WriteSets(IntPtr h, uint[] ids)
			{
				return Native.RestoreCpuSetsVerified(h, ids);
			}

			public void Close(IntPtr h)
			{
				Native.CloseHandle(h);
			}
		}

		private sealed class FailureBudget
		{
			internal int Failures;
			internal long NotBefore;
		}

		private static string FailurePhaseIdentity(Entry entry, string action)
		{
			return action + "/" + PerformanceTuning.RecoveryCycleSerial + "/" + entry.Level + "/"
				+ entry.AntiCheatLevel + "/" + entry.BackgroundLevel + "/" + GpuDemoteEnabled
				+ "/" + entry.RecoveryExitObserved;
		}

		private static FailureBudget FailureBudgetFor(Entry entry, string action)
		{
			string identity = FailurePhaseIdentity(entry, action);
			FailureBudget budget;
			if (!entry.FailureBudgets.TryGetValue(identity, out budget)) entry.FailureBudgets[identity] = budget = new FailureBudget();
			string prior;
			if (!entry.SelectedFailureBudgets.TryGetValue(action, out prior) || prior != identity)
			{
				entry.SelectedFailureBudgets[action] = identity;
				if (action == "restore") { entry.ProtectedRetries = budget.Failures; entry.NextRetryTicks = budget.NotBefore; }
				if (action == "layout") { entry.AutomaticLayoutFailures = budget.Failures; entry.AutomaticLayoutRetryAt = budget.NotBefore; }
				if (action == "reconcile") { entry.ReconcileFailures = budget.Failures; entry.NextReconcileTicks = budget.NotBefore; }
			}
			return budget;
		}

		private static void ReportFailureBudget(Entry entry, string action, FailureBudget budget)
		{
			if (budget.Failures < 8) { RecoveryAttemptTrace.DeferredUntil(budget.NotBefore); return; }
			string phase = FailurePhaseIdentity(entry, action);
			RecoveryAttemptTrace.Exhausted(() => budget.Failures < 8 || FailurePhaseIdentity(entry, action) != phase);
		}

		private static bool RestoreBudgetBlocked(Entry entry, string action, bool respectDeadline)
		{
			FailureBudget budget = FailureBudgetFor(entry, action);
			if (budget.Failures < 8 && (!respectDeadline || DateTime.UtcNow.Ticks >= budget.NotBefore)) return false;
			ReportFailureBudget(entry, action, budget);
			return true;
		}

		private static void RecordFailureBudget(Entry entry, string action, bool completed, long notBefore)
		{
			FailureBudget budget = FailureBudgetFor(entry, action);
			if (completed) { budget.Failures = 0; budget.NotBefore = 0; return; }
			budget.Failures = Math.Min(8, budget.Failures + 1);
			budget.NotBefore = budget.Failures >= 8 ? DateTime.MaxValue.Ticks : notBefore;
			ReportFailureBudget(entry, action, budget);
			SuccessLogBatch.FlushPending();
			Logger.Log("Suppression " + action + ": target=" + entry.Name + "/" + entry.Creation
				+ " phase=" + PerformanceTuning.RecoveryCycleSerial + " attempt=" + budget.Failures + "/8; ownership retained");
		}

		internal void ObserveTargetExit(int pid, long birth)
		{
			lock (sync)
			{
				Entry entry;
				if (!map.TryGetValue(pid, out entry) || entry.Creation != birth || entry.RecoveryExitObserved) return;
				entry.RecoveryExitObserved = true;
				FailureBudgetFor(entry, "restore");
				FailureBudgetFor(entry, "layout");
			}
		}

		private sealed class Entry
		{
			internal readonly Dictionary<string, FailureBudget> FailureBudgets = new Dictionary<string, FailureBudget>();
			internal readonly Dictionary<string, string> SelectedFailureBudgets = new Dictionary<string, string>();
			internal bool RecoveryExitObserved;
			internal bool AffinityPrepareInFlight;
			public string Name;

			public string FailureTarget;

			public string Group;

			internal SuppressionCore CountOwner;

			internal void SetGroup(string value)
			{
				bool changed = (Reasons & SuppressReason.AntiCheat) != 0 && !SameName(Group ?? "", value ?? "");
				Group = value;
				if (changed && CountOwner != null) CountOwner.InvalidateGroupCounts();
			}

			internal void SetOriginalPriority(uint value)
			{
				bool changed = (Reasons & SuppressReason.AntiCheat) != 0 && Applied && (OrigPri == uint.MaxValue) != (value == uint.MaxValue);
				OrigPri = value;
				if (changed && CountOwner != null) CountOwner.InvalidateGroupCounts();
			}

			internal void SetApplied(bool value)
			{
				bool changed = (Reasons & SuppressReason.AntiCheat) != 0 && OrigPri != uint.MaxValue && Applied != value;
				Applied = value;
				if (changed && CountOwner != null) CountOwner.InvalidateGroupCounts();
			}

			internal void SetReasons(SuppressReason value)
			{
				bool changed = (Reasons & SuppressReason.AntiCheat) != (value & SuppressReason.AntiCheat);
				Reasons = value;
				if (changed && CountOwner != null) CountOwner.InvalidateGroupCounts();
			}

			public uint OrigPri;

			public ulong OrigAff;

			public int OrigIo = -1;

			public int OrigPg = -1;

			public uint[] OrigCpuSets;

			public uint[] AutomaticLayoutSets;

			public bool AutomaticLayoutYielded;

			public bool AutomaticLayoutRestoring;

			public int AutomaticLayoutFailures;

			public long AutomaticLayoutRetryAt;

			public ulong SqueezeAff;

			public bool BackgroundPlacementOwned;

			// Background Job receipts retain exclusive affinity recovery ownership, including after neutralization.
			public bool BackgroundJobPlacement, BackgroundJobActive;
			public int BackgroundPid;
			public GameAffinityJobLease BackgroundJobs;
			public string BackgroundAffinityDetail;

			// Retain anti-cheat placement ownership after Reasons clears and in the journal.
			public bool AntiCheatPlacementExternal;

			public bool LegacyPlacementDebt;

			public bool BackgroundPlacementVerified;

			public bool BackgroundRestorePending;

			// Retained only until released Background ownership is restored or reapplied.
			public bool BackgroundReleaseDebt;

			public ulong BackgroundApplied;

			public ulong BackgroundPrevious;

			public bool SqueezeRefused;

			public int SqueezeFailures;

			public long SqueezeRetryAt;

			public bool GaveUp;

			public bool GaveUpRestored;

			public int OrigGpu = -1;

			public int OrigQoSControl = -1;

			public int OrigQoSState = -1;

			public int OrigBoost = -1;

			public long Creation;

			public SuppressionLevel Level;

			public SuppressionLevel AntiCheatLevel;

			public SuppressionLevel BackgroundLevel;

			public bool Applied;

			public SuppressReason Reasons;

			public int ProtectedRetries;

			public long NextRetryTicks;

			public long NextReconcileTicks;

			public int ReconcileFailures;

			public int FastReconcileRemaining;

			public long AntiCheatCorrectionWindow;

			public int AntiCheatCorrections;

			public bool Journaled;

			public bool RestoreInFlight;
		}

		internal enum RestoreResult
		{
			Restored,
			Gone,
			Protected
		}

		public sealed class BatchResult
		{
			private readonly Dictionary<int, bool> applied;

			private readonly Dictionary<int, string> errors;

			internal BatchResult(Dictionary<int, bool> values)
				: this(values, null)
			{
			}

			internal BatchResult(Dictionary<int, bool> values, Dictionary<int, string> errorValues)
			{
				applied = values ?? new Dictionary<int, bool>();
				errors = errorValues ?? new Dictionary<int, string>();
			}

			public bool WasApplied(int pid)
			{
				bool value;
				if (applied.TryGetValue(pid, out value))
				{
					return value;
				}
				return false;
			}

			public string FailureOf(int pid)
			{
				string value;
				if (!errors.TryGetValue(pid, out value))
				{
					return null;
				}
				return value;
			}
		}

		private enum JournalIdentity
		{
			Confirmed,
			Unknown,
			Mismatch
		}

		internal const string SelfProtectedDetail = "self-protected";

		public const string StateFileName = "Pavise.suppression.state";

		internal const string FailureAction = "BackgroundSuppression";

		private const int ProtectedBackoffBaseSeconds = 8;

		private const int ProtectedBackoffCapSeconds = 300;

		private const int ProtectedBackoffMax = 8;

		private const int ReconcileFastSeconds = 4;

		private const int ReconcileStableBaseSeconds = 20;

		private const int ReconcileStableJitterSeconds = 11;

		private const int ReconcileFailureCapSeconds = 60;

		private static readonly IAutomaticLayoutPlatform automaticLayoutPlatform = new AutomaticLayoutPlatform();

		public static volatile bool GpuDemoteEnabled;

		private readonly object sync = new object();

		private readonly object batchGate = new object();

		private readonly Dictionary<int, Entry> map = new Dictionary<int, Entry>();

		private readonly GameAffinityJobLease backgroundAffinityJobs = new GameAffinityJobLease();
		private bool backgroundJobJournalPending;
		private bool backgroundJobsStopCompleted;
		private readonly Dictionary<string, FailureBudget> backgroundJobJournalFailures = new Dictionary<string, FailureBudget>();

		private ulong throttleMask;

		private readonly ulong allMask;

		private readonly string journalPath;

		private bool marked;

		private int batchDepth;

		private int journalDefer;

		private bool batchJournalDirty;

		private readonly Dictionary<int, string> batchApply = new Dictionary<int, string>();

		private readonly Dictionary<int, bool> batchApplyResults = new Dictionary<int, bool>();

		private readonly Dictionary<int, string> batchApplyErrors = new Dictionary<int, string>();

		private long applyOperations;

		[ThreadStatic]
		private static string lastApplyError;

		private int lastThrottledCount;

		private readonly Dictionary<string, int[]> lastGroupCounts = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
		private long groupCountsVersion;
		private long publishedGroupCountsVersion = -1;

		private void InvalidateGroupCounts()
		{
			Interlocked.Increment(ref groupCountsVersion);
		}

		private static bool SameCountContribution(Entry first, Entry second)
		{
			bool a = first != null && (first.Reasons & SuppressReason.AntiCheat) != 0;
			bool b = second != null && (second.Reasons & SuppressReason.AntiCheat) != 0;
			return a == b && (!a || (SameName(first.Group ?? "", second.Group ?? "")
				&& (first.OrigPri != uint.MaxValue && first.Applied) == (second.OrigPri != uint.MaxValue && second.Applied)));
		}

		private void SetEntryLocked(int pid, Entry entry)
		{
			Entry previous;
			map.TryGetValue(pid, out previous);
			bool changed = !SameCountContribution(previous, entry);
			if (previous != null) previous.CountOwner = null;
			map[pid] = entry;
			entry.CountOwner = this;
			if (changed) InvalidateGroupCounts();
		}

		private void RemoveEntryLocked(int pid)
		{
			Entry previous;
			if (!map.TryGetValue(pid, out previous)) return;
			map.Remove(pid);
			previous.CountOwner = null;
			if (!SameCountContribution(previous, null)) InvalidateGroupCounts();
		}

		private static readonly Dictionary<string, long> protectedLogTimes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		internal bool EnforceBackgroundAffinity;

		internal bool BackgroundReleasePending
		{
			get
			{
				lock (sync)
				{
					if (backgroundJobJournalPending || backgroundAffinityJobs.HasPending) return true;
					foreach (Entry entry in map.Values)
						if ((entry.Reasons & SuppressReason.Background) != 0 || entry.BackgroundReleaseDebt || entry.BackgroundRestorePending
							|| (entry.Reasons == SuppressReason.None && entry.BackgroundPlacementOwned)) return true;
					return false;
				}
			}
		}

		internal bool BackgroundAffinityPending
		{
			get
			{
				lock (sync)
				{
					if (backgroundJobJournalPending || backgroundAffinityJobs.HasPending) return true;
					foreach (Entry entry in map.Values) if (entry.BackgroundRestorePending) return true;
					return false;
				}
			}
		}

		internal string BackgroundAffinityDetail()
		{
			lock (sync)
			{
				int jobs = 0, fallback = 0;
				HashSet<string> reasons = new HashSet<string>(StringComparer.Ordinal);
				foreach (Entry entry in map.Values)
				{
					if (entry.SqueezeAff == 0 || !SqueezeOwnedBy(entry, SuppressReason.Background)) continue;
					if (entry.BackgroundJobActive && VerifyBackgroundJob(entry)) jobs++;
					else if (!entry.BackgroundJobPlacement && entry.BackgroundPlacementVerified) fallback++;
					if (!string.IsNullOrEmpty(entry.BackgroundAffinityDetail)) reasons.Add(entry.BackgroundAffinityDetail);
				}
				return jobs == 0 && fallback == 0 && !BackgroundAffinityPending ? "" : "Job=" + jobs + "; process-fallback=" + fallback + (reasons.Count == 0 ? "" : "; " + string.Join("; ", new List<string>(reasons).ToArray()));
			}
		}

		internal bool StopBackgroundAffinityJobs()
		{
			lock (sync)
			{
				if (backgroundJobsStopCompleted) return FlushBackgroundJobJournalLocked(false);
				string reason;
				bool ok = backgroundAffinityJobs.Stop(out reason);
				backgroundJobsStopCompleted = ok;
				bool changed = false;
				foreach (Entry entry in map.Values)
				{
					if (!entry.BackgroundJobPlacement) continue;
					changed |= entry.BackgroundPlacementVerified || (ok ? entry.BackgroundJobActive || entry.BackgroundRestorePending || entry.SqueezeAff != 0 : !entry.BackgroundRestorePending);
					entry.BackgroundPlacementVerified = false;
					if (ok) { entry.BackgroundJobActive = false; entry.BackgroundRestorePending = false; entry.SqueezeAff = 0; }
					else entry.BackgroundRestorePending = true;
				}
				return FlushBackgroundJobJournalLocked(changed) && ok;
			}
		}

		private bool FlushBackgroundJobJournalLocked(bool changed)
		{
			backgroundJobJournalPending |= changed;
			if (!backgroundJobJournalPending) return true;
			string phase = PerformanceTuning.RecoveryCycleSerial + "/" + EnforceBackgroundAffinity;
			FailureBudget budget;
			if (!backgroundJobJournalFailures.TryGetValue(phase, out budget))
				backgroundJobJournalFailures[phase] = budget = new FailureBudget();
			long now = DateTime.UtcNow.Ticks;
			if (budget.Failures >= 8 || now < budget.NotBefore)
			{
				ReportBackgroundJournalBudget(phase, budget);
				return false;
			}
			if (SaveJournalLocked(true)) return true;
			budget.Failures++;
			budget.NotBefore = budget.Failures >= 8 ? DateTime.MaxValue.Ticks : DateTime.UtcNow.AddSeconds(30).Ticks;
			ReportBackgroundJournalBudget(phase, budget);
			Logger.Log("Suppression background journal: phase=" + phase + " attempt=" + budget.Failures + "/8; journal debt retained");
			return false;
		}

		private void ReportBackgroundJournalBudget(string phase, FailureBudget budget)
		{
			if (budget.Failures < 8) RecoveryAttemptTrace.DeferredUntil(budget.NotBefore);
			else RecoveryAttemptTrace.Exhausted(() => !backgroundJobJournalPending
				|| PerformanceTuning.RecoveryCycleSerial + "/" + EnforceBackgroundAffinity != phase);
		}

		private static IAutomaticLayoutPlatform LayoutPlatform
		{
			get
			{
				return automaticLayoutPlatform;
			}
		}

		internal bool AutomaticLayoutPending
		{
			get
			{
				lock (sync)
				{
					foreach (Entry value in map.Values)
					{
						if (value.AutomaticLayoutSets != null)
						{
							return true;
						}
					}
					return false;
				}
			}
		}

		public string LastApplyError
		{
			get
			{
				return lastApplyError;
			}
			private set
			{
				lastApplyError = value;
			}
		}

		public ulong ThrottleMask
		{
			get
			{
				return throttleMask;
			}
		}

		internal long ApplyOperations
		{
			get
			{
				return Interlocked.Read(ref applyOperations);
			}
		}

		public AcquireResult Acquire(int pid, string name, SuppressReason reason, string group)
		{
			return Acquire(pid, name, reason, group, SuppressionLevel.Isolated, 0L);
		}

		public AcquireResult Acquire(int pid, string name, SuppressReason reason, string group, SuppressionLevel level, long expectedCreation = 0L)
		{
			if (level == SuppressionLevel.None)
			{
				level = SuppressionLevel.Eco;
			}
			IntPtr intPtr = Native.OpenProcess(12800, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				lock (sync)
				{
					Entry value;
					if (map.TryGetValue(pid, out value) && SameName(value.Name, name))
					{
						value.SetReasons(value.Reasons | reason);
						SetReasonLevel(value, reason, level);
						if (group != null && value.Group == null)
						{
							value.SetGroup(group);
						}
						return AcquireResult.AlreadyProtected;
					}
					Entry entry = new Entry();
					entry.Name = name;
					entry.SetGroup(group);
					entry.SetOriginalPriority(uint.MaxValue);
					entry.SetReasons(reason);
					Entry entry2 = entry;
					SetReasonLevel(entry2, reason, level);
					SetEntryLocked(pid, entry2);
					return AcquireResult.NewlyProtected;
				}
			}
			try
			{
				string text = Native.ImageName(intPtr);
				if (text == null || !SameName(text, name))
				{
					return AcquireResult.AlreadyProtected;
				}
				if (reason == SuppressReason.Background && (WeGameRuntimeProtection.IsProtectedRuntimeName(text) || PeripheralCatalog.IsInputAudioLike(text)))
				{
					return AcquireResult.AlreadyProtected;
				}
				long creation = 0L;
				long cpu;
				ulong io;
				bool flag = Native.QueryProcessSample(intPtr, out creation, out cpu, out io);
				if (flag && creation > 0 && (expectedCreation <= 0 || expectedCreation == creation))
				{
					lock (sync)
					{
						Entry value2;
						bool flag2 = map.TryGetValue(pid, out value2);
						if (flag2 && !SameName(value2.Name, name))
						{
							RemoveEntryLocked(pid);
							flag2 = false;
							value2 = null;
						}
						if (flag2 && value2.OrigPri != uint.MaxValue && value2.Creation <= 0)
						{
							RemoveEntryLocked(pid);
							batchApply.Remove(pid);
							flag2 = false;
							value2 = null;
						}
						if (flag2 && value2.Creation > 0)
						{
							if (!flag)
							{
								return AcquireResult.AlreadyProtected;
							}
							if (value2.Creation != creation)
							{
								RemoveEntryLocked(pid);
								flag2 = false;
								value2 = null;
							}
						}
						if (flag2 && value2.OrigPri != uint.MaxValue && value2.GaveUp)
						{
							value2.SetReasons(value2.Reasons | reason);
							SetReasonLevel(value2, reason, level);
							if (group != null && value2.Group == null)
							{
								value2.SetGroup(group);
							}
							if (!value2.GaveUpRestored)
								value2.GaveUpRestored = RestoreEntryValues(intPtr, value2);
							if (!value2.GaveUpRestored) return AcquireResult.AlreadyProtected;
							ActionFailureStore.CancelApply("BackgroundSuppression", FailureTargetOf(intPtr, value2));
							if (!BeginSuppressionCycle(intPtr, value2, pid)) return AcquireResult.AlreadyProtected;
							value2.GaveUp = false;
							value2.NextReconcileTicks = 0;
						}
						if (flag2 && value2.OrigPri != uint.MaxValue)
						{
							SuppressionLevel level2 = value2.Level;
							SuppressReason reasons = value2.Reasons;
							string group2 = value2.Group;
							value2.SetReasons(value2.Reasons | reason);
							if (group != null && value2.Group == null)
							{
								value2.SetGroup(group);
							}
							SetReasonLevel(value2, reason, level);
							bool flag3 = reasons != value2.Reasons || level2 != value2.Level || !SameName(group2, value2.Group);
							long ticks = DateTime.UtcNow.Ticks;
							bool flag4 = level2 != value2.Level;
							bool flag5 = !value2.Journaled || flag4 || reasons != value2.Reasons || (!value2.Applied && ticks >= value2.NextReconcileTicks);
							if ((flag3 || !value2.Journaled) && !PersistJournalLocked())
							{
								return AcquireResult.ApplyFailed;
							}
							if (flag5)
							{
								if (QueueApplyLocked(pid, name))
								{
									return AcquireResult.AlreadyThrottled;
								}
								value2.SetApplied(ApplyEntryLocked(intPtr, value2, pid));
								ScheduleAfterApply(value2, value2.Applied, pid);
								if (!value2.Applied && TryNeutralizeUnwritableLocked(intPtr, pid, value2))
								{
									return AcquireResult.AlreadyProtected;
								}
								if (!value2.Applied && GiveUpAntiCheatLocked(intPtr, pid, value2))
								{
									return AcquireResult.AlreadyProtected;
								}
								return (!value2.Applied) ? AcquireResult.ApplyFailed : AcquireResult.AlreadyThrottled;
							}
							if (!value2.Applied)
							{
								RecordBatchApplyResultLocked(pid, false, "apply-pending-backoff");
								return AcquireResult.AlreadyThrottled;
							}
							if (ticks < value2.NextReconcileTicks)
							{
								RecordBatchApplyResultLocked(pid, true, null);
								return AcquireResult.AlreadyThrottled;
							}
							if (VerifyBackgroundJob(value2) && ThrottleMatches(intPtr, value2.Level, value2.OrigPri, value2.OrigAff, value2.OrigCpuSets, DesiredGpu(value2), AntiCheatThrottled(value2), DesiredAffinityOf(value2), PlacementIsProtected(value2), value2.OrigBoost))
							{
								ScheduleAfterMatch(value2, pid);
								RecordBatchApplyResultLocked(pid, true, null);
								return AcquireResult.AlreadyThrottled;
							}
							if (!AllowAntiCheatCorrection(intPtr, pid, value2))
							{
								RecordBatchApplyResultLocked(pid, false, "anti-cheat-policy-conflict");
								return AcquireResult.AlreadyProtected;
							}
							if (QueueApplyLocked(pid, name))
							{
								return AcquireResult.AlreadyThrottled;
							}
							value2.SetApplied(ApplyEntryLocked(intPtr, value2, pid));
							ScheduleAfterApply(value2, value2.Applied, pid);
							if (!value2.Applied && TryNeutralizeUnwritableLocked(intPtr, pid, value2))
							{
								return AcquireResult.AlreadyProtected;
							}
							if (!value2.Applied && GiveUpAntiCheatLocked(intPtr, pid, value2))
							{
								return AcquireResult.AlreadyProtected;
							}
							return AcquireResult.AlreadyThrottled;
						}
						uint priorityClass = Native.GetPriorityClass(intPtr);
						if (priorityClass == 0)
						{
							return AcquireResult.ApplyFailed;
						}
						ulong num = Native.QueryAffinity(intPtr);
						uint[] array = Native.QueryCpuSets(intPtr);
						if (array == null)
						{
							return AcquireResult.ApplyFailed;
						}
						int num2 = Native.QueryIoPriority(intPtr);
						int num3 = Native.QueryPagePriority(intPtr);
						if ((!CpuTopology.MultiGroup && num == 0) || num2 < 0 || num3 < 0)
						{
							return AcquireResult.ApplyFailed;
						}
						uint[] array2 = CpuTopology.BackgroundYieldCpuSetIds();
						bool flag6 = SameCpuSets(array, CpuTopology.BackgroundCpuSetIds()) || SameCpuSets(array, CpuTopology.InactiveBackgroundCpuSetIds()) || (array2 != null && array2.Length > 0 && SameCpuSets(array, array2));
						ulong num4 = (CpuTopology.MultiGroup ? 0 : CpuTopology.BackgroundSqueezeMask());
						bool flag7 = !CpuTopology.MultiGroup && (num == throttleMask || (CpuTopology.InactiveThrottleMask != 0 && num == CpuTopology.InactiveThrottleMask) || (num4 != 0 && num == num4));
						bool flag8 = CrashGuard.UncleanThrottleAtLaunch && priorityClass == 64 && num2 == 0 && num3 == 1 && (flag6 || flag7);
						uint num5 = (flag8 ? 32u : priorityClass);
						if (flag8 && flag7)
						{
							num = 0uL;
						}
						if (flag8 && flag6)
						{
							array = new uint[0];
						}
						if (flag8 && num2 == 0)
						{
							num2 = -1;
						}
						if (flag8 && num3 == 1)
						{
							num3 = -1;
						}
						int controlMask;
						int stateMask;
						if (!Native.TryQueryPowerThrottling(intPtr, out controlMask, out stateMask))
						{
							controlMask = -1;
							stateMask = -1;
						}
						else if (flag8 && (controlMask == 1 || controlMask == 5) && stateMask == controlMask)
						{
							controlMask = -1;
							stateMask = -1;
						}
						int num6 = Native.QueryBoostDisabled(intPtr);
						if (flag8 && num6 == 1)
						{
							num6 = 0;
						}
						int cls;
						if (Native.D3DKMTGetProcessSchedulingPriorityClass(intPtr, out cls) != 0)
						{
							cls = -1;
						}
						else if (flag8 && cls == 0)
						{
							cls = 2;
						}
						long creation2 = creation;
						Entry e;
						if (flag2)
						{
							value2.SetOriginalPriority(num5);
							value2.OrigAff = num;
							value2.OrigIo = num2;
							value2.OrigPg = num3;
							value2.OrigCpuSets = array;
							value2.OrigGpu = cls;
							value2.OrigQoSControl = controlMask;
							value2.OrigQoSState = stateMask;
							value2.OrigBoost = num6;
							value2.SetReasons(value2.Reasons | reason);
							SetReasonLevel(value2, reason, level);
							value2.Creation = creation2;
							value2.SetApplied(false);
							value2.Journaled = false;
							if (group != null && value2.Group == null)
							{
								value2.SetGroup(group);
							}
							e = value2;
						}
						else
						{
							Entry entry3 = new Entry();
							entry3.Name = name;
							entry3.SetGroup(group);
							entry3.SetOriginalPriority(num5);
							entry3.OrigAff = num;
							entry3.OrigIo = num2;
							entry3.OrigPg = num3;
							entry3.OrigCpuSets = array;
							entry3.OrigGpu = cls;
							entry3.OrigQoSControl = controlMask;
							entry3.OrigQoSState = stateMask;
							entry3.OrigBoost = num6;
							entry3.SetReasons(reason);
							entry3.Creation = creation2;
							Entry entry4 = entry3;
							SetReasonLevel(entry4, reason, level);
							SetEntryLocked(pid, entry4);
							e = entry4;
						}
						if (!PersistJournalLocked())
						{
							return AcquireResult.ApplyFailed;
						}
						bool flag9 = QueueApplyLocked(pid, name);
						bool flag10 = flag9 || ApplyThrottle(intPtr, level, num5, num, array, DesiredGpu(e), num6, (reason & SuppressReason.AntiCheat) != 0, DesiredAffinityOf(e), PlacementIsProtected(e));
						Entry value3;
						if (map.TryGetValue(pid, out value3) && !flag9)
						{
							value3.SetApplied(flag10);
							ScheduleAfterApply(value3, flag10, pid);
							if (!flag10 && TryNeutralizeUnwritableLocked(intPtr, pid, value3))
							{
								return AcquireResult.NewlyProtected;
							}
							if (!flag10 && GiveUpAntiCheatLocked(intPtr, pid, value3))
							{
								return AcquireResult.AlreadyProtected;
							}
						}
						if (!marked)
						{
							marked = true;
							CrashGuard.MarkThrottle(throttleMask);
						}
						return flag10 ? AcquireResult.NewlyThrottled : AcquireResult.ApplyFailed;
					}
				}
				return AcquireResult.ApplyFailed;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private int EntryIdentity(IntPtr handle, Entry entry)
		{
			int num = Native.QueryProcessIdentityState(handle, entry.Creation);
			if (num >= 0)
			{
				return num;
			}
			return 2;
		}

		private bool RestoreEntryValues(IntPtr handle, Entry entry)
		{
			if (RestoreBudgetBlocked(entry, "restore-values", false)) return false;
			bool restored = RestoreEntryValuesCore(handle, entry);
			RecordFailureBudget(entry, "restore-values", restored, 0);
			return restored;
		}

		private bool RestoreEntryValuesCore(IntPtr handle, Entry entry)
		{
			try
			{
				if (EntryIdentity(handle, entry) != 0)
				{
					return false;
				}
				return RestoreBackgroundPlacement(handle, entry) && RestoreValues(handle, entry.OrigPri, entry.OrigAff, entry.OrigIo, entry.OrigPg, allMask, entry.OrigCpuSets, entry.OrigQoSControl, entry.OrigQoSState, entry.OrigGpu, entry.OrigBoost, RestorePlacement(entry));
			}
			catch (Exception ex)
			{
				LastApplyError = ex.Message;
				return false;
			}
		}

		private static bool EntrySnapshotMatches(IntPtr handle, Entry entry)
		{
			if (entry.BackgroundJobPlacement && (entry.BackgroundJobActive || entry.BackgroundRestorePending)) return false;
			return SnapshotMatchesCurrent(handle, entry.OrigPri, entry.OrigAff, entry.OrigIo, entry.OrigPg, entry.OrigCpuSets, entry.OrigQoSControl, entry.OrigQoSState, entry.OrigGpu, entry.OrigBoost, !entry.AntiCheatPlacementExternal && !entry.BackgroundJobPlacement);
		}

		private bool ApplyEntryValues(IntPtr handle, Entry entry)
		{
			if (!VerifyBackgroundJob(entry)) return false;
			DetectBackgroundPlacementChange(handle, entry);
			return ApplyThrottle(handle, entry.Level, entry.OrigPri, entry.OrigAff, entry.OrigCpuSets, DesiredGpu(entry), entry.OrigBoost, AntiCheatThrottled(entry), DesiredAffinityOf(entry), PlacementIsProtected(entry));
		}

		private static void StopEntry(Entry entry, bool restored)
		{
			entry.GaveUp = true;
			entry.GaveUpRestored = restored;
			entry.SetApplied(false);
			entry.NextReconcileTicks = long.MaxValue;
		}

		private bool ThrottleMatches(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity, uint[] originalCpuSets, int desiredGpu, bool antiCheat, ulong desiredAffinity, bool affinityOwned, int originalBoost)
		{
			affinityOwned |= antiCheat;
			uint num = DesiredPriority(level, originalPriority, antiCheat);
			if (Native.GetPriorityClass(h) != num)
			{
				return false;
			}
			int cls;
			if (desiredGpu >= 0 && Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0 && cls != desiredGpu)
			{
				return false;
			}
			int num2 = DesiredIoPriority(level, antiCheat);
			int num3 = DesiredPagePriority(level, antiCheat);
			if (Native.QueryIoPriority(h) != num2 || Native.QueryPagePriority(h) != num3)
			{
				return false;
			}
			int controlMask;
			int stateMask;
			if (Native.PowerThrottlingSupported && (!Native.TryQueryPowerThrottling(h, out controlMask, out stateMask) || !PowerStateMatches(controlMask, stateMask, Native.EcoQoSWantMask(DesiredTimerSeal(level, antiCheat)))))
			{
				return false;
			}
			bool flag = DisableDynamicBoost(level, antiCheat);
			uint[] expected = originalCpuSets ?? new uint[0];
			if (!affinityOwned && !Native.CpuSetsMatch(h, expected))
			{
				return false;
			}
			if (!CpuTopology.MultiGroup && !affinityOwned && Native.QueryAffinity(h) != desiredAffinity)
			{
				return false;
			}
			int num4 = DesiredBoostDisabled(level, antiCheat, originalBoost);
			if ((flag && num4 < 0) || (num4 >= 0 && Native.QueryBoostDisabled(h) != num4))
			{
				return false;
			}
			return true;
		}

		internal static bool SevereAntiCheat(SuppressionLevel level, bool antiCheat)
		{
			return antiCheat && level >= SuppressionLevel.Isolated;
		}

		// Use the session's Aggressive flag for background boost limits; anti-cheat policy is independent.
		internal static volatile bool BackgroundBoostLimit;

		internal static bool DisableDynamicBoost(SuppressionLevel level, bool antiCheat)
		{
			if (antiCheat) return level >= SuppressionLevel.Restrained;
			return BackgroundBoostLimit && level >= SuppressionLevel.Isolated;
		}

		internal static uint DesiredPriority(SuppressionLevel level, uint originalPriority, bool antiCheat)
		{
			if (antiCheat && level >= SuppressionLevel.Eco)
				return level >= SuppressionLevel.Isolated ? 64u : 16384u;
			uint result = ((originalPriority == 0 || originalPriority == uint.MaxValue) ? 32u : originalPriority);
			if (level >= SuppressionLevel.Restrained)
			{
				result = ((level >= SuppressionLevel.Isolated && (!antiCheat || SevereAntiCheat(level, antiCheat))) ? 64u : 16384u);
			}
			return result;
		}

		internal static int DesiredIoPriority(SuppressionLevel level, bool antiCheat = false)
		{
			if (antiCheat && level >= SuppressionLevel.Restrained) return 0;
			if (level < SuppressionLevel.Isolated)
			{
				return 1;
			}
			return 0;
		}

		internal static int DesiredPagePriority(SuppressionLevel level, bool antiCheat)
		{
			if (antiCheat) return level >= SuppressionLevel.Isolated ? 1 : (level >= SuppressionLevel.Restrained ? 2 : 3);
			if (level < SuppressionLevel.Isolated || (antiCheat && !SevereAntiCheat(level, antiCheat)))
			{
				return 3;
			}
			return 1;
		}

		internal static bool DesiredTimerSeal(SuppressionLevel level, bool antiCheat)
		{
			if (level >= SuppressionLevel.Isolated)
			{
				if (antiCheat)
				{
					return SevereAntiCheat(level, antiCheat);
				}
				return true;
			}
			return false;
		}

		internal static int DesiredBoostDisabled(SuppressionLevel level, bool antiCheat, int originalBoost)
		{
			if (originalBoost != 0 && originalBoost != 1)
			{
				return -1;
			}
			if (!DisableDynamicBoost(level, antiCheat))
			{
				return originalBoost;
			}
			return 1;
		}

		internal static string ApplyBoostPolicy(SuppressionLevel level, bool antiCheat, int originalBoost, Func<int> read, Func<bool, bool> write)
		{
			int num = DesiredBoostDisabled(level, antiCheat, originalBoost);
			if (num < 0)
			{
				if (!DisableDynamicBoost(level, antiCheat))
				{
					return "";
				}
				return "boost-snapshot";
			}
			if (read() != num && !write(num == 1))
			{
				return "boost-write";
			}
			if (read() != num)
			{
				return "boost-readback";
			}
			return "";
		}

		internal static bool PowerStateMatches(int control, int state, uint required)
		{
			if (control >= 0 && state >= 0 && ((uint)control & required) == required && ((uint)state & required) == required)
			{
				if (required == 1)
				{
					return (control & 4) == 0;
				}
				return true;
			}
			return false;
		}

		internal static int DesiredGpuClass(bool demoteEnabled, SuppressReason reasons, SuppressionLevel backgroundLevel, int origGpu)
		{
			if (origGpu < 0)
			{
				return -1;
			}
			if (!demoteEnabled || (reasons & SuppressReason.Background) == 0 || backgroundLevel < SuppressionLevel.Restrained)
			{
				return origGpu;
			}
			if (backgroundLevel < SuppressionLevel.Isolated)
			{
				return 1;
			}
			return 0;
		}

		private ulong DesiredAffinityOf(Entry e)
		{
			return SuppressionAffinityPolicy.DesiredAffinity(e.Reasons, (e.BackgroundRestorePending && !AntiCheatThrottled(e)) ? 0 : e.SqueezeAff, e.OrigAff, allMask);
		}

		private bool BeginSuppressionCycle(IntPtr handle, Entry entry, int pid)
		{
			string signature = PerformanceTuning.RecoveryCycleSerial + "\n" + pid + "/" + entry.Creation + "/"
				+ entry.Level + "/" + entry.BackgroundLevel + "/" + entry.AntiCheatLevel + "/" + GpuDemoteEnabled + "/" + EnforceBackgroundAffinity;
			return ActionFailureStore.BeginAutomaticCycle("BackgroundSuppression", FailureTargetOf(handle, entry), signature);
		}

		private bool ApplyEntryLocked(IntPtr h, Entry e, int pid)
		{
			if (FailureBudgetFor(e, "reconcile").Failures >= 8) return false;
			string target = FailureTargetOf(h, e);
			BeginSuppressionCycle(h, e, pid);
			int num = EntryIdentity(h, e);
			if (num != 0)
			{
				StopEntry(e, num == 1);
				return false;
			}
			if (ActionFailureStore.IsIsolated("BackgroundSuppression", target))
			{
				if (!RestoreEntryValues(h, e))
				{
					if (EntryIdentity(h, e) == 1)
					{
						StopEntry(e, true);
						return false;
					}
					ActionFailureStore.Trip("BackgroundSuppression", target, "Background recovery incomplete before apply");
					StopEntry(e, false);
					return false;
				}
				e.GaveUp = false;
				ActionFailureStore.CancelApply("BackgroundSuppression", target);
				BeginSuppressionCycle(h, e, pid);
			}
			string reason;
			if (!ActionFailureStore.TryBeginApply("BackgroundSuppression", target, out reason))
			{
				StopEntry(e, EntrySnapshotMatches(h, e));
				return false;
			}
			LastApplyError = "";
			bool flag2;
			try
			{
				num = EntryIdentity(h, e);
				if (num != 0)
				{
					ActionFailureStore.CancelApply("BackgroundSuppression", target);
					StopEntry(e, num == 1);
					return false;
				}
				flag2 = ApplyEntryValues(h, e);
			}
			catch (NotSupportedException)
			{
				return CancelSkippedEntry(h, e, target);
			}
			catch (OperationCanceledException)
			{
				return CancelSkippedEntry(h, e, target);
			}
			catch (Exception ex3)
			{
				LastApplyError = ex3.Message;
				flag2 = false;
			}
			switch (EntryIdentity(h, e))
			{
			case 1:
				ActionFailureStore.CancelApply("BackgroundSuppression", target);
				StopEntry(e, true);
				return false;
			case 2:
				LastApplyError = "Background identity unreadable after apply; recovery retained";
				flag2 = false;
				break;
			}
			if (flag2)
			{
				if (ActionFailureStore.CompleteApply("BackgroundSuppression", target))
				{
					e.GaveUp = false;
					e.GaveUpRestored = false;
					return true;
				}
				LastApplyError = "Background completion record could not be persisted";
			}
			ActionFailureStore.Trip("BackgroundSuppression", target, string.IsNullOrEmpty(LastApplyError) ? "Background process apply or verification failed" : LastApplyError);
			StopEntry(e, RestoreEntryValues(h, e));
			return false;
		}

		private bool CancelSkippedEntry(IntPtr handle, Entry entry, string target)
		{
			bool flag = EntryIdentity(handle, entry) == 1 || RestoreEntryValues(handle, entry);
			if (flag)
			{
				ActionFailureStore.CancelApply("BackgroundSuppression", target);
			}
			else
			{
				ActionFailureStore.Trip("BackgroundSuppression", target, "Background recovery incomplete after skipped apply");
			}
			StopEntry(entry, flag);
			return false;
		}

		private static string FailureTargetOf(IntPtr h, Entry entry)
		{
			if (string.IsNullOrEmpty(entry.FailureTarget))
			{
				entry.FailureTarget = Native.ImagePath(h) ?? entry.Name ?? "unknown-process";
			}
			return entry.FailureTarget;
		}

		private bool GiveUpAntiCheatLocked(IntPtr h, int pid, Entry e)
		{
			if (e == null || e.GaveUp || e.OrigPri == uint.MaxValue)
			{
				return false;
			}
			if ((e.Reasons & SuppressReason.AntiCheat) == 0 || (e.Reasons & SuppressReason.Background) != 0)
			{
				return false;
			}
			string text = LastApplyError;
			bool flag = RestoreBackgroundPlacement(h, e) && RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg, allMask, e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState, e.OrigGpu, e.OrigBoost, RestorePlacement(e));
			e.GaveUp = true;
			e.GaveUpRestored = flag;
			e.SetApplied(false);
			e.SqueezeAff = 0uL;
			e.NextReconcileTicks = long.MaxValue;
			Logger.Log(Lang.T("log.suppressioncore.giveup.1") + e.Name + " pid " + pid + Lang.T("log.suppressioncore.giveup.2") + (string.IsNullOrEmpty(text) ? "" : text) + Lang.T(flag ? "log.suppressioncore.giveup.3" : "log.suppressioncore.giveup.4"));
			return true;
		}

		private bool AllowAntiCheatCorrection(IntPtr handle, int pid, Entry entry)
		{
			if (!entry.Applied || !AntiCheatThrottled(entry)
				|| (entry.Reasons & SuppressReason.Background) != 0) return true;
			long now = DateTime.UtcNow.Ticks;
			if (now < entry.AntiCheatCorrectionWindow || now - entry.AntiCheatCorrectionWindow >= TimeSpan.FromMinutes(1).Ticks)
			{
				entry.AntiCheatCorrectionWindow = now;
				entry.AntiCheatCorrections = 0;
			}
			if (entry.AntiCheatCorrections < 3) { entry.AntiCheatCorrections++; return true; }
			LastApplyError = "anti-cheat-policy-conflict";
			GiveUpAntiCheatLocked(handle, pid, entry);
			return false;
		}

		internal static bool AffinityOnlyFailure(string detail)
		{
			if (string.IsNullOrEmpty(detail))
			{
				return false;
			}
			string[] array = detail.Split(',');
			foreach (string text in array)
			{
				if (text != "affinity-write" && text != "affinity-readback")
				{
					return false;
				}
			}
			return true;
		}

		private static int DesiredGpu(Entry e)
		{
			return DesiredGpuClass(GpuDemoteEnabled && HagsTweak.SchedulingAllowsGpuDemotionCached(), e.Reasons, e.BackgroundLevel, e.OrigGpu);
		}

		private static void ScheduleAfterMatch(Entry e, int pid)
		{
			RecordFailureBudget(e, "reconcile", true, 0);
			e.BackgroundReleaseDebt = false;
			e.ReconcileFailures = 0;
			if (AntiCheatThrottled(e))
			{
				e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(AntiCheatAuditSeconds(e.Level)).Ticks;
				return;
			}
			if (e.FastReconcileRemaining > 0)
			{
				e.FastReconcileRemaining--;
				e.NextReconcileTicks = DateTime.UtcNow.AddSeconds((e.FastReconcileRemaining > 0) ? 4 : (20 + PositiveMod(pid, 11))).Ticks;
			}
			else
			{
				e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(20 + PositiveMod(pid, 11)).Ticks;
			}
		}

		private static void ScheduleAfterApply(Entry e, bool applied, int pid)
		{
			FailureBudgetFor(e, "reconcile");
			if (!applied) e.SetApplied(false);
			if (applied)
			{
				RecordFailureBudget(e, "reconcile", true, 0);
				e.BackgroundReleaseDebt = false;
				e.ReconcileFailures = 0;
				e.FastReconcileRemaining = 2;
				e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(AntiCheatThrottled(e) ? AntiCheatAuditSeconds(e.Level) : 4).Ticks;
				return;
			}
			if (e.ReconcileFailures < 8)
			{
				e.ReconcileFailures++;
			}
			int num = 4;
			for (int i = 1; i < e.ReconcileFailures; i++)
			{
				if (num >= 60)
				{
					break;
				}
				num = Math.Min(60, num * 2);
			}
			e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(num).Ticks;
			RecordFailureBudget(e, "reconcile", false, e.NextReconcileTicks);
			if (e.ReconcileFailures >= 8) e.NextReconcileTicks = DateTime.MaxValue.Ticks;
		}

		internal static int AntiCheatAuditSeconds(SuppressionLevel level)
		{
			return level >= SuppressionLevel.Isolated ? 1 : (level >= SuppressionLevel.Restrained ? 2 : 4);
		}

		private static int PositiveMod(int value, int modulo)
		{
			int num = value % modulo;
			if (num >= 0)
			{
				return num;
			}
			return num + modulo;
		}

		private bool ApplyThrottle(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity, uint[] originalCpuSets, int desiredGpu, int origBoost, bool antiCheat, ulong desiredAffinity, bool affinityOwned = false)
		{
			affinityOwned |= antiCheat;
			Interlocked.Increment(ref applyOperations);
			List<string> list = new List<string>();
			if (DisableDynamicBoost(level, antiCheat) && DesiredBoostDisabled(level, antiCheat, origBoost) < 0)
			{
				list.Add("boost-snapshot");
				return StopFailedApply(list);
			}
			uint num = DesiredPriority(level, originalPriority, antiCheat);
			if (Native.GetPriorityClass(h) != num && !Native.SetPriorityClass(h, num))
			{
				list.Add("priority-write");
			}
			if (list.Count != 0)
			{
				return StopFailedApply(list);
			}
			int cls;
			if (desiredGpu >= 0 && Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0 && cls != desiredGpu)
			{
				if (Native.D3DKMTSetProcessSchedulingPriorityClass(h, desiredGpu) != 0)
				{
					list.Add("gpu-write");
				}
				else if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) != 0 || cls != desiredGpu)
				{
					list.Add("gpu-readback");
				}
			}
			if (list.Count != 0)
			{
				return StopFailedApply(list);
			}
			if (!affinityOwned && !Native.CpuSetsMatch(h, originalCpuSets) && !Native.RestoreCpuSetsVerified(h, originalCpuSets))
			{
				list.Add("cpu-sets-restore");
			}
			if (list.Count != 0)
			{
				return StopFailedApply(list);
			}
			if (!CpuTopology.MultiGroup && !affinityOwned)
			{
				ulong num2 = ((originalAffinity != 0) ? originalAffinity : allMask);
				bool flag = desiredAffinity != num2;
				ulong currentAffinity = Native.QueryAffinity(h);
				if (flag && currentAffinity != desiredAffinity)
				{
					long birth, cpu; ulong io; string admissionReason = null;
					if (!Native.QueryProcessSample(h, out birth, out cpu, out io)
						|| !GameThreadAffinityAdmission.Check(h, birth, currentAffinity, out admissionReason))
					{
						list.Add(admissionReason ?? GameThreadAffinityAdmission.RefusalPrefix + "process-identity-unconfirmed");
						return StopFailedApply(list);
					}
				}
				if (currentAffinity != desiredAffinity && !Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity))
				{
					list.Add(flag ? "affinity-write" : "affinity-restore");
				}
				if (Native.QueryAffinity(h) != desiredAffinity)
				{
					list.Add(flag ? "affinity-readback" : "affinity-restore-readback");
				}
			}
			if (list.Count != 0)
			{
				return StopFailedApply(list);
			}
			int num3 = DesiredIoPriority(level, antiCheat);
			if (Native.QueryIoPriority(h) != num3 && !Native.TrySetIoPriority(h, num3))
			{
				list.Add("io-write");
			}
			if (list.Count != 0)
			{
				return StopFailedApply(list);
			}
			int num4 = DesiredPagePriority(level, antiCheat);
			if (Native.QueryPagePriority(h) != num4 && !Native.TrySetPagePriority(h, num4))
			{
				list.Add("page-write");
			}
			if (list.Count != 0)
			{
				return StopFailedApply(list);
			}
			if (Native.PowerThrottlingSupported)
			{
				bool flag2 = DesiredTimerSeal(level, antiCheat);
				uint num5 = Native.EcoQoSWantMask(flag2);
				int controlMask;
				int stateMask;
				bool flag3 = Native.TryQueryPowerThrottling(h, out controlMask, out stateMask);
				bool flag4 = flag3 && !flag2 && (controlMask & 4) != 0;
				if ((!flag3 || ((uint)controlMask & num5) != num5 || ((uint)stateMask & num5) != num5 || flag4) && !Native.ApplyEcoQoS(h, flag2))
				{
					list.Add("eco-write");
				}
			}
			if (list.Count != 0)
			{
				return StopFailedApply(list);
			}
			string text = ApplyBoostPolicy(level, antiCheat, origBoost, () => Native.QueryBoostDisabled(h), (bool disabled) => Native.TrySetBoostDisabled(h, disabled));
			if (text.Length > 0)
			{
				list.Add(text);
			}
			if (list.Count != 0)
			{
				return StopFailedApply(list);
			}
			if (Native.GetPriorityClass(h) != num)
			{
				list.Add("priority-readback");
			}
			if (Native.QueryIoPriority(h) != num3)
			{
				list.Add("io-readback");
			}
			if (Native.QueryPagePriority(h) != num4)
			{
				list.Add("page-readback");
			}
			if (Native.PowerThrottlingSupported && !EcoStateVisible(h, Native.EcoQoSWantMask(DesiredTimerSeal(level, antiCheat))))
			{
				list.Add("eco-readback");
			}
			LastApplyError = string.Join(",", list.ToArray());
			return list.Count == 0;
		}

		private bool StopFailedApply(List<string> failed)
		{
			LastApplyError = string.Join(",", failed.ToArray());
			return false;
		}

		private static bool EcoStateVisible(IntPtr h, uint required)
		{
			int controlMask;
			int stateMask;
			return ConfirmEcoState(() => Native.TryQueryPowerThrottling(h, out controlMask, out stateMask) && PowerStateMatches(controlMask, stateMask, required), delegate
			{
				Thread.Sleep(1);
			});
		}

		internal static bool ConfirmEcoState(Func<bool> query, Action wait)
		{
			for (int i = 0; i < 4; i++)
			{
				if (query())
				{
					return true;
				}
				if (i < 3)
				{
					wait();
				}
			}
			return false;
		}

		public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask)
		{
			return RestoreValues(h, pri, aff, io, pg, allMask, new uint[0]);
		}

		public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets)
		{
			return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, -1, -1);
		}

		public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState)
		{
			return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, -1);
		}

		public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState, int gpu)
		{
			return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, -1);
		}

		public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost)
		{
			return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, boost, true);
		}

		public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost, bool restorePlacement)
		{
			return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, boost, restorePlacement, true);
		}

		internal static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost, bool restorePlacement, bool restorePriority)
		{
			bool flag = !restorePlacement || Native.RestoreCpuSetsVerified(h, cpuSets);
			uint num = ((pri == 0 || pri == uint.MaxValue) ? 32u : pri);
			if (restorePriority) flag &= Native.SetPriorityClass(h, num);
			ulong num2 = ((aff != 0) ? aff : allMask);
			if (restorePlacement && !CpuTopology.MultiGroup && Native.QueryAffinity(h) != num2)
			{
				flag &= Native.SetProcessAffinityMask(h, (UIntPtr)num2);
			}
			int num3 = ((io >= 0) ? io : 2);
			flag &= Native.TrySetIoPriority(h, num3);
			int num4 = ((pg >= 0) ? pg : 5);
			flag &= Native.TrySetPagePriority(h, num4);
			if (Native.PowerThrottlingSupported)
			{
				flag &= Native.RestorePowerThrottling(h, qosControl, qosState);
			}
			if (boost >= 0)
			{
				if (Native.QueryBoostDisabled(h) != boost)
				{
					flag &= Native.TrySetBoostDisabled(h, boost == 1);
				}
				flag &= Native.QueryBoostDisabled(h) == boost;
			}
			if (restorePriority) flag &= Native.GetPriorityClass(h) == num;
			flag &= Native.QueryIoPriority(h) == num3;
			flag &= Native.QueryPagePriority(h) == num4;
			if (restorePlacement && !CpuTopology.MultiGroup)
			{
				flag &= Native.QueryAffinity(h) == num2;
			}
			int cls;
			if (gpu >= 0 && Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0 && cls != gpu)
			{
				Native.D3DKMTSetProcessSchedulingPriorityClass(h, gpu);
				flag &= Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0 && cls == gpu;
			}
			return flag;
		}

		private RestoreResult RestoreOne(int pid, Entry e)
		{
			if (!ReleaseBackgroundJob(e)) return RestoreResult.Protected;
			IntPtr intPtr = Native.OpenProcess(12800, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				IntPtr intPtr2 = Native.OpenProcess(4096, false, pid);
				if (intPtr2 == IntPtr.Zero)
				{
					if (!Native.LastOpenProcessFailureWasNoSuchProcess())
					{
						return RestoreResult.Protected;
					}
					return RestoreResult.Gone;
				}
				try
				{
					if (!Native.StillActive(intPtr2))
					{
						return RestoreResult.Gone;
					}
					string text = Native.ImageName(intPtr2);
					if (text != null && !SameName(text, e.Name))
					{
						return RestoreResult.Gone;
					}
					long creation;
					long cpu;
					ulong io;
					if (e.Creation > 0 && Native.QueryProcessSample(intPtr2, out creation, out cpu, out io) && creation != e.Creation)
					{
						return RestoreResult.Gone;
					}
					return RestoreResult.Protected;
				}
				finally
				{
					Native.CloseHandle(intPtr2);
				}
			}
			try
			{
				if (!Native.StillActive(intPtr))
				{
					return RestoreResult.Gone;
				}
				if (e.Creation <= 0)
				{
					return RestoreResult.Protected;
				}
				if (e.Name != null)
				{
					string text2 = Native.ImageName(intPtr);
					if (text2 == null)
					{
						return RestoreResult.Protected;
					}
					if (!SameName(text2, e.Name))
					{
						return RestoreResult.Gone;
					}
				}
				if (e.Creation > 0)
				{
					long creation2;
					long cpu2;
					ulong io2;
					if (!Native.QueryProcessSample(intPtr, out creation2, out cpu2, out io2))
					{
						return RestoreResult.Protected;
					}
					if (creation2 != e.Creation)
					{
						return RestoreResult.Gone;
					}
				}
				if (RestoreBackgroundPlacement(intPtr, e) && RestoreValues(intPtr, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg, allMask, e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState, e.OrigGpu, e.OrigBoost, RestorePlacement(e)))
				{
					return RestoreResult.Restored;
				}
				return (!Native.StillActive(intPtr)) ? RestoreResult.Gone : RestoreResult.Protected;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private static bool SameProcess(IntPtr h, Entry e)
		{
			if (e.Creation <= 0)
			{
				return false;
			}
			if (e.Name != null)
			{
				string text = Native.ImageName(h);
				if (text == null || !SameName(text, e.Name))
				{
					return false;
				}
			}
			if (e.Creation > 0)
			{
				long creation;
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(h, out creation, out cpu, out io))
				{
					return false;
				}
				if (creation != e.Creation)
				{
					return false;
				}
			}
			return true;
		}

		private static bool SameName(string a, string b)
		{
			return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
		}

		internal static bool FullyBlockedDetail(string detail)
		{
			if (detail != null && detail.Contains("priority-write") && detail.Contains("io-write"))
			{
				return detail.Contains("page-write");
			}
			return false;
		}

		internal static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost)
		{
			return SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qosControl, qosState, gpu, boost, true);
		}

		private static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg, uint[] cpuSets, int qosControl, int qosState, int gpu, int boost, bool placement)
		{
			if (!SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qosControl, qosState, gpu, placement))
			{
				return false;
			}
			if (boost >= 0)
			{
				int num = Native.QueryBoostDisabled(h);
				if (num != boost)
				{
					return false;
				}
			}
			return true;
		}

		internal static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg, uint[] cpuSets, int qosControl, int qosState, int gpu)
		{
			return SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qosControl, qosState, gpu, true);
		}

		private static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg, uint[] cpuSets, int qosControl, int qosState, int gpu, bool placement)
		{
			if (pri == 0 || pri == uint.MaxValue || io < 0 || pg < 0)
			{
				return false;
			}
			if (Native.GetPriorityClass(h) != pri)
			{
				return false;
			}
			if (Native.QueryIoPriority(h) != io)
			{
				return false;
			}
			if (Native.QueryPagePriority(h) != pg)
			{
				return false;
			}
			if (placement && !CpuTopology.MultiGroup && Native.QueryAffinity(h) != aff)
			{
				return false;
			}
			if (placement && !Native.CpuSetsMatch(h, cpuSets ?? new uint[0]))
			{
				return false;
			}
			int controlMask;
			int stateMask;
			if (qosControl >= 0 && Native.PowerThrottlingSupported && (!Native.TryQueryPowerThrottling(h, out controlMask, out stateMask) || controlMask != qosControl || stateMask != qosState))
			{
				return false;
			}
			int cls;
			if (gpu >= 0 && (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) != 0 || cls != gpu))
			{
				return false;
			}
			return true;
		}

		private bool TryNeutralizeUnwritableLocked(IntPtr h, int pid, Entry e)
		{
			if (e == null || e.OrigPri == uint.MaxValue)
			{
				return false;
			}
			if (!FullyBlockedDetail(LastApplyError))
			{
				return false;
			}
			if (Native.PowerThrottlingSupported)
			{
				Native.RestorePowerThrottling(h, e.OrigQoSControl, e.OrigQoSState);
			}
			if (e.OrigBoost == 0)
			{
				Native.TrySetBoostDisabled(h, false);
			}
			int cls;
			if (e.OrigGpu >= 0 && Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0 && cls != e.OrigGpu)
			{
				Native.D3DKMTSetProcessSchedulingPriorityClass(h, e.OrigGpu);
			}
			if (!EntrySnapshotMatches(h, e))
			{
				return false;
			}
			e.SetOriginalPriority(uint.MaxValue);
			e.SetApplied(false);
			e.NextRetryTicks = 0L;
			PersistJournalLocked();
			bool flag = SelfProtectedRoster.Mark(e.Name);
			if (flag || ShouldLogProtected(e.Name + "-unwritable"))
			{
				Logger.Log(Lang.T("log.suppressioncoreapply.1") + e.Name + " pid " + pid + Lang.T("log.suppressioncoreapply.2") + (flag ? Lang.T("log.suppressioncoreapply.3") : ""));
			}
			return true;
		}

		private bool SaveLayoutLocked()
		{
			return SaveJournalLocked(true);
		}

		private static void LayoutFailed(Entry e)
		{
			FailureBudgetFor(e, "layout");
			e.AutomaticLayoutFailures = Math.Min(8, e.AutomaticLayoutFailures + 1);
			e.AutomaticLayoutRetryAt = e.AutomaticLayoutFailures >= 8 ? DateTime.MaxValue.Ticks : DateTime.UtcNow.AddSeconds(Math.Min(60, 4 * (1 << Math.Min(4, e.AutomaticLayoutFailures - 1)))).Ticks;
			RecordFailureBudget(e, "layout", false, e.AutomaticLayoutRetryAt);
		}

		private static bool RestoreAutomaticLayoutValues(IntPtr h, Entry e)
		{
			if (e.AntiCheatPlacementExternal)
			{
				return true;
			}
			if (e.AutomaticLayoutSets == null || e.AutomaticLayoutYielded)
			{
				return true;
			}
			IAutomaticLayoutPlatform layoutPlatform = LayoutPlatform;
			if (layoutPlatform == null)
			{
				return false;
			}
			uint[] array = layoutPlatform.ReadSets(h);
			ulong mask;
			if (array == null || !layoutPlatform.ReadAffinity(h, out mask))
			{
				return false;
			}
			if (CoreThreadPlacement.Same(array, e.OrigCpuSets ?? new uint[0]) && mask == e.OrigAff)
			{
				return true;
			}
			if (!CoreThreadPlacement.Same(array, e.AutomaticLayoutSets) || mask != e.OrigAff)
			{
				e.AutomaticLayoutYielded = true;
				return true;
			}
			if (layoutPlatform.WriteSets(h, e.OrigCpuSets ?? new uint[0]))
			{
				return CoreThreadPlacement.Same(layoutPlatform.ReadSets(h), e.OrigCpuSets ?? new uint[0]);
			}
			return false;
		}

		private bool RestoreAutomaticLayoutLocked(int pid, Entry e)
		{
			if (e.AutomaticLayoutSets == null)
			{
				return true;
			}
			if (RestoreBudgetBlocked(e, "layout", false)) return false;
			if (e.RestoreInFlight)
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			e.AutomaticLayoutRestoring = true;
			IAutomaticLayoutPlatform layoutPlatform = LayoutPlatform;
			if (layoutPlatform == null)
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			IntPtr intPtr = layoutPlatform.Open(pid, e.Creation, e.Name);
			if (intPtr == IntPtr.Zero)
			{
				LayoutFailed(e);
				return false;
			}
			try
			{
				if (!RestoreAutomaticLayoutValues(intPtr, e))
				{
					LayoutFailed(e);
					return false;
				}
				uint[] automaticLayoutSets = e.AutomaticLayoutSets;
				e.AutomaticLayoutSets = null;
				if (!SaveLayoutLocked())
				{
					e.AutomaticLayoutSets = automaticLayoutSets;
					LayoutFailed(e);
					return false;
				}
				e.AutomaticLayoutRestoring = false;
				RecordFailureBudget(e, "layout", true, 0);
				return true;
			}
			finally
			{
				layoutPlatform.Close(intPtr);
			}
		}

		internal bool RestoreAutomaticLayouts()
		{
			lock (sync)
			{
				bool result = true;
				foreach (KeyValuePair<int, Entry> item in map)
				{
					if (!RestoreAutomaticLayoutLocked(item.Key, item.Value))
					{
						result = false;
					}
				}
				return result;
			}
		}

		internal void RetryRetiredAutomaticLayouts()
		{
			lock (sync)
			{
				long ticks = DateTime.UtcNow.Ticks;
				foreach (KeyValuePair<int, Entry> item in map)
				{
					if (item.Value.AutomaticLayoutSets != null && !RestoreBudgetBlocked(item.Value, "layout", true) && ticks >= item.Value.AutomaticLayoutRetryAt)
					{
						RestoreAutomaticLayoutLocked(item.Key, item.Value);
					}
				}
			}
		}

		private static bool PlacementIsProtected(Entry e)
		{
			if (e.BackgroundJobPlacement || e.AntiCheatPlacementExternal || AntiCheatThrottled(e))
			{
				return true;
			}
			if (!e.SqueezeRefused && e.AutomaticLayoutSets == null && !e.AutomaticLayoutYielded)
			{
				return e.AutomaticLayoutRestoring;
			}
			return true;
		}

		private static bool RestorePlacement(Entry e)
		{
			return !e.BackgroundJobPlacement && !e.AntiCheatPlacementExternal && !e.BackgroundPlacementOwned && !e.AutomaticLayoutYielded;
		}

		// Settle legacy affinity debt before admitting an independent anti-cheat affinity lease.
		internal bool PrepareAntiCheatAffinity(int pid, long creation, string name)
		{
			Entry entry;
			lock (sync)
			{
				if (!map.TryGetValue(pid, out entry) || entry.Creation != creation || !SameName(entry.Name, name)
					|| !AntiCheatThrottled(entry) || entry.OrigPri == uint.MaxValue || entry.RestoreInFlight || entry.LegacyPlacementDebt) return false;
				if (entry.AntiCheatPlacementExternal && entry.Journaled) return true;
				if (entry.AffinityPrepareInFlight) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
				if (RestoreBudgetBlocked(entry, "affinity-prepare", false)) return false;
				entry.AffinityPrepareInFlight = true;
			}
			bool completed = false;
			try { completed = PrepareAntiCheatAffinityCore(pid, creation, name); return completed; }
			finally { lock (sync) { entry.AffinityPrepareInFlight = false; RecordFailureBudget(entry, "affinity-prepare", completed, 0); } }
		}

		private bool PrepareAntiCheatAffinityCore(int pid, long creation, string name)
		{
			Entry entry;
			lock (sync)
			{
				if (!map.TryGetValue(pid, out entry) || !AntiCheatThrottled(entry) || entry.Creation != creation || creation <= 0 || !SameName(entry.Name, name) || entry.OrigPri == uint.MaxValue || entry.RestoreInFlight || entry.LegacyPlacementDebt)
				{
					return false;
				}
				if (entry.AntiCheatPlacementExternal)
				{
					return entry.Journaled || SaveJournalLocked(true);
				}
			}
			IntPtr handle = Native.OpenProcess(12800, false, pid);
			if (handle == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				lock (sync)
				{
					Entry current;
					if (!map.TryGetValue(pid, out current) || current != entry || !AntiCheatThrottled(entry) || entry.RestoreInFlight || !SameProcess(handle, entry))
					{
						return false;
					}
					if (!RestoreBackgroundPlacement(handle, entry))
					{
						return false;
					}
					bool oldOwned = entry.BackgroundPlacementOwned;
					bool oldPending = entry.BackgroundRestorePending;
					bool oldVerified = entry.BackgroundPlacementVerified;
					bool oldYielded = entry.AutomaticLayoutYielded;
					bool oldRestoring = entry.AutomaticLayoutRestoring;
					uint[] oldLayout = entry.AutomaticLayoutSets;
					ulong oldSqueeze = entry.SqueezeAff;
					entry.AntiCheatPlacementExternal = true;
					entry.BackgroundPlacementOwned = false;
					entry.BackgroundRestorePending = false;
					entry.BackgroundPlacementVerified = false;
					entry.AutomaticLayoutSets = null;
					entry.AutomaticLayoutYielded = false;
					entry.AutomaticLayoutRestoring = false;
					entry.SqueezeAff = 0;
					if (SaveJournalLocked(true))
					{
						return true;
					}
					entry.AntiCheatPlacementExternal = false;
					entry.BackgroundPlacementOwned = oldOwned;
					entry.BackgroundRestorePending = oldPending;
					entry.BackgroundPlacementVerified = oldVerified;
					entry.AutomaticLayoutSets = oldLayout;
					entry.AutomaticLayoutYielded = oldYielded;
					entry.AutomaticLayoutRestoring = oldRestoring;
					entry.SqueezeAff = oldSqueeze;
					return false;
				}
			}
			finally
			{
				Native.CloseHandle(handle);
			}
		}

		private bool ReapplyReleasedEntry(IntPtr h, Entry e, int pid)
		{
			lock (sync)
			{
				Entry value;
				if (!map.TryGetValue(pid, out value) || value != e || e.Reasons == SuppressReason.None || !SameProcess(h, e))
				{
					return false;
				}
				if ((e.Reasons & SuppressReason.Background) == 0 && !ReleaseBackgroundJob(e)) return false;
				return VerifyBackgroundJob(e) && ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e), e.OrigBoost, AntiCheatThrottled(e), DesiredAffinityOf(e), PlacementIsProtected(e));
			}
		}

		public SuppressionCore()
			: this(null)
		{
		}

		public SuppressionCore(string statePath)
		{
			throttleMask = CpuTopology.ThrottleMask;
			allMask = CpuTopology.AllMask;
			journalPath = statePath;
			LoadJournal();
		}

		public void BeginBatch()
		{
			Monitor.Enter(batchGate);
			try
			{
				Monitor.Enter(sync);
			}
			catch
			{
				Monitor.Exit(batchGate);
				throw;
			}
			if (batchDepth == 0)
			{
				batchApplyResults.Clear();
				batchApplyErrors.Clear();
			}
			batchDepth++;
		}

		public BatchResult EndBatch()
		{
			if (!Monitor.IsEntered(batchGate) || !Monitor.IsEntered(sync))
			{
				throw new InvalidOperationException("EndBatch requires a matching BeginBatch on the same thread.");
			}
			List<KeyValuePair<int, string>> list = null;
			bool flag = true;
			bool flag2 = false;
			try
			{
				try
				{
					if (batchDepth <= 0)
					{
						throw new InvalidOperationException("Suppression batch depth is invalid.");
					}
					batchDepth--;
					if (batchDepth == 0)
					{
						flag2 = true;
						if (batchJournalDirty)
						{
							flag = SaveJournalLocked();
						}
						batchJournalDirty = false;
						if (batchApply.Count > 0)
						{
							list = new List<KeyValuePair<int, string>>(batchApply);
							batchApply.Clear();
						}
					}
				}
				finally
				{
					Monitor.Exit(sync);
				}
				if (list != null)
				{
					foreach (KeyValuePair<int, string> item in list)
					{
						string error = null;
						bool flag3;
						try
						{
							if (flag)
							{
								flag3 = ApplyQueued(item.Key, item.Value, out error);
							}
							else
							{
								flag3 = false;
								error = "journal-write";
							}
						}
						catch (Exception ex)
						{
							flag3 = false;
							error = "apply-exception:" + ex.GetType().Name;
						}
						lock (sync)
						{
							batchApplyResults[item.Key] = flag3;
							if (flag3)
							{
								batchApplyErrors.Remove(item.Key);
							}
							else
							{
								batchApplyErrors[item.Key] = error ?? "unknown";
							}
						}
					}
				}
				if (!flag2)
				{
					return new BatchResult(null);
				}
				lock (sync)
				{
					Dictionary<int, bool> values = new Dictionary<int, bool>(batchApplyResults);
					Dictionary<int, string> errorValues = new Dictionary<int, string>(batchApplyErrors);
					batchApplyResults.Clear();
					batchApplyErrors.Clear();
					RefreshThrottledCacheLocked();
					RefreshGroupCountsLocked();
					return new BatchResult(values, errorValues);
				}
			}
			finally
			{
				Monitor.Exit(batchGate);
			}
		}

		public bool HasReason(int pid, SuppressReason reason)
		{
			lock (sync)
			{
				Entry value;
				return map.TryGetValue(pid, out value) && (value.Reasons & reason) != 0;
			}
		}

		public bool IsThrottled(int pid)
		{
			lock (sync)
			{
				Entry value;
				return map.TryGetValue(pid, out value) && value.OrigPri != uint.MaxValue && value.Applied;
			}
		}

		public SuppressionLevel LevelOf(int pid)
		{
			lock (sync)
			{
				Entry value;
				return map.TryGetValue(pid, out value) ? value.Level : SuppressionLevel.None;
			}
		}

		public SuppressionLevel LevelOf(int pid, SuppressReason reason)
		{
			lock (sync)
			{
				Entry value;
				if (!map.TryGetValue(pid, out value))
				{
					return SuppressionLevel.None;
				}
				SuppressionLevel suppressionLevel = SuppressionLevel.None;
				if ((reason & SuppressReason.AntiCheat) != 0)
				{
					suppressionLevel = value.AntiCheatLevel;
				}
				if ((reason & SuppressReason.Background) != 0 && value.BackgroundLevel > suppressionLevel)
				{
					suppressionLevel = value.BackgroundLevel;
				}
				return suppressionLevel;
			}
		}

		public bool Reconcile(int pid, string expectedName, SuppressReason reason)
		{
			return Reconcile(pid, expectedName, reason, false);
		}

		internal bool Reconcile(int pid, string expectedName, SuppressReason reason, bool forceAudit)
		{
			long ticks = DateTime.UtcNow.Ticks;
			Entry value;
			uint origPri;
			ulong origAff;
			uint[] origCpuSets;
			SuppressionLevel level;
			lock (sync)
			{
				if (!map.TryGetValue(pid, out value) || (value.Reasons & reason) == 0 || value.OrigPri == uint.MaxValue || !value.Journaled || !SameName(value.Name, expectedName))
				{
					return false;
				}
				if (value.GaveUp)
				{
					return true;
				}
				if (FailureBudgetFor(value, "reconcile").Failures >= 8) return true;
				if (!forceAudit && ticks < value.NextReconcileTicks)
				{
					return true;
				}
				origPri = value.OrigPri;
				origAff = value.OrigAff;
				origCpuSets = value.OrigCpuSets;
				level = value.Level;
			}
			IntPtr intPtr = Native.OpenProcess(12800, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				lock (sync)
				{
					Entry value2;
					if (map.TryGetValue(pid, out value2) && value2 == value)
					{
						ScheduleAfterApply(value2, false, pid);
					}
				}
				return true;
			}
			try
			{
				string text = Native.ImageName(intPtr);
				if (text == null || !SameName(text, expectedName))
				{
					if (text == null) { lock (sync) ScheduleAfterApply(value, false, pid); }
					return false;
				}
				long creation;
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(intPtr, out creation, out cpu, out io))
				{
					lock (sync) ScheduleAfterApply(value, false, pid);
					return false;
				}
				lock (sync)
				{
					Entry value3;
					if (!map.TryGetValue(pid, out value3) || value3 != value || (value3.Reasons & reason) == 0 || (value3.Creation > 0 && value3.Creation != creation))
					{
						return false;
					}
					if (value3.Level != level || value3.OrigPri != origPri || value3.OrigAff != origAff || !object.ReferenceEquals(value3.OrigCpuSets, origCpuSets))
					{
						return true;
					}
					int cls;
					if (value3.OrigGpu < 0 && GpuDemoteEnabled && (value3.Reasons & SuppressReason.Background) != 0 && value3.BackgroundLevel >= SuppressionLevel.Restrained && Native.D3DKMTGetProcessSchedulingPriorityClass(intPtr, out cls) == 0)
					{
						value3.OrigGpu = cls;
						if (!PersistJournalLocked())
						{
							value3.OrigGpu = -1;
						}
						else
						{
							Logger.Log(Lang.T("log.suppressioncore.6") + expectedName + " pid " + pid + Lang.T("log.suppressioncore.7"));
						}
					}
					int desiredGpu = DesiredGpu(value3);
					if (VerifyBackgroundJob(value3) && ThrottleMatches(intPtr, level, origPri, origAff, value3.OrigCpuSets, desiredGpu, AntiCheatThrottled(value3), DesiredAffinityOf(value3), PlacementIsProtected(value3), value3.OrigBoost))
					{
						if (!value3.Applied && value3.ReconcileFailures > 0)
						{
							SuccessLogBatch.Record("reconcile-verified", expectedName, pid, value3.Creation,
								"reason=" + reason + " previousFailures=" + value3.ReconcileFailures);
						}
						value3.SetApplied(true);
						ScheduleAfterMatch(value3, pid);
						return true;
					}
					bool applied = value3.Applied;
					if (!AllowAntiCheatCorrection(intPtr, pid, value3)) return true;
					int reconcileFailures = value3.ReconcileFailures;
					value3.SetApplied(ApplyEntryLocked(intPtr, value3, pid));
					ScheduleAfterApply(value3, value3.Applied, pid);
					if (value3.Applied)
					{
						if (!applied && reconcileFailures > 0)
						{
							SuccessLogBatch.Record("reconcile-recovered", expectedName, pid, value3.Creation,
								"reason=" + reason + " previousFailures=" + reconcileFailures);
						}
					}
					else
					{
						if (TryNeutralizeUnwritableLocked(intPtr, pid, value3))
						{
							return true;
						}
						if (reconcileFailures < 3)
						{
							SuccessLogBatch.FlushPending();
							Logger.Log(Lang.T("log.gamemodesweep.1") + expectedName + "(pid " + pid + ") " + ApplyFailureText.Of(LastApplyError) + Lang.T("log.suppressioncore.11"));
						}
					}
					return true;
				}
			}
			finally
			{
				Native.CloseHandle(intPtr);
				if ((reason & SuppressReason.AntiCheat) != 0)
				{
					lock (sync) RefreshGroupCountsLocked();
				}
			}
		}

		public bool AnyWith(SuppressReason reason)
		{
			lock (sync)
			{
				foreach (KeyValuePair<int, Entry> item in map)
				{
					if ((item.Value.Reasons & reason) != 0)
					{
						return true;
					}
				}
			}
			return false;
		}

		public string NameOf(int pid)
		{
			lock (sync)
			{
				Entry value;
				return map.TryGetValue(pid, out value) ? value.Name : null;
			}
		}

		public long CreationOf(int pid)
		{
			lock (sync)
			{
				Entry value;
				return map.TryGetValue(pid, out value) ? value.Creation : 0;
			}
		}

		public List<int> PidsWith(SuppressReason reason)
		{
			List<int> list = new List<int>();
			lock (sync)
			{
				foreach (KeyValuePair<int, Entry> item in map)
				{
					if ((item.Value.Reasons & reason) != 0)
					{
						list.Add(item.Key);
					}
				}
				return list;
			}
		}

        internal KeyValuePair<int, long>[] CaptureAppliedActivity()
        {
            lock (sync)
            {
                var result = new List<KeyValuePair<int, long>>();
                foreach (KeyValuePair<int, Entry> item in map)
                    if (item.Value.Reasons != SuppressReason.None && item.Value.OrigPri != uint.MaxValue
                        && item.Value.Applied && !item.Value.RestoreInFlight)
                        result.Add(new KeyValuePair<int, long>(item.Key, item.Value.Creation));
                return result.ToArray();
            }
        }

		public int CountThrottled(SuppressReason reason)
		{
			if (!Monitor.TryEnter(sync, 15))
			{
				return Volatile.Read(ref lastThrottledCount);
			}
			try
			{
				int num = CountThrottledLocked(reason);
				if (reason == SuppressReason.Background) Volatile.Write(ref lastThrottledCount, num);
				return num;
			}
			finally
			{
				Monitor.Exit(sync);
			}
		}

		public int ThrottledCountCached()
		{
			return Volatile.Read(ref lastThrottledCount);
		}

		private int CountThrottledLocked(SuppressReason reason)
		{
			int num = 0;
			foreach (KeyValuePair<int, Entry> item in map)
			{
				if ((item.Value.Reasons & reason) != 0 && item.Value.OrigPri != uint.MaxValue && item.Value.Applied)
				{
					num++;
				}
			}
			return num;
		}

		private void RefreshThrottledCacheLocked()
		{
			Volatile.Write(ref lastThrottledCount, CountThrottledLocked(SuppressReason.Background));
		}

		private void RefreshGroupCountsLocked()
		{
			long version = Interlocked.Read(ref groupCountsVersion);
			if (version == publishedGroupCountsVersion) return;
			Dictionary<string, int[]> dictionary = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
			foreach (KeyValuePair<int, Entry> item in map)
			{
				if ((item.Value.Reasons & SuppressReason.AntiCheat) != 0)
				{
					string key = item.Value.Group ?? "";
					int[] value;
					if (!dictionary.TryGetValue(key, out value))
					{
						value = (dictionary[key] = new int[2]);
					}
					if (item.Value.OrigPri == uint.MaxValue || !item.Value.Applied)
					{
						value[1]++;
					}
					else
					{
						value[0]++;
					}
				}
			}
			lock (lastGroupCounts)
			{
				foreach (KeyValuePair<string, int[]> item2 in dictionary)
				{
					lastGroupCounts[item2.Key] = item2.Value;
				}
				foreach (string item3 in new List<string>(lastGroupCounts.Keys))
				{
					if (!dictionary.ContainsKey(item3))
					{
						lastGroupCounts[item3] = new int[2];
					}
				}
			}
			publishedGroupCountsVersion = version;
		}

		public void AntiCheatGroupCountsCached(string groupKey, out int throttled, out int protectedCnt)
		{
			if (Monitor.TryEnter(sync))
			{
				try { RefreshGroupCountsLocked(); }
				finally { Monitor.Exit(sync); }
			}
			int num = 0;
			int num2 = 0;
			lock (lastGroupCounts)
			{
				int[] value;
				if (lastGroupCounts.TryGetValue(groupKey ?? "", out value))
				{
					num = value[0];
					num2 = value[1];
				}
			}
			throttled = num;
			protectedCnt = num2;
		}

		public void AntiCheatGroupCounts(string groupKey, out int throttled, out int protectedCnt)
		{
			int num = 0;
			int num2 = 0;
			string key = groupKey ?? "";
			if (!Monitor.TryEnter(sync, 15))
			{
				lock (lastGroupCounts)
				{
					int[] value;
					if (lastGroupCounts.TryGetValue(key, out value))
					{
						num = value[0];
						num2 = value[1];
					}
				}
				throttled = num;
				protectedCnt = num2;
				return;
			}
			try
			{
				foreach (KeyValuePair<int, Entry> item in map)
				{
					if ((item.Value.Reasons & SuppressReason.AntiCheat) != 0 && SameName(item.Value.Group, groupKey))
					{
						if (item.Value.OrigPri == uint.MaxValue || !item.Value.Applied)
						{
							num2++;
						}
						else
						{
							num++;
						}
					}
				}
				lock (lastGroupCounts)
				{
					lastGroupCounts[key] = new int[2] { num, num2 };
				}
				// The direct query distinguishes null from empty; the full cache combines them.
				if (string.IsNullOrEmpty(groupKey)) publishedGroupCountsVersion = -1;
			}
			finally
			{
				Monitor.Exit(sync);
			}
			throttled = num;
			protectedCnt = num2;
		}

		private static bool ShouldLogProtected(string name)
		{
			long ticks = DateTime.UtcNow.Ticks;
			lock (protectedLogTimes)
			{
				string key = name ?? "";
				long value;
				if (protectedLogTimes.TryGetValue(key, out value) && ticks - value < TimeSpan.FromMinutes(10.0).Ticks)
				{
					return false;
				}
				protectedLogTimes[key] = ticks;
				return true;
			}
		}

		private static bool SameCpuSets(uint[] a, uint[] b)
		{
			if (a == null || b == null || a.Length == 0 || a.Length != b.Length)
			{
				return false;
			}
			HashSet<uint> hashSet = new HashSet<uint>(b);
			foreach (uint item in a)
			{
				if (!hashSet.Contains(item))
				{
					return false;
				}
			}
			return true;
		}

		private static void SetReasonLevel(Entry e, SuppressReason reason, SuppressionLevel level)
		{
			if ((reason & SuppressReason.AntiCheat) != 0)
			{
				e.AntiCheatLevel = level;
				if (!e.Journaled && !e.LegacyPlacementDebt && !e.BackgroundPlacementOwned && e.AutomaticLayoutSets == null && !e.AutomaticLayoutYielded)
				{
					e.AntiCheatPlacementExternal = true;
				}
			}
			if ((reason & SuppressReason.Background) != 0)
			{
				e.BackgroundLevel = level;
			}
			e.Level = EffectiveLevel(e);
		}

		private static bool AntiCheatThrottled(Entry e)
		{
			return (e.Reasons & SuppressReason.AntiCheat) != 0;
		}

		private static SuppressionLevel EffectiveLevel(Entry e)
		{
			if (e.AntiCheatLevel <= e.BackgroundLevel)
			{
				return e.BackgroundLevel;
			}
			return e.AntiCheatLevel;
		}

		private bool PersistJournalLocked()
		{
			if (batchDepth > 0 || journalDefer > 0)
			{
				batchJournalDirty = true;
				return true;
			}
			return SaveJournalLocked();
		}

		private void BeginJournalDefer()
		{
			lock (sync)
			{
				journalDefer++;
			}
		}

		private void EndJournalDefer()
		{
			lock (sync)
			{
				if (journalDefer > 0)
				{
					journalDefer--;
					if (journalDefer == 0 && batchDepth == 0 && batchJournalDirty)
					{
						batchJournalDirty = false;
						SaveJournalLocked();
					}
				}
			}
		}

		private bool QueueApplyLocked(int pid, string name)
		{
			if (batchDepth <= 0)
			{
				return false;
			}
			batchApply[pid] = name;
			return true;
		}

		private void RecordBatchApplyResultLocked(int pid, bool applied, string error)
		{
			if (batchDepth > 0 && !batchApply.ContainsKey(pid))
			{
				batchApplyResults[pid] = applied;
				if (applied)
				{
					batchApplyErrors.Remove(pid);
				}
				else
				{
					batchApplyErrors[pid] = error ?? "unknown";
				}
			}
		}

		private bool ApplyQueued(int pid, string expectedName, out string error)
		{
			error = null;
			Entry entry;
			uint origPri;
			ulong origAff;
			SuppressionLevel level;
			long creation;
			uint[] origCpuSets;
			lock (sync)
			{
				Entry value;
				if (!map.TryGetValue(pid, out value) || value.Reasons == SuppressReason.None || value.OrigPri == uint.MaxValue || !value.Journaled || !SameName(value.Name, expectedName))
				{
					error = "entry-state";
					return false;
				}
				entry = value;
				origPri = value.OrigPri;
				origAff = value.OrigAff;
				level = value.Level;
				creation = value.Creation;
				origCpuSets = value.OrigCpuSets;
			}
			IntPtr intPtr = Native.OpenProcess(12800, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				error = "open-process";
				return false;
			}
			try
			{
				string text = Native.ImageName(intPtr);
				if (text == null || !SameName(text, expectedName))
				{
					error = "identity-image";
					return false;
				}
				long creation2;
				long cpu;
				ulong io;
				if (creation > 0 && (!Native.QueryProcessSample(intPtr, out creation2, out cpu, out io) || creation2 != creation))
				{
					error = "identity-creation";
					return false;
				}
				bool flag;
				lock (sync)
				{
					Entry value2;
					if (!map.TryGetValue(pid, out value2) || value2 != entry || value2.Reasons == SuppressReason.None || !SameName(value2.Name, expectedName) || value2.Creation != creation || value2.Level != level || value2.OrigPri != origPri || value2.OrigAff != origAff || !object.ReferenceEquals(value2.OrigCpuSets, origCpuSets))
					{
						error = "entry-state";
						return false;
					}
					flag = ApplyEntryLocked(intPtr, value2, pid);
					if (!flag && TryNeutralizeUnwritableLocked(intPtr, pid, value2))
					{
						error = "self-protected";
						return false;
					}
					if (!flag && GiveUpAntiCheatLocked(intPtr, pid, value2))
					{
						error = "self-protected";
						return false;
					}
					if (!flag)
					{
						error = (string.IsNullOrEmpty(LastApplyError) ? "apply" : LastApplyError);
					}
					value2.SetApplied(flag);
					ScheduleAfterApply(value2, flag, pid);
				}
				return flag;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		public static bool HasPendingJournalFile(string path)
		{
			try
			{
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
				{
					return false;
				}
				return File.ReadAllLines(path).Length > 1;
			}
			catch
			{
				return true;
			}
		}

		private bool SaveJournalLocked(bool durable = false)
		{
			if (string.IsNullOrEmpty(journalPath))
			{
				MarkJournaledLocked();
				return true;
			}
			try
			{
				List<string> list = new List<string>();
				list.Add("PAVISE_SUPPRESSION_V1");
				foreach (KeyValuePair<int, Entry> item in map)
				{
					Entry value = item.Value;
					if (value.OrigPri != uint.MaxValue)
					{
						if (value.BackgroundJobPlacement || value.AntiCheatPlacementExternal || value.AutomaticLayoutSets != null || value.AutomaticLayoutYielded)
						{
							durable = true;
						}
						list.Add(item.Key + "|" + value.Creation + "|" + B64(value.Name) + "|" + value.OrigPri + "|" + value.OrigAff + "|" + value.OrigIo + "|" + value.OrigPg + "|" + CpuSetsText(value.OrigCpuSets) + "|" + value.OrigQoSControl + "|" + value.OrigQoSState + "|" + value.OrigGpu + "|" + value.OrigBoost + (value.AntiCheatPlacementExternal ? "|ac-placement|1|0" : ((value.AutomaticLayoutSets != null || value.AutomaticLayoutYielded) ? ("|layout|" + CpuSetsText(value.AutomaticLayoutSets) + "|" + (value.AutomaticLayoutYielded ? "1" : "0")) : (value.BackgroundPlacementOwned ? ("|" + (value.BackgroundJobPlacement ? "bg-job" : "bg") + "|" + value.BackgroundApplied + "|" + value.BackgroundPrevious) : ""))));
					}
				}
				if (list.Count == 1)
				{
					if (File.Exists(journalPath))
					{
						File.Delete(journalPath);
					}
					MarkJournaledLocked();
					return true;
				}
				string text = journalPath + ".tmp";
				using (FileStream fileStream = new FileStream(text, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					using (StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(false), 4096, true))
					{
						foreach (string item2 in list)
						{
							streamWriter.WriteLine(item2);
						}
						streamWriter.Flush();
					}
					if (durable)
					{
						fileStream.Flush(true);
					}
				}
				if (File.Exists(journalPath))
				{
					File.Replace(text, journalPath, null);
				}
				else
				{
					File.Move(text, journalPath);
				}
				MarkJournaledLocked();
				return true;
			}
			catch (Exception error)
			{
				Logger.LogFailure(Lang.T("log.suppressioncorerecovery.1"), error);
				return false;
			}
		}

		private void MarkJournaledLocked()
		{
			backgroundJobJournalPending = false;
			backgroundJobJournalFailures.Clear();
			foreach (Entry value in map.Values)
			{
				if (value.OrigPri != uint.MaxValue)
				{
					value.Journaled = true;
				}
			}
		}

		private void LoadJournal()
		{
			if (string.IsNullOrEmpty(journalPath) || !File.Exists(journalPath))
			{
				return;
			}
			try
			{
				string[] array = File.ReadAllLines(journalPath, Encoding.UTF8);
				if (array.Length == 0 || array[0] != "PAVISE_SUPPRESSION_V1")
				{
					return;
				}
				for (int i = 1; i < array.Length; i++)
				{
					int pid;
					Entry entry = ParseJournalLine(array[i], out pid);
					if (entry != null && pid > 0 && entry.OrigPri != uint.MaxValue)
					{
						entry.Journaled = true;
						SetEntryLocked(pid, entry);
					}
				}
			}
			catch (Exception error)
			{
				Logger.LogFailure(Lang.T("log.suppressioncorerecovery.2"), error);
			}
		}

		private static Entry ParseJournalLine(string line, out int pid)
		{
			pid = 0;
			string[] array = line.Split('|');
			long result;
			uint result2;
			ulong result3;
			int result4;
			int result5;
			if ((array.Length != 7 && array.Length != 8 && array.Length != 10 && array.Length != 11 && array.Length != 12 && array.Length != 15) || !int.TryParse(array[0], out pid) || !long.TryParse(array[1], out result) || !uint.TryParse(array[3], out result2) || !ulong.TryParse(array[4], out result3) || !int.TryParse(array[5], out result4) || !int.TryParse(array[6], out result5))
			{
				return null;
			}
			uint[] array2 = ((array.Length >= 8) ? ParseCpuSets(array[7]) : new uint[0]);
			if (array2 == null)
			{
				return null;
			}
			int result6 = -1;
			int result7 = -1;
			if (array.Length >= 10)
			{
				if (!int.TryParse(array[8], out result6))
				{
					result6 = -1;
				}
				if (!int.TryParse(array[9], out result7))
				{
					result7 = -1;
				}
			}
			int result8 = -1;
			if (array.Length >= 11 && !int.TryParse(array[10], out result8))
			{
				result8 = -1;
			}
			int result9 = -1;
			if (array.Length >= 12 && !int.TryParse(array[11], out result9))
			{
				result9 = -1;
			}
			if (result9 < -1 || result9 > 1)
			{
				result9 = -1;
			}
			ulong result10 = 0uL;
			ulong result11 = 0uL;
			bool flag = array.Length == 15 && array[12] == "layout";
			bool externalAcPlacement = array.Length == 15 && array[12] == "ac-placement";
			bool jobPlacement = array.Length == 15 && array[12] == "bg-job";
			uint[] array3 = null;
			bool automaticLayoutYielded = false;
			if (flag)
			{
				array3 = ParseCpuSets(array[13]);
				if (array3 == null || array3.Length > 64 || new HashSet<uint>(array3).Count != array3.Length || (array[14] != "0" && array[14] != "1") || (array3.Length == 0 && array[14] == "0") || array2.Length != 0)
				{
					return null;
				}
				automaticLayoutYielded = array[14] == "1";
				if (array3.Length == 0)
				{
					array3 = null;
				}
			}
			else if (externalAcPlacement)
			{
				if (array[13] != "1" || array[14] != "0")
				{
					return null;
				}
			}
			else if (array.Length == 15 && ((array[12] != "bg" && !jobPlacement) || !ulong.TryParse(array[13], out result10) || !ulong.TryParse(array[14], out result11)))
			{
				return null;
			}
			string text = Un64(array[2]);
			if (text == null)
			{
				return null;
			}
			Entry entry = new Entry();
			entry.Name = text;
			entry.Creation = result;
			entry.SetOriginalPriority(result2);
			entry.OrigAff = result3;
			entry.OrigIo = result4;
			entry.OrigPg = result5;
			entry.OrigCpuSets = array2;
			entry.OrigQoSControl = result6;
			entry.OrigQoSState = result7;
			entry.OrigGpu = result8;
			entry.OrigBoost = result9;
			entry.AntiCheatPlacementExternal = externalAcPlacement;
			entry.LegacyPlacementDebt = !externalAcPlacement;
			entry.BackgroundPlacementOwned = array.Length == 15 && !flag && !externalAcPlacement;
			entry.BackgroundJobPlacement = jobPlacement;
			entry.BackgroundPid = pid;
			entry.AutomaticLayoutSets = array3;
			entry.AutomaticLayoutYielded = automaticLayoutYielded;
			entry.BackgroundApplied = result10;
			entry.BackgroundPrevious = result11;
			return entry;
		}

		private static JournalIdentity IdentifyJournalEntry(IntPtr h, Entry e)
		{
			if (e.Creation <= 0)
			{
				return JournalIdentity.Unknown;
			}
			string text = Native.ImageName(h);
			if (text != null && !SameName(text, e.Name))
			{
				return JournalIdentity.Mismatch;
			}
			long creation;
			long cpu;
			ulong io;
			bool flag = Native.QueryProcessSample(h, out creation, out cpu, out io);
			if (e.Creation > 0)
			{
				if (!flag)
				{
					return JournalIdentity.Unknown;
				}
				if (creation != e.Creation)
				{
					return JournalIdentity.Mismatch;
				}
			}
			if (text != null)
			{
				return JournalIdentity.Confirmed;
			}
			return JournalIdentity.Unknown;
		}

		public static int HealFromCrash(string statePath)
		{
			if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath))
			{
				return 0;
			}
			string jobReason;
			if (!GameAffinityJobLease.Recover(out jobReason)) return 0;
			int num = 0;
			List<string> list = new List<string>();
			list.Add("PAVISE_SUPPRESSION_V1");
			try
			{
				string[] array = File.ReadAllLines(statePath, Encoding.UTF8);
				if (array.Length == 0 || array[0] != "PAVISE_SUPPRESSION_V1")
				{
					return 0;
				}
				for (int i = 1; i < array.Length; i++)
				{
					int pid;
					Entry entry = ParseJournalLine(array[i], out pid);
					if (entry == null)
					{
						list.Add(array[i]);
						Logger.Log(Lang.T("log.suppressioncorerecovery.3") + (i + 1) + Lang.T("log.suppressioncorerecovery.4"));
						continue;
					}
					IntPtr intPtr = Native.OpenProcess(12800, false, pid);
					if (intPtr == IntPtr.Zero)
					{
						IntPtr intPtr2 = Native.OpenProcess(4096, false, pid);
						if (intPtr2 != IntPtr.Zero)
						{
							try
							{
								if (IdentifyJournalEntry(intPtr2, entry) != JournalIdentity.Mismatch)
								{
									list.Add(array[i]);
								}
							}
							finally
							{
								Native.CloseHandle(intPtr2);
							}
						}
						else if (!Native.LastOpenProcessFailureWasNoSuchProcess())
						{
							list.Add(array[i]);
						}
						continue;
					}
					try
					{
						switch (IdentifyJournalEntry(intPtr, entry))
						{
						case JournalIdentity.Mismatch:
							continue;
						case JournalIdentity.Unknown:
							list.Add(array[i]);
							continue;
						}
						if (entry.BackgroundJobPlacement && !ReleaseBackgroundJob(entry))
						{
							list.Add(array[i]); continue;
						}
						if (EntrySnapshotMatches(intPtr, entry))
						{
							num++;
						}
						else if (RestoreBackgroundPlacement(intPtr, entry) && RestoreValues(intPtr, entry.OrigPri, entry.OrigAff, entry.OrigIo, entry.OrigPg, CpuTopology.AllMask, entry.OrigCpuSets, entry.OrigQoSControl, entry.OrigQoSState, entry.OrigGpu, entry.OrigBoost, RestorePlacement(entry)))
						{
							num++;
						}
						else
						{
							list.Add(array[i]);
						}
					}
					catch (Exception error)
					{
						list.Add(array[i]);
						Logger.LogFailure(Lang.T("log.suppressioncorerecovery.5") + pid, error);
					}
					finally
					{
						Native.CloseHandle(intPtr);
					}
				}
				if (list.Count == 1)
				{
					File.Delete(statePath);
				}
				else
				{
					AtomicFile.WriteLines(statePath, list.ToArray(), Lang.T("t.suppressioncorerecovery.6"));
				}
			}
			catch (Exception error2)
			{
				Logger.LogFailure(Lang.T("log.suppressioncorerecovery.2"), error2);
			}
			return num;
		}

		private static string B64(string value)
		{
			return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
		}

		private static string Un64(string value)
		{
			try
			{
				return Encoding.UTF8.GetString(Convert.FromBase64String(value));
			}
			catch
			{
				return null;
			}
		}

		private static string CpuSetsText(uint[] ids)
		{
			if (ids == null || ids.Length == 0)
			{
				return "";
			}
			string[] array = new string[ids.Length];
			for (int i = 0; i < ids.Length; i++)
			{
				array[i] = ids[i].ToString();
			}
			return string.Join(",", array);
		}

		private static uint[] ParseCpuSets(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return new uint[0];
			}
			string[] array = text.Split(',');
			uint[] array2 = new uint[array.Length];
			for (int i = 0; i < array.Length; i++)
			{
				if (!uint.TryParse(array[i], out array2[i]))
				{
					return null;
				}
			}
			return array2;
		}

		public bool Release(int pid, SuppressReason reason)
		{
			bool had;
			ReleaseOne(pid, reason, out had);
			return had;
		}

		public bool ReleaseIfCreation(int pid, SuppressReason reason, long expectedCreation)
		{
			if (expectedCreation <= 0)
			{
				return false;
			}
			bool had;
			ReleaseOne(pid, reason, expectedCreation, true, out had);
			return had;
		}

		internal BackgroundReleaseState ReleaseBackgroundForRenderer(int pid, long expectedCreation, string expectedName)
		{
			if (pid <= 0 || expectedCreation <= 0 || string.IsNullOrWhiteSpace(expectedName))
			{
				return BackgroundReleaseState.IdentityMismatch;
			}
			lock (sync)
			{
				Entry value;
				if (!map.TryGetValue(pid, out value))
				{
					return BackgroundReleaseState.Ready;
				}
				if (value.Creation != expectedCreation || !SameName(value.Name, expectedName))
				{
					return BackgroundReleaseState.IdentityMismatch;
				}
				if ((value.Reasons & SuppressReason.Background) == 0)
				{
					return RendererReleaseStateOf(value);
				}
			}
			bool had;
			RestoreResult? restoreResult;
			ReleaseOne(pid, SuppressReason.Background, expectedCreation, true, expectedName, out had, out restoreResult);
			lock (sync)
			{
				Entry value2;
				if (!map.TryGetValue(pid, out value2))
				{
					return (restoreResult == RestoreResult.Gone) ? BackgroundReleaseState.Gone : BackgroundReleaseState.Ready;
				}
				if (value2.Creation != expectedCreation || !SameName(value2.Name, expectedName))
				{
					return BackgroundReleaseState.IdentityMismatch;
				}
				return RendererReleaseStateOf(value2);
			}
		}

		private static BackgroundReleaseState RendererReleaseStateOf(Entry entry)
		{
			if ((entry.Reasons & SuppressReason.Background) != 0 || entry.BackgroundRestorePending || (entry.BackgroundPlacementOwned && (entry.Reasons & SuppressReason.AntiCheat) != 0))
			{
				return BackgroundReleaseState.Pending;
			}
			if (((uint)entry.Reasons & 0xFFFFFFFDu) != 0)
			{
				return BackgroundReleaseState.OtherReasonActive;
			}
			return BackgroundReleaseState.Pending;
		}

		internal bool DiscardReusedRendererTracking(int pid, long expectedCreation, string expectedName)
		{
			if (pid <= 0 || expectedCreation <= 0 || string.IsNullOrWhiteSpace(expectedName))
			{
				return false;
			}
			Entry value;
			long num;
			lock (sync)
			{
				map.TryGetValue(pid, out value);
				num = ((value == null) ? 0 : value.Creation);
			}
			if (!RendererDiscardIdentityMatches(pid, expectedCreation, expectedName))
			{
				return false;
			}
			lock (sync)
			{
				Entry value2;
				if (!map.TryGetValue(pid, out value2))
				{
					return true;
				}
				if (!object.ReferenceEquals(value2, value) || value2.Creation != num)
				{
					return false;
				}
				bool flag = value2.Creation > 0 && value2.Creation != expectedCreation;
				bool flag2 = value2.Creation == 0 && value2.OrigPri == uint.MaxValue && !value2.Applied && !value2.Journaled;
				if (!flag && !flag2)
				{
					return false;
				}
				RemoveEntryLocked(pid);
				batchApply.Remove(pid);
				batchApplyResults.Remove(pid);
				batchApplyErrors.Remove(pid);
				TryClearMarkLocked();
				RefreshThrottledCacheLocked();
				RefreshGroupCountsLocked();
				return true;
			}
		}

		private bool RendererDiscardIdentityMatches(int pid, long expectedCreation, string expectedName)
		{
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				string a = Native.ImageName(intPtr);
				long creation;
				long cpu;
				ulong io;
				return SameName(a, expectedName) && Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation == expectedCreation && Native.StillActive(intPtr);
			}
			catch
			{
				return false;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		public int ReleaseReason(SuppressReason reason)
		{
			int num = 0;
			List<int> list = PidsWith(reason);
			BeginJournalDefer();
			try
			{
				foreach (int item in list)
				{
					bool had;
					num += ReleaseOne(item, reason, out had);
				}
				return num;
			}
			finally
			{
				EndJournalDefer();
			}
		}

		private int ReleaseOne(int pid, SuppressReason reason, out bool had)
		{
			return ReleaseOne(pid, reason, 0L, false, out had);
		}

		private int ReleaseOne(int pid, SuppressReason reason, long expectedCreation, bool requireCreation, out bool had)
		{
			RestoreResult? restoreResult;
			return ReleaseOne(pid, reason, expectedCreation, requireCreation, null, out had, out restoreResult);
		}

		private int ReleaseOne(int pid, SuppressReason reason, long expectedCreation, bool requireCreation, string expectedName, out bool had, out RestoreResult? restoreResult)
		{
			restoreResult = null;
			bool flag = false;
			bool flag2 = false;
			Entry value;
			lock (sync)
			{
				had = map.TryGetValue(pid, out value) && (value.Reasons & reason) != 0;
				if (had && requireCreation)
				{
					had = value.Creation > 0 && value.Creation == expectedCreation;
				}
				if (had && expectedName != null)
				{
					had = SameName(value.Name, expectedName);
				}
				if (!had)
				{
					return 0;
				}
				SuppressionLevel level = value.Level;
				bool flag3 = value.SqueezeAff != 0;
				value.SetReasons(value.Reasons & ~reason);
				if ((reason & SuppressReason.AntiCheat) != 0)
				{
					value.AntiCheatLevel = SuppressionLevel.None;
				}
				if ((reason & SuppressReason.Background) != 0)
				{
					value.BackgroundReleaseDebt |= value.OrigPri != uint.MaxValue && (value.Journaled || value.Applied);
					value.BackgroundLevel = SuppressionLevel.None;
					if ((value.Reasons & ~reason & SuppressReason.AntiCheat) == 0)
					{
						value.SqueezeAff = 0uL;
					}
				}
				value.Level = EffectiveLevel(value);
				RefreshThrottledCacheLocked();
				if (value.Reasons != 0)
				{
					flag2 = true;
					flag = value.OrigPri != uint.MaxValue && value.Journaled && (level != value.Level || !value.Applied || flag3 || (reason & SuppressReason.AntiCheat) != 0);
					if (!flag) value.BackgroundReleaseDebt = false;
					PersistJournalLocked();
				}
				else
				{
					if (value.OrigPri == uint.MaxValue)
					{
						RemoveEntryLocked(pid);
						PersistJournalLocked();
						return 0;
					}
					if (!value.Journaled && !value.Applied)
					{
						RemoveEntryLocked(pid);
						batchApply.Remove(pid);
						PersistJournalLocked();
						return 0;
					}
				}
			}
			if (flag)
			{
				IntPtr intPtr = Native.OpenProcess(12800, false, pid);
				bool applied = false;
				if (intPtr != IntPtr.Zero)
				{
					try
					{
						applied = ReapplyReleasedEntry(intPtr, value, pid);
					}
					finally
					{
						Native.CloseHandle(intPtr);
					}
				}
				lock (sync)
				{
					Entry value2;
					if (map.TryGetValue(pid, out value2) && value2 == value)
					{
						value2.SetApplied(applied);
						ScheduleAfterApply(value2, applied, pid);
						RefreshThrottledCacheLocked();
					}
				}
				return 0;
			}
			if (flag2)
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return 0;
			}
			RestoreResult result;
			bool flag4 = TryRestore(pid, value, out result);
			restoreResult = result;
			if (!flag4)
			{
				return 0;
			}
			return 1;
		}

		private bool TryRestore(int pid, Entry e, bool respectBackoff)
		{
			RestoreResult result;
			return TryRestore(pid, e, respectBackoff, out result);
		}

		private bool TryRestore(int pid, Entry e, out RestoreResult result)
		{
			return TryRestore(pid, e, false, out result);
		}

		private bool TryRestore(int pid, Entry e, bool respectBackoff, out RestoreResult result)
		{
			result = RestoreResult.Protected;
			lock (sync)
			{
				Entry value;
				if (RestoreBudgetBlocked(e, "restore", false)) return false;
				if (!map.TryGetValue(pid, out value) || !object.ReferenceEquals(value, e) || e.Reasons != 0 || e.RestoreInFlight)
				{
					RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
					return false;
				}
				if (respectBackoff && DateTime.UtcNow.Ticks < e.NextRetryTicks)
				{
					RecoveryAttemptTrace.DeferredUntil(e.NextRetryTicks);
					return false;
				}
				e.RestoreInFlight = true;
			}
			try
			{
				return TryRestoreOwned(pid, e, out result);
			}
			finally
			{
				lock (sync)
				{
					e.RestoreInFlight = false;
				}
			}
		}

		private bool TryRestoreOwned(int pid, Entry e, out RestoreResult result)
		{
			RestoreResult restoreResult = (result = ((!e.GaveUpRestored) ? RestoreOne(pid, e) : RestoreResult.Restored));
			bool flag = false;
			lock (sync)
			{
				Entry value;
				if (map.TryGetValue(pid, out value) && value == e)
				{
					if (e.Reasons == SuppressReason.None)
					{
						if (restoreResult != RestoreResult.Protected)
						{
							RemoveEntryLocked(pid);
						}
					}
					else if (restoreResult == RestoreResult.Restored)
					{
						flag = true;
					}
				}
				TryClearMarkLocked();
				RefreshThrottledCacheLocked();
			}
			if (flag)
			{
				IntPtr intPtr = Native.OpenProcess(12800, false, pid);
				bool applied = false;
				if (intPtr != IntPtr.Zero)
				{
					try
					{
						applied = ReapplyReleasedEntry(intPtr, e, pid);
					}
					finally
					{
						Native.CloseHandle(intPtr);
					}
				}
				lock (sync)
				{
					Entry value2;
					if (map.TryGetValue(pid, out value2) && value2 == e)
					{
						value2.SetApplied(applied);
						ScheduleAfterApply(value2, applied, pid);
						RefreshThrottledCacheLocked();
					}
				}
				return false;
			}
			switch (restoreResult)
			{
			case RestoreResult.Protected:
				lock (sync)
				{
					Entry value3;
					if (!map.TryGetValue(pid, out value3) || value3 != e)
					{
						break;
					}
					if (e.ProtectedRetries == 0 && ShouldLogProtected(e.Name))
					{
						Logger.Log(Lang.T("log.suppressioncore.1") + e.Name + " pid " + pid + Lang.T("log.suppressioncore.2"));
					}
					if (e.ProtectedRetries < 8)
					{
						e.ProtectedRetries++;
					}
					if (e.ProtectedRetries >= 8)
					{
						e.NextRetryTicks = DateTime.MaxValue.Ticks;
						if (ShouldLogProtected(e.Name + "-parked"))
						{
							Logger.Log(Lang.T("log.suppressioncore.1") + e.Name + " pid " + pid + Lang.T("log.suppressioncore.3"));
						}
						break;
					}
					int num = 8;
					for (int i = 1; i < e.ProtectedRetries; i++)
					{
						num *= 2;
						if (num >= 300)
						{
							break;
						}
					}
					if (num > 300)
					{
						num = 300;
					}
					e.NextRetryTicks = DateTime.UtcNow.AddSeconds(num).Ticks;
				}
				break;
			case RestoreResult.Restored:
				if (e.ProtectedRetries > 0)
				{
					SuccessLogBatch.Record("restored", e.Name, pid, e.Creation,
						"previousFailures=" + e.ProtectedRetries + " group=" + e.Group);
				}
				break;
			}
			lock (sync) RecordFailureBudget(e, "restore", restoreResult != RestoreResult.Protected, e.NextRetryTicks);
			return restoreResult == RestoreResult.Restored;
		}

		public void RetryPending()
		{
			using (SuccessLogBatch.Begin("suppression-recovery cycle=" + PerformanceTuning.RecoveryCycleSerial))
				RetryPendingCore();
		}

		private void RetryPendingCore()
		{
			List<KeyValuePair<int, Entry>> list = null;
			long ticks = DateTime.UtcNow.Ticks;
			lock (sync)
			{
				foreach (KeyValuePair<int, Entry> item in map)
				{
					if (item.Value.Reasons == SuppressReason.None && !RestoreBudgetBlocked(item.Value, "restore", true) && ticks >= item.Value.NextRetryTicks)
					{
						if (list == null)
						{
							list = new List<KeyValuePair<int, Entry>>();
						}
						list.Add(item);
					}
				}
			}
			if (list == null)
			{
				return;
			}
			foreach (KeyValuePair<int, Entry> item2 in list)
			{
				if (TryRestore(item2.Key, item2.Value, true) && item2.Value.ProtectedRetries == 0)
				{
					SuccessLogBatch.Record("restored", item2.Value.Name, item2.Key, item2.Value.Creation,
						"previousFailures=0 group=" + item2.Value.Group);
				}
			}
		}

		private void TryClearMarkLocked()
		{
			PersistJournalLocked();
			if (!marked)
			{
				return;
			}
			foreach (KeyValuePair<int, Entry> item in map)
			{
				if (item.Value.OrigPri != uint.MaxValue)
				{
					return;
				}
			}
			marked = false;
			CrashGuard.ReleaseThrottle(throttleMask);
		}

		public bool SetSqueeze(int pid, long creation, string name, ulong squeezeMask, SuppressReason reason, out bool changed)
		{
			changed = false;
			if (squeezeMask != 0 && GameAffinityJobLease.IsGuardian(pid, creation)) return false;
			if (squeezeMask != 0 && reason == SuppressReason.AntiCheat)
			{
				return false;
			}
			if (squeezeMask != 0 && reason == SuppressReason.Background && (WeGameRuntimeProtection.IsProtectedRuntimeName(name) || PeripheralCatalog.IsInputAudioLike(name)))
			{
				return false;
			}
			if (reason != SuppressReason.AntiCheat && reason != SuppressReason.Background && squeezeMask != 0)
			{
				return false;
			}
			Entry value;
			ulong num;
			lock (sync)
			{
				if (!map.TryGetValue(pid, out value) || !SqueezeOwnedBy(value, reason) || value.OrigPri == uint.MaxValue || !value.Journaled || value.Creation <= 0 || value.Creation != creation || !SameName(value.Name, name))
				{
					return false;
				}
				num = ((squeezeMask == 0) ? 0 : SuppressionAffinityPolicy.SqueezeTarget(squeezeMask, value.OrigAff, value.OrigCpuSets, allMask, CpuTopology.MultiGroup));
				if (squeezeMask != 0 && num == 0)
				{
					return false;
				}
				if (num != 0 && value.AutomaticLayoutSets != null && !RestoreAutomaticLayoutLocked(pid, value))
				{
					return false;
				}
				if (reason == SuppressReason.Background)
				{
					if (num == 0 && value.SqueezeAff != 0)
					{
						value.BackgroundRestorePending = true;
						value.BackgroundPlacementVerified = false;
					}
					if (num != 0 && value.BackgroundRestorePending)
					{
						return false;
					}
				}
				if (value.SqueezeAff == num && VerifyBackgroundJob(value) && (reason != SuppressReason.Background || (!value.BackgroundRestorePending && (value.BackgroundPlacementVerified || num == 0))))
				{
					return true;
				}
				if (num != 0 && (value.SqueezeRefused || value.GaveUp))
				{
					return false;
				}
				if (num != 0 && DateTime.UtcNow.Ticks < value.SqueezeRetryAt)
				{
					return false;
				}
				if (num == 0)
				{
					if (RestoreBudgetBlocked(value, "squeeze-restore", true)) return false;
				}
			}
			IntPtr intPtr = Native.OpenProcess(12800, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				NoteSqueezeAttempt(pid, value, num, false);
				return false;
			}
			bool ok = false;
			try
			{
				if (!SameProcess(intPtr, value))
				{
					return false;
				}
				lock (sync)
				{
					Entry value2;
					if (!map.TryGetValue(pid, out value2) || value2 != value || !SqueezeOwnedBy(value2, reason) || value2.OrigPri == uint.MaxValue || value2.Creation != creation)
					{
						return false;
					}
					ulong squeezeAff = value2.SqueezeAff;
					if (num == 0)
					{
						if (RestoreBudgetBlocked(value2, "squeeze-restore", true)) return false;
					}
					if (reason == SuppressReason.Background)
					{
						if (num == 0 && !ReleaseBackgroundJob(value2)) return false;
						ulong mask;
						if (!Native.TryQueryAffinity(intPtr, out mask))
						{
							return false;
						}
						ulong num2 = ((squeezeAff != 0) ? squeezeAff : value2.OrigAff);
						if (!value2.BackgroundJobPlacement && !EnforceBackgroundAffinity && (mask != num2 || !Native.CpuSetsMatch(intPtr, value2.OrigCpuSets ?? new uint[0])))
						{
							if (squeezeAff != 0)
							{
								value2.SqueezeAff = 0uL;
								value2.SqueezeRefused = true;
								value2.BackgroundPlacementVerified = false;
							}
							value2.BackgroundRestorePending = false;
							return false;
						}
						ulong num3 = ((num != 0) ? num : value2.OrigAff);
						if (num3 == 0)
						{
							return false;
						}
						bool backgroundPlacementOwned = value2.BackgroundPlacementOwned;
						bool priorJobPlacement = value2.BackgroundJobPlacement;
						// Restore legacy affinity ownership before transferring the recovery receipt to a Job.
						bool keepProcessBackend = !priorJobPlacement && squeezeAff != 0;
						if (num != 0 && (!keepProcessBackend || mask != num3))
						{
							string admissionReason;
							if (!GameThreadAffinityAdmission.Check(intPtr, pid, creation, mask, out admissionReason))
							{
								SetBackgroundBackend(value2, "Forward affinity refused pid=" + pid + ": " + admissionReason);
								return false;
							}
						}
						ulong backgroundApplied = value2.BackgroundApplied;
						ulong backgroundPrevious = value2.BackgroundPrevious;
						value2.BackgroundPlacementOwned = true;
						value2.BackgroundPrevious = ((squeezeAff != 0) ? squeezeAff : backgroundPrevious);
						value2.BackgroundApplied = num3;
						if (num != 0 && !keepProcessBackend)
						{
							value2.BackgroundJobPlacement = true;
							value2.BackgroundJobs = backgroundAffinityJobs; value2.BackgroundPid = pid;
						}
						if (!SaveJournalLocked(true))
						{
							value2.BackgroundPlacementOwned = backgroundPlacementOwned;
							value2.BackgroundJobPlacement = priorJobPlacement;
							value2.BackgroundApplied = backgroundApplied;
							value2.BackgroundPrevious = backgroundPrevious;
							return false;
						}
						bool flag;
						if (num == 0 && value2.BackgroundJobPlacement) flag = true; // Job release already restored or preserved foreign state.
						else if (num != 0 && !keepProcessBackend)
						{
							string jobReason;
							backgroundJobsStopCompleted = false;
							flag = backgroundAffinityJobs.Ensure(pid, creation, name, value2.OrigAff, num3, out jobReason);
							if (flag)
							{
								value2.BackgroundJobActive = true;
								SetBackgroundBackend(value2, "Job affinity verified", true);
							}
							else if (GameThreadAffinityAdmission.IsRefusal(jobReason))
							{
								// A game-owned placement refusal blocks all affinity backends; retain the existing Job ownership.
								value2.BackgroundJobActive = backgroundAffinityJobs.IsActive(pid, creation);
								value2.BackgroundRestorePending = backgroundAffinityJobs.HasPending;
								value2.BackgroundPlacementVerified = false;
								SetBackgroundBackend(value2, "Job affinity refused: " + jobReason);
								return false;
							}
							else if (backgroundAffinityJobs.HasPending || backgroundAffinityJobs.IsActive(pid, creation))
							{
								value2.BackgroundRestorePending = true; value2.BackgroundPlacementVerified = false;
								SetBackgroundBackend(value2, "Job recovery pending: " + jobReason);
								return false;
							}
							else
							{
								// Persist ownership transfer before the single process-affinity fallback writes.
								ulong currentMask;
								if (!SameProcess(intPtr, value2) || !Native.TryQueryAffinity(intPtr, out currentMask) || currentMask != mask)
								{ value2.BackgroundRestorePending = true; value2.BackgroundPlacementVerified = false; return false; }
								string admissionReason;
								if (!AdmitBackgroundProcessWrite(intPtr, pid, value2, mask, num3, out currentMask, out admissionReason))
								{
									SetBackgroundBackend(value2, "Process affinity refused: " + admissionReason);
									return false;
								}
								value2.BackgroundJobPlacement = value2.BackgroundJobActive = false;
								if (!SaveJournalLocked(true)) { value2.BackgroundJobPlacement = true; value2.BackgroundRestorePending = true; return false; }
								SetBackgroundBackend(value2, "Job unavailable (" + jobReason + "); process affinity fallback");
								flag = currentMask == num3 || Native.SetProcessAffinityMask(intPtr, (UIntPtr)num3);
							}
						}
						else if (num != 0)
						{
							ulong currentMask; string admissionReason;
							if (!AdmitBackgroundProcessWrite(intPtr, pid, value2, mask, num3, out currentMask, out admissionReason))
							{
								SetBackgroundBackend(value2, "Process affinity refused: " + admissionReason);
								return false;
							}
							flag = currentMask == num3 || Native.SetProcessAffinityMask(intPtr, (UIntPtr)num3);
						}
						else flag = Native.SetProcessAffinityMask(intPtr, (UIntPtr)num3);
						bool flag2 = Native.TryQueryAffinity(intPtr, out mask);
						bool verified = flag && ((num == 0 && value2.BackgroundJobPlacement) || (flag2 && mask == num3));
						value2.BackgroundPlacementVerified = verified;
						if (verified)
						{
							value2.SqueezeAff = num;
							value2.BackgroundRestorePending = false;
							if (EnforceBackgroundAffinity)
							{
								ScheduleAfterApply(value2, true, pid);
							}
							value2.BackgroundPrevious = 0uL;
							if (!SaveJournalLocked())
							{
								value2.BackgroundPrevious = squeezeAff;
							}
							changed = squeezeAff != num;
							ok = true;
							return true;
						}
						if (num != 0 && (!flag2 || mask == num3))
						{
							value2.SqueezeAff = num;
						}
						return false;
					}
					if (value2.BackgroundPlacementOwned)
					{
						value2.BackgroundPlacementOwned = false;
						if (!SaveJournalLocked())
						{
							value2.BackgroundPlacementOwned = true;
							return false;
						}
						value2.BackgroundRestorePending = false;
					}
					value2.SqueezeAff = num;
					bool flag3 = ApplyThrottle(intPtr, value2.Level, value2.OrigPri, value2.OrigAff, value2.OrigCpuSets, DesiredGpu(value2), value2.OrigBoost, AntiCheatThrottled(value2), DesiredAffinityOf(value2));
					if (!flag3 && num != 0)
					{
						value2.SqueezeAff = 0uL;
						ulong num4 = ((value2.OrigAff != 0) ? value2.OrigAff : allMask);
						if (!CpuTopology.MultiGroup && Native.QueryAffinity(intPtr) != num4)
						{
							Native.SetProcessAffinityMask(intPtr, (UIntPtr)num4);
						}
						return false;
					}
					value2.SetApplied(flag3);
					ScheduleAfterApply(value2, flag3, pid);
					changed = squeezeAff != value2.SqueezeAff;
					ok = flag3;
					return flag3;
				}
			}
			finally
			{
				Native.CloseHandle(intPtr);
				NoteSqueezeAttempt(pid, value, num, ok);
			}
		}

		private void NoteSqueezeAttempt(int pid, Entry entry, ulong target, bool ok)
		{
			if (target == 0)
			{
				lock (sync) RecordFailureBudget(entry, "squeeze-restore", ok, DateTime.UtcNow.AddSeconds(30).Ticks);
				return;
			}
			lock (sync)
			{
				Entry value;
				if (!map.TryGetValue(pid, out value) || value != entry)
				{
					return;
				}
				if (ok)
				{
					value.SqueezeFailures = 0;
					value.SqueezeRetryAt = 0L;
					return;
				}
				value.SqueezeFailures++;
				value.SqueezeRetryAt = DateTime.UtcNow.AddSeconds(30.0).Ticks;
				if (value.SqueezeFailures >= 2)
				{
					value.SqueezeRefused = true;
				}
			}
		}

		private static bool RestoreBackgroundPlacement(IntPtr handle, Entry e)
		{
			if (e.BackgroundJobPlacement) return ReleaseBackgroundJob(e);
			if (e.AntiCheatPlacementExternal)
			{
				return true;
			}
			if (RestoreAutomaticLayoutValues(handle, e))
			{
				if (e.BackgroundPlacementOwned)
				{
					if (CrashGuard.RestoreOwnedAffinity(handle, e.OrigAff, e.BackgroundApplied))
					{
						return CrashGuard.RestoreOwnedAffinity(handle, e.OrigAff, e.BackgroundPrevious);
					}
					return false;
				}
				return true;
			}
			return false;
		}

		private static bool VerifyBackgroundJob(Entry entry)
		{
			if (!entry.BackgroundJobPlacement) return true;
			if (!entry.BackgroundJobActive) return !entry.BackgroundRestorePending;
			string reason = "job-owner-unavailable";
			if (entry.BackgroundJobs != null && entry.BackgroundJobs.Verify(entry.BackgroundPid, entry.Creation, entry.SqueezeAff, out reason)) return true;
			entry.BackgroundPlacementVerified = false; entry.BackgroundRestorePending = true;
			SetBackgroundBackend(entry, "Job recovery pending: " + reason);
			return false;
		}

		private static bool ReleaseBackgroundJob(Entry entry)
		{
			if (!entry.BackgroundJobPlacement) return true;
			string reason;
			bool released = entry.BackgroundJobs != null
				? entry.BackgroundJobs.Release(entry.BackgroundPid, entry.Creation, out reason)
				: GameAffinityJobLease.Recover(out reason) && GameAffinityJobLease.TargetRecoverySettled(entry.BackgroundPid, entry.Creation, out reason);
			if (!released)
			{
				entry.BackgroundRestorePending = true; entry.BackgroundPlacementVerified = false;
				SetBackgroundBackend(entry, "Job recovery pending: " + reason);
				return false;
			}
			entry.BackgroundJobActive = false; entry.BackgroundRestorePending = false; entry.BackgroundPlacementVerified = false;
			return true;
		}

		private static bool AdmitBackgroundProcessWrite(IntPtr process, int pid, Entry entry, ulong expected, ulong target, out ulong current, out string reason)
		{
			current = 0; reason = null;
			if (!SameProcess(process, entry) || !Native.TryQueryAffinity(process, out current) || current != expected)
			{ reason = GameThreadAffinityAdmission.RefusalPrefix + "process-changed"; return false; }
			return current == target || GameThreadAffinityAdmission.Check(process, pid, entry.Creation, current, out reason);
		}

		private static void SetBackgroundBackend(Entry entry, string detail, bool verifiedSuccess = false)
		{
			if (entry.BackgroundAffinityDetail == detail) return;
			entry.BackgroundAffinityDetail = detail;
			if (verifiedSuccess)
			{
				SuccessLogBatch.Record("background-affinity-verified", entry.Name, entry.BackgroundPid, entry.Creation, detail);
				return;
			}
			SuccessLogBatch.FlushPending();
			Logger.Info("BACKGROUND AFFINITY " + entry.Name + " pid=" + entry.BackgroundPid + " " + detail);
		}

		internal bool ReleaseAllBackgroundAffinityJobs()
		{
			lock (sync)
			{
				bool needed = backgroundAffinityJobs.HasActive || backgroundAffinityJobs.HasPending;
				foreach (Entry entry in map.Values)
					if (entry.BackgroundJobPlacement && (entry.BackgroundJobActive || entry.BackgroundRestorePending || entry.SqueezeAff != 0)) needed = true;
				if (!needed) return FlushBackgroundJobJournalLocked(false);
				string reason;
				bool ok = backgroundAffinityJobs.ReleaseAll(out reason);
				bool changed = false;
				foreach (Entry entry in map.Values)
				{
					if (!entry.BackgroundJobPlacement) continue;
					bool active = entry.BackgroundJobActive, pending = entry.BackgroundRestorePending, verified = entry.BackgroundPlacementVerified;
					ulong squeeze = entry.SqueezeAff;
					if (!ReleaseBackgroundJob(entry)) { ok = false; continue; }
					entry.SqueezeAff = 0;
					changed |= active != entry.BackgroundJobActive || pending != entry.BackgroundRestorePending || verified != entry.BackgroundPlacementVerified || squeeze != entry.SqueezeAff;
				}
				return FlushBackgroundJobJournalLocked(changed) && ok;
			}
		}

		private void DetectBackgroundPlacementChange(IntPtr handle, Entry e)
		{
			ulong mask;
			if (!EnforceBackgroundAffinity && !e.BackgroundRestorePending && e.BackgroundPlacementOwned && !e.SqueezeRefused && (e.Reasons & SuppressReason.AntiCheat) == 0 && (!Native.TryQueryAffinity(handle, out mask) || mask != ((e.SqueezeAff == 0) ? e.OrigAff : e.SqueezeAff) || !Native.CpuSetsMatch(handle, e.OrigCpuSets ?? new uint[0])))
			{
				e.SqueezeRefused = true;
				e.SqueezeAff = 0uL;
				e.BackgroundPlacementVerified = false;
			}
		}

		private static bool SqueezeOwnedBy(Entry e, SuppressReason reason)
		{
			if (e.AntiCheatPlacementExternal || reason == SuppressReason.AntiCheat)
			{
				return false;
			}
			bool flag = (e.Reasons & SuppressReason.AntiCheat) != 0;
			if (!flag)
			{
				return (e.Reasons & SuppressReason.Background) != 0;
			}
			return false;
		}

		public int ClearSqueezes(SuppressReason reason)
		{
			List<KeyValuePair<int, Entry>> list = new List<KeyValuePair<int, Entry>>();
			lock (sync)
			{
				foreach (KeyValuePair<int, Entry> item in map)
				{
					if ((item.Value.SqueezeAff != 0 || (reason == SuppressReason.Background && item.Value.BackgroundRestorePending)) && SqueezeOwnedBy(item.Value, reason))
					{
						list.Add(item);
					}
				}
			}
			int num = 0;
			foreach (KeyValuePair<int, Entry> item2 in list)
			{
				bool changed;
				SetSqueeze(item2.Key, item2.Value.Creation, item2.Value.Name, 0uL, reason, out changed);
				if (changed)
				{
					num++;
				}
			}
			return num;
		}

		public int SqueezedCount(SuppressReason reason, bool verifiedOnly = true)
		{
			int num = 0;
			lock (sync)
			{
				foreach (KeyValuePair<int, Entry> item in map)
				{
					if (verifiedOnly && reason == SuppressReason.Background) VerifyBackgroundJob(item.Value);
					if ((item.Value.SqueezeAff != 0 || (reason == SuppressReason.Background && item.Value.BackgroundRestorePending)) && SqueezeOwnedBy(item.Value, reason) && (!verifiedOnly || reason != SuppressReason.Background || (item.Value.BackgroundPlacementVerified && !item.Value.BackgroundRestorePending)))
					{
						num++;
					}
				}
				return num;
			}
		}

		public bool IsSqueezed(int pid)
		{
			lock (sync)
			{
				Entry value;
				return map.TryGetValue(pid, out value) && VerifyBackgroundJob(value) && value.SqueezeAff != 0 && !value.BackgroundRestorePending && (!value.BackgroundPlacementOwned || value.BackgroundPlacementVerified);
			}
		}
	}
}
