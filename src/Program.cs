// @author bdth 2074055628@qq.com
// File purpose Program entry, runtime assembly and tray main loop
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
        public const string Version = "2.2.2.3";
        public const string Author = "bdth";
        public const string AuthorEmail = "2074055628@qq.com";
        public const string QqGroup = "1051472054";
        public const string QqGroup2 = "1101249532";
        public const string QqGroup3 = "383761286";
        public const string QqGroup4 = "166255062";
        public const string QqGroup5 = "1109874913";
        public const string Douyin = "44601770838";
        public const string WebsiteUrl = "https://pavise.club/";
        public const string WebsiteFallbackUrl = "https://pavise-website.2074055628.workers.dev/";
        public const string VersionFeedUrl = WebsiteUrl + "version.json";
        public const string VersionFeedFallbackUrl = WebsiteFallbackUrl + "version.json";
        public const string RepoName = "dulaiduwang003/Pavise-Game";
        public const string RepoUrl = "https://github.com/" + RepoName;
        public static string ChangelogUrl { get { return WebsiteUrl + (Lang.Cur == 1 ? "en/" : "") + "changelog/#latest"; } }

        public static string VersionTag { get { return "v" + Version; } }
    }

    internal static partial class Program
    {
        private const string PendingPanelKey = "ShowPanelOnNextStart";
        internal const int TrayTipIdleMs = 1500;
        internal const int TrayTipInGameMs = 6000;

        [STAThread]
        private static void Main(string[] args)
        {
            if (CoreIsolationWorker.TryHandleArgs(args)) return;
#if PAVISE_SELFTEST
            if (SelfTests.TryHandleRuntimeMode(args)) return;
#endif

            if (GameExtension.TryHandleArgs(args)) return;

            if (args.Length > 0 && args[0] == "--uninstall")
            {
                Environment.ExitCode = UninstallMode.Run(args.Length >= 2 ? args[1] : null) ? 0 : 1;
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
                    : (args.Length >= 3 && args[2] == "handheld" ? PerformancePreset.Handheld
                    : (args.Length >= 3 && args[2] == "custom" ? PerformancePreset.Custom : PerformancePreset.Standard));
                try { using (Bitmap bitmap = IconArt.Render(256, mode, true)) bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); }
                catch { Environment.ExitCode = 1; }
                return;
            }

            try { CpuTopology.ApplyDomainPreference(Settings.Load("GmCoreDomainAlt", false)); } catch { }

            if (args.Length >= 2 && args[0] == "--screenshot")
            {
                Dpi.NoFit = true;
                Dpi.Init();
                float shotScale;
                if (args.Length >= 6 && float.TryParse(args[5], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out shotScale) && shotScale >= 1f) Dpi.Scale = shotScale;
                Paths.Init();
                // Screenshots must not leak this machine's last match, power scheme, theme color or device adjustment records, settings go in memory, data dir points to temp
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                Settings.UseTransientStoreForCurrentProcess();
                Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)PerformancePreset.Competitive).ToString());
                Settings.Save("PowerPlanOn", false);
#endif
                Lang.Init();
                if (args.Length >= 4) Lang.Cur = args[3] == "en" ? 1 : (args[3] == "ja" ? 2 : 0);
                if (args.Length >= 5 && (args[4] == "light" || args[4] == "backdrop-light")) Theme.SetLight(true);
                // Screenshots keep a clean background by default, only the backdrop-specific check reads the user's current backdrop
                if (args.Length >= 5 && (args[4] == "backdrop-light" || args[4] == "backdrop-dark"))
                    try { Backdrop.Init(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string sdir = Path.Combine(Path.GetTempPath(), "PaviseShot_" + Process.GetCurrentProcess().Id);
                Directory.CreateDirectory(sdir);
                Paths.Data = sdir;
                Logger.LogPath = Path.Combine(sdir, "Pavise.log");
                int idx = args.Length >= 3 ? int.Parse(args[2]) : 0;
                var scCore = new SuppressionCore();
                var scTamer = new Tamer(scCore);
                try
                {
                    var scMode = new GameMode(sdir, scCore);
                    if (idx == (int)PageId.Log)
                    {
                        // Only screenshot mode injects this telemetry, so the structured log view can show every visual state
                        // without touching the user's real log file
                        Logger.Log("CORE 守护服务已开启，等待游戏进程");
                        Logger.Log("GAME 已识别 NebulaStrike-Win64-Shipping.exe");
                        Logger.Log("POWER 电源计划已生效：PG 专注 5EFC");
                        Logger.Log("SUPPRESS 后台资源边界已完成部署");
                        Logger.Log("IRQ 警告：检测到设备中断峰值，正在持续观测");
                        Logger.Log("GPU 策略写入失败：驱动接口拒绝访问");
                        Logger.Log("SESSION 游戏退出，系统状态已还原");
                    }
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
                    Logger.AppendCrash(
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
            try { Backdrop.Init(); } catch { }
            try
            {
                // The upper bound follows tier enum values, not the tier count, Handheld is 4, hard-coding 3 would lose its colors after restart
                for (int m = 0; m <= 4; m++)
                {
                    if (!PresetValue.IsValid(m)) continue;
                    Color mc;
                    if (Col.TryHex(Settings.LoadStr("ModeAccent" + m, ""), out mc))
                        Theme.SetModeColorOverride((PerformancePreset)m, mc);
                }
            }
            catch { }
            string dir = Paths.Data;
            Logger.LogPath = Path.Combine(dir, "Pavise.log");
            Logger.WritesEnabled = Settings.Load(Logger.WritesEnabledKey, true);
            if (taskStaleExe)
                Logger.Log(Lang.T("log.program.1"));
            Settings.Remove("EvidenceMode");
            try { VramShield.HealFromCrash(); } catch { }
            try { MemShield.HealFromCrash(); } catch { }
            int healedSuppression = SuppressionCore.HealFromCrash(Path.Combine(dir, SuppressionCore.StateFileName));
            if (healedSuppression > 0) Logger.Log(Lang.T("log.program.2") + healedSuppression + Lang.T("log.program.3"));
            PowerPlan.HealFromCrash();
            try { PowerPlan.ClearLegacyIdleDisableOnce(); } catch { }
            try { if (PowerPlan.HasParkResidue()) PowerPlan.RestoreParkState(); } catch { }
            try { UpdatePause.HealFromCrash(); } catch { }
            try { MaintenancePause.HealFromCrash(); } catch { }
            GameDvr.HealFromCrash();
            try { Mmcss.HealFromCrash(); } catch { }
            // Processor power interface evidence reads the system event log, worst case over a second, compute it in the background first
            //   Otherwise the first match reads it on the main loop thread while configuring the power scheme, and suppression and boost wait along with it
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { ProcessorPowerPlatform.Interface warmed = ProcessorPowerPlatform.Current; } catch { }
            });
            try { RssSteer.HealFromCrash(); } catch { }
            try { PresenceQos.HealFromCrash(); } catch { }
            try { PowerOverlay.HealFromCrash(); } catch { }
            try { DisplaySolo.HealFromCrash(); } catch { }
            try { GpuPowerMax.HealFromCrash(); } catch { }
            try { GpuClockLock.HealFromCrash(); } catch { }
            try { NvVrrWindowed.HealFromCrash(); } catch { }
            try { IntelEndurance.HealFromCrash(); } catch { }
            try { LaptopPerfMode.HealFromCrash(); } catch { }
            try { AdlxTweaks.HealFromCrash(); } catch { }
            try { InterruptAttribution.CleanupStaleSession(); } catch { }
            RenderLane.HealFromCrash();
            GpuPrefStage.HealFromCrash();
            try { AppGpuPreferences.HealFromCrash(); } catch { }
            try { IntelGraphicsTweaks.HealFromCrash(); } catch { }
            // The retired IFEO boost / CFG-off only leave historical residue, they were global switches so users have no per-game
            // retired fields and the library reset flow will not fire, reclaim the old writes here once, per the ledger
            try { if (IfeoBoost.HasResidue()) IfeoBoost.RestoreAll(); } catch { }
            try { if (CfgOffTweak.HasResidue()) CfgOffTweak.RestoreAll(); } catch { }
            // The Extreme tier in 2.1.3.3 bulk-set NICs to Off, the new policy no longer treats that as optimal
            // Startup only reclaims that generation's writes by the old receipts, the new UI's single-exit experiment uses V2 identity receipts and skips this migration
            try
            {
                if (NicModerationTweak.HasLegacyResidue) NicModerationTweak.MigrateLegacy();
                NicModerationTweak.ReconcileStartup();
            }
            catch { }
            if (!CoreIsolationWorker.Recover(new CoreIsolationSettingsStore()))
                Logger.Warn(Lang.T("schedule.isolation.pending"));
            CrashGuard.HealFromCrash();
            try { IrqRelocate.HealFromCrash(); } catch { }
            try { IrqAutoPilot.HealFromCrash(); } catch { }

            // Stop here if the OS build is below baseline, self-heal above has already run, historical changes are not locked onto the machine
            if (BlockIfOsTooOld(dir)) return;

            bool pendingPanel = Settings.Load(PendingPanelKey, false);
            if (pendingPanel) Settings.Save(PendingPanelKey, false);


            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    Logger.AppendCrash(Path.Combine(dir, "crash.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [UI] " + e.Exception + Environment.NewLine);
                }
                catch { }
            };

            bool showingPanel = !autoStarted || pendingPanel;
            var core = new SuppressionCore(Path.Combine(dir, SuppressionCore.StateFileName));
            var tamer = new Tamer(core);

            var gameMode = new GameMode(dir, core);
            gameMode.Enabled = Settings.Load("GameModeOn", true);

            if (gameMode.ProfileStoreSaveFailed)
            {
                int files;
                string failure;
                bool cleared = TryResetUserData(dir, delegate
                {
                    bool tamerStopped = tamer.Stop();
                    bool gameStopped = gameMode.Stop();
                    return tamerStopped && gameStopped;
                }, out files, out failure);
                PaviseDialog.Error(null, Lang.T("store.savefatal.title"),
                    cleared ? Lang.F("store.savefatal.body", dir)
                        : Lang.F("store.savefatal.deletefail", dir, failure));
                return;
            }

            var startGate = new object();
            bool exiting = false;
            var bootThread = new Thread(() =>
            {
                try { SvcPause.HealFromCrash(); } catch { }
                try { DoTweak.HealFromCrash(); } catch { }
                try { OptionalServicePause.HealFromCrash(); } catch { }
                lock (startGate)
                {
                    if (exiting) return;
                    tamer.Start();
                    gameMode.Start();
                    GameExtension.Start();
                }
                // This step may write the task XML into Paths.Data, keep it inside the merged startup
                // lifetime so a reset cannot race an untracked callback
                if (elevated && !Volatile.Read(ref exiting))
                    try { TaskHelper.RefreshStartupTask(); } catch { }
            });
            bootThread.IsBackground = true;
            bootThread.Start();

            var procNotify = new ProcNotify();
            procNotify.CaptureStartIdentity = delegate(string name, int session)
            {
                return gameMode.NeedsWhitelistParentIdentity(session)
                    || gameMode.NeedsGameFamilyIdentity(session)
                    || gameMode.NeedsGameProcessIdentity(name, session)
                    || GameExtension.NeedsProcessIdentity(name);
            };
            procNotify.CaptureParentIdentity =
                delegate(int parentPid, string name, int session)
                {
                    return gameMode.NeedsWhitelistParentIdentity(session)
                        || gameMode.NeedsGameFamilyIdentity(session)
                        || gameMode.NeedsLauncherChildParentIdentity(
                            parentPid, name, session);
                };
            procNotify.BatchChanged += batch =>
            {
                gameMode.NotifyProcessChanges(batch);
                tamer.NotifyProcessChanges(batch);
                GameExtension.NotifyProcessChanges(batch);
            };
            SystemAudit.LibraryPaths = gameMode.LibraryExecutablePaths;
            procNotify.FastTrack = delegate(string name, int session)
            {
                return gameMode.NeedsGameProcessIdentity(name, session);
            };
            procNotify.FastStart += gameMode.KickGameDetectionNow;
            procNotify.Start();
            gameMode.ProcessEventsAvailable = procNotify.IsActive;
            tamer.ProcessEventsAvailable = procNotify.IsActive;

            // Must come after GameMode is constructed, the ledger only Binds to the data dir then, calling earlier reads an empty ledger and misprompts
            ThreadPool.QueueUserWorkItem(_ => IrqRelocate.NotifyPendingVerification());

            PerformancePreset runtimeIconMode = gameMode.ActivePreset;
            bool runtimeIconEnabled = gameMode.Enabled;
            Icon appIcon = IconArt.MakeMultiIcon(runtimeIconMode, runtimeIconEnabled);
            var panel = new PanelForm(tamer, gameMode, appIcon, elevated);
            GC.KeepAlive(panel.Handle);

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
            System.Windows.Forms.Timer updTimer = null;
            int runtimeStopStarted = 0;
            int runtimeStopped = 0;
            Func<bool> stopRuntime = () =>
            {
                // A second or re-entrant exit must not mistake an in-progress stop for a completed one
                // and then delete the restore state still live underneath it
                if (Interlocked.CompareExchange(ref runtimeStopStarted, 1, 0) != 0)
                    return Volatile.Read(ref runtimeStopped) != 0;
                lock (startGate)
                {
                    exiting = true;
                }
                panel.RealExit = true;
                try { panel.Enabled = false; panel.Hide(); } catch { }
                try { trayTip.Stop(); trayTip.Dispose(); } catch { }
                try { updTimer.Stop(); updTimer.Dispose(); } catch { }
                try { icon.Visible = false; icon.Dispose(); } catch { }
                bool stopped = true;
                try { procNotify.Stop(); } catch { stopped = false; }
                try { GameExtension.Shutdown(4000); } catch { stopped = false; }
                // If the first stop failed the second is still attempted, a timeout is
                // a real failure, not a license to erase pending restore records
                try { if (!tamer.Stop()) stopped = false; } catch { stopped = false; }
                try { if (!gameMode.Stop()) stopped = false; } catch { stopped = false; }
                try
                {
                    if (bootThread == Thread.CurrentThread || !bootThread.Join(8000))
                        stopped = false;
                }
                catch { stopped = false; }
                try { panel.Dispose(); } catch { stopped = false; }
                Volatile.Write(ref runtimeStopped, stopped ? 1 : 0);
                return stopped;
            };
            Action<bool, bool> resetDataAndExit = null;
            Action doExit = () =>
            {
                // When a save failure and a normal exit coincide, the exit must not close
                // the message pump first and swallow the queued safe reset
                if (gameMode.ProfileStoreSaveFailed && resetDataAndExit != null)
                {
                    resetDataAndExit(true, true);
                    return;
                }
                stopRuntime();
                Application.Exit();
            };

            panel.ExitApp = doExit;

            int resetStarted = 0;
            // Uninstall and clear-all-config share the same stop, restore, delete, exit order, the extras are the startup task, old power scheme and old-version residue
            panel.UninstallApp = delegate
            {
                if (Interlocked.CompareExchange(ref resetStarted, 1, 0) != 0) return;
                string exePath = Application.ExecutablePath;
                UninstallReport report = UninstallMode.Execute(dir, Path.GetDirectoryName(exePath), stopRuntime);
                try
                {
                    if (report.Cleared)
                        PaviseDialog.Success(null, App.DisplayName, Lang.F("uninstall.done", report.Files, exePath));
                    else
                        PaviseDialog.Warn(null, App.DisplayName, Lang.F("uninstall.failed", report.Failure));
                }
                finally { Application.Exit(); }
            };
            resetDataAndExit = (fatal, closeApplication) =>
            {
                if (Interlocked.CompareExchange(ref resetStarted, 1, 0) != 0) return;
                int files;
                string failure;
                bool cleared = TryResetUserData(dir, stopRuntime, out files, out failure);
                try
                {
                    if (fatal)
                        PaviseDialog.Error(null, Lang.T("store.savefatal.title"),
                            cleared ? Lang.F("store.savefatal.body", dir)
                                : Lang.F("store.savefatal.deletefail", dir, failure));
                    else if (cleared)
                        PaviseDialog.Success(null, App.DisplayName, Lang.F("wipe.done", files));
                    else
                        PaviseDialog.Warn(null, App.DisplayName, Lang.F("wipe.failed", failure));
                }
                finally { if (closeApplication) Application.Exit(); }
            };
            panel.ResetApp = () => resetDataAndExit(false, true);
            Action requestFatalStoreReset = () =>
            {
                try { panel.BeginInvoke((MethodInvoker)(() => resetDataAndExit(true, true))); }
                catch
                {
                    // ApplicationExit goes through the same verified stop and restore path
                    // If called while the worker thread is still running the reset aborts safely
                    try { Application.Exit(); } catch { }
                }
            };
            Application.ApplicationExit += (s, e) =>
            {
                if (!gameMode.ProfileStoreSaveFailed) return;
                resetDataAndExit(true, false);
            };

            // Install the guarded fallback first, then subscribe and re-check for failures
            // No exit path may bypass restore and force-delete data
            gameMode.ProfileStoreSaveFailure += requestFatalStoreReset;
            if (gameMode.ProfileStoreSaveFailed) requestFatalStoreReset();

            var trayMenu = new TrayMenu(gameMode, doExit, () => panel.SyncAllToggles());

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
            bool trayShown;
            try { icon.Visible = true; trayShown = Native.NotificationAreaPresent(); }
            catch { trayShown = false; }
            if (!trayShown)
            {
                Logger.Log(Lang.T("log.program.10"));
                try { panel.ShowPanel(); } catch { }
            }
            SystemEvents.SessionEnded += (s, e) =>
            {
                try { gameMode.Enabled = false; } catch { }
                try { OptionalServicePause.Restore(); } catch { }
                try { PowerPlan.Restore(); } catch { }
                try { GameDvr.Restore(); } catch { }
                try { Mmcss.Restore(); } catch { }
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
            gameMode.RogueProcessSuspected += msg =>
            {
                try
                {
                    panel.BeginInvoke((MethodInvoker)(() =>
                    {
                        try { icon.ShowBalloonTip(15000, App.DisplayName, msg, ToolTipIcon.Warning); } catch { }
                    }));
                }
                catch { }
            };
            gameMode.LibraryChanged += () =>
            {
                try { panel.NotifyLibraryChanged(); } catch { }
            };
            gameMode.SessionBriefed += brief =>
            {
                try { panel.NotifyLastSession(brief); } catch { }
            };
            gameMode.IrqSuggested += count =>
            {
                try { panel.NotifyIrqSuggestions(count); } catch { }
            };
            gameMode.IrqObservationUpdated += () =>
            {
                try { panel.NotifyIrqObservationUpdated(); } catch { }
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

            updTimer = new System.Windows.Forms.Timer();
            updTimer.Interval = 6000;
            updTimer.Tick += (s, e) =>
            {
                updTimer.Stop();
                updTimer.Dispose();
                if (Volatile.Read(ref exiting)) return;
                UpdateChecker.CheckAsync(r =>
                {
                    if (Volatile.Read(ref exiting)) return;
                    panel.NotifyUpdate(r);
                    if (r.Ok && r.Donate != null) panel.NotifyDonate(r.Donate);
                    if (r.Ok && r.Newer)
                    {
                        Logger.Log(Lang.T("log.program.6") + r.Latest
                            + Lang.T("log.program.7") + App.VersionTag + " ");
                        try
                        {
                            panel.BeginInvoke((MethodInvoker)(() =>
                            {
                                try { icon.ShowBalloonTip(8000, App.DisplayName,
                                    Lang.F("bal.update", r.Latest), ToolTipIcon.Info); } catch { }
                            }));
                        }
                        catch { }
                    }
                    else if (r.Ok) Logger.Log(Lang.T("log.program.8") + App.VersionTag + " ");
                    else Logger.Warn(Lang.T("log.program.9") + r.Error);
                });
            };
            updTimer.Start();

            if (!elevated)
                icon.ShowBalloonTip(8000, App.DisplayName, Lang.T("bal.noelev"), ToolTipIcon.Warning);

            icon.DoubleClick += (s, e) => panel.ShowPanel();

            Application.Run();
            GC.KeepAlive(mtx);
        }
    }
}
