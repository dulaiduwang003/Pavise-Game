using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    // 每个守护在 EOF 前始终只有一个输出读取者；回复超时只撤销命令权限，不另建读取者。
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

        // 守护退出后才释放读取者；撤销命令权限不释放恢复句柄。
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
