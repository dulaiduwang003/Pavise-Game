using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal sealed class TimerResolutionLease
	{
		internal delegate int Request(uint desired, bool acquire, out uint actual);

		internal delegate int Read(out uint coarsest, out uint finest, out uint current);

		internal const uint Desired = 5000u;

		internal const string FailureAction = "GlobalTimerResolutionLeaseV1";

		internal const int AuditSeconds = 5;

		internal const int AcquireReadbackSamples = 3;

		internal const int AcquireReadbackDelayMs = 20;

		internal Request Set;

		internal Read Query;

		private readonly object gate = new object();

		private long nextAudit;

		private long releaseRetryAt;

		private int releaseFailures;

		private readonly AutomaticRecoveryBudget recoveryBudget = new AutomaticRecoveryBudget();

		internal void BeginAutomaticCycle(string identity)
		{
			lock (gate)
			{
				ActionFailureStore.BeginAutomaticCycle(FailureAction, "system", identity);
				if (recoveryBudget.BeginCycle(identity))
				{
					releaseRetryAt = 0L;
					releaseFailures = recoveryBudget.FailureCount;
					nextAudit = 0L;
				}
			}
		}

		private bool exemptionOwned;

		private int originalControl;

		private int originalState;

		private string nativeFailure;

		private string failureDetail;

		private StringBuilder acquisitionTrace;

		private string acquisitionDetail;

		private bool nativeAcquisition;

		internal string AcquisitionDetail
		{
			get { lock (gate) { return acquisitionDetail ?? ""; } }
		}

		private void TraceAcquisition(string detail)
		{
			if (acquisitionTrace == null) return;
			if (acquisitionTrace.Length > 0) acquisitionTrace.Append("; ");
			acquisitionTrace.Append(detail);
		}

		internal string FailureDetail
		{
			get
			{
				lock (gate)
				{
					string original = failureDetail ?? ActionFailureStore.FailureReason(FailureAction, "system");
					if (HasResidue && !string.IsNullOrEmpty(nativeFailure) && nativeFailure != original)
						return nativeFailure + (string.IsNullOrEmpty(original) ? "" : " cause={" + original + "}");
					return original ?? "";
				}
			}
		}

		internal bool Active { get; private set; }

		internal bool HasResidue
		{
			get
			{
				if (!Active)
				{
					return exemptionOwned;
				}
				return true;
			}
		}

		internal uint Actual { get; private set; }

		internal uint Finest { get; private set; }

		internal uint Coarsest { get; private set; }

		internal bool Verified { get; private set; }

		internal bool TargetReached
		{
			get
			{
				if (Active && Verified && Actual != 0)
				{
					return Actual <= 5000;
				}
				return false;
			}
		}

		internal bool Failed { get; private set; }

		internal string State
		{
			get
			{
				lock (gate)
				{
					return (!Failed) ? ((!Active) ? "" : ((!Verified) ? "timer-unverified" : (TargetReached ? "timer-locked" : "timer-limited"))) : (HasResidue ? "restore-failed" : "stopped");
				}
			}
		}

		internal TimerResolutionLease()
		{
			Set = NativeSet;
			Query = NativeQuery;
		}

		internal string ReadState(out bool hasResidue)
		{
			lock (gate)
			{
				hasResidue = HasResidue;
				return State;
			}
		}

		internal bool Readback(out bool verified, out uint actual, out uint finest)
		{
			lock (gate)
			{
				verified = Verified;
				actual = Actual;
				finest = Finest;
				return Active;
			}
		}

		internal void ResetAcquireAttempts()
		{
			lock (gate)
			{
				nextAudit = 0L;
			}
		}

		private int NativeSet(uint desired, bool acquire, out uint actual)
		{
			actual = 0;
			if (acquire) nativeAcquisition = true;
			if (acquire && Native.OsBuild() >= 22000)
			{
				int controlMask;
				int stateMask;
				int win32, ntStatus;
				if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out controlMask, out stateMask, out win32, out ntStatus))
				{
					nativeFailure = PowerReadFailure("exemption-query", win32, ntStatus);
					TraceAcquisition("exemptionBefore=unreadable " + nativeFailure);
					return -1;
				}
				TraceAcquisition("exemptionBefore=" + controlMask + "/" + stateMask);
				if (!exemptionOwned)
				{
					originalControl = controlMask;
					originalState = stateMask;
				}
				if (!Native.TimerExemptOk(controlMask, stateMask))
				{
					exemptionOwned = true;
					if (!Native.RestorePowerThrottling(new IntPtr(-1), controlMask | 4, stateMask & -5, out win32))
					{
						nativeFailure = "stage=exemption-write api=SetProcessInformation win32=" + win32;
						return -1;
					}
					int controlMask2;
					int stateMask2;
					if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out controlMask2, out stateMask2, out win32, out ntStatus))
					{
						nativeFailure = PowerReadFailure("exemption-readback", win32, ntStatus);
						TraceAcquisition("exemptionAfter=unreadable " + nativeFailure);
						return -1;
					}
					TraceAcquisition("exemptionAfter=" + controlMask2 + "/" + stateMask2);
					if (!Native.TimerExemptOk(controlMask2, stateMask2))
					{
						nativeFailure = "stage=exemption-readback result=mismatch control=" + controlMask2 + " state=" + stateMask2;
						return -1;
					}
				}
				else TraceAcquisition("exemptionAfter=" + controlMask + "/" + stateMask + " already-confirmed");
			}
			else if (acquire) TraceAcquisition("exemption=not-required");
			int status = Native.NtSetTimerResolution(desired, acquire, out actual);
			if (acquire) TraceAcquisition("ntSetStatus=0x" + status.ToString("X8") + " ntSetActual100ns=" + actual);
			if (status < 0) nativeFailure = TimerStatus(acquire ? "request" : "release", "NtSetTimerResolution", status);
			return status;
		}

		private static string TimerStatus(string stage, string api, int status)
		{
			return "stage=" + stage + " api=" + api + " ntstatus=0x" + status.ToString("X8");
		}

		private static string PowerReadFailure(string stage, int win32, int status)
		{
			return TimerStatus(stage, "GetProcessInformation/NtQueryInformationProcess", status) + " win32=" + win32;
		}

		private string CurrentFailure(string stage)
		{
			return string.IsNullOrEmpty(nativeFailure) ? "stage=" + stage : nativeFailure;
		}

		private void RecordFailure(string reason)
		{
			if (acquisitionTrace != null && acquisitionTrace.Length > 0)
				reason += " request={" + acquisitionTrace + "}";
			failureDetail = reason;
			ActionFailureStore.Trip(FailureAction, "system", reason);
		}

		private static int NativeQuery(out uint coarsest, out uint finest, out uint current)
		{
			return Native.NtQueryTimerResolution(out coarsest, out finest, out current);
		}

		private bool CleanupExemption()
		{
			if (!exemptionOwned)
			{
				return true;
			}
			try
			{
				nativeFailure = null;
				int controlMask;
				int stateMask;
				int win32, ntStatus;
				if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out controlMask, out stateMask, out win32, out ntStatus))
				{
					nativeFailure = PowerReadFailure("exemption-restore-query", win32, ntStatus);
					return false;
				}
				if (Native.TimerExemptOk(controlMask, stateMask))
				{
					int desiredControl = (controlMask & -5) | (originalControl & 4);
					int desiredState = (stateMask & -5) | (originalState & 4);
					if (!Native.RestorePowerThrottling(new IntPtr(-1), desiredControl, desiredState, out win32))
					{
						nativeFailure = "stage=exemption-restore-write api=SetProcessInformation win32=" + win32;
						return false;
					}
					int verifiedControl, verifiedState;
					if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out verifiedControl, out verifiedState, out win32, out ntStatus))
					{
						nativeFailure = PowerReadFailure("exemption-restore-readback", win32, ntStatus);
						return false;
					}
					if ((verifiedControl & 4) != (originalControl & 4) || (verifiedState & 4) != (originalState & 4))
					{
						nativeFailure = "stage=exemption-restore-readback result=mismatch control=" + verifiedControl + " state=" + verifiedState;
						return false;
					}
				}
				exemptionOwned = false;
				return true;
			}
			catch (Exception error)
			{
				nativeFailure = "stage=exemption-restore exception=" + error.GetType().Name;
				return false;
			}
		}

		internal bool Update(bool want, long now)
		{
			lock (gate)
			{
				long previousDeadline = releaseRetryAt;
				Stopwatch elapsed = Stopwatch.StartNew();
				bool result = UpdateLocked(want, now);
				if (releaseRetryAt != previousDeadline && releaseRetryAt > now) releaseRetryAt += elapsed.Elapsed.Ticks;
                if (!result && HasResidue)
                    RecoveryAttemptTrace.FailureSchedule(recoveryBudget.FailureCount, AutomaticRecoveryBudget.MaximumFailures, releaseRetryAt);
				return result;
			}
		}

		private bool UpdateLocked(bool want, long now)
		{
			if (!want)
			{
				nextAudit = 0L;
				if (now < releaseRetryAt || (HasResidue && !recoveryBudget.CanAttempt))
				{
					return !HasResidue;
				}
				if (!Active)
				{
					bool hadResidue = HasResidue;
					Verified = false;
					Failed = !CleanupExemption();
					recoveryBudget.Record(!Failed);
					if (hadResidue && !Failed && !HasResidue) recoveryBudget.CompleteRecovery();
					if (Failed)
					{
						RecordFailure(CurrentFailure("exemption-restore"));
						DelayRelease(now);
					}
					else
					{
						ActionFailureStore.CancelApply(FailureAction, "system");
						releaseFailures = 0;
						releaseRetryAt = 0L;
					}
					return !Failed;
				}
				bool flag = Release();
				if (!flag)
				{
					RecordFailure(CurrentFailure("release"));
					DelayRelease(now);
				}
				else
				{
					ActionFailureStore.CancelApply(FailureAction, "system");
					releaseFailures = 0;
					releaseRetryAt = 0L;
				}
				return flag;
			}
			if (ActionFailureStore.IsBlocked("GlobalTimerResolutionLeaseV1", "system"))
			{
				failureDetail = ActionFailureStore.FailureReason(FailureAction, "system");
				if (HasResidue && recoveryBudget.CanAttempt && now >= releaseRetryAt)
				{
					if (!Release())
					{
						DelayRelease(now);
					}
					else
					{
						ActionFailureStore.CancelApply(FailureAction, "system");
						releaseFailures = 0;
						releaseRetryAt = 0L;
					}
				}
				Failed = true;
				return false;
			}
			if (ActionFailureStore.IsIsolated("GlobalTimerResolutionLeaseV1", "system"))
			{
				if (HasResidue && (!recoveryBudget.CanAttempt || now < releaseRetryAt)) return false;
				if (HasResidue && !Release())
				{
					RecordFailure(CurrentFailure("recovery-before-apply"));
					DelayRelease(now);
					return false;
				}
				nextAudit = 0L;
			}
			if (Active)
			{
				if (now < nextAudit)
				{
					return !Failed;
				}
				if (!ReadCurrent(now))
				{
					return Isolate(CurrentFailure("query"), now);
				}
				if (TargetReached)
				{
					Failed = false;
					return true;
				}
				if (Finest > 5000)
				{
					Failed = false;
					return true;
				}
				if (!Release(false))
				{
					RecordFailure(CurrentFailure("release-before-maintenance"));
					DelayRelease(now);
					return false;
				}
				return Acquire(now);
			}
			return Acquire(now);
		}

		private bool Acquire(long now)
		{
			string reason;
			if (!ActionFailureStore.TryBeginApply("GlobalTimerResolutionLeaseV1", "system", out reason))
			{
				Failed = true;
				failureDetail = string.IsNullOrEmpty(reason) ? "stage=admission result=denied" : reason;
				return false;
			}
			acquisitionTrace = new StringBuilder();
			nativeAcquisition = false;
			try
			{
				nativeFailure = null;
				uint actual;
				int status = Set(5000u, true, out actual);
				TraceAcquisition("setStatus=0x" + status.ToString("X8") + " setActual100ns=" + actual);
				if (status >= 0)
				{
					Active = true;
					Actual = actual;
					Failed = false;
					// Recheck the same request at most three times without reacquiring or refunding its failure budget.
					for (int sample = 1; sample <= AcquireReadbackSamples; sample++)
					{
						if (sample > 1)
						{
							Thread.Sleep(AcquireReadbackDelayMs);
							if (!ConfirmAcquisitionExemption(sample))
								return Isolate(CurrentFailure("exemption-during-acquire"), now);
						}
						if (!ReadCurrent(now, sample))
							return Isolate(CurrentFailure("query-after-acquire"), now);
						if (TargetReached || Finest > Desired) break;
					}
					if (!TargetReached && Finest <= 5000)
					{
						return Isolate("stage=target-readback desired100ns=" + Desired + " actual100ns=" + Actual + " finest100ns=" + Finest, now);
					}
					if (!ActionFailureStore.CompleteApply("GlobalTimerResolutionLeaseV1", "system"))
					{
						return Isolate("stage=completion-record result=persistence-failed", now);
					}
					failureDetail = null;
					nativeFailure = null;
					return true;
				}
				if (string.IsNullOrEmpty(nativeFailure)) nativeFailure = TimerStatus("request", "NtSetTimerResolution", status);
				Verified = false;
				return Isolate(CurrentFailure("request"), now);
			}
			catch (Exception error)
			{
				nativeFailure = "stage=request exception=" + error.GetType().Name;
				Verified = false;
				return Isolate(CurrentFailure("request"), now);
			}
			finally
			{
				acquisitionDetail = acquisitionTrace.ToString();
				acquisitionTrace = null;
				nativeAcquisition = false;
			}
		}

		private bool ConfirmAcquisitionExemption(int sample)
		{
			// The synchronous native path targets this process, so PID reuse cannot redirect acquisition.
			if (!nativeAcquisition || Native.OsBuild() < 22000) return true;
			int control, state, win32, status;
			if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out control, out state, out win32, out status))
			{
				nativeFailure = PowerReadFailure("exemption-during-acquire", win32, status);
				TraceAcquisition("exemptionSample" + sample + "=unreadable " + nativeFailure);
				return false;
			}
			TraceAcquisition("exemptionSample" + sample + "=" + control + "/" + state);
			if (Native.TimerExemptOk(control, state)) return true;
			nativeFailure = "stage=exemption-during-acquire result=mismatch control=" + control + " state=" + state;
			return false;
		}

		private bool Isolate(string reason, long now)
		{
			RecordFailure(reason);
			if (HasResidue && !Release())
			{
				DelayRelease(now);
			}
			Failed = true;
			return false;
		}

		private void DelayRelease(long now)
		{
			int num = Math.Min(300, 30 * (1 << releaseFailures));
			releaseFailures = Math.Min(4, releaseFailures + 1);
			releaseRetryAt = now + TimeSpan.FromSeconds(num).Ticks;
		}

		private bool Release(bool cleanup = true)
		{
			if (HasResidue && !recoveryBudget.CanAttempt) return false;
			bool hadResidue = HasResidue;
			bool restored = ReleaseCore(cleanup);
			recoveryBudget.Record(restored);
			if (hadResidue && restored && !HasResidue) recoveryBudget.CompleteRecovery();
			return restored;
		}

		private bool ReleaseCore(bool cleanup)
		{
			nativeFailure = null;
			if (!Active)
			{
				Failed = cleanup && !CleanupExemption();
				return !Failed;
			}
			try
			{
				uint actual;
				int status = Set(5000u, false, out actual);
				if (status >= 0)
				{
					Active = false;
					Actual = actual;
					Verified = false;
					Failed = cleanup && !CleanupExemption();
					return !Failed;
				}
				if (string.IsNullOrEmpty(nativeFailure)) nativeFailure = TimerStatus("release", "NtSetTimerResolution", status);
			}
			catch (Exception error)
			{
				nativeFailure = "stage=release exception=" + error.GetType().Name;
			}
			Failed = true;
			return false;
		}

		private bool ReadCurrent(long now, int acquisitionSample = 0)
		{
			nextAudit = now + TimeSpan.FromSeconds(5.0).Ticks;
			Verified = false;
			try
			{
				uint coarsest;
				uint finest;
				uint current;
				int status = Query(out coarsest, out finest, out current);
				if (acquisitionSample > 0)
					TraceAcquisition("query" + acquisitionSample + "Status=0x" + status.ToString("X8")
						+ " actual100ns=" + current + " finest100ns=" + finest + " coarsest100ns=" + coarsest);
				if (status >= 0 && finest != 0 && coarsest >= finest && current != 0)
				{
					Coarsest = coarsest;
					Finest = finest;
					Actual = current;
					Verified = true;
				}
				else
				{
					nativeFailure = TimerStatus("query", "NtQueryTimerResolution", status)
						+ " coarsest100ns=" + coarsest + " finest100ns=" + finest + " current100ns=" + current;
				}
			}
			catch (Exception error)
			{
				if (acquisitionSample > 0)
					TraceAcquisition("query" + acquisitionSample + "Exception=" + error.GetType().Name);
				nativeFailure = "stage=query exception=" + error.GetType().Name;
			}
			return Verified;
		}
	}
}
