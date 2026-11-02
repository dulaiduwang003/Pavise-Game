using System;
using System.Collections.Generic;

namespace PaviseApp
{
#if PAVISE_AC_ELIGIBILITY_STUBS
    internal static class AutoCores { internal static long NowTicks { get { return 0; } } }
#endif
    internal static class AntiCheatEligibilityChecks
    {
        private static int checks;
        private static void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
        private static KeyValuePair<string, int> Service(string name, int pid) { return new KeyValuePair<string, int>(name, pid); }
        private static bool Eligible(string name, int pid, int session, IEnumerable<KeyValuePair<string, int>> services, HashSet<int> ancestors, out string reason)
        { return AntiCheatAffinityEligibility.Eligible(name, pid, session, services, ancestors, out reason); }
        private static ProcEntry Process(int pid, int parent, long born) { return new ProcEntry { Pid = pid, ParentPid = parent, Creation = born }; }
        private static IEnumerable<KeyValuePair<string, int>> UnreadableServices()
        { yield return Service("BEService", 1); throw new InvalidOperationException("SCM cache incomplete"); }
        public static int Main()
        {
            try
            {
                string reason;
                HashSet<int> none = new HashSet<int>();
                foreach (string image in new string[] { "SGuard64", "SGuardSvc64", "ACE-Service64", "AntiCheatExpert.Service.exe" })
                    Check(Eligible(image, 7, 0, new[] { Service("AntiCheatExpert", 7) }, none, out reason), "dedicated ACE service rejected: " + image);
                Check(Eligible("easyanticheat_eos.EXE", 7, 0, new[] { Service("EASYANTICHEAT_EOS_ab-12_34", 7) }, none, out reason), "EAC product service rejected");
                Check(Eligible("BEService_x64", 7, 0, new[] { Service("BEService", 7) }, none, out reason), "BattlEye dedicated service rejected");
                Check(Eligible("EasyAntiCheat", 7, 0, new[] { Service("EasyAntiCheat", 7), Service("EasyAntiCheat_EOS_product", 7), Service("Unrelated", 8) }, none, out reason), "same-group service mapping rejected");

                foreach (string image in new string[] { "ACE-PC", "ACE-Tray", "ACE-Helper", "EasyAntiCheat_Launcher", "EAAntiCheat.GameServiceLauncher", "BEServiceLauncher", "vgc", "faceitservice", "svchost", "C:\\fake\\BEService.exe", "BEService.exe:stream" })
                    Check(!Eligible(image, 7, 0, new[] { Service("BEService", 7), Service("AntiCheatExpert", 7) }, none, out reason), "launcher/helper/unknown role admitted: " + image);
                Check(!Eligible("BEService", 7, 1, new[] { Service("BEService", 7) }, none, out reason) && reason == "not-session-zero-service", "interactive bootstrapper admitted");
                Check(!Eligible("BEService", 7, -1, new[] { Service("BEService", 7) }, none, out reason), "unverified session admitted");
                Check(!Eligible("BEService", 0, 0, new[] { Service("BEService", 0) }, none, out reason), "invalid PID admitted");
                Check(!Eligible("BEService", 7, 0, null, none, out reason) && reason == "running-services-unavailable", "unavailable SCM cache admitted");
                Check(!Eligible("BEService", 7, 0, new[] { Service("BEService", 7) }, null, out reason) && reason == "game-ancestry-unavailable", "unknown inheritance graph admitted");
                Check(!Eligible("BEService", 7, 0, new KeyValuePair<string, int>[0], none, out reason), "missing service PID admitted");
                Check(!Eligible("BEService", 7, 0, new[] { Service("BEService", 8) }, none, out reason), "wrong service PID admitted");
                Check(!Eligible("BEService", 7, 0, new[] { Service("BEService", 7), Service("OtherService", 7) }, none, out reason) && reason == "shared-or-unverified-service-host", "shared non-AC host admitted");
                Check(!Eligible("BEService", 7, 0, new[] { Service("BEService", 7), Service("EasyAntiCheat", 7) }, none, out reason), "mixed AC shared host admitted");
                Check(!Eligible("EasyAntiCheat_EOS", 7, 0, new[] { Service("EasyAntiCheat_EOS_", 7) }, none, out reason), "empty product suffix admitted");
                Check(!Eligible("EasyAntiCheat_EOS", 7, 0, new[] { Service("EasyAntiCheat_EOS_evil/service", 7) }, none, out reason), "malformed product service admitted");
                Check(!Eligible("EasyAntiCheat_EOS", 7, 0, new[] { Service("EasyAntiCheat_EOSLauncher", 7) }, none, out reason), "prefix collision admitted");
                Check(!Eligible("BEService", 7, 0, UnreadableServices(), none, out reason) && reason == "running-services-unavailable", "incomplete service enumeration admitted");

                var graph = new ProcessSnapshot(new[] { Process(30, 20, 300), Process(20, 7, 200), Process(7, 1, 100), Process(1, 0, 50), Process(40, 7, 400), Process(99, 0, 99) });
                HashSet<int> ancestors = AntiCheatAffinityEligibility.GameAncestors(graph, new[] { 30, 40, 500, 0 });
                Check(ancestors.SetEquals(new[] { 30, 20, 7, 1, 40, 500 }), "ancestor chains were incomplete or included unrelated processes");
                Check(!Eligible("BEService", 7, 0, new[] { Service("BEService", 7) }, ancestors, out reason) && reason == "game-process-or-ancestor", "game ancestor service admitted");
                Check(!Eligible("BEService", 30, 0, new[] { Service("BEService", 30) }, ancestors, out reason), "game PID collision admitted");
                graph = new ProcessSnapshot(new[] { Process(30, 20, 300), Process(20, 7, 400), Process(7, 1, 100), Process(1, 0, 50) });
                Check(AntiCheatAffinityEligibility.GameAncestors(graph, new[] { 30 }).SetEquals(new[] { 30 }), "reused parent PID created false ancestry");
                graph = new ProcessSnapshot(new[] { Process(30, 20, 300), Process(20, 7, 0), Process(7, 1, 100) });
                Check(AntiCheatAffinityEligibility.GameAncestors(graph, new[] { 30 }).SetEquals(new[] { 30 }), "unknown parent birth created false ancestry");
                graph = new ProcessSnapshot(new[] { Process(30, 20, 300), Process(20, 30, 300) });
                Check(AntiCheatAffinityEligibility.GameAncestors(graph, new[] { 30 }).SetEquals(new[] { 30, 20 }), "cyclic ancestry did not terminate");
                graph = new ProcessSnapshot(new[] { Process(30, 30, 300) });
                Check(AntiCheatAffinityEligibility.GameAncestors(graph, new[] { 30 }).SetEquals(new[] { 30 }), "self parent did not terminate");
                graph = new ProcessSnapshot(new[] { Process(30, 20, 0), Process(20, 1, 100) });
                Check(AntiCheatAffinityEligibility.GameAncestors(graph, new[] { 30 }).SetEquals(new[] { 30 }), "unknown child birth admitted ancestry");
                Check(AntiCheatAffinityEligibility.GameAncestors(null, new[] { 30 }) == null, "missing snapshot did not remain unknown");
                Check(AntiCheatAffinityEligibility.GameAncestors(graph, null) == null, "missing game list did not remain unknown");
                Console.WriteLine("PASS anti-cheat eligibility assertions=" + checks + " native_writes=false product_main=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
