using System.IO;

namespace PaviseApp
{
    internal sealed class AppGpuPreferenceChange
    {
        internal readonly AppGpuPreferenceManager Owner;

        internal readonly string CurrentRaw;

        internal readonly string BaselineRaw;

        internal readonly string StageOriginal;

        internal readonly bool Staged;

        public readonly string ExePath;

        public readonly string Name;

        public readonly string CurrentPreference;

        public readonly string OriginalPreference;

        public readonly bool NeedsConfirmation;

        public readonly bool AlreadyLowPower;

        internal AppGpuPreferenceChange(AppGpuPreferenceManager owner, string path, string current, string baseline, bool staged, string stageOriginal)
        {
            Owner = owner;
            ExePath = path;
            Name = Path.GetFileNameWithoutExtension(path);
            CurrentRaw = current;
            BaselineRaw = baseline;
            Staged = staged;
            StageOriginal = stageOriginal;
            CurrentPreference = PrefFieldText.ReadField(current, "GpuPreference");
            OriginalPreference = PrefFieldText.ReadField(baseline, "GpuPreference");
            AlreadyLowPower = OriginalPreference == "1";
            NeedsConfirmation = !AlreadyLowPower && !string.IsNullOrEmpty(baseline);
        }
    }
}
