using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 核心类型备用探测用的 CPUID 读取 源码在 src/Native/PaviseCpu.cpp
    // 正式构建定义了 PAVISE_EMBEDDED_NATIVE 导出就在 Pavise.exe 主模块里 不写出也不加载任何文件
    // 测试构建只有 csc 没有链接这一步 把 NativeCpuImage.cs 里的 DLL 落盘后加载 落盘与核对见 NativeImageFile
    // 取不到导出时不用别的办法执行 CPUID 由调用方把结果记为未知
    internal static class NativeCpu
    {
        internal const string FileName = "PaviseCpu.dll";
        internal const int Abi = 1;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AbiFunction();
        // 输出数组固定四个元素 依次是 EAX EBX ECX EDX
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CpuidFunction(uint leaf, uint subleaf, [Out] uint[] registers);

        private static readonly object gate = new object();
        private static bool attempted;
        private static CpuidFunction cpuid;
        private static string status = NativeEtw.LoadReason("未尝试", "not attempted");

        internal static string Status { get { lock (gate) return NativeEtw.LoadReasonText(status); } }

        // 只尝试一次 失败的原因留在 Status 里
        internal static CpuidFunction Cpuid
        {
            get
            {
                lock (gate)
                {
                    if (!attempted)
                    {
                        attempted = true;
                        string reason;
                        cpuid = Load(out reason);
                        status = reason;
                        Logger.Info(Logger.Format("CPUID 原生读取：{0}", "CPUID native read: {0}", NativeEtw.LoadReasonText(reason)));
                    }
                    return cpuid;
                }
            }
        }

        private static CpuidFunction Bind(IntPtr module, out string reason)
        {
            IntPtr abiAddress = GetProcAddress(module, "PaviseCpuAbi");
            IntPtr cpuidAddress = GetProcAddress(module, "PaviseCpuidEx");
            if (abiAddress == IntPtr.Zero || cpuidAddress == IntPtr.Zero) { reason = NativeEtw.LoadReason("原生组件缺少 CPUID 导出", "native component lacks CPUID exports"); return null; }
            int version = ((AbiFunction)Marshal.GetDelegateForFunctionPointer(abiAddress, typeof(AbiFunction)))();
            if (version != Abi) { reason = NativeEtw.LoadReason("CPUID 原生接口版本 " + version + " 与程序的 " + Abi + " 不符", "CPUID native ABI version " + version + " vs program " + Abi + " (mismatch)"); return null; }
            reason = "";
            return (CpuidFunction)Marshal.GetDelegateForFunctionPointer(cpuidAddress, typeof(CpuidFunction));
        }

#if PAVISE_EMBEDDED_NATIVE
        private static CpuidFunction Load(out string reason)
        {
            try
            {
                if (IntPtr.Size != 8) { reason = NativeEtw.LoadReason("32 位进程", "32-bit process"); return null; }
                IntPtr module = GetModuleHandleW(null);
                if (module == IntPtr.Zero) { reason = NativeEtw.LoadReason("读不到主模块", "main module unreadable"); return null; }
                CpuidFunction loaded = Bind(module, out reason);
                if (loaded != null) reason = NativeEtw.LoadReason("已加载 内置", "loaded built-in");
                return loaded;
            }
            catch (Exception ex)
            {
                reason = NativeEtw.LoadReason("CPUID 原生读取准备失败 " + ex.GetType().Name, "CPUID native read setup failed " + ex.GetType().Name);
                return null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string name);
#else
        private static CpuidFunction Load(out string reason)
        {
            try
            {
                if (IntPtr.Size != 8) { reason = NativeEtw.LoadReason("32 位进程", "32-bit process"); return null; }
                string path;
                using (FileStream held = NativeImageFile.Prepare(NativeCpuImage.Base64, NativeCpuImage.Length, NativeCpuImage.Sha256, FileName, out path, out reason))
                {
                    if (held == null) return null;
                    // 依赖只从系统目录取 文件在核对后一直禁止改写 直到映射完成
                    IntPtr module = LoadLibraryExW(path, IntPtr.Zero, LoadLibrarySearchSystem32);
                    if (module == IntPtr.Zero) { int loadError = Marshal.GetLastWin32Error(); reason = NativeEtw.LoadReason("原生组件加载失败 code=" + loadError, "native component load failed code=" + loadError); return null; }
                    CpuidFunction loaded = Bind(module, out reason);
                    if (loaded == null) { FreeLibrary(module); return null; }
                    reason = NativeEtw.LoadReason("已加载 " + NativeCpuImage.Sha256.Substring(0, 16), "loaded " + NativeCpuImage.Sha256.Substring(0, 16));
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                reason = NativeEtw.LoadReason("CPUID 原生读取准备失败 " + ex.GetType().Name, "CPUID native read setup failed " + ex.GetType().Name);
                return null;
            }
        }

        private const uint LoadLibrarySearchSystem32 = 0x00000800;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);
#endif

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
