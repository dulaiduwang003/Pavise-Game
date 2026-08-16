// @author bdth 2074055628@qq.com
// 文件用途 后台硬限帧已于 1.7.1 移除 这里只保留还原能力

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static class NvGlobalTweaks
    {
        private const string SnapKey = "NvBaseSnap";
        private const string BgKeyPrefix = "bgfrl@";

        private static readonly object lk = new object();

        public static bool HasResidue()
        {
            return Settings.LoadStr(SnapKey, "").Length > 0;
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
                    if (allOk) Logger.Log(Lang.T("log.nvglobaltweaks.1"));
                    else Logger.Log(Lang.T("log.nvglobaltweaks.2"));
                    return allOk;
                }
                finally { NvApi.CloseSession(session); }
            }
        }
    }
}
