using System;
using System.Threading;

namespace PaviseApp
{
	internal static class GameNvWorkerQueue
	{
		internal static bool TryQueue(Func<WaitCallback, bool> queue, Action work, Action complete)
		{
			if (queue == null || work == null || complete == null)
			{
				throw new ArgumentNullException("Worker admission callbacks");
			}
			int entry = 0;
			int completed = 0;
			Action finish = delegate
			{
				if (Interlocked.Exchange(ref completed, 1) == 0)
				{
					complete();
				}
			};
			WaitCallback arg = delegate
			{
				if (Interlocked.CompareExchange(ref entry, 1, 0) != 0)
				{
					return;
				}
				try
				{
					work();
				}
				finally
				{
					finish();
				}
			};
			try
			{
				if (queue(arg))
				{
					return true;
				}
			}
			catch
			{
				if (Interlocked.CompareExchange(ref entry, 2, 0) == 0)
				{
					finish();
				}
				throw;
			}
			if (Interlocked.CompareExchange(ref entry, 2, 0) == 0)
			{
				finish();
			}
			return false;
		}
	}
}
