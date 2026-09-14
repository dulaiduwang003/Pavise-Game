// @author bdth 2074055628@qq.com
// File purpose Session hookup for the suspected malicious process watch, fed from main loop snapshots, logs and raises a tray balloon on a verdict, same name reported once per day
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal partial class GameMode
    {
        public event Action<string> RogueProcessSuspected;

        private readonly RogueProcessWatch rogueWatch = new RogueProcessWatch();
        private volatile HashSet<int> rogueTrustedPids;
        private HashSet<int> rogueVisiblePids;
        private long rogueVisibleStamp;
        private long rogueLastStep;

        internal const string RogueLedgerKey = "RogueAlertLedgerV1";
        private const long RogueRepeatTicks = TimeSpan.TicksPerDay;
        private const long RogueVisibleRefreshTicks = TimeSpan.TicksPerSecond * 15;

        // Processes under the system directory, Program Files and the Defender directory skip the 'saturating cores' rule, AV disk scans and update installs peg cores for long stretches
        //   session 0 processes have no path in the snapshot, look it up again by handle at verdict time, and let it go if it lands in these directories too
        private static readonly string[] RogueTrustedRoots = BuildRogueTrustedRoots();

        // Common legitimate heavy background work, game platform shader precompilation and updates, system indexing and maintenance, a name match means no report
        private static readonly HashSet<string> RogueTrustedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Registry", "Memory Compression", "svchost", "dllhost", "csrss", "dwm", "audiodg",
            "MsMpEng", "MpDefenderCoreService", "NisSrv", "SearchIndexer", "TiWorker", "TrustedInstaller",
            "MoUsoCoreWorker", "CompatTelRunner", "WmiPrvSE", "steam", "steamwebhelper", "steamservice",
            "fossilize", "EpicGamesLauncher", "EpicWebHelper", "wegame", "WeGame", "Battle.net", "Agent",
            "GalaxyClient", "UbisoftConnect", "upc", "EADesktop", "Origin", "RiotClientServices",
            "nvcontainer", "NVDisplay.Container", "Pavise"
        };

        private static string[] BuildRogueTrustedRoots()
        {
            var roots = new List<string>();
            string[] candidates =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Microsoft\Windows Defender")
            };
            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                string root = c.TrimEnd('\\') + "\\";
                bool dup = false;
                foreach (string r in roots) if (string.Equals(r, root, StringComparison.OrdinalIgnoreCase)) dup = true;
                if (!dup) roots.Add(root);
            }
            return roots.ToArray();
        }

        internal static bool RogueTrustedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (string root in RogueTrustedRoots)
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string RogueImagePath(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                return h == IntPtr.Zero ? null : Native.ImagePath(h);
            }
            catch { return null; }
            finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
        }

        // Fed once per main loop round, the snapshot is already there, only adds one read of the background suppression list and the visible window set
        private void StepRogueWatch(ProcessSnapshot all, HashSet<int> gamePids)
        {
            if (all == null) return;
            long now = DateTime.UtcNow.Ticks;
            if (now - rogueLastStep < RogueProcessWatch.MinIntervalTicks) return;
            rogueLastStep = now;
            var confined = new HashSet<int>(core.PidsWith(SuppressReason.Background));
            if (rogueVisiblePids == null || now - rogueVisibleStamp >= RogueVisibleRefreshTicks)
            {
                rogueVisiblePids = CollectVisibleWindowPids();
                rogueVisibleStamp = now;
            }
            HashSet<int> visible = rogueVisiblePids;
            HashSet<int> trusted = rogueTrustedPids;
            var samples = new List<RogueProcessWatch.Sample>(all.Count);
            foreach (ProcEntry e in all.Entries)
            {
                if (e.Pid <= 4 || e.Pid == selfPid) continue;
                if (gamePids != null && gamePids.Contains(e.Pid)) continue;
                samples.Add(new RogueProcessWatch.Sample
                {
                    Pid = e.Pid, Creation = e.Creation, Cpu = e.Cpu, Name = e.Name, Path = e.Path,
                    Confined = confined.Contains(e.Pid),
                    // If window enumeration fails treat everything as visible, better to miss a report than flag a program in use as a virus
                    Visible = visible == null || visible.Contains(e.Pid),
                    Trusted = (trusted != null && trusted.Contains(e.Pid))
                        || RogueTrustedPath(e.Path) || RogueTrustedNames.Contains(e.Name ?? "")
                });
            }
            int confinedCpus = CpuPartitionPolicy.BitCount(CpuTopology.BackgroundAllowedMask());
            List<RogueProcessWatch.Verdict> verdicts =
                rogueWatch.Step(samples, now, Environment.ProcessorCount, confinedCpus);
            foreach (RogueProcessWatch.Verdict v in verdicts) AnnounceRogue(v, now);
        }

        private void AnnounceRogue(RogueProcessWatch.Verdict v, long now)
        {
            string path = v.Path ?? RogueImagePath(v.Pid);
            // Processes that lifted their own core restriction ignore the directory check, the rest landing in trusted directories are treated as false positives
            if (!v.Escaped && RogueTrustedPath(path)) return;
            string ledger = Settings.LoadStr(RogueLedgerKey, "");
            if (RogueProcessWatch.LedgerRecent(ledger, v.Name, now, RogueRepeatTicks)) return;
            Settings.SaveStr(RogueLedgerKey, RogueProcessWatch.LedgerAppend(ledger, v.Name, now, RogueRepeatTicks));
            string cores = v.Cores.ToString("0.0", CultureInfo.InvariantCulture);
            Logger.Error(Lang.F("log.rogue.1", v.Name, path ?? "?", cores)
                + (v.Escaped ? Lang.T("log.rogue.2") : ""));
            var h = RogueProcessSuspected;
            if (h != null) { try { h(Lang.F("bal.rogue", v.Name, cores)); } catch { } }
        }
    }
}
