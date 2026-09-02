// @author bdth 2074055628@qq.com
// 文件用途 疑似恶意进程监视的会话接入 喂主循环快照 出结论后记日志并抛托盘气泡 同名一天只报一次
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

        // 系统目录 Program Files 和 Defender 目录下的进程不走"吃满核心"这条 杀软扫盘 更新安装都会长时间满核
        //   会话 0 的进程快照里没有路径 出结论时再按句柄查一次 查到落在这些目录同样放过
        private static readonly string[] RogueTrustedRoots = BuildRogueTrustedRoots();

        // 常见的合法重负载后台 游戏平台的着色器预编译和更新 系统的索引与维护 名字对上就不报
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

        // 主循环每轮喂一次 快照是现成的 只多一次后台压制名单和可见窗口集合的读取
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
                    // 窗口枚举失败按全部可见处理 宁可漏报也不把在用的程序当病毒
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
            // 自行解除核心限制的不看目录 其余落在受信目录的按误报处理
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
