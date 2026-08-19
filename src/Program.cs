// @author bdth 2074055628@qq.com
// 文件用途 启动程序并处理单实例 自愈和命令行入口
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class App
    {
        public const string DisplayName = "PAVISE";
        public const string Version = "1.8.1.1";
        public const string Author = "bdth";
        public const string AuthorEmail = "2074055628@qq.com";
        public const string WeChat = "Ssssssstyle";
        public const string QqGroup = "1051472054";
        public const string QqGroup2 = "1101249532";
        public const string QqGroup3 = "383761286";
        public const string Douyin = "44601770838";
        public const string PanUrl = "https://pan.quark.cn/s/3c8c986b3ea4";

        public const string RepoName = "dulaiduwang003/Pavise-Game";
        public const string RepoUrl = "https://github.com/" + RepoName;
        public const string ReleasesUrl = RepoUrl + "/releases";
        public static string VersionTag { get { return "v" + Version; } }
    }

    internal static class Program
    {
        private const string PendingPanelKey = "ShowPanelOnNextStart";
        internal const int TrayTipIdleMs = 1500;
        internal const int TrayTipInGameMs = 6000;

        [STAThread]
        private static void Main(string[] args)
        {
#if PAVISE_SELFTEST
            if (SelfTests.TryHandleRuntimeMode(args)) return;
#endif

            if (args.Length >= 4 && args[0] == "--cage-guard")
            {
                try { CpuCage.RunGuard(args[1], args[2], args[3]); } catch { }
                return;
            }

            if (args.Length > 0 && args[0] == "--genicon")
            {
                try { IcoWriter.Save(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Pavise.ico"), new[] { 16, 20, 24, 32, 48, 64, 128, 256 }); }
                catch { }
                return;
            }

            if (args.Length >= 2 && args[0] == "--geniconpng")
            {
                PerformancePreset mode = args.Length >= 3 && args[2] == "competitive" ? PerformancePreset.Competitive
                    : (args.Length >= 3 && args[2] == "custom" ? PerformancePreset.Custom : PerformancePreset.Standard);
                try { using (Bitmap bitmap = IconArt.Render(256, mode, true)) bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); }
                catch { Environment.ExitCode = 1; }
                return;
            }

            try { CpuTopology.ApplyDomainPreference(Settings.Load("GmCoreDomainAlt", false)); } catch { }

            if (args.Length >= 2 && args[0] == "--screenshot")
            {
                Dpi.Init();
                Paths.Init();
                Lang.Init();
                if (args.Length >= 4) Lang.Cur = args[3] == "en" ? 1 : (args[3] == "ja" ? 2 : 0);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string sdir = Path.Combine(Path.GetTempPath(), "PaviseShot_" + Process.GetCurrentProcess().Id);
                Directory.CreateDirectory(sdir);
                Logger.LogPath = Path.Combine(sdir, "Pavise.log");
                int idx = args.Length >= 3 ? int.Parse(args[2]) : 0;
                var scCore = new SuppressionCore();
                var scTamer = new Tamer(scCore);
                try
                {
                    var scMode = new GameMode(sdir, scCore);
                    if (idx == (int)PageId.Library)
                    {
                        string demoDir = Path.Combine(sdir, "NebulaStrike", "Binaries", "Win64");
                        Directory.CreateDirectory(demoDir);
                        string demoExe = Path.Combine(demoDir, "NebulaStrike-Win64-Shipping.exe");
                        File.Copy(Application.ExecutablePath, demoExe, true);
                        scMode.AddGameExecutable("NEBULA STRIKE", demoExe);
                        if (args.Length >= 5 && (args[4] == "gameconfig" || args[4] == "gameconfig-core"))
                            foreach (GameProfile shotProfile in scMode.GetProfiles())
                            {
                                scMode.SetProfileOverride(shotProfile.Id, PolicyCatalog.KeyPreset, "1");
                                scMode.SetProfileOverride(shotProfile.Id, PolicyCatalog.KeySuppress, "0");
                                scMode.SetProfileOverride(shotProfile.Id, PolicyCatalog.KeyNvDlss, "latest");
                                if (args[4] == "gameconfig-core")
                                    scMode.SetProfileOverride(shotProfile.Id, PolicyCatalog.KeyCoreMask, "F");
                            }
                    }
                    using (var f = new PanelForm(scTamer, scMode, IconArt.MakeIcon(Dpi.S(24)), true))
                    {
                        IntPtr hShot = f.Handle;
                        GC.KeepAlive(hShot);
                        f.RenderTo(args[1], idx,
                            args.Length >= 5 && args[4] == "anticheat",
                            args.Length >= 5 && args[4] == "mode",
                            args.Length >= 5 ? args[4] : null);
                    }
                }
                finally { try { Directory.Delete(sdir, true); } catch { } }
                return;
            }

            if (args.Length >= 2 && args[0] == "--shot-contact")
            {
                Dpi.Init(); Paths.Init(); Lang.Init();
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (var dlg = new ContactDialog())
                {
                    dlg.StartPosition = FormStartPosition.Manual;
                    dlg.ShowInTaskbar = false;
                    dlg.Location = new Point(-20000, -20000);
                    dlg.Show();
                    Application.DoEvents();
                    using (var bmp = new Bitmap(dlg.ClientSize.Width, dlg.ClientSize.Height))
                    {
                        dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return;
            }

            if (args.Length > 0 && args[0] == "--ui-preview")
            {
                Dpi.Init(); Paths.Init(); Lang.Init();
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Logger.LogPath = Path.Combine(Paths.Data, "Pavise.preview.log");
                var previewCore = new SuppressionCore();
                var previewTamer = new Tamer(previewCore);
                var previewMode = new GameMode(Paths.Data, previewCore);
                previewMode.Enabled = Settings.Load("GameModeOn", true);
                using (Icon previewIcon = IconArt.MakeMultiIcon(previewMode.ActivePreset, previewMode.Enabled))
                using (var preview = new PanelForm(previewTamer, previewMode, previewIcon, true))
                {
                    preview.RealExit = true;
                    preview.ShowPanel();
                    Application.Run(preview);
                }
                return;
            }

            Dpi.Init();
            try { Native.SetPreferredAppMode(1); } catch { }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    string cdir = Paths.Data ?? Path.GetDirectoryName(Application.ExecutablePath);
                    File.AppendAllText(
                        Path.Combine(cdir, "crash.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + e.ExceptionObject + Environment.NewLine);
                }
                catch { }
            };

            bool created = false;
            Mutex mtx = null;
            try { mtx = new Mutex(true, "Global\\Pavise_SingleInstance", out created); }
            catch { created = false; }
            if (!created && TryReplaceOlderInstance())
                try { mtx = new Mutex(true, "Global\\Pavise_SingleInstance", out created); }
                catch { created = false; }
            if (!created)
            {
                SignalShowPanel();
                return;
            }

            bool autoStarted = false;
            if (args != null)
                foreach (string a in args)
                    if (string.Equals(a, TaskHelper.AutostartArgument, StringComparison.OrdinalIgnoreCase))
                        autoStarted = true;

            bool elevated = IsElevated();

            bool taskUsable = false, taskStaleExe = false;
            if (!elevated && TaskHelper.TaskExists())
            {
                string taskExe;
                taskUsable = TaskHelper.TryReadTaskCommand(out taskExe)
                    && !TaskHelper.NeedsStartupTaskRefresh(Application.ExecutablePath, taskExe);
                taskStaleExe = !taskUsable;
            }
            if (!elevated && taskUsable)
            {
                mtx.ReleaseMutex();
                mtx.Close();
                Settings.Save(PendingPanelKey, true);
                if (TaskHelper.Run("/Run /TN " + TaskHelper.TaskName) == 0) return;
                Settings.Save(PendingPanelKey, false);
                try { mtx = new Mutex(true, "Global\\Pavise_SingleInstance", out created); }
                catch { created = false; }
                if (!created)
                {
                    SignalShowPanel();
                    return;
                }
            }

            var showEvt = CreateSignalEvent("Global\\Pavise_ShowPanel");
            var exitEvt = CreateSignalEvent("Global\\Pavise_Exit");

            Paths.Init();
            Lang.Init();
            try { Theme.SetLight(Settings.Load("UiLight", false)); } catch { }
            try
            {
                for (int m = 0; m < 3; m++)
                {
                    Color mc;
                    if (Col.TryHex(Settings.LoadStr("ModeAccent" + m, ""), out mc))
                        Theme.SetModeColorOverride((PerformancePreset)m, mc);
                }
            }
            catch { }
            string dir = Paths.Data;
            Logger.LogPath = Path.Combine(dir, "Pavise.log");
            try { VersionMigrations.ClearLogsOnUpgrade(dir); } catch { }
            if (taskStaleExe)
                Logger.Log(Lang.T("log.program.1"));
            Settings.Remove("EvidenceMode");
            int healedSuppression = SuppressionCore.HealFromCrash(Path.Combine(dir, SuppressionCore.StateFileName));
            try { CpuCage.HealFromCrash(dir); } catch { }
            if (healedSuppression > 0) Logger.Log(Lang.T("log.program.2") + healedSuppression + Lang.T("log.program.3"));
            PowerPlan.HealFromCrash();
            try { if (PowerPlan.HasParkResidue()) PowerPlan.RestoreParkState(); } catch { }
            try { FrameDiagnostics.HealFromCrash(); } catch { }
            try { FrameRemedy.HealFromCrash(); } catch { }
            try { UpdatePause.HealFromCrash(); } catch { }
            try { UploadYield.HealFromCrash(); } catch { }
            GameDvr.HealFromCrash();
            try { Mmcss.HealFromCrash(); } catch { }
            try { VersionMigrations.PurgeRetired(); } catch { }
            VisualFx.HealFromCrash();
            try { PresenceQos.HealFromCrash(); } catch { }
            try { PowerOverlay.HealFromCrash(); } catch { }
            try { GpuPowerMax.HealFromCrash(); } catch { }
            try { AdlxTweaks.HealFromCrash(); } catch { }
            try { NvDrsTweaks.HealOrphans(); } catch { }
            try { InterruptAttribution.CleanupStaleSession(); } catch { }
            RenderLane.HealFromCrash();
            CrashGuard.HealFromCrash();
            try { InterruptAffinityTweak.HealStaleMask(); } catch { }
            try { NetworkAffinityTweak.HealStaleMask(); } catch { }
            try { InterruptAffinityTweak.ResyncMask(); } catch { }

            bool pendingPanel = Settings.Load(PendingPanelKey, false);
            if (pendingPanel) Settings.Save(PendingPanelKey, false);

            try { VersionMigrations.ResetDataOnUpgrade(dir); } catch { }
            try { LegacyPurge.RunOnce(dir); } catch { }
            try { VersionMigrations.StampRunVersion(); } catch { }

            if (Settings.Load("GmIfeoBoost", false))
                try
                {
                    int preArmed = IfeoBoost.PreArmAll();
                    if (preArmed > 0)
                        Logger.Log(Lang.T("log.program.4") + preArmed + Lang.T("log.program.5"));
                }
                catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    File.AppendAllText(Path.Combine(dir, "crash.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [UI] " + e.Exception + Environment.NewLine);
                }
                catch { }
            };

            var core = new SuppressionCore(Path.Combine(dir, SuppressionCore.StateFileName));
            var tamer = new Tamer(core);
            tamer.Paused = !Settings.Load("TameOn", true);

            var gameMode = new GameMode(dir, core);
            gameMode.Enabled = Settings.Load("GameModeOn", true);

            var startGate = new object();
            bool exiting = false;
            var bootThread = new Thread(() =>
            {
                try { SvcPause.HealFromCrash(); } catch { }
                try { SvcYield.HealFromCrash(); } catch { }
                try { DoTweak.HealFromCrash(); } catch { }
                lock (startGate)
                {
                    if (exiting) return;
                    tamer.Start();
                    gameMode.Start();
                }
            });
            bootThread.IsBackground = true;
            bootThread.Start();

            var procNotify = new ProcNotify();
            procNotify.CaptureStartIdentity = delegate(string name, int session)
            {
                return gameMode.NeedsWhitelistParentIdentity(session)
                    || gameMode.NeedsGameProcessIdentity(name, session);
            };
            procNotify.CaptureParentIdentity =
                delegate(int parentPid, string name, int session)
                {
                    return gameMode.NeedsWhitelistParentIdentity(session)
                        || gameMode.NeedsLauncherChildParentIdentity(
                            parentPid, name, session);
                };
            procNotify.BatchChanged += batch =>
            {
                gameMode.NotifyProcessChanges(batch);
                tamer.NotifyProcessChanges(batch);
            };
            procNotify.Start();
            gameMode.ProcessEventsAvailable = procNotify.IsActive;
            tamer.ProcessEventsAvailable = procNotify.IsActive;

            if (elevated)
                ThreadPool.QueueUserWorkItem(_ => TaskHelper.RefreshStartupTask());

            PerformancePreset runtimeIconMode = gameMode.ActivePreset;
            bool runtimeIconEnabled = gameMode.Enabled;
            Icon appIcon = IconArt.MakeMultiIcon(runtimeIconMode, runtimeIconEnabled);
            var panel = new PanelForm(tamer, gameMode, appIcon, elevated);
            GC.KeepAlive(panel.Handle);

            bool showingPanel = !autoStarted || pendingPanel;
            if (showingPanel) panel.ShowPanel();

            if (showEvt != null)
            {
                var evtThread = new Thread(() =>
                {
                    while (true)
                    {
                        showEvt.WaitOne();
                        try { panel.ShowPanel(); } catch { }
                    }
                });
                evtThread.IsBackground = true;
                evtThread.Start();
            }

            if (elevated) Native.AllowTaskbarCreatedMessage();

            var icon = new NotifyIcon();
            icon.Icon = (Icon)appIcon.Clone();
            appIcon.Dispose();
            icon.Text = elevated ? Lang.T("tray.idle") : Lang.T("tray.noelev");

            System.Windows.Forms.Timer trayTip = null;
            Action doExit = () =>
            {

                try { trayTip.Stop(); trayTip.Dispose(); } catch { }
                icon.Visible = false;
                icon.Dispose();
                lock (startGate) exiting = true;
                try { procNotify.Stop(); } catch { }
                tamer.Stop();
                gameMode.Stop();
                panel.RealExit = true;
                Application.Exit();
            };

            panel.ExitApp = doExit;

            var trayMenu = new TrayMenu(tamer, gameMode,
                () => panel.ShowPanel(),
                doExit,
                () => panel.SyncAllToggles());

            if (exitEvt != null)
            {
                var exitThread = new Thread(() =>
                {
                    exitEvt.WaitOne();
                    try { panel.BeginInvoke(doExit); } catch { }
                });
                exitThread.IsBackground = true;
                exitThread.Start();
            }
            icon.ContextMenuStrip = trayMenu.Strip;
            icon.Visible = true;
            SystemEvents.SessionEnded += (s, e) =>
            {
                try { gameMode.Enabled = false; } catch { }
                try { PowerPlan.Restore(); } catch { }
                try { GameDvr.Restore(); } catch { }
                try { Mmcss.Restore(); } catch { }
                try { VersionMigrations.RestoreAll(); } catch { }
                try { VisualFx.Restore(); } catch { }
                try { NvGlobalTweaks.Restore(); } catch { }
                try { PresenceQos.Restore(); } catch { }
                try { PowerOverlay.Restore(); } catch { }
                try { GpuPowerMax.Restore(); } catch { }
            };
            gameMode.SessionEnded += msg =>
            {
                try
                {
                    panel.BeginInvoke((MethodInvoker)(() =>
                    {
                        try { icon.ShowBalloonTip(10000, App.DisplayName, msg, ToolTipIcon.Info); } catch { }
                    }));
                }
                catch { }
            };
            gameMode.LibraryChanged += () =>
            {
                try { panel.NotifyLibraryChanged(); } catch { }
            };

            trayTip = new System.Windows.Forms.Timer();
            trayTip.Interval = TrayTipIdleMs;
            trayTip.Tick += (s, e) =>
            {
                int wanted = gameMode.IsActive ? TrayTipInGameMs : TrayTipIdleMs;
                if (trayTip.Interval != wanted) trayTip.Interval = wanted;
                PerformancePreset nextIconMode = gameMode.ActivePreset;
                bool nextIconEnabled = gameMode.Enabled;
                if (nextIconMode != runtimeIconMode || nextIconEnabled != runtimeIconEnabled)
                {
                    runtimeIconMode = nextIconMode; runtimeIconEnabled = nextIconEnabled;
                    using (Icon next = IconArt.MakeMultiIcon(nextIconMode, nextIconEnabled))
                    {
                        Icon old = icon.Icon;
                        icon.Icon = (Icon)next.Clone();
                        panel.SetRuntimeIcon(next);
                        if (old != null) old.Dispose();
                    }
                }
                string txt;
                if (!elevated) txt = Lang.T("tray.noelev");
                else
                {
                    string g = gameMode.ActiveGame;
                    string a = g == null ? gameMode.ArmedGame : null;
                    txt = g != null ? Lang.F("tray.active", g)
                        : (a != null ? Lang.F("tray.armed", a) : Lang.T("tray.idle"));
                }
                if (txt.Length > 63) txt = txt.Substring(0, 62) + " ";
                if (icon.Text != txt) icon.Text = txt;
            };
            trayTip.Start();

            var updTimer = new System.Windows.Forms.Timer();
            updTimer.Interval = 6000;
            updTimer.Tick += (s, e) =>
            {
                updTimer.Stop();
                updTimer.Dispose();
                UpdateChecker.CheckAsync(r =>
                {
                    if (r.Ok && r.Newer)
                    {
                        Logger.Log(Lang.T("log.program.6") + r.Latest + Lang.T("log.program.7") + App.VersionTag + " ");
                        try
                        {
                            panel.BeginInvoke((MethodInvoker)(() =>
                            {
                                try { icon.ShowBalloonTip(8000, App.DisplayName, Lang.F("bal.update", r.Latest), ToolTipIcon.Info); } catch { }
                            }));
                        }
                        catch { }
                    }
                    else if (r.Ok) Logger.Log(Lang.T("log.program.8") + App.VersionTag + " ");
                    else Logger.Log(Lang.T("log.program.9") + r.Error);
                });
            };
            updTimer.Start();

            if (!elevated)
                icon.ShowBalloonTip(8000, App.DisplayName, Lang.T("bal.noelev"), ToolTipIcon.Warning);

            icon.DoubleClick += (s, e) => panel.ShowPanel();

            Application.Run();
            GC.KeepAlive(mtx);
        }

        private static void SignalShowPanel()
        {
            try
            {
                using (var show = EventWaitHandle.OpenExisting("Global\\Pavise_ShowPanel"))
                {
                    show.Set();
                    return;
                }
            }
            catch { }
            if (IsElevated()) return;
            try
            {
                if (!TaskHelper.TaskExists()) return;
                Settings.Save(PendingPanelKey, true);
                if (TaskHelper.Run("/Run /TN " + TaskHelper.TaskName) != 0)
                    Settings.Save(PendingPanelKey, false);
            }
            catch { }
        }

        private static EventWaitHandle CreateSignalEvent(string name)
        {
            try
            {
                var sec = new EventWaitHandleSecurity();
                sec.AddAccessRule(new EventWaitHandleAccessRule(
                    WindowsIdentity.GetCurrent().User,
                    EventWaitHandleRights.FullControl,
                    AccessControlType.Allow));
                bool createdNew;
                return new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew, sec);
            }
            catch { }
            try { return new EventWaitHandle(false, EventResetMode.AutoReset, name); }
            catch { return null; }
        }

        internal static int CompareVersions(string left, string right)
        {
            Version a, b;
            if (!Version.TryParse(NormalizeVersion(left), out a)) a = new Version(0, 0);
            if (!Version.TryParse(NormalizeVersion(right), out b)) b = new Version(0, 0);
            return a.CompareTo(b);
        }

        private static string NormalizeVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "0.0.0.0";
            string text = raw.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            int parts = text.Split('.').Length;
            for (int i = parts; i < 4; i++) text += ".0";
            return text;
        }

        private static bool TryReplaceOlderInstance()
        {
            Process older = null;
            try
            {
                int self = Process.GetCurrentProcess().Id;
                foreach (Process p in Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(Application.ExecutablePath)))
                {
                    if (p.Id == self) { p.Dispose(); continue; }
                    string version = null;
                    try { version = p.MainModule.FileVersionInfo.FileVersion; }
                    catch { }
                    if (version != null && CompareVersions(App.Version, version) > 0 && older == null) older = p;
                    else p.Dispose();
                }
                if (older == null) return false;

                try { using (var exit = EventWaitHandle.OpenExisting("Global\\Pavise_Exit")) exit.Set(); }
                catch { return false; }
                if (!older.WaitForExit(20000)) return false;
                return true;
            }
            catch { return false; }
            finally { if (older != null) older.Dispose(); }
        }

        private static bool IsElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

    }

}
