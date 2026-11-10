# CPU 拓扑与核心类型验证

## 判断依据

Windows 处理器关系、CPU Sets 和受支持平台的逐逻辑处理器 CPUID 提供不同证据。Pavise 核对物理核心掩码、同核线程关系及效率等级，不按型号名称、编号先后或是否具有第二线程猜测 P/E 核。

当前实现入口为 [CpuTopology.cs](../../src/PaviseApp/CpuTopology.cs)、[CpuTopologyEvidence.cs](../../src/PaviseApp/CpuTopologyEvidence.cs)、[CpuCoreClassification.cs](../../src/PaviseApp/CpuCoreClassification.cs) 和 [CpuCoreTypeProbe.cs](../../src/PaviseApp/CpuCoreTypeProbe.cs)。

## 验证矩阵

- 完整、缺失、截断和冲突的 Windows 拓扑与 CPU Sets 记录。
- 相同核心总数但不同 SMT 兄弟关系，稀疏逻辑处理器编号，混合 SMT 和缓存域。
- Windows 效率等级平坦、来源分区冲突、多处理器组和物理核心数量不确定。
- CPUID 类型有效、未知、读取异常和预算耗尽；测试线程结束前恢复自己的亲和性。
- UI 展示未确认类型时，保留已有选择与掩码，不把未知类型转换成推测范围。

## 2026-11-10 的结果

受控验证完成 28 项核心分类、73 项原始拓扑证据、88 项核心类型 UI、347 项原生亲和性及 1,478 项原生回退检查，均通过。以上包含合成输入、自有线程和本机只读检查，各自的覆盖范围不同。

本机 Intel Core i7-9750H 为 6 个物理核心、12 个逻辑处理器。两种 Windows 接口对物理核心分组一致，但效率等级平坦，CPUID 补充路径没有可用的类型叶；实现保持类型未确认，没有把该结果误称为已验证的 P/E 划分。重复回退没有改变调用线程或进程的原亲和性。

原生补充当前只接受已支持的 Intel 类型标识。其他平台依赖 Windows 证据；多处理器组也不会把组 0 冒充整机范围。

## 公开探测

```powershell
python tools/CpuMemoryBench/Run.py --mode probe
```

该命令检查本机台架能力及拓扑条件，不能代替上述完整开发验证矩阵。需要受控 P/E 实验的模式只在拓扑满足要求时运行，条件不符会记录原因。详见[独立台架](cpu-memory-bench.md)。
