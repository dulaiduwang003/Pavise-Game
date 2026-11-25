using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    // 显卡设备名称的读写和设备重启 测试换成内存实现 正式版走 64 位注册表视图和 SetupAPI
    internal interface IGpuNameStore
    {
        // 设备键不存在时返回 false 名称值缺失时 value 为 null
        bool TryRead(string instanceId, out string value, out RegistryValueKind kind);

        // 只改已有设备键里的名称 不新建设备键
        void Write(string instanceId, string value, RegistryValueKind kind);

        // 驱动登记的型号 取不到时返回 null
        string DriverName(string instanceId);

        // 像装驱动那样停下再启动设备 DXGI 的适配器名称只在设备启动时读一次
        // 失败时抛异常 系统要求重启电脑才能完成时 rebootRequired 为 true
        void RestartDevice(string instanceId, out bool rebootRequired);
    }

    internal enum GpuSpoofTier
    {
        None, Low1, Low2, Low3, High1, High2, Flagship
    }

    internal sealed class GpuSpoofDevice
    {
        internal string InstanceId;
        internal string RealName;
        internal string CurrentName;
        internal string VisibleName;
        internal string FakeName;
        internal string ExpectedName;
        internal bool Target;
        internal bool Amd;
        internal bool CanRestore;

        internal string RawName;
        internal string DriverName;
        internal RegistryValueKind Kind;
        internal bool Present;
        internal bool Listed;
        internal bool UnverifiedName;
        internal bool Recorded;
        internal bool Spoofed;
    }

    internal sealed class GpuSpoofSnapshot
    {
        internal int Targets;
        internal int Spoofed;
        internal int Tracked;
        internal int Records;
        internal int ExcludedSpoofed;
        internal int UnexpectedSpoofed;
        internal int OriginalNamesAtTier;
        internal bool Uncertain;
        internal bool RecoveryRestartPending;
        internal GpuSpoofTier? SelectedTier = GpuSpoofTier.None;
        internal bool Mobile;
        internal bool? GameSeesFake;
        internal bool? ReadbackMatches;
        internal GpuSpoofDevice[] Devices = new GpuSpoofDevice[0];

        internal string RealName = "";
        internal string VisibleName = "";
        internal string Error = "";

        internal bool Available { get { return Targets > 0; } }
        internal bool On { get { return Spoofed > 0; } }
        internal bool Partial
        {
            get { return On && (!SelectedTier.HasValue || Spoofed + OriginalNamesAtTier - ExcludedSpoofed - UnexpectedSpoofed < Targets); }
        }
        internal bool CanRestore
        {
            get { return On || Records > 0 || Array.Exists(Devices, device => device.UnverifiedName); }
        }
        internal bool CanRestart { get { return Available || CanRestore || RecoveryRestartPending; } }

        internal bool IsTierAvailable(GpuSpoofTier tier)
        {
            if (tier == GpuSpoofTier.None) return CanRestore;
            return Array.Exists(Devices, device => GpuSpoof.CanUseTier(device, Mobile, tier));
        }
    }

    internal static class GpuSpoof
    {
        internal const string FakeName = "NVIDIA GeForce GTX 750 Ti";

        internal const string AmdFakeName = "AMD Radeon (TM) R7 360 Series";

        internal const string MobileFakeName = "NVIDIA GeForce GTX 950M";

        internal const string MobileAmdFakeName = "Radeon (TM) R9 M470X";

        private static readonly string[][] names =
        {
            new[] { FakeName, "NVIDIA GeForce GTX 1050 Ti", "NVIDIA GeForce GTX 1650",
                "NVIDIA GeForce RTX 4080", "NVIDIA GeForce RTX 5080", "NVIDIA GeForce RTX 5090" },
            new[] { MobileFakeName, "NVIDIA GeForce GTX 1050 Ti", "NVIDIA GeForce GTX 1650",
                "NVIDIA GeForce RTX 4080 Laptop GPU", "NVIDIA GeForce RTX 5080 Laptop GPU", "NVIDIA GeForce RTX 5090 Laptop GPU" },
            new[] { AmdFakeName, "Radeon RX 560 Series", "AMD Radeon RX 6400",
                "AMD Radeon RX 7800 XT", "AMD Radeon RX 9070 XT", "AMD Radeon RX 7900 XTX" },
            new[] { MobileAmdFakeName, "Radeon RX 5500M", "AMD Radeon RX 6500M",
                "AMD Radeon RX 6800M", "AMD Radeon RX 6850M XT", "AMD Radeon RX 7900M" }
        };

        internal const string LedgerKey = "DeltaForceGpuNameV1";

        private const char EntrySep = '\u001f';

        private const char FieldSep = '\u001e';

        private const string NameValue = "DeviceDesc";

        private const int RecoveryReadbackSeconds = 15;

        private static readonly object sync = new object();

        // 保留已恢复的设备直到重启和读回完成
        private static readonly Dictionary<string, long> restartedAfterRestore =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

#if PAVISE_SELFTEST
        internal static IGpuNameStore StoreForTest;

        internal static Func<Dictionary<string, string>> VisibleNamesForTest;

        internal static LaboratoryEppEligibility.ChassisCache ChassisForTest;

        internal static Func<bool> HandheldForTest;

        internal static void ResetRecoveryForTest()
        {
            lock (sync) restartedAfterRestore.Clear();
        }

        private static IGpuNameStore Store
        {
            get
            {
                if (StoreForTest == null) throw new InvalidOperationException("Missing in-memory GPU name store");
                return StoreForTest;
            }
        }

        private static Dictionary<string, string> VisibleNames()
        {
            return VisibleNamesForTest == null ? null : VisibleNamesForTest();
        }
#else
        private static readonly IGpuNameStore Store = new RegistryGpuNameStore();

        private static Dictionary<string, string> VisibleNames()
        {
            Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string failure;
            return OverviewGpuProbe.TryReadAdapterNames(names, out failure) || names.Count > 0 ? names : null;
        }
#endif

        private static bool UsesMobileNames()
        {
            return IsHandheld() || ChassisKind() == LaboratoryEppDeviceKind.Laptop;
        }

        private static bool IsHandheld()
        {
#if PAVISE_SELFTEST
            return HandheldForTest != null && HandheldForTest();
#else
            return GamingHandheldDetection.Detected;
#endif
        }

        private static LaboratoryEppDeviceKind ChassisKind()
        {
#if PAVISE_SELFTEST
            return ChassisForTest == null ? LaboratoryEppDeviceKind.Unknown : ChassisForTest.Read();
#else
            return LaboratoryEppEligibility.ReadChassisKind();
#endif
        }

        internal static string TargetName(bool amd, bool mobile, GpuSpoofTier tier)
        {
            return tier >= GpuSpoofTier.Low1 && tier <= GpuSpoofTier.Flagship
                ? names[(amd ? 2 : 0) + (mobile ? 1 : 0)][(int)tier - 1] : null;
        }

        private static string ComparableName(string value)
        {
            string name = DisplayPart(value);
            if (name.StartsWith("AMD Radeon", StringComparison.OrdinalIgnoreCase)) name = name.Substring(4);
            const string marker = "(TM)";
            int markerIndex = name.StartsWith("Radeon", StringComparison.OrdinalIgnoreCase)
                ? name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) : -1;
            if (markerIndex > 0)
                name = name.Substring(0, markerIndex).TrimEnd() + name.Substring(markerIndex + marker.Length);
            return name;
        }

        private static bool SameName(string left, string right)
        {
            return string.Equals(ComparableName(left), ComparableName(right), StringComparison.OrdinalIgnoreCase);
        }

        private static GpuSpoofTier TierOf(string value)
        {
            string name = ComparableName(value);
            foreach (string[] series in names)
                for (int i = 0; i < series.Length; i++)
                    if (string.Equals(name, ComparableName(series[i]), StringComparison.OrdinalIgnoreCase))
                        return (GpuSpoofTier)(i + 1);
            return GpuSpoofTier.None;
        }

        internal static bool CanUseTier(GpuSpoofDevice device, bool mobile, GpuSpoofTier tier)
        {
            string name = TargetName(device.Amd, mobile, tier);
            return device.Target && !device.UnverifiedName && name != null
                && !SameName(device.RealName, name) && !SameName(device.DriverName, name);
        }

        private sealed class Entry
        {
            internal string InstanceId;
            internal string Original;
            internal RegistryValueKind Kind;
            internal string Expected;
        }

        private sealed class Change
        {
            internal GpuSpoofDevice Device;
            internal Entry After;
            internal string Name;
            internal RegistryValueKind Kind;

            internal bool WritesName
            {
                get { return Name != null && (Name != Device.RawName || Kind != Device.Kind); }
            }
        }


        private static List<GpuSpoofDevice> ReadDevices(Dictionary<string, Entry> entries,
            out bool uncertain, out HashSet<string> sharedKeys)
        {
            var adapters = new Dictionary<string, GpuAdapter>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            sharedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GpuAdapter adapter in GpuInventory.CachedAdapters())
            {
                if (adapter == null || !GpuInventory.IsPciAdapter(adapter.InstanceId) || adapters.ContainsKey(adapter.InstanceId))
                    continue;
                adapters.Add(adapter.InstanceId, adapter);
                string key = DeviceKey(adapter.InstanceId);
                if (key != null && !keys.Add(key)) sharedKeys.Add(key);
            }
            foreach (string id in entries.Keys)
                if (!adapters.ContainsKey(id)) adapters.Add(id, null);
            foreach (string id in restartedAfterRestore.Keys)
                if (!adapters.ContainsKey(id)) adapters.Add(id, null);

            uncertain = false;
            var devices = new List<GpuSpoofDevice>();
            foreach (KeyValuePair<string, GpuAdapter> pair in adapters)
            {
                string id = pair.Key;
                GpuAdapter adapter = pair.Value;
                GpuVendor vendor = GpuInventory.VendorOf(id);
                var device = new GpuSpoofDevice
                {
                    InstanceId = id,
                    Amd = vendor == GpuVendor.Amd,
                    Listed = adapter != null
                };
                device.Present = Store.TryRead(id, out device.RawName, out device.Kind);
                device.CurrentName = DisplayPart(device.RawName);
                string driver = Store.DriverName(id);
                device.DriverName = DisplayPart(string.IsNullOrWhiteSpace(driver) && adapter != null ? adapter.DriverName : driver);
                Entry entry;
                device.Recorded = entries.TryGetValue(id, out entry);

                bool requiresReport = device.Amd || vendor == GpuVendor.Intel;
                if (adapter != null)
                {
                    uncertain |= requiresReport && !adapter.IntegratedKnown;
                    device.Target = !adapter.Integrated && (vendor == GpuVendor.Nvidia
                        || (requiresReport && adapter.IntegratedKnown));
                }

                string original = entry == null ? device.DriverName : entry.Original;
                bool knownName = device.Present && IsFake(device.RawName);
                device.UnverifiedName = knownName && entry == null && device.DriverName.Length == 0;
                device.Spoofed = knownName && !device.UnverifiedName
                    && !SameName(device.CurrentName, original) && !SameName(device.CurrentName, device.DriverName);
                device.ExpectedName = entry == null ? null : entry.Expected;
                if (device.ExpectedName == null && device.Spoofed) device.ExpectedName = device.CurrentName;
                device.CanRestore = device.Spoofed || device.Recorded || device.UnverifiedName;
                if (!device.Target && !device.CanRestore && !restartedAfterRestore.ContainsKey(id)) continue;

                device.RealName = DisplayPart(original);
                if (device.RealName.Length == 0 && adapter != null && !IsFake(adapter.Name))
                    device.RealName = DisplayPart(adapter.Name);
                devices.Add(device);
            }
            return devices;
        }

        public static GpuSpoofSnapshot Read(bool refreshInventory = false)
        {
            if (refreshInventory) GpuInventory.Refresh();
            GpuSpoofSnapshot snapshot = new GpuSpoofSnapshot();
            lock (sync)
            {
                try
                {
                    HashSet<string> sharedKeys;
                    List<GpuSpoofDevice> devices = ReadDevices(LoadLedger(), out snapshot.Uncertain, out sharedKeys);

                    snapshot.Devices = devices.ToArray();
                    if (devices.Count == 0 && GpuInventory.CachedReadFailed)
                        snapshot.Error = Lang.T("env.gpu.spoof.state.error");
                    Dictionary<string, string> visible = devices.Count > 0 ? VisibleNames() : null;
                    snapshot.Mobile = devices.Count > 0 && UsesMobileNames();
                    GpuSpoofTier? selected = GpuSpoofTier.None;
                    bool unknown = devices.Count == 0;
                    bool mismatch = false;
                    foreach (GpuSpoofDevice device in devices)
                    {
                        if (device.Listed && (!device.Present || device.RawName == null))
                            snapshot.Error = Lang.T("env.gpu.spoof.gpu.err.read") + " " + device.InstanceId;
                        device.FakeName = TargetName(device.Amd, snapshot.Mobile, GpuSpoofTier.Low1);
                        device.ExpectedName = device.ExpectedName ?? device.FakeName;
                        bool expectedWritten = SameName(device.CurrentName, device.ExpectedName);
                        if (device.Target) snapshot.Targets++;
                        if (device.Recorded) snapshot.Records++;
                        if (device.Spoofed)
                        {
                            snapshot.Spoofed++;
                            GpuSpoofTier tier = TierOf(device.CurrentName);
                            if (selected == GpuSpoofTier.None) selected = tier;
                            else if (selected != tier) selected = null;
                            if (device.Recorded) snapshot.Tracked++;
                            if (!device.Target) snapshot.ExcludedSpoofed++;
                            else if (!expectedWritten) snapshot.UnexpectedSpoofed++;
                        }

                        string key = DeviceKey(device.InstanceId);
                        string seen = null;
                        bool readback = visible != null && key != null && visible.TryGetValue(key, out seen)
                            && !string.IsNullOrWhiteSpace(seen);
                        bool seenSpoofed = IsFake(seen) && !device.UnverifiedName
                            && !SameName(seen, device.RealName) && !SameName(seen, device.DriverName);
                        bool shared = sharedKeys.Contains(key);
                        device.VisibleName = readback && !shared ? DisplayPart(seen) : device.CurrentName;
                        if (snapshot.RealName.Length == 0) snapshot.RealName = device.RealName;
                        if (snapshot.VisibleName.Length == 0) snapshot.VisibleName = device.VisibleName;
                        if (readback)
                        {
                            if (seenSpoofed) snapshot.GameSeesFake = true;
                            else if (!snapshot.GameSeesFake.HasValue) snapshot.GameSeesFake = false;
                        }

                        // 同型号设备共用 DXGI 键
                        bool known = device.Present && !device.UnverifiedName && device.CurrentName.Length > 0 && readback && !shared;
                        long restartDeadline;
                        bool restoring = restartedAfterRestore.TryGetValue(device.InstanceId, out restartDeadline);
                        bool matches = seenSpoofed == device.Spoofed
                            && (!(device.Spoofed || restoring) || SameName(device.CurrentName, device.VisibleName));
                        if (!known) unknown = true;
                        else if (!matches) mismatch = true;

                        if (restoring)
                        {
                            if (restartDeadline != 0 && (known ? matches : Stopwatch.GetTimestamp() >= restartDeadline))
                                restartedAfterRestore.Remove(device.InstanceId);
                            else snapshot.RecoveryRestartPending = true;
                        }
                    }
                    snapshot.SelectedTier = selected;
                    if (selected.HasValue && selected != GpuSpoofTier.None)
                        snapshot.OriginalNamesAtTier = devices.FindAll(device => device.Target && !device.Spoofed
                            && !device.UnverifiedName && SameName(device.CurrentName,
                                TargetName(device.Amd, snapshot.Mobile, selected.Value))).Count;
                    if (mismatch) snapshot.ReadbackMatches = false;
                    else if (!unknown) snapshot.ReadbackMatches = true;
                }
                catch (Exception ex)
                {
                    snapshot.Error = ex.Message;
                }
            }
            return snapshot;
        }

#if PAVISE_SELFTEST
        internal static bool Apply(GpuSpoofTier tier, out string error, Func<bool> sessionBlocked = null)
        {
            bool rebootRequired;
            return ChangeSelection(tier, false, out rebootRequired, out error, sessionBlocked);
        }
#endif

        internal static bool ChangeTier(GpuSpoofTier tier, out bool rebootRequired, out string error,
            Func<bool> sessionBlocked = null)
        {
            return ChangeSelection(tier, true, out rebootRequired, out error, sessionBlocked);
        }

        private static bool ChangeSelection(GpuSpoofTier tier, bool restart, out bool rebootRequired,
            out string error, Func<bool> sessionBlocked)
        {
            rebootRequired = false;
            error = "";
            GpuInventory.Refresh();
            lock (sync)
            {
                if (GameBlocksChange(ref error, sessionBlocked)) return false;
                Dictionary<string, Entry> entries = null;
                var attempted = new List<Change>();
                var restarted = new List<string>();
                bool journalChanged = false;
                try
                {
                    entries = LoadLedger();
                    bool uncertain;
                    HashSet<string> sharedKeys;
                    List<GpuSpoofDevice> devices = ReadDevices(entries, out uncertain, out sharedKeys);
                    bool restore = tier == GpuSpoofTier.None;
                    bool mobile = !restore && UsesMobileNames();
                    if (!restore && !devices.Exists(device => CanUseTier(device, mobile, tier)))
                    {
                        error = devices.Exists(device => device.Target)
                            ? Lang.T("env.gpu.spoof.gpu.err.same") : NoTargetError(uncertain);
                        return false;
                    }

                    var changes = new List<Change>();
                    var journal = new Dictionary<string, Entry>(entries, entries.Comparer);
                    foreach (GpuSpoofDevice device in devices)
                    {
                        Entry before;
                        entries.TryGetValue(device.InstanceId, out before);
                        Change change = restore ? PlanRestore(device, before) : PlanSpoof(device, before, mobile, tier);
                        if (change == null) continue;
                        changes.Add(change);
                        if (change.After != null) journal[device.InstanceId] = change.After;
                    }
                    if (changes.Count == 0) return true;
                    if (GameBlocksChange(ref error, sessionBlocked)) return false;
                    journalChanged = changes.Exists(change => change.After != null);
                    if (journalChanged) SaveLedger(journal);

                    var restartIds = new List<string>();
                    foreach (Change change in changes)
                    {
                        if (!change.WritesName) continue;
                        if (GameBlocksChange(ref error, sessionBlocked)) throw new InvalidOperationException(error);
                        attempted.Add(change);
                        string id = change.Device.InstanceId;
                        Store.Write(id, change.Name, change.Kind);
                        string check;
                        RegistryValueKind kind;
                        if (!Store.TryRead(id, out check, out kind) || check != change.Name || kind != change.Kind)
                            throw new InvalidOperationException(Lang.T("env.gpu.spoof.gpu.err.verify") + " " + id);
                        restartIds.Add(id);
                    }

                    if (restart && !RestartDevices(restartIds, restarted, out rebootRequired, ref error, sessionBlocked))
                        throw new InvalidOperationException(error);

                    bool removesRecords = false;
                    foreach (Change change in changes)
                    {
                        string id = change.Device.InstanceId;
                        if (change.After == null) removesRecords |= journal.Remove(id);
                    }
                    if (removesRecords)
                    {
                        journalChanged = true;
                        SaveLedger(journal);
                    }
                    foreach (Change change in changes)
                    {
                        if (!change.WritesName) continue;
                        string id = change.Device.InstanceId;
                        if (change.After != null) restartedAfterRestore.Remove(id);
                        else restartedAfterRestore[id] = restart && !rebootRequired
                            ? Stopwatch.GetTimestamp() + Stopwatch.Frequency * RecoveryReadbackSeconds : 0;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    if (entries != null && (journalChanged || attempted.Count != 0))
                        Rollback(attempted, entries, restarted, sessionBlocked);
                    string message = ex.Message;
                    Logger.Log(() => Logger.Format("显卡伪装 更改失败 {0}", "GPU name change failed {0}", message));
                    return false;
                }
            }
        }

        private static Change PlanSpoof(GpuSpoofDevice device, Entry before, bool mobile, GpuSpoofTier tier)
        {
            if (!device.Target) return null;
            string name = TargetName(device.Amd, mobile, tier);
            if (!CanUseTier(device, mobile, tier))
                return device.UnverifiedName || !device.Spoofed ? null : PlanRestore(device, before);
            string id = device.InstanceId;
            if (!device.Present || device.RawName == null)
                throw new InvalidOperationException(Lang.T("env.gpu.spoof.gpu.err.read") + " " + id);
            if (string.Equals(device.RawName, name, StringComparison.Ordinal))
                return null;

            bool keepsOriginal = before != null && (device.Spoofed || device.RawName == before.Original);
            string original = keepsOriginal ? before.Original : device.Spoofed ? device.DriverName : device.RawName;
            RegistryValueKind kind = keepsOriginal ? before.Kind : device.Kind;
            if (!ValidField(id) || !ValidField(original) || !StringKind(kind))
                throw new InvalidOperationException(Lang.T("env.gpu.spoof.gpu.err.kind") + " " + id);
            return new Change
            {
                Device = device,
                After = new Entry { InstanceId = id, Original = original, Kind = kind, Expected = name },
                Name = name,
                Kind = kind
            };
        }

        private static Change PlanRestore(GpuSpoofDevice device, Entry before)
        {
            if (!device.CanRestore) return null;
            string id = device.InstanceId;
            if (device.UnverifiedName || (device.Listed && !device.Present) || (device.Present && device.RawName == null))
                throw new InvalidOperationException(Lang.T("env.gpu.spoof.gpu.err.restore") + " " + id);
            string name = null;
            RegistryValueKind kind = device.Kind;
            if (device.Spoofed)
            {
                name = before == null ? device.DriverName : before.Original;
                kind = before == null ? RegistryValueKind.String : before.Kind;
                if (name.Length == 0)
                    throw new InvalidOperationException(Lang.T("env.gpu.spoof.gpu.err.restore") + " " + id);
            }
            return new Change { Device = device, Name = name, Kind = kind };
        }


        private static string NoTargetError(bool uncertain)
        {
            return Lang.T(uncertain ? "env.gpu.spoof.gpu.err.uncertain" : "env.gpu.spoof.gpu.err.none");
        }

        public static bool Restore(out string error, Func<bool> sessionBlocked = null)
        {
            return RestoreNames(false, out error, sessionBlocked);
        }

        private static bool RestoreNames(bool cleanup, out string error, Func<bool> sessionBlocked)
        {
            error = "";
            GpuInventory.Refresh();
            lock (sync)
            {
                if (!cleanup && GameBlocksChange(ref error, sessionBlocked)) return false;
                bool allRestored = true;
                try
                {
                    Dictionary<string, Entry> entries = LoadLedger();
                    bool uncertain;
                    HashSet<string> sharedKeys;
                    foreach (GpuSpoofDevice device in ReadDevices(entries, out uncertain, out sharedKeys))
                    {
                        Entry entry;
                        entries.TryGetValue(device.InstanceId, out entry);
                        try
                        {
                            Change change = PlanRestore(device, entry);
                            if (change == null) continue;
                            if (!cleanup && GameBlocksChange(ref error, sessionBlocked)) return false;
                            if (change.WritesName && !WriteBack(device.InstanceId, change.Name, change.Kind))
                                throw new InvalidOperationException(Lang.T("env.gpu.spoof.gpu.err.restore") + " " + device.InstanceId);
                            if (!cleanup && (change.WritesName || (device.Present && !device.Target)))
                                restartedAfterRestore[device.InstanceId] = 0;
                            if (entry == null) continue;
                            entries.Remove(device.InstanceId);
                            SaveLedger(entries);
                        }
                        catch (InvalidOperationException ex)
                        {
                            error = ex.Message;
                            allRestored = false;
                        }
                    }
                    return allRestored;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    string message = ex.Message;
                    Logger.Log(() => Logger.Format("显卡伪装 恢复失败 {0}", "GPU name restore failed {0}", message));
                    return false;
                }
            }
        }

        // 三角洲客户端在运行时不重启显卡 游戏会丢失显卡设备直接退出
        internal static readonly string[] GameProcessNames = new string[2] { "DeltaForceClient-Win64-Shipping", "DeltaForceClient" };

#if PAVISE_SELFTEST
        internal static Func<bool> GameRunningForTest;
#endif

        public static bool GameRunning()
        {
#if PAVISE_SELFTEST
            if (GameRunningForTest != null) return GameRunningForTest();
#endif
            foreach (string name in GameProcessNames)
            {
                Process[] found = null;
                try
                {
                    found = Process.GetProcessesByName(name);
                    if (found.Length > 0) return true;
                }
                catch
                {
                }
                finally
                {
                    if (found != null)
                    {
                        foreach (Process process in found) process.Dispose();
                    }
                }
            }
            return false;
        }

        private static bool GameBlocksChange(ref string error, Func<bool> sessionBlocked)
        {
            if ((sessionBlocked == null || !sessionBlocked()) && !GameRunning()) return false;
            error = Lang.T("env.gpu.spoof.gpu.err.running");
            return true;
        }

        // 重启独立显卡 让 DXGI 立即读到当前名称 不用重启电脑
        // 这张显卡上的屏幕会黑几秒 正在用它的程序会丢失显卡设备 浏览器和桌面会自己恢复
        public static bool RestartAdapters(out bool rebootRequired, out string error, Func<bool> sessionBlocked = null)
        {
            rebootRequired = false;
            error = "";
            GpuInventory.Refresh();
            lock (sync)
            {
                try
                {
                    if (GameBlocksChange(ref error, sessionBlocked)) return false;
                    bool uncertain;
                    HashSet<string> sharedKeys;
                    List<GpuSpoofDevice> devices = ReadDevices(LoadLedger(), out uncertain, out sharedKeys);
                    var ids = new List<string>();
                    foreach (GpuSpoofDevice device in devices)
                    {
                        if (device.Present && (device.Target || device.CanRestore || restartedAfterRestore.ContainsKey(device.InstanceId)))
                            ids.Add(device.InstanceId);
                    }
                    if (ids.Count == 0)
                    {
                        error = NoTargetError(uncertain);
                        return false;
                    }
                    return RestartDevices(ids, null, out rebootRequired, ref error, sessionBlocked);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    string message = ex.Message;
                    Logger.Log(() => Logger.Format("显卡伪装 重启显卡设备失败 {0}", "GPU name spoof could not restart the GPU device {0}", message));
                    return false;
                }
            }
        }

        private static bool RestartDevices(List<string> ids, List<string> restarted, out bool rebootRequired,
            ref string error, Func<bool> sessionBlocked)
        {
            rebootRequired = false;
            if (ids.Count == 0) return true;
            GpuInventory.BeginAdapterRestart();
            try
            {
                foreach (string id in ids)
                {
                    if (GameBlocksChange(ref error, sessionBlocked)) return false;
                    bool needsReboot;
                    Store.RestartDevice(id, out needsReboot);
                    rebootRequired |= needsReboot;
                    if (restarted != null && !needsReboot) restarted.Add(id);
                    if (!needsReboot && restartedAfterRestore.ContainsKey(id))
                        restartedAfterRestore[id] = Stopwatch.GetTimestamp() + Stopwatch.Frequency * RecoveryReadbackSeconds;
                    Logger.Log(() => Logger.Format("显卡伪装 已重启显卡设备 {0} 需要重启电脑 {1}",
                        "GPU name spoof restarted the GPU device {0} reboot required={1}", id, needsReboot));
                }
                return true;
            }
            finally
            {
                GpuInventory.EndAdapterRestart();
            }
        }

        public static bool HasResidue()
        {
            try
            {
                lock (sync)
                {
                    Dictionary<string, Entry> entries = LoadLedger();
                    if (entries.Count != 0) return true;
#if PAVISE_SELFTEST
                    if (StoreForTest == null) return false;
#endif
                    GpuInventory.Refresh();
                    bool uncertain;
                    HashSet<string> sharedKeys;
                    return ReadDevices(entries, out uncertain, out sharedKeys).Exists(device => device.CanRestore);
                }
            }
            catch
            {
                return true;
            }
        }

        public static bool RestoreAll()
        {
            string error;
            // 卸载恢复设备名称 不重启显卡
            return RestoreNames(true, out error, null);
        }

        internal static bool IsFake(string value)
        {
            return TierOf(value) != GpuSpoofTier.None;
        }


        // 驱动写的名称以 @ 开头 前半段引用 INF 里的字符串 分号后面才是显示名
        internal static string DisplayPart(string value)
        {
            if (value == null) return "";
            if (value.StartsWith("@", StringComparison.Ordinal))
            {
                int split = value.LastIndexOf(';');
                if (split >= 0) return value.Substring(split + 1).Trim();
            }
            return value.Trim();
        }

        // 设备实例路径里的 VEN DEV SUBSYS 和 DXGI 报告的设备号对得上
        internal static string DeviceKey(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            int ven = instanceId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
            int dev = instanceId.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
            int sub = instanceId.IndexOf("SUBSYS_", StringComparison.OrdinalIgnoreCase);
            if (ven < 0 || dev < 0 || sub < 0 || ven + 8 > instanceId.Length || dev + 8 > instanceId.Length || sub + 15 > instanceId.Length) return null;
            return (instanceId.Substring(ven, 8) + "&" + instanceId.Substring(dev, 8) + "&" + instanceId.Substring(sub, 15)).ToUpperInvariant();
        }

        private static bool WriteBack(string instanceId, string value, RegistryValueKind kind)
        {
            try
            {
                Store.Write(instanceId, value, kind);
                string check;
                RegistryValueKind checkKind;
                return Store.TryRead(instanceId, out check, out checkKind)
                    && string.Equals(check, value, StringComparison.Ordinal) && checkKind == kind;
            }
            catch
            {
                return false;
            }
        }

        private static void Rollback(List<Change> attempted, Dictionary<string, Entry> original,
            List<string> restarted, Func<bool> sessionBlocked)
        {
            var reload = new List<string>();
            try
            {
                Dictionary<string, Entry> recovery = LoadLedger();
                var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Change change in attempted)
                {
                    GpuSpoofDevice device = change.Device;
                    string id = device.InstanceId;
                    string current;
                    RegistryValueKind kind;
                    bool read = Store.TryRead(id, out current, out kind);
                    bool reverted = read && ((current == device.RawName && kind == device.Kind)
                        || (current == change.Name && WriteBack(id, device.RawName, device.Kind)));
                    if (reverted)
                    {
                        if (restarted.Contains(id)) reload.Add(id);
                    }
                    else failed.Add(id);
                }
                var entries = new Dictionary<string, Entry>(original, original.Comparer);
                foreach (string id in failed)
                {
                    Entry entry;
                    if (recovery.TryGetValue(id, out entry)) entries[id] = entry;
                }
                SaveLedger(entries);
            }
            catch
            {
            }

            foreach (string id in reload) restartedAfterRestore[id] = 0;
            try
            {
                string error = "";
                bool rebootRequired;
                RestartDevices(reload, null, out rebootRequired, ref error, sessionBlocked);
            }
            catch
            {
            }
        }

        private static bool StringKind(RegistryValueKind kind)
        {
            return kind == RegistryValueKind.String || kind == RegistryValueKind.ExpandString;
        }


        private static bool ValidField(string value)
        {
            return value != null && value.IndexOf(EntrySep) < 0 && value.IndexOf(FieldSep) < 0;
        }

        // 记录读不出来或格式不对时报错 不能当成没有记录
        private static Dictionary<string, Entry> LoadLedger()
        {
            string value;
            if (!Settings.TryLoadStr(LedgerKey, out value))
            {
                throw new InvalidOperationException("Cannot read the GPU name recovery ledger");
            }
            Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in (value ?? "").Split(new char[1] { EntrySep }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = item.Split(FieldSep);
                int kind;
                if ((fields.Length != 3 && fields.Length != 4) || !GpuInventory.IsPciAdapter(fields[0])
                    || !int.TryParse(fields[2], out kind)
                    || !StringKind((RegistryValueKind)kind)
                    || (fields.Length == 4 && !IsFake(fields[3])))
                {
                    throw new InvalidOperationException("GPU name recovery ledger is malformed");
                }
                entries[fields[0]] = new Entry
                {
                    InstanceId = fields[0],
                    Original = fields[1],
                    Kind = (RegistryValueKind)kind,
                    Expected = fields.Length == 4 ? DisplayPart(fields[3]) : null
                };
            }
            return entries;
        }

        private static void SaveLedger(Dictionary<string, Entry> entries)
        {
            List<string> items = new List<string>();
            foreach (Entry entry in entries.Values)
            {
                string item = entry.InstanceId + FieldSep + entry.Original + FieldSep + ((int)entry.Kind).ToString();
                items.Add(entry.Expected == null ? item : item + FieldSep + entry.Expected);
            }
            string joined = string.Join(EntrySep.ToString(), items.ToArray());
            string confirmed;
            if (!Settings.SaveStr(LedgerKey, joined) || !Settings.TryLoadStr(LedgerKey, out confirmed) || confirmed != joined)
            {
                throw new InvalidOperationException("Cannot save the GPU name recovery ledger");
            }
        }

        private sealed class RegistryGpuNameStore : IGpuNameStore
        {
            private const string EnumKey = "SYSTEM\\CurrentControlSet\\Enum\\";

            private const string ClassKey = "SYSTEM\\CurrentControlSet\\Control\\Class\\";

            private static RegistryKey Machine()
            {
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            }

            public bool TryRead(string instanceId, out string value, out RegistryValueKind kind)
            {
                value = null;
                kind = RegistryValueKind.String;
                using (RegistryKey machine = Machine())
                using (RegistryKey key = machine.OpenSubKey(EnumKey + instanceId))
                {
                    if (key == null) return false;
                    object raw = key.GetValue(NameValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (raw == null) return true;
                    kind = key.GetValueKind(NameValue);
                    value = raw as string;
                    return true;
                }
            }

            public void Write(string instanceId, string value, RegistryValueKind kind)
            {
                using (RegistryKey machine = Machine())
                using (RegistryKey key = machine.OpenSubKey(EnumKey + instanceId, true))
                {
                    if (key == null)
                    {
                        throw new InvalidOperationException("GPU device key is missing");
                    }
                    key.SetValue(NameValue, value, kind);
                }
            }

            public string DriverName(string instanceId)
            {
                using (RegistryKey machine = Machine())
                {
                    string driver;
                    using (RegistryKey key = machine.OpenSubKey(EnumKey + instanceId))
                    {
                        driver = key == null ? null : key.GetValue("Driver") as string;
                    }
                    if (string.IsNullOrEmpty(driver) || driver.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
                    using (RegistryKey key = machine.OpenSubKey(ClassKey + driver))
                    {
                        string name = key == null ? null : key.GetValue("DriverDesc") as string;
                        return string.IsNullOrEmpty(name) ? null : name.Trim();
                    }
                }
            }

            private const uint DigcfPresent = 2u;

            private const uint DifPropertyChange = 0x12u;

            private const uint DicsPropChange = 3u;

            private const uint DicsFlagGlobal = 1u;

            private const uint DiNeedRestart = 0x80u;

            private const uint DiNeedReboot = 0x100u;

            private static Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");

            [StructLayout(LayoutKind.Sequential)]
            private struct SpDevinfoData
            {
                public uint Size;
                public Guid ClassGuid;
                public uint DevInst;
                public IntPtr Reserved;
            }

            // SP_PROPCHANGE_PARAMS 开头是 SP_CLASSINSTALL_HEADER
            [StructLayout(LayoutKind.Sequential)]
            private struct PropChangeParams
            {
                public uint HeaderSize;
                public uint InstallFunction;
                public uint StateChange;
                public uint Scope;
                public uint HwProfile;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct DevInstallParams
            {
                public uint Size;
                public uint Flags;
                public uint FlagsEx;
                public IntPtr Parent;
                public IntPtr InstallMsgHandler;
                public IntPtr InstallMsgHandlerContext;
                public IntPtr FileQueue;
                public UIntPtr ClassInstallReserved;
                public uint Reserved;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
                public string DriverPath;
            }

            // 和设备管理器改完属性后的做法一样 用 DIF_PROPERTYCHANGE 加 DICS_PROPCHANGE 停下再启动设备
            public void RestartDevice(string instanceId, out bool rebootRequired)
            {
                rebootRequired = false;
                IntPtr set = SetupDiGetClassDevsW(ref DisplayClass, null, IntPtr.Zero, DigcfPresent);
                if (set == IntPtr.Zero || set == new IntPtr(-1))
                {
                    throw new InvalidOperationException("SetupDiGetClassDevs " + Marshal.GetLastWin32Error());
                }
                try
                {
                    SpDevinfoData data = new SpDevinfoData();
                    data.Size = (uint)Marshal.SizeOf(typeof(SpDevinfoData));
                    for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
                    {
                        StringBuilder id = new StringBuilder(512);
                        uint required;
                        if (!SetupDiGetDeviceInstanceIdW(set, ref data, id, 512u, out required)) continue;
                        if (!string.Equals(id.ToString(), instanceId, StringComparison.OrdinalIgnoreCase)) continue;
                        PropChangeParams change = new PropChangeParams { HeaderSize = 8u, InstallFunction = DifPropertyChange, StateChange = DicsPropChange, Scope = DicsFlagGlobal, HwProfile = 0u };
                        if (!SetupDiSetClassInstallParamsW(set, ref data, ref change, (uint)Marshal.SizeOf(typeof(PropChangeParams))))
                        {
                            throw new InvalidOperationException("SetupDiSetClassInstallParams " + Marshal.GetLastWin32Error());
                        }
                        if (!SetupDiCallClassInstaller(DifPropertyChange, set, ref data))
                        {
                            throw new InvalidOperationException("SetupDiCallClassInstaller " + Marshal.GetLastWin32Error());
                        }
                        DevInstallParams install = new DevInstallParams();
                        install.Size = (uint)Marshal.SizeOf(typeof(DevInstallParams));
                        if (SetupDiGetDeviceInstallParamsW(set, ref data, ref install))
                        {
                            rebootRequired = (install.Flags & (DiNeedRestart | DiNeedReboot)) != 0;
                        }
                        return;
                    }
                    throw new InvalidOperationException("GPU device is not present");
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(set);
                }
            }

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);

            [DllImport("setupapi.dll", SetLastError = true)]
            private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData data);

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SpDevinfoData data, StringBuilder instanceId, uint size, out uint required);

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SpDevinfoData data, ref PropChangeParams parameters, uint size);

            [DllImport("setupapi.dll", SetLastError = true)]
            private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SpDevinfoData data);

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref SpDevinfoData data, ref DevInstallParams parameters);

            [DllImport("setupapi.dll", SetLastError = true)]
            private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        }
    }
}
