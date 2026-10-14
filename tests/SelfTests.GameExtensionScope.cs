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
            Eq(true, GameExtension.Current is LolGameExtension);
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
                Eq<GameCardExtension>(null, GameExtension.CreateCard(profile));
                // 旧配置还开着也不能触发脱壳
                GameExtension.NotifySession(profile, 99, 123, true);
                Eq(true, Settings.Load("WgShellAuto_" + profile.Id, false));
            }
            Eq(false, GameExtension.Current.AppliesTo(null));
            foreach (var profile in new[] {
                new GameProfile { Id = "lol-cn", Name = "英雄联盟" },
                new GameProfile { Id = "lol-global", Name = "League of Legends" },
                new GameProfile { Id = "lol-path", Name = "Game", ExecutablePath = @"C:\WeGameApps\LOL\LeagueClient\LeagueClient.exe" }
            }) Eq(true, GameExtension.Current.AppliesTo(profile));
            Console.WriteLine("PASS game extension scope: League of Legends only; legacy WeGame flags inactive; no service started");
            CheckLeagueDiscoveryShutdown();
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
