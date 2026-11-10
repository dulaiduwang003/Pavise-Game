using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed class GameFamilyHistory
    {
        private struct Identity : IEquatable<Identity>
        {
            internal int Session;

            internal int Pid;

            internal long Creation;

            internal Identity(int session, int pid, long creation)
            {
                Session = session;
                Pid = pid;
                Creation = creation;
            }

            public bool Equals(Identity other)
            {
                if (Session == other.Session && Pid == other.Pid)
                {
                    return Creation == other.Creation;
                }
                return false;
            }

            public override bool Equals(object value)
            {
                if (value is Identity)
                {
                    return Equals((Identity)value);
                }
                return false;
            }

            public override int GetHashCode()
            {
                return (((Session * 397) ^ Pid) * 397) ^ Creation.GetHashCode();
            }
        }

        private sealed class Node
        {
            internal readonly Identity Key;

            internal readonly string Path;

            internal int ParentPid;

            internal Identity Parent;

            internal bool HasParent;

            internal bool ParentConflicted;

            internal bool Poisoned;

            internal long KeepUntilMs;

            internal Node(Identity key, string path, int parentPid)
            {
                Key = key;
                Path = path;
                ParentPid = parentPid;
            }
        }

        internal const int MaxNodes = 4096;

        internal const int MaxAncestorDepth = 24;

        internal const int PendingEventRetentionMs = 5000;

        private readonly object gate = new object();

        private readonly Dictionary<Identity, Node> nodes = new Dictionary<Identity, Node>();

        private readonly Dictionary<int, Identity> lastLive = new Dictionary<int, Identity>();

        private int session = -1;

        private long lastNowMs = -1L;

        internal int RetainedNodeCount
        {
            get
            {
                lock (gate)
                {
                    return nodes.Count;
                }
            }
        }

        internal void Clear()
        {
            lock (gate)
            {
                ClearLocked();
                session = -1;
                lastNowMs = -1L;
            }
        }

        internal GameFamilyEvidence Capture(ProcessSnapshot snapshot, IList<GameProfile> profiles, int ownerSession, long nowMs)
        {
            lock (gate)
            {
                if (!SetContext(ownerSession, nowMs) || snapshot == null)
                {
                    return GameFamilyEvidence.Empty;
                }
                if (profiles == null || profiles.Count == 0 || snapshot.Count > 4096 || profiles.Count > 4096)
                {
                    ClearLocked();
                    return GameFamilyEvidence.Empty;
                }
                HashSet<int> pids = new HashSet<int>();
                Dictionary<int, ProcEntry> processEntriesById = new Dictionary<int, ProcEntry>();
                foreach (ProcEntry procEntry in snapshot.Entries)
                {
                    if (procEntry != null && procEntry.Pid > 4)
                    {
                        if (processEntriesById.ContainsKey(procEntry.Pid))
                        {
                            pids.Add(procEntry.Pid);
                        }
                        else
                        {
                            processEntriesById.Add(procEntry.Pid, procEntry);
                        }
                    }
                }
                PoisonPids(pids);
                lastLive.Clear();
                foreach (ProcEntry processSnapshot in processEntriesById.Values)
                {
                    if (!pids.Contains(processSnapshot.Pid) && Usable(processSnapshot, ownerSession))
                    {
                        lastLive[processSnapshot.Pid] = new Identity(processSnapshot.Session, processSnapshot.Pid, processSnapshot.Creation);
                    }
                }
                Prune(nowMs);
                Dictionary<int, Node> nodesById = new Dictionary<int, Node>();
                foreach (ProcEntry processSnapshot in processEntriesById.Values)
                {
                    if (lastLive.ContainsKey(processSnapshot.Pid))
                    {
                        Node node;
                        if (!Observe(new Identity(processSnapshot.Session, processSnapshot.Pid, processSnapshot.Creation), processSnapshot.Path, processSnapshot.ParentPid, nowMs, out node))
                        {
                            ClearLocked();
                            return GameFamilyEvidence.Empty;
                        }
                        if (node != null && !node.Poisoned)
                        {
                            nodesById.Add(processSnapshot.Pid, node);
                        }
                    }
                }
                foreach (Node childNode in nodesById.Values)
                {
                    Node currentNode;
                    ProcEntry processEntry;
                    if (childNode.ParentPid <= 4 || pids.Contains(childNode.ParentPid))
                    {
                        if (pids.Contains(childNode.ParentPid))
                        {
                            BreakParent(childNode);
                        }
                    }
                    else if (nodesById.TryGetValue(childNode.ParentPid, out currentNode))
                    {
                        if (currentNode.Key.Creation < childNode.Key.Creation)
                        {
                            Link(childNode, currentNode.Key);
                        }
                    }
                    else if (childNode.HasParent && processEntriesById.TryGetValue(childNode.ParentPid, out processEntry) && processEntry.Session == ownerSession && processEntry.Creation > 0 && processEntry.Creation <= childNode.Key.Creation && processEntry.Creation != childNode.Parent.Creation)
                    {
                        BreakParent(childNode);
                    }
                }
                Prune(nowMs);
                return BuildEvidence(profiles, nodesById);
            }
        }

        internal void ObserveEvents(ProcessChangeBatch batch, int ownerSession, long nowMs)
        {
            lock (gate)
            {
                if (!SetContext(ownerSession, nowMs) || batch == null)
                {
                    return;
                }
                if (batch.Overflowed || batch.Changes.Length > 4096)
                {
                    ClearLocked();
                    return;
                }
                Prune(nowMs);
                Dictionary<int, ProcessChange> processChangesById = new Dictionary<int, ProcessChange>();
                HashSet<int> pids = new HashSet<int>();
                foreach (ProcessChange processChange in batch.Changes)
                {
                    if (processChange != null && processChange.Kind == ProcessChangeKind.Started && processChange.Pid > 4)
                    {
                        if (processChangesById.ContainsKey(processChange.Pid))
                        {
                            pids.Add(processChange.Pid);
                        }
                        else
                        {
                            processChangesById.Add(processChange.Pid, processChange);
                        }
                    }
                }
                PoisonPids(pids);
                Dictionary<int, Node> nodesById = new Dictionary<int, Node>();
                foreach (ProcessChange processSnapshot in processChangesById.Values)
                {
                    if (!pids.Contains(processSnapshot.Pid) && Usable(processSnapshot, ownerSession))
                    {
                        Node node;
                        if (!Observe(new Identity(processSnapshot.Session, processSnapshot.Pid, processSnapshot.Creation), processSnapshot.Path, processSnapshot.ParentPid, nowMs, out node))
                        {
                            ClearLocked();
                            return;
                        }
                        if (node != null && !node.Poisoned)
                        {
                            nodesById.Add(processSnapshot.Pid, node);
                        }
                    }
                }
                foreach (KeyValuePair<int, Node> nodeEntry in nodesById)
                {
                    Node currentNode = nodeEntry.Value;
                    ProcessChange currentProcessChange = processChangesById[nodeEntry.Key];
                    if (currentNode.ParentPid <= 4 || pids.Contains(currentNode.ParentPid))
                    {
                        if (pids.Contains(currentNode.ParentPid))
                        {
                            BreakParent(currentNode);
                        }
                        continue;
                    }
                    Node candidateNode;
                    nodesById.TryGetValue(currentNode.ParentPid, out candidateNode);
                    if (currentProcessChange.ParentCreation > 0)
                    {
                        Identity identity = new Identity(ownerSession, currentNode.ParentPid, currentProcessChange.ParentCreation);
                        bool olderParent = candidateNode != null && candidateNode.Key.Creation <= currentNode.Key.Creation && !candidateNode.Key.Equals(identity);
                        Identity currentLastLive;
                        if (olderParent | (lastLive.TryGetValue(currentNode.ParentPid, out currentLastLive) && currentLastLive.Creation <= currentNode.Key.Creation && !currentLastLive.Equals(identity)))
                        {
                            BreakParent(currentNode);
                        }
                        else
                        {
                            Link(currentNode, identity);
                        }
                    }
                    else if (currentProcessChange.ParentCreation == 0 && candidateNode != null)
                    {
                        Link(currentNode, candidateNode.Key);
                    }
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
                lastNowMs = -1L;
                return false;
            }
            if (ownerSession != session || lastNowMs > nowMs)
            {
                ClearLocked();
            }
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
                if (!string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase) || (node.ParentPid > 0 && parentPid > 0 && node.ParentPid != parentPid))
                {
                    node.Poisoned = true;
                }
                else if (node.ParentPid <= 0 && parentPid > 0)
                {
                    node.ParentPid = parentPid;
                }
            }
            else
            {
                if (nodes.Count >= 4096)
                {
                    node = null;
                    return false;
                }
                node = new Node(key, path, parentPid);
                nodes.Add(key, node);
            }
            node.KeepUntilMs = ((nowMs > 9223372036854770807L) ? long.MaxValue : (nowMs + 5000));
            return true;
        }

        private void PoisonPids(HashSet<int> ambiguous)
        {
            if (ambiguous.Count == 0)
            {
                return;
            }
            foreach (Node value in nodes.Values)
            {
                if (ambiguous.Contains(value.Key.Pid))
                {
                    value.Poisoned = true;
                }
            }
        }

        private static void Link(Node child, Identity parent)
        {
            if (!child.ParentConflicted && parent.Session == child.Key.Session && parent.Pid > 4 && parent.Pid == child.ParentPid && parent.Pid != child.Key.Pid && parent.Creation > 0 && parent.Creation < child.Key.Creation)
            {
                if (child.HasParent && !child.Parent.Equals(parent))
                {
                    BreakParent(child);
                    return;
                }
                child.Parent = parent;
                child.HasParent = true;
            }
        }

        private static void BreakParent(Node child)
        {
            child.HasParent = false;
            child.ParentConflicted = true;
        }

        private void Prune(long nowMs)
        {
            HashSet<Identity> identitiesSet = new HashSet<Identity>();
            foreach (Identity liveIdentity in lastLive.Values)
            {
                KeepChain(liveIdentity, identitiesSet);
            }
            foreach (Node retainedNode in nodes.Values)
            {
                if (retainedNode.KeepUntilMs >= nowMs)
                {
                    KeepChain(retainedNode.Key, identitiesSet);
                }
            }
            List<Identity> keys = new List<Identity>();
            foreach (Identity key in nodes.Keys)
            {
                if (!identitiesSet.Contains(key))
                {
                    keys.Add(key);
                }
            }
            foreach (Identity currentKey in keys)
            {
                nodes.Remove(currentKey);
            }
        }

        private void KeepChain(Identity key, HashSet<Identity> keep)
        {
            for (int i = 0; i <= 24; i++)
            {
                Node value;
                if (!nodes.TryGetValue(key, out value))
                {
                    break;
                }
                keep.Add(key);
                if (!value.HasParent)
                {
                    break;
                }
                key = value.Parent;
            }
        }

        private GameFamilyEvidence BuildEvidence(IList<GameProfile> configured, Dictionary<int, Node> live)
        {
            Dictionary<GameFamilyProfileKey, Dictionary<int, GameFamilyEvidence.Member>> dictionariesById = new Dictionary<GameFamilyProfileKey, Dictionary<int, GameFamilyEvidence.Member>>();
            int evidenceValue = 0;
            int evidenceValue2 = 0;
            foreach (GameProfile currentConfigured in configured)
            {
                if (currentConfigured == null || string.IsNullOrEmpty(currentConfigured.Id))
                {
                    continue;
                }
                GameFamilyProfileKey key = new GameFamilyProfileKey(currentConfigured);
                if (dictionariesById.ContainsKey(key))
                {
                    continue;
                }
                string rootPrefix = key.CreateRootPrefix();
                Dictionary<int, GameFamilyEvidence.Member> membersById = new Dictionary<int, GameFamilyEvidence.Member>();
                foreach (Node rootNode in live.Values)
                {
                    Node node = rootNode;
                    for (int i = 0; i <= 24; i++)
                    {
                        if (++evidenceValue > 1048576)
                        {
                            return GameFamilyEvidence.Empty;
                        }
                        if (node.Poisoned)
                        {
                            break;
                        }
                        if (key.Owns(node.Path, rootPrefix))
                        {
                            if (++evidenceValue2 > 16384)
                            {
                                return GameFamilyEvidence.Empty;
                            }
                            membersById.Add(rootNode.Key.Pid, new GameFamilyEvidence.Member(rootNode.Key.Creation, rootNode.Path));
                            break;
                        }
                        if (!node.HasParent || !nodes.TryGetValue(node.Parent, out node))
                        {
                            break;
                        }
                    }
                }
                if (membersById.Count > 0)
                {
                    dictionariesById.Add(key, membersById);
                }
            }
            if (dictionariesById.Count != 0)
            {
                return new GameFamilyEvidence(dictionariesById);
            }
            return GameFamilyEvidence.Empty;
        }

        private static bool Usable(ProcEntry entry, int ownerSession)
        {
            if (entry.Session == ownerSession && entry.Creation > 0)
            {
                return ImageIdentityUsable(entry.Name, entry.Path);
            }
            return false;
        }

        private static bool Usable(ProcessChange change, int ownerSession)
        {
            if (change.Session == ownerSession && change.Creation > 0)
            {
                return ImageIdentityUsable(change.Name, change.Path);
            }
            return false;
        }

        private static bool ImageIdentityUsable(string name, string path)
        {
            if (!string.IsNullOrEmpty(name) && CanonicalPath(path) && string.Equals(name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
            {
                return !GameSessionDetector.ElectionVetoed(name, path);
            }
            return false;
        }

        internal static bool CanonicalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            try
            {
                string pathRoot = Path.GetPathRoot(path);
                return Path.IsPathRooted(path) && !string.IsNullOrEmpty(pathRoot) && pathRoot.EndsWith("\\", StringComparison.Ordinal) && !string.IsNullOrEmpty(Path.GetFileName(path)) && string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
