using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class FamilyBoundary
    {
        // 索引只属于一份已完成的快照 保留全部边 由各用途继续执行原有身份与边界检查
        internal sealed class DescendantIndex
        {
            private readonly Dictionary<int, int> firstChild;
            private readonly int[] childPids, nextSibling;

            internal DescendantIndex(Dictionary<int, int> parents)
            {
                int count = parents == null ? 0 : parents.Count;
                firstChild = new Dictionary<int, int>(count);
                childPids = new int[count];
                nextSibling = new int[count];
                if (parents == null) return;
                int position = 0;
                foreach (KeyValuePair<int, int> edge in parents)
                {
                    int previous;
                    nextSibling[position] = firstChild.TryGetValue(edge.Value, out previous) ? previous : -1;
                    childPids[position] = edge.Key;
                    firstChild[edge.Value] = position++;
                }
            }

            internal int FirstChild(int parent)
            { int position; return firstChild.TryGetValue(parent, out position) ? position : -1; }
            internal int ChildAt(int position) { return childPids[position]; }
            internal int NextSibling(int position) { return nextSibling[position]; }
        }

        private sealed class CatalogVerdict
        {
            public long Creation;

            public string Name;

            public string Path;

            public int KeywordGeneration;

            public int SecurityGeneration;

            public int PlatformMask;

            public bool Protected;
        }

        private const int CatalogVerdictCap = 8192;

        // 对局中每轮扫描都拿几百个进程的路径和整个游戏库比对 路径规范化和逐个目录比较占了大半耗时和内存分配
        // 规范化只看路径本身 按原始路径记下结果 所属游戏目录同样按原始路径记下 游戏目录列表一变就整体作废
        // 前台 进程身份和创建时间不进缓存 每轮照旧按快照判断
        private const int PathMemoCap = 4096;

        private static readonly object pathMemoSync = new object();

        private static readonly Dictionary<string, string> normalizedPaths = new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, string> libraryRootByPath = new Dictionary<string, string>(StringComparer.Ordinal);

        private static string[] libraryRootMemoRoots = new string[0];

        // 系统文件夹位置几乎不变 每次判断游戏目录是否安全都重新查 12 个文件夹太贵 隔一阵刷新一次
        private const long UnsafeRootRefreshMs = 30000;

        private static readonly object unsafeRootSync = new object();

        private static string[] unsafeRootPaths;

        private static long unsafeRootStamp;

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

        internal static bool BasicBackgroundEligible(int pid, int self, string name, string path, int session, int ownerSession, int foreground, bool userFacingFamily, string windowsRoot, bool gameHostAncestor = false, string activeGameRoot = null, bool wideScope = false, bool familyExempt = true, long creation = 0L, bool platformRemoved = false, bool allowForeground = false)
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
            // 扩围只是把有可见窗口的应用也纳进来 Windows 目录下的系统组件不论是否扩围都不碰
            if (!wideScope && !platformRemoved && userFacingFamily)
            {
                return false;
            }
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(windowsRoot))
            {
                return !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        internal static bool CatalogProtected(int pid, long creation, string name, string path)
        {
            int keywordGeneration = PeripheralCatalog.UserKeywordGeneration;
            int currentGenerationResult2 = SecurityProductCatalog.CurrentGeneration();
            int mask = PlatformRuntimePolicy.Mask;
            if (creation > 0)
            {
                lock (catalogSync)
                {
                    CatalogVerdict value;
                    if (catalogVerdicts.TryGetValue(pid, out value) && value.Creation == creation && value.KeywordGeneration == keywordGeneration && value.SecurityGeneration == currentGenerationResult2 && value.PlatformMask == mask && string.Equals(value.Name, name, StringComparison.Ordinal) && string.Equals(value.Path, path, StringComparison.Ordinal))
                    {
                        return value.Protected;
                    }
                }
            }
            // 反作弊一律受保护 不进家族治理 可压制的由反作弊引擎按游戏开关接管
            bool protectedRuntime = AntiCheatCatalog.IsAntiCheatProcess(name, path) || WeGameRuntimeProtection.IsCriticalRuntime(name, path) || NetAcceleratorCatalog.IsAcceleratorLikeName(name) || NetAcceleratorCatalog.IsAcceleratorLikePath(path) || SecurityProductCatalog.IsSecurityProduct(path) || PeripheralCatalog.IsInputChainProcess(name, path) || HardwareControlCatalog.IsHardwareControlProcess(name) || StreamingToolCatalog.IsStreamingToolProcess(name, path) || VoiceChatCatalog.IsVoiceChatProcess(name, path);
            if (creation > 0)
            {
                lock (catalogSync)
                {
                    if (catalogVerdicts.Count >= CatalogVerdictCap)
                    {
                        catalogVerdicts.Clear();
                    }
                    catalogVerdicts[pid] = new CatalogVerdict
                    {
                        Creation = creation,
                        Name = name,
                        Path = path,
                        KeywordGeneration = keywordGeneration,
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
                List<int> list = null;
                foreach (int key in catalogVerdicts.Keys)
                {
                    if (!live.Contains(key))
                    {
                        if (list == null)
                        {
                            list = new List<int>();
                        }
                        list.Add(key);
                    }
                }
                if (list == null)
                {
                    return;
                }
                foreach (int item in list)
                {
                    catalogVerdicts.Remove(item);
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
            // 这些后端会创建游戏进程并承担会话和网络工作 其界面渲染进程仍可独立纳入家族治理

            if (!string.Equals(normalized, "LeagueClient", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalized, "RiotClientServices", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(path)) return true;
            string image = WhitelistRule.NormalizeImagePath(path);
            return image.Length > 0 && string.Equals(normalized, WhitelistRule.NormalizeName(Path.GetFileName(image)), StringComparison.OrdinalIgnoreCase);
        }

        internal sealed class SweepIndex
        {
            internal readonly ProcEntry[] Entries;
            internal readonly Dictionary<int, ProcEntry> CreationProcesses = new Dictionary<int, ProcEntry>();
            private readonly Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.Ordinal);
            private Dictionary<int, int> parents;

            internal SweepIndex(ProcessSnapshot snapshot, int selfPid, int ownerSession)
            {
                var eligible = new List<ProcEntry>();
                var duplicates = new HashSet<int>();
                foreach (ProcEntry process in snapshot.Entries)
                {
                    IndexCreationProcess(process, CreationProcesses, duplicates);
                    if (process == null || process.Pid <= 4 || process.Pid == selfPid || process.Session != ownerSession || process.Creation <= 0)
                        continue;
                    eligible.Add(process);
                }
                Entries = eligible.ToArray();
            }

            internal Dictionary<int, int> LibraryParents(ProcessSnapshot snapshot, int selfPid, int ownerSession)
            {
                if (parents != null) return parents;
                parents = new Dictionary<int, int>();
                foreach (ProcEntry process in Entries)
                {
                    ProcEntry parent = snapshot.Find(process.ParentPid);
                    if (parent != null && parent.Pid > 4 && parent.Pid != selfPid && parent.Pid != process.Pid
                        && parent.Session == ownerSession && parent.Creation > 0 && parent.Creation <= process.Creation)
                        parents[process.Pid] = parent.Pid;
                }
                return parents;
            }

            internal string ImagePath(string raw)
            {
                string path;
                if (!paths.TryGetValue(raw, out path)) paths.Add(raw, path = NormalizedPath(raw));
                return path;
            }
        }

        private static void IndexCreationProcess(ProcEntry process, Dictionary<int, ProcEntry> processes, HashSet<int> duplicates)
        {
            if (process == null || duplicates.Contains(process.Pid)) return;
            if (processes.ContainsKey(process.Pid)) { processes.Remove(process.Pid); duplicates.Add(process.Pid); }
            else processes.Add(process.Pid, process);
        }

        internal static HashSet<int> CollectGameCreationHosts(ProcessSnapshot snapshot, ICollection<int> games, int selfPid, int ownerSession, SweepIndex index = null)
        {
            var result = new HashSet<int>();
            if (snapshot == null || games == null || ownerSession < 0) return result;
            Dictionary<int, ProcEntry> processes = index == null ? new Dictionary<int, ProcEntry>() : index.CreationProcesses;
            if (index == null)
            {
                var duplicates = new HashSet<int>();
                foreach (ProcEntry process in snapshot.Entries) IndexCreationProcess(process, processes, duplicates);
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

        internal static HashSet<int> CollectProtectedLibraryFamily(IList<GameProfile> configured, ProcessSnapshot snapshot, int selfPid, int ownerSession, GameFamilyEvidence familyEvidence = null, bool exactExecutablesOnly = false, bool forceFamilyProtection = false, SweepIndex index = null)
        {
            HashSet<int> hashSet = new HashSet<int>();
            if (configured == null || snapshot == null || ownerSession < 0)
            {
                return hashSet;
            }
            List<string> list = new List<string>();
            HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> legacyGameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile item in configured)
            {
                if (item != null && (exactExecutablesOnly || forceFamilyProtection || FamilyExemptFor(item)))
                {
                    string executable = NormalizedPath(item.ExecutablePath);
                    if (executable.Length > 0)
                    {
                        hashSet2.Add(executable);
                    }
                    if (exactExecutablesOnly && forceFamilyProtection && executable.Length == 0)
                        foreach (string entry in item.Entries)
                        {
                            string gameName = WhitelistRule.NormalizeName(entry);
                            if (gameName.Length > 0) legacyGameNames.Add(gameName);
                        }
                    if (!exactExecutablesOnly && SafeFamilyDir(item.Root))
                    {
                        list.Add(item.Root);
                    }
                }
            }
            if (hashSet2.Count == 0 && list.Count == 0 && legacyGameNames.Count == 0)
            {
                return hashSet;
            }
            Dictionary<int, int> dictionary = exactExecutablesOnly ? null : index == null ? new Dictionary<int, int>() : index.LibraryParents(snapshot, selfPid, ownerSession);
            HashSet<int> hashSet3 = new HashSet<int>();
            if (!exactExecutablesOnly && familyEvidence != null)
                hashSet3.UnionWith(familyEvidence.MatchingProcesses(configured, snapshot, selfPid, ownerSession, forceFamilyProtection, index == null ? null : index.Entries));
            ProcEntry[] entries = index == null ? snapshot.Entries : index.Entries;
            foreach (ProcEntry procEntry in entries)
            {
                if (procEntry.Pid <= 4 || procEntry.Pid == selfPid || procEntry.Session != ownerSession || procEntry.Creation <= 0)
                {
                    continue;
                }
                if ((!string.IsNullOrEmpty(procEntry.Path) && (hashSet2.Contains(index == null ? NormalizedPath(procEntry.Path) : index.ImagePath(procEntry.Path)) || (list.Count > 0 && LibraryRootMemo(procEntry.Path, list) != null)))
                    || (legacyGameNames.Count > 0 && legacyGameNames.Contains(WhitelistRule.NormalizeName(procEntry.Name))))
                {
                    hashSet3.Add(procEntry.Pid);
                }
                if (!exactExecutablesOnly && index == null)
                {
                    ProcEntry procEntry2 = snapshot.Find(procEntry.ParentPid);
                    if (procEntry2 != null && procEntry2.Pid > 4 && procEntry2.Pid != selfPid && procEntry2.Pid != procEntry.Pid && procEntry2.Session == ownerSession && procEntry2.Creation > 0 && procEntry2.Creation <= procEntry.Creation)
                    {
                        dictionary[procEntry.Pid] = procEntry2.Pid;
                    }
                }
            }
            hashSet.UnionWith(hashSet3);
            if (exactExecutablesOnly)
            {
                return hashSet;
            }
            hashSet.UnionWith(WalkDescendants(dictionary, hashSet3, selfPid, 24));
            foreach (int item3 in hashSet3)
            {
                hashSet.UnionWith(WalkAncestorChain(dictionary, item3, selfPid, 24));
            }
            return hashSet;
        }

        // 和 WhitelistRule.NormalizeImagePath 结果相同 只是同一条原始路径不再重复规范化
        internal static string NormalizedPath(string raw)
        {
            if (raw == null) return WhitelistRule.NormalizeImagePath(raw);
            lock (pathMemoSync)
            {
                string normalized;
                if (normalizedPaths.TryGetValue(raw, out normalized)) return normalized;
            }
            string result = WhitelistRule.NormalizeImagePath(raw);
            lock (pathMemoSync)
            {
                if (normalizedPaths.Count >= PathMemoCap) normalizedPaths.Clear();
                normalizedPaths[raw] = result;
            }
            return result;
        }

        // 和 LibraryRootOf 结果相同 游戏目录列表和上次不同时先清空记录
        private static string LibraryRootMemo(string path, List<string> roots)
        {
            lock (pathMemoSync)
            {
                if (!SameRoots(libraryRootMemoRoots, roots))
                {
                    libraryRootByPath.Clear();
                    libraryRootMemoRoots = roots.ToArray();
                }
                string root;
                if (libraryRootByPath.TryGetValue(path, out root)) return root.Length == 0 ? null : root;
                root = LibraryRootOf(path, roots);
                if (libraryRootByPath.Count >= PathMemoCap) libraryRootByPath.Clear();
                libraryRootByPath[path] = root ?? "";
                return root;
            }
        }

        private static bool SameRoots(string[] remembered, List<string> roots)
        {
            if (remembered.Length != roots.Count) return false;
            for (int i = 0; i < remembered.Length; i++)
            {
                if (!string.Equals(remembered[i], roots[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static string[] UnsafeRootPaths()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (unsafeRootSync)
            {
                if (unsafeRootPaths != null && (now - unsafeRootStamp) * 1000 / System.Diagnostics.Stopwatch.Frequency < UnsafeRootRefreshMs)
                    return unsafeRootPaths;
            }
            var paths = new List<string>(UnsafeRoots.Length);
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
                if (!string.IsNullOrEmpty(folderPath)) paths.Add(folderPath.TrimEnd('\\'));
            }
            string[] result = paths.ToArray();
            lock (unsafeRootSync)
            {
                unsafeRootPaths = result;
                unsafeRootStamp = now;
            }
            return result;
        }

        internal static HashSet<int> ExpandUserFacingFamily(Dictionary<int, int> parents, Dictionary<int, string> names, HashSet<int> roots,
            DescendantIndex descendants = null)
        {
            HashSet<int> hashSet = new HashSet<int>(roots ?? new HashSet<int>());
            HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names != null)
            {
                foreach (int item in hashSet)
                {
                    string value;
                    if (names.TryGetValue(item, out value) && !string.IsNullOrEmpty(value))
                    {
                        hashSet2.Add(value);
                    }
                }
            }
            if (names != null)
            {
                foreach (KeyValuePair<int, string> name in names)
                {
                    if (hashSet2.Contains(name.Value))
                    {
                        hashSet.Add(name.Key);
                    }
                }
            }
            if (parents == null || names == null || hashSet.Count == 0) return hashSet;
            descendants = descendants ?? new DescendantIndex(parents);
            var pending = new Queue<int>(hashSet);
            while (pending.Count != 0)
            {
                int parent = pending.Dequeue();
                string parentName;
                if (!names.TryGetValue(parent, out parentName) || SystemProcessCatalog.IsShellProcess(parentName)) continue;
                for (int position = descendants.FirstChild(parent); position >= 0; position = descendants.NextSibling(position))
                {
                    int child = descendants.ChildAt(position);
                    if (hashSet.Add(child)) pending.Enqueue(child);
                }
            }
            return hashSet;
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
            Dictionary<int, ProcEntry> dictionary = new Dictionary<int, ProcEntry>();
            HashSet<int> hashSet = new HashSet<int>();
            ProcEntry[] entries = snapshot.Entries;
            foreach (ProcEntry procEntry in entries)
            {
                if (procEntry != null)
                {
                    if (!hashSet.Add(procEntry.Pid))
                    {
                        dictionary.Remove(procEntry.Pid);
                    }
                    else if (procEntry.Pid > 4 && procEntry.Pid != selfPid && procEntry.Session == ownerSession && procEntry.Creation > 0 && !string.IsNullOrEmpty(procEntry.Name) && GameFamilyHistory.CanonicalPath(procEntry.Path) && string.Equals(procEntry.Name, Path.GetFileNameWithoutExtension(procEntry.Path), StringComparison.OrdinalIgnoreCase))
                    {
                        dictionary.Add(procEntry.Pid, procEntry);
                    }
                }
            }
            bool safeFamilyDirSucceeded = SafeFamilyDir(profile.Root);
            HashSet<int> hashSet2 = new HashSet<int>();
            Dictionary<int, int> dictionary2 = new Dictionary<int, int>();
            foreach (ProcEntry value2 in dictionary.Values)
            {
                if (string.Equals(value2.Path, profile.ExecutablePath, StringComparison.OrdinalIgnoreCase) || (safeFamilyDirSucceeded && UnderRoot(value2.Path, profile.Root)) || (familyEvidence != null && familyEvidence.Contains(profile, value2.Pid, value2.Creation, value2.Path)))
                {
                    hashSet2.Add(value2.Pid);
                }
                ProcEntry value;
                if (value2.ParentPid != value2.Pid && dictionary.TryGetValue(value2.ParentPid, out value) && value.Creation <= value2.Creation)
                {
                    dictionary2.Add(value2.Pid, value.Pid);
                }
            }
            if (hashSet2.Count != 0)
            {
                userFacingFamily.ExceptWith(hashSet2);
                userFacingFamily.ExceptWith(WalkDescendants(dictionary2, hashSet2, selfPid, 24));
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
            foreach (string folderPath in UnsafeRootPaths())
            {
                if (string.Equals(dirTrimmed, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        internal static HashSet<int> CollectRendererDescendants(ProcessSnapshot snapshot, int rendererPid, long rendererCreation, int selfPid,
            int ownerSession, Dictionary<int, long> retained)
        {
            var descendants = new HashSet<int>();
            ProcEntry renderer = snapshot == null ? null : snapshot.Find(rendererPid);
            if (renderer == null || rendererPid <= 4 || rendererCreation <= 0 || renderer.Creation != rendererCreation
                || ownerSession < 0 || renderer.Session != ownerSession)
            {
                retained.Clear();
                return descendants;
            }

            // 创建时间和会话足以核验快照中的父子边 不要求读到图像路径
            var parents = new Dictionary<int, int>();
            foreach (ProcEntry child in snapshot.ByPid.Values)
            {
                if (child.Pid <= 4 || child.Pid == selfPid || child.Session != ownerSession || child.Creation <= 0) continue;
                ProcEntry parent = snapshot.Find(child.ParentPid);
                if (parent != null && parent.Session == ownerSession && parent.Creation > 0 && parent.Creation < child.Creation)
                    parents.Add(child.Pid, parent.Pid);
            }
            var index = new DescendantIndex(parents);
            var pending = new Queue<int>();
            pending.Enqueue(rendererPid);
            // 中间进程退出后 仍用已核验且存活的子孙接续进程树
            foreach (KeyValuePair<int, long> previous in retained)
            {
                ProcEntry current = snapshot.Find(previous.Key);
                if (current != null && current.Creation == previous.Value && current.Session == ownerSession && descendants.Add(current.Pid))
                    pending.Enqueue(current.Pid);
            }
            while (pending.Count != 0)
            {
                int parent = pending.Dequeue();
                for (int position = index.FirstChild(parent); position >= 0; position = index.NextSibling(position))
                {
                    int child = index.ChildAt(position);
                    if (descendants.Add(child)) pending.Enqueue(child);
                }
            }
            retained.Clear();
            foreach (int pid in descendants) retained.Add(pid, snapshot.Find(pid).Creation);
            return descendants;
        }

        internal static HashSet<int> WalkDescendants(Dictionary<int, int> parents, ICollection<int> rootPids, int selfPid, int maxDepth)
        {
            return WalkDescendants(parents, rootPids, selfPid, maxDepth, null);
        }

        internal static HashSet<int> WalkDescendants(Dictionary<int, int> parents, ICollection<int> rootPids, int selfPid, int maxDepth, Dictionary<int, long> creations)
        {
            HashSet<int> hashSet = new HashSet<int>();
            if (parents == null || rootPids == null || rootPids.Count == 0)
            {
                return hashSet;
            }
            HashSet<int> hashSet2 = new HashSet<int>(rootPids);
            foreach (KeyValuePair<int, int> parent in parents)
            {
                int key = parent.Key;
                if (key <= 4 || key == selfPid || hashSet2.Contains(key) || hashSet.Contains(key))
                {
                    continue;
                }
                int keySnapshot = key;
                for (int i = 0; i < maxDepth; i++)
                {
                    int value;
                    if (!parents.TryGetValue(keySnapshot, out value))
                    {
                        break;
                    }
                    if (value <= 4)
                    {
                        break;
                    }
                    if (value == keySnapshot)
                    {
                        break;
                    }
                    if (!ParentNotNewer(creations, value, keySnapshot))
                    {
                        break;
                    }
                    if (hashSet2.Contains(value))
                    {
                        hashSet.Add(key);
                        break;
                    }
                    if (value == selfPid)
                    {
                        break;
                    }
                    keySnapshot = value;
                }
            }
            return hashSet;
        }

        internal static HashSet<int> WalkAncestorChain(Dictionary<int, int> parents, int startPid, int selfPid, int maxHops)
        {
            return WalkAncestorChain(parents, startPid, selfPid, maxHops, null);
        }

        internal static HashSet<int> WalkAncestorChain(Dictionary<int, int> parents, int startPid, int selfPid, int maxHops, Dictionary<int, long> creations)
        {
            HashSet<int> hashSet = new HashSet<int>();
            if (parents == null || startPid <= 4)
            {
                return hashSet;
            }
            int startPidSnapshot = startPid;
            for (int i = 0; i < maxHops; i++)
            {
                int value;
                if (!parents.TryGetValue(startPidSnapshot, out value))
                {
                    break;
                }
                if (value <= 4)
                {
                    break;
                }
                if (value == selfPid)
                {
                    break;
                }
                if (value == startPid)
                {
                    break;
                }
                if (hashSet.Contains(value))
                {
                    break;
                }
                if (!ParentNotNewer(creations, value, startPidSnapshot))
                {
                    break;
                }
                hashSet.Add(value);
                startPidSnapshot = value;
            }
            return hashSet;
        }

        private static bool ParentNotNewer(Dictionary<int, long> creations, int parentPid, int childPid)
        {
            if (creations == null)
            {
                return true;
            }
            long value;
            long value2;
            if (!creations.TryGetValue(parentPid, out value) || !creations.TryGetValue(childPid, out value2))
            {
                return true;
            }
            if (value <= 0 || value2 <= 0)
            {
                return true;
            }
            return value <= value2;
        }
    }

    // 开了逐游戏家族治理时 本局渲染进程往上的启动链在对局中按普通后台治理
    // 渲染进程退出后它们已不是任何存活游戏的上级 快照里看不出来 所以按 PID 和创建时间记住本局治理过的成员
    // 下一轮扫描照样放开 避免下一个游戏从被降级的父进程继承 Idle 优先级
    // 只在后台扫描里用 换局清空 进程退出或 PID 复用的条目每轮丢弃 总数有上限
    internal sealed class GameLaunchChainHold
    {
        private const int MaxMembers = 64;

        private readonly object gate = new object();

        private readonly Dictionary<int, long> members = new Dictionary<int, long>();

        private string session;

        internal void Begin(string automationSession, ProcessSnapshot snapshot)
        {
            lock (gate)
            {
                if (!string.Equals(session, automationSession, StringComparison.Ordinal))
                {
                    session = automationSession;
                    members.Clear();
                    return;
                }
                if (members.Count == 0) return;
                List<int> gone = null;
                foreach (KeyValuePair<int, long> member in members)
                {
                    ProcEntry current = snapshot == null ? null : snapshot.Find(member.Key);
                    if (current == null || current.Creation != member.Value)
                    {
                        if (gone == null) gone = new List<int>();
                        gone.Add(member.Key);
                    }
                }
                if (gone != null) foreach (int pid in gone) members.Remove(pid);
            }
        }

        internal void Remember(int pid, long creation)
        {
            if (pid <= 0 || creation <= 0) return;
            lock (gate)
            {
                if (members.Count >= MaxMembers && !members.ContainsKey(pid)) return;
                members[pid] = creation;
            }
        }

        internal bool Contains(int pid, long creation)
        {
            if (creation <= 0) return false;
            lock (gate)
            {
                long held;
                return members.TryGetValue(pid, out held) && held == creation;
            }
        }
    }
}
