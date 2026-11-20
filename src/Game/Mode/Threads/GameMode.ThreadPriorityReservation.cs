namespace PaviseApp
{
    internal partial class GameMode
    {
        private ThreadPriorityReservation criticalPriorityReservation;

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
