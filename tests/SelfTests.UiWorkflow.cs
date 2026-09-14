#if PAVISE_SELFTEST
using System;
using System.Drawing;
using System.Text;
using System.Reflection;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunUiWorkflowRegressionTests()
        {
            Eq("admin",IrqUiState.NextAction(false,false,0,0));
            Eq("restart",IrqUiState.NextAction(true,false,2,1));
            Eq("enable",IrqUiState.NextAction(true,false,2,0));
            Eq("observe",IrqUiState.NextAction(true,true,0,0));
            Eq("review",IrqUiState.NextAction(true,true,2,0));
            Eq("unknown",IrqUiState.Placement(new IrqDevice()));
            Eq("none",IrqUiState.Placement(new IrqDevice { ConfigurationKnown = true }));
            var device = new IrqDevice { Policy = 4, Mask = 4, RebootState = IrqRebootState.AwaitingReboot };
            Eq("reboot",IrqUiState.Placement(device));
            device.RebootState = IrqRebootState.Rebooted;
            Eq("pending",IrqUiState.Placement(device));
            device.AdjustmentPlacement = "matches";
            Eq("matches",IrqUiState.Placement(device));
            device.AdjustmentPlacement = "config";
            Eq("config",IrqUiState.Placement(device));
            device.ManagedElsewhere = true;
            Eq("managed",IrqUiState.Placement(device));
            bool light = Theme.LightMode;
            PerformancePreset mode = Theme.CurrentMode;
            bool custom = Theme.HasModeColorOverride(PerformancePreset.Standard);
            Color old = Theme.ModeColor(PerformancePreset.Standard);
            int language = Lang.Cur;
            try
            {
                foreach (bool day in new[] { false,true })
                foreach (Color accent in new[] { Color.FromArgb(121,83,220),Color.FromArgb(20,136,200) })
                {
                    Theme.SetLight(day);
                    Theme.SetModeColorOverride(PerformancePreset.Standard,accent);
                    Theme.SetMode(PerformancePreset.Standard,false);
                    Eq(accent,Theme.Accent);
                    Eq(Theme.Warning,IrqUiState.ColorFor("reboot"));
                    Eq(Theme.Warning,IrqUiState.ColorFor("pending"));
                    Eq(Theme.Warning,IrqUiState.ColorFor("unknown"));
                    Eq(Theme.Green,IrqUiState.ColorFor("matches"));
                    Eq(Theme.Danger,IrqUiState.ColorFor("mismatch"));
                    Eq(Theme.Warning,LogStreamView.SeverityColor(LogEventSeverity.Warning));
                    Eq(false,Theme.Accent == IrqUiState.ColorFor("pending"));
                }
                for (int i = 0; i < 3; i++)
                {
                    Lang.Cur = i;
                    Eq(true,IrqUiState.LogicalCpus(4).Contains("2"));
                    Eq(true,IrqUiState.LogicalCpus(4).Contains("1"));
                    Eq(true,IrqUiState.LogicalCpus(12).Contains("2"));
                    Eq(true,Lang.F("irq.flow.change",IrqUiState.LogicalCpus(4),IrqUiState.LogicalCpus(12)).Contains("\n"));
                }
            }
            finally
            {
                Lang.Cur = language; Theme.SetLight(light);
                if (custom) Theme.SetModeColorOverride(PerformancePreset.Standard,old);
                else Theme.ClearModeColorOverride(PerformancePreset.Standard);
                Theme.SetMode(mode,false);
            }
            int warnings, errors;
            DiagnosticSummary.CountIssues("2026-09-14 01:02:03  PASS error-game priorities applied\n"
                + "2026-09-14 01:02:04  WARN placement unreadable\n2026-09-14 01:02:05  FAIL restore rejected",out warnings,out errors);
            Eq(1,warnings); Eq(1,errors);
            var logs = new StringBuilder();
            for (int i = 0; i < 100; i++) logs.AppendLine("FAIL sample=" + i);
            string context = DiagnosticSummary.RecentContext(logs.ToString());
            Eq(false,context.Contains("sample=0\r"));
            Eq(true,context.Contains("sample=99"));
            Eq(true,context.Length < 2000);
            Eq(true,DiagnosticSummary.RecentContext("INFO before\nWARN target\nINFO after").Contains("INFO before"));
            Eq(true,DiagnosticSummary.RecentContext("INFO before\nWARN target\nINFO after").Contains("INFO after"));
            Eq(true,DiagnosticSummary.RecentContext(new string('x',5000)).Length < 1700);
            Eq(Lang.T("workflow.diagnostic.nolog"),DiagnosticSummary.RecentContext(""));
            using (var button = new WorkflowKeyButton())
            {
                int clicks = 0; button.Click += delegate { clicks++; };
                Eq(true,button.TabStop);
                Eq(AccessibleRole.PushButton,button.AccessibleRole);
                button.DispatchKey(Keys.Enter);
                Eq(1,clicks);
                button.DispatchKey(Keys.Space);
                Eq(2,clicks);
                button.DispatchKey(Keys.Escape);
                Eq(2,clicks);
                MethodInfo key = typeof(PillButton).GetMethod("OnKeyDown",BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (Keys modified in new[] { Keys.Control | Keys.Enter,Keys.Alt | Keys.Enter,Keys.Shift | Keys.Space })
                    key.Invoke(button,new object[] { new KeyEventArgs(modified) });
                Eq(2,clicks);
                KeyEventHandler cancel = delegate(object sender,KeyEventArgs e) { e.Handled = true; };
                button.KeyDown += cancel;
                button.DispatchKey(Keys.Enter);
                Eq(2,clicks);
                button.KeyDown -= cancel;
                button.KeyDown += delegate(object sender,KeyEventArgs e) { e.SuppressKeyPress = true; button.PerformClick(); };
                button.DispatchKey(Keys.Space);
                Eq(3,clicks); // A handled key must not trigger the action twice.
                button.Enabled = false;
                key.Invoke(button,new object[] { new KeyEventArgs(Keys.Enter) });
                button.PerformClick();
                Eq(3,clicks);
            }
            Console.WriteLine("PASS workflow states, theme semantics, bounded diagnostic context and button keyboard input");
        }

        private sealed class WorkflowKeyButton : PillButton
        {
            internal WorkflowKeyButton() : base("Fixture action") { }

            internal void DispatchKey(Keys key)
            {
                Message message = Message.Create(Handle,0x0100,new IntPtr((int)key),new IntPtr(1));
                if (PreProcessControlMessage(ref message) == PreProcessControlState.MessageNeeded)
                    ProcessKeyMessage(ref message);
            }
        }
    }
}
#endif
