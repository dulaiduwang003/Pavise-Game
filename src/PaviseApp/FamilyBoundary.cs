using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class FamilyBoundary
    {
        private sealed class CatalogVerdict
        {
            public long Creation;

            public string Name;

            public string Path;

            public int VendorGeneration;

            public int SecurityGeneration;

            public int PlatformMask;

            public bool Protected;
        }

        private static readonly object catalogSync = new object();

        private static readonly Dictionary<int, CatalogVerdict> catalogVerdicts = new Dictionary<int, CatalogVerdict>();

        private static readonly Environment.SpecialFolder[] UnsafeRoots = new Environment.SpecialFolder[12]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData
        };

        internal static bool BasicBackgroundEligible(int pid, int self, string name, string path, int session, int ownerSession, int foreground, bool userFacingFamily, string windowsRoot, bool gameHostAncestor = false, string activeGameRoot = null, bool aggressive = false, bool familyExempt = true, long creation = 0L, bool platformRemoved = false, bool allowForeground = false)
        {
            if (CatalogProtected(pid, creation, name, path))
            {
                return false;
            }
            if (!platformRemoved && gameHostAncestor)
            {
                return false;
            }
            if (!platformRemoved && UnderRoot(path, activeGameRoot))
            {
                return false;
            }
            if (pid <= 4 || pid == self || session < 0 || session != ownerSession)
            {
                return false;
            }
            if (!allowForeground && pid == foreground)
            {
                return false;
            }
            if (!aggressive && !platformRemoved && userFacingFamily)
            {
                return false;
            }
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
            {
                return false;
            }
            if (aggressive)
            {
                return !SystemProcessCatalog.IsCoreSystemProcess(name, path, windowsRoot);
            }
            if (!string.IsNullOrEmpty(windowsRoot))
            {
                return !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        internal static bool CatalogProtected(int pid, long creation, string name, string path)
        {
            int currentGenerationResult = PeripheralVendorProbe.CurrentGeneration();
            int currentGenerationResult2 = SecurityProductCatalog.CurrentGeneration();
            int mask = PlatformRuntimePolicy.Mask;
            if (creation > 0)
            {
                lock (catalogSync)
                {
                    CatalogVerdict catalogVerdict;
                    if (catalogVerdicts.TryGetValue(pid, out catalogVerdict) && catalogVerdict.Creation == creation && catalogVerdict.VendorGeneration == currentGenerationResult && catalogVerdict.SecurityGeneration == currentGenerationResult2 && catalogVerdict.PlatformMask == mask && string.Equals(catalogVerdict.Name, name, StringComparison.Ordinal) && string.Equals(catalogVerdict.Path, path, StringComparison.Ordinal))
                    {
                        return catalogVerdict.Protected;
                    }
                }
            }
            bool protectedRuntime = AntiCheatCatalog.IsAntiCheatProcess(name, path) || WeGameRuntimeProtection.IsCriticalRuntime(name, path) || NetAcceleratorCatalog.IsAcceleratorLikeName(name) || NetAcceleratorCatalog.IsAcceleratorLikePath(path) || SecurityProductCatalog.IsSecurityProduct(path) || PeripheralCatalog.IsInputChainProcess(name, path) || HardwareControlCatalog.IsHardwareControlProcess(name) || StreamingToolCatalog.IsStreamingToolProcess(name, path);
            if (creation > 0)
            {
                lock (catalogSync)
                {
                    if (catalogVerdicts.Count >= 8192)
                    {
                        catalogVerdicts.Clear();
                    }
                    catalogVerdicts[pid] = new CatalogVerdict
                    {
                        Creation = creation,
                        Name = name,
                        Path = path,
                        VendorGeneration = currentGenerationResult,
                        SecurityGeneration = currentGenerationResult2,
                        PlatformMask = mask,
                        Protected = protectedRuntime
                    };
                }
            }
            return protectedRuntime;
        }

        internal static void PruneCatalogVerdicts(HashSet<int> live)
        {
            if (live == null)
            {
                return;
            }
            lock (catalogSync)
            {
                if (catalogVerdicts.Count == 0)
                {
                    return;
                }
                List<int> keys = null;
                foreach (int key in catalogVerdicts.Keys)
                {
                    if (!live.Contains(key))
                    {
                        if (keys == null)
                        {
                            keys = new List<int>();
                        }
                        keys.Add(key);
                    }
                }
                if (keys == null)
                {
                    return;
                }
                foreach (int currentKey in keys)
                {
                    catalogVerdicts.Remove(currentKey);
                }
            }
        }

        internal static bool UnderRoot(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
            {
                return false;
            }
            int rootCount = root.Length;
            while (rootCount > 0 && root[rootCount - 1] == '\\')
            {
                rootCount--;
            }
            if (rootCount == 0 || path.Length <= rootCount)
            {
                return false;
            }
            if (path[rootCount] != '\\')
            {
                return false;
            }
            return string.Compare(path, 0, root, 0, rootCount, StringComparison.OrdinalIgnoreCase) == 0;
        }

        internal static string LibraryRootOf(string path, IList<string> roots)
        {
            if (roots == null)
            {
                return null;
            }
            foreach (string root in roots)
            {
                if (root != null && root.TrimEnd('\\').Length > 2 && UnderRoot(path, root))
                {
                    return root;
                }
            }
            return null;
        }

        internal static bool FamilyExemptFor(GameProfile profile)
        {
            if (profile != null)
            {
                return !profile.SuppressFamilyBackground;
            }
            return true;
        }

        internal static bool IsRequiredGameLaunchHost(string name, string path)
        {
            string normalized = WhitelistRule.NormalizeName(name);
            // 这些后端会创建游戏进程并承担会话和网络工作；其界面渲染进程仍可独立纳入家族治理。

            if (!string.Equals(normalized, "LeagueClient", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, "RiotClientServices", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(path)) return true;
            string image = WhitelistRule.NormalizeImagePath(path);
            return image.Length > 0 && string.Equals(normalized, WhitelistRule.NormalizeName(Path.GetFileName(image)), StringComparison.OrdinalIgnoreCase);
        }

        internal static HashSet<int> CollectGameCreationHosts(ProcessSnapshot snapshot, ICollection<int> games, int selfPid, int ownerSession)
        {
            var result = new HashSet<int>();
            if (snapshot == null || games == null || ownerSession < 0) return result;
            var processes = new Dictionary<int, ProcEntry>();
            var duplicates = new HashSet<int>();
            foreach (ProcEntry process in snapshot.Entries)
            {
                if (process == null || duplicates.Contains(process.Pid)) continue;
                if (processes.ContainsKey(process.Pid)) { processes.Remove(process.Pid); duplicates.Add(process.Pid); }
                else processes.Add(process.Pid, process);
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (int game in games)
            {
                ProcEntry child;
                if (!processes.TryGetValue(game, out child) || child.Session != ownerSession || child.Creation <= 0) continue;
                var visited = new HashSet<int>();
                for (int depth = 0; depth < 24 && visited.Add(child.Pid); depth++)
                {
                    if (SystemProcessCatalog.IsShellProcess(child.Name) || SystemProcessCatalog.IsCoreSystemProcess(child.Name, child.Path, windows)) break;
                    ProcEntry parent;
                    if (child.ParentPid <= 4 || child.ParentPid == selfPid || !processes.TryGetValue(child.ParentPid, out parent)
                        || parent.Session != ownerSession || parent.Creation <= 0 || parent.Creation > child.Creation || visited.Contains(parent.Pid)) break;
                    if (SystemProcessCatalog.IsShellProcess(parent.Name) || SystemProcessCatalog.IsCoreSystemProcess(parent.Name, parent.Path, windows)) break;
                    result.Add(parent.Pid);
                    child = parent;
                }
            }
            return result;
        }

        internal static HashSet<int> CollectProtectedLibraryFamily(IList<GameProfile> configured, ProcessSnapshot snapshot, int selfPid, int ownerSession, GameFamilyEvidence familyEvidence = null, bool exactExecutablesOnly = false, bool forceFamilyProtection = false)
        {
            HashSet<int> protectedPids = new HashSet<int>();
            if (configured == null || snapshot == null || ownerSession < 0)
            {
                return protectedPids;
            }
            List<string> roots = new List<string>();
            HashSet<string> executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> legacyGameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile currentConfigured in configured)
            {
                if (currentConfigured != null && (exactExecutablesOnly || forceFamilyProtection || FamilyExemptFor(currentConfigured)))
                {
                    string executable = WhitelistRule.NormalizeImagePath(currentConfigured.ExecutablePath);
                    string learned = WhitelistRule.NormalizeImagePath(currentConfigured.LearnedExecutablePath);
                    if (executable.Length > 0)
                    {
                        executables.Add(executable);
                    }
                    if (learned.Length > 0)
                    {
                        executables.Add(learned);
                    }
                    if (exactExecutablesOnly && forceFamilyProtection && executable.Length == 0 && learned.Length == 0)
                        foreach (string entry in currentConfigured.Entries)
                        {
                            string gameName = WhitelistRule.NormalizeName(entry);
                            if (gameName.Length > 0) legacyGameNames.Add(gameName);
                        }
                    if (!exactExecutablesOnly && SafeFamilyDir(currentConfigured.Root))
                    {
                        roots.Add(currentConfigured.Root);
                    }
                }
            }
            if (executables.Count == 0 && roots.Count == 0 && legacyGameNames.Count == 0)
            {
                return protectedPids;
            }
            Dictionary<int, int> pidsByPid = exactExecutablesOnly ? null : new Dictionary<int, int>();
            HashSet<int> pids = new HashSet<int>();
            foreach (ProcEntry procEntry in snapshot.Entries)
            {
                if (procEntry.Pid <= 4 || procEntry.Pid == selfPid || procEntry.Session != ownerSession || procEntry.Creation <= 0)
                {
                    continue;
                }
                if ((!string.IsNullOrEmpty(procEntry.Path) && (executables.Contains(WhitelistRule.NormalizeImagePath(procEntry.Path)) || LibraryRootOf(procEntry.Path, roots) != null))
                    || legacyGameNames.Contains(WhitelistRule.NormalizeName(procEntry.Name)))
                {
                    pids.Add(procEntry.Pid);
                }
                if (!exactExecutablesOnly && familyEvidence != null)
                {
                    foreach (GameProfile candidateConfigured in configured)
                    {
                        if (candidateConfigured != null && (forceFamilyProtection || FamilyExemptFor(candidateConfigured)) && familyEvidence.Contains(candidateConfigured, procEntry.Pid, procEntry.Creation, procEntry.Path))
                        {
                            pids.Add(procEntry.Pid);
                            break;
                        }
                    }
                }
                if (!exactExecutablesOnly)
                {
                    ProcEntry processEntry = snapshot.Find(procEntry.ParentPid);
                    if (processEntry != null && processEntry.Pid > 4 && processEntry.Pid != selfPid && processEntry.Pid != procEntry.Pid && processEntry.Session == ownerSession && processEntry.Creation > 0 && processEntry.Creation <= procEntry.Creation)
                    {
                        pidsByPid[procEntry.Pid] = processEntry.Pid;
                    }
                }
            }
            protectedPids.UnionWith(pids);
            if (exactExecutablesOnly)
            {
                return protectedPids;
            }
            protectedPids.UnionWith(WalkDescendants(pidsByPid, pids, selfPid, 24));
            foreach (int pid in pids)
            {
                protectedPids.UnionWith(WalkAncestorChain(pidsByPid, pid, selfPid, 24));
            }
            return protectedPids;
        }

        internal static HashSet<int> ExpandUserFacingFamily(Dictionary<int, int> parents, Dictionary<int, string> names, HashSet<int> roots)
        {
            HashSet<int> familyPids = new HashSet<int>(roots ?? new HashSet<int>());
            HashSet<string> textsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names != null)
            {
                foreach (int familyPid in familyPids)
                {
                    string currentName;
                    if (names.TryGetValue(familyPid, out currentName) && !string.IsNullOrEmpty(currentName))
                    {
                        textsSet.Add(currentName);
                    }
                }
            }
            if (names != null)
            {
                foreach (KeyValuePair<int, string> name in names)
                {
                    if (textsSet.Contains(name.Value))
                    {
                        familyPids.Add(name.Key);
                    }
                }
            }
            bool membersAdded;
            do
            {
                membersAdded = false;
                if (parents == null || names == null)
                {
                    break;
                }
                foreach (KeyValuePair<int, int> parent in parents)
                {
                    string candidateName;
                    if (!familyPids.Contains(parent.Key) && familyPids.Contains(parent.Value) && names.TryGetValue(parent.Value, out candidateName) && !SystemProcessCatalog.IsShellProcess(candidateName))
                    {
                        familyPids.Add(parent.Key);
                        membersAdded = true;
                    }
                }
            }
            while (membersAdded);
            return familyPids;
        }

        internal static void FilterUserFacingGameFamily(HashSet<int> userFacingFamily, GameProfile profile, ProcessSnapshot snapshot, int rendererPid, int selfPid, int ownerSession, ICollection<int> gamePids, ICollection<int> gameDescendants, ICollection<int> gameHostAncestors, GameFamilyEvidence familyEvidence = null)
        {
            if (userFacingFamily == null || userFacingFamily.Count == 0 || rendererPid <= 0 || FamilyExemptFor(profile))
            {
                return;
            }
            userFacingFamily.Remove(rendererPid);
            if (gameDescendants != null)
            {
                userFacingFamily.ExceptWith(gameDescendants);
            }
            if (gameHostAncestors != null)
            {
                userFacingFamily.ExceptWith(gameHostAncestors);
            }
            if (gamePids != null)
            {
                userFacingFamily.ExceptWith(gamePids);
            }
            if (userFacingFamily.Count == 0 || snapshot == null || ownerSession < 0)
            {
                return;
            }
            Dictionary<int, ProcEntry> processEntriesById = new Dictionary<int, ProcEntry>();
            HashSet<int> pids = new HashSet<int>();
            foreach (ProcEntry procEntry in snapshot.Entries)
            {
                if (procEntry != null)
                {
                    if (!pids.Add(procEntry.Pid))
                    {
                        processEntriesById.Remove(procEntry.Pid);
                    }
                    else if (procEntry.Pid > 4 && procEntry.Pid != selfPid && procEntry.Session == ownerSession && procEntry.Creation > 0 && !string.IsNullOrEmpty(procEntry.Name) && GameFamilyHistory.CanonicalPath(procEntry.Path) && string.Equals(procEntry.Name, Path.GetFileNameWithoutExtension(procEntry.Path), StringComparison.OrdinalIgnoreCase))
                    {
                        processEntriesById.Add(procEntry.Pid, procEntry);
                    }
                }
            }
            bool safeFamilyDirSucceeded = SafeFamilyDir(profile.Root);
            HashSet<int> currentPids = new HashSet<int>();
            Dictionary<int, int> parentByPid = new Dictionary<int, int>();
            foreach (ProcEntry processInfo in processEntriesById.Values)
            {
                if (string.Equals(processInfo.Path, profile.ExecutablePath, StringComparison.OrdinalIgnoreCase) || string.Equals(processInfo.Path, profile.LearnedExecutablePath, StringComparison.OrdinalIgnoreCase) || (safeFamilyDirSucceeded && UnderRoot(processInfo.Path, profile.Root)) || (familyEvidence != null && familyEvidence.Contains(profile, processInfo.Pid, processInfo.Creation, processInfo.Path)))
                {
                    currentPids.Add(processInfo.Pid);
                }
                ProcEntry processEntry;
                if (processInfo.ParentPid != processInfo.Pid && processEntriesById.TryGetValue(processInfo.ParentPid, out processEntry) && processEntry.Creation <= processInfo.Creation)
                {
                    parentByPid.Add(processInfo.Pid, processEntry.Pid);
                }
            }
            if (currentPids.Count != 0)
            {
                userFacingFamily.ExceptWith(currentPids);
                userFacingFamily.ExceptWith(WalkDescendants(parentByPid, currentPids, selfPid, 24));
            }
        }

        internal static bool IsGameOrWhitelistProtected(int pid, int rendererPid, bool whitelisted, bool protectedLibraryMember, bool familyExempt, HashSet<int> gamePids, HashSet<int> gameDescendants, bool platformRemoved = false)
        {
            if (!whitelisted && (rendererPid <= 0 || pid != rendererPid))
            {
                if (!platformRemoved)
                {
                    if (!protectedLibraryMember)
                    {
                        if (familyExempt)
                        {
                            if (gamePids == null || !gamePids.Contains(pid))
                            {
                                if (gameDescendants != null)
                                {
                                    return gameDescendants.Contains(pid);
                                }
                                return false;
                            }
                            return true;
                        }
                        return false;
                    }
                    return true;
                }
                return false;
            }
            return true;
        }

        internal static bool SafeFamilyDir(string dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }
            string dirTrimmed = dir.TrimEnd('\\');
            if (dirTrimmed.Length <= 2)
            {
                return false;
            }
            foreach (Environment.SpecialFolder folder in UnsafeRoots)
            {
                string folderPath;
                try
                {
                    folderPath = Environment.GetFolderPath(folder);
                }
                catch
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(folderPath) && string.Equals(dirTrimmed, folderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool IsGameFamily(string path, HashSet<string> gameDirs)
        {
            return IsGameFamily(path, gameDirs, null);
        }

        internal static bool IsGameFamily(string path, HashSet<string> gameDirs, string processName)
        {
            if (string.IsNullOrEmpty(path) || gameDirs.Count == 0)
            {
                return false;
            }
            if (GameSessionDetector.IsNonGameRole(processName, path))
            {
                return false;
            }
            foreach (string gameDir in gameDirs)
            {
                if (UnderRoot(path, gameDir))
                {
                    return true;
                }
            }
            return false;
        }

        internal static HashSet<int> WalkDescendants(Dictionary<int, int> parents, ICollection<int> rootPids, int selfPid, int maxDepth)
        {
            return WalkDescendants(parents, rootPids, selfPid, maxDepth, null);
        }

        internal static HashSet<int> WalkDescendants(Dictionary<int, int> parents, ICollection<int> rootPids, int selfPid, int maxDepth, Dictionary<int, long> creations)
        {
            HashSet<int> keys = new HashSet<int>();
            if (parents == null || rootPids == null || rootPids.Count == 0)
            {
                return keys;
            }
            HashSet<int> rootPidsSnapshot = new HashSet<int>(rootPids);
            foreach (KeyValuePair<int, int> parent in parents)
            {
                int key = parent.Key;
                if (key <= 4 || key == selfPid || rootPidsSnapshot.Contains(key) || keys.Contains(key))
                {
                    continue;
                }
                int keySnapshot = key;
                for (int i = 0; i < maxDepth; i++)
                {
                    int currentParent;
                    if (!parents.TryGetValue(keySnapshot, out currentParent))
                    {
                        break;
                    }
                    if (currentParent <= 4)
                    {
                        break;
                    }
                    if (currentParent == keySnapshot)
                    {
                        break;
                    }
                    if (!ParentNotNewer(creations, currentParent, keySnapshot))
                    {
                        break;
                    }
                    if (rootPidsSnapshot.Contains(currentParent))
                    {
                        keys.Add(key);
                        break;
                    }
                    if (currentParent == selfPid)
                    {
                        break;
                    }
                    keySnapshot = currentParent;
                }
            }
            return keys;
        }

        internal static HashSet<int> WalkAncestorChain(Dictionary<int, int> parents, int startPid, int selfPid, int maxHops)
        {
            return WalkAncestorChain(parents, startPid, selfPid, maxHops, null);
        }

        internal static HashSet<int> WalkAncestorChain(Dictionary<int, int> parents, int startPid, int selfPid, int maxHops, Dictionary<int, long> creations)
        {
            HashSet<int> visitedPids = new HashSet<int>();
            if (parents == null || startPid <= 4)
            {
                return visitedPids;
            }
            int startPidSnapshot = startPid;
            for (int i = 0; i < maxHops; i++)
            {
                int parent;
                if (!parents.TryGetValue(startPidSnapshot, out parent))
                {
                    break;
                }
                if (parent <= 4)
                {
                    break;
                }
                if (parent == selfPid)
                {
                    break;
                }
                if (parent == startPid)
                {
                    break;
                }
                if (visitedPids.Contains(parent))
                {
                    break;
                }
                if (!ParentNotNewer(creations, parent, startPidSnapshot))
                {
                    break;
                }
                visitedPids.Add(parent);
                startPidSnapshot = parent;
            }
            return visitedPids;
        }

        private static bool ParentNotNewer(Dictionary<int, long> creations, int parentPid, int childPid)
        {
            if (creations == null)
            {
                return true;
            }
            long value;
            long creation;
            if (!creations.TryGetValue(parentPid, out value) || !creations.TryGetValue(childPid, out creation))
            {
                return true;
            }
            if (value <= 0 || creation <= 0)
            {
                return true;
            }
            return value <= creation;
        }
    }
}
