#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static ProcEntry RuntimeFixture(int pid, int parent, long born, string path, int session = 7)
        { return new ProcEntry { Pid=pid, ParentPid=parent, Creation=born, Path=path, Name=Path.GetFileNameWithoutExtension(path), Session=session }; }
        internal static void RunWeGameRuntimeTests()
        {
            var snapshot = new ProcessSnapshot(new[] {
                RuntimeFixture(100, 90, 10, @"C:\WeGame\wegame_env.exe"),
                RuntimeFixture(101, 90, 11, @"C:\WeGame\tcls_core.exe"),
                RuntimeFixture(102, 90, 12, @"C:\WeGame\rail.exe"),
                RuntimeFixture(103, 90, 13, @"C:\WeGame\web\browser.exe"),
                RuntimeFixture(104, 102, 14, @"C:\Elsewhere\session-worker.exe"),
                RuntimeFixture(105, 90, 15, @"C:\Unrelated\browser.exe"),
                RuntimeFixture(106, 90, 16, @"C:\WeGameExtra\browser.exe"),
                RuntimeFixture(107, 102, 5, @"C:\Elsewhere\reused.exe"),
                RuntimeFixture(108, 102, 18, @"C:\Elsewhere\foreign.exe", 8),
                RuntimeFixture(109, 90, 19, @"C:\Listary\ListaryHookHost64.exe"),
                RuntimeFixture(110, 109, 20, @"C:\Listary\input-worker.exe"),
                RuntimeFixture(90, 0, 1, @"C:\Windows\explorer.exe")
            });
            var actual = WeGameRuntimeProtection.Collect(snapshot, 999, 7);
            foreach (int pid in new[]{100,101,102,103,104,109,110}) Eq(true, actual.Contains(pid));
            foreach (int pid in new[]{90,105,106,107,108}) Eq(false, actual.Contains(pid));
            foreach (string name in new[]{"WeGame.exe","wegame_env","TCLS_CORE","rail","Pallas"})
                foreach (bool family in new[]{false,true})
                    Eq(false, FamilyBoundary.BasicBackgroundEligible(700,999,name,@"C:\Runtime\"+WhitelistRule.NormalizeName(name)+".exe",
                        7,7,800,false,@"C:\Windows",false,null,true,family));
            foreach (string name in new[]{"railway","mywegame","tcls_core_extra","browser"}) Eq(false, WeGameRuntimeProtection.IsRuntimeName(name));
            Eq(false, WeGameRuntimeProtection.IsCriticalRuntime("rail", @"C:\Other\worker.exe"));

            string dir = Path.Combine(Path.GetTempPath(), "PaviseRuntime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var core = new SuppressionCore(Path.Combine(dir,"state"));
            var mode = new GameMode(dir,core);
            TestRuntimeProtectionLogStates(mode, dir);
            int session; using (Process self = Process.GetCurrentProcess()) session = self.SessionId;
            var evaluate = typeof(GameMode).GetMethod("EvaluateWhitelist", BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (ProcEntry p in snapshot.Entries) p.Session = session;
            object view = evaluate.Invoke(mode,new object[]{snapshot});
            Eq(true, ((HashSet<int>)view.GetType().GetField("Protected").GetValue(view)).Contains(104));
            snapshot = new ProcessSnapshot(new[]{RuntimeFixture(104,102,14,@"C:\Elsewhere\session-worker.exe",session)});
            view=evaluate.Invoke(mode,new object[]{snapshot});
            Eq(true, ((HashSet<int>)view.GetType().GetField("Protected").GetValue(view)).Contains(104));
            snapshot = new ProcessSnapshot(new[]{RuntimeFixture(104,102,30,@"C:\Elsewhere\session-worker.exe",session)});
            view=evaluate.Invoke(mode,new object[]{snapshot});
            Eq(false, ((HashSet<int>)view.GetType().GetField("Protected").GetValue(view)).Contains(104));

            foreach (WhitelistRuleKind kind in new[]{WhitelistRuleKind.LegacyName,WhitelistRuleKind.ExactPath,WhitelistRuleKind.ApplicationFamily})
            {
                WhitelistRule rule;
                Eq(true,WhitelistRule.TryCreate(kind,kind==WhitelistRuleKind.LegacyName ? "myapp" : @"C:\MyApp\myapp.exe",out rule));
                typeof(GameMode).GetMethod("AddWhiteRuleNoSave",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(mode,new object[]{rule});
            }
            snapshot = new ProcessSnapshot(new[]{ RuntimeFixture(200,0,10,@"C:\MyApp\myapp.exe",session),RuntimeFixture(201,200,11,@"C:\Helpers\worker.exe",session)});
            view=evaluate.Invoke(mode,new object[]{snapshot});
            Eq(true, ((HashSet<int>)view.GetType().GetField("Protected").GetValue(view)).Contains(201));
            Eq(true, mode.NeedsWhitelistParentIdentity(session));

            foreach (PerformancePreset preset in PresetValue.VisibleOrder())
            {
                Eq(preset == PerformancePreset.Extreme,GameMode.CpuCapSupported(preset));
                Eq(preset == PerformancePreset.Extreme || preset == PerformancePreset.Competitive,BackgroundServicePolicy.SupportedMode(preset));
                Eq(preset == PerformancePreset.Extreme, StrongBackgroundPolicy.Admit(preset,10,false,false,false,true));
                Eq(false,StrongBackgroundPolicy.Admit(preset,10,false,false,false,false));
                Eq(false,StrongBackgroundPolicy.Admit(preset,10,true,false,false,true));
                Eq(false,StrongBackgroundPolicy.Admit(preset,10,false,true,false,true));
                Eq(false,BackgroundFreezePolicy.Eligible(preset,100,10,@"C:\Apps\browser.exe",7,7,false,false,@"C:\Windows"));
            }
            Eq(true,StrongBackgroundPolicy.MetadataMatches("GoogleUpdate","GoogleUpdate.exe","Google LLC","Google Update"));
            Eq(false,StrongBackgroundPolicy.MetadataMatches("browser","browser.exe","Google LLC","Google Chrome"));
            Eq(false,StrongBackgroundPolicy.MetadataMatches("GoogleUpdate","worker.exe","Google LLC","Google Update"));
            Eq(false,StrongBackgroundPolicy.MetadataMatches("GoogleUpdate","GoogleUpdate.exe","Unknown","Google Update"));
            TestRuntimeNativeFloor(dir,core);
            TestOriginalBoostPolicy(mode,core);
            TestModeSwitchLock(mode);
            TestRemovedProfileModeSwitchLock(dir);
            Console.WriteLine("RUNTIME_PROTECTION platform/input families, orphan retention, PID reuse, strong admission, native deny and mode locks PASS");
        }

        internal static void RunModeSwitchLockRegressionTests(string output)
        {
            string previousLog = Logger.LogPath;
            try
            {
                Logger.LogPath = Path.Combine(output, "mode-lock-decisions.log");
                Settings.UseTransientStoreForCurrentProcess();
                var mode = new GameMode(output, new SuppressionCore(Path.Combine(output, "state")));
                TestModeSwitchLock(mode);
                TestRemovedProfileModeSwitchLock(output);
                Console.WriteLine("PASS mode locks: live process, guard disable, removed owner, unrelated/missing/failed removal, pending teardown and late capture");
            }
            finally { Logger.LogPath = previousLog; }
        }

        private sealed class RemovedProfileModeLockFixture : IDisposable
        {
            internal readonly string DirectoryPath;
            internal readonly GameMode Mode;
            internal readonly Process Target;
            internal readonly GameProfile Owner, Other;
            internal readonly GameDetection Detection;
            private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

            internal RemovedProfileModeLockFixture(string root, string name)
            {
                Settings.UseTransientStoreForCurrentProcess();
                DirectoryPath = Path.Combine(root, "removed-profile-lock-" + name + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(DirectoryPath);
                string targetPath = Path.Combine(DirectoryPath, "Weixin.exe");
                string otherPath = Path.Combine(DirectoryPath, "Other.exe");
                File.Copy(typeof(SelfTests).Assembly.Location, targetPath);
                File.Copy(typeof(SelfTests).Assembly.Location, otherPath);
                Target = Process.Start(new ProcessStartInfo(targetPath, "--cpu-cap-test-target")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
                try
                {
                    Eq("READY", CpuCapLine(Target));
                    Eq(true, new GameProfileStore(DirectoryPath).Save(new GameProfile[0]));
                    Mode = new GameMode(DirectoryPath, new SuppressionCore(Path.Combine(DirectoryPath, "state")));
                    Eq(true, Mode.AddGameExecutable("Weixin", targetPath));
                    Eq(true, Mode.AddGameExecutable("Other", otherPath));
                    List<GameProfile> profiles = Mode.GetProfiles();
                    Owner = profiles.Find(p => string.Equals(p.ExecutablePath, targetPath, StringComparison.OrdinalIgnoreCase));
                    Other = profiles.Find(p => string.Equals(p.ExecutablePath, otherPath, StringComparison.OrdinalIgnoreCase));
                    Eq(true, Owner != null); Eq(true, Other != null);
                    Eq(true, Mode.SetProfileForceTrigger(Owner.Id, true));
                    Owner = Mode.GetProfiles().Find(p => p.Id == Owner.Id);
                    Eq(true, Owner.ForceTrigger);
                    Detection = new GameDetection { Profile = Owner, RendererPid = Target.Id,
                        RendererCreation = CpuCapWant(Target)[Target.Id] };
                    Set("panicReq", false);
                }
                catch { CpuCapKill(Target); throw; }
            }

            internal void Set(string field, object value)
            { typeof(GameMode).GetField(field, Flags).SetValue(Mode, value); }

            internal bool RestoreRequested
            { get { return (bool)typeof(GameMode).GetField("panicReq", Flags).GetValue(Mode); } }

            internal void Capture(bool activeSession)
            {
                Set("enabled", true);
                Set("activeDetection", Detection);
                Set("active", activeSession);
                if (activeSession) Mode.ProbeSessionPolicyApply(Owner);
                Mode.Enabled = false;
                if (activeSession) Set("enabled", true);
            }

            internal void FinishTeardown()
            {
                Set("active", false);
                Mode.ProbeSessionPolicyClear();
                Set("activeDetection", null);
            }

            internal void AssertTargetAlive()
            {
                Eq(false, Target.HasExited);
                Target.StandardInput.WriteLine("PING"); Target.StandardInput.Flush();
                Eq("OK", CpuCapLine(Target));
            }

            internal void AssertPresetLocked()
            { Eq(true, Mode.ModeSwitchLocked); Eq(false, Mode.TrySetPreset(PerformancePreset.Extreme)); }

            public void Dispose() { CpuCapKill(Target); }
        }

        private static void TestRemovedProfileModeSwitchLock(string root)
        {
            using (var f = new RemovedProfileModeLockFixture(root, "active"))
            {
                f.Capture(true);
                f.AssertPresetLocked();
                f.Mode.RemoveProfile(f.Owner.Id);
                Eq(1, f.Mode.GetProfiles().Count);
                Eq(f.Other.Id, new GameProfileStore(f.DirectoryPath).LoadProfiles()[0].Id);
                Eq(true, f.RestoreRequested);
                f.AssertPresetLocked();
                f.Set("active", false); f.AssertPresetLocked();
                f.Mode.ProbeSessionPolicyClear(); f.AssertPresetLocked();
                f.Set("activeDetection", null);
                f.AssertTargetAlive();
                Eq(false, f.Mode.ModeSwitchLocked);
                Eq(true, f.Mode.TrySetPreset(PerformancePreset.Extreme));
                typeof(GameMode).GetMethod("RememberModeSwitchLockLocked", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(f.Mode, new object[] { f.Detection });
                Eq(false, f.Mode.ModeSwitchLocked);
                Console.WriteLine("PASS deleting forced live entry unlocks only after session teardown; stale capture stays unlocked");
            }

            using (var f = new RemovedProfileModeLockFixture(root, "unrelated"))
            {
                f.Capture(false); f.FinishTeardown();
                f.AssertPresetLocked();
                f.Mode.RemoveProfile(f.Other.Id);
                Eq(1, f.Mode.GetProfiles().Count);
                Eq(f.Owner.Id, new GameProfileStore(f.DirectoryPath).LoadProfiles()[0].Id);
                Eq(false, f.RestoreRequested);
                f.AssertTargetAlive(); f.AssertPresetLocked();
                string before = File.ReadAllText(Path.Combine(f.DirectoryPath, GameProfileStore.FileName));
                f.Mode.RemoveProfile("missing-" + f.Owner.Id);
                Eq(before, File.ReadAllText(Path.Combine(f.DirectoryPath, GameProfileStore.FileName)));
                Eq(false, f.RestoreRequested); f.AssertPresetLocked();
                Console.WriteLine("PASS unrelated and missing removal preserve the registered live owner's lock");
            }

            using (var f = new RemovedProfileModeLockFixture(root, "guard-disabled"))
            {
                f.Capture(false); f.FinishTeardown();
                Eq(false, f.Mode.Enabled); f.AssertPresetLocked();
                f.Mode.RemoveProfile(f.Owner.Id.ToUpperInvariant());
                Eq(1, f.Mode.GetProfiles().Count);
                Eq(false, f.Mode.ModeSwitchLocked);
                Eq(true, f.Mode.TrySetPreset(PerformancePreset.Extreme));
                f.AssertTargetAlive();
                Console.WriteLine("PASS deleting the owner after guard shutdown releases its retained process lock");
            }

            using (var f = new RemovedProfileModeLockFixture(root, "failed"))
            {
                f.Capture(false); f.FinishTeardown();
                string file = Path.Combine(f.DirectoryPath, GameProfileStore.FileName), before = File.ReadAllText(file);
                var store = (GameProfileStore)typeof(GameMode).GetField("profileStore", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Mode);
                store.RetryWaitForTest = delegate { };
                using (var lease = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    f.Mode.RemoveProfile(f.Owner.Id);
                Eq(2, f.Mode.GetProfiles().Count);
                Eq(before, File.ReadAllText(file));
                Eq(false, f.Mode.ProfileStoreSaveFailed);
                Eq(false, f.RestoreRequested);
                f.AssertPresetLocked(); f.AssertTargetAlive();
                f.Mode.RemoveProfile(f.Owner.Id);
                Eq(1, f.Mode.GetProfiles().Count); Eq(false, f.Mode.ModeSwitchLocked);
                Console.WriteLine("PASS failed library persistence retains the lock; successful removal retry releases it");
            }

            using (var f = new RemovedProfileModeLockFixture(root, "policy-only"))
            {
                f.Capture(false); f.FinishTeardown();
                f.Mode.ProbeSessionPolicyApply(f.Owner);
                f.Mode.RemoveProfile(f.Owner.Id);
                Eq(true, f.RestoreRequested);
                f.AssertPresetLocked();
                f.Mode.ProbeSessionPolicyClear();
                Eq(false, f.Mode.ModeSwitchLocked);
                Eq(true, f.Mode.TrySetPreset(PerformancePreset.Extreme));
                f.AssertTargetAlive();
                Console.WriteLine("PASS owner policy without detection still requests restoration and retains the teardown lock");
            }
        }
        private static void TestRuntimeProtectionLogStates(GameMode mode, string dir)
        {
            string previousLog = Logger.LogPath;
            string log = Path.Combine(dir, "runtime-protection.log");
            var record = typeof(GameMode).GetMethod("RecordRuntimeProtection", BindingFlags.NonPublic | BindingFlags.Instance);
            var process = RuntimeFixture(RendererReleaseFixture.Pid, 0, RendererReleaseFixture.Creation, @"C:\Fixture\ACE-Helper.exe");
            var fake = new RendererReleaseFixture();
            try
            {
                Logger.LogPath = log;
                fake.Seed(SuppressReason.AntiCheat);
                BackgroundReleaseState state = fake.Release();
                Eq(BackgroundReleaseState.OtherReasonActive, state);
                Eq(true, GameMode.RuntimeBackgroundExempt(state));
                record.Invoke(mode, new object[] { process, true, true, state });
                for (int i = 0; i < 20; i++) record.Invoke(mode, new object[] { process, true, true, state });
                Eq(false, File.Exists(log));
                Eq(true, fake.Core.HasReason(RendererReleaseFixture.Pid, SuppressReason.AntiCheat));
                Eq(0, fake.RestoreCalls); Eq(0L, fake.Core.ApplyOperations);
                foreach (string field in new[] { "BackgroundRestorePending", "BackgroundPlacementOwned" })
                {
                    object debt = fake.Seed(SuppressReason.AntiCheat);
                    RendererReleaseSet(debt, field, true);
                    Eq(BackgroundReleaseState.Pending, fake.Release());
                    Eq(false, GameMode.RuntimeBackgroundExempt(fake.Release()));
                }
                fake.Seed(SuppressReason.Background); fake.Result = SuppressionCore.RestoreResult.Protected;
                state = fake.Release();
                Eq(BackgroundReleaseState.Pending, state); Eq(false, GameMode.RuntimeBackgroundExempt(state));
                record.Invoke(mode, new object[] { process, true, true, state });
                string[] lines = File.ReadAllLines(log);
                Eq(LogEventSeverity.Error, LogStreamView.Parse(lines[lines.Length - 1]).Severity);
                Eq(true, lines[lines.Length - 1].Contains("background=Pending"));
                foreach (object[] args in new[] {
                    new object[] { process, false, true, BackgroundReleaseState.OtherReasonActive },
                    new object[] { process, true, false, BackgroundReleaseState.Ready },
                    new object[] { process, true, true, BackgroundReleaseState.IdentityMismatch } })
                {
                    record.Invoke(mode, args); lines = File.ReadAllLines(log);
                    Eq(LogEventSeverity.Error, LogStreamView.Parse(lines[lines.Length - 1]).Severity);
                }
                record.Invoke(mode, new object[] { process, true, true, BackgroundReleaseState.Ready });
                lines = File.ReadAllLines(log);
                Eq(LogEventSeverity.Info, LogStreamView.Parse(lines[lines.Length - 1]).Severity);
                Eq(true, lines[lines.Length - 1].Contains("recovered"));
                int count = lines.Length;
                for (int i = 0; i < 20; i++) record.Invoke(mode, new object[] { process, true, true, BackgroundReleaseState.Ready });
                Eq(count, File.ReadAllLines(log).Length);
                Console.WriteLine("PASS runtime protection keeps anti-cheat policy, omits healthy debug logs and retains real failures/recovery");
            }
            finally { Logger.LogPath = previousLog; }
        }

        private static void TestRuntimeNativeFloor(string dir, SuppressionCore core)
        {
            foreach (string name in new[]{"rail","wegame_env","tcls_core","ListaryHookHost64"})
            {
                string path=Path.Combine(dir,name+".exe"); File.Copy(typeof(SelfTests).Assembly.Location,path);
                Process child=Process.Start(new ProcessStartInfo(path,"--cpu-cap-test-target")
                    {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true});
                try
                {
                    Eq("READY",CpuCapLine(child)); long born=CpuCapWant(child)[child.Id];
                    IntPtr handle=CpuCapNative.OpenTarget(child.Id,born);
                    try { Eq(IntPtr.Zero,handle); } finally { if(handle!=IntPtr.Zero) Native.CloseHandle(handle); }
                    Eq(AcquireResult.AlreadyProtected,core.Acquire(child.Id,name,SuppressReason.Background,null,SuppressionLevel.Isolated,born));
                    string detail;
                    using (IDisposable lease=FreezeNative.Acquire(new FreezeTarget{Pid=child.Id,Creation=born,Path=path},child.SessionId,Process.GetCurrentProcess().Id,out detail)) Eq(true,lease==null);
                    child.StandardInput.WriteLine("PING");child.StandardInput.Flush();Eq("OK",CpuCapLine(child));
                }
                finally { CpuCapKill(child); }
            }
        }
        private static void TestOriginalBoostPolicy(GameMode mode, SuppressionCore core)
        {
            Process child = CpuCapHelper("--cpu-cap-test-target");
            try
            {
                Eq("READY",CpuCapLine(child)); long born=CpuCapWant(child)[child.Id];
                typeof(GameMode).GetField("enabled",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(mode,true);
                typeof(GameMode).GetField("bgSuppressOn",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(mode,true);
                foreach (bool originallyDisabled in new[]{false,true})
                {
                    Eq(true,Native.TrySetBoostDisabled(child.Handle,originallyDisabled));
                    int original=originallyDisabled ? 1 : 0;
                    foreach (PerformancePreset preset in PresetValue.VisibleOrder())
                    {
                        Eq(true,mode.TrySetPreset(preset));
                        core.Acquire(child.Id,child.ProcessName,SuppressReason.Background,null,SuppressionLevel.Isolated,born);
                        Eq(original,Native.QueryBoostDisabled(child.Handle));
                        core.Reconcile(child.Id,child.ProcessName,SuppressReason.Background,true);
                        Eq(original,Native.QueryBoostDisabled(child.Handle));
                        Eq(true,core.Release(child.Id,SuppressReason.Background));
                        Eq(original,Native.QueryBoostDisabled(child.Handle));
                    }
                }
            }
            finally { core.Release(child.Id,SuppressReason.Background); CpuCapKill(child); }
        }
        private static void TestModeSwitchLock(GameMode mode)
        {
            Eq(true,mode.TrySetPreset(PerformancePreset.Competitive));
            string saved=Settings.LoadStr("PerformancePreset", "");
            FieldInfo activeField=typeof(GameMode).GetField("active",BindingFlags.NonPublic|BindingFlags.Instance);
            activeField.SetValue(mode,true);
            Eq(true,mode.ModeSwitchLocked);Eq(false,mode.TrySetPreset(PerformancePreset.Extreme));
            mode.Preset=PerformancePreset.Extreme;Eq(PerformancePreset.Competitive,mode.Preset);
            Eq(saved,Settings.LoadStr("PerformancePreset",""));
            mode.Enabled=false;Eq(false,mode.TrySetPreset(PerformancePreset.Extreme));
            Eq(false,mode.SetProfileOverride("missing",PolicyCatalog.KeyPreset,"3"));
            activeField.SetValue(mode,false);
            Process game=CpuCapHelper("--freeze-test-exit259");
            try
            {
                Eq("READY",CpuCapLine(game));
                typeof(GameMode).GetField("enabled",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(mode,true);
                FieldInfo detection=typeof(GameMode).GetField("activeDetection",BindingFlags.NonPublic|BindingFlags.Instance);
                detection.SetValue(mode,new GameDetection { RendererPid=game.Id,RendererCreation=CpuCapWant(game)[game.Id] });
                mode.Enabled=false;
                detection.SetValue(mode,null);
                Eq(true,mode.ModeSwitchLocked);Eq(false,mode.TrySetPreset(PerformancePreset.Extreme));
                game.StandardInput.WriteLine("EXIT");game.StandardInput.Flush();Eq(true,game.WaitForExit(5000));
                Eq(false,CpuCapNative.IsAlive(game.Handle));Eq(false,mode.ModeSwitchLocked);
                Eq(true,mode.TrySetPreset(PerformancePreset.Extreme));
            }
            finally { CpuCapKill(game); }
        }
    }
}
#endif
