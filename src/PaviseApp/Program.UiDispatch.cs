using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class Program
    {

        internal sealed class RuntimeUiDispatcher : IDisposable
        {
            private readonly Control control = new Control();

            private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;

            private volatile bool closed;

            internal bool IsOwnerThread
            {
                get
                {
                    return Thread.CurrentThread.ManagedThreadId == ownerThread;
                }
            }

            internal RuntimeUiDispatcher()
            {
                GC.KeepAlive(control.Handle);
            }

            internal bool TrySend(Action action, int timeoutMs = 5000)
            {
                if (closed)
                {
                    return false;
                }
                if (IsOwnerThread)
                {
                    action();
                    return true;
                }
                object pending = new object();
                bool done = false;
                bool expired = false;
                Exception error = null;
                if (!Post(delegate
                {
                    lock (pending)
                    {
                        if (expired)
                        {
                            return;
                        }
                    }
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    finally
                    {
                        lock (pending)
                        {
                            done = true;
                            Monitor.PulseAll(pending);
                        }
                    }
                }))
                {
                    return false;
                }
                Stopwatch stopwatch = Stopwatch.StartNew();
                lock (pending)
                {
                    while (!done)
                    {
                        int lowerBound = (int)Math.Max(0L, timeoutMs - stopwatch.ElapsedMilliseconds);
                        if (lowerBound == 0)
                        {
                            expired = true;
                            return false;
                        }
                        Monitor.Wait(pending, lowerBound);
                    }
                }
                if (error != null)
                {
                    throw new InvalidOperationException("UI shutdown dispatch failed", error);
                }
                return true;
            }

            internal bool Post(Action action)
            {
                try
                {
                    if (closed || control.IsDisposed || !control.IsHandleCreated)
                    {
                        return false;
                    }
                    control.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!closed && !control.IsDisposed)
                        {
                            action();
                        }
                    });
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            public void Dispose()
            {
                closed = true;
                control.Dispose();
            }
        }

        internal static bool StartShutdownWorker(Action work, Action<Exception> failed)
        {
            Action<Exception> report = delegate(Exception error)
            {
                try
                {
                    if (failed != null)
                    {
                        failed(error);
                    }
                }
                catch
                {
                }
            };
            try
            {
                Thread thread = new Thread((ThreadStart)delegate
                {
                    try
                    {
                        work();
                    }
                    catch (Exception error2)
                    {
                        report(error2);
                    }
                });
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return true;
            }
            catch (Exception error2)
            {
                report(error2);
                return false;
            }
        }

    }
}
