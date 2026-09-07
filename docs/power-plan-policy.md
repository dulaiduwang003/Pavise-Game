# 极限电源策略边界与回归

2026-09-05。此页描述代码策略，不代表游戏帧率或端到端延迟的实测结论。

## 空闲状态

- 撤掉极限档的 `IDLEDEMOTE=0` 新写入，保留原方案的降级阈值。
- 微软定义为“空闲比例低于阈值时，选择更浅的空闲状态”。因此不能把 0 解释成更快退出。
- 目前仍保留 `IDLEPROMOTE=100` 和 `IDLESCALING=0`；这是状态选择策略，不是禁止 CPU 空闲，也不保证更短的硬件唤醒时延。
- `IDLESCALING` 涉及按当前性能状态缩放阈值，不是任意的“负载缩放”。

来源：[IdleDemoteThreshold](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-idledemotethreshold)、[IdlePromoteThreshold](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-idlepromotethreshold)、[IIdleConfiguration](https://learn.microsoft.com/en-us/dotnet/api/microsoft.windows.eventtracing.power.iidleconfiguration)。

## 最低性能值：请求与平台证据分开

首次配置时只读本次开机的 `Microsoft-Windows-Kernel-Processor-Power` 系统事件 55，不启动实时跟踪，不增加巡检。以最近一次 Kernel-General 事件 12 为开机边界，并与运行时间估算作 5 分钟容差校验；避免长期运行后的时钟校准误排除本次开机记录，记录缺失或时间明显不符则视为未知。按字段名读取 Group、Number、PerformanceImplementation；拒绝未知事件版本。全部活动逻辑处理器的记录必须齐全且接口一致，否则视为未知。读取有条数和时间预算，结果在进程内缓存。

| 平台记录 | 方案自主模式请求 | 激进档最低性能策略 |
| --- | --- | --- |
| ACPI P-State | 任意 | 保留原有传统调频策略 |
| CPPC | 恰好为 1 | 允许使用已有的低底座策略 |
| CPPC | 0、其它值或读取失败 | 保留这一侧原值 |
| 未知、记录缺失、混合接口 | 任意 | 保留这一侧原值 |

AC/DC 分开判断，效率等级 0/1 使用同一规则。笔记本和掌机已有的功耗让路规则不新增激进值。读取失败不等于传统调频，非零也不等于合法的启用值。

重要限制：事件 55 证明的是 Windows 公布的性能控制接口，不能区分全部 CPPC 版本，也不能证明 HWP 寄存器的实际启用状态。CPPC + 请求 1 是选择低底座的受限策略条件，不是“自主模式已实测开启”的诊断结论。请求 0 也不能证明硬件关闭了自主模式，因为某些单模式平台会忽略请求。

来源：[PerfAutonomousMode](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfautonomousmode)。事件接口枚举及字段以 Windows 随附的 Kernel-Processor-Power 提供程序元数据为依据。

## 旧版恢复

- 留在极限档也会先恢复撤回的 IDLEDEMOTE 和历史 IDLECHECK；仍在使用的两项保持原始快照。
- 新收据带方案 GUID，不能把某份计划的快照恢复到另一份计划。
- 兼容旧版无方案 GUID 的收据：旧设计仅写托管方案，迁移时以当前托管方案为目标。旧收据无法证明外部删除、重建之前的方案身份，这是历史格式的限制。
- AC/DC 分别完整备份；不再用 AC 冒充读取失败的 DC。备份落盘成功后才允许新写入。
- 恢复逐侧回读。部分失败、读取失败或收据保存失败均保留待恢复记录；已恢复侧重试时不重复写。
- 未知 GUID、重复项、坏格式、跨方案收据不执行恢复。程序不以“写过一次”作为恢复完成的依据。
- 从 Pavise 成功删除托管方案后清掉该方案收据；外部删除导致的跨方案收据不自动套用到重建的计划。

## 活动窗口：暂不启用

`PERFAUTONOMOUSWINDOW` 未加入自动写入表。微软限定为 CPPC v2 且启用自主模式的系统；较长窗口降低对短时负载起伏的敏感性。现有接口证据不能证明此项可用，数值 0 也不能直接当作“最快”。

要进入默认策略，仍需确认平台支持、默认值语义，并有重复的游戏帧时间和功耗对照结果。此次未修改本机计划做这类实验。

来源：[PerfAutonomousWindow](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfautonomouswindow)。

## 验证

隔离自测套件 `PowerPlanPolicy` 覆盖平台证据、AC/DC 策略分支、旧收据迁移、部分恢复失败、回读不一致、落盘失败、原值保护及跨方案拒绝。系统电源读取/写入由模拟接口接管。

使用项目现有 `build.cmd -b dev <临时输出路径> --selftest` 构建，再运行产物的 `--selftest` 入口。该入口不会启动主程序调优流程。

## 2026-09-06 实测记录：激进档最低性能值维持放开

i7-9750H 笔记本、RTX 2070 Max-Q、插电、极限档、英雄联盟。同一天两次对照：按上表放开最低性能值到温和值时对局 270 到 280 帧，改回锁 100 后 230 到 250 帧。放开策略维持不变。300 帧以上的差距已定位：候选线程提优默认值由开改关。重新打开后同机同局回到 300 帧以上，重压后台绑核开关对此无影响，默认值已改回开。
