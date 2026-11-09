using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class GpuIrqResult
    {
        public GpuIrqOutcome Outcome;

        public string Detail = "";

        public GpuIrqSample Gpu;

        public ulong Region;

        public ulong Target;

        public List<SlowDriver> Slow = new List<SlowDriver>();
    }
}
