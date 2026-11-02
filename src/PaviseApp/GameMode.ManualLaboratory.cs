using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private volatile bool manualLaboratoryMutation;
        internal bool RunLaboratoryManualWork(Func<bool> work)
        {
            if (work == null) return false;
            lock (driverStageGate)
            {
                lock (sync)
                {
                    if (stopping || active || laboratoryClosed) return false;
                    manualLaboratoryMutation = true;
                }
                try { lock (timerMode.Gate) return work(); }
                finally { manualLaboratoryMutation = false; kick.Set(); }
            }
        }
    }
}
