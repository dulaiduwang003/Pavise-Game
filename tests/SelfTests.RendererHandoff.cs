// @author bdth 2074055628@qq.com
// 文件用途 纯渲染交接状态机回归 身份 时钟 GPU 结果都是合成的 不启动进程 不读写游戏库
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunRendererHandoffRegressionTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Lang.Init();
            Action[] checks =
            {
                TestHandoffSingleFlight,
                TestHandoffCanceledWorkerStillOwnsSlot,
                TestHandoffIdentityAndProfileChanges,
                TestHandoffForegroundLease,
                TestHandoffRecoveryPending,
                TestHandoffForceSafetyOnly,
                TestHandoffLongLoadingBackoff,
                TestHandoffRepeatedOfferPreservesBudget,
                TestHandoffProofExpiresAndCanResample,
                TestHandoffStaleProbeResults,
                TestHandoffGpuEvidenceValidation,
                TestHandoffHardSelectionAndCopies
            };
            int failed = 0;
            foreach (Action check in checks)
            {
                try
                {
                    check();
                    Console.WriteLine("PASS " + check.Method.Name);
                }
                catch (Exception error)
                {
                    failed++;
                    Console.Error.WriteLine("FAIL " + check.Method.Name + " " + error);
                }
            }
            Console.WriteLine("RendererHandoffRegression: " + (checks.Length - failed) + "/" + checks.Length);
            return failed == 0 ? 0 : 1;
        }

        private static void TestHandoffSingleFlight()
        {
            GameDetection candidate = HandoffTestCandidate();
            RendererHandoffTracker tracker = HandoffReadyTracker(candidate, 0);
            RendererProbeTicket first = tracker.BeginProbe(0);
            HandoffRequire(first != null && tracker.HasProbe && tracker.Attempts == 1, "first probe was not started");
            HandoffRequire(tracker.BeginProbe(1) == null, "concurrent probe started before cooldown");
            HandoffRequire(tracker.Attempts == 1, "denied probes consumed attempt budget");

            var impostor = new RendererProbeTicket(first.Generation, first.StartedMs, candidate);
            tracker.Complete(impostor, HandoffGpu(candidate, 40), true, 100);
            HandoffRequire(tracker.HasProbe, "unowned ticket released the real worker slot");
            HandoffRequire(tracker.Confirmed(100) == null, "unowned ticket supplied proof");
            tracker.Complete(first, HandoffGpu(candidate, 40), true, 100);
            HandoffRequire(!tracker.HasProbe && tracker.Confirmed(100) != null, "owned completion was not accepted");
            HandoffRequire(tracker.BeginProbe(2000) == null, "fresh proof caused another probe");
            tracker.Complete(first, HandoffGpu(candidate, 0), true, 2000);
            HandoffRequire(tracker.Confirmed(2000) != null, "duplicate completion replaced accepted proof");
            RendererProbeTicket next = tracker.BeginProbe(8000);
            HandoffRequire(next != null && tracker.BeginProbe(16000) == null,
                "concurrent probe started after cooldown while its worker remained active");
            tracker.Complete(next, HandoffGpu(candidate, 40), true, 16010);
            HandoffRequire(!tracker.HasProbe && tracker.Confirmed(16010) == null,
                "overlong worker retained its slot or confirmed stale proof");
        }

        private static void TestHandoffCanceledWorkerStillOwnsSlot()
        {
            GameDetection original = HandoffTestCandidate();
            RendererHandoffTracker tracker = HandoffReadyTracker(original, 0);
            RendererProbeTicket old = tracker.BeginProbe(0);
            tracker.Clear();
            HandoffRequire(old.Canceled && tracker.HasProbe && !tracker.HasCandidate,
                "clear must cancel without pretending the worker exited");
            HandoffRequire(!tracker.Protects(original.RendererPid, original.RendererCreation,
                original.RendererPath, 1), "cleared candidate stayed protected");

            GameDetection replacement = RendererHandoffTracker.Copy(original);
            replacement.RendererCreation++;
            tracker.Offer(replacement, 100);
            tracker.Recovery(replacement, true);
            HandoffRequire(tracker.BeginProbe(9000) == null, "replacement overlapped the canceled worker");
            tracker.Complete(old, HandoffGpu(original, 99), true, 9100);
            HandoffRequire(!tracker.HasProbe && tracker.Confirmed(9100) == null, "canceled result confirmed replacement");
            RendererProbeTicket next = tracker.BeginProbe(9100);
            HandoffRequire(next != null && next.Generation != old.Generation, "replacement did not get a new epoch");
            tracker.Complete(old, HandoffGpu(original, 99), true, 9110);
            HandoffRequire(tracker.HasProbe, "late canceled completion released a newer worker");
            tracker.Complete(next, HandoffGpu(replacement, 30), true, 9120);
            GameDetection confirmed = tracker.Confirmed(9120);
            HandoffRequire(confirmed != null && confirmed.RendererCreation == replacement.RendererCreation,
                "replacement did not confirm after its own worker completed");

            tracker = HandoffReadyTracker(original, 0);
            old = tracker.BeginProbe(0);
            tracker.Clear();
            tracker.Complete(old, null, false, 100);
            tracker.Offer(replacement, 200);
            tracker.Recovery(replacement, true);
            HandoffRequire(tracker.BeginProbe(200) == null && tracker.BeginProbe(7999) == null,
                "clear reset the global probe cooldown");
            HandoffRequire(tracker.BeginProbe(8000) != null, "global cooldown did not reopen at its boundary");
        }

        private static void TestHandoffIdentityAndProfileChanges()
        {
            Action<GameDetection>[] changes =
            {
                delegate(GameDetection hit) { hit.RendererPid++; },
                delegate(GameDetection hit) { hit.RendererCreation++; },
                delegate(GameDetection hit) { hit.RendererPath = @"C:\HandoffUnit\Other\GameRender.exe"; },
                delegate(GameDetection hit) { hit.RendererName = "ChangedImage"; },
                delegate(GameDetection hit) { hit.Profile.Id = "another-profile"; },
                delegate(GameDetection hit) { hit.Profile.Root = @"C:\HandoffUnit\Other"; },
                delegate(GameDetection hit) { hit.Profile.ExecutablePath = @"C:\HandoffUnit\OtherEntry.exe"; },
                delegate(GameDetection hit) { hit.Profile.LearnedExecutablePath = @"C:\HandoffUnit\OldRender.exe"; },
                delegate(GameDetection hit) { hit.Profile.ForceTrigger = true; },
                delegate(GameDetection hit) { hit.RendererSafetyOnly = true; }
            };
            for (int i = 0; i < changes.Length; i++)
            {
                GameDetection original = HandoffTestCandidate();
                RendererHandoffTracker tracker = HandoffReadyTracker(original, 0);
                RendererProbeTicket old = tracker.BeginProbe(0);
                GameDetection changed = RendererHandoffTracker.Copy(original);
                changes[i](changed);
                tracker.Offer(changed, 10);
                HandoffRequire(old.Canceled && tracker.HasProbe && tracker.Attempts == 0,
                    "identity/profile change did not reset epoch or retain worker: " + i);
                tracker.Complete(old, HandoffGpu(original, 40), true, 100);
                HandoffRequire(!tracker.HasProbe && tracker.Confirmed(100) == null,
                    "old result survived identity/profile change: " + i);
                HandoffRequire(tracker.BeginProbe(8000) == null,
                    "replacement inherited recovery readiness: " + i);

                tracker = HandoffReadyTracker(original, 0);
                old = tracker.BeginProbe(0);
                tracker.Complete(old, HandoffGpu(original, 40), true, 100);
                HandoffRequire(tracker.Confirmed(100) != null, "test proof setup failed");
                tracker.Offer(changed, 200);
                tracker.Recovery(changed, true);
                HandoffRequire(tracker.Confirmed(200) == null,
                    "old proof survived identity/profile change: " + i);
            }

            GameDetection candidate = HandoffTestCandidate();
            RendererHandoffTracker invalidated = HandoffReadyTracker(candidate, 0);
            RendererProbeTicket running = invalidated.BeginProbe(0);
            invalidated.Refresh(false, true, 10);
            HandoffRequire(!invalidated.HasCandidate && invalidated.HasProbe && running.Canceled,
                "identity invalidation did not clear protection and cancel the owned worker");
            HandoffRequire(!invalidated.Protects(candidate.RendererPid, candidate.RendererCreation,
                candidate.RendererPath, 10), "invalid identity stayed protected");
            invalidated.Complete(running, HandoffGpu(candidate, 40), true, 100);
            HandoffRequire(!invalidated.HasProbe && invalidated.Confirmed(100) == null,
                "invalidated identity returned through a delayed completion");
        }

        private static void TestHandoffForegroundLease()
        {
            GameDetection candidate = HandoffTestCandidate();
            RendererHandoffTracker tracker = HandoffReadyTracker(candidate, 1000);
            RendererProbeTicket ticket = tracker.BeginProbe(1000);
            tracker.Complete(ticket, HandoffGpu(candidate, 30), true, 1010);
            tracker.Refresh(true, false, 1100);
            HandoffRequire(tracker.Confirmed(1100) == null && tracker.BeginProbe(1100) == null,
                "lost foreground still confirmed or probed");
            HandoffRequire(tracker.Protects(candidate.RendererPid, candidate.RendererCreation,
                candidate.RendererPath.ToUpperInvariant(), 2500), "1.5-second boundary lost its lease early");
            HandoffRequire(!tracker.Protects(candidate.RendererPid + 1, candidate.RendererCreation,
                candidate.RendererPath, 1200), "lease protected another PID");
            HandoffRequire(!tracker.Protects(candidate.RendererPid, candidate.RendererCreation + 1,
                candidate.RendererPath, 1200), "lease protected a reused PID");
            HandoffRequire(!tracker.Protects(candidate.RendererPid, candidate.RendererCreation,
                @"C:\HandoffUnit\Other.exe", 1200), "lease protected another image path");
            tracker.Refresh(true, false, 2500);
            HandoffRequire(tracker.HasCandidate, "lease was cleared at the inclusive boundary");
            tracker.Refresh(true, false, 2501);
            HandoffRequire(!tracker.HasCandidate && !tracker.Protects(candidate.RendererPid,
                candidate.RendererCreation, candidate.RendererPath, 2501), "expired foreground lease stayed alive");

            tracker = HandoffReadyTracker(candidate, 0);
            ticket = tracker.BeginProbe(0);
            tracker.Complete(ticket, HandoffGpu(candidate, 30), true, 10);
            tracker.Refresh(true, false, 500);
            tracker.Refresh(true, true, 1000);
            HandoffRequire(tracker.Protects(candidate.RendererPid, candidate.RendererCreation,
                candidate.RendererPath, 1000), "returning foreground did not renew protection");
            HandoffRequire(tracker.Confirmed(1000) == null, "foreground return reused pre-focus-loss proof");
        }

        private static void TestHandoffRecoveryPending()
        {
            GameDetection candidate = HandoffTestCandidate();
            var tracker = new RendererHandoffTracker();
            tracker.Offer(candidate, 0);
            HandoffRequire(tracker.Protects(candidate.RendererPid, candidate.RendererCreation,
                candidate.RendererPath, 0), "recovery-pending candidate was not safety protected");
            HandoffRequire(tracker.BeginProbe(0) == null && tracker.Confirmed(0) == null && tracker.Attempts == 0,
                "recovery-pending candidate probed or confirmed");
            GameDetection wrongIdentity = RendererHandoffTracker.Copy(candidate);
            wrongIdentity.RendererCreation++;
            tracker.Recovery(wrongIdentity, true);
            HandoffRequire(tracker.BeginProbe(0) == null, "another identity released the recovery gate");
            tracker.Recovery(candidate, true);
            RendererProbeTicket ticket = tracker.BeginProbe(0);
            HandoffRequire(ticket != null, "successful recovery did not permit a probe");
            tracker.Recovery(candidate, false);
            HandoffRequire(ticket.Canceled && tracker.HasProbe && tracker.BeginProbe(100) == null,
                "recovery regression failed to cancel or retained no single-flight barrier");
            tracker.Complete(ticket, HandoffGpu(candidate, 40), true, 100);
            HandoffRequire(tracker.Confirmed(100) == null, "probe result crossed recovery-pending state");
            tracker.Recovery(candidate, true);
            ticket = tracker.BeginProbe(8000);
            HandoffRequire(ticket != null, "recovered candidate could not try again");
            tracker.Complete(ticket, HandoffGpu(candidate, 40), true, 8100);
            HandoffRequire(tracker.Confirmed(8100) != null, "recovered candidate did not confirm fresh proof");
        }

        private static void TestHandoffForceSafetyOnly()
        {
            GameDetection candidate = HandoffTestCandidate();
            candidate.Profile.ForceTrigger = true;
            candidate.RendererSafetyOnly = true;
            candidate.RendererCandidateSelected = true;
            candidate.RendererUserSelected = true;
            RendererHandoffTracker tracker = HandoffReadyTracker(candidate, 0);
            HandoffRequire(tracker.Protects(candidate.RendererPid, candidate.RendererCreation,
                candidate.RendererPath, 100000), "foreground Force safety candidate lost protection");
            HandoffRequire(tracker.BeginProbe(0) == null && tracker.BeginProbe(100000) == null
                && tracker.Confirmed(100000) == null && tracker.Attempts == 0,
                "Force SafetyOnly acquired probe or confirmation capability");
            HandoffRequire(!tracker.TakeUncertainNotice(100000), "SafetyOnly produced a probe-budget notice");
            candidate.RequiresGpuConfirm = false;
            tracker.Offer(candidate, 100000);
            HandoffRequire(tracker.Confirmed(100000) == null,
                "hard-selected flag bypassed SafetyOnly confirmation guard");
            tracker.Refresh(true, false, 100001);
            HandoffRequire(tracker.Protects(candidate.RendererPid, candidate.RendererCreation,
                candidate.RendererPath, 101500), "SafetyOnly lost the bounded foreground grace early");
            tracker.Refresh(true, false, 101501);
            HandoffRequire(!tracker.HasCandidate, "Force SafetyOnly gained an unlimited background lease");
        }

        private static void TestHandoffLongLoadingBackoff()
        {
            GameDetection candidate = HandoffTestCandidate();
            RendererHandoffTracker tracker = HandoffReadyTracker(candidate, 0);
            long[] scheduled = { 0, 8000, 38000, 98000, 218000, 338000 };
            for (int i = 0; i < scheduled.Length; i++)
            {
                long now = scheduled[i];
                if (i > 0)
                {
                    tracker.Offer(RendererHandoffTracker.Copy(candidate), now - 1);
                    tracker.Refresh(true, true, now - 1);
                    HandoffRequire(tracker.BeginProbe(now - 1) == null, "backoff opened one millisecond early: " + i);
                }
                RendererProbeTicket ticket = tracker.BeginProbe(now);
                HandoffRequire(ticket != null && tracker.Attempts == i + 1,
                    "long-loading candidate lost scheduled probe: " + i);
                HandoffRequire(tracker.Protects(candidate.RendererPid, candidate.RendererCreation,
                    candidate.RendererPath, now), "probe budget expiry removed foreground safety protection");
                bool last = i == scheduled.Length - 1;
                tracker.Complete(ticket, HandoffGpu(candidate, last ? 30 : 0), true, now + 20);
                HandoffRequire((tracker.Confirmed(now + 20) != null) == last,
                    "low-GPU sample confirmed, or long-loading success was permanently rejected: " + i);
            }
            HandoffRequire(tracker.Attempts == 6, "two quick attempts incorrectly became a lifetime limit");
        }

        private static void TestHandoffRepeatedOfferPreservesBudget()
        {
            GameDetection candidate = HandoffTestCandidate();
            RendererHandoffTracker tracker = HandoffReadyTracker(candidate, 0);
            RendererProbeTicket first = tracker.BeginProbe(0);
            tracker.Complete(first, HandoffGpu(candidate, 0), true, 10);
            GameDetection same = RendererHandoffTracker.Copy(candidate);
            same.RendererName = same.RendererName.ToUpperInvariant();
            same.RendererPath = same.RendererPath.ToUpperInvariant();
            same.Profile.Root = same.Profile.Root.ToUpperInvariant();
            same.Profile.ExecutablePath = same.Profile.ExecutablePath.ToUpperInvariant();
            same.Profile.Name = "cosmetic rename";
            tracker.Offer(same, 7999);
            HandoffRequire(tracker.Attempts == 1 && tracker.BeginProbe(7999) == null,
                "same identity refreshed the quick-probe budget");
            RendererProbeTicket second = tracker.BeginProbe(8000);
            HandoffRequire(second != null && second.Generation == first.Generation,
                "case-only identity or cosmetic name change reset the epoch");
            tracker.Complete(second, HandoffGpu(candidate, 0), true, 8010);
            tracker.Offer(same, 9000);
            HandoffRequire(!tracker.TakeUncertainNotice(9999) && tracker.TakeUncertainNotice(10000),
                "same identity refreshed first-seen time or notice threshold");
            tracker.Offer(same, 11000);
            HandoffRequire(!tracker.TakeUncertainNotice(11000), "same identity reset the one-shot uncertain notice");
            tracker.Offer(same, 37000);
            HandoffRequire(tracker.Attempts == 2 && tracker.BeginProbe(37999) == null,
                "same identity reset unresolved backoff");
            HandoffRequire(tracker.BeginProbe(38000) != null, "same identity postponed its original retry deadline");

            tracker = HandoffReadyTracker(candidate, 0);
            tracker.Offer(same, 9500);
            first = tracker.BeginProbe(10000);
            HandoffRequire(first != null, "delayed first probe did not start");
            tracker.Complete(first, HandoffGpu(candidate, 0), true, 10010);
            HandoffRequire(tracker.BeginProbe(18000) == null && tracker.BeginProbe(39999) == null,
                "same identity reset the initial 10-second window");
            HandoffRequire(tracker.BeginProbe(40000) != null, "delayed first probe did not get 30-second backoff");
        }

        private static void TestHandoffProofExpiresAndCanResample()
        {
            GameDetection candidate = HandoffTestCandidate();
            RendererHandoffTracker tracker = HandoffReadyTracker(candidate, 0);
            RendererProbeTicket first = tracker.BeginProbe(0);
            tracker.Complete(first, HandoffGpu(candidate, 30), true, 100);
            HandoffRequire(tracker.Confirmed(3100) != null, "proof expired before the inclusive 3-second boundary");
            HandoffRequire(tracker.Confirmed(3101) == null, "proof survived beyond three seconds");
            HandoffRequire(tracker.BeginProbe(7999) == null, "expired proof bypassed probe cooldown");
            RendererProbeTicket next = tracker.BeginProbe(8000);
            HandoffRequire(next != null, "expired proof permanently blocked resampling");
            tracker.Complete(next, HandoffGpu(candidate, 35), true, 8100);
            HandoffRequire(tracker.Confirmed(8100) != null, "fresh sample after proof expiry did not confirm");
            tracker.Refresh(true, true, 11101);
            HandoffRequire(tracker.Confirmed(11101) == null, "refresh did not expire old proof");

            tracker = HandoffReadyTracker(candidate, 0);
            first = tracker.BeginProbe(0);
            tracker.Complete(first, HandoffGpu(candidate, 30), true, 100);
            HandoffRequire(tracker.Confirmed(99) == null && tracker.Confirmed(101) == null,
                "backward time resurrected evidence from the future");
        }

        private static void TestHandoffStaleProbeResults()
        {
            long[] ages = { -1, 5001, 8000 };
            foreach (long age in ages)
            {
                GameDetection candidate = HandoffTestCandidate();
                RendererHandoffTracker tracker = HandoffReadyTracker(candidate, 10000);
                RendererProbeTicket ticket = tracker.BeginProbe(10000);
                tracker.Complete(ticket, HandoffGpu(candidate, 40), true, 10000 + age);
                HandoffRequire(!tracker.HasProbe && tracker.Confirmed(10000 + age) == null,
                    "out-of-order or stale completion was accepted: " + age);
                HandoffRequire(tracker.BeginProbe(18000) != null, "discarded completion leaked the worker slot");
            }
            GameDetection exactCandidate = HandoffTestCandidate();
            RendererHandoffTracker exact = HandoffReadyTracker(exactCandidate, 10000);
            RendererProbeTicket exactTicket = exact.BeginProbe(10000);
            exact.Complete(exactTicket, HandoffGpu(exactCandidate, 40), true, 15000);
            HandoffRequire(exact.Confirmed(15000) != null, "completion at exactly five seconds was rejected");

            RendererHandoffTracker invalid = HandoffReadyTracker(exactCandidate, 0);
            RendererProbeTicket invalidTicket = invalid.BeginProbe(0);
            invalid.Complete(invalidTicket, HandoffGpu(exactCandidate, 40), false, 100);
            HandoffRequire(invalid.Confirmed(100) == null && !invalid.HasProbe,
                "identity-invalid completion supplied proof or leaked its slot");
        }

        private static void TestHandoffGpuEvidenceValidation()
        {
            GameDetection candidate = HandoffTestCandidate();
            double observed;
            HandoffRequire(!RendererHandoffTracker.HasGpuEvidence(null, HandoffGpu(candidate, 30), out observed),
                "null candidate supplied GPU evidence");
            HandoffRequire(!RendererHandoffTracker.HasGpuEvidence(candidate, null, out observed),
                "null GPU collection supplied evidence");
            HandoffRequire(!RendererHandoffTracker.HasGpuEvidence(candidate, new Dictionary<int, double>(), out observed),
                "missing candidate GPU entry supplied evidence");
            double[] rejected = { -1, 0, 9.999, double.NaN, double.PositiveInfinity, double.NegativeInfinity };
            foreach (double value in rejected)
                HandoffRequire(!RendererHandoffTracker.HasGpuEvidence(candidate, HandoffGpu(candidate, value), out observed),
                    "invalid or below-10-percent GPU sample was accepted: " + value);
            HandoffRequire(RendererHandoffTracker.HasGpuEvidence(candidate, HandoffGpu(candidate, 10), out observed)
                && observed == 10, "exact 10-percent evidence was rejected");

            Dictionary<int, double> values = HandoffGpu(candidate, 30);
            values[candidate.RendererPid + 1] = 31;
            HandoffRequire(!RendererHandoffTracker.HasGpuEvidence(candidate, values, out observed),
                "more active family member was ignored");
            values[candidate.RendererPid + 1] = 30;
            HandoffRequire(RendererHandoffTracker.HasGpuEvidence(candidate, values, out observed),
                "equal family utilization was treated as higher");
            values[candidate.RendererPid + 1] = 29;
            values[candidate.RendererPid + 100] = 99;
            HandoffRequire(RendererHandoffTracker.HasGpuEvidence(candidate, values, out observed),
                "GPU activity outside the candidate family incorrectly vetoed confirmation");
        }

        private static void TestHandoffHardSelectionAndCopies()
        {
            GameDetection candidate = HandoffTestCandidate();
            candidate.RendererCandidateSelected = true;
            candidate.RendererUserSelected = true;
            candidate.RendererLearnable = false;
            candidate.RequiresGpuConfirm = false;
            var tracker = new RendererHandoffTracker();
            tracker.Offer(candidate, 0);
            HandoffRequire(tracker.Confirmed(0) == null && tracker.BeginProbe(0) == null,
                "hard selection bypassed pending recovery");
            tracker.Recovery(candidate, true);
            GameDetection confirmed = tracker.Confirmed(0);
            HandoffRequire(confirmed != null && confirmed.RendererCandidateSelected && !confirmed.RequiresGpuConfirm
                && !tracker.HasProbe && tracker.Attempts == 0, "recovered hard selection unnecessarily waited for a probe");
            confirmed.Profile.ExecutablePath = @"C:\HandoffUnit\Tampered.exe";
            confirmed.FamilyPids.Clear();
            GameDetection view = tracker.Current;
            HandoffRequire(view.Profile.ExecutablePath == candidate.Profile.ExecutablePath && view.FamilyPids.Count == 2,
                "confirmed result leaked mutable profile or family state");
            view.Profile.Root = @"C:\HandoffUnit\Tampered";
            view.FamilyPids.Clear();
            HandoffRequire(tracker.Current.Profile.Root == candidate.Profile.Root && tracker.Current.FamilyPids.Count == 2,
                "Current getter leaked mutable candidate state");

            GameDetection background = RendererHandoffTracker.Copy(candidate);
            background.RendererForeground = false;
            tracker = new RendererHandoffTracker();
            tracker.Offer(background, 0);
            HandoffRequire(tracker.Confirmed(0) == null, "background hard selection bypassed recovery");
            tracker.Recovery(background, true);
            tracker.Refresh(true, false, 100000);
            confirmed = tracker.Confirmed(100000);
            HandoffRequire(confirmed != null && !confirmed.RendererForeground && tracker.BeginProbe(100000) == null,
                "recovered explicit background selection lost its established semantics");
        }

        private static GameDetection HandoffTestCandidate()
        {
            var profile = GameProfileStore.NewProfile("synthetic handoff", @"C:\HandoffUnit\Game",
                @"C:\HandoffUnit\Game\GameMenu.exe");
            profile.Id = "handoff-unit-profile";
            var result = new GameDetection
            {
                Profile = profile,
                RendererPid = 42001,
                RendererCreation = 123456789,
                RendererName = "GameRender",
                RendererPath = @"C:\HandoffUnit\Game\Client\GameRender.exe",
                RendererForeground = true,
                RendererCandidateSelected = false,
                RendererUserSelected = false,
                RendererLearnable = true,
                RequiresGpuConfirm = true,
                Evidence = "synthetic pending"
            };
            result.FamilyPids.Add(result.RendererPid);
            result.FamilyPids.Add(result.RendererPid + 1);
            result.FamilyNames.Add("GameRender");
            result.FamilyNames.Add("GameMenu");
            return result;
        }

        private static RendererHandoffTracker HandoffReadyTracker(GameDetection candidate, long nowMs)
        {
            var tracker = new RendererHandoffTracker();
            tracker.Offer(candidate, nowMs);
            tracker.Recovery(candidate, true);
            return tracker;
        }

        private static Dictionary<int, double> HandoffGpu(GameDetection candidate, double value)
        {
            var result = new Dictionary<int, double>();
            result[candidate.RendererPid] = value;
            return result;
        }

        private static void HandoffRequire(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
    }
}
#endif
