// @author bdth 2074055628@qq.com
// 文件用途 游戏列表 配置档案与白名单的持久化操作
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
            return AddGameExecutableCore(name, executablePath, null, true, out error);
        }

        private bool AddGameExecutableCore(string name, string executablePath,
            string preferredRoot, bool persist, out string error)
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
                if (normalized != null && UnderRoot(resolved, normalized)) root = normalized;
            }
            if (root == null) root = NormalizeGameRoot(GameScan.InferGameRoot(resolved));
            lock (sync)
            {
                foreach (GameProfile p in profiles)
                {
                    if (string.Equals(p.ExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.Equals(p.LearnedExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.IsNullOrEmpty(p.ExecutablePath) && p.Entries.Contains(entry))
                    {
                        p.ExecutablePath = resolved;
                        p.Root = root;
                        p.Name = display;
                        p.LearnedExecutablePath = null;
                        if (persist) PersistLibraryLocked();
                        if (persist) KickLibraryChanged();
                        return true;
                    }
                }
                GameProfile profile = GameProfileStore.NewProfile(display, root, resolved);
                profile.Entries.Clear();
                profile.Entries.Add(entry);
                profiles.Add(profile);
                if (persist) PersistLibraryLocked();
            }
            if (persist) KickLibraryChanged();
            return true;
        }

        private void PersistLibraryLocked()
        {
            RebuildLegacyGameIndex();
            profileStore.Save(profiles);
            SaveGames();
        }

        public event Action LibraryChanged;

        private void RaiseLibraryChanged()
        {
            Action handler = LibraryChanged;
            if (handler != null) { try { handler(); } catch { } }
        }

        private void KickLibraryChanged()
        {
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
            foreach (ScanHit hit in hits)
            {
                if (hit == null || string.IsNullOrEmpty(hit.Exe)) continue;
                string error;
                if (AddGameExecutableCore(hit.Name, hit.Exe, hit.Root, false, out error)) added++;
                else if (!string.IsNullOrEmpty(error)) lastError = error;
            }
            if (added > 0)
            {
                lock (sync) PersistLibraryLocked();
                KickLibraryChanged();
            }
            return added;
        }

        private void TryLearnRenderer(string profileId, string rendererPath, string rendererName)
        {
            if (string.IsNullOrEmpty(profileId) || string.IsNullOrEmpty(rendererPath)) return;
            if (GameSessionDetector.IsLauncherLikeName(rendererName)
                || AntiCheatCatalog.IsAntiCheatLikeName(rendererName)
                || GameSessionDetector.IsNonGameRole(rendererName, rendererPath)) return;
            string learnedGame = null, promotedGame = null;
            lock (sync)
            {
                foreach (GameProfile p in profiles)
                {
                    if (!string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(p.ExecutablePath, rendererPath, StringComparison.OrdinalIgnoreCase)) return;
                    if (string.Equals(p.LearnedExecutablePath, rendererPath, StringComparison.OrdinalIgnoreCase)) return;
                    string resolved = GameProfileStore.NormalizePath(rendererPath);
                    if (AnchorNeverElectable(p) && !RendererPathClaimedLocked(p, resolved))
                    {
                        PromoteRendererLocked(p, resolved);
                        promotedGame = p.Name;
                    }
                    else
                    {
                        p.LearnedExecutablePath = resolved;
                        if (!string.IsNullOrEmpty(rendererName)) p.Entries.Add(StripExe(rendererName));
                        profileStore.Save(profiles);
                        learnedGame = p.Name;
                    }
                    break;
                }
            }
            if (promotedGame != null)
            {
                Logger.Log(Lang.T("log.gamemodelibrary.29") + promotedGame + Lang.T("log.gamemodelibrary.30")
                    + rendererName + Lang.T("log.gamemodelibrary.31") + rendererPath + " ");
                RaiseLibraryChanged();
            }
            else if (learnedGame != null)
                Logger.Log(Lang.T("log.gamemodelibrary.2") + learnedGame + Lang.T("log.gamemodelibrary.3") + rendererName
                    + Lang.T("log.gamemodelibrary.4") + rendererPath + " ");
        }

        private static bool AnchorNeverElectable(GameProfile p)
        {
            if (string.IsNullOrEmpty(p.ExecutablePath)) return true;
            string name = Path.GetFileNameWithoutExtension(p.ExecutablePath);
            return GameSessionDetector.ElectionVetoed(name, p.ExecutablePath);
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
            TryLearnRenderer(profileId, rendererPath, rendererName);
        }
#endif

        private void PromoteRendererLocked(GameProfile p, string resolved)
        {
            string oldExe = p.ExecutablePath;
            if (!string.IsNullOrEmpty(oldExe))
            {
                p.Entries.Remove(StripExe(Path.GetFileName(oldExe)));
                if (string.Equals(p.Name, Path.GetFileNameWithoutExtension(oldExe), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Name, DisplayName(oldExe, null), StringComparison.OrdinalIgnoreCase))
                    p.Name = DisplayName(resolved, null);
            }
            p.ExecutablePath = resolved;
            p.Root = NormalizeGameRoot(GameScan.InferGameRoot(resolved));
            p.LearnedExecutablePath = null;
            p.Entries.Add(StripExe(Path.GetFileName(resolved)));
            PersistLibraryLocked();
        }

        public bool SetProfileForceTrigger(string profileId, bool on)
        {
            bool changed = false, dropSession = false;
            string name = null;
            lock (sync)
            {
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
                        PersistLibraryLocked();
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

        public bool SetProfileOverride(string profileId, string key, string value)
        {
            bool ok = false;
            lock (sync)
            {
                GameProfile p = FindProfileLocked(profileId);
                if (p != null)
                {
                    ok = PolicyResolver.SetOverride(p, key, value);
                    if (ok) profileStore.Save(profiles);
                }
            }
            return ok;
        }

        public bool ClearProfileOverride(string profileId, string key)
        {
            bool ok = false;
            lock (sync)
            {
                GameProfile p = FindProfileLocked(profileId);
                if (p != null && p.Overrides.Remove(key))
                {
                    profileStore.Save(profiles);
                    ok = true;
                }
            }
            return ok;
        }

        public int ClearProfileOverrides(string profileId)
        {
            int n = 0;
            string name = null;
            lock (sync)
            {
                GameProfile p = FindProfileLocked(profileId);
                if (p != null)
                {
                    n = PolicyResolver.ClearAllOverrides(p);
                    if (n > 0) { profileStore.Save(profiles); name = p.Name; }
                }
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
                GameProfile p = FindProfileLocked(profileId);
                if (p == null) return false;
                if (string.Equals(p.Name, trimmed, StringComparison.Ordinal)) return true;
                oldName = p.Name;
                p.Name = trimmed;
                profileStore.Save(profiles);
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
                profiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
                RebuildLegacyGameIndex();
                profileStore.Save(profiles);
                SaveGames();
                dropSession = activeDetection != null && activeDetection.Profile != null
                    && string.Equals(activeDetection.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase);
            }
            if (dropSession) panicReq = true;
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
            lock (whiteEvalSync)
            {
                lock (sync)
                {
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
            }
            int matched;
            int freed = ReleaseCurrentWhitelistMatches(out matched);
            Logger.Log(Lang.T("log.gamemodelibrary.17") + rule.Kind + " " + rule.Value + Lang.T("log.gamemodelibrary.18") + matched
                + Lang.T("log.gamemodelibrary.19") + freed + Lang.T("log.gamemodelibrary.20"));
            RequestPolicyApply();
            return true;
        }

        public bool RemoveWhitelistRule(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (whiteEvalSync)
            {
                lock (sync)
                {
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
            lock (whiteEvalSync)
            {
                lock (sync)
                {
                    if (!SaveWhite(next))
                    {
                        whitelistLastError = Lang.T("white.save.failed");
                        return false;
                    }
                    whiteRules.Clear();
                    whiteRuleKeys.Clear();
                    whiteFamilyMembers.Clear();
                    whiteRevision++;
                    RefreshWhitelistFamilyFlagLocked();
                    foreach (WhitelistRule rule in next)
                        AddWhiteRuleNoSave(rule);
                    whitelistLastError = "";
                }
            }
            int matched;
            int freed = ReleaseCurrentWhitelistMatches(out matched);
            Logger.Log(Lang.T("log.gamemodelibrary.21") + SystemProcessCatalog.PresetWhitelist.Length + Lang.T("log.gamemodelibrary.22") + matched
                + Lang.T("log.gamemodelibrary.19") + freed + Lang.T("log.gamemodelibrary.20"));
            RequestPolicyApply();
            return true;
        }

        private bool SaveWhite(IList<WhitelistRule> rules)
        {
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

        private void SaveGames()
        {
            try
            {
                var lines = new List<string>();
                foreach (string game in games)
                {
                    string root;
                    gameRoots.TryGetValue(game, out root);
                    lines.Add(EncodeGameLine(game, root));
                }
                AtomicFile.WriteLines(gamesPath, lines.ToArray(), Lang.T("t.gamemodelibrary.27"));
            }
            catch (Exception error) { Logger.LogFailure(Lang.T("log.gamemodelibrary.28"), error); }
        }

        private void RebuildLegacyGameIndex()
        {
            games.Clear();
            gameRoots.Clear();
            foreach (GameProfile profile in profiles)
                foreach (string entry in profile.Entries)
                {
                    bool exists = false;
                    foreach (string game in games)
                        if (string.Equals(game, entry, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                    if (!exists) games.Add(entry);
                    if (!string.IsNullOrEmpty(profile.Root)) gameRoots[entry] = profile.Root;
                }
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

        internal static string EncodeGameLine(string name, string root)
        {
            string normalized = StripExe((name ?? "").Trim());
            string normalizedRoot = NormalizeGameRoot(root);
            return normalizedRoot == null ? normalized : normalized + "|" + normalizedRoot;
        }

        internal static bool TryParseGameLine(string line, out string name, out string root)
        {
            name = null;
            root = null;
            if (string.IsNullOrWhiteSpace(line)) return false;
            string trimmed = line.Trim();
            int split = trimmed.IndexOf('|');
            string rawName = split >= 0 ? trimmed.Substring(0, split) : trimmed;
            name = StripExe(rawName.Trim());
            if (name.Length == 0) { name = null; return false; }
            if (split >= 0) root = NormalizeGameRoot(trimmed.Substring(split + 1));
            return true;
        }

        private static string NormalizeGameRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                string full = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
                return SafeFamilyDir(full) ? full : null;
            }
            catch { return null; }
        }
    }
}
