using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class Program
    {
        private static void RunPrimaryInstance(string[] args, Mutex mutex, bool isAutostart)
        {
            bool createdNew;
            bool elevated = IsElevated();
            bool resetRestart = args.Length > 0 && args[0] == "--reset-restart";
            bool updateRestart = args.Length > 0 && (args[0] == "--update-restart" || args[0] == "--update-rollback");
            try
            {
                Paths.Init((isAutostart || resetRestart || updateRestart) ? Paths.ResolveStartupDataArgument(args) : null);
            }
            catch (Exception ex)
            {
                Logger.Error("启动 " + ex.Message);
                return;
            }
            bool updateStartupGate = SelfUpdateInstaller.IsStartupUpdatePending(args) && SelfUpdateGate.TryBegin();
            bool startupTaskMatches = false;
            if (elevated)
            {
                try { SelfUpdateInstaller.RecordLaunch(); }
                catch (Exception error) { Logger.Warn("更新启动身份不可用： " + error.GetType().Name); }
            }
            bool repairStartupTask = false;
            if (!elevated && TaskHelper.TaskExists())
            {
                string command;
                string arguments;
                startupTaskMatches = TaskHelper.TryReadTaskTarget(out command, out arguments) && TaskHelper.IsCurrentTaskTarget(Application.ExecutablePath, Paths.Data, command, arguments);
                repairStartupTask = !startupTaskMatches;
            }
            if (!elevated && startupTaskMatches)
            {
                InvalidateMainInstanceBootstrap();
                mutex.ReleaseMutex();
                mutex.Close();
                SetPendingPanelRequest(!isAutostart);
                if (TaskHelper.Run("/Run /TN PaviseAuto") == 0)
                {
                    return;
                }
                SetPendingPanelRequest(false);
                try
                {
                    mutex = new Mutex(true, "Global\\Pavise_SingleInstance", out createdNew);
                }
                catch
                {
                    createdNew = false;
                }
                if (!createdNew)
                {
                    if (ShouldSignalExistingPanel(isAutostart))
                    {
                        SignalShowPanel();
                    }
                    return;
                }
            }
            using (MainInstanceBootstrapLease originalMain = AcquireMainInstanceBootstrap(mutex))
            {
                SharedSpiBootstrap.Prepare(originalMain);
                EventWaitHandle showEvt = CreateSignalEvent("Global\\Pavise_ShowPanel");
                EventWaitHandle exitEvt = CreateSignalEvent("Global\\Pavise_Exit");
                Lang.Init();
                try
                {
                    Theme.SetLight(Settings.Load("UiLight", false));
                }
                catch
                {
                }
                try
                {
                    Backdrop.Init();
                }
                catch
                {
                }
                try
                {
                    for (int i = 0; i <= 4; i++)
                    {
                        Color c;
                        if (PresetValue.IsValid(i) && Col.TryHex(Settings.LoadStr("ModeAccent" + i, ""), out c))
                        {
                            Theme.SetModeColorOverride((PerformancePreset)i, c);
                        }
                    }
                }
                catch
                {
                }
                string dir = Paths.Data;
                Logger.LogPath = Path.Combine(dir, "Pavise.log");
                Logger.WritesEnabled = Settings.Load("LogWritesEnabled", true);
                Logger.RuntimeIdentity();
                AutostartJobHandoff.LogOutcome();
                        try { SecurityProductCatalog.StartupScanOnce(); }
                        catch (SecurityProbeTerminationException error)
                        { Logger.Warn(error.Message); Environment.ExitCode = 15; return; }
                if (repairStartupTask)
                {
                    Logger.Log(Lang.T("log.program.1", 0));
                }
                Settings.Remove("EvidenceMode");
                try
                {
                    MemShield.HealFromCrash();
                }
                catch
                {
                }
                LaboratorySession.Recover();
                if (!LaboratoryManualActions.RecoverStartup()) Logger.Warn("手动实验室恢复仍未完成");
                PowerPlan.HealFromCrash();
                try
                {
                    PowerPlan.SweepUntrackedManagedPlansOnce();
                }
                catch (Exception sweepError)
                {
                    Logger.Warn("电源 事件=遗留清理 失败详情=" + sweepError.GetType().Name);
                }
                try
                {
                    PowerPlan.ClearLegacyIdleDisableOnce();
                }
                catch
                {
                }
                try
                {
                    if (PowerPlan.HasParkResidue())
                    {
                        PowerPlan.RestoreParkState();
                    }
                }
                catch
                {
                }
                try
                {
                    UpdatePause.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    MaintenancePause.HealFromCrash();
                }
                catch
                {
                }
                GameDvr.HealFromCrash();
                try
                {
                    Mmcss.HealFromCrash();
                }
                catch
                {
                }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        ProcessorPowerPlatform.Interface current = ProcessorPowerPlatform.Current;
                    }
                    catch
                    {
                    }
                });
                try
                {
                    RssSteer.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    PresenceQos.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    PowerOverlay.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    DisplaySolo.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    GpuPowerMax.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    GpuClockLock.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    NvVrrWindowed.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    IntelEndurance.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    LaptopPerfMode.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    AdlxTweaks.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    InterruptAttribution.CleanupStaleSession();
                }
                catch
                {
                }
                RenderLane.HealFromCrash();
                try
                {
                    FrameCoordination.HealFromCrash();
                }
                catch
                {
                }
                GpuPrefStage.HealFromCrash();
                try
                {
                    AppGpuPreferences.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    if (IfeoBoost.HasResidue())
                    {
                        IfeoBoost.RestoreAll();
                    }
                }
                catch
                {
                }
                try
                {
                    if (CfgOffTweak.HasResidue())
                    {
                        CfgOffTweak.RestoreAll();
                    }
                }
                catch
                {
                }
                try
                {
                    PerformanceTuning.RestoreSession();
                }
                catch
                {
                }
                if (!CoreIsolationWorker.Recover(new CoreIsolationSettingsStore()))
                {
                    Logger.Warn(Lang.T("schedule.isolation.pending", 0));
                }
                try
                {
                    LegacyReservedCores.Heal();
                }
                catch
                {
                }
                if (HardThreadFileStore.CanonicalRootHasRecoveryDebt(Path.Combine(dir, "CriticalThreadHardAffinityV1")))
                {
                    CriticalThreadGuardian.TrySettleHistoricalGone(dir);
                }
                string jobAffinityRecoveryReason;
                bool jobAffinityRecovered = GameAffinityJobLease.Recover(out jobAffinityRecoveryReason);
                if (!jobAffinityRecovered) Logger.Warn("游戏亲和性 Job 等待恢复： " + jobAffinityRecoveryReason);
                if (jobAffinityRecovered)
                {
                    int suppressionRestored = SuppressionCore.HealFromCrash(Path.Combine(dir, "Pavise.suppression.state"));
                    if (suppressionRestored > 0) Logger.Log(Lang.T("log.program.2", 0) + suppressionRestored + Lang.T("log.program.3", 0));
                }
                if (jobAffinityRecovered && !new AntiCheatAffinityLease().Recover()) Logger.Warn("反作弊亲和性等待恢复");
                if (jobAffinityRecovered && !HardThreadFileStore.CanonicalRootHasRecoveryDebt(Path.Combine(dir, "CriticalThreadHardAffinityV1")))
                {
                    CrashGuard.HealFromCrash();
                }
                else
                {
                    Logger.Warn(PerformancePolicy.Text("线程或 Job 亲和性恢复尚未结清，保留进程亲和性恢复记录", "线程或 Job 亲和性恢复未完成，已保留进程亲和性恢复记录"));
                }
                try
                {
                    IrqRelocate.HealFromCrash();
                }
                catch
                {
                }
                try
                {
                    IrqAutoPilot.HealFromCrash();
                }
                catch
                {
                }
                if (BlockIfOsTooOld(dir))
                {
                    return;
                }
                bool requested = Settings.Load("ShowPanelOnNextStart", false);
                long result2;
                long.TryParse(Settings.LoadStr("ShowPanelRequestUtcTicks", ""), out result2);
                SetPendingPanelRequest(false);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    try
                    {
                        Logger.AppendCrash(Path.Combine(dir, "crash.log"), string.Concat(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "  [界面] ", e.Exception, Environment.NewLine));
                    }
                    catch
                    {
                    }
                };
                bool shouldShowStartupPanel = ShouldShowStartupPanel(isAutostart, requested, result2, DateTime.UtcNow.Ticks);
                RetiredBackgroundCpuCap.Retire();
                SuppressionCore core3 = new SuppressionCore(Path.Combine(dir, "Pavise.suppression.state"));
                Tamer tamer = new Tamer(core3);
                GameMode gameMode = new GameMode(dir, core3);
                tamer.AffinityGameContext = gameMode.GetAntiCheatAffinityContext;
                gameMode.InitializeAutomation();
                gameMode.Enabled = Settings.Load("GameModeOn", true);
                if (gameMode.ProfileStoreSaveFailed)
                {
                    int files;
                    string failure;
                    bool tryResetUserData = TryResetUserData(dir, delegate
                    {
                        bool stopSucceeded = tamer.Stop();
                        bool stopSucceeded2 = gameMode.Stop();
                        return stopSucceeded && stopSucceeded2;
                    }, out files, out failure);
                    PaviseDialog.Error(null, Lang.T("store.savefatal.title"), tryResetUserData ? Lang.F("store.savefatal.body", dir) : Lang.F("store.savefatal.deletefail", dir, failure));
                    return;
                }
                object startGate = new object();
                bool exiting = false;
                int updateRuntimeReady = 0;
                Thread bootThread = new Thread((ThreadStart)delegate
                {
                    try
                    {
                        SvcPause.HealFromCrash();
                    }
                    catch
                    {
                    }
                    try
                    {
                        DoTweak.HealFromCrash();
                    }
                    catch
                    {
                    }
                    try
                    {
                        OptionalServicePause.HealFromCrash();
                    }
                    catch
                    {
                    }
                    lock (startGate)
                    {
                        if (exiting)
                        {
                            return;
                        }
                        LegacyMonitoringCleanup.Start();
                        tamer.Start();
                        gameMode.Start();
                        GameExtension.Start();
                    }
                    if (elevated && !Volatile.Read(ref exiting))
                    {
                        try
                        {
                            TaskHelper.RefreshStartupTask();
                        }
                        catch
                        {
                        }
                    }
                    Volatile.Write(ref updateRuntimeReady, 1);
                });
                bootThread.IsBackground = true;
                bootThread.Start();
                ProcNotify procNotify = new ProcNotify();
                procNotify.CaptureStartIdentity = (string name, int session) => gameMode.NeedsWhitelistParentIdentity(session) || gameMode.NeedsGameFamilyIdentity(session) || gameMode.NeedsGameProcessIdentity(name, session) || GameExtension.NeedsProcessIdentity(name);
                procNotify.CaptureParentIdentity = (int parentPid, string name, int session) => gameMode.NeedsWhitelistParentIdentity(session) || gameMode.NeedsGameFamilyIdentity(session) || gameMode.NeedsLauncherChildParentIdentity(parentPid, name, session);
                procNotify.BatchChanged += delegate(ProcessChangeBatch batch)
                {
                    gameMode.NotifyProcessChanges(batch);
                    tamer.NotifyProcessChanges(batch);
                    GameExtension.NotifyProcessChanges(batch);
                };
                SystemAudit.LibraryPaths = gameMode.LibraryExecutablePaths;
                procNotify.FastTrack = (string name, int session) => gameMode.NeedsGameProcessIdentity(name, session);
                procNotify.FastStart += gameMode.KickGameDetectionNow;
                procNotify.Start();
                gameMode.ProcessEventsAvailable = procNotify.IsActive;
                tamer.ProcessEventsAvailable = procNotify.IsActive;
                PerformancePreset runtimeIconMode = gameMode.ActivePreset;
                bool runtimeIconEnabled = gameMode.Enabled;
                Icon icon3 = IconArt.MakeMultiIcon(runtimeIconMode, runtimeIconEnabled);
                PanelForm panel = new PanelForm(tamer, gameMode, icon3, elevated);
                GC.KeepAlive(panel.Handle);
                if (shouldShowStartupPanel)
                {
                    panel.ShowPanel();
                }
                if (showEvt != null)
                {
                    Thread thread = new Thread((ThreadStart)delegate
                    {
                        while (true)
                        {
                            showEvt.WaitOne();
                            try
                            {
                                panel.ShowPanel();
                            }
                            catch
                            {
                            }
                        }
                    });
                    thread.IsBackground = true;
                    thread.Start();
                }
                if (elevated)
                {
                    Native.AllowTaskbarCreatedMessage();
                }
                NotifyIcon icon = new NotifyIcon();
                icon.Icon = (Icon)icon3.Clone();
                icon3.Dispose();
                icon.Text = (elevated ? Lang.T("tray.idle") : Lang.T("tray.noelev"));
                System.Windows.Forms.Timer trayTip = null;
                System.Windows.Forms.Timer updTimer = null;
                RuntimeUiDispatcher uiDispatcher = new RuntimeUiDispatcher();
                int runtimeStopStarted = 0;
                int runtimeStopped = 0;
                int resetStarted = 0;
                int normalExitStarted = 0;
                int normalExitCompleted = 0;
                int fatalResetPending = 0;
                int selfUpdateStopping = 0;
                bool restartAfterReset = false;
                Action<string> reportResetProgress = delegate(string message)
                {
                    uiDispatcher.Post(delegate
                    {
                        if (!panel.IsDisposed)
                        {
                            panel.SetResetProgress(message);
                        }
                    });
                };
                Action finishShutdown = delegate
                {
                    try
                    {
                        panel.FinishRuntimeShutdown();
                        panel.Dispose();
                    }
                    finally
                    {
                        Application.Exit();
                    }
                };
                Action<Exception> shutdownFailed = delegate(Exception error)
                {
                    if (!uiDispatcher.Post(delegate
                    {
                        try
                        {
                            PaviseDialog.Warn(panel.IsDisposed ? null : panel, "PAVISE AUTO", PerformancePolicy.Text("安全退出未完成，恢复记录已保留", "Safe shutdown incomplete; recovery records retained") + "\r\n\r\n" + error.GetType().Name);
                        }
                        finally
                        {
                            finishShutdown();
                        }
                    }))
                    {
                        try
                        {
                            Application.Exit();
                        }
                        catch
                        {
                        }
                    }
                };
                Func<bool> stopRuntime = delegate
                {
                    if (Interlocked.CompareExchange(ref runtimeStopStarted, 1, 0) != 0)
                    {
                        return Volatile.Read(ref runtimeStopped) != 0;
                    }
                    lock (startGate)
                    {
                        exiting = true;
                    }

                    bool succeeded = true;
                    try
                    {
                        if (!uiDispatcher.TrySend(delegate
                        {
                            panel.BeginRuntimeShutdown(Lang.T("wipe.progress.stop"));
                            if (Volatile.Read(ref resetStarted) == 0)
                            {
                                panel.Hide();
                            }
                            if (trayTip != null)
                            {
                                trayTip.Stop();
                                trayTip.Dispose();
                            }
                            if (updTimer != null)
                            {
                                updTimer.Stop();
                                updTimer.Dispose();
                            }
                            icon.Visible = false;
                            icon.Dispose();
                        }))
                        {
                            succeeded = false;
                        }
                    }
                    catch
                    {
                        succeeded = false;
                    }
                    try
                    {
                        if (!panel.WaitForConfigurationPresetWork(8000)) succeeded = false;
                        procNotify.Stop();
                    }
                    catch
                    {
                        succeeded = false;
                    }


                    try
                    {
                        GameExtension.Shutdown(4000);
                    }
                    catch
                    {
                        succeeded = false;
                    }
                    try
                    {
                        if (!tamer.Stop())
                        {
                            succeeded = false;
                        }
                    }
                    catch
                    {
                        succeeded = false;
                    }
                    try
                    {
                        if (!gameMode.Stop() && (Volatile.Read(ref resetStarted) == 0 || !gameMode.ShutdownWritersStopped))
                        {
                            succeeded = false;
                        }
                    }
                    catch
                    {
                        if (Volatile.Read(ref resetStarted) == 0 || !gameMode.ShutdownWritersStopped)
                        {
                            succeeded = false;
                        }
                    }
                    try
                    {
                        if (bootThread == Thread.CurrentThread || !bootThread.Join(8000))
                        {
                            succeeded = false;
                        }
                    }
                    catch
                    {
                        succeeded = false;
                    }

                    try
                    {
                        if (!PerformanceTuning.CloseEnvironmentWork(8000))
                        {
                            succeeded = false;
                        }
                    }
                    catch
                    {
                        succeeded = false;
                    }
                    try
                    {
                        if (!PrivacyControl.CloseWork(8000))
                        {
                            succeeded = false;
                        }
                    }
                    catch
                    {
                        succeeded = false;
                    }
                    Volatile.Write(ref runtimeStopped, succeeded ? 1 : 0);
                    return succeeded;
                };
                Action<bool, bool> resetDataAndExit = null;
                Action doExit = delegate
                {
                    if (SelfUpdateGate.IsBusy) { UpdateDialog.ActivateCurrent(); return; }
                    if (Volatile.Read(ref resetStarted) == 0)
                    {
                        if (gameMode.ProfileStoreSaveFailed && resetDataAndExit != null)
                        {
                            resetDataAndExit(true, true);
                        }
                        else if (Interlocked.CompareExchange(ref normalExitStarted, 1, 0) == 0)
                        {
                            StartShutdownWorker(delegate
                            {
                                stopRuntime();
                                Volatile.Write(ref normalExitCompleted, 1);
                                if (!uiDispatcher.Post(delegate
                                {
                                    if (gameMode.ProfileStoreSaveFailed || Volatile.Read(ref fatalResetPending) != 0)
                                    {
                                        resetDataAndExit(true, true);
                                    }
                                    else if (Volatile.Read(ref resetStarted) == 0)
                                    {
                                        finishShutdown();
                                    }
                                }))
                                {
                                    throw new InvalidOperationException("UI shutdown result could not be dispatched");
                                }
                            }, shutdownFailed);
                        }
                    }
                };
                panel.ExitApp = doExit;
                panel.InstallSelfUpdate = delegate(UpdateDialog dialog, string stagedExecutable, SelfUpdatePackage package)
                {
                    string admission = panel.SelfUpdateAdmissionReason();
                    if (admission != null || !elevated || Volatile.Read(ref resetStarted) != 0
                        || Interlocked.CompareExchange(ref normalExitStarted, 1, 0) != 0)
                    {
                        dialog.ShowManual(admission ?? PerformancePolicy.Text("当前无法安全安装更新，请使用手动更新。", "The update cannot be installed safely right now. Use a manual update."));
                        return;
                    }
                    SelfUpdateTransaction transaction = null;
                    dialog.SetInstalling(PerformancePolicy.Text("正在准备安全退出，尚未替换程序。", "Preparing a safe shutdown; the program has not been replaced."));
                    StartShutdownWorker(delegate
                    {
                        transaction = SelfUpdateInstaller.Prepare(stagedExecutable, package);
                        SelfUpdateInstaller.LaunchHelper(transaction);
                        Volatile.Write(ref selfUpdateStopping, 1);
                        if (!stopRuntime()) throw new InvalidOperationException(PerformancePolicy.Text("安全退出未完成，恢复记录已保留，未安装更新。", "Safe shutdown did not complete; recovery records are retained and the update was not installed."));
                        SelfUpdateInstaller.CommitSafeExit(transaction);
                        if (!uiDispatcher.Post(delegate
                        {
                            dialog.CloseForHandoff();
                            SelfUpdateGate.End();
                            finishShutdown();
                        })) throw new InvalidOperationException("Update shutdown result could not be dispatched");
                    }, delegate(Exception error)
                    {
                        try { if (transaction != null) SelfUpdateInstaller.Abort(transaction); }
                        catch (Exception cleanupError) { Logger.Warn("更新中止后保留恢复状态： " + cleanupError.GetType().Name); }
                        Logger.Warn("自更新已停止： " + error.GetType().Name);
                        uiDispatcher.Post(delegate
                        {
                            if (Volatile.Read(ref runtimeStopStarted) != 0)
                            {
                                dialog.Owner = null;
                                panel.Hide();
                                panel.FinishRuntimeShutdown();
                                dialog.FormClosed += delegate { SelfUpdateGate.End(); finishShutdown(); };
                                dialog.ShowStoppedManual(error.Message + "\r\n" + PerformancePolicy.Text("守护已停止，请退出后重新打开 Pavise。", "The guard has stopped. Exit and reopen Pavise."));
                            }
                            else
                            {
                                Interlocked.Exchange(ref normalExitStarted, 0);
                                dialog.ShowManual(error.Message);
                            }
                        });
                    });
                };
                panel.UninstallApp = delegate
                {
                    if (SelfUpdateGate.IsBusy) { UpdateDialog.ActivateCurrent(); return; }
                    if (Volatile.Read(ref normalExitStarted) == 0 && Interlocked.CompareExchange(ref resetStarted, 1, 0) == 0)
                    {
                        string exePath = Application.ExecutablePath;
                        panel.BeginRuntimeShutdown(PerformancePolicy.Text("正在卸载并还原系统改动", "Uninstalling and restoring system changes"));
                        StartShutdownWorker(delegate
                        {
                            UninstallReport report2;
                            try
                            {
                                report2 = UninstallMode.ExecuteInteractive(dir, exePath, stopRuntime, reportResetProgress);
                            }
                            catch (Exception ex3)
                            {
                                report2 = new UninstallReport
                                {
                                    Failure = Lang.T("wipe.cleanupfail") + " (" + ex3.GetType().Name + ")"
                                };
                            }
                            string finalizerFailure = null;
                            bool finalizerStarted = report2.RuntimeStopped && report2.Failure == null && UninstallFinalizer.Launch(exePath, out finalizerFailure);
                            if (!uiDispatcher.Post(delegate
                            {
                                DataOperationCompletion.CompleteUninstall(report2, finalizerStarted, finalizerFailure, delegate(bool warning, string message)
                                {
                                    if (warning)
                                    {
                                        PaviseDialog.Warn(panel, "PAVISE AUTO", message);
                                    }
                                    else
                                    {
                                        PaviseDialog.Success(panel, "PAVISE AUTO", message);
                                    }
                                }, finishShutdown);
                            }))
                            {
                                throw new InvalidOperationException("UI uninstall result could not be dispatched");
                            }
                        }, shutdownFailed);
                    }
                };
                resetDataAndExit = delegate(bool fatal, bool closeApplication)
                {
                    // 关闭或保存失败时，更新流程不能删除用户数据。
                    if (Volatile.Read(ref selfUpdateStopping) != 0 || SelfUpdateGate.IsBusy)
                    {
                        if (fatal) { Volatile.Write(ref fatalResetPending, 1); Logger.Warn("自更新期间延后数据重置，保留用户数据"); }
                        UpdateDialog.ActivateCurrent();
                        return;
                    }
                    if (closeApplication || uiDispatcher.IsOwnerThread)
                    {
                        if (closeApplication && Volatile.Read(ref normalExitStarted) != 0 && Volatile.Read(ref normalExitCompleted) == 0)
                        {
                            if (fatal)
                            {
                                Volatile.Write(ref fatalResetPending, 1);
                            }
                        }
                        else if (Interlocked.CompareExchange(ref resetStarted, 1, 0) == 0)
                        {
                            if (closeApplication)
                            {
                                panel.BeginRuntimeShutdown(PerformancePolicy.Text("正在停止运行并还原系统改动", "Stopping and restoring system changes"));
                            }
                            Action action2 = delegate
                            {
                                int files2;
                                string failure2;
                                bool cleared = TryResetUserData(dir, stopRuntime, out files2, out failure2, closeApplication ? reportResetProgress : null);
                                Action action3 = delegate
                                {
                                    try
                                    {
                                        IWin32Window owner = (panel.IsDisposed ? null : panel);
                                        if (fatal)
                                        {
                                            PaviseDialog.Error(owner, Lang.T("store.savefatal.title"), cleared ? Lang.F("store.savefatal.body", dir) : Lang.F("store.savefatal.deletefail", dir, failure2));
                                        }
                                        else if (cleared)
                                        {
                                            PaviseDialog.Success(owner, "PAVISE AUTO", Lang.F("wipe.done", files2));
                                        }
                                        else
                                        {
                                            PaviseDialog.Warn(owner, "PAVISE AUTO", Lang.F("wipe.failed", failure2));
                                        }
                                    }
                                    finally
                                    {
                                        if (closeApplication)
                                        {
                                            finishShutdown();
                                        }
                                    }
                                };
                                if (closeApplication)
                                {
                                    if (!uiDispatcher.Post(action3))
                                    {
                                        throw new InvalidOperationException("UI reset result could not be dispatched");
                                    }
                                }
                                else
                                {
                                    action3();
                                }
                            };
                            if (closeApplication)
                            {
                                StartShutdownWorker(action2, shutdownFailed);
                            }
                            else
                            {
                                action2();
                            }
                        }
                    }
                };
                panel.ResetApp = delegate
                {
                    if (Volatile.Read(ref normalExitStarted) == 0 && Interlocked.CompareExchange(ref resetStarted, 1, 0) == 0)
                    {
                        panel.BeginRuntimeShutdown(Lang.T("wipe.progress.stop"));
                        StartShutdownWorker(delegate
                        {
                            bool stopped = false;
                            ResetDataReport report;
                            try
                            {
                                report = AppDataReset.Execute(dir, () => stopped = stopRuntime(), reportResetProgress);
                            }
                            catch (Exception ex2)
                            {
                                report = new ResetDataReport
                                {
                                    Failure = Lang.T("wipe.cleanupfail") + " (" + ex2.GetType().Name + ")"
                                };
                            }
                            if (!uiDispatcher.Post(delegate
                            {
                                DataOperationCompletion.CompleteReset(report, stopped, delegate(string message)
                                {
                                    PaviseDialog.Warn(panel, "PAVISE AUTO", message);
                                }, delegate
                                {
                                    restartAfterReset = true;
                                }, finishShutdown);
                            }))
                            {
                                throw new InvalidOperationException("UI reset result could not be dispatched");
                            }
                        }, shutdownFailed);
                    }
                };
                Action action = delegate
                {
                    if (!uiDispatcher.Post(delegate
                    {
                        resetDataAndExit(true, true);
                    }))
                    {
                        try
                        {
                            Application.Exit();
                        }
                        catch
                        {
                        }
                    }
                };
                Application.ApplicationExit += delegate
                {
                    try
                    {
                        lock (startGate)
                        {
                            exiting = true;
                        }
                    }
                    catch
                    {
                    }
                    if (gameMode.ProfileStoreSaveFailed)
                    {
                        resetDataAndExit(true, false);
                    }
                };
                gameMode.ProfileStoreSaveFailure += action;
                if (gameMode.ProfileStoreSaveFailed)
                {
                    action();
                }
                TrayMenu trayMenu = new TrayMenu(gameMode, doExit, delegate
                {
                    panel.SyncAllToggles();
                });
                trayMenu.Strip.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs opening)
                {
                    if (SelfUpdateGate.IsBusy) { opening.Cancel = true; UpdateDialog.ActivateCurrent(); }
                };
                if (exitEvt != null)
                {
                    Thread thread2 = new Thread((ThreadStart)delegate
                    {
                        exitEvt.WaitOne();
                        try
                        {
                            panel.BeginInvoke(doExit);
                        }
                        catch
                        {
                        }
                    });
                    thread2.IsBackground = true;
                    thread2.Start();
                }
                icon.ContextMenuStrip = trayMenu.Strip;
                bool notificationAreaPresentSucceeded;
                try
                {
                    icon.Visible = true;
                    notificationAreaPresentSucceeded = Native.NotificationAreaPresent();
                }
                catch
                {
                    notificationAreaPresentSucceeded = false;
                }
                if (!notificationAreaPresentSucceeded)
                {
                    if (ShouldShowPanelForMissingTray(isAutostart, shouldShowStartupPanel))
                    {
                        Logger.Log(Lang.T("log.program.10", 0));
                        try
                        {
                            panel.ShowPanel();
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        Logger.Info(Lang.T("log.program.autostart.traywaiting", 0));
                    }
                }
                SystemEvents.SessionEnded += delegate
                {
                    try { tamer.PanicRestore(); } catch { }
                    bool labClosed = false;
                    try { labClosed = gameMode.CloseLaboratory(8000); } catch { }
                    try
                    {
                        gameMode.Enabled = false;
                    }
                    catch
                    {
                    }
                    try
                    {
                        OptionalServicePause.Restore();
                    }
                    catch
                    {
                    }
                    try
                    {
                        LaptopPerfMode.Restore();
                    }
                    catch
                    {
                    }
                    try
                    {
                        if (labClosed) PowerPlan.Restore();
                    }
                    catch
                    {
                    }
                    try
                    {
                        GameDvr.Restore();
                    }
                    catch
                    {
                    }
                    try
                    {
                        Mmcss.Restore();
                    }
                    catch
                    {
                    }
                    try
                    {
                        PresenceQos.Restore();
                    }
                    catch
                    {
                    }
                    try
                    {
                        PowerOverlay.Restore();
                    }
                    catch
                    {
                    }
                    try
                    {
                        if (labClosed) GpuPowerMax.Restore();
                    }
                    catch
                    {
                    }
                };
                gameMode.SessionEnded += delegate(string msg)
                {
                    try
                    {
                        panel.BeginInvoke((MethodInvoker)delegate
                        {
                            try
                            {
                                icon.ShowBalloonTip(10000, "PAVISE AUTO", msg, ToolTipIcon.Info);
                            }
                            catch
                            {
                            }
                        });
                    }
                    catch
                    {
                    }
                };
                gameMode.LibraryChanged += delegate
                {
                    try
                    {
                        panel.NotifyLibraryChanged();
                    }
                    catch
                    {
                    }
                };
                gameMode.SessionBriefed += delegate(string brief)
                {
                    try
                    {
                        panel.NotifyLastSession(brief);
                    }
                    catch
                    {
                    }
                };
                trayTip = new System.Windows.Forms.Timer();
                trayTip.Interval = 1500;
                trayTip.Tick += delegate
                {
                    int intervalValue = (gameMode.IsActive ? 6000 : 1500);
                    if (trayTip.Interval != intervalValue)
                    {
                        trayTip.Interval = intervalValue;
                    }
                    PerformancePreset activePreset = gameMode.ActivePreset;
                    bool enabled = gameMode.Enabled;
                    if (activePreset != runtimeIconMode || enabled != runtimeIconEnabled)
                    {
                        runtimeIconMode = activePreset;
                        runtimeIconEnabled = enabled;
                        using (Icon icon5 = IconArt.MakeMultiIcon(activePreset, enabled))
                        {
                            Icon icon4 = icon.Icon;
                            icon.Icon = (Icon)icon5.Clone();
                            panel.SetRuntimeIcon(icon5);
                            if (icon4 != null)
                            {
                                icon4.Dispose();
                            }
                        }
                    }
                    string textValue;
                    if (!elevated)
                    {
                        textValue = Lang.T("tray.noelev");
                    }
                    else
                    {
                        string activeGame = gameMode.ActiveGame;
                        string armedGame = ((activeGame == null) ? gameMode.ArmedGame : null);
                        textValue = ((activeGame != null) ? Lang.F("tray.active", activeGame) : ((armedGame != null) ? Lang.F("tray.armed", armedGame) : Lang.T("tray.idle")));
                    }
                    if (textValue.Length > 63)
                    {
                        textValue = textValue.Substring(0, 62) + " ";
                    }
                    if (icon.Text != textValue)
                    {
                        icon.Text = textValue;
                    }
                };
                trayTip.Start();
                updTimer = new System.Windows.Forms.Timer();
                updTimer.Interval = 6000;
                updTimer.Tick += delegate
                {
                    updTimer.Stop();
                    updTimer.Dispose();
                    if (!Volatile.Read(ref exiting))
                    {
                        UpdateChecker.CheckAsync(delegate(UpdateResult r)
                        {
                            if (!Volatile.Read(ref exiting))
                            {
                                panel.NotifyUpdate(r);
                                if (r.Ok && r.Donate != null)
                                {
                                    panel.NotifyDonate(r.Donate);
                                }
                                if (r.Ok && r.Newer)
                                {
                                    Logger.Log(Lang.T("log.program.6", 0) + r.Latest + Lang.T("log.program.7", 0) + App.VersionTag + " ");
                                    try
                                    {
                                        panel.BeginInvoke((MethodInvoker)delegate
                                        {
                                            try
                                            {
                                                icon.ShowBalloonTip(8000, "PAVISE AUTO", Lang.F("bal.update", r.Latest), ToolTipIcon.Info);
                                            }
                                            catch
                                            {
                                            }
                                        });
                                        return;
                                    }
                                    catch
                                    {
                                        return;
                                    }
                                }
                                if (r.Ok)
                                {
                                    Logger.Log(Lang.T("log.program.8", 0) + App.VersionTag + " ");
                                }
                                else
                                {
                                    Logger.Warn(Lang.T("log.program.9", 0) + r.Error);
                                }
                            }
                        });
                    }
                };
                updTimer.Start();
                if (!elevated)
                {
                    icon.ShowBalloonTip(8000, "PAVISE AUTO", Lang.T("bal.noelev"), ToolTipIcon.Warning);
                }
                icon.DoubleClick += delegate
                {
                    panel.ShowPanel();
                };
                EventHandler acknowledgeUpdate = null;
                acknowledgeUpdate = delegate
                {
                    if (Volatile.Read(ref updateRuntimeReady) == 0 || Volatile.Read(ref exiting)) return;
                    Application.Idle -= acknowledgeUpdate;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            if (!SelfUpdateInstaller.AcknowledgeStartup(args)) Logger.Warn("新版启动无法提交，保留恢复文件");
                            if (updateStartupGate)
                            {
                                while (!Volatile.Read(ref exiting) && SelfUpdateInstaller.IsStartupUpdatePending(args)) Thread.Sleep(200);
                                SelfUpdateGate.End();
                                updateStartupGate = false;
                            }
                            string updateFailure = SelfUpdateInstaller.GetStartupManualFailure();
                            if (updateFailure != null) uiDispatcher.Post(delegate
                            {
                                if (!Volatile.Read(ref exiting) && !panel.IsDisposed && !SelfUpdateGate.IsBusy)
                                {
                                    panel.ShowPanel();
                                    UpdateDialog.ShowStartupFailure(panel, updateFailure);
                                }
                            });
                        }
                        catch (Exception error) { Logger.Warn("更新启动确认失败： " + error.GetType().Name); }
                        finally { if (updateStartupGate) { SelfUpdateGate.End(); updateStartupGate = false; } }
                    });
                };
                Application.Idle += acknowledgeUpdate;
                try
                {
                    Application.Run();
                }
                finally
                {
                    uiDispatcher.Dispose();
                }
                InvalidateMainInstanceBootstrap();
                try
                {
                    if (mutex != null)
                    {
                        mutex.ReleaseMutex();
                    }
                }
                catch
                {
                }
                if (mutex != null)
                {
                    mutex.Dispose();
                }
                try
                {
                    if (showEvt != null)
                    {
                        showEvt.Dispose();
                    }
                }
                catch
                {
                }
                try
                {
                    if (exitEvt != null)
                    {
                        exitEvt.Dispose();
                    }
                }
                catch
                {
                }
                if (!restartAfterReset)
                {
                    return;
                }
                try
                {
                    ProcessStartInfo processStartInfo = new ProcessStartInfo(Application.ExecutablePath, "--reset-restart --autostart-data " + TaskHelper.QuoteCommandArgument(dir));
                    processStartInfo.UseShellExecute = false;
                    processStartInfo.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                    Process.Start(processStartInfo);
                    return;
                }
                catch
                {
                    PaviseDialog.Warn(null, "PAVISE AUTO", Lang.T("wipe.restartfail"));
                    return;
                }
            }
        }
    }
}
