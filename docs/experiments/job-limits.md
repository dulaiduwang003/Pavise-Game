# Job 核心范围与 CPU 配额实验

## 需要区分的两种用途

Pavise 使用 Job 约束已核实目标的资源范围。游戏和反作弊的核心范围保护，与微软后台的 CPU 配额治理各有独立条件：不能把后台占用硬上限附加到游戏或反作弊核心范围 Job。

实现入口包括 [GameAffinityJobLease.cs](../../src/PaviseApp/GameAffinityJobLease.cs)、[WindowsBackgroundJobNative.cs](../../src/PaviseApp/WindowsBackgroundJobNative.cs) 和 [WindowsBackgroundGovernor.cs](../../src/PaviseApp/WindowsBackgroundGovernor.cs)。

## 核验协议

1. 通过进程句柄同时确认 PID 和创建时间，保存原范围与优先级。
2. 先建立可恢复记录，再设置 Job 参数和关联目标。配置 CPU 配额时，Windows 的 `CpuRate` 以万分之一表示；500 对应 5%，200 对应 2%。
3. 查询 Job 实际限制及成员状态，再读取目标进程状态；接口调用返回成功不能代替这两次核验。
4. 在受控目标上尝试扩大亲和性、改变优先级，观察实际范围是否仍受 Job 约束。
5. 先中和本方 Job 限制，再恢复原范围和其他本方写入；读回成功后才结清恢复记录。

## 2026-11-10 的原生记录

[微软后台实验](windows-background.md)中的 11 个对象全部被核实为 Job 成员，配额读回符合配置。该记录中的亲和性挑战没有使实际进程范围突破 Job 边界。自有忙循环另用 1% 配额完成容量校准，数据见[汇总表](data/windows-background-20261110.csv)。

这说明 Job 在该次环境中可以约束已成功接纳的目标。受保护进程、已有 Job 的兼容性、多处理器组及动态线程分配仍需各自核验；不能据此声称任意服务均可无条件接纳。

## 恢复与兼容边界

- 不使用关闭 Job 即终止目标的策略。
- 目标已退出时，必须取得与保存身份一致的退出证据，不能仅凭查询失败结清记录。
- 关联或核验失败时，不显示为已生效。允许回退的核心范围路径要保留原因；微软后台 CPU 配额也不能被一个普通亲和性掩码冒充。
- 恢复未完成时保留句柄、回执及所有权，停止超过预算的主动尝试。

公开回归中的 [AutomaticRetryOwner](../../tools/Test-AutomaticRetryOwner.ps1) 和 [ConsumerRecovery](../../tools/Test-ConsumerRecovery.ps1) 核对失败与恢复边界；原生 Job 配额实验与这些内存夹具属于不同证据。

接口单位和系统约束见 [Microsoft CPU rate control](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information) 与 [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)。
