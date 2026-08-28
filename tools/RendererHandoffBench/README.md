# Per-game family policy and renderer handoff correctness bench

验证当前源码中逐游戏“压制家族后台”的隔离与安全默认值，以及“误选启动器 → 真实渲染进程确认 → 游戏库单入口替换”的完整交接链，不是 FPS 或性能测试。`results/` 下历史复现报告保留；新的默认判据为修复后预期，只有所有断言成立才 PASS。

## 运行

在仓库根目录的 PowerShell 中运行：

```powershell
.\tools\RendererHandoffBench\Run-Bench.ps1 -Repeat 3
```

需要 Windows/.NET Framework x64 C# 编译器。无需启动 Pavise，无需管理员权限。运行前冻结源码和本台架；脚本在唯一 `results/run-日期-随机值/` 中编译和保存产物，不覆盖已有 run 或生产 `Pavise.exe`。

脚本只编译 `src/**/*.cs`、本台架及以下专项测试文件：

- `SelfTests.RendererCandidate.cs`
- `SelfTests.RendererRelease.cs`
- `SelfTests.RendererHandoff.cs`
- `SelfTests.RendererLearning.cs`
- `SelfTests.RendererCoordinator.cs`
- `SelfTests.FamilySuppression.cs`
- `SelfTests.GenericRenderer.cs`
- `SelfTests.GameInstallScope.cs`
- `SelfTests.GameFamilyHistory.cs`
- `SelfTests.GameFamilyIntegration.cs`

使用 `PAVISE_SELFTEST;PAVISE_RENDERER_BENCH`，入口固定为 `PaviseApp.RendererHandoffBench`。HandoffBench.cs 尾部提供最小断言支持，不纳入完整 SelfTests runtime；误入应用 runtime 会立即抛错。只调用明确列出的专项测试方法，不启动 `Program.Main`。

## 验证内容

每轮运行纯状态机、恢复、候选归属、协调器、三组学习、逐游戏家族策略/观察标签及通用识别规则专项回归；报告记录实际返回的测试数（状态机入口为 0/1 状态码，对应 12 组）。

安装范围与家族发现新增真实添加链路回归：使用临时合法 EXE 和注入的通用安装记录，从 `AddGameExecutable` / `AddScannedGames` 开始，不预填正确的共同 Root。覆盖随机命名的启动器目录、兄弟渲染目录、存量窄 Root 加载修正、缺失/冲突/过宽安装证据，以及启动器或中间进程退出。安装范围解析不依赖静态扫描能够推荐唯一 EXE。

公共目录和已确认的平台根也不能作为旧路径推断的兜底。覆盖手选平台 EXE、旧宽 Root 加载修正及平台内具体游戏目录：平台入口仍能按精确 EXE 和真实进程亲缘关联，但不会仅凭平台安装目录包含多个游戏就把它们认成同一家。

历史亲缘只使用合成快照/已验证启动事件中的 PID、创建时间、会话和路径；检查同批多级启动、PID 复用、共享宿主无关兄弟、配置指纹、生命周期清理和有界容量。退出祖先不伪装为活进程，证据只提供当前活身份，仍交给原来的窗口/3D 确认规则。此类测试不等于真实启动事件源从不丢事件。

逐游戏专项使用真实临时 V5 库及 fake 提交动作，检查默认保护、旧全局关闭不静默迁入、两个游戏配置隔离、字段/配置往返保留、缺失 ID、幂等不写盘、启用/关闭保存失败不发布，以及关闭与旧后台请求交错时的策略版本守卫。关闭后重新开启也不能让旧请求重新有效。仅调用生产提交 gate 包装计数动作，不调用实际 Acquire 或进程还原。

逐游戏保护集合通过生产 `CollectProtectedLibraryFamily` 和纯进程快照验证：另一款游戏保持关闭时，其合法跨目录子孙仍受保护；共享宿主自身可受保护，但不能扩大到无关兄弟进程；启动器退出后的可信目录 seed、无 Root 的精确入口/旧 learned、创建时间、登录 session 和 Pavise 自身边界分别覆盖。

观察标签专项直接测试可选历史 store，并以合成身份、前台和事件门控 GPU 利用率字典调用生产 `MaybeObserveRendererActivity`。手动选择、全屏窗口或 Force 本身不产生标签；只有目标收到达到阈值的有效 GPU 3D 样本，才记录基础活动标签。测试不调用真实 GPU 采样。

标签集成回归覆盖弱/缺失/非有限样本、PID 身份或前台失效、生命周期取消、目标或文件改变、档案删除，以及 GPU worker 单实例和共享采样 gate。V1 六列历史按档案 ID、精确路径、文件长度/修改时间匹配；磁盘加载后先后台校验，界面查询不访问 EXE。缓存损坏/写入失败不能熔断主游戏库。只有实际业务开关变化或更换入口走真实临时文件保存，后台调度动作仍为 fake。

通用规则专项使用纯快照/内存或临时 PE 夹具，确认判断不依赖某个游戏或客户端名称。GPU 3D 活动也可能来自大厅、启动器或其他图形程序；这些测试不把“已观测渲染”解释为“主渲染程序已被百分之百证实”或“可以安全压制全部家族”。

真实夹具分同目录与 `Menu/Client` 兄弟目录两组，各自覆盖：

- Standard / Competitive / Handheld × 逐游戏家族后台压制开/关 × 旧 Launcher / 旧 learned 为历史 sticky 锚，共 12 种组合；Boost 全部关闭。配置通过 `SetProfileFamilySuppression` 写入本次临时库，不再由旧全局开关决定。
- 恢复仍 Pending：不启动 GPU 确认、不保存候选，精准保护新 renderer。
- 事件门控 GPU worker 未返回：八次连续逻辑轮询保留旧锚、保护候选，且只有一个 worker；八次轮询不等于八秒。
- 合成 GPU 证据返回后，经当前生产 `ResolveRendererHandoff → FinalizeRendererSelection → ApplyStickiness → CompleteRendererSelection` 切换到窗口化 renderer。
- 同 profile Id、用户 Name、Overrides、Force 保留；Executable 替换，Learned 清空，Entries 只剩真实 renderer。已有合法且包含 renderer 的声明 Root 保留，不把“仍属游戏目录”误称为“仍是旧精确入口”。
- 旧启动器和旧 learned 进程仍活着、旧 EXE 文件仍保留；重复确认不再次写库。
- Force SafetyOnly 只保护、不采证、不改目标；真实 owned PID 搭配合成过期 creation / 错误 path 时，由原生身份检查拒绝。没有声称实际制造了 OS PID 重用。
- Launcher 正常退出时其原 child 也退出；随后由 bench 创建新的独立 renderer leaf（真实父进程是 bench），验证父启动器已退出但声明 Root 仍可信。没有声称原 child 存活。

## 真实证据与合成证据

| 真实、只针对本次 owned helpers | 明确合成 / 隔离 |
|---|---|
| EXE 路径和 SHA256、PID、创建 FILETIME、父 PID | 可见 / 前台 / 窗口化标志 |
| 原生 candidate/sticky 身份复核 | 事件门控 GPU 利用率字典 |
| 初始和结束优先级只读对比 | Background release 的 Ready / Pending |
| stdin 正常退出、退出码、保留的 Process 对象 | 历史 sticky 状态、临时游戏库与预设/逐游戏家族开关 |

候选归属仍调用生产纯快照检测器；只替代窗口捕获和前台读取。真实身份验证 seams 保持未设置。纯专项回归另可使用完全合成身份，和真实夹具记录分开。

## 安全边界

- 在任何应用构造器/测试前调用 `Settings.UseTransientStoreForCurrentProcess()`，不读写真实 Pavise 设置。台架库位于本次 run；专项测试可创建并清理自己唯一命名的临时目录。
- 可构造 `GameMode`，但不调用有系统副作用的 Enabled setter，也不运行 Start / Loop / Stop / Sweep / Boost / Acquire / 实际 GPU 采样或恢复。候选保护直接查询生产保护方法，不执行后台压制。
- 平台/外设缓存仅在本进程固定为空，安装元数据使用显式空快照或临时用例记录，不扫描真实安装目录或读取宿主卸载记录。不会启动真实游戏、浏览器或生产 EXE。helpers 无 GUI、GPU 工作、网络、注册表或权限修改。
- 编译出的 FixtureHost 仅复制成运行目录下的 GameLauncher / GameRenderer / OldRenderer，启动前校验其 SHA256 相同。Root 创建 child，先等其 READY 后报告 `READY|rootPid|rendererPid`；leaf 报告 `READY|pid`。
- Root/leaf 接受 stdin `exit` 或 EOF，helper 独立寿命最多 40 秒，Root 退出时关闭自己的 child。台架优先正常退出；应急清理只针对已经持有并核验的 Process 对象，不按名称杀进程，发生 kill 或非零退出码即判失败。
- 脚本有有界运行超时，只可终止自己创建的 bench 实例；helper 管道关闭及自身超时防止长期残留。

## 产物与解释

`REPORT.md` 给出摘要，`results.json` 包含逐项断言和真实夹具身份；`console.log` / `stderr.log` / `production-decision.log` 保留诊断。`source-hashes.json`、`fixture-hashes.json` 和 `verification.json` 记录源文件/专项测试/台架/生产 EXE 未被运行修改及夹具副本一致性。

PASS 只表示所列正确性回归通过，不等于验证了真实窗口捕获、GPU 采样准确性、操作系统调度效果或帧率提升。

## 安全重置专项

```powershell
.\tools\RendererHandoffBench\Run-ResetChecks.ps1
```

该专项使用独立临时目录、内存设置和模拟还原/注册表接口，验证“停止确认 → 系统还原 → 删除文件 → 清注册表”的顺序。停止或还原失败时保留恢复记录；文件被占用、注册表清理失败、只读文件、便携版无关文件和目录链接分别校验。也覆盖停机后的异步写入保护，防止观察缓存、设置和日志在清理后被重建。

不启动正式程序，不执行真实系统还原或注册表删除；识别部分继续运行原有基础方案的专项回归，本轮多轮增强已撤回。完整台架的旧失败报告保留，不将隔离测试通过解释为所有机器上的保存/还原都会成功。
