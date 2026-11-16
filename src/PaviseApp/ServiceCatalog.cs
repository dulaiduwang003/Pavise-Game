using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class ServiceListEntry
    {
        internal readonly string Name, DisplayName;
        internal readonly uint State;
        internal readonly int ProcessId;

        internal ServiceListEntry(string name, string displayName, uint state, int processId = 0)
        { Name = name; DisplayName = displayName; State = state; ProcessId = processId; }
    }

    internal sealed class ServiceHostBinding
    {
        internal readonly string Name, Reason;
        internal readonly bool Known;
        internal readonly int ProcessId;
        internal readonly long CreationTime;
        internal bool HasHost { get { return Known && ProcessId > 4 && CreationTime > 0; } }

        internal ServiceHostBinding(string name, bool known, int processId, long creationTime, string reason = null)
        { Name = name; Known = known; ProcessId = processId; CreationTime = creationTime; Reason = reason ?? ""; }
    }

    // 只读取服务数据库与宿主身份，不申请启动、停止或修改服务的权限。
    internal static class ServiceCatalog
    {
        private const uint Connect = 1, Enumerate = 4, QueryStatus = 4, Win32Services = 48, AllStates = 3;
        private const int MoreData = 234, ServiceMissing = 1060, MaximumEntries = 32768, MaximumPages = 128;
        private const uint PageBytes = 262144;

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            internal uint Type, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceRecord
        {
            internal IntPtr Name, DisplayName;
            internal ServiceStatus Status;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumServicesStatusExW(IntPtr manager, int level, uint type, uint state, IntPtr buffer,
            uint bufferSize, out uint needed, out uint returned, ref uint resume, string group);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, uint size, out uint needed);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static bool Canceled(Func<bool> canceled)
        {
            try { return canceled != null && canceled(); }
            catch { return true; }
        }

        internal static List<ServiceListEntry> ListServices(Func<bool> canceled)
        {
            if (Canceled(canceled)) return null;
            IntPtr manager = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                manager = OpenSCManagerW(null, null, Enumerate);
                if (manager == IntPtr.Zero) return null;
                buffer = Marshal.AllocHGlobal((int)PageBytes);
                var result = new List<ServiceListEntry>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int recordSize = Marshal.SizeOf(typeof(ServiceRecord));
                uint resume = 0;
                for (int page = 0; page < MaximumPages; page++)
                {
                    if (Canceled(canceled)) return null;
                    uint needed, returned, previous = resume;
                    bool complete = EnumServicesStatusExW(manager, 0, Win32Services, AllStates, buffer, PageBytes,
                        out needed, out returned, ref resume, null);
                    int error = complete ? 0 : Marshal.GetLastWin32Error();
                    if (!complete && error != MoreData || returned > PageBytes / (uint)recordSize
                        || returned > MaximumEntries - result.Count) return null;
                    for (uint index = 0; index < returned; index++)
                    {
                        if ((index & 63) == 0 && Canceled(canceled)) return null;
                        IntPtr address = new IntPtr(buffer.ToInt64() + index * recordSize);
                        var record = (ServiceRecord)Marshal.PtrToStructure(address, typeof(ServiceRecord));
                        string name = ReadText(record.Name, buffer);
                        WhitelistRule valid;
                        if (!WhitelistRule.TryCreate(WhitelistRuleKind.Service, name, out valid)) return null;
                        string display = record.DisplayName == IntPtr.Zero ? name : ReadText(record.DisplayName, buffer);
                        if (!seen.Add(name)) return null;
                        result.Add(new ServiceListEntry(name, string.IsNullOrEmpty(display) ? name : display, record.Status.State,
                            record.Status.ProcessId <= int.MaxValue ? (int)record.Status.ProcessId : 0));
                    }
                    if (complete)
                    {
                        if (resume != 0) return null;
                        result.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName));
                        return result;
                    }
                    // 不完整枚举不能显示为空，也不能用不前进的恢复游标反复读取同一页。
                    if (returned == 0 || resume == 0 || resume == previous) return null;
                }
                return null;
            }
            catch { return null; }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (manager != IntPtr.Zero) CloseServiceHandle(manager);
            }
        }

        private static string ReadText(IntPtr text, IntPtr buffer)
        {
            long offset = text.ToInt64() - buffer.ToInt64();
            if (text == IntPtr.Zero || offset < 0 || offset > PageBytes - 2 || (offset & 1) != 0)
                throw new InvalidDataException("service-list-string-range");
            int available = (int)Math.Min(32768L, (PageBytes - offset) / 2);
            for (int index = 0; index < available; index++)
                if (Marshal.ReadInt16(text, index * 2) == 0) return Marshal.PtrToStringUni(text, index);
            throw new InvalidDataException("service-list-string-unterminated");
        }

        internal static Dictionary<string, ServiceHostBinding> ReadHosts(IList<string> names, Func<bool> canceled)
        {
            var result = new Dictionary<string, ServiceHostBinding>(StringComparer.OrdinalIgnoreCase);
            var requested = new List<string>();
            if (names != null) foreach (string name in names)
            {
                if (name == null || result.ContainsKey(name)) continue;
                WhitelistRule valid;
                bool accepted = WhitelistRule.TryCreate(WhitelistRuleKind.Service, name, out valid);
                result.Add(name, Unknown(name, accepted ? "服务归属尚未读取" : "服务名无效"));
                if (accepted) requested.Add(valid.Value);
            }
            if (requested.Count == 0 || Canceled(canceled)) return result;
            IntPtr manager = IntPtr.Zero;
            try
            {
                manager = OpenSCManagerW(null, null, Connect);
                if (manager == IntPtr.Zero)
                {
                    string reason = "服务数据库读取失败，win32=" + Marshal.GetLastWin32Error();
                    foreach (string name in requested) result[name] = Unknown(name, reason);
                    return result;
                }
                foreach (string name in requested)
                {
                    if (Canceled(canceled)) break;
                    result[name] = ReadHost(manager, name, canceled);
                }
            }
            catch (Exception error)
            {
                foreach (string name in requested)
                    if (!result[name].Known) result[name] = Unknown(name, "服务归属读取失败：" + error.GetType().Name);
            }
            finally { if (manager != IntPtr.Zero) CloseServiceHandle(manager); }
            return result;
        }

        private static ServiceHostBinding Unknown(string name, string reason)
        { return new ServiceHostBinding(name, false, 0, 0, reason); }

        private static bool HostState(uint state)
        { return state == 4 || state == 5 || state == 6 || state == 7; }

        private static ServiceHostBinding ReadHost(IntPtr manager, string name, Func<bool> canceled)
        {
            IntPtr service = IntPtr.Zero, process = IntPtr.Zero;
            try
            {
                service = OpenServiceW(manager, name, QueryStatus);
                if (service == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    return error == ServiceMissing ? new ServiceHostBinding(name, true, 0, 0)
                        : Unknown(name, "服务读取失败，win32=" + error);
                }
                ServiceStatus first, second;
                uint needed;
                uint size = (uint)Marshal.SizeOf(typeof(ServiceStatus));
                if (!QueryServiceStatusEx(service, 0, out first, size, out needed))
                    return Unknown(name, "服务状态读取失败，win32=" + Marshal.GetLastWin32Error());
                if (first.State == 1) return new ServiceHostBinding(name, true, 0, 0);
                if ((first.Type & Win32Services) == 0 || !HostState(first.State) || first.ProcessId <= 4 || first.ProcessId > int.MaxValue)
                    return Unknown(name, "服务尚无可核验的运行宿主");
                if (Canceled(canceled)) return Unknown(name, "服务归属读取已取消");
                process = OpenProcess(0x1000, false, (int)first.ProcessId);
                if (process == IntPtr.Zero) return Unknown(name, "服务宿主身份读取失败，win32=" + Marshal.GetLastWin32Error());
                long birth, exit, kernel, user;
                uint code;
                if (!GetProcessTimes(process, out birth, out exit, out kernel, out user) || birth <= 0 || exit != 0
                    || !GetExitCodeProcess(process, out code) || code != 259)
                    return Unknown(name, "服务宿主创建时间或存活状态未核验");
                if (Canceled(canceled)) return Unknown(name, "服务归属读取已取消");
                if (!QueryServiceStatusEx(service, 0, out second, size, out needed))
                    return Unknown(name, "服务宿主归属复核失败，win32=" + Marshal.GetLastWin32Error());
                // 启动及停止过渡状态中的 PID 不具备有效绑定资格；服务换宿主后也不能沿用旧创建时间。
                if ((second.Type & Win32Services) == 0 || !HostState(second.State) || second.ProcessId != first.ProcessId
                    || !GetExitCodeProcess(process, out code) || code != 259 || Canceled(canceled))
                    return Unknown(name, "服务宿主归属已变化或无法核验");
                return new ServiceHostBinding(name, true, (int)first.ProcessId, birth);
            }
            catch (Exception error) { return Unknown(name, "服务宿主读取失败：" + error.GetType().Name); }
            finally
            {
                if (process != IntPtr.Zero) CloseHandle(process);
                if (service != IntPtr.Zero) CloseServiceHandle(service);
            }
        }
    }
}
