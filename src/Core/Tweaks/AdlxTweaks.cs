// @author bdth 2074055628@qq.com
// 文件用途 还原 v1.7 移除的 AMD 驱动调优在本机留下的残留 崩溃续还原

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static class AdlxTweaks
    {
        private const string SnapKey = "AmdSnap";
        private static readonly object lk = new object();

        public static bool Available
        {
            get { return AdlxApi.Available; }
        }

        public static bool RestoreAntiLag()
        {
            return RestoreToggle("alag", "Anti-Lag",
                delegate(IntPtr gpu, bool on) { return AdlxApi.AntiLagSet(gpu, on); });
        }

        public static bool RestoreEnhancedSync()
        {
            return RestoreToggle("esync", "Enhanced Sync",
                delegate(IntPtr gpu, bool on) { return AdlxApi.EnhancedSyncSet(gpu, on); });
        }

        public static bool RestoreChill()
        {
            lock (lk)
            {
                var snapshot = NvDrsTweaks.ParseSnapshot(Settings.LoadStr(SnapKey, ""));
                if (!HasPrefix(snapshot, ".chill")) return true;
                if (!Available) return false;
                IntPtr[] gpus = AdlxApi.GetGpus();
                if (gpus == null) return false;
                try
                {
                    bool allOk = true;
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        string key = "g" + i + ".chill";
                        string orig;
                        if (!snapshot.TryGetValue(key, out orig)) continue;
                        string[] parts = orig.Split('|');
                        int minFps, maxFps;
                        if (parts.Length != 3
                            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minFps)
                            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxFps))
                        { snapshot.Remove(key); continue; }
                        bool wantOn = parts[0] == "1";
                        bool ok = wantOn
                            ? AdlxApi.ChillSet(gpus[i], true, minFps, maxFps)
                            : AdlxApi.ChillSet(gpus[i], false, 0, 0);
                        if (ok) snapshot.Remove(key);
                        else allOk = false;
                    }
                    Settings.SaveStr(SnapKey, snapshot.Count == 0 ? "" : NvDrsTweaks.SerializeSnapshot(snapshot));
                    if (allOk) Logger.Log("AMD Chill 已还原");
                    else Logger.Log("AMD Chill 还原失败 快照保留 下次启动继续尝试");
                    return allOk;
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
        }

        public static bool RestoreRis()
        {
            lock (lk)
            {
                var snapshot = NvDrsTweaks.ParseSnapshot(Settings.LoadStr(SnapKey, ""));
                if (!HasPrefix(snapshot, ".ris")) return true;
                if (!Available) return false;
                IntPtr[] gpus = AdlxApi.GetGpus();
                if (gpus == null) return false;
                try
                {
                    bool allOk = true;
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        string key = "g" + i + ".ris";
                        string orig;
                        if (!snapshot.TryGetValue(key, out orig)) continue;
                        string[] parts = orig.Split('|');
                        int sharpness;
                        if (parts.Length != 2
                            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sharpness))
                        { snapshot.Remove(key); continue; }
                        bool ok = AdlxApi.RisSet(gpus[i], parts[0] == "1", sharpness);
                        if (ok) snapshot.Remove(key);
                        else allOk = false;
                    }
                    Settings.SaveStr(SnapKey, snapshot.Count == 0 ? "" : NvDrsTweaks.SerializeSnapshot(snapshot));
                    if (allOk) Logger.Log("AMD 锐化已还原");
                    else Logger.Log("AMD 锐化还原失败 快照保留 下次启动继续尝试");
                    return allOk;
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
        }

        private delegate bool FeatureSetter(IntPtr gpu, bool on);

        private static bool RestoreToggle(string keySuffix, string label, FeatureSetter set)
        {
            lock (lk)
            {
                var snapshot = NvDrsTweaks.ParseSnapshot(Settings.LoadStr(SnapKey, ""));
                if (!HasPrefix(snapshot, "." + keySuffix)) return true;
                if (!Available) return false;
                IntPtr[] gpus = AdlxApi.GetGpus();
                if (gpus == null) return false;
                try
                {
                    bool allOk = true;
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        string key = "g" + i + "." + keySuffix;
                        string orig;
                        if (!snapshot.TryGetValue(key, out orig)) continue;
                        if (set(gpus[i], orig == "1")) snapshot.Remove(key);
                        else allOk = false;
                    }
                    Settings.SaveStr(SnapKey, snapshot.Count == 0 ? "" : NvDrsTweaks.SerializeSnapshot(snapshot));
                    if (allOk) Logger.Log("AMD " + label + " 已还原");
                    else Logger.Log("AMD " + label + " 还原失败 快照保留 下次启动继续尝试");
                    return allOk;
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
        }

        private static bool HasPrefix(Dictionary<string, string> snapshot, string suffix)
        {
            foreach (var kv in snapshot)
                if (kv.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool HasResidue() { return Settings.LoadStr(SnapKey, "").Length > 0; }

        public static void HealFromCrash()
        {
            if (Settings.LoadStr(SnapKey, "").Length == 0) return;
            bool ok = RestoreAntiLag() & RestoreEnhancedSync() & RestoreChill() & RestoreRis();
            if (ok) Logger.Log("检测到上次未还原的 AMD 驱动设置 已恢复");
        }
    }
}
