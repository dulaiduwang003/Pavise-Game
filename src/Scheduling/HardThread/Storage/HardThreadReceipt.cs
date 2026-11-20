namespace PaviseApp
{
    internal sealed class HardThreadReceipt
    {
        internal HardThreadIdentity Thread;

        internal ushort Group;

        internal ushort PreviousGroup;

        internal ushort RestorePreviousGroup;

        internal ulong Captured;

        internal ulong Applied;

        internal ulong Previous;

        internal ulong RestorePrevious;

        internal bool PreviousKnown;

        internal bool RestorePreviousKnown;

        internal HardThreadPhase Phase;

        internal string Detail = "";

        internal bool Terminal
        {
            get
            {
                if (Phase != HardThreadPhase.Restored && Phase != HardThreadPhase.Gone)
                {
                    return Phase == HardThreadPhase.NotApplied;
                }
                return true;
            }
        }

        internal HardThreadReceipt Clone()
        {
            return (HardThreadReceipt)MemberwiseClone();
        }
    }
}
