#if PAVISE_SELFTEST && PAVISE_DISPLAY_AWAKE_RUNNER
using System;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Display-awake regression cannot start the application runtime."); }
    }

    internal static class DisplayAwakeRegressionRunner
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks;
        private static long now;
        private static string output;

        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            try
            {
                Lang.Init(); Lang.Cur = 0;
                NativeEdgesAndFailures();
                AutomaticFocusChangesStayQuiet();
                WarningsAndRecoveryAreNotThrottled();
                FinalReleaseAndSessionReset();
                OtherAutomationKeepsItsLogging();
                Console.WriteLine("PASS display-awake assertions=" + checks
                    + " native_power=mocked settings=transient application_started=false windows_shown=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { DisplayAwake.ResetForTest(); Logger.WritesEnabled = false; }
        }

        private static void Check(bool good, string message)
        { if (!good) throw new InvalidOperationException(message); checks++; }
        private static object Call(GameMode mode, string name, params object[] values)
        { return typeof(GameMode).GetMethod(name, Hidden).Invoke(mode, values); }
        private static string[] Lines()
        { return File.Exists(Logger.LogPath) ? File.ReadAllLines(Logger.LogPath) : new string[0]; }
        private static int Count(string fragment)
        { return Array.FindAll(Lines(), delegate(string line) { return line.Contains(fragment); }).Length; }
        private static string AwakeState(string state)
        { return GameMode.AutomationActionLabel("awake") + " | " + GameMode.AutomationStateLabel(state); }
        private static void Prepare(string name, bool automatic)
        {
            // 控制方式由 CalibrationRequestedV1 表达；旧 AutomaticPolicyV1 启动时迁移为手动。
            Settings.UseTransientStoreForCurrentProcess(); Settings.Save(CalibrationRuntime.RequestedKey, automatic);
            DisplayAwake.ResetForTest(); Logger.ResetWriteBarrierForTest();
            Logger.LogPath = Path.Combine(output, name + ".log"); now = new DateTime(2026, 10, 10).Ticks;
        }
        private static GameMode Mode(string name)
        {
            Logger.WritesEnabled = false;
            string dir = Path.Combine(output, name); Directory.CreateDirectory(dir);
            GameMode mode;
            using (GameInstallScope.UseSnapshotForTest(new GameInstallRecord[0], new string[0]))
                mode = new GameMode(dir, new SuppressionCore());
            mode.AutomationLogNowForTest = delegate { return now; };
            typeof(GameMode).GetField("activeGame", Hidden).SetValue(mode, name);
            Logger.WritesEnabled = true; Call(mode, "BeginAutomationEvidence");
            return mode;
        }
        // 走生产的对局路径（StepDisplayAwake），不在测试里另选激活/恢复委托。
        private static bool Step(GameMode mode, bool want, bool active)
        {
            typeof(GameMode).GetField("awakeActive", Hidden).SetValue(mode, active);
            return (bool)Call(mode, "StepDisplayAwake", want);
        }

        private static void NativeEdgesAndFailures()
        {
            Prepare("manual", false);
            int calls = 0; bool success = true;
            DisplayAwake.ExecutionStateForTest = delegate(uint flags)
            {
                calls++; Check(flags == 0x80000003 || flags == 0x80000000, "Unexpected power flags");
                return success ? 1U : 0U;
            };
            for (int i = 0; i < 100; i++) Check(DisplayAwake.Activate(), "Steady activation failed");
            Check(calls == 1 && Count(Lang.T("log.displayawake.1")) == 1, "Steady activation reissues or repeats logs");
            for (int i = 0; i < 100; i++) Check(DisplayAwake.Restore(), "Steady restoration failed");
            Check(calls == 2 && Count(Lang.T("log.displayawake.2")) == 1, "Steady restoration reissues or repeats logs");
            success = false; Check(!DisplayAwake.Activate(), "Failed activation was reported successful");
            Check(Count(Lang.T("log.displayawake.1")) == 1, "Failed activation emitted success");
            success = true; Check(DisplayAwake.Activate(), "Activation retry failed");
            success = false; Check(!DisplayAwake.Restore(), "Failed restore was reported successful");
            Check(Count(Lang.T("log.displayawake.2")) == 1, "Failed restore emitted success");
            int before = calls; Check(DisplayAwake.Activate() && calls == before, "Failed restore discarded active state");
            success = true; Check(DisplayAwake.Restore(), "Restoration retry failed");
            Check(Count(Lang.T("log.displayawake.1")) == 2 && Count(Lang.T("log.displayawake.2")) == 2,
                "Manual transitions lost their edge records");
            DisplayAwake.ResetForTest(); bool rejected = false;
            try { DisplayAwake.Activate(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unmocked test invocation reached the operating system");
        }

        private static void AutomaticFocusChangesStayQuiet()
        {
            // 执行记录在两种控制方式下都写入，焦点切换不能再叠加逐次原始日志。
            foreach (bool automatic in new[] { false, true }) FocusChangesStayQuiet(automatic);
        }

        private static void FocusChangesStayQuiet(bool automatic)
        {
            Prepare("focus-" + automatic, automatic); var mode = Mode("focus-mode-" + automatic); int nativeCalls = 0;
            DisplayAwake.ExecutionStateForTest = delegate(uint flags) { nativeCalls++; return 1; };
            bool active = false;
            for (int i = 0; i < 1000; i++)
            {
                now += TimeSpan.TicksPerMillisecond;
                active = Step(mode, true, active);
                Check(active && mode.AutomaticExecutionState(PolicyCatalog.KeyAwake) == "applied", "Quiet activation lost real state");
                active = Step(mode, false, active);
                Check(!active && mode.AutomaticExecutionState(PolicyCatalog.KeyAwake) == "restored", "Quiet release lost real state");
            }
            Check(nativeCalls == 2000, "Log throttling altered native focus transitions");
            Check(Count(AwakeState("applied")) == 1 && Count(AwakeState("restored")) == 1,
                "Rapid focus changes still spam success records");
            Check(Count(Lang.T("log.displayawake.1")) == 0 && Count(Lang.T("log.displayawake.2")) == 0,
                "Session focus changes duplicated raw and structured logs (automatic=" + automatic + ")");
            now += TimeSpan.TicksPerMinute;
            active = Step(mode, true, active); active = Step(mode, false, active);
            Check(Count(AwakeState("applied")) == 2 && Count(AwakeState("restored")) == 2,
                "Normal transitions never resumed after cooldown");
            Check(Count("WARN ") == 0, "Normal focus changes fabricated warnings");
        }

        private static void WarningsAndRecoveryAreNotThrottled()
        {
            Prepare("warnings", true); var mode = Mode("warnings-mode"); bool success = true;
            DisplayAwake.ExecutionStateForTest = delegate(uint flags) { return success ? 1U : 0U; };
            bool active = Step(mode, true, false); active = Step(mode, false, active);
            success = false; active = Step(mode, true, active);
            Check(!active && Count("WARN AUTO ") == 1 && Count(AwakeState("failed")) == 1,
                "Activation failure vanished under normal throttle");
            // 失败的应用会被持久隔离；恢复须由用户在记录中重试（一次性许可），不能靠清除重试计时绕过。
            success = true; mode.ClearEnvRetryStateForTest(); active = Step(mode, true, active);
            Check(!active && Count(AwakeState("applied")) == 1, "A persistently isolated activation retried without user permission");
            string awakeAction = (string)typeof(GameMode).GetMethod("EnvFailureAction", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "awake" });
            Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(awakeAction, "system")), "Isolated awake activation could not be granted a retry");
            active = Step(mode, true, active);
            Check(active, "Granted retry did not reactivate keep-awake");
            Check(Count(AwakeState("applied")) == 2, "First activation recovery was throttled");
            success = false; active = Step(mode, false, active);
            Check(active && Count("WARN AUTO ") == 2 && Count(AwakeState("restore-failed")) == 1,
                "Restore failure lost its warning or active state");
            success = true; mode.ClearEnvRetryStateForTest(); active = Step(mode, false, active);
            Check(!active && Count(AwakeState("restored")) == 2, "First restoration recovery was throttled");
            Call(mode, "RecordAutomation", "awake", "failed"); Call(mode, "RecordAutomation", "awake", "failed");
            Check(Count(AwakeState("failed")) == 2, "Identical repeated warning was duplicated or first warning missing");
        }

        private static void FinalReleaseAndSessionReset()
        {
            Prepare("final", true); var mode = Mode("final-mode");
            DisplayAwake.ExecutionStateForTest = delegate(uint flags) { return 1; };
            Check((bool)Call(mode, "RestoreDisplayAwake"), "Unused restore failed");
            Check(Count(AwakeState("restored")) == 0, "Never-enabled session invented a release log");
            Call(mode, "RecordAutomation", "awake", "unchanged");
            Call(mode, "RestoreDisplayAwake");
            Check(Count(AwakeState("restored")) == 0, "Disabled awake policy invented a release log");
            bool active = Step(mode, true, false); active = Step(mode, false, active);
            Call(mode, "EndAutomationEvidence");
            Check((bool)Call(mode, "RestoreDisplayAwake"), "Final release failed");
            Check(Count(AwakeState("restored")) == 2, "Already-backgrounded game lost final release record");
            Call(mode, "RestoreDisplayAwake"); Call(mode, "EndAutomationEvidence");
            Check(Count(AwakeState("restored")) == 2, "Repeated final cleanup duplicated release");
            Call(mode, "RecordAutomation", "awake", "restore-failed");
            Call(mode, "RecordAutomation", "awake", "restored");
            Check(Count(AwakeState("restored")) == 3, "Late final recovery was muted");
            string first = mode.AutomationSession; Call(mode, "BeginAutomationEvidence");
            Check(mode.AutomationSession != first, "Session identity failed to reset");
            active = Step(mode, true, false); active = Step(mode, false, active);
            Check(Count(AwakeState("applied")) == 2 && Count(AwakeState("restored")) == 4,
                "A new game inherited the previous game cooldown");
        }

        private static void OtherAutomationKeepsItsLogging()
        {
            Prepare("others", true); var mode = Mode("other-mode");
            for (int i = 0; i < 50; i++)
            { Call(mode, "RecordAutomation", "dwmboost", "applied"); Call(mode, "RecordAutomation", "dwmboost", "restored"); }
            Check(Count(GameMode.AutomationActionLabel("dwmboost") + " | ") == 100,
                "Awake-specific throttle changed unrelated automation evidence");
            Check(!mode.IsActive, "Logging-only test started the runtime");
        }
    }
}
#endif
