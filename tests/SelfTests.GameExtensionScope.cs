#if PAVISE_SELFTEST
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunGameExtensionScopeRegressionTests()
        {
            CompositeGameExtension modules = GameExtension.Current as CompositeGameExtension;
            Eq(true, modules != null);
            Eq(1, modules.Modules.Length);
            Eq(true, modules.Modules[0] is LolGameExtension);
            foreach (var profile in new[] {
                new GameProfile { Id = "cf", Name = "穿越火线", Root = @"C:\WeGameApps\CF", ExecutablePath = @"C:\WeGameApps\CF\crossfire.exe" },
                new GameProfile { Id = "nz", Name = "逆战", Root = @"C:\WeGameApps\NZ", ExecutablePath = @"C:\WeGameApps\NZ\NZ.exe" },
                new GameProfile { Id = "delta", Name = "三角洲行动", Root = @"C:\WeGameApps\Delta", LearnedExecutablePath = @"C:\WeGameApps\Delta\game.exe" },
                new GameProfile { Id = "steam", Name = "Steam game", ExecutablePath = @"C:\Steam\steamapps\common\Game\game.exe" },
                new GameProfile { Id = "epic", Name = "Epic game", ExecutablePath = @"C:\Epic Games\Game\game.exe" },
                new GameProfile { Id = "empty", Name = "Game" }
            })
            {
                Settings.Save("WgShellAuto_" + profile.Id, true);
                Eq(false, GameExtension.Current.AppliesTo(profile));
                Eq(false, modules.Owner(profile) is WeGameGameExtension);
                Eq<GameCardExtension>(null, GameExtension.CreateCard(profile));
                // Both legacy and newer saved opt-ins must not reactivate the retired module.
                if (!string.IsNullOrEmpty(profile.Root) || !string.IsNullOrEmpty(profile.ExecutablePath))
                    Eq(true, WeGameGameExtension.SetProfileEnabled(profile, true));
                GameExtension.NotifySession(profile, 99, 123, true);
                Eq(false, GameExtension.Current.AppliesTo(profile));
                Eq<GameCardExtension>(null, GameExtension.CreateCard(profile));
                // 旧开关不替代已观测的渲染进程通知。
                Eq(true, Settings.Load("WgShellAuto_" + profile.Id, false));
                using (var service = new WeGameShellService())
                {
                    service.NotifySession(profile, 99, 123, true);
                    Eq(false, service.GetSnapshot(profile).Busy);
                    Eq(false, service.GetSnapshot(profile).Detached);
                    int epoch = service.GetSnapshot(profile).Generation;
                    service.NotifySession(null, 0, 0, false);
                    service.NotifySession(profile, 99, 123, true);
                    Eq(false, epoch == service.GetSnapshot(profile).Generation);
                    Eq(false, service.GetSnapshot(profile).Detached);
                }
            }
            Eq(false, GameExtension.Current.AppliesTo(null));
            foreach (var profile in new[] {
                new GameProfile { Id = "lol-cn", Name = "英雄联盟" },
                new GameProfile { Id = "lol-global", Name = "League of Legends" },
                new GameProfile { Id = "lol-path", Name = "Game", ExecutablePath = @"C:\WeGameApps\LOL\LeagueClient\LeagueClient.exe" }
            })
            {
                Eq(true, GameExtension.Current.AppliesTo(profile));
                Eq(true, modules.Owner(profile) is LolGameExtension);
                Eq(false, WeGameGameExtension.AppliesToProfile(profile));
            }
            var custom = new GameProfile { Id = "custom-wegame", Name = "Local game", Root = @"C:\Games\Custom", ExecutablePath = @"C:\Games\Custom\game.exe" };
            Eq(false, GameExtension.Current.AppliesTo(custom));
            Eq(true, WeGameGameExtension.SetProfileEnabled(custom, true));
            Eq(false, modules.Owner(custom) is WeGameGameExtension);
            Eq<GameCardExtension>(null, GameExtension.CreateCard(custom));
            Eq(true, AppDataReset.IsUserPreference(WeGameGameExtension.PreferencePrefix + custom.Id));
            var learned = custom.Clone(); learned.LearnedExecutablePath = @"C:\Games\Custom\bin\render.exe";
            Eq(true, WeGameGameExtension.AppliesToProfile(learned));
            var moved = custom.Clone(); moved.Root = @"C:\Games\Moved"; moved.ExecutablePath = @"C:\Games\Moved\game.exe";
            Eq(false, WeGameGameExtension.AppliesToProfile(moved));
            Eq(true, WeGameGameExtension.SetProfileEnabled(custom, false));
            Eq(false, GameExtension.Current.AppliesTo(custom));
            foreach (string path in new[] { @"C:\MyWeGameApps\CF\game.exe", @"C:\Tencent\CF\game.exe", @"C:\WeGameApps2\CF\game.exe", @"C:\WeGameApps\..\Steam\game.exe" })
                Eq(false, WeGameGameExtension.HasWeGameLocation(new GameProfile { ExecutablePath = path }));
            var disabled = new GameProfile { Id = "disabled", Root = @"C:\WeGameApps\Disabled", ExecutablePath = @"C:\WeGameApps\Disabled\game.exe" };
            Eq(true, WeGameGameExtension.AppliesToProfile(disabled));
            Eq(true, WeGameGameExtension.SetProfileEnabled(disabled, false));
            Eq(false, WeGameGameExtension.AppliesToProfile(disabled));
            Eq(false, WeGameGameExtension.SetProfileEnabled(new GameProfile { Id = "relative", Root = "Games", ExecutablePath = "Games\\game.exe" }, true));
            Settings.SuspendWritesForReset();
            Eq(false, WeGameGameExtension.SetProfileEnabled(disabled, true));
            Eq(false, WeGameGameExtension.AppliesToProfile(disabled));
            Settings.UseTransientStoreForCurrentProcess();
            CheckObservedRendererGate();
            Console.WriteLine("PASS extension scope: WeGame module retired despite saved opt-ins; no WeGame cards or active owner; LoL ownership retained; no service started");
            CheckLeagueDiscoveryShutdown();
        }

        private static void CheckObservedRendererGate()
        {
            var profile = new GameProfile { Id = "observed-gate", Name = "Game", Root = @"C:\WeGameApps\Gate", ExecutablePath = @"C:\WeGameApps\Gate\Game.exe" };
            var hit = new GameDetection { Profile = profile, RendererPid = 1234, RendererCreation = 5678, RendererName = "Game", RendererPath = profile.ExecutablePath, RendererCandidateSelected = true };
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(null, true));
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, false));
            Eq(true, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.RendererForeground = false;
            Eq(true, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.RequiresGpuConfirm = true;
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.RequiresGpuConfirm = false; hit.RendererSafetyOnly = true;
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.RendererSafetyOnly = false; hit.RendererCandidateSelected = false;
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.RendererCandidateSelected = true; hit.Profile.ForceTrigger = true;
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.Profile.ForceTrigger = false; hit.RendererCreation = 0;
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.RendererCreation = 5678; hit.RendererPid = 0;
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.RendererPid = 1234; hit.RendererPath = @"C:\WeGameApps\Gate\NewRender.exe"; hit.RendererName = "NewRender";
            profile.LearnedExecutablePath = hit.RendererPath;
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.RendererPath = profile.ExecutablePath; hit.RendererName = "wegame";
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
            hit.RendererName = "Game";
            Eq(true, WeGameGameExtension.SetProfileEnabled(profile, false));
            Eq(false, WeGameGameExtension.CanAutoDetachRenderer(hit, true));
        }

        private static void CheckLeagueDiscoveryShutdown()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            foreach (bool dispose in new[] { false, true })
            {
                string cachedRoot = Path.Combine(Path.GetTempPath(), "Pavise-mock-discovery-" + Guid.NewGuid().ToString("N"));
                Settings.SaveStr("LolInstallPath", cachedRoot);
                using (var entered = new ManualResetEvent(false))
                using (var service = new LolOptimizationService())
                {
                    Exception workerError = null;
                    bool observedCancellation = false;
                    int weGameLookups = 0;
                    service.FindLolRootForTest = delegate(string preferred, bool deep, Func<bool> cancelled)
                    {
                        Eq(cachedRoot, preferred); Eq(true, deep); Eq(true, cancelled != null);
                        entered.Set();
                        var clock = Stopwatch.StartNew();
                        while (!cancelled() && clock.ElapsedMilliseconds < 2000) Thread.Sleep(1);
                        observedCancellation = cancelled();
                        return cachedRoot + "-partial";
                    };
                    service.FindWeGameRootForTest = delegate { weGameLookups++; return null; };
                    typeof(LolOptimizationService).GetField("running", flags).SetValue(service, true);
                    typeof(LolOptimizationService).GetField("enabled", flags).SetValue(service, true);
                    typeof(LolOptimizationService).GetField("cleanupEnabled", flags).SetValue(service, true);
                    service.RequestDiscovery();
                    var worker = new Thread(delegate()
                    {
                        try { typeof(LolOptimizationService).GetMethod("RunCycle", flags).Invoke(service, null); }
                        catch (Exception error) { workerError = error; }
                    });
                    worker.IsBackground = true;
                    typeof(LolOptimizationService).GetField("worker", flags).SetValue(service, worker);
                    worker.Start();
                    Eq(true, entered.WaitOne(2000));
                    if (dispose) service.Dispose(); else service.Stop();
                    Eq(true, worker.Join(500));
                    if (workerError != null) throw workerError;
                    Eq(true, observedCancellation); Eq(0, weGameLookups);
                    Eq(cachedRoot, Settings.LoadStr("LolInstallPath", null));
                    Eq(DateTime.MinValue, (DateTime)typeof(LolOptimizationService).GetField("nextFullProcessScanUtc", flags).GetValue(service));
                    if (!dispose)
                    {
                        // 退出后的显式恢复仍能定位客户端，不受后台停机取消影响。
                        service.FindLolRootForTest = delegate(string preferred, bool deep, Func<bool> cancelled)
                        { Eq(false, cancelled()); return null; };
                        object[] discover = { null, null, true, false };
                        typeof(LolOptimizationService).GetMethod("Discover", flags).Invoke(service, discover);
                        Eq(1, weGameLookups);
                    }
                }
            }
            Settings.Remove("LolInstallPath");
            Console.WriteLine("PASS League discovery shutdown: stop/dispose cancels mock discovery, skips scans and partial writes; explicit restoration discovery remains available");
        }
    }
}
#endif
