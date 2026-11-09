namespace PaviseApp
{
    internal sealed class CriticalThreadRoleFact
    {
        internal readonly int Tid;

        internal readonly long Creation;

        internal readonly string Role;

        internal CriticalThreadRoleFact(int tid, long creation, string role)
        {
            Tid = tid;
            Creation = creation;
            Role = role;
        }
    }
}
