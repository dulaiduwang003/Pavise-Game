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
            return SaveJournalLocked();
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
                        SaveJournalLocked();
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

        private bool SaveJournalLocked(bool durable = false)
        {
            if (string.IsNullOrEmpty(journalPath))
            {
                MarkJournaledLocked();
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
                        if (entry.BackgroundJobPlacement || entry.AntiCheatPlacementExternal || entry.AutomaticLayoutSets != null || entry.AutomaticLayoutYielded)
                        {
                            durable = true;
                        }
                        lines.Add(item.Key + "|" + entry.Creation + "|" + B64(entry.Name) + "|" + entry.OrigPri + "|" + entry.OrigAff + "|" + entry.OrigIo + "|" + entry.OrigPg + "|" + CpuSetsText(entry.OrigCpuSets) + "|" + entry.OrigQoSControl + "|" + entry.OrigQoSState + "|" + entry.OrigGpu + "|" + entry.OrigBoost + (entry.AntiCheatPlacementExternal ? "|ac-placement|1|0" : ((entry.AutomaticLayoutSets != null || entry.AutomaticLayoutYielded) ? ("|layout|" + CpuSetsText(entry.AutomaticLayoutSets) + "|" + (entry.AutomaticLayoutYielded ? "1" : "0")) : (entry.BackgroundPlacementOwned ? ("|" + (entry.BackgroundJobPlacement ? "bg-job" : "bg") + "|" + entry.BackgroundApplied + "|" + entry.BackgroundPrevious) : ""))));
                    }
                }
                if (lines.Count == 1)
                {
                    if (File.Exists(journalPath))
                    {
                        File.Delete(journalPath);
                    }
                    MarkJournaledLocked();
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
                if (File.Exists(journalPath))
                {
                    File.Replace(journalPathOffset, journalPath, null);
                }
                else
                {
                    File.Move(journalPathOffset, journalPath);
                }
                MarkJournaledLocked();
                return true;
            }
            catch (Exception error)
            {
                Logger.LogFailure(Lang.T("log.suppressioncorerecovery.1", 0), error);
                return false;
            }
        }

        private void MarkJournaledLocked()
        {
            backgroundJobJournalPending = false;
            backgroundJobJournalFailures.Clear();
            foreach (Entry entry in map.Values)
            {
                if (entry.OrigPri != uint.MaxValue)
                {
                    entry.Journaled = true;
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
                Logger.LogFailure(Lang.T("log.suppressioncorerecovery.2", 0), error);
            }
        }

        private static Entry ParseJournalLine(string line, out int pid)
        {
            pid = 0;
            string[] lineParts = line.Split('|');
            long creation;
            uint originalPriority;
            ulong originalAffinity;
            int originalIoPriority;
            int originalPagePriority;
            if ((lineParts.Length != 7 && lineParts.Length != 8 && lineParts.Length != 10 && lineParts.Length != 11 && lineParts.Length != 12 && lineParts.Length != 15) || !int.TryParse(lineParts[0], out pid) || !long.TryParse(lineParts[1], out creation) || !uint.TryParse(lineParts[3], out originalPriority) || !ulong.TryParse(lineParts[4], out originalAffinity) || !int.TryParse(lineParts[5], out originalIoPriority) || !int.TryParse(lineParts[6], out originalPagePriority))
            {
                return null;
            }
            uint[] origCpuSetsValue = ((lineParts.Length >= 8) ? ParseCpuSets(lineParts[7]) : new uint[0]);
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
                automaticLayoutSetsValue = ParseCpuSets(lineParts[13]);
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
            entry.OrigAff = originalAffinity;
            entry.OrigIo = originalIoPriority;
            entry.OrigPg = originalPagePriority;
            entry.OrigCpuSets = origCpuSetsValue;
            entry.OrigQoSControl = originalQosControl;
            entry.OrigQoSState = originalQosState;
            entry.OrigGpu = originalGpuPriority;
            entry.OrigBoost = originalBoost;
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
                        Logger.Log(Lang.T("log.suppressioncorerecovery.3", 0) + (i + 1) + Lang.T("log.suppressioncorerecovery.4", 0));
                        continue;
                    }
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
                            unsettledLines.Add(allLines[i]);
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
                        if (entry.BackgroundJobPlacement && !ReleaseBackgroundJob(entry))
                        {
                            unsettledLines.Add(allLines[i]); continue;
                        }
                        if (EntrySnapshotMatches(restoreHandle, entry))
                        {
                            processedCount++;
                        }
                        else if (RestoreBackgroundPlacement(restoreHandle, entry) && RestoreValues(restoreHandle, entry.OrigPri, entry.OrigAff, entry.OrigIo, entry.OrigPg, CpuTopology.AllMask, entry.OrigCpuSets, entry.OrigQoSControl, entry.OrigQoSState, entry.OrigGpu, entry.OrigBoost, RestorePlacement(entry)))
                        {
                            processedCount++;
                        }
                        else
                        {
                            unsettledLines.Add(allLines[i]);
                        }
                    }
                    catch (Exception error)
                    {
                        unsettledLines.Add(allLines[i]);
                        Logger.LogFailure(Lang.T("log.suppressioncorerecovery.5", 0) + pid, error);
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
                Logger.LogFailure(Lang.T("log.suppressioncorerecovery.2", 0), error2);
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

        private static string CpuSetsText(uint[] ids)
        {
            if (ids == null || ids.Length == 0)
            {
                return "";
            }
            string[] textEntries = new string[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                textEntries[i] = ids[i].ToString();
            }
            return string.Join(",", textEntries);
        }

        private static uint[] ParseCpuSets(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new uint[0];
            }
            string[] parts = text.Split(',');
            uint[] numericValueItems = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!uint.TryParse(parts[i], out numericValueItems[i]))
                {
                    return null;
                }
            }
            return numericValueItems;
        }

    }
}
