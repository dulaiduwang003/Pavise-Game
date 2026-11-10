using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool LoadAutoIgnore()
        {
            try
            {
                HashSet<string> normalizedGameNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string entryText in (File.Exists(autoIgnorePath) ? File.ReadAllLines(autoIgnorePath) : new string[0]))
                {
                    string entryTextTrimmed = entryText.Trim();
                    if (entryTextTrimmed.Length != 0 && !entryTextTrimmed.StartsWith("#"))
                    {
                        normalizedGameNames.Add(entryTextTrimmed);
                    }
                }
                autoAddIgnore.Clear();
                autoAddIgnore.UnionWith(normalizedGameNames);
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
            bool profileChanged = false;
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
                    GameProfile profile = gameProfile.Clone();
                    if (on)
                    {
                        profile.Overrides["GameFamilyBackground"] = "1";
                    }
                    else
                    {
                        profile.Overrides.Remove("GameFamilyBackground");
                    }
                    List<GameProfile> profilesSnapshot = new List<GameProfile>(profiles);
                    int index = profiles.IndexOf(gameProfile);
                    profilesSnapshot[index] = profile;
                    if (!SaveProfileSnapshotLocked(profilesSnapshot))
                    {
                        return false;
                    }
                    profiles[index] = profile;
                    if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
                    {
                        activeDetection.Profile = profile.Clone();
                    }
                    if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
                    {
                        stickyDetection.Profile = profile.Clone();
                    }
                    Interlocked.Increment(ref familyPolicyEpoch);
                    firstSweep = true;
                    profileChanged = true;
                }
            }
            if (profileChanged)
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
            List<WhitelistRuleView> whitelistRuleViews = SnapshotWhitelistRuleViews();
            whitelistRuleViews.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
            {
                int kindOrder = a.Rule.Kind.CompareTo(b.Rule.Kind);
                return (kindOrder == 0) ? string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase) : kindOrder;
            });
            return whitelistRuleViews;
        }
        public List<WhitelistRuleView> GetWhitelistRulesFast()
        {
            List<WhitelistRuleView> whitelistRuleViews = new List<WhitelistRuleView>();
            AppendPlatformRuleViews(whitelistRuleViews, null);
            lock (sync)
            {
                foreach (WhitelistRule whiteRule in whiteRules)
                {
                    whitelistRuleViews.Add(new WhitelistRuleView(whiteRule, -1, whiteRule.Kind == WhitelistRuleKind.LegacyName && IsPresetWhitelistName(whiteRule.Value)));
                }
            }
            whitelistRuleViews.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
            {
                int kindOrder = a.Rule.Kind.CompareTo(b.Rule.Kind);
                return (kindOrder == 0) ? string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase) : kindOrder;
            });
            return whitelistRuleViews;
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
            int releasedCount;
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
                    List<WhitelistRule> whiteRulesSnapshot = new List<WhitelistRule>(whiteRules);
                    whiteRulesSnapshot.Add(rule);
                    List<WhitelistRule> rules = whiteRulesSnapshot;
                    if (!SaveWhite(rules))
                    {
                        whitelistLastError = Lang.T("white.save.failed");
                        return false;
                    }
                    AddWhiteRuleNoSave(rule);
                    whitelistLastError = "";
                }
                releasedCount = ReleaseCurrentWhitelistMatches(out matched);
            }
            Logger.Log(string.Concat(Lang.T("log.gamemodelibrary.17", 0), rule.Kind, " ", rule.Value, Lang.T("log.gamemodelibrary.18", 0), matched, Lang.T("log.gamemodelibrary.19", 0), releasedCount, Lang.T("log.gamemodelibrary.20", 0)));
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
                    List<WhitelistRule> whiteRulesSnapshot = new List<WhitelistRule>(whiteRules);
                    whiteRulesSnapshot.Remove(whitelistRule);
                    if (!SaveWhite(whiteRulesSnapshot))
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
            List<WhitelistRule> rules = new List<WhitelistRule>();
            HashSet<string> ruleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string currentPresetWhitelist in SystemProcessCatalog.PresetWhitelist)
            {
                WhitelistRule rule;
                if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, currentPresetWhitelist, out rule) && ruleKeys.Add(rule.Key))
                {
                    rules.Add(rule);
                }
            }
            int releasedCount;
            int matched;
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping)
                    {
                        return false;
                    }
                    if (!SaveWhite(rules))
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
                    foreach (WhitelistRule currentRule in rules)
                    {
                        AddWhiteRuleNoSave(currentRule);
                    }
                    whitelistLastError = "";
                }
                releasedCount = ReleaseCurrentWhitelistMatches(out matched);
            }
            Logger.Log(Lang.T("log.gamemodelibrary.21", 0) + SystemProcessCatalog.PresetWhitelist.Length + Lang.T("log.gamemodelibrary.22", 0) + matched + Lang.T("log.gamemodelibrary.19", 0) + releasedCount + Lang.T("log.gamemodelibrary.20", 0));
            RequestPolicyApply("ResetWhitelist");
            return true;
        }
        internal static string BuildWhitelistFooter(IList<WhitelistRule> rules)
        {
            ulong rulesDigest = 1469598103934665603uL;
            int processedCount = 0;
            if (rules != null)
            {
                foreach (WhitelistRule rule in rules)
                {
                    if (rule != null)
                    {
                        string serializedRule = rule.Serialize();
                        processedCount++;
                        for (int i = 0; i < serializedRule.Length; i++)
                        {
                            rulesDigest ^= (byte)serializedRule[i];
                            rulesDigest *= 1099511628211L;
                        }
                        rulesDigest ^= 0xA;
                        rulesDigest *= 1099511628211L;
                    }
                }
            }
            return "PAVISE_WHITELIST_END|" + processedCount + "|" + rulesDigest.ToString("X16");
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
            GameDetection currentGameDetection = new GameDetection();
            currentGameDetection.Profile = src.Profile;
            currentGameDetection.RendererPid = src.RendererPid;
            currentGameDetection.RendererCreation = src.RendererCreation;
            currentGameDetection.RendererName = src.RendererName;
            currentGameDetection.RendererPath = src.RendererPath;
            currentGameDetection.RendererForeground = src.RendererForeground;
            currentGameDetection.RendererCandidateSelected = src.RendererCandidateSelected;
            currentGameDetection.RendererUserSelected = src.RendererUserSelected;
            currentGameDetection.RendererLearnable = src.RendererLearnable;
            currentGameDetection.Evidence = src.Evidence;
            foreach (int familyPid in src.FamilyPids)
            {
                if (stickyIds.ContainsKey(familyPid))
                {
                    currentGameDetection.FamilyPids.Add(familyPid);
                }
            }
            foreach (string familyName in src.FamilyNames)
            {
                currentGameDetection.FamilyNames.Add(familyName);
            }
            return currentGameDetection;
        }
        private List<int> LiveStickyFamily()
        {
            List<int> liveFamilyPids = new List<int>();
            foreach (KeyValuePair<int, GameId> stickyId in stickyIds)
            {
                if (AliveWithIdentity(stickyId.Key))
                {
                    liveFamilyPids.Add(stickyId.Key);
                }
            }
            return liveFamilyPids;
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
                    bool cacheCurrent;
                    lock (sync)
                    {
                        cacheCurrent = whiteEvalRevision == whiteRevision && whiteEvalMembersVersion == whiteFamilyMembersVersion && whiteEvalPlatformMask == PlatformRuntimePolicy.Mask && whiteEvalPlatformRenderer == ((activeDetection != null) ? activeDetection.RendererPid : 0) && whiteEvalPlatformRendererCreation == ((activeDetection == null) ? 0 : activeDetection.RendererCreation);
                    }
                    if (cacheCurrent)
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
            Dictionary<int, long> runtimeFamilyMembersSnapshot = new Dictionary<int, long>(runtimeFamilyMembers);
            foreach (KeyValuePair<int, PlatformProcessOwner> platformProcess in whitelistEvaluation.PlatformProcesses)
            {
                if (PlatformRuleEnabled(platformProcess.Value))
                {
                    whitelistEvaluation.RuntimeProtected.Add(platformProcess.Key);
                }
                else
                {
                    runtimeFamilyMembersSnapshot.Remove(platformProcess.Key);
                }
            }
            whitelistEvaluation.RuntimeProtected.UnionWith(WeGameRuntimeProtection.Collect(all, selfPid, selfSession));
            whitelistEvaluation.RuntimeProtected.UnionWith(ExpandApplicationFamily(whitelistEvaluation.Parents, whitelistEvaluation.RuntimeProtected, runtimeFamilyMembersSnapshot, whitelistEvaluation.Creations));
            runtimeFamilyMembers = new Dictionary<int, long>();
            foreach (int runtimeProtected in whitelistEvaluation.RuntimeProtected)
            {
                long creation;
                if (whitelistEvaluation.Creations.TryGetValue(runtimeProtected, out creation) && creation > 0)
                {
                    runtimeFamilyMembers[runtimeProtected] = creation;
                }
            }
            whitelistEvaluation.Protected.UnionWith(whitelistEvaluation.RuntimeProtected);
            Dictionary<string, Dictionary<int, long>> previousFamiliesByRule = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
            int whiteRevisionSnapshot;
            int whiteFamilyMembersVersionSnapshot;
            List<WhitelistRule> rulesSnapshot;
            lock (sync)
            {
                whiteRevisionSnapshot = whiteRevision;
                whiteFamilyMembersVersionSnapshot = whiteFamilyMembersVersion;
                rulesSnapshot = new List<WhitelistRule>(whiteRules);
                foreach (KeyValuePair<string, Dictionary<int, long>> whiteFamilyMember in whiteFamilyMembers)
                {
                    previousFamiliesByRule[whiteFamilyMember.Key] = new Dictionary<int, long>(whiteFamilyMember.Value);
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
            Dictionary<string, Dictionary<int, long>> updatedFamiliesByRule = new Dictionary<string, Dictionary<int, long>>(StringComparer.OrdinalIgnoreCase);
            WhitelistMatchIndex matchIndex = (rulesSnapshot.Count >= 32 && whitelistEvaluation.Processes.Count >= 32)
                || (rulesSnapshot.Count >= 16 && whitelistEvaluation.Processes.Count >= 256)
                ? new WhitelistMatchIndex(whitelistEvaluation.Processes, rulesSnapshot) : null;
            foreach (WhitelistRule currentRulesSnapshot in rulesSnapshot)
            {
                HashSet<int> matchedPids = new HashSet<int>();
                if (matchIndex == null || currentRulesSnapshot == null || currentRulesSnapshot.Value == null)
                {
                    foreach (KeyValuePair<int, WhitelistProcessInfo> process2 in whitelistEvaluation.Processes)
                    {
                        WhitelistProcessInfo matchedProcess = process2.Value;
                        if ((matchedProcess.Session == selfSession || (matchedProcess.Session == 0 && BackgroundServicePolicy.CandidateName(matchedProcess.Name))) && currentRulesSnapshot.MatchesNormalized(matchedProcess.Name, matchedProcess.Path) && (currentRulesSnapshot.Kind != 0 || !IsPresetWhitelistName(currentRulesSnapshot.Value) || IsTrustedPresetProcessPath(currentRulesSnapshot.Value, matchedProcess.Path)))
                        {
                            matchedPids.Add(matchedProcess.Pid);
                        }
                    }
                }
                else
                {
                    foreach (WhitelistProcessInfo indexedProcess in matchIndex.Candidates(currentRulesSnapshot))
                    {
                        if ((indexedProcess.Session == selfSession || (indexedProcess.Session == 0 && BackgroundServicePolicy.CandidateName(indexedProcess.Name))) && currentRulesSnapshot.MatchesNormalized(indexedProcess.Name, indexedProcess.Path) && (currentRulesSnapshot.Kind != 0 || !IsPresetWhitelistName(currentRulesSnapshot.Value) || IsTrustedPresetProcessPath(currentRulesSnapshot.Value, indexedProcess.Path)))
                        {
                            matchedPids.Add(indexedProcess.Pid);
                        }
                    }
                }
                HashSet<int> protectedFamilyPids = matchedPids;
                if (RuleProtectsFamily(currentRulesSnapshot))
                {
                    HashSet<int> safeFamilyAnchors = new HashSet<int>();
                    foreach (int matchedPid in matchedPids)
                    {
                        WhitelistProcessInfo whitelistProcessInfo = whitelistEvaluation.Processes[matchedPid];
                        if (!string.IsNullOrEmpty(whitelistProcessInfo.Path) && !WhitelistRule.IsUnsafeFamilyAnchor(whitelistProcessInfo.Path))
                        {
                            safeFamilyAnchors.Add(matchedPid);
                        }
                    }
                    Dictionary<int, long> retainedCreationsByPid;
                    if (!previousFamiliesByRule.TryGetValue(currentRulesSnapshot.Key, out retainedCreationsByPid))
                    {
                        retainedCreationsByPid = new Dictionary<int, long>();
                    }
                    protectedFamilyPids = ExpandApplicationFamily(whitelistEvaluation.Parents, safeFamilyAnchors, retainedCreationsByPid, creations);
                    protectedFamilyPids.UnionWith(matchedPids);
                    Dictionary<int, long> familyCreationsByPid = new Dictionary<int, long>();
                    foreach (int protectedFamilyPid in protectedFamilyPids)
                    {
                        long memberCreation;
                        if (creations.TryGetValue(protectedFamilyPid, out memberCreation) && memberCreation > 0)
                        {
                            familyCreationsByPid[protectedFamilyPid] = memberCreation;
                        }
                    }
                    updatedFamiliesByRule[currentRulesSnapshot.Key] = familyCreationsByPid;
                }
                whitelistEvaluation.ByRule[currentRulesSnapshot.Key] = protectedFamilyPids;
                whitelistEvaluation.Protected.UnionWith(protectedFamilyPids);
            }
            lock (sync)
            {
                bool revisionsCurrent = whiteRevisionSnapshot == whiteRevision && whiteFamilyMembersVersionSnapshot == whiteFamilyMembersVersion;
                if (revisionsCurrent)
                {
                    whiteFamilyMembers.Clear();
                    foreach (KeyValuePair<string, Dictionary<int, long>> familyEntry in updatedFamiliesByRule)
                    {
                        whiteFamilyMembers[familyEntry.Key] = familyEntry.Value;
                    }
                    whiteFamilyMembersVersion++;
                }
                whiteEvalRevision = (revisionsCurrent ? whiteRevisionSnapshot : (whiteRevision - 1));
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
            foreach (ProcEntry procEntry in all.Entries)
            {
                try
                {
                    int pid = procEntry.Pid;
                    WhitelistProcessInfo currentWhitelistProcessInfo = new WhitelistProcessInfo();
                    currentWhitelistProcessInfo.Pid = pid;
                    currentWhitelistProcessInfo.Name = WhitelistRule.NormalizeName(procEntry.Name);
                    currentWhitelistProcessInfo.Session = procEntry.Session;
                    whitelistEvaluation.Processes[pid] = currentWhitelistProcessInfo;
                    whitelistEvaluation.Names[pid] = currentWhitelistProcessInfo.Name;
                    if (pid > 4 && selfSession >= 0 && (currentWhitelistProcessInfo.Session == selfSession || (currentWhitelistProcessInfo.Session == 0 && BackgroundServicePolicy.CandidateName(currentWhitelistProcessInfo.Name))) && procEntry.Path != null)
                    {
                        currentWhitelistProcessInfo.Path = WhitelistRule.NormalizeImagePath(procEntry.Path);
                        currentWhitelistProcessInfo.Parent = procEntry.ParentPid;
                        currentWhitelistProcessInfo.Creation = procEntry.Creation;
                        whitelistEvaluation.Creations[pid] = currentWhitelistProcessInfo.Creation;
                        currentWhitelistProcessInfo.Cpu = procEntry.Cpu;
                        currentWhitelistProcessInfo.Io = procEntry.Io;
                        if (currentWhitelistProcessInfo.Session == selfSession)
                        {
                            whitelistEvaluation.Parents[pid] = currentWhitelistProcessInfo.Parent;
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
            HashSet<int> familyPids = ((anchors == null) ? new HashSet<int>() : new HashSet<int>(anchors));
            if (retained != null && currentCreation != null)
            {
                foreach (KeyValuePair<int, long> retainedEntry in retained)
                {
                    long candidateCreation;
                    if (retainedEntry.Value > 0 && currentCreation.TryGetValue(retainedEntry.Key, out candidateCreation) && candidateCreation == retainedEntry.Value)
                    {
                        familyPids.Add(retainedEntry.Key);
                    }
                }
            }
            bool membersAdded;
            do
            {
                membersAdded = false;
                if (parents == null)
                {
                    break;
                }
                foreach (KeyValuePair<int, int> parent in parents)
                {
                    long childCreation;
                    long parentCreation;
                    if (!familyPids.Contains(parent.Key) && familyPids.Contains(parent.Value) && currentCreation != null && currentCreation.TryGetValue(parent.Key, out childCreation) && currentCreation.TryGetValue(parent.Value, out parentCreation) && childCreation > 0 && parentCreation > 0 && childCreation > parentCreation)
                    {
                        familyPids.Add(parent.Key);
                        membersAdded = true;
                    }
                }
            }
            while (membersAdded);
            return familyPids;
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
            foreach (string a in SystemProcessCatalog.PresetWhitelist)
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
            List<WhitelistRuleView> whitelistRuleViews = new List<WhitelistRuleView>();
            AppendPlatformRuleViews(whitelistRuleViews, whitelistEvaluation.PlatformProcesses);
            lock (sync)
            {
                foreach (WhitelistRule whiteRule in whiteRules)
                {
                    HashSet<int> value;
                    int currentMatches = (whitelistEvaluation.ByRule.TryGetValue(whiteRule.Key, out value) ? value.Count : 0);
                    whitelistRuleViews.Add(new WhitelistRuleView(whiteRule, currentMatches, whiteRule.Kind == WhitelistRuleKind.LegacyName && IsPresetWhitelistName(whiteRule.Value)));
                }
                return whitelistRuleViews;
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
                foreach (int integerValueEntry in whitelistEvaluation.Protected)
                {
                    WhitelistProcessInfo processInfo;
                    if (whitelistEvaluation.Processes.TryGetValue(integerValueEntry, out processInfo) && processInfo.Creation > 0)
                    {
                        if (core.ReleaseIfCreation(integerValueEntry, SuppressReason.Background, processInfo.Creation))
                        {
                            ReportUntrack(integerValueEntry);
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
                List<WhitelistRule> rulesSnapshot = new List<WhitelistRule>();
                Dictionary<int, long> protectedCreationsByPid = new Dictionary<int, long>();
                HashSet<int> excludedPlatformPids = new HashSet<int>();
                int whiteRevisionSnapshot;
                lock (sync)
                {
                    whiteRevisionSnapshot = whiteRevision;
                    foreach (WhitelistRule whiteRule in whiteRules)
                    {
                        if (RuleProtectsFamily(whiteRule))
                        {
                            rulesSnapshot.Add(whiteRule);
                        }
                    }
                    foreach (ProcessChange processChange in batch.Changes)
                    {
                        if (processChange == null || processChange.Kind != ProcessChangeKind.Stopped)
                        {
                            continue;
                        }
                        foreach (WhitelistRule currentRulesSnapshot in rulesSnapshot)
                        {
                            Dictionary<int, long> whiteFamilyMember;
                            if (whiteFamilyMembers.TryGetValue(currentRulesSnapshot.Key, out whiteFamilyMember) && whiteFamilyMember.ContainsKey(processChange.Pid))
                            {
                                excludedPlatformPids.Add(processChange.Pid);
                                break;
                            }
                        }
                    }
                }
                if (rulesSnapshot.Count == 0)
                {
                    return;
                }
                Dictionary<int, long> stopIdentity = CaptureStoppedProcessIdentity(excludedPlatformPids);
                lock (sync)
                {
                    if (whiteRevisionSnapshot != whiteRevision)
                    {
                        return;
                    }
                    foreach (WhitelistRule candidateRulesSnapshot in rulesSnapshot)
                    {
                        Dictionary<int, long> trackedFamily;
                        if (!whiteFamilyMembers.TryGetValue(candidateRulesSnapshot.Key, out trackedFamily))
                        {
                            trackedFamily = new Dictionary<int, long>();
                            whiteFamilyMembers[candidateRulesSnapshot.Key] = trackedFamily;
                        }
                        foreach (KeyValuePair<int, long> applyApplicationFamilyEventEntry in ApplyApplicationFamilyEvents(candidateRulesSnapshot, trackedFamily, batch.Changes, selfSession, stopIdentity))
                        {
                            protectedCreationsByPid[applyApplicationFamilyEventEntry.Key] = applyApplicationFamilyEventEntry.Value;
                        }
                    }
                    whiteFamilyMembersVersion++;
                }
                foreach (KeyValuePair<int, long> protectedProcessEntry in protectedCreationsByPid)
                {
                    if (core.ReleaseIfCreation(protectedProcessEntry.Key, SuppressReason.Background, protectedProcessEntry.Value))
                    {
                        ReportUntrack(protectedProcessEntry.Key);
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
            Dictionary<int, long> creationsByPid = new Dictionary<int, long>();
            if (rule == null || members == null || changes == null)
            {
                return creationsByPid;
            }
            List<ProcessChange> currentChanges = new List<ProcessChange>();
            foreach (ProcessChange change in changes)
            {
                if (change != null && change.Pid > 0)
                {
                    currentChanges.Add(change);
                }
            }
            currentChanges.Sort((ProcessChange left, ProcessChange right) => left.Sequence.CompareTo(right.Sequence));
            Dictionary<int, ProcessChange> processChangesByPid = new Dictionary<int, ProcessChange>();
            foreach (ProcessChange currentChange in currentChanges)
            {
                if (currentChange.Kind == ProcessChangeKind.Stopped)
                {
                    long currentStopIdentity;
                    long member;
                    if (stopIdentity != null && stopIdentity.TryGetValue(currentChange.Pid, out currentStopIdentity) && members.TryGetValue(currentChange.Pid, out member) && (currentStopIdentity <= 0 || currentStopIdentity != member))
                    {
                        members.Remove(currentChange.Pid);
                        creationsByPid.Remove(currentChange.Pid);
                        processChangesByPid.Remove(currentChange.Pid);
                    }
                }
                else
                {
                    if (currentChange.Creation <= 0 || currentChange.Session != ownerSession)
                    {
                        continue;
                    }
                    long currentMember;
                    if (members.TryGetValue(currentChange.Pid, out currentMember) && currentMember != currentChange.Creation)
                    {
                        members.Remove(currentChange.Pid);
                        creationsByPid.Remove(currentChange.Pid);
                    }
                    processChangesByPid[currentChange.Pid] = currentChange;
                    string normalizedName = WhitelistRule.NormalizeName(currentChange.Name);
                    string imagePath = WhitelistRule.NormalizeImagePath(currentChange.Path);
                    if ((rule.MatchesNormalized(normalizedName, imagePath) && !WhitelistRule.IsUnsafeFamilyAnchor(imagePath)) || HasVerifiedFamilyParent(currentChange, members, processChangesByPid))
                    {
                        bool identityChanged = !members.TryGetValue(currentChange.Pid, out currentMember) || currentMember != currentChange.Creation;
                        members[currentChange.Pid] = currentChange.Creation;
                        if (identityChanged)
                        {
                            creationsByPid[currentChange.Pid] = currentChange.Creation;
                        }
                    }
                }
            }
            bool membersAdded;
            do
            {
                membersAdded = false;
                foreach (ProcessChange knownCreation in processChangesByPid.Values)
                {
                    if (!members.ContainsKey(knownCreation.Pid) && HasVerifiedFamilyParent(knownCreation, members, processChangesByPid))
                    {
                        members[knownCreation.Pid] = knownCreation.Creation;
                        creationsByPid[knownCreation.Pid] = knownCreation.Creation;
                        membersAdded = true;
                    }
                }
            }
            while (membersAdded);
            return creationsByPid;
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
                HashSet<int> retainedPids = new HashSet<int>();
                lock (sync)
                {
                    foreach (Dictionary<int, long> familyCreationsByPid in whiteFamilyMembers.Values)
                    {
                        retainedPids.UnionWith(familyCreationsByPid.Keys);
                    }
                }
                if (retainedPids.Count == 0)
                {
                    return;
                }
                HashSet<int> pids = new HashSet<int>();
                Dictionary<int, long> creationsByPid = new Dictionary<int, long>();
                foreach (ProcEntry procEntry in all.Entries)
                {
                    int pid = procEntry.Pid;
                    if (retainedPids.Contains(pid))
                    {
                        pids.Add(pid);
                        creationsByPid[pid] = procEntry.Creation;
                    }
                }
                lock (sync)
                {
                    foreach (Dictionary<int, long> currentFamilyCreationsByPid in whiteFamilyMembers.Values)
                    {
                        List<int> expiredPids = null;
                        foreach (KeyValuePair<int, long> currentFamilyCreationEntry in currentFamilyCreationsByPid)
                        {
                            long creation;
                            if (!pids.Contains(currentFamilyCreationEntry.Key) || (creationsByPid.TryGetValue(currentFamilyCreationEntry.Key, out creation) && creation != currentFamilyCreationEntry.Value))
                            {
                                if (expiredPids == null)
                                {
                                    expiredPids = new List<int>();
                                }
                                expiredPids.Add(currentFamilyCreationEntry.Key);
                            }
                        }
                        if (expiredPids == null)
                        {
                            continue;
                        }
                        foreach (int expiredPid in expiredPids)
                        {
                            currentFamilyCreationsByPid.Remove(expiredPid);
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
            ProcessChange liveStart;
            if (parentCreation <= 0 && liveStarts != null && liveStarts.TryGetValue(change.ParentPid, out liveStart))
            {
                parentCreation = liveStart.Creation;
            }
            return parentCreation == value;
        }
    }
}
