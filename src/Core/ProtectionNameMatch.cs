using System;

namespace PaviseApp
{
    internal static class ProtectionNameMatch
    {
        internal static bool Word(string value, string word, bool camelCase)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(word)) return false;
            for (int start = 0; start <= value.Length - word.Length; )
            {
                int i = value.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return false;
                int end = i + word.Length;
                if (Boundary(value, i, camelCase) && Boundary(value, end, camelCase)) return true;
                start = i + 1;
            }
            return false;
        }

        private static bool Boundary(string value, int at, bool camelCase)
        {
            if (at == 0 || at == value.Length) return true;
            char before = value[at - 1], after = value[at];
            if (!char.IsLetter(before) || !char.IsLetter(after)) return true;
            return camelCase && char.IsUpper(after)
                && (char.IsLower(before) || at + 1 < value.Length && char.IsLower(value[at + 1]));
        }

        internal static bool RuntimeVariant(string name, string product)
        {
            string n = WhitelistRule.NormalizeName(name);
            if (!n.StartsWith(product, StringComparison.OrdinalIgnoreCase)) return false;
            string tail = n.Substring(product.Length).TrimStart('.', '-', '_', ' ');
            if (tail.Length == 0) return true;
            foreach (string suffix in new[] { "service", "svc", "engine", "daemon" })
                if (tail.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
                { tail = tail.Substring(suffix.Length).TrimStart('.', '-', '_', ' '); break; }
            if (tail.Length == 0) return true;
            if (tail.Equals("x86", StringComparison.OrdinalIgnoreCase)
                || tail.Equals("x64", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (char c in tail) if (!char.IsDigit(c) && c != '.') return false;
            return true;
        }

        internal static bool Auxiliary(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string marker in new[] { "update", "upgrade", "install", "setup", "crash",
                "telemetry", "diagnos", "analytics", "webhelper", "reporter" })
                if (name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
