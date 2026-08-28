#if PAVISE_UI_TEST && PAVISE_SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class IrqCoreLoadUiChecks
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int checks;
        private static void Check(bool good, string message)
        { if (!good) throw new Exception(message); checks++; }
        private static void CreateHiddenTree(Control control)
        {
            typeof(Control).GetMethod("CreateControl", Private, null, new[] { typeof(bool) }, null)
                .Invoke(control, new object[] { true });
            foreach (Control child in control.Controls) CreateHiddenTree(child);
        }

        internal static int Run(string output)
        {
            checks = 0;
            var topology = CpuTopology.CaptureTopologyForTest();
            ulong strict = CpuTopology.StrictBoostMask;
            float scaleBefore = Dpi.Scale; int langBefore = Lang.Cur; bool lightBefore = Theme.LightMode;
            try
            {
                Check(typeof(CoreMatrix).GetMethod("GlowText", Private | BindingFlags.Static) == null,
                    "Percentage text must not be repainted as offset glow copies");
                Check(typeof(IrqPinDialog).GetMethod("StartLoadProbe", Private) == null,
                    "Opening the dialog must not sample desktop utilization");
                foreach (int cpuCount in new[] { 12, 64 })
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
                foreach (int language in new[] { 0, 1 })
                foreach (bool light in new[] { false, true })
                foreach (bool missing in new[] { false, true })
                {
                    Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
                    ulong all = cpuCount == 64 ? ulong.MaxValue : (1UL << cpuCount) - 1;
                    var cores = new ulong[cpuCount / 2];
                    for (int i = 0; i < cores.Length; i++) cores[i] = 3UL << (i * 2);
                    CpuTopology.InjectTopologyForTest(all, cores, null, all, 0, 0, 0, false, false);
                    CpuTopology.StrictBoostMask = all; // Must not leak into historical labels.
                    var view = new IrqPinSession { Available = true, GameName = "台架对局 / Synthetic match",
                        StartUtcTicks = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc).Ticks,
                        DurationSeconds = 300, GameMask = missing ? 0UL : 0xFCUL, SeenMask = 3,
                        MinimumCoverage = 98, MaximumCoverage = 100,
                        Driver = new IrqDriverRecord { Driver = "fixture.sys", Dpc = 1000, DpcMaxNs = 967000, Over500Us = 1 } };
                    int[] values = { 56, 38, 81, 31, 50, 31, 50, 25, 63, 38, 0, 100 };
                    if (!missing) for (int cpu = 0; cpu < cpuCount; cpu++) view.Loads[cpu] = values[cpu % values.Length];
                    var device = new IrqDevice { InstanceId = "IRQ_LOAD_UI_FIXTURE", Name = "NVIDIA GeForce RTX 2070 with Max-Q Design",
                        Service = "fixture", DevicePriorityHigh = true, Dpc = 99999, MaxUs = 99999, SeenOnCpus = all };
                    using (var dialog = new IrqPinDialog(device, view))
                    {
                        dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000, -20000);
                        CreateHiddenTree(dialog);
                        dialog.FitToWorkingArea(new Rectangle(-20000, -20000, Math.Min(1920, Theme.S(620)), Math.Min(1080, Theme.S(700))));
                        var matrix = (CoreMatrix)typeof(IrqPinDialog).GetField("matrix", Private).GetValue(dialog);
                        var body = (Panel)typeof(IrqPinDialog).GetField("scrollBody", Private).GetValue(dialog);
                        Check(matrix.SeenMask == 3 && matrix.ObservedGameMask == view.GameMask, "Mixed-session core markers");
                        var loads = (Dictionary<int, double>)typeof(CoreMatrix).GetField("loads", Private).GetValue(matrix);
                        Check(loads.Count == (missing ? 0 : cpuCount), "Unexpected displayed load count");
                        foreach (Control c in dialog.Controls)
                            if (c is PillButton) Check(dialog.ClientRectangle.Contains(c.Bounds), "Dialog action is off-screen");
                        Check(!body.HorizontalScroll.Visible, "Unexpected horizontal scroll");
                        foreach (Control c in body.Controls)
                        {
                            Check(c.Left >= 0 && c.Right <= body.ClientSize.Width, "Body content clipped horizontally");
                            var label = c as Label;
                            if (label == null || label.AutoEllipsis || string.IsNullOrEmpty(label.Text)) continue;
                            int needed = TextRenderer.MeasureText(label.Text, label.Font,
                                new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak).Height;
                            Check(needed <= label.Height, "Label clipped: scale=" + scale + " lang=" + language + " " + label.Text);
                        }
                        if (cpuCount == 12 && scale == 1.25f && language == 0)
                        {
                            string name = "irq-" + (light ? "light" : "dark") + (missing ? "-missing" : "-session");
                            using (var bmp = new Bitmap(dialog.Width, dialog.Height))
                            { dialog.DrawToBitmap(bmp, dialog.ClientRectangle); bmp.Save(Path.Combine(output, name + ".png"), ImageFormat.Png); }
                        }
                        if (!missing) { view.Loads[0] = 1; Check(loads[0] == 56, "Matrix retained mutable source dictionary"); }
                        // Missing one sibling must not label the whole physical core low-load.
                        matrix.ObservedGameMask = 0;
                        matrix.SetLoads(new Dictionary<int, double> { {0, 0}, {2, double.NaN}, {3, double.PositiveInfinity}, {64, 10} });
                        loads = (Dictionary<int, double>)typeof(CoreMatrix).GetField("loads", Private).GetValue(matrix);
                        Check(loads.Count == 1 && loads[0] == 0, "Invalid/missing values turned into loads");
                        var bands = (IList)typeof(CoreMatrix).GetField("bands", Private).GetValue(matrix);
                        var groupList = (IList)bands[0].GetType().GetField("Groups").GetValue(bands[0]);
                        object kind = typeof(CoreMatrix).GetMethod("KindOf", Private).Invoke(matrix, new object[] { groupList[0] });
                        Check(kind.ToString() == "Perf", "Missing sibling or current preset produced a false core label");
                    }
                }
            }
            finally
            {
                CpuTopology.RestoreTopologyForTest(topology); CpuTopology.StrictBoostMask = strict;
                Dpi.Scale = scaleBefore; Lang.Cur = langBefore; Theme.SetLight(lightBefore); Theme.DropFontCache();
            }
            return checks;
        }
    }
}
#endif
