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
        private static int graphicsUiChecks;

        internal static int RunGraphicsInputUiRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseGraphicsUi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string oldLog = Logger.LogPath;
            int oldLanguage = Lang.Cur;
            float oldScale = Dpi.Scale;
            Action<string>[] cases =
            {
                GraphicsIntelEnduranceEntry,
                RetiredPreferencesRejected,
                GraphicsVendorCardLayout
            };
            graphicsUiChecks = 0;
            try
            {
                foreach (Action<string> test in cases)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest(); Logger.LogPath = Path.Combine(root, "decisions.log");
                    Lang.Cur = 0; Dpi.Scale = 1f; Theme.DropFontCache();
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                Console.WriteLine("PASS graphics-input-ui assertions=" + graphicsUiChecks
                    + " driver=mocked registry=mocked windows_shown=false snapshots=0");
                return cases.Length;
            }
            finally
            {
                Lang.Cur = oldLanguage; Dpi.Scale = oldScale; Theme.DropFontCache();
                Logger.ResetWriteBarrierForTest(); Logger.LogPath = oldLog;
                Settings.UseTransientStoreForCurrentProcess();
                string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (actual.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(actual).StartsWith("PaviseGraphicsUi-", StringComparison.Ordinal)
                    && Directory.Exists(actual)) Directory.Delete(actual, true);
            }
        }

        private static void GraphicsUiCheck(bool condition, string detail)
        {
            graphicsUiChecks++;
            if (!condition) throw new InvalidOperationException("Graphics UI regression: " + detail);
        }

        private sealed class GraphicsIntelFixture : IDisposable
        {
            internal bool Available = true;
            internal readonly FamilyPolicyFixture Family;
            private readonly Func<bool> previousAvailability;
            internal GameMode Mode { get { return Family.Mode; } }
            internal GraphicsIntelFixture(string root, string name)
            {
                previousAvailability = IntelGraphicsApi.AvailabilityForTest;
                IntelGraphicsApi.AvailabilityForTest = delegate { return Available; };
                Family = new FamilyPolicyFixture(root, name);
            }
            internal PanelForm Ui()
            {
                var form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
                GC.SuppressFinalize(form);
                ResetFlowUiSet(form, "gameMode", Mode);
                ResetFlowUiSet(form, "elevated", true);
                ResetFlowUiSet(form, "stackBase", new Dictionary<Control, Dictionary<Control, int>>());
                return form;
            }
            public void Dispose()
            {
                IntelGraphicsApi.AvailabilityForTest = previousAvailability;
                GraphicsUiCheck(FamilyPolicyGetField(Mode, "worker") == null,
                    "UI/policy-only checks started the application worker");
                Family.Dispose();
            }
        }

        private static T GraphicsUiField<T>(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing UI fixture field " + name);
            return (T)field.GetValue(instance);
        }

        private static object GraphicsUiCall(object instance, string name, params object[] args)
        {
            MethodInfo method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Missing UI fixture method " + name);
            return method.Invoke(instance, args);
        }

        private static void GraphicsIntelEnduranceEntry(string root)
        {
            foreach (bool available in new[] { false, true })
            using (var f = new GraphicsIntelFixture(root, "endurance-entry"))
            using (var panel = new Panel())
            {
                f.Available = available;
                PanelForm form = f.Ui();
                GraphicsUiCall(form, "BuildIntelGraphicsPage", panel);
                GraphicsUiCall(form, "SyncIntelGraphicsToggles");
                GraphicsUiCheck(panel.Controls.Count == 1,
                    "Intel page must contain the Endurance Gaming card");
                SettingCard card = panel.Controls[0] as SettingCard;
                Toggle endurance = GraphicsUiField<Toggle>(form, "swIntelEndurance");
                GraphicsUiCheck(card != null && card.Title == Lang.T("set.intel.endurance")
                    && card.Controls.Contains(endurance), "Intel page lost its Endurance Gaming control");
                GraphicsUiCheck(!panel.IsHandleCreated,
                    "opening Intel settings created a native control tree");
            }
        }

        private static void GraphicsVendorCardLayout(string root)
        {
            foreach (int language in new[] { 0, 1 })
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                Lang.Cur = language; Dpi.Scale = scale; Theme.DropFontCache();
                using (var f = new GraphicsIntelFixture(root, "cards"))
                using (var app = new AppGpuFixture())
                using (var common = new Panel { Size = new Size(Theme.S(956), Theme.S(530)), AutoScroll = true })
                using (var intel = new Panel { Size = common.Size, AutoScroll = true })
                {
                    PanelForm form = f.Ui();
                    GraphicsUiCall(form, "BuildCommonGraphicsPage", common);
                    GraphicsUiCall(form, "BuildIntelGraphicsPage", intel);
                    GraphicsUiCheck(common.Controls.Count == 2 && intel.Controls.Count == 1,
                        "common/Intel pages have missing or duplicate cards");
                    SettingCard autoCard = (SettingCard)common.Controls[1];
                    SettingCard enduranceCard = (SettingCard)intel.Controls[0];
                    GraphicsUiCheck(enduranceCard.Title == Lang.T("set.intel.endurance"),
                        "the Endurance Gaming card lost localization");
                    GraphicsUiCheck(autoCard.Title == Lang.T("set.autogpu") && autoCard.Desc == Lang.T("set.autogpu.n")
                        && autoCard.HasStatus, "the auto GPU preference card lost localization or its next-launch scope");
                    enduranceCard.Expanded = true;
                    autoCard.Expanded = true;
                    foreach (SettingCard card in new[] { autoCard, enduranceCard })
                    {
                        Control host = card.Controls[0];
                        int textWidth = card.Width - Theme.S(84) - host.Width - Theme.S(14)
                            - (card.Collapsible ? Theme.S(20) : 0);
                        int available = card.Height - Theme.S(11) - Theme.S(22) - Theme.S(1)
                            - (card.HasStatus ? Theme.S(SettingCard.StatusLineH) : 0) - Theme.S(9);
                        int line = Math.Max(1, TextRenderer.MeasureText("Ag", Theme.UI(8.5f, false)).Height);
                        int needed = TextRenderer.MeasureText(card.Desc, Theme.UI(8.5f, false),
                            new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
                        GraphicsUiCheck(card.ClientRectangle.Contains(host.Bounds) && textWidth > 0
                            && available / line * line >= needed,
                            "expanded vendor-card description or control did not fit: lang=" + language + " scale=" + scale);
                    }
                    GraphicsUiCheck(!common.IsHandleCreated && !intel.IsHandleCreated && app.Control.Writes == 0,
                        "card construction started a window or changed an application preference");
                }
            }
        }
    }
}
#endif
