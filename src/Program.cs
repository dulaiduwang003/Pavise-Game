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
        public const string Version = "2.1.3.3";
        public const string Author = "bdth";
        public const string AuthorEmail = "2074055628@qq.com";
        public const string QqGroup = "1051472054";
        public const string QqGroup2 = "1101249532";
        public const string QqGroup3 = "383761286";
        public const string QqGroup4 = "166255062";
        public const string Douyin = "44601770838";
        public const string PanUrl = "https://pan.quark.cn/s/3c8c986b3ea4";

        // 版本清单挂在自家对象存储 换源就改这两行
        //   两条指的是同一个文件 上海直连给国内 传输加速给境外 并发赛跑先到先用
        //   不按地区判断走哪条 判断会误伤代理和跨境线路 让网络自己比出快的那条更准
        public const string VersionFeedUrl =
            "https://paivse.oss-cn-shanghai.aliyuncs.com/version/version.json";

        public const string VersionFeedUrlAccelerate =
            "https://paivse.oss-accelerate.aliyuncs.com/version/version.json";

        public const string RepoName = "dulaiduwang003/Pavise-Game";
        public const string RepoUrl = "https://github.com/" + RepoName;
        public const string ReleasesUrl = RepoUrl + "/releases";

        // 概览页底栏三个外链 都在飞书 改地址只改这里
        //   教程是知识库页 问卷和 Bug 反馈是表单 两种地址形态不同 别互相照抄
        public const string GuideUrl =
            "https://ycnqux4mseky.feishu.cn/wiki/Do1jwytjmit0DJke8CvcyJEenxd?from=from_copylink";
        public const string SurveyUrl =
            "https://ycnqux4mseky.feishu.cn/share/base/form/shrcn8Je5doKoMrs8fqxC7kUo1G";
        public const string BugUrl =
            "https://ycnqux4mseky.feishu.cn/share/base/form/shrcnYFsTFhIfY93NpHZlUNEjMg";
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
                Dpi.Init();
                Paths.Init();
                Lang.Init();
                if (args.Length >= 4) Lang.Cur = args[3] == "en" ? 1 : (args[3] == "ja" ? 2 : 0);
                if (args.Length >= 5 && (args[4] == "light" || args[4] == "backdrop-light")) Theme.SetLight(true);
                // 截图默认保持干净背景 只有封面专项检查才读取用户当前封面
                if (args.Length >= 5 && (args[4] == "backdrop-light" || args[4] == "backdrop-dark"))
                    try { Backdrop.Init(); } catch { }
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
                    if (idx == (int)PageId.Log)
                    {
                        // 只有截图模式才注入这些遥测 让结构化日志视图能展示每种视觉状态
                        // 同时不碰用户真正的日志文件
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
                using (var dlg = new ContactDialog(true))
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
                // 上界跟着档位取值走 不是档位个数 掌机是 4 写死 3 会让它的配色重启后丢失
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
            try { WarnIfOsTooOld(); } catch { }
            Settings.Remove("EvidenceMode");
            try { VramShield.HealFromCrash(); } catch { }
            try { MemShield.HealFromCrash(); } catch { }
            int healedSuppression = SuppressionCore.HealFromCrash(Path.Combine(dir, SuppressionCore.StateFileName));
            if (healedSuppression > 0) Logger.Log(Lang.T("log.program.2") + healedSuppression + Lang.T("log.program.3"));
            PowerPlan.HealFromCrash();
            try { PowerPlan.ClearLegacyIdleDisableOnce(); } catch { }
            try { if (PowerPlan.HasParkResidue()) PowerPlan.RestoreParkState(); } catch { }
            try { UpdatePause.HealFromCrash(); } catch { }
            GameDvr.HealFromCrash();
            try { Mmcss.HealFromCrash(); } catch { }
            try { PresenceQos.HealFromCrash(); } catch { }
            try { PowerOverlay.HealFromCrash(); } catch { }
            try { DisplaySolo.HealFromCrash(); } catch { }
            try { GpuPowerMax.HealFromCrash(); } catch { }
            try { AdlxTweaks.HealFromCrash(); } catch { }
            try { InterruptAttribution.CleanupStaleSession(); } catch { }
            RenderLane.HealFromCrash();
            GpuPrefStage.HealFromCrash();
            try { AppGpuPreferences.HealFromCrash(); } catch { }
            try { IntelGraphicsTweaks.HealFromCrash(); } catch { }
            // 已下架的 IFEO 提优/关 CFG 只剩历史残留 全局开关用户没有逐游戏
            // 退役字段 不会触发库重置流程 这里按账本一次性收回旧写入
            try { if (IfeoBoost.HasResidue()) IfeoBoost.RestoreAll(); } catch { }
            try { if (CfgOffTweak.HasResidue()) CfgOffTweak.RestoreAll(); } catch { }
            CrashGuard.HealFromCrash();
            try { IrqRelocate.HealFromCrash(); } catch { }
            try { IrqAutoPilot.HealFromCrash(); } catch { }

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
            if (showingPanel)
                using (var welcome = new ContactDialog(true))
                {
                    welcome.StartPosition = FormStartPosition.CenterScreen;
                    welcome.ShowDialog();
                }

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
                }
                // 这一步可能在 Paths.Data 里写任务 XML 把它留在合并后的启动
                // 生命周期里 免得重置和一个没被跟踪的回调抢跑
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
                    || gameMode.NeedsGameProcessIdentity(name, session);
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

            // 必须在 GameMode 构造之后 台账那时才 Bind 到数据目录 提前调用会读到空账误提示
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
                // 第二次或者重入的退出 不能把正在进行的停止误当成已完成
                // 然后把脚下还活着的恢复状态删掉
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
                // 第一个停止失败了 第二个也要照样尝试 超时是
                // 真的失败 不是允许抹掉待处理恢复记录的许可
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
                // 保存失败与普通退出同时发生时 不能让退出先关掉
                // 消息泵而吞掉已排队的安全重置
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
                    // ApplicationExit 走的是同一条经过核实的停止与还原路径
                    // 如果是在工作线程还在跑的时候调用 重置会安全地中止
                    try { Application.Exit(); } catch { }
                }
            };
            Application.ApplicationExit += (s, e) =>
            {
                if (!gameMode.ProfileStoreSaveFailed) return;
                resetDataAndExit(true, false);
            };

            // 先装好带守卫的兜底 再去订阅和复查失败
            // 任何一条退出路径都不许绕过还原去强删数据
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

        internal const int OsBuildBaseline = 19041;
        internal const int OsBuildBest = 26100;
        private const string OsWarnedKey = "OsWarnedBuild";

        internal static bool OsBelowBaseline()
        {
            int build = Native.OsBuild();
            return build > 0 && build < OsBuildBaseline;
        }

        private static void WarnIfOsTooOld()
        {
            int build = Native.OsBuild();
            if (!OsBelowBaseline()) return;
            string stamp = build.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Settings.LoadStr(OsWarnedKey, "") == stamp) return;
            Settings.SaveStr(OsWarnedKey, stamp);
            Logger.Log(Lang.T("log.program.11") + build);
            PaviseDialog.Warn(null, App.DisplayName,
                Lang.F("os.old.body", build, OsBuildBaseline, OsBuildBest));
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

        internal static bool TryResetUserData(string dir, Func<bool> stopRuntime,
            out int files, out string failure)
        {
            files = 0;
            failure = null;
            if (string.IsNullOrWhiteSpace(dir))
            {
                failure = Lang.T("wipe.pathfail");
                return false;
            }
            try
            {
                if (stopRuntime == null || !stopRuntime())
                {
                    failure = Lang.T("wipe.stopfail");
                    return false;
                }
            }
            catch (Exception ex)
            {
                failure = Lang.T("wipe.stopfail") + " (" + ex.GetType().Name + ")";
                return false;
            }
            try
            {
                return LegacyPurge.WipeAll(dir, true, Lang.T("t.panelformsettingspage.3"),
                    out files, out failure);
            }
            catch (Exception ex)
            {
                failure = Lang.T("wipe.cleanupfail") + " (" + ex.GetType().Name + ")";
                return false;
            }
        }

    }

}
