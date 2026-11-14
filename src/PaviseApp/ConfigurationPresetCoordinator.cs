using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace PaviseApp
{
    internal enum ConfigurationPresetItemStatus { Applied, Unchanged, Skipped, Failed, Pending }
    internal sealed class ConfigurationPresetItemResult
    {
        internal string Key, Title, Detail;
        internal ConfigurationPresetItemStatus Status;
        internal bool RestartRequired;
    }
    internal sealed class ConfigurationPresetResult
    {
        internal ConfigurationPresetKind Preset;
        internal readonly List<ConfigurationPresetItemResult> Items = new List<ConfigurationPresetItemResult>();
        internal int Applied, Unchanged, Skipped, Failed, Pending;
        internal bool Cancelled;
        internal string Identity;
        internal string Summary
        {
            get {
                return ConfigurationPresetCatalog.Name(Preset) + PerformancePolicy.Text("：已设置 ", ": configured ") + Applied
                    + PerformancePolicy.Text("，保持 ", ", unchanged ") + Unchanged + PerformancePolicy.Text("，跳过 ", ", skipped ") + Skipped
                    + PerformancePolicy.Text("，失败 ", ", failed ") + Failed
                    + PerformancePolicy.Text("，待验证 ", ", awaiting verification ") + Pending
                    + (Cancelled ? PerformancePolicy.Text("；已停止后续设置", "; further changes cancelled") : "")
                    + PerformancePolicy.Text("。部分项目需重启游戏、应用或电脑后生效。", ". Some changes require restarting the game, app or PC.");
            }
        }
    }
    internal sealed class ConfigurationPresetAvailability
    {
        internal bool Available;
        internal string Detail;
        internal static ConfigurationPresetAvailability Yes() { return new ConfigurationPresetAvailability { Available = true, Detail = "" }; }
        internal static ConfigurationPresetAvailability No(string detail) { return new ConfigurationPresetAvailability { Detail = detail }; }
    }
    internal interface IConfigurationPresetBackend
    {
        string Read(ConfigurationPresetEntry entry);
        ConfigurationPresetAvailability Inspect(ConfigurationPresetEntry entry);
        bool Write(ConfigurationPresetEntry entry, string value, Func<bool> cancelled, out string detail);
        bool SaveIdentity(string identity);
        string LoadIdentity();
    }
    internal interface IConfigurationPresetSnapshot { string ExtraSnapshot(); }
    internal interface IConfigurationPresetWriteState { bool PendingVerification { get; } }

    internal sealed class ConfigurationPresetCoordinator
    {
        private readonly IConfigurationPresetBackend backend;
        internal ConfigurationPresetCoordinator(IConfigurationPresetBackend backend) { this.backend = backend; }
        internal static string IdentityPrefix(ConfigurationPresetKind preset)
        { return preset == ConfigurationPresetKind.Extreme ? "3|extreme|" : "5|default|"; }
        internal string Fingerprint()
        {
            StringBuilder b = new StringBuilder();
            foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries)
            { string value = backend.Read(entry); if (value == null) return null; b.Append(entry.Key).Append('=').Append(value).Append('\n'); }
            IConfigurationPresetSnapshot expanded = backend as IConfigurationPresetSnapshot;
            if (expanded != null) { string extra = expanded.ExtraSnapshot(); if (extra == null) return null; b.Append(extra); }
            using (SHA256 hash = SHA256.Create()) { return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(b.ToString()))); }
        }
        internal ConfigurationPresetKind? CurrentPreset()
        {
            string raw = backend.LoadIdentity(); string[] p = (raw ?? "").Split('|');
            if (p.Length != 3 || (p[1] != "default" && p[1] != "extreme")) return null;
            ConfigurationPresetKind preset = p[1] == "extreme" ? ConfigurationPresetKind.Extreme : ConfigurationPresetKind.Default;
            if (!raw.StartsWith(IdentityPrefix(preset), StringComparison.Ordinal)) return null;
            string actual = Fingerprint();
            return actual != null && actual == p[2] ? (ConfigurationPresetKind?)preset : null;
        }
        internal ConfigurationPresetResult Apply(ConfigurationPresetKind preset, Func<bool> cancelled)
        {
            ConfigurationPresetResult result = new ConfigurationPresetResult { Preset = preset };
            if (preset != ConfigurationPresetKind.Default && preset != ConfigurationPresetKind.Extreme) throw new ArgumentOutOfRangeException("preset");
            Dictionary<string, string> expected = new Dictionary<string, string>();
            // 第一次修改前先使预设身份失效，避免部分应用被误认为完整预设。
            if (!backend.SaveIdentity("")) { result.Failed++; return result; }
            foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries)
            {
                if (cancelled != null && cancelled()) { result.Cancelled = true; break; }
                ConfigurationPresetItemResult item = new ConfigurationPresetItemResult { Key = entry.Key, Title = entry.Title };
                result.Items.Add(item);
                try
                {
                    string before = backend.Read(entry), target = entry.Value(preset), detail;
                    if (before == null) { item.Status = ConfigurationPresetItemStatus.Skipped; item.Detail = PerformancePolicy.Text("配置无法读取，未修改", "Configuration unreadable; unchanged"); expected[entry.Key] = null; continue; }
                    expected[entry.Key] = before;
                    if (target == null)
                    {
                        // 保留用户选择不等于一次应用成功。
                        item.Status = ConfigurationPresetItemStatus.Skipped;
                        item.Detail = PerformancePolicy.Text("保留当前选择", "Current selection preserved");
                        continue;
                    }
                    ConfigurationPresetAvailability availability;
                    try { availability = ConfigurationPresetEntry.IsEnabled(target) ? backend.Inspect(entry) : ConfigurationPresetAvailability.Yes(); }
                    catch (Exception ex) { availability = ConfigurationPresetAvailability.No(PerformancePolicy.Text("适用性检测失败，已跳过：", "Eligibility check failed; skipped: ") + ex.GetType().Name); }
                    if (cancelled != null && cancelled()) { result.Cancelled = true; result.Items.Remove(item); break; }
                    if (availability == null || !availability.Available)
                    {
                        item.Detail = availability == null ? PerformancePolicy.Text("适用性无法核验", "Eligibility could not be verified") : availability.Detail;
                        // 已保存的开启项不受支持时应撤回，不能仅用跳过状态掩盖。
                        string off = target == "on" ? "off" : target == "threshold" ? "default" : "0";
                        if (ConfigurationPresetEntry.IsEnabled(before) && entry.Key != "EnvQuantumProfileV1")
                        {
                            if (!backend.Write(entry, off, cancelled, out detail) || backend.Read(entry) != off)
                            { item.Status = ConfigurationPresetItemStatus.Failed; item.Detail += PerformancePolicy.Text("；原启用配置恢复未完成：", "; prior enabled preference not restored: ") + detail; continue; }
                            expected[entry.Key] = off;
                            IConfigurationPresetWriteState withdrawal = backend as IConfigurationPresetWriteState;
                            if (withdrawal != null && withdrawal.PendingVerification)
                            {
                                item.Status = ConfigurationPresetItemStatus.Pending;
                                item.Detail += PerformancePolicy.Text("；关闭请求已记录，恢复状态待验证：", "; off preference saved; restoration awaits verification: ") + detail;
                                continue;
                            }
                            item.Detail += PerformancePolicy.Text("；已撤回原启用配置", "; prior enabled preference withdrawn");
                        }
                        item.Status = ConfigurationPresetItemStatus.Skipped; continue;
                    }
                    if (cancelled != null && cancelled()) { result.Cancelled = true; result.Items.Remove(item); break; }
                    if (!backend.Write(entry, target, cancelled, out detail))
                    { item.Status = ConfigurationPresetItemStatus.Failed; item.Detail = detail; continue; }
                    if (backend.Read(entry) != target)
                    { item.Status = ConfigurationPresetItemStatus.Failed; item.Detail = PerformancePolicy.Text("配置回读不一致", "Configuration readback differs"); continue; }
                    expected[entry.Key] = target;
                    IConfigurationPresetWriteState writeState = backend as IConfigurationPresetWriteState;
                    item.Status = writeState != null && writeState.PendingVerification ? ConfigurationPresetItemStatus.Pending
                        : before == target ? ConfigurationPresetItemStatus.Unchanged : ConfigurationPresetItemStatus.Applied;
                    item.Detail = detail;
                    item.RestartRequired = entry.RestartRequired && item.Status == ConfigurationPresetItemStatus.Applied;
                }
                catch (Exception ex) { item.Status = ConfigurationPresetItemStatus.Failed; item.Detail = ex.Message; }
            }
            foreach (ConfigurationPresetItemResult item in result.Items)
            {
                if (item.Status == ConfigurationPresetItemStatus.Applied) result.Applied++;
                else if (item.Status == ConfigurationPresetItemStatus.Unchanged) result.Unchanged++;
                else if (item.Status == ConfigurationPresetItemStatus.Skipped) result.Skipped++;
                else if (item.Status == ConfigurationPresetItemStatus.Pending) result.Pending++;
                else result.Failed++;
            }
            if (cancelled != null && cancelled()) result.Cancelled = true;
            if (!result.Cancelled && result.Failed == 0 && result.Pending == 0 && result.Items.Count == ConfigurationPresetCatalog.Entries.Length)
            {
                bool known = true;
                foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries)
                {
                    string value;
                    if (!expected.TryGetValue(entry.Key, out value) || value == null) { known = false; continue; }
                    if (backend.Read(entry) != value)
                    { result.Failed++; break; }
                }
                if (known && result.Failed == 0)
                {
                    string fingerprint = Fingerprint();
                    if (fingerprint != null)
                    {
                        string identity = IdentityPrefix(preset) + fingerprint;
                        if (backend.SaveIdentity(identity)) result.Identity = identity; else result.Failed++;
                    }
                }
            }
            return result;
        }
    }
}
