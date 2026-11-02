#if PAVISE_HWSCH_PROBE_CHECKS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class HardwareSchedulingProbeChecks
    {
        private const string Luid = "f123456789abcdef";
        private static int checks;
        private sealed class Fake : IHardwareSchedulingNative
        {
            internal uint Major = 10, Minor, Build = 26100;
            internal bool VersionOk = true, Bound = true;
            internal uint OutputHandle = 0x12345678;
            internal uint? ReplaceOpenLow, ReplaceQueryHandle, ReplaceQueryFeature;
            internal int? ReplaceOpenHigh;
            internal ushort Version = 1, Bits = 15;
            internal int OpenStatus, QueryStatus, CloseStatus;
            internal string ThrowAt;
            internal bool ReplaceCloseHandle;
            internal uint SeenLow, SeenHandle, ClosedHandle;
            internal int SeenHigh;
            internal readonly List<string> Calls = new List<string>();
            private void Visit(string name)
            { Calls.Add(name); if (ThrowAt == name) throw new InvalidOperationException("Injected " + name); }
            public bool TryGetOsVersion(out uint major, out uint minor, out uint build)
            { major = Major; minor = Minor; build = Build; Visit("version"); return VersionOk; }
            public bool TryBindEntryPoints() { Visit("bind"); return Bound; }
            public int OpenAdapter(ref HwsOpenAdapter args)
            {
                SeenLow = args.AdapterLuid.LowPart; SeenHigh = args.AdapterLuid.HighPart;
                Check(args.Adapter == 0, "open output initialized to zero");
                args.Adapter = OutputHandle;
                if (ReplaceOpenLow.HasValue) args.AdapterLuid.LowPart = ReplaceOpenLow.Value;
                if (ReplaceOpenHigh.HasValue) args.AdapterLuid.HighPart = ReplaceOpenHigh.Value;
                Visit("open"); return OpenStatus;
            }
            public int QueryFeature(ref HwsFeatureQuery args)
            {
                SeenHandle = args.Adapter;
                Check(args.Adapter == OutputHandle && args.FeatureId == 0, "query exact opened adapter and formal feature zero");
                Check(args.Result.Version == 0 && args.Result.Bits == 0, "query output initialized to zero");
                args.Result.Version = Version; args.Result.Bits = Bits;
                if (ReplaceQueryHandle.HasValue) args.Adapter = ReplaceQueryHandle.Value;
                if (ReplaceQueryFeature.HasValue) args.FeatureId = ReplaceQueryFeature.Value;
                Visit("query"); return QueryStatus;
            }
            public int CloseAdapter(ref HwsCloseAdapter args)
            {
                ClosedHandle = args.Adapter;
                Check(args.Adapter == OutputHandle, "close the original owned handle");
                if (ReplaceCloseHandle) args.Adapter = 17;
                Visit("close"); return CloseStatus;
            }
            public void Dispose() { Visit("dispose"); }
        }

        private static void Check(bool ok, string message)
        { checks++; if (!ok) throw new Exception("FAIL: " + message); }

        private static HardwareSchedulingObservation Run(Fake fake)
        { return HardwareSchedulingProbe.QueryWithBridge(Luid, fake); }

        private static void Unavailable(Fake fake, HardwareSchedulingProbeReason reason, string trace)
        {
            var result = Run(fake);
            Check(result.State == HardwareSchedulingState.ProbeUnavailable && !result.IsKnown, "unavailable never implies disabled: " + reason);
            Check(result.Reason == reason, "typed reason " + reason + " (got " + result.Reason + ")");
            Check(result.SupportedByDriver == null && result.SupportedOnCurrentConfig == null, "unavailable is not unsupported: " + reason);
            Check(result.RendererLuid == Luid && !string.IsNullOrEmpty(result.Detail), "unavailable retains renderer and reason");
            Check(string.Join(",", fake.Calls.ToArray()) == trace, "cleanup/call order " + reason);
        }

        private static void BasicAndIdentity()
        {
            Check((int)HardwareSchedulingState.ProbeUnavailable == 0, "default state is unavailable");
            var enabled = new Fake(); var result = Run(enabled);
            Check(result.State == HardwareSchedulingState.Enabled && result.Reason == HardwareSchedulingProbeReason.None, "known enabled");
            Check(result.FeatureVersion == 1 && result.RawResultBits == 15 && result.NativeStatus == 0, "successful raw observations retained");
            Check(result.SupportedByDriver == true && result.SupportedOnCurrentConfig == true, "known driver/config flags");
            Check(enabled.SeenLow == 0x89abcdef && enabled.SeenHigh == unchecked((int)0xf1234567), "both LUID halves and signed high retained");
            Check(enabled.SeenHandle == enabled.OutputHandle && enabled.ClosedHandle == enabled.OutputHandle, "same handle queried/closed");
            Check(string.Join(",", enabled.Calls.ToArray()) == "version,bind,open,query,close,dispose", "single exact adapter, then close, then release");
            var off = Run(new Fake { Bits = 2, Version = 0 });
            Check(off.State == HardwareSchedulingState.Disabled && off.SupportedByDriver == false && off.SupportedOnCurrentConfig == false,
                "known disabled with no driver/config flags is an HWSCH observation, no GPU-priority verdict");
            Check(Run(new Fake { Bits = 14, Version = 1 }).State == HardwareSchedulingState.Disabled, "known disabled version1 accepted");
            Check(Run(new Fake { Bits = 31 }).State == HardwareSchedulingState.Enabled, "documented Available bit does not change Enabled");
            Check(Run(new Fake { Bits = 47 }).State == HardwareSchedulingState.Enabled, "documented Experimental bit does not invent feature version");

            foreach (string bad in new[] { null, "", "0", "0000000000000000", "0123456789ABCDEF", " 123456789abcdef", "0123456789abcdeff", "g123456789abcdef", "0123456789abcde\n" })
            {
                var fake = new Fake(); var invalid = HardwareSchedulingProbe.QueryWithBridge(bad, fake);
                Check(invalid.State == HardwareSchedulingState.ProbeUnavailable && invalid.Reason == HardwareSchedulingProbeReason.InvalidRendererLuid, "malformed LUID refused");
                Check(string.Join(",", fake.Calls.ToArray()) == "dispose", "invalid input never opens any adapter");
            }
            var minimal = new Fake(); Check(HardwareSchedulingProbe.QueryWithBridge("0000000000000001", minimal).IsKnown && minimal.SeenHigh == 0 && minimal.SeenLow == 1, "minimal nonzero renderer LUID");
            var highOnly = new Fake(); Check(HardwareSchedulingProbe.QueryWithBridge("8000000000000000", highOnly).IsKnown && highOnly.SeenHigh == int.MinValue && highOnly.SeenLow == 0, "negative high-only LUID");
            bool rejected = false; try { HardwareSchedulingProbe.QueryWithBridge(Luid, null); } catch (ArgumentNullException) { rejected = true; }
            Check(rejected, "no implicit native fallback on null bridge");
        }

        private static void ErrorsAndCleanup()
        {
            Unavailable(new Fake { VersionOk = false }, HardwareSchedulingProbeReason.OsVersionUnavailable, "version,dispose");
            foreach (uint oldBuild in new uint[] { 0, 19045, 22000, 22631, 26099 })
                Unavailable(new Fake { Build = oldBuild }, HardwareSchedulingProbeReason.OsVersionOutsideContract, "version,dispose");
            Unavailable(new Fake { Major = 6 }, HardwareSchedulingProbeReason.OsVersionOutsideContract, "version,dispose");
            Unavailable(new Fake { Major = 11 }, HardwareSchedulingProbeReason.OsVersionOutsideContract, "version,dispose");
            Unavailable(new Fake { Minor = 1 }, HardwareSchedulingProbeReason.OsVersionOutsideContract, "version,dispose");
            Check(Run(new Fake { Build = 30000 }).IsKnown, "later build with same documented OS contract may query");
            Unavailable(new Fake { Bound = false }, HardwareSchedulingProbeReason.EntryPointsUnavailable, "version,bind,dispose");
            foreach (int status in new[] { unchecked((int)0xc000000d), unchecked((int)0x80000005), 1, 0x103 })
            {
                var openFailure = new Fake { OpenStatus = status, OutputHandle = 0 };
                Unavailable(openFailure, HardwareSchedulingProbeReason.OpenFailed, "version,bind,open,dispose");
                Check(Run(new Fake { OpenStatus = status, OutputHandle = 0 }).NativeStatus == status, "open error status preserved");
                Unavailable(new Fake { OpenStatus = status }, HardwareSchedulingProbeReason.OpenFailed, "version,bind,open,close,dispose");
                Unavailable(new Fake { QueryStatus = status }, HardwareSchedulingProbeReason.QueryFailed, "version,bind,open,query,close,dispose");
                Check(Run(new Fake { QueryStatus = status }).NativeStatus == status, "query error status preserved");
                Unavailable(new Fake { CloseStatus = status }, HardwareSchedulingProbeReason.CloseFailed, "version,bind,open,query,close,dispose");
            }
            Unavailable(new Fake { OutputHandle = 0 }, HardwareSchedulingProbeReason.InvalidAdapterHandle, "version,bind,open,dispose");
            Unavailable(new Fake { ReplaceOpenLow = 1 }, HardwareSchedulingProbeReason.RendererLuidChanged, "version,bind,open,close,dispose");
            Unavailable(new Fake { ReplaceOpenHigh = 1 }, HardwareSchedulingProbeReason.RendererLuidChanged, "version,bind,open,close,dispose");
            Unavailable(new Fake { ReplaceQueryHandle = 0xdeadbeef }, HardwareSchedulingProbeReason.QueryIdentityChanged, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { ReplaceQueryFeature = 70 }, HardwareSchedulingProbeReason.QueryIdentityChanged, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { ReplaceCloseHandle = true }, HardwareSchedulingProbeReason.CloseFailed, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { ThrowAt = "version" }, HardwareSchedulingProbeReason.NativeFailure, "version,dispose");
            Unavailable(new Fake { ThrowAt = "bind" }, HardwareSchedulingProbeReason.NativeFailure, "version,bind,dispose");
            Unavailable(new Fake { ThrowAt = "open" }, HardwareSchedulingProbeReason.NativeFailure, "version,bind,open,close,dispose");
            Unavailable(new Fake { ThrowAt = "query" }, HardwareSchedulingProbeReason.NativeFailure, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { ThrowAt = "close" }, HardwareSchedulingProbeReason.CloseFailed, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { ThrowAt = "dispose" }, HardwareSchedulingProbeReason.BridgeDisposeFailed, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { Bits = 0 }, HardwareSchedulingProbeReason.FeatureUnknown, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { Bits = 1 }, HardwareSchedulingProbeReason.FeatureUnknown, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { Version = 0 }, HardwareSchedulingProbeReason.UnsupportedFeatureVersion, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { Version = 2 }, HardwareSchedulingProbeReason.UnsupportedFeatureVersion, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { Version = ushort.MaxValue, Bits = 2 }, HardwareSchedulingProbeReason.UnsupportedFeatureVersion, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { Bits = 3 }, HardwareSchedulingProbeReason.InconsistentFeatureResult, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { Bits = 7 }, HardwareSchedulingProbeReason.InconsistentFeatureResult, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { Bits = 11 }, HardwareSchedulingProbeReason.InconsistentFeatureResult, "version,bind,open,query,close,dispose");
            Unavailable(new Fake { Bits = 0x4f }, HardwareSchedulingProbeReason.UnsupportedResultBits, "version,bind,open,query,close,dispose");
        }

        private static void FlagAndVersionMatrix()
        {
            // Independent truth table for the complete documented flag space and every reserved single-bit extension.
            // Version zero is admitted only for a known Disabled observation; version1 handles current Enabled/Disabled.
            for (ushort bits = 0; bits < 64; bits++)
                foreach (ushort version in new ushort[] { 0, 1, 2, ushort.MaxValue })
                {
                    var fake = new Fake { Bits = bits, Version = version }; var result = Run(fake);
                    bool known = (bits & 2) != 0, enabled = (bits & 1) != 0;
                    bool admitted = known && (enabled ? version == 1 && (bits & 12) == 12 : version == 0 || version == 1);
                    var expected = !admitted ? HardwareSchedulingState.ProbeUnavailable : enabled ? HardwareSchedulingState.Enabled : HardwareSchedulingState.Disabled;
                    Check(result.State == expected, "flags/version truth table " + bits + "/" + version);
                    Check(fake.ClosedHandle == fake.OutputHandle && fake.Calls.Count == 6, "matrix always closes/releases");
                    Check(!result.IsKnown || (result.SupportedByDriver == ((bits & 4) != 0) && result.SupportedOnCurrentConfig == ((bits & 8) != 0)), "support bits remain distinct observations");
                }
            for (int bit = 6; bit < 16; bit++)
                Unavailable(new Fake { Bits = (ushort)(15 | (1 << bit)) }, HardwareSchedulingProbeReason.UnsupportedResultBits, "version,bind,open,query,close,dispose");
        }

        private static void Layouts()
        {
            Check(IntPtr.Size == 8, "x64 test process");
            Check(Marshal.SizeOf(typeof(HwsLuid)) == 8, "LUID size8");
            Check(Marshal.OffsetOf(typeof(HwsLuid), "HighPart").ToInt32() == 4, "LUID signed high offset4");
            Check(Marshal.SizeOf(typeof(HwsOpenAdapter)) == 12 && Marshal.OffsetOf(typeof(HwsOpenAdapter), "Adapter").ToInt32() == 8, "open size12 handle offset8");
            Check(Marshal.SizeOf(typeof(HwsFeatureResult)) == 4 && Marshal.OffsetOf(typeof(HwsFeatureResult), "Bits").ToInt32() == 2, "result size4 flags offset2");
            Check(Marshal.SizeOf(typeof(HwsFeatureQuery)) == 12, "query size12");
            Check(Marshal.OffsetOf(typeof(HwsFeatureQuery), "FeatureId").ToInt32() == 4 && Marshal.OffsetOf(typeof(HwsFeatureQuery), "Result").ToInt32() == 8, "query feature offset4 result offset8");
            Check(Marshal.SizeOf(typeof(HwsCloseAdapter)) == 4, "close size4");
            byte[] bytes = new byte[12]; var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var query = new HwsFeatureQuery { Adapter = 0x12345678, FeatureId = 0,
                    Result = new HwsFeatureResult { Version = 1, Bits = 15 } };
                Marshal.StructureToPtr(query, pinned.AddrOfPinnedObject(), false);
                Check(BitConverter.ToUInt32(bytes, 0) == 0x12345678 && BitConverter.ToUInt32(bytes, 4) == 0 && BitConverter.ToUInt16(bytes, 8) == 1 && BitConverter.ToUInt16(bytes, 10) == 15, "managed marshalling bytes match SDK contract");
            }
            finally { pinned.Free(); }
        }

        private static void NativeDefaultBlocked()
        {
            var result = HardwareSchedulingProbe.Query(Luid);
            Check(result.State == HardwareSchedulingState.ProbeUnavailable && result.Reason == HardwareSchedulingProbeReason.NativeForbiddenInTestBuild,
                "native default blocked before bridge creation in each test symbol configuration");
            Check(result.FeatureVersion == null && result.RawResultBits == null && result.NativeStatus == null, "blocked default fabricates no native data");
        }

        internal static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--negative-control") { Console.Error.WriteLine("FAIL: intentional negative control"); return 3; }
                BasicAndIdentity(); ErrorsAndCleanup(); FlagAndVersionMatrix(); Layouts(); NativeDefaultBlocked();
                Console.WriteLine("PASS " + checks + " hardware scheduling probe checks (fake bridge only)"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 1; }
        }
    }
}
#endif
