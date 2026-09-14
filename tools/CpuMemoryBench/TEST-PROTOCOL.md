# CPU 与内存台架的测试规则

全程只跑自己的测试程序 不碰游戏 不注入 不装驱动 不提权 不改全局设置 子进程一律无窗口

## 预取器

先只读探测能力 真要测必须有按 CPU 型号区分的特权 MSR 后端 写完回读 测完还原 没有就记 NOT_TESTED_NO_PRIVILEGED_BACKEND

## 核心摆放

拓扑从 GetLogicalProcessorInformationEx 读 要求单处理器组 至少 6 个 P 核 前台核有 SMT 兄弟 四组 E 核 L2 不满足直接报错

前台一个线程钉在一个 P 核逻辑处理器 后台四个普通优先级线程 摆法六种

- alone 没有后台
- unrestricted 后台不限核
- smt_overlap 一个后台和前台共用物理核 故意找茬的对照
- p_separate 四个独立 P 核
- e_pack 挤在同一组 L2 的四个 E 核
- e_spread 四组 L2 各出一个 E 核

前台负载三种 整数递推 8 MiB 随机指针链 128 MiB 随机指针链 后台两种 整数递推 或者连续读四块各 64 MiB 的私有缓冲

三种前台 各配一个 alone 加两种后台五种摆法 每种六轮 一共 198 组 每组先热 0.7 秒再测 2.5 秒 轮次顺序用固定种子随机 再倒着来一遍 顺序开跑前就存好

主对照 e_spread 对 unrestricted 和 e_spread 对 p_separate 按前台 后台 轮次配对 看前台每秒完成步数 p99 单批耗时 后台吞吐

达标线 前台配对中位数至少 +3% 六轮至少五轮为正 后台保住 95% 以上 p99 比值不超过 1.05

跑完核对 198 组各跑一次 校验和 指针环 亲和回读和还原 没有弹窗 源码和二进制哈希没变

参考 [Intel Atom 预取控制](https://cdrdv2-public.intel.com/795247/357930-Hardware-Prefetch-Controls-for-Intel-Atom-Cores.pdf) [Windows 处理器关系](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-processor_relationship)
