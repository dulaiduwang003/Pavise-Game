#if PAVISE_SELFTEST && PAVISE_THREAD_BOOST_FALLBACK_CHECKS
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

namespace PaviseApp
{
    internal static partial class SelfTests
    { internal static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Thread boost fallback checks never start the application"); } }

    // 线程提优只有一个开关：渲染关键线程先行，认不出画面线程的游戏才交给最忙线程。
    // 只用临时设置与未初始化的 GameMode，不打开任何进程或线程，不写系统。
    internal static class ThreadBoostFallbackChecks
    {
        private const int Pid = 5151;
        private const long Birth = 133100000000000500;
        private const string Game = "fallback-fixture.exe", Isolated = "isolated-fixture.exe";
        private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        private static int checks;

        private static void Check(bool good, string message)
        {
            checks++;
            if (!good) throw new InvalidOperationException("FAIL " + message);
        }

        private static void Selection()
        {
            Check(ThreadBoostSelection.Choices.Length == 2 && ThreadBoostSelection.Choices[0] == ThreadBoostSelection.Off
                && ThreadBoostSelection.Choices[1] == ThreadBoostSelection.On, "the thread boost does not offer exactly Off and On");
            foreach (string value in new[] { ThreadBoostSelection.On, ThreadBoostSelection.Candidate, ThreadBoostSelection.Frame })
                Check(ThreadBoostSelection.Valid(value) && ThreadBoostSelection.IsOn(value)
                    && ThreadBoostSelection.Normalize(value) == ThreadBoostSelection.On, value + " does not read as on");
            Check(ThreadBoostSelection.Valid(ThreadBoostSelection.Off) && !ThreadBoostSelection.IsOn(ThreadBoostSelection.Off), "off reads as on");
            Check(!ThreadBoostSelection.Valid("lane") && !ThreadBoostSelection.Valid(null)
                && !ThreadBoostSelection.IsOn("lane") && !ThreadBoostSelection.IsOn(null), "an unknown choice was accepted or read as on");
            foreach (string legacy in new[] { ThreadBoostSelection.Candidate, ThreadBoostSelection.Frame })
            {
                Check(ThreadBoostSelection.Save(legacy) && Settings.LoadStr(ThreadBoostSelection.Key, "") == ThreadBoostSelection.On,
                    "saving the legacy " + legacy + " choice did not store on");
                Check(Settings.SaveStr(ThreadBoostSelection.Key, legacy) && ThreadBoostSelection.RawGlobal == ThreadBoostSelection.On,
                    "a stored legacy " + legacy + " choice did not read as on");
            }
            Check(!ThreadBoostSelection.Save("lane"), "an unknown choice was saved");
            Check(ThreadBoostSelection.LegacyValue(true, false) == ThreadBoostSelection.On
                && ThreadBoostSelection.LegacyValue(false, true) == ThreadBoostSelection.On
                && ThreadBoostSelection.LegacyValue(false, false) == ThreadBoostSelection.Off, "legacy flags were not merged into one switch");
            var overrides = new Dictionary<string, string> { { PolicyCatalog.KeyFrameCoordination, "1" }, { PolicyCatalog.KeyRenderLane, "0" } };
            Check(ThreadBoostSelection.MigrateOverrides(overrides) && overrides[ThreadBoostSelection.Key] == ThreadBoostSelection.On
                && !overrides.ContainsKey(PolicyCatalog.KeyFrameCoordination) && !overrides.ContainsKey(PolicyCatalog.KeyRenderLane),
                "per-game legacy flags were not migrated to the single switch");
            Check(PolicyCatalog.Canonical(PolicyCatalog.KeyThreadBoost, ThreadBoostSelection.Candidate) == ThreadBoostSelection.On
                && PolicyCatalog.Canonical(PolicyCatalog.KeyThreadBoost, ThreadBoostSelection.Frame) == ThreadBoostSelection.On
                && PolicyCatalog.Canonical(PolicyCatalog.KeyThreadBoost, "OFF") == ThreadBoostSelection.Off,
                "the policy catalog did not canonicalize thread boost choices");
            Check(ThreadBoostSelection.Title(0) != ThreadBoostSelection.Title(1), "the two thread boost labels are identical");
        }

        private static void Policy()
        {
            foreach (string value in new[] { ThreadBoostSelection.On, ThreadBoostSelection.Off })
            {
                Check(ThreadBoostSelection.Save(value), "the thread boost choice was not saved");
                PolicySnapshot policy = PolicyResolver.Global();
                bool on = value == ThreadBoostSelection.On;
                Check(policy.ThreadBoostOn == on && policy.FrameCoordinationOn == on && policy.RenderLane == on && policy.EffLane == on,
                    "the global policy did not follow the single switch: " + value);
            }
            var profile = new GameProfile { Id = "fallback-profile", Name = "Fallback" };
            profile.Overrides[ThreadBoostSelection.Key] = ThreadBoostSelection.Frame;
            Check(PolicyResolver.For(profile).ThreadBoostOn, "a per-game legacy frame override did not read as on");
            profile.Overrides[ThreadBoostSelection.Key] = ThreadBoostSelection.Off;
            Check(ThreadBoostSelection.Save(ThreadBoostSelection.On) && !PolicyResolver.For(profile).ThreadBoostOn,
                "a per-game Off override did not turn the thread boost off");
        }

        private static FrameCaptureSession Session(int pid)
        {
            FrameCaptureSession session = FrameCoordination.CaptureSessionForTest(pid, Birth);
            Check(session != null && session.Pid == pid && !session.Stopped, "the capture session fixture could not be created");
            return session;
        }

        private static void Exhaust(FrameCaptureSession session)
        {
            session.Failures = FrameCoordination.MaxCaptureFailures;
            session.Stopped = true;
        }

        private static void Exhaustion()
        {
            FrameCoordination.ResetCaptureSessionsForTest();
            try
            {
                Check(!FrameCoordination.IdentificationExhausted(Pid, Birth, Game), "a game without a capture session counted as exhausted");
                FrameCaptureSession session = Session(Pid);
                Check(!FrameCoordination.IdentificationExhausted(Pid, Birth, Game), "a capture still in progress counted as exhausted");
                Exhaust(session);
                Check(FrameCoordination.IdentificationExhausted(Pid, Birth, Game),
                    "a session that could not identify the presenter did not count as exhausted");
                Check(!FrameCoordination.IdentificationExhausted(Pid, Birth + 1, Game) && !FrameCoordination.IdentificationExhausted(Pid + 1, Birth, Game),
                    "exhaustion leaked to another process or a reused PID");
                Check(!FrameCoordination.IdentificationExhausted(Pid, Birth, "") && !FrameCoordination.IdentificationExhausted(0, Birth, Game)
                    && !FrameCoordination.IdentificationExhausted(Pid, 0, Game), "an incomplete identity counted as exhausted");
                session.NoChangeNeeded = true;
                Check(!FrameCoordination.IdentificationExhausted(Pid, Birth, Game), "game-managed thread priorities counted as unidentifiable");
                session.NoChangeNeeded = false;
                FieldInfo storeFailed = typeof(FrameCoordination).GetField("captureSessionStoreFailed", Static);
                Check(storeFailed != null, "FrameCoordination has no capture store failure flag");
                storeFailed.SetValue(null, true);
                Check(!FrameCoordination.IdentificationExhausted(Pid, Birth, Game), "an unreadable capture store handed the game to another writer");
                storeFailed.SetValue(null, false);
                Exhaust(Session(Pid + 7));
                ActionFailureStore.Trip(FrameCoordination.ActionKey, Isolated, "fixture failure");
                Check(!FrameCoordination.IdentificationExhausted(Pid + 7, Birth, Isolated),
                    "an isolated frame action handed the game to another priority writer");
                Check(FrameCoordination.IdentificationExhausted(Pid, Birth, Game), "isolating one game changed another game's exhaustion");
            }
            finally { FrameCoordination.ResetCaptureSessionsForTest(); }
        }

        private static GameMode Mode()
        {
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            typeof(GameMode).GetField("sync", Instance).SetValue(mode, new object());
            // 提优目标变化时会清理这两份按进程记录的状态。
            foreach (string name in new[] { "boostStateVerified", "gameBoostNextAudit" })
            {
                FieldInfo field = typeof(GameMode).GetField(name, Instance);
                if (field == null) throw new InvalidOperationException("FAIL GameMode has no " + name);
                field.SetValue(mode, Activator.CreateInstance(field.FieldType));
            }
            return mode;
        }

        private static object Call(GameMode mode, string method, params object[] args)
        {
            MethodInfo info = typeof(GameMode).GetMethod(method, Instance);
            if (info == null) throw new InvalidOperationException("FAIL GameMode has no " + method);
            try { return info.Invoke(mode, args); }
            catch (TargetInvocationException error) { throw new InvalidOperationException("FAIL " + method + " threw", error.InnerException); }
        }

        private static bool Fallback(GameMode mode, int pid, long birth) { return (bool)Call(mode, "LaneFallbackFor", pid, birth); }

        private static object Pass(int pid, long birth, string name)
        {
            Type type = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
            object pass = Activator.CreateInstance(type, true);
            type.GetField("RendererPid").SetValue(pass, pid);
            type.GetField("RendererCreation").SetValue(pass, birth);
            type.GetField("RendererName").SetValue(pass, name);
            return pass;
        }

        private static T PassField<T>(object pass, string name) { return (T)pass.GetType().GetField(name).GetValue(pass); }

        private static void Decision()
        {
            FrameCoordination.ResetCaptureSessionsForTest();
            try
            {
                GameMode mode = Mode();
                FrameCaptureSession session = Session(Pid);
                Exhaust(session);
                Check(ThreadBoostSelection.Save(ThreadBoostSelection.Off), "the thread boost choice was not saved");
                Call(mode, "UpdateLaneFallback", Pid, Birth, Game);
                Check(!Fallback(mode, Pid, Birth), "thread boost off still handed a game to the busiest thread");
                Check(ThreadBoostSelection.Save(ThreadBoostSelection.On), "the thread boost choice was not saved");
                Call(mode, "UpdateLaneFallback", Pid, Birth, Game);
                Check(Fallback(mode, Pid, Birth), "an unidentifiable game was not handed to the busiest thread");
                Check(!Fallback(mode, Pid, Birth + 1) && !Fallback(mode, Pid + 1, Birth) && !Fallback(mode, 0, 0),
                    "the fallback applied to another renderer");
                Call(mode, "UpdateLaneFallback", Pid + 1, Birth, Game);
                Check(!Fallback(mode, Pid, Birth) && !Fallback(mode, Pid + 1, Birth), "a new game session inherited the previous fallback");
                session.NoChangeNeeded = true;
                Call(mode, "UpdateLaneFallback", Pid, Birth, Game);
                Check(!Fallback(mode, Pid, Birth), "game-managed thread priorities were handed to the busiest thread");
                session.NoChangeNeeded = false;

                // 每轮提优的同步入口本身做这一判定。
                object pass = Pass(Pid, Birth, Game);
                Call(mode, "SyncFrameCoordination", pass);
                Check(Fallback(mode, Pid, Birth) && PassField<bool>(pass, "LaneRestorationReady"),
                    "the boost pass did not hand the unidentifiable game to the busiest thread");
                FrameCoordination.ResetCaptureSessionsForTest();
                pass = Pass(Pid, Birth, Game);
                Call(mode, "SyncFrameCoordination", pass);
                Check(!Fallback(mode, Pid, Birth), "the boost pass kept a fallback after the presenter became identifiable again");
            }
            finally { FrameCoordination.ResetCaptureSessionsForTest(); }
        }

        private static void Admission()
        {
            Func<PerformancePreset, bool> oldLane = GameMode.LaneSupportedForTest;
            GameMode.LaneSupportedForTest = delegate { return true; };
            FrameCoordination.ResetCaptureSessionsForTest();
            try
            {
                Check(ThreadBoostSelection.Save(ThreadBoostSelection.On) && Settings.Save(PolicyCatalog.KeyBoost, true), "policy fixture could not be saved");
                GameMode mode = Mode();
                typeof(GameMode).GetField("sessionPolicy", Instance).SetValue(mode, PolicyResolver.Global());
                typeof(GameMode).GetField("boostIdentityPid", Instance).SetValue(mode, Pid);
                typeof(GameMode).GetField("boostIdentityCreation", Instance).SetValue(mode, Birth);
                object pass = Pass(Pid, Birth, Game);
                Check((bool)Call(mode, "RefreshBoostPriority", pass) && !PassField<bool>(pass, "LaneAllowed"),
                    "the busiest-thread boost was admitted while the presenter path still owns the game");
                Exhaust(Session(Pid));
                Call(mode, "UpdateLaneFallback", Pid, Birth, Game);
                Check((bool)Call(mode, "RefreshBoostPriority", pass) && PassField<bool>(pass, "LaneAllowed"),
                    "the busiest-thread boost was not admitted after the presenter could not be identified");
                pass.GetType().GetField("WriteDenied").SetValue(pass, true);
                Check((bool)Call(mode, "RefreshBoostPriority", pass) && !PassField<bool>(pass, "LaneAllowed"),
                    "the busiest-thread boost was admitted on a game that denies writes");
                pass.GetType().GetField("WriteDenied").SetValue(pass, false);
                GameMode.LaneSupportedForTest = delegate { return false; };
                Check((bool)Call(mode, "RefreshBoostPriority", pass) && !PassField<bool>(pass, "LaneAllowed"),
                    "the busiest-thread boost ignored its eligibility");
                GameMode.LaneSupportedForTest = delegate { return true; };
                Check(ThreadBoostSelection.Save(ThreadBoostSelection.Off), "the thread boost choice was not saved");
                typeof(GameMode).GetField("sessionPolicy", Instance).SetValue(mode, PolicyResolver.Global());
                Check((bool)Call(mode, "RefreshBoostPriority", pass) && !PassField<bool>(pass, "LaneAllowed"),
                    "the busiest-thread boost ran with thread boost off");
            }
            finally
            {
                GameMode.LaneSupportedForTest = oldLane;
                FrameCoordination.ResetCaptureSessionsForTest();
            }
        }

        private static int Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                Selection();
                Policy();
                Exhaustion();
                Decision();
                Admission();
                Console.WriteLine("PASS thread boost fallback checks=" + checks + "; transient settings, no process or thread opened");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
