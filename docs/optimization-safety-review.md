# 缓存预热退役与安全修复：代码复核 / 台架结果

复核及修复日期：2026-09-05。**下述两处问题已修复，原探针从 15 次边界断言失败变为 0 次；修复与复验记录见文末。** 这只关闭本页问题，不代表整个项目已通过游戏性能验收或发布验收。

以下“问题 1 / 2、验证汇总、原始证据”保留首次只读复核的历史结果。当时没有修改生产代码或现有回归测试，结论为暂不建议打包发布。复核范围为缓存预热退役、候选线程提优与饱和回退、WsTrim 内存门、档案重试及配套 UI/档案事务；现有电源计划变更由全量隔离回归覆盖，未在真实系统上切换或重写计划。

## 1. [P1] 保存重试期间候选失效，交接失败却已改写游戏库

代码位置：`src/Core/GameMode.Library.cs:220`、`src/Core/Detection/GameProfiles.cs:254`、`src/Core/GameMode.RendererHandoff.cs:327`。

`TryLearnRendererCore` 只在调用 `profileStore.Save(next)` 前执行 `stillCurrent` 检查。保存内部发生文件占用重试后，不再核验候选是否仍有资格，就继续 File.Replace 并发布新档案。外层 `FinalizeRendererSelection` 最后的核验虽然返回 false，但游戏库、内存档案和 LibraryChanged 通知已经改变，不能撤销这次持久化。

复现使用现有 RendererCoordinatorFixture：先确认候选，再以真实只读且不共享删除的句柄锁住自建档案。第一次替换真实失败后，通过现有重试测试钩子释放句柄，并分别模拟失去前台、身份失效、GPU 确认依据撤销。这里的前台、身份和 GPU 依据是明确合成的时序条件，不是运行真实游戏或操控系统前台。

三个变体各重复三次，均得到：`committed=False, disk_changed=True, memory_changed=True, notifications=1, retries=1`，合计 9/9 复现。保持候选有效的对照组 3/3 正常成功提交。

影响：本轮交接被拒绝，但后续检测使用的唯一游戏入口已被替换，形成“操作失败、旧入口却丢失”的状态。新增等待重试使这个时间窗口更明显。

修复方向：将取消/资格检查贯穿到实际替换尝试，至少在每次等待后、File.Replace 前重新检查；取消必须独立于致命存储错误处理。应补入“等待中候选失效后磁盘和内存均不变”的回归。仅在外层保存结束后再检查不足以保护持久化。

## 2. [P2] 主档案回滚时，自动入库忽略名单没有回滚

代码位置：`src/Core/GameMode.Library.cs:46`、`:525`、`:530`、`:536`。

添加档案先从 `autoAddIgnore` 移除路径并保存忽略文件，然后保存主游戏库。删除档案则先加入忽略名单并保存，然后保存主库。新加入的主库失败回滚只处理 `profiles`，没有同步恢复忽略名单的内存与 `Pavise.autoignore.txt`。

真实临时档案持锁测试，各重复三次：

- 删除失败：主库未变，档案仍在；忽略状态却从 false 变为 true，并已写盘。
- 对此前删除过的游戏重新添加失败：主库未变，游戏仍未入库；忽略状态却从 true 变为 false，并已写盘。开启自动入库后，原本应被忽略的游戏可能再次自动入库。

合计 6/6 复现，全部没有触发致命熔断。这不是文件损坏，而是两份持久状态没有形成一致事务；以前首错熔断，改成可恢复失败后，这个遗漏不能继续依赖退出/重置流程掩盖。

修复方向：让主库与忽略名单有明确的一致提交/补偿策略，同时恢复内存。不能只修当前内存 HashSet，也不能仅将两个保存调用交换顺序后宣称跨文件原子性。为添加、删除、批量添加和补偿失败分别加入断言。

## 验证汇总

| 检查 | 本轮结果 | 能证明什么 |
|---|---|---|
| 全量隔离自检 | 66/66 套件通过 | 已有断言通过；没有覆盖上述新时序 |
| RendererHandoffBench，Repeat=5 | 230/230 记录通过 | 既有交接与逐游戏策略矩阵通过 |
| 新增复核探针 | 3 个正常对照通过；15 个边界断言失败 | 两类问题各连续三轮稳定复现 |
| 真实文件占用 StoreOnly | 通过 | 持锁失败保留原文件、不永久熔断；解锁后同实例可重试 |
| 非 SELFTEST 生产源码编译 | 通过 | 编译无错误；未打包、未启动正式 EXE |
| 缓存预热残留搜索 | 生产源码仅剩旧键迁移 | 没有运行路径、策略目录或 UI 入口；历史说明保留 |
| git diff --check | 通过 | 未检出补丁空白错误 |

新探针失败不能被已有两个全绿结果抵消。本轮未测 FPS、1% low 或端到端延迟，不给出性能提升百分比。

## 原始证据与复现

- 本轮编译、自检、探针源码及日志：`%USERPROFILE%\AppData\Local\Temp\Pavise-CodeReview-c1fc138334274abf97f70f600d311304`，关键文件为 `selftest.txt`、`ReviewProbe.cs`、`review-probes.txt`。
- 交接台架：`tools/RendererHandoffBench/results/run-20260905-202900-a7b9e599/REPORT.md`；`verification.json` 确认运行期间源码、测试、台架和正式 EXE 未变，0 超时。
- StoreOnly：`%USERPROFILE%\AppData\Local\Temp\Pavise-RiskBench-03bb951afd1443268d223d92d9dc465b`，关键文件为 `store-lock.csv`、`verification.json`。
- 探针通过专用 `PaviseApp.ReviewProbe` 入口编译当前 src、tests 及 ReviewProbe.cs，定义 `PAVISE_SELFTEST;PAVISE_SELFTEST_RUNNER`。使用与 build.cmd 相同的 .NET Framework 引用，不走 Program.Main。复跑应传入新建的空临时目录，避免旧夹具混入。

414 个 src/tests C# 输入的路径与 SHA256 列表聚合哈希，复核前后均为 `6C0D00FB044D373884E410D1E697929BE42BB96C53E4EFDC0DEEA0F8D764BDFD`。StoreOnly 前后 Pavise 设置指纹、活动电源计划不变，GPU 功耗上限保持 390 W，无残留台架进程。所有主动读写和占用均限定在测试自建文件；未改用户游戏库、真实 Pavise 设置或正式 EXE。

## 修复与复验（用户随后要求“修复”）

### P1：每次实际提交前重验候选资格

`GameProfileStore.Save` 接受提交资格回调；准备临时文件前、首次创建主文件前，以及每一次 `File.Replace` 前检查。`TryLearnRendererCore` 将原 `stillCurrent` 贯穿到这个提交点；包括重试等待后，失去前台、身份不符、GPU 依据撤销/过期或 epoch 变化都会取消写盘。

取消单独记录为 `SaveCanceled`，不触发致命熔断，不发布内存档案或变更通知，临时文件清理；正常对照仍可提交。真实文件占用与永久错误的分类、重试上限及严格替换规则不变。此修复关闭准备/重试等待造成的陈旧提交窗口，不宣称文件系统替换能够与操作系统前台状态变化实现跨系统原子操作。

### P2：游戏库与忽略名单按一组提交、补偿和恢复

- 添加、删除、批量添加先在副本中修改两份状态，仅在成功后发布内存与通知。忽略名单不变时仍只保存主库。
- 两份文件都需要改变时，先严格写入并刷出 `Pavise.library-ignore.txn` 恢复记录，再写忽略名单，最后严格提交主库。记录包括主库前后 SHA256、忽略文件前后精确字节及校验和，原 V5 主库和文本忽略名单格式保持不变。
- 主库未提交则恢复原忽略文件；已提交则保留新忽略文件。补偿或记录清理失败会保留恢复记录并暂停后续写库，不把它误当成完成清理。启动或下一次需要写库的操作会尝试恢复；没有新增定时巡检。
- 恢复时如果检测到第三方改动、未知主库状态或损坏记录，保留文件并拒绝覆盖，日志记录恢复待处理。此时不能只删除恢复记录来“解锁”；应先备份并核对两份文件。缺失主库也不会在恢复未决时被启动加载自动创建。
- 忽略文件不可读时不再把部分/空集合当作已加载数据。超出 8 MiB 的恢复记录在写入前拒绝。精确重置白名单包含恢复记录与合法 GUID 临时文件，不匹配用户备份或近似文件名。

这是有恢复记录的补偿式事务，不是宣称两个独立文件具备同时对外可见的文件系统原子提交。中断恢复只认可记录中的前/后状态，未知状态选择暂停而非猜测。

### 新回归及结果

- `RendererCoordinatorRetryRevalidates`：正常对照及前台、身份、依据撤销、依据过期、epoch 五种失效条件，各三轮。使用真实文件锁触发替换重试；前台/GPU/身份时序为合成测试条件。
- `OptimizationSafety` 从 10 组扩展到 18 组：取消不熔断、添加/删除/批量保存失败双状态回滚、忽略文件真实占用、准备/写入失败、补偿失败阻断后续写库、同实例重试、重启恢复提交前后两种状态、外部改动/损坏记录保护、超大记录拒绝、缺失主库不被创建。
- 重置回归补入恢复记录与 GUID 临时文件的精确删除断言，同时检查相似用户文件保留。所有删除仅针对测试自建数据。
- 最终全量隔离自检：**66/66 套件通过**。记录：`%USERPROFILE%\AppData\Local\Temp\Pavise-TxnFix-95a484fefdfb4c21a48638463fab6e8c\regression-final.txt`。
- 原 `ReviewProbe.cs` 原样重新编译运行：**3 个正常对照通过，15 个边界案例全部通过，失败数 0**。记录：`%USERPROFILE%\AppData\Local\Temp\Pavise-TxnFix-95a484fefdfb4c21a48638463fab6e8c\original-probe-327836cf4b7049dd9517b88d800844b4\review-probes.txt`。
- `RendererHandoffBench -Repeat 5`：**230/230 记录通过**，其中 focused 回归组执行 1300 次全部通过；0 超时，源码/测试/台架输入和正式 EXE 在运行期间未变。记录：`tools/RendererHandoffBench/results/run-20260905-213604-985c54ba/REPORT.md`、`verification.json`。
- 真实占用 `Run.ps1 -StoreOnly`：持锁保存 False、旧档完整 True、永久熔断 False、解锁后同实例重试 True、新实例重试 True。记录：`%USERPROFILE%\AppData\Local\Temp\Pavise-RiskBench-287521211e5e4d90b61aafef863e2311\store-lock.csv`、`verification.json`；Pavise 设置指纹、活动电源计划及测试期间源码未变，GPU 功耗上限仍为 390 W，无残留台架进程。
- 非 SELFTEST 生产源码编译为临时 DLL，通过、无编译警告；测试构建只有既有未赋值钩子的 CS0649 警告。没有打包、替换或启动正式 EXE。

本轮只验证所列正确性与恢复边界，未测真实游戏 FPS、1% low 或端到端延迟，也未切换真实电源计划。
