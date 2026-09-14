// @author bdth 2074055628@qq.com
// File purpose Records self-protected processes that refuse all policy writes; later matches skip them by roster
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class SelfProtectedRoster
    {
        private const string Key = "SelfProtectedProcs";
        private const int MaxNames = 64;
        private static readonly object sync = new object();
        private static HashSet<string> names;
        private static List<string> order;

        private static void EnsureLoadedLocked()
        {
            if (names != null) return;
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            order = new List<string>();
            foreach (string s in Settings.LoadStr(Key, "").Split(';'))
            {
                string n = s.Trim();
                if (n.Length > 0 && names.Add(n)) order.Add(n);
            }
        }

        public static bool Contains(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (sync)
            {
                EnsureLoadedLocked();
                return names.Contains(name);
            }
        }

        public static bool Mark(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf(';') >= 0) return false;
            lock (sync)
            {
                EnsureLoadedLocked();
                if (!names.Add(name)) return false;
                order.Add(name);
                while (order.Count > MaxNames)
                {
                    names.Remove(order[0]);
                    order.RemoveAt(0);
                }
                SaveLocked();
                return true;
            }
        }

        public static int Clear()
        {
            lock (sync)
            {
                EnsureLoadedLocked();
                int n = names.Count;
                names.Clear();
                order.Clear();
                SaveLocked();
                return n;
            }
        }

        public static string Describe()
        {
            lock (sync)
            {
                EnsureLoadedLocked();
                return string.Join(", ", order.ToArray());
            }
        }

        private static void SaveLocked()
        {
            Settings.SaveStr(Key, string.Join(";", order.ToArray()));
        }
    }
}
