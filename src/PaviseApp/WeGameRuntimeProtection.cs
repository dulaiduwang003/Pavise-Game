using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
	internal static class WeGameRuntimeProtection
	{
		private static readonly HashSet<string> RuntimeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wegame", "wegame_env", "wegameclient", "pallas", "rail", "tcls_core" };

		private static readonly HashSet<string> HelperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "browser", "crashpad_handler", "msedgewebview2", "QtWebEngineProcess", "cef_subprocess" };

		internal static bool IsRuntimeName(string name)
		{
			return RuntimeNames.Contains(WhitelistRule.NormalizeName(name));
		}

		internal static bool IsProtectedRuntimeName(string name)
		{
			if (IsRuntimeName(name))
			{
				return PlatformRuntimePolicy.Enabled("wegame");
			}
			return false;
		}

		internal static bool IsCriticalRuntime(string name, string path)
		{
			if (!IsProtectedRuntimeName(name))
			{
				return false;
			}
			if (string.IsNullOrEmpty(path))
			{
				return true;
			}
			string imagePath = WhitelistRule.NormalizeImagePath(path);
			if (imagePath.Length != 0)
			{
				return string.Equals(WhitelistRule.NormalizeName(name), WhitelistRule.NormalizeName(Path.GetFileName(imagePath)), StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		internal static HashSet<int> Collect(ProcessSnapshot snapshot, int selfPid, int ownerSession)
		{
			HashSet<int> hashSet = new HashSet<int>();
			if (snapshot == null || ownerSession < 0)
			{
				return hashSet;
			}
			Dictionary<int, ProcEntry> dictionary = new Dictionary<int, ProcEntry>();
			Dictionary<int, string> dictionary2 = new Dictionary<int, string>();
			HashSet<int> hashSet2 = new HashSet<int>();
			HashSet<string> hashSet3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			ProcEntry[] entries = snapshot.Entries;
			foreach (ProcEntry procEntry in entries)
			{
				if (procEntry == null)
				{
					continue;
				}
				if (!hashSet2.Add(procEntry.Pid))
				{
					dictionary.Remove(procEntry.Pid);
					dictionary2.Remove(procEntry.Pid);
				}
				else if (procEntry.Pid > 4 && procEntry.Pid != selfPid && procEntry.Session == ownerSession && procEntry.Creation > 0)
				{
					string imagePath = WhitelistRule.NormalizeImagePath(procEntry.Path);
					if (imagePath.Length != 0 && string.Equals(WhitelistRule.NormalizeName(procEntry.Name), WhitelistRule.NormalizeName(Path.GetFileName(imagePath)), StringComparison.OrdinalIgnoreCase))
					{
						dictionary[procEntry.Pid] = procEntry;
						dictionary2[procEntry.Pid] = imagePath;
					}
				}
			}
			foreach (ProcEntry value2 in dictionary.Values)
			{
				if (FamilyBoundary.CatalogProtected(value2.Pid, value2.Creation, value2.Name, value2.Path))
				{
					hashSet.Add(value2.Pid);
					string directoryName = Path.GetDirectoryName(dictionary2[value2.Pid]);
					// Only verified descendants inherit streaming protection; nearby browser/encoder processes do not.
					if (!StreamingToolCatalog.IsStreamingToolProcess(value2.Name, value2.Path) && FamilyBoundary.SafeFamilyDir(directoryName))
					{
						hashSet3.Add(directoryName);
					}
				}
			}
			foreach (ProcEntry value3 in dictionary.Values)
			{
				if (!HelperNames.Contains(WhitelistRule.NormalizeName(value3.Name)))
				{
					continue;
				}
				foreach (string item in hashSet3)
				{
					if (FamilyBoundary.UnderRoot(dictionary2[value3.Pid], item))
					{
						hashSet.Add(value3.Pid);
						break;
					}
				}
			}
			for (int j = 0; j < 24; j++)
			{
				bool collectReady = false;
				foreach (ProcEntry value4 in dictionary.Values)
				{
					ProcEntry value;
					if (!hashSet.Contains(value4.Pid) && hashSet.Contains(value4.ParentPid) && dictionary.TryGetValue(value4.ParentPid, out value) && value.Creation < value4.Creation)
					{
						hashSet.Add(value4.Pid);
						collectReady = true;
					}
				}
				if (!collectReady)
				{
					break;
				}
			}
			return hashSet;
		}
	}
}
