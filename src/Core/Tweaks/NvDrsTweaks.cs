// @author bdth 2074055628@qq.com
// 文件用途 按游戏写入 NVIDIA 驱动 Profile 设置 快照先行 可按项恢复

using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class NvDrsTweaks
    {
        private const string ListKey = "NvDrsList";
        private const string SnapPrefix = "NvDrs_";
        public const string KeyPState = "pstate";
        public const string KeyFrl = "frl";
        public const string KeyPreRender = "prerender";
        public const string KeyLowLatCpl = "lowlatcpl";

        private static readonly object sync = new object();

        internal static uint SettingIdOf(string key)
        {
            if (key == KeyPState) return NvApi.SettingPreferredPState;
            if (key == KeyPreRender) return NvApi.SettingPreRenderLimit;
            if (key == KeyLowLatCpl) return NvApi.SettingLowLatencyCpl;
            return NvApi.SettingFrlFps;
        }

        public static void ApplyForGame(string exePath, bool maxPerf, int frlFps, bool lowLatency)
        {
            if (string.IsNullOrEmpty(exePath) || !maxPerf && frlFps <= 0 && !lowLatency) return;
            if (!NvApi.Available) return;
            string exeName = Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(exeName)) return;
            var desired = new List<KeyValuePair<string, uint>>();
            if (maxPerf) desired.Add(new KeyValuePair<string, uint>(KeyPState, NvApi.PStatePreferMax));
            if (frlFps > 0) desired.Add(new KeyValuePair<string, uint>(KeyFrl, (uint)frlFps));
            if (lowLatency)
            {
                desired.Add(new KeyValuePair<string, uint>(KeyPreRender, 1u));
                desired.Add(new KeyValuePair<string, uint>(KeyLowLatCpl, 1u));
            }
            lock (sync)
            {
                IntPtr session;
                if (!NvApi.TryOpenSession(out session)) return;
                try
                {
                    IntPtr profile;
                    if (!NvApi.FindOrCreateAppProfile(session, exeName, out profile)) return;
                    var snapshot = ParseSnapshot(Settings.LoadStr(SnapPrefix + exeName, ""));
                    bool snapshotDirty = false;
                    foreach (var item in desired)
                    {
                        if (!snapshot.ContainsKey(item.Key))
                        {
                            uint orig;
                            int found = NvApi.TryGetDword(session, profile, SettingIdOf(item.Key), out orig);
                            if (found < 0) continue;
                            snapshot[item.Key] = found == 1 ? orig.ToString() : "absent";
                            snapshotDirty = true;
                        }
                    }
                    if (snapshotDirty)
                    {
                        if (!Settings.SaveStr(SnapPrefix + exeName, SerializeSnapshot(snapshot))
                            || !AddToList(exeName))
                        {
                            Logger.Log("NVIDIA 驱动调优：快照无法持久化，已跳过 " + exeName);
                            return;
                        }
                    }
                    bool wrote = false;
                    var failed = new List<string>();
                    foreach (var item in desired)
                    {
                        uint current;
                        if (NvApi.TryGetDword(session, profile, SettingIdOf(item.Key), out current) == 1
                            && current == item.Value) continue;
                        if (NvApi.SetDword(session, profile, SettingIdOf(item.Key), item.Value)) wrote = true;
                        else
                        {
                            failed.Add(item.Key);
                            Logger.Log("NVIDIA 驱动调优：写入 " + item.Key + " 失败 (" + exeName + ")");
                        }
                    }
                    if (wrote && NvApi.SaveSession(session))
                    {
                        string done = (maxPerf && !failed.Contains(KeyPState) ? " 电源最高性能" : "")
                            + (frlFps > 0 && !failed.Contains(KeyFrl) ? " 帧上限" + frlFps : "")
                            + (lowLatency && !failed.Contains(KeyPreRender) ? " 低延迟(预渲染1)" : "");
                        if (done.Length > 0) Logger.Log("NVIDIA 驱动调优：" + exeName + done);
                    }
                }
                finally { NvApi.CloseSession(session); }
            }
        }

        public static void RestoreKind(string key)
        {
            if (!NvApi.Available) return;
            lock (sync)
            {
                string[] games = Settings.LoadStr(ListKey, "")
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string exeName in games)
                {
                    var snapshot = ParseSnapshot(Settings.LoadStr(SnapPrefix + exeName, ""));
                    string orig;
                    if (!snapshot.TryGetValue(key, out orig)) continue;
                    IntPtr session;
                    if (!NvApi.TryOpenSession(out session)) return;
                    try
                    {
                        IntPtr profile;
                        if (NvApi.FindOrCreateAppProfile(session, exeName, out profile))
                        {
                            bool ok = orig == "absent"
                                ? NvApi.DeleteSetting(session, profile, SettingIdOf(key))
                                : NvApi.SetDword(session, profile, SettingIdOf(key), ParseUInt(orig));
                            if (ok && NvApi.SaveSession(session))
                            {
                                snapshot.Remove(key);
                                if (snapshot.Count == 0)
                                {
                                    Settings.SaveStr(SnapPrefix + exeName, "");
                                    RemoveFromList(exeName);
                                }
                                else Settings.SaveStr(SnapPrefix + exeName, SerializeSnapshot(snapshot));
                                Logger.Log("NVIDIA 驱动调优：已恢复 " + exeName + " 的 " + key);
                            }
                            else Logger.Log("NVIDIA 驱动调优：恢复 " + exeName + " 的 " + key + " 失败，快照保留");
                        }
                    }
                    finally { NvApi.CloseSession(session); }
                }
            }
        }

        private static uint ParseUInt(string value)
        {
            uint parsed;
            return uint.TryParse(value, out parsed) ? parsed : 0;
        }

        internal static Dictionary<string, string> ParseSnapshot(string raw)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(raw)) return map;
            foreach (string pair in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0) map[pair.Substring(0, eq)] = pair.Substring(eq + 1);
            }
            return map;
        }

        internal static string SerializeSnapshot(Dictionary<string, string> map)
        {
            var parts = new List<string>();
            foreach (var kv in map) parts.Add(kv.Key + "=" + kv.Value);
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(";", parts.ToArray());
        }

        private static bool AddToList(string exeName)
        {
            var games = new List<string>(Settings.LoadStr(ListKey, "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (string existing in games)
                if (string.Equals(existing, exeName, StringComparison.OrdinalIgnoreCase)) return true;
            games.Add(exeName);
            return Settings.SaveStr(ListKey, string.Join(";", games.ToArray()));
        }

        private static void RemoveFromList(string exeName)
        {
            var games = new List<string>(Settings.LoadStr(ListKey, "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            games.RemoveAll(delegate(string g)
            {
                return string.Equals(g, exeName, StringComparison.OrdinalIgnoreCase);
            });
            Settings.SaveStr(ListKey, string.Join(";", games.ToArray()));
        }
    }
}
