// @author bdth 2074055628@qq.com
// File purpose Renders the device interrupt IRQ core move selection dialog with the per-core load heat overlay to PNG for manual visual review

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // Build a representative real-match advised device, check the advice text, load heat, core types, and current placement marker
        private static void RunIrqPinShot(string outPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            try { Settings.UseTransientStoreForCurrentProcess(); } catch { }
            Dpi.Init();
            Lang.Init();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.SetLight(false);

            // Placement 0x3, i.e. cores 0 and 1, shows red dots meaning it's currently sitting on those two cores
            // Written cores 0xF0, i.e. cores 4 to 7, preselect the existing setting, the bottom gets a valid green-text selection
            const ulong seenMask = 0x3UL;
            const ulong selectedMask = 0xF0UL;

            var d = new IrqDevice
            {
                InstanceId = "PCI\\VEN_10DE&DEV_2504&SUBSYS_SELFTEST\\PAVISE_IRQPIN_SHOT",
                Name = "NVIDIA 网卡控制器",
                Service = "nvnet",
                Bus = "PCI",
                ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}",
                Dpc = 48000,
                MaxUs = 1200,
                TotalUs = 320000,
                Over500Us = 640,
                Over1Ms = 210,
                SeenOnCpus = seenMask,
                MessageCount = 1,
                MultiMessageRisk = false,
                CompletionFollowsIssuer = false,
                // Keep a written mask so the matrix has a lit selected state
                Policy = 4,
                Mask = selectedMask,
                RebootedSincePin = true,
                Verdict = new IrqDriverVerdict { Worth = true, VersionVerified = true },
            };

            // Inject deterministic per-core load so the dialog spans the full load range
            //   At a glance: busy cores glow red across the whole cell, idle cores stay cold and dark
            int[] pat = { 88, 44, 8, 22, 92, 70, 55, 12, 6, 34, 61, 79, 48, 84, 27, 15 };
            var synth = new System.Collections.Generic.Dictionary<int, double>();
            for (int cpu = 0; cpu < 64; cpu++) synth[cpu] = pat[cpu % pat.Length];
            var session = new IrqPinSession { Available = true, GameName = "示例对局 / Fixture",
                StartUtcTicks = DateTime.UtcNow.Ticks, DurationSeconds = 300,
                GameMask = CpuTopology.StrictBoostMask, SeenMask = seenMask,
                MinimumCoverage = 100, MaximumCoverage = 100,
                Driver = new IrqDriverRecord { Driver = "nvnet.sys", Dpc = d.Dpc,
                    DpcMaxNs = (long)(d.MaxUs * 1000), Over500Us = d.Over500Us, Over1Ms = d.Over1Ms } };
            foreach (var load in synth) session.Loads[load.Key] = load.Value;

            var dlg = new IrqPinDialog(d, session);
            try
            {
                dlg.StartPosition = FormStartPosition.Manual;
                dlg.Location = new Point(-20000, -20000);
                dlg.Show();

                // The fixed synthetic session samples no live PDH, just waits for the entrance animation to finish
                for (int i = 0; i < 120; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }

                using (var bmp = new Bitmap(dlg.ClientSize.Width, dlg.ClientSize.Height))
                {
                    dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(outPath, ImageFormat.Png);
                    Console.Write(outPath + "  " + bmp.Width + "x" + bmp.Height
                        + "  落核 0x" + seenMask.ToString("X")
                        + "  已选核 0x" + selectedMask.ToString("X") + Environment.NewLine);
                }
                dlg.Hide();
            }
            finally { try { dlg.Dispose(); } catch { } }
            Environment.ExitCode = 0;
        }
    }
}
