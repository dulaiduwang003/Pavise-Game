# CPU 与内存台架

独立跑的小台架 不接 Pavise 正式功能 测两件事

- 硬件预取器能不能调 目前只做能力探测 没有特权驱动就不动
- 后台线程放到不同的核 P 核 E 核 各组 L2 前台快多少 后台慢多少

要 Windows x64 Python 3 和系统自带的 .NET Framework 编译器

```powershell
python tools/CpuMemoryBench/Run.py --mode probe
python tools/CpuMemoryBench/Run.py --mode pilot
python tools/CpuMemoryBench/Run.py --mode run
python tools/CpuMemoryBench/Analyze.py tools/CpuMemoryBench/results/<结果目录>
```

probe 只探测能力 pilot 快速试六种摆法 run 跑完整 198 组 每次新建一个结果目录 原始计时 拓扑 顺序 哈希都在里面 Analyze.py 从原始数据重新算一遍

TEST-PROTOCOL.md 写死了负载 顺序和达标线 CpuMemoryBench.cs 是原生台架 Run.py 负责隐藏启动和留证
