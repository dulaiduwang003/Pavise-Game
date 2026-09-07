// @author bdth 2074055628@qq.com
// 文件用途 通用 WeGame 脱壳服务 对局确认后结束壳进程 游戏随之退出即熔断 壳反复重生即本局停手
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed class WeGameShellSnapshot
    {
        public bool AutoEnabled;
        public bool Fused;
        public bool WeGameFound;
        public bool WeGameRunning;
        public int ShellProcessCount;
        public bool SessionActive;
        public bool CleanedThisSession;
        public int CleanedCount;
        public bool CircuitOpen;
        public int SecondsUntilClean = -1;
        public string LastAction = "";
        public string LastError = "";
    }

    internal sealed class WeGameShellService : IDisposable
    {
        private const string WeGameRootKey = "WgWeGamePath";
        private const string AutoKeyPrefix = "WgShellAuto_";
        private const string FuseKeyPrefix = "WgShellFuse_";
        private const int SnapshotRefreshSeconds = 5;
        private const int WeGameStartWaitMs = 6000;

        private readonly object sync = new object();
        private readonly object actionLock = new object();
        private readonly Timer timer;
        private bool disposed;
        private string weGameRoot;

        // 当前对局 由 GameMode 的会话通知喂进来 不自己扫游戏进程
        private string sessionProfileId;
        private string sessionGameRoot;
        private int sessionPid;
        private long sessionCreation;
        private long sessionStartTicks;
        private long cleanedTicks;
        private int cleanedCount;
        private bool circuitOpen;
        private bool emptyCleanLogged;
        private readonly Queue<long> killCycles = new Queue<long>();
        private int timerGeneration;

        // 卡片用的轻量状态 只按需刷新
        private bool weGameRunning;
        private int shellProcessCount;
        private long shellScanTicks;
        private int shellScanBusy;
        private string lastAction = "";
        private string lastError = "";

        public event Action Changed;

        public WeGameShellService()
        {
            weGameRoot = Settings.LoadStr(WeGameRootKey, null);
            timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        // ---- 逐游戏设置 ----

        public bool IsAutoEnabled(string profileId)
        {
            return !string.IsNullOrEmpty(profileId) && Settings.LoadCached(AutoKeyPrefix + profileId, false);
        }

        public bool IsFused(string profileId)
        {
            return !string.IsNullOrEmpty(profileId) && Settings.LoadCached(FuseKeyPrefix + profileId, false);
        }

        // 打开开关即视为用户重新授权 熔断一并清掉
        public void SetAutoEnabled(string profileId, bool on)
        {
            if (string.IsNullOrEmpty(profileId)) return;
            Settings.Save(AutoKeyPrefix + profileId, on);
            if (on && IsFused(profileId)) Settings.Save(FuseKeyPrefix + profileId, false);
            lock (sync)
            {
                if (string.Equals(sessionProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                {
                    if (on) circuitOpen = false;
                    ScheduleLocked();
                }
            }
            RaiseChanged();
        }

        // ---- 对局通知 ----

        public void NotifySession(GameProfile profile, int rendererPid, long rendererCreation, bool active)
        {
            string root = null;
            bool applies = active && WeGameGameExtension.AppliesToProfile(profile, out root);
            bool changed = false;
            string fusedId = null;
            lock (sync)
            {
                if (disposed) return;
                if (!applies)
                {
                    if (sessionProfileId != null)
                    {
                        fusedId = EndSessionLocked(true);
                        changed = true;
                    }
                }
                else
                {
                    string id = profile.Id;
                    bool sameProfile = string.Equals(sessionProfileId, id, StringComparison.OrdinalIgnoreCase);
                    if (sameProfile && sessionPid == rendererPid && sessionCreation == rendererCreation) return;
                    if (!sameProfile) fusedId = EndSessionLocked(true);
                    sessionProfileId = id;
                    sessionGameRoot = root;
                    sessionPid = rendererPid;
                    sessionCreation = rendererCreation;
                    // 启动器交接成真实渲染进程也从这一刻重新数稳定期
                    sessionStartTicks = DateTime.UtcNow.Ticks;
                    if (!sameProfile)
                    {
                        cleanedTicks = 0;
                        cleanedCount = 0;
                        circuitOpen = false;
                        emptyCleanLogged = false;
                        killCycles.Clear();
                    }
                    ScheduleLocked();
                    changed = true;
                }
            }
            if (fusedId != null) LogFuse(fusedId);
            if (changed) RaiseChanged();
        }

        // 收掉当前对局 若游戏是在脱壳后不久退出的 返回要熔断的档案 id
        private string EndSessionLocked(bool fromNotification)
        {
            string fused = null;
            if (sessionProfileId != null && cleanedTicks > 0
                && WeGameShell.ExitBlamesCleanup(cleanedTicks, DateTime.UtcNow.Ticks, fromNotification)
                && !IsFused(sessionProfileId))
            {
                Settings.Save(FuseKeyPrefix + sessionProfileId, true);
                fused = sessionProfileId;
            }
            sessionProfileId = null;
            sessionGameRoot = null;
            sessionPid = 0;
            sessionCreation = 0;
            sessionStartTicks = 0;
            cleanedTicks = 0;
            circuitOpen = false;
            emptyCleanLogged = false;
            killCycles.Clear();
            timerGeneration++;
            try { timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            return fused;
        }

        private void LogFuse(string profileId)
        {
            Logger.Warn(Lang.F("wg.act.fused", WeGameShell.ExitFuseSeconds.ToString()));
            lock (sync) lastAction = Lang.T("wg.state.fused");
        }

        private void ScheduleLocked()
        {
            timerGeneration++;
            if (sessionProfileId == null || !IsAutoEnabled(sessionProfileId)
                || IsFused(sessionProfileId) || circuitOpen)
            {
                try { timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                return;
            }
            long now = DateTime.UtcNow.Ticks;
            int delayMs;
            if (cleanedTicks == 0)
            {
                long elapsedMs = (now - sessionStartTicks) / TimeSpan.TicksPerMillisecond;
                delayMs = (int)Math.Max(250, WeGameShell.StabilizeSeconds * 1000L - elapsedMs);
            }
            else
            {
                long sinceCleanMs = (now - cleanedTicks) / TimeSpan.TicksPerMillisecond;
                long fuseMs = WeGameShell.ExitFuseSeconds * 1000L;
                delayMs = sinceCleanMs < fuseMs
                    ? (int)Math.Max(250, fuseMs - sinceCleanMs + 200)
                    : WeGameShell.NextCheckDelayMs(false);
            }
            try { timer.Change(delayMs, Timeout.Infinite); } catch { }
        }

        private void OnTimer(object state)
        {
            int generation;
            string profileId;
            string root = null;
            int pid;
            long creation;
            long startTicks;
            long cleanedAt;
            bool circuit;
            lock (sync)
            {
                if (disposed || sessionProfileId == null) return;
                generation = timerGeneration;
                profileId = sessionProfileId;
                root = sessionGameRoot;
                pid = sessionPid;
                creation = sessionCreation;
                startTicks = sessionStartTicks;
                cleanedAt = cleanedTicks;
                circuit = circuitOpen;
            }

            bool alive = RendererAlive(pid, creation);
            long now = DateTime.UtcNow.Ticks;
            if (!alive)
            {
                // 对局结束通知还没到 游戏已经不在了 脱壳后不久就没了的算壳被依赖
                string fusedId = null;
                lock (sync)
                {
                    if (timerGeneration != generation) return;
                    fusedId = EndSessionLocked(false);
                }
                if (fusedId != null) LogFuse(fusedId);
                RaiseChanged();
                return;
            }

            bool auto = IsAutoEnabled(profileId);
            bool fused = IsFused(profileId);
            bool firstDue = WeGameShell.ShouldClean(startTicks, now, cleanedAt > 0, fused, circuit, auto);
            bool respawnDue = cleanedAt > 0 && auto && !fused && !circuit
                && now - cleanedAt >= WeGameShell.ExitFuseSeconds * TimeSpan.TicksPerSecond;
            if (!firstDue && !respawnDue)
            {
                lock (sync) { if (timerGeneration == generation) ScheduleLocked(); }
                return;
            }

            LolCleanupResult result;
            lock (actionLock)
            {
                string weGame = EnsureWeGameRoot();
                result = LolRuntimeProcesses.CleanShell(root, weGame, false);
            }
            bool tripped = false;
            bool logEmpty = false;
            lock (sync)
            {
                if (timerGeneration != generation) return;
                if (result.Count > 0)
                {
                    cleanedTicks = DateTime.UtcNow.Ticks;
                    cleanedCount += result.Count;
                    tripped = WeGameShell.RegisterKillCycle(killCycles, cleanedTicks);
                    if (tripped) circuitOpen = true;
                    lastAction = tripped ? Lang.T("wg.state.circuit") : Lang.F("wg.state.cleaned", cleanedCount.ToString());
                    lastError = "";
                }
                else if (cleanedTicks == 0 && !emptyCleanLogged)
                {
                    emptyCleanLogged = true;
                    logEmpty = true;
                    lastAction = Lang.T("wg.act.none");
                }
                shellScanTicks = 0;
                ScheduleLocked();
            }
            if (result.Count > 0)
                Logger.Log(tripped
                    ? Lang.T("wg.act.circuit")
                    : Lang.F("wg.act.cleaned", result.Count.ToString()));
            else if (logEmpty) Logger.Log(Lang.T("wg.act.none"));
            RaiseChanged();
        }

        private static bool RendererAlive(int pid, long creation)
        {
            if (pid <= 0) return false;
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                if (!Native.StillActive(handle)) return false;
                if (creation <= 0) return true;
                long actual, cpu;
                ulong io;
                return Native.QueryProcessSample(handle, out actual, out cpu, out io) && actual == creation;
            }
            catch { return false; }
            finally { Native.CloseHandle(handle); }
        }

        // ---- 手动动作 ----

        public bool LaunchWeGame()
        {
            lock (actionLock)
            {
                string weGame = EnsureWeGameRoot();
                string executable = LolInstallDiscovery.FindWeGameExecutable(weGame);
                if (executable == null) { SetError(Lang.T("lol.err.nowegame")); return false; }
                if (LolRuntimeProcesses.IsWeGameRunning(weGame))
                {
                    SetAction(Lang.T("lol.act.wegamerunning"));
                    return true;
                }
                if (!UserLaunch.Start(executable)) { SetError(Lang.T("lol.err.wegamestart")); return false; }
                SetAction(Lang.T("lol.act.wegamestarting"));
                Logger.Log(Lang.T("wg.act.launch"));
                int waited = 0;
                while (waited < WeGameStartWaitMs)
                {
                    if (LolRuntimeProcesses.IsWeGameRunning(weGame)) break;
                    Thread.Sleep(250);
                    waited += 250;
                }
                bool alive = LolRuntimeProcesses.IsWeGameRunning(weGame);
                if (!alive) SetError(Lang.T("lol.err.wegamenostart"));
                else SetAction(Lang.T("lol.act.wegamestarted"));
                lock (sync) shellScanTicks = 0;
                RaiseChanged();
                return alive;
            }
        }

        // 立即净化 不管有没有对局 连下载器一起收 对局中收的也计入熔断判断
        public bool CleanNow(GameProfile profile)
        {
            string root = null;
            WeGameGameExtension.AppliesToProfile(profile, out root);
            LolCleanupResult result;
            lock (actionLock)
            {
                string weGame = EnsureWeGameRoot();
                result = LolRuntimeProcesses.CleanShell(root, weGame, true);
            }
            bool tripped = false;
            lock (sync)
            {
                if (result.Count > 0 && profile != null
                    && string.Equals(sessionProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    cleanedTicks = DateTime.UtcNow.Ticks;
                    cleanedCount += result.Count;
                    tripped = WeGameShell.RegisterKillCycle(killCycles, cleanedTicks);
                    if (tripped) circuitOpen = true;
                    ScheduleLocked();
                }
                lastAction = result.Count > 0
                    ? (tripped ? Lang.T("wg.state.circuit") : Lang.F("wg.state.cleaned", result.Count.ToString()))
                    : Lang.T("wg.act.none");
                lastError = "";
                shellScanTicks = 0;
            }
            Logger.Log(result.Count > 0 ? Lang.F("wg.act.cleaned", result.Count.ToString()) : Lang.T("wg.act.none"));
            RaiseChanged();
            return true;
        }

        private string EnsureWeGameRoot()
        {
            string current;
            lock (sync) current = weGameRoot;
            if (LolInstallDiscovery.IsValidWeGameRoot(current)) return current;
            string found = null;
            try { found = LolInstallDiscovery.FindWeGameRoot(current, null, false, null); }
            catch { found = null; }
            lock (sync) weGameRoot = found;
            if (found != null) Settings.SaveStr(WeGameRootKey, found);
            return found;
        }

        private void SetAction(string text)
        {
            lock (sync) { lastAction = text ?? ""; lastError = ""; }
        }

        private void SetError(string text)
        {
            lock (sync) lastError = text ?? "";
        }

        // ---- 卡片 ----

        public void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            if (batch == null) return;
            bool touched = false;
            try
            {
                foreach (ProcessChange change in batch.Changes)
                    if (change != null && LolRuntimeProcesses.IsWeGameDiscoveryCandidateName(change.Name)) { touched = true; break; }
            }
            catch { }
            if (!touched) return;
            lock (sync) shellScanTicks = 0;
        }

        public WeGameShellSnapshot GetSnapshot(GameProfile profile)
        {
            string root = null;
            WeGameGameExtension.AppliesToProfile(profile, out root);
            var s = new WeGameShellSnapshot();
            string id = profile != null ? profile.Id : null;
            s.AutoEnabled = IsAutoEnabled(id);
            s.Fused = IsFused(id);
            long now = DateTime.UtcNow.Ticks;
            bool needScan;
            string weGame;
            lock (sync)
            {
                weGame = weGameRoot;
                s.WeGameFound = LolInstallDiscovery.IsValidWeGameRoot(weGame);
                s.WeGameRunning = weGameRunning;
                s.ShellProcessCount = shellProcessCount;
                s.SessionActive = id != null && string.Equals(sessionProfileId, id, StringComparison.OrdinalIgnoreCase);
                s.CleanedThisSession = s.SessionActive && cleanedTicks > 0;
                s.CleanedCount = s.SessionActive ? cleanedCount : 0;
                s.CircuitOpen = s.SessionActive && circuitOpen;
                if (s.SessionActive && s.AutoEnabled && !s.Fused && !circuitOpen && cleanedTicks == 0)
                {
                    long remaining = WeGameShell.StabilizeSeconds - (now - sessionStartTicks) / TimeSpan.TicksPerSecond;
                    s.SecondsUntilClean = (int)Math.Max(0, remaining);
                }
                s.LastAction = lastAction;
                s.LastError = lastError;
                needScan = now - shellScanTicks > SnapshotRefreshSeconds * TimeSpan.TicksPerSecond;
            }
            if (needScan) RefreshShellState(root);
            return s;
        }

        private void RefreshShellState(string gameRoot)
        {
            if (Interlocked.CompareExchange(ref shellScanBusy, 1, 0) != 0) return;
            bool queued = false;
            try
            {
                queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        string weGame = EnsureWeGameRoot();
                        LolProcessSnapshot scan = LolRuntimeProcesses.ScanShell(gameRoot, weGame, true);
                        lock (sync)
                        {
                            weGameRunning = scan.WeGameMainRunning;
                            shellProcessCount = scan.WeGameProcessCount + scan.CrossProcessCount;
                            shellScanTicks = DateTime.UtcNow.Ticks;
                        }
                        RaiseChanged();
                    }
                    catch { }
                    finally { Interlocked.Exchange(ref shellScanBusy, 0); }
                });
            }
            catch { }
            finally { if (!queued) Interlocked.Exchange(ref shellScanBusy, 0); }
        }

        private void RaiseChanged()
        {
            Action handler = Changed;
            if (handler == null) return;
            try { handler(); } catch { }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                timerGeneration++;
            }
            try { timer.Dispose(); } catch { }
        }
    }
}
