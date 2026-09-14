using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        internal const string ExtremeSnapKey = "ExtremePowerKnobSnap";
        private static readonly Guid RetiredIdleCheck = new Guid("c4581c31-89ab-4597-8e2b-9c9cab440e6b");

        private sealed class ExtremeSavedValue
        {
            internal Guid Setting;
            internal uint Ac, Dc;
            internal string Text { get { return Setting.ToString("N") + "="
                + Ac.ToString(CultureInfo.InvariantCulture) + "," + Dc.ToString(CultureInfo.InvariantCulture); } }
        }

        private static bool IsCurrentExtremeKnob(Guid setting)
        {
            foreach (Knob knob in ExtremeKnobs) if (knob.Setting == setting) return true;
            return false;
        }

        // v2 binds the receipt to the scheme; legacy managed-scheme receipts with only GUID=AC,DC are still supported
        private static bool ReadExtremeSnapshot(Guid scheme, out List<ExtremeSavedValue> values)
        {
            values = new List<ExtremeSavedValue>();
            string text;
            if (!Settings.TryLoadStr(ExtremeSnapKey, out text)) return false;
            if (text.Length == 0) return true;
            if (text.IndexOf('|') >= 0)
            {
                string[] header = text.Split('|');
                Guid owner;
                if (header.Length != 3 || header[0] != "2" || !Guid.TryParse(header[1], out owner)
                    || owner == Guid.Empty || owner != scheme) return false;
                text = header[2];
            }
            var seen = new HashSet<Guid>();
            foreach (string part in text.Split(';'))
            {
                string[] pair = part.Split('=');
                Guid setting;
                if (pair.Length != 2 || !Guid.TryParse(pair[0], out setting) || !seen.Add(setting)
                    || (!IsCurrentExtremeKnob(setting) && setting != IdleDemote && setting != RetiredIdleCheck)) return false;
                string[] indices = pair[1].Split(',');
                uint ac, dc;
                if (indices.Length != 2 || !uint.TryParse(indices[0], NumberStyles.None, CultureInfo.InvariantCulture, out ac)
                    || !uint.TryParse(indices[1], NumberStyles.None, CultureInfo.InvariantCulture, out dc)) return false;
                values.Add(new ExtremeSavedValue { Setting = setting, Ac = ac, Dc = dc });
            }
            return true;
        }

        private static bool SaveExtremeSnapshot(Guid scheme, List<ExtremeSavedValue> values)
        {
            var parts = new List<string>();
            foreach (ExtremeSavedValue value in values) parts.Add(value.Text);
            string text = parts.Count == 0 ? "" : "2|" + scheme.ToString("N") + "|" + string.Join(";", parts.ToArray());
#if PAVISE_SELFTEST
            if (ExtremeSaveSnapshotForTest != null) return ExtremeSaveSnapshotForTest(text);
#endif
            return Settings.SaveStrDurable(ExtremeSnapKey, text);
        }

        // Called only on the path where the managed scheme was deleted successfully; no deletion here, and no clearing other schemes' receipts along the way
        private static bool ForgetDeletedExtremeSnapshot(Guid scheme)
        {
            List<ExtremeSavedValue> values;
            return ReadExtremeSnapshot(scheme, out values)
                && (values.Count == 0 || SaveExtremeSnapshot(scheme, new List<ExtremeSavedValue>()));
        }

        // Drop the receipt only when a complete enumeration confirms the original scheme is gone; read failures or unknown formats keep it
        // The scheme being configured has an identity the caller already confirmed, do not overturn it by enumerating again
        private static bool DropOrphanExtremeSnapshot(Guid configuringScheme)
        {
            string text;
            if (!Settings.TryLoadStr(ExtremeSnapKey, out text) || text.Length == 0) return false;
            if (text.IndexOf('|') < 0) return false;
            string[] header = text.Split('|');
            Guid owner;
            if (header.Length != 3 || header[0] != "2"
                || !Guid.TryParse(header[1], out owner) || owner == Guid.Empty
                || owner == configuringScheme) return false;
            List<Guid> schemes;
            if (!TryEnumerateSchemes(out schemes) || schemes.Contains(owner)) return false;
            if (!SaveExtremeSnapshot(owner, new List<ExtremeSavedValue>())) return false;
            Logger.Log(Lang.T("log.powerplanschemes.extremeOrphan"));
            return true;
        }

        private static bool PrepareExtremeKnobs(Guid scheme, bool extreme, out List<ExtremeSavedValue> snapshot)
        {
            snapshot = null;
            bool incomplete = false;
            bool ready = RestoreExtremeKnobs(scheme, extreme);
            if (!ready && DropOrphanExtremeSnapshot(scheme))
                ready = RestoreExtremeKnobs(scheme, extreme);
            if (ready && extreme)
                ready = SnapshotExtremeKnobs(scheme, out incomplete) && ReadExtremeSnapshot(scheme, out snapshot);
            extremeTunePending = !ready || incomplete;
            return ready;
        }

        private static bool SnapshotExtremeKnobs(Guid scheme, out bool incomplete)
        {
            incomplete = false;
            List<ExtremeSavedValue> values;
            if (!ReadExtremeSnapshot(scheme, out values)) return false;
            foreach (Knob knob in ExtremeKnobs)
            {
                if (values.Exists(delegate(ExtremeSavedValue v) { return v.Setting == knob.Setting; })) continue;
                uint? ac = ReadExtremeIndex(scheme, knob.Setting, true);
                uint? dc = ReadExtremeIndex(scheme, knob.Setting, false);
                if (!ac.HasValue && !dc.HasValue) continue;
                // Readable on one side only cannot count as an unexposed item; skip this round's write but keep the retry state
                if (!ac.HasValue || !dc.HasValue) { incomplete = true; continue; }
                values.Add(new ExtremeSavedValue { Setting = knob.Setting, Ac = ac.Value, Dc = dc.Value });
            }
            return SaveExtremeSnapshot(scheme, values);
        }

        // retiredOnly=true migrates retired items only; original receipts for current idle policy items are kept
        // Read back after writing each side; partial failure keeps the ledger, calling restore is not the same as restore complete
        private static bool RestoreExtremeKnobs(Guid scheme, bool retiredOnly)
        {
            List<ExtremeSavedValue> values;
            if (!ReadExtremeSnapshot(scheme, out values)) return false;
            if (values.Count == 0) return true;
            var pending = new List<ExtremeSavedValue>();
            bool restored = true;
            foreach (ExtremeSavedValue value in values)
            {
                if (retiredOnly && IsCurrentExtremeKnob(value.Setting)) { pending.Add(value); continue; }
                if (!RestoreExtremeIndex(scheme, value.Setting, true, value.Ac)
                    || !RestoreExtremeIndex(scheme, value.Setting, false, value.Dc))
                {
                    pending.Add(value); restored = false;
                }
            }
            return SaveExtremeSnapshot(scheme, pending) && restored;
        }

        private static bool RestoreExtremeIndex(Guid scheme, Guid setting, bool ac, uint original)
        {
            uint? current = ReadExtremeIndex(scheme, setting, ac);
            if (!current.HasValue) return false;
            if (current.Value == original) return true;
#if PAVISE_SELFTEST
            if (ExtremeWriteIndexForTest == null) throw new InvalidOperationException("Unmocked extreme power write");
            if (!ExtremeWriteIndexForTest(scheme, setting, ac, original)) return false;
#else
            if (!(ac ? WriteAc(scheme, SubProcessor, setting, original) : WriteDc(scheme, SubProcessor, setting, original))) return false;
#endif
            return ReadExtremeIndex(scheme, setting, ac) == original;
        }

        private static uint? ReadExtremeIndex(Guid scheme, Guid setting, bool ac)
        {
#if PAVISE_SELFTEST
            if (ExtremeReadIndexForTest == null) throw new InvalidOperationException("Unmocked extreme power read");
            return ExtremeReadIndexForTest(scheme, setting, ac);
#else
            uint value;
            return (ac ? ReadAc(scheme, SubProcessor, setting, out value)
                : ReadDc(scheme, SubProcessor, setting, out value)) ? (uint?)value : null;
#endif
        }

#if PAVISE_SELFTEST
        internal static Func<Guid, Guid, bool, uint?> ExtremeReadIndexForTest;
        internal static Func<Guid, Guid, bool, uint, bool> ExtremeWriteIndexForTest;
        internal static Func<string, bool> ExtremeSaveSnapshotForTest;
        internal static bool SnapshotExtremeForTest(Guid scheme)
        {
            bool incomplete;
            return SnapshotExtremeKnobs(scheme, out incomplete);
        }
        internal static bool PrepareExtremeForTest(Guid scheme, bool extreme)
        {
            List<ExtremeSavedValue> snapshot;
            return PrepareExtremeKnobs(scheme, extreme, out snapshot);
        }
        internal static bool DropOrphanExtremeForTest(Guid scheme) { return DropOrphanExtremeSnapshot(scheme); }
        internal static bool RestoreExtremeForTest(Guid scheme, bool retiredOnly) { return RestoreExtremeKnobs(scheme, retiredOnly); }
        internal static bool ForgetDeletedExtremeForTest(Guid scheme) { return ForgetDeletedExtremeSnapshot(scheme); }
#endif
    }
}
