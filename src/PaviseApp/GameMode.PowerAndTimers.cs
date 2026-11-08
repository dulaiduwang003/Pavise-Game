using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal partial class GameMode
	{
		private bool ShouldLogAwakeTransition(string state, string previous)
		{
			if (state == "failed")
			{
				awakeRecoveryState = "applied";
				return true;
			}
			if (state == "restore-failed")
			{
				awakeRecoveryState = "restored";
				return true;
			}
			if (state != "applied" && state != "restored")
			{
				return true;
			}
			bool matchesAutomationSessionEnding = state == "restored" && automationSessionEnding;
			bool matchesAwakeRecoveryState = awakeRecoveryState == state;
			if (matchesAwakeRecoveryState)
			{
				awakeRecoveryState = null;
			}
			if (matchesAutomationSessionEnding && awakeFinalRestoreLogged && !matchesAwakeRecoveryState)
			{
				return false;
			}
			long ticks = DateTime.UtcNow.Ticks;
			long awakeAppliedLogTicksSnapshot = ((state == "applied") ? awakeAppliedLogTicks : awakeRestoredLogTicks);
			if (!matchesAutomationSessionEnding && !matchesAwakeRecoveryState && awakeAppliedLogTicksSnapshot != 0 && ticks >= awakeAppliedLogTicksSnapshot && ticks - awakeAppliedLogTicksSnapshot < 600000000)
			{
				return false;
			}
			if (state == "applied")
			{
				awakeAppliedLogTicks = ticks;
			}
			else
			{
				awakeRestoredLogTicks = ticks;
			}
			if (matchesAutomationSessionEnding)
			{
				awakeFinalRestoreLogged = true;
			}
			return true;
		}
		internal static int PowerPolicyKey(bool processorTuning, bool usePlan)
		{
			return (processorTuning ? 1 : 0) | (usePlan ? 2 : 0);
		}
		private void RollbackPowerPlan(string reason)
		{
			Interlocked.Exchange(ref laptopPerfPowerGeneration, -1);
			bool runAutomaticRestoreSucceeded = RunAutomaticRestore("powerplan", () => RestoreLaptopPerformanceForPowerPlan() && RestorePowerPlan());
			lock (sync)
			{
				if (runAutomaticRestoreSucceeded)
				{
					envRestorePending.Remove("powerplan");
					envNextAttempt.Remove("powerplan");
					envFailures.Remove("powerplan");
				}
				else
				{
					envRestorePending.Add("powerplan");
				}
			}
			if (runAutomaticRestoreSucceeded)
			{
				ActionFailureStore.CancelApply("PowerPlanOn", "system");
			}
			else
			{
				ActionFailureStore.Trip("PowerPlanOn", "system", reason + PerformancePolicy.Text("；电源计划回退失败", "; power-plan rollback failed"));
			}
			planActive = !runAutomaticRestoreSucceeded;
			if (runAutomaticRestoreSucceeded)
			{
				lastPowerPolicyKey = -1;
			}
			RecordAutomation("PowerPlanOn", PowerPlanExecutionState);
		}
		private bool PowerPlanStopped(int key)
		{
			AutomaticFailurePhase phase = PowerAttemptPhase(key);
			bool isBlocked = ActionFailureStore.IsBlocked("PowerPlanOn", "system");
			lock (sync)
			{
				isBlocked |= phase.Stopped || phase.Attempts >= 3 || DateTime.UtcNow.Ticks < phase.Next;
				if (isBlocked)
				{
					powerStoppedKeys.Add(key);
				}
				else
				{
					powerStoppedKeys.Remove(key);
				}
			}
			if (isBlocked) RecordAutomation("PowerPlanOn", PowerPlanExecutionState);
			return isBlocked;
		}
		internal void NotifyPowerSchemeChanged()
		{
			bool powerPlanApplySessionCurrentSucceeded;
			lock (sync)
			{
				powerPlanApplySessionCurrentSucceeded = PowerPlanApplySessionCurrent(Volatile.Read(ref powerSessionGen));
			}
			if (powerPlanApplySessionCurrentSucceeded)
			{
				Interlocked.Exchange(ref powerPlanNotificationPending, 1);
				QueuePowerPlanNotificationAudit();
			}
		}
		private void QueuePowerPlanNotificationAudit()
		{
			if (!AutomaticWorkAllowed("power-queue")) return;
			if (Interlocked.CompareExchange(ref powerApplyInFlight, 1, 0) != 0)
			{
				return;
			}
			int keyShot;
			int genShot;
			lock (sync)
			{
				keyShot = lastPowerPolicyKey;
				genShot = Volatile.Read(ref powerSessionGen);
				if (!PowerPlanApplySessionCurrent(genShot) || keyShot < 0 || (keyShot & 2) == 0 || PowerPlanStopped(keyShot) || (planFailStreak > 0 && DateTime.UtcNow.Ticks < Interlocked.Read(ref nextPowerAuditTicks)))
				{
					Interlocked.Exchange(ref powerPlanNotificationPending, 0);
					Interlocked.Exchange(ref powerApplyInFlight, 0);
					return;
				}
			}
			Interlocked.Exchange(ref powerPlanNotificationPending, 0);
			bool queuePowerPlanNotificationAuditReady = false;
			try
			{
				queuePowerPlanNotificationAuditReady = ThreadPool.QueueUserWorkItem(delegate
				{
					RunPowerPlanNotificationAudit(genShot, keyShot);
				});
			}
			catch
			{
			}
			if (!queuePowerPlanNotificationAuditReady)
			{
				RecordAutomaticWorkFailure("power-queue", "notification-queue-rejected");
				Interlocked.Exchange(ref powerPlanNotificationPending, 1);
				Interlocked.Exchange(ref powerApplyInFlight, 0);
			}
		}
		private bool PowerPlanNotificationStillCurrent(int genShot, int keyShot)
		{
			if (PowerPlanNotificationSessionCurrent(genShot, keyShot))
			{
				return !PowerPlanStopped(keyShot);
			}
			return false;
		}
		private bool PowerPlanNotificationSessionCurrent(int genShot, int keyShot)
		{
			lock (sync)
			{
				return PowerPlanApplySessionCurrent(genShot) && lastPowerPolicyKey == keyShot;
			}
		}
		private void InvalidatePowerPlanSession()
		{
			Interlocked.Exchange(ref laptopPerfPowerGeneration, -1);
			Interlocked.Increment(ref powerSessionGen);
			Interlocked.Exchange(ref powerPlanNotificationPending, 0);
			Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
		}
		private bool PowerPlanApplySessionCurrent(int genShot)
		{
			lock (sync)
			{
				return enabled && active && planActive && !stopping && !panicReq && !powerSessionEnding && !ProfileStoreSaveFailed
					&& gameGoneSinceTicks == 0 && !PowerPlan.NoSwitchSelected && Volatile.Read(ref powerSessionGen) == genShot;
			}
		}
		private void RunPowerPlanNotificationAudit(int genShot, int keyShot)
		{
			try
			{
				lock (powerApplyGate)
				{
					string reason;
					if (!PowerPlanNotificationStillCurrent(genShot, keyShot) || PowerPlan.EnforceSettled((keyShot & 1) != 0) || !BeginPowerAttempt(keyShot, out reason))
					{
						return;
					}
					bool skipped;
					bool runPowerPlanActionSucceeded = RunPowerPlanAction(delegate
					{
						if (!PowerPlanNotificationSessionCurrent(genShot, keyShot))
						{
							throw new OperationCanceledException();
						}
						return PowerPlan.Enforce((keyShot & 1) != 0);
					}, out skipped, out reason);
					if (skipped)
					{
						RollbackPowerPlan(PerformancePolicy.Text("电源计划应用已跳过", "Power-plan apply was skipped"));
						CompletePowerAttempt(false, "stage=apply-skipped");
					}
					else if (!PowerPlanNotificationSessionCurrent(genShot, keyShot))
					{
						if (!runPowerPlanActionSucceeded)
						{
							ActionFailureStore.Trip("PowerPlanOn", "system", reason);
						}
							RollbackPowerPlan(runPowerPlanActionSucceeded ? "stage=session-cancelled" : reason + " phase=session-cancelled");
						CompletePowerAttempt(false, "stage=session-cancelled");
					}
					else
					{
						OnPowerPlanAppliedWithReason(runPowerPlanActionSucceeded, reason, genShot);
					}
				}
			}
			catch
			{
			}
			finally
			{
				lock (powerApplyGate)
				{
					if (powerAttemptInProgress != null)
					{
						RollbackPowerPlan("stage=attempt-interrupted");
						CompletePowerAttempt(false, "stage=attempt-interrupted");
					}
				}
				Interlocked.Exchange(ref powerApplyInFlight, 0);
				if (Interlocked.CompareExchange(ref powerPlanNotificationPending, 0, 0) != 0)
				{
					QueuePowerPlanNotificationAudit();
				}
			}
		}
		private bool RunPowerPlanApply(int genShot, Func<bool> apply)
		{
			try
			{
				lock (powerApplyGate)
				{
					if (!PowerPlanApplySessionCurrent(genShot) || PowerPlanStopped(lastPowerPolicyKey))
					{
						return false;
					}
					if (apply == null)
					{
						return false;
					}
					if (Volatile.Read(ref laptopPerfPowerGeneration) != genShot && !RestoreLaptopPerformanceForPowerPlan())
					{
						lastPowerPolicyKey = -1;
						Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
						return false;
					}
					string reason;
					if (!BeginPowerAttempt(lastPowerPolicyKey, out reason))
					{
						RecordAutomation("PowerPlanOn", PowerPlanExecutionState);
						return false;
					}
					bool skipped;
					bool runPowerPlanActionSucceeded = RunPowerPlanAction(delegate
					{
						if (!PowerPlanApplySessionCurrent(genShot))
						{
							throw new OperationCanceledException();
						}
						return apply();
					}, out skipped, out reason);
					if (skipped)
					{
						RollbackPowerPlan(PerformancePolicy.Text("电源计划应用已跳过", "Power-plan apply was skipped"));
						CompletePowerAttempt(false, "stage=apply-skipped");
						return false;
					}
					if (!PowerPlanApplySessionCurrent(genShot))
					{
						if (!runPowerPlanActionSucceeded)
						{
							ActionFailureStore.Trip("PowerPlanOn", "system", reason);
						}
						RollbackPowerPlan(runPowerPlanActionSucceeded ? "stage=session-cancelled" : reason + " phase=session-cancelled");
						CompletePowerAttempt(false, "stage=session-cancelled");
						return false;
					}
					OnPowerPlanAppliedWithReason(runPowerPlanActionSucceeded, reason, genShot);
					return runPowerPlanActionSucceeded && !ActionFailureStore.IsBlocked("PowerPlanOn", "system");
				}
			}
			catch
			{
				return false;
			}
			finally
			{
				lock (powerApplyGate)
				{
					if (powerAttemptInProgress != null)
					{
						RollbackPowerPlan("stage=attempt-interrupted");
						CompletePowerAttempt(false, "stage=attempt-interrupted");
					}
				}
				Interlocked.Exchange(ref powerApplyInFlight, 0);
				if (Interlocked.CompareExchange(ref powerPlanNotificationPending, 0, 0) != 0)
				{
					QueuePowerPlanNotificationAudit();
				}
			}
		}
		private void OnPowerPlanApplied(bool planOk)
		{
			OnPowerPlanAppliedWithReason(planOk, PerformancePolicy.Text("电源计划执行返回失败", "The power-plan action returned failure"), Volatile.Read(ref powerSessionGen));
		}
		private void OnPowerPlanAppliedWithReason(bool planOk, string reason, int generation)
		{
			if (planOk)
			{
				if (!ActionFailureStore.CompleteApply("PowerPlanOn", "system"))
				{
					RollbackPowerPlan(PerformancePolicy.Text("电源计划完成记录失败", "Power-plan completion could not be persisted"));
					CompletePowerAttempt(false, "stage=completion-persistence");
					return;
				}
				planFailStreak = 0;
				if (LoadCounter("PowerPlanFailStreak") != 0)
				{
					SaveCounter("PowerPlanFailStreak", 0);
				}
				lock (sync)
				{
					powerStoppedKeys.Clear();
				}
				Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
				Interlocked.Exchange(ref laptopPerfPowerGeneration, generation);
				Interlocked.Exchange(ref laptopPerfNextWork, 0L);
				RecordAutomation("PowerPlanOn", "applied");
				CompletePowerAttempt(true, "stage=write-and-readback");
			}
			else
			{
				planFailStreak = 1;
				ActionFailureStore.Trip("PowerPlanOn", "system", reason);
				Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
				lock (sync)
				{
					powerStoppedKeys.Add(lastPowerPolicyKey);
				}
				RollbackPowerPlan(reason);
				CompletePowerAttempt(false, reason, PowerPlan.LastApplyFailureIsPermanent);
			}
		}
		private bool StepDisplayAwake(bool want)
		{
			awakeActive = EnvStep("awake", want, awakeActive, DisplayAwake.ActivateForSession, DisplayAwake.RestoreForSession);
			return awakeActive;
		}
		internal static bool ResolvePowerPlanEnabled(PerformancePreset mode, bool manuallyEnabled)
		{
			if (!PowerPlan.NoSwitchSelected)
			{
				return manuallyEnabled;
			}
			return false;
		}
	}
}
