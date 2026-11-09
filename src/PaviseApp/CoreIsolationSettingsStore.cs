namespace PaviseApp
{
    internal sealed class CoreIsolationSettingsStore : ICoreIsolationStore
    {
        internal const string Key = "Crash_CoreIsolationV1";

        public bool Read(out string value)
        {
            return Settings.TryLoadStr("Crash_CoreIsolationV1", out value);
        }

        public bool Write(string value)
        {
            return Settings.SaveStrDurable("Crash_CoreIsolationV1", value);
        }
    }
}
