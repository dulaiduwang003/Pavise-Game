using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class LaboratoryPowerSample
    {
        internal bool IdentityValid, AdapterKnown, Ambiguous;
        internal int AdapterHigh;
        internal uint AdapterLow, AdapterPhysical;
        internal double CpuPercent = double.NaN, GpuPercent = double.NaN, CpuWatts = double.NaN;
    }

    internal interface ILaboratoryPowerPlatform
    {
        long Now { get; }
        bool OnAc { get; }
        bool HasBattery { get; }
        Guid ActiveManagedPlan();
        bool EppSupported(Guid plan);
        int OwnerStatus(Guid plan); // 1 owned, 0 proven absent, -1 unknown / foreign
        uint? Read(Guid plan, Guid setting);
        bool Write(Guid plan, Guid setting, uint value);
        bool Flush(Guid plan);
        bool Load(out string receipt);
        bool Save(string receipt);
        LaboratoryPowerSample Sample(int pid, long creation);
        void ResetSampling();
    }

    // Optional capability: legacy platforms and V1 journals remain managed-plan only.
    internal interface ILaboratoryCurrentPowerPlatform
    {
        Guid ActivePlan();
        string DeviceIdentity { get; }
        int PlanStatus(Guid plan); // 1 confirmed present, 0 proven absent, -1 unknown
    }

    internal static class LaboratoryPower
    {
        internal const string EppKey = "LabEppYieldV1", IdleKey = "LabCpuIdleStrategyV1";
        internal const string ReceiptKey = "LabPowerSnapshotV1";
        internal static readonly Guid Promote = new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c");
        internal static readonly Guid Scaling = new Guid("6c2993b0-8f48-481f-bcc6-00dd2742aa06");
        internal static readonly Guid Disable = new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad");
        internal static readonly Guid Epp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        internal static readonly Guid Epp1 = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");
        private static LaboratoryPowerController controller;
        private static LaboratoryPowerController Controller
        {
            get { return controller ?? (controller = new LaboratoryPowerController(new PowerPlan.NativeLaboratoryPower(), PowerPlan.LaboratoryGate)); }
        }
        internal static void Update(bool epp, string idleStrategy) { Update(epp, idleStrategy, 0, 0); }
        internal static void Update(bool epp, string idleStrategy, int rendererPid, long rendererCreation)
        { lock (PowerPlan.LaboratoryGate) Controller.Update(epp, idleStrategy, rendererPid, rendererCreation); }
        internal static void Update(bool epp, string idleStrategy, int rendererPid, long rendererCreation, long session)
        { lock (PowerPlan.LaboratoryGate) if (Controller.Session == session) Controller.Update(epp, idleStrategy, rendererPid, rendererCreation); }
        internal static void BeginAutomaticCycle(long session)
        { lock (PowerPlan.LaboratoryGate) Controller.BeginAutomaticCycle(session); }
        internal static bool Restore() { lock (PowerPlan.LaboratoryGate) return Controller.Restore(); }
        internal static bool Restore(long expectedSession)
        { lock (PowerPlan.LaboratoryGate) return Controller.Session == expectedSession && Controller.Restore(); }
        internal static bool RecoveryExhausted
        { get { lock (PowerPlan.LaboratoryGate) return controller != null && controller.RecoveryExhausted; } }
        internal static string State(string key) { lock (PowerPlan.LaboratoryGate) return Controller.State(key); }
        internal static HashSet<Guid> OwnedSettings(Guid scheme)
        {
            lock (PowerPlan.LaboratoryGate)
                return controller == null ? new HashSet<Guid>() : controller.OwnedSettings(scheme);
        }
        internal static bool HasResidue
        {
            get
            {
                string raw;
                return !Settings.TryLoadStr(ReceiptKey, out raw) || !string.IsNullOrEmpty(raw);
            }
        }
    }

    internal sealed class LaboratoryPowerController
    {
        private sealed class Value
        {
            internal Guid Setting;
            internal uint Original, Wanted;
            internal bool ManagedOnly;
            internal string Text { get { return Setting.ToString("N") + ":" + Original.ToString(CultureInfo.InvariantCulture)
                + ":" + Wanted.ToString(CultureInfo.InvariantCulture); } }
        }
        private sealed class Journal
        {
            internal Guid Owner;
            internal string Device;
            internal bool Legacy;
            internal List<Value> Values = new List<Value>();
        }
        private readonly ILaboratoryPowerPlatform platform;
        private readonly object gate;
        private readonly LaboratoryFailureBudget failures = new LaboratoryFailureBudget();
        internal long Session { get; private set; }
        internal bool RecoveryExhausted { get { lock (gate) return failures.Exhausted("restore", 3); } }
        internal void BeginAutomaticCycle(long session)
        {
            lock (gate)
            {
                if (!failures.BeginSession(session)) return;
                Session = session; idleBlocked = eppBlocked = false; ResetEpp();
            }
        }
        private bool initialized, idleBlocked, eppBlocked, recoveryPending;
        private string idle = "default", idleState = "等待对局", eppState = "等待对局";
        private Guid plan;
        private int pid, attempts, samples, releaseSamples;
        private long creation, nextSample, windowStart, lastGood;
        private double gpuTotal, cpuTotal, wattsTotal, baselineGpu, baselineWatts;
        private bool adapterKnown;
        private int adapterHigh;
        private uint adapterLow, adapterPhysical;
        // observe -> verify -> holding; failed verification stops the current session.
        private int phase;

        internal LaboratoryPowerController(ILaboratoryPowerPlatform value, object sync)
        { platform = value; gate = sync; }
        internal HashSet<Guid> OwnedSettings(Guid scheme)
        {
            lock (gate)
            {
                Journal journal;
                if (!Load(out journal)) return null;
                var result = new HashSet<Guid>();
                if (journal.Values.Count == 0 || journal.Owner != scheme) return result;
                if (!JournalAuthorized(journal)) return null;
                foreach (Value value in journal.Values) result.Add(value.Setting);
                return result;
            }
        }
        internal string State(string key)
        {
            lock (gate)
            {
                string value = key == LaboratoryPower.IdleKey ? idleState : eppState;
#if PAVISE_LAB_POWER_ISOLATED
                return value;
#else
                return PerformancePolicy.Text(value, EnglishState(value));
#endif
            }
        }
        private static string EnglishState(string value)
        {
            switch (value)
            {
                case "等待对局": return "Waiting for a game session";
                case "操作失败，等待恢复": return "Operation failed; recovery is pending";
                case "恢复未完成": return "Recovery is pending";
                case "原设置已恢复，本次会话停止试探": return "Original settings restored; trials stopped for this session";
                case "需要交流供电和 Pavise 托管方案": return "Requires AC power and the Pavise managed plan";
                case "需要交流供电和可读取的当前电源方案": return "Requires AC power and an accessible current power plan";
                case "不适用：需要 Pavise 托管方案": return "Not applicable: requires the Pavise managed plan";
                case "跟随原电源策略": return "Following the original power policy";
                case "检测到外部修改，本次会话已让出": return "External modification detected; yielded for this session";
                case "设置不支持或应用失败": return "Setting is unsupported or could not be applied";
                case "已禁止 CPU 空闲": return "CPU idle has been disabled";
                case "已提高深层空闲门槛": return "Deep-idle promotion threshold has been raised";
                case "电源方案已包含禁止 CPU 空闲": return "The power plan already disables CPU idle";
                case "电源方案已包含提高深层空闲门槛": return "The power plan already includes raised deep-idle thresholds";
                case "已关闭": return "Off";
                case "不适用：需要笔记本及自主性能控制支持": return "Not applicable: requires a laptop and autonomous performance control";
                case "等待可验证的游戏渲染进程": return "Waiting for a verified game renderer";
                case "渲染进程身份失效，已停止试探": return "Renderer identity is no longer valid; trial stopped";
                case "渲染显卡发生变化，已停止试探": return "Rendering adapter changed; trial stopped";
                case "有效采样中断，已撤销让路": return "Valid samples stopped; EPP yield was reverted";
                case "采样暂缺，等待复核": return "Samples are temporarily unavailable; awaiting verification";
                case "等待 CPU 功耗和游戏 GPU 有效采样": return "Waiting for valid CPU package power and game GPU samples";
                case "本次会话试探次数已用完": return "No more trials in this session";
                case "瓶颈变化，已归还 CPU 性能偏好": return "Bottleneck changed; original CPU performance preference restored";
                case "正在观察游戏负载": return "Observing game load";
                case "正在验证 EPP 48": return "Verifying EPP 48";
                case "观察中：尚未满足 GPU 重载、CPU 余量条件": return "Observing: GPU load or CPU headroom conditions are not met";
                case "当前 EPP 已达到或超过让路值": return "Current EPP already meets or exceeds the yield value";
                case "EPP 应用失败": return "EPP could not be applied";
                case "功耗或 GPU 占用验证未通过，已撤销让路": return "Power or GPU utilization verification failed; EPP yield reverted";
                case "功耗与 GPU 占用检查通过，持续监测（未验证帧率收益）": return "Power and GPU utilization checks passed; monitoring continues (FPS benefit unverified)";
                default: return "Waiting for policy state";
            }
        }

        internal void Update(bool wantEpp, string wantedIdle, int rendererPid, long rendererCreation)
        {
            lock (gate)
            {
                try { UpdateCore(wantEpp, NormalizeIdle(wantedIdle), rendererPid, rendererCreation); }
                catch { idleState = eppState = "操作失败，等待恢复"; RestoreCore(null); idleBlocked = eppBlocked = true; }
            }
        }

        private static string NormalizeIdle(string value)
        { return value == "threshold" || value == "disabled" ? value : "default"; }

        private void UpdateCore(bool wantEpp, string wantedIdle, int rendererPid, long rendererCreation)
        {
            if (recoveryPending)
            {
                if (!RestoreCore(null)) { idleState = eppState = "恢复未完成"; return; }
                recoveryPending = false;
                ResetEpp(false); idleBlocked = eppBlocked = true;
                idleState = eppState = "原设置已恢复，本次会话停止试探";
            }
            if (!initialized)
            {
                if (!RestoreCore(null)) { idleState = eppState = "恢复未完成"; return; }
                initialized = true;
            }
            Guid current = ActiveIdlePlan();
            if (!platform.OnAc || current == Guid.Empty)
            {
                bool restored = Restore();
                idleState = restored ? "需要交流供电和可读取的当前电源方案" : "恢复未完成";
                eppState = restored ? "需要交流供电和 Pavise 托管方案" : "恢复未完成";
                return;
            }
            if (plan != Guid.Empty && plan != current)
            {
                if (!Restore()) return;
            }
            plan = current;
            if (idle != wantedIdle)
            {
                if (!RestoreBeforeIdleChange()) return;
                idle = wantedIdle; idleBlocked = false;
            }
            if (idle == "default") idleState = "跟随原电源策略";
            else if (!idleBlocked)
            {
                bool already;
                if (!OwnedValuesMatch(false, out already))
                {
                    // An external idle edit changes the conditions of an EPP trial too.
                    if (!RestoreBeforeIdleChange()) return;
                    idleBlocked = true; idleState = "检测到外部修改，本次会话已让出";
                }
                else if (!already)
                {
                    var wanted = new Dictionary<Guid, uint>();
                    if (idle == "disabled") wanted[LaboratoryPower.Disable] = 1;
                    else
                    {
                        wanted[LaboratoryPower.Disable] = 0;
                        wanted[LaboratoryPower.Promote] = 100;
                        wanted[LaboratoryPower.Scaling] = 0;
                    }
                    bool included;
                    if (!Apply(wanted, out included)) { idleBlocked = true; idleState = "设置不支持或应用失败"; }
                    else idleState = included
                        ? idle == "disabled" ? "电源方案已包含禁止 CPU 空闲" : "电源方案已包含提高深层空闲门槛"
                        : idle == "disabled" ? "已禁止 CPU 空闲" : "已提高深层空闲门槛";
                }
            }
            if (!wantEpp)
            {
                bool ok = RestoreCore(true);
                ResetEpp(); eppBlocked = false;
                eppState = ok ? "已关闭" : "恢复未完成";
                return;
            }
            if (platform.ActiveManagedPlan() != plan)
            {
                bool ok = RestoreCore(true); ResetEpp();
                eppState = ok ? "不适用：需要 Pavise 托管方案" : "恢复未完成";
                return;
            }
            if (!platform.HasBattery || !platform.EppSupported(plan))
            {
                bool ok = RestoreCore(true); ResetEpp();
                eppState = ok ? "不适用：需要笔记本及自主性能控制支持" : "恢复未完成";
                return;
            }
            if (rendererPid <= 0 || rendererCreation <= 0)
            {
                bool ok = RestoreCore(true); ResetEpp();
                eppState = ok ? "等待可验证的游戏渲染进程" : "恢复未完成";
                return;
            }
            if (pid != rendererPid || creation != rendererCreation)
            {
                if (!RestoreCore(true)) { eppState = "恢复未完成"; return; }
                ResetEpp(); eppBlocked = false; pid = rendererPid; creation = rendererCreation;
            }
            if (eppBlocked) return;
            bool owned;
            if (!OwnedValuesMatch(true, out owned))
            {
                RestoreCore(true); eppBlocked = true; eppState = "检测到外部修改，本次会话已让出"; return;
            }
            long now = platform.Now;
            if (now < nextSample) return;
            nextSample = now + TimeSpan.TicksPerSecond * 5;
            LaboratoryPowerSample sample = platform.Sample(pid, creation);
            if (sample == null || !sample.IdentityValid)
            { StopEpp("渲染进程身份失效，已停止试探"); return; }
            if (sample.AdapterKnown && !sample.Ambiguous && adapterKnown
                && (sample.AdapterHigh != adapterHigh || sample.AdapterLow != adapterLow || sample.AdapterPhysical != adapterPhysical))
            { StopEpp("渲染显卡发生变化，已停止试探"); return; }
            if (!sample.AdapterKnown || sample.Ambiguous || !Valid(sample.CpuPercent, 100)
                || !Valid(sample.GpuPercent, 100) || !Valid(sample.CpuWatts, 1000) || sample.CpuWatts <= 0)
            {
                if (phase > 0 && now - lastGood >= TimeSpan.TicksPerSecond * 10)
                    StopEpp("有效采样中断，已撤销让路");
                else
                {
                    if (phase == 0) ResetWindow();
                    eppState = phase > 0 ? "采样暂缺，等待复核" : "等待 CPU 功耗和游戏 GPU 有效采样";
                }
                return;
            }
            if (!adapterKnown)
            { adapterKnown = true; adapterHigh = sample.AdapterHigh; adapterLow = sample.AdapterLow; adapterPhysical = sample.AdapterPhysical; }
            lastGood = now;
            if (phase == 2)
            {
                if (sample.CpuPercent >= 75 || sample.GpuPercent < 85) releaseSamples++;
                else releaseSamples = 0;
                if (releaseSamples >= 2)
                {
                    if (!RestoreCore(true)) { eppBlocked = true; eppState = "恢复未完成"; return; }
                    phase = 0; ResetWindow();
                    if (attempts >= 3) { eppBlocked = true; eppState = "本次会话试探次数已用完"; }
                    else eppState = "瓶颈变化，已归还 CPU 性能偏好";
                }
                return;
            }
            if (windowStart == 0) windowStart = now;
            gpuTotal += sample.GpuPercent; cpuTotal += sample.CpuPercent; wattsTotal += sample.CpuWatts; samples++;
            if (samples < 4 || now - windowStart < TimeSpan.TicksPerSecond * (phase == 0 ? 20 : 15))
            { eppState = phase == 0 ? "正在观察游戏负载" : "正在验证 EPP 48"; return; }
            double gpu = gpuTotal / samples, cpu = cpuTotal / samples, watts = wattsTotal / samples;
            if (phase == 0)
            {
                if (gpu < 90 || cpu > 65) { ResetWindow(); eppState = "观察中：尚未满足 GPU 重载、CPU 余量条件"; return; }
                var wanted = new Dictionary<Guid, uint>();
                uint? original = platform.Read(plan, LaboratoryPower.Epp);
                if (!original.HasValue || original.Value >= 48)
                { eppBlocked = true; eppState = "当前 EPP 已达到或超过让路值"; return; }
                wanted[LaboratoryPower.Epp] = 48;
                uint? secondary = platform.Read(plan, LaboratoryPower.Epp1);
                if (secondary.HasValue && secondary.Value < 48) wanted[LaboratoryPower.Epp1] = 48;
                attempts++;
                if (!Apply(wanted)) { StopEpp("EPP 应用失败"); return; }
                baselineGpu = gpu; baselineWatts = watts; phase = 1; ResetWindow();
                eppState = "正在验证 EPP 48";
            }
            else
            {
                if (baselineWatts - watts < 3 || baselineGpu - gpu > 3 || cpu > 75)
                { StopEpp("功耗或 GPU 占用验证未通过，已撤销让路"); return; }
                phase = 2; releaseSamples = 0; ResetWindow();
                eppState = "功耗与 GPU 占用检查通过，持续监测（未验证帧率收益）";
            }
        }

        private static bool Valid(double value, double max)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= max; }
        private bool RestoreBeforeIdleChange()
        {
            // Drain EPP before idle; both observations belong to the same fixed idle configuration.
            if (!RestoreCore(true) || !RestoreCore(false))
            { idleState = eppState = "恢复未完成"; return false; }
            ResetEpp(); eppBlocked = false;
            return true;
        }
        private void StopEpp(string reason)
        {
            bool ok = RestoreCore(true); ResetEpp(false); eppBlocked = true;
            eppState = ok ? reason : "恢复未完成";
        }
        private void ResetWindow() { windowStart = 0; samples = 0; gpuTotal = cpuTotal = wattsTotal = 0; }
        private void ResetEpp(bool forgetSession = true)
        {
            phase = releaseSamples = 0; nextSample = lastGood = 0;
            if (forgetSession) { attempts = pid = 0; creation = 0; }
            adapterKnown = false; ResetWindow(); platform.ResetSampling();
        }

        internal bool Restore()
        {
            lock (gate)
            {
                bool ok;
                try { ok = RestoreCore(null); } catch { ok = false; }
                ResetEpp(); plan = Guid.Empty; idle = "default";
                initialized = ok; recoveryPending = !ok; idleBlocked = eppBlocked = false;
                idleState = eppState = ok ? "等待对局" : "恢复未完成";
                return ok;
            }
        }

        private static bool IsEpp(Guid setting) { return setting == LaboratoryPower.Epp || setting == LaboratoryPower.Epp1; }
        private static bool IsKnown(Guid setting)
        { return IsEpp(setting) || setting == LaboratoryPower.Promote || setting == LaboratoryPower.Scaling || setting == LaboratoryPower.Disable; }
        private static uint Limit(Guid setting)
        { return setting == LaboratoryPower.Disable || setting == LaboratoryPower.Scaling ? 1u : 100u; }
        private Guid ActiveIdlePlan()
        {
            var current = platform as ILaboratoryCurrentPowerPlatform;
            return current == null ? platform.ActiveManagedPlan() : current.ActivePlan();
        }
        private static bool NormalizeDevice(string value, out string normalized)
        {
            Guid device;
            normalized = null;
            if (!Guid.TryParse(value, out device) || device == Guid.Empty) return false;
            normalized = device.ToString("N"); return true;
        }
        private bool Load(out Journal journal)
        {
            journal = new Journal(); string text;
            if (!platform.Load(out text) || text == null) return false;
            if (text.Length == 0) return true;
            string[] header = text.Split('|'); string encoded;
            if (header.Length == 3 && header[0] == "1")
            {
                journal.Legacy = true;
                if (!Guid.TryParse(header[1], out journal.Owner)) return false;
                encoded = header[2];
            }
            else if (header.Length == 4 && header[0] == "2")
            {
                var current = platform as ILaboratoryCurrentPowerPlatform; string device;
                if (current == null || !NormalizeDevice(current.DeviceIdentity, out device)
                    || !NormalizeDevice(header[1], out journal.Device) || journal.Device != device
                    || !Guid.TryParse(header[2], out journal.Owner)) return false;
                encoded = header[3];
            }
            else return false;
            if (journal.Owner == Guid.Empty) return false;
            var seen = new HashSet<Guid>();
            foreach (string part in encoded.Split(';'))
            {
                string[] fields = part.Split(':'); Guid setting; uint original, wanted;
                if (fields.Length != (journal.Legacy ? 3 : 4) || !Guid.TryParse(fields[0], out setting)
                    || !IsKnown(setting) || !seen.Add(setting)
                    || !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out original)
                    || !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out wanted)
                    || original > Limit(setting) || wanted > Limit(setting)) return false;
                bool managed = journal.Legacy || fields[3] == "m";
                if (!journal.Legacy && fields[3] != "m" && fields[3] != "c" || IsEpp(setting) && !managed) return false;
                journal.Values.Add(new Value { Setting = setting, Original = original, Wanted = wanted, ManagedOnly = managed });
            }
            return journal.Values.Count > 0;
        }
        private bool Save(Journal journal, List<Value> values)
        {
            var current = platform as ILaboratoryCurrentPowerPlatform;
            string device = null;
            // Arbitrary-plan receipts require exact ownership, not a machine display name.
            if (!journal.Legacy && values.Count > 0 && (current == null || !NormalizeDevice(current.DeviceIdentity, out device)
                || journal.Device != null && journal.Device != device)) return false;
            if (!journal.Legacy && device != null) journal.Device = device;
            var text = new List<string>();
            foreach (Value value in values)
                text.Add(value.Text + (journal.Legacy ? "" : value.ManagedOnly ? ":m" : ":c"));
            string encoded = text.Count == 0 ? "" : journal.Legacy
                ? "1|" + journal.Owner.ToString("N") + "|" + string.Join(";", text.ToArray())
                : "2|" + device + "|" + journal.Owner.ToString("N") + "|" + string.Join(";", text.ToArray());
            if (!platform.Save(encoded)) return false;
            string read; return platform.Load(out read) && read == encoded;
        }
        private bool JournalAuthorized(Journal journal)
        {
            if (journal.Legacy) return platform.OwnerStatus(journal.Owner) == 1;
            var current = platform as ILaboratoryCurrentPowerPlatform; string device;
            return current != null && NormalizeDevice(current.DeviceIdentity, out device)
                && (journal.Device == null || journal.Device == device) && current.PlanStatus(journal.Owner) == 1;
        }
        private bool OwnedValuesMatch(bool epp, out bool any)
        {
            any = false; Journal journal;
            if (!Load(out journal) || journal.Values.Count != 0 && journal.Owner != plan) return false;
            foreach (Value value in journal.Values)
                if (IsEpp(value.Setting) == epp)
                {
                    any = true;
                    if (value.ManagedOnly && platform.OwnerStatus(journal.Owner) != 1
                        || platform.Read(journal.Owner, value.Setting) != value.Wanted) return false;
                }
            return true;
        }
        private bool Apply(Dictionary<Guid, uint> requested)
        {
            bool included;
            return Apply(requested, out included);
        }
        private bool Apply(Dictionary<Guid, uint> requested, out bool included)
        {
            var settings = new List<string>();
            bool rendererScoped = false;
            foreach (KeyValuePair<Guid, uint> value in requested) { settings.Add(value.Key.ToString("N") + ":" + value.Value.ToString(CultureInfo.InvariantCulture)); rendererScoped |= IsEpp(value.Key); }
            settings.Sort(StringComparer.Ordinal);
            string identity = "apply|" + plan.ToString("N") + (rendererScoped ? "|" + pid + "|" + creation : "") + "|" + string.Join(";", settings.ToArray());
            included = false;
            if (!failures.CanAttempt(identity, platform.Now, 1)) return false;
            bool ok = false;
            try { ok = ApplyCore(requested, out included); return ok; }
            finally { if (!ok) failures.Failed(identity, platform.Now, 1, "power"); }
        }
        private bool ApplyCore(Dictionary<Guid, uint> requested, out bool included)
        {
            included = true;
            Journal journal;
            if (!Load(out journal) || journal.Values.Count > 0 && journal.Owner != plan) return false;
            if (journal.Values.Count == 0)
            {
                journal.Owner = plan;
                journal.Legacy = !(platform is ILaboratoryCurrentPowerPlatform);
            }
            if (!JournalAuthorized(journal)) return false;
            List<Value> values = journal.Values;
            var added = new List<Value>();
            foreach (KeyValuePair<Guid, uint> request in requested)
            {
                uint? original = platform.Read(plan, request.Key);
                if (!original.HasValue || original.Value > Limit(request.Key) || request.Value > Limit(request.Key)) return false;
                if (original.Value != request.Value) included = false;
                // Journal idle no-ops too, so a later external edit cannot be silently reclaimed.
                if (original.Value == request.Value && IsEpp(request.Key)) continue;
                foreach (Value saved in values) if (saved.Setting == request.Key) return false;
                var value = new Value { Setting = request.Key, Original = original.Value, Wanted = request.Value,
                    ManagedOnly = journal.Legacy || IsEpp(request.Key) };
                added.Add(value); values.Add(value);
            }
            if (added.Count == 0) return true;
            if (!Save(journal, values)) return false;
            bool ok = true;
            foreach (Value value in added)
            {
                // Do not overwrite a modification made between snapshot and apply.
                if (ActiveIdlePlan() != plan || !platform.OnAc || !JournalAuthorized(journal)
                    || value.ManagedOnly && (platform.ActiveManagedPlan() != plan || platform.OwnerStatus(plan) != 1)
                    || platform.Read(plan, value.Setting) != value.Original
                    || value.Original != value.Wanted && !platform.Write(plan, value.Setting, value.Wanted)
                    || platform.Read(plan, value.Setting) != value.Wanted) { ok = false; break; }
            }
            if (ok && platform.Flush(plan)) return true;
            RestoreCore(IsEpp(added[0].Setting)); return false;
        }
        // null restores everything; true restores EPP only; false restores idle knobs only.
        private bool RestoreCore(bool? epp)
        {
            const string identity = "restore";
            if (!failures.CanAttempt(identity, platform.Now, 3)) { recoveryPending = true; failures.ReportRecovery(identity, 3); return false; }
            bool ok = false;
            try
            {
                ok = RestoreJournal(epp);
                if (ok)
                {
                    Journal remaining;
                    if (Load(out remaining) && remaining.Values.Count == 0) failures.Neutral(identity);
                }
                return ok;
            }
            finally
            {
                if (!ok) { recoveryPending = true; failures.Failed(identity, platform.Now, 3, "power"); failures.ReportRecovery(identity, 3); }
            }
        }
        private bool RestoreJournal(bool? epp)
        {
            Journal journal;
            if (!Load(out journal)) return false;
            List<Value> values = journal.Values; Guid owner = journal.Owner;
            if (values.Count == 0) return true;
            var currentPlatform = platform as ILaboratoryCurrentPowerPlatform;
            int status = journal.Legacy ? platform.OwnerStatus(owner) : currentPlatform.PlanStatus(owner);
            if (status == 0) return Save(journal, new List<Value>());
            if (status != 1) return false;
            var pending = new List<Value>();
            bool changed = false, ok = true, selected = false;
            foreach (Value value in values)
            {
                if (epp.HasValue && IsEpp(value.Setting) != epp.Value) { pending.Add(value); continue; }
                selected = true;
                if (value.ManagedOnly && platform.OwnerStatus(owner) != 1)
                { pending.Add(value); ok = false; continue; }
                if (!JournalAuthorized(journal)) { pending.Add(value); ok = false; continue; }
                uint? current = platform.Read(owner, value.Setting);
                if (!current.HasValue) { pending.Add(value); ok = false; continue; }
                if (current.Value == value.Original) continue;
                if (current.Value != value.Wanted) continue;
                changed = true;
                if (!platform.Write(owner, value.Setting, value.Original)
                    || platform.Read(owner, value.Setting) != value.Original) { pending.Add(value); ok = false; }
            }
            if (!selected) return true;
            // Keep the full receipt if activation fails, even when the index is already restored.
            if ((changed || selected) && !platform.Flush(owner)) return false;
            return Save(journal, pending) && ok;
        }
    }
}
