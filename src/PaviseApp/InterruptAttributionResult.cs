using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class InterruptAttributionResult
    {
        public bool Ok;

        public string Error;

        public readonly List<DriverInterrupt> Drivers = new List<DriverInterrupt>();

        public uint EventsLost;

        public uint BuffersLost;

        public bool Incomplete;

        public long Unmapped;

        public bool Lossy
        {
            get
            {
                if (EventsLost == 0)
                {
                    return BuffersLost != 0;
                }
                return true;
            }
        }
    }
}
