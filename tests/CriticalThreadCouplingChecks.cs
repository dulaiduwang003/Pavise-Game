#if PAVISE_SELFTEST && CB_T2B_COUPLING_CHECKS
using System;
using System.Reflection;
using System.Runtime.Serialization;

namespace PaviseApp
{
    internal static class SelfTests
    { internal static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application startup is forbidden in coupling fixtures"); } }

    // Pure classifier plus GameMode wiring on an uninitialized instance with transient settings.
    // No game thread, UI or application Main is involved; one fixture uses two managed threads of this
    // process to hold GameMode.sync while the wiring runs without a session policy. The admission fixtures
    // (C-1/C-2) run StepCriticalThreadRuntime with the test foreground probe and, for two checks, a fake hard-affinity
    // session (empty ledger, no thread write); the two C-2 checks open only a query-limited handle to this test
    // process and read its affinity. Nothing is written.
    internal static class CriticalThreadCouplingChecks
    {
        private const int Pid = 4242, Tid1 = 111, Tid2 = 222;
        private const long Birth = 133000000000000100, Birth1 = 133000000000000111, Birth2 = 133000000000000222;
        private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly string[] Forbidden = { "提升", "收益", "改善", "更流畅", "更快", "improve", "benefit", "faster", "smoother", "gain" };
        private static int assertions;

        private static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException("FAIL " + message); }
        private static HardThreadIdentity[] Both()
        { return new[] { new HardThreadIdentity(Tid1, Birth1), new HardThreadIdentity(Tid2, Birth2) }; }
        private static CriticalThreadLaneThread Lane(int tid, long threadBirth) { return new CriticalThreadLaneThread(Pid, Birth, tid, threadBirth); }
        private static CriticalThreadBoostCoupling Classify(bool boost, string mode, bool eligible,
            CriticalThreadLaneThread lane, HardThreadIdentity[] bound, bool frame, out int tid)
        { return CriticalThreadBoostCouplingPolicy.Classify(boost, mode, eligible, Pid, Birth, lane, bound, frame, out tid); }
        private static CriticalThreadBoostCoupling Classify(bool boost, string mode, bool eligible,
            CriticalThreadLaneThread lane, HardThreadIdentity[] bound, bool frame)
        { int ignored; return Classify(boost, mode, eligible, lane, bound, frame, out ignored); }

        private static void Pure()
        {
            int tid;
            Check(Classify(true, ThreadBoostSelection.Off, true, Lane(Tid1, Birth1), Both(), true) == CriticalThreadBoostCoupling.BoostOff
                && Classify(false, ThreadBoostSelection.Off, true, null, null, false) == CriticalThreadBoostCoupling.BoostOff,
                "a thread boost choice of off was reported as running");
            foreach (string mode in new[] { ThreadBoostSelection.Candidate, ThreadBoostSelection.Frame })
                Check(Classify(false, mode, true, Lane(Tid1, Birth1), Both(), true) == CriticalThreadBoostCoupling.BoostDisabled,
                    "the game resource priority switch was ignored");
            Check(Classify(true, ThreadBoostSelection.Candidate, false, Lane(Tid1, Birth1), Both(), false) == CriticalThreadBoostCoupling.CandidateUnsupported,
                "an unavailable busy-thread boost was reported as selected");
            Check(Classify(true, ThreadBoostSelection.Candidate, true, Lane(Tid1, Birth1), null, false) == CriticalThreadBoostCoupling.CandidateSelected
                && Classify(true, ThreadBoostSelection.Candidate, true, Lane(Tid1, Birth1), new HardThreadIdentity[0], false) == CriticalThreadBoostCoupling.CandidateSelected,
                "an unbound rule reported a thread-level overlap");
            Check(Classify(true, ThreadBoostSelection.Candidate, true, null, Both(), false) == CriticalThreadBoostCoupling.BoostInactive,
                "a missing busy-thread boost was reported for this game");
            Check(CriticalThreadBoostCouplingPolicy.Classify(true, ThreadBoostSelection.Candidate, true, Pid, Birth,
                    new CriticalThreadLaneThread(Pid + 1, Birth, Tid1, Birth1), Both(), false, out tid) == CriticalThreadBoostCoupling.BoostInactive,
                "a boost in another process was reported for this game");
            Check(CriticalThreadBoostCouplingPolicy.Classify(true, ThreadBoostSelection.Candidate, true, Pid, Birth,
                    new CriticalThreadLaneThread(Pid, Birth + 1, Tid1, Birth1), Both(), false, out tid) == CriticalThreadBoostCoupling.BoostInactive,
                "a boost in another process instance was reported for this game");
            Check(Classify(true, ThreadBoostSelection.Candidate, true, Lane(Tid1, 0), Both(), false) == CriticalThreadBoostCoupling.Unknown,
                "a busy-thread boost without creation time was classified");
            Check(Classify(true, ThreadBoostSelection.Candidate, true, Lane(Tid1, Birth1), Both(), false, out tid) == CriticalThreadBoostCoupling.SameThread && tid == Tid1,
                "the same thread was not reported");
            Check(Classify(true, ThreadBoostSelection.Candidate, true, Lane(Tid2, Birth2), Both(), false, out tid) == CriticalThreadBoostCoupling.SameThread && tid == Tid2,
                "the second candidate's overlap was missed");
            Check(Classify(true, ThreadBoostSelection.Candidate, true, Lane(Tid1, Birth1 + 5), Both(), false, out tid) == CriticalThreadBoostCoupling.OtherThread && tid == 0,
                "a reused TID was reported as the same thread");
            Check(Classify(true, ThreadBoostSelection.Candidate, true, Lane(333, Birth1), Both(), false) == CriticalThreadBoostCoupling.OtherThread,
                "a different thread was reported as the same thread");
            Check(Classify(true, ThreadBoostSelection.Frame, true, null, null, true) == CriticalThreadBoostCoupling.FrameSelected,
                "an unbound render-thread boost was not reported as selected");
            Check(Classify(true, ThreadBoostSelection.Frame, true, null, Both(), true) == CriticalThreadBoostCoupling.FrameAssociated,
                "a frame association was ignored");
            Check(Classify(true, ThreadBoostSelection.Frame, true, null, Both(), false) == CriticalThreadBoostCoupling.FrameOther,
                "an unassociated candidate was reported as possibly boosted");
            Check(Classify(true, "unexpected", true, Lane(Tid1, Birth1), Both(), true) == CriticalThreadBoostCoupling.Unknown
                && Classify(true, null, true, Lane(Tid1, Birth1), Both(), true) == CriticalThreadBoostCoupling.Unknown,
                "an unknown boost mode was classified");

            int previous = Lang.Cur;
            try
            {
                foreach (int language in new[] { 0, 1 })
                {
                    Lang.Cur = language;
                    foreach (CriticalThreadBoostCoupling value in Enum.GetValues(typeof(CriticalThreadBoostCoupling)))
                    {
                        string text = CriticalThreadBoostCouplingPolicy.Text(value, Tid2);
                        Check(value == CriticalThreadBoostCoupling.Unknown ? text == "" : text.Length > 0, "coupling text missing for " + value);
                        foreach (string word in Forbidden)
                            Check(text.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0, "coupling text claims a benefit for " + value);
                    }
                    string same = CriticalThreadBoostCouplingPolicy.Text(CriticalThreadBoostCoupling.SameThread, Tid2);
                    Check(same.Contains("TID " + Tid2), "the overlapping candidate TID was not shown");
                    foreach (CriticalThreadBoostCoupling value in new[] { CriticalThreadBoostCoupling.SameThread, CriticalThreadBoostCoupling.OtherThread })
                    {
                        string text = CriticalThreadBoostCouplingPolicy.Text(value, Tid2);
                        Check(text.IndexOf("渲染", StringComparison.Ordinal) < 0 && text.IndexOf("render", StringComparison.OrdinalIgnoreCase) < 0,
                            "a busy-thread candidate was called a render thread");
                    }
                }
            }
            finally { Lang.Cur = previous; }
        }

        // The frame-association flag computed at session start, as a pure function (CD review N3).
        private static void FrameAssociation()
        {
            var choices = new[] { new HardThreadChoice(new HardThreadIdentity(Tid1, Birth1), 1UL), new HardThreadChoice(new HardThreadIdentity(Tid2, Birth2), 4UL) };
            Check(CriticalThreadBoostCouplingPolicy.FrameAssociated(choices, new[] { new CriticalThreadRoleFact(Tid1, Birth1, "presentation-associated") }),
                "a role fact on a bound candidate was not treated as associated");
            Check(CriticalThreadBoostCouplingPolicy.FrameAssociated(choices, new[] { new CriticalThreadRoleFact(Tid2, Birth2, "dependency-associated") }),
                "the second candidate's role fact was missed");
            Check(!CriticalThreadBoostCouplingPolicy.FrameAssociated(choices, new[] { new CriticalThreadRoleFact(333, Birth1, "presentation-associated") }),
                "a role fact of an unbound thread marked the candidates as associated");
            Check(!CriticalThreadBoostCouplingPolicy.FrameAssociated(choices, new[] { new CriticalThreadRoleFact(Tid1, Birth1 + 7, "presentation-associated") }),
                "a reused TID with another creation time was treated as associated");
            Check(!CriticalThreadBoostCouplingPolicy.FrameAssociated(choices, new CriticalThreadRoleFact[0])
                && !CriticalThreadBoostCouplingPolicy.FrameAssociated(choices, null)
                && !CriticalThreadBoostCouplingPolicy.FrameAssociated(null, new[] { new CriticalThreadRoleFact(Tid1, Birth1, "presentation-associated") })
                && !CriticalThreadBoostCouplingPolicy.FrameAssociated(new HardThreadChoice[] { null }, new CriticalThreadRoleFact[] { null }),
                "missing role facts were treated as associated");
        }

        private static void Set(object target, string name, object value)
        {
            FieldInfo field = typeof(GameMode).GetField(name, Instance);
            if (field == null) throw new InvalidOperationException("FAIL missing GameMode field " + name);
            field.SetValue(target, value);
        }
        private static void SetLane(bool applied, int pid, long birth, int tid, long threadBirth)
        {
            foreach (var pair in new object[][] { new object[] { "laneApplied", applied }, new object[] { "lanePid", pid },
                new object[] { "laneCreation", birth }, new object[] { "laneTid", tid }, new object[] { "laneThreadCreation", threadBirth } })
            {
                FieldInfo field = typeof(RenderLane).GetField((string)pair[0], Static);
                if (field == null) throw new InvalidOperationException("FAIL missing RenderLane field " + pair[0]);
                field.SetValue(null, pair[1]);
            }
        }
        private static HardThreadPlan Plan()
        {
            ConstructorInfo create = typeof(HardThreadPlan).GetConstructor(Instance, null, new[] { typeof(HardThreadTarget), typeof(HardThreadChoice[]),
                typeof(ulong), typeof(ulong), typeof(ushort), typeof(HardThreadWorkerMode) }, null);
            if (create == null) throw new InvalidOperationException("FAIL HardThreadPlan constructor changed");
            HardThreadIdentity[] threads = Both();
            var choices = new[] { new HardThreadChoice(threads[0], 1UL), new HardThreadChoice(threads[1], 4UL) };
            return (HardThreadPlan)create.Invoke(new object[] { null, choices, 0UL, 0UL, (ushort)0, HardThreadWorkerMode.CandidatesOnly });
        }
        private static void Policy(GameMode mode, string thread, bool boost)
        {
            if (!ThreadBoostSelection.Save(thread) || !Settings.Save(PolicyCatalog.KeyBoost, boost))
                throw new InvalidOperationException("FAIL transient policy could not be saved");
            Set(mode, "sessionPolicy", PolicyResolver.Global());
        }
        // 模拟未识别画面线程时回退到最忙线程。
        private static void Fallback(GameMode mode, int pid, long birth)
        {
            Type type = typeof(GameMode).GetNestedType("LaneFallbackTarget", BindingFlags.NonPublic);
            if (type == null) throw new InvalidOperationException("FAIL GameMode has no lane fallback target");
            Set(mode, "laneFallback", pid <= 0 ? null : Activator.CreateInstance(type, Instance, null, new object[] { pid, birth }, null));
        }
        private static CriticalThreadBoostCoupling Wired(GameMode mode, bool applied, out int tid)
        {
            object[] args = { Pid, Birth, applied, 0 };
            MethodInfo method = typeof(GameMode).GetMethod("CriticalThreadCoupling", Instance);
            if (method == null) throw new InvalidOperationException("FAIL GameMode has no coupling wiring");
            var value = (CriticalThreadBoostCoupling)method.Invoke(mode, args);
            tid = (int)args[3]; return value;
        }
        private static object Source()
        {
            Type type = typeof(GameMode).GetNestedType("CriticalRuntimeSource", BindingFlags.NonPublic);
            var detection = new GameDetection { Profile = new GameProfile { Id = "coupling-fixture" }, RendererPid = Pid, RendererCreation = Birth,
                RendererName = "fixture.exe", RendererPath = @"C:\PaviseCouplingFixture\fixture.exe" };
            string rule = new CriticalThreadRule("3,C,30", 1UL).Encode();
            ConstructorInfo create = type.GetConstructor(Instance, null, new[] { typeof(GameDetection), typeof(string), typeof(long), typeof(long) }, null);
            if (create == null) throw new InvalidOperationException("FAIL CriticalRuntimeSource constructor changed");
            return create.Invoke(new object[] { detection, rule, 1L, 1L });
        }
        private static CriticalThreadUiState Publish(GameMode mode, object source, bool applied)
        {
            MethodInfo publish = typeof(GameMode).GetMethod("PublishCriticalThreadState", Instance);
            publish.Invoke(mode, new object[] { source, applied ? "active" : "observing", "", applied, true, 0, 1L,
                applied ? CriticalThreadCoverageState.CandidatesOnly : CriticalThreadCoverageState.None });
            return (CriticalThreadUiState)typeof(GameMode).GetField("criticalThreadUi", Instance).GetValue(mode);
        }

        private static void Wiring()
        {
            Func<PerformancePreset, bool> oldLane = GameMode.LaneSupportedForTest;
            Action<string> oldRaw = PolicyResolver.RawReadForTest;
            bool eligible = true;
            int laneReads = 0, choiceReads = 0;
            GameMode.LaneSupportedForTest = delegate { laneReads++; return eligible; };
            PolicyResolver.RawReadForTest = delegate (string key) { if (key == PolicyCatalog.KeyThreadBoost) choiceReads++; };
            Settings.UseTransientStoreForCurrentProcess();
            try
            {
                var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
                Set(mode, "sync", new object());
                Set(mode, "criticalPlan", Plan());
                Set(mode, "criticalChoiceLabels", new[] { "TID " + Tid1, "TID " + Tid2 });
                SetLane(true, Pid, Birth, Tid2, Birth2);
                int tid;

                // 开启后，认不出画面线程的这款游戏改用最忙线程
                Policy(mode, ThreadBoostSelection.On, true); Fallback(mode, Pid, Birth);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.SameThread && tid == Tid2,
                    "the wiring missed the busy-thread boost on a bound candidate");
                Check(Wired(mode, false, out tid) == CriticalThreadBoostCoupling.CandidateSelected && tid == 0,
                    "the wiring reported a thread overlap before candidates were applied");
                Policy(mode, ThreadBoostSelection.On, false);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.BoostDisabled,
                    "the wiring ignored the game resource priority switch");
                Policy(mode, ThreadBoostSelection.On, true); eligible = false;
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.CandidateUnsupported,
                    "the wiring ignored busy-thread boost eligibility");
                eligible = true;
                SetLane(true, Pid, Birth, Tid2, Birth2 + 9);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.OtherThread, "the wiring matched a reused TID");
                SetLane(false, Pid, Birth, Tid2, Birth2);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.BoostInactive, "the wiring reported an inactive busy-thread boost");
                SetLane(true, Pid, Birth, Tid2, 0);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.Unknown && tid == 0,
                    "the wiring trusted a busy-thread boost without creation time");
                SetLane(true, Pid, Birth, Tid2, Birth2);
                // 开启且认得出画面线程：渲染关键线程，哪怕最忙线程的旧收据还在
                Policy(mode, ThreadBoostSelection.On, true); Fallback(mode, 0, 0);
                choiceReads = 0;
                Wired(mode, true, out tid);
                Check(choiceReads == 1, "the wiring read the thread boost choice more than once");
                Set(mode, "criticalChoiceFrameAssociated", true);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.FrameAssociated, "the wiring ignored the frame association");
                Set(mode, "criticalChoiceFrameAssociated", false);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.FrameOther, "the wiring reported a frame association that was not observed");
                Fallback(mode, Pid + 1, Birth);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.FrameOther,
                    "a busy-thread fallback for another renderer changed this game's boost");
                Fallback(mode, Pid, Birth + 1);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.FrameOther,
                    "a busy-thread fallback for a reused PID changed this game's boost");
                foreach (string legacy in new[] { ThreadBoostSelection.Candidate, ThreadBoostSelection.Frame })
                {
                    Policy(mode, legacy, true); Fallback(mode, 0, 0);
                    Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.FrameOther,
                        "a saved " + legacy + " choice did not read as thread boost on");
                    Fallback(mode, Pid, Birth);
                    Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.SameThread && tid == Tid2,
                        "a saved " + legacy + " choice ignored the busy-thread fallback");
                }
                Policy(mode, ThreadBoostSelection.Off, true);
                Check(Wired(mode, true, out tid) == CriticalThreadBoostCoupling.BoostOff, "the wiring ignored thread boost being off");
                Fallback(mode, 0, 0);

                // No session policy: Unknown before any read that could fall back to the GameMode-locked preset.
                Set(mode, "sessionPolicy", null);
                object gate = new object(); Set(mode, "sync", gate);
                CriticalThreadBoostCoupling orphan = CriticalThreadBoostCoupling.SameThread; int orphanTid = -1; Exception orphanError = null;
                bool finished;
                using (var held = new System.Threading.ManualResetEventSlim(false))
                using (var release = new System.Threading.ManualResetEventSlim(false))
                {
                    var holder = new System.Threading.Thread(delegate () { lock (gate) { held.Set(); release.Wait(); } }) { IsBackground = true };
                    holder.Start(); held.Wait();
                    var caller = new System.Threading.Thread(delegate () {
                        try { int seen; orphan = Wired(mode, true, out seen); orphanTid = seen; } catch (Exception error) { orphanError = error; }
                    }) { IsBackground = true };
                    caller.Start();
                    finished = caller.Join(5000);
                    release.Set(); holder.Join(); if (!finished) caller.Join();
                }
                if (orphanError != null) throw orphanError;
                Check(finished, "the wiring took the GameMode lock without a session policy");
                Check(orphan == CriticalThreadBoostCoupling.Unknown && orphanTid == 0, "the wiring classified a relation without a session policy");
                Set(mode, "sync", new object());

                Policy(mode, ThreadBoostSelection.On, true); Fallback(mode, Pid, Birth);
                SetLane(true, Pid, Birth, Tid2, Birth2);
                object source = Source();
                CriticalThreadUiState view = Publish(mode, source, true);
                Check(view != null && view.Coupling == CriticalThreadBoostCoupling.SameThread && view.CouplingTid == Tid2
                    && view.CouplingText.Contains("TID " + Tid2), "the published state lost the coupling");
                view = Publish(mode, source, false);
                Check(view.Coupling == CriticalThreadBoostCoupling.CandidateSelected && view.CouplingTid == 0,
                    "an unapplied published state carried a thread overlap");
                var other = new CriticalThreadUiState("p", "off", "");
                Check(other.Coupling == CriticalThreadBoostCoupling.Unknown && other.CouplingText == "",
                    "states built elsewhere gained a coupling they did not compute");
            }
            finally
            {
                GameMode.LaneSupportedForTest = oldLane;
                PolicyResolver.RawReadForTest = oldRaw;
                RenderLane.ResetShutdownForTest();
                Settings.UseTransientStoreForCurrentProcess();
            }
        }

        private const string Listed = "fixture.exe", FixturePath = @"C:\PaviseCouplingFixture\fixture.exe";
        private const string RosterZh = "该游戏此前拒绝或剥夺过写入权限；未施加线程硬绑定";
        private const string RosterEn = "This game previously denied or stripped write access; no hard thread binding is applied";
        private sealed class Admitted { internal CriticalThreadUiState View; internal string FailureTarget; internal bool Session; internal int Hotspots; }

        // Session backends for one restore of a session in progress: an empty ledger, a known machine, no thread write.
        // RefuseRecovery makes the session's own Restore fail the way a refused recovery admission does.
        private sealed class Released : IDisposable { public void Dispose() { } }
        private sealed class FakeGate : IHardThreadGate
        {
            private readonly HardThreadTarget target;
            internal bool RefuseRecovery;
            internal FakeGate(HardThreadTarget target) { this.target = target; }
            public HardThreadTarget Target { get { return target; } }
            public bool AllowWorkers { get { return false; } }
            public IDisposable EnterUse() { return new Released(); }
            public void RequireActive() { }
            public void RequireRecovery() { if (RefuseRecovery) throw new InvalidOperationException("fixture recovery refused"); }
        }
        private sealed class FakeNative : IHardThreadNative
        {
            public HardThreadMachine ReadMachine() { return new HardThreadMachine { Read = HardThreadRead.Known, GroupCount = 1, ProcessMask = 0xFFUL, CpuSetsEmpty = true }; }
            public HardThreadObserved ReadThread(HardThreadIdentity thread) { return new HardThreadObserved { Read = HardThreadRead.Gone }; }
            public bool Set(HardThreadIdentity thread, ushort group, ulong mask, out ushort previousGroup, out ulong previousMask)
            { previousGroup = 0; previousMask = 0; return false; }
            public void Dispose() { }
        }
        private sealed class FakeStore : IHardThreadStore
        {
            public HardThreadJournal Load(HardThreadTarget target) { return new HardThreadJournal { Target = target }; }
            public void Save(long expectedRevision, HardThreadJournal journal) { }
            public void Dispose() { }
        }

        // An uninitialized GameMode set up as the current session of one game, so CaptureCriticalThreadSource and the
        // published state behave as in the product (CriticalThreadSnapshotCurrent holds). The placement gate is free and
        // the recovery ledger on disk is never read (its next read is pushed past the run).
        private static GameMode Current(bool listed, int pid, bool hot, out GameDetection detection, out string rule)
        {
            var profile = new GameProfile { Id = "coupling-fixture" };
            rule = new CriticalThreadRule("3,C,30", 1UL).Encode();
            profile.Overrides[CriticalThreadRule.Key] = rule;
            detection = new GameDetection { Profile = profile, RendererPid = pid, RendererCreation = Birth, RendererName = Listed,
                RendererPath = FixturePath, RendererCandidateSelected = true };
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            Set(mode, "sync", new object()); Set(mode, "criticalProcessGate", new object());
            Set(mode, "profileStore", FormatterServices.GetUninitializedObject(typeof(GameProfileStore)));
            Set(mode, "enabled", true); Set(mode, "active", true); Set(mode, "gameSessionStarts", 1L); Set(mode, "criticalIntentEpoch", 1L);
            Set(mode, "activeDetection", detection); Set(mode, "profiles", new System.Collections.Generic.List<GameProfile> { profile });
            Set(mode, "criticalChoiceLabels", new string[0]); Set(mode, "criticalThreadUi", new CriticalThreadUiState("", "off", ""));
            Set(mode, "criticalDebtNextRead", long.MaxValue);
            var observation = new CriticalThreadObservation();
            if (hot)
            {
                FieldInfo seeded = typeof(CriticalThreadObservation).GetField("candidates", Instance);
                if (seeded == null) throw new InvalidOperationException("FAIL missing CriticalThreadObservation field candidates");
                seeded.SetValue(observation, new[] { new CriticalThreadHotspot(new CoreThreadCandidate { Tid = Tid1, Creation = Birth1, Windows = 3, Mean = .5 }) });
            }
            Set(mode, "criticalObservation", observation);
            Policy(mode, ThreadBoostSelection.Off, false);
            if (listed) ProtectedGameRoster.Remember(Listed); else ProtectedGameRoster.Forget(Listed);
            return mode;
        }
        private static Admitted Read(GameMode mode)
        {
            var observation = (CriticalThreadObservation)typeof(GameMode).GetField("criticalObservation", Instance).GetValue(mode);
            return new Admitted { View = (CriticalThreadUiState)typeof(GameMode).GetField("criticalThreadUi", Instance).GetValue(mode),
                FailureTarget = (string)typeof(GameMode).GetField("criticalFailureTarget", Instance).GetValue(mode),
                Session = typeof(GameMode).GetField("criticalSession", Instance).GetValue(mode) != null, Hotspots = observation.Candidates.Length };
        }
        // One StepCriticalThreadRuntime pass. The foreground probe reports this game or another one; a session, when asked
        // for, is already in progress. No native call: the foreground comes from the test probe and nothing is sampled.
        private static Admitted Step(bool listed, bool foreground, bool hot, bool session, long nextSample, bool restoreFails = false, bool switchedOut = false)
        {
            GameDetection detection; string rule;
            GameMode mode = Current(listed, Pid, hot, out detection, out rule);
            mode.RendererTestForeground = delegate { return foreground ? Pid : Pid + 1; };
            Set(mode, "smartSwitchedOut", switchedOut);
            Set(mode, "criticalNextSample", nextSample);
            if (session)
                Set(mode, "criticalSession", CriticalThreadHardAffinitySession.OpenFake(
                    new FakeGate(new HardThreadTarget(Guid.NewGuid(), Pid, Birth, FixturePath, 1UL)) { RefuseRecovery = restoreFails }, new FakeNative(), new FakeStore()));
            MethodInfo step = typeof(GameMode).GetMethod("StepCriticalThreadRuntime", Instance);
            if (step == null) throw new InvalidOperationException("FAIL GameMode has no StepCriticalThreadRuntime");
            step.Invoke(mode, new object[0]);
            return Read(mode);
        }
        // One BeginCriticalThreadSession call for an unlisted game with a sustained hotspot; the placement gate is not held.
        private static Admitted Begin(int pid)
        {
            GameDetection detection; string rule;
            GameMode mode = Current(false, pid, true, out detection, out rule);
            Type type = typeof(GameMode).GetNestedType("CriticalRuntimeSource", BindingFlags.NonPublic);
            ConstructorInfo create = type.GetConstructor(Instance, null, new[] { typeof(GameDetection), typeof(string), typeof(long), typeof(long) }, null);
            MethodInfo begin = typeof(GameMode).GetMethod("BeginCriticalThreadSession", Instance);
            if (create == null || begin == null) throw new InvalidOperationException("FAIL the hard-affinity session entry changed");
            begin.Invoke(mode, new object[] { create.Invoke(new object[] { detection, rule, 1L, 1L }), null });
            return Read(mode);
        }
        private static bool Published(Admitted value, string state, string zh, string en)
        { return value.View != null && value.View.State == state && value.View.Detail == PerformancePolicy.Text(zh, en) && !value.Session; }

        // C-1: every StepCriticalThreadRuntime pass refuses a game on the protected roster before the foreground, sampling,
        // selection and handle steps, and restores a session in progress first. C-2: the two failures after the process
        // handle in BeginCriticalThreadSession publish a reason instead of returning silently.
        private static void Admission()
        {
            Func<PerformancePreset, bool> oldLane = GameMode.LaneSupportedForTest;
            Func<bool> oldAffinity = GameMode.CriticalAffinityReadableForTest;
            bool oldLogs = Logger.WritesEnabled;
            GameMode.LaneSupportedForTest = delegate { return false; };
            Logger.WritesEnabled = false;
            Settings.UseTransientStoreForCurrentProcess(); ProtectedGameRoster.ResetCache();
            try
            {
                int self = System.Diagnostics.Process.GetCurrentProcess().Id;
                Check(Published(Step(false, false, false, false, 0), "observing", "等待游戏回到前台后重新观察", "Observations restart when the game returns to the foreground"),
                    "an unlisted game lost its original foreground reason");
                Check(Published(Step(true, false, false, false, 0), "unsupported", RosterZh, RosterEn),
                    "a game that denied or stripped writes was not refused before the foreground and sampling steps");
                Admitted hot = Step(true, true, true, false, 0);
                Check(Published(hot, "unsupported", RosterZh, RosterEn) && hot.FailureTarget == null && hot.Hotspots == 0,
                    "a refused game kept its hotspot observations or reached candidate selection");
                Check(Published(Step(true, true, false, true, long.MaxValue), "unsupported", RosterZh, RosterEn),
                    "a session in progress kept its bindings after its game joined the protected roster");
                // T5 (CD review N1): when that restore fails, its recovery-pending state stays visible and the session is kept for the next restore.
                Admitted unrestored = Step(true, true, false, true, long.MaxValue, true);
                Check(unrestored.View != null && unrestored.View.State == "recovery-pending" && unrestored.View.Pending && unrestored.Session
                    && unrestored.View.Detail != PerformancePolicy.Text(RosterZh, RosterEn),
                    "a failed restore of a protected game's session was hidden behind the roster reason");
                // A binding in place does not follow the foreground; a new binding starts only in front of the game.
                Admitted away = Step(false, false, false, true, long.MaxValue, false, true);
                Check(away.Session && away.View != null && away.View.State == "off", "a binding in place was released when the game left the foreground");
                Check(Published(Step(false, true, false, false, 0, false, true), "observing", "等待游戏回到前台后重新观察", "Observations restart when the game returns to the foreground"),
                    "a new binding started while the session was switched out");
                GameMode.CriticalAffinityReadableForTest = delegate { return false; };
                Check(Published(Begin(self), "unsupported", "无法读取游戏进程的处理器亲和性；未施加线程硬绑定",
                    "The game's processor affinity cannot be read; no hard thread binding is applied"),
                    "an unreadable process affinity returned without a reason");
                GameMode.CriticalAffinityReadableForTest = null;
                Admitted refused = Begin(self);
                Check(Published(refused, "observing", "绑定前核对游戏身份与规则未通过；本次未绑定，稍后重新核对",
                    "Game identity and rule checks before binding did not pass; nothing was bound and they will be checked again")
                    && refused.FailureTarget == "coupling-fixture|" + @"C:\PaviseCouplingFixture\fixture.exe",
                    "a refused binding target returned without a reason");
            }
            finally
            {
                GameMode.CriticalAffinityReadableForTest = oldAffinity;
                GameMode.LaneSupportedForTest = oldLane;
                ProtectedGameRoster.Forget(Listed); ProtectedGameRoster.ResetCache();
                Logger.WritesEnabled = oldLogs;
                Settings.UseTransientStoreForCurrentProcess();
            }
        }

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--negative-control") return 3;
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                Pure(); FrameAssociation(); Wiring(); Admission();
                Console.WriteLine("PASS critical thread coupling assertions=" + assertions);
                return 0;
            }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                Console.WriteLine(error.Message.StartsWith("FAIL ") ? error.Message : "FAIL unexpected " + error);
                return 1;
            }
        }
    }
}
#endif
