using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    // 查找额度属于真实客户端身份，父进程和恢复辅助进程共用；设置刷新及服务对象重建不能补充次数。
    internal static class LolCredentialLookupBudget
    {
        internal const string Key = "LolCredentialLookupBudgetV1";
        internal const int MaximumAttempts = 32;
        internal const int MaximumClients = 128;
        private const int MaximumCharacters = 1048576;
        private static readonly object failureGate = new object();
        private static readonly Dictionary<string, Client> storageFailures = new Dictionary<string, Client>(StringComparer.Ordinal);
        private static readonly object observationGate = new object();
        private static readonly Dictionary<string, Client> observedClients = new Dictionary<string, Client>(StringComparer.Ordinal);
#if PAVISE_SELFTEST
        private const string MutexName = "Local\\Pavise_LolCredentialLookupBudgetFixtureV1";
#else
        private const string MutexName = "Global\\Pavise_LolCredentialLookupBudgetV1";
#endif

        private sealed class Client
        {
            internal string Root, Path;
            internal int Session, Pid, Used;
            internal long Birth;
            internal bool Reported;
            internal string Identity
            {
                get { return Root.ToUpperInvariant() + "|" + Session.ToString(CultureInfo.InvariantCulture) + "|" + Pid.ToString(CultureInfo.InvariantCulture)
                    + "|" + Birth.ToString(CultureInfo.InvariantCulture) + "|" + Path.ToUpperInvariant(); }
            }
        }

#if PAVISE_SELFTEST
        internal static void ResetStorageFailuresForTest()
        { lock (failureGate) storageFailures.Clear(); lock (observationGate) observedClients.Clear(); }
#endif

        internal static bool TryReserve(string root)
        {
            Client current = CaptureClient(root);
            if (current == null) return false;
            lock (failureGate)
            {
                Client failure;
                if (storageFailures.TryGetValue(current.Identity, out failure) && failure.Used >= 3) return false;
                if (failure == null && storageFailures.Count >= MaximumClients)
                {
                    PruneExited(storageFailures);
                    if (storageFailures.Count >= MaximumClients) return false;
                }
            }
            bool held = false;
            try
            {
                using (var mutex = new Mutex(false, MutexName))
                {
                    try { held = mutex.WaitOne(1000); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("lookup budget owner is busy");
                    try
                    {
                        string raw;
                        if (!Settings.TryLoadStr(Key, out raw)) throw new IOException("lookup budget is unreadable");
                        Dictionary<string, Client> entries = Parse(raw);
                        Client entry;
                        if (!entries.TryGetValue(current.Identity, out entry))
                        {
                            if (entries.Count >= MaximumClients) PruneExited(entries);
                            if (entries.Count >= MaximumClients) return false;
                            entries.Add(current.Identity, entry = current);
                        }
                        if (entry.Used >= MaximumAttempts)
                        {
                            if (!entry.Reported)
                            {
                                entry.Reported = true;
                                Persist(entries);
                                Logger.Warn("LOL 凭据查找已用完本客户端阶段的32次额度；进程编号=" + entry.Pid
                                    + "；创建时间=" + entry.Birth + "；停止新增查找，保留已有会话及恢复记录");
                            }
                            return false;
                        }
                        // 先耐久记录再进入 WMI、文件及网络回退；中途异常不能退还这次查找。
                        entry.Used++;
                        Persist(entries);
                        return true;
                    }
                    finally { mutex.ReleaseMutex(); held = false; }
                }
            }
            catch (Exception error)
            {
                lock (failureGate)
                {
                    Client failure;
                    if (storageFailures.TryGetValue(current.Identity, out failure) || storageFailures.Count < MaximumClients)
                        storageFailures[current.Identity] = new Client { Root = current.Root, Path = current.Path, Session = current.Session,
                            Pid = current.Pid, Birth = current.Birth, Used = Math.Min(3, (failure == null ? 0 : failure.Used) + 1) };
                }
                Logger.RepeatedError("LolCredentialLookupBudget", "LOL 凭据查找额度无法核验，暂停新增查找并保留已有会话：" + error.GetType().Name);
                return false;
            }
        }

        private static string Canonical(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                string path = System.IO.Path.GetFullPath(value.Trim().Trim('"')).TrimEnd('\\');
                return path.Length > 2 && path.Length <= 32768 ? path : null;
            }
            catch { return null; }
        }

        private static Client CaptureClient(string root)
        {
            root = Canonical(root);
            int session;
            if (root == null || !LolInstallDiscovery.IsValidLolRoot(root) || !LolRuntimeProcesses.TryGetCurrentSessionId(out session)) return null;
            lock (observationGate)
            {
                // 身份来自既有扫描的同一句柄，不为额度门新增全进程枚举。使用前只复核选中的原 PID。
                int inspected = 0;
                foreach (string name in new[] { "LeagueClient.exe", "LeagueClientUx.exe" })
                {
                    while (inspected < MaximumClients)
                    {
                        Client newest = null;
                        foreach (Client client in observedClients.Values)
                            if (client.Session == session && LolRuntimeProcesses.IsUnder(client.Path, root)
                                && string.Equals(System.IO.Path.GetFileName(client.Path), name, StringComparison.OrdinalIgnoreCase)
                                && (newest == null || client.Birth > newest.Birth || client.Birth == newest.Birth && client.Pid > newest.Pid)) newest = client;
                        if (newest == null) break;
                        inspected++;
                        IntPtr handle = Native.OpenProcess(0x101000, false, newest.Pid);
                        if (handle == IntPtr.Zero)
                        {
                            if (Native.LastOpenProcessFailureWasNoSuchProcess()) { observedClients.Remove(newest.Identity); continue; }
                            return null;
                        }
                        try
                        {
                            int state = Native.QueryProcessIdentityState(handle, newest.Birth);
                            if (state == 1) { observedClients.Remove(newest.Identity); continue; }
                            int actualSession;
                            string path = Canonical(Native.ImagePath(handle));
                            if (state != 0 || !Native.TryGetLiveProcessSessionId(handle, newest.Pid, out actualSession)
                                || actualSession != session || !string.Equals(path, newest.Path, StringComparison.OrdinalIgnoreCase)) return null;
                            return new Client { Root = root, Path = newest.Path, Pid = newest.Pid, Birth = newest.Birth, Session = session };
                        }
                        finally { Native.CloseHandle(handle); }
                    }
                }
            }
            return null;
        }

        // 仅接收既有进程扫描已用同一句柄核验的路径、会话及创建时间；这里不建立查找阶段。
        internal static void ObserveClient(int session, int pid, long birth, string path)
        {
            path = Canonical(path);
            if (session < 0 || pid <= 0 || birth <= 0 || path == null
                || !LolRuntimeProcesses.IsCredentialSourceName(System.IO.Path.GetFileName(path))) return;
            var client = new Client { Root = System.IO.Path.GetDirectoryName(path), Path = path, Session = session, Pid = pid, Birth = birth };
            lock (observationGate)
            {
                if (!observedClients.ContainsKey(client.Identity) && observedClients.Count >= MaximumClients)
                {
                    PruneExited(observedClients);
                    if (observedClients.Count >= MaximumClients) return;
                }
                observedClients[client.Identity] = client;
            }
        }

        private static Dictionary<string, Client> Parse(string raw)
        {
            var entries = new Dictionary<string, Client>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(raw)) return entries;
            if (raw.Length > MaximumCharacters) throw new InvalidDataException("lookup budget exceeds capacity");
            string[] lines = raw.Split('\n');
            if (lines.Length == 0 || lines[0] != "1" || lines.Length > MaximumClients + 1) throw new InvalidDataException("lookup budget format is invalid");
            for (int index = 1; index < lines.Length; index++)
            {
                string[] fields = lines[index].Split('|');
                int session, pid, used, reported; long birth;
                if (fields.Length != 7 || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out session)
                    || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
                    || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out birth)
                    || !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out used)
                    || !int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out reported)
                    || session < 0 || pid <= 0 || birth <= 0 || used < 1 || used > MaximumAttempts || reported < 0 || reported > 1)
                    throw new InvalidDataException("lookup budget entry is invalid");
                string root = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0]));
                string path = Encoding.UTF8.GetString(Convert.FromBase64String(fields[4]));
                if (Canonical(root) != root || Canonical(path) != path || !LolRuntimeProcesses.IsUnder(path, root)
                    || !LolRuntimeProcesses.IsCredentialSourceName(System.IO.Path.GetFileName(path))) throw new InvalidDataException("lookup budget identity is invalid");
                var entry = new Client { Root = root, Path = path, Session = session, Pid = pid, Birth = birth, Used = used, Reported = reported != 0 };
                if (entries.ContainsKey(entry.Identity)) throw new InvalidDataException("lookup budget identity is duplicated");
                entries.Add(entry.Identity, entry);
            }
            return entries;
        }

        private static void Persist(Dictionary<string, Client> entries)
        {
            var text = new StringBuilder("1");
            foreach (Client entry in entries.Values)
                text.Append('\n').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Root))).Append('|')
                    .Append(entry.Session.ToString(CultureInfo.InvariantCulture)).Append('|').Append(entry.Pid.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(entry.Birth.ToString(CultureInfo.InvariantCulture)).Append('|').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Path))).Append('|')
                    .Append(entry.Used.ToString(CultureInfo.InvariantCulture)).Append('|').Append(entry.Reported ? "1" : "0");
            string raw = text.ToString(), check;
            if (raw.Length > MaximumCharacters || !Settings.SaveStrDurable(Key, raw) || !Settings.TryLoadStr(Key, out check) || check != raw)
                throw new IOException("lookup budget persistence was not confirmed");
        }

        private static void PruneExited(Dictionary<string, Client> entries)
        {
            var gone = new List<string>();
            foreach (KeyValuePair<string, Client> entry in entries)
            {
                IntPtr handle = Native.OpenProcess(0x101000, false, entry.Value.Pid);
                if (handle == IntPtr.Zero)
                {
                    if (Native.LastOpenProcessFailureWasNoSuchProcess()) gone.Add(entry.Key);
                    continue;
                }
                try { if (Native.QueryProcessIdentityState(handle, entry.Value.Birth) == 1) gone.Add(entry.Key); }
                finally { Native.CloseHandle(handle); }
            }
            foreach (string identity in gone) entries.Remove(identity);
        }
    }
}
