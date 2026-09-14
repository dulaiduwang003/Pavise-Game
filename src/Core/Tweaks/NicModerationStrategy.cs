// @author bdth 2074055628@qq.com
// File purpose Pure state machine for the NIC interrupt moderation experiment policy: receipt first, identity check, read-back and conservative rollback
using System;
using System.Collections.Generic;
using System.Text;

namespace PaviseApp
{
    // Standard *InterruptModeration only has 0 and 1; Adaptive and Medium are vendor-private algorithms
    // Without a PCI driver INF fingerprint review, don't guess values here
    internal enum NicModerationMode
    {
        Unknown = -1,
        Off = 0,
        DriverManaged = 1
    }

    internal enum NicModerationScanIssue
    {
        None,
        ReadFailed,
        NoSupportedAdapter,
        NoActiveAdapter,
        AmbiguousActiveAdapters,
        NoDefaultRoute,
        DefaultRouteNotPhysical,
        UnsafeAdapter,
        UnsupportedValue,
        IdentityUnavailable,
        ReceiptUnreadable,
        ReceiptCorrupt,
        RecoveryPending,
        IdentityChanged,
        ExternalChanged,
        WriteFailed,
        ReceiptSaveFailed,
        AlreadyOff,
        Applied,
        Restored
    }

    internal enum NicModerationWriteResult
    {
        Applied,
        IdentityChanged,
        CurrentChanged,
        WriteFailed,
        ReadbackFailed
    }

    internal sealed class NicModerationTarget
    {
        public string SubKey = "";
        public string NetCfgInstanceId = "";
        public string DeviceInstanceId = "";
        public string Label = "";
        public string DriverVersion = "";
        public string Service = "";
        public string InfPath = "";
        public NicModerationMode Mode = NicModerationMode.Unknown;
        // Unknown comes in two kinds: a private value or missing value actually read, versus the read itself failing
        // Only the former counts as external takeover and can settle the restore receipt
        public bool ModeReadReliable = true;
        public bool LinkUp;
        public bool PhysicalWired;
        public bool Excluded;
        public int InterfaceIndex = -1;
    }

    internal sealed class NicModerationScan
    {
        public bool Success;
        public bool LiveStateReliable;
        public int BestInterfaceIndex = -1;
        public readonly List<NicModerationTarget> Targets = new List<NicModerationTarget>();
    }

    internal interface INicModerationPlatform
    {
        NicModerationScan Scan();
        NicModerationWriteResult CompareExchange(NicModerationTarget target,
            NicModerationMode expected, NicModerationMode desired);
    }

    internal interface INicModerationReceiptStore
    {
        bool TryRead(out string raw);
        bool SaveAndVerify(string raw);
    }

    internal sealed class NicModerationReceipt
    {
        public bool Applied;
        public string SubKey = "";
        public string NetCfgInstanceId = "";
        public string DeviceInstanceId = "";
        public string Label = "";
        public string DriverVersion = "";
        public string Service = "";
        public string InfPath = "";
        public NicModerationMode Original;
        public NicModerationMode Desired;
    }

    internal static class NicModerationReceiptCodec
    {
        private const string Version = "NIM2";
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private static bool TryB64(string text, out string value)
        {
            value = "";
            try
            {
                value = StrictUtf8.GetString(Convert.FromBase64String(text ?? ""));
                return true;
            }
            catch { return false; }
        }

        internal static string Encode(NicModerationReceipt receipt)
        {
            if (receipt == null) return "";
            return string.Join("|", new[]
            {
                Version,
                receipt.Applied ? "A" : "P",
                B64(receipt.SubKey),
                B64(receipt.NetCfgInstanceId),
                B64(receipt.DeviceInstanceId),
                B64(receipt.Label),
                B64(receipt.DriverVersion),
                B64(receipt.Service),
                B64(receipt.InfPath),
                ((int)receipt.Original).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ((int)receipt.Desired).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        internal static bool TryDecode(string raw, out NicModerationReceipt receipt)
        {
            receipt = null;
            if (string.IsNullOrEmpty(raw)) return false;
            string[] p = raw.Split('|');
            if (p.Length != 11 || p[0] != Version || (p[1] != "P" && p[1] != "A")) return false;

            var r = new NicModerationReceipt();
            r.Applied = p[1] == "A";
            if (!TryB64(p[2], out r.SubKey)
                || !TryB64(p[3], out r.NetCfgInstanceId)
                || !TryB64(p[4], out r.DeviceInstanceId)
                || !TryB64(p[5], out r.Label)
                || !TryB64(p[6], out r.DriverVersion)
                || !TryB64(p[7], out r.Service)
                || !TryB64(p[8], out r.InfPath)) return false;

            int original, desired;
            if (!int.TryParse(p[9], out original) || !int.TryParse(p[10], out desired)
                // NIM2 expresses only the one direction this version authorizes: driver-managed 1 to experimental Off 0
                // A ticket with valid syntax but the reverse direction is treated as corrupted
                // It must not trick us into a write this policy never established
                || original != (int)NicModerationMode.DriverManaged
                || desired != (int)NicModerationMode.Off
                || string.IsNullOrEmpty(r.NetCfgInstanceId)
                || string.IsNullOrEmpty(r.DeviceInstanceId)) return false;
            r.Original = (NicModerationMode)original;
            r.Desired = (NicModerationMode)desired;
            receipt = r;
            return true;
        }
    }

    internal sealed class NicModerationStrategy
    {
        private readonly INicModerationPlatform platform;
        private readonly INicModerationReceiptStore store;

        internal NicModerationStrategy(INicModerationPlatform platform,
            INicModerationReceiptStore store)
        {
            if (platform == null) throw new ArgumentNullException("platform");
            if (store == null) throw new ArgumentNullException("store");
            this.platform = platform;
            this.store = store;
        }

        internal bool HasReceipt
        {
            get
            {
                string raw;
                return !store.TryRead(out raw) || !string.IsNullOrEmpty(raw);
            }
        }

        internal bool TryReceipt(out NicModerationReceipt receipt,
            out NicModerationScanIssue issue)
        {
            receipt = null;
            issue = NicModerationScanIssue.None;
            string raw;
            if (!store.TryRead(out raw))
            {
                issue = NicModerationScanIssue.ReceiptUnreadable;
                return false;
            }
            if (string.IsNullOrEmpty(raw)) return true;
            if (!NicModerationReceiptCodec.TryDecode(raw, out receipt))
            {
                issue = NicModerationScanIssue.ReceiptCorrupt;
                return false;
            }
            return true;
        }

        internal NicModerationTarget Inspect(out NicModerationScanIssue issue)
        {
            return SelectTarget(platform.Scan(), out issue);
        }

        internal static NicModerationTarget SelectTarget(NicModerationScan scan,
            out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            if (scan == null || !scan.Success)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return null;
            }
            if (!scan.LiveStateReliable)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return null;
            }
            if (scan.Targets.Count == 0)
            {
                issue = NicModerationScanIssue.NoSupportedAdapter;
                return null;
            }

            NicModerationTarget selected = null;
            int active = 0;
            foreach (NicModerationTarget target in scan.Targets)
            {
                // First count all active physical wired devices, then check whether the sole egress is fit to write
                // Don't count only devices exposing *InterruptModeration
                // or a second USB or vendor NIC without the standard item slips through and the first gets wrongly cleared
                if (target == null || !target.LinkUp || !target.PhysicalWired) continue;
                active++;
                selected = target;
            }
            if (active == 0)
            {
                issue = NicModerationScanIssue.NoActiveAdapter;
                return null;
            }
            if (active != 1)
            {
                issue = NicModerationScanIssue.AmbiguousActiveAdapters;
                return null;
            }
            if (scan.BestInterfaceIndex <= 0)
            {
                issue = NicModerationScanIssue.NoDefaultRoute;
                return null;
            }
            if (selected.InterfaceIndex <= 0 || selected.InterfaceIndex != scan.BestInterfaceIndex)
            {
                issue = NicModerationScanIssue.DefaultRouteNotPhysical;
                return null;
            }
            if (selected.Excluded)
            {
                issue = NicModerationScanIssue.UnsafeAdapter;
                return null;
            }
            if (string.IsNullOrEmpty(selected.DeviceInstanceId))
            {
                issue = NicModerationScanIssue.IdentityUnavailable;
                return null;
            }
            if (!selected.ModeReadReliable)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return null;
            }
            if (selected.Mode == NicModerationMode.Unknown)
            {
                issue = NicModerationScanIssue.UnsupportedValue;
                return null;
            }
            return selected;
        }

        internal bool ApplyExperimentalOff(out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            NicModerationReceipt existing;
            if (!TryReceipt(out existing, out issue)) return false;
            if (existing != null)
            {
                issue = NicModerationScanIssue.RecoveryPending;
                return existing.Applied && existing.Desired == NicModerationMode.Off;
            }

            NicModerationTarget target = Inspect(out issue);
            if (target == null) return false;
            if (target.Mode == NicModerationMode.Off)
            {
                // Disabled externally doesn't count as a Pavise takeover; don't forge a receipt
                issue = NicModerationScanIssue.AlreadyOff;
                return true;
            }
            if (target.Mode != NicModerationMode.DriverManaged)
            {
                issue = NicModerationScanIssue.UnsupportedValue;
                return false;
            }

            var receipt = FromTarget(target, NicModerationMode.Off);
            if (!store.SaveAndVerify(NicModerationReceiptCodec.Encode(receipt)))
            {
                issue = NicModerationScanIssue.ReceiptSaveFailed;
                return false;
            }

            NicModerationWriteResult write = platform.CompareExchange(target,
                receipt.Original, receipt.Desired);
            if (write != NicModerationWriteResult.Applied)
            {
                // These three clearly happen before the write; clearing Prepared is enough
                // ReadbackFailed may already have written through; it must take the same CAS restore path
                bool settled;
                if (write == NicModerationWriteResult.IdentityChanged
                    || write == NicModerationWriteResult.CurrentChanged
                    || write == NicModerationWriteResult.WriteFailed)
                    settled = store.SaveAndVerify("");
                else
                {
                    NicModerationScanIssue settle;
                    settled = RestoreReceipt(receipt, out settle);
                }
                issue = !settled ? NicModerationScanIssue.RecoveryPending
                    : write == NicModerationWriteResult.IdentityChanged
                        ? NicModerationScanIssue.IdentityChanged
                        : NicModerationScanIssue.WriteFailed;
                return false;
            }

            receipt.Applied = true;
            if (!store.SaveAndVerify(NicModerationReceiptCodec.Encode(receipt)))
            {
                NicModerationScanIssue settle;
                bool restored = RestoreReceipt(receipt, out settle);
                issue = restored ? NicModerationScanIssue.ReceiptSaveFailed
                    : NicModerationScanIssue.RecoveryPending;
                return false;
            }
            issue = NicModerationScanIssue.Applied;
            return true;
        }

        internal bool Restore(out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            NicModerationReceipt receipt;
            if (!TryReceipt(out receipt, out issue)) return false;
            if (receipt == null) return true;
            return RestoreReceipt(receipt, out issue);
        }

        // Startup reconciliation must cover two crash windows: one, Prepared written but not confirmed
        // two, Applied already written back to Original but the receipt not yet cleared
        // Applied+Desired is an experiment the user set up himself; only confirm it's there, a normal startup doesn't restore on its own
        internal bool ReconcileStartup(out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            NicModerationReceipt receipt;
            if (!TryReceipt(out receipt, out issue)) return false;
            if (receipt == null) return true;

            NicModerationScan scan = platform.Scan();
            if (scan == null || !scan.Success)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return false;
            }
            NicModerationTarget current = FindIdentity(scan.Targets, receipt);
            if (current == null)
            {
                issue = NicModerationScanIssue.IdentityChanged;
                return false;
            }
            if (!current.ModeReadReliable)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return false;
            }
            if (receipt.Applied && current.Mode == receipt.Desired)
                return true;
            return RestoreReceipt(receipt, out issue);
        }

        // Only used when the user explicitly clicks wipe all; discards apply receipts that are unreadable, corrupted, or whose device was uninstalled
        // This action never touches the system NIC value; the normal disable path never calls it
        internal bool DiscardReceiptForReset()
        {
            return store.SaveAndVerify("");
        }

        private bool RestoreReceipt(NicModerationReceipt receipt,
            out NicModerationScanIssue issue)
        {
            issue = NicModerationScanIssue.None;
            NicModerationScan scan = platform.Scan();
            if (scan == null || !scan.Success)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return false;
            }
            NicModerationTarget current = FindIdentity(scan.Targets, receipt);
            if (current == null)
            {
                issue = NicModerationScanIssue.IdentityChanged;
                return false;
            }
            if (!current.ModeReadReliable)
            {
                issue = NicModerationScanIssue.ReadFailed;
                return false;
            }
            if (current.Mode == receipt.Original)
            {
                if (!store.SaveAndVerify(""))
                {
                    issue = NicModerationScanIssue.ReceiptSaveFailed;
                    return false;
                }
                issue = NicModerationScanIssue.Restored;
                return true;
            }
            if (current.Mode != receipt.Desired)
            {
                // User, driver or another tool has taken over; don't overwrite with the old receipt
                // Settle Pavise's ownership and leave an ExternalChanged line in the log for lookup
                if (!store.SaveAndVerify(""))
                {
                    issue = NicModerationScanIssue.ReceiptSaveFailed;
                    return false;
                }
                issue = NicModerationScanIssue.ExternalChanged;
                return true;
            }

            NicModerationWriteResult write = platform.CompareExchange(current,
                receipt.Desired, receipt.Original);
            if (write != NicModerationWriteResult.Applied)
            {
                issue = write == NicModerationWriteResult.IdentityChanged
                    ? NicModerationScanIssue.IdentityChanged : NicModerationScanIssue.WriteFailed;
                return false;
            }
            if (!store.SaveAndVerify(""))
            {
                issue = NicModerationScanIssue.ReceiptSaveFailed;
                return false;
            }
            issue = NicModerationScanIssue.Restored;
            return true;
        }

        private static NicModerationReceipt FromTarget(NicModerationTarget target,
            NicModerationMode desired)
        {
            return new NicModerationReceipt
            {
                Applied = false,
                SubKey = target.SubKey ?? "",
                NetCfgInstanceId = target.NetCfgInstanceId ?? "",
                DeviceInstanceId = target.DeviceInstanceId ?? "",
                Label = target.Label ?? "",
                DriverVersion = target.DriverVersion ?? "",
                Service = target.Service ?? "",
                InfPath = target.InfPath ?? "",
                Original = target.Mode,
                Desired = desired
            };
        }

        internal static NicModerationTarget FindIdentity(IList<NicModerationTarget> targets,
            NicModerationReceipt receipt)
        {
            if (targets == null || receipt == null) return null;
            NicModerationTarget match = null;
            int matches = 0;
            foreach (NicModerationTarget target in targets)
            {
                if (target == null || !string.Equals(target.NetCfgInstanceId,
                        receipt.NetCfgInstanceId, StringComparison.OrdinalIgnoreCase)) continue;
                // The NetCfg GUID only locates the interface; the full PnP instance ID is the second lock that must match
                // If either end is missing, never fall back to writing the old value by GUID alone
                if (string.IsNullOrEmpty(receipt.DeviceInstanceId)
                    || string.IsNullOrEmpty(target.DeviceInstanceId)
                    || !string.Equals(target.DeviceInstanceId, receipt.DeviceInstanceId,
                        StringComparison.OrdinalIgnoreCase)) continue;
                // A driver reinstall briefly leaves duplicate class keys; when it can't be proven which is the current device
                // don't just pick one by enumeration order and write back
                match = target;
                matches++;
            }
            return matches == 1 ? match : null;
        }

        internal static int CountIdentityMatches(IList<NicModerationTarget> targets,
            NicModerationReceipt receipt)
        {
            if (targets == null || receipt == null
                || string.IsNullOrEmpty(receipt.NetCfgInstanceId)
                || string.IsNullOrEmpty(receipt.DeviceInstanceId)) return 0;
            int count = 0;
            foreach (NicModerationTarget target in targets)
                if (target != null
                    && string.Equals(target.NetCfgInstanceId, receipt.NetCfgInstanceId,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(target.DeviceInstanceId)
                    && string.Equals(target.DeviceInstanceId, receipt.DeviceInstanceId,
                        StringComparison.OrdinalIgnoreCase)) count++;
            return count;
        }
    }
}
