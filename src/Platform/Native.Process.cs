// @author bdth 2074055628@qq.com
// 文件用途 进程采样 IO 与页面优先级 映像路径与父进程
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters counters);

        public static bool QueryProcessSample(IntPtr h, out long creation, out long cpu, out ulong io)
        {
            creation = cpu = 0; io = 0;
            long exit, kernel, user;
            if (!GetProcessTimes(h, out creation, out exit, out kernel, out user)) return false;
            cpu = kernel + user;
            IoCounters c;
            if (GetProcessIoCounters(h, out c)) io = c.ReadTransferCount + c.WriteTransferCount;
            return true;
        }

        public static ulong QueryAffinity(IntPtr h)
        {
            ulong mask;
            return TryQueryAffinity(h, out mask) ? mask : 0UL;
        }

        // 读失败和读到 0 必须分得开 调用方拿读失败当"落点不对"会误撤销已生效的隔离
        public static bool TryQueryAffinity(IntPtr h, out ulong mask)
        {
            UIntPtr pm, sm;
            bool ok = GetProcessAffinityMask(h, out pm, out sm);
            mask = ok ? (ulong)pm : 0UL;
            return ok;
        }
        public static int QueryIoPriority(IntPtr h)
        {
            int v = 0;
            return NtQueryInformationProcess(h, ProcessIoPriorityNt, ref v, 4, IntPtr.Zero) == 0 ? v : -1;
        }
        public static int QueryPagePriority(IntPtr h)
        {
            int v = 0;
            return NtQueryInformationProcess(h, ProcessPagePriorityNt, ref v, 4, IntPtr.Zero) == 0 ? v : -1;
        }

        public static bool TrySetIoPriority(IntPtr process, int priority, out int status)
        {
            status = NtSetInformationProcess(process, ProcessIoPriorityNt, ref priority, sizeof(int));
            return status == 0;
        }

        public static bool TrySetIoPriority(IntPtr process, int priority)
        {
            int status;
            return TrySetIoPriority(process, priority, out status);
        }

        public static bool TrySetPagePriority(IntPtr process, int priority)
        {
            return NtSetInformationProcess(process, ProcessPagePriorityNt, ref priority, sizeof(int)) == 0;
        }

        public static string ImagePath(IntPtr h)
        {
            try
            {
                int cap = 600;
                var sb = new System.Text.StringBuilder(cap);
                if (!QueryFullProcessImageName(h, 0, sb, ref cap))
                {
                    cap = 32768;
                    sb = new System.Text.StringBuilder(cap);
                    if (!QueryFullProcessImageName(h, 0, sb, ref cap)) return null;
                }
                string path = sb.ToString();
                return path.Length == 0 ? null : path;
            }
            catch { return null; }
        }

        public static bool StillActive(IntPtr h)
        {
            uint code;
            if (!GetExitCodeProcess(h, out code)) return true;
            return code == 259;
        }

        public static string ImageName(IntPtr h)
        {
            string path = ImagePath(h);
            if (path == null) return null;
            int slash = path.LastIndexOf('\\');
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file.Substring(0, file.Length - 4);
            return file;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        public static int ParentProcessId(IntPtr processHandle)
        {
            try
            {
                var info = new PROCESS_BASIC_INFORMATION();
                int size = Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION));
                return NtQueryInformationProcessBasic(processHandle, 0, ref info, size, IntPtr.Zero) == 0
                    ? info.InheritedFromUniqueProcessId.ToInt32() : 0;
            }
            catch { return 0; }
        }

        [DllImport("gdi32.dll")] public static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr h, out int cls);
        [DllImport("gdi32.dll")] public static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr h, int cls);

        // WDDM 显存查询与预留 全部走 gdi32 的用户态 D3DKMT 接口 不装驱动 不注入 不读游戏内存
        //   MemorySegmentGroup 0 是本地显存 1 是非本地(系统内存那份) 护盾只关心 0
        //   注意 Query 的 hProcess 是 HANDLE 而 Change 的 hProcess 在头文件里是 UINT64
        //     x64 上宽度一样 但类型照抄头文件 免得将来有人按 HANDLE 去改 Change 那个
        public const uint D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL = 0;
    }
}
