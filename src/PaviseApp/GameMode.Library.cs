using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal partial class GameMode
	{
		private static bool SameLibraryPath(string a, string b)
		{
			if (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b))
			{
				return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		private bool EnsureLibraryReadyLocked()
		{
			if (libraryIgnoreTransaction == null)
			{
				return true;
			}
			if (!libraryIgnoreTransaction.RecoveryPending && !autoIgnoreLoadFailed)
			{
				return true;
			}
			if (libraryIgnoreTransaction.TryRecover())
			{
				return LoadAutoIgnore();
			}
			return false;
		}
		private bool CommitLibraryLocked(List<GameProfile> next, HashSet<string> nextIgnore, Func<bool> canCommit = null)
		{
			bool persistenceFailed;
			return CommitLibraryLocked(next, nextIgnore, out persistenceFailed, canCommit);
		}
		private bool CommitLibraryLocked(List<GameProfile> next, HashSet<string> nextIgnore, out bool persistenceFailed, Func<bool> canCommit = null)
		{
			persistenceFailed = false;
			if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked() || (canCommit != null && !canCommit()))
			{
				return false;
			}
			bool flag;
			if (autoAddIgnore.SetEquals(nextIgnore))
			{
				flag = SaveProfileSnapshotLocked(next, out persistenceFailed, canCommit);
			}
			else
			{
				List<string> list = new List<string>();
				list.Add(Lang.T("t.autoadd.1"));
				List<string> list2 = list;
				List<string> list3 = new List<string>(nextIgnore);
				list3.Sort(StringComparer.OrdinalIgnoreCase);
				list2.AddRange(list3);
				byte[] bytes = new UTF8Encoding(false).GetBytes(string.Join(Environment.NewLine, list2.ToArray()) + Environment.NewLine);
				flag = libraryIgnoreTransaction.Commit(next, bytes, () => !stopping && (canCommit == null || canCommit()));
				if (!flag && profileStore.SaveFailed)
				{
					SignalProfileStoreSaveFailure();
				}
			}
			if (!flag)
			{
				return false;
			}
			profiles.Clear();
			profiles.AddRange(next);
			autoAddIgnore.Clear();
			autoAddIgnore.UnionWith(nextIgnore);
			return true;
		}
		private void AddWhiteNoSave(string n)
		{
			WhitelistRule rule;
			if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, n, out rule))
			{
				AddWhiteRuleNoSave(rule);
			}
		}
		internal static string ResolveLibraryInstallRoot(string executablePath, string fallbackRoot)
		{
			string text = NormalizeGameRoot(fallbackRoot);
			if (text == null)
			{
				text = NormalizeGameRoot(GameScan.InferGameRoot(executablePath));
			}
			text = GameInstallScope.RestrictFallback(executablePath, text);
			string text2 = NormalizeGameRoot(GameInstallScope.Resolve(executablePath, text));
			if (text2 == null || !FamilyBoundary.UnderRoot(executablePath, text2))
			{
				return text;
			}
			return text2;
		}
		internal static bool RefreshLibraryInstallRoots(IList<GameProfile> library)
		{
			bool result = false;
			if (library == null)
			{
				return false;
			}
			foreach (GameProfile item in library)
			{
				if (item != null && !string.IsNullOrEmpty(item.ExecutablePath))
				{
					string text = GameInstallScope.RestrictFallback(item.ExecutablePath, NormalizeGameRoot(item.Root));
					bool flag = !string.IsNullOrEmpty(item.Root) && text == null;
					string text2 = NormalizeGameRoot(GameInstallScope.Resolve(item.ExecutablePath, text));
					if (text2 != null && !FamilyBoundary.UnderRoot(item.ExecutablePath, text2))
					{
						text2 = text;
					}
					if (!string.Equals(text2, item.Root, StringComparison.OrdinalIgnoreCase) && (flag || (text2 != null && (string.IsNullOrEmpty(item.Root) || FamilyBoundary.UnderRoot(item.Root, text2)) && (string.IsNullOrEmpty(item.LearnedExecutablePath) || FamilyBoundary.UnderRoot(item.LearnedExecutablePath, text2)))))
					{
						item.Root = text2;
						result = true;
					}
				}
			}
			return result;
		}
		internal bool SetProfileFullscreenOptimizations(string profileId, bool on)
		{
			return SetProfileOverride(profileId, "GameDisableFullscreenOptimizationsV1", on ? "1" : "0");
		}
		public bool AddGameExecutable(string name, string executablePath)
		{
			string error;
			return AddGameExecutableCore(name, executablePath, null, out error);
		}
		private bool StageGameExecutable(string name, string executablePath, string preferredRoot, List<GameProfile> next, HashSet<string> nextIgnore, out string error)
		{
			string executablePath2;
			string suggestedName;
			if (!GameExecutableResolver.TryResolve(executablePath, out executablePath2, out error, out suggestedName))
			{
				return false;
			}
			string item = StripExe(Path.GetFileName(executablePath2));
			string name2 = DisplayName(executablePath2, string.IsNullOrWhiteSpace(name) ? suggestedName : name);
			string text = null;
			if (!string.IsNullOrEmpty(preferredRoot))
			{
				string text2 = NormalizeGameRoot(preferredRoot);
				if (text2 != null && FamilyBoundary.UnderRoot(executablePath2, text2))
				{
					text = text2;
				}
			}
			if (text == null)
			{
				text = NormalizeGameRoot(GameScan.InferGameRoot(executablePath2));
			}
			text = ResolveLibraryInstallRoot(executablePath2, text);
			foreach (GameProfile item2 in next)
			{
				if (string.Equals(item2.ExecutablePath, executablePath2, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				if (string.Equals(item2.LearnedExecutablePath, executablePath2, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				if (string.IsNullOrEmpty(item2.ExecutablePath) && item2.Entries.Contains(item))
				{
					item2.ExecutablePath = executablePath2;
					item2.Root = text;
					item2.Name = name2;
					item2.LearnedExecutablePath = null;
					nextIgnore.Remove(executablePath2);
					return true;
				}
			}
			GameProfile gameProfile = GameProfileStore.NewProfile(name2, text, executablePath2);
			gameProfile.Entries.Clear();
			gameProfile.Entries.Add(item);
			next.Add(gameProfile);
			nextIgnore.Remove(executablePath2);
			return true;
		}
		private bool PersistLibraryLocked()
		{
			return SaveProfilesLocked();
		}
		private void RaiseLibraryChanged()
		{
			Action libraryChanged = this.LibraryChanged;
			if (libraryChanged != null)
			{
				try
				{
					libraryChanged();
				}
				catch
				{
				}
			}
		}
		private void KickLibraryChanged()
		{
			InvalidateAutomaticFullscreenOptimizations();
			ClearFamilyDiscovery();
			InvalidateRendererHandoff();
			InvalidateFamilyPolicy();
			RequestFullGameDetection();
			RequestPolicyApply("KickLibraryChanged");
			RaiseLibraryChanged();
		}
		public bool AddGameFile(string selectedPath, out string error)
		{
			string executablePath;
			if (!GameExecutableResolver.TryResolve(selectedPath, out executablePath, out error))
			{
				return false;
			}
			if (!AddGameExecutable(null, executablePath))
			{
				error = Lang.T("t.gamemodelibrary.1");
				return false;
			}
			error = null;
			return true;
		}
		public bool SetProfileForceTrigger(string profileId, bool on)
		{
			bool flag = false;
			bool flag2 = false;
			string text = null;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				foreach (GameProfile profile in profiles)
				{
					if (string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
					{
						if (on && string.IsNullOrEmpty(profile.ExecutablePath) && string.IsNullOrEmpty(profile.LearnedExecutablePath))
						{
							return false;
						}
						if (profile.ForceTrigger != on)
						{
							profile.ForceTrigger = on;
							text = profile.Name;
							flag = true;
							if (!PersistLibraryLocked())
							{
								profile.ForceTrigger = !on;
								return false;
							}
							break;
						}
						break;
					}
				}
				if (flag && !on)
				{
					flag2 = activeDetection != null && activeDetection.Profile != null && string.Equals(activeDetection.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase);
				}
			}
			if (!flag)
			{
				return true;
			}
			if (flag2)
			{
				panicReq = true;
			}
			Logger.Log((on ? Lang.T("log.gamemodelibrary.5") : Lang.T("log.gamemodelibrary.6")) + text + (on ? Lang.T("log.gamemodelibrary.7") : Lang.T("log.gamemodelibrary.8")));
			KickLibraryChanged();
			return true;
		}
		private GameProfile FindProfileLocked(string profileId)
		{
			foreach (GameProfile profile in profiles)
			{
				if (string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
				{
					return profile;
				}
			}
			return null;
		}
		public bool SetProfileOverride(string profileId, string key, string value)
		{
			if (key == "GameFamilyBackground")
			{
				string text = PolicyCatalog.Canonical(key, value);
				if (text != null)
				{
					return SetProfileFamilySuppression(profileId, text == "1");
				}
				return false;
			}
			bool flag = false;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				if (key == "PerformancePreset" && ModeSwitchLockedLocked())
				{
					return false;
				}
				if (key == "GmStandbyCleanerV2" && PolicyCatalog.Canonical(key, value) == "1" && !standbyCleanerOptionsValid)
				{
					return false;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile != null)
				{
					if (key == "GmAutoCoresV2")
					{
						GameProfile draft = gameProfile.Clone();
						if (!PolicyResolver.SetOverride(draft, key, value) || ValidateCriticalPreference(draft) != null) return false;
					}
					string value2 = null;
					bool flag2 = key != null && gameProfile.Overrides.TryGetValue(key, out value2);
					InvalidateOverrideWorkLocked(key);
					flag = PolicyResolver.SetOverride(gameProfile, key, value);
					if (flag && !SaveProfilesLocked())
					{
						if (flag2)
						{
							gameProfile.Overrides[key] = value2;
						}
						else
						{
							gameProfile.Overrides.Remove(key);
						}
						flag = false;
					}
				}
			}
			if (flag)
			{
				NotifyOverridePolicyChanged(key, PolicyCatalog.Canonical(key, value) == "1");
			}
			return flag;
		}
		public bool ClearProfileOverride(string profileId, string key)
		{
			if (key == "GameFamilyBackground")
			{
				return SetProfileFamilySuppression(profileId, false);
			}
			bool flag = false;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile != null && gameProfile.Overrides.ContainsKey(key))
				{
					string value = gameProfile.Overrides[key];
					InvalidateOverrideWorkLocked(key);
					gameProfile.Overrides.Remove(key);
					flag = (key != "GmAutoCoresV2" || ValidateCriticalPreference(gameProfile) == null) && SaveProfilesLocked();
					if (!flag)
					{
						gameProfile.Overrides[key] = value;
					}
				}
			}
			if (flag)
			{
				NotifyOverridePolicyChanged(key, GlobalPolicyOn(key));
			}
			return flag;
		}
		public int ClearProfileOverrides(string profileId)
		{
			int num = 0;
			string text = null;
			bool flag = false;
			bool flag2 = false;
			lock (familyPolicyGate)
			{
				lock (sync)
				{
					if (stopping)
					{
						return 0;
					}
					GameProfile gameProfile = FindProfileLocked(profileId);
					if (gameProfile != null && gameProfile.Overrides.Count > 0 && !ProfileStoreSaveFailed)
					{
						flag2 = gameProfile.Overrides.ContainsKey("GmStandbyCleanerV2");
						foreach (string key in gameProfile.Overrides.Keys)
						{
							InvalidateOverrideWorkLocked(key);
						}
						GameProfile gameProfile2 = gameProfile.Clone();
						num = PolicyResolver.ClearAllOverrides(gameProfile2);
						List<GameProfile> list = new List<GameProfile>(profiles);
						int index = profiles.IndexOf(gameProfile);
						list[index] = gameProfile2;
						if (!SaveProfileSnapshotLocked(list))
						{
							return 0;
						}
						profiles[index] = gameProfile2;
						DisableSessionCriticalPreferenceLocked(profileId);
						flag = gameProfile.Overrides.ContainsKey("GmPauseServices");
						text = gameProfile.Name;
						InvalidateFamilyPolicy();
					}
				}
			}
			if (num > 0)
			{
				NotifyOverridePolicyChanged("GameDisableFullscreenOptimizationsV1", false);
				NotifyOverridePolicyChanged("GameNvRebarV1", false);
				if (flag)
				{
					PauseServicesPolicyChanged(pauseServicesOn);
				}
				if (flag2)
				{
					StandbyCleanerPolicyChanged(standbyCleanerOn);
				}
				if (!flag && !flag2)
				{
					RequestPolicyApply("ClearProfileOverrides");
				}
				RaiseLibraryChanged();
			}
			if (text != null)
			{
				Logger.Log(Lang.T("log.gamemodelibrary.9") + text + Lang.T("log.gamemodelibrary.10") + num + Lang.T("log.gamemodelibrary.11"));
			}
			return num;
		}
		public List<PolicyDiff> DiffProfile(string profileId)
		{
			GameProfile profile = null;
			lock (sync)
			{
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile != null)
				{
					profile = gameProfile.Clone();
				}
			}
			return PolicyResolver.Diff(profile);
		}
		public bool RenameProfile(string profileId, string name)
		{
			string text = (name ?? "").Trim();
			if (text.Length == 0)
			{
				return false;
			}
			string text2 = null;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile == null)
				{
					return false;
				}
				if (string.Equals(gameProfile.Name, text, StringComparison.Ordinal))
				{
					return true;
				}
				text2 = gameProfile.Name;
				gameProfile.Name = text;
				if (!SaveProfilesLocked())
				{
					gameProfile.Name = text2;
					return false;
				}
			}
			Logger.Log(Lang.T("log.gamemodelibrary.14") + text2 + Lang.T("log.gamemodelibrary.15") + text + Lang.T("log.gamemodelibrary.16"));
			RaiseLibraryChanged();
			return true;
		}
		public void RemoveProfile(string profileId)
		{
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
				{
					return;
				}
				HashSet<string> hashSet = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				foreach (GameProfile profile in profiles)
				{
					if (string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
					{
						if (!string.IsNullOrEmpty(profile.ExecutablePath))
						{
							hashSet.Add(profile.ExecutablePath);
						}
						if (!string.IsNullOrEmpty(profile.LearnedExecutablePath))
						{
							hashSet.Add(profile.LearnedExecutablePath);
						}
					}
				}
				List<GameProfile> list = GetProfiles();
				if (list.RemoveAll((GameProfile p) => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) == 0 || !CommitLibraryLocked(list, hashSet))
				{
					return;
				}
				ClearModeSwitchLockForProfileLocked(profileId);
				InvalidateAutomaticFullscreenOptimizations();
				InvalidateCacheWarm();
				InvalidateStandbyCleanerWork();
				InvalidateEnglishInputWork();
				ClearFamilyDiscovery();
				ForgetRendererObservation(profileId);
				if ((activeDetection != null && activeDetection.Profile != null && string.Equals(activeDetection.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase)) || (sessionPolicy != null && string.Equals(sessionPolicy.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)))
				{
					panicReq = true;
				}
				InvalidateRendererHandoff();
			}
			SyncAutomaticFullscreenOptimizations(true, -1L);
			RequestFullGameDetection();
			RequestPolicyApply("RemoveProfile");
			RaiseLibraryChanged();
		}
		private bool SaveWhite(IList<WhitelistRule> rules)
		{
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				try
				{
					List<string> list = new List<string>();
					list.Add(Lang.T("t.gamemodelibrary.23"));
					list.Add(Lang.T("t.gamemodelibrary.24"));
					list.Add(Lang.T("t.gamemodelibrary.25"));
					list.Add("PAVISE_WHITELIST_V4");
					if (rules != null)
					{
						foreach (WhitelistRule rule in rules)
						{
							list.Add(rule.Serialize());
						}
					}
					list.Add(BuildWhitelistFooter(rules));
					return AtomicFile.WriteLines(whitePath, list.ToArray(), Lang.T("nav.white"));
				}
				catch (Exception error)
				{
					Logger.LogFailure(Lang.T("log.gamemodelibrary.26"), error);
					return false;
				}
			}
		}
		public List<GameProfile> GetProfiles()
		{
			lock (sync)
			{
				List<GameProfile> list = new List<GameProfile>();
				foreach (GameProfile profile in profiles)
				{
					list.Add(profile.Clone());
				}
				return list;
			}
		}
		private bool SaveProfilesLocked()
		{
			return SaveProfileSnapshotLocked(profiles);
		}
		private bool SaveProfileSnapshotLocked(IList<GameProfile> next, Func<bool> canCommit = null)
		{
			bool persistenceFailed;
			return SaveProfileSnapshotLocked(next, out persistenceFailed, canCommit);
		}
		private bool SaveProfileSnapshotLocked(IList<GameProfile> next, out bool persistenceFailed, Func<bool> canCommit = null)
		{
			persistenceFailed = false;
			if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
			{
				return false;
			}
			if (profileStore.Save(next, () => !stopping && (canCommit == null || canCommit())))
			{
				return true;
			}
			persistenceFailed = !profileStore.SaveCanceled && (profileStore.RetryableSaveFailure || profileStore.SaveFailed);
			SignalProfileStoreSaveFailure(profileStore.RetryableSaveFailure);
			return false;
		}
		private void SignalProfileStoreSaveFailure(bool retryableProfileSave = false)
		{
			if (((retryableProfileSave || profileStore.SaveCanceled) && !profileStore.SaveFailed) || Interlocked.Exchange(ref profileSaveFailureSignaled, 1) != 0)
			{
				return;
			}
			InvalidateStandbyCleanerWork();
			InvalidateEnglishInputWork();
			InvalidateRendererHandoff();
			Action profileStoreSaveFailure = this.ProfileStoreSaveFailure;
			if (profileStoreSaveFailure == null)
			{
				return;
			}
			try
			{
				profileStoreSaveFailure();
			}
			catch
			{
			}
		}
		public List<string> LibraryExecutablePaths()
		{
			List<string> list = new List<string>();
			lock (sync)
			{
				foreach (GameProfile profile in profiles)
				{
					string preferredExecutablePath = profile.PreferredExecutablePath;
					if (!string.IsNullOrEmpty(preferredExecutablePath))
					{
						list.Add(preferredExecutablePath);
					}
				}
				return list;
			}
		}
		internal static bool SameReportedProfile(string sessionProfileId, string detectedProfileId)
		{
			if (!string.IsNullOrEmpty(sessionProfileId) && !string.IsNullOrEmpty(detectedProfileId))
			{
				return string.Equals(sessionProfileId, detectedProfileId, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		private void ClearModeSwitchLockForProfileLocked(string profileId)
		{
			if (!string.IsNullOrEmpty(profileId) && string.Equals(modeLockGameProfileId, profileId, StringComparison.OrdinalIgnoreCase))
			{
				modeLockGamePid = 0;
				modeLockGameCreation = 0L;
				modeLockGameProfileId = null;
			}
		}
		private bool AddWhiteRuleNoSave(WhitelistRule rule)
		{
			if (rule == null || !whiteRuleKeys.Add(rule.Key))
			{
				return false;
			}
			whiteRules.Add(rule);
			whiteRevision++;
			if (RuleProtectsFamily(rule))
			{
				Interlocked.Exchange(ref whiteHasFamilyRules, 1);
			}
			return true;
		}
	}
}
