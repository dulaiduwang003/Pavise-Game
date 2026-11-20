using System.Linq;

namespace PaviseApp
{
    // 布局只决定候选及落点 硬绑定强度由会话按规则执行 容量始终为其余线程保留物理核

    internal static class CriticalThreadAutoPolicy
    {
        internal const int MaximumIsolated = 4;

        internal const double MinimumShare = 0.30;

        internal static ulong[] Targets(int count, ulong[] physical, ulong allowed, ulong reserved, ulong favored, ulong performance, ulong[] domains, ulong preferredDomain, ulong crowded)
        {
            if (count < 1 || count > MaximumIsolated || physical == null || allowed == 0)
            {
                return null;
            }
            // 选中一个逻辑处理器即可 预留仍占用其整个物理核
            ulong[] whereItems = physical.Where((ulong x) => x != 0 && (x & allowed) != 0 && (x & reserved) == 0).ToArray();
            if (whereItems.Length - count < 2)
            {
                return null;
            }
            ulong[] numericValueItems = whereItems;
            if (performance != 0 && numericValueItems.Count((ulong x) => (x & performance) == x) >= count)
            {
                numericValueItems = numericValueItems.Where((ulong x) => (x & performance) == x).ToArray();
            }
            if (numericValueItems.Count((ulong x) => (x & 1) == 0) >= count)
            {
                numericValueItems = numericValueItems.Where((ulong x) => (x & 1) == 0).ToArray();
            }
            if (crowded != 0 && numericValueItems.Count((ulong x) => (x & crowded) == 0) >= count)
            {
                numericValueItems = numericValueItems.Where((ulong x) => (x & crowded) == 0).ToArray();
            }
            ulong domain = Domain(numericValueItems, count, favored, domains, preferredDomain);
            if (domain != 0)
            {
                numericValueItems = numericValueItems.Where((ulong x) => (x & domain) == x).ToArray();
            }
            ulong[] items = (from x in numericValueItems
                orderby (x & favored) != 0 descending, x descending
                select x).ToArray();
            if (items.Length < count)
            {
                return null;
            }
            return (from x in items.Take(count).Select((ulong x) => x & allowed)
                select x & (~x + 1)).ToArray();
        }

        private static ulong Domain(ulong[] pool, int count, ulong favored, ulong[] domains, ulong preferred)
        {
            if (preferred != 0 && pool.Count((ulong x) => (x & preferred) == x) >= count)
            {
                return preferred;
            }
            if (domains == null || domains.Length < 2)
            {
                return 0uL;
            }
            ulong result = 0uL;
            int domainValue = -1;
            int domainValue2 = -1;
            foreach (ulong domain in domains)
            {
                if (domain == 0)
                {
                    continue;
                }
                ulong[] whereItems = pool.Where((ulong x) => (x & domain) == x).ToArray();
                if (whereItems.Length >= count)
                {
                    int countResult = whereItems.Count((ulong x) => (x & favored) != 0);
                    if (countResult > domainValue || (countResult == domainValue && whereItems.Length > domainValue2))
                    {
                        result = domain;
                        domainValue = countResult;
                        domainValue2 = whereItems.Length;
                    }
                }
            }
            return result;
        }
    }
}
