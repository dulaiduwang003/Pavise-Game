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
            List<WhitelistRuleView> list = SnapshotWhitelistRuleViews();
            list.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
            {
                int kindOrder = a.Rule.Kind.CompareTo(b.Rule.Kind);
                return (kindOrder == 0) ? string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase) : kindOrder;
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
                int kindOrder = a.Rule.Kind.CompareTo(b.Rule.Kind);
                return (kindOrder == 0) ? string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase) : kindOrder;
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
                releasedCount = ReleaseCurrentWhitelistMatches(out matched);
            }
            Logger.Log(Lang.T("log.gamemodelibrary.21", 0) + SystemProcessCatalog.PresetWhitelist.Length + Lang.T("log.gamemodelibrary.22", 0) + matched + Lang.T("log.gamemodelibrary.19", 0) + releasedCount + Lang.T("log.gamemodelibrary.20", 0));
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
                        string serializedRule = rule.Serialize();
                        processedCount++;
                        for (int i = 0; i < serializedRule.Length; i++)
                        {
                            numericValue ^= (byte)serializedRule[i];
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
                bool revisionsCurrent = whiteRevisionSnapshot == whiteRevision && whiteFamilyMembersVersionSnapshot == whiteFamilyMembersVersion;
                if (revisionsCurrent)
                {
                    whiteFamilyMembers.Clear();
                    foreach (KeyValuePair<string, Dictionary<int, long>> item5 in dictionary3)
                    {
                        whiteFamilyMembers[item5.Key] = item5.Value;
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
                    long value2;
                    long value3;
                    if (!hashSet.Contains(parent.Key) && hashSet.Contains(parent.Value) && currentCreation != null && currentCreation.TryGetValue(parent.Key, out value2) && currentCreation.TryGetValue(parent.Value, out value3) && value2 > 0 && value3 > 0 && value2 > value3)
                    {
                        hashSet.Add(parent.Key);
                        membersAdded = true;
                    }
                }
            }
            while (membersAdded);
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
                        bool identityChanged = !members.TryGetValue(item.Pid, out value3) || value3 != item.Creation;
                        members[item.Pid] = item.Creation;
                        if (identityChanged)
                        {
                            dictionary[item.Pid] = item.Creation;
                        }
                    }
                }
            }
            bool membersAdded;
            do
            {
                membersAdded = false;
                foreach (ProcessChange value4 in dictionary2.Values)
                {
                    if (!members.ContainsKey(value4.Pid) && HasVerifiedFamilyParent(value4, members, dictionary2))
                    {
                        members[value4.Pid] = value4.Creation;
                        dictionary[value4.Pid] = value4.Creation;
                        membersAdded = true;
                    }
                }
            }
            while (membersAdded);
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
