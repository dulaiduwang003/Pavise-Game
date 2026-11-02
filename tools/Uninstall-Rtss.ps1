# Legacy cleanup only: a matching Pavise installation receipt is mandatory.
# Dot-sourcing defines helpers only and never uninstalls anything.
[CmdletBinding()]
param([string]$InstallPath = '', [switch]$Uninstall)

function ConvertTo-RtssExecutablePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    try {
        $candidate = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
        if ($candidate -notmatch '^[A-Za-z]:\\' -or $candidate.IndexOfAny([char[]]"`r`n`"") -ge 0) { return '' }
        if ([IO.Path]::GetExtension($candidate) -ine '.exe') { $candidate = [IO.Path]::Combine($candidate, 'RTSS.exe') }
        if ([IO.Path]::GetFileName($candidate) -ine 'RTSS.exe') { return '' }
        $candidate = [IO.Path]::GetFullPath($candidate)
        if ([IO.Path]::GetDirectoryName($candidate).TrimEnd('\') -ieq [IO.Path]::GetPathRoot($candidate).TrimEnd('\')) { return '' }
        return $candidate
    } catch { return '' }
}

function Test-RtssPlainPath {
    param([string]$Path)
    try {
        $current = [IO.Path]::GetFullPath($Path)
        while (-not [string]::IsNullOrEmpty($current)) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
            $current = [IO.Path]::GetDirectoryName($current)
        }
        return $true
    } catch { return $false }
}

function Get-RtssRegisteredUninstaller {
    param($Registration)
    try {
        if ($null -eq $Registration -or $Registration.KeyName -ine 'RTSS' -or $Registration.Publisher -ine 'Unwinder') { return $null }
        if ($Registration.DisplayName -notmatch '^(?i:RTSS|RivaTuner Statistics Server)(?: [0-9]+(?:\.[0-9]+){1,3})?$') { return $null }
        $version = $null
        if (-not [Version]::TryParse([string]$Registration.DisplayVersion, [ref]$version) -or $version.Major -le 0) { return $null }
        $command = ([string]$Registration.UninstallString).Trim()
        $executable = ''
        if ($command -match '^"([^"\r\n]+\.exe)"(?:\s|$)') { $executable = $Matches[1] }
        elseif ($command -match '^([^"\r\n]+?\.exe)(?:\s|$)') { $executable = $Matches[1] }
        if ($executable -notmatch '^[A-Za-z]:\\' -or [IO.Path]::GetFileName($executable) -ine 'uninstall.exe') { return $null }
        $executable = [IO.Path]::GetFullPath($executable)
        $directory = [IO.Path]::GetDirectoryName($executable)
        $main = ConvertTo-RtssExecutablePath ([IO.Path]::Combine($directory, 'RTSS.exe'))
        if ($main.Length -eq 0) { return $null }
        if (-not [string]::IsNullOrWhiteSpace($Registration.InstallLocation) -and
            (ConvertTo-RtssExecutablePath $Registration.InstallLocation) -ine $main) { return $null }
        return [pscustomobject]@{ Executable = $main; Directory = $directory; Uninstaller = $executable; Version = $version.ToString() }
    } catch { return $null }
}

function Get-RtssUninstallRegistrations {
    $views = @([Microsoft.Win32.RegistryView]::Registry32)
    if ([Environment]::Is64BitOperatingSystem) { $views = @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32) }
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser)) {
        foreach ($view in $views) {
            $root = $null; $key = $null
            try {
                $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
                $key = $root.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS', $false)
                if ($null -ne $key) {
                    [pscustomobject]@{ KeyName = 'RTSS'; DisplayName = $key.GetValue('DisplayName'); Publisher = $key.GetValue('Publisher');
                        InstallLocation = $key.GetValue('InstallLocation'); UninstallString = $key.GetValue('UninstallString'); DisplayVersion = $key.GetValue('DisplayVersion') }
                }
            } finally { if ($null -ne $key) { $key.Dispose() }; if ($null -ne $root) { $root.Dispose() } }
        }
    }
}

function Test-RtssProductFile {
    param([string]$Path)
    if (-not (Test-RtssPlainPath $Path)) { return $false }
    try {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        return $version.FileMajorPart -gt 0 -and (($version.ProductName + ' ' + $version.FileDescription + ' ' + $version.OriginalFilename) -match '(?i)RivaTuner|RTSS')
    } catch { return $false }
}

function Test-RtssUninstallerFile {
    param([string]$Path)
    if ([IO.Path]::GetFileName($Path) -ine 'uninstall.exe' -and [IO.Path]::GetFileName($Path) -ine 'PaviseRtssUninstall.exe') { return $false }
    if (-not (Test-RtssPlainPath $Path)) { return $false }
    $stream = $null; $reader = $null
    try {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $reader = [IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 512 -or $stream.Length -gt 64MB -or $reader.ReadUInt16() -ne 0x5A4D) { return $false }
        $stream.Position = 0x3C; $pe = $reader.ReadInt32()
        if ($pe -lt 64 -or $pe -gt $stream.Length - 4) { return $false }
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x4550) { return $false }
        $stream.Position = 0
        $magic = [byte[]]@(0xEF,0xBE,0xAD,0xDE,0x4E,0x75,0x6C,0x6C,0x73,0x6F,0x66,0x74,0x49,0x6E,0x73,0x74)
        $matched = 0
        while (($value = $stream.ReadByte()) -ge 0) {
            if ($value -eq $magic[$matched]) { $matched++; if ($matched -eq $magic.Length) { return $true } }
            elseif ($value -eq $magic[0]) { $matched = 1 } else { $matched = 0 }
        }
        return $false
    } catch { return $false }
    finally { if ($null -ne $reader) { $reader.Dispose() }; if ($null -ne $stream) { $stream.Dispose() } }
}

function Get-RtssFileIdentity {
    param([string]$Path)
    $stream = $null; $hash = $null
    try {
        if (-not (Test-RtssPlainPath $Path)) { throw 'A file or parent path is unavailable or is a reparse point.' }
        $creation = [IO.File]::GetCreationTimeUtc($Path).Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $hash = [Security.Cryptography.SHA256]::Create()
        return $creation + "`n" + [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '')
    } finally { if ($null -ne $hash) { $hash.Dispose() }; if ($null -ne $stream) { $stream.Dispose() } }
}

function Get-PaviseRtssOwnershipReceipt {
    $key = $null
    try {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\PaviseAuto', $false)
        if ($null -eq $key) { return '' }
        $value = $key.GetValue('RtssInstallationOwnerV1', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($value -is [string]) { return $value }
        return ''
    } finally { if ($null -ne $key) { $key.Dispose() } }
}

function Test-PaviseRtssOwnedInstallation {
    param([string]$Executable)
    try {
        $receipt = (Get-PaviseRtssOwnershipReceipt).Split("`n")
        if ($receipt.Count -ne 3 -or $receipt[0] -ine $Executable -or
            (ConvertTo-RtssExecutablePath $receipt[0]) -ine $Executable) { return $false }
        return (Get-RtssFileIdentity $Executable) -ceq ($receipt[1] + "`n" + $receipt[2])
    } catch { return $false }
}

function Clear-PaviseRtssOwnershipReceipt {
    param([string]$Expected)
    $key = $null
    try {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\PaviseAuto', $true)
        if ($null -ne $key -and $key.GetValue('RtssInstallationOwnerV1', '') -ceq $Expected) {
            $key.DeleteValue('RtssInstallationOwnerV1', $false)
            $key.Flush()
        }
    } finally { if ($null -ne $key) { $key.Dispose() } }
}

function Get-RtssActiveProcesses {
    param([string]$Executable)
    foreach ($process in @(Get-Process -Name RTSS -ErrorAction SilentlyContinue)) {
        try { if ($process.MainModule.FileName -ieq $Executable) { [pscustomobject]@{ ProcessId = $process.Id; Path = $Executable } } }
        finally { $process.Dispose() }
    }
}

function Get-RtssUninstallPlan {
    param([string]$InstallPath = '')
    $selected = ''
    if (-not [string]::IsNullOrWhiteSpace($InstallPath)) {
        $selected = ConvertTo-RtssExecutablePath $InstallPath
        if ($selected.Length -eq 0) { throw 'InstallPath must name a local RTSS installation directory or RTSS.exe, outside a drive root.' }
    }
    $seen = @{}
    foreach ($registration in @(Get-RtssUninstallRegistrations)) {
        $candidate = Get-RtssRegisteredUninstaller $registration
        if ($null -eq $candidate -or ($selected.Length -ne 0 -and $selected -ine $candidate.Executable) -or $seen.ContainsKey($candidate.Executable)) { continue }
        if (-not (Test-RtssProductFile $candidate.Executable) -or -not (Test-RtssUninstallerFile $candidate.Uninstaller)) { continue }
        if (-not (Test-PaviseRtssOwnedInstallation $candidate.Executable)) { continue }
        $seen[$candidate.Executable] = $true
        [pscustomobject]@{ Executable = $candidate.Executable; Directory = $candidate.Directory; Uninstaller = $candidate.Uninstaller;
            Version = $candidate.Version; ExecutableIdentity = Get-RtssFileIdentity $candidate.Executable;
            UninstallerIdentity = Get-RtssFileIdentity $candidate.Uninstaller; Running = @(Get-RtssActiveProcesses $candidate.Executable).Count }
    }
}

function Test-RtssAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { return [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
}

function Test-RtssMissingFile {
    param([string]$Path)
    try { [void][IO.File]::GetAttributes($Path); return $false }
    catch [IO.FileNotFoundException] { return $true }
    catch [IO.DirectoryNotFoundException] { return $true }
    # Permission and I/O failures do not prove removal.
}

function Test-RtssPendingOperation {
    $active = @(Get-Process -Name PaviseRtssUninstall, uninstall, 'RTSSSetup*' -ErrorAction SilentlyContinue)
    try { return $active.Count -gt 0 } finally { foreach ($process in $active) { $process.Dispose() } }
}

function New-RtssUninstallerStartInfo {
    param([string]$Copy, [string]$InstallationDirectory)
    $main = ConvertTo-RtssExecutablePath $InstallationDirectory
    if ($main.Length -eq 0 -or $Copy -notmatch '^[A-Za-z]:\\' -or $Copy.IndexOfAny([char[]]"`r`n`"") -ge 0) { throw 'Invalid uninstaller launch path.' }
    $directory = [IO.Path]::GetDirectoryName($main).TrimEnd('\')
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [IO.Path]::GetFullPath($Copy); $start.WorkingDirectory = $directory
    $start.Arguments = '/S _?=' + $directory # NSIS requires an unquoted _?= path at the end.
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden; $start.ErrorDialog = $false
    return $start
}

function Assert-RtssPlanUnchanged {
    param($Plan)
    if (-not (Test-PaviseRtssOwnedInstallation $Plan.Executable)) {
        throw 'Pavise installation ownership is missing or changed. The RTSS installation was preserved.'
    }
    $current = @(Get-RtssUninstallPlan -InstallPath $Plan.Executable)
    if ($current.Count -ne 1 -or $current[0].Directory -ine $Plan.Directory -or $current[0].Uninstaller -ine $Plan.Uninstaller -or
        $current[0].ExecutableIdentity -cne $Plan.ExecutableIdentity -or $current[0].UninstallerIdentity -cne $Plan.UninstallerIdentity) {
        throw 'RTSS files or registration changed. Run the preview again before uninstalling.'
    }
}

function Invoke-RtssUninstall {
    param($Plan)
    if (-not (Test-PaviseRtssOwnedInstallation $Plan.Executable)) {
        throw 'Only an identity-verified RTSS installation owned by Pavise may be removed.'
    }
    $ownershipReceipt = Get-PaviseRtssOwnershipReceipt
    if (-not (Test-RtssAdministrator)) { throw 'Removal requires administrator rights. Run Uninstall-RTSS.cmd as administrator, or open an administrator PowerShell and run this script with -Uninstall.' }
    $mutex = [Threading.Mutex]::new($false, 'Global\PaviseRtssManualUninstallV1')
    $locked = $false; $temporary = ''; $pending = $false; $copyLock = $null
    try {
        try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked -or (Test-RtssPendingOperation)) { throw 'Another installer or uninstaller is running. Wait for it to finish before retrying.' }
        Assert-RtssPlanUnchanged $Plan
        Initialize-RtssNativeRunner
        if (-not (Close-RtssSelectedProcess $Plan.Executable)) { throw 'RTSS could not exit normally. Close RTSS and any game that uses it, then retry. No process was forcibly terminated.' }
        Assert-RtssPlanUnchanged $Plan
        if (Test-RtssPendingOperation) { throw 'Another installer or uninstaller started. Wait before retrying.' }
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not (Test-RtssPlainPath $tempRoot)) { throw 'The temporary directory is unavailable or redirected.' }
        $temporary = [IO.Path]::Combine($tempRoot, 'PaviseRtssUninstall-' + [Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($temporary)
        $copy = [IO.Path]::Combine($temporary, 'PaviseRtssUninstall.exe')
        $inputFile = $null; $outputFile = $null
        try {
            $inputFile = [IO.FileStream]::new($Plan.Uninstaller, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $outputFile = [IO.FileStream]::new($copy, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $inputFile.CopyTo($outputFile)
        } finally { if ($null -ne $outputFile) { $outputFile.Dispose() }; if ($null -ne $inputFile) { $inputFile.Dispose() } }
        $copyLock = [IO.FileStream]::new($copy, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if (-not (Test-RtssUninstallerFile $copy) -or (Get-RtssFileIdentity $copy).Split("`n")[-1] -cne $Plan.UninstallerIdentity.Split("`n")[-1]) { throw 'The copied uninstaller failed verification.' }
        Assert-RtssPlanUnchanged $Plan
        $pending = $true # Any uncertain launch outcome retains the official uninstaller's input file.
        $result = Run-RtssPrivateUninstaller (New-RtssUninstallerStartInfo -Copy $copy -InstallationDirectory $Plan.Directory)
        $pending = $result.Pending
        if ($pending) { throw ('The uninstaller is still running. Do not start another removal. Temporary files were retained at ' + $temporary) }
        if (-not [string]::IsNullOrEmpty($result.Error)) { throw $result.Error }
        if ($result.ExitCode -notin @(0, 3010, 1641)) { throw ('The official RTSS uninstaller failed with exit code ' + $result.ExitCode + '. You can retry this script after resolving the problem.') }
        if (-not (Test-RtssMissingFile $Plan.Executable) -or -not (Test-RtssMissingFile $Plan.Uninstaller) -or @(Get-RtssActiveProcesses $Plan.Executable).Count -ne 0) {
            throw 'The uninstaller finished, but RTSS files or processes remain. Removal is not confirmed; restart Windows manually if requested, then retry.'
        }
        Write-Host 'RTSS removal verified.'
        Clear-PaviseRtssOwnershipReceipt $ownershipReceipt
        if ($result.RestartRequired -or $result.ExitCode -in @(3010, 1641)) { Write-Host 'Windows must be restarted manually to finish cleanup. This script does not restart it.' }
    } finally {
        if ($null -ne $copyLock) { $copyLock.Dispose() }
        if (-not $pending -and $temporary.Length -gt 0) {
            # Delete only this invocation's generated copy and its empty directory; never recurse.
            try { [IO.File]::Delete([IO.Path]::Combine($temporary, 'PaviseRtssUninstall.exe')) } catch { }
            try { [IO.Directory]::Delete($temporary, $false) } catch { }
        }
        if ($locked) { $mutex.ReleaseMutex() }; $mutex.Dispose()
    }
}

function Invoke-RtssUninstallCommand {
    param([string]$InstallPath = '', [switch]$Uninstall)
    $plans = @(Get-RtssUninstallPlan -InstallPath $InstallPath)
    if ($plans.Count -eq 0) { Write-Host 'No identity-verified RTSS installation owned by Pavise was found. User installations were preserved.'; return }
    foreach ($plan in $plans) { Write-Host ('RTSS ' + $plan.Version + ' | ' + $plan.Directory + ' | running processes: ' + $plan.Running) }
    if (-not $Uninstall) { Write-Host 'Preview only. Close your games, then run this script as administrator with -Uninstall to remove RTSS.'; return }
    if ($plans.Count -ne 1) { throw 'More than one RTSS installation was found. Preview and select one using -InstallPath.' }
    Write-Host 'Removing RTSS. Windows will not restart automatically.'
    Invoke-RtssUninstall $plans[0]
}

# Native runner definitions follow. They are compiled only after an explicit -Uninstall request.

function Initialize-RtssNativeRunner {
    if ($null -ne ('PaviseRtssManual.NativeRunner' -as [type])) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PaviseRtssManual
{
    public sealed class Result
    {
        public int ExitCode = -1, ProcessId;
        public bool Pending, RestartRequired;
        public string Error = "";
    }
    // Uninstall-only adaptation of the former verified RTSS private-desktop runner.
    public static class NativeRunner
    {
        private static string Text(string chinese, string english) { return english; }
        private static bool PlainPath(string path)
        {
            try {
                for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                return true;
            } catch { return false; }
        }
        public static Result Run(ProcessStartInfo start)
        {
            if (start == null || start.Arguments != "/S _?=" + start.WorkingDirectory
                || Path.GetFileName(start.FileName) != "PaviseRtssUninstall.exe"
                || !PlainPath(start.FileName) || !PlainPath(start.WorkingDirectory))
                throw new InvalidOperationException("Uninstaller launch verification failed.");
            var result = new Result();
            IntPtr desktop = IntPtr.Zero, process = IntPtr.Zero;
            string name = "PaviseRtssSetup-" + Guid.NewGuid().ToString("N");
            try
            {
                desktop = CreateSetupDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x0087, IntPtr.Zero);
                if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a private desktop; nothing was launched.");
                process = StartHiddenSetup(start, name);
                if (process == IntPtr.Zero) throw new InvalidOperationException("The official RTSS uninstaller did not start.");
                uint pid = GetSetupProcessId(process);
                if (pid == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                result.ProcessId = unchecked((int)pid);
                Stopwatch elapsed = Stopwatch.StartNew();
                while (true)
                {
                    uint wait = WaitForSetupProcess(process, 100);
                    if (wait == 0)
                    {
                        uint code;
                        if (!GetSetupExitCode(process, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        result.ExitCode = unchecked((int)code);
                        return result;
                    }
                    if (wait != 0x102) {
                        result.Pending = true; result.Error = "Could not confirm whether the uninstaller exited. Temporary files were retained.";
                        return result;
                    }
                    if (String.IsNullOrEmpty(result.Error)) RespondToSetupDialogs(desktop, pid, result);
                    if (elapsed.ElapsedMilliseconds >= 240000) {
                        result.Pending = true;
                        return result; // Never terminate the official uninstaller, reboot, or move its windows.
                    }
                }
            }
            catch (Exception error)
            {
                if (process == IntPtr.Zero) throw;
                result.Pending = true; result.Error = error.Message;
                return result;
            }
            finally
            {
                if (process != IntPtr.Zero) CloseComponentHandle(process);
                if (desktop != IntPtr.Zero) CloseSetupDesktop(desktop);
            }
        }
        public static bool CloseRtss(string executable)
        {
            string expected = System.IO.Path.GetFullPath(executable);
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

        private static void RespondToSetupDialogs(IntPtr desktop, uint processId, Result result)
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
                    ? KnownSetupPromptAction(body.ToString(), true) : 0;
                int buttonId = answer == 107 ? 7 : answer == 201 ? 1 : answer;
                IntPtr button = buttonId == 0 ? IntPtr.Zero : GetSetupDialogItem(dialog, buttonId);
                if (button == IntPtr.Zero || !String.Equals(ComponentWindowClass(button), "Button", StringComparison.OrdinalIgnoreCase)
                    || !IsSetupWindowEnabled(button) || !IsSetupWindowVisible(button))
                {
                    result.Error = Text("RTSS 组件操作遇到未识别的提示，已请求中止，请重新检测。", "An unrecognized RTSS prompt prevented completion. Detect the component again.");
                    PostComponentMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); // 未知提示只发送关闭命令，不猜测确认按钮。
                    return true;
                }
                // 只响应当前安装进程私有桌面上的已核对对话框。
                GetComponentWindowProcessId(dialog, out owner);
                if (owner != processId) return true;
                if (answer == 107) result.RestartRequired = true; // 拒绝立即重启。
                if (answer == 201) result.Error = "RTSS 上次组件操作尚需重启系统，请重启后重试。";
                else if (answer == 1) result.Error = Text("RTSS 仍在运行，组件操作已中止，请稍后重试。", "RTSS is still running. The component operation was stopped; try again shortly.");
                if (!PostComponentMessage(dialog, 0x0111, new IntPtr(buttonId), button))
                    result.Error = Text("无法完成 RTSS 确认，操作未完成，请稍后重新检测。", "Could not confirm the RTSS operation. Detect the component again shortly.");
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
                throw new InvalidOperationException(Text("RTSS 安装环境无效。", "The RTSS setup environment is invalid."));
            return new SetupStartupInfo { Size = Marshal.SizeOf(typeof(SetupStartupInfo)), Desktop = desktopName,
                Flags = 1, ShowWindow = 0 };
        }

        private static IntPtr StartHiddenSetup(ProcessStartInfo start, string desktopName)
        {
            if (start == null || start.UseShellExecute || !start.CreateNoWindow || start.WindowStyle != ProcessWindowStyle.Hidden
                || !String.IsNullOrEmpty(start.Verb) || String.IsNullOrWhiteSpace(start.FileName)
                || !System.IO.Path.IsPathRooted(start.FileName) || start.FileName.IndexOf('"') >= 0
                || start.FileName.IndexOf('\r') >= 0 || start.FileName.IndexOf('\n') >= 0)
                throw new InvalidOperationException(Text("RTSS 组件启动参数无效。", "The RTSS component launch arguments are invalid."));
            string executable = System.IO.Path.GetFullPath(start.FileName);
            SetupStartupInfo startup = HiddenSetupStartupInfo(desktopName);
            SetupProcessInformation process;
            if (!CreateSetupProcess(executable, new StringBuilder("\"" + executable + "\" " + start.Arguments),
                IntPtr.Zero, IntPtr.Zero, false, HiddenSetupCreationFlags, IntPtr.Zero, start.WorkingDirectory,
                ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), Text("无法启动 RTSS 组件操作。", "Could not start the RTSS component operation."));
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


    }
}
'@
}

function Close-RtssSelectedProcess {
    param([string]$Executable)
    return [PaviseRtssManual.NativeRunner]::CloseRtss($Executable)
}

function Run-RtssPrivateUninstaller {
    param([Diagnostics.ProcessStartInfo]$StartInfo)
    return [PaviseRtssManual.NativeRunner]::Run($StartInfo)
}

if ($MyInvocation.InvocationName -ne '.') {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    try { Invoke-RtssUninstallCommand -InstallPath $InstallPath -Uninstall:$Uninstall }
    catch { Write-Error -Message $_.Exception.Message -ErrorAction Continue; exit 1 }
}
