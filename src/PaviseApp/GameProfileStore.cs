using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal sealed class GameProfileStore
	{
		private sealed class ProfileCommitBusyException : IOException
		{
			internal ProfileCommitBusyException(IOException inner)
				: base("Profile commit busy", inner)
			{
			}
		}

		internal const string FileName = "Pavise.profiles.dat";

		private const string HeaderV5 = "PAVISE_PROFILES_V5";

		private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

		private readonly string path;

		private readonly object saveSync = new object();

		private bool loadFailed;

		private int saveFailed;

		private int retryableSaveFailure;

		private int saveCanceled;

		public bool LoadFailed
		{
			get
			{
				return loadFailed;
			}
		}

		public bool SaveFailed
		{
			get
			{
				return Interlocked.CompareExchange(ref saveFailed, 0, 0) != 0;
			}
		}

		internal bool RetryableSaveFailure
		{
			get
			{
				return Volatile.Read(ref retryableSaveFailure) != 0;
			}
		}

		internal bool SaveCanceled
		{
			get
			{
				return Volatile.Read(ref saveCanceled) != 0;
			}
		}

		public GameProfileStore(string dir)
		{
			path = Path.Combine(dir, "Pavise.profiles.dat");
		}

		public List<GameProfile> LoadProfiles()
		{
			return LoadProfiles(true);
		}

		internal List<GameProfile> LoadProfiles(bool createIfMissing)
		{
			List<GameProfile> list = Load();
			if (loadFailed)
			{
				Save(list);
				return new List<GameProfile>();
			}
			if (createIfMissing && !File.Exists(path))
			{
				Save(list);
			}
			return list;
		}

		public bool Save(IList<GameProfile> profiles)
		{
			return Save(profiles, null);
		}

		internal bool Save(IList<GameProfile> profiles, Func<bool> canCommit)
		{
			lock (saveSync)
			{
				return SaveCore(profiles, canCommit);
			}
		}

		private bool SaveCore(IList<GameProfile> profiles, Func<bool> canCommit)
		{
			if (SaveFailed)
			{
				return false;
			}
			Interlocked.Exchange(ref retryableSaveFailure, 0);
			Interlocked.Exchange(ref saveCanceled, 0);
			if (loadFailed)
			{
				Logger.Log(Lang.T("log.gameprofiles.1"));
				Interlocked.Exchange(ref saveFailed, 1);
				return false;
			}
			try
			{
				byte[] snapshot = SnapshotBytes(profiles);
				if (!CommitStrict(snapshot, canCommit))
				{
					Interlocked.Exchange(ref saveCanceled, 1);
					return false;
				}
				return true;
			}
			catch (ProfileCommitBusyException ex)
			{
				Interlocked.Exchange(ref retryableSaveFailure, 1);
				Logger.LogFailure(Lang.T("log.gameprofiles.busy"), ex.InnerException);
				return false;
			}
			catch (Exception error)
			{
				MarkSaveFailed(error);
				return false;
			}
		}

		internal void MarkSaveFailed(Exception error)
		{
			Interlocked.Exchange(ref saveFailed, 1);
			Logger.LogFailure(Lang.T("log.gameprofiles.3"), error);
		}

		internal static byte[] SnapshotBytes(IList<GameProfile> profiles)
		{
			ValidateProfiles(profiles);
			List<string> list = new List<string>();
			List<string> list2 = new List<string>();
			List<string> list3 = new List<string>();
			List<string> list4 = new List<string>();
			list.Add("PAVISE_PROFILES_V5");
			foreach (GameProfile profile in profiles)
			{
				list.Add("P|" + B64(profile.Id) + "|" + B64(profile.Name) + "|" + B64(profile.Root) + "|" + B64(profile.ExecutablePath) + "|" + B64(Join(profile.Entries)));
				if (!string.IsNullOrEmpty(profile.LearnedExecutablePath))
				{
					list2.Add("L|" + B64(profile.Id) + "|" + B64(profile.LearnedExecutablePath));
				}
				if (profile.ForceTrigger)
				{
					list3.Add("F|" + B64(profile.Id));
				}
				foreach (KeyValuePair<string, string> @override in profile.Overrides)
				{
					list4.Add("O|" + B64(profile.Id) + "|" + B64(@override.Key) + "|" + B64(@override.Value));
				}
			}
			list.AddRange(list2);
			list.AddRange(list3);
			list.AddRange(list4);
			return StrictUtf8.GetBytes(string.Join(Environment.NewLine, list.ToArray()) + Environment.NewLine);
		}

		private static bool CommitAllowed(Func<bool> canCommit)
		{
			if (canCommit == null)
			{
				return true;
			}
			try
			{
				return canCommit();
			}
			catch (Exception ex)
			{
				Logger.Warn("Profile commit eligibility check failed: " + ex.Message);
				return false;
			}
		}

		internal static bool IsRetryableReplaceError(IOException error)
		{
			uint hResult = (uint)error.HResult;
			if ((hResult & 0xFFFF0000u) != 2147942400u)
			{
				return false;
			}
			uint hResultMask = hResult & 0xFFFFu;
			if (hResultMask != 32 && hResultMask != 33)
			{
				return hResultMask == 1175;
			}
			return true;
		}

		private bool CommitStrict(byte[] snapshot, Func<bool> canCommit)
		{
			string sourceFileName = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				if (!CommitAllowed(canCommit))
				{
					return false;
				}
				using (FileStream fileStream = new FileStream(sourceFileName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					fileStream.Write(snapshot, 0, snapshot.Length);
					fileStream.Flush(true);
				}
				if (!File.Exists(path))
				{
					if (!CommitAllowed(canCommit))
					{
						return false;
					}
					File.Move(sourceFileName, path);
				}
				else
				{
					int[] integerValues = new int[4] { 25, 50, 100, 200 };
					int processedCount = 0;
					while (true)
					{
						try
						{
							if (!CommitAllowed(canCommit))
							{
								return false;
							}
							File.Replace(sourceFileName, path, null);
						}
						catch (IOException ex)
						{
							if (!IsRetryableReplaceError(ex) || !File.Exists(sourceFileName) || !File.Exists(path))
							{
								throw;
							}
							if (processedCount == integerValues.Length)
							{
								throw new ProfileCommitBusyException(ex);
							}
							Thread.Sleep(integerValues[processedCount]);
							goto CountMatchingEntry;
						}
						break;
						CountMatchingEntry:
						processedCount++;
					}
				}
				return true;
			}
			finally
			{
				try
				{
					if (File.Exists(sourceFileName))
					{
						File.Delete(sourceFileName);
					}
				}
				catch
				{
				}
			}
		}

		private static void ValidateProfiles(IList<GameProfile> profiles)
		{
			if (profiles == null)
			{
				throw new FormatException("profiles null");
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (GameProfile profile in profiles)
			{
				if (profile == null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
				{
					throw new FormatException("profile identity missing");
				}
				if (!hashSet.Add(profile.Id))
				{
					throw new FormatException("duplicate profile id");
				}
				string item = IdentityKey(profile.Id, profile.Root, profile.ExecutablePath);
				if (!hashSet2.Add(item))
				{
					throw new FormatException("duplicate profile identity");
				}
				ValidateRootPath(profile.Root);
				ValidateExecutablePath(profile.ExecutablePath);
				ValidateExecutablePath(profile.LearnedExecutablePath);
				if (profile.ForceTrigger && string.IsNullOrEmpty(profile.PreferredExecutablePath))
				{
					throw new FormatException("forced profile has no executable");
				}
				foreach (string entry in profile.Entries)
				{
					if (string.IsNullOrWhiteSpace(entry) || entry.IndexOf('\r') >= 0 || entry.IndexOf('\n') >= 0)
					{
						throw new FormatException("invalid profile entry");
					}
				}
				foreach (KeyValuePair<string, string> @override in profile.Overrides)
				{
					if (GameProfile.IsRetiredOverrideKey(@override.Key))
					{
						throw new FormatException("retired override key in live profile");
					}
					string canonicalResult = PolicyCatalog.Canonical(@override.Key, @override.Value);
					if (canonicalResult == null || !string.Equals(canonicalResult, @override.Value, StringComparison.Ordinal))
					{
						throw new FormatException("invalid profile override");
					}
				}
			}
		}

		private static void ValidateRootPath(string value)
		{
			if (value != null)
			{
				string root = NormalizeRoot(value);
				if (root == null || !string.Equals(root, value, StringComparison.Ordinal))
				{
					throw new FormatException("invalid profile root");
				}
			}
		}

		private static void ValidateExecutablePath(string value)
		{
			if (value != null)
			{
				string path2 = NormalizePath(value);
				if (path2 == null || !string.Equals(path2, value, StringComparison.Ordinal))
				{
					throw new FormatException("invalid profile executable path");
				}
			}
		}

		private static string IdentityKey(string id, string root, string executablePath)
		{
			if (!string.IsNullOrEmpty(executablePath))
			{
				return "E|" + executablePath;
			}
			if (!string.IsNullOrEmpty(root))
			{
				return "R|" + root;
			}
			return "I|" + id;
		}

		private List<GameProfile> Load()
		{
			List<GameProfile> list = new List<GameProfile>();
			try
			{
				if (!File.Exists(path))
				{
					return list;
				}
				string[] allLines = File.ReadAllLines(path, StrictUtf8);
				if (allLines.Length == 0 || allLines[0] != "PAVISE_PROFILES_V5")
				{
					throw new FormatException("invalid profile header");
				}
				Dictionary<string, GameProfile> dictionary = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				Dictionary<string, string> dictionary2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				Dictionary<string, Dictionary<string, string>> dictionary3 = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
				int processedCount = 0;
				for (int i = 1; i < allLines.Length; i++)
				{
					if (allLines[i].Length == 0)
					{
						throw new FormatException("empty profile record");
					}
					string[] parts = allLines[i].Split('|');
					if (parts[0] == "P")
					{
						if (parts.Length != 6)
						{
							throw new FormatException("invalid P record");
						}
						string idValue = Decode(parts[1]);
						string nameValue = Decode(parts[2]);
						string rootValue = NullIfEmpty(Decode(parts[3]));
						string executablePathValue = NullIfEmpty(Decode(parts[4]));
						if (string.IsNullOrWhiteSpace(idValue) || string.IsNullOrWhiteSpace(nameValue))
						{
							throw new FormatException("profile identity missing");
						}
						ValidateRootPath(rootValue);
						ValidateExecutablePath(executablePathValue);
						GameProfile gameProfile = new GameProfile();
						gameProfile.Id = idValue;
						gameProfile.Name = nameValue;
						gameProfile.Root = rootValue;
						gameProfile.ExecutablePath = executablePathValue;
						GameProfile gameProfile2 = gameProfile;
						ParseEntries(gameProfile2.Entries, Decode(parts[5]));
						if (dictionary.ContainsKey(idValue))
						{
							throw new FormatException("duplicate profile id");
						}
						if (!hashSet.Add(IdentityKey(idValue, rootValue, executablePathValue)))
						{
							throw new FormatException("duplicate profile identity");
						}
						dictionary.Add(idValue, gameProfile2);
						list.Add(gameProfile2);
						continue;
					}
					if (parts[0] == "L")
					{
						if (parts.Length != 3)
						{
							throw new FormatException("invalid L record");
						}
						string decodeResult = Decode(parts[1]);
						string nullIfEmptyResult = NullIfEmpty(Decode(parts[2]));
						if (string.IsNullOrWhiteSpace(decodeResult) || nullIfEmptyResult == null)
						{
							throw new FormatException("invalid L value");
						}
						ValidateExecutablePath(nullIfEmptyResult);
						if (dictionary2.ContainsKey(decodeResult))
						{
							throw new FormatException("duplicate L record");
						}
						dictionary2.Add(decodeResult, nullIfEmptyResult);
						continue;
					}
					if (parts[0] == "F")
					{
						if (parts.Length != 2)
						{
							throw new FormatException("invalid F record");
						}
						string decodeResult2 = Decode(parts[1]);
						if (string.IsNullOrWhiteSpace(decodeResult2) || !hashSet2.Add(decodeResult2))
						{
							throw new FormatException("invalid or duplicate F record");
						}
						continue;
					}
					if (parts[0] == "O")
					{
						if (parts.Length != 4)
						{
							throw new FormatException("invalid O record");
						}
						string decodeResult3 = Decode(parts[1]);
						string decodeResult4 = Decode(parts[2]);
						string decodeResult5 = Decode(parts[3]);
						if (string.IsNullOrWhiteSpace(decodeResult3) || string.IsNullOrEmpty(decodeResult4))
						{
							throw new FormatException("invalid O value");
						}
						if (!ThreadBoostSelection.IsLegacy(decodeResult4) && GameProfile.IsRetiredOverrideKey(decodeResult4))
						{
							if (PerformancePolicy.IsRetiredSceneRule(decodeResult4))
							{
								processedCount++;
							}
							continue;
						}
						string canonicalResult = ((!ThreadBoostSelection.IsLegacy(decodeResult4)) ? PolicyCatalog.Canonical(decodeResult4, decodeResult5) : ((decodeResult5 == "0" || decodeResult5 == "1") ? decodeResult5 : null));
						if (canonicalResult == null || !string.Equals(canonicalResult, decodeResult5, StringComparison.Ordinal))
						{
							throw new FormatException("invalid O value");
						}
						Dictionary<string, string> value;
						if (!dictionary3.TryGetValue(decodeResult3, out value))
						{
							value = (dictionary3[decodeResult3] = new Dictionary<string, string>(StringComparer.Ordinal));
						}
						if (value.ContainsKey(decodeResult4))
						{
							throw new FormatException("duplicate O record");
						}
						value.Add(decodeResult4, decodeResult5);
						continue;
					}
					throw new FormatException("unknown profile record");
				}
				foreach (KeyValuePair<string, string> item in dictionary2)
				{
					GameProfile value2;
					if (!dictionary.TryGetValue(item.Key, out value2))
					{
						throw new FormatException("dangling L record");
					}
					value2.LearnedExecutablePath = item.Value;
				}
				foreach (string item2 in hashSet2)
				{
					GameProfile value3;
					if (!dictionary.TryGetValue(item2, out value3))
					{
						throw new FormatException("dangling F record");
					}
					value3.ForceTrigger = true;
				}
				foreach (KeyValuePair<string, Dictionary<string, string>> item3 in dictionary3)
				{
					GameProfile value4;
					if (!dictionary.TryGetValue(item3.Key, out value4))
					{
						throw new FormatException("dangling O record");
					}
					foreach (KeyValuePair<string, string> item4 in item3.Value)
					{
						value4.Overrides.Add(item4.Key, item4.Value);
					}
					if (!ThreadBoostSelection.MigrateOverrides(value4.Overrides))
					{
						throw new FormatException("Legacy thread preferences could not be read; original profile data retained");
					}
				}
				ValidateProfiles(list);
				if (processedCount > 0)
				{
					Logger.Log(PerformancePolicy.Text("游戏库：已忽略 ", "Game library: ignored ") + processedCount + PerformancePolicy.Text(" 条旧的逐游戏“扩大后台范围／智能自适应升档”覆盖，已改由场景规则决定", " old per-game broader-scope / Smart adaptive escalation override(s); the scene rules now decide"));
				}
			}
			catch (Exception error)
			{
				loadFailed = true;
				list.Clear();
				Logger.LogFailure(Lang.T("log.gameprofiles.9"), error);
			}
			return list;
		}

		public static GameProfile NewProfile(string name, string root)
		{
			return NewProfile(name, root, null);
		}

		public static GameProfile NewProfile(string name, string root, string executablePath)
		{
			GameProfile gameProfile = new GameProfile();
			gameProfile.Id = Guid.NewGuid().ToString("N");
			gameProfile.Name = (string.IsNullOrEmpty(name) ? "Game" : name);
			gameProfile.Root = NormalizeRoot(root);
			gameProfile.ExecutablePath = NormalizePath(executablePath);
			GameProfile gameProfile2 = gameProfile;
			if (!string.IsNullOrEmpty(name))
			{
				gameProfile2.Entries.Add(StripExe(name));
			}
			return gameProfile2;
		}

		internal static string NormalizeRoot(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}
			try
			{
				return Path.GetFullPath(value.Trim().Trim('"')).TrimEnd('\\');
			}
			catch
			{
				return null;
			}
		}

		internal static string NormalizePath(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}
			try
			{
				return Path.GetFullPath(value.Trim().Trim('"'));
			}
			catch
			{
				return null;
			}
		}

		private static string StripExe(string s)
		{
			string trimmed = (s ?? "").Trim();
			if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return trimmed;
			}
			return trimmed.Substring(0, trimmed.Length - 4);
		}

		private static void ParseEntries(HashSet<string> set, string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			string[] parts = text.Split(new char[1] { '\n' }, StringSplitOptions.None);
			foreach (string entryText in parts)
			{
				if (string.IsNullOrWhiteSpace(entryText) || entryText.IndexOf('\r') >= 0 || !set.Add(entryText))
				{
					throw new FormatException("invalid or duplicate profile entry");
				}
			}
		}

		private static string Join(IEnumerable<string> values)
		{
			List<string> list = new List<string>(values);
			list.Sort(StringComparer.OrdinalIgnoreCase);
			return string.Join("\n", list.ToArray());
		}

		private static string B64(string s)
		{
			return Convert.ToBase64String(StrictUtf8.GetBytes(s ?? ""));
		}

		private static string Decode(string s)
		{
			byte[] fromBase64StringResult = Convert.FromBase64String(s ?? "");
			if (!string.Equals(Convert.ToBase64String(fromBase64StringResult), s, StringComparison.Ordinal))
			{
				throw new FormatException("non-canonical base64");
			}
			return StrictUtf8.GetString(fromBase64StringResult);
		}

		private static string NullIfEmpty(string value)
		{
			if (value.Length != 0)
			{
				return value;
			}
			return null;
		}
	}
}
