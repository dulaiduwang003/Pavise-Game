using System;
using System.Collections.Generic;

namespace PaviseApp
{
	// Forward priority writes and reservation admission share this gate. Restoration
	// never acquires it while holding a backend mutation gate.
	internal sealed class ThreadPriorityReservation : IDisposable
	{
		internal static readonly object Gate = new object();
		private static readonly HashSet<ThreadPriorityReservation> held = new HashSet<ThreadPriorityReservation>();
		private readonly int pid;
		private readonly long birth;
		private readonly Dictionary<int, long> threads = new Dictionary<int, long>();

		private ThreadPriorityReservation(int pid, long birth, HardThreadIdentity[] identities)
		{
			this.pid = pid;
			this.birth = birth;
			foreach (HardThreadIdentity identity in identities) threads.Add(identity.Tid, identity.Creation);
		}

		internal static ThreadPriorityReservation Create(int pid, long birth, HardThreadIdentity[] identities)
		{
			if (pid <= 0 || birth <= 0 || identities == null || identities.Length == 0)
				throw new ArgumentException("Hard thread priority reservation identity");
			lock (Gate)
			{
				ThreadPriorityReservation reservation = new ThreadPriorityReservation(pid, birth, identities);
				held.Add(reservation);
				return reservation;
			}
		}

		internal static bool Contains(int pid, long birth, int tid, long threadBirth)
		{
			lock (Gate)
			{
				foreach (ThreadPriorityReservation reservation in held)
				{
					long created;
					if (reservation.pid == pid && reservation.birth == birth && reservation.threads.TryGetValue(tid, out created) && created == threadBirth)
						return true;
				}
				return false;
			}
		}

		internal static bool HasProcess(int pid, long birth)
		{
			lock (Gate)
			{
				foreach (ThreadPriorityReservation reservation in held)
					if (reservation.pid == pid && reservation.birth == birth) return true;
				return false;
			}
		}

		public void Dispose()
		{
			lock (Gate) held.Remove(this);
		}
	}
}
