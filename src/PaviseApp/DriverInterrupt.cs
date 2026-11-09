namespace PaviseApp
{
    internal sealed class DriverInterrupt
    {
        public string Driver;

        public long Dpc;

        public long Isr;

        public double DpcTotalUs;

        public double DpcMaxUs;

        public long DpcOver500Us;

        public long DpcOver1Ms;

        public ulong CpuMask;

        public bool CpuMaskTruncated;

        public long BadDuration;

        public long[] DpcBuckets;
    }
}
