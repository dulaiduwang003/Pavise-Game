using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
	internal static class PrivacyControl
	{
		internal const string PreferencePrefix = "PrivacySetting_";

		private static readonly object gate = new object();

		private static readonly Dictionary<string, PrivacyResult> states = new Dictionary<string, PrivacyResult>();

		private static IPrivacyBackend backend;

		private static bool working;

		private static bool closing;

		private static long retiredRecoveryAt;

		private static int retiredRecoveryFailures;

		private static string activeAction;

		internal static Func<bool> AdmissionAllowed;

		internal static bool Busy
		{
			get
			{
				lock (gate)
				{
					return working;
				}
			}
		}

		private static bool IsClosing
		{
			get
			{
				lock (gate)
				{
					return closing;
				}
			}
		}

		internal static bool IsAction(string action)
		{
			return PrivacyActionCatalog.Find(action) != null;
		}

		private static PrivacyPlan RetiredPlan()
		{
			PrivacyPlan privacyPlan = new PrivacyPlan();
			privacyPlan.Targets = new PrivacyTarget[0];
			privacyPlan.UnavailableReason = "retired";
			privacyPlan.Note = PerformancePolicy.Text("此功能已移除，仅恢复旧版本保存的原配置。", "This feature was removed. Only settings saved by an earlier version can be restored.");
			return privacyPlan;
		}

		internal static void ClearRetiredPreferences()
		{
			PrivacyAction[] retired = PrivacyActionCatalog.Retired;
			foreach (PrivacyAction privacyAction in retired)
			{
				Settings.Remove(PreferenceKey(privacyAction.Key));
			}
		}

		internal static string CanonicalTarget(string target)
		{
			if (target == null)
			{
				return null;
			}
			if (!target.StartsWith("ctx|", StringComparison.Ordinal))
			{
				return target;
			}
			string[] targetParts = target.Split(new char[1] { '|' }, 4);
			Guid result;
			if (targetParts.Length != 4 || !Guid.TryParseExact(targetParts[1], "N", out result) || result == Guid.Empty || string.IsNullOrEmpty(targetParts[2]) || string.IsNullOrEmpty(targetParts[3]) || targetParts[3].StartsWith("ctx|", StringComparison.Ordinal))
			{
				throw new FormatException("Invalid privacy target context");
			}
			return targetParts[3];
		}

		private static IPrivacyBackend EnsureBackend()
		{
			lock (gate)
			{
				if (backend == null)
				{
					backend = new PrivacyNativeBackend();
				}
				return backend;
			}
		}

		internal static void Bind(IPrivacyBackend value)
		{
			if (value == null)
			{
				throw new ArgumentNullException("value");
			}
			lock (gate)
			{
				if (working)
				{
					throw new InvalidOperationException("Privacy work is running");
				}
				backend = value;
			}
		}

		private static string PreferenceKey(string action)
		{
			return "PrivacySetting_" + action;
		}

		internal static bool PreferenceEnabled(string action)
		{
			return PreferenceEnabled(action, "");
		}

		internal static bool PreferenceEnabled(string action, string selectedTarget)
		{
			string value;
			if (!PrivacyActionCatalog.IsRetired(action) && Settings.TryLoadStr(PreferenceKey(action), out value))
			{
				return value == "1";
			}
			return false;
		}

		private static bool SavePreference(string action, bool enabled)
		{
			string message = (enabled ? "1" : "0");
			string value;
			if (Settings.SaveStrDurable(PreferenceKey(action), message) && Settings.TryLoadStr(PreferenceKey(action), out value))
			{
				return value == message;
			}
			return false;
		}

		private static PrivacyResult Copy(PrivacyResult r)
		{
			PrivacyResult privacyResult = new PrivacyResult();
			privacyResult.State = r.State;
			privacyResult.Detail = r.Detail;
			privacyResult.SelectedTarget = r.SelectedTarget;
			privacyResult.UnavailableReason = r.UnavailableReason;
			privacyResult.Configured = r.Configured;
			privacyResult.Busy = r.Busy;
			privacyResult.Limited = r.Limited;
			privacyResult.Supported = r.Supported;
			privacyResult.Managed = r.Managed;
			privacyResult.RestartRequired = r.RestartRequired;
			privacyResult.HasRecovery = r.HasRecovery;
			return privacyResult;
		}

		internal static PrivacyResult State(string action)
		{
			return State(action, "");
		}

		internal static PrivacyResult State(string action, string selectedTarget)
		{
			PrivacyResult value;
			lock (gate)
			{
				if (!states.TryGetValue(action ?? "", out value) || !string.Equals(value.SelectedTarget ?? "", selectedTarget ?? "", StringComparison.OrdinalIgnoreCase))
				{
					PrivacyResult privacyResult = new PrivacyResult();
					privacyResult.State = "unverified";
					privacyResult.Detail = "";
					privacyResult.SelectedTarget = selectedTarget ?? "";
					value = privacyResult;
				}
				value = Copy(value);
				value.Busy = working;
				if (working && activeAction == action)
				{
					value.State = "working";
				}
			}
			value.Configured = PreferenceEnabled(action, selectedTarget);
			return value;
		}

		private static bool HasRecovery(string action)
		{
			try
			{
				foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
				{
					if (item.Id == action)
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

		private static bool RequiresReboot(string action)
		{
			return action == PrivacyActionCatalog.NtfsMemory;
		}

		internal static string BackgroundAppRestartState(string state, string phase)
		{
			if (phase == "apply" && (state == "configured" || state == "partial"))
				return "pending-app-restart";
			if (phase == "restore" && (state == "restored" || state == "ready"))
				return "restore-pending-app-restart";
			return state;
		}

		private static string RebootState(string action, string state)
		{
			if (action == PrivacyActionCatalog.BackgroundGlobal)
			{
				// This state requests application restart; it does not prove that affected processes restarted.
				string phase;
				Settings.TryLoadStr(PreferencePrefix + action + "Phase", out phase);
				return BackgroundAppRestartState(state, phase);
			}
			if (!RequiresReboot(action))
			{
				return state;
			}
			switch (state)
			{
			case "stopped":
			case "managed":
			case "unsupported":
			case "unverified":
				return state;
			default:
			{
				string value;
				string value2;
				if (!Settings.TryLoadStr("PrivacySetting_" + action + "Phase", out value) || !Settings.TryLoadStr("PrivacySetting_" + action + "Boot", out value2))
				{
					return "unverified";
				}
				if (string.IsNullOrEmpty(value) && string.IsNullOrEmpty(value2))
				{
					return state;
				}
				if (value != "apply" && value != "restore")
				{
					return "unverified";
				}
				IrqRebootState irqRebootState = BootSessionIdentity.Compare(value2, CurrentBoot());
				if (irqRebootState == IrqRebootState.Unknown)
				{
					return "unverified";
				}
				if (value == "restore" && (state == "ready" || state == "restored"))
				{
					if (irqRebootState != IrqRebootState.AwaitingReboot)
					{
						return "restored";
					}
					return "restore-pending-reboot";
				}
				if (value == "apply" && (state == "configured" || state == "partial"))
				{
					if (irqRebootState != IrqRebootState.AwaitingReboot)
					{
						return state;
					}
					return "pending-reboot";
				}
				return state;
			}
			}
		}

		private static void SetResult(string action, string selectedTarget, PrivacyPlan plan, string state, string detail)
		{
			bool setResultReady = false;
			if (plan != null && plan.Targets != null)
			{
				PrivacyTarget[] targets = plan.Targets;
				foreach (PrivacyTarget privacyTarget in targets)
				{
					setResultReady |= privacyTarget.RestartRequired;
				}
			}
			bool hasRecovery = HasRecovery(action);
			lock (gate)
			{
				states[action] = new PrivacyResult
				{
					State = state,
					Detail = (detail ?? ""),
					SelectedTarget = (selectedTarget ?? ""),
					HasRecovery = hasRecovery,
					UnavailableReason = ((plan == null) ? "" : plan.UnavailableReason),
					Limited = (plan != null && plan.Limited),
					Supported = (plan != null && plan.Supported),
					Managed = (plan != null && plan.Managed),
					RestartRequired = setResultReady
				};
			}
		}

		private static bool MayApply()
		{
			try
			{
				return AdmissionAllowed == null || AdmissionAllowed();
			}
			catch (Exception ex)
			{
				Logger.Warn("Privacy admission | " + ex.Message);
				return false;
			}
		}

		private static bool Queue(string action, WaitCallback work)
		{
			lock (gate)
			{
				if (closing || working || !IsAction(action))
				{
					return false;
				}
				working = true;
				activeAction = action;
			}
			bool queueSucceeded = false;
			try
			{
				queueSucceeded = ThreadPool.QueueUserWorkItem(work);
			}
			catch (Exception ex)
			{
				Logger.Warn("Privacy queue | " + ex.Message);
			}
			finally
			{
				if (!queueSucceeded)
				{
					Finish();
				}
			}
			return queueSucceeded;
		}

		private static void Finish()
		{
			lock (gate)
			{
				working = false;
				activeAction = null;
				Monitor.PulseAll(gate);
			}
		}

		private static void Complete(Action callback)
		{
			if (callback != null)
			{
				try
				{
					callback();
				}
				catch (Exception ex)
				{
					Logger.Warn("Privacy completion | " + ex.Message);
				}
			}
		}

		internal static bool Refresh(string action, string selectedTarget)
		{
			return Refresh(action, selectedTarget, null);
		}

		internal static bool Refresh(string action, string selectedTarget, Action<PrivacyPlan> completed)
		{
			return Queue(action, delegate
			{
				PrivacyPlan privacyPlan = null;
				try
				{
					lock (SystemConfigurationGate.Sync)
					{
						if (PrivacyActionCatalog.IsRetired(action))
						{
							privacyPlan = RetiredPlan();
							SetResult(action, selectedTarget, privacyPlan, "unsupported", privacyPlan.Note);
						}
						else
						{
							privacyPlan = EnsureBackend().Inspect(action, selectedTarget);
							Validate(privacyPlan);
							string state = RebootState(action, InspectState(action, privacyPlan));
							SetResult(action, selectedTarget, privacyPlan, state, privacyPlan.Note);
						}
					}
				}
				catch (Exception ex)
				{
					SetResult(action, selectedTarget, privacyPlan, "unverified", ex.Message);
				}
				finally
				{
					Finish();
					if (completed != null)
					{
						try
						{
							completed(privacyPlan);
						}
						catch (Exception ex2)
						{
							Logger.Warn("Privacy inspection completion | " + ex2.Message);
						}
					}
				}
			});
		}

		private static void Validate(PrivacyPlan plan)
		{
			if (plan == null)
			{
				throw new InvalidOperationException("Privacy inspection returned no evidence");
			}
			if (!plan.Supported || plan.Managed)
			{
				return;
			}
			if (plan.Targets == null || plan.Targets.Length == 0)
			{
				throw new InvalidOperationException("Privacy inspection returned no targets");
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			PrivacyTarget[] targets = plan.Targets;
			foreach (PrivacyTarget privacyTarget in targets)
			{
				if (privacyTarget == null || string.IsNullOrEmpty(privacyTarget.Key) || privacyTarget.Wanted == null || !hashSet.Add(privacyTarget.Key))
				{
					throw new InvalidOperationException("Invalid or duplicate privacy target");
				}
			}
		}

		private static string InspectState(string action, PrivacyPlan plan)
		{
			if (plan.Managed)
			{
				return "managed";
			}
			if (!plan.Supported)
			{
				return "unsupported";
			}
			bool inspectStateReady = true;
			bool inspectStateReady2 = false;
			bool inspectStateReady3 = false;
			PrivacyTarget[] targets = plan.Targets;
			foreach (PrivacyTarget privacyTarget in targets)
			{
				inspectStateReady3 |= ActionFailureStore.IsIsolated(action, privacyTarget.Key);
				bool satisfiedSucceeded = Satisfied(action, privacyTarget.Key, ReadTarget(action, privacyTarget.Key), privacyTarget.Wanted);
				inspectStateReady = inspectStateReady && satisfiedSucceeded;
				inspectStateReady2 = inspectStateReady2 || satisfiedSucceeded;
			}
			if (inspectStateReady3)
			{
				return "stopped";
			}
			if (inspectStateReady)
			{
				if (!plan.Limited)
				{
					return "configured";
				}
				return "partial";
			}
			if (!inspectStateReady2)
			{
				return "ready";
			}
			return "partial";
		}

		internal static bool Set(string action, bool enabled, string selectedTarget, Action completed)
		{
			if (enabled && PrivacyActionCatalog.IsRetired(action))
			{
				return false;
			}
			if (!MayApply())
			{
				return false;
			}
			return Queue(action, delegate
			{
				PrivacyPlan privacyPlan = null;
				try
				{
					lock (SystemConfigurationGate.Sync)
					{
						if (IsClosing)
						{
							throw new OperationCanceledException("Privacy work cancelled before inspection");
						}
						if (!MayApply())
						{
							throw new OperationCanceledException("A match or reset began before privacy work");
						}
						if (!enabled)
						{
							if (!RestoreAction(action, selectedTarget))
							{
								SetResult(action, selectedTarget, null, "restore-failed", "Original settings remain pending recovery");
							}
							else
							{
								if (!SavePreference(action, false))
								{
									throw new InvalidOperationException("Could not persist restored preference");
								}
								PrivacyPlan plan = (RequiresReboot(action) ? new PrivacyPlan
								{
									Supported = true,
									Targets = new PrivacyTarget[1]
									{
										new PrivacyTarget
										{
											RestartRequired = true
										}
									}
								} : (action == PrivacyActionCatalog.BackgroundGlobal ? new PrivacyPlan
								{
									Supported = true,
									Targets = new PrivacyTarget[0]
								} : null));
								SetResult(action, selectedTarget, plan, RebootState(action, "restored"), "");
							}
						}
						else if (PrivacyActionCatalog.IsRetired(action))
						{
							privacyPlan = RetiredPlan();
							SetResult(action, selectedTarget, privacyPlan, "unsupported", privacyPlan.Note);
						}
						else
						{
							privacyPlan = EnsureBackend().Inspect(action, selectedTarget);
							Validate(privacyPlan);
							if (privacyPlan.Managed || !privacyPlan.Supported)
							{
								SetResult(action, selectedTarget, privacyPlan, privacyPlan.Managed ? "managed" : "unsupported", privacyPlan.Note);
							}
							else
							{
								Apply(action, selectedTarget, privacyPlan);
							}
						}
					}
				}
				catch (OperationCanceledException ex)
				{
					SetResult(action, selectedTarget, privacyPlan, "unverified", ex.Message);
				}
				catch (Exception ex2)
				{
					SetResult(action, selectedTarget, privacyPlan, "unverified", ex2.Message);
					Logger.Warn("Privacy | " + action + " | " + ex2.Message);
				}
				finally
				{
					Finish();
					Complete(completed);
				}
			});
		}

		private static void Apply(string action, string selectedTarget, PrivacyPlan plan)
		{
			if (PrivacyActionCatalog.IsRetired(action))
			{
				plan = RetiredPlan();
				SetResult(action, selectedTarget, plan, "unsupported", plan.Note);
				return;
			}
			if (action == "EnvPrivacyNtfsMemoryV1" && !EnvironmentEnhancementEligibility.Allowed)
			{
				plan.Supported = false;
				plan.UnavailableReason = "memory";
				plan.Note = EnvironmentEnhancementEligibility.Description;
				SetResult(action, selectedTarget, plan, "unsupported", plan.Note);
				return;
			}
			List<string> list = new List<string>();
			bool applyReady = false;
			string target2 = null;
			try
			{
				PrivacyTarget[] targets = plan.Targets;
				foreach (PrivacyTarget privacyTarget in targets)
				{
					if (!PerformanceTuning.RestoreWithBudget(new PerformanceActionStore.Receipt { Id = "privacy-read:" + action, Target = privacyTarget.Key, Expected = privacyTarget.Wanted }, 3,
						delegate { ReadTarget(action, privacyTarget.Key); return true; }))
					{
						SetResult(action, selectedTarget, plan, "stopped", "Privacy source read budget exhausted");
						return;
					}
				}
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				bool applyReady2 = false;
				PrivacyTarget[] targets2 = plan.Targets;
				foreach (PrivacyTarget privacyTarget2 in targets2)
				{
					hashSet.Add(privacyTarget2.Key);
				}
				PrivacyTarget[] targets3 = plan.Targets;
				foreach (PrivacyTarget privacyTarget3 in targets3)
				{
					ActionFailureStore.BeginAutomaticCycle(action, privacyTarget3.Key, PerformanceTuning.RecoveryCycleSerial + "\n" + privacyTarget3.Wanted);
					applyReady2 |= ActionFailureStore.IsIsolated(action, privacyTarget3.Key);
				}
				List<PerformanceActionStore.Receipt> list2 = PerformanceActionStore.All();
				list2.Reverse();
				foreach (PerformanceActionStore.Receipt item in list2)
				{
					if (item.Id == action && (applyReady2 || !hashSet.Contains(item.Target)) && !RestoreReceipt(item))
					{
						target2 = item.Target;
						throw new InvalidOperationException("Recovery incomplete before privacy apply");
					}
				}
				// A fault can block new writes, but never the recovery of values we own.
				foreach (PrivacyTarget privacyTarget in plan.Targets)
				{
					ActionFailureStore.BeginAutomaticCycle(action, privacyTarget.Key, PerformanceTuning.RecoveryCycleSerial + "\n" + privacyTarget.Wanted);
					if (ActionFailureStore.IsBlocked(action, privacyTarget.Key))
					{
						SetResult(action, selectedTarget, plan, "stopped", ActionFailureStore.FailureReason(action, privacyTarget.Key));
						return;
					}
				}
				if (!SavePreference(action, true))
				{
					throw new InvalidOperationException("Could not persist privacy preference; no native change attempted");
				}
				PrivacyTarget[] targets4 = plan.Targets;
				foreach (PrivacyTarget privacyTarget4 in targets4)
				{
					if (IsClosing || !MayApply())
					{
						throw new OperationCanceledException("Privacy work cancelled");
					}
					string reason;
					if (!ActionFailureStore.TryBeginApply(action, privacyTarget4.Key, out reason))
					{
						SetResult(action, selectedTarget, plan, "stopped", reason);
						return;
					}
					list.Add(privacyTarget4.Key);
				}
				PrivacyTarget[] targets5 = plan.Targets;
				foreach (PrivacyTarget target in targets5)
				{
					target2 = target.Key;
					if (IsClosing || !MayApply())
					{
						throw new OperationCanceledException("Privacy work cancelled");
					}
					string wanted = target.Wanted;
					bool targetWriteAttempted = false;
					if (!PerformanceActionStore.Change(action, target.Key, wanted, delegate
					{
						try
						{
							return ReadTarget(action, target.Key);
						}
						catch (Exception innerException3)
						{
							if (targetWriteAttempted)
							{
								throw new InvalidOperationException("Post-write privacy verification unavailable", innerException3);
							}
							throw;
						}
					}, delegate(string value)
					{
						targetWriteAttempted = true;
						return WriteTarget(action, target.Key, value);
					}, delegate(string value)
					{
						try
						{
							return Satisfied(action, target.Key, value, wanted);
						}
						catch (Exception innerException2)
						{
							if (targetWriteAttempted)
							{
								throw new InvalidOperationException("Post-write privacy verification unavailable", innerException2);
							}
							throw;
						}
					}))
					{
						throw new InvalidOperationException(PerformanceActionStore.LastChangeFailure ?? "Privacy change failed");
					}
				}
				if (IsClosing || !MayApply())
				{
					throw new OperationCanceledException("Privacy work cancelled after writing");
				}
				PrivacyTarget[] targets6 = plan.Targets;
				foreach (PrivacyTarget privacyTarget5 in targets6)
				{
					target2 = privacyTarget5.Key;
					try
					{
						if (!Satisfied(action, privacyTarget5.Key, ReadTarget(action, privacyTarget5.Key), privacyTarget5.Wanted))
						{
							throw new InvalidOperationException("Privacy group changed before completion");
						}
					}
					catch (Exception innerException)
					{
						throw new InvalidOperationException("Post-write privacy group verification unavailable", innerException);
					}
				}
				if (IsClosing || !MayApply())
				{
					throw new OperationCanceledException("Privacy work cancelled during final verification");
				}
				foreach (string item2 in list)
				{
					target2 = item2;
					if (!ActionFailureStore.CompleteApply(action, item2))
					{
						throw new InvalidOperationException("Privacy completion could not be persisted");
					}
				}
				applyReady = true;
				bool applyReady3 = false;
				PrivacyTarget[] targets7 = plan.Targets;
				foreach (PrivacyTarget privacyTarget6 in targets7)
				{
					applyReady3 |= privacyTarget6.RestartRequired;
				}
				string state = (applyReady3 ? "pending-reboot" : (plan.Limited ? "partial" : "configured"));
				if (RequiresReboot(action) || action == PrivacyActionCatalog.BackgroundGlobal)
				{
					state = RebootState(action, plan.Limited ? "partial" : "configured");
				}
				SetResult(action, selectedTarget, plan, state, plan.Note);
			}
			catch (OperationCanceledException ex)
			{
				bool restoreActionSucceeded = RestoreAction(action, selectedTarget);
				if (!restoreActionSucceeded && target2 != null)
				{
					ActionFailureStore.Trip(action, target2, "Recovery incomplete after cancelled privacy change");
				}
				SetResult(action, selectedTarget, plan, restoreActionSucceeded ? "unverified" : "restore-failed", ex.Message);
			}
			catch (NotSupportedException ex2)
			{
				bool restoreActionSucceeded2 = RestoreAction(action, selectedTarget);
				if (!restoreActionSucceeded2 && target2 != null)
				{
					ActionFailureStore.Trip(action, target2, "Recovery incomplete after unsupported privacy change");
				}
				SetResult(action, selectedTarget, plan, restoreActionSucceeded2 ? "unsupported" : "restore-failed", ex2.Message);
			}
			catch (Exception ex3)
			{
				if (target2 != null)
				{
					ActionFailureStore.Trip(action, target2, ex3.Message);
				}
				bool restoreActionSucceeded3 = RestoreAction(action, selectedTarget);
				SetResult(action, selectedTarget, plan, (!restoreActionSucceeded3) ? "restore-failed" : ((target2 == null) ? "unverified" : "stopped"), ex3.Message);
				Logger.Warn("Privacy | " + action + " | " + ex3.Message);
			}
			finally
			{
				if (!applyReady)
				{
					foreach (string item3 in list)
					{
						bool owned = true;
						try { owned = PerformanceActionStore.All().Exists(r => r.Id == action && r.Target == item3); } catch { }
						if (!owned) ActionFailureStore.CancelApply(action, item3);
					}
				}
			}
		}

		internal static string ReadTarget(string action, string target)
		{
			if (!IsAction(action) || string.IsNullOrEmpty(target))
			{
				throw new InvalidOperationException("Unknown privacy target");
			}
			string readResult = EnsureBackend().Read(action, target);
			if (readResult == null)
			{
				throw new InvalidOperationException("Privacy setting is unreadable");
			}
			return readResult;
		}

		internal static bool WriteTarget(string action, string target, string value)
		{
			return WriteTarget(action, target, value, false);
		}

		private static bool WriteTarget(string action, string target, string value, bool restoring)
		{
			if (!IsAction(action) || string.IsNullOrEmpty(target) || value == null)
			{
				return false;
			}
			if (!restoring && PrivacyActionCatalog.IsRetired(action))
			{
				return false;
			}
			if (!restoring && action == "EnvPrivacyNtfsMemoryV1" && !EnvironmentEnhancementEligibility.Allowed)
			{
				throw new NotSupportedException(EnvironmentEnhancementEligibility.Description);
			}
			TrackRebootWrite(action, restoring);
			return EnsureBackend().Write(action, target, value);
		}

		internal static void TrackRebootWrite(string action, bool restoring)
		{
			if (action == PrivacyActionCatalog.BackgroundGlobal)
			{
				string phase = restoring ? "restore" : "apply";
				string stored;
				if (!Settings.SaveStrDurable(PreferencePrefix + action + "Phase", phase)
					|| !Settings.TryLoadStr(PreferencePrefix + action + "Phase", out stored) || stored != phase)
					throw new InvalidOperationException("App restart reminder could not be persisted");
				return;
			}
			if (RequiresReboot(action))
			{
				string currentBootResult = CurrentBoot();
				if (!BootSessionIdentity.Valid(currentBootResult))
				{
					throw new InvalidOperationException("Boot identity unavailable; restart requirement cannot be tracked");
				}
				string message = (restoring ? "restore" : "apply");
				string value;
				if (!Settings.SaveStrDurable("PrivacySetting_" + action + "Boot", currentBootResult) || !Settings.TryLoadStr("PrivacySetting_" + action + "Boot", out value) || value != currentBootResult || !Settings.SaveStrDurable("PrivacySetting_" + action + "Phase", message) || !Settings.TryLoadStr("PrivacySetting_" + action + "Phase", out value) || value != message)
				{
					throw new InvalidOperationException("Reboot tracking could not be persisted");
				}
			}
		}

		private static string CurrentBoot()
		{
			return BootSessionIdentity.Read();
		}

		private static bool Satisfied(string action, string target, string current, string wanted)
		{
			IPrivacyRecoveryBackend privacyRecoveryBackend = EnsureBackend() as IPrivacyRecoveryBackend;
			if (privacyRecoveryBackend != null)
			{
				return privacyRecoveryBackend.Satisfied(action, target, current, wanted);
			}
			return current == wanted;
		}

		internal static bool RestoreReceipt(PerformanceActionStore.Receipt receipt)
		{
			bool restored = PerformanceTuning.RestoreWithBudget(receipt, 3, () => RestoreReceiptCore(receipt));
			if (restored) ActionFailureStore.CancelApply(receipt.Id, receipt.Target);
			return restored;
		}

		private static bool RestoreReceiptCore(PerformanceActionStore.Receipt receipt)
		{
			if (receipt == null || !IsAction(receipt.Id))
			{
				return false;
			}
			try
			{
				lock (SystemConfigurationGate.Sync)
				{
					string canonicalTargetResult = CanonicalTarget(receipt.Target);
					if ((receipt.Id == "EnvPrivacyServiceV1" && canonicalTargetResult == "svc|DiagTrack") || (receipt.Id == "EnvPrivacyCompatibilityV1" && canonicalTargetResult == "svc|PcaSvc"))
					{
						string value2;
						if (!Settings.TryLoadStr(PerformanceActionStore.Slot(receipt.Id, receipt.Target), out value2))
						{
							return false;
						}
						if (string.IsNullOrEmpty(value2))
						{
							return true;
						}
						if (value2 != PerformanceActionStore.Pack(receipt))
						{
							return false;
						}
						string b = receipt.Target.Substring(0, receipt.Target.Length - canonicalTargetResult.Length) + "start|" + canonicalTargetResult.Substring(4);
						foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
						{
							if (item.Id == receipt.Id && string.Equals(item.Target, b, StringComparison.OrdinalIgnoreCase) && !RestoreReceipt(item))
							{
								return false;
							}
						}
					}
					IPrivacyRecoveryBackend privacyRecoveryBackend = EnsureBackend() as IPrivacyRecoveryBackend;
					if (privacyRecoveryBackend != null)
					{
						return privacyRecoveryBackend.Restore(receipt);
					}
					return PerformanceActionStore.Restore(receipt, () => ReadTarget(receipt.Id, receipt.Target), (string value) => WriteTarget(receipt.Id, receipt.Target, value, true), (string value) => value == receipt.Expected);
				}
			}
			catch (Exception ex)
			{
				Logger.Warn("Privacy restore | " + ex.Message);
				return false;
			}
		}

		private static bool RestoreAction(string action, string selectedTarget)
		{
			bool restoreActionSucceeded = true;
			try
			{
				List<PerformanceActionStore.Receipt> list = PerformanceActionStore.All();
				list.Reverse();
				foreach (PerformanceActionStore.Receipt item in list)
				{
					if (item.Id == action)
					{
						restoreActionSucceeded &= RestoreReceipt(item);
					}
				}
				return restoreActionSucceeded;
			}
			catch (Exception ex)
			{
				Logger.Warn("Privacy restore | " + ex.Message);
				return false;
			}
		}

		private static bool RecoveryAdmitted(Func<bool> mayContinue)
		{
			try
			{
				return mayContinue != null && mayContinue();
			}
			catch
			{
				return false;
			}
		}

		internal static void BeginRecoveryCycle(long serial)
		{
			lock (gate) { retiredRecoveryFailures = 0; retiredRecoveryAt = 0; }
		}

		internal static bool TryRestoreRetired(Func<bool> mayContinue)
		{
			if (!RecoveryAdmitted(mayContinue))
			{
				return false;
			}
			lock (gate)
			{
				if (closing || working || retiredRecoveryFailures >= 5 || DateTime.UtcNow.Ticks < retiredRecoveryAt)
				{
					return false;
				}
				retiredRecoveryAt = DateTime.UtcNow.AddMinutes(1.0).Ticks;
			}
			return Queue("EnvPrivacyServiceV1", delegate
			{
				bool ready = true;
				try
				{
					lock (SystemConfigurationGate.Sync)
					{
						if (IsClosing || !RecoveryAdmitted(mayContinue))
						{
							return;
						}
						List<PerformanceActionStore.Receipt> list = PerformanceActionStore.All();
						list.Reverse();
						foreach (PerformanceActionStore.Receipt item in list)
						{
							if (PrivacyActionCatalog.IsRetired(item.Id))
							{
								if (IsClosing || !RecoveryAdmitted(mayContinue))
								{
									break;
								}
								bool restoreReceiptSucceeded = RestoreReceipt(item);
								ready = ready && restoreReceiptSucceeded;
								SetResult(item.Id, "", RetiredPlan(), restoreReceiptSucceeded ? "restored" : "restore-failed", restoreReceiptSucceeded ? "" : PerformancePolicy.Text("旧配置恢复未完成，记录已保留。", "Legacy settings remain pending restoration; records are retained."));
							}
						}
					}
				}
				catch (Exception ex)
				{
					ready = false;
					Logger.Warn("Retired environment recovery | " + ex.Message);
				}
				finally
				{
					lock (gate)
					{
						if (!ready) retiredRecoveryFailures = Math.Min(5, retiredRecoveryFailures + 1);
						retiredRecoveryAt = DateTime.UtcNow.AddSeconds(ready ? 60 : Math.Min(300, 30 * (1 << retiredRecoveryFailures))).Ticks;
					}
					Finish();
				}
			});
		}

		internal static bool RestoreAll(out string failure)
		{
			failure = null;
			lock (gate)
			{
				if (working)
				{
					failure = "Privacy work is running";
					return false;
				}
				working = true;
				activeAction = null;
			}
			try
			{
				lock (SystemConfigurationGate.Sync)
				{
					bool restoreAllSucceeded = true;
					List<PerformanceActionStore.Receipt> list = PerformanceActionStore.All();
					list.Reverse();
					foreach (PerformanceActionStore.Receipt item in list)
					{
						if (IsAction(item.Id))
						{
							restoreAllSucceeded &= RestoreReceipt(item);
						}
					}
					if (!restoreAllSucceeded)
					{
						failure = "Privacy originals remain pending recovery";
					}
					return restoreAllSucceeded;
				}
			}
			catch (Exception ex)
			{
				failure = ex.Message;
				return false;
			}
			finally
			{
				Finish();
			}
		}

		internal static bool CloseWork(int timeoutMilliseconds)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			int timeoutMillisecondsLimit = Math.Max(0, timeoutMilliseconds);
			lock (gate)
			{
				closing = true;
				while (working)
				{
					int lowerBound = (int)Math.Max(0L, timeoutMillisecondsLimit - stopwatch.ElapsedMilliseconds);
					if (lowerBound == 0)
					{
						return false;
					}
					Monitor.Wait(gate, lowerBound);
				}
				return true;
			}
		}
	}
}
