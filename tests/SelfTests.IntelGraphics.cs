// 文件用途 Intel 驱动回归 只用假适配器和内存台账
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private const string IntelTestId = "8086:1234:8086:0001:03:00:00:0000000000010001";
        private const string IntelOtherId = "8086:5678:8086:0002:00:02:00:0000000000010001";
        private static int intelChecks;

        internal static int RunIntelGraphicsRegressionTests()
        {
            Action[] cases =
            {
                IntelNativeAbiAndGlobalScope,
                IntelCapabilityMustBeExplicit,
                IntelAdapterSelectionAndArcIdentity,
                IntelNativeDefaultsRefuseTests,
                IntelOwnedApplyAndRestore,
                IntelPreExistingChoiceIsNotOwned,
                IntelCancellationBoundaries,
                IntelJournalBeforeNative,
                IntelExternalChangesWin,
                IntelAmbiguousApplyDoesNotClaimExternalOn,
                IntelCrashRecoveryUsesConfirmedOwnership,
                IntelAmbiguousRestoreIsNotReissued,
                IntelSettledCleanupDoesNotTouchDriver,
                IntelInvalidLedgerAndChangedAdapter,
                IntelReentrancyDoesNotIssueTwice,
                IntelResetAbandonsUnprovableReceipt
            };
            intelChecks = 0;
            foreach (Action test in cases) { test(); Console.WriteLine("PASS " + test.Method.Name); }
            Console.WriteLine("PASS intel-graphics groups=" + cases.Length + " assertions=" + intelChecks
                + " driver=mocked settings=in_memory windows_shown=false");
            return cases.Length;
        }

        private static void IntelCheck(bool condition, string detail)
        {
            intelChecks++;
            if (!condition) throw new InvalidOperationException("Intel regression: " + detail);
        }

        private sealed class IntelFakeLedger : IIntelGraphicsLedger
        {
            internal string Value = "";
            internal bool RejectRead, RejectClear, RejectPrepared, RejectAcknowledged, WrongPreparedReadback;
            internal Action<string> AfterWrite;
            internal readonly List<string> Writes = new List<string>();
            public bool TryRead(out string value)
            {
                value = WrongPreparedReadback && Value.StartsWith("1|P|", StringComparison.Ordinal) ? "different" : Value;
                return !RejectRead;
            }
            public bool TryWrite(string value)
            {
                Writes.Add(value);
                if (value.Length == 0 && RejectClear || value.StartsWith("1|P|", StringComparison.Ordinal) && RejectPrepared
                    || value.StartsWith("1|A|", StringComparison.Ordinal) && RejectAcknowledged) return false;
                Value = value;
                if (AfterWrite != null) AfterWrite(value);
                return true;
            }
        }

        private sealed class IntelFakeControl : IIntelGraphicsControl
        {
            internal readonly IntelFakeLedger Ledger;
            internal IntelGraphicsAdapter[] Adapters;
            internal readonly Dictionary<string, uint> Modes = new Dictionary<string, uint>();
            internal int Queries, Reads, Writes, WriteEntries;
            internal bool RejectRead, RejectQuery, DenyWrite, UncertainRestore, ApplyReadbackFailure;
            internal bool UncertainApply, ThrowUncertainApply;
            internal Action AfterQuery, BeforeRead, BeforeWrite, AfterWrite;
            internal readonly List<uint> WrittenValues = new List<uint>();
            internal IntelFakeControl(IntelFakeLedger ledger)
            {
                Ledger = ledger;
                Adapters = new[] { IntelAdapter(IntelTestId, false, true) };
                Modes[IntelTestId] = 0;
            }
            public bool TryGetAdapters(out IntelGraphicsAdapter[] value)
            {
                Queries++;
                value = Adapters;
                if (AfterQuery != null) AfterQuery();
                return !RejectQuery;
            }
            public bool TryReadLowLatency(string id, out uint value)
            {
                Reads++;
                if (BeforeRead != null) BeforeRead();
                value = 0;
                if (RejectRead || ApplyReadbackFailure && Writes > 0) return false;
                return Modes.TryGetValue(id, out value);
            }
            public IntelGraphicsWriteResult TryWriteLowLatency(string id, uint expected, uint value,
                Func<bool> continueWork)
            {
                WriteEntries++;
                if (BeforeWrite != null) BeforeWrite();
                if (!IntelGraphicsApi.Continue(continueWork)) return IntelGraphicsWriteResult.Cancelled;
                uint current;
                if (!Modes.TryGetValue(id, out current) || DenyWrite) return IntelGraphicsWriteResult.NotIssued;
                if (current != expected) return IntelGraphicsWriteResult.Conflict;
                IntelCheck((expected == 0 && value == 1) || (expected == 1 && value == 0), "forbidden driver setting");
                IntelCheck(Ledger.Value == "1|" + (value == 1 ? "P" : "R") + "|" + id,
                    "driver write preceded a verified write-ahead journal");
                Writes++;
                WrittenValues.Add(value);
                if (UncertainRestore && value == 0) return IntelGraphicsWriteResult.Uncertain;
                if (value == 1 && (UncertainApply || ThrowUncertainApply))
                {
                    // setter 失败或者没确认 偏好不用跟着改
                    // 引擎回读之前 别的调用方可能已经选了 On
                    if (AfterWrite != null) AfterWrite();
                    if (ThrowUncertainApply) throw new InvalidOperationException("unconfirmed setter");
                    return IntelGraphicsWriteResult.Uncertain;
                }
                Modes[id] = value;
                if (AfterWrite != null) AfterWrite();
                return IntelGraphicsWriteResult.Written;
            }
        }

        private static IntelGraphicsAdapter IntelAdapter(string id, bool integrated, bool supported)
        {
            return new IntelGraphicsAdapter
            { Id = id, Name = integrated ? "Intel integrated" : "Intel Arc B580", VendorId = 0x8086,
                Integrated = integrated, LowLatencySupported = supported };
        }

        private static void IntelNativeAbiAndGlobalScope()
        {
            IntelCheck(Marshal.SizeOf(typeof(IntelCtlInit)) == 36, "ctl_init_args ABI");
            IntelCheck(Marshal.SizeOf(typeof(IntelCtlAdapterProperties)) == (IntPtr.Size == 8 ? 320 : 312), "adapter ABI");
            IntelCheck(Marshal.SizeOf(typeof(IntelCtlFeatureDetails)) == (IntPtr.Size == 8 ? 72 : 64), "capabilities union ABI");
            IntelCheck(Marshal.SizeOf(typeof(IntelCtlFeatureCaps)) == (IntPtr.Size == 8 ? 24 : 16), "capability header ABI");
            IntelCheck(Marshal.SizeOf(typeof(IntelCtlFeatureValue)) == (IntPtr.Size == 8 ? 56 : 40), "get/set ABI");
            IntelCheck(Marshal.OffsetOf(typeof(IntelCtlFeatureValue), "Set").ToInt32() == (IntPtr.Size == 8 ? 25 : 17), "C++ bool must be one byte");
            IntelCtlFeatureValue request = IntelGraphicsApi.LowLatencyRequest(true, 1);
            IntelCheck(request.FeatureType == 16 && request.ValueType == 4 && request.Value.EnumValue == 1
                && request.Set && request.Version == 0 && request.ApplicationName == IntPtr.Zero
                && request.ApplicationNameLength == 0 && request.CustomValue == IntPtr.Zero,
                "request was not exclusively global basic low latency");
        }

        private static IntelCtlFeatureDetails IntelSupportedCapability()
        {
            return new IntelCtlFeatureDetails
            { FeatureType = 16, ValueType = 4, Value = new IntelCtlPropertyInfo { SupportedTypes = 7 }, MiscSupport = 19 };
        }

        private static void IntelCapabilityMustBeExplicit()
        {
            IntelCtlFeatureDetails good = IntelSupportedCapability();
            IntelCheck(IntelGraphicsApi.SafeLowLatencyCapability(good), "valid capability rejected");
            var value = good; value.FeatureType = 17;
            IntelCheck(!IntelGraphicsApi.SafeLowLatencyCapability(value), "XeSS feature mistaken for low latency");
            value = good; value.ValueType = 0;
            IntelCheck(!IntelGraphicsApi.SafeLowLatencyCapability(value), "unexpected value ABI accepted");
            value = good; value.Value.SupportedTypes = 5;
            IntelCheck(!IntelGraphicsApi.SafeLowLatencyCapability(value), "Boost-only capability enabled basic On");
            value = good; value.MiscSupport = 3;
            IntelCheck(!IntelGraphicsApi.SafeLowLatencyCapability(value), "non-live setting enabled");
            value = good; value.MiscSupport = 16 | 4;
            IntelCheck(!IntelGraphicsApi.SafeLowLatencyCapability(value), "DX12-only capability advertised as DX9/11");
            value = good; value.ConflictingFeatures = 1L << 17;
            IntelCheck(!IntelGraphicsApi.SafeLowLatencyCapability(value), "conflicting capability enabled");
        }

        private static void IntelAdapterSelectionAndArcIdentity()
        {
            var info = new IntelCtlAdapterProperties
            { Version = 2, VendorId = 0x8086, PciDeviceId = 0x1234, SubsystemVendorId = 0x8086,
                SubsystemId = 1, Bus = 3, DriverVersion = 0x10001, AdapterFlags = 0 };
            IntelCheck(IntelGraphicsApi.AdapterIdentity(info) == IntelTestId, "PCI identity unstable");
            IntelGraphicsAdapter arc = IntelAdapter(IntelTestId, (info.AdapterFlags & 1) != 0, true);
            IntelGraphicsAdapter integrated = IntelAdapter(IntelOtherId, true, true);
            IntelCheck(!arc.Integrated && IntelLowLatencyEngine.SelectAdapter(new[] { integrated, arc }) == arc,
                "Intel brand incorrectly made Arc B580 integrated");
            var nv = new IntelGraphicsAdapter { VendorId = 0x10DE, Integrated = false };
            IntelCheck(IntelLowLatencyEngine.SelectAdapter(new[] { nv, integrated }) == null, "wrong mixed-GPU target");
            IntelCheck(IntelLowLatencyEngine.SelectAdapter(new[] { arc, IntelAdapter(IntelOtherId, false, true) }) == null,
                "ambiguous Intel dGPU selection");
            arc.LowLatencySupported = false;
            IntelCheck(IntelLowLatencyEngine.SelectAdapter(new[] { integrated, arc }) == null,
                "unsupported dGPU silently redirected writes to the iGPU");
        }

        private static void IntelNativeDefaultsRefuseTests()
        {
            var api = new IntelGraphicsApi();
            IntelGraphicsAdapter[] adapters; uint value;
            IntelCheck(!api.TryGetAdapters(out adapters) && adapters == null, "unmocked adapter enumeration");
            IntelCheck(!api.TryReadLowLatency(IntelTestId, out value), "unmocked driver read");
            IntelCheck(api.TryWriteLowLatency(IntelTestId, 0, 1, null) == IntelGraphicsWriteResult.NotIssued,
                "unmocked driver write");
            IntelCheck(api.TryWriteLowLatency(IntelTestId, 0, 2, null) == IntelGraphicsWriteResult.NotIssued,
                "Boost escaped native allowlist");
        }

        private static void IntelOwnedApplyAndRestore()
        {
            var ledger = new IntelFakeLedger(); var api = new IntelFakeControl(ledger);
            var engine = new IntelLowLatencyEngine(api, ledger);
            IntelCheck(engine.Apply(null) && engine.Active && engine.HasResidue && api.Modes[IntelTestId] == 1,
                "owned activation failed");
            IntelCheck(ledger.Value == "1|A|" + IntelTestId, "acknowledgement not saved");
            IntelCheck(engine.Apply(null) && api.Writes == 1, "active session rewrote the driver");
            IntelCheck(engine.Restore() && !engine.Active && !engine.HasResidue && api.Modes[IntelTestId] == 0,
                "owned original not restored and cleared");
            IntelCheck(api.WrittenValues.Count == 2 && api.WrittenValues[0] == 1 && api.WrittenValues[1] == 0,
                "unexpected write sequence");
        }

        private static void IntelPreExistingChoiceIsNotOwned()
        {
            foreach (uint original in new uint[] { 1, 2 })
            {
                var ledger = new IntelFakeLedger(); var api = new IntelFakeControl(ledger);
                api.Modes[IntelTestId] = original;
                var engine = new IntelLowLatencyEngine(api, ledger);
                IntelCheck(engine.Apply(null) && !engine.Active && !engine.HasResidue, "existing choice claimed");
                api.Modes[IntelTestId] = 0;
                int queries = api.Queries, reads = api.Reads;
                IntelCheck(engine.Apply(null) && api.Writes == 0 && ledger.Writes.Count == 0
                    && api.Queries == queries && api.Reads == reads, "existing choice re-enforced during session");
                IntelCheck(engine.Restore() && api.Writes == 0, "unowned preference restored");
                IntelCheck(engine.Apply(null) && api.Writes == 1, "new session remained suppressed");
                IntelCheck(engine.Restore(), "new session cleanup failed");
            }
        }

        private static void IntelCancellationBoundaries()
        {
            var deniedLedger = new IntelFakeLedger(); var deniedApi = new IntelFakeControl(deniedLedger);
            var deniedEngine = new IntelLowLatencyEngine(deniedApi, deniedLedger);
            IntelCheck(!deniedEngine.Apply(delegate { return false; }) && deniedApi.Queries == 0,
                "entry cancellation queried the driver");
            IntelCheck(!deniedEngine.Apply(delegate { throw new InvalidOperationException("canceled"); })
                && deniedApi.Queries == 0, "throwing admission did not fail closed");
            for (int boundary = 0; boundary < 6; boundary++)
            {
                var ledger = new IntelFakeLedger(); var api = new IntelFakeControl(ledger);
                var engine = new IntelLowLatencyEngine(api, ledger);
                bool allowed = true;
                if (boundary == 0) api.AfterQuery = delegate { allowed = false; };
                if (boundary == 1) api.BeforeRead = delegate { allowed = false; };
                if (boundary == 2) ledger.AfterWrite = delegate(string text) { if (text.StartsWith("1|P|", StringComparison.Ordinal)) allowed = false; };
                if (boundary == 3) api.BeforeWrite = delegate { allowed = false; };
                if (boundary == 4) api.AfterWrite = delegate { allowed = false; };
                if (boundary == 5) ledger.AfterWrite = delegate(string text) { if (text.StartsWith("1|A|", StringComparison.Ordinal)) allowed = false; };
                IntelCheck(!engine.Apply(delegate { return allowed; }), "obsolete activation succeeded");
                IntelCheck(!engine.Active && api.Modes[IntelTestId] == 0 && !engine.HasResidue,
                    "cancel left an owned driver change");
                IntelCheck(api.Writes == (boundary >= 4 ? 2 : 0), "native write crossed cancellation boundary");
            }
        }

        private static void IntelJournalBeforeNative()
        {
            var unreadable = new IntelFakeLedger { RejectRead = true };
            var untouched = new IntelFakeControl(unreadable);
            var blocked = new IntelLowLatencyEngine(untouched, unreadable);
            IntelCheck(blocked.HasResidue && !blocked.Apply(null) && untouched.Queries == 0
                && untouched.WriteEntries == 0, "unreadable ownership state allowed native work");
            foreach (bool wrongReadback in new[] { false, true })
            {
                var ledger = new IntelFakeLedger { RejectPrepared = !wrongReadback, WrongPreparedReadback = wrongReadback };
                var api = new IntelFakeControl(ledger); var engine = new IntelLowLatencyEngine(api, ledger);
                IntelCheck(!engine.Apply(null) && api.WriteEntries == 0, "unverified preparation reached driver");
            }
            var failedAck = new IntelFakeLedger { RejectAcknowledged = true };
            var driver = new IntelFakeControl(failedAck); var cleaner = new IntelLowLatencyEngine(driver, failedAck);
            IntelCheck(!cleaner.Apply(null) && driver.Modes[IntelTestId] == 0 && !cleaner.HasResidue,
                "failed acknowledgement did not roll back the in-process write");
            var rejected = new IntelFakeLedger(); var denied = new IntelFakeControl(rejected) { DenyWrite = true };
            var refused = new IntelLowLatencyEngine(denied, rejected);
            IntelCheck(!refused.Apply(null) && denied.Writes == 0 && !refused.HasResidue
                && !refused.Active, "explicitly unissued driver write was claimed");
        }

        private static void IntelExternalChangesWin()
        {
            var ledger = new IntelFakeLedger(); var api = new IntelFakeControl(ledger);
            var engine = new IntelLowLatencyEngine(api, ledger);
            IntelCheck(engine.Apply(null), "setup activation");
            api.Modes[IntelTestId] = 2;
            IntelCheck(engine.Apply(null) && !engine.Active && !engine.HasResidue && api.Writes == 1,
                "external Boost was overwritten");
            api.Modes[IntelTestId] = 0;
            IntelCheck(engine.Apply(null) && api.Writes == 1, "conflict was re-enabled in the same session");
            var ledger2 = new IntelFakeLedger(); var api2 = new IntelFakeControl(ledger2);
            var engine2 = new IntelLowLatencyEngine(api2, ledger2);
            api2.BeforeWrite = delegate { api2.Modes[IntelTestId] = 1; };
            IntelCheck(!engine2.Apply(null) && api2.Writes == 0 && !engine2.HasResidue
                && api2.Modes[IntelTestId] == 1, "write-time conflict claimed another caller's On");
            var ledger3 = new IntelFakeLedger(); var api3 = new IntelFakeControl(ledger3);
            var engine3 = new IntelLowLatencyEngine(api3, ledger3);
            api3.AfterWrite = delegate { api3.Modes[IntelTestId] = 0; };
            IntelCheck(!engine3.Apply(null) && !engine3.Active && !engine3.Apply(null) && api3.Writes == 1,
                "readback failure retried a write or became a fake success");
        }

        private static void IntelAmbiguousApplyDoesNotClaimExternalOn()
        {
            foreach (bool throwing in new[] { false, true })
            {
                var ledger = new IntelFakeLedger();
                var api = new IntelFakeControl(ledger) { UncertainApply = !throwing, ThrowUncertainApply = throwing };
                api.AfterWrite = delegate { api.Modes[IntelTestId] = 1; };
                var engine = new IntelLowLatencyEngine(api, ledger);
                IntelCheck(!engine.Apply(null) && !engine.Active && engine.HasResidue
                    && ledger.Value == "1|P|" + IntelTestId && api.Modes[IntelTestId] == 1 && api.Writes == 1,
                    "unconfirmed setter claimed or rolled back another caller's On");
                IntelCheck(!engine.Restore() && !engine.Apply(null) && api.Writes == 1
                    && api.Modes[IntelTestId] == 1 && ledger.Value == "1|P|" + IntelTestId,
                    "same-process retry assumed ownership of an ambiguous On");
                var reloaded = new IntelLowLatencyEngine(api, ledger);
                IntelCheck(!reloaded.Restore() && api.Writes == 1 && api.Modes[IntelTestId] == 1,
                    "restart assumed ownership of an ambiguous On");
                api.Modes[IntelTestId] = 0;
                IntelCheck(reloaded.Restore() && !reloaded.HasResidue && api.Writes == 1,
                    "externally restored ambiguous apply required another setter");
            }
            foreach (uint external in new uint[] { 0, 2 })
            {
                var ledger = new IntelFakeLedger(); var api = new IntelFakeControl(ledger) { UncertainApply = true };
                api.AfterWrite = delegate { api.Modes[IntelTestId] = external; };
                var engine = new IntelLowLatencyEngine(api, ledger);
                IntelCheck(!engine.Apply(null) && !engine.Active && !engine.HasResidue
                    && api.Writes == 1 && api.Modes[IntelTestId] == external,
                    "unconfirmed non-On readback changed an external value or kept false ownership");
            }
        }

        private static void IntelCrashRecoveryUsesConfirmedOwnership()
        {
            var ledger = new IntelFakeLedger(); var api = new IntelFakeControl(ledger);
            IntelCheck(new IntelLowLatencyEngine(api, ledger).Apply(null), "setup activation");
            var reloaded = new IntelLowLatencyEngine(api, ledger);
            IntelCheck(reloaded.Restore() && api.Modes[IntelTestId] == 0 && !reloaded.HasResidue,
                "confirmed A not restored after restart");
            api.ApplyReadbackFailure = true;
            var incomplete = new IntelLowLatencyEngine(api, ledger);
            // 假实现是从某次 setter 之后才开始失败的 不是在抓原值那会儿
            api.Writes = 0;
            IntelCheck(!incomplete.Apply(null) && ledger.Value == "1|P|" + IntelTestId, "unconfirmed write lost P");
            api.ApplyReadbackFailure = false;
            int writes = api.Writes;
            var unknown = new IntelLowLatencyEngine(api, ledger);
            IntelCheck(!unknown.Restore() && unknown.HasResidue && api.Writes == writes,
                "unconfirmed P was assumed owned on restart");
            api.Modes[IntelTestId] = 0;
            IntelCheck(unknown.Restore() && !unknown.HasResidue && api.Writes == writes,
                "already-original P did not settle without a write");
        }

        private static void IntelAmbiguousRestoreIsNotReissued()
        {
            var ledger = new IntelFakeLedger(); var api = new IntelFakeControl(ledger);
            var engine = new IntelLowLatencyEngine(api, ledger);
            IntelCheck(engine.Apply(null), "setup activation");
            api.UncertainRestore = true;
            IntelCheck(!engine.Restore() && ledger.Value == "1|R|" + IntelTestId, "uncertain restore lost R");
            int writes = api.Writes;
            var reloaded = new IntelLowLatencyEngine(api, ledger);
            IntelCheck(!reloaded.Restore() && api.Writes == writes && !engine.Restore() && api.Writes == writes,
                "ambiguous R caused a second setter");
            api.Modes[IntelTestId] = 0;
            IntelCheck(reloaded.Restore() && api.Writes == writes, "manual original did not settle R");
        }

        private static void IntelSettledCleanupDoesNotTouchDriver()
        {
            var ledger = new IntelFakeLedger(); var api = new IntelFakeControl(ledger);
            var engine = new IntelLowLatencyEngine(api, ledger);
            IntelCheck(engine.Apply(null), "setup activation");
            ledger.RejectClear = true;
            IntelCheck(!engine.Restore() && ledger.Value == "1|S|" + IntelTestId && api.Modes[IntelTestId] == 0,
                "failed clear did not keep S");
            int writes = api.Writes, reads = api.Reads;
            api.Modes[IntelTestId] = 1; api.RejectRead = true; api.RejectQuery = true;
            ledger.RejectClear = false;
            var reloaded = new IntelLowLatencyEngine(api, ledger);
            IntelCheck(reloaded.Restore() && api.Writes == writes && api.Reads == reads && !reloaded.HasResidue,
                "settled cleanup needed a driver or overwrote a later user On");
        }

        private static void IntelInvalidLedgerAndChangedAdapter()
        {
            foreach (string malformed in new[] { "garbage", "1|A|", "1|A|" + IntelTestId.Replace("8086:", "10DE:"), "1|X|" + IntelTestId })
            {
                var ledger = new IntelFakeLedger { Value = malformed }; var api = new IntelFakeControl(ledger);
                var engine = new IntelLowLatencyEngine(api, ledger);
                IntelCheck(engine.HasResidue && !engine.Restore() && !engine.Apply(null)
                    && api.Queries == 0 && api.Reads == 0 && api.Writes == 0 && ledger.Value == malformed,
                    "malformed journal was erased or used for driver access");
            }
            var saved = new IntelFakeLedger(); var driver = new IntelFakeControl(saved);
            var owned = new IntelLowLatencyEngine(driver, saved);
            IntelCheck(owned.Apply(null), "setup activation");
            driver.Modes.Remove(IntelTestId); driver.Modes[IntelOtherId] = 1;
            driver.Adapters = new[] { IntelAdapter(IntelOtherId, false, true) };
            IntelCheck(!owned.Restore() && owned.HasResidue && driver.Writes == 1
                && driver.Modes[IntelOtherId] == 1, "missing adapter replayed journal against another card");
        }

        private static void IntelReentrancyDoesNotIssueTwice()
        {
            var ledger = new IntelFakeLedger(); var api = new IntelFakeControl(ledger);
            var engine = new IntelLowLatencyEngine(api, ledger);
            api.BeforeRead = delegate { IntelCheck(!engine.Apply(null), "recursive Apply accepted"); };
            IntelCheck(engine.Apply(null) && api.Writes == 1, "recursive callback changed native write count");
            api.BeforeRead = null;
            IntelCheck(engine.Restore(), "cleanup after reentrancy");
        }

        // 清除全部配置的放弃语义:发过还原写入后驱动仍是 On 的 R 收据永远无法认领
        //   重置语境下放弃并保留驱动现状;可认领的 A 收据与瞬时失败不放弃
        private static void IntelResetAbandonsUnprovableReceipt()
        {
            var ledger = new IntelFakeLedger { Value = "1|R|" + IntelTestId };
            var api = new IntelFakeControl(ledger);
            api.Modes[IntelTestId] = 1;
            var engine = new IntelLowLatencyEngine(api, ledger);
            IntelCheck(!engine.Restore(), "an unclaimable R receipt must not restore");
            IntelCheck(engine.AbandonUnprovableForReset(), "reset must abandon the unclaimable receipt");
            IntelCheck(!engine.HasResidue && api.Modes[IntelTestId] == 1,
                "abandoning must clear the residue without touching the driver value");

            var claimableLedger = new IntelFakeLedger { Value = "1|A|" + IntelTestId };
            var claimableApi = new IntelFakeControl(claimableLedger);
            claimableApi.Modes[IntelTestId] = 1;
            claimableApi.DenyWrite = true;
            var claimable = new IntelLowLatencyEngine(claimableApi, claimableLedger);
            IntelCheck(!claimable.Restore(), "setup: a transient restore failure");
            IntelCheck(!claimable.AbandonUnprovableForReset() && claimable.HasResidue,
                "a claimable receipt must never be abandoned by reset");
            claimableApi.DenyWrite = false;
            IntelCheck(claimable.Restore() && !claimable.HasResidue && claimableApi.Modes[IntelTestId] == 0,
                "the claimable receipt must still restore after the failure clears");
        }
    }
}
#endif
