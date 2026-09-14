// @author bdth 2074055628@qq.com
// File purpose Kernel module manifest loading and stale session cleanup
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class InterruptAttribution
    {
        private bool LoadModules(out string error)
        {
            modules.Clear();
            List<Module> loaded;
            if (!TryReadLoadedModules(out loaded, out error)) return false;
            modules.AddRange(loaded);
            return true;
        }

        // A fresh read-only snapshot also supplies the driver images' real paths for version queries;
        // do not guess DriverStore packages by name
        internal static List<string> LoadedModuleImagePaths()
        {
            List<Module> loaded;
            string error;
            if (!TryReadLoadedModules(out loaded, out error)) return null;
            var paths = new List<string>(loaded.Count);
            foreach (Module module in loaded) paths.Add(module.ImagePath);
            return paths;
        }

        private static bool TryReadLoadedModules(out List<Module> loaded, out string error)
        {
            loaded = new List<Module>();
            error = null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                int len = 0;
                NtQuerySystemInformation(11, IntPtr.Zero, 0, out len);
                len = checked(Math.Max(len, 1 << 20) + 65536);
                buf = Marshal.AllocHGlobal(len);
                int ret;
                int status = NtQuerySystemInformation(11, buf, len, out ret);
                if (status != 0)
                {
                    error = "驱动模块枚举失败，无法启动中断归因 NTSTATUS=0x"
                        + unchecked((uint)status).ToString("X8");
                    return false;
                }
                int count = Marshal.ReadInt32(buf);
                long p = buf.ToInt64() + IntPtr.Size;
                int stride = 16 + 8 + 4 + 4 + 2 + 2 + 2 + 2 + 256;
                if (count < 0 || count > (len - IntPtr.Size) / stride)
                {
                    error = "驱动模块枚举返回的长度无效，无法启动中断归因";
                    return false;
                }
                for (int i = 0; i < count; i++)
                {
                    long rec = p + (long)i * stride;
                    ulong imgBase = (ulong)Marshal.ReadInt64(new IntPtr(rec + 16));
                    uint imgSize = (uint)Marshal.ReadInt32(new IntPtr(rec + 24));
                    if (imgBase == 0 || imgSize == 0) continue;
                    string full = Marshal.PtrToStringAnsi(new IntPtr(rec + 40));
                    if (string.IsNullOrEmpty(full)) continue;
                    int slash = full.LastIndexOf('\\');
                    string name = slash >= 0 ? full.Substring(slash + 1) : full;
                    loaded.Add(new Module { Base = imgBase, End = imgBase + imgSize, Name = name, ImagePath = full });
                }
                if (loaded.Count == 0)
                {
                    error = "驱动模块枚举未返回可用地址，无法启动中断归因";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                loaded.Clear();
                error = "驱动模块枚举异常，无法启动中断归因 " + ex.GetType().Name;
                return false;
            }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        private static IntPtr AllocProps(uint enableFlags)
        {
            int nameBytes = (SessionName.Length + 1) * 2;
            int size = Marshal.SizeOf(typeof(EventTraceProperties)) + nameBytes + 16;
            IntPtr props = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(props, i, 0);
            var p = new EventTraceProperties();
            p.Wnode.BufferSize = (uint)size;
            p.Wnode.Flags = WnodeFlagTracedGuid;
            p.Wnode.Guid = SessionGuid;
            p.Wnode.ClientContext = 1;
            // Pool grown from 4MB (32 x 128KB) to 32MB to absorb short DPC and ISR bursts during a match
            //   When the old pool ran dry the kernel had no free buffer and could only drop events, seen as high EventsLost with BuffersLost at 0
            //   32MB buffers roughly 250 thousand events; the session is only held during real match observation and released on end
            p.BufferSize = 128;
            p.MinimumBuffers = 64;
            p.MaximumBuffers = 256;
            p.LogFileMode = RealTimeMode | SystemLoggerMode | IndependentSessionMode;
            p.FlushTimer = 1;
            p.EnableFlags = enableFlags;
            p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
            Marshal.StructureToPtr(p, props, false);
            return props;
        }

        public static void CleanupStaleSession()
        {
            try { StopStale(); } catch { }
        }

        public static void HealFromCrash() { StopStale(); }

        private static void StopStale()
        {
            uint lost, buffers, error;
            StopStale(out lost, out buffers, out error);
        }
    }
}
