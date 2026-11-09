namespace PaviseApp
{
    internal sealed class CriticalThreadHotspot
    {
        internal readonly int Tid;

        internal readonly int Windows;

        internal readonly long Creation;

        internal readonly double CpuShare;

        internal CriticalThreadHotspot(CoreThreadCandidate value)
        {
            Tid = value.Tid;
            Creation = value.Creation;
            Windows = value.Windows;
            CpuShare = value.Mean;
        }
    }
}
