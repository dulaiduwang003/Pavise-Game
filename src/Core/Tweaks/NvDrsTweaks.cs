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
        public string LowLatMode;
        public bool SmoothMotion;
        public bool ShaderCacheMax;
        public bool AnselOff;
        public bool Rebar;
        public string DlssMode;

        public bool Empty
        {
            get
            {
                return !MaxPerf && !AnselOff && !Rebar
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
        // 限帧功能已下架 KeyFrl 只留作还原旧版本写下的 DRS 键 不再写入
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
                        ? Lang.T("log.nvdrstweaks.1")
                        : Lang.T("log.nvdrstweaks.2") + FormatDriver(SmoothMotionMinDriver())
                            + Lang.T("log.nvdrstweaks.3") + FormatDriver(NvApi.DriverVersion()) + Lang.T("log.nvdrstweaks.4"));
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
                        ? Lang.T("log.nvdrstweaks.5")
                        : Lang.T("log.nvdrstweaks.6")
                            + FormatDriver(NvApi.DriverVersion()) + Lang.T("log.nvdrstweaks.4"));
                }
            }
            return desired;
        }

        internal static string FormatDriver(uint version)
        {
            if (version == 0) return Lang.T("t.nvdrstweaks.7");
            return (version / 100) + "." + (version % 100).ToString("00");
        }

        private static string SatKey(string exeName) { return "NvDrsSat_" + exeName; }

        // 快照值编码 "原值" 或 "原值~已写值" 已写值供还原时做所有权判定
        // 无已写值的旧快照按未知处理走无条件还原(与历史行为一致)
        private const char AppliedSep = '~';

        internal static string SnapOrig(string stored)
        {
            int cut = stored.IndexOf(AppliedSep);
            return cut < 0 ? stored : stored.Substring(0, cut);
        }

        internal static bool TrySnapApplied(string stored, out uint applied)
        {
            applied = 0;
            int cut = stored.IndexOf(AppliedSep);
            return cut >= 0 && uint.TryParse(stored.Substring(cut + 1), out applied);
        }

        private static string SatSignature(List<KeyValuePair<string, uint>> desired)
        {
            var parts = new List<string>();
            foreach (var item in desired) parts.Add(item.Key + "=" + item.Value);
            parts.Sort(StringComparer.Ordinal);
            return "d" + NvApi.DriverVersion() + "|" + string.Join(",", parts.ToArray());
        }

        public static List<string> ApplyForGame(string exePath, NvGamePlan plan)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            if (!NvApi.Available) return null;
            string exeName = Path.GetFileName(exePath);
            if (string.IsNullOrEmpty(exeName)) return null;
            // 空计划=还原该游戏全部已写键 否则 Ultra→On/开→关 后上次会话写的旧键继续生效
            var desired = plan == null || plan.Empty
                ? new List<KeyValuePair<string, uint>>() : BuildDesired(plan);
            if (desired.Count == 0
                && Settings.LoadStr(SnapPrefix + exeName, "").Length == 0) return null;
            string satKey = SatKey(exeName);
            string sig = SatSignature(desired);
            if (desired.Count > 0 && Settings.LoadStr(satKey, "") == sig) return null;
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
                            Logger.Log(Lang.T("log.nvdrstweaks.8") + exeName);
                            return null;
                        }
                    }
                    // 快照里上次写过而本次计划不再包含的键 还原为原值
                    // 所有权守卫:当前值 ≠ 我们当初写的值 = 用户事后在 NVCP 改过 尊重其值只弃快照
                    var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in desired) keep.Add(item.Key);
                    var stale = new List<string>();
                    foreach (string key in snapshot.Keys)
                        if (!keep.Contains(key)) stale.Add(key);

                    bool wrote = false;
                    var failed = new List<string>();
                    var restoredKeys = new List<string>();
                    int yielded = 0;
                    foreach (string key in stale)
                    {
                        string orig = SnapOrig(snapshot[key]);
                        uint applied, cur;
                        if (TrySnapApplied(snapshot[key], out applied)
                            && NvApi.TryGetDword(session, profile, SettingIdOf(key), out cur) == 1
                            && cur != applied)
                        {
                            restoredKeys.Add(key); yielded++;
                            continue;
                        }
                        bool ok = orig == "absent"
                            ? NvApi.TryGetDword(session, profile, SettingIdOf(key), out cur) == 0
                                || NvApi.DeleteSetting(session, profile, SettingIdOf(key))
                            : NvApi.SetDword(session, profile, SettingIdOf(key), ParseUInt(orig));
                        if (ok) { restoredKeys.Add(key); wrote = true; }
                        else failed.Add(key);
                    }

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
                            Logger.Log(Lang.T("log.nvdrstweaks.9") + item.Key + Lang.T("log.nvdrstweaks.10") + exeName
                                + Lang.T("log.nvdrstweaks.11") + status);
                        }
                    }
                    bool saved = !wrote || NvApi.SaveSession(session);
                    if (wrote && saved)
                    {
                        string done = (plan.MaxPerf && !failed.Contains(KeyPState) ? Lang.T("t.nvdrstweaks.12") : "")
                            + (plan.LowLatMode == "ultra" && !ContainsAny(failed, UltraKeys) ? Lang.T("t.nvdrstweaks.14")
                                : plan.LowLatMode == "on" && !failed.Contains(KeyPreRender) ? Lang.T("t.nvdrstweaks.15") : "")
                            + (plan.SmoothMotion && SmoothMotionSupported() && !failed.Contains(KeySmooth)
                                ? Lang.T("t.nvdrstweaks.16") : "")
                            + (plan.ShaderCacheMax && !failed.Contains(KeyShaderCache) ? Lang.T("t.nvdrstweaks.17") : "")
                            + (plan.AnselOff && !failed.Contains(KeyAnsel) ? Lang.T("t.nvdrstweaks.18") : "")
                            + (plan.Rebar && !ContainsAny(failed, RebarKeys) ? Lang.T("t.nvdrstweaks.19") : "")
                            + (DesiredHasDlss(desired) && !ContainsAny(failed, DlssKeys)
                                ? " " + DlssText(plan.DlssMode) : "");
                        if (done.Length > 0) Logger.Log(Lang.T("log.nvdrstweaks.21") + exeName + done);
                    }
                    if (!saved)
                    {
                        failed.Clear();
                        foreach (var item in desired) failed.Add(item.Key);
                        Logger.Log(Lang.T("log.nvdrstweaks.22") + exeName);
                    }
                    // 还原确认落盘后才从快照里遗忘原值 成功写入的键补记已写值供下次所有权判定
                    if (saved)
                    {
                        bool snapDirty = false;
                        if (restoredKeys.Count > 0)
                        {
                            foreach (string key in restoredKeys) snapshot.Remove(key);
                            snapDirty = true;
                            Logger.Log(Lang.T("log.nvdrstweaks.36") + exeName
                                + Lang.T("log.nvdrstweaks.37") + (restoredKeys.Count - yielded) + Lang.T("log.nvdrstweaks.38")
                                + (yielded > 0 ? Lang.T("log.nvdrstweaks.39") + yielded + Lang.T("log.nvdrstweaks.40") : ""));
                        }
                        foreach (var item in desired)
                        {
                            if (failed.Contains(item.Key)) continue;
                            string stored;
                            if (!snapshot.TryGetValue(item.Key, out stored)) continue;
                            string next = SnapOrig(stored) + AppliedSep + item.Value;
                            if (stored != next) { snapshot[item.Key] = next; snapDirty = true; }
                        }
                        if (snapDirty)
                        {
                            if (snapshot.Count == 0)
                            {
                                Settings.SaveStr(SnapPrefix + exeName, "");
                                RemoveFromList(exeName);
                            }
                            else Settings.SaveStr(SnapPrefix + exeName, SerializeSnapshot(snapshot));
                        }
                    }
                    Settings.SaveStr(satKey, failed.Count == 0 && desired.Count > 0 ? sig : "");
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
            if (mode == "latest") return Lang.T("t.nvdrstweaks.23");
            if (mode == "j") return Lang.T("t.nvdrstweaks.24");
            if (mode == "k") return Lang.T("t.nvdrstweaks.25");
            return Lang.T("t.nvdrstweaks.26");
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
                    string stored;
                    if (!snapshot.TryGetValue(key, out stored)) continue;
                    string orig = SnapOrig(stored);
                    IntPtr session;
                    if (!NvApi.TryOpenSession(out session)) return;
                    try
                    {
                        IntPtr profile;
                        if (NvApi.FindOrCreateAppProfile(session, exeName, out profile))
                        {
                            // 所有权守卫:当前值 ≠ 我们当初写的值 = 用户事后在 NVCP 改过 尊重其值只弃快照
                            uint applied, curNow;
                            if (TrySnapApplied(stored, out applied)
                                && NvApi.TryGetDword(session, profile, SettingIdOf(key), out curNow) == 1
                                && curNow != applied)
                            {
                                snapshot.Remove(key);
                                Settings.SaveStr(SatKey(exeName), "");
                                if (snapshot.Count == 0)
                                {
                                    Settings.SaveStr(SnapPrefix + exeName, "");
                                    RemoveFromList(exeName);
                                }
                                else Settings.SaveStr(SnapPrefix + exeName, SerializeSnapshot(snapshot));
                                Logger.Log(Lang.T("log.nvdrstweaks.41") + exeName + Lang.T("log.gamemodeenv.13") + key);
                                continue;
                            }
                            uint cur;
                            bool ok = orig == "absent"
                                ? NvApi.TryGetDword(session, profile, SettingIdOf(key), out cur) == 0
                                    || NvApi.DeleteSetting(session, profile, SettingIdOf(key))
                                : NvApi.SetDword(session, profile, SettingIdOf(key), ParseUInt(orig));
                            if (ok && NvApi.SaveSession(session))
                            {
                                snapshot.Remove(key);
                                Settings.SaveStr(SatKey(exeName), "");
                                if (snapshot.Count == 0)
                                {
                                    Settings.SaveStr(SnapPrefix + exeName, "");
                                    RemoveFromList(exeName);
                                }
                                else Settings.SaveStr(SnapPrefix + exeName, SerializeSnapshot(snapshot));
                                Logger.Log(Lang.T("log.nvdrstweaks.27") + exeName + Lang.T("log.gamemodeenv.13") + key);
                            }
                            else Logger.Log(Lang.T("log.nvdrstweaks.28") + exeName + Lang.T("log.gamemodeenv.13") + key + Lang.T("log.nvdrstweaks.29"));
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
            // NVIDIA 卡已拔走时 DRS 配置随驱动库一起消失 本地快照无处可还原 弃掉防止每次启动报无法还原
            // 只有硬件确实不在才弃 驱动暂时不可用(升级中)仍保留等下次
            if (!NvApi.Available && NvidiaAbsent())
            {
                int dropped = DropAllSnapshots();
                if (dropped > 0)
                    Logger.Log(Lang.T("log.nvdrstweaks.34") + dropped + Lang.T("log.nvdrstweaks.35"));
                return 0;
            }
            int healed = 0;
            int stuck = 0;
            foreach (string key in OrphanHealKinds())
            {
                if (!HasSnapshotFor(key)) continue;
                RestoreKind(key);
                if (HasSnapshotFor(key)) stuck++; else healed++;
            }
            if (healed > 0)
                Logger.Log(Lang.T("log.nvdrstweaks.30") + healed + Lang.T("log.nvdrstweaks.31"));
            if (stuck > 0)
                Logger.Log(Lang.T("log.nvdrstweaks.32") + stuck + Lang.T("log.nvdrstweaks.33"));
            return healed;
        }

        private static bool NvidiaAbsent()
        {
            GpuAdapter[] all = GpuInventory.Adapters();
            if (all == null || all.Length == 0) return false;
            foreach (GpuAdapter a in all) if (a.Vendor == GpuVendor.Nvidia) return false;
            return true;
        }

        private static int DropAllSnapshots()
        {
            lock (sync)
            {
                string[] games = Settings.LoadStr(ListKey, "")
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                int n = 0;
                foreach (string exeName in games)
                {
                    if (Settings.LoadStr(SnapPrefix + exeName, "").Length > 0) n++;
                    Settings.SaveStr(SnapPrefix + exeName, "");
                    Settings.SaveStr(SatKey(exeName), "");
                }
                Settings.SaveStr(ListKey, "");
                return n;
            }
        }

        private static List<string> OrphanHealKinds()
        {
            var kinds = new List<string>();
            if (!Settings.Load("NvMaxPerf", false)) kinds.Add(KeyPState);
            kinds.Add(KeyFrl);
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
