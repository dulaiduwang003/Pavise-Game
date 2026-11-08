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

		private const int CatalogVerdictCap = 8192;

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
			int num = PeripheralVendorProbe.CurrentGeneration();
			int num2 = SecurityProductCatalog.CurrentGeneration();
			int mask = PlatformRuntimePolicy.Mask;
			if (creation > 0)
			{
				lock (catalogSync)
				{
					CatalogVerdict value;
					if (catalogVerdicts.TryGetValue(pid, out value) && value.Creation == creation && value.VendorGeneration == num && value.SecurityGeneration == num2 && value.PlatformMask == mask && string.Equals(value.Name, name, StringComparison.Ordinal) && string.Equals(value.Path, path, StringComparison.Ordinal))
					{
						return value.Protected;
					}
				}
			}
			bool flag = AntiCheatCatalog.IsAntiCheatProcess(name, path) || WeGameRuntimeProtection.IsCriticalRuntime(name, path) || NetAcceleratorCatalog.IsAcceleratorLikeName(name) || NetAcceleratorCatalog.IsAcceleratorLikePath(path) || SecurityProductCatalog.IsSecurityProduct(path) || PeripheralCatalog.IsInputChainProcess(name, path) || HardwareControlCatalog.IsHardwareControlProcess(name) || StreamingToolCatalog.IsStreamingToolProcess(name, path);
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
						VendorGeneration = num,
						SecurityGeneration = num2,
						PlatformMask = mask,
						Protected = flag
					};
				}
			}
			return flag;
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
			int num = root.Length;
			while (num > 0 && root[num - 1] == '\\')
			{
				num--;
			}
			if (num == 0 || path.Length <= num)
			{
				return false;
			}
			if (path[num] != '\\')
			{
				return false;
			}
			return string.Compare(path, 0, root, 0, num, StringComparison.OrdinalIgnoreCase) == 0;
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
			// These backends create game processes and carry session/network work.
			// Their UI renderers remain independently eligible for family control.
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
					string executable = WhitelistRule.NormalizeImagePath(item.ExecutablePath);
					string learned = WhitelistRule.NormalizeImagePath(item.LearnedExecutablePath);
					if (executable.Length > 0)
					{
						hashSet2.Add(executable);
					}
					if (learned.Length > 0)
					{
						hashSet2.Add(learned);
					}
					if (exactExecutablesOnly && forceFamilyProtection && executable.Length == 0 && learned.Length == 0)
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
			Dictionary<int, int> dictionary = exactExecutablesOnly ? null : new Dictionary<int, int>();
			HashSet<int> hashSet3 = new HashSet<int>();
			ProcEntry[] entries = snapshot.Entries;
			foreach (ProcEntry procEntry in entries)
			{
				if (procEntry.Pid <= 4 || procEntry.Pid == selfPid || procEntry.Session != ownerSession || procEntry.Creation <= 0)
				{
					continue;
				}
				if ((!string.IsNullOrEmpty(procEntry.Path) && (hashSet2.Contains(WhitelistRule.NormalizeImagePath(procEntry.Path)) || LibraryRootOf(procEntry.Path, list) != null))
					|| legacyGameNames.Contains(WhitelistRule.NormalizeName(procEntry.Name)))
				{
					hashSet3.Add(procEntry.Pid);
				}
				if (!exactExecutablesOnly && familyEvidence != null)
				{
					foreach (GameProfile item2 in configured)
					{
						if (item2 != null && (forceFamilyProtection || FamilyExemptFor(item2)) && familyEvidence.Contains(item2, procEntry.Pid, procEntry.Creation, procEntry.Path))
						{
							hashSet3.Add(procEntry.Pid);
							break;
						}
					}
				}
				if (!exactExecutablesOnly)
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

		internal static HashSet<int> ExpandUserFacingFamily(Dictionary<int, int> parents, Dictionary<int, string> names, HashSet<int> roots)
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
			bool flag;
			do
			{
				flag = false;
				if (parents == null || names == null)
				{
					break;
				}
				foreach (KeyValuePair<int, int> parent in parents)
				{
					string value2;
					if (!hashSet.Contains(parent.Key) && hashSet.Contains(parent.Value) && names.TryGetValue(parent.Value, out value2) && !SystemProcessCatalog.IsShellProcess(value2))
					{
						hashSet.Add(parent.Key);
						flag = true;
					}
				}
			}
			while (flag);
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
			bool flag = SafeFamilyDir(profile.Root);
			HashSet<int> hashSet2 = new HashSet<int>();
			Dictionary<int, int> dictionary2 = new Dictionary<int, int>();
			foreach (ProcEntry value2 in dictionary.Values)
			{
				if (string.Equals(value2.Path, profile.ExecutablePath, StringComparison.OrdinalIgnoreCase) || string.Equals(value2.Path, profile.LearnedExecutablePath, StringComparison.OrdinalIgnoreCase) || (flag && UnderRoot(value2.Path, profile.Root)) || (familyEvidence != null && familyEvidence.Contains(profile, value2.Pid, value2.Creation, value2.Path)))
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
			string text = dir.TrimEnd('\\');
			if (text.Length <= 2)
			{
				return false;
			}
			Environment.SpecialFolder[] unsafeRoots = UnsafeRoots;
			foreach (Environment.SpecialFolder folder in unsafeRoots)
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
				if (!string.IsNullOrEmpty(folderPath) && string.Equals(text, folderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
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
				int num = key;
				for (int i = 0; i < maxDepth; i++)
				{
					int value;
					if (!parents.TryGetValue(num, out value))
					{
						break;
					}
					if (value <= 4)
					{
						break;
					}
					if (value == num)
					{
						break;
					}
					if (!ParentNotNewer(creations, value, num))
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
					num = value;
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
			int num = startPid;
			for (int i = 0; i < maxHops; i++)
			{
				int value;
				if (!parents.TryGetValue(num, out value))
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
				if (!ParentNotNewer(creations, value, num))
				{
					break;
				}
				hashSet.Add(value);
				num = value;
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
}
