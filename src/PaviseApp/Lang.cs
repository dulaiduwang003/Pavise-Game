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
            Cur = ((Settings.LoadStr("Lang", "zh") == "en") ? 1 : 0);
        }

        public static void Set(int i)
        {
            lock (ChangeGate)
            {
                Cur = ((i == 1) ? 1 : 0);
                Settings.SaveStr("Lang", (Cur == 1) ? "en" : "zh");
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
                return value[(language >= 0 && language < value.Length) ? language : 0];
            }
            return key;
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
