using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // Root's isolated runner compiles this entry point instead of product Main.
    // The only write target is this short-lived console test process itself.
    internal static class AntiCheatNativeChecks
    {
        private sealed class MemoryStore : IAntiCheatAffinityStore
        {
            internal string Receipt = "";
            internal int Saves;
            public bool Load(out string receipt) { receipt = Receipt; return true; }
            public bool Save(string receipt) { Receipt = receipt; Saves++; return true; }
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        private static int checks;
        private static void Check(bool yes, string message)
        { checks++; if (!yes) throw new InvalidOperationException(message); }
        private static int Skip(string reason)
        { Console.WriteLine("SKIP anti-cheat native own-process checks=" + checks + " reason=" + reason + " other_process_writes=false settings_writes=false product_main=false"); return 0; }

        private static int Main()
        {
            IAntiCheatAffinityPlatform native = null;
            AntiCheatAffinityIdentity identity = null;
            AntiCheatAffinityLease lease = null;
            ushort originalGroup = 0;
            ulong originalMask = 0;
            bool writeAttempted = false;
            int result = 1;
            try
            {
                long creation, exit, kernel, user;
                Check(GetProcessTimes(GetCurrentProcess(), out creation, out exit, out kernel, out user) && creation > 0 && exit == 0, "self process times ABI");
                int pid; string path;
                using (Process self = Process.GetCurrentProcess()) { pid = self.Id; path = Path.GetFullPath(self.MainModule.FileName); }
                string reason;
                Check(AntiCheatAffinityLease.TryIdentify(pid, creation, Path.GetFileName(path), out identity, out reason), "identify actual own process: " + reason);
                Check(identity.ProcessId == pid && identity.CreationTime == creation && string.Equals(identity.ImagePath, path, StringComparison.OrdinalIgnoreCase), "self image and creation identity round trip");
                AntiCheatAffinityIdentity rejected;
                Check(!AntiCheatAffinityLease.TryIdentify(pid, creation + 1, Path.GetFileName(path), out rejected, out reason), "wrong creation rejected by actual API");
                Check(!AntiCheatAffinityLease.TryIdentify(pid, creation, "Pavise.NativeProbe.WrongIdentity.exe", out rejected, out reason), "wrong image rejected by actual API");

                Type implementation = typeof(AntiCheatAffinityLease).GetNestedType("NativePlatform", BindingFlags.NonPublic);
                Check(implementation != null, "actual private native implementation found");
                native = (IAntiCheatAffinityPlatform)Activator.CreateInstance(implementation, true);
                AntiCheatAffinityObservation initial = native.Read(identity);
                if (initial.Read != AntiCheatAffinityRead.Known) return Skip(initial.Read + ":" + initial.Detail);
                Check(initial.Mask != 0 && initial.AllowedMask != 0 && (initial.Mask & ~initial.AllowedMask) == 0, "native process/system masks decode");
                originalGroup = initial.Group; originalMask = initial.Mask;
                if (!initial.CpuSetsKnown || !initial.CpuSetsEmpty) return Skip("native CPU Sets admission unavailable");
                if (string.IsNullOrEmpty(native.DeviceIdentity)) return Skip("native device identity unavailable");
                ulong subset = originalMask & (originalMask - 1);
                if (subset == 0) return Skip("own process has fewer than two allowed logical processors");
                Check(subset != originalMask && (subset & originalMask) == subset, "temporary target is a proper subset of own original mask");
                ushort inspectedGroup; ulong inspectedMask;
                Check(AntiCheatAffinityLease.TryInspect(identity, out inspectedGroup, out inspectedMask, out reason)
                    && inspectedGroup == originalGroup && inspectedMask == originalMask, "public read-only inspection agrees with actual native backend");
                MemoryStore store = new MemoryStore();
                lease = new AntiCheatAffinityLease(native, store);
                Check(lease.Recover() && store.Receipt == "", "memory-only journal recovery");

                writeAttempted = true;
                AntiCheatAffinityResult bound = lease.Ensure(identity, originalGroup, subset);
                Check(bound.Bound && !bound.PendingRecovery, "actual own-process hard affinity write and verification: " + bound.Code + ":" + bound.Detail);
                AntiCheatAffinityObservation applied = native.Read(identity);
                Check(applied.Read == AntiCheatAffinityRead.Known && applied.Group == originalGroup && applied.Mask == subset, "actual kernel hard affinity readback");
                Check(store.Saves >= 2 && store.Receipt.Length > 0 && lease.OriginalAffinity(identity) == originalMask, "memory-only write-ahead ownership retains exact original mask");
                Check(lease.ReleaseAll(), "actual own-process hard affinity restoration");
                AntiCheatAffinityObservation restored = native.Read(identity);
                Check(restored.Read == AntiCheatAffinityRead.Known && restored.Group == originalGroup && restored.Mask == originalMask, "exact own-process original affinity restored");
                Check(store.Receipt == "" && !lease.PendingRecovery && !lease.HasOwnership(identity), "memory journal and ownership clear after native restoration");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.ToString()); result = 1; }
            finally
            {
                // Recovery is attempted even if an assertion or actual ABI call failed above.
                if (native != null && identity != null)
                {
                    try
                    {
                        if (writeAttempted && originalMask != 0)
                        {
                            if (lease != null) lease.ReleaseAll();
                            AntiCheatAffinityObservation observed = native.Read(identity);
                            if (observed.Read == AntiCheatAffinityRead.Known && observed.Group == originalGroup && observed.Mask != originalMask)
                            {
                                IAntiCheatAffinityRestorePlatform restorer = native as IAntiCheatAffinityRestorePlatform;
                                AntiCheatAffinityObservation after;
                                bool restored = restorer != null
                                    ? restorer.Restore(identity, originalGroup, observed.Mask, originalMask, out after)
                                    : native.Write(identity, originalGroup, observed.Mask, originalMask, out after);
                                if (!restored || after == null || after.Read != AntiCheatAffinityRead.Known || after.Mask != originalMask)
                                { Console.Error.WriteLine("FAIL native final own-process restoration"); result = 1; }
                            }
                            else if (observed.Read != AntiCheatAffinityRead.Known || observed.Group != originalGroup)
                            { Console.Error.WriteLine("FAIL native final own-process restoration unreadable"); result = 1; }
                        }
                    }
                    catch (Exception error) { Console.Error.WriteLine("FAIL native final recovery: " + error); result = 1; }
                    finally { native.Forget(identity); }
                }
            }
            if (result == 0) Console.WriteLine("PASS anti-cheat actual native own-process assertions=" + checks
                + " group=" + originalGroup + " original=0x" + originalMask.ToString("X")
                + " original_restored=true other_process_writes=false settings_writes=false product_main=false");
            return result;
        }
    }
}
