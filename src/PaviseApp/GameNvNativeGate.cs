using System;
using System.Security.Principal;
using System.Threading;

namespace PaviseApp
{
	internal static class GameNvNativeGate
	{
		private sealed class LocalTestHeld : IDisposable
		{
			public void Dispose()
			{
				Monitor.Exit(SyncRoot);
			}
		}

		private sealed class Held : IDisposable
		{
			private Mutex mutex;

			private readonly int threadId = Thread.CurrentThread.ManagedThreadId;

			internal Held(Mutex value)
			{
				mutex = value;
			}

			public void Dispose()
			{
				if (this.mutex == null)
				{
					return;
				}
				if (Thread.CurrentThread.ManagedThreadId != threadId)
				{
					throw new InvalidOperationException("A DRS transaction must close on its original thread");
				}
				Mutex mutex = this.mutex;
				this.mutex = null;
				try
				{
					mutex.ReleaseMutex();
				}
				finally
				{
					try
					{
						mutex.Dispose();
					}
					finally
					{
						Monitor.Exit(SyncRoot);
					}
				}
			}
		}

		internal static readonly object SyncRoot = new object();

		internal static IDisposable Enter()
		{
			if (!Monitor.TryEnter(SyncRoot, 3000))
			{
				throw new TimeoutException("A Pavise DRS transaction is still active");
			}
			Mutex mutex = null;
			bool enterReady = false;
			try
			{
				SecurityIdentifier user;
				using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
				{
					user = windowsIdentity.User;
				}
				if (user == null)
				{
					throw new UnauthorizedAccessException("DRS ownership requires the current user SID");
				}
				bool createdNew;
				mutex = new Mutex(false, "Global\\Pavise.DRS." + user.Value + ".v1", out createdNew, UserMutexSecurity.Create(user));
				UserMutexSecurity.Verify(mutex.GetAccessControl(), user);
				try
				{
					enterReady = mutex.WaitOne(3000);
				}
				catch (AbandonedMutexException)
				{
					enterReady = true;
				}
				if (!enterReady)
				{
					throw new TimeoutException("A Pavise DRS transaction is active in another session");
				}
				UserMutexSecurity.Verify(mutex.GetAccessControl(), user);
				return new Held(mutex);
			}
			catch
			{
				try
				{
					if (enterReady)
					{
						mutex.ReleaseMutex();
					}
				}
				finally
				{
					try
					{
						if (mutex != null)
						{
							mutex.Dispose();
						}
					}
					finally
					{
						Monitor.Exit(SyncRoot);
					}
				}
				throw;
			}
		}
	}
}
