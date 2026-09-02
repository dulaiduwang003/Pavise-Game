// @author bdth 2074055628@qq.com
// 文件用途 压制保护边界的家族圈定 进程树行走 目录归属与后台资格判定 纯函数只吃本轮快照
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
            bool familyExempt = true)
        {
            // 家族是否保护由调用方按档案和本轮身份确定 不按游戏/客户端名字推断
            //   开启家族压制仍可能影响依赖进程的响应 所以 UI 默认关闭并提示风险
            //   渲染本体 待确认候选 白名单和其它档案的保护在调用方先行放行
            //   下面四条是独立安全边界 不随逐游戏设置取消
            //   反作弊被压会心跳超时掉线 加速器被压会断流 输入音频外设链被压会卡鼠标和丢声音
            if (AntiCheatCatalog.IsAntiCheatLikeName(name)) return false;
            if (NetAcceleratorCatalog.IsAcceleratorLikeName(name)) return false;
            if (PeripheralCatalog.IsInputChainProcess(name, path)) return false;
            if (HardwareControlCatalog.IsHardwareControlProcess(name)) return false;
            if (gameHostAncestor) return false;
            if (UnderRoot(path, activeGameRoot)) return false;
            if (pid <= 4 || pid == self || session < 0 || session != ownerSession) return false;

            if (!aggressive && (pid == foreground || userFacingFamily)) return false;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return false;
            if (aggressive) return !SystemProcessCatalog.IsCoreSystemProcess(name, path, windowsRoot);
            return string.IsNullOrEmpty(windowsRoot) || !path.StartsWith(windowsRoot, StringComparison.OrdinalIgnoreCase);
        }

        // 语义跟原来那版一样 只是不再为每次比较拼一个前缀字符串出来
        //   家族豁免开着时这里是 进程数×游戏数 的量级 每次分配都摊在对局的热路径上
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

        // 保护属于每一个选择退出的档案 不只是当前前台那个游戏
        // 复用本轮 sweep 那份不可变进程快照 绝不能从游戏或客户端的
        // 可执行文件名去推断家族归属
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
                // 不要把只看 PID 的老兜底逻辑带进这些新的跨根链接
                // 身份缺失或者 PID 被复用 就到此为止
                if (parent == null || parent.Pid <= 4 || parent.Pid == selfPid
                    || parent.Pid == child.Pid || parent.Session != ownerSession
                    || parent.Creation <= 0 || parent.Creation > child.Creation) continue;
                parents[child.Pid] = parent.Pid;
            }
            result.UnionWith(seeds);
            result.UnionWith(WalkDescendants(parents, seeds, selfPid, 24));
            foreach (int seed in seeds)
                result.UnionWith(WalkAncestorChain(parents, seed, selfPid, 24));
            // 祖先进程自己受保护 但绝不能当新种子 共享宿主
            // 不能顺带豁免它那些无关的兄弟进程或者别的游戏
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

        // 窗口枚举放在这个纯策略步骤之外 快照和档案都是本轮 Sweep
        // 用的同一份输入
        // 这里只摘掉可见窗口豁免 渲染进程 显式白名单 别的档案
        // 以及直接前台这几项保护 仍然由它们各自更早或更晚的判断负责
        // 这不是一份压制名单
        internal static void FilterUserFacingGameFamily(HashSet<int> userFacingFamily,
            GameProfile profile, ProcessSnapshot snapshot, int rendererPid, int selfPid, int ownerSession,
            ICollection<int> gamePids, ICollection<int> gameDescendants, ICollection<int> gameHostAncestors,
            GameFamilyEvidence familyEvidence = null)
        {
            // 专注档共用同一个空集合 绝对不能改它 档案缺失 默认不压制
            // 或者渲染进程没确认 都保留保护
            if (userFacingFamily == null || userFacingFamily.Count == 0
                || rendererPid <= 0 || FamilyExemptFor(profile)) return;
            userFacingFamily.Remove(rendererPid);
            if (gameDescendants != null) userFacingFamily.ExceptWith(gameDescendants);
            if (gameHostAncestors != null) userFacingFamily.ExceptWith(gameHostAncestors);
            if (gamePids != null) userFacingFamily.ExceptWith(gamePids);

            if (userFacingFamily.Count == 0 || snapshot == null || ownerSession < 0) return;
            // 大厅可能在缓存下渲染家族之后才出现 复用本轮快照
            // 但只减去新证实的归属者 PID 复用 身份缺失 跨会话条目
            // 都保留可见窗口豁免 过滤之前先查重
            // 这样即便第二条是无效项 也不会让一个说不清的 PID 变成种子
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
            // 上面每条边都有两个当前且唯一的身份 以及合法的创建先后
            // 不要拿缓存 PID 或者共享宿主的祖先当新根 光是 Steam/WeGame
            // 的兄弟进程 证明不了游戏归属
            userFacingFamily.ExceptWith(seeds);
            userFacingFamily.ExceptWith(WalkDescendants(parents, seeds, selfPid, 24));
        }

        // 这份早期保护判断和隔离的策略测试共用同一套逻辑
        // 逐游戏的家族开关 永远不会摘掉渲染进程 显式白名单
        // 或者另一个受保护档案自己确认过的成员
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

        // 父进程退出后 PID 会被系统复用 同一份快照里的 PPID 可能指向一个后来才起的无关进程
        //   只比 PID 会把它当成游戏后代放行 补一道创建时间校验 父必须不晚于子
        //   拿不到时间数据的进程退回只比 PID 的老口径 宁可多放行也不要把游戏的子进程压掉
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

        // 两边的创建时间都拿得到才判 父比子晚说明这个 PPID 指的是复用后的另一个进程
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
