// @author bdth 2074055628@qq.com
// File purpose Family scoping for the suppression protection boundary, process tree walking, directory ownership and background eligibility decisions; pure functions consuming only this round's snapshot
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
            bool familyExempt = true, long creation = 0)
        {
            // Whether the family is protected is decided by the caller from the profile and this round's identity, not inferred from game/client names
            //   Enabling family suppression can still affect the responsiveness of dependent processes, so the UI defaults it off and warns of the risk
            //   The renderer itself, pending candidates, whitelist and other profiles' protection are let through earlier by the caller
            //   The four below are independent safety boundaries, not cancelled by per-game settings
            //   Suppressed anti-cheat times out its heartbeat and disconnects, a suppressed accelerator drops the stream, a suppressed input/audio peripheral chain stutters the mouse and loses audio
            if (CatalogProtected(pid, creation, name, path)) return false;
            if (gameHostAncestor) return false;
            if (UnderRoot(path, activeGameRoot)) return false;
            if (pid <= 4 || pid == self || session < 0 || session != ownerSession) return false;

            if (!aggressive && (pid == foreground || userFacingFamily)) return false;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return false;
            if (aggressive) return !SystemProcessCatalog.IsCoreSystemProcess(name, path, windowsRoot);
            return string.IsNullOrEmpty(windowsRoot) || !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
        }

        // The four catalog decisions look only at name, path and present peripheral terms, the result never changes for the same process instance
        //   Dozens to hundreds of substring matches per process, 3ms per round on a six-hundred-process machine; remember the result by pid+creation time
        //   The term set has a generation number: recompute after a peripheral change, recompute when name or path mismatch, calls without creation time are not cached
        private sealed class CatalogVerdict
        {
            public long Creation;
            public string Name;
            public string Path;
            public int VendorGeneration;
            public bool Protected;
        }

        private static readonly object catalogSync = new object();
        private static readonly Dictionary<int, CatalogVerdict> catalogVerdicts = new Dictionary<int, CatalogVerdict>();
        private const int CatalogVerdictCap = 8192;

        internal static bool CatalogProtected(int pid, long creation, string name, string path)
        {
            // Let the terms refresh on expiry first, then take the generation; hit or miss both go through this step, refresh does not depend on a cache miss
            int generation = PeripheralVendorProbe.CurrentGeneration();
            if (creation > 0)
                lock (catalogSync)
                {
                    CatalogVerdict hit;
                    if (catalogVerdicts.TryGetValue(pid, out hit) && hit.Creation == creation
                        && hit.VendorGeneration == generation
                        && string.Equals(hit.Name, name, StringComparison.Ordinal)
                        && string.Equals(hit.Path, path, StringComparison.Ordinal))
                        return hit.Protected;
                }
            bool verdict = AntiCheatCatalog.IsAntiCheatProcess(name, path)
                || NetAcceleratorCatalog.IsAcceleratorLikeName(name)
                || PeripheralCatalog.IsInputChainProcess(name, path)
                || HardwareControlCatalog.IsHardwareControlProcess(name);
            if (creation > 0)
                lock (catalogSync)
                {
                    if (catalogVerdicts.Count >= CatalogVerdictCap) catalogVerdicts.Clear();
                    catalogVerdicts[pid] = new CatalogVerdict
                    {
                        Creation = creation, Name = name, Path = path,
                        VendorGeneration = generation, Protected = verdict
                    };
                }
            return verdict;
        }

        // After each sweep evict processes that are gone from the cache; PID reuse is covered by creation time, this only keeps it from growing
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

        // Same semantics as the previous version, just no longer builds a prefix string for every comparison
        //   With family exemption on this is on the order of process count times game count, every allocation lands on the match hot path
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

        // Protection belongs to every opted-out profile, not just the current foreground game
        // Reuse this sweep's immutable process snapshot; never infer family membership from
        // the game's or client's executable name
        internal static HashSet<int> CollectProtectedLibraryFamily(IList<GameProfile> configured,
            ProcessSnapshot snapshot, int selfPid, int ownerSession, GameFamilyEvidence familyEvidence = null)
        {
            var result = new HashSet<int>();
            if (configured == null || snapshot == null || ownerSession < 0) return result;
            var roots = new List<string>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile profile in configured)
            {
                if (profile == null || !FamilyExemptFor(profile)) continue;
                if (!string.IsNullOrEmpty(profile.ExecutablePath)) paths.Add(profile.ExecutablePath);
                if (!string.IsNullOrEmpty(profile.LearnedExecutablePath)) paths.Add(profile.LearnedExecutablePath);
                if (SafeFamilyDir(profile.Root)) roots.Add(profile.Root);
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
                if (familyEvidence != null)
                    foreach (GameProfile profile in configured)
                        if (profile != null && FamilyExemptFor(profile)
                            && familyEvidence.Contains(profile, child.Pid, child.Creation, child.Path))
                        { seeds.Add(child.Pid); break; }
                ProcEntry parent = snapshot.Find(child.ParentPid);
                // Do not carry the old PID-only fallback logic into these new cross-root links
                // Identity missing or PID reused, stop here
                if (parent == null || parent.Pid <= 4 || parent.Pid == selfPid
                    || parent.Pid == child.Pid || parent.Session != ownerSession
                    || parent.Creation <= 0 || parent.Creation > child.Creation) continue;
                parents[child.Pid] = parent.Pid;
            }
            result.UnionWith(seeds);
            result.UnionWith(WalkDescendants(parents, seeds, selfPid, 24));
            foreach (int seed in seeds)
                result.UnionWith(WalkAncestorChain(parents, seed, selfPid, 24));
            // The ancestor process is itself protected but must never become a new seed; a shared host
            // must not incidentally exempt its unrelated siblings or other games
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

        // Window enumeration stays outside this pure policy step; snapshot and profiles are the same
        // input this round's Sweep uses
        // This only removes the visible-window exemption; renderer process, explicit whitelist, other profiles
        // and direct foreground protections remain the responsibility of their own earlier or later checks
        // This is not a suppression list
        internal static void FilterUserFacingGameFamily(HashSet<int> userFacingFamily,
            GameProfile profile, ProcessSnapshot snapshot, int rendererPid, int selfPid, int ownerSession,
            ICollection<int> gamePids, ICollection<int> gameDescendants, ICollection<int> gameHostAncestors,
            GameFamilyEvidence familyEvidence = null)
        {
            // Esports tier shares the same empty set, never mutate it; profile missing means no suppression by default
            // or renderer unconfirmed, protection is kept either way
            if (userFacingFamily == null || userFacingFamily.Count == 0
                || rendererPid <= 0 || FamilyExemptFor(profile)) return;
            userFacingFamily.Remove(rendererPid);
            if (gameDescendants != null) userFacingFamily.ExceptWith(gameDescendants);
            if (gameHostAncestors != null) userFacingFamily.ExceptWith(gameHostAncestors);
            if (gamePids != null) userFacingFamily.ExceptWith(gamePids);

            if (userFacingFamily.Count == 0 || snapshot == null || ownerSession < 0) return;
            // The lobby may appear after the render family is cached, reuse this round's snapshot
            // but subtract only newly confirmed members; PID reuse, missing identity, cross-session entries
            // all keep the visible-window exemption; dedupe before filtering
            // so even if the second entry is invalid, an ambiguous PID never becomes a seed
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
            // Every edge above has two current, unique identities and a valid creation order
            // Do not take a cached PID or a shared host's ancestor as a new root; merely being a sibling
            // of Steam/WeGame proves no game membership
            userFacingFamily.ExceptWith(seeds);
            userFacingFamily.ExceptWith(WalkDescendants(parents, seeds, selfPid, 24));
        }

        // This early protection check shares the same logic with the isolation policy tests
        // The per-game family toggle never removes the renderer process, the explicit whitelist
        // or a member confirmed by another protected profile itself
        internal static bool IsGameOrWhitelistProtected(int pid, int rendererPid,
            bool whitelisted, bool protectedLibraryMember, bool familyExempt,
            HashSet<int> gamePids, HashSet<int> gameDescendants)
        {
            return whitelisted || protectedLibraryMember || (rendererPid > 0 && pid == rendererPid)
                || (familyExempt && ((gamePids != null && gamePids.Contains(pid))
                    || (gameDescendants != null && gameDescendants.Contains(pid))));
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

        // After a parent exits the system reuses its PID; a PPID in the same snapshot may point to an unrelated process started later
        //   Comparing PID alone would let it through as a game descendant; add a creation time check, the parent must not be newer than the child
        //   Processes with no timing data fall back to the old PID-only criteria; better to let through too much than suppress the game's child process
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

        // Decide only when both creation times are available; a parent newer than its child means this PPID refers to a different process after reuse
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
