# 双显卡协作台架的测试规则

问题只有一个 同样的场景生成和后处理 把后处理从独显挪到核显 算上跨适配器开销后 吞吐能不能稳定提高 尾部帧间隔不变差

## 边界

- 只跑自己的 DX12 离屏计算 不捕获游戏 不开交换链 不 Present
- 不注入 不装驱动 不改电源和显卡设置 不为了安静去杀用户进程
- build-manifest.json 必须 status success 跑的 exe 哈希要和清单对上
- 无参数只打印计划 --self-test 不建 D3D 设备 --validate-only 只查像素 --run 才计时

## 开跑前定死

1. 记系统版本 两张卡的名字 厂商设备 ID LUID 驱动版本 显示拓扑和全部命令行 编号用 --list-adapters 核对 不猜
2. 分辨率 像素格式 循环数 帧数 传输路径 核显输入方式 电源方案全部固定 筛查和正式确认的参数分开存
3. 记录后台 CPU GPU 活动 不动鼠标不等于机器空闲 远控 桌面合成 浏览器动画都会占核显
4. --precondition-seconds 默认 0 正式跑要写明预运行多久
5. 出现操作 后台突发 温度时钟漂移 整个受影响的配对块作废 存原始数据重跑

环境标签两种 idle_gated 要求连续三个 CPU 样本不超过 20% background_loaded_screening 只有显式传 -AllowBackgroundLoadScreening 才启用 上限 50% 其他门槛不变

## 传输路径和像素校验

- direct 独显场景直接写共享缓冲
- copy 先写本地缓冲 再 CopyBufferRegion 到共享缓冲 复制和屏障计入 scene_gpu_ms
- 核显输入 shared 直接读共享缓冲 local 先复制到自己的 DEFAULT 缓冲再后处理 复制全部计入 post_gpu_ms

计分前用 seed 0 41 193 检查单卡双卡输出 场景源图逐像素相同 最终 RGB 每通道误差不超过 1 alpha 必须 255 每对输入再和 CPU 参考抽样最多约 4160 点 跨 seed 输出必须真的变了 每种传输和输入组合各自校验 像素不过就没有候选结论

## 顺序和指标

三块交替 single hetero hetero single 然后 hetero single single hetero 再 single hetero hetero single 相邻阶段组成不重叠的 A B 配对

指标 计算平均 FPS 用 1000 除以完成间隔均值 1% low 完成间隔 P50 P95 P99 阶段 wall FPS 两张卡各自的 GPU 时间 每阶段 wall 和 GPU 吞吐差超过 5% 判无效

## 有效性

两张卡 LUID 不同 都能建 D3D12 硬件设备 不要 WARP 跨适配器 heap buffer fence 建得起来同步得了 所有阶段跑满帧数 时间戳有效 没有设备丢失和像素失败 单卡各阶段 max/min 减 1 超过 5% 判无效

## 候选收益

有效之后还要同时满足 计算平均 FPS 和 wall FPS 中位数都至少 +2% 1% low 至少 +1% 至少三分之二的配对两项都不下降 P99 中位数不恶化超过 5% 冻结参数独立重复还成立

判定三档 INVALID NO_BENEFIT CANDIDATE --run 无效退出 2 其余退出 0

## 归档

留 frames.csv phases.csv summary.json validation.json report.txt build-manifest.json summary 是 schema 2 scope 是 owned_offscreen_compute_only product_ready 固定 false 用包装器时 request.json 环境快照 telemetry.json 输出日志 controlled-result.json 和 artifact-snapshot 一起存

计算候选过了 还要在真实可卸载的工作上再验 单卡双卡画面质量一样 算上输入 复制 格式转换 同步 回传 合成和 Present 看显示帧时间 丢帧和延迟 固定场景重复 A B 不支持时能回退 只对验证过的硬件驱动声明支持

参考 [微软异构多适配器示例](https://learn.microsoft.com/en-us/samples/microsoft/directx-graphics-samples/d3d12-heterogeneous-multiadapter-sample-win32/) [DX12 共享堆](https://learn.microsoft.com/en-us/windows/win32/direct3d12/shared-heaps)
