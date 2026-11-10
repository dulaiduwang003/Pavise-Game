# 实验与验证

Pavise 开发期间的机制验证、运行开销对照和独立台架方法。

| 专题 | 核对的问题 | 证据与入口 |
| --- | --- | --- |
| [微软后台治理](windows-background.md) | 诊断、索引与小组件是否被正确识别、纳管和恢复 | 本机原生实验、生产策略回归 |
| [Job 核心范围与 CPU 配额](job-limits.md) | 限制是否实际存在，能否阻止目标自行扩大范围 | Job／进程双向读回、自有负载校准 |
| [异常退出与恢复](recovery.md) | 失败后是否保留原值、所有权和有限重试边界 | 生产方法、受控故障和独立守护验证 |
| [CPU 拓扑与核心类型](cpu-topology.md) | 核心分类与线程关系是否有完整证据 | 拓扑夹具、原生只读与自有线程验证 |
| [线程巡检与恢复开销](runtime-overhead.md) | 减少重复查询和持久化后是否仍保持恢复约束 | 同一自有线程负载的前后对照 |
| [CPU 与内存独立台架](cpu-memory-bench.md) | 如何执行固定负载和受控落核比较 | 仓库内可运行台架与分析脚本 |

## 数据与版本

数据记录于 2026-11-07 至 2026-11-10，对应 3.3.0.8 的开发快照。构建身份和汇总来源见[数据说明](data/README.md)。

## 运行验证

在 Windows x64 上，使用系统 .NET Framework 编译器及 Windows PowerShell 5.1：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Run-PublicTests.ps1
```

各检查的实际隔离范围见[测试说明](../../tests/README.md)。独立 CPU／内存台架还需要 Python 3。

各专题说明测量对象、运行条件和结论范围。限额读回、恢复核验、固定负载与游戏实测采用不同指标，结果按对应场景解读。
