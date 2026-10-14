using System;
using System.Text;

namespace PaviseApp
{
    internal static class GameSearchMatcher
    {
        internal static bool Matches(string query, string name, string path)
        {
            string text = CompatibleText(query).Trim();
            if (text.Length == 0) return true;

            string normalizedName = CompatibleText(name);
            string normalizedPath = CompatibleText(path);
            string compactName = Compact(normalizedName);
            string compactPath = Compact(normalizedPath);
            bool hasTerm = false;
            foreach (string word in text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                string term = Compact(word);
                if (term.Length == 0) continue;
                hasTerm = true;
                if (compactName.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0
                    && compactPath.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            // 纯标点搜索按原文匹配，不能变为空串。
            return hasTerm
                || normalizedName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedPath.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string CompatibleText(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return value.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { return value; }
        }

        private static string Compact(string value)
        {
            var result = new StringBuilder(value.Length);
            foreach (char character in value)
                if (!char.IsWhiteSpace(character) && !char.IsPunctuation(character)) result.Append(character);
            return result.ToString();
        }
    }
}
