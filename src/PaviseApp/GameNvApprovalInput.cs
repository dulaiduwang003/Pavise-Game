namespace PaviseApp
{
    internal sealed class GameNvApprovalInput
    {
        internal readonly string EvidenceHash;

        internal readonly string MachineHash;

        internal readonly string PhysicalToken;

        internal readonly long Generation;

        internal readonly bool Current;

        internal readonly bool NvidiaScope;

        internal readonly bool PStateSelected;

        internal readonly bool OpenGlSelected;

        internal readonly bool OnAc;

        internal GameNvApprovalInput(string evidenceHash, string machineHash, string physicalToken, long generation, bool current, bool nvidiaScope, bool pstateSelected, bool openGlSelected, bool onAc)
        {
            EvidenceHash = evidenceHash;
            MachineHash = machineHash;
            PhysicalToken = physicalToken;
            Generation = generation;
            Current = current;
            NvidiaScope = nvidiaScope;
            PStateSelected = pstateSelected;
            OpenGlSelected = openGlSelected;
            OnAc = onAc;
        }
    }
}
