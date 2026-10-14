#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunUiConfigAuditRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseUiConfigAudit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string oldLog = Logger.LogPath;
            int oldLanguage = Lang.Cur;
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest();
                Logger.LogPath = Path.Combine(root, "decisions.log");
                Lang.Cur = 0;
                int failed = 0;
                Action<string>[] cases = {
                    RetiredPreferencesRejected,
                    UiConfigUnsupportedGlobalGraphicsRemainDisableable,
                    UiConfigCachedAmdSupportNeedsApi,
                    UiConfigGraphicsBindingContracts,
                    UiConfigGraphicsPresentationDoesNotProbe,
                    UiConfigGlobalLaneRecoversAfterHandheld };
                foreach (Action<string> test in cases)
                {
                    try { test(root); Console.WriteLine("PASS " + test.Method.Name); }
                    catch (Exception error)
                    {
                        while (error.InnerException != null) error = error.InnerException;
                        failed++;
                        Console.WriteLine("FAIL " + test.Method.Name + ": " + error.Message);
                    }
                }
                if (failed != 0) throw new InvalidOperationException("UI config audit failures=" + failed);
                Console.WriteLine("PASS UI config audit windows_shown=false affinity_writes=0 registry=mocked");
                return cases.Length;
            }
            finally
            {
                Lang.Cur = oldLanguage;
                Logger.ResetWriteBarrierForTest();
                Logger.LogPath = oldLog;
                Settings.UseTransientStoreForCurrentProcess();
                string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (actual.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(actual).StartsWith("PaviseUiConfigAudit-", StringComparison.Ordinal)
                    && Directory.Exists(actual)) Directory.Delete(actual, true);
            }
        }

        private static void UiConfigGlobalLaneRecoversAfterHandheld(string root)
        {
            CpuTopology.TopologySnapshot topology = CpuTopology.CaptureTopologyForTest();
            try
            {
                CpuTopology.InjectTopologyForTest(63, new ulong[] { 1, 2, 4, 8, 16, 32 }, new ulong[] { 63 },
                    0, 0, 0, 0, false, false);
                using (var f = new UiConfigFixture(root, "lane-global"))
                using (var toggle = new Toggle())
                using (var card = new SettingCard())
                {
                    UiConfigSetField(f.Form, "swPolicyLane", toggle);
                    UiConfigSetField(f.Form, "cardPolicyLane", card);
                    foreach (bool savedOn in new[] { true, false })
                    {
                        f.Family.Mode.RenderLaneOn = savedOn;
                        foreach (PerformancePreset mode in new[] { PerformancePreset.Competitive,
                            PerformancePreset.Handheld, PerformancePreset.Competitive,
                            PerformancePreset.Standard, PerformancePreset.Handheld,
                            PerformancePreset.Competitive })
                        {
                            FamilyPolicySetField(f.Family.Mode, "preset", mode);
                            toggle.SetSilently(savedOn);
                            UiConfigCall(f.Form, "SyncPolicyLane");
                            bool supported = mode != PerformancePreset.Handheld;
                            Eq(supported && savedOn, toggle.Checked);
                            Eq(supported, toggle.Enabled);
                            Eq(Lang.T(supported ? "gm.lane.sub" : "gm.lane.unsupported"), card.Desc);
                            Eq(savedOn, f.Family.Mode.RenderLaneOn);
                        }
                    }
                }
            }
            finally { CpuTopology.RestoreTopologyForTest(topology); }
        }

        private sealed class UiConfigFixture : IDisposable
        {
            internal readonly FamilyPolicyFixture Family;
            internal readonly PanelForm Form;
            internal readonly Toggle Toggle;
            internal readonly SettingCard Card;
            internal readonly DBPanel ProfilePanel = new DBPanel();
            private readonly Dictionary<FieldInfo, object> originals = new Dictionary<FieldInfo, object>();
            private readonly Func<bool> previousIntelAvailability;

            internal UiConfigFixture(string root, string name)
            {
                SetStatic(typeof(Native), "hasBattery", 1);
                SetStatic(typeof(EnergyMeter), "probed", true);
                SetStatic(typeof(EnergyMeter), "devicePath", "mock-energy-meter");
                SetStatic(typeof(EnergyMeter), "unreadable", false);
                SetStatic(typeof(EnergyMeter), "railIndex", new[] { 0, -1, -1, -1 });
                SetStatic(typeof(NvApi), "state", -1);
                SetStatic(typeof(AdlxApi), "state", -1);
                SetStatic(typeof(GpuInventory), "cached", new GpuAdapter[0]);
                previousIntelAvailability = IntelGraphicsApi.AvailabilityForTest;
                IntelGraphicsApi.AvailabilityForTest = delegate { return true; };
                Family = new FamilyPolicyFixture(root, name);
                Form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                GC.SuppressFinalize(Form);
                Toggle = new Toggle(); Card = new SettingCard();
                UiConfigSetField(Form, "gameMode", Family.Mode);
                UiConfigSetField(Form, "elevated", true);
            }

            internal void SetStatic(Type type, string name, object value)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                originals.Add(field, field.GetValue(null));
                field.SetValue(null, value);
            }

            internal void Refresh()
            {
                UiConfigCall(Form, "RefreshPolicyPresentation");
            }

            internal void Capabilities(bool laptop, bool watts, bool elevated)
            {
                typeof(Native).GetField("hasBattery", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, laptop ? 1 : 0);
                typeof(EnergyMeter).GetField("unreadable", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, !watts);
                UiConfigSetField(Form, "elevated", elevated);
            }

            internal void BuildGlobalGraphics(DBPanel page)
            {
                UiConfigSetField(Form, "pageGraphics", page);
                UiConfigSetField(Form, "pageTabPanels", new Dictionary<Control, DBPanel[]>());
                UiConfigSetField(Form, "tabScrollPositions", new Dictionary<DBPanel, Point>());
                UiConfigSetField(Form, "stackBase", new Dictionary<Control, Dictionary<Control, int>>());
                UiConfigSetField(Form, "graphicsSync", new List<Action>());
                UiConfigCall(Form, "BuildGraphicsPage");
            }

            public void Dispose()
            {
                try
                {
                    Eq(false, ProfilePanel.IsHandleCreated);
                }
                finally
                {
                    Toggle.Dispose(); Card.Dispose(); ProfilePanel.Dispose(); Family.Dispose();
                    IntelGraphicsApi.AvailabilityForTest = previousIntelAvailability;
                    foreach (KeyValuePair<FieldInfo, object> old in originals) old.Key.SetValue(null, old.Value);
                }
            }
        }

        private static void UiConfigUnsupportedGlobalGraphicsRemainDisableable(string root)
        {
            using (var fixture = new UiConfigFixture(root, "graphics-global-unsupported"))
            using (var page = new DBPanel { Size = new Size(996, 696) })
            {
                string[] fields = { "nvMaxPerf", "nvSmoothMotion", "nvShaderCacheMax", "nvRebarOn",
                    "amdAfmf", "rsrOn" };
                string[] toggles = { "swNvMax", "swNvSmooth", "swNvShader", "swNvRebar",
                    "swAmdAfmf", "swAmdRsr" };
                foreach (string field in fields) FamilyPolicySetField(fixture.Family.Mode, field, true);
                FamilyPolicySetField(fixture.Family.Mode, "nvLowLatMode", "ultra");
                FamilyPolicySetField(fixture.Family.Mode, "nvDlssMode", "k");
                fixture.BuildGlobalGraphics(page);
                int trapped = 0;
                foreach (string name in toggles)
                {
                    var toggle = (Toggle)UiConfigGetField(fixture.Form, name);
                    Console.WriteLine("GRAPHICS_GLOBAL control=" + name + " checked=" + toggle.Checked + " enabled=" + toggle.Enabled);
                    Eq(true, toggle.Checked);
                    if (!toggle.Enabled) trapped++;
                    toggle.Checked = false;
                    Eq(false, toggle.Checked); Eq(false, toggle.Enabled);
                    toggle.Checked = true;
                    Eq(false, toggle.Checked); Eq(false, toggle.Enabled);
                    Eq(false, toggle.IsHandleCreated);
                }
                var lowLatency = (TierPicker)UiConfigGetField(fixture.Form, "nvllPicker");
                var dlss = (TierPicker)UiConfigGetField(fixture.Form, "dlssPicker");
                Eq(2, lowLatency.Index); Eq(3, dlss.Index);
                if (!lowLatency.Enabled) trapped++;
                if (!dlss.Enabled) trapped++;
                foreach (TierPicker picker in new[] { lowLatency, dlss })
                {
                    int old = picker.Index;
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(old, picker.Index);
                    picker.Index = 0; picker.IndexChanged(0);
                    Eq(0, picker.Index); Eq(false, picker.Enabled);
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(0, picker.Index); Eq(false, picker.Enabled);
                }
                foreach (string field in fields) Eq(false, (bool)FamilyPolicyGetField(fixture.Family.Mode, field));
                Eq("off", fixture.Family.Mode.NvLowLatMode); Eq("off", fixture.Family.Mode.NvDlssMode);
                Eq(false, page.IsHandleCreated);
                Eq(0, trapped);
            }
        }

private static void UiConfigCachedAmdSupportNeedsApi(string root)
        {
            using (var fixture = new UiConfigFixture(root, "graphics-amd-stale-support"))
            using (var page = new DBPanel { Size = new Size(996, 696) })
            {
                fixture.SetStatic(typeof(AdlxTweaks), "alagSup", 1);
                fixture.SetStatic(typeof(AdlxTweaks), "afmfSup", 1);
                fixture.SetStatic(typeof(AdlxTweaks), "rsrSup", 1);
                Eq(false, AdlxTweaks.Available);
                fixture.BuildGlobalGraphics(page);
                int enabled = 0;
                string[] names = { "swAmdAfmf", "swAmdRsr" };
                foreach (string name in names)
                {
                    var toggle = (Toggle)UiConfigGetField(fixture.Form, name);
                    Console.WriteLine("AMD_STALE_SUPPORT control=" + name + " enabled=" + toggle.Enabled);
                    if (toggle.Enabled) enabled++;
                    if (name != "swAmdRsr") toggle.Checked = true;
                }
                Console.WriteLine("AMD_STALE_SUPPORT alag_on=" + fixture.Family.Mode.AmdAntiLag
                    + " afmf_on=" + fixture.Family.Mode.AmdAfmf);
                Eq(0, enabled);
                foreach (string name in names)
                {
                    var toggle = (Toggle)UiConfigGetField(fixture.Form, name);
                    toggle.Checked = true;
                    Eq(false, toggle.Checked); Eq(false, toggle.Enabled);
                }
                Eq(false, fixture.Family.Mode.AmdAntiLag); Eq(false, fixture.Family.Mode.AmdAfmf);
                Eq(false, fixture.Family.Mode.RsrUpscale); Eq(false, page.IsHandleCreated);
            }
        }

        private static void UiConfigGraphicsBindingContracts(string root)
        {
            using (var fixture = new UiConfigFixture(root, "graphics-bindings"))
            using (var toggle = new Toggle())
            {
                UiConfigSetField(fixture.Form, "graphicsSync", new List<Action>());
                bool value = false, supported = true, accept = true, loseSupport = false;
                int writes = 0, prompts = 0;
                UiConfigCall(fixture.Form, "BindGraphicsToggle", toggle,
                    new Func<bool>(delegate { return value; }),
                    new Action<bool>(delegate(bool next) { writes++; value = next; }),
                    new Func<bool>(delegate { return supported; }),
                    new Func<bool>(delegate { prompts++; if (loseSupport) supported = false; return accept; }));
                Eq(true, toggle.Enabled);
                toggle.Checked = true; Eq(true, value); Eq(1, writes); Eq(1, prompts);
                toggle.Checked = false; Eq(false, value); Eq(2, writes); Eq(1, prompts);
                accept = false;
                toggle.Checked = true; Eq(false, value); Eq(false, toggle.Checked); Eq(2, writes); Eq(2, prompts);
                accept = true; loseSupport = true;
                toggle.Checked = true; Eq(false, value); Eq(false, toggle.Enabled); Eq(2, writes); Eq(3, prompts);
                value = true;
                UiConfigCall(fixture.Form, "SyncGraphicsToggles");
                Eq(true, toggle.Checked); Eq(true, toggle.Enabled);
                toggle.Checked = false; Eq(false, value); Eq(false, toggle.Enabled); Eq(3, writes);
                toggle.Checked = true; Eq(false, value); Eq(false, toggle.Checked); Eq(3, prompts); Eq(3, writes);
                supported = true; loseSupport = false;
                UiConfigCall(fixture.Form, "SyncGraphicsToggles");
                toggle.Checked = true; Eq(true, value); Eq(4, writes);

                foreach (int count in new[] { 3, 4 })
                using (var picker = new TierPicker())
                {
                    picker.Labels = count == 3 ? new[] { "Off", "On", "Ultra" } : new[] { "Off", "Latest", "J", "K" };
                    int current = 0, changes = 0;
                    bool capability = true;
                    UiConfigCall(fixture.Form, "BindGraphicsPicker", picker,
                        new Func<int>(delegate { return current; }),
                        new Action<int>(delegate(int next) { changes++; current = next; }),
                        new Func<bool>(delegate { return capability; }));
                    for (int i = 1; i < count; i++)
                    {
                        picker.Index = i; picker.IndexChanged(i);
                        Eq(i, current); Eq(i, picker.Index); Eq(true, picker.Enabled);
                    }
                    int before = changes;
                    capability = false;
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(count - 1, current); Eq(count - 1, picker.Index); Eq(before, changes);
                    picker.IndexChanged(-1); picker.IndexChanged(count);
                    Eq(before, changes);
                    picker.Index = 0; picker.IndexChanged(0);
                    Eq(0, current); Eq(0, picker.Index); Eq(false, picker.Enabled);
                    picker.Index = 1; picker.IndexChanged(1);
                    Eq(0, current); Eq(0, picker.Index); Eq(before + 1, changes);
                    Eq(false, picker.IsHandleCreated);
                }
                Eq(false, toggle.IsHandleCreated);
            }
        }

private static void UiConfigGraphicsPresentationDoesNotProbe(string root)
        {
            using (var fixture = new UiConfigFixture(root, "graphics-cached-presentation"))
            using (var toggle = new Toggle())
            {
                UiConfigSetField(fixture.Form, "graphicsSync", new List<Action>());
                bool value = false, cached = true, hardware = false;
                int probes = 0, writes = 0;
                UiConfigCall(fixture.Form, "BindGraphicsToggle", toggle,
                    new Func<bool>(delegate { return value; }),
                    new Action<bool>(delegate(bool next) { value = next; writes++; }),
                    new Func<bool>(delegate { probes++; cached = hardware; return cached; }), null,
                    new Func<bool>(delegate { return cached; }));
                for (int i = 0; i < 10; i++) UiConfigCall(fixture.Form, "SyncGraphicsToggles");
                Eq(0, probes); Eq(true, toggle.Enabled);
                toggle.Checked = true;
                Eq(1, probes); Eq(0, writes); Eq(false, toggle.Checked); Eq(false, toggle.Enabled);
                hardware = true;
                toggle.Checked = true;
                Eq(2, probes); Eq(1, writes); Eq(true, value);
                toggle.Checked = false;
                Eq(2, probes); Eq(2, writes); Eq(false, value); Eq(true, toggle.Enabled);
                Eq(false, toggle.IsHandleCreated);
            }
        }

        private static object UiConfigCallStatic(string name, params object[] args)
        {
            MethodInfo method = typeof(PanelForm).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Missing UI config static fixture method " + name);
            return method.Invoke(null, args);
        }

        private static void UiConfigSetField(PanelForm form, string name, object value)
        {
            FieldInfo field = typeof(PanelForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing UI config fixture field " + name);
            field.SetValue(form, value);
        }

        private static object UiConfigGetField(PanelForm form, string name)
        {
            return typeof(PanelForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }

        private static object UiConfigCall(PanelForm form, string name, params object[] args)
        {
            MethodInfo method = null;
            foreach (MethodInfo candidate in typeof(PanelForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
                if (candidate.Name == name && candidate.GetParameters().Length == args.Length)
                { method = candidate; break; }
            if (method == null) throw new InvalidOperationException("Missing UI config method " + name);
            return method.Invoke(form, args);
        }
    }
}
#endif
