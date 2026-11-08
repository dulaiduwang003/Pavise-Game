using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PaviseApp
{
    internal sealed class SuccessLogBatch : IDisposable
    {
        private const int MaximumTargets = 32;
        private const int MaximumMessageBytes = 4000;
        private const int MaximumGroups = 8;
        [ThreadStatic] private static SuccessLogBatch current;
        private readonly SuccessLogBatch parent;
        private readonly string context;
        private readonly List<Group> groups = new List<Group>();
        private bool disposed;

        private SuccessLogBatch(string phase)
        {
            parent = current;
            if (parent != null) parent.Flush();
            context = phase;
            current = this;
        }

        internal static SuccessLogBatch Begin(string phase) { return new SuccessLogBatch(phase); }

        internal static void Record(string action, string name, int pid, long birth, string detail)
        {
            if (!Logger.WritesEnabled) return;
            string target = name + "(pid=" + pid.ToString(CultureInfo.InvariantCulture)
                + ",birth=" + birth.ToString(CultureInfo.InvariantCulture) + ")";
            if (current == null)
            {
                using (var batch = Begin("single")) batch.Add(action, target, detail);
            }
            else current.Add(action, target, detail);
        }

        internal static void FlushPending() { if (current != null) current.Flush(); }

        private sealed class Group
        {
            internal readonly string Action, Detail;
            private readonly string context;
            private readonly List<string> targets = new List<string>();
            private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            private DateTime first, last;
            internal Group(string context, string action, string detail)
            { this.context = context; Action = action; Detail = detail; }

            private string Header(int count)
            {
                return "SUPPRESSION success phase=" + context + " action=" + Action
                    + " count=" + count.ToString(CultureInfo.InvariantCulture)
                    + " time=" + first.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + ".." + last.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + (string.IsNullOrEmpty(Detail) ? "" : " detail=" + Detail) + " targets=";
            }

            internal void Add(string target)
            {
                if (targets.Count == 0) first = DateTime.Now;
                last = DateTime.Now;
                if (seen.Contains(target)) return;
                string prospective = Header(targets.Count + 1) + string.Join(";", targets.ToArray())
                    + (targets.Count == 0 ? "" : ";") + target;
                if (targets.Count >= MaximumTargets || Encoding.UTF8.GetByteCount(prospective) > MaximumMessageBytes)
                { Flush(); first = last = DateTime.Now; }
                targets.Add(target); seen.Add(target);
                if (targets.Count >= MaximumTargets) Flush();
            }

            internal void Flush()
            {
                if (targets.Count == 0) return;
                string message = Header(targets.Count) + string.Join(";", targets.ToArray());
                targets.Clear(); seen.Clear();
                Write(message);
            }
        }

        private void Add(string action, string target, string detail)
        {
            Group group = groups.Find(item => item.Action == action && item.Detail == detail);
            if (group == null)
            {
                if (groups.Count == MaximumGroups) { groups[0].Flush(); groups.RemoveAt(0); }
                group = new Group(context, action, detail); groups.Add(group);
            }
            group.Add(target);
        }

        private void Flush() { foreach (Group group in groups) group.Flush(); }

        private static void Write(string message)
        {
            // Bound log records without dropping target details or splitting UTF-16 surrogate pairs.
            if (Encoding.UTF8.GetByteCount(message) <= MaximumMessageBytes) { Logger.Info(message); return; }
            for (int offset = 0, part = 1; offset < message.Length; part++)
            {
                int end = offset, bytes = 0;
                while (end < message.Length)
                {
                    char value = message[end];
                    bool pair = char.IsHighSurrogate(value) && end + 1 < message.Length && char.IsLowSurrogate(message[end + 1]);
                    int size = pair ? 4 : value < 128 ? 1 : value < 2048 ? 2 : 3;
                    if (bytes + size > MaximumMessageBytes - 80) break;
                    bytes += size; end += pair ? 2 : 1;
                }
                Logger.Info("SUPPRESSION success continuation part=" + part.ToString(CultureInfo.InvariantCulture)
                    + " data=" + message.Substring(offset, end - offset));
                offset = end;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Flush(); }
            finally { if (ReferenceEquals(current, this)) current = parent; }
        }
    }
}
