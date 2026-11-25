using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal sealed class BackgroundFocusItem
    {
        internal const string SettingKey = "BackgroundFocusProgramsV1";
        internal const string ReasonPrefix = "background.focus.reason.";

        internal readonly string Name, Path, BlockReason;
        internal readonly bool UsesPerProcessLimits;
        internal readonly string Key;

        private BackgroundFocusItem(string name, string path, string key, string blockReason, bool usesPerProcessLimits = false)
        {
            Name = name;
            Path = path;
            Key = key;
            BlockReason = blockReason;
            UsesPerProcessLimits = usesPerProcessLimits;
        }

        internal static string Identity(string name, string path)
        {
            string normalizedName = WhitelistRule.NormalizeName(name);
            string normalizedPath = WhitelistRule.NormalizeImagePath(path);
            return KeyOf(normalizedName, normalizedPath);
        }

        private static string KeyOf(string name, string path)
        {
            if (name.Length == 0 || name.IndexOf('|') >= 0 || name.IndexOf('\0') >= 0 || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return name.ToUpperInvariant() + "|" + path.ToUpperInvariant();
        }

        internal static BackgroundFocusItem Create(string name, string path)
        {
            string normalizedName = WhitelistRule.NormalizeName(name);
            string normalizedPath = WhitelistRule.NormalizeImagePath(path);
            string key = KeyOf(normalizedName, normalizedPath);
            return key == null ? null : new BackgroundFocusItem(normalizedName, normalizedPath, key, null);
        }

        internal BackgroundFocusItem WithStatus(string reason, bool usesPerProcessLimits = false)
        {
            return new BackgroundFocusItem(Name, Path, Key, reason, usesPerProcessLimits);
        }

        internal static Dictionary<string, BackgroundFocusItem> Parse(string saved)
        {
            var rules = new Dictionary<string, BackgroundFocusItem>(StringComparer.Ordinal);
            foreach (string entry in (saved ?? "").Split(';'))
            {
                if (entry.Length == 0) continue;
                try
                {
                    string record = Encoding.UTF8.GetString(Convert.FromBase64String(entry));
                    int separator = record.IndexOf('|');
                    if (separator < 1) continue;
                    BackgroundFocusItem rule = Create(record.Substring(0, separator), record.Substring(separator + 1));
                    if (rule != null) rules[rule.Key] = rule;
                }
                catch (FormatException) { }
            }
            return rules;
        }

        internal static string Serialize(IEnumerable<BackgroundFocusItem> rules)
        {
            var saved = new List<string>();
            foreach (BackgroundFocusItem rule in rules)
                saved.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(rule.Name + "|" + rule.Path)));
            saved.Sort(StringComparer.Ordinal);
            return string.Join(";", saved);
        }
    }
}
