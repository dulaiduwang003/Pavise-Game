using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // BootIdentifier 标识加载器；启动次数取自共享页 BootId。
    internal static class BootSessionIdentity
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            byte[] bytes, UIntPtr size, out UIntPtr read);

        internal static string Read()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return null;
#else
            try
            {
                var data = new byte[0x2c8]; UIntPtr read;
                if (!ReadProcessMemory(new IntPtr(-1), new IntPtr(0x7ffe0000), data,
                    new UIntPtr((uint)data.Length), out read) || read.ToUInt64() != (ulong)data.Length) return null;
                if (BitConverter.ToUInt32(data, 0x26c) != 10) return null;
                Guid loader;
                if (!Guid.TryParse(new CoreIsolationNative().BootIdentity(), out loader)) return null;
                return Create(loader, BitConverter.ToUInt32(data, 0x2c4));
            }
            catch { return null; }
#endif
        }

        internal static string Create(Guid loader, uint sequence)
        {
            return loader == Guid.Empty || sequence == 0 ? null
                : "boot2:" + loader.ToString("N") + ":" + sequence.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool Valid(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return false;
            string[] fields = identity.Split(':'); Guid loader; uint sequence;
            return fields.Length == 3 && fields[0] == "boot2"
                && Guid.TryParseExact(fields[1], "N", out loader) && loader != Guid.Empty
                && uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
                && sequence > 0;
        }

        internal static IrqRebootState Compare(string saved, string current)
        {
            if (!Valid(saved) || !Valid(current)) return IrqRebootState.Unknown;
            return string.Equals(saved, current, StringComparison.OrdinalIgnoreCase)
                ? IrqRebootState.AwaitingReboot : IrqRebootState.Rebooted;
        }
    }
}
