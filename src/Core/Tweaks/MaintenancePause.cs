// 作者：bdth 2074055628@qq.com
using System;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum MaintenanceRuntimeState { Unknown, NotObservedRunning, Running, Unsupported }

    internal sealed class MaintenanceRuntimeSnapshot
    {
        internal MaintenanceRuntimeState State;
        internal int RunningTaskCount;
        internal string Detail;
    }

    internal sealed class MaintenanceValue
    {
        internal bool Exists;
        internal RegistryValueKind Kind;
        internal object Value;
        internal bool IsDisabled { get { return Exists && Kind == RegistryValueKind.DWord && Value is int && (int)Value == 1; } }
        internal bool SameAs(MaintenanceValue other)
        { return other != null && Exists == other.Exists && (!Exists || Kind == other.Kind && object.Equals(Value, other.Value)); }
    }

    internal interface IMaintenanceControl
    {
        bool TryRead(out MaintenanceValue value);
        bool TryWrite(int value, MaintenanceValue expected);
        bool TryDelete(MaintenanceValue expected);
        MaintenanceRuntimeSnapshot QueryRuntime();
    }

    // This controls future automatic triggers, not already-running maintenance tasks.
    internal sealed class MaintenancePauseEngine
    {
        private const char Separator = '\u001f';
        private readonly IMaintenanceControl control;
        private readonly IOptionalServiceLedger ledger;
        private readonly object gate = new object();
        private bool active;
        internal MaintenancePauseEngine(IMaintenanceControl control, IOptionalServiceLedger ledger)
        {
            if (control == null || ledger == null) throw new ArgumentNullException();
            this.control = control; this.ledger = ledger;
        }
        internal bool Active { get { lock (gate) return active; } }
        internal bool HasResidue { get { lock (gate) { string raw; return !ReadLedger(out raw) || raw.Length != 0; } } }

        internal bool Activate()
        {
            lock (gate)
            {
                try
                {
                    MaintenanceValue current;
                    if (active) return control.TryRead(out current) && current != null && current.IsDisabled;
                    string raw;
                    if (!ReadLedger(out raw)) return false;
                    if (raw.Length != 0 && !Restore()) return false;
                    if (!control.TryRead(out current) || current == null) return false;
                    if (current.Exists && (current.Kind != RegistryValueKind.DWord || !(current.Value is int))) return false;
                    if (current.IsDisabled) { active = true; return true; }
                    MaintenanceValue originalValue = current;
                    string original = current.Exists ? "=" + ((int)current.Value).ToString(CultureInfo.InvariantCulture) : ReversibleReg.Absent;
                    // The exact original and expected ownership must be durable first.
                    if (!SaveLedger(original + Separator + "=1")) return false;
                    if (!control.TryRead(out current) || !originalValue.SameAs(current)) return false;
                    if (!control.TryWrite(1, originalValue) || !control.TryRead(out current) || current == null || !current.IsDisabled) return false;
                    active = true;
                    return true;
                }
                catch { return false; }
            }
        }

        internal bool Restore()
        {
            lock (gate)
            {
                active = false;
                string raw;
                if (!ReadLedger(out raw)) return false;
                if (raw.Length == 0) return true;
                bool absent;
                int original;
                if (!Parse(raw, out absent, out original)) return false;
                try
                {
                    MaintenanceValue current;
                    if (!control.TryRead(out current) || current == null) return false;
                    // Type/value changes relinquish ownership instead of overwriting.
                    if (!current.IsDisabled)
                    {
                        return SaveLedger("");
                    }
                    bool written = absent ? control.TryDelete(current) : control.TryWrite(original, current);
                    if (!written || !control.TryRead(out current) || current == null) return false;
                    bool verified = absent ? !current.Exists
                        : current.Exists && current.Kind == RegistryValueKind.DWord && current.Value is int && (int)current.Value == original;
                    if (!verified) return false;
                    return SaveLedger("");
                }
                catch { return false; }
            }
        }

        internal MaintenanceRuntimeSnapshot QueryRuntime()
        {
            try { return control.QueryRuntime() ?? new MaintenanceRuntimeSnapshot { State = MaintenanceRuntimeState.Unknown }; }
            catch { return new MaintenanceRuntimeSnapshot { State = MaintenanceRuntimeState.Unknown, Detail = "Runtime status could not be read" }; }
        }
        private bool ReadLedger(out string raw)
        { raw = null; try { return ledger.TryRead(out raw) && raw != null; } catch { return false; } }
        private bool SaveLedger(string raw)
        { try { string readback; return ledger.TryWrite(raw) && ReadLedger(out readback) && raw == readback; } catch { return false; } }
        private static bool Parse(string raw, out bool absent, out int original)
        {
            absent = false; original = 0;
            if (raw.Length > 128) return false;
            int separator = raw.IndexOf(Separator);
            if (separator >= 0 && (raw.IndexOf(Separator, separator + 1) >= 0 || raw.Substring(separator + 1) != "=1")) return false;
            string value = separator < 0 ? raw : raw.Substring(0, separator);
            if (value == ReversibleReg.Absent) { absent = true; return true; }
            if (value.StartsWith("=", StringComparison.Ordinal)) value = value.Substring(1);
            return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out original);
        }
    }

    internal static class MaintenancePause
    {
        internal const string JournalKey = "PrevMaintDisabled";
        private sealed class SettingsLedger : IOptionalServiceLedger
        {
            public bool TryRead(out string value) { return Settings.TryLoadStr(JournalKey, out value); }
            public bool TryWrite(string value) { return Settings.SaveStr(JournalKey, value); }
        }
        private static readonly MaintenancePauseEngine engine = new MaintenancePauseEngine(new MaintenanceControl(), new SettingsLedger());
        internal static bool ConfigurationOnly { get { return true; } }
        public static bool HasResidue { get { return engine.HasResidue; } }
        public static bool Active { get { return engine.Active; } }
        internal static MaintenanceRuntimeSnapshot QueryRuntime() { return engine.QueryRuntime(); }
        public static bool Activate()
        {
            lock (SystemConfigurationGate.Sync)
            {
                if (!Native.IsElevated()) return false;
                bool ok = engine.Activate();
                Logger.Log(Lang.T(ok ? "log.maint.2" : "log.maint.1"));
                return ok;
            }
        }
        public static bool Restore()
        {
            lock (SystemConfigurationGate.Sync)
            {
                bool had = engine.HasResidue, ok = engine.Restore();
                if (had) Logger.Log(Lang.T(ok ? "log.maint.3" : "log.maint.4"));
                return ok;
            }
        }
        public static void HealFromCrash() { if (HasResidue) Restore(); }
    }
}
