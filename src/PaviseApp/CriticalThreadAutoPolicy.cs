using System.Linq;

namespace PaviseApp
{
	internal static class CriticalThreadAutoPolicy
	{
		internal const int MaximumCandidates = 2;

		internal const int MinimumWindows = 3;

		internal const double MinimumShare = 0.35;

		internal const string Presenter = "presentation-associated";

		internal static HardThreadIdentity[] SelectPresenter(CriticalThreadHotspot[] hotspots, CriticalThreadRoleFact[] roles)
		{
			if (hotspots == null || roles == null)
			{
				return new HardThreadIdentity[0];
			}
			CriticalThreadRoleFact[] facts = roles.Where((CriticalThreadRoleFact x) => x != null && x.Role == "presentation-associated").ToArray();
			if (facts.Length != 1)
			{
				return new HardThreadIdentity[0];
			}
			CriticalThreadHotspot criticalThreadHotspot = hotspots.FirstOrDefault((CriticalThreadHotspot x) => x != null && x.Tid == facts[0].Tid && x.Creation == facts[0].Creation && x.Tid > 0 && x.Creation > 0 && x.Windows >= 3 && x.CpuShare >= 0.35);
			if (criticalThreadHotspot != null)
			{
				return new HardThreadIdentity[1]
				{
					new HardThreadIdentity(criticalThreadHotspot.Tid, criticalThreadHotspot.Creation)
				};
			}
			return new HardThreadIdentity[0];
		}

		internal static ulong[] Targets(int count, ulong[] physical, ulong allowed, ulong reserved, ulong favored, ulong performance, ulong[] domains, ulong preferredDomain, ulong crowded)
		{
			if (count < 1 || count > 2 || physical == null || allowed == 0)
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
