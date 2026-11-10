using System;

namespace PaviseApp
{
    internal sealed class LaboratoryManualState
    {
        internal bool CanApply, CanRestore, Applied, RestartRequired;
        internal string Summary = "", Reason = "", Failure = "", Details = "";
    }

    internal interface ILaboratoryManualStore
    {
        bool Read(out string value);
        bool Write(string value);
    }

    internal sealed class LaboratoryManualSettingsStore : ILaboratoryManualStore
    {
        private readonly string key;
        internal LaboratoryManualSettingsStore(string key) { this.key = key; }
        public bool Read(out string value) { return Settings.TryLoadStr(key, out value); }
        public bool Write(string value)
        {
            string actual;
            return Settings.SaveStrDurable(key, value ?? "") && Settings.TryLoadStr(key, out actual)
                && string.Equals(actual, value ?? "", StringComparison.Ordinal);
        }
    }

    internal static class LaboratoryActionText
    {
        internal static string Pick(string zh, string en)
        { return Lang.Cur == 1 ? en : zh; }
    }
}
