using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal static class AdlxTweaks
    {

        internal const string SnapKey = "AmdSnap";

        public const int RsrDefaultSharpness = 75;

        private static readonly object lk = new object();

        private static bool renderSkipLogged;

        private static int afmfSup;

        private static int rsrSup;

        public static bool Available
        {
            get
            {
                return AdlxApi.Available;
            }
        }

        private static bool AmdRenderGateOpen()
        {
            GpuAdapter[] adapters = GpuInventory.Adapters();
            if (adapters == null || adapters.Length == 0)
            {
                return true;
            }
            bool hasAmdGpu = false;
            bool hasDiscreteAmdGpu = false;
            bool hasOtherDiscreteGpu = false;
            GpuAdapter[] availableAdapters = adapters;
            foreach (GpuAdapter gpuAdapter in availableAdapters)
            {
                if (gpuAdapter.Vendor == GpuVendor.Amd)
                {
                    hasAmdGpu = true;
                    if (!gpuAdapter.Integrated)
                    {
                        hasDiscreteAmdGpu = true;
                    }
                }
                else if (!gpuAdapter.Integrated)
                {
                    hasOtherDiscreteGpu = true;
                }
            }
            bool canRenderOnAmd = hasDiscreteAmdGpu || (hasAmdGpu && !hasOtherDiscreteGpu);
            if (!canRenderOnAmd && !renderSkipLogged)
            {
                renderSkipLogged = true;
                Logger.Log(Lang.T("log.adlxtweaks.36", 0));
            }
            return canRenderOnAmd;
        }

        // 1.x/2.x 写入的逐显卡旧项（Anti-Lag、Chill、Enhanced Sync、RIS、FRTC、最低频率）不恢复，读取快照时丢弃。
        private static readonly string[] RetiredSnapSuffixes = { ".alag", ".esync", ".chill", ".ris", ".frtc", ".gfxmin" };

        private static bool IsRetiredSnapKey(string key)
        {
            foreach (string suffix in RetiredSnapSuffixes)
            {
                if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static Dictionary<string, string> LoadSnap()
        {
            Dictionary<string, string> snapshot = NvDrsTweaks.ParseSnapshot(Settings.LoadStr("AmdSnap", ""));
            List<string> retired = new List<string>();
            foreach (string key in snapshot.Keys)
            {
                if (IsRetiredSnapKey(key))
                {
                    retired.Add(key);
                }
            }
            foreach (string key in retired)
            {
                snapshot.Remove(key);
            }
            return snapshot;
        }

        // 启动时把旧项从持久快照中清掉，避免它们一直被当成待恢复记录。
        private static void DropRetiredSnapshot()
        {
            lock (lk)
            {
                string raw = Settings.LoadStr("AmdSnap", "");
                if (raw.Length == 0)
                {
                    return;
                }
                Dictionary<string, string> snapshot = LoadSnap();
                if (NvDrsTweaks.ParseSnapshot(raw).Count != snapshot.Count && SaveSnap(snapshot))
                {
                    Logger.Info("启动 事件=旧版AMD显卡记录 结果=已丢弃");
                }
            }
        }

        private static bool SaveSnap(Dictionary<string, string> snapshot)
        {
            return Settings.SaveStrDurable("AmdSnap", (snapshot.Count == 0) ? "" : NvDrsTweaks.SerializeSnapshot(snapshot));
        }

        public static bool ActivateAfmf()
        {
            if (!Available)
            {
                return false;
            }
            lock (lk)
            {
                bool supported;
                bool enabled;
                if (!AdlxApi.AfmfGet(out supported, out enabled) || !supported)
                {
                    Logger.Log(Lang.T("log.adlxtweaks.11", 0));
                    return false;
                }
                if (enabled)
                {
                    return true;
                }
                Dictionary<string, string> dictionary = LoadSnap();
                if (!dictionary.ContainsKey("sys.afmf"))
                {
                    dictionary["sys.afmf"] = "0";
                    if (!SaveSnap(dictionary))
                    {
                        Logger.Log(Lang.T("log.adlxtweaks.12", 0));
                        return false;
                    }
                }
                if (!AdlxApi.AfmfSet(true))
                {
                    Logger.Warn(Lang.T("log.adlxtweaks.13", 0));
                    return false;
                }
                bool supported2;
                bool enabled2;
                if (AdlxApi.AfmfGet(out supported2, out enabled2) && enabled2)
                {
                    Logger.Log(Lang.T("log.adlxtweaks.14", 0));
                    return true;
                }
                Logger.Log(Lang.T("log.adlxtweaks.15", 0));
                return false;
            }
        }

        public static bool RestoreAfmf()
        {
            lock (lk)
            {
                Dictionary<string, string> dictionary = LoadSnap();
                string value;
                if (!dictionary.TryGetValue("sys.afmf", out value))
                {
                    return true;
                }
                if (!Available)
                {
                    return false;
                }
                if (!AdlxApi.AfmfSet(value == "1"))
                {
                    Logger.Log(Lang.T("log.adlxtweaks.16", 0));
                    return false;
                }
                dictionary.Remove("sys.afmf");
                SaveSnap(dictionary);
                Logger.Log(Lang.T("log.adlxtweaks.17", 0));
                return true;
            }
        }

        public static bool ActivateRsr()
        {
            if (!AmdRenderGateOpen())
            {
                return true;
            }
            if (!Available)
            {
                return false;
            }
            lock (lk)
            {
                bool supported;
                bool enabled;
                int sharpness;
                if (!AdlxApi.RsrGet(out supported, out enabled, out sharpness) || !supported)
                {
                    Logger.Log(Lang.T("log.adlxtweaks.18", 0));
                    return false;
                }
                if (enabled)
                {
                    return true;
                }
                Dictionary<string, string> dictionary = LoadSnap();
                if (!dictionary.ContainsKey("sys.rsr"))
                {
                    dictionary["sys.rsr"] = "0";
                    dictionary["sys.rsrsharp"] = sharpness.ToString();
                    if (!SaveSnap(dictionary))
                    {
                        Logger.Log(Lang.T("log.adlxtweaks.19", 0));
                        return false;
                    }
                }
                if (!AdlxApi.RsrSet(true, 75))
                {
                    Logger.Warn(Lang.T("log.adlxtweaks.20", 0));
                    return false;
                }
                bool supported2;
                bool enabled2;
                int sharpness2;
                if (AdlxApi.RsrGet(out supported2, out enabled2, out sharpness2) && enabled2)
                {
                    Logger.Log(Lang.T("log.adlxtweaks.21", 0));
                }
                return true;
            }
        }

        public static bool RestoreRsr()
        {
            lock (lk)
            {
                Dictionary<string, string> dictionary = LoadSnap();
                string value;
                if (!dictionary.TryGetValue("sys.rsr", out value))
                {
                    return true;
                }
                if (!Available)
                {
                    return false;
                }
                string value2;
                int result;
                if (!dictionary.TryGetValue("sys.rsrsharp", out value2) || !int.TryParse(value2, out result))
                {
                    result = -1;
                }
                if (!AdlxApi.RsrSet(value == "1", (value == "1") ? result : (-1)))
                {
                    Logger.Log(Lang.T("log.adlxtweaks.22", 0));
                    return false;
                }
                dictionary.Remove("sys.rsr");
                dictionary.Remove("sys.rsrsharp");
                SaveSnap(dictionary);
                Logger.Log(Lang.T("log.adlxtweaks.23", 0));
                return true;
            }
        }

        public static bool PowerLimitGetFirst(out int current, out int max)
        {
            current = 0;
            max = 0;
            if (!Available)
            {
                return false;
            }
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0)
            {
                return false;
            }
            try
            {
                IntPtr[] gpusSnapshot = gpus;
                foreach (IntPtr gpu in gpusSnapshot)
                {
                    bool supported;
                    int current2;
                    int max2;
                    if (AdlxApi.PowerLimitGet(gpu, out supported, out current2, out max2) && supported && max2 > 0)
                    {
                        current = current2;
                        max = max2;
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                AdlxApi.ReleaseAll(gpus);
            }
        }

        public static bool PowerLimitSetFirst(int value)
        {
            if (!Available)
            {
                return false;
            }
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0)
            {
                return false;
            }
            try
            {
                IntPtr[] gpusSnapshot = gpus;
                foreach (IntPtr gpu in gpusSnapshot)
                {
                    bool supported;
                    int current;
                    int max;
                    if (AdlxApi.PowerLimitGet(gpu, out supported, out current, out max) && supported && max > 0)
                    {
                        return AdlxApi.PowerLimitSet(gpu, value);
                    }
                }
                return false;
            }
            finally
            {
                AdlxApi.ReleaseAll(gpus);
            }
        }

        public static bool RsrSupported()
        {
            if (!AmdRenderGateOpen())
            {
                return false;
            }
            if (rsrSup == 0)
            {
                bool supported;
                bool enabled;
                int sharpness;
                rsrSup = ((Available && AdlxApi.RsrGet(out supported, out enabled, out sharpness) && supported) ? 1 : (-1));
            }
            return rsrSup > 0;
        }

        public static bool AfmfSupported()
        {
            if (!AmdRenderGateOpen())
            {
                return false;
            }
            if (afmfSup == 0)
            {
                bool supported;
                bool enabled;
                afmfSup = ((Available && AdlxApi.AfmfGet(out supported, out enabled) && supported) ? 1 : (-1));
            }
            return afmfSup > 0;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (max > 0 && value > max)
            {
                return max;
            }
            if (min > 0 && value < min)
            {
                return min;
            }
            return value;
        }

        public static bool ResetShaderCacheAll(out int done)
        {
            done = 0;
            if (!Available)
            {
                return false;
            }
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0)
            {
                return false;
            }
            try
            {
                IntPtr[] gpusSnapshot = gpus;
                foreach (IntPtr gpu in gpusSnapshot)
                {
                    if (AdlxApi.ResetShaderCache(gpu))
                    {
                        done++;
                    }
                }
                Logger.Log(Lang.T("log.adlxtweaks.31", 0) + done + "/" + gpus.Length + Lang.T("log.adlxtweaks.32", 0));
                return done > 0;
            }
            finally
            {
                AdlxApi.ReleaseAll(gpus);
            }
        }

        public static bool HasResidue()
        {
            return LoadSnap().Count > 0;
        }

        public static bool SamGet(out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            if (!Available)
            {
                return false;
            }
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0)
            {
                return false;
            }
            try
            {
                IntPtr[] gpusSnapshot = gpus;
                foreach (IntPtr gpu in gpusSnapshot)
                {
                    bool supported2;
                    bool enabled2;
                    if (AdlxApi.SamGet(gpu, out supported2, out enabled2) && supported2)
                    {
                        supported = true;
                        enabled = enabled2;
                        return true;
                    }
                }
                return true;
            }
            finally
            {
                AdlxApi.ReleaseAll(gpus);
            }
        }

        public static bool SamSet(bool on)
        {
            if (!Available)
            {
                return false;
            }
            IntPtr[] gpus = AdlxApi.GetGpus();
            if (gpus == null || gpus.Length == 0)
            {
                return false;
            }
            try
            {
                IntPtr[] gpusSnapshot = gpus;
                foreach (IntPtr gpu in gpusSnapshot)
                {
                    bool supported;
                    bool enabled;
                    if (AdlxApi.SamGet(gpu, out supported, out enabled) && supported)
                    {
                        return AdlxApi.SamSet(gpu, on);
                    }
                }
                return false;
            }
            finally
            {
                AdlxApi.ReleaseAll(gpus);
            }
        }

        public static void HealFromCrash()
        {
            DropRetiredSnapshot();
            if (HasResidue() && (RestoreAfmf() & RestoreRsr()))
            {
                Logger.Log(Lang.T("log.adlxtweaks.33", 0));
            }
        }

        public static bool PurgeResidue()
        {
            HealFromCrash();
            if (!HasResidue())
            {
                return true;
            }
            if (!Available)
            {
                if (AmdDisplayPresent())
                {
                    Logger.Log(Lang.T("log.adlxtweaks.34", 0));
                    return false;
                }
                Logger.Log(Lang.T("log.adlxtweaks.35", 0));
                Settings.SaveStr("AmdSnap", "");
                return !HasResidue();
            }
            return false;
        }

        private static bool AmdDisplayPresent()
        {
            try
            {
                foreach (string item in PresentDevices.ByClass(new Guid("4d36e968-e325-11ce-bfc1-08002be10318")))
                {
                    if (item != null && item.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
