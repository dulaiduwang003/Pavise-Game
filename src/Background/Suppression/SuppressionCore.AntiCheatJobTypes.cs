using System;
using System.Globalization;

namespace PaviseApp
{
    internal interface IAntiCheatJobPlatform
    {
        IntPtr OpenTarget(int pid, long birth);
        IntPtr Create(string name, out string reason);
        bool Anchor(IntPtr job, IntPtr process, out string reason);
        bool Apply(IntPtr job, IntPtr process, long birth, ulong mask, out string reason);
        bool Verify(IntPtr job, IntPtr process, long birth, ulong mask, out string reason);
        bool Clear(IntPtr job, out string reason);
        IntPtr Open(string name, out int error);
        int Probe(int pid, long birth);
        void Close(IntPtr handle);
    }

    internal static class AntiCheatJobIdentity
    {
        internal const string Prefix = "Local\\Pavise.AntiCheat.V1.";
        internal static string Name(int pid, long birth)
        {
            if (pid <= 0 || birth <= 0) return null;
            return Prefix + pid.ToString(CultureInfo.InvariantCulture) + "." + birth.ToString(CultureInfo.InvariantCulture);
        }
        internal static bool ValidName(string name, int pid, long birth)
        { return name != null && string.Equals(name, Name(pid, birth), StringComparison.Ordinal); }
    }
}
