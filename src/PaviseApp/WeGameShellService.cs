using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class WeGameShellSnapshot
    {
        internal string ProfileId;
        internal int RendererPid;
        internal long RendererCreation;
        internal int Generation;
        internal bool Busy;
        internal bool Detached;
        internal string Status;
        internal string Error;
    }

#if PAVISE_SELFTEST
    internal sealed class WeGameShellCapture
    {
        internal bool Complete;
        internal List<GameProcessSnapshot> Entries;
    }
#endif

    internal sealed class WeGameShellService : IDisposable
    {
        private sealed class AttemptReceipt
        {
            internal bool Finished;
            internal bool Detached;
            internal string Status;
            internal string Error;
        }

        private const int MaximumAttemptReceipts = 128;
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Dictionary<string, AttemptReceipt> attemptReceipts = new Dictionary<string, AttemptReceipt>(StringComparer.Ordinal);
        private readonly int ownerSession;
        private Thread worker;
        private bool running;
        private bool disposed;
        private int workerEpoch;
        private int generation;
        private GameProfile profile;
        private int rendererPid;
        private long rendererCreation;
        private string rendererPath;
        private string weGameRoot;
        private bool discoveryAttempted;
        private long nextDiscoveryAt;
        private bool attempted;
        private bool cleaning;
        private bool detached;
        private AttemptReceipt activeReceipt;
        private string status = "";
        private string error = "";
        internal event Action Changed;

#if PAVISE_SELFTEST
        private readonly bool testOnly;
        internal Func<GameProfile, int, long, string, GameProcessSnapshot> ProbeRendererForTest;
        internal Func<GameProfile, string> FindRootForTest;
        internal Func<string, WeGameShellCapture> CaptureCandidatesForTest;
        internal Func<GameProcessSnapshot, string, bool> TerminateForTest;
        internal Func<long> ClockForTest;

        internal WeGameShellService(bool useTestSeams)
        {
            testOnly = useTestSeams;
            if (testOnly) { ownerSession = 1; return; }
            try { using (Process self = Process.GetCurrentProcess()) ownerSession = self.SessionId; }
            catch { ownerSession = -1; }
        }

        internal void RunCycleForTest()
        {
            if (!testOnly) throw new InvalidOperationException("A test-only service is required");
            int epoch;
            lock (gate) { epoch = workerEpoch; }
            RunCycle(epoch);
        }
#endif

        internal WeGameShellService()
        {
            try { using (Process self = Process.GetCurrentProcess()) ownerSession = self.SessionId; }
            catch { ownerSession = -1; }
        }

        internal void Start()
        {
            lock (gate)
            {
                if (disposed || running) return;
                running = true;
#if PAVISE_SELFTEST
                if (testOnly) return;
#endif
                int epoch = ++workerEpoch;
                worker = new Thread(delegate() { Run(epoch); }) { IsBackground = true, Name = "Pavise WeGame shell" };
                worker.Start();
            }
        }

        internal void Stop()
        {
            Thread old;
            lock (gate)
            {
                running = false; workerEpoch++; generation++; ResetSessionLocked(); old = worker;
                wake.Set();
            }
            if (old != null && old != Thread.CurrentThread) old.Join(2500);
            RaiseChanged();
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Stop();
            lock (gate) { attemptReceipts.Clear(); }
            // A slow read-only query may outlive Stop; its event carries no cleanup authority.
        }

        internal void NotifySession(GameProfile value, int pid, long creation, bool active)
        {
            lock (gate)
            {
                if (disposed) return;
                if (!active || value == null || string.IsNullOrEmpty(value.Id) || pid <= 0 || creation <= 0)
                {
                    if (profile == null) return;
                    generation++; ResetSessionLocked();
                }
                else if (profile == null || !SameProfile(profile, value) || rendererPid != pid || rendererCreation != creation)
                {
                    generation++; ResetSessionLocked(); profile = value.Clone(); rendererPid = pid; rendererCreation = creation;
                    status = Text("已检测到已观测的渲染进程，准备退出 WeGame 界面", "Observed renderer detected; preparing to close the WeGame interface");
                    AttemptReceipt receipt;
                    if (attemptReceipts.TryGetValue(AttemptKey(profile, pid, creation), out receipt)) RestoreReceiptLocked(receipt);
                }
                wake.Set();
            }
            RaiseChanged();
        }

        internal void CancelProfile(GameProfile value)
        {
            lock (gate)
            {
                if (profile == null || !SameProfile(profile, value)) return;
                generation++; ResetSessionLocked(); wake.Set();
            }
            RaiseChanged();
        }

        internal WeGameShellSnapshot GetSnapshot(GameProfile value)
        {
            lock (gate)
            {
                if (value == null || profile == null || !SameProfile(profile, value))
                    return new WeGameShellSnapshot { ProfileId = value == null ? "" : value.Id, Generation = generation, Status = Text("等待已观测的游戏渲染进程启动", "Waiting for an observed game renderer to start"), Error = "" };
                return new WeGameShellSnapshot
                {
                    ProfileId = profile.Id, RendererPid = rendererPid, RendererCreation = rendererCreation, Generation = generation,
                    Busy = cleaning, Detached = detached, Status = status, Error = error
                };
            }
        }

        private void Run(int epoch)
        {
            while (true)
            {
                lock (gate) { if (!running || disposed || workerEpoch != epoch) return; }
                RunCycle(epoch);
                wake.WaitOne(1000);
            }
        }

        private void RunCycle(int executionEpoch)
        {
            int epoch;
            lock (gate)
            {
                if (!running || disposed || workerEpoch != executionEpoch) return;
                epoch = generation;
            }
            try { Observe(executionEpoch); }
            catch (Exception ex)
            {
                lock (gate)
                {
                    if (!running || disposed || generation != epoch || workerEpoch != executionEpoch) return;
                    if (profile != null && activeReceipt == null) ClaimAttemptLocked(profile, rendererPid, rendererCreation);
                    attempted = true; cleaning = false; error = ex.Message;
                    status = Text("WeGame 精简失败，本次会话已停止", "WeGame cleanup failed; stopped for this session");
                    FinishReceiptLocked();
                }
                RaiseChanged();
            }
        }

        private void Observe(int executionEpoch)
        {
            GameProfile observedProfile; int pid; long creation; int epoch; string path; bool discover;
            lock (gate)
            {
                if (!running || disposed || workerEpoch != executionEpoch || profile == null || attempted || detached || cleaning) return;
                if (!WeGameGameExtension.AppliesToProfile(profile)) { generation++; ResetSessionLocked(); return; }
                observedProfile = profile.Clone(); pid = rendererPid; creation = rendererCreation; epoch = generation; path = rendererPath;
                discover = !discoveryAttempted || string.IsNullOrEmpty(weGameRoot) && NowTicks() >= nextDiscoveryAt;
                discoveryAttempted = true;
                if (discover) nextDiscoveryAt = NowTicks() + 30 * TimeSpan.TicksPerSecond;
            }
            string root = discover ? FindRoot(observedProfile) : null;
            GameProcessSnapshot identity;
            bool valid = TryRendererIdentity(observedProfile, pid, creation, path, out identity);
            bool execute = false;
            lock (gate)
            {
                if (!CurrentLocked(epoch, observedProfile, pid, creation) || workerEpoch != executionEpoch || attempted || cleaning || detached) return;
                if (discover) weGameRoot = root;
                if (valid && rendererPath == null) rendererPath = identity.Path;
                if (!valid)
                {
                    if (!ClaimAttemptLocked(observedProfile, pid, creation)) return;
                    attempted = true;
                    error = Text("无法验证当前渲染进程身份，已跳过本次脱壳", "The current renderer identity could not be verified; skipped this session");
                    status = Text("WeGame 精简已跳过", "WeGame cleanup skipped");
                    FinishReceiptLocked();
                }
                else if (string.IsNullOrEmpty(weGameRoot))
                    status = Text("未找到可验证的 WeGame 安装目录", "A verified WeGame installation was not found");
                else
                {
                    if (!ClaimAttemptLocked(observedProfile, pid, creation)) return;
                    attempted = true; cleaning = true; execute = true;
                    status = Text("已观测的渲染进程已启动，正在退出 WeGame 界面", "Observed renderer started; closing the WeGame interface");
                }
            }
            RaiseChanged();
            if (execute) Detach(epoch, observedProfile, pid, creation);
        }

        private void Detach(int epoch, GameProfile currentProfile, int pid, long creation)
        {
            string root;
            lock (gate) { if (!CurrentLocked(epoch, currentProfile, pid, creation)) return; root = weGameRoot; }
            bool complete; List<GameProcessSnapshot> candidates = CaptureCandidates(root, out complete);
            int closed = 0; int failed = 0; bool cancelled = false;
            foreach (GameProcessSnapshot candidate in candidates)
            {
                lock (gate)
                {
                    // Serialize invalidation with native mutation to cancel queued termination after Stop/renderer changes.
                    GameProcessSnapshot game;
                    if (!CurrentLocked(epoch, currentProfile, pid, creation) || !cleaning || !TryRendererIdentity(currentProfile, pid, creation, rendererPath, out game))
                    { cancelled = true; break; }
                    // Refresh ancestry immediately before every termination.
                    bool freshComplete;
                    List<GameProcessSnapshot> fresh = CaptureCandidates(root, out freshComplete);
                    if (!freshComplete || !ContainsIdentity(fresh, candidate)) { failed++; break; }
                    if (!CurrentLocked(epoch, currentProfile, pid, creation) || !cleaning || !TryRendererIdentity(currentProfile, pid, creation, rendererPath, out game))
                    { cancelled = true; break; }
                    if (TerminateCandidate(candidate, root)) closed++; else { failed++; break; }
                }
            }
            lock (gate)
            {
                if (!CurrentLocked(epoch, currentProfile, pid, creation))
                {
                    if (generation == epoch) CancelAttemptLocked();
                    return;
                }
                cleaning = false;
                if (cancelled || !complete || failed > 0)
                {
                    error = Text("未完整执行，本次会话已停止，不会连续重试", "Cleanup did not finish; stopped for this session without repeated retries");
                    status = Text("WeGame 精简已停止", "WeGame cleanup stopped");
                }
                else
                {
                    detached = true; error = "";
                    status = closed > 0 ? Text("WeGame 界面已退出，登录与运行组件保留", "WeGame interface closed; login and runtime components retained") : Text("没有可退出的 WeGame 界面进程", "No eligible WeGame interface process is running");
                }
                FinishReceiptLocked();
            }
            RaiseChanged();
        }

        private bool TryRendererIdentity(GameProfile currentProfile, int pid, long creation, string expectedPath, out GameProcessSnapshot identity)
        {
            identity = null;
            GameProcessSnapshot captured;
#if PAVISE_SELFTEST
            if (testOnly)
            {
                if (ProbeRendererForTest == null) throw new InvalidOperationException("A renderer probe fixture is required");
                captured = ProbeRendererForTest(currentProfile, pid, creation, expectedPath);
            }
            else
#endif
            {
                if (!GameSessionDetector.TryCaptureProcessIdentity(pid, ownerSession, out captured)) return false;
            }
            if (captured == null || captured.Pid != pid || captured.Creation != creation || !NameMatchesPath(captured) || GameSessionDetector.ElectionVetoed(captured.Name, captured.Path) || AntiCheatCatalog.IsAntiCheatProcess(captured.Name, captured.Path) || !SamePath(captured.Path, currentProfile.ExecutablePath) || !BelongsToProfile(currentProfile, captured.Path) || (!string.IsNullOrEmpty(expectedPath) && !SamePath(expectedPath, captured.Path))) return false;
            identity = captured; return true;
        }

        internal static bool BelongsToProfile(GameProfile value, string path)
        {
            if (value == null || string.IsNullOrEmpty(path)) return false;
            if (SamePath(value.ExecutablePath, path) || SamePath(value.LearnedExecutablePath, path)) return true;
            string restricted = GameInstallScope.RestrictFallback(value.PreferredExecutablePath, value.Root);
            return !string.IsNullOrEmpty(restricted) && LolRuntimeProcesses.IsUnder(path, restricted);
        }

        private bool CurrentLocked(int epoch, GameProfile value, int pid, long creation)
        { return running && !disposed && generation == epoch && profile != null && SameProfile(profile, value) && WeGameGameExtension.AppliesToProfile(profile) && rendererPid == pid && rendererCreation == creation; }

        private void ResetSessionLocked()
        {
            if (activeReceipt != null && !activeReceipt.Finished) CancelAttemptLocked();
            profile = null; rendererPid = 0; rendererCreation = 0; rendererPath = null; weGameRoot = null;
            discoveryAttempted = false; nextDiscoveryAt = 0; attempted = false; cleaning = false; detached = false; activeReceipt = null; status = ""; error = "";
        }

        // Retain live cleanup receipts across detector gaps and Stop/Start; never evict them for capacity.
        private bool ClaimAttemptLocked(GameProfile value, int pid, long creation)
        {
            string key = AttemptKey(value, pid, creation);
            AttemptReceipt receipt;
            if (attemptReceipts.TryGetValue(key, out receipt)) { RestoreReceiptLocked(receipt); return false; }
            if (attemptReceipts.Count >= MaximumAttemptReceipts)
            {
                attempted = true; cleaning = false;
                status = Text("WeGame 自动精简暂不可用", "WeGame automatic cleanup is currently unavailable");
                error = Text("本次运行的会话记录已达到上限，已跳过自动操作", "The session-record limit for this run was reached; automatic cleanup was skipped");
                return false;
            }
            activeReceipt = new AttemptReceipt();
            attemptReceipts.Add(key, activeReceipt);
            return true;
        }

        private static string AttemptKey(GameProfile value, int pid, long creation)
        {
            // Bind cleanup to the executable path so renderer learning cannot re-admit the same live process.
            string binding = string.IsNullOrEmpty(value.ExecutablePath) ? value.Root : value.ExecutablePath;
            try { binding = Path.GetFullPath(binding ?? "").TrimEnd('\\', '/').ToUpperInvariant(); }
            catch { binding = "<invalid>"; }
            return (value.Id ?? "").Length.ToString() + ":" + value.Id + "|" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + creation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + binding;
        }

        private void RestoreReceiptLocked(AttemptReceipt receipt)
        {
            activeReceipt = receipt; attempted = true; cleaning = false; detached = receipt.Detached;
            status = receipt.Status ?? Text("本次渲染进程已处理", "This renderer instance has already been handled");
            error = receipt.Error ?? "";
        }

        private void FinishReceiptLocked()
        {
            if (activeReceipt == null) return;
            activeReceipt.Finished = true; activeReceipt.Detached = detached; activeReceipt.Status = status; activeReceipt.Error = error;
        }

        private void CancelAttemptLocked()
        {
            attempted = true; cleaning = false; detached = false;
            status = Text("本次渲染进程的 WeGame 精简已取消", "WeGame cleanup was cancelled for this renderer instance");
            error = ""; FinishReceiptLocked();
        }

        private static bool SameProfile(GameProfile a, GameProfile b)
        {
            return a != null && b != null && string.Equals(a.Id, b.Id, StringComparison.Ordinal) && SameOptionalPath(a.Root, b.Root) && SameOptionalPath(a.ExecutablePath, b.ExecutablePath) && SameOptionalPath(a.LearnedExecutablePath, b.LearnedExecutablePath);
        }
        private static bool SameOptionalPath(string a, string b) { return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b) || SamePath(a, b); }
        private static bool SamePath(string a, string b)
        { try { return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); } catch { return false; } }
        private static long MonotonicTicks() { return (long)(Stopwatch.GetTimestamp() * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)); }
        private long NowTicks()
        {
#if PAVISE_SELFTEST
            if (testOnly)
            {
                if (ClockForTest == null) throw new InvalidOperationException("A clock fixture is required");
                return ClockForTest();
            }
#endif
            return MonotonicTicks();
        }
        private string FindRoot(GameProfile value)
        {
#if PAVISE_SELFTEST
            if (testOnly)
            {
                if (FindRootForTest == null) throw new InvalidOperationException("A WeGame root fixture is required");
                return FindRootForTest(value);
            }
#endif
            return LolInstallDiscovery.FindWeGameRoot(null, value.Root, false, null);
        }
        private List<GameProcessSnapshot> CaptureCandidates(string root, out bool complete)
        {
#if PAVISE_SELFTEST
            if (testOnly)
            {
                if (CaptureCandidatesForTest == null) throw new InvalidOperationException("A candidate capture fixture is required");
                WeGameShellCapture captured = CaptureCandidatesForTest(root);
                complete = captured != null && captured.Complete;
                return complete ? SelectShellCandidates(captured.Entries, root) : new List<GameProcessSnapshot>();
            }
#endif
            return CaptureShellCandidates(root, ownerSession, out complete);
        }
        private bool TerminateCandidate(GameProcessSnapshot candidate, string root)
        {
#if PAVISE_SELFTEST
            if (testOnly)
            {
                if (TerminateForTest == null) throw new InvalidOperationException("A termination fixture is required");
                return IsShellCandidate(candidate.Path, root) && TerminateForTest(candidate, root);
            }
#endif
            return TerminateVerified(candidate, root, ownerSession);
        }
        private static string Text(string zh, string en) { return PerformancePolicy.Text(zh, en); }
        private void RaiseChanged() { Action changed = Changed; if (changed != null) { try { changed(); } catch { } } }

        internal static bool IsShellCandidate(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root) || !LolRuntimeProcesses.IsUnder(path, root)) return false;
            string file = Path.GetFileName(path);
            return string.Equals(file, "wegame.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(file, "wegameclient.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(file, "browser.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(file, "crashpad_handler.exe", StringComparison.OrdinalIgnoreCase);
        }

        // Require verified UI-shell ancestry; exclude runtime and anti-cheat families.
        internal static List<GameProcessSnapshot> SelectShellCandidates(IList<GameProcessSnapshot> entries, string root)
        {
            List<GameProcessSnapshot> result = new List<GameProcessSnapshot>();
            Dictionary<int, GameProcessSnapshot> byPid = new Dictionary<int, GameProcessSnapshot>();
            if (entries == null) return result;
            foreach (GameProcessSnapshot item in entries)
            {
                if (item == null || item.Pid <= 0 || item.Creation <= 0 || byPid.ContainsKey(item.Pid)) return result;
                byPid.Add(item.Pid, item);
            }
            foreach (GameProcessSnapshot item in entries)
            {
                if (!IsShellCandidate(item.Path, root) || !NameMatchesPath(item)) continue;
                bool helper = IsHelper(item.Path);
                bool shellAncestor = false; bool protectedAncestor = false;
                GameProcessSnapshot node = item;
                HashSet<int> visited = new HashSet<int>();
                while (node != null)
                {
                    if (!visited.Add(node.Pid) || !NameMatchesPath(node) || IsProtectedRole(node)) { protectedAncestor = true; break; }
                    if (node.Pid != item.Pid && IsShellCandidate(node.Path, root) && !IsHelper(node.Path)) shellAncestor = true;
                    GameProcessSnapshot parent;
                    if (node.ParentPid <= 0 || !byPid.TryGetValue(node.ParentPid, out parent)) break;
                    if (parent.Creation >= node.Creation) { protectedAncestor = true; break; }
                    node = parent;
                }
                if (!protectedAncestor && (!helper || shellAncestor)) result.Add(item);
            }
            // Close UI children before roots so subsequent ancestry checks retain evidence.
            result.Sort(delegate(GameProcessSnapshot a, GameProcessSnapshot b) { return b.Creation.CompareTo(a.Creation); });
            return result;
        }

        private static bool NameMatchesPath(GameProcessSnapshot item)
        { return !string.IsNullOrEmpty(item.Name) && !string.IsNullOrEmpty(item.Path) && string.Equals(item.Name, Path.GetFileNameWithoutExtension(item.Path), StringComparison.OrdinalIgnoreCase); }
        private static bool IsHelper(string path)
        { string file = Path.GetFileName(path); return string.Equals(file, "browser.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(file, "crashpad_handler.exe", StringComparison.OrdinalIgnoreCase); }
        private static bool IsProtectedRole(GameProcessSnapshot item)
        {
            string name = item.Name;
            return string.Equals(name, "wegame_env", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "pallas", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "rail", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "tcls_core", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "teniodl", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "wegameupdate", StringComparison.OrdinalIgnoreCase) || AntiCheatCatalog.IsAntiCheatProcess(name, item.Path);
        }

        private static List<GameProcessSnapshot> CaptureShellCandidates(string root, int session, out bool complete)
        {
            complete = false;
            List<GameProcessSnapshot> entries = new List<GameProcessSnapshot>();
            if (session < 0 || !LolInstallDiscovery.IsValidWeGameRoot(root) || !GameInstallScope.IsSafePath(root, File.GetAttributes)) return entries;
            Process[] all;
            try { all = Process.GetProcesses(); } catch { return entries; }
            try
            {
                foreach (Process process in all)
                {
                    try
                    {
                        if (process.SessionId != session) continue;
                        GameProcessSnapshot identity;
                        if (GameSessionDetector.TryCaptureProcessIdentity(process.Id, session, out identity)) entries.Add(identity);
                        else
                        {
                            string name = process.ProcessName;
                            if (WeGameRuntimeProtection.IsRuntimeName(name) || AntiCheatCatalog.IsKnownProcess(name) || string.Equals(name, "browser", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "crashpad_handler", StringComparison.OrdinalIgnoreCase)) return new List<GameProcessSnapshot>();
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch { return new List<GameProcessSnapshot>(); }
                }
                complete = true; return SelectShellCandidates(entries, root);
            }
            finally { foreach (Process process in all) process.Dispose(); }
        }

        private static bool ContainsIdentity(IList<GameProcessSnapshot> list, GameProcessSnapshot expected)
        {
            foreach (GameProcessSnapshot item in list) if (item.Pid == expected.Pid && item.Creation == expected.Creation && SamePath(item.Path, expected.Path)) return true;
            return false;
        }

        private static bool TerminateVerified(GameProcessSnapshot expected, string root, int session)
        {
            if (!IsShellCandidate(expected.Path, root) || !GameInstallScope.IsSafePath(expected.Path, File.GetAttributes)) return false;
            IntPtr handle = Native.OpenProcess(1052673, false, expected.Pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                int actualSession; long creation; long cpu; ulong io;
                return Native.TryGetLiveProcessSessionId(handle, expected.Pid, out actualSession) && actualSession == session && SamePath(Native.ImagePath(handle), expected.Path) && Native.QueryProcessSample(handle, out creation, out cpu, out io) && creation == expected.Creation && TerminateProcess(handle, 0) && Native.WaitForSingleObject(handle, 750) == 0;
            }
            finally { Native.CloseHandle(handle); }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr handle, uint code);
    }
}
