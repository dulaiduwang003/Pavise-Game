using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private int hardPinRestoreRequested;

        private void RequestHardPinRestoreLocked(HardPin pin, bool wake = true)
        {
            bool firstRequest = !pin.RestoreRequested;
            pin.RestoreRequested = true;
            corePinPending = true;
            Interlocked.Exchange(ref hardPinRestoreRequested, 1);
            if (wake && firstRequest && kick != null) kick.Set();
        }

        // Reuse the existing worker; criticalProcessGate remains the single recovery gate.
        // A restore task must not outlive shutdown or race a replacement writer.
        private void RetryPendingHardPinRestores()
        {
            if (Monitor.IsEntered(sync) || Interlocked.Exchange(ref hardPinRestoreRequested, 0) == 0) return;
            var pending = new List<KeyValuePair<int, HardPin>>();
            lock (sync)
                foreach (KeyValuePair<int, HardPin> pair in hardPins)
                    if (pair.Value.RestoreRequested) pending.Add(pair);
            foreach (KeyValuePair<int, HardPin> pair in pending)
            {
                lock (sync)
                {
                    HardPin current;
                    if (!hardPins.TryGetValue(pair.Key, out current) || !object.ReferenceEquals(current, pair.Value)) continue;
                }
                RestoreHardPin(pair.Key, pair.Value);
            }
            lock (sync)
            {
                bool requested = false;
                foreach (HardPin pin in hardPins.Values) requested |= pin.RestoreRequested;
                if (requested) Interlocked.Exchange(ref hardPinRestoreRequested, 1);
                corePinPending = requested || coreAffinityJobs.HasPending;
            }
        }
    }
}
