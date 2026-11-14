using System;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class SourceCleanupChecks
    {
        private static int checks;
        private static void Check(bool condition, string reason)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(reason);
        }

        private static void CheckAutomationLabels()
        {
            string[,] labels = {
                { "", "auto.log.empty" }, { "started", "auto.log.started" },
                { "applied", "auto.log.applied" }, { "configured", "auto.log.configured" },
                { "fso-configured", "auto.fso.configured" }, { "restored", "auto.log.restored" },
                { "failed", "auto.log.failed" }, { "restore-failed", "auto.log.restore-failed" },
                { "pending-reboot", "auto.log.pending-reboot" },
                { "restore-pending-reboot", "auto.log.restore-pending-reboot" },
                { "ready", "auto.log.ready" }, { "unverified", "env.performance.unverified" },
                { "timer-requested", "auto.log.timer-requested" },
                { "timer-locked", "auto.log.timer-locked" }, { "timer-limited", "auto.log.timer-limited" },
                { "timer-unverified", "auto.log.timer-unverified" },
                { "unsupported", "auto.log.unsupported" }, { "unchanged", "auto.log.unchanged" },
                { "stopped", "auto.log.stopped" }, { "restoring", "auto.log.restoring" }
            };
            for (int language = 0; language < 3; language++)
            {
                Lang.Cur = language;
                for (int index = 0; index < labels.GetLength(0); index++)
                    Check(GameMode.AutomationStateLabel(labels[index, 0]) == Lang.T(labels[index, 1]),
                        "Automation state changed: " + labels[index, 0]);
                Check(GameMode.AutomationStateLabel(null) == Lang.T("auto.log.empty"), "Null state lost its empty label");
                foreach (string unknown in new[] { "custom-state", "STARTED", " started", "started " })
                    Check(GameMode.AutomationStateLabel(unknown) == unknown, "Unknown or case-distinct state was rewritten");
            }
        }

        private static void CheckKeyboardNavigation()
        {
            MethodInfo inputKey = typeof(AntiCheatStrengthPicker).GetMethod("IsInputKey", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var picker = new AntiCheatStrengthPicker())
            {
                foreach (bool horizontal in new[] { false, true })
                {
                    picker.Horizontal = horizontal;
                    foreach (Keys key in new[] { Keys.Home, Keys.End, Keys.Enter, Keys.Space,
                        horizontal ? Keys.Left : Keys.Up, horizontal ? Keys.Right : Keys.Down })
                    {
                        Check((bool)inputKey.Invoke(picker, new object[] { key }), "Navigation key was lost: " + key);
                        Check((bool)inputKey.Invoke(picker, new object[] { key | Keys.Shift }), "Modified navigation key was lost: " + key);
                    }
                }
                Check(!picker.IsHandleCreated, "Keyboard checks unexpectedly created a window");
            }
        }

        private static void CheckAnimationAdmission()
        {
            for (int flags = 0; flags < 128; flags++)
            {
                bool requested = (flags & 1) != 0, handle = (flags & 2) != 0, visible = (flags & 4) != 0;
                bool formVisible = (flags & 8) != 0, activeGame = (flags & 16) != 0, foreground = (flags & 32) != 0;
                FormWindowState state = (flags & 64) != 0 ? FormWindowState.Minimized : FormWindowState.Normal;
                bool expected = requested && handle && visible && formVisible && !activeGame && foreground && state != FormWindowState.Minimized;
                Check(PaviseCore.ShouldAnimate(requested, handle, visible, formVisible, state, activeGame, foreground) == expected,
                    "Animation admission changed for flags=" + flags);
            }
        }

        [STAThread]
        private static int Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess();
                Logger.WritesEnabled = false;
                Lang.Init();
                CheckAutomationLabels();
                CheckKeyboardNavigation();
                CheckAnimationAdmission();
                Console.WriteLine("PASS source cleanup checks=" + checks + "; product_started=false registry_writes=0 visible_windows=0");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
