#if PAVISE_SELFTEST && PAVISE_ACTION_FAILURE_RUNNER
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static readonly string[] IsolationBoostActions =
            { GameMode.GamePriorityAction, GameMode.GameIoAction, GameMode.GameGpuAction, GameMode.GameMemoryAction, GameMode.GameEcoAction };
        private const BindingFlags IsolationBoostFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static void RunBoostFailureIsolationChecks()
        {
            try
            {
                BoostRetryReadBudget();
                BoostResourceOptions();
                BoostResourceToggleRestores();
                BoostMemoryStageIsolation();
                BoostScheduledAuditWithoutRetry();
                BoostFirstFaultAndTargetIsolation();
                BoostSkipAndQueryBoundaries();
                BoostRetryRestoresOriginals();
                BoostCompletionPersistenceRollback();
                BoostRecoveryRemainsAllowed();
                BoostNvRetryReopensOnlyDispatch();
            }
            finally { ResetIsolationBoostHooks(); }
        }

        private static void BoostResourceOptions()
        {
            Fresh();
            string[] keys = { PolicyCatalog.KeyBoostCpu, PolicyCatalog.KeyBoostMemory, PolicyCatalog.KeyBoostIo, PolicyCatalog.KeyBoostGpu };
            try
            {
                AutomaticPolicy.RequestedForTest = delegate { return false; };
                foreach (string key in keys)
                {
                    Check(PolicyResolver.Global().ValueOf(key) == "1", "resource option lost its initial bundle membership");
                    Settings.Save(key, false);
                    Check(PolicyResolver.Global().ValueOf(key) == "1", "legacy resource preference split the unified bundle");
                }
                Settings.Save(PolicyCatalog.KeyBoost, false);
                foreach (string key in keys) Check(PolicyResolver.Global().ValueOf(key) == "0", "resource ignored the master switch");
                AutomaticPolicy.RequestedForTest = delegate { return true; };
                AutomaticPolicy.EffectiveForTest = delegate { return false; };
                AutomaticPolicy.ApprovedForTest = delegate(string key) { return "1"; };
                foreach (string key in keys) Check(PolicyResolver.Global().ValueOf(key) == "0", "unverified calibration enabled a resource");
                AutomaticPolicy.EffectiveForTest = delegate { return true; };
                AutomaticPolicy.ApprovedForTest = delegate(string key) { return key == PolicyCatalog.KeyBoost ? "1" : null; };
                foreach (string key in keys) Check(PolicyResolver.Global().ValueOf(key) == "1", "approved bundle was silently split by manual options");
                AutomaticPolicy.ApprovedForTest = delegate(string key) { return null; };
                foreach (string key in keys) Check(PolicyResolver.Global().ValueOf(key) == "0", "missing bundle approval enabled a resource");
            }
            finally { AutomaticPolicy.RequestedForTest = null; AutomaticPolicy.EffectiveForTest = null; AutomaticPolicy.ApprovedForTest = null; }
        }

        private static void BoostResourceToggleRestores()
        {
            foreach (string action in new[] { GameMode.GamePriorityAction, GameMode.GameMemoryAction, GameMode.GameIoAction, GameMode.GameGpuAction })
            {
                Fresh();
                using (var fixture = new IsolationBoostFixture())
                {
                    Check(fixture.Select(action, false) && fixture.WriteCount == 0, "disabled resource wrote an untouched process");
                    Check(fixture.Select(action, true) && fixture.Values[action] == fixture.Desired[action], "selected resource failed readback");
                    fixture.DenyRestore = true;
                    Check(!fixture.Select(action, false), "failed opt-out restore reported success");
                    fixture.DenyRestore = false;
                    Check(fixture.Select(action, false) && fixture.Values[action] == fixture.Original[action], "opt-out lost the original priority");
                    int writes = fixture.WriteCount;
                    Check(fixture.Select(action, false) && fixture.WriteCount == writes, "disabled resource repeatedly wrote restoration");
                    Check(fixture.Select(action, true) && fixture.Select(action, false), "resource could not be re-enabled and restored");
                }
            }
            Fresh();
            using (var fixture = new IsolationBoostFixture())
            {
                string action = GameMode.GameMemoryAction;
                fixture.Values[action] = fixture.Original[action] = 5;
                IsolationBoostFixture.Set(fixture.Snapshot, "Pg", 5);
                Check(fixture.Select(action, true) && fixture.WriteCount == 0, "default memory level invented a native write");
            }
        }

        private static void BoostMemoryStageIsolation()
        {
            Fresh();
            using (var fixture = new IsolationBoostFixture())
            {
                fixture.ApplyStatus[GameMode.GameMemoryAction] = unchecked((int)0xC00000BB);
                object[] stage = { fixture.Handle, IsolationBoostFixture.Pid, fixture.Pass, false, false, false };
                Check((bool)fixture.Call("ApplyBoostStateStage", stage) && !(bool)stage[4], "unsupported memory was reported as a complete bundle");
                Check(fixture.Values[GameMode.GamePriorityAction] == fixture.Desired[GameMode.GamePriorityAction]
                    && fixture.Values[GameMode.GameIoAction] == fixture.Desired[GameMode.GameIoAction], "memory refusal stopped CPU or IO");
                Check(fixture.Values[GameMode.GameMemoryAction] == fixture.Original[GameMode.GameMemoryAction], "memory refusal left an altered value");
            }
            Fresh();
            using (var fixture = new IsolationBoostFixture())
            {
                string action = GameMode.GameMemoryAction;
                fixture.Values[action] = fixture.Original[action] = 6;
                IsolationBoostFixture.Set(fixture.Snapshot, "Pg", 6);
                object[] stage = { fixture.Handle, IsolationBoostFixture.Pid, fixture.Pass, false, false, false };
                Check((bool)fixture.Call("ApplyBoostStateStage", stage) && (bool)stage[4], "existing higher memory priority was rejected");
                Check(fixture.Values[action] == 6 && fixture.Applies[action] == 0, "game boost lowered an existing higher memory priority");
            }
        }

        private static void BoostRetryReadBudget()
        {
            Fresh();
            using (var fixture = new IsolationBoostFixture())
            {
                int reads = 0;
                Settings.BeforeStrictStringReadForTest = delegate { reads++; };
                try
                {
                    for (int i = 0; i < 20; i++)
                        Check(!(bool)fixture.Call("HasBoostActionRetry", IsolationBoostFixture.Pid), "stable boost invented an explicit retry");
                    Check(reads == 0, "boost polled persistent retry state without a retry permit");
                }
                finally { Settings.BeforeStrictStringReadForTest = null; }
                ActionFailureStore.Trip(GameMode.GamePriorityAction, fixture.Target, "fixture failure");
                string key = ActionFailureStore.RecordKey(GameMode.GamePriorityAction, fixture.Target), reason;
                Check(!ActionFailureStore.HasRetryPermits && !(bool)fixture.Call("HasBoostActionRetry", IsolationBoostFixture.Pid), "failure alone granted a retry");
                Check(ActionFailureStore.RequestRetry(key) && (bool)fixture.Call("HasBoostActionRetry", IsolationBoostFixture.Pid), "explicit permit did not wake boost immediately");
                Check(ActionFailureStore.TryBeginApply(GameMode.GamePriorityAction, fixture.Target, out reason)
                    && !ActionFailureStore.HasRetryPermits && !(bool)fixture.Call("HasBoostActionRetry", IsolationBoostFixture.Pid),
                    "consumed permit kept requesting boost audits");
                ActionFailureStore.CancelApply(GameMode.GamePriorityAction, fixture.Target);
                Check(ActionFailureStore.HasRetryPermits && (bool)fixture.Call("HasBoostActionRetry", IsolationBoostFixture.Pid), "cancelled apply lost its explicit retry permit");
                ActionFailureStore.WithdrawRetry(GameMode.GamePriorityAction, fixture.Target);
                Check(!ActionFailureStore.HasRetryPermits && !(bool)fixture.Call("HasBoostActionRetry", IsolationBoostFixture.Pid), "withdrawn permit kept requesting boost audits");
            }
        }

        private static void BoostScheduledAuditWithoutRetry()
        {
            Fresh();
            using (var fixture = new IsolationBoostFixture())
            {
                string action = GameMode.GamePriorityAction;
                Check(fixture.Apply(action), "scheduled audit fixture could not apply the initial priority");
                ((HashSet<int>)IsolationBoostFixture.Get(fixture.Mode, "tweakApplied")).Add(IsolationBoostFixture.Pid);
                IDictionary deadlines = (IDictionary)IsolationBoostFixture.Get(fixture.Mode, "gameBoostNextAudit");
                deadlines[IsolationBoostFixture.Pid] = DateTime.UtcNow.AddDays(1).Ticks;
                object[] audit = { IsolationBoostFixture.Pid, fixture.Pass, false, false, false };
                Check(!ActionFailureStore.HasRetryPermits && !(bool)fixture.Call("ComputeAuditDue", audit),
                    "stable boost bypassed the existing audit interval without a retry permit");

                // 模拟外部程序改回优先级，仍由原有定时巡检纠正。
                fixture.Values[action] = fixture.Original[action];
                deadlines[IsolationBoostFixture.Pid] = DateTime.UtcNow.AddSeconds(-1).Ticks;
                Check(!ActionFailureStore.HasRetryPermits && (bool)fixture.Call("ComputeAuditDue", audit),
                    "retry fast path suppressed a due scheduled audit");
                Check(fixture.Apply(action) && fixture.Values[action] == fixture.Desired[action]
                    && fixture.Applies[action] == 2,
                    "scheduled audit did not repair external priority drift without a retry permit");
                Check(fixture.Restore(action) && fixture.Values[action] == fixture.Original[action],
                    "scheduled repair replaced the original restoration value");
            }
        }

        private static void ResetIsolationBoostHooks()
        {
            GameMode.BoostActionReadForTest = null;
            GameMode.BoostActionWriteForTest = null;
            GameMode.BoostActionIdentityForTest = null;
            GameMode.BoostActionPathForTest = null;
            ActionFailureStore.SaveForTest = null;
        }

        private static long IsolationBoostQos(int control, int state)
        { return unchecked(((long)(uint)control << 32) | (uint)state); }

        private sealed class IsolationBoostFixture : IDisposable
        {
            internal const int Pid = 424200;
            internal const long Birth = 123456789;
            internal readonly IntPtr Handle = new IntPtr(424200);
            internal readonly GameMode Mode;
            internal readonly object Pass;
            internal object Snapshot;
            internal string Target = @"C:\Games\FixtureGame.exe";
            internal int Identity;
            internal bool DenyRestore, ExitDuringWrite;
            internal string UnknownAction;
            internal int UnknownReadOrdinal;
            internal readonly Dictionary<string, long> Original = new Dictionary<string, long>();
            internal readonly Dictionary<string, long> Desired = new Dictionary<string, long>();
            internal readonly Dictionary<string, long> Values = new Dictionary<string, long>();
            internal readonly Dictionary<string, int> Applies = new Dictionary<string, int>();
            internal readonly Dictionary<string, int> Restores = new Dictionary<string, int>();
            internal readonly Dictionary<string, int> Reads = new Dictionary<string, int>();
            internal readonly Dictionary<string, int> ApplyStatus = new Dictionary<string, int>();
            internal readonly List<string> Events = new List<string>();

            internal IsolationBoostFixture()
            {
                ResetIsolationBoostHooks();
                string root = Path.Combine(Path.GetTempPath(), "Pavise-boost-isolation-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                Mode = new GameMode(root, new SuppressionCore());
                Type passType = typeof(GameMode).GetNestedType("BoostPass", BindingFlags.NonPublic);
                Pass = Activator.CreateInstance(passType, true);
                Set(Pass, "RendererPid", Pid); Set(Pass, "RendererCreation", Birth);
                Set(Pass, "RendererName", "FixtureGame"); Set(Pass, "ActionTarget", Target);
                Set(Pass, "IdentityGeneration", Get(Mode, "boostIdentityGeneration"));
                Set(Pass, "PriorityTarget", Native.HIGH_PRIORITY_CLASS);
                Type snapType = typeof(GameMode).GetNestedType("Snap", BindingFlags.NonPublic);
                Snapshot = Activator.CreateInstance(snapType);
                Set(Snapshot, "Pri", Native.NORMAL_PRIORITY_CLASS); Set(Snapshot, "Io", 2); Set(Snapshot, "Pg", 3);
                Set(Snapshot, "QoSControl", 1); Set(Snapshot, "QoSState", 1);
                Set(Snapshot, "Creation", Birth); Set(Snapshot, "Name", "FixtureGame"); Set(Snapshot, "CpuSets", new uint[0]);
                ((IDictionary)Get(Mode, "gameBoost"))[Pid] = Snapshot;
                ((IDictionary)Get(Mode, "gameGpu"))[Pid] = 0;
                ((IDictionary)Get(Mode, "boostActionTargets"))[Pid] = Target;
                Original[GameMode.GamePriorityAction] = Native.NORMAL_PRIORITY_CLASS;
                Original[GameMode.GameIoAction] = 2;
                Original[GameMode.GameMemoryAction] = 3;
                Original[GameMode.GameGpuAction] = 0;
                Original[GameMode.GameEcoAction] = IsolationBoostQos(1, 1);
                Desired[GameMode.GamePriorityAction] = Native.HIGH_PRIORITY_CLASS;
                Desired[GameMode.GameIoAction] = 3;
                Desired[GameMode.GameMemoryAction] = 5;
                Desired[GameMode.GameGpuAction] = Native.GpuPriorityHigh;
                Desired[GameMode.GameEcoAction] = IsolationBoostQos(Native.TimerExemptWanted ? 5 : 1, 0);
                foreach (string action in IsolationBoostActions)
                {
                    Values[action] = Original[action]; Applies[action] = Restores[action] = Reads[action] = ApplyStatus[action] = 0;
                }
                GameMode.BoostActionReadForTest = Read;
                GameMode.BoostActionWriteForTest = Write;
                GameMode.BoostActionIdentityForTest = delegate(IntPtr handle, long birth)
                { Check(handle == Handle && birth == Birth, "boost native identity was not scoped to the fake target"); return Identity; };
                GameMode.BoostActionPathForTest = delegate(IntPtr handle)
                { Check(handle == Handle, "boost path query escaped the fake handle"); return Target; };
            }

            internal static object Get(object owner, string field)
            { return owner.GetType().GetField(field, IsolationBoostFlags).GetValue(owner); }
            internal static void Set(object owner, string field, object value)
            { owner.GetType().GetField(field, IsolationBoostFlags).SetValue(owner, value); }
            internal object Call(string method, params object[] args)
            {
                try { return typeof(GameMode).GetMethod(method, IsolationBoostFlags).Invoke(Mode, args); }
                catch (TargetInvocationException error) { throw error.InnerException ?? error; }
            }
            private long? Read(string action, IntPtr handle)
            {
                Check(handle == Handle, "boost state read escaped the fake handle");
                Reads[action]++;
                if (UnknownAction == action && (UnknownReadOrdinal == 0 || UnknownReadOrdinal == Reads[action])) return null;
                return Values[action];
            }
            private int Write(string action, IntPtr handle, long value)
            {
                Check(handle == Handle, "boost write escaped the fake handle");
                bool restoring = value == Original[action];
                Events.Add(action + "|" + value);
                if (restoring)
                {
                    Restores[action]++;
                    if (DenyRestore) return 1;
                    Values[action] = value;
                    return 0;
                }
                Applies[action]++;
                string text, intent = ActionFailureStore.AttemptPrefix
                    + ActionFailureStore.RecordKey(action, Target).Substring(ActionFailureStore.Prefix.Length);
                Check(Settings.TryLoadStr(intent, out text) && !string.IsNullOrEmpty(text), "boost applied before durable execution intent");
                int status = ApplyStatus[action];
                // Protected and unsupported requests do not alter the mock target.
                if (status != 5 && status != unchecked((int)0xC00000BB)) Values[action] = value;
                if (ExitDuringWrite) Identity = 1;
                return status;
            }
            internal bool Apply(string action)
            {
                Set(Pass, "ActionTarget", Target);
                Func<long, bool> desired = delegate(long value) { return value == Desired[action]; };
                Func<int> write = delegate { return Write(action, Handle, Desired[action]); };
                return (bool)Call("ApplyIsolatedBoostAction", Handle, Pid, Pass, action, desired, write);
            }
            internal bool Restore(string action)
            { return (bool)Call("RestoreBoostAction", Handle, Pid, Birth, action); }
            internal int WriteCount { get { int count = 0; foreach (string action in IsolationBoostActions) count += Applies[action] + Restores[action]; return count; } }
            internal bool Select(string action, bool enabled)
            {
                return (bool)Call("ApplySelectedBoostAction", Handle, Pid, Pass, action, enabled,
                    new Func<long, bool>(delegate(long value) { return value == Desired[action]; }),
                    new Func<int>(delegate { return Write(action, Handle, Desired[action]); }));
            }
            public void Dispose() { ResetIsolationBoostHooks(); }
        }

        private static void BoostFirstFaultAndTargetIsolation()
        {
            foreach (string bad in IsolationBoostActions)
            {
                Fresh(); Settings.Save(PolicyCatalog.KeyBoost, true);
                using (var fixture = new IsolationBoostFixture())
                {
                    fixture.ApplyStatus[bad] = 1;
                    Check(!fixture.Apply(bad) && fixture.Applies[bad] == 1 && fixture.Restores[bad] == 1,
                        bad + " first fault did not immediately restore its original");
                    Check(fixture.Values[bad] == fixture.Original[bad] && ActionFailureStore.IsBlocked(bad, fixture.Target), bad + " first fault was not isolated");
                    Check(ActionFailureStore.Records().Count == 1 && Settings.Load(PolicyCatalog.KeyBoost, false), bad + " changed preferences or isolated extra actions");
                    foreach (string other in IsolationBoostActions)
                        if (other != bad) Check(!ActionFailureStore.IsBlocked(other, fixture.Target), bad + " isolated unrelated " + other);
                    for (int round = 0; round < 10; round++) Check(!fixture.Apply(bad), bad + " resumed automatically");
                    Check(fixture.Applies[bad] == 1, bad + " repeated native applies while isolated");
                    ActionFailureStore.ReloadForTest();
                    Check(!fixture.Apply(bad) && fixture.Applies[bad] == 1, bad + " store reload silently resumed apply");
                    fixture.Target = @"C:\OtherGames\FixtureGame.exe";
                    fixture.ApplyStatus[bad] = 0;
                    Check(fixture.Apply(bad) && fixture.Applies[bad] == 2, bad + " same executable name in another directory was isolated");
                    Check(ActionFailureStore.Records().Count == 1, bad + " healthy other executable erased the failed target record");
                }
            }
        }

        private static void BoostSkipAndQueryBoundaries()
        {
            foreach (string action in IsolationBoostActions)
            {
                Fresh(); using (var fixture = new IsolationBoostFixture())
                {
                    IsolationBoostFixture.Set(fixture.Pass, "WriteDenied", true);
                    Check(!fixture.Apply(action) && fixture.WriteCount == 0 && ActionFailureStore.Records().Count == 0, "protected boost target caused native writes or fault isolation");
                    IsolationBoostFixture.Set(fixture.Pass, "WriteDenied", false); fixture.Identity = 1;
                    Check(!fixture.Apply(action) && fixture.WriteCount == 0 && ActionFailureStore.Records().Count == 0, "gone boost identity became an apply fault");
                    fixture.Identity = -1;
                    Check(!fixture.Apply(action) && fixture.WriteCount == 0 && ActionFailureStore.Records().Count == 0, "unreadable boost identity authorized a write");
                    fixture.Identity = 0; fixture.UnknownAction = action;
                    Check(!fixture.Apply(action) && fixture.WriteCount == 0 && ActionFailureStore.Records().Count == 0, "unknown native original authorized an apply");
                }
                Fresh(); using (var fixture = new IsolationBoostFixture())
                {
                    fixture.UnknownAction = action; fixture.UnknownReadOrdinal = 2;
                    Check(!fixture.Apply(action) && fixture.WriteCount == 0 && ActionFailureStore.Records().Count == 0, "second boost query became a write or a fault");
                    Check(!ActionFailureStore.IsIsolated(action, fixture.Target), "cancelled second-query intent remained isolated");
                }
                Fresh(); using (var fixture = new IsolationBoostFixture())
                {
                    fixture.UnknownAction = action; fixture.UnknownReadOrdinal = 3;
                    Check(!fixture.Apply(action) && fixture.Applies[action] == 1 && fixture.Restores[action] == 1,
                        "unreadable boost readback did not isolate and restore the actual action");
                    Check(fixture.Values[action] == fixture.Original[action] && ActionFailureStore.Records().Count == 1,
                        "unreadable boost readback left values applied or quarantined other actions");
                }
            }
            Fresh(); using (var fixture = new IsolationBoostFixture())
            {
                fixture.ApplyStatus[GameMode.GameGpuAction] = unchecked((int)0xC00000BB);
                Check(!fixture.Apply(GameMode.GameGpuAction) && ActionFailureStore.Records().Count == 0, "unsupported GPU was treated as a failure");
                foreach (string other in IsolationBoostActions)
                    if (other != GameMode.GameGpuAction) Check(fixture.Apply(other), "unsupported GPU stopped unrelated " + other);
                Check(ActionFailureStore.Records().Count == 0, "unsupported GPU isolated another action");
            }
            Fresh(); using (var fixture = new IsolationBoostFixture())
            {
                fixture.ApplyStatus[GameMode.GamePriorityAction] = 5;
                Check(!fixture.Apply(GameMode.GamePriorityAction) && ActionFailureStore.Records().Count == 0, "native protection denial became an apply fault");
                Check(fixture.Apply(GameMode.GameIoAction), "protected priority request stopped independent IO action");
                fixture.ExitDuringWrite = true;
                Check(!fixture.Apply(GameMode.GameGpuAction) && ActionFailureStore.Records().Count == 0,
                    "exit after boost write left an unfinished apply failure");
            }
        }

        private static void BoostRetryRestoresOriginals()
        {
            foreach (string action in IsolationBoostActions)
            {
                Fresh(); using (var fixture = new IsolationBoostFixture())
                {
                    fixture.ApplyStatus[action] = 1; fixture.DenyRestore = true;
                    Check(!fixture.Apply(action) && fixture.Values[action] == fixture.Desired[action], "boost partial-failure fixture did not retain residue");
                    string key = ActionFailureStore.RecordKey(action, fixture.Target);
                    Check(ActionFailureStore.RequestRetry(key), "boost explicit retry was denied");
                    ((HashSet<int>)IsolationBoostFixture.Get(fixture.Mode, "tweakApplied")).Add(IsolationBoostFixture.Pid);
                    ((IDictionary)IsolationBoostFixture.Get(fixture.Mode, "gameBoostNextAudit"))[IsolationBoostFixture.Pid] = DateTime.UtcNow.AddDays(1).Ticks;
                    object[] audit = { IsolationBoostFixture.Pid, fixture.Pass, false, false, false };
                    Check((bool)fixture.Call("ComputeAuditDue", audit), "boost explicit retry did not make the audit due immediately");
                    Check(!fixture.Apply(action) && fixture.Applies[action] == 1 && !ActionFailureStore.IsRetryPending(action, fixture.Target),
                        "boost retry applied over residue or retained permission after failed restoration");
                    fixture.DenyRestore = false;
                    Check(!fixture.Apply(action) && fixture.Values[action] == fixture.Original[action] && fixture.Applies[action] == 1,
                        "isolated boost action could not finish restoration without reapplying");
                    fixture.Values[action] = fixture.Desired[action]; // Existing residue must be restored before admission.
                    fixture.Events.Clear(); fixture.ApplyStatus[action] = 0;
                    Check(ActionFailureStore.RequestRetry(key), "boost retry after recovery was denied");
                    Check(fixture.Apply(action) && fixture.Applies[action] == 2, "boost explicit retry was not admitted exactly once");
                    Check(fixture.Events.Count == 2 && fixture.Events[0] == action + "|" + fixture.Original[action]
                        && fixture.Events[1] == action + "|" + fixture.Desired[action], "boost retried before restoring its original snapshot");
                    Check(!ActionFailureStore.IsIsolated(action, fixture.Target) && !ActionFailureStore.IsRetryPending(action, fixture.Target), "verified boost retry retained isolation or permit");
                    Check(fixture.Apply(action) && fixture.Applies[action] == 2, "healthy verified boost action repeated a native apply");
                    Check(fixture.Restore(action) && fixture.Values[action] == fixture.Original[action], "boost original snapshot was overwritten by explicit retry");
                }
            }
        }

        private static void BoostCompletionPersistenceRollback()
        {
            foreach (string action in IsolationBoostActions)
            {
                Fresh(); using (var fixture = new IsolationBoostFixture())
                {
                    string key = ActionFailureStore.RecordKey(action, fixture.Target);
                    ActionFailureStore.Trip(action, fixture.Target, "fixture previous failure");
                    Check(ActionFailureStore.RequestRetry(key), "boost completion fixture retry denied");
                    ActionFailureStore.SaveForTest = delegate(string name, string text)
                    { return name == key && text.Length == 0 ? false : Settings.SaveStrDurable(name, text); };
                    Check(!fixture.Apply(action) && fixture.Applies[action] == 1 && fixture.Restores[action] == 1,
                        "boost failed durable completion did not immediately rollback");
                    Check(fixture.Values[action] == fixture.Original[action] && ActionFailureStore.IsBlocked(action, fixture.Target),
                        "boost failed durable completion retained applied state or reopened admission");
                    ActionFailureStore.SaveForTest = null;
                    Check(!fixture.Apply(action) && fixture.Applies[action] == 1, "boost failed durable completion retried automatically");
                }
            }
            Fresh(); using (var fixture = new IsolationBoostFixture())
            {
                ActionFailureStore.SaveForTest = delegate(string name, string text)
                { return name.StartsWith(ActionFailureStore.AttemptPrefix, StringComparison.Ordinal) && text.Length == 0 ? false : Settings.SaveStrDurable(name, text); };
                Check(!fixture.Apply(GameMode.GamePriorityAction) && fixture.Values[GameMode.GamePriorityAction] == fixture.Original[GameMode.GamePriorityAction],
                    "boost failed intent clearance did not rollback the native action");
                ActionFailureStore.SaveForTest = null; ActionFailureStore.ReloadForTest();
                Check(ActionFailureStore.IsBlocked(GameMode.GamePriorityAction, fixture.Target), "unfinished boost completion was lost on reload");
            }
        }

        private static void BoostRecoveryRemainsAllowed()
        {
            Fresh(); using (var fixture = new IsolationBoostFixture())
            {
                ActionFailureStore.Trip(GameMode.GamePriorityAction, fixture.Target, "fixture fault");
                foreach (string action in IsolationBoostActions) fixture.Values[action] = fixture.Desired[action];
                Check((bool)fixture.Call("RestoreBoostSnapshot", fixture.Handle, IsolationBoostFixture.Pid, fixture.Snapshot), "boost isolation blocked full snapshot restoration");
                foreach (string action in IsolationBoostActions) Check(fixture.Values[action] == fixture.Original[action], "boost recovery did not restore " + action);
                Check(ActionFailureStore.IsBlocked(GameMode.GamePriorityAction, fixture.Target), "boost restoration silently granted apply retry");
                int writes = fixture.WriteCount;
                fixture.Call("ForgetBoostActionTarget", IsolationBoostFixture.Pid);
                Check(((IDictionary)IsolationBoostFixture.Get(fixture.Mode, "boostActionTargets")).Count == 0
                    && fixture.WriteCount == writes, "boost release cleanup retained target cache or wrote native state");
            }
            Fresh(); using (var fixture = new IsolationBoostFixture())
            {
                fixture.Identity = 1;
                foreach (string action in IsolationBoostActions) Check(fixture.Restore(action), "exited boost target blocked restoration completion");
                Check(fixture.WriteCount == 0 && ActionFailureStore.Records().Count == 0, "exited boost restoration wrote to another process or isolated actions");
                fixture.Call("ForgetBoostActionTarget", IsolationBoostFixture.Pid);
                Check(((IDictionary)IsolationBoostFixture.Get(fixture.Mode, "boostActionRestorePending")).Count == 0, "exited boost release retained pending action state");
            }
        }

        private static void BoostNvRetryReopensOnlyDispatch()
        {
            Fresh(); using (var fixture = new IsolationBoostFixture())
            {
                fixture.Call("SyncNvFailureRetryGeneration");
                var applied = (HashSet<int>)IsolationBoostFixture.Get(fixture.Mode, "tweakApplied");
                applied.Add(IsolationBoostFixture.Pid);
                IsolationBoostFixture.Set(fixture.Mode, "nvTweakRetryAtTicks", long.MaxValue);
                ActionFailureStore.Trip(NvDrsTweaks.DriverFailureAction, "FixtureGame.exe", "mocked driver failure");
                Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(NvDrsTweaks.DriverFailureAction, "FixtureGame.exe")), "NVIDIA dispatch retry denied");
                fixture.Call("SyncNvFailureRetryGeneration");
                Check(!applied.Contains(IsolationBoostFixture.Pid)
                    && (long)IsolationBoostFixture.Get(fixture.Mode, "nvTweakRetryAtTicks") == 0,
                    "NVIDIA retry remained behind completed-plan or thirty-second dispatch cache");
                Check(ActionFailureStore.IsIsolated(NvDrsTweaks.DriverFailureAction, "FixtureGame.exe")
                    && ActionFailureStore.IsRetryPending(NvDrsTweaks.DriverFailureAction, "FixtureGame.exe") && fixture.WriteCount == 0,
                    "NVIDIA dispatch wake deleted isolation, consumed retry or performed native writes");
                applied.Add(IsolationBoostFixture.Pid);
                fixture.Call("SyncNvFailureRetryGeneration");
                Check(applied.Contains(IsolationBoostFixture.Pid), "unchanged retry generation repeatedly cleared satisfied-plan dispatch cache");
            }
        }
    }
}
#endif
