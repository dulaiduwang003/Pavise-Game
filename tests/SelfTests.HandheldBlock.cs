// 作者：bdth 2074055628@qq.com
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
            string[] blocked = { PolicyCatalog.KeyCacheWarm };
            Eq(blocked.Length, PolicyCatalog.HandheldBlocked.Length);
            foreach (string key in blocked) Eq(true, PolicyCatalog.IsHandheldBlocked(key));
            Eq(false, PolicyCatalog.IsHandheldBlocked(PolicyCatalog.KeyAggressive));
            Eq(false, PolicyCatalog.IsHandheldBlocked(PolicyCatalog.KeyStandbyCleaner));
            Eq(false, PolicyCatalog.IsHandheldBlocked(null));

            string root = Path.Combine(Path.GetTempPath(), "PaviseHandheldBlock-" + Guid.NewGuid().ToString("N"));
            CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            bool? isolationSupport = AutoCores.SupportedForTest;
            Func<ulong> allocated = AutoCores.AllocatedForTest;
            try
            {
                CpuTopology.InjectTopologyForTest(0xFFF, new ulong[] { 3, 12, 48, 192, 256, 512, 1024, 2048 },
                    new ulong[0], 0xFF, 0xF00, 0, 0, true, false);
                AutoCores.SupportedForTest = true;
                AutoCores.AllocatedForTest = delegate { return 0; };
                using (var f = new FamilyPolicyFixture(root, "handheld"))
                {
                    GameMode mode = f.Mode;
                    GameProfile profile = f.First;
                    profile.Overrides.Remove(PolicyCatalog.KeyPreset);
                    foreach (string key in blocked) Settings.Save(key, true);

                    mode.Preset = PerformancePreset.Competitive;
                    PolicySnapshot s = PolicyResolver.For(profile);
                    Eq(PerformancePreset.Competitive, s.Preset);
                    foreach (string key in blocked) Eq("1", s.ValueOf(key));
                    Eq(0UL, HandheldSessionPlan(mode, s).Exclusive);
                    FamilyPolicySetField(mode, "sessionPolicy", s);
                    Eq(true, HandheldLiveBool(mode, PolicyCatalog.KeyCacheWarm));

                    mode.ProbeSessionPolicyClear();
                    mode.Preset = PerformancePreset.Handheld;
                    s = PolicyResolver.For(profile);
                    Eq(PerformancePreset.Handheld, s.Preset);
                    foreach (string key in blocked) Eq("0", s.ValueOf(key));
                    AutoCorePlan handheld = HandheldSessionPlan(mode, s);
                    Eq("schedule.manual.required", handheld.Blocked); Eq(0UL, handheld.Exclusive);
                    Eq(null, mode.ProbeGovernor);
                    Eq(((int)PerformancePreset.Handheld).ToString(), s.ValueOf(PolicyCatalog.KeyPreset));
                    Eq(profile.Id, s.ProfileId);
                    FamilyPolicySetField(mode, "sessionPolicy", s);
                    Eq(false, HandheldLiveBool(mode, PolicyCatalog.KeyCacheWarm));
                    Eq(true, HandheldLiveBool(mode, PolicyCatalog.KeyStandbyCleaner));
                    PolicySnapshot global = PolicyResolver.Global();
                    Eq(PerformancePreset.Handheld, global.Preset);
                    foreach (string key in blocked) Eq("0", global.ValueOf(key));
                    Eq("schedule.manual.required", HandheldSessionPlan(mode, global).Blocked);

                    foreach (string key in blocked) Eq(true, Settings.Load(key, false));
                    mode.ProbeSessionPolicyClear();
                    mode.Preset = PerformancePreset.Competitive;
                    s = PolicyResolver.For(profile);
                    foreach (string key in blocked) Eq("1", s.ValueOf(key));
                    Eq(0UL, HandheldSessionPlan(mode, s).Exclusive);
                    Eq(null, mode.ProbeGovernor);

                    profile.Overrides[PolicyCatalog.KeyPreset] = ((int)PerformancePreset.Handheld).ToString();
                    s = PolicyResolver.For(profile);
                    Eq(PerformancePreset.Handheld, s.Preset);
                    foreach (string key in blocked) Eq("0", s.ValueOf(key));
                    Eq("schedule.manual.required", HandheldSessionPlan(mode, s).Blocked);
                    global = PolicyResolver.Global();
                    Eq(PerformancePreset.Competitive, global.Preset);
                    Eq("1", global.ValueOf(PolicyCatalog.KeyCacheWarm));
                    Eq(false, HandheldSessionPlan(mode, global).Available);
                    profile.Overrides.Remove(PolicyCatalog.KeyPreset);

                    FamilyPolicySetField(mode, "sessionPolicy", null);
                    var form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                    GC.SuppressFinalize(form);
                    UiConfigSetField(form, "gameMode", mode);
                    UiConfigSetField(form, "elevated", true);
                    string[] toggleFields = { "swPolicyCacheWarm" };
                    string[] cardFields = { "cardPolicyCacheWarm" };
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
                }
            }
            finally
            {
                CpuTopology.RestoreTopologyForTest(topology);
                AutoCores.SupportedForTest = isolationSupport;
                AutoCores.AllocatedForTest = allocated;
                AutoCores.Publish(null);
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine("PASS HandheldBlock: snapshot, live preference, exclusive cores, policy page, per-game rows");
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

        private static AutoCorePlan HandheldSessionPlan(GameMode mode, PolicySnapshot snapshot)
        {
            FamilyPolicyInvoke(mode, "BeginAutoCores", snapshot, true);
            return AutoCores.Status.Plan;
        }
    }
}
#endif
