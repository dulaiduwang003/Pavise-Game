#if PAVISE_SELFTEST && PAVISE_ACTION_FAILURE_RUNNER
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static object IsolationEnvCall(GameMode mode, string method, params object[] args)
        { return typeof(GameMode).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, args); }
        private static void IsolationEnvSet(GameMode mode, string name, object value)
        { typeof(GameMode).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(mode, value); }
        private static Dictionary<string, long> IsolationEnvNext(GameMode mode)
        { return (Dictionary<string, long>)typeof(GameMode).GetField("envNextAttempt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode); }
        private static GameMode IsolationEnvMode()
        {
            string path = Path.Combine(Path.GetTempPath(), "Pavise-env-isolation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new GameMode(path, new SuppressionCore());
        }
        private static bool IsolationEnvStep(GameMode mode, bool active, Func<bool> apply, Func<bool> restore)
        { return (bool)IsolationEnvCall(mode, "EnvStep", "awake", true, active, apply, restore); }
        private static void RunEnvFailureIsolationChecks()
        {
            try { EnvModeStableReadBudget(); EnvModeIsolation(); EnvModeSkipAndPersistence(); PowerModeIsolation(); NvModeIsolation(); NvCommonAndUnknownIsolation(); NvCompletionAndRecovery(); }
            finally { GameMode.EnvironmentReadyForTest = null; GameMode.PowerRestoreForTest = null; NvDrsTweaks.RestoreClockForTest = null; NvDrsTweaks.DriverVersionForTest = null; ActionFailureStore.SaveForTest = null; }
        }

        private static void EnvModeStableReadBudget()
        {
            Fresh(); GameMode mode = IsolationEnvMode(); int reads = 0, writes = 0;
            ActionFailureStore.IsBlocked(PolicyCatalog.KeyAwake, "system");
            Settings.BeforeStrictStringReadForTest = delegate(string name) {
                if (name.StartsWith(ActionFailureStore.Prefix, StringComparison.Ordinal)
                    || name.StartsWith(ActionFailureStore.AttemptPrefix, StringComparison.Ordinal)) reads++;
            };
            try
            {
                for (int i = 0; i < 20; i++)
                    Check(IsolationEnvStep(mode, true, delegate { writes++; return true; }, delegate { writes++; return true; }),
                        "stable active environment action unexpectedly changed state");
                Check(reads == 60 && writes == 0, "stable environment pass duplicated isolation reads or re-applied an active action");
            }
            finally { Settings.BeforeStrictStringReadForTest = null; }
            Settings.SaveStrDurable(ActionFailureStore.RecordKey(PolicyCatalog.KeyAwake, "system"), "unreadable failure record");
            Check(!IsolationEnvStep(mode, true, delegate { throw new InvalidOperationException("isolated action applied"); }, delegate { writes++; return true; })
                && writes == 1, "later failure did not restore a previously active environment action");
        }

        private static void EnvModeIsolation()
        {
            Fresh(); Settings.Save(AutomaticPolicy.VersionKey, true); Settings.Save(PolicyCatalog.KeyAwake, true);
            GameMode mode = IsolationEnvMode(); int applies = 0, restores = 0; bool applyOk = false, restoreOk = false;
            Func<bool> apply = delegate { applies++; return applyOk; };
            Func<bool> restore = delegate { restores++; return restoreOk; };
            bool active = IsolationEnvStep(mode, false, apply, restore);
            string key = ActionFailureStore.RecordKey(PolicyCatalog.KeyAwake, "system");
            Check(active && applies == 1 && restores == 1, "environment first failure did not immediately attempt rollback");
            Check(ActionFailureStore.IsBlocked(PolicyCatalog.KeyAwake, "system") && Settings.Load(PolicyCatalog.KeyAwake, false), "environment isolation changed preference or failed to lock");
            for (int round = 0; round < 20; round++) { IsolationEnvNext(mode).Clear(); active = IsolationEnvStep(mode, active, apply, restore); }
            Check(applies == 1 && restores == 21, "environment restoration was blocked or apply repeated while isolated");
            double delay = TimeSpan.FromTicks(IsolationEnvNext(mode)["awake"] - DateTime.UtcNow.Ticks).TotalSeconds;
            Check(delay > 55 && delay <= 60, "environment recovery retry is not capped at sixty seconds");
            IsolationEnvCall(mode, "BeginRetrySession"); IsolationEnvCall(mode, "ClearEnvFuse", "awake");
            Check(applies == 1 && ActionFailureStore.IsBlocked(PolicyCatalog.KeyAwake, "system"), "session or preference change cleared environment isolation");
            Check(ActionFailureStore.RequestRetry(key), "environment explicit retry was denied");
            active = IsolationEnvStep(mode, active, apply, restore);
            Check(applies == 1 && !ActionFailureStore.IsRetryPending(PolicyCatalog.KeyAwake, "system"), "environment retry applied before recovery or retained permission after failed recovery");
            restoreOk = true; IsolationEnvNext(mode).Clear(); active = IsolationEnvStep(mode, active, apply, restore);
            Check(!active && applies == 1, "environment recovery did not finish without applying");
            applyOk = true; Check(ActionFailureStore.RequestRetry(key), "environment retry after clean recovery was denied");
            active = IsolationEnvStep(mode, active, apply, restore);
            Check(active && applies == 2 && !ActionFailureStore.IsIsolated(PolicyCatalog.KeyAwake, "system"), "environment verified retry did not clear isolation exactly once");
            IsolationEnvStep(mode, active, apply, restore); Check(applies == 2, "active environment action was applied again");
        }

        private static void EnvModeSkipAndPersistence()
        {
            Fresh(); GameMode mode = IsolationEnvMode(); int applies = 0;
            GameMode.EnvironmentReadyForTest = delegate { return false; };
            Check(!IsolationEnvStep(mode, false, delegate { applies++; return false; }, delegate { return true; })
                && applies == 0 && ActionFailureStore.Records().Count == 0, "environment not-ready condition was executed or classified as a fault");
            GameMode.EnvironmentReadyForTest = null;
            Check(!IsolationEnvStep(mode, false, delegate { throw new NotSupportedException(); }, delegate { return true; })
                && ActionFailureStore.Records().Count == 0, "environment unsupported action was classified as a fault");
            Check(!IsolationEnvStep(mode, false, delegate { throw new OperationCanceledException(); }, delegate { return true; })
                && ActionFailureStore.Records().Count == 0, "environment cancellation was classified as a fault");
            Fresh(); mode = IsolationEnvMode(); applies = 0;
            IsolationEnvStep(mode, false, delegate { applies++; ActionFailureStore.SaveForTest = delegate { return false; }; return false; }, delegate { return true; });
            Check(applies == 1 && ActionFailureStore.IsBlocked(PolicyCatalog.KeyAwake, "system"), "environment failed persistence forgot isolation");
            ActionFailureStore.ReloadForTest();
            Check(ActionFailureStore.IsBlocked(PolicyCatalog.KeyAwake, "system"), "unfinished environment intent did not survive store reload");
            GameMode freshMode = IsolationEnvMode(); IsolationEnvCall(freshMode, "BeginRetrySession");
            IsolationEnvStep(freshMode, false, delegate { applies++; return true; }, delegate { return true; });
            Check(applies == 1, "new game-mode instance resumed a failed action automatically");
        }

        private static void PowerModeIsolation()
        {
            Fresh(); Settings.Save("PowerPlanOn", true); GameMode mode = IsolationEnvMode(); int applies = 0, restores = 0; bool restoreOk = false;
            GameMode.PowerRestoreForTest = delegate { restores++; return restoreOk; };
            IsolationEnvSet(mode, "lastPowerPolicyKey", 3);
            Func<bool> failedApply = delegate { applies++; return false; };
            Check(!(bool)IsolationEnvCall(mode, "RunPowerPlanApply", 0, failedApply) && applies == 1 && restores == 1, "power first failure did not roll back immediately");
            Check((bool)IsolationEnvCall(mode, "PowerPlanStopped", 3) && (bool)IsolationEnvCall(mode, "PowerPlanStopped", 11)
                && Settings.Load("PowerPlanOn", false), "power policy change bypassed isolation or changed preference");
            IsolationEnvCall(mode, "BeginRetrySession"); IsolationEnvSet(mode, "lastPowerPolicyKey", 11);
            IsolationEnvCall(mode, "RunPowerPlanApply", 0, failedApply); Check(applies == 1, "power session restarted a failed native action");
            string key = ActionFailureStore.RecordKey(PolicyCatalog.KeyPowerPlan, "system");
            Check(ActionFailureStore.RequestRetry(key), "power explicit retry denied");
            Check(!(bool)IsolationEnvCall(mode, "RestoreFailedPowerPlan", DateTime.UtcNow.Ticks)
                && applies == 1 && !ActionFailureStore.IsRetryPending(PolicyCatalog.KeyPowerPlan, "system"), "power retry remained authorized after failed recovery");
            restoreOk = true; IsolationEnvNext(mode).Clear();
            Check((bool)IsolationEnvCall(mode, "RestoreFailedPowerPlan", DateTime.UtcNow.Ticks), "power recovery remained blocked by failure isolation");
            Check(ActionFailureStore.RequestRetry(key), "power retry after recovery denied");
            IsolationEnvSet(mode, "lastPowerPolicyKey", 3);
            Check((bool)IsolationEnvCall(mode, "RunPowerPlanApply", 0, new Func<bool>(delegate { applies++; return true; }))
                && applies == 2 && !ActionFailureStore.IsIsolated(PolicyCatalog.KeyPowerPlan, "system"), "verified power retry did not clear isolation");
            IsolationEnvCall(mode, "RunPowerPlanApply", -1, failedApply); IsolationEnvSet(mode, "stopping", true);
            IsolationEnvCall(mode, "RunPowerPlanApply", 0, failedApply); Check(applies == 2, "stale or stopping power task performed native work");
            GameMode.PowerRestoreForTest = null;
        }

        private sealed class IsolationNvDriver : INvDrsDriver
        {
            private readonly Dictionary<string, Dictionary<uint, uint>> values = new Dictionary<string, Dictionary<uint, uint>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<int, string> profiles = new Dictionary<int, string>();
            private Dictionary<string, Dictionary<uint, uint>> working;
            private readonly Dictionary<uint, int> reads = new Dictionary<uint, int>();
            internal readonly Dictionary<uint, int> WriteCounts = new Dictionary<uint, int>();
            internal uint RejectId, UnsupportedId, UnreadableId;
            internal uint UnknownReadId;
            internal int UnknownReadNumber;
            internal bool RejectOpen, RejectSave, IgnoreWrites;
            internal int Opens, Writes;
            public bool Available { get { return true; } }
            internal Dictionary<uint, uint> Values(string name)
            {
                Dictionary<uint, uint> rows;
                if (!values.TryGetValue(name, out rows)) { rows = new Dictionary<uint, uint>(); values[name] = rows; }
                return rows;
            }
            public bool TryOpenSession(out IntPtr session)
            {
                Opens++; session = IntPtr.Zero; if (RejectOpen) return false;
                working = new Dictionary<string, Dictionary<uint, uint>>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in values) working[item.Key] = new Dictionary<uint, uint>(item.Value);
                profiles.Clear(); session = new IntPtr(1); return true;
            }
            public void CloseSession(IntPtr session) { }
            public bool FindOrCreateAppProfile(IntPtr session, string exe, out IntPtr profile)
            {
                if (!working.ContainsKey(exe)) working[exe] = new Dictionary<uint, uint>();
                int id = profiles.Count + 1; profiles[id] = exe; profile = new IntPtr(id); return true;
            }
            public int TryGetDword(IntPtr session, IntPtr profile, uint id, out uint value)
            {
                value = 0; int count; reads.TryGetValue(id, out count); reads[id] = ++count;
                if (id == UnreadableId || (id == UnknownReadId && count == UnknownReadNumber)) return -1;
                return working[profiles[profile.ToInt32()]].TryGetValue(id, out value) ? 1 : 0;
            }
            public bool SetDword(IntPtr session, IntPtr profile, uint id, uint value, out int status)
            {
                Writes++; int count; WriteCounts.TryGetValue(id, out count); WriteCounts[id] = count + 1;
                status = id == UnsupportedId ? NvApi.StatusSettingNotFound : id == RejectId ? -999 : 0;
                if (status != 0) return false;
                if (!IgnoreWrites) working[profiles[profile.ToInt32()]][id] = value;
                return true;
            }
            public bool DeleteSetting(IntPtr session, IntPtr profile, uint id)
            { Writes++; if (!IgnoreWrites) working[profiles[profile.ToInt32()]].Remove(id); return true; }
            public bool SaveSession(IntPtr session)
            {
                if (RejectSave) return false;
                values.Clear(); foreach (var item in working) values[item.Key] = new Dictionary<uint, uint>(item.Value);
                return true;
            }
        }

        private static void NvModeIsolation()
        {
            Fresh(); INvDrsDriver saved = NvDrsTweaks.Driver;
            try
            {
                var driver = new IsolationNvDriver { RejectId = NvApi.SettingShaderCacheSize }; NvDrsTweaks.Driver = driver;
                NvDrsTweaks.DriverVersionForTest = delegate { return 12345; };
                driver.Values("Game.exe")[NvApi.SettingPreferredPState] = 5; driver.Values("Game.exe")[NvApi.SettingShaderCacheEnable] = 0;
                Settings.Save(PolicyCatalog.KeyNvShaderCache, true); bool retry;
                NvGamePlan plan = NvGamePlan.ForAutomatic(true, true, true);
                List<string> failed = NvDrsTweaks.ApplyForGame(@"C:\Fixture\Game.exe", plan, out retry);
                Check(failed.Contains(NvDrsTweaks.KeyShaderCache) && retry && ActionFailureStore.IsBlocked(PolicyCatalog.KeyNvShaderCache, "Game.exe"), "NVIDIA first setting failure was not isolated");
                Check(ActionFailureStore.Records()[0].Environment.Contains("driver 123.45"), "NVIDIA confirmed mock driver metadata was not retained");
                Check(!ActionFailureStore.IsIsolated(PolicyCatalog.KeyNvMaxPerf, "Game.exe") && driver.Values("Game.exe")[NvApi.SettingPreferredPState] == NvApi.PStatePreferMax,
                    "NVIDIA shader failure rolled back or isolated unrelated power setting");
                Check(driver.Values("Game.exe")[NvApi.SettingShaderCacheEnable] == 0 && !driver.Values("Game.exe").ContainsKey(NvApi.SettingShaderCacheSize), "NVIDIA partial shader group was not rolled back");
                Check(Settings.Load(PolicyCatalog.KeyNvShaderCache, false) && !ActionFailureStore.IsIsolated(PolicyCatalog.KeyNvShaderCache, "Other.exe"), "NVIDIA failure changed preference or isolated another game");
                int writes = driver.Writes; ActionFailureStore.ReloadForTest();
                NvDrsTweaks.ApplyForGame(@"c:\fixture\GAME.EXE", plan, out retry);
                Check(driver.Writes == writes, "NVIDIA reload or path case bypassed setting isolation");
                driver.RejectId = 0; Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PolicyCatalog.KeyNvShaderCache, "Game.exe")), "NVIDIA setting retry denied");
                Check(NvDrsTweaks.ApplyForGame(@"C:\Fixture\Game.exe", plan, out retry).Count == 0
                    && !ActionFailureStore.IsIsolated(PolicyCatalog.KeyNvShaderCache, "Game.exe") && driver.Values("Game.exe")[NvApi.SettingShaderCacheSize] == NvApi.ShaderCacheUnlimited,
                    "NVIDIA verified setting retry did not clear isolation");
                writes = driver.Writes; NvDrsTweaks.ApplyForGame(@"C:\Fixture\Game.exe", plan, out retry); Check(driver.Writes == writes, "satisfied NVIDIA plan wrote settings again");
            }
            finally { NvDrsTweaks.Driver = saved; }
        }

        private static void NvCommonAndUnknownIsolation()
        {
            Fresh(); INvDrsDriver saved = NvDrsTweaks.Driver;
            try
            {
                var driver = new IsolationNvDriver { RejectOpen = true }; NvDrsTweaks.Driver = driver; bool retry;
                NvGamePlan plan = NvGamePlan.ForAutomatic(true, true, true);
                NvDrsTweaks.ApplyForGame(@"C:\Fixture\Game.exe", plan, out retry);
                Check(ActionFailureStore.Records().Count == 1 && ActionFailureStore.IsBlocked(NvDrsTweaks.DriverFailureAction, "Game.exe"), "NVIDIA session failure was blamed on individual settings");
                int opens = driver.Opens; NvDrsTweaks.ApplyForGame(@"C:\Fixture\Game.exe", plan, out retry); Check(driver.Opens == opens && driver.Writes == 0, "NVIDIA failed session retried automatically");
                driver.RejectOpen = false; Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(NvDrsTweaks.DriverFailureAction, "Game.exe")), "NVIDIA common retry denied");
                Check(NvDrsTweaks.ApplyForGame(@"C:\Fixture\Game.exe", plan, out retry).Count == 0 && !ActionFailureStore.IsIsolated(NvDrsTweaks.DriverFailureAction, "Game.exe"), "NVIDIA common verified retry failed");
                Fresh(); driver = new IsolationNvDriver { UnreadableId = NvApi.SettingPreRenderLimit }; NvDrsTweaks.Driver = driver;
                NvDrsTweaks.ApplyForGame(@"C:\Fixture\Unknown.exe", NvGamePlan.ForAutomatic(false, false, true, "on"), out retry);
                Check(driver.Writes == 0 && ActionFailureStore.IsBlocked(PolicyCatalog.KeyNvLowLat, "Unknown.exe"), "NVIDIA unknown original setting was written or not isolated");
                Fresh(); driver = new IsolationNvDriver { RejectId = NvApi.SettingShaderCacheEnable }; NvDrsTweaks.Driver = driver;
                driver.Values("FirstFault.exe")[NvApi.SettingShaderCacheEnable] = 0;
                NvDrsTweaks.ApplyForGame(@"C:\Fixture\FirstFault.exe", NvGamePlan.ForAutomatic(false, true, true), out retry);
                Check(!driver.WriteCounts.ContainsKey(NvApi.SettingShaderCacheSize), "NVIDIA later group setting was written after first setting fault");
                Check(driver.Values("FirstFault.exe")[NvApi.SettingShaderCacheEnable] == 0
                    && !driver.Values("FirstFault.exe").ContainsKey(NvApi.SettingShaderCacheSize), "NVIDIA first-fault group was partially committed before rollback");
                Fresh(); driver = new IsolationNvDriver { UnknownReadId = NvApi.SettingShaderCacheEnable, UnknownReadNumber = 2 }; NvDrsTweaks.Driver = driver;
                driver.Values("ReadRace.exe")[NvApi.SettingShaderCacheEnable] = 0;
                NvDrsTweaks.ApplyForGame(@"C:\Fixture\ReadRace.exe", NvGamePlan.ForAutomatic(false, true, true), out retry);
                Check(driver.Writes == 0 && ActionFailureStore.IsBlocked(PolicyCatalog.KeyNvShaderCache, "ReadRace.exe"), "NVIDIA second query becoming unknown fell through to a write");
                Fresh(); driver = new IsolationNvDriver { UnsupportedId = NvApi.SettingPreRenderLimit }; NvDrsTweaks.Driver = driver;
                NvDrsTweaks.ApplyForGame(@"C:\Fixture\Unsupported.exe", NvGamePlan.ForAutomatic(false, false, true, "on"), out retry);
                Check(ActionFailureStore.Records().Count == 0 && !NvDrsTweaks.HasGameResidue, "NVIDIA unsupported setting became an apply fault or left a receipt");
            }
            finally { NvDrsTweaks.Driver = saved; }
        }

        private static void NvCompletionAndRecovery()
        {
            Fresh(); INvDrsDriver saved = NvDrsTweaks.Driver;
            try
            {
                var driver = new IsolationNvDriver { RejectId = NvApi.SettingPreRenderLimit }; NvDrsTweaks.Driver = driver;
                driver.Values("Game.exe")[NvApi.SettingPreRenderLimit] = 5; bool retry;
                NvGamePlan plan = NvGamePlan.ForAutomatic(false, false, true, "on");
                NvDrsTweaks.ApplyForGame(@"C:\Fixture\Game.exe", plan, out retry);
                string key = ActionFailureStore.RecordKey(PolicyCatalog.KeyNvLowLat, "Game.exe"); driver.RejectId = 0;
                Check(ActionFailureStore.RequestRetry(key), "NVIDIA completion fixture retry denied");
                ActionFailureStore.SaveForTest = delegate(string name, string text) { return name != key || text.Length != 0 ? Settings.SaveStrDurable(name, text) : false; };
                NvDrsTweaks.ApplyForGame(@"C:\Fixture\Game.exe", plan, out retry);
                Check(driver.Values("Game.exe")[NvApi.SettingPreRenderLimit] == 5 && ActionFailureStore.IsBlocked(PolicyCatalog.KeyNvLowLat, "Game.exe"), "NVIDIA failed durable completion retained optimization or reopened action");
                ActionFailureStore.SaveForTest = null;
                Fresh(); driver = new IsolationNvDriver(); NvDrsTweaks.Driver = driver;
                driver.Values("Recover.exe")[NvApi.SettingPreRenderLimit] = 5;
                NvDrsTweaks.ApplyForGame(@"C:\Fixture\Recover.exe", plan, out retry); driver.IgnoreWrites = true;
                Check(!NvDrsTweaks.RestoreAllGames() && NvDrsTweaks.HasGameResidue, "NVIDIA unverified restore cleared original receipt");
                driver.IgnoreWrites = false;
                Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PolicyCatalog.KeyNvLowLat, "Recover.exe")), "NVIDIA recovery retry denied");
                Check(NvDrsTweaks.RestoreAllGames() && driver.Values("Recover.exe")[NvApi.SettingPreRenderLimit] == 5 && !NvDrsTweaks.HasGameResidue, "NVIDIA isolation blocked verified restoration");
                Check(ActionFailureStore.IsIsolated(PolicyCatalog.KeyNvLowLat, "Recover.exe"), "NVIDIA restoration prematurely cleared apply failure");
            }
            finally { NvDrsTweaks.Driver = saved; ActionFailureStore.SaveForTest = null; }
        }
    }
}
#endif
