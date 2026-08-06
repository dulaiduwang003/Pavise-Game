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
        public const string Version = "1.6.6";
        public const string Author = "bdth";
        public const string AuthorEmail = "2074055628@qq.com";
        public const string WeChat = "Ssssssstyle";
        public const string RepoName = "dulaiduwang003/Pavise-Game";
        public const string RepoUrl = "https://github.com/" + RepoName;
        public const string ReleasesUrl = RepoUrl + "/releases";
        public static string VersionTag { get { return "v" + Version; } }
    }

    internal static class Program
    {
        private const string PendingPanelKey = "ShowPanelOnNextStart";

        [STAThread]
        private static void Main(string[] args)
        {

            if (LegacyFreezeRecovery.TryHandle(args)) return;
            if (LolWatchdog.TryHandle(args)) return;
#if PAVISE_SELFTEST
            if (SelfTests.TryHandleRuntimeMode(args)) return;
#endif

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
                try { EventWaitHandle.OpenExisting("Global\\Pavise_ShowPanel").Set(); } catch { }
                return;
            }

            bool autoStarted = false;
            if (args != null)
                foreach (string a in args)
                    if (string.Equals(a, TaskHelper.AutostartArgument, StringComparison.OrdinalIgnoreCase))
                        autoStarted = true;

            bool elevated = IsElevated();

            if (!elevated && TaskHelper.TaskExists())
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
                    try { EventWaitHandle.OpenExisting("Global\\Pavise_ShowPanel").Set(); } catch { }
                    return;
                }
            }

            var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\Pavise_ShowPanel");
            EventWaitHandle exitEvt;
            try
            {
                var exitSec = new EventWaitHandleSecurity();
                exitSec.AddAccessRule(new EventWaitHandleAccessRule(
                    WindowsIdentity.GetCurrent().User,
                    EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                    AccessControlType.Allow));
                bool exitCreated;
                exitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\Pavise_Exit",
                    out exitCreated, exitSec);
            }
            catch
            {
                exitEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\Pavise_Exit");
            }

            Paths.Init();
            Lang.Init();
            try { Theme.SetLight(Settings.Load("UiLight", false)); } catch { }
            string dir = Paths.Data;
            Logger.LogPath = Path.Combine(dir, "Pavise.log");
            Settings.Remove("EvidenceMode");
            LegacyFreezeRecovery.BeginHeal(Path.Combine(dir, LegacyFreezeRecovery.StateFileName));
            int healedSuppression = SuppressionCore.HealFromCrash(Path.Combine(dir, SuppressionCore.StateFileName));
            if (healedSuppression > 0) Logger.Log("检测到上次未还原的分级后台控制，已恢复 " + healedSuppression + " 个进程");
            PowerPlan.HealFromCrash();
            try { UpdatePause.HealFromCrash(); } catch { }
            FgBoost.HealFromCrash();
            GameDvr.HealFromCrash();
            try { Mmcss.PurgeLegacy(); } catch { }
            Notif.HealFromCrash();
            VisualFx.HealFromCrash();
            DisplayGuard.HealFromCrash();
            try { NvGlobalTweaks.HealFromCrash(); } catch { }
            try { AdlxTweaks.HealFromCrash(); } catch { }
            try { PresenceQos.HealFromCrash(); } catch { }
            try { PowerOverlay.HealFromCrash(); } catch { }
            RenderLane.HealFromCrash();
            CrashGuard.HealFromCrash();

            try { LegacyPurge.RunOnce(dir); } catch { }

            if (Settings.Load("GmIfeoBoost", false))
                try
                {
                    int preArmed = IfeoBoost.PreArmAll();
                    if (preArmed > 0)
                        Logger.Log("后备提优：已预置 " + preArmed + " 个游戏");
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
            var lolService = new LolOptimizationService();

            var startGate = new object();
            bool exiting = false;
            var bootThread = new Thread(() =>
            {
                try { SvcPause.HealFromCrash(); } catch { }
                try { DoTweak.HealFromCrash(); } catch { }
                lock (startGate)
                {
                    if (exiting) return;
                    tamer.Start();
                    gameMode.Start();
                    lolService.Start();
                }
            });
            bootThread.IsBackground = true;
            bootThread.Start();

            var procNotify = new ProcNotify();
            procNotify.CaptureStartIdentity = delegate(string name, int session)
            {
                return gameMode.NeedsWhitelistParentIdentity(session)
                    || gameMode.NeedsGameProcessIdentity(name, session)
                    || LolRuntimeProcesses.IsScanCandidateName(name);
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
                lolService.NotifyProcessChanges(batch);
            };
            procNotify.Start();
            gameMode.ProcessEventsAvailable = procNotify.IsActive;
            tamer.ProcessEventsAvailable = procNotify.IsActive;

            if (elevated)
                ThreadPool.QueueUserWorkItem(_ => TaskHelper.RefreshStartupTask());

            PerformancePreset runtimeIconMode = gameMode.ActivePreset;
            bool runtimeIconEnabled = gameMode.Enabled;
            Icon appIcon = IconArt.MakeMultiIcon(runtimeIconMode, runtimeIconEnabled);
            var panel = new PanelForm(tamer, gameMode, appIcon, elevated, lolService);
            GC.KeepAlive(panel.Handle);

            bool pendingPanel = Settings.Load(PendingPanelKey, false);
            if (pendingPanel) Settings.Save(PendingPanelKey, false);
            bool showingPanel = !autoStarted || pendingPanel;
            if (showingPanel && ContactDialog.ShouldShow())
                try { using (var contact = new ContactDialog()) contact.ShowDialog(); }
                catch { }
            if (showingPanel) panel.ShowPanel();

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
                try { panel.WaitForLolIdle(4000); } catch { }
                try { lolService.Dispose(); } catch { }
                tamer.Stop();
                gameMode.Stop();
                panel.RealExit = true;
                Application.Exit();
            };

            var trayMenu = new TrayMenu(tamer, gameMode,
                () => panel.ShowPanel(),
                doExit,
                () => panel.SyncAllToggles());

            var exitThread = new Thread(() =>
            {
                exitEvt.WaitOne();
                try { panel.BeginInvoke(doExit); } catch { }
            });
            exitThread.IsBackground = true;
            exitThread.Start();
            icon.ContextMenuStrip = trayMenu.Strip;
            icon.Visible = true;
            SystemEvents.SessionEnded += (s, e) =>
            {
                try { gameMode.Enabled = false; } catch { }
                try { PowerPlan.Restore(); } catch { }
                try { GameDvr.Restore(); } catch { }
                try { Notif.Restore(); } catch { }
                try { Mmcss.Restore(); } catch { }
                try { FgBoost.Restore(); } catch { }
                try { VisualFx.Restore(); } catch { }
                try { NvGlobalTweaks.Restore(); } catch { }
                try { AdlxTweaks.RestoreAntiLag(); } catch { }
                try { AdlxTweaks.RestoreChill(); } catch { }
                try { AdlxTweaks.RestoreEnhancedSync(); } catch { }
                try { AdlxTweaks.RestoreRis(); } catch { }
                try { PresenceQos.Restore(); } catch { }
                try { PowerOverlay.Restore(); } catch { }
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
            gameMode.GameAutoAdded += name =>
            {
                try
                {
                    panel.BeginInvoke((MethodInvoker)(() =>
                    {
                        try { icon.ShowBalloonTip(10000, App.DisplayName, Lang.F("bal.autoadd", name), ToolTipIcon.Info); } catch { }
                    }));
                }
                catch { }
            };
            gameMode.LibraryChanged += () =>
            {
                try { panel.NotifyLibraryChanged(); } catch { }
            };

            trayTip = new System.Windows.Forms.Timer();
            trayTip.Interval = 1500;
            trayTip.Tick += (s, e) =>
            {
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
                if (txt.Length > 63) txt = txt.Substring(0, 62) + "…";
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
                        Logger.Log("启动检查更新：发现新版本 " + r.Latest + "（当前 " + App.VersionTag + "）");
                        try
                        {
                            panel.BeginInvoke((MethodInvoker)(() =>
                            {
                                try { icon.ShowBalloonTip(8000, App.DisplayName, Lang.F("bal.update", r.Latest), ToolTipIcon.Info); } catch { }
                            }));
                        }
                        catch { }
                    }
                    else if (r.Ok) Logger.Log("启动检查更新：已是最新版本（" + App.VersionTag + "）");
                    else Logger.Log("启动检查更新失败：" + r.Error);
                });
            };
            updTimer.Start();

            if (!elevated)
                icon.ShowBalloonTip(8000, App.DisplayName, Lang.T("bal.noelev"), ToolTipIcon.Warning);

            icon.DoubleClick += (s, e) => panel.ShowPanel();

            Application.Run();
            GC.KeepAlive(mtx);
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

                try { EventWaitHandle.OpenExisting("Global\\Pavise_Exit").Set(); }
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
