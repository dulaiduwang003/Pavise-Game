using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
    internal sealed class CriticalThreadRuntimeTelemetry
    {
        private struct Entry
        {
            internal string Stage, Result;
            internal int Pid;
            internal long LocalTicks, ElapsedTicks;
            internal bool ForwardAttempt;
        }

        private readonly object gate = new object();
        private readonly Entry[] recent = new Entry[12];
        private int next, count;
        private long samples, audits, forwardBatches, maxTicks;

        internal void Record(string stage, string result, int pid, long started, bool forwardAttempt)
        {
            long elapsed = Math.Max(0, Stopwatch.GetTimestamp() - started);
            lock (gate)
            {
                recent[next] = new Entry { Stage = stage, Result = result, Pid = pid,
                    LocalTicks = DateTime.Now.Ticks, ElapsedTicks = elapsed, ForwardAttempt = forwardAttempt };
                next = (next + 1) % recent.Length;
                count = Math.Min(count + 1, recent.Length);
                if (stage == "sample") samples++;
                if (stage == "candidate-audit") audits++;
                if (forwardAttempt) forwardBatches++;
                maxTicks = Math.Max(maxTicks, elapsed);
            }
        }

        internal string Describe()
        {
            lock (gate)
            {
                var stringBuilder = new StringBuilder("samples=" + samples + " candidateAudits=" + audits
                    + " forwardBatches=" + forwardBatches + " maxMs=" + Milliseconds(maxTicks));
                for (int index = 0; index < count; index++)
                {
                    Entry item = recent[(next - count + index + recent.Length) % recent.Length];
                    stringBuilder.Append(" | ").Append(new DateTime(item.LocalTicks).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                        .Append(' ').Append(item.Stage).Append('/').Append(item.Result).Append(" pid=").Append(item.Pid)
                        .Append(' ').Append(Milliseconds(item.ElapsedTicks)).Append("ms");
                    if (item.ForwardAttempt) stringBuilder.Append(" forward");
                }
                return stringBuilder.ToString();
            }
        }

        private static string Milliseconds(long ticks)
        { return (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture); }
    }
}
