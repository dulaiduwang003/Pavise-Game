namespace PaviseApp
{
	internal partial class GameMode
	{
		private ThreadPriorityReservation criticalPriorityReservation;

		private bool TryReserveCriticalThreadPriority(int pid, long birth, HardThreadIdentity[] threads, out string why)
		{
			lock (ThreadPriorityReservation.Gate)
			{
				if (criticalPriorityReservation == null)
					criticalPriorityReservation = ThreadPriorityReservation.Create(pid, birth, threads);
				bool frame = FrameCoordination.ReleaseReservedPriority();
				bool lane = RenderLane.ReleaseReservedPriority();
				why = frame && lane ? "" : "Original thread priority restoration is unconfirmed";
				return frame && lane;
			}
		}

		private bool ReleaseCriticalThreadPriorityReservation()
		{
			lock (ThreadPriorityReservation.Gate)
			{
				if (criticalPriorityReservation == null) return true;
				bool frame = FrameCoordination.ReleaseReservedPriority();
				bool lane = RenderLane.ReleaseReservedPriority();
				if (!frame || !lane) return false;
				criticalPriorityReservation.Dispose();
				criticalPriorityReservation = null;
				return true;
			}
		}
	}
}
