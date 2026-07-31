// @author bdth 2074055628@qq.com

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal static partial class SelfTests
    {
        private static void TestLolCredentialParsing()
        {
            int port;
            string token;
            Eq(true, LolCredentialParser.TryParseCommandLine(
                "LeagueClientUx.exe --app-port=63352 --remoting-auth-token=\"abc-123_DEF\" --region=TENCENT",
                out port, out token));
            Eq(63352, port);
            Eq("abc-123_DEF", token);
            Eq(true, LolCredentialParser.TryParseCommandLine(
                "--remoting-auth-token tokenValue --app-port 49152", out port, out token));
            Eq(49152, port);
            Eq("tokenValue", token);
            Eq(true, LolCredentialParser.TryParseCommandLine(
                "--app-port=41001 --remoting-auth-token=oldToken\r\n"
                + "--app-port=41002 --remoting-auth-token=newToken",
                out port, out token));
            Eq(41002, port);
            Eq("newToken", token);
            Eq(false, LolCredentialParser.TryParseCommandLine(
                "--app-port=70000 --remoting-auth-token=x", out port, out token));
            Eq(false, LolCredentialParser.TryParseCommandLine(
                "--app-port=49152 --remoting-auth-token=\"unterminated", out port, out token));
            Eq(true, LolCredentialParser.TryParseLockfile(
                "LeagueClient:1234:54321:localToken:https", out port, out token));
            Eq(54321, port);
            Eq("localToken", token);
            Eq(false, LolCredentialParser.TryParseLockfile(
                "LeagueClient:1234:54321:localToken:http", out port, out token));
            int lockPid;
            Eq(true, LolCredentialParser.TryParseLockfile(
                "LeagueClient:1234:54321:localToken:https",
                out lockPid, out port, out token));
            Eq(1234, lockPid);
            Eq(false, LolCredentialParser.TryParseLockfile(
                "LeagueClient:not-a-pid:54321:localToken:https",
                out lockPid, out port, out token));
            Eq(true, LolRuntimeProcesses.IsExpectedSession(7, 7));
            Eq(false, LolRuntimeProcesses.IsExpectedSession(7, 8));
            Eq(false, LolRuntimeProcesses.IsExpectedSession(-1, 7));
            Eq(false, LolRuntimeProcesses.IsExpectedSession(7, -1));
            const long creationTicks = 134000000000000000L;
            Eq(true, LolRuntimeProcesses.CredentialCreationMatches(
                creationTicks, creationTicks));
            Eq(true, LolRuntimeProcesses.CredentialCreationMatches(
                creationTicks,
                creationTicks + TimeSpan.TicksPerMillisecond));
            Eq(false, LolRuntimeProcesses.CredentialCreationMatches(
                creationTicks,
                creationTicks + TimeSpan.TicksPerMillisecond + 1));
            Eq(false, LolRuntimeProcesses.CredentialCreationMatches(
                0, creationTicks));
            int currentSession;
            Eq(true, LolRuntimeProcesses.TryGetCurrentSessionId(
                out currentSession));
            if (currentSession < 0)
                throw new Exception("current process session was invalid");
            using (Process current = Process.GetCurrentProcess())
            {
                string ownedPath;
                Eq(true, LolRuntimeProcesses.TryGetOwnedImagePath(
                    current, currentSession, out ownedPath));
                if (string.IsNullOrEmpty(ownedPath))
                    throw new Exception(
                        "current-session image path was empty");
                int foreignSession = currentSession == int.MaxValue
                    ? currentSession - 1 : currentSession + 1;
                Eq(false, LolRuntimeProcesses.TryGetOwnedImagePath(
                    current, foreignSession, out ownedPath));
            }
            string parsedPhase;
            if (!LolLcuClient.TryParseGameflowPhaseBody(
                    "\"InProgress\"", out parsedPhase))
                throw new Exception("valid InProgress phase was rejected");
            Eq("InProgress", parsedPhase);
            if (!LolLcuClient.TryParseGameflowPhaseBody(
                    "  \"Reconnect\"  ", out parsedPhase))
                throw new Exception("valid Reconnect phase was rejected");
            Eq("Reconnect", parsedPhase);
            if (!LolLcuClient.TryParseGameflowPhaseBody(
                    "\"CheckedIntoTournament\"", out parsedPhase))
                throw new Exception("valid tournament phase was rejected");
            Eq("CheckedIntoTournament", parsedPhase);
            Eq(false, LolLcuClient.TryParseGameflowPhaseBody(
                "InProgress", out parsedPhase));
            Eq(false, LolLcuClient.TryParseGameflowPhaseBody(
                "\"ok\"", out parsedPhase));
            Eq(false, LolLcuClient.TryParseGameflowPhaseBody(
                "{\"phase\":\"InProgress\"}", out parsedPhase));

            Eq(5, LolOptimizationService.CredentialRetrySeconds(1));
            Eq(10, LolOptimizationService.CredentialRetrySeconds(2));
            Eq(30, LolOptimizationService.CredentialRetrySeconds(3));
            Eq(60, LolOptimizationService.CredentialRetrySeconds(4));
            Eq(60, LolOptimizationService.CredentialRetrySeconds(100));
            Eq(2, LolOptimizationService.RestoreCredentialRetrySeconds(1));
            Eq(5, LolOptimizationService.RestoreCredentialRetrySeconds(2));
            Eq(10, LolOptimizationService.RestoreCredentialRetrySeconds(3));
            Eq(30, LolOptimizationService.RestoreCredentialRetrySeconds(4));
            Eq(30, LolOptimizationService.RestoreCredentialRetrySeconds(100));

            Eq(30000, LolOptimizationService.WorkerDelayMs(
                false, true, true, true, true, false, false));
            Eq(15000, LolOptimizationService.WorkerDelayMs(
                true, true, false, false, false, false, false));
            Eq(10000, LolOptimizationService.WorkerDelayMs(
                true, true, false, true, false, false, false));
            Eq(6000, LolOptimizationService.WorkerDelayMs(
                true, false, true, true, false, false, false));
            Eq(5000, LolOptimizationService.WorkerDelayMs(
                true, false, true, true, true, false, false));
            Eq(20000, LolOptimizationService.WorkerDelayMs(
                true, false, true, true, true, true, true));
            Eq(20000, LolOptimizationService.WorkerDelayMs(
                false, false, false, false, false, false, true));

            Eq(true, LolOptimizationService.IsConfirmedMatchExitPhase("Lobby"));
            Eq(true, LolOptimizationService.IsConfirmedMatchExitPhase("WaitingForStats"));
            Eq(true, LolOptimizationService.IsConfirmedMatchExitPhase("TerminatedInError"));
            Eq(true, LolOptimizationService.IsConfirmedMatchExitPhase("FailedToLaunch"));
            Eq(true, LolOptimizationService.IsConfirmedMatchExitPhase(
                "CheckedIntoTournament"));
            Eq(false, LolOptimizationService.IsConfirmedMatchExitPhase("Reconnect"));
            Eq(false, LolOptimizationService.IsConfirmedMatchExitPhase("GameStart"));
            Eq(false, LolOptimizationService.IsConfirmedMatchExitPhase(null));
            Eq(true, LolOptimizationService.IsSameMatchPhase("InProgress"));
            Eq(true, LolOptimizationService.IsSameMatchPhase("Reconnect"));
            Eq(true, LolOptimizationService.IsSameMatchPhase("GameStart"));
            Eq(false, LolOptimizationService.IsSameMatchPhase("Lobby"));
            Eq(true, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "None", false, false));
            Eq(true, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "Lobby", false, false));
            Eq(true, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "Matchmaking", false, false));
            Eq(false, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "None", true, false));
            Eq(false, LolOptimizationService.IsVerifiedCleanupSession(
                true, false, "None", false, false));
            Eq(false, LolOptimizationService.IsVerifiedCleanupSession(
                false, true, "None", false, false));
            Eq(false, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, null, false, false));
            Eq(false, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "InProgress", false, true));
            Eq(false, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "InProgress", true, false));
            Eq(true, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "InProgress", true, true));
            Eq(false, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "GameStart", false, false));
            Eq(true, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "GameStart", true, true));
            Eq(false, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "Reconnect", false, true));
            Eq(false, LolOptimizationService.IsVerifiedCleanupSession(
                true, true, "Unknown", false, false));
            Eq(false, LolOptimizationService.ShouldResetMatchSafetyState(
                false, true, false));
            Eq(true, LolOptimizationService.ShouldResetMatchSafetyState(
                false, true, true));
            Eq(true, LolOptimizationService.ShouldResetMatchSafetyState(
                true, false, false));
            Eq(false, LolOptimizationService.ShouldResetMatchSafetyState(
                false, false, true));
            Eq(true, LolRuntimeProcesses.IsCoreIdentityCandidateName(
                "LeagueClientUx.exe"));
            Eq(true, LolRuntimeProcesses.IsCoreIdentityCandidateName(
                "League of Legends"));
            Eq(false, LolRuntimeProcesses.IsCoreIdentityCandidateName(
                "browser"));
            Eq(true, LolRuntimeProcesses.IsWeGameDiscoveryCandidateName(
                "WeGame.exe"));
            Eq(true, LolRuntimeProcesses.IsWeGameDiscoveryCandidateName(
                "wegame_env"));
            Eq(false, LolRuntimeProcesses.IsWeGameDiscoveryCandidateName(
                "browser.exe"));
            Eq(true, LolRuntimeProcesses.IsLeagueClientProcess(
                @"D:\League\LeagueClient.exe",
                "LeagueClient.exe", @"D:\League"));
            Eq(false, LolRuntimeProcesses.IsLeagueClientProcess(
                @"D:\League\LeagueClientUxRender.exe",
                "LeagueClientUxRender.exe", @"D:\League"));

            int absentSamples = 0;
            Eq(false, LolOptimizationService.ConfirmClientExit(
                false, false, false, ref absentSamples));
            Eq(0, absentSamples);
            for (int i = 0; i < 3; i++)
                Eq(false, LolOptimizationService.ConfirmClientExit(
                    true, false, false, ref absentSamples));
            Eq(true, LolOptimizationService.ConfirmClientExit(
                true, false, false, ref absentSamples));
            Eq(false, LolOptimizationService.ConfirmClientExit(
                true, false, true, ref absentSamples));
            Eq(0, absentSamples);

            var respawns = new Queue<DateTime>();
            DateTime now = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);
            Eq(false, LolOptimizationService.RegisterUxRespawn(
                now, respawns));
            Eq(false, LolOptimizationService.RegisterUxRespawn(
                now.AddSeconds(10), respawns));
            Eq(false, LolOptimizationService.RegisterUxRespawn(
                now.AddSeconds(20), respawns));
            Eq(true, LolOptimizationService.RegisterUxRespawn(
                now.AddSeconds(30), respawns));
            Eq(false, LolOptimizationService.RegisterUxRespawn(
                now.AddSeconds(91), respawns));
            Eq(1, respawns.Count);

            respawns.Clear();
            Eq(false, LolOptimizationService.RegisterUxRespawn(
                now, respawns));
            Eq(false, LolOptimizationService.RegisterUxRespawn(
                now.AddMilliseconds(59900), respawns));
            Eq(false, LolOptimizationService.RegisterUxRespawn(
                now.AddMilliseconds(60100), respawns));
            Eq(false, LolOptimizationService.RegisterUxRespawn(
                now.AddMilliseconds(60200), respawns));
            Eq(true, LolOptimizationService.RegisterUxRespawn(
                now.AddMilliseconds(60300), respawns));

            var cleanupCycles = new Queue<DateTime>();
            Eq(false, LolOptimizationService.RegisterCleanupKillCycle(
                now, cleanupCycles));
            Eq(false, LolOptimizationService.RegisterCleanupKillCycle(
                now.AddSeconds(10), cleanupCycles));
            Eq(false, LolOptimizationService.RegisterCleanupKillCycle(
                now.AddSeconds(20), cleanupCycles));
            Eq(true, LolOptimizationService.RegisterCleanupKillCycle(
                now.AddSeconds(30), cleanupCycles));
            Eq(false, LolOptimizationService.RegisterCleanupKillCycle(
                now.AddSeconds(91), cleanupCycles));

            var candidates = new List<LolLcuCredentials>
            {
                new LolLcuCredentials(41001, "stale"),
                new LolLcuCredentials(41001, "stale"),
                new LolLcuCredentials(41002, "live")
            };
            int candidateProbes;
            LolLcuCredentials selected = LolLcuCredentialSource.SelectReachable(
                candidates,
                delegate(LolLcuCredentials candidate)
                {
                    return candidate.Port == 41002;
                },
                3,
                out candidateProbes);
            Eq(41002, selected.Port);
            Eq(2, candidateProbes);
            selected = LolLcuCredentialSource.SelectReachable(
                candidates,
                delegate { return false; },
                1,
                out candidateProbes);
            Eq(null, selected);
            Eq(1, candidateProbes);
            var sharedSeen = new HashSet<string>(StringComparer.Ordinal);
            selected = LolLcuCredentialSource.SelectReachable(
                new List<LolLcuCredentials>
                {
                    new LolLcuCredentials(41001, "stale-a"),
                    new LolLcuCredentials(41002, "stale-b")
                },
                delegate { return false; },
                2,
                sharedSeen,
                out candidateProbes);
            Eq(null, selected);
            Eq(2, candidateProbes);
            selected = LolLcuCredentialSource.SelectReachable(
                new List<LolLcuCredentials>
                {
                    new LolLcuCredentials(41001, "stale-a"),
                    new LolLcuCredentials(41002, "stale-b"),
                    new LolLcuCredentials(41003, "live")
                },
                delegate(LolLcuCredentials candidate)
                {
                    return candidate.Port == 41003;
                },
                2,
                sharedSeen,
                out candidateProbes);
            Eq(41003, selected.Port);
            Eq(1, candidateProbes);
            sharedSeen.Clear();
            selected = LolLcuCredentialSource.SelectReachable(
                new List<LolLcuCredentials>
                {
                    new LolLcuCredentials(41001, "stale-a"),
                    new LolLcuCredentials(41002, "stale-b"),
                    new LolLcuCredentials(41003, "live")
                },
                delegate { return false; },
                2,
                sharedSeen,
                out candidateProbes);
            Eq(null, selected);
            Eq(2, candidateProbes);
            selected = LolLcuCredentialSource.SelectReachable(
                new List<LolLcuCredentials>
                {
                    new LolLcuCredentials(41003, "live")
                },
                delegate { return true; },
                2,
                sharedSeen,
                out candidateProbes);
            Eq(41003, selected.Port);
            Eq(1, candidateProbes);

            string serializedLease;
            Eq(true, LolHeadlessLease.TrySerialize(
                @"D:\Games\League", 321, 987654321, out serializedLease));
            LolHeadlessLeaseInfo lease;
            Eq(true, LolHeadlessLease.TryParse(serializedLease, out lease));
            Eq(321, lease.GamePid);
            Eq(987654321L, lease.GameCreation);
            Eq(false, lease.Legacy);
            Eq(false, LolHeadlessLease.TryParse(
                "V2|broken|321|987654321", out lease));

            string readyToken = LolWatchdog.CreateReadyToken();
            string secondReadyToken = LolWatchdog.CreateReadyToken();
            Eq(true, LolWatchdog.IsReadyToken(readyToken));
            Eq(true, LolWatchdog.IsReadyToken(secondReadyToken));
            Eq(false, LolWatchdog.IsReadyToken(readyToken.Substring(1)));
            Eq(false, LolWatchdog.IsReadyToken(
                readyToken.Substring(0, readyToken.Length - 1) + "Z"));
            if (string.Equals(
                    readyToken, secondReadyToken, StringComparison.Ordinal))
                throw new Exception("watchdog ready challenges were reused");
            string readyName = LolWatchdog.ReadyEventName(
                @"D:\Games\League", 321, 987654321, readyToken);
            if (string.IsNullOrEmpty(readyName))
                throw new Exception("watchdog ready event name was not created");
            bool readyEventCreated;
            using (var readyEvent = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                readyName,
                out readyEventCreated))
            {
                Eq(true, readyEventCreated);
                Eq(true, LolWatchdog.SignalReady(
                    @"D:\Games\League", 321, 987654321, readyToken));
                Eq(true, readyEvent.WaitOne(0));
            }
            if (string.Equals(
                    readyName,
                    LolWatchdog.ReadyEventName(
                        @"D:\Games\League", 322, 987654321, readyToken),
                    StringComparison.Ordinal)
                || string.Equals(
                    readyName,
                    LolWatchdog.ReadyEventName(
                        @"D:\Games\League", 321, 987654322, readyToken),
                    StringComparison.Ordinal)
                || string.Equals(
                    readyName,
                    LolWatchdog.ReadyEventName(
                        @"D:\Games\League", 321, 987654321, secondReadyToken),
                    StringComparison.Ordinal)
                || string.Equals(
                    readyName,
                    LolWatchdog.ReadyEventName(
                        @"D:\Games\OtherLeague", 321, 987654321, readyToken),
                    StringComparison.Ordinal))
                throw new Exception(
                    "watchdog ready event was not bound to lease identity");
            Eq(null, LolWatchdog.ReadyEventName(
                @"D:\Games\League", 321, 987654321, "not-a-token"));

            string guardSuffix = Process.GetCurrentProcess().Id + "_"
                + Guid.NewGuid().ToString("N");
            string guardMutexName = "Aegis_TestLolGuard_" + guardSuffix;
            string guardAliveName = "Aegis_TestLolAlive_" + guardSuffix;
            Exception guardThreadError = null;
            using (var holderReady = new ManualResetEvent(false))
            using (var holderRelease = new ManualResetEvent(false))
            {
                var holder = new Thread(new ThreadStart(delegate
                {
                    Mutex guardMutex = null;
                    EventWaitHandle guardAlive = null;
                    bool held = false;
                    try
                    {
                        bool mutexCreated;
                        guardMutex = new Mutex(
                            true, guardMutexName, out mutexCreated);
                        held = mutexCreated;
                        bool eventCreated;
                        guardAlive = new EventWaitHandle(
                            true,
                            EventResetMode.ManualReset,
                            guardAliveName,
                            out eventCreated);
                        if (!held || !eventCreated)
                            throw new Exception(
                                "watchdog liveness objects were not exclusive");
                    }
                    catch (Exception ex) { guardThreadError = ex; }
                    finally { holderReady.Set(); }
                    if (held && guardThreadError == null)
                        holderRelease.WaitOne();
                    if (held && guardMutex != null)
                        try { guardMutex.ReleaseMutex(); } catch { }
                    if (guardAlive != null) guardAlive.Close();
                    if (guardMutex != null) guardMutex.Close();
                }));
                holder.IsBackground = true;
                holder.Start();
                if (!holderReady.WaitOne(3000))
                    throw new Exception(
                        "watchdog liveness holder did not start");
                try
                {
                    if (guardThreadError != null) throw guardThreadError;
                    Eq(true, LolWatchdog.GuardObjectsReady(
                        guardMutexName, guardAliveName));
                }
                finally
                {
                    holderRelease.Set();
                    holder.Join(3000);
                }
                Eq(false, LolWatchdog.GuardObjectsReady(
                    guardMutexName, guardAliveName));
            }

            var service = new LolOptimizationService();
            int published = 0;
            service.Changed += delegate { published++; };
            service.RaiseChanged();
            service.RaiseChanged();
            Eq(1, published);
            service.Dispose();

            string logRoot = Path.Combine(Path.GetTempPath(),
                "AegisLolLogs_" + Process.GetCurrentProcess().Id + "_"
                + Guid.NewGuid().ToString("N"));
            try
            {
                string logs = Path.Combine(logRoot, "LeagueClient", "Logs");
                Directory.CreateDirectory(logs);
                for (int i = 0; i < 30; i++)
                {
                    string mainLog = Path.Combine(
                        logs, "LeagueClient-" + i.ToString("D2") + ".log");
                    File.WriteAllText(mainLog, i.ToString(), Encoding.UTF8);
                    File.SetLastWriteTimeUtc(mainLog, now.AddMinutes(i));
                    string helperLog = Path.Combine(
                        logs, "LeagueClientUxHelper-" + i.ToString("D2") + ".log");
                    File.WriteAllText(helperLog, "helper", Encoding.UTF8);
                    File.SetLastWriteTimeUtc(helperLog, now.AddHours(2).AddMinutes(i));
                }
                string[] recent = LolLcuCredentialSource.SelectRecentLogPaths(logRoot, 8);
                Eq(8, recent.Length);
                for (int i = 0; i < recent.Length; i++)
                {
                    if (Path.GetFileName(recent[i]).IndexOf(
                        "LeagueClientUxHelper", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new Exception("UxHelper log entered credential fallback");
                    if (i > 0 && File.GetLastWriteTimeUtc(recent[i - 1])
                        < File.GetLastWriteTimeUtc(recent[i]))
                        throw new Exception("credential log candidates are not newest-first");
                }
                Eq("LeagueClient-29.log", Path.GetFileName(recent[0]));
                Eq("LeagueClient-22.log", Path.GetFileName(recent[7]));

                string older = Path.Combine(logRoot, "lockfile");
                string newer = Path.Combine(logRoot, "LeagueClient", "lockfile");
                File.WriteAllText(older, "old", Encoding.UTF8);
                File.WriteAllText(newer, "new", Encoding.UTF8);
                File.SetLastWriteTimeUtc(older, now);
                File.SetLastWriteTimeUtc(newer, now.AddMinutes(1));
                string[] ordered = LolLcuCredentialSource.OrderExistingCredentialFiles(
                    new[] { older, newer });
                Eq(2, ordered.Length);
                Eq(newer, ordered[0]);
                Eq(older, ordered[1]);

                string longLog = Path.Combine(
                    logs, "LeagueClient-long.log");
                string credentialLine =
                    "--app-port=42001 --remoting-auth-token=headToken";
                File.WriteAllText(
                    longLog,
                    credentialLine + Environment.NewLine
                        + new string('x', 600 * 1024),
                    Encoding.UTF8);
                string boundedLog;
                if (!LolLcuCredentialSource.TryReadHeadAndTail(
                        longLog, 64 * 1024, 448 * 1024, out boundedLog))
                    throw new Exception("bounded log read failed");
                if (!LolCredentialParser.TryParseCommandLine(
                        boundedLog, out port, out token))
                    throw new Exception("credential in log head was not retained");
                Eq(42001, port);
                Eq("headToken", token);

                long generation1 =
                    LolLcuCredentialSource.CredentialGenerationStamp(logRoot);
                File.AppendAllText(newer, "changed", Encoding.UTF8);
                File.SetLastWriteTimeUtc(newer, now.AddMinutes(2));
                long generation2 =
                    LolLcuCredentialSource.CredentialGenerationStamp(logRoot);
                if (generation1 == generation2)
                    throw new Exception("credential generation did not change");
            }
            finally
            {
                try { Directory.Delete(logRoot, true); } catch { }
            }
        }

        private static void TestLolCleanupBoundary()
        {
            const string lol = @"D:\Games\英雄联盟";
            const string weGame = @"D:\Apps\WeGame";
            Eq(true, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\Cross\coach\LolAICoach.exe", "LolAICoach.exe", lol, weGame));
            Eq(true, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\LeagueClient\FeedBack\FeedBack.exe", "FeedBack.exe", lol, weGame));
            Eq(true, LolRuntimeProcesses.IsCleanupTarget(
                weGame + @"\wegame.exe", "wegame.exe", lol, weGame));
            Eq(false, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\LeagueClient\LeagueClient.exe", "LeagueClient.exe", lol, weGame));
            Eq(false, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\Game\League of Legends.exe", "League of Legends.exe", lol, weGame));
            Eq(false, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\ACE\SGuard64.exe", "SGuard64.exe", lol, weGame));
            Eq(false, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\Riot Client\RiotClientServices.exe", "RiotClientServices.exe", lol, weGame));
            Eq(false, LolRuntimeProcesses.IsCleanupTarget(
                weGame + @"\unrelated.exe", "unrelated.exe", lol, weGame));
            string[] declaredRoots =
            {
                lol + @"\Cross",
                lol + @"\LeagueClient\FeedBack",
                lol + @"\LeagueClient\NetworkAssist",
                lol + @"\LeagueClient\TQM",
                lol + @"\LeagueClient\DiagnosticAssistant",
                lol + @"\Launcher\qbblinktrial"
            };
            foreach (string declared in declaredRoots)
            {
                string unlisted = declared + @"\某个从未列入名单的进程.exe";
                if (!LolRuntimeProcesses.IsCleanupTarget(
                        unlisted, "某个从未列入名单的进程.exe", lol, weGame))
                    throw new Exception("已声明的清理根未生效：" + declared);
            }
            Eq(true, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\LeagueClient\DiagnosticAssistant\diagnostic-assistant.exe",
                "diagnostic-assistant.exe", lol, weGame));
            Eq(true, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\Launcher\qbblinktrial\minibrowser.exe", "minibrowser.exe", lol, weGame));
            Eq(true, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\Launcher\qbblinktrial\BugReport.exe", "BugReport.exe", lol, weGame));
            Eq(false, LolRuntimeProcesses.IsCleanupTarget(
                lol + @"\LeagueClient\FeedBackExtra\FeedBack.exe", "FeedBack.exe", lol, weGame));

            Eq(true, LolRuntimeProcesses.IsUnder(lol + @"\Cross\a.exe", lol + @"\Cross"));
            Eq(false, LolRuntimeProcesses.IsUnder(lol + @"Backup\Cross\a.exe", lol));
            Eq(true, LolRuntimeProcesses.IsMainUxProcess(
                lol + @"\LeagueClientUx.exe", "LeagueClientUx.exe", lol));
            Eq(false, LolRuntimeProcesses.IsMainUxProcess(
                lol + @"\LeagueClientUxRender.exe", "LeagueClientUxRender.exe", lol));
            Eq(false, LolRuntimeProcesses.IsMainUxProcess(
                lol + @"Backup\LeagueClientUx.exe", "LeagueClientUx.exe", lol));
            Eq(true, LolOptimizationService.IsExactCleanupStartEvent(
                new ProcessChange
                {
                    Kind = ProcessChangeKind.Started,
                    Name = "wegame",
                    Path = weGame + @"\wegame.exe"
                },
                lol,
                weGame));
            Eq(false, LolOptimizationService.IsExactCleanupStartEvent(
                new ProcessChange
                {
                    Kind = ProcessChangeKind.Started,
                    Name = "browser",
                    Path = @"D:\Unrelated\browser.exe"
                },
                lol,
                weGame));
            Eq(true, LolOptimizationService.IsCredentialSourceStartEvent(
                new ProcessChange
                {
                    Kind = ProcessChangeKind.Started,
                    Name = "LeagueClientUx",
                    Path = lol + @"\LeagueClientUx.exe"
                },
                lol));
            Eq(false, LolOptimizationService.IsCredentialSourceStartEvent(
                new ProcessChange
                {
                    Kind = ProcessChangeKind.Started,
                    Name = "LeagueClientUx",
                    Path = @"D:\Unrelated\LeagueClientUx.exe"
                },
                lol));
            Eq(true, LolOptimizationService.IsCredentialDiscoveryStartEvent(
                new ProcessChange
                {
                    Kind = ProcessChangeKind.Started,
                    Name = "LeagueClientUx",
                    Path = @"D:\Moved\LeagueClientUx.exe"
                }));
            Eq(false, LolOptimizationService.IsCredentialDiscoveryStartEvent(
                new ProcessChange
                {
                    Kind = ProcessChangeKind.Started,
                    Name = "browser",
                    Path = @"D:\Moved\LeagueClientUx.exe"
                }));
            Eq(true, LolOptimizationService.IsCredentialSourceStartEvent(
                new ProcessChange
                {
                    Kind = ProcessChangeKind.Started,
                    Name = "LeagueClient",
                    Path = null
                },
                lol));
            Eq(false, LolOptimizationService.IsCredentialSourceStartEvent(
                new ProcessChange
                {
                    Kind = ProcessChangeKind.Stopped,
                    Name = "LeagueClientUx",
                    Path = lol + @"\LeagueClientUx.exe"
                },
                lol));
            Eq(false, LolOptimizationService.IsExactCleanupStartEvent(
                new ProcessChange
                {
                    Kind = ProcessChangeKind.Stopped,
                    Name = "wegame",
                    Path = weGame + @"\wegame.exe"
                },
                lol,
                weGame));
            Eq(true, LolRuntimeProcesses.IsRelevantProcessChange(
                "wegame", weGame + @"\wegame.exe", lol, weGame));
            Eq(false, LolRuntimeProcesses.IsRelevantProcessChange(
                "wegame", @"D:\Unrelated\wegame.exe", lol, weGame));
            Eq(false, LolRuntimeProcesses.IsRelevantProcessChange(
                "browser", null, lol, weGame));
            Eq(true, LolRuntimeProcesses.IsRelevantProcessChange(
                "leagueclient", null, lol, weGame));

            string root = Path.Combine(Path.GetTempPath(),
                "AegisLolCleanup_" + Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N"));
            Process probe = null;
            Process gameProbe = null;
            try
            {
                Directory.CreateDirectory(root);
                string executable = Path.Combine(root, "wegame.exe");
                File.Copy(Application.ExecutablePath, executable, true);
                probe = Process.Start(new ProcessStartInfo(executable, "--cpu-burn")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (probe == null) throw new Exception("verified cleanup probe did not start");
                Thread.Sleep(250);
                LolCleanupResult cleaned = LolRuntimeProcesses.Clean(null, root);
                if (cleaned.Count != 1) throw new Exception("verified cleanup did not confirm the terminated process");
                if (!probe.WaitForExit(3000)) throw new Exception("verified cleanup probe remained alive");
                probe.Dispose();
                probe = null;
                File.Delete(executable);

                string browser = Path.Combine(root, "browser.exe");
                File.Copy(Application.ExecutablePath, browser, true);
                probe = Process.Start(new ProcessStartInfo(browser, "--cpu-burn")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (probe == null) throw new Exception("stale-root cleanup probe did not start");
                Thread.Sleep(250);
                cleaned = LolRuntimeProcesses.Clean(null, root);
                if (cleaned.Count != 0 || probe.HasExited)
                    throw new Exception("invalid WeGame root was allowed to terminate a process");

                string clientDirectory = Path.Combine(root, "LeagueClient");
                string gameDirectory = Path.Combine(root, "Game");
                Directory.CreateDirectory(clientDirectory);
                Directory.CreateDirectory(gameDirectory);
                File.Copy(Application.ExecutablePath,
                    Path.Combine(clientDirectory, "LeagueClient.exe"), true);
                string gameExecutable = Path.Combine(
                    gameDirectory, "League of Legends.exe");
                File.Copy(Application.ExecutablePath, gameExecutable, true);
                gameProbe = Process.Start(new ProcessStartInfo(
                    gameExecutable, "--cpu-burn")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (gameProbe == null) throw new Exception("game identity probe did not start");
                Thread.Sleep(250);
                int gamePid;
                long gameCreation;
                if (!LolRuntimeProcesses.TryGetGameIdentity(
                    root, gameProbe.Id, out gamePid, out gameCreation))
                    throw new Exception("validated game identity was not found");
                Eq(gameProbe.Id, gamePid);
                if (gameCreation <= 0) throw new Exception("game creation time was not sampled");
                LolProcessSnapshot gameSnapshot = LolRuntimeProcesses.Scan(root, null, true);
                Eq(true, gameSnapshot.GameRunning);
                Eq(gameProbe.Id, gameSnapshot.GameProcessId);
                IntPtr waitHandle;
                if (!LolWatchdog.TryOpenGameWaitHandle(
                    root, gamePid, gameCreation, out waitHandle))
                    throw new Exception("watchdog rejected a validated game identity");
                Native.CloseHandle(waitHandle);
                Eq(false, LolWatchdog.TryOpenGameWaitHandle(
                    root, gamePid, gameCreation + 1, out waitHandle));
                Eq(IntPtr.Zero, waitHandle);
            }
            finally
            {
                StopOwned(gameProbe);
                if (gameProbe != null) gameProbe.Dispose();
                StopOwned(probe);
                if (probe != null) probe.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void TestLolAddonDelete(string testRoot)
        {
            string install = Path.Combine(testRoot, "lol-addons", "英雄联盟");
            string crossPath = Path.Combine(install, "Cross");
            string feedbackPath = Path.Combine(install, "LeagueClient", "FeedBack");
            string gameSentinel = Path.Combine(install, "Game", "game.bin");
            string aceSentinel = Path.Combine(install, "ACE", "ace.bin");
            string launcherSentinel = Path.Combine(install, "Launcher", "launcher.bin");
            string siblingSentinel = Path.Combine(install, "CrossBackup", "outside.bin");
            string outsideSentinel = Path.Combine(testRoot, "lol-addons", "outside", "outside.bin");
            Process probe = null;
            Directory.CreateDirectory(Path.Combine(install, "Game"));
            Directory.CreateDirectory(Path.Combine(install, "ACE"));
            Directory.CreateDirectory(feedbackPath);
            Directory.CreateDirectory(Path.Combine(install, "Launcher"));
            Directory.CreateDirectory(Path.Combine(crossPath, "coach"));
            Directory.CreateDirectory(Path.Combine(crossPath, "empty"));
            Directory.CreateDirectory(Path.Combine(install, "CrossBackup"));
            Directory.CreateDirectory(Path.GetDirectoryName(outsideSentinel));
            File.Copy(Application.ExecutablePath,
                Path.Combine(install, "LeagueClient", "LeagueClient.exe"), true);
            File.Copy(Application.ExecutablePath,
                Path.Combine(install, "Launcher", "Client.exe"), true);
            File.WriteAllText(gameSentinel, "game", Encoding.UTF8);
            File.WriteAllText(aceSentinel, "ace", Encoding.UTF8);
            File.WriteAllText(launcherSentinel, "launcher", Encoding.UTF8);
            File.WriteAllText(siblingSentinel, "sibling", Encoding.UTF8);
            File.WriteAllText(outsideSentinel, "outside", Encoding.UTF8);
            File.WriteAllText(Path.Combine(crossPath, "coach", "coach.bin"), "coach", Encoding.UTF8);
            string readOnly = Path.Combine(crossPath, "readonly.bin");
            File.WriteAllText(readOnly, "readonly", Encoding.UTF8);
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            File.WriteAllText(Path.Combine(feedbackPath, "feedback.bin"), "feedback", Encoding.UTF8);

            try
            {
                LolAddonCleaner.Inspection inspection = LolAddonCleaner.Inspect(install);
                if (inspection.IsBlocked)
                    Skip("League or WeGame is currently running: "
                        + string.Join(", ", inspection.BlockingProcesses.ToArray()));
                if (!inspection.IsValidRoot) throw new Exception("initial root invalid: " + inspection.Error);
                Eq(1, inspection.CandidateCount);
                if (!inspection.CanDelete) throw new Exception("initial delete unavailable: " + inspection.Error);

                string probePath = Path.Combine(crossPath, "CrossProbe.exe");
                File.Copy(Application.ExecutablePath, probePath, true);
                probe = Process.Start(new ProcessStartInfo(probePath, "--cpu-burn")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (probe == null) throw new Exception("Cross blocking probe did not start");
                Thread.Sleep(250);
                LolAddonCleaner.OperationResult blocked = LolAddonCleaner.Delete(install);
                Eq(false, blocked.Success);
                Eq(false, blocked.Changed);
                Eq(true, File.Exists(Path.Combine(crossPath, "coach", "coach.bin")));
                Eq(false, probe.HasExited);
                StopOwned(probe);
                probe.Dispose();
                probe = null;
                File.Delete(probePath);

                LolAddonCleaner.OperationResult deleted = LolAddonCleaner.Delete(install);
                if (!deleted.Success) throw new Exception("delete failed: " + deleted.Message);
                Eq(1, deleted.DeletedCount);
                Eq(false, Directory.Exists(crossPath));
                Eq(true, Directory.Exists(feedbackPath));
                Eq(true, File.Exists(Path.Combine(feedbackPath, "feedback.bin")));
                Eq("game", File.ReadAllText(gameSentinel, Encoding.UTF8));
                Eq("ace", File.ReadAllText(aceSentinel, Encoding.UTF8));
                Eq("launcher", File.ReadAllText(launcherSentinel, Encoding.UTF8));
                Eq("sibling", File.ReadAllText(siblingSentinel, Encoding.UTF8));
                Eq("outside", File.ReadAllText(outsideSentinel, Encoding.UTF8));
                Eq(false, LolAddonCleaner.Inspect(install).CanDelete);
                Eq(false, LolAddonCleaner.Delete(install).Success);
            }
            finally
            {
                StopOwned(probe);
                if (probe != null) probe.Dispose();
                ClearReadOnlyTree(Path.Combine(testRoot, "lol-addons"));
            }
        }

        private static void ClearReadOnlyTree(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(file);
                        if ((attributes & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
