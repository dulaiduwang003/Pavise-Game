// @author bdth 2074055628@qq.com
// 文件用途 内核模块清单加载与残留会话清理
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

        // 一份新鲜的只读快照同时提供驱动映像的真实路径给版本查询
        // 不要凭名字去猜 DriverStore 里的包
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
            // 池子从 4MB(32×128KB)扩到 32MB 以容纳对局中 DPC/ISR 的短时爆发
            //   旧池耗尽时内核没有空闲缓冲只能丢事件 表现为 EventsLost 高而 BuffersLost 为 0
            //   32MB 约可缓冲 25 万个事件 会话仅在真实对局观测时占用 结束即释放
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
