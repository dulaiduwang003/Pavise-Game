# 设备中断台架

设备中断这套功能的回归检查 观测账本 加权核负载 候选核方案 手动调整 验证和恢复

```powershell
./tools/IrqLoadBench/Run.ps1
```

用当前生产源码加一份明确的测试清单 编到临时目录 IRQ 各组测试跑三遍 再用 12 线程和 64 线程的假拓扑 把真实的设备页和选核对话框在 100% 到 300% 缩放 中英文 明暗主题下各画一遍 日志 截图和输入哈希留在报告目录 不采 ETW 不采 PDH 不写设备 不动注册表

## 账本格式 V5

保留 V3 的会话 驱动行和 V4 的负载行 新增

- `B|cpu|busyTicks` 利用率 80% 以上的时间
- `K|driver|cpu|count|totalNs|maxNs|over500|over1ms|badDuration|1|buckets` 每个驱动在每个核上的 DPC 分布
- `M|gameId|configuration|scene` 游戏身份和策略拓扑指纹
- `Q|deviceId|configuration` 设备的策略 掩码 优先级和包版本
- `F` Present 计数 活跃秒数 P99 P99.9 身份可靠度

旧账本追加时自动升级

## 测的规则

候选方案最多合并五局 游戏 掩码 拓扑 启动 驱动版本和设备配置都要对得上 三局以上才标多局 候选核的每个物理核兄弟都要采样覆盖 80% 以上 平均负载 60% 以下 忙碌 20% 以下 游戏核和它的 SMT 兄弟排除

验证要两边各三局以上 比游戏核慢 DPC 频率 帧 P99 P99.9 长帧频率和目标核负载 慢 DPC P99 长帧都降 10% 以上 P99.9 和目标负载不变差 才算改善

每次调整记一份 irq-adjustment-<id>.xml 身份 新旧配置 结果 基线样本和验证状态 恢复结果按设备按项各记各的

参考 [中断亲和与优先级](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/interrupt-affinity-and-priority) [处理器组](https://learn.microsoft.com/en-us/windows/win32/procthread/processor-groups)
