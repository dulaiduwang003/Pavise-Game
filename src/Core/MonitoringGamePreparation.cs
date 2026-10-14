using System;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    // 同一启动提示不续期，失败启动不能让 RTSS 常驻。
    internal sealed class MonitoringGamePreparation
    {
        internal const int LifetimeMs = 60000;
        private readonly Func<long> clock;
        private readonly Func<int, long, string, bool> alive;
        private int pid;
        private long creation, expires;
        private string path, profileId;
        private bool pending;

        internal MonitoringGamePreparation() : this(
            delegate { return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency); }, IsAlive) { }
        internal MonitoringGamePreparation(Func<long> clock, Func<int, long, string, bool> alive)
        { this.clock = clock; this.alive = alive; }

        internal static bool Eligible(GameProfile profile, string imagePath)
        {
            if (!MatchesExecutable(profile, imagePath)) return false;
            // 共享运行时和启动器仍需正常渲染进程确认。
            string bare = Path.GetFileNameWithoutExtension(imagePath);
            if (String.IsNullOrEmpty(bare)) return false;
            switch (bare.ToLowerInvariant())
            {
                case "steam": case "steamwebhelper": case "wegame": case "wegameapp":
                case "tgp": case "tgp_daemon": case "riotclientservices":
                case "riotclientux": case "riotclientuxrender": case "leagueclient":
                case "leagueclientux": case "leagueclientuxrender": case "epicgameslauncher":
                case "eadesktop": case "origin": case "battle.net": case "ubisoftconnect":
                case "upc": return false;
            }
            return GamePerformanceTargetPolicy.AllowsGameTarget(imagePath);
        }
        private static bool MatchesExecutable(GameProfile profile, string path)
        {
            return profile != null && !String.IsNullOrEmpty(path)
                && (WhitelistRule.PathEquals(profile.ExecutablePath, path)
                    || WhitelistRule.PathEquals(profile.LearnedExecutablePath, path));
        }

        internal bool Observe(GameProfile profile, ProcEntry entry, int session)
        {
            if (entry == null || entry.Pid <= 4 || entry.Creation <= 0 || entry.Session != session) return false;
            if (entry.Creation <= creation) return false;
            if (!Eligible(profile, entry.Path)) return false;
            pid = entry.Pid; creation = entry.Creation; path = entry.Path; profileId = profile.Id;
            expires = clock() + LifetimeMs; pending = true;
            return true;
        }
        internal bool IsPending(Func<string, GameProfile> findProfile)
        {
            if (!pending) return false;
            GameProfile profile = findProfile(profileId);
            if (clock() >= expires || profile == null
                || !MatchesExecutable(profile, path)
                || !alive(pid, creation, path)) { pending = false; return false; }
            return true;
        }
        internal void Cancel() { pending = false; }
        internal void Exited(int exitedPid, long exitedCreation)
        { if (pid == exitedPid && (exitedCreation == 0 || creation == exitedCreation)) Cancel(); }

        private static bool IsAlive(int pid, long creation, string path)
        {
            IntPtr process = Native.OpenProcess(0x100000 | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                return CpuCapNative.IsAlive(process) && Native.QueryProcessIdentityState(process, creation) == 0
                    && WhitelistRule.PathEquals(Native.ImagePath(process), path);
            }
            finally { Native.CloseHandle(process); }
        }
    }
}
