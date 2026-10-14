using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class PrivacyNativeBackend
    {
#if PAVISE_SELFTEST
        internal static Func<string, string> ServiceReadForTest;
        internal static Func<string, string, bool> ServiceWriteForTest;
#endif
        private static void ValidateServiceName(string name)
        {
            if (name != "DiagTrack" && name != "PcaSvc") throw new ArgumentException("Unsupported persistent service");
        }
        private static OptionalServiceSnapshot QueryService(string name)
        {
            ValidateServiceName(name);
            OptionalServiceSnapshot snapshot;
            if (!new OptionalServiceControl().TryQuery(name, out snapshot) || snapshot == null)
                throw new InvalidOperationException("Service state unavailable");
            return snapshot;
        }
        private static string ServiceIdentity(string configuration)
        {
            if (configuration == null || !configuration.StartsWith("svc1|", StringComparison.Ordinal)) throw new FormatException("Unknown service identity");
            int position = 5;
            var identity = new System.Text.StringBuilder("svc1|");
            for (int field = 0; position < configuration.Length; field++)
            {
                int colon = configuration.IndexOf(':', position), length;
                if (colon < position || !int.TryParse(configuration.Substring(position, colon - position), out length)
                    || length < 0 || length > configuration.Length - colon - 2 || configuration[colon + 1 + length] != '|')
                    throw new FormatException("Invalid service identity");
                string part = configuration.Substring(position, colon + length + 2 - position);
                if (field != 1) identity.Append(part); // Startup type is a separate owned target.
                position = colon + length + 2;
            }
            return identity.ToString();
        }
        private static string ServiceRead(string name)
        {
            ValidateServiceName(name);
#if PAVISE_SELFTEST
            if (ServiceReadForTest == null) throw new InvalidOperationException("Unmocked privacy service read");
            return ServiceReadForTest(name) ?? throwUnknown();
#else
            var snapshot = QueryService(name);
            if (!snapshot.Exists) return "missing";
            if (snapshot.State != 1 && snapshot.State != 4) throw new NotSupportedException("Service is transitioning; retry after it settles");
            return "v:" + snapshot.State.ToString(CultureInfo.InvariantCulture) + ":" + B64(ServiceIdentity(snapshot.Configuration));
#endif
        }
        private static string throwUnknown() { throw new InvalidOperationException("Unknown service state"); }
        private static string ServiceStartRead(string name)
        {
            ValidateServiceName(name);
#if PAVISE_SELFTEST
            if (ServiceReadForTest == null) throw new InvalidOperationException("Unmocked privacy service startup read");
            return ServiceReadForTest(name + ".Start") ?? throwUnknown();
#else
            var snapshot = QueryService(name);
            if (!snapshot.Exists) return "missing";
            return "c:" + snapshot.StartType.ToString(CultureInfo.InvariantCulture) + ":" + B64(ServiceIdentity(snapshot.Configuration));
#endif
        }
        private static string ChangeServiceValue(string value, char prefix, int desired)
        {
            string[] parts = (value ?? "").Split(':');
            int state;
            if (parts.Length != 3 || parts[0] != prefix.ToString() || !int.TryParse(parts[1], out state)
                || string.IsNullOrEmpty(UnB64(parts[2]))) throw new FormatException("Invalid service snapshot");
            return prefix + ":" + desired.ToString(CultureInfo.InvariantCulture) + ":" + parts[2];
        }
        internal static string ServiceWanted(string current, bool running) { return ChangeServiceValue(current, 'v', running ? 4 : 1); }
        private static string ServiceStartWanted(string current, int startup) { return ChangeServiceValue(current, 'c', startup); }
        private static bool ServiceStartWrite(string name, string desired)
        {
            string[] wanted = (desired ?? "").Split(':');
            uint startup;
            if (wanted.Length != 3 || wanted[0] != "c" || !uint.TryParse(wanted[1], out startup) || startup > 4) return false;
            string[] before = ServiceStartRead(name).Split(':');
            if (before.Length != 3 || before[2] != wanted[2]) return false;
            if (before[1] == wanted[1]) return true;
#if PAVISE_SELFTEST
            if (ServiceWriteForTest == null) throw new InvalidOperationException("Unmocked privacy service startup write");
            return ServiceWriteForTest(name + ".Start", desired);
#else
            IntPtr manager = PrivacyOpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = PrivacyOpenService(manager, name, 0xf);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try {
                    OptionalServiceSnapshot exact;
                    if (!OptionalServiceControl.TryQueryHandleForPrivacy(service, out exact) || !exact.Exists
                        || B64(ServiceIdentity(exact.Configuration)) != wanted[2]
                        || exact.StartType.ToString(CultureInfo.InvariantCulture) != before[1] || (startup == 4 && exact.State != 1)) return false;
                    return PrivacyChangeServiceConfig(service, 0xffffffff, startup, 0xffffffff,
                        null, null, IntPtr.Zero, null, null, null, null) && ServiceStartRead(name) == desired;
                }
                finally { PrivacyCloseServiceHandle(service); }
            }
            finally { PrivacyCloseServiceHandle(manager); }
#endif
        }
        private static bool ServiceWrite(string name, string desired)
        {
            string[] wanted = (desired ?? "").Split(':');
            int state;
            if (wanted.Length != 3 || wanted[0] != "v" || !int.TryParse(wanted[1], out state) || (state != 1 && state != 4)) return false;
            string[] before = ServiceRead(name).Split(':');
            if (before.Length != 3 || before[2] != wanted[2]) return false;
            if (before[1] == wanted[1]) return true;
#if PAVISE_SELFTEST
            if (ServiceWriteForTest == null) throw new InvalidOperationException("Unmocked privacy service write");
            return ServiceWriteForTest(name, desired);
#else
            OptionalServiceSnapshot snapshot = QueryService(name);
            if (B64(ServiceIdentity(snapshot.Configuration)) != wanted[2] || snapshot.State.ToString(CultureInfo.InvariantCulture) != before[1]) return false;
            if (state == 1 && (!snapshot.AcceptsStop || snapshot.HasActiveDependents)) throw new NotSupportedException("Service cannot safely stop");
            if (state == 4 && snapshot.StartType == 4) return false;
            IntPtr manager = PrivacyOpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = PrivacyOpenService(manager, name, 0x3d);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    OptionalServiceSnapshot exact;
                    if (!OptionalServiceControl.TryQueryHandleForPrivacy(service, out exact) || !exact.Exists
                        || B64(ServiceIdentity(exact.Configuration)) != wanted[2] || exact.State != snapshot.State
                        || exact.StartType != snapshot.StartType || (state == 1 && (!exact.AcceptsStop || exact.HasActiveDependents))) return false;
                    PrivacyServiceStatus status;
                    bool accepted = state == 1 ? PrivacyControlService(service, 1, out status) : PrivacyStartService(service, 0, IntPtr.Zero);
                    if (!accepted) return false;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.ElapsedMilliseconds < 5000)
                    {
                        // Query the native status directly; a transitioning state is not completion.
                        if (!PrivacyQueryServiceStatus(service, out status)) return false;
                        if (status.CurrentState == state)
                        {
                            try { return ServiceRead(name) == desired; }
                            catch { return false; } // An accepted native operation with unknown readback is a fault.
                        }
                        Thread.Sleep(40);
                    }
                    return false;
                }
                finally { PrivacyCloseServiceHandle(service); }
            }
            finally { PrivacyCloseServiceHandle(manager); }
#endif
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PrivacyServiceStatus
        {
            internal int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
        }
        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr PrivacyOpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr PrivacyOpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
        private static extern bool PrivacyCloseServiceHandle(IntPtr service);
        [DllImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
        private static extern bool PrivacyControlService(IntPtr service, uint control, out PrivacyServiceStatus status);
        [DllImport("advapi32.dll", EntryPoint = "QueryServiceStatus", SetLastError = true)]
        private static extern bool PrivacyQueryServiceStatus(IntPtr service, out PrivacyServiceStatus status);
        [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
        private static extern bool PrivacyStartService(IntPtr service, uint count, IntPtr arguments);
        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool PrivacyChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string binary,
            string group, IntPtr tag, string dependencies, string account, string password, string display);
    }
}
