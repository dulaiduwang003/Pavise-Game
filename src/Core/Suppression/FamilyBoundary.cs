// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class FamilyBoundary
    {
        internal static bool BasicBackgroundEligible(int pid, int self, string name, string path,
            int session, int ownerSession, int foreground, bool userFacingFamily, string windowsRoot,
            bool gameHostAncestor = false, string activeGameRoot = null, bool aggressive = false,
            bool familyExempt = true, long creation = 0, bool platformRemoved = false)
        {
            if (CatalogProtected(pid, creation, name, path)) return false;
            if (!platformRemoved && gameHostAncestor) return false;
            if (!platformRemoved && UnderRoot(path, activeGameRoot)) return false;
            if (pid <= 4 || pid == self || session < 0 || session != ownerSession) return false;

            if (pid == foreground && SystemProcessCatalog.RetiredInteractiveName(name)
                && GameMode.IsTrustedPresetProcessPath(name, path)) return false;
            if (!aggressive && (pid == foreground || (!platformRemoved && userFacingFamily))) return false;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return false;
            if (aggressive) return !SystemProcessCatalog.IsCoreSystemProcess(name, path, windowsRoot);
            return string.IsNullOrEmpty(windowsRoot) || !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class CatalogVerdict
        {
            public long Creation;
            public string Name;
            public string Path;
            public int VendorGeneration, PlatformMask;
            public bool Protected;
        }

        private static readonly object catalogSync = new object();
        private static readonly Dictionary<int, CatalogVerdict> catalogVerdicts = new Dictionary<int, CatalogVerdict>();
        private const int CatalogVerdictCap = 8192;

        internal static bool CatalogProtected(int pid, long creation, string name, string path)
        {
            int generation = PeripheralVendorProbe.CurrentGeneration();
            int platformMask = PlatformRuntimePolicy.Mask;
            if (creation > 0)
                lock (catalogSync)
                {
                    CatalogVerdict hit;
                    if (catalogVerdicts.TryGetValue(pid, out hit) && hit.Creation == creation
                        && hit.VendorGeneration == generation && hit.PlatformMask == platformMask
                        && string.Equals(hit.Name, name, StringComparison.Ordinal)
                        && string.Equals(hit.Path, path, StringComparison.Ordinal))
                        return hit.Protected;
                }
            bool verdict = AntiCheatCatalog.IsAntiCheatProcess(name, path)
                || WeGameRuntimeProtection.IsCriticalRuntime(name, path)
                || NetAcceleratorCatalog.IsAcceleratorLikeName(name)
                || PeripheralCatalog.IsInputChainProcess(name, path)
                || HardwareControlCatalog.IsHardwareControlProcess(name);
            if (creation > 0)
                lock (catalogSync)
                {
                    if (catalogVerdicts.Count >= CatalogVerdictCap) catalogVerdicts.Clear();
                    catalogVerdicts[pid] = new CatalogVerdict
                    {
                        Creation = creation, Name = name, Path = path, VendorGeneration = generation, PlatformMask = platformMask,
                        Protected = verdict
                    };
                }
            return verdict;
        }

        internal static void PruneCatalogVerdicts(HashSet<int> live)
        {
            if (live == null) return;
            lock (catalogSync)
            {
                if (catalogVerdicts.Count == 0) return;
                List<int> dead = null;
                foreach (int pid in catalogVerdicts.Keys)
                {
                    if (live.Contains(pid)) continue;
                    if (dead == null) dead = new List<int>();
                    dead.Add(pid);
                }
                if (dead != null) foreach (int pid in dead) catalogVerdicts.Remove(pid);
            }
        }

#if PAVISE_SELFTEST
        internal static int CatalogVerdictCountForTest { get { lock (catalogSync) return catalogVerdicts.Count; } }
        internal static void ClearCatalogVerdictsForTest() { lock (catalogSync) catalogVerdicts.Clear(); }
#endif

        internal static bool UnderRoot(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            int len = root.Length;
            while (len > 0 && root[len - 1] == '\\') len--;
            if (len == 0 || path.Length <= len) return false;
            if (path[len] != '\\') return false;
            return string.Compare(path, 0, root, 0, len, StringComparison.OrdinalIgnoreCase) == 0;
        }

        internal static string LibraryRootOf(string path, IList<string> roots)
        {
            if (roots == null) return null;
            foreach (string root in roots)
            {
                if (root == null || root.TrimEnd('\\').Length <= 2) continue;
                if (UnderRoot(path, root)) return root;
            }
            return null;
        }

        internal static bool FamilyExemptFor(GameProfile profile)
        {
            return profile == null || !profile.SuppressFamilyBackground;
        }

        internal static HashSet<int> CollectProtectedLibraryFamily(IList<GameProfile> configured,
            ProcessSnapshot snapshot, int selfPid, int ownerSession, GameFamilyEvidence familyEvidence = null, bool exactExecutablesOnly = false, bool forceFamilyProtection = false)
        {
            var result = new HashSet<int>();
            if (configured == null || snapshot == null || ownerSession < 0) return result;
            var roots = new List<string>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile profile in configured)
            {
                if (profile == null || (!exactExecutablesOnly && !forceFamilyProtection && !FamilyExemptFor(profile))) continue;
                if (!string.IsNullOrEmpty(profile.ExecutablePath)) paths.Add(profile.ExecutablePath);
                if (!string.IsNullOrEmpty(profile.LearnedExecutablePath)) paths.Add(profile.LearnedExecutablePath);
                if (!exactExecutablesOnly && SafeFamilyDir(profile.Root)) roots.Add(profile.Root);
            }
            if (paths.Count == 0 && roots.Count == 0) return result;
            var parents = new Dictionary<int, int>();
            var seeds = new HashSet<int>();
            foreach (ProcEntry child in snapshot.Entries)
            {
                if (child.Pid <= 4 || child.Pid == selfPid || child.Session != ownerSession
                    || child.Creation <= 0) continue;
                if (!string.IsNullOrEmpty(child.Path)
                    && (paths.Contains(child.Path) || LibraryRootOf(child.Path, roots) != null))
                    seeds.Add(child.Pid);
                if (!exactExecutablesOnly && familyEvidence != null)
                    foreach (GameProfile profile in configured)
                        if (profile != null && (forceFamilyProtection || FamilyExemptFor(profile))
                            && familyEvidence.Contains(profile, child.Pid, child.Creation, child.Path))
                        { seeds.Add(child.Pid); break; }
                ProcEntry parent = snapshot.Find(child.ParentPid);
                // 跨目录家族关系必须使用当前创建身份。
                if (parent == null || parent.Pid <= 4 || parent.Pid == selfPid
                    || parent.Pid == child.Pid || parent.Session != ownerSession
                    || parent.Creation <= 0 || parent.Creation > child.Creation) continue;
                parents[child.Pid] = parent.Pid;
            }
            result.UnionWith(seeds);
            if (exactExecutablesOnly) return result;
            result.UnionWith(WalkDescendants(parents, seeds, selfPid, 24));
            foreach (int seed in seeds)
                result.UnionWith(WalkAncestorChain(parents, seed, selfPid, 24));
            // 共享宿主不能作为新家族种子扩大豁免范围。
            return result;
        }

        internal static HashSet<int> ExpandUserFacingFamily(Dictionary<int, int> parents,
            Dictionary<int, string> names, HashSet<int> roots)
        {
            var result = new HashSet<int>(roots ?? new HashSet<int>());
            var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names != null)
                foreach (int pid in result)
                {
                    string name;
                    if (names.TryGetValue(pid, out name) && !string.IsNullOrEmpty(name)) rootNames.Add(name);
                }
            if (names != null)
                foreach (var pair in names)
                    if (rootNames.Contains(pair.Value)) result.Add(pair.Key);

            bool changed;
            do
            {
                changed = false;
                if (parents == null || names == null) break;
                foreach (var pair in parents)
                {
                    if (result.Contains(pair.Key) || !result.Contains(pair.Value)) continue;
                    string parentName;
                    if (!names.TryGetValue(pair.Value, out parentName) || SystemProcessCatalog.IsShellProcess(parentName)) continue;
                    result.Add(pair.Key);
                    changed = true;
                }
            }
            while (changed);
            return result;
        }

        internal static void FilterUserFacingGameFamily(HashSet<int> userFacingFamily,
            GameProfile profile, ProcessSnapshot snapshot, int rendererPid, int selfPid, int ownerSession,
            ICollection<int> gamePids, ICollection<int> gameDescendants, ICollection<int> gameHostAncestors,
            GameFamilyEvidence familyEvidence = null)
        {
            if (userFacingFamily == null || userFacingFamily.Count == 0
                || rendererPid <= 0 || FamilyExemptFor(profile)) return;
            userFacingFamily.Remove(rendererPid);
            if (gameDescendants != null) userFacingFamily.ExceptWith(gameDescendants);
            if (gameHostAncestors != null) userFacingFamily.ExceptWith(gameHostAncestors);
            if (gamePids != null) userFacingFamily.ExceptWith(gamePids);

            if (userFacingFamily.Count == 0 || snapshot == null || ownerSession < 0) return;
            var current = new Dictionary<int, ProcEntry>();
            var seen = new HashSet<int>();
            foreach (ProcEntry process in snapshot.Entries)
            {
                if (process == null) continue;
                if (!seen.Add(process.Pid)) { current.Remove(process.Pid); continue; }
                if (process.Pid <= 4 || process.Pid == selfPid || process.Session != ownerSession
                    || process.Creation <= 0 || string.IsNullOrEmpty(process.Name)
                    || !GameFamilyHistory.CanonicalPath(process.Path)
                    || !string.Equals(process.Name, Path.GetFileNameWithoutExtension(process.Path),
                        StringComparison.OrdinalIgnoreCase)) continue;
                current.Add(process.Pid, process);
            }
            bool safeRoot = SafeFamilyDir(profile.Root);
            var seeds = new HashSet<int>();
            var parents = new Dictionary<int, int>();
            foreach (ProcEntry process in current.Values)
            {
                if (string.Equals(process.Path, profile.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(process.Path, profile.LearnedExecutablePath, StringComparison.OrdinalIgnoreCase)
                    || (safeRoot && UnderRoot(process.Path, profile.Root))
                    || (familyEvidence != null
                        && familyEvidence.Contains(profile, process.Pid, process.Creation, process.Path)))
                    seeds.Add(process.Pid);

                ProcEntry parent;
                if (process.ParentPid != process.Pid && current.TryGetValue(process.ParentPid, out parent)
                    && parent.Creation <= process.Creation)
                    parents.Add(process.Pid, parent.Pid);
            }
            if (seeds.Count == 0) return;
            userFacingFamily.ExceptWith(seeds);
            userFacingFamily.ExceptWith(WalkDescendants(parents, seeds, selfPid, 24));
        }

        internal static bool IsGameOrWhitelistProtected(int pid, int rendererPid,
            bool whitelisted, bool protectedLibraryMember, bool familyExempt,
            HashSet<int> gamePids, HashSet<int> gameDescendants, bool platformRemoved = false)
        {
            return whitelisted || (rendererPid > 0 && pid == rendererPid)
                || (!platformRemoved && (protectedLibraryMember || (familyExempt && ((gamePids != null && gamePids.Contains(pid))
                    || (gameDescendants != null && gameDescendants.Contains(pid))))));
        }

        private static readonly Environment.SpecialFolder[] UnsafeRoots =
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData
        };

        internal static bool SafeFamilyDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            string d = dir.TrimEnd('\\');
            if (d.Length <= 2) return false;
            foreach (Environment.SpecialFolder sf in UnsafeRoots)
            {
                string sd;
                try { sd = Environment.GetFolderPath(sf); } catch { continue; }
                if (!string.IsNullOrEmpty(sd) && string.Equals(d, sd.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        internal static bool IsGameFamily(string path, HashSet<string> gameDirs)
        {
            return IsGameFamily(path, gameDirs, null);
        }

        internal static bool IsGameFamily(string path, HashSet<string> gameDirs, string processName)
        {
            if (string.IsNullOrEmpty(path) || gameDirs.Count == 0) return false;
            if (GameSessionDetector.IsNonGameRole(processName, path)) return false;
            foreach (string d in gameDirs)
                if (UnderRoot(path, d)) return true;
            return false;
        }

        internal static HashSet<int> WalkDescendants(
            Dictionary<int, int> parents, ICollection<int> rootPids, int selfPid, int maxDepth)
        {
            return WalkDescendants(parents, rootPids, selfPid, maxDepth, null);
        }

        internal static HashSet<int> WalkDescendants(
            Dictionary<int, int> parents, ICollection<int> rootPids, int selfPid, int maxDepth,
            Dictionary<int, long> creations)
        {
            var result = new HashSet<int>();
            if (parents == null || rootPids == null || rootPids.Count == 0) return result;
            var roots = new HashSet<int>(rootPids);
            foreach (KeyValuePair<int, int> kv in parents)
            {
                int pid = kv.Key;
                if (pid <= 4 || pid == selfPid || roots.Contains(pid) || result.Contains(pid)) continue;
                int current = pid;
                for (int depth = 0; depth < maxDepth; depth++)
                {
                    int parent;
                    if (!parents.TryGetValue(current, out parent) || parent <= 4 || parent == current) break;
                    if (!ParentNotNewer(creations, parent, current)) break;
                    if (roots.Contains(parent)) { result.Add(pid); break; }
                    if (parent == selfPid) break;
                    current = parent;
                }
            }
            return result;
        }

        internal static HashSet<int> WalkAncestorChain(Dictionary<int, int> parents, int startPid, int selfPid, int maxHops)
        {
            return WalkAncestorChain(parents, startPid, selfPid, maxHops, null);
        }

        internal static HashSet<int> WalkAncestorChain(Dictionary<int, int> parents, int startPid, int selfPid,
            int maxHops, Dictionary<int, long> creations)
        {
            var result = new HashSet<int>();
            if (parents == null || startPid <= 4) return result;
            int current = startPid;
            for (int hop = 0; hop < maxHops; hop++)
            {
                int parent;
                if (!parents.TryGetValue(current, out parent)) break;
                if (parent <= 4 || parent == selfPid || parent == startPid || result.Contains(parent)) break;
                if (!ParentNotNewer(creations, parent, current)) break;
                result.Add(parent);
                current = parent;
            }
            return result;
        }

        // 父进程晚于子进程创建时，说明 PPID 已被复用。
        private static bool ParentNotNewer(Dictionary<int, long> creations, int parentPid, int childPid)
        {
            if (creations == null) return true;
            long parentCreation, childCreation;
            if (!creations.TryGetValue(parentPid, out parentCreation)
                || !creations.TryGetValue(childPid, out childCreation)) return true;
            if (parentCreation <= 0 || childCreation <= 0) return true;
            return parentCreation <= childCreation;
        }
    }
}
