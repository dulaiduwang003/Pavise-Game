namespace PaviseApp
{
    // 兼容所有权机制引入前的记录；原生写入后、完成回执前崩溃时仍保留恢复意图。

    internal enum BoostPriorityPhase
    {
        Legacy,
        None,
        Intent,
        Owned
    }
}
