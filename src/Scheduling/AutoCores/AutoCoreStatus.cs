namespace PaviseApp
{
    internal sealed class AutoCoreStatus
    {
        public AutoCorePlan Plan;

        public string Game;

        public string ProfileId;

        public bool Watching;

        public bool Exclusive;

        public bool Engaged;

        public bool Pending;

        public string Blocked;

        public bool ObservationOnly;

        public bool Unrestricted;

        public bool Manual;

        public bool ManualIsolation;

        public string BackgroundAffinityDetail;

        public ulong GameMask;

        public ulong ExpectedMask;

        public ulong ObservedMask;

        public bool AffinityRead;

        public bool AffinityVerified;

        public bool JobAffinity;
    }
}
