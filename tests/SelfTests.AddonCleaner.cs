// @author bdth 2074055628@qq.com
// 文件用途 英雄联盟附加层清理的删除边界自测

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
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
