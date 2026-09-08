# CPU / memory isolated benchmarks

针对两个设想：硬件预取器调参；CPU 核心布局与缓存、内存争抢优化。

本目录是独立台架，不接入 Pavise 正式功能。不会访问游戏、注入 DLL、安装驱动、请求提权或修改全局电源/注册表设置。测试程序编译为没有界面的 WinExe，运行器对所有子进程使用 `CREATE_NO_WINDOW`，标准输入关闭，并监测自建子进程是否出现可见窗口。

在 Windows x64、Python 3 和系统自带 .NET Framework 编译器可用时，用终端执行以下命令。程序不会自行打开终端或报告窗口；如需连发起命令的终端也隐藏，应由隐藏的任务宿主启动 Python。

```powershell
python tools/CpuMemoryBench/Run.py --mode probe
python tools/CpuMemoryBench/Run.py --mode pilot
python tools/CpuMemoryBench/Run.py --mode run
python tools/CpuMemoryBench/Analyze.py tools/CpuMemoryBench/results/<run-directory>
```

每次运行创建新的结果目录，拒绝覆盖已有证据。`probe` 只做能力审计；`pilot` 验证六种布局；`run` 按预先冻结的 198 组计划运行。完整台架要求能从 Windows 验证 P/E 两种核心、SMT 和四个 E 核 L2 分组，其他机器不会靠猜测硬套拓扑。

硬件预取器实测需要经过验证、按具体 CPU 型号区分的特权访问后端。本实现没有该后端，只输出能力审计和 `NOT_TESTED_NO_PRIVILEGED_BACKEND`；不会把未更改预取器设置的内存负载包装成 A/B 测试。

`TEST-PROTOCOL.md` 固定工作负载、顺序、判据与限制；`CpuMemoryBench.cs` 是原生内存和线程台架；`Run.py` 负责隐藏启动、隔离与留证；`Analyze.py` 从原始计时重新计算并验证全部数据。结果包括原始批次 CSV、每组 JSON、拓扑、运行顺序、源文件与数据哈希、隔离验证及完整对照表。

这里的吞吐与尾延迟来自合成工作批次，不是游戏 FPS、游戏帧时间或 1% low。台架不提供 CPU 温度、频率或硬件缓存未命中计数，因此不能只根据跑分把变化归因于某一级缓存。迁移到 E 核的收益必须与后台吞吐损失一起解读。
