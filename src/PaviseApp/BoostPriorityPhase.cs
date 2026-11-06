namespace PaviseApp
{
    // Legacy receipts predate write ownership. Intent remains recoverable across
    // a crash between the native setter and its verified completion receipt.
    internal enum BoostPriorityPhase
    {
        Legacy,
        None,
        Intent,
        Owned
    }
}
