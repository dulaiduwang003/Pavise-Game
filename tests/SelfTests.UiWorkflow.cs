#if PAVISE_SELFTEST
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunUiWorkflowRegressionTests()
        {
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
                    Eq(Theme.Warning,LogStreamView.SeverityColor(LogEventSeverity.Warning));
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
            OverviewAttentionOnlyCountsErrors();
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
                Eq(3,clicks);
                button.Enabled = false;
                key.Invoke(button,new object[] { new KeyEventArgs(Keys.Enter) });
                button.PerformClick();
                Eq(3,clicks);
            }
            Console.WriteLine("PASS workflow states, theme semantics, bounded diagnostic context and button keyboard input");
        }

        private static void OverviewAttentionOnlyCountsErrors()
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            string previousPath = Logger.LogPath;
            bool previousWrites = Logger.WritesEnabled, previousLight = Theme.LightMode;
            int previousLanguage = Lang.Cur;
            string directory = Path.Combine(Path.GetTempPath(),"PaviseOverviewAttention-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var form = (PanelForm)FormatterServices.GetUninitializedObject(typeof(PanelForm));
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            GC.SuppressFinalize(form); GC.SuppressFinalize(mode);
            typeof(GameMode).GetField("sync",hidden).SetValue(mode,new object());
            typeof(GameMode).GetField("enabled",hidden).SetValue(mode,true);
            typeof(GameMode).GetField("profileStore",hidden).SetValue(mode,
                FormatterServices.GetUninitializedObject(typeof(GameProfileStore)));
            using (var status = new Label()) using (var runtime = new Label())
            using (var attention = new Label()) using (var action = new PillButton(""))
            {
                Action<string,object> set = delegate(string name,object value)
                    { typeof(PanelForm).GetField(name,hidden).SetValue(form,value); };
                Action refresh = delegate { typeof(PanelForm).GetMethod("RefreshOverviewAttention",hidden).Invoke(form,null); };
                set("gameMode",mode); set("elevated",true); set("lblStatus",status);
                set("lblOverviewRuntime",runtime); set("lblOverviewAttention",attention); set("btnOverviewAction",action);
                try
                {
                    Logger.ResetWriteBarrierForTest(); Logger.LogPath = Path.Combine(directory,"warnings.log");
                    Logger.Warn("graphics component unavailable");
                    Logger.Warn("placement query unavailable");
                    foreach (bool day in new[] { false,true })
                    foreach (int language in new[] { 0,1,2 })
                    {
                        Theme.SetLight(day); Lang.Cur = language;
                        status.Text = Lang.T("workflow.game.waiting");
                        set("overviewLogVersion",-1L);
                        refresh();
                        Eq(Lang.T("workflow.attention.none"),attention.Text);
                        Eq(Theme.Dim,attention.ForeColor);
                        Eq("",(string)typeof(PanelForm).GetField("overviewAction",hidden).GetValue(form));
                        Eq(false,action.Visible);
                    }
                    int warnings, errors;
                    DiagnosticSummary.CountIssues(Logger.Tail(220),out warnings,out errors);
                    Eq(2,warnings); Eq(0,errors);
                    Logger.Error("restore rejected");
                    foreach (int language in new[] { 0,1,2 })
                    {
                        Lang.Cur = language; status.Text = Lang.T("workflow.game.waiting");
                        refresh();
                        Eq(Lang.F("workflow.attention.logs",1),attention.Text);
                        Eq(Theme.Danger,attention.ForeColor);
                        Eq("logs",(string)typeof(PanelForm).GetField("overviewAction",hidden).GetValue(form));
                    }
                    Logger.Clear(); refresh();
                    Eq(Lang.T("workflow.attention.none"),attention.Text);
                    Eq(Theme.Dim,attention.ForeColor);
                    Eq(false,status.IsHandleCreated || runtime.IsHandleCreated || attention.IsHandleCreated);
                }
                finally
                {
                    Logger.ResetWriteBarrierForTest(); Logger.LogPath = previousPath; Logger.WritesEnabled = previousWrites;
                    Lang.Cur = previousLanguage; Theme.SetLight(previousLight);
                    Directory.Delete(directory,true);
                }
            }
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
