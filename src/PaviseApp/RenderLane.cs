using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
	internal static partial class RenderLane
	{
		private enum RestoreResult
		{
			Failed,
			Restored,
			Released
		}

		internal struct Candidate
		{
			public int Tid;

			public long ThreadCreation;

			public double Share;

			public int ThreadCount;

			public SampleFailure Failure;

			public int NativeError;
		}

		internal enum PinOutcome
		{
			Pinned,
			Retryable,
			Fatal,
			Canceled
		}

		internal enum SampleFailure
		{
			None,
			NoCpuProgress,
			NoReadableThreads,
			ProcessUnavailable,
			ProcessGone,
			IdentityChanged
		}

		internal struct ThreadSample
		{
			public long Creation;

			public long CpuTicks;
		}

		internal const double MinDominantShare = 0.35;

		private const int SampleGapMs = 800;

		private const int MaxThreads = 512;

		private const int BatchWindows = 6;

		internal const int MaxIdentifyTries = 5;

		private const int RetryGapSeconds = 60;

		private const int ReverifyGapSeconds = 30;

		private const string ThreadBirthKey = "RenderLaneThreadBirthV1";

		private static readonly object sync = new object();

		private static readonly object operationGate = new object();

		private static readonly Dictionary<int, Func<bool>> recoveryAdmissions = new Dictionary<int, Func<bool>>();

		private static bool shutdownClosed;

		private static int lanePid;

		private static long laneCreation;

		private static int laneTid;

		private static long laneThreadCreation;

		private static int laneOriginalPriority;

		private static bool laneApplied;

		private static int laneGen;

		private static int trackPid;

		private static long trackCreation;

		private static int trackGen;

		private static int trackBatch;

		private static int gaveUpPid;

		private static long gaveUpCreation;

		public static bool IsActiveFor(int pid, long creation)
		{
			lock (sync)
			{
				return laneApplied && lanePid == pid && laneCreation == creation;
			}
		}

		internal static bool TryGetAppliedThread(out int pid, out long processBirth, out int tid, out long threadBirth)
		{
			lock (sync)
			{
				pid = lanePid;
				processBirth = laneCreation;
				tid = laneTid;
				threadBirth = laneThreadCreation;
				return laneApplied;
			}
		}

		public static LaneState StateFor(int pid, long creation)
		{
			lock (sync)
			{
				if (laneApplied && lanePid == pid && laneCreation == creation)
				{
					return LaneState.Engaged;
				}
				if (stoppedPhases.Contains(LanePhase(pid, creation)))
				{
					return LaneState.Unavailable;
				}
				if (trackPid == pid && trackCreation == creation)
				{
					return LaneState.Trying;
				}
				return LaneState.Idle;
			}
		}

		public static void EnsureForGame(int pid, long creation, string gameName)
		{
			EnsureForGame(pid, creation, gameName, null);
		}

		internal static void EnsureForGame(int pid, long creation, string gameName, Func<bool> admitted)
		{
			try { if (admitted != null && !admitted()) return; }
			catch { return; }
			if (ThreadPriorityReservation.HasProcess(pid, creation)) return;
			Thread thread;
			int scheduledGeneration;
			lock (sync)
			{
				if (shutdownClosed || pid <= 0 || (laneApplied && lanePid == pid && laneCreation == creation) || stoppedPhases.Contains(LanePhase(pid, creation)) || (trackPid == pid && trackCreation == creation))
				{
					return;
				}
				laneGen++;
				int gen = laneGen;
				scheduledGeneration = gen;
				recoveryAdmissions[gen] = admitted;
				workerPhases[gen] = LanePhase(pid, creation);
				trackPid = pid;
				trackCreation = creation;
				trackGen = gen;
				trackBatch = 0;
				thread = new Thread((ThreadStart)delegate
				{
					Work(pid, creation, gameName, gen);
				});
				thread.IsBackground = true;
				thread.Name = "Pavise.RenderLane";
				thread.Priority = ThreadPriority.BelowNormal;
			}
			try { thread.Start(); }
			catch
			{
				lock (sync)
				{
					string phase;
					if (workerPhases.TryGetValue(scheduledGeneration, out phase)) stoppedPhases.Add(phase);
					recoveryAdmissions.Remove(scheduledGeneration); workerPhases.Remove(scheduledGeneration);
					if (trackGen == scheduledGeneration) { trackPid = 0; trackCreation = 0; trackGen = 0; }
				}
			}
		}

		private static bool GenAlive(int gen)
		{
			lock (sync)
			{
				return !shutdownClosed && gen == laneGen;
			}
		}

		private static bool WaitAlive(int ms, int gen)
		{
			int waitAliveValue = 0;
			while (waitAliveValue < ms)
			{
				int upperBound = Math.Min(2000, ms - waitAliveValue);
				Thread.Sleep(upperBound);
				waitAliveValue += upperBound;
				if (!GenAlive(gen))
				{
					return false;
				}
			}
			return true;
		}

		private static void Work(int pid, long creation, string gameName, int gen)
		{
			try
			{
				WorkLoop(pid, creation, gameName, gen);
			}
			catch
			{
				GiveUp(pid, creation, gen);
			}
			finally
			{
				lock (sync)
				{
					recoveryAdmissions.Remove(gen);
					workerPhases.Remove(gen);
					if (trackGen == gen)
					{
						trackPid = 0;
						trackCreation = 0L;
						trackGen = 0;
					}
				}
			}
		}

		private static void WorkLoop(int pid, long creation, string gameName, int gen)
		{
			LaneJudge laneJudge = new LaneJudge();
			int processedCount = 0;
			while (GenAlive(gen))
			{
				bool pinnedNow;
				lock (sync)
				{
					pinnedNow = laneApplied && lanePid == pid && laneCreation == creation;
				}
				if (!pinnedNow)
				{
					processedCount++;
					lock (sync)
					{
						if (trackGen == gen)
						{
							trackBatch = processedCount;
						}
					}
					bool logThis = processedCount == 1 || processedCount >= 5;
					if (!IdentifyBatch(pid, creation, gameName, gen, laneJudge, logThis, out pinnedNow))
					{
						break;
					}
					if (!pinnedNow)
					{
						if (laneJudge.GaveUp)
						{
							GiveUp(pid, creation, gen);
							break;
						}
						if (processedCount >= 5)
						{
							GiveUp(pid, creation, gen);
							break;
						}
						if (!WaitAlive(60000, gen))
						{
							break;
						}
					}
				}
				else if (!WaitAlive(30000, gen) || !VerifyOnce(pid, creation, gameName, gen, laneJudge))
				{
					break;
				}
			}
		}

		private static bool IdentifyBatch(int pid, long creation, string gameName, int gen, LaneJudge judge, bool logThis, out bool pinnedNow)
		{
			pinnedNow = false;
			int processedCount = 0;
			Candidate candidate = default(Candidate);
			Candidate status = default(Candidate);
			bool identifyBatchReady = false;
			int identifyBatchValue = 0;
			long numericValue = 0L;
			judge.ResetCandidate();
			for (int i = 0; i < 6; i++)
			{
				if (!GenAlive(gen))
				{
					return false;
				}
				Candidate best;
				double watchShare;
				if (!TryIdentifyProcess(pid, creation, 0, out best, out watchShare))
				{
					processedCount++;
					status = best;
					judge.ResetCandidate();
					if (best.Failure == SampleFailure.ProcessGone || best.Failure == SampleFailure.IdentityChanged)
					{
						if (GenAlive(gen))
						{
							Logger.Info(SampleFailureText(pid, best));
						}
						return false;
					}
					if (best.Failure != SampleFailure.NoCpuProgress)
					{
						processedCount = 6;
						break;
					}
					continue;
				}
				if (best.Tid != identifyBatchValue || best.ThreadCreation != numericValue)
				{
					judge.ResetCandidate();
				}
				identifyBatchValue = best.Tid;
				numericValue = best.ThreadCreation;
				candidate = best;
				if (best.Share < 0.35)
				{
					identifyBatchReady = true;
				}
				if (judge.Observe(best.Tid, best.Share, 0.0) == LaneDecision.Pin)
				{
					switch (TryPin(pid, creation, best, gameName, gen, logThis))
					{
					case PinOutcome.Pinned:
						pinnedNow = true;
						return true;
					case PinOutcome.Fatal:
						GiveUp(pid, creation, gen);
						return false;
					case PinOutcome.Canceled:
						return false;
					default:
						judge.PinFailed();
						break;
					}
				}
			}
			if (!GenAlive(gen))
			{
				return false;
			}
			if (logThis)
			{
				if (processedCount >= 6)
				{
					if (status.Failure == SampleFailure.NoCpuProgress)
					{
						Logger.Info(SampleFailureText(pid, status));
					}
					else
					{
						Logger.Warn(SampleFailureText(pid, status));
					}
				}
				else if (identifyBatchReady || candidate.Tid != 0)
				{
					Logger.Info(Lang.T("log.renderlane.3") + (gameName ?? "?") + Lang.T("log.renderlane.4") + (candidate.Share * 100.0).ToString("F0") + Lang.T("log.renderlane.5") + candidate.ThreadCount + Lang.T("log.renderlane.6"));
				}
			}
			return true;
		}

		private static PinOutcome TryPin(int pid, long creation, Candidate best, string gameName, int gen, bool logThis)
		{
			lock (ThreadPriorityReservation.Gate)
			return RunPinMutation(gen, () => TryPinLocked(pid, creation, best, gameName, gen, logThis));
		}

		private static PinOutcome RunGenerationMutation(int gen, Func<PinOutcome> action)
		{
			lock (operationGate)
			{
				if (!GenAlive(gen))
				{
					return PinOutcome.Canceled;
				}
				return action();
			}
		}

		private static PinOutcome RunPinMutation(int gen, Func<PinOutcome> pin)
		{
			return RunGenerationMutation(gen, delegate
			{
				if (!RestoreLaneLocked())
				{
					return PinOutcome.Retryable;
				}
				return (!GenAlive(gen)) ? PinOutcome.Canceled : pin();
			});
		}

		private static PinOutcome TryPinLocked(int pid, long creation, Candidate best, string gameName, int gen, bool logThis)
		{
			IntPtr h = Native.OpenThread(3072, false, best.Tid);
			if (h == IntPtr.Zero)
			{
				int lastWin32Error = Marshal.GetLastWin32Error();
				if (lastWin32Error == 87)
				{
					if (logThis)
					{
						Logger.Log(Lang.T("log.renderlane.changed"));
					}
					return PinOutcome.Retryable;
				}
				Logger.Log(Lang.T("log.renderlane.7") + " Win32=" + lastWin32Error);
				return PinOutcome.Fatal;
			}
			try
			{
				long creation2;
				long exit;
				long kernel;
				long user;
				if (Native.QueryThreadOwnerPid(h) != pid || !ProcessAlive(pid, creation) || !Native.GetThreadTimes(h, out creation2, out exit, out kernel, out user) || exit != 0 || creation2 != best.ThreadCreation)
				{
					if (logThis)
					{
						Logger.Log(Lang.T("log.renderlane.changed"));
					}
					return PinOutcome.Retryable;
				}
				return TryPinPriorityLocked(pid, creation, best, gameName, gen, logThis, () => Native.GetThreadPriority(h), (int priority) => Native.SetThreadPriority(h, priority));
			}
			finally
			{
				Native.CloseHandle(h);
			}
		}

		private static PinOutcome TryPinPriorityLocked(int pid, long creation, Candidate best, string gameName, int gen, bool logThis, Func<int> readPriority, Func<int, bool> setPriority)
		{
			if (!RecoveryAdmissionAllowed(gen)) return PinOutcome.Canceled;
			if (ThreadPriorityReservation.Contains(pid, creation, best.Tid, best.ThreadCreation)) return PinOutcome.Canceled;
			int readPriorityResult = readPriority();
			if (readPriorityResult == int.MaxValue)
			{
				if (logThis)
				{
					Logger.Log(Lang.T("log.renderlane.8"));
				}
				return PinOutcome.Retryable;
			}
			if (readPriorityResult >= 2)
			{
				if (logThis)
				{
					Logger.Log(Lang.T("log.renderlane.3") + (gameName ?? "?") + Lang.T("log.renderlane.9"));
				}
				return PinOutcome.Fatal;
			}
			string expected = JournalLine(pid, creation, best.Tid, readPriorityResult);
			if (!SaveJournal(pid, creation, best.Tid, best.ThreadCreation, readPriorityResult))
			{
				if (logThis)
				{
					Logger.Log(Lang.T("log.renderlane.10"));
				}
				return PinOutcome.Retryable;
			}
			BindLaneRecoveryOwner();
			if (!RecoveryAdmissionAllowed(gen))
			{
				ClearJournal(expected);
				return PinOutcome.Canceled;
			}
			if (!setPriority(2))
			{
				if (RestorePriorityVerified(readPriority, setPriority, readPriorityResult))
				{
					ClearJournal(expected);
				}
				if (logThis)
				{
					Logger.Warn(Lang.T("log.renderlane.11"));
				}
				return PinOutcome.Retryable;
			}
			int readPriorityResult2 = readPriority();
			if (readPriorityResult2 != 2)
			{
				if (RestorePriorityVerified(readPriority, setPriority, readPriorityResult))
				{
					ClearJournal(expected);
				}
				if (logThis)
				{
					Logger.Log(Lang.T("log.renderlane.12") + readPriorityResult2 + Lang.T("log.renderlane.13"));
				}
				return PinOutcome.Retryable;
			}
			bool pinPriorityLockedReady = false;
			lock (sync)
			{
				if (gen != laneGen)
				{
					pinPriorityLockedReady = true;
				}
				else
				{
					lanePid = pid;
					laneCreation = creation;
					laneTid = best.Tid;
					laneThreadCreation = best.ThreadCreation;
					laneOriginalPriority = readPriorityResult;
					laneApplied = true;
				}
			}
			if (pinPriorityLockedReady)
			{
				if (RestorePriorityVerified(readPriority, setPriority, readPriorityResult))
				{
					ClearJournal(expected);
				}
				if (logThis)
				{
					Logger.Log(Lang.T("log.renderlane.14"));
				}
				return PinOutcome.Canceled;
			}
			Logger.Log(Lang.T("log.renderlane.15") + (gameName ?? "?") + Lang.T("log.renderlane.16") + best.Tid + Lang.T("log.renderlane.17") + (best.Share * 100.0).ToString("F0") + Lang.T("log.renderlane.5") + best.ThreadCount + Lang.T("log.renderlane.18") + readPriorityResult + " " + 2);
			return PinOutcome.Pinned;
		}

		private static bool RecoveryAdmissionAllowed(int gen)
		{
			Func<bool> admitted;
			lock (sync) recoveryAdmissions.TryGetValue(gen, out admitted);
			try { return admitted == null || admitted(); }
			catch { return false; }
		}

		private static bool VerifyOnce(int pid, long creation, string gameName, int gen, LaneJudge judge)
		{
			int laneTidSnapshot;
			long threadCreation;
			int original;
			lock (sync)
			{
				if (!laneApplied || lanePid != pid || laneCreation != creation)
				{
					return true;
				}
				laneTidSnapshot = laneTid;
				threadCreation = laneThreadCreation;
				original = laneOriginalPriority;
			}
			Candidate best;
			double watchShare;
			if (!TryIdentifyProcess(pid, creation, laneTidSnapshot, out best, out watchShare))
			{
				return ProcessAlive(pid, creation);
			}
			if (judge.Observe(best.Tid, best.Share, watchShare) != LaneDecision.Unpin)
			{
				return true;
			}
			lock (operationGate)
			{
				if (!GenAlive(gen))
				{
					return false;
				}
				if (!RestoreLaneLocked())
				{
					Logger.Log(Lang.T("log.renderlane.21") + laneTidSnapshot + Lang.T("log.renderlane.22"));
					GiveUp(pid, creation, gen);
					return false;
				}
				lock (sync)
				{
					if (gen == laneGen && laneApplied && lanePid == pid && laneCreation == creation)
					{
						laneApplied = false;
						lanePid = 0;
						laneCreation = 0L;
						laneTid = 0;
						laneThreadCreation = 0L;
					}
				}
				if (!ClearJournal(JournalLine(pid, creation, laneTidSnapshot, original)))
				{
					return false;
				}
				Logger.Log(Lang.T("log.renderlane.25") + laneTidSnapshot + Lang.T("log.renderlane.26") + best.Tid + Lang.T("log.renderlane.17") + (best.Share * 100.0).ToString("F0") + "%");
				if (judge.GaveUp)
				{
					Logger.Log(Lang.T("log.renderlane.27") + (gameName ?? "?") + Lang.T("log.renderlane.28"));
					GiveUp(pid, creation, gen);
					return false;
				}
				return true;
			}
		}

		private static bool ProcessAlive(int pid, long creation)
		{
			try
			{
				using (Process process = Process.GetProcessById(pid))
				{
					return creation <= 0 || process.StartTime.ToFileTimeUtc() == creation;
				}
			}
			catch
			{
				return false;
			}
		}

		private static void GiveUp(int pid, long creation, int gen)
		{
			lock (sync)
			{
				if (!shutdownClosed && gen == laneGen)
				{
					gaveUpPid = pid;
					gaveUpCreation = creation;
					string phase;
					stoppedPhases.Add(workerPhases.TryGetValue(gen, out phase) ? phase : LanePhase(pid, creation));
				}
			}
		}

		internal static bool ReleaseExcept(int keepPid, long keepCreation)
		{
			lock (operationGate)
			{
				lock (sync)
				{
					if (keepPid > 0 && ((trackPid == keepPid && trackCreation == keepCreation) || (laneApplied && lanePid == keepPid && laneCreation == keepCreation)) && (!laneApplied || (lanePid == keepPid && laneCreation == keepCreation)))
					{
						return true;
					}
				}
				return ReleaseLocked();
			}
		}

		public static bool Release()
		{
			lock (operationGate)
			{
				return ReleaseLocked();
			}
		}

		internal static bool ReleaseReservedPriority()
		{
			lock (ThreadPriorityReservation.Gate)
			lock (operationGate)
			{
				int target;
				long created;
				int tid;
				long threadBirth;
				lock (sync)
				{
					target = lanePid;
					created = laneCreation;
					tid = laneTid;
					threadBirth = laneThreadCreation;
				}
				if (ThreadPriorityReservation.Contains(target, created, tid, threadBirth)) return ReleaseLocked();
				string raw;
				if (!Settings.TryLoadStr("RenderLane", out raw)) return false;
				if (raw.Length == 0) return true;
				int original;
				if (!ParseJournal(raw, out target, out created, out tid, out original) || !ReadThreadBirth(raw, out threadBirth)) return false;
				return !ThreadPriorityReservation.Contains(target, created, tid, threadBirth) || ReleaseLocked();
			}
		}

		internal static bool CloseForShutdown(int timeoutMs)
		{
			if (timeoutMs < 0)
			{
				return false;
			}
			lock (sync)
			{
				shutdownClosed = true;
				laneGen++;
			}
			if (Monitor.IsEntered(operationGate) || !Monitor.TryEnter(operationGate, timeoutMs))
			{
				return false;
			}
			try
			{
				return ReleaseLocked();
			}
			catch
			{
				return false;
			}
			finally
			{
				Monitor.Exit(operationGate);
			}
		}

		private static bool ReleaseLocked()
		{
			lock (sync)
			{
				laneGen++;
				trackPid = (trackGen = (trackBatch = 0));
				trackCreation = 0L;
				gaveUpPid = 0;
				gaveUpCreation = 0L;
			}
			return RestoreLaneLocked();
		}

		private static bool RestoreLaneLocked()
		{
			if (!laneApplied && !HasResidue()) { SettleLaneRecovery(); return true; }
			BindLaneRecoveryOwner();
			return RunLaneRecovery(RestoreLaneCore);
		}

		private static bool RestoreLaneCore()
		{
			bool laneAppliedSnapshot;
			int pid;
			long creation;
			int laneTidSnapshot;
			long threadCreation;
			int laneOriginalPrioritySnapshot;
			lock (sync)
			{
				laneAppliedSnapshot = laneApplied;
				pid = lanePid;
				creation = laneCreation;
				laneTidSnapshot = laneTid;
				threadCreation = laneThreadCreation;
				laneOriginalPrioritySnapshot = laneOriginalPriority;
			}
			if (!laneAppliedSnapshot)
			{
				return HealFromCrashCore();
			}
			RestoreResult restoreResult = RestoreThread(pid, creation, laneTidSnapshot, threadCreation, laneOriginalPrioritySnapshot);
			bool matchesRestoreResult = restoreResult != RestoreResult.Failed;
			if (matchesRestoreResult)
			{
				lock (sync)
				{
					laneApplied = false;
					lanePid = 0;
					laneCreation = 0L;
					laneTid = 0;
					laneThreadCreation = 0L;
				}
				matchesRestoreResult = ClearJournal(JournalLine(pid, creation, laneTidSnapshot, laneOriginalPrioritySnapshot));
				Logger.Log((restoreResult == RestoreResult.Restored) ? (Lang.T("log.renderlane.19") + laneTidSnapshot + Lang.T("log.renderlane.20") + laneOriginalPrioritySnapshot) : ReleasedLine(laneTidSnapshot));
			}
			else
			{
				Logger.Log(Lang.T("log.renderlane.21") + laneTidSnapshot + Lang.T("log.renderlane.22"));
			}
			return matchesRestoreResult;
		}

		public static bool HasResidue()
		{
			string value;
			if (Settings.TryLoadStr("RenderLane", out value))
			{
				return value.Length > 0;
			}
			return true;
		}

		public static void HealFromCrash()
		{
			lock (operationGate)
			{
				HealFromCrashLocked();
			}
		}

		private static bool HealFromCrashLocked()
		{
			return RestoreLaneLocked();
		}

		private static bool HealFromCrashCore()
		{
			string value;
			if (!Settings.TryLoadStr("RenderLane", out value))
			{
				return false;
			}
			if (value.Length == 0)
			{
				return true;
			}
			int pid;
			long creation;
			int tid;
			int original;
			long threadCreation;
			if (!ParseJournal(value, out pid, out creation, out tid, out original) || !ReadThreadBirth(value, out threadCreation))
			{
				return false;
			}
			RestoreResult restoreResult = RestoreThread(pid, creation, tid, threadCreation, original);
			if (restoreResult == RestoreResult.Failed)
			{
				return false;
			}
			bool result = ClearJournal(value);
			Logger.Log((restoreResult == RestoreResult.Restored) ? (Lang.T("log.renderlane.23") + tid + Lang.T("log.renderlane.24") + original) : ReleasedLine(tid));
			return result;
		}

		private static RestoreResult RestoreThread(int pid, long creation, int tid, long threadCreation, int original)
		{
			string value;
			if (!Settings.TryLoadStr("RenderLane", out value))
			{
				return RestoreResult.Failed;
			}
			if (pid <= 0 || creation <= 0 || tid <= 0)
			{
				return RestoreResult.Failed;
			}
			try
			{
				using (Process process = Process.GetProcessById(pid))
				{
					if (creation > 0 && process.StartTime.ToFileTimeUtc() != creation)
					{
						return RestoreResult.Released;
					}
				}
			}
			catch (ArgumentException)
			{
				return RestoreResult.Released;
			}
			catch
			{
				return RestoreResult.Failed;
			}
			IntPtr intPtr = Native.OpenThread(3072, false, tid);
			if (intPtr == IntPtr.Zero)
			{
				if (Marshal.GetLastWin32Error() != 87)
				{
					return RestoreResult.Failed;
				}
				return RestoreResult.Released;
			}
			try
			{
				int threadOwnerPid = Native.QueryThreadOwnerPid(intPtr);
				if (threadOwnerPid < 0)
				{
					return RestoreResult.Failed;
				}
				if (threadOwnerPid != pid)
				{
					return RestoreResult.Released;
				}
				if (threadCreation > 0)
				{
					long creation2;
					long exit;
					long kernel;
					long user;
					if (!Native.GetThreadTimes(intPtr, out creation2, out exit, out kernel, out user))
					{
						return RestoreResult.Failed;
					}
					if (creation2 != threadCreation || exit != 0)
					{
						return RestoreResult.Released;
					}
				}
				int threadPriority = Native.GetThreadPriority(intPtr);
				if (threadPriority == int.MaxValue)
				{
					return RestoreResult.Failed;
				}
				if (threadPriority == original)
				{
					return RestoreResult.Restored;
				}
				if (threadPriority != 2)
				{
					return RestoreResult.Released;
				}
				if (threadCreation <= 0)
				{
					return RestoreResult.Failed;
				}
				return (Native.SetThreadPriority(intPtr, original) && Native.GetThreadPriority(intPtr) == original) ? RestoreResult.Restored : RestoreResult.Failed;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private static string JournalLine(int pid, long creation, int tid, int original)
		{
			return pid + "|" + creation + "|" + tid + "|" + original;
		}

		private static bool SaveJournal(int pid, long creation, int tid, long threadCreation, int original)
		{
			string value;
			if (!Settings.TryLoadStr("RenderLane", out value) || value.Length != 0)
			{
				return false;
			}
			string journalLineResult = JournalLine(pid, creation, tid, original);
			if (SaveThreadBirth(journalLineResult, threadCreation) && Settings.SaveStr("RenderLane", journalLineResult) && Settings.TryLoadStr("RenderLane", out value))
			{
				return value == journalLineResult;
			}
			return false;
		}

		private static bool SaveThreadBirth(string line, long threadCreation)
		{
			string threadCreationOffset = ((threadCreation > 0) ? (line + "|" + threadCreation) : "");
			string value;
			if (Settings.SaveStr("RenderLaneThreadBirthV1", threadCreationOffset) && Settings.TryLoadStr("RenderLaneThreadBirthV1", out value))
			{
				return value == threadCreationOffset;
			}
			return false;
		}

		private static bool ReadThreadBirth(string line, out long threadCreation)
		{
			threadCreation = 0L;
			string value;
			if (!Settings.TryLoadStr("RenderLaneThreadBirthV1", out value))
			{
				return false;
			}
			string lineOffset = line + "|";
			long result;
			if (value.StartsWith(lineOffset, StringComparison.Ordinal) && long.TryParse(value.Substring(lineOffset.Length), out result) && result > 0)
			{
				threadCreation = result;
			}
			return true;
		}

		private static string ReleasedLine(int tid)
		{
			return Lang.T("log.renderlane.21") + tid + PerformancePolicy.Text(" 已结束或优先级已被外部改动，未写回原值", " ended or had its priority changed externally; the original value was not written back");
		}

		private static bool RestorePriorityVerified(Func<int> readPriority, Func<int, bool> setPriority, int original)
		{
			if (readPriority() != original)
			{
				if (setPriority(original))
				{
					return readPriority() == original;
				}
				return false;
			}
			return true;
		}

		private static bool ClearJournal(string expected)
		{
			string value;
			if (!Settings.TryLoadStr("RenderLane", out value))
			{
				return false;
			}
			if (value.Length == 0)
			{
				return true;
			}
			if (value != expected)
			{
				return false;
			}
			if (!Settings.SaveStr("RenderLane", "") || !Settings.TryLoadStr("RenderLane", out value) || value.Length != 0)
			{
				return false;
			}
			Settings.SaveStr("RenderLaneThreadBirthV1", "");
			return true;
		}

		internal static bool ParseJournal(string raw, out int pid, out long creation, out int tid, out int original)
		{
			pid = 0;
			creation = 0L;
			tid = 0;
			original = 0;
			if (string.IsNullOrEmpty(raw))
			{
				return false;
			}
			string[] rawParts = raw.Split('|');
			if (rawParts.Length == 4 && int.TryParse(rawParts[0], out pid) && pid > 0 && long.TryParse(rawParts[1], out creation) && int.TryParse(rawParts[2], out tid) && tid > 0)
			{
				return int.TryParse(rawParts[3], out original);
			}
			return false;
		}

		internal static bool TryIdentify(int pid, out Candidate best)
		{
			double watchShare;
			return TryIdentifyProcess(pid, 0L, 0, out best, out watchShare);
		}

		internal static bool TryIdentify(int pid, int watchTid, out Candidate best, out double watchShare)
		{
			return TryIdentifyProcess(pid, 0L, watchTid, out best, out watchShare);
		}

		internal static bool TryIdentifyProcess(int pid, long creation, int watchTid, out Candidate best, out double watchShare)
		{
			best = default(Candidate);
			watchShare = 0.0;
			Dictionary<int, ThreadSample> dictionary = new Dictionary<int, ThreadSample>();
			if (!SampleThreads(pid, creation, dictionary, out best))
			{
				return false;
			}
			Thread.Sleep(800);
			Dictionary<int, ThreadSample> dictionary2 = new Dictionary<int, ThreadSample>();
			if (!SampleThreads(pid, creation, dictionary2, out best))
			{
				return false;
			}
			return SelectCandidate(dictionary, dictionary2, watchTid, out best, out watchShare);
		}

		internal static bool SelectCandidate(Dictionary<int, ThreadSample> first, Dictionary<int, ThreadSample> second, int watchTid, out Candidate best, out double watchShare)
		{
			best = new Candidate
			{
				ThreadCount = second.Count,
				Failure = SampleFailure.NoCpuProgress
			};
			watchShare = 0.0;
			long numericValue = 0L;
			long numericValue2 = -1L;
			long numericValue3 = 0L;
			foreach (KeyValuePair<int, ThreadSample> item in second)
			{
				ThreadSample value;
				if (!first.TryGetValue(item.Key, out value) || value.Creation != item.Value.Creation)
				{
					continue;
				}
				long remainingCpuTicks = item.Value.CpuTicks - value.CpuTicks;
				if (remainingCpuTicks > 0)
				{
					numericValue += remainingCpuTicks;
					if (remainingCpuTicks > numericValue2)
					{
						numericValue2 = remainingCpuTicks;
						best.Tid = item.Key;
						best.ThreadCreation = item.Value.Creation;
					}
					if (watchTid == item.Key)
					{
						numericValue3 = remainingCpuTicks;
					}
				}
			}
			if (best.Tid == 0 || numericValue <= 0)
			{
				return false;
			}
			best.Failure = SampleFailure.None;
			best.Share = (double)numericValue2 / (double)numericValue;
			if (watchTid != 0)
			{
				watchShare = (double)numericValue3 / (double)numericValue;
			}
			return true;
		}

		private static bool SampleThreads(int pid, long creation, Dictionary<int, ThreadSample> into, out Candidate status)
		{
			status = new Candidate
			{
				Failure = SampleFailure.NoReadableThreads
			};
			try
			{
				ThreadTimes threadTimes = ProcessSnapshotSource.SampleThreads(pid, creation, 0);
				if (threadTimes != null)
				{
					for (int i = 0; i < threadTimes.Ids.Length && i < 512; i++)
					{
						if (threadTimes.Ids[i] > 0 && threadTimes.Ids[i] <= int.MaxValue && threadTimes.Created[i] > 0)
						{
							into[(int)threadTimes.Ids[i]] = new ThreadSample
							{
								Creation = threadTimes.Created[i],
								CpuTicks = threadTimes.Cpu[i]
							};
						}
					}
					return into.Count > 0;
				}
				using (Process process = Process.GetProcessById(pid))
				{
					if (creation > 0 && process.StartTime.ToFileTimeUtc() != creation)
					{
						status.Failure = SampleFailure.IdentityChanged;
						return false;
					}
					int sampleThreadsValue = 0;
					foreach (ProcessThread thread in process.Threads)
					{
						using (thread)
						{
							if (++sampleThreadsValue > 512)
							{
								break;
							}
							ReadAccessibleThread(pid, thread.Id, 0L, into, ref status);
							continue;
						}
					}
					return into.Count > 0;
				}
			}
			catch (ArgumentException)
			{
				status.Failure = SampleFailure.ProcessGone;
			}
			catch (InvalidOperationException)
			{
				status.Failure = SampleFailure.ProcessGone;
			}
			catch (Win32Exception ex3)
			{
				status.Failure = SampleFailure.ProcessUnavailable;
				status.NativeError = ex3.NativeErrorCode;
			}
			catch
			{
				status.Failure = SampleFailure.ProcessUnavailable;
			}
			return false;
		}

		private static void ReadAccessibleThread(int pid, int tid, long expectedCreation, Dictionary<int, ThreadSample> into, ref Candidate status)
		{
			IntPtr intPtr = Native.OpenThread(2048, false, tid);
			if (intPtr == IntPtr.Zero)
			{
				status.NativeError = Marshal.GetLastWin32Error();
				return;
			}
			try
			{
				if (Native.QueryThreadOwnerPid(intPtr) == pid)
				{
					long creation;
					long exit;
					long kernel;
					long user;
					if (!Native.GetThreadTimes(intPtr, out creation, out exit, out kernel, out user))
					{
						status.NativeError = Marshal.GetLastWin32Error();
					}
					else if (expectedCreation <= 0 || creation == expectedCreation)
					{
						into[tid] = new ThreadSample
						{
							Creation = creation,
							CpuTicks = kernel + user
						};
					}
				}
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		private static string SampleFailureText(int pid, Candidate status)
		{
			string message = ((status.Failure == SampleFailure.NoCpuProgress) ? "idle" : ((status.Failure == SampleFailure.ProcessGone) ? "exited" : ((status.Failure == SampleFailure.IdentityChanged) ? "identity" : ((status.Failure == SampleFailure.ProcessUnavailable) ? "process" : "threads"))));
			return Lang.T("log.renderlane.sample." + message) + " PID " + pid + ((status.NativeError == 0) ? "" : (" Win32=" + status.NativeError));
		}
	}
}
