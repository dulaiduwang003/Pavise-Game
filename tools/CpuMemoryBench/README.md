# CPU 与内存台架

独立于 Pavise 应用入口运行的固定负载台架，用于核对计量与亲和性恢复，并在符合条件的设备上比较前后台线程的核心放置。需要 Windows x64、Python 3 和系统 .NET Framework C# 编译器，使用 Python 标准库，无需安装驱动或管理员权限。

```powershell
python tools/CpuMemoryBench/Run.py --mode probe
python tools/CpuMemoryBench/Run.py --mode smoke
python tools/CpuMemoryBench/Run.py --mode pilot
python tools/CpuMemoryBench/Run.py --mode run
python tools/CpuMemoryBench/Analyze.py tools/CpuMemoryBench/results/<结果目录>
```

| 模式 | 行为与条件 |
| --- | --- |
| `probe` | 读取拓扑和能力。普通同构 CPU 也可运行；不支持受控 P/E 实验时记录原因。 |
| `smoke` | 在一个可用核心上运行一组短时自有负载，核验计量及亲和性应用、读回、恢复。支持单处理器组内的同构或混合 CPU。 |
| `pilot` | 快速运行六组受控摆放，要求已确认的 P/E 拓扑、至少六个 P 核、P 核 SMT 和四个独立 E 核 L2 组。 |
| `run` | 在相同拓扑条件下运行完整 198 组实验，记录顺序、原始计时和源码哈希。 |

每次创建新的 `results/` 子目录；也可用 `--run-dir C:/path/to/new-results` 指定新目录。每个子进程都有超时，失败停止并保留结果，不自动重试。子进程保持隐藏，亲和性变更仅作用于台架自有线程；运行前后核对产品源码、设置摘要和活动电源方案。原始结果可能包含设备与系统信息，请检查后再分享。

`Analyze.py` 从受控实验的原始数据重新计算统计。`probe` 不产生负载结果，`smoke` 用于验证计量链路，均不用于给出 A/B 性能收益结论。预取器项目只做能力探测，没有经过验证的特权后端时不读取或写入 MSR。固定负载的吞吐结果不能直接解释为游戏帧率收益。

`CpuMemoryBench.cs` 实现台架负载，`Run.py` 负责隐藏启动并记录源码哈希与运行参数。
