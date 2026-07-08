// @author bdth 2074055628@qq.com
// 文件用途 按用户配置压制指定反作弊进程

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace AegisApp
{
    internal class Tamer
    {
        private readonly object sync = new object();
        private readonly object engineSync = new object();
        private readonly Dictionary<string, bool> enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly AutoResetEvent kick = new AutoResetEvent(true);
        private readonly SuppressionCore core;
        private readonly int selfPid;
        private readonly int selfSession;
        private volatile bool paused;
        private volatile bool stopping;
        private long panicUntilUtcTicks;
        private Thread worker;

        public Tamer(SuppressionCore core)
        {
            this.core = core;
            using (Process self = Process.GetCurrentProcess())
            {
                selfPid = self.Id;
                try { selfSession = self.SessionId; } catch { selfSession = -1; }
            }
            foreach (AcGroup g in AntiCheatCatalog.Groups)
                enabled[g.Key] = Settings.Load("Tame_" + g.Key, g.Default);
        }

        public bool Paused
        {
            get { return paused; }
            set { paused = value; kick.Set(); }
        }

        public bool IsGroupEnabled(string key)
        {
            lock (sync) { bool v; return enabled.TryGetValue(key, out v) && v; }
        }

        public bool PanicRestore()
        {
            Interlocked.Exchange(ref panicUntilUtcTicks, DateTime.UtcNow.AddSeconds(4).Ticks);
            kick.Set();
            lock (engineSync) return ReleaseAll("紧急恢复");
        }

        public void SetGroupEnabled(string key, bool on)
        {
            lock (sync) enabled[key] = on;
            Settings.Save("Tame_" + key, on);
            kick.Set();
            Logger.Log("反作弊分组 " + key + " → " + (on ? "开启压制" : "关闭并恢复"));
        }

        public string GroupStatus(string key)
        {
            if (paused) return Lang.T("gs.moff");
            bool on;
            lock (sync) { if (!(enabled.TryGetValue(key, out on) && on)) return Lang.T("gs.noff"); }
            int t, f; core.AntiCheatGroupCounts(key, out t, out f);
            if (t == 0 && f == 0) return Lang.T("gs.noproc");
            string s = Lang.F("gs.thr", t);
            if (f > 0) s += Lang.F("gs.prot", f);
            return s;
        }

        public int GroupState(string key)
        {
            if (paused) return 0;
            bool on;
            lock (sync) { if (!(enabled.TryGetValue(key, out on) && on)) return 0; }
            int t, f; core.AntiCheatGroupCounts(key, out t, out f);
            return t > 0 ? 1 : 2;
        }

        public void Start()
        {
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();
        }

        public void Stop()
        {
            stopping = true;
            kick.Set();
            if (worker != null) worker.Join(6000);
        }

        public void Poke() { kick.Set(); }

        private void Loop()
        {
            Logger.Log("反作弊压制引擎启动，可压制掩码 0x" + core.ThrottleMask.ToString("X"));
            while (!stopping)
            {
                try
                {
                    lock (engineSync)
                    {
                        bool panicHold = DateTime.UtcNow.Ticks < Interlocked.Read(ref panicUntilUtcTicks);
                        if (paused || panicHold) ReleaseAll(paused ? "总开关关闭" : "紧急恢复冷却");
                        else Sweep();
                        core.RetryPending();
                    }
                }
                catch (Exception ex) { Logger.Log("反作弊压制异常: " + ex.Message); }
                kick.WaitOne(8000);
            }
            ReleaseAll("Aegis 退出");
        }

        private Dictionary<string, string> BuildActive()
        {
            var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (sync)
            {
                foreach (AcGroup g in AntiCheatCatalog.Groups)
                {
                    bool on;
                    if (enabled.TryGetValue(g.Key, out on) && on)
                        foreach (string p in g.Procs) active[p] = g.Key;
                }
            }
            return active;
        }

        private void Sweep()
        {
            var active = BuildActive();
            var seen = new HashSet<int>();
            if (active.Count > 0)
            {
                Process[] all;
                try { all = Process.GetProcesses(); } catch { all = new Process[0]; }
                foreach (Process p in all)
                {
                    try
                    {
                        string grp, nm = p.ProcessName;
                        if (!active.TryGetValue(nm, out grp)) continue;
                        int pid = p.Id;
                        if (pid == selfPid) continue;
                        int session;
                        try { session = p.SessionId; } catch { continue; }
                        if (selfSession < 0 || session != selfSession && session != 0) continue;
                        seen.Add(pid);
                        AcquireResult r = core.Acquire(pid, nm, SuppressReason.AntiCheat, grp);
                        if (r == AcquireResult.NewlyThrottled) Logger.Log("压制 " + nm + " (pid " + pid + ")");
                        else if (r == AcquireResult.NewlyProtected) Logger.Log("打开 " + nm + " (pid " + pid + ") 失败（句柄被内核保护，压不动）");
                        else if (r == AcquireResult.ApplyFailed) Logger.Log("压制 " + nm + " (pid " + pid + ") 未完全生效，下一轮继续重试");
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }

            foreach (int pid in core.PidsWith(SuppressReason.AntiCheat))
                if (!seen.Contains(pid)) core.Release(pid, SuppressReason.AntiCheat);
        }

        private bool ReleaseAll(string reason)
        {
            int n = core.ReleaseReason(SuppressReason.AntiCheat);
            if (n > 0) Logger.Log("反作弊压制解除（" + reason + "）：恢复 " + n + " 个进程");
            return !core.AnyWith(SuppressReason.AntiCheat);
        }
    }
}
