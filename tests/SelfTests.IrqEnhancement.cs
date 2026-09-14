using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestIrqEnhancedCapturePath()
        {
            DriverInterrupt driver = InterruptAttribution.SyntheticCoreEvents();
            Eq(3L,driver.Dpc); Eq(5UL,driver.CpuMask); Eq(2,driver.Cores.Count);
            var cpu0 = driver.Cores.Find(delegate(IrqDriverCoreRecord c) { return c.Cpu == 0; });
            var cpu2 = driver.Cores.Find(delegate(IrqDriverCoreRecord c) { return c.Cpu == 2; });
            Eq(2L,cpu0.Count); Eq(1L,cpu0.Over500Us); Eq(700000L,cpu0.TotalNs);
            Eq(600000L,cpu0.MaxNs); Eq(0L,cpu0.BadDuration);
            Eq(1L,cpu2.Count); Eq(1L,cpu2.Over1Ms); Eq(1200000L,cpu2.TotalNs);
            // Seal -> attach frame evidence -> commit preserves one observation identity
            string deviceConfig = "one";
            var platform = new ObservationTestPlatform { DeviceSnapshot = delegate { return new Dictionary<string,string> { { "fixture",deviceConfig } }; } };
            using (var probe = new IrqSessionProbe(platform))
            {
                probe.Arm("fixture",15); probe.SetContext("stable-id","frozen-config");
                Eq(true,probe.ConfirmGameMask(3,200,300));
                platform.Now += TimeSpan.TicksPerSecond; Eq(true,probe.ConfirmGameMask(3,200,300));
                probe.Seal(); bool truncated; probe.TakeDpcTimeline(out truncated,false); Eq(0,platform.AppendCalls);
                var evidence = new IrqFrameEvidence { Intervals = 60,Seconds = 1,P99Ms = 20,P999Ms = 22 };
                probe.SetFrameEvidence(evidence); probe.TakeSummary(); Eq(1,platform.AppendCalls);
                Eq("stable-id",platform.Records[0].GameId); Eq("frozen-config",platform.Records[0].Configuration);
                Eq(evidence,platform.Records[0].Frames);
                Eq("one",platform.Records[0].DeviceConfigurations["fixture"]);
                probe.Arm("next",15); Eq(true,probe.ConfirmGameMask(3,201,301));
                platform.Now += TimeSpan.TicksPerSecond; Eq(true,probe.ConfirmGameMask(3,201,301));
                deviceConfig = "changed";
                probe.TakeSummary(); Eq(0,platform.Records[1].DeviceConfigurations.Count);
                Eq("",platform.Records[1].GameId); Eq(true,platform.Records[1].Frames == null);
            }
            var frames = new List<PresentFrame>();
            for (int i = 0; i <= 120; i++) frames.Add(new PresentFrame { Pid = 77,Qpc = 1000 + i * 10 });
            IrqFrameEvidence captured;
            Eq(0,PresentDpcAlignment.BuildLongFrameIntervals(frames,1000,77,1,out captured).Count);
            Eq(false,captured.IdentityReliable); Eq(120,captured.Intervals); Eq(10.0,captured.P99Ms);
        }

        internal static IrqSessionRecord EnhancedIrqRecord(long start, string boot, bool slowOnGame)
        {
            var r = new IrqSessionRecord { StartUtcTicks = start, DurationSeconds = 120,
                GameName = "Synthetic match", GameId = "stable-game", Configuration = "config-v1",
                Scene = "map-A/high/1080p", TopologyStamp = CpuTopology.TopologyStamp(), BootStamp = boot,
                SystemMask = 15, GameMask = 3, CoreLoadWindowTicks = 120 * TimeSpan.TicksPerSecond };
            var d = new IrqDriverRecord { Driver = "fixture.sys", DriverVersion = "v1", CpuMask = 5 };
            foreach (int cpu in new[] { 0, 2 })
            {
                bool slow = slowOnGame ? cpu == 0 : cpu == 2;
                var core = new IrqDriverCoreRecord { Cpu = cpu, Count = 100,
                    TotalNs = slow ? 60000000 : 100000, MaxNs = slow ? 600000 : 1000,
                    Over500Us = slow ? 100 : 0, Buckets = new long[13] };
                core.Buckets[slow ? 9 : 1] = 100;
                d.Cores.Add(core); d.Dpc += core.Count; d.DpcTotalNs += core.TotalNs;
                d.DpcMaxNs = Math.Max(d.DpcMaxNs,core.MaxNs); d.Over500Us += core.Over500Us;
            }
            d.Buckets = new long[13]; d.Buckets[9] = d.Buckets[1] = 100;
            r.Drivers.Add(d);
            for (int cpu = 0; cpu < 4; cpu++) r.CoreLoads.Add(new IrqCoreLoadRecord { Cpu = cpu,
                AveragePercent = 20 + cpu, ObservedTicks = r.CoreLoadWindowTicks, Samples = 60, BusyTicks = 0 });
            r.Frames = new IrqFrameEvidence { Intervals = 10000, LongFrames = 20, Seconds = 115,
                P99Ms = 22, P999Ms = 35, IdentityReliable = true };
            r.DeviceConfigurations["fixture-device"] = "4:4:-1:package-v1";
            return r;
        }

        private static void TestIrqEnhancedCoreEvidence()
        {
            var devices = new List<IrqDevice>();
            foreach (string id in new[] { "A","B","C" })
                IrqDeviceInventory.AddDevice(id,"PCI",devices,delegate(string value,string bus) {
                    if (value == "B") throw new UnauthorizedAccessException();
                    return new IrqDevice { InstanceId = value }; });
            Eq(2,devices.Count); Eq("C",devices[1].InstanceId);
            string boot = IrqAffinityEngine.BootStamp();
            var records = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++) records.Add(EnhancedIrqRecord(i + 1,boot,true));
            int used; var verdict = IrqVerdict.Evaluate(records,60,out used)[0];
            Eq(true,verdict.Worth); Eq(300L,verdict.OverlapOver500); Eq(1UL,verdict.OverlapCpuMask);
            Eq(600.0,verdict.OverlapWorstMaxUs); Eq(360.0,verdict.ScoredSeconds);
            records.Clear();
            for (int i = 0; i < 3; i++) records.Add(EnhancedIrqRecord(i + 1,boot,false));
            verdict = IrqVerdict.Evaluate(records,60,out used)[0];
            Eq(false,verdict.Worth); Eq(0L,verdict.OverlapOver500); Eq(1.0,verdict.OverlapWorstMaxUs);
            // One bad session cannot complete the multi-session threshold
            records[0] = EnhancedIrqRecord(1,boot,true); records[1] = EnhancedIrqRecord(2,boot,true);
            Eq(false,IrqVerdict.Evaluate(records,60,out used)[0].Worth);
            var detailed = records[0].Drivers[0]; Eq(true,detailed.ValidCores(15));
            detailed.Cores[0].Count++; Eq(false,detailed.ValidCores(15)); detailed.Cores[0].Count--;
            detailed.MaskTruncated = true; Eq(false,detailed.ValidCores(15)); detailed.MaskTruncated = false;
            detailed.Cores[0].BadDuration = 1; Eq(false,detailed.ValidCores(15)); detailed.Cores[0].BadDuration = 0;
            detailed.Cores.Add(detailed.Cores[0]); Eq(false,detailed.ValidCores(15)); detailed.Cores.RemoveAt(2);
            Eq(false,detailed.ValidCores(3));
            detailed.DpcTotalNs++; Eq(false,detailed.ValidCores(15)); detailed.DpcTotalNs--;
            var legacy = EnhancedIrqRecord(3,boot,true); legacy.Drivers[0].Cores.Clear();
            Eq(false,legacy.Drivers[0].ValidCores(15));
            var device = new IrqDevice { InstanceId = "fixture-device",Service = "fixture" };
            var view = IrqPinSession.FromRecord(records[0],device,boot,CpuTopology.TopologyStamp(),delegate { return "v1"; });
            string options = IrqCoreRecommendation.Describe(view,device,new ulong[] { 3,12 },false);
            Eq(true,options.Contains("2-3"));
            var candidates = IrqCoreRecommendation.Candidates(view,device,new ulong[] { 3,12 },false);
            Eq(1,candidates.Count); Eq(12UL,candidates[0].Mask); Eq(true,candidates[0].Seen);
            Eq(23.0,candidates[0].AveragePercent); Eq(0.0,candidates[0].BusyPercent);
            // One game thread excludes its entire physical core including the other sibling
            view.GameMask = 1;
            Eq(12UL,IrqCoreRecommendation.Candidates(view,device,new ulong[] { 3,12 },false)[0].Mask);
            view.GameMask = 3;
            // Equal scores remain deterministic when topology enumeration order changes
            records[0].CoreLoads[2].AveragePercent = 24;
            candidates = IrqCoreRecommendation.Candidates(view,device,new ulong[] { 8,4,3 },false);
            Eq(2,candidates.Count); Eq(4UL,candidates[0].Mask); Eq(true,candidates[0].Seen);
            Eq(8UL,candidates[1].Mask); Eq(false,candidates[1].Seen);
            Eq(candidates[0].Mask,IrqCoreRecommendation.Candidates(view,device,new ulong[] { 3,4,8 },false)[0].Mask);
            records[0].CoreLoads[2].AveragePercent = 22;
            Eq(0,IrqCoreRecommendation.Candidates(null,device,new ulong[] { 3,12 },false).Count);
            Eq(0,IrqCoreRecommendation.Candidates(new IrqPinSession(),device,new ulong[] { 3,12 },false).Count);
            Eq(0,IrqCoreRecommendation.Candidates(view,device,new ulong[] { 3,12 },true).Count);
            device.SharedStats = true;
            Eq(0,IrqCoreRecommendation.Candidates(view,device,new ulong[] { 3,12 },false).Count);
            device.SharedStats = false; device.FrameworkStats = true;
            Eq(0,IrqCoreRecommendation.Candidates(view,device,new ulong[] { 3,12 },false).Count);
            device.FrameworkStats = false;
            records[0].CoreLoads[3].ObservedTicks = records[0].CoreLoadWindowTicks * 79 / 100;
            Eq(0,IrqCoreRecommendation.Candidates(view,device,new ulong[] { 3,12 },false).Count);
            records[0].CoreLoads[3].ObservedTicks = records[0].CoreLoadWindowTicks;
            records[0].CoreLoads[3].AveragePercent = double.NaN;
            Eq(0,IrqCoreRecommendation.Candidates(view,device,new ulong[] { 3,12 },false).Count);
            records[0].CoreLoads[3].AveragePercent = 23;
            records[0].CoreLoads[3].BusyTicks = -1;
            Eq(0,IrqCoreRecommendation.Candidates(view,device,new ulong[] { 3,12 },false).Count);
            Eq(Lang.T("irq.candidate.none") + "\n" + Lang.T("irq.candidate.caution"),
                IrqCoreRecommendation.Describe(view,device,new ulong[] { 3,12 },false));
            Eq(Lang.T("irq.exact.unsupported"),IrqCoreRecommendation.Describe(view,device,new ulong[] { 3,12 },true));
            // A single visible device does not make framework attribution unique
            device = new IrqDevice { Service = "nic",ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}" };
            IrqDeviceInventory.AttachVerdicts(new List<IrqDevice> { device },new List<IrqDriverVerdict> {
                new IrqDriverVerdict { Driver = "ndis.sys",DriverVersion = "v1",Worth = true,DpcPerMinute = 100,CpuMask = 1 } },delegate { return "v1"; });
            Eq(true,device.SharedStats); Eq(true,device.FrameworkStats); Eq(false,device.ActionableWorth);
            device.Policy = 4; device.Mask = 1; device.RebootedSincePin = true; Eq(false,device.Effective);
        }

        private static void TestIrqEnhancedTransactions()
        {
            Eq(false,IrqAffinityEngine.SupportsExactMask(true,1,15));
            Eq(false,IrqAffinityEngine.SupportsExactMask(false,16,15));
            Eq(false,IrqAffinityEngine.SupportsExactMask(false,0,15));
            Eq(true,IrqAffinityEngine.SupportsExactMask(false,8,15));
            Eq(true,IrqAffinityEngine.SupportsExactMask(false,1UL << 63,ulong.MaxValue));
            int applies = 0, restores = 0; bool attempted; string saved = "";
            var result = new IrqWriteResult();
            Func<bool> write = delegate { applies++; return false; };
            Func<bool> restore = delegate { restores++; return false; };
            Eq(false,IrqAffinityEngine.ApplyTarget(result,false,"1000000",delegate { return false; },
                delegate { return saved; },write,restore,out attempted));
            Eq(false,attempted); Eq(0,applies); Eq(0,restores); Eq(1,result.Failed);
            Eq(false,IrqAffinityEngine.ApplyTarget(result,false,"1000000",delegate(string value) { saved = value; return true; },
                delegate { return saved; },write,restore,out attempted));
            Eq(true,attempted); Eq(1,applies); Eq(1,restores); Eq(1,result.RollbackFailed);
            Eq(false,IrqAffinityEngine.ApplyTarget(result,false,"1000000",delegate { return true; },
                delegate { return saved; },delegate { throw new IOException(); },delegate { return true; },out attempted));
            Eq(1,result.RolledBack);
            Eq(true,IrqAffinityEngine.ApplyTarget(result,true,"bad",null,null,write,restore,out attempted));
            Eq(false,attempted); Eq(1,result.Succeeded); Eq(1,applies);
            var change = IrqChangeResult.Run(delegate { return true; },delegate { return false; },false);
            Eq(true,change.Affinity); Eq(false,change.Priority); Eq(false,change.Success);
            int priority = 0;
            change = IrqChangeResult.Run(delegate { throw new IOException(); },delegate { priority++; return true; },true);
            Eq(1,priority); Eq(false,change.Affinity); Eq(true,change.Priority);
            change = IrqChangeResult.Run(delegate { return false; },delegate { priority++; return true; },false);
            Eq(1,priority); Eq(false,change.Success);
            string raw = IrqPriorityTweak.EncodeJournal(new List<KeyValuePair<string,string>> {
                new KeyValuePair<string,string>("A","-"),new KeyValuePair<string,string>("B","2") });
            string remaining; int restoreCalls = 0;
            Eq(true,IrqPriorityTweak.RestoreJournal(raw,"a",delegate(string id,string value) {
                restoreCalls++; Eq("A",id); Eq("-",value); return true; },out remaining));
            Eq(1,restoreCalls); Eq(1,IrqPriorityTweak.DecodeJournal(remaining).Count);
            Eq("B",IrqPriorityTweak.DecodeJournal(remaining)[0].Key);
            Eq(false,IrqPriorityTweak.RestoreJournal(raw,"A",delegate { throw new IOException(); },out remaining)); Eq(raw,remaining);
            Eq(false,IrqPriorityTweak.RestoreJournal(raw + ";bad",null,delegate { restoreCalls++; return true; },out remaining));
            Eq(1,restoreCalls); Eq(raw + ";bad",remaining);
        }

        private static void TestIrqEnhancedPersistence(string parent)
        {
            string work = Path.Combine(parent,"irq-enhanced-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work); IrqSessionLedger.Bind(work);
            var r = EnhancedIrqRecord(10,"1000000",true);
            Eq(true,IrqSessionLedger.Append(r)); string issue;
            var read = IrqSessionLedger.Load(out issue); Eq("",issue); Eq(1,read.Count);
            Eq(true,read[0].Drivers[0].ValidCores(15)); Eq("stable-game",read[0].GameId);
            Eq(0L,read[0].CoreLoads[0].BusyTicks); Eq(35.0,read[0].Frames.P999Ms);
            Eq("4:4:-1:package-v1",read[0].DeviceConfigurations["fixture-device"]);
            Eq(r.Scene,read[0].Scene); // Preserve legacy metadata when reading old sessions
            string path = Path.Combine(work,IrqSessionLedger.FileName), intact = File.ReadAllText(path);
            foreach (string broken in new[] { intact + "B|0|1\r\n",intact + "K|broken\r\n",intact.Replace("B|0|0","B|0|-2") })
            {
                File.WriteAllText(path,broken,new UTF8Encoding(false)); Eq(0,IrqSessionLedger.Load(out issue).Count);
                Eq(false,IrqSessionLedger.Append(r)); Eq(broken,File.ReadAllText(path));
            }
            File.WriteAllText(path,intact,new UTF8Encoding(false));
            var device = new IrqDevice { InstanceId = "fixture-device",Service = "fixture",DriverVersion = "package-v1",Policy = 4,Mask = 4 };
            r.Scene = ""; // New sessions need no manual annotation to provide a baseline
            var view = IrqPinSession.FromRecord(r,device,"1000000",r.TopologyStamp,delegate { return "v1"; });
            var adjustment = IrqAdjustmentLedger.Prepare(device,view,4,false,new[] { r });
            Eq(1,adjustment.Baseline.Count); Eq(true,IrqAdjustmentLedger.Save(adjustment));
            adjustment.AffinityResult = "ok"; adjustment.PriorityResult = "failed";
            adjustment.RollbackResult = "skipped"; Eq(true,IrqAdjustmentLedger.Save(adjustment));
            var changes = IrqAdjustmentLedger.Load(out issue); Eq("",issue); Eq(1,changes.Count);
            Eq("ok",changes[0].AffinityResult); Eq("failed",changes[0].PriorityResult);
            Eq(adjustment.Id,changes[0].Id); Eq(1,changes[0].Baseline.Count); Eq(22.0,changes[0].Baseline[0].P99);
            // Independent baseline survives eviction from the observation ledger
            for (int i = 0; i < 13; i++) Eq(true,IrqSessionLedger.Append(EnhancedIrqRecord(i + 20,"1000000",true)));
            Eq(12,IrqSessionLedger.Load().Count); Eq(1,IrqAdjustmentLedger.Load(out issue)[0].Baseline.Count);
            string adjustmentPath = Path.Combine(work,"irq-adjustment-" + adjustment.Id + ".xml");
            File.WriteAllText(adjustmentPath,"<future/>",new UTF8Encoding(false));
            Eq(0,IrqAdjustmentLedger.Load(out issue).Count); Eq(false,string.IsNullOrEmpty(issue));
            Eq("<future/>",File.ReadAllText(adjustmentPath));
            IrqSessionLedger.Bind(parent);
        }

        private static void TestIrqEnhancedComparison()
        {
            var a = new IrqAdjustment { Device = "fixture-device",Driver = "fixture.sys",DriverVersion = "v1",
                DeviceVersion = "package-v1",GameId = "stable-game",Configuration = "config-v1",Scene = "map-A/high/1080p",
                Topology = CpuTopology.TopologyStamp(),Boot = "1000000",Target = 4,GameMask = 3,
                Phase = "written",WrittenUtc = 1000,Before = "4:4:-1:package-v1" };
            var after = new List<IrqSessionRecord>(); string reason;
            for (int i = 0; i < 3; i++)
            {
                var b = EnhancedIrqRecord(i + 1,"1000000",true);
                a.Baseline.Add(IrqAdjustmentLedger.Sample(a,b,out reason));
                var n = EnhancedIrqRecord(i + 2000,"2000000",false);
                n.Frames.P99Ms = 15; n.Frames.P999Ms = 25; n.Frames.LongFrames = 2; after.Add(n);
            }
            string outcome; string text = IrqAdjustmentVerification.Compare(a,after,out outcome); Eq("improved",outcome);
            foreach (var n in after) n.Frames.IdentityReliable = false;
            text = IrqAdjustmentVerification.Compare(a,after,out outcome); Eq("insufficient",outcome);
            Eq(true,text.Contains(Lang.T("irq.compare.reason.stream")));
            foreach (var n in after) { n.Frames.IdentityReliable = true; n.Frames.P99Ms = 23; }
            IrqAdjustmentVerification.Compare(a,after,out outcome); Eq("unchanged",outcome);
            var sample = after[0]; sample.Scene = "";
            Eq(true,IrqAdjustmentLedger.Sample(a,sample,out reason) != null); Eq("",reason);
            a.Scene = ""; sample.Scene = "legacy-scene";
            Eq(true,IrqAdjustmentLedger.Sample(a,sample,out reason) != null); Eq("",reason);
            sample.GameId = "other"; Eq(true,IrqAdjustmentLedger.Sample(a,sample,out reason) == null); Eq("game",reason); sample.GameId = a.GameId;
            sample.Configuration = "changed"; Eq(true,IrqAdjustmentLedger.Sample(a,sample,out reason) == null); Eq("config",reason); sample.Configuration = a.Configuration;
            sample.Drivers[0].DriverVersion = "v2"; Eq(true,IrqAdjustmentLedger.Sample(a,sample,out reason) == null); Eq("driver",reason); sample.Drivers[0].DriverVersion = "v1";
            sample.DeviceConfigurations[a.Device] = "4:4:2:package-v1";
            Eq(true,IrqAdjustmentLedger.Sample(a,sample,out reason) == null); Eq("deviceconfig",reason);
            sample.DeviceConfigurations.Clear(); Eq(true,IrqAdjustmentLedger.Sample(a,sample,out reason) == null); Eq("deviceconfig",reason);
            sample.DeviceConfigurations[a.Device] = "4:4:-1:package-v1";
            var device = new IrqDevice { InstanceId = a.Device,Policy = 4,Mask = 4,DriverVersion = a.DeviceVersion };
            Eq("reboot",IrqAdjustmentVerification.Placement(a,device,after,"1000000",a.Topology));
            Eq("pending",IrqAdjustmentVerification.Placement(a,device,new List<IrqSessionRecord>(),"2000000",a.Topology));
            Eq("mismatch",IrqAdjustmentVerification.Placement(a,device,after,"2000000",a.Topology));
            a.PriorityRequested = true; a.PriorityResult = "failed"; a.Phase = "partial";
            Eq("mismatch",IrqAdjustmentVerification.Placement(a,device,after,"2000000",a.Topology));
            a.PriorityRequested = false; a.Phase = "written";
            foreach (var n in after)
            {
                var d = n.Drivers[0]; d.Cores[0].Cpu = 2; d.Cores[1].Cpu = 2; // duplicate core map must not prove anything
            }
            Eq("pending",IrqAdjustmentVerification.Placement(a,device,after,"2000000",a.Topology));
            foreach (var n in after)
            {
                var d = n.Drivers[0]; d.Cores[0].Cpu = 2; d.Cores[1].Cpu = 3; d.CpuMask = 12;
                n.DeviceConfigurations[a.Device] = "4:C:-1:package-v1";
            }
            a.Target = 12; device.Mask = 12;
            Eq("matches",IrqAdjustmentVerification.Placement(a,device,after,"2000000",a.Topology));
            Eq("pending",IrqAdjustmentVerification.Placement(a,device,after,"3000000",a.Topology));
            device.FrameworkStats = true; Eq("pending",IrqAdjustmentVerification.Placement(a,device,after,"2000000",a.Topology));
            device.FrameworkStats = false; device.Mask = 1; Eq("config",IrqAdjustmentVerification.Placement(a,device,after,"2000000",a.Topology));
        }
    }

    // Feed production parsing and folding with in-memory ETW-shaped payloads no trace is started
    internal sealed partial class InterruptAttribution
    {
        internal static DriverInterrupt SyntheticCoreEvents()
        {
            var probe = new InterruptAttribution { usPerTick = 1,sanityMaxTicks = 1000000 };
            probe.modules.Add(new Module { Base = 1000,End = 2000,Name = "fixture.sys" });
            IntPtr payload = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
            try
            {
                int[] durations = { 600,100,1200 }; ushort[] cpus = { 0,0,2 };
                for (int i = 0; i < durations.Length; i++)
                {
                    System.Runtime.InteropServices.Marshal.WriteInt64(payload,10000);
                    System.Runtime.InteropServices.Marshal.WriteInt64(payload,8,1000);
                    var record = new EventRecord { UserData = payload,UserDataLength = 16,
                        EventHeader = new EventHeader { ProviderId = PerfInfoGuid,Flags = HeaderFlag64Bit,
                            TimeStamp = 10000 + durations[i],EventDescriptor = new EventDescriptor { Opcode = 66 } },
                        BufferContext = new EtwBufferContext { ProcessorIndex = cpus[i] } };
                    probe.OnEvent(ref record);
                }
                var drivers = new Dictionary<string,DriverInterrupt>(); var buckets = new Dictionary<string,long[]>();
                probe.Fold(drivers,buckets,probe.dpcHits,true);
                return drivers["fixture.sys"];
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(payload); }
        }
    }
}
