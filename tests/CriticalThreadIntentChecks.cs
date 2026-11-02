using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading;

namespace PaviseApp
{
#if PAVISE_CRITICAL_THREAD_INTENT_CHECKS
    internal static class SelfTests
    { internal static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application startup is forbidden in pure critical-thread fixtures"); } }
#endif
    internal static class CriticalThreadIntentChecks
    {
        private static int checks;
        private static void Check(bool value, string reason)
        { checks++; if (!value) throw new InvalidOperationException("FAIL " + reason); }
        private static ThreadTimes Sample(int window, long birth = 9000)
        {
            long elapsed = window * 5L * TimeSpan.TicksPerSecond;
            return new ThreadTimes { Creation = birth, Ticks = 1000000000L + elapsed, ProcessCpu = elapsed * 12 / 10,
                Ids = new long[] { 101, 102, 103 }, Created = new long[] { 10, 20, 30 },
                Cpu = new long[] { elapsed * 7 / 10, elapsed / 2, 0 } };
        }
        private static void Rules()
        {
            ulong[] cores = { 3, 12, 48, 192 }; const ulong all = 255;
            string stamp = ManualCorePlan.Stamp(all, cores);
            var candidateOnly = new CriticalThreadRule(stamp, 1, 16);
            Check(!candidateOnly.WorkerAvoidance, "worker avoidance must default off");
            Check(candidateOnly.Validate(all, cores, all, 0, true) == null, "two independent physical candidates with two workers are usable");
            var avoid = new CriticalThreadRule(stamp, true, 1, 16);
            Check(avoid.WorkerAvoidance && CriticalThreadRule.Parse(avoid.Encode()).WorkerAvoidance, "worker choice persists only through explicit flag");
            Check(CriticalThreadRule.Parse(candidateOnly.Encode()).Encode() == candidateOnly.Encode(), "candidate rule round trip");
            Check(CriticalThreadRule.Parse(candidateOnly.Encode().Substring(0, candidateOnly.Encode().Length - 2)) == null, "missing worker intent cannot silently become enabled");
            Check(CriticalThreadRule.Parse(candidateOnly.Encode().Replace("|0", "|2")) == null, "unknown worker mode rejected");
            Check(new CriticalThreadRule(stamp, 1, 2).Validate(all, cores, all, 0, true) != null, "SMT siblings are not two physical candidates");
            Check(candidateOnly.Validate(all, cores, 63, 0, true) != null, "two candidates still require two other physical cores in candidate-only mode");
            Check(candidateOnly.Validate(all, cores, 253, 0, true) == null, "an allowed candidate required its unselected SMT sibling");
            Check(candidateOnly.Validate(all, cores, 0x55, 0, true) == null && avoid.Validate(all, cores, 0x55, 0, true) == null,
                "two candidates and two workers could not use four single-thread physical cores");
            Check(new CriticalThreadRule(stamp, true, 0x80).Validate(all, cores, 0xA8, 0, true) == null,
                "a candidate on an odd-only physical core was refused");
            Check(new CriticalThreadRule(stamp, true, 1).Validate(all, cores, 7, 0, true) != null,
                "three selected logical processors on two physical cores were enough for a rule");
            Check(new CriticalThreadRule(stamp, true, 1).Validate(all, cores, 0x55, 0x80, true) == null,
                "a half-reserved core removed more than its own worker capacity");
            Check(new CriticalThreadRule(stamp, true, 1).Validate(all, cores, 0x15, 0x20, true) != null,
                "the allowed sibling of a reserved logical processor counted as worker capacity");
            Check(new CriticalThreadRule(stamp, true, 2).Validate(all, cores, 0x55, 0, true) != null,
                "a candidate outside the allowed logical processors was admitted");
            Check(candidateOnly.Validate(all, cores, all, 3, true) != null, "external reserved physical candidate rejected");
            Check(candidateOnly.Validate(all, cores, all, 0, false) != null, "unknown topology does not create permission");
            Check(candidateOnly.Validate(all, new ulong[] { 3, 12, 48, 128 }, all, 0, true) != null, "changed topology rejected");
            var none = CriticalThreadAllowedRange.Evaluate(false, null, all, cores, 0, true, true);
            Check(none.Mask == all && none.Error == null, "disabled old plan does not restrict a new intent");
            var malformed = CriticalThreadAllowedRange.Evaluate(true, null, all, cores, 0, true, true);
            Check(malformed.Mask == 0 && malformed.Error != null, "enabled malformed old plan cannot expand to all");
            var stale = new ManualCorePlan(63, false, stamp + ",100");
            Check(CriticalThreadAllowedRange.Evaluate(true, stale, all, cores, 0, true, true).Mask == 0, "enabled stale old plan cannot expand to all");
            var manual = new ManualCorePlan(63, false, stamp);
            var restricted = CriticalThreadAllowedRange.Evaluate(true, manual, all, cores, 0, true, true);
            Check(restricted.Mask == 63 && restricted.Error == null, "valid enabled original plan remains the boundary");
            Check(CriticalThreadAllowedRange.Evaluate(false, stale, all, cores, 0, true, true).Mask == all, "disabled stale intent is not active");
            Check(CriticalThreadAllowedRange.Evaluate(false, null, all, cores, 0, true, false).Error != null, "untrusted groups remain unsupported even without a manual process plan");
            var profile = new GameProfile { Id = "pure-profile", Name = "Pure profile" };
            profile.Overrides[PolicyCatalog.KeyAutoCores] = "1";
            profile.Overrides[ManualCorePlan.Key] = "invalid";
            Check(CriticalThreadAllowedRange.FromProfile(profile, all, cores, 0, true, true).Error != null,
                "actual Resolver path must inspect enabled intent before requiring a parsed plan");
            profile.Overrides[PolicyCatalog.KeyAutoCores] = "0";
            Check(CriticalThreadAllowedRange.FromProfile(profile, all, cores, 0, true, true).Mask == all,
                "actual Resolver disabled malformed intent does not restrict a new rule");
            profile.Overrides[PolicyCatalog.KeyAutoCores] = "1";
            profile.Overrides[ManualCorePlan.Key] = manual.Encode();
            Check(CriticalThreadAllowedRange.FromProfile(profile, all, cores, 0, true, true).Mask == manual.GameMask,
                "actual Resolver preserves a valid enabled process boundary");
#if PAVISE_CRITICAL_THREAD_INTENT_CHECKS
            Check(PolicyCatalog.Canonical(CriticalThreadRule.Key, candidateOnly.Encode()) == candidateOnly.Encode(),
                "actual profile-store canonicalizer accepts a valid new hard-thread rule");
            Check(PolicyCatalog.Canonical(CriticalThreadRule.Key, "invalid") == null,
                "actual profile-store canonicalizer refuses malformed new hard-thread rules");
#endif
        }
        private static void Observations()
        {
            var observation = new CriticalThreadObservation();
            var original = Sample(0);
            Check(observation.Observe(original, original.Creation, original.Ticks), "first complete snapshot admitted");
            original.Cpu[0] = long.MaxValue;
            for (int window = 1; window <= 3; window++) {
                var sample = Sample(window);
                Check(observation.Observe(sample, sample.Creation, sample.Ticks), "fresh snapshot " + window);
                Check(observation.Candidates.Length == (window == 3 ? 2 : 0), "three sustained windows required, input arrays privately owned");
            }
            var facts = observation.Candidates;
            Check(facts.All(x => x.Windows == 3 && x.CpuShare >= .35), "facts contain sustained CPU demand, no invented role");
            facts[0] = null;
            Check(observation.Candidates[0] != null, "caller cannot mutate candidate array");
            var same = Sample(3);
            Check(observation.Observe(same, same.Creation, same.Ticks) && observation.Candidates.Length == 2
                && observation.Candidates[0].Windows == 3, "shared cache replay cannot erase or manufacture an observation window");
            var reused = Sample(4); reused.Created[0] = 1000;
            Check(observation.Observe(reused, reused.Creation, reused.Ticks), "thread recreation remains an observation event");
            Check(observation.Candidates.All(x => x.Tid != 101), "same TID with a new birth cannot inherit old confidence");
            var stale = Sample(5);
            Check(!observation.Observe(stale, stale.Creation, stale.Ticks + CriticalThreadObservation.MaximumSampleAge + 1)
                && observation.Candidates.Length == 0, "stale snapshot withdraws old candidates");
            var duplicate = Sample(6); duplicate.Ids[1] = duplicate.Ids[0];
            Check(!observation.Observe(duplicate, duplicate.Creation, duplicate.Ticks), "duplicate TIDs rejected");
            var wrong = Sample(7);
            Check(!observation.Observe(wrong, wrong.Creation + 1, wrong.Ticks), "wrong process creation rejected");
            var reset = Sample(0, 10000);
            Check(observation.Observe(reset, reset.Creation, reset.Ticks) && observation.Candidates.Length == 0, "new process starts with no inherited confidence");
            var backwards = Sample(0, 10000); backwards.Ticks--;
            Check(!observation.Observe(backwards, backwards.Creation, backwards.Ticks), "clock reversal withdraws observation");
            var large = Sample(1); large.Ids = new long[CriticalThreadObservation.MaximumThreads + 1];
            large.Created = new long[large.Ids.Length]; large.Cpu = new long[large.Ids.Length];
            Check(!observation.Observe(large, large.Creation, large.Ticks), "oversize source cannot be truncated and called complete");
            observation.Observe(Sample(0), 9000, Sample(0).Ticks);
            var counter = Sample(1); counter.ProcessCpu = -1;
            Check(!observation.Observe(counter, 9000, counter.Ticks), "negative CPU source rejected");
            var reverseCounter = new CriticalThreadObservation();
            var later = Sample(3);
            reverseCounter.Observe(later, 9000, later.Ticks);
            var lower = Sample(4); lower.ProcessCpu = later.ProcessCpu - 1;
            Check(!reverseCounter.Observe(lower, 9000, lower.Ticks), "positive process CPU counter reversal also withdraws confidence");
        }
        private static void RoleFacts()
        {
            var latest = Sample(3);
            var source = new FrameCoordinationSample { Pid = 42, Birth = 9000, Valid = true,
                PresentTid = 101, WorkerTid = 102, Issue = FrameCaptureIssue.None };
            source.Threads.Add(101, 10); source.Threads.Add(102, 20);
            FrameCoordination.SeedCriticalRolesForTest(source, 7, latest.Ticks);
            var facts = FrameCoordination.ReadCriticalRoles(42, 9000, latest, latest.Ticks);
            Check(facts.Length == 2 && facts[0].Role == "presentation-associated", "fresh same-target captured association can be reused");
            facts[0] = null;
            Check(FrameCoordination.ReadCriticalRoles(42, 9000, latest, latest.Ticks)[0] != null, "caller cannot mutate retained role array");
            Check(FrameCoordination.ReadCriticalRoles(43, 9000, latest, latest.Ticks).Length == 0, "another PID cannot borrow captured roles");
            Check(FrameCoordination.ReadCriticalRoles(42, 9001, latest, latest.Ticks).Length == 0, "another process birth cannot borrow roles");
            var reused = Sample(3); reused.Created[0] = 1000;
            Check(FrameCoordination.ReadCriticalRoles(42, 9000, reused, reused.Ticks).All(x => x.Tid != 101), "captured TID cannot follow a recreated thread");
            FrameCoordination.InvalidateCriticalRolesForTest();
            Check(FrameCoordination.ReadCriticalRoles(42, 9000, latest, latest.Ticks).Length == 0, "retired capture generation cannot supply facts");
            FrameCoordination.SeedCriticalRolesForTest(source, 8, latest.Ticks);
            var later = Sample(17);
            Check(FrameCoordination.ReadCriticalRoles(42, 9000, later, later.Ticks).Length == 0, "association expires independently of a fresh thread snapshot");
            source.LostEvents = 1;
            FrameCoordination.SeedCriticalRolesForTest(source, 9, latest.Ticks);
            Check(FrameCoordination.ReadCriticalRoles(42, 9000, latest, latest.Ticks).Length == 0, "lost-event source is not promoted to role facts");
            source.LostEvents = 0; source.Valid = false;
            FrameCoordination.SeedCriticalRolesForTest(source, 10, latest.Ticks);
            Check(FrameCoordination.ReadCriticalRoles(42, 9000, latest, latest.Ticks).Length == 0, "invalid capture is not promoted to a role");
        }
        private static void Set(GameMode mode, string name, object value)
        { typeof(GameMode).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mode, value); }
        private static T Get<T>(GameMode mode, string name)
        { return (T)typeof(GameMode).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode); }
        private static void SessionViewCache()
        {
            // Exercise the real GameMode UI consumer, without constructing the
            // application/controller or opening a process, window or real store.
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            var profile = new GameProfile { Id = "session-view", Name = "Session view" };
            string rule = new CriticalThreadRule(ManualCorePlan.Stamp(255, new ulong[] { 3, 12, 48, 192 }), 1).Encode();
            profile.Overrides[CriticalThreadRule.Key] = rule;
            Set(mode, "sync", new object()); Set(mode, "profiles", new List<GameProfile> { profile });
            Set(mode, "enabled", true); Set(mode, "active", true);
            Set(mode, "gameSessionStarts", 7L); Set(mode, "criticalIntentEpoch", 20L);
            Set(mode, "activeDetection", new GameDetection { Profile = profile.Clone(), RendererPid = 42, RendererCreation = 9000 });
            Guid token = Guid.NewGuid();
            var view = new CriticalThreadUiState(profile.Id, "observing", "", true, sessionActive: true,
                sessionToken: token, gameSerial: 7, intentEpoch: 20,
                targetPid: 42, targetBirth: 9000, ruleSignature: rule);
            Set(mode, "criticalThreadUi", view);
            Check(ReferenceEquals(mode.CriticalThreadView(profile.Id), view), "current session view is returned from cache");
            Set(mode, "gameSessionStarts", 8L);
            Check(mode.CriticalThreadView(profile.Id).State == "waiting-game", "stale same-profile view is not shown as an active session");
            Set(mode, "gameSessionStarts", 7L);
            Set(mode, "criticalThreadUi", new CriticalThreadUiState(profile.Id, "recovery-pending", "", true, pending: true,
                sessionActive: true, sessionToken: token, gameSerial: 7, intentEpoch: 20,
                targetPid: 42, targetBirth: 9000, ruleSignature: rule));
            Set(mode, "criticalHistoricalDebt", true);
            Check(mode.CriticalThreadView(profile.Id).Pending, "actual historical recovery debt remains visible");
            Set(mode, "criticalHistoricalDebt", false);
            Check(!mode.CriticalThreadView(profile.Id).Pending && mode.CriticalThreadView(profile.Id).State == "waiting-game",
                "settled recovery cannot retain a stale pending view through either cached-view path");
        }
#if PAVISE_CRITICAL_THREAD_INTENT_CHECKS
        private static void OriginalLeasePredicate()
        {
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            object gate = new object();
            string rule = new CriticalThreadRule(ManualCorePlan.Stamp(255, new ulong[] { 3, 12, 48, 192 }), 1).Encode();
            Set(mode, "criticalProcessGate", gate); Set(mode, "criticalIntentEpoch", 20L);
            Set(mode, "gameSessionStarts", 7L); Set(mode, "active", true); Set(mode, "enabled", true);
            Set(mode, "dataDir", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Pavise-Authority-Pure-NoIO"));
            Set(mode, "profileStore", FormatterServices.GetUninitializedObject(typeof(GameProfileStore)));
            Set(mode, "rendererObservedForeground", 42); mode.RendererTestForeground = delegate { return 42; };
            Set(mode, "criticalThreadUi", new CriticalThreadUiState("", "off", ""));
            var mintType = typeof(GameMode).GetNestedType("CriticalThreadMint", BindingFlags.NonPublic);
            object mint = Activator.CreateInstance(mintType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { 42, 9000L, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NoNativeTarget.exe"), 255UL, 20L, 7L, rule }, null);
            Set(mode, "criticalMintInFlight", mint);
            bool refused = false;
            try { GameMode.CriticalThreadTargetLease.Issue(mode, mint); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "controlled original mint still requires the actual process gate");
            lock (gate) {
                var original = GameMode.CriticalThreadTargetLease.Issue(mode, mint);
                Set(mode, "criticalOriginalTarget", original); original.RequireActive(); checks++;
                refused = false;
                try { GameMode.CriticalThreadTargetLease.Issue(mode, mint); } catch (InvalidOperationException) { refused = true; }
                Check(refused, "the original mint is single-use even while its original gate is held");
                // A binding does not follow the foreground: another foreground, a switch-out or the
                // renderer foreground observer neither refuses its writes nor revokes it.
                mode.RendererTestForeground = delegate { return 99; };
                Set(mode, "smartSwitchedOut", true);
                bool kept = true;
                try { original.RequireActive(); } catch (OperationCanceledException) { kept = false; }
                Check(kept, "another foreground or a switch-out refused the writes of a binding in place");
                typeof(GameMode).GetMethod("ObserveRendererForegroundChange", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, null);
                Check(!original.Revoked && (long)typeof(GameMode).GetField("criticalIntentEpoch", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode) == 20L,
                    "a foreground change revoked a binding in place");
                Set(mode, "smartSwitchedOut", false);
                original.RequireRecovery(); checks++;
                mode.RendererTestForeground = delegate { return 42; };
                original.Revoke(); refused = false;
                try { original.RequireActive(); } catch (OperationCanceledException) { refused = true; }
                Check(refused, "original revocation withdraws only forward writes");
                original.RequireRecovery(); checks++;
            }
        }
        private static void ParentMaskLockOrder()
        {
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            object gate = new object(), shared = new object();
            Set(mode, "criticalProcessGate", gate); Set(mode, "sync", shared);
            bool result = true, fast = false; Exception failure = null;
            using (var began = new ManualResetEvent(false))
            using (var complete = new ManualResetEvent(false)) {
                var worker = new Thread(delegate() {
                    lock (shared) {
                        began.Set();
                        try { result = (bool)typeof(GameMode).GetMethod("RestoreHardPin", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(mode, new object[] { 42 }); }
                        catch (Exception error) { failure = error; }
                        finally { complete.Set(); }
                    }
                });
                worker.IsBackground = true;
                lock (gate) {
                    worker.Start();
                    if (!began.WaitOne(5000)) throw new InvalidOperationException("FAIL isolated lock-order worker did not enter the source lock");
                    fast = complete.WaitOne(500);
                }
                Check(worker.Join(5000) && fast && failure == null && !result,
                    "a parent-mask caller holding sync must refuse a busy placement gate without waiting or native access");
            }
        }
#endif
        private static int Main()
        {
            try {
                Settings.UseTransientStoreForCurrentProcess(); Lang.Cur = 1; Logger.WritesEnabled = false;
                Rules(); Observations(); RoleFacts(); SessionViewCache();
#if PAVISE_CRITICAL_THREAD_INTENT_CHECKS
                OriginalLeasePredicate(); ParentMaskLockOrder();
#endif
                Check(typeof(GameMode.CriticalThreadTargetLease).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).All(x => x.IsPrivate), "native target constructor remains private");
                bool refused = false;
                try { GameMode.CriticalThreadTargetLease.Issue(null, new object()); } catch (InvalidOperationException) { refused = true; }
                Check(refused, "an arbitrary object cannot mint an original target");
                Console.WriteLine("PASS critical thread intent checks=" + checks + "; pure data/observation; no native thread or UI execution");
                return 0;
            } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
