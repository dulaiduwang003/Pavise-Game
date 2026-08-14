// @author bdth 2074055628@qq.com
// 文件用途 按游戏写入 NVIDIA 驱动 Profile 设置 快照先行 可按项恢复

using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed class NvGamePlan
    {
        public bool MaxPerf;
        public int FrlFps;
        public string LowLatMode;
        public bool SmoothMotion;
        public bool ShaderCacheMax;
        public bool AnselOff;
        public bool Rebar;
        public string DlssMode;
        public bool BattFull;

        public bool Empty
        {
            get
            {
                return !MaxPerf && FrlFps <= 0 && !AnselOff && !Rebar && !BattFull
                    && !SmoothMotion && !ShaderCacheMax
                    && (LowLatMode == null || LowLatMode == "off")
                    && (DlssMode == null || DlssMode == "off");
            }
        }
    }

    internal static class NvDrsTweaks
    {
        private const string ListKey = "NvDrsList";
        private const string SnapPrefix = "NvDrs_";
        public const string KeyPState = "pstate";
        public const string KeyFrl = "frl";
        public const string KeyPreRender = "prerender";
        public const string KeyLowLatCpl = "lowlatcpl";
        public const string KeyUllEnable = "ullenable";
        public const string KeySmooth = "smooth";
        public const string KeyShaderCache = "shadercache";
        public const string KeyAnsel = "ansel";
        public const string KeyRebarFeat = "rebarfeat";
        public const string KeyRebarOpt = "rebaropt";
        public const string KeyRebarSize = "rebarsize";
        public const string KeyDlssOvr = "dlssovr";
        public const string KeyDlssPreset = "dlsspreset";
        public const string KeyBattFps = "battfps";

        public static readonly string[] RebarKeys = { KeyRebarFeat, KeyRebarOpt, KeyRebarSize };
        public static readonly string[] DlssKeys = { KeyDlssOvr, KeyDlssPreset };
        public static readonly string[] UltraKeys = { KeyPreRender, KeyUllEnable, KeyLowLatCpl };

        private static readonly object sync = new object();
        private static bool dlssGateLogged;
        private static bool smoothGateLogged;

        internal static uint SettingIdOf(string key)
        {
            switch (key)
            {
                case KeyPState: return NvApi.SettingPreferredPState;
                case KeyPreRender: return NvApi.SettingPreRenderLimit;
                case KeyLowLatCpl: return NvApi.SettingLowLatencyCpl;
                case KeyUllEnable: return NvApi.SettingUltraLowLatEnable;
                case KeySmooth: return NvApi.SettingSmoothMotion;
                case KeyShaderCache: return NvApi.SettingShaderCacheSize;
                case KeyAnsel: return NvApi.SettingAnselAllow;
                case KeyRebarFeat: return NvApi.SettingRebarFeature;
                case KeyRebarOpt: return NvApi.SettingRebarOptions;
                case KeyRebarSize: return NvApi.SettingRebarSizeLimit;
                case KeyDlssOvr: return NvApi.SettingDlssSrOverride;
                case KeyDlssPreset: return NvApi.SettingDlssSrPreset;
                case KeyBattFps: return NvApi.SettingBatteryBoostAppFps;
                default: return NvApi.SettingFrlFps;
            }
        }

        internal static bool IsDlssCapableName(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                gpuName, @"\bRTX\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static bool DlssGpuCapable()
        {
            try
            {
                foreach (GpuAdapter a in GpuInventory.Adapters())
                    if (a.Vendor == GpuVendor.Nvidia && IsDlssCapableName(a.Name)) return true;
            }
            catch { }
            return false;
        }

        public static bool DlssDriverSupported()
        {
            return NvApi.Available && NvApi.DriverVersion() >= NvApi.MinDriverForDlssOverride;
        }

        public static bool DlssOverrideSupported()
        {
            return DlssDriverSupported() && DlssGpuCapable();
        }

        internal static bool IsSmoothMotionCapableName(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                gpuName, @"\bRTX\s*[45]\d{3}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public static bool SmoothMotionGpuCapable()
        {
            return SmoothMotionMinDriver() > 0;
        }

        internal static uint SmoothMotionMinDriver()
        {
            bool has50 = false, has40 = false;
            try
            {
                foreach (GpuAdapter a in GpuInventory.Adapters())
                {
                    if (a.Vendor != GpuVendor.Nvidia || string.IsNullOrEmpty(a.Name)) continue;
                    if (System.Text.RegularExpressions.Regex.IsMatch(a.Name, @"\bRTX\s*5\d{3}\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)) has50 = true;
                    else if (System.Text.RegularExpressions.Regex.IsMatch(a.Name, @"\bRTX\s*4\d{3}\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)) has40 = true;
                }
            }
            catch { }
            return has50 ? NvApi.MinDriverForSmoothMotion50 : has40 ? NvApi.MinDriverForSmoothMotion40 : 0u;
        }

        public static bool SmoothMotionSupported()
        {
            uint min = SmoothMotionMinDriver();
            return min > 0 && NvApi.Available && NvApi.DriverVersion() >= min;
        }

        internal static List<KeyValuePair<string, uint>> BuildDesired(NvGamePlan plan)
        {
            var desired = new List<KeyValuePair<string, uint>>();
            if (plan == null) return desired;
            if (plan.MaxPerf) desired.Add(new KeyValuePair<string, uint>(KeyPState, NvApi.PStatePreferMax));
            if (plan.FrlFps > 0) desired.Add(new KeyValuePair<string, uint>(KeyFrl, (uint)plan.FrlFps));
            string lowLat = plan.LowLatMode;
            if (lowLat == "on" || lowLat == "ultra")
            {
                desired.Add(new KeyValuePair<string, uint>(KeyPreRender, 1u));
                if (lowLat == "ultra")
                {
                    desired.Add(new KeyValuePair<string, uint>(KeyUllEnable, 1u));
                    desired.Add(new KeyValuePair<string, uint>(KeyLowLatCpl, NvApi.UltraCplUltra));
                }
            }
            if (plan.SmoothMotion)
            {
                if (SmoothMotionSupported())
                    desired.Add(new KeyValuePair<string, uint>(KeySmooth, 1u));
                else if (!smoothGateLogged)
                {
                    smoothGateLogged = true;
                    Logger.Log(!SmoothMotionGpuCapable()
                        ? "Smooth Motion 插帧 需要 RTX 40 或 50 系显卡 该项跳过"
                        : "Smooth Motion 插帧 需要 " + FormatDriver(SmoothMotionMinDriver())
                            + " 及以上驱动 本机 " + FormatDriver(NvApi.DriverVersion()) + " 该项跳过");
                }
            }
            if (plan.ShaderCacheMax)
                desired.Add(new KeyValuePair<string, uint>(KeyShaderCache, NvApi.ShaderCacheUnlimited));
            if (plan.AnselOff) desired.Add(new KeyValuePair<string, uint>(KeyAnsel, 0u));
            if (plan.Rebar)
            {
                desired.Add(new KeyValuePair<string, uint>(KeyRebarFeat, 1u));
                desired.Add(new KeyValuePair<string, uint>(KeyRebarOpt, 1u));
                desired.Add(new KeyValuePair<string, uint>(KeyRebarSize, NvApi.RebarSizeDefault));
            }
            string dlss = plan.DlssMode;
            if (dlss == "latest" || dlss == "j" || dlss == "k")
            {
                if (DlssOverrideSupported())
                {
                    desired.Add(new KeyValuePair<string, uint>(KeyDlssOvr, 1u));
                    desired.Add(new KeyValuePair<string, uint>(KeyDlssPreset,
                        dlss == "j" ? NvApi.DlssPresetJ : dlss == "k" ? NvApi.DlssPresetK : NvApi.DlssPresetLatest));
                }
                else if (!dlssGateLogged)
                {
                    dlssGateLogged = true;
                    Logger.Log(!DlssGpuCapable()
                        ? "DLSS 覆写 本机显卡不是 RTX 卡 没有 DLSS 超分能力 该项跳过"
                        : "DLSS 覆写 需要 566.14 及以上驱动 本机 "
                            + FormatDriver(NvApi.DriverVersion()) + " 该项跳过");
                }
            }
            if (plan.BattFull) desired.Add(new KeyValuePair<string, uint>(KeyBattFps, NvApi.BatteryFpsUncapped));
            return desired;
        }

        internal static string FormatDriver(uint version)
        {
            if (version == 0) return "未知版本";
            return (version / 100) + "." + (version % 100).ToString("00");
        }

        public static List<string> ApplyForGame(string exePath, NvGamePlan plan)
        {
            if (string.IsNullOrEmpty(exePath) || plan == null || plan.Empty) return null;
            if (!NvApi.Available) return null;
            string exeName = Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(exeName)) return null;
            var desired = BuildDesired(plan);
            if (desired.Count == 0) return null;
            lock (sync)
            {
                IntPtr session;
                if (!NvApi.TryOpenSession(out session)) return null;
                try
                {
                    IntPtr profile;
                    if (!NvApi.FindOrCreateAppProfile(session, exeName, out profile)) return null;
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
                            Logger.Log("NVIDIA 驱动调优 快照无法持久化 已跳过 " + exeName);
                            return null;
                        }
                    }
                    bool wrote = false;
                    var failed = new List<string>();
                    foreach (var item in desired)
                    {
                        uint current;
                        if (NvApi.TryGetDword(session, profile, SettingIdOf(item.Key), out current) == 1
                            && current == item.Value) continue;
                        int status;
                        if (NvApi.SetDword(session, profile, SettingIdOf(item.Key), item.Value, out status)) wrote = true;
                        else
                        {
                            failed.Add(item.Key);
                            Logger.Log("NVIDIA 驱动调优 写入 " + item.Key + " 失败 " + exeName
                                + " NVAPI 状态 " + status);
                        }
                    }
                    bool saved = !wrote || NvApi.SaveSession(session);
                    if (wrote && saved)
                    {
                        string done = (plan.MaxPerf && !failed.Contains(KeyPState) ? " 电源最高性能" : "")
                            + (plan.FrlFps > 0 && !failed.Contains(KeyFrl) ? " 帧上限" + plan.FrlFps : "")
                            + (plan.LowLatMode == "ultra" && !ContainsAny(failed, UltraKeys) ? " 超低延迟Ultra "
                                : plan.LowLatMode == "on" && !failed.Contains(KeyPreRender) ? " 低延迟 预渲染1 " : "")
                            + (plan.SmoothMotion && SmoothMotionSupported() && !failed.Contains(KeySmooth)
                                ? " SmoothMotion插帧" : "")
                            + (plan.ShaderCacheMax && !failed.Contains(KeyShaderCache) ? " 着色器缓存无上限" : "")
                            + (plan.AnselOff && !failed.Contains(KeyAnsel) ? " Ansel关" : "")
                            + (plan.Rebar && !ContainsAny(failed, RebarKeys) ? " ReBAR强开" : "")
                            + (DesiredHasDlss(desired) && !ContainsAny(failed, DlssKeys)
                                ? " " + DlssText(plan.DlssMode) : "")
                            + (plan.BattFull && !failed.Contains(KeyBattFps) ? " 电池满血" : "");
                        if (done.Length > 0) Logger.Log("显卡驱动调优 " + exeName + done);
                    }
                    if (!saved)
                    {
                        failed.Clear();
                        foreach (var item in desired) failed.Add(item.Key);
                        Logger.Log("NVIDIA 驱动调优 保存驱动会话失败 " + exeName);
                    }
                    return failed;
                }
                finally { NvApi.CloseSession(session); }
            }
        }

        private static bool DesiredHasDlss(List<KeyValuePair<string, uint>> desired)
        {
            foreach (var item in desired) if (item.Key == KeyDlssOvr) return true;
            return false;
        }

        internal static string DlssText(string mode)
        {
            if (mode == "latest") return "DLSS 覆写为最新";
            if (mode == "j") return "DLSS 覆写为预设 J";
            if (mode == "k") return "DLSS 覆写为预设 K";
            return "DLSS 覆写";
        }

        internal static bool ContainsAny(List<string> list, string[] keys)
        {
            foreach (string key in keys) if (list.Contains(key)) return true;
            return false;
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
                                Logger.Log("NVIDIA 驱动调优 已恢复 " + exeName + " 的 " + key);
                            }
                            else Logger.Log("NVIDIA 驱动调优 恢复 " + exeName + " 的 " + key + " 失败 快照保留");
                        }
                    }
                    finally { NvApi.CloseSession(session); }
                }
            }
        }

        public static void RestoreKinds(string[] keys)
        {
            foreach (string key in keys) RestoreKind(key);
        }

        public static bool HasSnapshotFor(string key)
        {
            lock (sync)
            {
                string[] games = Settings.LoadStr(ListKey, "")
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string exeName in games)
                    if (ParseSnapshot(Settings.LoadStr(SnapPrefix + exeName, "")).ContainsKey(key))
                        return true;
                return false;
            }
        }

        public static int HealOrphans()
        {
            int healed = 0;
            int stuck = 0;
            foreach (string key in OrphanHealKinds())
            {
                if (!HasSnapshotFor(key)) continue;
                RestoreKind(key);
                if (HasSnapshotFor(key)) stuck++; else healed++;
            }
            if (healed > 0)
                Logger.Log("NVIDIA 驱动调优 启动时补还原 " + healed + " 类残留快照");
            if (stuck > 0)
                Logger.Log("NVIDIA 驱动调优 " + stuck + " 类残留快照暂时无法还原 下次启动继续尝试");
            return healed;
        }

        private static List<string> OrphanHealKinds()
        {
            var kinds = new List<string>();
            if (!Settings.Load("NvMaxPerf", false)) kinds.Add(KeyPState);
            if (Settings.LoadStr("NvFrl", "off") == "off") kinds.Add(KeyFrl);
            string lowLat = Settings.LoadStr("NvLowLat", "off");
            if (lowLat == "off")
            {
                kinds.Add(KeyPreRender); kinds.Add(KeyUllEnable); kinds.Add(KeyLowLatCpl);
            }
            if (!Settings.Load("NvSmoothMotion", false)) kinds.Add(KeySmooth);
            if (!Settings.Load("NvShaderCache", false)) kinds.Add(KeyShaderCache);
            if (!Settings.Load("NvAnselOff", false)) kinds.Add(KeyAnsel);
            if (!Settings.Load("NvRebar", false))
            {
                kinds.Add(KeyRebarFeat); kinds.Add(KeyRebarOpt); kinds.Add(KeyRebarSize);
            }
            if (Settings.LoadStr("NvDlss", "off") == "off")
            {
                kinds.Add(KeyDlssOvr); kinds.Add(KeyDlssPreset);
            }
            if (!Settings.Load("NvBattFull", false)) kinds.Add(KeyBattFps);
            return kinds;
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
