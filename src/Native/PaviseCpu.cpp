// Pavise 的 CPUID 读取
// Windows 报告所有核同一效率等级时 CpuCoreTypeProbe 要逐个逻辑处理器读 CPUID 0x1A 叶确认核心类型
// 绑核 核对处理器编号 时间预算和恢复亲和性都在 C# 里 这里只执行一条 CPUID 指令并交回四个寄存器
// 不链接 C 运行库 不导入任何 DLL 正式构建由 build.cmd 把目标文件链进 Pavise.exe
// 测试构建把同一份代码编成 DLL 从 NativeCpuImage.cs 落盘加载
// 构建与生成内嵌镜像见 tools/Build-Native.ps1

#include <stdint.h>
#include <cpuid.h>

extern "C"
{
    // 编成 DLL 时的入口 链进 exe 时不会被调用 名字和 PaviseEtw.cpp 的入口分开 两个目标文件才能一起链接
    int PaviseCpuDllMain(void*, unsigned long, void*) { return 1; }

    // 接口版本 改动导出的参数或含义时加一 C# 侧按这个值核对
    __declspec(dllexport) int32_t PaviseCpuAbi() { return 1; }

    // 在调用线程当前所在的处理器上执行 CPUID 依次写出 EAX EBX ECX EDX
    __declspec(dllexport) void PaviseCpuidEx(uint32_t leaf, uint32_t subleaf, uint32_t* out4)
    {
        uint32_t eax = 0, ebx = 0, ecx = 0, edx = 0;
        __cpuid_count(leaf, subleaf, eax, ebx, ecx, edx);
        out4[0] = eax;
        out4[1] = ebx;
        out4[2] = ecx;
        out4[3] = edx;
    }
}
