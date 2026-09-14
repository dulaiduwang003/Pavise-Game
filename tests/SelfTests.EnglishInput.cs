// File purpose Input APIs are all fake, no windows, neither reads nor changes the active input layout and keyboard settings
// The GameMode apply worker thread is not started either
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int englishInputChecks;
        private static readonly IntPtr EnglishUs = new IntPtr(0x04090409);
        private static readonly IntPtr EnglishUk = new IntPtr(0x08090809);
        private static readonly IntPtr ChineseInput = new IntPtr(unchecked((int)0xe0200804));
        private static readonly GameInputProcess EnglishGame = new GameInputProcess(101, 1001);

        internal static int RunEnglishInputRegressionTests()
        {
            Action[] tests = {
                EnglishInputDefaultNativeRefusesTests,
                EnglishInputOneAttemptAndManualSwitch,
                EnglishInputAlreadyAndMissingLayoutAreTerminal,
                EnglishInputLoadedLayoutSelection,
                EnglishInputForegroundWaitIsBounded,
                EnglishInputInitialRendererWaitIsBounded,
                EnglishInputDisabledEntryAndOffOn,
                EnglishInputRendererHandoffAndRedetection,
                EnglishInputProcessReuseAndUnknownHistory,
                EnglishInputHistoryIsBounded,
                EnglishInputRejectedAndUnconfirmedRequests,
                EnglishInputTargetIdentityChecks,
                EnglishInputManualChangeDuringPreparation,
                EnglishInputCancelAtNativeBoundaries,
                EnglishInputExceptionsReleaseLeases,
                EnglishInputCancelDuringBegin,
                EnglishInputDrainAndNoOverlap,
                EnglishInputGameModeConsentAndDefaults,
                EnglishInputGameModeStartupFloor,
                EnglishInputGameModeAdmissionBoundaries,
                EnglishInputGameModeSaveFailure,
                EnglishInputLibraryChangesCannotRearm
            };
            englishInputChecks = 0;
            foreach (Action test in tests)
            {
                Settings.UseTransientStoreForCurrentProcess();
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS english-input assertions=" + englishInputChecks
                + " input_APIs=mocked settings=transient application_started=false windows_shown=false");
            return tests.Length;
        }

        private static void EnglishCheck(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("English input regression: " + message);
            Interlocked.Increment(ref englishInputChecks);
        }

        private static void EnglishInputDefaultNativeRefusesTests()
        {
            var native = new WindowsGameInputLanguageApi();
            bool sendAdmissionReached = false;
            Action[] calls = {
                delegate { int ignored = native.CurrentProcessId; },
                delegate { GameInputWindow ignored; native.TryGetForeground(out ignored); },
                delegate { bool gone; using (IGameInputProcessLease ignored = native.OpenProcess(-1, out gone)) { } },
                delegate { native.GetKeyboardLayouts(); },
                delegate { native.GetKeyboardLayout(0); },
                delegate { native.IsIme(IntPtr.Zero); },
                delegate
                {
                    int error;
                    // Even if that protection degrades, admission returning false plus a null HWND
                    // still guarantees this regression check cannot send real input
                    native.SendRequest(new GameInputWindow(IntPtr.Zero, IntPtr.Zero, 0, 0),
                        IntPtr.Zero, 0, delegate { sendAdmissionReached = true; return false; }, out error);
                }
            };
            foreach (Action call in calls)
            {
                bool refused = false;
                try { call(); }
                catch (InvalidOperationException) { refused = true; }
                EnglishCheck(refused, "default native entry reached Windows instead of refusing test access");
            }
            EnglishCheck(!sendAdmissionReached, "native guard ran after send admission");
            var productionDefault = new GameInputLanguage();
            EnglishInputOutcome result = productionDefault.TryOnce(new GameInputProcess(int.MaxValue, 1),
                delegate { throw new InvalidOperationException("default native must fail before claiming"); }, delegate { return true; });
            EnglishCheck(result.Result == EnglishInputResult.Unavailable && !result.RequestAttempted,
                "default input implementation did not fail closed");
            EnglishCheck(productionDefault.Probe(EnglishGame) == GameInputProcessState.Unknown,
                "test-native refusal was mistaken for a dead real process");
        }

        private sealed class EnglishLife
        {
            internal long Creation;
            internal bool Alive = true, Unknown;
        }

        private sealed class EnglishApiFake : IGameInputLanguageApi
        {
            internal readonly Dictionary<int, EnglishLife> Lives = new Dictionary<int, EnglishLife>();
            internal readonly HashSet<IntPtr> ImeLayouts = new HashSet<IntPtr>();
            internal GameInputWindow Foreground = EnglishWindow(101);
            internal IntPtr Current = ChineseInput;
            internal IntPtr[] Layouts = { ChineseInput, EnglishUs };
            internal IntPtr LastRequested;
            internal int Sends, Leases, Releases, LayoutReads, ForegroundReads, LayoutLists;
            internal int SendError;
            internal uint LastTimeout;
            internal bool ApplyLayout = true;
            internal GameInputSendResult SendResult = GameInputSendResult.Processed;
            internal Action<string> At;

            internal EnglishApiFake()
            {
                Lives.Add(EnglishGame.Pid, new EnglishLife { Creation = EnglishGame.Creation });
                ImeLayouts.Add(ChineseInput);
            }
            private void Hit(string stage) { if (At != null) At(stage); }
            public int CurrentProcessId { get { return 9999; } }
            public bool TryGetForeground(out GameInputWindow window)
            { ForegroundReads++; Hit("foreground"); window = Foreground; return window != null; }
            public IGameInputProcessLease OpenProcess(int pid, out bool knownGone)
            {
                Hit("open");
                EnglishLife life;
                knownGone = !Lives.TryGetValue(pid, out life);
                if (knownGone || life.Unknown) return null;
                Leases++;
                return new Lease(this, life);
            }
            public IntPtr[] GetKeyboardLayouts() { LayoutLists++; Hit("layouts"); return Layouts; }
            public IntPtr GetKeyboardLayout(uint threadId) { LayoutReads++; Hit("layout"); return Current; }
            public bool IsIme(IntPtr layout) { Hit("ime"); return ImeLayouts.Contains(layout); }
            public GameInputSendResult SendRequest(GameInputWindow window, IntPtr layout,
                uint timeout, Func<bool> mayContinue, out int error)
            {
                Hit("send-entry"); error = 0;
                if (mayContinue == null || !mayContinue()) return GameInputSendResult.NotIssued;
                Sends++; LastRequested = layout; LastTimeout = timeout;
                if (ApplyLayout && SendResult == GameInputSendResult.Processed) Current = layout;
                Hit("sent"); error = SendError;
                return SendResult;
            }
            private sealed class Lease : IGameInputProcessLease
            {
                private readonly EnglishApiFake owner;
                private readonly EnglishLife life;
                private readonly long creation;
                private bool disposed;
                internal Lease(EnglishApiFake owner, EnglishLife life)
                { this.owner = owner; this.life = life; creation = life.Creation; }
                public long Creation { get { return creation; } }
                public bool IsAlive { get { owner.Hit("alive"); return life.Alive; } }
                public void Dispose() { if (!disposed) { disposed = true; owner.Releases++; } }
            }
        }

        private static GameInputWindow EnglishWindow(int pid)
        { return new GameInputWindow(new IntPtr(pid * 10), new IntPtr(pid * 10 + 1), (uint)(pid + 50), pid); }

        private sealed class EnglishFixture
        {
            internal readonly EnglishApiFake Api = new EnglishApiFake();
            internal readonly EnglishInputOnce Once;
            internal long Now;
            internal bool Admitted = true;
            internal int Epoch;
            internal EnglishFixture() { Once = new EnglishInputOnce(new GameInputLanguage(Api), delegate { return Now; }); }
            internal void Begin(bool enabled)
            {
                int epoch = Epoch;
                Once.Begin("game", EnglishGame, enabled, delegate { return Admitted && epoch == Epoch; });
            }
            internal EnglishInputOutcome Step()
            { return Once.Step("game", EnglishGame, delegate { return Admitted; }); }
            internal void Cancel() { Epoch++; Once.Cancel(); }
        }

        private static void EnglishInputOneAttemptAndManualSwitch()
        {
            var f = new EnglishFixture(); f.Begin(true);
            EnglishInputOutcome first = f.Step();
            EnglishCheck(first != null && first.Result == EnglishInputResult.Changed && first.RequestAttempted,
                "successful request was not verified");
            EnglishCheck(f.Api.Sends == 1 && f.Api.LastTimeout == 500 && f.Api.LastRequested == EnglishUs,
                "request was unbounded, repeated, or chose an absent layout");
            int reads = f.Api.LayoutReads, foregroundReads = f.Api.ForegroundReads;
            f.Api.Current = ChineseInput;
            for (int i = 0; i < 20; i++)
            {
                f.Api.Foreground = i % 2 == 0 ? EnglishWindow(202) : EnglishWindow(101);
                f.Now += 500; f.Begin(true); EnglishCheck(f.Step() == null, "AltTab/repeated Begin rearmed");
            }
            EnglishCheck(f.Api.Current == ChineseInput && f.Api.Sends == 1 && f.Api.LayoutReads == reads
                && f.Api.ForegroundReads == foregroundReads, "consumed session kept inspecting/correcting input");
            EnglishCheck(f.Api.Leases == f.Api.Releases, "process lease leaked");
        }

        private static void EnglishInputAlreadyAndMissingLayoutAreTerminal()
        {
            foreach (bool already in new[] { false, true })
            {
                var f = new EnglishFixture();
                f.Api.Current = already ? EnglishUk : ChineseInput;
                f.Api.Layouts = new[] { ChineseInput };
                f.Begin(true);
                EnglishInputOutcome result = f.Step();
                EnglishCheck(result.Result == (already ? EnglishInputResult.AlreadyEnglish : EnglishInputResult.NoEnglishLayout)
                    && !result.RequestAttempted && f.Api.Sends == 0, "non-request terminal state was misreported");
                int reads = f.Api.LayoutReads;
                f.Api.Current = ChineseInput; f.Api.Layouts = new[] { EnglishUs };
                f.Begin(true);
                EnglishCheck(f.Step() == null && f.Api.LayoutReads == reads && f.Api.Sends == 0,
                    "manual switch/layout installation caused a delayed retry");
            }
        }

        private static void EnglishInputLoadedLayoutSelection()
        {
            var f = new EnglishFixture();
            IntPtr englishIme = new IntPtr(unchecked((int)0xe0200409));
            f.Api.ImeLayouts.Add(englishIme);
            f.Api.Layouts = new[] { new IntPtr(0x24002400), englishIme, ChineseInput, EnglishUk, EnglishUs };
            f.Begin(true);
            EnglishCheck(f.Step().Result == EnglishInputResult.Changed && f.Api.LastRequested == EnglishUk,
                "selection assumed US, used transient/IME HKL, or ignored loaded order");
            var empty = new EnglishFixture(); empty.Api.Layouts = new IntPtr[0]; empty.Begin(true);
            EnglishCheck(empty.Step().Result == EnglishInputResult.NoEnglishLayout && empty.Api.Sends == 0,
                "empty loaded list installed a layout or used a private IME fallback");
        }

        private static void EnglishInputForegroundWaitIsBounded()
        {
            var f = new EnglishFixture(); f.Api.Foreground = EnglishWindow(202); f.Begin(true);
            EnglishCheck(f.Step() == null && f.Api.Sends == 0 && f.Api.LayoutReads == 0,
                "another application's input was inspected or changed");
            f.Now = 14999; f.Api.Foreground = EnglishWindow(101);
            EnglishCheck(f.Step().Result == EnglishInputResult.Changed && f.Api.Sends == 1, "first game foreground was missed");
            var late = new EnglishFixture(); late.Api.Foreground = null; late.Begin(true);
            late.Now = 14999; late.Begin(true); EnglishCheck(late.Step() == null, "no foreground should wait briefly");
            late.Now = 15001; late.Api.Foreground = EnglishWindow(101);
            EnglishCheck(late.Step().Result == EnglishInputResult.Expired && late.Api.Sends == 0,
                "repeated Begin reset the 15-second deadline");
            EnglishCheck(late.Step() == null, "expired entry retried later");
        }

        private static void EnglishInputDisabledEntryAndOffOn()
        {
            var f = new EnglishFixture(); f.Begin(false); f.Begin(true);
            EnglishCheck(f.Step() == null && f.Api.Sends == 0, "midgame enable armed the current runtime");
            var pending = new EnglishFixture(); pending.Api.Foreground = EnglishWindow(202); pending.Begin(true);
            EnglishCheck(pending.Step() == null, "pending foreground unexpectedly completed");
            pending.Cancel(); pending.Admitted = false; pending.Admitted = true; pending.Begin(true);
            pending.Api.Foreground = EnglishWindow(101);
            EnglishCheck(pending.Step() == null && pending.Api.Sends == 0, "off/on revived canceled work");
            pending.Once.End(); pending.Begin(true);
            EnglishCheck(pending.Step() == null && pending.Api.Sends == 0, "End/start retried the still-running game");
        }

        private static void EnglishInputInitialRendererWaitIsBounded()
        {
            var f = new EnglishFixture();
            f.Once.Begin("game", new GameInputProcess(), true, delegate { return true; });
            EnglishCheck(f.Once.Step("game", new GameInputProcess(), delegate { return false; }) == null
                && f.Api.ForegroundReads == 0, "unselected renderer was consumed or queried");
            f.Now = 1000;
            EnglishCheck(f.Step().Result == EnglishInputResult.Changed && f.Api.Sends == 1,
                "first verified renderer inside the original deadline could not use the one opportunity");
            var late = new EnglishFixture();
            late.Once.Begin("game", new GameInputProcess(), true, delegate { return true; });
            late.Now = 15001;
            EnglishCheck(late.Step().Result == EnglishInputResult.Expired && late.Api.Sends == 0,
                "late renderer discovery reset the initial wait");
        }

        private static void EnglishInputRendererHandoffAndRedetection()
        {
            var f = new EnglishFixture(); f.Begin(true); EnglishCheck(f.Step() != null, "first attempt missing");
            GameInputProcess second = new GameInputProcess(102, 1002);
            f.Api.Lives.Add(102, new EnglishLife { Creation = 1002 });
            f.Api.Foreground = EnglishWindow(102); f.Api.Current = ChineseInput;
            EnglishCheck(f.Once.Step("game", second, delegate { return true; }) == null, "renderer handoff rearmed");
            f.Api.Lives[101].Alive = false; f.Once.End();
            f.Once.Begin("GAME", second, true, delegate { return true; });
            EnglishCheck(f.Once.Step("game", second, delegate { return true; }) == null && f.Api.Sends == 1,
                "redetection forgot a renderer alias after the original renderer exited");
        }

        private static void EnglishInputProcessReuseAndUnknownHistory()
        {
            var f = new EnglishFixture(); f.Begin(true); f.Step(); f.Once.End();
            f.Api.Lives[101].Alive = false;
            GameInputProcess next = new GameInputProcess(101, 2001);
            f.Api.Lives[101] = new EnglishLife { Creation = 2001 }; f.Api.Current = ChineseInput;
            f.Once.Begin("game", next, true, delegate { return true; });
            EnglishCheck(f.Once.Step("game", next, delegate { return true; }).Result == EnglishInputResult.Changed
                && f.Api.Sends == 2, "a new creation identity could not receive its own opportunity");
            f.Once.End(); f.Api.Lives[101].Unknown = true;
            GameInputProcess candidate = new GameInputProcess(103, 3001);
            f.Api.Lives[103] = new EnglishLife { Creation = 3001 }; f.Api.Foreground = EnglishWindow(103);
            f.Once.Begin("game", candidate, true, delegate { return true; });
            EnglishCheck(f.Once.Step("game", candidate, delegate { return true; }) == null && f.Api.Sends == 2,
                "inaccessible prior runtime was treated as definitely exited");
        }

        private static void EnglishInputHistoryIsBounded()
        {
            var f = new EnglishFixture();
            for (int i = 0; i < 129; i++)
            {
                int pid = 1000 + i;
                f.Api.Lives[pid] = new EnglishLife { Creation = 5000 + i };
                f.Once.Begin("profile" + i, new GameInputProcess(pid, 5000 + i), true, delegate { return true; });
                if (i == 128)
                    EnglishCheck(f.Once.Step("profile128", new GameInputProcess(pid, 5000 + i), delegate { return true; })
                        .Result == EnglishInputResult.HistoryFull, "live history was evicted to permit unsafe repeats");
                f.Once.End();
            }
            EnglishCheck(f.Api.Sends == 0 && f.Api.Leases == f.Api.Releases, "history probing changed input/leaked handles");
            f.Api.Lives.Clear();
            f.Once.Begin("profile128", new GameInputProcess(1128, 5128), true, delegate { return true; });
            EnglishCheck(f.Once.Step("profile128", new GameInputProcess(1128, 5128), delegate { return true; })
                .Result == EnglishInputResult.HistoryFull, "history overflow was forgotten and rearmed an unrecorded runtime");
        }

        private static void EnglishInputRejectedAndUnconfirmedRequests()
        {
            foreach (string failure in new[] { "denied", "timeout", "ignored", "switched-away" })
            {
                var f = new EnglishFixture();
                if (failure == "denied" || failure == "timeout")
                { f.Api.SendResult = GameInputSendResult.Failed; f.Api.SendError = failure == "denied" ? 5 : 1460; }
                if (failure == "ignored") f.Api.ApplyLayout = false;
                if (failure == "switched-away") f.Api.At = delegate(string stage)
                { if (stage == "sent") f.Api.Foreground = EnglishWindow(202); };
                f.Begin(true); EnglishInputOutcome result = f.Step();
                EnglishInputResult expected = failure == "denied" ? EnglishInputResult.Denied
                    : failure == "timeout" ? EnglishInputResult.Unavailable : EnglishInputResult.Unconfirmed;
                EnglishCheck(result.Result == expected && result.RequestAttempted && f.Api.Sends == 1,
                    "message processing was mistaken for successful input change: " + failure);
                f.Api.At = null; f.Api.SendResult = GameInputSendResult.Processed; f.Api.ApplyLayout = true;
                f.Api.Foreground = EnglishWindow(101); f.Api.Current = ChineseInput;
                EnglishCheck(f.Step() == null && f.Api.Sends == 1, "failed request retried: " + failure);
            }
        }

        private static void EnglishInputTargetIdentityChecks()
        {
            foreach (string race in new[] { "creation", "process-exit", "window", "thread", "pid", "self", "protected" })
            {
                var f = new EnglishFixture();
                if (race == "creation") f.Api.Lives[101].Creation++;
                else if (race == "self")
                {
                    var input = new GameInputLanguage(f.Api);
                    EnglishCheck(input.TryOnce(new GameInputProcess(9999, 5), delegate { return true; }, delegate { return true; })
                        .Result == EnglishInputResult.Unavailable && f.Api.Sends == 0, "self-send bypassed bounded timeout guarantee");
                    continue;
                }
                else if (race == "protected") f.Api.Lives[101].Unknown = true;
                else f.Api.At = delegate(string stage)
                {
                    if (stage != "layouts") return;
                    if (race == "process-exit") f.Api.Lives[101].Alive = false;
                    else if (race == "window") f.Api.Foreground = new GameInputWindow(new IntPtr(2010), new IntPtr(2011), 151, 101);
                    else if (race == "thread") f.Api.Foreground = new GameInputWindow(new IntPtr(1010), new IntPtr(1011), 152, 101);
                    else f.Api.Foreground = EnglishWindow(202);
                };
                f.Begin(true); EnglishInputOutcome result = f.Step();
                EnglishCheck(result != null && result.Result != EnglishInputResult.Changed && f.Api.Sends == 0
                    && f.Api.Leases == f.Api.Releases, "identity changed but request escaped: " + race);
                EnglishCheck(f.Step() == null, "identity failure retried: " + race);
            }
        }

        private static void EnglishInputManualChangeDuringPreparation()
        {
            foreach (string stage in new[] { "layouts", "send-entry" })
            {
                var f = new EnglishFixture();
                f.Api.At = delegate(string at) { if (at == stage) f.Api.Current = new IntPtr(0x04110411); };
                f.Begin(true); EnglishInputOutcome result = f.Step();
                EnglishCheck(result != null && f.Api.Sends == 0 && f.Step() == null,
                    "manual input change during preparation was overwritten: " + stage);
            }
        }

        private static void EnglishInputCancelAtNativeBoundaries()
        {
            foreach (string stage in new[] { "foreground", "open", "alive", "layout", "layouts", "ime", "send-entry" })
            {
                var f = new EnglishFixture(); f.Begin(true);
                f.Api.At = delegate(string at) { if (at == stage) f.Cancel(); };
                f.Step();
                EnglishCheck(f.Api.Sends == 0 && f.Api.Leases == f.Api.Releases && f.Step() == null,
                    "canceled native preparation still sent or leaked: " + stage);
            }
        }

        private static void EnglishInputExceptionsReleaseLeases()
        {
            foreach (string stage in new[] { "foreground", "open", "alive", "layout", "layouts", "ime", "send-entry", "sent" })
            {
                var f = new EnglishFixture(); f.Begin(true);
                f.Api.At = delegate(string at) { if (at == stage) throw new IOException("mock input failure"); };
                EnglishInputOutcome result = f.Step();
                EnglishCheck(result != null && result.Result == EnglishInputResult.Unavailable
                    && f.Api.Leases == f.Api.Releases && f.Step() == null,
                    "native failure was retried or leaked a process lease: " + stage);
                EnglishCheck(f.Api.Sends == (stage == "sent" ? 1 : 0), "unexpected native send after exception: " + stage);
            }
        }

        private static void EnglishInputCancelDuringBegin()
        {
            var f = new EnglishFixture();
            f.Once.Begin("game", EnglishGame, true, delegate { f.Once.Cancel(); return true; });
            EnglishCheck(f.Step() == null && f.Api.Sends == 0, "Cancel before session publication was lost");
            var failed = new EnglishFixture();
            failed.Once.Begin("game", EnglishGame, true, delegate { throw new IOException("mock admission failed"); });
            EnglishCheck(failed.Step() == null && failed.Api.Sends == 0, "failed admission did not fail closed");
        }

        private static void EnglishInputDrainAndNoOverlap()
        {
            var f = new EnglishFixture(); f.Begin(true);
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                Exception error = null;
                bool selfDrain = true;
                f.Api.At = delegate(string stage)
                {
                    if (stage != "send-entry") return;
                    selfDrain = f.Once.Drain(0); entered.Set();
                    if (!release.WaitOne(2000)) throw new TimeoutException("mock request was not released");
                };
                Thread worker = new Thread(delegate()
                { try { f.Step(); } catch (Exception e) { error = e; } });
                worker.IsBackground = true; worker.Start();
                try
                {
                    EnglishCheck(entered.WaitOne(2000), "mock native operation never entered");
                    EnglishCheck(f.Step() == null && !f.Once.Drain(0) && !selfDrain, "overlap/reentrant drain succeeded");
                    f.Cancel();
                }
                finally { release.Set(); EnglishCheck(worker.Join(2000), "mock operation did not drain"); }
                EnglishCheck(error == null && f.Once.Drain(0) && f.Api.Sends == 0 && f.Api.Leases == f.Api.Releases,
                    "cancel/drain allowed an unissued request or leaked a lease");
            }
        }

        private static GameMode EnglishUninitializedMode(EnglishFixture f, GameProfile profile)
        {
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            FamilyPolicySetField(mode, "sync", new object());
            FamilyPolicySetField(mode, "profiles", new List<GameProfile> { profile });
            // The constructor only builds a path, this fixture never loads or saves it
            FamilyPolicySetField(mode, "profileStore", new GameProfileStore(Path.GetTempPath()));
            FamilyPolicySetField(mode, "englishInputOnce", f.Once);
            FamilyPolicySetField(mode, "englishInputCreationFloor", 1000L);
            FamilyPolicySetField(mode, "sessionPolicy", PolicyResolver.For(profile));
            FamilyPolicySetField(mode, "activeDetection", EnglishDetection(profile));
            FamilyPolicySetField(mode, "active", true); FamilyPolicySetField(mode, "enabled", true);
            return mode;
        }

        private static GameDetection EnglishDetection(GameProfile profile)
        {
            return new GameDetection {
                Profile = profile, RendererPid = EnglishGame.Pid, RendererCreation = EnglishGame.Creation,
                RendererCandidateSelected = true
            };
        }

        private static void EnglishInputGameModeConsentAndDefaults()
        {
            var f = new EnglishFixture();
            var profile = new GameProfile { Id = "game", Name = "Mock game" };
            GameMode mode = EnglishUninitializedMode(f, profile);
            EnglishCheck(!mode.EnglishInputEnabled && !PolicyResolver.Global().EnglishInput
                && !PolicyResolver.For(profile).EnglishInput, "feature is enabled by default");
            FamilyPolicyInvoke(mode, "BeginEnglishInputSession");
            mode.EnglishInputEnabled = true;
            FamilyPolicyInvoke(mode, "BeginEnglishInputSession"); FamilyPolicyInvoke(mode, "StepEnglishInputSession");
            EnglishCheck(mode.EnglishInputEnabled && Settings.Load(PolicyCatalog.KeyEnglishInput, false)
                && f.Api.Sends == 0 && f.Api.LayoutReads == 0, "saving midgame intent touched input");

            var next = new EnglishFixture();
            var configured = new GameProfile { Id = "game", Name = "Mock game" };
            GameMode inherited = EnglishUninitializedMode(next, configured);
            // A profile without overrides has IsGlobal true in the snapshot
            // but whether the entry has its own choice is still decided by the active profile
            configured.Overrides[PolicyCatalog.KeyEnglishInput] = "1";
            FamilyPolicyInvoke(inherited, "BeginEnglishInputSession"); FamilyPolicyInvoke(inherited, "StepEnglishInputSession");
            EnglishCheck(!inherited.EnglishInputEnabled && next.Api.Sends == 1,
                "empty snapshot's IsGlobal incorrectly ignored a live per-game override");
            next.Api.Current = ChineseInput; inherited.EnglishInputEnabled = false; inherited.EnglishInputEnabled = true;
            FamilyPolicyInvoke(inherited, "BeginEnglishInputSession"); FamilyPolicyInvoke(inherited, "StepEnglishInputSession");
            EnglishCheck(next.Api.Sends == 1 && next.Api.Current == ChineseInput, "global off/on repeated the game request");
        }

        private static void EnglishInputGameModeAdmissionBoundaries()
        {
            foreach (string boundary in new[] { "stopping", "panicReq", "active", "enabled", "standbyCleanerRestorePending",
                "profileSaveFailureSignaled", "stickyGraceOnly", "gameGoneSinceTicks", "renderer", "profile" })
            {
                Settings.UseTransientStoreForCurrentProcess();
                var f = new EnglishFixture();
                var profile = new GameProfile { Id = "game", Name = "Mock game" };
                GameMode mode = EnglishUninitializedMode(f, profile);
                mode.EnglishInputEnabled = true; FamilyPolicyInvoke(mode, "BeginEnglishInputSession");
                f.Api.At = delegate(string at)
                {
                    if (at != "layouts") return;
                    if (boundary == "renderer")
                    {
                        GameDetection changed = EnglishDetection(profile); changed.RendererCreation++;
                        FamilyPolicySetField(mode, "activeDetection", changed);
                    }
                    else if (boundary == "profile")
                        FamilyPolicySetField(mode, "activeDetection", EnglishDetection(new GameProfile { Id = "other" }));
                    else if (boundary == "gameGoneSinceTicks") FamilyPolicySetField(mode, boundary, 1L);
                    else if (boundary == "standbyCleanerRestorePending" || boundary == "profileSaveFailureSignaled")
                        FamilyPolicySetField(mode, boundary, 1);
                    else FamilyPolicySetField(mode, boundary, boundary != "active" && boundary != "enabled");
                };
                FamilyPolicyInvoke(mode, "StepEnglishInputSession");
                EnglishCheck(f.Api.Sends == 0 && f.Api.Leases == f.Api.Releases,
                    "GameMode admission ignored cancellation boundary " + boundary);
            }
        }

        private static void EnglishInputGameModeStartupFloor()
        {
            var profile = new GameProfile { Id = "game", Name = "Mock game" };
            var beforeStartup = new EnglishFixture();
            GameMode latePavise = EnglishUninitializedMode(beforeStartup, profile);
            FamilyPolicySetField(latePavise, "englishInputCreationFloor", 2000L);
            latePavise.EnglishInputEnabled = true;
            FamilyPolicyInvoke(latePavise, "BeginEnglishInputSession");
            FamilyPolicyInvoke(latePavise, "StepEnglishInputSession");
            EnglishCheck(beforeStartup.Api.Sends == 0 && beforeStartup.Api.ForegroundReads == 0,
                "a game running before Pavise startup received an input request");

            var firstRun = new EnglishFixture();
            GameMode firstMode = EnglishUninitializedMode(firstRun, profile);
            firstMode.EnglishInputEnabled = true;
            FamilyPolicyInvoke(firstMode, "BeginEnglishInputSession"); FamilyPolicyInvoke(firstMode, "StepEnglishInputSession");
            EnglishCheck(firstRun.Api.Sends == 1, "fresh game after Pavise startup was not eligible");
            firstRun.Api.Current = ChineseInput;
            var restarted = new EnglishFixture(); // Same PID/creation new Pavise and empty in-memory history
            GameMode restartedMode = EnglishUninitializedMode(restarted, profile);
            FamilyPolicySetField(restartedMode, "englishInputCreationFloor", 2000L);
            restartedMode.EnglishInputEnabled = true;
            FamilyPolicyInvoke(restartedMode, "BeginEnglishInputSession");
            FamilyPolicyInvoke(restartedMode, "StepEnglishInputSession");
            EnglishCheck(restarted.Api.Sends == 0 && restarted.Api.Current == ChineseInput
                && restarted.Api.LayoutReads == 0, "restarting Pavise repeated the same running game's switch");

            foreach (bool fresh in new[] { false, true })
            {
                var delayed = new EnglishFixture();
                GameMode delayedMode = EnglishUninitializedMode(delayed, profile);
                FamilyPolicySetField(delayedMode, "englishInputCreationFloor", fresh ? 1000L : 2000L);
                GameDetection pending = EnglishDetection(profile); pending.RendererCandidateSelected = false;
                FamilyPolicySetField(delayedMode, "activeDetection", pending);
                delayedMode.EnglishInputEnabled = true;
                FamilyPolicyInvoke(delayedMode, "BeginEnglishInputSession");
                FamilyPolicyInvoke(delayedMode, "StepEnglishInputSession");
                EnglishCheck(delayed.Api.ForegroundReads == 0, "unverified initial renderer queried input");
                FamilyPolicySetField(delayedMode, "activeDetection", EnglishDetection(profile));
                FamilyPolicyInvoke(delayedMode, "StepEnglishInputSession");
                EnglishCheck(delayed.Api.Sends == (fresh ? 1 : 0), "late selected renderer bypassed the creation floor");
                if (!fresh)
                {
                    GameDetection replacement = EnglishDetection(profile); replacement.RendererCreation = 3000;
                    FamilyPolicySetField(delayedMode, "activeDetection", replacement);
                    delayed.Api.Lives[EnglishGame.Pid].Creation = 3000;
                    FamilyPolicyInvoke(delayedMode, "BeginEnglishInputSession");
                    FamilyPolicyInvoke(delayedMode, "StepEnglishInputSession");
                    EnglishCheck(delayed.Api.Sends == 0, "old runtime's skipped opportunity was rearmed by a later renderer");
                }
            }

            var uninitialized = new EnglishFixture();
            GameMode uninitializedMode = EnglishUninitializedMode(uninitialized, profile);
            FamilyPolicySetField(uninitializedMode, "englishInputCreationFloor", 0L);
            uninitializedMode.EnglishInputEnabled = true;
            FamilyPolicyInvoke(uninitializedMode, "BeginEnglishInputSession");
            FamilyPolicyInvoke(uninitializedMode, "StepEnglishInputSession");
            EnglishCheck(uninitialized.Api.Sends == 0, "missing startup identity did not fail closed");
            long before = DateTime.UtcNow.ToFileTimeUtc();
            FamilyPolicyInvoke(uninitializedMode, "InitializeEnglishInput");
            long after = DateTime.UtcNow.ToFileTimeUtc();
            long recorded = (long)FamilyPolicyGetField(uninitializedMode, "englishInputCreationFloor");
            EnglishCheck(recorded >= before && recorded <= after,
                "Initialize did not capture the startup lower bound in UTC FILETIME units");
        }

        private static void EnglishInputGameModeSaveFailure()
        {
            var f = new EnglishFixture();
            GameMode mode = EnglishUninitializedMode(f, new GameProfile { Id = "game", Name = "Mock game" });
            mode.EnglishInputEnabled = true; FamilyPolicyInvoke(mode, "BeginEnglishInputSession");
            Settings.SuspendWritesForReset(); mode.EnglishInputEnabled = false; mode.EnglishInputEnabled = true;
            FamilyPolicyInvoke(mode, "StepEnglishInputSession");
            EnglishCheck(!mode.EnglishInputEnabled && f.Api.Sends == 0,
                "failed save granted consent or revived canceled work");
            EnglishCheck((int)FamilyPolicyGetField(mode, "englishInputGeneration") == 3,
                "a failed/off-on save did not invalidate each old token");
            Settings.UseTransientStoreForCurrentProcess();
        }

        private static void EnglishInputLibraryChangesCannotRearm()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseEnglishInput-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (string change in new[] { "off-on", "inherit", "clear-all", "remove" })
                    using (var library = new FamilyPolicyFixture(root, change))
                    {
                        var f = new EnglishFixture();
                        FamilyPolicySetField(library.Mode, "englishInputOnce", f.Once);
                        FamilyPolicySetField(library.Mode, "englishInputCreationFloor", 1000L);
                        // The unrelated observation writes on removal are not this input test's concern
                        // Do not push filesystem worker threads onto the queue
                        FamilyPolicySetField(library.Mode, "rendererObservations", null);
                        library.Mode.EnglishInputEnabled = true;
                        EnglishCheck(library.Mode.SetProfileOverride("first", PolicyCatalog.KeyEnglishInput, "1"),
                            "fixture could not set per-game consent");
                        GameProfile profile = library.Current("first");
                        FamilyPolicySetField(library.Mode, "sessionPolicy", PolicyResolver.For(profile));
                        FamilyPolicySetField(library.Mode, "activeDetection", EnglishDetection(profile));
                        FamilyPolicySetField(library.Mode, "active", true); FamilyPolicySetField(library.Mode, "enabled", true);
                        FamilyPolicyInvoke(library.Mode, "BeginEnglishInputSession");
                        int generation = (int)FamilyPolicyGetField(library.Mode, "englishInputGeneration");
                        if (change == "off-on")
                        {
                            EnglishCheck(library.Mode.SetProfileOverride("first", PolicyCatalog.KeyEnglishInput, "0")
                                && library.Mode.SetProfileOverride("first", PolicyCatalog.KeyEnglishInput, "1"), "override off/on failed");
                        }
                        else if (change == "inherit") EnglishCheck(library.Mode.ClearProfileOverride("first", PolicyCatalog.KeyEnglishInput),
                            "clear override failed");
                        else if (change == "clear-all") EnglishCheck(library.Mode.ClearProfileOverrides("first") > 0, "clear-all failed");
                        else library.Mode.RemoveProfile("first");
                        FamilyPolicyInvoke(library.Mode, "StepEnglishInputSession");
                        EnglishCheck((int)FamilyPolicyGetField(library.Mode, "englishInputGeneration") > generation
                            && f.Api.Sends == 0, "library change revived pending input: " + change);
                        EnglishCheck(FamilyPolicyGetField(library.Mode, "worker") == null,
                            "isolated policy test started the application worker");
                    }
            }
            finally
            {
                string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (actual.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(actual).StartsWith("PaviseEnglishInput-", StringComparison.Ordinal)
                    && Directory.Exists(actual)) Directory.Delete(actual, true);
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
    }
}
#endif
