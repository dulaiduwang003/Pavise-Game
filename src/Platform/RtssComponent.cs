using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class RtssComponentInfo
    {
        public bool Installed;
        public bool Running;
        public bool InstalledByPavise;
        public string Path = "";
        public string Version = "";
        public string Error = "";
        public bool RestartRequired;
        public bool InstallationInProgress;
        public bool UninstallationInProgress;
        public bool SupportsOverlay { get { return RtssComponent.SupportsOverlayVersion(Version); } }
        public bool SupportsFontOverride { get { return RtssComponent.SupportsFontOverrideVersion(Version); } }
    }

    internal sealed class RtssInstallProgress
    {
        public string Phase;
        public int Percent;
        public string Detail;
    }

    internal sealed class RtssComponentBackend
    {
        public Func<IEnumerable<string>> Candidates;
        public Func<string, bool> Exists;
        public Func<string, string> Version;
        public Func<string, bool> Running;
        public Func<string, bool> InstallationOwned;
        public Func<string, bool> RecordInstallation;
        public Func<bool> Elevated;
        public Func<bool> InstallerRunning;
        public Action<string, Action<RtssInstallProgress>, CancellationToken> Download;
        public Action<string> Verify;
        public Action<string, string, CancellationToken> Extract;
        public Func<string, int> RunInstaller;
        public Func<bool> UninstallerRunning;
        public Func<IEnumerable<RtssUninstallRegistration>> UninstallRegistrations;
        public Func<string, bool> ValidateUninstaller;
        public Func<string, string, CancellationToken, int> RunUninstaller;
        public Func<string, bool> RequestExit;
    }

    internal sealed class RtssUninstallRegistration
    {
        public string KeyName, DisplayName, Publisher, InstallLocation, UninstallString, DisplayVersion;
    }

    internal sealed class RtssHiddenSetupBackend
    {
        internal Func<string, IntPtr> CreateDesktop;
        internal Func<ProcessStartInfo, string, IntPtr> Start;
        internal Func<IntPtr, int, bool> Wait;
        internal Func<IntPtr, int> ExitCode;
        internal Action<IntPtr> CloseProcess, CloseDesktop;
        internal Action<Action> CompleteLater;
        internal string PromptError;
        internal bool RestartRequired, Uninstall;
        internal RtssSetupState State;
    }

    internal sealed class RtssSetupState
    {
        private readonly object gate = new object();
        private long operation;
        private bool pending, uninstall, restart;
        private string error = "";
        internal long Started(bool removing)
        {
            lock (gate) { uninstall = removing; pending = true; restart = false; error = ""; return ++operation; }
        }
        internal bool Pending(bool removing) { lock (gate) return pending && uninstall == removing; }
        internal void Finished(long token, int? code, string promptError, bool needsRestart)
        {
            lock (gate)
            {
                if (token != operation) return;
                pending = false;
                restart = needsRestart || code == 3010 || code == 1641;
                error = promptError ?? "";
                if (error.Length == 0 && !code.HasValue) error = AutomaticPolicy.Text("RTSS 组件操作已结束，但无法确认结果，请重新检测。", "The RTSS operation has ended, but its result could not be verified. Detect the component again.");
                if (error.Length == 0 && code != 0 && code != 3010 && code != 1641)
                    error = "RTSS " + (uninstall ? "卸载" : "安装") + "未完成，退出码 " + code + "。";
            }
        }
        internal void Apply(RtssComponentInfo result)
        {
            lock (gate)
            {
                result.InstallationInProgress |= pending && !uninstall;
                result.UninstallationInProgress |= pending && uninstall;
                result.RestartRequired |= restart;
                if (pending) result.Error = AutomaticPolicy.Text("RTSS 组件操作仍在进行，请稍后重新检测。", "The RTSS operation is in progress. Detect the component again shortly.");
                else if (error.Length != 0) result.Error = error;
            }
        }
    }

    internal static partial class RtssComponent
    {
        // 安装包哈希与静默参数以 WinGet 的 RTSS 清单为准。
        internal const string DownloadUrl = "https://ftp.nluug.nl/pub/games/PC/guru3d/afterburner/[Guru3D]-RTSSSetup737Build28314.zip";
        internal const string ArchiveSha256 = "9B084A8CB3E53EC1A673894D0B66E22B16C9FD8785636B020B2D422F3F2A820E";
        internal const string InstallerName = "RTSSSetup737.exe";
        internal const string SilentArguments = "/S";
        private const long MaxArchiveBytes = 64L * 1024 * 1024;
        private const long MaxInstallerBytes = 64L * 1024 * 1024;
        private static readonly SemaphoreSlim InstallGate = new SemaphoreSlim(1, 1);
        private static readonly RtssSetupState SetupState = new RtssSetupState();
        internal const string InstallationOwnerKey = "RtssInstallationOwnerV1";

        internal static bool TryRunMaintenance(Func<bool> operation)
        {
            if (operation == null || !InstallGate.Wait(0)) return false;
            try { return !SetupState.Pending(false) && !SetupState.Pending(true) && operation(); }
            finally { InstallGate.Release(); }
        }

        private static RtssComponentBackend NewBackend()
        {
            return new RtssComponentBackend
            {
                Candidates = InstalledCandidates,
                Exists = File.Exists,
                Version = ReadVersion,
                Running = IsRunning,
                InstallationOwned = IsInstallationOwned,
                RecordInstallation = RecordInstallation,
                Elevated = IsElevated,
                InstallerRunning = delegate { return SetupState.Pending(false) || IsInstallerRunning(); },
                Download = DownloadArchive,
                Verify = VerifyArchive,
                Extract = ExtractComponentInstaller,
                RunInstaller = RunSilentInstaller,
                UninstallerRunning = delegate { return SetupState.Pending(true) || IsUninstallerRunning(); },
                UninstallRegistrations = ReadUninstallRegistrations,
                ValidateUninstaller = ValidateRegisteredUninstallerFile,
                RunUninstaller = RunOfficialUninstaller,
                RequestExit = TryCloseInstalledRtss
            };
        }

        // 组件检测只在安装操作时执行。
        public static RtssComponentInfo Detect()
        {
            RtssComponentInfo result = Detect(NewBackend());
            SetupState.Apply(result);
            return result;
        }

        internal static RtssComponentInfo Detect(RtssComponentBackend backend)
        {
            var result = new RtssComponentInfo();
            try
            {
                result.InstallationInProgress = backend.InstallerRunning != null && backend.InstallerRunning();
                result.UninstallationInProgress = backend.UninstallerRunning != null && backend.UninstallerRunning();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string candidate in backend.Candidates())
                {
                    string path = NormalizeCandidate(candidate);
                    if (path.Length == 0 || !seen.Add(path) || !backend.Exists(path)) continue;
                    string version = backend.Version(path);
                    if (String.IsNullOrEmpty(version)) continue;
                    result.Installed = true;
                    result.Path = path;
                    result.Version = version;
                    result.Running = backend.Running(path);
                    try { result.InstalledByPavise = backend.InstallationOwned != null && backend.InstallationOwned(path); }
                    catch { result.InstalledByPavise = false; }
                    if (!result.SupportsOverlay)
                        result.Error = "当前 RTSS 版本不支持所选布局，需要 RTSS 7.3.2 或更新版本。请更新现有组件后重新检测。";
                    return result;
                }
            }
            catch (Exception error) { result.Error = "RTSS 检测失败：" + error.Message; }
            return result;
        }

        internal static bool SupportsOverlayVersion(string value)
        { return SupportsVersion(value, new Version(7, 3, 2)); }

        internal static bool SupportsFontOverrideVersion(string value)
        { return SupportsVersion(value, new Version(7, 3, 7)); }

        private static bool SupportsVersion(string value, Version minimum)
        {
            Version version;
            return !String.IsNullOrWhiteSpace(value)
                && System.Version.TryParse(value.Replace(", ", ".").Replace(',', '.').Trim(), out version)
                && version >= minimum;
        }

        public static RtssComponentInfo Install(Action<RtssInstallProgress> progress, CancellationToken cancellation)
        {
            return Install(NewBackend(), progress, cancellation);
        }

        // 安装器启动后等待完成，取消不能留下半安装状态。
        internal static RtssComponentInfo Install(RtssComponentBackend backend,
            Action<RtssInstallProgress> progress, CancellationToken cancellation)
        {
            if (!InstallGate.Wait(0))
                return new RtssComponentInfo { Error = "RTSS 组件操作正在进行，请等待完成。" };
            string temporary = null;
            bool preserveTemporary = false;
            try
            {
                Report(progress, "Checking", 0, "正在检测 RTSS");
                cancellation.ThrowIfCancellationRequested();
                RtssComponentInfo existing = Detect(backend);
                if (existing.UninstallationInProgress)
                { existing.Error = AutomaticPolicy.Text("RTSS 卸载正在进行，请稍后重新检测。", "RTSS removal is in progress. Detect the component again shortly."); return existing; }
                if (existing.Installed)
                {
                    Report(progress, "Ready", 100, "已检测到现有 RTSS");
                    return existing;
                }
                if (!String.IsNullOrEmpty(existing.Error)) return existing;
                if (backend.InstallerRunning != null && backend.InstallerRunning())
                    return new RtssComponentInfo { InstallationInProgress = true, Error = AutomaticPolicy.Text("RTSS 安装正在进行，请稍后重新检测。", "RTSS installation is in progress. Detect the component again shortly.") };
                if (!backend.Elevated())
                    return new RtssComponentInfo { Error = AutomaticPolicy.Text("安装 RTSS 需要管理员权限；请以管理员身份运行 Pavise 后重试。", "Installing RTSS requires administrator permissions. Run Pavise as administrator and try again.") };

                temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "PaviseRtssInstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                string archive = System.IO.Path.Combine(temporary, "RTSS.zip");
                string installer = System.IO.Path.Combine(temporary, InstallerName);
                backend.Download(archive, progress, cancellation);
                Report(progress, "Verifying", 72, "正在校验安装包");
                cancellation.ThrowIfCancellationRequested();
                backend.Verify(archive);
                backend.Extract(archive, installer, cancellation);
                cancellation.ThrowIfCancellationRequested();
                // 提取 RTSS 后立即删除下载包。
                File.Delete(archive);

                // 下载期间可能已有其他程序完成安装，执行前重新检测。
                existing = Detect(backend);
                if (existing.UninstallationInProgress)
                { existing.Error = AutomaticPolicy.Text("RTSS 卸载正在进行，请稍后重新检测。", "RTSS removal is in progress. Detect the component again shortly."); return existing; }
                if (existing.Installed)
                {
                    Report(progress, "Ready", 100, "已检测到现有 RTSS");
                    return existing;
                }
                if (!String.IsNullOrEmpty(existing.Error)) return existing;
                if (backend.InstallerRunning != null && backend.InstallerRunning())
                    return new RtssComponentInfo { InstallationInProgress = true, Error = AutomaticPolicy.Text("RTSS 安装正在进行，请稍后重新检测。", "RTSS installation is in progress. Detect the component again shortly.") };
                Report(progress, "Installing", 80, AutomaticPolicy.Text("正在安装 RTSS", "Installing RTSS"));
                int exitCode;
                try { exitCode = backend.RunInstaller(installer); }
                catch (InstallerStillRunningException) { preserveTemporary = true; throw; }
                if (exitCode != 0 && exitCode != 3010 && exitCode != 1641)
                    throw new InvalidOperationException("RTSS 安装失败，退出码 " + exitCode + "。");
                Report(progress, "Checking", 95, "正在确认安装结果");
                RtssComponentInfo installed = Detect(backend);
                if (!installed.Installed)
                    throw new InvalidOperationException("安装程序已结束，但未检测到有效的 RTSS 程序。请重新检测。" );
                try { installed.InstalledByPavise = backend.RecordInstallation != null && backend.RecordInstallation(installed.Path); }
                catch { installed.InstalledByPavise = false; }
                installed.RestartRequired = exitCode == 3010 || exitCode == 1641;
                Report(progress, "Ready", 100, installed.RestartRequired ? "安装完成，需要重启系统" : "安装完成");
                return installed;
            }
            catch (OperationCanceledException)
            {
                Report(progress, "Cancelled", 0, "下载已取消");
                return new RtssComponentInfo { Error = "RTSS 下载已取消。" };
            }
            catch (InstallerStillRunningException error)
            {
                Report(progress, "Failed", 0, error.Message);
                return new RtssComponentInfo { InstallationInProgress = true, Error = error.Message };
            }
            catch (Exception error)
            {
                Report(progress, "Failed", 0, error.Message);
                return new RtssComponentInfo { Error = error.Message };
            }
            finally
            {
                if (temporary != null && !preserveTemporary)
                    try { Directory.Delete(temporary, true); } catch { }
                InstallGate.Release();
            }
        }

        public static RtssComponentInfo Uninstall(Action<RtssInstallProgress> progress, CancellationToken cancellation)
        { return Uninstall(NewBackend(), progress, cancellation); }

        // 卸载开始后不强杀卸载器。
        internal static RtssComponentInfo Uninstall(RtssComponentBackend backend,
            Action<RtssInstallProgress> progress, CancellationToken cancellation)
        {
            if (!InstallGate.Wait(0)) return UninstallFailure(backend, "RTSS 组件操作正在进行，请等待完成。", false);
            try
            {
                Report(progress, "Checking", 0, "正在检测 RTSS 卸载信息");
                cancellation.ThrowIfCancellationRequested();
                RtssComponentInfo current = Detect(backend);
                if (current.InstallationInProgress || current.UninstallationInProgress)
                { current.Error = AutomaticPolicy.Text("RTSS 安装或卸载正在进行，请稍后重新检测。", "RTSS installation or removal is in progress. Detect the component again shortly."); return current; }
                if (!String.IsNullOrEmpty(current.Error) && !current.Installed) return current;
                if (!current.Installed) { Report(progress, "Ready", 100, "RTSS 已卸载"); return current; }
                if (!backend.Elevated()) { current.Error = "卸载 RTSS 需要管理员权限；请以管理员身份运行 Pavise 后重试。"; return current; }
                string uninstaller = "";
                if (backend.UninstallRegistrations != null)
                    foreach (RtssUninstallRegistration registration in backend.UninstallRegistrations())
                    {
                        string candidate = RegisteredUninstaller(current.Path, registration);
                        if (candidate.Length != 0 && backend.Exists(candidate)
                            && backend.ValidateUninstaller != null && backend.ValidateUninstaller(candidate))
                        { uninstaller = candidate; break; }
                    }
                if (uninstaller.Length == 0)
                { current.Error = "未找到与当前 RTSS 匹配的官方注册卸载程序，请重新检测。"; return current; }
                cancellation.ThrowIfCancellationRequested();
                if (current.Running)
                {
                    Report(progress, "Stopping", 10, "正在请求 RTSS 正常退出，游戏和其他应用不会被关闭");
                    cancellation.ThrowIfCancellationRequested();
                    if (backend.RequestExit == null || !backend.RequestExit(current.Path))
                        return UninstallFailure(backend, AutomaticPolicy.Text("RTSS 未能正常退出，暂时无法卸载，请稍后重试。", "RTSS could not exit normally. Removal cannot proceed; try again shortly."), false);
                }
                current = Detect(backend);
                if (!current.Installed) { Report(progress, "Ready", 100, "RTSS 已卸载"); return current; }
                if (current.InstallationInProgress || current.UninstallationInProgress)
                { current.Error = "RTSS 状态已改变，请等待组件操作完成后重试。"; return current; }
                if (current.Running)
                    return UninstallFailure(backend, AutomaticPolicy.Text("RTSS 仍在运行，暂时无法卸载，请稍后重试。", "RTSS is still running. Removal cannot proceed; try again shortly."), false);
                if (!String.Equals(uninstaller, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(current.Path), "uninstall.exe"), StringComparison.OrdinalIgnoreCase))
                    return UninstallFailure(backend, "RTSS 安装路径已改变，请重新检测后重试。", false);
                Report(progress, "Uninstalling", 25, AutomaticPolicy.Text("正在卸载 RTSS", "Uninstalling RTSS"));
                cancellation.ThrowIfCancellationRequested();
                if (backend.RunUninstaller == null)
                    return UninstallFailure(backend, AutomaticPolicy.Text("无法启动 RTSS 卸载程序，请重新检测后重试。", "Could not start the RTSS uninstaller. Detect the component again and retry."), false);
                int exitCode = backend.RunUninstaller(uninstaller, System.IO.Path.GetDirectoryName(current.Path), cancellation);
                Report(progress, "Checking", 95, "正在确认卸载结果");
                RtssComponentInfo removed = Detect(backend);
                removed.RestartRequired = exitCode == 3010 || exitCode == 1641;
                if (exitCode == 1)
                { removed.Error = "RTSS 卸载已取消。"; Report(progress, "Cancelled", 0, removed.Error); return removed; }
                if (exitCode != 0 && !removed.RestartRequired)
                { removed.Error = "RTSS 卸载未完成，退出码 " + exitCode + "。"; Report(progress, "Failed", 0, removed.Error); return removed; }
                if (removed.Installed || removed.Running || !String.IsNullOrEmpty(removed.Error))
                { removed.Error = removed.RestartRequired ? "RTSS 卸载需要重启系统后完成。" : "卸载程序已结束，但仍检测到 RTSS，请重新检测。"; Report(progress, "Failed", 0, removed.Error); return removed; }
                Report(progress, "Ready", 100, removed.RestartRequired ? "卸载完成，需要重启系统" : "RTSS 已卸载");
                return removed;
            }
            catch (OperationCanceledException)
            { Report(progress, "Cancelled", 0, "卸载已取消"); return UninstallFailure(backend, "RTSS 卸载已取消。", false); }
            catch (UninstallerStillRunningException error)
            { Report(progress, "Uninstalling", 25, error.Message); return UninstallFailure(backend, error.Message, true); }
            catch (Exception error)
            { Report(progress, "Failed", 0, error.Message); return UninstallFailure(backend, error.Message, false); }
            finally { InstallGate.Release(); }
        }

        private static RtssComponentInfo UninstallFailure(RtssComponentBackend backend, string error, bool pending)
        {
            RtssComponentInfo result = Detect(backend);
            result.Error = error;
            result.UninstallationInProgress |= pending;
            return result;
        }

        internal static string RegisteredUninstaller(string executable, RtssUninstallRegistration registration)
        {
            try
            {
                if (registration == null || !String.Equals(registration.Publisher, "Unwinder", StringComparison.OrdinalIgnoreCase)) return "";
                string expected = NormalizeCandidate(executable);
                if (expected.Length == 0 || expected.StartsWith(@"\\", StringComparison.Ordinal)) return "";
                string directory = System.IO.Path.GetDirectoryName(expected);
                string root = System.IO.Path.GetPathRoot(expected);
                if (root.Length != 3 || root[1] != ':' || String.Equals(directory.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "";
                string commandPath = CommandExecutable(registration.UninstallString);
                if (!System.IO.Path.IsPathRooted(commandPath)) return "";
                commandPath = System.IO.Path.GetFullPath(commandPath);
                if (!String.Equals(commandPath, System.IO.Path.Combine(directory, "uninstall.exe"), StringComparison.OrdinalIgnoreCase)) return "";
                if (!String.IsNullOrWhiteSpace(registration.InstallLocation)
                    && !String.Equals(expected, NormalizeCandidate(registration.InstallLocation), StringComparison.OrdinalIgnoreCase)) return "";
                return RegisteredUninstallerVersion(expected, registration.KeyName, registration.DisplayName,
                    null, registration.UninstallString, registration.DisplayVersion).Length != 0 ? commandPath : "";
            }
            catch { return ""; }
        }

        private static void Report(Action<RtssInstallProgress> progress, string phase, int percent, string detail)
        {
            if (progress == null) return;
            try { progress(new RtssInstallProgress { Phase = phase, Percent = percent, Detail = detail }); }
            catch { }
        }

        // Pavise 安装的 RTSS 才隐藏托盘 已有安装保持用户设置
        internal static bool IsInstallationOwned(string executable)
        {
            try
            {
                string path = NormalizeCandidate(executable);
                string[] receipt = Settings.LoadStr(InstallationOwnerKey, "").Split('\n');
                if (path.Length == 0 || receipt.Length != 3
                    || !String.Equals(path, receipt[0], StringComparison.OrdinalIgnoreCase)) return false;
                string identity = InstallationIdentity(path);
                return identity.Length != 0 && String.Equals(identity, receipt[1] + "\n" + receipt[2], StringComparison.Ordinal);
            }
            catch { return false; }
        }

        internal static bool RecordInstallation(string executable)
        {
            try
            {
                string path = NormalizeCandidate(executable);
                string identity = path.Length == 0 ? "" : InstallationIdentity(path);
                return identity.Length != 0 && Settings.SaveStr(InstallationOwnerKey, path + "\n" + identity);
            }
            catch { return false; }
        }

        private static string InstallationIdentity(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists) return "";
            long creation = file.CreationTimeUtc.Ticks;
            using (var input = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return creation.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"
                    + BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
        }

        internal static string NormalizeCandidate(string candidate)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(candidate)) return "";
                string path = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
                if (!System.IO.Path.IsPathRooted(path)) return "";
                if (!String.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                    path = System.IO.Path.Combine(path, "RTSS.exe");
                if (!String.Equals(System.IO.Path.GetFileName(path), "RTSS.exe", StringComparison.OrdinalIgnoreCase)) return "";
                return System.IO.Path.GetFullPath(path);
            }
            catch { return ""; }
        }

        private static IEnumerable<string> InstalledCandidates()
        {
            var paths = new List<string>();
            RegistryView[] views = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Registry32 };
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (RegistryView view in views)
                {
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                        {
                            using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Unwinder\RTSS", false))
                                if (key != null)
                                    foreach (string name in new[] { "InstallPath", "InstallDir", "InstallLocation", "Path", "" })
                                    {
                                        string value = key.GetValue(name) as string;
                                        if (!String.IsNullOrWhiteSpace(value)) paths.Add(value);
                                    }
                            using (RegistryKey uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false))
                                if (uninstall != null)
                                    foreach (string subkey in uninstall.GetSubKeyNames())
                                    {
                                        using (RegistryKey key = uninstall.OpenSubKey(subkey, false))
                                        {
                                            if (key == null) continue;
                                            string display = key.GetValue("DisplayName") as string;
                                            if (String.IsNullOrEmpty(display)
                                                || (display.IndexOf("RivaTuner Statistics Server", StringComparison.OrdinalIgnoreCase) < 0
                                                    && !String.Equals(display, "RTSS", StringComparison.OrdinalIgnoreCase))) continue;
                                            string location = key.GetValue("InstallLocation") as string;
                                            if (!String.IsNullOrWhiteSpace(location)) paths.Add(location);
                                            string icon = key.GetValue("DisplayIcon") as string;
                                            if (!String.IsNullOrWhiteSpace(icon))
                                            {
                                                int comma = icon.LastIndexOf(',');
                                                if (comma > 0) icon = icon.Substring(0, comma);
                                                paths.Add(icon);
                                            }
                                            string command = key.GetValue("UninstallString") as string;
                                            string exe = CommandExecutable(command);
                                            if (exe.Length > 0) paths.Add(System.IO.Path.GetDirectoryName(exe));
                                        }
                                    }
                        }
                    }
                    catch { }
                }
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (!String.IsNullOrWhiteSpace(root))
                    paths.Add(System.IO.Path.Combine(root, "RivaTuner Statistics Server"));
            try
            {
                foreach (Process process in Process.GetProcessesByName("RTSS"))
                    using (process) try { paths.Add(process.MainModule.FileName); } catch { }
            }
            catch { }
            return paths;
        }

        private static string CommandExecutable(string command)
        {
            if (String.IsNullOrWhiteSpace(command)) return "";
            command = command.Trim();
            if (command[0] == '"')
            {
                int end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : "";
            }
            int suffix = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return suffix >= 0 ? command.Substring(0, suffix + 4) : "";
        }

        private static string ReadVersion(string path)
        {
            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
                if (version.FileMajorPart <= 0) return "";
                string product = (version.ProductName ?? "") + " " + (version.FileDescription ?? "")
                    + " " + (version.OriginalFilename ?? "");
                if (product.IndexOf("RivaTuner", StringComparison.OrdinalIgnoreCase) < 0
                    && product.IndexOf("RTSS", StringComparison.OrdinalIgnoreCase) < 0) return "";
                // RTSS 7.3.7 的文件版本仍为 7.3.5，以匹配的安装注册信息为准。
                string release = ReadRegisteredVersion(path);
                return release.Length > 0 ? release : version.FileVersion ?? "";
            }
            catch { return ""; }
        }

        internal static string RegisteredVersion(string executable, string location, string icon, string displayVersion)
        {
            string expected = NormalizeCandidate(executable);
            if (expected.Length == 0) return "";
            string iconPath = icon ?? "";
            int comma = iconPath.LastIndexOf(',');
            int iconIndex;
            if (comma >= 0 && Int32.TryParse(iconPath.Substring(comma + 1).Trim(), out iconIndex))
                iconPath = iconPath.Substring(0, comma);
            string normalizedIcon = "";
            try
            {
                if (String.Equals(System.IO.Path.GetFileName(iconPath.Trim().Trim('"')), "RTSS.exe", StringComparison.OrdinalIgnoreCase))
                    normalizedIcon = NormalizeCandidate(iconPath);
            }
            catch { }
            if (!String.Equals(expected, NormalizeCandidate(location), StringComparison.OrdinalIgnoreCase)
                && !String.Equals(expected, normalizedIcon, StringComparison.OrdinalIgnoreCase)) return "";
            Version parsed;
            string value = (displayVersion ?? "").Trim();
            return System.Version.TryParse(value, out parsed) && parsed.Major > 0
                ? parsed.ToString() : "";
        }

        internal static string RegisteredUninstallerVersion(string executable, string keyName, string displayName,
            string icon, string command, string displayVersion)
        {
            if (!String.Equals(keyName, "RTSS", StringComparison.OrdinalIgnoreCase)) return "";
            string product = (displayName ?? "").Trim();
            bool matchesProduct = false;
            foreach (string name in new[] { "RTSS", "RivaTuner Statistics Server" })
            {
                if (String.Equals(product, name, StringComparison.OrdinalIgnoreCase)) matchesProduct = true;
                else if (product.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
                {
                    Version release;
                    if (System.Version.TryParse(product.Substring(name.Length + 1).Trim(), out release)
                        && release.Major > 0) matchesProduct = true;
                }
            }
            if (!matchesProduct) return "";
            // NSIS 安装路径为空时，用已验证卸载器的同级路径。
            foreach (string value in new[] { icon, command })
                try
                {
                    string uninstaller = CommandExecutable(value);
                    if (!System.IO.Path.IsPathRooted(uninstaller)
                        || !String.Equals(System.IO.Path.GetFileName(uninstaller), "uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string sibling = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(uninstaller), "RTSS.exe");
                    string release = RegisteredVersion(executable, null, sibling, displayVersion);
                    if (release.Length > 0) return release;
                }
                catch { }
            return "";
        }

        private static string ReadRegisteredVersion(string executable)
        {
            RegistryView[] views = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Registry32 };
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (RegistryView view in views)
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false))
                        {
                            if (uninstall == null) continue;
                            foreach (string name in uninstall.GetSubKeyNames())
                                try
                                {
                                    using (RegistryKey key = uninstall.OpenSubKey(name, false))
                                    {
                                        if (key == null) continue;
                                        string release = RegisteredVersion(executable, key.GetValue("InstallLocation") as string,
                                            key.GetValue("DisplayIcon") as string, key.GetValue("DisplayVersion") as string);
                                        if (release.Length == 0)
                                            release = RegisteredUninstallerVersion(executable, name, key.GetValue("DisplayName") as string,
                                                key.GetValue("DisplayIcon") as string, key.GetValue("UninstallString") as string,
                                                key.GetValue("DisplayVersion") as string);
                                        if (release.Length > 0) return release;
                                    }
                                }
                                catch { }
                        }
                    }
                    catch { }
            return "";
        }

        private static bool IsRunning(string path)
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName("RTSS"))
                    using (process)
                        try
                        {
                            long birth;
                            if (ReadRtssProcessIdentity(process.Id, path, out birth) == 1) return true;
                        }
                        catch { }
            }
            catch { }
            return false;
        }

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool IsInstallerRunning()
        {
            try
            {
                Process[] installers = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(InstallerName));
                bool active = installers.Length > 0;
                foreach (Process process in installers) process.Dispose();
                return active;
            }
            catch { return true; }
        }

        private static bool IsUninstallerRunning()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("PaviseRtssUninstall");
                bool active = processes.Length > 0;
                foreach (Process process in processes) process.Dispose();
                return active;
            }
            catch { return true; }
        }

        private static IEnumerable<RtssUninstallRegistration> ReadUninstallRegistrations()
        {
            var registrations = new List<RtssUninstallRegistration>();
            RegistryView[] views = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 };
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (RegistryView view in views)
                    try
                    {
                        using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS", false))
                            if (key != null) registrations.Add(new RtssUninstallRegistration {
                                KeyName = "RTSS", DisplayName = key.GetValue("DisplayName") as string,
                                Publisher = key.GetValue("Publisher") as string, InstallLocation = key.GetValue("InstallLocation") as string,
                                UninstallString = key.GetValue("UninstallString") as string, DisplayVersion = key.GetValue("DisplayVersion") as string
                            });
                    }
                    catch { }
            return registrations;
        }

        private static bool ValidateRegisteredUninstallerFile(string path)
        {
            try
            {
                // 官方卸载器没有版本和签名，需验证产品登记、同级主程序及 NSIS 格式。
                string fullPath = System.IO.Path.GetFullPath(path);
                if (!String.Equals(System.IO.Path.GetFileName(fullPath), "uninstall.exe", StringComparison.OrdinalIgnoreCase)) return false;
                string current = fullPath;
                while (!String.IsNullOrEmpty(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                    current = System.IO.Path.GetDirectoryName(current);
                }
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 512 || stream.Length > MaxInstallerBytes || reader.ReadUInt16() != 0x5A4D) return false;
                    stream.Position = 0x3C;
                    int pe = reader.ReadInt32();
                    if (pe < 64 || pe > stream.Length - 4) return false;
                    stream.Position = pe;
                    if (reader.ReadUInt32() != 0x00004550) return false;
                    stream.Position = 0;
                    byte[] magic = { 0xEF, 0xBE, 0xAD, 0xDE, 0x4E, 0x75, 0x6C, 0x6C, 0x73, 0x6F, 0x66, 0x74, 0x49, 0x6E, 0x73, 0x74 };
                    int matched = 0, value;
                    while ((value = stream.ReadByte()) >= 0)
                    {
                        if (value == magic[matched]) { if (++matched == magic.Length) return true; }
                        else matched = value == magic[0] ? 1 : 0;
                    }
                }
            }
            catch { }
            return false;
        }

        internal static string DetectRegisteredUninstaller(string executable)
        {
            foreach (RtssUninstallRegistration registration in ReadUninstallRegistrations())
            {
                string path = RegisteredUninstaller(executable, registration);
                if (path.Length != 0 && ValidateRegisteredUninstallerFile(path)) return path;
            }
            return "";
        }

        // NSIS 的 _?= 参数必须放最后且路径不加引号；安装窗口隔离到私有桌面。
        internal static ProcessStartInfo UninstallerStartInfo(string copy, string installationDirectory)
        {
            if (String.IsNullOrWhiteSpace(installationDirectory) || !System.IO.Path.IsPathRooted(installationDirectory))
                throw new InvalidOperationException("RTSS 卸载路径无效。");
            string directory = System.IO.Path.GetFullPath(installationDirectory).TrimEnd('\\');
            if (directory.Length <= 3 || directory.StartsWith(@"\\", StringComparison.Ordinal)
                || directory.IndexOf('"') >= 0 || directory.IndexOf('\r') >= 0 || directory.IndexOf('\n') >= 0)
                throw new InvalidOperationException("RTSS 卸载路径无效。");
            return new ProcessStartInfo {
                FileName = copy, Arguments = SilentArguments + " _?=" + directory, WorkingDirectory = directory,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, ErrorDialog = false
            };
        }

        internal sealed class UninstallerStillRunningException : InvalidOperationException
        {
            public UninstallerStillRunningException(string detail = null) : base(detail ?? AutomaticPolicy.Text("RTSS 卸载仍在进行，请稍后重新检测，暂勿重复卸载。", "RTSS removal is still in progress. Detect the component again shortly; do not start another removal.")) { }
        }

        private static int RunOfficialUninstaller(string registeredUninstaller, string installationDirectory, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ValidateRegisteredUninstallerFile(registeredUninstaller)) throw new InvalidOperationException("RTSS 卸载程序校验失败，请重新检测。");
            string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PaviseRtssUninstall-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            bool pending = false;
            try
            {
                string copy = System.IO.Path.Combine(temporary, "PaviseRtssUninstall.exe");
                using (var input = new FileStream(registeredUninstaller, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                cancellation.ThrowIfCancellationRequested();
                try { return RunHiddenSetup(UninstallerStartInfo(copy, installationDirectory), NewHiddenSetupBackend(), true,
                    delegate { CleanupUninstallerCopy(temporary); }); }
                catch (UninstallerStillRunningException) { pending = true; throw; }
            }
            finally
            {
                if (!pending)
                {
                    CleanupUninstallerCopy(temporary);
                }
            }
        }

        private static void CleanupUninstallerCopy(string directory)
        {
            try { File.Delete(System.IO.Path.Combine(directory, "PaviseRtssUninstall.exe")); } catch { }
            try { Directory.Delete(directory, false); } catch { }
        }

        private static bool TryCloseInstalledRtss(string executable)
        {
            string expected = NormalizeCandidate(executable);
            if (expected.Length == 0) return false;
            var targets = new Dictionary<int, long>();
            try
            {
                foreach (Process process in Process.GetProcessesByName("RTSS"))
                    using (process)
                    {
                        long birth;
                        int identity = ReadRtssProcessIdentity(process.Id, expected, out birth);
                        if (identity < 0) return false;
                        if (identity > 0) targets[process.Id] = birth;
                    }
                if (targets.Count == 0) return true;
                var notified = new HashSet<int>();
                bool failed = false;
                bool complete = EnumComponentWindows(delegate(IntPtr hwnd, IntPtr ignored) {
                    uint pid;
                    GetComponentWindowProcessId(hwnd, out pid);
                    long original;
                    if (!targets.TryGetValue((int)pid, out original)) return true;
                    var title = new StringBuilder(256);
                    var className = new StringBuilder(256);
                    GetComponentWindowText(hwnd, title, title.Capacity);
                    GetComponentClassName(hwnd, className, className.Capacity);
                    // RTSS 7.3.7 的退出菜单命令为 1001。
                    if (!String.Equals(title.ToString(), "RivaTunerStatisticsServer", StringComparison.Ordinal)
                        || !className.ToString().StartsWith("Afx:", StringComparison.Ordinal)
                        || className.ToString().IndexOf(":20:", StringComparison.Ordinal) < 0) return true;
                    long birth;
                    if (ReadRtssProcessIdentity((int)pid, expected, out birth) != 1 || birth != original) { failed = true; return false; }
                    if (!PostComponentMessage(hwnd, 0x0111, new IntPtr(1001), IntPtr.Zero)) { failed = true; return false; }
                    notified.Add((int)pid);
                    return true;
                }, IntPtr.Zero);
                if (!complete || failed || notified.Count != targets.Count) return false;
                Stopwatch wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 5000)
                {
                    bool active = false;
                    foreach (KeyValuePair<int, long> target in targets)
                    {
                        long birth;
                        int identity = ReadRtssProcessIdentity(target.Key, expected, out birth);
                        if (identity < 0) return false;
                        if (identity == 1 && birth == target.Value) active = true;
                    }
                    if (!active) return true;
                    Thread.Sleep(100);
                }
            }
            catch { }
            return false; // 组件管理不强制结束 RTSS 或游戏。
        }

        internal static bool RequestOwnedRtssExit(int expectedPid, long expectedBirth, string executable)
        {
            long birth;
            if (expectedPid <= 4 || expectedBirth <= 0
                || ReadRtssProcessIdentity(expectedPid, executable, out birth) != 1 || birth != expectedBirth) return false;
            bool sent = false;
            EnumComponentWindows(delegate(IntPtr hwnd, IntPtr ignored) {
                uint pid; GetComponentWindowProcessId(hwnd, out pid);
                if (pid != expectedPid) return true;
                var title = new StringBuilder(256); var className = new StringBuilder(256);
                GetComponentWindowText(hwnd, title, title.Capacity); GetComponentClassName(hwnd, className, className.Capacity);
                if (title.ToString() != "RivaTunerStatisticsServer" || !className.ToString().StartsWith("Afx:", StringComparison.Ordinal)
                    || className.ToString().IndexOf(":20:", StringComparison.Ordinal) < 0) return true;
                long currentBirth;
                if (ReadRtssProcessIdentity(expectedPid, executable, out currentBirth) == 1 && currentBirth == expectedBirth)
                    sent |= PostComponentMessage(hwnd, 0x0111, new IntPtr(1001), IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            return sent;
        }

        private static int ReadRtssProcessIdentity(int pid, string expected, out long birth)
        {
            birth = 0;
            IntPtr handle = OpenComponentProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) return Marshal.GetLastWin32Error() == 87 ? 0 : -1;
            try
            {
                long exit, kernel, user;
                if (!GetComponentProcessTimes(handle, out birth, out exit, out kernel, out user)) return -1;
                if (birth <= 0 || exit != 0) return 0;
                var path = new StringBuilder(32768);
                int size = path.Capacity;
                if (!QueryComponentProcessPath(handle, 0, path, ref size)) return -1;
                return String.Equals(System.IO.Path.GetFullPath(path.ToString()), expected, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }
            finally { CloseComponentHandle(handle); }
        }

        private delegate bool ComponentWindowCallback(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "EnumWindows")] private static extern bool EnumComponentWindows(ComponentWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint GetComponentWindowProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int GetComponentWindowText(IntPtr hwnd, StringBuilder text, int maximum);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetComponentClassName(IntPtr hwnd, StringBuilder text, int maximum);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] private static extern bool PostComponentMessage(IntPtr hwnd, uint message, IntPtr first, IntPtr second);
        [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)] private static extern IntPtr OpenComponentProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryComponentProcessPath(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", EntryPoint = "GetProcessTimes")] private static extern bool GetComponentProcessTimes(IntPtr process, out long birth, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] private static extern bool CloseComponentHandle(IntPtr handle);

        private static void DownloadFile(string url, string destination, string detail,
            Action<RtssInstallProgress> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Pavise-RTSS-Component/1.0";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 15000;
            request.AllowAutoRedirect = false;
            var clock = Stopwatch.StartNew();
            using (var abort = cancellation.Register(delegate { try { request.Abort(); } catch { } }))
            using (var timeout = new System.Threading.Timer(delegate { try { request.Abort(); } catch { } }, null, 180000, Timeout.Infinite))
            {
                try
                {
                    for (int redirects = 0; ; redirects++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (clock.ElapsedMilliseconds >= 180000)
                            throw new TimeoutException("RTSS 下载超时，请重试。");
                        using (var response = (HttpWebResponse)request.GetResponse())
                        {
                            int status = (int)response.StatusCode;
                            if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                            {
                                if (redirects >= 3) throw new InvalidDataException("RTSS 下载跳转次数过多。");
                                Uri next = ResolveDownloadRedirect(request.RequestUri, response.Headers[HttpResponseHeader.Location]);
                                request = (HttpWebRequest)WebRequest.Create(next);
                                request.UserAgent = "Pavise-RTSS-Component/1.0";
                                request.Timeout = 30000;
                                request.ReadWriteTimeout = 15000;
                                request.AllowAutoRedirect = false;
                                continue;
                            }
                            if (response.StatusCode != HttpStatusCode.OK)
                                throw new InvalidOperationException("RTSS 下载失败：服务器未返回安装包。");
                            if (response.ContentLength > MaxArchiveBytes)
                                throw new InvalidOperationException("RTSS 安装包超过下载大小限制。");
                            using (Stream input = response.GetResponseStream())
                            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[65536];
                                long total = 0, lastReport = -500;
                                int count;
                                Report(progress, "Downloading", 5, detail);
                                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    if (clock.ElapsedMilliseconds > 180000)
                                        throw new TimeoutException("RTSS 下载超时，请重试。");
                                    total += count;
                                    if (total > MaxArchiveBytes)
                                        throw new InvalidOperationException("RTSS 安装包超过下载大小限制。");
                                    output.Write(buffer, 0, count);
                                    if (clock.ElapsedMilliseconds - lastReport >= 250)
                                    {
                                        int percent = response.ContentLength > 0
                                            ? 5 + (int)Math.Min(65, total * 65 / response.ContentLength) : 5;
                                        Report(progress, "Downloading", percent, detail);
                                        lastReport = clock.ElapsedMilliseconds;
                                    }
                                }
                                if (total == 0 || (response.ContentLength >= 0 && total != response.ContentLength))
                                    throw new InvalidOperationException("RTSS 安装包下载不完整。");
                            }
                            return;
                        }
                    }
                }
                catch (WebException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (clock.ElapsedMilliseconds >= 180000)
                        throw new TimeoutException("RTSS 下载超时，请重试。");
                    throw;
                }
            }
        }

        internal static void VerifyArchive(string path)
        {
            VerifiedArchiveIsMsi(path);
        }

        // 只解压指定安装器，拒绝 ZIP 中的任意路径。
        internal static void ExtractInstaller(string archive, string destination, CancellationToken cancellation)
        { ExtractZipEntry(archive, destination, InstallerName, cancellation); }

        private static void ExtractZipEntry(string archive, string destination, string expectedName, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var input = File.OpenRead(archive))
            using (var reader = new BinaryReader(input, Encoding.UTF8))
            {
                if (input.Length < 22 || input.Length > MaxArchiveBytes)
                    throw new InvalidDataException("RTSS ZIP 安装包无效。");
                int tailSize = (int)Math.Min(input.Length, 65557);
                input.Position = input.Length - tailSize;
                byte[] tail = reader.ReadBytes(tailSize);
                int end = -1;
                for (int i = tail.Length - 22; i >= 0; i--)
                    if (BitConverter.ToUInt32(tail, i) == 0x06054b50
                        && i + 22 + BitConverter.ToUInt16(tail, i + 20) == tail.Length)
                    { end = i; break; }
                if (end < 0 || BitConverter.ToUInt16(tail, end + 4) != 0
                    || BitConverter.ToUInt16(tail, end + 6) != 0)
                    throw new InvalidDataException("RTSS ZIP 目录无效。");
                int entries = BitConverter.ToUInt16(tail, end + 10);
                long directorySize = BitConverter.ToUInt32(tail, end + 12);
                long directory = BitConverter.ToUInt32(tail, end + 16);
                if (entries <= 0 || entries > 1024 || directorySize < 46
                    || directory + directorySize > input.Length - tailSize + end)
                    throw new InvalidDataException("RTSS ZIP 目录超出边界。");
                input.Position = directory;
                long compressed = 0, expanded = 0, local = 0;
                ushort method = 0, flags = 0;
                int found = 0;
                for (int i = 0; i < entries; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (input.Position + 46 > directory + directorySize || reader.ReadUInt32() != 0x02014b50)
                        throw new InvalidDataException("RTSS ZIP 文件目录无效。");
                    reader.ReadUInt16(); reader.ReadUInt16();
                    ushort entryFlags = reader.ReadUInt16(), entryMethod = reader.ReadUInt16();
                    reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32();
                    uint packedSize = reader.ReadUInt32(), unpackedSize = reader.ReadUInt32();
                    int nameLength = reader.ReadUInt16(), extraLength = reader.ReadUInt16(), commentLength = reader.ReadUInt16();
                    reader.ReadUInt16(); reader.ReadUInt16(); reader.ReadUInt32();
                    uint offset = reader.ReadUInt32();
                    if (input.Position + nameLength + extraLength + commentLength > directory + directorySize)
                        throw new InvalidDataException("RTSS ZIP 文件名超出边界。");
                    string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
                    input.Position += extraLength + commentLength;
                    if (!String.Equals(name, expectedName, StringComparison.Ordinal)) continue;
                    found++;
                    compressed = packedSize; expanded = unpackedSize; local = offset;
                    method = entryMethod; flags = entryFlags;
                }
                if (found != 1 || (flags & 1) != 0 || (method != 0 && method != 8)
                    || compressed <= 0 || compressed > MaxArchiveBytes || expanded <= 0 || expanded > MaxInstallerBytes
                    || local + 30 > directory)
                    throw new InvalidDataException("RTSS ZIP 未包含有效的指定安装程序。");
                input.Position = local;
                if (reader.ReadUInt32() != 0x04034b50)
                    throw new InvalidDataException("RTSS ZIP 本地文件头无效。");
                reader.ReadBytes(22);
                int localNameLength = reader.ReadUInt16(), localExtraLength = reader.ReadUInt16();
                if (input.Position + localNameLength + localExtraLength + compressed > directory)
                    throw new InvalidDataException("RTSS ZIP 安装程序超出边界。");
                string localName = Encoding.UTF8.GetString(reader.ReadBytes(localNameLength));
                if (localName != expectedName)
                    throw new InvalidDataException("RTSS ZIP 安装程序名称不一致。");
                input.Position += localExtraLength;
                using (var limited = new LimitedReadStream(input, compressed))
                using (Stream decoded = method == 8 ? (Stream)new DeflateStream(limited, CompressionMode.Decompress) : limited)
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536];
                    long written = 0;
                    int count;
                    while ((count = decoded.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        written += count;
                        if (written > expanded) throw new InvalidDataException("RTSS ZIP 解压大小无效。");
                        output.Write(buffer, 0, count);
                    }
                    if (written != expanded) throw new InvalidDataException("RTSS 安装程序不完整。");
                }
            }
        }

        internal static ProcessStartInfo InstallerStartInfo(string installer)
        {
            return new ProcessStartInfo
            {
                FileName = installer,
                Arguments = SilentArguments,
                WorkingDirectory = System.IO.Path.GetDirectoryName(installer),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false
            };
        }

        private sealed class InstallerStillRunningException : InvalidOperationException
        {
            public InstallerStillRunningException(string detail = null) : base(detail ?? AutomaticPolicy.Text("RTSS 安装仍在进行，请稍后重新检测，暂勿重复安装。", "RTSS installation is still in progress. Detect the component again shortly; do not start another installation.")) { }
        }

        private static int RunSilentInstaller(string installer)
        {
            // 已有管理员权限，避免 runas 再次弹出 UAC。
            return RunHiddenSetup(InstallerStartInfo(installer), NewHiddenSetupBackend(), false, delegate {
                // 安装超时后保留仍被安装器使用的临时文件。
                string directory = System.IO.Path.GetDirectoryName(installer);
                try { File.Delete(installer); } catch { }
                try { File.Delete(System.IO.Path.Combine(directory, "RTSS.zip")); } catch { }
                try { Directory.Delete(directory, false); } catch { }
            }, delegate {
                RtssComponentInfo installed = Detect(NewBackend());
                if (installed.Installed) RecordInstallation(installed.Path);
            });
        }

        private static RtssHiddenSetupBackend NewHiddenSetupBackend(bool trackState = true)
        {
            IntPtr ownedDesktop = IntPtr.Zero;
            var backend = new RtssHiddenSetupBackend();
            backend.State = trackState ? SetupState : new RtssSetupState();
            backend.CreateDesktop = delegate(string name) {
                    IntPtr desktop = CreateSetupDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x0087, IntPtr.Zero);
                    if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), AutomaticPolicy.Text("无法准备 RTSS 组件操作，请重试。", "Could not prepare the RTSS component operation. Try again."));
                    ownedDesktop = desktop; return desktop;
                };
            backend.Start = StartHiddenSetup;
            backend.Wait = delegate(IntPtr process, int milliseconds) { return WaitHiddenSetup(process, ownedDesktop, milliseconds, backend); };
            backend.ExitCode = delegate(IntPtr process) {
                    uint code;
                    if (!GetSetupExitCode(process, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    return unchecked((int)code);
                };
            backend.CloseProcess = delegate(IntPtr process) { CloseComponentHandle(process); };
            backend.CloseDesktop = delegate(IntPtr desktop) { CloseSetupDesktop(desktop); };
            backend.CompleteLater = delegate(Action finish) {
                    var worker = new Thread(delegate() { finish(); });
                    worker.IsBackground = true; worker.Name = "Pavise RTSS setup completion"; worker.Start();
                };
            return backend;
        }

#if PAVISE_SELFTEST
        internal static int RunNativeHiddenSetupFixture(ProcessStartInfo start, bool uninstall, Action completedAfterTimeout = null)
        { return RunHiddenSetup(start, NewHiddenSetupBackend(false), uninstall, completedAfterTimeout); }
#endif

        internal static int RunHiddenSetup(ProcessStartInfo start, RtssHiddenSetupBackend backend, bool uninstall,
            Action completedAfterTimeout, Action verifiedInstallationAfterTimeout = null)
        {
            backend.Uninstall = uninstall;
            string desktopName = "PaviseRtssSetup-" + Guid.NewGuid().ToString("N");
            IntPtr desktop = IntPtr.Zero, process = IntPtr.Zero;
            bool pending = false;
            long operation = 0;
            try
            {
                desktop = backend.CreateDesktop(desktopName);
                if (desktop == IntPtr.Zero) throw new InvalidOperationException(AutomaticPolicy.Text("无法准备 RTSS 组件操作，请重试。", "Could not prepare the RTSS component operation. Try again."));
                process = backend.Start(start, desktopName);
                if (process == IntPtr.Zero) throw new InvalidOperationException(AutomaticPolicy.Text("无法启动 RTSS 组件操作。", "Could not start the RTSS component operation."));
                if (backend.State != null) operation = backend.State.Started(uninstall);
                bool exited = false;
                string waitError = "";
                try { exited = backend.Wait(process, 240000); }
                catch { waitError = AutomaticPolicy.Text("暂时无法确认 RTSS 组件操作状态，请稍后重新检测。", "Could not verify the RTSS operation status. Detect the component again shortly."); }
                if (exited)
                {
                    int code = FinishHiddenSetup(backend, process, operation);
                    if (!String.IsNullOrEmpty(backend.PromptError)) throw new InvalidOperationException(backend.PromptError);
                    return code == 0 && backend.RestartRequired ? 3010 : code;
                }
                IntPtr retainedProcess = process, retainedDesktop = desktop;
                pending = true;
                try { backend.CompleteLater(delegate {
                    // 等待结果不明时保留安装桌面和输入文件。
                    bool completed = false;
                    try { completed = backend.Wait(retainedProcess, -1); } catch { }
                    if (completed) {
                        try {
                            int code = FinishHiddenSetup(backend, retainedProcess, operation);
                            if (!uninstall && (code == 0 || code == 3010 || code == 1641)
                                && String.IsNullOrEmpty(backend.PromptError) && verifiedInstallationAfterTimeout != null)
                                verifiedInstallationAfterTimeout();
                        } catch { }
                        try { backend.CloseProcess(retainedProcess); } catch { }
                        try { backend.CloseDesktop(retainedDesktop); } catch { }
                        if (completedAfterTimeout != null) try { completedAfterTimeout(); } catch { }
                    }
                }); } catch { }
                string detail = String.IsNullOrEmpty(backend.PromptError) ? (waitError.Length == 0 ? null : waitError)
                    : backend.PromptError + AutomaticPolicy.Text(" 操作仍未结束，请稍后重新检测。", " The operation has not ended. Detect the component again shortly.");
                if (uninstall) throw new UninstallerStillRunningException(detail);
                throw new InstallerStillRunningException(detail);
            }
            finally
            {
                if (!pending)
                {
                    if (process != IntPtr.Zero) try { backend.CloseProcess(process); } catch { }
                    if (desktop != IntPtr.Zero) try { backend.CloseDesktop(desktop); } catch { }
                }
            }
        }

        private static int FinishHiddenSetup(RtssHiddenSetupBackend backend, IntPtr process, long operation)
        {
            int? code = null;
            try { code = backend.ExitCode(process); return code.Value; }
            finally
            {
                if (backend.State != null)
                    backend.State.Finished(operation, code, backend.PromptError, backend.RestartRequired);
            }
        }

        private static bool WaitHiddenSetup(IntPtr process, IntPtr desktop, int milliseconds, RtssHiddenSetupBackend backend)
        {
            uint pid = GetSetupProcessId(process);
            if (pid == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Stopwatch elapsed = Stopwatch.StartNew();
            do
            {
                uint result = WaitForSetupProcess(process, 100);
                if (result == 0) return true;
                if (result != 0x102) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (String.IsNullOrEmpty(backend.PromptError)) RespondToSetupDialogs(desktop, pid, backend);
                if (milliseconds >= 0 && !String.IsNullOrEmpty(backend.PromptError))
                {
                    return WaitForSetupProcess(process, 2000) == 0;
                }
            } while (milliseconds < 0 || elapsed.ElapsedMilliseconds < milliseconds);
            return false;
        }

        private static void RespondToSetupDialogs(IntPtr desktop, uint processId, RtssHiddenSetupBackend backend)
        {
            EnumSetupDesktopWindows(desktop, delegate(IntPtr dialog, IntPtr ignored) {
                uint owner;
                GetComponentWindowProcessId(dialog, out owner);
                if (owner != processId || !String.Equals(ComponentWindowClass(dialog), "#32770", StringComparison.Ordinal)
                    || !IsSetupWindowVisible(dialog)) return true;
                string caption = ComponentWindowText(dialog);
                var body = new StringBuilder();
                EnumSetupChildWindows(dialog, delegate(IntPtr child, IntPtr unused) {
                    if (String.Equals(ComponentWindowClass(child), "Static", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = ComponentWindowText(child);
                        if (text.Length != 0) { if (body.Length != 0) body.Append('\n'); body.Append(text); }
                    }
                    return true;
                }, IntPtr.Zero);
                int answer = caption.IndexOf("RivaTuner Statistics Server", StringComparison.Ordinal) >= 0
                    ? KnownSetupPromptAction(body.ToString(), backend.Uninstall) : 0;
                int buttonId = answer == 107 ? 7 : answer == 201 ? 1 : answer;
                IntPtr button = buttonId == 0 ? IntPtr.Zero : GetSetupDialogItem(dialog, buttonId);
                if (button == IntPtr.Zero || !String.Equals(ComponentWindowClass(button), "Button", StringComparison.OrdinalIgnoreCase)
                    || !IsSetupWindowEnabled(button) || !IsSetupWindowVisible(button))
                {
                    backend.PromptError = AutomaticPolicy.Text("RTSS 组件操作遇到未识别的提示，已请求中止，请重新检测。", "An unrecognized RTSS prompt prevented completion. Detect the component again.");
                    PostComponentMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); // 未知提示只发送关闭命令，不猜测确认按钮。
                    return true;
                }
                // 只响应当前安装进程私有桌面上的已核对对话框。
                GetComponentWindowProcessId(dialog, out owner);
                if (owner != processId) return true;
                if (answer == 107) backend.RestartRequired = true; // 拒绝立即重启。
                if (answer == 201) backend.PromptError = "RTSS 上次组件操作尚需重启系统，请重启后重试。";
                else if (answer == 1) backend.PromptError = AutomaticPolicy.Text("RTSS 仍在运行，组件操作已中止，请稍后重试。", "RTSS is still running. The component operation was stopped; try again shortly.");
                else if (answer == 7) backend.PromptError = "已检测到现有 RTSS，已取消安装以保留现有版本与配置。";
                if (!PostComponentMessage(dialog, 0x0111, new IntPtr(buttonId), button))
                    backend.PromptError = AutomaticPolicy.Text("无法完成 RTSS 确认，操作未完成，请稍后重新检测。", "Could not confirm the RTSS operation. Detect the component again shortly.");
                return true;
            }, IntPtr.Zero);
        }

        private static string ComponentWindowText(IntPtr window)
        {
            var value = new StringBuilder(4096);
            GetComponentWindowText(window, value, value.Capacity);
            return value.ToString();
        }
        private static string ComponentWindowClass(IntPtr window)
        {
            var value = new StringBuilder(256);
            GetComponentClassName(window, value, value.Capacity);
            return value.ToString();
        }

        // 提示文本使用固定版本的精确哈希匹配。
        internal static int KnownSetupPromptAction(string text, bool uninstall)
        {
            if (String.IsNullOrWhiteSpace(text) || text.Length > 4096) return 0;
            string digest;
            using (SHA256 hash = SHA256.Create())
                digest = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.Trim().Replace("\r\n", "\n")))).Replace("-", "");
            int answer = KnownSetupPromptDigest(digest);
            if (answer == 6 && !uninstall) return 0;
            if (answer == 7 && uninstall) return 0;
            return answer;
        }

        private static int KnownSetupPromptDigest(string digest)
        {
            switch (digest)
            {
                case "07BE9A287D3FE76150B3C9EECDD0ED36831C2D028DC2AAACC87EEF1948675C29": return 1;
                case "0BB66C59CC025A0E22EA6F27C087CDC2F4426CE203C05822760727CFD9555216": return 1;
                case "3F6EC35EC2900F81FA09863FC65CFA3B54143F33FB7762F5BD5D1ED80180FA56": return 1;
                case "417D7E3E5061D7D003F5B7EA6235A6D47DA7274A474695905A2093E2A5238E5E": return 1;
                case "49674DEC9CC3084BF59CE06D320ECC9622D2D61F974DBBCD38FA59B2A3909AF3": return 1;
                case "92591BF4572814D0826F714FC3DE71E0BCCF557A4CCB415D367D5C12FB2187EA": return 1;
                case "C4D50F7ED509BFA35D48F601B7F681E26029A791EA120EB9117C9041FB9D7076": return 1;
                case "CBBC234A9B23698497224E2212BA94DB29558376B5328201A271F4D664407D71": return 1;
                case "D46114B22A62C2C8331852776E44780E2D130904827193A9046E9BE824BBEF13": return 1;
                case "DC4D5FB90172154DD2BE60F6EE8E23290DC5498F81C5A7EB7C8808510328B4D1": return 1;
                case "E13AFB0A34F3513ED140958280928842D1CF8E00998A2EE41D641B079CEEB883": return 1;
                case "32FDE753CE6BACCAC1181BFC429B94C6A2F755DB65235A259F7E8B42A2B090C8": return 6;
                case "82E76A6B210B5E929928FF7FD5EA45C0E8FA9FAE2C15461F5481CC9082DF2BDD": return 6;
                case "8672F46B848E21DE0CA1610A1F3B277F5801C5D9B2ADEFE0CFBF4A61191EF630": return 6;
                case "86C9FEE809B085777A5E2C55F625DDF84468E7676707C693F6CEBE91F3586D42": return 6;
                case "A4C5059D5F4EC5480DC5A405E293560016DD62A53141AFA5B33F2CBF6D78C8AF": return 6;
                case "C26E2D78B00AE4D03096AF72A05C7ACD03094B12445DCD190A4E8E32561F2C35": return 6;
                case "334BAA0D53F36D75E55F94FA78895E2F9C9663D0CEA2F7D4B9C1C08D1411E90B": return 7;
                case "3BBA6734109596B3AF6E619F483B59BFDF5272A13F83708D21C2B15DBAB4A1CC": return 7;
                case "44B7ED9B3688B49D0D5AF97EA851BD06B786840D601323730D5B95E07F157E50": return 7;
                case "6E8E9884283051C9F9D77F9F14639928704485D1E98464EE44C4B526C24653E5": return 7;
                case "8A88A14DCDE05DED1D5AC02A6E925AFE79617A57AE2FE07DA3B4405ACC141D8A": return 7;
                case "8DA4A3DD3871EF614D23A9E2D97999ED7682CA231E4431E5748D7A73B736D58B": return 7;
                case "9BE7FB66BF61B1DB8F55671FD691D4280659E1635BCC1AD8706F8FB60B12C763": return 7;
                case "B736A2D865791BA84F4B3E2CC0B8EDFCC81648255CA0A623E5870CCE4E9D1519": return 7;
                case "C7878D372EED03537687505BC83632E371E6F5C29F5D50FC4F6148F4D44ADD2B": return 7;
                case "F44C95578718F5D40CEB5959AD22544958277EE1542F69F598DDDF484E72400B": return 7;
                case "1AF2746333164BD37AB9D0FE790C5B8804B9C53F5704C1E44FCDABEC396973BD": return 107;
                case "31A2A27D19DEE086B613E84C551ADA09B4E3F44F057C8570BC4DF880880C6C71": return 107;
                case "51DE3D723C19A1E861B722052F8725673034B0AAE22587868A45A69841769BA8": return 107;
                case "763EEBAF96C972B507D299212FD5465AAE937B3766785E00997939C152F4A427": return 107;
                case "8E9E6F7417900592B25AA8F5A7D0599BB8C55BC093DC279417012B0873C5E1EE": return 107;
                case "A2601509E3155B97B802D997FCE1873CB8A3EFC499228277FADED5B8959711F9": return 107;
                case "A5F3AFA561591B9676E46C90FE33AD5871DE6739881C45CE350940EA2C2E62BA": return 107;
                case "AA3673AE8649444728A26A80EB8310810D98730154ECEB9FEB4786C9843D2BCF": return 107;
                case "B9C6D4759161CD02766573C9DAF176B9A2E1B4718D2BF68179A55645823F8555": return 107;
                case "DA205178E9CFF784FB19AEF0F73ECED1F28494B6E5AB7EF787EA969C9B182DD8": return 107;
                case "DED00ADCCBE9C96B0394D5D01F11306D8B364029FF2AD528106A95B8DA17EAA1": return 107;
                case "1EADF689CF679D39025657B01F62E0AAA76BBB8ED38CDE2F80D0FC65E5B34E87": return 201;
                case "E4C3DF7BB760A8719DAB91660C38E7B9B7F42B290DE1BEF04B3B1E2BE58AD390": return 201;
                default: return 0;
            }
        }

        internal const uint HiddenSetupCreationFlags = 0x08000000;
        internal static SetupStartupInfo HiddenSetupStartupInfo(string desktopName)
        {
            if (String.IsNullOrEmpty(desktopName) || !desktopName.StartsWith("PaviseRtssSetup-", StringComparison.Ordinal)
                || desktopName.IndexOf('\\') >= 0 || desktopName.IndexOf('/') >= 0)
                throw new InvalidOperationException(AutomaticPolicy.Text("RTSS 安装环境无效。", "The RTSS setup environment is invalid."));
            return new SetupStartupInfo { Size = Marshal.SizeOf(typeof(SetupStartupInfo)), Desktop = desktopName,
                Flags = 1, ShowWindow = 0 };
        }

        private static IntPtr StartHiddenSetup(ProcessStartInfo start, string desktopName)
        {
            if (start == null || start.UseShellExecute || !start.CreateNoWindow || start.WindowStyle != ProcessWindowStyle.Hidden
                || !String.IsNullOrEmpty(start.Verb) || String.IsNullOrWhiteSpace(start.FileName)
                || !System.IO.Path.IsPathRooted(start.FileName) || start.FileName.IndexOf('"') >= 0
                || start.FileName.IndexOf('\r') >= 0 || start.FileName.IndexOf('\n') >= 0)
                throw new InvalidOperationException(AutomaticPolicy.Text("RTSS 组件启动参数无效。", "The RTSS component launch arguments are invalid."));
            string executable = System.IO.Path.GetFullPath(start.FileName);
            SetupStartupInfo startup = HiddenSetupStartupInfo(desktopName);
            SetupProcessInformation process;
            if (!CreateSetupProcess(executable, new StringBuilder("\"" + executable + "\" " + start.Arguments),
                IntPtr.Zero, IntPtr.Zero, false, HiddenSetupCreationFlags, IntPtr.Zero, start.WorkingDirectory,
                ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), AutomaticPolicy.Text("无法启动 RTSS 组件操作。", "Could not start the RTSS component operation."));
            if (process.Thread != IntPtr.Zero) CloseComponentHandle(process.Thread);
            return process.Process;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SetupStartupInfo
        {
            internal int Size;
            internal string Reserved, Desktop, Title;
            internal int X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
            internal short ShowWindow, ReservedBytes;
            internal IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SetupProcessInformation
        { internal IntPtr Process, Thread; internal int ProcessId, ThreadId; }
        [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateSetupDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", EntryPoint = "CloseDesktop")] private static extern bool CloseSetupDesktop(IntPtr desktop);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSetupProcess(string application, StringBuilder commandLine, IntPtr processSecurity,
            IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string workingDirectory,
            ref SetupStartupInfo startup, out SetupProcessInformation process);
        [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
        private static extern uint WaitForSetupProcess(IntPtr process, uint milliseconds);
        [DllImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
        private static extern bool GetSetupExitCode(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
        private static extern uint GetSetupProcessId(IntPtr process);
        [DllImport("user32.dll", EntryPoint = "EnumDesktopWindows", SetLastError = true)]
        private static extern bool EnumSetupDesktopWindows(IntPtr desktop, ComponentWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "EnumChildWindows")]
        private static extern bool EnumSetupChildWindows(IntPtr parent, ComponentWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "GetDlgItem")] private static extern IntPtr GetSetupDialogItem(IntPtr dialog, int item);
        [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")] private static extern bool IsSetupWindowEnabled(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool IsSetupWindowVisible(IntPtr window);

        private sealed class LimitedReadStream : Stream
        {
            private readonly Stream source;
            private long remaining;
            public LimitedReadStream(Stream source, long length) { this.source = source; remaining = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = source.Read(buffer, offset, (int)Math.Min(count, remaining));
                remaining -= read;
                return read;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
