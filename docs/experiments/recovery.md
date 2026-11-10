# 异常退出与恢复验证

## 恢复约束

Pavise 在修改前保存原值和归属，恢复时重新核实目标身份，区分本方写入与后续外部修改。只有实际恢复成功，或取得相应目标已退出的可靠证据，才能结清恢复记录。

自动失败重试具有次数上限。轮询、同值保存、工作线程重建、健康状态观察，以及同会话中的 A→B→A 切换，都不能为耗尽的同一配置重新填充预算。预算耗尽后停止主动尝试，保留尚未结清的恢复记录、句柄和所有权。

实现入口包括 [AutomaticRecoveryBudget.cs](../../src/PaviseApp/AutomaticRecoveryBudget.cs)、[GameAffinityJobLease.cs](../../src/PaviseApp/GameAffinityJobLease.cs)、[GameMode.Recovery.cs](../../src/PaviseApp/GameMode.Recovery.cs) 和 [HardThreadJournal.cs](../../src/PaviseApp/HardThreadJournal.cs)。

## 已验证的场景

| 场景 | 检查的结果 |
| --- | --- |
| 权限拒绝、暂时不可读、身份不符 | 不误报生效，不把未知当作退出 |
| 恢复失败并耗尽预算 | 停止主动尝试，保留归属和原值 |
| 同值保存、重复扫描、A→B→A | 不刷新已耗尽预算 |
| 目标真正退出或新会话 | 仅相关身份开始新的有限阶段 |
| 延迟回调、旧配置完成结果 | 不覆盖新配置或清除其他所有者的状态 |
| 本方已写入后发生外部修改 | 恢复不得盲目覆盖外部新值 |

独立守护及控制进程异常退出另用自有工作进程验证，不终止系统服务或游戏来制造故障。2026-11-10 的微软后台原生检查完成 27 项自有进程验证；恢复后没有遗留该台架的工作进程。[服务实验](windows-background.md)还单独核对了正常退出后的原值恢复。

## 公开复现

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Test-AutomaticRetryOwner.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Test-ConsumerRecovery.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Test-ConfigurationPresetIdentityRetry.ps1 -Mutations
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Test-TimerRetryBoundary.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Test-TamerRetry.ps1
```

这些入口直接检查对应生产方法，通过内存设置、受控时钟、真实事件或受控线程池制造故障。`-Mutations` 故意移除保护条件，要求负对照失败，防止用例只检查顺利路径。

各套检查的隔离范围见[测试说明](../../tests/README.md)。原生失败条件还取决于系统版本、权限和目标状态；复核时同时记录原生读回与恢复结果。
