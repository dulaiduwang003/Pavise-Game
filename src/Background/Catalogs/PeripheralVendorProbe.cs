using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class PeripheralVendorProbe
    {
        private const int RefreshMs = 15000;
        private const int RegistrationLifetimeMs = 45000;
        private const int DeviceChangeDebounceMs = 1000;
        private const int Win32OwnProcess = 0x10;
        private const int UserOwnProcess = 0x50;
        private const int UserOwnProcessInstance = 0xD0;
        private const int InteractiveProcess = 0x100;
        private static readonly object sync = new object();
        private static PeripheralDevice[] devices = new PeripheralDevice[0];
        private static Dictionary<string, HashSet<string>> registeredPrograms = EmptyRegistrations();
        private static int stamp;
        private static int generation;
        private static bool scanned;
        private static bool refreshing;
        private static bool invalidated;
        private static int deviceEpoch;
        private static int registrationStamp;
        private static int refreshQueued;
        private static int failureLogged;

        internal static int CurrentGeneration()
        {
            if (RefreshDue() && Interlocked.CompareExchange(ref refreshQueued, 1, 0) == 0)
            {
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    try { RefreshDevicesForDisplay(); }
                    finally { Volatile.Write(ref refreshQueued, 0); }
                })) Volatile.Write(ref refreshQueued, 0);
            }
            return CachedGeneration();
        }

        internal static int CachedGeneration()
        {
            lock (sync) return generation;
        }

        internal static bool RefreshDue()
        {
            lock (sync) return ShouldRefresh(Environment.TickCount);
        }

        internal static void InvalidateDevices()
        {
            lock (sync)
            {
                invalidated = true;
                deviceEpoch++;
                if (registeredPrograms.Count > 0)
                {
                    registeredPrograms = EmptyRegistrations();
                    generation++;
                }
            }
        }

        private static bool ShouldRefresh(int now)
        {
            return !refreshing && (!scanned || unchecked((uint)(now - stamp)) >= (invalidated ? DeviceChangeDebounceMs : RefreshMs));
        }

        internal static void RefreshDevicesForDisplay()
        {
            int epoch;
            lock (sync)
            {
                int now = Environment.TickCount;
                if (!ShouldRefresh(now)) return;
                refreshing = true;
                stamp = now;
                scanned = true;
                invalidated = false;
                epoch = deviceEpoch;
            }

            try
            {
                PeripheralDevice[] nextDevices = PeripheralDeviceInventory.Discover();
                Publish(epoch, nextDevices, ReadRegisteredPrograms(nextDevices));
                Volatile.Write(ref failureLogged, 0);
            }
            catch (Exception error)
            {
                Publish(epoch, new PeripheralDevice[0], EmptyRegistrations());
                if (Interlocked.Exchange(ref failureLogged, 1) == 0)
                    Logger.Warn(Logger.Format("外设扫描失败，已撤销设备登记的自动豁免 失败类型 {0}",
                        "Peripheral discovery failed, device-registered exemptions cleared. Error type {0}", error.GetType().Name));
            }
            finally
            {
                lock (sync) refreshing = false;
            }
        }

        private static Dictionary<string, HashSet<string>> EmptyRegistrations()
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        private static void Publish(int epoch, PeripheralDevice[] nextDevices, Dictionary<string, HashSet<string>> nextPrograms)
        {
            lock (sync)
            {
                if (epoch != deviceEpoch) return;
                registrationStamp = Environment.TickCount;
                if (SameDevices(devices, nextDevices) && SamePrograms(registeredPrograms, nextPrograms)) return;
                devices = nextDevices;
                registeredPrograms = nextPrograms;
                generation++;
            }
        }

        internal static PeripheralDevice[] DevicesForDisplay()
        {
            lock (sync) return (PeripheralDevice[])devices.Clone();
        }

        internal static string[] RegisteredProgramsForDisplay()
        {
            lock (sync)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (RegistrationsFresh())
                    foreach (string program in registeredPrograms.Keys) names.Add(Path.GetFileName(program));
                var result = new string[names.Count];
                names.CopyTo(result);
                Array.Sort(result, StringComparer.OrdinalIgnoreCase);
                return result;
            }
        }

        internal static bool MatchesRegisteredProgram(int pid, long creation, string path)
        {
            if (pid <= 4 || creation <= 0) return false;
            Dictionary<string, HashSet<string>> snapshot;
            HashSet<string> services;
            string executable;
            lock (sync)
            {
                if (registeredPrograms.Count == 0 || !RegistrationsFresh()) return false;
                snapshot = registeredPrograms;
                executable = WhitelistRule.NormalizeImagePath(path);
                if (!snapshot.TryGetValue(executable, out services)) return false;
            }
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                if (Native.QueryProcessIdentityState(handle, creation) != 0
                    || !WhitelistRule.PathEquals(executable, Native.ImagePath(handle))) return false;
                foreach (string service in services)
                {
                    if (SvcQuery.RunningOwnProcessId(service) != pid) continue;
                    if (Native.QueryProcessIdentityState(handle, creation) != 0) return false;
                    lock (sync) return ReferenceEquals(snapshot, registeredPrograms) && RegistrationsFresh();
                }
                return false;
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }

        private static bool RegistrationsFresh()
        {
            return unchecked((uint)(Environment.TickCount - registrationStamp)) < RegistrationLifetimeMs;
        }

        private static bool SamePrograms(Dictionary<string, HashSet<string>> left, Dictionary<string, HashSet<string>> right)
        {
            if (left.Count != right.Count) return false;
            foreach (KeyValuePair<string, HashSet<string>> program in left)
            {
                HashSet<string> services;
                if (!right.TryGetValue(program.Key, out services) || !program.Value.SetEquals(services)) return false;
            }
            return true;
        }

        private static bool SameDevices(PeripheralDevice[] left, PeripheralDevice[] right)
        {
            if (left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index].Kind != right[index].Kind || left[index].Connected != right[index].Connected
                    || !string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static Dictionary<string, HashSet<string>> ReadRegisteredPrograms(PeripheralDevice[] current)
        {
            var programs = EmptyRegistrations();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (PeripheralDevice device in current)
            {
                if (!device.Connected) continue;
                foreach (string service in device.ServiceNames)
                {
                    if (string.IsNullOrEmpty(service) || service.IndexOfAny(new[] { '\\', '/', '\0' }) >= 0 || !seen.Add(service)) continue;
                    try
                    {
                        using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service))
                        {
                            if (key == null) continue;
                            object serviceType = key.GetValue("Type");
                            if (!(serviceType is int)) continue;
                            string command = key.GetValue("ImagePath") as string;
                            string program = RegisteredProgramPath((int)serviceType, command, windows);
                            if (program.Length == 0) continue;
                            HashSet<string> services;
                            if (!programs.TryGetValue(program, out services))
                            {
                                services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                programs.Add(program, services);
                            }
                            services.Add(service);
                        }
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            return programs;
        }

        internal static string RegisteredProgramPath(int type, string command, string windows)
        {
            int hostType = type & ~InteractiveProcess;
            if ((hostType != Win32OwnProcess && hostType != UserOwnProcess && hostType != UserOwnProcessInstance)
                || string.IsNullOrWhiteSpace(command)) return "";
            string value = command.Trim();
            if (value.IndexOf('\0') >= 0) return "";
            if (value[0] == '"')
            {
                int endQuote = value.IndexOf('"', 1);
                if (endQuote < 2 || (endQuote + 1 < value.Length && !char.IsWhiteSpace(value[endQuote + 1]))) return "";
                value = value.Substring(1, endQuote - 1);
            }
            else
            {
                // 未加引号的服务命令行只接受第一个无歧义的可执行文件路径
                int end = 0;
                while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
                value = value.Substring(0, end);
            }
            if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || value.IndexOfAny(new[] { '"', '<', '>', '|', '*', '?' }) >= 0) return "";
            string path = WhitelistRule.NormalizeImagePath(value);
            if (path.Length < 4 || path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0) return "";
            if (string.IsNullOrEmpty(windows) || path.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return "";
            return path;
        }
    }
}
