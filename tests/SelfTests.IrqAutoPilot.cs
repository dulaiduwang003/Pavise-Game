// Pure planning/verification logic only. No registry, ETW or devices are
// touched; the affinity engine write paths are exercised by the app itself.
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int irqAutoChecks;

        internal static int RunIrqAutoPilotRegressionTests()
        {
            Action[] tests =
            {
                IrqAutoTargetMaskAvoidsGameCores,
                IrqAutoPlanCodecRoundtripsAndRejectsDamage,
                IrqAutoVerifyOutcomes,
                IrqAutoCandidateSelection,
                IrqAutoPlanPruneKeepsLiveEntries,
                IrqAutoFuseListKeepsOrder,
                IrqAutoObservationBudget
            };
            irqAutoChecks = 0;
            foreach (Action test in tests)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Lang.Cur = 0;
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS irq-autopilot assertions=" + irqAutoChecks
                + " registry=untouched settings=transient windows_shown=false");
            return tests.Length;
        }

        private static void AutoCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("IRQ autopilot regression: " + message);
            Interlocked.Increment(ref irqAutoChecks);
        }

        private static void IrqAutoTargetMaskAvoidsGameCores()
        {
            AutoCheck(IrqAutoPilot.TargetMask(0x0F, 0xFF, 0xFF) == 0xF0,
                "target must be the measured system cores minus the game cores");
            AutoCheck(IrqAutoPilot.TargetMask(0, 0xFF, 0xFF) == 0,
                "no proven game mask means no plan");
            AutoCheck(IrqAutoPilot.TargetMask(0x0F, 0, 0xFF) == 0,
                "no proven system mask means no plan");
            AutoCheck(IrqAutoPilot.TargetMask(0xF1, 0x0F, 0xFF) == 0,
                "a game mask outside the system mask is broken evidence");
            AutoCheck(IrqAutoPilot.TargetMask(0x0F, 0xFF, 0x0F) == 0,
                "an empty remainder after topology clipping must not pin anywhere");
        }

        private static void IrqAutoPlanCodecRoundtripsAndRejectsDamage()
        {
            var plan = new List<IrqAutoPilot.PlanEntry>
            {
                new IrqAutoPilot.PlanEntry { DeviceId = @"PCI\VEN_10DE\0001", Driver = "nvlddmkm.sys",
                    DriverVersion = "31.0.15.5222", BaselinePerMin = 12.5, State = 'P' },
                new IrqAutoPilot.PlanEntry { DeviceId = @"PCI\VEN_8086\0002", Driver = "驱动|奇怪名字",
                    DriverVersion = "", BaselinePerMin = 0, State = 'A' }
            };
            List<IrqAutoPilot.PlanEntry> parsed =
                IrqAutoPilot.ParsePlan(IrqAutoPilot.EncodePlan(plan));
            AutoCheck(parsed.Count == 2, "roundtrip must keep both entries");
            AutoCheck(parsed[0].DeviceId == plan[0].DeviceId && parsed[0].Driver == plan[0].Driver
                && parsed[0].DriverVersion == plan[0].DriverVersion && parsed[0].State == 'P'
                && Math.Abs(parsed[0].BaselinePerMin - 12.5) < 0.001,
                "roundtrip must preserve every field");
            AutoCheck(parsed[1].Driver == plan[1].Driver && parsed[1].State == 'A',
                "separator characters in driver names must survive the codec");
            AutoCheck(IrqAutoPilot.ParsePlan("1|id|garbage-not-b64|x|0|P").Count == 0,
                "a damaged line must discard the whole plan rather than half-load it");
            AutoCheck(IrqAutoPilot.ParsePlan("2|id|" + Convert.ToBase64String(new byte[0])
                    + "|" + Convert.ToBase64String(new byte[0]) + "|0|P").Count == 0,
                "an unknown version tag must not be guessed at");
        }

        private static IrqDriverVerdict AutoVerdict(string driver, string version, bool worth)
        {
            return new IrqDriverVerdict { Driver = driver, DriverVersion = version, Worth = worth };
        }

        private static void IrqAutoVerifyOutcomes()
        {
            int enough = IrqSessionLedger.MinSessionsForVerdict;
            var entry = new IrqAutoPilot.PlanEntry
            { DeviceId = "id", Driver = "bad.sys", DriverVersion = "1.0", State = 'V' };
            var verdicts = new List<IrqDriverVerdict> { AutoVerdict("bad.sys", "1.0", true) };

            var pending = new IrqAutoPilot.PlanEntry
            { DeviceId = "id", Driver = "bad.sys", DriverVersion = "1.0", State = 'P' };
            AutoCheck(IrqAutoPilot.VerifyEntry(pending, verdicts, enough)
                == IrqAutoPilot.VerifyOutcome.KeepWaiting,
                "an entry still awaiting the restart must not be judged");
            AutoCheck(IrqAutoPilot.VerifyEntry(entry, verdicts, enough - 1)
                == IrqAutoPilot.VerifyOutcome.KeepWaiting,
                "too few post-restart sessions must not be judged");
            AutoCheck(IrqAutoPilot.VerifyEntry(entry, verdicts, enough)
                == IrqAutoPilot.VerifyOutcome.RevertFuse,
                "a driver still crossing the line after the pin must revert and fuse");
            AutoCheck(IrqAutoPilot.VerifyEntry(entry,
                    new List<IrqDriverVerdict> { AutoVerdict("bad.sys", "1.0", false) }, enough)
                == IrqAutoPilot.VerifyOutcome.Accept,
                "a driver no longer judged worth moving means the conflict is gone");
            AutoCheck(IrqAutoPilot.VerifyEntry(entry, new List<IrqDriverVerdict>(), enough)
                == IrqAutoPilot.VerifyOutcome.Accept,
                "a driver with no interrupt record at all means the conflict is gone");
            AutoCheck(IrqAutoPilot.VerifyEntry(entry,
                    new List<IrqDriverVerdict> { AutoVerdict("bad.sys", "2.0", true) }, enough)
                == IrqAutoPilot.VerifyOutcome.RevertNoFuse,
                "an updated driver voids the old evidence: revert without blaming the machine");
        }

        private static IrqDevice AutoDevice(string id, string driver, string version,
            double collisions)
        {
            return new IrqDevice
            {
                InstanceId = id,
                Name = id,
                Service = driver,
                Verdict = new IrqDriverVerdict
                {
                    Driver = driver, DriverVersion = version,
                    Worth = true, VersionVerified = true, Collisions = collisions
                }
            };
        }

        private static void IrqAutoCandidateSelection()
        {
            var plan = new List<IrqAutoPilot.PlanEntry>
            {
                new IrqAutoPilot.PlanEntry { DeviceId = "planned", Driver = "p.sys", State = 'P' },
                new IrqAutoPilot.PlanEntry { DeviceId = "reverted", Driver = "r.sys", State = 'R' }
            };
            var fused = new HashSet<string>(StringComparer.Ordinal)
            { IrqAutoPilot.FuseTag("fused.sys", "1.0") };

            IrqDevice pinned = AutoDevice("pinned", "a.sys", "1", 9);
            pinned.Policy = 4; pinned.Mask = 0x3;
            IrqDevice input = AutoDevice("input", "b.sys", "1", 9);
            input.InputRisk = true;
            IrqDevice nic = AutoDevice("nic", "c.sys", "1", 9);
            nic.MultiMessageRisk = true;
            IrqDevice storage = AutoDevice("st", "d.sys", "1", 9);
            storage.CompletionFollowsIssuer = true;
            IrqDevice fusedDev = AutoDevice("fdev", "fused.sys", "1.0", 9);
            IrqDevice planned = AutoDevice("planned", "p.sys", "1", 9);
            IrqDevice display = AutoDevice("gpu", "nvl.sys", "1", 9);
            display.ClassGuid = "{4D36E968-E325-11CE-BFC1-08002BE10318}";
            IrqDevice audio = AutoDevice("hda", "hdaud.sys", "1", 9);
            audio.Bus = "HDAUDIO";
            var devices = new List<IrqDevice>
            {
                pinned, input, nic, storage, fusedDev, planned, display, audio,
                AutoDevice("low", "l.sys", "1", 1),
                AutoDevice("high", "h.sys", "1", 8),
                AutoDevice("mid", "m.sys", "1", 5),
                AutoDevice("tiny", "t.sys", "1", 0.5),
                AutoDevice("reverted", "r.sys", "1", 3)
            };

            List<IrqDevice> picked = IrqAutoPilot.SelectCandidates(devices, plan, fused,
                IrqAutoPilot.MaxDevicesPerPlan);
            AutoCheck(picked.Count == IrqAutoPilot.MaxDevicesPerPlan,
                "a plan must be capped at " + IrqAutoPilot.MaxDevicesPerPlan + " devices");
            AutoCheck(picked[0].InstanceId == "high" && picked[1].InstanceId == "mid"
                && picked[2].InstanceId == "reverted",
                "candidates must rank by measured collisions, and a reverted un-fused device may retry");
            foreach (IrqDevice d in picked)
                AutoCheck(d.InstanceId != "pinned" && d.InstanceId != "input"
                    && d.InstanceId != "nic" && d.InstanceId != "st"
                    && d.InstanceId != "fdev" && d.InstanceId != "planned"
                    && d.InstanceId != "gpu" && d.InstanceId != "hda",
                    "excluded device leaked into the plan: " + d.InstanceId);
            // 显卡音频的排除是自动路径专属判定 大小写不敏感 空设备按排除处理
            AutoCheck(IrqAutoPilot.AutoPathExcluded(display)
                && IrqAutoPilot.AutoPathExcluded(audio)
                && IrqAutoPilot.AutoPathExcluded(null),
                "display/audio/null must be excluded from the automatic path");
            IrqDevice media = AutoDevice("med", "m.sys", "1", 1);
            media.ClassGuid = "{4d36e96c-e325-11ce-bfc1-08002be10318}";
            AutoCheck(IrqAutoPilot.AutoPathExcluded(media),
                "media-class devices must be excluded from the automatic path");
            AutoCheck(!IrqAutoPilot.AutoPathExcluded(AutoDevice("ok", "o.sys", "1", 1)),
                "an ordinary device must not be excluded");
            // 在途配额吃掉名额时只补差额 满了就一台都不添
            AutoCheck(IrqAutoPilot.SelectCandidates(devices, plan, fused, 1).Count == 1,
                "a partially spent in-flight budget must shrink the pick");
            AutoCheck(IrqAutoPilot.SelectCandidates(devices, plan, fused, 0).Count == 0,
                "a spent in-flight budget must pick nothing");
        }

        private static void IrqAutoPlanPruneKeepsLiveEntries()
        {
            var plan = new List<IrqAutoPilot.PlanEntry>();
            for (int i = 0; i < IrqAutoPilot.PlanCompletedKeepLimit + 6; i++)
                plan.Add(new IrqAutoPilot.PlanEntry
                { DeviceId = "done" + i, Driver = "d.sys", State = i % 2 == 0 ? 'A' : 'R' });
            plan.Insert(3, new IrqAutoPilot.PlanEntry { DeviceId = "live-p", Driver = "p.sys", State = 'P' });
            plan.Insert(9, new IrqAutoPilot.PlanEntry { DeviceId = "live-v", Driver = "v.sys", State = 'V' });
            AutoCheck(IrqAutoPilot.PruneCompleted(plan), "an over-limit ledger must prune");
            int completed = 0;
            bool liveP = false, liveV = false, oldestGone = true;
            foreach (IrqAutoPilot.PlanEntry entry in plan)
            {
                if (entry.State == 'A' || entry.State == 'R') completed++;
                if (entry.DeviceId == "live-p") liveP = true;
                if (entry.DeviceId == "live-v") liveV = true;
                if (entry.DeviceId == "done0") oldestGone = false;
            }
            AutoCheck(completed == IrqAutoPilot.PlanCompletedKeepLimit,
                "completed entries must be trimmed to the keep limit");
            AutoCheck(liveP && liveV, "pending and verifying entries must never be pruned");
            AutoCheck(oldestGone, "pruning must drop the oldest completed entries first");
            AutoCheck(!IrqAutoPilot.PruneCompleted(plan), "an in-limit ledger must not prune");
        }

        private static void IrqAutoObservationBudget()
        {
            int window = IrqSessionLedger.VerdictWindow;
            // 有待验收的钉核 永远全量观测
            AutoCheck(IrqObservationBudget.ShouldObserve(window + 3, true, 0),
                "pending verification must force full observation");
            // 证据没攒够 永远全量观测
            AutoCheck(IrqObservationBudget.ShouldObserve(window - 1, false, 99),
                "insufficient evidence must force full observation");
            // 饱和后按预算 跳满 N-1 局观测第 N 局
            for (int skips = 0; skips < IrqObservationBudget.ObserveEveryN - 1; skips++)
                AutoCheck(!IrqObservationBudget.ShouldObserve(window, false, skips),
                    "a saturated ledger must skip until the budget lands: skips=" + skips);
            AutoCheck(IrqObservationBudget.ShouldObserve(window, false,
                    IrqObservationBudget.ObserveEveryN - 1),
                "the budgeted match must observe");

            // 局末记账 短局不动计数 观测名额不被闪退秒退烧掉
            Settings.SaveStr(IrqObservationBudget.SkipCountKey, "0");
            IrqObservationBudget.CommitSession(false, IrqSessionRecord.MinUsableSeconds - 1);
            AutoCheck(Settings.LoadStr(IrqObservationBudget.SkipCountKey, "0") == "0",
                "a short skipped session must not advance the counter");
            IrqObservationBudget.CommitSession(false, IrqSessionRecord.MinUsableSeconds);
            AutoCheck(Settings.LoadStr(IrqObservationBudget.SkipCountKey, "0") == "1",
                "a real skipped match must advance the counter");
            IrqObservationBudget.CommitSession(true, IrqSessionRecord.MinUsableSeconds - 1);
            AutoCheck(Settings.LoadStr(IrqObservationBudget.SkipCountKey, "0") == "1",
                "a short observed blip must not burn the budget slot");
            IrqObservationBudget.CommitSession(true, IrqSessionRecord.MinUsableSeconds);
            AutoCheck(Settings.LoadStr(IrqObservationBudget.SkipCountKey, "0") == "0",
                "a real observed match must reset the counter");
            Settings.SaveStr(IrqObservationBudget.SkipCountKey, "");

            // 待验收判定 P/V 是活账 A/R 不算
            var plan = new List<IrqAutoPilot.PlanEntry>
            {
                new IrqAutoPilot.PlanEntry { DeviceId = "a", Driver = "a.sys", State = 'A' },
                new IrqAutoPilot.PlanEntry { DeviceId = "r", Driver = "r.sys", State = 'R' }
            };
            Settings.SaveStr(IrqAutoPilot.PlanKey, IrqAutoPilot.EncodePlan(plan));
            AutoCheck(!IrqAutoPilot.HasPendingVerification(),
                "completed entries are not pending verification");
            plan.Add(new IrqAutoPilot.PlanEntry { DeviceId = "p", Driver = "p.sys", State = 'P' });
            Settings.SaveStr(IrqAutoPilot.PlanKey, IrqAutoPilot.EncodePlan(plan));
            AutoCheck(IrqAutoPilot.HasPendingVerification(),
                "a pin awaiting restart is pending verification");
            plan[2].State = 'V';
            Settings.SaveStr(IrqAutoPilot.PlanKey, IrqAutoPilot.EncodePlan(plan));
            AutoCheck(IrqAutoPilot.HasPendingVerification(),
                "a pin under verification is pending verification");
            Settings.SaveStr(IrqAutoPilot.PlanKey, "");
        }

        private static void IrqAutoFuseListKeepsOrder()
        {
            Settings.SaveStr("IrqAutoFuseV1", "a|1\nb|2\nc|3");
            List<string> fuses = IrqAutoPilot.LoadFuseList();
            AutoCheck(fuses.Count == 3 && fuses[0] == "a|1" && fuses[2] == "c|3",
                "the fuse ledger must keep insertion order so FIFO trimming drops the oldest");
            AutoCheck(IrqAutoPilot.LoadFuses().Contains("b|2"),
                "the set view must contain every ledger line");
            Settings.SaveStr("IrqAutoFuseV1", "");
        }
    }
}
#endif
