namespace PaviseApp
{
    internal sealed class IrqWriteResult
    {
        internal int Succeeded;

        internal int Failed;

        internal int RolledBack;

        internal int RollbackFailed;

        internal int Unchanged;

        internal int Attempted;

        internal string Failure = "";

        internal string Describe()
        {
            return Lang.F("irq.write.detail", Succeeded, Failed, RolledBack, RollbackFailed) + ((Failure.Length == 0) ? "" : (" · " + Failure));
        }
    }
}
