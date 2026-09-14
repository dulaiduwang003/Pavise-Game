using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static IrqPinSession PlanView(IrqSessionRecord record, IrqDevice device)
        {
            return IrqPinSession.FromRecord(record,device,record.BootStamp,record.TopologyStamp,delegate { return "v1"; });
        }

        private static void TestIrqCorePlan()
        {
            var device = new IrqDevice { InstanceId = "fixture-device",Service = "fixture",DriverVersion = "package-v1",
                Policy = 4,Mask = 4,ConfigurationKnown = true };
            ulong[] physical = { 3,12 };
            var first = EnhancedIrqRecord(100,"1000000",true);
            var second = EnhancedIrqRecord(200,"1000000",true);
            var latest = EnhancedIrqRecord(300,"1000000",true);
            first.CoreLoads[2].AveragePercent = 55;
            var selected = PlanView(latest,device);
            var history = new List<IrqPinSession> { PlanView(second,device),selected,PlanView(first,device) };
            var plan = IrqCorePlan.Build(selected,history,device,physical,false);
            Eq(3,plan.Sessions); Eq(0,plan.Excluded); Eq(true,plan.Stable); Eq("stable",plan.Reason);
            Eq(1,plan.Options.Count); Eq(12UL,plan.Options[0].Mask); Eq(3,plan.Options[0].EvidenceSessions);
            Eq(55.0,plan.Options[0].AveragePercent); Eq(50.0,plan.GameSlowPerMinute);
            Eq(30.0,plan.GameDpcMsPerMinute); Eq(true,plan.GameDpcShare > 99 && plan.GameDpcShare < 100);
            Eq(22.0,latest.CoreLoads[2].AveragePercent); Eq(4UL,device.Mask);
            Eq(true,plan.Details.Contains(Lang.T("irq.plan.caution")));
            var ranked = IrqCorePlan.Build(selected,history,device,new ulong[] { 4,3,8 },false);
            Eq(2,ranked.Options.Count); Eq(8UL,ranked.Options[0].Mask); Eq(4UL,ranked.Options[1].Mask);

            // A cool most recent match must not conceal busy or missing older evidence
            first.CoreLoads[2].BusyTicks = first.CoreLoadWindowTicks * 21 / 100;
            plan = IrqCorePlan.Build(selected,history,device,physical,false);
            Eq(3,plan.Sessions); Eq(0,plan.Options.Count); Eq("none",plan.Reason);
            first.CoreLoads[2].BusyTicks = 0;
            first.CoreLoads[3].ObservedTicks = first.CoreLoadWindowTicks * 79 / 100;
            Eq(0,IrqCorePlan.Build(selected,history,device,physical,false).Options.Count);
            first.CoreLoads[3].ObservedTicks = first.CoreLoadWindowTicks;
            first.CoreLoads[2].AveragePercent = 61;
            Eq(0,IrqCorePlan.Build(selected,history,device,physical,false).Options.Count);
            first.CoreLoads[2].AveragePercent = 55;

            // Scope mismatches never strengthen or poison the plan of a selected game
            Action<IrqSessionRecord>[] changes = {
                delegate(IrqSessionRecord r) { r.GameId = "other-game"; },
                delegate(IrqSessionRecord r) { r.Configuration = "other-policy"; },
                delegate(IrqSessionRecord r) { r.GameMask = 12; },
                delegate(IrqSessionRecord r) { r.SystemMask = 31; },
                delegate(IrqSessionRecord r) { r.TopologyStamp += "-different"; },
                delegate(IrqSessionRecord r) { r.BootStamp = "2000000"; },
                delegate(IrqSessionRecord r) { r.Drivers[0].DriverVersion = "v2"; },
                delegate(IrqSessionRecord r) { r.DeviceConfigurations[device.InstanceId] = "4:8:-1:package-v1"; },
                delegate(IrqSessionRecord r) { r.DurationSeconds = 59; },
                delegate(IrqSessionRecord r) { r.EventsLost = 1; },
                delegate(IrqSessionRecord r) { r.Unmapped = 1; },
                delegate(IrqSessionRecord r) { r.Drivers[0].Cores[0].BadDuration = 1; }
            };
            foreach (var change in changes)
            {
                var other = EnhancedIrqRecord(100,"1000000",true); change(other);
                plan = IrqCorePlan.Build(selected,new[] { selected,PlanView(other,device) },device,physical,false);
                Eq(1,plan.Sessions); Eq(1,plan.Excluded); Eq(false,plan.Stable); Eq(1,plan.Options.Count);
            }
            var earlier = PlanView(first,device);
            plan = IrqCorePlan.Build(selected,new[] { selected,earlier,earlier,selected },device,physical,false);
            Eq(2,plan.Sessions); Eq(false,plan.Stable);
            plan = IrqCorePlan.Build(earlier,history,device,physical,false);
            Eq(1,plan.Sessions); Eq(0,plan.Excluded); // No future matches in a historical view
            earlier.CurrentBoot = false;
            Eq(true,IrqCorePlan.Build(earlier,history,device,physical,false).Historical);
            var many = new List<IrqPinSession>();
            for (int i = 1; i <= 10; i++) many.Add(PlanView(EnhancedIrqRecord(i,"1000000",true),device));
            Eq(5,IrqCorePlan.Build(selected,many,device,physical,false).Sessions);

            // Recommendations are gated while reading evidence never writes settings
            Eq("groups",IrqCorePlan.Build(selected,history,device,physical,true).Reason);
            device.ManagedElsewhere = true;
            Eq("owned",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            Eq(0,IrqCoreRecommendation.Candidates(selected,device,physical,false).Count);
            device.ManagedElsewhere = false; device.SharedStats = true;
            Eq("shared",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            device.SharedStats = false; device.FrameworkStats = true;
            Eq("shared",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            device.FrameworkStats = false; device.CompletionFollowsIssuer = true;
            Eq("storage",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            Eq(0,IrqCoreRecommendation.Candidates(selected,device,physical,false).Count);
            device.CompletionFollowsIssuer = false; device.MultiMessageRisk = true;
            Eq("queues",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            Eq(0,IrqCoreRecommendation.Candidates(selected,device,physical,false).Count);
            device.MultiMessageRisk = false; device.ConfigurationKnown = false;
            Eq("configuration",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            device.ConfigurationKnown = true; device.Mask = 8;
            Eq("changed",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            device.Mask = 4; latest.GameId = "";
            Eq("missing",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            latest.GameId = "stable-game"; latest.DeviceConfigurations.Clear();
            Eq("changed",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            latest.DeviceConfigurations[device.InstanceId] = "4:4:-1:package-v1";
            var driver = selected.Driver;
            selected.Driver = EnhancedIrqRecord(500,"1000000",true).Drivers[0];
            Eq("missing",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            selected.Driver = driver;
            selected.GameMask = latest.GameMask = latest.SystemMask;
            Eq(0,IrqCoreRecommendation.Candidates(selected,device,physical,false).Count);
            Eq("missing",IrqCorePlan.Build(selected,history,device,physical,false).Reason);
            Eq(4UL,device.Mask); Eq(4,device.Policy);
        }
    }
}
