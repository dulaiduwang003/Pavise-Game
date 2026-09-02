// Pure strategy and injected-platform tests only. The Windows registry, route
// table, network adapters and persistent Settings store are never touched.
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int nicImChecks;

        internal static int RunNicModerationRegressionTests()
        {
            Action[] tests = {
                NicImRowEligibility,
                NicImNumericTolerance,
                NicImTargetSelectionFailsClosed,
                NicImReceiptIdentityRoundTrip,
                NicImApplyAndRestore,
                NicImAlreadyOffIsNotOwned,
                NicImWriteFailureDoesNotClaim,
                NicImPreparedCleanupFailureRetainsReceipt,
                NicImUnknownWriteOutcomeRecovers,
                NicImReceiptCommitFailureRollsBack,
                NicImStartupReconciliation,
                NicImRestoreClearFailureReconciles,
                NicImIdentityMismatchKeepsReceipt,
                NicImExternalChangeIsPreserved,
                NicImCorruptReceiptBlocksWrites,
                NicImExplicitResetCanDiscardBadReceipt,
                NicImLegacyValueDecisions,
                NicImLedgerParsing
            };
            nicImChecks = 0;
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS nic-moderation assertions=" + nicImChecks
                + " registry=untouched route=untouched adapters=untouched settings=fake windows_shown=false");
            return tests.Length;
        }

        private static void NicCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("NIC moderation regression: " + message);
            Interlocked.Increment(ref nicImChecks);
        }

        private static void NicImRowEligibility()
        {
            NicCheck(NicModerationTweak.RowEligible("1", 6, 0x84),
                "the real-machine enabled value shapes must pass");
            NicCheck(NicModerationTweak.RowEligible("0", "6", "132"),
                "string-typed numeric hardware values must pass");
            NicCheck(!NicModerationTweak.RowEligible("2", 6, 0x84)
                && !NicModerationTweak.RowEligible("Adaptive", 6, 0x84),
                "unknown or vendor-private values must fail closed");
            NicCheck(!NicModerationTweak.RowEligible(1, 6, 0x84),
                "a non-string moderation value must fail closed");
            NicCheck(!NicModerationTweak.RowEligible("1", 71, 0x84),
                "wireless adapters must be excluded");
            NicCheck(!NicModerationTweak.RowEligible("1", null, 0x84)
                && !NicModerationTweak.RowEligible("1", 6, 0x80)
                && !NicModerationTweak.RowEligible("1", 6, null),
                "missing type, nonphysical, and missing characteristics must fail closed");
            NicCheck(NicModerationTweak.ParseMode("0") == NicModerationMode.Off
                && NicModerationTweak.ParseMode("1") == NicModerationMode.DriverManaged
                && NicModerationTweak.ParseMode("medium") == NicModerationMode.Unknown,
                "only standardized raw values 0 and 1 may become modes");
            NicCheck(NicModerationTweak.ParseModeForKind("0",
                    Microsoft.Win32.RegistryValueKind.String) == NicModerationMode.Off
                && NicModerationTweak.ParseModeForKind("0",
                    Microsoft.Win32.RegistryValueKind.ExpandString) == NicModerationMode.Unknown
                && NicModerationTweak.ParseModeForKind(0,
                    Microsoft.Win32.RegistryValueKind.DWord) == NicModerationMode.Unknown,
                "only an exact REG_SZ standard value may become a writable mode");
            NicCheck(!NicModerationTweak.NetworkDirectExcluded(0, null)
                && !NicModerationTweak.NetworkDirectExcluded("0", "iWARP")
                && NicModerationTweak.NetworkDirectExcluded(1, null)
                && NicModerationTweak.NetworkDirectExcluded("unknown", null)
                && NicModerationTweak.NetworkDirectExcluded(null, "RoCE"),
                "RDMA must fail closed for enabled, malformed, or technology-only shapes");
            NicCheck(NicModerationTweak.PhysicalSourceConflict(true, true, true, false)
                && !NicModerationTweak.PhysicalSourceConflict(true, false, true, false)
                && !NicModerationTweak.PhysicalSourceConflict(false, true, true, false)
                && !NicModerationTweak.PhysicalSourceConflict(true, true, true, true),
                "an active class-physical row rejected by a successful WMI map must fail closed");
            string resolved;
            NicCheck(NicModerationTweak.TryResolveConsistentDeviceId("PCI\\A", true,
                    true, "pci\\a", out resolved) && resolved == "pci\\a"
                && NicModerationTweak.TryResolveConsistentDeviceId("", true,
                    true, "PCI\\A", out resolved) && resolved == "PCI\\A"
                && !NicModerationTweak.TryResolveConsistentDeviceId("PCI\\OLD", true,
                    true, "PCI\\NEW", out resolved)
                && !NicModerationTweak.TryResolveConsistentDeviceId("PCI\\A", true,
                    false, "", out resolved)
                && NicModerationTweak.TryResolveConsistentDeviceId("PCI\\A", false,
                    false, "", out resolved) && resolved == "PCI\\A",
                "registry and WMI PnP identities must agree before a new write");
            var physicalMap = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            NicModerationTweak.MergePhysicalPnpId(physicalMap, "{A}", "PCI\\ONE");
            NicModerationTweak.MergePhysicalPnpId(physicalMap, "{a}", "pci\\one");
            NicCheck(physicalMap["{A}"] == "PCI\\ONE",
                "duplicate identical WMI identities must remain usable");
            NicModerationTweak.MergePhysicalPnpId(physicalMap, "{A}", "PCI\\TWO");
            NicCheck(physicalMap["{A}"] == "",
                "conflicting WMI identities for one GUID must become unusable");
        }

        private static void NicImNumericTolerance()
        {
            int value;
            NicCheck(NicModerationTweak.TryNumeric(6, out value) && value == 6,
                "DWORD values must parse");
            NicCheck(NicModerationTweak.TryNumeric("132", out value) && value == 132,
                "numeric strings must parse");
            NicCheck(!NicModerationTweak.TryNumeric("abc", out value)
                && !NicModerationTweak.TryNumeric(null, out value)
                && !NicModerationTweak.TryNumeric(6L, out value),
                "non-numeric and unexpected shapes must fail closed");
        }

        private static void NicImTargetSelectionFailsClosed()
        {
            NicModerationScanIssue issue;
            NicCheck(NicModerationStrategy.SelectTarget(null, out issue) == null
                && issue == NicModerationScanIssue.ReadFailed,
                "scan failure must not become an empty successful scan");

            var empty = new NicModerationScan {
                Success = true, LiveStateReliable = true, BestInterfaceIndex = 7 };
            NicCheck(NicModerationStrategy.SelectTarget(empty, out issue) == null
                && issue == NicModerationScanIssue.NoSupportedAdapter,
                "empty successful scans must be explicit");

            NicModerationScan one = OneScan(NicModerationMode.DriverManaged);
            one.LiveStateReliable = false;
            NicCheck(NicModerationStrategy.SelectTarget(one, out issue) == null
                && issue == NicModerationScanIssue.ReadFailed,
                "an incomplete live-state map must block all new writes");

            one = OneScan(NicModerationMode.DriverManaged);
            one.Targets[0].LinkUp = false;
            NicCheck(NicModerationStrategy.SelectTarget(one, out issue) == null
                && issue == NicModerationScanIssue.NoActiveAdapter,
                "disconnected adapters must not be changed");

            one = OneScan(NicModerationMode.DriverManaged);
            one.Targets.Add(Target("{B}", NicModerationMode.DriverManaged, 8));
            NicCheck(NicModerationStrategy.SelectTarget(one, out issue) == null
                && issue == NicModerationScanIssue.AmbiguousActiveAdapters,
                "multiple active physical adapters must fail closed");

            one = OneScan(NicModerationMode.DriverManaged);
            one.Targets.Add(Target("{UNSUPPORTED}", NicModerationMode.Unknown, 8));
            NicCheck(NicModerationStrategy.SelectTarget(one, out issue) == null
                && issue == NicModerationScanIssue.AmbiguousActiveAdapters,
                "an active physical adapter without the standard property must still count");

            one = OneScan(NicModerationMode.DriverManaged);
            one.BestInterfaceIndex = 99;
            NicCheck(NicModerationStrategy.SelectTarget(one, out issue) == null
                && issue == NicModerationScanIssue.DefaultRouteNotPhysical,
                "VPN, virtual, or teamed default routes must not fall through to a physical NIC");

            one = OneScan(NicModerationMode.Unknown);
            NicCheck(NicModerationStrategy.SelectTarget(one, out issue) == null
                && issue == NicModerationScanIssue.UnsupportedValue,
                "nonstandard raw values must not be overwritten");

            one = OneScan(NicModerationMode.DriverManaged);
            one.Targets[0].Excluded = true;
            NicCheck(NicModerationStrategy.SelectTarget(one, out issue) == null
                && issue == NicModerationScanIssue.UnsafeAdapter,
                "RDMA or otherwise unsafe default adapters must fail closed");

            one = OneScan(NicModerationMode.DriverManaged);
            one.Targets[0].DeviceInstanceId = "";
            NicCheck(NicModerationStrategy.SelectTarget(one, out issue) == null
                && issue == NicModerationScanIssue.IdentityUnavailable,
                "a writable target must have a strong PnP identity lock");
        }

        private static void NicImReceiptIdentityRoundTrip()
        {
            var original = new NicModerationReceipt {
                Applied = true, SubKey = "0001", NetCfgInstanceId = "{ABC}",
                DeviceInstanceId = "PCI\\VEN_1234", Label = "Ethernet | 主口",
                DriverVersion = "1.2.3", Service = "nic", InfPath = "oem7.inf",
                Original = NicModerationMode.DriverManaged, Desired = NicModerationMode.Off };
            NicModerationReceipt parsed;
            string raw = NicModerationReceiptCodec.Encode(original);
            NicCheck(NicModerationReceiptCodec.TryDecode(raw, out parsed)
                && parsed.Applied && parsed.SubKey == "0001"
                && parsed.NetCfgInstanceId == "{ABC}" && parsed.Label == "Ethernet | 主口"
                && parsed.Original == NicModerationMode.DriverManaged
                && parsed.Desired == NicModerationMode.Off,
                "versioned receipts must round-trip arbitrary labels without delimiter ambiguity");
            var reversed = new NicModerationReceipt {
                Applied = true, SubKey = "0001", NetCfgInstanceId = "{ABC}",
                DeviceInstanceId = "PCI\\VEN_1234",
                Original = NicModerationMode.Off,
                Desired = NicModerationMode.DriverManaged };
            NicCheck(!NicModerationReceiptCodec.TryDecode(
                    NicModerationReceiptCodec.Encode(reversed), out parsed),
                "a syntactically valid receipt with the unauthorized reverse direction must be rejected");
            NicCheck(NicModerationReceiptCodec.TryDecode(raw, out parsed),
                "the valid fixture must remain available after the forged receipt check");

            NicModerationTarget moved = Target("{ABC}", NicModerationMode.Off, 7);
            moved.SubKey = "0042";
            moved.DeviceInstanceId = "PCI\\VEN_1234";
            NicCheck(NicModerationStrategy.FindIdentity(
                    new List<NicModerationTarget> { moved }, parsed) == moved,
                "a class-index move must resolve by stable NetCfg identity");
            moved.NetCfgInstanceId = "{NEW}";
            NicCheck(NicModerationStrategy.FindIdentity(
                    new List<NicModerationTarget> { moved }, parsed) == null,
                "class-index reuse by another adapter must never match the receipt");
            moved.NetCfgInstanceId = "{ABC}";
            moved.DeviceInstanceId = "PCI\\VEN_REPLACEMENT";
            NicCheck(NicModerationStrategy.FindIdentity(
                    new List<NicModerationTarget> { moved }, parsed) == null,
                "the same interface GUID with a different PnP device must be rejected");
            moved.DeviceInstanceId = "PCI\\VEN_1234";
            moved.DriverVersion = "9.9.9";
            NicCheck(NicModerationStrategy.FindIdentity(
                    new List<NicModerationTarget> { moved }, parsed) == moved,
                "a driver update may not impersonate a device, but the same PnP device remains restorable");
            NicModerationTarget duplicate = Target("{ABC}", NicModerationMode.Off, 7);
            duplicate.DeviceInstanceId = "PCI\\VEN_1234";
            NicCheck(NicModerationStrategy.FindIdentity(
                    new List<NicModerationTarget> { moved, duplicate }, parsed) == null,
                "duplicate class rows with the same identities must fail closed");
        }

        private static void NicImApplyAndRestore()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            var store = new FakeNicStore();
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(engine.ApplyExperimentalOff(out issue)
                && issue == NicModerationScanIssue.Applied,
                "explicit apply must succeed on one standard driver-managed default route");
            NicCheck(platform.ScanValue.Targets[0].Mode == NicModerationMode.Off
                && engine.HasReceipt && platform.Writes == 1,
                "apply must write exactly once after persisting a receipt");
            NicModerationReceipt receipt;
            NicCheck(engine.TryReceipt(out receipt, out issue) && receipt != null && receipt.Applied,
                "the receipt must be committed only after read-back succeeds");
            NicCheck(engine.Restore(out issue) && issue == NicModerationScanIssue.Restored
                && platform.ScanValue.Targets[0].Mode == NicModerationMode.DriverManaged
                && !engine.HasReceipt && platform.Writes == 2,
                "restore must CAS back to the original and clear ownership");
        }

        private static void NicImAlreadyOffIsNotOwned()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.Off));
            var store = new FakeNicStore();
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(engine.ApplyExperimentalOff(out issue)
                && issue == NicModerationScanIssue.AlreadyOff,
                "an externally disabled adapter is a successful no-op");
            NicCheck(!engine.HasReceipt && platform.Writes == 0,
                "a no-op must not claim external state");
        }

        private static void NicImWriteFailureDoesNotClaim()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            platform.NextResult = NicModerationWriteResult.WriteFailed;
            var store = new FakeNicStore();
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(!engine.ApplyExperimentalOff(out issue)
                && issue == NicModerationScanIssue.WriteFailed,
                "a pre-write failure must be reported");
            NicCheck(!engine.HasReceipt
                && platform.ScanValue.Targets[0].Mode == NicModerationMode.DriverManaged,
                "a confirmed pre-write failure must clear Prepared without changing the adapter");
        }

        private static void NicImReceiptCommitFailureRollsBack()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            var store = new FakeNicStore { FailSaveCall = 2 };
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(!engine.ApplyExperimentalOff(out issue)
                && issue == NicModerationScanIssue.ReceiptSaveFailed,
                "failure to commit the applied receipt must fail the operation");
            NicCheck(platform.ScanValue.Targets[0].Mode == NicModerationMode.DriverManaged
                && !engine.HasReceipt && platform.Writes == 2,
                "receipt commit failure must CAS rollback and clear the prepared receipt");
        }

        private static void NicImPreparedCleanupFailureRetainsReceipt()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            platform.NextResult = NicModerationWriteResult.WriteFailed;
            var store = new FakeNicStore { FailSaveCall = 2 };
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(!engine.ApplyExperimentalOff(out issue)
                && issue == NicModerationScanIssue.RecoveryPending,
                "a failed Prepared cleanup must be reported as pending recovery");
            NicCheck(engine.HasReceipt
                && platform.ScanValue.Targets[0].Mode == NicModerationMode.DriverManaged,
                "failed cleanup must retain evidence even when no adapter change was confirmed");
        }

        private static void NicImUnknownWriteOutcomeRecovers()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            platform.NextResult = NicModerationWriteResult.ReadbackFailed;
            platform.MutateOnReadbackFailure = true;
            var store = new FakeNicStore();
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(!engine.ApplyExperimentalOff(out issue)
                && issue == NicModerationScanIssue.WriteFailed,
                "an unknown write outcome must fail the experiment after recovery");
            NicCheck(platform.ScanValue.Targets[0].Mode == NicModerationMode.DriverManaged
                && !engine.HasReceipt && platform.Writes == 2,
                "an unknown outcome that did write must be CAS-restored from Prepared");
        }

        private static void NicImStartupReconciliation()
        {
            NicModerationScan scan = OneScan(NicModerationMode.DriverManaged);
            NicModerationTarget target = scan.Targets[0];
            var prepared = new NicModerationReceipt {
                Applied = false, SubKey = target.SubKey,
                NetCfgInstanceId = target.NetCfgInstanceId,
                DeviceInstanceId = target.DeviceInstanceId, Label = target.Label,
                DriverVersion = target.DriverVersion, Service = target.Service,
                InfPath = target.InfPath, Original = NicModerationMode.DriverManaged,
                Desired = NicModerationMode.Off };
            var store = new FakeNicStore { Raw = NicModerationReceiptCodec.Encode(prepared) };
            var platform = new FakeNicPlatform(scan);
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(engine.ReconcileStartup(out issue)
                && issue == NicModerationScanIssue.Restored && !engine.HasReceipt
                && platform.Writes == 0,
                "Prepared with the original value must settle without a registry write");

            scan = OneScan(NicModerationMode.Off);
            platform = new FakeNicPlatform(scan);
            store = new FakeNicStore { Raw = NicModerationReceiptCodec.Encode(prepared) };
            engine = new NicModerationStrategy(platform, store);
            NicCheck(engine.ReconcileStartup(out issue)
                && issue == NicModerationScanIssue.Restored
                && scan.Targets[0].Mode == NicModerationMode.DriverManaged
                && !engine.HasReceipt && platform.Writes == 1,
                "Prepared with the desired value must restore and settle at startup");

            prepared.Applied = true;
            scan = OneScan(NicModerationMode.Off);
            platform = new FakeNicPlatform(scan);
            store = new FakeNicStore { Raw = NicModerationReceiptCodec.Encode(prepared) };
            engine = new NicModerationStrategy(platform, store);
            NicCheck(engine.ReconcileStartup(out issue)
                && issue == NicModerationScanIssue.None && engine.HasReceipt
                && platform.Writes == 0,
                "a confirmed Applied experiment must remain enabled at startup");
        }

        private static void NicImRestoreClearFailureReconciles()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            var store = new FakeNicStore();
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(engine.ApplyExperimentalOff(out issue), "fixture apply must succeed");
            store.FailSaveCall = 3;
            NicCheck(!engine.Restore(out issue)
                && issue == NicModerationScanIssue.ReceiptSaveFailed
                && platform.ScanValue.Targets[0].Mode == NicModerationMode.DriverManaged
                && engine.HasReceipt,
                "a crash-window equivalent after write-back must retain the Applied receipt");
            NicCheck(engine.ReconcileStartup(out issue)
                && issue == NicModerationScanIssue.Restored && !engine.HasReceipt
                && platform.Writes == 2,
                "startup reconciliation must clear Applied when the original is already present");
        }

        private static void NicImIdentityMismatchKeepsReceipt()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            var store = new FakeNicStore();
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(engine.ApplyExperimentalOff(out issue), "fixture apply must succeed");
            platform.ScanValue.Targets[0].NetCfgInstanceId = "{REPLACEMENT}";
            NicCheck(!engine.Restore(out issue) && issue == NicModerationScanIssue.IdentityChanged,
                "an adapter replacement must block restoration into the new device");
            NicCheck(engine.HasReceipt && platform.Writes == 1,
                "identity mismatch must retain the receipt and avoid a second write");
        }

        private static void NicImExternalChangeIsPreserved()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            var store = new FakeNicStore();
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(engine.ApplyExperimentalOff(out issue), "fixture apply must succeed");
            platform.ScanValue.Targets[0].Mode = NicModerationMode.Unknown;
            NicCheck(engine.Restore(out issue) && issue == NicModerationScanIssue.ExternalChanged,
                "an external value must be preserved and ownership settled");
            NicCheck(platform.ScanValue.Targets[0].Mode == NicModerationMode.Unknown
                && !engine.HasReceipt && platform.Writes == 1,
                "restore must not overwrite an external post-apply modification");

            platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            store = new FakeNicStore();
            engine = new NicModerationStrategy(platform, store);
            NicCheck(engine.ApplyExperimentalOff(out issue), "unreadable fixture apply must succeed");
            platform.ScanValue.Targets[0].Mode = NicModerationMode.Unknown;
            platform.ScanValue.Targets[0].ModeReadReliable = false;
            NicCheck(!engine.Restore(out issue)
                && issue == NicModerationScanIssue.ReadFailed && engine.HasReceipt
                && platform.Writes == 1,
                "a transient mode-read failure must retain recovery evidence without writing");
        }

        private static void NicImCorruptReceiptBlocksWrites()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            var store = new FakeNicStore { Raw = "NIM2|broken" };
            var engine = new NicModerationStrategy(platform, store);
            NicModerationScanIssue issue;
            NicCheck(!engine.ApplyExperimentalOff(out issue)
                && issue == NicModerationScanIssue.ReceiptCorrupt,
                "a corrupt receipt must block new writes");
            NicCheck(platform.Writes == 0 && engine.HasReceipt,
                "corrupt recovery evidence must be retained for diagnosis");
        }

        private static void NicImExplicitResetCanDiscardBadReceipt()
        {
            var platform = new FakeNicPlatform(OneScan(NicModerationMode.DriverManaged));
            var store = new FakeNicStore { Raw = "NIM2|broken" };
            var engine = new NicModerationStrategy(platform, store);
            NicCheck(engine.DiscardReceiptForReset() && !engine.HasReceipt
                && platform.Writes == 0,
                "explicit reset may discard a corrupt app receipt without touching the adapter");
            store.Raw = "opaque";
            store.Readable = false;
            NicCheck(engine.DiscardReceiptForReset(),
                "explicit reset may replace an unreadable app receipt when persistence recovers");
        }

        private static void NicImLedgerParsing()
        {
            string[] ids = NicModerationTweak.ParseList("0001;;0016;");
            NicCheck(ids.Length == 2 && ids[0] == "0001" && ids[1] == "0016",
                "the legacy ledger must drop empty segments and keep order");
            NicCheck(NicModerationTweak.ParseList(null).Length == 0
                && NicModerationTweak.ParseList("").Length == 0,
                "an empty legacy ledger must parse to nothing");
        }

        private static void NicImLegacyValueDecisions()
        {
            NicCheck(NicModerationTweak.DecideLegacyValue(true, "1",
                    Microsoft.Win32.RegistryValueKind.String, "1", false)
                    == NicLegacyValueDecision.ClearReceipt,
                "a V1 receipt with the original already present must clear without writing");
            NicCheck(NicModerationTweak.DecideLegacyValue(true, "0",
                    Microsoft.Win32.RegistryValueKind.String, "1", false)
                    == NicLegacyValueDecision.RestoreOriginal,
                "a V1 Prepared/Applied crash window may CAS only exact legacy Desired=0");
            NicCheck(NicModerationTweak.DecideLegacyValue(true, "2",
                    Microsoft.Win32.RegistryValueKind.String, "1", false)
                    == NicLegacyValueDecision.ClearReceipt,
                "a V1 external value must be preserved and its receipt settled");
            NicCheck(NicModerationTweak.DecideLegacyValue(true, 0,
                    Microsoft.Win32.RegistryValueKind.DWord, "1", false)
                    == NicLegacyValueDecision.ClearReceipt,
                "a V1 kind change is external and must never be overwritten");
        }

        private static NicModerationTarget Target(string id, NicModerationMode mode, int index)
        {
            return new NicModerationTarget {
                SubKey = "0001", NetCfgInstanceId = id, DeviceInstanceId = "PCI\\VEN_TEST",
                Label = "fixture", DriverVersion = "1", Service = "fixture", InfPath = "oem1.inf",
                Mode = mode, LinkUp = true, PhysicalWired = true, InterfaceIndex = index };
        }

        private static NicModerationScan OneScan(NicModerationMode mode)
        {
            var scan = new NicModerationScan {
                Success = true, LiveStateReliable = true, BestInterfaceIndex = 7 };
            scan.Targets.Add(Target("{ABC}", mode, 7));
            return scan;
        }

        private sealed class FakeNicPlatform : INicModerationPlatform
        {
            internal readonly NicModerationScan ScanValue;
            internal NicModerationWriteResult NextResult = NicModerationWriteResult.Applied;
            internal bool MutateOnReadbackFailure;
            internal int Writes;

            internal FakeNicPlatform(NicModerationScan scan) { ScanValue = scan; }
            public NicModerationScan Scan() { return ScanValue; }

            public NicModerationWriteResult CompareExchange(NicModerationTarget target,
                NicModerationMode expected, NicModerationMode desired)
            {
                Writes++;
                NicModerationWriteResult result = NextResult;
                NextResult = NicModerationWriteResult.Applied;
                if (result == NicModerationWriteResult.ReadbackFailed
                    && MutateOnReadbackFailure && target != null)
                    target.Mode = desired;
                if (result != NicModerationWriteResult.Applied) return result;
                if (target == null || target.Mode != expected)
                    return NicModerationWriteResult.CurrentChanged;
                target.Mode = desired;
                return NicModerationWriteResult.Applied;
            }
        }

        private sealed class FakeNicStore : INicModerationReceiptStore
        {
            internal string Raw = "";
            internal int SaveCalls;
            internal int FailSaveCall;
            internal bool Readable = true;

            public bool TryRead(out string raw)
            {
                raw = Raw;
                return Readable;
            }

            public bool SaveAndVerify(string raw)
            {
                SaveCalls++;
                if (FailSaveCall == SaveCalls) return false;
                Raw = raw ?? "";
                Readable = true;
                return true;
            }
        }
    }
}
#endif
