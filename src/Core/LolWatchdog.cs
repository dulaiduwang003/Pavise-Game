// @author bdth 2074055628@qq.com

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal static class LolWatchdog
    {
        private const int MaxWatchMinutes = 90;

        public static bool TryHandle(string[] args)
        {
            if (args == null || args.Length != 2
                || !string.Equals(args[0], "--lol-watchdog", StringComparison.OrdinalIgnoreCase))
                return false;
            string root;
            try { root = Path.GetFullPath(args[1]).TrimEnd('\\'); }
            catch { return true; }
            if (!LolInstallDiscovery.IsValidLolRoot(root)) return true;
            Run(root);
            return true;
        }

        public static bool StartDetached(string lolRoot)
        {
            if (!LolInstallDiscovery.IsValidLolRoot(lolRoot)) return false;
            try
            {
                string executable = Application.ExecutablePath;
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = executable;
                startInfo.Arguments = "--lol-watchdog " + Quote(lolRoot);
                startInfo.WorkingDirectory = Path.GetDirectoryName(executable);
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                Process process = Process.Start(startInfo);
                if (process == null) return false;
                process.Dispose();
                return true;
            }
            catch { return false; }
        }

        private static void Run(string lolRoot)
        {
            Mutex mutex = null;
            bool created = false;
            try
            {
                try
                {
                    mutex = new Mutex(
                        true, "Global\\Aegis_LolWatchdog_" + StableHash(lolRoot), out created);
                }
                catch
                {
                    mutex = new Mutex(
                        true, "Aegis_LolWatchdog_" + StableHash(lolRoot), out created);
                }
                if (!created) return;
                if (WaitForGameExit(lolRoot)) TryRestore(lolRoot);
            }
            finally
            {
                if (mutex != null)
                {
                    if (created)
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                    try { mutex.Close(); } catch { }
                }
            }
        }

        private static bool WaitForGameExit(string lolRoot)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(MaxWatchMinutes);
            DateTime nextCredentialRefresh = DateTime.MinValue;
            LolLcuCredentials credentials = null;
            int absentSamples = 0;
            while (DateTime.UtcNow < deadline)
            {
                if (!LolRuntimeProcesses.IsClientRunning(lolRoot)) return false;
                bool gameRunning = LolRuntimeProcesses.IsGameRunning(lolRoot);
                if (credentials == null || DateTime.UtcNow >= nextCredentialRefresh)
                {
                    credentials = LolLcuCredentialSource.Find(lolRoot);
                    nextCredentialRefresh = DateTime.UtcNow.AddSeconds(10);
                }
                string phase = credentials == null
                    ? null : LolLcuClient.GetGameflowPhase(credentials);
                bool phaseKnown = !string.IsNullOrEmpty(phase);
                bool inProgress = string.Equals(
                    phase, "InProgress", StringComparison.OrdinalIgnoreCase);
                if (gameRunning || inProgress)
                    absentSamples = 0;
                else if (phaseKnown && ++absentSamples >= 3)
                    return true;
                Thread.Sleep(1000);
            }
            return false;
        }

        private static bool TryRestore(string lolRoot)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(2);
            DateTime nextCredentialRefresh = DateTime.MinValue;
            LolLcuCredentials credentials = null;
            while (DateTime.UtcNow < deadline)
            {
                if (!LolRuntimeProcesses.IsClientRunning(lolRoot)) return false;
                if (credentials == null || DateTime.UtcNow >= nextCredentialRefresh)
                {
                    credentials = LolLcuCredentialSource.Find(lolRoot);
                    nextCredentialRefresh = DateTime.UtcNow.AddSeconds(10);
                }
                if (credentials != null && LolLcuClient.RestoreUx(credentials, lolRoot))
                {
                    Settings.SaveStr("LolHeadlessEngagedRoot", "");
                    return true;
                }
                Thread.Sleep(1500);
            }
            return false;
        }

        private static string StableHash(string value)
        {
            uint hash = 2166136261;
            string normalized = (value ?? "").ToUpperInvariant();
            for (int i = 0; i < normalized.Length; i++)
            {
                hash ^= normalized[i];
                hash *= 16777619;
            }
            return hash.ToString("X8");
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "") + "\"";
        }
    }
}
