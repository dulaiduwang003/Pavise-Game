using System;

namespace PaviseApp
{
	internal sealed class TimerResolutionLease
	{
		internal delegate int Request(uint desired, bool acquire, out uint actual);

		internal delegate int Read(out uint coarsest, out uint finest, out uint current);

		internal const uint Desired = 5000u;

		internal const string FailureAction = "GlobalTimerResolutionLeaseV1";

		internal const int AuditSeconds = 5;

		internal Request Set;

		internal Read Query;

		private readonly object gate = new object();

		private long nextAudit;

		private long releaseRetryAt;

		private int releaseFailures;

		private bool exemptionOwned;

		private int originalControl;

		private int originalState;

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
			if (acquire && Native.OsBuild() >= 22000)
			{
				actual = 0u;
				int controlMask;
				int stateMask;
				if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out controlMask, out stateMask))
				{
					return -1;
				}
				if (!exemptionOwned)
				{
					originalControl = controlMask;
					originalState = stateMask;
				}
				if (!Native.TimerExemptOk(controlMask, stateMask))
				{
					exemptionOwned = true;
					if (!Native.RestorePowerThrottling(new IntPtr(-1), controlMask | 4, stateMask & -5))
					{
						return -1;
					}
					int controlMask2;
					int stateMask2;
					if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out controlMask2, out stateMask2) || !Native.TimerExemptOk(controlMask2, stateMask2))
					{
						return -1;
					}
				}
			}
			return Native.NtSetTimerResolution(desired, acquire, out actual);
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
				int controlMask;
				int stateMask;
				if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out controlMask, out stateMask))
				{
					return false;
				}
				if (Native.TimerExemptOk(controlMask, stateMask) && !Native.RestorePowerThrottling(new IntPtr(-1), (controlMask & -5) | (originalControl & 4), (stateMask & -5) | (originalState & 4)))
				{
					return false;
				}
				exemptionOwned = false;
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal bool Update(bool want, long now)
		{
			lock (gate)
			{
				return UpdateLocked(want, now);
			}
		}

		private bool UpdateLocked(bool want, long now)
		{
			if (!want)
			{
				nextAudit = 0L;
				if (now < releaseRetryAt)
				{
					return !HasResidue;
				}
				if (!Active)
				{
					Verified = false;
					Failed = !CleanupExemption();
					if (Failed)
					{
						ActionFailureStore.Trip("GlobalTimerResolutionLeaseV1", "system", "Timer exemption restoration failed");
						DelayRelease(now);
					}
					else
					{
						releaseFailures = 0;
						releaseRetryAt = 0L;
					}
					return !Failed;
				}
				bool flag = Release();
				if (!flag)
				{
					ActionFailureStore.Trip("GlobalTimerResolutionLeaseV1", "system", "Timer request release failed");
					DelayRelease(now);
				}
				else
				{
					releaseFailures = 0;
					releaseRetryAt = 0L;
				}
				return flag;
			}
			if (ActionFailureStore.IsBlocked("GlobalTimerResolutionLeaseV1", "system"))
			{
				if (HasResidue && now >= releaseRetryAt)
				{
					if (!Release())
					{
						DelayRelease(now);
					}
					else
					{
						releaseFailures = 0;
						releaseRetryAt = 0L;
					}
				}
				Failed = true;
				return false;
			}
			if (ActionFailureStore.IsRetryPending("GlobalTimerResolutionLeaseV1", "system"))
			{
				if (HasResidue && !Release())
				{
					ActionFailureStore.Trip("GlobalTimerResolutionLeaseV1", "system", "Timer recovery incomplete before retry");
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
					return Isolate("Timer readback failed", now);
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
					ActionFailureStore.Trip("GlobalTimerResolutionLeaseV1", "system", "Timer request release failed before maintenance");
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
				return false;
			}
			try
			{
				uint actual;
				if (Set(5000u, true, out actual) >= 0)
				{
					Active = true;
					Actual = actual;
					Failed = false;
					if (!ReadCurrent(now))
					{
						return Isolate("Timer request accepted but readback failed", now);
					}
					if (!TargetReached && Finest <= 5000)
					{
						return Isolate("Timer request did not reach the supported target", now);
					}
					if (!ActionFailureStore.CompleteApply("GlobalTimerResolutionLeaseV1", "system"))
					{
						return Isolate("Timer isolation record persistence failed", now);
					}
					return true;
				}
			}
			catch
			{
			}
			Verified = false;
			return Isolate("Timer request failed", now);
		}

		private bool Isolate(string reason, long now)
		{
			ActionFailureStore.Trip("GlobalTimerResolutionLeaseV1", "system", reason);
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
			if (!Active)
			{
				Failed = cleanup && !CleanupExemption();
				return !Failed;
			}
			try
			{
				uint actual;
				if (Set(5000u, false, out actual) >= 0)
				{
					Active = false;
					Actual = actual;
					Verified = false;
					Failed = cleanup && !CleanupExemption();
					return !Failed;
				}
			}
			catch
			{
			}
			Failed = true;
			return false;
		}

		private bool ReadCurrent(long now)
		{
			nextAudit = now + TimeSpan.FromSeconds(5.0).Ticks;
			Verified = false;
			try
			{
				uint coarsest;
				uint finest;
				uint current;
				if (Query(out coarsest, out finest, out current) >= 0 && finest != 0 && coarsest >= finest && current != 0)
				{
					Coarsest = coarsest;
					Finest = finest;
					Actual = current;
					Verified = true;
				}
			}
			catch
			{
			}
			return Verified;
		}
	}
}
