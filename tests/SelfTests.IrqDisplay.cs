// 文件用途 验证短局原始展示与严格建议分离；不创建窗口、不启动 ETW、不写系统配置。
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static IrqSessionRecord IrqDisplayRecord(int seconds)
        {
            IrqSessionRecord rec = IrqStatusRecord();
            rec.DurationSeconds = seconds;
            rec.GameMask = 1;
            rec.Drivers[0].Dpc = Math.Max(1, seconds) * 20;
            rec.Drivers[0].DpcMaxNs = 900000;
            rec.Drivers[0].Over500Us = Math.Max(1, seconds) * 10;
            return rec;
        }

        private static void AssertIrqScoreEqual(IrqDriverVerdict expected, IrqDriverVerdict actual)
        {
            Eq(expected.SessionsSeen, actual.SessionsSeen);
            Eq(expected.SessionsOverThreshold, actual.SessionsOverThreshold);
            Eq(expected.ScoredSeconds, actual.ScoredSeconds);
            Eq(expected.OverlapOver500, actual.OverlapOver500);
            Eq(expected.OverlapWorstMaxUs, actual.OverlapWorstMaxUs);
            Eq(expected.OverlapCpuMask, actual.OverlapCpuMask);
            Eq(expected.Collisions, actual.Collisions);
            Eq(expected.StructuralConflict, actual.StructuralConflict);
            Eq(expected.Worth, actual.Worth);
        }

        private static void TestIrqShortObservationDisplay()
        {
            foreach (int duration in new[] { 1, 30, 59 })
            {
                var all = new List<IrqSessionRecord> { IrqDisplayRecord(duration) };
                int used, displayed;
                List<IrqDriverVerdict> strict = IrqVerdict.Evaluate(all, 60, out used);
                Eq(0, used);
                Eq(0, strict.Count);
                List<IrqDriverVerdict> raw = IrqVerdict.AggregateForDisplay(all, strict,
                    "1000000", "status-test-topology", out displayed);
                Eq(1, displayed);
                Eq(1, raw.Count);
                Eq(1200.0, raw[0].DpcPerMinute);
                Eq(900.0, raw[0].WorstMaxUs);
                AssertIrqScoreEqual(new IrqDriverVerdict(), raw[0]);
            }

            var shortOnly = new List<IrqSessionRecord>
            { IrqDisplayRecord(1), IrqDisplayRecord(30), IrqDisplayRecord(59) };
            int eligible, shown;
            List<IrqDriverVerdict> noAdvice = IrqVerdict.Evaluate(shortOnly, 60, out eligible);
            Eq(0, eligible);
            List<IrqDriverVerdict> shortRaw = IrqVerdict.AggregateForDisplay(shortOnly, noAdvice,
                "1000000", "status-test-topology", out shown);
            Eq(3, shown);
            Eq(1200.0, shortRaw[0].DpcPerMinute);
            AssertIrqScoreEqual(new IrqDriverVerdict(), shortRaw[0]);

            // 使用既有 Session 工厂的本次 boot 标记，以覆盖真正的 Evaluate 入口；
            // 仅查询时间基准，不采集硬件、设备或 ETW 数据。
            var eligibleMatches = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
            {
                IrqSessionRecord rec = IrqDisplayRecord(120);
                rec.BootStamp = IrqAffinityEngine.BootStamp();
                rec.TopologyStamp = "";
                eligibleMatches.Add(rec);
            }
            List<IrqDriverVerdict> baseline = IrqVerdict.Evaluate(eligibleMatches, 60, out eligible);
            Eq(3, eligible);
            Eq(true, baseline[0].Worth);
            Eq(360.0, baseline[0].ScoredSeconds);
            foreach (int duration in new[] { 1, 20, 30, 59 })
            {
                IrqSessionRecord rec = IrqDisplayRecord(duration);
                rec.BootStamp = eligibleMatches[0].BootStamp;
                rec.TopologyStamp = "";
                rec.Drivers[0].DpcMaxNs = 5000000;
                eligibleMatches.Add(rec);
            }
            List<IrqDriverVerdict> stillStrict = IrqVerdict.Evaluate(eligibleMatches, 60, out eligible);
            Eq(3, eligible);
            AssertIrqScoreEqual(baseline[0], stillStrict[0]);
            List<IrqDriverVerdict> display = IrqVerdict.EvaluateForDisplay(eligibleMatches,
                60, out eligible, out shown);
            Eq(7, shown);
            Eq(3, eligible);
            Eq(5000.0, display[0].WorstMaxUs);
            AssertIrqScoreEqual(baseline[0], display[0]);

            var notEnough = new List<IrqSessionRecord>
            { eligibleMatches[0], eligibleMatches[3], eligibleMatches[4] };
            display = IrqVerdict.EvaluateForDisplay(notEnough, 60, out eligible, out shown);
            Eq(3, shown);
            Eq(1, eligible);
            Eq(1, display[0].SessionsSeen);
            Eq(120.0, display[0].ScoredSeconds);
            Eq(false, display[0].Worth);
        }

        private static void TestIrqDisplayVersionAndValidity()
        {
            const string boot = "1000000", topology = "status-test-topology";
            var strict = new List<IrqDriverVerdict>
            {
                new IrqDriverVerdict
                {
                    Driver = "status-test.sys", DriverVersion = "1.0", SessionsSeen = 3,
                    SessionsOverThreshold = 3, ScoredSeconds = 360, OverlapCpuMask = 1,
                    OverlapOver500 = 3600, OverlapWorstMaxUs = 900,
                    Worth = true, StructuralConflict = true, Collisions = 0.54
                }
            };
            var all = new List<IrqSessionRecord> { IrqDisplayRecord(120), IrqDisplayRecord(120) };
            IrqSessionRecord updated = IrqDisplayRecord(10);
            updated.Drivers[0].Driver = "STATUS-TEST.SYS";
            updated.Drivers[0].DriverVersion = "2.0";
            updated.Drivers[0].Dpc = 20;
            updated.Drivers[0].DpcMaxNs = 100000;
            updated.Drivers[0].Over500Us = 0;
            all.Add(updated);
            int displayed;
            List<IrqDriverVerdict> raw = IrqVerdict.AggregateForDisplay(all, strict,
                boot, topology, out displayed);
            Eq(3, displayed);
            Eq(1, raw.Count);
            Eq("2.0", raw[0].DriverVersion);
            Eq(100.0, raw[0].WorstMaxUs);
            Eq(120.0, raw[0].DpcPerMinute);
            Eq(0L, raw[0].TotalOver500);
            AssertIrqScoreEqual(new IrqDriverVerdict(), raw[0]);
            // 展示聚合不能反向修改由严格入口给出的结论对象。
            Eq(true, strict[0].Worth);
            Eq(3, strict[0].SessionsSeen);

            var guarded = new List<IrqSessionRecord> { IrqDisplayRecord(30) };
            for (int bad = 0; bad < 7; bad++)
            {
                IrqSessionRecord rec = IrqDisplayRecord(30);
                rec.Drivers[0].DpcMaxNs = 9000000;
                rec.Drivers[0].DriverVersion = "untrusted-version";
                if (bad == 0) rec.EventsLost = 1;
                if (bad == 1) rec.BootStamp = "1000010";
                if (bad == 2) rec.TopologyStamp = "different";
                if (bad == 3) rec.DurationSeconds = 0;
                if (bad == 4) rec.DurationSeconds = -1;
                if (bad == 5) rec.SystemMask = 0;
                if (bad == 6) rec.Drivers.Clear();
                guarded.Add(rec);
            }
            raw = IrqVerdict.AggregateForDisplay(guarded, null, boot, topology, out displayed);
            Eq(1, displayed);
            Eq("1.0", raw[0].DriverVersion);
            Eq(900.0, raw[0].WorstMaxUs);
            Eq(1200.0, raw[0].DpcPerMinute);
            AssertIrqScoreEqual(new IrqDriverVerdict(), raw[0]);

            IrqSessionRecord zero = IrqDisplayRecord(0);
            Eq(IrqSessionExclusion.NoDuration, zero.DisplayExclusion(boot, topology));
            Eq(IrqSessionExclusion.TooShort, zero.VerdictExclusion(boot, topology));
            raw = IrqVerdict.AggregateForDisplay(new List<IrqSessionRecord> { zero }, null,
                boot, topology, out displayed);
            Eq(0, displayed);
            Eq(0, raw.Count);

            IrqSessionRecord duplicate = IrqDisplayRecord(10);
            duplicate.Drivers.Add(duplicate.Drivers[0]);
            raw = IrqVerdict.AggregateForDisplay(new List<IrqSessionRecord> { duplicate }, null,
                boot, topology, out displayed);
            Eq(1, displayed);
            Eq(1200.0, raw[0].DpcPerMinute);
            Eq(100L, raw[0].TotalOver500);

            TestIrqDriverVersionPaths();
            TestIrqPlacementEvidence();
        }

        private static void TestIrqDriverVersionPaths()
        {
            const string windows = @"C:\Windows";
            const string store = @"C:\Windows\System32\DriverStore\FileRepository\vendor_a\status-test.sys";
            const string next = @"C:\Windows\System32\DriverStore\FileRepository\vendor_b\status-test.sys";
            const string legacy = @"C:\Windows\System32\drivers\status-test.sys";
            const string direct = @"C:\Windows\System32\status-test.sys";
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { { store, "loaded#2" }, { next, "registered#3" }, { legacy, "stale#1" } };
            int loadedReads = 0, serviceReads = 0, fileReads = 0;
            Func<string, string> versionOf = delegate(string path)
            {
                fileReads++;
                string version;
                return files.TryGetValue(path, out version) ? version : "";
            };
            Func<List<string>> loaded = delegate
            {
                loadedReads++;
                return new List<string>
                {
                    @"\SystemRoot\System32\DriverStore\FileRepository\vendor_a\status-test.sys",
                    store.ToUpperInvariant()
                };
            };
            // The service key deliberately differs from the module basename.
            var servicePaths = new Dictionary<string, string>
            { { "vendor-service-with-another-name", next } };
            Func<List<string>> services = delegate
            {
                serviceReads++;
                return new List<string>(servicePaths.Values);
            };
            Func<string, string> reader = IrqSessionProbe.CreateDriverVersionReader(
                windows, loaded, services, versionOf);
            Eq(0, loadedReads); Eq(0, serviceReads); Eq(0, fileReads);
            Eq("loaded#2", reader("STATUS-TEST.SYS"));
            Eq("loaded#2", reader("status-test.sys"));
            Eq(1, loadedReads); Eq(0, serviceReads); Eq(1, fileReads);
            // One refresh is consistent; the next refresh must not inherit a stale cache.
            files[store] = "updated#4";
            Eq("loaded#2", reader("status-test.sys"));
            Eq("updated#4", IrqSessionProbe.CreateDriverVersionReader(
                windows, loaded, services, versionOf)("status-test.sys"));
            Eq(2, loadedReads); Eq(0, serviceReads);

            Func<List<string>> unavailable = delegate { throw new InvalidOperationException("unavailable"); };
            reader = IrqSessionProbe.CreateDriverVersionReader(windows, unavailable, services, versionOf);
            Eq("registered#3", reader("status-test.sys"));
            Eq("registered#3", reader("STATUS-TEST.SYS"));
            Eq(1, serviceReads);
            // Failure or ambiguity in an authoritative path must not fall back to the stale copy.
            files.Remove(store);
            Func<string, string> missingReader = IrqSessionProbe.CreateDriverVersionReader(
                windows, loaded, services, versionOf);
            Eq("", missingReader("status-test.sys"));
            int readsAfterMissing = fileReads;
            files[store] = "restored#6";
            Eq("", missingReader("STATUS-TEST.SYS"));
            Eq(readsAfterMissing, fileReads);
            Eq("restored#6", IrqSessionProbe.CreateDriverVersionReader(
                windows, loaded, services, versionOf)("status-test.sys"));
            files.Remove(store);
            Func<List<string>> ambiguous = delegate { return new List<string> { store, next }; };
            Eq("", IrqSessionProbe.CreateDriverVersionReader(
                windows, ambiguous, services, versionOf)("status-test.sys"));
            Eq("", IrqSessionProbe.CreateDriverVersionReader(
                windows, null, ambiguous, versionOf)("status-test.sys"));
            Eq("", IrqSessionProbe.CreateDriverVersionReader(windows,
                delegate { return new List<string> { @"\\server\share\status-test.sys" }; },
                services, versionOf)("status-test.sys"));
            Eq("", IrqSessionProbe.CreateDriverVersionReader(windows, loaded, services,
                delegate { throw new UnauthorizedAccessException(); })("status-test.sys"));

            // Preserve conventional paths only when no explicit image identifies the module.
            Eq("stale#1", IrqSessionProbe.CreateDriverVersionReader(
                windows, null, null, versionOf)("status-test.sys"));
            files[direct] = "other#9";
            Eq("", IrqSessionProbe.CreateDriverVersionReader(
                windows, null, null, versionOf)("status-test.sys"));
            files.Remove(legacy);
            Eq("other#9", IrqSessionProbe.CreateDriverVersionReader(
                windows, null, null, versionOf)("status-test.sys"));
            Eq("", IrqSessionProbe.CreateDriverVersionReader(
                "", null, null, versionOf)("status-test.sys"));
            Eq("", IrqSessionProbe.CreateDriverVersionReader(
                windows, loaded, services, null)("status-test.sys"));
            int callsBeforeInvalid = loadedReads + serviceReads + fileReads;
            foreach (string invalid in new[] { null, "", ".", "..", @"..\status-test.sys",
                @"C:\status-test.sys", "status-test.sys:stream", "status*.sys" })
                Eq("", reader(invalid));
            Eq(callsBeforeInvalid, loadedReads + serviceReads + fileReads);

            foreach (string image in new[]
            {
                @"\SystemRoot\System32\drivers\status-test.sys",
                @"%SYSTEMROOT%\System32\drivers\status-test.sys",
                @"%WinDir%\System32\drivers\status-test.sys",
                @"System32\drivers\status-test.sys",
                @"\??\C:\Windows\System32\drivers\status-test.sys",
                @"\\?\C:\Windows\System32\drivers\status-test.sys",
                "\"C:\\Windows\\System32\\drivers\\status-test.sys\"",
                @"C:/Windows/System32/drivers/../drivers/status-test.sys"
            })
                Eq(legacy, IrqSessionProbe.NormalizeDriverImagePath(image, windows));
            foreach (string invalid in new[]
            {
                "", @"status-test.sys", @"C:status-test.sys", @"\\server\share\status-test.sys",
                "\"C:\\Windows\\System32\\drivers\\status-test.sys",
                @"C:\Windows\System32\drivers\status-test.sys:stream",
                @"C:\Windows\System32\drivers\status*.sys",
                @"%PAVISE_TEST_UNRESOLVED_DRIVER_ROOT%\status-test.sys"
            })
                Eq("", IrqSessionProbe.NormalizeDriverImagePath(invalid, windows));

            // Exercise both real consumers with an ImagePath-backed reader, preserving strict versions.
            IrqSessionRecord record = IrqStatusRecord();
            record.Drivers[0].DriverVersion = "registered#3";
            var device = new IrqDevice { Service = "status-test" };
            reader = IrqSessionProbe.CreateDriverVersionReader(windows, null, services, versionOf);
            IrqPinSession session = IrqPinSession.FromLatest(new[] { record }, device,
                "1000000", "status-test-topology", reader);
            Eq(true, session.Available); Eq(false, session.Driver == null); Eq(1UL, session.SeenMask);
            int displayed;
            List<IrqDriverVerdict> verdicts = IrqVerdict.AggregateForDisplay(
                new List<IrqSessionRecord> { record }, null,
                "1000000", "status-test-topology", out displayed);
            IrqDeviceInventory.AttachVerdicts(new List<IrqDevice> { device }, verdicts, reader);
            Eq(true, device.Verdict.VersionVerified);
            files[next] = "changed#5";
            reader = IrqSessionProbe.CreateDriverVersionReader(windows, null, services, versionOf);
            session = IrqPinSession.FromLatest(new[] { record }, device,
                "1000000", "status-test-topology", reader);
            Eq(true, session.Available); Eq(true, session.Driver == null); Eq(0UL, session.SeenMask);
            IrqDeviceInventory.VerifyCurrentVersions(verdicts, reader);
            Eq(false, device.Verdict.VersionVerified);
        }

        private static void TestIrqPlacementEvidence()
        {
            var device = new IrqDevice
            { Service = "status-test", Policy = 4, Mask = 5, RebootState = IrqRebootState.AwaitingReboot };
            var devices = new List<IrqDevice> { device };
            var records = new List<IrqSessionRecord> { IrqDisplayRecord(120) };
            int displayed;
            List<IrqDriverVerdict> verdicts = IrqVerdict.AggregateForDisplay(records, null,
                "1000000", "status-test-topology", out displayed);
            Func<string, string> currentVersion = delegate { return "1.0"; };
            IrqDeviceInventory.AttachVerdicts(devices, verdicts, currentVersion);
            // A target expanded from CPU0 to CPU0+2 contains old observations but has not taken effect.
            Eq(1UL, device.SeenOnCpus);
            Eq(false, device.Effective); Eq(true, device.AwaitingReboot);
            Eq(false, device.PlacementMismatch); Eq(false, device.Unverified);
            device.SeenOnCpus = 8;
            Eq(false, device.Effective); Eq(true, device.AwaitingReboot);
            Eq(false, device.PlacementMismatch);

            device.RebootedSincePin = true;
            device.SeenOnCpus = 1;
            Eq(true, device.Effective); Eq(false, device.AwaitingReboot);
            Eq(false, device.PlacementMismatch); Eq(false, device.Unverified);
            device.SeenOnCpus = 8;
            Eq(false, device.Effective); Eq(true, device.PlacementMismatch);
            Eq(false, device.Unverified);
            for (int invalid = 0; invalid < 5; invalid++)
            {
                device.SeenOnCpus = invalid == 0 ? 0UL : 1UL;
                device.SharedStats = invalid == 1;
                device.Verdict = invalid == 2 ? null : verdicts[0];
                verdicts[0].VersionVerified = invalid != 3;
                verdicts[0].MaskTruncated = invalid == 4;
                Eq(false, device.Effective); Eq(false, device.PlacementMismatch);
                Eq(true, device.Unverified);
                device.SeenOnCpus = invalid == 0 ? 0UL : 8UL;
                Eq(false, device.PlacementMismatch);
            }

            // After reboot, previous-boot records cannot serve as placement proof.
            device = new IrqDevice
            { Service = "status-test", Policy = 4, Mask = 5, RebootedSincePin = true };
            devices = new List<IrqDevice> { device };
            verdicts = IrqVerdict.AggregateForDisplay(records, null,
                "2000000", "status-test-topology", out displayed);
            Eq(0, displayed);
            IrqDeviceInventory.AttachVerdicts(devices, verdicts, currentVersion);
            Eq(false, device.Effective); Eq(true, device.Unverified);
            IrqSessionRecord fresh = IrqDisplayRecord(120);
            fresh.BootStamp = "2000000";
            records.Add(fresh);
            verdicts = IrqVerdict.AggregateForDisplay(records, null,
                "2000000", "status-test-topology", out displayed);
            Eq(1, displayed);
            IrqDeviceInventory.AttachVerdicts(devices, verdicts, currentVersion);
            Eq(true, device.Effective); Eq(false, device.Unverified);

            TestIrqBootWriteEvidence();
        }

        private static void TestIrqBootWriteEvidence()
        {
            const string current = "1000000";
            foreach (string invalid in new[]
            { null, "", "garbage", "0", "-1", "-9223372036854775808", "9223372036854775807" })
            {
                Eq(false, IrqAffinityEngine.RebootedSinceStamp(invalid, current));
                Eq(false, IrqAffinityEngine.RebootedSinceStamp(current, invalid));
                Eq(IrqRebootState.Unknown, IrqAffinityEngine.RebootStateFromStamps(invalid, current));
                Eq(IrqRebootState.Unknown, IrqAffinityEngine.RebootStateFromStamps(current, invalid));
            }
            Eq(false, IrqAffinityEngine.RebootedSinceStamp(current, current));
            Eq(false, IrqAffinityEngine.RebootedSinceStamp("999995", current));
            Eq(true, IrqAffinityEngine.RebootedSinceStamp("999994", current));
            Eq(false, IrqAffinityEngine.RebootedSinceStamp("1000006", current));
            Eq(true, IrqAffinityEngine.RebootedSinceStamp("900000", current));
            Eq(IrqRebootState.AwaitingReboot, IrqAffinityEngine.RebootStateFromStamps(current, current));
            Eq(IrqRebootState.AwaitingReboot, IrqAffinityEngine.RebootStateFromStamps("999995", current));
            Eq(IrqRebootState.AwaitingReboot, IrqAffinityEngine.RebootStateFromStamps("1000005", current));
            Eq(IrqRebootState.Rebooted, IrqAffinityEngine.RebootStateFromStamps("999994", current));
            Eq(IrqRebootState.Unknown, IrqAffinityEngine.RebootStateFromStamps("1000006", current));

            string persisted = "900000";
            int writes = 0;
            Func<bool> apply = delegate
            {
                // The production helper must confirm the current marker before invoking any write.
                Eq(current, persisted);
                writes++;
                return true;
            };
            Func<string, bool> save = delegate(string stamp) { persisted = stamp; return true; };
            Func<string> load = delegate { return persisted; };
            bool attempted;

            // Enable's already-at-target branch preserves both external pins (no marker)
            // and verified old writes, without touching the marker or device callbacks.
            int markerReads = 0, markerWrites = 0;
            foreach (string original in new[] { "", "900000" })
            {
                persisted = original;
                Eq(true, IrqAffinityEngine.ApplyWithBootStamp(true, current,
                    delegate(string stamp) { markerWrites++; persisted = stamp; return true; },
                    delegate { markerReads++; return persisted; }, apply, out attempted));
                Eq(false, attempted); Eq(0, writes);
                Eq(0, markerReads); Eq(0, markerWrites); Eq(original, persisted);
            }

            // Failed saves may retain a valid old marker. No new affinity write is permitted.
            Eq(false, IrqAffinityEngine.ApplyWithBootStamp(false, current,
                delegate { return false; }, load, apply, out attempted));
            Eq(false, attempted); Eq(0, writes); Eq("900000", persisted);
            Eq(false, IrqAffinityEngine.ApplyWithBootStamp(false, current,
                delegate { throw new UnauthorizedAccessException(); }, load, apply, out attempted));
            Eq(false, attempted); Eq(0, writes); Eq("900000", persisted);
            // A reported success without matching read-back is equally insufficient.
            Eq(false, IrqAffinityEngine.ApplyWithBootStamp(false, current,
                delegate { return true; }, load, apply, out attempted));
            Eq(false, attempted); Eq(0, writes); Eq("900000", persisted);
            Eq(false, IrqAffinityEngine.ApplyWithBootStamp(false, current, save,
                delegate { throw new InvalidOperationException("read failed"); }, apply, out attempted));
            Eq(false, attempted); Eq(0, writes);
            Eq(false, IrqAffinityEngine.ApplyWithBootStamp(false, current, save,
                delegate { return ""; }, apply, out attempted));
            Eq(false, attempted); Eq(0, writes);
            Eq(false, IrqAffinityEngine.ApplyWithBootStamp(false, "", save, load, apply, out attempted));
            Eq(false, attempted); Eq(0, writes);

            Eq(true, IrqAffinityEngine.ApplyWithBootStamp(false, current, save, load, apply, out attempted));
            Eq(true, attempted); Eq(1, writes);
            Eq(false, IrqAffinityEngine.RebootedSinceStamp(persisted, current));
            Eq(true, IrqAffinityEngine.RebootedSinceStamp(persisted, "2000000"));
            Eq(false, IrqAffinityEngine.ApplyWithBootStamp(false, current, save, load,
                delegate { writes++; return false; }, out attempted));
            // Only an actual attempted write should enter Enable's rollback branch.
            Eq(true, attempted); Eq(2, writes);

            var device = new IrqDevice
            {
                Policy = 4, Mask = 5, SeenOnCpus = 1,
                Verdict = new IrqDriverVerdict { VersionVerified = true }
            };
            foreach (string unknown in new[] { "", "garbage", "1000006" })
            {
                device.RebootState = IrqAffinityEngine.RebootStateFromStamps(unknown, current);
                Eq(false, device.Effective); Eq(false, device.AwaitingReboot);
                Eq(true, device.Unverified); Eq(false, device.PlacementMismatch);
            }
            device.RebootState = IrqAffinityEngine.RebootStateFromStamps(current, current);
            Eq(true, device.AwaitingReboot); Eq(false, device.Unverified);
            device.RebootState = IrqAffinityEngine.RebootStateFromStamps("900000", current);
            Eq(true, device.Effective); Eq(false, device.AwaitingReboot);
        }
    }
}
