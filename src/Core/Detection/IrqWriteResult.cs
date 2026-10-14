// 作者：bdth 2074055628@qq.com
namespace PaviseApp
{
    internal sealed class IrqWriteResult
    {
        internal int Succeeded, Failed, RolledBack, RollbackFailed, Unchanged, Attempted;
        internal string Failure = "";
        internal string Describe()
        {
            return Lang.F("irq.write.detail", Succeeded, Failed, RolledBack, RollbackFailed)
                + (Failure.Length == 0 ? "" : " · " + Failure);
        }
    }
}
