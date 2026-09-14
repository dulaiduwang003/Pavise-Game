// @author bdth 2074055628@qq.com
// File purpose Unload the software mitigations for CPU speculative-execution vulnerabilities, reclaiming the fixed cost of every kernel transition
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class SpecMitigationTweak
    {
        private const string MmKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

        private const int OverrideOff = 3;
        private const int OverrideMask = 3;

        private static readonly ReversibleReg Override = new ReversibleReg(
            Registry.LocalMachine, MmKey, "FeatureSettingsOverride", RegistryValueKind.DWord, "PrevSpecOverride");
        private static readonly ReversibleReg Mask = new ReversibleReg(
            Registry.LocalMachine, MmKey, "FeatureSettingsOverrideMask", RegistryValueKind.DWord, "PrevSpecMask");

        private static readonly object lk = new object();

        private const int KernelVaShadowClass = 196;
        private const int SpeculationControlClass = 201;

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int cls, out uint buffer, int len, out int returned);

        public struct State
        {
            public bool QueryOk;

            public bool KvaShadowEnabled;
            public bool KvaShadowRequired;

            public bool BpbEnabled;
            public bool RetpolineEnabled;
            public bool EnhancedIbrs;

            public bool MbClearEnabled;
            public bool MdsHardwareProtected;

            public bool SsbdSystemWide;

            public bool RecoverableCost
            {
                get
                {
                    if (!QueryOk) return false;
                    return KvaShadowEnabled
                        || RetpolineEnabled
                        || MbClearEnabled
                        || SsbdSystemWide
                        || (BpbEnabled && !EnhancedIbrs);
                }
            }
        }

        public static bool DisabledByPavise { get { return Settings.Load("SpecMitDisabledByPavise", false); } }

        // Only two software mitigations really cost every kernel transition: KPTI running, or Spectre v2 on the legacy per-transition IBRS path
        //   Retpoline and eIBRS are nearly free, MbClear SSBD and friends are cheap too; disabling those leaves only the security cost
        //   Originally the eligibility gate for the Extreme tier's mandatory items; with Extreme retired it is now just an honest line shown to the user in the UI
        //   Refusing writes is still handled by BlockedReason's RecoverableCost; this only affects the description text
        public static bool WorthDisabling(State st)
        {
            if (!st.QueryOk) return false;
            if (st.KvaShadowEnabled && st.KvaShadowRequired) return true;
            return st.BpbEnabled && !st.EnhancedIbrs && !st.RetpolineEnabled;
        }

        private static bool TryQueryClass(int cls, out uint flags)
        {
            flags = 0;
            try
            {
                int returned;
                return NtQuerySystemInformation(cls, out flags, 4, out returned) == 0 && returned >= 4;
            }
            catch { return false; }
        }

        public static State Query()
        {
            var st = new State();
            uint kva, spec;
            bool kvaOk = TryQueryClass(KernelVaShadowClass, out kva);
            bool specOk = TryQueryClass(SpeculationControlClass, out spec);
            st.QueryOk = kvaOk && specOk;
            if (!st.QueryOk) return st;

            st.KvaShadowEnabled = Bit(kva, 0);
            st.KvaShadowRequired = Bit(kva, 4);

            st.BpbEnabled = Bit(spec, 0);
            st.RetpolineEnabled = Bit(spec, 14);
            st.EnhancedIbrs = Bit(spec, 16);
            st.SsbdSystemWide = Bit(spec, 10);
            st.MbClearEnabled = Bit(spec, 25);
            st.MdsHardwareProtected = Bit(spec, 24);
            return st;
        }

        private static bool Bit(uint value, int index) { return ((value >> index) & 1u) != 0; }

        public static bool BlockedReason(out string reasonKey)
        {
            reasonKey = null;
            State st = Query();
            if (!st.QueryOk) { reasonKey = "spec.blocked.query"; return true; }
            if (!st.RecoverableCost) { reasonKey = "spec.blocked.hardwarefixed"; return true; }
            return false;
        }

        public static bool Disable()
        {
            lock (lk)
            {
                try
                {
                    string blockKey;
                    if (BlockedReason(out blockKey))
                    {
                        Logger.Log(Lang.T(blockKey));
                        return false;
                    }

                    bool ok = Override.Apply(OverrideOff) & Mask.Apply(OverrideMask);
                    if (!ok)
                    {
                        Override.Restore(); Mask.Restore();
                        Logger.Log(Lang.T("log.specmitigation.1"));
                        return false;
                    }

                    Settings.Save("SpecMitDisabledByPavise", true);
                    if (!Settings.Load("SpecMitDisabledByPavise", false))
                    {
                        Override.Restore(); Mask.Restore();
                        Logger.Log(Lang.T("log.specmitigation.2"));
                        return false;
                    }

                    Logger.Log(Lang.T("log.specmitigation.3"));

                    try
                    {
                        VbsTweak.State vbs = VbsTweak.Query();
                        if (vbs.WmiOk && vbs.VbsRunning) Logger.Log(Lang.T("log.specmitigation.4"));
                    }
                    catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log(Lang.T("log.specmitigation.5") + ex.Message);
                    return false;
                }
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                try
                {
                    if (!Override.HasBackup && !Mask.HasBackup && !DisabledByPavise) return true;

                    bool ok = Override.Restore() & Mask.Restore();
                    if (!ok)
                    {
                        Logger.Log(Lang.T("log.specmitigation.6"));
                        return false;
                    }

                    Settings.Save("SpecMitDisabledByPavise", false);
                    if (Settings.Load("SpecMitDisabledByPavise", true))
                    {
                        Logger.Log(Lang.T("log.specmitigation.7"));
                        return false;
                    }
                    Logger.Log(Lang.T("log.specmitigation.8"));
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log(Lang.T("log.specmitigation.9") + ex.Message);
                    return false;
                }
            }
        }

        public static bool EffectConfirmed(out bool stillPending)
        {
            stillPending = false;
            if (!DisabledByPavise) return false;
            State st = Query();
            if (!st.QueryOk) return false;
            if (st.RecoverableCost) { stillPending = true; return false; }
            return true;
        }

        public static string ActiveCostSummary(State st)
        {
            if (!st.QueryOk) return Lang.T("t.specmitigation.1");
            var parts = new System.Collections.Generic.List<string>();
            if (st.KvaShadowEnabled) parts.Add(Lang.T("t.specmitigation.2"));
            if (st.RetpolineEnabled) parts.Add(Lang.T("t.specmitigation.3"));
            if (st.BpbEnabled && !st.EnhancedIbrs) parts.Add(Lang.T("t.specmitigation.4"));
            if (st.MbClearEnabled) parts.Add(Lang.T("t.specmitigation.5"));
            if (st.SsbdSystemWide) parts.Add(Lang.T("t.specmitigation.6"));
            if (parts.Count == 0) return Lang.T("t.specmitigation.7");
            return string.Join("、", parts.ToArray());
        }
    }
}
