#if PAVISE_SELFTEST && PAVISE_LOL_DELETION_UI_CHECKS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace PaviseApp
{
    internal static class LolDeletionUiChecks
    {
        private static int checks, warnings, confirmations;
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        private static void Set(object host, string field, object value) { host.GetType().GetField(field, Hidden).SetValue(host, value); }
        private static object Get(object host, string field) { return host.GetType().GetField(field, Hidden).GetValue(host); }
        private static void Call(object host, string method, params object[] args) { host.GetType().GetMethod(method, Hidden).Invoke(host, args); }
        private static void ClearRuntime(LolOptimizationService svc)
        {
            foreach (string flag in new[] { "clientRunning", "gameRunning", "weGameMainRunning", "discovering" }) Set(svc, flag, false);
            foreach (string count in new[] { "weGameProcessCount", "uxProcessCount", "crossProcessCount" }) Set(svc, count, 0);
        }
        [STAThread] private static int Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                Dpi.Init(); Lang.Init(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                LolCardPanel.AddonDeleteWarningForTest = delegate(string message) {
                    warnings++; Check(message.Contains("WeGame"), "Exit warning must name WeGame");
                    Check(message.Contains(Lang.Cur == 0 ? "英雄联盟" : "League of Legends"), "Exit warning must name League");
                    Check(!message.Contains("browser") && !message.Contains("rail") && !message.Contains("PID"), "Exit warning exposed raw process details");
                };
                LolCardPanel.AddonDeleteConfirmationForTest = delegate { confirmations++; return false; };
                foreach (int language in new[] { 0, 1 })
                using (var svc = new LolOptimizationService())
                {
                    Lang.Cur = language; Set(svc, "enabled", true);
                    using (var panel = new LolCardPanel(svc))
                    {
                        panel.Size = new Size(Theme.S(300), panel.PreferredHeightForWidth(Theme.S(300)));
                        var button = (PillButton)Get(panel, "btnDelete");
                        Set(panel, "candidateCount", 1); Set(panel, "canDelete", true);
                        foreach (string flag in new[] { "clientRunning", "gameRunning", "weGameMainRunning", "weGameProcessCount", "uxProcessCount", "crossProcessCount" })
                        {
                            ClearRuntime(svc); Set(svc, flag, flag.EndsWith("Count") ? (object)1 : true);
                            Call(panel, "RefreshState", false);
                            Check(button.Enabled && button.Visible, "Blocker should allow delete-button warning: " + flag);
                            int before = warnings, beforeConfirm = confirmations;
                            button.PerformClick();
                            Check(warnings == before + 1 && confirmations == beforeConfirm, "Blocked click must warn without confirmation: " + flag);
                        }
                        ClearRuntime(svc); Set(panel, "canDelete", false); Set(panel, "candidateCount", 0);
                        Set(panel, "inspectBlocked", true);
                        var blockers = (List<string>)Get(panel, "blockers"); blockers.Add("browser PID 999999"); blockers.Add("rail PID 999998");
                        Call(panel, "RefreshState", false);
                        Check(button.Enabled, "Leftover blocker should allow warning even without candidates");
                        int beforeWarning = warnings; button.PerformClick(); Check(warnings == beforeWarning + 1, "Leftover click did not warn");
                        Check(!((ToolTip)Get(panel, "tips")).GetToolTip(button).Contains("browser"), "Tooltip exposed raw process name");
                        using (var canvas = new Bitmap(panel.Width, panel.Height)) panel.DrawToBitmap(canvas, panel.ClientRectangle);
                        Check(!IsWindowVisible(panel.Handle), "Test panel became visible");
                        Check(typeof(LolCardPanel).GetField("addonText", Hidden) == null, "Inline leftover status remains");
                        Set(panel, "inspectBlocked", false); blockers.Clear(); Set(panel, "candidateCount", 1); Set(panel, "canDelete", true);
                        Call(panel, "RefreshState", false);
                        int beforeConfirmation = confirmations; button.PerformClick();
                        Check(confirmations == beforeConfirmation + 1, "Unblocked click did not reach original danger confirmation");
                        foreach (string gate in new[] { "uiBusy", "inspectBusy" })
                        {
                            Set(panel, gate, 1); Call(panel, "RefreshState", false); Check(!button.Enabled, "Busy gate missing " + gate);
                            int w = warnings, c = confirmations; Call(panel, "OnDelete"); Check(warnings == w && confirmations == c, "Busy direct click bypassed gate " + gate); Set(panel, gate, 0);
                        }
                        Set(svc, "discovering", true); Call(panel, "RefreshState", false); Check(!button.Enabled, "Discovery gate missing");
                        Set(svc, "discovering", false); Set(svc, "enabled", false); Call(panel, "RefreshState", false); Check(!button.Enabled, "Disabled enhancement gate missing");
                        Check(panel.PreferredHeightForWidth(Theme.S(300)) == Theme.S(208) && button.Bottom <= panel.Height, "Compact layout leaves launch-button gap or clips deletion");
                    }
                }
                Console.WriteLine("PASS LolDeletionUiChecks assertions=" + checks + " warnings=" + warnings + " danger_confirmations=" + confirmations + " processes_touched=false deletion_called=false application_started=false windows_shown=false settings=transient");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { LolCardPanel.AddonDeleteWarningForTest = null; LolCardPanel.AddonDeleteConfirmationForTest = null; }
        }
    }
    internal static partial class SelfTests { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Cannot start application runtime"); } }
}
#endif
