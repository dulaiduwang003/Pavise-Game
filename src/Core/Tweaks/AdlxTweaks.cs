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

        private static bool renderSkipLogged;

        private static bool AmdRenderGateOpen()
        {
            GpuAdapter[] all = GpuInventory.Adapters();
            if (all == null || all.Length == 0) return true;
            bool amdAny = false, amdDiscrete = false, otherDiscrete = false;
            foreach (GpuAdapter a in all)
            {
                if (a.Vendor == GpuVendor.Amd) { amdAny = true; if (!a.Integrated) amdDiscrete = true; }
                else if (!a.Integrated) otherDiscrete = true;
            }
            bool open = amdDiscrete || (amdAny && !otherDiscrete);
            if (!open && !renderSkipLogged)
            {
                renderSkipLogged = true;
                Logger.Log(Lang.T("log.adlxtweaks.36"));
            }
            return open;
        }

        private static Dictionary<string, string> LoadSnap()
        {
            return NvDrsTweaks.ParseSnapshot(Settings.LoadStr(SnapKey, ""));
        }

        private static bool SaveSnap(Dictionary<string, string> snapshot)
        {
            return Settings.SaveStr(SnapKey,
                snapshot.Count == 0 ? "" : NvDrsTweaks.SerializeSnapshot(snapshot));
        }

        private static string GpuKey(IntPtr gpu, int index, ref bool identityMissing)
        {
            string pnp = AdlxApi.GpuPnpString(gpu);
            if (string.IsNullOrEmpty(pnp)) { identityMissing = true; return "g" + index; }
            return "id" + pnp.Replace('=', '_').Replace(';', '_');
        }

        private static bool EnsureSnapshot(Dictionary<string, string> snapshot,
            string key, string legacyKey, string value, string label)
        {
            if (!snapshot.ContainsKey(key) && legacyKey != null && snapshot.ContainsKey(legacyKey))
            {
                snapshot[key] = snapshot[legacyKey];
                snapshot.Remove(legacyKey);
                if (!SaveSnap(snapshot))
                {
                    Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.1"));
                    return false;
                }
                return true;
            }
            if (!snapshot.ContainsKey(key))
            {
                snapshot[key] = value;
                if (!SaveSnap(snapshot))
                {
                    Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.1"));
                    return false;
                }
            }
            return true;
        }

        public static bool ActivateAntiLag()
        {
            if (!AmdRenderGateOpen()) return true;
            SnapshotChillConflict();
            return ApplyToggle("alag", "Anti-Lag",
                delegate(IntPtr gpu, out bool supported, out bool enabled)
                { return AdlxApi.AntiLagGet(gpu, out supported, out enabled); },
                delegate(IntPtr gpu) { return AdlxApi.AntiLagSet(gpu, true); },
                true);
        }

        public static bool RestoreAntiLag()
        {
            return RestoreFeature("alag", "Anti-Lag",
                delegate(IntPtr gpu, string orig) { return AdlxApi.AntiLagSet(gpu, orig == "1"); });
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
            return RestoreFeature("esync", "Enhanced Sync",
                delegate(IntPtr gpu, string orig) { return AdlxApi.EnhancedSyncSet(gpu, orig == "1"); });
        }

        public static bool RestoreChill()
        {
            return RestoreFeature("chill", "Chill",
                delegate(IntPtr gpu, string orig)
                {
                    string[] parts = orig.Split('|');
                    int minFps, maxFps;
                    if (parts.Length != 3
                        || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minFps)
                        || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxFps))
                        return true;
                    return parts[0] == "1"
                        ? AdlxApi.ChillSet(gpu, true, minFps, maxFps)
                        : AdlxApi.ChillSet(gpu, false, 0, 0);
                });
        }

        public static bool ActivateRis()
        {
            return ApplyRange("ris", Lang.T("t.adlxtweaks.4"), RisSharpness,
                Lang.T("t.adlxtweaks.5") + RisSharpness,
                delegate(IntPtr gpu, out bool supported, out string snapValue, out AdlxIntRange range)
                {
                    bool enabled;
                    int sharpness;
                    bool ok = AdlxApi.RisGet(gpu, out supported, out enabled, out sharpness, out range);
                    snapValue = (enabled ? "1" : "0") + "|" + sharpness;
                    return ok;
                },
                delegate(IntPtr gpu, int target) { return AdlxApi.RisSet(gpu, true, target); },
                delegate(IntPtr gpu, int target)
                {
                    bool vSupported, vEnabled;
                    int vSharp;
                    AdlxIntRange vRange;
                    return AdlxApi.RisGet(gpu, out vSupported, out vEnabled, out vSharp, out vRange)
                        && vEnabled;
                });
        }

        public static bool RestoreRis()
        {
            return RestoreFeature("ris", Lang.T("t.adlxtweaks.4"),
                delegate(IntPtr gpu, string orig)
                {
                    string[] parts = orig.Split('|');
                    int sharpness;
                    if (parts.Length != 2
                        || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sharpness))
                        return true;
                    return AdlxApi.RisSet(gpu, parts[0] == "1", sharpness);
                });
        }

        private static void SnapshotChillConflict()
        {
            if (!Available) return;
            lock (lk)
            {
                IntPtr[] gpus = AdlxApi.GetGpus();
                if (gpus == null || gpus.Length == 0) return;
                try
                {
                    var snapshot = LoadSnap();
                    bool idMiss = false;
                    bool dirty = false;
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        bool supported, enabled;
                        int minFps, maxFps;
                        AdlxIntRange range;
                        if (!AdlxApi.ChillGet(gpus[i], out supported, out enabled, out minFps, out maxFps, out range)
                            || !supported || !enabled) continue;
                        string key = GpuKey(gpus[i], i, ref idMiss) + ".chill";
                        if (snapshot.ContainsKey(key) || snapshot.ContainsKey("g" + i + ".chill")) continue;
                        snapshot[key] = "1|" + minFps + "|" + maxFps;
                        dirty = true;
                    }
                    if (dirty && SaveSnap(snapshot))
                        Logger.Log(Lang.T("log.adlxtweaks.6"));
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
        }

        public static bool RestoreFrameLimit()
        {
            return RestoreChill() & RestoreFrtc();
        }

        public static bool HasFrameLimitResidue()
        {
            var snapshot = LoadSnap();
            return HasPrefix(snapshot, ".chill") || HasPrefix(snapshot, ".frtc");
        }

        public static bool RestoreFrtc()
        {
            return RestoreFeature("frtc", Lang.T("t.adlxtweaks.8"),
                delegate(IntPtr gpu, string orig)
                {
                    string[] parts = orig.Split('|');
                    int fps;
                    if (parts.Length != 2
                        || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out fps))
                        return true;
                    return parts[0] == "1"
                        ? AdlxApi.FrtcSet(gpu, true, fps)
                        : AdlxApi.FrtcSet(gpu, false, 0);
                });
        }

        public static bool ActivateAfmf()
        {
            if (!Available) return false;
            lock (lk)
            {
                bool supported, enabled;
                if (!AdlxApi.AfmfGet(out supported, out enabled) || !supported)
                {
                    Logger.Log(Lang.T("log.adlxtweaks.11"));
                    return false;
                }
                if (enabled) return true;
                var snapshot = LoadSnap();
                if (!snapshot.ContainsKey("sys.afmf"))
                {
                    snapshot["sys.afmf"] = "0";
                    if (!SaveSnap(snapshot))
                    {
                        Logger.Log(Lang.T("log.adlxtweaks.12"));
                        return false;
                    }
                }
                if (!AdlxApi.AfmfSet(true))
                {
                    Logger.Log(Lang.T("log.adlxtweaks.13"));
                    return false;
                }
                bool vSupported, vEnabled;
                if (AdlxApi.AfmfGet(out vSupported, out vEnabled) && vEnabled)
                {
                    Logger.Log(Lang.T("log.adlxtweaks.14"));
                    return true;
                }
                Logger.Log(Lang.T("log.adlxtweaks.15"));
                return false;
            }
        }

        public static bool RestoreAfmf()
        {
            lock (lk)
            {
                var snapshot = LoadSnap();
                string orig;
                if (!snapshot.TryGetValue("sys.afmf", out orig)) return true;
                if (!Available) return false;
                if (!AdlxApi.AfmfSet(orig == "1"))
                {
                    Logger.Log(Lang.T("log.adlxtweaks.16"));
                    return false;
                }
                snapshot.Remove("sys.afmf");
                SaveSnap(snapshot);
                Logger.Log(Lang.T("log.adlxtweaks.17"));
                return true;
            }
        }

        public const int RsrDefaultSharpness = 75;

        public static bool ActivateRsr()
        {
            if (!AmdRenderGateOpen()) return true;
            if (!Available) return false;
            lock (lk)
            {
                bool supported, enabled;
                int sharpness;
                if (!AdlxApi.RsrGet(out supported, out enabled, out sharpness) || !supported)
                {
                    Logger.Log(Lang.T("log.adlxtweaks.18"));
                    return false;
                }
                if (enabled) return true;
                var snapshot = LoadSnap();
                if (!snapshot.ContainsKey("sys.rsr"))
                {
                    snapshot["sys.rsr"] = "0";
                    snapshot["sys.rsrsharp"] = sharpness.ToString();
                    if (!SaveSnap(snapshot))
                    {
                        Logger.Log(Lang.T("log.adlxtweaks.19"));
                        return false;
                    }
                }
                if (!AdlxApi.RsrSet(true, RsrDefaultSharpness))
                {
                    Logger.Log(Lang.T("log.adlxtweaks.20"));
                    return false;
                }
                bool vSupported, vEnabled;
                int vSharp;
                if (AdlxApi.RsrGet(out vSupported, out vEnabled, out vSharp) && vEnabled)
                    Logger.Log(Lang.T("log.adlxtweaks.21"));
                return true;
            }
        }

        public static bool RestoreRsr()
        {
            lock (lk)
            {
                var snapshot = LoadSnap();
                string orig;
                if (!snapshot.TryGetValue("sys.rsr", out orig)) return true;
                if (!Available) return false;
                string sharpRaw;
                int origSharp;
                if (!snapshot.TryGetValue("sys.rsrsharp", out sharpRaw)
                    || !int.TryParse(sharpRaw, out origSharp)) origSharp = -1;
                if (!AdlxApi.RsrSet(orig == "1", orig == "1" ? origSharp : -1))
                {
                    Logger.Log(Lang.T("log.adlxtweaks.22"));
                    return false;
                }
                snapshot.Remove("sys.rsr");
                snapshot.Remove("sys.rsrsharp");
                SaveSnap(snapshot);
                Logger.Log(Lang.T("log.adlxtweaks.23"));
                return true;
            }
        }

        public static bool PowerLimitGetFirst(out int current, out int max)
        {
            current = 0; max = 0;
            if (!Available) return false;
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0) return false;
            try
            {
                foreach (IntPtr gpu in gpus)
                {
                    bool supported;
                    int cur, top;
                    if (AdlxApi.PowerLimitGet(gpu, out supported, out cur, out top) && supported && top > 0)
                    {
                        current = cur; max = top;
                        return true;
                    }
                }
                return false;
            }
            finally { AdlxApi.ReleaseAll(gpus); }
        }

        public static bool PowerLimitSetFirst(int value)
        {
            if (!Available) return false;
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0) return false;
            try
            {
                foreach (IntPtr gpu in gpus)
                {
                    bool supported;
                    int cur, top;
                    if (AdlxApi.PowerLimitGet(gpu, out supported, out cur, out top) && supported && top > 0)
                        return AdlxApi.PowerLimitSet(gpu, value);
                }
                return false;
            }
            finally { AdlxApi.ReleaseAll(gpus); }
        }

        private static int alagSup, afmfSup, rsrSup;

        public static bool RsrSupported()
        {
            if (!AmdRenderGateOpen()) return false;
            if (rsrSup == 0)
            {
                bool s, e;
                int sharp;
                rsrSup = Available && AdlxApi.RsrGet(out s, out e, out sharp) && s ? 1 : -1;
            }
            return rsrSup > 0;
        }

        public static bool AntiLagSupported()
        {
            if (!AmdRenderGateOpen()) return false;
            if (alagSup == 0) alagSup = ProbeGpuToggle(delegate(IntPtr gpu, out bool s, out bool e)
                { return AdlxApi.AntiLagGet(gpu, out s, out e); }) ? 1 : -1;
            return alagSup > 0;
        }

        public static bool AfmfSupported()
        {
            if (!AmdRenderGateOpen()) return false;
            if (afmfSup == 0)
            {
                bool s, e;
                afmfSup = Available && AdlxApi.AfmfGet(out s, out e) && s ? 1 : -1;
            }
            return afmfSup > 0;
        }

        private static bool ProbeGpuToggle(FeatureGetter get)
        {
            if (!Available) return false;
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0) return false;
            try
            {
                foreach (IntPtr gpu in gpus)
                {
                    bool s, e;
                    if (get(gpu, out s, out e) && s) return true;
                }
                return false;
            }
            finally { AdlxApi.ReleaseAll(gpus); }
        }

        private delegate bool FeatureGetter(IntPtr gpu, out bool supported, out bool enabled);
        private delegate bool FeatureApplier(IntPtr gpu);
        private delegate bool RestoreApply(IntPtr gpu, string orig);
        private delegate bool RangeGetter(IntPtr gpu, out bool supported, out string snapValue,
            out AdlxIntRange range);
        private delegate bool RangeSetter(IntPtr gpu, int value);
        private delegate bool RangeVerifier(IntPtr gpu, int value);

        private static bool ApplyRange(string keySuffix, string label, int desired, string successLog,
            RangeGetter get, RangeSetter set, RangeVerifier verify)
        {
            if (!Available) return false;
            lock (lk)
            {
                IntPtr[] gpus = AdlxApi.GetGpus();
                if (gpus == null || gpus.Length == 0) return false;
                try
                {
                    var snapshot = LoadSnap();
                    bool idMiss = false;
                    int applied = 0, failed = 0;
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        bool supported;
                        string snapValue;
                        AdlxIntRange range;
                        if (!get(gpus[i], out supported, out snapValue, out range) || !supported) continue;
                        string key = GpuKey(gpus[i], i, ref idMiss) + "." + keySuffix;
                        if (!EnsureSnapshot(snapshot, key, "g" + i + "." + keySuffix,
                            snapValue, label)) return false;
                        int value = Clamp(desired, range.Min, range.Max);
                        if (!set(gpus[i], value)) { failed++; continue; }
                        if (verify(gpus[i], value)) applied++;
                        else failed++;
                    }
                    if (applied > 0 && failed == 0)
                    {
                        Logger.Log(successLog);
                        return true;
                    }
                    if (failed > 0) Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.24") + failed + Lang.T("log.adlxtweaks.25"));
                    else if (applied == 0) Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.26"));
                    return false;
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
        }

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
                    var snapshot = LoadSnap();
                    bool idMiss = false;
                    int applied = 0, failed = 0;
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        bool supported, enabled;
                        if (!get(gpus[i], out supported, out enabled) || !supported) continue;
                        if (enabled == wantEnabled) { applied++; continue; }
                        string key = GpuKey(gpus[i], i, ref idMiss) + "." + keySuffix;
                        if (!EnsureSnapshot(snapshot, key, "g" + i + "." + keySuffix,
                            enabled ? "1" : "0", label)) return false;
                        if (!apply(gpus[i])) { failed++; continue; }
                        bool vSupported, vEnabled;
                        if (get(gpus[i], out vSupported, out vEnabled) && vEnabled == wantEnabled) applied++;
                        else failed++;
                    }
                    if (applied > 0 && failed == 0)
                    {
                        Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.27"));
                        return true;
                    }
                    if (failed > 0) Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.24") + failed + Lang.T("log.adlxtweaks.25"));
                    else if (applied == 0) Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.26"));
                    return false;
                }
                finally { AdlxApi.ReleaseAll(gpus); }
            }
        }

        private static bool RestoreFeature(string suffix, string label, RestoreApply apply)
        {
            lock (lk)
            {
                var snapshot = LoadSnap();
                if (!HasPrefix(snapshot, "." + suffix)) return true;
                if (!Available) return false;
                IntPtr[] gpus = AdlxApi.GetGpus();
                if (gpus == null) return false;
                try
                {
                    bool allOk = true;
                    bool idMiss = false;
                    var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < gpus.Length; i++)
                    {
                        string[] keys =
                        {
                            GpuKey(gpus[i], i, ref idMiss) + "." + suffix,
                            "g" + i + "." + suffix
                        };
                        foreach (string key in keys)
                        {
                            reachable.Add(key);
                            string orig;
                            if (!snapshot.TryGetValue(key, out orig)) continue;
                            if (apply(gpus[i], orig)) snapshot.Remove(key);
                            else allOk = false;
                        }
                    }
                    if (!idMiss)
                    {
                        var stale = new List<string>();
                        foreach (KeyValuePair<string, string> kv in snapshot)
                            if (kv.Key.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)
                                && !reachable.Contains(kv.Key)) stale.Add(kv.Key);
                        foreach (string key in stale) snapshot.Remove(key);
                        if (stale.Count > 0)
                            Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.28") + stale.Count + Lang.T("log.adlxtweaks.29"));
                    }
                    SaveSnap(snapshot);
                    if (allOk) Logger.Log("AMD " + label + Lang.T("log.renderlane.13"));
                    else Logger.Log("AMD " + label + Lang.T("log.adlxtweaks.30"));
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
                Logger.Log(Lang.T("log.adlxtweaks.31") + done + "/" + gpus.Length + Lang.T("log.adlxtweaks.32"));
                return done > 0;
            }
            finally { AdlxApi.ReleaseAll(gpus); }
        }

        public static bool HasResidue() { return Settings.LoadStr(SnapKey, "").Length > 0; }

        public static void HealFromCrash()
        {
            if (Settings.LoadStr(SnapKey, "").Length == 0) return;
            bool ok = RestoreAntiLag() & RestoreEnhancedSync() & RestoreChill() & RestoreRis()
                & RestoreFrtc() & RestoreAfmf() & RestoreRsr();
            if (ok) Logger.Log(Lang.T("log.adlxtweaks.33"));
        }

        public static bool PurgeResidue()
        {
            HealFromCrash();
            if (!HasResidue()) return true;
            if (!Available)
            {
                if (AmdDisplayPresent())
                {
                    Logger.Log(Lang.T("log.adlxtweaks.34"));
                    return false;
                }
                Logger.Log(Lang.T("log.adlxtweaks.35"));
                Settings.SaveStr(SnapKey, "");
                return !HasResidue();
            }
            return false;
        }

        private static bool AmdDisplayPresent()
        {
            try
            {
                foreach (string id in PresentDevices.ByClass(
                    new Guid("4d36e968-e325-11ce-bfc1-08002be10318")))
                    if (id != null && id.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            }
            catch { }
            return false;
        }
    }
}
