using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
    // 后台治理释放某个进程时 找出它被压以后新建的子孙 交给 SuppressionCore 判断要不要一并写回
    // 只看同一会话 反作弊和 Windows 目录下的系统组件不碰 最多看 64 个 一层层往下找 子孙必须晚于各自的父进程和开始压的时间
    internal static class InheritedDescendants
    {
        internal const int Limit = 64;
        // 退局时一批条目接连释放 快照复用一秒内的那张 父子索引也按快照只建一次 窗口里刚出生的子进程漏掉也无妨
        internal const int SnapshotReuseMs = 1000;
        private static readonly object gate = new object();
        private static ProcessSnapshot indexed;
        private static Dictionary<int, List<ProcEntry>> index;
        private static int indexBuilds;
        internal static int IndexBuilds { get { lock (gate) return indexBuilds; } }
        // 本进程的会话和 Windows 目录在运行期间不会变
        private static readonly int session = CurrentSession();
        private static readonly string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        private static int CurrentSession() { using (Process self = Process.GetCurrentProcess()) return self.SessionId; }

        internal static List<KeyValuePair<int, long>> Find(int pid, long creation, long pressedSince)
        {
            var found = new List<KeyValuePair<int, long>>();
            if (pid <= 4 || creation <= 0 || pressedSince <= 0) return found;
            ProcessSnapshot snapshot = ProcessSnapshotSource.Capture(session, SnapshotReuseMs);
            if (snapshot == null || snapshot.Entries == null) return found;
            Dictionary<int, List<ProcEntry>> children;
            lock (gate)
            {
                if (!ReferenceEquals(snapshot, indexed)) { index = Index(snapshot); indexed = snapshot; indexBuilds++; }
                children = index;
            }
            var queue = new Queue<KeyValuePair<int, long>>();
            var seen = new HashSet<int> { pid };
            queue.Enqueue(new KeyValuePair<int, long>(pid, creation));
            while (queue.Count > 0 && found.Count < Limit)
            {
                KeyValuePair<int, long> parent = queue.Dequeue();
                List<ProcEntry> list;
                if (!children.TryGetValue(parent.Key, out list)) continue;
                foreach (ProcEntry child in list)
                {
                    if (found.Count >= Limit) break;
                    // 编号会被复用 子进程必须晚于父进程出生 也必须是开始压以后才出生的
                    if (child.Creation <= parent.Value || child.Creation < pressedSince || !seen.Add(child.Pid)) continue;
                    if (!CanRestore(child)) continue;
                    found.Add(new KeyValuePair<int, long>(child.Pid, child.Creation));
                    queue.Enqueue(new KeyValuePair<int, long>(child.Pid, child.Creation));
                }
            }
            return found;
        }

        internal static bool CanRestore(ProcEntry child)
        { return CanRestore(child.Session, child.Name, child.Path); }

        internal static bool CanRestore(int childSession, string name, string path)
        {
            return childSession == session && !string.IsNullOrEmpty(path)
                && !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                && !AntiCheatCatalog.IsAntiCheatLikeName(name) && !AntiCheatCatalog.IsAntiCheatPath(path);
        }

        // 建好以后只读 多个线程共用同一份
        private static Dictionary<int, List<ProcEntry>> Index(ProcessSnapshot snapshot)
        {
            var children = new Dictionary<int, List<ProcEntry>>();
            foreach (ProcEntry entry in snapshot.Entries)
            {
                if (entry == null || entry.Pid <= 4 || entry.Pid == entry.ParentPid) continue;
                List<ProcEntry> list;
                if (!children.TryGetValue(entry.ParentPid, out list)) children[entry.ParentPid] = list = new List<ProcEntry>();
                list.Add(entry);
            }
            return children;
        }
    }
}
