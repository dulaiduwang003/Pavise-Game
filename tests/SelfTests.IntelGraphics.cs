#if PAVISE_SELFTEST
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int intelChecks;

        internal static int RunIntelGraphicsRegressionTests()
        {
            Action[] cases =
            {
                IntelNativeAbiAndEnduranceRequests,
                IntelAdapterIdentity,
                IntelAvailabilityProbe,
                IntelNativeDefaultsRefuseTests
            };
            intelChecks = 0;
            foreach (Action test in cases) { test(); Console.WriteLine("PASS " + test.Method.Name); }
            Console.WriteLine("PASS intel-graphics groups=" + cases.Length + " assertions=" + intelChecks
                + " native_access=false windows_shown=false");
            return cases.Length;
        }

        private static void IntelCheck(bool condition, string detail)
        {
            intelChecks++;
            if (!condition) throw new InvalidOperationException("Intel regression: " + detail);
        }

        private static void IntelNativeAbiAndEnduranceRequests()
        {
            IntelCheck(Marshal.SizeOf(typeof(IntelCtlInit)) == 36, "ctl_init_args ABI");
            IntelCheck(Marshal.SizeOf(typeof(IntelCtlAdapterProperties)) == (IntPtr.Size == 8 ? 320 : 312), "adapter ABI");
            IntelCheck(Marshal.SizeOf(typeof(IntelCtlFeatureValue)) == (IntPtr.Size == 8 ? 56 : 40), "get/set ABI");
            IntelCheck(Marshal.OffsetOf(typeof(IntelCtlFeatureValue), "Set").ToInt32() == (IntPtr.Size == 8 ? 25 : 17),
                "C++ bool must be one byte");
            foreach (bool set in new[] { false, true })
            {
                var block = new IntPtr(1234);
                IntelCtlFeatureValue request = IntelGraphicsApi.EnduranceRequest(set, block);
                IntelCheck(request.Size == Marshal.SizeOf(typeof(IntelCtlFeatureValue))
                    && request.FeatureType == 1 && request.ValueType == 5 && request.Set == set
                    && request.CustomValueSize == 8 && request.CustomValue == block
                    && request.Version == 0 && request.ApplicationName == IntPtr.Zero
                    && request.ApplicationNameLength == 0,
                    "Endurance request did not use its global custom control/mode block");
            }
        }

        private static void IntelAdapterIdentity()
        {
            var info = new IntelCtlAdapterProperties
            {
                Version = 2, VendorId = 0x8086, PciDeviceId = 0x1234, SubsystemVendorId = 0x8086,
                SubsystemId = 1, Bus = 3, DriverVersion = 0x10001
            };
            IntelCheck(IntelGraphicsApi.AdapterIdentity(info) == "8086:1234:8086:0001:03:00:00:0000000000010001",
                "PCI identity unstable");
            var invalid = info; invalid.Version = 1;
            IntelCheck(IntelGraphicsApi.AdapterIdentity(invalid) == null, "incomplete adapter properties accepted");
            invalid = info; invalid.PciDeviceId = 0;
            IntelCheck(IntelGraphicsApi.AdapterIdentity(invalid) == null, "empty PCI device id accepted");
            invalid = info; invalid.Function = 8;
            IntelCheck(IntelGraphicsApi.AdapterIdentity(invalid) == null, "invalid PCI function accepted");
            invalid = info; invalid.DriverVersion = 0;
            IntelCheck(IntelGraphicsApi.AdapterIdentity(invalid) == null, "missing driver version accepted");
        }

        private static void IntelAvailabilityProbe()
        {
            Func<bool> previous = IntelGraphicsApi.AvailabilityForTest;
            try
            {
                int queries = 0;
                IntelGraphicsApi.AvailabilityForTest = delegate { queries++; return true; };
                IntelCheck(IntelGraphicsApi.HasAvailable && queries == 1, "available adapter probe was ignored");
                IntelGraphicsApi.AvailabilityForTest = delegate { queries++; return false; };
                IntelCheck(!IntelGraphicsApi.HasAvailable && queries == 2, "unavailable adapter probe was ignored");
                IntelGraphicsApi.AvailabilityForTest = delegate { throw new InvalidOperationException("probe failed"); };
                IntelCheck(!IntelGraphicsApi.HasAvailable, "failed availability probe escaped to the UI");
                IntelGraphicsApi.AvailabilityForTest = null;
                IntelCheck(!IntelGraphicsApi.HasAvailable, "unmocked availability used a native driver");
            }
            finally { IntelGraphicsApi.AvailabilityForTest = previous; }
        }

        private static void IntelNativeDefaultsRefuseTests()
        {
            var api = new IntelGraphicsApi();
            IntelGraphicsAdapter[] adapters;
            int control, mode;
            IntelCheck(!api.TryGetAdapters(out adapters) && adapters == null, "unmocked adapter enumeration");
            IntelCheck(!api.TryReadEndurance(out control, out mode) && control == -1 && mode == -1,
                "unmocked Endurance read");
            IntelCheck(!api.TryWriteEndurance(0, 0), "unmocked Endurance write");
        }
    }
}
#endif
