#if PAVISE_LAB_AUDIO_EFFECTS_CHECKS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PaviseApp
{
    internal static class Lang { internal static int Cur = 1; }
    internal static class Settings
    {
        internal static bool TryLoadStr(string key, out string value) { throw new InvalidOperationException("Unmocked settings read"); }
        internal static bool SaveStrDurable(string key, string value) { throw new InvalidOperationException("Unmocked settings write"); }
    }
    internal static class LaboratoryAudioEffectsChecks
    {
        private sealed class Store : ILaboratoryManualStore
        {
            internal string Value = "";
            internal int Writes;
            internal bool FailRead;
            internal readonly HashSet<int> FailedWrites = new HashSet<int>();
            public bool Read(out string value) { value = Value; return !FailRead; }
            public bool Write(string value) { Writes++; if (FailedWrites.Contains(Writes)) return false; Value = value; return true; }
        }
        private sealed class Native : IAudioEffectsNative
        {
            internal readonly Dictionary<string, AudioEffectsEndpoint> Endpoints = new Dictionary<string, AudioEffectsEndpoint>();
            internal string DefaultId = "wired";
            internal int Reads, Writes;
            internal bool NoDefault, NoChangeOnce, FalseAfterWriteOnce, FailWriteOnce, NoFxStore, FxTypeMismatch, FxPresenceMismatch;
            internal int OrdinaryStoreReads;
            internal Action<int> BeforeRead;
            internal Native() { Endpoints.Add("wired", Endpoint("wired", AudioEffectsProperty.Empty)); }
            internal static AudioEffectsEndpoint Endpoint(string id, AudioEffectsProperty property)
            { return new AudioEffectsEndpoint { Id = id, Name = id + " output", Active = true, LegacyEffects = true, EffectIdentity = "effects-" + id, Property = property }; }
            private static AudioEffectsEndpoint Copy(AudioEffectsEndpoint e)
            { return new AudioEffectsEndpoint { Id = e.Id, Name = e.Name, Active = e.Active, LegacyEffects = e.LegacyEffects, ModernEffects = e.ModernEffects, EffectIdentity = e.EffectIdentity, Property = e.Property }; }
            public bool DefaultEndpoint(out AudioEffectsEndpoint endpoint, out string error)
            { if (NoDefault) { endpoint = null; error = "no default"; return false; } return Read(DefaultId, out endpoint, out error); }
            public bool Read(string id, out AudioEffectsEndpoint endpoint, out string error)
            {
                Reads++; if (BeforeRead != null) BeforeRead(Reads);
                OrdinaryStoreReads++;
                if (NoFxStore) { endpoint = null; error = "ordinary properties succeeded but FX store unavailable"; return false; }
                if (FxTypeMismatch || FxPresenceMismatch) { endpoint = null; error = "COM and actual FX property type/presence mismatch"; return false; }
                AudioEffectsEndpoint e;
                if (!Endpoints.TryGetValue(id, out e)) { endpoint = null; error = "endpoint disappeared"; return false; }
                endpoint = Copy(e); error = ""; return true;
            }
            public bool Write(string id, AudioEffectsProperty property, out string error)
            {
                Writes++; error = "";
                if (FailWriteOnce) { FailWriteOnce = false; error = "access denied"; return false; }
                if (NoChangeOnce) { NoChangeOnce = false; return true; }
                if (!Endpoints.ContainsKey(id)) { error = "endpoint disappeared"; return false; }
                Endpoints[id].Property = property;
                if (FalseAfterWriteOnce) { FalseAfterWriteOnce = false; error = "error after commit"; return false; }
                return true;
            }
        }
        private static int checks;
        private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); checks++; }
        private static LaboratoryAudioEffectsController Controller(Native n, Store s) { return new LaboratoryAudioEffectsController(n, s); }

        private static void NormalRoundTrips()
        {
            foreach (AudioEffectsProperty original in new[] { AudioEffectsProperty.Empty, AudioEffectsProperty.Enabled })
            {
                var n = new Native(); var s = new Store(); n.Endpoints["wired"].Property = original; var c = Controller(n, s); string error;
                Check(!c.HasReceipt && n.Writes == 0, "default wrote endpoint");
                Check(c.Inspect().CanApply && !c.Inspect().Applied, "supported default not admitted");
                Check(c.Apply(out error), "normal apply failed: " + error);
                Check(n.Endpoints["wired"].Property == AudioEffectsProperty.Disabled && n.Writes == 1 && c.HasReceipt, "apply not recorded");
                Check(c.Inspect().Applied && c.Inspect().CanRestore && c.Inspect().Summary.Contains("requested"), "readback overclaims/does not report request");
                int writes = n.Writes; Check(c.Apply(out error) && n.Writes == writes, "same apply was not idempotent");
                Check(c.Restore(out error), "normal restore failed: " + error);
                Check(n.Endpoints["wired"].Property == original && !c.HasReceipt, "original kind not restored exactly");
                writes = n.Writes; Check(c.Restore(out error) && n.Writes == writes, "empty restore wrote endpoint");
            }
        }
        private static void AdmissionAndNoBenefit()
        {
            Check(AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Empty, false, false, 0), "matching absent FX master switch rejected");
            Check(!AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Empty, true, true, 0)
                && !AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Empty, true, true, -1)
                && !AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Empty, true, false, 0), "existing FX value mistaken for absence");
            Check(AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Enabled, true, true, 0)
                && AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Disabled, true, true, 1), "matching explicit FX master rejected");
            Check(!AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Enabled, true, true, 1)
                && !AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Disabled, false, false, 0)
                && !AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Disabled, true, false, 1)
                && !AudioEffectsNative.FxPropertyMatches(AudioEffectsProperty.Disabled, true, true, 3), "COM/actual FX mismatch admitted");
            AudioEffectsProperty decoded;
            Check(AudioEffectsNative.DecodeProperty(0, 0, out decoded) && decoded == AudioEffectsProperty.Empty, "VT_EMPTY lost during decoding");
            Check(AudioEffectsNative.DecodeProperty(19, 0, out decoded) && decoded == AudioEffectsProperty.Enabled, "explicit DWORD0 became absent");
            Check(AudioEffectsNative.DecodeProperty(19, 1, out decoded) && decoded == AudioEffectsProperty.Disabled, "explicit DWORD1 not decoded");
            Check(!AudioEffectsNative.DecodeProperty(3, 0, out decoded) && !AudioEffectsNative.DecodeProperty(19, uint.MaxValue, out decoded), "invalid FX type/value decoded");
            foreach (AudioEffectsProperty original in new[] { AudioEffectsProperty.Empty, AudioEffectsProperty.Enabled, AudioEffectsProperty.Disabled })
            {
                ushort type; uint number; AudioEffectsNative.EncodeProperty(original, out type, out number);
                Check(type == (original == AudioEffectsProperty.Empty ? 0 : 19) && number == (original == AudioEffectsProperty.Disabled ? 1u : 0u), "property payload lost explicit create/delete semantics");
            }
            var n = new Native(); var s = new Store(); var c = Controller(n, s); string error;
            n.Endpoints["wired"].Property = AudioEffectsProperty.Disabled;
            Check(!c.Inspect().CanApply && c.Apply(out error) && !c.HasReceipt && n.Writes == 0, "already disabled was rewritten");
            n.Endpoints["wired"].Property = AudioEffectsProperty.Empty; n.Endpoints["wired"].LegacyEffects = false;
            Check(!c.Inspect().CanApply && !c.Apply(out error) && n.Writes == 0 && s.Writes == 0, "unregistered effects were admitted");
            n.Endpoints["wired"].ModernEffects = true;
            Check(c.Inspect().CanApply && c.Apply(out error), "documented modern effect request was not admitted");
            Check(c.Restore(out error), "modern effect restore failed");
            n.Endpoints["wired"].Active = false;
            Check(!c.Inspect().CanApply && !c.Apply(out error), "inactive endpoint admitted");
            n.NoDefault = true;
            Check(!c.Inspect().CanApply && !c.Apply(out error), "missing default admitted");
            n = new Native { NoFxStore = true }; s = new Store(); c = Controller(n, s);
            Check(!c.Inspect().CanApply && !c.Apply(out error) && n.OrdinaryStoreReads > 0 && n.Writes == 0 && s.Writes == 0,
                "ordinary endpoint store success was mistaken for an FX control backend");
            foreach (bool wrongType in new[] { false, true })
            {
                n = new Native { FxTypeMismatch = wrongType, FxPresenceMismatch = !wrongType }; s = new Store(); c = Controller(n, s);
                Check(!c.Inspect().CanApply && !c.Apply(out error) && n.Writes == 0 && s.Writes == 0, "FX type/presence mismatch allowed a native write");
            }
            foreach (int kind in new[] { 0, 1, 2 })
            {
                n = new Native(); s = new Store(); c = Controller(n, s);
                if (kind == 0) n.Endpoints["wired"].Id = "";
                if (kind == 1) n.Endpoints["wired"].EffectIdentity = "";
                if (kind == 2) n.Endpoints["wired"].Property = (AudioEffectsProperty)9;
                Check(!c.Inspect().CanApply && !c.Apply(out error) && n.Writes == 0, "invalid endpoint identity/property admitted");
            }
        }
        private static void DurableReceiptBeforeWrites()
        {
            var n = new Native(); var s = new Store(); s.FailedWrites.Add(1); var c = Controller(n, s); string error;
            Check(!c.Apply(out error) && n.Writes == 0 && !c.HasReceipt, "initial save failure wrote device");
            n = new Native(); s = new Store { FailRead = true }; c = Controller(n, s);
            Check(c.HasReceipt && !c.Inspect().CanApply && !c.Apply(out error) && !c.Restore(out error) && n.Writes == 0, "unreadable store did not block writes");
            n = new Native(); s = new Store(); s.FailedWrites.Add(2); c = Controller(n, s);
            Check(!c.Apply(out error) && n.Endpoints["wired"].Property == AudioEffectsProperty.Empty && !c.HasReceipt, "applied-save failure failed rollback");
        }
        private static void ReadbackAndPartialWrites()
        {
            foreach (int kind in new[] { 0, 1, 2 })
            {
                var n = new Native(); var s = new Store(); var c = Controller(n, s); string error;
                if (kind == 0) n.NoChangeOnce = true;
                if (kind == 1) n.FailWriteOnce = true;
                if (kind == 2) n.FalseAfterWriteOnce = true;
                Check(!c.Apply(out error), "unconfirmed/failed apply accepted");
                Check(n.Endpoints["wired"].Property == AudioEffectsProperty.Empty && !c.HasReceipt, "failed apply retained change");
            }
            var native = new Native(); var store = new Store(); var controller = Controller(native, store); string reason;
            Check(controller.Apply(out reason), "restore failure setup failed");
            native.FailWriteOnce = true;
            Check(!controller.Restore(out reason) && controller.HasReceipt && native.Endpoints["wired"].Property == AudioEffectsProperty.Disabled, "failed restore lost debt");
            Check(controller.Restore(out reason) && !controller.HasReceipt && native.Endpoints["wired"].Property == AudioEffectsProperty.Empty, "restore retry failed");
            native = new Native(); store = new Store(); controller = Controller(native, store);
            Check(controller.Apply(out reason), "post-commit error setup failed");
            native.FalseAfterWriteOnce = true;
            Check(controller.Restore(out reason) && !controller.HasReceipt, "confirmed original not accepted after HRESULT error");
        }
        private static void OwnershipAndDeviceIdentity()
        {
            var n = new Native(); var s = new Store(); var c = Controller(n, s); string error;
            Check(c.Apply(out error), "external setting setup failed"); n.Endpoints["wired"].Property = AudioEffectsProperty.Enabled;
            int writes = n.Writes;
            Check(c.Restore(out error) && n.Writes == writes && !c.HasReceipt && n.Endpoints["wired"].Property == AudioEffectsProperty.Enabled, "external explicit zero overwritten by original absence");
            Check(c.Apply(out error), "effects change setup failed"); n.Endpoints["wired"].EffectIdentity = "replacement-driver-effects"; writes = n.Writes;
            Check(!c.Restore(out error) && c.HasReceipt && n.Writes == writes && !c.Inspect().Applied, "effects identity change overwritten");
            n.Endpoints["wired"].EffectIdentity = "effects-wired";
            var original = n.Endpoints["wired"]; n.Endpoints.Remove("wired");
            Check(!c.Restore(out error) && c.HasReceipt && n.Writes == writes, "missing endpoint debt erased");
            n.Endpoints.Add("wired", original); Check(c.Restore(out error), "returning endpoint could not restore");

            n = new Native(); s = new Store(); c = Controller(n, s);
            Check(c.Apply(out error), "default switch setup failed");
            n.Endpoints.Add("usb", Native.Endpoint("usb", AudioEffectsProperty.Enabled)); n.DefaultId = "usb";
            Check(c.Apply(out error) && n.Endpoints["wired"].Property == AudioEffectsProperty.Empty && n.Endpoints["usb"].Property == AudioEffectsProperty.Disabled, "default switch did not restore old endpoint first");
            Check(c.Restore(out error) && n.Endpoints["usb"].Property == AudioEffectsProperty.Enabled, "new endpoint restore failed");
        }
        private static void FailureRecordsAndRaces()
        {
            var n = new Native(); var s = new Store(); var c = Controller(n, s); string error;
            n.BeforeRead = delegate(int read) { if (read == 2) n.Endpoints["wired"].Property = AudioEffectsProperty.Enabled; };
            Check(!c.Apply(out error) && n.Writes == 0 && !c.HasReceipt, "changed baseline was overwritten");
            n = new Native(); s = new Store(); c = Controller(n, s);
            Check(c.Apply(out error), "clear failure setup failed"); s.FailedWrites.Add(s.Writes + 2);
            Check(!c.Restore(out error) && c.HasReceipt && n.Endpoints["wired"].Property == AudioEffectsProperty.Empty, "record-clear failure did not retain restored debt");
            int writes = n.Writes; Check(c.Restore(out error) && !c.HasReceipt && n.Writes == writes, "already-restored debt repeated native write");
            foreach (string raw in new[] { "bad", "1;A;@@@;0;eA==;eA==", "1;X;d2lyZWQ=;0;ZQ==;bg==", "1;A;d2lyZWQ=;2;ZQ==;bg==", new string('x', 17000) })
            {
                n = new Native(); s = new Store { Value = raw }; c = Controller(n, s);
                Check(c.HasReceipt && !c.Inspect().CanApply && !c.Apply(out error) && !c.Restore(out error) && n.Writes == 0 && s.Value == raw, "corrupt receipt was erased or used to write");
            }
        }
        private static int Main()
        {
            try
            {
                NormalRoundTrips(); AdmissionAndNoBenefit(); DurableReceiptBeforeWrites(); ReadbackAndPartialWrites(); OwnershipAndDeviceIdentity(); FailureRecordsAndRaces();
                foreach (int language in new[] { 0, 1, 2 }) { Lang.Cur = language; var n = new Native(); n.Endpoints["wired"].LegacyEffects = false; var state = Controller(n, new Store()).Inspect(); Check(!state.CanApply && !string.IsNullOrEmpty(state.Reason), "localized unsupported reason missing"); }
                Console.WriteLine("PASS LaboratoryAudioEffects " + checks + " assertions; mocked endpoints/store only; no device writes, playback or recording."); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
#endif
