namespace PaviseApp
{
    internal sealed class AppGpuPreferenceEntry
    {
        public readonly string ExePath;

        public readonly string Name;

        public readonly string CurrentPreference;

        public readonly AppGpuPreferenceState State;

        public readonly bool ReadFailed;

        public readonly bool CanForget;

        internal AppGpuPreferenceEntry(string path, string name, AppGpuPreferenceState state, string current, bool readFailed)
        {
            ExePath = path;
            Name = name;
            State = state;
            CurrentPreference = PrefFieldText.ReadField(current, "GpuPreference");
            ReadFailed = readFailed;
            CanForget = state == AppGpuPreferenceState.PendingApply || state == AppGpuPreferenceState.PendingRestore || state == AppGpuPreferenceState.ExternalChange;
        }
    }
}
