// @author bdth 2074055628@qq.com
// File purpose Clean up the foreground scheduling weight written by older versions; only the restore ability remains
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class FgBoost
    {
        private static readonly ReversibleReg Sep = new ReversibleReg(
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl",
            "Win32PrioritySeparation", RegistryValueKind.DWord, "PrevWin32PriSep");
        private static readonly object lk = new object();

        public static bool Restore()
        {
            lock (lk) return !Sep.HasBackup || Sep.Restore();
        }

        public static bool HasResidue() { return Sep.HasBackup; }
    }
}
