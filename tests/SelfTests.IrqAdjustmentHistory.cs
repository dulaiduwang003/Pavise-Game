using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestIrqAdjustmentHistory(string parent)
        {
            var original = new IrqAdjustment { Device = "fixture-device", Driver = "fixture.sys",
                DriverVersion = "v1", DeviceVersion = "package-v1", Phase = "written", Target = 12,
                WrittenUtc = 1000, Boot = "1000000", Topology = CpuTopology.TopologyStamp() };
            original.Baseline.Add(new IrqComparisonSample { Start = 1, Seconds = 120, SlowPerMinute = 50 });
            var device = new IrqDevice { InstanceId = original.Device, Policy = 4, Mask = 12,
                DriverVersion = original.DeviceVersion };
            var session = EnhancedIrqRecord(2000,"2000000",false);
            session.Drivers[0].Cores[0].Cpu = 2; session.Drivers[0].Cores[1].Cpu = 3;
            session.Drivers[0].CpuMask = 12;
            session.DeviceConfigurations[original.Device] = "4:C:-1:package-v1";
            var sessions = new[] { session };
            Eq("matches",IrqAdjustmentVerification.Placement(original,device,sessions,"2000000",original.Topology));

            var noOp = new IrqAdjustment { Device = "FIXTURE-DEVICE", Target = 12, WrittenUtc = 3000 };
            var noOpWrite = new IrqWriteResult { Succeeded = 1, Unchanged = 1 };
            IrqAdjustmentHistory.RecordWrite(noOp,IrqChangeResult.Run(delegate { return true; },null,false),noOpWrite,false);
            Eq("unchanged",noOp.Phase); Eq(true,noOp.NoDeviceWrite);
            var records = new List<IrqAdjustment> { original,noOp };
            Eq(original,IrqAdjustmentHistory.Current(records,device.InstanceId));
            Eq(noOp,IrqAdjustmentHistory.Latest(records,device.InstanceId));
            IrqAdjustment current;
            string text = IrqAdjustmentVerification.DescribeHistory(records,device,sessions,"2000000",original.Topology,out current);
            Eq(original,current); Eq("matches",current.Placement);
            Eq(true,text.Contains(Lang.F("irq.adjust.latest",Lang.T("irq.phase.unchanged"))));
            Eq(true,text.Contains(Lang.T("irq.adjust.current")));
            Eq(1000L,current.WrittenUtc); Eq("1000000",current.Boot); Eq(1,current.Baseline.Count);
            records.Add(new IrqAdjustment { Device = "unrelated", Phase = "prepared" });
            records.Add(new IrqAdjustment { Device = device.InstanceId, Phase = "unchanged" });
            Eq(original,IrqAdjustmentHistory.Current(records,device.InstanceId));

            // A rejected write is transparent only when the actual write path completed
            // and explicitly reports that no device write or rollback was attempted
            var rejected = new IrqAdjustment { Device = device.InstanceId, Target = 12, WrittenUtc = 4000 };
            int priorityCalls = 0;
            var rejection = IrqChangeResult.Run(delegate { return false; },delegate { priorityCalls++; return true; },false);
            IrqAdjustmentHistory.RecordWrite(rejected,rejection,new IrqWriteResult { Failed = 1 },false);
            Eq(0,priorityCalls); Eq("skipped",rejected.PriorityResult); Eq(true,rejected.NoDeviceWrite);
            records.Add(rejected);
            Eq(original,IrqAdjustmentHistory.Current(records,device.InstanceId));
            Eq("matches",IrqAdjustmentVerification.Placement(IrqAdjustmentHistory.Current(records,device.InstanceId),
                device,sessions,"2000000",original.Topology));

            bool attempted;
            var rolledBack = new IrqWriteResult();
            Eq(false,IrqAffinityEngine.ApplyTarget(rolledBack,false,"1000000",delegate { return true; },
                delegate { return "1000000"; },delegate { return false; },delegate { return true; },out attempted));
            Eq(true,attempted); Eq(1,rolledBack.Attempted);
            IrqAdjustmentHistory.RecordWrite(rejected,rejection,rolledBack,false);
            Eq(false,rejected.NoDeviceWrite); Eq("ok",rejected.RollbackResult);
            Eq(rejected,IrqAdjustmentHistory.Current(records,device.InstanceId));
            Eq("unknown",IrqAdjustmentVerification.Placement(rejected,device,sessions,"2000000",original.Topology));
            rolledBack.RollbackFailed = 1;
            IrqAdjustmentHistory.RecordWrite(rejected,rejection,rolledBack,false);
            Eq(false,rejected.NoDeviceWrite); Eq("failed",rejected.RollbackResult);

            var thrown = IrqChangeResult.Run(delegate { throw new IOException(); },null,false);
            IrqAdjustmentHistory.RecordWrite(rejected,thrown,new IrqWriteResult(),false);
            Eq(false,rejected.NoDeviceWrite);
            // Actual priority readback not the pre-dialog UI snapshot determines a no-op
            var priorityOk = IrqChangeResult.Run(delegate { return true; },delegate { return true; },false);
            IrqAdjustmentHistory.RecordWrite(noOp,priorityOk,noOpWrite,true);
            Eq("unchanged",noOp.Phase);
            IrqAdjustmentHistory.RecordWrite(noOp,priorityOk,noOpWrite,false);
            Eq("written",noOp.Phase); Eq(false,noOp.NoDeviceWrite);
            IrqAdjustmentHistory.RecordWrite(noOp,IrqChangeResult.Run(delegate { return true; },delegate { return false; },false),noOpWrite,false);
            Eq("partial",noOp.Phase); Eq(false,noOp.NoDeviceWrite);
            IrqAdjustmentHistory.RecordWrite(noOp,IrqChangeResult.Run(delegate { return true; },null,false),noOpWrite,false);

            foreach (string phase in new[] { "partial","prepared","failed","restorepartial","restored","future" })
            {
                var boundary = new IrqAdjustment { Device = device.InstanceId, Phase = phase };
                Eq(boundary,IrqAdjustmentHistory.Current(new[] { original,boundary,noOp },device.InstanceId));
            }
            Eq(noOp,IrqAdjustmentHistory.Current(new[] { noOp },device.InstanceId));
            Eq("unknown",IrqAdjustmentVerification.Placement(noOp,device,sessions,"2000000",original.Topology));
            // Legacy priority no-ops relied on a stale UI snapshot so require explicit proof
            var legacyPriority = new IrqAdjustment { Device = device.InstanceId, Phase = "unchanged", PriorityRequested = true };
            Eq(legacyPriority,IrqAdjustmentHistory.Current(new[] { original,legacyPriority },device.InstanceId));
            legacyPriority.NoDeviceWrite = true;
            Eq(original,IrqAdjustmentHistory.Current(new[] { original,legacyPriority },device.InstanceId));
            noOp.RestoredUtc = 5000;
            Eq(noOp,IrqAdjustmentHistory.Current(new[] { original,noOp },device.InstanceId));
            noOp.RestoredUtc = 0;
            device.Mask = 4;
            Eq("config",IrqAdjustmentVerification.Placement(IrqAdjustmentHistory.Current(new[] { original,noOp },device.InstanceId),
                device,sessions,"2000000",original.Topology));
            device.Mask = 12;

            // Both the independent baseline and the explicit no-write evidence survive reload
            string work = Path.Combine(parent,"irq-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work); IrqAdjustmentLedger.Bind(work);
            try
            {
                IrqAdjustmentHistory.RecordWrite(rejected,rejection,new IrqWriteResult { Failed = 1 },false);
                Eq(true,IrqAdjustmentLedger.Save(original)); Eq(true,IrqAdjustmentLedger.Save(noOp));
                Eq(true,IrqAdjustmentLedger.Save(rejected));
                string issue; var loaded = IrqAdjustmentLedger.Load(out issue);
                Eq("",issue); Eq(3,loaded.Count);
                current = IrqAdjustmentHistory.Current(loaded,device.InstanceId);
                Eq(original.Id,current.Id); Eq(1,current.Baseline.Count); Eq(1000L,current.WrittenUtc);
                Eq(true,loaded[2].NoDeviceWrite);
                Eq("matches",IrqAdjustmentVerification.Placement(current,device,sessions,"2000000",original.Topology));
                string path = Path.Combine(work,"irq-adjustment-" + rejected.Id + ".xml");
                File.WriteAllText(path,File.ReadAllText(path).Replace("<noDeviceWrite>1</noDeviceWrite>",""));
                loaded = IrqAdjustmentLedger.Load(out issue); Eq("",issue);
                Eq(false,loaded[2].NoDeviceWrite);
                Eq(rejected.Id,IrqAdjustmentHistory.Current(loaded,device.InstanceId).Id);
            }
            finally { IrqAdjustmentLedger.Bind(parent); }

            TestIrqRestoreHistory();
        }

        private static void TestIrqRestoreHistory()
        {
            var a = new IrqAdjustment { Device = "A", Phase = "written" };
            var b = new IrqAdjustment { Device = "B", Phase = "written" };
            var untouched = new IrqAdjustment { Device = "C", Phase = "written" };
            var closed = new IrqAdjustment { Device = "A", Phase = "restored", RestoredUtc = 10 };
            var records = new[] { a,b,untouched,closed };
            IrqReceiptReader both = delegate(out List<string> ids) { ids = new List<string> { "A","B" }; return true; };
            var result = IrqRestoreResult.Run(null,both,both,null,
                delegate { return true; },delegate(string id) { return id == "A"; });
            Eq(false,result.Success);
            Eq(2,IrqAdjustmentHistory.RecordRestore(records,result,100).Count);
            Eq("restored",a.Phase); Eq("ok",a.RestoreAffinityResult); Eq("ok",a.RestorePriorityResult);
            Eq("restorepartial",b.Phase); Eq("ok",b.RestoreAffinityResult); Eq("failed",b.RestorePriorityResult);
            Eq("written",untouched.Phase); Eq(0L,untouched.RestoredUtc); Eq(10L,closed.RestoredUtc);

            // Only the remaining priority receipt for B is retried A and the original cutoff stay intact
            IrqReceiptReader none = delegate(out List<string> ids) { ids = new List<string>(); return true; };
            IrqReceiptReader remaining = delegate(out List<string> ids) { ids = new List<string> { "b" }; return true; };
            result = IrqRestoreResult.Run(null,none,remaining,null,null,delegate { return true; });
            Eq(1,IrqAdjustmentHistory.RecordRestore(records,result,200).Count);
            Eq("restored",b.Phase); Eq("ok",b.RestoreAffinityResult); Eq("ok",b.RestorePriorityResult);
            Eq(100L,a.RestoredUtc); Eq(100L,b.RestoredUtc);
            Eq(0,IrqAdjustmentHistory.RecordRestore(records,new IrqRestoreResult(),300).Count);
            result = IrqRestoreResult.Run(null,none,none,null,null,null);
            Eq(0,IrqAdjustmentHistory.RecordRestore(records,result,400).Count);

            // Mixed affinity failures are also attributed only to their own device
            a.Phase = b.Phase = "written";
            result = IrqRestoreResult.Run(null,both,none,null,delegate(string id) { return id == "B"; },null);
            Eq(2,IrqAdjustmentHistory.RecordRestore(new[] { a,b },result,500).Count);
            Eq("restorepartial",a.Phase); Eq("failed",a.RestoreAffinityResult);
            Eq("restored",b.Phase); Eq("ok",b.RestoreAffinityResult);
        }
    }
}
