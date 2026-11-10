using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class PlatformRuntimePolicy
    {
        internal const string RemovedKey = "PlatformRuntimeRemovedV1";

        internal const string RulePrefix = "platform:";

        internal static readonly string[] Ids = new string[1] { "steam" };

        private static readonly object gate = new object();

        private static int generation = -1;

        private static int removed;

        private static readonly HashSet<string> SteamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steam", "steamwebhelper", "gameoverlayui" };

        private static readonly HashSet<string> Helpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser", "crashpad_handler", "msedgewebview2", "QtWebEngineProcess", "cef_subprocess" };

        internal static int Mask
        {
            get
            {
                lock (gate)
                {
                    int mutationGeneration = Settings.MutationGeneration;
                    if (generation == mutationGeneration)
                    {
                        return removed;
                    }
                    removed = 0;
                    foreach (string id in Settings.LoadStr("PlatformRuntimeRemovedV1", "").Split(';'))
                    {
                        removed |= Bit(id);
                    }
                    generation = mutationGeneration;
                    return removed;
                }
            }
        }

        private static int Bit(string id)
        {
            // 保留 Steam 既有配置位，忽略旧版 WeGame 移除设置。
            if (string.Equals(id, "steam", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            return 0;
        }

        internal static bool Enabled(string id)
        {
            int bitResult = Bit(id);
            if (bitResult != 0)
            {
                return (Mask & bitResult) == 0;
            }
            return false;
        }

        internal static string DisplayName(string id)
        {
            if (id != "wegame")
            {
                return "Steam";
            }
            return "WeGame";
        }

        internal static string RuntimePlatform(string name)
        {
            // 平台分类阻止家族继承与渲染进程选择；WeGame 没有内置保护。
            if (WeGameRuntimeProtection.IsRuntimeName(name))
            {
                return "wegame";
            }
            if (!SteamNames.Contains(WhitelistRule.NormalizeName(name)))
            {
                return null;
            }
            return "steam";
        }

        internal static bool TryRemove(string id)
        {
            lock (gate)
            {
                int bitResult = Bit(id);
                if (bitResult == 0)
                {
                    return false;
                }
                return Save(Mask | bitResult);
            }
        }

        internal static bool TryRestore()
        {
            lock (gate)
            {
                return Save(0);
            }
        }

        private static bool Save(int mask)
        {
            List<string> entryTexts = new List<string>();
            foreach (string entryText in Ids)
            {
                if ((mask & Bit(entryText)) != 0)
                {
                    entryTexts.Add(entryText);
                }
            }
            return Settings.SaveStr("PlatformRuntimeRemovedV1", string.Join(";", entryTexts.ToArray()));
        }

        internal static string ProcessNames(string id)
        {
            if (id != "wegame")
            {
                return "Steam · SteamWebHelper · GameOverlayUI";
            }
            return "WeGame · WeGame_env · WeGameClient · Pallas · Rail · TCLS_core";
        }

        internal static Dictionary<int, PlatformProcessOwner> Collect(ProcessSnapshot snapshot, int self, int session, HashSet<int> gameRoots, Dictionary<int, PlatformProcessOwner> retained)
        {
            Dictionary<int, PlatformProcessOwner> ownersByPid = new Dictionary<int, PlatformProcessOwner>();
            if (snapshot == null || session < 0)
            {
                return ownersByPid;
            }
            Dictionary<int, ProcEntry> procEntriesByPid = new Dictionary<int, ProcEntry>();
            HashSet<int> pids = new HashSet<int>();
            Dictionary<string, string> runtimePlatformResultsByDirectoryName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcEntry procEntry in snapshot.Entries)
            {
                if (procEntry == null)
                {
                    continue;
                }
                if (!pids.Add(procEntry.Pid))
                {
                    procEntriesByPid.Remove(procEntry.Pid);
                }
                else if (procEntry.Pid > 4 && procEntry.Pid != self && procEntry.Session == session && procEntry.Creation > 0)
                {
                    string imagePath = WhitelistRule.NormalizeImagePath(procEntry.Path);
                    if (imagePath.Length != 0 && string.Equals(WhitelistRule.NormalizeName(procEntry.Name), WhitelistRule.NormalizeName(Path.GetFileName(imagePath)), StringComparison.OrdinalIgnoreCase))
                    {
                        procEntriesByPid[procEntry.Pid] = procEntry;
                    }
                }
            }
            HashSet<int> currentPids = new HashSet<int>(gameRoots ?? new HashSet<int>());
            for (int j = 0; j < 24; j++)
            {
                bool gameDescendantsAdded = false;
                foreach (ProcEntry processSnapshot in procEntriesByPid.Values)
                {
                    ProcEntry currentProcEntry;
                    if (!currentPids.Contains(processSnapshot.Pid) && currentPids.Contains(processSnapshot.ParentPid) && RuntimePlatform(processSnapshot.Name) == null && procEntriesByPid.TryGetValue(processSnapshot.ParentPid, out currentProcEntry) && currentProcEntry.Creation < processSnapshot.Creation)
                    {
                        currentPids.Add(processSnapshot.Pid);
                        gameDescendantsAdded = true;
                    }
                }
                if (!gameDescendantsAdded)
                {
                    break;
                }
            }
            foreach (int currentPid in currentPids)
            {
                procEntriesByPid.Remove(currentPid);
            }
            foreach (ProcEntry platformProcess in procEntriesByPid.Values)
            {
                string runtimePlatformResult = RuntimePlatform(platformProcess.Name);
                if (runtimePlatformResult != null)
                {
                    ownersByPid[platformProcess.Pid] = Owner(platformProcess, runtimePlatformResult);
                    string directoryName = Path.GetDirectoryName(WhitelistRule.NormalizeImagePath(platformProcess.Path));
                    if (FamilyBoundary.SafeFamilyDir(directoryName) && (runtimePlatformResult != "steam" || string.Equals(WhitelistRule.NormalizeName(platformProcess.Name), "steam", StringComparison.OrdinalIgnoreCase)))
                    {
                        runtimePlatformResultsByDirectoryName[directoryName] = runtimePlatformResult;
                    }
                }
                PlatformProcessOwner currentRetained;
                if (retained != null && retained.TryGetValue(platformProcess.Pid, out currentRetained) && currentRetained.Creation == platformProcess.Creation && string.Equals(currentRetained.Name, platformProcess.Name, StringComparison.OrdinalIgnoreCase) && WhitelistRule.PathEquals(currentRetained.Path, platformProcess.Path))
                {
                    ownersByPid[platformProcess.Pid] = currentRetained;
                }
            }
            foreach (ProcEntry helperProcess in procEntriesByPid.Values)
            {
                if (ownersByPid.ContainsKey(helperProcess.Pid) || !Helpers.Contains(WhitelistRule.NormalizeName(helperProcess.Name)))
                {
                    continue;
                }
                foreach (KeyValuePair<string, string> runtimePlatformResultEntry in runtimePlatformResultsByDirectoryName)
                {
                    if (FamilyBoundary.UnderRoot(WhitelistRule.NormalizeImagePath(helperProcess.Path), runtimePlatformResultEntry.Key))
                    {
                        ownersByPid[helperProcess.Pid] = Owner(helperProcess, runtimePlatformResultEntry.Value);
                        break;
                    }
                }
            }
            for (int k = 0; k < 24; k++)
            {
                bool platformDescendantsAdded = false;
                foreach (ProcEntry childProcess in procEntriesByPid.Values)
                {
                    PlatformProcessOwner owner;
                    ProcEntry candidateProcEntry;
                    if (!ownersByPid.ContainsKey(childProcess.Pid) && ownersByPid.TryGetValue(childProcess.ParentPid, out owner) && procEntriesByPid.TryGetValue(childProcess.ParentPid, out candidateProcEntry) && candidateProcEntry.Creation < childProcess.Creation)
                    {
                        ownersByPid[childProcess.Pid] = Owner(childProcess, owner.Platform);
                        platformDescendantsAdded = true;
                    }
                }
                if (!platformDescendantsAdded)
                {
                    break;
                }
            }
            return ownersByPid;
        }

        private static PlatformProcessOwner Owner(ProcEntry p, string id)
        {
            PlatformProcessOwner platformProcessOwner = new PlatformProcessOwner();
            platformProcessOwner.Platform = id;
            platformProcessOwner.Creation = p.Creation;
            platformProcessOwner.Name = p.Name;
            platformProcessOwner.Path = p.Path;
            return platformProcessOwner;
        }
    }
}
