# NVIDIA Profile Inspector 与 Magpie 源码研究

研究日期：2026-09-08。范围：官方仓库、关键实现和 Pavise 现有代码的静态对照。本次没有运行上游程序、修改驱动设置、注入游戏或执行性能台架；下文的收益判断属于机制分析，不是实测结果。

结论：NVIDIA Profile Inspector（NPI）最适合帮助 Pavise 完善驱动档案的类型、来源和恢复语义；Magpie 最适合提供外部缩放协同。若目标是增加用户能直接感知的新能力，Magpie 更有差异；若目标是尽快改善现有功能的可靠性，应先借鉴 NPI。

## 研究版本

| 项目 | 查询到的最新正式版 | 源码研究基准 | 技术与许可 |
|---|---|---|---|
| NPI | [v3.0.2.1，2026-07-05](https://github.com/Orbmu2k/nvidiaProfileInspector/releases/tag/v3.0.2.1) | master，`2f50c388b3a4d661cade66b32746bec096d1eee1` | C#、WPF、.NET Framework 4.8；MIT |
| Magpie | [v0.12.1，2025-08-27](https://github.com/Blinue/Magpie/releases/tag/v0.12.1) | dev，`9372bc46f6b0daa65eef91685aa1ad5304f5c353` | C++、HLSL、WinUI；GPL-3.0 |

源码细节以下述提交为准，不能据此假定所有实现均已包含在正式版安装包中。平台与许可证信息见 [NPI 仓库](https://github.com/Orbmu2k/nvidiaProfileInspector) 和 [Magpie 仓库](https://github.com/Blinue/Magpie)。

## NPI：值得学的是完整的驱动档案模型

它通过 NVIDIA DRS 接口管理全局和逐应用档案，把驱动接口、设置元数据、应用匹配、导入导出和界面分开。设置能否产生效果仍取决于当前驱动、显卡和游戏；隐藏项目的存在不能作为有效提帧的证据。[NPI 说明](https://github.com/Orbmu2k/nvidiaProfileInspector#advanced-settings-notice)

Pavise 已有逐游戏功耗策略、低延迟、Shader Cache、ReBAR、DLSS 覆盖、Smooth Motion 等写入路径，也有修改前快照、按项恢复和避免覆盖第三方新值的逻辑。继续增加相同开关的价值有限。依据：[NvDrsTweaks.cs](src/Core/Tweaks/NvDrsTweaks.cs:10)。

真正值得借鉴的四点：

| 能力 | NPI 的做法 | 对 Pavise 的意义 |
|---|---|---|
| 当前驱动提供设置类型和值域 | 枚举当前驱动的可用设置与取值；类型优先采用驱动结果，再使用扫描和 XML 信息 | 避免把所有设置永久当成 DWORD，也便于解释当前机器支持哪些项 |
| 设置来源可见 | 区分 NVIDIA 预设、当前应用自定义、全局继承和未指定 | 界面显示“开关设置了什么”和“实际值从哪里来”；恢复时保留原本的来源语义 |
| 多来源元数据 | 合并官方常量、当前驱动、CustomSettingNames、Reference 和扫描数据 | 可维护名称、说明和值域，而不是不断扩张硬编码开关 |
| 档案导入与应用匹配 | 支持 Merge / Replace；应用记录还保留 fileInFolder 等信息 | 适合做导入预览、冲突说明和精确匹配；不要默认整份覆盖用户档案 |

源码依据：[DriverSettingMetaService](https://github.com/Orbmu2k/nvidiaProfileInspector/blob/2f50c388b3a4d661cade66b32746bec096d1eee1/nvidiaProfileInspector/Common/Meta/DriverSettingMetaService.cs#L25)、[DrsSettingsMetaService](https://github.com/Orbmu2k/nvidiaProfileInspector/blob/2f50c388b3a4d661cade66b32746bec096d1eee1/nvidiaProfileInspector/Common/DrsSettingsMetaService.cs#L96)、[设置来源及应用指纹](https://github.com/Orbmu2k/nvidiaProfileInspector/blob/2f50c388b3a4d661cade66b32746bec096d1eee1/nvidiaProfileInspector/Common/DrsSettingsService.cs#L425)、[导入实现](https://github.com/Orbmu2k/nvidiaProfileInspector/blob/2f50c388b3a4d661cade66b32746bec096d1eee1/nvidiaProfileInspector/Common/DrsImportService.cs#L509)。

### 对照发现一：读取错误被当作没有本地设置

Pavise 的 `TryGetDword` 在 `drsGetSetting` 返回任何非零状态时都返回 0；调用方则把 0 保存成 `absent`，只有负数才中止该项写入。这样，原生接口的读取错误无法进入已有的“无法快照就不写”分支。部分恢复判断也把返回 0 当作已经没有本地覆盖，可能过早认为恢复完成。

这是从控制流直接确认的问题；本次没有在真实驱动上触发故障，不表示每次写入和恢复都失败。位置：[NvApi.cs:236](src/Platform/NvApi.cs:236)、[快照判断](src/Core/Tweaks/NvDrsTweaks.cs:321)、[恢复判断](src/Core/Tweaks/NvDrsTweaks.cs:363)。

NPI 只将 `NVAPI_SETTING_NOT_FOUND` 视为不存在，其余错误保留失败状态。建议沿用 Pavise 现有保护逻辑，将读取结果明确分为“本地设置、继承值、不存在、错误”，并附上原生状态码。[NPI ReadSetting](https://github.com/Orbmu2k/nvidiaProfileInspector/blob/2f50c388b3a4d661cade66b32746bec096d1eee1/nvidiaProfileInspector/Common/DrsSettingsServiceBase.cs#L204)

### 对照发现二：ReBAR 容量设置需要核对类型

Pavise 把 `0x000F00FF`（ReBAR Size Limit）放在 `uint` 计划中，并统一用 `SettingType = 0` 的 DWORD 写入。NPI 当前元数据将该项的回退类型标为 QWORD，类型选择逻辑还明确处理不同驱动代际的类型变化。

这构成具体的兼容性疑点，尚不足以断言本机 ReBAR 功能已经失效。应先只读查询本机驱动报告的实际类型和值，再决定如何扩充值模型和旧快照迁移；不要直接将所有驱动都改成某一种类型。依据：[Pavise 写入计划](src/Core/Tweaks/NvDrsTweaks.cs:212)、[底层写入](src/Platform/NvApi.cs:250)、[NPI 容量元数据](https://github.com/Orbmu2k/nvidiaProfileInspector/blob/2f50c388b3a4d661cade66b32746bec096d1eee1/nvidiaProfileInspector/CustomSettingNames.xml#L1936)、[类型优先级](https://github.com/Orbmu2k/nvidiaProfileInspector/blob/2f50c388b3a4d661cade66b32746bec096d1eee1/nvidiaProfileInspector/Common/DrsSettingsMetaService.cs#L130)。

### 对照发现三：数值还原不等于来源还原

Pavise 的读取结构包含预定义标记，但目前主要使用位置和当前 DWORD 数值。原本的 NVIDIA 预设若被当作普通数值保存，恢复为同值覆盖并不完整表达“重新跟随驱动预设”。扩充收据时应同时保存类型、来源和原值，并保留现有“用户或其他工具后来改过就让位”的逻辑。[Pavise 结构](src/Platform/NvApi.cs:110)、[NVIDIA 设置结构](https://docs.nvidia.com/nvapi/struct___n_v_d_r_s___s_e_t_t_i_n_g___v1-members.html)

推荐先交付一个只读“驱动档案详情”：档案名称、应用匹配、当前值、来源、支持情况和 Pavise 的修改记录。导入预览放在后面；不把未知隐藏项汇总成“一键极限性能”。这部分的直接产出是可靠性和可解释性，不能承诺固定 FPS 增益。

## Magpie：新增能力在外部缩放链路

其核心流程是：游戏输出较低分辨率窗口 → Windows 捕获 → GPU 执行缩放与锐化效果 → Magpie 自己的窗口呈现。游戏代码和原始渲染流程由游戏自身管理。官方定位是通用窗口缩放器，FAQ 明确没有帧生成计划，也不能取得游戏引擎内部的运动矢量、深度等数据来实现引擎内 FSR 2/3。[Magpie FAQ](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/docs/FAQ%20%28EN%29.md)

| 关键实现 | 源码观察 | 可借鉴之处 |
|---|---|---|
| 捕获只取最新帧 | Graphics Capture 循环取帧，丢弃队列里较旧的结果 | 外部处理赶不上时优先降低排队延迟 |
| 重复帧检测 | GPU 比较当前与前一张捕获图像；重复时跳过后续效果链；动态模式会减少无收益的检测 | 对静态内容降低自身重复后处理成本 |
| 缩放与最终呈现分开 | 后端执行效果链；前端呈现结果、光标和覆盖层，通过共享纹理同步 | 昂贵滤镜不必让光标也以同样低的频率更新 |
| 独立呈现策略 | 使用 flip-model 交换链、帧延迟等待对象和 DirectComposition 路径 | 呈现节奏也是工程重点，不能只拿一个 HLSL 滤镜就认为功能完成 |

源码依据：[捕获最新帧](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/src/Magpie.Core/GraphicsCaptureFrameSource.cpp#L82)、[重复检测](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/src/Magpie.Core/FrameSourceBase.cpp#L59)、[渲染线程与呈现](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/src/Magpie.Core/Renderer.cpp#L203)、[AdaptivePresenter](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/src/Magpie.Core/AdaptivePresenter.cpp#L10)。

重复帧检测尤其容易被误解：它不会让游戏只计算变化区域，只能省掉 Magpie 自己的重复缩放；比较、图像保存与读回结果也有开销。源码在开启 3D game mode 时直接跳过这项检测。因此不适合将它包装成大型 3D 游戏的通用提帧技术。[检测入口与结果读回](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/src/Magpie.Core/FrameSourceBase.cpp#L308)

### 性能成立的条件

这部分是机制推断：只有降低游戏内部渲染分辨率所节省的工作，足以覆盖捕获、缩放和呈现成本，才可能有净收益。各阶段可以重叠执行，不能用简单耗时相加来准确预测最终 FPS；输出帧率与端到端延迟也必须分别评价。

| 游戏情况 | 预期方向 |
|---|---|
| GPU 像素渲染受限、没有合适的内置超分 | 值得比较低分辨率窗口加轻量缩放，可能提高真实游戏帧率，但会有画质与延迟取舍 |
| CPU 主线程受限，降低分辨率本来就不提帧 | 主要 CPU 瓶颈仍在，额外缩放可能没有收益 |
| 游戏已有质量合适的内置超分 | 应先把内置超分作为对照，外部方案还要承担捕获与额外呈现 |
| 原分辨率不降，只叠加复杂滤镜 | 主要获得画质变化，额外计算可能降低帧率 |

不报告固定收益百分比。Magpie 叠加层显示的自身处理帧率不能替代游戏的真实帧率，更不能替代输入到显示的延迟测量。官方性能文档中的驱动版本、HAGS、限帧与其他程序冲突建议都有具体背景，不能提炼成所有电脑通用的设置。[官方性能说明](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/docs/Performance%20optimization.md)

### 适合 Pavise 的第一步：识别并协同外部 Magpie

Magpie 已公开 `MagpieScalingChanged` 状态消息、固定窗口类名和 `Magpie.SrcHWND` 等窗口属性。Pavise 可以在游戏卡片显示缩放状态、识别输出对应的源游戏窗口，并让参与当前画面输出的 Magpie 进程免于后台压制。这类协作使用窗口消息与属性，不需要读取游戏内存或向游戏注入 DLL。[官方交互文档](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/docs/Interact%20with%20Magpie%20programally.md)

Pavise 当前覆盖层保护名单已经包含 OBS、RTSS 和 NVIDIA Overlay 等宿主，但未包含 Magpie，源码也没有 Magpie 专用状态桥接。这是一个实际的集成缺口。现有白名单、前台和家族规则仍可能保护它，不能据此断言所有模式下一定会误压制。位置：[OverlayHostCatalog.cs](src/Core/OverlayHostCatalog.cs:20)、[后台保护边界](src/Core/GameMode.Sweep.cs:232)。

实现时应核对输出 HWND、源 HWND、进程路径、PID 与创建时间，并只在有效缩放会话内建立关系。消息的开始/结束通知不等于完整的外部启停接口；本次查到的公开交互文档没有给出指定游戏一键开始/停止缩放的完整控制协议。不能仅凭这些消息就承诺游戏卡片的启停开关已经可以落地。

官方将 Magpie 描述为非侵入式工具，但这不构成任何具体网游反作弊的兼容保证。Pavise 的研究边界继续保持为外部窗口协作；不扩展为注入、游戏内 Hook 或反作弊绕过。[官方 FAQ 的兼容性说明](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/docs/FAQ%20%28EN%29.md)

Magpie 的多适配器选项也不能证明集显分担一定更快。项目已有异构 GPU 台架的负向结果仍有效，当前研究没有提供推翻该结果的新实测。[已有台架报告](tools/HeterogeneousGpuBench/RESULT-20260817.md)

## 建议次序

| 次序 | 工作 | 相对成本 | 验收重点 |
|---|---|---|---|
| 1 | 借鉴 NPI 修正错误分类，核对 ReBAR 类型，补齐来源信息 | 小到中 | 读取失败不生成虚假快照、不误判恢复完成；原值和来源可解释 |
| 2 | NVIDIA 档案只读详情 | 中 | 显示当前驱动实际读取值、档案来源与应用匹配；不把 UI 开关当成生效证据 |
| 3 | Magpie 状态识别、源游戏关联和会话内保护 | 中 | 开始、切出、停止、异常退出与 PID 复用时关联正确；不影响现有白名单语义 |
| 4 | 再评估受控启停或自有缩放引擎 | 高 | 明确可用控制接口；验证缩放画质、实际游戏帧率、延迟及兼容性 |

NPI 使用 MIT，Magpie 使用 GPL-3.0，而 Pavise 当前采用自己的许可协议。若将上游代码或二进制纳入产品发行，需要单独核对对应许可和依赖；外部程序协作可以先用于能力验证，但不能仅凭“独立进程”就替整个发行方案作许可结论。[NPI LICENSE](https://github.com/Orbmu2k/nvidiaProfileInspector/blob/2f50c388b3a4d661cade66b32746bec096d1eee1/LICENSE)、[Magpie LICENSE](https://github.com/Blinue/Magpie/blob/9372bc46f6b0daa65eef91685aa1ad5304f5c353/LICENSE)、[Pavise LICENSE](LICENSE)。

本次交付仅为研究报告。生产代码、驱动档案和游戏运行状态未因本次研究而改变。
