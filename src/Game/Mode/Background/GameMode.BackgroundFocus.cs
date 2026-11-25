using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private static readonly HashSet<string> focusPlatformClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EpicGamesLauncher", "EpicWebHelper", "EADesktop", "EABackgroundService", "EALauncher", "Origin",
            "UbisoftConnect", "UbisoftGameLauncher", "upc", "Battle.net", "Battle.net Helper", "BlizzardBrowser",
            "GalaxyClient", "GalaxyClient Helper", "RockstarGamesLauncher", "SocialClubHelper", "RiotClientServices",
            "RiotClientUx", "RiotClientUxRender", "XboxPcApp", "GamingApp", "HoYoPlay", "Playnite.DesktopApp", "Playnite.FullscreenApp",
            "Amazon Games", "AmazonGames", "itch", "Garena", "GarenaPlatform", "NexonLauncher", "NGM",
            "PURPLE", "DMMGamePlayer", "DMMGamePlayerFastLauncher", "NetEaseGameLauncher"
        };

        private readonly Dictionary<string, string> focusBlockedThisGame = new Dictionary<string, string>(StringComparer.Ordinal);

        internal BackgroundFocusItem[] BackgroundFocusEntries()
        {
            var rules = BackgroundFocusItem.Parse(Settings.LoadStr(BackgroundFocusItem.SettingKey, ""));
            if (rules.Count == 0) return new BackgroundFocusItem[0];
            ProcessSnapshot snapshot = latestProcessSnapshot;
            lock (whiteEvalSync)
            {
                WhitelistEvaluation whitelist = snapshot == null ? null : EvaluateWhitelist(snapshot);
                HashSet<int> games = FocusProtectedGames(snapshot, whitelist, null, null);
                var entries = new List<BackgroundFocusItem>();
                foreach (BackgroundFocusItem item in rules.Values)
                {
                    string reason = FocusBlockReason(item);
                    lock (sync)
                    {
                        string retained;
                        if (reason == null && focusBlockedThisGame.TryGetValue(item.Key, out retained)) reason = retained;
                    }
                    if (reason == null && snapshot != null) reason = FocusLiveBlockReason(item, whitelist, games);
                    entries.Add(item.WithStatus(reason, reason == null && core.FocusUsesPerProcessLimits(item.Key)));
                }
                entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
                return entries.ToArray();
            }
        }

        internal bool TryAddBackgroundFocus(string name, string path, out string reason)
        {
            BackgroundFocusItem item = BackgroundFocusItem.Create(name, path);
            if (item == null)
            {
                reason = FocusReason("invalid");
                return false;
            }
            ProcessSnapshot snapshot = ProcessSnapshotSource.Capture(selfSession, 1500) ?? latestProcessSnapshot;
            lock (whiteEvalSync)
            lock (familyPolicyGate)
            {
                reason = FocusBlockReason(item);
                if (reason == null)
                {
                    if (snapshot == null)
                    {
                        lock (sync) { if (repGame != null) reason = FocusReason("invalid"); }
                    }
                    else
                    {
                        WhitelistEvaluation whitelist = EvaluateWhitelist(snapshot);
                        reason = FocusLiveBlockReason(item, whitelist, FocusProtectedGames(snapshot, whitelist, null, null));
                    }
                }
                if (reason != null) return false;
                if (!SaveBackgroundFocus(item, true))
                {
                    reason = FocusReason("save");
                    return false;
                }
            }
            RequestPolicyApply("AddBackgroundFocus");
            return true;
        }

        internal bool TryRemoveBackgroundFocus(string name, string path, out string reason)
        {
            BackgroundFocusItem item = BackgroundFocusItem.Create(name, path);
            reason = item == null ? FocusReason("invalid") : null;
            if (item == null) return false;
            lock (familyPolicyGate)
            {
                if (!SaveBackgroundFocus(item, false))
                {
                    reason = FocusReason("save");
                    return false;
                }
                core.RemoveFocusedProgram(item.Key);
            }
            RequestPolicyApply("RemoveBackgroundFocus");
            return true;
        }

        private bool SaveBackgroundFocus(BackgroundFocusItem item, bool add)
        {
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed) return false;
            }
            string raw;
            if (!Settings.TryLoadStr(BackgroundFocusItem.SettingKey, out raw)) return false;
            var rules = BackgroundFocusItem.Parse(raw);
            if (add == rules.ContainsKey(item.Key)) return true;
            if (add) rules.Add(item.Key, item);
            else rules.Remove(item.Key);
            string value = BackgroundFocusItem.Serialize(rules.Values);
            if (!Settings.SaveStrDurable(BackgroundFocusItem.SettingKey, value)
                || !Settings.TryLoadStr(BackgroundFocusItem.SettingKey, out raw) || raw != value) return false;
            InvalidateFamilyPolicy();
            lock (sync)
            {
                if (!add) focusBlockedThisGame.Remove(item.Key);
                firstSweep = true;
            }
            return true;
        }

        private static string FocusReason(string reason)
        {
            return BackgroundFocusItem.ReasonPrefix + reason;
        }

        private string FocusBlockReason(BackgroundFocusItem item)
        {
            if (string.Equals(item.Name, selfName, StringComparison.OrdinalIgnoreCase)
                || SelfBuildGuard.IsOwnBuild(item.Name, item.Path)) return FocusReason("self");
            if (FamilyBoundary.UnderRoot(item.Path, windowsPrefix)) return FocusReason("windows");
            if (AntiCheatCatalog.IsAntiCheatProcess(item.Name, item.Path)) return FocusReason("anticheat");
            if (PlatformRuntimePolicy.RuntimePlatform(item.Name) != null || focusPlatformClients.Contains(item.Name)) return FocusReason("platform");
            if (VoiceChatCatalog.IsVoiceChatProcess(item.Name, item.Path)) return FocusReason("voice");
            if (StreamingToolCatalog.IsStreamingToolProcess(item.Name, item.Path)) return FocusReason("streaming");
            if (OverlayHostCatalog.ShouldProtectProcess(item.Name, item.Path)) return FocusReason("overlay");
            if (PeripheralCatalog.IsInputChainProcess(item.Name, item.Path)
                || HardwareControlCatalog.IsHardwareControlProcess(item.Name)) return FocusReason("peripheral");
            if (SecurityProductCatalog.IsSecurityProduct(item.Path)) return FocusReason("security");
            if (NetAcceleratorCatalog.IsAcceleratorLikeName(item.Name)
                || NetAcceleratorCatalog.IsAcceleratorLikePath(item.Path)) return FocusReason("accelerator");
            if (SelfProtectedRoster.Contains(item.Name)) return FocusReason("whitelist");
            if (FamilyBoundary.IsRequiredGameLaunchHost(item.Name, item.Path)) return FocusReason("game");
            lock (sync)
            {
                foreach (WhitelistRule rule in whiteRules)
                    if (rule.MatchesNormalized(item.Name, item.Path)) return FocusReason("whitelist");
                foreach (GameProfile profile in profiles)
                {
                    if (WhitelistRule.PathEquals(profile.ExecutablePath, item.Path)
                        || (FamilyBoundary.SafeFamilyDir(profile.Root) && FamilyBoundary.UnderRoot(item.Path, profile.Root)))
                        return FocusReason("game");
                    if (string.IsNullOrEmpty(profile.ExecutablePath))
                        foreach (string entry in profile.Entries)
                            if (string.Equals(WhitelistRule.NormalizeName(entry), item.Name, StringComparison.OrdinalIgnoreCase))
                                return FocusReason("game");
                }
            }
            return null;
        }

        private HashSet<int> FocusProtectedGames(ProcessSnapshot snapshot, WhitelistEvaluation whitelist,
            HashSet<int> gamePids, HashSet<int> rendererDescendants, FamilyBoundary.SweepIndex familyIndex = null)
        {
            var games = new HashSet<int>(gamePids ?? EmptyPidSet);
            if (snapshot == null) return games;
            List<GameProfile> configured;
            lock (sync)
            {
                configured = new List<GameProfile>(profiles);
                if (activeDetection != null)
                {
                    ProcEntry live = snapshot.Find(activeDetection.RendererPid);
                    if (live != null && live.Creation == activeDetection.RendererCreation) games.Add(live.Pid);
                }
            }
            games.UnionWith(FamilyBoundary.CollectProtectedLibraryFamily(configured, snapshot, selfPid, selfSession, FamilyEvidence, false, true, familyIndex));
            if (games.Count > 0)
                games.UnionWith(FamilyBoundary.WalkDescendants(whitelist.Parents, games, selfPid, 24, whitelist.Creations));
            if (rendererDescendants != null) games.UnionWith(rendererDescendants);
            return games;
        }

        private static string FocusLiveBlockReason(BackgroundFocusItem item, WhitelistEvaluation whitelist, HashSet<int> games)
        {
            foreach (WhitelistProcessInfo process in whitelist.Processes.Values)
            {
                if (!string.Equals(process.Name, item.Name, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(process.Path, item.Path, StringComparison.OrdinalIgnoreCase)) continue;
                string reason = FocusLiveBlockReason(process.Pid, whitelist, games);
                if (reason != null) return reason;
            }
            return null;
        }

        private static string FocusLiveBlockReason(int pid, WhitelistEvaluation whitelist, HashSet<int> games)
        {
            if (games.Contains(pid)) return FocusReason("game");
            if (whitelist.PlatformProcesses.ContainsKey(pid)) return FocusReason("platform");
            if (whitelist.Protected.Contains(pid)) return FocusReason("whitelist");
            return null;
        }

        private Dictionary<int, string> FocusProgramMembers(ProcessSnapshot snapshot, WhitelistEvaluation whitelist,
            HashSet<int> gamePids, HashSet<int> rendererDescendants, FamilyBoundary.SweepIndex familyIndex)
        {
            var members = new Dictionary<int, string>();
            var rules = BackgroundFocusItem.Parse(Settings.LoadStr(BackgroundFocusItem.SettingKey, ""));
            if (rules.Count == 0) return members;
            HashSet<int> games = FocusProtectedGames(snapshot, whitelist, gamePids, rendererDescendants, familyIndex);
            var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BackgroundFocusItem item in rules.Values)
            {
                names.Add(item.Name);
                string reason = FocusBlockReason(item);
                if (reason != null) blocked[item.Key] = reason;
            }
            foreach (WhitelistProcessInfo process in whitelist.Processes.Values)
            {
                if (process.Session != selfSession || process.Creation <= 0 || !names.Contains(process.Name)) continue;
                string key = BackgroundFocusItem.Identity(process.Name, process.Path);
                if (key == null || !rules.ContainsKey(key)) continue;
                string reason = FocusLiveBlockReason(process.Pid, whitelist, games);
                if (reason != null) blocked[key] = reason;
                members[process.Pid] = key;
            }
            lock (sync)
            {
                foreach (KeyValuePair<string, string> item in blocked) focusBlockedThisGame[item.Key] = item.Value;
                foreach (int pid in new List<int>(members.Keys))
                    if (focusBlockedThisGame.ContainsKey(members[pid])) members.Remove(pid);
            }
            return members;
        }

        private void ReleaseFocusWhitelistMatches(WhitelistEvaluation whitelist)
        {
            var rules = BackgroundFocusItem.Parse(Settings.LoadStr(BackgroundFocusItem.SettingKey, ""));
            if (rules.Count == 0) return;
            lock (familyPolicyGate)
            {
                foreach (BackgroundFocusItem item in rules.Values)
                {
                    string reason = FocusBlockReason(item);
                    if (reason == null) reason = FocusLiveBlockReason(item, whitelist, EmptyPidSet);
                    if (reason == null) continue;
                    lock (sync)
                    {
                        if (repGame != null) focusBlockedThisGame[item.Key] = reason;
                    }
                    core.RemoveFocusedProgram(item.Key);
                }
            }
        }

        private void ResetBackgroundFocusGame()
        {
            focusBlockedThisGame.Clear();
        }
    }
}
