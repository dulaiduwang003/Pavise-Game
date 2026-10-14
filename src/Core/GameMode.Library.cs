// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private const string WhitelistFooterPrefix = "PAVISE_WHITELIST_END|";
        private string whitelistLastError = "";

        public string WhitelistLastError
        {
            get { lock (sync) return whitelistLastError; }
        }

        public bool AddGameExecutable(string name, string executablePath)
        {
            string error;
            return AddGameExecutableCore(name, executablePath, null, out error);
        }

        private bool AddGameExecutableCore(string name, string executablePath,
            string preferredRoot, out string error)
        {
            error = null;
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked()) return false;
                List<GameProfile> next = GetProfiles();
                var nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
                if (!StageGameExecutable(name, executablePath, preferredRoot, next, nextIgnore, out error)
                    || !CommitLibraryLocked(next, nextIgnore)) return false;
            }
            KickLibraryChanged();
            return true;
        }

        private bool StageGameExecutable(string name, string executablePath, string preferredRoot,
            List<GameProfile> next, HashSet<string> nextIgnore, out string error)
        {
            string resolved, suggestedName;
            if (!GameExecutableResolver.TryResolve(executablePath, out resolved, out error, out suggestedName))
                return false;
            string entry = StripExe(Path.GetFileName(resolved));
            string display = DisplayName(resolved,
                string.IsNullOrWhiteSpace(name) ? suggestedName : name);
            string root = null;
            if (!string.IsNullOrEmpty(preferredRoot))
            {
                string normalized = NormalizeGameRoot(preferredRoot);
                if (normalized != null && FamilyBoundary.UnderRoot(resolved, normalized)) root = normalized;
            }
            if (root == null) root = NormalizeGameRoot(GameScan.InferGameRoot(resolved));
            root = ResolveLibraryInstallRoot(resolved, root);
            foreach (GameProfile p in next)
            {
                if (string.Equals(p.ExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(p.LearnedExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return false;
                if (string.IsNullOrEmpty(p.ExecutablePath) && p.Entries.Contains(entry))
                {
                    p.ExecutablePath = resolved;
                    p.Root = root;
                    p.Name = display;
                    p.LearnedExecutablePath = null;
                    nextIgnore.Remove(resolved);
                    return true;
                }
            }
            GameProfile profile = GameProfileStore.NewProfile(display, root, resolved);
            profile.Entries.Clear();
            profile.Entries.Add(entry);
            next.Add(profile);
            nextIgnore.Remove(resolved);
            return true;
        }

        private bool PersistLibraryLocked()
        {
            return SaveProfilesLocked();
        }

        public event Action LibraryChanged;

        private void RaiseLibraryChanged()
        {
            Action handler = LibraryChanged;
            if (handler != null) { try { handler(); } catch { } }
        }

        private void KickLibraryChanged()
        {
            InvalidateAutomaticFullscreenOptimizations();
            ClearFamilyDiscovery();
            InvalidateRendererHandoff();
            InvalidateFamilyPolicy();
            RequestFullGameDetection();
            RequestPolicyApply();
            RaiseLibraryChanged();
        }

        public bool AddGameFile(string selectedPath, out string error)
        {
            string executable;
            if (!GameExecutableResolver.TryResolve(selectedPath, out executable, out error)) return false;
            if (!AddGameExecutable(null, executable))
            {
                error = Lang.T("t.gamemodelibrary.1");
                return false;
            }
            error = null;
            return true;
        }

        public int AddScannedGames(IList<ScanHit> hits, out string lastError)
        {
            lastError = null;
            int added = 0;
            if (hits == null) return 0;
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked()) return 0;
                List<GameProfile> next = GetProfiles();
                var nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
                foreach (ScanHit hit in hits)
                {
                    if (hit == null || string.IsNullOrEmpty(hit.Exe)) continue;
                    string error;
                    if (StageGameExecutable(hit.Name, hit.Exe, hit.Root, next, nextIgnore, out error)) added++;
                    else if (!string.IsNullOrEmpty(error)) lastError = error;
                }
                if (added > 0 && !CommitLibraryLocked(next, nextIgnore)) return 0;
            }
            if (added > 0) KickLibraryChanged();
            return added;
        }

        internal bool TryLearnConfirmedRenderer(GameDetection hit)
        {
            return TryLearnConfirmedRenderer(hit, null);
        }

        private bool TryLearnConfirmedRenderer(GameDetection hit, Func<bool> stillCurrent)
        {
            if (hit == null || hit.Profile == null || !hit.RendererCandidateSelected
                || !hit.RendererLearnable || hit.RendererSafetyOnly || hit.RequiresGpuConfirm
                || hit.Profile.ForceTrigger || hit.RendererPid <= 0 || hit.RendererCreation <= 0)
                return false;
            return TryLearnRendererCore(hit.Profile.Id, hit.RendererPath,
                hit.RendererName, hit.Profile, stillCurrent);
        }

        private bool TryLearnRendererCore(string profileId, string rendererPath,
            string rendererName, GameProfile observedProfile, Func<bool> stillCurrent)
        {
            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(rendererPath)
                || string.IsNullOrWhiteSpace(rendererName))
                return false;
            string suppliedPath = rendererPath.Trim().Trim('"');
            string resolved = GameProfileStore.NormalizePath(suppliedPath);
            if (resolved == null || !Path.IsPathRooted(suppliedPath)) return false;
            string volume = Path.GetPathRoot(suppliedPath);
            if (string.IsNullOrEmpty(volume) || volume.Length < 3
                || (!volume.EndsWith("\\", StringComparison.Ordinal)
                    && !volume.EndsWith("/", StringComparison.Ordinal))) return false;
            string name = Path.GetFileNameWithoutExtension(resolved);
            if (string.IsNullOrEmpty(name)
                || !string.Equals(name, StripExe(rendererName), StringComparison.OrdinalIgnoreCase)
                || !GameSessionDetector.IsLibraryCandidate(name, resolved, windowsPrefix)) return false;

            string root = ResolveLibraryInstallRoot(resolved, GameScan.InferGameRoot(resolved));
            if (root != null && !FamilyBoundary.UnderRoot(resolved, root)) root = null;
            string learnedGame;
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed || (stillCurrent != null && !stillCurrent())) return false;
                GameProfile current = FindProfileLocked(profileId);
                if (current == null || current.ForceTrigger
                    || RendererPathClaimedLocked(current, resolved)) return false;

                string declaredRoot = GameInstallScope.RestrictFallback(
                    current.ExecutablePath, NormalizeGameRoot(current.Root));
                if (declaredRoot != null && FamilyBoundary.UnderRoot(resolved, declaredRoot)) root = declaredRoot;

                bool alreadyTarget = string.Equals(current.ExecutablePath, resolved,
                    StringComparison.OrdinalIgnoreCase);
                // 等待期间用户可能改过配置，旧观察不能覆盖新入口。
                if (!alreadyTarget && observedProfile != null
                    && (!string.Equals(current.ExecutablePath, observedProfile.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(current.LearnedExecutablePath, observedProfile.LearnedExecutablePath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(current.Root, observedProfile.Root, StringComparison.OrdinalIgnoreCase)))
                    return false;
                if (alreadyTarget && current.LearnedExecutablePath == null
                    && string.Equals(current.Root, root, StringComparison.OrdinalIgnoreCase)
                    && current.Entries.Count == 1 && current.Entries.Contains(name)) return true;

                // 候选先保存成功再发布到内存。
                GameProfile replacement = current.Clone();
                replacement.ExecutablePath = resolved;
                replacement.LearnedExecutablePath = null;
                replacement.Root = root;
                replacement.Entries.Clear();
                replacement.Entries.Add(name);
                int index = profiles.IndexOf(current);
                var next = new List<GameProfile>(profiles);
                next[index] = replacement;
                if (stillCurrent != null && !stillCurrent()) return false;
                if (!SaveProfileSnapshotLocked(next, stillCurrent))
                {
                    return false;
                }
                profiles[index] = replacement;
                if (!alreadyTarget) ForgetRendererObservation(profileId);
                learnedGame = replacement.Name;
            }
            Logger.Log(Lang.T("log.gamemodelibrary.2") + learnedGame + Lang.T("log.gamemodelibrary.3") + name
                + Lang.T("log.gamemodelibrary.4") + resolved + " ");
            InvalidateAutomaticFullscreenOptimizations();
            RequestFullGameDetection();
            RaiseLibraryChanged();
            return true;
        }

        private bool RendererPathClaimedLocked(GameProfile self, string resolved)
        {
            foreach (GameProfile other in profiles)
            {
                if (ReferenceEquals(other, self)) continue;
                if (string.Equals(other.ExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(other.LearnedExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

#if PAVISE_SELFTEST
        internal void ProbeLearnRenderer(string profileId, string rendererPath, string rendererName)
        {
            TryLearnRendererCore(profileId, rendererPath, rendererName, null, null);
        }
#endif

        public bool SetProfileForceTrigger(string profileId, bool on)
        {
            bool changed = false, dropSession = false;
            string name = null;
            lock (sync)
            {
                if (stopping) return false;
                foreach (GameProfile p in profiles)
                {
                    if (!string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (on && string.IsNullOrEmpty(p.ExecutablePath)
                        && string.IsNullOrEmpty(p.LearnedExecutablePath)) return false;
                    if (p.ForceTrigger != on)
                    {
                        p.ForceTrigger = on;
                        name = p.Name;
                        changed = true;
                        if (!PersistLibraryLocked()) { p.ForceTrigger = !on; return false; }
                    }
                    break;
                }
                if (changed && !on)
                    dropSession = activeDetection != null && activeDetection.Profile != null
                        && string.Equals(activeDetection.Profile.Id, profileId,
                            StringComparison.OrdinalIgnoreCase);
            }
            if (!changed) return true;
            if (dropSession) panicReq = true;
            Logger.Log((on ? Lang.T("log.gamemodelibrary.5") : Lang.T("log.gamemodelibrary.6")) + name
                + (on ? Lang.T("log.gamemodelibrary.7")
                     : Lang.T("log.gamemodelibrary.8")));
            KickLibraryChanged();
            return true;
        }

        private GameProfile FindProfileLocked(string profileId)
        {
            foreach (GameProfile p in profiles)
                if (string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        private bool LiveBoolPreferenceLocked(string policyKey, bool globalOn)
        {
            if (AutomaticPolicy.Enabled) return (sessionPolicy ?? PolicyResolver.Global()).ValueOf(policyKey) == "1";
            PolicySnapshot snapshot = sessionPolicy;
            if (snapshot == null) return globalOn;
            if (PolicyCatalog.IsHandheldBlocked(policyKey) && snapshot.Preset == PerformancePreset.Handheld) return false;
            if (string.IsNullOrEmpty(snapshot.ProfileId)) return globalOn;
            GameProfile profile = FindProfileLocked(snapshot.ProfileId);
            if (profile == null) return false;
            string value;
            return profile.Overrides.TryGetValue(policyKey, out value) ? value == "1" : globalOn;
        }

        private void InvalidateOverrideWorkLocked(string key)
        {
            if (key == PolicyCatalog.KeyCacheWarm || key == PolicyCatalog.KeyStandbyCleaner) InvalidateCacheWarm();
            if (key == PolicyCatalog.KeyStandbyCleaner) InvalidateStandbyCleanerWork();
            if (key == PolicyCatalog.KeyEnglishInput) InvalidateEnglishInputWork();
        }

        private void NotifyOverridePolicyChanged(string key, bool effectiveOn)
        {
            if (key == PolicyCatalog.KeyGameFullscreenOptimizations)
            {
                InvalidateAutomaticFullscreenOptimizations();
                SyncAutomaticFullscreenOptimizations(true);
                RaiseLibraryChanged();
                return;
            }
            if (key == PolicyCatalog.KeyGameRebar)
            {
                lock (sync)
                {
                    if (sessionPolicy != null)
                        sessionPolicy = PolicyResolver.For(FindProfileLocked(sessionPolicy.ProfileId));
                }
            }
            if (key == PolicyCatalog.KeyPauseServices) PauseServicesPolicyChanged(effectiveOn);
            else if (key == PolicyCatalog.KeyStandbyCleaner) StandbyCleanerPolicyChanged(effectiveOn);
            else if (key == PolicyCatalog.KeyPowerPlan || key == PolicyCatalog.KeyEnglishInput
                || key == PolicyCatalog.KeyCacheWarm || key == PolicyCatalog.KeyGameRebar
                || key == PolicyCatalog.KeyAutoCores || key == ManualCorePlan.Key || key == PolicyCatalog.KeyPreset)
                RequestPolicyApply();
        }

        private bool GlobalPolicyOn(string key)
        {
            if (key == PolicyCatalog.KeyPauseServices) return pauseServicesOn;
            if (key == PolicyCatalog.KeyStandbyCleaner) return standbyCleanerOn;
            if (key == PolicyCatalog.KeyCacheWarm) return cacheWarmOn;
            return false;
        }

        public bool SetProfileOverride(string profileId, string key, string value)
        {
            if (key == PolicyCatalog.KeySuppressFamily)
            {
                string canonical = PolicyCatalog.Canonical(key, value);
                return canonical != null && SetProfileFamilySuppression(profileId, canonical == "1");
            }
            bool ok = false;
            lock (sync)
            {
                if (stopping) return false;
                if (key == PolicyCatalog.KeyPreset && ModeSwitchLockedLocked()) return false;
                if (key == PolicyCatalog.KeyStandbyCleaner
                    && PolicyCatalog.Canonical(key, value) == "1" && !standbyCleanerOptionsValid) return false;
                GameProfile p = FindProfileLocked(profileId);
                if (p != null)
                {
                    string before = null;
                    bool hadValue = key != null && p.Overrides.TryGetValue(key, out before);
                    InvalidateOverrideWorkLocked(key);
                    ok = PolicyResolver.SetOverride(p, key, value);
                    if (ok && !SaveProfilesLocked())
                    {
                        if (hadValue) p.Overrides[key] = before;
                        else p.Overrides.Remove(key);
                        ok = false;
                    }
                }
            }
            if (ok) NotifyOverridePolicyChanged(key, PolicyCatalog.Canonical(key, value) == "1");
            return ok;
        }

        public bool ClearProfileOverride(string profileId, string key)
        {
            if (key == PolicyCatalog.KeySuppressFamily) return SetProfileFamilySuppression(profileId, false);
            bool ok = false;
            lock (sync)
            {
                if (stopping) return false;
                GameProfile p = FindProfileLocked(profileId);
                if (p != null && p.Overrides.ContainsKey(key))
                {
                    string before = p.Overrides[key];
                    InvalidateOverrideWorkLocked(key);
                    p.Overrides.Remove(key);
                    ok = SaveProfilesLocked();
                    if (!ok) p.Overrides[key] = before;
                }
            }
            if (ok) NotifyOverridePolicyChanged(key, GlobalPolicyOn(key));
            return ok;
        }

        public int ClearProfileOverrides(string profileId)
        {
            int n = 0;
            string name = null;
            bool servicesChanged = false;
            bool standbyCleanerChanged = false;
            lock (familyPolicyGate)
            {
                lock (sync)
                {
                    if (stopping) return 0;
                    GameProfile p = FindProfileLocked(profileId);
                    if (p != null && p.Overrides.Count > 0 && !ProfileStoreSaveFailed)
                    {
                        standbyCleanerChanged = p.Overrides.ContainsKey(PolicyCatalog.KeyStandbyCleaner);
                        foreach (string overrideKey in p.Overrides.Keys)
                            InvalidateOverrideWorkLocked(overrideKey);
                        GameProfile replacement = p.Clone();
                        n = PolicyResolver.ClearAllOverrides(replacement);
                        var next = new List<GameProfile>(profiles);
                        int index = profiles.IndexOf(p);
                        next[index] = replacement;
                        if (!SaveProfileSnapshotLocked(next))
                        {
                            return 0;
                        }
                        profiles[index] = replacement;
                        servicesChanged = p.Overrides.ContainsKey(PolicyCatalog.KeyPauseServices);
                        name = p.Name;
                        InvalidateFamilyPolicy();
                    }
                }
            }
            if (n > 0)
            {
                NotifyOverridePolicyChanged(PolicyCatalog.KeyGameFullscreenOptimizations, false);
                NotifyOverridePolicyChanged(PolicyCatalog.KeyGameRebar, false);
                if (servicesChanged) PauseServicesPolicyChanged(pauseServicesOn);
                if (standbyCleanerChanged) StandbyCleanerPolicyChanged(standbyCleanerOn);
                if (!servicesChanged && !standbyCleanerChanged) RequestPolicyApply();
                RaiseLibraryChanged();
            }
            if (name != null) Logger.Log(Lang.T("log.gamemodelibrary.9") + name + Lang.T("log.gamemodelibrary.10") + n + Lang.T("log.gamemodelibrary.11"));
            return n;
        }

        public List<PolicyDiff> DiffProfile(string profileId)
        {
            GameProfile copy = null;
            lock (sync)
            {
                GameProfile p = FindProfileLocked(profileId);
                if (p != null) copy = p.Clone();
            }
            return PolicyResolver.Diff(copy);
        }

        public bool RenameProfile(string profileId, string name)
        {
            string trimmed = (name ?? "").Trim();
            if (trimmed.Length == 0) return false;
            string oldName = null;
            lock (sync)
            {
                if (stopping) return false;
                GameProfile p = FindProfileLocked(profileId);
                if (p == null) return false;
                if (string.Equals(p.Name, trimmed, StringComparison.Ordinal)) return true;
                oldName = p.Name;
                p.Name = trimmed;
                if (!SaveProfilesLocked()) { p.Name = oldName; return false; }
            }
            Logger.Log(Lang.T("log.gamemodelibrary.14") + oldName + Lang.T("log.gamemodelibrary.15") + trimmed + Lang.T("log.gamemodelibrary.16"));
            RaiseLibraryChanged();
            return true;
        }

        public void RemoveProfile(string profileId)
        {
            bool dropSession;
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked()) return;
                var nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
                foreach (GameProfile p in profiles)
                {
                    if (!string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(p.ExecutablePath)) nextIgnore.Add(p.ExecutablePath);
                    if (!string.IsNullOrEmpty(p.LearnedExecutablePath)) nextIgnore.Add(p.LearnedExecutablePath);
                }
                List<GameProfile> next = GetProfiles();
                if (next.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) == 0) return;
                if (!CommitLibraryLocked(next, nextIgnore)) return;
                ClearModeSwitchLockForProfileLocked(profileId);
                InvalidateAutomaticFullscreenOptimizations();
                InvalidateCacheWarm();
                InvalidateStandbyCleanerWork();
                InvalidateEnglishInputWork();
                ClearFamilyDiscovery();
                ForgetRendererObservation(profileId);
                dropSession = (activeDetection != null && activeDetection.Profile != null
                    && string.Equals(activeDetection.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
                    || (sessionPolicy != null && string.Equals(sessionPolicy.ProfileId, profileId,
                        StringComparison.OrdinalIgnoreCase));
                if (dropSession) panicReq = true;
                InvalidateRendererHandoff();
            }
            SyncAutomaticFullscreenOptimizations(true);
            RequestFullGameDetection();
            RequestPolicyApply();
            RaiseLibraryChanged();
        }

#if PAVISE_SELFTEST
        public List<string> GetWhitelist()
        {
            var result = new List<string>();
            lock (sync)
                foreach (WhitelistRule rule in whiteRules) result.Add(rule.Value);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
#endif

        public List<WhitelistRuleView> GetWhitelistRules()
        {
            List<WhitelistRuleView> result = SnapshotWhitelistRuleViews();
            result.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
            {
                int kind = a.Rule.Kind.CompareTo(b.Rule.Kind);
                return kind != 0 ? kind : string.Compare(a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public List<WhitelistRuleView> GetWhitelistRulesFast()
        {
            var result = new List<WhitelistRuleView>();
            AppendPlatformRuleViews(result, null);
            lock (sync)
                foreach (WhitelistRule rule in whiteRules)
                    result.Add(new WhitelistRuleView(
                        rule, -1, rule.Kind == WhitelistRuleKind.LegacyName
                            && IsPresetWhitelistName(rule.Value)));
            result.Sort(delegate(WhitelistRuleView a, WhitelistRuleView b)
            {
                int kind = a.Rule.Kind.CompareTo(b.Rule.Kind);
                return kind != 0 ? kind : string.Compare(
                    a.Rule.Value, b.Rule.Value, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public bool AddWhitelistPath(string executablePath)
        {
            return AddWhitelistRule(WhitelistRuleKind.ExactPath, executablePath);
        }

        public bool AddWhitelistAuto(string executablePath)
        {
            return AddWhitelistRule(ResolveAutoKind(executablePath), executablePath);
        }

        internal static WhitelistRuleKind ResolveAutoKind(string executablePath)
        {
            return WhitelistRule.IsUnsafeFamilyAnchor(executablePath)
                ? WhitelistRuleKind.ExactPath
                : WhitelistRuleKind.ApplicationFamily;
        }

        public bool NarrowWhitelistRule(string key)
        {
            WhitelistRule found = null;
            lock (sync)
                foreach (WhitelistRule rule in whiteRules)
                    if (rule.Key == key) { found = rule; break; }
            if (found == null || found.Kind != WhitelistRuleKind.ApplicationFamily) return false;
            if (!RemoveWhitelistRule(key)) return false;
            if (AddWhitelistRule(WhitelistRuleKind.ExactPath, found.Value)) return true;
            AddWhitelistRule(WhitelistRuleKind.ApplicationFamily, found.Value);
            return false;
        }

        public bool WidenWhitelistRule(string key)
        {
            WhitelistRule found = null;
            lock (sync)
                foreach (WhitelistRule rule in whiteRules)
                    if (rule.Key == key) { found = rule; break; }
            if (found == null || found.Kind != WhitelistRuleKind.ExactPath) return false;
            if (WhitelistRule.IsUnsafeFamilyAnchor(found.Value)) return false;
            if (!RemoveWhitelistRule(key)) return false;
            if (AddWhitelistRule(WhitelistRuleKind.ApplicationFamily, found.Value)) return true;
            AddWhitelistRule(WhitelistRuleKind.ExactPath, found.Value);
            return false;
        }

        private bool AddWhitelistRule(WhitelistRuleKind kind, string value)
        {
            WhitelistRule rule;
            if (!WhitelistRule.TryCreate(kind, value, out rule))
            {
                lock (sync) whitelistLastError = Lang.T("white.duplicate");
                return false;
            }
            int matched, freed;
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping) return false;
                    if (whiteRuleKeys.Contains(rule.Key))
                    {
                        whitelistLastError = Lang.T("white.duplicate");
                        return false;
                    }
                    var next = new List<WhitelistRule>(whiteRules) { rule };
                    if (!SaveWhite(next))
                    {
                        whitelistLastError = Lang.T("white.save.failed");
                        return false;
                    }
                    AddWhiteRuleNoSave(rule);
                    whitelistLastError = "";
                }
                // 停止流程需同时等待白名单恢复工作排空。
                freed = ReleaseCurrentWhitelistMatches(out matched);
            }
            Logger.Log(Lang.T("log.gamemodelibrary.17") + rule.Kind + " " + rule.Value + Lang.T("log.gamemodelibrary.18") + matched
                + Lang.T("log.gamemodelibrary.19") + freed + Lang.T("log.gamemodelibrary.20"));
            RequestPolicyApply();
            return true;
        }

        public bool RemoveWhitelistRule(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.StartsWith(PlatformRuntimePolicy.RulePrefix, StringComparison.Ordinal))
                return RemovePlatformProtection(key.Substring(PlatformRuntimePolicy.RulePrefix.Length));
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping) return false;
                    WhitelistRule target = whiteRules.Find(delegate(WhitelistRule rule)
                    {
                        return string.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase);
                    });
                    if (target == null) return false;
                    if (target.Kind == WhitelistRuleKind.LegacyName
                        && IsPresetWhitelistName(target.Value))
                    {
                        whitelistLastError = Lang.T("white.required");
                        return false;
                    }
                    var next = new List<WhitelistRule>(whiteRules);
                    next.Remove(target);
                    if (!SaveWhite(next))
                    {
                        whitelistLastError = Lang.T("white.save.failed");
                        return false;
                    }
                    whiteRules.Remove(target);
                    whiteRuleKeys.Remove(key);
                    whiteFamilyMembers.Remove(key);
                    whiteFamilyMembersVersion++;
                    whiteRevision++;
                    RefreshWhitelistFamilyFlagLocked();
                    whitelistLastError = "";
                }
            }
            RequestPolicyApply();
            return true;
        }

        public bool ResetWhitelist()
        {
            var next = new List<WhitelistRule>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in SystemProcessCatalog.PresetWhitelist)
            {
                WhitelistRule rule;
                if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, entry, out rule)
                    && keys.Add(rule.Key)) next.Add(rule);
            }
            int matched, freed;
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (stopping) return false;
                    if (!SaveWhite(next))
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
                    foreach (WhitelistRule rule in next)
                        AddWhiteRuleNoSave(rule);
                    whitelistLastError = "";
                }
                freed = ReleaseCurrentWhitelistMatches(out matched);
            }
            Logger.Log(Lang.T("log.gamemodelibrary.21") + SystemProcessCatalog.PresetWhitelist.Length + Lang.T("log.gamemodelibrary.22") + matched
                + Lang.T("log.gamemodelibrary.19") + freed + Lang.T("log.gamemodelibrary.20"));
            RequestPolicyApply();
            return true;
        }

        private bool SaveWhite(IList<WhitelistRule> rules)
        {
            lock (sync)
            {
                if (stopping) return false;
                try
                {
                    var lines = new List<string>();
                    lines.Add(Lang.T("t.gamemodelibrary.23"));
                    lines.Add(Lang.T("t.gamemodelibrary.24"));
                    lines.Add(Lang.T("t.gamemodelibrary.25"));
                    lines.Add(WhitelistRule.Header);
                    if (rules != null)
                        foreach (WhitelistRule rule in rules) lines.Add(rule.Serialize());
                    lines.Add(BuildWhitelistFooter(rules));
                    return AtomicFile.WriteLines(whitePath, lines.ToArray(), Lang.T("nav.white"));
                }
                catch (Exception error)
                {
                    Logger.LogFailure(Lang.T("log.gamemodelibrary.26"), error);
                    return false;
                }
            }
        }

        internal static string BuildWhitelistFooter(IList<WhitelistRule> rules)
        {
            ulong hash = 1469598103934665603UL;
            int count = 0;
            if (rules != null)
                foreach (WhitelistRule rule in rules)
                {
                    if (rule == null) continue;
                    string line = rule.Serialize();
                    count++;
                    unchecked
                    {
                        for (int i = 0; i < line.Length; i++)
                        {
                            hash ^= (byte)line[i];
                            hash *= 1099511628211UL;
                        }
                        hash ^= (byte)'\n';
                        hash *= 1099511628211UL;
                    }
                }
            return WhitelistFooterPrefix + count + "|" + hash.ToString("X16");
        }

        private static string DisplayName(string executablePath, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(fallback)) return fallback.Trim();
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(executablePath);
                string value = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            catch { }
            return Path.GetFileNameWithoutExtension(executablePath);
        }

        private static string NormalizeGameRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                string full = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
                return FamilyBoundary.SafeFamilyDir(full) ? full : null;
            }
            catch { return null; }
        }
    }
}
