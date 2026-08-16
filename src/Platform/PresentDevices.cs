// @author bdth 2074055628@qq.com
// 文件用途 只枚举当前真正插着的设备实例 以及沿设备树向上读取父链

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static class PresentDevices
    {
        private const uint DIGCF_PRESENT = 0x02;
        private const uint DIGCF_ALLCLASSES = 0x04;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int CR_BUFFER_SMALL = 26;
        private const uint DEVPROP_TYPE_STRING = 0x12;

        private static readonly DEVPROPKEY BusReportedDescKey = new DEVPROPKEY
        {
            fmtid = new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2),
            pid = 4
        };

        public static List<string> ByClass(Guid classGuid)
        {
            return Enumerate(classGuid, null, DIGCF_PRESENT);
        }

        public static List<string> ByEnumerator(string enumerator)
        {
            return Enumerate(Guid.Empty, enumerator, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        }

        public static List<string> ParentChain(string instanceId)
        {
            var chain = new List<string>();
            if (string.IsNullOrEmpty(instanceId)) return chain;
            try
            {
                uint node;
                if (CM_Locate_DevNodeW(out node, instanceId, 0) != 0) return chain;
                for (int depth = 0; depth < 16; depth++)
                {
                    uint parent;
                    if (CM_Get_Parent(out parent, node, 0) != 0) break;
                    var buffer = new StringBuilder(260);
                    if (CM_Get_Device_IDW(parent, buffer, 260, 0) != 0) break;
                    string id = buffer.ToString();
                    if (id.Length == 0 || id.StartsWith("HTREE", StringComparison.OrdinalIgnoreCase)) break;
                    chain.Add(id);
                    node = parent;
                }
            }
            catch (Exception ex) { Logger.Log(Lang.T("log.presentdevices.1") + ex.Message); }
            return chain;
        }

        public static string BusReportedDesc(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            try
            {
                uint node;
                if (CM_Locate_DevNodeW(out node, instanceId, 0) != 0) return null;
                DEVPROPKEY key = BusReportedDescKey;
                uint type;
                uint size = 0;
                int cr = CM_Get_DevNode_PropertyW(node, ref key, out type, null, ref size, 0);
                if (cr != CR_BUFFER_SMALL || size == 0 || size > 4096) return null;
                var buffer = new byte[size];
                cr = CM_Get_DevNode_PropertyW(node, ref key, out type, buffer, ref size, 0);
                if (cr != 0 || type != DEVPROP_TYPE_STRING) return null;
                string text = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
                return text.Length == 0 ? null : text;
            }
            catch { return null; }
        }

        private static List<string> Enumerate(Guid classGuid, string enumerator, uint flags)
        {
            var ids = new List<string>();
            IntPtr set = IntPtr.Zero;
            try
            {
                if (classGuid == Guid.Empty)
                    set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, flags);
                else
                {
                    Guid g = classGuid;
                    set = SetupDiGetClassDevsW(ref g, enumerator, IntPtr.Zero, flags);
                }
                if (set == IntPtr.Zero || set == new IntPtr(-1)) return ids;

                var data = new SP_DEVINFO_DATA();
                data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    string id = InstanceIdOf(set, ref data);
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                }
            }
            catch (Exception ex) { Logger.Log(Lang.T("log.presentdevices.2") + ex.Message); }
            finally
            {
                if (set != IntPtr.Zero && set != new IntPtr(-1))
                    try { SetupDiDestroyDeviceInfoList(set); } catch { }
            }
            return ids;
        }

        private static string InstanceIdOf(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            uint needed = 0;
            if (!SetupDiGetDeviceInstanceIdW(set, ref data, null, 0, out needed)
                && Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER) return null;
            if (needed == 0 || needed > 4096) return null;
            var buffer = new StringBuilder((int)needed + 1);
            if (!SetupDiGetDeviceInstanceIdW(set, ref data, buffer, needed + 1, out needed)) return null;
            return buffer.ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(
            ref Guid classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
        private static extern IntPtr SetupDiGetClassDevsW(
            IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(
            IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder buffer, uint size, out uint needed);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, uint bufferLen, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key,
            out uint type, byte[] buffer, ref uint size, uint flags);
    }
}
