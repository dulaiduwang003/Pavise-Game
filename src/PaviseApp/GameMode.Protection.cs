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
		private bool LoadAutoIgnore()
		{
			try
			{
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string[] allLines = (File.Exists(autoIgnorePath) ? File.ReadAllLines(autoIgnorePath) : new string[0]);
				foreach (string entryText in allLines)
				{
					string entryTextTrimmed = entryText.Trim();
					if (entryTextTrimmed.Length != 0 && !entryTextTrimmed.StartsWith("#"))
					{
						hashSet.Add(entryTextTrimmed);
					}
				}
				autoAddIgnore.Clear();
				autoAddIgnore.UnionWith(hashSet);
				autoIgnoreLoadFailed = false;
				return true;
			}
			catch
			{
				autoIgnoreLoadFailed = true;
				return false;
			}
		}
		private void ClearFamilyDiscovery()
		{
			lock (sync)
			{
				GameFamilyHistory gameFamilyHistory = this.gameFamilyHistory;
				if (gameFamilyHistory != null)
				{
					gameFamilyHistory.Clear();
				}
				Volatile.Write(ref gameFamilyEvidence, GameFamilyEvidence.Empty);
			}
		}
		public bool NeedsGameFamilyIdentity(int session)
		{
			if (session != selfSession || session < 0 || stopping || !enabled)
			{
				return false;
			}
			lock (sync)
			{
				return profiles.Count > 0;
			}
		}
		private void ObserveGameFamilyChanges(ProcessChangeBatch batch)
		{
			if (batch == null || stopping || !enabled)
			{
				return;
			}
			lock (sync)
			{
				if (!stopping && enabled && profiles.Count != 0)
				{
					FamilyHistory.ObserveEvents(batch, selfSession, RendererNowMs());
				}
			}
		}
		private void CaptureGameFamily(ProcessSnapshot snapshot, IList<GameProfile> library)
		{
			Volatile.Write(ref gameFamilyEvidence, FamilyHistory.Capture(snapshot, library, selfSession, RendererNowMs()));
		}
		public bool SetProfileFamilySuppression(string profileId, bool on)
		{
			return SetProfileFamilySuppression(profileId, on, null);
		}
		public bool SetProfileFamilySuppression(string profileId, bool on, string expectedExecutablePath)
		{
			bool setProfileFamilySuppressionReady = false;
			lock (familyPolicyGate)
			{
				lock (sync)
				{
					GameProfile gameProfile = FindProfileLocked(profileId);
					if (stopping || gameProfile == null || ProfileStoreSaveFailed)
					{
						return false;
					}
					if (expectedExecutablePath != null && !string.Equals(gameProfile.ExecutablePath, expectedExecutablePath, StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
					if (gameProfile.SuppressFamilyBackground == on)
					{
						return true;
					}
					GameProfile gameProfile2 = gameProfile.Clone();
					if (on)
					{
						gameProfile2.Overrides["GameFamilyBackground"] = "1";
					}
					else
					{
						gameProfile2.Overrides.Remove("GameFamilyBackground");
					}
					List<GameProfile> list = new List<GameProfile>(profiles);
					int index = profiles.IndexOf(gameProfile);
					list[index] = gameProfile2;
					if (!SaveProfileSnapshotLocked(list))
					{
						return false;
					}
					profiles[index] = gameProfile2;
					if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
					{
						activeDetection.Profile = gameProfile2.Clone();
					}
					if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
					{
						stickyDetection.Profile = gameProfile2.Clone();
					}
					Interlocked.Increment(ref familyPolicyEpoch);
					firstSweep = true;
					setProfileFamilySuppressionReady = true;
				}
			}
			if (setProfileFamilySuppressionReady)
			{
				RequestPolicyApply("SetProfileFamilySuppression");
				RaiseLibraryChanged();
			}
			return true;
		}
		internal bool FamilyPolicyEpochCurrent(int epoch)
		{
			if (epoch == Volatile.Read(ref familyPolicyEpoch) && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed)
			{
				return EffSuppress;
			}
			return false;
		}
		private void InvalidateFamilyPolicy()
		{
			Interlocked.Increment(ref familyPolicyEpoch);
		}
		public List<WhitelistRuleView> GetWhitelistRules()
		{
			List<WhitelistRuleView> list = SnapshotWhitelistRuleViews();
			list.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
			{
				int compareToResult = a.Rule.Kind.CompareTo(b.Rule.Kind);
				return (compareToResult == 0) ? string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase) : compareToResult;
			});
			return list;
		}
		public List<WhitelistRuleView> GetWhitelistRulesFast()
		{
			List<WhitelistRuleView> list = new List<WhitelistRuleView>();
			AppendPlatformRuleViews(list, null);
			lock (sync)
			{
				foreach (WhitelistRule whiteRule in whiteRules)
				{
					list.Add(new WhitelistRuleView(whiteRule, -1, whiteRule.Kind == WhitelistRuleKind.LegacyName && IsPresetWhitelistName(whiteRule.Value)));
				}
			}
			list.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
			{
				int compareToResult = a.Rule.Kind.CompareTo(b.Rule.Kind);
				return (compareToResult == 0) ? string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase) : compareToResult;
			});
			return list;
		}
		public bool AddWhitelistPath(string executablePath)
		{
			return AddWhitelistRule(WhitelistRuleKind.ExactPath, executablePath);
		}
		public bool AddWhitelistAuto(string executablePath)
		{
			return AddWhitelistRule(ResolveAutoKind(executablePath), executablePath);
		}
		public bool NarrowWhitelistRule(string key)
		{
			WhitelistRule whitelistRule = null;
			lock (sync)
			{
				foreach (WhitelistRule whiteRule in whiteRules)
				{
					if (whiteRule.Key == key)
					{
						whitelistRule = whiteRule;
						break;
					}
				}
			}
			if (whitelistRule == null || whitelistRule.Kind != WhitelistRuleKind.ApplicationFamily)
			{
				return false;
			}
			if (!RemoveWhitelistRule(key))
			{
				return false;
			}
			if (AddWhitelistRule(WhitelistRuleKind.ExactPath, whitelistRule.Value))
			{
				return true;
			}
			AddWhitelistRule(WhitelistRuleKind.ApplicationFamily, whitelistRule.Value);
			return false;
		}
		public bool WidenWhitelistRule(string key)
		{
			WhitelistRule whitelistRule = null;
			lock (sync)
			{
				foreach (WhitelistRule whiteRule in whiteRules)
				{
					if (whiteRule.Key == key)
					{
						whitelistRule = whiteRule;
						break;
					}
				}
			}
			if (whitelistRule == null || whitelistRule.Kind != WhitelistRuleKind.ExactPath)
			{
				return false;
			}
			if (WhitelistRule.IsUnsafeFamilyAnchor(whitelistRule.Value))
			{
				return false;
			}
			if (!RemoveWhitelistRule(key))
			{
				return false;
			}
			if (AddWhitelistRule(WhitelistRuleKind.ApplicationFamily, whitelistRule.Value))
			{
				return true;
			}
			AddWhitelistRule(WhitelistRuleKind.ExactPath, whitelistRule.Value);
			return false;
		}
		private bool AddWhitelistRule(WhitelistRuleKind kind, string value)
		{
			WhitelistRule rule;
			if (!WhitelistRule.TryCreate(kind, value, out rule))
			{
				lock (sync)
				{
					whitelistLastError = Lang.T("white.duplicate");
				}
				return false;
			}
			int releaseCurrentWhitelistMatchesResult;
			int matched;
			lock (whiteEvalSync)
			{
				lock (sync)
				{
					if (stopping)
					{
						return false;
					}
					if (whiteRuleKeys.Contains(rule.Key))
					{
						whitelistLastError = Lang.T("white.duplicate");
						return false;
					}
					List<WhitelistRule> list = new List<WhitelistRule>(whiteRules);
					list.Add(rule);
					List<WhitelistRule> rules = list;
					if (!SaveWhite(rules))
					{
						whitelistLastError = Lang.T("white.save.failed");
						return false;
					}
					AddWhiteRuleNoSave(rule);
					whitelistLastError = "";
				}
				releaseCurrentWhitelistMatchesResult = ReleaseCurrentWhitelistMatches(out matched);
			}
			Logger.Log(string.Concat(Lang.T("log.gamemodelibrary.17"), rule.Kind, " ", rule.Value, Lang.T("log.gamemodelibrary.18"), matched, Lang.T("log.gamemodelibrary.19"), releaseCurrentWhitelistMatchesResult, Lang.T("log.gamemodelibrary.20")));
			RequestPolicyApply("AddWhitelistRule");
			return true;
		}
		public bool RemoveWhitelistRule(string key)
		{
			if (string.IsNullOrEmpty(key))
			{
				return false;
			}
			if (key.StartsWith("platform:", StringComparison.Ordinal))
			{
				return RemovePlatformProtection(key.Substring("platform:".Length));
			}
			lock (whiteEvalSync)
			{
				lock (sync)
				{
					if (stopping)
					{
						return false;
					}
					WhitelistRule whitelistRule = whiteRules.Find((WhitelistRule rule) => string.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase));
					if (whitelistRule == null)
					{
						return false;
					}
					if (whitelistRule.Kind == WhitelistRuleKind.LegacyName && IsPresetWhitelistName(whitelistRule.Value))
					{
						whitelistLastError = Lang.T("white.required");
						return false;
					}
					List<WhitelistRule> list = new List<WhitelistRule>(whiteRules);
					list.Remove(whitelistRule);
					if (!SaveWhite(list))
					{
						whitelistLastError = Lang.T("white.save.failed");
						return false;
					}
					whiteRules.Remove(whitelistRule);
					whiteRuleKeys.Remove(key);
					whiteFamilyMembers.Remove(key);
					whiteFamilyMembersVersion++;
					whiteRevision++;
					RefreshWhitelistFamilyFlagLocked();
					whitelistLastError = "";
				}
			}
			RequestPolicyApply("RemoveWhitelistRule");
			return true;
		}
		public bool ResetWhitelist()
		{
			List<WhitelistRule> list = new List<WhitelistRule>();
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string[] presetWhitelist = SystemProcessCatalog.PresetWhitelist;
			foreach (string value in presetWhitelist)
			{
				WhitelistRule rule;
				if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, value, out rule) && hashSet.Add(rule.Key))
				{
					list.Add(rule);
				}
			}
			int releaseCurrentWhitelistMatchesResult;
			int matched;
			lock (whiteEvalSync)
			{
				lock (sync)
				{
					if (stopping)
					{
						return false;
					}
					if (!SaveWhite(list))
					{
						whitelistLastError = Lang.T("white.save.failed");
						return false;
					}
					whiteRules.Clear();
					whiteRuleKeys.Clear();
					whiteFamilyMembers.Clear();
					whiteFamilyMembersVersion++;
					whiteRevision++;
					RefreshWhitelistFamilyFlagLocked();
					foreach (WhitelistRule item in list)
					{
						AddWhiteRuleNoSave(item);
					}
					whitelistLastError = "";
				}
				releaseCurrentWhitelistMatchesResult = ReleaseCurrentWhitelistMatches(out matched);
			}
			Logger.Log(Lang.T("log.gamemodelibrary.21") + SystemProcessCatalog.PresetWhitelist.Length + Lang.T("log.gamemodelibrary.22") + matched + Lang.T("log.gamemodelibrary.19") + releaseCurrentWhitelistMatchesResult + Lang.T("log.gamemodelibrary.20"));
			RequestPolicyApply("ResetWhitelist");
			return true;
		}
		internal static string BuildWhitelistFooter(IList<WhitelistRule> rules)
		{
			ulong numericValue = 1469598103934665603uL;
			int processedCount = 0;
			if (rules != null)
			{
				foreach (WhitelistRule rule in rules)
				{
					if (rule != null)
					{
						string serializeResult = rule.Serialize();
						processedCount++;
						for (int i = 0; i < serializeResult.Length; i++)
						{
							numericValue ^= (byte)serializeResult[i];
							numericValue *= 1099511628211L;
						}
						numericValue ^= 0xA;
						numericValue *= 1099511628211L;
					}
				}
			}
			return "PAVISE_WHITELIST_END|" + processedCount + "|" + numericValue.ToString("X16");
		}
		internal static bool IsActiveFamilyChildStart(GameDetection detection, ProcessChange change, int ownerSession)
		{
			if (detection != null && change != null && change.Kind == ProcessChangeKind.Started && ownerSession >= 0 && change.Session == ownerSession && change.Creation > 0 && change.ParentPid > 0 && change.ParentPid == detection.RendererPid && detection.RendererCreation > 0 && change.ParentCreation == detection.RendererCreation)
			{
				return change.Creation > detection.RendererCreation;
			}
			return false;
		}
		private GameDetection CloneWithAnchoredFamily(GameDetection src)
		{
			GameDetection gameDetection = new GameDetection();
			gameDetection.Profile = src.Profile;
			gameDetection.RendererPid = src.RendererPid;
			gameDetection.RendererCreation = src.RendererCreation;
			gameDetection.RendererName = src.RendererName;
			gameDetection.RendererPath = src.RendererPath;
			gameDetection.RendererForeground = src.RendererForeground;
			gameDetection.RendererCandidateSelected = src.RendererCandidateSelected;
			gameDetection.RendererUserSelected = src.RendererUserSelected;
			gameDetection.RendererLearnable = src.RendererLearnable;
			gameDetection.Evidence = src.Evidence;
			GameDetection gameDetection2 = gameDetection;
			foreach (int familyPid in src.FamilyPids)
			{
				if (stickyIds.ContainsKey(familyPid))
				{
					gameDetection2.FamilyPids.Add(familyPid);
				}
			}
			foreach (string familyName in src.FamilyNames)
			{
				gameDetection2.FamilyNames.Add(familyName);
			}
			return gameDetection2;
		}
		private List<int> LiveStickyFamily()
		{
			List<int> list = new List<int>();
			foreach (KeyValuePair<int, GameId> stickyId in stickyIds)
			{
				if (AliveWithIdentity(stickyId.Key))
				{
					list.Add(stickyId.Key);
				}
			}
			return list;
		}
		private void SweepWithStableWhitelist(ProcessSnapshot all, HashSet<int> gamePids)
		{
			using (SuccessLogBatch.Begin("background-sweep session=" + AutomationSession))
				SweepWithStableWhitelistCore(all, gamePids);
		}
		private void SweepWithStableWhitelistCore(ProcessSnapshot all, HashSet<int> gamePids)
		{
			PolicySnapshot policySnapshot = sessionPolicy;
			PerformancePreset performancePreset = ((policySnapshot != null) ? policySnapshot.Preset : ActivePreset);
			SuppressionCore.BackgroundBoostLimit = (policySnapshot ?? PolicyResolver.Global()).BackgroundBoostLimitRequested;
			bool preserveOnForeground = performancePreset == PerformancePreset.Competitive;
			int foregroundPid = GameSessionDetector.ForegroundPid();
			bool wideBackgroundScopeSucceeded = WideBackgroundScope(policySnapshot ?? PolicyResolver.Global());
			WhitelistEvaluation whitelistEvaluation = EvaluateWhitelist(all);
			int epoch = FamilyPolicyEpoch;
			List<KeyValuePair<string, int>> services = (BackgroundServicePolicy.SupportedMode(performancePreset) ? BackgroundServiceHosts(all) : null);
			bool sweepWithStableWhitelistCoreReady = true;
			int rendererPid = 0;
			string root = null;
			GameProfile profile = null;
			List<string> list = new List<string>();
			List<GameProfile> exactGameProfiles = new List<GameProfile>();
			bool lockTaken = false;
			object instance = default(object);
			List<GameProfile> list2;
			try
			{
				Monitor.Enter(instance = sync, ref lockTaken);
				if (activeDetection != null)
				{
					rendererPid = activeDetection.RendererPid;
					GameProfile gameProfile = ((activeDetection.Profile == null) ? null : FindProfileLocked(activeDetection.Profile.Id));
					profile = ((gameProfile == null) ? null : gameProfile.Clone());
					sweepWithStableWhitelistCoreReady = FamilyBoundary.FamilyExemptFor(profile);
					if (sweepWithStableWhitelistCoreReady && activeDetection.Profile != null && FamilyBoundary.SafeFamilyDir(activeDetection.Profile.Root))
					{
						root = activeDetection.Profile.Root;
					}
				}
				list2 = new List<GameProfile>();
				foreach (GameProfile profile2 in profiles)
				{
					GameProfile snapshotProfile = profile2.Clone();
					exactGameProfiles.Add(snapshotProfile);
					if (FamilyBoundary.FamilyExemptFor(profile2))
					{
						list2.Add(snapshotProfile);
						if (FamilyBoundary.SafeFamilyDir(profile2.Root))
						{
							list.Add(profile2.Root);
						}
					}
				}
			}
			finally
			{
				if (lockTaken)
				{
					Monitor.Exit(instance);
				}
			}
			HashSet<int> hashSet = FamilyBoundary.CollectProtectedLibraryFamily(list2, all, selfPid, selfSession, FamilyEvidence);
			// Protect registered and learned game bodies from family suppression, even without a usable library root.
			HashSet<int> exactGameProcesses = FamilyBoundary.CollectProtectedLibraryFamily(exactGameProfiles, all, selfPid, selfSession, FamilyEvidence, true, true);
			hashSet.UnionWith(exactGameProcesses);
			var gameCreationSeeds = new HashSet<int>(exactGameProcesses);
			gameCreationSeeds.UnionWith(gamePids ?? EmptyPidSet);
			if (rendererPid > 0) gameCreationSeeds.Add(rendererPid);
			HashSet<int> gameCreationHosts = FamilyBoundary.CollectGameCreationHosts(all, gameCreationSeeds, selfPid, selfSession);
			bool matchesRendererPid = rendererPid > 0;
			bool matchesSweepWithStableWhitelistCoreReady = sweepWithStableWhitelistCoreReady && matchesRendererPid;
			bool matchesMatchesRendererPid = matchesRendererPid && (sweepWithStableWhitelistCoreReady || !wideBackgroundScopeSucceeded);
			Dictionary<int, long> creations = (matchesMatchesRendererPid ? whitelistEvaluation.Creations : null);
			HashSet<int> hashSet2 = (matchesMatchesRendererPid ? FamilyBoundary.WalkAncestorChain(whitelistEvaluation.Parents, rendererPid, selfPid, 24, creations) : EmptyPidSet);
			HashSet<int> hashSet3 = null;
			if (matchesMatchesRendererPid)
			{
				hashSet3 = new HashSet<int>(gamePids ?? EmptyPidSet);
				hashSet3.Add(rendererPid);
			}
			HashSet<int> gameDescendants = (matchesMatchesRendererPid ? FamilyBoundary.WalkDescendants(whitelistEvaluation.Parents, hashSet3, selfPid, 24, creations) : EmptyPidSet);
			HashSet<int> hashSet4 = (wideBackgroundScopeSucceeded ? EmptyPidSet : CollectUserFacingFamily(foregroundPid, whitelistEvaluation, (!sweepWithStableWhitelistCoreReady) ? rendererPid : 0));
			FamilyBoundary.FilterUserFacingGameFamily(hashSet4, profile, all, rendererPid, selfPid, selfSession, gamePids, gameDescendants, hashSet2, FamilyEvidence);
			bool lockTaken2 = false;
			bool firstSweepSnapshot;
			try
			{
				Monitor.Enter(instance = sync, ref lockTaken2);
				firstSweepSnapshot = firstSweep;
			}
			finally
			{
				if (lockTaken2)
				{
					Monitor.Exit(instance);
				}
			}
			int processedCount = 0;
			int processedCount2 = 0;
			int processedCount3 = 0;
			int processedCount4 = 0;
			int processedCount5 = 0;
			HashSet<int> hashSet5 = new HashSet<int>();
			List<BackgroundRequest> pending = new List<BackgroundRequest>();
			long toFileTimeUtcResult = DateTime.UtcNow.ToFileTimeUtc();
			ProcEntry[] entries = all.Entries;
			foreach (ProcEntry procEntry in entries)
			{
				try
				{
					int pid = procEntry.Pid;
					WhitelistProcessInfo value;
					whitelistEvaluation.Processes.TryGetValue(pid, out value);
					hashSet5.Add(pid);
					if (pid <= 4 || pid == selfPid)
					{
						continue;
					}
					string nm;
					long creation;
					long cpu;
					SuppressionLevel suppressionLevel;
					if (IsCriticalThreadGuardian(pid, procEntry.Creation))
					{
						ReleaseCriticalGuardianBackground();
					}
					else if (GameAffinityJobLease.IsGuardian(pid, procEntry.Creation))
					{
						ReleaseJobGuardianBackground(pid, procEntry.Creation, value != null ? value.Name : procEntry.Name);
					}
					else
					{
						if (procEntry.Creation > 0 && toFileTimeUtcResult - procEntry.Creation < 20000000)
						{
							continue;
						}
						nm = ((value != null) ? value.Name : procEntry.Name);
						if (whitelistEvaluation.RuntimeProtected.Contains(pid))
						{
							BackgroundReleaseState backgroundReleaseState = core.ReleaseBackgroundForRenderer(pid, procEntry.Creation, nm);
							if (backgroundReleaseState == BackgroundReleaseState.IdentityMismatch && core.DiscardReusedRendererTracking(pid, procEntry.Creation, nm))
							{
								backgroundReleaseState = core.ReleaseBackgroundForRenderer(pid, procEntry.Creation, nm);
							}
							if (RuntimeBackgroundExempt(backgroundReleaseState))
							{
								ReportUntrack(pid);
							}
							RecordRuntimeProtection(procEntry, backgroundReleaseState);
							continue;
						}
						if (string.Equals(nm, selfName, StringComparison.OrdinalIgnoreCase))
						{
							if (core.Release(pid, SuppressReason.Background))
							{
								ReportUntrack(pid);
							}
							continue;
						}
						bool allConditionsMet = value != null && selfSession >= 0 && value.Session == selfSession;
						if (value == null)
						{
							allConditionsMet = selfSession >= 0 && procEntry.Session == selfSession;
						}
						string imagePath = (allConditionsMet ? null : ServiceImagePath(procEntry, performancePreset));
						bool matchesEligible = !allConditionsMet && BackgroundServicePolicy.Eligible(performancePreset, procEntry, imagePath, services, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
						if (!allConditionsMet && !matchesEligible)
						{
							ReleaseBackgroundExemption(pid, nm, null);
							continue;
						}
						long creation2 = ((value != null) ? value.Creation : procEntry.Creation);
						string path = ((value != null) ? value.Path : procEntry.Path);
						if (IsRendererHandoffProtected(pid, creation2, path))
						{
							BackgroundReleaseState backgroundReleaseState2 = core.ReleaseBackgroundForRenderer(pid, creation2, nm);
							if (backgroundReleaseState2 == BackgroundReleaseState.Ready || backgroundReleaseState2 == BackgroundReleaseState.Gone)
							{
								ReportUntrack(pid);
							}
							continue;
						}
						bool lockTaken3 = false;
						bool isInGameBoost;
						try
						{
							Monitor.Enter(instance = sync, ref lockTaken3);
							isInGameBoost = gameBoost.ContainsKey(pid);
						}
						finally
						{
							if (lockTaken3)
							{
								Monitor.Exit(instance);
							}
						}
						if (isInGameBoost)
						{
							continue;
						}
						PlatformProcessOwner value2;
						// Protect verified live creators and required launch backends from passing throttled state into new games.
						if (exactGameProcesses.Contains(pid) || gameCreationHosts.Contains(pid) || FamilyBoundary.IsRequiredGameLaunchHost(nm, path))
						{
							ReleaseBackgroundExemption(pid, nm, null);
							continue;
						}
						bool platformRemoved = whitelistEvaluation.PlatformProcesses.TryGetValue(pid, out value2) && !PlatformRuntimePolicy.Enabled(value2.Platform);
						bool whitelisted = whitelistEvaluation.Protected.Contains(pid) || (matchesEligible && ServiceWhitelisted(nm, imagePath));
						if (FamilyBoundary.IsGameOrWhitelistProtected(pid, rendererPid, whitelisted, hashSet.Contains(pid), sweepWithStableWhitelistCoreReady, gamePids, gameDescendants, platformRemoved))
						{
							if (core.Release(pid, SuppressReason.Background))
							{
								ReportUntrack(pid);
							}
							continue;
						}
						string path2 = ((value != null) ? value.Path : null);
						creation = ((value != null) ? value.Creation : 0);
						cpu = ((value != null) ? value.Cpu : 0);
						ulong io = ((value != null) ? value.Io : 0);
						if (value == null)
						{
							IntPtr intPtr = Native.OpenProcess(4096, false, pid);
							if (intPtr != IntPtr.Zero)
							{
								try
								{
									path2 = Native.ImagePath(intPtr);
									Native.QueryProcessSample(intPtr, out creation, out cpu, out io);
								}
								finally
								{
									Native.CloseHandle(intPtr);
								}
							}
						}
						if (SelfBuildGuard.IsOwnBuild(nm, path2))
						{
							if (core.Release(pid, SuppressReason.Background))
							{
								ReportUntrack(pid);
							}
						}
						else if (!PerformanceScopeAllows(path2))
						{
							ReleaseBackgroundExemption(pid, nm, null);
						}
						else
						{
							if (TryProtectOverlayHost(pid, creation, nm, path2, sweepWithStableWhitelistCoreReady))
							{
								continue;
							}
							string libraryRootOfResult = FamilyBoundary.LibraryRootOf(path2, list);
							if (libraryRootOfResult == null && sweepWithStableWhitelistCoreReady)
							{
								libraryRootOfResult = root;
							}
							if (!FamilyBoundary.BasicBackgroundEligible(pid, selfPid, nm, path2, (allConditionsMet || matchesEligible) ? selfSession : (-1), selfSession, foregroundPid, hashSet4.Contains(pid), windowsPrefix, matchesSweepWithStableWhitelistCoreReady && hashSet2.Contains(pid), libraryRootOfResult, wideBackgroundScopeSucceeded, sweepWithStableWhitelistCoreReady, creation, platformRemoved, preserveOnForeground))
							{
								ReleaseBackgroundExemption(pid, nm, null);
								continue;
							}
							if (SelfProtectedRoster.Contains(nm))
							{
								ReleaseBackgroundExemption(pid, nm, null);
								processedCount4++;
								continue;
							}
							if (matchesEligible)
							{
								processedCount5++;
							}
							suppressionLevel = (EffSuppress ? BackgroundLevel() : SuppressionLevel.None);
							string nameOfResult = core.NameOf(pid);
							if (nameOfResult == null)
							{
								goto ApplyBackgroundDecision;
							}
							if (!string.Equals(nameOfResult, nm, StringComparison.OrdinalIgnoreCase))
							{
								ReportUntrack(pid);
								goto ApplyBackgroundDecision;
							}
							if (suppressionLevel == SuppressionLevel.None || !core.HasReason(pid, SuppressReason.Background) || core.LevelOf(pid, SuppressReason.Background) != suppressionLevel)
							{
								goto ApplyBackgroundDecision;
							}
							bool reconciled = false;
							if (!RunBackgroundPolicy(epoch, delegate
							{
								reconciled = core.Reconcile(pid, nm, SuppressReason.Background);
							}))
							{
								return;
							}
							if (!reconciled)
							{
								goto ApplyBackgroundDecision;
							}
						}
						continue;
					}
					goto FinishBackgroundCandidate;
					ApplyBackgroundDecision:
					if (suppressionLevel == SuppressionLevel.None)
					{
						if (core.Release(pid, SuppressReason.Background))
						{
							ReportUntrack(pid);
						}
					}
					else if (EffSuppress)
					{
						pending.Add(new BackgroundRequest
						{
							Pid = pid,
							Name = nm,
							Creation = creation,
							Cpu = cpu,
							Desired = suppressionLevel,
							Previous = core.LevelOf(pid, SuppressReason.Background),
							HadBackgroundReason = core.HasReason(pid, SuppressReason.Background)
						});
					}
					FinishBackgroundCandidate:;
				}
				catch
				{
				}
			}
			SuppressionCore.BatchResult batchResult = null;
			if (!RunBackgroundPolicy(epoch, delegate
			{
				core.BeginBatch();
				try
				{
					foreach (BackgroundRequest item in pending)
					{
						try
						{
							item.Result = core.Acquire(item.Pid, item.Name, SuppressReason.Background, null, item.Desired, item.Creation);
						}
						catch
						{
							item.Result = AcquireResult.AlreadyProtected;
						}
					}
				}
				finally
				{
					batchResult = core.EndBatch();
				}
			}))
			{
				return;
			}
			foreach (BackgroundRequest item2 in pending)
			{
				if ((item2.Result == AcquireResult.NewlyThrottled || item2.Result == AcquireResult.AlreadyThrottled) && (batchResult == null || !batchResult.WasApplied(item2.Pid)))
				{
					string failureDetailValue = ((batchResult != null) ? batchResult.FailureOf(item2.Pid) : "batch-missing");
					if (failureDetailValue == "self-protected")
					{
						item2.Result = AcquireResult.NewlyProtected;
						continue;
					}
					item2.Result = AcquireResult.ApplyFailed;
					item2.FailureDetail = failureDetailValue;
				}
			}
			foreach (BackgroundRequest item3 in pending)
			{
				if (item3.Result == AcquireResult.NewlyThrottled)
				{
					processedCount++;
					ReportTrack(item3.Pid, item3.Name);
					SuccessLogBatch.Record("background-applied", item3.Name, item3.Pid, item3.Creation,
						"level=" + item3.Desired);
				}
				else if (item3.Result == AcquireResult.AlreadyThrottled)
				{
					if (!item3.HadBackgroundReason)
					{
						ReportTrack(item3.Pid, item3.Name);
						SuccessLogBatch.Record("background-applied", item3.Name, item3.Pid, item3.Creation,
							"level=" + item3.Desired);
					}
					else if (item3.Previous != item3.Desired)
					{
						SuccessLogBatch.Record("background-level-changed", item3.Name, item3.Pid, item3.Creation,
							"from=" + item3.Previous + " to=" + item3.Desired);
					}
				}
				else if (item3.Result == AcquireResult.NewlyProtected)
				{
					processedCount2++;
				}
				else if (item3.Result == AcquireResult.ApplyFailed)
				{
					processedCount3++;
					SuccessLogBatch.FlushPending();
					Logger.Log(Lang.T("log.gamemodesweep.1") + item3.Name + "(pid " + item3.Pid + ") " + ApplyFailureText.Of(item3.FailureDetail) + Lang.T("log.gamemodeboost.24"));
				}
			}
			HashSet<int> coreProtected = new HashSet<int>(hashSet4);
			coreProtected.UnionWith(whitelistEvaluation.Protected);
			foreach (KeyValuePair<int, PlatformProcessOwner> platformProcess in whitelistEvaluation.PlatformProcesses)
			{
				if (!PlatformRuntimePolicy.Enabled(platformProcess.Value.Platform) && !whitelistEvaluation.Protected.Contains(platformProcess.Key))
				{
					coreProtected.Remove(platformProcess.Key);
				}
			}
			if (!RunBackgroundPolicy(epoch, delegate
			{
				SyncCoreBackground(coreProtected, foregroundPid);
			}))
			{
				return;
			}
			FamilyBoundary.PruneCatalogVerdicts(hashSet5);
			foreach (int item4 in core.PidsWith(SuppressReason.Background))
			{
				if (!hashSet5.Contains(item4) && core.Release(item4, SuppressReason.Background))
				{
					ReportUntrack(item4);
				}
			}
			if (!firstSweepSnapshot)
			{
				return;
			}
			if (EffSuppress)
			{
				string presetExtremeText = ((performancePreset == PerformancePreset.Extreme) ? Lang.T("preset.extreme") : (PresetValue.IsCompetitive(performancePreset) ? Lang.T("preset.competitive") : ((performancePreset == PerformancePreset.Handheld) ? Lang.T("preset.handheld") : Lang.T("preset.standard"))));
				bool matchesIsCompetitive = PresetValue.IsCompetitive(performancePreset) || performancePreset == PerformancePreset.Handheld;
				string message = presetExtremeText + (matchesIsCompetitive ? Lang.T("t.gamemodesweep.2") : Lang.T("t.gamemodesweep.3")) + (wideBackgroundScopeSucceeded ? Lang.T("t.gamemodesweep.5") : "");
				Logger.Log(Lang.F("background.scope", processedCount5, SuppressionCore.DisableDynamicBoost(BackgroundLevel(), false)));
				Logger.Log(Lang.T("log.gamemodesweep.1") + message + (SuppressionCore.GpuDemoteEnabled ? Lang.T("log.gamemodesweep.6") : "") + Lang.T("log.gamemodesweep.7") + processedCount + Lang.T("log.gamemodesweep.8") + ((processedCount3 > 0) ? (" " + processedCount3 + Lang.T("log.gamemodesweep.9")) : "") + ((processedCount2 > 0) ? (" " + processedCount2 + Lang.T("log.gamemodesweep.10")) : "") + ((processedCount4 > 0) ? (" " + processedCount4 + Lang.T("t.gamemodesweep.11")) : ""));
			}
			bool lockTaken4 = false;
			try
			{
				Monitor.Enter(instance = sync, ref lockTaken4);
				firstSweep = false;
			}
			finally
			{
				if (lockTaken4)
				{
					Monitor.Exit(instance);
				}
			}
		}
		private HashSet<int> CollectUserFacingFamily(int foregroundPid, WhitelistEvaluation whitelist, int excludeRootPid)
		{
			return CollectUserFacingFamily(foregroundPid, GameSessionDetector.VisibleWindowPids(true), whitelist, excludeRootPid);
		}
		private HashSet<int> CollectUserFacingFamily(int foregroundPid, HashSet<int> visible, WhitelistEvaluation whitelist, int excludeRootPid)
		{
			HashSet<int> hashSet = new HashSet<int>();
			foreach (KeyValuePair<int, WhitelistProcessInfo> process in whitelist.Processes)
			{
				try
				{
					int key = process.Key;
					if (excludeRootPid <= 0 || key != excludeRootPid)
					{
						WhitelistProcessInfo value = process.Value;
						if (selfSession >= 0 && value.Session == selfSession && (key == foregroundPid || visible.Contains(key)))
						{
							hashSet.Add(key);
						}
					}
				}
				catch
				{
				}
			}
			return FamilyBoundary.ExpandUserFacingFamily(whitelist.Parents, whitelist.Names, hashSet);
		}
		public bool NeedsWhitelistParentIdentity(int session)
		{
			if (session == selfSession)
			{
				return Interlocked.CompareExchange(ref whiteHasFamilyRules, 0, 0) != 0;
			}
			return false;
		}
		private void RefreshWhitelistFamilyFlagLocked()
		{
			int value = 0;
			foreach (WhitelistRule whiteRule in whiteRules)
			{
				if (RuleProtectsFamily(whiteRule))
				{
					value = 1;
					break;
				}
			}
			Interlocked.Exchange(ref whiteHasFamilyRules, value);
		}
		private static bool RuleProtectsFamily(WhitelistRule rule)
		{
			if (rule != null)
			{
				if (rule.Kind == WhitelistRuleKind.LegacyName)
				{
					return !IsPresetWhitelistName(rule.Value);
				}
				return true;
			}
			return false;
		}
		private WhitelistEvaluation EvaluateWhitelist(ProcessSnapshot all)
		{
			lock (whiteEvalSync)
			{
				if (all != null && object.ReferenceEquals(all, whiteEvalSnapshot) && whiteEvalCached != null)
				{
					bool allConditionsMet;
					lock (sync)
					{
						allConditionsMet = whiteEvalRevision == whiteRevision && whiteEvalMembersVersion == whiteFamilyMembersVersion && whiteEvalPlatformMask == PlatformRuntimePolicy.Mask && whiteEvalPlatformRenderer == ((activeDetection != null) ? activeDetection.RendererPid : 0) && whiteEvalPlatformRendererCreation == ((activeDetection == null) ? 0 : activeDetection.RendererCreation);
					}
					if (allConditionsMet)
					{
						return whiteEvalCached;
					}
				}
				WhitelistEvaluation result = EvaluateWhitelistLocked(all);
				whiteEvalSnapshot = all;
				whiteEvalCached = result;
				return result;
			}
		}
		private WhitelistEvaluation EvaluateWhitelistLocked(ProcessSnapshot all)
		{
			WhitelistEvaluation whitelistEvaluation = BuildWhitelistProcessSnapshot(all);
			int mask = PlatformRuntimePolicy.Mask;
			int rendererPid;
			long rendererCreation;
			lock (sync)
			{
				rendererPid = ((activeDetection != null) ? activeDetection.RendererPid : 0);
				rendererCreation = ((activeDetection == null) ? 0 : activeDetection.RendererCreation);
			}
			whitelistEvaluation.PlatformProcesses = CollectPlatformMembers(all);
			Dictionary<int, long> dictionary = new Dictionary<int, long>(runtimeFamilyMembers);
			foreach (KeyValuePair<int, PlatformProcessOwner> platformProcess in whitelistEvaluation.PlatformProcesses)
			{
				if (PlatformRuleEnabled(platformProcess.Value))
				{
					whitelistEvaluation.RuntimeProtected.Add(platformProcess.Key);
				}
				else
				{
					dictionary.Remove(platformProcess.Key);
				}
			}
			whitelistEvaluation.RuntimeProtected.UnionWith(WeGameRuntimeProtection.Collect(all, selfPid, selfSession));
			whitelistEvaluation.RuntimeProtected.UnionWith(ExpandApplicationFamily(whitelistEvaluation.Parents, whitelistEvaluation.RuntimeProtected, dictionary, whitelistEvaluation.Creations));
			runtimeFamilyMembers = new Dictionary<int, long>();
			foreach (int item in whitelistEvaluation.RuntimeProtected)
			{
				long value;
				if (whitelistEvaluation.Creations.TryGetValue(item, out value) && value > 0)
				{
					runtimeFamilyMembers[item] = value;
				}
			}
			whitelistEvaluation.Protected.UnionWith(whitelistEvaluation.RuntimeProtected);
			Dictionary<string, Dictionary<int, long>> dictionary2 = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
			int whiteRevisionSnapshot;
			int whiteFamilyMembersVersionSnapshot;
			List<WhitelistRule> list;
			lock (sync)
			{
				whiteRevisionSnapshot = whiteRevision;
				whiteFamilyMembersVersionSnapshot = whiteFamilyMembersVersion;
				list = new List<WhitelistRule>(whiteRules);
				foreach (KeyValuePair<string, Dictionary<int, long>> whiteFamilyMember in whiteFamilyMembers)
				{
					dictionary2[whiteFamilyMember.Key] = new Dictionary<int, long>(whiteFamilyMember.Value);
				}
			}
			Dictionary<int, long> creations = whitelistEvaluation.Creations;
			foreach (KeyValuePair<int, WhitelistProcessInfo> process in whitelistEvaluation.Processes)
			{
				if (process.Value.Creation > 0)
				{
					creations[process.Key] = process.Value.Creation;
				}
			}
			Dictionary<string, Dictionary<int, long>> dictionary3 = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
			WhitelistMatchIndex matchIndex = (list.Count >= 32 && whitelistEvaluation.Processes.Count >= 32)
				|| (list.Count >= 16 && whitelistEvaluation.Processes.Count >= 256)
				? new WhitelistMatchIndex(whitelistEvaluation.Processes, list) : null;
			foreach (WhitelistRule item2 in list)
			{
				HashSet<int> hashSet = new HashSet<int>();
				if (matchIndex == null || item2 == null || item2.Value == null)
				{
					foreach (KeyValuePair<int, WhitelistProcessInfo> process2 in whitelistEvaluation.Processes)
					{
						WhitelistProcessInfo value2 = process2.Value;
						if ((value2.Session == selfSession || (value2.Session == 0 && BackgroundServicePolicy.CandidateName(value2.Name))) && item2.MatchesNormalized(value2.Name, value2.Path) && (item2.Kind != 0 || !IsPresetWhitelistName(item2.Value) || IsTrustedPresetProcessPath(item2.Value, value2.Path)))
						{
							hashSet.Add(value2.Pid);
						}
					}
				}
				else
				{
					foreach (WhitelistProcessInfo value2 in matchIndex.Candidates(item2))
					{
						if ((value2.Session == selfSession || (value2.Session == 0 && BackgroundServicePolicy.CandidateName(value2.Name))) && item2.MatchesNormalized(value2.Name, value2.Path) && (item2.Kind != 0 || !IsPresetWhitelistName(item2.Value) || IsTrustedPresetProcessPath(item2.Value, value2.Path)))
						{
							hashSet.Add(value2.Pid);
						}
					}
				}
				HashSet<int> hashSet2 = hashSet;
				if (RuleProtectsFamily(item2))
				{
					HashSet<int> hashSet3 = new HashSet<int>();
					foreach (int item3 in hashSet)
					{
						WhitelistProcessInfo whitelistProcessInfo = whitelistEvaluation.Processes[item3];
						if (!string.IsNullOrEmpty(whitelistProcessInfo.Path) && !WhitelistRule.IsUnsafeFamilyAnchor(whitelistProcessInfo.Path))
						{
							hashSet3.Add(item3);
						}
					}
					Dictionary<int, long> value3;
					if (!dictionary2.TryGetValue(item2.Key, out value3))
					{
						value3 = new Dictionary<int, long>();
					}
					hashSet2 = ExpandApplicationFamily(whitelistEvaluation.Parents, hashSet3, value3, creations);
					hashSet2.UnionWith(hashSet);
					Dictionary<int, long> dictionary4 = new Dictionary<int, long>();
					foreach (int item4 in hashSet2)
					{
						long value4;
						if (creations.TryGetValue(item4, out value4) && value4 > 0)
						{
							dictionary4[item4] = value4;
						}
					}
					dictionary3[item2.Key] = dictionary4;
				}
				whitelistEvaluation.ByRule[item2.Key] = hashSet2;
				whitelistEvaluation.Protected.UnionWith(hashSet2);
			}
			lock (sync)
			{
				bool allConditionsMet = whiteRevisionSnapshot == whiteRevision && whiteFamilyMembersVersionSnapshot == whiteFamilyMembersVersion;
				if (allConditionsMet)
				{
					whiteFamilyMembers.Clear();
					foreach (KeyValuePair<string, Dictionary<int, long>> item5 in dictionary3)
					{
						whiteFamilyMembers[item5.Key] = item5.Value;
					}
					whiteFamilyMembersVersion++;
				}
				whiteEvalRevision = (allConditionsMet ? whiteRevisionSnapshot : (whiteRevision - 1));
				whiteEvalMembersVersion = whiteFamilyMembersVersion;
				whiteEvalPlatformMask = mask;
				whiteEvalPlatformRenderer = rendererPid;
				whiteEvalPlatformRendererCreation = rendererCreation;
				return whitelistEvaluation;
			}
		}
		private WhitelistEvaluation BuildWhitelistProcessSnapshot(ProcessSnapshot all)
		{
			WhitelistEvaluation whitelistEvaluation = new WhitelistEvaluation();
			if (all == null)
			{
				return whitelistEvaluation;
			}
			ProcEntry[] entries = all.Entries;
			foreach (ProcEntry procEntry in entries)
			{
				try
				{
					int pid = procEntry.Pid;
					WhitelistProcessInfo whitelistProcessInfo = new WhitelistProcessInfo();
					whitelistProcessInfo.Pid = pid;
					WhitelistProcessInfo whitelistProcessInfo2 = whitelistProcessInfo;
					whitelistProcessInfo2.Name = WhitelistRule.NormalizeName(procEntry.Name);
					whitelistProcessInfo2.Session = procEntry.Session;
					whitelistEvaluation.Processes[pid] = whitelistProcessInfo2;
					whitelistEvaluation.Names[pid] = whitelistProcessInfo2.Name;
					if (pid > 4 && selfSession >= 0 && (whitelistProcessInfo2.Session == selfSession || (whitelistProcessInfo2.Session == 0 && BackgroundServicePolicy.CandidateName(whitelistProcessInfo2.Name))) && procEntry.Path != null)
					{
						whitelistProcessInfo2.Path = WhitelistRule.NormalizeImagePath(procEntry.Path);
						whitelistProcessInfo2.Parent = procEntry.ParentPid;
						whitelistProcessInfo2.Creation = procEntry.Creation;
						whitelistEvaluation.Creations[pid] = whitelistProcessInfo2.Creation;
						whitelistProcessInfo2.Cpu = procEntry.Cpu;
						whitelistProcessInfo2.Io = procEntry.Io;
						if (whitelistProcessInfo2.Session == selfSession)
						{
							whitelistEvaluation.Parents[pid] = whitelistProcessInfo2.Parent;
						}
					}
				}
				catch
				{
				}
			}
			return whitelistEvaluation;
		}
		internal static HashSet<int> ExpandApplicationFamily(Dictionary<int, int> parents, HashSet<int> anchors, Dictionary<int, long> retained, Dictionary<int, long> currentCreation)
		{
			HashSet<int> hashSet = ((anchors == null) ? new HashSet<int>() : new HashSet<int>(anchors));
			if (retained != null && currentCreation != null)
			{
				foreach (KeyValuePair<int, long> item in retained)
				{
					long value;
					if (item.Value > 0 && currentCreation.TryGetValue(item.Key, out value) && value == item.Value)
					{
						hashSet.Add(item.Key);
					}
				}
			}
			bool expandApplicationFamilyReady;
			do
			{
				expandApplicationFamilyReady = false;
				if (parents == null)
				{
					break;
				}
				foreach (KeyValuePair<int, int> parent in parents)
				{
					long value2;
					long value3;
					if (!hashSet.Contains(parent.Key) && hashSet.Contains(parent.Value) && currentCreation != null && currentCreation.TryGetValue(parent.Key, out value2) && currentCreation.TryGetValue(parent.Value, out value3) && value2 > 0 && value3 > 0 && value2 > value3)
					{
						hashSet.Add(parent.Key);
						expandApplicationFamilyReady = true;
					}
				}
			}
			while (expandApplicationFamilyReady);
			return hashSet;
		}
		private bool ServiceWhitelisted(string name, string imagePath)
		{
			List<WhitelistRule> rules;
			lock (sync)
			{
				rules = new List<WhitelistRule>(whiteRules);
			}
			return ServiceRuleMatches(rules, name, imagePath);
		}
		private static bool IsPresetWhitelistName(string name)
		{
			string[] presetWhitelist = SystemProcessCatalog.PresetWhitelist;
			foreach (string a in presetWhitelist)
			{
				if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			return false;
		}
		private List<WhitelistRuleView> SnapshotWhitelistRuleViews()
		{
			ProcessSnapshot all = ProcessSnapshotSource.Capture(selfSession);
			WhitelistEvaluation whitelistEvaluation = EvaluateWhitelist(all);
			List<WhitelistRuleView> list = new List<WhitelistRuleView>();
			AppendPlatformRuleViews(list, whitelistEvaluation.PlatformProcesses);
			lock (sync)
			{
				foreach (WhitelistRule whiteRule in whiteRules)
				{
					HashSet<int> value;
					int currentMatches = (whitelistEvaluation.ByRule.TryGetValue(whiteRule.Key, out value) ? value.Count : 0);
					list.Add(new WhitelistRuleView(whiteRule, currentMatches, whiteRule.Kind == WhitelistRuleKind.LegacyName && IsPresetWhitelistName(whiteRule.Value)));
				}
				return list;
			}
		}
		private int ReleaseCurrentWhitelistMatches(out int matched)
		{
			matched = 0;
			lock (whiteEvalSync)
			{
				ProcessSnapshot all = ProcessSnapshotSource.Capture(selfSession);
				WhitelistEvaluation whitelistEvaluation = EvaluateWhitelist(all);
				matched = whitelistEvaluation.Protected.Count;
				int processedCount = 0;
				foreach (int item in whitelistEvaluation.Protected)
				{
					WhitelistProcessInfo value;
					if (whitelistEvaluation.Processes.TryGetValue(item, out value) && value.Creation > 0)
					{
						if (core.ReleaseIfCreation(item, SuppressReason.Background, value.Creation))
						{
							ReportUntrack(item);
							processedCount++;
						}
					}
				}
				return processedCount;
			}
		}
		private void ObserveWhitelistProcessChanges(ProcessChangeBatch batch)
		{
			if (batch == null || batch.Changes.Length == 0)
			{
				return;
			}
			lock (whiteEvalSync)
			{
				List<WhitelistRule> list = new List<WhitelistRule>();
				Dictionary<int, long> dictionary = new Dictionary<int, long>();
				HashSet<int> hashSet = new HashSet<int>();
				int whiteRevisionSnapshot;
				lock (sync)
				{
					whiteRevisionSnapshot = whiteRevision;
					foreach (WhitelistRule whiteRule in whiteRules)
					{
						if (RuleProtectsFamily(whiteRule))
						{
							list.Add(whiteRule);
						}
					}
					ProcessChange[] changes = batch.Changes;
					foreach (ProcessChange processChange in changes)
					{
						if (processChange == null || processChange.Kind != ProcessChangeKind.Stopped)
						{
							continue;
						}
						foreach (WhitelistRule item in list)
						{
							Dictionary<int, long> value;
							if (whiteFamilyMembers.TryGetValue(item.Key, out value) && value.ContainsKey(processChange.Pid))
							{
								hashSet.Add(processChange.Pid);
								break;
							}
						}
					}
				}
				if (list.Count == 0)
				{
					return;
				}
				Dictionary<int, long> stopIdentity = CaptureStoppedProcessIdentity(hashSet);
				lock (sync)
				{
					if (whiteRevisionSnapshot != whiteRevision)
					{
						return;
					}
					foreach (WhitelistRule item2 in list)
					{
						Dictionary<int, long> value2;
						if (!whiteFamilyMembers.TryGetValue(item2.Key, out value2))
						{
							value2 = new Dictionary<int, long>();
							whiteFamilyMembers[item2.Key] = value2;
						}
						foreach (KeyValuePair<int, long> item3 in ApplyApplicationFamilyEvents(item2, value2, batch.Changes, selfSession, stopIdentity))
						{
							dictionary[item3.Key] = item3.Value;
						}
					}
					whiteFamilyMembersVersion++;
				}
				foreach (KeyValuePair<int, long> item4 in dictionary)
				{
					if (core.ReleaseIfCreation(item4.Key, SuppressReason.Background, item4.Value))
					{
						ReportUntrack(item4.Key);
					}
				}
			}
		}
		internal static Dictionary<int, long> ApplyApplicationFamilyEvents(WhitelistRule rule, Dictionary<int, long> members, IList<ProcessChange> changes, int ownerSession)
		{
			return ApplyApplicationFamilyEvents(rule, members, changes, ownerSession, null);
		}
		internal static Dictionary<int, long> ApplyApplicationFamilyEvents(WhitelistRule rule, Dictionary<int, long> members, IList<ProcessChange> changes, int ownerSession, IDictionary<int, long> stopIdentity)
		{
			Dictionary<int, long> dictionary = new Dictionary<int, long>();
			if (rule == null || members == null || changes == null)
			{
				return dictionary;
			}
			List<ProcessChange> list = new List<ProcessChange>();
			foreach (ProcessChange change in changes)
			{
				if (change != null && change.Pid > 0)
				{
					list.Add(change);
				}
			}
			list.Sort((ProcessChange left, ProcessChange right) => left.Sequence.CompareTo(right.Sequence));
			Dictionary<int, ProcessChange> dictionary2 = new Dictionary<int, ProcessChange>();
			foreach (ProcessChange item in list)
			{
				if (item.Kind == ProcessChangeKind.Stopped)
				{
					long value;
					long value2;
					if (stopIdentity != null && stopIdentity.TryGetValue(item.Pid, out value) && members.TryGetValue(item.Pid, out value2) && (value <= 0 || value != value2))
					{
						members.Remove(item.Pid);
						dictionary.Remove(item.Pid);
						dictionary2.Remove(item.Pid);
					}
				}
				else
				{
					if (item.Creation <= 0 || item.Session != ownerSession)
					{
						continue;
					}
					long value3;
					if (members.TryGetValue(item.Pid, out value3) && value3 != item.Creation)
					{
						members.Remove(item.Pid);
						dictionary.Remove(item.Pid);
					}
					dictionary2[item.Pid] = item;
					string normalizedName = WhitelistRule.NormalizeName(item.Name);
					string imagePath = WhitelistRule.NormalizeImagePath(item.Path);
					if ((rule.MatchesNormalized(normalizedName, imagePath) && !WhitelistRule.IsUnsafeFamilyAnchor(imagePath)) || HasVerifiedFamilyParent(item, members, dictionary2))
					{
						bool anyConditionMet = !members.TryGetValue(item.Pid, out value3) || value3 != item.Creation;
						members[item.Pid] = item.Creation;
						if (anyConditionMet)
						{
							dictionary[item.Pid] = item.Creation;
						}
					}
				}
			}
			bool applyApplicationFamilyEventsReady;
			do
			{
				applyApplicationFamilyEventsReady = false;
				foreach (ProcessChange value4 in dictionary2.Values)
				{
					if (!members.ContainsKey(value4.Pid) && HasVerifiedFamilyParent(value4, members, dictionary2))
					{
						members[value4.Pid] = value4.Creation;
						dictionary[value4.Pid] = value4.Creation;
						applyApplicationFamilyEventsReady = true;
					}
				}
			}
			while (applyApplicationFamilyEventsReady);
			return dictionary;
		}
		private void PruneWhitelistFamilyMembersIfDue(ProcessSnapshot all)
		{
			if (all == null || Interlocked.CompareExchange(ref whiteHasFamilyRules, 0, 0) == 0)
			{
				return;
			}
			long ticks = DateTime.UtcNow.Ticks;
			long lastWhiteFamilyPruneTicksSnapshot = Interlocked.Read(ref lastWhiteFamilyPruneTicks);
			if ((lastWhiteFamilyPruneTicksSnapshot > 0 && ticks >= lastWhiteFamilyPruneTicksSnapshot && ticks - lastWhiteFamilyPruneTicksSnapshot < 200000000) || Interlocked.CompareExchange(ref lastWhiteFamilyPruneTicks, ticks, lastWhiteFamilyPruneTicksSnapshot) != lastWhiteFamilyPruneTicksSnapshot)
			{
				return;
			}
			lock (whiteEvalSync)
			{
				HashSet<int> hashSet = new HashSet<int>();
				lock (sync)
				{
					foreach (Dictionary<int, long> value2 in whiteFamilyMembers.Values)
					{
						hashSet.UnionWith(value2.Keys);
					}
				}
				if (hashSet.Count == 0)
				{
					return;
				}
				HashSet<int> hashSet2 = new HashSet<int>();
				Dictionary<int, long> dictionary = new Dictionary<int, long>();
				ProcEntry[] entries = all.Entries;
				foreach (ProcEntry procEntry in entries)
				{
					int pid = procEntry.Pid;
					if (hashSet.Contains(pid))
					{
						hashSet2.Add(pid);
						dictionary[pid] = procEntry.Creation;
					}
				}
				lock (sync)
				{
					foreach (Dictionary<int, long> value3 in whiteFamilyMembers.Values)
					{
						List<int> list = null;
						foreach (KeyValuePair<int, long> item in value3)
						{
							long value;
							if (!hashSet2.Contains(item.Key) || (dictionary.TryGetValue(item.Key, out value) && value != item.Value))
							{
								if (list == null)
								{
									list = new List<int>();
								}
								list.Add(item.Key);
							}
						}
						if (list == null)
						{
							continue;
						}
						foreach (int item2 in list)
						{
							value3.Remove(item2);
						}
						whiteFamilyMembersVersion++;
					}
				}
			}
		}
		private static bool HasVerifiedFamilyParent(ProcessChange change, Dictionary<int, long> members, Dictionary<int, ProcessChange> liveStarts)
		{
			long value;
			if (change == null || change.ParentPid <= 0 || change.Creation <= 0 || !members.TryGetValue(change.ParentPid, out value) || value <= 0 || change.Creation <= value)
			{
				return false;
			}
			long parentCreation = change.ParentCreation;
			ProcessChange value2;
			if (parentCreation <= 0 && liveStarts != null && liveStarts.TryGetValue(change.ParentPid, out value2))
			{
				parentCreation = value2.Creation;
			}
			return parentCreation == value;
		}
	}
}
