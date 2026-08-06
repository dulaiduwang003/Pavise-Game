// @author bdth 2074055628@qq.com
// 文件用途 会话期间在 NVIDIA 基础 Profile 写入全局项 设置 ID 按名称运行时发现 快照先行 崩溃续还原

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static class NvGlobalTweaks
    {
        private const string SnapKey = "NvBaseSnap";
        public const int BackgroundFpsCap = 20;
        private const string BgKeyPrefix = "bgfrl@";

        private static readonly object lk = new object();
        private static int bgState;
        private static uint bgId;
        private static string bgName;

        public static bool TryResolveBgSetting(out uint id, out string name)
        {
            lock (lk)
            {
                if (bgState != 0) { id = bgId; name = bgName; return bgState > 0; }
                id = 0; name = null;
                if (!NvApi.Available) { bgState = -1; return false; }
                string nm;
                if (NvApi.TryGetSettingName(NvApi.SettingFrlFpsBackground, out nm) && NameMatches(nm))
                {
                    bgState = 1; bgId = NvApi.SettingFrlFpsBackground; bgName = nm;
                    id = bgId; name = nm;
                    return true;
                }
                uint[] ids = NvApi.EnumAvailableSettingIds();
                if (ids != null)
                {
                    foreach (uint candidate in ids)
                    {
                        if (!NvApi.TryGetSettingName(candidate, out nm) || !NameMatches(nm)) continue;
                        bgState = 1; bgId = candidate; bgName = nm;
                        id = bgId; name = nm;
                        Logger.Log("后台硬限帧：在驱动设置数据库中发现「" + nm + "」(0x"
                            + candidate.ToString("X8") + ")，按此下发");
                        return true;
                    }
                }
                bgState = -1;
                Logger.Log("后台硬限帧：当前驱动的设置数据库里没有后台帧率上限项，该功能停用");
                return false;
            }
        }

        internal static bool NameMatches(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            bool scope = lower.IndexOf("background", StringComparison.Ordinal) >= 0
                || lower.IndexOf("idle application", StringComparison.Ordinal) >= 0;
            if (!scope) return false;
            return lower.IndexOf("frame rate", StringComparison.Ordinal) >= 0
                || lower.IndexOf("fps", StringComparison.Ordinal) >= 0;
        }

        public static bool Activate()
        {
            uint settingId;
            string settingName;
            if (!TryResolveBgSetting(out settingId, out settingName)) return false;
            lock (lk)
            {
                IntPtr session;
                if (!NvApi.TryOpenSession(out session)) return false;
                try
                {
                    IntPtr profile;
                    if (!NvApi.TryGetBaseProfile(session, out profile)) return false;
                    string key = BgKeyPrefix + settingId.ToString("X8");
                    var snapshot = NvDrsTweaks.ParseSnapshot(Settings.LoadStr(SnapKey, ""));
                    if (!snapshot.ContainsKey(key))
                    {
                        uint orig;
                        int found = NvApi.TryGetDword(session, profile, settingId, out orig);
                        if (found < 0) return false;
                        snapshot[key] = found == 1 ? orig.ToString() : "absent";
                        if (!Settings.SaveStr(SnapKey, NvDrsTweaks.SerializeSnapshot(snapshot)))
                        {
                            Logger.Log("后台硬限帧：快照无法持久化，本轮未启用");
                            return false;
                        }
                    }
                    uint current;
                    if (NvApi.TryGetDword(session, profile, settingId, out current) == 1
                        && current == (uint)BackgroundFpsCap) return true;
                    int status;
                    if (!NvApi.SetDword(session, profile, settingId, (uint)BackgroundFpsCap, out status)
                        || !NvApi.SaveSession(session))
                    {
                        Logger.Log("后台硬限帧：写入失败 (NVAPI 状态 " + status + ")");
                        return false;
                    }
                    uint after;
                    if (NvApi.TryGetDword(session, profile, settingId, out after) != 1
                        || after != (uint)BackgroundFpsCap)
                    {
                        Logger.Log("后台硬限帧：写入报成功但回读不符，不算生效");
                        return false;
                    }
                    Logger.Log("后台硬限帧：非前台程序帧率上限 " + BackgroundFpsCap
                        + " fps（驱动级「" + settingName + "」，退出恢复）");
                    return true;
                }
                finally { NvApi.CloseSession(session); }
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                var snapshot = NvDrsTweaks.ParseSnapshot(Settings.LoadStr(SnapKey, ""));
                var pending = new List<string>();
                foreach (var kv in snapshot)
                    if (kv.Key.StartsWith(BgKeyPrefix, StringComparison.OrdinalIgnoreCase)) pending.Add(kv.Key);
                if (pending.Count == 0) return true;
                if (!NvApi.Available) return false;
                IntPtr session;
                if (!NvApi.TryOpenSession(out session)) return false;
                try
                {
                    IntPtr profile;
                    if (!NvApi.TryGetBaseProfile(session, out profile)) return false;
                    bool allOk = true;
                    foreach (string key in pending)
                    {
                        uint settingId;
                        if (!uint.TryParse(key.Substring(BgKeyPrefix.Length),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out settingId))
                        { snapshot.Remove(key); continue; }
                        string orig = snapshot[key];
                        uint parsed;
                        bool ok = orig == "absent"
                            ? NvApi.DeleteSetting(session, profile, settingId)
                            : uint.TryParse(orig, out parsed) && NvApi.SetDword(session, profile, settingId, parsed);
                        if (ok) snapshot.Remove(key);
                        else allOk = false;
                    }
                    if (!NvApi.SaveSession(session)) allOk = false;
                    Settings.SaveStr(SnapKey, snapshot.Count == 0 ? "" : NvDrsTweaks.SerializeSnapshot(snapshot));
                    if (allOk) Logger.Log("后台硬限帧已还原");
                    else Logger.Log("后台硬限帧还原失败，快照保留，下次启动继续尝试");
                    return allOk;
                }
                finally { NvApi.CloseSession(session); }
            }
        }

        public static void HealFromCrash()
        {
            if (Settings.LoadStr(SnapKey, "").Length == 0) return;
            if (Restore()) Logger.Log("检测到上次未还原的后台硬限帧，已恢复");
        }
    }
}
