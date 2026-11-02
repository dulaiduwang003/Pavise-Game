using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class GraphicsDisableChecks
    {
        private static int checks;
        internal static Fake HagsStore = new Fake();
        internal static bool HagsKnown = true, HagsSupported = true;
        internal static string DirectXRegistryRaw;
        internal static int DirectXMemoryWrites;
        internal sealed class Fake : IGraphicsPreferenceStore
        {
            internal string Value = "1", Receipt = "", FailPhase;
            internal int Writes, Reads, ChangeAtRead, ReadFailAtWrites;
            internal string ChangeValue;
            internal bool ReadFails, WriteFails, WriteThenFail, ReadAfterWriteFails, FailClear;
            public string ReadValue()
            {
                Reads++;
                if (ReadFails || (ReadAfterWriteFails && Writes > 0) || (ReadFailAtWrites > 0 && Writes >= ReadFailAtWrites)) throw new InvalidOperationException("Unreadable");
                if (ChangeAtRead == Reads) Value = ChangeValue;
                return Value;
            }
            public bool WriteValue(string value)
            {
                Writes++;
                if (!WriteFails || WriteThenFail) Value = value;
                return !WriteFails;
            }
            public string LoadReceipt() { return Receipt; }
            public bool SaveReceipt(string value)
            {
                if ((FailClear && value == "") || (FailPhase != null && value.EndsWith("|" + FailPhase, StringComparison.Ordinal))) return false;
                Receipt = value; return true;
            }
        }

        private static void Check(bool ok, string message)
        { checks++; if (!ok) throw new Exception("FAIL: " + message); }

        private static void Transactions()
        {
            var fake = new Fake(); var tx = new GraphicsPreferenceTransaction(fake);
            Check(tx.Set("0") && fake.Value == "0", "external On can explicitly become Off");
            Check(fake.Receipt == GraphicsPreferenceTransaction.Receipt("1", "0", 'A'), "Off owns original On and applied Off");
            Check(new GraphicsPreferenceTransaction(fake).Restore() && fake.Value == "1" && fake.Receipt == "", "new process restores external original On");
            fake = new Fake { Value = null }; tx = new GraphicsPreferenceTransaction(fake);
            Check(tx.Set("0") && tx.Restore() && fake.Value == null, "absent original restored as absence");
            fake = new Fake { Value = "0" }; tx = new GraphicsPreferenceTransaction(fake);
            Check(tx.Set("1") && tx.Set("0") && fake.Receipt == "", "returning to baseline releases ownership");
            fake = new Fake { Value = "default" }; tx = new GraphicsPreferenceTransaction(fake);
            Check(tx.Set("1") && tx.Set("0") && fake.Value == "0", "retarget preserves first original");
            Check(tx.Restore() && fake.Value == "default", "first original survives multiple targets");
            foreach (bool partialWrite in new[] { false, true })
            {
                fake = new Fake { Value = null }; tx = new GraphicsPreferenceTransaction(fake);
                Check(tx.Set("1"), "retarget failure starts with owned On");
                fake.WriteFails = true; fake.WriteThenFail = partialWrite;
                Check(!tx.Set("0") && fake.Receipt.StartsWith("G3|", StringComparison.Ordinal), "retarget failure preserves previous and requested applied values");
                fake.WriteFails = false;
                Check(tx.Restore() && fake.Value == null, "retarget failure restores original whether setter left old On or wrote new Off");
            }
            fake = new Fake { Value = null }; tx = new GraphicsPreferenceTransaction(fake); tx.Set("1");
            fake.ReadAfterWriteFails = true;
            Check(!tx.Set("0") && fake.Receipt.Length != 0, "retarget initial unreadability preserves old lease");
            fake.ReadAfterWriteFails = false;
            Check(tx.Restore() && fake.Value == null, "retarget initial read failure restores prior applied value");
            fake = new Fake { Value = null }; tx = new GraphicsPreferenceTransaction(fake); tx.Set("1");
            fake.ReadFailAtWrites = 2;
            Check(!tx.Set("0") && fake.Value == "0" && fake.Receipt.StartsWith("G3|", StringComparison.Ordinal), "retarget write readback failure retains previous and next candidates");
            fake.ReadFailAtWrites = 0;
            Check(tx.Restore() && fake.Value == null, "retarget write readback failure restores initial absence");
            fake = new Fake { Value = null }; tx = new GraphicsPreferenceTransaction(fake); tx.Set("1"); fake.FailPhase = "P";
            Check(!tx.Set("0") && fake.Value == "1" && fake.Receipt == GraphicsPreferenceTransaction.Receipt(null, "1", 'A'), "failed retarget prepare preserves prior active receipt");
            fake.FailPhase = null;
            Check(tx.Restore() && fake.Value == null, "failed retarget prepare remains recoverable");
            fake = new Fake { Value = null }; tx = new GraphicsPreferenceTransaction(fake); tx.Set("1");
            fake.FailPhase = "A";
            Check(!tx.Set("0") && fake.Value == "0" && fake.Receipt.StartsWith("G3|", StringComparison.Ordinal), "retarget commit failure retains transition receipt");
            fake.FailPhase = null; fake.WriteFails = true;
            Check(!tx.Restore() && fake.Receipt.EndsWith("|R"), "retarget rollback failure retains exact observed applied value");
            fake.WriteFails = false;
            Check(tx.Restore() && fake.Value == null, "retarget rollback retry restores the initial baseline");
            fake = new Fake(); tx = new GraphicsPreferenceTransaction(fake);
            Check(tx.Set("1") && fake.Writes == 0 && fake.Receipt == "", "already exact target does not claim external ownership");
            fake = new Fake { FailPhase = "P" }; tx = new GraphicsPreferenceTransaction(fake);
            Check(!tx.Set("0") && fake.Writes == 0 && fake.Value == "1", "failed durable prepare forbids write");
            fake = new Fake { FailPhase = "A" }; tx = new GraphicsPreferenceTransaction(fake);
            Check(!tx.Set("0") && fake.Value == "0" && fake.Receipt.EndsWith("|P"), "failed commit retains prepared recovery");
            fake.FailPhase = null;
            Check(tx.Restore() && fake.Value == "1", "prepared recovery restores a completed write");
            fake = new Fake { WriteFails = true }; tx = new GraphicsPreferenceTransaction(fake);
            Check(!tx.Set("0") && fake.Receipt.EndsWith("|P"), "failed setter retains receipt");
            Check(tx.Restore() && fake.Receipt == "" && fake.Value == "1", "failed untouched setter can resolve original");
            fake = new Fake { WriteFails = true, WriteThenFail = true }; tx = new GraphicsPreferenceTransaction(fake);
            Check(!tx.Set("0") && fake.Value == "0", "false setter result may have changed state");
            fake.WriteFails = false;
            Check(tx.Restore() && fake.Value == "1", "false setter result never drops ownership of a completed write");
            fake = new Fake { ReadAfterWriteFails = true }; tx = new GraphicsPreferenceTransaction(fake);
            Check(!tx.Set("0") && fake.Receipt.EndsWith("|P"), "unreadable readback retains debt");
            fake.ReadAfterWriteFails = false;
            Check(tx.Restore() && fake.Value == "1", "readback debt recovers");
            fake = new Fake(); tx = new GraphicsPreferenceTransaction(fake);
            Check(tx.Set("0"), "cleanup scenario prepared");
            fake.FailClear = true;
            Check(!tx.Restore() && fake.Value == "1" && fake.Receipt.EndsWith("|F"), "cleanup failure keeps finished phase");
            fake.Value = "0"; fake.FailClear = false;
            int writes = fake.Writes;
            Check(tx.Restore() && fake.Value == "0" && fake.Writes == writes, "finished cleanup never overwrites later external writer even when it equals the old target");
            fake = new Fake { ReadFails = true, Receipt = GraphicsPreferenceTransaction.Receipt("1", "0", 'F') }; tx = new GraphicsPreferenceTransaction(fake);
            Check(tx.Restore() && fake.Writes == 0 && fake.Receipt == "", "finished cleanup needs no native read or write");
            fake = new Fake(); tx = new GraphicsPreferenceTransaction(fake); tx.Set("0");
            fake.Value = "external"; writes = fake.Writes;
            Check(tx.Restore() && fake.Value == "external" && fake.Writes == writes && fake.Receipt == "", "external ownership respected");
            fake = new Fake(); tx = new GraphicsPreferenceTransaction(fake); tx.Set("0");
            fake.Value = "external";
            Check(tx.Set("1") && fake.Receipt == GraphicsPreferenceTransaction.Receipt("external", "1", 'A'), "new explicit request snapshots new external baseline");
            Check(tx.Restore() && fake.Value == "external", "new lease restores new baseline");
            fake = new Fake { ReadFails = true, Receipt = GraphicsPreferenceTransaction.Receipt("1", "0", 'A') }; tx = new GraphicsPreferenceTransaction(fake);
            Check(!tx.Restore() && fake.Receipt.Length != 0 && fake.Writes == 0, "unknown current value preserves recovery");
            foreach (string corrupt in new[] { "bad", "G2|!|MA==|A", "G2|MQ==|MA==|Z", "G2|MQ==|MA==|AA" })
            {
                fake = new Fake { Receipt = corrupt }; tx = new GraphicsPreferenceTransaction(fake);
                Check(!tx.Set("0") && !tx.Restore() && fake.Receipt == corrupt && fake.Writes == 0, "corrupt receipt retained: " + corrupt);
            }
            fake = new Fake { ChangeAtRead = 2, ChangeValue = "external" }; tx = new GraphicsPreferenceTransaction(fake);
            Check(!tx.Set("0") && fake.Value == "external" && fake.Writes == 0, "concurrent writer before apply is preserved");
            fake = new Fake { Value = "0", Receipt = GraphicsPreferenceTransaction.Receipt("1", "0", 'A'), ChangeAtRead = 2, ChangeValue = "external" }; tx = new GraphicsPreferenceTransaction(fake);
            Check(tx.Restore() && fake.Value == "external" && fake.Writes == 0, "concurrent writer before restore is preserved");
        }

        private static void DirectX()
        {
            var fake = new Fake { Value = null };
            var dx = new DirectXGlobalPreference("test", true, delegate { return true; }, fake);
            bool on;
            Check(dx.TryReadState(out on) && on, "absent windowed preference means supported OS default On");
            Check(dx.Disable() && fake.Value == "0" && dx.HasResidue, "default/external windowed On explicitly disabled");
            Check(dx.TryReadState(out on) && !on && dx.HasResidue, "owned Off never masquerades as On");
            Check(dx.Restore() && fake.Value == null && !dx.HasResidue, "windowed default original retained");
            fake = new Fake(); dx = new DirectXGlobalPreference("test", false, delegate { return true; }, fake);
            Check(dx.Disable() && fake.Value == "0" && dx.HasResidue, "external VRR On can be disabled");
            Check(dx.Enable() && fake.Value == "1" && !dx.HasResidue, "reverse switch restores original On directly");
            fake = new Fake { Value = "0" }; dx = new DirectXGlobalPreference("test", false, delegate { return false; }, fake);
            Check(!dx.Enable() && !dx.Disable() && fake.Writes == 0, "unsupported DirectX does not change either request");
            fake = new Fake { Value = "unknown" }; dx = new DirectXGlobalPreference("test", false, delegate { return true; }, fake);
            Check(!dx.TryReadState(out on) && !dx.Disable() && !dx.Enable() && fake.Writes == 0, "unrecognized DirectX value is not guessed Off");
            fake = new Fake { ReadFails = true }; dx = new DirectXGlobalPreference("test", false, delegate { return true; }, fake);
            Check(!dx.Disable() && fake.Writes == 0, "unreadable DirectX never changes state");
            Settings.Reset(); DirectXRegistryRaw = "OtherSetting=7;SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;"; DirectXMemoryWrites = 0;
            Check(DirectXGlobalPreference.Windowed.Disable() && PrefFieldText.ReadField(DirectXRegistryRaw, "SwapEffectUpgradeEnable") == "0", "actual DirectX adapter explicitly disables external On");
            Check(PrefFieldText.ReadField(DirectXRegistryRaw, "OtherSetting") == "7" && PrefFieldText.ReadField(DirectXRegistryRaw, "VRROptimizeEnable") == "1", "DirectX adapter preserves unrelated fields");
            Check(DirectXGlobalPreference.Windowed.Restore() && PrefFieldText.ReadField(DirectXRegistryRaw, "SwapEffectUpgradeEnable") == "1", "actual DirectX adapter restores original field");
            foreach (bool legacy in new[] { false, true })
            {
                Settings.Reset(); DirectXRegistryRaw = "OtherSetting=7;SwapEffectUpgradeEnable=" + (legacy ? "1" : "0") + ";"; DirectXMemoryWrites = 0;
                string invalid = "invalid;OtherSetting=0";
                Settings.Values["DirectXOwned_SwapEffectUpgradeEnable"] = legacy ? "value:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(invalid)) : GraphicsPreferenceTransaction.Receipt(invalid, "0", 'A');
                string before = DirectXRegistryRaw;
                Check(!DirectXGlobalPreference.Windowed.Restore() && DirectXMemoryWrites == 0 && DirectXRegistryRaw == before, "invalid original in DirectX receipt cannot write registry fields");
                Check(DirectXGlobalPreference.Windowed.HasResidue, "invalid DirectX original retains recovery evidence");
            }
        }

        private static void Sam()
        {
            Settings.Reset(); AdlxTweaks.Reset();
            Check(AmdSamTweak.Disable() && !AdlxTweaks.Enabled, "actual SAM Off entry works for externally enabled SAM");
            Check(AmdSamTweak.HasResidue() && !AmdSamTweak.EnabledByPavise, "owned SAM Off has recovery without falsely enabled marker");
            Check(AmdSamTweak.Restore() && AdlxTweaks.Enabled && !AmdSamTweak.HasResidue(), "SAM restores original external On");
            Settings.Reset(); AdlxTweaks.Reset(); AdlxTweaks.Supported = false;
            Check(!AmdSamTweak.Enable() && !AmdSamTweak.Disable() && AdlxTweaks.Writes == 0, "unsupported SAM blocks requests");
            Settings.Reset(); AdlxTweaks.Reset(); AdlxTweaks.Known = false;
            Check(!AmdSamTweak.Disable() && AdlxTweaks.Writes == 0, "unknown SAM never treated as Off");
            Settings.Reset(); AdlxTweaks.Reset(); Settings.FailContains = "|P";
            Check(!AmdSamTweak.Disable() && AdlxTweaks.Writes == 0, "SAM requires durable receipt before driver write");
            Settings.Reset(); AdlxTweaks.Reset(); AdlxTweaks.FailAfterWrite = true;
            Check(!AmdSamTweak.Disable() && AmdSamTweak.HasResidue(), "SAM unverified write retains recovery");
            AdlxTweaks.FailAfterWrite = false; AdlxTweaks.Known = true;
            Check(AmdSamTweak.Restore() && AdlxTweaks.Enabled, "SAM unverified write can recover later");
            Settings.Reset(); AdlxTweaks.Reset(); Settings.Values["AmdSamByPavise"] = "1";
            Check(AmdSamTweak.Restore() && !AdlxTweaks.Enabled && !AmdSamTweak.HasResidue(), "legacy SAM marker migrates original Off");
            Settings.Reset(); AdlxTweaks.Reset(); AmdSamTweak.Disable(); AdlxTweaks.Known = false;
            Check(!AmdSamTweak.Restore() && AmdSamTweak.HasResidue(), "SAM unreadable restoration cannot clear receipt");
            Settings.Reset(); AdlxTweaks.Reset(); AdlxTweaks.Enabled = false;
            Check(AmdSamTweak.Enable() && AmdSamTweak.EnabledByPavise, "SAM enable owns verified original Off");
            Check(AmdSamTweak.Disable() && !AdlxTweaks.Enabled && !AmdSamTweak.HasResidue(), "SAM Off returns original baseline without phantom ownership");
        }

        private static void Hags()
        {
            Settings.Reset(); HagsKnown = HagsSupported = true; HagsStore = new Fake { Value = "2" };
            Check(HagsTweak.Disable() && HagsStore.Value == "1", "HAGS actual Off entry saves disabled request");
            Check(!HagsTweak.EnabledByPavise && HagsStore.Receipt.Length != 0, "HAGS Off request has independent recovery ownership");
            Check(HagsTweak.Restore() && HagsStore.Value == "2" && HagsStore.Receipt.Length == 0, "HAGS actual recovery entry restores original request On");
            HagsStore = new Fake { Value = null };
            Check(HagsTweak.Enable() && HagsTweak.Disable() && HagsStore.Value == "1", "HAGS actual On/Off entries preserve absent baseline");
            Check(HagsTweak.Restore() && HagsStore.Value == null, "HAGS actual retarget recovery restores absent baseline");
            foreach (bool partial in new[] { false, true })
            {
                Settings.Reset(); HagsStore = new Fake { Value = null };
                Check(HagsTweak.Enable(), "actual HAGS transition begins owned On");
                HagsStore.WriteFails = true; HagsStore.WriteThenFail = partial;
                Check(!HagsTweak.Disable() && Settings.LoadStr("PrevHwSch", "").StartsWith("G3|", StringComparison.Ordinal), "actual failed HAGS Off preserves transition journal");
                HagsStore.WriteFails = false;
                Check(HagsTweak.HasResidue() && HagsTweak.Restore() && HagsStore.Value == null, "actual HAGS recovery uses transition decoder, never legacy restorer");
            }
            HagsStore = new Fake { Value = "2" }; HagsKnown = false;
            Check(!HagsTweak.Disable() && HagsStore.Writes == 0, "unverified HAGS state blocks requests");
            HagsKnown = true; HagsSupported = false;
            Check(!HagsTweak.Enable() && HagsStore.Writes == 0, "unsupported HAGS blocks enable");
            Settings.Values["PrevHwSch"] = "G3|LQ==|Mg==|MQ==|P";
            Check(!HagsPreference.UnverifiedLegacyReceipt, "pending HAGS transition is never sent to legacy registry decoder");
            Settings.Values["PrevHwSch"] = "malformed";
            Check(!HagsPreference.UnverifiedLegacyReceipt, "malformed receipt is retained by strict decoder");
            Settings.Values["PrevHwSch"] = "=2";
            Check(HagsPreference.UnverifiedLegacyReceipt, "typed old unverified backup retains historical restore route");
        }

        private static void Presentation()
        {
            var toggle = new Toggle(); var card = new SettingCard();
            PanelForm.ApplyGraphics(new EnvironmentPreferenceObservation { Readable = true, Supported = true, On = false, Owned = true }, card, toggle);
            Check(!toggle.Checked && toggle.Enabled && !card.Locked, "owned DirectX Off is displayed Off and remains writable");
            Check(card.Status.Contains("关闭"), "owned DirectX Off has explicit disabled status");
            PanelForm.ApplyGraphics(new EnvironmentPreferenceObservation { Readable = true, Supported = true, On = true, Owned = false }, card, toggle);
            Check(toggle.Checked && toggle.Enabled && !card.Locked, "external DirectX On can be switched Off without falsely owned status");
            PanelForm.ApplyGraphics(new EnvironmentPreferenceObservation { Readable = false, Supported = true, On = false, Owned = true }, card, toggle);
            Check(!toggle.Enabled && card.Status == "env.graphics.unknown", "unreadable graphics with a receipt is still unknown");
            PanelForm.ApplyGraphics(new EnvironmentPreferenceObservation { Readable = true, Supported = false, On = false }, card, toggle);
            Check(!toggle.Enabled, "unsupported graphics cannot be switched On");
            var external = PanelForm.DescribeHags(true, true, true, false, 2);
            Check(external.Checked && external.External && external.Editable, "external supported HAGS On is editable");
            var off = PanelForm.DescribeHags(true, true, true, false, 1);
            Check(!off.Checked && off.Editable && off.Status.Contains("请求关闭"), "HAGS Off request stays Off while current state awaits reboot");
            var debt = PanelForm.DescribeHags(false, false, false, true, 2);
            Check(debt.Checked && !debt.Editable && debt.Status.Contains("未核验"), "unverified saved HAGS request is not permission to enable");
            Check(!PanelForm.DescribeHags(true, false, false, false, null).Editable, "known unsupported HAGS remains disabled");
        }

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 0) Check(false, "intentional negative control");
                Transactions(); DirectX(); Sam(); Hags(); Presentation();
                Console.WriteLine("PASS " + checks + " graphics explicit-disable and recovery checks; native endpoints isolated"); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }

    internal static class Settings
    {
        internal static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        internal static string FailContains;
        internal static void Reset() { Values.Clear(); FailContains = null; }
        internal static string LoadStr(string key, string fallback) { string value; return Values.TryGetValue(key, out value) ? value : fallback; }
        internal static bool Load(string key, bool fallback) { string value = LoadStr(key, null); return value == null ? fallback : value == "1" || value == "true"; }
        internal static bool SaveStrDurable(string key, string value) { if (FailContains != null && value.Contains(FailContains)) return false; Values[key] = value; return true; }
        internal static bool Save(string key, bool value) { return SaveStrDurable(key, value ? "1" : "0"); }
    }
    internal static class AdlxTweaks
    {
        internal static bool Known, Supported, Enabled, FailAfterWrite; internal static int Writes;
        internal static void Reset() { Known = Supported = Enabled = true; Writes = 0; FailAfterWrite = false; }
        internal static bool SamGet(out bool supported, out bool enabled) { supported = Supported; enabled = Enabled; return Known; }
        internal static bool SamSet(bool value) { Writes++; Enabled = value; if (FailAfterWrite) Known = false; return true; }
    }
    internal static class Native { internal static int OsBuild() { return 26100; } }
    internal static class Lang { internal static string T(string value) { return value; } }
    internal sealed class ReversibleReg
    {
        internal const string Absent = "__pavise_absent__";
        internal ReversibleReg(object hive, string key, string name, object kind, string slot) { }
        internal bool HasBackup { get { return GraphicsDisableChecks.HagsStore.Receipt.Length != 0 || Settings.LoadStr("PrevHwSch", "").Length != 0; } }
        internal bool Restore() { throw new InvalidOperationException("Legacy restorer forbidden for modern graphics receipts"); }
    }
    internal enum HardwareSchedulingState { Enabled, Disabled }
    internal sealed class HardwareSchedulingObservation { internal bool IsKnown; internal bool? SupportedByDriver; internal HardwareSchedulingState State; }
    internal static class Logger { internal static void Log(string text) { } }
    internal static class Theme { internal const int Dim = 1, Accent = 2, Warning = 3; }
    internal sealed class Toggle { internal bool Enabled, Checked; internal void SetSilently(bool value) { Checked = value; } }
    internal sealed class SettingCard { internal bool Locked; internal string Status; internal void SetStatus(string value, int ink) { Status = value; } internal void SetLock(string text, bool on) { Locked = on; } }
    internal sealed class EnvironmentPreferenceObservation { internal bool Readable, Supported, On, Owned; }
    internal sealed class HagsUiPresentation { internal bool Checked, Editable, External; internal int Ink; internal string Status; }
}
