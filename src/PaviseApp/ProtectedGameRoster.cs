using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class ProtectedGameRoster
    {
        private const string Key = "AcProtectedGames";

        private static readonly object lk = new object();

        private static Dictionary<string, string> cache;

        private static string Norm(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName))
            {
                return null;
            }
            string rendererNameSnapshot = rendererName;
            int lastIndexOfAnyResult = rendererNameSnapshot.LastIndexOfAny(new char[2] { '\\', '/' });
            if (lastIndexOfAnyResult >= 0)
            {
                rendererNameSnapshot = rendererNameSnapshot.Substring(lastIndexOfAnyResult + 1);
            }
            if (!rendererNameSnapshot.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                rendererNameSnapshot += ".exe";
            }
            rendererNameSnapshot = rendererNameSnapshot.Trim();
            if (rendererNameSnapshot.Length != 0 && rendererNameSnapshot.IndexOf(';') < 0 && rendererNameSnapshot.IndexOf('|') < 0)
            {
                return rendererNameSnapshot.ToLowerInvariant();
            }
            return null;
        }

        private static Dictionary<string, string> Load()
        {
            if (cache != null)
            {
                return cache;
            }
            Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] loadStrParts = Settings.LoadStr("AcProtectedGames", "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string entryText in loadStrParts)
            {
                int matchIndex = entryText.LastIndexOf('|');
                if (matchIndex > 0 && matchIndex < entryText.Length - 1)
                {
                    dictionary[entryText.Substring(0, matchIndex)] = entryText.Substring(matchIndex + 1);
                }
            }
            cache = dictionary;
            return cache;
        }

        private static void Persist()
        {
            List<string> list = new List<string>();
            foreach (KeyValuePair<string, string> item in cache)
            {
                list.Add(item.Key + "|" + item.Value);
            }
            Settings.SaveStr("AcProtectedGames", string.Join(";", list.ToArray()));
        }

        private static bool Current(string version)
        {
            // 写入拒绝的观测按版本划分，不作为数据迁移标记。
            return string.Equals(version, App.Version, StringComparison.OrdinalIgnoreCase);
        }

        public static bool Contains(string rendererName)
        {
            string normResult = Norm(rendererName);
            if (normResult == null)
            {
                return false;
            }
            lock (lk)
            {
                string value;
                return Load().TryGetValue(normResult, out value) && Current(value);
            }
        }

        public static void Remember(string rendererName)
        {
            string normResult = Norm(rendererName);
            if (normResult == null)
            {
                return;
            }
            lock (lk)
            {
                Dictionary<string, string> dictionary = Load();
                string value;
                if (dictionary.TryGetValue(normResult, out value) && Current(value))
                {
                    return;
                }
                dictionary[normResult] = App.Version;
                Persist();
            }
            Logger.Log(Lang.T("log.protectedgameroster.1", 0) + normResult + Lang.T("log.protectedgameroster.2", 0));
        }

        public static bool Forget(string rendererName)
        {
            string normResult = Norm(rendererName);
            if (normResult == null)
            {
                return false;
            }
            lock (lk)
            {
                if (!Load().Remove(normResult))
                {
                    return false;
                }
                Persist();
            }
            Logger.Log(Lang.T("log.protectedgameroster.3", 0) + normResult);
            return true;
        }

        public static int Clear()
        {
            int count;
            lock (lk)
            {
                count = Load().Count;
                if (count == 0)
                {
                    return 0;
                }
                cache.Clear();
                Persist();
            }
            Logger.Log(Lang.T("log.protectedgameroster.4", 0) + count);
            return count;
        }

        public static string[] Names()
        {
            lock (lk)
            {
                List<string> list = new List<string>();
                foreach (KeyValuePair<string, string> item in Load())
                {
                    if (Current(item.Value))
                    {
                        list.Add(item.Key);
                    }
                }
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list.ToArray();
            }
        }
    }
}
