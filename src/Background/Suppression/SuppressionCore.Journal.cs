using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {

        private enum JournalIdentity
        {
            Confirmed,
            Unknown,
            Mismatch
        }

        private bool PersistJournalLocked()
        {
            if (batchDepth > 0 || journalDefer > 0)
            {
                batchJournalDirty = true;
                return true;
            }
            return ActiveBackgroundJournalRound == null ? SaveJournalLocked() : SaveBackgroundResourceCleanup();
        }

        private void BeginJournalDefer()
        {
            lock (sync)
            {
                journalDefer++;
            }
        }

        private void EndJournalDefer()
        {
            lock (sync)
            {
                if (journalDefer > 0)
                {
                    journalDefer--;
                    if (journalDefer == 0 && batchDepth == 0 && batchJournalDirty)
                    {
                        batchJournalDirty = false;
                        if (ActiveBackgroundJournalRound == null) SaveJournalLocked();
                        else SaveBackgroundResourceCleanup();
                    }
                }
            }
        }

        public static bool HasPendingJournalFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return false;
                }
                return File.ReadAllLines(path).Length > 1;
            }
            catch
            {
                return true;
            }
        }

        private long journalFailureLogAt;

        private static string ResourceJobJournalSuffix(Entry entry)
        {
            if (entry.ResourceJobName == null) return "";
            if (entry.ResourceJobOnlyIo) return "|job-io-v2|" + entry.ResourceJobName;
            if (entry.ResourceJobRestoringProcess) return "|job-restore-v2|" + entry.ResourceJobName;
            // 旧记录可能已经写入磁盘限额 只有明确无需解除磁盘限额才使用单独的 CPU 标记
            return (entry.ResourceJobMayHaveIo ? "|job-v1|" : "|job-cpu-v2|") + entry.ResourceJobName;
        }

        private static string FocusJobJournalSuffix(Entry entry)
        {
            FocusJobReceipt receipt = entry.FocusJob;
            return receipt == null ? "" : "|focus-v1|" + receipt.Name + "|" + receipt.Priority + "|" + (receipt.Disk ? "1" : "0");
        }

        private static string OrdinaryJournalSuffix(Entry entry)
        {
            if (!entry.OrdinaryValues) return "";
            int flags = (entry.OrdinaryPriorityOwned ? 1 : 0) | (entry.OrdinaryTimerOwned ? 2 : 0);
            return "|ordinary-v1|" + flags;
        }

        // 依照已恢复字段更新回执
        private static string UpdatedJournalLine(string original, Entry entry)
        {
            int gpuMarker = original.LastIndexOf("|gpu-v1|", StringComparison.Ordinal);
            if (gpuMarker >= 0) original = original.Substring(0, gpuMarker);
            else
            {
                int nameStart = original.LastIndexOf('|');
                if (nameStart > 0 && ValidResourceJobName(original.Substring(nameStart + 1)))
                    original = original.Substring(0, original.LastIndexOf('|', nameStart - 1));
            }
            int ordinaryMarker = original.LastIndexOf("|ordinary-v1|", StringComparison.Ordinal);
            if (ordinaryMarker >= 0) original = original.Substring(0, ordinaryMarker) + OrdinaryJournalSuffix(entry);
            int marker = original.LastIndexOf("|ac-resources|", StringComparison.Ordinal);
            if (HasAntiCheatResources(entry) && marker >= 0)
                original = original.Substring(0, marker) + "|ac-resources|" + EncodeAntiCheatResources(entry.AntiCheatResources);
            return original + "|gpu-v1|" + entry.GpuLease.Encode()
                + FocusJobJournalSuffix(entry) + ResourceJobJournalSuffix(entry);
        }

#if PAVISE_SELFTEST
        internal static Action<string, string, bool> BeforeJournalPublishForTest;
#endif

        private static void PublishJournalFile(string temporaryPath, string destinationPath)
        {
            for (int delay = 10; ; delay *= 2)
            {
                try
                {
                    bool replaceExisting = File.Exists(destinationPath);
#if PAVISE_SELFTEST
                    Action<string, string, bool> beforePublish = BeforeJournalPublishForTest;
                    if (beforePublish != null) beforePublish(temporaryPath, destinationPath, replaceExisting);
#endif
                    if (replaceExisting) File.Replace(temporaryPath, destinationPath, null);
                    else File.Move(temporaryPath, destinationPath);
                    return;
                }
                catch (IOException)
                {
                    if (delay > 80) throw;
                }
                catch (UnauthorizedAccessException)
                {
                    if (delay > 80) throw;
                }
                System.Threading.Thread.Sleep(delay);
            }
        }

        private bool SaveJournalLocked(bool durable = false, bool preserveBackgroundBudgets = false)
        {
            if (string.IsNullOrEmpty(journalPath))
            {
                MarkJournaledLocked(preserveBackgroundBudgets);
                return true;
            }
            try
            {
                List<string> lines = new List<string>();
                lines.Add("PAVISE_SUPPRESSION_V1");
                foreach (KeyValuePair<int, Entry> item in map)
                {
                    Entry entry = item.Value;
                    if (entry.OrigPri != uint.MaxValue)
                    {
                        if (entry.BackgroundJobPlacement || entry.AntiCheatPlacementExternal || HasAntiCheatResources(entry) || entry.AutomaticLayoutSets != null || entry.AutomaticLayoutYielded)
                        {
                            durable = true;
                        }
                        lines.Add(item.Key + "|" + entry.Creation + "|" + B64(entry.Name) + "|" + entry.OrigPri + "|" + entry.OrigAff + "|" + entry.OrigIo + "|" + entry.OrigPg + "|" + Native.CpuSetIdsText(entry.OrigCpuSets) + "|" + entry.OrigQoSControl + "|" + entry.OrigQoSState + "|" + entry.OrigGpu + "|" + entry.OrigBoost + (entry.AntiCheatPlacementExternal ? "|ac-placement|1|0" : ((entry.AutomaticLayoutSets != null || entry.AutomaticLayoutYielded) ? ("|layout|" + Native.CpuSetIdsText(entry.AutomaticLayoutSets) + "|" + (entry.AutomaticLayoutYielded ? "1" : "0")) : (entry.BackgroundPlacementOwned ? ("|" + (entry.BackgroundJobPlacement ? "bg-job" : "bg") + "|" + entry.BackgroundApplied + "|" + entry.BackgroundPrevious) : "")))
                            + (HasAntiCheatResources(entry) ? "|ac-resources|" + EncodeAntiCheatResources(entry.AntiCheatResources) : "")
                            + OrdinaryJournalSuffix(entry)
                            + "|gpu-v1|" + entry.GpuLease.Encode()
                            + FocusJobJournalSuffix(entry) + ResourceJobJournalSuffix(entry));
                    }
                }
                if (lines.Count == 1)
                {
                    if (File.Exists(journalPath))
                    {
                        File.Delete(journalPath);
                    }
                    MarkJournaledLocked(preserveBackgroundBudgets);
                    return true;
                }
                string journalPathOffset = journalPath + ".tmp";
                using (FileStream fileStream = new FileStream(journalPathOffset, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using (StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(false), 4096, true))
                    {
                        foreach (string line in lines)
                        {
                            streamWriter.WriteLine(line);
                        }
                        streamWriter.Flush();
                    }
                    if (durable)
                    {
                        fileStream.Flush(true);
                    }
                }
                PublishJournalFile(journalPathOffset, journalPath);
                bool resourceJournal = false;
                foreach (Entry entry in map.Values) resourceJournal |= HasAntiCheatResources(entry);
                if (resourceJournal)
                {
                    string[] actual = File.ReadAllLines(journalPath, Encoding.UTF8);
                    if (actual.Length != lines.Count) return false;
                    for (int i = 0; i < actual.Length; i++) if (actual[i] != lines[i]) return false;
                }
                MarkJournaledLocked(preserveBackgroundBudgets);
                return true;
            }
            catch (Exception error)
            {
                if (!preserveBackgroundBudgets || DateTime.UtcNow.Ticks >= journalFailureLogAt)
                {
                    if (preserveBackgroundBudgets) journalFailureLogAt = DateTime.UtcNow.AddSeconds(30).Ticks;
                    Logger.LogFailure(Lang.T("log.suppressioncorerecovery.1"), error);
                }
                return false;
            }
        }

        private void MarkJournaledLocked(bool preserveBackgroundBudgets = false)
        {
            backgroundJobJournalPending = false;
            backgroundJournalCleanupPending = false;
            if (!preserveBackgroundBudgets) backgroundJobJournalFailures.Clear();
            foreach (Entry entry in map.Values)
            {
                if (entry.OrigPri != uint.MaxValue)
                {
                    entry.Journaled = true;
                    entry.OrdinaryOwnershipJournalPending = false;
                }
            }
        }

        private void LoadJournal()
        {
            if (string.IsNullOrEmpty(journalPath) || !File.Exists(journalPath))
            {
                return;
            }
            try
            {
                string[] allLines = File.ReadAllLines(journalPath, Encoding.UTF8);
                if (allLines.Length == 0 || allLines[0] != "PAVISE_SUPPRESSION_V1")
                {
                    return;
                }
                for (int i = 1; i < allLines.Length; i++)
                {
                    int pid;
                    Entry entry = ParseJournalLine(allLines[i], out pid);
                    if (entry != null && pid > 0 && entry.OrigPri != uint.MaxValue)
                    {
                        entry.Journaled = true;
                        SetEntryLocked(pid, entry);
                    }
                }
            }
            catch (Exception error)
            {
                Logger.LogFailure(Lang.T("log.suppressioncorerecovery.2"), error);
            }
        }

        private static Entry ParseJournalLine(string line, out int pid)
        {
            pid = 0;
            string[] lineParts = line.Split('|');
            // 资源 Job 段在最后 先取出来 剩下的部分按没有 Job 段的格式解析
            string resourceJobName = null;
            string jobTag = lineParts.Length >= 11 ? lineParts[lineParts.Length - 2] : "";
            bool resourceJobMayHaveIo = jobTag == "job-v1" || jobTag == "job-restore-v2" || jobTag == "job-io-v2";
            if (resourceJobMayHaveIo || jobTag == "job-cpu-v2")
            {
                resourceJobName = lineParts[lineParts.Length - 1];
                Array.Resize(ref lineParts, lineParts.Length - 2);
            }
            FocusJobReceipt focusJob = null;
            if (lineParts.Length >= 13 && lineParts[lineParts.Length - 4] == "focus-v1")
            {
                int offset = lineParts.Length - 4;
                string name = lineParts[offset + 1];
                uint priority;
                string disk = lineParts[offset + 3];
                if ((!ValidFocusJobName(name) && !ValidResourceJobName(name)) || !uint.TryParse(lineParts[offset + 2], out priority)
                    || (priority != 0 && priority != 0x40 && priority != 0x4000 && priority != 0x20
                        && priority != 0x8000 && priority != 0x80 && priority != 0x100)
                    || (disk != "0" && disk != "1"))
                    return null;
                focusJob = new FocusJobReceipt { Name = name, Priority = priority, Disk = disk == "1" };
                Array.Resize(ref lineParts, offset);
            }
            GpuPriorityLease gpuLease = null;
            if (lineParts.Length >= 9 && lineParts[lineParts.Length - 2] == "gpu-v1")
            {
                gpuLease = GpuPriorityLease.Parse(lineParts[lineParts.Length - 1]);
                if (gpuLease == null) return null;
                Array.Resize(ref lineParts, lineParts.Length - 2);
            }
            int ordinaryFlags = -1;
            if (lineParts.Length >= 14 && lineParts[lineParts.Length - 2] == "ordinary-v1")
            {
                if (!int.TryParse(lineParts[lineParts.Length - 1], out ordinaryFlags) || ordinaryFlags < 0 || ordinaryFlags > 3) return null;
                Array.Resize(ref lineParts, lineParts.Length - 2);
            }
            AntiCheatResourceState resources = null;
            if (lineParts.Length >= 14 && lineParts[lineParts.Length - 2] == "ac-resources")
            {
                resources = ParseAntiCheatResources(lineParts[lineParts.Length - 1]);
                if (resources == null) return null;
                Array.Resize(ref lineParts, lineParts.Length - 2);
            }
            long creation;
            uint originalPriority;
            ulong originalAffinity;
            int originalIoPriority;
            int originalPagePriority;
            if ((lineParts.Length != 7 && lineParts.Length != 8 && lineParts.Length != 10 && lineParts.Length != 11 && lineParts.Length != 12 && lineParts.Length != 15) || !int.TryParse(lineParts[0], out pid) || !long.TryParse(lineParts[1], out creation) || !uint.TryParse(lineParts[3], out originalPriority) || !ulong.TryParse(lineParts[4], out originalAffinity) || !int.TryParse(lineParts[5], out originalIoPriority) || !int.TryParse(lineParts[6], out originalPagePriority))
            {
                return null;
            }
            uint[] origCpuSetsValue = ((lineParts.Length >= 8) ? Native.ParseCpuSetIds(lineParts[7]) : new uint[0]);
            if (resourceJobName != null && !ResourceJobIdentityMatches(resourceJobName, pid, creation)) return null;
            if (focusJob != null && !ValidFocusJobName(focusJob.Name)
                && (!ResourceJobIdentityMatches(focusJob.Name, pid, creation) || resourceJobName != null)) return null;
            if (origCpuSetsValue == null)
            {
                return null;
            }
            int originalQosControl = -1;
            int originalQosState = -1;
            if (lineParts.Length >= 10)
            {
                if (!int.TryParse(lineParts[8], out originalQosControl))
                {
                    originalQosControl = -1;
                }
                if (!int.TryParse(lineParts[9], out originalQosState))
                {
                    originalQosState = -1;
                }
            }
            int originalGpuPriority = -1;
            if (lineParts.Length >= 11 && !int.TryParse(lineParts[10], out originalGpuPriority))
            {
                originalGpuPriority = -1;
            }
            int originalBoost = -1;
            if (lineParts.Length >= 12 && !int.TryParse(lineParts[11], out originalBoost))
            {
                originalBoost = -1;
            }
            if (originalBoost < -1 || originalBoost > 1)
            {
                originalBoost = -1;
            }
            ulong backgroundApplied = 0uL;
            ulong backgroundPrevious = 0uL;
            bool layoutRecord = lineParts.Length == 15 && lineParts[12] == "layout";
            bool externalAcPlacement = lineParts.Length == 15 && lineParts[12] == "ac-placement";
            bool jobPlacement = lineParts.Length == 15 && lineParts[12] == "bg-job";
            uint[] automaticLayoutSetsValue = null;
            bool automaticLayoutYielded = false;
            if (layoutRecord)
            {
                automaticLayoutSetsValue = Native.ParseCpuSetIds(lineParts[13]);
                if (automaticLayoutSetsValue == null || automaticLayoutSetsValue.Length > 64 || new HashSet<uint>(automaticLayoutSetsValue).Count != automaticLayoutSetsValue.Length || (lineParts[14] != "0" && lineParts[14] != "1") || (automaticLayoutSetsValue.Length == 0 && lineParts[14] == "0") || origCpuSetsValue.Length != 0)
                {
                    return null;
                }
                automaticLayoutYielded = lineParts[14] == "1";
                if (automaticLayoutSetsValue.Length == 0)
                {
                    automaticLayoutSetsValue = null;
                }
            }
            else if (externalAcPlacement)
            {
                if (lineParts[13] != "1" || lineParts[14] != "0")
                {
                    return null;
                }
            }
            else if (lineParts.Length == 15 && ((lineParts[12] != "bg" && !jobPlacement) || !ulong.TryParse(lineParts[13], out backgroundApplied) || !ulong.TryParse(lineParts[14], out backgroundPrevious)))
            {
                return null;
            }
            string nameValue = Un64(lineParts[2]);
            if (nameValue == null)
            {
                return null;
            }
            Entry entry = new Entry();
            entry.Name = nameValue;
            entry.Creation = creation;
            entry.SetOriginalPriority(originalPriority);
            entry.OrdinaryValues = ordinaryFlags >= 0;
            entry.OrdinaryPriorityOwned = (ordinaryFlags & 1) != 0 && entry.OrdinaryValues;
            entry.OrdinaryTimerOwned = (ordinaryFlags & 2) != 0 && entry.OrdinaryValues;
            entry.OrigAff = originalAffinity;
            entry.OrigIo = originalIoPriority;
            entry.OrigPg = originalPagePriority;
            entry.OrigCpuSets = origCpuSetsValue;
            entry.OrigQoSControl = originalQosControl;
            entry.OrigQoSState = originalQosState;
            entry.OrigGpu = originalGpuPriority;
            entry.GpuLease = gpuLease ?? GpuPriorityLease.Legacy(originalGpuPriority);
            entry.ResourceJobName = resourceJobName;
            entry.FocusJob = focusJob;
            entry.ResourceJobMayHaveIo = resourceJobMayHaveIo;
            entry.ResourceJobRestoringProcess = jobTag == "job-restore-v2";
            entry.ResourceJobOnlyIo = jobTag == "job-io-v2";
            entry.OrigBoost = originalBoost;
            if (resources != null && resources.JobName != null && !AntiCheatJobIdentity.ValidName(resources.JobName, pid, entry.Creation)) return null;
            entry.AntiCheatPid = pid;
            entry.AntiCheatResources = resources;
            entry.AntiCheatPlacementExternal = externalAcPlacement;
            entry.LegacyPlacementDebt = !externalAcPlacement;
            entry.BackgroundPlacementOwned = lineParts.Length == 15 && !layoutRecord && !externalAcPlacement;
            entry.BackgroundJobPlacement = jobPlacement;
            entry.BackgroundPid = pid;
            entry.AutomaticLayoutSets = automaticLayoutSetsValue;
            entry.AutomaticLayoutYielded = automaticLayoutYielded;
            entry.BackgroundApplied = backgroundApplied;
            entry.BackgroundPrevious = backgroundPrevious;
            return entry;
        }

        private static JournalIdentity IdentifyJournalEntry(IntPtr h, Entry e)
        {
            if (e.Creation <= 0)
            {
                return JournalIdentity.Unknown;
            }
            string processName = Native.ImageName(h);
            if (processName != null && !SameName(processName, e.Name))
            {
                return JournalIdentity.Mismatch;
            }
            long creation;
            long cpu;
            ulong io;
            bool processSample = Native.QueryProcessSample(h, out creation, out cpu, out io);
            if (e.Creation > 0)
            {
                if (!processSample)
                {
                    return JournalIdentity.Unknown;
                }
                if (creation != e.Creation)
                {
                    return JournalIdentity.Mismatch;
                }
            }
            if (processName != null)
            {
                return JournalIdentity.Confirmed;
            }
            return JournalIdentity.Unknown;
        }

        public static int HealFromCrash(string statePath)
        {
            if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath))
            {
                return 0;
            }
            string jobReason;
            if (!GameAffinityJobLease.Recover(out jobReason)) return 0;
            int processedCount = 0;
            List<string> unsettledLines = new List<string>();
            unsettledLines.Add("PAVISE_SUPPRESSION_V1");
            try
            {
                string[] allLines = File.ReadAllLines(statePath, Encoding.UTF8);
                if (allLines.Length == 0 || allLines[0] != "PAVISE_SUPPRESSION_V1")
                {
                    return 0;
                }
                for (int i = 1; i < allLines.Length; i++)
                {
                    int pid;
                    Entry entry = ParseJournalLine(allLines[i], out pid);
                    if (entry == null)
                    {
                        unsettledLines.Add(allLines[i]);
                        Logger.Log(() => Lang.T("log.suppressioncorerecovery.3") + (i + 1) + Lang.T("log.suppressioncorerecovery.4"));
                        continue;
                    }
                    if (HasAntiCheatResources(entry) && !ClearAntiCheatJob(entry, null))
                    { unsettledLines.Add(UpdatedJournalLine(allLines[i], entry)); continue; }
                    IntPtr restoreHandle = Native.OpenProcess(12800, false, pid);
                    if (restoreHandle == IntPtr.Zero)
                    {
                        IntPtr queryHandle = Native.OpenProcess(4096, false, pid);
                        if (queryHandle != IntPtr.Zero)
                        {
                            try
                            {
                                if (IdentifyJournalEntry(queryHandle, entry) != JournalIdentity.Mismatch)
                                {
                                    unsettledLines.Add(allLines[i]);
                                }
                            }
                            finally
                            {
                                Native.CloseHandle(queryHandle);
                            }
                        }
                        else if (!Native.LastOpenProcessFailureWasNoSuchProcess())
                        {
                            unsettledLines.Add(UpdatedJournalLine(allLines[i], entry));
                        }
                        continue;
                    }
                    try
                    {
                        switch (IdentifyJournalEntry(restoreHandle, entry))
                        {
                        case JournalIdentity.Mismatch:
                            continue;
                        case JournalIdentity.Unknown:
                            unsettledLines.Add(allLines[i]);
                            continue;
                        }
                        Func<bool> resourceCheckpoint = delegate
                        {
                            allLines[i] = UpdatedJournalLine(allLines[i], entry);
                            // 前面已经结清的记录不能随当前字段的进度重新落盘
                            var pending = new List<string>(unsettledLines);
                            for (int lineIndex = i; lineIndex < allLines.Length; lineIndex++) pending.Add(allLines[lineIndex]);
                            try { return AtomicFile.WriteLines(statePath, pending.ToArray(), Lang.T("t.suppressioncorerecovery.6")); }
                            catch { return false; }
                        };
                        if (!entry.ResourceJobOnlyIo && entry.BackgroundJobPlacement && !ReleaseBackgroundJob(entry))
                        {
                            unsettledLines.Add(allLines[i]); continue;
                        }
                        if (entry.FocusJob != null)
                        {
                            string focusReason;
                            if (!RecoverFocusJob(ResourceJobPlatform, entry, pid, out focusReason))
                            {
                                Logger.Warn(Logger.Format("重点压制 Job 恢复未完成：进程={0} 原因={1}", "Focus Job recovery incomplete: process={0} reason={1}", entry.Name, focusReason));
                                unsettledLines.Add(UpdatedJournalLine(allLines[i], entry));
                                continue;
                            }
                        }
                        // 资源 Job 锁住的优先级要先解开 后面的原值才写得回去
                        if (entry.ResourceJobName != null)
                        {
                            string resourceReason = null;
                            IResourceJobPlatform resourcePlatform = ResourceJobPlatform;
                            bool onlyDisk = entry.ResourceJobOnlyIo;
                            if (entry.ResourceJobRestoringProcess)
                            {
                                if (!FinishResourceJobProcessRestore(restoreHandle, entry, resourceCheckpoint, null))
                                {
                                    unsettledLines.Add(UpdatedJournalLine(allLines[i], entry)); continue;
                                }
                                onlyDisk = true;
                            }
                            if (resourcePlatform == null || !ClearResourceJobByName(resourcePlatform, entry, pid, out resourceReason))
                            {
                                Logger.Warn(Logger.Format("后台资源 Job 崩溃恢复未完成：进程={0} 原因={1}；保留恢复记录", "Background resource Job crash restore incomplete: process={0} reason={1}; restore records kept", entry.Name, (resourcePlatform == null ? "platform" : resourceReason)));
                                if (entry.ResourceJobRestoringProcess)
                                    FinishResourceJobProcessRestore(restoreHandle, entry, resourceCheckpoint, null);
                                unsettledLines.Add(UpdatedJournalLine(allLines[i], entry)); continue;
                            }
                            entry.ResourceJobName = null;
                            if (onlyDisk) { processedCount++; continue; }
                        }
                        if (!OwnsAntiCheatResources(entry) && EntrySnapshotMatches(restoreHandle, entry))
                        {
                            processedCount++;
                            continue;
                        }
                        if (RestoreBackgroundPlacement(restoreHandle, entry) && (OwnsAntiCheatResources(entry) ? RestoreAntiCheatResources(restoreHandle, entry, resourceCheckpoint)
                            : HasAntiCheatResources(entry) ? RestoreLegacyAntiCheatResources(restoreHandle, entry, resourceCheckpoint)
                            : RestoreOrdinaryEntryValues(restoreHandle, entry, CpuTopology.AllMask, resourceCheckpoint)))
                        {
                            processedCount++;
                        }
                        else
                        {
                            unsettledLines.Add(UpdatedJournalLine(allLines[i], entry));
                        }
                    }
                    catch (Exception error)
                    {
                        unsettledLines.Add(UpdatedJournalLine(allLines[i], entry));
                        Logger.LogFailure(Lang.T("log.suppressioncorerecovery.5") + pid, error);
                    }
                    finally
                    {
                        Native.CloseHandle(restoreHandle);
                    }
                }
                if (unsettledLines.Count == 1)
                {
                    File.Delete(statePath);
                }
                else
                {
                    AtomicFile.WriteLines(statePath, unsettledLines.ToArray(), Lang.T("t.suppressioncorerecovery.6"));
                }
            }
            catch (Exception error2)
            {
                Logger.LogFailure(Lang.T("log.suppressioncorerecovery.2"), error2);
            }
            return processedCount;
        }

        private static string B64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string Un64(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return null;
            }
        }

    }
}
