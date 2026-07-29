// @author bdth 2074055628@qq.com
// 文件用途 可逆地把英雄联盟画质与呈现模式压到竞技档

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AegisApp
{
    internal static class LolGraphicsConfig
    {
        private const string BackupSettingKey = "LolGraphicsOriginal";

        internal enum GraphicsGroup
        {
            Quality = 0,
            Presentation = 1
        }

        internal sealed class Tunable
        {
            public readonly GraphicsGroup Group;
            public readonly string Section;
            public readonly string Key;
            public readonly int Competitive;

            public Tunable(GraphicsGroup group, string section, string key, int competitive)
            {
                Group = group; Section = section; Key = key; Competitive = competitive;
            }

            public string Identity { get { return Section + "." + Key; } }
        }

        internal static readonly Tunable[] Tunables =
        {
            new Tunable(GraphicsGroup.Quality, "Performance", "ShadowQuality", 0),
            new Tunable(GraphicsGroup.Quality, "Performance", "EnableFXAA", 0),
            new Tunable(GraphicsGroup.Quality, "Performance", "EnvironmentQuality", 0),
            new Tunable(GraphicsGroup.Quality, "Performance", "EffectsQuality", 0),
            new Tunable(GraphicsGroup.Quality, "Performance", "CharacterQuality", 0),
            new Tunable(GraphicsGroup.Quality, "Performance", "EnableHUDAnimations", 0),
            new Tunable(GraphicsGroup.Quality, "General", "HideEyeCandy", 1),
            new Tunable(GraphicsGroup.Quality, "General", "ShowGodray", 0),
            new Tunable(GraphicsGroup.Presentation, "General", "WaitForVerticalSync", 0),
            new Tunable(GraphicsGroup.Presentation, "General", "WindowMode", 0)
        };

        internal sealed class GroupState
        {
            public int AtCompetitive;
            public int Total;
            public int Owned;
            public bool AllCompetitive { get { return Total > 0 && AtCompetitive == Total; } }
        }

        internal sealed class State
        {
            public bool Available;
            public string Error;
            public string ConfigPath;
            public bool Blocked;
            public List<string> BlockingProcesses = new List<string>();
            public readonly Dictionary<GraphicsGroup, GroupState> Groups =
                new Dictionary<GraphicsGroup, GroupState>();

            public GroupState Of(GraphicsGroup group)
            {
                GroupState found;
                if (Groups.TryGetValue(group, out found)) return found;
                found = new GroupState();
                Groups[group] = found;
                return found;
            }
        }

        public static string ConfigPathOf(string lolRoot)
        {
            if (string.IsNullOrEmpty(lolRoot)) return null;
            try { return Path.Combine(lolRoot, "Game", "Config", "game.cfg"); }
            catch { return null; }
        }

        public static State Inspect(string lolRoot)
        {
            List<string> blocking;
            bool blocked = LolAddonCleaner.HasBlockingProcesses(lolRoot, out blocking);
            return Inspect(lolRoot, blocked, blocking);
        }

        // 调用方已经做过占用检查时走这个重载，省掉一次全进程枚举
        public static State Inspect(string lolRoot, bool blocked, List<string> blocking)
        {
            var state = new State();
            foreach (Tunable tunable in Tunables) state.Of(tunable.Group).Total++;
            state.ConfigPath = ConfigPathOf(lolRoot);
            if (string.IsNullOrEmpty(state.ConfigPath) || !File.Exists(state.ConfigPath))
            {
                state.Error = Lang.T("lolgfx.err.noconfig");
                return state;
            }

            string[] lines;
            if (!TryReadLines(state.ConfigPath, out lines, out state.Error)) return state;

            Dictionary<string, int> owned = OwnedBackupFor(lolRoot);
            foreach (Tunable tunable in Tunables)
            {
                GroupState group = state.Of(tunable.Group);
                if (ReadInt(lines, tunable.Section, tunable.Key) == tunable.Competitive)
                    group.AtCompetitive++;
                if (owned != null && owned.ContainsKey(tunable.Identity)) group.Owned++;
            }
            state.Available = true;

            if (blocked)
            {
                state.Blocked = true;
                state.BlockingProcesses = blocking ?? new List<string>();
            }
            return state;
        }

        public static bool Apply(string lolRoot, GraphicsGroup group, out string error)
        {
            return Write(lolRoot, group, true, out error);
        }

        public static bool Restore(string lolRoot, GraphicsGroup group, out string error)
        {
            return Write(lolRoot, group, false, out error);
        }

        private static bool Write(
            string lolRoot, GraphicsGroup group, bool apply, out string error)
        {
            error = null;
            string path = ConfigPathOf(lolRoot);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = Lang.T("lolgfx.err.noconfig");
                return false;
            }

            List<string> blocking;
            if (LolAddonCleaner.HasBlockingProcesses(lolRoot, out blocking))
            {
                error = Lang.F("lolgfx.err.running", string.Join(", ", blocking.ToArray()));
                return false;
            }

            string owner;
            Dictionary<string, int> backup;
            if (!ReadBackup(out owner, out backup))
            {
                owner = null;
                backup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
            if (backup.Count > 0 && owner != null && !SameRoot(owner, lolRoot))
            {
                error = Lang.T("lolgfx.err.otherinstall");
                return false;
            }

            string[] lines;
            if (!TryReadLines(path, out lines, out error)) return false;

            string[] updated = lines;
            int written = 0;
            int releasedByUser = 0;
            foreach (Tunable tunable in Tunables)
            {
                if (tunable.Group != group) continue;
                int current = ReadInt(updated, tunable.Section, tunable.Key);

                if (apply)
                {
                    if (!backup.ContainsKey(tunable.Identity)) backup[tunable.Identity] = current;
                    if (current == tunable.Competitive) continue;
                    updated = SetInt(updated, tunable.Section, tunable.Key, tunable.Competitive);
                    written++;
                    continue;
                }

                int original;
                if (!backup.TryGetValue(tunable.Identity, out original)) continue;
                if (current != tunable.Competitive)
                {
                    backup.Remove(tunable.Identity);
                    releasedByUser++;
                    continue;
                }
                backup.Remove(tunable.Identity);
                if (original < 0)
                {
                    // 应用前原文件没有这个键：整行移除，让游戏回落内置默认值
                    string[] trimmed = RemoveKey(updated, tunable.Section, tunable.Key);
                    if (!ReferenceEquals(trimmed, updated))
                    {
                        updated = trimmed;
                        written++;
                    }
                    continue;
                }
                updated = SetInt(updated, tunable.Section, tunable.Key, original);
                written++;
            }

            // 应用先落备份再改文件，还原先改文件再收备份：任一顺序断掉都不会丢原值
            if (apply && !Settings.SaveStr(BackupSettingKey, FormatBackup(lolRoot, backup)))
            {
                error = Lang.T("lolgfx.err.backupfail");
                return false;
            }
            if (written > 0 && !AtomicFile.WriteLines(path, updated, "game.cfg"))
            {
                error = Lang.T("lolgfx.err.writefail");
                return false;
            }
            if (!apply && !Settings.SaveStr(BackupSettingKey, FormatBackup(lolRoot, backup)))
            {
                error = Lang.T("lolgfx.err.backupfail");
                return false;
            }
            Logger.Log("英雄联盟画质：" + (apply ? "压到竞技档" : "还原原值")
                + "（" + GroupName(group) + "），写入 " + written + " 项"
                + (releasedByUser > 0 ? "，跳过 " + releasedByUser + " 项（用户已自行修改）" : ""));
            return true;
        }

        internal static string GroupName(GraphicsGroup group)
        {
            return Lang.T(group == GraphicsGroup.Presentation
                ? "lolgfx.group.presentation" : "lolgfx.group.quality");
        }

        private static bool SameRoot(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd('\\'),
                    Path.GetFullPath(right).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static Dictionary<string, int> OwnedBackupFor(string lolRoot)
        {
            string owner;
            Dictionary<string, int> backup;
            if (!ReadBackup(out owner, out backup)) return null;
            if (owner != null && !SameRoot(owner, lolRoot)) return null;
            return backup;
        }

        private static bool TryReadLines(string path, out string[] lines, out string error)
        {
            lines = null;
            error = null;
            try
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                error = Lang.F("lolgfx.err.readfail", ex.Message);
                return false;
            }
        }

        internal static int ReadInt(string[] lines, string section, string key)
        {
            if (lines == null) return -1;
            bool inSection = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i] == null ? "" : lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == '[')
                {
                    inSection = IsSectionHeader(line, section);
                    continue;
                }
                if (!inSection) continue;
                string value;
                if (!TryMatchKey(line, key, out value)) continue;
                int parsed;
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    ? parsed : -1;
            }
            return -1;
        }

        internal static string[] SetInt(string[] lines, string section, string key, int value)
        {
            var output = new List<string>(lines == null ? new string[0] : lines);
            string rendered = key + "=" + value.ToString(CultureInfo.InvariantCulture);
            bool inSection = false;
            int sectionEnd = -1;

            for (int i = 0; i < output.Count; i++)
            {
                string line = output[i] == null ? "" : output[i].Trim();
                if (line.Length > 0 && line[0] == '[')
                {
                    if (inSection) { sectionEnd = i; break; }
                    inSection = IsSectionHeader(line, section);
                    continue;
                }
                if (!inSection) continue;
                string existing;
                if (!TryMatchKey(line, key, out existing)) continue;
                output[i] = rendered;
                return output.ToArray();
            }

            if (!inSection && sectionEnd < 0)
            {
                bool needsBlank = output.Count > 0
                    && !string.IsNullOrEmpty((output[output.Count - 1] ?? "").Trim());
                if (needsBlank) output.Add("");
                output.Add("[" + section + "]");
                output.Add(rendered);
                return output.ToArray();
            }

            int insertAt = sectionEnd < 0 ? output.Count : sectionEnd;
            while (insertAt > 0 && string.IsNullOrEmpty((output[insertAt - 1] ?? "").Trim())) insertAt--;
            output.Insert(insertAt, rendered);
            return output.ToArray();
        }

        internal static string[] RemoveKey(string[] lines, string section, string key)
        {
            if (lines == null) return lines;
            bool inSection = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i] == null ? "" : lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == '[')
                {
                    inSection = IsSectionHeader(line, section);
                    continue;
                }
                if (!inSection) continue;
                string value;
                if (!TryMatchKey(line, key, out value)) continue;
                var output = new List<string>(lines);
                output.RemoveAt(i);
                return output.ToArray();
            }
            return lines;
        }

        private static bool IsSectionHeader(string line, string section)
        {
            int close = line.IndexOf(']');
            if (close <= 1) return false;
            return string.Equals(
                line.Substring(1, close - 1).Trim(), section, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryMatchKey(string line, string key, out string value)
        {
            value = null;
            int eq = line.IndexOf('=');
            if (eq <= 0) return false;
            if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                return false;
            value = line.Substring(eq + 1).Trim();
            return true;
        }

        internal static string FormatBackup(string lolRoot, Dictionary<string, int> values)
        {
            if (values == null || values.Count == 0) return "";
            var parts = new List<string>();
            parts.Add("V3");
            parts.Add("root=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(lolRoot ?? "")));
            foreach (Tunable tunable in Tunables)
            {
                int value;
                if (!values.TryGetValue(tunable.Identity, out value)) continue;
                parts.Add(tunable.Identity + "=" + value.ToString(CultureInfo.InvariantCulture));
            }
            return parts.Count > 2 ? string.Join("|", parts.ToArray()) : "";
        }

        internal static bool TryParseBackup(
            string text, out string lolRoot, out Dictionary<string, int> values)
        {
            lolRoot = null;
            values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('|');
            if (parts.Length < 2) return false;

            if (parts[0] == "V1")
            {
                int shadow, fxaa;
                if (parts.Length != 3
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out shadow)
                    || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out fxaa))
                    return false;
                values["Performance.ShadowQuality"] = shadow;
                values["Performance.EnableFXAA"] = fxaa;
                return true;
            }
            if (parts[0] != "V2" && parts[0] != "V3") return false;

            for (int i = 1; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) return false;
                string name = parts[i].Substring(0, eq);
                string raw = parts[i].Substring(eq + 1);
                if (string.Equals(name, "root", StringComparison.Ordinal))
                {
                    try { lolRoot = Encoding.UTF8.GetString(Convert.FromBase64String(raw)); }
                    catch { return false; }
                    continue;
                }
                int parsed;
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return false;
                values[name] = parsed;
            }
            return values.Count > 0;
        }

        private static bool ReadBackup(out string lolRoot, out Dictionary<string, int> values)
        {
            lolRoot = null;
            values = null;
            string text = Settings.LoadStr(BackupSettingKey, "");
            if (string.IsNullOrEmpty(text)) return false;
            return TryParseBackup(text, out lolRoot, out values);
        }
    }
}
