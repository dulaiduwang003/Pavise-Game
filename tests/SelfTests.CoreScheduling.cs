#if PAVISE_SELFTEST
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // Masks and hybrid detection come from GLPIE, the plan stamp from CPU Sets, two enumerations each counting physical cores
        //   When they disagree the saved core plan is judged invalid by stamp mismatch and silently falls back to unrestricted cores, the user never notices
        //   This one runs on the real local topology, not synthetic data; a failure means they really disagree on this machine
        // On hybrid CPUs the E-cores are single-thread cards, the selected tag used to be dropped by card width
        //   On a 13900K 16 of 24 cards are E-cores, selecting left only a fill color and no text at all
        //   The exclusive tag is also two characters and has always drawn on single-thread cards; if exclusive fits, selected fits
        private static void TestCoreCardHeadTag()
        {
            // Selected must show a tag regardless of thread count, whether it fits is decided separately by measured width
            Eq(CoreMatrix.HeadTag.Primary, CoreMatrix.HeadTagFor(false, true, false, false));
            Eq(CoreMatrix.HeadTag.Primary, CoreMatrix.HeadTagFor(false, true, false, true));
            // Exclusive beats selected, same on both card widths
            foreach (bool multi in new[] { false, true })
                Eq(CoreMatrix.HeadTag.Exclusive, CoreMatrix.HeadTagFor(true, true, true, multi));
            // When not selected the big-cache tag beats SMT
            Eq(CoreMatrix.HeadTag.Cache, CoreMatrix.HeadTagFor(false, false, true, true));
            // SMT only for multi-thread cards, hyper-threading is meaningless on a single-thread card
            Eq(CoreMatrix.HeadTag.Smt, CoreMatrix.HeadTagFor(false, false, false, true));
            Eq(CoreMatrix.HeadTag.None, CoreMatrix.HeadTagFor(false, false, false, false));

            // Draw only if it fits: number plus gap plus tag exactly equal to head width fits, one pixel more does not draw
            Eq(true, CoreMatrix.HeadTagFits(46, 20, 23, 3));
            Eq(false, CoreMatrix.HeadTagFits(46, 20, 24, 3));
            // Wide card has room to spare
            Eq(true, CoreMatrix.HeadTagFits(92, 20, 23, 3));
            // No tag means nothing to draw, must not return true just because it measures as fitting
            Eq(false, CoreMatrix.HeadTagFits(92, 20, 0, 3));
        }

        // When ProcessorCount and the enumeration disagree, AllMask must shrink to the narrower one
        //   Too wide puts cores that do not exist on this machine into the mask, the write is bound to fail and it pollutes the plan stamp
        private static void TestAllMaskReconcile()
        {
            bool bad;
            // Consistent: returned as is, flag not set
            Eq(0xFFFUL, CpuTopology.ReconcileAllMask(0xFFF, 0xFFF, out bad));
            Eq(false, bad);
            // When the enumeration is unreadable only ProcessorCount can be trusted, flag not set
            Eq(0xFFFUL, CpuTopology.ReconcileAllMask(0xFFF, 0, out bad));
            Eq(false, bad);
            // ProcessorCount over-reports: shrink to the enumeration's value
            Eq(0xFFFFUL, CpuTopology.ReconcileAllMask(0xFFFFFF, 0xFFFF, out bad));
            Eq(true, bad);
            // Enumeration over-reports: shrink to ProcessorCount's value, both directions shrink to the narrower
            Eq(0xFFFFUL, CpuTopology.ReconcileAllMask(0xFFFF, 0xFFFFFF, out bad));
            Eq(true, bad);
            // Fully disjoint cannot be narrowed, fall back to ProcessorCount, never return an empty mask
            Eq(0x0FUL, CpuTopology.ReconcileAllMask(0x0F, 0xF0, out bad));
            Eq(true, bad);
            Console.WriteLine("  本机 AllMask 对账 " + (CpuTopology.AllMaskReconciled ? "不一致 已收窄" : "一致"));
        }

        private static void TestTopologySourcesAgree()
        {
            Console.WriteLine("  本机拓扑 逻辑核 " + CpuTopology.CountSetBits(CpuTopology.AllMask)
                + " 物理核 GLPIE/CpuSet " + CpuTopology.TopologySourceCounts
                + " 混合架构 " + (CpuTopology.Hybrid ? "是" : "否"));
            Eq(true, CpuTopology.TopologySourcesAgree);
        }

        // Favored cores compare ratings only within the top efficiency class; a hybrid CPU without TBM favored cores must not treat the whole P-core group as favored
        private static void FavoredCoresOnlyWithinTopEfficiencyClass()
        {
            // 8P+8E with a single rating: no favored cores
            Eq(0UL, CpuTopology.FavoredMaskOf(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 }));
            // 8P+8E where P-cores 4 and 5 rate higher: favored cores are 4 and 5
            Eq(0x30UL, CpuTopology.FavoredMaskOf(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
                new byte[] { 1, 1, 1, 1, 2, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 }));
            // Non-hybrid with differing ratings: take the highest directly
            Eq(0x0CUL, CpuTopology.FavoredMaskOf(
                new[] { 0, 1, 2, 3 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 1, 1, 3, 3 }));
            Eq(0UL, CpuTopology.FavoredMaskOf(new int[0], new byte[0], new byte[0]));
            Eq(0UL, CpuTopology.FavoredMaskOf(new[] { 0, 1 }, new byte[] { 0 }, new byte[] { 1, 2 }));
            // Per-core detail: adjacent equal values merge into ranges, unordered input sorts by core number
            Eq("0-7:1/1 8-11:1/2 12-15:0/0", CpuTopology.DescribeCoreClasses(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0 }));
            Eq("0:0/1 2-3:0/1", CpuTopology.DescribeCoreClasses(
                new[] { 3, 0, 2 }, new byte[] { 0, 0, 0 }, new byte[] { 1, 1, 1 }));
            Eq("", CpuTopology.DescribeCoreClasses(new int[0], new byte[0], new byte[0]));
            Console.WriteLine("PASS FavoredCores: ranked only within the top efficiency class");
        }

        internal static void RunCoreSchedulingTests(string output)
        {
            FavoredCoresOnlyWithinTopEfficiencyClass();
            CorePlanValidation(); CorePlanSerialization(); CorePlanMergedPageDraft(output);
            CorePlanProfileReturn(output); CorePlanUnsupportedIsolation(output); CorePlanUiAndPersistence(output);
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
            // The core holding CPU 0 may be exclusive, the user decides; the only floor left is two whole physical cores outside the exclusive range
            p = SampleCorePlan(); p.IsolationMask = 3; Eq(null, ValidateSample(p));
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
            // Previously enabled empty conflicting partial or obsolete heavy ranges do not block migration
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
                Eq(false, f.Editor.SaveButton.Enabled);
                // Assignment and exclusive cores merged into one page, only one core selection matrix remains and the exclusive range derives from it
                Eq(true, f.Editor.Matrix.Visible);
                Eq(true, f.Editor.Contains(f.Editor.Matrix));
                Eq(12UL, f.Editor.Draft.GameMask);
                f.Editor.Matrix.ToggleCpu(4); Eq(28UL, f.Editor.Draft.GameMask);
                // 28 is bit2 bit3 bit4, spanning the two cores 12 and 48, aligned to whole cores gives 60, core 3 excluded
                Eq(60UL, f.Editor.Draft.IsolationMask); Eq(true, f.Editor.SaveButton.Enabled);
                Eq(CoreScheduling.IsolationSupported, f.Editor.IsolationToggle.Enabled);
                Eq(28UL, f.Editor.Matrix.Selected);
                Eq(CoreMatrix.GameColor, f.Editor.Matrix.SelectionColor);
                Eq(false, f.Editor.Matrix.SelectWholeCore);
                // The core holding CPU 0 can be exclusive too, selecting it means exactly it
                f.Editor.SelectMask(3);
                Eq(3UL, f.Editor.Draft.IsolationMask);

                f.Editor.SelectMask(240); f.Editor.PhysicalOnlyButton.PerformClick();
                Eq(80UL, f.Editor.Draft.GameMask); Eq(240UL, f.Editor.Draft.IsolationMask);
                Eq(false, f.Editor.PhysicalOnlyButton.Enabled);
                f.Editor.SelectMask(160); f.Editor.PhysicalOnlyButton.PerformClick();
                Eq(160UL, f.Editor.Draft.GameMask); // preserve selected SMT sibling no new CCD
                f.Editor.SelectMask(28);
                string before = Settings.LoadStr(CoreScheduling.Key, "");
                Settings.SuspendWritesForReset();
                try { f.Editor.SaveDraft(); Eq(before, Settings.LoadStr(CoreScheduling.Key, "")); }
                finally { Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr(CoreScheduling.Key, before); }
                Eq(true, f.Editor.SaveButton.Enabled);
                f.Editor.SaveDraft(); Eq(28UL, CoreScheduling.LoadGlobal().GameMask);
                Eq(60UL, CoreScheduling.LoadGlobal().IsolationMask); Eq(false, f.Editor.SaveButton.Enabled);
                Eq(true, Settings.LoadStr(CoreScheduling.Key, "").StartsWith("2|"));

                var entered = CoreScheduling.LoadGlobal(); entered.IsolationOn = true;
                Settings.SaveStr(CoreScheduling.Key, entered.Encode());
                var frozen = PolicyResolver.Global();
                var changed = CoreScheduling.LoadGlobal(); changed.GameMask = 3; changed.IsolationOn = false; changed.IsolationMask = 48;
                Settings.SaveStr(CoreScheduling.Key, changed.Encode());
                Eq(28UL, frozen.CoreMask); Eq(3UL, PolicyResolver.Global().CoreMask);
                Eq(true, frozen.CorePlan.IsolationOn); Eq(60UL, frozen.CorePlan.IsolationMask);
                Eq(false, PolicyResolver.Global().CorePlan.IsolationOn); Eq(48UL, PolicyResolver.Global().CorePlan.IsolationMask);
                f.Editor.SelectMask(48); Eq(false, f.Editor.SaveButton.Enabled);
                Eq(48UL, f.Editor.Draft.GameMask); f.Editor.Reload();

                var profileEditor = f.ProfileEditor(); profileEditor.FollowToggle.Checked = false;
                // Exclusive cores exist only globally, the per-game page has no toggle and its core selection does not change the exclusive range
                Eq(false, profileEditor.IsolationToggle.Enabled);
                Eq(false, profileEditor.IsolationToggle.Visible);
                profileEditor.SelectMask(12);
                Eq(48UL, profileEditor.Draft.IsolationMask);
                Eq(true, profileEditor.SaveButton.Enabled);
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

                // Legacy heavy overrides must neither block global edits nor survive a profile save
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
                    // With all selected nothing is left outside exclusive, cannot be checked and an empty range cannot be saved
                    f.Editor.SelectMask(255);
                    f.Editor.IsolationToggle.Checked = true;
                    Eq(false, f.Editor.Draft.IsolationOn); Eq(0UL, f.Editor.Draft.IsolationMask);
                    // Exclusive only holds once the selection can derive a range
                    f.Editor.SelectMask(12);
                    f.Editor.IsolationToggle.Checked = true;
                    Eq(true, f.Editor.Draft.IsolationOn); Eq(12UL, f.Editor.Draft.IsolationMask);
                    Eq(true, f.Editor.SaveButton.Enabled);
                }
                Settings.SaveStr(CoreScheduling.Key, "broken record");
                Eq(0UL, PolicyResolver.Global().CoreMask);
                Eq("0", PolicyResolver.Global().ValueOf(PolicyCatalog.KeyHeavySqueeze));
                Eq("broken record", Settings.LoadStr(CoreScheduling.Key, ""));

                CoreEditorScreenshots(f, output);
            }
            Console.WriteLine("PASS CorePlanUiAndPersistence: separate game/isolation pages, No HT, migration, frozen session, failed writes, CAS");
        }

        // Default selection is all cores, exclusive would take every core and leave none for the system, the toggle grays out
        //   This is a state users actually hit, one click must yield cores and the hint must explain it clearly
        private static void CorePlanExclusiveFromFullSelection()
        {
            ulong[] cores = new ulong[] { 3, 12, 48, 192 };
            // All selected derives no exclusive range
            Eq(0UL, CoreScheduling.ExclusiveMaskFor(255, cores));
            // Holds after yielding the two highest-numbered whole cores
            ulong trimmed = CoreScheduling.TrimForExclusive(255, cores);
            Eq(15UL, trimmed);
            Eq(15UL, CoreScheduling.ExclusiveMaskFor(trimmed, cores));
            Eq(2, CoreScheduling.SpareCoresOutside(15, cores));

            // The core holding CPU 0 may be exclusive, whether to select it is the user's call
            Eq(3UL, CoreScheduling.ExclusiveMaskFor(3, cores));
            Eq(3UL, CoreScheduling.TrimForExclusive(3, cores));
            // An already valid selection must not be touched, yielding only happens when needed
            Eq(12UL, CoreScheduling.TrimForExclusive(12, cores));
            Eq(0UL, CoreScheduling.TrimForExclusive(0, cores));

            // Hybrid topology like this machine with 24 physical cores, after selecting all yielding one is enough
            var many = new System.Collections.Generic.List<ulong>();
            ulong all = 0;
            for (int i = 0; i < 8; i++) { ulong c = 3UL << (i * 2); many.Add(c); all |= c; }
            for (int i = 0; i < 16; i++) { ulong c = 1UL << (16 + i); many.Add(c); all |= c; }
            ulong[] hybrid = many.ToArray();
            Eq(0UL, CoreScheduling.ExclusiveMaskFor(all, hybrid));
            ulong hybridTrimmed = CoreScheduling.TrimForExclusive(all, hybrid);
            Eq(true, hybridTrimmed != 0);
            Eq(true, CoreScheduling.ExclusiveMaskFor(hybridTrimmed, hybrid) != 0);
            Eq(2, CoreScheduling.SpareCoresOutside(
                CoreScheduling.ExclusiveMaskFor(hybridTrimmed, hybrid), hybrid));
            // The tail cores are yielded, the core holding CPU 0 stays exclusive, untouched unless the user deselects it
            Eq(3UL, CoreScheduling.ExclusiveMaskFor(hybridTrimmed, hybrid) & 3UL);
            Console.WriteLine("PASS CorePlanExclusiveFromFullSelection: default full selection can be trimmed into a usable exclusive range");
        }

        private static void CorePlanMergedPageDraft(string output)
        {
            using (var f = new UiConfigCoreFixture(output, "core-scheduling-merged-page"))
            {
                CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
                Settings.SaveStr(CoreScheduling.Key, SampleCorePlan().Encode());
                f.Editor.Reload();

                // As soon as the selection changes the exclusive range is derived immediately, no second selection needed
                f.Editor.SelectMask(28);
                Eq(28UL, f.Editor.Draft.GameMask); Eq(60UL, f.Editor.Draft.IsolationMask);
                Eq(28UL, f.Editor.Matrix.SchedulingGameMask);
                f.Editor.SaveButton.PerformClick();
                Eq(28UL, CoreScheduling.LoadGlobal().GameMask); Eq(60UL, CoreScheduling.LoadGlobal().IsolationMask);
                Eq(false, f.Editor.SaveButton.Enabled);

                f.Editor.SelectMask(48); f.Editor.SaveButton.PerformClick();
                Eq(48UL, CoreScheduling.LoadGlobal().GameMask); Eq(48UL, CoreScheduling.LoadGlobal().IsolationMask);

                // Only an explicit reload discards the draft
                f.Editor.SelectMask(28);
                ((PillButton)typeof(CoreSchedulingPanel).GetField("reloadButton",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(f.Editor)).PerformClick();
                Eq(48UL, f.Editor.Draft.GameMask); Eq(48UL, f.Editor.Draft.IsolationMask);
                Eq(48UL, f.Editor.Matrix.Selected);

                // An invalid selection cannot be saved and the exclusive range falls back to empty at the same time
                string stored = Settings.LoadStr(CoreScheduling.Key, "");
                f.Editor.SelectMask(1);
                Eq(3UL, f.Editor.Draft.IsolationMask);
                Eq(false, f.Editor.SaveButton.Enabled);
                f.Editor.SaveButton.PerformClick(); Eq(stored, Settings.LoadStr(CoreScheduling.Key, ""));

                // On a version conflict from an external change keep the draft until the user explicitly reloads
                f.Editor.SelectMask(28);
                var external = CoreScheduling.LoadGlobal(); external.GameMask = 192; external.IsolationMask = 192;
                Settings.SaveStr(CoreScheduling.Key, external.Encode());
                f.Editor.RefreshView();
                Eq(28UL, f.Editor.Draft.GameMask); Eq(60UL, f.Editor.Draft.IsolationMask);
                Eq(false, f.Editor.SaveButton.Enabled);
                f.Editor.SaveButton.PerformClick();
                Eq(external.Encode(), Settings.LoadStr(CoreScheduling.Key, ""));
                ((PillButton)typeof(CoreSchedulingPanel).GetField("reloadButton",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(f.Editor)).PerformClick();
                Eq(192UL, f.Editor.Draft.GameMask); Eq(192UL, f.Editor.Draft.IsolationMask);
                Eq(false, f.Editor.SaveButton.Enabled);
            }
            Console.WriteLine("PASS CorePlanMergedPageDraft: exclusive range follows the selection, shared validation, conflict preserves draft");
        }

        private static void CorePlanProfileReturn(string output)
        {
            bool? previousSupport = CoreScheduling.IsolationSupportedForTest;
            CoreScheduling.IsolationSupportedForTest = true;
            try
            {
                using (var f = new UiConfigCoreFixture(output, "core-scheduling-profile-return"))
                {
                    CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
                    Settings.SaveStr(CoreScheduling.Key, SampleCorePlan().Encode());
                    var editor = f.ProfileEditor();
                    editor.FollowToggle.Checked = false; editor.SelectMask(28); editor.SaveDraft();
                    Eq(false, editor.SaveButton.Enabled);

                    // Refresh inherited isolation and its baseline without turning an unchanged local plan dirty
                    var clean = editor.CaptureEditorState();
                    var global = CoreScheduling.LoadGlobal();
                    global.GameMask = 192; global.IsolationOn = true; global.IsolationMask = 48;
                    Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(clean);
                    Eq(28UL, editor.Draft.GameMask); Eq(false, editor.FollowToggle.Checked);
                    Eq(true, editor.Draft.IsolationOn); Eq(48UL, editor.Draft.IsolationMask);
                    Eq(48UL, editor.Matrix.SchedulingIsolationMask); Eq(false, editor.SaveButton.Enabled);
                    Eq(Lang.F("schedule.exclusive.global", Lang.T("schedule.on"), CpuTopology.DescribeMask(48)),
                        CoreEditorField<SettingCard>(editor, "exclusiveCard").Desc);
                    Eq(true, CoreEditorField<Label>(editor, "status").Text.StartsWith(Lang.T("schedule.stored.active")));

                    // A follow-global edit is also preserved while the inherited range changes
                    editor.FollowToggle.Checked = true;
                    var following = editor.CaptureEditorState();
                    global.IsolationMask = 12; Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(following);
                    Eq(true, editor.FollowToggle.Checked); Eq(28UL, editor.Draft.GameMask);
                    Eq(192UL, editor.Matrix.Selected); Eq(12UL, editor.Matrix.SchedulingIsolationMask);
                    Eq(true, editor.SaveButton.Enabled);
                    editor.FollowToggle.Checked = false;

                    // A broken global game selection does not invalidate a valid per-game selection
                    var beforeGlobalGameEdit = editor.CaptureEditorState();
                    global.GameMask = 1; global.IsolationMask = 48;
                    Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(beforeGlobalGameEdit);
                    Eq(28UL, editor.Draft.GameMask); Eq(48UL, editor.Draft.IsolationMask);
                    editor.SelectMask(48); Eq(true, editor.SaveButton.Enabled);
                    editor.SaveDraft(); Eq(48UL, PolicyResolver.For(f.Family.Current(f.Family.First.Id)).CoreMask);
                    Eq(1UL, CoreScheduling.LoadGlobal().GameMask);
                    global.GameMask = 192; global.IsolationMask = 12;
                    Settings.SaveStr(CoreScheduling.Key, global.Encode()); editor.Reload();

                    // A genuine profile edit must still conflict even when global isolation also changed
                    editor.SelectMask(28);
                    var dirty = editor.CaptureEditorState();
                    var external = CoreScheduling.LoadGlobal(); external.GameMask = 192;
                    Eq(null, f.Family.Mode.SaveCoreScheduling(external, CoreScheduling.GlobalToken(), f.Family.First.Id,
                        false, CoreScheduling.ProfileToken(f.Family.Current(f.Family.First.Id))));
                    global.IsolationMask = 48; Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(dirty);
                    Eq(28UL, editor.Draft.GameMask); Eq(12UL, editor.Draft.IsolationMask);
                    Eq(false, editor.FollowToggle.Checked); Eq(false, editor.SaveButton.Enabled);
                    Eq(true, CoreEditorField<Label>(editor, "status").Text.StartsWith(Lang.T("schedule.error.changed")));
                    editor.SaveDraft(); Eq(192UL, PolicyResolver.For(f.Family.Current(f.Family.First.Id)).CoreMask);

                    // Following global cannot bypass an unreadable global record on return
                    editor.Reload(); editor.FollowToggle.Checked = true;
                    var beforeCorruption = editor.CaptureEditorState();
                    Settings.SaveStr(CoreScheduling.Key, "broken record");
                    editor.RestoreEditorState(beforeCorruption);
                    Eq(true, editor.FollowToggle.Checked); Eq(192UL, editor.Draft.GameMask);
                    Eq(48UL, editor.Draft.IsolationMask); Eq(false, editor.SaveButton.Enabled);
                    Eq(true, CoreEditorField<Label>(editor, "status").Text.StartsWith(Lang.T("schedule.error.changed")));
                    editor.SaveDraft(); Eq("broken record", Settings.LoadStr(CoreScheduling.Key, ""));

                    // A different physical layout must not be rebased onto the current topology of this profile
                    global.Topology = "old-topology"; Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(beforeCorruption);
                    Eq(192UL, editor.Draft.GameMask); Eq(48UL, editor.Draft.IsolationMask);
                    Eq(false, editor.SaveButton.Enabled);
                    Eq(true, CoreEditorField<Label>(editor, "status").Text.StartsWith(Lang.T("schedule.error.changed")));
                }
            }
            finally { CoreScheduling.IsolationSupportedForTest = previousSupport; }
            Console.WriteLine("PASS CorePlanProfileReturn: refreshed inherited isolation, clean baseline, preserved follow/game draft, real conflicts retained");
        }

        private static void CorePlanUnsupportedIsolation(string output)
        {
            bool? previousSupport = CoreScheduling.IsolationSupportedForTest;
            CoreScheduling.IsolationSupportedForTest = false;
            try
            {
                using (var f = new UiConfigCoreFixture(output, "core-scheduling-unsupported"))
                {
                    CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
                    Settings.SaveStr(CoreScheduling.Key, SampleCorePlan().Encode());
                    f.Editor.Reload();
                    var card = CoreEditorField<SettingCard>(f.Editor, "exclusiveCard");
                    Eq(Lang.T("schedule.error.isolationunsupported"), card.Desc);
                    Eq(false, f.Editor.IsolationToggle.Enabled);

                    // Existing enabled settings can still be turned off on an unsupported system
                    var global = CoreScheduling.LoadGlobal(); global.IsolationOn = true;
                    Settings.SaveStr(CoreScheduling.Key, global.Encode()); f.Editor.Reload();
                    Eq(Lang.T("schedule.error.isolationunsupported"), card.Desc);
                    Eq(true, f.Editor.IsolationToggle.Enabled); Eq(false, f.Editor.SaveButton.Enabled);
                    f.Editor.IsolationToggle.Checked = false;
                    Eq(true, f.Editor.SaveButton.Enabled); f.Editor.SaveDraft();
                    Eq(false, CoreScheduling.LoadGlobal().IsolationOn); Eq(12UL, CoreScheduling.LoadGlobal().IsolationMask);
                    Eq(Lang.T("schedule.error.isolationunsupported"), card.Desc);

                    CoreScheduling.IsolationSupportedForTest = true; f.Editor.RefreshView();
                    Eq(Lang.T("schedule.exclusive.off"), card.Desc);
                    f.Editor.IsolationToggle.Checked = true;
                    Eq(Lang.F("schedule.exclusive.on", CpuTopology.DescribeMask(12)), card.Desc);
                }
            }
            finally { CoreScheduling.IsolationSupportedForTest = previousSupport; }
            Console.WriteLine("PASS CorePlanUnsupportedIsolation: explicit support guidance, existing enabled setting can be disabled");
        }

        private static T CoreEditorField<T>(CoreSchedulingPanel editor, string name)
        {
            return (T)typeof(CoreSchedulingPanel).GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(editor);
        }

        private static void CoreEditorScreenshots(UiConfigCoreFixture f, string output)
        {
            ulong[] cores = new ulong[16];
            for (int i = 0; i < cores.Length; i++) cores[i] = 3UL << (i * 2);
            CpuTopology.InjectTopologyForTest(uint.MaxValue, cores, new ulong[] { 65535, 0xFFFF0000 },
                0, 0, 0, 0, false, false);
            Settings.SaveStr(CoreScheduling.Key, new CoreSchedulingPlan { GameMask = 65532,
                IsolationOn = true, IsolationMask = 65532, Topology = CoreScheduling.CurrentStamp }.Encode());
            int language = Lang.Cur; bool light = Theme.LightMode; float scale = Dpi.Scale;
            try
            {
                foreach (float screenshotScale in new[] { 1f, 1.5f })
                foreach (int screenshotLanguage in new[] { 0, 1, 2 })
                foreach (bool screenshotLight in new[] { false, true })
                {
                    Dpi.Scale = screenshotScale; Theme.DropFontCache(); Lang.Cur = screenshotLanguage; Theme.SetLight(screenshotLight);
                    // Recreate after topology language and scale changes so all labels and bounds match
                    using (var editor = new CoreSchedulingPanel(900, null, delegate { return null; }, delegate { return false; }))
                    {
                        f.Panel.Controls.Add(editor);
                        editor.Matrix.ShowSelectionImmediately(editor.Draft.GameMask);
                        string suffix = "-" + screenshotLanguage + "-" + (screenshotLight ? "light" : "dark")
                            + "-" + (screenshotScale == 1f ? "100" : "150");
                        using (var bitmap = new Bitmap(editor.Width, editor.Height))
                        {
                            editor.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(Path.Combine(output, "core-scheduling-game" + suffix + ".png"));
                        }
                        // Vertical order after the merge: selection matrix on top, exclusive toggle below, save last
                        Eq(true, CoreEditorBounds(editor, editor.Matrix).Bottom
                            < CoreEditorBounds(editor, editor.IsolationToggle).Top);
                        Eq(true, CoreEditorBounds(editor, editor.IsolationToggle).Bottom
                            < CoreEditorBounds(editor, editor.SaveButton).Top);
                    }
                }
                Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
                var perGame = f.ProfileEditor(); perGame.Reload();
                using (var bitmap = new Bitmap(perGame.Width, perGame.Height))
                {
                    perGame.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(output, "core-scheduling-editor-profile.png"));
                }
            }
            finally { Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light); }
            Console.WriteLine("CORE_EDITOR_IMAGE " + Path.Combine(output, "core-scheduling-game-0-dark-100.png"));
            Console.WriteLine("CORE_ISOLATION_IMAGE " + Path.Combine(output, "core-scheduling-isolation-0-dark-100.png"));
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
