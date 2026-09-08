#if PAVISE_SELFTEST
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunScalingRegressionTests()
        {
            const string a = "scale-profile-A", b = "scale-profile-B";
            Eq(false, ScalingSettings.Enabled(a));
            Eq(true, ScalingSettings.Sharpen(a));
            Eq(false, ScalingSettings.MappedMouse(a));
            Eq(true, ScalingSettings.Set(a, "Enabled", true));
            Eq(true, ScalingSettings.Enabled(a));
            Eq(false, ScalingSettings.Enabled(b));
            Eq(true, ScalingSettings.Set(b, "MappedMouse", true));
            Eq(false, ScalingSettings.MappedMouse(a));
            Eq(true, ScalingSettings.MappedMouse(b));
            Eq(false, ScalingSettings.Set(a, "ExecutablePath", true));
            Eq(ScalingSettings.Key(a, "Enabled"), ScalingSettings.Key(a.ToUpperInvariant(), "Enabled"));
            Eq<string>(null, ScalingService.ParseState("READY"));
            Eq<string>(null, ScalingService.ParseState("READY bad 720 1920 1080"));
            Eq<string>(null, ScalingService.ParseState("READY 0 720 1920 1080"));
            Eq<string>(null, ScalingService.ParseState("READY 1280 720 99999 1080"));
            Eq<string>(null, ScalingService.ParseState(new string('x', 513)));
            Eq("starting", ScalingService.ParseState("STARTING"));
            Eq("running", ScalingService.ParseState("READY 1280 720 1920 1080"));
            Eq("paused", ScalingService.ParseState("PAUSED"));
            Eq("windowed", ScalingService.ParseState("WAITING windowed"));
            Eq("stopped", ScalingService.ParseState("STOPPED user"));
            Eq("error", ScalingService.ParseState("ERROR capture-window"));
            Eq(false, ScalingService.IsHost(42, 1));
            Eq(false, ScalingService.IsHost(0, 0));

            // Build and paint cards without showing any form or starting runtime.
            foreach (int language in new[] { 0, 1 })
            foreach (int width in new[] { 330, 520, 720 })
            {
                Lang.Cur = language;
                using (var card = new ScalingCardPanel(new GameProfile { Id = b, Name = "Scale Test" }, null))
                {
                    card.Size = new Size(Theme.S(width), card.PreferredHeight - Theme.S(9));
                    card.PerformLayout();
                    foreach (Control control in card.Controls)
                    {
                        if (control.Left < 0 || control.Top < 0 || control.Right > card.Width || control.Bottom > card.Height)
                            throw new InvalidOperationException("Scaling card control outside bounds: " + control.GetType().Name);
                    }
                    using (var image = new Bitmap(card.Width, card.Height))
                    {
                        card.DrawToBitmap(image, card.ClientRectangle);
                        string shots = Environment.GetEnvironmentVariable("PAVISE_SCALING_SHOTS");
                        if (!string.IsNullOrEmpty(shots))
                        {
                            System.IO.Directory.CreateDirectory(shots);
                            image.Save(System.IO.Path.Combine(shots, "scaling-card-" + language + "-" + width + ".png"));
                        }
                    }
                    Eq(3, card.Controls.OfType<Toggle>().Count());
                }
            }
            Settings.SuspendWritesForReset();
            Eq(false, ScalingSettings.Set(a, "Enabled", false));
            Settings.UseTransientStoreForCurrentProcess();
        }
    }
}
#endif
