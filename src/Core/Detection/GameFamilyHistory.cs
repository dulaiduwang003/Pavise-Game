// @author bdth 2074055628@qq.com
// 文件用途 保留已经验证的进程亲缘，不把退出的启动器伪装成活进程，也不扩大安装目录。
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    // Configuration identity, deliberately excluding cosmetic labels and policy
    // switches. An old result cannot grant membership to a changed entry/root.
    internal struct GameFamilyProfileKey : IEquatable<GameFamilyProfileKey>
    {
        private readonly string id, root, executable, learned;
        private readonly bool force;

        internal GameFamilyProfileKey(GameProfile profile)
        {
            id = profile.Id;
            root = profile.Root;
            executable = profile.ExecutablePath;
            learned = profile.LearnedExecutablePath;
            force = profile.ForceTrigger;
        }

        internal string CreateRootPrefix()
        {
            if (!string.IsNullOrEmpty(root))
            {
                string directory = root.TrimEnd('\\');
                if (GameFamilyHistory.CanonicalPath(directory)
                    && !string.Equals(directory, Path.GetPathRoot(directory).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                    return directory + "\\";
            }
            return null;
        }

        internal bool Owns(string path, string rootPrefix)
        {
            return !string.IsNullOrEmpty(path)
                && ((!string.IsNullOrEmpty(executable) && Equal(executable, path))
                    || (!string.IsNullOrEmpty(learned) && Equal(learned, path))
                    || (rootPrefix != null && path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)));
        }

        public bool Equals(GameFamilyProfileKey other)
        {
            return force == other.force && Equal(id, other.id) && Equal(root, other.root)
                && Equal(executable, other.executable) && Equal(learned, other.learned);
        }

        public override bool Equals(object value)
        {
            return value is GameFamilyProfileKey && Equals((GameFamilyProfileKey)value);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int result = force ? 1 : 0;
                result = result * 31 + Hash(id);
                result = result * 31 + Hash(root);
                result = result * 31 + Hash(executable);
                return result * 31 + Hash(learned);
            }
        }

        private static bool Equal(string a, string b)
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static int Hash(string value)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(value ?? "");
        }
    }

    // Only current, verified snapshot identities are exported. All mutable
    // history and all dead parents remain private to GameFamilyHistory.
    internal sealed class GameFamilyEvidence
    {
        internal sealed class Member
        {
            internal readonly long Creation;
            internal readonly string Path;

            internal Member(long creation, string path)
            {
                Creation = creation;
                Path = path;
            }
        }

        private readonly Dictionary<GameFamilyProfileKey, Dictionary<int, Member>> profiles;

        internal static readonly GameFamilyEvidence Empty = new GameFamilyEvidence(
            new Dictionary<GameFamilyProfileKey, Dictionary<int, Member>>());

        internal GameFamilyEvidence(Dictionary<GameFamilyProfileKey, Dictionary<int, Member>> source)
        {
            profiles = new Dictionary<GameFamilyProfileKey, Dictionary<int, Member>>();
            foreach (var pair in source)
                profiles.Add(pair.Key, new Dictionary<int, Member>(pair.Value));
        }

        internal bool Contains(GameProfile profile, int pid, long creation, string path)
        {
            if (profile == null || string.IsNullOrEmpty(profile.Id) || pid <= 4
                || creation <= 0 || string.IsNullOrEmpty(path)) return false;
            Dictionary<int, Member> members;
            Member member;
            return profiles.TryGetValue(new GameFamilyProfileKey(profile), out members)
                && members.TryGetValue(pid, out member) && member.Creation == creation
                && string.Equals(member.Path, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class GameFamilyHistory
    {
        internal const int MaxNodes = 4096;
        internal const int MaxAncestorDepth = 24;
        internal const int PendingEventRetentionMs = 5000;
        private const int MaxMemberships = 16384;
        private const int MaxMembershipChecks = 1048576;

        private struct Identity : IEquatable<Identity>
        {
            internal int Session, Pid;
            internal long Creation;

            internal Identity(int session, int pid, long creation)
            {
                Session = session;
                Pid = pid;
                Creation = creation;
            }

            public bool Equals(Identity other)
            {
                return Session == other.Session && Pid == other.Pid && Creation == other.Creation;
            }

            public override bool Equals(object value)
            {
                return value is Identity && Equals((Identity)value);
            }

            public override int GetHashCode()
            {
                unchecked { return ((Session * 397) ^ Pid) * 397 ^ Creation.GetHashCode(); }
            }
        }

        private sealed class Node
        {
            internal readonly Identity Key;
            internal readonly string Path;
            internal int ParentPid;
            internal Identity Parent;
            internal bool HasParent, ParentConflicted, Poisoned;
            internal long KeepUntilMs;

            internal Node(Identity key, string path, int parentPid)
            {
                Key = key;
                Path = path;
                ParentPid = parentPid;
            }
        }

        private readonly object gate = new object();
        private readonly Dictionary<Identity, Node> nodes = new Dictionary<Identity, Node>();
        private readonly Dictionary<int, Identity> lastLive = new Dictionary<int, Identity>();
        private int session = -1;
        private long lastNowMs = -1;

        internal int RetainedNodeCount { get { lock (gate) return nodes.Count; } }

        internal void Clear()
        {
            lock (gate)
            {
                ClearLocked();
                session = -1;
                lastNowMs = -1;
            }
        }

        internal GameFamilyEvidence Capture(ProcessSnapshot snapshot,
            IList<GameProfile> profiles, int ownerSession, long nowMs)
        {
            lock (gate)
            {
                if (!SetContext(ownerSession, nowMs) || snapshot == null)
                    return GameFamilyEvidence.Empty;
                if (profiles == null || profiles.Count == 0 || snapshot.Count > MaxNodes
                    || profiles.Count > MaxNodes)
                {
                    ClearLocked();
                    return GameFamilyEvidence.Empty;
                }

                var ambiguous = new HashSet<int>();
                var byPid = new Dictionary<int, ProcEntry>();
                foreach (ProcEntry entry in snapshot.Entries)
                {
                    if (entry == null || entry.Pid <= 4) continue;
                    if (byPid.ContainsKey(entry.Pid)) ambiguous.Add(entry.Pid);
                    else byPid.Add(entry.Pid, entry);
                }
                PoisonPids(ambiguous);

                // Prune against this snapshot before admitting new nodes. The
                // short tail also covers starts still in ProcNotify's batch.
                lastLive.Clear();
                foreach (ProcEntry entry in byPid.Values)
                    if (!ambiguous.Contains(entry.Pid) && Usable(entry, ownerSession))
                        lastLive[entry.Pid] = new Identity(entry.Session, entry.Pid, entry.Creation);
                Prune(nowMs);

                var live = new Dictionary<int, Node>();
                foreach (ProcEntry entry in byPid.Values)
                {
                    if (!lastLive.ContainsKey(entry.Pid)) continue;
                    Node node;
                    if (!Observe(new Identity(entry.Session, entry.Pid, entry.Creation),
                            entry.Path, entry.ParentPid, nowMs, out node))
                    {
                        ClearLocked();
                        return GameFamilyEvidence.Empty;
                    }
                    if (node != null && !node.Poisoned) live.Add(entry.Pid, node);
                }

                foreach (Node child in live.Values)
                {
                    if (child.ParentPid <= 4 || ambiguous.Contains(child.ParentPid))
                    {
                        if (ambiguous.Contains(child.ParentPid)) BreakParent(child);
                        continue;
                    }
                    Node parent;
                    if (live.TryGetValue(child.ParentPid, out parent))
                    {
                        if (parent.Key.Creation < child.Key.Creation) Link(child, parent.Key);
                        // A new process reusing a dead parent's PID must not
                        // replace an already-proved old parent identity.
                        continue;
                    }
                    ProcEntry rawParent;
                    if (child.HasParent && byPid.TryGetValue(child.ParentPid, out rawParent)
                        && rawParent.Session == ownerSession && rawParent.Creation > 0
                        && rawParent.Creation <= child.Key.Creation
                        && rawParent.Creation != child.Parent.Creation)
                        BreakParent(child);
                }
                Prune(nowMs);
                return BuildEvidence(profiles, live);
            }
        }

        internal void ObserveEvents(ProcessChangeBatch batch, int ownerSession, long nowMs)
        {
            lock (gate)
            {
                if (!SetContext(ownerSession, nowMs) || batch == null) return;
                if (batch.Overflowed || batch.Changes.Length > MaxNodes)
                {
                    ClearLocked();
                    return;
                }
                Prune(nowMs);
                var starts = new Dictionary<int, ProcessChange>();
                var ambiguous = new HashSet<int>();
                foreach (ProcessChange change in batch.Changes)
                {
                    // Stop notifications have no verified creation identity.
                    // A fresh snapshot, not PID-only stop ordering, retires it.
                    if (change == null || change.Kind != ProcessChangeKind.Started || change.Pid <= 4)
                        continue;
                    if (starts.ContainsKey(change.Pid)) ambiguous.Add(change.Pid);
                    else starts.Add(change.Pid, change);
                }
                PoisonPids(ambiguous);

                var admitted = new Dictionary<int, Node>();
                foreach (ProcessChange change in starts.Values)
                {
                    if (ambiguous.Contains(change.Pid) || !Usable(change, ownerSession)) continue;
                    Node node;
                    if (!Observe(new Identity(change.Session, change.Pid, change.Creation),
                            change.Path, change.ParentPid, nowMs, out node))
                    {
                        ClearLocked();
                        return;
                    }
                    if (node != null && !node.Poisoned) admitted.Add(change.Pid, node);
                }

                // Two passes allow an entire short launcher/broker/renderer
                // chain in one coalesced batch without callback-order guesses.
                foreach (var pair in admitted)
                {
                    Node child = pair.Value;
                    ProcessChange change = starts[pair.Key];
                    if (child.ParentPid <= 4 || ambiguous.Contains(child.ParentPid))
                    {
                        if (ambiguous.Contains(child.ParentPid)) BreakParent(child);
                        continue;
                    }
                    Node batchParent;
                    admitted.TryGetValue(child.ParentPid, out batchParent);
                    if (change.ParentCreation > 0)
                    {
                        var parentKey = new Identity(ownerSession, child.ParentPid, change.ParentCreation);
                        Identity priorLive;
                        bool contradicts = batchParent != null
                            && batchParent.Key.Creation <= child.Key.Creation
                            && !batchParent.Key.Equals(parentKey);
                        contradicts |= lastLive.TryGetValue(child.ParentPid, out priorLive)
                            && priorLive.Creation <= child.Key.Creation && !priorLive.Equals(parentKey);
                        if (contradicts) BreakParent(child);
                        else Link(child, parentKey);
                    }
                    else if (change.ParentCreation == 0 && batchParent != null)
                        Link(child, batchParent.Key);
                    // ParentCreation==0 cannot resurrect a cached same-PID
                    // parent from an earlier batch or an unrelated lifetime.
                }
                Prune(nowMs);
            }
        }

        private bool SetContext(int ownerSession, long nowMs)
        {
            if (ownerSession < 0 || nowMs < 0)
            {
                ClearLocked();
                session = -1;
                lastNowMs = -1;
                return false;
            }
            if (ownerSession != session || lastNowMs > nowMs) ClearLocked();
            session = ownerSession;
            lastNowMs = nowMs;
            return true;
        }

        private void ClearLocked()
        {
            nodes.Clear();
            lastLive.Clear();
        }

        private bool Observe(Identity key, string path, int parentPid, long nowMs, out Node node)
        {
            if (nodes.TryGetValue(key, out node))
            {
                if (!string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase)
                    || (node.ParentPid > 0 && parentPid > 0 && node.ParentPid != parentPid))
                    node.Poisoned = true;
                else if (node.ParentPid <= 0 && parentPid > 0) node.ParentPid = parentPid;
            }
            else
            {
                if (nodes.Count >= MaxNodes) { node = null; return false; }
                node = new Node(key, path, parentPid);
                nodes.Add(key, node);
            }
            node.KeepUntilMs = nowMs > long.MaxValue - PendingEventRetentionMs
                ? long.MaxValue : nowMs + PendingEventRetentionMs;
            return true;
        }

        private void PoisonPids(HashSet<int> ambiguous)
        {
            if (ambiguous.Count == 0) return;
            foreach (Node node in nodes.Values)
                if (ambiguous.Contains(node.Key.Pid)) node.Poisoned = true;
        }

        private static void Link(Node child, Identity parent)
        {
            if (child.ParentConflicted || parent.Session != child.Key.Session
                || parent.Pid <= 4 || parent.Pid != child.ParentPid || parent.Pid == child.Key.Pid
                || parent.Creation <= 0 || parent.Creation >= child.Key.Creation) return;
            if (child.HasParent && !child.Parent.Equals(parent))
            {
                BreakParent(child);
                return;
            }
            child.Parent = parent;
            child.HasParent = true;
        }

        private static void BreakParent(Node child)
        {
            child.HasParent = false;
            child.ParentConflicted = true;
        }

        private void Prune(long nowMs)
        {
            var keep = new HashSet<Identity>();
            foreach (Identity key in lastLive.Values) KeepChain(key, keep);
            foreach (Node node in nodes.Values)
                if (node.KeepUntilMs >= nowMs) KeepChain(node.Key, keep);
            var remove = new List<Identity>();
            foreach (Identity key in nodes.Keys)
                if (!keep.Contains(key)) remove.Add(key);
            foreach (Identity key in remove) nodes.Remove(key);
        }

        private void KeepChain(Identity key, HashSet<Identity> keep)
        {
            Node node;
            for (int depth = 0; depth <= MaxAncestorDepth && nodes.TryGetValue(key, out node); depth++)
            {
                keep.Add(key);
                if (!node.HasParent) break;
                key = node.Parent;
            }
        }

        private GameFamilyEvidence BuildEvidence(IList<GameProfile> configured, Dictionary<int, Node> live)
        {
            var result = new Dictionary<GameFamilyProfileKey, Dictionary<int, GameFamilyEvidence.Member>>();
            int checks = 0, total = 0;
            foreach (GameProfile profile in configured)
            {
                if (profile == null || string.IsNullOrEmpty(profile.Id)) continue;
                var key = new GameFamilyProfileKey(profile);
                if (result.ContainsKey(key)) continue;
                string rootPrefix = key.CreateRootPrefix();
                var members = new Dictionary<int, GameFamilyEvidence.Member>();
                foreach (Node child in live.Values)
                {
                    Node node = child;
                    for (int depth = 0; depth <= MaxAncestorDepth; depth++)
                    {
                        if (++checks > MaxMembershipChecks) return GameFamilyEvidence.Empty;
                        if (node.Poisoned) break;
                        if (key.Owns(node.Path, rootPrefix))
                        {
                            if (++total > MaxMemberships) return GameFamilyEvidence.Empty;
                            members.Add(child.Key.Pid, new GameFamilyEvidence.Member(child.Key.Creation, child.Path));
                            break;
                        }
                        if (!node.HasParent || !nodes.TryGetValue(node.Parent, out node)) break;
                    }
                }
                if (members.Count > 0) result.Add(key, members);
            }
            return result.Count == 0 ? GameFamilyEvidence.Empty : new GameFamilyEvidence(result);
        }

        private static bool Usable(ProcEntry entry, int ownerSession)
        {
            return entry.Session == ownerSession && entry.Creation > 0
                && ImageIdentityUsable(entry.Name, entry.Path);
        }

        private static bool Usable(ProcessChange change, int ownerSession)
        {
            return change.Session == ownerSession && change.Creation > 0
                && ImageIdentityUsable(change.Name, change.Path);
        }

        private static bool ImageIdentityUsable(string name, string path)
        {
            return !string.IsNullOrEmpty(name) && CanonicalPath(path)
                && string.Equals(name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
                // The old detector removes these nodes before walking parents.
                // Historical edges must not bypass that same safety boundary.
                && !GameSessionDetector.ElectionVetoed(name, path);
        }

        internal static bool CanonicalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string volume = Path.GetPathRoot(path);
                return Path.IsPathRooted(path) && !string.IsNullOrEmpty(volume)
                    && volume.EndsWith("\\", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(Path.GetFileName(path))
                    && string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
