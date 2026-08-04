// @author bdth 2074055628@qq.com
// 文件用途 为 LoL 无头模式记录带对局身份的跨进程恢复租约

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class LolHeadlessLeaseInfo
    {
        public string Root;
        public int GamePid;
        public long GameCreation;
        public bool Legacy;
    }

    internal static class LolHeadlessLease
    {
        private const string Key = "LolHeadlessEngagedRoot";
        private const string Prefix = "V2|";
        private const string GlobalMutex = "Global\\Pavise_LolHeadlessLease";
        private const string LocalMutex = "Pavise_LolHeadlessLease";

        public static bool Exists()
        {
            LolHeadlessLeaseInfo info;
            return TryRead(out info);
        }

        public static bool MatchesRoot(string root)
        {
            LolHeadlessLeaseInfo info;
            return TryRead(out info) && SameRoot(info.Root, root);
        }

        public static bool MatchesLegacyRoot(string root)
        {
            LolHeadlessLeaseInfo info;
            return TryRead(out info) && info.Legacy
                && SameRoot(info.Root, root);
        }

        public static bool Matches(string root, int pid, long creation)
        {
            LolHeadlessLeaseInfo info;
            if (!TryRead(out info) || !SameRoot(info.Root, root)) return false;
            return !info.Legacy && pid > 0 && creation > 0
                && info.GamePid == pid && info.GameCreation == creation;
        }

        public static bool Set(string root, int pid, long creation)
        {
            string value;
            if (!TrySerialize(root, pid, creation, out value)) return false;
            return WithLeaseLock(delegate
            {
                Settings.SaveStr(Key, value);
                return string.Equals(
                    Settings.LoadStr(Key, ""), value, StringComparison.Ordinal);
            });
        }

        public static void Clear()
        {
            WithLeaseLock(delegate
            {
                if (!Settings.SaveStr(Key, "")) return false;
                return string.IsNullOrEmpty(Settings.LoadStr(Key, ""));
            });
        }

        public static bool ClearIfMatches(string root, int pid, long creation)
        {
            return WithLeaseLock(delegate
            {
                LolHeadlessLeaseInfo info;
                if (!TryRead(out info) || info.Legacy
                    || !SameRoot(info.Root, root)
                    || info.GamePid != pid || info.GameCreation != creation)
                    return false;
                if (!Settings.SaveStr(Key, "")) return false;
                return string.IsNullOrEmpty(Settings.LoadStr(Key, ""));
            });
        }

        public static bool ClearLegacyIfMatches(string root)
        {
            return WithLeaseLock(delegate
            {
                LolHeadlessLeaseInfo info;
                if (!TryRead(out info) || !info.Legacy
                    || !SameRoot(info.Root, root))
                    return false;
                if (!Settings.SaveStr(Key, "")) return false;
                return string.IsNullOrEmpty(Settings.LoadStr(Key, ""));
            });
        }

        public static bool TryRead(out LolHeadlessLeaseInfo info)
        {
            return TryParse(Settings.LoadStr(Key, ""), out info);
        }

        internal static bool TrySerialize(
            string root, int pid, long creation, out string value)
        {
            value = null;
            string normalized = NormalizeRoot(root);
            if (normalized == null || pid <= 0 || creation <= 0) return false;
            value = Prefix + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(normalized)) + "|" + pid + "|" + creation;
            return true;
        }

        internal static bool TryParse(string value, out LolHeadlessLeaseInfo info)
        {
            info = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string text = value.Trim();
            if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            {
                string legacyRoot = NormalizeRoot(text);
                if (legacyRoot == null) return false;
                info = new LolHeadlessLeaseInfo
                {
                    Root = legacyRoot,
                    Legacy = true
                };
                return true;
            }
            string[] parts = text.Split('|');
            int pid;
            long creation;
            if (parts.Length != 4 || !int.TryParse(parts[2], out pid) || pid <= 0
                || !long.TryParse(parts[3], out creation) || creation <= 0)
                return false;
            string root;
            try
            {
                root = Encoding.UTF8.GetString(
                    Convert.FromBase64String(parts[1]));
            }
            catch { return false; }
            root = NormalizeRoot(root);
            if (root == null) return false;
            info = new LolHeadlessLeaseInfo
            {
                Root = root,
                GamePid = pid,
                GameCreation = creation,
                Legacy = false
            };
            return true;
        }

        internal static string StableHash(string value)
        {
            uint hash = 2166136261;
            string normalized = (value ?? "").ToUpperInvariant();
            unchecked
            {
                for (int i = 0; i < normalized.Length; i++)
                {
                    hash ^= normalized[i];
                    hash *= 16777619;
                }
            }
            return hash.ToString("X8");
        }

        private static bool WithLeaseLock(Func<bool> action)
        {
            Mutex mutex = null;
            bool held = false;
            try
            {
                try { mutex = new Mutex(false, GlobalMutex); }
                catch { mutex = new Mutex(false, LocalMutex); }
                try { held = mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return false;
                return action != null && action();
            }
            catch { return false; }
            finally
            {
                if (held && mutex != null)
                    try { mutex.ReleaseMutex(); } catch { }
                if (mutex != null) try { mutex.Close(); } catch { }
            }
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                string full = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
                return full.Length > 2 ? full : null;
            }
            catch { return null; }
        }

        private static bool SameRoot(string left, string right)
        {
            string a = NormalizeRoot(left);
            string b = NormalizeRoot(right);
            return a != null && b != null
                && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
