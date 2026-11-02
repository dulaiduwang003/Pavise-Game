using System;
using System.Runtime.ExceptionServices;

namespace PaviseApp
{
	internal static class HardThreadConstructionCleanup
	{
		internal static void CloseOwned(IDisposable original)
		{
			try
			{
				original.Dispose();
			}
			catch (Exception inner)
			{
				throw new HardThreadClosureFailure("Original temporary source closure is unconfirmed", inner);
			}
		}

		internal static void CloseFailed(IDisposable original, Exception cause)
		{
			try
			{
				original.Dispose();
			}
			catch (Exception inner)
			{
				throw new AggregateException("Original pin construction failed with unconfirmed cleanup", cause, new HardThreadClosureFailure("Unassigned original pin closure is unconfirmed", inner));
			}
		}

		internal static void AcquireAndRelease(Action acquire, Action release)
		{
			Exception ex = null;
			try
			{
				acquire();
			}
			catch (Exception ex2)
			{
				ex = ex2;
			}
			try
			{
				release();
			}
			catch (Exception ex3)
			{
				if (ex != null)
				{
					throw new AggregateException("Original acquisition and cleanup both failed", ex, ex3);
				}
				throw;
			}
			if (ex != null)
			{
				ExceptionDispatchInfo.Capture(ex).Throw();
			}
		}
	}
}
