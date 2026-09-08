#if PAVISE_SELFTEST
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunCoreSchedulingTests(string output)
        {
            CorePlanValidation(); CorePlanSerialization(); CorePlanUiAndPersistence(output);
        }

        private static CoreSchedulingPlan SampleCorePlan()
        {
            return new CoreSchedulingPlan { GameMask = 12, IsolationMask = 12,
                Topology = CoreScheduling.Stamp(255, new ulong[] { 3, 12, 48, 192 }) };
        }

        private static string ValidateSample(CoreSchedulingPlan plan)
        {
            return CoreScheduling.Validate(plan, 255, new ulong[] { 3, 12, 48, 192 }, false, false);
        }

        private static void CorePlanValidation()
        {
            var p = SampleCorePlan(); Eq(null, ValidateSample(p));
            p.GameMask = 68; Eq(null, ValidateSample(p)); // any pair of logical CPUs remains manual
            Eq(12UL, p.IsolationMask); Eq(68UL, p.GameMask);
            p = SampleCorePlan(); p.IsolationMask = 64; Eq("schedule.error.whole", ValidateSample(p));
            p.IsolationMask = 1024; Eq("schedule.error.whole", ValidateSample(p));
            p = SampleCorePlan(); p.IsolationMask = 3; Eq("schedule.error.cpu0", ValidateSample(p));
            p = SampleCorePlan(); p.IsolationMask = 252; Eq("schedule.error.spare", ValidateSample(p));
            p = SampleCorePlan(); p.IsolationOn = true; Eq("schedule.error.isolationunsupported", ValidateSample(p));
            Eq(null, CoreScheduling.Validate(p, 255, new ulong[] { 3, 12, 48, 192 }, false, true));
            p.IsolationMask = 0; Eq("schedule.error.isolationempty", ValidateSample(p));
            p = SampleCorePlan(); p.GameMask = 1; Eq("schedule.error.game", ValidateSample(p));
            p.GameMask = 1024; Eq("schedule.error.game", ValidateSample(p));
            p = SampleCorePlan(); p.Topology = "other"; Eq("schedule.error.topology", ValidateSample(p));
            p = SampleCorePlan(); Eq("schedule.error.groups", CoreScheduling.Validate(p, 255, new ulong[] { 3, 12, 48, 192 }, true, false));
            Console.WriteLine("PASS CorePlanValidation: whole cores, CPU0, spare cores, topology, unsupported isolation");
        }

        private static void CorePlanSerialization()
        {
            var original = SampleCorePlan(); CoreSchedulingPlan parsed;
            Eq(true, CoreSchedulingPlan.TryParse(original.Encode(), out parsed)); Eq(original.Encode(), parsed.Encode());
            foreach (string bad in new[] { "", "3|x|C|0|C", "2|x|C|0|C|1|C0", "2|x|C|true|C",
                "1|x|C|true|C|1|C0", "1|x| C|0|C|1|C0", "1|x|C|0|C|1|10000000000000000",
                "1||C|0|C|1|C0", "1|x|C|0|C|1|C0|extra", "2|x|10000000000000000|0|C" })
            {
                Eq(false, CoreSchedulingPlan.TryParse(bad, out parsed)); Eq(true, parsed.ReadFailed);
            }
            original.GameMask = 1UL << 63;
            Eq(true, CoreSchedulingPlan.TryParse(original.Encode(), out parsed)); Eq(1UL << 63, parsed.GameMask);
            // Previously enabled, empty, conflicting, partial or obsolete heavy ranges do not block migration.
            original = SampleCorePlan();
            foreach (string heavy in new[] { "0", "C", "C0", "40", "FFFFFFFFFFFFFFFF" })
            {
                string old = "1|" + original.Topology + "|C|1|C|1|" + heavy;
                Eq(true, CoreSchedulingPlan.TryParse(old, out parsed));
                Eq(12UL, parsed.GameMask); Eq(true, parsed.IsolationOn); Eq(12UL, parsed.IsolationMask);
                Eq("2|" + original.Topology + "|C|1|C", parsed.Encode());
                parsed.IsolationOn = false; Eq(null, ValidateSample(parsed));
            }
            Console.WriteLine("PASS CorePlanSerialization: V1 migration retires heavy fields; V2 strict parsing and bit 63");
        }

        private static void CorePlanUiAndPersistence(string output)
        {
            using (var f = new UiConfigCoreFixture(output, "core-scheduling"))
            {
                CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
                var legacySession = PolicyResolver.Global();
                var plan = SampleCorePlan();
                string legacy = "1|" + plan.Topology + "|C|0|C|1|C"; // old heavy/game conflict
                Settings.SaveStr(CoreScheduling.Key, legacy);
                Eq(false, legacySession.ManualPlacement); Eq(true, legacySession.StrictCores);
                Eq("0", legacySession.ValueOf(PolicyCatalog.KeyHeavySqueeze));
                f.Editor.Reload();
                Eq(false, f.Editor.IsolationExpanded); Eq(false, f.Editor.SaveButton.Enabled);
                int collapsed = f.Editor.Height;
                f.Editor.IsolationDetailsButton.PerformClick();
                Eq(true, f.Editor.IsolationExpanded); Eq(true, f.Editor.Height > collapsed);
                Eq(true, f.Editor.Matrix.Visible); Eq(true, f.Editor.IsolationMatrix.Visible);
                int gameTop = f.Editor.Matrix.Top;
                f.Editor.IsolationMatrix.ToggleCpu(4); Eq(60UL, f.Editor.Draft.IsolationMask); // whole SMT core
                Eq(12UL, f.Editor.Draft.GameMask);
                f.Editor.Matrix.ToggleCpu(4); Eq(28UL, f.Editor.Draft.GameMask);
                Eq(60UL, f.Editor.Draft.IsolationMask); Eq(true, f.Editor.SaveButton.Enabled);
                f.Editor.IsolationMatrix.ToggleCpu(0); Eq(60UL, f.Editor.Draft.IsolationMask);
                f.Editor.IsolationMatrix.ToggleCpu(4); Eq(12UL, f.Editor.Draft.IsolationMask);
                Eq(CoreScheduling.IsolationSupported, f.Editor.IsolationToggle.Enabled);
                f.Editor.SetIsolationExpanded(false); Eq(gameTop, f.Editor.Matrix.Top);
                Eq(28UL, f.Editor.Matrix.Selected); Eq(12UL, f.Editor.Draft.IsolationMask);
                Eq(CoreMatrix.GameColor, f.Editor.Matrix.SelectionColor);
                Eq(false, f.Editor.Matrix.SelectWholeCore); Eq(true, f.Editor.IsolationMatrix.SelectWholeCore);

                f.Editor.SelectMask(240); f.Editor.PhysicalOnlyButton.PerformClick();
                Eq(80UL, f.Editor.Draft.GameMask); Eq(12UL, f.Editor.Draft.IsolationMask);
                Eq(false, f.Editor.PhysicalOnlyButton.Enabled);
                f.Editor.SelectMask(160); f.Editor.PhysicalOnlyButton.PerformClick();
                Eq(160UL, f.Editor.Draft.GameMask); // preserve selected SMT sibling, no new CCD
                f.Editor.SelectMask(28);
                string before = Settings.LoadStr(CoreScheduling.Key, "");
                Settings.SuspendWritesForReset();
                try { f.Editor.SaveDraft(); Eq(before, Settings.LoadStr(CoreScheduling.Key, "")); }
                finally { Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr(CoreScheduling.Key, before); }
                Eq(true, f.Editor.SaveButton.Enabled);
                f.Editor.SaveDraft(); Eq(28UL, CoreScheduling.LoadGlobal().GameMask);
                Eq(12UL, CoreScheduling.LoadGlobal().IsolationMask); Eq(false, f.Editor.SaveButton.Enabled);
                Eq(true, Settings.LoadStr(CoreScheduling.Key, "").StartsWith("2|"));

                var entered = CoreScheduling.LoadGlobal(); entered.IsolationOn = true;
                Settings.SaveStr(CoreScheduling.Key, entered.Encode());
                var frozen = PolicyResolver.Global();
                var changed = CoreScheduling.LoadGlobal(); changed.GameMask = 3; changed.IsolationOn = false; changed.IsolationMask = 48;
                Settings.SaveStr(CoreScheduling.Key, changed.Encode());
                Eq(28UL, frozen.CoreMask); Eq(3UL, PolicyResolver.Global().CoreMask);
                Eq(true, frozen.CorePlan.IsolationOn); Eq(12UL, frozen.CorePlan.IsolationMask);
                Eq(false, PolicyResolver.Global().CorePlan.IsolationOn); Eq(48UL, PolicyResolver.Global().CorePlan.IsolationMask);
                f.Editor.SelectMask(48); Eq(false, f.Editor.SaveButton.Enabled);
                Eq(48UL, f.Editor.Draft.GameMask); f.Editor.Reload();

                var profileEditor = f.ProfileEditor(); profileEditor.FollowToggle.Checked = false;
                Eq(false, profileEditor.IsolationToggle.Enabled); Eq(false, profileEditor.IsolationDetailsButton.Visible);
                profileEditor.SetIsolationExpanded(true); Eq(false, profileEditor.IsolationExpanded);
                profileEditor.SelectIsolationMask(12); Eq(48UL, profileEditor.Draft.IsolationMask);
                profileEditor.SelectMask(12); Eq(true, profileEditor.SaveButton.Enabled);
                string token = CoreScheduling.ProfileToken(f.Family.Current(f.Family.First.Id));
                var custom = CoreScheduling.LoadGlobal(); custom.GameMask = 48;
                Eq(null, f.Family.Mode.SaveCoreScheduling(custom, CoreScheduling.GlobalToken(), f.Family.First.Id, false, token));
                var disk = new GameProfileStore(f.Family.DirectoryPath).LoadProfiles();
                Eq(CoreScheduling.ProfileToken(f.Family.Current(f.Family.First.Id)),
                    CoreScheduling.ProfileToken(FamilyPolicyFind(disk, f.Family.First.Id)));
                profileEditor.RefreshView(); Eq(false, profileEditor.SaveButton.Enabled);
                profileEditor.Reload(); profileEditor.FollowToggle.Checked = true;
                Eq(3UL, profileEditor.Matrix.Selected); Eq(false, profileEditor.PhysicalOnlyButton.Enabled);
                profileEditor.FollowToggle.Checked = false;
                Eq(48UL, profileEditor.Draft.GameMask); Eq(48UL, profileEditor.Matrix.Selected);
                profileEditor.SelectMask(240); profileEditor.PhysicalOnlyButton.PerformClick(); Eq(80UL, profileEditor.Draft.GameMask);

                // Legacy heavy overrides must neither block global edits nor survive a profile save.
                var second = FamilyPolicyFind((System.Collections.Generic.List<GameProfile>)
                    FamilyPolicyGetField(f.Family.Mode, "profiles"), f.Family.First.Id);
                second.Overrides[PolicyCatalog.KeyHeavySqueeze] = "1";
                second.Overrides[CoreScheduling.HeavyMaskKey] = "C";
                second.Overrides[CoreScheduling.Key] = legacy;
                changed = CoreScheduling.LoadGlobal(); changed.GameMask = 12;
                Eq(null, f.Family.Mode.SaveCoreScheduling(changed, CoreScheduling.GlobalToken(), null, false, null));
                Eq(12UL, CoreScheduling.LoadGlobal().GameMask);
                Eq(12UL, PolicyResolver.For(second).CoreMask);
                Eq("0", PolicyResolver.For(second).ValueOf(PolicyCatalog.KeyHeavySqueeze));
                Eq(null, f.Family.Mode.SaveCoreScheduling(changed, CoreScheduling.GlobalToken(), second.Id, false, CoreScheduling.ProfileToken(second)));
                second = f.Family.Current(second.Id);
                Eq(false, second.Overrides.ContainsKey(PolicyCatalog.KeyHeavySqueeze));
                Eq(false, second.Overrides.ContainsKey(CoreScheduling.HeavyMaskKey));
                Eq(true, second.Overrides[CoreScheduling.Key].StartsWith("2|"));
                Eq(null, f.Family.Mode.SaveCoreScheduling(changed, CoreScheduling.GlobalToken(), second.Id, true, CoreScheduling.ProfileToken(second)));

                plan.IsolationOn = true; Settings.SaveStr(CoreScheduling.Key, plan.Encode());
                f.Editor.Reload(); Eq(true, f.Editor.IsolationToggle.Enabled);
                f.Editor.IsolationToggle.Checked = false; Eq(12UL, f.Editor.Draft.IsolationMask);
                Eq(true, f.Editor.SaveButton.Enabled);
                f.Editor.SaveDraft(); Eq(false, CoreScheduling.LoadGlobal().IsolationOn);
                if (CoreScheduling.IsolationSupported)
                {
                    f.Editor.SelectIsolationMask(0); f.Editor.SetIsolationExpanded(false);
                    f.Editor.IsolationToggle.Checked = true;
                    Eq(true, f.Editor.IsolationExpanded); Eq(false, f.Editor.SaveButton.Enabled);
                    f.Editor.SelectIsolationMask(12); Eq(true, f.Editor.SaveButton.Enabled);
                }
                Settings.SaveStr(CoreScheduling.Key, "broken record");
                Eq(0UL, PolicyResolver.Global().CoreMask);
                Eq("0", PolicyResolver.Global().ValueOf(PolicyCatalog.KeyHeavySqueeze));
                Eq("broken record", Settings.LoadStr(CoreScheduling.Key, ""));

                CoreEditorScreenshots(f, output);
            }
            Console.WriteLine("PASS CorePlanUiAndPersistence: fixed game/expandable isolation, No HT, migration, frozen session, failed writes, CAS");
        }

        private static void CoreEditorScreenshots(UiConfigCoreFixture f, string output)
        {
            ulong[] cores = new ulong[16];
            for (int i = 0; i < cores.Length; i++) cores[i] = 3UL << (i * 2);
            CpuTopology.InjectTopologyForTest(uint.MaxValue, cores, new ulong[] { 65535, 0xFFFF0000 },
                0, 0, 0, 0, false, false);
            Settings.SaveStr(CoreScheduling.Key, new CoreSchedulingPlan { GameMask = 65532,
                IsolationOn = true, IsolationMask = 65532, Topology = CoreScheduling.CurrentStamp }.Encode());
            int language = Lang.Cur; Lang.Cur = 0;
            try
            {
                // Recreate after topology/language selection so all static shortcut labels match.
                using (var editor = new CoreSchedulingPanel(900, null, delegate { return null; }, delegate { return false; }))
                {
                    f.Panel.Controls.Add(editor);
                    for (int expanded = 0; expanded <= 1; expanded++)
                    {
                        editor.SetIsolationExpanded(expanded == 1);
                        editor.Matrix.ShowSelectionImmediately(editor.Draft.GameMask);
                        editor.IsolationMatrix.ShowSelectionImmediately(editor.Draft.IsolationMask);
                        using (var bitmap = new Bitmap(editor.Width, editor.Height))
                        {
                            editor.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(Path.Combine(output, expanded == 0 ? "core-scheduling-editor.png" : "core-scheduling-editor-expanded.png"));
                        }
                        Eq(true, CoreEditorBounds(editor, editor.Matrix).Bottom < CoreEditorBounds(editor, editor.IsolationToggle).Top);
                        if (expanded != 0) Eq(true, CoreEditorBounds(editor, editor.IsolationMatrix).Bottom < CoreEditorBounds(editor, editor.SaveButton).Top);
                    }
                }
                var perGame = f.ProfileEditor(); perGame.Reload();
                using (var bitmap = new Bitmap(perGame.Width, perGame.Height))
                {
                    perGame.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(output, "core-scheduling-editor-profile.png"));
                }
            }
            finally { Lang.Cur = language; }
            Console.WriteLine("CORE_EDITOR_IMAGE " + Path.Combine(output, "core-scheduling-editor.png"));
        }

        private static Rectangle CoreEditorBounds(Control root, Control child)
        {
            Point p = child.Location;
            for (Control parent = child.Parent; parent != root; parent = parent.Parent) p.Offset(parent.Location);
            return new Rectangle(p, child.Size);
        }
    }
}
#endif
