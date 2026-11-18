using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class Lang
    {
        public static int Cur;

        internal static readonly object ChangeGate = new object();

        private static readonly Dictionary<string, string[]> M = CreateMessages();

        private static Dictionary<string, string[]> CreateMessages()
        {
            var messages = new Dictionary<string, string[]>();
            AddCommonMessages(messages);
            AddSettingsMessages(messages);
            AddLibraryMessages(messages);
            AddGuardMessages(messages);
            AddSchedulingMessages(messages);
            AddPowerMessages(messages);
            AddCheckupMessages(messages);
            AddPerformanceMessages(messages);
            AddLogsMessages(messages);
            return messages;
        }

        public static void Init()
        {
            string saved = Settings.LoadStr("Lang", "zh");
            Cur = saved == "pt-BR" ? 3 : saved == "en" ? 1 : 0;
        }

        public static void Set(int i)
        {
            lock (ChangeGate)
            {
                Cur = i == 3 ? 3 : i == 1 ? 1 : 0;
                Settings.SaveStr("Lang", Cur == 3 ? "pt-BR" : Cur == 1 ? "en" : "zh");
            }
        }

        public static string T(string key)
        { return T(key, Cur); }

        public static string T(string key, int language)
        {
            if (key != null && (key.StartsWith("log.", System.StringComparison.Ordinal) || key.IndexOf(".log.", System.StringComparison.Ordinal) >= 0)) language = 0;
            string[] value;
            if (M.TryGetValue(key, out value))
            {
                if (language == 3)
                {
                    string translated;
                    if (PortugueseMessages.TryGetValue(key, out translated)) return translated;
                    return value.Length > 1 ? value[1] : value[0];
                }
                return value[(language >= 0 && language < value.Length) ? language : 0];
            }
            return key;
        }

        // UI labels in the log viewer are localized without changing canonical log records.
        internal static string Ui(string key)
        {
            string translated;
            return Cur == 3 && key != null && PortugueseMessages.TryGetValue(key, out translated) ? translated : T(key);
        }

        // Prompt fragments originate in the Chinese-only hardware and feature catalog.
        internal static string TranslateChinese(string value)
        {
            string translated;
            return Cur == 3 && value != null && PortuguesePromptMessages.TryGetValue(value, out translated) ? translated : value;
        }

        internal static string TranslateEnglish(string english)
        {
            string translated;
            return english != null && PortugueseDirectMessages.TryGetValue(english, out translated) ? translated : english;
        }

        public static string F(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }

        public static bool HasKey(string key)
        {
            if (key != null)
            {
                return M.ContainsKey(key);
            }
            return false;
        }

        public static void Merge(IDictionary<string, string[]> extra)
        {
            if (extra == null)
            {
                return;
            }
            foreach (KeyValuePair<string, string[]> item in extra)
            {
                if (item.Key != null && item.Value != null && item.Value.Length > 0)
                {
                    M[item.Key] = item.Value;
                }
            }
        }
    }
}
