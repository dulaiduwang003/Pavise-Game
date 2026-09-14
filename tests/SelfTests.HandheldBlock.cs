// @author bdth 2074055628@qq.com
// File purpose The five items Handheld tier doesn't provide: snapshot, live resolution, core plan, Policy page, per-game page, Core Scheduling page all read as off
#if PAVISE_SELFTEST
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void RunHandheldBlockTests()
        {
            string[] blocked = { PolicyCatalog.KeyDisableCpuIdle, PolicyCatalog.KeyIdlePolicy,
                PolicyCatalog.KeyCacheWarm, PolicyCatalog.KeyVramShield };
            Eq(blocked.Length, PolicyCatalog.HandheldBlocked.Length);
            foreach (string key in blocked) Eq(true, PolicyCatalog.IsHandheldBlocked(key));
            Eq(false, PolicyCatalog.IsHandheldBlocked(PolicyCatalog.KeyAggressive));
            Eq(false, PolicyCatalog.IsHandheldBlocked(PolicyCatalog.KeyStandbyCleaner));
            Eq(false, PolicyCatalog.IsHandheldBlocked(null));

            string root = Path.Combine(Path.GetTempPath(), "PaviseHandheldBlock-" + Guid.NewGuid().ToString("N"));
            CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            bool? isolationSupport = CoreScheduling.IsolationSupportedForTest;
            try
            {
                CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
                CoreScheduling.IsolationSupportedForTest = true;
                using (var f = new FamilyPolicyFixture(root, "handheld"))
                {
                    GameMode mode = f.Mode;
                    GameProfile profile = f.First;
                    // The fixture presets a custom tier for the first game, follow the global tier here and test per-game tier changes separately later
                    profile.Overrides.Remove(PolicyCatalog.KeyPreset);
                    // All four on globally, per-game overrides two more to on, global core plan has exclusive on
                    foreach (string key in blocked) Settings.Save(key, true);
                    FamilyPolicySetField(mode, "disableCpuIdleOn", true);
                    profile.Overrides[PolicyCatalog.KeyDisableCpuIdle] = "1";
                    profile.Overrides[PolicyCatalog.KeyVramShield] = "1";
                    var plan = new CoreSchedulingPlan { Topology = CoreScheduling.CurrentStamp, GameMask = 0xF0, IsolationOn = true, IsolationMask = 0xF0 };
                    Settings.SaveStr(CoreScheduling.Key, plan.Encode());

                    // Esports tier: all five as usual
                    mode.Preset = PerformancePreset.Competitive;
                    PolicySnapshot s = PolicyResolver.For(profile);
                    Eq(PerformancePreset.Competitive, s.Preset);
                    foreach (string key in blocked) Eq("1", s.ValueOf(key));
                    Eq(true, s.CorePlan.IsolationOn);
                    Eq(0xF0UL, s.CorePlan.IsolationMask);
                    FamilyPolicySetField(mode, "sessionPolicy", s);
                    Eq(true, HandheldLiveCpuIdle(mode));
                    Eq(true, HandheldLiveBool(mode, PolicyCatalog.KeyCacheWarm));
                    Eq(true, HandheldLiveBool(mode, PolicyCatalog.KeyVramShield));

                    // Handheld tier: snapshot all off, live resolution off, exclusive off, core selection itself untouched, items not named are unaffected
                    mode.Preset = PerformancePreset.Handheld;
                    s = PolicyResolver.For(profile);
                    Eq(PerformancePreset.Handheld, s.Preset);
                    foreach (string key in blocked) Eq("0", s.ValueOf(key));
                    Eq(false, s.CorePlan.IsolationOn);
                    Eq(0UL, s.CorePlan.IsolationMask);
                    Eq(0xF0UL, s.CorePlan.GameMask);
                    Eq(((int)PerformancePreset.Handheld).ToString(), s.ValueOf(PolicyCatalog.KeyPreset));
                    Eq(profile.Id, s.ProfileId);
                    FamilyPolicySetField(mode, "sessionPolicy", s);
                    Eq(false, HandheldLiveCpuIdle(mode));
                    Eq(false, HandheldLiveBool(mode, PolicyCatalog.KeyCacheWarm));
                    Eq(false, HandheldLiveBool(mode, PolicyCatalog.KeyVramShield));
                    Eq(true, HandheldLiveBool(mode, PolicyCatalog.KeyStandbyCleaner));
                    PolicySnapshot global = PolicyResolver.Global();
                    Eq(PerformancePreset.Handheld, global.Preset);
                    foreach (string key in blocked) Eq("0", global.ValueOf(key));
                    Eq(false, global.CorePlan.IsolationOn);

                    // The user's own switches were not rewritten, switching back to Esports tier brings everything back, exclusive too
                    foreach (string key in blocked) Eq(true, Settings.Load(key, false));
                    Eq(true, profile.Overrides.ContainsKey(PolicyCatalog.KeyVramShield));
                    Eq(plan.Encode(), Settings.LoadStr(CoreScheduling.Key, ""));
                    mode.Preset = PerformancePreset.Competitive;
                    s = PolicyResolver.For(profile);
                    foreach (string key in blocked) Eq("1", s.ValueOf(key));
                    Eq(true, s.CorePlan.IsolationOn);
                    Eq(0xF0UL, s.CorePlan.IsolationMask);

                    // Global Esports, this game alone changed to Handheld tier, off for this match likewise, global snapshot unaffected
                    profile.Overrides[PolicyCatalog.KeyPreset] = ((int)PerformancePreset.Handheld).ToString();
                    s = PolicyResolver.For(profile);
                    Eq(PerformancePreset.Handheld, s.Preset);
                    foreach (string key in blocked) Eq("0", s.ValueOf(key));
                    Eq(false, s.CorePlan.IsolationOn);
                    global = PolicyResolver.Global();
                    Eq(PerformancePreset.Competitive, global.Preset);
                    Eq("1", global.ValueOf(PolicyCatalog.KeyCacheWarm));
                    Eq(true, global.CorePlan.IsolationOn);
                    profile.Overrides.Remove(PolicyCatalog.KeyPreset);

                    // Per-game page: under Handheld tier the four items are marked preset-forced off, other tiers untouched
                    bool effective;
                    foreach (string key in blocked)
                    {
                        Eq(true, PanelForm.CfgPresetForcesForTest(key, PerformancePreset.Handheld, out effective));
                        Eq(false, effective);
                        Eq(false, PanelForm.CfgPresetForcesForTest(key, PerformancePreset.Custom, out effective));
                        Eq(false, PanelForm.CfgPresetForcesForTest(key, PerformancePreset.Standard, out effective));
                    }
                    Eq(false, PanelForm.CfgPresetForcesForTest(PolicyCatalog.KeyStandbyCleaner, PerformancePreset.Handheld, out effective));

                    // Policy page: the four switches lock as preset-forced off, already-on ones lock too, switching back unlocks
                    FamilyPolicySetField(mode, "sessionPolicy", null);
                    var form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                    GC.SuppressFinalize(form);
                    UiConfigSetField(form, "gameMode", mode);
                    UiConfigSetField(form, "elevated", true);
                    string[] toggleFields = { "swPolicyDisableCpuIdle", "swPolicyIdlePolicy", "swPolicyCacheWarm", "swPolicyVramShield" };
                    string[] cardFields = { "cardPolicyDisableCpuIdle", "cardPolicyIdlePolicy", "cardPolicyCacheWarm", "cardPolicyVramShield" };
                    var toggles = new Toggle[toggleFields.Length];
                    var cards = new SettingCard[cardFields.Length];
                    for (int i = 0; i < toggles.Length; i++)
                    {
                        toggles[i] = new Toggle(); toggles[i].SetSilently(true);
                        cards[i] = new SettingCard();
                        UiConfigSetField(form, toggleFields[i], toggles[i]);
                        UiConfigSetField(form, cardFields[i], cards[i]);
                    }
                    try
                    {
                        mode.Preset = PerformancePreset.Handheld;
                        UiConfigCall(form, "RefreshPolicyPresentation");
                        for (int i = 0; i < toggles.Length; i++)
                        {
                            Eq(false, toggles[i].Enabled);
                            Eq(false, toggles[i].Checked);
                            Eq(Lang.T("v14.preset.forced.off"), HandheldLockText(cards[i]));
                        }
                        Eq(true, Settings.Load(PolicyCatalog.KeyCacheWarm, false));
                        mode.Preset = PerformancePreset.Competitive;
                        UiConfigCall(form, "RefreshPolicyPresentation");
                        for (int i = 0; i < toggles.Length; i++)
                        {
                            Eq(true, toggles[i].Enabled);
                            if (HandheldLockText(cards[i]) == Lang.T("v14.preset.forced.off"))
                                throw new InvalidOperationException(cardFields[i] + " still carries the handheld lock on the Competitive tier");
                        }
                    }
                    finally
                    {
                        foreach (Toggle t in toggles) t.Dispose();
                        foreach (SettingCard c in cards) c.Dispose();
                    }

                    // Core Scheduling page: exclusive toggle locked, description changes to not provided on Handheld tier, hard-lock card hidden, all back after unblocking
                    using (var panel = new CoreSchedulingPanel(900, null, delegate { return null; }, delegate { return false; }))
                    {
                        panel.Draft = plan.Clone();
                        panel.IsolationBlocked = delegate { return true; };
                        panel.RefreshView();
                        var exclusiveCard = HandheldPanelCard(panel, "exclusiveCard");
                        var hardCard = HandheldPanelCard(panel, "hardCard");
                        Eq(false, panel.IsolationToggle.Enabled);
                        Eq(true, panel.IsolationToggle.Checked);
                        Eq(Lang.T("schedule.exclusive.handheld"), exclusiveCard.Desc);
                        Eq(true, exclusiveCard.Visible);
                        Eq(false, hardCard.Visible);
                        panel.IsolationBlocked = delegate { return false; };
                        panel.RefreshView();
                        Eq(true, panel.IsolationToggle.Enabled);
                        Eq(true, hardCard.Visible);
                        if (exclusiveCard.Desc == Lang.T("schedule.exclusive.handheld"))
                            throw new InvalidOperationException("Exclusive card still explains the handheld block after unblocking");
                    }
                }
            }
            finally
            {
                CpuTopology.RestoreTopologyForTest(topology);
                CoreScheduling.IsolationSupportedForTest = isolationSupport;
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine("PASS HandheldBlock: snapshot, live preference, core plan, policy page, per-game rows, core scheduling panel");
        }

        private static bool HandheldLiveCpuIdle(GameMode mode)
        {
            MethodInfo method = typeof(GameMode).GetMethod("LiveCpuIdlePreference", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Missing GameMode.LiveCpuIdlePreference");
            return (bool)method.Invoke(mode, new object[] { PolicyCatalog.KeyDisableCpuIdle });
        }

        private static bool HandheldLiveBool(GameMode mode, string key)
        {
            MethodInfo method = typeof(GameMode).GetMethod("LiveBoolPreferenceLocked", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Missing GameMode.LiveBoolPreferenceLocked");
            return (bool)method.Invoke(mode, new object[] { key, true });
        }

        private static string HandheldLockText(SettingCard card)
        {
            FieldInfo field = typeof(SettingCard).GetField("lockText", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing SettingCard.lockText");
            return (string)field.GetValue(card);
        }

        private static SettingCard HandheldPanelCard(CoreSchedulingPanel panel, string name)
        {
            FieldInfo field = typeof(CoreSchedulingPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing CoreSchedulingPanel." + name);
            return (SettingCard)field.GetValue(panel);
        }
    }
}
#endif
