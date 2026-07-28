// @author bdth 2074055628@qq.com
// 游戏列表、配置档案与白名单的持久化操作。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AegisApp
{
    internal partial class GameMode
    {
        public bool AddGameExecutable(string name, string executablePath)
        {
            string resolved, error;
            if (!GameExecutableResolver.TryResolve(executablePath, out resolved, out error)) return false;
            string entry = StripExe(Path.GetFileName(resolved));
            string display = DisplayName(resolved, name);
            string root = NormalizeGameRoot(GameScan.InferGameRoot(resolved));
            lock (sync)
            {
                foreach (GameProfile p in profiles)
                {
                    if (string.Equals(p.ExecutablePath, resolved, StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.IsNullOrEmpty(p.ExecutablePath) && p.Entries.Contains(entry))
                    {
                        p.ExecutablePath = resolved;
                        p.Root = root;
                        p.Name = display;
                        RebuildLegacyGameIndex();
                        profileStore.Save(profiles);
                        SaveGames();
                        kick.Set();
                        return true;
                    }
                }
                GameProfile profile = GameProfileStore.NewProfile(display, root, resolved);
                profile.Entries.Clear();
                profile.Entries.Add(entry);
                profiles.Add(profile);
                RebuildLegacyGameIndex();
                profileStore.Save(profiles);
                SaveGames();
            }
            kick.Set();
            return true;
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
            kick.Set();
        }

        public List<string> GetWhitelist()
        {
            lock (sync)
            {
                var copy = new List<string>(white);
                copy.Sort(StringComparer.OrdinalIgnoreCase);
                return copy;
            }
        }

        public bool AddWhitelist(string name)
        {
            string normalized = StripExe(name.Trim());
            if (normalized.Length == 0) return false;
            lock (sync)
            {
                if (whiteSet.Contains(normalized)) return false;
                whiteSet.Add(normalized);
                white.Add(normalized);
                SaveWhite();
            }
            int freed = core.ReleaseByName(normalized, SuppressReason.Background);
            int thawed = freezer.RestoreByName(normalized);
            if (freed > 0) Logger.Log("白名单新增 " + normalized + "：已立即恢复 " + freed + " 个进程");
            if (thawed > 0) Logger.Log("白名单新增 " + normalized + "：已立即解冻 " + thawed + " 个进程");
            kick.Set();
            return true;
        }

        public void RemoveWhitelist(string name)
        {
            lock (sync)
            {
                string normalized = StripExe(name.Trim());
                whiteSet.Remove(normalized);
                white.RemoveAll(w => string.Equals(w, normalized, StringComparison.OrdinalIgnoreCase));
                SaveWhite();
            }
            kick.Set();
        }

        public void ResetWhitelist()
        {
            lock (sync)
            {
                white.Clear();
                whiteSet.Clear();
                foreach (string entry in PresetWhitelist) AddWhiteNoSave(entry);
                try { WritePreset(); }
                catch (Exception error) { Logger.LogFailure("恢复预设白名单失败", error); }
            }
            Logger.Log("白名单已恢复为预设（" + PresetWhitelist.Length + " 项）");
            kick.Set();
        }

        private void SaveWhite()
        {
            try
            {
                var lines = new List<string>();
                lines.Add("# Aegis 游戏模式白名单（必要清单）—— 游戏模式激活时只有这些进程不被压制");
                lines.Add("# 一行一个进程名(不带 .exe)，# 开头是注释。仅处理当前用户会话；");
                lines.Add("# Windows 核心另有安全边界，这里也保留必要项并允许用户追加明确例外。");
                lines.AddRange(white);
                AtomicFile.WriteLines(whitePath, lines.ToArray(), "白名单");
            }
            catch (Exception error) { Logger.LogFailure("保存游戏模式白名单失败", error); }
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
