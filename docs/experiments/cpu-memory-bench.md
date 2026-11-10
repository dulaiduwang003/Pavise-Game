# CPU 与内存独立台架方法

台架通过固定负载检查计量、亲和性应用与恢复，并在符合条件的设备上比较前后台线程的核心放置。源码和脚本位于 [tools/CpuMemoryBench](../../tools/CpuMemoryBench/README.md)，独立于 Pavise 应用入口，不需要驱动或管理员权限。

## 环境与运行

需要 Windows x64、Python 3 和系统 .NET Framework C# 编译器；Python 部分只使用标准库。

```powershell
python tools/CpuMemoryBench/Run.py --mode probe
python tools/CpuMemoryBench/Run.py --mode smoke
python tools/CpuMemoryBench/Run.py --mode pilot
python tools/CpuMemoryBench/Run.py --mode run
python tools/CpuMemoryBench/Analyze.py tools/CpuMemoryBench/results/<结果目录>
```

| 模式 | 目标与准入 |
| --- | --- |
| `probe` | 检查拓扑、计时和能力；不产生负载比较结论 |
| `smoke` | 在一个可用核心运行短时自有负载，核验计量及亲和性往返 |
| `pilot` | 快速运行六组受控摆放；要求已确认的 P/E 拓扑及指定核心数量、SMT 与缓存结构 |
| `run` | 在相同拓扑条件下执行完整 198 组实验，记录顺序和原始计时 |

普通同构 CPU 可以运行 `probe` 和符合条件的 `smoke`。受控 P/E 实验的具体准入以台架说明和实际探测结果为准，不能为凑齐实验组而猜测核心类型。

## 对照与计量

运行前确认机器供电、温度和后台负载，保持驱动、电源策略与源代码一致。每次创建全新结果目录，保存运行参数、源码哈希、顺序和原始样本，再用 `Analyze.py` 重算统计。比较前先确认每组均实际执行并通过恢复核验，不能把跳过或失败样本当作零耗时。

亲和性修改只作用于台架自己的线程；运行前后核对产品源码、设置摘要和活动电源方案。子进程隐藏并具有时间上限，失败即停止并保留记录，不自动重试。

没有经过验证的特权后端时，预取器项目只做能力探测，不读写 MSR。`smoke` 验证计量链路，不用于产生 A/B 收益结论；完整固定负载的吞吐也不能直接换算为游戏帧率。

完整实验的结果应包含 198 组执行状态、运行参数、原始计时和恢复核验，并附对应机器的配置与运行记录，便于复现。
