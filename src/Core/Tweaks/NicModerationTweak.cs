// @author bdth 2074055628@qq.com
// File purpose Interrupt moderation experiment on the physical wired default egress; standard 0/1 only, receipt first, takes effect after reboot, reversible
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum NicLegacyValueDecision
    {
        ClearReceipt,
        RestoreOriginal
    }

    // Microsoft's *InterruptModeration only covers Off and Enabled; whether Enabled then goes
    // Adaptive, Medium or a vendor's own algorithm isn't in the standard key; no matching on localized DisplayName here
    // and no guessing private ITR values; the experiment, only after explicit user confirmation, stages the sole physical default egress
    // from Driver-managed to Off; the driver's original value, stable interface identity and the applied value all go in the record
    // Restore follows CAS rules so new changes by the user or other tools aren't overwritten
    internal static class NicModerationTweak
    {
        private const string ClassRoot =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        private const string ConnectionRoot =
            @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";
        private const string ModerationValue = "*InterruptModeration";

        // V1 is the receipt left by the 2.1.3.3 version that disabled all physical wired NICs uniformly; kept only for upgrade restore
        private const string LegacyListKey = "NicImList";
        private const string LegacyFlagKey = "NicImOffByPavise";
        private const char LegacyAppliedSep = '\u001F';
        internal const string ReceiptKey = "NicImStrategyV2";

        private static readonly object lk = new object();
        private static readonly SettingsReceiptStore receiptStore = new SettingsReceiptStore();
        private static readonly WindowsPlatform windows = new WindowsPlatform();
        private static readonly NicModerationStrategy strategy =
            new NicModerationStrategy(windows, receiptStore);

        public static bool EnabledByPavise { get { return HasResidue(); } }
        internal static bool HasLegacyResidue
        {
            get
            {
                return Settings.Load(LegacyFlagKey, false)
                    || ParseList(Settings.LoadStr(LegacyListKey, "")).Length > 0
                    || LegacyReceiptIds().Length > 0;
            }
        }

        public static List<NicModerationTarget> Scan()
        {
            NicModerationScan scan = windows.Scan();
            return scan != null && scan.Success
                ? scan.Targets : new List<NicModerationTarget>();
        }

        public static bool ModerationActive()
        {
            return HasResidue();
        }

        public static string Describe()
        {
            bool confirmed, recovery;
            return Describe(out confirmed, out recovery);
        }

        internal static string Describe(out bool confirmed, out bool recovery)
        {
            confirmed = false;
            recovery = false;
            if (HasLegacyResidue)
            {
                recovery = true;
                return Lang.T("t.nicim.legacy");
            }

            NicModerationReceipt receipt;
            NicModerationScanIssue issue;
            if (!strategy.TryReceipt(out receipt, out issue))
            {
                recovery = true;
                return IssueText(issue);
            }
            if (receipt != null)
            {
                recovery = true;
                string receiptLabel = string.IsNullOrEmpty(receipt.Label)
                    ? receipt.NetCfgInstanceId : receipt.Label;
                if (!receipt.Applied) return Lang.F("t.nicim.prepared", receiptLabel);
                NicModerationScan receiptScan = windows.Scan();
                if (receiptScan == null || !receiptScan.Success
                        || !receiptScan.LiveStateReliable)
                    return IssueText(NicModerationScanIssue.ReadFailed);
                NicModerationTarget current = NicModerationStrategy.FindIdentity(
                    receiptScan.Targets, receipt);
                if (current == null) return IssueText(NicModerationScanIssue.IdentityChanged);
                if (!current.ModeReadReliable)
                    return IssueText(NicModerationScanIssue.ReadFailed);
                if (current.Mode != receipt.Desired)
                    return IssueText(NicModerationScanIssue.RecoveryPending);
                confirmed = true;
                recovery = false;
                return Lang.F("t.nicim.applied", receiptLabel);
            }

            NicModerationTarget target = strategy.Inspect(out issue);
            if (target == null) return IssueText(issue);
            return Lang.F(target.Mode == NicModerationMode.Off
                    ? "t.nicim.externaloff" : "t.nicim.driver",
                string.IsNullOrEmpty(target.Label) ? target.NetCfgInstanceId : target.Label);
        }

        public static bool Enable()
        {
            bool result;
            lock (lk)
            {
                // The new policy doesn't inherit the old policy's ownership semantics; settle the old records cleanly first, then let the user start the single-egress experiment himself
                // An old Off receipt must not be interpreted as a new receipt
                if (HasLegacyResidue && !RestoreLegacyLocked())
                {
                    Logger.Log(Lang.T("log.nicim.legacyfail"));
                    return false;
                }

                NicModerationScanIssue issue;
                bool ok = strategy.ApplyExperimentalOff(out issue);
                NicModerationTarget target = null;
                NicModerationScanIssue inspectIssue;
                try { target = strategy.Inspect(out inspectIssue); } catch { }
                string label = target == null ? "" : " " + target.Label;
                if (ok && issue == NicModerationScanIssue.Applied)
                    Logger.Log(Lang.T("log.nicim.applied") + label);
                else if (ok && issue == NicModerationScanIssue.AlreadyOff)
                    Logger.Log(Lang.T("log.nicim.externaloff") + label);
                else if (!ok)
                    Logger.Log(Lang.T("log.nicim.failed") + IssueText(issue) + label);
                result = ok;
            }
            // Legacy migration runs outside the nic lock; the Extreme ledger is withdrawn, so no cross-lock ordering issue remains here
            // The old token must never be interpreted as ownership of the V2 experiment either
            return result;
        }

        public static bool Restore()
        {
            bool result;
            lock (lk)
            {
                NicModerationScanIssue issue;
                bool current = strategy.Restore(out issue);
                if (current && issue == NicModerationScanIssue.ExternalChanged)
                    Logger.Log(Lang.T("log.nicim.externalchanged"));
                else if (!current)
                    Logger.Log(Lang.T("log.nicim.restorefailed") + IssueText(issue));

                bool legacy = RestoreLegacyLocked();
                bool all = current && legacy;
                if (all && issue != NicModerationScanIssue.ExternalChanged)
                    Logger.Log(Lang.T("log.nicim.restored"));
                else if (!legacy) Logger.Log(Lang.T("log.nicim.legacyfail"));
                result = all;
            }
            return result;
        }

        // Upgrading to the new policy only takes back the V1 batch of writes
        // V2 is an experiment the user started in the new UI; a normal startup must not turn it off for him
        public static bool MigrateLegacy()
        {
            bool ok;
            lock (lk)
            {
                ok = !HasLegacyResidue || RestoreLegacyLocked();
                Logger.Log(Lang.T(ok ? "log.nicim.migrated" : "log.nicim.legacyfail"));
            }
            return ok;
        }

        // Startup reconciliation of Prepared, also handling the second crash window
        // the one where Original is already written back but the Applied receipt isn't cleared yet; a normal Applied+Desired experiment is left alone
        public static void ReconcileStartup()
        {
            lock (lk)
            {
                NicModerationScanIssue issue;
                bool ok = strategy.ReconcileStartup(out issue);
                if (ok && issue == NicModerationScanIssue.None) return;
                if (ok && issue == NicModerationScanIssue.ExternalChanged)
                    Logger.Log(Lang.T("log.nicim.externalchanged"));
                else
                    Logger.Log(Lang.T(ok ? "log.nicim.healed" : "log.nicim.healfail")
                        + (ok ? "" : IssueText(issue)));
            }
        }

        public static bool HasResidue()
        {
            return HasLegacyResidue || strategy.HasReceipt;
        }

        // Wipe all is the only path allowed to drop restore evidence; corrupted or unreadable receipts only clear the apply record, never guess the system's original value
        // Device uninstalled with its stable identity gone can be settled the same way
        // A normal disable always keeps these receipts to avoid a silent overwrite or a wrong restore
        public static bool AbandonUnprovableForReset()
        {
            lock (lk)
            {
                NicModerationReceipt receipt;
                NicModerationScanIssue issue;
                bool appCleared = true;
                if (!strategy.TryReceipt(out receipt, out issue))
                {
                    appCleared = strategy.DiscardReceiptForReset();
                }
                else if (receipt != null)
                {
                    NicModerationScan scan = windows.Scan();
                    if (scan == null || !scan.Success || !scan.LiveStateReliable) return false;
                    int matches = NicModerationStrategy.CountIdentityMatches(
                        scan.Targets, receipt);
                    if (matches > 1) return false;
                    if (matches == 1)
                    {
                        NicModerationTarget current = NicModerationStrategy.FindIdentity(
                            scan.Targets, receipt);
                        // Clearing the receipt while the target still reads Pavise Desired would leave an ownerless Off
                        // Only when Original or an external value has taken over may an explicit reset settle the apply record
                        if (current == null || !current.ModeReadReliable
                                || current.Mode == receipt.Desired) return false;
                    }
                    appCleared = strategy.DiscardReceiptForReset();
                }

                bool legacyCleared = DiscardLegacyReceiptsForResetLocked();
                bool all = appCleared && legacyCleared && !HasResidue();
                if (all) Logger.Log(Lang.T("log.nicim.abandoned"));
                return all;
            }
        }

        private static bool DiscardLegacyReceiptsForResetLocked()
        {
            List<string> ids = BuildLegacyIds();
            // Full precheck first, so we don't get halfway through clearing before finding a still-valid legacy record with Pavise Desired=0
            foreach (string id in ids) if (!CanDiscardLegacySlot(id)) return false;
            bool all = true;
            foreach (string id in ids)
            {
                string slot = "NicIm_" + id;
                all &= Settings.SaveStrDurable(slot, "");
                string actual;
                all &= Settings.TryLoadStr(slot, out actual) && actual.Length == 0;
            }
            all &= Settings.SaveStrDurable(LegacyListKey, "");
            all &= Settings.SaveDurable(LegacyFlagKey, false);
            return all;
        }

        private static bool RestoreLegacyLocked()
        {
            List<string> ids = BuildLegacyIds();
            bool all = true;
            foreach (string id in ids) all &= RestoreLegacySlot(id);
            if (!all) return false;

            bool listCleared = Settings.SaveStrDurable(LegacyListKey, "")
                && Settings.LoadStr(LegacyListKey, "x").Length == 0;
            bool flagCleared = Settings.SaveDurable(LegacyFlagKey, false)
                && !Settings.Load(LegacyFlagKey, true);
            return listCleared && flagCleared;
        }

        private static List<string> BuildLegacyIds()
        {
            var ids = new List<string>();
            foreach (string id in ParseList(Settings.LoadStr(LegacyListKey, "")))
                if (IsLegacyId(id) && !ids.Contains(id)) ids.Add(id);
            foreach (string id in LegacyReceiptIds())
                if (IsLegacyId(id) && !ids.Contains(id)) ids.Add(id);
            return ids;
        }

        private static bool IsLegacyId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length != 4) return false;
            for (int i = 0; i < id.Length; i++)
                if (id[i] < '0' || id[i] > '9') return false;
            return true;
        }

        private static bool TryReadLegacyReceipt(string id, out string original,
            out bool originalAbsent)
        {
            original = "";
            originalAbsent = false;
            string stored;
            if (!Settings.TryLoadStr("NicIm_" + id, out stored)
                || string.IsNullOrEmpty(stored)) return false;
            int sep = stored.LastIndexOf(LegacyAppliedSep);
            string originalRepr = sep < 0 ? stored : stored.Substring(0, sep);
            string appliedRepr = sep < 0 ? "=0" : stored.Substring(sep + 1);
            string applied;
            bool appliedAbsent;
            if (!TryDecodeLegacyString(originalRepr, out original,
                    out originalAbsent)
                || !TryDecodeLegacyString(appliedRepr, out applied,
                    out appliedAbsent) || appliedAbsent || applied != "0") return false;
            return true;
        }

        private static bool TryDecodeLegacyString(string repr, out string value,
            out bool absent)
        {
            value = "";
            absent = false;
            if (string.IsNullOrEmpty(repr)) return false;
            if (repr == ReversibleReg.Absent) { absent = true; return true; }
            value = repr[0] == '=' ? repr.Substring(1) : repr;
            return true;
        }

        private static bool RestoreLegacySlot(string id)
        {
            string rawReceipt;
            if (!Settings.TryLoadStr("NicIm_" + id, out rawReceipt)) return false;
            // The list and flag may crash after the slot was settled; an empty slot is an orphan beacon that's safe to finish off
            if (string.IsNullOrEmpty(rawReceipt)) return true;
            string original;
            bool originalAbsent;
            if (!TryReadLegacyReceipt(id, out original, out originalAbsent)) return false;
            string slot = "NicIm_" + id;
            try
            {
                using (RegistryKey node = Registry.LocalMachine.OpenSubKey(
                    ClassRoot + @"\" + id, true))
                {
                    if (node == null) return ClearLegacySlot(slot);
                    // V1 has no stable identity; only CAS when the original 000x row is still physical Ethernet
                    // and the current value exactly equals the legacy policy's Desired=0; any other value counts as external takeover
                    bool rowEligible = HardwareRowEligible(node.GetValue("*IfType"),
                        node.GetValue("Characteristics"));
                    object raw = node.GetValue(ModerationValue);
                    if (raw == null) return ClearLegacySlot(slot);
                    RegistryValueKind kind;
                    try { kind = node.GetValueKind(ModerationValue); }
                    catch { return false; }
                    if (DecideLegacyValue(rowEligible, raw, kind, original,
                            originalAbsent) == NicLegacyValueDecision.ClearReceipt)
                        return ClearLegacySlot(slot);

                    if (originalAbsent) node.DeleteValue(ModerationValue, false);
                    else node.SetValue(ModerationValue, original, RegistryValueKind.String);
                    node.Flush();
                    object actual = node.GetValue(ModerationValue);
                    if (originalAbsent)
                    {
                        if (actual != null) return false;
                    }
                    else
                    {
                        RegistryValueKind actualKind;
                        try { actualKind = node.GetValueKind(ModerationValue); }
                        catch { return false; }
                        if (actualKind != RegistryValueKind.String
                            || !string.Equals(actual as string, original,
                                StringComparison.Ordinal)) return false;
                    }
                }
                return ClearLegacySlot(slot);
            }
            catch { return false; }
        }

        private static bool CanDiscardLegacySlot(string id)
        {
            string original;
            bool originalAbsent;
            if (!TryReadLegacyReceipt(id, out original, out originalAbsent)) return true;
            try
            {
                using (RegistryKey node = Registry.LocalMachine.OpenSubKey(
                    ClassRoot + @"\" + id))
                {
                    if (node == null) return true;
                    bool rowEligible = HardwareRowEligible(node.GetValue("*IfType"),
                        node.GetValue("Characteristics"));
                    object raw = node.GetValue(ModerationValue);
                    if (raw == null) return true;
                    RegistryValueKind kind;
                    try { kind = node.GetValueKind(ModerationValue); }
                    catch { return false; }
                    return DecideLegacyValue(rowEligible, raw, kind, original,
                        originalAbsent) != NicLegacyValueDecision.RestoreOriginal;
                }
            }
            catch { return false; }
        }

        private static bool ClearLegacySlot(string slot)
        {
            if (!Settings.SaveStrDurable(slot, "")) return false;
            string actual;
            return Settings.TryLoadStr(slot, out actual) && actual.Length == 0;
        }

        private static string[] LegacyReceiptIds()
        {
            var ids = new List<string>();
#if PAVISE_SELFTEST
            return ids.ToArray();
#else
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Pavise"))
                {
                    if (key == null) return ids.ToArray();
                    foreach (string name in key.GetValueNames())
                    {
                        if (!name.StartsWith("NicIm_", StringComparison.OrdinalIgnoreCase)
                            || name.Length != 10) continue;
                        object raw = key.GetValue(name);
                        if (raw == null || string.IsNullOrEmpty(raw.ToString())) continue;
                        string id = name.Substring(6);
                        int numeric;
                        if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture,
                                out numeric) && !ids.Contains(id)) ids.Add(id);
                    }
                }
            }
            catch { }
            return ids.ToArray();
#endif
        }

        internal static NicModerationMode ParseMode(object raw)
        {
            string text = raw as string;
            if (text == "0") return NicModerationMode.Off;
            if (text == "1") return NicModerationMode.DriverManaged;
            return NicModerationMode.Unknown;
        }

        internal static NicModerationMode ParseModeForKind(object raw,
            RegistryValueKind kind)
        {
            return kind == RegistryValueKind.String
                ? ParseMode(raw) : NicModerationMode.Unknown;
        }

        internal static NicLegacyValueDecision DecideLegacyValue(bool rowEligible,
            object raw, RegistryValueKind kind, string original, bool originalAbsent)
        {
            if (!rowEligible || raw == null || kind != RegistryValueKind.String)
                return NicLegacyValueDecision.ClearReceipt;
            string current = raw as string;
            if (current == null || (!originalAbsent
                    && string.Equals(current, original, StringComparison.Ordinal)))
                return NicLegacyValueDecision.ClearReceipt;
            return current == "0" ? NicLegacyValueDecision.RestoreOriginal
                : NicLegacyValueDecision.ClearReceipt;
        }

        internal static bool NetworkDirectExcluded(object networkDirect,
            object networkDirectTechnology)
        {
            if (networkDirect == null)
                return networkDirectTechnology != null;
            int value;
            // Standard key present but in an unrecognized form; don't read 'can't prove it's off' as 'this is a plain NIC'
            return !TryNumeric(networkDirect, out value) || value != 0;
        }

        internal static bool PhysicalSourceConflict(bool wmiReadOk, bool linkUp,
            bool classPhysicalWired, bool wmiPhysical)
        {
            // When WMI succeeds it is the second independent source of physical devices
            // Class key claims physical NIC but the active interface isn't in that set: most likely a virtual or VPN driver misreport, fail closed
            return wmiReadOk && linkUp && classPhysicalWired && !wmiPhysical;
        }

        internal static bool TryResolveConsistentDeviceId(string direct,
            bool wmiReadOk, bool wmiMapped, string wmiValue, out string resolved)
        {
            direct = direct ?? "";
            wmiValue = wmiValue ?? "";
            resolved = "";
            if (!wmiReadOk)
            {
                // If WMI is temporarily unavailable, keep the class key identity so old receipts can do CAS restore
                // The scan sets LiveStateReliable to false, so this branch never establishes a new experiment
                if (direct.Length == 0) return false;
                resolved = direct;
                return true;
            }
            if (!wmiMapped || wmiValue.Length == 0) return false;
            if (direct.Length > 0 && !string.Equals(direct, wmiValue,
                    StringComparison.OrdinalIgnoreCase)) return false;
            resolved = wmiValue;
            return true;
        }

        internal static void MergePhysicalPnpId(Dictionary<string, string> ids,
            string guid, string pnp)
        {
            string existing;
            pnp = pnp ?? "";
            if (ids.TryGetValue(guid, out existing))
            {
                if (!string.Equals(existing, pnp,
                        StringComparison.OrdinalIgnoreCase)) ids[guid] = "";
            }
            else ids[guid] = pnp;
        }

        // Row-level check accepts only standard 0 and 1; unknown values, non-string values, wireless and non-physical devices all fail closed
        internal static bool RowEligible(object moderation, object ifType, object characteristics)
        {
            int typeValue, flags;
            return ParseMode(moderation) != NicModerationMode.Unknown
                && TryNumeric(ifType, out typeValue) && typeValue == 6
                && TryNumeric(characteristics, out flags) && (flags & 0x4) != 0;
        }

        private static bool HardwareRowEligible(object ifType, object characteristics)
        {
            int typeValue, flags;
            return TryNumeric(ifType, out typeValue) && typeValue == 6
                && TryNumeric(characteristics, out flags) && (flags & 0x4) != 0;
        }

        internal static bool TryNumeric(object raw, out int value)
        {
            if (raw is int) { value = (int)raw; return true; }
            string text = raw as string;
            if (text != null && int.TryParse(text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value)) return true;
            value = 0;
            return false;
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string IssueText(NicModerationScanIssue issue)
        {
            switch (issue)
            {
                case NicModerationScanIssue.ReadFailed: return Lang.T("t.nicim.readfailed");
                case NicModerationScanIssue.NoSupportedAdapter: return Lang.T("t.nicim.none");
                case NicModerationScanIssue.NoActiveAdapter: return Lang.T("t.nicim.noactive");
                case NicModerationScanIssue.AmbiguousActiveAdapters: return Lang.T("t.nicim.ambiguous");
                case NicModerationScanIssue.NoDefaultRoute: return Lang.T("t.nicim.noroute");
                case NicModerationScanIssue.DefaultRouteNotPhysical: return Lang.T("t.nicim.routeother");
                case NicModerationScanIssue.UnsafeAdapter: return Lang.T("t.nicim.unsafe");
                case NicModerationScanIssue.UnsupportedValue: return Lang.T("t.nicim.unsupported");
                case NicModerationScanIssue.IdentityUnavailable: return Lang.T("t.nicim.noidentity");
                case NicModerationScanIssue.ReceiptUnreadable: return Lang.T("t.nicim.receiptread");
                case NicModerationScanIssue.ReceiptCorrupt: return Lang.T("t.nicim.receiptbad");
                case NicModerationScanIssue.RecoveryPending: return Lang.T("t.nicim.recovery");
                case NicModerationScanIssue.IdentityChanged: return Lang.T("t.nicim.identity");
                case NicModerationScanIssue.ReceiptSaveFailed: return Lang.T("t.nicim.receiptsave");
                case NicModerationScanIssue.WriteFailed: return Lang.T("t.nicim.writefailed");
                default: return Lang.T("t.nicim.unmanaged");
            }
        }

        private sealed class SettingsReceiptStore : INicModerationReceiptStore
        {
            public bool TryRead(out string raw)
            {
                return Settings.TryLoadStr(ReceiptKey, out raw);
            }

            public bool SaveAndVerify(string raw)
            {
                raw = raw ?? "";
                if (!Settings.SaveStrDurable(ReceiptKey, raw)) return false;
                string actual;
                return Settings.TryLoadStr(ReceiptKey, out actual)
                    && string.Equals(actual, raw, StringComparison.Ordinal);
            }
        }

        private sealed class WindowsPlatform : INicModerationPlatform
        {
            [DllImport("iphlpapi.dll")]
            private static extern int GetBestInterface(uint destinationAddress,
                out uint bestInterfaceIndex);

            public NicModerationScan Scan()
            {
                var result = new NicModerationScan();
                var interfaces = new Dictionary<string, NetworkInterface>(
                    StringComparer.OrdinalIgnoreCase);
                bool liveStateReliable = true;
                try
                {
                    foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        string id = NormalizeGuid(nic.Id);
                        if (id.Length > 0) interfaces[id] = nic;
                    }
                }
                catch { liveStateReliable = false; }

                bool pnpReadOk;
                Dictionary<string, string> pnpIds = ReadPhysicalPnpIds(out pnpReadOk);
                if (!pnpReadOk) liveStateReliable = false;
                var mappedClassRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    using (RegistryKey root = Registry.LocalMachine.OpenSubKey(ClassRoot))
                    {
                        if (root == null)
                        {
                            result.BestInterfaceIndex = BestInterfaceIndex();
                            result.LiveStateReliable = liveStateReliable;
                            result.Success = true;
                            return result;
                        }
                        foreach (string sub in root.GetSubKeyNames())
                        {
                            if (sub.Length != 4) continue;
                            using (RegistryKey node = root.OpenSubKey(sub))
                            {
                                if (node == null) continue;
                                if (!HardwareRowEligible(node.GetValue("*IfType"),
                                        node.GetValue("Characteristics"))) continue;
                                string netCfg = node.GetValue("NetCfgInstanceId") as string;
                                if (string.IsNullOrEmpty(netCfg)) continue;
                                string normalized = NormalizeGuid(netCfg);
                                if (normalized.Length > 0) mappedClassRows.Add(normalized);
                                NetworkInterface nic;
                                bool interfaceKnown = interfaces.TryGetValue(normalized, out nic);
                                bool connectionKnown;
                                bool connectionReadOk = TryConnectionKnown(netCfg,
                                    out connectionKnown);
                                if (!connectionReadOk) liveStateReliable = false;
                                // A class key that both independent sources say is absent is treated as an uninstall ghost
                                // If even one source still sees the device, keep it conservatively and forbid new writes until state is reliable
                                if (connectionReadOk && !connectionKnown && !interfaceKnown) continue;
                                if (!connectionKnown && interfaceKnown) liveStateReliable = false;

                                var target = new NicModerationTarget();
                                target.SubKey = sub;
                                target.NetCfgInstanceId = netCfg;
                                string directDeviceId = (node.GetValue(
                                    "DeviceInstanceID") as string) ?? "";
                                string wmiDeviceId;
                                bool wmiMapped = pnpIds.TryGetValue(normalized,
                                    out wmiDeviceId);
                                string deviceId;
                                bool identityConsistent = TryResolveConsistentDeviceId(
                                    directDeviceId, pnpReadOk, wmiMapped, wmiDeviceId,
                                    out deviceId);
                                if (!identityConsistent)
                                    liveStateReliable = false;
                                target.DeviceInstanceId = deviceId;
                                target.Label = (node.GetValue("DriverDesc") as string) ?? sub;
                                target.DriverVersion = (node.GetValue("DriverVersion") as string) ?? "";
                                target.Service = (node.GetValue("Service") as string) ?? "";
                                target.InfPath = (node.GetValue("InfPath") as string) ?? "";
                                // Missing or private values still count toward the number of active physical NICs
                                // they just can never be a write target
                                target.Mode = ReadMode(node, out target.ModeReadReliable);
                                string component = (node.GetValue("ComponentId") as string) ?? "";
                                bool aggregate = IsSoftwareAggregate(component,
                                    target.Service);
                                target.PhysicalWired = !aggregate
                                    && (!pnpReadOk || identityConsistent);
                                target.Excluded = NetworkDirectExcluded(
                                    node.GetValue("*NetworkDirect"),
                                    node.GetValue("*NetworkDirectTechnology"));
                                target.Excluded |= aggregate;

                                if (interfaceKnown)
                                {
                                    target.LinkUp = nic.OperationalStatus == OperationalStatus.Up;
                                    if (!IsWiredEthernet(nic.NetworkInterfaceType))
                                        target.PhysicalWired = false;
                                    if (PhysicalSourceConflict(pnpReadOk, target.LinkUp,
                                            target.PhysicalWired,
                                            pnpIds.ContainsKey(normalized)))
                                    {
                                        target.PhysicalWired = false;
                                        liveStateReliable = false;
                                    }
                                    try
                                    {
                                        IPv4InterfaceProperties ipv4 = nic.GetIPProperties()
                                            .GetIPv4Properties();
                                        target.InterfaceIndex = ipv4 == null ? -1 : ipv4.Index;
                                        if (target.LinkUp && target.PhysicalWired
                                            && target.InterfaceIndex <= 0) liveStateReliable = false;
                                    }
                                    catch
                                    {
                                        target.InterfaceIndex = -1;
                                        if (target.LinkUp && target.PhysicalWired)
                                            liveStateReliable = false;
                                    }
                                }
                                result.Targets.Add(target);
                            }
                        }
                    }
                    // Cross-check the live interfaces the other way: an active physical Ethernet confirmed by WMI with no parseable class key
                    // or WMI itself down yet an unmapped active Ethernet shows up
                    // neither can prove a sole physical egress; new writes are forbidden
                    foreach (KeyValuePair<string, NetworkInterface> pair in interfaces)
                    {
                        NetworkInterface nic = pair.Value;
                        if (nic == null || nic.OperationalStatus != OperationalStatus.Up
                            || !IsWiredEthernet(nic.NetworkInterfaceType)) continue;
                        bool mustMap = pnpReadOk ? pnpIds.ContainsKey(pair.Key) : true;
                        if (mustMap && !mappedClassRows.Contains(pair.Key))
                            liveStateReliable = false;
                    }
                    result.BestInterfaceIndex = BestInterfaceIndex();
                    result.LiveStateReliable = liveStateReliable;
                    result.Success = true;
                }
                catch { result.Success = false; }
                return result;
            }

            public NicModerationWriteResult CompareExchange(NicModerationTarget target,
                NicModerationMode expected, NicModerationMode desired)
            {
                if (target == null || (desired != NicModerationMode.Off
                        && desired != NicModerationMode.DriverManaged))
                    return NicModerationWriteResult.IdentityChanged;
                if (desired == NicModerationMode.Off)
                {
                    // After Prepared is persisted, recheck the live egress to close the window where the route or link changed between the scan and the registry write
                    // Restoring the original doesn't look at the current topology
                    NicModerationScanIssue issue;
                    NicModerationTarget latest = NicModerationStrategy.SelectTarget(
                        Scan(), out issue);
                    if (latest == null || !SameStableIdentity(latest, target))
                        return NicModerationWriteResult.IdentityChanged;
                    target = latest;
                }
                bool wrote = false;
                try
                {
                    using (RegistryKey node = Registry.LocalMachine.OpenSubKey(
                        ClassRoot + @"\" + target.SubKey, true))
                    {
                        if (node == null || !SameIdentity(node, target,
                                desired == NicModerationMode.Off))
                            return NicModerationWriteResult.IdentityChanged;
                        RegistryValueKind kind;
                        try { kind = node.GetValueKind(ModerationValue); }
                        catch { return NicModerationWriteResult.CurrentChanged; }
                        if (kind != RegistryValueKind.String
                            || ParseMode(node.GetValue(ModerationValue)) != expected)
                            return NicModerationWriteResult.CurrentChanged;

                        try
                        {
                            node.SetValue(ModerationValue,
                                ((int)desired).ToString(CultureInfo.InvariantCulture),
                                RegistryValueKind.String);
                            wrote = true;
                            node.Flush();
                        }
                        // A SetValue throw can't prove not a single byte was written
                        // Classify as OutcomeUnknown, keep Prepared and go through CAS reconciliation
                        catch { return NicModerationWriteResult.ReadbackFailed; }

                        RegistryValueKind actualKind;
                        try { actualKind = node.GetValueKind(ModerationValue); }
                        catch { return NicModerationWriteResult.ReadbackFailed; }
                        return actualKind == RegistryValueKind.String
                            && ParseMode(node.GetValue(ModerationValue)) == desired
                            ? NicModerationWriteResult.Applied
                            : NicModerationWriteResult.ReadbackFailed;
                    }
                }
                catch { return wrote ? NicModerationWriteResult.ReadbackFailed
                    : NicModerationWriteResult.WriteFailed; }
            }

            private static bool SameIdentity(RegistryKey node, NicModerationTarget target,
                bool requireLiveWmi)
            {
                string netCfg = node.GetValue("NetCfgInstanceId") as string;
                if (!string.Equals(netCfg, target.NetCfgInstanceId,
                        StringComparison.OrdinalIgnoreCase)) return false;
                string device = ResolveDeviceInstanceId(node, netCfg, requireLiveWmi);
                return !string.IsNullOrEmpty(target.DeviceInstanceId)
                    && !string.IsNullOrEmpty(device)
                    && string.Equals(device, target.DeviceInstanceId,
                        StringComparison.OrdinalIgnoreCase);
            }

            private static bool SameStableIdentity(NicModerationTarget left,
                NicModerationTarget right)
            {
                return left != null && right != null
                    && !string.IsNullOrEmpty(left.DeviceInstanceId)
                    && !string.IsNullOrEmpty(right.DeviceInstanceId)
                    && string.Equals(left.NetCfgInstanceId, right.NetCfgInstanceId,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(left.DeviceInstanceId, right.DeviceInstanceId,
                        StringComparison.OrdinalIgnoreCase);
            }

            private static string ResolveDeviceInstanceId(RegistryKey node, string netCfg,
                bool requireLiveWmi)
            {
                string direct = node == null ? ""
                    : (node.GetValue("DeviceInstanceID") as string) ?? "";
                bool ok;
                Dictionary<string, string> ids = ReadPhysicalPnpIds(out ok);
                if (requireLiveWmi && !ok) return "";
                string mapped;
                bool mappedPresent = ids.TryGetValue(NormalizeGuid(netCfg), out mapped);
                string resolved;
                return TryResolveConsistentDeviceId(direct, ok, mappedPresent, mapped,
                    out resolved) ? resolved : "";
            }

            private static Dictionary<string, string> ReadPhysicalPnpIds(out bool success)
            {
                var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                success = false;
                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT GUID, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
                    using (ManagementObjectCollection rows = searcher.Get())
                    {
                        foreach (ManagementObject row in rows)
                        {
                            string guid = NormalizeGuid(Convert.ToString(row["GUID"],
                                CultureInfo.InvariantCulture));
                            string pnp = Convert.ToString(row["PNPDeviceID"],
                                CultureInfo.InvariantCulture) ?? "";
                            // Keep the GUID key even with an empty PNP, for the 'every active physical interface must map' check
                            // on its own it can't pass the dual-identity write check
                            // Two different PNPs under the same GUID also collapse to empty, so we don't just pick one by WMI enumeration order
                            if (guid.Length > 0) MergePhysicalPnpId(ids, guid, pnp);
                        }
                    }
                    success = true;
                }
                catch { }
                return ids;
            }

            private static bool TryConnectionKnown(string netCfgInstanceId, out bool known)
            {
                known = false;
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                        ConnectionRoot + "\\" + netCfgInstanceId + "\\Connection"))
                    {
                        known = key != null;
                        return true;
                    }
                }
                catch { return false; }
            }

            private static int BestInterfaceIndex()
            {
                try
                {
                    // GetBestInterface only queries the routing table; nothing is sent to 1.1.1.1
                    // All four bytes of this address are the same, which sidesteps the host vs network byte-order ambiguity
                    byte[] address = IPAddress.Parse("1.1.1.1").GetAddressBytes();
                    uint best;
                    return GetBestInterface(BitConverter.ToUInt32(address, 0), out best) == 0
                        && best > 0 && best <= int.MaxValue ? (int)best : -1;
                }
                catch { return -1; }
            }

            private static bool IsWiredEthernet(NetworkInterfaceType type)
            {
                return type == NetworkInterfaceType.Ethernet
                    || type == NetworkInterfaceType.FastEthernetFx
                    || type == NetworkInterfaceType.FastEthernetT
                    || type == NetworkInterfaceType.GigabitEthernet;
            }

            private static NicModerationMode ReadMode(RegistryKey node,
                out bool reliable)
            {
                reliable = false;
                if (node == null) return NicModerationMode.Unknown;
                try
                {
                    object raw = node.GetValue(ModerationValue);
                    // Confirmed absent is a reliable 'unsupported', not the same thing as the read API throwing
                    if (raw == null)
                    {
                        reliable = true;
                        return NicModerationMode.Unknown;
                    }
                    RegistryValueKind kind = node.GetValueKind(ModerationValue);
                    reliable = true;
                    return ParseModeForKind(raw, kind);
                }
                catch { return NicModerationMode.Unknown; }
            }

            // Exclude Team, Bridge, Hyper-V, TAP and Wintun only by the non-localized ComponentId and Service
            // Display names go to the log only, never into policy decisions
            private static bool IsSoftwareAggregate(string component, string service)
            {
                string raw = ((component ?? "") + " " + (service ?? "")).ToLowerInvariant();
                return raw.Contains("ms_implat") || raw.Contains("ms_bridge")
                    || raw.Contains("vms_mp") || raw.Contains("vms_pp")
                    || raw.Contains("ndiswan") || raw.Contains("wintun")
                    || raw.Contains("wireguard") || raw.Contains("tap");
            }

            private static string NormalizeGuid(string raw)
            {
                Guid id;
                return Guid.TryParse((raw ?? "").Trim(), out id)
                    ? id.ToString("D") : "";
            }
        }
    }
}
