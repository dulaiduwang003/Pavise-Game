using System;
using System.Diagnostics;

namespace PaviseApp
{
	internal sealed class TimerModeController
	{
		internal readonly object Gate = new object();

		internal Func<int> OsBuild = Native.OsBuild;

		internal Func<string> Boot = BootSessionIdentity.Read;

		internal Func<bool> TickOn = TimerTickTweak.CurrentlyOn;

		internal Func<bool> TickOwned = () => TimerTickTweak.OwnsState;

		internal Func<bool> TickEnable = TimerTickTweak.Enable;

		internal Func<bool> TickRestore = TimerTickTweak.Restore;

		internal Func<bool> GlobalOn = GlobalTimerResTweak.CurrentlyOn;

		internal Func<bool> GlobalOwned = () => GlobalTimerResTweak.OwnsState;

		internal Func<bool> GlobalEnable = GlobalTimerResTweak.Enable;

		internal Func<bool> GlobalRestore = GlobalTimerResTweak.Restore;

		internal volatile string TickState = "";

		internal volatile string GlobalState = "";

		private sealed class CheckSchedule
		{
			internal bool Initialized, Wanted, ForwardStopped;
			internal int BuildClass, Failures;
			internal long NextCheck;
			internal readonly AutomaticRecoveryBudget Recovery = new AutomaticRecoveryBudget();
		}

		private readonly CheckSchedule tickSchedule = new CheckSchedule();

		private readonly CheckSchedule globalSchedule = new CheckSchedule();

		internal void BeginAutomaticCycle(string identity)
		{
			lock (Gate)
			{
				ActionFailureStore.BeginAutomaticCycle("AutoTimerTick", "system", identity);
				ActionFailureStore.BeginAutomaticCycle("AutoGlobalTimer", "system", identity);
				if (tickSchedule.Recovery.BeginCycle(identity)) { tickSchedule.ForwardStopped = false; tickSchedule.NextCheck = 0L; tickSchedule.Failures = tickSchedule.Recovery.FailureCount; }
				if (globalSchedule.Recovery.BeginCycle(identity)) { globalSchedule.ForwardStopped = false; globalSchedule.NextCheck = 0L; globalSchedule.Failures = globalSchedule.Recovery.FailureCount; }
			}
		}

		internal bool GlobalReady
		{
			get
			{
				return GlobalState == "ready";
			}
		}

		internal void SyncEnvironmentPreferences()
		{
			lock (Gate)
			{
				Sync(PerformancePolicy.EnvironmentPreference("AutoTimerTick"), PerformancePolicy.EnvironmentPreference("AutoGlobalTimer"), OsBuild(), DateTime.UtcNow.Ticks);
			}
		}

		internal void Sync(bool tickWanted, bool globalWanted, int build, long now)
		{
			lock (Gate)
			{
				SyncLocked(tickWanted, globalWanted, build, now);
			}
		}

		private void SyncLocked(bool tickWanted, bool globalWanted, int build, long now)
		{
			int buildClass = build >= 20348 ? 2 : (build > 0 && build < 19041 ? 1 : 0);
			bool tickDue = CheckDue(tickSchedule, tickWanted, 0, TickState, now);
			bool globalDue = CheckDue(globalSchedule, globalWanted, buildClass, GlobalState, now);
			if (!tickDue && !globalDue) return;
			Stopwatch elapsed = Stopwatch.StartNew();
			string boot;
			try { boot = Boot(); }
			catch { boot = null; }
			if (tickDue)
			{
				NoteCheck(tickSchedule, tickWanted, 0);
				TickState = Step("AutoTimerTickBoot", tickWanted, boot, TickOn, TickOwned, TickEnable, TickRestore, tickSchedule.Recovery);
				tickSchedule.ForwardStopped = ActionFailureStore.IsBlocked("AutoTimerTick", "system");
				ScheduleCheck(tickSchedule, TickState, now + elapsed.Elapsed.Ticks);
			}
			if (globalDue)
			{
				NoteCheck(globalSchedule, globalWanted, buildClass);
				GlobalState = Step("AutoGlobalTimerBoot", globalWanted && buildClass == 2, boot, GlobalOn, GlobalOwned, GlobalEnable, GlobalRestore, globalSchedule.Recovery);
				globalSchedule.ForwardStopped = ActionFailureStore.IsBlocked("AutoGlobalTimer", "system");
				if (globalWanted && buildClass != 2 && GlobalState != "restore-failed")
					GlobalState = buildClass == 1 ? "ready" : "unsupported";
				ScheduleCheck(globalSchedule, GlobalState, now + elapsed.Elapsed.Ticks);
			}
		}

		private static bool CheckDue(CheckSchedule schedule, bool wanted, int buildClass, string state, long now)
		{
			// Preference changes cannot turn unresolved recovery into a polling loop.
			if (state == "restore-failed" && (!schedule.Recovery.CanAttempt || (schedule.Initialized && now < schedule.NextCheck)))
			{
				RecoveryAttemptTrace.FailureSchedule(schedule.Recovery.FailureCount, AutomaticRecoveryBudget.MaximumFailures, schedule.NextCheck);
				return false;
			}
			if (wanted && schedule.ForwardStopped && state != "restore-failed") return false;
			if (!wanted && state == "unverified" && !schedule.Recovery.CanAttempt) return false;
			if (schedule.Initialized && schedule.Wanted == wanted && schedule.BuildClass == buildClass
				&& ((!wanted && (state == "" || state == "restored")) || state == "unsupported")) return false;
			return !schedule.Initialized || schedule.Wanted != wanted || schedule.BuildClass != buildClass
				|| now >= schedule.NextCheck;
		}

		private static void NoteCheck(CheckSchedule schedule, bool wanted, int buildClass)
		{
			schedule.Initialized = true;
			schedule.Wanted = wanted;
			schedule.BuildClass = buildClass;
		}

		private static void ScheduleCheck(CheckSchedule schedule, string state, long completedAt)
		{
			bool failed = state == "failed" || state == "restore-failed";
			schedule.Failures = state == "restore-failed" ? schedule.Recovery.FailureCount : (failed ? Math.Min(8, schedule.Failures + 1) : 0);
			int delay = failed || state == "stopped"
				? Math.Min(300, 30 * (1 << Math.Min(4, Math.Max(0, schedule.Failures - 1)))) : 300;
			schedule.NextCheck = completedAt + TimeSpan.FromSeconds(delay).Ticks;
			if (state == "restore-failed")
				RecoveryAttemptTrace.FailureSchedule(schedule.Recovery.FailureCount, AutomaticRecoveryBudget.MaximumFailures, schedule.NextCheck);
		}

		internal static string Step(string slot, bool want, string boot, Func<bool> read, Func<bool> owned, Func<bool> enable, Func<bool> restore, AutomaticRecoveryBudget recovery = null)
		{
			string action = ((slot == "AutoTimerTickBoot") ? "AutoTimerTick" : ((slot == "AutoGlobalTimerBoot") ? "AutoGlobalTimer" : slot));
			bool admitted = false;
			try
			{
				string value = Settings.LoadStr(slot, "");
				if (want)
				{
					if (ActionFailureStore.IsBlocked(action, "system"))
					{
						if (!RestoreOwnedTimer(owned, restore, recovery)) return "restore-failed";
						ActionFailureStore.CancelApply(action, "system");
						return "stopped";
					}
					if (ActionFailureStore.IsIsolated(action, "system") && !RestoreOwnedTimer(owned, restore, recovery))
					{
						ActionFailureStore.Trip(action, "system", "Timer setting recovery incomplete before apply");
						return "restore-failed";
					}
					string reason;
					if (!ActionFailureStore.TryBeginApply(action, "system", out reason))
					{
						return "stopped";
					}
					admitted = true;
					bool readSucceeded = read();
					if (!readSucceeded)
					{
						if (!BootSessionIdentity.Valid(boot))
						{
							ActionFailureStore.CancelApply(action, "system");
							return "unverified";
						}
						string bootOffset = "on|" + boot;
						if (!Settings.SaveStrDurable(slot, bootOffset) || Settings.LoadStr(slot, "") != bootOffset)
						{
							throw new InvalidOperationException("Timer boot journal persistence failed");
						}
						value = bootOffset;
						if (!readSucceeded && !enable())
						{
							throw new InvalidOperationException("Timer setting apply failed");
						}
					}
					if (!read())
					{
						throw new InvalidOperationException("Timer setting readback failed");
					}
					string rebootStatusResult = RebootStatus(slot, value, boot, "on", "ready", "pending-reboot");
					if (rebootStatusResult == "failed")
					{
						throw new InvalidOperationException("Timer observation journal persistence failed");
					}
					if (rebootStatusResult == "unverified")
					{
						ActionFailureStore.CancelApply(action, "system");
						return rebootStatusResult;
					}
					if (ActionFailureStore.CompleteApply(action, "system"))
					{
						return ActionFailureStore.IsBlocked(action, "system") ? "stopped" : rebootStatusResult;
					}
					if (!RestoreOwnedTimer(owned, restore, recovery))
					{
						return "restore-failed";
					}
					ActionFailureStore.CancelApply(action, "system");
					return "stopped";
				}
				if (owned())
				{
					if (!BootSessionIdentity.Valid(boot))
					{
						if (recovery != null) recovery.Record(false);
						return "restore-failed";
					}
					string bootOffset2 = "off|" + boot;
					if (!Settings.SaveStrDurable(slot, bootOffset2) || Settings.LoadStr(slot, "") != bootOffset2)
					{
						if (recovery != null) recovery.Record(false);
						return "restore-failed";
					}
					value = bootOffset2;
					if (!RestoreOwnedTimer(owned, restore, recovery))
					{
						return "restore-failed";
					}
					ActionFailureStore.CancelApply(action, "system");
				}
				if (value.StartsWith("off|", StringComparison.Ordinal) || value.StartsWith("seen-off|", StringComparison.Ordinal))
				{
					string status = RebootStatus(slot, value, boot, "off", "restored", "restore-pending-reboot");
					if ((status == "unverified" || status == "restore-failed") && recovery != null) recovery.Record(false);
					return status;
				}
				return "";
			}
			catch (NotSupportedException)
			{
				return SkipTimerApply(action, want, admitted, owned, restore, "unsupported", recovery);
			}
			catch (OperationCanceledException)
			{
				return SkipTimerApply(action, want, admitted, owned, restore, "", recovery);
			}
			catch (Exception ex3)
			{
				if (!want)
				{
					if (recovery != null) recovery.Record(false);
					return "restore-failed";
				}
				ActionFailureStore.Trip(action, "system", ex3.Message);
				if (!RestoreOwnedTimer(owned, restore, recovery))
				{
					return "restore-failed";
				}
				ActionFailureStore.CancelApply(action, "system");
				return "stopped";
			}
		}

		private static bool RestoreOwnedTimer(Func<bool> owned, Func<bool> restore, AutomaticRecoveryBudget recovery)
		{
			if (recovery != null && !recovery.CanAttempt) return false;
			bool restored;
			bool hadOwner = false;
			try
			{
				hadOwner = owned();
				restored = !hadOwner || (restore() && !owned());
			}
			catch
			{
				restored = false;
			}
			if (recovery != null) recovery.Record(restored);
			if (recovery != null && restored && hadOwner) recovery.CompleteRecovery();
			return restored;
		}

		private static string SkipTimerApply(string action, bool want, bool admitted, Func<bool> owned, Func<bool> restore, string state, AutomaticRecoveryBudget recovery)
		{
			if (!want)
			{
				if (recovery != null) recovery.Record(false);
				return "restore-failed";
			}
			if (!RestoreOwnedTimer(owned, restore, recovery))
			{
				ActionFailureStore.Trip(action, "system", "Timer setting recovery after skipped apply remains incomplete");
				return "restore-failed";
			}
			if (admitted)
			{
				ActionFailureStore.CancelApply(action, "system");
			}
			return state;
		}

		private static string RebootStatus(string slot, string saved, string boot, string direction, string confirmed, string pending)
		{
			bool startsWith = saved.StartsWith("seen-" + direction + "|", StringComparison.Ordinal);
			string message = (startsWith ? "seen-" : "") + direction + "|";
			string substringResult = (saved.StartsWith(message, StringComparison.Ordinal) ? saved.Substring(message.Length) : "");
			switch (BootSessionIdentity.Compare(substringResult, boot))
			{
			case IrqRebootState.Rebooted:
				return confirmed;
			case IrqRebootState.AwaitingReboot:
				if (!startsWith)
				{
					return pending;
				}
				return "unverified";
			default:
				if (BootSessionIdentity.Valid(boot) && !BootSessionIdentity.Valid(substringResult))
				{
					string bootOffset = "seen-" + direction + "|" + boot;
					if (!Settings.SaveStrDurable(slot, bootOffset) || Settings.LoadStr(slot, "") != bootOffset)
					{
						if (!(direction == "on"))
						{
							return "restore-failed";
						}
						return "failed";
					}
				}
				return "unverified";
			}
		}
	}
}
