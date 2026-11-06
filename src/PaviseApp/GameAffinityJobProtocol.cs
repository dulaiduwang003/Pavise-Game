using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    // One output reader belongs to one guardian process for its entire lifetime.
    // Losing a reply deadline revokes this channel; the reader still drains late
    // output through EOF, without leaving a blocking ReadLine task per command.
    internal sealed class GameAffinityJobProtocol : IDisposable
    {
        private readonly object gate = new object();
        private readonly Process process;
        private readonly Queue<string> replies = new Queue<string>();
        private bool ended, lost, disposed;

        internal GameAffinityJobProtocol(Process process)
        {
            this.process = process;
            process.OutputDataReceived += Received;
            try { process.BeginOutputReadLine(); }
            catch { process.OutputDataReceived -= Received; throw; }
        }

        private void Received(object sender, DataReceivedEventArgs args)
        {
            lock (gate)
            {
                if (args.Data == null) ended = true;
                else if (!lost && !disposed)
                {
                    if (args.Data.Length > 4096 || replies.Count >= 8)
                    { lost = true; replies.Clear(); }
                    else replies.Enqueue(args.Data);
                }
                Monitor.PulseAll(gate);
            }
        }

        internal bool Ended { get { lock (gate) return ended; } }

        internal string ReadReply(int timeoutMilliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            lock (gate)
            {
                while (!disposed && !lost)
                {
                    if (replies.Count > 0) return replies.Dequeue();
                    if (ended) return null;
                    int remaining = (int)Math.Max(0L, timeoutMilliseconds - elapsed.ElapsedMilliseconds);
                    if (remaining == 0)
                    {
                        lost = true;
                        throw new TimeoutException("game-affinity-guardian-reply");
                    }
                    Monitor.Wait(gate, remaining);
                }
                throw new InvalidOperationException("game-affinity-guardian-protocol-lost");
            }
        }

        internal void Revoke()
        {
            lock (gate) { lost = true; replies.Clear(); Monitor.PulseAll(gate); }
        }

        // Only the lease owner disposes this reader, after guardian exit has been
        // verified. Revoking commands alone must not dispose recovery handles.
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; replies.Clear(); Monitor.PulseAll(gate);
            }
            process.OutputDataReceived -= Received;
            try { process.CancelOutputRead(); } catch { }
        }
    }
}
