using System;
using System.Threading;

namespace PaviseApp
{
	internal sealed class CacheWarmRunner
	{
		private readonly object gate = new object();

		private Thread worker;

		private ManualResetEvent cancel;

		private string attempted;

		private string status = "cachewarm.idle";

		internal string Status
		{
			get
			{
				lock (gate)
				{
					return status;
				}
			}
		}

		private void SetStatus(string value)
		{
			lock (gate)
			{
				status = value;
			}
		}

		private void StopAttempt(ManualResetEvent token)
		{
			lock (gate)
			{
				if (object.ReferenceEquals(cancel, token))
				{
					status = "cachewarm.stopped";
					attempted = null;
				}
			}
		}

		internal void Update(string key, string executable, string root, Func<bool> allowed)
		{
			lock (gate)
			{
				if (worker != null && worker.IsAlive)
				{
					if (!string.Equals(key, attempted, StringComparison.OrdinalIgnoreCase))
					{
						if (cancel != null)
						{
							cancel.Set();
						}
						attempted = null;
					}
				}
				else
				{
					if (string.Equals(key, attempted, StringComparison.OrdinalIgnoreCase))
					{
						return;
					}
					if (cancel != null)
					{
						cancel.Dispose();
					}
					attempted = key;
					cancel = new ManualResetEvent(false);
					ManualResetEvent token = cancel;
					status = "cachewarm.waiting";
					worker = new Thread((ThreadStart)delegate
					{
						bool ready = false;
						try
						{
							if (token.WaitOne(90000) || !allowed())
							{
								StopAttempt(token);
							}
							else
							{
								ready = CacheWarmPlatform.Background(true);
								if (!ready)
								{
									SetStatus("cachewarm.skipped");
								}
								else
								{
									string restrictFallbackResult = GameInstallScope.RestrictFallback(executable, root);
									CacheWarmEnvironment cacheWarmEnvironment = CacheWarmPlatform.Sample();
									if (restrictFallbackResult == null || !CacheWarmPlatform.SafePath(restrictFallbackResult) || !CacheWarmPlatform.SolidState(restrictFallbackResult) || !cacheWarmEnvironment.Ready || cacheWarmEnvironment.Budget < 1048576)
									{
										SetStatus("cachewarm.skipped");
									}
									else
									{
										Func<bool> func = () => !token.WaitOne(0) && allowed() && CacheWarmPlatform.Sample().Ready;
										SetStatus("cachewarm.running");
										long warmResult = CacheWarmEngine.Warm(restrictFallbackResult, cacheWarmEnvironment.Budget, func, token.WaitOne, delegate
										{
										});
										SetStatus(func() ? "cachewarm.done" : "cachewarm.stopped");
										Logger.Log(Lang.T("gm.cachewarm") + " · " + Lang.T(Status) + " · " + warmResult / 1048576 + " MiB");
									}
								}
							}
						}
						catch (Exception ex)
						{
							SetStatus("cachewarm.skipped");
							Logger.Log(Lang.T("gm.cachewarm") + " · " + ex.Message);
						}
						finally
						{
							if (ready)
							{
								CacheWarmPlatform.Background(false);
							}
						}
					})
					{
						IsBackground = true,
						Name = "Pavise cache warm-up"
					};
					worker.Start();
				}
			}
		}

		internal void Cancel(bool retry)
		{
			lock (gate)
			{
				if (cancel != null)
				{
					cancel.Set();
				}
				if (retry)
				{
					attempted = null;
				}
			}
		}

		internal bool Drain(int milliseconds)
		{
			Thread thread;
			lock (gate)
			{
				if (cancel != null)
				{
					cancel.Set();
				}
				thread = worker;
			}
			bool matchesJoin = thread == null || thread == Thread.CurrentThread || thread.Join(milliseconds);
			lock (gate)
			{
				if (matchesJoin && worker == thread)
				{
					worker = null;
					if (cancel != null)
					{
						cancel.Dispose();
						cancel = null;
					}
				}
			}
			return matchesJoin;
		}
	}
}
