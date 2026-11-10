using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class Program
    {

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
                if (TryRunPreviewCommand(args)) return;
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
                bool isAutostart = IsAutostart(args);
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
                    RunPrimaryInstance(args, mutex, isAutostart);
                    return;
                }
                if (ShouldSignalExistingPanel(isAutostart))
                {
                    SignalShowPanel();
                }
            }
        }
    }
}
