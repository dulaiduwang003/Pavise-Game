namespace PaviseApp
{
	// A short grace period for maintenance of an already-owned, identity-verified pin.
	// Admission, ownership conflicts and access denial must never use this budget.
	internal sealed class CoreAffinityAuditRetry
	{
		private long firstFailureTicks;
		private bool stopped;

		internal int Failures { get; private set; }

		internal bool ShouldRetry(int nativeError, long nowTicks)
		{
			if (stopped) return false;
			if (Failures == 0) firstFailureTicks = nowTicks;
			Failures++;
			stopped = nativeError == 1 || nativeError == 5 || nativeError == 6 || nativeError == 50
				|| nativeError == 87 || nativeError == 120 || nativeError == 1314 || Failures >= 3
				|| nowTicks < firstFailureTicks || nowTicks - firstFailureTicks >= 30000000L;
			return !stopped;
		}

		internal void Reset()
		{
			Failures = 0;
			firstFailureTicks = 0L;
			stopped = false;
		}
	}
}
