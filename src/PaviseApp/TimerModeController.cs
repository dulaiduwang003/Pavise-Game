using System;

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

		private int previousMode = -1;

		private long nextCheck;

		private int tickFailures;

		private int globalFailures;

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
			int num = (tickWanted ? 1 : 0) | (globalWanted ? 2 : 0);
			if (num != previousMode || now >= nextCheck || ActionFailureStore.IsRetryPending("AutoTimerTick", "system") || ActionFailureStore.IsRetryPending("AutoGlobalTimer", "system"))
			{
				previousMode = num;
				string boot;
				try
				{
					boot = Boot();
				}
				catch
				{
					boot = null;
				}
				TickState = Step("AutoTimerTickBoot", tickWanted, boot, TickOn, TickOwned, TickEnable, TickRestore);
				if (TickState == "failed" || TickState == "restore-failed")
				{
					tickFailures = Math.Min(8, tickFailures + 1);
				}
				else
				{
					tickFailures = 0;
				}
				bool flag = build > 0 && build < 19041;
				bool flag2 = build >= 20348;
				GlobalState = Step("AutoGlobalTimerBoot", globalWanted && flag2, boot, GlobalOn, GlobalOwned, GlobalEnable, GlobalRestore);
				if (GlobalState == "failed" || GlobalState == "restore-failed")
				{
					globalFailures = Math.Min(8, globalFailures + 1);
				}
				else
				{
					globalFailures = 0;
				}
				if (globalWanted && !flag2 && GlobalState != "restore-failed")
				{
					GlobalState = (flag ? "ready" : "unsupported");
				}
				bool flag3 = TickState == "failed" || TickState == "stopped" || TickState == "restore-failed" || GlobalState == "failed" || GlobalState == "stopped" || GlobalState == "restore-failed";
				int num2 = Math.Max(tickFailures, globalFailures);
				nextCheck = now + TimeSpan.FromSeconds(flag3 ? Math.Min(300, 30 * (1 << Math.Min(4, Math.Max(0, num2 - 1)))) : 300).Ticks;
			}
		}

		internal static string Step(string slot, bool want, string boot, Func<bool> read, Func<bool> owned, Func<bool> enable, Func<bool> restore)
		{
			string action = ((slot == "AutoTimerTickBoot") ? "AutoTimerTick" : ((slot == "AutoGlobalTimerBoot") ? "AutoGlobalTimer" : slot));
			bool admitted = false;
			try
			{
				string text = Settings.LoadStr(slot, "");
				if (want)
				{
					if (ActionFailureStore.IsBlocked(action, "system"))
					{
						return RestoreOwnedTimer(owned, restore) ? "stopped" : "restore-failed";
					}
					if (ActionFailureStore.IsRetryPending(action, "system") && owned() && (!restore() || owned()))
					{
						ActionFailureStore.Trip(action, "system", "Timer setting recovery incomplete before retry");
						return "restore-failed";
					}
					string reason;
					if (!ActionFailureStore.TryBeginApply(action, "system", out reason))
					{
						return "stopped";
					}
					admitted = true;
					bool flag = read();
					if (!flag)
					{
						if (!BootSessionIdentity.Valid(boot))
						{
							ActionFailureStore.CancelApply(action, "system");
							return "unverified";
						}
						string text2 = "on|" + boot;
						if (!Settings.SaveStrDurable(slot, text2) || Settings.LoadStr(slot, "") != text2)
						{
							throw new InvalidOperationException("Timer boot journal persistence failed");
						}
						text = text2;
						if (!flag && !enable())
						{
							throw new InvalidOperationException("Timer setting apply failed");
						}
					}
					if (!read())
					{
						throw new InvalidOperationException("Timer setting readback failed");
					}
					string text3 = RebootStatus(slot, text, boot, "on", "ready", "pending-reboot");
					if (text3 == "failed")
					{
						throw new InvalidOperationException("Timer observation journal persistence failed");
					}
					if (ActionFailureStore.CompleteApply(action, "system"))
					{
						return text3;
					}
					if (!RestoreOwnedTimer(owned, restore))
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
						return "restore-failed";
					}
					string text4 = "off|" + boot;
					if (!Settings.SaveStrDurable(slot, text4) || Settings.LoadStr(slot, "") != text4)
					{
						return "restore-failed";
					}
					text = text4;
					if (!restore() || owned())
					{
						return "restore-failed";
					}
				}
				if (text.StartsWith("off|", StringComparison.Ordinal) || text.StartsWith("seen-off|", StringComparison.Ordinal))
				{
					return RebootStatus(slot, text, boot, "off", "restored", "restore-pending-reboot");
				}
				return "";
			}
			catch (NotSupportedException)
			{
				return SkipTimerApply(action, want, admitted, owned, restore, "unsupported");
			}
			catch (OperationCanceledException)
			{
				return SkipTimerApply(action, want, admitted, owned, restore, "");
			}
			catch (Exception ex3)
			{
				if (!want)
				{
					return "restore-failed";
				}
				ActionFailureStore.Trip(action, "system", ex3.Message);
				if (!RestoreOwnedTimer(owned, restore))
				{
					return "restore-failed";
				}
				ActionFailureStore.CancelApply(action, "system");
				return "stopped";
			}
		}

		private static bool RestoreOwnedTimer(Func<bool> owned, Func<bool> restore)
		{
			try
			{
				return !owned() || (restore() && !owned());
			}
			catch
			{
				return false;
			}
		}

		private static string SkipTimerApply(string action, bool want, bool admitted, Func<bool> owned, Func<bool> restore, string state)
		{
			if (!want)
			{
				return "restore-failed";
			}
			if (!RestoreOwnedTimer(owned, restore))
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
			bool flag = saved.StartsWith("seen-" + direction + "|", StringComparison.Ordinal);
			string text = (flag ? "seen-" : "") + direction + "|";
			string text2 = (saved.StartsWith(text, StringComparison.Ordinal) ? saved.Substring(text.Length) : "");
			switch (BootSessionIdentity.Compare(text2, boot))
			{
			case IrqRebootState.Rebooted:
				return confirmed;
			case IrqRebootState.AwaitingReboot:
				if (!flag)
				{
					return pending;
				}
				return "unverified";
			default:
				if (BootSessionIdentity.Valid(boot) && !BootSessionIdentity.Valid(text2))
				{
					string text3 = "seen-" + direction + "|" + boot;
					if (!Settings.SaveStrDurable(slot, text3) || Settings.LoadStr(slot, "") != text3)
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
