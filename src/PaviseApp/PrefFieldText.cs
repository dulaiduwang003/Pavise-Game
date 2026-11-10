using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class PrefFieldText
    {
        public static string MergeField(string current, string field, string value)
        {
            List<string> entryTextTrimmeds = new List<string>();
            bool fieldReplaced = false;
            if (!string.IsNullOrEmpty(current))
            {
                foreach (string entryText in current.Split(';'))
                {
                    string entryTextTrimmed = entryText.Trim();
                    if (entryTextTrimmed.Length == 0)
                    {
                        continue;
                    }
                    int matchIndex = entryTextTrimmed.IndexOf('=');
                    string a = ((matchIndex > 0) ? entryTextTrimmed.Substring(0, matchIndex).Trim() : entryTextTrimmed);
                    if (matchIndex > 0 && string.Equals(a, field, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!fieldReplaced)
                        {
                            entryTextTrimmeds.Add(field + "=" + value);
                            fieldReplaced = true;
                        }
                    }
                    else
                    {
                        entryTextTrimmeds.Add(entryTextTrimmed);
                    }
                }
            }
            if (!fieldReplaced)
            {
                entryTextTrimmeds.Add(field + "=" + value);
            }
            return string.Join(";", entryTextTrimmeds.ToArray()) + ";";
        }

        public static string RemoveField(string current, string field)
        {
            List<string> entryTextTrimmeds = new List<string>();
            if (!string.IsNullOrEmpty(current))
            {
                foreach (string entryText in current.Split(';'))
                {
                    string entryTextTrimmed = entryText.Trim();
                    if (entryTextTrimmed.Length != 0)
                    {
                        int matchIndex = entryTextTrimmed.IndexOf('=');
                        string a = ((matchIndex > 0) ? entryTextTrimmed.Substring(0, matchIndex).Trim() : entryTextTrimmed);
                        if (matchIndex <= 0 || !string.Equals(a, field, StringComparison.OrdinalIgnoreCase))
                        {
                            entryTextTrimmeds.Add(entryTextTrimmed);
                        }
                    }
                }
            }
            if (entryTextTrimmeds.Count == 0)
            {
                return "";
            }
            return string.Join(";", entryTextTrimmeds.ToArray()) + ";";
        }

        public static string ReadField(string current, string field)
        {
            if (string.IsNullOrEmpty(current))
            {
                return null;
            }
            foreach (string entryText in current.Split(';'))
            {
                string entryTextTrimmed = entryText.Trim();
                int matchIndex = entryTextTrimmed.IndexOf('=');
                if (matchIndex > 0 && string.Equals(entryTextTrimmed.Substring(0, matchIndex).Trim(), field, StringComparison.OrdinalIgnoreCase))
                {
                    return entryTextTrimmed.Substring(matchIndex + 1).Trim();
                }
            }
            return null;
        }

        public static string RestoreField(string current, string original, string field)
        {
            string field2 = ReadField(original, field);
            if (field2 != null)
            {
                return MergeField(current, field, field2);
            }
            return RemoveField(current, field);
        }
    }
}
