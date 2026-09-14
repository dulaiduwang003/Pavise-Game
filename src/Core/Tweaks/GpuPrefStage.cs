// @author bdth 2074055628@qq.com
// File purpose Pre-select the high performance GPU for the game during standby; dual-GPU machines only, restored on exit or disarm
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GpuPrefStage
    {
        private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
        // Session journal key shared with the restore-complete check; rename both sides together
        internal const string JournalKey = "GpuPrefStage";
        private static readonly object lk = new object();
        private static string pendingApplyReceipt;
        private static string pendingApplyNotIssued;
        private static string pendingRestoreNotIssued;
        private static string settledIdentity;
        internal enum WriteResult { Written, NotIssued, Unconfirmed }
        private sealed class StageRecord
        {
            internal char Phase;
            internal string Path, Original;
        }
        internal static object MutationGate { get { return lk; } }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal delegate bool ReadPreferenceOverride(string path, out string value);
        internal static ReadPreferenceOverride ReadPreferenceForTest;
        internal static Func<string, string, bool> WritePreferenceForTest;
        internal static Func<string, string, WriteResult> WriteResultForTest;
        internal static Func<bool> SupportedForTest;
        internal static void ForgetReceiptForTest()
        { lock (lk) { pendingApplyReceipt = pendingApplyNotIssued = pendingRestoreNotIssued = settledIdentity = null; } }
#endif

        public static bool Supported
        {
            get
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return SupportedForTest != null && SupportedForTest();
#else
                return GpuInventory.Hybrid;
#endif
            }
        }
        public static bool HasResidue
        {
            get { string raw; return !Settings.TryLoadStr(JournalKey, out raw) || raw.Length > 0; }
        }

        public static bool Stage(string exePath)
        {
            if (!ValidExecutablePath(exePath)) return false;
            lock (lk)
            {
                if (!Supported) return true;
                if (AppGpuPreferences.HasManagedPath(exePath)) return true;
                string journal;
                if (!Settings.TryLoadStr(JournalKey, out journal)) return false;
                if (journal.Length != 0)
                {
                    StageRecord staged;
                    if (!TryDecodeRecord(journal, out staged)) return false;
                    if (staged.Phase == 'O' && !IsSettled(staged)
                        && string.Equals(staged.Path, exePath, StringComparison.OrdinalIgnoreCase)) return true;
                    if (!RestoreLocked()) return false;
                }
                string shown = exePath;
                try { shown = System.IO.Path.GetFileName(exePath); } catch { }
                try
                {
                    string cur;
                    if (!TryReadPreference(exePath, out cur)) return false;
                    if (!ValidPreferenceFields(cur)) return false;
                    string pref = PrefFieldText.ReadField(cur, "GpuPreference");
                    if (pref == "1" || pref == "2") return true;
                    if (!string.IsNullOrEmpty(pref) && pref != "0") return false;
                    string line = EncodeJournal(exePath, cur);
                    pendingApplyReceipt = pendingApplyNotIssued = pendingRestoreNotIssued = settledIdentity = null;
                    if (!SaveJournal(line)) return false;
                    string before;
                    if (!TryReadPreference(exePath, out before) || !string.Equals(before, cur, StringComparison.Ordinal))
                    { pendingApplyNotIssued = line; return false; }
                    WriteResult written = WriteAndVerify(exePath, PrefFieldText.MergeField(cur, "GpuPreference", "2"));
                    if (written != WriteResult.Written)
                    {
                        if (written == WriteResult.NotIssued) pendingApplyNotIssued = line;
                        return false;
                    }
                    // A pending record alone can't prove the write actually happened
                    // If persisting ownership fails, keep the in-process receipt
                    pendingApplyReceipt = line;
                    if (!SaveJournal(EncodeRecord('O', exePath, cur))) return false;
                    pendingApplyReceipt = null;
                    Logger.Log(Lang.T("log.gpuprefstage.1") + shown + Lang.T("log.gpuprefstage.2"));
                    return true;
                }
                catch { return false; }
            }
        }

        public static bool Restore()
        {
            lock (lk) return RestoreLocked();
        }

        internal static bool TryReleaseForManualPreference(string path)
        {
            lock (lk)
            {
                string raw;
                if (!Settings.TryLoadStr(JournalKey, out raw)) return false;
                if (raw.Length == 0) return true;
                StageRecord record;
                if (!TryDecodeRecord(raw, out record)) return false;
                return !string.Equals(path, record.Path, StringComparison.OrdinalIgnoreCase) || RestoreLocked();
            }
        }

        internal static bool TryGetStagedOriginal(string path, out bool staged, out string original)
        {
            staged = false; original = null;
            lock (lk)
            {
                string raw;
                if (!Settings.TryLoadStr(JournalKey, out raw)) return false;
                if (raw.Length == 0) return true;
                StageRecord record;
                if (!TryDecodeRecord(raw, out record)) return false;
                if (!string.Equals(path, record.Path, StringComparison.OrdinalIgnoreCase)) return true;
                if (record.Phase == 'S' || IsSettled(record)) return true;
                string current;
                if (!TryReadPreference(path, out current)) return false;
                if (PrefFieldText.ReadField(current, "GpuPreference") != "2") return true;
                if (!ValidPreferenceFields(current)) return false;
                if (record.Phase == 'P' && string.Equals(raw, pendingApplyNotIssued, StringComparison.Ordinal)) return true;
                if (record.Phase != 'O'
                    && !(record.Phase == 'P' && string.Equals(raw, pendingApplyReceipt, StringComparison.Ordinal))
                    && !(record.Phase == 'R' && string.Equals(raw, pendingRestoreNotIssued, StringComparison.Ordinal)))
                    return false;
                staged = true; original = record.Original;
                return true;
            }
        }

        public static void HealFromCrash()
        {
            lock (lk) if (HasResidue && RestoreLocked()) Logger.Log(Lang.T("log.gpuprefstage.5"));
        }

        // For full reset only: records whose ownership can't be proven across processes (P/R left by a crash window, or corrupted
        //   records) can never be claimed by RestoreLocked, so reset would fail forever; once the user has
        //   explicitly asked to wipe all data, give up that restore duty and log it; no registry write, the residue
        //   is just that exe's GPU preference staying on high performance; provable records and transient failures are not abandoned
        internal static bool AbandonUnprovableForReset()
        {
            lock (lk)
            {
                string raw;
                if (!Settings.TryLoadStr(JournalKey, out raw)) return false;
                if (raw.Length == 0) return true;
                StageRecord record;
                string shown = null;
                if (TryDecodeRecord(raw, out record))
                {
                    bool unprovable =
                        (record.Phase == 'P'
                            && !string.Equals(raw, pendingApplyReceipt, StringComparison.Ordinal)
                            && !string.Equals(raw, pendingApplyNotIssued, StringComparison.Ordinal))
                        || (record.Phase == 'R'
                            && !string.Equals(raw, pendingRestoreNotIssued, StringComparison.Ordinal));
                    if (!unprovable) return false;
                    shown = record.Path;
                    try { shown = System.IO.Path.GetFileName(record.Path); } catch { }
                }
                if (!SaveJournal("")) return false;
                Logger.Warn(Lang.T("log.gpuprefstage.6") + (shown ?? "?") + Lang.T("log.gpuprefstage.7"));
                return true;
            }
        }

        private static bool RestoreLocked()
        {
            string raw;
            if (!Settings.TryLoadStr(JournalKey, out raw)) return false;
            if (raw.Length == 0) return true;
            StageRecord record;
            if (!TryDecodeRecord(raw, out record)) return false;
            // A settled receipt only needs deleting; whatever the user changes later doesn't matter
            if (record.Phase == 'S' || IsSettled(record)) return Settle(record);
            if (record.Phase == 'P' && string.Equals(raw, pendingApplyNotIssued, StringComparison.Ordinal))
                return Settle(record);
            string exePath = record.Path, original = record.Original;
            string shown = exePath;
            try { shown = System.IO.Path.GetFileName(exePath); } catch { }
            try
            {
                string cur;
                if (!TryReadPreference(exePath, out cur)) return false;
                // A preference the user or Windows set later wins over the 2 we wrote temporarily
                if (PrefFieldText.ReadField(cur, "GpuPreference") != "2") return Settle(record);
                if (!ValidPreferenceFields(cur)) return false;
                // Across a crash, P may mean nothing was written and R may mean the restore finished long ago
                // and the user chose 2 afterwards; neither can be used to infer ownership
                if ((record.Phase == 'R' && !string.Equals(raw, pendingRestoreNotIssued, StringComparison.Ordinal))
                    || (record.Phase == 'P'
                    && !string.Equals(raw, pendingApplyReceipt, StringComparison.Ordinal))) return false;
                string restoring = EncodeRecord('R', exePath, original);
                // An R read-back failure can't erase the fact that no registry restore was dispatched at the time
                // A new process still treats R as unknown
                pendingRestoreNotIssued = restoring;
                if (!SaveJournal(restoring)) return false;
                string before;
                if (!TryReadPreference(exePath, out before))
                { MarkRestoreNotIssued(record, restoring); return false; }
                // Once we've seen the user set a preference later, don't reclaim ownership
                // just because he picked 2 again afterwards
                if (PrefFieldText.ReadField(before, "GpuPreference") != "2") return Settle(record);
                if (!ValidPreferenceFields(before) || !string.Equals(before, cur, StringComparison.Ordinal))
                { MarkRestoreNotIssued(record, restoring); return false; }
                string back = original != null
                    ? PrefFieldText.RestoreField(cur, original, "GpuPreference")
                    : PrefFieldText.RemoveField(cur, "GpuPreference");
                pendingRestoreNotIssued = null;
                WriteResult written = WriteAndVerify(exePath, back.Length == 0 && original == null ? null : back);
                if (written != WriteResult.Written)
                {
                    if (written == WriteResult.NotIssued) MarkRestoreNotIssued(record, restoring);
                    return false;
                }
                if (!Settle(record)) return false;
                Logger.Log(Lang.T("log.gpuprefstage.3") + shown);
                return true;
            }
            catch
            {
                Logger.Log(Lang.T("log.gpuprefstage.4"));
                return false;
            }
        }

        internal static string EncodeJournal(string exePath, string original)
        {
            return EncodeRecord('P', exePath, original);
        }

        internal static bool DecodeJournal(string raw, out string exePath, out string original)
        {
            exePath = null; original = null;
            StageRecord record;
            if (!TryDecodeRecord(raw, out record)) return false;
            exePath = record.Path; original = record.Original;
            return true;
        }

        private static bool TryDecodeRecord(string raw, out StageRecord record)
        {
            record = null;
            if (string.IsNullOrEmpty(raw) || raw.Length > 262144) return false;
            string[] parts = raw.Split('|');
            bool legacy = parts.Length == 2;
            if (!legacy && (parts.Length != 4 || parts[0] != "2"
                || parts[1].Length != 1 || "PORS".IndexOf(parts[1][0]) < 0)) return false;
            try
            {
                int offset = legacy ? 0 : 2;
                string path = UnB64(parts[offset]);
                string original = parts[offset + 1] == "-" ? null : UnB64(parts[offset + 1]);
                if (!ValidExecutablePath(path) || !ValidPreferenceFields(original)) return false;
                string preference = PrefFieldText.ReadField(original, "GpuPreference");
                if (preference == "1" || preference == "2") return false;
                // The old two-column format has no write receipt; keep its restore data
                // but that evidence isn't enough to overwrite the current 2
                record = new StageRecord { Phase = legacy ? 'P' : parts[1][0], Path = path, Original = original };
                return true;
            }
            catch { return false; }
        }

        private static string EncodeRecord(char phase, string path, string original)
        {
            return "2|" + phase + "|" + B64(path) + "|" + (original == null ? "-" : B64(original));
        }

        private static bool Settle(StageRecord record)
        {
            settledIdentity = EncodeRecord('S', record.Path, record.Original);
            if (!SaveJournal(settledIdentity)) return false;
            pendingApplyReceipt = pendingApplyNotIssued = pendingRestoreNotIssued = null;
            if (!SaveJournal("")) return false;
            settledIdentity = null;
            return true;
        }

        private static bool IsSettled(StageRecord record)
        { return string.Equals(settledIdentity, EncodeRecord('S', record.Path, record.Original), StringComparison.Ordinal); }

        private static void MarkRestoreNotIssued(StageRecord record, string restoring)
        {
            // Only a result known to be not-issued keeps ownership; a write with an unknown outcome
            // always stays at R, and after restart it must not be retried as our own 2
            pendingRestoreNotIssued = restoring;
            if (SaveJournal(EncodeRecord('O', record.Path, record.Original))) pendingRestoreNotIssued = null;
        }

        private static bool ValidPreferenceFields(string value)
        {
            if (value == null) return true;
            if (value.Length > 65536 || value.IndexOf('\0') >= 0) return false;
            int count = 0;
            foreach (string token in value.Split(';'))
            {
                int equals = token.IndexOf('=');
                string field = (equals < 0 ? token : token.Substring(0, equals)).Trim();
                if (!string.Equals(field, "GpuPreference", StringComparison.OrdinalIgnoreCase)) continue;
                if (equals < 0 || ++count > 1 || token.Substring(equals + 1).Trim().Length == 0) return false;
            }
            return true;
        }

        private static bool ValidExecutablePath(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && path.Length <= 32767
                    && path.IndexOf('\0') < 0 && System.IO.Path.IsPathRooted(path)
                    && string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool SaveJournal(string value)
        {
            string readBack;
            return Settings.SaveStr(JournalKey, value) && Settings.TryLoadStr(JournalKey, out readBack)
                && string.Equals(value, readBack, StringComparison.Ordinal);
        }

        private static WriteResult WriteAndVerify(string path, string value)
        {
            WriteResult written = TryWritePreference(path, value);
            if (written != WriteResult.Written) return written;
            string current;
            return TryReadPreference(path, out current) && string.Equals(current, value, StringComparison.Ordinal)
                ? WriteResult.Written : WriteResult.Unconfirmed;
        }

        private static bool TryReadPreference(string path, out string value)
        {
            value = null;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return ReadPreferenceForTest != null && ReadPreferenceForTest(path, out value);
#else
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(GpuKey))
                {
                    object raw = key == null ? null : key.GetValue(path, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (raw != null && (!(raw is string) || key.GetValueKind(path) != RegistryValueKind.String)) return false;
                    value = raw as string;
                    return true;
                }
            }
            catch { return false; }
#endif
        }

        private static WriteResult TryWritePreference(string path, string value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (WriteResultForTest != null) return WriteResultForTest(path, value);
            if (WritePreferenceForTest == null) return WriteResult.NotIssued;
            return WritePreferenceForTest(path, value) ? WriteResult.Written : WriteResult.Unconfirmed;
#else
            bool issued = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(GpuKey))
                {
                    if (key == null) return WriteResult.NotIssued;
                    issued = true;
                    if (value == null) key.DeleteValue(path, false);
                    else key.SetValue(path, value, RegistryValueKind.String);
                    return WriteResult.Written;
                }
            }
            catch { return issued ? WriteResult.Unconfirmed : WriteResult.NotIssued; }
#endif
        }

        private static string B64(string s)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s ?? ""));
        }

        private static string UnB64(string s)
        {
            return new System.Text.UTF8Encoding(false, true).GetString(Convert.FromBase64String(s));
        }
    }
}
