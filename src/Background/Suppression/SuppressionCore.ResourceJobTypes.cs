using System;

namespace PaviseApp
{
    // 普通后台用 CPU 和磁盘限额 重点压制另外锁定 Idle
    internal sealed class ResourceJobLimits
    {
        // CPU 硬上限 单位是全机 CPU 的万分之一
        internal uint CpuRate;
        internal bool LockIdle;
        // 每卷请求的带宽上限 单位为字节每秒
        internal long IoBandwidth;
    }

    // 资源 Job 的系统调用都走这个接口 产品启动时注入 测试不注入时资源 Job 整体不启用
    internal interface IResourceJobPlatform
    {
        // 打开目标进程 身份不符或打不开返回 IntPtr.Zero
        IntPtr OpenTarget(int pid, long creation);
        bool SameSession(IntPtr process);
        // 新建命名 Job 再把只读句柄放进目标进程当锚点 目标活着 Job 的名字就一直在
        IntPtr CreateAnchored(string name, IntPtr process, out string reason);
        bool Anchor(IntPtr job, IntPtr process, out string reason);
        bool MatchesFocusProgram(IntPtr process, string key);
        uint ReadPriority(IntPtr process);
        bool RestoreIdlePriority(int pid, long creation, uint priority, out bool restored);
        // 按名字重开 失败时 error 是系统错误码 2 表示名字已经不在
        IntPtr Open(string name, out int error);
        // 写入限额并核验成员和限额
        bool Apply(IntPtr job, IntPtr process, ResourceJobLimits limits, out string reason);
        // 解除优先级锁及 CPU 硬上限 保留子进程静默脱离
        bool Clear(IntPtr job, out string reason);
        // 磁盘带宽单独申请 系统接受零值表示解除
        bool SetIoBandwidth(IntPtr job, long bandwidth, out string reason);
        // 0 存活 1 已退出或编号被复用 2 读不到
        int Probe(int pid, long creation);
        void Close(IntPtr handle);
    }
}
