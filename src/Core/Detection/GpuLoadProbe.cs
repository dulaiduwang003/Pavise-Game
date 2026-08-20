// @author bdth 2074055628@qq.com
// 文件用途 只读取整卡 GPU 利用率 显存与温度 N 卡走 NVAPI A 卡走 ADLX 取不到的项一律留 -1
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class GpuReading
    {
        public double Percent = -1;
        public double TempC = -1;
        public double VramUsedMb = -1;
        public double VramTotalMb = -1;
    }

    internal static class GpuLoadProbe
    {
        private const uint IdInitialize = 0x0150E828;
        private const uint IdEnumPhysicalGpus = 0xE5AC921F;
        private const uint IdDynamicPstatesEx = 0x60DED2ED;
        private const uint IdMemoryInfo = 0x07F9B368;
        private const uint IdThermalSettings = 0xE3640A56;

        private const int MaxPhysicalGpus = 64;
        private const int Domains = 8;
        private const int PstateBytes = 4 + 4 + Domains * 8;
        private const int DomainGpu = 0;

        // V3 一共八个 NvU32 version 自己就是第一个 别再在外面多加一个 4
        private const int MemV3Fields = 8;
        private const int MemBytes = MemV3Fields * 4;
        private const int ThermalSensors = 3;
        private const int ThermalSensorBytes = 20;
        private const int ThermalBytes = 4 + 4 + ThermalSensors * ThermalSensorBytes;
        private const int ThermalIndexAll = 15;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnVoid();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnEnum([Out] IntPtr[] handles, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnPstates(IntPtr gpu, IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnMemory(IntPtr gpu, IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int FnThermal(IntPtr gpu, uint sensorIndex, IntPtr info);

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface64(uint id);

        [DllImport("nvapi.dll", EntryPoint = "nvapi_QueryInterface",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface32(uint id);

        private static int nvState;
        private static FnEnum nvEnum;
        private static FnPstates nvPstates;
        private static FnMemory nvMemory;
        private static FnThermal nvThermal;
        private static IntPtr nvGpu;

        private static int amdState;
        private static IntPtr[] amdGpus;

        internal static string Route { get; private set; }

        public static void Forget()
        {
            Volatile.Write(ref nvState, 0);
            Volatile.Write(ref amdState, 0);
            nvEnum = null; nvPstates = null; nvMemory = null; nvThermal = null;
            nvGpu = IntPtr.Zero;
            if (amdGpus != null) { try { AdlxApi.ReleaseAll(amdGpus); } catch { } amdGpus = null; }
            Route = null;
        }

        // 每一项都可能单独取不到 取不到就留 -1 调用方不要把 -1 当成 0 画出来
        public static GpuReading Read()
        {
            var r = new GpuReading();
            if (ReadNvidia(r)) { Route = "NVAPI"; return r; }
            if (ReadAmd(r)) { Route = "ADLX"; return r; }
            Route = null;
            return r;
        }

        private static IntPtr Resolve(uint id)
        {
            try { return IntPtr.Size == 8 ? QueryInterface64(id) : QueryInterface32(id); }
            catch { return IntPtr.Zero; }
        }

        private static bool ReadNvidia(GpuReading r)
        {
            if (Volatile.Read(ref nvState) < 0) return false;
            if (Volatile.Read(ref nvState) == 0 && !ProbeNvidia()) return false;
            if (nvGpu == IntPtr.Zero) return false;

            bool any = false;
            if (nvPstates != null && NvPercent(r)) any = true;
            if (nvMemory != null && NvMemory(r)) any = true;
            if (nvThermal != null && NvThermal(r)) any = true;
            return any;
        }

        private static bool NvPercent(GpuReading r)
        {
            IntPtr mem = Marshal.AllocHGlobal(PstateBytes);
            try
            {
                Zero(mem, PstateBytes);
                Marshal.WriteInt32(mem, 0, unchecked((int)(PstateBytes | (1u << 16))));
                if (nvPstates(nvGpu, mem) != 0) return false;
                if ((Marshal.ReadInt32(mem, 8 + DomainGpu * 8) & 1) == 0) return false;
                int pct = Marshal.ReadInt32(mem, 8 + DomainGpu * 8 + 4);
                if (pct < 0) return false;
                r.Percent = pct > 100 ? 100 : pct;
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(mem); }
        }

        private static bool NvMemory(GpuReading r)
        {
            IntPtr mem = Marshal.AllocHGlobal(MemBytes);
            try
            {
                Zero(mem, MemBytes);
                Marshal.WriteInt32(mem, 0, unchecked((int)(MemBytes | (3u << 16))));
                if (nvMemory(nvGpu, mem) != 0) return false;
                // 字段单位是 KB 依次为 专用显存 可用专用 系统显存 共享系统 当前可用专用
                long dedicatedKb = (uint)Marshal.ReadInt32(mem, 4);
                long freeKb = (uint)Marshal.ReadInt32(mem, 20);
                if (dedicatedKb <= 0 || freeKb < 0 || freeKb > dedicatedKb) return false;
                r.VramTotalMb = dedicatedKb / 1024.0;
                r.VramUsedMb = (dedicatedKb - freeKb) / 1024.0;
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(mem); }
        }

        private static bool NvThermal(GpuReading r)
        {
            IntPtr mem = Marshal.AllocHGlobal(ThermalBytes);
            try
            {
                Zero(mem, ThermalBytes);
                Marshal.WriteInt32(mem, 0, unchecked((int)(ThermalBytes | (2u << 16))));
                if (nvThermal(nvGpu, ThermalIndexAll, mem) != 0) return false;
                int count = Marshal.ReadInt32(mem, 4);
                if (count <= 0 || count > ThermalSensors) return false;
                // 每个传感器 20 字节 currentTemp 在第 12 字节
                int temp = Marshal.ReadInt32(mem, 8 + 12);
                if (temp <= 0 || temp > 150) return false;
                r.TempC = temp;
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(mem); }
        }

        private static void Zero(IntPtr mem, int bytes)
        {
            for (int i = 0; i < bytes; i++) Marshal.WriteByte(mem, i, 0);
        }

        private static bool ProbeNvidia()
        {
            try
            {
                IntPtr init = Resolve(IdInitialize);
                if (init == IntPtr.Zero) { Volatile.Write(ref nvState, -1); return false; }
                var fnInit = (FnVoid)Marshal.GetDelegateForFunctionPointer(init, typeof(FnVoid));
                if (fnInit() != 0) { Volatile.Write(ref nvState, -1); return false; }

                IntPtr e = Resolve(IdEnumPhysicalGpus);
                if (e == IntPtr.Zero) { Volatile.Write(ref nvState, -1); return false; }
                nvEnum = (FnEnum)Marshal.GetDelegateForFunctionPointer(e, typeof(FnEnum));

                IntPtr p = Resolve(IdDynamicPstatesEx);
                if (p != IntPtr.Zero)
                    nvPstates = (FnPstates)Marshal.GetDelegateForFunctionPointer(p, typeof(FnPstates));
                IntPtr m = Resolve(IdMemoryInfo);
                if (m != IntPtr.Zero)
                    nvMemory = (FnMemory)Marshal.GetDelegateForFunctionPointer(m, typeof(FnMemory));
                IntPtr t = Resolve(IdThermalSettings);
                if (t != IntPtr.Zero)
                    nvThermal = (FnThermal)Marshal.GetDelegateForFunctionPointer(t, typeof(FnThermal));
                if (nvPstates == null && nvMemory == null && nvThermal == null)
                {
                    Volatile.Write(ref nvState, -1);
                    return false;
                }

                var handles = new IntPtr[MaxPhysicalGpus];
                uint count;
                if (nvEnum(handles, out count) != 0 || count == 0)
                {
                    Volatile.Write(ref nvState, -1);
                    return false;
                }
                nvGpu = handles[0];
                Volatile.Write(ref nvState, 1);
                return true;
            }
            catch { Volatile.Write(ref nvState, -1); return false; }
        }

        private static bool ReadAmd(GpuReading r)
        {
            if (Volatile.Read(ref amdState) < 0) return false;
            try
            {
                if (Volatile.Read(ref amdState) == 0)
                {
                    if (!AdlxApi.Available) { Volatile.Write(ref amdState, -1); return false; }
                    amdGpus = AdlxApi.GetGpus();
                    if (amdGpus == null || amdGpus.Length == 0)
                    {
                        Volatile.Write(ref amdState, -1);
                        return false;
                    }
                    Volatile.Write(ref amdState, 1);
                }
                double usage, temp, watts; int clock, vram;
                if (!AdlxApi.TryReadMetrics(amdGpus[0], out usage, out clock, out temp, out watts, out vram))
                    return false;
                if (usage >= 0) r.Percent = usage > 100 ? 100 : usage;
                if (temp > 0 && temp < 150) r.TempC = temp;
                if (vram > 0) r.VramUsedMb = vram;
                return true;
            }
            catch { Volatile.Write(ref amdState, -1); return false; }
        }
    }
}
