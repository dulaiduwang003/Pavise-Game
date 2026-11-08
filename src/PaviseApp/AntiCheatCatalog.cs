using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class AntiCheatCatalog
	{
		public static readonly AcGroup[] Groups = new AcGroup[9]
		{
			new AcGroup("ace", "ac.ace.n", false, new string[10] { "SGuard64", "SGuardSvc64", "ACE-Tray", "ACE-PC", "ACE-Helper", "ACE-Service64", "SGuard", "SGuardSvc", "AntiCheatExpert", "AntiCheatExpert.Service" }),
			new AcGroup("tp", "ac.tp.n", false, new string[6] { "TenSafe", "TenSafe_1", "TenSafe_2", "TASLogin", "TP3Helper", "TPHelper" }, AcCategory.RecordOnly),
			new AcGroup("vanguard", "Vanguard (Riot)", false, new string[2] { "vgc", "vgtray" }, AcCategory.ProtectOnly),
			new AcGroup("eac", "EasyAntiCheat (Epic)", false, new string[2] { "EasyAntiCheat", "EasyAntiCheat_EOS" }),
			new AcGroup("battleye", "BattlEye", false, new string[2] { "BEService", "BEService_x64" }),
			new AcGroup("eaac", "ac.eaac.n", false, new string[2] { "EAAntiCheat.GameService", "EAAntiCheat.GameServiceLauncher" }, AcCategory.RecordOnly),
			new AcGroup("gameguard", "nProtect GameGuard", false, new string[7] { "GameMon", "GameMon.des", "GameMon64", "GameMon64.des", "npggNT", "npggNT.des", "GameGuard" }, AcCategory.RecordOnly),
			new AcGroup("faceit", "ac.faceit.n", false, new string[3] { "faceitservice", "faceitclient", "faceit" }, AcCategory.RecordOnly),
			new AcGroup("neac", "ac.neac.n", false, new string[6] { "NeacSafe64", "NeacSafe", "nac", "NeacClient", "OWNeacClient", "NeacProtect" }, AcCategory.RecordOnly)
		};

		public static readonly AcProtectionGroup[] ProtectionOnlyGroups = new AcProtectionGroup[6]
		{
			new AcProtectionGroup("punkbuster", new string[2] { "PnkBstrA", "PnkBstrB" }, new string[2] { "PnkBstr", "PunkBuster" }, new string[1] { "PunkBuster" }),
			new AcProtectionGroup("ngs", new string[6] { "BlackCipher", "BlackCipher.aes", "BlackCipher64", "BlackCipher64.aes", "BlackXchg", "BlackXchg.aes" }, new string[3] { "BlackCipher", "BlackCall", "BlackXchg" }, new string[2] { "BlackCipher", "Nexon Game Security" }),
			new AcProtectionGroup("wellbia", new string[0], new string[3] { "XIGNCODE", "Uncheater", "ucldr_" }, new string[5] { "XIGNCODE", "XIGNCODE3", "UNCHEATER", "Wellbia", "Wellbia.com" }),
			new AcProtectionGroup("pwarena", new string[1] { "完美世界竞技平台" }, new string[1] { "完美世界竞技平台" }, new string[0]),
			new AcProtectionGroup("5e", new string[1] { "5EClient" }, new string[1] { "5EClient" }, new string[1] { "5EClient" }),
			new AcProtectionGroup("b5", new string[5] { "B5AntiCheat", "B5GameService", "B5GameServiceLoader", "B5esportsMain", "B5CSGO" }, new string[2] { "B5AntiCheat", "B5GameService" }, new string[0])
		};

		private static readonly string[] SharedDirectories = new string[9] { "AntiCheatExpert", "TenProtect", "Riot Vanguard", "EasyAntiCheat", "EasyAntiCheat_EOS", "BattlEye", "EA\\AC", "GameGuard", "FACEIT AC" };

		private static readonly HashSet<string> ProcessNames = BuildProcessNames();

		private static readonly string[] NameTokens = new string[12]
		{
			"anticheat", "anti-cheat", "sguard", "tensafe", "easyanticheat", "beservice", "battleye", "gameguard", "gamemon", "vgtray",
			"ace-helper", "ace-base"
		};

		public static bool IsKnownProcess(string name)
		{
			return ProcessNames.Contains(NormalizeName(name));
		}

		internal static AcGroup RecordOnlyGroupOf(string name)
		{
			string b = NormalizeName(name);
			AcGroup[] groups = Groups;
			foreach (AcGroup acGroup in groups)
			{
				if (!acGroup.RecordOnly)
				{
					continue;
				}
				string[] procs = acGroup.Procs;
				foreach (string a in procs)
				{
					if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
					{
						return acGroup;
					}
				}
			}
			return null;
		}

		private static HashSet<string> BuildProcessNames()
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			AcGroup[] groups = Groups;
			foreach (AcGroup acGroup in groups)
			{
				string[] procs = acGroup.Procs;
				foreach (string item in procs)
				{
					hashSet.Add(item);
				}
			}
			AcProtectionGroup[] protectionOnlyGroups = ProtectionOnlyGroups;
			foreach (AcProtectionGroup acProtectionGroup in protectionOnlyGroups)
			{
				string[] procs2 = acProtectionGroup.Procs;
				foreach (string item2 in procs2)
				{
					hashSet.Add(item2);
				}
			}
			return hashSet;
		}

		private static string NormalizeName(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return "";
			}
			if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return name;
			}
			return name.Substring(0, name.Length - 4);
		}

		internal static bool ContainsToken(string lowered)
		{
			if (string.IsNullOrEmpty(lowered))
			{
				return false;
			}
			string[] nameTokens = NameTokens;
			foreach (string value in nameTokens)
			{
				if (lowered.Contains(value))
				{
					return true;
				}
			}
			return false;
		}

		internal static bool IsAntiCheatLikeName(string name)
		{
			string name2 = NormalizeName(name);
			if (ProcessNames.Contains(name2) || ContainsToken(name2.ToLowerInvariant()))
			{
				return true;
			}
			AcProtectionGroup[] protectionOnlyGroups = ProtectionOnlyGroups;
			foreach (AcProtectionGroup acProtectionGroup in protectionOnlyGroups)
			{
				string[] prefixes = acProtectionGroup.Prefixes;
				foreach (string value in prefixes)
				{
					if (name2.StartsWith(value, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			return false;
		}

		internal static bool IsAntiCheatProcess(string name, string path)
		{
			if (!IsAntiCheatLikeName(name))
			{
				return IsAntiCheatPath(path);
			}
			return true;
		}

		internal static bool IsAntiCheatPath(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}
			string replaceResult = path.Replace('/', '\\');
			if (replaceResult.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
			{
				replaceResult = "\\\\" + replaceResult.Substring(8);
			}
			else if (replaceResult.StartsWith("\\\\?\\", StringComparison.Ordinal))
			{
				replaceResult = replaceResult.Substring(4);
			}
			if (replaceResult.StartsWith("\\\\.\\", StringComparison.Ordinal))
			{
				return false;
			}
			bool allConditionsMet = replaceResult.Length >= 3 && ((replaceResult[0] >= 'A' && replaceResult[0] <= 'Z') || (replaceResult[0] >= 'a' && replaceResult[0] <= 'z')) && replaceResult[1] == ':' && replaceResult[2] == '\\';
			bool startsWith = replaceResult.StartsWith("\\\\", StringComparison.Ordinal);
			if ((!allConditionsMet && !startsWith) || replaceResult.EndsWith("\\", StringComparison.Ordinal))
			{
				return false;
			}
			string[] replaceResultParts = replaceResult.Split(new char[1] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
			int isAntiCheatPathValue = (allConditionsMet ? 1 : 2);
			if (replaceResultParts.Length <= isAntiCheatPathValue)
			{
				return false;
			}
			string[] textEntries = replaceResultParts;
			foreach (string entryText in textEntries)
			{
				if (entryText == "." || entryText == "..")
				{
					return false;
				}
			}
			for (int j = isAntiCheatPathValue; j < replaceResultParts.Length - 1; j++)
			{
				string[] sharedDirectories = SharedDirectories;
				foreach (string directory in sharedDirectories)
				{
					if (MatchesDirectory(replaceResultParts, j, directory))
					{
						return true;
					}
				}
				AcProtectionGroup[] protectionOnlyGroups = ProtectionOnlyGroups;
				foreach (AcProtectionGroup acProtectionGroup in protectionOnlyGroups)
				{
					string[] directories = acProtectionGroup.Directories;
					foreach (string directory2 in directories)
					{
						if (MatchesDirectory(replaceResultParts, j, directory2))
						{
							return true;
						}
					}
				}
			}
			return false;
		}

		private static bool MatchesDirectory(string[] parts, int index, string directory)
		{
			int matchIndex = directory.IndexOf('\\');
			if (matchIndex < 0)
			{
				return string.Equals(parts[index], directory, StringComparison.OrdinalIgnoreCase);
			}
			if (index + 1 < parts.Length - 1 && string.Equals(parts[index], directory.Substring(0, matchIndex), StringComparison.OrdinalIgnoreCase))
			{
				return string.Equals(parts[index + 1], directory.Substring(matchIndex + 1), StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		internal static string[] DirectoryNamesForDisplay()
		{
			List<string> list = new List<string>(SharedDirectories);
			AcProtectionGroup[] protectionOnlyGroups = ProtectionOnlyGroups;
			foreach (AcProtectionGroup acProtectionGroup in protectionOnlyGroups)
			{
				list.AddRange(acProtectionGroup.Directories);
			}
			return list.ToArray();
		}

		internal static string[] NameTokensForDisplay()
		{
			return (string[])NameTokens.Clone();
		}
	}
}
