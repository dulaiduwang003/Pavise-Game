namespace PaviseApp
{
    internal sealed class AutoCorePlan
    {
        public ulong Exclusive;

        public int Cores;

        public int Logical;

        public ulong Allocated;

        public string Kind = "";

        public string Blocked;

        public bool Available
        {
            get
            {
                if (Blocked == null)
                {
                    return Exclusive != 0;
                }
                return false;
            }
        }
    }
}
