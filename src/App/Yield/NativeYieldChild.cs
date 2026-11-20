using System.Collections.Generic;

namespace PaviseApp
{
    internal static class NativeYieldChild
    {
        internal static bool Same(uint[] first, uint[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }
            HashSet<uint> hashSet = new HashSet<uint>(first);
            if (hashSet.Count != first.Length)
            {
                return false;
            }
            foreach (uint item in second)
            {
                if (!hashSet.Remove(item))
                {
                    return false;
                }
            }
            return hashSet.Count == 0;
        }
    }
}
