// @author bdth 2074055628@qq.com
// 文件用途 开启 关闭并恢复硬件加速 GPU 调度 状态优先读注册表配置 缺省时按驱动能力判定

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class HagsTweak
    {
        private const string GfxKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        private const string Val = "HwSchMode";

        private static readonly ReversibleReg Sch = new ReversibleReg(
            Registry.LocalMachine, GfxKey, Val, RegistryValueKind.DWord, "PrevHwSch");

        [StructLayout(LayoutKind.Sequential)]
        private struct AdapterInfo
        {
            public uint HAdapter;
            public long Luid;
            public uint NumOfSources;
            public int PresentMoveRegionsPreferred;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EnumAdapters
        {
            public uint NumAdapters;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public AdapterInfo[] Adapters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QueryAdapterInfo
        {
            public uint HAdapter;
            public uint Type;
            public IntPtr PrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CloseAdapter
        {
            public uint HAdapter;
        }

        private const uint KmtQueryWddm27Caps = 70;

        [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters(ref EnumAdapters args);
        [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo args);
        [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter args);

        public static bool EnabledByPavise { get { return Settings.Load("HagsOnByPavise", false); } }

        public static bool TryQueryState(out bool supported, out bool enabled)
        {
            supported = false;
            enabled = false;
            var list = new EnumAdapters { Adapters = new AdapterInfo[16] };
            if (D3DKMTEnumAdapters(ref list) != 0 || list.NumAdapters == 0) return false;
            bool any = false;
            IntPtr buf = Marshal.AllocHGlobal(4);
            try
            {
                for (int i = 0; i < list.NumAdapters && i < 16; i++)
                {
                    var query = new QueryAdapterInfo
                    {
                        HAdapter = list.Adapters[i].HAdapter,
                        Type = KmtQueryWddm27Caps,
                        PrivateDriverData = buf,
                        PrivateDriverDataSize = 4
                    };
                    if (D3DKMTQueryAdapterInfo(ref query) == 0)
                    {
                        any = true;
                        uint caps = (uint)Marshal.ReadInt32(buf);
                        if ((caps & 1u) != 0) supported = true;
                        if ((caps & 2u) != 0) enabled = true;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                for (int i = 0; i < list.NumAdapters && i < 16; i++)
                {
                    var close = new CloseAdapter { HAdapter = list.Adapters[i].HAdapter };
                    D3DKMTCloseAdapter(ref close);
                }
            }
            return any;
        }

        internal static int? ConfiguredMode()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(GfxKey))
                {
                    if (k == null) return null;
                    object v = k.GetValue(Val);
                    return v is int ? (int?)(int)v : null;
                }
            }
            catch { return null; }
        }

        public static bool CurrentlyOn()
        {
            int? mode = ConfiguredMode();
            if (mode.HasValue) return mode.Value == 2;
            bool supported, enabled;
            return TryQueryState(out supported, out enabled) && enabled;
        }

        public static bool Enable()
        {
            try
            {
                if (!Sch.Apply(2))
                {
                    Logger.Log("GPU 硬件调度（HAGS）写入或回读失败，未标记为已开启");
                    return false;
                }
                Settings.Save("HagsOnByPavise", true);
                if (!Settings.Load("HagsOnByPavise", false))
                {
                    Sch.Restore();
                    Logger.Log("HAGS 状态标志无法持久化，已还原注册表修改");
                    return false;
                }
                Logger.Log("GPU 硬件调度（HAGS）已开启，重启后生效");
                return true;
            }
            catch { return false; }
        }

        public static bool Disable()
        {
            try
            {

                bool ok = Sch.HasBackup ? Sch.Restore() : Sch.Apply(1);
                if (ok && CurrentlyOn()) ok = Sch.Apply(1);
                if (ok)
                {
                    Settings.Save("HagsOnByPavise", false);
                    if (Settings.Load("HagsOnByPavise", true)) return false;
                    Logger.Log("GPU 硬件调度（HAGS）已关闭，重启后生效");
                }
                return ok;
            }
            catch { return false; }
        }

    }
}
