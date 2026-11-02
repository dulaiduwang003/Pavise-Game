using System;
using System.IO;

namespace PaviseApp
{
	internal sealed class HardThreadClosureFailure : IOException
	{
		internal HardThreadClosureFailure(string message)
			: base(message)
		{
		}

		internal HardThreadClosureFailure(string message, Exception inner)
			: base(message, inner)
		{
		}

		internal static bool Contains(Exception error)
		{
			if (error == null)
			{
				return false;
			}
			if (error is HardThreadClosureFailure)
			{
				return true;
			}
			AggregateException ex = error as AggregateException;
			if (ex != null)
			{
				foreach (Exception innerException in ex.InnerExceptions)
				{
					if (Contains(innerException))
					{
						return true;
					}
				}
				return false;
			}
			return Contains(error.InnerException);
		}
	}
}
