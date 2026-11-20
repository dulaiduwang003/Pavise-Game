using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    // 自动入库只通过这个接口读游戏模式的状态和请它做事 不直接碰 GameMode 的字段
    internal interface IAutoAddHost
    {
        // 就是游戏模式自己的 sync 锁 游戏库和会话状态都在这把锁下读写
        object Sync { get; }
        bool AutoAddOn { get; }
        bool Stopping { get; }
        bool PanicRequested { get; }
        bool GuardEnabled { get; }
        bool ProfileStoreSaveFailed { get; }
        bool AutoIgnoreLoadFailed { get; }
        bool LibraryRecoveryPending { get; }
        // 只在持有 Sync 时读
        bool GameActive { get; }
        int SelfPid { get; }
        int SelfSession { get; }
        string WindowsPrefix { get; }
        bool IgnoredLocked(string path);
        bool LibraryClaimsLocked(string path);
        bool AutoDetectedCommitAllowed(string path);
        bool AddAutoDetectedExecutable(string path, out string error, out bool persistenceFailed, Func<bool> canCommit);
        // 和渲染进程探测共用一条 GPU 采样通道
        bool TryEnterGpuSampling();
        void LeaveGpuSampling();
    }

    // 前台全屏程序自动加入游戏库 节流 失败预算和逐路径退避都在这里 实例归 GameMode 所有
    internal sealed class AutoAddCoordinator
    {
        private sealed class Attempt
        {
            internal int Pid;

            internal int Misses;

            internal int Confirmations;

            internal long Creation;

            internal long EvidenceAt;

            internal long LastSeen;

            internal long NoteAt;

            internal string Note;
        }

        private readonly IAutoAddHost host;
        private readonly ProcessActionFailureBudget failureBudget = new ProcessActionFailureBudget();
        private readonly Dictionary<string, Attempt> attempts = new Dictionary<string, Attempt>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> rejectUntil = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private int currentEpoch;
        private long gateTicks;

        internal AutoAddCoordinator(IAutoAddHost host)
        {
            if (host == null) throw new ArgumentNullException("host");
            this.host = host;
        }

#if PAVISE_SELFTEST
        internal Func<long> NowForTest;
        internal Func<int> ForegroundForTest;
        internal Func<ForegroundGameWindowSnapshot> WindowForTest;
        internal Func<int, GameProcessSnapshot> IdentityForTest;
        internal Func<ProcessSnapshot> SnapshotForTest;
        internal Func<Func<bool>, Dictionary<int, double>> GpuForTest;
#endif

        // 守护开关 自动入库开关或忽略名单变化后 已经在采样的那一轮作废
        internal void Invalidate()
        {
            Interlocked.Increment(ref currentEpoch);
        }

        internal void OpenGate()
        {
            Interlocked.Exchange(ref gateTicks, 0L);
        }

        // 调用方持有 Sync 用户把路径移出忽略名单后 它的退避记录清掉 下一轮立即重新判断
        internal void ResetPathLocked(string path)
        {
            rejectUntil.Remove(path);
            attempts.Remove(path);
            Interlocked.Increment(ref currentEpoch);
            Interlocked.Exchange(ref gateTicks, 0L);
        }

        internal void TryForegroundGame()
        {
            if (!host.AutoAddOn || host.Stopping || !host.GuardEnabled || host.ProfileStoreSaveFailed || host.AutoIgnoreLoadFailed || host.LibraryRecoveryPending)
            {
                return;
            }
            bool activeSnapshot;
            int epoch;
            lock (host.Sync)
            {
                activeSnapshot = host.GameActive;
                epoch = Volatile.Read(ref currentEpoch);
            }
            ForegroundGameWindowSnapshot window;
            if (activeSnapshot || !ForegroundWindow(out window) || window.Pid <= 0 || window.Pid == host.SelfPid)
            {
                return;
            }
            int pid = window.Pid;
            long now = DateTime.UtcNow.Ticks;
#if PAVISE_SELFTEST
            if (NowForTest != null) now = NowForTest();
#endif
            if (now < gateTicks)
            {
                return;
            }
            Interlocked.Exchange(ref gateTicks, now + TimeSpan.FromSeconds(5.0).Ticks);
            GameProcessSnapshot identity;
            if (!CaptureIdentity(pid, out identity) || !GameSessionDetector.IsLibraryCandidate(identity.Name, identity.Path, host.WindowsPrefix) || identity.Pid != pid || identity.Creation <= 0)
            {
                return;
            }
            identity = new GameProcessSnapshot
            {
                Pid = pid,
                Creation = identity.Creation,
                Name = identity.Name,
                Path = identity.Path
            };
            string path = identity.Path;
            if (!failureBudget.Allowed(pid, identity.Creation, path, "capture")
                || !failureBudget.Allowed(pid, identity.Creation, path, "save")) return;
            lock (host.Sync)
            {
                if (host.IgnoredLocked(path))
                {
                    return;
                }
                Attempt value;
                if (attempts.TryGetValue(path, out value) && (value.Pid != pid || value.Creation != identity.Creation))
                {
                    attempts.Remove(path);
                    rejectUntil.Remove(path);
                }
                long value2;
                if (rejectUntil.TryGetValue(path, out value2) && now < value2)
                {
                    return;
                }
                if (host.LibraryClaimsLocked(path))
                {
                    return;
                }
                value = AttemptLocked(path, now);
                value.Pid = pid;
                value.Creation = identity.Creation;
                value.LastSeen = now;
            }
            if (!host.TryEnterGpuSampling())
            {
                return;
            }
            bool scanQueued = false;
            try
            {
                scanQueued = ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        Finish(identity, window.FullscreenLike, epoch, now);
                    }
                    catch (Exception ex)
                    {
                        if (!Canceled(epoch))
                        {
                            RememberReject(path, now);
                            RecordCaptureFailure(pid, identity.Creation, path, "capture", ex.GetType().Name);
                            Note(path, "CaptureUnavailable", ex.GetType().Name, now);
                        }
                    }
                    finally
                    {
                        host.LeaveGpuSampling();
                    }
                });
            }
            catch
            {
            }
            finally
            {
                if (!scanQueued)
                {
                    RecordCaptureFailure(pid, identity.Creation, path, "queue", "QueueUnavailable");
                    RememberReject(path, DateTime.UtcNow.Ticks);
                    host.LeaveGpuSampling();
                }
                else
                {
                    Interlocked.Exchange(ref gateTicks, now + 300000000);
                }
            }
        }
        private bool Canceled(int epoch)
        {
            if (!host.Stopping && !host.PanicRequested && host.GuardEnabled && host.AutoAddOn)
            {
                return Volatile.Read(ref currentEpoch) != epoch;
            }
            return true;
        }
        private void RecordCaptureFailure(int pid, long creation, string path, string phase, string reason)
        {
            int count = failureBudget.Failed(pid, creation, path, "capture");
            Logger.Warn(Logger.Format("自动入库 {0} 失败详情={1}/3 目标={2}/{3} 原因={4}{5}", "Auto-add {0} failure={1}/3 target={2}/{3} reason={4}{5}", phase, count, pid, creation, reason, (count >= 3 ? Logger.Text("；该进程身份的预算已耗尽", "; exhausted for this process identity") : Logger.Text("；等待退避", "; backing off"))));
        }
        private void Finish(GameProcessSnapshot identity, bool fullscreen, int epoch, long now)
        {
            if (Canceled(epoch))
            {
                return;
            }
            int pid = identity.Pid;
            string path = identity.Path;
            GpuSampleDiagnostics diagnostics = null;
            Dictionary<int, double> dictionary;
#if PAVISE_SELFTEST
            if (GpuForTest != null) dictionary = GpuForTest(() => Canceled(epoch));
            else
#endif
            dictionary = GpuEvidence.Sample3D(2, 700, () => Canceled(epoch), pid, out diagnostics);
            if (Canceled(epoch))
            {
                return;
            }
            if (dictionary == null)
            {
                RecordCaptureFailure(pid, identity.Creation, path, "capture", diagnostics == null ? "GpuUnavailable" : diagnostics.ToString());
                RememberReject(path, now);
                Note(path, "GpuUnavailable", (diagnostics == null) ? null : diagnostics.ToString(), now);
                return;
            }
            double value;
            if (!dictionary.TryGetValue(pid, out value))
            {
                value = 0.0;
            }
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 10.0)
            {
                lock (host.Sync)
                {
                    Attempt value2;
                    if (attempts.TryGetValue(path, out value2))
                    {
                        value2.Confirmations = 0;
                    }
                }
                RememberReject(path, now);
                Note(path, "ActivityWaiting", null, now);
                return;
            }
            bool activeSnapshot;
            lock (host.Sync)
            {
                activeSnapshot = host.GameActive;
            }
            ForegroundGameWindowSnapshot window;
            GameProcessSnapshot identity2;
            if (activeSnapshot || Canceled(epoch) || !ForegroundWindow(out window) || window.Pid != pid || !CaptureIdentity(pid, out identity2) || identity2 == null || identity2.Pid != pid || identity2.Creation != identity.Creation || !GameMode.SameLibraryPath(identity2.Path, path))
            {
                return;
            }
            lock (host.Sync)
            {
                if (Canceled(epoch) || !host.AutoDetectedCommitAllowed(path))
                {
                    return;
                }
                Attempt attempt = AttemptLocked(path, now);
                if (attempt.Pid != pid || attempt.Creation != identity.Creation)
                {
                    attempt.Pid = pid;
                    attempt.Creation = identity.Creation;
                    attempt.Confirmations = (attempt.Misses = 0);
                }
                attempt.LastSeen = now;
                attempt.Misses = 0;
                if ((!fullscreen || !window.FullscreenLike || !(value >= 20.0)) && (attempt.Confirmations == 0 || now < attempt.EvidenceAt || now - attempt.EvidenceAt > 1200000000))
                {
                    attempt.Confirmations = 1;
                    attempt.EvidenceAt = now;
                    rejectUntil[path] = now + 300000000;
                    Note(path, "ConfirmingActivity", null, now);
                    return;
                }
            }
            string error;
            bool persistenceFailed;
            if (!failureBudget.Allowed(pid, identity.Creation, path, "save")) return;
            if (!host.AddAutoDetectedExecutable(path, out error, out persistenceFailed, () => !Canceled(epoch)))
            {
                if (persistenceFailed)
                {
                    int failed = failureBudget.Failed(pid, identity.Creation, path, "save");
                    Logger.Warn(Logger.Format("自动入库持久保存失败={0}/3 目标={1}/{2} 原因={3}{4}", "Auto-add durable save failure={0}/3 target={1}/{2} reason={3}{4}", failed, pid, identity.Creation, error, (failed >= 3 ? Logger.Text("；已停止该进程身份的自动保存", "; automatic save stopped for this process identity") : "")));
                }
                RememberReject(path, now);
                return;
            }
            failureBudget.Succeeded(pid, identity.Creation, path, "save");
            lock (host.Sync)
            {
                rejectUntil.Remove(path);
                attempts.Remove(path);
            }
            Logger.Log(() => Lang.T("log.autoadd.3") + (int)value + Lang.T("log.autoadd.4") + identity.Name + Lang.T("log.autoadd.5") + path + Lang.T("log.autoadd.6"));
        }
        private Attempt AttemptLocked(string path, long now)
        {
            Attempt value;
            if (attempts.TryGetValue(path, out value))
            {
                return value;
            }
            if (attempts.Count >= 64)
            {
                string key = null;
                long maxValue = long.MaxValue;
                foreach (KeyValuePair<string, Attempt> attempt2 in attempts)
                {
                    if (attempt2.Value.LastSeen < maxValue)
                    {
                        key = attempt2.Key;
                        maxValue = attempt2.Value.LastSeen;
                    }
                }
                if (key != null)
                {
                    attempts.Remove(key);
                    rejectUntil.Remove(key);
                }
            }
            Attempt attempt = new Attempt();
            attempt.LastSeen = now;
            value = attempt;
            attempts[path] = value;
            return value;
        }
        private void RememberReject(string path, long now)
        {
            lock (host.Sync)
            {
                if (rejectUntil.Count >= 64)
                {
                    List<string> list = new List<string>();
                    foreach (KeyValuePair<string, long> item in rejectUntil)
                    {
                        if (item.Value <= now)
                        {
                            list.Add(item.Key);
                        }
                    }
                    foreach (string item2 in list)
                    {
                        rejectUntil.Remove(item2);
                    }
                    if (rejectUntil.Count >= 64)
                    {
                        rejectUntil.Clear();
                    }
                }
                Attempt attempt = AttemptLocked(path, now);
                attempt.LastSeen = now;
                attempt.Confirmations = 0;
                if (attempt.Misses < 4)
                {
                    attempt.Misses++;
                }
                int rememberRejectValue = ((attempt.Misses == 1) ? 30 : ((attempt.Misses == 2) ? 60 : ((attempt.Misses == 3) ? 120 : 300)));
                rejectUntil[path] = now + (long)rememberRejectValue * 10000000L;
            }
        }
        private void Note(string path, string reason, string detail, long now)
        {
            if (reason == "ActivityWaiting" || reason == "ConfirmingActivity") return;
            lock (host.Sync)
            {
                Attempt attempt = AttemptLocked(path, now);
                if (attempt.Note == reason && now < attempt.NoteAt)
                {
                    return;
                }
                attempt.Note = reason;
                attempt.NoteAt = now + TimeSpan.FromMinutes(5.0).Ticks;
            }
            Logger.Info(Logger.Format("自动入库 路径={0} 结果={1}{2}", "AUTOADD exe={0} result={1}{2}", Path.GetFileName(path), reason, (string.IsNullOrEmpty(detail) ? "" : (Logger.Text(" 详情=", " detail=") + detail))));
        }
        private bool ForegroundWindow(out ForegroundGameWindowSnapshot window)
        {
#if PAVISE_SELFTEST
            if (WindowForTest != null) { window = WindowForTest(); return window != null && window.Pid > 0; }
            if (ForegroundForTest != null)
            {
                int pid = ForegroundForTest(); window = pid <= 0 ? null
                    : new ForegroundGameWindowSnapshot { Pid = pid, FullscreenLike = true };
                return window != null;
            }
#endif
            return GameSessionDetector.TryForegroundGameWindow(out window);
        }
        private bool CaptureIdentity(int pid, out GameProcessSnapshot identity)
        {
            identity = null;
            try
            {
#if PAVISE_SELFTEST
                if (IdentityForTest != null)
                {
                    identity = IdentityForTest(pid);
                    if (identity != null) return true;
                    if (SnapshotForTest == null) return false;
                    ProcessSnapshot mocked = SnapshotForTest();
                    return mocked != null && GameSessionDetector.TryCaptureProcessIdentity(mocked.Find(pid), host.SelfSession, out identity);
                }
#endif
                if (GameSessionDetector.TryCaptureProcessIdentity(pid, host.SelfSession, out identity))
                {
                    return true;
                }
                ProcessSnapshot processSnapshot = ProcessSnapshotSource.Capture(host.SelfSession, 0);
                return processSnapshot != null && GameSessionDetector.TryCaptureProcessIdentity(processSnapshot.Find(pid), host.SelfSession, out identity);
            }
            catch
            {
                identity = null;
                return false;
            }
        }
    }
}
