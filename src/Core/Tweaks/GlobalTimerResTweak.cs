// 作者：bdth 2074055628@qq.com
using Microsoft.Win32;
using System;

namespace PaviseApp
{
    internal static class GlobalTimerResTweak
    {
        private const string KernelKey =
            @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

        private static readonly ReversibleReg Global = new ReversibleReg(
            Registry.LocalMachine, KernelKey, "GlobalTimerResolutionRequests",
            RegistryValueKind.DWord, "PrevGlobalTimerRes");

        private const string OnKey = "GlobalTimerResByPavise";
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }
        public static bool OwnsState { get { return EnabledByPavise || Global.HasBackup; } }
        public static bool CurrentlyOn() { return Global.Matches(1); }

        public static bool Enable()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return false;
#else
            lock (lk)
            {
                if (!Native.IsElevated()) return false;
                if (!Global.Apply(1)) { Logger.Log(Lang.T("log.gtimer.1")); return false; }
                if (!Settings.Save(OnKey, true)) return false;
                Logger.Log(Lang.T("log.gtimer.2"));
                return true;
            }
#endif
        }

        public static bool Restore()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return false;
#else
            lock (lk)
            {
                if (!Global.Restore()) { Logger.Log(Lang.T("log.gtimer.3")); return false; }
                if (!Settings.Save(OnKey, false)) return false;
                Logger.Log(Lang.T("log.gtimer.4"));
                return true;
            }
#endif
        }
    }

    internal sealed class TimerResolutionLease
    {
        internal delegate int Request(uint desired, bool acquire, out uint actual);
        internal delegate int Read(out uint coarsest, out uint finest, out uint current);
        internal Request Set;
        internal Read Query;
        internal TimerResolutionLease() { Set = NativeSet; Query = NativeQuery; }
        private readonly object gate = new object();
        internal bool Active { get; private set; }
        internal bool HasResidue { get { return Active || exemptionOwned; } }
        internal uint Actual { get; private set; }
        internal uint Finest { get; private set; }
        internal uint Coarsest { get; private set; }
        internal bool Verified { get; private set; }
        internal bool TargetReached { get { return Active && Verified && Actual > 0 && Actual <= Desired; } }
        internal bool Failed { get; private set; }
        internal bool Readback(out bool verified, out uint actual, out uint finest)
        {
            lock (gate) { verified = Verified; actual = Actual; finest = Finest; return Active; }
        }
        internal string State
        {
            get
            {
                lock (gate) return Failed ? HasResidue ? "restore-failed" : "stopped" : !Active ? ""
                    : !Verified ? "timer-unverified" : TargetReached ? "timer-locked" : "timer-limited";
            }
        }
        internal const uint Desired = 5000;
        internal const string FailureAction = "GlobalTimerResolutionLeaseV1";
        internal const int AuditSeconds = 5;
        private long nextAudit;
        private long releaseRetryAt;
        private int releaseFailures;
        internal void ResetAcquireAttempts()
        { lock (gate) nextAudit = 0; } // A mode change requests an audit, never a new failure permit.
        private bool exemptionOwned;
        private int originalControl, originalState;
        private int NativeSet(uint desired, bool acquire, out uint actual)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            actual = 0; return -1;
#else
            if (acquire && Native.OsBuild() >= 22000)
            {
                actual = 0;
                int currentControl, currentState;
                if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out currentControl, out currentState)) return -1;
                if (!exemptionOwned) { originalControl = currentControl; originalState = currentState; }
                if (!Native.TimerExemptOk(currentControl, currentState))
                {
                    // 只接管计时器位，保留原有 QoS 策略。
                    exemptionOwned = true;
                    if (!Native.RestorePowerThrottling(new IntPtr(-1), currentControl | 4, currentState & ~4)) return -1;
                    int control, state;
                    if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out control, out state)
                        || !Native.TimerExemptOk(control, state)) return -1;
                }
            }
            return Native.NtSetTimerResolution(desired, acquire, out actual);
#endif
        }
        private static int NativeQuery(out uint coarsest, out uint finest, out uint current)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            coarsest = finest = current = 0; return -1;
#else
            return Native.NtQueryTimerResolution(out coarsest, out finest, out current);
#endif
        }
        private bool CleanupExemption()
        {
            if (!exemptionOwned) return true;
            try
            {
                int control, state;
                if (!Native.TryQueryPowerThrottling(new IntPtr(-1), out control, out state)) return false;
                if (Native.TimerExemptOk(control, state)
                    && !Native.RestorePowerThrottling(new IntPtr(-1), (control & ~4) | (originalControl & 4),
                        (state & ~4) | (originalState & 4))) return false;
                exemptionOwned = false; return true;
            }
            catch { return false; }
        }
        internal bool Update(bool want, long now)
        {
            lock (gate) return UpdateLocked(want, now);
        }
        private bool UpdateLocked(bool want, long now)
        {
            if (!want)
            {
                nextAudit = 0;
                if (now < releaseRetryAt) return !HasResidue;
                if (!Active)
                {
                    Verified = false; Failed = !CleanupExemption();
                    if (Failed) { ActionFailureStore.Trip(FailureAction, "system", "Timer exemption restoration failed"); DelayRelease(now); }
                    else { releaseFailures = 0; releaseRetryAt = 0; }
                    return !Failed;
                }
                bool released = Release();
                if (!released) { ActionFailureStore.Trip(FailureAction, "system", "Timer request release failed"); DelayRelease(now); }
                else { releaseFailures = 0; releaseRetryAt = 0; }
                return released;
            }
            if (ActionFailureStore.IsBlocked(FailureAction, "system"))
            {
                if (HasResidue && now >= releaseRetryAt)
                { if (!Release()) DelayRelease(now); else { releaseFailures = 0; releaseRetryAt = 0; } }
                Failed = true; return false;
            }
            if (ActionFailureStore.IsRetryPending(FailureAction, "system"))
            {
                if (HasResidue && !Release()) { ActionFailureStore.Trip(FailureAction, "system", "Timer recovery incomplete before retry"); DelayRelease(now); return false; }
                nextAudit = 0;
            }
            if (Active)
            {
                if (now < nextAudit) return !Failed;
                if (!ReadCurrent(now)) return Isolate("Timer readback failed", now);
                if (TargetReached)
                { Failed = false; return true; }
                // 系统最低精度不足时保留请求，不反复改写。
                if (Finest > Desired) { Failed = false; return true; }
                // 每次重新请求前先成功释放，避免累积原生引用。
                if (!Release(false))
                { ActionFailureStore.Trip(FailureAction, "system", "Timer request release failed before maintenance"); DelayRelease(now); return false; }
                return Acquire(now);
            }
            return Acquire(now);
        }
        private bool Acquire(long now)
        {
            string blocked;
            if (!ActionFailureStore.TryBeginApply(FailureAction, "system", out blocked)) { Failed = true; return false; }
            try
            {
                uint actual;
                if (Set(Desired, true, out actual) >= 0)
                {
                    Active = true; Actual = actual; Failed = false;
                    if (!ReadCurrent(now)) return Isolate("Timer request accepted but readback failed", now);
                    if (!TargetReached && Finest <= Desired) return Isolate("Timer request did not reach the supported target", now);
                    if (!ActionFailureStore.CompleteApply(FailureAction, "system")) return Isolate("Timer isolation record persistence failed", now);
                    return true; // 请求被接受与达到目标精度是两种状态。
                }
            }
            catch { }
            Verified = false; return Isolate("Timer request failed", now);
        }
        private bool Isolate(string reason, long now)
        {
            ActionFailureStore.Trip(FailureAction, "system", reason);
            if (HasResidue && !Release()) DelayRelease(now);
            Failed = true; return false;
        }
        private void DelayRelease(long now)
        {
            int seconds = Math.Min(300, 30 * (1 << releaseFailures));
            releaseFailures = Math.Min(4, releaseFailures + 1);
            releaseRetryAt = now + TimeSpan.FromSeconds(seconds).Ticks;
        }
        private bool Release(bool cleanup = true)
        {
            if (!Active) { Failed = cleanup && !CleanupExemption(); return !Failed; }
            try
            {
                uint actual;
                if (Set(Desired, false, out actual) >= 0)
                {
                    Active = false; Actual = actual; Verified = false;
                    Failed = cleanup && !CleanupExemption(); return !Failed;
                }
            }
            catch { }
            Failed = true;
            return false; // 释放失败时保留租约，供退出流程重试。
        }
        private bool ReadCurrent(long now)
        {
            nextAudit = now + TimeSpan.FromSeconds(AuditSeconds).Ticks;
            Verified = false;
            try
            {
                uint coarsest, finest, current;
                if (Query(out coarsest, out finest, out current) >= 0
                    && finest > 0 && coarsest >= finest && current > 0)
                {
                    Coarsest = coarsest; Finest = finest; Actual = current; Verified = true;
                }
            }
            catch { }
            return Verified;
        }
    }

    internal sealed class TimerModeController
    {
        internal readonly object Gate = new object();
        internal Func<int> OsBuild = Native.OsBuild;
        internal Func<string> Boot = BootSessionIdentity.Read;
        internal Func<bool> TickOn = TimerTickTweak.CurrentlyOn;
        internal Func<bool> TickOwned = delegate { return TimerTickTweak.OwnsState; };
        internal Func<bool> TickEnable = TimerTickTweak.Enable, TickRestore = TimerTickTweak.Restore;
        internal Func<bool> GlobalOn = GlobalTimerResTweak.CurrentlyOn;
        internal Func<bool> GlobalOwned = delegate { return GlobalTimerResTweak.OwnsState; };
        internal Func<bool> GlobalEnable = GlobalTimerResTweak.Enable, GlobalRestore = GlobalTimerResTweak.Restore;
        internal volatile string TickState = "", GlobalState = "";
        internal bool GlobalReady { get { return GlobalState == "ready"; } }
        private int previousMode = -1;
        private long nextCheck;
        private int tickFailures, globalFailures;

        internal void SyncEnvironmentPreferences()
        {
            lock (Gate) Sync(AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyTimerTick),
                AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer), OsBuild(), DateTime.UtcNow.Ticks);
        }

        internal void Sync(bool tickWanted, bool globalWanted, int build, long now)
        {
            lock (Gate) SyncLocked(tickWanted, globalWanted, build, now);
        }

        private void SyncLocked(bool tickWanted, bool globalWanted, int build, long now)
        {
            int mode = (tickWanted ? 1 : 0) | (globalWanted ? 2 : 0);
            if (mode == previousMode && now < nextCheck
                && !ActionFailureStore.IsRetryPending(PolicyCatalog.KeyTimerTick, "system")
                && !ActionFailureStore.IsRetryPending(PolicyCatalog.KeyGlobalTimer, "system")) return;
            previousMode = mode;
            // 写入前记录启动标记，同一系统启动内重开软件不算重启生效。
            string boot;
            try { boot = Boot(); } catch { boot = null; }
            TickState = Step("AutoTimerTickBoot", tickWanted, boot, TickOn, TickOwned, TickEnable, TickRestore);
            if (TickState == "failed" || TickState == "restore-failed") tickFailures = Math.Min(8, tickFailures + 1);
            else tickFailures = 0;
            bool legacyGlobal = build > 0 && build < 19041;
            bool registryGlobal = build >= 20348;
            GlobalState = Step("AutoGlobalTimerBoot", globalWanted && registryGlobal, boot,
                    GlobalOn, GlobalOwned, GlobalEnable, GlobalRestore);
            if (GlobalState == "failed" || GlobalState == "restore-failed") globalFailures = Math.Min(8, globalFailures + 1);
            else globalFailures = 0;
            if (globalWanted && !registryGlobal && GlobalState != "restore-failed")
                GlobalState = legacyGlobal ? "ready" : "unsupported";
            bool failed = TickState == "failed" || TickState == "stopped" || TickState == "restore-failed"
                || GlobalState == "failed" || GlobalState == "stopped" || GlobalState == "restore-failed";
            int failures = Math.Max(tickFailures, globalFailures);
            nextCheck = now + TimeSpan.FromSeconds(failed ? Math.Min(300, 30 * (1 << Math.Min(4, Math.Max(0, failures - 1)))) : 300).Ticks;
        }

        internal static string Step(string slot, bool want, string boot, Func<bool> read,
            Func<bool> owned, Func<bool> enable, Func<bool> restore)
        {
            string action = slot == "AutoTimerTickBoot" ? PolicyCatalog.KeyTimerTick : slot == "AutoGlobalTimerBoot" ? PolicyCatalog.KeyGlobalTimer : slot;
            bool admitted = false;
            try
            {
                string saved = Settings.LoadStr(slot, "");
                if (want)
                {
                    if (ActionFailureStore.IsBlocked(action, "system"))
                        return RestoreOwnedTimer(owned, restore) ? "stopped" : "restore-failed";
                    if (ActionFailureStore.IsRetryPending(action, "system") && owned())
                    {
                        if (!restore() || owned()) { ActionFailureStore.Trip(action, "system", "Timer setting recovery incomplete before retry"); return "restore-failed"; }
                    }
                    string reason;
                    if (!ActionFailureStore.TryBeginApply(action, "system", out reason)) return "stopped";
                    admitted = true;
                    bool on = read();
                    if (!on)
                    {
                        // 启动身份未知时不能确认重启边界。
                        if (!BootSessionIdentity.Valid(boot)) { ActionFailureStore.CancelApply(action, "system"); return "unverified"; }
                        string marker = "on|" + boot;
                        if (!Settings.SaveStrDurable(slot, marker) || Settings.LoadStr(slot, "") != marker) throw new InvalidOperationException("Timer boot journal persistence failed");
                        saved = marker;
                        if (!on && !enable()) throw new InvalidOperationException("Timer setting apply failed");
                    }
                    if (!read()) throw new InvalidOperationException("Timer setting readback failed");
                    string next = RebootStatus(slot, saved, boot, "on", "ready", "pending-reboot");
                    if (next == "failed") throw new InvalidOperationException("Timer observation journal persistence failed");
                    if (ActionFailureStore.CompleteApply(action, "system")) return next;
                    if (!RestoreOwnedTimer(owned, restore)) return "restore-failed";
                    ActionFailureStore.CancelApply(action, "system"); return "stopped";
                }
                if (owned())
                {
                    if (!BootSessionIdentity.Valid(boot)) return "restore-failed";
                    string marker = "off|" + boot;
                    if (!Settings.SaveStrDurable(slot, marker) || Settings.LoadStr(slot, "") != marker) return "restore-failed";
                    saved = marker;
                    if (!restore() || owned()) return "restore-failed";
                }
                if (saved.StartsWith("off|", StringComparison.Ordinal) || saved.StartsWith("seen-off|", StringComparison.Ordinal))
                    return RebootStatus(slot, saved, boot, "off", "restored", "restore-pending-reboot");
                return "";
            }
            catch (NotSupportedException) { return SkipTimerApply(action, want, admitted, owned, restore, "unsupported"); }
            catch (OperationCanceledException) { return SkipTimerApply(action, want, admitted, owned, restore, ""); }
            catch (Exception error)
            {
                if (!want) return "restore-failed";
                ActionFailureStore.Trip(action, "system", error.Message);
                if (!RestoreOwnedTimer(owned, restore)) return "restore-failed";
                ActionFailureStore.CancelApply(action, "system");
                return "stopped";
            }
        }

        private static bool RestoreOwnedTimer(Func<bool> owned, Func<bool> restore)
        { try { return !owned() || restore() && !owned(); } catch { return false; } }
        private static string SkipTimerApply(string action, bool want, bool admitted,
            Func<bool> owned, Func<bool> restore, string state)
        {
            if (!want) return "restore-failed";
            if (!RestoreOwnedTimer(owned, restore))
            { ActionFailureStore.Trip(action, "system", "Timer setting recovery after skipped apply remains incomplete"); return "restore-failed"; }
            if (admitted) ActionFailureStore.CancelApply(action, "system"); return state;
        }

        private static string RebootStatus(string slot, string saved, string boot, string direction,
            string confirmed, string pending)
        {
            bool observed = saved.StartsWith("seen-" + direction + "|", StringComparison.Ordinal);
            string prefix = (observed ? "seen-" : "") + direction + "|";
            string prior = saved.StartsWith(prefix, StringComparison.Ordinal) ? saved.Substring(prefix.Length) : "";
            IrqRebootState relation = BootSessionIdentity.Compare(prior, boot);
            if (relation == IrqRebootState.Rebooted) return confirmed;
            if (relation == IrqRebootState.AwaitingReboot) return observed ? "unverified" : pending;
            if (BootSessionIdentity.Valid(boot) && !BootSessionIdentity.Valid(prior))
            {
                string marker = "seen-" + direction + "|" + boot;
                if (!Settings.SaveStrDurable(slot, marker) || Settings.LoadStr(slot, "") != marker)
                    return direction == "on" ? "failed" : "restore-failed";
            }
            return "unverified";
        }
    }
}
