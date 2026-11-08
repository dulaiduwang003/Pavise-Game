using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal sealed class HardThreadResourceClosure : IDisposable
	{
		private readonly IDisposable[] original;

		private readonly bool[] closed;

		internal bool ResourcesClosed
		{
			get
			{
				bool[] closedSnapshot = closed;
				for (int i = 0; i < closedSnapshot.Length; i++)
				{
					if (!closedSnapshot[i])
					{
						return false;
					}
				}
				return true;
			}
		}

		internal HardThreadResourceClosure(params IDisposable[] resources)
		{
			if (resources == null)
			{
				throw new ArgumentNullException("resources");
			}
			original = (IDisposable[])resources.Clone();
			closed = new bool[original.Length];
		}

		public void Dispose()
		{
			List<Exception> list = new List<Exception>();
			for (int i = 0; i < original.Length; i++)
			{
				if (closed[i])
				{
					continue;
				}
				try
				{
					if (original[i] != null)
					{
						original[i].Dispose();
					}
					closed[i] = true;
				}
				catch (Exception item)
				{
					list.Add(item);
				}
			}
			if (list.Count != 0)
			{
				throw new AggregateException("Original guardian resources remain unconfirmed", list);
			}
		}
	}
}
