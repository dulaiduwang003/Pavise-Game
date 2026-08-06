// @author bdth 2074055628@qq.com
// 文件用途 会话期间应用 AMD 全局 3D 设置 快照先行 回读核验 退出恢复 崩溃续还原 实验性

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static class AdlxTweaks
    {
        private const string SnapKey = "AmdSnap";
        public const int RisSharpness = 80;
        private static readonly object lk = new object();

        public static bool Available
        {
            get { return AdlxApi.Available; }
        }

        public static bool ActivateAntiLag()
        {
            return ApplyToggle("alag", "Anti-Lag",
                delegate(IntPtr gpu, out bool supported, out bool enabled)
                { return AdlxApi.AntiLagGet(gpu, out supported, out enabled); },
                delegate(IntPtr gpu) { return AdlxApi.AntiLagSet(gpu, true); },
                true);
        }

        public static bool RestoreAntiLag()
        {
            return RestoreToggle("alag", "Anti-Lag",
                delegate(IntPtr gpu, bool on) { return AdlxApi.AntiLagSet(gpu, on); },
                delegate(IntPtr gpu, out bool supported, out bool enabled)
                { return AdlxApi.AntiLagGet(gpu, out supported, out enabled); });
        }

        public static bool ActivateEnhancedSync()
        {
            return ApplyToggle("esync", "Enhanced Sync",
                delegate(IntPtr gpu, out bool supported, out bool enabled)
                { return AdlxApi.EnhancedSyncGet(gpu, out supported, out enabled); },
                delegate(IntPtr gpu) { return AdlxApi.EnhancedSyncSet(gpu, true); },
                true);
        }

        public static bool RestoreEnhancedSync()
        {
            return RestoreToggle("esync", "Enhanced Sync",
                delegate(IntPtr gpu, bool on) { return AdlxApi.EnhancedSyncSet(gpu, on); },
                delegate(IntPtr gpu, out bool supported, out bool enabled)
                { return AdlxApi.EnhancedSyncGet(gpu, out supported, out enabled); });
        }

        public static bool ActivateChill(int targetFps)
        {
            if (targetFps <= 0 || !Available) return false;
            lock (lk)
            {
                IntPtr[] gpus = AdlxApi.GetGpus();
                if (gpus == null || gpus.Length == 0) return false;
                try
                {
                    var snapshot = NvDrsTweaks.ParseSnapshot(Settings.LoadStr(SnapKey, ""));
                    int applied = 0, failed = 0;
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        bool supported, enabled;
                        int minFps, maxFps;
                        AdlxIntRange range;
                        if (!AdlxApi.ChillGet(gpus[i], out supported, out enabled, out minFps, out maxFps, out range)
                            || !supported) continue;
                        string key = "g" + i + ".chill";
                        if (!snapshot.ContainsKey(key))
                        {
                            snapshot[key] = (enabled ? "1" : "0") + "|" + minFps + "|" + maxFps;
                            if (!Settings.SaveStr(SnapKey, NvDrsTweaks.SerializeSnapshot(snapshot)))
                            {
                                Logger.Log("AMD Chill：快照无法持久化，本轮未启用");
                                return false;
                            }
                        }
                        int fps = Clamp(targetFps, range.Min, range.Max);
                        if (!AdlxApi.ChillSet(gpus[i], true, fps, fps)) { failed++; continue; }
                        bool vSupported, vEnabled;
                        int vMin, vMax;
                        AdlxIntRange vRange;
                        if (AdlxApi.ChillGet(gpus[i], out vSupported, out vEnabled, out vMin, out vMax, out vRange)
                            && vEnabled && vMax == fps) applied++;
                        else failed++;
                    }
                    if (applied > 0 && failed == 0)
                    {
                        Logger.Log("AMD Chill：帧率钉在 " + targetFps + " fps（驱动级，退出恢复）");
                        return true;
                    }
                    if (failed > 0) Logger.Log("AMD Chill：写入或回读核验失败 (" + failed + " 个 GPU)");
                    return false;
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
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
                    else Logger.Log("AMD Chill 还原失败，快照保留，下次启动继续尝试");
                    return allOk;
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
        }

        public static bool ActivateRis()
        {
            if (!Available) return false;
            lock (lk)
            {
                IntPtr[] gpus = AdlxApi.GetGpus();
                if (gpus == null || gpus.Length == 0) return false;
                try
                {
                    var snapshot = NvDrsTweaks.ParseSnapshot(Settings.LoadStr(SnapKey, ""));
                    int applied = 0, failed = 0;
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        bool supported, enabled;
                        int sharpness;
                        AdlxIntRange range;
                        if (!AdlxApi.RisGet(gpus[i], out supported, out enabled, out sharpness, out range)
                            || !supported) continue;
                        string key = "g" + i + ".ris";
                        if (!snapshot.ContainsKey(key))
                        {
                            snapshot[key] = (enabled ? "1" : "0") + "|" + sharpness;
                            if (!Settings.SaveStr(SnapKey, NvDrsTweaks.SerializeSnapshot(snapshot)))
                            {
                                Logger.Log("AMD 锐化：快照无法持久化，本轮未启用");
                                return false;
                            }
                        }
                        int target = Clamp(RisSharpness, range.Min, range.Max);
                        if (!AdlxApi.RisSet(gpus[i], true, target)) { failed++; continue; }
                        bool vSupported, vEnabled;
                        int vSharp;
                        AdlxIntRange vRange;
                        if (AdlxApi.RisGet(gpus[i], out vSupported, out vEnabled, out vSharp, out vRange)
                            && vEnabled) applied++;
                        else failed++;
                    }
                    if (applied > 0 && failed == 0)
                    {
                        Logger.Log("AMD 锐化：RIS 已开启，强度 " + RisSharpness);
                        return true;
                    }
                    if (failed > 0) Logger.Log("AMD 锐化：写入或回读核验失败 (" + failed + " 个 GPU)");
                    return false;
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
                    else Logger.Log("AMD 锐化还原失败，快照保留，下次启动继续尝试");
                    return allOk;
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
        }

        private delegate bool FeatureGetter(IntPtr gpu, out bool supported, out bool enabled);
        private delegate bool FeatureApplier(IntPtr gpu);
        private delegate bool FeatureSetter(IntPtr gpu, bool on);

        private static bool ApplyToggle(string keySuffix, string label,
            FeatureGetter get, FeatureApplier apply, bool wantEnabled)
        {
            if (!Available) return false;
            lock (lk)
            {
                IntPtr[] gpus = AdlxApi.GetGpus();
                if (gpus == null || gpus.Length == 0) return false;
                try
                {
                    var snapshot = NvDrsTweaks.ParseSnapshot(Settings.LoadStr(SnapKey, ""));
                    int applied = 0, failed = 0;
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        bool supported, enabled;
                        if (!get(gpus[i], out supported, out enabled) || !supported) continue;
                        if (enabled == wantEnabled) { applied++; continue; }
                        string key = "g" + i + "." + keySuffix;
                        if (!snapshot.ContainsKey(key))
                        {
                            snapshot[key] = enabled ? "1" : "0";
                            if (!Settings.SaveStr(SnapKey, NvDrsTweaks.SerializeSnapshot(snapshot)))
                            {
                                Logger.Log("AMD " + label + "：快照无法持久化，本轮未启用");
                                return false;
                            }
                        }
                        if (!apply(gpus[i])) { failed++; continue; }
                        bool vSupported, vEnabled;
                        if (get(gpus[i], out vSupported, out vEnabled) && vEnabled == wantEnabled) applied++;
                        else failed++;
                    }
                    if (applied > 0 && failed == 0)
                    {
                        Logger.Log("AMD " + label + "：已开启（驱动级，退出恢复）");
                        return true;
                    }
                    if (failed > 0) Logger.Log("AMD " + label + "：写入或回读核验失败 (" + failed + " 个 GPU)");
                    else if (applied == 0) Logger.Log("AMD " + label + "：本机 GPU 均不支持该功能");
                    return false;
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
        }

        private static bool RestoreToggle(string keySuffix, string label, FeatureSetter set, FeatureGetter get)
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
                    else Logger.Log("AMD " + label + " 还原失败，快照保留，下次启动继续尝试");
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

        private static int Clamp(int value, int min, int max)
        {
            if (max > 0 && value > max) return max;
            if (min > 0 && value < min) return min;
            return value;
        }

        public static bool ResetShaderCacheAll(out int done)
        {
            done = 0;
            if (!Available) return false;
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0) return false;
            try
            {
                foreach (IntPtr gpu in gpus)
                    if (AdlxApi.ResetShaderCache(gpu)) done++;
                Logger.Log("AMD 着色器缓存重置：" + done + "/" + gpus.Length + " 个 GPU 已执行");
                return done > 0;
            }
            finally { AdlxApi.ReleaseAll(gpus); }
        }

        public static void HealFromCrash()
        {
            if (Settings.LoadStr(SnapKey, "").Length == 0) return;
            bool ok = RestoreAntiLag() & RestoreEnhancedSync() & RestoreChill() & RestoreRis();
            if (ok) Logger.Log("检测到上次未还原的 AMD 驱动设置，已恢复");
        }

        public sealed class ProbeRow
        {
            public string Name;
            public string Outcome;
            public bool Ok;
        }

        public static List<ProbeRow> ProbeWriteback()
        {
            var rows = new List<ProbeRow>();
            if (!Available) return rows;
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0) return rows;
            try
            {
                for (int i = 0; i < gpus.Length; i++)
                {
                    string name = AdlxApi.GpuName(gpus[i]);
                    bool supported, enabled;
                    if (AdlxApi.AntiLagGet(gpus[i], out supported, out enabled) && supported)
                    {
                        bool wrote = AdlxApi.AntiLagSet(gpus[i], !enabled);
                        bool vSupported, vEnabled;
                        bool verified = wrote && AdlxApi.AntiLagGet(gpus[i], out vSupported, out vEnabled)
                            && vEnabled == !enabled;
                        bool restored = AdlxApi.AntiLagSet(gpus[i], enabled);
                        rows.Add(new ProbeRow
                        {
                            Name = (name ?? ("GPU" + i)) + " Anti-Lag",
                            Ok = verified && restored,
                            Outcome = !wrote ? "写入被拒绝"
                                : !verified ? "写入报成功但回读不符"
                                : !restored ? "生效但原值恢复失败"
                                : "写入与还原均生效"
                        });
                    }
                    else
                        rows.Add(new ProbeRow
                        {
                            Name = (name ?? ("GPU" + i)) + " Anti-Lag",
                            Ok = false,
                            Outcome = "不支持或读取失败"
                        });

                    bool cSupported, cEnabled;
                    int cMin, cMax;
                    AdlxIntRange cRange;
                    if (AdlxApi.ChillGet(gpus[i], out cSupported, out cEnabled, out cMin, out cMax, out cRange))
                        rows.Add(new ProbeRow
                        {
                            Name = (name ?? ("GPU" + i)) + " Chill",
                            Ok = cSupported,
                            Outcome = cSupported
                                ? "支持，范围 " + cRange.Min + "-" + cRange.Max + " fps"
                                : "不支持"
                        });
                }
                return rows;
            }
            finally { AdlxApi.ReleaseAll(gpus); }
        }
    }
}
