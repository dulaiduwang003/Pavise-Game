namespace PaviseApp
{
    // ACE 压制使用空闲 CPU 优先级 极低 I/O 优先级 内存页优先级 1 和 EcoQoS
    // 对局中使用最后一个可用逻辑处理器
    internal static class AntiCheatModes
    {
        internal const SuppressionLevel Level = SuppressionLevel.Isolated;

        internal const int CoreLimit = 1;
    }
}
