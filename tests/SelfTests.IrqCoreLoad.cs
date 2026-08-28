using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private sealed class TestCoreLoadSource : ICoreLoadSource
        {
            internal int Reads, Disposals;
            internal bool ThrowOnRead;
            internal Dictionary<int, double> Values = new Dictionary<int, double>();
            public Dictionary<int, double> Read()
            { Reads++; if (ThrowOnRead) throw new IOException("fake counter failure"); return Values; }
            public void Dispose() { Disposals++; }
        }

        private static void NearLoad(double expected, double actual)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > 0.000001)
                throw new Exception("Expected load " + expected + ", actual " + actual);
        }

        private static IrqSessionRecord LoadTestRecord(int seconds)
        {
            var record = new IrqSessionRecord { StartUtcTicks = new DateTime(2026, 8, 27).Ticks,
                DurationSeconds = seconds, GameName = "Synthetic match", BootStamp = "1000000",
                TopologyStamp = "load-test", GameMask = 0xCUL, SystemMask = 0xFUL };
            record.Drivers.Add(new IrqDriverRecord { Driver = "load.sys", DriverVersion = "v1",
                Dpc = 100, DpcTotalNs = 1000000, DpcMaxNs = 967000, CpuMask = 1, Over500Us = 1 });
            return record;
        }

        private static void TestIrqCoreLoadWeighted()
        {
            long start = new DateTime(2026, 8, 27).Ticks, second = TimeSpan.TicksPerSecond;
            var source = new TestCoreLoadSource();
            source.Values[0] = 20; source.Values[1] = 0;
            var record = LoadTestRecord(5);
            using (var capture = new IrqCoreLoadCapture(source, start, 0xFUL))
            {
                capture.Poll(start); capture.Poll(start + second); Eq(0, source.Reads);
                capture.Poll(start + 2 * second); Eq(1, source.Reads);
                source.Values[0] = 80;
                source.Values.Remove(1); source.Values[2] = 100;
                capture.Poll(start + 4 * second); Eq(2, source.Reads);
                source.Values[0] = 50;
                capture.Finish(start + 5 * second, record); Eq(3, source.Reads);
                capture.Poll(start + 100 * second); capture.Finish(start + 100 * second, record);
                Eq(3, source.Reads); Eq(1, source.Disposals);
            }
            Eq(1, source.Disposals); Eq(3, record.CoreLoads.Count);
            Eq(5 * second, record.CoreLoadWindowTicks);
            NearLoad(50, record.CoreLoads[0].AveragePercent); Eq(3, record.CoreLoads[0].Samples);
            NearLoad(0, record.CoreLoads[1].AveragePercent); Eq(2 * second, record.CoreLoads[1].ObservedTicks);
            NearLoad(100, record.CoreLoads[2].AveragePercent); Eq(3 * second, record.CoreLoads[2].ObservedTicks);
            Eq(true, IrqSessionLedger.ValidCoreLoads(record));
        }

        private static void TestIrqCoreLoadMissingAndFailure()
        {
            long start = new DateTime(2026, 8, 27).Ticks, second = TimeSpan.TicksPerSecond;
            foreach (int failure in new[] { 0, 1, 2 })
            {
                var source = new TestCoreLoadSource(); source.Values[0] = 64;
                var record = LoadTestRecord(6);
                using (var capture = new IrqCoreLoadCapture(source, start, 0xFUL))
                {
                    capture.Poll(start + 2 * second);
                    if (failure == 0) source.Values = null;
                    if (failure == 1) source.Values.Clear();
                    if (failure == 2) source.ThrowOnRead = true;
                    capture.Poll(start + 4 * second); capture.Finish(start + 6 * second, record);
                }
                Eq(1, source.Disposals); Eq(1, record.CoreLoads.Count);
                NearLoad(64, record.CoreLoads[0].AveragePercent);
                Eq(2 * second, record.CoreLoads[0].ObservedTicks);
                Eq(6 * second, record.CoreLoadWindowTicks);
            }
            var invalid = new TestCoreLoadSource();
            invalid.Values[-1] = 10; invalid.Values[64] = 10; invalid.Values[4] = 10;
            invalid.Values[0] = double.NaN; invalid.Values[1] = double.PositiveInfinity;
            invalid.Values[2] = -1; invalid.Values[3] = 101;
            var empty = LoadTestRecord(2);
            using (var capture = new IrqCoreLoadCapture(invalid, start, 0xFUL))
                capture.Finish(start + 2 * second, empty);
            Eq(0, empty.CoreLoads.Count);
            using (var capture = new IrqCoreLoadCapture(null, start, 0xFUL))
            { capture.Poll(start + 2 * second); capture.Finish(start + 2 * second, LoadTestRecord(2)); }
            var all = new TestCoreLoadSource();
            for (int cpu = 0; cpu < 64; cpu++) all.Values[cpu] = cpu;
            var full = LoadTestRecord(2); full.SystemMask = ulong.MaxValue;
            using (var capture = new IrqCoreLoadCapture(all, start, ulong.MaxValue))
                capture.Finish(start + 2 * second, full);
            Eq(64, full.CoreLoads.Count); Eq(true, IrqSessionLedger.ValidCoreLoads(full));
            var dropped = new TestCoreLoadSource();
            using (var capture = new IrqCoreLoadCapture(dropped, start, 0xFUL))
            { capture.Poll(start - second); capture.Dispose(); capture.Poll(start + second * 5); }
            Eq(0, dropped.Reads); Eq(1, dropped.Disposals);
        }

        private static void TestIrqCoreLoadLifecycle()
        {
            foreach (bool strict in new[] { false, true })
            {
                int opens = 0; var source = new TestCoreLoadSource(); source.Values[0] = 81;
                var platform = new ObservationTestPlatform();
                platform.CoreLoadFactory = delegate { opens++; return source; };
                using (var probe = new IrqSessionProbe(platform))
                {
                    probe.Arm("load match", ObservationSystemMask, !strict); Eq(0, opens);
                    Eq(true, strict ? probe.ConfirmGameMask(ObservationStrictMask,
                        ObservationRendererPid, ObservationRendererCreation)
                        : probe.ConfirmSystemObservation(ObservationRendererPid, ObservationRendererCreation));
                    Eq(1, opens);
                    AdvanceIrqObservation(platform, probe, 5, strict); Eq(2, source.Reads);
                    platform.Captures[0].OnStop = delegate { Eq(1, source.Disposals); source.Values[0] = 0; };
                    probe.Seal(); Eq(3, source.Reads); Eq(0, platform.Records.Count);
                    platform.Now += 8 * TimeSpan.TicksPerSecond;
                    probe.TakeSummary(); Eq(3, source.Reads); Eq(1, source.Disposals);
                    Eq(1, platform.Records.Count); var record = platform.Records[0];
                    Eq(5 * TimeSpan.TicksPerSecond, record.CoreLoadWindowTicks);
                    NearLoad(81, record.CoreLoads[0].AveragePercent);
                    Eq(strict ? ObservationStrictMask : 0UL, record.GameMask);
                    Eq(true, IrqSessionLedger.ValidCoreLoads(record));
                    probe.TakeSummary(); Eq(1, platform.Records.Count);
                }
            }
        }

        private static void TestIrqCoreLoadDiscardAndRearm()
        {
            for (int reason = 0; reason < 6; reason++)
            {
                var sources = new List<TestCoreLoadSource>();
                var platform = new ObservationTestPlatform();
                platform.CoreLoadFactory = delegate {
                    var source = new TestCoreLoadSource(); source.Values[0] = sources.Count == 0 ? 81 : 20;
                    sources.Add(source); return source; };
                using (var probe = new IrqSessionProbe(platform))
                {
                    StartIrqObservation(platform, probe, "discarded");
                    AdvanceIrqObservation(platform, probe, 4, false);
                    if (reason == 0) probe.InvalidateGameMask();
                    if (reason == 1) { platform.Now += 3 * TimeSpan.TicksPerSecond;
                        probe.ConfirmSystemObservation(ObservationRendererPid, ObservationRendererCreation); }
                    if (reason == 2) { probe.Seal(); }
                    if (reason == 3) { platform.EnabledValue = false;
                        probe.ConfirmSystemObservation(ObservationRendererPid, ObservationRendererCreation);
                        platform.EnabledValue = true; }
                    if (reason == 4) probe.ConfirmSystemObservation(ObservationRendererPid, ObservationRendererCreation + 1);
                    if (reason == 5) { probe.Arm("replacement", ObservationSystemMask, true); }
                    Eq(1, sources[0].Disposals); Eq(0, platform.Records.Count);
                    StartIrqObservation(platform, probe, "new epoch");
                    AdvanceIrqObservation(platform, probe, 3, false);
                    probe.Seal(); probe.TakeSummary();
                    Eq(1, platform.Records.Count); Eq("new epoch", platform.Records[0].GameName);
                    Eq(3 * TimeSpan.TicksPerSecond, platform.Records[0].CoreLoadWindowTicks);
                    NearLoad(20, platform.Records[0].CoreLoads[0].AveragePercent);
                    Eq(1, sources[1].Disposals);
                }
            }
            var throwing = new ObservationTestPlatform();
            throwing.CoreLoadFactory = delegate { throw new IOException("no PDH"); };
            using (var probe = new IrqSessionProbe(throwing))
            { StartIrqObservation(throwing, probe, "counter unavailable");
                AdvanceIrqObservation(throwing, probe, 3, false); probe.Seal(); probe.TakeSummary(); }
            Eq(1, throwing.Records.Count); Eq(0, throwing.Records[0].CoreLoads.Count);
        }

        private static void TestIrqCoreLoadLedger(string parent)
        {
            string work = Path.Combine(parent, "core-load-ledger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            IrqSessionLedger.Bind(work);
            string path = Path.Combine(work, IrqSessionLedger.FileName);
            var legacy = LoadTestRecord(5);
            Eq(true, IrqSessionLedger.Append(legacy));
            string legacyText = File.ReadAllText(path).Replace("PAVISE_IRQ_SESSIONS_V4", "PAVISE_IRQ_SESSIONS_V3");
            File.WriteAllText(path, legacyText, new UTF8Encoding(false));
            string issue; var loaded = IrqSessionLedger.Load(out issue);
            Eq("", issue); Eq(1, loaded.Count); Eq(0, loaded[0].CoreLoads.Count);
            var fresh = LoadTestRecord(5); fresh.CoreLoadWindowTicks = 5 * TimeSpan.TicksPerSecond;
            fresh.CoreLoads.Add(new IrqCoreLoadRecord { Cpu = 0, AveragePercent = 81.25,
                ObservedTicks = 4 * TimeSpan.TicksPerSecond, Samples = 2 });
            Eq(true, IrqSessionLedger.Append(fresh));
            loaded = IrqSessionLedger.Load(out issue); Eq("", issue); Eq(2, loaded.Count);
            Eq(0, loaded[0].CoreLoads.Count); NearLoad(81.25, loaded[1].CoreLoads[0].AveragePercent);
            Eq(4 * TimeSpan.TicksPerSecond, loaded[1].CoreLoads[0].ObservedTicks);
            string valid = File.ReadAllText(path);
            Eq(true, valid.StartsWith("PAVISE_IRQ_SESSIONS_V4", StringComparison.Ordinal));
            string row = "C|0|81.25|40000000|2";
            foreach (string invalid in new[] {
                valid.Replace(row, "C|0|NaN|40000000|2"),
                valid.Replace(row, "C|64|81.25|40000000|2"),
                valid.Replace(row, "C|4|81.25|40000000|2"),
                valid.Replace(row, "C|0|81.25|60000000|2"),
                valid.Replace(row, "C|0|81.25|40000000|0"),
                valid.Replace(row, row + "\r\n" + row),
                valid.Replace("L|50000000", "L|60000000"),
                valid.Replace("L|50000000\r\n", ""),
                valid.Replace("V4", "V999") })
            {
                Eq(false, invalid == valid);
                File.WriteAllText(path, invalid, new UTF8Encoding(false));
                Eq(0, IrqSessionLedger.Load(out issue).Count); Eq(false, string.IsNullOrEmpty(issue));
                Eq(false, IrqSessionLedger.Append(fresh)); Eq(invalid, File.ReadAllText(path));
            }
            File.WriteAllText(path, valid, new UTF8Encoding(false));
            Eq(2, IrqSessionLedger.Load(out issue).Count); Eq("", issue);
            fresh.CoreLoads[0].AveragePercent = double.PositiveInfinity;
            Eq(false, IrqSessionLedger.Append(fresh)); Eq(valid, File.ReadAllText(path));
            IrqSessionLedger.Bind(parent);
        }

        private static void TestIrqPinSessionSources()
        {
            var older = LoadTestRecord(5); var latest = LoadTestRecord(10);
            older.GameMask = 3; older.Drivers[0].CpuMask = 8;
            latest.GameName = "latest"; latest.CoreLoadWindowTicks = 10 * TimeSpan.TicksPerSecond;
            latest.CoreLoads.Add(new IrqCoreLoadRecord { Cpu = 0, AveragePercent = 63,
                ObservedTicks = 5 * TimeSpan.TicksPerSecond, Samples = 3 });
            var records = new[] { older, latest };
            var device = new IrqDevice { Service = "load", Dpc = 99999, MaxUs = 9999, SeenOnCpus = 0xFUL };
            Func<string, string> version = delegate { return "v1"; };
            var view = IrqPinSession.FromLatest(records, device, "1000000", "load-test", version);
            Eq(true, view.Available); Eq("latest", view.GameName); Eq(0xCUL, view.GameMask);
            Eq(1UL, view.SeenMask); NearLoad(967, view.Driver.DpcMaxUs); NearLoad(63, view.Loads[0]);
            NearLoad(50, view.MinimumCoverage); NearLoad(50, view.MaximumCoverage);
            latest.CoreLoads[0].AveragePercent = 0; NearLoad(63, view.Loads[0]);
            latest.CoreLoads.Clear(); latest.CoreLoadWindowTicks = 0;
            view = IrqPinSession.FromLatest(records, device, "1000000", "load-test", version);
            Eq(true, view.Available); Eq(0, view.Loads.Count); Eq(1UL, view.SeenMask);
            latest.EventsLost = 1;
            view = IrqPinSession.FromLatest(records, device, "1000000", "load-test", version);
            Eq(false, view.Available); Eq(0UL, view.SeenMask); Eq(0UL, view.GameMask);
            latest.EventsLost = 0;
            view = IrqPinSession.FromLatest(records, device, "90000000", "load-test", version);
            Eq(false, view.Available);
            view = IrqPinSession.FromLatest(records, device, "1000000", "different-topology", version);
            Eq(false, view.Available);
            view = IrqPinSession.FromLatest(records, device, "1000000", "load-test", delegate { return "v2"; });
            Eq(true, view.Available); Eq(true, view.Driver == null); Eq(0UL, view.SeenMask);
            latest.Drivers[0].MaskTruncated = true;
            view = IrqPinSession.FromLatest(records, device, "1000000", "load-test", version);
            Eq(0UL, view.SeenMask); Eq(false, view.Driver == null);
            latest.TopologyStamp = "";
            Eq(false, IrqPinSession.FromLatest(records, device, "1000000", "load-test", version).Available);
            Eq(false, IrqPinSession.FromLatest(null, device, "1000000", "load-test", version).Available);
        }
    }
}
