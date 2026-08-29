// @author bdth 2074055628@qq.com
// Local SCM adapter for the optional, session-owned service pause policy.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal sealed class OptionalServiceControl : IOptionalServiceControl
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryConfig = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceEnumerateDependents = 0x0008;
        private const uint ServiceStart = 0x0010;
        private const uint ServiceStop = 0x0020;
        private const uint QueryAccess = ServiceQueryConfig | ServiceQueryStatus | ServiceEnumerateDependents;
        private const uint ServiceActive = 1;
        private const uint ServiceAcceptStop = 1;
        private const int ServiceStopped = 1;
        private const int ServiceRunning = 4;
        private const int ServiceDisabled = 4;
        private const int ErrorPathNotFound = 3;
        private const int ErrorAccessDenied = 5;
        private const int ErrorInvalidHandle = 6;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorMoreData = 234;
        private const int ErrorServiceRequestTimeout = 1053;
        private const int ErrorServiceDatabaseLocked = 1055;
        private const int ErrorServiceAlreadyRunning = 1056;
        private const int ErrorServiceDisabled = 1058;
        private const int ErrorServiceDoesNotExist = 1060;
        private const int ErrorServiceDependencyFail = 1068;
        private const int ErrorServiceLogonFailed = 1069;
        private const int ErrorServiceMarkedForDelete = 1072;
        private const int ErrorServiceDependencyDeleted = 1075;
        private const int MaxConfigurationBytes = 65536;
        private const int MaxPrinterBytes = 16 * 1024 * 1024;
        private const uint PrinterEnumLocal = 0x00000002;
        private const uint PrinterEnumConnections = 0x00000004;
        private const uint PrinterEnumCategoryAll = 0x02000000;
        private const uint PrinterAttributeShared = 0x00000008;
        private const uint PrinterAttributeNetwork = 0x00000010;
        private const uint PrinterBusyStates = 0x00000100 | 0x00000200 | 0x00000400 | 0x00004000;

        private sealed class ServiceConfiguration
        {
            public uint Type;
            public uint StartType;
            public string[] Dependencies;
            public string Identity;
        }

        public bool TryGetBootIdentity(out string identity)
        {
            RefuseNativeInTests();
            identity = "";
            try
            {
                // phnt/ntexapi.h: information class 90; BootIdentifier is the
                // first field of SYSTEM_BOOT_ENVIRONMENT_INFORMATION.
                SYSTEM_BOOT_ENVIRONMENT_INFORMATION boot;
                uint returned;
                uint size = (uint)Marshal.SizeOf(typeof(SYSTEM_BOOT_ENVIRONMENT_INFORMATION));
                int status = NtQuerySystemInformation(90, out boot, size, out returned);
                if (status < 0 || returned < 20 || returned > size || boot.BootIdentifier == Guid.Empty)
                    return false;
                identity = boot.BootIdentifier.ToString("N");
                return true;
            }
            catch { return false; }
        }

        public bool TryQuery(string name, out OptionalServiceSnapshot snapshot)
        {
            RefuseNativeInTests();
            snapshot = new OptionalServiceSnapshot { Configuration = "" };
            if (string.IsNullOrWhiteSpace(name)) return false;
            IntPtr scm = IntPtr.Zero, service = IntPtr.Zero;
            try
            {
                scm = OpenSCManagerW(null, null, ScManagerConnect);
                if (scm == IntPtr.Zero) return false;
                service = OpenServiceW(scm, name, QueryAccess);
                if (service == IntPtr.Zero)
                    return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist;
                ServiceConfiguration configuration;
                return TryReadSnapshot(service, out snapshot, out configuration);
            }
            catch { return false; }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (scm != IntPtr.Zero) CloseServiceHandle(scm);
            }
        }

        public bool TryStop(string name, OptionalServiceSnapshot expected, Func<bool> mayContinue)
        {
            RefuseNativeInTests();
            if (!OptionalServicePauseEngine.IsAllowed(name) || object.ReferenceEquals(expected, null)
                || !CanStop(expected) || string.IsNullOrEmpty(expected.Configuration)
                || !MayContinue(mayContinue)) return false;
            // This must be repeated for PrintNotify as well as Spooler. The
            // check is only an idle snapshot, not a lock against new print jobs.
            if (IsPrintingService(name) && !IsPrintingIdle(mayContinue)) return false;
            IntPtr scm = IntPtr.Zero, service = IntPtr.Zero;
            bool stopCallEntered = false;
            try
            {
                scm = OpenSCManagerW(null, null, ScManagerConnect);
                if (scm == IntPtr.Zero || !MayContinue(mayContinue)) return false;
                service = OpenServiceW(scm, name, QueryAccess | ServiceStop);
                if (service == IntPtr.Zero || !MayContinue(mayContinue)) return false;
                OptionalServiceSnapshot current;
                ServiceConfiguration configuration;
                if (!TryReadSnapshot(service, out current, out configuration, mayContinue)
                    || !CanStop(current) || !IsWin32Service(configuration.Type)
                    || !string.Equals(current.Configuration, expected.Configuration, StringComparison.Ordinal))
                    return false;
                SERVICE_STATUS status;
                if (!MayContinue(mayContinue)) return false;
                // Success means our request was accepted, not that STOPPED has
                // been reached. Do not discard success if cancellation arrives
                // during this call: the engine must still own that pending stop.
                stopCallEntered = true;
                bool accepted = ControlService(service, 1, out status);
                int error = accepted ? 0 : Marshal.GetLastWin32Error();
                if (!accepted && error == ErrorServiceRequestTimeout)
                    throw new TimeoutException("The service stop request timed out; its ownership is unknown.");
                return accepted;
            }
            catch
            {
                // Once the native call began, an exception cannot prove that no
                // stop was delivered. Let the engine retain its Prepared record.
                if (stopCallEntered) throw;
                return false;
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (scm != IntPtr.Zero) CloseServiceHandle(scm);
            }
        }

        public OptionalServiceStartResult TryStart(string name, string expectedConfiguration)
        {
            RefuseNativeInTests();
            if (!OptionalServicePauseEngine.IsAllowed(name) || string.IsNullOrEmpty(expectedConfiguration))
                return OptionalServiceStartResult.OwnershipChanged;
            IntPtr scm = IntPtr.Zero, service = IntPtr.Zero;
            bool startCallEntered = false;
            try
            {
                scm = OpenSCManagerW(null, null, ScManagerConnect);
                if (scm == IntPtr.Zero) return OptionalServiceStartResult.NotIssued;
                service = OpenServiceW(scm, name, ServiceQueryConfig | ServiceQueryStatus | ServiceStart);
                if (service == IntPtr.Zero)
                    return IsStartOwnershipError(Marshal.GetLastWin32Error())
                        ? OptionalServiceStartResult.OwnershipChanged : OptionalServiceStartResult.NotIssued;
                ServiceConfiguration configuration;
                bool ownershipChanged;
                if (!TryReadStartState(service, expectedConfiguration, out configuration, out ownershipChanged))
                    return ownershipChanged ? OptionalServiceStartResult.OwnershipChanged
                        : OptionalServiceStartResult.NotIssued;
                var checkedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                checkedServices.Add(name);
                if (!DependenciesRunning(scm, configuration.Dependencies, checkedServices, 0))
                    return OptionalServiceStartResult.NotIssued;
                // Recheck after walking dependencies. Never change the startup
                // type, and never treat ALREADY_RUNNING as our own start.
                if (!TryReadStartState(service, expectedConfiguration, out configuration, out ownershipChanged))
                    return ownershipChanged ? OptionalServiceStartResult.OwnershipChanged
                        : OptionalServiceStartResult.NotIssued;
                startCallEntered = true;
                bool accepted = StartServiceW(service, 0, IntPtr.Zero);
                int error = accepted ? 0 : Marshal.GetLastWin32Error();
                return accepted ? OptionalServiceStartResult.Accepted : ClassifyStartFailure(error);
            }
            catch
            {
                // The engine must retain Restoring after an uncertain native
                // call instead of treating it as a safely retryable rejection.
                if (startCallEntered) throw;
                return OptionalServiceStartResult.NotIssued;
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (scm != IntPtr.Zero) CloseServiceHandle(scm);
            }
        }

        public bool IsPrintingIdle()
        {
            RefuseNativeInTests();
            return IsPrintingIdle(null);
        }

        private bool IsPrintingIdle(Func<bool> mayContinue)
        {
            if (!MayContinue(mayContinue) || !SpoolerReady() || !MayContinue(mayContinue)) return false;
            try
            {
                uint needed, count;
                // Level 4 reads the local connection cache without contacting
                // remote printers. We cannot prove their queues idle, so any
                // connection (or failure) excludes the whole printing group.
                bool noConnections = EnumPrintersW(PrinterEnumConnections, null, 4,
                    IntPtr.Zero, 0, out needed, out count);
                if (!MayContinue(mayContinue) || !noConnections || count != 0) return false;
                uint flags = PrinterEnumLocal | PrinterEnumCategoryAll;
                bool success = EnumPrintersW(flags, null, 2, IntPtr.Zero, 0, out needed, out count);
                int error = success ? 0 : Marshal.GetLastWin32Error();
                if (!MayContinue(mayContinue)) return false;
                if (success) return count == 0 && SpoolerReady() && MayContinue(mayContinue);
                if (error != ErrorInsufficientBuffer) return false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (needed == 0 || needed > MaxPrinterBytes) return false;
                    uint capacity = needed;
                    IntPtr buffer = Marshal.AllocHGlobal((int)capacity);
                    try
                    {
                        success = EnumPrintersW(flags, null, 2, buffer, capacity, out needed, out count);
                        error = success ? 0 : Marshal.GetLastWin32Error();
                        if (!MayContinue(mayContinue)) return false;
                        if (success)
                        {
                            int stride = Marshal.SizeOf(typeof(PRINTER_INFO_2));
                            if (count > capacity / (uint)stride) return false;
                            for (uint i = 0; i < count; i++)
                            {
                                var printer = (PRINTER_INFO_2)Marshal.PtrToStructure(
                                    IntPtr.Add(buffer, (int)i * stride), typeof(PRINTER_INFO_2));
                                if (printer.Jobs != 0
                                    || (printer.Attributes & (PrinterAttributeShared | PrinterAttributeNetwork)) != 0
                                    || (printer.Status & PrinterBusyStates) != 0) return false;
                            }
                            return SpoolerReady() && MayContinue(mayContinue);
                        }
                        if (error != ErrorInsufficientBuffer || needed <= capacity)
                            return false;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                return false;
            }
            catch { return false; }
        }

        private bool SpoolerReady()
        {
            OptionalServiceSnapshot snapshot;
            return TryQuery("Spooler", out snapshot) && snapshot.Exists
                && snapshot.State == ServiceRunning && snapshot.StartType != ServiceDisabled;
        }

        private static bool IsPrintingService(string name)
        {
            return string.Equals(name, "Spooler", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "PrintNotify", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanStop(OptionalServiceSnapshot snapshot)
        {
            return snapshot.Exists && snapshot.State == ServiceRunning && snapshot.StartType != ServiceDisabled
                && snapshot.AcceptsStop && !snapshot.HasActiveDependents;
        }

        private static bool TryReadStartState(IntPtr service, string expectedConfiguration,
            out ServiceConfiguration configuration, out bool ownershipChanged)
        {
            configuration = null;
            ownershipChanged = false;
            SERVICE_STATUS status;
            if (!QueryServiceStatus(service, out status))
            {
                ownershipChanged = IsStartOwnershipError(Marshal.GetLastWin32Error());
                return false;
            }
            // Preserve an observed external start/pause immediately. A later
            // configuration or dependent query failure must not erase it.
            if (status.State != ServiceStopped)
            {
                ownershipChanged = true;
                return false;
            }
            int error;
            if (!TryReadConfiguration(service, out configuration, out error))
            {
                ownershipChanged = IsStartOwnershipError(error);
                return false;
            }
            ownershipChanged = configuration.StartType == ServiceDisabled || !IsWin32Service(configuration.Type)
                || !string.Equals(configuration.Identity, expectedConfiguration, StringComparison.Ordinal);
            return !ownershipChanged;
        }

        private static bool IsStartOwnershipError(int error)
        {
            return error == ErrorServiceAlreadyRunning || error == ErrorServiceDisabled
                || error == ErrorServiceDoesNotExist || error == ErrorServiceMarkedForDelete;
        }

        private static OptionalServiceStartResult ClassifyStartFailure(int error)
        {
            if (IsStartOwnershipError(error)) return OptionalServiceStartResult.OwnershipChanged;
            // StartServiceW documents these as rejections before the target
            // service starts. A dependency failure does not start this target.
            switch (error)
            {
                case ErrorPathNotFound:
                case ErrorAccessDenied:
                case ErrorInvalidHandle:
                case ErrorServiceDatabaseLocked:
                case ErrorServiceDependencyFail:
                case ErrorServiceLogonFailed:
                case ErrorServiceDependencyDeleted:
                    return OptionalServiceStartResult.NotIssued;
                case ErrorServiceRequestTimeout:
                    throw new TimeoutException("The service start request timed out; its outcome is unknown.");
                default:
                    // NO_THREAD can follow process creation. Unknown errors,
                    // including RPC/registry failures, cannot prove no start.
                    throw new Win32Exception(error, "The service start request failed; its outcome is unknown.");
            }
        }

        private static bool IsWin32Service(uint type)
        {
            return (type & 0x30) != 0 && (type & 0x03) == 0;
        }

        private static bool DependenciesRunning(IntPtr scm, string[] dependencies,
            HashSet<string> checkedServices, int depth)
        {
            if (depth > 32) return false;
            foreach (string name in dependencies)
            {
                // Starting a group dependency may start arbitrary services in
                // that group. This policy must never do that implicitly.
                if (string.IsNullOrEmpty(name) || name[0] == '+') return false;
                if (checkedServices.Contains(name)) continue;
                if (checkedServices.Count >= 128) return false;
                IntPtr dependency = OpenServiceW(scm, name, ServiceQueryConfig | ServiceQueryStatus);
                if (dependency == IntPtr.Zero) return false;
                try
                {
                    SERVICE_STATUS status;
                    ServiceConfiguration configuration;
                    if (!QueryServiceStatus(dependency, out status) || status.State != ServiceRunning
                        || !TryReadConfiguration(dependency, out configuration)) return false;
                    checkedServices.Add(name);
                    if (!DependenciesRunning(scm, configuration.Dependencies, checkedServices, depth + 1))
                        return false;
                }
                finally { CloseServiceHandle(dependency); }
            }
            return true;
        }

        private static bool TryReadSnapshot(IntPtr service, out OptionalServiceSnapshot snapshot,
            out ServiceConfiguration configuration, Func<bool> mayContinue = null)
        {
            snapshot = new OptionalServiceSnapshot { Configuration = "" };
            configuration = null;
            SERVICE_STATUS status;
            bool activeDependents;
            if (!TryReadConfiguration(service, out configuration, mayContinue) || !MayContinue(mayContinue)
                || !QueryServiceStatus(service, out status) || !MayContinue(mayContinue)
                || status.State < 1 || status.State > 7 || !TryHasActiveDependents(service, out activeDependents)
                || !MayContinue(mayContinue))
                return false;
            snapshot = new OptionalServiceSnapshot
            {
                Exists = true,
                State = (int)status.State,
                StartType = (int)configuration.StartType,
                AcceptsStop = (status.ControlsAccepted & ServiceAcceptStop) != 0,
                HasActiveDependents = activeDependents,
                Configuration = configuration.Identity
            };
            return true;
        }

        private static bool TryHasActiveDependents(IntPtr service, out bool active)
        {
            active = false;
            uint needed, count;
            if (EnumDependentServicesW(service, ServiceActive, IntPtr.Zero, 0, out needed, out count))
            {
                active = count != 0;
                return true;
            }
            // A nonempty required buffer already proves an active dependent;
            // its name is not needed because we never recursively stop it.
            if (Marshal.GetLastWin32Error() != ErrorMoreData || needed == 0) return false;
            active = true;
            return true;
        }

        private static bool TryReadConfiguration(IntPtr service, out ServiceConfiguration configuration,
            Func<bool> mayContinue = null)
        {
            int error;
            return TryReadConfiguration(service, out configuration, out error, mayContinue);
        }

        private static bool TryReadConfiguration(IntPtr service, out ServiceConfiguration configuration,
            out int error, Func<bool> mayContinue = null)
        {
            configuration = null;
            uint needed;
            bool success = QueryServiceConfigW(service, IntPtr.Zero, 0, out needed);
            error = success ? 0 : Marshal.GetLastWin32Error();
            if (success || error != ErrorInsufficientBuffer || !MayContinue(mayContinue)) return false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (needed < Marshal.SizeOf(typeof(QUERY_SERVICE_CONFIG)) || needed > MaxConfigurationBytes)
                    return false;
                uint capacity = needed;
                IntPtr buffer = Marshal.AllocHGlobal((int)capacity);
                try
                {
                    success = QueryServiceConfigW(service, buffer, capacity, out needed);
                    error = success ? 0 : Marshal.GetLastWin32Error();
                    if (!MayContinue(mayContinue)) return false;
                    if (success)
                    {
                        var raw = (QUERY_SERVICE_CONFIG)Marshal.PtrToStructure(buffer, typeof(QUERY_SERVICE_CONFIG));
                        if (raw.StartType > ServiceDisabled) return false;
                        string binary, group, account, display;
                        string[] dependencies;
                        if (!TryReadString(buffer, (int)capacity, raw.BinaryPath, out binary)
                            || !TryReadString(buffer, (int)capacity, raw.LoadOrderGroup, out group)
                            || !TryReadString(buffer, (int)capacity, raw.Account, out account)
                            || !TryReadString(buffer, (int)capacity, raw.DisplayName, out display)
                            || !TryReadDependencies(buffer, (int)capacity, raw.Dependencies, out dependencies))
                            return false;
                        var identity = new StringBuilder("svc1|");
                        AppendPart(identity, raw.Type.ToString(CultureInfo.InvariantCulture));
                        AppendPart(identity, raw.StartType.ToString(CultureInfo.InvariantCulture));
                        AppendPart(identity, raw.ErrorControl.ToString(CultureInfo.InvariantCulture));
                        AppendPart(identity, raw.TagId.ToString(CultureInfo.InvariantCulture));
                        AppendPart(identity, binary);
                        AppendPart(identity, group);
                        AppendPart(identity, account);
                        AppendPart(identity, display);
                        AppendPart(identity, dependencies.Length.ToString(CultureInfo.InvariantCulture));
                        foreach (string dependency in dependencies) AppendPart(identity, dependency);
                        configuration = new ServiceConfiguration
                        {
                            Type = raw.Type, StartType = raw.StartType,
                            Dependencies = dependencies, Identity = identity.ToString()
                        };
                        return true;
                    }
                    if (error != ErrorInsufficientBuffer || needed <= capacity) return false;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return false;
        }

        private static void AppendPart(StringBuilder target, string value)
        {
            target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
        }

        private static bool MayContinue(Func<bool> mayContinue)
        {
            if (mayContinue == null) return true;
            try { return mayContinue(); }
            catch { return false; }
        }

        private static bool TryReadString(IntPtr buffer, int capacity, IntPtr pointer, out string value)
        {
            value = "";
            if (pointer == IntPtr.Zero) return true;
            long offset = pointer.ToInt64() - buffer.ToInt64();
            if (offset < 0 || offset > capacity - 2 || (offset & 1) != 0) return false;
            int available = (capacity - (int)offset) / 2;
            for (int i = 0; i < available; i++)
                if (Marshal.ReadInt16(pointer, i * 2) == 0)
                {
                    value = Marshal.PtrToStringUni(pointer, i);
                    return true;
                }
            return false;
        }

        private static bool TryReadDependencies(IntPtr buffer, int capacity, IntPtr pointer, out string[] names)
        {
            names = new string[0];
            if (pointer == IntPtr.Zero) return true;
            var values = new List<string>();
            for (int i = 0; i < 128; i++)
            {
                string value;
                if (!TryReadString(buffer, capacity, pointer, out value)) return false;
                if (value.Length == 0)
                {
                    names = values.ToArray();
                    return true;
                }
                values.Add(value);
                pointer = IntPtr.Add(pointer, (value.Length + 1) * 2);
            }
            return false;
        }

        private static void RefuseNativeInTests()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            throw new InvalidOperationException("Optional service checks must use a mocked service control in tests.");
#endif
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint Type, State, ControlsAccepted, Win32ExitCode, SpecificExitCode, CheckPoint, WaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QUERY_SERVICE_CONFIG
        {
            public uint Type, StartType, ErrorControl;
            public IntPtr BinaryPath, LoadOrderGroup;
            public uint TagId;
            public IntPtr Dependencies, Account, DisplayName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PRINTER_INFO_2
        {
            public IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location;
            public IntPtr DevMode, SeparatorFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
            public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_BOOT_ENVIRONMENT_INFORMATION
        {
            public Guid BootIdentifier;
            public uint FirmwareType;
            public ulong BootFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool QueryServiceConfigW(IntPtr service, IntPtr buffer, uint size, out uint needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool EnumDependentServicesW(IntPtr service, uint state, IntPtr buffer, uint size,
            out uint needed, out uint count);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool StartServiceW(IntPtr service, uint count, IntPtr arguments);
        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr service);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool EnumPrintersW(uint flags, string name, uint level, IntPtr buffer, uint size,
            out uint needed, out uint count);
        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtQuerySystemInformation(int informationClass,
            out SYSTEM_BOOT_ENVIRONMENT_INFORMATION information, uint size, out uint returned);
    }
}
