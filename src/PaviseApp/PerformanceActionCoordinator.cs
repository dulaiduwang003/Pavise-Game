using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
	internal sealed class PerformanceActionCoordinator
	{
		private sealed class Request
		{
			internal bool Guard;

			internal bool Match;

			internal bool VisualMatch;

			internal bool RequireExistingGames;

			internal string[] Games;

			internal string Signature;
			internal long SessionSerial;
		}

		private sealed class Attempt
		{
			internal long Next;

			internal int Failures;
			internal Func<bool>[] RecoveryObservers;

			internal string State = "";

			internal string Resolved;

			internal string Target;

			internal string Detail;
		}

		private readonly object gate = new object();

		private readonly ManualResetEvent drained = new ManualResetEvent(true);

		private readonly ManualResetEvent sessionRestored = new ManualResetEvent(true);

		private volatile bool sessionRestoreOk = true;
		private sealed class RestoreSchedule
		{
			internal bool Reported;
			internal long Due;
			internal Func<bool>[] Observers;
		}
		private RestoreSchedule sessionRestoreSchedule;

		private readonly Dictionary<string, string> states = new Dictionary<string, string>();

		private readonly Dictionary<string, string> publishedDetails = new Dictionary<string, string>();

		private readonly Dictionary<string, Attempt> attempts = new Dictionary<string, Attempt>();

		private readonly Dictionary<string, Attempt> restoreAttempts = new Dictionary<string, Attempt>();

		private readonly Dictionary<string, string> restoreDetails = new Dictionary<string, string>();

		private readonly Dictionary<string, string> batchStates = new Dictionary<string, string>();

		private readonly Dictionary<string, List<string>> batchDetails = new Dictionary<string, List<string>>();

		private bool collectingBatch;

		private Request latest;

		private bool running;

		private bool closing;

		private bool guardWasOn;

		private bool visualWasOn;

		private bool defenderWasRunning;

		private long nextCheck;

		internal bool DrainForNvHandoff(int timeout)
		{
			return drained.WaitOne(timeout);
		}

		private long Now()
		{
			return DateTime.UtcNow.Ticks;
		}

		internal string State(string id)
		{
			lock (gate)
			{
				string value;
				return states.TryGetValue(id, out value) ? value : "";
			}
		}

		private void Record(string id, string value)
		{
			Record(id, value, null);
		}

		private void Record(string id, string value, string detail)
		{
			if (!collectingBatch)
			{
				Publish(id, value, detail);
				return;
			}
			string value2;
			if (batchStates.TryGetValue(id, out value2))
			{
				if (value2 == "restore-failed" || (value != "restore-failed" && (value2 == "stopped" || (value2 == "failed" && value != "stopped"))))
				{
					value = value2;
				}
				else if (value == "unsupported" && (value2 == "configured" || value2 == "applied"))
				{
					value = value2;
				}
			}
			batchStates[id] = value;
			if (!string.IsNullOrEmpty(detail))
			{
				detail = PsRunner.NormalizeError(detail);
				List<string> value3;
				if (!batchDetails.TryGetValue(id, out value3))
				{
					value3 = (batchDetails[id] = new List<string>());
				}
				if (!value3.Contains(detail))
				{
					value3.Add(detail);
				}
			}
		}

		private void PublishBatch()
		{
			foreach (KeyValuePair<string, string> batchState in batchStates)
			{
				List<string> value;
				Publish(batchState.Key, batchState.Value, batchDetails.TryGetValue(batchState.Key, out value) ? string.Join(" ; ", value.ToArray()) : null);
			}
		}

		private void Publish(string id, string value, string detail)
		{
			detail = (string.IsNullOrEmpty(detail) ? "" : PsRunner.NormalizeError(detail));
			lock (gate)
			{
				string value2;
				string value3;
				if (states.TryGetValue(id, out value2) && value2 == value && publishedDetails.TryGetValue(id, out value3) && value3 == detail)
				{
					return;
				}
				states[id] = value;
				publishedDetails[id] = detail;
			}
			PerformanceAction performanceAction = PerformanceActionCatalog.Find(id);
			string text = "AUTO " + ((performanceAction == null) ? id : performanceAction.Title) + " | " + PerformanceTuning.Label(value);
			if (!string.IsNullOrEmpty(detail))
			{
				text = text + " | " + PsRunner.NormalizeError(detail);
			}
			switch (value)
			{
			case "failed":
			case "stopped":
			case "restore-failed":
				Logger.Warn(text);
				break;
			default:
				Logger.Info(text);
				break;
			}
		}

		internal void Queue(bool guard, bool match, string[] games, bool visualMatch = false, bool requireExistingGames = false)
		{
			games = ((match || games == null) ? new string[0] : ((string[])games.Clone()));
			if (!match)
			{
				Array.Sort(games, StringComparer.OrdinalIgnoreCase);
			}
			long sessionSerial = PerformanceTuning.RecoveryCycleSerial;
			string text = sessionSerial + "|" + guard + "|" + match + "|" + visualMatch + "|" + requireExistingGames + "|" + string.Join("\n", games) + "|selected:" + PerformanceTuning.ActionSelectionSignature();
			lock (gate)
			{
				long num = Now();
				if (closing || (latest != null && latest.Signature == text && num < nextCheck))
				{
					return;
				}
				latest = new Request
				{
					Guard = guard,
					Match = match,
					VisualMatch = visualMatch,
					RequireExistingGames = requireExistingGames,
					Games = games,
					Signature = text
					, SessionSerial = sessionSerial
				};
				sessionRestored.Reset();
				nextCheck = num + TimeSpan.FromMinutes(1.0).Ticks;
				if (!running)
				{
					running = true;
					drained.Reset();
					if (!ThreadPool.QueueUserWorkItem(delegate
					{
						Run();
					}))
					{
						running = false;
						drained.Set();
					}
				}
			}
		}

		private bool Current(Request request)
		{
			lock (gate)
			{
				return latest == request;
			}
		}

		private bool Due(string id, string key, out Attempt attempt)
		{
			if (!attempts.TryGetValue(key, out attempt))
			{
				attempt = new Attempt();
				attempts[key] = attempt;
			}
			if (attempt.State != "stopped" && Now() >= attempt.Next)
			{
				return true;
			}
			if (attempt.State != "resolved")
			{
				Record(id, attempt.State, attempt.Detail);
			}
			return false;
		}

		private void Failure(string id, Attempt attempt, string detail)
		{
			attempt.Failures++;
			attempt.Next = Now() + TimeSpan.FromMinutes(5.0).Ticks;
			attempt.State = "stopped";
			attempt.Detail = (string.IsNullOrEmpty(attempt.Target) ? "" : (attempt.Target + " | ")) + (string.IsNullOrEmpty(detail) ? PerformancePolicy.Text("配置未通过确认", "Configuration could not be confirmed") : PsRunner.NormalizeError(detail));
			ActionFailureStore.Trip(id, attempt.Target, detail);
			Record(id, attempt.State, attempt.Detail);
			RestoreSelected(id, (PerformanceActionStore.Receipt receipt) => string.Equals(receipt.Target, attempt.Target, StringComparison.OrdinalIgnoreCase), true);
		}

		private void Unsupported(string id, Attempt attempt, string state)
		{
			attempt.Failures = 0;
			attempt.State = state;
			attempt.Detail = null;
			attempt.Next = Now() + TimeSpan.FromMinutes(30.0).Ticks;
			Record(id, state);
		}

		private void DropAction(string id)
		{
			foreach (string item in new List<string>(attempts.Keys))
			{
				if (item.StartsWith(id + "\n", StringComparison.Ordinal))
				{
					attempts.Remove(item);
				}
			}
		}

		private bool Resolve(string id, string target, Func<string> query, out string resolved)
		{
			string key = PerformanceTuning.RecoveryCycleSerial + "\n" + id + "\nresolve:" + target;
			resolved = null;
			Attempt attempt;
			if (!Due(id, key, out attempt))
			{
				resolved = attempt.Resolved;
				return attempt.State == "resolved";
			}
			attempt.Target = target;
			try
			{
				string queried = null;
				if (!PerformanceTuning.RestoreWithBudget(new PerformanceActionStore.Receipt { Id = "resolve:" + id, Target = target }, 3,
					delegate { queried = query(); return true; }))
				{
					attempt.State = "stopped";
					Record(id, "stopped", "Query failure budget exhausted");
					return false;
				}
				resolved = queried;
				if (string.IsNullOrEmpty(resolved))
				{
					Unsupported(id, attempt, "unsupported");
					return false;
				}
				attempt.Resolved = resolved;
				attempt.State = "resolved";
				attempt.Failures = 0;
				attempt.Detail = null;
				attempt.Next = Now() + TimeSpan.FromMinutes(30.0).Ticks;
				return true;
			}
			catch (PerformanceActionBackend.DefenderInactiveException)
			{
				attempts.Remove(key);
				Record(id, "inactive");
			}
			catch (NotSupportedException)
			{
				Unsupported(id, attempt, "unsupported");
			}
			catch (OperationCanceledException)
			{
				attempts.Remove(key);
			}
			catch (Exception ex4)
			{
				attempt.State = "failed";
				attempt.Detail = PsRunner.NormalizeError(ex4.Message);
				attempt.Next = Now() + TimeSpan.FromMinutes(5.0).Ticks;
				Record(id, "failed", attempt.Detail);
			}
			return false;
		}

		private void Try(string id, string target, string wanted, Request request, string success)
		{
			if (id == "AutoDefenderResourcesV1" || id == "AutoDefenderSessionV1" || !Current(request) || ((id == "AutoGameMitigationsV1" || id == "AutoOpenGlThreadsV1") && !GamePerformanceTargetPolicy.AllowsGameTarget(target)))
			{
				return;
			}
			ActionFailureStore.BeginAutomaticCycle(id, target, request.SessionSerial + "\n" + wanted);
			string key = request.SessionSerial + "\n" + id + "\napply:" + target + "\n" + wanted;
			Attempt value;
			if (ActionFailureStore.IsIsolated(id, target))
			{
				if (!RestoreSelected(id, (PerformanceActionStore.Receipt receipt) => string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase), false))
				{
					Record(id, "restore-failed", target);
					return;
				}
			}
			ActionFailureStore.BeginAutomaticCycle(id, target, request.SessionSerial + "\n" + wanted);
			if (ActionFailureStore.IsBlocked(id, target))
			{
				bool flag = RestoreSelected(id, (PerformanceActionStore.Receipt receipt) => string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase), false);
				Record(id, flag ? "stopped" : "restore-failed", target + " | " + ActionFailureStore.FailureReason(id, target));
			}
			else
			{
				if (!Due(id, key, out value))
				{
					return;
				}
				value.Target = target;
				string reason;
				if (ActionFailureStore.TryBeginApply(id, target, out reason))
				{
					try
					{
						if (!Current(request))
						{
							ActionFailureStore.CancelApply(id, target);
						}
						else if (!PerformanceActionBackend.Apply(id, target, wanted))
						{
							Failure(id, value, PerformanceActionBackend.ApplyFailureHint(id, target));
						}
						else if (!ActionFailureStore.CompleteApply(id, target))
						{
							Failure(id, value, "Isolation record persistence failed");
						}
						else
						{
							value.Next = Now() + TimeSpan.FromMinutes(30.0).Ticks;
							value.Failures = 0;
							value.State = success;
							value.Detail = null;
							Record(id, success);
						}
						return;
					}
					catch (PerformanceActionBackend.DefenderInactiveException)
					{
						CancelAfterRestore(id, target);
						attempts.Remove(key);
						Record(id, "inactive");
						return;
					}
					catch (NotSupportedException)
					{
						CancelAfterRestore(id, target);
						Unsupported(id, value, "unsupported");
						return;
					}
					catch (OperationCanceledException)
					{
						CancelAfterRestore(id, target);
						attempts.Remove(key);
						return;
					}
					catch (Exception ex4)
					{
						Failure(id, value, ex4.Message);
						return;
					}
				}
				Record(id, "stopped", target + " | " + reason);
			}
		}

		private void CancelAfterRestore(string id, string target)
		{
			if (RestoreSelected(id, (PerformanceActionStore.Receipt receipt) => string.Equals(receipt.Target, target, StringComparison.OrdinalIgnoreCase), true))
			{
				ActionFailureStore.CancelApply(id, target);
			}
			else
			{
				ActionFailureStore.Trip(id, target, "Recovery incomplete after skipped apply");
			}
		}

		private bool RestoreSelected(string id, Predicate<PerformanceActionStore.Receipt> selected, bool force)
		{
			int count;
			return RestoreSelected(id, selected, force, out count);
		}

		private bool RestoreSelected(string id, Predicate<PerformanceActionStore.Receipt> selected, bool force, out int count)
		{
			bool result = true;
			count = 0;
			List<PerformanceActionStore.Receipt> list;
			try
			{
				if (!PerformanceTuning.ReadRecoveryReceipts(out list)) return false;
			}
			catch (Exception ex)
			{
				RestoreFailure(id, "journal:" + id, ex.Message);
				return false;
			}
			foreach (PerformanceActionStore.Receipt item in list)
			{
				if (item.Id != id || (selected != null && !selected(item)))
				{
					continue;
				}
				string key = PerformanceTuning.RecoveryCycleSerial + "\n" + PerformanceActionStore.Slot(item.Id, item.Target) + "\n" + item.Original + "\n" + item.Expected;
				Attempt value;
				if (!restoreAttempts.TryGetValue(key, out value))
				{
					value = new Attempt();
					restoreAttempts[key] = value;
				}
				if (!force && Now() < value.Next)
				{
					ReportRestoreSchedule(value);
					result = false;
					string value2;
					restoreDetails.TryGetValue(key, out value2);
					Record(id, "restore-failed", value2);
					continue;
				}
				bool flag = false;
				string text = null;
				using (var attemptTrace = new RecoveryAttemptTrace())
				{
					try { flag = PerformanceTuning.RestorePerformanceReceipt(item); }
					catch (Exception ex2) { text = ex2.Message; }
					if (!flag && attemptTrace.Reported && attemptTrace.Next == long.MaxValue)
					{
						value.Failures = 5;
						value.RecoveryObservers = attemptTrace.PhaseObservers.ToArray();
					}
				}
				if (flag)
				{
					restoreAttempts.Remove(key);
					restoreDetails.Remove(key);
					count++;
					continue;
				}
				result = false;
				if (value.Failures < 5)
				{
					value.Failures++;
				}
				value.Next = Now() + TimeSpan.FromSeconds(Math.Min(300, 30 << value.Failures - 1)).Ticks;
				ReportRestoreSchedule(value);
				RestoreFailure(id, key, item.Target + " | " + (text ?? PerformancePolicy.Text("配置恢复未完成", "Configuration restore incomplete")));
			}
			return result;
		}

		private static void ReportRestoreSchedule(Attempt attempt)
		{
			if (attempt.Failures < 5) { RecoveryAttemptTrace.DeferredUntil(attempt.Next); return; }
			long session = PerformanceTuning.RecoveryCycleSerial;
			RecoveryAttemptTrace.Exhausted(() => PerformanceTuning.RecoveryCycleSerial != session);
			if (attempt.RecoveryObservers != null)
				foreach (Func<bool> observer in attempt.RecoveryObservers) RecoveryAttemptTrace.Exhausted(observer);
		}

		private static bool HasRetiredDefenderRecords()
		{
			try
			{
				List<PerformanceActionStore.Receipt> receipts;
				if (!PerformanceTuning.ReadRecoveryReceipts(out receipts)) return true;
				foreach (PerformanceActionStore.Receipt item in receipts)
				{
					if (item.Id == "AutoDefenderSessionV1" || item.Id == "AutoDefenderResourcesV1")
					{
						return true;
					}
				}
				return false;
			}
			catch
			{
				return true;
			}
		}

		private bool RestoreRetired(bool force)
		{
			bool flag = true;
			string[] array = new string[3] { "AutoDefenderSessionV1", "AutoDefenderResourcesV1", "AutoBackgroundFpsV1" };
			foreach (string id in array)
			{
				int count;
				flag &= RestoreSelected(id, null, force, out count);
				if (count > 0)
				{
					Record(id, "restored");
				}
				DropAction(id);
			}
			return flag;
		}

		private void RestoreFailure(string id, string key, string detail)
		{
			detail = PsRunner.NormalizeError(detail);
			restoreDetails[key] = detail;
			Record(id, "restore-failed", detail);
		}

		private void Process(Request request)
		{
			using (var recoveryTrace = new RecoveryAttemptTrace())
			{
			if (!request.Match || !request.Guard) sessionRestoreOk = false;
			batchStates.Clear();
			batchDetails.Clear();
			collectingBatch = true;
			try
			{
				ProcessCore(request);
			}
			finally
			{
				collectingBatch = false;
				PublishBatch();
				if (!request.Match || !request.Guard)
				{
					Volatile.Write(ref sessionRestoreSchedule, new RestoreSchedule { Reported = recoveryTrace.Reported,
						Due = recoveryTrace.Next, Observers = recoveryTrace.PhaseObservers.ToArray() });
					sessionRestored.Set();
				}
			}
			}
		}

		private void ProcessCore(Request request)
		{
			bool flag = guardWasOn && !request.Guard;
			if (request.Guard && !guardWasOn)
			{
				attempts.Clear();
			}
			guardWasOn = request.Guard;
            bool flag2 = visualWasOn;
            visualWasOn = false;
            // Reject ON for retired desktop effects while retaining receipt-backed restoration.
            bool flag3 = RestoreSelected("MatchVisualEffectsV1", null, flag2);
            if (!flag3 || flag2 || State("MatchVisualEffectsV1").Length > 0)
			{
                Record("MatchVisualEffectsV1", (!flag3) ? "restore-failed" : "off");
			}
            DropAction("MatchVisualEffectsV1");
			bool flag4 = HasRetiredDefenderRecords() && PerformanceActionBackend.DefenderRunning();
			bool flag5 = flag4 && !defenderWasRunning;
			defenderWasRunning = flag4;
			bool flag6 = RestoreRetired(flag5 || flag);
			if (!request.Match || !request.Guard)
			{
				sessionRestoreOk = flag6 && flag3;
			}
			if (!request.Guard || !Current(request))
			{
				return;
			}
			string[] array = new string[3] { "AutoGameMitigationsV1", "AutoOpenGlThreadsV1", "AutoNtfsShortNamesV1" };
			foreach (string id in array)
			{
				if (!PerformanceTuning.LegacyActionSelected(id))
				{
					int count;
					if (!RestoreSelected(id, null, false, out count))
					{
						Record(id, "restore-failed");
					}
					else if (count > 0)
					{
						Record(id, "restored");
					}
					else if (State(id).Length > 0 && State(id) != "off")
					{
						Record(id, "off");
					}
					DropAction(id);
				}
			}
			if (request.Match)
			{
				return;
			}
			bool flag7 = PerformanceTuning.ActionSelected("AutoGameMitigationsV1");
			bool flag8 = PerformanceTuning.LegacyActionSelected("AutoOpenGlThreadsV1");
			bool flag9 = PerformanceTuning.ActionSelected("AutoNtfsShortNamesV1");
			if (!flag7 && !flag8 && !flag9)
			{
				return;
			}
			string[] array2 = GamePerformanceTargetPolicy.FilterGameTargets(request.Games, request.RequireExistingGames);
			foreach (string path in array2)
			{
				if (!Current(request))
				{
					break;
				}
				if (flag7)
				{
					Try("AutoGameMitigationsV1", path, PerformanceActionBackend.Is32Bit(path) ? "off32" : "off64", request, "configured");
				}
				if (!Current(request))
				{
					break;
				}
				if (flag8)
				{
					Try("AutoOpenGlThreadsV1", path, "1", request, "configured");
				}
				if (flag9)
				{
					string target;
					try
					{
						target = Path.GetPathRoot(path);
					}
					catch
					{
						target = path;
					}
					string resolved;
					if (Resolve("AutoNtfsShortNamesV1", target, () => PerformanceActionBackend.ShortNameVolume(path), out resolved))
					{
						Try("AutoNtfsShortNamesV1", resolved, "1", request, "applied");
					}
				}
			}
		}

		private void Run()
		{
			while (true)
			{
				Request request;
				lock (gate)
				{
					request = latest;
				}
				try
				{
					Process(request);
				}
				catch (Exception ex)
				{
					Logger.Warn("Performance automation | " + ex.Message);
				}
				lock (gate)
				{
					if (request == latest)
					{
						running = false;
						drained.Set();
						break;
					}
				}
			}
		}

		internal bool Close(int timeout)
		{
			lock (gate)
			{
				closing = true;
				latest = new Request
				{
					Guard = false,
					Match = false,
					Games = new string[0]
				};
			}
			if (!drained.WaitOne(timeout))
			{
				return false;
			}
			bool flag = RestoreRetired(true);
			bool result = RestoreSelected("MatchVisualEffectsV1", null, true);
			if (flag)
			{
				return result;
			}
			return false;
		}

		internal bool WaitForSessionRestore(int timeout)
		{
			if (sessionRestored.WaitOne(timeout))
			{
				if (sessionRestoreOk) return true;
				RestoreSchedule schedule = Volatile.Read(ref sessionRestoreSchedule);
				if (schedule != null && schedule.Reported)
				{
					if (schedule.Due == long.MaxValue) RecoveryAttemptTrace.Exhausted();
					else RecoveryAttemptTrace.DeferredUntil(Math.Max(schedule.Due, DateTime.UtcNow.AddSeconds(1).Ticks));
					if (schedule.Observers != null)
						foreach (Func<bool> observer in schedule.Observers) RecoveryAttemptTrace.Exhausted(observer);
				}
				return false;
			}
			RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
			return false;
		}
	}
}
