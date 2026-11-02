# 优化风险台架

几个小反例 专门查那些怀疑会拖累游戏的优化 用当前生产源码单独编一个入口 不启动 Pavise 本体 不改系统设置

```powershell
powershell -NoProfile -File tools/OptimizationRiskBench/Run.ps1
powershell -NoProfile -File tools/OptimizationRiskBench/Run.ps1 -SpreadControlOnly
powershell -NoProfile -File tools/OptimizationRiskBench/Run.ps1 -ExtrasOnly
powershell -NoProfile -File tools/OptimizationRiskBench/Run.ps1 -StoreOnly
```

- policy 拿假数据过一遍生产里的判断函数
- trim 256 MiB 私有内存 修剪工作集前后各观测 12 次 ABBA 排序
- lane baseline 和 lane boost 两个自己的线程挤一个逻辑核 让 RenderLane.TryIdentify 挑一个提到 THREAD_PRIORITY_HIGHEST 看另一个受多大影响
- lane spread 同样的事放到两个逻辑核上
- -ExtrasOnly 真开一条静音音频流 看低延迟周期切换和回读
- -StoreOnly 拿一个只读句柄占住档案文件 看保存怎么处理

每个进程 45 秒看门狗 编译用 -define:PAVISE_PERFLAB -main:PaviseApp.OptimizationRiskBench 引用和 build.cmd 一样 源文件是全部 src 加 RiskBench.cs 和 ExtraBench.cs

## 复核入口

`Run-Review.ps1 -Arm priority|power-inputs|auto-gpu-filter|read-cost` 是另一个控制台入口

- priority 两个自己的进程互相等 故意做优先级反转 同核和分核各跑 ABBA
- power-inputs 拿假的 GPU 功耗 CPU 序列喂生产的功耗让路状态机 每个用例五遍
- auto-gpu-filter 自动显卡分配的可见 EXE 门槛和入库函数 全假输入
- read-cost 只读进程快照和共享显存 PDH 查询的开销 冷热分开记

`Summarize-Review.ps1 -RunDirectory <目录>` 核对行数和还原 再从原始样本重算分位数 正常应有 16 条优先级 20 条功耗判定 10 条显卡门槛 40 条读取开销
