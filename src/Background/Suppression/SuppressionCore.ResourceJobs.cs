using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        internal const string ResourceJobPrefix = "Local\\Pavise.BackgroundJob.V1.";

        // 产品启动时注入 空着表示不用资源 Job
        internal static IResourceJobPlatform ResourceJobPlatform;

        private int resourceJobMode = -1;

        private sealed class ResourceJob
        {
            internal string Name;
            internal IntPtr Job;
            internal int Pid;
            internal long Creation;
            // 只记录已核验的限制 部分写入的恢复责任由日志里的 Job 名称保留
            internal uint? AppliedCpuRate;
            internal long AcceptedIoBandwidth;
            internal bool SkipIo;
            internal bool Unusable, Preparing;

            internal bool Matches(ResourceJobLimits want)
            {
                return AppliedCpuRate == want.CpuRate && (SkipIo || AcceptedIoBandwidth == want.IoBandwidth);
            }
        }

        // 按进程保留 Job 恢复后 Job 留空 同一进程下一局直接复用 进程退出时才关句柄
        private readonly Dictionary<int, ResourceJob> resourceJobs = new Dictionary<int, ResourceJob>();
        private readonly HashSet<string> resourceJobReasonsLogged = new HashSet<string>(StringComparer.Ordinal);
        private int resourceJobJoined, resourceJobFallbacks, resourceJobAnchorFailures, resourceJobPruneCounter;

        internal static bool ValidResourceJobName(string name)
        {
            int pid; long creation;
            return TryReadResourceJobIdentity(name, out pid, out creation);
        }

        private static bool TryReadResourceJobIdentity(string name, out int pid, out long creation)
        {
            pid = 0;
            creation = 0;
            if (name == null || !name.StartsWith(ResourceJobPrefix, StringComparison.Ordinal)) return false;
            string tail = name.Substring(ResourceJobPrefix.Length);
            string[] parts = tail.Split('.');
            Guid nonce;
            return parts.Length == 3 && parts[0].Length == 32 && Guid.TryParseExact(parts[0], "N", out nonce) && nonce != Guid.Empty
                && int.TryParse(parts[1], out pid) && pid > 4 && long.TryParse(parts[2], out creation) && creation > 0;
        }

        private static bool ResourceJobIdentityMatches(string name, int pid, long creation)
        {
            int jobPid; long jobCreation;
            return TryReadResourceJobIdentity(name, out jobPid, out jobCreation) && jobPid == pid && jobCreation == creation;
        }

        private ResourceJobLimits DesiredResourceLimits(Entry e)
        {
            int mode = resourceJobMode;
            if (mode != BackgroundGovernancePolicy.Aggressive || ResourceJobPlatform == null || e == null) return null;
            if ((e.Reasons & SuppressReason.Background) == 0 || e.BackgroundLevel < SuppressionLevel.Restrained) return null;
            // 反作弊不进资源 Job 扫描型反作弊会挂起游戏线程 它自己抢不到时间片 挂起就从几百毫秒拉长到秒级 硬限额等于把它饿死
            if (AntiCheatCatalog.IsAntiCheatLikeName(e.Name)) return null;
            return new ResourceJobLimits
            {
                CpuRate = BackgroundGovernancePolicy.ResourceCpuRate(mode, BackgroundResourceJobPlatform.LogicalProcessorCount),
                IoBandwidth = BackgroundGovernancePolicy.ResourceIoBandwidth(mode)
            };
        }

        // 核对时用 本方记得的限制和档位要的一致才算匹配 Job 的限制只有持有写权限的句柄能改 目标自己改不了
        private bool ResourceJobMatches(Entry e, int pid)
        {
            if (ResourceJobPlatform == null) return true;
            if (IsFocusTarget(e, pid)) return FocusResourceJobMatches(e, pid);
            if (e.FocusJob != null) return false;
            ResourceJobLimits want = DesiredResourceLimits(e);
            ResourceJob job;
            bool tracked = resourceJobs.TryGetValue(pid, out job) && job.Creation == e.Creation;
            if (want == null) return e.ResourceJobName == null && (!tracked || !job.AppliedCpuRate.HasValue);
            if (!tracked) return false;
            if (job.Unusable) return true;
            return job.Matches(want);
        }

        // 不再需要限制时先清空 否则 Job 锁住的 Idle 会让随后的优先级写回失效
        private bool LiveOrdinaryResourceJob(Entry entry, int pid)
        {
            ResourceJob job;
            return resourceJobs.TryGetValue(pid, out job) && job.Job != IntPtr.Zero && !job.Unusable
                && job.Creation == entry.Creation && job.Name == entry.ResourceJobName;
        }

        private bool PrepareResourceJob(Entry e, int pid)
        {
            if (e.ResourceJobOnlyIo || e.ResourceJobRestoringProcess) return false;
            lock (sync)
            {
                bool focus = IsFocusTarget(e, pid);
                if (!focus) ReleaseFocusForEntry(e, pid);
                if (focus || DesiredResourceLimits(e) == null || (e.ResourceJobName != null && !LiveOrdinaryResourceJob(e, pid)))
                    ReleaseOrdinaryResourceJobLocked(e, pid, false);
                return true;
            }
        }

        private void EnsureResourceJob(Entry e, int pid)
        {
            IResourceJobPlatform platform = ResourceJobPlatform;
            if (platform == null || pid <= 4 || e.Creation <= 0 || !Monitor.IsEntered(sync)) return;
            if (IsFocusTarget(e, pid))
            {
                EnsureFocusResourceJob(platform, e, pid);
                return;
            }
            ResourceJobLimits want = DesiredResourceLimits(e);
            if (want == null || e.FocusJob != null) return;
            PruneResourceJobs(platform);
            ResourceJob job;
            if (resourceJobs.TryGetValue(pid, out job) && job.Creation != e.Creation)
            {
                platform.Close(job.Job);
                resourceJobs.Remove(pid);
                job = null;
            }
            // 只靠日志找回的 Job 要先按原名恢复 新 Job 不能覆盖它的回执
            if (e.ResourceJobName != null && (job == null || e.ResourceJobName != job.Name)) return;
            if (job != null && (job.Preparing || job.Unusable || job.Matches(want))) return;
            IntPtr process = platform.OpenTarget(pid, e.Creation);
            if (process == IntPtr.Zero) { MarkResourceJobUnusable(e, pid, job, "process-access"); return; }
            try
            {
                if (!platform.SameSession(process)) { MarkResourceJobUnusable(e, pid, job, "other-session"); return; }
                if (job == null)
                {
                    string name = ResourceJobPrefix + Guid.NewGuid().ToString("N") + "." + pid + "." + e.Creation;
                    string reason;
                    IntPtr handle = platform.CreateAnchored(name, process, out reason);
                    if (handle == IntPtr.Zero)
                    {
                        if (reason != null && reason.StartsWith("anchor", StringComparison.Ordinal)) resourceJobAnchorFailures++;
                        MarkResourceJobUnusable(e, pid, null, reason ?? "create");
                        return;
                    }
                    job = new ResourceJob { Name = name, Job = handle, Pid = pid, Creation = e.Creation };
                    resourceJobs[pid] = job;
                }
                string previousName = e.ResourceJobName;
                bool previousDisk = e.ResourceJobMayHaveIo;
                bool applyDisk = !job.SkipIo && job.AcceptedIoBandwidth != want.IoBandwidth;
                e.ResourceJobName = job.Name;
                e.ResourceJobMayHaveIo = previousDisk || applyDisk;
                job.Preparing = true;
                IntPtr target = process;
                process = IntPtr.Zero;
                CommitBackgroundResource(
                    () => ApplyPreparedResourceJob(platform, job, e, pid, target, want, previousDisk, applyDisk),
                    () =>
                    {
                        job.Preparing = false;
                        if (e.ResourceJobName == job.Name)
                        {
                            e.ResourceJobName = previousName;
                            e.ResourceJobMayHaveIo = previousDisk;
                        }
                        NoteResourceJobFailure(e, job, "journal");
                        platform.Close(target);
                        SaveBackgroundResourceCleanup();
                    });
            }
            catch (Exception error)
            {
                NoteResourceJobFailure(e, job, "exception:" + error.GetType().Name);
            }
            finally { if (process != IntPtr.Zero) platform.Close(process); }
        }

        private void ApplyPreparedResourceJob(IResourceJobPlatform platform, ResourceJob job, Entry entry, int pid,
            IntPtr process, ResourceJobLimits limits, bool previousDisk, bool applyDisk)
        {
            job.Preparing = false;
            try
            {
                Entry live;
                if (!map.TryGetValue(pid, out live) || live != entry || entry.ResourceJobName != job.Name
                    || IsFocusTarget(entry, pid) || DesiredResourceLimits(entry) == null) return;
                entry.ResourceJobMayHaveIo = previousDisk;
                if (job.AppliedCpuRate != limits.CpuRate)
                {
                    bool wasApplied = job.AppliedCpuRate.HasValue;
                    job.AppliedCpuRate = null;
                    string failure;
                    if (!platform.Apply(job.Job, process, limits, out failure))
                    {
                        string clearFailure;
                        job.AcceptedIoBandwidth = 0;
                        if (ClearResourceLimits(platform, job.Job, entry, out clearFailure))
                        {
                            entry.ResourceJobName = null;
                            SaveBackgroundResourceCleanup();
                        }
                        NoteResourceJobFailure(entry, job, failure ?? "apply");
                        return;
                    }
                    if (!wasApplied) resourceJobJoined++;
                    job.AppliedCpuRate = limits.CpuRate;
                }
                if (applyDisk) ApplyResourceIo(platform, job, entry, limits.IoBandwidth, previousDisk);
            }
            catch (Exception error)
            {
                NoteResourceJobFailure(entry, job, "exception:" + error.GetType().Name);
            }
            finally { platform.Close(process); }
        }

        private void ApplyResourceIo(IResourceJobPlatform platform, ResourceJob job, Entry entry, long bandwidth, bool previouslyOwned)
        {
            entry.ResourceJobMayHaveIo = true;
            string reason;
            try
            {
                if (platform.SetIoBandwidth(job.Job, bandwidth, out reason))
                {
                    job.AcceptedIoBandwidth = bandwidth;
                    return;
                }
            }
            catch (EntryPointNotFoundException) { reason = "io:entry-point"; }
            catch (Exception error)
            {
                previouslyOwned = true;
                reason = "exception:" + error.GetType().Name;
            }
            job.SkipIo = true;
            job.AcceptedIoBandwidth = 0;
            LogResourceIoSkipped(entry, reason);
            string clearReason;
            if (previouslyOwned && !ClearResourceIo(platform, job.Job, out clearReason))
            {
                if (resourceJobReasonsLogged.Add("disk-restore:" + clearReason))
                    Logger.Warn(Logger.Format("后台磁盘限速未能解除：进程={0} 原因={1}；保留恢复记录",
                        "Background disk cap restore incomplete: process={0} reason={1}; restore record kept", entry.Name, clearReason));
                return;
            }
            entry.ResourceJobMayHaveIo = false;
            SaveBackgroundResourceCleanup();
        }

        private void LogResourceIoSkipped(Entry entry, string reason)
        {
            if (!resourceJobReasonsLogged.Add("disk-limit:" + reason)) return;
            Logger.Info(Logger.Format("后台磁盘限速已跳过：进程={0} 原因={1}；保留极低 I/O 优先级和 CPU 限额",
                "Background disk cap skipped: process={0} reason={1}; very low I/O priority and CPU cap retained", entry.Name, reason));
        }

        // 清空这个进程的资源 Job 限制 没有限制或进程已退出都算完成
        private bool ReleaseResourceJob(Entry e, int pid)
        {
            lock (sync) return ReleaseFocusForEntry(e, pid) & ReleaseOrdinaryResourceJobLocked(e, pid, true);
        }

        private bool ReleaseOrdinaryResourceJobLocked(Entry e, int pid, bool restoreProcess)
        {
            IResourceJobPlatform platform = ResourceJobPlatform;
            ResourceJob job;
            bool tracked = resourceJobs.TryGetValue(pid, out job) && job.Creation == e.Creation;
            if (e.ResourceJobName == null && (!tracked || !job.AppliedCpuRate.HasValue)) return true;
            if (platform == null) return false;
            string reason;
            if (tracked && job.Job != IntPtr.Zero && (e.ResourceJobName == null || job.Name == e.ResourceJobName))
            {
                e.ResourceJobName = job.Name;
                job.AppliedCpuRate = null;
                job.AcceptedIoBandwidth = 0;
                if (!ClearResourceLimits(platform, job.Job, e, out reason, restoreProcess))
                {
                    if (platform.Probe(pid, e.Creation) == 1) { e.ResourceJobName = null; return true; }
                    LogResourceJobReason("release:" + reason, e);
                    return false;
                }
                e.ResourceJobName = null;
                return true;
            }
            bool cleared = ClearResourceJobByName(platform, e, pid, out reason, restoreProcess);
            if (cleared) e.ResourceJobName = null;
            else LogResourceJobReason("release:" + reason, e);
            return cleared;
        }

        private static bool ClearResourceIo(IResourceJobPlatform platform, IntPtr job, out string reason)
        {
            try { return platform.SetIoBandwidth(job, 0, out reason); }
            catch (Exception error) { reason = "clear-io:" + error.GetType().Name; return false; }
        }

        private static bool ClearResourceLimits(IResourceJobPlatform platform, IntPtr job, Entry entry, out string reason, bool restoreProcess = true)
        {
            reason = null;
            if (!entry.ResourceJobOnlyIo && !platform.Clear(job, out reason)) return false;
            if (entry.ResourceJobMayHaveIo && !ClearResourceIo(platform, job, out reason))
            {
                if (!entry.ResourceJobOnlyIo && restoreProcess) entry.ResourceJobRestoringProcess = true;
                return false;
            }
            entry.ResourceJobMayHaveIo = false;
            return true;
        }

        // 崩溃恢复和日志载入的条目没有内存里的句柄 按名字重开
        private static bool ClearResourceJobByName(IResourceJobPlatform platform, Entry entry, int pid, out string reason, bool restoreProcess = true)
        {
            reason = null;
            string name = entry.ResourceJobName;
            long creation = entry.Creation;
            if (!ResourceJobIdentityMatches(name, pid, creation))
            { reason = "invalid-name"; return false; }
            int error;
            IntPtr job = platform.Open(name, out error);
            if (job == IntPtr.Zero)
            {
                // 名字不在了 目标已经退出就没有残留 目标还活着说明锚点丢了 限制只能等目标退出
                if (platform.Probe(pid, creation) == 1) return true;
                reason = "open:" + error;
                return false;
            }
            try { return ClearResourceLimits(platform, job, entry, out reason, restoreProcess); }
            finally { platform.Close(job); }
        }

        private void NoteResourceJobFailure(Entry e, ResourceJob job, string reason)
        {
            if (job != null) job.Unusable = true;
            resourceJobFallbacks++;
            LogResourceJobReason(reason, e);
        }

        private void MarkResourceJobUnusable(Entry e, int pid, ResourceJob job, string reason)
        {
            if (job == null)
            {
                job = new ResourceJob { Pid = pid, Creation = e.Creation };
                resourceJobs[pid] = job;
            }
            job.Unusable = true;
            resourceJobFallbacks++;
            LogResourceJobReason(reason, e);
        }

        // 同一原因只记一次 剩下的进程照旧逐进程写入
        private void LogResourceJobReason(string reason, Entry e)
        {
            string key = reason ?? "unknown";
            int colon = key.IndexOf(':');
            string family = colon > 0 ? key.Substring(0, colon) : key;
            if (!resourceJobReasonsLogged.Add(family)) return;
            Logger.Info(Logger.Format("后台资源 Job 退回逐进程写入：原因={0} 进程={1}", "Background resource Job fell back to per-process writes: reason={0} process={1}", key, (e != null ? e.Name : "?")));
        }

        // 进程退出后 Job 里已经没有进程 限制写着也不影响谁 只需关掉本方句柄
        private void PruneResourceJobs(IResourceJobPlatform platform)
        {
            if (++resourceJobPruneCounter < 64 && resourceJobs.Count < 512) return;
            resourceJobPruneCounter = 0;
            List<int> gone = new List<int>();
            foreach (KeyValuePair<int, ResourceJob> item in resourceJobs)
            {
                Entry e;
                bool governed = map.TryGetValue(item.Key, out e) && e.Creation == item.Value.Creation;
                if (!governed && platform.Probe(item.Key, item.Value.Creation) == 1) gone.Add(item.Key);
            }
            foreach (int pid in gone)
            {
                platform.Close(resourceJobs[pid].Job);
                resourceJobs.Remove(pid);
            }
        }

        // 对局摘要用 加入 Job 的进程数和退回逐进程的次数
        internal string ResourceJobSummary()
        {
            lock (sync)
            {
                if (ResourceJobPlatform == null || (resourceJobJoined == 0 && resourceJobFallbacks == 0)) return "";
                int active = 0, diskAccepted = 0, diskSkipped = 0;
                foreach (IEnumerable<FocusResourceJob> jobs in new IEnumerable<FocusResourceJob>[] { focusResourceJobs.Values, focusIndividualJobs.Values })
                {
                    foreach (FocusResourceJob job in jobs)
                    {
                        if (!job.Active) continue;
                        active++;
                        if (job.DiskOwned) diskAccepted++;
                        if (job.SkipDisk) diskSkipped++;
                    }
                }
                foreach (ResourceJob job in resourceJobs.Values)
                {
                    if (!job.AppliedCpuRate.HasValue) continue;
                    active++;
                    if (job.AcceptedIoBandwidth > 0) diskAccepted++;
                    if (job.SkipIo) diskSkipped++;
                }
                return Logger.Text(" 资源Job 生效=", " resourceJob active=") + active + Logger.Text(" 累计加入=", " joined=") + resourceJobJoined + Logger.Text(" 退回逐进程=", " perProcessFallbacks=") + resourceJobFallbacks
                    + Logger.Text(" 磁盘限速请求已接受=", " diskCapAccepted=") + diskAccepted + Logger.Text(" 磁盘限速已跳过=", " diskCapSkipped=") + diskSkipped
                    + (resourceJobAnchorFailures > 0 ? Logger.Text(" 锚点失败=", " anchorFailures=") + resourceJobAnchorFailures : "");
            }
        }
    }
}
