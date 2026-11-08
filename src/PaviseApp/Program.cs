using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
	internal static class Program
	{
		internal sealed class MainInstanceBootstrapLease : IDisposable
		{
			private readonly Mutex originalMutex;

			private readonly SafeWaitHandle originalHandle;

			private readonly IntPtr handleValue;

			private readonly Thread ownerThread;

			private int active = 1;

			private int claimed;

			internal MainInstanceBootstrapLease(Mutex ownedMutex, object witness)
			{
				if (!object.ReferenceEquals(witness, mainInstanceBootstrapWitness))
				{
					throw new UnauthorizedAccessException("Only the original Main acquisition can mint this capability");
				}
				if (ownedMutex == null)
				{
					throw new ArgumentNullException("ownedMutex");
				}
				originalMutex = ownedMutex;
				ownerThread = Thread.CurrentThread;
				originalHandle = ownedMutex.SafeWaitHandle;
				if (originalHandle.IsClosed || originalHandle.IsInvalid)
				{
					throw new InvalidOperationException("The original single-instance handle is unavailable");
				}
				handleValue = originalHandle.DangerousGetHandle();
			}

			internal void RequireCurrent()
			{
				if (Volatile.Read(ref active) != 1 || !object.ReferenceEquals(ownerThread, Thread.CurrentThread) || originalHandle.IsClosed || originalHandle.IsInvalid || !object.ReferenceEquals(originalMutex.SafeWaitHandle, originalHandle) || originalHandle.DangerousGetHandle() != handleValue)
				{
					throw new UnauthorizedAccessException("The original main-thread single-instance capability is no longer current");
				}
			}

			internal void ClaimBootstrap()
			{
				RequireCurrent();
				if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
				{
					throw new InvalidOperationException("The main-instance bootstrap capability has already been claimed");
				}
				RequireCurrent();
			}

			public void Dispose()
			{
				if (Interlocked.Exchange(ref active, 0) != 0)
				{
					Interlocked.CompareExchange(ref mainInstanceBootstrap, null, this);
				}
			}
		}

		internal sealed class RuntimeUiDispatcher : IDisposable
		{
			private readonly Control control = new Control();

			private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;

			private volatile bool closed;

			internal bool IsOwnerThread
			{
				get
				{
					return Thread.CurrentThread.ManagedThreadId == ownerThread;
				}
			}

			internal RuntimeUiDispatcher()
			{
				GC.KeepAlive(control.Handle);
			}

			internal bool TrySend(Action action, int timeoutMs = 5000)
			{
				if (closed)
				{
					return false;
				}
				if (IsOwnerThread)
				{
					action();
					return true;
				}
				object pending = new object();
				bool done = false;
				bool expired = false;
				Exception error = null;
				if (!Post(delegate
				{
					lock (pending)
					{
						if (expired)
						{
							return;
						}
					}
					try
					{
						action();
					}
					catch (Exception ex)
					{
						error = ex;
					}
					finally
					{
						lock (pending)
						{
							done = true;
							Monitor.PulseAll(pending);
						}
					}
				}))
				{
					return false;
				}
				Stopwatch stopwatch = Stopwatch.StartNew();
				lock (pending)
				{
					while (!done)
					{
						int num = (int)Math.Max(0L, timeoutMs - stopwatch.ElapsedMilliseconds);
						if (num == 0)
						{
							expired = true;
							return false;
						}
						Monitor.Wait(pending, num);
					}
				}
				if (error != null)
				{
					throw new InvalidOperationException("UI shutdown dispatch failed", error);
				}
				return true;
			}

			internal bool Post(Action action)
			{
				try
				{
					if (closed || control.IsDisposed || !control.IsHandleCreated)
					{
						return false;
					}
					control.BeginInvoke((MethodInvoker)delegate
					{
						if (!closed && !control.IsDisposed)
						{
							action();
						}
					});
					return true;
				}
				catch
				{
					return false;
				}
			}

			public void Dispose()
			{
				closed = true;
				control.Dispose();
			}
		}

		private const string PendingPanelKey = "ShowPanelOnNextStart";

		private const string PendingPanelTicksKey = "ShowPanelRequestUtcTicks";

		internal const int TrayTipIdleMs = 1500;

		internal const int TrayTipInGameMs = 6000;

		internal const int OsBuildBaseline = 19041;

		internal const int OsBuildBest = 26100;

		private static readonly object mainInstanceBootstrapWitness = new object();

		private static MainInstanceBootstrapLease mainInstanceBootstrap;

		internal static MainInstanceBootstrapLease CurrentMainInstanceBootstrap
		{
			get
			{
				return Volatile.Read(ref mainInstanceBootstrap);
			}
		}

		private static bool TryHandleCriticalThreadGuardian(string[] args)
		{
			bool flag = false;
			if (args != null)
			{
				foreach (string a in args)
				{
					if (string.Equals(a, "--hard-thread-guardian", StringComparison.OrdinalIgnoreCase))
					{
						flag = true;
					}
				}
			}
			if (!flag)
			{
				return false;
			}
			Environment.ExitCode = 13;
			try
			{
				int exitCode;
				if (CriticalThreadGuardian.TryRun(args, Paths.ResolveStartupDataArgument(args), out exitCode))
				{
					Environment.ExitCode = exitCode;
				}
			}
			catch (ArgumentException)
			{
			}
			catch (Exception)
			{
				Environment.ExitCode = 14;
			}
			return true;
		}

		[STAThread]
		private static void Main(string[] args)
		{
            int securityProbeExit;
            if (SecurityStartupProbe.TryRunHelper(args, out securityProbeExit))
            { Environment.ExitCode = securityProbeExit; return; }
			AutostartJobHandoff.Incoming jobHandoff;
			args = AutostartJobHandoff.Strip(args, out jobHandoff);
			if (SelfUpdateInstaller.TryRun(args)) return;
			if (TryHandleCriticalThreadGuardian(args))
			{
				return;
			}
			if (args.Length == 2 && args[0] == "--finish-uninstall")
			{
				Environment.ExitCode = ((!UninstallFinalizer.Run(args[1])) ? 1 : 0);
			}
			else
			{
				if (GameAffinityJobGuard.TryHandleArgs(args) || CoreIsolationWorker.TryHandleArgs(args) || GameExtension.TryHandleArgs(args))
				{
					return;
				}
				if (args.Length > 0 && args[0] == "--uninstall")
				{
					Environment.ExitCode = ((!UninstallMode.Run((args.Length >= 2) ? args[1] : null)) ? 1 : 0);
					return;
				}
				if (args.Length > 0 && args[0] == "--genicon")
				{
					try
					{
						IcoWriter.Save(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Pavise.ico"), new int[8] { 16, 20, 24, 32, 48, 64, 128, 256 });
						return;
					}
					catch
					{
						return;
					}
				}
				if (args.Length >= 2 && args[0] == "--geniconpng")
				{
					PerformancePreset mode = ((args.Length >= 3 && args[2] == "extreme") ? PerformancePreset.Extreme : ((args.Length >= 3 && args[2] == "competitive") ? PerformancePreset.Competitive : ((args.Length >= 3 && args[2] == "handheld") ? PerformancePreset.Handheld : PerformancePreset.Standard)));
					try
					{
						using (Bitmap bitmap = IconArt.Render(256, mode, true))
						{
							bitmap.Save(args[1], ImageFormat.Png);
							return;
						}
					}
					catch
					{
						Environment.ExitCode = 1;
						return;
					}
				}
				if (args.Length >= 2 && args[0] == "--screenshot")
				{
					Dpi.NoFit = true;
					Dpi.Init();
					float result;
					if (args.Length >= 6 && float.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out result) && result >= 1f)
					{
						Dpi.Scale = result;
					}
					Paths.Init();
					Lang.Init();
					if (args.Length >= 4)
					{
						Lang.Cur = ((args[3] == "en") ? 1 : ((args[3] == "ja") ? 2 : 0));
					}
					if (args.Length >= 5 && (args[4] == "light" || args[4] == "backdrop-light"))
					{
						Theme.SetLight(true);
					}
					if (args.Length >= 5 && (args[4] == "backdrop-light" || args[4] == "backdrop-dark"))
					{
						try
						{
							Backdrop.Init();
						}
						catch
						{
						}
					}
					Application.EnableVisualStyles();
					Application.SetCompatibleTextRenderingDefault(false);
					string text = Path.Combine(Path.GetTempPath(), "PaviseShot_" + Process.GetCurrentProcess().Id);
					Directory.CreateDirectory(text);
					Paths.Data = text;
					Logger.LogPath = Path.Combine(text, "Pavise.log");
					int num = ((args.Length >= 3) ? int.Parse(args[2]) : 0);
					SuppressionCore core = new SuppressionCore();
					Tamer t = new Tamer(core);
					try
					{
						GameMode gameMode2 = new GameMode(text, core);
						if (num == 7)
						{
							Logger.Log("CORE 守护服务已开启，等待游戏进程");
							Logger.Log("GAME 已识别 NebulaStrike-Win64-Shipping.exe");
							Logger.Log("POWER 电源计划已生效：PG 专注 5EFC");
							Logger.Log("SUPPRESS 后台资源边界已完成部署");
							Logger.Log("IRQ 警告：检测到慢驱动 rtwlane.sys，单次 DPC 最长 2.1ms");
							Logger.Log("GPU 策略写入失败：驱动接口拒绝访问");
							Logger.Log("SESSION 游戏退出，系统状态已还原");
						}
						if (num == 1)
						{
							string text2 = Path.Combine(text, "NebulaStrike", "Binaries", "Win64");
							Directory.CreateDirectory(text2);
							string text3 = Path.Combine(text2, "NebulaStrike-Win64-Shipping.exe");
							File.Copy(Application.ExecutablePath, text3, true);
							gameMode2.AddGameExecutable("NEBULA STRIKE", text3);
						}
						using (PanelForm panelForm = new PanelForm(t, gameMode2, IconArt.MakeIcon(Dpi.S(24)), true))
						{
							IntPtr handle = panelForm.Handle;
							GC.KeepAlive(handle);
							panelForm.RenderTo(args[1], num, args.Length >= 5 && args[4] == "anticheat", args.Length >= 5 && args[4] == "mode", (args.Length >= 5) ? args[4] : null);
							return;
						}
					}
					finally
					{
						try
						{
							Directory.Delete(text, true);
						}
						catch
						{
						}
					}
				}
				if (args.Length >= 2 && args[0] == "--shot-contact")
				{
					Dpi.Init();
					Paths.Init();
					Lang.Init();
					Application.EnableVisualStyles();
					Application.SetCompatibleTextRenderingDefault(false);
					using (ContactDialog contactDialog = new ContactDialog())
					{
						contactDialog.StartPosition = FormStartPosition.Manual;
						contactDialog.ShowInTaskbar = false;
						contactDialog.Location = new Point(-20000, -20000);
						contactDialog.Show();
						Application.DoEvents();
						using (Bitmap bitmap2 = new Bitmap(contactDialog.ClientSize.Width, contactDialog.ClientSize.Height))
						{
							contactDialog.DrawToBitmap(bitmap2, new Rectangle(0, 0, bitmap2.Width, bitmap2.Height));
							bitmap2.Save(args[1], ImageFormat.Png);
							return;
						}
					}
				}
				if (args.Length > 0 && args[0] == "--ui-preview")
				{
					Dpi.Init();
					Paths.Init();
					Lang.Init();
					Application.EnableVisualStyles();
					Application.SetCompatibleTextRenderingDefault(false);
					Logger.LogPath = Path.Combine(Paths.Data, "Pavise.preview.log");
					SuppressionCore core2 = new SuppressionCore();
					Tamer t2 = new Tamer(core2);
					GameMode gameMode3 = new GameMode(Paths.Data, core2);
					gameMode3.Enabled = Settings.Load("GameModeOn", true);
					using (Icon icon2 = IconArt.MakeMultiIcon(gameMode3.ActivePreset, gameMode3.Enabled))
					{
						using (PanelForm panelForm2 = new PanelForm(t2, gameMode3, icon2, true))
						{
							panelForm2.RealExit = true;
							panelForm2.ShowPanel();
							Application.Run(panelForm2);
							return;
						}
					}
				}
				Dpi.Init();
				try
				{
					Native.SetPreferredAppMode(1);
				}
				catch
				{
				}
				AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
				{
					try
					{
						string path = Paths.Data ?? Path.GetDirectoryName(Application.ExecutablePath);
						Logger.AppendCrash(Path.Combine(path, "crash.log"), string.Concat(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "  ", e.ExceptionObject, Environment.NewLine));
					}
					catch
					{
					}
				};
				if (SelfUpdateInstaller.IsUncoordinatedStartupBlocked(args))
				{
					PaviseDialog.Warn(null, "PAVISE", PerformancePolicy.Text("更新正在进行，请等待更新窗口完成。", "An update is in progress. Please wait for the update window to finish."));
					return;
				}
				if (jobHandoff != null ? !AutostartJobHandoff.Adopt(jobHandoff) : AutostartJobHandoff.TryHandOff(args))
				{
					return;
				}
				bool flag = IsAutostart(args);
				bool createdNew = false;
				Mutex mutex = null;
				try
				{
					mutex = new Mutex(true, "Global\\Pavise_SingleInstance", out createdNew);
				}
				catch
				{
					createdNew = false;
				}
				if (!createdNew && TryReplaceOlderInstance())
				{
					createdNew = RecreateSingleInstanceMutex(ref mutex, "Global\\Pavise_SingleInstance");
				}
				if (createdNew)
				{
					bool elevated = IsElevated();
					bool flag2 = args.Length > 0 && args[0] == "--reset-restart";
					bool updateRestart = args.Length > 0 && (args[0] == "--update-restart" || args[0] == "--update-rollback");
					try
					{
						Paths.Init((flag || flag2 || updateRestart) ? Paths.ResolveStartupDataArgument(args) : null);
					}
					catch (Exception ex)
					{
						Logger.Error("STARTUP " + ex.Message);
						return;
					}
					bool updateStartupGate = SelfUpdateInstaller.IsStartupUpdatePending(args) && SelfUpdateGate.TryBegin();
					bool flag3 = false;
					if (elevated)
					{
						try { SelfUpdateInstaller.RecordLaunch(); }
						catch (Exception error) { Logger.Warn("Update launch identity unavailable: " + error.GetType().Name); }
					}
					bool flag4 = false;
					if (!elevated && TaskHelper.TaskExists())
					{
						string command;
						string arguments;
						flag3 = TaskHelper.TryReadTaskTarget(out command, out arguments) && TaskHelper.IsCurrentTaskTarget(Application.ExecutablePath, Paths.Data, command, arguments);
						flag4 = !flag3;
					}
					if (!elevated && flag3)
					{
						InvalidateMainInstanceBootstrap();
						mutex.ReleaseMutex();
						mutex.Close();
						SetPendingPanelRequest(!flag);
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
							if (ShouldSignalExistingPanel(flag))
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
						if (flag4)
						{
							Logger.Log(Lang.T("log.program.1"));
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
						if (!LaboratoryManualActions.RecoverStartup()) Logger.Warn("Manual laboratory recovery remains pending");
						PowerPlan.HealFromCrash();
						try
						{
							PowerPlan.SweepUntrackedManagedPlansOnce();
						}
						catch (Exception sweepError)
						{
							Logger.Warn("POWER event=orphan-sweep failure=" + sweepError.GetType().Name);
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
							Logger.Warn(Lang.T("schedule.isolation.pending"));
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
						if (!jobAffinityRecovered) Logger.Warn("Game affinity Job recovery pending: " + jobAffinityRecoveryReason);
						if (jobAffinityRecovered)
						{
							int num2 = SuppressionCore.HealFromCrash(Path.Combine(dir, "Pavise.suppression.state"));
							if (num2 > 0) Logger.Log(Lang.T("log.program.2") + num2 + Lang.T("log.program.3"));
						}
						if (jobAffinityRecovered && !new AntiCheatAffinityLease().Recover()) Logger.Warn("AC AFFINITY recovery pending");
						if (jobAffinityRecovered && !HardThreadFileStore.CanonicalRootHasRecoveryDebt(Path.Combine(dir, "CriticalThreadHardAffinityV1")))
						{
							CrashGuard.HealFromCrash();
						}
						else
						{
							Logger.Warn(PerformancePolicy.Text("线程或 Job 亲和性恢复尚未结清，保留进程亲和性恢复记录", "Thread or Job affinity recovery is unresolved; process-affinity recovery records were retained"));
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
								Logger.AppendCrash(Path.Combine(dir, "crash.log"), string.Concat(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "  [UI] ", e.Exception, Environment.NewLine));
							}
							catch
							{
							}
						};
						bool flag5 = ShouldShowStartupPanel(flag, requested, result2, DateTime.UtcNow.Ticks);
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
							bool flag6 = TryResetUserData(dir, delegate
							{
								bool flag9 = tamer.Stop();
								bool flag10 = gameMode.Stop();
								return flag9 && flag10;
							}, out files, out failure);
							PaviseDialog.Error(null, Lang.T("store.savefatal.title"), flag6 ? Lang.F("store.savefatal.body", dir) : Lang.F("store.savefatal.deletefail", dir, failure));
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
						if (flag5)
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

							bool flag8 = true;
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
									flag8 = false;
								}
							}
							catch
							{
								flag8 = false;
							}
							try
							{
								if (!panel.WaitForConfigurationPresetWork(8000)) flag8 = false;
								procNotify.Stop();
							}
							catch
							{
								flag8 = false;
							}


							try
							{
								GameExtension.Shutdown(4000);
							}
							catch
							{
								flag8 = false;
							}
							try
							{
								if (!tamer.Stop())
								{
									flag8 = false;
								}
							}
							catch
							{
								flag8 = false;
							}
							try
							{
								if (!gameMode.Stop() && (Volatile.Read(ref resetStarted) == 0 || !gameMode.ShutdownWritersStopped))
								{
									flag8 = false;
								}
							}
							catch
							{
								if (Volatile.Read(ref resetStarted) == 0 || !gameMode.ShutdownWritersStopped)
								{
									flag8 = false;
								}
							}
							try
							{
								if (bootThread == Thread.CurrentThread || !bootThread.Join(8000))
								{
									flag8 = false;
								}
							}
							catch
							{
								flag8 = false;
							}

							try
							{
								if (!PerformanceTuning.CloseEnvironmentWork(8000))
								{
									flag8 = false;
								}
							}
							catch
							{
								flag8 = false;
							}
							try
							{
								if (!PrivacyControl.CloseWork(8000))
								{
									flag8 = false;
								}
							}
							catch
							{
								flag8 = false;
							}
							Volatile.Write(ref runtimeStopped, flag8 ? 1 : 0);
							return flag8;
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
								catch (Exception cleanupError) { Logger.Warn("Update abort retained recovery state: " + cleanupError.GetType().Name); }
								Logger.Warn("Self-update stopped: " + error.GetType().Name);
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
							// Updating must never turn a failed shutdown/save into deletion of the user's data.
							if (Volatile.Read(ref selfUpdateStopping) != 0 || SelfUpdateGate.IsBusy)
							{
								if (fatal) { Volatile.Write(ref fatalResetPending, 1); Logger.Warn("Data reset deferred during self-update; user data retained"); }
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
						bool flag7;
						try
						{
							icon.Visible = true;
							flag7 = Native.NotificationAreaPresent();
						}
						catch
						{
							flag7 = false;
						}
						if (!flag7)
						{
							if (ShouldShowPanelForMissingTray(flag, flag5))
							{
								Logger.Log(Lang.T("log.program.10"));
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
								Logger.Info(Lang.T("log.program.autostart.traywaiting"));
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
							int num3 = (gameMode.IsActive ? 6000 : 1500);
							if (trayTip.Interval != num3)
							{
								trayTip.Interval = num3;
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
							string text4;
							if (!elevated)
							{
								text4 = Lang.T("tray.noelev");
							}
							else
							{
								string activeGame = gameMode.ActiveGame;
								string text5 = ((activeGame == null) ? gameMode.ArmedGame : null);
								text4 = ((activeGame != null) ? Lang.F("tray.active", activeGame) : ((text5 != null) ? Lang.F("tray.armed", text5) : Lang.T("tray.idle")));
							}
							if (text4.Length > 63)
							{
								text4 = text4.Substring(0, 62) + " ";
							}
							if (icon.Text != text4)
							{
								icon.Text = text4;
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
											Logger.Log(Lang.T("log.program.6") + r.Latest + Lang.T("log.program.7") + App.VersionTag + " ");
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
											Logger.Log(Lang.T("log.program.8") + App.VersionTag + " ");
										}
										else
										{
											Logger.Warn(Lang.T("log.program.9") + r.Error);
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
									if (!SelfUpdateInstaller.AcknowledgeStartup(args)) Logger.Warn("Updated startup could not be committed; recovery files retained");
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
								catch (Exception error) { Logger.Warn("Update startup acknowledgement failed: " + error.GetType().Name); }
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
				if (ShouldSignalExistingPanel(flag))
				{
					SignalShowPanel();
				}
			}
		}

		private static MainInstanceBootstrapLease AcquireMainInstanceBootstrap(Mutex ownedMutex)
		{
			MainInstanceBootstrapLease mainInstanceBootstrapLease = new MainInstanceBootstrapLease(ownedMutex, mainInstanceBootstrapWitness);
			if (Interlocked.CompareExchange(ref mainInstanceBootstrap, mainInstanceBootstrapLease, null) != null)
			{
				mainInstanceBootstrapLease.Dispose();
				throw new InvalidOperationException("The original main-instance capability already exists");
			}
			return mainInstanceBootstrapLease;
		}

		private static void InvalidateMainInstanceBootstrap()
		{
			MainInstanceBootstrapLease mainInstanceBootstrapLease = Volatile.Read(ref mainInstanceBootstrap);
			if (mainInstanceBootstrapLease != null)
			{
				mainInstanceBootstrapLease.Dispose();
			}
		}

		internal static bool IsAutostart(string[] args)
		{
			if (args != null)
			{
				foreach (string a in args)
				{
					if (string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}
			return false;
		}

		internal static bool ShouldSignalExistingPanel(bool autoStarted)
		{
			return !autoStarted;
		}

		internal static bool RecreateSingleInstanceMutex(ref Mutex mutex, string name)
		{
			if (mutex != null)
			{
				mutex.Dispose();
				mutex = null;
			}
			bool createdNew = false;
			try
			{
				mutex = new Mutex(true, name, out createdNew);
			}
			catch
			{
				createdNew = false;
			}
			return createdNew;
		}

		internal static bool ShouldShowStartupPanel(bool autoStarted, bool requested, long requestTicks, long nowTicks)
		{
			if (!autoStarted)
			{
				return true;
			}
			if (requested && requestTicks > 0 && nowTicks >= requestTicks)
			{
				return nowTicks - requestTicks <= 300000000;
			}
			return false;
		}

		internal static bool ShouldShowPanelForMissingTray(bool autoStarted, bool showingPanel)
		{
			if (autoStarted)
			{
				return showingPanel;
			}
			return true;
		}

		private static void SetPendingPanelRequest(bool requested)
		{
			Settings.SaveStr("ShowPanelRequestUtcTicks", requested ? DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) : "");
			Settings.Save("ShowPanelOnNextStart", requested);
		}

		private static void SignalShowPanel()
		{
			try
			{
				using (EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting("Global\\Pavise_ShowPanel"))
				{
					eventWaitHandle.Set();
					return;
				}
			}
			catch
			{
			}
			if (IsElevated())
			{
				return;
			}
			try
			{
				if (TaskHelper.TaskExists())
				{
					SetPendingPanelRequest(true);
					if (TaskHelper.Run("/Run /TN PaviseAuto") != 0)
					{
						SetPendingPanelRequest(false);
					}
				}
			}
			catch
			{
			}
		}

		private static EventWaitHandle CreateSignalEvent(string name)
		{
			try
			{
				EventWaitHandleSecurity eventWaitHandleSecurity = new EventWaitHandleSecurity();
				eventWaitHandleSecurity.AddAccessRule(new EventWaitHandleAccessRule(WindowsIdentity.GetCurrent().User, EventWaitHandleRights.FullControl, AccessControlType.Allow));
				bool createdNew;
				return new EventWaitHandle(false, EventResetMode.AutoReset, name, out createdNew, eventWaitHandleSecurity);
			}
			catch
			{
			}
			try
			{
				return new EventWaitHandle(false, EventResetMode.AutoReset, name);
			}
			catch
			{
				return null;
			}
		}

		internal static int CompareVersions(string left, string right)
		{
			Version result;
			if (!Version.TryParse(NormalizeVersion(left), out result))
			{
				result = new Version(0, 0);
			}
			Version result2;
			if (!Version.TryParse(NormalizeVersion(right), out result2))
			{
				result2 = new Version(0, 0);
			}
			return result.CompareTo(result2);
		}

		private static string NormalizeVersion(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return "0.0.0.0";
			}
			string text = raw.Trim();
			if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
			{
				text = text.Substring(1);
			}
			int num = text.Split('.').Length;
			for (int i = num; i < 4; i++)
			{
				text += ".0";
			}
			return text;
		}

		private static bool TryReplaceOlderInstance()
		{
			Process process = null;
			try
			{
				int id;
				int sessionId;
				using (Process process2 = Process.GetCurrentProcess())
				{
					id = process2.Id;
					sessionId = process2.SessionId;
				}
				Process[] processes = Process.GetProcesses();
				foreach (Process process3 in processes)
				{
					if (process3.Id == id)
					{
						process3.Dispose();
						continue;
					}
					string text = null;
					try
					{
						if (process3.SessionId == sessionId)
						{
							FileVersionInfo fileVersionInfo = process3.MainModule.FileVersionInfo;
							if (IsOlderPaviseInstance(fileVersionInfo.ProductName, fileVersionInfo.CompanyName, fileVersionInfo.FileVersion))
							{
								text = fileVersionInfo.FileVersion;
							}
						}
					}
					catch
					{
					}
					if (text != null && CompareVersions(App.Version, text) > 0 && process == null)
					{
						process = process3;
					}
					else
					{
						process3.Dispose();
					}
				}
				if (process == null)
				{
					return false;
				}
				try
				{
					using (EventWaitHandle eventWaitHandle = EventWaitHandle.OpenExisting("Global\\Pavise_Exit"))
					{
						eventWaitHandle.Set();
					}
				}
				catch
				{
					return false;
				}
				if (!process.WaitForExit(20000))
				{
					return false;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				if (process != null)
				{
					process.Dispose();
				}
			}
		}

		internal static bool IsOlderPaviseInstance(string product, string company, string version)
		{
			Version result;
			if (string.Equals(product, "Pavise Auto", StringComparison.OrdinalIgnoreCase) && string.Equals(company, "bdth", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(version) && Version.TryParse(NormalizeVersion(version), out result))
			{
				return CompareVersions(App.Version, version) > 0;
			}
			return false;
		}

		internal static bool OsBelowBaseline()
		{
			int num = Native.OsBuild();
			if (num > 0)
			{
				return num < 19041;
			}
			return false;
		}

		private static bool BlockIfOsTooOld(string dataDir)
		{
			int num = Native.OsBuild();
			if (!OsBelowBaseline())
			{
				return false;
			}
			Logger.Log(Lang.T("log.program.11") + num);
			int num2 = 0;
			try
			{
				List<string> list = LegacyPurge.RestorePersistent(dataDir);
				num2 = ((list == null) ? 1 : ((list.Count == 0) ? 1 : 2));
			}
			catch
			{
				num2 = 2;
			}
			string text = Lang.F("os.old.body", num, 19041, 26100);
			if (num2 == 2)
			{
				text += Lang.T("os.old.restorefail");
			}
			PaviseDialog.Warn(null, "PAVISE AUTO", text);
			return true;
		}

		private static bool IsElevated()
		{
			try
			{
				using (WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent())
				{
					return new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator);
				}
			}
			catch
			{
				return false;
			}
		}

		internal static bool TryResetUserData(string dir, Func<bool> stopRuntime, out int files, out string failure, Action<string> progress = null)
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
				return LegacyPurge.WipeAll(dir, true, Lang.T("t.panelformsettingspage.3"), out files, out failure, progress);
			}
			catch (Exception ex2)
			{
				failure = Lang.T("wipe.cleanupfail") + " (" + ex2.GetType().Name + ")";
				return false;
			}
		}

		internal static bool StartShutdownWorker(Action work, Action<Exception> failed)
		{
			Action<Exception> report = delegate(Exception error)
			{
				try
				{
					if (failed != null)
					{
						failed(error);
					}
				}
				catch
				{
				}
			};
			try
			{
				Thread thread = new Thread((ThreadStart)delegate
				{
					try
					{
						work();
					}
					catch (Exception obj2)
					{
						report(obj2);
					}
				});
				thread.IsBackground = true;
				thread.SetApartmentState(ApartmentState.STA);
				thread.Start();
				return true;
			}
			catch (Exception obj)
			{
				report(obj);
				return false;
			}
		}
	}
}
