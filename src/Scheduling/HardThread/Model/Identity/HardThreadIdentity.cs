using System;

namespace PaviseApp
{
    internal sealed class HardThreadIdentity
    {
        internal readonly int Tid;

        internal readonly long Creation;

        internal string Key
        {
            get
            {
                return Tid + ":" + Creation;
            }
        }

        internal HardThreadIdentity(int tid, long creation)
        {
            if (tid <= 0 || creation <= 0)
            {
                throw new ArgumentException("thread creation identity");
            }
            Tid = tid;
            Creation = creation;
        }
    }
}
