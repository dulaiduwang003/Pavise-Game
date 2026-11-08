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

		private const int MaxMemberships = 16384;

		private const int MaxMembershipChecks = 1048576;

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
				HashSet<int> hashSet = new HashSet<int>();
				Dictionary<int, ProcEntry> dictionary = new Dictionary<int, ProcEntry>();
				ProcEntry[] entries = snapshot.Entries;
				foreach (ProcEntry procEntry in entries)
				{
					if (procEntry != null && procEntry.Pid > 4)
					{
						if (dictionary.ContainsKey(procEntry.Pid))
						{
							hashSet.Add(procEntry.Pid);
						}
						else
						{
							dictionary.Add(procEntry.Pid, procEntry);
						}
					}
				}
				PoisonPids(hashSet);
				lastLive.Clear();
				foreach (ProcEntry value3 in dictionary.Values)
				{
					if (!hashSet.Contains(value3.Pid) && Usable(value3, ownerSession))
					{
						lastLive[value3.Pid] = new Identity(value3.Session, value3.Pid, value3.Creation);
					}
				}
				Prune(nowMs);
				Dictionary<int, Node> dictionary2 = new Dictionary<int, Node>();
				foreach (ProcEntry value4 in dictionary.Values)
				{
					if (lastLive.ContainsKey(value4.Pid))
					{
						Node node;
						if (!Observe(new Identity(value4.Session, value4.Pid, value4.Creation), value4.Path, value4.ParentPid, nowMs, out node))
						{
							ClearLocked();
							return GameFamilyEvidence.Empty;
						}
						if (node != null && !node.Poisoned)
						{
							dictionary2.Add(value4.Pid, node);
						}
					}
				}
				foreach (Node value5 in dictionary2.Values)
				{
					Node value;
					ProcEntry value2;
					if (value5.ParentPid <= 4 || hashSet.Contains(value5.ParentPid))
					{
						if (hashSet.Contains(value5.ParentPid))
						{
							BreakParent(value5);
						}
					}
					else if (dictionary2.TryGetValue(value5.ParentPid, out value))
					{
						if (value.Key.Creation < value5.Key.Creation)
						{
							Link(value5, value.Key);
						}
					}
					else if (value5.HasParent && dictionary.TryGetValue(value5.ParentPid, out value2) && value2.Session == ownerSession && value2.Creation > 0 && value2.Creation <= value5.Key.Creation && value2.Creation != value5.Parent.Creation)
					{
						BreakParent(value5);
					}
				}
				Prune(nowMs);
				return BuildEvidence(profiles, dictionary2);
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
				Dictionary<int, ProcessChange> dictionary = new Dictionary<int, ProcessChange>();
				HashSet<int> hashSet = new HashSet<int>();
				ProcessChange[] changes = batch.Changes;
				foreach (ProcessChange processChange in changes)
				{
					if (processChange != null && processChange.Kind == ProcessChangeKind.Started && processChange.Pid > 4)
					{
						if (dictionary.ContainsKey(processChange.Pid))
						{
							hashSet.Add(processChange.Pid);
						}
						else
						{
							dictionary.Add(processChange.Pid, processChange);
						}
					}
				}
				PoisonPids(hashSet);
				Dictionary<int, Node> dictionary2 = new Dictionary<int, Node>();
				foreach (ProcessChange value4 in dictionary.Values)
				{
					if (!hashSet.Contains(value4.Pid) && Usable(value4, ownerSession))
					{
						Node node;
						if (!Observe(new Identity(value4.Session, value4.Pid, value4.Creation), value4.Path, value4.ParentPid, nowMs, out node))
						{
							ClearLocked();
							return;
						}
						if (node != null && !node.Poisoned)
						{
							dictionary2.Add(value4.Pid, node);
						}
					}
				}
				foreach (KeyValuePair<int, Node> item in dictionary2)
				{
					Node value = item.Value;
					ProcessChange processChange2 = dictionary[item.Key];
					if (value.ParentPid <= 4 || hashSet.Contains(value.ParentPid))
					{
						if (hashSet.Contains(value.ParentPid))
						{
							BreakParent(value);
						}
						continue;
					}
					Node value2;
					dictionary2.TryGetValue(value.ParentPid, out value2);
					if (processChange2.ParentCreation > 0)
					{
						Identity identity = new Identity(ownerSession, value.ParentPid, processChange2.ParentCreation);
						bool allConditionsMet = value2 != null && value2.Key.Creation <= value.Key.Creation && !value2.Key.Equals(identity);
						Identity value3;
						if (allConditionsMet | (lastLive.TryGetValue(value.ParentPid, out value3) && value3.Creation <= value.Key.Creation && !value3.Equals(identity)))
						{
							BreakParent(value);
						}
						else
						{
							Link(value, identity);
						}
					}
					else if (processChange2.ParentCreation == 0 && value2 != null)
					{
						Link(value, value2.Key);
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
			HashSet<Identity> hashSet = new HashSet<Identity>();
			foreach (Identity value in lastLive.Values)
			{
				KeepChain(value, hashSet);
			}
			foreach (Node value2 in nodes.Values)
			{
				if (value2.KeepUntilMs >= nowMs)
				{
					KeepChain(value2.Key, hashSet);
				}
			}
			List<Identity> list = new List<Identity>();
			foreach (Identity key in nodes.Keys)
			{
				if (!hashSet.Contains(key))
				{
					list.Add(key);
				}
			}
			foreach (Identity item in list)
			{
				nodes.Remove(item);
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
			Dictionary<GameFamilyProfileKey, Dictionary<int, GameFamilyEvidence.Member>> dictionary = new Dictionary<GameFamilyProfileKey, Dictionary<int, GameFamilyEvidence.Member>>();
			int evidenceValue = 0;
			int evidenceValue2 = 0;
			foreach (GameProfile item in configured)
			{
				if (item == null || string.IsNullOrEmpty(item.Id))
				{
					continue;
				}
				GameFamilyProfileKey key = new GameFamilyProfileKey(item);
				if (dictionary.ContainsKey(key))
				{
					continue;
				}
				string rootPrefix = key.CreateRootPrefix();
				Dictionary<int, GameFamilyEvidence.Member> dictionary2 = new Dictionary<int, GameFamilyEvidence.Member>();
				foreach (Node value2 in live.Values)
				{
					Node value = value2;
					for (int i = 0; i <= 24; i++)
					{
						if (++evidenceValue > 1048576)
						{
							return GameFamilyEvidence.Empty;
						}
						if (value.Poisoned)
						{
							break;
						}
						if (key.Owns(value.Path, rootPrefix))
						{
							if (++evidenceValue2 > 16384)
							{
								return GameFamilyEvidence.Empty;
							}
							dictionary2.Add(value2.Key.Pid, new GameFamilyEvidence.Member(value2.Key.Creation, value2.Path));
							break;
						}
						if (!value.HasParent || !nodes.TryGetValue(value.Parent, out value))
						{
							break;
						}
					}
				}
				if (dictionary2.Count > 0)
				{
					dictionary.Add(key, dictionary2);
				}
			}
			if (dictionary.Count != 0)
			{
				return new GameFamilyEvidence(dictionary);
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
