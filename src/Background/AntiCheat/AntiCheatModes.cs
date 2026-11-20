namespace PaviseApp
{
    // 反作弊压制只有一档 CPU 空闲 磁盘 I/O 极低 内存页优先级 1 EcoQoS 并忽略计时器精度请求 禁用动态提优
    // 每秒核验一次 对局中最多共享两个完整物理核 只有一个可用时用一个 不读也不保存任何档位设置
    internal static class AntiCheatModes
    {
        internal const SuppressionLevel Level = SuppressionLevel.Isolated;

        internal const int CoreLimit = 2;
    }
}
