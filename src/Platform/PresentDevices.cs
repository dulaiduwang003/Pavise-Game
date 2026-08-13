// @author bdth 2074055628@qq.com
// 文件用途 只枚举当前真正插着的设备实例
// 注册表 Enum 树会把历史上插过的每一个设备都留着 本机实测键鼠类注册表 49 条而在场只有 8 条
// 照着注册表做判断会报出一堆早就拔掉的设备 写改动也会写到不存在的设备上
// 设备实例键下的 Control 子键虽然也标记在场 但它带特殊 ACL 非管理员读不到 不能当判据

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

        public static List<string> ByClass(Guid classGuid)
        {
            return Enumerate(classGuid, null, DIGCF_PRESENT);
        }

        public static List<string> ByEnumerator(string enumerator)
        {
            return Enumerate(Guid.Empty, enumerator, DIGCF_PRESENT | DIGCF_ALLCLASSES);
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
            catch (Exception ex) { Logger.Log("在场设备枚举失败 " + ex.Message); }
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
    }
}
