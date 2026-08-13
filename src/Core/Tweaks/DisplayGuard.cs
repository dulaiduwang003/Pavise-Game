// @author bdth 2074055628@qq.com
// 文件用途 只读查询主显示器刷新率 并还原旧版刷新率守护留下的残留

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class DisplayGuard
    {
        private const string Slot = "PrevRefreshHz";
        private static readonly object lk = new object();

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int DM_BITSPERPEL = 0x40000;
        private const int DM_PELSWIDTH = 0x80000;
        private const int DM_PELSHEIGHT = 0x100000;
        private const int DM_DISPLAYFREQUENCY = 0x400000;

        internal static int CurrentRefreshRate()
        {
            try
            {
                string dev = Screen.PrimaryScreen.DeviceName;
                DEVMODE cur = NewDm();
                if (!EnumDisplaySettingsW(dev, ENUM_CURRENT_SETTINGS, ref cur)) return 0;
                return cur.dmDisplayFrequency;
            }
            catch { return 0; }
        }

        internal static void QueryRefreshRates(out int current, out int best)
        {
            current = 0; best = 0;
            try
            {
                string dev = Screen.PrimaryScreen.DeviceName;
                DEVMODE cur = NewDm();
                if (!EnumDisplaySettingsW(dev, ENUM_CURRENT_SETTINGS, ref cur)) return;
                current = cur.dmDisplayFrequency;
                best = current;
                DEVMODE m = NewDm();
                for (int i = 0; EnumDisplaySettingsW(dev, i, ref m); i++)
                {
                    if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight
                        && m.dmBitsPerPel == cur.dmBitsPerPel && m.dmDisplayFrequency > best)
                        best = m.dmDisplayFrequency;
                    m = NewDm();
                }
            }
            catch { }
        }

        public static bool HasResidue()
        {
            return Settings.LoadStr(Slot, "").Length > 0;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string s = Settings.LoadStr(Slot, "");
                if (s.Length == 0) return true;

                int cut = s.IndexOf('|');
                int hz = 0;
                if (cut <= 0 || !int.TryParse(s.Substring(0, cut), out hz) || hz <= 0)
                {
                    Settings.SaveStr(Slot, "");
                    return false;
                }
                string dev = s.Substring(cut + 1);
                try
                {
                    DEVMODE cur = NewDm();
                    if (!EnumDisplaySettingsW(dev, ENUM_CURRENT_SETTINGS, ref cur))
                    {
                        Logger.Log("刷新率守护 找不到显示器 " + dev + " 还原推迟到下次重试");
                        return false;
                    }
                    if (cur.dmDisplayFrequency == hz)
                    {
                        Settings.SaveStr(Slot, "");
                        Logger.Log("刷新率守护 当前已是 " + hz + "Hz 游戏或系统已自行切回 无需还原");
                        return Settings.LoadStr(Slot, "").Length == 0;
                    }

                    cur.dmDisplayFrequency = hz;
                    cur.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
                    if (ChangeDisplaySettingsExW(dev, ref cur, IntPtr.Zero, 0, IntPtr.Zero) == 0)
                    {
                        DEVMODE verify = NewDm();
                        if (!EnumDisplaySettingsW(dev, ENUM_CURRENT_SETTINGS, ref verify)
                            || verify.dmDisplayFrequency != hz) return false;
                        Settings.SaveStr(Slot, "");
                        Logger.Log("刷新率守护 已还原 " + hz + "Hz");
                        return Settings.LoadStr(Slot, "").Length == 0;
                    }
                    Logger.Log("刷新率守护 切换失败 快照保留待下次重试");
                    return false;
                }
                catch { return false; }
            }
        }

        private static DEVMODE NewDm()
        {
            var d = new DEVMODE();
            d.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
            return d;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight;
            public int dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
            public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
    }
}
