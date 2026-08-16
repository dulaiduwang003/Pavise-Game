// @author bdth 2074055628@qq.com
// 文件用途 只读查询主显示器刷新率 并还原旧版刷新率守护留下的残留

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class DisplayGuard
    {
        private const string Slot = "PrevRefreshHz";
        private static readonly object lk = new object();

        private const int ENUM_CURRENT_SETTINGS = -1;

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

        internal static int MaxRefreshRate()
        {
            int max = 0;
            foreach (int hz in AllRefreshRates()) if (hz > max) max = hz;
            return max > 0 ? max : CurrentRefreshRate();
        }

        internal static List<int> AllRefreshRates()
        {
            var rates = new List<int>();
            try
            {
                foreach (Screen s in Screen.AllScreens)
                {
                    DEVMODE cur = NewDm();
                    if (EnumDisplaySettingsW(s.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)
                        && cur.dmDisplayFrequency > 1)
                        rates.Add(cur.dmDisplayFrequency);
                }
            }
            catch { }
            return rates;
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

        // 旧版守护用动态改模式(不写注册表)提升刷新率 重启即自然失效
        // 残留槽存的是当年的低刷新率 事后写回只会把换过显示器或自行调过刷新率的用户拉回低刷
        // 故迁移只弃槽 不再动显示器
        public static bool Restore()
        {
            lock (lk)
            {
                string s = Settings.LoadStr(Slot, "");
                if (s.Length == 0) return true;
                Settings.SaveStr(Slot, "");
                Logger.Log(Lang.T("log.displayguard.7"));
                return Settings.LoadStr(Slot, "").Length == 0;
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
    }
}
