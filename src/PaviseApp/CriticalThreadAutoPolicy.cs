using System.Collections.Generic;
using System.Linq;

namespace PaviseApp
{
	// Only the primary thread receives hard affinity; secondary threads use ideal-processor hints.
	// Capacity reserves physical cores for game workers and the system.
	internal static class CriticalThreadAutoPolicy
	{
		internal const int MaximumIsolated = 4;

		internal const int MinimumWindows = 3;

		// Without presentation evidence the load must stay sustained for 30 seconds.
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

		// Prefer presentation evidence; otherwise require sustained load distinct from a worker pool.
		// Shared thread entry points cannot distinguish render threads from pool workers.
		internal static HardThreadIdentity SelectPrimary(CriticalThreadHotspot[] hotspots, CriticalThreadRoleFact[] roles, out bool pooled)
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
				.Where((CriticalThreadHotspot x) => presenters.Length != 0 || x.Windows >= EvidenceFreeWindows)
				.OrderByDescending((CriticalThreadHotspot x) => x.CpuShare)
				.ThenBy((CriticalThreadHotspot x) => x.Tid)
				.FirstOrDefault();
			if (top == null)
			{
				return null;
			}
			if (busy.Count((CriticalThreadHotspot x) => System.Math.Abs(x.CpuShare - top.CpuShare) <= PoolBand) >= PoolSize)
			{
				pooled = true;
				return null;
			}
			return Identity(top);
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
			// A thread identity seen twice in one sample is ambiguous and never selected.
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

		// Reserve two physical cores; hybrid CPUs place critical work only on performance cores.
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
			// A selected logical processor is sufficient; reservations still exclude its entire physical core.
			ulong[] array = physical.Where((ulong x) => x != 0 && (x & allowed) != 0 && (x & reserved) == 0).ToArray();
			if (array.Length - count < 2)
			{
				return null;
			}
			ulong[] array2 = array;
			if (performance != 0 && array2.Count((ulong x) => (x & performance) == x) >= count)
			{
				array2 = array2.Where((ulong x) => (x & performance) == x).ToArray();
			}
			if (array2.Count((ulong x) => (x & 1) == 0) >= count)
			{
				array2 = array2.Where((ulong x) => (x & 1) == 0).ToArray();
			}
			if (crowded != 0 && array2.Count((ulong x) => (x & crowded) == 0) >= count)
			{
				array2 = array2.Where((ulong x) => (x & crowded) == 0).ToArray();
			}
			ulong domain = Domain(array2, count, favored, domains, preferredDomain);
			if (domain != 0)
			{
				array2 = array2.Where((ulong x) => (x & domain) == x).ToArray();
			}
			ulong[] array3 = (from x in array2
				orderby (x & favored) != 0 descending, x descending
				select x).ToArray();
			if (array3.Length < count)
			{
				return null;
			}
			return (from x in array3.Take(count).Select((ulong x) => x & allowed)
				select x & (~x + 1)).ToArray();
		}

		internal static ulong[] Destinations(CriticalThreadRule preference, int count, ulong[] physical, ulong allowed, ulong reserved, ulong favored, ulong performance, ulong[] domains, ulong preferredDomain, ulong crowded)
		{
			if (preference == null || !preference.Automatic) return null;
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
			int num = -1;
			int num2 = -1;
			foreach (ulong domain in domains)
			{
				if (domain == 0)
				{
					continue;
				}
				ulong[] array = pool.Where((ulong x) => (x & domain) == x).ToArray();
				if (array.Length >= count)
				{
					int num3 = array.Count((ulong x) => (x & favored) != 0);
					if (num3 > num || (num3 == num && array.Length > num2))
					{
						result = domain;
						num = num3;
						num2 = array.Length;
					}
				}
			}
			return result;
		}
	}
}
