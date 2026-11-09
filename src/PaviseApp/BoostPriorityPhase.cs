namespace PaviseApp
{
    // 旧记录早于写入所有权机制。即使在原生写入与完成回执之间崩溃，恢复意图仍然有效。

    internal enum BoostPriorityPhase
    {
        Legacy,
        None,
        Intent,
        Owned
    }
}
