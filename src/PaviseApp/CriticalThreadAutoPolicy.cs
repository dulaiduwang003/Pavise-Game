using System.Collections.Generic;
using System.Linq;

namespace PaviseApp
{
    // 布局只决定候选及落点；硬绑定强度由会话按规则执行，容量始终为其余线程保留物理核。

    internal static class CriticalThreadAutoPolicy
    {
        internal const int MaximumIsolated = 4;

        internal const int MinimumWindows = 3;

        // 缺少画面提交证据时，负载必须持续达到 30 秒。
        internal const int EvidenceFreeWindows = 6;

        internal const double MinimumShare = 0.30;

        internal const double SaturatedShare = 0.70;

        internal const double PoolBand = 0.10;

        internal const int PoolSize = 3;

        internal const string Presenter = "presentation-associated";

        internal static HardThreadIdentity[] SelectPresenter(CriticalThreadHotspot[] hotspots, CriticalThreadRoleFact[] roles)
        {
            if (hotspots == null || roles == null)
            {
                return new HardThreadIdentity[0];
            }
            CriticalThreadRoleFact[] facts = roles.Where((CriticalThreadRoleFact x) => x != null && x.Role == Presenter).ToArray();
            if (facts.Length != 1)
            {
                return new HardThreadIdentity[0];
            }
            CriticalThreadHotspot criticalThreadHotspot = hotspots.FirstOrDefault((CriticalThreadHotspot x) => x != null && x.Tid == facts[0].Tid && x.Creation == facts[0].Creation && x.Tid > 0 && x.Creation > 0 && x.Windows >= MinimumWindows && x.CpuShare >= MinimumShare);
            if (criticalThreadHotspot != null)
            {
                return new HardThreadIdentity[1]
                {
                    new HardThreadIdentity(criticalThreadHotspot.Tid, criticalThreadHotspot.Creation)
                };
            }
            return new HardThreadIdentity[0];
        }

        // 优先使用画面提交证据，否则要求持续且能与工作线程池区分的负载。共用线程入口无法区分渲染线程和线程池工作线程。

        internal static HardThreadIdentity SelectPrimary(CriticalThreadHotspot[] hotspots, CriticalThreadRoleFact[] roles, out bool pooled)
        {
            return SelectPrimary(hotspots, roles, CriticalThreadLayout.Compatible, out pooled);
        }

        internal static HardThreadIdentity SelectPrimary(CriticalThreadHotspot[] hotspots, CriticalThreadRoleFact[] roles, CriticalThreadLayout layout, out bool pooled)
        {
            pooled = false;
            CriticalThreadHotspot[] busy = Eligible(hotspots, MinimumShare);
            if (busy.Length == 0)
            {
                return null;
            }
            CriticalThreadRoleFact[] presenters = (roles ?? new CriticalThreadRoleFact[0]).Where((CriticalThreadRoleFact x) => x != null && x.Role == Presenter).ToArray();
            if (presenters.Length == 1)
            {
                CriticalThreadHotspot presenter = busy.FirstOrDefault((CriticalThreadHotspot x) => x.Tid == presenters[0].Tid && x.Creation == presenters[0].Creation);
                if (presenter != null)
                {
                    return Identity(presenter);
                }
            }
            CriticalThreadHotspot top = busy
                // 未与当前线程身份匹配的呈现证据，不能缩短这个线程自己的观察期。
                .Where((CriticalThreadHotspot x) => x.Windows >= EvidenceFreeWindows)
                .OrderByDescending((CriticalThreadHotspot x) => x.CpuShare)
                .ThenBy((CriticalThreadHotspot x) => x.Tid)
                .FirstOrDefault();
            if (top == null)
            {
                return null;
            }
            if (layout == CriticalThreadLayout.Compatible
                && busy.Count((CriticalThreadHotspot x) => System.Math.Abs(x.CpuShare - top.CpuShare) <= PoolBand) >= PoolSize)
            {
                pooled = true;
                return null;
            }
            return Identity(top);
        }

        // 主线程由唯一的画面提交证据指认时，一次选择即可提交；否则需要连续两次一致。
        internal static int RequiredStableSelections(HardThreadIdentity primary, CriticalThreadRoleFact[] roles)
        {
            if (primary == null || roles == null)
            {
                return 2;
            }
            CriticalThreadRoleFact[] presenters = roles.Where((CriticalThreadRoleFact x) => x != null && x.Role == Presenter).ToArray();
            return presenters.Length == 1 && presenters[0].Tid == primary.Tid && presenters[0].Creation == primary.Creation ? 1 : 2;
        }

        internal static HardThreadIdentity[] SelectSecondary(CriticalThreadHotspot[] hotspots, HardThreadIdentity primary, int count)
        {
            if (primary == null || count < 1)
            {
                return new HardThreadIdentity[0];
            }
            return Eligible(hotspots, SaturatedShare)
                .Where((CriticalThreadHotspot x) => x.Tid != primary.Tid || x.Creation != primary.Creation)
                .OrderByDescending((CriticalThreadHotspot x) => x.CpuShare)
                .ThenBy((CriticalThreadHotspot x) => x.Tid)
                .Take(count)
                .Select(Identity)
                .ToArray();
        }

        private static CriticalThreadHotspot[] Eligible(CriticalThreadHotspot[] hotspots, double share)
        {
            if (hotspots == null)
            {
                return new CriticalThreadHotspot[0];
            }
            // 同一采样中出现两次的线程身份存在歧义，不参与选择。
            return hotspots
                .Where((CriticalThreadHotspot x) => x != null && x.Tid > 0 && x.Creation > 0 && x.Windows >= MinimumWindows && x.CpuShare >= share)
                .GroupBy((CriticalThreadHotspot x) => x.Tid)
                .Where((IGrouping<int, CriticalThreadHotspot> g) => g.Count() == 1)
                .Select((IGrouping<int, CriticalThreadHotspot> g) => g.First())
                .ToArray();
        }

        private static HardThreadIdentity Identity(CriticalThreadHotspot hotspot)
        {
            return new HardThreadIdentity(hotspot.Tid, hotspot.Creation);
        }

        // 保留两个物理核；混合架构只把关键工作放到性能核。
        internal static int Capacity(ulong[] physical, ulong allowed, ulong reserved, ulong performance)
        {
            if (physical == null || allowed == 0)
            {
                return 0;
            }
            ulong[] pool = physical.Where((ulong x) => x != 0 && (x & allowed) != 0 && (x & reserved) == 0).ToArray();
            if (pool.Length < 3)
            {
                return 0;
            }
            int limit;
            int fast = (performance == 0) ? 0 : pool.Count((ulong x) => (x & performance) == x);
            if (fast > 0 && fast < pool.Length)
            {
                limit = (fast >= 2) ? (fast / 2) : 1;
            }
            else
            {
                limit = System.Math.Max(1, pool.Length / 2 - 1);
            }
            limit = System.Math.Min(limit, pool.Length - 2);
            return System.Math.Max(0, System.Math.Min(MaximumIsolated, limit));
        }

        internal static ulong[] Targets(int count, ulong[] physical, ulong allowed, ulong reserved, ulong favored, ulong performance, ulong[] domains, ulong preferredDomain, ulong crowded)
        {
            if (count < 1 || count > MaximumIsolated || physical == null || allowed == 0)
            {
                return null;
            }
            // 选中一个逻辑处理器即可，预留仍占用其整个物理核。
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

        internal static ulong[] Destinations(CriticalThreadRule preference, int count, ulong[] physical, ulong allowed, ulong reserved, ulong favored, ulong performance, ulong[] domains, ulong preferredDomain, ulong crowded)
        {
            if (preference == null || !preference.Automatic) return null;
            if (preference.Layout != CriticalThreadLayout.Compatible)
                return LocalDestinations(preference, count, physical, allowed, reserved, favored, performance, domains, preferredDomain, crowded);
            if (!preference.ManualTarget)
                return Targets(count, physical, allowed, reserved, favored, performance, domains, preferredDomain, crowded);
            if (count < 1 || count > MaximumIsolated || physical == null) return null;
            ulong target = preference.Targets[0];
            ulong whole = physical.FirstOrDefault(x => (x & target) != 0);
            if (whole == 0 || (target & allowed) != target || (whole & reserved) != 0
                || physical.Count(x => (x & allowed) != 0 && (x & (whole | reserved)) == 0) < 2) return null;
            if (count == 1) return new ulong[] { target };
            ulong[] secondary = Targets(count - 1, physical, allowed, reserved | whole, favored, performance, domains, preferredDomain, crowded);
            return secondary == null ? null : new ulong[] { target }.Concat(secondary).ToArray();
        }

        private static ulong[] LocalDestinations(CriticalThreadRule preference, int count, ulong[] physical, ulong allowed, ulong reserved, ulong favored, ulong performance, ulong[] domains, ulong preferredDomain, ulong crowded)
        {
            if (count < 1 || count > MaximumIsolated || physical == null || allowed == 0) return null;
            int capacity = Capacity(physical, allowed, reserved, performance);
            if (capacity == 0) return null;
            count = System.Math.Min(count, capacity);
            ulong primary;
            if (preference.ManualTarget) primary = preference.Targets[0];
            else
            {
                ulong[] selected = Targets(1, physical, allowed, reserved, favored, performance, domains, preferredDomain, crowded);
                if (selected == null) return null;
                primary = selected[0];
            }
            ulong whole = physical.FirstOrDefault((ulong core) => (core & primary) != 0);
            if (whole == 0 || (primary & allowed) != primary || (whole & reserved) != 0
                || physical.Count((ulong core) => (core & allowed) != 0 && (core & (whole | reserved)) == 0) < 2) return null;
            if (count == 1) return new ulong[] { primary };

            // 缓存域不明或互相重叠时仅保留主候选，不能把未知拓扑当成同域。
            ulong[] knownDomains = (domains ?? new ulong[0]).Where((ulong domain) => domain != 0).Distinct().ToArray();
            ulong seen = 0, allPhysical = 0;
            foreach (ulong core in physical) allPhysical |= core;
            foreach (ulong domain in knownDomains)
            {
                if ((seen & domain) != 0 || (domain & ~allPhysical) != 0) return new ulong[] { primary };
                seen |= domain;
            }
            ulong[] matchingDomains = knownDomains.Where((ulong domain) => (whole & domain) == whole).ToArray();
            if (matchingDomains.Length != 1) return new ulong[] { primary };
            ulong anchor = matchingDomains[0];
            ulong[] pool = physical.Where((ulong core) => core != 0 && (core & allowed) != 0
                && (core & (whole | reserved)) == 0 && (core & anchor) == core
                && (performance == 0 || (core & performance) == core)).ToArray();
            ulong[] secondary = pool
                .OrderByDescending((ulong core) => (core & 1UL) == 0)
                .ThenByDescending((ulong core) => crowded == 0 || (core & crowded) == 0)
                .ThenByDescending((ulong core) => (core & favored) != 0)
                .ThenByDescending((ulong core) => core)
                .Take(count - 1)
                .Select((ulong core) => core & allowed)
                .Select((ulong core) => core & (~core + 1)).ToArray();
            return new ulong[] { primary }.Concat(secondary).ToArray();
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
