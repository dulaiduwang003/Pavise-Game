using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
	internal static class FrameCoordination
	{
		private sealed class OriginalCriticalRoles
		{
			internal readonly int Epoch;

			internal readonly int Pid;

			internal readonly long Birth;

			internal readonly long Observed;

			internal readonly CriticalThreadRoleFact[] Facts;

			internal OriginalCriticalRoles(int epoch, int pid, long birth, long observed, CriticalThreadRoleFact[] facts)
			{
				Epoch = epoch;
				Pid = pid;
				Birth = birth;
				Observed = observed;
				Facts = facts;
			}
		}

		internal const string ActionKey = "FrameCoordination";

		internal const string JournalKey = "FrameCoordinationJournalV1";

		// TimeCritical is relative to the process class; a High process still maps this to base priority 15.
		internal const int PresentThreadPriority = (int)ThreadPriorityLevel.TimeCritical;

		internal const int LeaseCheckMs = 5000;

		internal const int ObserveGapMs = 30000;

		internal const string CaptureSessionKey = "FrameCoordinationCaptureSessionsV1";

		internal const int MaxCaptureFailures = 5;

		internal const int MaxCaptureSessions = 64;

		private static readonly object sync = new object();

		private static readonly object mutation = new object();

		private static Thread worker;

		private static int generation;

		private static int priorityReleaseVersion;

		private static int pid;

		private static long birth;

		private static bool closed;

		private static volatile int state;

		private static IFrameThreads platform = new NativeFrameThreads();

		[ThreadStatic]
		private static string applyExecutionFailure;

		private static bool observing;

		private static readonly object captureSessionGate = new object();

		private static readonly Dictionary<int, FrameCaptureSession> captureSessions = new Dictionary<int, FrameCaptureSession>();
		private static readonly Dictionary<string, Dictionary<int, FrameCaptureSession>> capturePhases = new Dictionary<string, Dictionary<int, FrameCaptureSession>>(StringComparer.Ordinal);
		private static string capturePhase;

		private static bool captureSessionsLoaded;

		private static bool captureSessionStoreFailed;

		private static OriginalCriticalRoles criticalRoles;

		internal static int State
		{
			get
			{
				return state;
			}
		}

		internal static bool HasResidue
		{
			get
			{
				string value;
				if (Settings.TryLoadStr("FrameCoordinationJournalV1", out value) && value.Length <= 0 && !FrameMemoryProtection.HasResidue)
				{
					return FrameCoordinationTrace.HasResidue;
				}
				return true;
			}
		}

		internal static bool Observing
		{
			get
			{
				lock (sync)
				{
					return observing && worker != null && worker.IsAlive;
				}
			}
		}

		internal static void Ensure(int target, long created, string name, Func<bool> admitted)
		{
			if (target <= 0 || created <= 0 || admitted == null || !admitted() || ThreadPriorityReservation.HasProcess(target, created))
			{
				return;
			}
			ActionFailureStore.BeginAutomaticCycle(ActionKey, name, FrameRecoveryBudget.Cycle(target, created));
			lock (sync)
			{
				if (closed)
				{
					return;
				}
				if (worker != null && worker.IsAlive)
				{
					if (pid != target || birth != created || (observing && !GameManagedLocked(target, created, name)))
					{
						Interlocked.Increment(ref generation);
					}
					return;
				}
				if (ActionFailureStore.IsBlocked("FrameCoordination", name))
				{
					state = 4;
					return;
				}
				FrameCaptureSession session = CaptureSession(target, created);
				if ((session == null || session.Stopped) && !HasResidue)
				{
					state = ((session == null) ? 3 : ((!session.NoChangeNeeded) ? 4 : 0));
					return;
				}
				pid = target;
				birth = created;
				observing = false;
				int epoch = NextEpochLocked(target, created);
				worker = new Thread((ThreadStart)delegate
				{
					Run(target, created, name, epoch, admitted, session);
				});
				worker.IsBackground = true;
				worker.Name = "Pavise.FrameCoordination";
				worker.Priority = ThreadPriority.BelowNormal;
				worker.Start();
			}
		}

		private static bool GameManagedLocked(int target, long created, string name)
		{
			if (ActionFailureStore.IsBlocked("FrameCoordination", name))
			{
				return false;
			}
			FrameCaptureSession frameCaptureSession = CaptureSession(target, created);
			if (frameCaptureSession != null && frameCaptureSession.Stopped)
			{
				return frameCaptureSession.NoChangeNeeded;
			}
			return false;
		}

		private static bool Current(int epoch, Func<bool> admitted)
		{
			if (!closed && Volatile.Read(ref generation) == epoch)
			{
				return admitted();
			}
			return false;
		}

		private static bool Wait(int ms, Func<bool> alive)
		{
			for (int i = 0; i < ms; i += 200)
			{
				if (!alive())
				{
					return false;
				}
				Thread.Sleep(Math.Min(200, ms - i));
			}
			return alive();
		}

		internal static string WaitingLine(string name, FrameCoordinationSample sample)
		{
			string message = "WARN ";
			if (sample != null && sample.Valid && sample.NoChangeNeeded)
			{
				message = "INFO ";
			}
			if (sample != null && !sample.Valid)
			{
				if (sample.Issue == FrameCaptureIssue.Canceled || sample.Issue == FrameCaptureIssue.TargetChanged || sample.Issue == FrameCaptureIssue.InsufficientEvidence)
				{
					message = "INFO ";
				}
				else if (sample.Issue == FrameCaptureIssue.TraceStopFailed || sample.Issue == FrameCaptureIssue.DecodeFailed || sample.Issue == FrameCaptureIssue.CaptureFailed)
				{
					message = "FAIL ";
				}
			}
			string textResult = ((sample == null) ? PerformancePolicy.Text("采样暂不可用", "Capture is unavailable") : ((!sample.Valid) ? sample.Detail : (sample.NoChangeNeeded ? PerformancePolicy.Text("目标线程优先级由游戏控制，无需重复调整", "Target thread priorities are game-managed; no change needed") : PerformancePolicy.Text("目标线程读取或优先级写入受限，保持原调度", "Thread access or priority writes are blocked; original scheduling retained"))));
			string concatResult = ((sample == null) ? "" : string.Concat(" [reason=", sample.Issue, " traceCode=", sample.TraceCode, " lostEvents=", sample.LostEvents, " eventCount=", sample.Events, " sampleCpuMs=", sample.CpuMs.ToString("F1", CultureInfo.InvariantCulture), " systemEvents=", sample.SystemEvents, "]"));
			return message + "FRAME " + PerformancePolicy.Text("线程协同待命：", "Coordination waiting: ") + name + " | " + textResult + concatResult;
		}

		private static void Run(int target, long created, string name, int epoch, Func<bool> admitted, FrameCaptureSession session)
		{
			Func<bool> func = () => Current(epoch, admitted);
			Stopwatch stopwatch = Stopwatch.StartNew();
			try
			{
				while (func())
				{
					bool restoreSucceeded = Restore();
					if (!(restoreSucceeded & FrameCoordinationTrace.Heal()))
					{
						state = 3;
						if (!Wait(30000, func))
						{
							break;
						}
						continue;
					}
					if (session == null || session.Stopped || CaptureProcessState(target, created) > 0)
					{
						break;
					}
					state = 1;
					bool presenterOnly = session.PresenterOnly;
					FrameCoordinationSample frameCoordinationSample = TakeCapture(target, created, func, presenterOnly);
					if (!func())
					{
						if (frameCoordinationSample != null && !frameCoordinationSample.Valid && frameCoordinationSample.Issue != FrameCaptureIssue.Canceled && frameCoordinationSample.Issue != FrameCaptureIssue.TargetChanged)
						{
							ObserveCapture(session, frameCoordinationSample, false, stopwatch.ElapsedMilliseconds);
							if (frameCoordinationSample.Issue == FrameCaptureIssue.TraceStopFailed || frameCoordinationSample.Issue == FrameCaptureIssue.DecodeFailed || frameCoordinationSample.Issue == FrameCaptureIssue.CaptureFailed)
							{
								Logger.Log(WaitingLine(name, frameCoordinationSample));
							}
						}
						break;
					}
					if (frameCoordinationSample != null && (frameCoordinationSample.Issue == FrameCaptureIssue.Canceled || frameCoordinationSample.Issue == FrameCaptureIssue.TargetChanged))
					{
						break;
					}
					if (frameCoordinationSample == null || frameCoordinationSample.Issue == FrameCaptureIssue.CaptureFailed || frameCoordinationSample.Issue == FrameCaptureIssue.DecodeFailed || frameCoordinationSample.Issue == FrameCaptureIssue.TraceStopFailed)
					{
						ActionFailureStore.Trip("FrameCoordination", name, (frameCoordinationSample == null) ? "Thread capture returned no result" : frameCoordinationSample.Detail);
					}
					if (frameCoordinationSample != null && frameCoordinationSample.Valid)
					{
						PublishCriticalRoles(frameCoordinationSample, epoch);
					}
					int appliedReleaseVersion;
					bool matchesApplyIsolated;
					lock (ThreadPriorityReservation.Gate)
					{
						appliedReleaseVersion = Volatile.Read(ref priorityReleaseVersion);
						matchesApplyIsolated = frameCoordinationSample != null && frameCoordinationSample.Valid && ApplyIsolated(frameCoordinationSample, func, name);
					}
					long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
					ObserveCapture(session, frameCoordinationSample, matchesApplyIsolated, elapsedMilliseconds);
					if (!matchesApplyIsolated && frameCoordinationSample != null && frameCoordinationSample.PriorityReserved)
					{
						break;
					}
					if (!matchesApplyIsolated && ActionFailureStore.IsBlocked("FrameCoordination", name))
					{
						session.Stopped = true;
						SaveCaptureSessions();
						state = 4;
						break;
					}
					if (!matchesApplyIsolated)
					{
						state = ((frameCoordinationSample == null || !frameCoordinationSample.NoChangeNeeded) ? 3 : 0);
						if (session.Stopped)
						{
							string waitingLineResult = WaitingLine(name, frameCoordinationSample);
							if (!waitingLineResult.StartsWith("FAIL ", StringComparison.Ordinal))
							{
								waitingLineResult = "INFO " + waitingLineResult.Substring(waitingLineResult.IndexOf("FRAME", StringComparison.Ordinal));
							}
							Logger.Log(waitingLineResult + " | " + PerformancePolicy.Text("本阶段停止重复采样", "Further capture stopped for this phase") + " captureFailures=" + session.Failures + "/" + MaxCaptureFailures);
							break;
						}
						if (!presenterOnly && session.PresenterOnly)
						{
							Logger.Info("FRAME " + name + " | " + PerformancePolicy.Text("调度采样受限，改为仅识别画面提交线程", "Scheduling capture limited; switching to presenter-only capture"));
							continue;
						}
						if (session.Retry.ShouldLog)
						{
							Logger.Log(WaitingLine(name, frameCoordinationSample) + " retryMs=" + session.Retry.DelayMs + " captureFailures=" + session.Failures + "/" + MaxCaptureFailures);
						}
						if (!Wait(session.Retry.DelayMs, func))
						{
							break;
						}
						continue;
					}
					if (session.Stopped)
					{
						break;
					}
					state = 2;
					Logger.Log("PASS FRAME " + ((Lang.Cur == 1) ? "Coordination active: " : "线程协同已执行：") + name + " pid=" + target + " present=" + frameCoordinationSample.PresentTid + " worker=" + frameCoordinationSample.WorkerTid + " sampleCpuMs=" + frameCoordinationSample.CpuMs.ToString("F1", CultureInfo.InvariantCulture));
					// A healthy lease needs no periodic restore, ETW capture, or reapply.
					while (Wait(LeaseCheckMs, func))
					{
						bool healthy;
						bool priorityReleased;
						lock (ThreadPriorityReservation.Gate)
						{
							healthy = LeaseHealthy();
							int releaseVersion = Volatile.Read(ref priorityReleaseVersion);
							priorityReleased = releaseVersion != appliedReleaseVersion && LeaseReturnedByReservation();
							if (healthy) appliedReleaseVersion = releaseVersion;
						}
						if (!healthy)
						{
							if (!priorityReleased)
							{
								YieldBoostToGame(session);
								Logger.Info("FRAME " + name + " | " + PerformancePolicy.Text("线程状态已变化；恢复自有设置并停止本进程重复提优", "Thread state changed; owned settings restored and repeated boosting stopped for this process"));
							}
							break;
						}
					}
					break;
				}
			}
			catch (Exception ex)
			{
				state = 3;
				if (session != null)
				{
					ObserveCapture(session, new FrameCoordinationSample
					{
						Pid = target,
						Birth = created
					}.Reject(FrameCaptureIssue.CaptureFailed), false, stopwatch.ElapsedMilliseconds);
					if (func())
					{
						ActionFailureStore.Trip("FrameCoordination", name, "Thread capture execution failed: " + ex.Message);
						session.Stopped = true;
						SaveCaptureSessions();
					}
				}
				Logger.Error("FRAME " + ex.Message);
			}
			finally
			{
				Restore();
				FrameCoordinationTrace.Heal();
				if (Volatile.Read(ref generation) == epoch)
				{
					state = ((session != null && session.Stopped && !session.NoChangeNeeded) ? 4 : 0);
				}
			}
		}

		internal static bool Apply(FrameCoordinationSample sample, Func<bool> alive)
		{
			lock (ThreadPriorityReservation.Gate)
			lock (mutation)
			{
				applyExecutionFailure = null;
				if (sample != null)
				{
					sample.NoChangeNeeded = false;
					sample.PriorityReserved = false;
				}
				if (sample == null || !sample.Valid || sample.Pid <= 0 || sample.Birth <= 0 || sample.PresentTid <= 0 || !sample.Threads.ContainsKey(sample.PresentTid) || sample.Threads[sample.PresentTid] <= 0 || !alive())
				{
					return false;
				}
				bool available = false;
				foreach (int tid in new int[] { sample.PresentTid, sample.WorkerTid })
				{
					long threadBirth;
					if (tid > 0 && sample.Threads.TryGetValue(tid, out threadBirth) && threadBirth > 0
						&& !ThreadPriorityReservation.Contains(sample.Pid, sample.Birth, tid, threadBirth)) available = true;
				}
				if (!available)
				{
					sample.PriorityReserved = true;
					return false;
				}
				if (!RestoreLocked()) return false;
				List<FrameThreadReceipt> list = new List<FrameThreadReceipt>();
				int[] integerValues = new int[2] { sample.PresentTid, sample.WorkerTid };
				foreach (int numericValueEntry in integerValues)
				{
					long value;
					if ((numericValueEntry == sample.WorkerTid && numericValueEntry == sample.PresentTid && list.Count > 0) || numericValueEntry <= 0 || !sample.Threads.TryGetValue(numericValueEntry, out value))
					{
						continue;
					}
					if (ThreadPriorityReservation.Contains(sample.Pid, sample.Birth, numericValueEntry, value))
					{
						sample.PriorityReserved = true;
						continue;
					}
					FrameThreadReceipt frameThreadReceipt = new FrameThreadReceipt();
					frameThreadReceipt.Pid = sample.Pid;
					frameThreadReceipt.ProcessBirth = sample.Birth;
					frameThreadReceipt.Tid = numericValueEntry;
					frameThreadReceipt.ThreadBirth = value;
					frameThreadReceipt.Applied = numericValueEntry == sample.PresentTid ? PresentThreadPriority : 1;
					FrameThreadReceipt frameThreadReceipt2 = frameThreadReceipt;
					int priority;
					int readResult = platform.Read(frameThreadReceipt2, out priority);
					if (readResult != 0)
					{
						if (readResult < 0)
						{
							applyExecutionFailure = "Thread original state could not be read";
						}
						return false;
					}
					if (priority >= -2 && priority < 1)
					{
						frameThreadReceipt2.Original = priority;
						list.Add(frameThreadReceipt2);
					}
				}
				if (list.Count == 0)
				{
					sample.NoChangeNeeded = !sample.PriorityReserved;
					return false;
				}
				string encodeResult = Encode(list);
				if (!alive())
				{
					return false;
				}
				string value2;
				if (!Settings.SaveStrDurable("FrameCoordinationJournalV1", encodeResult) || !Settings.TryLoadStr("FrameCoordinationJournalV1", out value2) || value2 != encodeResult)
				{
					applyExecutionFailure = "Thread recovery record could not be saved or verified";
					return false;
				}
				FrameRecoveryBudget.Own("priority", sample.Pid, sample.Birth);
				foreach (FrameThreadReceipt item in list)
				{
					if (!alive())
					{
						RestoreLocked();
						return false;
					}
					if (!platform.Set(item, item.Original, item.Applied))
					{
						int priority2;
						int readResult2 = platform.Read(item, out priority2);
						if (readResult2 <= 0 && (readResult2 < 0 || priority2 == item.Original || priority2 == item.Applied))
						{
							applyExecutionFailure = "Thread priority apply or readback failed";
						}
						RestoreLocked();
						return false;
					}
				}
				if (!FrameMemoryProtection.Apply(list, platform as IFrameThreadMemory, alive))
				{
					applyExecutionFailure = FrameMemoryProtection.ExecutionFailure;
					if (!FrameMemoryProtection.Restore())
					{
						applyExecutionFailure = "Thread memory protection could not be restored";
					}
					RestoreLocked();
					return false;
				}
				if (!alive())
				{
					RestoreLocked();
					return false;
				}
				return true;
			}
		}

		internal static bool ApplyIsolated(FrameCoordinationSample sample, Func<bool> alive, string target)
		{
			if (sample == null || !sample.Valid || alive == null || !alive() || string.IsNullOrEmpty(target))
			{
				return false;
			}
			if (HasResidue && !Restore())
			{
				return false;
			}
			string reason;
			if (!ActionFailureStore.TryBeginApply("FrameCoordination", target, out reason))
			{
				return false;
			}
			bool applyIsolatedReady = false;
			string applyExecutionFailureSnapshot = null;
			try
			{
				applyIsolatedReady = Apply(sample, alive);
				applyExecutionFailureSnapshot = applyExecutionFailure;
			}
			catch (Exception ex)
			{
				applyExecutionFailureSnapshot = ex.Message;
			}
			if (applyIsolatedReady && ActionFailureStore.CompleteApply("FrameCoordination", target))
			{
				return true;
			}
			if (applyIsolatedReady)
			{
				applyExecutionFailureSnapshot = "Thread action completion could not be saved";
			}
			if (applyExecutionFailureSnapshot == null)
			{
				if (Restore())
				{
					if (sample.NoChangeNeeded || sample.PriorityReserved) ActionFailureStore.CompleteApply("FrameCoordination", target);
					else ActionFailureStore.CancelApply("FrameCoordination", target);
				}
				else
				{
					ActionFailureStore.Trip("FrameCoordination", target, "Thread restoration is incomplete; new applications stopped");
				}
			}
			else
			{
				ActionFailureStore.Trip("FrameCoordination", target, applyExecutionFailureSnapshot);
				Restore();
			}
			return false;
		}

		private static string Encode(List<FrameThreadReceipt> receipts)
		{
			List<string> list = new List<string>();
			list.Add("v2");
			List<string> list2 = list;
			foreach (FrameThreadReceipt receipt in receipts)
			{
				list2.Add(receipt.Encode() + "|" + receipt.Applied);
			}
			return string.Join(";", list2.ToArray());
		}

		internal static bool Parse(string text, out List<FrameThreadReceipt> result)
		{
			result = new List<FrameThreadReceipt>();
			if (text == "")
			{
				return true;
			}
			string[] parts = text.Split(';');
			if (parts.Length < 2 || parts.Length > 3 || (parts[0] != "v1" && parts[0] != "v2"))
			{
				return false;
			}
			bool legacy = parts[0] == "v1";
			HashSet<int> hashSet = new HashSet<int>();
			for (int i = 1; i < parts.Length; i++)
			{
				string[] parts2 = parts[i].Split('|');
				FrameThreadReceipt frameThreadReceipt = new FrameThreadReceipt();
				if (parts2.Length != (legacy ? 5 : 6) || !int.TryParse(parts2[0], out frameThreadReceipt.Pid) || !long.TryParse(parts2[1], out frameThreadReceipt.ProcessBirth) || !int.TryParse(parts2[2], out frameThreadReceipt.Tid) || !long.TryParse(parts2[3], out frameThreadReceipt.ThreadBirth) || !int.TryParse(parts2[4], out frameThreadReceipt.Original) || (!legacy && (!int.TryParse(parts2[5], out frameThreadReceipt.Applied) || (frameThreadReceipt.Applied != 1 && frameThreadReceipt.Applied != PresentThreadPriority))) || frameThreadReceipt.Pid <= 0 || frameThreadReceipt.Tid <= 0 || frameThreadReceipt.ProcessBirth <= 0 || frameThreadReceipt.ThreadBirth <= 0 || frameThreadReceipt.Original < -2 || frameThreadReceipt.Original > 0 || !hashSet.Add(frameThreadReceipt.Tid))
				{
					return false;
				}
				if (result.Count > 0 && (frameThreadReceipt.Pid != result[0].Pid || frameThreadReceipt.ProcessBirth != result[0].ProcessBirth))
				{
					return false;
				}
				result.Add(frameThreadReceipt);
			}
			return true;
		}

		private static bool LeaseHealthy()
		{
			lock (mutation)
			{
				string value;
				List<FrameThreadReceipt> result;
				if (!Settings.TryLoadStr("FrameCoordinationJournalV1", out value) || value.Length == 0 || !Parse(value, out result))
				{
					return false;
				}
				foreach (FrameThreadReceipt item in result)
				{
					int priority;
					if (platform.Read(item, out priority) != 0 || priority != item.Applied)
					{
						return false;
					}
				}
				return FrameMemoryProtection.Healthy();
			}
		}

		internal static bool Restore()
		{
			lock (mutation)
			{
				return RestoreLocked();
			}
		}

		internal static void SyncAutomaticPhase(long gameSerial, int target, long created, string materialConfig)
		{
			if (!FrameRecoveryBudget.Sync(gameSerial, target, created, materialConfig)) return;
			string phase = FrameRecoveryBudget.Cycle(target, created);
			lock (captureSessionGate)
			{
				if (phase == capturePhase) return;
				if (capturePhase != null)
				{
					capturePhases[capturePhase] = new Dictionary<int, FrameCaptureSession>(captureSessions);
					captureSessions.Clear();
					Dictionary<int, FrameCaptureSession> previous;
					if (capturePhases.TryGetValue(phase, out previous))
						foreach (var row in previous) captureSessions[row.Key] = row.Value;
					captureSessionsLoaded = true;
				}
				capturePhase = phase;
			}
		}

		private static bool LeaseReturnedByReservation()
		{
			lock (mutation)
			{
				string raw;
				List<FrameThreadReceipt> rows;
				if (!Settings.TryLoadStr(JournalKey, out raw) || !Parse(raw, out rows)) return false;
				foreach (FrameThreadReceipt row in rows)
				{
					if (ThreadPriorityReservation.Contains(row.Pid, row.ProcessBirth, row.Tid, row.ThreadBirth)) continue;
					int priority;
					if (platform.Read(row, out priority) != 0 || priority != row.Applied) return false;
				}
				// A partial return must not hide a different thread's ownership change
				// in the same five-second interval.
				return FrameMemoryProtection.HealthyOutsideReservation();
			}
		}

		internal static bool ReleaseReservedPriority()
		{
			if (!HasResidue) return true;
			return FrameRecoveryBudget.Run("priority", ReleaseReservedPriorityCore,
				delegate { string raw; return Settings.TryLoadStr(JournalKey, out raw) && raw.Length == 0; });
		}

		private static bool ReleaseReservedPriorityCore()
		{
			lock (ThreadPriorityReservation.Gate)
			lock (mutation)
			{
				string raw;
				List<FrameThreadReceipt> rows;
				if (!Settings.TryLoadStr(JournalKey, out raw) || !Parse(raw, out rows)) return false;
				List<FrameThreadReceipt> remaining = new List<FrameThreadReceipt>();
				bool matched = false;
				foreach (FrameThreadReceipt row in rows)
				{
					if (!ThreadPriorityReservation.Contains(row.Pid, row.ProcessBirth, row.Tid, row.ThreadBirth))
					{
						remaining.Add(row);
						continue;
					}
					if (!matched) Interlocked.Increment(ref priorityReleaseVersion);
					matched = true;
					int priority;
					int read = platform.Read(row, out priority);
					if (read < 0 || (read == 0 && priority == row.Applied && !platform.Set(row, row.Applied, row.Original))) return false;
				}
				if (!FrameMemoryProtection.ReleaseReservedPriority()) return false;
				if (!matched) return true;
				string next = remaining.Count == 0 ? "" : Encode(remaining);
				string check;
				return Settings.TryLoadStr(JournalKey, out check) && check == raw && Settings.SaveStrDurable(JournalKey, next)
					&& Settings.TryLoadStr(JournalKey, out check) && check == next;
			}
		}

		private static void YieldBoostToGame(FrameCaptureSession session)
		{
			lock (captureSessionGate)
			{
				// This is an ownership change, not identification exhaustion. Observer-only
				// capture remains available and candidate fallback must not fight it.
				session.Stopped = true;
				session.NoChangeNeeded = true;
				session.Failures = 0;
				SaveCaptureSessions();
			}
		}

		private static bool RestoreLocked()
		{
			FrameRecoveryBudget.ObserveExit(CaptureProcessState);
			bool restoreSucceeded = FrameMemoryProtection.Restore();
			string raw;
			if (Settings.TryLoadStr(JournalKey, out raw) && raw.Length == 0) return restoreSucceeded;
			return FrameRecoveryBudget.Run("priority", RestorePriorityCore) && restoreSucceeded;
		}

		private static bool RestorePriorityCore()
		{
			string value;
			List<FrameThreadReceipt> result;
			if (!Settings.TryLoadStr("FrameCoordinationJournalV1", out value) || !Parse(value, out result))
			{
				return false;
			}
			if (value.Length == 0)
			{
				return true;
			}
			bool restorePriorityCoreReady = true;
			foreach (FrameThreadReceipt item in result)
			{
				int priority;
				int readResult = platform.Read(item, out priority);
				if (readResult < 0)
				{
					restorePriorityCoreReady = false;
				}
				else if (readResult == 0 && priority == item.Applied && !platform.Set(item, item.Applied, item.Original))
				{
					restorePriorityCoreReady = false;
				}
			}
			string value2;
			bool allConditionsMet = restorePriorityCoreReady && Settings.TryLoadStr("FrameCoordinationJournalV1", out value2) && value2 == value && Settings.SaveStr("FrameCoordinationJournalV1", "") && Settings.TryLoadStr("FrameCoordinationJournalV1", out value2) && value2.Length == 0;
			return allConditionsMet;
		}

		internal static bool ReleaseExcept(int keepPid, long keepBirth)
		{
			lock (sync)
			{
				List<FrameThreadReceipt> result = null;
				string value;
				bool matchesParse = keepPid > 0 && pid == keepPid && birth == keepBirth && Settings.TryLoadStr("FrameCoordinationJournalV1", out value) && Parse(value, out result);
				if (matchesParse)
				{
					foreach (FrameThreadReceipt item in result)
					{
						if (item.Pid != keepPid || item.ProcessBirth != keepBirth)
						{
							matchesParse = false;
						}
					}
					if (matchesParse)
					{
						return true;
					}
				}
				Interlocked.Increment(ref generation);
				state = 0;
			}
			bool restoreSucceeded = Restore();
			return restoreSucceeded & FrameCoordinationTrace.Heal();
		}

		internal static bool Close(int timeout)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			Thread thread;
			lock (sync)
			{
				closed = true;
				Interlocked.Increment(ref generation);
				thread = worker;
			}
			bool result = FrameCoordinationTrace.CloseIdentity(timeout);
			int millisecondsTimeout = Math.Max(0, timeout - (int)Math.Min(2147483647L, stopwatch.ElapsedMilliseconds));
			if (thread != null && thread.IsAlive && (thread == Thread.CurrentThread || !thread.Join(millisecondsTimeout)))
			{
				return false;
			}
			bool restoreSucceeded = Restore();
			if (restoreSucceeded & FrameCoordinationTrace.Heal())
			{
				return result;
			}
			return false;
		}

		internal static bool HealFromCrash()
		{
			return FrameCoordinationTrace.Heal() & Restore();
		}

		internal static void EnsureObserver(int target, long created, string name, Func<bool> admitted)
		{
			if (target <= 0 || created <= 0 || admitted == null || !admitted())
			{
				return;
			}
			ActionFailureStore.BeginAutomaticCycle(ActionKey, name, FrameRecoveryBudget.Cycle(target, created));
			lock (sync)
			{
				if (closed)
				{
					return;
				}
				if (worker != null && worker.IsAlive)
				{
					if (observing && (pid != target || birth != created))
					{
						Interlocked.Increment(ref generation);
					}
					return;
				}
				if (ActionFailureStore.IsBlocked("FrameCoordination", name))
				{
					state = 4;
					return;
				}
				FrameCaptureSession session = CaptureSession(target, created);
				if (session != null && (!session.Stopped || session.NoChangeNeeded) && session.Failures < MaxCaptureFailures)
				{
					pid = target;
					birth = created;
					observing = true;
					int epoch = NextEpochLocked(target, created);
					worker = new Thread((ThreadStart)delegate
					{
						Observe(target, created, name, epoch, admitted, session);
					});
					worker.IsBackground = true;
					worker.Name = "Pavise.FrameObserver";
					worker.Priority = ThreadPriority.BelowNormal;
					worker.Start();
				}
			}
		}

		internal static void ReleaseObserver()
		{
			lock (sync)
			{
				if (observing && worker != null && worker.IsAlive)
				{
					Interlocked.Increment(ref generation);
				}
			}
		}

		internal static bool ReleaseBoost(int observedPid, long observedBirth)
		{
			bool allConditionsMet;
			lock (sync)
			{
				allConditionsMet = observing && worker != null && worker.IsAlive && observedPid > 0 && pid == observedPid && birth == observedBirth;
				if (!allConditionsMet)
				{
					Interlocked.Increment(ref generation);
					state = 0;
				}
			}
			bool restoreSucceeded = Restore();
			if (!allConditionsMet)
			{
				restoreSucceeded &= FrameCoordinationTrace.Heal();
			}
			return restoreSucceeded;
		}

		private static void Observe(int target, long created, string name, int epoch, Func<bool> admitted, FrameCaptureSession session)
		{
			Func<bool> func = () => Current(epoch, admitted);
			Stopwatch stopwatch = Stopwatch.StartNew();
			int observeValue = -1;
			try
			{
				while (func())
				{
					bool restoreSucceeded = Restore();
					if (!(restoreSucceeded & FrameCoordinationTrace.Heal()))
					{
						state = 3;
						if (!Wait(30000, func))
						{
							break;
						}
						continue;
					}
					if (session.Failures >= MaxCaptureFailures || (session.Stopped && !session.NoChangeNeeded) || CaptureProcessState(target, created) > 0)
					{
						break;
					}
					state = 1;
					FrameCoordinationSample frameCoordinationSample = TakeCapture(target, created, func, true);
					state = 0;
					if (!func() || (frameCoordinationSample != null && (frameCoordinationSample.Issue == FrameCaptureIssue.Canceled || frameCoordinationSample.Issue == FrameCaptureIssue.TargetChanged)))
					{
						break;
					}
					if (frameCoordinationSample == null || frameCoordinationSample.Issue == FrameCaptureIssue.CaptureFailed || frameCoordinationSample.Issue == FrameCaptureIssue.DecodeFailed || frameCoordinationSample.Issue == FrameCaptureIssue.TraceStopFailed)
					{
						ActionFailureStore.Trip("FrameCoordination", name, (frameCoordinationSample == null) ? "Thread capture returned no result" : frameCoordinationSample.Detail);
					}
					bool matchesValid = frameCoordinationSample != null && frameCoordinationSample.Valid;
					long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
					ObserveCapture(session, frameCoordinationSample, matchesValid, elapsedMilliseconds);
					if (matchesValid)
					{
						PublishCriticalRoles(frameCoordinationSample, epoch);
						if (frameCoordinationSample.PresentTid != observeValue)
						{
							observeValue = frameCoordinationSample.PresentTid;
							Logger.Info("FRAME " + PerformancePolicy.Text("已识别渲染线程（仅观测，不改优先级）：", "Render thread identified (observation only, no priority change): ") + name + " pid=" + target + " present=" + observeValue);
						}
						if (!Wait(30000, func))
						{
							break;
						}
						continue;
					}
					if (session.Failures >= MaxCaptureFailures)
					{
						state = 4;
						Logger.Log(WaitingLine(name, frameCoordinationSample) + " | " + PerformancePolicy.Text("连续采样未成功，本进程不再观测", "Repeated captures were unsuccessful; observation stopped for this process") + " captureFailures=" + session.Failures + "/" + MaxCaptureFailures);
						break;
					}
					if (ActionFailureStore.IsBlocked("FrameCoordination", name) || (session.Stopped && !session.NoChangeNeeded))
					{
						state = 4;
						break;
					}
					if (session.Retry.ShouldLog)
					{
						Logger.Info("FRAME " + PerformancePolicy.Text("渲染线程识别等待：", "Render thread identification waiting: ") + name + " | " + ((frameCoordinationSample == null) ? FrameCoordinationSample.IssueText(FrameCaptureIssue.CaptureFailed) : frameCoordinationSample.Detail) + " retryMs=" + session.Retry.DelayMs + " captureFailures=" + session.Failures + "/" + MaxCaptureFailures);
					}
					if (!Wait(session.Retry.DelayMs, func))
					{
						break;
					}
				}
			}
			catch (Exception ex)
			{
				state = 3;
				ObserveCapture(session, new FrameCoordinationSample
				{
					Pid = target,
					Birth = created
				}.Reject(FrameCaptureIssue.CaptureFailed), false, stopwatch.ElapsedMilliseconds);
				if (func())
				{
					ActionFailureStore.Trip("FrameCoordination", name, "Thread capture execution failed: " + ex.Message);
					session.Stopped = true;
					SaveCaptureSessions();
				}
				Logger.Error("FRAME " + ex.Message);
			}
			finally
			{
				Restore();
				FrameCoordinationTrace.Heal();
				if (Volatile.Read(ref generation) == epoch)
				{
					state = 0;
				}
			}
		}

		private static FrameCaptureSession CaptureSession(int target, long created)
		{
			lock (captureSessionGate)
			{
				if (!LoadCaptureSessions())
				{
					return null;
				}
				FrameCaptureSession value;
				if (captureSessions.TryGetValue(target, out value) && value.Birth == created)
				{
					return value;
				}
				if (value != null)
				{
					if (CaptureProcessState(target, created) != 0)
					{
						return null;
					}
					captureSessions.Remove(target);
				}
				if (captureSessions.Count >= 64)
				{
					List<int> list = new List<int>();
					foreach (KeyValuePair<int, FrameCaptureSession> captureSession in captureSessions)
					{
						if (CaptureProcessState(captureSession.Key, captureSession.Value.Birth) > 0)
						{
							list.Add(captureSession.Key);
						}
					}
					foreach (int item in list)
					{
						captureSessions.Remove(item);
					}
					if (captureSessions.Count >= 64)
					{
						return null;
					}
				}
				value = new FrameCaptureSession(target, created);
				captureSessions[target] = value;
				return value;
			}
		}

		private static bool LoadCaptureSessions()
		{
			if (captureSessionsLoaded)
			{
				return !captureSessionStoreFailed;
			}
			captureSessionsLoaded = true;
			string value;
			if (!Settings.TryLoadStr("FrameCoordinationCaptureSessionsV1", out value))
			{
				return CaptureSessionLoadFailed();
			}
			if (value.Length == 0)
			{
				return true;
			}
			string[] valueParts = value.Split(';');
			bool legacy = valueParts[0] == "v1";
			if ((!legacy && valueParts[0] != "v2") || valueParts.Length > 65)
			{
				return CaptureSessionLoadFailed();
			}
			int storedLimit = legacy ? 3 : MaxCaptureFailures;
			for (int i = 1; i < valueParts.Length; i++)
			{
				string[] parts = valueParts[i].Split('|');
				int result;
				long result2;
				int result3;
				int result4;
				int result5;
				int result6;
				if (parts.Length != 6 || !int.TryParse(parts[0], out result) || result <= 0 || !long.TryParse(parts[1], out result2) || result2 <= 0 || !int.TryParse(parts[2], out result3) || (result3 != 0 && result3 != 1) || !int.TryParse(parts[3], out result4) || result4 < 0 || result4 > storedLimit || !int.TryParse(parts[4], out result5) || (result5 != 0 && result5 != 1) || !int.TryParse(parts[5], out result6) || (result6 != 0 && result6 != 1) || (result4 == storedLimit && result5 == 0) || (result6 != 0 && result5 == 0) || captureSessions.ContainsKey(result))
				{
					return CaptureSessionLoadFailed();
				}
				captureSessions[result] = new FrameCaptureSession(result, result2)
				{
					PresenterOnly = (result3 != 0),
					// Saturate an exhausted legacy budget instead of granting it two new attempts.
					Failures = (legacy && result4 == storedLimit) ? MaxCaptureFailures : result4,
					Stopped = (result5 != 0),
					NoChangeNeeded = (result6 != 0)
				};
			}
			return true;
		}

		private static bool CaptureSessionLoadFailed()
		{
			captureSessions.Clear();
			captureSessionStoreFailed = true;
			Logger.Warn("FRAME " + PerformancePolicy.Text("采样状态无法读取，保留系统调度", "Capture state is unreadable; system scheduling retained"));
			return false;
		}

		private static void SaveCaptureSessions()
		{
			List<string> list = new List<string>();
			list.Add("v2");
			List<string> list2 = list;
			foreach (FrameCaptureSession value2 in captureSessions.Values)
			{
				if (value2.PresenterOnly || value2.Failures != 0 || value2.Stopped)
				{
					list2.Add(value2.Pid.ToString(CultureInfo.InvariantCulture) + "|" + value2.Birth.ToString(CultureInfo.InvariantCulture) + "|" + (value2.PresenterOnly ? "1" : "0") + "|" + value2.Failures.ToString(CultureInfo.InvariantCulture) + "|" + (value2.Stopped ? "1" : "0") + "|" + (value2.NoChangeNeeded ? "1" : "0"));
				}
			}
			string joinResult = ((list2.Count == 1) ? "" : string.Join(";", list2.ToArray()));
			string value;
			if (Settings.SaveStr("FrameCoordinationCaptureSessionsV1", joinResult) && Settings.TryLoadStr("FrameCoordinationCaptureSessionsV1", out value) && value == joinResult)
			{
				return;
			}
			captureSessionStoreFailed = true;
			foreach (FrameCaptureSession value3 in captureSessions.Values)
			{
				value3.Stopped = true;
			}
		}

		private static bool CapturePressure(FrameCaptureIssue issue)
		{
			if (issue != FrameCaptureIssue.SystemEventBudget && issue != FrameCaptureIssue.EventBudget && issue != FrameCaptureIssue.EdgeBudget && issue != FrameCaptureIssue.CpuBudget && issue != FrameCaptureIssue.EventsLost)
			{
				return issue == FrameCaptureIssue.TraceUnavailable;
			}
			return true;
		}

		private static void ObserveCapture(FrameCaptureSession session, FrameCoordinationSample sample, bool applied, long elapsedMs)
		{
			if (sample != null && sample.PriorityReserved && !applied) return;
			lock (captureSessionGate)
			{
				bool presenterOnly = session.PresenterOnly;
				bool stopped = session.Stopped;
				bool noChangeNeeded = session.NoChangeNeeded;
				int failures = session.Failures;
				if (sample != null && sample.Valid && (applied || sample.NoChangeNeeded))
				{
					session.Failures = 0;
					session.Retry.Reset();
					if (sample.NoChangeNeeded)
					{
						session.NoChangeNeeded = true;
						session.Stopped = true;
					}
				}
				else
				{
					session.Retry.Observe(sample, elapsedMs);
					FrameCaptureIssue frameCaptureIssue = ((sample == null) ? FrameCaptureIssue.CaptureFailed : sample.Issue);
					if (frameCaptureIssue == FrameCaptureIssue.Canceled || frameCaptureIssue == FrameCaptureIssue.TargetChanged)
					{
						return;
					}
					// Evidence misses consume the same persisted budget as other unsuccessful
					// captures. Both boost and observer workers must share this counter.
					if (session.Failures < MaxCaptureFailures)
					{
						session.Failures++;
					}
					if (!session.PresenterOnly && CapturePressure(frameCaptureIssue))
					{
						session.PresenterOnly = true;
					}
					if (session.Failures >= MaxCaptureFailures)
					{
						session.Stopped = true;
					}
				}
				if (presenterOnly != session.PresenterOnly || stopped != session.Stopped || noChangeNeeded != session.NoChangeNeeded || failures != session.Failures)
				{
					SaveCaptureSessions();
				}
			}
		}

		internal static bool IdentificationExhausted(int target, long created, string name)
		{
			if (target <= 0 || created <= 0 || string.IsNullOrEmpty(name))
			{
				return false;
			}
			lock (captureSessionGate)
			{
				FrameCaptureSession value;
				if (!LoadCaptureSessions() || !captureSessions.TryGetValue(target, out value) || value.Birth != created || !value.Stopped || value.NoChangeNeeded)
				{
					return false;
				}
			}
			return !ActionFailureStore.IsBlocked("FrameCoordination", name);
		}

		private static int CaptureProcessState(int target, long created)
		{
			return NativeFrameThreads.ProcessState(target, created);
		}

		private static FrameCoordinationSample TakeCapture(int target, long created, Func<bool> alive, bool presenterOnly)
		{
			return FrameCoordinationSampler.Capture(target, created, alive, presenterOnly);
		}

		private static void PublishCriticalRoles(FrameCoordinationSample original, int originalEpoch)
		{
			PublishCriticalRolesAt(original, originalEpoch, AutoCores.NowTicks);
		}

		private static void PublishCriticalRolesAt(FrameCoordinationSample original, int originalEpoch, long observed)
		{
			if (original == null || !original.Valid || original.Issue != 0 || original.LostEvents != 0)
			{
				return;
			}
			lock (sync)
			{
				if (closed || originalEpoch != generation || original.Pid != pid || original.Birth != birth)
				{
					return;
				}
				List<CriticalThreadRoleFact> list = new List<CriticalThreadRoleFact>();
				long value;
				if (original.PresentTid > 0 && original.Threads.TryGetValue(original.PresentTid, out value) && value > 0)
				{
					list.Add(new CriticalThreadRoleFact(original.PresentTid, value, "presentation-associated"));
					if (original.WorkerTid > 0 && original.WorkerTid != original.PresentTid && original.Threads.TryGetValue(original.WorkerTid, out value) && value > 0)
					{
						list.Add(new CriticalThreadRoleFact(original.WorkerTid, value, "dependency-associated"));
					}
					criticalRoles = new OriginalCriticalRoles(originalEpoch, pid, birth, observed, list.ToArray());
				}
			}
		}

		private static int NextEpochLocked(int target, long created)
		{
			OriginalCriticalRoles originalCriticalRoles = criticalRoles;
			int incrementResult = Interlocked.Increment(ref generation);
			if (originalCriticalRoles != null && originalCriticalRoles.Epoch == incrementResult - 1 && originalCriticalRoles.Pid == target && originalCriticalRoles.Birth == created)
			{
				criticalRoles = new OriginalCriticalRoles(incrementResult, target, created, originalCriticalRoles.Observed, originalCriticalRoles.Facts);
			}
			return incrementResult;
		}

		internal static bool HasCriticalPresenterCandidate(int target, long processBirth, long now)
		{
			lock (sync)
			{
				OriginalCriticalRoles roles = criticalRoles;
				if (closed || roles == null || roles.Epoch != generation || roles.Pid != target || roles.Birth != processBirth
					|| pid != target || birth != processBirth || now < roles.Observed || now - roles.Observed > 650000000)
					return false;
				foreach (CriticalThreadRoleFact fact in roles.Facts)
					if (fact.Tid > 0 && fact.Creation > 0 && fact.Role == "presentation-associated") return true;
				return false;
			}
		}

		internal static CriticalThreadRoleFact[] ReadCriticalRoles(int target, long processBirth, ThreadTimes latest, long now)
		{
			if (!CriticalThreadObservation.Valid(latest, processBirth, now))
			{
				return new CriticalThreadRoleFact[0];
			}
			lock (sync)
			{
				OriginalCriticalRoles originalCriticalRoles = criticalRoles;
				if (closed || originalCriticalRoles == null || originalCriticalRoles.Epoch != generation || originalCriticalRoles.Pid != target || originalCriticalRoles.Birth != processBirth || pid != target || birth != processBirth || now < originalCriticalRoles.Observed || now - originalCriticalRoles.Observed > 650000000)
				{
					return new CriticalThreadRoleFact[0];
				}
				Dictionary<long, long> dictionary = new Dictionary<long, long>();
				for (int i = 0; i < latest.Ids.Length; i++)
				{
					dictionary.Add(latest.Ids[i], latest.Created[i]);
				}
				List<CriticalThreadRoleFact> list = new List<CriticalThreadRoleFact>();
				CriticalThreadRoleFact[] facts = originalCriticalRoles.Facts;
				foreach (CriticalThreadRoleFact criticalThreadRoleFact in facts)
				{
					long value;
					if (dictionary.TryGetValue(criticalThreadRoleFact.Tid, out value) && value == criticalThreadRoleFact.Creation)
					{
						list.Add(criticalThreadRoleFact);
					}
				}
				return list.ToArray();
			}
		}
	}
}
