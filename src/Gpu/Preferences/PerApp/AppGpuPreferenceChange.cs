using System.IO;

namespace PaviseApp
{
    internal sealed class AppGpuPreferenceChange
    {
        internal readonly AppGpuPreferenceManager Owner;

        internal readonly string CurrentRaw;

        public readonly string ExePath;

        public readonly string Name;

        public readonly string CurrentPreference;

        public readonly bool NeedsConfirmation;

        public readonly bool AlreadyLowPower;

        internal AppGpuPreferenceChange(AppGpuPreferenceManager owner, string path, string current)
        {
            Owner = owner;
            ExePath = path;
            Name = Path.GetFileNameWithoutExtension(path);
            CurrentRaw = current;
            CurrentPreference = PrefFieldText.ReadField(current, "GpuPreference");
            AlreadyLowPower = CurrentPreference == "1";
            NeedsConfirmation = !AlreadyLowPower && !string.IsNullOrEmpty(current);
        }
    }
}
