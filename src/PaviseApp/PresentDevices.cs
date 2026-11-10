using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static class PresentDevices
    {
        private struct DEVPROPKEY
        {
            public Guid fmtid;

            public uint pid;
        }

        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;

            public Guid ClassGuid;

            public uint DevInst;

            public IntPtr Reserved;
        }

        private static readonly DEVPROPKEY BusReportedDescKey = new DEVPROPKEY
        {
            fmtid = new Guid(1410045054u, 35648, 17852, 168, 162, 106, 11, 137, 76, 189, 162),
            pid = 4u
        };

        public static List<string> ByClass(Guid classGuid)
        {
            return Enumerate(classGuid, null, 2u);
        }

        public static List<string> ByEnumerator(string enumerator)
        {
            return Enumerate(Guid.Empty, enumerator, 6u);
        }

        public static List<string> ParentChain(string instanceId)
        {
            List<string> stringBuilderTexts = new List<string>();
            if (string.IsNullOrEmpty(instanceId))
            {
                return stringBuilderTexts;
            }
            try
            {
                uint devInst;
                if (CM_Locate_DevNodeW(out devInst, instanceId, 0u) != 0)
                {
                    return stringBuilderTexts;
                }
                for (int i = 0; i < 16; i++)
                {
                    uint parent;
                    if (CM_Get_Parent(out parent, devInst, 0u) != 0)
                    {
                        break;
                    }
                    StringBuilder stringBuilder = new StringBuilder(260);
                    if (CM_Get_Device_IDW(parent, stringBuilder, 260u, 0u) == 0)
                    {
                        string stringBuilderText = stringBuilder.ToString();
                        if (stringBuilderText.Length != 0 && !stringBuilderText.StartsWith("HTREE", StringComparison.OrdinalIgnoreCase))
                        {
                            stringBuilderTexts.Add(stringBuilderText);
                            devInst = parent;
                            continue;
                        }
                        break;
                    }
                    break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(Lang.T("log.presentdevices.1", 0) + ex.Message);
            }
            return stringBuilderTexts;
        }

        public static string BusReportedDesc(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return null;
            }
            try
            {
                uint devInst;
                if (CM_Locate_DevNodeW(out devInst, instanceId, 0u) != 0)
                {
                    return null;
                }
                DEVPROPKEY key = BusReportedDescKey;
                uint size = 0u;
                uint type;
                int cMGetDevNodePropertyWResult = CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, 0u);
                if (cMGetDevNodePropertyWResult != 26 || size == 0 || size > 4096)
                {
                    return null;
                }
                byte[] rawBytes = new byte[size];
                if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, rawBytes, ref size, 0u) != 0 || type != 18)
                {
                    return null;
                }
                string @string = Encoding.Unicode.GetString(rawBytes, 0, (int)size);
                char[] trimChars = new char[1];
                string trimEndTrimmed = @string.TrimEnd(trimChars).Trim();
                return (trimEndTrimmed.Length == 0) ? null : trimEndTrimmed;
            }
            catch
            {
                return null;
            }
        }

        private static List<string> Enumerate(Guid classGuid, string enumerator, uint flags)
        {
            List<string> instanceIdOfResults = new List<string>();
            IntPtr deviceInfoSet = IntPtr.Zero;
            try
            {
                if (classGuid == Guid.Empty)
                {
                    deviceInfoSet = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, flags);
                }
                else
                {
                    Guid classGuid2 = classGuid;
                    deviceInfoSet = SetupDiGetClassDevsW(ref classGuid2, enumerator, IntPtr.Zero, flags);
                }
                if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
                {
                    return instanceIdOfResults;
                }
                SP_DEVINFO_DATA data = default(SP_DEVINFO_DATA);
                data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                for (uint iterationIndex = 0u; SetupDiEnumDeviceInfo(deviceInfoSet, iterationIndex, ref data); iterationIndex++)
                {
                    string instanceIdOfResult = InstanceIdOf(deviceInfoSet, ref data);
                    if (!string.IsNullOrEmpty(instanceIdOfResult))
                    {
                        instanceIdOfResults.Add(instanceIdOfResult);
                    }
                    data.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(Lang.T("log.presentdevices.2", 0) + ex.Message);
            }
            finally
            {
                if (deviceInfoSet != IntPtr.Zero && deviceInfoSet != new IntPtr(-1))
                {
                    try
                    {
                        SetupDiDestroyDeviceInfoList(deviceInfoSet);
                    }
                    catch
                    {
                    }
                }
            }
            return instanceIdOfResults;
        }

        private static string InstanceIdOf(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            uint needed = 0u;
            if (!SetupDiGetDeviceInstanceIdW(set, ref data, null, 0u, out needed) && Marshal.GetLastWin32Error() != 122)
            {
                return null;
            }
            if (needed == 0 || needed > 4096)
            {
                return null;
            }
            StringBuilder stringBuilder = new StringBuilder((int)(needed + 1));
            if (!SetupDiGetDeviceInstanceIdW(set, ref data, stringBuilder, needed + 1, out needed))
            {
                return null;
            }
            return stringBuilder.ToString();
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder buffer, uint size, out uint needed);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, uint bufferLen, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);
    }
}
