// SPDX-License-Identifier: GPL-3.0-only
// 作者：bdth 2074055628@qq.com
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
        public const string DisplayName = "PAVISE AUTO";
        public const string Version = "3.1.0.5";
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
        private const string PendingPanelTicksKey = "ShowPanelRequestUtcTicks";
        internal const int TrayTipIdleMs = 1500;
        internal const int TrayTipInGameMs = 6000;

        [STAThread]
        private static void Main(string[] args)
        {
            // 临时清理副本不能初始化正常设置或游戏服务。
            if (args.Length == 2 && args[0] == "--finish-uninstall")
            {
                Environment.ExitCode = UninstallFinalizer.Run(args[1]) ? 0 : 1;
                return;
            }
            if (CpuCapGuard.TryHandleArgs(args)) return;
            if (FreezeGuard.TryHandleArgs(args)) return;
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
                PerformancePreset mode = args.Length >= 3 && args[2] == "extreme" ? PerformancePreset.Extreme
                    : args.Length >= 3 && args[2] == "competitive" ? PerformancePreset.Competitive
                    : (args.Length >= 3 && args[2] == "handheld" ? PerformancePreset.Handheld
                    : PerformancePreset.Standard);
                try { using (Bitmap bitmap = IconArt.Render(256, mode, true)) bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png); }
                catch { Environment.ExitCode = 1; }
                return;
            }

            if (args.Length >= 2 && args[0] == "--screenshot")
            {
                Dpi.NoFit = true;
                Dpi.Init();
                float shotScale;
                if (args.Length >= 6 && float.TryParse(args[5], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out shotScale) && shotScale >= 1f) Dpi.Scale = shotScale;
                Paths.Init();
                // 截图使用临时内存配置，避免泄露本机游戏与设备记录。
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                Settings.UseTransientStoreForCurrentProcess();
                Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)PerformancePreset.Competitive).ToString());
                Settings.Save("PowerPlanOn", false);
#endif
                Lang.Init();
                if (args.Length >= 4) Lang.Cur = args[3] == "en" ? 1 : (args[3] == "ja" ? 2 : 0);
                if (args.Length >= 5 && (args[4] == "light" || args[4] == "backdrop-light")) Theme.SetLight(true);
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
                        Logger.Log("CORE 守护服务已开启，等待游戏进程");
                        Logger.Log("GAME 已识别 NebulaStrike-Win64-Shipping.exe");
                        Logger.Log("POWER 电源计划已生效：PG 专注 5EFC");
                        Logger.Log("SUPPRESS 后台资源边界已完成部署");
                        Logger.Log("IRQ 警告：检测到慢驱动 rtwlane.sys，单次 DPC 最长 2.1ms");
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

            bool autoStarted = IsAutostart(args);
            bool created = false;
            Mutex mtx = null;
            try { mtx = new Mutex(true, "Global\\Pavise_SingleInstance", out created); }
            catch { created = false; }
            if (!created && TryReplaceOlderInstance())
                created = RecreateSingleInstanceMutex(ref mtx, "Global\\Pavise_SingleInstance");
            if (!created)
            {
                if (ShouldSignalExistingPanel(autoStarted)) SignalShowPanel();
                return;
            }

            bool elevated = IsElevated();
            bool resetRestart = args.Length > 0 && args[0] == "--reset-restart";
            try { Paths.Init(autoStarted || resetRestart ? Paths.ResolveStartupDataArgument(args) : null); }
            catch (Exception error) { Logger.Error("STARTUP " + error.Message); return; }

            bool taskUsable = false, taskStaleExe = false;
            if (!elevated && TaskHelper.TaskExists())
            {
                string taskExe, taskArguments;
                taskUsable = TaskHelper.TryReadTaskTarget(out taskExe, out taskArguments)
                    && TaskHelper.IsCurrentTaskTarget(Application.ExecutablePath, Paths.Data, taskExe, taskArguments);
                taskStaleExe = !taskUsable;
            }
            if (!elevated && taskUsable)
            {
                mtx.ReleaseMutex();
                mtx.Close();
                SetPendingPanelRequest(!autoStarted);
                if (TaskHelper.Run("/Run /TN " + TaskHelper.TaskName) == 0) return;
                SetPendingPanelRequest(false);
                try { mtx = new Mutex(true, "Global\\Pavise_SingleInstance", out created); }
                catch { created = false; }
                if (!created)
                {
                    if (ShouldSignalExistingPanel(autoStarted)) SignalShowPanel();
                    return;
                }
            }

            var showEvt = CreateSignalEvent("Global\\Pavise_ShowPanel");
            var exitEvt = CreateSignalEvent("Global\\Pavise_Exit");

            Lang.Init();
            try { Theme.SetLight(Settings.Load("UiLight", false)); } catch { }
            try { Backdrop.Init(); } catch { }
            try
            {
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
            // 系统事件查询预先异步执行，避免阻塞首次入局。
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
            try { FrameCoordination.HealFromCrash(); } catch { }
            GpuPrefStage.HealFromCrash();
            try { AppGpuPreferences.HealFromCrash(); } catch { }
            try { if (IfeoBoost.HasResidue()) IfeoBoost.RestoreAll(); } catch { }
            try { if (CfgOffTweak.HasResidue()) CfgOffTweak.RestoreAll(); } catch { }
            try { PerformanceTuning.RestoreSession(); } catch { }
            if (!CoreIsolationWorker.Recover(new CoreIsolationSettingsStore()))
                Logger.Warn(Lang.T("schedule.isolation.pending"));
            try { LegacyReservedCores.Heal(); } catch { }
            CrashGuard.HealFromCrash();
            try { IrqRelocate.HealFromCrash(); } catch { }
            try { IrqAutoPilot.HealFromCrash(); } catch { }

            if (BlockIfOsTooOld(dir)) return;

            bool pendingPanel = Settings.Load(PendingPanelKey, false);
            long pendingTicks;
            long.TryParse(Settings.LoadStr(PendingPanelTicksKey, ""), out pendingTicks);
            SetPendingPanelRequest(false);

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

            bool showingPanel = ShouldShowStartupPanel(autoStarted, pendingPanel, pendingTicks, DateTime.UtcNow.Ticks);
            if (!BackgroundCpuCap.Recover()) Logger.Warn(Lang.F("cpucap.failed", "recovery-pending"));
            var core = new SuppressionCore(Path.Combine(dir, SuppressionCore.StateFileName));
            var tamer = new Tamer(core);

            var gameMode = new GameMode(dir, core);
            gameMode.InitializeAutomation();
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
                    RtssOverlayClient.PreferredPid = gameMode.GetMonitoringRendererPid;
                    RtssOverlayClient.PreferredBirth = gameMode.GetMonitoringRendererBirth;
                    RtssOverlayClient.SessionActive = delegate { return gameMode.IsActive; };
                    RtssOverlayClient.SessionPreparing = delegate { return gameMode.IsMonitoringPreparing; };
                    RtssOverlayClient.ActiveMode = delegate { return gameMode.ActivePreset; };
                    PerformanceMonitoring.Start();
                    RtssComponentInfo rtss = RtssComponent.Detect();
                    if (rtss.Installed && !rtss.InstallationInProgress && !rtss.UninstallationInProgress)
                    {
                        string quietReason;
                        RtssStartup.PrepareBackgroundConfiguration(rtss.Path, out quietReason);
                    }
                    RtssTrayIcon.Hide(rtss);
                }
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
            var uiDispatcher = new RuntimeUiDispatcher();
            MonitoringHotkey.Start();
            int runtimeStopStarted = 0;
            int runtimeStopped = 0;
            int resetStarted = 0;
            int normalExitStarted = 0;
            int normalExitCompleted = 0;
            int fatalResetPending = 0;
            bool restartAfterReset = false;
            Action<string> reportResetProgress = message =>
                uiDispatcher.Post(delegate { if (!panel.IsDisposed) panel.SetResetProgress(message); });
            Action finishShutdown = delegate
            {
                try { panel.FinishRuntimeShutdown(); panel.Dispose(); }
                finally { Application.Exit(); }
            };
            Action<Exception> shutdownFailed = error =>
            {
                if (!uiDispatcher.Post(delegate
                {
                    try
                    {
                        PaviseDialog.Warn(panel.IsDisposed ? null : panel, App.DisplayName,
                            AutomaticPolicy.Text("安全退出未完成，恢复记录已保留", "Safe shutdown incomplete; recovery records retained")
                            + "\r\n\r\n" + error.GetType().Name);
                    }
                    finally { finishShutdown(); }
                })) try { Application.Exit(); } catch { }
            };
            Func<bool> stopRuntime = () =>
            {
                // 重复退出不能把尚在执行的停止流程当作完成。
                if (Interlocked.CompareExchange(ref runtimeStopStarted, 1, 0) != 0)
                    return Volatile.Read(ref runtimeStopped) != 0;
                lock (startGate)
                {
                    exiting = true;
                }
                bool stopped = true;
                try
                {
                    if (!uiDispatcher.TrySend(delegate
                    {
                        panel.BeginRuntimeShutdown(Lang.T("wipe.progress.stop"));
                        MonitoringHotkey.Stop();
                        if (Volatile.Read(ref resetStarted) == 0) panel.Hide();
                        if (trayTip != null) { trayTip.Stop(); trayTip.Dispose(); }
                        if (updTimer != null) { updTimer.Stop(); updTimer.Dispose(); }
                        icon.Visible = false;
                        icon.Dispose();
                    })) stopped = false;
                }
                catch { stopped = false; }
                try { procNotify.Stop(); } catch { stopped = false; }
                try { PerformanceMonitoring.Stop(); } catch { stopped = false; }
                try { RtssTrayIcon.Forget(); } catch { }
                RtssOverlayClient.PreferredPid = null;
                RtssOverlayClient.PreferredBirth = null;
                RtssOverlayClient.SessionActive = null;
                RtssOverlayClient.SessionPreparing = null;
                RtssOverlayClient.ActiveMode = null;
                try { GameExtension.Shutdown(4000); } catch { stopped = false; }
                // 关闭超时保留待恢复记录。
                try { if (!tamer.Stop()) stopped = false; } catch { stopped = false; }
                try
                {
                    if (!gameMode.Stop() && (Volatile.Read(ref resetStarted) == 0 || !gameMode.ShutdownWritersStopped))
                        stopped = false;
                }
                catch
                {
                    if (Volatile.Read(ref resetStarted) == 0 || !gameMode.ShutdownWritersStopped) stopped = false;
                }
                try
                {
                    if (bootThread == Thread.CurrentThread || !bootThread.Join(8000))
                        stopped = false;
                }
                catch { stopped = false; }
                try { if (!PerformanceTuning.CloseEnvironmentWork(8000)) stopped = false; }
                catch { stopped = false; }
                try { if (!PrivacyControl.CloseWork(8000)) stopped = false; }
                catch { stopped = false; }
                Volatile.Write(ref runtimeStopped, stopped ? 1 : 0);
                return stopped;
            };
            Action<bool, bool> resetDataAndExit = null;
            Action doExit = () =>
            {
                if (Volatile.Read(ref resetStarted) != 0) return;
                // 保存失败与正常退出重合时，消息循环需处理安全重置。
                if (gameMode.ProfileStoreSaveFailed && resetDataAndExit != null)
                {
                    resetDataAndExit(true, true);
                    return;
                }
                if (Interlocked.CompareExchange(ref normalExitStarted, 1, 0) != 0) return;
                StartShutdownWorker(delegate
                {
                    stopRuntime();
                    Volatile.Write(ref normalExitCompleted, 1);
                    if (!uiDispatcher.Post(delegate
                    {
                        if (gameMode.ProfileStoreSaveFailed || Volatile.Read(ref fatalResetPending) != 0)
                        { resetDataAndExit(true, true); return; }
                        if (Volatile.Read(ref resetStarted) != 0) return;
                        finishShutdown();
                    })) throw new InvalidOperationException("UI shutdown result could not be dispatched");
                }, shutdownFailed);
            };

            panel.ExitApp = doExit;

            panel.UninstallApp = delegate
            {
                if (Volatile.Read(ref normalExitStarted) != 0) return;
                if (Interlocked.CompareExchange(ref resetStarted, 1, 0) != 0) return;
                string exePath = Application.ExecutablePath;
                panel.BeginRuntimeShutdown(AutomaticPolicy.Text("正在卸载并还原系统改动", "Uninstalling and restoring system changes"));
                StartShutdownWorker(delegate
                {
                    UninstallReport report;
                    try { report = UninstallMode.ExecuteInteractive(dir, exePath, stopRuntime, reportResetProgress); }
                    catch (Exception error) { report = new UninstallReport { Failure = Lang.T("wipe.cleanupfail") + " (" + error.GetType().Name + ")" }; }
                    string finalizerFailure = null;
                    bool finalizerStarted = report.RuntimeStopped && report.Failure == null
                        && UninstallFinalizer.Launch(exePath, out finalizerFailure);
                    if (!uiDispatcher.Post(delegate
                    {
                        DataOperationCompletion.CompleteUninstall(report, finalizerStarted, finalizerFailure,
                            (warning, message) =>
                            {
                                if (warning) PaviseDialog.Warn(panel, App.DisplayName, message);
                                else PaviseDialog.Success(panel, App.DisplayName, message);
                            }, finishShutdown);
                    })) throw new InvalidOperationException("UI uninstall result could not be dispatched");
                }, shutdownFailed);
            };
            resetDataAndExit = (fatal, closeApplication) =>
            {
                // 消息循环已停时不同步等待，保留恢复收据。
                if (!closeApplication && !uiDispatcher.IsOwnerThread) return;
                if (closeApplication && Volatile.Read(ref normalExitStarted) != 0
                    && Volatile.Read(ref normalExitCompleted) == 0)
                {
                    if (fatal) Volatile.Write(ref fatalResetPending, 1);
                    return;
                }
                if (Interlocked.CompareExchange(ref resetStarted, 1, 0) != 0) return;
                if (closeApplication)
                    panel.BeginRuntimeShutdown(AutomaticPolicy.Text("正在停止运行并还原系统改动", "Stopping and restoring system changes"));
                Action resetWork = delegate
                {
                    int files;
                    string failure;
                    bool cleared = TryResetUserData(dir, stopRuntime, out files, out failure,
                        closeApplication ? reportResetProgress : null);
                    Action completeReset = delegate
                    {
                        try
                        {
                            IWin32Window owner = panel.IsDisposed ? null : panel;
                            if (fatal)
                                PaviseDialog.Error(owner, Lang.T("store.savefatal.title"),
                                    cleared ? Lang.F("store.savefatal.body", dir)
                                        : Lang.F("store.savefatal.deletefail", dir, failure));
                            else if (cleared)
                                PaviseDialog.Success(owner, App.DisplayName, Lang.F("wipe.done", files));
                            else
                                PaviseDialog.Warn(owner, App.DisplayName, Lang.F("wipe.failed", failure));
                        }
                        finally
                        {
                            if (closeApplication)
                            finishShutdown();
                        }
                    };
                    if (closeApplication)
                    {
                        if (!uiDispatcher.Post(completeReset))
                            throw new InvalidOperationException("UI reset result could not be dispatched");
                    }
                    else completeReset();
                };
                if (closeApplication) StartShutdownWorker(resetWork, shutdownFailed);
                else resetWork();
            };
            panel.ResetApp = delegate
            {
                if (Volatile.Read(ref normalExitStarted) != 0) return;
                if (Interlocked.CompareExchange(ref resetStarted, 1, 0) != 0) return;
                panel.BeginRuntimeShutdown(Lang.T("wipe.progress.stop"));
                StartShutdownWorker(delegate
                {
                    bool stopped = false;
                    ResetDataReport report;
                    try { report = AppDataReset.Execute(dir, () => stopped = stopRuntime(), reportResetProgress); }
                    catch (Exception error) { report = new ResetDataReport { Failure = Lang.T("wipe.cleanupfail") + " (" + error.GetType().Name + ")" }; }
                    if (!uiDispatcher.Post(delegate
                    {
                        DataOperationCompletion.CompleteReset(report, stopped,
                            message => PaviseDialog.Warn(panel, App.DisplayName, message),
                            () => restartAfterReset = true, finishShutdown);
                    })) throw new InvalidOperationException("UI reset result could not be dispatched");
                }, shutdownFailed);
            };
            Action requestFatalStoreReset = () =>
            {
                if (!uiDispatcher.Post(() => resetDataAndExit(true, true)))
                {
                    try { Application.Exit(); } catch { }
                }
            };
            Application.ApplicationExit += (s, e) =>
            {
                try { RtssTrayIcon.Forget(); } catch { }
                if (!gameMode.ProfileStoreSaveFailed) return;
                resetDataAndExit(true, false);
            };

            // 所有退出路径均需经过恢复。
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
                if (ShouldShowPanelForMissingTray(autoStarted, showingPanel))
                {
                    Logger.Log(Lang.T("log.program.10"));
                    try { panel.ShowPanel(); } catch { }
                }
                else Logger.Info(Lang.T("log.program.autostart.traywaiting"));
            }
            SystemEvents.SessionEnded += (s, e) =>
            {
                try { gameMode.Enabled = false; } catch { }
                try { OptionalServicePause.Restore(); } catch { }
                try { LaptopPerfMode.Restore(); } catch { }
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
            gameMode.LibraryChanged += () =>
            {
                try { panel.NotifyLibraryChanged(); } catch { }
            };
            gameMode.SessionBriefed += brief =>
            {
                try { panel.NotifyLastSession(brief); } catch { }
            };

            trayTip = new System.Windows.Forms.Timer();
            trayTip.Interval = TrayTipIdleMs;
            trayTip.Tick += (s, e) =>
            {
                RtssTrayIcon.Maintain();
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

            try { Application.Run(); }
            finally { MonitoringHotkey.Stop(); uiDispatcher.Dispose(); }
            // 启动重置副本前释放旧实例锁。
            try { if (mtx != null) mtx.ReleaseMutex(); } catch { }
            if (mtx != null) mtx.Dispose();
            try { if (showEvt != null) showEvt.Dispose(); } catch { }
            try { if (exitEvt != null) exitEvt.Dispose(); } catch { }
            if (restartAfterReset)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                        "--reset-restart " + TaskHelper.AutostartDataArgument + " " + TaskHelper.QuoteCommandArgument(dir))
                    { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath) });
                }
                catch { PaviseDialog.Warn(null, App.DisplayName, Lang.T("wipe.restartfail")); }
            }
        }
    }
}
