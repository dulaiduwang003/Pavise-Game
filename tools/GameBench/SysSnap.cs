// System state snapshot for apply/restore checks: registry keys Pavise may touch, services, power scheme and overlay,
// timer resolution, and every process's scheduling attributes. Writes one "section<TAB>key<TAB>value" line per fact.
// Usage (elevated): SysSnap <out.tsv> [threadsOfProcessName]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

internal static class SysSnap
{
    [DllImport("ntdll.dll")] private static extern int NtQueryTimerResolution(out uint min, out uint max, out uint current);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActualOverlayScheme(out Guid value);
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr process, out UIntPtr system);
    [DllImport("kernel32.dll")] private static extern bool GetProcessPriorityBoost(IntPtr h, out bool disabled);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref int value, int length, IntPtr ret);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationThread(IntPtr h, int cls, ref ThreadBasic info, int length, IntPtr ret);
    [DllImport("kernel32.dll")] private static extern int GetThreadPriority(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GetThreadSelectedCpuSets(IntPtr h, uint[] ids, uint count, out uint required);
    [DllImport("kernel32.dll")] private static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count, out uint required);
    [DllImport("kernel32.dll")] private static extern bool GetProcessInformation(IntPtr h, int cls, ref PowerThrottling info, int size);
    [DllImport("gdi32.dll")] private static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr process, out int priority);
    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)] private struct ThreadBasic { public int ExitStatus; public IntPtr TebBaseAddress; public IntPtr UniqueProcess, UniqueThread; public UIntPtr AffinityMask; public int Priority, BasePriority; }
    private const uint QueryLimited = 0x1000, QueryInformation = 0x0400, ThreadQueryLimited = 0x0800, ThreadQuery = 0x0040;

    private static readonly string[] MachineKeys =
    {
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
        @"SYSTEM\CurrentControlSet\Control\PriorityControl",
        @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
        @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling",
        @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
        @"SYSTEM\CurrentControlSet\Control\FileSystem",
        @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters",
        @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters",
        @"SOFTWARE\Microsoft\Windows\Dwm",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance",
        @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization",
        @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
        @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
        @"SOFTWARE\Policies\Microsoft\Windows\GameDVR",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\BenchGame.exe",
        @"SOFTWARE\Policies\Microsoft\Windows\AppCompat",
        @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
        @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
        @"SOFTWARE\Microsoft\Windows\Windows Error Reporting",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection",
    };
    private static readonly string[] UserKeys =
    {
        @"System\GameConfigStore",
        @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
        @"Software\Microsoft\GameBar",
        @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
        @"Software\Microsoft\DirectX\UserGpuPreferences",
        @"Software\Microsoft\Windows\CurrentVersion\PushNotifications",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        @"Control Panel\Desktop",
        @"Control Panel\Desktop\WindowMetrics",
        @"Control Panel\Mouse",
        @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
        @"Software\Microsoft\Windows\CurrentVersion\Privacy",
        @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
    };

    private static StreamWriter output;
    private static void Put(string section, string key, object value)
    { output.Write(section); output.Write('\t'); output.Write(key.Replace('\t', ' ')); output.Write('\t'); output.WriteLine(Convert.ToString(value).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')); }

    private static string Format(object v)
    {
        if (v == null) return "<null>";
        var bytes = v as byte[]; if (bytes != null) return "hex:" + BitConverter.ToString(bytes).Replace("-", "");
        var strings = v as string[]; if (strings != null) return string.Join("|", strings);
        return Convert.ToString(v);
    }

    private static void Dump(RegistryKey root, string label, string path, int depth)
    {
        RegistryKey key;
        // Some system subkeys deny even administrators; record that instead of stopping the snapshot.
        try { key = root.OpenSubKey(path); }
        catch (Exception e) { Put("reg", label + "\\" + path, "<" + e.GetType().Name + ">"); return; }
        using (key)
        {
            if (key == null) { Put("reg", label + "\\" + path, "<absent>"); return; }
            string[] names, subs;
            try { names = key.GetValueNames(); subs = depth > 0 ? key.GetSubKeyNames() : new string[0]; }
            catch (Exception e) { Put("reg", label + "\\" + path, "<" + e.GetType().Name + ">"); return; }
            foreach (string name in names)
            {
                object v; try { v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch (Exception e) { v = "<" + e.GetType().Name + ">"; }
                Put("reg", label + "\\" + path + "\\" + (name.Length == 0 ? "(default)" : name), Format(v));
            }
            foreach (string sub in subs) Dump(root, label, path + "\\" + sub, depth - 1);
        }
    }

    private static string Guid(IntPtr p) { return ((Guid)Marshal.PtrToStructure(p, typeof(Guid))).ToString(); }

    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("SysSnap <out.tsv> [threadProcessName]"); return 2; }
        using (output = new StreamWriter(args[0], false, new UTF8Encoding(false)))
        {
            Put("meta", "time", DateTime.Now.ToString("o"));
            foreach (string k in MachineKeys) Dump(Registry.LocalMachine, "HKLM", k, k.EndsWith("SystemProfile") ? 3 : 1);
            foreach (string k in UserKeys) Dump(Registry.CurrentUser, "HKCU", k, 1);

            uint min, max, cur;
            if (NtQueryTimerResolution(out min, out max, out cur) == 0) { Put("timer", "current_100ns", cur); Put("timer", "finest_100ns", max); }
            IntPtr scheme;
            if (PowerGetActiveScheme(IntPtr.Zero, out scheme) == 0) { Put("power", "active_scheme", Guid(scheme)); LocalFree(scheme); }
            Guid overlay;
            if (PowerGetActualOverlayScheme(out overlay) == 0) Put("power", "overlay_actual", overlay);
            if (PowerGetEffectiveOverlayScheme(out overlay) == 0) Put("power", "overlay_effective", overlay);

            foreach (ServiceController s in ServiceController.GetServices())
            {
                string start; try { start = s.StartType.ToString(); } catch { start = "?"; }
                Put("service", s.ServiceName, s.Status + "/" + start);
                s.Dispose();
            }

            string threadsOf = args.Length > 1 ? args[1] : null;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id <= 4) continue;
                    string key = p.ProcessName + "#" + p.Id;
                    IntPtr h = OpenProcess(QueryLimited | QueryInformation, false, p.Id);
                    if (h == IntPtr.Zero) h = OpenProcess(QueryLimited, false, p.Id);
                    if (h == IntPtr.Zero) { Put("proc", key, "<no access>"); continue; }
                    try
                    {
                        var sb = new StringBuilder();
                        sb.Append("prio=0x").Append(GetPriorityClass(h).ToString("X"));
                        UIntPtr pm, sm; if (GetProcessAffinityMask(h, out pm, out sm)) sb.Append(" aff=0x").Append(pm.ToUInt64().ToString("X"));
                        bool boostOff; if (GetProcessPriorityBoost(h, out boostOff)) sb.Append(" boostoff=").Append(boostOff ? 1 : 0);
                        int io = -1; if (NtQueryInformationProcess(h, 33, ref io, 4, IntPtr.Zero) == 0) sb.Append(" io=").Append(io);
                        int page = -1; if (NtQueryInformationProcess(h, 39, ref page, 4, IntPtr.Zero) == 0) sb.Append(" page=").Append(page);
                        var pt = new PowerThrottling { Version = 1 };
                        if (GetProcessInformation(h, 4, ref pt, Marshal.SizeOf(typeof(PowerThrottling)))) sb.Append(" eco=").Append(pt.ControlMask).Append('/').Append(pt.StateMask);
                        int gpu; if (D3DKMTGetProcessSchedulingPriorityClass(h, out gpu) == 0) sb.Append(" gpu=").Append(gpu);
                        uint need; if (!GetProcessDefaultCpuSets(h, null, 0, out need) && need > 0) sb.Append(" cpusets=").Append(need);
                        Put("proc", key, sb.ToString());
                    }
                    finally { CloseHandle(h); }
                    if (threadsOf != null && string.Equals(p.ProcessName, threadsOf, StringComparison.OrdinalIgnoreCase))
                        foreach (ProcessThread t in p.Threads)
                        {
                            IntPtr th = OpenThread(ThreadQuery | ThreadQueryLimited, false, t.Id);
                            if (th == IntPtr.Zero) continue;
                            try
                            {
                                var info = new ThreadBasic();
                                string mask = NtQueryInformationThread(th, 0, ref info, Marshal.SizeOf(typeof(ThreadBasic)), IntPtr.Zero) == 0 ? "0x" + info.AffinityMask.ToUInt64().ToString("X") : "?";
                                uint sets; GetThreadSelectedCpuSets(th, null, 0, out sets);
                                Put("thread", p.ProcessName + "#" + p.Id + "/" + t.Id, "mask=" + mask + " prio=" + GetThreadPriority(th) + " cpusets=" + sets + " cpu_ms=" + (long)t.TotalProcessorTime.TotalMilliseconds);
                            }
                            finally { CloseHandle(th); }
                        }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return 0;
    }
}
