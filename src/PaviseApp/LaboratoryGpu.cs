using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    // Session-only requests. Preferences and game qualification belong to the caller.
    internal static class LaboratoryGpu
    {
        internal const string PowerKey = "LabGpuPowerMaxV1";
        internal const string ClockKey = "LabGpuClockMaxV1";
        internal const string PowerReceipt = "LabGpuPowerReceiptV1";
        internal const string ClockReceipt = "LabGpuClockReceiptV1";
        private static readonly object sync = new object();
        private sealed class Control
        {
            internal string Key, Receipt, Zh = "关闭", En = "Off";
            internal bool Active, Yielded, Requested, RecoveryPending;
            internal DateTime Next;
            internal LaboratoryFailureBudget Failures = new LaboratoryFailureBudget();
        }
        private static readonly Control power = new Control { Key = PowerKey, Receipt = PowerReceipt };
        private static readonly Control clock = new Control { Key = ClockKey, Receipt = ClockReceipt };
        private static long session;
        internal static void BeginAutomaticCycle(long value)
        {
            lock (sync)
            {
                if (value <= session) return;
                session = value;
                foreach (Control c in new[] { power, clock }) { c.Failures.BeginSession(value); c.Next = DateTime.MinValue; c.Yielded = false; }
            }
        }
        internal static void Update(bool powerEnabled, bool clocksEnabled, long expectedSession)
        { lock (sync) if (expectedSession == session) Update(powerEnabled, clocksEnabled); }
        internal static bool HasResidue
        {
            get
            {
                lock (sync)
                {
                    string a, b;
                    return !Load(PowerReceipt, out a) || !Load(ClockReceipt, out b) || a.Length > 0 || b.Length > 0;
                }
            }
        }
        internal sealed class Sample
        {
            internal string Vendor, Id;
            internal long Current, Maximum, Minimum, Context;
            internal bool RejectedWithoutChange;
        }
        private sealed class Receipt
        {
            internal Sample Device;
            internal long Original, Applied;
        }
        private static void Status(Control c, string zh, string en) { c.Zh = zh; c.En = en; }
        internal static string State(string key)
        {
            lock (sync)
            {
                Control c = key == PowerKey ? power : key == ClockKey ? clock : null;
                return c == null ? "" : PerformancePolicy.Text(c.Zh, c.En);
            }
        }
        internal static bool CanConfigure(string key, out string reason)
        {
            lock (sync)
            {
                Sample sample;
                if (key != PowerKey && key != ClockKey) { reason = "Unknown GPU option"; return false; }
#if !PAVISE_LAB_GPU_CHECKS
                foreach (GpuAdapter adapter in GpuInventory.Adapters())
                    if (!adapter.IntegratedKnown) { reason = PerformancePolicy.Text("显卡类型无法核验，未开启", "GPU type could not be verified; not enabled"); return false; }
#endif
                if (!Probe(key, null, out sample, out reason) || sample == null || sample.Maximum <= 0) return false;
#if PAVISE_LAB_GPU_CHECKS
                return true;
#else
                if (sample.Vendor == "nv") return Nvml.CanConfigure(key, sample, out reason);
                return sample.Vendor == "amd";
#endif
            }
        }
        internal static void Update(bool powerEnabled, bool clocksEnabled)
        {
            lock (sync)
            {
                UpdateOne(power, powerEnabled);
                UpdateOne(clock, clocksEnabled);
            }
        }
        internal static bool Restore()
        {
            lock (sync)
            {
                power.Requested = clock.Requested = false;
                bool a = RestoreOne(power), b = RestoreOne(clock);
                return a & b;
            }
        }
        internal static bool Restore(long expectedSession)
        { lock (sync) return expectedSession == session && Restore(); }
        internal static bool RecoveryExhausted
        { get { lock (sync) return (!power.RecoveryPending || power.Failures.Exhausted("restore", 3))
            && (!clock.RecoveryPending || clock.Failures.Exhausted("restore", 3)); } }
        private static DateTime Now
        {
            get
            {
#if PAVISE_LAB_GPU_CHECKS
                if (NowForTest == null) throw new InvalidOperationException("Unmocked laboratory GPU clock");
                return NowForTest();
#else
                return DateTime.UtcNow;
#endif
            }
        }
        private static bool Load(string key, out string value)
        {
#if PAVISE_LAB_GPU_CHECKS
            if (LoadForTest == null) throw new InvalidOperationException("Unmocked GPU receipt read");
            value = LoadForTest(key); return value != null;
#else
            return Settings.TryLoadStr(key, out value);
#endif
        }
        private static bool Save(string key, string value)
        {
#if PAVISE_LAB_GPU_CHECKS
            if (SaveForTest == null) throw new InvalidOperationException("Unmocked GPU receipt write");
            if (!SaveForTest(key, value)) return false;
#else
            if (!Settings.SaveStrDurable(key, value)) return false;
#endif
            string check;
            return Load(key, out check) && check == value;
        }
        private static string Encode(Receipt r)
        {
            return "1;" + r.Device.Vendor + ";" + Convert.ToBase64String(Encoding.UTF8.GetBytes(r.Device.Id))
                + ";" + r.Original.ToString(CultureInfo.InvariantCulture)
                + ";" + r.Applied.ToString(CultureInfo.InvariantCulture)
                + ";" + r.Device.Context.ToString(CultureInfo.InvariantCulture);
        }
        private static bool Decode(string raw, out Receipt r)
        {
            r = null;
            try
            {
                string[] p = raw.Split(';');
                long original, applied, context;
                if (p.Length != 6 || p[0] != "1" || (p[1] != "nv" && p[1] != "amd")
                    || !long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out original)
                    || !long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out applied)
                    || !long.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out context)) return false;
                string id = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(p[2]));
                if (id.Length == 0 || id.Length > 1024 || applied <= 0 || original > applied) return false;
                r = new Receipt { Device = new Sample { Vendor = p[1], Id = id, Context = context }, Original = original, Applied = applied };
                return true;
            }
            catch { return false; }
        }
        private static void UpdateOne(Control c, bool enabled)
        {
            if (!enabled)
            {
                if (!c.Requested && Now < c.Next) return;
                c.Requested = false;
                if (RestoreOne(c)) { c.Yielded = false; c.Next = DateTime.MaxValue; }
                else c.Next = Now.AddSeconds(30);
                return;
            }
            if (!c.Requested) { c.Requested = true; c.Next = DateTime.MinValue; c.Yielded = false; }
            if (c.Yielded || Now < c.Next) return;
            c.Next = Now.AddSeconds(15);
            if (c.RecoveryPending && !RestoreOne(c)) return;
            if (!c.Active && !c.Failures.CanAttempt("apply", Now.Ticks, 1)) { RestoreOne(c); return; }
            string raw;
            if (!Load(c.Receipt, out raw)) { c.Failures.Failed("apply", Now.Ticks, 1, c.Key); Status(c, "无法读取恢复记录", "Recovery record is unreadable"); return; }
            if (c.Active)
            {
                Receipt existing;
                if (raw.Length == 0 || !Decode(raw, out existing))
                { c.Active = false; c.Yielded = true; Status(c, "恢复记录异常，停止干预", "Recovery record changed; intervention stopped"); return; }
                // NVML exposes no getter for the requested locked range. Do not mistake
                // instantaneous MHz for ownership or rewrite a request every sampling tick.
                if (c.Key == ClockKey && existing.Device.Vendor == "nv") return;
                Sample current; string reason;
                if (!Probe(c.Key, existing.Device, out current, out reason))
                { Status(c, "暂时无法核验；保留恢复记录", "Cannot verify now; recovery record retained"); return; }
                if (current.Current != existing.Applied || current.Context != existing.Device.Context)
                {
                    if (!RestoreOne(c)) { Status(c, "外部设置已变更，恢复记录待清理", "External setting changed; record cleanup pending"); return; }
                    c.Active = false; c.Yielded = true;
                    Status(c, "外部设置已接管，本局停止干预", "External setting took ownership; yielded for this session");
                }
                return;
            }
            // Settle previous-process recovery before acquiring a new session lease.
            if (raw.Length > 0 && !RestoreOne(c)) return;
            if (!c.Failures.CanAttempt("apply", Now.Ticks, 1)) return;
            Sample sample; string why;
            if (!Probe(c.Key, null, out sample, out why))
            { c.Next = Now.AddSeconds(60); Status(c, "不可用：" + why, "Unavailable: " + why); return; }
            if (sample.Maximum <= 0 || sample.Current > sample.Maximum)
            { c.Failures.Failed("apply", Now.Ticks, 1, c.Key); Status(c, "驱动返回的范围无效", "Driver returned an invalid range"); return; }
            if (sample.Current == sample.Maximum && !(c.Key == ClockKey && sample.Vendor == "nv"))
            { Status(c, "当前已是上限，无需修改", "Already at the limit; no change needed"); return; }
            var receipt = new Receipt { Device = sample, Original = sample.Current, Applied = sample.Maximum };
            if (!Save(c.Receipt, Encode(receipt)))
            { c.Failures.Failed("apply", Now.Ticks, 1, c.Key); Status(c, "恢复记录保存失败，未修改", "Recovery record could not be saved; unchanged"); return; }
            bool wrote = Write(c.Key, sample, sample.Maximum);
            if (!wrote && sample.RejectedWithoutChange)
            {
                c.Failures.Failed("apply", Now.Ticks, 1, c.Key);
                bool cleared = Save(c.Receipt, "");
                c.Next = Now.AddSeconds(60);
                Status(c, cleared ? "驱动不支持或拒绝该设置，未修改" : "驱动拒绝设置，恢复记录待清理",
                    cleared ? "Driver unsupported or request rejected; unchanged" : "Driver rejected request; record cleanup pending");
                return;
            }
            Sample check; string ignored;
            bool verified = wrote && (c.Key == ClockKey && sample.Vendor == "nv"
                || Probe(c.Key, sample, out check, out ignored) && check.Current == receipt.Applied && check.Context == sample.Context);
            if (!verified)
            {
                c.Failures.Failed("apply", Now.Ticks, 1, c.Key);
                bool restored = RestoreOne(c);
                c.Next = Now.AddSeconds(60);
                Status(c, restored ? "驱动拒绝或核验失败，已撤回" : "驱动写入未核验，等待恢复",
                    restored ? "Driver rejected request or verification failed; reverted" : "Write not verified; recovery pending");
                return;
            }
            c.Active = true;
            if (c.Key == ClockKey && sample.Vendor == "nv")
                Status(c, "驱动已接受 " + sample.Maximum + " MHz 锁频请求；关闭恢复自动调频",
                    "Driver accepted " + sample.Maximum + " MHz lock request; disabling restores automatic clocks");
            else Status(c, c.Key == ClockKey ? "最大频率下限已写入并核验" : "驱动功耗上限已写入并核验",
                c.Key == ClockKey ? "Maximum clock floor applied and verified" : "Driver power limit applied and verified");
        }
        private static bool RestoreOne(Control c)
        {
            if (!c.Failures.CanAttempt("restore", Now.Ticks, 3)) { c.Failures.ReportRecovery("restore", 3); return false; }
            bool ok = false;
            try { ok = RestoreOneCore(c); return ok; }
            finally
            {
                c.RecoveryPending = !ok;
                if (ok) c.Failures.Neutral("restore");
                else { c.Failures.Failed("restore", Now.Ticks, 3, c.Key); c.Failures.ReportRecovery("restore", 3); }
            }
        }
        private static bool RestoreOneCore(Control c)
        {
            string raw;
            if (!Load(c.Receipt, out raw)) { Status(c, "无法读取恢复记录", "Recovery record is unreadable"); return false; }
            if (raw.Length == 0) { c.Active = false; Status(c, "关闭", "Off"); return true; }
            Receipt r;
            if (!Decode(raw, out r)) { Status(c, "恢复记录损坏，保留待处理", "Invalid recovery record retained"); return false; }
            bool valid = c.Key == ClockKey
                ? r.Device.Vendor == "nv" ? r.Original == -1 && r.Device.Context == 0
                    : r.Original > 0 && r.Device.Context == r.Applied
                : r.Device.Context == 0 && (r.Device.Vendor != "nv" || r.Original > 0);
            if (!valid) { Status(c, "恢复记录与功能不匹配，保留待处理", "Recovery record does not match feature; retained"); return false; }
            if (c.Key == ClockKey && r.Device.Vendor == "nv")
            {
                // Explicit takeover contract: NVML cannot read the previous locked range.
                // Reset returns to driver automatic clocks, never an invented old value.
                if (!ResetNvClock(r.Device)) { Status(c, "恢复自动调频失败，保留恢复记录", "Automatic-clock reset failed; record retained"); return false; }
            }
            else
            {
                Sample current; string reason;
                if (!Probe(c.Key, r.Device, out current, out reason))
                { Status(c, "设备不可读，保留恢复记录", "Device unreadable; recovery record retained"); return false; }
                if (current.Current == r.Applied && current.Context == r.Device.Context)
                {
                    if (!Write(c.Key, current, r.Original)) { Status(c, "恢复失败，保留恢复记录", "Restore failed; record retained"); return false; }
                    Sample check;
                    if (!Probe(c.Key, r.Device, out check, out reason) || check.Current != r.Original || check.Context != r.Device.Context)
                    { Status(c, "恢复尚未核验，保留恢复记录", "Restore not verified; record retained"); return false; }
                }
                // Another writer changed the setting: relinquish ownership without writes.
            }
            if (!Save(c.Receipt, "")) { Status(c, "设置已恢复，记录清理待重试", "Setting restored; record cleanup pending"); return false; }
            c.Active = false; Status(c, "关闭", "Off"); return true;
        }
        private static bool Probe(string key, Sample wanted, out Sample sample, out string reason)
        {
#if PAVISE_LAB_GPU_CHECKS
            if (ProbeForTest == null) throw new InvalidOperationException("Unmocked GPU probe");
            return ProbeForTest(key, wanted, out sample, out reason);
#else
            sample = null; reason = PerformancePolicy.Text("未找到唯一独立显卡", "No unique discrete GPU");
            string vendor;
            if (wanted != null) vendor = wanted.Vendor;
            else
            {
                GpuAdapter target = null;
                foreach (GpuAdapter adapter in GpuInventory.Adapters())
                {
                    if (adapter.Integrated) continue;
                    if (target != null)
                    {
                        reason = target.IntegratedKnown && adapter.IntegratedKnown
                            ? PerformancePolicy.Text("检测到多个独显设备，需指定目标", "Multiple discrete GPU devices require an explicit target")
                            : PerformancePolicy.Text("显卡类型未完全核验，无法确认唯一独显", "GPU types are not fully verified; no unique discrete target confirmed");
                        return false;
                    }
                    target = adapter;
                }
                if (target == null) return false;
                vendor = target.Vendor == GpuVendor.Nvidia ? "nv" : target.Vendor == GpuVendor.Amd ? "amd" : "";
            }
            if (vendor == "nv") return Nvml.Read(key, wanted, out sample, out reason);
            if (vendor == "amd") return AmdRead(key, wanted, out sample, out reason);
            reason = PerformancePolicy.Text("显卡厂商不支持", "GPU vendor is unsupported"); return false;
#endif
        }
        private static bool Write(string key, Sample device, long value)
        {
#if PAVISE_LAB_GPU_CHECKS
            if (WriteForTest == null) throw new InvalidOperationException("Unmocked GPU write");
            return WriteForTest(key, device, value);
#else
            if (device.Vendor == "nv") return Nvml.Write(key, device, value);
            return AmdWrite(key, device, value);
#endif
        }
        private static bool ResetNvClock(Sample device)
        {
#if PAVISE_LAB_GPU_CHECKS
            if (ResetClockForTest == null) throw new InvalidOperationException("Unmocked GPU clock reset");
            return ResetClockForTest(device);
#else
            return Nvml.Reset(device);
#endif
        }
        private static bool AmdRead(string key, Sample wanted, out Sample sample, out string reason)
        {
            sample = null; reason = PerformancePolicy.Text("AMD 驱动接口不支持或无法读取", "AMD driver API is unsupported or unreadable");
            if (!AdlxApi.Available) return false;
            IntPtr[] devices = AdlxApi.GetGpus();
            if (devices == null) return false;
            try
            {
                IntPtr found = IntPtr.Zero; string id = null;
                foreach (IntPtr gpu in devices)
                {
                    string pnp = AdlxApi.GpuPnpString(gpu);
                    if (wanted == null ? AdlxApi.GpuType(gpu) != AdlxApi.GpuTypeDiscrete
                        : !string.Equals(wanted.Id, pnp, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found != IntPtr.Zero) { reason = PerformancePolicy.Text("AMD 目标设备不唯一", "AMD target device is ambiguous"); return false; }
                    found = gpu; id = pnp;
                }
                if (found == IntPtr.Zero || string.IsNullOrEmpty(id)) return false;
                bool supported; int current, maximum;
                if (key == PowerKey)
                {
                    if (!AdlxApi.PowerLimitGet(found, out supported, out current, out maximum) || !supported) return false;
                    sample = new Sample { Vendor = "amd", Id = id, Current = current, Maximum = maximum };
                }
                else
                {
                    AdlxIntRange range;
                    if (!AdlxApi.GfxMinGet(found, out supported, out current, out maximum, out range) || !supported) return false;
                    // Do not silently substitute "almost maximum" or modify the max/voltage.
                    if (maximum <= 0 || maximum > range.Max || maximum < range.Min
                        || range.Step > 0 && (maximum - range.Min) % range.Step != 0)
                    { reason = PerformancePolicy.Text("驱动不支持将最低频率设为当前最高频率", "Driver cannot set the floor to the current maximum clock"); return false; }
                    sample = new Sample { Vendor = "amd", Id = id, Current = current, Maximum = maximum, Minimum = range.Min, Context = maximum };
                }
                return true;
            }
            catch { return false; }
            finally { AdlxApi.ReleaseAll(devices); }
        }
        private static bool AmdWrite(string key, Sample device, long value)
        {
            if (value < int.MinValue || value > int.MaxValue || !AdlxApi.Available) return false;
            IntPtr[] devices = AdlxApi.GetGpus();
            if (devices == null) return false;
            try
            {
                IntPtr found = IntPtr.Zero;
                foreach (IntPtr gpu in devices)
                {
                    if (!string.Equals(AdlxApi.GpuPnpString(gpu), device.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found != IntPtr.Zero) return false;
                    found = gpu;
                }
                if (found == IntPtr.Zero) return false;
                bool supported; int current, maximum;
                if (key == PowerKey)
                {
                    if (!AdlxApi.PowerLimitGet(found, out supported, out current, out maximum) || !supported
                        || current != device.Current || value > maximum) return false;
                    return AdlxApi.PowerLimitSet(found, (int)value);
                }
                AdlxIntRange range;
                if (!AdlxApi.GfxMinGet(found, out supported, out current, out maximum, out range) || !supported
                    || current != device.Current || maximum != device.Context || value < range.Min || value > range.Max) return false;
                return AdlxApi.GfxMinSet(found, (int)value);
            }
            catch { return false; }
            finally { AdlxApi.ReleaseAll(devices); }
        }
        private static class Nvml
        {
            [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int Init();
            [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")] private static extern int Shutdown();
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int Count(out uint count);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int ByIndex(uint index, out IntPtr gpu);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByUUID", CharSet = CharSet.Ansi)] private static extern int ByUuid(string uuid, out IntPtr gpu);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUUID", CharSet = CharSet.Ansi)] private static extern int Uuid(IntPtr gpu, StringBuilder uuid, uint length);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit")] private static extern int Power(IntPtr gpu, out uint value);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")] private static extern int PowerRange(IntPtr gpu, out uint min, out uint max);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetPowerManagementLimit")] private static extern int SetPower(IntPtr gpu, uint value);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxClockInfo")] private static extern int MaxClock(IntPtr gpu, uint domain, out uint mhz);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetArchitecture")] private static extern int Architecture(IntPtr gpu, out uint architecture);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementMode")] private static extern int PowerMode(IntPtr gpu, out uint mode);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")] private static extern int Clock(IntPtr gpu, uint domain, out uint clock);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetSupportedGraphicsClocks")] private static extern int SupportedClocks(IntPtr gpu, uint memory, ref uint count, [Out] uint[] clocks);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetGpuLockedClocks")] private static extern int SetClock(IntPtr gpu, uint min, uint max);
            [DllImport("nvml.dll", EntryPoint = "nvmlDeviceResetGpuLockedClocks")] private static extern int ResetClock(IntPtr gpu);
            private static bool Handle(Sample wanted, out IntPtr gpu, out string id)
            {
                gpu = IntPtr.Zero; id = null;
                if (wanted != null)
                {
                    if (ByUuid(wanted.Id, out gpu) != 0) return false;
                }
                else
                {
                    uint count;
                    if (Count(out count) != 0 || count != 1 || ByIndex(0, out gpu) != 0) return false;
                }
                var buffer = new StringBuilder(96);
                if (Uuid(gpu, buffer, (uint)buffer.Capacity) != 0) return false;
                id = buffer.ToString();
                return id.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase)
                    && (wanted == null || string.Equals(wanted.Id, id, StringComparison.OrdinalIgnoreCase));
            }
            internal static bool Read(string key, Sample wanted, out Sample sample, out string reason)
            {
                sample = null; reason = PerformancePolicy.Text("NVIDIA 驱动接口不支持、设备不唯一或无法读取", "NVIDIA API unsupported, target ambiguous, or unreadable");
                bool opened = false;
                try
                {
                    if (Init() != 0) return false;
                    opened = true; IntPtr gpu; string id;
                    if (!Handle(wanted, out gpu, out id)) return false;
                    uint current, minimum, maximum;
                    if (key == PowerKey)
                    {
                        if (Power(gpu, out current) != 0 || PowerRange(gpu, out minimum, out maximum) != 0
                            || current < minimum || current > maximum) return false;
                        sample = new Sample { Vendor = "nv", Id = id, Current = current, Minimum = minimum, Maximum = maximum };
                    }
                    else
                    {
                        if (MaxClock(gpu, 0, out maximum) != 0 || maximum == 0) return false;
                        sample = new Sample { Vendor = "nv", Id = id, Current = -1, Maximum = maximum };
                    }
                    return true;
                }
                catch { return false; }
                finally { if (opened) try { Shutdown(); } catch { } }
            }
            internal static bool Write(string key, Sample device, long value)
            {
                bool opened = false;
                try
                {
                    if (value <= 0 || value > uint.MaxValue || Init() != 0) return false;
                    opened = true; IntPtr gpu; string id;
                    if (!Handle(device, out gpu, out id)) return false;
                    if (key == ClockKey)
                    {
                        int rc = SetClock(gpu, (uint)value, (uint)value);
                        device.RejectedWithoutChange = rc == 2 || rc == 3 || rc == 4;
                        return rc == 0;
                    }
                    uint minimum, maximum, current;
                    if (PowerRange(gpu, out minimum, out maximum) != 0 || value < minimum || value > maximum
                        || Power(gpu, out current) != 0 || current != device.Current) return false;
                    int result = SetPower(gpu, (uint)value);
                    device.RejectedWithoutChange = result == 2 || result == 3 || result == 4;
                    return result == 0;
                }
                catch (EntryPointNotFoundException) { device.RejectedWithoutChange = true; return false; }
                catch (DllNotFoundException) { device.RejectedWithoutChange = true; return false; }
                catch { return false; }
                finally { if (opened) try { Shutdown(); } catch { } }
            }
            internal static bool CanConfigure(string key, Sample wanted, out string reason)
            {
                reason = PerformancePolicy.Text("NVIDIA 设置能力无法核验，未开启", "NVIDIA configuration support could not be verified; not enabled");
                bool opened = false;
                try
                {
                    if (Init() != 0) return false;
                    opened = true; IntPtr gpu; string id;
                    if (!Handle(wanted, out gpu, out id)) return false;
                    uint value;
                    if (key == PowerKey) return PowerMode(gpu, out value) == 0 && value == 1;
                    // Read-only support evidence; never test support by temporarily writing clocks.
                    if (Architecture(gpu, out value) != 0 || value < 5 || value == uint.MaxValue) return false;
                    uint memory;
                    if (Clock(gpu, 2, out memory) != 0) return false;
                    uint count = 0; int rc = SupportedClocks(gpu, memory, ref count, null);
                    if (rc != 0 && rc != 7 || count == 0 || count > 4096) return false;
                    uint[] clocks = new uint[count];
                    if (SupportedClocks(gpu, memory, ref count, clocks) != 0) return false;
                    foreach (uint frequency in clocks) if (frequency == wanted.Maximum) return true;
                    return false;
                }
                catch { return false; }
                finally { if (opened) try { Shutdown(); } catch { } }
            }
            internal static bool Reset(Sample device)
            {
                bool opened = false;
                try
                {
                    if (Init() != 0) return false;
                    opened = true; IntPtr gpu; string id;
                    return Handle(device, out gpu, out id) && ResetClock(gpu) == 0;
                }
                catch { return false; }
                finally { if (opened) try { Shutdown(); } catch { } }
            }
        }
#if PAVISE_LAB_GPU_CHECKS
        internal delegate bool ProbeDelegate(string key, Sample wanted, out Sample sample, out string reason);
        internal static ProbeDelegate ProbeForTest;
        internal static Func<string, Sample, long, bool> WriteForTest;
        internal static Func<Sample, bool> ResetClockForTest;
        internal static Func<string, string> LoadForTest;
        internal static Func<string, string, bool> SaveForTest;
        internal static Func<DateTime> NowForTest;
        internal static void ResetForTest()
        {
            session = 0;
            foreach (Control c in new[] { power, clock })
            { c.Failures = new LaboratoryFailureBudget(); c.Active = c.Requested = c.Yielded = c.RecoveryPending = false; c.Next = DateTime.MinValue; Status(c, "关闭", "Off"); }
        }
#endif
    }
}
