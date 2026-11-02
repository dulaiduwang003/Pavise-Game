using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class FreezeLeaseSet : IDisposable
	{
		private sealed class Entry
		{
			internal FreezeTarget Target;

			internal IDisposable Lease;
		}

		private readonly Dictionary<int, Entry> active = new Dictionary<int, Entry>();

		private readonly Dictionary<int, FreezeTarget> refused = new Dictionary<int, FreezeTarget>();

		internal Action<string> Trace;

		internal int Count
		{
			get
			{
				return active.Count;
			}
		}

		internal int[] Pids
		{
			get
			{
				int[] array = new int[active.Count];
				active.Keys.CopyTo(array, 0);
				return array;
			}
		}

		private void Note(string message)
		{
			try
			{
				if (Trace != null)
				{
					Trace(message);
				}
			}
			catch
			{
			}
		}

		internal void Release(int pid, string reason = "explicit-release")
		{
			Entry value;
			if (active.TryGetValue(pid, out value))
			{
				value.Lease.Dispose();
				active.Remove(pid);
				Note("released " + FreezeTrace.Identity(value.Target) + " reason=" + reason);
			}
		}

		internal void Release(int pid, long creation)
		{
			Entry value;
			if (active.TryGetValue(pid, out value) && value.Target.Creation == creation)
			{
				Release(pid);
			}
		}

		internal void Sync(Dictionary<int, FreezeTarget> wanted, Func<bool> allowed, Func<FreezeTarget, IDisposable> acquire)
		{
			try
			{
				if (!allowed())
				{
					Dispose();
					return;
				}
				int[] pids = Pids;
				foreach (int num in pids)
				{
					FreezeTarget value;
					if (!wanted.TryGetValue(num, out value) || !active[num].Target.Same(value))
					{
						Release(num, "ineligible-or-identity-changed");
					}
				}
				foreach (int item in new List<int>(refused.Keys))
				{
					FreezeTarget value2;
					if (!wanted.TryGetValue(item, out value2) || !refused[item].Same(value2))
					{
						refused.Remove(item);
					}
				}
				foreach (KeyValuePair<int, FreezeTarget> item2 in wanted)
				{
					if (!allowed())
					{
						Dispose();
						break;
					}
					if (!active.ContainsKey(item2.Key) && !refused.ContainsKey(item2.Key))
					{
						IDisposable disposable = acquire(item2.Value);
						if (disposable == null)
						{
							refused[item2.Key] = item2.Value;
						}
						else
						{
							active.Add(item2.Key, new Entry
							{
								Target = item2.Value,
								Lease = disposable
							});
						}
						if (!allowed())
						{
							Dispose();
							break;
						}
					}
				}
			}
			catch
			{
				Dispose();
				throw;
			}
		}

		public void Dispose()
		{
			int[] pids = Pids;
			foreach (int pid in pids)
			{
				Release(pid, "session-gate-or-cleanup");
			}
			refused.Clear();
		}
	}
}
