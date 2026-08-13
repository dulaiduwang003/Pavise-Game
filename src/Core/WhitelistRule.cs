// @author bdth 2074055628@qq.com
// 文件用途 定义可版本化的后台策略豁免规则

using System;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal enum WhitelistRuleKind
    {
        LegacyName = 0,
        ExactPath = 1,
        ApplicationFamily = 2
    }

    internal sealed class WhitelistRule
    {
        internal const string Header = "PAVISE_WHITELIST_V3";
        internal const string LegacyHeader = "PAVISE_WHITELIST_V2";
        private static readonly string[] UnsafeFamilyHosts =
        {

            "explorer", "cmd", "powershell", "powershell_ise", "pwsh",
            "wscript", "cscript", "mshta", "rundll32", "regsvr32",
            "conhost", "openconsole", "windowsterminal", "dllhost",
            "svchost", "taskhostw", "taskeng", "runtimebroker",
            "applicationframehost", "msiexec",

            "wsl", "wslhost", "wslservice", "bash", "sh",
            "python", "pythonw", "py", "pyw", "pypy", "pypy3",
            "node", "deno", "bun", "java", "javaw", "dotnet",
            "php", "php-win", "perl", "ruby", "rubyw", "lua",
            "csi", "msbuild"
        };
        private static readonly string[] VersionedRuntimeHostPrefixes =
        {
            "python", "pythonw", "pyw", "pypy", "node", "java", "javaw",
            "dotnet", "php", "php-win", "perl", "ruby", "rubyw", "lua"
        };

        public readonly WhitelistRuleKind Kind;
        public readonly string Value;

        private WhitelistRule(WhitelistRuleKind kind, string value)
        {
            Kind = kind;
            Value = value;
        }

        public string Key { get { return ((int)Kind) + ":" + Value; } }

        public static bool TryCreate(WhitelistRuleKind kind, string value, out WhitelistRule rule)
        {
            rule = null;
            string normalized = kind == WhitelistRuleKind.LegacyName
                ? NormalizeName(value) : NormalizeImagePath(value);
            if (string.IsNullOrEmpty(normalized)) return false;
            if (kind != WhitelistRuleKind.LegacyName
                && !string.Equals(Path.GetExtension(normalized), ".exe",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (kind == WhitelistRuleKind.ApplicationFamily
                && IsUnsafeFamilyAnchor(normalized))
                return false;
            rule = new WhitelistRule(kind, normalized);
            return true;
        }

        public static bool TryParseVersioned(string line, out WhitelistRule rule)
        {
            rule = null;
            if (string.IsNullOrWhiteSpace(line)) return false;
            int split = line.IndexOf('|');
            if (split != 1 || line.Length <= 2) return false;

            WhitelistRuleKind kind;
            char tag = char.ToUpperInvariant(line[0]);
            if (tag == 'N') kind = WhitelistRuleKind.LegacyName;
            else if (tag == 'P') kind = WhitelistRuleKind.ExactPath;
            else if (tag == 'F') kind = WhitelistRuleKind.ApplicationFamily;
            else return false;

            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(2))); }
            catch { return false; }
            return TryCreate(kind, decoded, out rule);
        }

        public string Serialize()
        {
            char tag = Kind == WhitelistRuleKind.LegacyName ? 'N'
                : (Kind == WhitelistRuleKind.ExactPath ? 'P' : 'F');
            return tag + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Value));
        }

#if PAVISE_SELFTEST
        public bool MatchesDirect(string processName, string imagePath)
        {
            if (Kind == WhitelistRuleKind.LegacyName)
                return string.Equals(Value, NormalizeName(processName), StringComparison.OrdinalIgnoreCase);
            return PathEquals(Value, imagePath);
        }
#endif

        internal bool MatchesNormalized(string normalizedName, string normalizedImagePath)
        {
            return Kind == WhitelistRuleKind.LegacyName
                ? string.Equals(Value, normalizedName, StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrEmpty(normalizedImagePath)
                    && string.Equals(Value, normalizedImagePath, StringComparison.OrdinalIgnoreCase);
        }

        internal static string NormalizeName(string value)
        {
            string name = (value ?? "").Trim();
            if (name.Length == 0) return "";
            try
            {
                string leaf = Path.GetFileName(name);
                if (!string.IsNullOrEmpty(leaf)) name = leaf;
            }
            catch { }
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return name.Trim();
        }

        internal static string NormalizeImagePath(string value)
        {
            string path = (value ?? "").Trim().Trim('"');
            if (path.Length == 0 || path.IndexOf('\0') >= 0) return "";
            path = path.Replace('/', '\\');
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                path = @"\\" + path.Substring(8);
            else if (path.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
                path = @"\\" + path.Substring(8);
            else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                string tail = path.Substring(4);
                if (tail.Length < 3 || !char.IsLetter(tail[0])
                    || tail[1] != ':' || tail[2] != '\\') return "";
                path = tail;
            }
            else if (path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            {
                string tail = path.Substring(4);
                if (tail.Length < 3 || !char.IsLetter(tail[0])
                    || tail[1] != ':' || tail[2] != '\\') return "";
                path = tail;
            }
            if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
                return "";
            bool driveAbsolute = path.Length >= 3 && char.IsLetter(path[0])
                && path[1] == ':' && path[2] == '\\';
            bool uncAbsolute = path.StartsWith(@"\\", StringComparison.Ordinal)
                && path.Length > 3;
            if (!driveAbsolute && !uncAbsolute)
                return "";
            try { path = Path.GetFullPath(path); }
            catch { return ""; }
            string leaf;
            try { leaf = Path.GetFileName(path); }
            catch { return ""; }
            return string.IsNullOrEmpty(leaf) ? "" : path;
        }

        internal static bool PathEquals(string left, string right)
        {
            string a = NormalizeImagePath(left);
            string b = NormalizeImagePath(right);
            return a.Length > 0 && b.Length > 0
                && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsUnsafeFamilyAnchor(string imagePath)
        {
            string path = NormalizeImagePath(imagePath);
            string name;
            try { name = Path.GetFileNameWithoutExtension(path); }
            catch { return true; }
            foreach (string host in UnsafeFamilyHosts)
                if (string.Equals(name, host, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string prefix in VersionedRuntimeHostPrefixes)
                if (HasVersionSuffix(name, prefix)) return true;
            return false;
        }

        private static bool HasVersionSuffix(string name, string prefix)
        {
            if (string.IsNullOrEmpty(name)
                || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || name.Length == prefix.Length)
                return false;
            for (int i = prefix.Length; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsDigit(c) && c != '.' && c != '-' && c != '_')
                    return false;
            }
            return true;
        }
    }

    internal sealed class WhitelistRuleView
    {
        public readonly WhitelistRule Rule;
        public readonly int CurrentMatches;
        public readonly bool Required;

        public WhitelistRuleView(
            WhitelistRule rule, int currentMatches, bool required = false)
        {
            Rule = rule;
            CurrentMatches = currentMatches;
            Required = required;
        }

        public override string ToString()
        {
            string kind = Rule.Kind == WhitelistRuleKind.ApplicationFamily ? Lang.T("white.kind.family")
                : (Rule.Kind == WhitelistRuleKind.ExactPath ? Lang.T("white.kind.path") : Lang.T("white.kind.name"));
            return "[" + kind + "] " + Rule.Value + " "
                + (CurrentMatches < 0
                    ? Lang.T("white.matches.pending") : Lang.F("white.matches", CurrentMatches))
                + (Required ? " " + Lang.T("white.required.badge") : "");
        }
    }
}
