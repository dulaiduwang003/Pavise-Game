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
            Eq(true, LolRuntimeProcesses.IsUnder(lol + @"\Cross\a.exe", lol + @"\Cross"));
            Eq(false, LolRuntimeProcesses.IsUnder(lol + @"Backup\Cross\a.exe", lol));

            string root = Path.Combine(Path.GetTempPath(),
                "AegisLolCleanup_" + Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N"));
            Process probe = null;
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
            }
            finally
            {
                StopOwned(probe);
                if (probe != null) probe.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void TestLolQuarantineRoundTrip(string testRoot)
        {
            string install = Path.Combine(testRoot, "lol-quarantine", "英雄联盟");
            string crossPath = Path.Combine(install, "Cross");
            string feedbackPath = Path.Combine(install, "LeagueClient", "FeedBack");
            string gameSentinel = Path.Combine(install, "Game", "game.bin");
            string aceSentinel = Path.Combine(install, "ACE", "ace.bin");
            string launcherSentinel = Path.Combine(install, "Launcher", "launcher.bin");
            string siblingSentinel = Path.Combine(install, "CrossBackup", "outside.bin");
            string outsideSentinel = Path.Combine(testRoot, "lol-quarantine", "outside", "outside.bin");
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
                LolQuarantineManager.Inspection inspection = LolQuarantineManager.Inspect(install);
                if (inspection.IsBlocked)
                    Skip("League or WeGame is currently running: "
                        + string.Join(", ", inspection.BlockingProcesses.ToArray()));
                if (!inspection.IsValidRoot) throw new Exception("initial root invalid: " + inspection.Error);
                Eq(2, inspection.CandidateCount);
                if (!inspection.CanQuarantine) throw new Exception("initial quarantine unavailable: " + inspection.Error);

                LolQuarantineManager.OperationResult quarantined = LolQuarantineManager.Quarantine(install);
                if (!quarantined.Success) throw new Exception("initial quarantine failed: " + quarantined.Message);
                Eq(2, quarantined.MovedCount);
                Eq(false, Directory.Exists(crossPath));
                Eq(false, Directory.Exists(feedbackPath));
                string initialSetPath = Path.Combine(
                    install, ".aegis-quarantine", quarantined.SetName, "payload");
                Eq(true, File.Exists(Path.Combine(initialSetPath, "Cross", "coach", "coach.bin")));

                Eq("game", File.ReadAllText(gameSentinel, Encoding.UTF8));
                Eq("ace", File.ReadAllText(aceSentinel, Encoding.UTF8));
                Eq("launcher", File.ReadAllText(launcherSentinel, Encoding.UTF8));
                Eq("sibling", File.ReadAllText(siblingSentinel, Encoding.UTF8));
                Eq("outside", File.ReadAllText(outsideSentinel, Encoding.UTF8));

                inspection = LolQuarantineManager.Inspect(install);
                if (!inspection.CanRestore) throw new Exception("initial restore unavailable");

                LolQuarantineManager.OperationResult restored = LolQuarantineManager.Restore(install);
                if (!restored.Success) throw new Exception("initial restore failed: " + restored.Message);
                Eq(true, File.Exists(Path.Combine(crossPath, "coach", "coach.bin")));
                Eq(true, File.Exists(Path.Combine(feedbackPath, "feedback.bin")));
                Eq("readonly", File.ReadAllText(readOnly, Encoding.UTF8));

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
                LolQuarantineManager.OperationResult blocked = LolQuarantineManager.Quarantine(install);
                Eq(false, blocked.Success);
                Eq(false, blocked.Changed);
                Eq(true, File.Exists(Path.Combine(crossPath, "coach", "coach.bin")));
                Eq(false, probe.HasExited);
                StopOwned(probe);
                probe.Dispose();
                probe = null;
                File.Delete(probePath);

                quarantined = LolQuarantineManager.Quarantine(install);
                if (!quarantined.Success) throw new Exception("conflict quarantine failed: " + quarantined.Message);
                Directory.CreateDirectory(feedbackPath);
                File.WriteAllText(Path.Combine(feedbackPath, "new.bin"), "new", Encoding.UTF8);
                restored = LolQuarantineManager.Restore(install);
                Eq(false, restored.Success);
                Eq("new", File.ReadAllText(Path.Combine(feedbackPath, "new.bin"), Encoding.UTF8));
                LolQuarantineManager.Inspection conflicted = LolQuarantineManager.Inspect(install);
                if (!conflicted.CanRestore) throw new Exception("conflict batch was not retained: " + conflicted.Error);
                if (!conflicted.CanDiscard) throw new Exception("conflict batch was not discardable");

                string setPath = Path.GetDirectoryName(conflicted.Active[0].ManifestPath);
                Directory.Delete(Path.Combine(setPath, "payload", "LeagueClient", "FeedBack"), true);
                Directory.Delete(feedbackPath, true);
                LolQuarantineManager.Inspection missing = LolQuarantineManager.Inspect(install);
                Eq(1, missing.Active.Count);
                Eq(1, missing.Active[0].MissingCount);
                Eq(true, string.IsNullOrEmpty(missing.Error));
                if (!missing.CanRestore)
                    throw new Exception("a missing payload item must not veto restoring the intact ones");
                Eq(false, LolQuarantineManager.Restore(install).Success);
                Eq(1, LolQuarantineManager.Inspect(install).Active.Count);

                LolQuarantineManager.Inspection stuck = LolQuarantineManager.Inspect(install);
                if (!stuck.CanDiscard) throw new Exception("stuck batch was not discardable");
                LolQuarantineManager.OperationResult discarded =
                    LolQuarantineManager.Discard(install, stuck.Active[0].Name);
                if (!discarded.Success) throw new Exception("discard failed: " + discarded.Message);
                Eq(true, File.Exists(Path.Combine(crossPath, "coach", "coach.bin")));
                LolQuarantineManager.Inspection afterDiscard = LolQuarantineManager.Inspect(install);
                Eq(0, afterDiscard.Active.Count);
                Eq(true, string.IsNullOrEmpty(afterDiscard.Error));
                if (!afterDiscard.CanQuarantine) throw new Exception("deadlock was not cleared: " + afterDiscard.Error);
            }
            finally
            {
                StopOwned(probe);
                if (probe != null) probe.Dispose();
                ClearReadOnlyTree(Path.Combine(testRoot, "lol-quarantine"));
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
