// File purpose Fuzzy name and path search for the game list; matches existing text only, never guesses aliases
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

            // Pure punctuation must not collapse to an empty string and match everything; keep literal search semantics
            return hasTerm
                || normalizedName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedPath.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string CompatibleText(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try { return value.Normalize(NormalizationForm.FormKC); }
            // IME or clipboard input may briefly carry unpaired surrogates; keep the original text for a safe literal match
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
