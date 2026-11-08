using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
	internal static class PlatformRuntimePolicy
	{
		internal const string RemovedKey = "PlatformRuntimeRemovedV1";

		internal const string RulePrefix = "platform:";

		internal static readonly string[] Ids = new string[1] { "steam" };

		private static readonly object gate = new object();

		private static int generation = -1;

		private static int removed;

		private static readonly HashSet<string> SteamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steam", "steamwebhelper", "gameoverlayui" };

		private static readonly HashSet<string> Helpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser", "crashpad_handler", "msedgewebview2", "QtWebEngineProcess", "cef_subprocess" };

		internal static int Mask
		{
			get
			{
				lock (gate)
				{
					int mutationGeneration = Settings.MutationGeneration;
					if (generation == mutationGeneration)
					{
						return removed;
					}
					removed = 0;
					string[] loadStrParts = Settings.LoadStr("PlatformRuntimeRemovedV1", "").Split(';');
					foreach (string id in loadStrParts)
					{
						removed |= Bit(id);
					}
					generation = mutationGeneration;
					return removed;
				}
			}
		}

		private static int Bit(string id)
		{
			// Keep Steam's existing bit; legacy WeGame removal settings are ignored.
			if (string.Equals(id, "steam", StringComparison.OrdinalIgnoreCase))
			{
				return 2;
			}
			return 0;
		}

		internal static bool Enabled(string id)
		{
			int bitResult = Bit(id);
			if (bitResult != 0)
			{
				return (Mask & bitResult) == 0;
			}
			return false;
		}

		internal static string DisplayName(string id)
		{
			if (!(id == "wegame"))
			{
				return "Steam";
			}
			return "WeGame";
		}

		internal static string RuntimePlatform(string name)
		{
			// Platform classification prevents family inheritance and renderer selection; WeGame has no built-in protection.
			if (WeGameRuntimeProtection.IsRuntimeName(name))
			{
				return "wegame";
			}
			if (!SteamNames.Contains(WhitelistRule.NormalizeName(name)))
			{
				return null;
			}
			return "steam";
		}

		internal static bool TryRemove(string id)
		{
			lock (gate)
			{
				int bitResult = Bit(id);
				if (bitResult == 0)
				{
					return false;
				}
				return Save(Mask | bitResult);
			}
		}

		internal static bool TryRestore()
		{
			lock (gate)
			{
				return Save(0);
			}
		}

		private static bool Save(int mask)
		{
			List<string> list = new List<string>();
			string[] ids = Ids;
			foreach (string entryText in ids)
			{
				if ((mask & Bit(entryText)) != 0)
				{
					list.Add(entryText);
				}
			}
			return Settings.SaveStr("PlatformRuntimeRemovedV1", string.Join(";", list.ToArray()));
		}

		internal static string ProcessNames(string id)
		{
			if (!(id == "wegame"))
			{
				return "Steam · SteamWebHelper · GameOverlayUI";
			}
			return "WeGame · WeGame_env · WeGameClient · Pallas · Rail · TCLS_core";
		}

		internal static Dictionary<int, PlatformProcessOwner> Collect(ProcessSnapshot snapshot, int self, int session, HashSet<int> gameRoots, Dictionary<int, PlatformProcessOwner> retained)
		{
			Dictionary<int, PlatformProcessOwner> dictionary = new Dictionary<int, PlatformProcessOwner>();
			if (snapshot == null || session < 0)
			{
				return dictionary;
			}
			Dictionary<int, ProcEntry> dictionary2 = new Dictionary<int, ProcEntry>();
			HashSet<int> hashSet = new HashSet<int>();
			Dictionary<string, string> dictionary3 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			ProcEntry[] entries = snapshot.Entries;
			foreach (ProcEntry procEntry in entries)
			{
				if (procEntry == null)
				{
					continue;
				}
				if (!hashSet.Add(procEntry.Pid))
				{
					dictionary2.Remove(procEntry.Pid);
				}
				else if (procEntry.Pid > 4 && procEntry.Pid != self && procEntry.Session == session && procEntry.Creation > 0)
				{
					string imagePath = WhitelistRule.NormalizeImagePath(procEntry.Path);
					if (imagePath.Length != 0 && string.Equals(WhitelistRule.NormalizeName(procEntry.Name), WhitelistRule.NormalizeName(Path.GetFileName(imagePath)), StringComparison.OrdinalIgnoreCase))
					{
						dictionary2[procEntry.Pid] = procEntry;
					}
				}
			}
			HashSet<int> hashSet2 = new HashSet<int>(gameRoots ?? new HashSet<int>());
			for (int j = 0; j < 24; j++)
			{
				bool collectReady = false;
				foreach (ProcEntry value5 in dictionary2.Values)
				{
					ProcEntry value;
					if (!hashSet2.Contains(value5.Pid) && hashSet2.Contains(value5.ParentPid) && RuntimePlatform(value5.Name) == null && dictionary2.TryGetValue(value5.ParentPid, out value) && value.Creation < value5.Creation)
					{
						hashSet2.Add(value5.Pid);
						collectReady = true;
					}
				}
				if (!collectReady)
				{
					break;
				}
			}
			foreach (int item in hashSet2)
			{
				dictionary2.Remove(item);
			}
			foreach (ProcEntry value6 in dictionary2.Values)
			{
				string runtimePlatformResult = RuntimePlatform(value6.Name);
				if (runtimePlatformResult != null)
				{
					dictionary[value6.Pid] = Owner(value6, runtimePlatformResult);
					string directoryName = Path.GetDirectoryName(WhitelistRule.NormalizeImagePath(value6.Path));
					if (FamilyBoundary.SafeFamilyDir(directoryName) && (runtimePlatformResult != "steam" || string.Equals(WhitelistRule.NormalizeName(value6.Name), "steam", StringComparison.OrdinalIgnoreCase)))
					{
						dictionary3[directoryName] = runtimePlatformResult;
					}
				}
				PlatformProcessOwner value2;
				if (retained != null && retained.TryGetValue(value6.Pid, out value2) && value2.Creation == value6.Creation && string.Equals(value2.Name, value6.Name, StringComparison.OrdinalIgnoreCase) && WhitelistRule.PathEquals(value2.Path, value6.Path))
				{
					dictionary[value6.Pid] = value2;
				}
			}
			foreach (ProcEntry value7 in dictionary2.Values)
			{
				if (dictionary.ContainsKey(value7.Pid) || !Helpers.Contains(WhitelistRule.NormalizeName(value7.Name)))
				{
					continue;
				}
				foreach (KeyValuePair<string, string> item2 in dictionary3)
				{
					if (FamilyBoundary.UnderRoot(WhitelistRule.NormalizeImagePath(value7.Path), item2.Key))
					{
						dictionary[value7.Pid] = Owner(value7, item2.Value);
						break;
					}
				}
			}
			for (int k = 0; k < 24; k++)
			{
				bool collectReady2 = false;
				foreach (ProcEntry value8 in dictionary2.Values)
				{
					PlatformProcessOwner value3;
					ProcEntry value4;
					if (!dictionary.ContainsKey(value8.Pid) && dictionary.TryGetValue(value8.ParentPid, out value3) && dictionary2.TryGetValue(value8.ParentPid, out value4) && value4.Creation < value8.Creation)
					{
						dictionary[value8.Pid] = Owner(value8, value3.Platform);
						collectReady2 = true;
					}
				}
				if (!collectReady2)
				{
					break;
				}
			}
			return dictionary;
		}

		private static PlatformProcessOwner Owner(ProcEntry p, string id)
		{
			PlatformProcessOwner platformProcessOwner = new PlatformProcessOwner();
			platformProcessOwner.Platform = id;
			platformProcessOwner.Creation = p.Creation;
			platformProcessOwner.Name = p.Name;
			platformProcessOwner.Path = p.Path;
			return platformProcessOwner;
		}
	}
}
