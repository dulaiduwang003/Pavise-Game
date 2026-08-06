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
                if (autoAddIgnore.Remove(resolved)) SaveAutoIgnoreLocked();
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
                error = "该游戏已经在列表中";
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
                || GameSessionDetector.IsAntiCheatLikeName(rendererName)
                || GameSessionDetector.IsNonGameRole(rendererName, rendererPath)) return;
            string learnedGame = null;
            lock (sync)
            {
                foreach (GameProfile p in profiles)
                {
                    if (!string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(p.ExecutablePath, rendererPath, StringComparison.OrdinalIgnoreCase)) return;
                    if (string.Equals(p.LearnedExecutablePath, rendererPath, StringComparison.OrdinalIgnoreCase)) return;
                    p.LearnedExecutablePath = GameProfileStore.NormalizePath(rendererPath);
                    if (!string.IsNullOrEmpty(rendererName)) p.Entries.Add(StripExe(rendererName));
                    profileStore.Save(profiles);
                    learnedGame = p.Name;
                    break;
                }
            }
            if (learnedGame != null)
                Logger.Log("已确认《" + learnedGame + "》的实际渲染进程是 " + rendererName
                    + "，档案已更新，之后可直接按它识别（" + rendererPath + "）");
        }

        public void RemoveProfile(string profileId)
        {
            bool dropSession;
            lock (sync)
            {
                bool ignoreChanged = false;
                foreach (GameProfile p in profiles)
                {
                    if (!string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(p.ExecutablePath) && autoAddIgnore.Add(p.ExecutablePath))
                        ignoreChanged = true;
                    if (!string.IsNullOrEmpty(p.LearnedExecutablePath) && autoAddIgnore.Add(p.LearnedExecutablePath))
                        ignoreChanged = true;
                }
                if (ignoreChanged) SaveAutoIgnoreLocked();
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

        public bool AddWhitelist(string name)
        {
            return AddWhitelistRule(WhitelistRuleKind.LegacyName, name);
        }

        public bool AddWhitelistPath(string executablePath)
        {
            return AddWhitelistRule(WhitelistRuleKind.ExactPath, executablePath);
        }

        public bool AddWhitelistFamily(string anchorExecutablePath)
        {
            return AddWhitelistRule(WhitelistRuleKind.ApplicationFamily, anchorExecutablePath);
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
            Logger.Log("白名单新增 " + rule.Kind + " · " + rule.Value + "：当前匹配 " + matched
                + " 个，立即恢复 " + freed + " 个后台压制");
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
            foreach (string entry in PresetWhitelist)
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
            Logger.Log("白名单已恢复为预设（" + PresetWhitelist.Length + " 项，当前匹配 " + matched
                + " 个，立即恢复 " + freed + " 个后台压制）");
            RequestPolicyApply();
            return true;
        }

        private bool SaveWhite(IList<WhitelistRule> rules)
        {
            try
            {
                var lines = new List<string>();
                lines.Add("# Pavise 后台策略豁免规则。旧版一行一个进程名的文件仍可直接读取。");
                lines.Add("# V3：N=进程名兼容规则，P=精确 EXE，F=锚点 EXE 及其当前/后续子孙。");
                lines.Add("# Windows 核心另有安全边界，这里也保留必要项并允许用户追加明确例外。");
                lines.Add(WhitelistRule.Header);
                if (rules != null)
                    foreach (WhitelistRule rule in rules) lines.Add(rule.Serialize());
                lines.Add(BuildWhitelistFooter(rules));
                return AtomicFile.WriteLines(whitePath, lines.ToArray(), "白名单");
            }
            catch (Exception error)
            {
                Logger.LogFailure("保存游戏模式白名单失败", error);
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
                AtomicFile.WriteLines(gamesPath, lines.ToArray(), "游戏列表");
            }
            catch (Exception error) { Logger.LogFailure("保存游戏列表失败", error); }
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
