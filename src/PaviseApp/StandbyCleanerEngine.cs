using System;

namespace PaviseApp
{
	internal sealed class StandbyCleanerEngine
	{
		internal const int StatusCancelled = -1073741536;

		internal const int StatusUnsuccessful = -1073741823;

		internal const int StatusInvalidParameter = -1073741811;

		private readonly IStandbyMemoryControl control;

		private readonly object gate = new object();

		private bool polling;

		public StandbyCleanerEngine(IStandbyMemoryControl control)
		{
			if (control == null)
			{
				throw new ArgumentNullException("control");
			}
			this.control = control;
		}

		public StandbyCleanerResult Poll(StandbyCleanerOptions options, Func<bool> mayContinue, out StandbyMemorySnapshot snapshot, out int nativeStatus)
		{
			return Poll(options, mayContinue, out snapshot, out nativeStatus, null);
		}

		internal StandbyCleanerResult Poll(StandbyCleanerOptions options, Func<bool> mayContinue, out StandbyMemorySnapshot snapshot, out int nativeStatus, Func<bool> admitPurge)
		{
			lock (gate)
			{
				snapshot = null;
				nativeStatus = 0;
				if (polling)
				{
					return Cancelled(out nativeStatus);
				}
				polling = true;
				try
				{
					return PollCore(options, mayContinue, out snapshot, out nativeStatus, admitPurge);
				}
				finally
				{
					polling = false;
				}
			}
		}

		private StandbyCleanerResult PollCore(StandbyCleanerOptions options, Func<bool> mayContinue, out StandbyMemorySnapshot snapshot, out int nativeStatus, Func<bool> admitPurge)
		{
			snapshot = null;
			nativeStatus = 0;
			if (options == null || !options.IsValid)
			{
				nativeStatus = -1073741811;
				return StandbyCleanerResult.InvalidOptions;
			}
			if (!MayContinue(mayContinue))
			{
				return Cancelled(out nativeStatus);
			}
			bool tryQuery;
			try
			{
				tryQuery = control.TryQuery(out snapshot);
			}
			catch
			{
				tryQuery = false;
			}
			if (!MayContinue(mayContinue))
			{
				return Cancelled(out nativeStatus);
			}
			if (!tryQuery || !ValidSnapshot(snapshot))
			{
				snapshot = null;
				nativeStatus = -1073741823;
				return StandbyCleanerResult.QueryFailed;
			}
			ulong scaledListMegabytes = (ulong)options.ListMegabytes * 1048576uL;
			ulong scaledFreeMegabytes = (ulong)options.FreeMegabytes * 1048576uL;
			if (snapshot.ListBytes < scaledListMegabytes || snapshot.FreeBytes >= scaledFreeMegabytes)
			{
				return StandbyCleanerResult.BelowThreshold;
			}
			if (!MayContinue(mayContinue))
			{
				return Cancelled(out nativeStatus);
			}
			if (!MayContinue(admitPurge))
			{
				return Cancelled(out nativeStatus);
			}
			bool pollCoreReady = false;
			int nativeStatus2 = -1073741536;
			try
			{
				pollCoreReady = control.TryPurge(mayContinue, out nativeStatus2);
			}
			catch
			{
				nativeStatus2 = -1073741823;
			}
			nativeStatus = nativeStatus2;
			if (pollCoreReady)
			{
				return StandbyCleanerResult.Purged;
			}
			if (nativeStatus2 == -1073741536)
			{
				return Cancelled(out nativeStatus);
			}
			if (nativeStatus == 0)
			{
				nativeStatus = -1073741823;
			}
			return StandbyCleanerResult.PurgeFailed;
		}

		internal static bool ValidSnapshot(StandbyMemorySnapshot snapshot)
		{
			if (snapshot == null || snapshot.TotalBytes == 0)
			{
				return false;
			}
			ulong totalBytes = snapshot.TotalBytes;
			if (snapshot.AvailableBytes <= totalBytes && snapshot.FreeBytes <= totalBytes && snapshot.StandbyBytes <= totalBytes && snapshot.ListBytes <= totalBytes)
			{
				return snapshot.FreeBytes <= totalBytes - snapshot.StandbyBytes;
			}
			return false;
		}

		internal static bool MayContinue(Func<bool> mayContinue)
		{
			if (mayContinue == null)
			{
				return true;
			}
			try
			{
				return mayContinue();
			}
			catch
			{
				return false;
			}
		}

		private static StandbyCleanerResult Cancelled(out int nativeStatus)
		{
			nativeStatus = -1073741536;
			return StandbyCleanerResult.Cancelled;
		}
	}
}
